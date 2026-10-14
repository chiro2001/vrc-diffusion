#!/usr/bin/env python3
"""Upload the allowlisted plain-text sources to the Windows Unity project."""

import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath, PureWindowsPath
import re
import subprocess
import tempfile
import uuid
from collections import defaultdict

from unity_project_common import checked_pwsh, pipeline
from unity_remote import DEFAULT_HOST, DEFAULT_PROJECT, pwsh_literal

ROOT = Path(__file__).resolve().parents[1]
TASK_TMP = Path.home() / "tmp/vrc-diffusion"
DATA_ROOT = Path("Assets/DiffusionLab/Data")
ALLOWED_EXTENSIONS = {".md", ".cs", ".shader", ".json", ".bytes", ".py", ".ttf", ".txt"}
DATA_EXTENSIONS = {".json", ".bytes"}
ALLOWED_FILENAMES = {".gitignore"}
FORBIDDEN_EXTENSIONS = {
    ".meta", ".asset", ".mat", ".unity", ".prefab", ".controller",
    ".anim", ".overridecontroller", ".mask", ".playable", ".terrainlayer",
    ".physicmaterial", ".rendertexture", ".lighting", ".guiskin", ".flare",
    ".cubemap", ".fontsettings", ".giparams", ".brush", ".mixer",
}
NAME_PART = re.compile(r"^[A-Za-z0-9_.-]+$")


def relative_files(patterns):
    files = []
    for pattern in patterns:
        for path in sorted(ROOT.glob(pattern)):
            if path.is_symlink():
                raise ValueError(f"Refusing symlink source: {path.relative_to(ROOT)}")
            if path.is_file():
                files.append(path.relative_to(ROOT).as_posix())
    return files


def collect_sources(include_data: bool):
    patterns = [
        "Assets/DiffusionLab/Scripts/*.cs",
        "Assets/DiffusionLab/Shaders/*.shader",
        "Assets/DiffusionLab/Localization/catalog.json",
        "Assets/DiffusionLab/Localization/teaching.json",
        "Assets/DiffusionLab/Localization/FontSources/*.ttf",
        "Assets/DiffusionLab/Localization/FontSources/*-OFL.txt",
        "AgentScripts/*.cs",
        "ml/*.py",
        "docs/*.md",
        "tools/*.py",
        ".agents/skills/**/*.md",
    ]
    required = ["README.md", "AGENTS.md", "UNITY-GUIDE.md", ".gitignore"]
    missing = [path for path in required if not (ROOT / path).is_file()]
    if missing:
        raise FileNotFoundError("Required project text files are missing: " + ", ".join(missing))

    files = relative_files(patterns) + required
    if include_data:
        data_dir = ROOT / DATA_ROOT
        if data_dir.exists():
            for path in sorted(data_dir.rglob("*")):
                if path.is_symlink():
                    raise ValueError(f"Refusing symlink model data: {path.relative_to(ROOT)}")
                if not path.is_file():
                    continue
                suffix = path.suffix.lower()
                if suffix == ".meta":
                    continue  # Unity-generated GUIDs stay in place; only native model sources are uploaded.
                if suffix in FORBIDDEN_EXTENSIONS:
                    raise ValueError(f"Refusing serialized or metadata file: {path.relative_to(ROOT)}")
                if suffix not in DATA_EXTENSIONS:
                    raise ValueError(
                        f"Data deployment accepts only native model .bytes/.json files: {path.relative_to(ROOT)}"
                    )
                if suffix == ".json":
                    json.loads(path.read_text(encoding="utf-8"))
                elif path.stat().st_size == 0:
                    raise ValueError(f"Model byte file is empty: {path.relative_to(ROOT)}")
                files.append(path.relative_to(ROOT).as_posix())

    files = sorted(set(files))
    for relative in files:
        path = PurePosixPath(relative)
        if path.is_absolute() or ".." in path.parts or not path.parts:
            raise ValueError(f"Unsafe source path: {relative}")
        if not all(NAME_PART.fullmatch(part) for part in path.parts):
            raise ValueError(f"Source path contains unsupported characters: {relative}")
        suffix = Path(relative).suffix.lower()
        if suffix in {".ttf", ".txt"} and not relative.startswith("Assets/DiffusionLab/Localization/FontSources/"):
            raise ValueError(f"Font sources must be under Localization/FontSources: {relative}")
        if suffix in FORBIDDEN_EXTENSIONS:
            raise ValueError(f"Refusing serialized or metadata file: {relative}")
        if suffix not in ALLOWED_EXTENSIONS and relative not in ALLOWED_FILENAMES:
            raise ValueError(f"Refusing non-text source type: {relative}")
        if relative.startswith("Assets/DiffusionLab/Data/"):
            if not include_data or suffix not in DATA_EXTENSIONS:
                raise ValueError(f"Model data requires --data and must be .bytes/.json: {relative}")
        elif suffix in DATA_EXTENSIONS and relative.endswith(".bytes"):
            raise ValueError(f"Native model data is only allowed under {DATA_ROOT}: {relative}")
        source = ROOT / relative
        if any((ROOT.joinpath(*path.parts[:index])).is_symlink() for index in range(1, len(path.parts) + 1)):
            raise ValueError(f"Refusing symlink source path: {relative}")
        if not source.is_file():
            raise FileNotFoundError(f"Source disappeared during enumeration: {relative}")
    if not files:
        raise RuntimeError("No allowlisted source files were found.")
    return files


def remote_prepare_script(project_path: str, files: list[str], expected: dict[str, str]) -> str:
    windows_project = PureWindowsPath(project_path)
    if not windows_project.is_absolute() or ".." in windows_project.parts:
        raise ValueError("--project-path must be an absolute Windows path without '..'.")
    if not re.fullmatch(r"[A-Za-z]:\\(?:[A-Za-z0-9_.-]+\\)*[A-Za-z0-9_.-]+", str(windows_project)):
        raise ValueError("--project-path contains characters unsupported by the scp destination syntax.")
    directories = sorted(
        {
            parent.as_posix()
            for parent in (PurePosixPath(path).parent for path in files)
            if parent != PurePosixPath(".")
        }
    )
    paths_literal = ", ".join(pwsh_literal(path) for path in files)
    dirs_literal = ", ".join(pwsh_literal(path) for path in directories)
    expected_literal = pwsh_literal(json.dumps(expected, ensure_ascii=True, separators=(",", ":")))
    return f"""
param([switch]$Verify)
$project = [IO.Path]::GetFullPath({pwsh_literal(str(windows_project))})
if (!(Test-Path -LiteralPath $project -PathType Container)) {{ throw 'Target Unity project does not exist.' }}
$root = $project.TrimEnd('\\', '/')
$prefix = $root + '\\'
$files = @({paths_literal})
$directories = @({dirs_literal})
$expected = ConvertFrom-Json -AsHashtable {expected_literal}
function Get-ProjectPath([string]$relative) {{
    if ([IO.Path]::IsPathRooted($relative) -or $relative -match '(^|[\\\\/])\\.{{1,2}}([\\\\/]|$)') {{ throw "Unsafe destination path: $relative" }}
    $full = [IO.Path]::GetFullPath((Join-Path $root ($relative -replace '/', '\\')))
    if (!$full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {{ throw "Destination escapes Unity project: $relative" }}
    return $full
}}
if ($Verify) {{
    foreach ($relative in $expected.Keys) {{
        $target = Get-ProjectPath $relative
        if (!(Test-Path -LiteralPath $target -PathType Leaf)) {{ throw "Uploaded source is missing: $relative" }}
        $actual = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $expected[$relative]) {{ throw "Uploaded source hash differs: $relative" }}
    }}
    [pscustomobject]@{{ verified=$true; fileCount=$expected.Count }} | ConvertTo-Json
    exit 0
}}
foreach ($relative in $files) {{
    $extension = [IO.Path]::GetExtension($relative).ToLowerInvariant()
    if ($extension -in @('.meta', '.asset', '.mat', '.unity', '.prefab', '.controller', '.anim', '.overridecontroller', '.mask', '.playable', '.terrainlayer', '.physicmaterial', '.rendertexture', '.lighting', '.guiskin', '.flare', '.cubemap', '.fontsettings', '.giparams', '.brush', '.mixer')) {{ throw "Refusing metadata or serialized asset: $relative" }}
    if ($extension -notin @('.md', '.cs', '.shader', '.json', '.bytes', '.py', '.ttf', '.txt') -and [IO.Path]::GetFileName($relative) -ne '.gitignore') {{ throw "Refusing unsupported source type: $relative" }}
    $target = Get-ProjectPath $relative
    if (Test-Path -LiteralPath $target -PathType Container) {{ throw "Destination is a directory: $relative" }}
    if (Test-Path -LiteralPath $target) {{
        $targetItem = Get-Item -LiteralPath $target -Force
        if (($targetItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {{ throw "Destination uses a reparse point: $relative" }}
    }}
    $cursor = Split-Path -Parent $target
    while ($cursor.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {{
        if (Test-Path -LiteralPath $cursor) {{
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {{ throw "Destination uses a reparse point: $relative" }}
        }}
        $parent = Split-Path -Parent $cursor
        if ($parent -eq $cursor) {{ break }}
        $cursor = $parent
    }}
}}
foreach ($relative in $directories) {{
    $directory = Get-ProjectPath $relative
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}}
Write-Output 'Target directories prepared; only allowlisted plain-text paths passed preflight.'
"""


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default=DEFAULT_HOST)
    parser.add_argument("--project-path", default=DEFAULT_PROJECT)
    parser.add_argument(
        "--data",
        action="store_true",
        help="Include only Assets/DiffusionLab/Data/**/*.bytes and valid .json model data.",
    )
    parser.add_argument(
        "--no-refresh",
        action="store_true",
        help="Do not call Unity AssetDatabase.Refresh after copying sources.",
    )
    options = parser.parse_args()
    if not re.fullmatch(r"[A-Za-z0-9_.@-]+", options.host):
        parser.error("--host contains unsupported SSH host characters.")
    files = collect_sources(options.data)
    expected = {
        relative: hashlib.sha256((ROOT / relative).read_bytes()).hexdigest()
        for relative in files
    }

    # The source allowlist excludes all .meta and serialized assets. Upload the short
    # PowerShell launcher to task temp so SSH never has to pass a long encoded command.
    TASK_TMP.mkdir(parents=True, exist_ok=True)
    name = "source-deploy-" + uuid.uuid4().hex
    remote_stage = checked_pwsh(
        "$taskRoot = Join-Path ([Environment]::GetFolderPath('UserProfile')) 'tmp\\vrc-diffusion'; "
        "New-Item -ItemType Directory -Path $taskRoot -Force | Out-Null; "
        f"$stage = Join-Path $taskRoot {pwsh_literal(name)}; "
        "New-Item -ItemType Directory -Path $stage -Force | Out-Null; Write-Output $stage",
        options.host,
    )
    try:
        script = remote_prepare_script(options.project_path, files, expected)
        with tempfile.TemporaryDirectory(prefix="source-deploy-", dir=TASK_TMP) as temporary:
            script_path = Path(temporary) / "deploy.ps1"
            script_path.write_text(script, encoding="utf-8")
            remote_script = remote_stage.replace("\\", "/") + "/deploy.ps1"
            subprocess.run(
                [
                    "scp", "-q", "-o", "BatchMode=yes", "-o", "LogLevel=ERROR",
                    str(script_path), options.host + ":" + remote_script,
                ],
                check=True,
            )
        checked_pwsh("& " + pwsh_literal(remote_script), options.host)
        by_directory = defaultdict(list)
        for relative in files:
            by_directory[PurePosixPath(relative).parent].append(relative)
        for directory, relatives in sorted(by_directory.items(), key=lambda item: item[0].as_posix()):
            target = PureWindowsPath(options.project_path, *directory.parts).as_posix() + "/"
            subprocess.run(
                [
                    "scp", "-q", "-o", "BatchMode=yes", "-o", "LogLevel=ERROR",
                    *(str(ROOT / relative) for relative in relatives),
                    options.host + ":" + target,
                ],
                check=True,
            )
        verification = checked_pwsh("& " + pwsh_literal(remote_script) + " -Verify", options.host)
    finally:
        checked_pwsh(f"Remove-Item -LiteralPath {pwsh_literal(remote_stage)} -Recurse -Force", options.host)
    if not options.no_refresh:
        state = pipeline(options.host, options.project_path, "editor_status")
        if state.get("playMode") != "stopped" or state.get("compiling"):
            raise RuntimeError("Stop Play Mode and finish compilation before refreshing imported sources.")
        pipeline(
            options.host,
            options.project_path,
            "eval",
            "UnityEditor.AssetDatabase.Refresh(); return true;",
        )

    print(
        json.dumps(
            {
                "deployed": True,
                "project": options.project_path,
                "fileCount": len(files),
                "dataIncluded": options.data,
                "refreshed": not options.no_refresh,
                "verified": True,
                "remoteVerification": verification,
                "files": files,
            },
            indent=2,
        )
    )


if __name__ == "__main__":
    main()
