"""M4 real NX/stdio replay; use a new registered disposable session per run."""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import json
import math
import os
from pathlib import Path
import sys
import time

PROJECT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT / "tests/m2"))
from stdio_client import StdioMcpProcess, assert_stdout_jsonl_only


class Bench:
    def __init__(self, launch, window, output):
        self.launch, self.window, self.output = launch, window, output
        self.source = Path(launch["source_part"])
        self.input = Path(launch["copied_part"])
        self.source_before, self.input_before = self.source.read_bytes(), self.input.read_bytes()
        self.saved = self.input.with_name("m4-saved.prt")
        if self.saved.exists():
            raise ValueError("use a new session and absent save target")
        self.report = {"started_utc": datetime.now(timezone.utc).isoformat(), "result": "FAIL",
                       "evidence_level": "real NX API through local MCP stdio; representative saved independent copy",
                       "nx_pid": launch["pid"], "nx_process_start_utc": launch["process_start_utc"],
                       "input_path": str(self.input), "steps": [], "cases": []}
        self.client = StdioMcpProcess(
            [sys.executable, "-B", str(PROJECT / "tools/mcp_stdio.py"), "--config", launch["config_path"]],
            cwd=str(PROJECT), env={**os.environ, "PYTHONUTF8": "1"})
        self.binding = None
        self.sequence = 0

    def call(self, name, arguments, expect_error=False):
        response = self.client.request("tools/call", {"name": name, "arguments": arguments}, timeout=130)
        self.report["steps"].append({"tool": name, "arguments": arguments, "response": response})
        result = response.get("result", {})
        payload = result.get("structuredContent", {})
        if expect_error:
            assert result.get("isError") is True, response
        elif result.get("isError") is not False:
            raise RuntimeError(f"{name}: {payload or response}")
        return payload

    def operation(self, name, fields, expect_success=True):
        self.sequence += 1
        operation = self.call(name, {**self.binding, "operation_id": f"m4-{self.sequence}-{name}", **fields})
        deadline = time.monotonic() + 125
        while operation["state"] in ("QUEUED", "RUNNING"):
            if time.monotonic() >= deadline:
                raise TimeoutError(str(operation))
            time.sleep(0.1)
            operation = self.call("nx_get_operation", {"operation_id": operation["operation_id"]})
        if expect_success:
            assert operation["state"] == "SUCCEEDED", operation
        return operation

    def inspect(self):
        return self.call("nx_inspect_part", self.binding)

    def apply(self, name, fields, label):
        before = self.inspect()
        prepared = self.operation(name, {"revision_id": before["revision_id"], **fields})
        plan = prepared["result"]["plan"]
        executed = self.operation("nx_execute_plan", {"plan_id": plan["plan_id"]})
        verified = self.operation("nx_verify_part", {"execute_operation_id": executed["operation_id"]})
        assert verified["result"]["verified"] is True, verified
        assert all(c["status"] == "VERIFIED" for c in verified["result"]["checks"].values()), verified
        self.report["cases"].append({"label": label, "plan": plan, "execution": executed, "verification": verified})
        return executed["result"]

    def sketch(self, shape, origin, label, plane="XY"):
        result = self.apply("nx_prepare_sketch", {"plane": plane, "origin_mm": origin,
                            "geometries": [{"id": label, **shape}]}, label)
        return result["sketch_id"]

    def extrude(self, sketch_id, mode, direction, label, depth=None, target=None):
        fields = {"sketch_id": sketch_id, "mode": mode, "direction": direction,
                  "extent": "finite" if depth is not None else "through_all"}
        if depth is not None:
            fields["depth_mm"] = depth
        if target is not None:
            fields["target_body_id"] = target
        return self.apply("nx_prepare_extrude", fields, label)

    def edit_fields(self, sketch_id, dimension_type, current_value, value):
        part = self.inspect()
        sketch = next(s for s in part["sketches"] if s["journal_id"] == sketch_id)
        expressions = {e["name"]: float(e["value"]) for e in part["expressions"]}
        matches = [c for c in sketch["constraints"] if c.get("is_driving") is True
                   and c["constraint_type"] == dimension_type and math.isclose(
                       expressions[c["expression_name"]], current_value, abs_tol=1e-6)]
        assert len(matches) == 1, matches
        return {"sketch_id": sketch_id, "constraint_id": matches[0]["journal_id"],
                "value": str(value), "unit": "mm"}

    def check_volume(self, result, expected, label):
        body_id = result["body_id"]
        body = next(b for b in result["body_inventory"] if b["journal_id"] == body_id)
        actual = float(body["volume_mm3"])
        assert math.isclose(actual, expected, rel_tol=1e-8, abs_tol=1e-5), (label, actual, expected)
        self.report.setdefault("volume_checks", []).append(
            {"label": label, "body_id": body_id, "actual_mm3": actual, "expected_mm3": expected})

    def reject_prepare(self, name, fields, label, expected_codes):
        before = self.inspect()
        outcome = self.operation(name, {"revision_id": before["revision_id"], **fields}, False)
        assert outcome["state"] == "FAILED", outcome
        code = outcome.get("error", {}).get("code")
        assert code in expected_codes, (label, outcome)
        after = self.inspect()
        assert after["features"] == before["features"] and bodies_match(after["bodies"], before["bodies"])
        self.report.setdefault("rejections", []).append({"label": label, "outcome": outcome, "model_unchanged": True})
        return outcome

    def start(self):
        response = self.client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                                       "clientInfo": {"name": "m4-nx-smoke", "version": "1"}})
        assert response["result"]["protocolVersion"] == "2025-11-25"
        self.client.notification("notifications/initialized")
        listing = self.client.request("tools/list")
        self.report["tools"] = [t["name"] for t in listing["result"]["tools"]]
        assert "nx_query_faces" in self.report["tools"]
        status = self.call("nx_bridge_status", {})
        assert status["pid"] == self.launch["pid"] == self.window["pid"]
        assert status["ownership_state"] == "HUMAN_OWNED"
        assert status["main_thread_id"] == self.window["nx_window_thread_id"]
        ping = self.call("nx_bridge_ping", {"nonce": "M4 face association"})
        assert ping["on_main_thread"] and ping["callback_thread_id"] == self.window["nx_window_thread_id"]
        owned = self.call("nx_takeover", {"expected_pid": self.launch["pid"], "expected_part_path": str(self.input),
                                         "artifact_scope": "saved_independent_copy", "authorize": True})
        self.binding = {"ownership_id": owned["ownership_id"], "binding_epoch": owned["binding_epoch"]}
        self.report["capabilities"] = self.call("nx_get_capabilities", {})
        self.report["before"] = self.inspect()

    def save_and_return(self):
        self.report["before_save"] = self.inspect()
        saved = self.operation("nx_save_copy", {"target_path": str(self.saved), "reopen": True})
        result = saved["result"]
        assert result["saved"] and result["reopened"] and all(
            c["status"] == "VERIFIED" for c in result["checks"].values()), saved
        self.binding = {"ownership_id": result["new_ownership_id"], "binding_epoch": result["new_binding_epoch"]}
        self.report["save"] = saved
        self.report["after_reopen"] = self.inspect()
        reopened_associations = []
        for sketch_id in self.report.get("face_sketch_ids", []):
            sketch = self.call("nx_inspect_sketch", {**self.binding, "sketch_id": sketch_id})
            assert all(sketch["support_association"][key] is True for key in
                       ("native", "selector_unique", "in_face_domain", "current_face_matches")), sketch
            assert sketch["support_association"]["geometry_kind"] == "Face", sketch
            reopened_associations.append(sketch)
        self.report["reopened_associations"] = reopened_associations
        if self.report.get("resume_model"):
            for sketch in reopened_associations:
                if sketch["sketch"]["journal_id"] == self.report["resume_model"]["side_sketch_id"]:
                    check_circle(self, sketch, [300 + self.report["resume_model"]["width_mm"], 20, 3], 1,
                                 "reopened-side-circle")
        returned = self.call("nx_return", self.binding)
        assert returned["ownership_state"] == "HUMAN_OWNED" and returned["safe_for_human"]
        self.binding = None
        self.report["return"] = returned
        self.report["final_status"] = self.call("nx_bridge_status", {})
        assert self.report["final_status"]["writes_halted"] is False

    def finish(self):
        if self.binding is not None:
            try:
                self.report["cleanup_return"] = self.call("nx_return", self.binding)
            except Exception as exc:
                self.report["cleanup_failure"] = str(exc)
        self.client.close_stdin()
        try:
            self.report["stdio_exit_code"] = self.client.wait(timeout=5)
            assert self.report["stdio_exit_code"] == 0
            self.report["stdout_jsonl_lines"] = assert_stdout_jsonl_only(self.client.all_stdout_bytes())
        except Exception as exc:
            self.report["shutdown_failure"] = str(exc)
            self.client.terminate_own_process()
            self.report["result"] = "FAIL"
        self.report["source_bytes_unchanged"] = self.source.read_bytes() == self.source_before
        self.report["input_bytes_unchanged"] = self.input.read_bytes() == self.input_before
        if not self.report["source_bytes_unchanged"] or not self.report["input_bytes_unchanged"]:
            self.report["result"] = "FAIL"
        self.report["finished_utc"] = datetime.now(timezone.utc).isoformat()
        self.output.write_text(json.dumps(self.report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def bodies_match(left, right):
    a, b = {x["journal_id"]: x for x in left}, {x["journal_id"]: x for x in right}
    if a.keys() != b.keys():
        return False
    for body_id, old in a.items():
        new = b[body_id]
        metrics = {"volume_mm3", "bbox_mm"}
        if {k: v for k, v in old.items() if k not in metrics} != {k: v for k, v in new.items() if k not in metrics}:
            return False
        if not math.isclose(float(old["volume_mm3"]), float(new["volume_mm3"]), rel_tol=1e-9, abs_tol=1e-7):
            return False
        if not all(math.isclose(float(x), float(y), rel_tol=1e-9, abs_tol=1e-7)
                   for x, y in zip(old["bbox_mm"], new["bbox_mm"])):
            return False
    return True


def check_circle(bench, sketch, center, radius, label):
    circles = [g for g in sketch["profile_geometry"] if g["kind"] == "ARC"]
    assert len(circles) == 1, circles
    circle = circles[0]
    assert all(math.isclose(float(a), e, abs_tol=1e-5) for a, e in zip(circle["center"], center)), circle
    assert math.isclose(float(circle["radius_mm"]), radius, abs_tol=1e-5), circle
    bench.report.setdefault("circle_checks", []).append({"label": label, "actual": circle,
                                                        "expected_center_mm": center,
                                                        "expected_radius_mm": radius})


def run_cases(bench):
    def rect(width, height):
        return {"type": "rectangle", "corner_mm": [0, 0], "width_mm": width, "height_mm": height}

    def query(body_id, selector):
        part = bench.inspect()
        response = bench.call("nx_query_faces", {**bench.binding, "revision_id": part["revision_id"],
                                                "body_id": body_id, "selector": selector})
        bench.report.setdefault("face_queries", []).append({"selector": selector, "response": response})
        return response

    def unique(body_id, selector):
        result = query(body_id, selector)
        assert result["selection_status"] == "unique" and result["candidate_count"] == 1, result
        return result["candidates"][0]

    def local(face, point):
        frame = face["frame"]
        delta = [p - o for p, o in zip(point, frame["origin_mm"])]
        return [sum(d * u for d, u in zip(delta, frame[axis]))
                for axis in ("x_axis_world", "y_axis_world")]

    def face_fields(body_id, selector, geometries):
        return {"support_body_id": body_id, "support_face_selector": selector, "geometries": geometries}

    def face_circle(body_id, selector, center, diameter, label):
        face = unique(body_id, selector)
        fields = face_fields(body_id, selector, [{"id": label, "type": "circle",
                                                 "center_mm": local(face, center), "diameter_mm": diameter}])
        result = bench.apply("nx_prepare_sketch", fields, label)
        association = result["support_association"]
        assert association["native"] and association["geometry_kind"] == "Face", result
        return result["sketch_id"]

    base_sketch = bench.sketch(rect(60, 40), [300, 0, 0], "bracket-base")
    base = bench.extrude(base_sketch, "create", "positive", "bracket-base-solid", depth=6)
    upright_sketch = bench.sketch(rect(10, 40), [300, 0, 6], "bracket-upright")
    bracket = bench.extrude(upright_sketch, "unite", "positive", "L-bracket", depth=24, target=base["body_id"])
    target = bracket["body_id"]
    bench.check_volume(bracket, 24000, "L-bracket")

    ambiguous = query(target, "positive X side")
    assert ambiguous["selection_status"] == "ambiguous" and ambiguous["candidate_count"] == 2, ambiguous
    assert ambiguous["clarification_required"] is True
    ambiguity_fields = face_fields(target, "positive X side", [
        {"id": "ambiguous", "type": "circle", "center_mm": [1, 1], "diameter_mm": 1}])
    bench.reject_prepare("nx_prepare_sketch", ambiguity_fields, "ambiguous-side",
                         {"M4_FACE_AMBIGUOUS"})
    unsupported = query(target, "the nicest face")
    assert unsupported["selection_status"] == "unsupported_selector", unsupported
    conflicting = query(target, "positive X side positive Z side")
    assert conflicting["selection_status"] == "unsupported_selector", conflicting
    z_alias = query(target, "+Z")
    assert z_alias["selection_status"] == "ambiguous" and z_alias["candidate_count"] == 2, z_alias
    none = query(target, "positive X side at x=999 mm")
    assert none["selection_status"] == "none" and none["candidate_count"] == 0, none
    located = unique(target, "positive X side at x=310 mm")
    assert math.isclose(float(located["plane_origin_mm"][0]), 310, abs_tol=1e-5), located
    chinese = unique(target, "最外侧正X面")
    assert math.isclose(float(chinese["plane_origin_mm"][0]), 360, abs_tol=1e-5), chinese
    negative = unique(target, "outermost negative X side")
    assert math.isclose(float(negative["plane_origin_mm"][0]), 300, abs_tol=1e-5), negative
    assert negative["normal_world"] == [-1, 0, 0], negative
    negative_chinese = unique(target, "最外侧负X面")
    assert math.isclose(float(negative_chinese["plane_origin_mm"][0]), 300, abs_tol=1e-5), negative_chinese
    global_query = bench.call("nx_query_faces", {**bench.binding, "revision_id": bench.inspect()["revision_id"],
                                                "selector": "outermost positive X side"})
    assert global_query["selection_status"] == "unique", global_query
    global_face = global_query["candidates"][0]
    global_fields = {"support_face_selector": "outermost positive X side", "geometries": [
        {"id": "global-face-plan", "type": "circle", "center_mm": local(global_face, [360, 20, 3]),
         "diameter_mm": 2}]}
    global_before = bench.inspect()
    global_plan = bench.operation("nx_prepare_sketch", {"revision_id": global_before["revision_id"], **global_fields})
    global_after = bench.inspect()
    assert global_after["features"] == global_before["features"]
    assert bodies_match(global_after["bodies"], global_before["bodies"])
    bench.report["global_face_preparation"] = {"query": global_query, "plan": global_plan, "model_unchanged": True}

    side_sketch = face_circle(target, "outermost positive X side", [360, 20, 3], 2, "side-hole")
    cut = bench.extrude(side_sketch, "subtract", "negative", "side-through-hole", target=target)
    target = cut["body_id"]
    bench.check_volume(cut, 24000 - 60 * math.pi, "side-through-hole")
    before_move = bench.call("nx_inspect_sketch", {**bench.binding, "sketch_id": side_sketch})
    edit = bench.apply("nx_prepare_operation", bench.edit_fields(base_sketch, "HorizontalDim", 60, 70),
                       "upstream-width-moves-support")
    after_move = bench.call("nx_inspect_sketch", {**bench.binding, "sketch_id": side_sketch})
    for result, expected_x in ((before_move, 360), (after_move, 370)):
        assert result["support_association"]["native"] is True, result
        assert math.isclose(float(result["support_frame"]["origin_mm"][0]), expected_x, abs_tol=1e-5), result
        check_circle(bench, result, [expected_x, 20, 3], 1, f"side-circle-x-{expected_x}")
    live = bench.inspect()
    body = next(b for b in live["bodies"] if b["journal_id"] == target)
    actual_volume = float(body["volume_mm3"])
    expected_volume = 26400 - 70 * math.pi
    assert math.isclose(actual_volume, expected_volume, rel_tol=1e-8, abs_tol=1e-5), body
    bench.report.setdefault("volume_checks", []).append(
        {"label": "upstream-width-moves-support", "body_id": target,
         "actual_mm3": actual_volume, "expected_mm3": expected_volume})
    bench.report["association_motion"] = {"before": before_move, "after": after_move,
                                           "expected_support_x_mm": [360, 370], "execution": edit}
    bench.reject_prepare("nx_prepare_operation", bench.edit_fields(side_sketch, "DiameterDim", 2, 3),
                         "cut-profile-edit-unsupported", {"M4_SUBTRACT_CONTOUR_EDIT_UNSUPPORTED"})

    # Reducing width below the upright changes which +X face is outermost.
    # Either prepare rejects with the existing history intact, or execution
    # rolls the actual NX change back. This is not counted as successful edit.
    for dimension, old_value, value, label in (("HorizontalDim", 70, 5, "topology_failure"),
                                              ("VerticalDim", 40, 20.5, "domain_failure")):
        before_invalid = bench.inspect()
        invalid_fields = bench.edit_fields(base_sketch, dimension, old_value, value)
        prepared = bench.operation("nx_prepare_operation", {"revision_id": before_invalid["revision_id"],
                                                            **invalid_fields}, False)
        if prepared["state"] == "SUCCEEDED":
            invalid = bench.operation("nx_execute_plan", {"plan_id": prepared["result"]["plan"]["plan_id"]}, False)
            assert invalid["state"] == "FAILED", invalid
            assert invalid["result"]["mutation_state"] in ("ROLLED_BACK", "UNCHANGED"), invalid
        else:
            invalid = prepared
            assert invalid["state"] == "FAILED", invalid
        after_invalid = bench.inspect()
        assert after_invalid["features"] == before_invalid["features"]
        assert after_invalid["expressions"] == before_invalid["expressions"]
        assert bodies_match(after_invalid["bodies"], before_invalid["bodies"])
        restored_sketch = bench.call("nx_inspect_sketch", {**bench.binding, "sketch_id": side_sketch})
        assert all(restored_sketch["support_association"][key] is True for key in
                   ("native", "selector_unique", "in_face_domain", "current_face_matches")), restored_sketch
        check_circle(bench, restored_sketch, [370, 20, 3], 1, label + "-restored-circle")
        bench.report[label] = {"outcome": invalid, "history_restored": True, "bodies_restored": True,
                                "expressions_restored": True, "restored_sketch": restored_sketch}

    # A second body has a concave top face, then a genuine internal circular
    # loop. Missing corner and existing void are inside its bounding box.
    lower_sketch = bench.sketch(rect(50, 20), [500, 0, 0], "domain-base")
    lower = bench.extrude(lower_sketch, "create", "positive", "domain-base-solid", depth=6)
    wing_sketch = bench.sketch(rect(20, 30), [500, 20, 0], "domain-wing")
    concave = bench.extrude(wing_sketch, "unite", "positive", "concave-domain-solid", depth=6,
                            target=lower["body_id"])
    domain_body = concave["body_id"]
    bench.check_volume(concave, 9600, "concave-domain-solid")
    face = unique(domain_body, "top")
    for center, diameter, label in (([540, 40, 6], 4, "concave-gap"),
                                    ([549, 10, 6], 4, "crosses-face-edge")):
        fields = face_fields(domain_body, "top", [{"id": label, "type": "circle",
                             "center_mm": local(face, center), "diameter_mm": diameter}])
        bench.reject_prepare("nx_prepare_sketch", fields, label, {"M4_PROFILE_OUTSIDE_FACE"})
    hole = face_circle(domain_body, "top", [510, 10, 6], 6, "top-hole")
    holed = bench.extrude(hole, "subtract", "negative", "top-through-hole", target=domain_body)
    domain_body = holed["body_id"]
    bench.check_volume(holed, 9600 - 54 * math.pi, "top-through-hole")
    face = unique(domain_body, "top")
    assert face["loop_count"] >= 2, face
    inner = face_fields(domain_body, "top", [{"id": "in-existing-hole", "type": "circle",
                       "center_mm": local(face, [510, 10, 6]), "diameter_mm": 2}])
    bench.reject_prepare("nx_prepare_sketch", inner, "inside-hole", {"M4_PROFILE_OUTSIDE_FACE"})
    corner = local(face, [505, 5, 6])
    enclosing = face_fields(domain_body, "top", [{"id": "encloses-hole", "type": "rectangle",
                            "corner_mm": corner, "width_mm": 10, "height_mm": 10}])
    bench.reject_prepare("nx_prepare_sketch", enclosing, "encloses-hole", {"M4_PROFILE_OUTSIDE_FACE"})
    corners = [local(face, point) for point in ([505, 5, 6], [515, 5, 6], [515, 15, 6], [505, 15, 6])]
    four_lines = face_fields(domain_body, "top", [{"id": f"enclosing-line-{i}", "type": "line",
                          "start_mm": corners[i], "end_mm": corners[(i + 1) % 4]} for i in range(4)])
    bench.reject_prepare("nx_prepare_sketch", four_lines, "four-lines-enclose-hole", {"M4_PROFILE_OUTSIDE_FACE"})
    branch_start = local(face, [505, 5, 6])
    branch_end = local(face, [503, 3, 6])
    branched = face_fields(domain_body, "top", [{"id": "branch-first", "type": "line",
                          "start_mm": branch_start, "end_mm": branch_end}, *four_lines["geometries"]])
    bench.reject_prepare("nx_prepare_sketch", branched, "branched-lines-enclose-hole", {"M4_PROFILE_OUTSIDE_FACE"})
    live = bench.inspect()
    original_ids = {b["journal_id"] for b in bench.report["before"]["bodies"]}
    assert bodies_match(bench.report["before"]["bodies"], [b for b in live["bodies"]
                                                         if b["journal_id"] in original_ids])
    bench.report["original_bodies_unchanged"] = True
    bench.report["face_sketch_ids"] = [side_sketch, hole]
    bench.report["resume_model"] = {"base_sketch_id": base_sketch, "side_sketch_id": side_sketch,
                                    "bracket_body_id": target, "width_mm": 70}


def run_fresh_session_cases(bench, prior_report):
    prior = json.loads(prior_report.read_text(encoding="utf-8"))
    assert prior["result"] == "PASS", "fresh session probe requires a successful saved M4 run"
    saved_path = Path(prior["save"]["result"]["saved_path"])
    assert bench.source.resolve() == saved_path.resolve(), "launch a fresh NX on the prior saved model"
    assert (bench.launch["pid"], bench.launch["process_start_utc"]) != (
        prior["nx_pid"], prior["nx_process_start_utc"]), "a new NX process is required"
    assert bench.report["before"]["load_instance_id"] != prior["before"]["load_instance_id"], "a new bridge is required"
    model = prior["resume_model"]
    before = bench.call("nx_inspect_sketch", {**bench.binding, "sketch_id": model["side_sketch_id"]})
    assert before["support_association"]["native"] is True, before
    assert math.isclose(float(before["support_frame"]["origin_mm"][0]), 370, abs_tol=1e-5), before
    check_circle(bench, before, [370, 20, 3], 1, "fresh-process-before-circle")
    executed = bench.apply("nx_prepare_operation",
                           bench.edit_fields(model["base_sketch_id"], "HorizontalDim", 70, 74),
                           "fresh-process-upstream-edit")
    after = bench.call("nx_inspect_sketch", {**bench.binding, "sketch_id": model["side_sketch_id"]})
    assert after["support_association"]["native"] is True, after
    assert math.isclose(float(after["support_frame"]["origin_mm"][0]), 374, abs_tol=1e-5), after
    check_circle(bench, after, [374, 20, 3], 1, "fresh-process-after-circle")
    live = bench.inspect()
    body = next(b for b in live["bodies"] if b["journal_id"] == model["bracket_body_id"])
    actual, expected = float(body["volume_mm3"]), 74 * 40 * 6 + 10 * 40 * 24 - 74 * math.pi
    assert math.isclose(actual, expected, rel_tol=1e-8, abs_tol=1e-5), body
    bench.report["volume_checks"] = [{"label": "fresh-process-upstream-edit", "actual_mm3": actual,
                                      "expected_mm3": expected, "body_id": model["bracket_body_id"]}]
    other_ids = {b["journal_id"] for b in bench.report["before"]["bodies"]} - {model["bracket_body_id"]}
    assert bodies_match([b for b in bench.report["before"]["bodies"] if b["journal_id"] in other_ids],
                        [b for b in live["bodies"] if b["journal_id"] in other_ids])
    bench.report["non_target_bodies_unchanged"] = True
    bench.report["fresh_process_association"] = {"before": before, "after": after, "execution": executed,
                                                 "prior_report": str(prior_report.resolve())}
    bench.report["face_sketch_ids"] = prior["face_sketch_ids"]
    bench.report["resume_model"] = {**model, "width_mm": 74}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--launch-report", type=Path, required=True)
    parser.add_argument("--window-report", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--resume-report", type=Path,
                        help="test persisted M4 association in a new NX process using a prior saved model")
    args = parser.parse_args()
    if args.output.exists():
        parser.error("evidence already exists")
    launch = json.loads(args.launch_report.read_text(encoding="utf-8"))
    if launch["test_stage"] != "M4":
        parser.error("registered M4 session required")
    bench = Bench(launch, json.loads(args.window_report.read_text(encoding="utf-8")), args.output)
    try:
        bench.start()
        if args.resume_report:
            run_fresh_session_cases(bench, args.resume_report)
        else:
            run_cases(bench)
        bench.save_and_return()
        bench.report["result"] = "PASS"
    except Exception as exc:
        bench.report["failure"] = f"{type(exc).__name__}: {exc}"
    finally:
        bench.finish()
    summary = {k: bench.report[k] for k in ("result", "volume_checks", "source_bytes_unchanged",
                                           "input_bytes_unchanged") if k in bench.report}
    if "failure" in bench.report:
        summary["failure"] = bench.report["failure"][:1200]
    summary["evidence_path"] = str(args.output.resolve())
    print(json.dumps(summary, ensure_ascii=False))
    return 0 if bench.report["result"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
