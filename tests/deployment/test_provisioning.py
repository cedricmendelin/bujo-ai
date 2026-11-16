import argparse
import contextlib
import importlib
import io
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / ".github" / "scripts"))
review = importlib.import_module("bootstrap_review")
COMMIT = "a" * 40
OWNER = "00000000-0000-0000-0000-000000000001"
CLIENT = "00000000-0000-0000-0000-000000000002"
SUB = "00000000-0000-0000-0000-000000000003"
APP = f"/subscriptions/{SUB}/resourceGroups/test/providers/Microsoft.App/containerApps/test"
IMAGE = "example.invalid/existing:retained"
EVIDENCE = {"expected_image": IMAGE, "expected_owner": OWNER, "expected_app_id": APP}


class ProvisioningTests(unittest.TestCase):
    def args(self, path, operation="review", **kwargs):
        return argparse.Namespace(
            plan=path, operation=operation, expected_sha256=kwargs.get("digest"),
            expected_commit=kwargs.get("commit"), plan_run_id=kwargs.get("run"),
            publish_plan=False, subscription=SUB,
        )

    def workflow_env(self):
        return {
            "GITHUB_ACTIONS": "true", "GITHUB_REPOSITORY": "cedricmendelin/bujo-ai",
            "GITHUB_EVENT_NAME": "workflow_dispatch", "GITHUB_REF": "refs/heads/main",
            "GITHUB_ACTOR": "cedricmendelin", "GITHUB_TRIGGERING_ACTOR": "cedricmendelin",
            "GITHUB_REPOSITORY_OWNER": "cedricmendelin",
        }

    def test_owner_dispatch_requires_main_and_the_actual_triggering_owner(self):
        env = self.workflow_env()
        review.check_workflow_context(env)
        for key, bad in (
            ("GITHUB_EVENT_NAME", "pull_request"), ("GITHUB_REF", "refs/heads/feature"),
            ("GITHUB_ACTOR", "other"), ("GITHUB_TRIGGERING_ACTOR", "other"),
            ("GITHUB_REPOSITORY", "other/repo"),
        ):
            with self.subTest(key=key), self.assertRaises(ValueError):
                review.check_workflow_context(env | {key: bad})

    def test_exact_v9_approval_and_current_main_are_required(self):
        with patch.object(review, "run_text", side_effect=["", "another plan"]):
            with self.assertRaisesRegex(ValueError, "approved scope"):
                review.check_infrastructure_approval()
        with patch.object(review, "run_text", return_value="b" * 40):
            with self.assertRaisesRegex(ValueError, "current merged main"):
                review.check_current_main(COMMIT)

    def test_approval_command_failure_reports_a_redacted_category_not_a_missing_approval(self):
        private = "private-token-url-and-response"
        for stderr, category in (
            (f"GitHub API 403 for https://example.invalid/{private}", "github-api-http-403"),
            ("The plan approval or binding label is missing.", "approval-labels-missing"),
            (f"The plan approval or binding label is missing. {private}", "command-failed"),
            (private, "command-failed"),
        ):
            with self.subTest(category=category), \
                 patch.object(review.shutil, "which", side_effect=lambda command: command), \
                 patch.object(review.subprocess, "run", side_effect=[
                     subprocess.CompletedProcess(["git"], 0, stdout="", stderr=""),
                     subprocess.CompletedProcess(["node"], 1, stdout=private, stderr=stderr),
                 ]):
                with self.assertRaisesRegex(ValueError, f"\\[{category}\\]") as caught:
                    review.check_infrastructure_approval()
                self.assertNotIn(private, str(caught.exception))
                self.assertNotIn("must restore", str(caught.exception))

    def test_approval_verifier_timeout_and_missing_executable_remain_blocking_and_redacted(self):
        for executable, failure, category in (
            ("node", subprocess.TimeoutExpired(["node", "private-argument"], 1), "command-timeout"),
            (None, None, "command-unavailable"),
        ):
            with self.subTest(category=category), \
                 patch.object(review.shutil, "which", side_effect=["git", executable]), \
                 patch.object(review.subprocess, "run", side_effect=[
                     subprocess.CompletedProcess(["git"], 0, stdout="", stderr=""),
                     failure,
                 ]):
                with self.assertRaisesRegex(ValueError, f"\\[{category}\\]") as caught:
                    review.check_infrastructure_approval()
                self.assertNotIn("private-argument", str(caught.exception))

    def test_private_plan_storage_never_overlaps_state(self):
        for key in ("bootstrap/plans/123/plan.tfplan", "bootstrap/diagnostics/123/apply.log", ""):
            with self.subTest(key=key), patch.dict(os.environ, {"TFSTATE_KEY": key}):
                with self.assertRaises(ValueError):
                    review.private_plan_blob("123", "plan.tfplan")
        with patch.dict(os.environ, {"TFSTATE_KEY": "prod.tfstate"}):
            self.assertEqual("bootstrap/plans/123/plan.tfplan", review.private_plan_blob("123", "plan.tfplan"))
            for run in ("../prod.tfstate", "0", "123/other"):
                with self.assertRaises(ValueError):
                    review.private_plan_blob(run, "plan.tfplan")

    def test_initialized_backend_must_match_private_journal_and_plan_storage(self):
        config = {
            "resource_group_name": "state-rg", "storage_account_name": "stateaccount",
            "container_name": "state", "key": "prod.tfstate",
        }
        env = dict(zip(
            ("TFSTATE_RESOURCE_GROUP", "TFSTATE_STORAGE_ACCOUNT", "TFSTATE_CONTAINER", "TFSTATE_KEY"),
            config.values(),
        ))
        config["use_azuread_auth"] = True
        with tempfile.TemporaryDirectory() as folder, patch.dict(os.environ, env):
            path = Path(folder) / "backend.json"
            path.write_text(json.dumps({"backend": {"type": "azurerm", "config": config}}), encoding="utf-8")
            review.check_private_backend(path)
            for changed in (
                config | {"key": "other.tfstate"}, config | {"access_key": "do-not-use"},
                config | {"use_azuread_auth": False},
            ):
                path.write_text(json.dumps({"backend": {"type": "azurerm", "config": changed}}), encoding="utf-8")
                with self.assertRaisesRegex(ValueError, "initialized Entra backend"):
                    review.check_private_backend(path)

    def test_saved_plan_from_another_source_never_downloads(self):
        with tempfile.TemporaryDirectory() as folder, \
             patch.dict(os.environ, self.workflow_env() | {"TFSTATE_KEY": "prod.tfstate"}), \
             patch.object(review, "run_json", return_value={
                 "event": "workflow_dispatch", "path": ".github/workflows/provision.yml",
                 "head_branch": "main", "head_sha": "b" * 40,
             }), patch.object(review, "transfer_plan") as transfer:
            with self.assertRaisesRegex(ValueError, "successful owner provisioning run"):
                review.download_reviewed_plan(
                    self.args(Path(folder) / "plan.tfplan", "apply", digest="c" * 64, run="123"), COMMIT,
                )
            transfer.assert_not_called()

    def test_remote_plan_recovery_binds_private_artifact_to_exact_run_attempt_and_owner_hash(self):
        run = {
            "event": "workflow_dispatch", "path": ".github/workflows/provision.yml",
            "head_branch": "main", "head_sha": COMMIT, "run_attempt": 2,
            "repository": {"full_name": "cedricmendelin/bujo-ai"},
            "conclusion": "success", "status": "completed",
            "actor": {"login": "cedricmendelin"}, "triggering_actor": {"login": "cedricmendelin"},
        }
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "plan.tfplan"
            args = self.args(path, "apply", digest="c" * 64, run="123")
            for attempt, accepted in (("2", True), ("1", False)):
                downloads = []

                def transfer(operation, run_id, filename, target):
                    downloads.append((operation, run_id, filename))
                    if filename == "review.json":
                        target.write_text(json.dumps({
                            "purpose": "infrastructure-plan", "source_commit": COMMIT,
                            "plan_sha256": args.expected_sha256,
                            "plan_run_id": "123", "plan_run_attempt": attempt,
                        }), encoding="utf-8")
                    else:
                        target.write_bytes(b"saved-artifact")

                with self.subTest(attempt=attempt), \
                     patch.dict(os.environ, self.workflow_env() | {"TFSTATE_KEY": "prod.tfstate"}), \
                     patch.object(review, "run_json", return_value=run), \
                     patch.object(review, "transfer_plan", side_effect=transfer):
                    if accepted:
                        review.download_reviewed_plan(args, COMMIT)
                        self.assertEqual(b"saved-artifact", path.read_bytes())
                        self.assertEqual(["review.json", "plan.tfplan"], [call[2] for call in downloads])
                    else:
                        with self.assertRaisesRegex(ValueError, "metadata differs"):
                            review.download_reviewed_plan(args, COMMIT)
                        self.assertEqual(["review.json"], [call[2] for call in downloads])

    def test_live_evidence_rejects_an_active_or_unknown_execution(self):
        app = {"id": APP, "properties": {"template": {"containers": [{"image": IMAGE}]}}}
        for status in ("Running", "Processing", None):
            with self.subTest(status=status), \
                 patch.dict(os.environ, {"TF_VAR_deployment_client_id": CLIENT, "ARM_SUBSCRIPTION_ID": SUB,
                                         "TF_VAR_resource_group_name": "test"}), \
                 patch.object(review, "run_json", side_effect=[
                     [app], OWNER, [{"name": "finished"}, {"name": "migration"}],
                     [{"properties": {"status": "Succeeded"}}], [{"properties": {"status": status}}],
                 ]):
                with self.assertRaisesRegex(ValueError, "active or indeterminate"):
                    review.baseline_evidence()

    def test_absent_or_ambiguous_live_app_never_substitutes_an_image(self):
        for apps in ([], [{"id": APP}, {"id": APP}]):
            with self.subTest(count=len(apps)), \
                 patch.dict(os.environ, {"TF_VAR_deployment_client_id": CLIENT,
                                         "TF_VAR_resource_group_name": "test"}), \
                 patch.object(review, "run_json", return_value=apps) as reads:
                with self.assertRaisesRegex(ValueError, "exactly one existing app"):
                    review.baseline_evidence()
                self.assertEqual(1, reads.call_count)

    def test_live_evidence_retains_the_exact_existing_image_without_requiring_a_job(self):
        app = {"id": APP, "properties": {"template": {"containers": [{"image": IMAGE}]}}}
        with patch.dict(os.environ, {"TF_VAR_deployment_client_id": CLIENT, "ARM_SUBSCRIPTION_ID": SUB,
                                     "TF_VAR_resource_group_name": "test"}), \
             patch.object(review, "run_json", side_effect=[[app], OWNER, []]) as reads:
            self.assertEqual(EVIDENCE, review.baseline_evidence())
            self.assertEqual(3, reads.call_count)
            self.assertFalse(any("start" in call.args[0] for call in reads.call_args_list))

    def exercise(self, operation, *, fail_apply=False, corrupt=False):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "plan.tfplan"
            if operation != "plan":
                path.write_bytes(b"owner-reviewed-plan")
            digest = review.sha256(path) if path.exists() else None
            commands = []

            def execute(args, **kwargs):
                commands.append((args, kwargs))
                if "plan" in args:
                    path.write_bytes(b"owner-reviewed-plan")
                if "apply" in args and fail_apply:
                    raise ValueError("terraform failed")
                return ""

            def evidence():
                if corrupt and evidence.calls:
                    path.write_bytes(b"changed")
                evidence.calls += 1
                return dict(EVIDENCE)
            evidence.calls = 0
            output = io.StringIO()
            with patch.dict(os.environ, {"TF_VAR_deployment_client_id": CLIENT}, clear=True), \
                 patch.object(review, "check_infrastructure_approval"), \
                 patch.object(review, "check_current_main"), \
                 patch.object(review, "check_release_quiescent"), \
                 patch.object(review, "check_private_backend"), \
                 patch.object(review, "check_private_storage"), \
                 patch.object(review, "check_checkout", return_value=COMMIT), \
                 patch.object(review, "baseline_evidence", side_effect=evidence), \
                 patch.object(review, "check_plan_subscription"), \
                 patch.object(review, "review_package", return_value={}), \
                 patch.object(review, "run_text", side_effect=execute), \
                 patch.object(review, "run_json", side_effect=[
                     {"terraform_version": "1.17.0", "provider_selections": {}},
                     {"variables": {"deployment_client_id": {"value": CLIENT}}},
                 ]), contextlib.redirect_stdout(output):
                if fail_apply or corrupt:
                    with self.assertRaisesRegex(ValueError, "apply failed|changed immediately"):
                        review.infrastructure_main(self.args(path, operation, digest=digest, commit=COMMIT), {"name": "private"})
                    self.assertNotIn("apply succeeded", output.getvalue())
                else:
                    review.infrastructure_main(self.args(path, operation, digest=digest, commit=COMMIT), {"name": "private"})
            return commands

    def test_planning_uses_observed_image_and_never_starts_or_applies(self):
        commands = self.exercise("plan")
        plan = next(args for args, _ in commands if "plan" in args)
        self.assertIn("-var=bootstrap_only=true", plan)
        self.assertIn(f"-var=container_image={IMAGE}", plan)
        self.assertFalse(any("apply" in args or "start" in args for args, _ in commands))

    def test_failed_apply_is_not_reported_as_success_and_has_private_diagnostics(self):
        commands = self.exercise("apply", fail_apply=True)
        apply = next((args, kwargs) for args, kwargs in commands if "apply" in args)
        self.assertTrue(str(apply[1]["private_log"]).endswith(".apply.log"))
        self.assertNotIn("-auto-approve", apply[0])

    def test_approved_apply_uses_the_exact_saved_file_without_regenerating_it(self):
        commands = self.exercise("apply")
        applies = [args for args, _ in commands if "apply" in args]
        self.assertEqual(1, len(applies))
        self.assertTrue(applies[0][-1].endswith("plan.tfplan"))
        self.assertFalse(any("plan" in args or "start" in args for args, _ in commands))

    def test_saved_plan_change_immediately_before_apply_blocks_all_mutation(self):
        commands = self.exercise("apply", corrupt=True)
        self.assertFalse(any("apply" in args for args, _ in commands))

    def test_ambiguous_release_journal_blocks_baseline_changes(self):
        sys.path.insert(0, str(ROOT / "infra"))
        import deploy_gate
        with patch.dict(os.environ, {"TFSTATE_STORAGE_ACCOUNT": "private", "TFSTATE_CONTAINER": "private"}), \
             patch.object(deploy_gate.BlobJournal, "load", return_value=({"phase": "start-requested"}, "etag")):
            with self.assertRaisesRegex(ValueError, "ambiguous application release"):
                review.check_release_quiescent(ROOT)

    def test_timeouts_do_not_leak_private_command_arguments(self):
        with patch.object(review.shutil, "which", return_value="az"), \
             patch.object(review.subprocess, "run", side_effect=subprocess.TimeoutExpired(["az", "private-id"], 1)):
            with self.assertRaisesRegex(ValueError, "az timed out") as caught:
                review.run_text(["az", "private-id"])
            self.assertNotIn("private-id", str(caught.exception))
        with tempfile.TemporaryDirectory() as folder, \
             patch.object(review.shutil, "which", return_value="terraform"), \
             patch.object(review.subprocess, "run", side_effect=subprocess.TimeoutExpired(
                 ["terraform", "apply"], 1, output=b"private partial diagnostic",
             )):
            path = Path(folder) / "apply.log"
            with self.assertRaises(ValueError) as caught:
                review.run_text(["terraform", "apply"], private_log=path)
            self.assertIn("private partial diagnostic", path.read_text(encoding="utf-8"))
            self.assertNotIn("private partial diagnostic", str(caught.exception))

    def test_workflow_never_uploads_saved_plans_to_public_actions_artifacts(self):
        workflow = (ROOT / ".github" / "workflows" / "provision.yml").read_text(encoding="utf-8")
        self.assertNotIn("upload-artifact", workflow)
        self.assertIn("group: terraform-prod", workflow)
        self.assertIn("cancel-in-progress: false", workflow)
        self.assertIn("--mode infrastructure", workflow)
        self.assertIn('if [ "$OPERATION" = plan ]', workflow)
        self.assertNotIn("BUJO_DEPLOYMENT_ENABLED", workflow)
        self.assertNotIn("BUJO_OWNER_SETUP_CONFIRMED", workflow)
        self.assertNotIn("runner.temp", workflow.split("    steps:", 1)[0])

    def test_pipeline_initialization_cannot_rewrite_the_reviewed_provider_lockfile(self):
        for name in ("provision.yml", "deploy.yml", "ci.yml"):
            workflow = (ROOT / ".github" / "workflows" / name).read_text(encoding="utf-8")
            commands = re.findall(r"^\s*(?:if ! )?terraform(?: -chdir=infra)? init ([^\n]*)",
                                  workflow, re.MULTILINE)
            with self.subTest(workflow=name):
                self.assertTrue(commands, "Expected Terraform initialization to be exercised")
                for command in commands:
                    self.assertIn("-lockfile=readonly", command)


if __name__ == "__main__":
    unittest.main()
