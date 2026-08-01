import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).parents[1] / "scripts" / "scan_candidates.py"
SPEC = importlib.util.spec_from_file_location("scan_candidates", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


class ScanCandidatesTests(unittest.TestCase):
    def test_protected_user_libraries(self):
        self.assertTrue(MODULE.is_protected(Path(r"C:\Users\Someone\Downloads\old.iso")))
        self.assertTrue(MODULE.is_protected(Path(r"C:\Windows\System32\config")))
        self.assertTrue(MODULE.is_protected(Path(r"D:\work\.git\objects")))
        self.assertFalse(MODULE.is_protected(Path(r"D:\apps\Widget\Cache")))

    def test_tree_size_does_not_follow_symlinks(self):
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            (root / "Cache").mkdir()
            (root / "Cache" / "blob.bin").write_bytes(b"x" * 2048)
            budget = MODULE.Budget(5, 100)
            size, complete, denied = MODULE.tree_size(root / "Cache", budget)
            self.assertEqual(size, 2048)
            self.assertTrue(complete)
            self.assertEqual(denied, 0)

    def test_cli_report_is_read_only(self):
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            cache = root / "Cache"
            cache.mkdir()
            payload = cache / "large.tmp"
            payload.write_bytes(b"x" * 2048)
            report = root / "report.json"
            result = subprocess.run(
                [
                    sys.executable,
                    str(SCRIPT),
                    str(root),
                    "--min-size-mb",
                    "1",
                    "--max-seconds",
                    "5",
                    "--output",
                    str(report),
                ],
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(result.returncode, 0, result.stderr)
            data = json.loads(report.read_text(encoding="utf-8"))
            self.assertTrue(data["read_only"])
            self.assertTrue(payload.exists())

    def test_exact_duplicates_require_content_hash_match(self):
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            first = root / "one.bin"
            second = root / "two.bin"
            third = root / "different.bin"
            first.write_bytes(b"a" * 4096)
            second.write_bytes(b"a" * 4096)
            third.write_bytes(b"b" * 4096)
            budget = MODULE.Budget(5, 100)
            groups = MODULE.find_duplicates(
                [(first, 4096), (second, 4096), (third, 4096)],
                budget,
            )
            self.assertEqual(len(groups), 1)
            self.assertEqual(set(groups[0].paths), {str(first), str(second)})
            self.assertEqual(groups[0].risk, "high")
            self.assertEqual(groups[0].confidence, "high")
            self.assertIn("SHA-256", groups[0].evidence)

    def test_candidate_description_explains_identity_and_impact(self):
        details = MODULE.describe_candidate(
            Path(r"C:\Users\Someone\AppData\Local\pip\Cache"),
            "known-cache",
            "known path",
        )
        self.assertIn("pip", details["identity"])
        self.assertEqual(details["confidence"], "high")
        self.assertIn("不会卸载", details["deletion_impact"])
        self.assertTrue(details["recommended_action"])

    def test_bundled_application_node_modules_is_not_project_cache(self):
        self.assertTrue(
            MODULE.is_bundled_app_dependency(
                Path(r"D:\cursor\resources\app\node_modules")
            )
        )
        self.assertFalse(
            MODULE.is_bundled_app_dependency(
                Path(r"D:\Code\my-app\node_modules")
            )
        )


if __name__ == "__main__":
    unittest.main()
