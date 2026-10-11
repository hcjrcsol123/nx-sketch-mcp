"""Exercise the production rectangle endpoint validator without starting NX."""

from __future__ import annotations

import os
from pathlib import Path
import subprocess
import tempfile
import unittest


PROJECT_ROOT = Path(__file__).resolve().parents[2]
ENTRY_DLL = PROJECT_ROOT / "workspace/build/current/M2/M2Entry.dll"
HARNESS_SOURCE = PROJECT_ROOT / "tests/m2/M3RectangleContractHarness.cs"
COMPILERS = (
    Path(r"C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"),
    Path(r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"),
)


class M3RectangleContractTests(unittest.TestCase):
    def test_production_validator_requires_complete_rectangle_sides(self):
        nx_base = os.environ.get("UGII_BASE_DIR")
        if not nx_base or not ENTRY_DLL.is_file():
            self.skipTest("built M2Entry.dll and local UGII_BASE_DIR are required")
        compiler = next((candidate for candidate in COMPILERS if candidate.is_file()), None)
        if compiler is None:
            self.skipTest("local C# compiler is unavailable")
        with tempfile.TemporaryDirectory(prefix="nx-m3-rectangle-") as temp:
            executable = Path(temp) / "M3RectangleContractHarness.exe"
            compiled = subprocess.run(
                [str(compiler), "/nologo", "/langversion:5", "/target:exe",
                 f"/out:{executable}", str(HARNESS_SOURCE)],
                cwd=PROJECT_ROOT, capture_output=True, text=True, timeout=30,
            )
            self.assertEqual(compiled.returncode, 0, compiled.stdout + compiled.stderr)
            executed = subprocess.run(
                [str(executable), str(ENTRY_DLL), str(Path(nx_base) / "NXBIN/managed")],
                cwd=PROJECT_ROOT, capture_output=True, text=True, timeout=30,
            )
            self.assertEqual(executed.returncode, 0, executed.stdout + executed.stderr)
            self.assertIn("rectangle_contract_checks=16 failures=0", executed.stdout)


if __name__ == "__main__":
    unittest.main()
