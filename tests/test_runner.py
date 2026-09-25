import base64
import io
import json
import os
from pathlib import Path
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import patch
import zipfile

import app
import core
from qa_results import summarize_adapter, validate_summary
from recover import restore

REV = "a" * 40


class RunnerTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.state = self.root / "state"
        self.source = self.root / "source"
        self.state.mkdir()
        self.source.mkdir()

    def tearDown(self):
        self.tmp.cleanup()

    def job(self, codes, profile="custom"):
        steps = [{"argv": ["$PYTHON", "-c", c], "timeout_seconds": 5} for c in codes]
        (self.source / "automation.json").write_text(json.dumps({"schema": 1, "profile": profile, "steps": steps}))
        return core.run_job(self.source, self.state, REV)

    def archive(self, names):
        buf = io.BytesIO()
        with zipfile.ZipFile(buf, "w") as z:
            for name in names:
                info = zipfile.ZipInfo("placeholder")
                # Preserve raw archive bytes even on Windows, where __init__ normalizes separators.
                info.filename = info.orig_filename = name
                z.writestr(info, "test")
        return buf.getvalue()

    def test_archive_extracts_single_root(self):
        target = self.root / "download"
        core.extract_archive(self.archive(["owner-sha/a.txt", "owner-sha/folder/b.txt"]), target)
        self.assertEqual((target / "folder/b.txt").read_text(), "test")

    def test_download_is_pinned_and_saves_original_source_zip(self):
        data = self.archive(["r/app.py", "r/worker.py", "r/core.py", "r/automation.json"])
        with patch("core.api", return_value=data) as request:
            target = core.download_revision(self.state, REV)
        request.assert_called_once_with(self.state, f"repos/{core.REPOSITORY}/zipball/{REV}", binary=True)
        self.assertTrue((target / "app.py").is_file())
        self.assertEqual((self.state / "latest-source.zip").read_bytes(), data)

    def test_incomplete_download_never_becomes_latest(self):
        with patch("core.api", return_value=self.archive(["r/missing.txt"])):
            with self.assertRaisesRegex(core.RunnerError, "incomplete-update"):
                core.download_revision(self.state, REV)
        self.assertFalse((self.state / "latest-source.zip").exists())

    def test_pending_private_fields_block_upload(self):
        pending = self.state / "pending"
        pending.mkdir()
        (pending / ("a"*32 + ".json")).write_text('{"token":"must never upload"}')
        with patch("app.upload_report") as upload:
            with self.assertRaises(ValueError):
                app.retry_pending(self.state)
            upload.assert_not_called()

    def test_stop_during_auto_wait_prevents_github_call(self):
        core.atomic_json(self.state / "last-run.json", {"revision": REV, "status": "passed"})
        (self.state / "STOP").touch()
        with patch("app.latest_revision") as request:
            with self.assertRaises(core.Cancelled):
                app.automatic(self.state)
            request.assert_not_called()

    def test_archive_rejects_traversal_windows_paths_and_duplicate_names(self):
        for names in (["r/../../escaped"], ["r/C:evil"], ["r/foo\\bar"], ["r/NUL.txt"],
                      ["r/a", "r/A"], ["r/a "], ["r/a", "s/b"]):
            with self.subTest(names=names), self.assertRaises(core.RunnerError):
                core.extract_archive(self.archive(names), self.root / "bad")

    def test_archive_rejects_symlink(self):
        buf = io.BytesIO()
        with zipfile.ZipFile(buf, "w") as z:
            info = zipfile.ZipInfo("r/link")
            info.external_attr = 0o120777 << 16
            z.writestr(info, "../outside")
        with self.assertRaises(core.RunnerError):
            core.extract_archive(buf.getvalue(), self.root / "bad")

    def test_job_pass_and_report_never_contains_raw_output(self):
        report = self.job(["print('user@example.test C:/Users/private 192.0.2.1 ghp_secret error CS1234')"])
        self.assertEqual(report["status"], "passed")
        self.assertEqual(report["steps"][0]["compiler_codes"], ["CS1234"])
        for file in self.state.rglob("*.json"):
            text = file.read_text()
            for secret in ("user@example", "private", "192.0.2.1", "ghp_secret"):
                self.assertNotIn(secret, text)

    def test_first_failure_prevents_later_step(self):
        report = self.job(["raise SystemExit(7)", "raise SystemExit(0)"])
        self.assertEqual(report["status"], "failed")
        self.assertEqual(len(report["steps"]), 1)
        self.assertEqual(report["steps"][0]["exit_code"], 7)

    def test_mod_exit_three_is_skip(self):
        report = self.job(["raise SystemExit(3)"], profile="mod-qa")
        self.assertEqual(report["status"], "skipped")

    def test_generic_exit_three_is_failure(self):
        self.assertEqual(self.job(["raise SystemExit(3)"])["status"], "failed")

    def test_mod_exit_zero_without_game_evidence_fails(self):
        self.assertEqual(self.job(["print('not a gameplay test')"], profile="mod-qa")["status"], "failed")

    def test_stop_before_job_does_not_execute(self):
        (self.state / "STOP").touch()
        report = self.job(["raise SystemExit(0)"])
        self.assertEqual(report["status"], "cancelled")
        self.assertFalse(report["steps"])

    def test_stop_cancels_running_command(self):
        timer = threading.Timer(0.3, lambda: (self.state / "STOP").touch())
        timer.start()
        start = time.monotonic()
        try:
            with self.assertRaises(core.Cancelled):
                core.run_process([sys.executable, "-c", "import time; time.sleep(30)"], self.source, self.state)
            self.assertLess(time.monotonic() - start, 5)
        finally:
            timer.join()

    def test_command_timeout(self):
        with self.assertRaisesRegex(core.RunnerError, "timeout"):
            core.run_process([sys.executable, "-c", "import time; time.sleep(30)"], self.source, self.state, timeout=0.2)

    def test_output_capture_is_bounded(self):
        with self.assertRaisesRegex(core.RunnerError, "output-too-large"):
            core.run_process([sys.executable, "-c", "print('x'*50000)"], self.source, self.state, capture=True, limit=100)

    def test_instance_lock_blocks_second_launcher(self):
        with core.InstanceLock(self.state):
            with self.assertRaisesRegex(core.RunnerError, "already-running"):
                with core.InstanceLock(self.state):
                    self.fail("second lock acquired")
        with core.InstanceLock(self.state):
            pass

    def test_invalid_manifest_rejected_without_execution(self):
        (self.source / "automation.json").write_text('{"schema":1,"profile":"custom","steps":[]}')
        report = core.run_job(self.source, self.state, REV)
        self.assertEqual(report["error"], "invalid-job-manifest")

    def test_report_rejects_extra_private_fields(self):
        report = self.job(["pass"])
        report["username"] = "private"
        with self.assertRaises(ValueError):
            core.validate_report(report)

    def test_upload_uses_reports_branch_and_exact_safe_schema(self):
        report = self.job(["pass"])
        calls = []
        def fake_api(state, endpoint, payload=None, **kwargs):
            calls.append((endpoint, payload))
            return {}
        with patch("core.api", side_effect=fake_api):
            core.upload_report(self.state, report)
        payload = calls[-1][1]
        self.assertEqual(payload["branch"], "runner-reports")
        self.assertEqual(json.loads(base64.b64decode(payload["content"])), report)
        self.assertFalse(list((self.state / "pending").glob("*.json")))

    def test_failed_upload_preserves_pending(self):
        report = self.job(["pass"])
        with patch("core.api", side_effect=core.RunnerError("github-request-failed")):
            with self.assertRaises(core.RunnerError):
                core.upload_report(self.state, report)
        self.assertEqual(len(list((self.state / "pending").glob("*.json"))), 1)

    def test_retry_after_lost_response_is_idempotent(self):
        report = self.job(["pass"])
        expected = json.dumps(report, indent=2) + "\n"
        def fake_api(state, endpoint, payload=None):
            if payload:
                raise core.RunnerError("github-request-failed")
            return {"content": base64.b64encode(expected.encode()).decode()}
        with patch("core.api", side_effect=fake_api):
            core.upload_report(self.state, report)
        self.assertFalse(list((self.state / "pending").glob("*.json")))

    def test_auto_skips_same_revision_and_stops_after_failure(self):
        core.atomic_json(self.state / "last-run.json", {"revision": REV, "status": "passed"})
        with patch("app.wait_cancellable"), patch("app.latest_revision", side_effect=[REV, "b"*40]), patch("app.run_once", return_value={"status": "failed"}) as run:
            app.automatic(self.state)
            run.assert_called_once_with(self.state, "b"*40)

    def test_auto_requires_successful_manual_result(self):
        core.atomic_json(self.state / "last-run.json", {"revision": REV, "status": "failed"})
        with self.assertRaises(core.RunnerError):
            app.automatic(self.state)

    def test_auto_stops_at_run_limit(self):
        core.atomic_json(self.state / "last-run.json", {"revision": REV, "status": "passed"})
        with patch("app.wait_cancellable"), patch("app.latest_revision", side_effect=["b"*40,"c"*40]), patch("app.run_once", return_value={"status": "passed"}) as run:
            app.automatic(self.state, max_runs=2)
            self.assertEqual(run.call_count, 2)

    def test_adapter_retains_skip_drops_all_free_text(self):
        status, assertions = summarize_adapter({"schemaVersion": "ue.qa.adapter.v1", "status": "SKIP", "assertions": [
            {"id":"punch.execution", "status":"SKIP", "critical":True, "reason":"private", "evidence":{"token":"secret"}}]})
        self.assertEqual(status, "SKIP")
        self.assertEqual(assertions, [{"id":"punch.execution", "status":"SKIP", "critical":True}])

    def test_adapter_rejects_unrecognized_assertion(self):
        with self.assertRaises(ValueError):
            summarize_adapter({"schemaVersion":"ue.qa.adapter.v1", "status":"PASS", "assertions":[{"id":"secret"}]})

    def test_recovery_restores_only_previous_dll(self):
        target = self.root / "game/BepInEx/plugins/UnspottableExpanded/UnspottableExpanded.dll"
        target.parent.mkdir(parents=True)
        target.write_bytes(b"new")
        target.with_name("UnspottableExpanded.uqa-backup").write_bytes(b"old")
        target.with_name("UnspottableExpanded.uqa-testing").write_text("original-present")
        self.assertTrue(restore(self.root / "game"))
        self.assertEqual(target.read_bytes(), b"old")
        self.assertFalse(restore(self.root / "game"))

    def test_recovery_missing_backup_does_not_delete_installed_dll(self):
        target = self.root / "game/BepInEx/plugins/UnspottableExpanded/UnspottableExpanded.dll"
        target.parent.mkdir(parents=True)
        target.write_bytes(b"new")
        target.with_name("UnspottableExpanded.uqa-testing").write_text("original-present")
        with self.assertRaises(core.RunnerError):
            restore(self.root / "game")
        self.assertEqual(target.read_bytes(), b"new")


if __name__ == "__main__":
    unittest.main()
