"""Source-tree launcher for the stdlib MCP server."""

from __future__ import annotations

from pathlib import Path
import sys


def main() -> int:
    project_root = Path(__file__).resolve().parents[1]
    source_root = project_root / "src"
    sys.path.insert(0, str(source_root))

    from nx_sketch_mcp.server import main as server_main

    return server_main()


if __name__ == "__main__":
    raise SystemExit(main())

