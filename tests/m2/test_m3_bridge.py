"""Targeted M3 bridge/helper/stdio checks against the NX-free STA fixture."""

from __future__ import annotations

import tempfile
import time
import unittest
import uuid
from pathlib import Path

from evidence_recorder import record
import test_m2_bridge as _m2
from test_m2_bridge import (
    call_tool,
    successful_helper_result,
    successful_tool_result,
)


class M3BridgeSubsystemTests(unittest.TestCase):
    """Reuse the M2 process helpers while keeping the M3 cases isolated."""

    @classmethod
    def setUpClass(cls):
        _m2.M2BridgeSubsystemTests.setUpClass.__func__(cls)

    @classmethod
    def tearDownClass(cls):
        _m2.M2BridgeSubsystemTests.tearDownClass.__func__(cls)

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="nx-m3-run-")
        self.temp_path = Path(self.temp.name)
        self.pipe_name = "nx-m3-test-" + uuid.uuid4().hex
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
    wait_operation = _m2.M2BridgeSubsystemTests.wait_operation
    release_fixture_operation = _m2.M2BridgeSubsystemTests.release_fixture_operation
    _cleanup_harness = _m2.M2BridgeSubsystemTests._cleanup_harness
    _cleanup_child = staticmethod(_m2.M2BridgeSubsystemTests._cleanup_child)
    _stop_harness = _m2.M2BridgeSubsystemTests._stop_harness

    def takeover(self, ready, *, host=None, helper=None):
        args = {
            "expected_pid": int(ready["pid"]),
            "expected_part_path": "C:\\M2Fake\\model.prt",
            "artifact_scope": "saved_independent_copy",
            "authorize": True,
        }
        if host is not None:
            return successful_tool_result(self, call_tool(host, "nx_takeover", args))
        return successful_helper_result(self, self.helper_call(helper, "ownership.takeover", args))

    def prepare_sketch_args(self, operation_id, owner, epoch, revision_id, geometries,
                            *, plane="XY", origin_mm=(0.0, 0.0, 0.0)):
        return {
            "operation_id": operation_id,
            "ownership_id": owner,
            "binding_epoch": epoch,
            "revision_id": revision_id,
            "plane": plane,
            "origin_mm": list(origin_mm),
            "geometries": geometries,
        }

    def prepare_extrude_args(self, operation_id, owner, epoch, revision_id, sketch_id,
                             *, mode, direction, extent, depth_mm=None, target_body_id=None):
        args = {
            "operation_id": operation_id,
            "ownership_id": owner,
            "binding_epoch": epoch,
            "revision_id": revision_id,
            "sketch_id": sketch_id,
            "mode": mode,
            "direction": direction,
            "extent": extent,
        }
        if depth_mm is not None:
            args["depth_mm"] = depth_mm
        if target_body_id is not None:
            args["target_body_id"] = target_body_id
        return args

    def create_sketch(self, helper, owner, epoch, geometries, *, plane="XY", origin_mm=(0, 0, 0)):
        session = successful_helper_result(self, self.helper_call(helper, "model.get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        prepared = successful_helper_result(self, self.helper_call(helper, "operation.prepare_sketch",
            self.prepare_sketch_args(
                "m3-sketch-prepare-" + uuid.uuid4().hex, owner, epoch,
                session["revision_id"], geometries, plane=plane, origin_mm=origin_mm,
            )))
        plan_id = prepared["result"]["plan"]["plan_id"]
        execute_id = "m3-sketch-execute-" + uuid.uuid4().hex
        self.helper_call(helper, "operation.execute_plan", {
            "operation_id": execute_id,
            "ownership_id": owner,
            "binding_epoch": epoch,
            "plan_id": plan_id,
        })
        completed = self.wait_operation(helper, execute_id)
        self.assertEqual(completed.get("state"), "SUCCEEDED", completed)
        return completed

    def prepare_extrude(self, helper, owner, epoch, sketch_id, *, mode, direction, extent,
                        depth_mm=None, target_body_id=None, operation_id=None):
        session = successful_helper_result(self, self.helper_call(helper, "model.get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        args = self.prepare_extrude_args(
            operation_id or ("m3-extrude-prepare-" + uuid.uuid4().hex),
            owner, epoch, session["revision_id"], sketch_id,
            mode=mode, direction=direction, extent=extent,
            depth_mm=depth_mm, target_body_id=target_body_id,
        )
        return session, args, successful_helper_result(self, self.helper_call(helper, "operation.prepare_extrude", args))

    def execute_plan(self, helper, owner, epoch, plan_id, *, operation_id=None):
        operation_id = operation_id or ("m3-execute-" + uuid.uuid4().hex)
        accepted = successful_helper_result(self, self.helper_call(helper, "operation.execute_plan", {
            "operation_id": operation_id,
            "ownership_id": owner,
            "binding_epoch": epoch,
            "plan_id": plan_id,
        }))
        return operation_id, accepted, self.wait_operation(helper, operation_id)

    def verify_plan(self, helper, host, owner, epoch, execute_operation_id, *, operation_id=None):
        operation_id = operation_id or ("m3-verify-" + uuid.uuid4().hex)
        args = {
            "operation_id": operation_id,
            "ownership_id": owner,
            "binding_epoch": epoch,
            "execute_operation_id": execute_operation_id,
        }
        accepted = successful_tool_result(self, call_tool(host, "nx_verify_part", args))
        self.assertIn(accepted.get("state"), {"QUEUED", "RUNNING", "SUCCEEDED"}, accepted)
        completed = self.wait_operation(helper, operation_id)
        self.assertEqual(completed.get("state"), "SUCCEEDED", completed)
        self.assertIs(completed.get("result", {}).get("verified"), True, completed)
        self.assertEqual(completed.get("result", {}).get("execute_operation_id"), execute_operation_id, completed)
        return completed

    def test_native_sketch_plan_execute_verify_and_driving_dimension_edit(self):
        ready = self.start_harness()
        helper = self.start_helper()
        host, _ = self.start_host(timeout_seconds=10.0)
        grant = self.takeover(ready, host=host)
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]
        session = successful_tool_result(self, call_tool(host, "nx_get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))

        geometries = [
            {"id": "line-a", "type": "line", "start_mm": [0.0, 0.0], "end_mm": [12.0, 9.0]},
            {"id": "rectangle-a", "type": "rectangle", "corner_mm": [20.0, 30.0],
             "width_mm": 24.0, "height_mm": 10.0},
            {"id": "circle-a", "type": "circle", "center_mm": [-25.0, -18.0], "diameter_mm": 16.0},
        ]
        prepare_id = "m3-native-sketch-prepare-" + uuid.uuid4().hex
        prepare_args = self.prepare_sketch_args(
            prepare_id, owner, epoch, session["revision_id"], geometries,
        )
        prepared = successful_tool_result(self, call_tool(host, "nx_prepare_sketch", prepare_args))
        self.assertEqual(prepared.get("state"), "SUCCEEDED", prepared)
        plan = prepared.get("result", {}).get("plan", {})
        self.assertEqual(plan.get("kind"), "create_sketch", plan)
        self.assertEqual(plan.get("plane"), "XY", plan)
        self.assertEqual(plan.get("origin_mm"), [0.0, 0.0, 0.0], plan)
        self.assertEqual([item.get("type") for item in plan.get("geometries", [])],
                         ["line", "rectangle", "circle"], plan)
        dimensions = plan.get("driving_dimensions", [])
        self.assertTrue(dimensions, plan)
        self.assertTrue(all(item.get("unit") == "mm" for item in dimensions), dimensions)
        planned_values = {(item.get("geometry_id"), item.get("role")): item.get("value")
                          for item in dimensions}
        self.assertIn(("line-a", "length_x"), planned_values, dimensions)
        self.assertIn(("line-a", "length_y"), planned_values, dimensions)
        self.assertIn(("rectangle-a", "width"), planned_values, dimensions)
        self.assertIn(("rectangle-a", "height"), planned_values, dimensions)
        self.assertIn(("circle-a", "diameter"), planned_values, dimensions)
        self.assertEqual(float(planned_values[("rectangle-a", "width")]), 24.0, dimensions)
        self.assertEqual(float(planned_values[("rectangle-a", "height")]), 10.0, dimensions)
        self.assertEqual(float(planned_values[("circle-a", "diameter")]), 16.0, dimensions)
        self.assertEqual(float(planned_values[("line-a", "length_x")]), 12.0, dimensions)
        self.assertEqual(float(planned_values[("line-a", "length_y")]), 9.0, dimensions)

        retry_args = dict(reversed(list(prepare_args.items())))
        retried = successful_helper_result(self, self.helper_call(helper, "operation.prepare_sketch", retry_args))
        self.assertEqual(retried.get("result"), prepared.get("result"), retried)
        conflict_args = dict(prepare_args)
        conflict_args["geometries"] = [dict(item) for item in geometries]
        conflict_args["geometries"][2]["diameter_mm"] = 17.0
        conflict = self.helper_call(helper, "operation.prepare_sketch", conflict_args)
        self.assertEqual(conflict.get("error", {}).get("code"), "OPERATION_ID_CONFLICT", conflict)
        counts = self.fake_counts()
        self.assertEqual(counts.get("m3_sketch_prepare_calls"), 1, counts)

        execute_id = "m3-native-sketch-execute-" + uuid.uuid4().hex
        execute_args = {
            "operation_id": execute_id,
            "ownership_id": owner,
            "binding_epoch": epoch,
            "plan_id": plan["plan_id"],
        }
        altered = dict(execute_args)
        altered["geometries"] = geometries
        rejected_override = self.helper_call(helper, "operation.execute_plan", altered)
        self.assertEqual(rejected_override.get("error", {}).get("code"), "INVALID_REQUEST", rejected_override)
        accepted = successful_tool_result(self, call_tool(host, "nx_execute_plan", execute_args))
        self.assertIn(accepted.get("state"), {"QUEUED", "RUNNING", "SUCCEEDED"}, accepted)
        executed = self.wait_operation(helper, execute_id)
        self.assertEqual(executed.get("state"), "SUCCEEDED", executed)
        execution_result = executed.get("result", {})
        self.assertEqual(execution_result.get("kind"), "create_sketch", executed)
        self.assertTrue(execution_result.get("sketch_id"), executed)
        self.assertTrue(execution_result.get("feature_id"), executed)
        self.assertEqual(execution_result.get("sketch_status"), "FullyConstrained", executed)
        self.assertEqual(execution_result.get("degrees_of_freedom"), 0, executed)
        self.assertTrue(execution_result.get("constraint_ids"), executed)
        created_dimensions = execution_result.get("driving_dimensions", [])
        self.assertGreaterEqual(len(created_dimensions), len(dimensions), executed)
        self.assertTrue(all(item.get("is_driving") is True for item in created_dimensions), executed)
        self.assertTrue(all(item.get("constraint_id") for item in created_dimensions), executed)
        self.assertTrue(set(item["constraint_id"] for item in created_dimensions).issubset(
            set(execution_result["constraint_ids"])), executed)

        verify_id = "m3-native-sketch-verify-" + uuid.uuid4().hex
        verify_args = {
            "operation_id": verify_id,
            "ownership_id": owner,
            "binding_epoch": epoch,
            "execute_operation_id": execute_id,
        }
        verify_accepted = successful_tool_result(self, call_tool(host, "nx_verify_part", verify_args))
        self.assertIn(verify_accepted.get("state"), {"QUEUED", "RUNNING", "SUCCEEDED"}, verify_accepted)
        verified = self.wait_operation(helper, verify_id)
        self.assertEqual(verified.get("state"), "SUCCEEDED", verified)
        verification_result = verified.get("result", {})
        self.assertIs(verification_result.get("verified"), True, verified)
        self.assertEqual(verification_result.get("execute_operation_id"), execute_id, verified)
        checks = verification_result.get("checks", {})
        self.assertEqual(set(checks), {"geometry", "constraints", "history", "bodies", "non_target_bodies"}, checks)
        self.assertTrue(all(check.get("status") == "VERIFIED" for check in checks.values()), checks)

        horizontal_projection = next(item for item in created_dimensions
                                     if item.get("geometry_id") == "line-a" and item.get("role") == "length_x")
        current_session = successful_tool_result(self, call_tool(host, "nx_get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        edit_id = "m3-line-projection-edit-prepare-" + uuid.uuid4().hex
        edit_args = {
            "operation_id": edit_id,
            "ownership_id": owner,
            "binding_epoch": epoch,
            "revision_id": current_session["revision_id"],
            "sketch_id": execution_result["sketch_id"],
            "constraint_id": horizontal_projection["constraint_id"],
            "value": "16",
            "unit": "mm",
        }
        edit_plan = successful_tool_result(self, call_tool(host, "nx_prepare_operation", edit_args))
        self.assertEqual(edit_plan.get("state"), "SUCCEEDED", edit_plan)
        parameter_plan = edit_plan.get("result", {}).get("plan", {})
        self.assertEqual(parameter_plan.get("target", {}).get("dimension_type"), "HorizontalDim", parameter_plan)
        self.assertEqual(parameter_plan.get("target", {}).get("constraint_id"), horizontal_projection["constraint_id"], parameter_plan)
        parameter_geometry_refs = parameter_plan.get("target", {}).get("geometry_refs", [])
        self.assertTrue(parameter_geometry_refs, parameter_plan)

        edit_execute_id = "m3-line-projection-edit-execute-" + uuid.uuid4().hex
        self.helper_call(helper, "operation.execute_plan", {
            "operation_id": edit_execute_id,
            "ownership_id": owner,
            "binding_epoch": epoch,
            "plan_id": parameter_plan["plan_id"],
        })
        edited = self.wait_operation(helper, edit_execute_id)
        self.assertEqual(edited.get("state"), "SUCCEEDED", edited)
        edit_result = edited.get("result", {})
        self.assertEqual(edit_result.get("after", {}).get("value"), "16", edited)
        self.assertNotEqual(edit_result.get("geometry_before"), edit_result.get("geometry_after"), edited)

        observed = successful_helper_result(self, self.helper_call(helper, "model.inspect_sketch", {
            "ownership_id": owner,
            "binding_epoch": epoch,
            "sketch_id": execution_result["sketch_id"],
        }))
        observed_constraints = observed.get("sketch", {}).get("constraints", [])
        observed_line_dimension = next(item for item in observed_constraints
                                       if item.get("journal_id") == horizontal_projection["constraint_id"])
        self.assertEqual(observed_line_dimension.get("dimension_state"), "Driving", observed_line_dimension)
        self.assertAlmostEqual(float(observed_line_dimension.get("value")), 16.0, places=8, msg=observed_line_dimension)
        observed_line = next(item for item in observed["sketch"]["geometry"] if item.get("id") == "line-a")
        self.assertEqual(observed_line.get("end_mm"), [16.0, 9.0], observed_line)
        parameter_verification = self.verify_plan(helper, host, owner, epoch, edit_execute_id)
        self.assertEqual(set(parameter_verification.get("result", {}).get("checks", {})),
                         {"expression", "geometry", "history", "associations"}, parameter_verification)

        record("M3-sketch-create-drive-verify", {
            "geometry_types": ["line", "rectangle", "circle"],
            "planned_driving_dimensions": len(dimensions),
            "created_driving_constraints": len(created_dimensions),
            "execute_operation_id": execute_id,
            "verified_checks": sorted(checks),
            "edited_constraint_id": horizontal_projection["constraint_id"],
            "edited_value_mm": 16.0,
            "edited_component": "horizontal projection (length_x)",
            "same_sketch_id": execution_result["sketch_id"],
        })

    def test_m4_query_and_face_sketch_contract_reaches_bridge_adapter(self):
        ready = self.start_harness()
        helper = self.start_helper()
        grant = self.takeover(ready, helper=helper)
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]
        session = successful_helper_result(self, self.helper_call(helper, "model.get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        revision = session["revision_id"]

        selector = "最外侧正X面"
        query = successful_helper_result(self, self.helper_call(helper, "model.query_faces", {
            "ownership_id": owner, "binding_epoch": epoch, "revision_id": revision,
            "selector": selector, "body_id": "body-scope-1",
        }))
        self.assertEqual(query.get("selection_status"), "unique", query)
        self.assertEqual(query.get("candidate_count"), 1, query)
        self.assertEqual(query.get("body_id"), "body-scope-1", query)
        counts = self.fake_counts()
        self.assertEqual(counts.get("m4_face_query_calls"), 1, counts)
        self.assertEqual(counts.get("m4_last_selector"), selector, counts)
        self.assertEqual(counts.get("m4_last_body_id"), "body-scope-1", counts)

        common = {
            "ownership_id": owner, "binding_epoch": epoch, "revision_id": revision,
            "geometries": [{"id": "support-circle", "type": "circle",
                            "center_mm": [20, 3], "diameter_mm": 2}],
        }
        face_request = {
            "operation_id": "m4-face-default-" + uuid.uuid4().hex,
            **common,
            "support_face_selector": "outermost positive X side",
        }
        prepared = successful_helper_result(self, self.helper_call(helper, "operation.prepare_sketch", face_request))
        plan = prepared.get("result", {}).get("plan", {})
        self.assertEqual(plan.get("support_face_selector"), "outermost positive X side", plan)
        self.assertIsNone(plan.get("support_body_id"), plan)
        self.assertEqual(plan.get("face_origin_offset_mm"), [0, 0], plan)

        explicit = dict(face_request)
        explicit["operation_id"] = "m4-face-explicit-" + uuid.uuid4().hex
        explicit["support_body_id"] = "body-scope-2"
        explicit["face_origin_offset_mm"] = [-1.25, 2.5]
        planned = successful_helper_result(self, self.helper_call(helper, "operation.prepare_sketch", explicit))
        explicit_plan = planned.get("result", {}).get("plan", {})
        self.assertEqual(explicit_plan.get("support_body_id"), "body-scope-2", explicit_plan)
        self.assertEqual(explicit_plan.get("face_origin_offset_mm"), [-1.25, 2.5], explicit_plan)

        invalid = dict(face_request)
        invalid["operation_id"] = "m4-face-mixed-" + uuid.uuid4().hex
        invalid["plane"] = "XY"
        invalid["origin_mm"] = [0, 0, 0]
        rejected = self.helper_call(helper, "operation.prepare_sketch", invalid)
        self.assertEqual(rejected.get("error", {}).get("code"), "INVALID_REQUEST", rejected)
        invalid = dict(face_request)
        invalid["operation_id"] = "m4-face-candidate-" + uuid.uuid4().hex
        invalid["candidate_id"] = "candidate-001"
        rejected = self.helper_call(helper, "operation.prepare_sketch", invalid)
        self.assertEqual(rejected.get("error", {}).get("code"), "INVALID_REQUEST", rejected)
        counts = self.fake_counts()
        self.assertEqual(counts.get("m3_sketch_prepare_calls"), 2, counts)

    def test_extrude_create_unite_subtract_direction_target_and_non_target(self):
        ready = self.start_harness()
        helper = self.start_helper()
        host, _ = self.start_host(timeout_seconds=10.0)
        grant = self.takeover(ready, host=host)
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]

        target_sketch = self.create_sketch(helper, owner, epoch, [{
            "id": "target-profile", "type": "rectangle", "corner_mm": [10.0, 10.0],
            "width_mm": 20.0, "height_mm": 10.0,
        }], origin_mm=(0.0, 0.0, -2.0))
        target_sketch_id = target_sketch["result"]["sketch_id"]
        _, create_args, create_prepared = self.prepare_extrude(
            helper, owner, epoch, target_sketch_id,
            mode="create", direction="positive", extent="finite", depth_mm=7.0,
        )
        create_plan = create_prepared["result"]["plan"]
        self.assertEqual(create_plan.get("kind"), "create_extrude", create_plan)
        self.assertEqual(create_plan.get("mode"), "create", create_plan)
        self.assertEqual(create_plan.get("extent"), "finite", create_plan)
        self.assertEqual(create_plan.get("depth_mm"), 7.0, create_plan)
        self.assertIsNone(create_plan.get("target_body_id"), create_plan)
        create_id, _, created = self.execute_plan(helper, owner, epoch, create_plan["plan_id"])
        target_body_id = created.get("result", {}).get("body_id")
        self.assertEqual(created.get("state"), "SUCCEEDED", created)
        self.assertTrue(target_body_id, created)
        target_before = next(body for body in created["result"]["body_inventory"]
                             if body.get("journal_id") == target_body_id)
        self.assertIs(target_before.get("is_solid"), True, target_before)
        self.assertAlmostEqual(float(target_before.get("volume_mm3")), 1400.0, places=7, msg=target_before)
        self.assertEqual(len(target_before.get("bbox_mm", [])), 6, target_before)

        boolean_sketch = self.create_sketch(helper, owner, epoch, [{
            "id": "boolean-profile", "type": "rectangle", "corner_mm": [15.0, 12.0],
            "width_mm": 5.0, "height_mm": 4.0,
        }])
        boolean_sketch_id = boolean_sketch["result"]["sketch_id"]
        non_target_sketch = self.create_sketch(helper, owner, epoch, [{
            "id": "non-target-profile", "type": "rectangle", "corner_mm": [60.0, 50.0],
            "width_mm": 3.0, "height_mm": 3.0,
        }])
        non_target_sketch_id = non_target_sketch["result"]["sketch_id"]
        _, other_create_args, other_create_prepared = self.prepare_extrude(
            helper, owner, epoch, non_target_sketch_id,
            mode="create", direction="positive", extent="finite", depth_mm=2.0,
        )
        other_create_id, _, other_created = self.execute_plan(
            helper, owner, epoch, other_create_prepared["result"]["plan"]["plan_id"],
        )
        self.assertEqual(other_created.get("state"), "SUCCEEDED", other_created)
        non_target_body_id = other_created["result"]["body_id"]
        body_before_boolean = {body["journal_id"]: body for body in other_created["result"]["body_inventory"]}
        non_target_before = body_before_boolean[non_target_body_id]

        union_id = "m3-unite-prepare-" + uuid.uuid4().hex
        union_session = successful_tool_result(self, call_tool(host, "nx_get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        union_args = self.prepare_extrude_args(
            union_id, owner, epoch, union_session["revision_id"], boolean_sketch_id,
            mode="unite", direction="negative", extent="finite", depth_mm=4.0,
            target_body_id=target_body_id,
        )
        union_prepared = successful_tool_result(self, call_tool(host, "nx_prepare_extrude", union_args))
        union_plan = union_prepared["result"]["plan"]
        self.assertEqual(union_plan.get("mode"), "unite", union_plan)
        self.assertEqual(union_plan.get("direction"), "negative", union_plan)
        self.assertEqual(union_plan.get("extent"), "finite", union_plan)
        self.assertEqual(union_plan.get("target_body_id"), target_body_id, union_plan)
        self.assertEqual(union_plan.get("target_body_before", {}).get("journal_id"), target_body_id, union_plan)
        self.assertEqual([body.get("journal_id") for body in union_plan.get("non_target_bodies_before", [])],
                         [non_target_body_id], union_plan)
        union_execute_id = "m3-unite-execute-" + uuid.uuid4().hex
        union_accepted = successful_tool_result(self, call_tool(host, "nx_execute_plan", {
            "operation_id": union_execute_id, "ownership_id": owner,
            "binding_epoch": epoch, "plan_id": union_plan["plan_id"],
        }))
        self.assertIn(union_accepted.get("state"), {"QUEUED", "RUNNING", "SUCCEEDED"}, union_accepted)
        union_completed = self.wait_operation(helper, union_execute_id)
        self.assertEqual(union_completed.get("state"), "SUCCEEDED", union_completed)
        union_bodies = {body["journal_id"]: body for body in union_completed["result"]["body_inventory"]}
        self.assertEqual(set(union_bodies), {target_body_id, non_target_body_id}, union_bodies)
        self.assertEqual(union_bodies[non_target_body_id], non_target_before, union_bodies)
        self.assertGreater(float(union_bodies[target_body_id]["volume_mm3"]),
                           float(target_before["volume_mm3"]), union_bodies)
        union_verified = self.verify_plan(helper, host, owner, epoch, union_execute_id)
        union_checks = union_verified["result"]["checks"]
        self.assertTrue(all(check.get("status") == "VERIFIED" for check in union_checks.values()), union_checks)

        subtract_session = successful_helper_result(self, self.helper_call(helper, "model.get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        subtract_args = self.prepare_extrude_args(
            "m3-subtract-prepare-" + uuid.uuid4().hex, owner, epoch, subtract_session["revision_id"],
            boolean_sketch_id, mode="subtract", direction="positive", extent="through_all",
            target_body_id=target_body_id,
        )
        self.assertNotIn("depth_mm", subtract_args)
        subtract_prepared = successful_helper_result(self, self.helper_call(helper, "operation.prepare_extrude", subtract_args))
        subtract_plan = subtract_prepared["result"]["plan"]
        self.assertEqual(subtract_plan.get("mode"), "subtract", subtract_plan)
        self.assertEqual(subtract_plan.get("direction"), "positive", subtract_plan)
        self.assertEqual(subtract_plan.get("extent"), "through_all", subtract_plan)
        self.assertEqual(subtract_plan.get("target_body_id"), target_body_id, subtract_plan)
        self.assertIsNone(subtract_plan.get("depth_mm"), subtract_plan)
        subtract_execute_id, _, subtracted = self.execute_plan(
            helper, owner, epoch, subtract_plan["plan_id"],
        )
        self.assertEqual(subtracted.get("state"), "SUCCEEDED", subtracted)
        subtract_bodies = {body["journal_id"]: body for body in subtracted["result"]["body_inventory"]}
        self.assertEqual(set(subtract_bodies), {target_body_id, non_target_body_id}, subtract_bodies)
        self.assertEqual(subtract_bodies[non_target_body_id], non_target_before, subtract_bodies)
        self.assertLess(float(subtract_bodies[target_body_id]["volume_mm3"]),
                        float(union_bodies[target_body_id]["volume_mm3"]), subtract_bodies)
        subtract_verified = self.verify_plan(helper, host, owner, epoch, subtract_execute_id)
        subtract_checks = subtract_verified["result"]["checks"]
        self.assertTrue(all(check.get("status") == "VERIFIED" for check in subtract_checks.values()), subtract_checks)
        non_target_evidence = subtract_checks["non_target_bodies"].get("evidence", {})
        self.assertEqual(non_target_evidence.get("before"), non_target_evidence.get("after"), non_target_evidence)

        record("M3-extrude-create-unite-subtract", {
            "create": {"direction": "positive", "extent": "finite", "depth_mm": 7.0},
            "unite": {"direction": "negative", "extent": "finite", "depth_mm": 4.0,
                      "target_body_id": target_body_id},
            "subtract": {"direction": "positive", "extent": "through_all",
                         "target_body_id": target_body_id},
            "body_count_after_create": 1,
            "body_count_after_booleans": len(subtract_bodies),
            "non_target_body_unchanged": True,
            "verified_checks": sorted(subtract_checks),
        })

    def test_direct_helper_rejects_invalid_sketch_and_extrude_requests_before_adapter(self):
        ready = self.start_harness()
        helper = self.start_helper()
        geometries = [{
            "id": "line-valid", "type": "line", "start_mm": [0.0, 0.0], "end_mm": [10.0, 0.0],
        }]
        unowned = self.helper_call(helper, "operation.prepare_sketch", self.prepare_sketch_args(
            "m3-unowned-" + uuid.uuid4().hex, "no-current-owner", 1, "1", geometries,
        ))
        self.assertEqual(unowned.get("error", {}).get("code"), "AUTHORIZATION_REQUIRED", unowned)

        grant = self.takeover(ready, helper=helper)
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]
        session = successful_helper_result(self, self.helper_call(helper, "model.get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        revision = session["revision_id"]

        stale_epoch = self.helper_call(helper, "operation.prepare_sketch", self.prepare_sketch_args(
            "m3-stale-epoch-" + uuid.uuid4().hex, owner, epoch + 1, revision, geometries,
        ))
        self.assertEqual(stale_epoch.get("error", {}).get("code"), "AUTHORIZATION_REQUIRED", stale_epoch)

        stale = self.helper_call(helper, "operation.prepare_sketch", self.prepare_sketch_args(
            "m3-stale-revision-" + uuid.uuid4().hex, owner, epoch, "stale-revision", geometries,
        ))
        self.assertEqual(stale.get("result", {}).get("state"), "FAILED", stale)
        self.assertEqual(stale.get("result", {}).get("error", {}).get("code"), "STALE_REVISION", stale)

        invalid_requests = [
            self.prepare_sketch_args("m3-invalid-plane-" + uuid.uuid4().hex, owner, epoch, revision,
                                     geometries, plane="AB"),
            self.prepare_sketch_args("m3-zero-line-" + uuid.uuid4().hex, owner, epoch, revision, [{
                "id": "zero", "type": "line", "start_mm": [2.0, 3.0], "end_mm": [2.0, 3.0],
            }]),
            self.prepare_sketch_args("m3-duplicate-id-" + uuid.uuid4().hex, owner, epoch, revision, [
                {"id": "same", "type": "circle", "center_mm": [0.0, 0.0], "diameter_mm": 5.0},
                {"id": "same", "type": "circle", "center_mm": [10.0, 0.0], "diameter_mm": 7.0},
            ]),
            self.prepare_sketch_args("m3-zero-width-" + uuid.uuid4().hex, owner, epoch, revision, [{
                "id": "zero-width", "type": "rectangle", "corner_mm": [0.0, 0.0],
                "width_mm": 0.0, "height_mm": 5.0,
            }]),
            self.prepare_sketch_args("m3-zero-diameter-" + uuid.uuid4().hex, owner, epoch, revision, [{
                "id": "zero-diameter", "type": "circle", "center_mm": [0.0, 0.0], "diameter_mm": 0.0,
            }]),
        ]
        for args in invalid_requests:
            response = self.helper_call(helper, "operation.prepare_sketch", args)
            self.assertEqual(response.get("error", {}).get("code"), "INVALID_REQUEST", response)

        invalid_extrusions = [
            {
                "operation_id": "m3-create-through-all-" + uuid.uuid4().hex,
                "ownership_id": owner, "binding_epoch": epoch, "revision_id": revision,
                "sketch_id": "unresolved", "mode": "create", "direction": "positive", "extent": "through_all",
            },
            {
                "operation_id": "m3-boolean-without-target-" + uuid.uuid4().hex,
                "ownership_id": owner, "binding_epoch": epoch, "revision_id": revision,
                "sketch_id": "unresolved", "mode": "unite", "direction": "positive",
                "extent": "finite", "depth_mm": 5.0,
            },
            {
                "operation_id": "m3-create-with-target-" + uuid.uuid4().hex,
                "ownership_id": owner, "binding_epoch": epoch, "revision_id": revision,
                "sketch_id": "unresolved", "mode": "create", "direction": "positive",
                "extent": "finite", "depth_mm": 5.0, "target_body_id": "body-1",
            },
            {
                "operation_id": "m3-zero-depth-" + uuid.uuid4().hex,
                "ownership_id": owner, "binding_epoch": epoch, "revision_id": revision,
                "sketch_id": "unresolved", "mode": "create", "direction": "positive",
                "extent": "finite", "depth_mm": 0.0,
            },
        ]
        for args in invalid_extrusions:
            response = self.helper_call(helper, "operation.prepare_extrude", args)
            self.assertEqual(response.get("error", {}).get("code"), "INVALID_REQUEST", response)

        counts = self.fake_counts()
        self.assertEqual(counts.get("m3_sketch_prepare_calls"), 0, counts)
        self.assertEqual(counts.get("m3_extrude_prepare_calls"), 0, counts)
        self.assertEqual(counts.get("m3_execution_calls"), 0, counts)
        record("M3-helper-validation", {
            "unowned_error": unowned["error"]["code"],
            "stale_binding_epoch_error": stale_epoch["error"]["code"],
            "stale_revision_error": stale["result"]["error"]["code"],
            "invalid_sketch_cases": len(invalid_requests),
            "invalid_extrude_cases": len(invalid_extrusions),
            "adapter_prepare_calls_after_rejections": 0,
        })

    def test_fixture_rollback_leaves_no_extrude_body_and_allows_retest(self):
        ready = self.start_harness()
        helper = self.start_helper()
        grant = self.takeover(ready, helper=helper)
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]
        sketch = self.create_sketch(helper, owner, epoch, [{
            "id": "rollback-profile", "type": "rectangle", "corner_mm": [0.0, 0.0],
            "width_mm": 12.0, "height_mm": 8.0,
        }])
        sketch_id = sketch["result"]["sketch_id"]
        _, _, prepared = self.prepare_extrude(
            helper, owner, epoch, sketch_id, mode="create", direction="positive",
            extent="finite", depth_mm=6.0,
        )
        plan_id = prepared["result"]["plan"]["plan_id"]
        self.harness_command("rollback-after-write")
        failed_id, _, failed = self.execute_plan(helper, owner, epoch, plan_id)
        self.assertEqual(failed.get("state"), "FAILED", failed)
        self.assertEqual(failed.get("error", {}).get("code"), "EDIT_NOT_VERIFIED", failed)
        self.assertEqual(failed.get("result", {}).get("mutation_state"), "ROLLED_BACK", failed)
        self.assertIs(failed.get("result", {}).get("changed"), False, failed)
        self.assertEqual(failed.get("result", {}).get("body_inventory"), [], failed)

        status = successful_helper_result(self, self.helper_call(helper, "bridge.status", {}))
        self.assertIs(status.get("writes_halted"), False, status)
        self.assertEqual(status.get("ownership_state"), "AI_OWNED", status)
        after_failure = self.fake_counts()
        self.assertEqual(after_failure.get("m3_body_count"), 0, after_failure)
        self.assertEqual(after_failure.get("rollback_count"), 1, after_failure)

        _, _, retry_prepared = self.prepare_extrude(
            helper, owner, epoch, sketch_id, mode="create", direction="positive",
            extent="finite", depth_mm=6.0,
        )
        retry_id, _, retry = self.execute_plan(
            helper, owner, epoch, retry_prepared["result"]["plan"]["plan_id"],
        )
        self.assertEqual(retry.get("state"), "SUCCEEDED", retry)
        self.assertEqual(retry.get("result", {}).get("mutation_state"), "APPLIED", retry)
        self.assertEqual(self.fake_counts().get("m3_body_count"), 1)
        record("M3-rollback-retest", {
            "injection": "rollback-after-write",
            "failed_execute_operation_id": failed_id,
            "failure_error": failed["error"]["code"],
            "body_count_after_rollback": after_failure["m3_body_count"],
            "writes_halted_after_fixture_rollback_state": status["writes_halted"],
            "fresh_execute_operation_id": retry_id,
            "retest_state": retry["state"],
        })

    def test_unknown_m3_result_freezes_writes_and_ownership(self):
        ready = self.start_harness()
        helper = self.start_helper()
        grant = self.takeover(ready, helper=helper)
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]
        sketch = self.create_sketch(helper, owner, epoch, [{
            "id": "unknown-profile", "type": "rectangle", "corner_mm": [0.0, 0.0],
            "width_mm": 10.0, "height_mm": 5.0,
        }])
        sketch_id = sketch["result"]["sketch_id"]
        _, _, first_prepared = self.prepare_extrude(
            helper, owner, epoch, sketch_id, mode="create", direction="positive",
            extent="finite", depth_mm=3.0,
        )
        _, _, second_prepared = self.prepare_extrude(
            helper, owner, epoch, sketch_id, mode="create", direction="negative",
            extent="finite", depth_mm=4.0,
        )
        executions_before = self.fake_counts()["m3_execution_calls"]
        self.harness_command("unknown-after-mutation")
        unknown_id, _, unknown = self.execute_plan(
            helper, owner, epoch, first_prepared["result"]["plan"]["plan_id"],
        )
        self.assertEqual(unknown.get("state"), "UNKNOWN", unknown)
        self.assertEqual(unknown.get("error", {}).get("code"), "UNKNOWN_RESULT", unknown)
        self.assertEqual(unknown.get("result", {}).get("mutation_state"), "UNKNOWN", unknown)
        self.assertIsNone(unknown.get("result", {}).get("changed"), unknown)
        replay = successful_helper_result(self, self.helper_call(helper, "operation.execute_plan", {
            "operation_id": unknown_id,
            "ownership_id": owner,
            "binding_epoch": epoch,
            "plan_id": first_prepared["result"]["plan"]["plan_id"],
        }))
        self.assertEqual(replay.get("state"), "UNKNOWN", replay)
        status = successful_helper_result(self, self.helper_call(helper, "bridge.status", {}))
        self.assertIs(status.get("writes_halted"), True, status)
        self.assertEqual(status.get("ownership_state"), "UNKNOWN_FROZEN", status)
        self.assertIs(status.get("model_write_enabled"), False, status)

        blocked = self.helper_call(helper, "operation.execute_plan", {
            "operation_id": "m3-blocked-execute-" + uuid.uuid4().hex,
            "ownership_id": owner,
            "binding_epoch": epoch,
            "plan_id": second_prepared["result"]["plan"]["plan_id"],
        })
        self.assertEqual(blocked.get("error", {}).get("code"), "OPERATION_OUTCOME_UNKNOWN", blocked)
        returned = self.helper_call(helper, "ownership.return", {
            "ownership_id": owner, "binding_epoch": epoch,
        })
        self.assertEqual(returned.get("error", {}).get("code"), "OPERATION_OUTCOME_UNKNOWN", returned)
        counts = self.fake_counts()
        self.assertEqual(counts.get("m3_execution_calls"), executions_before + 1, counts)
        self.assertEqual(counts.get("m3_body_count"), 1, counts)
        record("M3-unknown-freeze", {
            "injection": "unknown-after-mutation",
            "operation_id": unknown_id,
            "state": unknown["state"],
            "query_replay_state": replay["state"],
            "writes_halted": status["writes_halted"],
            "blocked_execute_error": blocked["error"]["code"],
            "return_error": returned["error"]["code"],
            "adapter_execute_calls_for_unknown_and_blocked": counts["m3_execution_calls"] - executions_before,
        })

    def test_timed_out_m3_operation_keeps_unknown_after_late_completion(self):
        ready = self.start_harness()
        helper = self.start_helper()
        grant = self.takeover(ready, helper=helper)
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]
        self.addCleanup(self.release_fixture_operation)
        sketch = self.create_sketch(helper, owner, epoch, [{
            "id": "late-profile", "type": "rectangle", "corner_mm": [0.0, 0.0],
            "width_mm": 14.0, "height_mm": 6.0,
        }])
        _, _, prepared = self.prepare_extrude(
            helper, owner, epoch, sketch["result"]["sketch_id"], mode="create",
            direction="positive", extent="finite", depth_mm=5.0,
        )
        operation_id = "m3-late-execute-" + uuid.uuid4().hex
        self.harness_command("block-operation")
        accepted = successful_helper_result(self, self.helper_call(helper, "operation.execute_plan", {
            "operation_id": operation_id,
            "ownership_id": owner,
            "binding_epoch": epoch,
            "plan_id": prepared["result"]["plan"]["plan_id"],
        }))
        self.assertIn(accepted.get("state"), {"QUEUED", "RUNNING"}, accepted)
        started = self.harness_command("wait-operation-start")
        self.assertIs(started.get("observed"), True, started)

        aged = self.harness_command("age-running-m3-operation")
        self.assertIs(aged.get("observed"), True, aged)
        status = successful_helper_result(self, self.helper_call(helper, "bridge.status", {}))
        self.assertIs(status.get("writes_halted"), True, status)
        self.assertEqual(status.get("ownership_state"), "UNKNOWN_FROZEN", status)
        timed_out = self.get_operation(helper, operation_id)
        self.assertEqual(timed_out.get("state"), "UNKNOWN", timed_out)
        self.assertEqual(timed_out.get("error", {}).get("code"), "OPERATION_EXECUTION_TIMEOUT", timed_out)

        self.harness_command("release-operation")
        completed_late = None
        deadline = time.monotonic() + 5.0
        while time.monotonic() < deadline:
            completed_late = self.get_operation(helper, operation_id)
            if "late_completion" in completed_late:
                break
            time.sleep(0.02)
        self.assertIsInstance(completed_late, dict)
        self.assertEqual(completed_late.get("state"), "UNKNOWN", completed_late)
        self.assertEqual(completed_late.get("error", {}).get("code"), "OPERATION_EXECUTION_TIMEOUT", completed_late)
        late_result = completed_late.get("late_completion", {})
        self.assertEqual(late_result.get("mutation_state"), "APPLIED", completed_late)
        self.assertTrue(late_result.get("body_id"), completed_late)
        blocked_return = self.helper_call(helper, "ownership.return", {
            "ownership_id": owner, "binding_epoch": epoch,
        })
        self.assertEqual(blocked_return.get("error", {}).get("code"), "OPERATION_OUTCOME_UNKNOWN", blocked_return)
        counts = self.fake_counts()
        self.assertEqual(counts.get("m3_execution_calls"), 2, counts)
        self.assertEqual(counts.get("m3_body_count"), 1, counts)
        record("M3-timeout-late-completion", {
            "operation_id": operation_id,
            "timestamp_aged_by_harness": True,
            "timeout_state_after_late_completion": completed_late["state"],
            "late_adapter_state": late_result["mutation_state"],
            "writes_remain_halted": status["writes_halted"],
            "return_error": blocked_return["error"]["code"],
        })
