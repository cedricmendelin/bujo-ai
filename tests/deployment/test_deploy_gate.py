import sys
import unittest
from pathlib import Path
from typing import Callable
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from infra.deploy_gate import deploy_after_migration, redacted_log


JOB_ID = "/subscriptions/test/resourceGroups/test/providers/Microsoft.App/jobs/migrate"


def fake_rest(status: str) -> tuple[Callable[[str, str, dict | None], dict], list[str]]:
    calls = []

    def rest(method: str, uri: str, body: dict | None) -> dict:
        calls.append(f"{method} {uri}")
        if method == "post":
            return {"name": "new-execution", "id": f"{JOB_ID}/executions/new-execution"}
        if uri.endswith("/executions?api-version=2024-03-01"):
            return {"value": []}
        if uri.endswith("/executions/new-execution?api-version=2024-03-01"):
            return {"properties": {"status": status}}
        raise AssertionError(f"Unexpected REST call: {method} {uri}")

    return rest, calls


class DeployGateTests(unittest.TestCase):
    def test_promotes_only_after_the_new_exact_execution_succeeds(self) -> None:
        rest, calls = fake_rest("Succeeded")
        promoted = []

        execution_name = deploy_after_migration(
            JOB_ID,
            rest=rest,
            promote=lambda: promoted.append(True),
            sleep=lambda _: None,
        )

        self.assertEqual("new-execution", execution_name)
        self.assertEqual([True], promoted)
        self.assertTrue(any("executions/new-execution?" in call for call in calls))
        self.assertFalse(any("executions/previous-execution?" in call for call in calls))

    def test_failed_migration_does_not_promote(self) -> None:
        rest, _ = fake_rest("Failed")
        promoted = []

        with self.assertRaisesRegex(RuntimeError, "status Failed"):
            deploy_after_migration(
                JOB_ID,
                rest=rest,
                promote=lambda: promoted.append(True),
                sleep=lambda _: None,
            )

        self.assertEqual([], promoted)

    def test_timeout_does_not_promote(self) -> None:
        rest, calls = fake_rest("Running")
        promoted = []

        with self.assertRaisesRegex(TimeoutError, "did not finish"):
            deploy_after_migration(
                JOB_ID,
                rest=rest,
                promote=lambda: promoted.append(True),
                sleep=lambda _: None,
                status_attempts=2,
            )

        self.assertEqual([], promoted)
        self.assertEqual(
            2,
            sum("executions/new-execution?" in call for call in calls),
        )

    def test_missing_database_grant_failure_does_not_promote_first_deployment(self) -> None:
        rest, _ = fake_rest("Failed")
        promoted = []

        with self.assertRaisesRegex(RuntimeError, "check the Container Apps execution logs"):
            deploy_after_migration(
                JOB_ID,
                rest=rest,
                promote=lambda: promoted.append(True),
                sleep=lambda _: None,
            )

        self.assertEqual([], promoted)

    def test_indeterminate_status_does_not_promote(self) -> None:
        rest, _ = fake_rest("Unknown")
        promoted = []

        with self.assertRaisesRegex(RuntimeError, "indeterminate status"):
            deploy_after_migration(
                JOB_ID,
                rest=rest,
                promote=lambda: promoted.append(True),
                sleep=lambda _: None,
            )

        self.assertEqual([], promoted)

    def test_unverifiable_start_response_never_promotes_or_polls_another_execution(self) -> None:
        for response in (
            {},
            {"name": "other", "id": f"{JOB_ID}/executions/new-execution"},
            {"name": "../other", "id": f"{JOB_ID}/executions/../other"},
            {"name": "other", "id": "/subscriptions/other/jobs/migrate/executions/other"},
        ):
            with self.subTest(response=response):
                calls = []
                promoted = []

                def rest(method: str, uri: str, body: dict | None) -> dict:
                    calls.append(method)
                    if method == "post":
                        return response
                    if uri.endswith("/executions?api-version=2024-03-01"):
                        return {"value": []}
                    raise AssertionError("An unverifiable execution must not be polled.")

                with self.assertRaisesRegex(RuntimeError, "verifiable execution ID"):
                    deploy_after_migration(
                        JOB_ID,
                        rest=rest,
                        promote=lambda: promoted.append(True),
                        sleep=lambda _: None,
                    )

                self.assertEqual(["get", "post"], calls)
                self.assertEqual([], promoted)

    def test_existing_or_indeterminate_executions_block_start_and_promotion(self) -> None:
        for status in ("Running", "Processing", "Unknown", "Degraded", None):
            with self.subTest(status=status):
                calls = []
                promoted = []

                def rest(method: str, uri: str, body: dict | None) -> dict:
                    calls.append(method)
                    return {"value": [{"properties": {"status": status}}]}

                with self.assertRaisesRegex(RuntimeError, "active or has an indeterminate"):
                    deploy_after_migration(JOB_ID, rest=rest, promote=lambda: promoted.append(True))
                self.assertEqual(["get"], calls)
                self.assertEqual([], promoted)

    def test_checks_every_page_and_rejects_untrusted_pagination(self) -> None:
        next_page = f"https://management.azure.com{JOB_ID}/executions?api-version=2024-03-01&skip=1"
        for link in (next_page, "https://example.invalid/executions"):
            with self.subTest(link=link):
                calls = []
                promoted = []

                def rest(method: str, uri: str, body: dict | None) -> dict:
                    calls.append(uri)
                    if len(calls) == 1:
                        return {"value": [{"properties": {"status": "Succeeded"}}], "nextLink": link}
                    return {"value": [{"properties": {"status": "Running"}}]}

                with self.assertRaises(RuntimeError):
                    deploy_after_migration(JOB_ID, rest=rest, promote=lambda: promoted.append(True))
                self.assertEqual(2 if link == next_page else 1, len(calls))
                self.assertEqual([], promoted)

    def test_completed_executions_on_multiple_pages_allow_start(self) -> None:
        next_page = f"https://management.azure.com{JOB_ID}/executions?api-version=2024-03-01&skip=1"
        calls = []
        promoted = []

        def rest(method: str, uri: str, body: dict | None) -> dict:
            calls.append((method, uri))
            if method == "post":
                return {"name": "new", "id": f"{JOB_ID}/executions/new"}
            if uri == next_page:
                return {"value": [{"properties": {"status": "Failed"}}]}
            if uri.endswith("/executions?api-version=2024-03-01"):
                return {"value": [{"properties": {"status": "Succeeded"}}], "nextLink": next_page}
            return {"properties": {"status": "Succeeded"}}

        self.assertEqual("new", deploy_after_migration(
            JOB_ID, rest=rest, promote=lambda: promoted.append(True), sleep=lambda _: None
        ))
        self.assertEqual(["get", "get", "post", "get"], [method for method, _ in calls])
        self.assertEqual([True], promoted)

    def test_execution_list_failure_blocks_start_and_promotion(self) -> None:
        for response in (None, {}, {"value": None}, {"value": [], "nextLink": ""}):
            with self.subTest(response=response):
                calls = []
                promoted = []

                def rest(method: str, uri: str, body: dict | None) -> dict:
                    calls.append(method)
                    return response

                with self.assertRaises(RuntimeError):
                    deploy_after_migration(JOB_ID, rest=rest, promote=lambda: promoted.append(True))
                self.assertEqual(["get"], calls)
                self.assertEqual([], promoted)

    def test_execution_list_request_error_blocks_start_and_promotion(self) -> None:
        calls = []
        promoted = []

        def rest(method: str, uri: str, body: dict | None) -> dict:
            calls.append(method)
            raise RuntimeError("execution list unavailable")

        with self.assertRaisesRegex(RuntimeError, "execution list unavailable"):
            deploy_after_migration(JOB_ID, rest=rest, promote=lambda: promoted.append(True))
        self.assertEqual(["get"], calls)
        self.assertEqual([], promoted)

    def test_deployment_workflow_requires_owner_dispatch_from_main(self) -> None:
        workflow = (Path(__file__).resolve().parents[2] / ".github/workflows/deploy.yml").read_text()
        self.assertIn("  workflow_dispatch:", workflow)
        self.assertNotRegex(workflow, r"(?m)^  push:")
        self.assertIn('refs/heads/main', workflow)
        self.assertIn('${GITHUB_REPOSITORY_OWNER,,}', workflow)
        self.assertIn('${TRIGGERING_ACTOR,,}', workflow)
        self.assertIn('${{ github.triggering_actor }}', workflow)
        self.assertIn("    needs: authorize", workflow)

    def test_workflow_calls_provisioning_before_the_migration_gate(self) -> None:
        workflow = (Path(__file__).resolve().parents[2] / ".github/workflows/deploy.yml").read_text()
        commands = [
            line.strip()
            for line in workflow.splitlines()
            if line.strip().startswith("run: python3 infra/deploy_gate.py")
        ]

        self.assertEqual(
            [
                "run: python3 infra/deploy_gate.py provision-migration-job",
                "run: python3 infra/deploy_gate.py",
            ],
            commands,
        )

    def test_sensitive_deployment_values_are_redacted_from_terraform_logs(self) -> None:
        with patch.dict(
            "os.environ",
            {
                "ARM_SUBSCRIPTION_ID": "subscription-placeholder",
                "TF_VAR_sql_entra_admin_login": "group-placeholder",
            },
        ):
            result = redacted_log(
                "subscription-placeholder group-placeholder keep-this"
            )

        self.assertEqual("[REDACTED] [REDACTED] keep-this", result)


if __name__ == "__main__":
    unittest.main()
