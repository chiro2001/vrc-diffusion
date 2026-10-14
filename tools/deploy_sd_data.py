#!/usr/bin/env python3
"""Validate and copy native Counterfeit-V3.0 W8A8 data without invoking Unity."""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import math
import os
from pathlib import Path, PurePosixPath, PureWindowsPath
import re
import shutil
import struct
import subprocess
import sys
import tarfile
import tempfile
import uuid
from typing import Any

from unity_remote import DEFAULT_HOST, DEFAULT_PROJECT


ROOT = Path(__file__).resolve().parents[1]
TASK_TMP = Path.home() / "tmp/vrc-diffusion"
DEFAULT_SOURCE = Path("/home/chiro/tmp/vrc-diffusion/sd-anime/w8a8-export")
DEFAULT_CONDITIONS = Path("/home/chiro/tmp/vrc-diffusion/sd-anime/conditions")
FIXED_SOURCE = Path("/home/chiro/tmp/vrc-diffusion/sd-anime/fixed-w8a8-export")
FIXED_CONDITIONS = Path("/home/chiro/tmp/vrc-diffusion/sd-anime/fixed-conditions")
TARGET_RELATIVE = PurePosixPath("Assets/DiffusionLab/Data/SD")
REMOTE_STAGE_ROOT = r"E:\tmp\vrc-diffusion"
EXPECTED_REPO = "gsdf/Counterfeit-V3.0"
BASELINE_REVISION = "f79f307806896050ac376e46d8081d221b8aa93a"
FIX_REVISION = "d8ce63b3eb8e9102e54b3fcc07f9cb7a28ef30fc"
FIX_CHECKPOINT = "Counterfeit-V3.0_fix_fp16.safetensors"
FIX_CHECKPOINT_SHA256 = "a54c944e4c04e9d9ca43468ef5b90ea0408bb6264829a4980de79a768df7179f"
NATIVE_EXTENSIONS = {".png", ".json", ".bytes"}
PATH_PART = re.compile(r"^[A-Za-z0-9_.-]+$")
SHA256 = re.compile(r"^[0-9a-f]{64}$")


class DeployError(RuntimeError):
    pass


def sha256_file(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def safe_relative(value: str) -> PurePosixPath:
    path = PurePosixPath(value)
    if (
        not value
        or path.is_absolute()
        or ".." in path.parts
        or "." in path.parts
        or not path.parts
        or not all(PATH_PART.fullmatch(part) for part in path.parts)
    ):
        raise DeployError(f"Unsafe relative asset path: {value!r}")
    if Path(value).suffix.lower() not in NATIVE_EXTENSIONS:
        raise DeployError(f"Refusing non-native asset type: {value}")
    return path


def hash_inventory(directory: Path, allowed: set[str]) -> dict[str, dict[str, Any]]:
    if not directory.is_dir() or directory.is_symlink():
        raise DeployError(f"Expected a real source directory: {directory}")
    inventory: dict[str, dict[str, Any]] = {}
    for path in sorted(directory.rglob("*")):
        if path.is_symlink():
            raise DeployError(f"Refusing symlink in model source: {path}")
        if not path.is_file() or path.suffix.lower() not in allowed:
            continue
        relative = path.relative_to(directory).as_posix()
        safe_relative(relative)
        inventory[relative] = {
            "source": path,
            "path": relative,
            "sizeBytes": path.stat().st_size,
            "sha256": sha256_file(path),
        }
    return inventory


def json_file(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except Exception as exc:
        raise DeployError(f"Invalid JSON file {path}: {exc}") from exc


def validate_png(path: Path, expected_sha: str, expected_width: int, expected_height: int) -> str:
    actual_sha = sha256_file(path)
    if actual_sha != expected_sha:
        raise DeployError(
            f"PNG SHA-256 mismatch for {path.name}: expected {expected_sha}, got {actual_sha}"
        )
    with path.open("rb") as stream:
        header = stream.read(26)
    if len(header) < 26 or header[:8] != b"\x89PNG\r\n\x1a\n" or header[12:16] != b"IHDR":
        raise DeployError(f"Invalid PNG header: {path}")
    width, height = struct.unpack(">II", header[16:24])
    bit_depth, color_type = header[24], header[25]
    if (width, height) != (expected_width, expected_height):
        raise DeployError(
            f"PNG dimensions differ for {path.name}: {(width, height)} != "
            f"{(expected_width, expected_height)}"
        )
    if bit_depth != 8 or color_type != 6:
        raise DeployError(f"Expected RGBA8 PNG weight data, got bit_depth={bit_depth}, color_type={color_type}: {path}")
    return actual_sha


def validate_parameter_bytes(path: Path, channels: int) -> None:
    expected_size = channels * 16  # four little-endian float32 values per channel
    size = path.stat().st_size
    if size != expected_size:
        raise DeployError(
            f"Parameter byte length mismatch for {path.name}: {size} != "
            f"{channels} channels × 16 bytes"
        )
    with path.open("rb") as stream:
        while chunk := stream.read(4 * 4096):
            if len(chunk) % 4:
                raise DeployError(f"Parameter data is not float32 aligned: {path}")
            values = struct.unpack("<" + "f" * (len(chunk) // 4), chunk)
            if not all(math.isfinite(value) for value in values):
                raise DeployError(f"Non-finite parameter value in {path}")


def collect_model_files(source: Path) -> tuple[list[dict[str, Any]], dict[str, Any]]:
    inventory = hash_inventory(source, NATIVE_EXTENSIONS)
    model_rel = "model.json"
    stats_rel = "quantization-stats.json"
    if model_rel not in inventory or stats_rel not in inventory:
        raise DeployError("w8a8-export must contain model.json and quantization-stats.json")
    model = json_file(inventory[model_rel]["source"])
    stats = json_file(inventory[stats_rel]["source"])
    if not isinstance(model, dict) or not isinstance(model.get("weights"), dict):
        raise DeployError("model.json is missing its weights manifest")
    revision = model.get("revision")
    if (
        model.get("source") != EXPECTED_REPO
        or revision not in {BASELINE_REVISION, FIX_REVISION}
        or model.get("license") != "CreativeML Open RAIL-M"
        or model.get("layout") != "NHWC4"
    ):
        raise DeployError("model.json source, revision, license, or layout does not match the pinned model")
    checkpoint_sha256 = None
    if revision == FIX_REVISION:
        conversion = model.get("conversion")
        if not isinstance(conversion, dict) or any(
            conversion.get(key) != expected
            for key, expected in {
                "repoId": EXPECTED_REPO,
                "revision": FIX_REVISION,
                "checkpoint": FIX_CHECKPOINT,
                "checkpointSHA256": FIX_CHECKPOINT_SHA256,
                "configurationSourceRevision": BASELINE_REVISION,
            }.items()
        ):
            raise DeployError("model.json does not record the pinned fix checkpoint conversion")
        checkpoint_sha256 = conversion["checkpointSHA256"]
    if not isinstance(stats, list):
        raise DeployError("quantization-stats.json must contain a list")

    expected_parameters: set[str] = set()
    expected_pngs: set[str] = set()
    w8a8_names: set[str] = set()
    for tensor_name, item in model["weights"].items():
        if not isinstance(item, dict) or not isinstance(item.get("channels"), int) or item["channels"] <= 0:
            raise DeployError(f"Invalid tensor manifest entry: {tensor_name}")
        parameter_file = item.get("parameterFile")
        if not isinstance(parameter_file, str):
            raise DeployError(f"Missing parameterFile for {tensor_name}")
        parameter_path = safe_relative(parameter_file)
        if parameter_path.parent != PurePosixPath(".") or parameter_path.suffix != ".bytes":
            raise DeployError(f"Parameter file must be a root-level .bytes file: {parameter_file}")
        expected_parameters.add(parameter_file)
        if parameter_file not in inventory:
            raise DeployError(f"Missing parameter file for {tensor_name}: {parameter_file}")
        validate_parameter_bytes(inventory[parameter_file]["source"], item["channels"])

        weight_file = item.get("weightFile")
        if weight_file is not None:
            weight_path = safe_relative(weight_file)
            if weight_path.parent != PurePosixPath(".") or weight_path.suffix != ".png":
                raise DeployError(f"Weight image must be a root-level .png file: {weight_file}")
            expected_pngs.add(weight_file)
            if weight_file not in inventory:
                raise DeployError(f"Missing PNG weight file for {tensor_name}: {weight_file}")
            expected_sha = item.get("sha256")
            expected_width, expected_height = item.get("width"), item.get("height")
            if not isinstance(expected_sha, str) or not SHA256.fullmatch(expected_sha):
                raise DeployError(f"Invalid PNG SHA-256 in model.json for {tensor_name}")
            if not isinstance(expected_width, int) or not isinstance(expected_height, int):
                raise DeployError(f"Missing PNG dimensions in model.json for {tensor_name}")
            validate_png(
                inventory[weight_file]["source"], expected_sha, expected_width, expected_height
            )
            if item.get("kind") == "w8a8":
                w8a8_names.add(tensor_name)
        elif item.get("kind") == "w8a8":
            raise DeployError(f"W8A8 tensor has no PNG weight image: {tensor_name}")

    actual_parameters = {
        path for path in inventory if Path(path).suffix.lower() == ".bytes"
    }
    actual_pngs = {path for path in inventory if Path(path).suffix.lower() == ".png"}
    if actual_parameters != expected_parameters:
        raise DeployError(
            "Unreferenced or missing parameter files: "
            f"unreferenced={sorted(actual_parameters-expected_parameters)[:10]}, "
            f"missing={sorted(expected_parameters-actual_parameters)[:10]}"
        )
    if actual_pngs != expected_pngs:
        raise DeployError(
            "Unreferenced or missing weight PNGs: "
            f"unreferenced={sorted(actual_pngs-expected_pngs)[:10]}, "
            f"missing={sorted(expected_pngs-actual_pngs)[:10]}"
        )

    stats_names: set[str] = set()
    for row in stats:
        if not isinstance(row, dict) or not isinstance(row.get("name"), str):
            raise DeployError("Invalid row in quantization-stats.json")
        name = row["name"]
        if name in stats_names:
            raise DeployError(f"Duplicate quantization stats for {name}")
        stats_names.add(name)
        for metric in ("maxAbs", "rmse"):
            value = row.get(metric)
            if not isinstance(value, (int, float)) or not math.isfinite(value) or value < 0:
                raise DeployError(f"Invalid {metric} quantization statistic for {name}")
    if stats_names != w8a8_names:
        raise DeployError(
            "Quantization statistics do not match W8A8 tensors: "
            f"missing={sorted(w8a8_names-stats_names)[:10]}, "
            f"extra={sorted(stats_names-w8a8_names)[:10]}"
        )

    entries = []
    for relative, metadata in sorted(inventory.items()):
        entries.append(
            {
                "source": metadata["source"],
                "path": relative,
                "sizeBytes": metadata["sizeBytes"],
                "sha256": metadata["sha256"],
            }
        )
    return entries, {
        "manifestSha256": inventory[model_rel]["sha256"],
        "source": model["source"],
        "revision": revision,
        "checkpointSHA256": checkpoint_sha256,
        "license": model["license"],
        "layout": model["layout"],
        "formatVersion": model["formatVersion"],
        "activationQuantization": model["activationQuantization"],
        "accumulation": model["accumulation"],
        "parameterCount": model.get("parameterCount"),
        "latentScale": model.get("latentScale"),
        "parameterFileCount": len(expected_parameters),
        "weightPngCount": len(expected_pngs),
        "w8a8TensorCount": len(w8a8_names),
        "quantizationStatCount": len(stats_names),
    }


def collect_conditions_files(
    conditions_dir: Path,
) -> tuple[list[dict[str, Any]], dict[str, Any]]:
    inventory = hash_inventory(conditions_dir, {".json", ".bytes"})
    if "conditions.json" not in inventory:
        raise DeployError("conditions directory must contain conditions.json")
    document = json_file(inventory["conditions.json"]["source"])
    if not isinstance(document, dict):
        raise DeployError("conditions.json must contain a JSON object")
    source = document.get("source", {})
    if source.get("repoId") != EXPECTED_REPO:
        raise DeployError("conditions.json refers to a different model source")
    revision = source.get("revision")
    if revision not in {BASELINE_REVISION, FIX_REVISION}:
        raise DeployError("conditions.json refers to a different model revision")
    checkpoint_sha256 = None
    if revision == FIX_REVISION:
        if (
            source.get("checkpoint") != FIX_CHECKPOINT
            or source.get("checkpointSha256") != FIX_CHECKPOINT_SHA256
            or source.get("configurationSourceRevision") != BASELINE_REVISION
        ):
            raise DeployError("conditions.json does not record the pinned fix checkpoint source")
        checkpoint_sha256 = source["checkpointSha256"]
    embedding = document.get("embedding", {})
    if (
        embedding.get("shape") != [77, 768]
        or embedding.get("dtype") != "float32"
        or embedding.get("endianness") != "little"
        or embedding.get("layout") != "row-major [token, hidden]"
    ):
        raise DeployError("Unexpected condition embedding shape or encoding")
    records = document.get("conditions")
    if not isinstance(records, list) or len(records) != 5:
        raise DeployError("Expected one shared negative and four positive condition embeddings")

    references: set[str] = set()
    labels: list[str] = []
    for record in records:
        if not isinstance(record, dict):
            raise DeployError("Invalid condition row in conditions.json")
        relative = record.get("path")
        if not isinstance(relative, str):
            raise DeployError("Condition is missing its output path")
        rel_path = safe_relative(relative)
        if rel_path.parent != PurePosixPath(".") or rel_path.suffix != ".bytes":
            raise DeployError(f"Condition embedding path must be a root-level .bytes file: {relative}")
        references.add(relative)
        labels.append(record.get("label", ""))
        metadata = inventory.get(relative)
        if metadata is None:
            raise DeployError(f"Missing condition embedding: {relative}")
        expected_sha = record.get("sha256")
        if not isinstance(expected_sha, str) or not SHA256.fullmatch(expected_sha):
            raise DeployError(f"Invalid embedding SHA-256 in conditions.json for {relative}")
        if metadata["sha256"] != expected_sha:
            raise DeployError(f"Embedding SHA-256 mismatch: {relative}")
        if metadata["sizeBytes"] != 77 * 768 * 4 or record.get("byteLength") != metadata["sizeBytes"]:
            raise DeployError(f"Embedding byte length mismatch: {relative}")
        if record.get("truncated") is True:
            raise DeployError(f"Unexpectedly truncated preset prompt: {record.get('label')}")

    actual_embeddings = {
        path for path in inventory if Path(path).suffix.lower() == ".bytes"
    }
    if actual_embeddings != references:
        raise DeployError(
            "Unreferenced or missing condition embeddings: "
            f"unreferenced={sorted(actual_embeddings-references)}, "
            f"missing={sorted(references-actual_embeddings)}"
        )
    negative = document.get("sharedNegativeCondition", {})
    if (
        negative.get("index") != 0
        or negative.get("label") != records[0].get("label")
        or negative.get("path") != records[0].get("path")
        or negative.get("sha256") != records[0].get("sha256")
        or records[0].get("label") != "negative_common"
    ):
        raise DeployError("Common negative condition entry is inconsistent")
    if set(negative.get("usedWith", [])) != set(labels[1:]):
        raise DeployError("Common negative embedding does not reference all positive presets")

    entries = [
        {
            "source": metadata["source"],
            "path": f"conditions/{relative}",
            "sizeBytes": metadata["sizeBytes"],
            "sha256": metadata["sha256"],
        }
        for relative, metadata in sorted(inventory.items())
    ]
    return entries, {"revision": revision, "checkpointSHA256": checkpoint_sha256}


def combine_entries(
    model_files: list[dict[str, Any]],
    condition_files: list[dict[str, Any]],
    baseline_hashes: dict[str, str] | None = None,
) -> list[dict[str, Any]]:
    entries = []
    baseline_hashes = baseline_hashes or {}
    for entry in model_files + condition_files:
        baseline_sha = baseline_hashes.get(entry["path"])
        if baseline_sha:
            entry = {**entry, "baselineSha256": baseline_sha}
        entries.append(entry)
    paths = [entry["path"] for entry in entries]
    if len(paths) != len(set(paths)):
        raise DeployError("Duplicate output path between model and conditions data")
    for entry in entries:
        safe_relative(entry["path"])
        if Path(entry["source"]).suffix.lower() not in NATIVE_EXTENSIONS:
            raise DeployError(f"Refusing unsupported source: {entry['source']}")
    return sorted(entries, key=lambda entry: entry["path"])


def local_preflight(project: Path, entries: list[dict[str, Any]]) -> dict[str, Any]:
    project = project.absolute()
    target = project.joinpath(*TARGET_RELATIVE.parts)
    if not project.is_dir():
        raise DeployError(f"Local project directory does not exist: {project}")
    if target.exists() and not target.is_dir():
        raise DeployError(f"Local SD destination is not a directory: {target}")
    cursor = target
    while cursor == project or project in cursor.parents:
        if cursor.exists() and cursor.is_symlink():
            raise DeployError(f"Refusing symlink in local destination path: {cursor}")
        if cursor == project:
            break
        cursor = cursor.parent

    new: list[dict[str, Any]] = []
    matching: list[str] = []
    replacements: list[dict[str, Any]] = []
    conflicts: list[dict[str, str]] = []
    for entry in entries:
        dest = target.joinpath(*PurePosixPath(entry["path"]).parts)
        if dest.is_symlink():
            conflicts.append({"path": entry["path"], "reason": "destination is a symlink"})
        elif dest.exists() and not dest.is_file():
            conflicts.append({"path": entry["path"], "reason": "destination is not a regular file"})
        elif dest.is_file():
            actual = sha256_file(dest)
            if actual == entry["sha256"]:
                matching.append(entry["path"])
            elif entry.get("baselineSha256") == actual:
                replacements.append(entry)
            else:
                conflicts.append(
                    {"path": entry["path"], "reason": f"hash mismatch: {actual}"}
                )
        else:
            # The baseline hash is only an overwrite allowance for a file that
            # still exists. A missing target remains a normal new-file copy.
            new.append({key: value for key, value in entry.items() if key != "baselineSha256"})
    return {
        "target": str(target),
        "new": new,
        "replace": replacements,
        "matching": matching,
        "conflicts": conflicts,
    }


def validate_remote_project(project_path: str) -> PureWindowsPath:
    project = PureWindowsPath(project_path)
    if not project.is_absolute() or ".." in project.parts:
        raise DeployError("--project-path must be an absolute Windows path without '..'")
    if not re.fullmatch(r"[A-Za-z]:\\(?:[A-Za-z0-9_.-]+\\)*[A-Za-z0-9_.-]+", str(project)):
        raise DeployError("--project-path contains unsupported characters")
    return project


def run_remote_encoded(host: str, script: str, timeout: int = 180) -> Any:
    encoded = base64.b64encode(script.encode("utf-16le")).decode("ascii")
    command = [
        "ssh",
        "-T",
        "-o",
        "BatchMode=yes",
        "-o",
        "ConnectTimeout=20",
        "-o",
        "LogLevel=ERROR",
        host,
        "pwsh -NoLogo -NoProfile -NonInteractive -EncodedCommand " + encoded,
    ]
    result = subprocess.run(command, capture_output=True, timeout=timeout)
    stdout = result.stdout.decode("utf-8", "replace").strip()
    stderr = result.stderr.decode("utf-8", "replace").strip()
    if result.returncode != 0:
        raise DeployError(
            f"Remote PowerShell command failed (exit={result.returncode}). "
            f"stdout={stdout[-2000:]!r}; stderr={stderr[-2000:]!r}"
        )
    try:
        return json.loads(stdout)
    except json.JSONDecodeError as exc:
        raise DeployError(f"Remote PowerShell returned non-JSON output: {stdout[-2000:]!r}") from exc


REMOTE_PREFLIGHT_BODY = r'''
$target = [IO.Path]::GetFullPath((Join-Path $project 'Assets\DiffusionLab\Data\SD'))
$projectRoot = $project.TrimEnd('\', '/')
$targetRoot = $target.TrimEnd('\', '/')
$targetPrefix = $targetRoot + '\'
function Assert-NoReparsePath([string]$candidate) {
    $cursor = $candidate
    while ($cursor.StartsWith($projectRoot, [StringComparison]::OrdinalIgnoreCase)) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing reparse point in destination path: $cursor"
            }
        }
        if ([string]::Equals($cursor.TrimEnd('\', '/'), $projectRoot, [StringComparison]::OrdinalIgnoreCase)) { break }
        $parent = Split-Path -Parent $cursor
        if (!$parent -or $parent -eq $cursor) { break }
        $cursor = $parent
    }
}
$new = [Collections.Generic.List[object]]::new()
$replace = [Collections.Generic.List[object]]::new()
$matching = [Collections.Generic.List[string]]::new()
$conflicts = [Collections.Generic.List[object]]::new()
foreach ($entry in $manifest.files) {
    $relative = [string]$entry.path
    if ($relative -notmatch '^[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*$' -or $relative -match '(^|/)\.\.?(/|$)') {
        throw "Unsafe asset path in manifest: $relative"
    }
    $extension = [IO.Path]::GetExtension($relative).ToLowerInvariant()
    if ($extension -notin @('.png', '.json', '.bytes')) { throw "Refusing non-native asset: $relative" }
    $destination = [IO.Path]::GetFullPath((Join-Path $target ($relative -replace '/', '\')))
    if (!$destination.StartsWith($targetPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Asset path escapes SD data directory: $relative"
    }
    Assert-NoReparsePath $destination
    $baselineSha = [string]$entry.baselineSha256
    if ($baselineSha -and $baselineSha -notmatch '^[0-9a-fA-F]{64}$') {
        throw "Invalid baseline SHA-256 for $relative"
    }
    if (Test-Path -LiteralPath $destination -PathType Container) {
        $conflicts.Add([ordered]@{path=$relative; reason='destination is a directory'})
    } elseif (Test-Path -LiteralPath $destination) {
        $item = Get-Item -LiteralPath $destination -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            $conflicts.Add([ordered]@{path=$relative; reason='destination is a reparse point'})
        } else {
            $actual = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($actual -eq ([string]$entry.sha256).ToLowerInvariant()) {
                $matching.Add($relative)
            } elseif ($baselineSha -and $actual -eq $baselineSha.ToLowerInvariant()) {
                $replace.Add($entry)
            } else {
                $conflicts.Add([ordered]@{path=$relative; reason=('hash mismatch: '+$actual)})
            }
        }
    } else {
        $new.Add($entry)
    }
}
$projectDrive = (Split-Path -Qualifier $project).TrimEnd(':')
$projectDisk = Get-PSDrive -Name $projectDrive
$stageDisk = Get-PSDrive -Name 'E'
$result = [ordered]@{
    target=$target
    projectDriveFreeBytes=[int64]$projectDisk.Free
    stageDriveFreeBytes=[int64]$stageDisk.Free
    new=@($new.ToArray())
    replace=@($replace.ToArray())
    matching=@($matching.ToArray())
    conflicts=@($conflicts.ToArray())
}
[Console]::Out.WriteLine((ConvertTo-Json -InputObject $result -Depth 6 -Compress))
'''


REMOTE_CREATE_STAGE_BODY = r'''
$stageRoot = 'E:\tmp\vrc-diffusion'
$stage = Join-Path $stageRoot ('sd-data-deploy-' + [guid]::NewGuid().ToString('N'))
if (!(Test-Path -LiteralPath $stageRoot -PathType Container)) {
    New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null
}
$rootItem = Get-Item -LiteralPath $stageRoot -Force
if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'The E-drive deployment staging root is a reparse point.'
}
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$result = [ordered]@{stage=$stage; freeBytes=[int64](Get-PSDrive -Name 'E').Free}
[Console]::Out.WriteLine((ConvertTo-Json -InputObject $result -Compress))
'''


REMOTE_APPLY_BODY = r'''
$stage = '__STAGE__'
$stageRoot = 'E:\tmp\vrc-diffusion\'
if (!$stage.StartsWith($stageRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe E-drive staging path.' }
$target = [IO.Path]::GetFullPath((Join-Path $project 'Assets\DiffusionLab\Data\SD'))
$targetRoot = $target.TrimEnd('\', '/')
$targetPrefix = $targetRoot + '\'
$archive = Join-Path $stage 'data.tar'
$payload = Join-Path $stage 'payload'
if (!(Test-Path -LiteralPath $archive -PathType Leaf)) { throw 'Transfer archive is missing.' }
$expectedPaths = @($manifest.files | ForEach-Object { [string]$_.path } | Sort-Object)
$archivePaths = @(& tar.exe -tf $archive)
if ($LASTEXITCODE -ne 0) { throw 'Cannot list transfer archive.' }
$archivePaths = @($archivePaths | ForEach-Object { ([string]$_).Trim() } | Sort-Object)
$difference = @(Compare-Object -ReferenceObject $expectedPaths -DifferenceObject $archivePaths)
if ($difference.Count -ne 0) { throw 'Transfer archive paths differ from the allowlisted manifest.' }
New-Item -ItemType Directory -Path $payload -Force | Out-Null
& tar.exe -xf $archive -C $payload
if ($LASTEXITCODE -ne 0) { throw 'Could not extract transfer archive into E-drive staging.' }
function Assert-NoReparsePath([string]$candidate) {
    $cursor = $candidate
    while ($cursor.StartsWith($project, [StringComparison]::OrdinalIgnoreCase)) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing reparse point in destination path: $cursor"
            }
        }
        if ([string]::Equals($cursor.TrimEnd('\', '/'), $project.TrimEnd('\', '/'), [StringComparison]::OrdinalIgnoreCase)) { break }
        $parent = Split-Path -Parent $cursor
        if (!$parent -or $parent -eq $cursor) { break }
        $cursor = $parent
    }
}
$created = [Collections.Generic.List[string]]::new()
$replaced = [Collections.Generic.List[string]]::new()
$matching = [Collections.Generic.List[string]]::new()
foreach ($entry in $manifest.files) {
    $relative = [string]$entry.path
    if ($relative -notmatch '^[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*$' -or $relative -match '(^|/)\.\.?(/|$)') {
        throw "Unsafe asset path in manifest: $relative"
    }
    $sourcePath = [IO.Path]::GetFullPath((Join-Path $payload ($relative -replace '/', '\')))
    $payloadPrefix = [IO.Path]::GetFullPath($payload).TrimEnd('\') + '\'
    if (!$sourcePath.StartsWith($payloadPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Tar path escapes staging: $relative" }
    if (!(Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw "Staged source is missing: $relative" }
    $sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($sourceHash -ne ([string]$entry.sha256).ToLowerInvariant()) { throw "Staged source hash mismatch: $relative" }
    $destination = [IO.Path]::GetFullPath((Join-Path $target ($relative -replace '/', '\')))
    if (!$destination.StartsWith($targetPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Destination escapes SD folder: $relative" }
    Assert-NoReparsePath $destination
    $baselineSha = [string]$entry.baselineSha256
    if ($baselineSha -and $baselineSha -notmatch '^[0-9a-fA-F]{64}$') { throw "Invalid baseline SHA-256 for $relative" }
    if (Test-Path -LiteralPath $destination -PathType Container) { throw "Destination became a directory: $relative" }
    $replaceExisting = $false
    if (Test-Path -LiteralPath $destination) {
        $existingHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($existingHash -eq $sourceHash) { $matching.Add($relative); continue }
        if ($baselineSha -and $existingHash -eq $baselineSha.ToLowerInvariant()) {
            $replaceExisting = $true
        } else {
            throw "Destination changed after preflight; refusing overwrite: $relative ($existingHash)"
        }
    }
    $parent = Split-Path -Parent $destination
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    Assert-NoReparsePath $parent
    $temp = $destination + '.sddeploy-' + [guid]::NewGuid().ToString('N') + '.tmp'
    $backup = $destination + '.sddeploy-' + [guid]::NewGuid().ToString('N') + '.backup'
    try {
        Copy-Item -LiteralPath $sourcePath -Destination $temp
        $tempHash = (Get-FileHash -LiteralPath $temp -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($tempHash -ne $sourceHash) { throw "Temporary destination hash mismatch: $relative" }
        if (Test-Path -LiteralPath $destination) {
            $currentHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($currentHash -eq $sourceHash) {
                $matching.Add($relative)
            } elseif ($replaceExisting -and $currentHash -eq $baselineSha.ToLowerInvariant()) {
                [IO.File]::Replace($temp, $destination, $backup)
                $replaced.Add($relative)
                if (Test-Path -LiteralPath $backup) { [IO.File]::Delete($backup) }
            } else {
                throw "Destination changed during copy; refusing overwrite: $relative ($currentHash)"
            }
        } else {
            if ($replaceExisting) { throw "Baseline destination disappeared during copy: $relative" }
            [IO.File]::Move($temp, $destination)
            $created.Add($relative)
        }
    } catch {
        if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force }
        if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Force }
        throw
    }
}
$result = [ordered]@{
    created=@($created.ToArray())
    replaced=@($replaced.ToArray())
    alreadyMatching=@($matching.ToArray())
}
[Console]::Out.WriteLine((ConvertTo-Json -InputObject $result -Depth 4 -Compress))
'''


REMOTE_VERIFY_BODY = r'''
$target = [IO.Path]::GetFullPath((Join-Path $project 'Assets\DiffusionLab\Data\SD'))
$missing = [Collections.Generic.List[string]]::new()
$mismatch = [Collections.Generic.List[object]]::new()
foreach ($entry in $manifest.files) {
    $relative = [string]$entry.path
    $path = [IO.Path]::GetFullPath((Join-Path $target ($relative -replace '/', '\')))
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { $missing.Add($relative); continue }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne ([string]$entry.sha256).ToLowerInvariant()) {
        $mismatch.Add([ordered]@{path=$relative; actual=$actual; expected=$entry.sha256})
    }
}
$result = [ordered]@{target=$target; missing=@($missing.ToArray()); mismatches=@($mismatch.ToArray())}
[Console]::Out.WriteLine((ConvertTo-Json -InputObject $result -Depth 5 -Compress))
'''


REMOTE_DEPLOY_SCRIPT = r'''param(
    [Parameter(Mandatory=$true)]
    [ValidateSet('Preflight','Apply','Verify')]
    [string]$Mode,
    [string]$ManifestName = 'manifest.json'
)
$ErrorActionPreference = 'Stop'
if ($ManifestName -notmatch '^[A-Za-z0-9_.-]+\.json$') { throw 'Invalid manifest filename.' }
$manifestPath = Join-Path $PSScriptRoot $ManifestName
if (!(Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Deployment manifest is missing.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
$project = [IO.Path]::GetFullPath([string]$manifest.projectPath)
switch ($Mode) {
    'Preflight' {
<<PREFLIGHT_BODY>>
    }
    'Apply' {
<<APPLY_BODY>>
    }
    'Verify' {
<<VERIFY_BODY>>
    }
}
'''


def render_remote_deploy_script() -> str:
    return (
        REMOTE_DEPLOY_SCRIPT.replace("<<PREFLIGHT_BODY>>", REMOTE_PREFLIGHT_BODY)
        .replace(
            "<<APPLY_BODY>>",
            REMOTE_APPLY_BODY.replace("$stage = '__STAGE__'", "$stage = $PSScriptRoot"),
        )
        .replace("<<VERIFY_BODY>>", REMOTE_VERIFY_BODY)
    )


def remote_manifest(entries: list[dict[str, Any]], project_path: str) -> dict[str, Any]:
    files = []
    for entry in entries:
        file_record = {
            "path": entry["path"],
            "sizeBytes": entry["sizeBytes"],
            "sha256": entry["sha256"],
        }
        baseline_sha = entry.get("baselineSha256")
        if baseline_sha is not None:
            if not isinstance(baseline_sha, str) or not SHA256.fullmatch(baseline_sha):
                raise DeployError(f"Invalid baseline SHA-256 for {entry['path']}")
            file_record["baselineSha256"] = baseline_sha
        files.append(file_record)
    return {
        "projectPath": project_path,
        "files": files,
        "totalBytes": sum(entry["sizeBytes"] for entry in entries),
    }


def create_remote_stage(host: str) -> dict[str, Any]:
    result = run_remote_encoded(host, REMOTE_CREATE_STAGE_BODY)
    if not isinstance(result, dict) or not isinstance(result.get("stage"), str):
        raise DeployError("Could not create the E-drive transfer staging directory")
    stage = result["stage"]
    if not stage.lower().startswith((REMOTE_STAGE_ROOT + "\\").lower()):
        raise DeployError(f"Remote staging path did not use E:\\tmp: {stage}")
    return result


def prepare_remote_stage(host: str, stage: str, manifest: dict[str, Any], temp_dir: Path) -> None:
    script_path = temp_dir / "deploy_sd_data.ps1"
    manifest_path = temp_dir / "manifest.json"
    script_path.write_text(render_remote_deploy_script(), encoding="utf-8")
    manifest_path.write_text(json.dumps(manifest, separators=(",", ":")), encoding="utf-8")
    scp_to_remote(host, script_path, stage + r"\deploy_sd_data.ps1")
    scp_to_remote(host, manifest_path, stage + r"\manifest.json")


def run_remote_stage_mode(
    host: str,
    stage: str,
    mode: str,
    manifest_name: str = "manifest.json",
    timeout: int = 180,
) -> dict[str, Any]:
    script_path = (PureWindowsPath(stage) / "deploy_sd_data.ps1").as_posix()
    script_literal = "'" + script_path.replace("'", "''") + "'"
    mode_literal = "'" + mode.replace("'", "''") + "'"
    manifest_literal = "'" + manifest_name.replace("'", "''") + "'"
    invocation = (
        "$ErrorActionPreference='Stop'; "
        f"try {{ & {script_literal} -Mode {mode_literal} -ManifestName {manifest_literal} }} "
        "catch { [Console]::Error.WriteLine($_.ToString()); exit 1 }"
    )
    result = run_remote_encoded(host, invocation, timeout=timeout)
    if not isinstance(result, dict):
        raise DeployError(f"Remote {mode} returned an unexpected payload")
    return result


def run_remote_stage_mode(
    host: str,
    stage: str,
    mode: str,
    manifest_name: str = "manifest.json",
    timeout: int = 180,
) -> dict[str, Any]:
    script_path = (PureWindowsPath(stage) / "deploy_sd_data.ps1").as_posix()
    script_literal = "'" + script_path.replace("'", "''") + "'"
    mode_literal = "'" + mode.replace("'", "''") + "'"
    manifest_literal = "'" + manifest_name.replace("'", "''") + "'"
    invocation = (
        "$ErrorActionPreference='Stop'; "
        f"try {{ & {script_literal} -Mode {mode_literal} -ManifestName {manifest_literal} }} "
        "catch { [Console]::Error.WriteLine($_.ToString()); exit 1 }"
    )
    result = run_remote_encoded(host, invocation, timeout=timeout)
    if not isinstance(result, dict):
        raise DeployError(f"Remote {mode} returned an unexpected payload")
    return result


def cleanup_remote_stage(host: str, stage: str) -> None:
    stage_literal = "'" + stage.replace("'", "''") + "'"
    script = rf"""
$ErrorActionPreference = 'Stop'
$stage = [IO.Path]::GetFullPath({stage_literal})
$prefix = [IO.Path]::GetFullPath('E:\tmp\vrc-diffusion').TrimEnd('\') + '\'
if (!$stage.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {{ throw 'Refusing to remove an unsafe staging path.' }}
if (Test-Path -LiteralPath $stage) {{ Remove-Item -LiteralPath $stage -Recurse -Force }}
[Console]::Out.WriteLine((ConvertTo-Json -InputObject ([ordered]@{{removed=$true}}) -Compress))
"""
    run_remote_encoded(host, script)


def copy_local_new(
    project: Path, target: Path, entries: list[dict[str, Any]]
) -> dict[str, list[str]]:
    copied: list[str] = []
    replaced: list[str] = []
    for entry in entries:
        relative = PurePosixPath(entry["path"])
        destination = target.joinpath(*relative.parts)
        assert_local_destination_safe(project, destination)
        destination.parent.mkdir(parents=True, exist_ok=True)
        assert_local_destination_safe(project, destination)
        baseline_sha = entry.get("baselineSha256")
        replace_existing = False
        if destination.exists():
            if not destination.is_file() or destination.is_symlink():
                raise DeployError(f"Local destination appeared or changed; refusing overwrite: {destination}")
            actual = sha256_file(destination)
            if actual == entry["sha256"]:
                continue
            if baseline_sha is not None and actual == baseline_sha:
                replace_existing = True
            else:
                raise DeployError(f"Local destination appeared or changed; refusing overwrite: {destination} ({actual})")
        elif baseline_sha is not None:
            raise DeployError(f"Local baseline destination disappeared after preflight: {destination}")
        temp = destination.with_name(destination.name + ".sddeploy-" + uuid.uuid4().hex + ".tmp")
        try:
            shutil.copyfile(entry["source"], temp)
            if sha256_file(temp) != entry["sha256"]:
                raise DeployError(f"Local staged copy failed hash verification: {relative}")
            if replace_existing:
                if not destination.is_file() or destination.is_symlink():
                    raise DeployError(f"Local replacement destination changed type: {destination}")
                current = sha256_file(destination)
                if current == entry["sha256"]:
                    continue
                if current != baseline_sha:
                    raise DeployError(f"Local baseline changed during copy; refusing overwrite: {destination} ({current})")
                os.replace(temp, destination)
                if sha256_file(destination) != entry["sha256"]:
                    raise DeployError(f"Local replacement hash verification failed: {relative}")
                replaced.append(entry["path"])
                continue
            if destination.exists():
                if destination.is_file() and not destination.is_symlink() and sha256_file(destination) == entry["sha256"]:
                    continue
                raise DeployError(f"Local destination appeared during copy; refusing overwrite: {destination}")
            try:
                os.link(temp, destination)
            except FileExistsError:
                if destination.is_file() and sha256_file(destination) == entry["sha256"]:
                    continue
                raise DeployError(f"Local destination appeared during copy; refusing overwrite: {destination}")
            copied.append(entry["path"])
        finally:
            if temp.exists():
                temp.unlink()
    return {"created": copied, "replaced": replaced}


def assert_local_destination_safe(project: Path, destination: Path) -> None:
    project_abs = project.absolute()
    if project_abs not in destination.absolute().parents:
        raise DeployError(f"Local destination escapes project: {destination}")
    cursor = destination.absolute()
    while cursor == project_abs or project_abs in cursor.parents:
        if cursor.exists() and cursor.is_symlink():
            raise DeployError(f"Refusing local reparse/symlink path: {cursor}")
        if cursor == project_abs:
            break
        cursor = cursor.parent


def make_tar(entries: list[dict[str, Any]], archive: Path) -> None:
    with tarfile.open(archive, mode="w", format=tarfile.PAX_FORMAT) as tar:
        for entry in entries:
            if sha256_file(entry["source"]) != entry["sha256"]:
                raise DeployError(f"Source changed after preflight: {entry['source']}")
            info = tar.gettarinfo(str(entry["source"]), arcname=entry["path"])
            if not info.isfile():
                raise DeployError(f"Tar source is not a regular file: {entry['source']}")
            with entry["source"].open("rb") as stream:
                tar.addfile(info, stream)


def scp_to_remote(host: str, source: Path, remote_path: str) -> None:
    destination = host + ":" + PureWindowsPath(remote_path).as_posix()
    subprocess.run(
        [
            "scp",
            "-q",
            "-o",
            "BatchMode=yes",
            "-o",
            "LogLevel=ERROR",
            str(source),
            destination,
        ],
        check=True,
        timeout=1200,
    )


def print_json(value: dict[str, Any]) -> None:
    print(json.dumps(value, ensure_ascii=False, indent=2, default=str))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=DEFAULT_SOURCE)
    parser.add_argument("--conditions", type=Path, default=DEFAULT_CONDITIONS)
    parser.add_argument("--baseline-source", type=Path, default=DEFAULT_SOURCE)
    parser.add_argument("--baseline-conditions", type=Path, default=DEFAULT_CONDITIONS)
    parser.add_argument("--local-project", type=Path, default=ROOT)
    parser.add_argument("--project-path", default=DEFAULT_PROJECT)
    parser.add_argument("--host", default=DEFAULT_HOST)
    parser.add_argument("--verify-only", action="store_true")
    parser.add_argument("--replace-baseline-source", action="store_true")
    parser.add_argument("--replace-baseline-conditions", action="store_true")
    args = parser.parse_args()
    if not re.fullmatch(r"[A-Za-z0-9_.@-]+", args.host):
        parser.error("--host contains unsupported SSH characters")
    project_windows = validate_remote_project(args.project_path)
    local_project = args.local_project.absolute()

    try:
        model_files, model_info = collect_model_files(args.source)
        condition_files, condition_info = collect_conditions_files(args.conditions)
        if model_info["revision"] != condition_info["revision"]:
            raise DeployError("Model and condition data refer to different revisions")
        if model_info["revision"] == FIX_REVISION and (
            model_info["checkpointSHA256"] != condition_info["checkpointSHA256"]
        ):
            raise DeployError("Model and condition data refer to different fix checkpoints")

        baseline_hashes: dict[str, str] = {}
        baseline_model_info: dict[str, Any] | None = None
        baseline_condition_info: dict[str, Any] | None = None
        if args.replace_baseline_source:
            if model_info["revision"] != FIX_REVISION:
                raise DeployError("--replace-baseline-source is only valid for the fixed checkpoint revision")
            baseline_model_files, baseline_model_info = collect_model_files(args.baseline_source)
            if baseline_model_info["revision"] not in {BASELINE_REVISION, FIX_REVISION}:
                raise DeployError("The source replacement baseline has an unsupported model revision")
            baseline_hashes.update(
                {entry["path"]: entry["sha256"] for entry in baseline_model_files}
            )
        if args.replace_baseline_conditions:
            if condition_info["revision"] != FIX_REVISION:
                raise DeployError("--replace-baseline-conditions is only valid for fixed checkpoint conditions")
            baseline_condition_files, baseline_condition_info = collect_conditions_files(
                args.baseline_conditions
            )
            if baseline_condition_info["revision"] not in {BASELINE_REVISION, FIX_REVISION}:
                raise DeployError("The condition replacement baseline has an unsupported model revision")
            baseline_hashes.update(
                {entry["path"]: entry["sha256"] for entry in baseline_condition_files}
            )
        if baseline_model_info is not None and baseline_condition_info is not None:
            if baseline_model_info["revision"] != baseline_condition_info["revision"]:
                raise DeployError("Model and condition replacement baselines refer to different revisions")
            if baseline_model_info["checkpointSHA256"] != baseline_condition_info["checkpointSHA256"]:
                raise DeployError("Model and condition replacement baselines refer to different checkpoints")

        entries = combine_entries(model_files, condition_files, baseline_hashes)
        manifest = remote_manifest(entries, str(project_windows))
        local_plan = local_preflight(local_project, entries)
        if local_plan["conflicts"]:
            print_json(
                {
                    "verifiedSource": True,
                    "deployed": False,
                    "conflicts": {"local": local_plan["conflicts"]},
                }
            )
            return 3
        local_target = Path(local_plan["target"])
        local_to_write = local_plan["new"] + local_plan["replace"]
        local_write_bytes = sum(x["sizeBytes"] for x in local_to_write)
        if local_to_write and shutil.disk_usage(local_project).free < local_write_bytes:
            raise DeployError("Insufficient local disk space for staged SD data copies")

        TASK_TMP.mkdir(parents=True, exist_ok=True)
        remote_stage_result: dict[str, Any] | None = create_remote_stage(args.host)
        try:
            remote_stage = remote_stage_result["stage"]
            with tempfile.TemporaryDirectory(prefix="sd-data-deploy-", dir=TASK_TMP) as temp_dir_name:
                temp_dir = Path(temp_dir_name)
                prepare_remote_stage(args.host, remote_stage, manifest, temp_dir)
                remote_plan = run_remote_stage_mode(args.host, remote_stage, "Preflight")
                if remote_plan.get("conflicts"):
                    print_json(
                        {
                            "verifiedSource": True,
                            "deployed": False,
                            "conflicts": {"remote": remote_plan["conflicts"]},
                            "remoteTarget": remote_plan.get("target"),
                        }
                    )
                    return 3

                remote_new_paths = {str(item["path"]) for item in remote_plan.get("new", [])}
                remote_replace_records = {
                    str(item["path"]): str(item.get("baselineSha256", ""))
                    for item in remote_plan.get("replace", [])
                }
                remote_write_paths = remote_new_paths | set(remote_replace_records)
                remote_to_write = []
                for entry in entries:
                    if entry["path"] not in remote_write_paths:
                        continue
                    transfer_entry = dict(entry)
                    if entry["path"] in remote_replace_records:
                        transfer_entry["baselineSha256"] = remote_replace_records[entry["path"]]
                    else:
                        transfer_entry.pop("baselineSha256", None)
                    remote_to_write.append(transfer_entry)
                remote_write_bytes = sum(entry["sizeBytes"] for entry in remote_to_write)
                if remote_to_write and remote_plan.get("projectDriveFreeBytes", 0) < remote_write_bytes:
                    raise DeployError("Insufficient free space on the remote project drive")
                # The E: staging volume holds both data.tar and its extracted payload.
                if remote_to_write and remote_plan.get("stageDriveFreeBytes", 0) < (2 * remote_write_bytes + 1024 * 1024):
                    raise DeployError("Insufficient free space on the E-drive staging volume")

                summary = {
                    "verifiedSource": True,
                    "model": model_info,
                    "replacementFlags": {
                        "source": args.replace_baseline_source,
                        "conditions": args.replace_baseline_conditions,
                    },
                    "sourceFileCount": len(entries),
                    "sourceBytes": manifest["totalBytes"],
                    "sourceExtensions": {
                        ext: sum(Path(entry["path"]).suffix.lower() == ext for entry in entries)
                        for ext in sorted(NATIVE_EXTENSIONS)
                    },
                    "localTarget": str(local_target),
                    "remoteTarget": remote_plan.get("target"),
                    "newLocalFiles": len(local_plan["new"]),
                    "replaceLocalFiles": len(local_plan["replace"]),
                    "matchingLocalFiles": len(local_plan["matching"]),
                    "newRemoteFiles": len(remote_plan.get("new", [])),
                    "replaceRemoteFiles": len(remote_plan.get("replace", [])),
                    "matchingRemoteFiles": len(remote_plan.get("matching", [])),
                    "remoteStagingDrive": "E:",
                    "unityInvoked": False,
                    "assetDatabaseRefreshed": False,
                }
                if args.verify_only:
                    summary["deployed"] = False
                    summary["localComplete"] = not local_to_write
                    summary["remoteComplete"] = not remote_to_write
                    summary["temporaryRemoteStageUsed"] = True
                    print_json(summary)
                    return 0

                if remote_to_write:
                    transfer_manifest = remote_manifest(remote_to_write, str(project_windows))
                    transfer_manifest_path = temp_dir / "transfer-manifest.json"
                    transfer_manifest_path.write_text(
                        json.dumps(transfer_manifest, separators=(",", ":")), encoding="utf-8"
                    )
                    scp_to_remote(
                        args.host, transfer_manifest_path, remote_stage + r"\transfer-manifest.json"
                    )
                    archive = Path(temp_dir) / "data.tar"
                    print(
                        f"Packing {len(remote_to_write)} native files ({remote_write_bytes} bytes) for E-drive staging...",
                        flush=True,
                    )
                    make_tar(remote_to_write, archive)
                    scp_to_remote(args.host, archive, remote_stage + r"\data.tar")
                    applied = run_remote_stage_mode(
                        args.host, remote_stage, "Apply", "transfer-manifest.json", timeout=900
                    )
                    summary["remoteCopy"] = {
                        "createdCount": len(applied.get("created", [])),
                        "replacedCount": len(applied.get("replaced", [])),
                        "alreadyMatchingCount": len(applied.get("alreadyMatching", [])),
                    }

                remote_verified = run_remote_stage_mode(
                    args.host, remote_stage, "Verify", timeout=900
                )
                if remote_verified.get("missing") or remote_verified.get("mismatches"):
                    raise DeployError(
                        "Remote destination verification failed after copy: "
                        + json.dumps(remote_verified, ensure_ascii=False)
                    )

                local_copy = copy_local_new(local_project, local_target, local_to_write)
                local_verified = local_preflight(local_project, entries)
                if local_verified["new"] or local_verified["replace"] or local_verified["conflicts"]:
                    raise DeployError("Local destination verification failed after copy")
                summary.update(
                    {
                        "deployed": True,
                        "copiedLocalFiles": len(local_copy["created"]),
                        "replacedLocalFiles": len(local_copy["replaced"]),
                        "remoteHashVerified": True,
                        "localHashVerified": True,
                        "remoteVerification": remote_verified,
                        "localVerification": {
                            "fileCount": len(entries),
                            "matchingFileCount": len(local_verified["matching"]),
                        },
                    }
                )
                print_json(summary)
                return 0
        finally:
            try:
                cleanup_remote_stage(args.host, remote_stage_result["stage"])
            except Exception as cleanup_error:
                print(f"Warning: remote E-drive staging cleanup failed: {cleanup_error}", file=sys.stderr)
    except (DeployError, OSError, subprocess.SubprocessError, json.JSONDecodeError) as exc:
        print_json({"verifiedSource": False, "deployed": False, "error": str(exc)})
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
