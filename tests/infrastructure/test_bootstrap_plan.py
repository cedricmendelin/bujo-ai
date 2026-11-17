import copy
import json
import os
import subprocess
import sys
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / ".github" / "scripts"))
from check_bootstrap_plan import validate_plan
import bootstrap_review


class BootstrapPlanTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        result = subprocess.run(
            ["terraform", "-chdir=infra", "test", "-json", "-verbose",
             "-filter=" + os.path.join("tests", "bootstrap.tftest.hcl")],
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

    def test_sql_free_limit_plan_keeps_paid_continuation_and_idle_auto_pause(self):
        resource = next(
            item for item in self.plan["resource_changes"]
            if item["address"] == "azapi_resource.sql_database"
        )
        database = resource["change"]["after"]
        self.assertEqual(
            database["body"]["sku"],
            {"name": "GP_S_Gen5", "tier": "GeneralPurpose", "family": "Gen5", "capacity": 2},
        )
        self.assertEqual(
            database["body"]["properties"],
            {
                "maxSizeBytes": 34359738368,
                "minCapacity": 0.5,
                "autoPauseDelay": 60,
                "useFreeLimit": True,
                "freeLimitExhaustionBehavior": "BillForUsage",
            },
        )

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

    def test_rejects_widened_azure_services_firewall(self):
        plan = copy.deepcopy(self.plan)
        firewall = next(item for item in plan["resource_changes"] if "azurerm_mssql_firewall_rule" in item["address"])
        firewall["change"]["after"]["end_ip_address"] = "255.255.255.255"
        with self.assertRaisesRegex(ValueError, "firewall"):
            validate_plan(plan)

    def test_review_redacts_values_and_arbitrary_map_keys(self):
        plan = copy.deepcopy(self.plan)
        resource = next(item for item in plan["resource_changes"] if item["address"] == "azapi_resource.sql_database")
        resource["change"]["after"]["name"] = "sql.private.example.com"
        resource["change"]["after"]["tags"] = {"private-secret": "sensitive-value"}
        resource["change"]["after"]["body"] = {"tenant": "11111111-1111-1111-1111-111111111111"}
        resource["change"]["after"]["server_id"] = "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/private"
        package = bootstrap_review.review_package(
            plan, "a" * 64, "1.17.0", {name: "1.2.3" for name in bootstrap_review.PROVIDERS},
            "Private Subscription",
            '# azapi_resource.sql_database will be created\n'
            '  + name = "sql.private.example.com"\n'
            '  + arbitrary_secret = "sensitive-value"\n'
            '  + tags = { "private-secret" = "value" }\n',
        )
        text = json.dumps(package)
        for secret in ("sql.private.example.com", "private-secret", "sensitive-value",
                       "11111111-1111-1111-1111-111111111111", "/subscriptions/22222222",
                       "Private Subscription"):
            self.assertNotIn(secret, text)
        self.assertIn("azapi_resource.sql_database", text)
        self.assertIn("<redacted>", text)
        self.assertIn("+ name = <redacted>", text)

    def test_review_refuses_disallowed_resources_and_job_triggers(self):
        plan = copy.deepcopy(self.plan)
        plan["resource_changes"].append({
            "address": "azurerm_role_assignment.sql", "type": "azurerm_role_assignment",
            "mode": "managed", "change": {"actions": ["create"]},
        })
        args = ("a" * 64, "1.17.0", {name: "1.2.3" for name in bootstrap_review.PROVIDERS}, "test")
        with self.assertRaisesRegex(ValueError, "Unexpected managed resource"):
            bootstrap_review.review_package(plan, *args)
        plan = copy.deepcopy(self.plan)
        job = next(item for item in plan["resource_changes"] if item["address"] == "azurerm_container_app_job.migration")
        job["change"]["after"]["schedule_trigger_config"] = [{"cron_expression": "* * * * *"}]
        with self.assertRaisesRegex(ValueError, "not the inert manual placeholder"):
            bootstrap_review.review_package(plan, *args)

    def test_local_auth_requires_matching_user_and_subscription(self):
        account = {"id": "sub", "tenantId": "tenant", "user": {"type": "user"}}
        env = {"ARM_SUBSCRIPTION_ID": "sub", "ARM_TENANT_ID": "tenant"}
        bootstrap_review.check_auth(account, "sub", env)
        for changed in (
            {**env, "ARM_CLIENT_SECRET": "secret"},
            {**env, "ARM_USE_OIDC": "true"},
            {**env, "ARM_ACCESS_KEY": "secret"},
            {**env, "ARM_SUBSCRIPTION_ID": "other"},
        ):
            with self.subTest(env=changed):
                with self.assertRaises(ValueError):
                    bootstrap_review.check_auth(account, "sub", changed)
        with self.assertRaises(ValueError):
            bootstrap_review.check_auth(account, "other", env)
        with self.assertRaises(ValueError):
            bootstrap_review.check_auth({**account, "user": {"type": "servicePrincipal"}}, "sub", env)

    def test_plan_subscription_evidence_must_match(self):
        subscription = "00000000-0000-0000-0000-000000000004"
        plan = copy.deepcopy(self.plan)
        environment = next(item for item in plan["resource_changes"] if item["address"] == "module.container_app_environment.azapi_resource.this")
        environment["change"]["after"]["parent_id"] = f"/subscriptions/{subscription}/resourceGroups/test-rg"
        bootstrap_review.check_plan_subscription(plan, subscription)
        with self.assertRaisesRegex(ValueError, "resource IDs"):
            bootstrap_review.check_plan_subscription(plan, "11111111-1111-1111-1111-111111111111")
        with self.assertRaisesRegex(ValueError, "resource IDs"):
            bootstrap_review.check_plan_subscription({"resource_changes": []}, subscription)

    def test_oidc_review_uses_the_same_checker_without_accepting_persistent_credentials(self):
        account = {
            "id": "sub", "tenantId": "tenant", "user": {"type": "servicePrincipal", "name": "client"},
        }
        env = {
            "ARM_SUBSCRIPTION_ID": "sub", "ARM_TENANT_ID": "tenant", "ARM_CLIENT_ID": "client",
            "ARM_USE_OIDC": "true", "ARM_USE_AZUREAD": "true", "ARM_USE_CLI": "false",
        }
        bootstrap_review.check_oidc_auth(account, "sub", env)
        for key, value in (
            ("ARM_CLIENT_SECRET", "private"), ("ARM_ACCESS_KEY", "private"),
            ("ARM_CLIENT_ID", "other"), ("ARM_USE_OIDC", "false"),
            ("ARM_SUBSCRIPTION_ID", "other"), ("ARM_TENANT_ID", "other"),
            ("ARM_USE_MSI", "true"),
        ):
            with self.subTest(key=key), self.assertRaises(ValueError):
                bootstrap_review.check_oidc_auth(account, "sub", env | {key: value})
        with self.assertRaises(ValueError):
            bootstrap_review.check_oidc_auth(account | {"user": {"type": "user"}}, "sub", env)

    def test_bootstrap_provenance_requires_clean_matching_commit(self):
        commit = "a" * 40
        with patch.object(bootstrap_review, "run_text", side_effect=["", commit]):
            self.assertEqual(commit, bootstrap_review.check_checkout(commit))
        with patch.object(bootstrap_review, "run_text", return_value=" M infra/main.tf"):
            with self.assertRaisesRegex(ValueError, "clean"):
                bootstrap_review.check_checkout()
        with patch.object(bootstrap_review, "run_text", side_effect=["", "b" * 40]):
            with self.assertRaisesRegex(ValueError, "commit"):
                bootstrap_review.check_checkout(commit)

    def test_workflows_do_not_use_interactive_azure_login(self):
        workflows = ROOT / ".github" / "workflows"
        for path in workflows.glob("*.yml"):
            with self.subTest(workflow=path.name):
                self.assertNotRegex(path.read_text(encoding="utf-8"), r"(?i)\baz\s+login\b")
        deploy = (workflows / "deploy.yml").read_text(encoding="utf-8")
        self.assertIn('ARM_USE_OIDC: "true"', deploy)
        self.assertIn("id-token: write", deploy)

    def test_modified_plan_is_rejected_before_rendering(self):
        import tempfile
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "stage1.tfplan"
            path.write_bytes(b"original")
            reviewed_hash = bootstrap_review.sha256(path)
            path.write_bytes(b"modified")
            account = {"id": "sub", "tenantId": "tenant", "user": {"type": "user"}}
            env = {"ARM_SUBSCRIPTION_ID": "sub", "ARM_TENANT_ID": "tenant"}
            args = ["bootstrap_review.py", "--plan", str(path), "--subscription", "sub",
                    "--expected-sha256", reviewed_hash]
            with patch.object(sys, "argv", args), patch.dict("os.environ", env, clear=True), \
                    patch.object(bootstrap_review, "run_json", return_value=account) as run:
                self.assertEqual(bootstrap_review.main(), 1)
                self.assertEqual(run.call_count, 1)

    def test_review_command_checks_plan_and_emits_redacted_package(self):
        import contextlib
        import io
        import tempfile
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "stage1.tfplan"
            path.write_bytes(b"saved-plan")
            digest = bootstrap_review.sha256(path)
            subscription = "00000000-0000-0000-0000-000000000004"
            account = {"id": subscription, "name": "Private", "tenantId": "tenant", "user": {"type": "user"}}
            env = {"ARM_SUBSCRIPTION_ID": subscription, "ARM_TENANT_ID": "tenant"}
            version = {"terraform_version": "1.17.0",
                       "provider_selections": {name: "1.2.3" for name in bootstrap_review.PROVIDERS}}
            plan = copy.deepcopy(self.plan)
            environment = next(item for item in plan["resource_changes"] if item["address"] == "module.container_app_environment.azapi_resource.this")
            environment["change"]["after"]["parent_id"] = f"/subscriptions/{subscription}/resourceGroups/test-rg"
            args = ["bootstrap_review.py", "--plan", str(path), "--subscription", subscription,
                    "--expected-sha256", digest]
            output = io.StringIO()
            with patch.object(sys, "argv", args), patch.dict("os.environ", env, clear=True), \
                    patch.object(bootstrap_review, "run_json", side_effect=[account, version, plan]), \
                    patch.object(bootstrap_review, "check_checkout", return_value="a" * 40), \
                    patch.object(bootstrap_review, "run_text", return_value=(
                        '# azapi_resource.sql_database will be created\n'
                        '  + name = "private.example.com"\n'
                    )), contextlib.redirect_stdout(output):
                self.assertEqual(bootstrap_review.main(), 0)
            package = json.loads(output.getvalue())
            self.assertEqual(package["plan_sha256"], digest)
            self.assertEqual(package["source_commit"], "a" * 40)
            self.assertIn("  + name = <redacted>", package["redacted_terraform_show"])
            self.assertNotIn("private.example.com", output.getvalue())


if __name__ == "__main__":
    unittest.main()
