"""Minimal MCP 2025-11-25 stdio server for local bridge diagnostics."""

from __future__ import annotations

import argparse
import json
import math
import re
import sys
from typing import Any

from . import __version__
from .bridge import BridgeFailure, JsonlBridge
from .config import BridgeConfig, ConfigError, load_config
from .jsonutil import dumps_line, loads_unique
from .readiness import NxReadiness


PROTOCOL_VERSION = "2025-11-25"
SERVER_NAME = "nx-sketch-mcp"
MAX_MCP_LINE_BYTES = 1_048_576
_OPERATION_ID_SCHEMA = {"type": "string", "minLength": 1, "maxLength": 128}
_POSITIVE_INTEGER_SCHEMA = {"type": "integer", "minimum": 1}
_NONEMPTY_STRING_SCHEMA = {"type": "string", "minLength": 1}
_NUMBER_SCHEMA = {"type": "number"}
_POINT_2_SCHEMA = {"type": "array", "items": _NUMBER_SCHEMA, "minItems": 2, "maxItems": 2}
_ORIGIN_3_SCHEMA = {"type": "array", "items": _NUMBER_SCHEMA, "minItems": 3, "maxItems": 3}
_AXIS_SCHEMA = {"type": "string", "enum": ["X", "Y", "Z"]}
_HANDEDNESS_SCHEMA = {"type": "string", "enum": ["right", "left"]}
_HELICAL_IDENTIFIER_SCHEMA = {"type": "string", "minLength": 1, "maxLength": 128}
_HELICAL_TARGET_BODY_SCHEMA = {"type": "string", "minLength": 1, "maxLength": 4096}
_HELICAL_COORDINATE_SCHEMA = {"type": "number", "minimum": -1_000_000, "maximum": 1_000_000}
_HELICAL_POSITIVE_MM_SCHEMA = {"type": "number", "exclusiveMinimum": 0, "maximum": 1_000_000}
_HELICAL_ORIGIN_3_SCHEMA = {
    "type": "array", "items": _HELICAL_COORDINATE_SCHEMA, "minItems": 3, "maxItems": 3,
}
_DECIMAL_PATTERN = r"^[+-]?(?:(?:0|[1-9][0-9]*)(?:\.[0-9]+)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?$"
_FINITE_DECIMAL = re.compile(_DECIMAL_PATTERN, re.ASCII)

_NATIVE_GEOMETRY_SCHEMA = {
    "anyOf": [
        {
            "type": "object",
            "properties": {
                "id": {"type": "string", "minLength": 1, "maxLength": 128},
                "type": {"const": "line"},
                "start_mm": dict(_POINT_2_SCHEMA),
                "end_mm": dict(_POINT_2_SCHEMA),
            },
            "required": ["id", "type", "start_mm", "end_mm"],
            "additionalProperties": False,
        },
        {
            "type": "object",
            "properties": {
                "id": {"type": "string", "minLength": 1, "maxLength": 128},
                "type": {"const": "rectangle"},
                "corner_mm": dict(_POINT_2_SCHEMA),
                "width_mm": {"type": "number", "exclusiveMinimum": 0},
                "height_mm": {"type": "number", "exclusiveMinimum": 0},
            },
            "required": ["id", "type", "corner_mm", "width_mm", "height_mm"],
            "additionalProperties": False,
        },
        {
            "type": "object",
            "properties": {
                "id": {"type": "string", "minLength": 1, "maxLength": 128},
                "type": {"const": "circle"},
                "center_mm": dict(_POINT_2_SCHEMA),
                "diameter_mm": {"type": "number", "exclusiveMinimum": 0},
            },
            "required": ["id", "type", "center_mm", "diameter_mm"],
            "additionalProperties": False,
        },
    ]
}


class _InvalidToolArguments(ValueError):
    """An MCP tool argument did not satisfy its strict input contract."""


def _empty_schema() -> dict[str, Any]:
    return {"type": "object", "properties": {}, "additionalProperties": False}


TOOLS: list[dict[str, Any]] = [
    {
        "name": "nx_ensure_ready",
        "title": "Prepare NX connection",
        "description": (
            "Explicitly prepares the configured NX connection with a startup/probe budget up to "
            "30 seconds, plus bounded helper cleanup. "
            "Reuses a connected NX session; otherwise the optional launch profile starts a dedicated "
            "NX GUI with the installed startup bridge. Returns READY, STARTING, NEEDS_MANUAL_ACTION, "
            "or FAILED. Existing user NX sessions and license dialogs are never closed. This does "
            "not take ownership, open a model, or replay model writes. Call before nx_takeover."
        ),
        "inputSchema": _empty_schema(),
        "annotations": {
            "readOnlyHint": False,
            "destructiveHint": False,
            "idempotentHint": True,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_get_capabilities",
        "title": "NX bridge capabilities",
        "description": (
            "Returns bridge capabilities, active-operation count, and write-halt state. Model "
            "writes require model_write_enabled=true for an AI_OWNED session with "
            "writes_halted=false; supported operations, geometry, units, and constraints are "
            "listed in model_write_support and model_native_geometry_support. "
            "model_helical_adapter_available and model_helical_write_enabled report whether the "
            "loaded bridge supports and currently permits helical thread/groove preparation. "
            "Ownership takeover requires explicit human confirmation."
        ),
        "inputSchema": _empty_schema(),
        "annotations": {
            "readOnlyHint": True,
            "destructiveHint": False,
            "idempotentHint": True,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_bridge_status",
        "title": "Local bridge status",
        "description": (
            "Returns cached local bridge status only. It includes model_write_enabled, "
            "writes_halted, write_halt_reason, and active_operations so write freezes and "
            "unknown outcomes are visible. It does not read or change NX model data."
        ),
        "inputSchema": _empty_schema(),
        "annotations": {
            "readOnlyHint": True,
            "destructiveHint": False,
            "idempotentHint": True,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_bridge_ping",
        "title": "Local bridge diagnostic ping",
        "description": (
            "Sends a diagnostic ping with the caller-provided nonce. The ping does not read or "
            "change NX model data. Nonce length must be 1 through 128 characters."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "nonce": {"type": "string", "minLength": 1, "maxLength": 128},
            },
            "required": ["nonce"],
            "additionalProperties": False,
        },
        "annotations": {
            "readOnlyHint": True,
            "destructiveHint": False,
            "idempotentHint": True,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_takeover",
        "title": "Take NX session ownership",
        "description": (
            "Requests AI ownership of the identified NX session. Call this only after a human has "
            "explicitly confirmed the takeover, and pass authorize=true. Use saved_independent_copy "
            "for a saved standalone part, or current_work_part to inspect and make supported edits "
            "to the active in-memory part while preserving its identity. current_work_part cannot "
            "be saved with nx_save_copy. The NX adapter checks the live part structure and reports "
            "its supported operations and dependencies."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "expected_pid": {"type": "integer", "minimum": 1},
                "expected_part_path": {"type": "string", "maxLength": 4096},
                "artifact_scope": {"type": "string", "enum": ["saved_independent_copy", "current_work_part"]},
                "authorize": {"type": "boolean", "const": True},
            },
            "required": ["expected_pid", "expected_part_path", "artifact_scope", "authorize"],
            "additionalProperties": False,
        },
        "annotations": {
            "readOnlyHint": False,
            "destructiveHint": False,
            "idempotentHint": False,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_return",
        "title": "Return NX session ownership",
        "description": "Returns AI ownership of the NX session identified by ownership_id and binding_epoch.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "ownership_id": {"type": "string", "minLength": 1},
                "binding_epoch": {"type": "integer", "minimum": 1},
            },
            "required": ["ownership_id", "binding_epoch"],
            "additionalProperties": False,
        },
        "annotations": {
            "readOnlyHint": False,
            "destructiveHint": False,
            "idempotentHint": False,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_get_session",
        "title": "Get NX session",
        "description": "Reads session metadata for ownership_id and binding_epoch. Model reads are enabled only while the session is AI_OWNED.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "ownership_id": {"type": "string", "minLength": 1},
                "binding_epoch": {"type": "integer", "minimum": 1},
            },
            "required": ["ownership_id", "binding_epoch"],
            "additionalProperties": False,
        },
        "annotations": {
            "readOnlyHint": True,
            "destructiveHint": False,
            "idempotentHint": True,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_inspect_part",
        "title": "Inspect NX part",
        "description": "Reads the owned NX part identified by ownership_id and binding_epoch. This model read is enabled only while the session is AI_OWNED.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "ownership_id": {"type": "string", "minLength": 1},
                "binding_epoch": {"type": "integer", "minimum": 1},
            },
            "required": ["ownership_id", "binding_epoch"],
            "additionalProperties": False,
        },
        "annotations": {
            "readOnlyHint": True,
            "destructiveHint": False,
            "idempotentHint": True,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_query_faces",
        "title": "Query NX faces",
        "description": (
            "Searches faces in the owned NX part using a natural-language selector. The selector is "
            "passed to the model query unchanged; ambiguous and unsupported selectors are reported "
            "by the backend for clarification. Optional body_id scopes the search to one solid body; "
            "when omitted, uniqueness is evaluated across the part. This is a read-only model query."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "ownership_id": dict(_NONEMPTY_STRING_SCHEMA),
                "binding_epoch": dict(_POSITIVE_INTEGER_SCHEMA),
                "revision_id": dict(_NONEMPTY_STRING_SCHEMA),
                "selector": dict(_NONEMPTY_STRING_SCHEMA),
                "body_id": dict(_NONEMPTY_STRING_SCHEMA),
            },
            "required": ["ownership_id", "binding_epoch", "revision_id", "selector"],
            "additionalProperties": False,
        },
        "annotations": {
            "readOnlyHint": True,
            "destructiveHint": False,
            "idempotentHint": True,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_inspect_sketch",
        "title": "Inspect NX sketch",
        "description": "Reads sketch_id from the owned NX part identified by ownership_id and binding_epoch. This model read is enabled only while the session is AI_OWNED.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "ownership_id": {"type": "string", "minLength": 1},
                "binding_epoch": {"type": "integer", "minimum": 1},
                "sketch_id": {"type": "string", "minLength": 1},
            },
            "required": ["ownership_id", "binding_epoch", "sketch_id"],
            "additionalProperties": False,
        },
        "annotations": {
            "readOnlyHint": True,
            "destructiveHint": False,
            "idempotentHint": True,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_get_operation",
        "title": "Get NX operation",
        "description": "Returns the current record for a caller-supplied operation_id, including terminal or unknown status.",
        "inputSchema": {
            "type": "object",
            "properties": {"operation_id": dict(_OPERATION_ID_SCHEMA)},
            "required": ["operation_id"],
            "additionalProperties": False,
        },
        "annotations": {
            "readOnlyHint": True,
            "destructiveHint": False,
            "idempotentHint": True,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_cancel_operation",
        "title": "Cancel NX operation",
        "description": "Requests cancellation of a queued cancellable operation by operation_id and returns its operation record.",
        "inputSchema": {
            "type": "object",
            "properties": {"operation_id": dict(_OPERATION_ID_SCHEMA)},
            "required": ["operation_id"],
            "additionalProperties": False,
        },
        "annotations": {
            "readOnlyHint": False,
            "destructiveHint": False,
            "idempotentHint": True,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_prepare_sketch",
        "title": "Prepare NX native sketch",
        "description": (
            "Prepares a sketch on XY, XZ, or YZ from local millimeter coordinates and a world "
            "origin, or on a uniquely selected support face using local millimeter coordinates. "
            "Face mode can optionally be scoped to support_body_id and offset within the face. "
            "Supports nonzero line segments, rectangles, and circles; this does not execute the plan."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "operation_id": dict(_OPERATION_ID_SCHEMA),
                "ownership_id": dict(_NONEMPTY_STRING_SCHEMA),
                "binding_epoch": dict(_POSITIVE_INTEGER_SCHEMA),
                "revision_id": dict(_NONEMPTY_STRING_SCHEMA),
                "plane": {"type": "string", "enum": ["XY", "XZ", "YZ"]},
                "origin_mm": dict(_ORIGIN_3_SCHEMA),
                "support_face_selector": dict(_NONEMPTY_STRING_SCHEMA),
                "support_body_id": dict(_NONEMPTY_STRING_SCHEMA),
                "face_origin_offset_mm": {**dict(_POINT_2_SCHEMA), "default": [0, 0]},
                "geometries": {
                    "type": "array",
                    "items": _NATIVE_GEOMETRY_SCHEMA,
                    "minItems": 1,
                    "maxItems": 32,
                },
            },
            "required": [
                "operation_id", "ownership_id", "binding_epoch", "revision_id", "geometries",
            ],
            "oneOf": [
                {
                    "required": ["plane", "origin_mm"],
                    "not": {"anyOf": [
                        {"required": ["support_face_selector"]},
                        {"required": ["face_origin_offset_mm"]},
                        {"required": ["support_body_id"]},
                    ]},
                },
                {
                    "required": ["support_face_selector"],
                    "not": {"anyOf": [
                        {"required": ["plane"]},
                        {"required": ["origin_mm"]},
                    ]},
                },
            ],
            "additionalProperties": False,
        },
        "annotations": {
            "readOnlyHint": True,
            "destructiveHint": False,
            "idempotentHint": False,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_prepare_extrude",
        "title": "Prepare NX extrusion",
        "description": (
            "Prepares a finite or through-all create, unite, or subtract extrusion from an existing "
            "sketch containing exactly one full circle or one closed axis-aligned rectangle made "
            "of four straight lines in sketch coordinates. Open profiles, arbitrary polygons, "
            "and multiple profiles are rejected with UNSUPPORTED_PROFILE. Boolean modes require "
            "an explicit target body; this does not execute the plan."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "operation_id": dict(_OPERATION_ID_SCHEMA),
                "ownership_id": dict(_NONEMPTY_STRING_SCHEMA),
                "binding_epoch": dict(_POSITIVE_INTEGER_SCHEMA),
                "revision_id": dict(_NONEMPTY_STRING_SCHEMA),
                "sketch_id": dict(_NONEMPTY_STRING_SCHEMA),
                "mode": {"type": "string", "enum": ["create", "unite", "subtract"]},
                "direction": {"type": "string", "enum": ["positive", "negative"]},
                "extent": {"type": "string", "enum": ["finite", "through_all"]},
                "depth_mm": {"type": "number", "exclusiveMinimum": 0},
                "target_body_id": dict(_NONEMPTY_STRING_SCHEMA),
            },
            "required": [
                "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id",
                "mode", "direction", "extent",
            ],
            "allOf": [
                {
                    "if": {"properties": {"mode": {"const": "create"}}},
                    "then": {
                        "properties": {"extent": {"const": "finite"}},
                        "required": ["depth_mm"],
                        "not": {"required": ["target_body_id"]},
                    },
                },
                {
                    "if": {"properties": {"mode": {"enum": ["unite", "subtract"]}}},
                    "then": {"required": ["target_body_id"]},
                },
                {
                    "if": {"properties": {"extent": {"const": "finite"}}},
                    "then": {"required": ["depth_mm"]},
                    "else": {"not": {"required": ["depth_mm"]}},
                },
            ],
            "additionalProperties": False,
        },
        "annotations": {
            "readOnlyHint": True,
            "destructiveHint": False,
            "idempotentHint": False,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_prepare_thread",
        "title": "Prepare external solid thread",
        "description": (
            "Prepares a single-start, external solid thread with a fixed 60-degree profile on an "
            "existing target body when model_helical_adapter_available and "
            "model_helical_write_enabled are true; it does not execute the plan. The caller must provide the "
            "major diameter, minor diameter, and pitch explicitly because no drawing or model "
            "assumptions are made. Axis is X, Y, or Z; axis_origin_mm is a world XYZ point and "
            "start_mm is the signed axial offset from it. direction selects the positive or negative "
            "axis, and handedness selects the helix sense. Linear inputs are limited to an absolute "
            "value of 1,000,000 mm. length_mm / pitch_mm must not exceed 500."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "operation_id": dict(_OPERATION_ID_SCHEMA),
                "ownership_id": dict(_HELICAL_IDENTIFIER_SCHEMA),
                "binding_epoch": dict(_POSITIVE_INTEGER_SCHEMA),
                "revision_id": dict(_HELICAL_IDENTIFIER_SCHEMA),
                "target_body_id": dict(_HELICAL_TARGET_BODY_SCHEMA),
                "axis": dict(_AXIS_SCHEMA),
                "axis_origin_mm": dict(_HELICAL_ORIGIN_3_SCHEMA),
                "start_mm": dict(_HELICAL_COORDINATE_SCHEMA),
                "length_mm": dict(_HELICAL_POSITIVE_MM_SCHEMA),
                "handedness": dict(_HANDEDNESS_SCHEMA),
                "direction": {"type": "string", "enum": ["positive", "negative"]},
                "major_diameter_mm": dict(_HELICAL_POSITIVE_MM_SCHEMA),
                "minor_diameter_mm": dict(_HELICAL_POSITIVE_MM_SCHEMA),
                "pitch_mm": dict(_HELICAL_POSITIVE_MM_SCHEMA),
            },
            "required": [
                "operation_id", "ownership_id", "binding_epoch", "revision_id", "target_body_id",
                "axis", "axis_origin_mm", "start_mm", "length_mm", "handedness", "direction",
                "major_diameter_mm", "minor_diameter_mm", "pitch_mm",
            ],
            "additionalProperties": False,
        },
        "annotations": {
            "readOnlyHint": True,
            "destructiveHint": False,
            "idempotentHint": False,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_prepare_helical_groove",
        "title": "Prepare helical groove",
        "description": (
            "Prepares a subtractive helical groove whose axis advances along the positive selected "
            "axis; axis_origin_mm is a world XYZ point and start_mm is its signed axial offset. "
            "handedness determines the sign of angular travel. The operation requires "
            "model_helical_adapter_available and model_helical_write_enabled to be true. The caller must provide the "
            "root, reference, and outer diameters and the groove space width explicitly; no drawing "
            "assumptions are made. space_width_mm is the axial groove width at the reference "
            "diameter. The flank half-width varies linearly with radius using flank_angle_deg. "
            "length_mm / lead_mm must not exceed 50. The prepared profile uses a fixed 96-point-per- "
            "turn interpolated spline and 0.001 mm distance tolerance; linear inputs are limited to "
            "an absolute value of 1,000,000 mm. The profile must remain "
            "positive at the root and avoid overlap between adjacent turns. This only prepares a "
            "plan and does not execute it."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "operation_id": dict(_OPERATION_ID_SCHEMA),
                "ownership_id": dict(_HELICAL_IDENTIFIER_SCHEMA),
                "binding_epoch": dict(_POSITIVE_INTEGER_SCHEMA),
                "revision_id": dict(_HELICAL_IDENTIFIER_SCHEMA),
                "target_body_id": dict(_HELICAL_TARGET_BODY_SCHEMA),
                "axis": dict(_AXIS_SCHEMA),
                "axis_origin_mm": dict(_HELICAL_ORIGIN_3_SCHEMA),
                "start_mm": dict(_HELICAL_COORDINATE_SCHEMA),
                "length_mm": dict(_HELICAL_POSITIVE_MM_SCHEMA),
                "handedness": dict(_HANDEDNESS_SCHEMA),
                "lead_mm": dict(_HELICAL_POSITIVE_MM_SCHEMA),
                "root_diameter_mm": dict(_HELICAL_POSITIVE_MM_SCHEMA),
                "reference_diameter_mm": dict(_HELICAL_POSITIVE_MM_SCHEMA),
                "outer_diameter_mm": dict(_HELICAL_POSITIVE_MM_SCHEMA),
                "space_width_mm": dict(_HELICAL_POSITIVE_MM_SCHEMA),
                "flank_angle_deg": {"type": "number", "exclusiveMinimum": 0, "exclusiveMaximum": 45},
                "phase_deg": {"type": "number", "minimum": -360, "maximum": 360},
            },
            "required": [
                "operation_id", "ownership_id", "binding_epoch", "revision_id", "target_body_id",
                "axis", "axis_origin_mm", "start_mm", "length_mm", "handedness", "lead_mm",
                "root_diameter_mm", "reference_diameter_mm", "outer_diameter_mm", "space_width_mm",
                "flank_angle_deg", "phase_deg",
            ],
            "additionalProperties": False,
        },
        "annotations": {
            "readOnlyHint": True,
            "destructiveHint": False,
            "idempotentHint": False,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_prepare_operation",
        "title": "Prepare NX sketch operation",
        "description": (
            "Dynamically checks the identified Driving constraint, unique expression relation, "
            "Millimeters Work Part, exact mm expression unit, and current geometry before "
            "preparing an immutable plan for a positive finite value. Supported signatures are "
            "DiameterDim on one full Arc; HorizontalDim on ArcCenter plus Point for X offset; "
            "and HorizontalDim or VerticalDim on both endpoints of one Line for the corresponding "
            "projected length. Ambiguous, changed, or unsupported targets are refused. This does "
            "not execute the plan."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "operation_id": dict(_OPERATION_ID_SCHEMA),
                "ownership_id": dict(_NONEMPTY_STRING_SCHEMA),
                "binding_epoch": dict(_POSITIVE_INTEGER_SCHEMA),
                "revision_id": dict(_NONEMPTY_STRING_SCHEMA),
                "sketch_id": dict(_NONEMPTY_STRING_SCHEMA),
                "constraint_id": dict(_NONEMPTY_STRING_SCHEMA),
                "value": {"type": "string", "minLength": 1, "maxLength": 128, "pattern": _DECIMAL_PATTERN},
                "unit": dict(_NONEMPTY_STRING_SCHEMA),
            },
            "required": [
                "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id",
                "constraint_id", "value", "unit",
            ],
            "additionalProperties": False,
        },
        "annotations": {
            "readOnlyHint": True,
            "destructiveHint": False,
            "idempotentHint": False,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_execute_plan",
        "title": "Execute NX plan",
        "description": (
            "Enqueues a previously prepared sketch, extrusion, or parameter-edit plan when "
            "model_write_enabled is true and writes are not halted, then promptly returns its "
            "queryable operation record."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "operation_id": dict(_OPERATION_ID_SCHEMA),
                "ownership_id": dict(_NONEMPTY_STRING_SCHEMA),
                "binding_epoch": dict(_POSITIVE_INTEGER_SCHEMA),
                "plan_id": dict(_NONEMPTY_STRING_SCHEMA),
            },
            "required": ["operation_id", "ownership_id", "binding_epoch", "plan_id"],
            "additionalProperties": False,
        },
        "annotations": {
            "readOnlyHint": False,
            "destructiveHint": True,
            "idempotentHint": False,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_verify_part",
        "title": "Verify NX parameter edit",
        "description": (
            "Reads the live NX expression, measured sketch geometry, sketch feature history, and "
            "dimension associations for a succeeded execute operation. The verification itself is "
            "queryable and deduplicated by operation_id."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "operation_id": dict(_OPERATION_ID_SCHEMA),
                "ownership_id": dict(_NONEMPTY_STRING_SCHEMA),
                "binding_epoch": dict(_POSITIVE_INTEGER_SCHEMA),
                "execute_operation_id": dict(_OPERATION_ID_SCHEMA),
            },
            "required": ["operation_id", "ownership_id", "binding_epoch", "execute_operation_id"],
            "additionalProperties": False,
        },
        "annotations": {
            "readOnlyHint": True,
            "destructiveHint": False,
            "idempotentHint": False,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_save_copy",
        "title": "Save and reopen NX copy",
        "description": (
            "Explicitly saves the currently owned Work Part to a new, nonexistent .prt path, then "
            "reopens that copy and compares actual expressions, sketch geometry, feature history, "
            "and dimensional associations. reopen=true is required for the round-trip evidence. "
            "The active binding and ownership credentials rotate after success; use the returned "
            "new_ownership_id and new_binding_epoch for later tools."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "operation_id": dict(_OPERATION_ID_SCHEMA),
                "ownership_id": dict(_NONEMPTY_STRING_SCHEMA),
                "binding_epoch": dict(_POSITIVE_INTEGER_SCHEMA),
                "target_path": {"type": "string", "minLength": 1, "maxLength": 4096},
                "reopen": {"type": "boolean", "const": True},
            },
            "required": ["operation_id", "ownership_id", "binding_epoch", "target_path", "reopen"],
            "additionalProperties": False,
        },
        "annotations": {
            "readOnlyHint": False,
            "destructiveHint": True,
            "idempotentHint": False,
            "openWorldHint": False,
        },
    },
]


class McpServer:
    def __init__(self, config: BridgeConfig) -> None:
        self._config = config
        self._bridge = JsonlBridge(config)
        self._unknown_operation_ids: set[str] = set()
        self._initialization_started = False
        self._initialized = False

    def close(self) -> None:
        self._bridge.close()

    def handle_message(self, message: Any) -> dict[str, Any] | None:
        if not isinstance(message, dict):
            return self._error(None, -32600, "Invalid Request")

        request_id = message.get("id")
        valid_id = "id" not in message or self._valid_id(request_id)
        if message.get("jsonrpc") != "2.0" or not isinstance(message.get("method"), str):
            return self._error(request_id if valid_id else None, -32600, "Invalid Request")
        if set(message) - {"jsonrpc", "id", "method", "params"}:
            return self._error(request_id if valid_id else None, -32600, "Invalid Request")
        if not valid_id:
            return self._error(None, -32600, "Invalid Request")

        # A notification has no id and never receives a JSON-RPC response.
        if "id" not in message:
            self._handle_notification(message)
            return None

        method = message["method"]
        params = message.get("params", {})
        if not isinstance(params, dict):
            return self._error(request_id, -32602, "Invalid params")

        if method == "ping":
            if params:
                return self._error(request_id, -32602, "Invalid params")
            return self._result(request_id, {})
        if method == "initialize":
            return self._initialize(request_id, params)
        if method.startswith("notifications/"):
            return self._error(request_id, -32600, "Notifications must not include an id")
        if not self._initialized:
            return self._error(request_id, -32002, "Server not initialized")
        if method == "tools/list":
            return self._tools_list(request_id, params)
        if method == "tools/call":
            return self._tools_call(request_id, params)
        return self._error(request_id, -32601, "Method not found")

    @staticmethod
    def _valid_id(value: Any) -> bool:
        if isinstance(value, bool):
            return False
        if isinstance(value, str):
            return True
        if isinstance(value, int):
            return True
        if isinstance(value, float):
            return math.isfinite(value)
        return False

    def _handle_notification(self, message: dict[str, Any]) -> None:
        if message.get("jsonrpc") != "2.0" or not isinstance(message.get("method"), str):
            return
        method = message["method"]
        if method == "notifications/initialized":
            params = message.get("params", {})
            if self._initialization_started and isinstance(params, dict) and not params:
                self._initialized = True
                return
        # Other notifications are intentionally ignored. JSON-RPC notifications have no response.

    def _initialize(self, request_id: Any, params: dict[str, Any]) -> dict[str, Any]:
        if self._initialization_started:
            return self._error(request_id, -32600, "Already initialized")
        protocol_version = params.get("protocolVersion")
        if not isinstance(protocol_version, str) or not protocol_version:
            return self._error(request_id, -32602, "Invalid params")
        if not isinstance(params.get("capabilities"), dict):
            return self._error(request_id, -32602, "Invalid params")
        client_info = params.get("clientInfo")
        if (
            not isinstance(client_info, dict)
            or not isinstance(client_info.get("name"), str)
            or not isinstance(client_info.get("version"), str)
        ):
            return self._error(request_id, -32602, "Invalid params")
        self._initialization_started = True
        return self._result(
            request_id,
            {
                "protocolVersion": PROTOCOL_VERSION,
                "capabilities": {"tools": {}},
                "serverInfo": {"name": SERVER_NAME, "version": __version__},
                "instructions": (
                    "Call nx_ensure_ready to prepare or reuse the NX connection; STARTING means retry "
                    "after NX startup or resolving a license dialog. This does not take ownership. "
                    "Bridge status and diagnostic ping use local bridge data without launching NX. Model reads require "
                    "AI_OWNED session state. Call nx_takeover only after explicit human confirmation "
                    "using authorize=true and an artifact_scope supported by the live NX adapter. "
                    "saved_independent_copy selects a saved standalone part; current_work_part retains "
                    "the active in-memory part and does not support nx_save_copy. Prepare native "
                    "sketches with nx_prepare_sketch, extrusions with nx_prepare_extrude, external "
                    "single-start 60-degree solid threads with nx_prepare_thread, and helical grooves "
                    "with nx_prepare_helical_groove, or supported parameter changes with "
                    "nx_prepare_operation; execute only a returned plan with "
                    "nx_execute_plan. Writes require an AI_OWNED session, "
                    "the parameter-edit adapter, and an unhalted write gate. Supported targets are "
                    "DiameterDim on one full Arc, HorizontalDim ArcCenter plus Point X offset, and "
                    "HorizontalDim or VerticalDim between both endpoints of one Line. Use the "
                    "operation_id to query or cancel queued work. After a successful execute, call "
                    "nx_verify_part with its execute_operation_id to read back the live expression, "
                    "geometry, feature history, and association. Then nx_save_copy explicitly saves "
                    "and reopens a new, nonexistent .prt path; it rotates ownership_id and "
                    "binding_epoch, so use the credentials returned by that operation. Inspect "
                    "write_halt_reason and active_operations when writes are frozen or an outcome is unknown."
                ),
            },
        )

    def _tools_list(self, request_id: Any, params: dict[str, Any]) -> dict[str, Any]:
        if set(params) - {"cursor", "_meta"}:
            return self._error(request_id, -32602, "Invalid params")
        if "cursor" in params:
            return self._error(request_id, -32602, "Pagination is not supported")
        return self._result(request_id, {"tools": TOOLS})

    def _tools_call(self, request_id: Any, params: dict[str, Any]) -> dict[str, Any]:
        if set(params) - {"name", "arguments", "_meta"}:
            return self._error(request_id, -32602, "Invalid params")
        name = params.get("name")
        arguments = params.get("arguments", {})
        if not isinstance(name, str) or not isinstance(arguments, dict):
            return self._error(request_id, -32602, "Invalid params")

        try:
            if name == "nx_ensure_ready":
                if arguments:
                    return self._error(request_id, -32602, "nx_ensure_ready accepts no arguments")
                result = NxReadiness(self._config, self._bridge).ensure_ready()
                if isinstance(result.get("bridge_status"), dict):
                    result = {**result, "bridge_status": self._with_local_write_halt(result["bridge_status"])}
            elif name == "nx_get_capabilities":
                if arguments:
                    return self._error(request_id, -32602, "nx_get_capabilities accepts no arguments")
                result = self._capabilities()
            elif name == "nx_bridge_status":
                if arguments:
                    return self._error(request_id, -32602, "nx_bridge_status accepts no arguments")
                result = self._with_local_write_halt(self._bridge.call("bridge.status", {}))
            elif name == "nx_bridge_ping":
                if set(arguments) != {"nonce"}:
                    return self._error(request_id, -32602, "nx_bridge_ping requires only nonce")
                nonce = arguments["nonce"]
                if not isinstance(nonce, str) or not (1 <= len(nonce) <= 128):
                    return self._error(request_id, -32602, "nonce must be a string of length 1 through 128")
                try:
                    nonce.encode("utf-8", errors="strict")
                except UnicodeEncodeError:
                    return self._error(request_id, -32602, "nonce must contain valid Unicode text")
                result = self._bridge.call("bridge.dispatch_probe", {"nonce": nonce})
            elif name == "nx_takeover":
                if set(arguments) != {"expected_pid", "expected_part_path", "artifact_scope", "authorize"}:
                    return self._error(request_id, -32602, "nx_takeover requires expected_pid, expected_part_path, artifact_scope, and authorize")
                expected_pid = arguments["expected_pid"]
                expected_part_path = arguments["expected_part_path"]
                artifact_scope = arguments["artifact_scope"]
                authorize = arguments["authorize"]
                if isinstance(expected_pid, bool) or not isinstance(expected_pid, int) or expected_pid <= 0:
                    return self._error(request_id, -32602, "expected_pid must be a positive integer")
                if not isinstance(expected_part_path, str) or len(expected_part_path) > 4096:
                    return self._error(request_id, -32602, "expected_part_path must be a string of at most 4096 characters")
                if artifact_scope not in ("saved_independent_copy", "current_work_part"):
                    return self._error(request_id, -32602, "artifact_scope must be saved_independent_copy or current_work_part")
                if not expected_part_path.strip() and artifact_scope != "current_work_part":
                    return self._error(request_id, -32602, "expected_part_path may be empty only for current_work_part")
                if authorize is not True:
                    return self._error(request_id, -32602, "authorize must be true after explicit human confirmation")
                result = self._bridge.call("ownership.takeover", {
                    "expected_pid": expected_pid,
                    "expected_part_path": expected_part_path,
                    "artifact_scope": artifact_scope,
                    "authorize": authorize,
                })
            elif name == "nx_return":
                if set(arguments) != {"ownership_id", "binding_epoch"}:
                    return self._error(request_id, -32602, "nx_return requires ownership_id and binding_epoch")
                ownership_id = arguments["ownership_id"]
                binding_epoch = arguments["binding_epoch"]
                if not isinstance(ownership_id, str) or not ownership_id.strip():
                    return self._error(request_id, -32602, "ownership_id must be a non-empty string")
                if isinstance(binding_epoch, bool) or not isinstance(binding_epoch, int) or binding_epoch < 1:
                    return self._error(request_id, -32602, "binding_epoch must be a positive integer")
                result = self._bridge.call("ownership.return", {
                    "ownership_id": ownership_id,
                    "binding_epoch": binding_epoch,
                })
            elif name == "nx_get_session":
                if set(arguments) != {"ownership_id", "binding_epoch"}:
                    return self._error(request_id, -32602, "nx_get_session requires ownership_id and binding_epoch")
                ownership_id = arguments["ownership_id"]
                binding_epoch = arguments["binding_epoch"]
                if not isinstance(ownership_id, str) or not ownership_id.strip():
                    return self._error(request_id, -32602, "ownership_id must be a non-empty string")
                if isinstance(binding_epoch, bool) or not isinstance(binding_epoch, int) or binding_epoch < 1:
                    return self._error(request_id, -32602, "binding_epoch must be a positive integer")
                result = self._bridge.call("model.get_session", {
                    "ownership_id": ownership_id,
                    "binding_epoch": binding_epoch,
                })
            elif name == "nx_inspect_part":
                if set(arguments) != {"ownership_id", "binding_epoch"}:
                    return self._error(request_id, -32602, "nx_inspect_part requires ownership_id and binding_epoch")
                ownership_id = arguments["ownership_id"]
                binding_epoch = arguments["binding_epoch"]
                if not isinstance(ownership_id, str) or not ownership_id.strip():
                    return self._error(request_id, -32602, "ownership_id must be a non-empty string")
                if isinstance(binding_epoch, bool) or not isinstance(binding_epoch, int) or binding_epoch < 1:
                    return self._error(request_id, -32602, "binding_epoch must be a positive integer")
                result = self._bridge.call("model.inspect_part", {
                    "ownership_id": ownership_id,
                    "binding_epoch": binding_epoch,
                })
            elif name == "nx_query_faces":
                expected = {"ownership_id", "binding_epoch", "revision_id", "selector"}
                if set(arguments) not in (expected, expected | {"body_id"}):
                    return self._error(request_id, -32602, "nx_query_faces requires ownership_id, binding_epoch, revision_id, selector, and optional body_id")
                request = {
                    "ownership_id": self._validated_nonempty_string(arguments["ownership_id"], "ownership_id"),
                    "binding_epoch": self._validated_positive_int(arguments["binding_epoch"], "binding_epoch"),
                    "revision_id": self._validated_nonempty_string(arguments["revision_id"], "revision_id"),
                    "selector": self._validated_nonempty_string(arguments["selector"], "selector"),
                }
                if "body_id" in arguments:
                    request["body_id"] = self._validated_nonempty_string(arguments["body_id"], "body_id")
                result = self._bridge.call("model.query_faces", request)
            elif name == "nx_inspect_sketch":
                if set(arguments) != {"ownership_id", "binding_epoch", "sketch_id"}:
                    return self._error(request_id, -32602, "nx_inspect_sketch requires ownership_id, binding_epoch, and sketch_id")
                ownership_id = arguments["ownership_id"]
                binding_epoch = arguments["binding_epoch"]
                sketch_id = arguments["sketch_id"]
                if not isinstance(ownership_id, str) or not ownership_id.strip():
                    return self._error(request_id, -32602, "ownership_id must be a non-empty string")
                if isinstance(binding_epoch, bool) or not isinstance(binding_epoch, int) or binding_epoch < 1:
                    return self._error(request_id, -32602, "binding_epoch must be a positive integer")
                if not isinstance(sketch_id, str) or not sketch_id.strip():
                    return self._error(request_id, -32602, "sketch_id must be a non-empty string")
                result = self._bridge.call("model.inspect_sketch", {
                    "ownership_id": ownership_id,
                    "binding_epoch": binding_epoch,
                    "sketch_id": sketch_id,
                })
            elif name == "nx_get_operation":
                if set(arguments) != {"operation_id"}:
                    return self._error(request_id, -32602, "nx_get_operation requires only operation_id")
                operation_id = self._validated_operation_id(arguments["operation_id"])
                result = self._bridge.call("operation.get", {"operation_id": operation_id})
                if (
                    operation_id in self._unknown_operation_ids
                    and isinstance(result, dict)
                    and result.get("state") in {"SUCCEEDED", "FAILED", "CANCELLED"}
                    and result.get("writes_halted") is False
                ):
                    self._unknown_operation_ids.discard(operation_id)
            elif name == "nx_cancel_operation":
                if set(arguments) != {"operation_id"}:
                    return self._error(request_id, -32602, "nx_cancel_operation requires only operation_id")
                operation_id = self._validated_operation_id(arguments["operation_id"])
                result = self._bridge.call("operation.cancel", {"operation_id": operation_id})
            elif name == "nx_prepare_sketch":
                common = {
                    "operation_id", "ownership_id", "binding_epoch", "revision_id", "geometries",
                }
                keys = set(arguments)
                legacy_mode = keys == common | {"plane", "origin_mm"}
                required_face_keys = common | {"support_face_selector"}
                allowed_face_keys = required_face_keys | {"face_origin_offset_mm", "support_body_id"}
                face_mode = (
                    required_face_keys <= keys <= allowed_face_keys
                )
                if not (legacy_mode or face_mode):
                    return self._error(request_id, -32602, "nx_prepare_sketch requires common fields plus plane and origin_mm, or support_face_selector with optional face_origin_offset_mm and support_body_id")
                request = self._validate_prepare_sketch(arguments)
                result = self._bridge.call("operation.prepare_sketch", request)
            elif name == "nx_prepare_extrude":
                request = self._validate_prepare_extrude(arguments)
                result = self._bridge.call("operation.prepare_extrude", request)
            elif name == "nx_prepare_thread":
                request = self._validate_prepare_thread(arguments)
                result = self._bridge.call("operation.prepare_thread", request)
            elif name == "nx_prepare_helical_groove":
                request = self._validate_prepare_helical_groove(arguments)
                result = self._bridge.call("operation.prepare_helical_groove", request)
            elif name == "nx_prepare_operation":
                expected = {
                    "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id",
                    "constraint_id", "value", "unit",
                }
                if set(arguments) != expected:
                    return self._error(request_id, -32602, "nx_prepare_operation requires exactly operation_id, ownership_id, binding_epoch, revision_id, sketch_id, constraint_id, value, and unit")
                request = self._validate_operation_prepare(arguments)
                result = self._bridge.call("operation.prepare", request)
            elif name == "nx_execute_plan":
                expected = {"operation_id", "ownership_id", "binding_epoch", "plan_id"}
                if set(arguments) != expected:
                    return self._error(request_id, -32602, "nx_execute_plan requires exactly operation_id, ownership_id, binding_epoch, and plan_id")
                request = self._validate_operation_execute(arguments)
                if self._unknown_operation_ids:
                    return self._result(
                        request_id,
                        self._tool_error(
                            "CLIENT_OUTCOME_UNKNOWN",
                            "A previous execute_plan outcome is unknown; query its operation_id before submitting more writes.",
                        ),
                    )
                try:
                    result = self._bridge.call("operation.execute_plan", request)
                except BridgeFailure as exc:
                    if exc.outcome_unknown:
                        self._unknown_operation_ids.add(request["operation_id"])
                    raise
            elif name == "nx_verify_part":
                expected = {"operation_id", "ownership_id", "binding_epoch", "execute_operation_id"}
                if set(arguments) != expected:
                    return self._error(request_id, -32602, "nx_verify_part requires exactly operation_id, ownership_id, binding_epoch, and execute_operation_id")
                request = self._validate_operation_verify(arguments)
                result = self._bridge.call("operation.verify_part", request)
            elif name == "nx_save_copy":
                expected = {"operation_id", "ownership_id", "binding_epoch", "target_path", "reopen"}
                if set(arguments) != expected:
                    return self._error(request_id, -32602, "nx_save_copy requires exactly operation_id, ownership_id, binding_epoch, target_path, and reopen")
                request = self._validate_save_copy(arguments)
                if self._unknown_operation_ids:
                    return self._result(
                        request_id,
                        self._tool_error(
                            "CLIENT_OUTCOME_UNKNOWN",
                            "A previous write or save outcome is unknown; query its operation_id before submitting another write.",
                        ),
                    )
                try:
                    result = self._bridge.call("operation.save_copy", request)
                except BridgeFailure as exc:
                    if exc.outcome_unknown:
                        self._unknown_operation_ids.add(request["operation_id"])
                    raise
                if result.get("state") == "UNKNOWN":
                    self._unknown_operation_ids.add(request["operation_id"])
            else:
                return self._error(request_id, -32602, f"Unknown tool: {name}")
        except _InvalidToolArguments as exc:
            return self._error(request_id, -32602, str(exc))
        except BridgeFailure as exc:
            if name == "nx_save_copy" and exc.outcome_unknown:
                operation_id = arguments.get("operation_id") if isinstance(arguments, dict) else None
                if isinstance(operation_id, str) and 1 <= len(operation_id) <= 128:
                    self._unknown_operation_ids.add(operation_id)
            return self._result(request_id, self._tool_error(exc.code, exc.message))
        except Exception as exc:  # Keep protocol output safe; diagnostic details go only to stderr.
            print(f"bridge call failed ({type(exc).__name__})", file=sys.stderr, flush=True)
            return self._result(
                request_id,
                self._tool_error("BRIDGE_INTERNAL_ERROR", "The local bridge request failed."),
            )
        return self._result(request_id, self._tool_success(result))

    def _capabilities(self) -> dict[str, Any]:
        state = "UNKNOWN"
        read_enabled = False
        status: dict[str, Any] = {}
        unavailable_reason: dict[str, str] | None = None
        if not self._config.configured:
            unavailable_reason = {
                "code": "BRIDGE_UNCONFIGURED",
                "message": "No NX bridge session is configured.",
            }
        else:
            try:
                cached_status = self._with_local_write_halt(self._bridge.call("bridge.status", {}))
                status = cached_status
                state_value = cached_status.get("ownership_state")
                if isinstance(state_value, str):
                    state = state_value
                read_enabled = cached_status.get("model_read_available") is True
                if not read_enabled:
                    code = "OWNERSHIP_REQUIRED" if state == "HUMAN_OWNED" else "MODEL_READ_UNAVAILABLE"
                    message = (
                        "Model reads require an explicit takeover of the saved independent copy."
                        if state == "HUMAN_OWNED"
                        else f"Model reads are unavailable while ownership state is {state}."
                    )
                    unavailable_reason = {"code": code, "message": message}
            except BridgeFailure as exc:
                unavailable_reason = {"code": exc.code, "message": exc.message[:1000]}
        status = self._with_local_write_halt(status)
        binding_identity = status.get("binding_identity")
        if not isinstance(binding_identity, dict):
            binding_identity = {}
        artifact_scope = binding_identity.get("artifact_scope")
        write_enabled = (
            status.get("model_write_enabled") is True
            and state == "AI_OWNED"
            and status.get("writes_halted") is False
        )
        helical_adapter_available = status.get("model_helical_adapter_available") is True
        helical_write_enabled = (
            status.get("model_helical_write_enabled") is True
            and helical_adapter_available
            and state == "AI_OWNED"
            and status.get("writes_halted") is False
            and not self._unknown_operation_ids
        )
        capabilities = {
            "protocol_version": PROTOCOL_VERSION,
            "bridge_configured": self._config.configured,
            "connection_preparation": {
                "tool": "nx_ensure_ready",
                "automatic_launch_configured": self._config.launch is not None,
                "startup_probe_budget_seconds": self._config.launch.ready_timeout_seconds if self._config.launch else 2.0,
                "bounded_helper_cleanup_additional": True,
                "changes_ownership": False,
                "diagnostics_launch_nx": False,
            },
            "available_operations": [
                "bridge.status", "bridge.dispatch_probe", "ownership.takeover", "ownership.return",
                "model.get_session", "model.inspect_part", "model.inspect_sketch", "model.query_faces",
                "operation.get", "operation.cancel", "operation.prepare", "operation.prepare_sketch",
                "operation.prepare_extrude", "operation.prepare_thread", "operation.prepare_helical_groove",
                "operation.execute_plan",
                "operation.verify_part", "operation.save_copy",
            ],
            "ownership_state": state,
            "model_access": {"read": read_enabled, "write": write_enabled, "control": False},
            "model_read_implemented": True,
            "model_read_enabled": read_enabled,
            "model_read_available": read_enabled,
            "model_helical_adapter_available": helical_adapter_available,
            "model_helical_write_enabled": helical_write_enabled,
            "model_read_requires": ["AI_OWNED", "ownership_id", "binding_epoch"],
            "artifact_scope": artifact_scope,
            "default_artifact_scope": "saved_independent_copy",
            "supported_artifact_scopes": ["saved_independent_copy", "current_work_part"],
            "revision_schema": "m2-semantic-v1",
            "revision_source": "monotonic revision_id with canonical semantic snapshot comparison; not a digest",
            "read_support": {
                "session": ["NX_FULL_VERSION", "Session epoch", "load instance", "active Work Part identity"],
                "part": ["units", "part inventory", "body/face/edge identities", "feature graph", "sketches", "expressions", "dependencies"],
                "sketch_geometry_detail": ["LINE", "ARC"],
                "arc_geometry_fields": ["center", "radius_mm", "start_angle_rad", "end_angle_rad", "normal"],
                "feature_parameters": "metadata and created expressions; type-specific parameters are reported unsupported unless implemented",
            },
            "model_native_geometry_support": {
                "prepare_operations": [
                    "operation.prepare_sketch", "operation.prepare_extrude",
                    "operation.prepare_thread", "operation.prepare_helical_groove",
                ],
                "sketch": {
                    "planes": ["XY", "XZ", "YZ"],
                    "face_mode": {
                        "argument": "support_face_selector",
                        "optional_arguments": ["support_body_id", "face_origin_offset_mm"],
                        "selection": "unique planar Face selected by top/bottom, signed X/Y/Z direction, outermost, or an explicit axis coordinate in millimeters",
                        "local_frame": "2D millimeter coordinates on the selected face; optional [u,v] origin offset defaults to [0,0]",
                        "profile_domain": "line and circular boundaries are read with 0.001 mm chord tolerance and 0.002 mm clearance; supported profiles are circles and complete axis-aligned rectangles",
                    },
                    "local_coordinates": "2D millimeter coordinates on the selected plane",
                    "origin": "world-coordinate XYZ millimeters",
                    "geometries": [
                        {
                            "type": "line",
                            "allowed_orientation": "any nonzero 2D segment",
                            "measured_length": "Euclidean distance between endpoints",
                            "driving_dimensions": [
                                "HorizontalDim for the endpoint horizontal projection",
                                "VerticalDim for the endpoint vertical projection",
                            ],
                            "parameter_edit_limit": "M2 edits one projected component per operation; it does not set Euclidean line length directly",
                        },
                        {"type": "rectangle", "constraints": ["driving width", "driving height"]},
                        {"type": "circle", "constraint": "driving diameter"},
                    ],
                    "driving_constraint_units": "mm",
                },
                "extrude": {
                    "supported_profiles": ["one full circle", "one closed axis-aligned rectangle made of four straight lines in sketch coordinates"],
                    "profiles_per_sketch": 1,
                    "unsupported_profile_error": "UNSUPPORTED_PROFILE",
                    "modes": ["create", "unite", "subtract"],
                    "directions": ["positive", "negative"],
                    "extents": ["finite", "through_all"],
                    "through_all_modes": ["unite", "subtract"],
                    "boolean_target": "explicit target_body_id required for unite and subtract",
                },
                "thread": {
                    "operation": "operation.prepare_thread",
                    "availability_requires": ["model_helical_adapter_available", "model_helical_write_enabled"],
                    "supported_profile": "external solid, single-start, 60-degree thread",
                    "axis_choices": ["X", "Y", "Z"],
                    "start_mm_definition": "signed axial offset from axis_origin_mm",
                    "explicit_inputs": ["major_diameter_mm", "minor_diameter_mm", "pitch_mm"],
                    "minor_diameter_requirement": "positive and less than major_diameter_mm",
                    "maximum_length_to_pitch_ratio": 500,
                    "linear_dimensions_absolute_limit_mm": 1000000,
                    "axis_direction": "positive or negative selected axis",
                    "handedness": ["right", "left"],
                },
                "helical_groove": {
                    "operation": "operation.prepare_helical_groove",
                    "availability_requires": ["model_helical_adapter_available", "model_helical_write_enabled"],
                    "axis_choices": ["X", "Y", "Z"],
                    "start_mm_definition": "signed axial offset from axis_origin_mm",
                    "explicit_inputs": [
                        "root_diameter_mm", "reference_diameter_mm", "outer_diameter_mm", "space_width_mm",
                    ],
                    "diameter_order": "0 < root_diameter_mm < reference_diameter_mm < outer_diameter_mm",
                    "space_width_definition": "axial groove width at reference_diameter_mm",
                    "half_width_profile": "w(r)=space_width_mm/2+(r-reference_diameter_mm/2)*tan(flank_angle_deg)",
                    "axis_direction": "positive selected axis; handedness determines angular travel sign",
                    "maximum_length_to_lead_ratio": 50,
                    "linear_dimensions_absolute_limit_mm": 1000000,
                    "samples_per_turn": 96,
                    "interpolation": "interpolated spline",
                    "distance_tolerance_mm": 0.001,
                    "profile_constraints": [
                        "w(root_diameter_mm/2) > 0",
                        "2*w(outer_diameter_mm/2) < lead_mm",
                    ],
                    "phase_range_deg": [-360, 360],
                },
                "limitations": [
                    "a line segment must have distinct endpoints",
                    "open lines cannot be extruded as solid profiles",
                    "extrude requires exactly one full circle or one closed axis-aligned rectangle made of four straight lines in sketch coordinates; open profiles, arbitrary polygons, and multiple profiles return UNSUPPORTED_PROFILE",
                    "face mode requires a unique planar support Face and a provable supported profile domain",
                    "with M4 support markers present, Driving edits to any source Sketch of an existing Subtract targeting the same M4 support body return M4_SUBTRACT_CONTOUR_EDIT_UNSUPPORTED",
                ],
            },
            "model_write_enabled": write_enabled,
            "model_save_copy_enabled": status.get("model_save_copy_available") is True and state == "AI_OWNED" and
                status.get("writes_halted") is False and artifact_scope == "saved_independent_copy" and
                binding_identity.get("is_read_only") is not True,
            "writes_halted": status.get("writes_halted"),
            "write_halt_reason": status.get("write_halt_reason"),
            "active_operations": status.get("active_operations", 0),
            "model_write_support": {
                "operation": "set_driving_dimension_value",
                "availability_requires": [
                    "ownership_state=AI_OWNED", "parameter-edit adapter loaded", "writes_halted=false",
                ],
                "work_part_units": ["Millimeters"],
                "expression_unit_symbol": "mm",
                "requested_value": "finite positive decimal string",
                "supported_geometry_signatures": [
                    {"dimension_type": "DiameterDim", "geometry": "one full Arc; measured value is diameter"},
                    {"dimension_type": "HorizontalDim", "geometry": "ArcCenter plus Point; absolute X offset"},
                    {"dimension_type": "HorizontalDim", "geometry": "one Line with StartPoint and EndPoint; horizontal endpoint projection"},
                    {"dimension_type": "VerticalDim", "geometry": "one Line with StartPoint and EndPoint; vertical endpoint projection"},
                ],
                "line_length": "The measured line length is the Euclidean distance between endpoints; M2 parameter edits can change one horizontal or vertical endpoint projection per operation, not Euclidean length directly.",
                "validation": "Prepare resolves the live Driving constraint, unique expression relation, unit, and geometry; execute revalidates the prepared target.",
            },
            "model_verify_support": {
                "operation": "verify_successful_parameter_edit",
                "checks": ["expression_rhs_and_value", "measured_geometry", "feature_history", "dimension_associations"],
                "operation_id_requires": "a SUCCEEDED execute_parameter_edit operation in this bridge load",
            },
            "model_save_copy_support": {
                "operation": "nxopen_saveas_new_prt",
                "target_requires": "absolute .prt path that does not exist and differs from the takeover source path",
                "reopen_required": True,
                "current_work_part": "REJECTED_BEFORE_SAVEAS_OR_REOPEN_TO_PRESERVE_IN_MEMORY_PART_IDENTITY",
                "checks": ["expressions", "sketch_geometry", "feature_history", "dimension_associations"],
                "success_rotates": ["ownership_id", "binding_epoch"],
            },
            "model_control_enabled": False,
            "unavailable_reason": unavailable_reason,
        }
        if "unknown_operation_ids" in status:
            capabilities["unknown_operation_ids"] = status["unknown_operation_ids"]
        return capabilities

    def _with_local_write_halt(self, status: dict[str, Any]) -> dict[str, Any]:
        if not self._unknown_operation_ids:
            return status
        result = dict(status)
        result["model_write_enabled"] = False
        result["writes_halted"] = True
        result["write_halt_reason"] = "CLIENT_OUTCOME_UNKNOWN"
        result["unknown_operation_ids"] = sorted(self._unknown_operation_ids)
        return result

    @staticmethod
    def _validated_operation_id(value: Any) -> str:
        if not isinstance(value, str) or not 1 <= len(value) <= 128:
            raise _InvalidToolArguments("operation_id must be a string of length 1 through 128")
        try:
            value.encode("utf-8", errors="strict")
        except UnicodeEncodeError as exc:
            raise _InvalidToolArguments("operation_id must contain valid Unicode text") from exc
        return value

    @classmethod
    def _validate_operation_prepare(cls, arguments: dict[str, Any]) -> dict[str, Any]:
        request = {
            "operation_id": cls._validated_operation_id(arguments["operation_id"]),
            "ownership_id": cls._validated_nonempty_string(arguments["ownership_id"], "ownership_id"),
            "binding_epoch": cls._validated_positive_int(arguments["binding_epoch"], "binding_epoch"),
            "revision_id": cls._validated_nonempty_string(arguments["revision_id"], "revision_id"),
            "sketch_id": cls._validated_nonempty_string(arguments["sketch_id"], "sketch_id"),
            "constraint_id": cls._validated_nonempty_string(arguments["constraint_id"], "constraint_id"),
            "value": cls._validated_decimal_string(arguments["value"]),
            "unit": cls._validated_unit_symbol(arguments["unit"]),
        }
        return request

    @classmethod
    def _validate_prepare_sketch(cls, arguments: dict[str, Any]) -> dict[str, Any]:
        common = {
            "operation_id": cls._validated_operation_id(arguments["operation_id"]),
            "ownership_id": cls._validated_nonempty_string(arguments["ownership_id"], "ownership_id"),
            "binding_epoch": cls._validated_positive_int(arguments["binding_epoch"], "binding_epoch"),
            "revision_id": cls._validated_nonempty_string(arguments["revision_id"], "revision_id"),
        }
        legacy_mode = "plane" in arguments or "origin_mm" in arguments
        if legacy_mode:
            if set(arguments) != {
                "operation_id", "ownership_id", "binding_epoch", "revision_id", "plane", "origin_mm", "geometries",
            }:
                raise _InvalidToolArguments("legacy sketch mode requires exactly plane and origin_mm")
            plane = arguments["plane"]
            if not isinstance(plane, str) or plane not in {"XY", "XZ", "YZ"}:
                raise _InvalidToolArguments("plane must be XY, XZ, or YZ")
            origin = cls._validated_number_array(arguments["origin_mm"], 3, "origin_mm")
        else:
            argument_keys = frozenset(arguments)
            required_face_keys = frozenset({
                "operation_id", "ownership_id", "binding_epoch", "revision_id", "geometries", "support_face_selector",
            })
            allowed_face_keys = required_face_keys | {"face_origin_offset_mm", "support_body_id"}
            if not required_face_keys <= argument_keys or not argument_keys <= allowed_face_keys:
                raise _InvalidToolArguments(
                    "face sketch mode requires support_face_selector and accepts only optional "
                    "face_origin_offset_mm and support_body_id"
                )
            selector = cls._validated_nonempty_string(arguments["support_face_selector"], "support_face_selector")
            face_origin_offset = cls._validated_number_array(
                arguments.get("face_origin_offset_mm", [0, 0]), 2, "face_origin_offset_mm",
            )
            support_body_id = None
            if "support_body_id" in arguments:
                support_body_id = cls._validated_nonempty_string(arguments["support_body_id"], "support_body_id")
        geometries = arguments["geometries"]
        if not isinstance(geometries, list) or not 1 <= len(geometries) <= 32:
            raise _InvalidToolArguments("geometries must contain between 1 and 32 geometry records")

        normalized_geometries: list[dict[str, Any]] = []
        seen_ids: set[str] = set()
        for index, geometry in enumerate(geometries):
            if not isinstance(geometry, dict):
                raise _InvalidToolArguments(f"geometries[{index}] must be an object")
            geometry_type = geometry.get("type")
            geometry_id = cls._validated_geometry_id(geometry.get("id"))
            if geometry_id in seen_ids:
                raise _InvalidToolArguments("geometry ids must be unique within the sketch request")
            seen_ids.add(geometry_id)

            if geometry_type == "line":
                if set(geometry) != {"id", "type", "start_mm", "end_mm"}:
                    raise _InvalidToolArguments("line geometry requires exactly id, type, start_mm, and end_mm")
                start = cls._validated_number_array(geometry["start_mm"], 2, "start_mm")
                end = cls._validated_number_array(geometry["end_mm"], 2, "end_mm")
                if start == end:
                    raise _InvalidToolArguments("line start_mm and end_mm must differ")
                normalized_geometries.append({"id": geometry_id, "type": "line", "start_mm": start, "end_mm": end})
            elif geometry_type == "rectangle":
                if set(geometry) != {"id", "type", "corner_mm", "width_mm", "height_mm"}:
                    raise _InvalidToolArguments("rectangle geometry requires exactly id, type, corner_mm, width_mm, and height_mm")
                normalized_geometries.append({
                    "id": geometry_id,
                    "type": "rectangle",
                    "corner_mm": cls._validated_number_array(geometry["corner_mm"], 2, "corner_mm"),
                    "width_mm": cls._validated_positive_number(geometry["width_mm"], "width_mm"),
                    "height_mm": cls._validated_positive_number(geometry["height_mm"], "height_mm"),
                })
            elif geometry_type == "circle":
                if set(geometry) != {"id", "type", "center_mm", "diameter_mm"}:
                    raise _InvalidToolArguments("circle geometry requires exactly id, type, center_mm, and diameter_mm")
                normalized_geometries.append({
                    "id": geometry_id,
                    "type": "circle",
                    "center_mm": cls._validated_number_array(geometry["center_mm"], 2, "center_mm"),
                    "diameter_mm": cls._validated_positive_number(geometry["diameter_mm"], "diameter_mm"),
                })
            else:
                raise _InvalidToolArguments("geometry type must be line, rectangle, or circle")

        request = {**common, "geometries": normalized_geometries}
        if legacy_mode:
            request.update({"plane": plane, "origin_mm": origin})
        else:
            request.update({
                "support_face_selector": selector,
                "face_origin_offset_mm": face_origin_offset,
            })
            if support_body_id is not None:
                request["support_body_id"] = support_body_id
        return request

    @classmethod
    def _validate_prepare_extrude(cls, arguments: dict[str, Any]) -> dict[str, Any]:
        mode = arguments.get("mode")
        direction = arguments.get("direction")
        extent = arguments.get("extent")
        if not isinstance(mode, str) or mode not in {"create", "unite", "subtract"}:
            raise _InvalidToolArguments("mode must be create, unite, or subtract")
        if not isinstance(direction, str) or direction not in {"positive", "negative"}:
            raise _InvalidToolArguments("direction must be positive or negative")
        if not isinstance(extent, str) or extent not in {"finite", "through_all"}:
            raise _InvalidToolArguments("extent must be finite or through_all")
        if mode == "create" and extent != "finite":
            raise _InvalidToolArguments("create extrusion supports only finite extent")

        expected = {
            "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id",
            "mode", "direction", "extent",
        }
        if extent == "finite":
            expected.add("depth_mm")
        if mode != "create":
            expected.add("target_body_id")
        if set(arguments) != expected:
            raise _InvalidToolArguments(
                "nx_prepare_extrude requires depth_mm exactly for finite extent and "
                "target_body_id exactly for unite or subtract"
            )

        request: dict[str, Any] = {
            "operation_id": cls._validated_operation_id(arguments["operation_id"]),
            "ownership_id": cls._validated_nonempty_string(arguments["ownership_id"], "ownership_id"),
            "binding_epoch": cls._validated_positive_int(arguments["binding_epoch"], "binding_epoch"),
            "revision_id": cls._validated_nonempty_string(arguments["revision_id"], "revision_id"),
            "sketch_id": cls._validated_nonempty_string(arguments["sketch_id"], "sketch_id"),
            "mode": mode,
            "direction": direction,
            "extent": extent,
        }
        if extent == "finite":
            request["depth_mm"] = cls._validated_positive_number(arguments["depth_mm"], "depth_mm")
        if mode != "create":
            request["target_body_id"] = cls._validated_nonempty_string(arguments["target_body_id"], "target_body_id")
        return request

    @classmethod
    def _validate_prepare_thread(cls, arguments: dict[str, Any]) -> dict[str, Any]:
        expected = {
            "operation_id", "ownership_id", "binding_epoch", "revision_id", "target_body_id",
            "axis", "axis_origin_mm", "start_mm", "length_mm", "handedness", "direction",
            "major_diameter_mm", "minor_diameter_mm", "pitch_mm",
        }
        if set(arguments) != expected:
            raise _InvalidToolArguments("nx_prepare_thread requires exactly its documented fields")

        axis = arguments["axis"]
        handedness = arguments["handedness"]
        direction = arguments["direction"]
        if not isinstance(axis, str) or axis not in {"X", "Y", "Z"}:
            raise _InvalidToolArguments("axis must be X, Y, or Z")
        if not isinstance(handedness, str) or handedness not in {"right", "left"}:
            raise _InvalidToolArguments("handedness must be right or left")
        if not isinstance(direction, str) or direction not in {"positive", "negative"}:
            raise _InvalidToolArguments("direction must be positive or negative")

        major_diameter = cls._validated_bounded_positive_mm(arguments["major_diameter_mm"], "major_diameter_mm")
        minor_diameter = cls._validated_bounded_positive_mm(arguments["minor_diameter_mm"], "minor_diameter_mm")
        pitch = cls._validated_bounded_positive_mm(arguments["pitch_mm"], "pitch_mm")
        if minor_diameter >= major_diameter:
            raise _InvalidToolArguments("minor_diameter_mm must be less than major_diameter_mm")
        length = cls._validated_bounded_positive_mm(arguments["length_mm"], "length_mm")
        if length > 500 * pitch:
            raise _InvalidToolArguments("length_mm / pitch_mm must not exceed 500")

        return {
            "operation_id": cls._validated_operation_id(arguments["operation_id"]),
            "ownership_id": cls._validated_bounded_nonempty_string(arguments["ownership_id"], "ownership_id", 128),
            "binding_epoch": cls._validated_positive_int(arguments["binding_epoch"], "binding_epoch"),
            "revision_id": cls._validated_bounded_nonempty_string(arguments["revision_id"], "revision_id", 128),
            "target_body_id": cls._validated_bounded_nonempty_string(arguments["target_body_id"], "target_body_id", 4096),
            "axis": axis,
            "axis_origin_mm": cls._validated_bounded_number_array(arguments["axis_origin_mm"], 3, "axis_origin_mm"),
            "start_mm": cls._validated_bounded_finite_mm(arguments["start_mm"], "start_mm"),
            "length_mm": length,
            "handedness": handedness,
            "direction": direction,
            "major_diameter_mm": major_diameter,
            "minor_diameter_mm": minor_diameter,
            "pitch_mm": pitch,
        }

    @classmethod
    def _validate_prepare_helical_groove(cls, arguments: dict[str, Any]) -> dict[str, Any]:
        expected = {
            "operation_id", "ownership_id", "binding_epoch", "revision_id", "target_body_id",
            "axis", "axis_origin_mm", "start_mm", "length_mm", "handedness", "lead_mm",
            "root_diameter_mm", "reference_diameter_mm", "outer_diameter_mm", "space_width_mm",
            "flank_angle_deg", "phase_deg",
        }
        if set(arguments) != expected:
            raise _InvalidToolArguments("nx_prepare_helical_groove requires exactly its documented fields")

        axis = arguments["axis"]
        handedness = arguments["handedness"]
        if not isinstance(axis, str) or axis not in {"X", "Y", "Z"}:
            raise _InvalidToolArguments("axis must be X, Y, or Z")
        if not isinstance(handedness, str) or handedness not in {"right", "left"}:
            raise _InvalidToolArguments("handedness must be right or left")

        lead = cls._validated_bounded_positive_mm(arguments["lead_mm"], "lead_mm")
        length = cls._validated_bounded_positive_mm(arguments["length_mm"], "length_mm")
        if length > 50 * lead:
            raise _InvalidToolArguments("length_mm / lead_mm must not exceed 50")
        root_diameter = cls._validated_bounded_positive_mm(arguments["root_diameter_mm"], "root_diameter_mm")
        reference_diameter = cls._validated_bounded_positive_mm(
            arguments["reference_diameter_mm"], "reference_diameter_mm",
        )
        outer_diameter = cls._validated_bounded_positive_mm(arguments["outer_diameter_mm"], "outer_diameter_mm")
        if not root_diameter < reference_diameter < outer_diameter:
            raise _InvalidToolArguments(
                "diameters must satisfy root_diameter_mm < reference_diameter_mm < outer_diameter_mm"
            )
        space_width = cls._validated_bounded_positive_mm(arguments["space_width_mm"], "space_width_mm")
        flank_angle = cls._validated_positive_number(arguments["flank_angle_deg"], "flank_angle_deg")
        if flank_angle >= 45:
            raise _InvalidToolArguments("flank_angle_deg must be less than 45 degrees")
        phase = cls._validated_finite_number(arguments["phase_deg"], "phase_deg")
        if not -360 <= phase <= 360:
            raise _InvalidToolArguments("phase_deg must be between -360 and 360 degrees")

        half_width_at_reference = space_width / 2
        flank_slope = math.tan(math.radians(flank_angle))
        root_half_width = half_width_at_reference + (root_diameter - reference_diameter) / 2 * flank_slope
        outer_half_width = half_width_at_reference + (outer_diameter - reference_diameter) / 2 * flank_slope
        if not math.isfinite(root_half_width) or root_half_width <= 0:
            raise _InvalidToolArguments("groove half-width at the root diameter must be positive and finite")
        if not math.isfinite(outer_half_width) or 2 * outer_half_width >= lead:
            raise _InvalidToolArguments("groove width at outer_diameter_mm must be less than lead_mm")

        return {
            "operation_id": cls._validated_operation_id(arguments["operation_id"]),
            "ownership_id": cls._validated_bounded_nonempty_string(arguments["ownership_id"], "ownership_id", 128),
            "binding_epoch": cls._validated_positive_int(arguments["binding_epoch"], "binding_epoch"),
            "revision_id": cls._validated_bounded_nonempty_string(arguments["revision_id"], "revision_id", 128),
            "target_body_id": cls._validated_bounded_nonempty_string(arguments["target_body_id"], "target_body_id", 4096),
            "axis": axis,
            "axis_origin_mm": cls._validated_bounded_number_array(arguments["axis_origin_mm"], 3, "axis_origin_mm"),
            "start_mm": cls._validated_bounded_finite_mm(arguments["start_mm"], "start_mm"),
            "length_mm": length,
            "handedness": handedness,
            "lead_mm": lead,
            "root_diameter_mm": root_diameter,
            "reference_diameter_mm": reference_diameter,
            "outer_diameter_mm": outer_diameter,
            "space_width_mm": space_width,
            "flank_angle_deg": flank_angle,
            "phase_deg": phase,
        }

    @classmethod
    def _validated_geometry_id(cls, value: Any) -> str:
        geometry_id = cls._validated_nonempty_string(value, "geometry id")
        if len(geometry_id) > 128:
            raise _InvalidToolArguments("geometry id must be at most 128 characters")
        return geometry_id

    @classmethod
    def _validated_number_array(cls, value: Any, length: int, field: str) -> list[int | float]:
        if not isinstance(value, list) or len(value) != length:
            raise _InvalidToolArguments(f"{field} must be an array of exactly {length} finite numbers")
        return [cls._validated_finite_number(item, field) for item in value]

    @classmethod
    def _validated_bounded_number_array(cls, value: Any, length: int, field: str) -> list[int | float]:
        if not isinstance(value, list) or len(value) != length:
            raise _InvalidToolArguments(f"{field} must be an array of exactly {length} bounded finite numbers")
        return [cls._validated_bounded_finite_mm(item, field) for item in value]

    @classmethod
    def _validated_bounded_finite_mm(cls, value: Any, field: str) -> int | float:
        number = cls._validated_finite_number(value, field)
        if abs(number) > 1_000_000:
            raise _InvalidToolArguments(f"{field} absolute value must not exceed 1000000 mm")
        return number

    @classmethod
    def _validated_bounded_positive_mm(cls, value: Any, field: str) -> int | float:
        number = cls._validated_positive_number(value, field)
        if number > 1_000_000:
            raise _InvalidToolArguments(f"{field} must not exceed 1000000 mm")
        return number

    @classmethod
    def _validated_positive_number(cls, value: Any, field: str) -> int | float:
        number = cls._validated_finite_number(value, field)
        if number <= 0:
            raise _InvalidToolArguments(f"{field} must be a positive finite number")
        return number

    @staticmethod
    def _validated_finite_number(value: Any, field: str) -> int | float:
        if isinstance(value, bool) or not isinstance(value, (int, float)):
            raise _InvalidToolArguments(f"{field} must be a finite JSON number")
        try:
            finite = math.isfinite(value)
        except (OverflowError, TypeError, ValueError):
            finite = False
        if not finite:
            raise _InvalidToolArguments(f"{field} must be a finite JSON number")
        return value

    @classmethod
    def _validate_operation_execute(cls, arguments: dict[str, Any]) -> dict[str, Any]:
        return {
            "operation_id": cls._validated_operation_id(arguments["operation_id"]),
            "ownership_id": cls._validated_nonempty_string(arguments["ownership_id"], "ownership_id"),
            "binding_epoch": cls._validated_positive_int(arguments["binding_epoch"], "binding_epoch"),
            "plan_id": cls._validated_nonempty_string(arguments["plan_id"], "plan_id"),
        }

    @classmethod
    def _validate_operation_verify(cls, arguments: dict[str, Any]) -> dict[str, Any]:
        return {
            "operation_id": cls._validated_operation_id(arguments["operation_id"]),
            "ownership_id": cls._validated_nonempty_string(arguments["ownership_id"], "ownership_id"),
            "binding_epoch": cls._validated_positive_int(arguments["binding_epoch"], "binding_epoch"),
            "execute_operation_id": cls._validated_operation_id(arguments["execute_operation_id"]),
        }

    @classmethod
    def _validate_save_copy(cls, arguments: dict[str, Any]) -> dict[str, Any]:
        reopen = arguments["reopen"]
        if not isinstance(reopen, bool) or not reopen:
            raise _InvalidToolArguments("reopen must be true for NX save-copy round-trip verification")
        return {
            "operation_id": cls._validated_operation_id(arguments["operation_id"]),
            "ownership_id": cls._validated_nonempty_string(arguments["ownership_id"], "ownership_id"),
            "binding_epoch": cls._validated_positive_int(arguments["binding_epoch"], "binding_epoch"),
            "target_path": cls._validated_nonempty_string(arguments["target_path"], "target_path"),
            "reopen": True,
        }

    @staticmethod
    def _validated_nonempty_string(value: Any, field: str) -> str:
        if not isinstance(value, str) or not value.strip():
            raise _InvalidToolArguments(f"{field} must be a non-empty string")
        try:
            value.encode("utf-8", errors="strict")
        except UnicodeEncodeError as exc:
            raise _InvalidToolArguments(f"{field} must contain valid Unicode text") from exc
        return value

    @classmethod
    def _validated_bounded_nonempty_string(cls, value: Any, field: str, maximum: int) -> str:
        string = cls._validated_nonempty_string(value, field)
        if len(string) > maximum:
            raise _InvalidToolArguments(f"{field} must be at most {maximum} characters")
        return string

    @staticmethod
    def _validated_positive_int(value: Any, field: str) -> int:
        if isinstance(value, bool) or not isinstance(value, int) or value < 1:
            raise _InvalidToolArguments(f"{field} must be a positive integer")
        return value

    @staticmethod
    def _validated_decimal_string(value: Any) -> str:
        if not isinstance(value, str) or not 1 <= len(value) <= 128 or not _FINITE_DECIMAL.fullmatch(value):
            raise _InvalidToolArguments("value must be a finite decimal string of at most 128 characters")
        try:
            numeric = float(value)
        except (OverflowError, ValueError) as exc:
            raise _InvalidToolArguments("value must be a finite decimal string") from exc
        if not math.isfinite(numeric):
            raise _InvalidToolArguments("value must be a finite decimal string")
        return value

    @classmethod
    def _validated_unit_symbol(cls, value: Any) -> str:
        return cls._validated_nonempty_string(value, "unit")

    @staticmethod
    def _tool_success(value: dict[str, Any]) -> dict[str, Any]:
        text = json.dumps(value, ensure_ascii=False, allow_nan=False, separators=(",", ":"))
        return {
            "content": [{"type": "text", "text": text}],
            "structuredContent": value,
            "isError": False,
        }

    @staticmethod
    def _tool_error(code: str, message: str) -> dict[str, Any]:
        safe_message = message[:1000]
        text = f"{code}: {safe_message}"
        return {
            "content": [{"type": "text", "text": text}],
            "structuredContent": {"error": {"code": code, "message": safe_message}},
            "isError": True,
        }

    @staticmethod
    def _result(request_id: Any, value: dict[str, Any]) -> dict[str, Any]:
        return {"jsonrpc": "2.0", "id": request_id, "result": value}

    @staticmethod
    def _error(
        request_id: Any,
        code: int,
        message: str,
        data: Any | None = None,
    ) -> dict[str, Any]:
        error: dict[str, Any] = {"code": code, "message": message}
        if data is not None:
            error["data"] = data
        return {"jsonrpc": "2.0", "id": request_id, "error": error}


def _serve(config: BridgeConfig) -> int:
    server = McpServer(config)
    try:
        source = sys.stdin.buffer
        destination = sys.stdout.buffer
        while True:
            raw = source.readline(MAX_MCP_LINE_BYTES + 1)
            if not raw:
                return 0
            if len(raw) > MAX_MCP_LINE_BYTES:
                destination.write(dumps_line(server._error(None, -32700, "Parse error: message too large")))
                destination.flush()
                print("MCP input line exceeded the size limit", file=sys.stderr, flush=True)
                return 1
            if raw.endswith(b"\n"):
                raw = raw[:-1]
            if raw.endswith(b"\r"):
                raw = raw[:-1]
            try:
                message = loads_unique(raw.decode("utf-8", errors="strict"))
            except (ValueError, UnicodeError):
                response = server._error(None, -32700, "Parse error")
            else:
                try:
                    response = server.handle_message(message)
                except Exception as exc:
                    print(f"MCP request failed ({type(exc).__name__})", file=sys.stderr, flush=True)
                    if isinstance(message, dict) and "id" not in message:
                        response = None
                    else:
                        fallback_id = message.get("id") if isinstance(message, dict) else None
                        if not server._valid_id(fallback_id):
                            fallback_id = None
                        response = server._error(fallback_id, -32603, "Internal error")
            if response is not None:
                try:
                    destination.write(dumps_line(response))
                    destination.flush()
                except (BrokenPipeError, OSError):
                    return 0
    finally:
        server.close()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Local NX Sketch MCP stdio host")
    parser.add_argument("--config", required=True, help="absolute or working-directory-relative local JSON config")
    args = parser.parse_args(argv)
    try:
        config = load_config(args.config)
    except ConfigError as exc:
        print(f"configuration error: {exc}", file=sys.stderr, flush=True)
        return 2
    except Exception as exc:
        print(f"configuration error ({type(exc).__name__})", file=sys.stderr, flush=True)
        return 2
    return _serve(config)
