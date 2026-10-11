"""Read-only status/dispatch smoke check for a bridge already loaded in NX.

This script never launches NX. It connects to the named pipe supplied by the
caller and sends only bridge.status and bridge.dispatch_probe.
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
import time
import uuid
from pathlib import Path

from stdio_client import ProtocolFailure, StdioMcpProcess


def parse_reply(reply: dict, request_id: str) -> dict:
    if reply.get("id") != request_id:
        raise ProtocolFailure("helper response id mismatch")
    if "error" in reply:
        raise ProtocolFailure("helper reported an error for " + request_id + ": " + safe_error(reply["error"]))
    result = reply.get("result")
    if not isinstance(result, dict) or result.get("ok") is not True:
        raise ProtocolFailure("helper returned no successful result for " + request_id)
    return result


def safe_error(value) -> str:
    if isinstance(value, dict):
        # Deliberately retain only non-secret classification fields.
        fields = {key: value.get(key) for key in ("code", "type", "name", "message") if key in value}
        return json.dumps(fields, ensure_ascii=False, separators=(",", ":"))[:500]
    return str(value)[:500]


def main(argv: list[str] | None = None) -> int:
    project = Path(__file__).resolve().parents[2]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pipe-name", required=True, help="bare local named pipe name")
    parser.add_argument("--token-file", required=True, type=Path)
    parser.add_argument("--helper-path", type=Path,
                        default=project / "workspace" / "build" / "current" / "M2" / "M2JsonlHelper.exe")
    parser.add_argument("--timeout-seconds", type=float, default=5.0)
    parser.add_argument("--nonce", default="m2-smoke-" + uuid.uuid4().hex)
    args = parser.parse_args(argv)
    if not 0.1 <= args.timeout_seconds <= 120:
        parser.error("--timeout-seconds must be between 0.1 and 120")
    if not (1 <= len(args.nonce) <= 128):
        parser.error("--nonce must contain between 1 and 128 characters")
    if not args.token_file.is_absolute() or not args.token_file.is_file():
        parser.error("--token-file must be an existing absolute path")
    if not args.helper_path.is_absolute() or not args.helper_path.is_file():
        parser.error("--helper-path must be an existing absolute path")

    command = [str(args.helper_path), "--pipe-name", args.pipe_name,
               "--token-file", str(args.token_file)]
    helper = StdioMcpProcess(command, cwd=str(project))
    started = time.monotonic()
    try:
        status_reply = helper.exchange(
            {"id": "status", "command": "bridge.status", "params": {}},
            expected_id="status", timeout=args.timeout_seconds)
        status = parse_reply(status_reply, "status")
        probe_reply = helper.exchange(
            {"id": "probe", "command": "bridge.dispatch_probe", "params": {"nonce": args.nonce}},
            expected_id="probe", timeout=args.timeout_seconds)
        probe = parse_reply(probe_reply, "probe")
        for key in ("request_thread_id", "callback_thread_id", "main_thread_id"):
            if not isinstance(probe.get(key), str) or not probe[key]:
                raise ProtocolFailure("probe omitted " + key)
        if probe.get("nonce") != args.nonce or probe.get("on_main_thread") is not True:
            raise ProtocolFailure("dispatch probe nonce/thread contract failed")
        if status.get("nx_api_calls") != 0 or probe.get("nx_api_calls") != 0:
            raise ProtocolFailure("bridge reported a nonzero nx_api_calls value")
        if probe.get("callback_thread_id") != probe.get("main_thread_id"):
            raise ProtocolFailure("callback thread differs from bridge main thread")

        helper.close_stdin()
        exit_code = helper.wait(timeout=5.0)
        raw = helper.all_stdout_bytes()
        line_count = 0
        if raw:
            text = raw.decode("utf-8", errors="strict")
            line_count = len(text.splitlines())
        report = {
            "level": "bridge smoke check; NX-loaded only if caller connected an NX-loaded pipe",
            "pipe_name": args.pipe_name,
            "status": status,
            "dispatch_probe": probe,
            "helper_exit_code": exit_code,
            "helper_stdout_jsonl_lines": line_count,
            "elapsed_ms": round((time.monotonic() - started) * 1000, 3),
        }
        print(json.dumps(report, ensure_ascii=False, indent=2))
        return 0 if exit_code == 0 else 3
    except (ProtocolFailure, TimeoutError, subprocess.TimeoutExpired, OSError, UnicodeError) as exc:
        print(json.dumps({"level": "bridge smoke check", "pipe_name": args.pipe_name,
                          "result": "FAIL", "error": str(exc)[:500]}, ensure_ascii=False), file=sys.stderr)
        return 2
    finally:
        helper.terminate_own_process()


if __name__ == "__main__":
    raise SystemExit(main())
