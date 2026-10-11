"""Offline configuration and optional live bridge diagnostics for M6."""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import subprocess
import sys
from typing import Any


PROJECT_ROOT = Path(__file__).resolve().parents[1]
SOURCE_ROOT = PROJECT_ROOT / "src"
if str(SOURCE_ROOT) not in sys.path:
    sys.path.insert(0, str(SOURCE_ROOT))

from nx_sketch_mcp.config import BridgeConfig, ConfigError, config_from_object  # noqa: E402
from nx_sketch_mcp.jsonutil import loads_unique  # noqa: E402


PROTOCOL_VERSION = "2025-11-25"
DIAGNOSTIC_NONCE = "m6-diagnostics-ping"
_SAFE_CODE = re.compile(r"^[A-Za-z0-9_.-]{1,80}$", re.ASCII)
_OWNERSHIP_STATES = {"HUMAN_OWNED", "TAKEOVER_CHECK", "AI_OWNED", "RETURNING", "UNKNOWN_FROZEN", "STOPPING"}


def _check(status: str, message: str, **details: Any) -> dict[str, Any]:
    result: dict[str, Any] = {"status": status, "message": message}
    result.update(details)
    return result


def _resolve_file(value: Any, field: str) -> tuple[Path | None, str | None]:
    if not isinstance(value, str) or not value:
        return None, f"{field} must be an absolute file path."
    path = Path(value)
    if not path.is_absolute():
        return None, f"{field} must be an absolute file path."
    try:
        return path.resolve(strict=False), None
    except (OSError, RuntimeError, ValueError):
        return None, f"{field} cannot be resolved as a local path."


def _local_file_check(path: Path | None, field: str, *, missing_status: str = "fail") -> dict[str, Any]:
    if path is None:
        return _check("skipped", f"{field} could not be checked until its path is configured.")
    try:
        exists = path.is_file()
    except OSError:
        exists = False
    if exists:
        return _check("pass", f"{field} exists.", path=str(path))
    return _check(
        missing_status,
        f"{field} is missing or is not a file.",
        path=str(path),
        suggestion=f"Build or correct the configured {field} path, then run this diagnostic again.",
    )


def _safe_config_problem(exc: ConfigError) -> str:
    """ConfigError messages contain field names, never configured values."""
    message = str(exc)
    if message.startswith("unsupported configuration key"):
        return "Configuration contains unsupported field names."
    if message.startswith("bridge configuration is incomplete"):
        missing = sorted({name for name in ("pipe_name", "token_file", "helper_path") if name in message})
        return "Bridge configuration is incomplete; missing: " + ", ".join(missing)
    for field in ("token_file", "helper_path", "pipe_name", "timeout_seconds"):
        if message.startswith(field + " "):
            return message[:240]
    if message.startswith("configuration"):
        return message[:240]
    return "Configuration fields do not match the local MCP schema."


def _mcp_request(request_id: int, method: str, params: dict[str, Any]) -> dict[str, Any]:
    return {"jsonrpc": "2.0", "id": request_id, "method": method, "params": params}


def _online_probe(config_path: Path, config: BridgeConfig) -> dict[str, Any]:
    init = _mcp_request(1, "initialize", {
        "protocolVersion": PROTOCOL_VERSION,
        "capabilities": {},
        "clientInfo": {"name": "nx-sketch-mcp-m6-diagnostics", "version": "1"},
    })
    initialized = {"jsonrpc": "2.0", "method": "notifications/initialized"}
    status_call = _mcp_request(2, "tools/call", {"name": "nx_bridge_status", "arguments": {}})
    ping_call = _mcp_request(3, "tools/call", {
        "name": "nx_bridge_ping",
        "arguments": {"nonce": DIAGNOSTIC_NONCE},
    })
    payload = "".join(json.dumps(item, separators=(",", ":")) + "\n" for item in (init, initialized, status_call, ping_call))
    timeout = max(8.0, 2 * config.timeout_seconds + 5.0)
    command = [sys.executable, "-B", str(PROJECT_ROOT / "tools" / "mcp_stdio.py"), "--config", str(config_path)]
    try:
        completed = subprocess.run(
            command,
            cwd=PROJECT_ROOT,
            input=payload,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=timeout,
            check=False,
        )
    except subprocess.TimeoutExpired:
        return {
            "status": "fail",
            "message": "The MCP host did not finish the status and ping checks within the configured bridge wait.",
            "suggestion": "Check the local NX bridge responsiveness and timeout_seconds, then retry online diagnostics.",
            "status_probe": _check("fail", "No complete status response was received."),
            "ping_probe": _check("fail", "No complete ping response was received."),
        }
    except OSError as exc:
        return {
            "status": "fail",
            "message": f"The MCP host could not be started ({type(exc).__name__}).",
            "suggestion": "Run the diagnostic with the supported Python installation and check the project launcher path.",
            "status_probe": _check("fail", "The status call was not sent."),
            "ping_probe": _check("fail", "The ping call was not sent."),
        }

    replies: dict[int, dict[str, Any]] = {}
    malformed = False
    for line in completed.stdout.splitlines():
        try:
            value = json.loads(line)
        except json.JSONDecodeError:
            malformed = True
            continue
        if isinstance(value, dict) and isinstance(value.get("id"), int):
            replies[value["id"]] = value

    status_result = _probe_result(replies.get(2), "status")
    ping_result = _probe_result(replies.get(3), "ping")
    status_pid = status_result.get("pid")
    ping_pid = ping_result.get("pid")
    if status_pid is not None and ping_pid is not None and status_pid != ping_pid:
        ping_result = _check(
            "fail", "The bridge ping and status calls reported different NX process IDs.",
            **{key: value for key, value in ping_result.items() if key not in {"status", "message", "suggestion"}},
            suggestion="Check that the configured pipe and token belong to the intended NX session, then retry.",
        )
    status_thread = status_result.get("main_thread_id")
    callback_thread = ping_result.get("callback_thread_id")
    if status_thread is not None and callback_thread is not None and status_thread != callback_thread:
        ping_result = _check(
            "fail", "The bridge ping callback thread does not match the main thread reported by status.",
            **{key: value for key, value in ping_result.items() if key not in {"status", "message", "suggestion"}},
            suggestion="Check NX session responsiveness and bridge dispatch; do not treat this session as ready for model work.",
        )
    if status_result["status"] == "pass" and ping_result["status"] == "pass":
        return {
            "status": "pass",
            "message": "The existing bridge status and ping tools both responded.",
            "status_probe": status_result,
            "ping_probe": ping_result,
        }
    if status_result["status"] == "warn" and ping_result["status"] == "pass":
        return {
            "status": "warn",
            "message": "Bridge connectivity passed, but its status reports that model writes are unavailable.",
            "suggestion": status_result.get("suggestion"),
            "status_probe": status_result,
            "ping_probe": ping_result,
        }
    suggestions = [item.get("suggestion") for item in (status_result, ping_result) if item.get("suggestion")]
    detail = "The bridge status and/or ping call returned an error."
    if completed.returncode != 0 and not any(item.get("code") for item in (status_result, ping_result)):
        detail = "The MCP host exited before completing the bridge checks."
    if malformed:
        detail += " Its stdout also contained a line that was not JSON."
    return {
        "status": "fail",
        "message": detail,
        "suggestion": suggestions[0] if suggestions else "Check the configured local NX bridge, then retry online diagnostics.",
        "status_probe": status_result,
        "ping_probe": ping_result,
    }


def _probe_result(reply: dict[str, Any] | None, name: str) -> dict[str, Any]:
    if reply is None:
        return _check(
            "fail",
            f"No nx_bridge_{name} response was received.",
            suggestion="Check MCP host startup and configuration, then retry.",
        )
    if "error" in reply:
        error = reply.get("error")
        return _check(
            "fail",
            f"The nx_bridge_{name} MCP request failed.",
            code=f"MCP_{error.get('code')}" if isinstance(error, dict) and isinstance(error.get("code"), int) else "MCP_ERROR",
            suggestion="Check the local MCP host configuration and startup output.",
        )
    result = reply.get("result")
    if not isinstance(result, dict):
        return _check("fail", f"The nx_bridge_{name} response has no result object.")
    structured = result.get("structuredContent")
    if result.get("isError") is True:
        error = structured.get("error") if isinstance(structured, dict) else None
        code_value = error.get("code") if isinstance(error, dict) else None
        code = code_value if isinstance(code_value, str) and _SAFE_CODE.fullmatch(code_value) else "BRIDGE_ERROR"
        advice = {
            "BRIDGE_NOT_READY": "Load the built M2Entry.dll in the intended NX session so it creates the configured token file, then retry.",
            "BRIDGE_UNCONFIGURED": "Set pipe_name, token_file, and helper_path in the local JSON configuration.",
            "BRIDGE_UNAVAILABLE": "Check that NX has loaded M2Entry.dll and that the helper path, pipe name, and session token path match this NX session.",
            "BRIDGE_TIMEOUT": "Check that the local NX session is responsive and increase timeout_seconds only if the bridge operation normally needs longer.",
            "BRIDGE_PROTOCOL_ERROR": "Check that the helper and NX entry assembly were built from the same project revision.",
        }.get(code, "Check the local NX bridge and helper diagnostics; this report does not capture token or model data.")
        return _check("fail", f"The nx_bridge_{name} tool reported {code}.", code=code, suggestion=advice)
    if not isinstance(structured, dict):
        return _check("fail", f"The nx_bridge_{name} response did not include structured content.")
    details = _safe_probe_fields(name, structured)
    if name == "ping":
        if structured.get("nonce") != DIAGNOSTIC_NONCE:
            return _check(
                "fail", "The bridge ping did not echo the diagnostic nonce.", **details,
                suggestion="Check the helper and bridge build versions, then retry.",
            )
        if structured.get("on_main_thread") is not True:
            return _check(
                "fail", "The bridge ping callback did not confirm the NX main thread.", **details,
                suggestion="Check NX session responsiveness and bridge dispatch; do not treat this session as ready for model work.",
            )
        if "callback_thread_id" not in details:
            return _check(
                "fail", "The bridge ping did not return a usable callback thread ID.", **details,
                suggestion="Check that the loaded NX entry and helper were built from the same project revision.",
            )
    if name == "status":
        if "pid" not in details:
            return _check(
                "warn", "The bridge responded, but status did not provide an NX process ID to identify the session.",
                **details,
                suggestion="Check that the configured helper and NX entry match this project version.",
            )
        if structured.get("writes_halted") is True:
            return _check(
                "warn", "The bridge responded, but writes_halted is true and model writes are blocked.",
                **details,
                suggestion="Inspect nx_bridge_status and query the reported operation ID with nx_get_operation before attempting another write.",
            )
        if structured.get("ownership_state") == "UNKNOWN_FROZEN":
            return _check(
                "warn", "The bridge reports UNKNOWN_FROZEN; model writes are unavailable.",
                **details,
                suggestion="Inspect nx_bridge_status and query the reported operation ID with nx_get_operation before attempting another write.",
            )
    return _check("pass", f"The nx_bridge_{name} tool responded successfully.", **details)


def _safe_probe_fields(name: str, values: dict[str, Any]) -> dict[str, Any]:
    allowed = (
        ("pid", "ownership_state", "writes_halted", "main_thread_id")
        if name == "status"
        else ("nonce", "pid", "on_main_thread", "callback_thread_id")
    )
    safe: dict[str, Any] = {}
    for key in allowed:
        value = values.get(key)
        if key == "pid":
            if isinstance(value, int) and not isinstance(value, bool) and value > 0:
                safe[key] = value
        elif key in {"main_thread_id", "callback_thread_id"}:
            if isinstance(value, int) and not isinstance(value, bool) and value > 0:
                safe[key] = str(value)
            elif isinstance(value, str) and value.isascii() and value.isdecimal() and 0 < len(value) <= 20:
                safe[key] = value
        elif key == "ownership_state":
            if value in _OWNERSHIP_STATES:
                safe[key] = value
        elif key in {"writes_halted", "on_main_thread"}:
            if isinstance(value, bool):
                safe[key] = value
        elif key == "nonce" and value == DIAGNOSTIC_NONCE:
            safe[key] = value
    return safe


def diagnose(args: argparse.Namespace) -> dict[str, Any]:
    requested_config = Path(args.config) if args.config else PROJECT_ROOT / "config" / "bridge.local.json"
    try:
        config_path = requested_config.resolve(strict=False)
    except (OSError, RuntimeError, ValueError):
        config_path = requested_config.absolute()
    checks: dict[str, dict[str, Any]] = {}

    python_path = Path(sys.executable)
    python_ok = sys.version_info >= (3, 14) and python_path.is_file()
    checks["python"] = _check(
        "pass" if python_ok else "fail",
        f"Python {sys.version_info.major}.{sys.version_info.minor}.{sys.version_info.micro} is "
        f"{'supported' if python_ok else 'outside the supported 3.14+ runtime'}.",
        executable=str(python_path),
        version=f"{sys.version_info.major}.{sys.version_info.minor}.{sys.version_info.micro}",
        suggestion=None if python_ok else "Run this diagnostic with Python 3.14 or newer.",
    )

    nx_value = args.nx_executable
    if nx_value is None:
        nx_base = os.environ.get("UGII_BASE_DIR")
        nx_value = str(Path(nx_base) / "NXBIN" / "ugraf.exe") if nx_base else None
    nx_path, nx_path_error = _resolve_file(nx_value, "NX executable") if nx_value else (None, None)
    nx_check = _local_file_check(nx_path, "NX executable", missing_status="fail")
    if nx_value is None:
        nx_check = _check(
            "warn",
            "UGII_BASE_DIR is not set and no NX executable path was provided.",
            suggestion="Set UGII_BASE_DIR or pass --nx-executable with the installed ugraf.exe path.",
        )
    elif nx_path_error:
        nx_check = _check("fail", nx_path_error, suggestion="Pass an absolute path to NXBIN\\ugraf.exe.")
    checks["nx_executable"] = nx_check

    checks["config_file"] = _local_file_check(config_path, "configuration file", missing_status="fail")
    raw: Any = None
    config: BridgeConfig | None = None
    raw_read = False
    if config_path.is_file():
        try:
            raw = loads_unique(config_path.read_text(encoding="utf-8"))
            raw_read = True
        except UnicodeError:
            checks["configuration"] = _check(
                "fail", "Configuration is not valid UTF-8 JSON.",
                suggestion="Save the local config as UTF-8 JSON and rerun the diagnostic.",
            )
        except json.JSONDecodeError as exc:
            checks["configuration"] = _check(
                "fail", f"Configuration JSON is invalid at line {exc.lineno}, column {exc.colno}.",
                suggestion="Correct the JSON syntax in the local config and rerun the diagnostic.",
            )
        except ValueError as exc:
            message = "Configuration contains a duplicate key or unsupported JSON value."
            if type(exc).__name__ == "DuplicateKeyError":
                message = "Configuration contains a duplicate JSON key."
            checks["configuration"] = _check(
                "fail", message, suggestion="Keep one value per configuration key and rerun the diagnostic.",
            )
        except OSError as exc:
            checks["configuration"] = _check(
                "fail", f"Configuration could not be read ({type(exc).__name__}).",
                suggestion="Check the config file path and local read access.",
            )

    helper_path: Path | None = None
    token_path: Path | None = None
    if raw_read:
        try:
            config = config_from_object(raw)
        except ConfigError as exc:
            checks["configuration"] = _check(
                "fail", f"Configuration is invalid: {_safe_config_problem(exc)}",
                suggestion="Correct the named field in the local JSON config, then rerun the diagnostic.",
            )
        else:
            if config.configured:
                checks["configuration"] = _check("pass", "Bridge configuration fields and paths are valid.", configured=True)
            else:
                checks["configuration"] = _check(
                    "warn", "An empty configuration is valid for offline MCP discovery; no bridge session is configured.",
                    configured=False,
                    suggestion="For an NX session, configure pipe_name, token_file, and helper_path.",
                )

        if isinstance(raw, dict) and "helper_path" in raw:
            helper_path, helper_error = _resolve_file(raw.get("helper_path"), "helper_path")
            if helper_error:
                checks["bridge_helper"] = _check(
                    "fail", helper_error, suggestion="Set helper_path to the absolute M2JsonlHelper.exe path.",
                )
            else:
                checks["bridge_helper"] = _local_file_check(helper_path, "bridge helper")

        if isinstance(raw, dict) and "token_file" in raw:
            token_path, token_error = _resolve_file(raw.get("token_file"), "token_file")
            if token_error:
                checks["token_file"] = _check(
                    "fail", token_error, suggestion="Set token_file to the absolute path used by this NX session.",
                )
            elif token_path is not None and token_path.is_file():
                checks["token_file"] = _check("pass", "The configured token file exists; its contents were not read.")
            elif token_path is not None and token_path.exists():
                checks["token_file"] = _check(
                    "fail", "The configured token path exists but is not a file.",
                    suggestion="Set token_file to the session token file created by M2Entry.",
                )
            else:
                checks["token_file"] = _check(
                    "warn", "The configured token file is not present yet; this is normal before M2Entry loads.",
                    suggestion="Load M2Entry.dll in the intended NX session, then rerun online diagnostics.",
                )
        elif isinstance(raw, dict) and config is not None and not config.configured:
            checks["token_file"] = _check("skipped", "No token file is configured for offline discovery.")

    if "configuration" not in checks:
        checks["configuration"] = _check(
            "skipped", "Configuration fields could not be checked because the config file was unavailable or unreadable.",
        )
    if "bridge_helper" not in checks:
        checks["bridge_helper"] = _check("skipped", "No usable helper_path was available from the configuration.")
    if "token_file" not in checks:
        checks["token_file"] = _check("skipped", "No usable token_file was available from the configuration.")

    if helper_path is not None:
        entry_path, entry_error = _resolve_file(args.entry_dll or str(helper_path.parent / "M2Entry.dll"), "M2Entry.dll")
        bridge_path, bridge_error = _resolve_file(args.bridge_dll or str(helper_path.parent / "M2Bridge.dll"), "M2Bridge.dll")
        checks["bridge_entry"] = (
            _check("fail", entry_error, suggestion="Build M2Entry.dll or pass --entry-dll with its installed path.")
            if entry_error else _local_file_check(entry_path, "NX bridge entry (M2Entry.dll)")
        )
        checks["bridge_library"] = (
            _check("fail", bridge_error, suggestion="Build M2Bridge.dll or pass --bridge-dll with its installed path.")
            if bridge_error else _local_file_check(bridge_path, "bridge library (M2Bridge.dll)")
        )
    else:
        checks["bridge_entry"] = _check("skipped", "Build or configure the bridge helper before locating M2Entry.dll.")
        checks["bridge_library"] = _check("skipped", "Build or configure the bridge helper before locating M2Bridge.dll.")

    if args.online:
        if config is None:
            checks["online"] = _check(
                "skipped", "Online status and ping were not run because the configuration could not be loaded.",
            )
        else:
            checks["online"] = _online_probe(config_path, config)
    else:
        checks["online"] = _check(
            "skipped", "Online bridge status and ping were not requested; no MCP host was started.",
        )

    states = [item["status"] for item in checks.values()]
    overall = "fail" if "fail" in states else "warn" if "warn" in states else "pass"
    return {
        "schema_version": "m6-diagnostics-1",
        "checked_at_utc": datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z"),
        "overall": overall,
        "checks": checks,
        "notes": [
            "Offline checks inspect local paths and configuration only.",
            "Token file contents are never read or included in this report.",
            "Online mode calls only nx_bridge_status and nx_bridge_ping; it does not call model tools.",
        ],
    }


def _arguments(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Check the local NX Sketch MCP runtime for M6.")
    parser.add_argument("--config", help="local MCP JSON config; defaults to config/bridge.local.json")
    parser.add_argument("--nx-executable", help="installed NX ugraf.exe path; defaults to UGII_BASE_DIR\\NXBIN\\ugraf.exe")
    parser.add_argument("--entry-dll", help="M2Entry.dll path; defaults to the helper's directory")
    parser.add_argument("--bridge-dll", help="M2Bridge.dll path; defaults to the helper's directory")
    parser.add_argument("--online", action="store_true", help="also call nx_bridge_status and nx_bridge_ping")
    parser.add_argument("--report", help="save JSON to a new file; existing files are never overwritten")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = _arguments(argv)
    report = diagnose(args)
    if args.report:
        report_path = Path(args.report).resolve(strict=False)
        try:
            with report_path.open("x", encoding="utf-8", newline="\n") as stream:
                report["saved_report"] = str(report_path)
                json.dump(report, stream, ensure_ascii=False, indent=2)
                stream.write("\n")
        except FileExistsError:
            report.pop("saved_report", None)
            report["report_save"] = _check(
                "fail", "The requested report file already exists and was not overwritten.",
                suggestion="Choose a new output path and rerun the diagnostic.",
            )
        except OSError as exc:
            report.pop("saved_report", None)
            report["report_save"] = _check(
                "fail", f"The report could not be saved ({type(exc).__name__}).",
                suggestion="Choose a writable directory and a new report path.",
            )
        states = [item["status"] for item in report["checks"].values()]
        if report.get("report_save", {}).get("status") == "fail":
            states.append("fail")
        report["overall"] = "fail" if "fail" in states else "warn" if "warn" in states else "pass"
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 1 if report["overall"] == "fail" else 0


if __name__ == "__main__":
    raise SystemExit(main())
