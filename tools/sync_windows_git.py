#!/usr/bin/env python3
"""Align the Windows Git metadata with the local commit without rewriting Unity files."""

import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import tarfile
import tempfile
import uuid

from unity_project_common import checked_pwsh, pipeline
from unity_remote import DEFAULT_HOST, DEFAULT_PROJECT, pwsh_literal

ROOT = Path(__file__).resolve().parents[1]
TASK_TMP = Path.home() / "tmp/vrc-diffusion"
UNITY_ROOTS = ("Assets/", "Packages/", "ProjectSettings/", "AgentScripts/")


def git(*arguments, text=False):
    return subprocess.check_output(["git", *arguments], cwd=ROOT, text=text)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default=DEFAULT_HOST)
    parser.add_argument("--project-path", default=DEFAULT_PROJECT)
    options = parser.parse_args()

    if subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT):
        raise RuntimeError("Commit or resolve local changes before syncing Windows Git.")
    branch = git("branch", "--show-current", text=True).strip()
    if not branch:
        raise RuntimeError("The local repository is detached; check out a named branch first.")
    head = git("rev-parse", "HEAD", text=True).strip()
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
            subprocess.run(
                ["git", "bundle", "create", str(stage / "project.bundle"), branch],
                cwd=ROOT,
                check=True,
            )
            with tarfile.open(stage / "supplemental.tgz", "w:gz") as tar:
                for path in supplemental:
                    tar.add(ROOT / path, arcname=path)
            for file in stage.iterdir():
                subprocess.run(
                    [
                        "scp", "-q", "-o", "BatchMode=yes", "-o", "LogLevel=ERROR",
                        str(file), options.host + ":" + remote_stage.replace("\\", "/") + "/" + file.name,
                    ],
                    check=True,
                )

        sync_script = f"""
Set-Location -LiteralPath {pwsh_literal(options.project_path)}
$stage = {pwsh_literal(remote_stage)}
$projectRoot = [IO.Path]::GetFullPath({pwsh_literal(options.project_path)}).TrimEnd('\\')
$expected = Get-Content -Raw (Join-Path $stage 'files.json') | ConvertFrom-Json -AsHashtable
$expectedPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($expectedPath in $expected.Keys) {{ [void]$expectedPaths.Add($expectedPath) }}
$unityPaths = Get-Content -Raw (Join-Path $stage 'unity-paths.json') | ConvertFrom-Json
$bundle = Join-Path $stage 'project.bundle'
if (!(Test-Path -LiteralPath '.git')) {{ throw 'Windows project has no Git repository; initialize it separately before syncing.' }}
$gitRoot = (& git rev-parse --show-toplevel).Trim()
if ($LASTEXITCODE -ne 0 -or [IO.Path]::GetFullPath($gitRoot).TrimEnd('\\') -ne $projectRoot) {{ throw 'Windows Git root does not match the target Unity project.' }}
$currentBranch = (& git branch --show-current).Trim()
if ($LASTEXITCODE -ne 0 -or $currentBranch -ne {pwsh_literal(branch)}) {{ throw 'Windows and local branches differ; align them manually before syncing.' }}

# Only align Git metadata after confirming the live Unity files match the local commit.
foreach ($path in $unityPaths) {{
    if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected[$path]) {{ throw "Live Unity content differs: $path. Capture or resolve it first." }}
}}
$dirty = @(& git -c core.quotepath=false diff HEAD --name-only)
if ($LASTEXITCODE -ne 0) {{ throw 'Cannot inspect Windows Git changes.' }}
foreach ($path in $dirty) {{
    if (!$expected.ContainsKey($path) -or !(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected[$path]) {{ throw "Uncaptured Windows edit: $path" }}
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
$status = @(& git status --porcelain --untracked-files=all)
if ($actual -ne {pwsh_literal(head)} -or $status.Count -ne 0) {{ throw "Windows Git is not clean: $($status -join ', ')" }}
[pscustomobject]@{{ synced=$true; branch={pwsh_literal(branch)}; windowsHead=$actual; localHead={pwsh_literal(head)}; unityFilesRewritten=$false }} | ConvertTo-Json
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
