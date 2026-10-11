"""Explicit, bounded connection preparation; importing this module never starts NX."""

from __future__ import annotations

from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time
from typing import Any, Iterator

from .bridge import BridgeFailure, JsonlBridge
from .config import BridgeConfig
from .jsonutil import loads_unique


@contextmanager
def _startup_lock(path: Path) -> Iterator[bool]:
    """An OS-released byte lock coordinates separate MCP client processes."""
    import msvcrt

    with path.open("a+b") as handle:
        if handle.seek(0, os.SEEK_END) == 0:
            handle.write(b"\0")
            handle.flush()
        handle.seek(0)
        try:
            msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
        except OSError:
            yield False
            return
        try:
            yield True
        finally:
            handle.seek(0)
            msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)


class NxReadiness:
    def __init__(self, config: BridgeConfig, bridge: JsonlBridge) -> None:
        self._config = config
        self._bridge = bridge

    def _probe(self, deadline: float) -> tuple[dict[str, Any] | None, dict[str, str] | None]:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            return None, {"code": "READY_TIMEOUT", "message": "Connection preparation reached its deadline."}
        try:
            status = self._bridge.call("bridge.status", {}, timeout_seconds=min(2.0, remaining))
            return status, None
        except BridgeFailure as exc:
            return None, {"code": exc.code, "message": exc.message[:1000]}

    @staticmethod
    def _ready(status: dict[str, Any], *, reused: bool) -> dict[str, Any]:
        return {
            "state": "READY", "ready": True, "reused": reused,
            "ownership_changed": False, "bridge_status": status,
        }

    def ensure_ready(self) -> dict[str, Any]:
        launch = self._config.launch
        budget = launch.ready_timeout_seconds if launch is not None else min(self._config.timeout_seconds, 2.0)
        deadline = time.monotonic() + budget
        status, error = self._probe(deadline)
        if status is not None:
            return self._ready(status, reused=True)
        if self._config.configured:
            # Explicit diagnostic reconnect only: a helper can have cached a previous NX token.
            self._bridge.reset_connection()
            status, error = self._probe(deadline)
            if status is not None:
                return self._ready(status, reused=True)
        if not self._config.configured or launch is None or self._config.config_path is None:
            return {
                "state": "NEEDS_MANUAL_ACTION", "ready": False, "code": "LAUNCH_UNCONFIGURED",
                "message": "Configure a launch profile for nx_ensure_ready, or load the bridge in the existing NX session.",
                "last_bridge_error": error,
            }
        if os.name != "nt":
            return {"state": "NEEDS_MANUAL_ACTION", "ready": False, "code": "UNSUPPORTED_PLATFORM",
                    "message": "Automatic NX GUI connection preparation requires Windows."}
        assert self._config.token_file is not None
        identity = hashlib.sha256((str(self._config.pipe_name).casefold() + "\0" +
                                   os.path.normcase(str(self._config.token_file))).encode()).hexdigest()[:24]
        private_dir = self._config.token_file.parent
        if not private_dir.is_dir():
            return {"state": "NEEDS_MANUAL_ACTION", "ready": False, "code": "SESSION_DIRECTORY_MISSING",
                    "message": "The configured private session directory is missing; repair the installation."}
        lock_path = private_dir / ("ready-" + identity + ".lock")
        state_path = private_dir / ("ready-" + identity + ".json")
        try:
            with _startup_lock(lock_path) as acquired:
                if not acquired:
                    return {"state": "STARTING", "ready": False, "code": "STARTUP_IN_PROGRESS",
                            "message": "Another MCP client is preparing this NX connection; retry nx_ensure_ready."}
                # Another client may have finished between the initial probe and acquiring the lock.
                status, error = self._probe(deadline)
                if status is not None:
                    return self._ready(status, reused=True)
                return self._prepare_locked(deadline, state_path, error)
        except OSError:
            return {"state": "NEEDS_MANUAL_ACTION", "ready": False, "code": "READINESS_STATE_UNAVAILABLE",
                    "message": "The private session directory cannot store the NX startup coordination state."}

    def _prepare_locked(self, deadline: float, state_path: Path,
                        error: dict[str, str] | None) -> dict[str, Any]:
        launch = self._config.launch
        assert launch is not None
        previous: dict[str, Any] = {}
        if state_path.is_file():
            try:
                record = loads_unique(state_path.read_text(encoding="utf-8"))
                if isinstance(record, dict):
                    previous = record
            except (OSError, ValueError, UnicodeError):
                return {"state": "NEEDS_MANUAL_ACTION", "ready": False, "code": "INVALID_STARTUP_STATE",
                        "message": "The saved startup identity is invalid; inspect the private session state before retrying."}
        launched = self._launch(deadline, previous)
        if launched.get("state") not in {"STARTED", "EXISTING_MANAGED"}:
            return launched
        pid = launched.get("nx_pid")
        ticks = launched.get("process_start_ticks")
        if isinstance(pid, bool) or not isinstance(pid, int) or pid <= 0 or not isinstance(ticks, str) or not ticks.isdecimal():
            return {"state": "NEEDS_MANUAL_ACTION", "ready": False, "code": "INVALID_LAUNCH_IDENTITY",
                    "message": "NX startup returned no reliable process identity; inspect NX before retrying."}
        temporary_state = state_path.with_suffix(".tmp")
        temporary_state.write_text(json.dumps({"nx_pid": pid, "process_start_ticks": ticks}), encoding="utf-8")
        os.replace(temporary_state, state_path)
        self._bridge.reset_connection()
        while time.monotonic() < deadline:
            status, error = self._probe(deadline)
            if status is not None:
                return self._ready(status, reused=launched["state"] == "EXISTING_MANAGED")
            startup_error = self._startup_error(pid, ticks)
            if startup_error is not None:
                return startup_error
            if error is not None and error["code"] == "AUTHORIZATION_REQUIRED":
                self._bridge.reset_connection()
            remaining = deadline - time.monotonic()
            if remaining > 0:
                time.sleep(min(0.25, remaining))
        return {
            "state": "STARTING", "ready": False, "code": "READY_TIMEOUT", "nx_pid": pid,
            "message": "NX was started but the bridge is not ready. Check NX for license or startup dialogs, then retry nx_ensure_ready. No NX process was closed.",
            "last_bridge_error": error,
        }

    def _startup_error(self, pid: int, start_ticks: str) -> dict[str, Any] | None:
        assert self._config.token_file is not None
        path = self._config.token_file.parent / "startup.last_error.txt"
        try:
            start_ns = (int(start_ticks) - 621355968000000000) * 100
            if path.stat().st_mtime_ns < start_ns:
                return None
            with path.open("r", encoding="utf-8-sig") as stream:
                first_line = stream.readline(64).strip()
                if first_line != str(pid):
                    return None
                message = stream.read(1000).strip()
        except (OSError, UnicodeError):
            return None
        return {"state": "FAILED", "ready": False, "code": "STARTUP_LOAD_FAILED", "nx_pid": pid,
                "message": message or "The installed NX startup bridge reported a load failure."}

    def _launch(self, deadline: float, previous: dict[str, Any]) -> dict[str, Any]:
        launch = self._config.launch
        assert launch is not None
        script = Path(__file__).resolve().parents[2] / "tools/start-nx-global.ps1"
        if not script.is_file():
            return {"state": "FAILED", "ready": False, "code": "LAUNCHER_MISSING",
                    "message": "The installed NX connection launcher is missing."}
        command = [str(Path(os.environ.get("SystemRoot", r"C:\Windows")) / "System32/WindowsPowerShell/v1.0/powershell.exe"),
                   "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", str(script),
                   "-Config", str(self._config.config_path), "-NxExe", str(launch.nx_executable),
                   "-StartupDirectory", str(launch.startup_directory), "-AsJson"]
        pid = previous.get("nx_pid")
        ticks = previous.get("process_start_ticks")
        if isinstance(pid, int) and not isinstance(pid, bool) and pid > 0 and isinstance(ticks, str) and ticks.isdecimal():
            command.extend(["-ExistingProcessId", str(pid), "-ExistingStartTicks", ticks])
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            return {"state": "STARTING", "ready": False, "code": "READY_TIMEOUT",
                    "message": "Connection preparation reached its deadline before launch; retry nx_ensure_ready."}
        try:
            result = subprocess.run(command, stdin=subprocess.DEVNULL, capture_output=True, timeout=remaining,
                                    creationflags=subprocess.CREATE_NO_WINDOW)
        except subprocess.TimeoutExpired:
            return {"state": "NEEDS_MANUAL_ACTION", "ready": False, "code": "LAUNCH_TIMEOUT",
                    "message": "The NX launcher exceeded its deadline. Inspect NX before retrying; no NX process was closed."}
        except OSError:
            return {"state": "FAILED", "ready": False, "code": "LAUNCH_UNAVAILABLE",
                    "message": "The installed NX launcher could not be started."}
        try:
            payload = loads_unique(result.stdout.decode("utf-8-sig", errors="strict").strip())
        except (ValueError, UnicodeError):
            payload = None
        if not isinstance(payload, dict) or payload.get("state") not in {
            "STARTED", "EXISTING_MANAGED", "STARTING", "NEEDS_MANUAL_ACTION", "FAILED",
        }:
            return {"state": "FAILED", "ready": False, "code": "LAUNCH_PROTOCOL_ERROR",
                    "message": "The NX launcher did not return its structured connection result."}
        if result.returncode != 0 and payload["state"] in {"STARTED", "EXISTING_MANAGED"}:
            return {"state": "FAILED", "ready": False, "code": "LAUNCH_FAILED",
                    "message": "The NX launcher exited unsuccessfully."}
        return {**payload, "ready": False}
