"""Real child-process checks for the MCP JSONL host, without NX or a bridge config."""

from __future__ import annotations

import json
import os
from pathlib import Path
import sys
import subprocess
import tempfile
import time
import unittest
import uuid
from unittest.mock import Mock, call

from evidence_recorder import record
from stdio_client import ProtocolFailure, StdioMcpProcess, assert_stdout_jsonl_only

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "src"))
from nx_sketch_mcp.bridge import BridgeFailure
from nx_sketch_mcp.config import config_from_object
from nx_sketch_mcp.server import McpServer


PROJECT_ROOT = Path(__file__).resolve().parents[2]
EXPECTED_TOOLS = {
    "nx_ensure_ready",
    "nx_get_capabilities", "nx_bridge_status", "nx_bridge_ping", "nx_takeover", "nx_return",
    "nx_get_session", "nx_inspect_part", "nx_inspect_sketch", "nx_query_faces", "nx_get_operation",
    "nx_cancel_operation", "nx_prepare_operation", "nx_prepare_sketch", "nx_prepare_extrude",
    "nx_prepare_thread", "nx_prepare_helical_groove",
    "nx_execute_plan", "nx_verify_part", "nx_save_copy",
}
PROTOCOL_VERSION = "2025-11-25"


class McpStdioProcessTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="nx-m2-stdio-")
        self.temp_path = Path(self.temp.name)
        self.config_path = self.temp_path / "empty-config.json"
        self.config_path.write_text("{}\n", encoding="utf-8")
        env = os.environ.copy()
        existing = env.get("PYTHONPATH", "")
        paths = [str(PROJECT_ROOT / "src"), str(PROJECT_ROOT)]
        if existing:
            paths.append(existing)
        env["PYTHONPATH"] = os.pathsep.join(paths)
        env["PYTHONUTF8"] = "1"
        self.child = StdioMcpProcess(
            [sys.executable, "-m", "nx_sketch_mcp", "--config", str(self.config_path)],
            cwd=str(PROJECT_ROOT), env=env,
        )
        self.addCleanup(self.child.terminate_own_process)
        self.addCleanup(self.temp.cleanup)

        initialized = self.child.request("initialize", {
            "protocolVersion": PROTOCOL_VERSION,
            "capabilities": {},
            "clientInfo": {"name": "m2-validation", "version": "1"},
        })
        result = initialized.get("result")
        self.assertIsInstance(result, dict, initialized)
        self.assertEqual(result.get("protocolVersion"), PROTOCOL_VERSION)
        self.assertIsInstance(result.get("serverInfo"), dict)
        self.assertIsInstance(result["serverInfo"].get("version"), str)
        self.child.notification("notifications/initialized")
        self.child.assert_quiet(duration=0.25)
        ping = self.child.request("ping")
        self.assertIn("result", ping, ping)

    def tearDown(self):
        if self.child.process.poll() is None:
            self.child.close_stdin()
            try:
                self.child.wait(timeout=5.0)
            except subprocess.TimeoutExpired:
                self.child.terminate_own_process()
                self.fail("MCP child did not exit after stdin EOF")
        assert_stdout_jsonl_only(self.child.all_stdout_bytes())

    def test_static_capabilities_are_discoverable_without_bridge_config(self):
        listing = self.child.request("tools/list")
        tools = listing.get("result", {}).get("tools")
        self.assertIsInstance(tools, list, listing)
        names = {item.get("name") for item in tools if isinstance(item, dict)}
        self.assertEqual(names, EXPECTED_TOOLS)

        response = self.child.request("tools/call", {
            "name": "nx_get_capabilities", "arguments": {},
        })
        result = response.get("result")
        self.assertIsInstance(result, dict, response)
        self.assertFalse(result.get("isError", False), response)
        payload = extract_structured_result(result)
        self.assertIsInstance(payload, dict, result)
        self.assertEqual(payload.get("protocol_version"), PROTOCOL_VERSION)
        self.assertIs(payload.get("bridge_configured"), False)
        self.assertEqual(payload.get("available_operations"), [
            "bridge.status", "bridge.dispatch_probe", "ownership.takeover", "ownership.return",
            "model.get_session", "model.inspect_part", "model.inspect_sketch", "model.query_faces", "operation.get",
            "operation.cancel", "operation.prepare", "operation.prepare_sketch", "operation.prepare_extrude",
            "operation.prepare_thread", "operation.prepare_helical_groove",
            "operation.execute_plan", "operation.verify_part",
            "operation.save_copy",
        ])
        self.assertEqual(payload.get("read_support", {}).get("sketch_geometry_detail"), ["LINE", "ARC"])
        native_geometry = payload.get("model_native_geometry_support", {})
        sketch_support = native_geometry.get("sketch", {})
        self.assertEqual(sketch_support.get("planes"), ["XY", "XZ", "YZ"])
        self.assertEqual(sketch_support.get("face_mode", {}).get("argument"), "support_face_selector")
        self.assertTrue(any("M4_SUBTRACT_CONTOUR_EDIT_UNSUPPORTED" in item
                            for item in native_geometry.get("limitations", [])))
        self.assertEqual(payload.get("model_access"), {
            "read": False, "write": False, "control": False,
        })
        self.assertIs(payload.get("model_read_enabled"), False)
        self.assertEqual(payload.get("ownership_state"), "UNKNOWN")
        self.assertEqual(payload.get("unavailable_reason", {}).get("code"), "BRIDGE_UNCONFIGURED")
        self.assertEqual(payload.get("default_artifact_scope"), "saved_independent_copy")
        self.assertEqual(payload.get("supported_artifact_scopes"), ["saved_independent_copy", "current_work_part"])
        self.assertIn("current_work_part", payload.get("model_save_copy_support", {}))
        self.assertEqual(payload.get("revision_schema"), "m2-semantic-v1")
        for key in ("model_write_enabled", "model_control_enabled"):
            self.assertIs(payload.get(key), False)
        record("M2-1-02", {"discovered_tools": sorted(names), "bridge_configured": False,
                           "available_operations": payload["available_operations"],
                           "model_access": payload["model_access"]})

    def test_m2_tools_publish_confirmation_and_required_argument_schemas(self):
        tools = self.child.request("tools/list")["result"]["tools"]
        by_name = {item["name"]: item for item in tools}

        takeover = by_name["nx_takeover"]
        takeover_schema = takeover["inputSchema"]
        self.assertEqual(takeover_schema["required"], [
            "expected_pid", "expected_part_path", "artifact_scope", "authorize",
        ])
        self.assertEqual(takeover_schema["properties"]["expected_pid"], {"type": "integer", "minimum": 1})
        self.assertEqual(takeover_schema["properties"]["expected_part_path"], {"type": "string", "maxLength": 4096})
        self.assertEqual(takeover_schema["properties"]["artifact_scope"], {
            "type": "string", "enum": ["saved_independent_copy", "current_work_part"],
        })
        self.assertEqual(takeover_schema["properties"]["authorize"], {"type": "boolean", "const": True})
        self.assertIn("explicitly confirmed", takeover["description"])

        for name in ("nx_return", "nx_get_session", "nx_inspect_part"):
            schema = by_name[name]["inputSchema"]
            self.assertEqual(schema["required"], ["ownership_id", "binding_epoch"], name)
            self.assertEqual(schema["properties"]["ownership_id"], {"type": "string", "minLength": 1}, name)
            self.assertEqual(schema["properties"]["binding_epoch"], {"type": "integer", "minimum": 1}, name)
        sketch_schema = by_name["nx_inspect_sketch"]["inputSchema"]
        self.assertEqual(sketch_schema["required"], ["ownership_id", "binding_epoch", "sketch_id"])
        self.assertEqual(sketch_schema["properties"]["binding_epoch"], {"type": "integer", "minimum": 1})
        self.assertEqual(sketch_schema["properties"]["sketch_id"], {"type": "string", "minLength": 1})

        for name in ("nx_get_operation", "nx_cancel_operation"):
            schema = by_name[name]["inputSchema"]
            self.assertEqual(schema["required"], ["operation_id"], name)
            self.assertEqual(schema["properties"]["operation_id"], {
                "type": "string", "minLength": 1, "maxLength": 128,
            }, name)
        prepare_schema = by_name["nx_prepare_operation"]["inputSchema"]
        self.assertEqual(prepare_schema["required"], [
            "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id",
            "constraint_id", "value", "unit",
        ])
        execute_schema = by_name["nx_execute_plan"]["inputSchema"]
        self.assertEqual(execute_schema["required"], ["operation_id", "ownership_id", "binding_epoch", "plan_id"])

        verify_schema = by_name["nx_verify_part"]["inputSchema"]
        self.assertEqual(verify_schema["required"], [
            "operation_id", "ownership_id", "binding_epoch", "execute_operation_id",
        ])
        self.assertEqual(verify_schema["additionalProperties"], False)
        save_schema = by_name["nx_save_copy"]["inputSchema"]
        self.assertEqual(save_schema["required"], [
            "operation_id", "ownership_id", "binding_epoch", "target_path", "reopen",
        ])
        self.assertEqual(save_schema["properties"]["target_path"]["type"], "string")
        self.assertEqual(save_schema["properties"]["reopen"]["type"], "boolean")
        self.assertEqual(save_schema["additionalProperties"], False)

    def test_capabilities_reflect_cached_ownership_state_and_bridge_failure(self):
        config = config_from_object({
            "pipe_name": "capabilities-test",
            "token_file": str((self.temp_path / "token.txt").resolve()),
            "helper_path": str(Path(sys.executable).resolve()),
        })
        server = McpServer(config)
        self.addCleanup(server.close)
        status_call = Mock(return_value={
            "ownership_state": "HUMAN_OWNED", "model_read_available": False,
        })
        server._bridge.call = status_call

        human = server._capabilities()
        status_call.assert_called_once_with("bridge.status", {})
        self.assertIs(human["model_access"]["read"], False)
        self.assertEqual(human["ownership_state"], "HUMAN_OWNED")
        self.assertEqual(human["unavailable_reason"]["code"], "OWNERSHIP_REQUIRED")
        self.assertIs(human["model_read_implemented"], True)

        server._bridge.call = Mock(return_value={
            "ownership_state": "AI_OWNED", "model_read_available": True,
            "model_write_enabled": True, "model_save_copy_available": True, "writes_halted": False,
            "binding_identity": {"artifact_scope": "saved_independent_copy", "is_read_only": False},
        })
        owned = server._capabilities()
        self.assertIs(owned["model_access"]["read"], True)
        self.assertEqual(owned["ownership_state"], "AI_OWNED")
        self.assertEqual(owned["artifact_scope"], "saved_independent_copy")
        self.assertIs(owned["model_save_copy_enabled"], True)
        self.assertIsNone(owned["unavailable_reason"])

        server._bridge.call = Mock(return_value={
            "ownership_state": "AI_OWNED", "model_read_available": True,
            "model_write_enabled": True, "model_save_copy_available": True, "writes_halted": False,
            "binding_identity": {"artifact_scope": "current_work_part", "is_read_only": False},
        })
        current_work_part = server._capabilities()
        self.assertEqual(current_work_part["artifact_scope"], "current_work_part")
        self.assertIs(current_work_part["model_write_enabled"], True)
        self.assertIs(current_work_part["model_save_copy_enabled"], False)

        server._bridge.call = Mock(return_value={
            "ownership_state": "AI_OWNED", "model_read_available": True,
            "model_write_enabled": False, "model_save_copy_available": True, "writes_halted": False,
            "binding_identity": {"artifact_scope": "saved_independent_copy", "is_read_only": True},
        })
        read_only = server._capabilities()
        self.assertEqual(read_only["artifact_scope"], "saved_independent_copy")
        self.assertIs(read_only["model_write_enabled"], False)
        self.assertIs(read_only["model_save_copy_enabled"], False)

        server._bridge.call = Mock(side_effect=BridgeFailure("BRIDGE_UNAVAILABLE", "local helper not ready"))
        unavailable = server._capabilities()
        self.assertIs(unavailable["model_access"]["read"], False)
        self.assertEqual(unavailable["unavailable_reason"]["code"], "BRIDGE_UNAVAILABLE")

    def test_protocol_and_parameter_errors_are_explicit(self):
        unknown = self.child.request("tools/call", {
            "name": "nx_not_a_tool", "arguments": {},
        })
        self.assertEqual(unknown.get("error", {}).get("code"), -32602, unknown)
        invalid = self.child.request("tools/call", {
            "name": "nx_bridge_ping", "arguments": {"nonce": ""},
        })
        self.assertEqual(invalid.get("error", {}).get("code"), -32602, invalid)

        # The tool exists, but it cannot be presented as a successful bridge result
        # when this host was deliberately started without bridge configuration.
        disconnected = self.child.request("tools/call", {
            "name": "nx_bridge_status", "arguments": {},
        })
        tool_result = disconnected.get("result")
        self.assertIsInstance(tool_result, dict, disconnected)
        self.assertIs(tool_result.get("isError"), True, disconnected)
        payload = extract_structured_result(tool_result)
        if isinstance(payload, dict):
            self.assertIsNot(payload.get("ok"), True, disconnected)
        record("M2-1-03", {"unknown_tool_error": unknown["error"]["code"],
                           "invalid_nonce_error": invalid["error"]["code"],
                           "unconfigured_status_is_tool_error": tool_result["isError"]})

    def test_ownership_and_model_tools_route_arguments_to_bridge_commands(self):
        server = McpServer(config_from_object({}))
        self.addCleanup(server.close)
        bridge_call = Mock(return_value={"ok": True})
        server._bridge.call = bridge_call

        routed_calls = [
            ("nx_takeover", {
                "expected_pid": 4152,
                "expected_part_path": "C:/work/part.prt",
                "artifact_scope": "saved_independent_copy",
                "authorize": True,
            }, "ownership.takeover", {
                "expected_pid": 4152,
                "expected_part_path": "C:/work/part.prt",
                "artifact_scope": "saved_independent_copy",
                "authorize": True,
            }),
            ("nx_takeover", {
                "expected_pid": 4152,
                "expected_part_path": "",
                "artifact_scope": "current_work_part",
                "authorize": True,
            }, "ownership.takeover", {
                "expected_pid": 4152,
                "expected_part_path": "",
                "artifact_scope": "current_work_part",
                "authorize": True,
            }),
            ("nx_return", {"ownership_id": "own-1", "binding_epoch": 11}, "ownership.return",
             {"ownership_id": "own-1", "binding_epoch": 11}),
            ("nx_get_session", {"ownership_id": "own-2", "binding_epoch": 12}, "model.get_session",
             {"ownership_id": "own-2", "binding_epoch": 12}),
            ("nx_inspect_part", {"ownership_id": "own-3", "binding_epoch": 13}, "model.inspect_part",
             {"ownership_id": "own-3", "binding_epoch": 13}),
            ("nx_inspect_sketch", {"ownership_id": "own-4", "binding_epoch": 14, "sketch_id": "sketch-5"},
             "model.inspect_sketch", {"ownership_id": "own-4", "binding_epoch": 14, "sketch_id": "sketch-5"}),
            ("nx_get_operation", {"operation_id": "op-1"}, "operation.get", {"operation_id": "op-1"}),
            ("nx_cancel_operation", {"operation_id": "op-2"}, "operation.cancel", {"operation_id": "op-2"}),
            ("nx_prepare_operation", {
                "operation_id": "op-3", "ownership_id": "own-5", "binding_epoch": 15,
                "revision_id": "4", "sketch_id": "sketch-6", "constraint_id": "constraint-7",
                "value": "12.5", "unit": "mm",
            }, "operation.prepare", {
                "operation_id": "op-3", "ownership_id": "own-5", "binding_epoch": 15,
                "revision_id": "4", "sketch_id": "sketch-6", "constraint_id": "constraint-7",
                "value": "12.5", "unit": "mm",
            }),
            ("nx_prepare_sketch", {
                "operation_id": "op-m3-sketch", "ownership_id": "own-m3-sketch", "binding_epoch": 19,
                "revision_id": "10", "plane": "XY", "origin_mm": [0, 0, 0],
                "geometries": [{"id": "circle-a", "type": "circle", "center_mm": [2, 3], "diameter_mm": 8}],
            }, "operation.prepare_sketch", {
                "operation_id": "op-m3-sketch", "ownership_id": "own-m3-sketch", "binding_epoch": 19,
                "revision_id": "10", "plane": "XY", "origin_mm": [0, 0, 0],
                "geometries": [{"id": "circle-a", "type": "circle", "center_mm": [2, 3], "diameter_mm": 8}],
            }),
            ("nx_prepare_extrude", {
                "operation_id": "op-m3-extrude", "ownership_id": "own-m3-extrude", "binding_epoch": 20,
                "revision_id": "11", "sketch_id": "sketch-m3", "mode": "unite",
                "direction": "negative", "extent": "through_all", "target_body_id": "body-m3",
            }, "operation.prepare_extrude", {
                "operation_id": "op-m3-extrude", "ownership_id": "own-m3-extrude", "binding_epoch": 20,
                "revision_id": "11", "sketch_id": "sketch-m3", "mode": "unite",
                "direction": "negative", "extent": "through_all", "target_body_id": "body-m3",
            }),
            ("nx_execute_plan", {
                "operation_id": "op-4", "ownership_id": "own-6", "binding_epoch": 16, "plan_id": "plan-8",
            }, "operation.execute_plan", {
                "operation_id": "op-4", "ownership_id": "own-6", "binding_epoch": 16, "plan_id": "plan-8",
            }),
            ("nx_verify_part", {
                "operation_id": "op-5", "ownership_id": "own-7", "binding_epoch": 17,
                "execute_operation_id": "op-4",
            }, "operation.verify_part", {
                "operation_id": "op-5", "ownership_id": "own-7", "binding_epoch": 17,
                "execute_operation_id": "op-4",
            }),
            ("nx_save_copy", {
                "operation_id": "op-6", "ownership_id": "own-8", "binding_epoch": 18,
                "target_path": "C:/work/saved-copy.prt", "reopen": True,
            }, "operation.save_copy", {
                "operation_id": "op-6", "ownership_id": "own-8", "binding_epoch": 18,
                "target_path": "C:/work/saved-copy.prt", "reopen": True,
            }),
        ]
        for name, arguments, _command, _params in routed_calls:
            response = server._tools_call(1, {"name": name, "arguments": arguments})
            result = response.get("result")
            self.assertIsInstance(result, dict, response)
            self.assertIs(result.get("isError"), False, response)

        empty_saved_path = server._tools_call(2, {"name": "nx_takeover", "arguments": {
            "expected_pid": 4152, "expected_part_path": "",
            "artifact_scope": "saved_independent_copy", "authorize": True,
        }})
        self.assertEqual(empty_saved_path.get("error", {}).get("code"), -32602, empty_saved_path)

        self.assertEqual(bridge_call.call_args_list, [call(command, params) for _, _, command, params in routed_calls])

    def test_bridge_failures_are_forwarded_as_mcp_tool_errors(self):
        server = McpServer(config_from_object({}))
        self.addCleanup(server.close)
        server._bridge.call = Mock(side_effect=BridgeFailure("OWNERSHIP_REQUIRED", "session is not AI_OWNED"))

        response = server._tools_call(7, {
            "name": "nx_inspect_part", "arguments": {"ownership_id": "own-9", "binding_epoch": 42},
        })
        result = response.get("result")
        self.assertIsInstance(result, dict, response)
        self.assertIs(result.get("isError"), True, response)
        self.assertEqual(result.get("structuredContent"), {
            "error": {"code": "OWNERSHIP_REQUIRED", "message": "session is not AI_OWNED"},
        })

    def test_execute_unknown_client_gate_waits_for_queried_terminal_result(self):
        terminal_states = ("SUCCEEDED", "FAILED", "CANCELLED")
        for terminal_state in terminal_states:
            with self.subTest(terminal_state=terminal_state):
                config = config_from_object({
                    "pipe_name": "client-unknown-" + uuid.uuid4().hex,
                    "token_file": str((self.temp_path / ("token-" + terminal_state + ".txt")).resolve()),
                    "helper_path": str(Path(sys.executable).resolve()),
                })
                server = McpServer(config)
                self.addCleanup(server.close)
                calls = []
                operation_id = "uncertain-" + terminal_state.lower()
                operation_states = [
                    {"ok": True, "operation_id": operation_id, "state": "RUNNING", "writes_halted": False},
                    {"ok": True, "operation_id": operation_id, "state": terminal_state, "writes_halted": False},
                ]
                base_status = {
                    "ownership_state": "AI_OWNED", "model_read_available": True,
                    "model_write_enabled": True, "writes_halted": False,
                    "write_halt_reason": None, "active_operations": 1,
                }

                def bridge_call(command, params):
                    calls.append((command, dict(params)))
                    if command == "operation.execute_plan":
                        if params.get("operation_id") == operation_id:
                            raise BridgeFailure("BRIDGE_TIMEOUT", "helper response was lost", outcome_unknown=True)
                        return {
                            "ok": True, "operation_id": params["operation_id"], "kind": "execute_parameter_edit",
                            "state": "QUEUED", "cancellable": True, "result": None, "error": None,
                            "writes_halted": False,
                        }
                    if command == "operation.get":
                        return operation_states.pop(0)
                    if command == "bridge.status":
                        return dict(base_status)
                    raise AssertionError("unexpected bridge command: " + command)

                server._bridge.call = Mock(side_effect=bridge_call)
                original_args = {
                    "operation_id": operation_id, "ownership_id": "owner-1",
                    "binding_epoch": 1, "plan_id": "plan-1",
                }
                initial = server._tools_call(20, {"name": "nx_execute_plan", "arguments": original_args})
                initial_tool = initial.get("result")
                self.assertIs(initial_tool.get("isError"), True, initial)
                self.assertEqual(extract_structured_result(initial_tool).get("error", {}).get("code"), "BRIDGE_TIMEOUT", initial)
                self.assertEqual([item for item in calls if item[0] == "operation.execute_plan"], [
                    ("operation.execute_plan", original_args),
                ])

                bridge_status_response = server._tools_call(21, {"name": "nx_bridge_status", "arguments": {}})
                status_tool = bridge_status_response["result"]
                status = extract_structured_result(status_tool)
                self.assertIs(status.get("writes_halted"), True, status)
                self.assertIs(status.get("model_write_enabled"), False, status)
                self.assertEqual(status.get("write_halt_reason"), "CLIENT_OUTCOME_UNKNOWN", status)

                capabilities = server._capabilities()
                self.assertIs(capabilities.get("model_write_enabled"), False, capabilities)
                self.assertIs(capabilities.get("model_access", {}).get("write"), False, capabilities)
                self.assertEqual(capabilities.get("unknown_operation_ids"), [operation_id], capabilities)

                blocked_args = dict(original_args)
                blocked_args["operation_id"] = "new-write-while-unknown"
                blocked = server._tools_call(22, {"name": "nx_execute_plan", "arguments": blocked_args})
                self.assertIs(blocked["result"].get("isError"), True, blocked)
                self.assertEqual(extract_structured_result(blocked["result"]).get("error", {}).get("code"),
                                 "CLIENT_OUTCOME_UNKNOWN", blocked)
                self.assertEqual(len([item for item in calls if item[0] == "operation.execute_plan"]), 1, calls)

                running = server._tools_call(23, {"name": "nx_get_operation", "arguments": {"operation_id": operation_id}})
                running_record = extract_structured_result(running["result"])
                self.assertEqual(running_record.get("state"), "RUNNING", running_record)
                self.assertIs(running_record.get("writes_halted"), False, running_record)
                still_halted = server._capabilities()
                self.assertIs(still_halted.get("model_write_enabled"), False, still_halted)

                terminal = server._tools_call(24, {"name": "nx_get_operation", "arguments": {"operation_id": operation_id}})
                terminal_record = extract_structured_result(terminal["result"])
                self.assertEqual(terminal_record.get("state"), terminal_state, terminal_record)
                self.assertIs(terminal_record.get("writes_halted"), False, terminal_record)
                released = server._capabilities()
                self.assertIs(released.get("model_write_enabled"), True, released)
                self.assertNotIn("unknown_operation_ids", released)

                next_args = dict(original_args)
                next_args["operation_id"] = "write-after-reconcile"
                next_write = server._tools_call(25, {"name": "nx_execute_plan", "arguments": next_args})
                self.assertIs(next_write["result"].get("isError"), False, next_write)
                execute_calls = [item for item in calls if item[0] == "operation.execute_plan"]
                self.assertEqual([item[1]["operation_id"] for item in execute_calls], [operation_id, "write-after-reconcile"])
                record("M2-5-client-unknown-gate", {
                    "resolved_terminal_state": terminal_state,
                    "initial_bridge_timeout": True,
                    "running_record_kept_writes_halted": True,
                    "new_write_blocked_before_resolution": True,
                    "original_execute_call_count": 1,
                    "writes_resumed_after_query": True,
                })

    def test_takeover_requires_the_explicit_confirmation_phrase(self):
        server = McpServer(config_from_object({}))
        self.addCleanup(server.close)
        bridge_call = Mock(return_value={"ok": True})
        server._bridge.call = bridge_call

        response = server._tools_call(8, {
            "name": "nx_takeover",
            "arguments": {
                "expected_pid": 4152,
                "expected_part_path": "C:/work/part.prt",
                "artifact_scope": "saved_independent_copy",
                "authorize": False,
            },
        })
        self.assertEqual(response.get("error", {}).get("code"), -32602, response)
        bridge_call.assert_not_called()

    def test_notification_has_no_response_and_eof_exits_cleanly(self):
        started = time.monotonic()
        self.child.notification("notifications/progress", {"progressToken": "m2-check", "progress": 1})
        self.child.assert_quiet(duration=0.25)
        # The child stays usable after an ignored notification.
        self.assertIn("result", self.child.request("ping"))

        self.child.close_stdin()
        exit_code = self.child.wait(timeout=5.0)
        elapsed_ms = (time.monotonic() - started) * 1000.0
        raw = self.child.all_stdout_bytes()
        line_count = assert_stdout_jsonl_only(raw)
        self.assertEqual(exit_code, 0)
        self.assertGreaterEqual(line_count, 3)
        self.assertLess(elapsed_ms, 5000.0)
        record("M2-1-04", {"notifications_responded": False, "eof_exit_code": exit_code,
                           "elapsed_ms": round(elapsed_ms, 3), "stdout_jsonl_lines": line_count})

    def test_initialization_state_machine_and_protocol_negotiation(self):
        child = self._new_uninitialized_child()
        try:
            # A malformed no-id object is not a JSON-RPC notification.
            child.send({"unexpected": "field"})
            malformed = child.receive_json(timeout=3.0)
            self.assertEqual(malformed.get("error", {}).get("code"), -32600, malformed)
            self.assertIsNone(malformed.get("id"), malformed)

            # An initialized notification before initialize is ignored.
            child.notification("notifications/initialized")
            child.assert_quiet(duration=0.15)
            not_ready = child.request("tools/list")
            self.assertEqual(not_ready.get("error", {}).get("code"), -32002, not_ready)

            # Valid but unknown protocol versions negotiate the server's supported version.
            negotiated = self._initialize(child, protocol_version="2099-01-01")
            self.assertEqual(negotiated.get("result", {}).get("protocolVersion"), PROTOCOL_VERSION, negotiated)

            # A second initialize while awaiting the initialized notification is rejected.
            duplicate = self._initialize(child, protocol_version=PROTOCOL_VERSION)
            self.assertEqual(duplicate.get("error", {}).get("code"), -32600, duplicate)
            still_not_ready = child.request("tools/list")
            self.assertEqual(still_not_ready.get("error", {}).get("code"), -32002, still_not_ready)

            child.notification("notifications/initialized")
            child.assert_quiet(duration=0.15)
            ready = child.request("tools/list")
            self.assertEqual({tool["name"] for tool in ready["result"]["tools"]}, EXPECTED_TOOLS)
            record("M2-1-lifecycle", {"malformed_no_id_error": malformed["error"]["code"],
                                      "preinitialize_notification_kept_not_ready": True,
                                      "unknown_protocol_negotiated": negotiated["result"]["protocolVersion"],
                                      "duplicate_initialize_error": duplicate["error"]["code"],
                                      "discovered_tool_count": len(ready["result"]["tools"])})
        finally:
            child.close_stdin()
            child.wait(timeout=5.0)
            assert_stdout_jsonl_only(child.all_stdout_bytes())
            child.terminate_own_process()

    def test_initialize_requires_string_version_fields(self):
        child = self._new_uninitialized_child()
        try:
            missing_version = self._initialize(child, protocol_version=None, include_protocol_version=False)
            self.assertEqual(missing_version.get("error", {}).get("code"), -32602, missing_version)
            wrong_client_version = self._initialize(child, client_version=1)
            self.assertEqual(wrong_client_version.get("error", {}).get("code"), -32602, wrong_client_version)
            missing_client_version = self._initialize(child, include_client_version=False)
            self.assertEqual(missing_client_version.get("error", {}).get("code"), -32602, missing_client_version)

            accepted = self._initialize(child, protocol_version=PROTOCOL_VERSION)
            self.assertEqual(accepted.get("result", {}).get("protocolVersion"), PROTOCOL_VERSION, accepted)
            child.notification("notifications/initialized")
            child.assert_quiet(duration=0.15)
            record("M2-1-initialize-parameters", {"invalid_parameter_errors": [
                missing_version["error"]["code"], wrong_client_version["error"]["code"],
                missing_client_version["error"]["code"]],
                "accepted_version": accepted["result"]["protocolVersion"]})
        finally:
            child.close_stdin()
            child.wait(timeout=5.0)
            assert_stdout_jsonl_only(child.all_stdout_bytes())
            child.terminate_own_process()

    def _new_uninitialized_child(self):
        env = os.environ.copy()
        existing = env.get("PYTHONPATH", "")
        paths = [str(PROJECT_ROOT / "src"), str(PROJECT_ROOT)]
        if existing:
            paths.append(existing)
        env["PYTHONPATH"] = os.pathsep.join(paths)
        env["PYTHONUTF8"] = "1"
        return StdioMcpProcess(
            [sys.executable, "-m", "nx_sketch_mcp", "--config", str(self.config_path)],
            cwd=str(PROJECT_ROOT), env=env,
        )

    @staticmethod
    def _initialize(child, *, protocol_version=PROTOCOL_VERSION, include_protocol_version=True,
                    client_version="1", include_client_version=True):
        params = {"capabilities": {}, "clientInfo": {"name": "m2-validation"}}
        if include_client_version:
            params["clientInfo"]["version"] = client_version
        if include_protocol_version:
            params["protocolVersion"] = protocol_version
        return child.request("initialize", params)


def extract_structured_result(result: dict):
    structured = result.get("structuredContent")
    if isinstance(structured, dict):
        return structured
    content = result.get("content")
    if isinstance(content, list):
        for item in content:
            if isinstance(item, dict) and item.get("type") == "text" and isinstance(item.get("text"), str):
                try:
                    value = json.loads(item["text"])
                except json.JSONDecodeError:
                    continue
                if isinstance(value, dict):
                    return value
    return None


if __name__ == "__main__":
    unittest.main()
