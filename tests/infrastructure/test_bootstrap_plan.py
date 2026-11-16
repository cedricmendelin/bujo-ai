import copy
import json
import subprocess
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / ".github" / "scripts"))
from check_bootstrap_plan import validate_plan


class BootstrapPlanTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        result = subprocess.run(
            ["terraform", "-chdir=infra", "test", "-json", "-verbose"],
            cwd=ROOT, check=True, capture_output=True, text=True,
        )
        events = [json.loads(line) for line in result.stdout.splitlines()]
        cls.plan = next(
            event["test_plan"] for event in events
            if event["type"] == "test_plan" and event["@testrun"] == "bootstrap_job_is_inert"
        )

    def test_real_targeted_plan_has_only_the_expected_dependencies(self):
        plan = copy.deepcopy(self.plan)
        plan["complete"] = False
        inventory = validate_plan(plan)
        addresses = {address for address, _ in inventory}
        self.assertIn("azurerm_container_app_job.migration", addresses)
        self.assertIn("module.migration_identity.azurerm_user_assigned_identity.this", addresses)
        self.assertNotIn("module.container_app.azurerm_container_app.this", addresses)

    def test_rejects_deferred_changes(self):
        plan = copy.deepcopy(self.plan)
        plan["deferred_changes"] = [{"reason": "resource_config_unknown"}]
        with self.assertRaisesRegex(ValueError, "deferred changes"):
            validate_plan(plan)

    def test_rejects_app_role_assignment_and_destructive_changes(self):
        for address, resource_type in (
            ("module.container_app.azurerm_container_app.this", "azurerm_container_app"),
            ("azurerm_role_assignment.sql", "azurerm_role_assignment"),
            ("module.key_vault.azapi_resource.this", "azapi_resource"),
        ):
            with self.subTest(address=address):
                plan = copy.deepcopy(self.plan)
                plan["resource_changes"].append({
                    "address": address, "type": resource_type, "mode": "managed",
                    "change": {"actions": ["create"]},
                })
                with self.assertRaisesRegex(ValueError, "Unexpected managed resource"):
                    validate_plan(plan)
        plan = copy.deepcopy(self.plan)
        plan["resource_changes"][0]["change"]["actions"] = ["delete", "create"]
        with self.assertRaisesRegex(ValueError, "Unsafe resource action"):
            validate_plan(plan)

    def test_rejects_job_execution_configuration(self):
        for field, value in (("image", "example/app:latest"), ("command", ["dotnet"])):
            with self.subTest(field=field):
                plan = copy.deepcopy(self.plan)
                job = next(item for item in plan["resource_changes"] if item["address"] == "azurerm_container_app_job.migration")
                job["change"]["after"]["template"][0]["container"][0][field] = value
                with self.assertRaisesRegex(ValueError, "not the inert manual placeholder"):
                    validate_plan(plan)

    def test_rejects_unknown_container_execution_settings(self):
        plan = copy.deepcopy(self.plan)
        job = next(item for item in plan["resource_changes"] if item["address"] == "azurerm_container_app_job.migration")
        job["change"]["after_unknown"]["template"][0]["container"][0]["env"] = True
        with self.assertRaisesRegex(ValueError, "unknown execution settings"):
            validate_plan(plan)

    def test_rejects_an_azapi_resource_with_an_unexpected_type(self):
        plan = copy.deepcopy(self.plan)
        resource = next(item for item in plan["resource_changes"] if item["address"] == "azapi_resource.sql_database")
        resource["change"]["after"]["type"] = "Microsoft.Authorization/roleAssignments@2022-04-01"
        with self.assertRaisesRegex(ValueError, "Unexpected Azure resource type"):
            validate_plan(plan)


if __name__ == "__main__":
    unittest.main()
