#!/usr/bin/env python3
"""Pack, verify, and restore model data that is intentionally absent from Git."""

from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
import sys
import zipfile
from datetime import datetime, timezone
from pathlib import Path, PurePosixPath
from typing import Any


LOCK_SCHEMA = 1
BUNDLES = {
    "sdModelData": "sd-model-data-v1.zip",
    "teaLegacyData": "tea-legacy-data-v1.zip",
}
MODEL_SUFFIXES = {".png", ".bytes"}


class ModelDataError(RuntimeError):
    pass


def sha256_file(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def tracked_paths(root: Path) -> list[str]:
    result = subprocess.run(
        ["git", "-C", str(root), "ls-tree", "-r", "--name-only", "HEAD"],
        check=True,
        capture_output=True,
        text=True,
    )
    return [line for line in result.stdout.splitlines() if line]


def source_commit(root: Path) -> str:
    result = subprocess.run(
        ["git", "-C", str(root), "rev-parse", "HEAD"],
        check=True,
        capture_output=True,
        text=True,
    )
    return result.stdout.strip()


def classify(relative: str) -> str | None:
    path = PurePosixPath(relative)
    if path.is_absolute() or ".." in path.parts or "\\" in relative:
        raise ModelDataError(f"Unsafe repository path: {relative!r}")
    if relative.startswith("Assets/DiffusionLab/Data/SD/") and path.suffix in MODEL_SUFFIXES:
        return "sdModelData"
    if relative.startswith("Assets/DiffusionLab/SD/Parameters/") and path.suffix == ".asset":
        return "sdModelData"
    if relative.startswith("Assets/DiffusionLab/Data/") and path.suffix in MODEL_SUFFIXES:
        return "teaLegacyData"
    if relative == "Assets/DiffusionLab/Textures/Weights.asset":
        return "teaLegacyData"
    if relative.startswith("Assets/DiffusionLab/Textures/Tea/") and path.suffix == ".asset":
        return "teaLegacyData"
    return None


def source_inventory(root: Path) -> dict[str, dict[str, Any]]:
    inventory: dict[str, dict[str, Any]] = {}
    for relative in tracked_paths(root):
        bundle = classify(relative)
        if bundle is None:
            continue
        path = root / relative
        if path.is_symlink() or not path.is_file():
            raise ModelDataError(f"Tracked model data is missing or not a regular file: {relative}")
        inventory[relative] = {
            "bundle": bundle,
            "bytes": path.stat().st_size,
            "sha256": sha256_file(path),
        }

    untracked = subprocess.run(
        ["git", "-C", str(root), "ls-files", "--others", "--exclude-standard", "-z"],
        check=True,
        capture_output=True,
    ).stdout.split(b"\0")
    for raw in untracked:
        if raw:
            relative = raw.decode("utf-8")
            if classify(relative):
                raise ModelDataError(f"Refusing to ignore untracked model data: {relative}")
    return inventory


def load_lock(path: Path) -> dict[str, Any]:
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise ModelDataError(f"Cannot read model data lock {path}: {exc}") from exc
    if data.get("schemaVersion") != LOCK_SCHEMA or not isinstance(data.get("bundles"), dict):
        raise ModelDataError(f"Unsupported model data lock: {path}")
    for bundle, archive in data["bundles"].items():
        if bundle not in BUNDLES or not isinstance(archive, dict):
            raise ModelDataError(f"Unknown or invalid bundle in lock: {bundle}")
        if archive.get("fileName") != BUNDLES[bundle]:
            raise ModelDataError(f"Unexpected archive name for {bundle}: {archive.get('fileName')!r}")
        files = archive.get("files")
        if not isinstance(files, list) or not files:
            raise ModelDataError(f"Bundle {bundle} has no file inventory")
    return data


def verify_lock_files(lock: dict[str, Any], target: Path) -> int:
    verified = 0
    for bundle, archive in lock["bundles"].items():
        for entry in archive["files"]:
            relative = entry.get("path")
            expected_size = entry.get("bytes")
            expected_sha = entry.get("sha256")
            if not isinstance(relative, str) or not isinstance(expected_size, int):
                raise ModelDataError(f"Invalid file entry in {bundle}")
            if PurePosixPath(relative).is_absolute() or ".." in PurePosixPath(relative).parts:
                raise ModelDataError(f"Unsafe path in {bundle}: {relative!r}")
            path = target / relative
            if path.is_symlink() or not path.is_file():
                raise ModelDataError(f"Missing model data: {relative}")
            if path.stat().st_size != expected_size:
                raise ModelDataError(f"Size mismatch: {relative}")
            if sha256_file(path) != expected_sha:
                raise ModelDataError(f"SHA-256 mismatch: {relative}")
            verified += 1
    return verified


def pack(source: Path, output: Path, lock_path: Path, force: bool) -> dict[str, Any]:
    inventory = source_inventory(source)
    output.mkdir(parents=True, exist_ok=True)
    archives: dict[str, Any] = {}
    for bundle, archive_name in BUNDLES.items():
        entries = sorted(
            ((relative, entry) for relative, entry in inventory.items() if entry["bundle"] == bundle),
            key=lambda item: item[0],
        )
        if not entries:
            raise ModelDataError(f"No tracked files found for bundle: {bundle}")
        archive_path = output / archive_name
        if archive_path.exists() and not force:
            raise ModelDataError(f"Archive already exists; pass --force to replace it: {archive_path}")
        with zipfile.ZipFile(
            archive_path,
            "w",
            compression=zipfile.ZIP_STORED,
            allowZip64=True,
        ) as archive:
            for relative, entry in entries:
                info = zipfile.ZipInfo(relative, date_time=(2026, 10, 18, 0, 0, 0))
                info.compress_type = zipfile.ZIP_STORED
                info.external_attr = 0o100644 << 16
                with (source / relative).open("rb") as stream, archive.open(info, "w") as destination:
                    while chunk := stream.read(8 * 1024 * 1024):
                        destination.write(chunk)
        archives[bundle] = {
            "fileName": archive_name,
            "bytes": archive_path.stat().st_size,
            "sha256": sha256_file(archive_path),
            "fileCount": len(entries),
            "files": [
                {"path": relative, "bytes": entry["bytes"], "sha256": entry["sha256"]}
                for relative, entry in entries
            ],
        }

    lock = {
        "schemaVersion": LOCK_SCHEMA,
        "sourceCommit": source_commit(source),
        "createdUtc": datetime.now(timezone.utc).replace(microsecond=0).isoformat(),
        "publication": {
            "status": "local-archives-only",
            "githubRelease": None,
        },
        "bundles": archives,
    }
    lock_path.parent.mkdir(parents=True, exist_ok=True)
    lock_path.write_text(json.dumps(lock, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return {
        "lock": str(lock_path),
        "files": len(inventory),
        "bytes": sum(entry["bytes"] for entry in inventory.values()),
        "bundles": {
            name: {
                "file": str(output / archive["fileName"]),
                "bytes": archive["bytes"],
                "sha256": archive["sha256"],
                "fileCount": archive["fileCount"],
            }
            for name, archive in archives.items()
        },
    }


def restore(lock_path: Path, archives: Path, target: Path) -> dict[str, Any]:
    lock = load_lock(lock_path)
    restored = 0
    skipped = 0
    for bundle, entry in lock["bundles"].items():
        archive_path = archives / entry["fileName"]
        if not archive_path.is_file():
            raise ModelDataError(f"Missing archive for {bundle}: {archive_path}")
        if archive_path.stat().st_size != entry["bytes"]:
            raise ModelDataError(f"Archive size mismatch: {archive_path}")
        if sha256_file(archive_path) != entry["sha256"]:
            raise ModelDataError(f"Archive SHA-256 mismatch: {archive_path}")
        expected = {item["path"]: item for item in entry["files"]}
        with zipfile.ZipFile(archive_path) as archive:
            names = archive.namelist()
            if len(names) != len(set(names)) or set(names) != set(expected):
                raise ModelDataError(f"Archive inventory differs from lock: {archive_path}")
            for relative in sorted(names):
                path = PurePosixPath(relative)
                if path.is_absolute() or ".." in path.parts or "\\" in relative:
                    raise ModelDataError(f"Unsafe archive path: {relative!r}")
                item = expected[relative]
                destination = target / relative
                if destination.exists():
                    if destination.is_symlink() or not destination.is_file():
                        raise ModelDataError(f"Destination is not a regular file: {destination}")
                    if (
                        destination.stat().st_size == item["bytes"]
                        and sha256_file(destination) == item["sha256"]
                    ):
                        skipped += 1
                        continue
                    raise ModelDataError(f"Refusing to overwrite different data: {destination}")
                destination.parent.mkdir(parents=True, exist_ok=True)
                extracted = Path(archive.extract(relative, target))
                if extracted.stat().st_size != item["bytes"] or sha256_file(extracted) != item["sha256"]:
                    raise ModelDataError(f"Extracted file failed validation: {relative}")
                restored += 1
    verified = verify_lock_files(lock, target)
    return {"restored": restored, "skipped": skipped, "verified": verified}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)

    pack_parser = subparsers.add_parser("pack", help="create local archives and a lock file")
    pack_parser.add_argument("--source", type=Path, required=True)
    pack_parser.add_argument("--output", type=Path, required=True)
    pack_parser.add_argument("--lock", type=Path)
    pack_parser.add_argument("--force", action="store_true")

    verify_parser = subparsers.add_parser("verify", help="verify files listed by the lock")
    verify_parser.add_argument("--target", type=Path, required=True)
    verify_parser.add_argument("--lock", type=Path)

    restore_parser = subparsers.add_parser("restore", help="verify archives and restore model data")
    restore_parser.add_argument("--target", type=Path, required=True)
    restore_parser.add_argument("--archives", type=Path, required=True)
    restore_parser.add_argument("--lock", type=Path)

    args = parser.parse_args()
    try:
        if args.command == "pack":
            lock_path = args.lock or (args.source / "model-assets.lock.json")
            result = pack(args.source, args.output, lock_path, args.force)
        elif args.command == "verify":
            lock_path = args.lock or (args.target / "model-assets.lock.json")
            result = {"verified": verify_lock_files(load_lock(lock_path), args.target)}
        else:
            lock_path = args.lock or (args.target / "model-assets.lock.json")
            result = restore(lock_path, args.archives, args.target)
    except (ModelDataError, OSError, subprocess.CalledProcessError, zipfile.BadZipFile) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1
    print(json.dumps(result, indent=2, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
