import importlib.util
import os
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest
from unittest.mock import patch


spec = importlib.util.spec_from_file_location(
    "apply_infrastructure", Path(__file__).resolve().parents[2] / ".github/scripts/apply_infrastructure.py"
)
infra = importlib.util.module_from_spec(spec)
spec.loader.exec_module(infra)


class ApplyInfrastructureTests(unittest.TestCase):
    def setUp(self):
        temp = self.enterContext(TemporaryDirectory())
        self.enterContext(patch.dict(os.environ, {
            "RUNNER_TEMP": temp, "TFSTATE_KEY": "test.tfstate", "TFSTATE_STORAGE_ACCOUNT": "teststate",
            "TFSTATE_CONTAINER": "tfstate", "TF_VAR_container_image": "new-release",
            "TF_VAR_auth_allowed_principal_object_ids": "existing-allowlist",
        }))
        self.revision = "a" * 40
        self.saved_revision = self.revision
        self.images = self.enterContext(patch.object(infra, "retained_images", return_value=("live-app", "live-job")))
        self.command = self.enterContext(patch.object(infra, "command", side_effect=self.run_command))
        self.storage = self.enterContext(patch.object(infra, "az", side_effect=self.run_storage))
        self.fail_phase = None
        self.calls = []

    def run_command(self, label, args, *, env=None):
        if args[0] == "git":
            return self.revision + "\n"
        phase = args[2]
        self.calls.append(phase)
        if phase == self.fail_phase:
            raise infra.InfrastructureError(f"{phase} failed")
        return ""

    def run_storage(self, *args):
        phase = args[2]
        self.calls.append(phase)
        self.assertIn("login", args)
        if phase == "exists":
            return {"exists": self.saved_revision is not None}
        path = Path(args[args.index("--file") + 1])
        if phase == "download":
            path.write_text(self.saved_revision + "\n", encoding="ascii")
        else:
            self.assertEqual(phase, "upload")
            self.saved_revision = path.read_text(encoding="ascii").strip()

    def test_automatic_run_skips_already_applied_tree(self):
        infra.apply_infrastructure(always=False)
        self.assertEqual(self.calls, ["exists", "download"])
        self.images.assert_not_called()

    def test_automatic_run_applies_pending_tree_with_live_images_and_no_approval(self):
        for marker in ("b" * 40, None):
            with self.subTest(marker=marker):
                self.saved_revision = marker
                self.calls.clear()
                infra.apply_infrastructure(always=False)
                self.assertEqual(self.calls[-3:], ["plan", "apply", "upload"])
                self.assertEqual(self.saved_revision, self.revision)
                plan, apply = self.command.call_args_list[-2:]
                plan_path = next(arg.removeprefix("-out=") for arg in plan.args[1] if arg.startswith("-out="))
                self.assertEqual(apply.args[1][-1], plan_path)
                self.assertIn("-input=false", apply.args[1])
                self.assertEqual(plan.kwargs["env"]["TF_VAR_retained_app_image"], "live-app")
                self.assertEqual(plan.kwargs["env"]["TF_VAR_container_image"], "live-job")
                env = {key.upper(): value for key, value in plan.kwargs["env"].items()}
                self.assertEqual(env["TF_VAR_AUTH_ALLOWED_PRINCIPAL_OBJECT_IDS"], "existing-allowlist")

    def test_manual_run_reconciles_same_tree_without_arming_placeholder_job(self):
        self.images.return_value = ("live-app", None)
        infra.apply_infrastructure(always=True)
        self.assertEqual(self.calls, ["plan", "apply", "upload"])
        self.assertIn("-var=bootstrap_only=true", self.command.call_args_list[-2].args[1])

    def test_failed_plan_or_apply_never_marks_tree_applied(self):
        for phase in ("plan", "apply"):
            with self.subTest(phase=phase):
                self.saved_revision = "b" * 40
                self.fail_phase = phase
                self.calls.clear()
                with self.assertRaisesRegex(infra.InfrastructureError, f"{phase} failed"):
                    infra.apply_infrastructure(always=False)
                self.assertEqual(self.calls[-1], phase)
                self.assertNotIn("upload", self.calls)
                self.assertEqual(self.saved_revision, "b" * 40)
