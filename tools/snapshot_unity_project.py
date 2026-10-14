#!/usr/bin/env python3
"""Capture the saved Windows Unity project into this local worktree, preserving GUIDs."""

import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tarfile
import tempfile
import uuid

from unity_project_common import pipeline, checked_pwsh
from unity_remote import DEFAULT_HOST, DEFAULT_PROJECT, pwsh_literal
from sync_windows_git import parse_preserve_manifest

ROOT = Path(__file__).resolve().parents[1]
TASK_TMP = Path.home() / "tmp/vrc-diffusion"
TREES = (
    "Assets",
    "AgentScripts",
    "ProjectSettings",
    "Packages/com.unity.pipeline",
    "Packages/com.vrchat.core.vpm-resolver",
)
MANIFESTS = (
    "Packages/.gitignore",
    "Packages/manifest.json",
    "Packages/packages-lock.json",
    "Packages/vpm-manifest.json",
)


def digest(path):
    return hashlib.sha256(path.read_bytes()).digest() if path.is_file() else None


def validate_preserved_snapshot(root, unpacked, baseline_head, files):
    """Keep audited Editor-only edits out of the mirror without losing either copy."""
    head = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
    if head != baseline_head:
        raise RuntimeError("Preserve manifest must name the current local HEAD.")
    for path, expected in files.items():
        if not any(path == base or path.startswith(base + "/") for base in (*TREES, *MANIFESTS)):
            raise RuntimeError("Preserved path is outside snapshot scope: " + path)
        original = subprocess.check_output(["git", "show", baseline_head + ":" + path], cwd=root)
        if digest(root / path) != hashlib.sha256(original).digest():
            raise RuntimeError("Local preserved path has edits: " + path)
        if digest(unpacked / path) != bytes.fromhex(expected):
            raise RuntimeError("Editor preserved path changed after audit: " + path)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default=DEFAULT_HOST)
    parser.add_argument("--project-path", default=DEFAULT_PROJECT)
    parser.add_argument("--preserve-dirty-manifest", type=Path,
                        help="Leave exact audited Editor-only tracked edits out of this snapshot; verify both copies before copying anything.")
    parser.add_argument("--remote-temp-root",
                        help="Optional Windows temporary directory for the snapshot archive.")
    parser.add_argument(
        "--accept-editor-file",
        action="append",
        default=[],
        metavar="PATH",
        help="Adopt this exact saved Unity asset or generated meta when its local mirror differs; back up the local file first.",
    )
    parser.add_argument(
        "--accept-editor-removal",
        action="append",
        default=[],
        metavar="PATH",
        help="Adopt this exact deletion from the Editor after backing up the local file.",
    )
    options = parser.parse_args()
    accepted = set(options.accept_editor_file)
    removals = set(options.accept_editor_removal)
    preserve_head, preserved = None, {}
    if options.preserve_dirty_manifest is not None:
        preserve_head, preserved = parse_preserve_manifest(json.loads(options.preserve_dirty_manifest.read_text(encoding="utf-8")))
        if set(preserved) & (accepted | removals):
            parser.error("A preserved path cannot also be adopted or removed.")
        remote_head = checked_pwsh(f"& git -C {pwsh_literal(options.project_path)} rev-parse HEAD", options.host).strip()
        if remote_head != preserve_head:
            raise RuntimeError("Preserve manifest must name the current Editor HEAD.")
    for option_name, paths in (
        ("--accept-editor-file", accepted),
        ("--accept-editor-removal", removals),
    ):
        for path in paths:
            relative = Path(path)
            if (
                relative.is_absolute()
                or ".." in relative.parts
                or not path.startswith("Assets/")
            ):
                parser.error(f"{option_name} must name an exact relative Assets/ path")
    for path in accepted:
        if Path(path).suffix not in (
            ".unity", ".prefab", ".asset", ".mat", ".controller", ".anim", ".meta"
        ):
            parser.error("--accept-editor-file applies only to Unity assets or generated metadata")

    state = pipeline(options.host, options.project_path, "editor_status")
    if state.get("playMode") != "stopped" or state.get("compiling"):
        raise RuntimeError("Stop Play Mode and finish compilation before taking a snapshot.")
    scenes = pipeline(
        options.host,
        options.project_path,
        "eval",
        "return Enumerable.Range(0, UnityEngine.SceneManagement.SceneManager.sceneCount).Select(i => new { path = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).path, dirty = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty }).ToArray();",
    )
    if not scenes.get("success") or any(scene["dirty"] for scene in scenes["result"]):
        raise RuntimeError(
            "Save the intended scene edits before taking a snapshot; existing unsaved edits are preserved."
        )

    TASK_TMP.mkdir(parents=True, exist_ok=True)
    name = "unity-snapshot-" + uuid.uuid4().hex + ".tgz"
    paths = (*TREES, *MANIFESTS)
    temp_root = (f"$taskRoot = {pwsh_literal(options.remote_temp_root)}; " if options.remote_temp_root
                 else "$taskRoot = Join-Path ([Environment]::GetFolderPath('UserProfile')) 'tmp\\vrc-diffusion'; ")
    remote_archive = checked_pwsh(
        temp_root +
        "New-Item -ItemType Directory -Path $taskRoot -Force | Out-Null; "
        f"$archive = Join-Path $taskRoot {pwsh_literal(name)}; "
        f"$project = {pwsh_literal(options.project_path)}; "
        "$archivePaths = @(" + ", ".join(pwsh_literal(path) for path in paths) + "); "
        "$archivePaths = @($archivePaths | Where-Object { Test-Path -LiteralPath (Join-Path $project $_) }); "
        "if ($archivePaths.Count -eq 0) { throw 'No Unity project paths were found to snapshot' }; "
        "& tar -czf $archive -C $project @archivePaths; "
        "if ($LASTEXITCODE -ne 0) { throw 'Unity snapshot archive failed' }; Write-Output $archive",
        options.host,
    )
    try:
        with tempfile.TemporaryDirectory(prefix="snapshot-", dir=TASK_TMP) as temporary:
            stage = Path(temporary)
            archive = stage / name
            subprocess.run(
                [
                    "scp", "-q", "-o", "BatchMode=yes", "-o", "LogLevel=ERROR",
                    options.host + ":" + remote_archive.replace("\\", "/"), str(archive),
                ],
                check=True,
            )
            unpacked = stage / "project"
            with tarfile.open(archive, "r:gz") as tar:
                tar.extractall(unpacked, filter="data")
            if preserved:
                validate_preserved_snapshot(ROOT, unpacked, preserve_head, preserved)

            changed = set()
            if (ROOT / ".git").exists():
                changed.update(
                    path
                    for path in subprocess.check_output(
                        ["git", "ls-files", "--others", "--exclude-standard", "-z"], cwd=ROOT
                    ).decode().split("\0")
                    if path
                )
                if subprocess.run(
                    ["git", "rev-parse", "--verify", "HEAD"], cwd=ROOT, capture_output=True
                ).returncode == 0:
                    changed.update(
                        path
                        for path in subprocess.check_output(
                            ["git", "diff", "HEAD", "--name-only", "-z"], cwd=ROOT
                        ).decode().split("\0")
                        if path
                    )
            mirrored = (*TREES, *MANIFESTS)
            # Also catch ignored files and a not-yet-initialized mirror. Git status alone
            # cannot protect local files in those cases.
            for base in mirrored:
                target = ROOT / base
                if target.is_file():
                    local_files = (target,)
                elif target.is_dir():
                    local_files = (path for path in target.rglob("*") if path.is_file())
                else:
                    local_files = ()
                for path in local_files:
                    relative = path.relative_to(ROOT).as_posix()
                    if digest(path) != digest(unpacked / relative):
                        changed.add(relative)
            conflicts = [
                path
                for path in changed
                if path
                and path not in preserved
                and any(path == base or path.startswith(base + "/") for base in mirrored)
                and digest(ROOT / path) != digest(unpacked / path)
            ]
            unresolved = [path for path in conflicts if path not in accepted and path not in removals]
            if unresolved:
                raise RuntimeError(
                    "Local edits differ from the Editor snapshot; resolve them first: "
                    + ", ".join(sorted(unresolved))
                )

            stale_removal_requests = [path for path in removals if (unpacked / path).exists()]
            if stale_removal_requests:
                raise RuntimeError(
                    "Editor removal was requested, but these assets still exist: "
                    + ", ".join(sorted(stale_removal_requests))
                )

            adopted = []
            for path in conflicts:
                removed_by_editor = path in removals and not (unpacked / path).exists()
                if path in removals and not removed_by_editor:
                    raise RuntimeError("Editor removal was requested, but the asset still exists: " + path)
                if not removed_by_editor and not (unpacked / path).is_file():
                    raise RuntimeError("Cannot adopt a missing Editor asset: " + path)
                backup = TASK_TMP / (name.removesuffix(".tgz") + "-local-backup") / path
                backup.parent.mkdir(parents=True, exist_ok=True)
                if (ROOT / path).is_file():
                    shutil.copy2(ROOT / path, backup)
                adopted.append({"path": path, "backup": str(backup), "removedByEditor": removed_by_editor})

            copied = removed = 0
            for base in TREES:
                source, target = unpacked / base, ROOT / base
                for old in target.rglob("*") if target.exists() else ():
                    if old.relative_to(ROOT).as_posix() in preserved:
                        continue
                    if old.is_file() and not (source / old.relative_to(target)).is_file():
                        old.unlink()
                        removed += 1
                for file in source.rglob("*") if source.exists() else ():
                    if not file.is_file():
                        continue
                    if file.relative_to(unpacked).as_posix() in preserved:
                        continue
                    destination = target / file.relative_to(source)
                    if digest(file) != digest(destination):
                        destination.parent.mkdir(parents=True, exist_ok=True)
                        shutil.copy2(file, destination)
                        copied += 1
            for path in MANIFESTS:
                if path in preserved:
                    continue
                source, destination = unpacked / path, ROOT / path
                if source.is_file():
                    destination.parent.mkdir(parents=True, exist_ok=True)
                    if digest(source) != digest(destination):
                        shutil.copy2(source, destination)
                        copied += 1
                elif destination.is_file():
                    destination.unlink()
                    removed += 1

            print(
                json.dumps(
                    {
                        "captured": True,
                        "copiedFiles": copied,
                        "removedStaleMirrorFiles": removed,
                        "adoptedEditorFiles": adopted,
                        "scenePaths": [scene["path"] for scene in scenes["result"]],
                        "preservedUnityMeta": True,
                        "preservedEditorOnlyPaths": sorted(preserved),
                    },
                    indent=2,
                )
            )
    finally:
        checked_pwsh(f"Remove-Item -LiteralPath {pwsh_literal(remote_archive)} -Force", options.host)


if __name__ == "__main__":
    main()
