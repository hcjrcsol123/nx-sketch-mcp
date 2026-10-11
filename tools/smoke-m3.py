"""M3 real NX/stdio replay on a registered disposable independent copy."""
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


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--launch-report", type=Path, required=True)
    parser.add_argument("--window-report", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.output.exists():
        parser.error("evidence already exists")
    launch = json.loads(args.launch_report.read_text(encoding="utf-8"))
    if launch["test_stage"] != "M3":
        parser.error("registered M3 session required")
    source = Path(launch["source_part"])
    input_path = Path(launch["copied_part"])
    source_before, input_before = source.read_bytes(), input_path.read_bytes()
    saved_path = input_path.with_name("m3-saved.prt")
    if saved_path.exists():
        parser.error("use a new session and absent save target")
    report = {"started_utc": datetime.now(timezone.utc).isoformat(), "result": "FAIL",
              "evidence_level": "real NX API through local MCP stdio; single representative part",
              "nx_pid": launch["pid"], "input_path": str(input_path), "steps": [], "cases": []}
    client = StdioMcpProcess([sys.executable, "-B", str(PROJECT / "tools/mcp_stdio.py"),
                             "--config", launch["config_path"]], cwd=str(PROJECT),
                            env={**os.environ, "PYTHONUTF8": "1"})
    binding = None
    sequence = 0

    def call(name, arguments):
        response = client.request("tools/call", {"name": name, "arguments": arguments}, timeout=130)
        report["steps"].append({"tool": name, "arguments": arguments, "response": response})
        result = response.get("result", {})
        payload = result.get("structuredContent", {})
        if result.get("isError") is not False:
            raise RuntimeError(f"{name}: {payload or response}")
        return payload

    def terminal(operation):
        deadline = time.monotonic() + 125
        while operation["state"] in ("QUEUED", "RUNNING"):
            if time.monotonic() >= deadline:
                raise TimeoutError(str(operation))
            time.sleep(0.1)
            operation = call("nx_get_operation", {"operation_id": operation["operation_id"]})
        return operation

    def operation(name, fields, expect_success=True):
        nonlocal sequence
        sequence += 1
        op_id = f"m3-{sequence}-{name}"
        outcome = terminal(call(name, {**binding, "operation_id": op_id, **fields}))
        if expect_success:
            assert outcome["state"] == "SUCCEEDED", outcome
        return outcome

    def inspect():
        return call("nx_inspect_part", binding)

    def apply(name, fields, label):
        part = inspect()
        prepared = operation(name, {"revision_id": part["revision_id"], **fields})
        plan = prepared["result"]["plan"]
        executed = operation("nx_execute_plan", {"plan_id": plan["plan_id"]})
        verified = operation("nx_verify_part", {"execute_operation_id": executed["operation_id"]})
        assert verified["result"]["verified"] is True, verified
        checks = verified["result"]["checks"]
        assert checks and all(c["status"] == "VERIFIED" for c in checks.values()), checks
        case = {"label": label, "plan": plan, "execution": executed, "verification": verified}
        report["cases"].append(case)
        return executed["result"]

    def sketch(shape, origin, label, plane="XY"):
        result = apply("nx_prepare_sketch", {"plane": plane, "origin_mm": origin,
                       "geometries": [{"id": label, **shape}]}, label)
        return result["sketch_id"]

    def edit(sketch_id, dimension_type, value, label, current_value=None):
        part = inspect()
        s = next(s for s in part["sketches"] if s["journal_id"] == sketch_id)
        constraints = [c for c in s["constraints"] if c.get("is_driving") is True
                       and c["constraint_type"] == dimension_type]
        if current_value is not None:
            expressions = {e["name"]: float(e["value"]) for e in part["expressions"]}
            constraints = [c for c in constraints if math.isclose(
                expressions[c["expression_name"]], current_value, abs_tol=1e-6)]
        assert len(constraints) == 1, constraints
        return apply("nx_prepare_operation", {"sketch_id": sketch_id,
                     "constraint_id": constraints[0]["journal_id"], "value": str(value), "unit": "mm"}, label)

    def extrude(sketch_id, mode, direction, extent, label, depth=None, target=None):
        fields = {"sketch_id": sketch_id, "mode": mode, "direction": direction, "extent": extent}
        if depth is not None:
            fields["depth_mm"] = depth
        if target is not None:
            fields["target_body_id"] = target
        return apply("nx_prepare_extrude", fields, label)

    def volume(result, expected, label):
        # Measurement location is fixed by the M3 implementation contract below.
        measurements = result["body_inventory"]
        body_id = result.get("body_id")
        entry = next(b for b in measurements if b["journal_id"] == body_id)
        actual = float(entry["volume_mm3"])
        assert math.isclose(actual, expected, rel_tol=1e-8, abs_tol=1e-5), (label, actual, expected)
        report.setdefault("volume_checks", []).append({"label": label, "actual_mm3": actual,
                                                       "expected_mm3": expected, "body_id": body_id})

    def bounds(result, expected):
        actual = [float(v) for v in result["bbox_mm"]]
        assert len(actual) == 6 and all(math.isclose(a, e, abs_tol=1e-5)
                                      for a, e in zip(actual, expected)), (actual, expected)

    def bodies_match(left, right):
        left_by_id = {b["journal_id"]: b for b in left}
        right_by_id = {b["journal_id"]: b for b in right}
        if left_by_id.keys() != right_by_id.keys():
            return False
        metrics = {"volume_mm3", "bbox_mm"}
        for body_id, a in left_by_id.items():
            b = right_by_id[body_id]
            if {k: v for k, v in a.items() if k not in metrics} != {
                k: v for k, v in b.items() if k not in metrics}:
                return False
            if not math.isclose(float(a["volume_mm3"]), float(b["volume_mm3"]),
                                rel_tol=1e-9, abs_tol=1e-7):
                return False
            if not all(math.isclose(float(x), float(y), rel_tol=1e-9, abs_tol=1e-7)
                       for x, y in zip(a["bbox_mm"], b["bbox_mm"])):
                return False
        return True

    try:
        response = client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                                  "clientInfo": {"name": "m3-nx-smoke", "version": "1"}})
        assert response["result"]["protocolVersion"] == "2025-11-25"
        client.notification("notifications/initialized")
        listing = client.request("tools/list")
        report["tools"] = [t["name"] for t in listing["result"]["tools"]]
        assert {"nx_prepare_sketch", "nx_prepare_extrude"} <= set(report["tools"])
        status = call("nx_bridge_status", {})
        window = json.loads(args.window_report.read_text(encoding="utf-8"))
        assert status["pid"] == launch["pid"] == window["pid"]
        assert status["ownership_state"] == "HUMAN_OWNED"
        assert status["main_thread_id"] == window["nx_window_thread_id"]
        ping = call("nx_bridge_ping", {"nonce": "M3 native modeling"})
        assert ping["on_main_thread"] and ping["callback_thread_id"] == window["nx_window_thread_id"]
        owned = call("nx_takeover", {"expected_pid": launch["pid"], "expected_part_path": str(input_path),
                                    "artifact_scope": "saved_independent_copy", "authorize": True})
        binding = {"ownership_id": owned["ownership_id"], "binding_epoch": owned["binding_epoch"]}
        report["capabilities"] = call("nx_get_capabilities", {})
        report["before"] = inspect()

        line = sketch({"type": "line", "start_mm": [0, 0], "end_mm": [25, 0]}, [300, -100, 0], "line")
        edit(line, "HorizontalDim", 31, "edit-original-line")
        vertical = sketch({"type": "line", "start_mm": [5, 3], "end_mm": [5, 21]},
                          [300, -150, 0], "vertical-line")
        edit(vertical, "VerticalDim", 23, "edit-original-vertical-line", current_value=18)
        diagonal = sketch({"type": "line", "start_mm": [0, 0], "end_mm": [12, 9]},
                          [300, -200, 0], "diagonal-line")
        edit(diagonal, "HorizontalDim", 16, "edit-original-diagonal-projection", current_value=12)
        rect = sketch({"type": "rectangle", "corner_mm": [0, 0], "width_mm": 40, "height_mm": 30},
                      [300, 0, 0], "rectangle")
        edit(rect, "HorizontalDim", 45, "edit-original-rectangle")
        base = extrude(rect, "create", "positive", "finite", "new-solid", depth=6)
        target = base["body_id"]
        volume(base, 45 * 30 * 6, "new-solid")
        bounds(base, [300, 0, 0, 345, 30, 6])
        hole = sketch({"type": "circle", "center_mm": [0, 0], "diameter_mm": 8}, [320, 15, 6], "circle")
        edit(hole, "DiameterDim", 10, "edit-original-circle")
        blind = extrude(hole, "subtract", "negative", "finite", "blind-cut", depth=3, target=target)
        target = blind["body_id"]
        volume(blind, 8100 - 75 * math.pi, "blind-cut")
        through = extrude(hole, "subtract", "negative", "through_all", "through-cut", target=target)
        target = through["body_id"]
        volume(through, 8100 - 150 * math.pi, "through-cut")
        boss = sketch({"type": "rectangle", "corner_mm": [0, 0], "width_mm": 10, "height_mm": 8},
                      [305, 5, 6], "boss")
        union = extrude(boss, "unite", "positive", "finite", "union", depth=4, target=target)
        target = union["body_id"]
        volume(union, 8100 - 150 * math.pi + 320, "union")
        negative_circle = sketch({"type": "circle", "center_mm": [4, 3], "diameter_mm": 12},
                                 [380, 0, 0], "negative-circle")
        negative = extrude(negative_circle, "create", "negative", "finite", "negative-solid", depth=5)
        volume(negative, 180 * math.pi, "negative-solid")
        bounds(negative, [378, -3, -5, 390, 9, 0])
        for plane, origin in (("XZ", [420, 0, 0]), ("YZ", [480, 0, 0])):
            s = sketch({"type": "rectangle", "corner_mm": [0, 0], "width_mm": 18, "height_mm": 12},
                       origin, "plane-" + plane, plane=plane)
            r = extrude(s, "create", "positive", "finite", "solid-" + plane, depth=3)
            volume(r, 648, "solid-" + plane)
            bounds(r, [420, -3, 0, 438, 0, 12] if plane == "XZ" else [480, 0, 0, 483, 18, 12])

        # A disjoint subtraction gives a natural NX rejection after preparation.
        far = sketch({"type": "circle", "center_mm": [0, 0], "diameter_mm": 5}, [600, 0, 6], "disjoint")
        before_failure = inspect()
        prepared = operation("nx_prepare_extrude", {"revision_id": before_failure["revision_id"],
            "sketch_id": far, "mode": "subtract", "direction": "negative", "extent": "finite",
            "depth_mm": 3, "target_body_id": target}, expect_success=False)
        if prepared["state"] == "SUCCEEDED":
            failed = operation("nx_execute_plan", {"plan_id": prepared["result"]["plan"]["plan_id"]}, False)
            assert failed["state"] == "FAILED", failed
            assert failed["result"]["mutation_state"] in ("ROLLED_BACK", "UNCHANGED"), failed
        else:
            failed = prepared
            assert failed["state"] == "FAILED", failed
        after_failure = inspect()
        assert after_failure["features"] == before_failure["features"]
        assert bodies_match(after_failure["bodies"], before_failure["bodies"])
        report["disjoint_failure"] = failed
        report["before_save"] = after_failure
        assert len(after_failure["bodies"]) == len(report["before"]["bodies"]) + 4
        after_bodies = {b["journal_id"]: b for b in after_failure["bodies"]}
        assert bodies_match(report["before"]["bodies"],
                            [after_bodies[b["journal_id"]] for b in report["before"]["bodies"]])
        report["original_bodies_unchanged"] = True
        saved = operation("nx_save_copy", {"target_path": str(saved_path), "reopen": True})
        result = saved["result"]
        assert result["saved"] and result["reopened"], saved
        assert all(c["status"] == "VERIFIED" for c in result["checks"].values()), saved
        binding = {"ownership_id": result["new_ownership_id"], "binding_epoch": result["new_binding_epoch"]}
        report["save"] = saved
        report["after_reopen"] = inspect()
        returned = call("nx_return", binding)
        assert returned["ownership_state"] == "HUMAN_OWNED" and returned["safe_for_human"]
        binding = None
        report["return"] = returned
        report["final_status"] = call("nx_bridge_status", {})
        assert report["final_status"]["writes_halted"] is False
        report["result"] = "PASS"
    except Exception as exc:
        report["failure"] = f"{type(exc).__name__}: {exc}"
    finally:
        if binding is not None:
            try:
                report["cleanup_return"] = call("nx_return", binding)
            except Exception as exc:
                report["cleanup_failure"] = str(exc)
        client.close_stdin()
        try:
            report["stdio_exit_code"] = client.wait(timeout=5)
            assert report["stdio_exit_code"] == 0, report["stdio_exit_code"]
            report["stdout_jsonl_lines"] = assert_stdout_jsonl_only(client.all_stdout_bytes())
        except Exception as exc:
            report["shutdown_failure"] = str(exc)
            client.terminate_own_process()
            report["result"] = "FAIL"
        report["source_bytes_unchanged"] = source.read_bytes() == source_before
        report["input_bytes_unchanged"] = input_path.read_bytes() == input_before
        if not report["source_bytes_unchanged"] or not report["input_bytes_unchanged"]:
            report["result"] = "FAIL"
        report["finished_utc"] = datetime.now(timezone.utc).isoformat()
        args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    summary = {k: report[k] for k in ("result", "volume_checks", "source_bytes_unchanged",
                                     "input_bytes_unchanged") if k in report}
    if "failure" in report:
        summary["failure"] = report["failure"][:1200]
    summary["evidence_path"] = str(args.output.resolve())
    print(json.dumps(summary, ensure_ascii=False))
    return 0 if report["result"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
