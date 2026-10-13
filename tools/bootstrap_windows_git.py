#!/usr/bin/env python3
"""Bootstrap Windows Git metadata from a clean local commit without checking out files."""

import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath, PureWindowsPath
import re
import subprocess
import tempfile
import uuid

from unity_project_common import checked_pwsh
from unity_remote import DEFAULT_HOST, DEFAULT_PROJECT, pwsh_literal

ROOT = Path(__file__).resolve().parents[1]
TASK_TMP = Path.home() / "tmp/vrc-diffusion"
UNITY_ROOTS = ("Assets/", "Packages/", "ProjectSettings/", "AgentScripts/")
SAFE_SCP_PART = re.compile(r"^[A-Za-z0-9_.-]+$")


def git(*arguments, text=False):
    return subprocess.check_output(["git", *arguments], cwd=ROOT, text=text)


def collect_local_manifest():
    status = subprocess.check_output(
        ["git", "status", "--porcelain", "--untracked-files=all"], cwd=ROOT
    )
    if status:
        raise RuntimeError("Local Git must be clean before bootstrapping Windows Git.")

    head = git("rev-parse", "--verify", "HEAD^{commit}", text=True).strip()
    branch = git("branch", "--show-current", text=True).strip()
    if not branch:
        raise RuntimeError("The local repository is detached; check out its intended branch first.")
    if subprocess.run(
        ["git", "check-ref-format", "refs/heads/" + branch], cwd=ROOT, capture_output=True
    ).returncode:
        raise RuntimeError("The local branch name cannot be used on Windows Git: " + branch)

    tree = git("ls-tree", "-r", "--name-only", "-z", "HEAD").decode("utf-8").split("\0")
    paths = sorted(path for path in tree if path)
    if not paths:
        raise RuntimeError("The local HEAD has no tracked files.")

    files = {}
    seen_windows_names = set()
    for relative in paths:
        path = PurePosixPath(relative)
        if path.is_absolute() or ".." in path.parts or not path.parts or "\\" in relative or ":" in relative:
            raise RuntimeError("Unsafe tracked path in local HEAD: " + relative)
        windows_key = relative.casefold()
        if windows_key in seen_windows_names:
            raise RuntimeError("Windows cannot represent case-colliding paths: " + relative)
        seen_windows_names.add(windows_key)
        source = ROOT / relative
        if any(ROOT.joinpath(*path.parts[:index]).is_symlink() for index in range(1, len(path.parts) + 1)):
            raise RuntimeError("Refusing symlink tracked path: " + relative)
        if not source.is_file():
            raise RuntimeError("A file tracked by local HEAD is missing: " + relative)
        files[relative] = hashlib.sha256(source.read_bytes()).hexdigest()

    unity_paths = [path for path in paths if path.startswith(UNITY_ROOTS)]
    auxiliary_paths = [path for path in paths if not path.startswith(UNITY_ROOTS)]
    if not unity_paths:
        raise RuntimeError("Local HEAD contains no Unity assets or source files to verify.")
    return {
        "branch": branch,
        "head": head,
        "files": files,
        "unityPaths": unity_paths,
        "auxiliaryPaths": auxiliary_paths,
    }


def assert_local_manifest_unchanged(manifest):
    if subprocess.check_output(
        ["git", "status", "--porcelain", "--untracked-files=all"], cwd=ROOT
    ):
        raise RuntimeError("Local Git changed during bootstrap; refusing to create the bundle.")
    if git("rev-parse", "--verify", "HEAD^{commit}", text=True).strip() != manifest["head"]:
        raise RuntimeError("Local HEAD changed during bootstrap.")
    if git("branch", "--show-current", text=True).strip() != manifest["branch"]:
        raise RuntimeError("The local branch changed during bootstrap.")
    for relative, expected in manifest["files"].items():
        source = ROOT / relative
        if not source.is_file() or hashlib.sha256(source.read_bytes()).hexdigest() != expected:
            raise RuntimeError("Local file changed during bootstrap: " + relative)


def validate_project_path(project_path: str):
    windows_project = PureWindowsPath(project_path)
    if not windows_project.is_absolute() or ".." in windows_project.parts:
        raise ValueError("--project-path must be an absolute Windows path without '..'.")
    if not re.fullmatch(r"[A-Za-z]:\\(?:[A-Za-z0-9_.-]+\\)*[A-Za-z0-9_.-]+", str(windows_project)):
        raise ValueError("--project-path contains characters unsupported by scp destination syntax.")
    return windows_project


def remote_bootstrap_script(project_path: str, remote_stage: str):
    windows_project = validate_project_path(project_path)
    return f"""
param([switch]$Prepare, [switch]$Finalize)
if ($Prepare -eq $Finalize) {{ throw 'Choose exactly one bootstrap phase.' }}
$project = [IO.Path]::GetFullPath({pwsh_literal(str(windows_project))})
$stage = {pwsh_literal(remote_stage)}
$manifestPath = Join-Path $stage 'manifest.json'
$bundle = Join-Path $stage 'project.bundle'
if (!(Test-Path -LiteralPath $project -PathType Container)) {{ throw 'Target Unity project does not exist.' }}
$root = $project.TrimEnd('\\', '/')
$prefix = $root + '\\'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable

function Assert-FreshProject {{
    $gitPath = Join-Path $root '.git'
    if (Test-Path -LiteralPath $gitPath) {{
        $existingHead = & git -C $root rev-parse --verify HEAD 2>$null
        if ($LASTEXITCODE -eq 0) {{ throw 'Windows Git already has HEAD; use sync_windows_git.py instead.' }}
        throw 'Windows project contains an incomplete .git directory; inspect it manually.'
    }}
}}

function Get-ProjectFile([string]$relative) {{
    if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or $relative.Contains('\\') -or $relative -match '(^|[\\\\/])\\.{{1,2}}([\\\\/]|$)') {{ throw "Unsafe project path: $relative" }}
    $full = [IO.Path]::GetFullPath((Join-Path $root ($relative -replace '/', '\\')))
    if (!$full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {{ throw "Path escapes Unity project: $relative" }}
    return $full
}}

function Assert-NoReparsePath([string]$target, [string]$relative) {{
    $cursor = $target
    while ($cursor.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {{
        if (Test-Path -LiteralPath $cursor) {{
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {{ throw "Reparse-point path is not allowed: $relative" }}
        }}
        $parent = Split-Path -Parent $cursor
        if (!$parent -or $parent -eq $cursor) {{ break }}
        $cursor = $parent
    }}
}}

function Get-MissingAuxiliary([bool]$AllowMissing) {{
    $missing = @()
    foreach ($relative in $manifest.unityPaths) {{
        $target = Get-ProjectFile $relative
        Assert-NoReparsePath $target $relative
        if (!(Test-Path -LiteralPath $target -PathType Leaf)) {{ throw "Unity asset/source is missing on Windows: $relative" }}
        $actual = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $manifest.files[$relative]) {{ throw "Unity asset/source differs from local HEAD: $relative" }}
    }}
    foreach ($relative in $manifest.auxiliaryPaths) {{
        $target = Get-ProjectFile $relative
        Assert-NoReparsePath $target $relative
        if (!(Test-Path -LiteralPath $target -PathType Leaf)) {{
            if ((Test-Path -LiteralPath $target) -or !$AllowMissing -or [IO.Path]::GetExtension($relative).ToLowerInvariant() -notin @('.md', '.py')) {{
                throw "Auxiliary file cannot be copied safely or is required to exist: $relative"
            }}
            $missing += $relative
            continue
        }}
        $actual = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $manifest.files[$relative]) {{ throw "Auxiliary file differs from local HEAD: $relative" }}
    }}
    return $missing
}}

Assert-FreshProject
if ($Prepare) {{
    $missing = @(Get-MissingAuxiliary $true)
    foreach ($relative in $missing) {{
        $target = Get-ProjectFile $relative
        $parent = Split-Path -Parent $target
        Assert-NoReparsePath $parent $relative
        if (![string]::Equals($parent, $root, [StringComparison]::OrdinalIgnoreCase)) {{
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }}
    }}
    [pscustomobject]@{{ missingAux=@($missing) }} | ConvertTo-Json -Compress
    exit 0
}}

$missing = @(Get-MissingAuxiliary $false)
if ($missing.Count -gt 0) {{ throw 'Auxiliary files are still missing after the copy phase.' }}
if (!(Test-Path -LiteralPath $bundle -PathType Leaf)) {{ throw 'Git bundle is missing from the temporary directory.' }}
Set-Location -LiteralPath $root
& git init -b $manifest.branch | Out-Null
if ($LASTEXITCODE -ne 0) {{ throw 'Windows Git initialization failed.' }}
& git config core.autocrlf false
if ($LASTEXITCODE -ne 0) {{ throw 'Could not set byte-preserving Windows Git configuration.' }}
$branchRef = 'refs/heads/' + $manifest.branch
$trackingRef = 'refs/remotes/local/' + $manifest.branch
$refspec = $branchRef + ':' + $trackingRef
& git fetch --no-tags $bundle $refspec 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {{ throw 'Could not fetch the local branch from its bundle.' }}
$fetched = (& git rev-parse $trackingRef).Trim()
if ($LASTEXITCODE -ne 0 -or $fetched -ne $manifest.head) {{ throw 'Fetched bundle commit does not match local HEAD.' }}
& git update-ref $branchRef $manifest.head
if ($LASTEXITCODE -ne 0) {{ throw 'Could not set the Windows branch ref.' }}
& git symbolic-ref HEAD $branchRef
if ($LASTEXITCODE -ne 0) {{ throw 'Could not set the Windows HEAD symbolic ref.' }}
& git read-tree $manifest.head
if ($LASTEXITCODE -ne 0) {{ throw 'Could not populate the Windows Git index without checkout.' }}
$missing = @(Get-MissingAuxiliary $false)
if ($missing.Count -gt 0) {{ throw 'A tracked source or Unity file changed during bootstrap.' }}
$actual = (& git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $actual -ne $manifest.head) {{ throw 'Windows HEAD does not match local HEAD.' }}
$indexChanges = @(& git diff --cached --name-only)
if ($LASTEXITCODE -ne 0 -or $indexChanges.Count -gt 0) {{ throw 'Windows index does not match the bootstrapped commit.' }}
[pscustomobject]@{{ bootstrapped=$true; branch=$manifest.branch; windowsHead=$actual; localHead=$manifest.head; unityFilesVerified=$manifest.unityPaths.Count; auxiliaryFilesVerified=$manifest.auxiliaryPaths.Count; unityFilesWritten=$false; checkoutPerformed=$false }} | ConvertTo-Json
"""


def git_bundle_has_branch(bundle: Path, branch: str, head: str):
    listed = subprocess.check_output(["git", "bundle", "list-heads", str(bundle)], cwd=ROOT, text=True)
    expected = f"{head} refs/heads/{branch}"
    if expected not in {line.strip() for line in listed.splitlines()}:
        raise RuntimeError("The complete Git bundle does not contain the current branch at HEAD.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default=DEFAULT_HOST)
    parser.add_argument("--project-path", default=DEFAULT_PROJECT)
    options = parser.parse_args()
    if not re.fullmatch(r"[A-Za-z0-9_.@-]+", options.host):
        parser.error("--host contains unsupported SSH host characters.")
    validate_project_path(options.project_path)

    manifest = collect_local_manifest()
    TASK_TMP.mkdir(parents=True, exist_ok=True)
    name = "git-bootstrap-" + uuid.uuid4().hex
    remote_stage = checked_pwsh(
        "$taskRoot = Join-Path ([Environment]::GetFolderPath('UserProfile')) 'tmp\\vrc-diffusion'; "
        "New-Item -ItemType Directory -Path $taskRoot -Force | Out-Null; "
        f"$stage = Join-Path $taskRoot {pwsh_literal(name)}; "
        "New-Item -ItemType Directory -Path $stage -Force | Out-Null; Write-Output $stage",
        options.host,
    )
    try:
        with tempfile.TemporaryDirectory(prefix="git-bootstrap-", dir=TASK_TMP) as temporary:
            stage = Path(temporary)
            manifest_path = stage / "manifest.json"
            manifest_path.write_text(json.dumps(manifest, ensure_ascii=True, separators=(",", ":")), encoding="utf-8")
            script_path = stage / "bootstrap.ps1"
            script_path.write_text(remote_bootstrap_script(options.project_path, remote_stage), encoding="utf-8")

            for path in (manifest_path, script_path):
                subprocess.run(
                    [
                        "scp", "-q", "-o", "BatchMode=yes", "-o", "LogLevel=ERROR",
                        str(path), options.host + ":" + remote_stage.replace("\\", "/") + "/" + path.name,
                    ],
                    check=True,
                )

            remote_script = remote_stage.replace("\\", "/") + "/bootstrap.ps1"
            prepared = checked_pwsh("& " + pwsh_literal(remote_script) + " -Prepare", options.host)
            try:
                missing_aux = json.loads(prepared).get("missingAux", [])
            except (ValueError, AttributeError) as exc:
                raise RuntimeError("Could not read Windows bootstrap preflight result: " + prepared) from exc
            if isinstance(missing_aux, str):
                missing_aux = [missing_aux]
            for relative in missing_aux:
                relative_path = PurePosixPath(relative)
                if (
                    relative_path.is_absolute()
                    or ".." in relative_path.parts
                    or Path(relative).suffix.lower() not in (".md", ".py")
                    or not all(SAFE_SCP_PART.fullmatch(part) for part in relative_path.parts)
                ):
                    raise RuntimeError("Refusing unsafe missing auxiliary path: " + relative)
            by_directory = {}
            for relative in missing_aux:
                by_directory.setdefault(PurePosixPath(relative).parent, []).append(relative)
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

            assert_local_manifest_unchanged(manifest)
            bundle = stage / "project.bundle"
            subprocess.run(["git", "bundle", "create", str(bundle), "--all"], cwd=ROOT, check=True)
            git_bundle_has_branch(bundle, manifest["branch"], manifest["head"])
            subprocess.run(
                [
                    "scp", "-q", "-o", "BatchMode=yes", "-o", "LogLevel=ERROR",
                    str(bundle), options.host + ":" + remote_stage.replace("\\", "/") + "/project.bundle",
                ],
                check=True,
            )
            print(checked_pwsh("& " + pwsh_literal(remote_script) + " -Finalize", options.host))
    finally:
        checked_pwsh(f"Remove-Item -LiteralPath {pwsh_literal(remote_stage)} -Recurse -Force", options.host)


if __name__ == "__main__":
    main()
