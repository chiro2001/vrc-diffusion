"""Configuration checks for the vrc-diffusion Unity helper tools."""

from pathlib import Path
import unittest

import snapshot_unity_project
import sync_windows_git
from unity_remote import DEFAULT_HOST, DEFAULT_PROJECT, pwsh_literal


class ProjectToolConfigTests(unittest.TestCase):
    def test_remote_targets_new_project(self):
        self.assertEqual(DEFAULT_HOST, "192.168.101.5")
        self.assertEqual(DEFAULT_PROJECT, r"D:\Projects\VRChatProjects2\vrc-diffusion")

    def test_temporary_files_stay_under_project_task_directory(self):
        expected = Path.home() / "tmp/vrc-diffusion"
        self.assertEqual(snapshot_unity_project.TASK_TMP, expected)
        self.assertEqual(sync_windows_git.TASK_TMP, expected)

    def test_snapshot_mirrors_generic_unity_trees(self):
        self.assertIn("Assets", snapshot_unity_project.TREES)
        self.assertIn("ProjectSettings", snapshot_unity_project.TREES)
        self.assertFalse(any("CircuitLab" in path for path in snapshot_unity_project.TREES))

    def test_powershell_literal_escapes_quotes(self):
        self.assertEqual(pwsh_literal("D:\\Project's Files"), "'D:\\Project''s Files'")

    def test_git_sync_has_no_public_remote_or_repo_initialization(self):
        source = Path(sync_windows_git.__file__).read_text(encoding="utf-8")
        for forbidden in ("git init", "git commit", "git push", "remote get-url", "ls-remote"):
            self.assertNotIn(forbidden, source)


if __name__ == "__main__":
    unittest.main()
