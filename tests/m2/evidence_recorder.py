"""Append sanitized, machine-readable observations when the suite runner opts in."""

from __future__ import annotations

from datetime import datetime, timezone
import json
import os
from pathlib import Path
from typing import Any


def record(test_id: str, observations: dict[str, Any]) -> None:
    destination = os.environ.get("M2_PUBLIC_EVIDENCE_PATH")
    if not destination:
        return
    if not test_id or not isinstance(observations, dict):
        raise ValueError("invalid evidence record")
    record_object = {
        "captured_utc": datetime.now(timezone.utc).isoformat(timespec="milliseconds"),
        "test_id": test_id,
        "result": "PASS",
        "observations": observations,
    }
    line = json.dumps(record_object, ensure_ascii=False, allow_nan=False, separators=(",", ":"))
    path = Path(destination)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a", encoding="utf-8", newline="\n") as stream:
        stream.write(line + "\n")
