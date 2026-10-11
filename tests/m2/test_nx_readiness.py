"""Offline connection lifecycle checks; all NX launches are mocked."""

from __future__ import annotations

from dataclasses import replace
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import Mock, patch


PROJECT_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT_ROOT / "src"))
from nx_sketch_mcp.bridge import BridgeFailure, JsonlBridge
from nx_sketch_mcp.config import ConfigError, config_from_object, load_config
from nx_sketch_mcp.readiness import NxReadiness, _startup_lock
from nx_sketch_mcp.server import McpServer, TOOLS


class NxReadinessTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="nx-readiness-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.private = self.root / "sessions" / ("a" * 32) / "private"
        self.private.mkdir(parents=True)
        self.startup = self.root / "nx-profile" / "startup"
        self.startup.mkdir(parents=True)
        self.nx = self.root / "ugraf.exe"
        self.helper = self.root / "M2JsonlHelper.exe"
        for path in (self.nx, self.helper, self.root / "M2Entry.dll", self.root / "M2Bridge.dll",
                     self.startup / "M2Startup.dll"):
            path.write_bytes(b"offline fixture, never executed")
        self.raw = {
            "pipe_name": "nx-ready-test", "token_file": str(self.private / "bridge.token"),
            "helper_path": str(self.helper), "timeout_seconds": 5,
            "launch": {"nx_executable": str(self.nx), "startup_directory": str(self.startup),
                       "ready_timeout_seconds": 1},
        }
        self.config_path = self.root / "mcp-config.json"
        self.config_path.write_text(json.dumps(self.raw), encoding="utf-8")
        self.config = load_config(self.config_path)
        self.bridge = Mock(spec=JsonlBridge)

    def disconnected(self):
        return BridgeFailure("BRIDGE_NOT_READY", "offline fixture")

    def test_old_four_fields_remain_compatible_and_launch_validation_is_strict(self):
        raw = {key: value for key, value in self.raw.items() if key != "launch"}
        self.assertIsNone(config_from_object(raw).launch)
        self.assertIsNone(config_from_object({}).launch)
        self.assertEqual(self.config.config_path, self.config_path.resolve())
        for changed in ({"launch": None}, {"launch": {}}, {"launch": {**self.raw["launch"], "extra": 1}},
                        {"launch": {**self.raw["launch"], "startup_directory": str(self.root)}},
                        *({"launch": {**self.raw["launch"], "ready_timeout_seconds": invalid}}
                          for invalid in (True, "30", 0, 31, float("nan"), float("inf")))):
            with self.subTest(changed=changed), self.assertRaises(ConfigError):
                config_from_object({**self.raw, **changed})
        with self.assertRaises(ConfigError):
            config_from_object({"launch": self.raw["launch"]})

    def test_connected_session_is_reused_without_launch_or_ownership_change(self):
        self.bridge.call.return_value = {"ownership_state": "HUMAN_OWNED", "pid": 42}
        readiness = NxReadiness(self.config, self.bridge)
        with patch.object(readiness, "_launch") as launch:
            result = readiness.ensure_ready()
        self.assertEqual(result["state"], "READY")
        self.assertTrue(result["reused"])
        self.assertFalse(result["ownership_changed"])
        launch.assert_not_called()
        self.bridge.reset_connection.assert_not_called()
        self.assertEqual(self.bridge.call.call_args.args, ("bridge.status", {}))

    def test_unconfigured_launch_has_manual_path_and_does_not_spawn(self):
        self.bridge.call.side_effect = self.disconnected()
        readiness = NxReadiness(replace(self.config, launch=None), self.bridge)
        with patch("nx_sketch_mcp.readiness.subprocess.run") as spawned:
            result = readiness.ensure_ready()
        self.assertEqual(result["code"], "LAUNCH_UNCONFIGURED")
        spawned.assert_not_called()

    def test_startup_loads_bridge_and_preserves_human_ownership(self):
        self.bridge.call.side_effect = [self.disconnected(), self.disconnected(), self.disconnected(), {"ownership_state": "HUMAN_OWNED"}]
        readiness = NxReadiness(self.config, self.bridge)
        with patch.object(readiness, "_launch", return_value={"state": "STARTED", "nx_pid": 123,
                                                             "process_start_ticks": "456"}) as launch:
            result = readiness.ensure_ready()
        self.assertTrue(result["ready"])
        self.assertFalse(result["reused"])
        self.assertEqual(result["bridge_status"]["ownership_state"], "HUMAN_OWNED")
        launch.assert_called_once()
        self.assertGreaterEqual(self.bridge.reset_connection.call_count, 2)
        self.assertTrue(all(call.args == ("bridge.status", {}) for call in self.bridge.call.call_args_list))

    def test_timeout_records_process_and_retry_reuses_identity_without_duplicate_start(self):
        self.bridge.call.side_effect = self.disconnected()
        readiness = NxReadiness(self.config, self.bridge)
        clock = [0.0]
        def advance(seconds):
            clock[0] += seconds
        with patch("nx_sketch_mcp.readiness.time.monotonic", side_effect=lambda: clock[0]), \
             patch("nx_sketch_mcp.readiness.time.sleep", side_effect=advance), \
             patch.object(readiness, "_launch", return_value={"state": "STARTED", "nx_pid": 123,
                                                             "process_start_ticks": "456"}) as launch:
            result = readiness.ensure_ready()
            self.assertEqual(result["state"], "STARTING")
            self.assertEqual(result["code"], "READY_TIMEOUT")
            self.assertLessEqual(clock[0], 1.0)
            launch.assert_called_once()
            state_file = next(self.private.glob("ready-*.json"))
            self.assertEqual(json.loads(state_file.read_text()), {"nx_pid": 123, "process_start_ticks": "456"})
            self.bridge.call.side_effect = [self.disconnected(), self.disconnected(), self.disconnected(), {"ownership_state": "HUMAN_OWNED"}]
            launch.return_value = {"state": "EXISTING_MANAGED", "nx_pid": 123, "process_start_ticks": "456"}
            result = readiness.ensure_ready()
            self.assertTrue(result["reused"])
            self.assertEqual(launch.call_args.args[1], {"nx_pid": 123, "process_start_ticks": "456"})

    def test_existing_user_nx_returns_manual_action_without_polling_or_state_creation(self):
        self.bridge.call.side_effect = self.disconnected()
        readiness = NxReadiness(self.config, self.bridge)
        with patch.object(readiness, "_launch", return_value={"state": "NEEDS_MANUAL_ACTION", "code": "EXISTING_NX"}):
            result = readiness.ensure_ready()
        self.assertEqual(result["code"], "EXISTING_NX")
        self.assertEqual(self.bridge.call.call_count, 3)
        self.assertFalse(list(self.private.glob("ready-*.json")))

    def test_startup_error_is_reported_only_for_current_process(self):
        self.bridge.call.side_effect = self.disconnected()
        readiness = NxReadiness(self.config, self.bridge)
        error_file = self.private / "startup.last_error.txt"
        error_file.write_text("999\nold startup error", encoding="utf-8")
        self.assertIsNone(readiness._startup_error(123, "456"))
        error_file.write_text("123\nbridge cannot load", encoding="utf-8")
        with patch.object(readiness, "_launch", return_value={"state": "STARTED", "nx_pid": 123,
                                                             "process_start_ticks": "456"}):
            result = readiness.ensure_ready()
        self.assertEqual(result["code"], "STARTUP_LOAD_FAILED")
        self.assertIn("bridge cannot load", result["message"])

    def test_stale_error_with_reused_pid_is_ignored_by_creation_time(self):
        readiness = NxReadiness(self.config, self.bridge)
        error_file = self.private / "startup.last_error.txt"
        error_file.write_text("123\nstale startup error", encoding="utf-8")
        created_ns = error_file.stat().st_mtime_ns + 10_000_000_000
        future_ticks = str(created_ns // 100 + 621355968000000000)
        self.assertIsNone(readiness._startup_error(123, future_ticks))
        current_ticks = str((error_file.stat().st_mtime_ns - 1_000_000) // 100 + 621355968000000000)
        self.assertEqual(readiness._startup_error(123, current_ticks)["code"], "STARTUP_LOAD_FAILED")

    def test_fresh_diagnostic_after_stale_token_failure_reuses_session_without_launch(self):
        self.bridge.call.side_effect = [BridgeFailure("AUTHORIZATION_REQUIRED", "stale token"),
                                        {"ownership_state": "HUMAN_OWNED"}]
        readiness = NxReadiness(self.config, self.bridge)
        with patch.object(readiness, "_launch") as launch:
            self.assertTrue(readiness.ensure_ready()["reused"])
        self.bridge.reset_connection.assert_called_once_with()
        launch.assert_not_called()

    def test_additional_startup_plugin_and_wrong_executable_are_rejected(self):
        plugin = self.startup / "user-plugin.dll"
        plugin.write_bytes(b"offline fixture")
        with self.assertRaises(ConfigError):
            config_from_object(self.raw)
        plugin.unlink()
        wrong_executable = self.root / "other.exe"
        wrong_executable.write_bytes(b"offline fixture")
        with self.assertRaises(ConfigError):
            config_from_object({**self.raw, "launch": {**self.raw["launch"], "nx_executable": str(wrong_executable)}})

    def test_mutex_excludes_a_second_client_process_and_releases_after_owner_exit(self):
        if os.name != "nt":
            self.skipTest("Windows byte locking is required")
        lock_file = self.private / "cross-process.lock"
        program = "from pathlib import Path; import sys; from nx_sketch_mcp.readiness import _startup_lock; " \
                  "ctx = _startup_lock(Path(sys.argv[1])); acquired = ctx.__enter__(); print(acquired); ctx.__exit__(None,None,None)"
        env = {**os.environ, "PYTHONPATH": str(PROJECT_ROOT / "src"), "PYTHONDONTWRITEBYTECODE": "1"}
        def second_client():
            return subprocess.run([sys.executable, "-B", "-c", program, str(lock_file)],
                                  env=env, capture_output=True, text=True, timeout=5)
        with _startup_lock(lock_file) as acquired:
            self.assertTrue(acquired)
            attempted = second_client()
            self.assertEqual(attempted.returncode, 0, attempted.stderr)
            self.assertEqual(attempted.stdout.strip(), "False")
        attempted = second_client()
        self.assertEqual(attempted.stdout.strip(), "True")

    def test_busy_mutex_does_not_launch(self):
        self.bridge.call.side_effect = self.disconnected()
        readiness = NxReadiness(self.config, self.bridge)
        from contextlib import contextmanager
        @contextmanager
        def busy(_path):
            yield False
        with patch("nx_sketch_mcp.readiness._startup_lock", busy), patch.object(readiness, "_launch") as launch:
            result = readiness.ensure_ready()
        self.assertEqual(result["code"], "STARTUP_IN_PROGRESS")
        launch.assert_not_called()

    def test_malformed_launcher_output_and_timeout_are_bounded(self):
        readiness = NxReadiness(self.config, self.bridge)
        with patch("nx_sketch_mcp.readiness.subprocess.run", return_value=Mock(stdout=b"not JSON", returncode=0)):
            self.assertEqual(readiness._launch(100.0 + time.monotonic(), {})["code"], "LAUNCH_PROTOCOL_ERROR")
        with patch("nx_sketch_mcp.readiness.subprocess.run", side_effect=subprocess.TimeoutExpired("mock", 1)):
            self.assertEqual(readiness._launch(100.0 + time.monotonic(), {})["code"], "LAUNCH_TIMEOUT")

    def test_startup_identity_is_published_by_atomic_replace(self):
        self.bridge.call.side_effect = [self.disconnected(), self.disconnected(), self.disconnected(),
                                        {"ownership_state": "HUMAN_OWNED"}]
        readiness = NxReadiness(self.config, self.bridge)
        original_replace = os.replace
        observed = []
        def publish(source, target):
            observed.append((Path(source), Path(target)))
            self.assertEqual(json.loads(Path(source).read_text()), {"nx_pid": 123, "process_start_ticks": "456"})
            original_replace(source, target)
        with patch.object(readiness, "_launch", return_value={"state": "STARTED", "nx_pid": 123,
                                                             "process_start_ticks": "456"}), \
             patch("nx_sketch_mcp.readiness.os.replace", side_effect=publish):
            self.assertTrue(readiness.ensure_ready()["ready"])
        self.assertEqual(len(observed), 1)
        self.assertEqual(observed[0][0].parent, observed[0][1].parent)
        self.assertFalse(observed[0][0].exists())

    def test_launcher_reuses_only_matching_process_creation_time(self):
        if os.name != "nt":
            self.skipTest("Windows launcher is required")
        launcher = PROJECT_ROOT / "tools/start-nx-global.ps1"
        wrapper = self.root / "mock-existing-nx.ps1"
        wrapper.write_text(
            "param([string]$Launcher,[string]$Config,[string]$NxExe,[string]$Ticks)\n"
            "function Get-Process { param($Name,$ErrorAction) "
            "[pscustomobject]@{Id=123;Path=$NxExe;StartTime=[datetime]'2024-01-01T00:00:00Z'} }\n"
            "function Start-Process { throw 'Unexpected NX launch in offline test' }\n"
            "& $Launcher -Config $Config -NxExe $NxExe -ExistingProcessId 123 -ExistingStartTicks $Ticks -AsJson\n",
            encoding="utf-8",
        )
        for ticks, expected in (("638396640000000000", "EXISTING_MANAGED"), ("1", "NEEDS_MANUAL_ACTION")):
            completed = subprocess.run(
                [r"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(wrapper),
                 "-Launcher", str(launcher), "-Config", str(self.config_path), "-NxExe", str(self.nx), "-Ticks", ticks],
                capture_output=True, timeout=10,
            )
            self.assertEqual(completed.returncode, 0, completed.stderr.decode(errors="replace"))
            payload = json.loads(completed.stdout.decode("utf-8-sig"))
            self.assertEqual(payload["state"], expected, payload)
        self.assertFalse(self.config.token_file.exists())

    def test_global_launcher_mutex_blocks_different_profile_and_direct_launcher(self):
        if os.name != "nt":
            self.skipTest("Windows named mutex is required")
        powershell = r"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"
        keeper_source = "$mutex=[Threading.Mutex]::new($false,('Global\\NxSketchMcpDedicatedLaunch-' + " \
                        "[Security.Principal.WindowsIdentity]::GetCurrent().User.Value)); " \
                        "if (-not $mutex.WaitOne(0)) { exit 2 }; " \
                        "[Console]::Out.WriteLine('READY'); [Console]::In.ReadLine() | Out-Null; " \
                        "$mutex.ReleaseMutex(); $mutex.Dispose()"
        keeper = subprocess.Popen([powershell, "-NoProfile", "-Command", keeper_source],
                                  stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        try:
            self.assertEqual(keeper.stdout.readline().strip(), b"READY")
            for pipe in ("first-profile", "different-profile"):
                self.config_path.write_text(json.dumps({**self.raw, "pipe_name": pipe}), encoding="utf-8")
                completed = subprocess.run(
                    [powershell, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(PROJECT_ROOT / "tools/start-nx-global.ps1"),
                     "-Config", str(self.config_path), "-AsJson"], capture_output=True, timeout=10,
                )
                self.assertEqual(completed.returncode, 0, completed.stderr.decode(errors="replace"))
                payload = json.loads(completed.stdout.decode("utf-8-sig"))
                self.assertEqual(payload["state"], "STARTING")
                self.assertEqual(payload["code"], "STARTUP_IN_PROGRESS")
        finally:
            if keeper.poll() is None:
                keeper.stdin.write(b"release\n")
                keeper.stdin.flush()
                keeper.wait(timeout=5)
            for stream in (keeper.stdin, keeper.stdout, keeper.stderr):
                stream.close()

    def test_launcher_refuses_readonly_and_hidden_stale_tokens_without_spawning(self):
        if os.name != "nt":
            self.skipTest("Windows file attributes and launcher are required")
        import ctypes
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.SetFileAttributesW.argtypes = (ctypes.c_wchar_p, ctypes.c_uint32)
        kernel.SetFileAttributesW.restype = ctypes.c_int
        copied_launcher = self.root / "test-launcher.ps1"
        copied_launcher.write_text((PROJECT_ROOT / "tools/start-nx-global.ps1").read_text(), encoding="utf-8")
        (self.root / "verify-m2-local-network.ps1").write_text("param([string]$NxExe)\n'{}'\n", encoding="utf-8")
        wrapper = self.root / "mock-no-running-nx.ps1"
        wrapper.write_text(
            "param([string]$Launcher,[string]$Config)\n"
            "function Get-Process { param($Name,$ErrorAction) @() }\n"
            "function Start-Process { throw 'Unexpected NX launch in offline test' }\n"
            "& $Launcher -Config $Config -AsJson\n", encoding="utf-8",
        )
        token = self.config.token_file
        token.write_bytes(b"offline stale token")
        try:
            for attributes in (0x1, 0x2):
                self.assertTrue(kernel.SetFileAttributesW(str(token), attributes))
                completed = subprocess.run(
                    [r"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass",
                     "-File", str(wrapper), "-Launcher", str(copied_launcher), "-Config", str(self.config_path)],
                    capture_output=True, timeout=10,
                )
                self.assertEqual(completed.returncode, 0, completed.stderr.decode(errors="replace"))
                payload = json.loads(completed.stdout.decode("utf-8-sig"))
                self.assertEqual(payload["state"], "NEEDS_MANUAL_ACTION", payload)
                self.assertEqual(payload["code"], "SESSION_TOKEN_PROTECTED", payload)
                self.assertEqual(token.read_bytes(), b"offline stale token")
        finally:
            kernel.SetFileAttributesW(str(token), 0x80)

    def test_explicit_tool_rejects_arguments_and_preserves_unknown_write_gate(self):
        server = McpServer(self.config)
        self.addCleanup(server.close)
        server._unknown_operation_ids.add("previous-unknown-write")
        with patch("nx_sketch_mcp.server.NxReadiness") as lifecycle:
            lifecycle.return_value.ensure_ready.return_value = {
                "state": "READY", "ready": True,
                "bridge_status": {"ownership_state": "AI_OWNED", "model_write_enabled": True, "writes_halted": False},
            }
            invalid = server._tools_call(1, {"name": "nx_ensure_ready", "arguments": {"launch": True}})
            self.assertEqual(invalid["error"]["code"], -32602)
            lifecycle.assert_not_called()
            valid = server._tools_call(2, {"name": "nx_ensure_ready", "arguments": {}})
            self.assertEqual(valid["result"]["structuredContent"]["state"], "READY")
            status = valid["result"]["structuredContent"]["bridge_status"]
            self.assertFalse(status["model_write_enabled"])
            self.assertTrue(status["writes_halted"])
            self.assertEqual(status["write_halt_reason"], "CLIENT_OUTCOME_UNKNOWN")
            self.assertEqual(status["unknown_operation_ids"], ["previous-unknown-write"])
            self.assertEqual(server._unknown_operation_ids, {"previous-unknown-write"})
        tools = {item["name"]: item for item in TOOLS}
        self.assertEqual(len(tools), 20)
        self.assertFalse(tools["nx_ensure_ready"]["annotations"]["readOnlyHint"])

    def test_helper_reset_disposes_only_own_helper_and_does_not_replay_calls(self):
        bridge = JsonlBridge(self.config)
        process = Mock()
        bridge._process = process
        with patch.object(bridge, "_dispose_process") as dispose, patch.object(bridge, "call") as call:
            bridge.reset_connection()
        dispose.assert_called_once_with(process, graceful=False)
        call.assert_not_called()


if __name__ == "__main__":
    unittest.main()
