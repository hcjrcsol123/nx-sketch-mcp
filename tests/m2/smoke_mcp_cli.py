"""Reproduce MCP-to-helper-to-bridge smoke checks against an existing NX pipe.

This command never launches NX or reads token contents. It launches one MCP
stdio child from the supplied public runtime config and closes only that child.
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import sys
import time

from stdio_client import StdioMcpProcess, assert_stdout_jsonl_only
from test_mcp_stdio import EXPECTED_TOOLS, PROTOCOL_VERSION, extract_structured_result


PROJECT_ROOT = Path(__file__).resolve().parents[2]


def tool_result(client: StdioMcpProcess, name: str, arguments: dict, timeout: float) -> dict:
    response = client.request("tools/call", {"name": name, "arguments": arguments}, timeout=timeout)
    result = response.get("result")
    if not isinstance(result, dict) or result.get("isError") is not False:
        raise RuntimeError("MCP tool failed: " + name)
    payload = extract_structured_result(result)
    if not isinstance(payload, dict):
        raise RuntimeError("MCP tool returned no structured object: " + name)
    return payload


def validate_probe(probe: dict, nonce: str, main_thread_id: str) -> None:
    if probe.get("nonce") != nonce:
        raise RuntimeError("probe nonce did not round-trip exactly")
    for key in ("request_thread_id", "callback_thread_id", "main_thread_id"):
        if not isinstance(probe.get(key), str) or not probe[key]:
            raise RuntimeError("probe omitted an OS thread id")
    if probe.get("on_main_thread") is not True:
        raise RuntimeError("probe callback did not report the bridge main thread")
    if probe.get("callback_thread_id") != main_thread_id or probe.get("main_thread_id") != main_thread_id:
        raise RuntimeError("probe callback/main thread differs from the NX bridge status")
    if probe.get("nx_api_calls") != 0:
        raise RuntimeError("bridge probe reported a nonzero NX API call count")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path, required=True, help="absolute prepared MCP runtime config")
    parser.add_argument("--expected-nx-pid", type=int, required=True)
    parser.add_argument("--timeout-seconds", type=float, default=10.0)
    parser.add_argument("--output", type=Path, help="optional UTF-8 JSON report path")
    args = parser.parse_args(argv)
    if not args.config.is_absolute() or not args.config.is_file():
        parser.error("--config must be an existing absolute path")
    if args.expected_nx_pid <= 0:
        parser.error("--expected-nx-pid must be positive")
    if not 0.1 <= args.timeout_seconds <= 120:
        parser.error("--timeout-seconds must be between 0.1 and 120")

    env = os.environ.copy()
    existing = env.get("PYTHONPATH", "")
    paths = [str(PROJECT_ROOT / "src"), str(PROJECT_ROOT)]
    if existing:
        paths.append(existing)
    env["PYTHONPATH"] = os.pathsep.join(paths)
    env["PYTHONUTF8"] = "1"
    client = StdioMcpProcess(
        [sys.executable, "-B", "-m", "nx_sketch_mcp", "--config", str(args.config)],
        cwd=str(PROJECT_ROOT), env=env,
    )
    started = time.monotonic()
    step = "initialize"
    try:
        initialized = client.request("initialize", {
            "protocolVersion": PROTOCOL_VERSION,
            "capabilities": {},
            "clientInfo": {"name": "m2-smoke-mcp-cli", "version": "1"},
        }, timeout=args.timeout_seconds)
        init_result = initialized.get("result")
        if not isinstance(init_result, dict) or init_result.get("protocolVersion") != PROTOCOL_VERSION:
            raise RuntimeError("MCP initialize did not negotiate protocol 2025-11-25")
        client.notification("notifications/initialized")
        client.assert_quiet(duration=0.2)

        step = "tools/list"
        listing = client.request("tools/list", timeout=args.timeout_seconds)
        tools = listing.get("result", {}).get("tools")
        names = {item.get("name") for item in tools if isinstance(item, dict)} if isinstance(tools, list) else set()
        if names != EXPECTED_TOOLS:
            raise RuntimeError("MCP tool discovery did not match the M2 tool set")

        step = "nx_get_capabilities"
        capabilities = tool_result(client, "nx_get_capabilities", {}, args.timeout_seconds)
        if capabilities.get("bridge_configured") is not True:
            raise RuntimeError("runtime config did not report a prepared bridge connection")
        if capabilities.get("model_access") != {"read": False, "write": False, "control": False}:
            raise RuntimeError("static capabilities did not keep model access disabled")

        step = "nx_bridge_status"
        status = tool_result(client, "nx_bridge_status", {}, args.timeout_seconds)
        if status.get("state") != "loaded" or status.get("pid") != args.expected_nx_pid:
            raise RuntimeError("bridge status did not identify the expected loaded NX process")
        main_thread_id = status.get("main_thread_id")
        if not isinstance(main_thread_id, str) or not main_thread_id:
            raise RuntimeError("bridge status omitted its main OS thread id")
        if status.get("nx_api_calls") != 0:
            raise RuntimeError("bridge status reported a nonzero NX API call count")

        probes = []
        nonces = ("草图-🧪", "界🙂" * 64)
        if len(nonces[1]) != 128:
            raise RuntimeError("128-codepoint nonce fixture is malformed")
        for nonce in nonces:
            step = "nx_bridge_ping"
            probe = tool_result(client, "nx_bridge_ping", {"nonce": nonce}, args.timeout_seconds)
            validate_probe(probe, nonce, main_thread_id)
            probes.append(probe)

        step = "EOF shutdown"
        client.close_stdin()
        exit_code = client.wait(timeout=5.0)
        raw_stdout = client.all_stdout_bytes()
        line_count = assert_stdout_jsonl_only(raw_stdout)
        if exit_code != 0:
            raise RuntimeError("MCP child did not exit cleanly after stdin EOF")

        report = {
            "result": "PASS",
            "evidence_level": "NX-loaded MCP stdio to production helper and bridge smoke",
            "protocol_version": PROTOCOL_VERSION,
            "server_info": init_result.get("serverInfo"),
            "discovered_tools": sorted(names),
            "expected_nx_pid": args.expected_nx_pid,
            "bridge_status": status,
            "dispatch_probes": probes,
            "mcp_child_pid": client.process.pid,
            "mcp_exit_code_after_eof": exit_code,
            "stdout_jsonl_lines": line_count,
            "stdout_bytes": len(raw_stdout),
            "elapsed_ms": round((time.monotonic() - started) * 1000.0, 3),
        }
        output = json.dumps(report, ensure_ascii=False, indent=2) + "\n"
        if args.output:
            output_path = args.output.resolve()
            output_path.parent.mkdir(parents=True, exist_ok=True)
            output_path.write_text(output, encoding="utf-8", newline="\n")
        # Keep an interactive Windows console's active code page from turning a
        # successful Unicode probe into a reporting failure. The saved file above
        # remains UTF-8 with the original nonce characters.
        print(json.dumps(report, ensure_ascii=True, indent=2))
        return 0
    except Exception as exc:
        # Avoid writing raw child output or config/token path details on failure.
        failure = {
            "result": "FAIL",
            "evidence_level": "NX-loaded MCP stdio smoke",
            "step": step,
            "error_type": type(exc).__name__,
        }
        print(json.dumps(failure, ensure_ascii=False), file=sys.stderr)
        return 2
    finally:
        client.terminate_own_process()


if __name__ == "__main__":
    raise SystemExit(main())
