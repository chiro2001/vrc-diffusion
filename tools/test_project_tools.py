"""Configuration checks for the vrc-diffusion Unity helper tools."""

from pathlib import Path
import unittest
from unittest import mock

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


class PreserveDirtyManifestTests(unittest.TestCase):
    def test_reads_preserved_path_tree_entry(self):
        mode, blob = sync_windows_git.git_tree_entry("HEAD", "tools/sync_windows_git.py")

        self.assertEqual(mode, "100644")
        self.assertRegex(blob, r"^(?:[0-9a-f]{40}|[0-9a-f]{64})$")

    def test_parses_valid_manifest_and_normalizes_hashes(self):
        baseline = "a" * 40
        digest = "B" * 64
        parsed_baseline, files = sync_windows_git.parse_preserve_manifest(
            {"baselineHead": baseline, "files": {"Assets/Scene.unity": digest}}
        )

        self.assertEqual(parsed_baseline, baseline)
        self.assertEqual(files, {"Assets/Scene.unity": digest.lower()})

    def test_rejects_unsafe_or_unhashed_manifest_paths(self):
        baseline = "a" * 40
        with self.assertRaises(ValueError):
            sync_windows_git.parse_preserve_manifest(
                {"baselineHead": baseline, "files": {"../outside.asset": "b" * 64}}
            )
        with self.assertRaises(ValueError):
            sync_windows_git.parse_preserve_manifest(
                {"baselineHead": baseline, "files": {".": "b" * 64}}
            )
        with self.assertRaises(ValueError):
            sync_windows_git.parse_preserve_manifest(
                {"baselineHead": baseline, "files": {"Assets/Scene.unity": "not-a-hash"}}
            )

    def test_rejects_local_commit_that_changes_a_preserved_tree_entry(self):
        with mock.patch.object(
            sync_windows_git,
            "git_tree_entry",
            side_effect=[("100644", "1" * 40), ("100644", "2" * 40)],
        ):
            with self.assertRaisesRegex(RuntimeError, "changed preserved path"):
                sync_windows_git.verify_preserved_paths_unchanged(
                    "a" * 40, "b" * 40, ["Assets/Scene.unity"]
                )


if __name__ == "__main__":
    unittest.main()
