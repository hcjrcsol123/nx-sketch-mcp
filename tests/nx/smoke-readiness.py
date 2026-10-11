"""Verify bounded NX connection preparation and reuse through real MCP stdio.

Only readiness and bridge status are called. EOF shuts down the test's MCP
hosts; NX is never closed, taken over, or asked to read/change a model.
Results stay in memory and are printed to stdout as JSON.
"""

from __future__ import annotations

import argparse
import json
import math
import os
from pathlib import Path
import sys
import time

PROJECT_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT_ROOT / "tests" / "m2"))
from stdio_client import StdioMcpProcess, assert_stdout_jsonl_only

PROTOCOL_VERSION = "2025-11-25"
MAX_COLD_ATTEMPTS = 3


def tool(client: StdioMcpProcess, name: str, timeout: float) -> dict:
    if name not in {"nx_ensure_ready", "nx_bridge_status"}:
        raise ValueError("Only connection preparation/status tools are permitted")
    reply = client.request("tools/call", {"name": name, "arguments": {}}, timeout=timeout)
    result = reply.get("result")
    if not isinstance(result, dict):
        raise RuntimeError("Tool response omitted its result object")
    payload = result.get("structuredContent")
    if not isinstance(payload, dict):
        content = result.get("content", [])
        payload = next((json.loads(item["text"]) for item in content
                        if item.get("type") == "text"), None)
    if not isinstance(payload, dict):
        raise RuntimeError("Tool response omitted structured diagnostic data")
    if result.get("isError"):
        return {"state": "FAILED", "diagnostic": payload}
    return payload


def start_host(command: list[str], timeout: float, clients: list) -> tuple:
    env = os.environ.copy()
    env["PYTHONUTF8"] = "1"
    env["PYTHONDONTWRITEBYTECODE"] = "1"
    client = StdioMcpProcess(command, cwd=str(PROJECT_ROOT), env=env)
    clients.append(client)
    response = client.request("initialize", {
        "protocolVersion": PROTOCOL_VERSION, "capabilities": {},
        "clientInfo": {"name": "nx-readiness-smoke", "version": "1"},
    }, timeout=timeout)
    initialized = response.get("result", {})
    if initialized.get("protocolVersion") != PROTOCOL_VERSION:
        raise RuntimeError("MCP protocol negotiation failed")
    client.notification("notifications/initialized")
    client.assert_quiet(duration=0.2)
    listing = client.request("tools/list", timeout=timeout).get("result", {}).get("tools")
    if not isinstance(listing, list):
        raise RuntimeError("MCP tool discovery failed")
    names = {entry.get("name") for entry in listing}
    if not {"nx_ensure_ready", "nx_bridge_status"}.issubset(names):
        raise RuntimeError("Readiness/status tools are missing")
    return client, initialized.get("serverInfo", {}).get("version"), len(listing)


def finish_host(client: StdioMcpProcess) -> None:
    client.close_stdin()
    if client.wait(timeout=5.0) != 0:
        raise RuntimeError("MCP host did not exit cleanly after EOF")
    assert_stdout_jsonl_only(client.all_stdout_bytes())


def check_status(status: dict) -> int:
    if status.get("ownership_state") != "HUMAN_OWNED":
        raise RuntimeError("Non-cold ownership condition: acceptance requires HUMAN_OWNED; ownership was left unchanged")
    if status.get("nx_api_calls") != 0 or status.get("active_operations") != 0:
        raise RuntimeError("Connection-only acceptance requires zero NX API calls and active operations")
    pid = status.get("pid")
    if isinstance(pid, bool) or not isinstance(pid, int) or pid <= 0:
        raise RuntimeError("Bridge did not report a positive NX PID")
    return pid


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--host", type=Path, default=PROJECT_ROOT / "tools" / "mcp_stdio.py")
    parser.add_argument("--python", default=sys.executable)
    args = parser.parse_args(argv)
    if not args.config.is_absolute() or not args.config.is_file():
        parser.error("--config must be an existing absolute path")
    if not args.host.is_file():
        parser.error("--host must be an existing MCP entry script")
    clients: list[StdioMcpProcess] = []
    report = {"result": "FAIL", "first_result_state": None, "ready_pid": None,
              "reuse_same_pid": False, "warm_seconds": None,
              "ownership_state": None, "nx_api_calls": None, "active_operations": None}
    step = "read configuration"
    try:
        config = json.loads(args.config.read_text(encoding="utf-8-sig"))
        launch = config.get("launch")
        budget = float(launch.get("ready_timeout_seconds", 30.0)) if launch else min(float(config.get("timeout_seconds", 5.0)), 2.0)
        if not math.isfinite(budget) or not 0 < budget <= 30:
            raise ValueError("Invalid readiness budget")
        timeout = budget + 10.0
        report["call_timeout_seconds"] = timeout
        command = [args.python, "-B", str(args.host.resolve()), "--config", str(args.config)]
        step = "first host initialize/discovery"
        client, version, count = start_host(command, timeout, clients)
        report.update(version=version, tool_count=count)
        step = "cold readiness"
        started = time.monotonic()
        attempts = []
        for attempt in range(MAX_COLD_ATTEMPTS):
            call_started = time.monotonic()
            ready = tool(client, "nx_ensure_ready", timeout)
            attempts.append({"state": ready.get("state"), "seconds": round(time.monotonic() - call_started, 3)})
            report["cold_attempts"] = attempts
            report["cold_seconds"] = round(time.monotonic() - started, 3)
            if attempt == 0:
                report["first_result_state"] = ready.get("state")
            if ready.get("state") != "STARTING":
                break
            if attempt + 1 < MAX_COLD_ATTEMPTS:
                time.sleep(0.5)
        report["first_readiness"] = ready
        if ready.get("state") != "READY" or ready.get("ready") is not True:
            raise RuntimeError("Readiness did not reach READY within the finite attempt limit")
        status = ready.get("bridge_status", {})
        report.update({key: status.get(key) for key in ("ownership_state", "nx_api_calls", "active_operations")})
        report["cold_launch_observed"] = ready.get("reused") is False
        pid = check_status(status)
        report["ready_pid"] = pid
        step = "same host warm readiness"
        started = time.monotonic()
        warm = tool(client, "nx_ensure_ready", timeout)
        report["warm_seconds"] = round(time.monotonic() - started, 3)
        report["same_host_readiness"] = warm
        if warm.get("state") != "READY" or warm.get("reused") is not True or check_status(warm.get("bridge_status", {})) != pid:
            raise RuntimeError("Same host did not reuse the ready NX process")
        step = "first host EOF shutdown"
        finish_host(client)
        step = "fresh host initialize/discovery"
        fresh, fresh_version, fresh_count = start_host(command, timeout, clients)
        if (fresh_version, fresh_count) != (version, count):
            raise RuntimeError("Fresh host configuration changed")
        step = "fresh host warm readiness"
        started = time.monotonic()
        reused = tool(fresh, "nx_ensure_ready", timeout)
        report["fresh_host_seconds"] = round(time.monotonic() - started, 3)
        report["fresh_host_readiness"] = reused
        if reused.get("state") != "READY" or reused.get("reused") is not True or check_status(reused.get("bridge_status", {})) != pid:
            raise RuntimeError("Fresh host did not reuse the ready NX process")
        step = "final bridge status"
        final = tool(fresh, "nx_bridge_status", timeout)
        report["final_status"] = final
        if check_status(final) != pid:
            raise RuntimeError("Final bridge process changed")
        report["reuse_same_pid"] = True
        step = "fresh host EOF shutdown"
        finish_host(fresh)
        report["result"] = "PASS"
        return 0
    except Exception as exc:
        report.update(step=step, error_type=type(exc).__name__, error=str(exc))
        return 2
    finally:
        # The client cleanup acts exclusively on child MCP PIDs, never on NX.
        for client in clients:
            client.terminate_own_process()
        print(json.dumps(report, ensure_ascii=True, indent=2))


if __name__ == "__main__":
    raise SystemExit(main())
