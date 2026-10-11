"""Focused CLI checks for the M6 local diagnostics tool."""

from __future__ import annotations

import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
from types import SimpleNamespace
from unittest.mock import patch
import unittest


PROJECT_ROOT = Path(__file__).resolve().parents[2]
DIAGNOSTIC = PROJECT_ROOT / "tools" / "diagnose-m6.py"
SOURCE_ROOT = PROJECT_ROOT / "src"
if str(SOURCE_ROOT) not in sys.path:
    sys.path.insert(0, str(SOURCE_ROOT))
_SPEC = importlib.util.spec_from_file_location("diagnose_m6", DIAGNOSTIC)
diagnostics = importlib.util.module_from_spec(_SPEC)
assert _SPEC is not None and _SPEC.loader is not None
_SPEC.loader.exec_module(diagnostics)


class M6DiagnosticsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="nx-m6-diagnostics-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.config_path = self.root / "mcp-config.json"
        self.build_dir = self.root / "M2"
        self.build_dir.mkdir()
        self.helper = self.build_dir / "M2JsonlHelper.exe"
        self.entry = self.build_dir / "M2Entry.dll"
        self.bridge = self.build_dir / "M2Bridge.dll"
        for path in (self.helper, self.entry, self.bridge):
            path.write_bytes(b"local diagnostic fixture")
        self.nx_exe = self.root / "NXBIN" / "ugraf.exe"
        self.nx_exe.parent.mkdir()
        self.nx_exe.write_bytes(b"local NX path fixture")
        self.token = self.root / "private" / "bridge.token"
        self.write_config()

    def write_config(self, *, helper: Path | None = None, token: Path | None = None):
        config = {
            "pipe_name": "m6-diagnostics-test",
            "token_file": str((token or self.token).resolve()),
            "helper_path": str((helper or self.helper).resolve()),
            "timeout_seconds": 0.2,
        }
        self.config_path.write_text(json.dumps(config), encoding="utf-8")

    def run_diagnostic(self, *arguments: str):
        completed = subprocess.run(
            [sys.executable, "-B", str(DIAGNOSTIC), "--config", str(self.config_path),
             "--nx-executable", str(self.nx_exe), *arguments],
            cwd=PROJECT_ROOT,
            capture_output=True,
            text=True,
            encoding="utf-8",
            timeout=20,
            check=False,
        )
        self.assertEqual(completed.stderr, "", completed.stderr)
        return completed, json.loads(completed.stdout)

    def test_valid_offline_paths_and_token_presence_without_reading_token_value(self):
        sentinel = "M6_DIAGNOSTICS_SECRET_MUST_NOT_APPEAR"
        self.token.parent.mkdir()
        self.token.write_text(sentinel, encoding="utf-8")

        completed, report = self.run_diagnostic()

        self.assertEqual(completed.returncode, 0, report)
        self.assertEqual(report["checks"]["configuration"]["status"], "pass")
        self.assertTrue(report["checks"]["configuration"]["configured"])
        self.assertEqual(report["checks"]["nx_executable"]["status"], "pass")
        self.assertEqual(report["checks"]["bridge_helper"]["status"], "pass")
        self.assertEqual(report["checks"]["bridge_entry"]["status"], "pass")
        self.assertEqual(report["checks"]["bridge_library"]["status"], "pass")
        self.assertEqual(report["checks"]["token_file"]["status"], "pass")
        self.assertEqual(report["checks"]["online"]["status"], "skipped")
        self.assertNotIn(sentinel, completed.stdout)

    def test_missing_helper_and_nx_paths_are_located_with_actionable_status(self):
        missing_helper = self.root / "absent" / "M2JsonlHelper.exe"
        self.write_config(helper=missing_helper)

        completed, report = self.run_diagnostic("--nx-executable", str(self.root / "absent" / "ugraf.exe"))

        self.assertEqual(completed.returncode, 1)
        self.assertEqual(report["checks"]["configuration"]["status"], "fail")
        self.assertIn("helper_path", report["checks"]["configuration"]["message"])
        self.assertEqual(report["checks"]["bridge_helper"]["status"], "fail")
        self.assertEqual(report["checks"]["bridge_entry"]["status"], "fail")
        self.assertEqual(report["checks"]["nx_executable"]["status"], "fail")
        self.assertIn("suggestion", report["checks"]["nx_executable"])

    def test_online_missing_token_reports_bridge_not_ready_from_mcp_status_and_ping(self):
        completed, report = self.run_diagnostic("--online")

        self.assertEqual(completed.returncode, 1)
        online = report["checks"]["online"]
        self.assertEqual(online["status"], "fail")
        self.assertEqual(online["status_probe"]["code"], "BRIDGE_NOT_READY")
        self.assertEqual(online["ping_probe"]["code"], "BRIDGE_NOT_READY")
        self.assertIn("M2Entry.dll", online["status_probe"]["suggestion"])

    def test_online_report_keeps_only_status_and_ping_diagnostic_fields(self):
        status = {
            "pid": 8848,
            "ownership_state": "UNKNOWN_FROZEN",
            "writes_halted": True,
            "main_thread_id": "16728",
            "ownership_id": "must-not-be-reported",
            "binding_epoch": 9,
            "binding_identity": {"part_path": "must-not-be-reported"},
        }
        ping = {
            "nonce": diagnostics.DIAGNOSTIC_NONCE,
            "callback_thread_id": "16728",
            "main_thread_id": "16728",
            "on_main_thread": True,
            "request_thread_id": "22222",
            "ownership_id": "must-not-be-reported",
        }
        report = self.mock_online(status, ping)

        self.assertEqual(report["status"], "warn")
        self.assertEqual(report["status_probe"]["status"], "warn")
        self.assertEqual(report["status_probe"]["pid"], 8848)
        self.assertEqual(report["status_probe"]["ownership_state"], "UNKNOWN_FROZEN")
        self.assertTrue(report["status_probe"]["writes_halted"])
        self.assertEqual(report["status_probe"]["main_thread_id"], "16728")
        self.assertEqual(report["ping_probe"]["nonce"], diagnostics.DIAGNOSTIC_NONCE)
        self.assertEqual(report["ping_probe"]["callback_thread_id"], "16728")
        self.assertTrue(report["ping_probe"]["on_main_thread"])
        serialized = json.dumps(report)
        self.assertNotIn("ownership_id", serialized)
        self.assertNotIn("binding_epoch", serialized)
        self.assertNotIn("binding_identity", serialized)
        self.assertNotIn("request_thread_id", serialized)

    def test_online_report_fails_when_ping_is_not_on_nx_main_thread(self):
        status = {
            "pid": 8848,
            "ownership_state": "HUMAN_OWNED",
            "writes_halted": False,
            "main_thread_id": "16728",
        }
        ping = {
            "nonce": diagnostics.DIAGNOSTIC_NONCE,
            "callback_thread_id": "22222",
            "on_main_thread": False,
        }

        report = self.mock_online(status, ping)

        self.assertEqual(report["status"], "fail")
        self.assertEqual(report["ping_probe"]["status"], "fail")
        self.assertFalse(report["ping_probe"]["on_main_thread"])
        self.assertEqual(report["ping_probe"]["callback_thread_id"], "22222")
        self.assertIn("main thread", report["ping_probe"]["message"])

    def mock_online(self, status: dict, ping: dict):
        events = [
            {"id": 1, "result": {"protocolVersion": diagnostics.PROTOCOL_VERSION}},
            {"id": 2, "result": {"isError": False, "structuredContent": status}},
            {"id": 3, "result": {"isError": False, "structuredContent": ping}},
        ]
        completed = SimpleNamespace(
            stdout="".join(json.dumps(event) + "\n" for event in events),
            returncode=0,
        )
        with patch.object(diagnostics.subprocess, "run", return_value=completed):
            return diagnostics._online_probe(self.config_path, SimpleNamespace(timeout_seconds=0.2))

    def test_report_file_is_new_only_and_stdout_remains_json(self):
        output = self.root / "reports" / "first.json"
        output.parent.mkdir()
        completed, report = self.run_diagnostic("--report", str(output))

        self.assertEqual(completed.returncode, 0, report)
        self.assertEqual(json.loads(output.read_text(encoding="utf-8"))["overall"], "warn")
        self.assertEqual(report["saved_report"], str(output.resolve()))
        output.write_text("keep this file", encoding="utf-8")

        second, existing_report = self.run_diagnostic("--report", str(output))

        self.assertEqual(second.returncode, 1)
        self.assertEqual(output.read_text(encoding="utf-8"), "keep this file")
        self.assertEqual(existing_report["report_save"]["status"], "fail")


if __name__ == "__main__":
    unittest.main()
