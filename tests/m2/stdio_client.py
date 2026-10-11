"""Small stdlib MCP stdio child-process client for M2 bench verification.

The client keeps raw child output in memory for framing and secret checks. Callers
must redact sensitive values before writing any response or diagnostic to disk.
"""

from __future__ import annotations

import json
import queue
import subprocess
import threading
import time
from dataclasses import dataclass
from typing import Any, Iterable


class ProtocolFailure(AssertionError):
    """The child did not speak the expected UTF-8 JSONL protocol."""


@dataclass
class OutputLine:
    stream: str
    raw: bytes
    received_at: float


class StdioMcpProcess:
    """Launch and interact with one real MCP stdio child process."""

    def __init__(self, command: Iterable[str], *, cwd: str, env: dict[str, str] | None = None):
        self.command = list(command)
        self.process = subprocess.Popen(
            self.command,
            cwd=cwd,
            env=env,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            bufsize=0,
        )
        self._stdout: queue.Queue[OutputLine | None] = queue.Queue()
        self._stderr: queue.Queue[OutputLine | None] = queue.Queue()
        self.stdout_raw: list[bytes] = []
        self.stderr_raw: list[bytes] = []
        self._next_id = 1
        self._closed = False
        self._threads = [
            threading.Thread(target=self._drain, args=(self.process.stdout, "stdout", self._stdout), daemon=True),
            threading.Thread(target=self._drain, args=(self.process.stderr, "stderr", self._stderr), daemon=True),
        ]
        for thread in self._threads:
            thread.start()

    @staticmethod
    def _drain(stream, label: str, target: queue.Queue[OutputLine | None]) -> None:
        try:
            while True:
                raw = stream.readline()
                if not raw:
                    break
                target.put(OutputLine(label, raw, time.monotonic()))
        finally:
            target.put(None)

    def send(self, message: dict[str, Any]) -> None:
        if self._closed or self.process.poll() is not None:
            raise ProtocolFailure("child process is already closed")
        if self.process.stdin is None:
            raise ProtocolFailure("child stdin is unavailable")
        payload = json.dumps(message, ensure_ascii=False, separators=(",", ":")).encode("utf-8") + b"\n"
        self.process.stdin.write(payload)
        self.process.stdin.flush()

    def send_raw_line(self, line: str) -> None:
        """Send one newline-terminated control line to non-MCP JSONL harnesses."""
        if self._closed or self.process.poll() is not None or self.process.stdin is None:
            raise ProtocolFailure("child process is already closed")
        if "\n" in line or "\r" in line:
            raise ValueError("control command must be one line")
        self.process.stdin.write(line.encode("utf-8") + b"\n")
        self.process.stdin.flush()

    def request(self, method: str, params: dict[str, Any] | None = None, *, timeout: float = 10.0) -> dict[str, Any]:
        request_id = self._next_id
        self._next_id += 1
        request: dict[str, Any] = {"jsonrpc": "2.0", "id": request_id, "method": method}
        if params is not None:
            request["params"] = params
        response = self.exchange(request, expected_id=request_id, timeout=timeout)
        if response.get("jsonrpc") != "2.0":
            raise ProtocolFailure("response has wrong or missing jsonrpc version")
        return response

    def exchange(self, message: dict[str, Any], *, expected_id: Any, timeout: float = 10.0) -> dict[str, Any]:
        """Send one JSON object and wait for a response with its correlation id."""
        self.send(message)
        deadline = time.monotonic() + timeout
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError(f"timed out waiting for response id={expected_id}")
            response = self.receive_json(remaining)
            if response.get("id") == expected_id:
                return response
            # This minimal test client uses one request at a time. Unsolicited or
            # mismatched responses indicate a server framing/correlation failure.
            raise ProtocolFailure(f"unexpected response id={response.get('id')!r}; expected {expected_id}")

    def notification(self, method: str, params: dict[str, Any] | None = None) -> None:
        notification: dict[str, Any] = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            notification["params"] = params
        self.send(notification)

    def receive_json(self, timeout: float = 10.0) -> dict[str, Any]:
        return self._parse(self._next_stdout(timeout))

    def assert_quiet(self, duration: float = 2.0) -> None:
        """Require no stdout response during a notification quiet interval."""
        try:
            line = self._stdout.get(timeout=duration)
        except queue.Empty:
            return
        if line is None:
            raise ProtocolFailure("child stdout closed during notification quiet interval")
        raise ProtocolFailure("server emitted stdout for a notification: " + repr(line.raw[:240]))

    def drain_stderr(self) -> list[bytes]:
        while True:
            try:
                item = self._stderr.get_nowait()
            except queue.Empty:
                break
            if item is not None:
                self.stderr_raw.append(item.raw)
        return list(self.stderr_raw)

    def close_stdin(self) -> None:
        if self.process.stdin is not None and not self.process.stdin.closed:
            self.process.stdin.close()

    def wait(self, timeout: float = 5.0) -> int:
        exit_code = self.process.wait(timeout=timeout)
        self._finish_streams()
        return exit_code

    def terminate_own_process(self, timeout: float = 2.0) -> None:
        """Cleanup only this test's child process."""
        if self.process.poll() is None:
            self.process.terminate()
            try:
                self.process.wait(timeout=timeout)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=timeout)
        self._closed = True
        self._finish_streams()

    def all_stdout_bytes(self) -> bytes:
        # Collect queued output after callers have completed their requests.
        while True:
            try:
                item = self._stdout.get_nowait()
            except queue.Empty:
                break
            if item is not None:
                self.stdout_raw.append(item.raw)
        return b"".join(self.stdout_raw)

    def _next_stdout(self, timeout: float) -> OutputLine:
        try:
            item = self._stdout.get(timeout=timeout)
        except queue.Empty as exc:
            raise TimeoutError("timed out waiting for stdout JSONL line") from exc
        if item is None:
            raise ProtocolFailure("child stdout closed before response")
        self.stdout_raw.append(item.raw)
        return item

    def _finish_streams(self) -> None:
        for thread in self._threads:
            thread.join(timeout=1.0)
        self.drain_stderr()
        for pipe in (self.process.stdin, self.process.stdout, self.process.stderr):
            if pipe is not None and not pipe.closed:
                pipe.close()

    @staticmethod
    def _parse(line: OutputLine) -> dict[str, Any]:
        try:
            text = line.raw.decode("utf-8", errors="strict")
        except UnicodeDecodeError as exc:
            raise ProtocolFailure("stdout is not valid UTF-8") from exc
        if not text.endswith("\n"):
            raise ProtocolFailure("stdout line is not terminated by LF")
        try:
            value = json.loads(text)
        except json.JSONDecodeError as exc:
            raise ProtocolFailure("stdout contains non-JSON data: " + repr(text[:240])) from exc
        if not isinstance(value, dict):
            raise ProtocolFailure("stdout JSONL message is not an object")
        return value


def assert_stdout_jsonl_only(raw: bytes, *, forbidden_values: Iterable[str] = ()) -> int:
    """Validate all captured stdout as UTF-8 JSONL and scan for leaked secrets."""
    for secret in forbidden_values:
        encoded = secret.encode("utf-8")
        if encoded and encoded in raw:
            raise ProtocolFailure("stdout contains a configured secret value")
    if not raw:
        return 0
    try:
        text = raw.decode("utf-8", errors="strict")
    except UnicodeDecodeError as exc:
        raise ProtocolFailure("stdout is not valid UTF-8") from exc
    if not text.endswith("\n"):
        raise ProtocolFailure("stdout ends with an incomplete JSONL record")
    lines = text.splitlines()
    for index, line in enumerate(lines, 1):
        try:
            value = json.loads(line)
        except json.JSONDecodeError as exc:
            raise ProtocolFailure(f"stdout line {index} is not JSON") from exc
        if not isinstance(value, dict):
            raise ProtocolFailure(f"stdout line {index} is not a JSON object")
    return len(lines)
