"""M5 real NX replay against a native fixture already active in a dedicated session."""
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
    def __init__(self, args):
        self.args = args
        self.launch = json.loads(args.launch_report.read_text(encoding="utf-8-sig"))
        self.fixture = json.loads(args.fixture_report.read_text(encoding="utf-8-sig"))
        self.window = json.loads(args.window_report.read_text(encoding="utf-8-sig"))
        self.binding = None
        self.sequence = 0
        self.report = {"result": "FAIL", "started_utc": datetime.now(timezone.utc).isoformat(),
                       "case": args.case, "scope": args.scope, "fixture": self.fixture,
                       "evidence_level": "real NX2506 / production bridge / local MCP stdio",
                       "nx_pid": self.launch["pid"], "steps": []}
        self.files = {p: p.read_bytes() for p in args.protect_file}
        self.client = StdioMcpProcess(
            [sys.executable, "-B", str(PROJECT / "tools/mcp_stdio.py"), "--config", self.launch["config_path"]],
            cwd=str(PROJECT), env={**os.environ, "PYTHONUTF8": "1"})

    def call(self, name, fields, error=False):
        response = self.client.request("tools/call", {"name": name, "arguments": fields}, timeout=130)
        self.report["steps"].append({"tool": name, "arguments": fields, "response": response})
        result = response.get("result", {})
        assert result.get("isError") is error, response
        return result.get("structuredContent", {})

    def operation(self, name, fields, success=True):
        self.sequence += 1
        value = self.call(name, {**self.binding, "operation_id": f"m5-{self.args.output.stem}-{self.sequence}", **fields})
        deadline = time.monotonic() + 125
        while value["state"] in ("QUEUED", "RUNNING"):
            assert time.monotonic() < deadline, value
            time.sleep(0.1)
            value = self.call("nx_get_operation", {"operation_id": value["operation_id"]})
        assert value["state"] == ("SUCCEEDED" if success else "FAILED"), value
        return value

    def inspect(self):
        return self.call("nx_inspect_part", self.binding)

    def apply(self, tool, fields):
        part = self.inspect()
        prepared = self.operation(tool, {"revision_id": part["revision_id"], **fields})
        executed = self.operation("nx_execute_plan", {"plan_id": prepared["result"]["plan"]["plan_id"]})
        verified = self.operation("nx_verify_part", {"execute_operation_id": executed["operation_id"]})
        assert verified["result"]["verified"] is True, verified
        assert all(c["status"] == "VERIFIED" for c in verified["result"]["checks"].values()), verified
        self.report.setdefault("actions", []).append({"prepare": prepared, "execute": executed, "verify": verified})
        return executed["result"]

    def reject(self, tool, fields, codes):
        before = self.inspect()
        value = self.operation(tool, {"revision_id": before["revision_id"], **fields}, False)
        assert value["error"]["code"] in codes, value
        after = self.inspect()
        for key in ("features", "sketches", "expressions", "dependencies"):
            assert before[key] == after[key], key
        assert before["revision_id"] == after["revision_id"], (before, after)
        self.report.setdefault("rejections", []).append({"operation": value, "semantic_state_preserved": True})

    def run(self):
        self.client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                            "clientInfo": {"name": "m5-nx-smoke", "version": "1"}})
        self.client.notification("notifications/initialized")
        self.report["tools"] = self.client.request("tools/list")
        status = self.call("nx_bridge_status", {})
        assert status["pid"] == self.launch["pid"] == self.window["pid"]
        assert status["main_thread_id"] == self.window["nx_window_thread_id"]
        assert status["ownership_state"] == "HUMAN_OWNED"
        current = self.fixture["current_part"]
        takeover_fields = {"expected_pid": self.launch["pid"], "expected_part_path": current["path"],
                           "artifact_scope": self.args.scope, "authorize": True}
        if self.args.case in ("assembly", "external_reference"):
            rejected = self.call("nx_takeover", takeover_fields, True)
            expected_code = "ASSEMBLY_UNSUPPORTED" if self.args.case == "assembly" else "EXTERNAL_REFERENCE_UNSUPPORTED"
            assert rejected["error"]["code"] == expected_code, rejected
            self.report["takeover_rejection"] = rejected
            self.report["final_status"] = self.call("nx_bridge_status", {})
            assert self.report["final_status"]["ownership_state"] == "HUMAN_OWNED"
            return
        owned = self.call("nx_takeover", takeover_fields)
        self.report["takeover"] = owned
        self.binding = {"ownership_id": owned["ownership_id"], "binding_epoch": owned["binding_epoch"]}
        before = self.inspect()
        self.report["before"] = before
        assert before["identity"]["part_tag"] == current["tag"], (before["identity"], current)
        assert owned["artifact_kind"] == "standalone_part" and owned["artifact_scope"] == self.args.scope
        support = before["support_summary"]
        assert support["history_mode"] == ("APPEND_ONLY" if self.args.case == "no_history" else "NATIVE_SKETCH_HISTORY"), support
        assert all(value == 0 for value in support["dependencies"].values()), support
        memory = self.fixture.get("memory_edit", self.fixture.get("dirty_evidence", {}).get("memory_edit"))
        if self.args.case in ("saved_dirty", "unsaved"):
            assert current["is_modified"] and before["part"]["is_modified"], (current, before["part"])
            sentinel = next(e for e in before["expressions"] if e["name"] == "M5_USER_MEMORY_SENTINEL")
            assert math.isclose(float(sentinel["value"]), 123.456, abs_tol=1e-8), sentinel
            if memory:
                expression = next(e for e in before["expressions"] if e["name"] == memory["expression_name"])
                assert math.isclose(float(expression["value"]), float(memory["value"]), abs_tol=1e-6), expression
        if self.args.case == "readonly":
            assert current["is_read_only"] and before["part"]["is_read_only"], current
            edit = self.fixture["edit"]
            fields = {k: edit[k] for k in ("sketch_id", "constraint_id", "value", "unit")}
            fields["value"] = str(fields["value"])
            self.reject("nx_prepare_operation", fields,
                        {"READ_ONLY_PART", "UNSUPPORTED_PARAMETER"})
            self.reject("nx_prepare_sketch", {"plane": "XY", "origin_mm": [100, 0, 0],
                        "geometries": [{"id": "read-only", "type": "circle", "center_mm": [5, 5], "diameter_mm": 2}]},
                        {"READ_ONLY_PART", "UNSUPPORTED_PARAMETER"})
            self.reject("nx_prepare_extrude", {"sketch_id": edit["sketch_id"], "mode": "create", "direction": "positive",
                        "extent": "finite", "depth_mm": 3}, {"READ_ONLY_PART", "UNSUPPORTED_PARAMETER"})
        elif self.args.case == "no_history":
            assert not before["sketches"], before["sketches"]
            self.reject("nx_prepare_operation", {"sketch_id": "missing-original-sketch", "constraint_id": "missing-original-dimension",
                        "value": "20", "unit": "mm"}, {"SKETCH_NOT_FOUND", "NO_ORIGINAL_SKETCH", "UNSUPPORTED_PARAMETER"})
            body_id = before["bodies"][0]["journal_id"]
            query = self.call("nx_query_faces", {**self.binding, "revision_id": before["revision_id"],
                         "body_id": body_id, "selector": "outermost positive Z side"})
            assert query["selection_status"] == "unique", query
            face = query["candidates"][0]
            # Derive local coordinates from the native frame and bbox center.
            box = before["bodies"][0]["bbox_mm"]
            world = [(float(box[i]) + float(box[i + 3])) / 2 for i in range(3)]
            world[2] = float(box[5])
            delta = [p - o for p, o in zip(world, face["frame"]["origin_mm"])]
            center = [sum(d * axis for d, axis in zip(delta, face["frame"][key])) for key in ("x_axis_world", "y_axis_world")]
            sketch = self.apply("nx_prepare_sketch", {"support_body_id": body_id,
                    "support_face_selector": "outermost positive Z side", "geometries": [
                    {"id": "append-hole", "type": "circle", "center_mm": center, "diameter_mm": 2}]})
            assert sketch["support_association"]["native"] is True, sketch
            self.apply("nx_prepare_extrude", {"sketch_id": sketch["sketch_id"], "mode": "subtract", "direction": "negative",
                    "extent": "through_all", "target_body_id": body_id})
            after_cut = self.inspect()
            actual = float(after_cut["bodies"][0]["volume_mm3"])
            expected = float(before["bodies"][0]["volume_mm3"]) - math.pi * (float(box[5]) - float(box[2]))
            assert math.isclose(actual, expected, rel_tol=1e-8, abs_tol=1e-5), (actual, expected)
            self.report["volume_check"] = {"actual_mm3": actual, "expected_mm3": expected}
        else:
            edit = self.fixture.get("edit")
            if edit is None:
                # Select a live supported diameter for the saved-copy compatibility case.
                live = [(s, c) for s in before["sketches"] for c in s["constraints"]
                        if c.get("is_driving") is True and c["constraint_type"] == "DiameterDim"]
                assert len(live) == 1, live
                sketch, constraint = live[0]
                expression = next(e for e in before["expressions"] if e["name"] == constraint["expression_name"])
                edit = {"sketch_id": sketch["journal_id"], "constraint_id": constraint["journal_id"],
                        "value": str(float(expression["value"]) + 2), "unit": "mm"}
                self.report["live_selected_edit"] = edit
            fields = {k: edit[k] for k in ("sketch_id", "constraint_id", "value", "unit")}
            fields["value"] = str(fields["value"])
            self.apply("nx_prepare_operation", fields)
            after_edit = self.inspect()
            expressions = {e["name"]: e for e in after_edit["expressions"]}
            constraint = next(c for s in after_edit["sketches"] if s["journal_id"] == edit["sketch_id"]
                              for c in s["constraints"] if c["journal_id"] == edit["constraint_id"])
            assert constraint["is_driving"] is True
            actual_value = float(expressions[constraint["expression_name"]]["value"])
            assert math.isclose(actual_value, float(edit["value"]), abs_tol=1e-6), actual_value
            if "expected_volume_mm3" in self.fixture:
                actual = sum(float(b["volume_mm3"]) for b in after_edit["bodies"])
                expected = float(self.fixture["expected_volume_mm3"])
                assert math.isclose(actual, expected, rel_tol=1e-8, abs_tol=1e-5), (actual, expected)
                self.report["volume_check"] = {"actual_mm3": actual, "expected_mm3": expected}
        after = self.inspect()
        self.report["after"] = after
        if self.args.case in ("saved_dirty", "unsaved"):
            sentinel = next(e for e in after["expressions"] if e["name"] == "M5_USER_MEMORY_SENTINEL")
            assert math.isclose(float(sentinel["value"]), 123.456, abs_tol=1e-8)
            assert after["part"]["is_modified"]
            if memory:
                expression = next(e for e in after["expressions"] if e["name"] == memory["expression_name"])
                assert math.isclose(float(expression["value"]), float(memory["value"]), abs_tol=1e-6), expression
        if self.args.scope == "current_work_part":
            target = self.args.output.with_suffix(".prt")
            assert not target.exists(), target
            rejected = self.operation("nx_save_copy", {"target_path": str(target.resolve()), "reopen": True}, False)
            assert rejected["error"]["code"] in {"CURRENT_WORK_PART_SAVE_UNSUPPORTED", "READ_ONLY_PART"}, rejected
            assert not target.exists(), target
            final = self.inspect()
            assert final["identity"]["part_tag"] == current["tag"]
            assert final["identity"]["part_path"] == current["path"]
            assert final["revision_id"] == after["revision_id"]
            self.report["save_rejected_without_file_or_identity_change"] = rejected
        elif self.args.save_target:
            saved = self.operation("nx_save_copy", {"target_path": str(self.args.save_target.resolve()), "reopen": True})
            result = saved["result"]
            assert result["saved"] and result["reopened"]
            assert all(c["status"] == "VERIFIED" for c in result["checks"].values())
            self.report["save"] = saved
            self.binding = {"ownership_id": result["new_ownership_id"], "binding_epoch": result["new_binding_epoch"]}
            self.report["after_reopen"] = self.inspect()
        returned = self.call("nx_return", self.binding)
        assert returned["ownership_state"] == "HUMAN_OWNED" and returned["safe_for_human"]
        self.binding = None
        self.report["return"] = returned
        self.report["final_status"] = self.call("nx_bridge_status", {})
        assert self.report["final_status"]["writes_halted"] is False

    def finish(self):
        if self.binding:
            try:
                self.report["cleanup_return"] = self.call("nx_return", self.binding)
            except Exception as ex:
                self.report["cleanup_error"] = str(ex)
        self.client.close_stdin()
        try:
            self.report["stdio_exit_code"] = self.client.wait(5)
            assert self.report["stdio_exit_code"] == 0
            self.report["stdout_jsonl_lines"] = assert_stdout_jsonl_only(self.client.all_stdout_bytes())
        except Exception as ex:
            self.report["shutdown_error"] = str(ex)
            self.report["result"] = "FAIL"
            self.client.terminate_own_process()
        self.report["protected_files"] = [{"path": str(p), "bytes_unchanged": p.read_bytes() == data} for p, data in self.files.items()]
        if not all(x["bytes_unchanged"] for x in self.report["protected_files"]):
            self.report["result"] = "FAIL"
        self.report["finished_utc"] = datetime.now(timezone.utc).isoformat()
        self.args.output.write_text(json.dumps(self.report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("launch-report", "window-report", "fixture-report", "output"):
        parser.add_argument("--" + name, required=True, type=Path)
    parser.add_argument("--case", required=True, choices=("history", "no_history", "saved_dirty", "unsaved", "readonly", "assembly", "external_reference"))
    parser.add_argument("--scope", default="current_work_part", choices=("current_work_part", "saved_independent_copy"))
    parser.add_argument("--protect-file", action="append", type=Path, default=[])
    parser.add_argument("--save-target", type=Path)
    args = parser.parse_args()
    if args.output.exists():
        parser.error("output must be absent")
    bench = Bench(args)
    try:
        bench.run()
        bench.report["result"] = "PASS"
    except Exception as ex:
        bench.report["failure"] = repr(ex)
    finally:
        bench.finish()
    print(json.dumps({k: bench.report.get(k) for k in ("result", "case", "failure", "protected_files")}, ensure_ascii=False))
    return 0 if bench.report["result"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
