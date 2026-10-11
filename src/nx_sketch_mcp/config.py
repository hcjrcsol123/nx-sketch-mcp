"""Runtime configuration for the local bridge connection."""

from __future__ import annotations

from dataclasses import dataclass
import math
from pathlib import Path
import re
from typing import Any

from .jsonutil import loads_unique


DEFAULT_TIMEOUT_SECONDS = 5.0
MIN_TIMEOUT_SECONDS = 0.1
MAX_TIMEOUT_SECONDS = 120.0
_PIPE_NAME = re.compile(r"^[A-Za-z0-9_.-]{1,128}$", re.ASCII)
_ALLOWED_KEYS = {"pipe_name", "token_file", "helper_path", "timeout_seconds", "launch"}
_CONNECTION_KEYS = {"pipe_name", "token_file", "helper_path"}


class ConfigError(ValueError):
    """Invalid or unusable local runtime configuration."""


@dataclass(frozen=True, slots=True)
class LaunchConfig:
    nx_executable: Path
    startup_directory: Path
    ready_timeout_seconds: float = 30.0


@dataclass(frozen=True, slots=True)
class BridgeConfig:
    pipe_name: str | None
    token_file: Path | None
    helper_path: Path | None
    timeout_seconds: float = DEFAULT_TIMEOUT_SECONDS
    launch: LaunchConfig | None = None
    config_path: Path | None = None

    @property
    def configured(self) -> bool:
        return self.pipe_name is not None


def _absolute_file_path(value: Any, field: str, *, must_exist: bool) -> Path:
    if not isinstance(value, str) or not value:
        raise ConfigError(f"{field} must be an absolute file path")
    path = Path(value)
    if not path.is_absolute():
        raise ConfigError(f"{field} must be an absolute file path")
    try:
        resolved = path.resolve(strict=must_exist)
    except OSError as exc:
        raise ConfigError(f"{field} does not identify an existing file") from exc
    if must_exist and not resolved.is_file():
        raise ConfigError(f"{field} does not identify a file")
    if not must_exist and resolved.exists() and not resolved.is_file():
        raise ConfigError(f"{field} must not identify a directory")
    return resolved


def _launch_from_object(raw: Any) -> LaunchConfig:
    if not isinstance(raw, dict) or set(raw) - {
        "nx_executable", "startup_directory", "ready_timeout_seconds",
    } or not {"nx_executable", "startup_directory"} <= set(raw):
        raise ConfigError("launch requires nx_executable and startup_directory, with optional ready_timeout_seconds")
    nx_executable = _absolute_file_path(raw["nx_executable"], "launch.nx_executable", must_exist=True)
    if nx_executable.name.lower() != "ugraf.exe":
        raise ConfigError("launch.nx_executable must identify ugraf.exe")
    directory_value = raw["startup_directory"]
    if not isinstance(directory_value, str) or not directory_value or not Path(directory_value).is_absolute():
        raise ConfigError("launch.startup_directory must be an absolute existing directory")
    try:
        startup_directory = Path(directory_value).resolve(strict=True)
    except OSError as exc:
        raise ConfigError("launch.startup_directory must be an absolute existing directory") from exc
    if not startup_directory.is_dir():
        raise ConfigError("launch.startup_directory must be an absolute existing directory")
    if startup_directory.name.lower() != "startup":
        raise ConfigError("launch.startup_directory must identify the NX user directory's startup subdirectory")
    startup_dlls = {path.name.lower() for path in startup_directory.iterdir() if path.suffix.lower() == ".dll"}
    if startup_dlls != {"m2startup.dll"}:
        raise ConfigError("launch.startup_directory must contain only M2Startup.dll as a startup DLL")
    timeout = raw.get("ready_timeout_seconds", 30.0)
    if isinstance(timeout, bool) or not isinstance(timeout, (int, float)):
        raise ConfigError("launch.ready_timeout_seconds must be a number")
    timeout = float(timeout)
    if not math.isfinite(timeout) or not 1.0 <= timeout <= 30.0:
        raise ConfigError("launch.ready_timeout_seconds must be between 1 and 30")
    return LaunchConfig(nx_executable, startup_directory, timeout)


def config_from_object(raw: Any, *, config_path: Path | None = None) -> BridgeConfig:
    if not isinstance(raw, dict):
        raise ConfigError("configuration must be a JSON object")
    unknown = set(raw) - _ALLOWED_KEYS
    if unknown:
        names = ", ".join(sorted(str(key) for key in unknown))
        raise ConfigError(f"unsupported configuration key(s): {names}")

    present = _CONNECTION_KEYS.intersection(raw)
    if present and present != _CONNECTION_KEYS:
        missing = ", ".join(sorted(_CONNECTION_KEYS - present))
        raise ConfigError(f"bridge configuration is incomplete; missing: {missing}")

    timeout = raw.get("timeout_seconds", DEFAULT_TIMEOUT_SECONDS)
    if isinstance(timeout, bool) or not isinstance(timeout, (int, float)):
        raise ConfigError("timeout_seconds must be a number")
    timeout = float(timeout)
    if not math.isfinite(timeout) or not MIN_TIMEOUT_SECONDS <= timeout <= MAX_TIMEOUT_SECONDS:
        raise ConfigError(
            f"timeout_seconds must be between {MIN_TIMEOUT_SECONDS:g} and {MAX_TIMEOUT_SECONDS:g}"
        )

    if not present:
        if "launch" in raw:
            raise ConfigError("launch requires a complete bridge configuration")
        return BridgeConfig(None, None, None, timeout)

    pipe_name = raw["pipe_name"]
    if not isinstance(pipe_name, str) or not _PIPE_NAME.fullmatch(pipe_name):
        raise ConfigError("pipe_name must be a simple local pipe name (1 to 128 ASCII letters, digits, '.', '_' or '-')")
    token_file = _absolute_file_path(raw["token_file"], "token_file", must_exist=False)
    helper_path = _absolute_file_path(raw["helper_path"], "helper_path", must_exist=True)
    launch = _launch_from_object(raw["launch"]) if "launch" in raw else None
    return BridgeConfig(pipe_name, token_file, helper_path, timeout, launch, config_path)


def load_config(path: str | Path) -> BridgeConfig:
    config_path = Path(path)
    try:
        text = config_path.read_text(encoding="utf-8")
    except OSError as exc:
        raise ConfigError("configuration file could not be read") from exc
    try:
        raw = loads_unique(text)
    except (ValueError, UnicodeError) as exc:
        raise ConfigError(f"configuration is not valid UTF-8 JSON: {exc}") from exc
    return config_from_object(raw, config_path=config_path.resolve())
