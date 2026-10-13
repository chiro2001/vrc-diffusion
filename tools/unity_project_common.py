#!/usr/bin/env python3
"""Shared calls for the remote Unity project tools."""

import json
import subprocess
import sys
from pathlib import Path

from unity_remote import run_pwsh

ROOT = Path(__file__).resolve().parents[1]


def checked_pwsh(script: str, host: str) -> str:
    result = run_pwsh(script, host, capture_output=True)
    if result.returncode:
        raise RuntimeError(result.stderr.decode("utf-8", errors="replace"))
    return result.stdout.decode("utf-8-sig").strip()


def pipeline(host: str, project_path: str, *arguments: str):
    result = subprocess.run(
        [
            sys.executable,
            str(ROOT / "tools/unity_remote.py"),
            "--check-result",
            "--host",
            host,
            "--project-path",
            project_path,
            *arguments,
        ],
        text=True,
        capture_output=True,
    )
    if result.returncode:
        raise RuntimeError(result.stdout + result.stderr)
    return json.loads(result.stdout)["data"]["result"]
