"""Run the mocked Foundry contract in the infrastructure CI suite."""
import json
import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


class FoundryPlanTests(unittest.TestCase):
    def test_foundry_contract(self):
        result = subprocess.run(
            [
                "terraform", f"-chdir={ROOT / 'infra'}", "test", "-json",
                "-filter=tests/foundry.tftest.hcl",
            ],
            cwd=ROOT, capture_output=True, text=True,
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        events = [json.loads(line) for line in result.stdout.splitlines() if line.strip()]
        statuses = {
            event["@testrun"]: event["test_run"]["status"]
            for event in events
            if event.get("type") == "test_run"
            and event["test_run"].get("progress") == "complete"
        }
        self.assertEqual(statuses, dict.fromkeys([
            "foundry_disabled",
            "foundry_enabled",
            "foundry_disabled_with_group",
            "foundry_enabled_with_group",
            "foundry_invalid_group_guid_rejected",
            "foundry_blank_group_name_rejected",
            "foundry_empty_deployments_rejected",
            "foundry_invalid_sku_rejected",
        ], "pass"))
