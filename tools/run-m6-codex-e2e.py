"""Run the installed Codex CLI as an actual MCP client for the M6 native-model E2E."""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import json
import math
import os
from pathlib import Path
import shutil
import statistics
import subprocess
import time
import tomllib

PROJECT = Path(__file__).resolve().parents[1]
PARAMETER_VERIFY_CHECKS = ("expression", "geometry", "history", "associations")
M3_VERIFY_CHECKS = ("geometry", "constraints", "history", "bodies", "non_target_bodies")
MODEL_STEPS = [
    ("p12", "m6-prepare-p12", "m6-execute-p12", "m6-verify-p12", "parameter"),
    ("p11", "m6-prepare-p11", "m6-execute-p11", "m6-verify-p11", "parameter"),
    ("base_rectangle_sketch", "m6-prepare-base-sketch", "m6-execute-base-sketch", "m6-verify-base-sketch", "sketch"),
    ("base_create_extrude", "m6-prepare-base-extrude", "m6-execute-base-extrude", "m6-verify-base-extrude", "extrude"),
    ("upright_rectangle_sketch", "m6-prepare-upright-sketch", "m6-execute-upright-sketch", "m6-verify-upright-sketch", "sketch"),
    ("unite_extrude", "m6-prepare-unite", "m6-execute-unite", "m6-verify-unite", "extrude"),
    ("face_circle_sketch", "m6-prepare-face-circle", "m6-execute-face-circle", "m6-verify-face-circle", "sketch"),
    ("through_all_subtract", "m6-prepare-through-all-cut", "m6-execute-through-all-cut", "m6-verify-through-all-cut", "extrude"),
]


def _payload(call: dict) -> dict:
    result = call.get("result") or {}
    value = result.get("structured_content", result.get("structuredContent"))
    if isinstance(value, dict):
        return value
    for content in result.get("content", []):
        if content.get("type") == "text":
            try:
                decoded = json.loads(content["text"])
            except (ValueError, TypeError):
                continue
            if isinstance(decoded, dict):
                return decoded
    return {}


def _number(value):
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def _find_key(value, wanted: str):
    if isinstance(value, dict):
        if wanted in value:
            return value[wanted]
        for child in value.values():
            found = _find_key(child, wanted)
            if found is not None:
                return found
    elif isinstance(value, list):
        for child in value:
            found = _find_key(child, wanted)
            if found is not None:
                return found
    return None


def _metrics(values: list[float]) -> dict:
    if not values:
        return {"count": 0, "min_ms": None, "median_ms": None, "max_ms": None}
    return {"count": len(values), "min_ms": min(values),
            "median_ms": statistics.median(values), "max_ms": max(values)}


def _snapshot_scale(payload: dict) -> dict:
    return {key: len(payload.get(key, [])) if isinstance(payload.get(key), list) else None
            for key in ("bodies", "features", "sketches", "expressions")}


def _terminal_result(record: dict) -> dict:
    result = record.get("result")
    return result if isinstance(result, dict) else {}


def _step_summary(name: str, prepare_id: str, execute_id: str, verify_id: str | None,
                  kind: str, calls: list[dict], operations: dict[str, dict], receipts: dict[str, dict],
                  step_ids: dict[str, str], snapshots: list[dict]) -> dict:
    prepare_call = next((c for c in calls if c["tool"] in
                         {"nx_prepare_operation", "nx_prepare_sketch", "nx_prepare_extrude"}
                         and c["arguments"].get("operation_id") == prepare_id), None)
    execute_call = next((c for c in calls if c["tool"] == "nx_execute_plan"
                         and c["arguments"].get("operation_id") == execute_id), None)
    verify_call = next((c for c in calls if c["tool"] == "nx_verify_part"
                        and c["arguments"].get("operation_id") == verify_id), None) if verify_id else None
    prep_record = operations.get(prepare_id, _payload(prepare_call) if prepare_call else {})
    exec_record = operations.get(execute_id, {})
    verify_record = operations.get(verify_id, {}) if verify_id else {}
    prep_payload = _payload(prepare_call) if prepare_call else {}
    plan_id = _find_key(prep_payload, "plan_id") or _find_key(prep_record, "plan_id")
    prepare_args = prepare_call["arguments"] if prepare_call else {}
    exec_result = _terminal_result(exec_record)
    verify_result = _terminal_result(verify_record)
    terminal_index = receipts.get(execute_id, {}).get("terminal_event_index")
    post = next((s for s in snapshots if terminal_index is not None and s["event_index"] > terminal_index), None)
    verify_evidence = None
    if verify_id:
        checks = verify_result.get("checks", {})
        required_checks = PARAMETER_VERIFY_CHECKS if kind == "parameter" else M3_VERIFY_CHECKS
        required_statuses = {
            key: (checks.get(key, {}).get("status") if isinstance(checks.get(key), dict) else None)
            for key in required_checks
        }
        verify_evidence = {
            "operation_id": verify_id,
            "state": verify_record.get("state"),
            "verified": verify_result.get("verified"),
            "required_checks": list(required_checks),
            "check_statuses": required_statuses,
            "all_required_checks_verified": len(required_statuses) == len(required_checks)
                and all(status == "VERIFIED" for status in required_statuses.values()),
            "result": verify_result,
        }
    else:
        verify_evidence = {
            "method": "SUCCEEDED execute operation plus subsequent live nx_inspect_part readback",
            "post_execute_revision_id": post["payload"].get("revision_id") if post else None,
            "post_execute_scale": _snapshot_scale(post["payload"]) if post else None,
            "post_execute_body_volumes_mm3": [
                {"journal_id": b.get("journal_id"), "volume_mm3": b.get("volume_mm3")}
                for b in (post["payload"].get("bodies", []) if post else [])
            ],
        }
    return {
        "name": name,
        "kind": kind,
        "prepare_operation_id": prepare_id,
        "prepare_state": prep_record.get("state"),
        "prepare_revision_id": prepare_args.get("revision_id"),
        "plan_id": plan_id,
        "execute_operation_id": execute_id,
        "execute_state": exec_record.get("state"),
        "execute_kind": exec_record.get("kind"),
        "execute_result": exec_result,
        "verify": verify_evidence,
        "prepare_call_event_index": prepare_call.get("event_index") if prepare_call else None,
        "execute_call_event_index": execute_call.get("event_index") if execute_call else None,
        "verify_call_event_index": verify_call.get("event_index") if verify_call else None,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--launch-report", type=Path, required=True)
    parser.add_argument("--window", "--window-report", dest="window_report", type=Path)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--model", default="gpt-6-luna")
    parser.add_argument("--codex-exe", type=Path)
    args = parser.parse_args()

    launch_path = args.launch_report.resolve()
    try:
        launch = json.loads(launch_path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:
        parser.error(f"cannot read --launch-report: {exc}")
    if launch.get("test_stage") != "M6":
        parser.error("a dedicated M6 launch report is required")
    for key in ("pid", "copied_part", "source_part", "config_path"):
        if key not in launch:
            parser.error(f"M6 launch report is missing {key}")
    copied_path = Path(launch["copied_part"]).resolve()
    source_path = Path(launch["source_part"]).resolve()
    config = Path(launch["config_path"]).resolve()
    saved_path = copied_path.with_name("m6-e2e-saved.prt")
    if not copied_path.is_file() or not source_path.is_file() or not config.is_file():
        parser.error("launch report copied/source part and MCP config must all exist")
    if saved_path.exists():
        parser.error(f"saved target already exists: {saved_path}; use a fresh M6 launch copy")
    if args.window_report:
        try:
            window = json.loads(args.window_report.resolve().read_text(encoding="utf-8"))
        except (OSError, ValueError) as exc:
            parser.error(f"cannot read --window: {exc}")
        if window.get("pid") != launch["pid"]:
            parser.error("window report PID does not match M6 launch report")
    else:
        window = None

    output = args.output_dir.resolve()
    output.mkdir(parents=True, exist_ok=False)
    source_before = source_path.read_bytes()
    input_before = copied_path.read_bytes()
    if args.codex_exe:
        executable = args.codex_exe.resolve()
    else:
        npm_cmd = shutil.which("codex.cmd")
        if not npm_cmd:
            parser.error("installed codex.cmd or --codex-exe is required")
        matches = list((Path(npm_cmd).parent / "node_modules/@openai/codex").glob(
            "node_modules/@openai/codex-win32-x64/vendor/*/bin/codex.exe"))
        if len(matches) != 1:
            parser.error("cannot resolve installed Codex binary; pass --codex-exe")
        executable = matches[0]
    python_exe = shutil.which("python.exe")
    if not python_exe:
        parser.error("python.exe is required to host the nxsketch stdio server")

    prompt = f"""Perform only this authorized M6 native-model E2E using the nxsketch MCP tools.
Use no shell tools, file editing, other MCP servers, or subagents. Do not restart, close, or force-kill NX.
Dedicated NX PID: {launch['pid']}. The only editable artifact is the saved independent copy {copied_path}.
Source part {source_path} is read-only reference. Final save path must be exactly {saved_path}.
Never claim success without tool-returned evidence. Stop writes after any failed/UNKNOWN operation; never replay a write.

Start with nx_bridge_status, nx_get_capabilities, and nx_bridge_ping(nonce='M6 actual host'). Confirm the exact PID,
HUMAN_OWNED state, writes_halted=false, and ping on_main_thread=true. Take over only the stated copied part with
nx_takeover(expected_pid={launch['pid']}, expected_part_path={json.dumps(str(copied_path))},
artifact_scope='saved_independent_copy', authorize=true). Use only returned ownership_id and binding_epoch thereafter.
Call nx_get_session and nx_inspect_part. Before EVERY prepare call below, call nx_inspect_part and use that fresh
returned revision_id. Keep the sequence serial; each prepare, execute, and verify operation must have its own listed
unique operation_id. Poll with nx_get_operation only after a tool response contains an accepted operation record whose
operation_id matches the submitted ID and whose state is present. A JSON-RPC/MCP argument error or other error without
an accepted operation record did not create an operation: do not query that unregistered ID, retry, or reuse it. Stop
later model writes on any failed or UNKNOWN operation. For a known failure with no accepted operation and status still
showing writes_halted=false, return the current ownership to HUMAN_OWNED and stop; do not retry the rejected request.
If an operation outcome is UNKNOWN or writes are frozen, follow the bridge's existing handoff contract and stop.

For each accepted prepare, execute, verify, and save operation, poll only its returned matching operation_id until
terminal and require SUCCEEDED. Never infer that an operation was accepted from the ID present in a request.

The source part is the existing 200x100x6 perforated plate. p12 is its 36 mm hole dimension and p11 is its 71 mm
locator dimension; identify both by live expression and Driving constraint association, without inferring other feature
meanings from those labels. The new L body begins at x=300 mm and is a separate newly created target body.

1) Find exactly one original Driving constraint whose expression is p12. Prepare value='36', unit='mm', using the
actual sketch_id, constraint_id, and latest revision, operation_id='m6-prepare-p12'. Execute its returned plan with
operation_id='m6-execute-p12', poll SUCCEEDED, then nx_verify_part(operation_id='m6-verify-p12',
execute_operation_id='m6-execute-p12'); poll SUCCEEDED and require result.verified=true plus all four checks
expression, geometry, history, associations with status VERIFIED. Inspect the part after verify.
2) Re-inspect and find exactly one original Driving constraint whose expression is p11. Prepare value='71', unit='mm',
operation_id='m6-prepare-p11'; execute as 'm6-execute-p11'; verify as 'm6-verify-p11'. Require both terminal
operations SUCCEEDED and verified=true plus all four checks expression, geometry, history, associations VERIFIED.
Inspect the part after verify.
3) Prepare and execute an XY rectangle sketch at origin [300,0,0], corner [0,0], width 60, height 40, using
'm6-prepare-base-sketch', 'm6-execute-base-sketch', 'm6-verify-base-sketch'. The exact nx_prepare_sketch arguments
include plane='XY', origin_mm=[300,0,0], and geometries=[{{"id":"m6-base-rectangle","type":"rectangle",
"corner_mm":[0,0],"width_mm":60,"height_mm":40}}]. The whole rectangle object must be inside the geometries array;
do not send a singular geometry field or put rectangle fields beside geometries. Inspect after verification.
4) Use the actual base sketch_id. nx_prepare_extrude must use mode='create', direction='positive', extent='finite',
depth_mm=6 and that actual sketch_id; it has no target_body_id. Use 'm6-prepare-base-extrude',
'm6-execute-base-extrude', 'm6-verify-base-extrude'. Inspect after verification and identify the new L target body by
its actual body journal_id.
5) Re-inspect, then prepare and execute an XY rectangle sketch at origin [300,0,6], corner [0,0], width 10, height 40,
using 'm6-prepare-upright-sketch', 'm6-execute-upright-sketch', 'm6-verify-upright-sketch'. Its exact sketch geometry
is geometries=[{{"id":"m6-upright-rectangle","type":"rectangle","corner_mm":[0,0],"width_mm":10,
"height_mm":40}}] with plane='XY' and origin_mm=[300,0,6]; again, rectangle fields occur only inside geometries.
Inspect after verification.
6) Re-inspect, then nx_prepare_extrude the actual second sketch using mode='unite', direction='positive',
extent='finite', depth_mm=24, and target_body_id=<actual new L body journal_id>. Use 'm6-prepare-unite',
'm6-execute-unite', 'm6-verify-unite'. Inspect after verification. Do not target any input body. The L target should
now measure 24000 mm^3.
7) Re-inspect and query faces on that explicit target body with selector exactly 'outermost positive X side'. Require
selection_status='unique', candidate_count=1, positive-X normal, and outermost x=360 mm. Use the returned candidate's
native frame (origin_mm, x_axis_world, y_axis_world) to transform world point [360,20,3] to the face sketch's local
coordinates: local=[dot(world-origin,x_axis), dot(world-origin,y_axis)]. Do not assume a face frame. Prepare and execute
a circle of diameter 2 mm at those local coordinates on support_face_selector='outermost positive X side' and the same
explicit support_body_id, using 'm6-prepare-face-circle', 'm6-execute-face-circle', 'm6-verify-face-circle'. Its exact
geometries argument is [{{"id":"m6-through-hole","type":"circle","center_mm":[local_x,local_y],
"diameter_mm":2}}]. Replace local_x and local_y with the computed numeric face coordinates. Pass that array with support_face_selector='outermost positive X side' and the explicit
support_body_id; do not use a singular geometry field. Inspect after verification and call nx_inspect_sketch; require
native support association to the selected face/body.
8) Re-inspect and use the same explicit L target. nx_prepare_extrude the actual circle sketch using mode='subtract',
direction='negative', extent='through_all', and target_body_id=<same new L body journal_id>; omit depth_mm for
through_all. Use 'm6-prepare-through-all-cut', 'm6-execute-through-all-cut', 'm6-verify-through-all-cut'. Inspect after verification.
The cut must remove the diameter-2 circle through the 60 mm X span; target body volume should be 24000 - 60*pi mm^3.

After each of the six geometric executes above succeeds, immediately call nx_verify_part with its listed verify ID and
execute_operation_id for that execute. Poll verification to terminal SUCCEEDED before the next prepare. Require
result.verified=true and every M3 check status VERIFIED: geometry, constraints, history, bodies, non_target_bodies.

There must be exactly 8 model-changing prepare/execute actions: p12, p11, two sketches plus their two extrusions, the
face circle sketch, and the through-all subtract. Each of all eight actions requires a unique prepare, execute, and
verify operation ID and its own nx_verify_part after a SUCCEEDED execute. Parameter verifications require all four
expression/geometry/history/associations checks VERIFIED; M3 geometric verifications require all five
geometry/constraints/history/bodies/non_target_bodies checks VERIFIED. Record each verify state, verified flag, and
required check statuses independently in the summary. Also inspect the face sketch support association.

Call nx_save_copy with operation_id='m6-save', current ownership/binding, target_path exactly {saved_path}, reopen=true.
Poll until SUCCEEDED and require saved=true/reopened=true. Use only returned new_ownership_id/new_binding_epoch after save.
Inspect the reopened part and the saved face-circle sketch; require p11=71 mm, p12=36 mm, actual target body volume
24000-60*pi mm^3, and retained native sketch-to-face/body association. Record actual final expressions, bodies, features,
sketches and association evidence. Then nx_return using the new binding; require HUMAN_OWNED and safe_for_human=true;
finally nx_bridge_status must show writes_halted=false. Stop after handoff.
If any operation fails, report exact tool evidence and completed actions. Do not continue later writes or claim handoff
unless nx_return actually succeeds. Briefly report observed values, feature counts, saved/reopened path and ownership state."""

    command = [str(executable), "exec", "--skip-git-repo-check", "--ephemeral", "--json",
               "--color", "never", "--dangerously-bypass-approvals-and-sandbox",
               "-C", str(PROJECT), "-m", args.model,
               "-c", 'model_reasoning_effort="max"', "-c", 'approval_policy="never"']
    codex_home = Path(os.environ.get("CODEX_HOME", str(Path.home() / ".codex")))
    config_path = codex_home / "config.toml"
    if config_path.exists():
        existing = tomllib.loads(config_path.read_text(encoding="utf-8"))
        for server_name in existing.get("mcp_servers", {}):
            command += ["-c", f"mcp_servers.{server_name}.enabled=false"]
    overrides = {
        "command": str(Path(python_exe).resolve()),
        "args": ["-B", str(PROJECT / "tools/mcp_stdio.py"), "--config", str(config)],
        "cwd": str(PROJECT), "required": True, "startup_timeout_sec": 30,
        "tool_timeout_sec": 140, "default_tools_approval_mode": "auto",
    }
    for key, value in overrides.items():
        command += ["-c", f"mcp_servers.nxsketch.{key}={json.dumps(value)}"]
    command += ["-o", str(output / "host-final.txt"), "-"]
    (output / "host-prompt.txt").write_text(prompt + "\n", encoding="utf-8")

    started_utc = datetime.now(timezone.utc).isoformat()
    started_ns = time.perf_counter_ns()
    process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                               stderr=(output / "host-stderr.txt").open("wb"),
                               env={**os.environ, "PYTHONUTF8": "1"}, cwd=PROJECT)
    assert process.stdin is not None and process.stdout is not None
    input_error = None
    try:
        process.stdin.write(prompt.encode("utf-8"))
        process.stdin.close()
    except (BrokenPipeError, OSError) as exc:
        input_error = f"{type(exc).__name__}: {exc}"
        try:
            process.stdin.close()
        except OSError:
            pass
    receipt_path = output / "host-event-timings.jsonl"
    with (output / "host-events.jsonl").open("wb") as event_file, receipt_path.open("w", encoding="utf-8") as timing_file:
        for event_index, raw_line in enumerate(process.stdout):
            received_utc = datetime.now(timezone.utc).isoformat()
            received_ns = time.perf_counter_ns()
            event_file.write(raw_line)
            event_file.flush()
            try:
                event = json.loads(raw_line.decode("utf-8"))
            except (UnicodeDecodeError, ValueError):
                event = {}
            item = event.get("item") if isinstance(event, dict) else None
            receipt = {
                "event_index": event_index,
                "received_utc": received_utc,
                "received_monotonic_ns": received_ns,
                "event_type": event.get("type") if isinstance(event, dict) else None,
                "item_id": item.get("id") if isinstance(item, dict) else None,
                "item_type": item.get("type") if isinstance(item, dict) else None,
                "tool": item.get("tool") if isinstance(item, dict) else None,
            }
            timing_file.write(json.dumps(receipt, ensure_ascii=False) + "\n")
            timing_file.flush()
    process.wait()
    finished_ns = time.perf_counter_ns()
    finished_utc = datetime.now(timezone.utc).isoformat()

    raw_lines = (output / "host-events.jsonl").read_text(encoding="utf-8").splitlines()
    events = []
    receipts = []
    for index, line in enumerate(raw_lines):
        try:
            events.append(json.loads(line))
        except ValueError:
            events.append({"type": "invalid_json_line", "raw": line})
    for line in receipt_path.read_text(encoding="utf-8").splitlines():
        try:
            receipts.append(json.loads(line))
        except ValueError:
            receipts.append({})
    receipt_by_index = {r.get("event_index"): r for r in receipts}
    starts = {}
    calls = []
    timings = []
    for index, event in enumerate(events):
        if not isinstance(event, dict):
            continue
        item = event.get("item") or {}
        if item.get("type") != "mcp_tool_call":
            continue
        item_id = item.get("id")
        if event.get("type") == "item.started":
            starts[item_id] = (index, receipt_by_index.get(index, {}))
        elif event.get("type") == "item.completed":
            begin = starts.get(item_id)
            end_receipt = receipt_by_index.get(index, {})
            duration_ms = None
            if begin:
                start_ns = begin[1].get("received_monotonic_ns")
                end_ns = end_receipt.get("received_monotonic_ns")
                if isinstance(start_ns, int) and isinstance(end_ns, int):
                    duration_ms = (end_ns - start_ns) / 1_000_000
            call = {"event_index": index, "item_id": item_id, "tool": item.get("tool"),
                    "arguments": item.get("arguments") or {}, "result": item.get("result"),
                    "error": item.get("error"), "duration_ms": duration_ms}
            calls.append(call)
            if duration_ms is not None:
                timings.append({"item_id": item_id, "tool": item.get("tool"),
                                "start_event_index": begin[0], "completed_event_index": index,
                                "duration_ms": duration_ms})

    completed_calls = calls
    observed_tools = {c.get("tool") for c in completed_calls}
    operation_ids = {}
    operation_calls = []
    for call in completed_calls:
        tool = call.get("tool")
        if tool in {"nx_prepare_operation", "nx_prepare_sketch", "nx_prepare_extrude",
                    "nx_execute_plan", "nx_verify_part", "nx_save_copy"}:
            op_id = call["arguments"].get("operation_id")
            if op_id:
                operation_ids.setdefault(op_id, []).append(call)
                operation_calls.append((op_id, call))
    operations = {}
    operation_event_indices = {}
    operation_tools = {"nx_prepare_operation", "nx_prepare_sketch", "nx_prepare_extrude",
                       "nx_execute_plan", "nx_verify_part", "nx_save_copy", "nx_get_operation"}
    for call in completed_calls:
        if call.get("tool") not in operation_tools:
            continue
        op_id = call.get("arguments", {}).get("operation_id")
        record = _payload(call)
        if op_id and isinstance(record.get("state"), str):
            operations[op_id] = record
            if record["state"] in {"SUCCEEDED", "FAILED", "CANCELLED", "UNKNOWN"}:
                operation_event_indices[op_id] = call["event_index"]
    op_receipts = {op_id: {"terminal_event_index": operation_event_indices.get(op_id)}
                   for op_id in operation_ids}
    snapshots = [{"event_index": c["event_index"], "payload": _payload(c)}
                 for c in completed_calls if c.get("tool") == "nx_inspect_part"]
    step_summaries = [_step_summary(*spec, completed_calls, operations, op_receipts,
                                    {s[1]: s[0] for s in MODEL_STEPS}, snapshots)
                      for spec in MODEL_STEPS]

    def find_call(tool: str, predicate=lambda _c: True, last=False):
        found = [c for c in completed_calls if c.get("tool") == tool and predicate(c)]
        return found[-1] if last and found else (found[0] if found else None)

    statuses = [_payload(c) for c in completed_calls if c.get("tool") == "nx_bridge_status"]
    pings = [_payload(c) for c in completed_calls if c.get("tool") == "nx_bridge_ping"]
    takeovers = [_payload(c) for c in completed_calls if c.get("tool") == "nx_takeover"]
    returns = [_payload(c) for c in completed_calls if c.get("tool") == "nx_return"]
    final_inspect_call = find_call("nx_inspect_part", last=True)
    final_part = _payload(final_inspect_call) if final_inspect_call else {}
    expressions = {str(e.get("name")): e for e in final_part.get("expressions", [])
                   if isinstance(e, dict) and e.get("name")}
    final_values = {name: {"value": record.get("value"), "right_hand_side": record.get("right_hand_side"),
                           "unit": record.get("unit")}
                    for name, record in expressions.items() if name in {"p11", "p12"}}
    unite_call = find_call("nx_prepare_extrude", lambda c: c["arguments"].get("mode") == "unite")
    cut_call = find_call("nx_prepare_extrude", lambda c: c["arguments"].get("mode") == "subtract")
    target_body_id = (unite_call or {}).get("arguments", {}).get("target_body_id")
    query_call = find_call("nx_query_faces")
    query = _payload(query_call) if query_call else {}
    candidate = (query.get("candidates") or [{}])[0]
    circle_call = find_call("nx_prepare_sketch", lambda c: "support_face_selector" in c["arguments"])
    circle_args = (circle_call or {}).get("arguments", {})
    circle_geometry = next((g for g in circle_args.get("geometries", []) if g.get("type") == "circle"), {})
    frame = candidate.get("frame") or {}
    world_target = [360.0, 20.0, 3.0]
    local_center = circle_geometry.get("center_mm")
    transformed_world = None
    local_transform_ok = False
    try:
        origin = frame["origin_mm"]
        x_axis = frame["x_axis_world"]
        y_axis = frame["y_axis_world"]
        delta = [world_target[i] - float(origin[i]) for i in range(3)]
        transformed_world = [float(origin[i]) + float(local_center[0]) * float(x_axis[i])
                             + float(local_center[1]) * float(y_axis[i]) for i in range(3)]
        local_transform_ok = all(abs(transformed_world[i] - world_target[i]) <= 1e-6 for i in range(3))
    except (KeyError, TypeError, ValueError, IndexError):
        delta = None
    cut_op_id = (cut_call or {}).get("arguments", {}).get("operation_id")
    cut_prepare_record = operations.get(cut_op_id, {}) if cut_op_id else {}
    save_record = operations.get("m6-save", {})
    save_result = _terminal_result(save_record)
    save_candidate = save_result
    saved = save_candidate.get("saved") is True
    reopened = save_candidate.get("reopened") is True
    saved_sketch_call = find_call("nx_inspect_sketch", last=True)
    saved_sketch_payload = _payload(saved_sketch_call) if saved_sketch_call else {}
    association = saved_sketch_payload.get("support_association")
    if association is None:
        association = _find_key(saved_sketch_payload.get("sketch", {}), "support_association")
    if association is None:
        association = _find_key(saved_sketch_payload, "support_associations")
    association_status = association.get("status") if isinstance(association, dict) else None
    body_records = final_part.get("bodies", [])
    target_body = next((b for b in body_records if b.get("journal_id") == target_body_id), {})
    actual_volume = _number(target_body.get("volume_mm3"))
    expected_volume = 24000.0 - 60.0 * math.pi
    expected_expression = lambda name, expected: _number(final_values.get(name, {}).get("value")) == expected

    all_step_terminal = len(step_summaries) == 8 and all(
        step["prepare_state"] == "SUCCEEDED"
        and step["execute_state"] == "SUCCEEDED"
        and step["verify"].get("state") == "SUCCEEDED"
        and step["verify"].get("verified") is True
        and step["verify"].get("all_required_checks_verified") is True
        for step in step_summaries)
    latest_revisions = {}
    for step, spec in zip(step_summaries, MODEL_STEPS):
        prep_id = spec[1]
        prep_call = next((c for c in completed_calls if c.get("tool") in
                          {"nx_prepare_operation", "nx_prepare_sketch", "nx_prepare_extrude"}
                          and c["arguments"].get("operation_id") == prep_id), None)
        if not prep_call:
            latest_revisions[step["name"]] = {"matches_latest_inspection": False}
            continue
        preceding = [s for s in snapshots if s["event_index"] < prep_call["event_index"]]
        latest = preceding[-1]["payload"] if preceding else {}
        arg_revision = prep_call["arguments"].get("revision_id")
        latest_revisions[step["name"]] = {
            "prepare_revision_id": arg_revision,
            "latest_prior_inspection_revision_id": latest.get("revision_id"),
            "matches_latest_inspection": bool(latest) and str(arg_revision) == str(latest.get("revision_id")),
        }

    unique_ids = [op_id for op_id, _ in operation_calls]
    unique_operation_ids = len(unique_ids) == len(set(unique_ids))
    model_action_count = sum(1 for step in step_summaries if step["execute_state"] == "SUCCEEDED")
    human_handoff = bool(returns) and returns[-1].get("ownership_state") == "HUMAN_OWNED" \
        and returns[-1].get("safe_for_human") is True
    ping_ok = bool(pings) and pings[-1].get("on_main_thread") is True
    window_match = None
    if window:
        window_match = bool(pings) and str(pings[-1].get("callback_thread_id")) == str(window.get("nx_window_thread_id"))
    input_unchanged = copied_path.read_bytes() == input_before
    source_unchanged = source_path.read_bytes() == source_before

    call_timing_by_id = {t["item_id"]: t for t in timings}
    operation_kind_by_id = {}
    for name, prepare_id, execute_id, verify_id, _ in MODEL_STEPS:
        operation_kind_by_id[prepare_id] = "prepare"
        operation_kind_by_id[execute_id] = "execute"
        if verify_id:
            operation_kind_by_id[verify_id] = "verify"
    operation_kind_by_id["m6-save"] = "save"
    timing_groups = {key: [] for key in ("read", "prepare", "execute", "verify", "save")}
    tool_call_id_to_operation = {}
    for c in completed_calls:
        if c.get("tool") in {"nx_prepare_operation", "nx_prepare_sketch", "nx_prepare_extrude",
                              "nx_execute_plan", "nx_verify_part", "nx_save_copy"}:
            op_id = c.get("arguments", {}).get("operation_id")
            if op_id:
                tool_call_id_to_operation[c["item_id"]] = operation_kind_by_id.get(op_id)
        elif c.get("tool") == "nx_get_operation":
            op_id = c.get("arguments", {}).get("operation_id")
            if op_id:
                tool_call_id_to_operation[c["item_id"]] = operation_kind_by_id.get(op_id)
        else:
            tool_call_id_to_operation[c["item_id"]] = "read"
    for timing in timings:
        category = tool_call_id_to_operation.get(timing["item_id"], "read")
        if category in timing_groups:
            timing_groups[category].append(timing["duration_ms"])
    stage_timings = {key: _metrics(values) for key, values in timing_groups.items()}

    expected_tools = {"nx_bridge_status", "nx_get_capabilities", "nx_bridge_ping", "nx_takeover",
                      "nx_get_session", "nx_inspect_part", "nx_query_faces", "nx_inspect_sketch",
                      "nx_prepare_operation", "nx_prepare_sketch", "nx_prepare_extrude", "nx_execute_plan",
                      "nx_get_operation", "nx_verify_part", "nx_save_copy", "nx_return"}
    acceptance = {
        "tool_chain_observed": process.returncode == 0 and expected_tools <= observed_tools,
        "dedicated_pid_matches": bool(statuses) and statuses[0].get("pid") == launch["pid"],
        "dispatch_on_main_thread": ping_ok,
        "window_thread_matches_dispatch": window_match,
        "takeover_ai_owned": bool(takeovers) and takeovers[0].get("ownership_state") == "AI_OWNED",
        "eight_model_actions_succeeded": model_action_count == 8 and all_step_terminal,
        "all_eight_verifications_succeeded_with_required_checks": len(step_summaries) == 8 and all(
            step["verify"].get("state") == "SUCCEEDED"
            and step["verify"].get("verified") is True
            and step["verify"].get("all_required_checks_verified") is True
            for step in step_summaries),
        "unique_operation_ids": unique_operation_ids,
        "all_prepares_use_latest_revision": bool(latest_revisions) and all(
            value["matches_latest_inspection"] for value in latest_revisions.values()),
        "p12_final_value_is_36_mm": expected_expression("p12", 36.0)
            and final_values.get("p12", {}).get("unit") == "mm",
        "p11_final_value_is_71_mm": expected_expression("p11", 71.0)
            and final_values.get("p11", {}).get("unit") == "mm",
        "unique_outermost_positive_x_face_on_target": query.get("selection_status") == "unique"
            and query.get("candidate_count") == 1 and candidate.get("body_id") == target_body_id
            and (candidate.get("normal_world") or [0, 0, 0])[0] > 0.999999
            and abs(float((candidate.get("plane_origin_mm") or [math.inf])[0]) - 360.0) <= 1e-6,
        "circle_local_frame_maps_to_world_target": local_transform_ok,
        "through_all_subtract_targets_l_body": (cut_call is not None and target_body_id is not None
            and cut_call["arguments"].get("target_body_id") == target_body_id
            and cut_call["arguments"].get("extent") == "through_all"
            and cut_call["arguments"].get("direction") == "negative"),
        "target_body_volume_matches_24000_minus_60pi": actual_volume is not None
            and math.isclose(actual_volume, expected_volume, rel_tol=1e-9, abs_tol=1e-5),
        "saved_and_reopened": save_record.get("state") == "SUCCEEDED" and saved and reopened,
        "saved_file_exists": saved_path.is_file(),
        "reopened_native_support_association_retained": association_status == "VERIFIED"
            or (isinstance(association, dict) and association.get("native") is True
                and association.get("selector_unique") is True and association.get("current_face_matches") is True),
        "input_file_bytes_unchanged": input_unchanged,
        "original_source_bytes_unchanged": source_unchanged,
        "human_handoff": human_handoff,
        "final_writes_not_halted": bool(statuses) and statuses[-1].get("writes_halted") is False,
    }
    if window_match is None:
        acceptance.pop("window_thread_matches_dispatch")
    summary = {
        "started_utc": started_utc,
        "finished_utc": finished_utc,
        "codex_client_wall_duration_seconds": (finished_ns - started_ns) / 1_000_000_000,
        "wall_duration_note": "Includes Codex model reasoning and client/MCP work.",
        "tool_event_duration_note": "Each duration is item.started-to-item.completed event receipt time measured by this runner; excludes time between calls and model reasoning.",
        "client": "Codex CLI",
        "client_version": subprocess.check_output([str(executable), "--version"], text=True).strip(),
        "model": args.model,
        "exit_code": process.returncode,
        "input_error": input_error,
        "tool_call_count": len(completed_calls),
        "observed_tools": sorted(x for x in observed_tools if x),
        "missing_tools": sorted(expected_tools - observed_tools),
        "events_file": str(output / "host-events.jsonl"),
        "event_timings_file": str(receipt_path),
        "tool_event_durations": timings,
        "stage_timing_summary": stage_timings,
        "model_action_count": model_action_count,
        "model_steps": step_summaries,
        "fresh_revision_evidence": latest_revisions,
        "initial_model_scale": _snapshot_scale(snapshots[0]["payload"]) if snapshots else {},
        "final_pre_save_model_scale": _snapshot_scale(snapshots[-2]["payload"])
            if len(snapshots) >= 2 else (_snapshot_scale(snapshots[-1]["payload"]) if snapshots else {}),
        "reopened_model_scale": _snapshot_scale(final_part),
        "face_query": {"selector": query_call.get("arguments", {}).get("selector") if query_call else None,
                       "body_id": query_call.get("arguments", {}).get("body_id") if query_call else None,
                       "selection_status": query.get("selection_status"),
                       "candidate_count": query.get("candidate_count"), "candidate": candidate},
        "circle_local_coordinates": {"world_target_mm": world_target, "local_center_mm": local_center,
                                     "frame": frame, "reconstructed_world_mm": transformed_world},
        "final_expressions": final_values,
        "target_body_id": target_body_id,
        "final_target_body": target_body,
        "expected_target_volume_mm3": expected_volume,
        "saved_path": str(saved_path),
        "save_result": {"state": save_record.get("state"), "saved": saved, "reopened": reopened,
                        "result_keys": sorted(save_result.keys())},
        "reopened_face_sketch_association": association,
        "source_comparison": "direct byte equality in memory; no digest or hash",
        "acceptance": acceptance,
        "result": "PASS" if all(acceptance.values()) else "FAIL",
    }
    (output / "host-summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2) + "\n",
                                              encoding="utf-8")
    print(json.dumps(summary, ensure_ascii=False))
    return 0 if summary["result"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
