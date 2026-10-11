"""Focused Python protocol tests for M4 face selection and face-supported sketches."""

from __future__ import annotations

from pathlib import Path
import sys
import unittest
from unittest.mock import Mock


PROJECT_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from nx_sketch_mcp.bridge import BridgeFailure, JsonlBridge
from nx_sketch_mcp.config import config_from_object
from nx_sketch_mcp.server import McpServer, TOOLS


class M4PythonProtocolTests(unittest.TestCase):
    def setUp(self):
        self.server = McpServer(config_from_object({}))
        self.addCleanup(self.server.close)
        self.bridge_call = Mock(return_value={"ok": True})
        self.server._bridge.call = self.bridge_call

    @staticmethod
    def tool(name: str):
        return next(tool for tool in TOOLS if tool["name"] == name)

    @staticmethod
    def common_sketch_args():
        return {
            "operation_id": "m4-sketch-1",
            "ownership_id": "owner-1",
            "binding_epoch": 7,
            "revision_id": "rev-8",
            "geometries": [{
                "id": "circle-1", "type": "circle", "center_mm": [3, 4], "diameter_mm": 6,
            }],
        }

    def assert_tool_input_error(self, name: str, arguments: dict):
        response = self.server._tools_call(1, {"name": name, "arguments": arguments})
        self.assertEqual(response.get("error", {}).get("code"), -32602, response)
        self.bridge_call.assert_not_called()

    def test_query_faces_tool_schema_is_strict_and_read_only(self):
        query = self.tool("nx_query_faces")
        schema = query["inputSchema"]
        self.assertEqual(schema["required"], ["ownership_id", "binding_epoch", "revision_id", "selector"])
        self.assertEqual(set(schema["properties"]), set(schema["required"]) | {"body_id"})
        self.assertEqual(schema["properties"]["body_id"], {"type": "string", "minLength": 1})
        self.assertIs(schema["additionalProperties"], False)
        self.assertIs(query["annotations"]["readOnlyHint"], True)
        self.assertIs(query["annotations"]["destructiveHint"], False)

    def test_query_faces_routes_selector_unchanged_and_preserves_backend_result(self):
        selector = "  upper planar face beside the mounting boss  "
        backend_result = {
            "selection_status": "ambiguous",
            "selector_normalized": "upper planar face",
            "candidate_count": 2,
            "candidates": [{"face_id": "face-a"}, {"face_id": "face-b"}],
            "clarification_required": True,
            "revision_id": "rev-8",
        }
        self.bridge_call.return_value = backend_result
        response = self.server._tools_call(1, {
            "name": "nx_query_faces",
            "arguments": {
                "ownership_id": "owner-1", "binding_epoch": 7, "revision_id": "rev-8",
                "selector": selector, "body_id": "body-2",
            },
        })
        self.assertIs(response["result"]["isError"], False, response)
        self.assertIs(response["result"]["structuredContent"], backend_result)
        self.bridge_call.assert_called_once_with("model.query_faces", {
            "ownership_id": "owner-1", "binding_epoch": 7, "revision_id": "rev-8",
            "selector": selector, "body_id": "body-2",
        })

    def test_query_faces_rejects_missing_extra_and_empty_selector(self):
        base = {"ownership_id": "owner-1", "binding_epoch": 7, "revision_id": "rev-8", "selector": "top face"}
        for arguments in (
            {key: value for key, value in base.items() if key != "revision_id"},
            {**base, "extra": True},
            {**base, "selector": " \t "},
        ):
            with self.subTest(arguments=arguments):
                self.assert_tool_input_error("nx_query_faces", arguments)

    def test_query_faces_without_body_scope_omits_body_id(self):
        arguments = {
            "ownership_id": "owner-1", "binding_epoch": 7, "revision_id": "rev-8", "selector": "top face",
        }
        self.server._tools_call(9, {"name": "nx_query_faces", "arguments": arguments})
        self.bridge_call.assert_called_once_with("model.query_faces", arguments)

    def test_face_sketch_routes_only_face_mode_and_defaults_offset(self):
        arguments = self.common_sketch_args()
        arguments["support_face_selector"] = "front planar mounting face"
        response = self.server._tools_call(2, {"name": "nx_prepare_sketch", "arguments": arguments})
        self.assertIs(response["result"]["isError"], False, response)
        self.bridge_call.assert_called_once_with("operation.prepare_sketch", {
            **arguments,
            "face_origin_offset_mm": [0, 0],
        })
        sent = self.bridge_call.call_args.args[1]
        self.assertNotIn("plane", sent)
        self.assertNotIn("origin_mm", sent)

    def test_face_sketch_passes_explicit_offset_unchanged(self):
        arguments = self.common_sketch_args()
        arguments.update({
            "support_face_selector": "rear planar face",
            "face_origin_offset_mm": [-1.25, 2.5],
            "support_body_id": "body-3",
        })
        self.server._tools_call(3, {"name": "nx_prepare_sketch", "arguments": arguments})
        self.bridge_call.assert_called_once_with("operation.prepare_sketch", arguments)

    def test_face_sketch_accepts_body_scope_without_explicit_offset(self):
        arguments = self.common_sketch_args()
        arguments.update({
            "support_face_selector": "rear planar face",
            "support_body_id": "body-3",
        })
        self.server._tools_call(6, {"name": "nx_prepare_sketch", "arguments": arguments})
        self.bridge_call.assert_called_once_with("operation.prepare_sketch", {
            **arguments,
            "face_origin_offset_mm": [0, 0],
        })

    def test_legacy_plane_sketch_mode_remains_unchanged(self):
        arguments = self.common_sketch_args()
        arguments.update({"plane": "XZ", "origin_mm": [10, 20, 30]})
        self.server._tools_call(4, {"name": "nx_prepare_sketch", "arguments": arguments})
        self.bridge_call.assert_called_once_with("operation.prepare_sketch", arguments)

    def test_prepare_sketch_rejects_mixed_partial_and_malformed_modes(self):
        common = self.common_sketch_args()
        invalid = []
        invalid.append({**common, "plane": "XY"})
        invalid.append({**common, "origin_mm": [0, 0, 0]})
        invalid.append({**common, "support_face_selector": "top face", "plane": "XY", "origin_mm": [0, 0, 0]})
        invalid.append({**common, "support_face_selector": "top face", "origin_mm": [0, 0, 0]})
        invalid.append({**common, "support_face_selector": "top face", "plane": "XY"})
        invalid.append({**common, "plane": "XY", "origin_mm": [0, 0, 0], "support_body_id": "body-3"})
        invalid.append({**common, "support_face_selector": " \t "})
        invalid.append({**common, "support_face_selector": "top face", "face_origin_offset_mm": [0]})
        for arguments in invalid:
            with self.subTest(arguments=arguments):
                self.assert_tool_input_error("nx_prepare_sketch", arguments)

    def test_bridge_accepts_model_query_faces_as_supported_command(self):
        bridge = JsonlBridge(config_from_object({}))
        self.addCleanup(bridge.close)
        with self.assertRaises(BridgeFailure) as raised:
            bridge.call("model.query_faces", {})
        self.assertEqual(raised.exception.code, "BRIDGE_UNCONFIGURED")


if __name__ == "__main__":
    unittest.main()
