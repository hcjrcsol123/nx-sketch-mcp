"""Small strict JSON helpers shared by the MCP host and bridge client."""

from __future__ import annotations

import json
from typing import Any


class DuplicateKeyError(ValueError):
    pass


def _unique_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise DuplicateKeyError(f"duplicate JSON key: {key}")
        result[key] = value
    return result


def _reject_nonfinite(value: str) -> None:
    raise ValueError(f"invalid JSON numeric constant: {value}")


def loads_unique(data: str | bytes) -> Any:
    return json.loads(data, object_pairs_hook=_unique_object, parse_constant=_reject_nonfinite)


def dumps_line(value: Any) -> bytes:
    """Encode one JSON value as a UTF-8 JSONL record."""
    return (
        json.dumps(value, ensure_ascii=True, allow_nan=False, separators=(",", ":")) + "\n"
    ).encode("utf-8")
