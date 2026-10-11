"""Real M2 bridge/helper/MCP subsystem checks using the production assemblies."""

from __future__ import annotations

import json
import os
from pathlib import Path
import secrets
import subprocess
import sys
import tempfile
import threading
import time
import unittest
import uuid

from stdio_client import StdioMcpProcess, assert_stdout_jsonl_only
from test_mcp_stdio import extract_structured_result
from evidence_recorder import record


PROJECT_ROOT = Path(__file__).resolve().parents[2]
BUILD_DIR = PROJECT_ROOT / "workspace" / "build" / "current" / "M2"
BRIDGE_DLL = BUILD_DIR / "M2Bridge.dll"
HELPER_EXE = BUILD_DIR / "M2JsonlHelper.exe"
BUILD_HARNESS_PS1 = Path(__file__).resolve().parent / "build_harness.ps1"
PROTOCOL_VERSION = "2025-11-25"


class M2BridgeSubsystemTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        missing = [str(path) for path in (BRIDGE_DLL, HELPER_EXE) if not path.is_file()]
        if missing:
            raise FileNotFoundError("production M2 build output missing: " + ", ".join(missing))
        cls.build_temp = tempfile.TemporaryDirectory(prefix="nx-m2-harness-build-")
        cls.harness_exe = Path(cls.build_temp.name) / "M2StaHarness.exe"
        completed = subprocess.run(
            ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
             str(BUILD_HARNESS_PS1), "-OutputExe", str(cls.harness_exe),
             "-BridgeAssembly", str(BRIDGE_DLL)],
            cwd=str(PROJECT_ROOT), capture_output=True, text=True, encoding="utf-8", timeout=40,
        )
        if completed.returncode != 0:
            raise RuntimeError("STA harness build failed: " + completed.stderr[-1000:])
        if not cls.harness_exe.is_file():
            raise FileNotFoundError("STA harness executable was not produced")

    @classmethod
    def tearDownClass(cls):
        cls.build_temp.cleanup()

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="nx-m2-run-")
        self.temp_path = Path(self.temp.name)
        self.pipe_name = "nx-m2-test-" + uuid.uuid4().hex
        self.token_file = self.temp_path / "server-token.txt"
        self.wrong_token_file = self.temp_path / "wrong-token.txt"
        self.harness = None
        self.addCleanup(self.temp.cleanup)

    def start_harness(self):
        self.assertFalse(self.token_file.exists(), "bridge-generated token should not pre-exist")
        self.harness = StdioMcpProcess(
            [str(self.harness_exe), str(BRIDGE_DLL), self.pipe_name, str(self.token_file)],
            cwd=str(PROJECT_ROOT),
        )
        self.addCleanup(self._cleanup_harness)
        try:
            ready = self.harness.receive_json(timeout=10.0)
        except Exception as exc:
            stderr = b"".join(self.harness.drain_stderr()).decode("utf-8", errors="replace")
            raise RuntimeError(f"STA harness did not start: {exc}; stderr={stderr}") from exc
        self.assertEqual(ready.get("event"), "ready", ready)
        self.assertEqual(ready.get("apartment"), "STA", ready)
        self.assertFalse(ready.get("references_nxopen"), ready)
        self.assertEqual(Path(ready.get("assembly_path", "")).resolve(), BRIDGE_DLL.resolve())
        self.assertGreater(int(ready.get("pid", 0)), 0)
        self.assertGreater(int(ready.get("main_thread_id", 0)), 0)
        self.assertTrue(self.token_file.is_file(), "M2BridgeServer.Start should create its protected token file")
        self.ready = ready
        record("M2-2-01", {"bridge_assembly": ready.get("assembly_path"),
                           "bridge_assembly_version": ready.get("assembly_version"),
                           "harness_pid": ready.get("pid"), "main_os_thread_id": ready.get("main_thread_id"),
                           "sta_message_loop": ready.get("apartment") == "STA",
                           "bridge_references_nxopen": ready.get("references_nxopen"),
                           "server_token_file_created": self.token_file.is_file()})
        return ready

    def start_helper(self, token_file: Path | None = None):
        token_file = token_file or self.token_file
        helper = StdioMcpProcess(
            [str(HELPER_EXE), "--pipe-name", self.pipe_name, "--token-file", str(token_file)],
            cwd=str(PROJECT_ROOT),
        )
        self.addCleanup(lambda child=helper: self._cleanup_child(child))
        return helper

    def start_host(self, *, pipe_name: str | None = None, token_file: Path | None = None,
                   timeout_seconds: float = 5.0):
        token_file = token_file or self.token_file
        config_path = self.temp_path / ("mcp-config-" + uuid.uuid4().hex + ".json")
        config = {
            "pipe_name": pipe_name or self.pipe_name,
            "token_file": str(token_file.resolve()),
            "helper_path": str(HELPER_EXE.resolve()),
            "timeout_seconds": timeout_seconds,
        }
        config_path.write_text(json.dumps(config, separators=(",", ":")) + "\n", encoding="utf-8")
        self.assertEqual(set(config), {"pipe_name", "token_file", "helper_path", "timeout_seconds"})
        env = os.environ.copy()
        existing = env.get("PYTHONPATH", "")
        paths = [str(PROJECT_ROOT / "src"), str(PROJECT_ROOT)]
        if existing:
            paths.append(existing)
        env["PYTHONPATH"] = os.pathsep.join(paths)
        env["PYTHONUTF8"] = "1"
        host = StdioMcpProcess(
            [sys.executable, "-m", "nx_sketch_mcp", "--config", str(config_path)],
            cwd=str(PROJECT_ROOT), env=env,
        )
        self.addCleanup(lambda child=host: self._cleanup_child(child))
        init = host.request("initialize", {
            "protocolVersion": PROTOCOL_VERSION,
            "capabilities": {},
            "clientInfo": {"name": "m2-subsystem-validation", "version": "1"},
        })
        self.assertEqual(init.get("result", {}).get("protocolVersion"), PROTOCOL_VERSION, init)
        host.notification("notifications/initialized")
        host.assert_quiet(duration=0.15)
        return host, config

    def test_production_helper_dispatch_and_mcp_round_trip(self):
        ready = self.start_harness()
        server_token = self.token_file.read_text(encoding="utf-8").strip()
        self.assertEqual(len(server_token), 64)

        helper = self.start_helper()
        direct_status = helper.exchange(
            {"id": "direct-status", "command": "bridge.status", "params": {}},
            expected_id="direct-status", timeout=5.0,
        )
        status = successful_helper_result(self, direct_status)
        self.assertEqual(status.get("service"), "nx-sketch-mcp-m2")
        self.assertEqual(status.get("state"), "loaded")
        self.assertEqual(status.get("pid"), ready.get("pid"))
        self.assertEqual(status.get("main_thread_id"), str(ready.get("main_thread_id")))
        self.assertEqual(status.get("nx_api_calls"), 0)

        unknown = helper.exchange(
            {"id": "direct-unknown", "command": "bridge.not_supported", "params": {}},
            expected_id="direct-unknown", timeout=5.0,
        )
        self.assertNotIn("result", unknown, unknown)
        self.assertEqual(unknown.get("error", {}).get("code"), "UNKNOWN_COMMAND", unknown)

        direct_probes = []
        for request_id, nonce in (("direct-probe-1", "probe-first"), ("direct-probe-2", "probe-second")):
            reply = helper.exchange(
                {"id": request_id, "command": "bridge.dispatch_probe", "params": {"nonce": nonce}},
                expected_id=request_id, timeout=5.0,
            )
            probe = successful_helper_result(self, reply)
            assert_probe(self, probe, nonce)
            self.assertEqual(probe.get("main_thread_id"), str(ready.get("main_thread_id")))
            direct_probes.append(probe)
        self.assertNotEqual(direct_probes[0]["nonce"], direct_probes[1]["nonce"])

        host, config = self.start_host()
        self.assertNotIn("manifest", config)
        status_response = call_tool(host, "nx_bridge_status", {})
        mcp_status = successful_tool_result(self, status_response)
        self.assertEqual(mcp_status.get("service"), status.get("service"))
        self.assertEqual(mcp_status.get("pid"), status.get("pid"))
        self.assertEqual(mcp_status.get("nx_api_calls"), 0)

        unicode_nonces = (("草图-🧪", 4), ("界🙂" * 64, 128))
        for nonce, codepoint_count in unicode_nonces:
            self.assertEqual(len(nonce), codepoint_count)
            response = call_tool(host, "nx_bridge_ping", {"nonce": nonce})
            probe = successful_tool_result(self, response)
            assert_probe(self, probe, nonce)

        too_long = call_tool(host, "nx_bridge_ping", {"nonce": "🙂" * 129})
        self.assertEqual(too_long.get("error", {}).get("code"), -32602, too_long)

        host_exit, host_output = finish_child(self, host, forbidden_values=(server_token,))
        self.assertEqual(host_exit, 0)
        self.assertTrue(self.harness.process.poll() is None, "closing the MCP child must not stop the independent harness")

        # The independent direct helper still reaches the server after MCP EOF.
        still_alive = helper.exchange(
            {"id": "after-host-exit", "command": "bridge.status", "params": {}},
            expected_id="after-host-exit", timeout=5.0,
        )
        self.assertEqual(successful_helper_result(self, still_alive).get("state"), "loaded")
        helper_exit, helper_output = finish_child(self, helper, forbidden_values=(server_token,))
        self.assertEqual(helper_exit, 0)
        self.assertTrue(host_output)
        self.assertTrue(helper_output)

        # Stop is executed on the STA owner thread and removes its own private token file.
        stop_event = self._stop_harness()
        self.assertEqual(stop_event.get("event"), "stopped", stop_event)
        self.assertFalse(self.token_file.exists())
        record("M2-2-02/04", {"status": status, "direct_probe_results": direct_probes,
                              "mcp_probe_nonces": [nonce for nonce, _ in unicode_nonces],
                              "host_child_exit_code": host_exit, "helper_child_exit_code": helper_exit,
                              "host_stdout_jsonl_lines": len(host_output.splitlines()),
                              "helper_stdout_jsonl_lines": len(helper_output.splitlines()),
                              "stdout_secret_scan_passed": True,
                              "independent_harness_survived_host_eof": True,
                              "token_file_removed_by_sta_stop": True})

    def test_model_ownership_identity_failure_and_return_drain(self):
        ready = self.start_harness()
        first = self.start_helper()
        second = self.start_helper()

        human_read = first.exchange(
            {"id": "human-read", "command": "model.inspect_part",
             "params": {"ownership_id": "none", "binding_epoch": 1}},
            expected_id="human-read", timeout=5.0,
        )
        self.assertEqual(human_read.get("error", {}).get("code"), "AUTHORIZATION_REQUIRED", human_read)

        self.harness.send_raw_line("fake-counts")
        counts_before = self.harness.receive_json(timeout=5.0)
        self.assertEqual(counts_before.get("takeover_calls"), 0, counts_before)
        self.assertEqual(counts_before.get("inspection_calls"), 0, counts_before)

        wrong_pid = first.exchange(
            {"id": "wrong-pid", "command": "ownership.takeover", "params": {
                "expected_pid": int(ready["pid"]) + 1000,
                "expected_part_path": "C:\\M2Fake\\model.prt",
                "artifact_scope": "saved_independent_copy", "authorize": True}},
            expected_id="wrong-pid", timeout=5.0,
        )
        self.assertEqual(wrong_pid.get("error", {}).get("code"), "SESSION_MISMATCH", wrong_pid)

        wrong_path = first.exchange(
            {"id": "wrong-path", "command": "ownership.takeover", "params": {
                "expected_pid": int(ready["pid"]),
                "expected_part_path": "C:\\M2Fake\\wrong.prt",
                "artifact_scope": "saved_independent_copy", "authorize": True}},
            expected_id="wrong-path", timeout=5.0,
        )
        self.assertEqual(wrong_path.get("error", {}).get("code"), "SESSION_MISMATCH", wrong_path)

        takeover = first.exchange(
            {"id": "takeover-1", "command": "ownership.takeover", "params": {
                "expected_pid": int(ready["pid"]),
                "expected_part_path": "C:\\M2Fake\\model.prt",
                "artifact_scope": "saved_independent_copy", "authorize": True}},
            expected_id="takeover-1", timeout=5.0,
        )
        granted = successful_helper_result(self, takeover)
        owner = granted["ownership_id"]
        epoch = granted["binding_epoch"]
        self.assertEqual(granted.get("ownership_state"), "AI_OWNED", granted)
        self.assertEqual(granted.get("nx_build"), "NXOpen fixture build 1", granted)

        session = first.exchange(
            {"id": "session", "command": "model.get_session", "params": {
                "ownership_id": owner, "binding_epoch": epoch}},
            expected_id="session", timeout=5.0,
        )
        self.assertEqual(successful_helper_result(self, session).get("part_tag"), "fake-part-tag-1")

        disconnected_exit, _ = finish_child(self, first)
        self.assertEqual(disconnected_exit, 0)
        first = self.start_helper()
        recovered_status_response = first.exchange(
            {"id": "recover-status", "command": "bridge.status", "params": {}},
            expected_id="recover-status", timeout=5.0,
        )
        recovered_status = successful_helper_result(self, recovered_status_response)
        self.assertEqual(recovered_status.get("ownership_id"), owner, recovered_status)
        self.assertEqual(recovered_status.get("binding_epoch"), epoch, recovered_status)
        self.assertEqual(recovered_status.get("binding_identity", {}).get("part_path"),
                         "C:\\M2Fake\\model.prt", recovered_status)

        old_epoch = first.exchange(
            {"id": "stale-epoch", "command": "model.inspect_part", "params": {
                "ownership_id": owner, "binding_epoch": epoch + 1}},
            expected_id="stale-epoch", timeout=5.0,
        )
        self.assertEqual(old_epoch.get("error", {}).get("code"), "AUTHORIZATION_REQUIRED", old_epoch)

        inspect_response = {}
        inspect_failure = []

        def inspect_in_flight():
            try:
                inspect_response["value"] = first.exchange(
                    {"id": "in-flight-inspect", "command": "model.inspect_part", "params": {
                        "ownership_id": owner, "binding_epoch": epoch}},
                    expected_id="in-flight-inspect", timeout=8.0,
                )
            except Exception as exc:
                inspect_failure.append(exc)

        inspect_thread = threading.Thread(target=inspect_in_flight, name="m2-in-flight-inspect")
        inspect_thread.start()
        self.harness.send_raw_line("wait-inspect-start")
        started = self.harness.receive_json(timeout=7.0)
        self.assertIs(started.get("observed"), True, started)

        return_started = time.monotonic()
        returned = second.exchange(
            {"id": "return-1", "command": "ownership.return", "params": {
                "ownership_id": owner, "binding_epoch": epoch}},
            expected_id="return-1", timeout=8.0,
        )
        return_elapsed_ms = (time.monotonic() - return_started) * 1000.0
        inspect_thread.join(timeout=8.0)
        self.assertFalse(inspect_thread.is_alive(), "in-flight model read did not finish")
        self.assertFalse(inspect_failure, inspect_failure)
        inspected = successful_helper_result(self, inspect_response["value"])
        self.assertEqual(inspected.get("part_path"), "C:\\M2Fake\\model.prt", inspected)
        returned_value = successful_helper_result(self, returned)
        self.assertEqual(returned_value.get("ownership_state"), "HUMAN_OWNED", returned_value)
        self.assertIs(returned_value.get("safe_for_human"), True, returned_value)
        self.assertGreaterEqual(return_elapsed_ms, 600.0, return_elapsed_ms)

        old_owner = first.exchange(
            {"id": "old-owner", "command": "model.get_session", "params": {
                "ownership_id": owner, "binding_epoch": epoch}},
            expected_id="old-owner", timeout=5.0,
        )
        self.assertEqual(old_owner.get("error", {}).get("code"), "AUTHORIZATION_REQUIRED", old_owner)

        takeover2 = first.exchange(
            {"id": "takeover-2", "command": "ownership.takeover", "params": {
                "expected_pid": int(ready["pid"]),
                "expected_part_path": "C:\\M2Fake\\model.prt",
                "artifact_scope": "saved_independent_copy", "authorize": True}},
            expected_id="takeover-2", timeout=5.0,
        )
        granted2 = successful_helper_result(self, takeover2)
        owner2, epoch2 = granted2["ownership_id"], granted2["binding_epoch"]
        self.harness.send_raw_line("change-identity")
        identity_event = self.harness.receive_json(timeout=5.0)
        self.assertEqual(identity_event.get("event"), "identity-changed", identity_event)
        stale_binding = first.exchange(
            {"id": "identity-stale", "command": "model.inspect_part", "params": {
                "ownership_id": owner2, "binding_epoch": epoch2}},
            expected_id="identity-stale", timeout=5.0,
        )
        self.assertEqual(stale_binding.get("error", {}).get("code"), "STALE_BINDING", stale_binding)
        old_binding = first.exchange(
            {"id": "identity-old-binding", "command": "model.get_session", "params": {
                "ownership_id": owner2, "binding_epoch": epoch2}},
            expected_id="identity-old-binding", timeout=5.0,
        )
        self.assertEqual(old_binding.get("error", {}).get("code"), "AUTHORIZATION_REQUIRED", old_binding)

        record("M2-3-4-STA-model-ownership", {
            "human_state_model_read_error": human_read["error"]["code"],
            "takeover_wrong_pid_error": wrong_pid["error"]["code"],
            "takeover_wrong_path_error": wrong_path["error"]["code"],
            "takeover_state": granted["ownership_state"],
            "recovered_after_helper_disconnect": True,
            "in_flight_inspection_passed": True,
            "return_drain_elapsed_ms": round(return_elapsed_ms, 3),
            "return_state": returned_value["ownership_state"],
            "old_owner_error": old_owner["error"]["code"],
            "identity_change_error": stale_binding["error"]["code"],
            "old_binding_error": old_binding["error"]["code"],
        })

    def test_model_read_exception_is_an_explicit_bridge_failure(self):
        ready = self.start_harness()
        helper = self.start_helper()
        takeover = helper.exchange(
            {"id": "takeover", "command": "ownership.takeover", "params": {
                "expected_pid": int(ready["pid"]),
                "expected_part_path": "C:\\M2Fake\\model.prt",
                "artifact_scope": "saved_independent_copy", "authorize": True}},
            expected_id="takeover", timeout=5.0,
        )
        grant = successful_helper_result(self, takeover)
        failed = helper.exchange(
            {"id": "read-failure", "command": "model.inspect_sketch", "params": {
                "ownership_id": grant["ownership_id"], "binding_epoch": grant["binding_epoch"],
                "sketch_id": "raise"}},
            expected_id="read-failure", timeout=5.0,
        )
        self.assertNotIn("result", failed, failed)
        self.assertEqual(failed.get("error", {}).get("code"), "MODEL_READ_FAILED", failed)
        self.assertIn("fixture inspection failure", failed.get("error", {}).get("message", ""), failed)
        returned = helper.exchange(
            {"id": "return", "command": "ownership.return", "params": {
                "ownership_id": grant["ownership_id"], "binding_epoch": grant["binding_epoch"]}},
            expected_id="return", timeout=5.0,
        )
        self.assertIs(successful_helper_result(self, returned).get("safe_for_human"), True)
        record("M2-3-4-STA-model-read-error", {"error_code": failed["error"]["code"],
                                                "message": failed["error"]["message"],
                                                "returned_to_human": True})

    def test_parameter_plan_idempotency_rejections_and_stale_revision(self):
        ready = self.start_harness()
        helper = self.start_helper()
        host, _ = self.start_host(timeout_seconds=10.0)

        takeover = successful_tool_result(self, call_tool(host, "nx_takeover", {
            "expected_pid": int(ready["pid"]),
            "expected_part_path": "C:\\M2Fake\\model.prt",
            "artifact_scope": "saved_independent_copy", "authorize": True,
        }))
        owner, epoch = takeover["ownership_id"], takeover["binding_epoch"]
        session = successful_tool_result(self, call_tool(host, "nx_get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))

        prepare_id = "prepare-" + uuid.uuid4().hex
        prepare_args = operation_prepare_args(prepare_id, owner, epoch, session["revision_id"], value="12.5")
        first_prepare = successful_tool_result(self, call_tool(host, "nx_prepare_operation", prepare_args))
        self.assertEqual(first_prepare.get("state"), "SUCCEEDED", first_prepare)
        plan = first_prepare.get("result", {}).get("plan")
        self.assertIsInstance(plan, dict, first_prepare)
        self.assertEqual(plan.get("schema"), "m2-parameter-edit-v1", plan)
        self.assertEqual(plan.get("plan_kind"), "set_driving_dimension_value", plan)
        self.assertEqual(plan.get("before", {}).get("value"), "10", plan)
        self.assertEqual(plan.get("requested"), {"value": "12.5", "unit": "mm"}, plan)
        self.assertEqual(plan.get("target", {}).get("expression_name"), "p11", plan)
        self.assertEqual(plan.get("target", {}).get("geometry_refs"), ["line-1", "line-2"], plan)

        # Same normalized decimal and a different JSON property order are the same operation request.
        retry_args = dict(reversed(list(prepare_args.items())))
        retry_args["value"] = "12.500"
        retried_prepare = self.helper_call(helper, "operation.prepare", retry_args)
        retry_result = successful_helper_result(self, retried_prepare)
        self.assertEqual(retry_result.get("state"), "SUCCEEDED", retry_result)
        self.assertEqual(retry_result.get("result"), first_prepare.get("result"), retry_result)

        conflicting_args = dict(prepare_args)
        conflicting_args["value"] = "13"
        conflict = self.helper_call(helper, "operation.prepare", conflicting_args)
        self.assertEqual(conflict.get("error", {}).get("code"), "OPERATION_ID_CONFLICT", conflict)
        counts = self.fake_counts()
        self.assertEqual(counts.get("prepare_calls"), 1, counts)
        self.assertEqual(counts.get("mutation_count"), 0, counts)

        plan_id = plan["plan_id"]
        execute_id = "execute-" + uuid.uuid4().hex
        execute_args = {
            "operation_id": execute_id, "ownership_id": owner,
            "binding_epoch": epoch, "plan_id": plan_id,
        }
        tampered = dict(execute_args)
        tampered["value"] = "99"
        rejected_override = self.helper_call(helper, "operation.execute_plan", tampered)
        self.assertEqual(rejected_override.get("error", {}).get("code"), "INVALID_REQUEST", rejected_override)

        accepted_execute = successful_tool_result(self, call_tool(host, "nx_execute_plan", execute_args))
        self.assertIn(accepted_execute.get("state"), {"QUEUED", "RUNNING", "SUCCEEDED"}, accepted_execute)
        completed_execute = self.wait_operation(helper, execute_id)
        self.assertEqual(completed_execute.get("state"), "SUCCEEDED", completed_execute)
        self.assertEqual(completed_execute.get("result", {}).get("mutation_state"), "APPLIED", completed_execute)
        self.assertIs(completed_execute.get("result", {}).get("changed"), True, completed_execute)
        self.assertIs(completed_execute.get("result", {}).get("readback_verified"), True, completed_execute)
        self.assertEqual(completed_execute.get("result", {}).get("before", {}).get("value"), "10", completed_execute)
        self.assertEqual(completed_execute.get("result", {}).get("after", {}).get("value"), "12.5", completed_execute)

        replay_execute_args = dict(reversed(list(execute_args.items())))
        replay = successful_helper_result(self, self.helper_call(helper, "operation.execute_plan", replay_execute_args))
        self.assertEqual(replay.get("state"), "SUCCEEDED", replay)
        self.assertEqual(replay.get("result"), completed_execute.get("result"), replay)
        conflicting_execute = dict(execute_args)
        conflicting_execute["plan_id"] = "other-plan"
        conflict = self.helper_call(helper, "operation.execute_plan", conflicting_execute)
        self.assertEqual(conflict.get("error", {}).get("code"), "OPERATION_ID_CONFLICT", conflict)
        counts = self.fake_counts()
        self.assertEqual(counts.get("execution_calls"), 1, counts)
        self.assertEqual(counts.get("mutation_count"), 1, counts)
        self.assertEqual(counts.get("current_value"), "12.5", counts)

        # The adapter fixture rejects non-driving dimensions, unsupported types, and incompatible units.
        current_session = successful_tool_result(self, call_tool(host, "nx_get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        rejection_cases = (
            ("unit", "constraint-1", "in", "UNIT_MISMATCH"),
            ("driving", "non-driving", "mm", "NON_DRIVING_DIMENSION"),
            ("ambiguous", "ambiguous", "mm", "AMBIGUOUS_CONSTRAINT"),
            ("support", "unsupported", "mm", "UNSUPPORTED_DIMENSION_TYPE"),
        )
        for label, constraint_id, unit, error_code in rejection_cases:
            rejected_args = operation_prepare_args(
                "reject-" + label + "-" + uuid.uuid4().hex, owner, epoch,
                current_session["revision_id"], value="20", unit=unit, constraint_id=constraint_id,
            )
            rejected = successful_helper_result(self, self.helper_call(helper, "operation.prepare", rejected_args))
            self.assertEqual(rejected.get("state"), "FAILED", rejected)
            self.assertEqual(rejected.get("error", {}).get("code"), error_code, rejected)
            self.assertIsNone(rejected.get("result"), rejected)

        stale_prepare_args = operation_prepare_args(
            "prepare-stale-" + uuid.uuid4().hex, owner, epoch,
            current_session["revision_id"], value="15",
        )
        stale_prepare = successful_helper_result(self, self.helper_call(helper, "operation.prepare", stale_prepare_args))
        self.assertEqual(stale_prepare.get("state"), "SUCCEEDED", stale_prepare)
        stale_plan = stale_prepare["result"]["plan"]
        revision_event = self.harness_command("change-revision")
        self.assertEqual(revision_event.get("event"), "revision-changed", revision_event)
        self.assertNotEqual(revision_event.get("revision_id"), stale_plan.get("revision_id"), revision_event)
        stale_execution_id = "execute-stale-" + uuid.uuid4().hex
        stale_execute_args = {
            "operation_id": stale_execution_id, "ownership_id": owner,
            "binding_epoch": epoch, "plan_id": stale_plan["plan_id"],
        }
        self.helper_call(helper, "operation.execute_plan", stale_execute_args)
        stale_result = self.wait_operation(helper, stale_execution_id)
        self.assertEqual(stale_result.get("state"), "FAILED", stale_result)
        self.assertEqual(stale_result.get("error", {}).get("code"), "REVISION_MISMATCH", stale_result)
        self.assertEqual(stale_result.get("result", {}).get("mutation_state"), "UNCHANGED", stale_result)
        counts = self.fake_counts()
        self.assertEqual(counts.get("mutation_count"), 1, counts)
        self.assertEqual(counts.get("current_value"), "12.5", counts)
        record("M2-5-6-STA-parameter-plan", {
            "tools": ["nx_prepare_operation", "nx_execute_plan", "nx_get_operation"],
            "prepare_retry_normalized_once": True,
            "prepare_parameter_conflict": "OPERATION_ID_CONFLICT",
            "execution_retry_mutation_count": counts["mutation_count"],
            "immutable_execute_override_error": "INVALID_REQUEST",
            "parameter_rejections": [item[3] for item in rejection_cases],
            "stale_revision_error": stale_result["error"]["code"],
        })

    def test_post_execute_verification_correlates_ledger_and_reports_model_drift(self):
        ready = self.start_harness()
        helper = self.start_helper()
        host, _ = self.start_host(timeout_seconds=10.0)
        grant = successful_tool_result(self, call_tool(host, "nx_takeover", {
            "expected_pid": int(ready["pid"]), "expected_part_path": "C:\\M2Fake\\model.prt",
            "artifact_scope": "saved_independent_copy", "authorize": True,
        }))
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]
        session = successful_tool_result(self, call_tool(host, "nx_get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        prepared = successful_tool_result(self, call_tool(host, "nx_prepare_operation", operation_prepare_args(
            "m27-verify-prepare-" + uuid.uuid4().hex, owner, epoch, session["revision_id"], value="12.5",
        )))
        execute_id = "m27-verify-execute-" + uuid.uuid4().hex
        execute_args = {
            "operation_id": execute_id, "ownership_id": owner,
            "binding_epoch": epoch, "plan_id": prepared["result"]["plan"]["plan_id"],
        }
        accepted = successful_tool_result(self, call_tool(host, "nx_execute_plan", execute_args))
        self.assertIn(accepted.get("state"), {"QUEUED", "RUNNING", "SUCCEEDED"}, accepted)
        executed = self.wait_operation(helper, execute_id)
        self.assertEqual(executed.get("state"), "SUCCEEDED", executed)
        self.assertIs(executed.get("result", {}).get("changed"), True, executed)

        verify_id = "m27-verify-" + uuid.uuid4().hex
        verify_args = {
            "operation_id": verify_id, "ownership_id": owner, "binding_epoch": epoch,
            "execute_operation_id": execute_id,
        }
        verify_accepted = successful_tool_result(self, call_tool(host, "nx_verify_part", verify_args))
        self.assertIn(verify_accepted.get("state"), {"QUEUED", "RUNNING", "SUCCEEDED"}, verify_accepted)
        verified = self.wait_operation(helper, verify_id)
        self.assertEqual(verified.get("kind"), "verify_part", verified)
        self.assertEqual(verified.get("state"), "SUCCEEDED", verified)
        result = verified.get("result", {})
        self.assertIs(result.get("verified"), True, verified)
        self.assertEqual(result.get("execute_operation_id"), execute_id, verified)
        checks = result.get("checks", {})
        self.assertEqual(set(checks), {"expression", "geometry", "history", "associations"}, checks)
        for check_name, check in checks.items():
            self.assertEqual(check.get("status"), "VERIFIED", (check_name, check))
            self.assertIsInstance(check.get("evidence"), dict, (check_name, check))
        expression_evidence = checks["expression"]["evidence"]
        self.assertEqual(expression_evidence.get("expression_name"), "p11", expression_evidence)
        self.assertEqual(expression_evidence.get("expected_value"), "12.5", expression_evidence)
        self.assertEqual(expression_evidence.get("actual_rhs"), "12.5", expression_evidence)
        self.assertEqual(checks["geometry"]["evidence"].get("geometry_refs"), ["line-1", "line-2"], checks)
        self.assertEqual(checks["history"]["evidence"].get("feature_id"), "feature-1", checks)
        self.assertEqual(checks["associations"]["evidence"].get("constraint_id"), "constraint-1", checks)

        verify_replay = successful_helper_result(self, self.helper_call(
            helper, "operation.verify_part", dict(reversed(list(verify_args.items()))),
        ))
        self.assertEqual(verify_replay.get("result"), result, verify_replay)
        conflict_args = dict(verify_args)
        conflict_args["execute_operation_id"] = "missing-execute-operation"
        conflict = self.helper_call(helper, "operation.verify_part", conflict_args)
        self.assertEqual(conflict.get("error", {}).get("code"), "OPERATION_ID_CONFLICT", conflict)

        drift = self.harness_command("model-drift")
        self.assertEqual(drift.get("event"), "model-drift", drift)
        failed_verify_id = "m27-verify-drift-" + uuid.uuid4().hex
        failed_verify_args = dict(verify_args)
        failed_verify_args["operation_id"] = failed_verify_id
        self.helper_call(helper, "operation.verify_part", failed_verify_args)
        failed_verify = self.wait_operation(helper, failed_verify_id)
        self.assertEqual(failed_verify.get("state"), "FAILED", failed_verify)
        self.assertEqual(failed_verify.get("error", {}).get("code"), "PART_VERIFICATION_FAILED", failed_verify)
        failed_checks = failed_verify.get("result", {}).get("checks", {})
        self.assertIs(failed_verify.get("result", {}).get("verified"), False, failed_verify)
        self.assertEqual(failed_checks.get("expression", {}).get("status"), "MISMATCH", failed_verify)
        self.assertEqual(failed_checks.get("geometry", {}).get("status"), "MISMATCH", failed_verify)
        self.assertEqual(failed_checks.get("history", {}).get("status"), "VERIFIED", failed_verify)
        self.assertEqual(failed_checks.get("associations", {}).get("status"), "VERIFIED", failed_verify)
        failed_replay = successful_helper_result(self, self.helper_call(
            helper, "operation.verify_part", failed_verify_args,
        ))
        self.assertEqual(failed_replay.get("result"), failed_verify.get("result"), failed_replay)
        counts = self.fake_counts()
        self.assertEqual(counts.get("verification_calls"), 2, counts)
        record("M2-7-verify", {
            "execute_operation_id": execute_id,
            "verified_checks": sorted(checks),
            "verification_replay_adapter_calls": counts["verification_calls"],
            "model_drift_error": failed_verify["error"]["code"],
            "expression_and_geometry_mismatch_reported": True,
        })

    def test_current_work_part_save_copy_is_refused_before_adapter_call(self):
        ready = self.start_harness()
        helper = self.start_helper()
        host, _ = self.start_host(timeout_seconds=10.0)
        grant = successful_tool_result(self, call_tool(host, "nx_takeover", {
            "expected_pid": int(ready["pid"]), "expected_part_path": "",
            "artifact_scope": "current_work_part", "authorize": True,
        }))
        self.assertEqual(grant.get("artifact_scope"), "current_work_part", grant)
        self.assertEqual(grant.get("artifact_kind"), "standalone_part", grant)
        self.assertEqual(grant.get("support_summary", {}).get("history_mode"), "NATIVE_SKETCH_HISTORY", grant)
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]

        save_id = "m5-current-save-refused-" + uuid.uuid4().hex
        refused = successful_tool_result(self, call_tool(host, "nx_save_copy", {
            "operation_id": save_id, "ownership_id": owner, "binding_epoch": epoch,
            "target_path": "C:\\M2Fake\\current-copy.prt", "reopen": True,
        }))
        self.assertEqual(refused.get("state"), "FAILED", refused)
        self.assertEqual(refused.get("error", {}).get("code"), "CURRENT_WORK_PART_SAVE_UNSUPPORTED", refused)
        self.assertIs(refused.get("result", {}).get("saved"), False, refused)
        self.assertIs(refused.get("result", {}).get("evidence", {}).get("save_attempted"), False, refused)
        self.assertEqual(self.fake_counts().get("save_copy_calls"), 0, "adapter SaveCopy must never run for current_work_part")

        returned = successful_tool_result(self, call_tool(host, "nx_return", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        self.assertEqual(returned.get("ownership_state"), "HUMAN_OWNED", returned)
        self.assertIs(returned.get("safe_for_human"), True, returned)
        record("M5-2-current-save", {
            "artifact_scope": grant["artifact_scope"],
            "artifact_kind": grant["artifact_kind"],
            "save_error": refused["error"]["code"],
            "adapter_save_calls": 0,
            "ownership_returned": returned["ownership_state"],
        })

    def test_save_copy_reopens_new_binding_and_invalidates_prepared_plans(self):
        ready = self.start_harness()
        helper = self.start_helper()
        host, _ = self.start_host(timeout_seconds=10.0)
        grant = successful_tool_result(self, call_tool(host, "nx_takeover", {
            "expected_pid": int(ready["pid"]), "expected_part_path": "C:\\M2Fake\\model.prt",
            "artifact_scope": "saved_independent_copy", "authorize": True,
        }))
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]
        session = successful_tool_result(self, call_tool(host, "nx_get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        first_prepare = successful_tool_result(self, call_tool(host, "nx_prepare_operation", operation_prepare_args(
            "m27-save-edit-" + uuid.uuid4().hex, owner, epoch, session["revision_id"], value="12.5",
        )))
        pending_prepare = successful_tool_result(self, call_tool(host, "nx_prepare_operation", operation_prepare_args(
            "m27-save-pending-" + uuid.uuid4().hex, owner, epoch, session["revision_id"], value="13",
        )))
        execute_id = "m27-save-execute-" + uuid.uuid4().hex
        self.helper_call(helper, "operation.execute_plan", {
            "operation_id": execute_id, "ownership_id": owner, "binding_epoch": epoch,
            "plan_id": first_prepare["result"]["plan"]["plan_id"],
        })
        executed = self.wait_operation(helper, execute_id)
        self.assertEqual(executed.get("state"), "SUCCEEDED", executed)

        target_path = fixture_target_path(self.temp_path, "m27-save")
        self.assertFalse(Path(target_path).exists(), "save-copy fixture target must be absent before the request")
        save_id = "m27-save-" + uuid.uuid4().hex
        save_args = {
            "operation_id": save_id, "ownership_id": owner, "binding_epoch": epoch,
            "target_path": target_path, "reopen": True,
        }
        self.harness_command("block-save")
        self.addCleanup(self.release_fixture_save)
        accepted = successful_tool_result(self, call_tool(host, "nx_save_copy", save_args))
        self.assertIn(accepted.get("state"), {"QUEUED", "RUNNING", "SUCCEEDED"}, accepted)
        save_started = self.harness_command("wait-save-start")
        self.assertIs(save_started.get("observed"), True, save_started)

        competing_execute = self.helper_call(helper, "operation.execute_plan", {
            "operation_id": "m27-save-concurrent-edit-" + uuid.uuid4().hex,
            "ownership_id": owner, "binding_epoch": epoch,
            "plan_id": pending_prepare["result"]["plan"]["plan_id"],
        })
        self.assertEqual(competing_execute.get("error", {}).get("code"), "OPERATION_IN_PROGRESS", competing_execute)
        during_save_read = self.helper_call(helper, "model.inspect_part", {
            "ownership_id": owner, "binding_epoch": epoch,
        })
        self.assertEqual(during_save_read.get("error", {}).get("code"), "OPERATION_IN_PROGRESS", during_save_read)

        self.harness_command("release-save")
        saved = self.wait_operation(helper, save_id)
        old_read = self.helper_call(helper, "model.inspect_part", {
            "ownership_id": owner, "binding_epoch": epoch,
        })
        self.assertEqual(old_read.get("error", {}).get("code"), "AUTHORIZATION_REQUIRED", old_read)
        pre_reopen_counts = self.fake_counts()
        self.assertEqual(pre_reopen_counts.get("inspection_calls"), 0, pre_reopen_counts)
        self.assertEqual(saved.get("kind"), "save_copy", saved)
        self.assertEqual(saved.get("state"), "SUCCEEDED", saved)
        result = saved.get("result", {})
        self.assertIs(result.get("saved"), True, saved)
        self.assertEqual(result.get("source_path"), "C:\\M2Fake\\model.prt", saved)
        self.assertEqual(result.get("saved_path"), target_path, saved)
        self.assertIs(result.get("reopened"), True, saved)
        self.assertFalse(Path(target_path).exists(), "the NX-free fixture must not create a native .prt file")
        self.assertIs(result.get("plans_invalidated"), True, saved)
        new_owner, new_epoch = result.get("new_ownership_id"), result.get("new_binding_epoch")
        self.assertIsInstance(new_owner, str, saved)
        self.assertTrue(new_owner)
        self.assertNotEqual(new_owner, owner)
        self.assertIsInstance(new_epoch, int, saved)
        self.assertNotEqual(new_epoch, epoch)
        self.assertEqual(result.get("revision_id"), executed.get("result", {}).get("revision_id"), saved)
        save_checks = result.get("checks", {})
        self.assertEqual(set(save_checks), {"expression", "geometry", "history", "associations"}, save_checks)
        for check_name, check in save_checks.items():
            self.assertEqual(check.get("status"), "VERIFIED", (check_name, check))
        self.assertEqual(save_checks["expression"]["evidence"].get("actual_value"), "12.5", save_checks)
        self.assertEqual(save_checks["history"]["evidence"].get("feature_id"), "feature-1", save_checks)
        self.assertEqual(save_checks["associations"]["evidence"].get("geometry_refs"), ["line-1", "line-2"], save_checks)
        self.assertEqual(result.get("evidence", {}).get("saved_path"), target_path, saved)

        replay = successful_helper_result(self, self.helper_call(
            helper, "operation.save_copy", dict(reversed(list(save_args.items()))),
        ))
        self.assertEqual(replay.get("result"), result, replay)
        conflict_args = dict(save_args)
        conflict_args["target_path"] = fixture_target_path(self.temp_path, "m27-conflict")
        conflict = self.helper_call(helper, "operation.save_copy", conflict_args)
        self.assertEqual(conflict.get("error", {}).get("code"), "OPERATION_ID_CONFLICT", conflict)

        new_session = successful_tool_result(self, call_tool(host, "nx_get_session", {
            "ownership_id": new_owner, "binding_epoch": new_epoch,
        }))
        self.assertEqual(new_session.get("part_path"), target_path, new_session)
        self.assertEqual(new_session.get("revision_id"), result.get("revision_id"), new_session)
        part = successful_tool_result(self, call_tool(host, "nx_inspect_part", {
            "ownership_id": new_owner, "binding_epoch": new_epoch,
        }))
        self.assertEqual(part.get("part_path"), target_path, part)
        expression = next(item for item in part.get("expressions", []) if item.get("name") == "p11")
        self.assertEqual(expression.get("right_hand_side"), "12.5", part)
        self.assertEqual(part.get("history", [])[0].get("journal_id"), "feature-1", part)
        association = part.get("associations", [])[0]
        self.assertEqual(association.get("constraint_id"), "constraint-1", part)
        self.assertEqual(association.get("dimension_state"), "Driving", part)

        stale_plan_execute = self.helper_call(helper, "operation.execute_plan", {
            "operation_id": "m27-invalidated-plan-" + uuid.uuid4().hex,
            "ownership_id": new_owner, "binding_epoch": new_epoch,
            "plan_id": pending_prepare["result"]["plan"]["plan_id"],
        })
        self.assertEqual(stale_plan_execute.get("error", {}).get("code"), "PLAN_NOT_FOUND", stale_plan_execute)
        old_binding = call_tool(host, "nx_get_session", {"ownership_id": owner, "binding_epoch": epoch})
        old_error = extract_structured_result(old_binding.get("result", {})).get("error", {})
        self.assertEqual(old_error.get("code"), "AUTHORIZATION_REQUIRED", old_binding)
        counts = self.fake_counts()
        self.assertEqual(counts.get("save_copy_calls"), 1, counts)
        self.assertEqual(counts.get("saved_copy_count"), 1, counts)
        self.assertEqual(counts.get("inspection_calls"), 1, counts)
        self.assertEqual(counts.get("execution_calls"), 1, counts)
        record("M2-7-save-copy", {
            "target_path": target_path,
            "target_extension": ".prt",
            "reopened": result["reopened"],
            "binding_rotated": new_owner != owner and new_epoch != epoch,
            "saved_copy_checks": sorted(save_checks),
            "operation_retry_adapter_calls": counts["save_copy_calls"],
            "pending_plan_error_after_save": stale_plan_execute["error"]["code"],
            "during_save_read_error": during_save_read["error"]["code"],
            "old_binding_read_after_save_error": old_read["error"]["code"],
            "queued_edit_during_save_error": competing_execute["error"]["code"],
            "old_binding_adapter_read_calls": pre_reopen_counts["inspection_calls"],
            "reopened_expression_value_mm": expression["value"],
            "real_nx_save_as_evidence": False,
        })

    def test_known_save_failure_is_unchanged_and_allows_safe_handoff(self):
        ready = self.start_harness()
        helper = self.start_helper()
        host, _ = self.start_host(timeout_seconds=10.0)
        grant = successful_tool_result(self, call_tool(host, "nx_takeover", {
            "expected_pid": int(ready["pid"]), "expected_part_path": "C:\\M2Fake\\model.prt",
            "artifact_scope": "saved_independent_copy", "authorize": True,
        }))
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]
        target_path = "C:\\M2Fake\\model.prt"
        save_id = "m27-known-save-failure-" + uuid.uuid4().hex
        accepted = successful_tool_result(self, call_tool(host, "nx_save_copy", {
            "operation_id": save_id, "ownership_id": owner, "binding_epoch": epoch,
            "target_path": target_path, "reopen": True,
        }))
        self.assertIn(accepted.get("state"), {"QUEUED", "RUNNING", "FAILED"}, accepted)
        failed = self.wait_operation(helper, save_id)
        self.assertEqual(failed.get("kind"), "save_copy", failed)
        self.assertEqual(failed.get("state"), "FAILED", failed)
        self.assertEqual(failed.get("error", {}).get("code"), "TARGET_IS_SOURCE_PART", failed)
        self.assertIs(failed.get("result", {}).get("saved"), False, failed)
        self.assertIs(failed.get("result", {}).get("reopened"), False, failed)
        evidence = failed.get("result", {}).get("evidence", {})
        self.assertIs(evidence.get("save_attempted"), False, failed)

        existing_target = Path(fixture_target_path(self.temp_path, "m27-existing"))
        sentinel = "pre-existing temporary test marker; must not be overwritten"
        existing_target.write_text(sentinel, encoding="utf-8")
        existing_save_id = "m27-existing-target-" + uuid.uuid4().hex
        existing_accepted = successful_tool_result(self, call_tool(host, "nx_save_copy", {
            "operation_id": existing_save_id, "ownership_id": owner, "binding_epoch": epoch,
            "target_path": str(existing_target), "reopen": True,
        }))
        self.assertEqual(existing_accepted.get("state"), "FAILED", existing_accepted)
        existing_failed = self.get_operation(helper, existing_save_id)
        self.assertEqual(existing_failed.get("state"), "FAILED", existing_failed)
        self.assertEqual(existing_failed.get("error", {}).get("code"), "TARGET_ALREADY_EXISTS", existing_failed)
        self.assertIs(existing_failed.get("result", {}).get("saved"), False, existing_failed)
        self.assertIs(existing_failed.get("result", {}).get("evidence", {}).get("save_attempted"), False, existing_failed)
        self.assertEqual(existing_target.read_text(encoding="utf-8"), sentinel)
        status = successful_helper_result(self, self.helper_call(helper, "bridge.status", {}))
        self.assertIs(status.get("writes_halted"), False, status)
        self.assertEqual(status.get("ownership_state"), "AI_OWNED", status)

        returned = successful_tool_result(self, call_tool(host, "nx_return", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        self.assertEqual(returned.get("ownership_state"), "HUMAN_OWNED", returned)
        self.assertIs(returned.get("safe_for_human"), True, returned)
        counts = self.fake_counts()
        self.assertEqual(counts.get("save_copy_calls"), 0, counts)
        self.assertEqual(counts.get("saved_copy_count"), 0, counts)
        record("M2-7-save-copy-known-failure", {
            "source_target_error": failed["error"]["code"],
            "existing_target_error": existing_failed["error"]["code"],
            "save_attempted": evidence["save_attempted"],
            "existing_target_marker_unchanged": existing_target.read_text(encoding="utf-8") == sentinel,
            "writes_halted": status["writes_halted"],
            "safe_handoff": returned["safe_for_human"],
        })

    def test_unknown_save_copy_outcome_freezes_subsequent_model_writes(self):
        ready = self.start_harness()
        helper = self.start_helper()
        host, _ = self.start_host(timeout_seconds=10.0)
        grant = successful_tool_result(self, call_tool(host, "nx_takeover", {
            "expected_pid": int(ready["pid"]), "expected_part_path": "C:\\M2Fake\\model.prt",
            "artifact_scope": "saved_independent_copy", "authorize": True,
        }))
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]
        session = successful_tool_result(self, call_tool(host, "nx_get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        prepared = successful_tool_result(self, call_tool(host, "nx_prepare_operation", operation_prepare_args(
            "m27-unknown-plan-" + uuid.uuid4().hex, owner, epoch, session["revision_id"], value="14",
        )))
        self.harness_command("unknown-next-save")
        target_path = fixture_target_path(self.temp_path, "m27-unknown")
        save_id = "m27-unknown-save-" + uuid.uuid4().hex
        save_args = {
            "operation_id": save_id, "ownership_id": owner, "binding_epoch": epoch,
            "target_path": target_path, "reopen": True,
        }
        accepted = successful_tool_result(self, call_tool(host, "nx_save_copy", save_args))
        self.assertIn(accepted.get("state"), {"QUEUED", "RUNNING", "UNKNOWN"}, accepted)
        unknown = self.wait_operation(helper, save_id)
        self.assertEqual(unknown.get("state"), "UNKNOWN", unknown)
        self.assertEqual(unknown.get("error", {}).get("code"), "SAVE_RESULT_UNKNOWN", unknown)
        status = successful_helper_result(self, self.helper_call(helper, "bridge.status", {}))
        self.assertIs(status.get("writes_halted"), True, status)
        self.assertIs(status.get("model_write_enabled"), False, status)
        self.assertEqual(status.get("ownership_state"), "UNKNOWN_FROZEN", status)
        self.assertEqual(self.get_operation(helper, save_id).get("state"), "UNKNOWN")

        blocked_save = self.helper_call(helper, "operation.save_copy", {
            "operation_id": "m27-save-while-frozen-" + uuid.uuid4().hex,
            "ownership_id": owner, "binding_epoch": epoch,
            "target_path": fixture_target_path(self.temp_path, "m27-blocked"), "reopen": True,
        })
        self.assertEqual(blocked_save.get("error", {}).get("code"), "OPERATION_OUTCOME_UNKNOWN", blocked_save)
        blocked_execute = self.helper_call(helper, "operation.execute_plan", {
            "operation_id": "m27-execute-while-frozen-" + uuid.uuid4().hex,
            "ownership_id": owner, "binding_epoch": epoch,
            "plan_id": prepared["result"]["plan"]["plan_id"],
        })
        self.assertEqual(blocked_execute.get("error", {}).get("code"), "OPERATION_OUTCOME_UNKNOWN", blocked_execute)
        replay = successful_helper_result(self, self.helper_call(helper, "operation.save_copy", save_args))
        self.assertEqual(replay.get("state"), "UNKNOWN", replay)
        counts = self.fake_counts()
        self.assertEqual(counts.get("save_copy_calls"), 1, counts)
        self.assertEqual(counts.get("saved_copy_count"), 1, counts)
        self.assertEqual(counts.get("execution_calls"), 0, counts)
        record("M2-7-save-copy-unknown", {
            "unknown_operation_id": save_id,
            "unknown_error": unknown["error"]["code"],
            "writes_halted": status["writes_halted"],
            "blocked_save_error": blocked_save["error"]["code"],
            "blocked_execute_error": blocked_execute["error"]["code"],
            "adapter_save_calls": counts["save_copy_calls"],
            "unknown_outcome_stays_frozen": True,
        })

    def test_verify_and_save_copy_refuse_human_owned_model(self):
        ready = self.start_harness()
        helper = self.start_helper()
        host, _ = self.start_host(timeout_seconds=10.0)
        grant = successful_tool_result(self, call_tool(host, "nx_takeover", {
            "expected_pid": int(ready["pid"]), "expected_part_path": "C:\\M2Fake\\model.prt",
            "artifact_scope": "saved_independent_copy", "authorize": True,
        }))
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]
        session = successful_tool_result(self, call_tool(host, "nx_get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        prepared = successful_tool_result(self, call_tool(host, "nx_prepare_operation", operation_prepare_args(
            "m27-human-prepare-" + uuid.uuid4().hex, owner, epoch, session["revision_id"], value="11",
        )))
        execute_id = "m27-human-execute-" + uuid.uuid4().hex
        self.helper_call(helper, "operation.execute_plan", {
            "operation_id": execute_id, "ownership_id": owner, "binding_epoch": epoch,
            "plan_id": prepared["result"]["plan"]["plan_id"],
        })
        self.assertEqual(self.wait_operation(helper, execute_id).get("state"), "SUCCEEDED")
        returned = successful_tool_result(self, call_tool(host, "nx_return", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        self.assertEqual(returned.get("ownership_state"), "HUMAN_OWNED", returned)
        self.assertIs(returned.get("safe_for_human"), True, returned)

        verify = call_tool(host, "nx_verify_part", {
            "operation_id": "m27-human-verify-" + uuid.uuid4().hex,
            "ownership_id": owner, "binding_epoch": epoch, "execute_operation_id": execute_id,
        })
        verify_error = extract_structured_result(verify.get("result", {})).get("error", {})
        self.assertIs(verify.get("result", {}).get("isError"), True, verify)
        self.assertEqual(verify_error.get("code"), "AUTHORIZATION_REQUIRED", verify)
        save = call_tool(host, "nx_save_copy", {
            "operation_id": "m27-human-save-" + uuid.uuid4().hex,
            "ownership_id": owner, "binding_epoch": epoch,
            "target_path": fixture_target_path(self.temp_path, "m27-human"), "reopen": True,
        })
        save_error = extract_structured_result(save.get("result", {})).get("error", {})
        self.assertIs(save.get("result", {}).get("isError"), True, save)
        self.assertEqual(save_error.get("code"), "AUTHORIZATION_REQUIRED", save)
        counts = self.fake_counts()
        self.assertEqual(counts.get("verification_calls"), 0, counts)
        self.assertEqual(counts.get("save_copy_calls"), 0, counts)
        record("M2-7-human-owned-refusal", {
            "return_state": returned["ownership_state"],
            "verify_error": verify_error["code"],
            "save_error": save_error["code"],
            "adapter_calls_after_return": 0,
        })

    def test_rolled_back_operation_remains_queryable_and_allows_a_fresh_plan(self):
        ready = self.start_harness()
        helper = self.start_helper()
        grant = successful_helper_result(self, self.helper_call(helper, "ownership.takeover", {
            "expected_pid": int(ready["pid"]), "expected_part_path": "C:\\M2Fake\\model.prt",
            "artifact_scope": "saved_independent_copy", "authorize": True,
        }))
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]
        session = successful_helper_result(self, self.helper_call(helper, "model.get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))

        rollback_prepare = successful_helper_result(self, self.helper_call(helper, "operation.prepare", operation_prepare_args(
            "prepare-rollback-" + uuid.uuid4().hex, owner, epoch, session["revision_id"], value="11",
        )))
        self.harness_command("rollback-after-write")
        rollback_id = "execute-rollback-" + uuid.uuid4().hex
        rollback_args = {"operation_id": rollback_id, "ownership_id": owner, "binding_epoch": epoch,
                         "plan_id": rollback_prepare["result"]["plan"]["plan_id"]}
        self.helper_call(helper, "operation.execute_plan", rollback_args)
        rolled_back = self.wait_operation(helper, rollback_id)
        self.assertEqual(rolled_back.get("state"), "FAILED", rolled_back)
        self.assertEqual(rolled_back.get("error", {}).get("code"), "EDIT_NOT_VERIFIED", rolled_back)
        result = rolled_back.get("result", {})
        self.assertEqual(result.get("mutation_state"), "ROLLED_BACK", rolled_back)
        self.assertIs(result.get("changed"), False, rolled_back)
        self.assertEqual(result.get("before"), result.get("after"), rolled_back)

        status = successful_helper_result(self, self.helper_call(helper, "bridge.status", {}))
        self.assertIs(status.get("writes_halted"), False, status)
        self.assertIs(status.get("model_write_enabled"), True, status)
        self.assertEqual(status.get("ownership_state"), "AI_OWNED", status)
        counts_after_rollback = self.fake_counts()
        self.assertEqual(counts_after_rollback.get("rollback_count"), 1, counts_after_rollback)
        self.assertEqual(counts_after_rollback.get("write_attempt_count"), 1, counts_after_rollback)
        self.assertEqual(counts_after_rollback.get("mutation_count"), 0, counts_after_rollback)
        self.assertEqual(counts_after_rollback.get("current_value"), "10", counts_after_rollback)

        fresh_session = successful_helper_result(self, self.helper_call(helper, "model.get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        fresh_prepare = successful_helper_result(self, self.helper_call(helper, "operation.prepare", operation_prepare_args(
            "prepare-after-rollback-" + uuid.uuid4().hex, owner, epoch,
            fresh_session["revision_id"], value="12",
        )))
        fresh_id = "execute-after-rollback-" + uuid.uuid4().hex
        fresh_args = {"operation_id": fresh_id, "ownership_id": owner, "binding_epoch": epoch,
                      "plan_id": fresh_prepare["result"]["plan"]["plan_id"]}
        self.helper_call(helper, "operation.execute_plan", fresh_args)
        fresh_result = self.wait_operation(helper, fresh_id)
        self.assertEqual(fresh_result.get("state"), "SUCCEEDED", fresh_result)
        self.assertEqual(fresh_result.get("result", {}).get("mutation_state"), "APPLIED", fresh_result)
        counts = self.fake_counts()
        self.assertEqual(counts.get("rollback_count"), 1, counts)
        self.assertEqual(counts.get("write_attempt_count"), 2, counts)
        self.assertEqual(counts.get("mutation_count"), 1, counts)
        self.assertEqual(counts.get("current_value"), "12", counts)
        record("M2-5-6-STA-rollback", {
            "rollback_operation_state": rolled_back["state"],
            "rollback_error_code": rolled_back["error"]["code"],
            "writes_halted_after_verified_rollback": status["writes_halted"],
            "fresh_plan_after_rollback": fresh_result["state"],
            "adapter_write_attempts": counts["write_attempt_count"],
            "adapter_mutation_count": counts["mutation_count"],
        })

    def test_operation_cancel_queue_running_and_return_drains_started_execution(self):
        ready = self.start_harness()
        helper = self.start_helper()
        cancel_helper = self.start_helper()
        observer = self.start_helper()
        cancel_host, _ = self.start_host(timeout_seconds=10.0)
        self.addCleanup(self.release_fixture_operation)
        self.addCleanup(self.release_fixture_inspection)

        grant_response = self.helper_call(helper, "ownership.takeover", {
            "expected_pid": int(ready["pid"]), "expected_part_path": "C:\\M2Fake\\model.prt",
            "artifact_scope": "saved_independent_copy", "authorize": True,
        })
        grant = successful_helper_result(self, grant_response)
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]
        session = successful_helper_result(self, self.helper_call(helper, "model.get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))

        plans = {}
        for label, value in (("queued", "12"), ("first", "11"), ("after-return", "13")):
            args = operation_prepare_args(
                "prepare-" + label + "-" + uuid.uuid4().hex, owner, epoch,
                session["revision_id"], value=value,
            )
            prepared = successful_helper_result(self, self.helper_call(helper, "operation.prepare", args))
            self.assertEqual(prepared.get("state"), "SUCCEEDED", prepared)
            plans[label] = prepared["result"]["plan"]["plan_id"]

        # Occupy the STA with a controlled read, then cancel an accepted execute while it is queued.
        self.harness_command("block-inspect")
        read_response = {}
        read_failure = []

        def inspect_in_flight():
            try:
                read_response["value"] = self.helper_call(cancel_helper, "model.inspect_part", {
                    "ownership_id": owner, "binding_epoch": epoch,
                }, timeout=15.0)
            except Exception as exc:
                read_failure.append(exc)

        read_thread = threading.Thread(target=inspect_in_flight, name="m2-queued-operation-read-gate")
        read_thread.start()
        read_started = self.harness_command("wait-inspect-start")
        self.assertIs(read_started.get("observed"), True, read_started)
        queued_id = "execute-queued-" + uuid.uuid4().hex
        queued_args = {"operation_id": queued_id, "ownership_id": owner, "binding_epoch": epoch,
                       "plan_id": plans["queued"]}
        queued = successful_helper_result(self, self.helper_call(observer, "operation.execute_plan", queued_args))
        self.assertEqual(queued.get("state"), "QUEUED", queued)
        self.assertIs(queued.get("cancellable"), True, queued)
        cancelled = successful_tool_result(self, call_tool(cancel_host, "nx_cancel_operation", {
            "operation_id": queued_id,
        }))
        self.assertEqual(cancelled.get("state"), "CANCELLED", cancelled)
        self.assertIs(cancelled.get("result", {}).get("cancelled_before_start"), True, cancelled)
        self.harness_command("release-inspect")
        read_thread.join(timeout=10.0)
        self.assertFalse(read_thread.is_alive(), "STA read gate did not release after queued operation cancellation")
        self.assertFalse(read_failure, read_failure)
        self.assertEqual(successful_helper_result(self, read_response["value"]).get("part_path"),
                         "C:\\M2Fake\\model.prt")
        after_queued_cancel = self.fake_counts()
        self.assertEqual(after_queued_cancel.get("execution_calls"), 0, after_queued_cancel)
        self.assertEqual(after_queued_cancel.get("prepare_calls"), 3, after_queued_cancel)
        self.assertEqual(after_queued_cancel.get("mutation_count"), 0, after_queued_cancel)

        self.harness_command("block-operation")
        first_id = "execute-first-" + uuid.uuid4().hex
        first_args = {"operation_id": first_id, "ownership_id": owner, "binding_epoch": epoch,
                      "plan_id": plans["first"]}
        queued_first = successful_helper_result(self, self.helper_call(helper, "operation.execute_plan", first_args))
        self.assertIn(queued_first.get("state"), {"QUEUED", "RUNNING"}, queued_first)
        started = self.harness_command("wait-operation-start")
        self.assertIs(started.get("observed"), True, started)

        running_cancel = successful_tool_result(self, call_tool(cancel_host, "nx_cancel_operation", {
            "operation_id": first_id,
        }))
        self.assertEqual(running_cancel.get("state"), "RUNNING", running_cancel)
        self.assertIs(running_cancel.get("cancellable"), False, running_cancel)
        replay = successful_helper_result(self, self.helper_call(observer, "operation.execute_plan", dict(reversed(list(first_args.items())))))
        self.assertEqual(replay.get("state"), "RUNNING", replay)
        competing_id = "execute-competing-" + uuid.uuid4().hex
        competing_args = {"operation_id": competing_id, "ownership_id": owner, "binding_epoch": epoch,
                          "plan_id": plans["after-return"]}
        competing_write = self.helper_call(observer, "operation.execute_plan", competing_args)
        self.assertEqual(competing_write.get("error", {}).get("code"), "OPERATION_IN_PROGRESS", competing_write)
        counts = self.fake_counts()
        self.assertEqual(counts.get("prepare_calls"), 3, counts)
        self.assertEqual(counts.get("execution_calls"), 1, counts)
        self.assertEqual(counts.get("mutation_count"), 0, counts)

        returned = {}
        return_failure = []

        def return_ownership():
            try:
                returned["response"] = self.helper_call(cancel_helper, "ownership.return", {
                    "ownership_id": owner, "binding_epoch": epoch,
                }, timeout=15.0)
            except Exception as exc:
                return_failure.append(exc)

        return_thread = threading.Thread(target=return_ownership, name="m2-operation-return-drain")
        return_thread.start()
        deadline = time.monotonic() + 5.0
        status = None
        while time.monotonic() < deadline:
            status_response = self.helper_call(observer, "bridge.status", {})
            status = successful_helper_result(self, status_response)
            if status.get("ownership_state") == "RETURNING":
                break
            time.sleep(0.02)
        self.assertIsInstance(status, dict)
        self.assertEqual(status.get("ownership_state"), "RETURNING", status)
        self.assertGreater(status.get("active_operations", 0), 0, status)
        self.assertTrue(return_thread.is_alive(), "return completed before the started operation was released")

        after_return_args = {"operation_id": "execute-during-return-" + uuid.uuid4().hex,
                             "ownership_id": owner, "binding_epoch": epoch,
                             "plan_id": plans["after-return"]}
        rejected_during_return = self.helper_call(observer, "operation.prepare", operation_prepare_args(
            "prepare-during-return-" + uuid.uuid4().hex, owner, epoch,
            session["revision_id"], value="14",
        ))
        self.assertEqual(rejected_during_return.get("error", {}).get("code"), "AUTHORIZATION_REQUIRED", rejected_during_return)
        self.assertTrue(return_thread.is_alive(), "return completed before the started operation was released")

        self.harness_command("release-operation")
        completed_first = self.wait_operation(helper, first_id)
        self.assertEqual(completed_first.get("state"), "SUCCEEDED", completed_first)
        self.assertEqual(completed_first.get("result", {}).get("mutation_state"), "APPLIED", completed_first)
        return_thread.join(timeout=10.0)
        self.assertFalse(return_thread.is_alive(), "ownership return did not finish after the admitted operation drained")
        self.assertFalse(return_failure, return_failure)
        returned_value = successful_helper_result(self, returned["response"])
        self.assertEqual(returned_value.get("ownership_state"), "HUMAN_OWNED", returned_value)
        self.assertIs(returned_value.get("safe_for_human"), True, returned_value)
        self.assertEqual(self.get_operation(helper, queued_id).get("state"), "CANCELLED")

        old_binding = self.helper_call(observer, "operation.execute_plan", after_return_args)
        self.assertEqual(old_binding.get("error", {}).get("code"), "AUTHORIZATION_REQUIRED", old_binding)
        counts = self.fake_counts()
        self.assertEqual(counts.get("prepare_calls"), 3, counts)
        self.assertEqual(counts.get("execution_calls"), 1, counts)
        self.assertEqual(counts.get("mutation_count"), 1, counts)
        record("M2-5-STA-cancel-return", {
            "queued_cancel_state": cancelled["state"],
            "queued_cancel_execution_calls_before_release": after_queued_cancel["execution_calls"],
            "started_cancel_state": running_cancel["state"],
            "competing_execute_error": competing_write["error"]["code"],
            "started_operation_survived_cancel": completed_first["state"] == "SUCCEEDED",
            "return_state": returned_value["ownership_state"],
            "old_binding_execute_error": old_binding["error"]["code"],
            "adapter_execution_calls": counts["execution_calls"],
            "adapter_mutation_count": counts["mutation_count"],
        })

    def test_unknown_adapter_result_freezes_writes_but_stays_queryable(self):
        ready = self.start_harness()
        helper = self.start_helper()
        host, _ = self.start_host(timeout_seconds=10.0)
        takeover = successful_tool_result(self, call_tool(host, "nx_takeover", {
            "expected_pid": int(ready["pid"]),
            "expected_part_path": "C:\\M2Fake\\model.prt",
            "artifact_scope": "saved_independent_copy", "authorize": True,
        }))
        owner, epoch = takeover["ownership_id"], takeover["binding_epoch"]
        session = successful_tool_result(self, call_tool(host, "nx_get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        first_prepare = successful_tool_result(self, call_tool(host, "nx_prepare_operation", operation_prepare_args(
            "prepare-unknown-" + uuid.uuid4().hex, owner, epoch, session["revision_id"], value="14",
        )))
        second_prepare = successful_helper_result(self, self.helper_call(helper, "operation.prepare", operation_prepare_args(
            "prepare-after-unknown-" + uuid.uuid4().hex, owner, epoch, session["revision_id"], value="15",
        )))
        self.harness_command("unknown-after-mutation")

        execute_id = "execute-unknown-" + uuid.uuid4().hex
        execute_args = {"operation_id": execute_id, "ownership_id": owner, "binding_epoch": epoch,
                        "plan_id": first_prepare["result"]["plan"]["plan_id"]}
        accepted = successful_tool_result(self, call_tool(host, "nx_execute_plan", execute_args))
        self.assertIn(accepted.get("state"), {"QUEUED", "RUNNING", "UNKNOWN"}, accepted)
        unknown = self.wait_operation(helper, execute_id)
        self.assertEqual(unknown.get("state"), "UNKNOWN", unknown)
        self.assertEqual(unknown.get("error", {}).get("code"), "UNKNOWN_RESULT", unknown)
        self.assertIsNone(unknown.get("result", {}).get("changed"), unknown)

        queried = successful_tool_result(self, call_tool(host, "nx_get_operation", {"operation_id": execute_id}))
        self.assertEqual(queried.get("state"), "UNKNOWN", queried)
        status = successful_helper_result(self, self.helper_call(helper, "bridge.status", {}))
        self.assertIs(status.get("writes_halted"), True, status)
        self.assertIs(status.get("model_write_enabled"), False, status)
        self.assertEqual(status.get("ownership_state"), "UNKNOWN_FROZEN", status)

        rejected_write = self.helper_call(helper, "operation.execute_plan", {
            "operation_id": "execute-blocked-" + uuid.uuid4().hex,
            "ownership_id": owner, "binding_epoch": epoch,
            "plan_id": second_prepare["result"]["plan"]["plan_id"],
        })
        self.assertEqual(rejected_write.get("error", {}).get("code"), "OPERATION_OUTCOME_UNKNOWN", rejected_write)
        replay = successful_helper_result(self, self.helper_call(helper, "operation.execute_plan", execute_args))
        self.assertEqual(replay.get("state"), "UNKNOWN", replay)
        self.assertEqual(self.get_operation(helper, execute_id).get("state"), "UNKNOWN")
        counts = self.fake_counts()
        self.assertEqual(counts.get("execution_calls"), 1, counts)
        self.assertEqual(counts.get("mutation_count"), 1, counts)
        self.assertEqual(counts.get("current_value"), "14", counts)
        record("M2-5-STA-unknown-result", {
            "operation_state": unknown["state"],
            "error_code": unknown["error"]["code"],
            "query_after_unknown": queried["state"],
            "writes_halted": status["writes_halted"],
            "model_write_enabled": status["model_write_enabled"],
            "subsequent_execute_error": rejected_write["error"]["code"],
            "adapter_mutation_count": counts["mutation_count"],
        })

    def test_wrong_token_is_an_authorization_error_not_a_success_or_protocol_error(self):
        ready = self.start_harness()
        server_token = self.token_file.read_text(encoding="utf-8").strip()
        wrong_token = secrets.token_hex(32)
        self.assertNotEqual(wrong_token, server_token)
        self.wrong_token_file.write_text(wrong_token + "\n", encoding="utf-8")

        host, _ = self.start_host(token_file=self.wrong_token_file, timeout_seconds=1.0)
        started = time.monotonic()
        response = call_tool(host, "nx_bridge_status", {})
        elapsed_ms = (time.monotonic() - started) * 1000.0
        error_result = response.get("result")
        self.assertIsInstance(error_result, dict, response)
        self.assertIs(error_result.get("isError"), True, response)
        error_payload = extract_structured_result(error_result)
        self.assertEqual(error_payload.get("error", {}).get("code"), "AUTHORIZATION_REQUIRED", response)
        self.assertNotIn("ok", error_payload, response)
        self.assertLess(elapsed_ms, 3000.0)
        host_exit, host_output = finish_child(self, host, forbidden_values=(server_token, wrong_token))
        self.assertEqual(host_exit, 0)

        valid_helper = self.start_helper()
        valid = valid_helper.exchange(
            {"id": "valid-after-wrong-token", "command": "bridge.status", "params": {}},
            expected_id="valid-after-wrong-token", timeout=5.0,
        )
        self.assertEqual(successful_helper_result(self, valid).get("pid"), ready.get("pid"))
        helper_exit, helper_output = finish_child(self, valid_helper, forbidden_values=(server_token,))
        self.assertEqual(helper_exit, 0)
        assert_stdout_jsonl_only(host_output, forbidden_values=(server_token, wrong_token))
        self.assertTrue(helper_output)
        record("M2-2-03-wrong-token", {"is_error": True, "error_code": "AUTHORIZATION_REQUIRED",
                                       "elapsed_ms": round(elapsed_ms, 3),
                                       "host_child_exit_code": host_exit,
                                       "valid_server_remained_available": True,
                                       "stdout_secret_scan_passed": True})

    def test_disconnected_pipe_is_a_bounded_tool_error(self):
        self.start_harness()
        server_token = self.token_file.read_text(encoding="utf-8").strip()
        absent_pipe = "nx-m2-absent-" + uuid.uuid4().hex
        host, _ = self.start_host(pipe_name=absent_pipe, timeout_seconds=1.0)
        started = time.monotonic()
        response = call_tool(host, "nx_bridge_status", {})
        elapsed_ms = (time.monotonic() - started) * 1000.0
        result = response.get("result")
        self.assertIsInstance(result, dict, response)
        self.assertIs(result.get("isError"), True, response)
        payload = extract_structured_result(result)
        error_code = payload.get("error", {}).get("code")
        self.assertIn(error_code, {"BRIDGE_UNAVAILABLE", "BRIDGE_TIMEOUT", "TRANSPORT_ERROR"}, response)
        self.assertNotIn("ok", payload, response)
        self.assertLess(elapsed_ms, 4000.0)
        exit_code, output = finish_child(self, host, forbidden_values=(server_token,))
        self.assertEqual(exit_code, 0)
        self.assertTrue(self.harness.process.poll() is None)
        assert_stdout_jsonl_only(output, forbidden_values=(server_token,))
        record("M2-2-03-disconnected", {"is_error": True, "error_code": error_code,
                                        "elapsed_ms": round(elapsed_ms, 3), "mcp_child_exit_code": exit_code,
                                        "unrelated_server_remained_available": True,
                                        "stdout_secret_scan_passed": True})

    def test_prepared_session_retries_after_token_file_appears(self):
        prepared_pipe = "nx-m2-prepared-" + uuid.uuid4().hex
        host, _ = self.start_host(pipe_name=prepared_pipe, token_file=self.token_file, timeout_seconds=1.0)
        self.assertFalse(self.token_file.exists())
        first = call_tool(host, "nx_bridge_status", {})
        first_result = first.get("result")
        self.assertIsInstance(first_result, dict, first)
        self.assertIs(first_result.get("isError"), True, first)
        first_payload = extract_structured_result(first_result)
        self.assertEqual(first_payload.get("error", {}).get("code"), "BRIDGE_NOT_READY", first)
        self.assertFalse(self.token_file.exists())

        self.pipe_name = prepared_pipe
        ready = self.start_harness()
        server_token = self.token_file.read_text(encoding="utf-8").strip()
        second = call_tool(host, "nx_bridge_status", {})
        status = successful_tool_result(self, second)
        self.assertEqual(status.get("pid"), ready.get("pid"))
        self.assertEqual(status.get("state"), "loaded")

        exit_code, output = finish_child(self, host, forbidden_values=(server_token,))
        self.assertEqual(exit_code, 0)
        self.assertTrue(self.harness.process.poll() is None)
        assert_stdout_jsonl_only(output, forbidden_values=(server_token,))
        record("M2-2-05", {"first_error_code": "BRIDGE_NOT_READY", "token_absent_before_load": True,
                           "token_present_after_harness_start": True, "retry_state": status.get("state"),
                           "retry_nx_pid": status.get("pid"), "same_mcp_child_pid": host.process.pid,
                           "mcp_child_exit_code": exit_code, "stdout_secret_scan_passed": True})

    def _stop_harness(self):
        if self.harness is None or self.harness.process.poll() is not None:
            return {"event": "already-exited"}
        self.harness.send_raw_line("stop")
        stopped = self.harness.receive_json(timeout=5.0)
        self.assertEqual(stopped.get("event"), "stopped", stopped)
        self.assertEqual(self.harness.wait(timeout=5.0), 0)
        raw = self.harness.all_stdout_bytes()
        assert_stdout_jsonl_only(raw)
        return stopped

    def helper_call(self, helper, command: str, params: dict, *, timeout: float = 5.0):
        request_id = "m2-test-" + uuid.uuid4().hex
        return helper.exchange({"id": request_id, "command": command, "params": params},
                               expected_id=request_id, timeout=timeout)

    def harness_command(self, command: str):
        self.harness.send_raw_line(command)
        return self.harness.receive_json(timeout=5.0)

    def fake_counts(self):
        result = self.harness_command("fake-counts")
        self.assertEqual(result.get("event"), "fake-counts", result)
        return result

    def get_operation(self, helper, operation_id: str):
        response = self.helper_call(helper, "operation.get", {"operation_id": operation_id})
        return successful_helper_result(self, response)

    def wait_operation(self, helper, operation_id: str, *, timeout: float = 5.0):
        terminal = {"SUCCEEDED", "FAILED", "CANCELLED", "UNKNOWN"}
        deadline = time.monotonic() + timeout
        latest = None
        while time.monotonic() < deadline:
            latest = self.get_operation(helper, operation_id)
            if latest.get("state") in terminal:
                return latest
            time.sleep(0.02)
        self.fail("operation did not reach a terminal state: " + repr(latest))

    def release_fixture_operation(self):
        if self.harness is None or self.harness.process.poll() is not None:
            return
        try:
            self.harness_command("release-operation")
        except Exception:
            pass

    def release_fixture_inspection(self):
        if self.harness is None or self.harness.process.poll() is not None:
            return
        try:
            self.harness_command("release-inspect")
        except Exception:
            pass

    def release_fixture_save(self):
        if self.harness is None or self.harness.process.poll() is not None:
            return
        try:
            self.harness_command("release-save")
        except Exception:
            pass

    def _cleanup_harness(self):
        if self.harness is None:
            return
        if self.harness.process.poll() is None:
            try:
                self._stop_harness()
            except Exception:
                self.harness.terminate_own_process()
        else:
            self.harness.terminate_own_process()

    @staticmethod
    def _cleanup_child(child):
        if child.process.poll() is None:
            try:
                child.close_stdin()
                child.wait(timeout=5.0)
            except Exception:
                child.terminate_own_process()
        else:
            child.terminate_own_process()


def call_tool(host: StdioMcpProcess, name: str, arguments: dict):
    return host.request("tools/call", {"name": name, "arguments": arguments})


def operation_prepare_args(operation_id: str, owner: str, epoch: int, revision_id: str, *,
                           value: str, unit: str = "mm", constraint_id: str = "constraint-1"):
    return {
        "operation_id": operation_id,
        "ownership_id": owner,
        "binding_epoch": epoch,
        "revision_id": revision_id,
        "sketch_id": "sketch-1",
        "constraint_id": constraint_id,
        "value": value,
        "unit": unit,
    }


def fixture_target_path(temp_path: Path, prefix: str) -> str:
    """Return an explicit, unique .prt target under an existing test temp directory."""
    return str((temp_path / (prefix + "-" + uuid.uuid4().hex + ".prt")).resolve())


def successful_helper_result(test: unittest.TestCase, response: dict) -> dict:
    test.assertNotIn("error", response, response)
    result = response.get("result")
    test.assertIsInstance(result, dict, response)
    test.assertIs(result.get("ok"), True, response)
    return result


def successful_tool_result(test: unittest.TestCase, response: dict) -> dict:
    result = response.get("result")
    test.assertIsInstance(result, dict, response)
    test.assertIs(result.get("isError"), False, response)
    payload = extract_structured_result(result)
    test.assertIsInstance(payload, dict, response)
    test.assertIs(payload.get("ok"), True, response)
    return payload


def assert_probe(test: unittest.TestCase, probe: dict, nonce: str):
    test.assertEqual(probe.get("nonce"), nonce, probe)
    for key in ("request_thread_id", "callback_thread_id", "main_thread_id"):
        test.assertIsInstance(probe.get(key), str, probe)
        test.assertTrue(probe[key], probe)
    test.assertIs(probe.get("on_main_thread"), True, probe)
    test.assertEqual(probe.get("callback_thread_id"), probe.get("main_thread_id"), probe)
    test.assertEqual(probe.get("nx_api_calls"), 0, probe)


def finish_child(test: unittest.TestCase, child: StdioMcpProcess, *, forbidden_values=()):
    if child.process.poll() is None:
        child.close_stdin()
    exit_code = child.wait(timeout=5.0)
    raw = child.all_stdout_bytes()
    assert_stdout_jsonl_only(raw, forbidden_values=forbidden_values)
    return exit_code, raw


if __name__ == "__main__":
    unittest.main()
