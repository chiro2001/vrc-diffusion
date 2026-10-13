#!/usr/bin/env python3
"""Run Unity CLI/Pipeline through SSH and PowerShell on the project workstation."""

import argparse
import base64
import json
import subprocess
import sys

DEFAULT_HOST = "192.168.101.5"
DEFAULT_PROJECT = r"D:\Projects\VRChatProjects2\vrc-diffusion"


def pwsh_literal(value: str) -> str:
    return "'" + value.replace("'", "''") + "'"


def run_pwsh(script: str, host: str = DEFAULT_HOST, capture_output: bool = False):
    prefix = (
        "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'; "
        "$PSStyle.OutputRendering = 'PlainText'; "
        "[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); "
        "$OutputEncoding = [Console]::OutputEncoding; "
    )
    encoded = base64.b64encode((prefix + script).encode("utf-16le")).decode("ascii")
    return subprocess.run([
        "ssh", "-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=15",
        "-o", "LogLevel=ERROR", host,
        "pwsh -NoLogo -NoProfile -NonInteractive -EncodedCommand " + encoded,
    ], capture_output=capture_output)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default=DEFAULT_HOST)
    parser.add_argument(
        "--project-path", default=DEFAULT_PROJECT
    )
    parser.add_argument(
        "--cli", action="store_true", help="Forward a top-level Unity CLI command."
    )
    parser.add_argument(
        "--check-result", action="store_true",
        help="Return a failure exit code when the Pipeline payload reports failure (requires JSON output).",
    )
    parser.add_argument("arguments", nargs=argparse.REMAINDER)
    options = parser.parse_args()
    if not options.arguments:
        parser.error("Supply a Pipeline command, or use --cli with a Unity CLI command.")

    arguments = options.arguments
    if arguments[0] == "--":
        arguments = arguments[1:]
    if not options.cli:
        arguments = [
            "command", "--project-path", options.project_path,
            "--caller", "codex", "--skill", "unity-pipeline", "--json",
            *arguments,
        ]
    script = (
        f"Set-Location -LiteralPath {pwsh_literal(options.project_path)}; "
        "& 'unity' " + " ".join(pwsh_literal(arg) for arg in arguments)
        + "; exit $LASTEXITCODE"
    )
    result = run_pwsh(script, options.host, options.check_result)
    if options.check_result:
        sys.stdout.buffer.write(result.stdout)
        sys.stderr.buffer.write(result.stderr)
        if result.returncode:
            return result.returncode
        try:
            envelope = json.loads(result.stdout)
            payload = (envelope.get("data") or {}).get("result")
            if isinstance(payload, dict) and (payload.get("success") is False or payload.get("failed") is True):
                return 6
        except (ValueError, AttributeError):
            print("Cannot validate non-JSON Unity CLI output.", file=sys.stderr)
            return 6
    return result.returncode


if __name__ == "__main__":
    sys.exit(main())
