"""M6 fault regressions across the production helper and MCP host, using the NX-free STA fixture."""

from __future__ import annotations

from pathlib import Path
import tempfile
import time
import unittest
import uuid

import test_m2_bridge as _m2
from evidence_recorder import record
from test_m2_bridge import (
    call_tool,
    operation_prepare_args,
    successful_helper_result,
    successful_tool_result,
)
from test_mcp_stdio import extract_structured_result


PROJECT_ROOT = Path(__file__).resolve().parents[2]


class M6FaultRegressionTests(unittest.TestCase):
    """Isolated additions; base M2/M3 test methods are not inherited or repeated."""

    @classmethod
    def setUpClass(cls):
        _m2.M2BridgeSubsystemTests.setUpClass.__func__(cls)

    @classmethod
    def tearDownClass(cls):
        _m2.M2BridgeSubsystemTests.tearDownClass.__func__(cls)

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="nx-m6-faults-")
        self.temp_path = Path(self.temp.name)
        self.pipe_name = "nx-m6-test-" + uuid.uuid4().hex
        self.token_file = self.temp_path / "server-token.txt"
        self.wrong_token_file = self.temp_path / "wrong-token.txt"
        self.harness = None
        self.addCleanup(self.temp.cleanup)

    start_harness = _m2.M2BridgeSubsystemTests.start_harness
    start_helper = _m2.M2BridgeSubsystemTests.start_helper
    start_host = _m2.M2BridgeSubsystemTests.start_host
    helper_call = _m2.M2BridgeSubsystemTests.helper_call
    harness_command = _m2.M2BridgeSubsystemTests.harness_command
    fake_counts = _m2.M2BridgeSubsystemTests.fake_counts
    get_operation = _m2.M2BridgeSubsystemTests.get_operation
    release_fixture_operation = _m2.M2BridgeSubsystemTests.release_fixture_operation
    release_fixture_inspection = _m2.M2BridgeSubsystemTests.release_fixture_inspection
    _cleanup_harness = _m2.M2BridgeSubsystemTests._cleanup_harness
    _cleanup_child = staticmethod(_m2.M2BridgeSubsystemTests._cleanup_child)

    def wait_helper_operation(self, helper, operation_id: str, *, timeout: float = 8.0):
        terminal = {"SUCCEEDED", "FAILED", "CANCELLED", "UNKNOWN"}
        deadline = time.monotonic() + timeout
        latest = None
        while time.monotonic() < deadline:
            latest = self.get_operation(helper, operation_id)
            if latest.get("state") in terminal:
                return latest
            time.sleep(0.02)
        self.fail("operation did not reach a terminal state: " + repr(latest))

    def wait_host_operation(self, host, operation_id: str, *, timeout: float = 8.0):
        terminal = {"SUCCEEDED", "FAILED", "CANCELLED", "UNKNOWN"}
        deadline = time.monotonic() + timeout
        latest = None
        while time.monotonic() < deadline:
            latest = successful_tool_result(self, call_tool(host, "nx_get_operation", {
                "operation_id": operation_id,
            }))
            if latest.get("state") in terminal:
                return latest
            time.sleep(0.02)
        self.fail("MCP operation did not reach a terminal state: " + repr(latest))

    @staticmethod
    def _tool_error(response):
        result = response.get("result", {})
        payload = extract_structured_result(result)
        return result, payload.get("error", {})

    def test_bridge_helper_timeout_on_blocked_read_is_not_the_120_second_write_timeout(self):
        ready = self.start_harness()
        helper = self.start_helper()
        grant = successful_helper_result(self, self.helper_call(helper, "ownership.takeover", {
            "expected_pid": int(ready["pid"]),
            "expected_part_path": "C:\\M2Fake\\model.prt",
            "artifact_scope": "saved_independent_copy",
            "authorize": True,
        }))
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]

        # Warm the independent MCP child's helper at the minimum configured timeout.
        host, _ = self.start_host(timeout_seconds=0.1)
        warm_status = successful_tool_result(self, call_tool(host, "nx_bridge_status", {}))
        self.assertEqual(warm_status.get("ownership_state"), "AI_OWNED", warm_status)

        self.addCleanup(self.release_fixture_inspection)
        self.harness_command("block-inspect")
        timeout_started = time.monotonic()
        response = call_tool(host, "nx_inspect_part", {
            "ownership_id": owner, "binding_epoch": epoch,
        })
        timeout_elapsed_ms = (time.monotonic() - timeout_started) * 1000.0
        tool_result, error = self._tool_error(response)
        self.assertIs(tool_result.get("isError"), True, response)
        self.assertEqual(error.get("code"), "BRIDGE_TIMEOUT", response)
        self.assertEqual(error.get("message"), "The local bridge request timed out.", response)
        self.assertGreaterEqual(timeout_elapsed_ms, 75.0, timeout_elapsed_ms)
        self.assertLess(timeout_elapsed_ms, 5000.0, timeout_elapsed_ms)

        started = self.harness_command("wait-inspect-start")
        self.assertIs(started.get("observed"), True, started)
        self.harness_command("release-inspect")
        recovered = successful_helper_result(self, self.helper_call(helper, "bridge.status", {}))
        counts = self.fake_counts()
        self.assertEqual(recovered.get("ownership_state"), "AI_OWNED", recovered)
        self.assertIs(recovered.get("writes_halted"), False, recovered)
        self.assertEqual(counts.get("inspection_calls"), 1, counts)
        self.assertEqual(counts.get("mutation_count"), 0, counts)
        record("M6-helper-bridge-timeout", {
            "configured_helper_timeout_seconds": 0.1,
            "observed_timeout_elapsed_ms": round(timeout_elapsed_ms, 3),
            "trigger": "blocked model.inspect_part dispatch",
            "stdio_error_code": error.get("code"),
            "late_read_completed_once": counts.get("inspection_calls") == 1,
            "writes_halted": recovered.get("writes_halted"),
            "bridge_120_second_execution_age_injection_used": False,
        })

    def test_aged_late_m3_completion_stays_unknown_through_stdio_without_replay_or_return(self):
        ready = self.start_harness()
        host, _ = self.start_host(timeout_seconds=10.0)
        grant = successful_tool_result(self, call_tool(host, "nx_takeover", {
            "expected_pid": int(ready["pid"]),
            "expected_part_path": "C:\\M2Fake\\model.prt",
            "artifact_scope": "saved_independent_copy",
            "authorize": True,
        }))
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]
        session = successful_tool_result(self, call_tool(host, "nx_get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))

        sketch_prepare = successful_tool_result(self, call_tool(host, "nx_prepare_sketch", {
            "operation_id": "m6-late-sketch-prepare-" + uuid.uuid4().hex,
            "ownership_id": owner, "binding_epoch": epoch,
            "revision_id": session["revision_id"], "plane": "XY", "origin_mm": [0.0, 0.0, 0.0],
            "geometries": [{
                "id": "m6-late-profile", "type": "rectangle", "corner_mm": [0.0, 0.0],
                "width_mm": 14.0, "height_mm": 6.0,
            }],
        }))
        sketch_execute_id = "m6-late-sketch-execute-" + uuid.uuid4().hex
        sketch_accepted = successful_tool_result(self, call_tool(host, "nx_execute_plan", {
            "operation_id": sketch_execute_id, "ownership_id": owner, "binding_epoch": epoch,
            "plan_id": sketch_prepare["result"]["plan"]["plan_id"],
        }))
        self.assertIn(sketch_accepted.get("state"), {"QUEUED", "RUNNING", "SUCCEEDED"}, sketch_accepted)
        sketch_done = self.wait_host_operation(host, sketch_execute_id)
        self.assertEqual(sketch_done.get("state"), "SUCCEEDED", sketch_done)
        sketch_id = sketch_done.get("result", {}).get("sketch_id")
        self.assertTrue(sketch_id, sketch_done)

        fresh_session = successful_tool_result(self, call_tool(host, "nx_get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        extrude_plans = []
        for direction, depth in (("positive", 5.0), ("negative", 4.0)):
            prepared = successful_tool_result(self, call_tool(host, "nx_prepare_extrude", {
                "operation_id": "m6-late-extrude-prepare-" + uuid.uuid4().hex,
                "ownership_id": owner, "binding_epoch": epoch,
                "revision_id": fresh_session["revision_id"], "sketch_id": sketch_id,
                "mode": "create", "direction": direction, "extent": "finite", "depth_mm": depth,
            }))
            extrude_plans.append(prepared["result"]["plan"]["plan_id"])

        self.addCleanup(self.release_fixture_operation)
        self.harness_command("block-operation")
        operation_id = "m6-aged-late-extrude-" + uuid.uuid4().hex
        execute_args = {
            "operation_id": operation_id, "ownership_id": owner, "binding_epoch": epoch,
            "plan_id": extrude_plans[0],
        }
        accepted = successful_tool_result(self, call_tool(host, "nx_execute_plan", execute_args))
        self.assertIn(accepted.get("state"), {"QUEUED", "RUNNING"}, accepted)
        started = self.harness_command("wait-operation-start")
        self.assertIs(started.get("observed"), True, started)

        aged = self.harness_command("age-running-m3-operation")
        self.assertIs(aged.get("observed"), True, aged)
        status = successful_tool_result(self, call_tool(host, "nx_bridge_status", {}))
        self.assertIs(status.get("writes_halted"), True, status)
        self.assertEqual(status.get("ownership_state"), "UNKNOWN_FROZEN", status)
        timed_out = self.wait_host_operation(host, operation_id)
        self.assertEqual(timed_out.get("state"), "UNKNOWN", timed_out)
        self.assertEqual(timed_out.get("error", {}).get("code"), "OPERATION_EXECUTION_TIMEOUT", timed_out)

        self.harness_command("release-operation")
        deadline = time.monotonic() + 8.0
        completed_late = None
        while time.monotonic() < deadline:
            completed_late = successful_tool_result(self, call_tool(host, "nx_get_operation", {
                "operation_id": operation_id,
            }))
            if "late_completion" in completed_late:
                break
            time.sleep(0.02)
        self.assertIsInstance(completed_late, dict)
        self.assertEqual(completed_late.get("state"), "UNKNOWN", completed_late)
        self.assertEqual(completed_late.get("error", {}).get("code"), "OPERATION_EXECUTION_TIMEOUT", completed_late)
        self.assertEqual(completed_late.get("late_completion", {}).get("mutation_state"), "APPLIED", completed_late)

        replay = successful_tool_result(self, call_tool(host, "nx_execute_plan", execute_args))
        self.assertEqual(replay.get("state"), "UNKNOWN", replay)
        write_result, write_error = self._tool_error(call_tool(host, "nx_execute_plan", {
            "operation_id": "m6-write-after-late-" + uuid.uuid4().hex,
            "ownership_id": owner, "binding_epoch": epoch, "plan_id": extrude_plans[1],
        }))
        self.assertIs(write_result.get("isError"), True, write_result)
        self.assertEqual(write_error.get("code"), "OPERATION_OUTCOME_UNKNOWN", write_error)
        return_result, return_error = self._tool_error(call_tool(host, "nx_return", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        self.assertIs(return_result.get("isError"), True, return_result)
        self.assertEqual(return_error.get("code"), "OPERATION_OUTCOME_UNKNOWN", return_error)
        counts = self.fake_counts()
        self.assertEqual(counts.get("m3_execution_calls"), 2, counts)
        self.assertEqual(counts.get("m3_body_count"), 1, counts)
        self.assertEqual(counts.get("m3_mutation_count"), 2, counts)
        self.assertEqual(counts.get("rollback_count"), 0, counts)
        record("M6-m3-aged-late-stdio", {
            "operation_id": operation_id,
            "timeout_error": timed_out["error"]["code"],
            "late_adapter_state": completed_late["late_completion"]["mutation_state"],
            "replay_state": replay["state"],
            "subsequent_write_error": write_error["code"],
            "return_error": return_error["code"],
            "m3_adapter_execution_calls_including_sketch": counts["m3_execution_calls"],
            "fixture_timestamp_aged": True,
            "waited_for_real_120_second_threshold": False,
            "native_nx_undo_tested": False,
        })

    def test_unverified_parameter_rollback_freezes_writes_and_preserves_adapter_error(self):
        ready = self.start_harness()
        helper = self.start_helper()
        host, _ = self.start_host(timeout_seconds=10.0)
        grant = successful_tool_result(self, call_tool(host, "nx_takeover", {
            "expected_pid": int(ready["pid"]),
            "expected_part_path": "C:\\M2Fake\\model.prt",
            "artifact_scope": "saved_independent_copy", "authorize": True,
        }))
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]
        session = successful_tool_result(self, call_tool(host, "nx_get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        first = successful_tool_result(self, call_tool(host, "nx_prepare_operation", operation_prepare_args(
            "m6-rollback-failure-plan-1-" + uuid.uuid4().hex,
            owner, epoch, session["revision_id"], value="12",
        )))
        second = successful_tool_result(self, call_tool(host, "nx_prepare_operation", operation_prepare_args(
            "m6-rollback-failure-plan-2-" + uuid.uuid4().hex,
            owner, epoch, session["revision_id"], value="13",
        )))

        injection = self.harness_command("rollback-unverified-after-write")
        self.assertEqual(injection.get("event"), "rollback-unverified-after-write", injection)
        operation_id = "m6-rollback-unverified-" + uuid.uuid4().hex
        execute_args = {
            "operation_id": operation_id, "ownership_id": owner, "binding_epoch": epoch,
            "plan_id": first["result"]["plan"]["plan_id"],
        }
        accepted = successful_tool_result(self, call_tool(host, "nx_execute_plan", execute_args))
        self.assertIn(accepted.get("state"), {"QUEUED", "RUNNING"}, accepted)
        failed = self.wait_host_operation(host, operation_id)
        self.assertEqual(failed.get("state"), "UNKNOWN", failed)
        self.assertEqual(failed.get("error", {}).get("code"), "ROLLBACK_UNVERIFIED", failed)
        self.assertEqual(failed.get("error", {}).get("message"),
                         "fixture applied the edit but could not verify restoration after the update failure", failed)
        evidence = failed.get("result", {}).get("evidence", {})
        self.assertIs(evidence.get("rollback_attempted"), True, evidence)
        self.assertIs(evidence.get("rollback_readback_verified"), False, evidence)
        self.assertEqual(evidence.get("rollback_error"), "FIXTURE_UNDO_UNVERIFIED", evidence)

        replay = successful_tool_result(self, call_tool(host, "nx_execute_plan", execute_args))
        self.assertEqual(replay.get("state"), "UNKNOWN", replay)
        status = successful_tool_result(self, call_tool(host, "nx_bridge_status", {}))
        self.assertIs(status.get("writes_halted"), True, status)
        self.assertIs(status.get("model_write_enabled"), False, status)
        self.assertEqual(status.get("ownership_state"), "UNKNOWN_FROZEN", status)
        write_result, write_error = self._tool_error(call_tool(host, "nx_execute_plan", {
            "operation_id": "m6-rollback-failure-new-write-" + uuid.uuid4().hex,
            "ownership_id": owner, "binding_epoch": epoch, "plan_id": second["result"]["plan"]["plan_id"],
        }))
        self.assertIs(write_result.get("isError"), True, write_result)
        self.assertEqual(write_error.get("code"), "OPERATION_OUTCOME_UNKNOWN", write_error)
        return_result, return_error = self._tool_error(call_tool(host, "nx_return", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        self.assertIs(return_result.get("isError"), True, return_result)
        self.assertEqual(return_error.get("code"), "OPERATION_OUTCOME_UNKNOWN", return_error)

        counts = self.fake_counts()
        self.assertEqual(counts.get("execution_calls"), 1, counts)
        self.assertEqual(counts.get("write_attempt_count"), 1, counts)
        self.assertEqual(counts.get("rollback_count"), 1, counts)
        self.assertEqual(counts.get("mutation_count"), 1, counts)
        self.assertEqual(counts.get("current_value"), "12", counts)
        bridge_status = successful_helper_result(self, self.helper_call(helper, "bridge.status", {}))
        self.assertEqual(bridge_status.get("write_halt_reason"), "ROLLBACK_UNVERIFIED", bridge_status)
        record("M6-rollback-unverified-freeze", {
            "operation_id": operation_id,
            "state": failed["state"],
            "original_adapter_error": failed["error"],
            "rollback_evidence": evidence,
            "same_id_replay_state": replay["state"],
            "new_write_error": write_error["code"],
            "return_error": return_error["code"],
            "writes_halted": status["writes_halted"],
            "adapter_execution_calls": counts["execution_calls"],
            "native_nx_undo_failure_tested": False,
        })
