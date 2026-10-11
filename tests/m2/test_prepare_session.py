"""Execute the session preparation script in a clean, test-owned PowerShell process."""

from __future__ import annotations

import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

from evidence_recorder import record

PROJECT_ROOT = Path(__file__).resolve().parents[2]
BUILD_DIR = PROJECT_ROOT / "workspace" / "build" / "current" / "M2"
WRAPPER = Path(__file__).resolve().parent / "run_prepare_test.ps1"


class PrepareSessionScriptTests(unittest.TestCase):
    def test_prepare_script_emits_consistent_mcp_and_nx_environment_configuration(self):
        self.assertTrue((BUILD_DIR / "M2JsonlHelper.exe").is_file())
        with tempfile.TemporaryDirectory(prefix="nx-m2-prep-test-") as temp:
            session_root = Path(temp) / "independent-session-root"
            completed = subprocess.run(
                ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(WRAPPER),
                 "-BuildRoot", str(BUILD_DIR), "-SessionRoot", str(session_root), "-PythonExe", sys.executable],
                cwd=str(PROJECT_ROOT), capture_output=True, text=True, encoding="utf-8", timeout=40,
            )
            self.assertEqual(completed.returncode, 0, completed.stderr[-1000:])
            result = json.loads(completed.stdout.strip())

            prepared = result["prepared"]
            runtime = result["runtime_config"]
            environment = result["environment"]
            self.assertTrue(Path(prepared["ConfigPath"]).is_file())
            self.assertTrue(Path(prepared["ClientConfigPath"]).is_file())
            self.assertTrue(Path(prepared["TokenFile"]).is_absolute())
            self.assertFalse(result["token_file_exists_before_nx_load"])

            self.assertEqual(runtime["pipe_name"], prepared["PipeName"])
            self.assertEqual(runtime["token_file"], prepared["TokenFile"])
            self.assertEqual(runtime["helper_path"], prepared["HelperPath"])
            self.assertEqual(environment["pipe"], prepared["PipeName"])
            self.assertEqual(environment["token_file"], prepared["TokenFile"])
            self.assertTrue(Path(environment["token_file"]).is_absolute())
            self.assertTrue(Path(prepared["PythonExe"]).is_absolute())

            client = result["client_config"]["mcpServers"]["nx-sketch-mcp"]
            self.assertEqual(client["command"], prepared["PythonExe"])
            self.assertEqual(client["args"][0:2], ["-B", str(PROJECT_ROOT / "tools" / "mcp_stdio.py")])
            self.assertIn("--config", client["args"])
            config_arg = client["args"][client["args"].index("--config") + 1]
            self.assertEqual(config_arg, prepared["ConfigPath"])

            token_path = Path(prepared["TokenFile"])
            self.assertFalse(token_path.exists())
            private_dir = token_path.parent
            self.assertTrue(private_dir.is_dir())
            record("M2-1-00", {"pipe_name": prepared["PipeName"],
                               "runtime_config_keys": sorted(runtime.keys()),
                               "client_command_absolute": Path(client["command"]).is_absolute(),
                               "environment_matches_runtime_config": (
                                   environment["pipe"] == runtime["pipe_name"]
                                   and environment["token_file"] == runtime["token_file"]
                               ),
                               "token_file_absent_before_nx_load": not token_path.exists(),
                               "prepare_script_exit_code": completed.returncode})


if __name__ == "__main__":
    unittest.main()
