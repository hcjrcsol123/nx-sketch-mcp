"""Exercise M2-5/6 via stdio on a recorded, dedicated NX copied part.

Only the recorded copy's in-memory Driving dimensions are changed. No save,
reopen, forced NX termination, or source-part modification is performed.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import sys
import time

PROJECT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT / "tests" / "m2"))
from stdio_client import StdioMcpProcess, assert_stdout_jsonl_only


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--launch-report", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--window-report", type=Path, required=True)
    args = parser.parse_args()
    if args.output.exists():
        parser.error("refusing to overwrite evidence")
    launch = json.loads(args.launch_report.read_text(encoding="utf-8"))
    if launch.get("test_stage") != "M2-5-M2-6":
        parser.error("a dedicated M2-5-M2-6 launch report is required")
    report = {"captured_utc": datetime.now(timezone.utc).isoformat(),
              "nx_pid": launch["pid"], "copy_path": launch["copied_part"],
              "steps": [], "result": "FAIL", "saved": False}
    client = StdioMcpProcess(
        [sys.executable, "-B", str(PROJECT / "tools" / "mcp_stdio.py"),
         "--config", launch["config_path"]], cwd=str(PROJECT),
        env={**os.environ, "PYTHONUTF8": "1"})
    binding = None

    def call(name: str, arguments: dict, error: str | None = None) -> dict:
        response = client.request("tools/call", {"name": name, "arguments": arguments}, timeout=130)
        result = response.get("result", {})
        payload = result.get("structuredContent", {})
        report["steps"].append({"tool": name, "arguments": arguments, "response": response})
        if error:
            assert result.get("isError") is True and payload.get("error", {}).get("code") == error, payload
        else:
            assert result.get("isError") is False, payload or response
        return payload

    def terminal(operation: dict) -> dict:
        deadline = time.monotonic() + 120
        while operation["state"] in ("QUEUED", "RUNNING"):
            if time.monotonic() >= deadline:
                raise TimeoutError(f"operation {operation['operation_id']} is still {operation['state']}")
            time.sleep(0.1)
            operation = call("nx_get_operation", {"operation_id": operation["operation_id"]})
        return operation

    def find_target(part: dict, name: str) -> tuple[str, str]:
        matches = [(s["journal_id"], c["journal_id"])
                   for s in part["sketches"] for c in s["constraints"]
                   if c.get("expression_name") == name and c.get("is_driving") is True]
        assert len(matches) == 1, f"expected one actual Driving association for {name}: {matches}"
        return matches[0]

    try:
        initialized = client.request("initialize", {"protocolVersion": "2025-11-25",
            "capabilities": {}, "clientInfo": {"name": "m2-parameter-smoke", "version": "1"}})
        assert initialized.get("result", {}).get("protocolVersion") == "2025-11-25"
        client.notification("notifications/initialized")
        listing = client.request("tools/list")
        report["tools"] = sorted(item["name"] for item in listing["result"]["tools"])
        assert set(report["tools"]) >= {"nx_get_capabilities", "nx_bridge_status", "nx_bridge_ping",
            "nx_takeover", "nx_return", "nx_get_session", "nx_inspect_part", "nx_inspect_sketch",
            "nx_get_operation", "nx_cancel_operation", "nx_prepare_operation", "nx_execute_plan"}
        status = call("nx_bridge_status", {})
        assert status["pid"] == launch["pid"] and status["ownership_state"] == "HUMAN_OWNED"
        window = json.loads(args.window_report.read_text(encoding="utf-8"))
        assert window["pid"] == launch["pid"]
        assert status["main_thread_id"] == window["nx_window_thread_id"]
        ping = call("nx_bridge_ping", {"nonce": "M2-5/6 参数派发"})
        assert ping["on_main_thread"] is True
        assert ping["callback_thread_id"] == window["nx_window_thread_id"]
        owned = call("nx_takeover", {"expected_pid": launch["pid"],
            "expected_part_path": launch["copied_part"],
            "artifact_scope": "saved_independent_copy", "authorize": True})
        binding = {"ownership_id": owned["ownership_id"], "binding_epoch": owned["binding_epoch"]}
        report["takeover"] = owned
        report["owned_capabilities"] = call("nx_get_capabilities", {})
        part = call("nx_inspect_part", binding)
        report["before"] = part
        sketch_id, constraint_id = find_target(part, "p12")
        rejection_base = {**binding, "revision_id": part["revision_id"], "sketch_id": sketch_id,
                          "constraint_id": constraint_id, "value": "34", "unit": "mm"}
        for operation_id, overrides, error_code in (
            ("nx-reject-unit", {"unit": "m"}, "UNIT_MISMATCH"),
            ("nx-reject-value", {"value": "-1"}, "INVALID_VALUE"),
        ):
            rejected = terminal(call("nx_prepare_operation", {
                **rejection_base, "operation_id": operation_id, **overrides}))
            assert rejected["state"] == "FAILED" and rejected["error"]["code"] == error_code, rejected
            assert rejected["writes_halted"] is False
        non_driving = next((s["journal_id"], c["journal_id"])
            for s in part["sketches"] for c in s["constraints"]
            if c.get("class") == "DIMENSIONAL" and c.get("is_driving") is False)
        rejected = terminal(call("nx_prepare_operation", {
            **rejection_base, "operation_id": "nx-reject-non-driving",
            "sketch_id": non_driving[0], "constraint_id": non_driving[1]}))
        assert rejected["state"] == "FAILED" and rejected["error"]["code"] == "UNSUPPORTED_PARAMETER", rejected
        part = call("nx_inspect_part", binding)
        assert part["expressions"] == report["before"]["expressions"]
        assert part["sketches"] == report["before"]["sketches"]
        report["rejections"] = {"unit_mismatch": True, "non_positive_value": True,
            "non_driving_dimension": True, "expressions_and_sketches_unchanged": True}
        changes = []
        for index, (name, value) in enumerate((("p12", "34"), ("p11", "73")), 1):
            sketch_id, constraint_id = find_target(part, name)
            prepare_args = {**binding, "operation_id": f"nx-prepare-{index}",
                "revision_id": part["revision_id"], "sketch_id": sketch_id,
                "constraint_id": constraint_id, "value": value, "unit": "mm"}
            prepared = terminal(call("nx_prepare_operation", prepare_args))
            assert prepared["state"] == "SUCCEEDED", prepared
            plan = prepared["result"]["plan"]
            assert plan["target"]["expression_name"] == name
            assert plan["requested"]["value"] == value and plan["requested"]["unit"] == "mm"
            repeated = call("nx_prepare_operation", {**prepare_args, "value": value + ".0"})
            assert repeated["result"]["plan"] == plan
            call("nx_prepare_operation", {**prepare_args, "value": str(int(value) + 1)}, "OPERATION_ID_CONFLICT")
            execution_args = {**binding, "operation_id": f"nx-execute-{index}", "plan_id": plan["plan_id"]}
            execution = terminal(call("nx_execute_plan", execution_args))
            assert execution["state"] == "SUCCEEDED", execution
            evidence = execution["result"]
            assert evidence["mutation_state"] == "APPLIED" and evidence["changed"] is True
            assert evidence["readback_verified"] is True and evidence["update_error_count"] == 0
            assert evidence["geometry_before"] and evidence["geometry_after"]
            assert evidence["geometry_before"] != evidence["geometry_after"]
            repeated_execution = call("nx_execute_plan", execution_args)
            assert repeated_execution["state"] == "SUCCEEDED"
            assert repeated_execution["result"] == execution["result"]
            call("nx_execute_plan", {**execution_args, "plan_id": "different-plan"}, "OPERATION_ID_CONFLICT")
            cancelled_terminal = call("nx_cancel_operation", {"operation_id": execution["operation_id"]})
            assert cancelled_terminal["state"] == "SUCCEEDED"
            part = call("nx_inspect_part", binding)
            expression = next(e for e in part["expressions"] if e["name"] == name)
            assert float(expression["value"]) == float(value)
            assert part["revision_id"] != plan["revision_id"]
            changes.append({"expression": name, "requested": value, "plan": plan,
                            "execution": execution, "readback": expression,
                            "revision_after": part["revision_id"]})
        report["changes"] = changes
        report["after"] = part
        old_binding = binding
        returned = call("nx_return", binding)
        assert returned["ownership_state"] == "HUMAN_OWNED" and returned["safe_for_human"] is True
        binding = None
        report["return"] = returned
        call("nx_execute_plan", {**old_binding, "operation_id": "nx-stale-execute",
                                 "plan_id": changes[-1]["plan"]["plan_id"]}, "AUTHORIZATION_REQUIRED")
        status = call("nx_bridge_status", {})
        assert status["ownership_state"] == "HUMAN_OWNED" and status["writes_halted"] is False
        report["summary"] = {"changes": [{"expression": c["expression"],
            "before": c["plan"]["before"]["value"], "after": c["readback"]["value"],
            "unit": c["readback"]["unit"]} for c in changes],
            "final_state": status["ownership_state"], "main_thread_id": status["main_thread_id"]}
        report["result"] = "PASS"
    except Exception as exc:
        report["failure"] = f"{type(exc).__name__}: {exc}"
    finally:
        if binding is not None:
            try:
                report["cleanup_return"] = call("nx_return", binding)
            except Exception as exc:
                report["cleanup_failure"] = f"{type(exc).__name__}: {exc}"
        client.close_stdin()
        try:
            report["stdio_exit_code"] = client.wait(timeout=5)
            report["stdout_jsonl_lines"] = assert_stdout_jsonl_only(client.all_stdout_bytes())
            assert report["stdio_exit_code"] == 0
        except Exception as exc:
            report["shutdown_failure"] = f"{type(exc).__name__}: {exc}"
            report["result"] = "FAIL"
            client.terminate_own_process()
        args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({k: report[k] for k in ("result", "summary", "failure", "cleanup_failure", "shutdown_failure")
                      if k in report}, ensure_ascii=False))
    return 0 if report["result"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
