"""Full-root AVM bootstrap plans and the first strict application release."""
import copy
import json
import os
import subprocess
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / ".github" / "scripts"))
sys.path.insert(0, str(ROOT / "infra"))
import check_bootstrap_plan as bootstrap  # noqa: E402
import deploy_gate as gate  # noqa: E402

EXPECTED_IMAGE = "registry.example/legacy/app@sha256:" + "a" * 64
EXPECTED_OWNER = "00000000-0000-0000-0000-000000000006"
EXPECTED_APP_ID = (
    "/subscriptions/00000000-0000-0000-0000-000000000004/"
    "resourceGroups/test-rg/providers/Microsoft.App/containerApps/ca-test1234"
)
RELEASE = "ghcr.io/cedricmendelin/bujo-ai@sha256:" + "b" * 64
RUNS = (
    "baseline_full_plan",
    "baseline_full_apply",
    "baseline_infrastructure_plan",
    "baseline_allowlist_preparation_plan",
    "baseline_allowlist_preparation_apply",
    "first_release_migration_plan",
    "first_release_migration_apply",
    "first_release_promotion_plan",
    "same_principal_owner_plan",
)


def resource(plan, address):
    return next(item for item in plan["resource_changes"] if item.get("address") == address)


def changes(plan):
    return {
        item["address"]: item["change"]["actions"]
        for item in plan["resource_changes"]
        if item.get("mode") == "managed" and item["change"]["actions"] != ["no-op"]
    }


def differences(before, after, path=""):
    if isinstance(before, dict) and isinstance(after, dict):
        for key in sorted(set(before) | set(after)):
            yield from differences(before.get(key), after.get(key), f"{path}.{key}")
    elif isinstance(before, list) and isinstance(after, list) and len(before) == len(after):
        for index, (old, new) in enumerate(zip(before, after)):
            yield from differences(old, new, f"{path}[{index}]")
    elif before != after:
        yield path


class InfrastructurePlanTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        result = subprocess.run(
            [
                "terraform", "-chdir=infra", "test", "-json", "-verbose",
                "-filter=" + os.path.join("tests", "baseline.tftest.hcl"),
            ],
            cwd=ROOT,
            capture_output=True,
            text=True,
        )
        events = [json.loads(line) for line in result.stdout.splitlines() if line.strip()]
        statuses = {
            event["@testrun"]: event["test_run"]["status"]
            for event in events
            if event.get("type") == "test_run"
            and event["test_run"].get("progress") == "complete"
        }
        if result.returncode != 0 or statuses != {run: "pass" for run in RUNS}:
            raise AssertionError(f"Actual-module baseline/release sequence failed: {statuses}")
        cls.plans = {
            event["@testrun"]: event["test_plan"]
            for event in events
            if event.get("type") == "test_plan"
        }

    def plan(self, run):
        return copy.deepcopy(self.plans[run])

    def validate(self, plan):
        return bootstrap.validate_baseline_plan(
            plan,
            expected_image=EXPECTED_IMAGE,
            expected_owner=EXPECTED_OWNER,
            expected_app_id=EXPECTED_APP_ID,
        )

    def assert_rejected(self, plan, pattern="Baseline"):
        with self.assertRaisesRegex(ValueError, pattern):
            self.validate(plan)

    def test_baseline_inventory_covers_real_root_and_avm_auxiliaries(self):
        plan = self.plan("baseline_full_plan")
        managed = {
            item["address"]: item["type"]
            for item in plan["resource_changes"]
            if item.get("mode") == "managed"
        }
        self.assertEqual(managed, bootstrap.BASELINE_ALLOWED)
        inventory = self.validate(plan)
        self.assertIn(("azurerm_container_app_job.migration", "create"), inventory)
        self.assertIn(("module.container_app.azapi_resource_action.post_creation_update", "create"), inventory)
        self.assertIn(("module.container_app.terraform_data.update_keeper", "create"), inventory)
        self.assertIn(("module.log_analytics.time_sleep.wait_for_ampls_update", "create"), inventory)

    def test_actual_full_root_bootstrap_plan_retains_image_owner_target_and_deny_all(self):
        plan = self.plan("baseline_full_plan")
        self.validate(plan)

        app = resource(plan, "module.container_app.azapi_resource.container_app")["change"]["after"]
        container = app["body"]["properties"]["template"]["containers"][0]
        self.assertEqual(container["image"], EXPECTED_IMAGE)
        self.assertEqual(len(app["body"]["properties"]["template"]["containers"]), 1)
        self.assertEqual(container["env"][4]["value"], "")
        self.assertEqual(app["name"], "ca-test1234")

        application = resource(plan, "azuread_application.api")["change"]["after"]
        service_principal = resource(plan, "azuread_service_principal.api")["change"]["after"]
        self.assertEqual(application["owners"], [EXPECTED_OWNER])
        self.assertEqual(service_principal["owners"], [EXPECTED_OWNER])
        self.assertEqual(application.get("required_resource_access") or [], [])

    def test_existing_infrastructure_plan_also_passes_without_drift(self):
        plan = self.plan("baseline_infrastructure_plan")
        inventory = self.validate(plan)
        self.assertTrue(all(action == "no-op" for _, action in inventory))
        app = resource(plan, "module.container_app.azapi_resource.container_app")["change"]
        self.assertEqual(app["before"]["id"], EXPECTED_APP_ID)
        self.assertEqual(app["after"]["id"], EXPECTED_APP_ID)
        self.assertEqual(plan["output_changes"]["container_app_id"]["after"], EXPECTED_APP_ID)

    def test_same_oidc_principal_uses_current_object_id_without_sp_lookup(self):
        plan = self.plan("same_principal_owner_plan")
        application = resource(plan, "azuread_application.api")["change"]["after"]
        service_principal = resource(plan, "azuread_service_principal.api")["change"]["after"]
        self.assertEqual(application["owners"], ["00000000-0000-0000-0000-000000000005"])
        self.assertEqual(service_principal["owners"], ["00000000-0000-0000-0000-000000000005"])
        self.assertFalse(any(
            item.get("address") == "data.azuread_service_principal.deployment[0]"
            for item in plan.get("resource_changes", [])
        ))

    def test_owner_allowlist_preparation_is_isolated_before_migration_and_promotion(self):
        preparation = self.plan("baseline_allowlist_preparation_plan")
        keeper = resource(preparation, "module.container_app.terraform_data.update_keeper")["change"]
        self.assertEqual(
            list(differences(keeper["before"]["input"], keeper["after"]["input"])),
            [".containers[0].env[4].value"],
        )
        env_value = keeper["after"]["input"]["containers"][0]["env"][4]["value"]
        self.assertEqual(env_value, "00000000-0000-0000-0000-000000000003:00000000-0000-0000-0000-000000000002")
        self.assertEqual(
            set(changes(preparation)),
            {
                "module.container_app.azapi_resource_action.post_creation_update",
                "module.container_app.terraform_data.update_keeper",
            },
        )

        migration = self.plan("first_release_migration_plan")
        trusted_migration_sql = migration["output_changes"]["migration_sql_connection_string"]["before"]
        self.assertTrue(gate.validate_migration_plan(migration, RELEASE, trusted_migration_sql))

        promotion = self.plan("first_release_promotion_plan")
        trusted_runtime_sql = promotion["output_changes"]["runtime_sql_connection_string"]["before"]
        app_id = resource(promotion, gate.APP_UPDATE_ADDRESS)["change"]["before"]["resource_id"]
        self.assertEqual(app_id, EXPECTED_APP_ID)
        self.assertTrue(gate.validate_promotion_plan(promotion, RELEASE, trusted_runtime_sql, app_id))

    def test_rejects_unexpected_types_and_destructive_or_unapproved_replacement(self):
        plan = self.plan("baseline_full_plan")
        plan["resource_changes"].append({
            "address": "azurerm_role_assignment.private_value",
            "type": "azurerm_role_assignment",
            "mode": "managed",
            "change": {"actions": ["create"]},
        })
        with self.assertRaisesRegex(ValueError, "unexpected managed resource") as error:
            self.validate(plan)
        self.assertNotIn("private_value", str(error.exception))

        for address, actions, reason in (
            ("module.container_app.azapi_resource.container_app", ["delete"], None),
            ("module.sql_server.azurerm_mssql_server.this", ["delete", "create"], None),
            ("module.container_app.azapi_resource_action.post_creation_update", ["delete", "create"], "tainted"),
        ):
            with self.subTest(address=address):
                mutated = self.plan("baseline_full_plan")
                item = resource(mutated, address)
                item["change"]["actions"] = actions
                if reason:
                    item["action_reason"] = reason
                self.assert_rejected(mutated, "unsafe|destroy|replacement")

        harmless = self.plan("baseline_full_plan")
        patch = resource(harmless, "module.container_app.azapi_resource_action.post_creation_update")
        patch["change"]["actions"] = ["delete", "create"]
        patch["action_reason"] = "replace_by_triggers"
        self.validate(harmless)

    def test_rejects_owner_graph_permission_or_application_role_drift(self):
        mutations = (
            lambda p: resource(p, "azuread_application.api")["change"]["after"].update(
                owners=["00000000-0000-0000-0000-000000000099"]
            ),
            lambda p: resource(p, "azuread_service_principal.api")["change"]["after"].update(
                owners=["00000000-0000-0000-0000-000000000099"]
            ),
            lambda p: resource(p, "azuread_application.api")["change"]["after"].update(
                required_resource_access=[{"resource_app_id": "unexpected", "resource_access": [{"id": "x", "type": "Role"}]}]
            ),
            lambda p: resource(p, "azuread_application.api")["change"]["after"]["api"][0].update(
                known_client_applications=["unexpected"]
            ),
            lambda p: resource(p, "azuread_application.api")["change"]["after"]["app_role"][0].update(
                allowed_member_types=["Application"]
            ),
        )
        for mutate in mutations:
            with self.subTest(mutation=mutate):
                plan = self.plan("baseline_full_plan")
                mutate(plan)
                self.assert_rejected(plan, "owned|Graph permissions|application roles|delegated scope")

    def test_rejects_mismatched_application_id_and_sensitive_sql_outputs(self):
        plan = self.plan("baseline_infrastructure_plan")
        plan["output_changes"]["container_app_id"]["after"] = EXPECTED_APP_ID + "-other"
        self.assert_rejected(plan, "Container App ID output")

        plan = self.plan("baseline_infrastructure_plan")
        plan["output_changes"]["runtime_sql_connection_string"]["after_sensitive"] = False
        self.assert_rejected(plan, "SQL connection outputs")

        plan = self.plan("baseline_full_plan")
        plan["complete"] = False
        self.assert_rejected(plan, "errored or contains deferred changes")

        plan = self.plan("baseline_infrastructure_plan")
        scope = resource(plan, "azuread_application.api")["change"]["after"]["api"][0]["oauth2_permission_scope"][0]
        scope["id"] = "not-a-uuid"
        self.assert_rejected(plan, "identifiers must be UUIDs")

    def test_rejects_unsafe_sql_firewall_database_cost_and_secret_changes(self):
        mutations = (
            lambda p: resource(
                p, 'module.sql_server.azurerm_mssql_firewall_rule.this["azure_services"]'
            )["change"]["after"].update(end_ip_address="255.255.255.255"),
            lambda p: resource(p, "azapi_resource.sql_database")["change"]["after"]["body"]["properties"].update(
                useFreeLimit=False
            ),
            lambda p: resource(p, "azurerm_consumption_budget_resource_group.prod")["change"]["after"].update(
                amount=500
            ),
            lambda p: resource(p, "module.log_analytics.azurerm_log_analytics_workspace.this")["change"]["after"].update(
                daily_quota_gb=10
            ),
            lambda p: resource(p, "module.sql_server.azurerm_mssql_server.this")["change"]["after"].update(
                administrator_login_password="not-a-real-secret"
            ),
        )
        for mutate in mutations:
            with self.subTest(mutation=mutate):
                plan = self.plan("baseline_full_plan")
                mutate(plan)
                self.assert_rejected(plan, "firewall|free offer|cost budget|Log Analytics|Entra-only")

    def test_rejects_app_target_image_auth_sidecar_ingress_and_job_drift(self):
        mutations = (
            lambda p: resource(p, "module.container_app.azapi_resource.container_app")["change"]["after"]["body"]["properties"]["template"]["containers"][0].update(
                image="registry.example/attacker/app@sha256:" + "c" * 64
            ),
            lambda p: resource(p, "module.container_app.azapi_resource.container_app")["change"]["after"]["body"]["properties"]["template"]["containers"].append(
                copy.deepcopy(resource(p, "module.container_app.azapi_resource.container_app")["change"]["after"]["body"]["properties"]["template"]["containers"][0])
            ),
            lambda p: resource(p, "module.container_app.azapi_resource.container_app")["change"]["after"]["body"]["properties"]["configuration"]["ingress"].update(
                external=False
            ),
            lambda p: resource(p, "module.container_app.azapi_resource.container_app")["change"]["after"]["body"]["properties"]["template"]["containers"][0]["env"][4].update(
                value="00000000-0000-0000-0000-000000000003:00000000-0000-0000-0000-000000000002"
            ),
            lambda p: resource(p, "module.container_app.azapi_resource.container_app")["change"]["after"]["body"]["properties"]["template"]["containers"][0]["env"][1].update(
                secretRef="injected"
            ),
            lambda p: resource(p, "azurerm_container_app_job.migration")["change"]["after"]["template"][0]["container"][0].update(
                command=["dotnet"]
            ),
        )
        for mutate in mutations:
            with self.subTest(mutation=mutate):
                plan = self.plan("baseline_full_plan")
                mutate(plan)
                self.assert_rejected(plan)

        plan = self.plan("baseline_full_plan")
        with self.assertRaisesRegex(ValueError, "target"):
            bootstrap.validate_baseline_plan(
                plan,
                expected_image=EXPECTED_IMAGE,
                expected_owner=EXPECTED_OWNER,
                expected_app_id=EXPECTED_APP_ID + "-other",
            )

    def test_only_approved_api_unknowns_are_accepted(self):
        plan = self.plan("baseline_full_plan")
        self.validate(plan)
        app = resource(plan, "module.container_app.azapi_resource.container_app")
        unknown = app["change"].setdefault("after_unknown", {})
        unknown["body"] = {"properties": {"template": {"containers": [{"env": [None, {"value": True}]}]}}}
        self.assert_rejected(plan, "unknown input values")

        plan = self.plan("baseline_full_plan")
        plan["relevant_attributes"] = [
            item for item in plan["relevant_attributes"]
            if not (item.get("resource") == "azuread_application.api" and item.get("attribute") == ["client_id"])
        ]
        self.assert_rejected(plan, "computed-value dependency")


if __name__ == "__main__":
    unittest.main()
