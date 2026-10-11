"""Run the installed Codex CLI as an actual MCP client; retain its tool events."""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import shutil
import subprocess
import tomllib

PROJECT = Path(__file__).resolve().parents[1]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--launch-report", type=Path)
    parser.add_argument("--window-report", type=Path)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--probe", action="store_true")
    parser.add_argument("--model", default="gpt-6-luna")
    parser.add_argument("--codex-exe", type=Path)
    args = parser.parse_args()
    if not args.probe and not args.launch_report:
        parser.error("--launch-report is required for NX E2E")
    output = args.output_dir.resolve()
    output.mkdir(parents=True, exist_ok=False)
    if args.codex_exe:
        executable = args.codex_exe.resolve()
    else:
        # The npm wrapper resolves this installed native binary; no installation.
        npm_cmd = shutil.which("codex.cmd")
        if not npm_cmd:
            parser.error("installed codex.cmd or --codex-exe is required")
        matches = list((Path(npm_cmd).parent / "node_modules/@openai/codex").glob(
            "node_modules/@openai/codex-win32-x64/vendor/*/bin/codex.exe"))
        if len(matches) != 1:
            parser.error("cannot resolve installed Codex binary; pass --codex-exe")
        executable = matches[0]
    launch = None
    source_before = input_before = None
    if args.probe:
        config = output / "offline-mcp.json"
        config.write_text("{}\n", encoding="utf-8")
        prompt = "Call nx_get_capabilities through the nxsketch MCP server once. Report the result. Do not use shell tools, edit files, start agents, or call other servers."
    else:
        launch = json.loads(args.launch_report.read_text(encoding="utf-8"))
        if launch["test_stage"] != "M2-7":
            parser.error("a dedicated M2-7 launch report is required")
        config = Path(launch["config_path"])
        source_before = Path(launch["source_part"]).read_bytes()
        input_before = Path(launch["copied_part"]).read_bytes()
        saved_path = Path(launch["copied_part"]).with_name("m2-e2e-saved.prt")
        if saved_path.exists():
            parser.error("saved target already exists; use a new dedicated launch")
        prompt = f"""Perform only the authorized M2-7 E2E with the nxsketch MCP tools.
Use no shell tools, file editing, external tools, or subagents. Do not restart or force-kill anything.
The dedicated NX PID is {launch['pid']}. The saved independent input copy is {launch['copied_part']}.
1. Call nx_bridge_status, nx_get_capabilities, and nx_bridge_ping with nonce='M2-7 actual host'. Confirm that exact PID, HUMAN_OWNED, and on_main_thread=true.
2. nx_takeover with expected_pid={launch['pid']}, expected_part_path={launch['copied_part']}, artifact_scope=saved_independent_copy, authorize=true.
3. Use returned ownership_id and binding_epoch in nx_get_session and nx_inspect_part. Find the real unique Driving sketch constraint associated with p12, then prepare it with explicit value='36', unit='mm', actual revision_id, and operation_id='host-prepare-p12'.
4. Wait by nx_get_operation until preparation is terminal. Execute its returned plan_id with operation_id='host-execute-p12', then query until SUCCEEDED. Do not automatically replay unknown/failed writes. Call nx_verify_part with operation_id='host-verify-p12' and execute_operation_id='host-execute-p12'; require verified=true.
5. Inspect the part again and find the real unique Driving constraint for p11. Prepare value='71', unit='mm' with operation_id='host-prepare-p11' and current revision, execute its plan using 'host-execute-p11', query terminal, then nx_verify_part with operation_id='host-verify-p11' and execute_operation_id='host-execute-p11'; require verified=true.
6. Explicitly save the native part to this nonexistent unique path: {saved_path}. Call nx_save_copy with operation_id='host-save', current binding, target_path equal to that exact path, reopen=true. Wait by nx_get_operation for SUCCEEDED. Require saved=true and reopened=true; read the returned new_ownership_id/new_binding_epoch and use them for all subsequent model calls.
7. nx_inspect_part and nx_inspect_sketch on the reopened saved copy; confirm p12=36 mm, p11=71 mm and retained native features, constraints, expressions and associations. Do not make any further changes. nx_return with the new binding; require HUMAN_OWNED and safe_for_human=true. nx_bridge_status must have writes_halted=false.
If any step fails, report the exact error and completed steps. Never claim a tool succeeded without its returned evidence. A known ordinary failure may be followed by nx_return only if the bridge allows it; UNKNOWN_FROZEN requires stopping writes and no safe-handoff claim.
Return a brief report of observed dimensions, saved path, reopen verification and final ownership state. Stop after M2-7."""
    command = [str(executable), "exec", "--skip-git-repo-check", "--ephemeral", "--json",
               "--color", "never", "--dangerously-bypass-approvals-and-sandbox",
               "-C", str(PROJECT), "-m", args.model,
               "-c", 'model_reasoning_effort="max"', "-c", 'approval_policy="never"']
    # Disable configured unrelated MCP servers for this invocation only.
    codex_home = Path(os.environ.get("CODEX_HOME", str(Path.home() / ".codex")))
    config_path = codex_home / "config.toml"
    if config_path.exists():
        existing = tomllib.loads(config_path.read_text(encoding="utf-8"))
        for name in existing.get("mcp_servers", {}):
            command += ["-c", f"mcp_servers.{name}.enabled=false"]
    overrides = {
        "command": str(Path(shutil.which("python.exe")).resolve()),
        "args": ["-B", str(PROJECT / "tools/mcp_stdio.py"), "--config", str(config.resolve())],
        "cwd": str(PROJECT), "required": True, "startup_timeout_sec": 30,
        "tool_timeout_sec": 140, "default_tools_approval_mode": "auto",
    }
    for key, value in overrides.items():
        command += ["-c", f"mcp_servers.nxsketch.{key}={json.dumps(value)}"]
    command += ["-o", str(output / "host-final.txt"), "-"]
    (output / "host-prompt.txt").write_text(prompt + "\n", encoding="utf-8")
    started = datetime.now(timezone.utc).isoformat()
    with (output / "host-events.jsonl").open("wb") as stdout, (output / "host-stderr.txt").open("wb") as stderr:
        process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=stdout, stderr=stderr,
                                   env={**os.environ, "PYTHONUTF8": "1"}, cwd=PROJECT)
        process.communicate(prompt.encode("utf-8"))
    events = [json.loads(line) for line in (output / "host-events.jsonl").read_text(encoding="utf-8").splitlines()]
    calls = [e["item"] for e in events if e.get("type") == "item.completed"
             and e.get("item", {}).get("type") == "mcp_tool_call"]
    expected = {"nx_get_capabilities"} if args.probe else {
        "nx_bridge_status", "nx_get_capabilities", "nx_bridge_ping", "nx_takeover", "nx_get_session", "nx_inspect_part",
        "nx_prepare_operation", "nx_execute_plan", "nx_verify_part", "nx_save_copy",
        "nx_inspect_sketch", "nx_return"}
    observed = {c.get("tool") for c in calls}
    summary = {"started_utc": started, "finished_utc": datetime.now(timezone.utc).isoformat(),
               "client": "Codex CLI", "client_version": subprocess.check_output([str(executable), "--version"], text=True).strip(),
               "model": args.model, "probe_only": args.probe,
               "exit_code": process.returncode, "tool_call_count": len(calls),
               "observed_tools": sorted(observed), "missing_tools": sorted(expected - observed),
               "events_file": str(output / "host-events.jsonl"),
               "tool_chain_observed": process.returncode == 0 and expected <= observed,
               "note": "Observed tool chain is not sufficient for acceptance; inspect returned NX evidence and final state."}
    if launch:
        def payload(call: dict) -> dict:
            result = call.get("result") or {}
            value = result.get("structured_content", result.get("structuredContent"))
            if isinstance(value, dict):
                return value
            for content in result.get("content", []):
                if content.get("type") == "text":
                    try:
                        return json.loads(content["text"])
                    except (ValueError, TypeError):
                        pass
            return {}

        operations = {}
        for call in calls:
            value = payload(call)
            if value.get("operation_id"):
                operations[value["operation_id"]] = value
        save = operations.get("host-save", {})
        save_result = save.get("result") or {}
        returns = [payload(c) for c in calls if c["tool"] == "nx_return"]
        statuses = [payload(c) for c in calls if c["tool"] == "nx_bridge_status"]
        pings = [payload(c) for c in calls if c["tool"] == "nx_bridge_ping"]
        parts = [payload(c) for c in calls if c["tool"] == "nx_inspect_part"]
        final_part = parts[-1] if parts else {}
        expressions = {e["name"]: e.get("value") for e in final_part.get("expressions", [])}
        acceptance = {
            "tool_chain_observed": summary["tool_chain_observed"],
            "dedicated_pid_matches": bool(statuses) and statuses[0].get("pid") == launch["pid"],
            "dispatch_on_main_thread": bool(pings) and pings[-1].get("on_main_thread") is True,
            "p12_verified": operations.get("host-verify-p12", {}).get("state") == "SUCCEEDED"
                and (operations.get("host-verify-p12", {}).get("result") or {}).get("verified") is True,
            "p11_verified": operations.get("host-verify-p11", {}).get("state") == "SUCCEEDED"
                and (operations.get("host-verify-p11", {}).get("result") or {}).get("verified") is True,
            "saved_and_reopened": save.get("state") == "SUCCEEDED"
                and save_result.get("saved") is True and save_result.get("reopened") is True,
            "saved_file_exists": saved_path.is_file(),
            "input_file_bytes_unchanged": Path(launch["copied_part"]).read_bytes() == input_before,
            "original_source_bytes_unchanged": Path(launch["source_part"]).read_bytes() == source_before,
            "reopened_dimensions_match": str(expressions.get("p12")) in {"36", "36.0"}
                and str(expressions.get("p11")) in {"71", "71.0"},
            "human_handoff": bool(returns) and returns[-1].get("ownership_state") == "HUMAN_OWNED"
                and returns[-1].get("safe_for_human") is True,
            "final_writes_not_halted": bool(statuses) and statuses[-1].get("writes_halted") is False,
        }
        if args.window_report:
            window = json.loads(args.window_report.read_text(encoding="utf-8"))
            acceptance["nx_window_thread_matches_dispatch"] = bool(pings) and str(pings[-1].get("callback_thread_id")) == str(window.get("nx_window_thread_id")) and window.get("pid") == launch["pid"]
        summary.update(acceptance=acceptance, result="PASS" if all(acceptance.values()) else "FAIL",
                       saved_path=str(saved_path), final_expressions=expressions,
                       source_comparison="direct byte equality in memory; no digest or hash")
    (output / "host-summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(summary, ensure_ascii=False))
    return 0 if summary.get("result", "PASS" if summary["tool_chain_observed"] else "FAIL") == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
