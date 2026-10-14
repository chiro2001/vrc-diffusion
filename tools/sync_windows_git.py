#!/usr/bin/env python3
"""Align Windows Git metadata without rewriting Unity files; optionally preserve audited tracked edits."""

import argparse
import hashlib
import json
from pathlib import Path
from pathlib import PurePosixPath, PureWindowsPath
import re
import subprocess
import tarfile
import tempfile
import uuid

from unity_project_common import checked_pwsh, pipeline
from unity_remote import DEFAULT_HOST, DEFAULT_PROJECT, pwsh_literal

ROOT = Path(__file__).resolve().parents[1]
TASK_TMP = Path.home() / "tmp/vrc-diffusion"
UNITY_ROOTS = ("Assets/", "Packages/", "ProjectSettings/", "AgentScripts/")
SHA256_PATTERN = re.compile(r"^[0-9a-fA-F]{64}$")
GIT_HASH_PATTERN = re.compile(r"^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$")


def git(*arguments, text=False):
    return subprocess.check_output(["git", *arguments], cwd=ROOT, text=text)


def parse_preserve_manifest(data):
    if not isinstance(data, dict) or set(data) != {"baselineHead", "files"}:
        raise ValueError("Preserve manifest must contain only baselineHead and files.")

    baseline_head = data["baselineHead"]
    if not isinstance(baseline_head, str) or not GIT_HASH_PATTERN.fullmatch(baseline_head):
        raise ValueError("Preserve manifest baselineHead must be a full Git commit hash.")

    entries = data["files"]
    if not isinstance(entries, dict) or not entries:
        raise ValueError("Preserve manifest files must be a non-empty path-to-SHA256 object.")

    files = {}
    for relative, digest in entries.items():
        if (
            not isinstance(relative, str)
            or not relative
            or "\\" in relative
            or any(ord(character) < 32 or ord(character) == 127 for character in relative)
        ):
            raise ValueError(f"Invalid preserve manifest path: {relative!r}")
        posix_path = PurePosixPath(relative)
        windows_path = PureWindowsPath(relative)
        if (
            posix_path.is_absolute()
            or windows_path.is_absolute()
            or windows_path.drive
            or not posix_path.parts
            or posix_path.as_posix() != relative
            or any(part in ("", ".", "..") for part in posix_path.parts)
        ):
            raise ValueError(f"Preserve manifest path must be a normalized relative path: {relative!r}")
        if not isinstance(digest, str) or not SHA256_PATTERN.fullmatch(digest):
            raise ValueError(f"Preserve manifest SHA256 is invalid for {relative!r}.")
        files[relative] = digest.lower()

    return baseline_head.lower(), files


def git_tree_entry(revision, relative):
    output = subprocess.check_output(
        ["git", "ls-tree", "-z", revision, "--", ":(literal)" + relative],
        cwd=ROOT,
    )
    for record in output.split(b"\0"):
        if not record:
            continue
        metadata, raw_path = record.split(b"\t", 1)
        path = raw_path.decode("utf-8")
        if path != relative:
            continue
        mode, object_type, object_id = metadata.decode("ascii").split(" ", 2)
        if object_type != "blob" or mode not in ("100644", "100755"):
            raise RuntimeError(f"Preserved path is not a regular tracked file: {relative}")
        return mode, object_id

    raise RuntimeError(f"Preserved path is absent from Git tree {revision}: {relative}")


def verify_preserved_paths_unchanged(baseline_head, local_head, preserved_paths):
    for relative in sorted(preserved_paths):
        baseline_entry = git_tree_entry(baseline_head, relative)
        local_entry = git_tree_entry(local_head, relative)
        if baseline_entry != local_entry:
            raise RuntimeError(
                f"Local commit changed preserved path {relative}; "
                "the audited Windows edit cannot be preserved safely."
            )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default=DEFAULT_HOST)
    parser.add_argument("--project-path", default=DEFAULT_PROJECT)
    parser.add_argument(
        "--preserve-dirty-manifest",
        type=Path,
        help="Preserve the exact audited Windows tracked edits described by a baselineHead/files JSON manifest.",
    )
    options = parser.parse_args()

    if subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT):
        raise RuntimeError("Commit or resolve local changes before syncing Windows Git.")
    branch = git("branch", "--show-current", text=True).strip()
    if not branch:
        raise RuntimeError("The local repository is detached; check out a named branch first.")
    head = git("rev-parse", "HEAD", text=True).strip()
    preserve_baseline_head = None
    preserve_sha = {}
    if options.preserve_dirty_manifest is not None:
        preserve_data = json.loads(options.preserve_dirty_manifest.read_text(encoding="utf-8"))
        preserve_baseline_head, preserve_sha = parse_preserve_manifest(preserve_data)
        canonical_baseline_head = git(
            "rev-parse", "--verify", f"{preserve_baseline_head}^{{commit}}", text=True
        ).strip()
        if canonical_baseline_head != preserve_baseline_head:
            raise RuntimeError("Preserve manifest baselineHead is not the specified full commit hash.")
        verify_preserved_paths_unchanged(preserve_baseline_head, head, preserve_sha)

    paths = git("ls-files", "-z").decode().strip("\0").split("\0")
    manifest = {
        path: hashlib.sha256((ROOT / path).read_bytes()).hexdigest()
        for path in paths
        if path
    }
    unity_paths = [path for path in manifest if path.startswith(UNITY_ROOTS)]
    supplemental = [path for path in manifest if not path.startswith(UNITY_ROOTS)]

    state = pipeline(options.host, options.project_path, "editor_status")
    if state.get("playMode") != "stopped" or state.get("compiling"):
        raise RuntimeError("Stop Play Mode and finish compilation before syncing Git metadata.")

    TASK_TMP.mkdir(parents=True, exist_ok=True)
    name = "git-sync-" + uuid.uuid4().hex
    remote_stage = checked_pwsh(
        "$taskRoot = Join-Path ([Environment]::GetFolderPath('UserProfile')) 'tmp\\vrc-diffusion'; "
        "New-Item -ItemType Directory -Path $taskRoot -Force | Out-Null; "
        f"$stage = Join-Path $taskRoot {pwsh_literal(name)}; "
        "New-Item -ItemType Directory -Path $stage -Force | Out-Null; Write-Output $stage",
        options.host,
    )
    try:
        with tempfile.TemporaryDirectory(prefix="git-sync-", dir=TASK_TMP) as temporary:
            stage = Path(temporary)
            (stage / "files.json").write_text(json.dumps(manifest), encoding="utf-8")
            (stage / "unity-paths.json").write_text(json.dumps(unity_paths), encoding="utf-8")
            if preserve_baseline_head is not None:
                (stage / "preserve-dirty.json").write_text(
                    json.dumps(
                        {"baselineHead": preserve_baseline_head, "files": preserve_sha},
                        separators=(",", ":"),
                    ),
                    encoding="utf-8",
                )
            subprocess.run(
                ["git", "bundle", "create", str(stage / "project.bundle"), branch],
                cwd=ROOT,
                check=True,
            )
            with tarfile.open(stage / "supplemental.tgz", "w:gz") as tar:
                for path in supplemental:
                    if path not in preserve_sha:
                        tar.add(ROOT / path, arcname=path)
            for file in stage.iterdir():
                subprocess.run(
                    [
                        "scp", "-q", "-o", "BatchMode=yes", "-o", "LogLevel=ERROR",
                        str(file), options.host + ":" + remote_stage.replace("\\", "/") + "/" + file.name,
                    ],
                    check=True,
                )

        preserve_enabled = preserve_baseline_head is not None
        sync_script = f"""
Set-Location -LiteralPath {pwsh_literal(options.project_path)}
$stage = {pwsh_literal(remote_stage)}
$projectRoot = [IO.Path]::GetFullPath({pwsh_literal(options.project_path)}).TrimEnd('\\')
$expected = Get-Content -Raw (Join-Path $stage 'files.json') | ConvertFrom-Json -AsHashtable
$expectedPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($expectedPath in $expected.Keys) {{ [void]$expectedPaths.Add($expectedPath) }}
$unityPaths = Get-Content -Raw (Join-Path $stage 'unity-paths.json') | ConvertFrom-Json
$preserveEnabled = {"$true" if preserve_enabled else "$false"}
$preserveSHA = [System.Collections.Generic.Dictionary[string,string]]::new([System.StringComparer]::Ordinal)
$preservePathSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
$baselineHead = $null
if ($preserveEnabled) {{
    $preserve = Get-Content -Raw (Join-Path $stage 'preserve-dirty.json') | ConvertFrom-Json -AsHashtable
    $baselineHead = [string]$preserve['baselineHead']
    foreach ($entry in $preserve['files'].GetEnumerator()) {{
        $path = [string]$entry.Key
        $hash = [string]$entry.Value
        [void]$preserveSHA.Add($path, $hash)
        [void]$preservePathSet.Add($path)
    }}
}}
function Get-ExpectedHash([string]$path) {{
    if ($preserveSHA.ContainsKey($path)) {{ return $preserveSHA[$path] }}
    if ($expected.ContainsKey($path)) {{ return $expected[$path] }}
    return $null
}}
$bundle = Join-Path $stage 'project.bundle'
if (!(Test-Path -LiteralPath '.git')) {{ throw 'Windows project has no Git repository; initialize it separately before syncing.' }}
$gitRoot = (& git rev-parse --show-toplevel).Trim()
if ($LASTEXITCODE -ne 0 -or [IO.Path]::GetFullPath($gitRoot).TrimEnd('\\') -ne $projectRoot) {{ throw 'Windows Git root does not match the target Unity project.' }}
$currentBranch = (& git branch --show-current).Trim()
if ($LASTEXITCODE -ne 0 -or $currentBranch -ne {pwsh_literal(branch)}) {{ throw 'Windows and local branches differ; align them manually before syncing.' }}
$currentHead = (& git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) {{ throw 'Cannot inspect Windows HEAD.' }}
if ($preserveEnabled -and $currentHead -ne $baselineHead) {{ throw 'Windows HEAD differs from the audited preserve manifest baseline.' }}
if ($preserveEnabled) {{
    $staged = @(& git diff --cached --name-only)
    if ($LASTEXITCODE -ne 0) {{ throw 'Cannot inspect staged Windows changes.' }}
    if ($staged.Count -ne 0) {{ throw 'Windows index has staged changes; resolve them before preserving tracked edits.' }}
}}

# Only align Git metadata after confirming the live Unity files match the local commit.
foreach ($path in $unityPaths) {{
    $expectedHash = Get-ExpectedHash $path
    if (!(Test-Path -LiteralPath $path -PathType Leaf) -or !$expectedHash -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedHash) {{ throw "Live Unity content differs: $path. Capture or resolve it first." }}
}}
$dirty = @(& git -c core.quotepath=false diff HEAD --name-only)
if ($LASTEXITCODE -ne 0) {{ throw 'Cannot inspect Windows Git changes.' }}
if ($preserveEnabled) {{
    $dirtySet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($path in $dirty) {{ [void]$dirtySet.Add($path) }}
    foreach ($path in $preservePathSet) {{ if (!$dirtySet.Contains($path)) {{ throw "Audited path is not currently dirty: $path" }} }}
}}
foreach ($path in $dirty) {{
    if ($preserveEnabled -and $preserveSHA.ContainsKey($path)) {{
        $expectedHash = $preserveSHA[$path]
    }} else {{
        if (!$expected.ContainsKey($path)) {{ throw "Uncaptured Windows edit: $path" }}
        $expectedHash = $expected[$path]
    }}
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) {{ throw "Uncaptured Windows edit: $path" }}
    if ($preserveEnabled) {{
        $item = Get-Item -LiteralPath $path -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {{ throw "Preserved path uses a reparse point: $path" }}
    }}
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedHash) {{ throw "Uncaptured Windows edit: $path" }}
}}
$untracked = @(& git ls-files --others --exclude-standard)
if ($LASTEXITCODE -ne 0) {{ throw 'Cannot inspect untracked Windows files.' }}
foreach ($path in $untracked) {{
    if (!$expectedPaths.Contains($path) -or !(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected[$path]) {{ throw "Uncaptured Windows file: $path" }}
}}

& git fetch $bundle {pwsh_literal(branch)} 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {{ throw 'Git bundle fetch failed.' }}
& git merge-base --is-ancestor HEAD FETCH_HEAD
if ($LASTEXITCODE -ne 0) {{ throw 'Windows branch contains commits absent from local Git; resolve the divergence before syncing.' }}
& tar -xzf (Join-Path $stage 'supplemental.tgz')
if ($LASTEXITCODE -ne 0) {{ throw 'Supplemental source extraction failed.' }}
# Mixed reset changes only the branch pointer and index; validated Unity files stay in place.
& git reset --mixed FETCH_HEAD | Out-Null
if ($LASTEXITCODE -ne 0) {{ throw 'Windows Git index alignment failed.' }}
$actual = (& git rev-parse HEAD).Trim()
if ($actual -ne {pwsh_literal(head)}) {{ throw 'Windows HEAD does not match the local commit after reset.' }}
if ($preserveEnabled) {{
    $stagedAfter = @(& git diff --cached --name-only)
    if ($LASTEXITCODE -ne 0 -or $stagedAfter.Count -ne 0) {{ throw 'Windows Git index is not clean after reset.' }}
    $remainingDirty = @(& git -c core.quotepath=false diff HEAD --name-only)
    if ($LASTEXITCODE -ne 0) {{ throw 'Cannot verify Windows dirty paths after reset.' }}
    foreach ($path in $remainingDirty) {{ if (!$preservePathSet.Contains($path)) {{ throw "Unexpected Windows edit after reset: $path" }} }}
    foreach ($path in $preservePathSet) {{
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) {{ throw "Preserved file is missing after reset: $path" }}
        $item = Get-Item -LiteralPath $path -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {{ throw "Preserved path uses a reparse point after reset: $path" }}
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $preserveSHA[$path]) {{ throw "Preserved file changed during reset: $path" }}
    }}
    $untrackedAfter = @(& git ls-files --others --exclude-standard)
    if ($LASTEXITCODE -ne 0 -or $untrackedAfter.Count -ne 0) {{ throw "Windows has unexpected untracked files after reset: $($untrackedAfter -join ', ')" }}
    [pscustomobject]@{{ synced=$true; branch={pwsh_literal(branch)}; windowsHead=$actual; localHead={pwsh_literal(head)}; unityFilesRewritten=$false; preservedDirtyPaths=@($preservePathSet) }} | ConvertTo-Json
}} else {{
    $status = @(& git status --porcelain --untracked-files=all)
    if ($status.Count -ne 0) {{ throw "Windows Git is not clean: $($status -join ', ')" }}
    [pscustomobject]@{{ synced=$true; branch={pwsh_literal(branch)}; windowsHead=$actual; localHead={pwsh_literal(head)}; unityFilesRewritten=$false }} | ConvertTo-Json
}}
"""
        with tempfile.TemporaryDirectory(prefix="git-sync-script-", dir=TASK_TMP) as temporary:
            script_path = Path(temporary) / "align.ps1"
            script_path.write_text(sync_script, encoding="utf-8")
            remote_script = remote_stage.replace("\\", "/") + "/align.ps1"
            subprocess.run(
                [
                    "scp", "-q", "-o", "BatchMode=yes", "-o", "LogLevel=ERROR",
                    str(script_path), options.host + ":" + remote_script,
                ],
                check=True,
            )
        print(checked_pwsh("& " + pwsh_literal(remote_script), options.host))
    finally:
        checked_pwsh(f"Remove-Item -LiteralPath {pwsh_literal(remote_stage)} -Recurse -Force", options.host)


if __name__ == "__main__":
    main()
