"""Focused source inspection for M2 bridge security and scope boundaries."""

from __future__ import annotations

import re
from pathlib import Path
import unittest

from evidence_recorder import record


PROJECT_ROOT = Path(__file__).resolve().parents[2]
BRIDGE_SOURCE = PROJECT_ROOT / "src" / "bridge" / "M2Bridge.cs"
PYTHON_BRIDGE_SOURCE = PROJECT_ROOT / "src" / "nx_sketch_mcp" / "bridge.py"


def strip_comments(source: str) -> str:
    source = re.sub(r"/\*.*?\*/", "", source, flags=re.S)
    return re.sub(r"//[^\n]*", "", source)


class M2BridgeSourceInspection(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source = BRIDGE_SOURCE.read_text(encoding="utf-8")
        cls.code = strip_comments(cls.source)

    def test_pipe_server_is_current_user_scoped(self):
        self.assertRegex(
            self.code,
            r'_authorizedGroup\s*=\s*WindowsIdentity\.GetCurrent\(\)\.Name\s*;',
        )
        self.assertIn('options["authorizedGroup"] = _authorizedGroup;', self.code)
        self.assertRegex(self.code, r'_exclusiveAddressUse\s*=\s*true\s*;')
        self.assertIn('options["exclusiveAddressUse"] = _exclusiveAddressUse;', self.code)
        record("M2-2-00", {"current_identity_pipe": True, "exclusive_pipe": True})

    def test_server_generated_token_file_is_protected_and_verified(self):
        self.assertIn("security.SetAccessRuleProtection(true, false)", self.code)
        self.assertIn("security.SetOwner(identity.User)", self.code)
        self.assertIn("new FileSystemAccessRule(identity.User, FileSystemRights.FullControl", self.code)
        self.assertIn("File.GetAccessControl(path)", self.code)
        self.assertIn("if (rule.AccessControlType == AccessControlType.Allow && !sid.Equals(identity.User))", self.code)
        record("M2-2-00-token-acl", {"protected_acl": True, "current_user_owner_verified": True,
                                     "other_allow_identity_rejected": True})

    def test_bridge_has_no_nxopen_reference_or_model_api_code(self):
        self.assertNotIn("NXOpen", self.code)
        self.assertNotIn("Session.GetSession", self.code)
        self.assertNotIn("PartCollection", self.code)
        record("M2-2-00-scope", {"nxopen_reference": False, "model_api_code": False})

    def test_only_declared_diagnostic_ownership_and_model_commands_are_executable(self):
        expected = {
            "bridge.status", "bridge.dispatch_probe", "ownership.takeover", "ownership.return",
            "model.get_session", "model.inspect_part", "model.inspect_sketch",
            "operation.get", "operation.cancel", "operation.prepare", "operation.execute_plan",
            "operation.verify_part", "operation.save_copy",
        }
        for command in expected:
            self.assertIn('String.Equals(command, "' + command + '", StringComparison.Ordinal)', self.code)
        self.assertIn('"UNKNOWN_COMMAND"', self.code)
        record("M2-7-command-scope-static", {
            "declared_commands": sorted(expected),
            "operation_control_commands": ["operation.get", "operation.cancel", "operation.prepare", "operation.verify_part"],
            "model_write_commands": ["operation.execute_plan", "operation.save_copy"],
            "source_inspection_is_runtime_or_nx_write_evidence": False,
        })

    def test_prepared_missing_token_is_rejected_before_helper_spawn(self):
        python_source = PYTHON_BRIDGE_SOURCE.read_text(encoding="utf-8")
        token_guard = python_source.index("if not self._config.token_file.is_file():")
        helper_spawn = python_source.index("process = subprocess.Popen(")
        self.assertLess(token_guard, helper_spawn)
        self.assertIn('"BRIDGE_NOT_READY"', python_source[token_guard:helper_spawn])
        record("M2-2-05-helper-launch-guard", {"missing_token_checked_before_helper_spawn": True,
                                                "error_code": "BRIDGE_NOT_READY"})


if __name__ == "__main__":
    unittest.main()
