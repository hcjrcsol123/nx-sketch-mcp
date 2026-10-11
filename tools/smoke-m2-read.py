"""Verify M2-3/4 through a real MCP stdio child in an existing dedicated NX.

Only use with launch-m2-read-test.ps1's fresh saved independent copy. This test
reads model data, returns ownership in finally, and never saves or edits geometry.
"""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import sys

PROJECT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT / "tests" / "m2"))
from stdio_client import StdioMcpProcess, assert_stdout_jsonl_only


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--launch-report", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--window-report", type=Path, help="optional same-process NX window/thread observation")
    args = parser.parse_args()
    if args.output.exists():
        parser.error("refusing to overwrite smoke evidence")
    launch = json.loads(args.launch_report.read_text(encoding="utf-8"))
    client = StdioMcpProcess(
        [sys.executable, "-B", str(PROJECT / "tools" / "mcp_stdio.py"), "--config", launch["config_path"]],
        cwd=str(PROJECT), env={**os.environ, "PYTHONUTF8": "1"},
    )
    report = {"captured_utc": datetime.now(timezone.utc).isoformat(), "nx_pid": launch["pid"],
              "copy_path": launch["copied_part"], "steps": [], "result": "FAIL"}
    binding = None
    failure = None

    def call(name: str, arguments: dict, expected_error: str | None = None) -> dict:
        response = client.request("tools/call", {"name": name, "arguments": arguments}, timeout=130)
        result = response.get("result", {})
        payload = result.get("structuredContent", {})
        report["steps"].append({"tool": name, "response": response})
        if expected_error:
            if result.get("isError") is not True or payload.get("error", {}).get("code") != expected_error:
                raise AssertionError(f"{name}: expected {expected_error}; got {payload}")
        elif result.get("isError") is not False:
            raise AssertionError(f"{name}: {payload or response}")
        return payload

    try:
        initialized = client.request("initialize", {
            "protocolVersion": "2025-11-25", "capabilities": {},
            "clientInfo": {"name": "m2-read-smoke", "version": "1"},
        })
        assert initialized.get("result", {}).get("protocolVersion") == "2025-11-25"
        client.notification("notifications/initialized")
        listing = client.request("tools/list")
        tools = {item["name"] for item in listing["result"]["tools"]}
        assert tools >= {"nx_get_capabilities", "nx_bridge_status", "nx_bridge_ping", "nx_takeover",
                         "nx_return", "nx_get_session", "nx_inspect_part", "nx_inspect_sketch"}
        report["tools"] = sorted(tools)
        status = call("nx_bridge_status", {})
        assert status["pid"] == launch["pid"] and status["ownership_state"] == "HUMAN_OWNED"
        report["initial_capabilities"] = call("nx_get_capabilities", {})
        ping = call("nx_bridge_ping", {"nonce": "M2-read-copy"})
        assert ping["on_main_thread"] is True
        if args.window_report:
            window = json.loads(args.window_report.read_text(encoding="utf-8"))
            assert window["pid"] == launch["pid"]
            assert ping["callback_thread_id"] == status["main_thread_id"] == window["nx_window_thread_id"]
            report["nx_window_thread_id"] = window["nx_window_thread_id"]
        call("nx_get_session", {"ownership_id": "unowned", "binding_epoch": 1}, "AUTHORIZATION_REQUIRED")
        takeover_args = {"expected_pid": launch["pid"], "expected_part_path": launch["copied_part"],
                         "artifact_scope": "saved_independent_copy", "authorize": True}
        call("nx_takeover", {**takeover_args, "expected_pid": launch["pid"] + 1}, "SESSION_MISMATCH")
        owned = call("nx_takeover", takeover_args)
        binding = {"ownership_id": owned["ownership_id"], "binding_epoch": owned["binding_epoch"]}
        assert owned["ownership_state"] == "AI_OWNED"
        report["owned_capabilities"] = call("nx_get_capabilities", {})
        session = call("nx_get_session", binding)
        assert Path(session["part_path"]) == Path(launch["copied_part"])
        assert session["nx_build"] and session["part_units"]
        part = call("nx_inspect_part", binding)
        for field in ("bodies", "features", "sketches", "expressions", "dependencies"):
            assert isinstance(part[field], list)
        assert part["bodies"] and part["features"] and part["sketches"] and part["expressions"]
        revision = part["revision_id"]
        again = call("nx_inspect_part", binding)
        assert again["revision_id"] == revision
        sketches = []
        for sketch in part["sketches"]:
            detail = call("nx_inspect_sketch", {**binding, "sketch_id": sketch["journal_id"]})
            assert detail["sketch"]["journal_id"] == sketch["journal_id"]
            assert detail["revision_id"] == revision
            sketches.append({"id": sketch["journal_id"], "geometry": len(detail["sketch"]["geometry"]),
                             "constraints": len(detail["sketch"]["constraints"])})
        call("nx_inspect_sketch", {**binding, "sketch_id": "missing-sketch"}, "SKETCH_NOT_FOUND")
        call("nx_inspect_part", {**binding, "binding_epoch": binding["binding_epoch"] + 1}, "AUTHORIZATION_REQUIRED")
        returned = call("nx_return", binding)
        assert returned["ownership_state"] == "HUMAN_OWNED" and returned["safe_for_human"] is True
        old_binding = binding
        binding = None
        call("nx_inspect_part", old_binding, "AUTHORIZATION_REQUIRED")
        call("nx_return", old_binding, "AUTHORIZATION_REQUIRED")
        final_status = call("nx_bridge_status", {})
        assert final_status["ownership_state"] == "HUMAN_OWNED"
        report["summary"] = {"nx_build": session["nx_build"], "units": session["part_units"],
                             "revision_id": revision,
                             "counts": {key: len(part[key]) for key in ("bodies", "features", "sketches", "expressions", "dependencies")},
                             "sketches": sketches, "takeover_read_effects": {key: owned.get(key) for key in
                                 ("is_modified_before_read", "is_modified_after_read", "dirty_changed_by_read")},
                             "support": part["support"], "final_state": final_status["ownership_state"]}
        report["result"] = "PASS"
    except Exception as exc:
        failure = f"{type(exc).__name__}: {exc}"
        report["failure"] = failure
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
    print(json.dumps({key: report[key] for key in ("result", "summary", "failure", "cleanup_failure", "shutdown_failure")
                      if key in report}, ensure_ascii=False))
    return 0 if report["result"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
