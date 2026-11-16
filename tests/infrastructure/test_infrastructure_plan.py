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
    "pwa_client_configuration_plan",
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

    def test_pwa_client_plan_binds_owner_callback_scope_and_service_principal(self):
        full_plan = self.plan("baseline_full_plan")
        self.validate(full_plan)
        pwa = resource(full_plan, "azuread_application.pwa_client")
        pwa_unknown = set(bootstrap._unknown_paths(pwa["change"]["after_unknown"]))
        self.assertIn("web[0].redirect_uris[0]", pwa_unknown)
        self.assertIn("required_resource_access[0].resource_app_id", pwa_unknown)
        self.assertIn(("module.container_app.azapi_resource.container_app", ("output",)), {
            (item.get("resource"), tuple(item.get("attribute") or []))
            for item in full_plan.get("relevant_attributes", [])
        })

        known_plan = self.plan("pwa_client_configuration_plan")
        self.validate(known_plan)
        application = resource(known_plan, "azuread_application.pwa_client")["change"]["after"]
        service_principal = resource(known_plan, "azuread_service_principal.pwa_client")["change"]["after"]
        api_permission = application["required_resource_access"][0]
        self.assertEqual(application["owners"], [EXPECTED_OWNER])
        self.assertEqual(application["display_name"], "app-test1234-pwa")
        self.assertEqual(
            application["web"][0]["redirect_uris"],
            ["https://test-app.test.switzerlandnorth.azurecontainerapps.io/signin-oidc"],
        )
        self.assertEqual(api_permission["resource_app_id"], "00000000-0000-0000-0000-000000000031")
        self.assertEqual(api_permission["resource_access"][0], {
            "id": "00000000-0000-0000-0000-000000000011",
            "type": "Scope",
        })
        self.assertEqual(service_principal["owners"], [EXPECTED_OWNER])
        self.assertEqual(service_principal["client_id"], application["client_id"])

    def test_rejects_pwa_ownership_name_and_application_configuration_drift(self):
        mutations = (
            lambda p: resource(p, "azuread_application.pwa_client")["change"]["after"].update(owners=[]),
            lambda p: resource(p, "azuread_application.pwa_client")["change"]["after"].update(
                owners=[EXPECTED_OWNER, "00000000-0000-0000-0000-000000000099"]
            ),
            lambda p: resource(p, "azuread_service_principal.pwa_client")["change"]["after"].update(
                owners=["00000000-0000-0000-0000-000000000099"]
            ),
            lambda p: resource(p, "azuread_application.pwa_client")["change"]["after"].update(
                display_name="app-other-pwa"
            ),
            lambda p: resource(p, "azuread_application.pwa_client")["change"]["after"].update(
                sign_in_audience="AzureADMultipleOrgs"
            ),
            lambda p: resource(p, "azuread_application.pwa_client")["change"]["after"].update(
                prevent_duplicate_names=False
            ),
            lambda p: resource(p, "azuread_application.pwa_client")["change"]["after"].update(
                api=[{"known_client_applications": ["unexpected"]}]
            ),
            lambda p: resource(p, "azuread_service_principal.pwa_client")["change"]["after"].update(
                use_existing=True
            ),
            lambda p: resource(p, "azuread_application.pwa_client")["change"]["after"]["web"][0][
                "implicit_grant"
            ][0].update(id_token_issuance_enabled=True),
            lambda p: resource(p, "azuread_application.pwa_client")["change"]["after"]["web"][0].update(
                implicit_grant="unexpected"
            ),
        )
        for mutate in mutations:
            with self.subTest(mutation=mutate):
                plan = self.plan("pwa_client_configuration_plan")
                mutate(plan)
                self.assert_rejected(plan, "PWA")

    def test_rejects_pwa_redirect_uri_drift(self):
        known_plan = self.plan("pwa_client_configuration_plan")
        application = resource(known_plan, "azuread_application.pwa_client")["change"]["after"]
        redirect_uri = application["web"][0]["redirect_uris"][0]
        host = redirect_uri.removeprefix("https://").removesuffix("/signin-oidc")
        for invalid_uri in (
            f"http://{host}/signin-oidc",
            f"https://{host}/callback",
            f"https://{host}/signin-oidc/",
            f"https://{host}/signin-oidc?returnUrl=/",
            f"https://*.{host}/signin-oidc",
            "https://localhost/signin-oidc",
            "https://other.example.test/signin-oidc",
        ):
            with self.subTest(uri=invalid_uri):
                plan = self.plan("pwa_client_configuration_plan")
                pwa = resource(plan, "azuread_application.pwa_client")["change"]["after"]
                pwa["web"][0]["redirect_uris"][0] = invalid_uri
                self.assert_rejected(plan, "PWA redirect URI")

        plan = self.plan("pwa_client_configuration_plan")
        pwa = resource(plan, "azuread_application.pwa_client")["change"]["after"]
        pwa["web"][0]["redirect_uris"].append(redirect_uri)
        self.assert_rejected(plan, "PWA redirect URI")

        plan = self.plan("pwa_client_configuration_plan")
        container_app = resource(plan, "module.container_app.azapi_resource.container_app")["change"]
        container_app["after"]["output"] = {}
        if isinstance(container_app.get("before"), dict):
            container_app["before"]["output"] = {}
        self.assert_rejected(plan, "Container App FQDN")

    def test_rejects_pwa_permission_and_credential_drift(self):
        mutations = (
            lambda p: resource(p, "azuread_application.pwa_client")["change"]["after"][
                "required_resource_access"
            ][0].update(resource_app_id="00000003-0000-0000-c000-000000000000"),
            lambda p: resource(p, "azuread_application.pwa_client")["change"]["after"][
                "required_resource_access"
            ][0]["resource_access"][0].update(type="Role"),
            lambda p: resource(p, "azuread_application.pwa_client")["change"]["after"][
                "required_resource_access"
            ][0]["resource_access"][0].update(id="00000000-0000-0000-0000-000000000099"),
            lambda p: resource(p, "azuread_application.pwa_client")["change"]["after"][
                "required_resource_access"
            ][0]["resource_access"].append({"id": "unexpected", "type": "Scope"}),
            lambda p: resource(p, "azuread_application.pwa_client")["change"]["after"][
                "required_resource_access"
            ].append({"resource_app_id": "unexpected", "resource_access": []}),
            lambda p: resource(p, "azuread_application.pwa_client")["change"]["after"].update(
                password=[{"display_name": "unexpected"}]
            ),
            lambda p: resource(p, "azuread_application.pwa_client")["change"]["after"].update(
                key_credentials=[{"display_name": "unexpected"}]
            ),
            lambda p: resource(p, "azuread_application.pwa_client")["change"]["after"].update(
                federated_identity_credentials=[{"display_name": "unexpected"}]
            ),
            lambda p: resource(p, "azuread_application.pwa_client")["change"]["after"].update(
                certificate=[{"display_name": "unexpected"}]
            ),
        )
        for mutate in mutations:
            with self.subTest(mutation=mutate):
                plan = self.plan("pwa_client_configuration_plan")
                mutate(plan)
                self.assert_rejected(plan, "PWA")

        for address, resource_type in (
            ("azuread_application_password.pwa_client", "azuread_application_password"),
            ("azuread_application_certificate.pwa_client", "azuread_application_certificate"),
            (
                "azuread_application_federated_identity_credential.pwa_client",
                "azuread_application_federated_identity_credential",
            ),
        ):
            with self.subTest(address=address):
                plan = self.plan("baseline_full_plan")
                plan["resource_changes"].append({
                    "address": address,
                    "type": resource_type,
                    "mode": "managed",
                    "change": {"actions": ["create"], "after": {}},
                })
                self.assert_rejected(plan, "unexpected managed resource")

    def test_pwa_unknown_values_require_their_specific_producer_dependencies(self):
        cases = (
            (
                "redirect",
                "module.container_app.azapi_resource.container_app",
                ["output"],
                lambda app: (
                    app["change"]["after"]["web"][0]["redirect_uris"].__setitem__(0, None),
                    app["change"].setdefault("after_unknown", {}).setdefault("web", [{}])[0]
                    .setdefault("redirect_uris", [True]),
                ),
            ),
            (
                "API application",
                "azuread_application.api",
                ["client_id"],
                lambda app: (
                    app["change"]["after"]["required_resource_access"][0].__setitem__(
                        "resource_app_id", None
                    ),
                    app["change"].setdefault("after_unknown", {}).setdefault(
                        "required_resource_access", [{}]
                    )[0].__setitem__("resource_app_id", True),
                ),
            ),
            (
                "generated API scope",
                "random_uuid.api_scope",
                ["result"],
                lambda app: (
                    app["change"]["after"]["required_resource_access"][0]["resource_access"][0]
                    .__setitem__("id", None),
                    app["change"].setdefault("after_unknown", {}).setdefault(
                        "required_resource_access", [{}]
                    )[0].setdefault("resource_access", [{}])[0].__setitem__("id", True),
                ),
            ),
        )
        for label, dependency_resource, dependency_attribute, make_unknown in cases:
            with self.subTest(dependency=label):
                plan = self.plan("pwa_client_configuration_plan")
                pwa = resource(plan, "azuread_application.pwa_client")
                make_unknown(pwa)
                plan["relevant_attributes"] = [
                    item for item in plan["relevant_attributes"]
                    if not (
                        item.get("resource") == dependency_resource
                        and item.get("attribute") == dependency_attribute
                    )
                ]
                self.assert_rejected(plan, "computed-value dependency")

        plan = self.plan("pwa_client_configuration_plan")
        pwa = resource(plan, "azuread_application.pwa_client")
        pwa["change"]["after"]["client_id"] = None
        pwa["change"]["after_unknown"]["client_id"] = True
        principal = resource(plan, "azuread_service_principal.pwa_client")
        principal["change"]["after"]["client_id"] = None
        principal["change"]["after_unknown"]["client_id"] = True
        plan["relevant_attributes"] = [
            item for item in plan["relevant_attributes"]
            if not (
                item.get("resource") == "azuread_application.pwa_client"
                and item.get("attribute") == ["client_id"]
            )
        ]
        self.assert_rejected(plan, "computed-value dependency")

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

    def test_live_migration_identity_set_unknown_requires_the_approved_dependency(self):
        plan = self.plan("baseline_full_plan")
        job = resource(plan, "azurerm_container_app_job.migration")
        job["change"]["after_unknown"]["identity"][0]["identity_ids"] = True
        job["change"]["after"]["identity"][0].pop("identity_ids", None)
        self.assertIn(("azurerm_container_app_job.migration", "create"), self.validate(plan))

        plan["relevant_attributes"] = [
            item for item in plan["relevant_attributes"]
            if not (item.get("resource") == "module.migration_identity.azurerm_user_assigned_identity.this"
                    and item.get("attribute") == ["id"])
        ]
        self.assert_rejected(plan, "computed-value dependency")

    def test_live_migration_unknown_shape_does_not_allow_unknown_identity_type_or_execution(self):
        for field in ("type", "image", "command", "env"):
            with self.subTest(field=field):
                plan = self.plan("baseline_full_plan")
                job = resource(plan, "azurerm_container_app_job.migration")
                unknown = job["change"]["after_unknown"]
                unknown["identity"][0]["identity_ids"] = True
                if field == "type":
                    unknown["identity"][0]["type"] = True
                else:
                    unknown["template"][0]["container"][0][field] = True
                self.assert_rejected(plan, "unknown input values")

    def test_live_entra_tag_metadata_is_computed_without_allowing_unknown_permissions(self):
        plan = self.plan("baseline_full_plan")
        for address, fields in (
            ("azuread_application.api", ("feature_tags",)),
            ("azuread_service_principal.api", ("feature_tags", "features")),
        ):
            change = resource(plan, address)["change"]
            for field in fields:
                change["after_unknown"][field] = True
                change["after"].pop(field, None)
        self.validate(plan)

        resource(plan, "azuread_application.api")["change"]["after_unknown"]["required_resource_access"] = True
        with self.assertRaisesRegex(ValueError, "azuread_application.api"):
            self.validate(plan)

        plan = self.plan("baseline_full_plan")
        unknown = resource(plan, "azuread_application.api")["change"]["after_unknown"]
        unknown["feature_tags"] = [{"private_key_must_not_appear": True}]
        with self.assertRaisesRegex(ValueError, "unknown input values") as error:
            self.validate(plan)
        self.assertNotIn("private_key_must_not_appear", str(error.exception))

    def retained_template_plan(self):
        plan = self.plan("baseline_full_plan")
        app = resource(plan, "module.container_app.azapi_resource.container_app")["change"]
        app["actions"] = ["update"]
        app["after"]["id"] = EXPECTED_APP_ID
        app["after"]["body"]["properties"]["template"]["containers"][0]["env"] = []
        app["before"] = copy.deepcopy(app["after"])
        app["after_unknown"].pop("id", None)
        app["after_unknown"].get("body", {}).get("properties", {}).pop("template", None)
        patch = resource(plan, "module.container_app.azapi_resource_action.post_creation_update")
        patch["change"]["actions"] = ["delete", "create"]
        patch["action_reason"] = "replace_by_triggers"
        return plan

    def test_existing_app_uses_the_scheduled_patch_for_environment_validation(self):
        plan = self.retained_template_plan()
        inventory = self.validate(plan)
        self.assertIn(("module.container_app.azapi_resource_action.post_creation_update", "delete"), inventory)

    def test_retained_environment_requires_an_unchanged_template_and_an_actual_patch(self):
        for mutation in ("template", "patch", "target", "missing-target", "sql", "allowlist", "secret"):
            with self.subTest(mutation=mutation):
                plan = self.retained_template_plan()
                app = resource(plan, "module.container_app.azapi_resource.container_app")["change"]
                patch = resource(plan, "module.container_app.azapi_resource_action.post_creation_update")["change"]
                environment = patch["after"]["body"]["properties"]["template"]["containers"][0]["env"]
                if mutation == "template":
                    app["after"]["body"]["properties"]["template"]["containers"][0]["env"] = [
                        {"name": "unexpected", "value": "unexpected"}
                    ]
                elif mutation == "patch":
                    patch["actions"] = ["no-op"]
                elif mutation == "target":
                    patch["after"]["resource_id"] = EXPECTED_APP_ID + "-other"
                elif mutation == "missing-target":
                    patch["after"]["resource_id"] = None
                    patch["after_unknown"].pop("resource_id", None)
                elif mutation == "sql":
                    environment[0]["value"] = "Server=unexpected;Password=unexpected"
                elif mutation == "allowlist":
                    environment[4]["value"] = "unexpected"
                else:
                    environment[1]["secretRef"] = "unexpected"
                self.assert_rejected(plan)


if __name__ == "__main__":
    unittest.main()
