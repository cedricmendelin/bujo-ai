import importlib.util
import hashlib
import io
import json
import subprocess
import unittest
from pathlib import Path
from unittest.mock import patch


spec = importlib.util.spec_from_file_location(
    "deployment_preflight",
    Path(__file__).resolve().parents[2] / ".github/scripts/deployment_preflight.py",
)
preflight = importlib.util.module_from_spec(spec)
spec.loader.exec_module(preflight)


class PreflightTests(unittest.TestCase):
    def test_plans_and_diagnostics_require_an_actually_private_storage_container(self):
        env = {"TFSTATE_STORAGE_ACCOUNT": "private", "TFSTATE_CONTAINER": "private"}
        def private_container(args, **_):
            query = args[args.index("--query") + 1]
            output = "" if query == "properties.publicAccess" else '{"publicAccess":null}'
            return subprocess.CompletedProcess(args, 0, stdout=output)

        with patch.object(preflight.shutil, "which", return_value="az"), \
             patch.object(preflight.subprocess, "run", side_effect=private_container) as run:
            preflight.require_private_storage(env)
        args = run.call_args.args[0]
        self.assertEqual("{publicAccess:properties.publicAccess}", args[args.index("--query") + 1])
        self.assertEqual("login", args[args.index("--auth-mode") + 1])
        self.assertEqual("json", args[args.index("--output") + 1])

    def test_private_storage_rejects_public_or_unverified_access(self):
        env = {"TFSTATE_STORAGE_ACCOUNT": "private", "TFSTATE_CONTAINER": "private"}
        for value in ({"publicAccess": "blob"}, {"publicAccess": "container"},
                      {}, None, [], {"publicAccess": False}):
            with self.subTest(value=value), patch.object(preflight.shutil, "which", return_value="az"), \
                 patch.object(preflight.subprocess, "run", return_value=subprocess.CompletedProcess(
                     ["az"], 0, stdout=json.dumps(value),
                 )):
                with self.assertRaisesRegex(ValueError, "must not allow public"):
                    preflight.require_private_storage(env)

    def test_private_storage_rejects_failed_commands_and_invalid_json(self):
        env = {"TFSTATE_STORAGE_ACCOUNT": "private", "TFSTATE_CONTAINER": "private"}
        for code, output in ((1, '{"publicAccess":null}'), (0, ""), (0, "not-json")):
            with self.subTest(code=code, output=output), \
                 patch.object(preflight.shutil, "which", return_value="az"), \
                 patch.object(preflight.subprocess, "run", return_value=subprocess.CompletedProcess(
                     ["az"], code, stdout=output,
                 )):
                with self.assertRaises(ValueError):
                    preflight.require_private_storage(env)

    def test_infrastructure_configuration_does_not_depend_on_application_setup(self):
        private = "private-setting"
        env = {name: private for name in preflight.PROVISIONING_SETTINGS}
        result = preflight.report(env, mode="infrastructure")
        self.assertTrue(result["ready"])
        self.assertNotIn(private, str(result))
        self.assertNotIn("API_ALLOWED_PRINCIPAL_OBJECT_IDS", {item["requirement"] for item in result["checks"]})
        self.assertIn("permissions are not certified", result["scope"])
        self.assertIn("SQL users/grants", str(result["deferred_until_after_provisioning"]))
        self.assertFalse(preflight.report(env)["ready"])
        self.assertFalse(preflight.report(env | {"TFSTATE_STORAGE_ACCOUNT": ""}, mode="infrastructure")["ready"])
        self.assertFalse(preflight.report(env | {"TFSTATE_KEY": "bootstrap/diagnostics/run/apply.log"},
                                          mode="infrastructure")["ready"])

    def test_presence_is_redacted_and_external_checks_are_unknown(self):
        secret = "sensitive-private-value"
        result = preflight.report({name: secret for name in preflight.SETTINGS})
        self.assertNotIn(secret, str(result))
        self.assertFalse(result["ready"])
        self.assertTrue(any(check["status"] == "unknown" for check in result["checks"]))

    def test_missing_and_blank_settings_block(self):
        result = preflight.report({"AZURE_CLIENT_ID": "  "})
        self.assertEqual("missing", result["checks"][0]["status"])
        self.assertFalse(result["ready"])
        self.assertIn("/issues/55", result["remediation"])

    def test_owner_confirmation_is_explicit_and_never_hides_a_failed_live_check(self):
        env = {name: "private" for name in preflight.SETTINGS}
        env.update(BUJO_OWNER_SETUP_CONFIRMED="true", BUJO_DEPLOYMENT_ENABLED="true")
        self.assertTrue(preflight.report(env)["ready"])
        self.assertTrue(all(
            "not an independent permission test" in check["evidence"]
            for check in preflight.report(env)["checks"] if check["requirement"] in preflight.EXTERNAL_CHECKS
        ))
        self.assertFalse(preflight.report(env, [{
            "requirement": "live check", "status": "unknown", "evidence": "unavailable",
        }])["ready"])

    def test_anonymous_registry_check_requires_exact_manifest_and_tested_config(self):
        config_digest = "sha256:" + "c" * 64
        body = json.dumps({"config": {"digest": config_digest}}).encode()
        manifest = {
            "image": "ghcr.io/cedricmendelin/bujo-ai@sha256:" + hashlib.sha256(body).hexdigest(),
            "config_digest": config_digest,
        }
        calls = []

        def open_url(request, **_):
            calls.append(request)
            return io.BytesIO(b'{"token":"anonymous-test-token"}' if len(calls) % 2 else body)

        preflight.verify_public_image(manifest, open_url)
        with self.assertRaises(ValueError):
            preflight.verify_public_image(manifest | {"config_digest": "wrong"}, open_url)
        with self.assertRaises(ValueError):
            preflight.verify_public_image(manifest | {"image": "ghcr.io/other/repo:latest"}, open_url)
        with self.assertRaises(ValueError):
            preflight.verify_public_image(manifest | {
                "image": "ghcr.io/cedricmendelin/bujo-ai@sha256:" + "0" * 64,
            }, open_url)

    def test_live_bootstrap_requires_actual_job_scope_trigger_and_migration_identity(self):
        env = {
            "AZURE_SUBSCRIPTION_ID": "configured-subscription", "AZURE_TENANT_ID": "configured-tenant",
            "AZURE_RESOURCE_GROUP": "configured-group",
            "AZURE_CLIENT_ID": "configured-client",
        }
        account = {
            "id": env["AZURE_SUBSCRIPTION_ID"], "tenantId": env["AZURE_TENANT_ID"],
            "user": {"type": "servicePrincipal", "name": env["AZURE_CLIENT_ID"]},
        }
        job_id = (
            "/subscriptions/configured-subscription/resourceGroups/configured-group/"
            "providers/Microsoft.App/jobs/migration"
        )
        outputs = {
            "migration_job_id": {"value": job_id},
            "migration_identity_principal_id": {"value": "migration-principal"},
            "runtime_identity_principal_id": {"value": "runtime-principal"},
        }
        job = {
            "id": job_id, "properties": {
                "provisioningState": "Succeeded",
                "configuration": {"triggerType": "Manual", "replicaRetryLimit": 0},
            },
            "identity": {"userAssignedIdentities": {"identity": {"principalId": "migration-principal"}}},
        }
        with patch.object(preflight, "run_json", side_effect=[account, outputs, job]), \
             patch.object(preflight, "verify_public_image"):
            self.assertTrue(all(check["status"] == "verified" for check in preflight.inspect_live(env, {})))
        job["identity"]["userAssignedIdentities"]["identity"]["principalId"] = "wrong-principal"
        with patch.object(preflight, "run_json", side_effect=[account, outputs, job]), \
             patch.object(preflight, "verify_public_image"):
            checks = preflight.inspect_live(env, {})
        self.assertEqual("unknown", checks[1]["status"])

    def test_public_image_index_must_resolve_exact_tested_linux_image(self):
        config_digest = "sha256:" + "c" * 64
        image = json.dumps({"config": {"digest": config_digest}}).encode()
        child_digest = "sha256:" + hashlib.sha256(image).hexdigest()

        def verify(entries):
            index = json.dumps({"manifests": entries}).encode()
            responses = iter((b'{"token":"test-token"}', index, image))
            preflight.verify_public_image({
                "image": "ghcr.io/cedricmendelin/bujo-ai@sha256:" + hashlib.sha256(index).hexdigest(),
                "config_digest": config_digest,
            }, lambda *_args, **_kwargs: io.BytesIO(next(responses)))

        entry = {"platform": {"os": "linux", "architecture": "amd64"}, "digest": child_digest}
        verify([entry])
        for entries in ([], [entry, entry], [entry | {"digest": "sha256:" + "0" * 64}]):
            with self.subTest(entries=entries), self.assertRaises(ValueError):
                verify(entries)

    def test_live_inspection_errors_are_unknown_and_redacted(self):
        env = {name: "do-not-print" for name in preflight.SETTINGS}
        with patch.object(preflight, "run_json", side_effect=ValueError("do-not-print")), \
             patch.object(preflight, "verify_public_image", side_effect=ValueError("do-not-print")):
            checks = preflight.inspect_live(env, {})
        self.assertEqual(3, len(checks))
        self.assertTrue(all(check["status"] == "unknown" for check in checks))
        self.assertNotIn("do-not-print", str(checks))


if __name__ == "__main__":
    unittest.main()
