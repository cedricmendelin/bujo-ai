"""Real Terraform mock-provider release plans for the deployment gate validators.

`infra/tests/release.tftest.hcl` applies a mocked previous release (A), applies
the targeted migration job move to release B, then plans the full promotion,
using the repository's actual AVM modules. A second, separately stated sequence
starts from only the stage-one bootstrap. No Azure access: every provider is a
Terraform mock provider. Plans are read from `terraform test -json -verbose`.
"""
import copy
import json
import os
import subprocess
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "infra"))
import deploy_gate as gate  # noqa: E402

RELEASE_A = "ghcr.io/cedricmendelin/bujo-ai@sha256:" + "a" * 64
RELEASE_B = "ghcr.io/cedricmendelin/bujo-ai@sha256:" + "b" * 64
RUNS = (
    "release_baseline_apply", "release_migration_plan", "release_migration_apply",
    "release_migration_rerun_plan", "release_promotion_plan",
    "first_release_bootstrap_apply", "first_release_migration_plan",
    "first_release_migration_apply", "first_release_promotion_plan",
)
APP_RESOURCE = "module.container_app.azapi_resource.container_app"
# Attributes azapi computes when post_creation_update is replaced; observed in
# the real plan, not hand-written.
ACTION_COMPUTED_UNKNOWN = {
    "exist", "id", "output", "retry.max_interval_seconds", "retry.multiplier",
    "retry.randomization_factor", "sensitive_output", "when",
}


def unknown_paths(value, path=""):
    if value is True:
        yield path
    elif isinstance(value, dict):
        for key, item in value.items():
            yield from unknown_paths(item, f"{path}.{key}" if path else key)
    elif isinstance(value, list):
        for index, item in enumerate(value):
            yield from unknown_paths(item, f"{path}[{index}]")


def differences(before, after, path=""):
    if isinstance(before, dict) and isinstance(after, dict):
        for key in sorted(set(before) | set(after)):
            yield from differences(before.get(key), after.get(key), f"{path}.{key}")
    elif isinstance(before, list) and isinstance(after, list) and len(before) == len(after):
        for index, (old, new) in enumerate(zip(before, after)):
            yield from differences(old, new, f"{path}[{index}]")
    elif before != after:
        yield path


class ReleasePlanTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        result = subprocess.run(
            ["terraform", "-chdir=infra", "test", "-json", "-verbose",
             "-filter=" + os.path.join("tests", "release.tftest.hcl")],
            cwd=ROOT, capture_output=True, text=True,
        )
        events = [json.loads(line) for line in result.stdout.splitlines() if line.strip()]
        statuses = {
            event["@testrun"]: event["test_run"]["status"] for event in events
            if event.get("type") == "test_run" and event["test_run"].get("progress") == "complete"
        }
        if result.returncode != 0 or statuses != {run: "pass" for run in RUNS}:
            raise AssertionError(f"Mocked release sequence failed: {statuses}")
        cls.plans = {
            event["@testrun"]: event["test_plan"] for event in events if event.get("type") == "test_plan"
        }

    def plan(self, run):
        return copy.deepcopy(self.plans[run])

    @staticmethod
    def changes(plan):
        return {
            item["address"]: item["change"]["actions"] for item in plan["resource_changes"]
            if item["change"]["actions"] != ["no-op"]
        }

    @staticmethod
    def resource(plan, address):
        return next(item for item in plan["resource_changes"] if item["address"] == address)

    @staticmethod
    def trusted(plan, name):
        # Stands for `terraform output -raw <name>` read before planning.
        return plan["output_changes"][name]["before"]

    def assert_outputs_unchanged(self, plan):
        changed = {name for name, item in plan["output_changes"].items() if item["actions"] != ["no-op"]}
        self.assertEqual(changed, set())

    # Exposed plan shapes -------------------------------------------------

    def test_migration_plan_moves_only_the_job_image(self):
        plan = self.plan("release_migration_plan")
        self.assertEqual(self.changes(plan), {gate.JOB_ADDRESS: ["update"]})
        change = self.resource(plan, gate.JOB_ADDRESS)["change"]
        self.assertEqual(list(differences(change["before"], change["after"])), [".template[0].container[0].image"])
        self.assertEqual(list(unknown_paths(change["after_unknown"])), [])
        self.assert_outputs_unchanged(plan)

    def test_migration_rerun_on_the_release_image_is_a_no_op(self):
        plan = self.plan("release_migration_rerun_plan")
        self.assertEqual(self.changes(plan), {})
        self.assertIn(gate.JOB_ADDRESS, {item["address"] for item in plan["resource_changes"]})

    def test_promotion_plan_replaces_the_avm_patch_action_with_computed_unknowns(self):
        plan = self.plan("release_promotion_plan")
        self.assertEqual(self.changes(plan), {
            gate.APP_KEEPER_ADDRESS: ["update"],
            gate.APP_UPDATE_ADDRESS: ["delete", "create"],
        })
        self.assertEqual(self.resource(plan, gate.JOB_ADDRESS)["change"]["actions"], ["no-op"])
        self.assertEqual(self.resource(plan, APP_RESOURCE)["change"]["actions"], ["no-op"])
        self.assert_outputs_unchanged(plan)

        keeper = self.resource(plan, gate.APP_KEEPER_ADDRESS)["change"]
        self.assertEqual(list(differences(keeper["before"]["input"], keeper["after"]["input"])),
                         [".containers[0].image"])
        self.assertEqual(list(unknown_paths(keeper["after_unknown"])), ["output"])

        action = self.resource(plan, gate.APP_UPDATE_ADDRESS)
        self.assertEqual(action["action_reason"], "replace_by_triggers")
        change = action["change"]
        self.assertEqual(set(unknown_paths(change["after_unknown"])), ACTION_COMPUTED_UNKNOWN)
        self.assertEqual(change["after"]["method"], "PATCH")
        self.assertEqual(change["after"]["resource_id"], change["before"]["resource_id"])
        self.assertEqual(list(differences(change["before"]["body"], change["after"]["body"])),
                         [".properties.template.containers[0].image"])
        self.assertTrue(change["after_sensitive"]["body"]["properties"]["template"]["containers"][0])

    def test_first_release_migration_moves_the_placeholder_job_to_the_release(self):
        change = self.resource(self.plan("first_release_migration_plan"), gate.JOB_ADDRESS)["change"]
        before = change["before"]["template"][0]["container"][0]
        after = change["after"]["template"][0]["container"][0]
        self.assertEqual(before["command"], ["/bootstrap-disabled"])
        self.assertEqual(before.get("env") or [], [])
        self.assertEqual(after["image"], RELEASE_A)
        self.assertEqual(after["command"], gate.MIGRATION_COMMAND)
        self.assertEqual(after["args"], gate.MIGRATION_ARGS)

    def test_first_release_after_stage_one_still_creates_the_app_and_api(self):
        changes = self.changes(self.plan("first_release_promotion_plan"))
        self.assertTrue({
            APP_RESOURCE, gate.APP_UPDATE_ADDRESS, gate.APP_KEEPER_ADDRESS,
            "azuread_application.api", "module.key_vault.azapi_resource.this",
        } <= set(changes))
        self.assertTrue(all(actions == ["create"] for actions in changes.values()))

    # Gate validators on the real plans -----------------------------------

    def test_gate_accepts_real_migration_plans(self):
        for run, image, expected in (
            ("release_migration_plan", RELEASE_B, True),
            ("release_migration_rerun_plan", RELEASE_B, False),
            ("first_release_migration_plan", RELEASE_A, True),
        ):
            with self.subTest(run=run):
                plan = self.plan(run)
                trusted = self.trusted(plan, "migration_sql_connection_string")
                self.assertIs(gate.validate_migration_plan(plan, image, trusted), expected)

    def test_gate_rejects_a_migration_plan_for_another_image(self):
        plan = self.plan("release_migration_plan")
        with self.assertRaises(gate.DeploymentBlocked):
            gate.validate_migration_plan(plan, RELEASE_A, self.trusted(plan, "migration_sql_connection_string"))

    def test_gate_accepts_the_real_promotion_plan(self):
        plan = self.plan("release_promotion_plan")
        trusted = self.trusted(plan, "runtime_sql_connection_string")
        app_id = self.resource(plan, gate.APP_UPDATE_ADDRESS)["change"]["before"]["resource_id"]
        self.assertIs(gate.validate_promotion_plan(plan, RELEASE_B, trusted, app_id), True)

    def test_real_plans_preserve_the_dependency_proof_between_migration_and_promotion(self):
        migration = self.plan("release_migration_plan")
        promotion = self.plan("release_promotion_plan")
        self.assertEqual(gate.job_dependencies(migration), gate.job_dependencies(promotion))
        for name in ("migration_sql_connection_string", "runtime_sql_connection_string"):
            self.assertEqual(self.trusted(migration, name), self.trusted(promotion, name))

    def test_gate_rejects_the_real_promotion_plan_for_another_image(self):
        plan = self.plan("release_promotion_plan")
        with self.assertRaises(gate.DeploymentBlocked):
            gate.validate_promotion_plan(plan, RELEASE_A, self.trusted(plan, "runtime_sql_connection_string"))

    def test_gate_rejects_first_release_promotion_without_a_complete_baseline(self):
        plan = self.plan("first_release_promotion_plan")
        with self.assertRaises(gate.DeploymentBlocked):
            gate.validate_promotion_plan(plan, RELEASE_A, self.trusted(plan, "runtime_sql_connection_string"))


if __name__ == "__main__":
    unittest.main()
