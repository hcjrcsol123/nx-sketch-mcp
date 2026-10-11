"""Persistent JSONL client for the configured NX bridge helper."""

from __future__ import annotations

from dataclasses import dataclass
import queue
import subprocess
import threading
import time
from typing import Any, BinaryIO

from .config import BridgeConfig
from .jsonutil import dumps_line, loads_unique


_MAX_LINE_BYTES = 8_388_608
_SUPPORTED_COMMANDS = {
    "bridge.status",
    "bridge.dispatch_probe",
    "ownership.takeover",
    "ownership.return",
    "model.get_session",
    "model.inspect_part",
    "model.inspect_sketch",
    "model.query_faces",
    "operation.get",
    "operation.cancel",
    "operation.prepare",
    "operation.prepare_sketch",
    "operation.prepare_extrude",
    "operation.prepare_thread",
    "operation.prepare_helical_groove",
    "operation.execute_plan",
    "operation.verify_part",
    "operation.save_copy",
}
_EOF = object()


@dataclass(slots=True)
class BridgeFailure(Exception):
    code: str
    message: str
    outcome_unknown: bool = False

    def __str__(self) -> str:
        return f"{self.code}: {self.message}"


class JsonlBridge:
    """Owns one helper process and serializes all bridge exchanges."""

    def __init__(self, config: BridgeConfig) -> None:
        self._config = config
        self._process: subprocess.Popen[bytes] | None = None
        self._messages: queue.Queue[object] | None = None
        self._next_id = 1
        self._lock = threading.Lock()
        self._closed = False

    def call(self, command: str, params: dict[str, Any], *, timeout_seconds: float | None = None) -> dict[str, Any]:
        if command not in _SUPPORTED_COMMANDS:
            raise ValueError("unsupported bridge command")
        if not self._config.configured:
            raise BridgeFailure("BRIDGE_UNCONFIGURED", "No local bridge session is configured.")

        with self._lock:
            if self._closed:
                raise BridgeFailure("BRIDGE_CLOSED", "The local bridge host is shutting down.")
            assert self._config.token_file is not None
            if not self._config.token_file.is_file():
                raise BridgeFailure(
                    "BRIDGE_NOT_READY",
                    "The NX session token file is not available; load M2Entry first.",
                )
            request_id = str(self._next_id)
            self._next_id += 1
            request = {"id": request_id, "command": command, "params": params}
            timeout = self._config.timeout_seconds if timeout_seconds is None else min(
                self._config.timeout_seconds, max(0.0, timeout_seconds),
            )
            deadline = time.monotonic() + timeout
            process, messages = self._ensure_process()
            self._write_request(process, request, deadline)
            try:
                response = self._read_response(process, messages, request_id, deadline)
            except BridgeFailure as exc:
                if exc.code in {"BRIDGE_TIMEOUT", "BRIDGE_UNAVAILABLE", "BRIDGE_PROTOCOL_ERROR"}:
                    exc.outcome_unknown = True
                raise
            if "error" in response:
                error = response["error"]
                if not isinstance(error, dict):
                    self._fail_process(process)
                    raise BridgeFailure("BRIDGE_PROTOCOL_ERROR", "The bridge returned an invalid error response.")
                code = error.get("code")
                message = error.get("message")
                if not isinstance(code, str) or not code or not isinstance(message, str) or not message:
                    self._fail_process(process)
                    raise BridgeFailure("BRIDGE_PROTOCOL_ERROR", "The bridge returned an invalid error response.")
                raise BridgeFailure(code, message)

            result = response["result"]
            if not isinstance(result, dict):
                self._fail_process(process)
                raise BridgeFailure("BRIDGE_PROTOCOL_ERROR", "The bridge returned an invalid result.")
            return result

    def _ensure_process(self) -> tuple[subprocess.Popen[bytes], queue.Queue[object]]:
        process = self._process
        messages = self._messages
        if process is not None and messages is not None and process.poll() is None:
            return process, messages
        if process is not None:
            self._dispose_process(process, graceful=False)

        assert self._config.helper_path is not None
        assert self._config.token_file is not None
        assert self._config.pipe_name is not None
        try:
            process = subprocess.Popen(
                [
                    str(self._config.helper_path),
                    "--pipe-name",
                    self._config.pipe_name,
                    "--token-file",
                    str(self._config.token_file),
                ],
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                bufsize=0,
            )
        except OSError as exc:
            raise BridgeFailure("BRIDGE_UNAVAILABLE", "The local bridge helper could not be started.") from exc

        if process.stdin is None or process.stdout is None:
            self._dispose_process(process, graceful=False)
            raise BridgeFailure("BRIDGE_UNAVAILABLE", "The local bridge helper did not open its JSONL pipes.")
        messages: queue.Queue[object] = queue.Queue()
        reader = threading.Thread(
            target=self._read_stdout,
            args=(process.stdout, messages),
            name="nx-sketch-mcp-helper-reader",
            daemon=True,
        )
        reader.start()
        self._process = process
        self._messages = messages
        return process, messages

    @staticmethod
    def _read_stdout(stream: BinaryIO, messages: queue.Queue[object]) -> None:
        try:
            while True:
                line = stream.readline(_MAX_LINE_BYTES + 1)
                if not line:
                    messages.put(_EOF)
                    return
                if len(line) > _MAX_LINE_BYTES or not line.endswith(b"\n"):
                    messages.put(BridgeFailure("BRIDGE_PROTOCOL_ERROR", "The bridge response line is too large."))
                    return
                messages.put(line)
        except (OSError, ValueError):
            messages.put(_EOF)

    def _write_request(
        self,
        process: subprocess.Popen[bytes], request: dict[str, Any], deadline: float
    ) -> None:
        if process.stdin is None:
            self._fail_process(process)
            raise BridgeFailure("BRIDGE_UNAVAILABLE", "The local bridge helper is unavailable.")
        if process.poll() is not None:
            self._fail_process(process)
            raise BridgeFailure("BRIDGE_UNAVAILABLE", "The local bridge helper exited before the request.")
        if time.monotonic() >= deadline:
            self._fail_process(process)
            raise BridgeFailure("BRIDGE_TIMEOUT", "The local bridge request timed out.")
        try:
            process.stdin.write(dumps_line(request))
            process.stdin.flush()
        except (BrokenPipeError, OSError) as exc:
            self._fail_process(process)
            raise BridgeFailure("BRIDGE_UNAVAILABLE", "The local bridge helper stopped accepting requests.") from exc

    def _read_response(
        self,
        process: subprocess.Popen[bytes],
        messages: queue.Queue[object],
        request_id: str,
        deadline: float,
    ) -> dict[str, Any]:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            self._fail_process(process)
            raise BridgeFailure("BRIDGE_TIMEOUT", "The local bridge request timed out.")
        try:
            item = messages.get(timeout=remaining)
        except queue.Empty as exc:
            self._fail_process(process)
            raise BridgeFailure("BRIDGE_TIMEOUT", "The local bridge request timed out.") from exc
        if item is _EOF:
            self._fail_process(process)
            raise BridgeFailure("BRIDGE_UNAVAILABLE", "The local bridge helper exited without a response.")
        if isinstance(item, BridgeFailure):
            self._fail_process(process)
            raise item
        assert isinstance(item, bytes)
        try:
            response = loads_unique(item[:-1].decode("utf-8", errors="strict"))
        except (ValueError, UnicodeError) as exc:
            self._fail_process(process)
            raise BridgeFailure("BRIDGE_PROTOCOL_ERROR", "The local bridge returned invalid JSON.") from exc
        if not isinstance(response, dict):
            self._fail_process(process)
            raise BridgeFailure("BRIDGE_PROTOCOL_ERROR", "The local bridge returned an invalid response.")
        if response.get("id") != request_id or not isinstance(response.get("id"), str):
            self._fail_process(process)
            raise BridgeFailure("BRIDGE_PROTOCOL_ERROR", "The local bridge response id did not match the request.")
        if set(response) == {"id", "result"}:
            return response
        if set(response) == {"id", "error"}:
            return response
        self._fail_process(process)
        raise BridgeFailure("BRIDGE_PROTOCOL_ERROR", "The local bridge returned an invalid response envelope.")

    def _fail_process(self, process: subprocess.Popen[bytes]) -> None:
        self._dispose_process(process, graceful=False)

    def _dispose_process(self, process: subprocess.Popen[bytes], *, graceful: bool) -> None:
        if graceful and process.poll() is None:
            if process.stdin is not None:
                try:
                    process.stdin.close()
                except OSError:
                    pass
            try:
                process.wait(timeout=min(1.0, self._config.timeout_seconds))
            except subprocess.TimeoutExpired:
                pass
        if process.poll() is None:
            try:
                process.kill()
            except OSError:
                pass
            try:
                process.wait(timeout=1.0)
            except subprocess.TimeoutExpired:
                pass
        for pipe in (process.stdin, process.stdout):
            if pipe is not None:
                try:
                    pipe.close()
                except OSError:
                    pass
        if self._process is process:
            self._process = None
            self._messages = None

    def close(self) -> None:
        with self._lock:
            if self._closed:
                return
            self._closed = True
            process = self._process
            if process is not None:
                self._dispose_process(process, graceful=True)

    def reset_connection(self) -> None:
        """Discard only our helper so an explicit reconnect reads the current token.

        This never replays a command or changes NX operation/ownership state.
        """
        with self._lock:
            if not self._closed and self._process is not None:
                self._dispose_process(self._process, graceful=False)
