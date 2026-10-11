"""Offline contract tests for thread and helical-groove MCP preparation tools."""

from __future__ import annotations

from pathlib import Path
import sys
import unittest
from unittest.mock import Mock, call
from dataclasses import replace


PROJECT_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from nx_sketch_mcp.config import config_from_object
from nx_sketch_mcp.bridge import BridgeFailure
from nx_sketch_mcp.server import McpServer, TOOLS


THREAD_ARGUMENTS = {
    "operation_id": "thread-op-1",
    "ownership_id": "owner-1",
    "binding_epoch": 7,
    "revision_id": "rev-9",
    "target_body_id": "body-2",
    "axis": "Y",
    "axis_origin_mm": [10.0, 20.0, 30.0],
    "start_mm": -2.5,
    "length_mm": 4.0,
    "handedness": "left",
    "direction": "positive",
    "major_diameter_mm": 10.0,
    "minor_diameter_mm": 8.0,
    "pitch_mm": 1.0,
}

GROOVE_ARGUMENTS = {
    "operation_id": "groove-op-1",
    "ownership_id": "owner-2",
    "binding_epoch": 8,
    "revision_id": "rev-10",
    "target_body_id": "body-3",
    "axis": "Z",
    "axis_origin_mm": [0.0, 0.0, 0.0],
    "start_mm": 1.25,
    "length_mm": 8.0,
    "handedness": "right",
    "lead_mm": 4.0,
    "root_diameter_mm": 8.0,
    "reference_diameter_mm": 10.0,
    "outer_diameter_mm": 12.0,
    "space_width_mm": 1.0,
    "flank_angle_deg": 10.0,
    "phase_deg": -30.0,
}


class HelicalToolTests(unittest.TestCase):
    def setUp(self) -> None:
        self.server = McpServer(config_from_object({}))
        self.addCleanup(self.server.close)
        self.bridge_call = Mock(return_value={"accepted": True})
        self.server._bridge.call = self.bridge_call

    def _call_tool(self, name: str, arguments: dict) -> dict:
        return self.server._tools_call(1, {"name": name, "arguments": arguments})

    def _assert_invalid_without_bridge(self, name: str, arguments: dict) -> None:
        self.bridge_call.reset_mock()
        response = self._call_tool(name, arguments)
        self.assertEqual(response.get("error", {}).get("code"), -32602, response)
        self.bridge_call.assert_not_called()

    def test_tool_schemas_require_every_argument_and_reject_extras(self) -> None:
        by_name = {tool["name"]: tool for tool in TOOLS}
        expected_fields = {
            "nx_prepare_thread": set(THREAD_ARGUMENTS),
            "nx_prepare_helical_groove": set(GROOVE_ARGUMENTS),
        }
        for name, fields in expected_fields.items():
            with self.subTest(tool=name):
                schema = by_name[name]["inputSchema"]
                self.assertEqual(set(schema["properties"]), fields)
                self.assertEqual(set(schema["required"]), fields)
                self.assertIs(schema["additionalProperties"], False)
                self.assertEqual(schema["properties"]["axis_origin_mm"]["items"]["maximum"], 1_000_000)
                self.assertEqual(schema["properties"]["start_mm"]["maximum"], 1_000_000)
                self.assertEqual(schema["properties"]["target_body_id"]["maxLength"], 4096)

    def test_documented_numeric_and_identifier_limits_are_inclusive(self) -> None:
        thread = dict(THREAD_ARGUMENTS)
        thread.update({
            "operation_id": "o" * 128,
            "ownership_id": "u" * 128,
            "revision_id": "r" * 128,
            "target_body_id": "b" * 4096,
            "axis_origin_mm": [1_000_000, 0, -1_000_000],
            "start_mm": -1_000_000,
            "length_mm": 500,
            "pitch_mm": 1,
        })
        groove = dict(GROOVE_ARGUMENTS)
        groove.update({
            "axis_origin_mm": [1_000_000, 0, -1_000_000],
            "start_mm": 1_000_000,
            "length_mm": 100,
            "lead_mm": 2,
            "root_diameter_mm": 1,
            "reference_diameter_mm": 2,
            "outer_diameter_mm": 1_000_000,
            "space_width_mm": 0.01,
            "flank_angle_deg": 0.0001,
        })

        self.assertIs(self._call_tool("nx_prepare_thread", thread)["result"]["isError"], False)
        self.assertIs(self._call_tool("nx_prepare_helical_groove", groove)["result"]["isError"], False)
        self.assertEqual(self.bridge_call.call_args_list, [
            call("operation.prepare_thread", thread),
            call("operation.prepare_helical_groove", groove),
        ])

    def test_valid_requests_forward_exact_arguments_to_expected_bridge_operations(self) -> None:
        thread = self._call_tool("nx_prepare_thread", dict(THREAD_ARGUMENTS))
        groove = self._call_tool("nx_prepare_helical_groove", dict(GROOVE_ARGUMENTS))

        self.assertIs(thread["result"]["isError"], False, thread)
        self.assertIs(groove["result"]["isError"], False, groove)
        self.assertEqual(self.bridge_call.call_args_list, [
            call("operation.prepare_thread", THREAD_ARGUMENTS),
            call("operation.prepare_helical_groove", GROOVE_ARGUMENTS),
        ])

    def test_thread_rejects_malformed_values_and_unsupported_geometry(self) -> None:
        invalid = []

        bad = dict(THREAD_ARGUMENTS)
        bad["length_mm"] = True
        invalid.append(bad)

        bad = dict(THREAD_ARGUMENTS)
        bad["axis_origin_mm"] = [0.0, float("nan"), 0.0]
        invalid.append(bad)

        bad = dict(THREAD_ARGUMENTS)
        bad["start_mm"] = float("inf")
        invalid.append(bad)

        bad = dict(THREAD_ARGUMENTS)
        bad["unexpected"] = 1
        invalid.append(bad)

        bad = dict(THREAD_ARGUMENTS)
        bad.pop("pitch_mm")
        invalid.append(bad)

        for field, value in (("length_mm", 0), ("major_diameter_mm", -1), ("pitch_mm", 0)):
            bad = dict(THREAD_ARGUMENTS)
            bad[field] = value
            invalid.append(bad)

        bad = dict(THREAD_ARGUMENTS)
        bad["minor_diameter_mm"] = bad["major_diameter_mm"]
        invalid.append(bad)

        bad = dict(THREAD_ARGUMENTS)
        bad["length_mm"] = 500.1
        invalid.append(bad)

        bad = dict(THREAD_ARGUMENTS)
        bad["direction"] = "forward"
        invalid.append(bad)

        bad = dict(THREAD_ARGUMENTS)
        bad["handedness"] = "clockwise"
        invalid.append(bad)

        bad = dict(THREAD_ARGUMENTS)
        bad["axis_origin_mm"] = [1_000_000.1, 0.0, 0.0]
        invalid.append(bad)

        bad = dict(THREAD_ARGUMENTS)
        bad["major_diameter_mm"] = 1_000_001
        invalid.append(bad)

        bad = dict(THREAD_ARGUMENTS)
        bad["ownership_id"] = "o" * 129
        invalid.append(bad)

        bad = dict(THREAD_ARGUMENTS)
        bad["target_body_id"] = "b" * 4097
        invalid.append(bad)

        for index, arguments in enumerate(invalid):
            with self.subTest(index=index, arguments=arguments):
                self._assert_invalid_without_bridge("nx_prepare_thread", arguments)

    def test_helical_groove_rejects_malformed_values_ranges_and_open_profiles(self) -> None:
        invalid = []

        bad = dict(GROOVE_ARGUMENTS)
        bad["lead_mm"] = True
        invalid.append(bad)

        bad = dict(GROOVE_ARGUMENTS)
        bad["phase_deg"] = float("nan")
        invalid.append(bad)

        bad = dict(GROOVE_ARGUMENTS)
        bad["axis_origin_mm"] = [0.0, 0.0, float("inf")]
        invalid.append(bad)

        bad = dict(GROOVE_ARGUMENTS)
        bad["unknown"] = 1
        invalid.append(bad)

        bad = dict(GROOVE_ARGUMENTS)
        bad.pop("space_width_mm")
        invalid.append(bad)

        for field, value in (
            ("length_mm", 0),
            ("lead_mm", 0),
            ("root_diameter_mm", -1),
            ("space_width_mm", 0),
            ("flank_angle_deg", 0),
            ("flank_angle_deg", 45),
        ):
            bad = dict(GROOVE_ARGUMENTS)
            bad[field] = value
            invalid.append(bad)

        bad = dict(GROOVE_ARGUMENTS)
        bad["length_mm"] = 200.1
        invalid.append(bad)

        bad = dict(GROOVE_ARGUMENTS)
        bad["root_diameter_mm"] = bad["reference_diameter_mm"]
        invalid.append(bad)

        bad = dict(GROOVE_ARGUMENTS)
        bad["outer_diameter_mm"] = bad["reference_diameter_mm"]
        invalid.append(bad)

        bad = dict(GROOVE_ARGUMENTS)
        bad["phase_deg"] = 360.1
        invalid.append(bad)

        # The linear flank profile crosses zero at the root radius.
        bad = dict(GROOVE_ARGUMENTS)
        bad["space_width_mm"] = 0.1
        bad["flank_angle_deg"] = 20.0
        invalid.append(bad)

        # Adjacent turns overlap because the outer profile width exceeds the lead.
        bad = dict(GROOVE_ARGUMENTS)
        bad["lead_mm"] = 1.0
        bad["length_mm"] = 1.0
        invalid.append(bad)

        bad = dict(GROOVE_ARGUMENTS)
        bad["handedness"] = "clockwise"
        invalid.append(bad)

        bad = dict(GROOVE_ARGUMENTS)
        bad["start_mm"] = -1_000_000.1
        invalid.append(bad)

        bad = dict(GROOVE_ARGUMENTS)
        bad["outer_diameter_mm"] = 1_000_001
        invalid.append(bad)

        for index, arguments in enumerate(invalid):
            with self.subTest(index=index, arguments=arguments):
                self._assert_invalid_without_bridge("nx_prepare_helical_groove", arguments)

    def test_capabilities_use_live_helical_flags_and_default_false_without_a_bridge(self) -> None:
        capabilities = self.server._capabilities()
        self.assertIs(capabilities["model_helical_adapter_available"], False)
        self.assertIs(capabilities["model_helical_write_enabled"], False)
        self.assertIn("operation.prepare_thread", capabilities["available_operations"])
        self.assertIn(
            "operation.prepare_helical_groove",
            capabilities["model_native_geometry_support"]["prepare_operations"],
        )

    def test_capabilities_report_true_only_when_live_status_reports_helical_support(self) -> None:
        self.server._config = replace(self.server._config, pipe_name="test-pipe")
        self.bridge_call.return_value = {
            "ownership_state": "AI_OWNED",
            "model_read_available": True,
            "model_write_enabled": True,
            "model_helical_adapter_available": True,
            "model_helical_write_enabled": True,
            "writes_halted": False,
            "binding_identity": {"artifact_scope": "saved_independent_copy"},
        }
        capabilities = self.server._capabilities()
        self.assertIs(capabilities["model_helical_adapter_available"], True)
        self.assertIs(capabilities["model_helical_write_enabled"], True)

        self.bridge_call.side_effect = BridgeFailure("BRIDGE_UNAVAILABLE", "transport unavailable")
        capabilities = self.server._capabilities()
        self.assertIs(capabilities["model_helical_adapter_available"], False)
        self.assertIs(capabilities["model_helical_write_enabled"], False)


if __name__ == "__main__":
    unittest.main()
