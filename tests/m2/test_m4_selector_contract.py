"""Table-driven contract checks for the production C# face selector parser."""

from __future__ import annotations

import os
from pathlib import Path
import subprocess
import tempfile
import unittest


PROJECT_ROOT = Path(__file__).resolve().parents[2]
ENTRY_DLL = PROJECT_ROOT / "workspace/build/current/M2/M2Entry.dll"
HARNESS_SOURCE = PROJECT_ROOT / "tests/m2/M4SelectorContractHarness.cs"
COMPILERS = (
    Path(r"C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"),
    Path(r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"),
)


class M4SelectorContractTests(unittest.TestCase):
    def test_supported_axes_languages_qualifiers_and_ambiguity_rejection(self):
        nx_base = os.environ.get("UGII_BASE_DIR")
        if not nx_base or not ENTRY_DLL.is_file():
            self.skipTest("built M2Entry.dll and local UGII_BASE_DIR are required")
        compiler = next((candidate for candidate in COMPILERS if candidate.is_file()), None)
        if compiler is None:
            self.skipTest("local C# compiler is unavailable")
        with tempfile.TemporaryDirectory(prefix="nx-m4-selector-") as temp:
            executable = Path(temp) / "M4SelectorContractHarness.exe"
            compiled = subprocess.run(
                [str(compiler), "/nologo", "/langversion:5", "/target:exe",
                 f"/out:{executable}", str(HARNESS_SOURCE)],
                cwd=PROJECT_ROOT, capture_output=True, text=True, timeout=30,
            )
            self.assertEqual(compiled.returncode, 0, compiled.stdout + compiled.stderr)
            managed = Path(nx_base) / "NXBIN/managed"
            executed = subprocess.run(
                [str(executable), str(ENTRY_DLL), str(managed)],
                cwd=PROJECT_ROOT, capture_output=True, text=True, timeout=30,
            )
            self.assertEqual(executed.returncode, 0, executed.stdout + executed.stderr)


if __name__ == "__main__":
    unittest.main()
