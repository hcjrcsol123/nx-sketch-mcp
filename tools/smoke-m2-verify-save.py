"""Exercise M2-7 on an already registered, dedicated NX session.

The script never launches, closes, or restarts NX. It requires explicit takeover
and edit/save confirmations, edits the supplied Driving dimension, verifies the
live part, saves to a new .prt path with reopen enabled, checks the reopened part,
and returns ownership after a known terminal outcome. Unknown outcomes stop the
script without retrying a write or returning ownership.
"""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
from decimal import Decimal, InvalidOperation
import json
import os
from pathlib import Path
import sys
import time
import uuid

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT / "tests" / "m2"))
from stdio_client import StdioMcpProcess, assert_stdout_jsonl_only

PROTOCOL_VERSION = "2025-11-25"


TERMINAL_STATES = {"SUCCEEDED", "FAILED", "CANCELLED", "UNKNOWN"}
CHECK_NAMES = ("expression", "geometry", "history", "associations")
UNCERTAIN_OUTCOME_CODES = {
    "UNKNOWN_RESULT", "OPERATION_OUTCOME_UNKNOWN", "BRIDGE_TIMEOUT", "SAVE_RESULT_UNKNOWN",
    "SAVE_COPY_OUTCOME_UNKNOWN", "STALE_BINDING_AFTER_SAVE",
}


class ToolFailure(RuntimeError):
    def __init__(self, tool: str, code: str, message: str):
        super().__init__(tool + " failed: " + code + ": " + message)
        self.tool = tool
        self.code = code
        self.message = message


class UnknownOutcome(ToolFailure):
    pass


def normalized_path(path: str | Path) -> str:
    return os.path.normcase(os.path.abspath(str(path)))


def decimal_value(value: object, label: str) -> Decimal:
    try:
        result = Decimal(str(value))
    except (InvalidOperation, ValueError):
        raise RuntimeError(label + " is not a decimal millimeter value")
    if not result.is_finite():
        raise RuntimeError(label + " must be finite")
    return result


def extract_structured_result(result: dict) -> dict | None:
    structured = result.get("structuredContent")
    if isinstance(structured, dict):
        return structured
    content = result.get("content")
    if isinstance(content, list):
        for item in content:
            if not isinstance(item, dict) or item.get("type") != "text" or not isinstance(item.get("text"), str):
                continue
            try:
                value = json.loads(item["text"])
            except json.JSONDecodeError:
                continue
            if isinstance(value, dict):
                return value
    return None


class SmokeRun:
    def __init__(self, client: StdioMcpProcess, timeout: float, report: dict):
        self.client = client
        self.timeout = timeout
        self.report = report

    def call(self, name: str, arguments: dict) -> dict:
        response = self.client.request("tools/call", {"name": name, "arguments": arguments}, timeout=self.timeout)
        result = response.get("result", {})
        payload = extract_structured_result(result) if isinstance(result, dict) else None
        if not isinstance(payload, dict):
            raise RuntimeError(name + " returned no structured result")
        entry = {"tool": name, "arguments": arguments, "result": payload}
        self.report["steps"].append(entry)
        error = payload.get("error")
        if result.get("isError") is True or payload.get("ok") is False or isinstance(error, dict):
            error = error if isinstance(error, dict) else {}
            raise ToolFailure(name, str(error.get("code", "TOOL_ERROR")), str(error.get("message", "")))
        return payload

    def wait_operation(self, operation_id: str, timeout: float) -> dict:
        deadline = time.monotonic() + timeout
        latest = None
        while time.monotonic() < deadline:
            latest = self.call("nx_get_operation", {"operation_id": operation_id})
            state = latest.get("state")
            if state in TERMINAL_STATES:
                return latest
            time.sleep(0.2)
        raise TimeoutError("operation did not reach a terminal state: " + operation_id)


def assert_checks(checks: object, label: str) -> dict:
    if not isinstance(checks, dict) or set(checks) != set(CHECK_NAMES):
        raise RuntimeError(label + " omitted expression, geometry, history, or association checks")
    for name in CHECK_NAMES:
        item = checks[name]
        if not isinstance(item, dict) or item.get("status") != "VERIFIED":
            raise RuntimeError(label + " check did not pass: " + name)
        if not isinstance(item.get("evidence"), dict):
            raise RuntimeError(label + " check omitted evidence: " + name)
    return checks


def operation_result(run: SmokeRun, tool_name: str, arguments: dict, timeout: float) -> dict:
    accepted = run.call(tool_name, arguments)
    if accepted.get("state") not in {"QUEUED", "RUNNING", "SUCCEEDED", "FAILED", "UNKNOWN"}:
        raise RuntimeError(tool_name + " did not return a queryable operation record")
    operation_id = arguments["operation_id"]
    operation = run.wait_operation(operation_id, timeout)
    if operation.get("state") == "UNKNOWN":
        raise UnknownOutcome(tool_name, str(operation.get("error", {}).get("code", "UNKNOWN_RESULT")),
                             "operation outcome is unknown; the bridge write gate remains in force")
    if operation.get("state") != "SUCCEEDED":
        error = operation.get("error") or {}
        raise ToolFailure(tool_name, str(error.get("code", operation.get("state", "FAILED"))),
                          str(error.get("message", "operation did not succeed")))
    return operation


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path, required=True, help="prepared MCP config for the registered NX bridge")
    parser.add_argument("--expected-nx-pid", type=int, required=True)
    parser.add_argument("--expected-part-path", type=Path, required=True, help="current dedicated independent-copy Work Part")
    parser.add_argument("--sketch-id", required=True, help="JournalIdentifier from nx_inspect_part")
    parser.add_argument("--constraint-id", required=True, help="Driving constraint JournalIdentifier")
    parser.add_argument("--value-mm", required=True, help="positive finite value to apply to the selected dimension")
    parser.add_argument("--target-path", type=Path, required=True, help="new .prt path in an existing directory")
    parser.add_argument("--output", type=Path, required=True, help="new UTF-8 evidence report path")
    parser.add_argument("--operation-timeout-seconds", type=float, default=130.0)
    parser.add_argument("--tool-timeout-seconds", type=float, default=10.0)
    parser.add_argument("--authorize-takeover", action="store_true", required=True,
                        help="confirm that this script may take ownership of the supplied NX Work Part")
    parser.add_argument("--authorize-edit-and-save-copy", action="store_true", required=True,
                        help="confirm the requested dimension edit and independent SaveAs/reopen")
    args = parser.parse_args(argv)

    if not args.config.is_absolute() or not args.config.is_file():
        parser.error("--config must be an existing absolute path")
    if args.expected_nx_pid <= 0:
        parser.error("--expected-nx-pid must be positive")
    if not args.expected_part_path.is_absolute() or not args.expected_part_path.is_file():
        parser.error("--expected-part-path must identify an existing absolute .prt file")
    if args.expected_part_path.suffix.casefold() != ".prt":
        parser.error("--expected-part-path must have a .prt extension")
    if not args.target_path.is_absolute() or args.target_path.suffix.casefold() != ".prt":
        parser.error("--target-path must be an absolute .prt path")
    if not args.target_path.parent.is_dir():
        parser.error("--target-path parent directory must already exist")
    if args.target_path.exists():
        parser.error("--target-path must not exist; save-copy never overwrites")
    if normalized_path(args.target_path) == normalized_path(args.expected_part_path):
        parser.error("--target-path must differ from the Work Part")
    if not 0.1 <= args.tool_timeout_seconds <= 120:
        parser.error("--tool-timeout-seconds must be between 0.1 and 120")
    if not 1.0 <= args.operation_timeout_seconds <= 600:
        parser.error("--operation-timeout-seconds must be between 1 and 600")
    if args.output.exists():
        parser.error("--output already exists; refusing to overwrite evidence")

    try:
        requested_value = decimal_value(args.value_mm, "--value-mm")
    except RuntimeError as exc:
        parser.error(str(exc))
    if requested_value <= 0:
        parser.error("--value-mm must be positive")

    environment = os.environ.copy()
    python_path = environment.get("PYTHONPATH", "")
    project_paths = [str(PROJECT_ROOT / "src"), str(PROJECT_ROOT)]
    if python_path:
        project_paths.append(python_path)
    environment["PYTHONPATH"] = os.pathsep.join(project_paths)
    environment["PYTHONUTF8"] = "1"
    client = StdioMcpProcess(
        [sys.executable, "-B", "-m", "nx_sketch_mcp", "--config", str(args.config)],
        cwd=str(PROJECT_ROOT), env=environment,
    )
    started = time.monotonic()
    report = {
        "captured_utc": datetime.now(timezone.utc).isoformat(),
        "result": "FAIL",
        "evidence_level": "single registered NX session API smoke; not reliability or compliance evidence",
        "mcp_config_path": str(args.config.resolve()),
        "expected_nx_pid": args.expected_nx_pid,
        "expected_part_path": str(args.expected_part_path.resolve()),
        "sketch_id": args.sketch_id,
        "constraint_id": args.constraint_id,
        "requested_value_mm": str(requested_value),
        "target_path": str(args.target_path.resolve()),
        "steps": [],
    }
    run = SmokeRun(client, args.tool_timeout_seconds, report)
    binding = None
    unknown_outcome = False
    current_step = "initialize"
    try:
        initialized = client.request("initialize", {
            "protocolVersion": PROTOCOL_VERSION,
            "capabilities": {},
            "clientInfo": {"name": "m2-7-verify-save-smoke", "version": "1"},
        }, timeout=args.tool_timeout_seconds)
        if initialized.get("result", {}).get("protocolVersion") != PROTOCOL_VERSION:
            raise RuntimeError("MCP protocol negotiation failed")
        client.notification("notifications/initialized")
        client.assert_quiet(duration=0.2)

        current_step = "nx_bridge_status"
        status = run.call("nx_bridge_status", {})
        if status.get("state") != "loaded" or status.get("pid") != args.expected_nx_pid:
            raise RuntimeError("bridge status did not identify the expected NX PID")
        if status.get("ownership_state") != "HUMAN_OWNED":
            raise RuntimeError("NX session must be HUMAN_OWNED before takeover")

        current_step = "nx_takeover"
        takeover = run.call("nx_takeover", {
            "expected_pid": args.expected_nx_pid,
            "expected_part_path": str(args.expected_part_path.resolve()),
            "artifact_scope": "saved_independent_copy",
            "authorize": True,
        })
        binding = {"ownership_id": takeover["ownership_id"], "binding_epoch": takeover["binding_epoch"]}
        if takeover.get("ownership_state") != "AI_OWNED":
            raise RuntimeError("takeover did not establish AI_OWNED state")

        current_step = "nx_get_session"
        session = run.call("nx_get_session", binding)
        if normalized_path(session.get("part_path", "")) != normalized_path(args.expected_part_path):
            raise RuntimeError("takeover Work Part path differs from the expected independent copy")
        if session.get("part_units") != "Millimeters":
            raise RuntimeError("the selected Work Part is not millimeter based")
        report["nx_build"] = session.get("nx_build")
        report["part_units"] = session.get("part_units")

        current_step = "nx_inspect_part_before"
        part_before = run.call("nx_inspect_part", binding)
        revision_id = part_before.get("revision_id")
        sketches = part_before.get("sketches", [])
        if not any(item.get("journal_id") == args.sketch_id for item in sketches if isinstance(item, dict)):
            raise RuntimeError("--sketch-id was not present in the current Work Part")
        current_step = "nx_inspect_sketch_before"
        sketch_before = run.call("nx_inspect_sketch", {**binding, "sketch_id": args.sketch_id})
        constraints = sketch_before.get("sketch", {}).get("constraints", [])
        constraint = next((item for item in constraints if isinstance(item, dict) and
                           item.get("journal_id") == args.constraint_id), None)
        if constraint is None:
            raise RuntimeError("--constraint-id was not present in the selected sketch")
        if constraint.get("is_driving") is not True or constraint.get("dimension_state") != "Driving":
            raise RuntimeError("selected constraint is not a Driving dimension")
        expression_name = constraint.get("expression_name")
        expression = next((item for item in part_before.get("expressions", []) if isinstance(item, dict) and
                           item.get("name") == expression_name), None)
        if expression is None or expression.get("unit") != "mm":
            raise RuntimeError("selected dimension is not associated with a millimeter expression")
        current_value = decimal_value(expression.get("value"), "current expression value")
        if requested_value == current_value:
            raise RuntimeError("requested value equals the current value; choose a value that exercises a real edit")
        report["before_expression_mm"] = str(current_value)
        report["revision_before"] = revision_id
        feature_count_before = len(part_before.get("features", []))
        geometry_refs_before = list(constraint.get("geometry_refs", []))

        current_step = "nx_prepare_operation"
        prepare_id = "m27-smoke-prepare-" + uuid.uuid4().hex
        prepared = operation_result(run, "nx_prepare_operation", {
            "operation_id": prepare_id, **binding, "revision_id": revision_id,
            "sketch_id": args.sketch_id, "constraint_id": args.constraint_id,
            "value": str(requested_value), "unit": "mm",
        }, args.operation_timeout_seconds)
        plan = prepared.get("result", {}).get("plan", {})
        if plan.get("target", {}).get("expression_name") != expression_name:
            raise RuntimeError("prepared plan resolved a different expression")

        current_step = "nx_execute_plan"
        execute_id = "m27-smoke-execute-" + uuid.uuid4().hex
        try:
            executed = operation_result(run, "nx_execute_plan", {
                "operation_id": execute_id, **binding, "plan_id": plan["plan_id"],
            }, args.operation_timeout_seconds)
        except ToolFailure as exc:
            if exc.code in UNCERTAIN_OUTCOME_CODES:
                unknown_outcome = True
            raise
        report["execute_operation_id"] = execute_id
        report["execute_result"] = executed.get("result")

        current_step = "nx_verify_part"
        verify_id = "m27-smoke-verify-" + uuid.uuid4().hex
        verified = operation_result(run, "nx_verify_part", {
            "operation_id": verify_id, **binding, "execute_operation_id": execute_id,
        }, args.operation_timeout_seconds)
        verification = verified.get("result", {})
        if verification.get("verified") is not True:
            raise RuntimeError("post-execution verification did not pass")
        report["verify_operation_id"] = verify_id
        report["verify_checks"] = assert_checks(verification.get("checks"), "post-execution verification")

        current_step = "nx_save_copy"
        save_id = "m27-smoke-save-" + uuid.uuid4().hex
        try:
            saved = operation_result(run, "nx_save_copy", {
                "operation_id": save_id, **binding,
                "target_path": str(args.target_path.resolve()), "reopen": True,
            }, args.operation_timeout_seconds)
        except ToolFailure as exc:
            if exc.code in UNCERTAIN_OUTCOME_CODES:
                unknown_outcome = True
            raise
        save_result = saved.get("result", {})
        new_owner = save_result.get("new_ownership_id")
        new_epoch = save_result.get("new_binding_epoch")
        if not isinstance(new_owner, str) or not new_owner or not isinstance(new_epoch, int):
            raise RuntimeError("save-copy did not return the rotated binding credentials")
        if new_owner == binding["ownership_id"] or new_epoch == binding["binding_epoch"]:
            raise RuntimeError("save-copy did not rotate both ownership id and binding epoch")
        old_binding = dict(binding)
        binding = {"ownership_id": new_owner, "binding_epoch": new_epoch}
        if save_result.get("saved") is not True or save_result.get("reopened") is not True:
            raise RuntimeError("save-copy did not report a reopened independent part")
        if normalized_path(save_result.get("source_path", "")) != normalized_path(args.expected_part_path):
            raise RuntimeError("save-copy source path differs from the owned independent Work Part")
        if normalized_path(save_result.get("saved_path", "")) != normalized_path(args.target_path):
            raise RuntimeError("save-copy result path differs from the explicitly requested target")
        if not args.target_path.is_file():
            raise RuntimeError("NX SaveAs reported success but the requested .prt file is absent")
        report["save_operation_id"] = save_id
        report["save_checks"] = assert_checks(save_result.get("checks"), "saved-copy reopen verification")
        replay = run.call("nx_save_copy", {
            "operation_id": save_id, **old_binding,
            "target_path": str(args.target_path.resolve()), "reopen": True,
        })
        if replay.get("result") != save_result or replay.get("state") != "SUCCEEDED":
            raise RuntimeError("same operation_id did not replay the original saved-copy result")
        conflict_path = args.target_path.parent / (args.target_path.stem + "-conflict.prt")
        if conflict_path.exists():
            raise RuntimeError("conflict probe path already exists; choose a different target")
        try:
            run.call("nx_save_copy", {
                "operation_id": save_id, **old_binding,
                "target_path": str(conflict_path.resolve()), "reopen": True,
            })
        except ToolFailure as exc:
            if exc.code != "OPERATION_ID_CONFLICT":
                raise
            report["save_retry_conflict"] = exc.code
        else:
            raise RuntimeError("same operation_id with a different target_path was not rejected")
        current_step = "reopened part inspection"
        session_after = run.call("nx_get_session", binding)
        if normalized_path(session_after.get("part_path", "")) != normalized_path(args.target_path):
            raise RuntimeError("reopened Work Part path differs from the requested SaveAs target")
        part_after = run.call("nx_inspect_part", binding)
        saved_expression = next((item for item in part_after.get("expressions", []) if isinstance(item, dict) and
                                 item.get("name") == expression_name), None)
        if saved_expression is None or decimal_value(saved_expression.get("value"), "reopened expression value") != requested_value:
            raise RuntimeError("reopened part does not contain the requested expression value")
        sketch_after = run.call("nx_inspect_sketch", {**binding, "sketch_id": args.sketch_id})
        saved_constraint = next((item for item in sketch_after.get("sketch", {}).get("constraints", [])
                                 if isinstance(item, dict) and item.get("journal_id") == args.constraint_id), None)
        if saved_constraint is None or saved_constraint.get("is_driving") is not True:
            raise RuntimeError("reopened part lost the selected Driving constraint")
        if saved_constraint.get("expression_name") != expression_name:
            raise RuntimeError("reopened part changed the selected constraint association")
        geometry_refs_after = list(saved_constraint.get("geometry_refs", []))
        if geometry_refs_after != geometry_refs_before:
            raise RuntimeError("reopened part changed the selected constraint geometry association")
        feature_count_after = len(part_after.get("features", []))
        if feature_count_after != feature_count_before or feature_count_after == 0:
            raise RuntimeError("reopened part history feature count differs from the source copy")

        current_step = "nx_return"
        returned = run.call("nx_return", binding)
        binding = None
        if returned.get("ownership_state") != "HUMAN_OWNED" or returned.get("safe_for_human") is not True:
            raise RuntimeError("ownership did not return to HUMAN_OWNED safely")
        report["reopened_expression"] = {
            "name": expression_name, "value": saved_expression.get("value"), "unit": saved_expression.get("unit"),
        }
        report["revision_after_reopen"] = session_after.get("revision_id")
        report["reopened_constraint"] = {
            "journal_id": saved_constraint.get("journal_id"),
            "is_driving": saved_constraint.get("is_driving"),
            "expression_name": saved_constraint.get("expression_name"),
            "geometry_refs": saved_constraint.get("geometry_refs"),
        }
        report["history_feature_count"] = feature_count_after
        report["final_ownership_state"] = returned["ownership_state"]
        report["result"] = "PASS"
    except Exception as exc:
        report["failed_step"] = current_step
        report["failure"] = {"type": type(exc).__name__, "message": str(exc)}
        if isinstance(exc, ToolFailure) and exc.code in UNCERTAIN_OUTCOME_CODES:
            unknown_outcome = True
        if isinstance(exc, UnknownOutcome) or (isinstance(exc, TimeoutError) and current_step in {
            "nx_prepare_operation", "nx_execute_plan", "nx_verify_part", "nx_save_copy",
        }):
            unknown_outcome = True
    finally:
        if binding is not None and not unknown_outcome:
            try:
                report["cleanup_return"] = run.call("nx_return", binding)
                binding = None
            except Exception as exc:
                report["cleanup_failure"] = {"type": type(exc).__name__, "message": str(exc)}
                report["result"] = "FAIL"
        elif unknown_outcome:
            report["ownership_cleanup"] = "skipped because operation outcome is unknown; do not retry writes"

        client.close_stdin()
        try:
            report["stdio_exit_code"] = client.wait(timeout=5.0)
            report["stdout_jsonl_lines"] = assert_stdout_jsonl_only(client.all_stdout_bytes())
            report["stderr_line_count"] = len(client.drain_stderr())
            if report["stdio_exit_code"] != 0:
                report["result"] = "FAIL"
        except Exception as exc:
            report["stdio_shutdown_failure"] = {"type": type(exc).__name__, "message": str(exc)}
            report["result"] = "FAIL"
            client.terminate_own_process()
        report["finished_utc"] = datetime.now(timezone.utc).isoformat()
        report["elapsed_ms"] = round((time.monotonic() - started) * 1000.0, 3)
        args.output.parent.mkdir(parents=True, exist_ok=True)
        with args.output.open("x", encoding="utf-8", newline="\n") as output_file:
            output_file.write(json.dumps(report, ensure_ascii=False, indent=2) + "\n")

    print(json.dumps({key: report[key] for key in ("result", "failed_step", "failure", "target_path", "cleanup_failure")
                      if key in report}, ensure_ascii=True))
    return 0 if report["result"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
