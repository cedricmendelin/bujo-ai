import copy
import io
import json
import sys
import tempfile
import unittest
import urllib.error
from contextlib import redirect_stderr
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from infra import deploy_gate as gate  # noqa: E402
from infra.deploy_gate import (  # noqa: E402
    AmbiguousStart, ArmClient, ArmUnavailable, BlobJournal, DeploymentBlocked, ExecutionFailed, Release,
    ReleaseController, Settings, StartRejected, ensure_no_active_executions, load_settings, main, next_step,
    probe_anonymous_rejected, probe_liveness, recover_start, release_recheck, run_terraform, start_execution,
    validate_migration_plan, validate_promotion_plan, verify_deployed_revision,
)

SUB = "00000000-0000-0000-0000-000000000000"
SCOPE = f"/subscriptions/{SUB}/resourceGroups/rg-test"
JOB_ID = SCOPE + "/providers/Microsoft.App/jobs/migrate"
APP_ID = SCOPE + "/providers/Microsoft.App/containerApps/app"
IMAGE = "ghcr.io/cedricmendelin/bujo-ai@sha256:" + "a" * 64
OLD_IMAGE = "ghcr.io/cedricmendelin/bujo-ai@sha256:" + "b" * 64
SHA = "c" * 40
MIGRATION_SQL = ("Server=tcp:sql.example,1433;Initial Catalog=bujo;Encrypt=True;"
                 "Authentication=Active Directory Managed Identity;User Id=migration-client")
RUNTIME_SQL = MIGRATION_SQL.replace("migration-client", "runtime-client")
OPERATION = (f"https://management.azure.com/subscriptions/{SUB}/providers/Microsoft.App/locations/"
             "switzerlandnorth/containerappOperationStatuses/op-1?api-version=2024-03-01")
EXEC_LIST = f"{JOB_ID}/executions"
START = f"{JOB_ID}/start"
FQDN = "app.example.switzerlandnorth.azurecontainerapps.io"


def body(value) -> bytes:
    return json.dumps(value).encode()


def execution(name: str, status: str = "Succeeded", image: str = IMAGE) -> dict:
    return {"id": f"{JOB_ID}/executions/{name}", "name": name,
            "properties": {"status": status, "template": {"containers": [{
                "image": image, "command": gate.MIGRATION_COMMAND, "args": gate.MIGRATION_ARGS,
                "env": [{"name": "DOTNET_ENVIRONMENT", "value": "Production"},
                        {"name": "ConnectionStrings__journal", "value": MIGRATION_SQL}],
            }]}}}


class FakeArm:
    """Scripted ARM transport keyed by (method, path); the last response repeats."""

    def __init__(self, routes: dict, events: list | None = None):
        self.routes = {key: list(value) for key, value in routes.items()}
        self.calls: list[tuple[str, str]] = []
        self.events = events if events is not None else []

    def __call__(self, method, url, headers, payload):
        path = url.split("?", 1)[0].replace("https://management.azure.com", "")
        self.calls.append((method, path))
        self.events.append(f"{method}:{path}")
        assert headers["Authorization"] == "Bearer test-token"
        queue = self.routes.get((method, path))
        if not queue:
            return 404, {}, b"{}"
        response = queue.pop(0) if len(queue) > 1 else queue[0]
        if isinstance(response, Exception):
            raise response
        status, headers, data = response
        return status, headers, data if isinstance(data, bytes) else body(data)

    def count(self, method, path):
        return self.calls.count((method, path))


def arm_for(fake: FakeArm, sleeps: list | None = None) -> ArmClient:
    return ArmClient(JOB_ID, transport=fake, token=lambda: "test-token",
                     sleep=(sleeps.append if sleeps is not None else (lambda _: None)))


def idle_list(*statuses: str):
    return (200, {}, {"value": [execution(f"old-{i}", s) for i, s in enumerate(statuses)]})


def app_document(image: str = IMAGE, revision: str = "app--r2", flag: str = "true") -> dict:
    return {"properties": {
        "provisioningState": "Succeeded", "latestRevisionName": revision, "latestReadyRevisionName": revision,
        "configuration": {
            "activeRevisionsMode": "Single",
            "ingress": {"fqdn": FQDN, "traffic": [{"weight": 100, "latestRevision": True}]},
        },
        "template": {"containers": [{"image": image, "env": [
            {"name": gate.STARTUP_FLAG, "value": flag},
            {"name": "ConnectionStrings__journal", "value": RUNTIME_SQL},
        ]}]},
    }}


def revision_document(image: str = IMAGE) -> dict:
    return {"properties": {"provisioningState": "Provisioned", "healthState": "Healthy", "active": True,
                           "template": {"containers": [{"image": image, "env": [
                               {"name": gate.STARTUP_FLAG, "value": "true"},
                               {"name": "ConnectionStrings__journal", "value": RUNTIME_SQL},
                           ]}]}}}


class Response:
    def __init__(self, status):
        self.status = status

    def __enter__(self):
        return self

    def __exit__(self, *args):
        return False


def fake_open(responses: dict, calls: list | None = None):
    """responses maps URL suffix to a list of status codes or exceptions."""
    def open_url(url, timeout=None):
        if calls is not None:
            calls.append(url)
        for suffix, queue in responses.items():
            if url.endswith(suffix):
                item = queue.pop(0) if len(queue) > 1 else queue[0]
                if isinstance(item, Exception):
                    raise item
                if item >= 400:
                    raise urllib.error.HTTPError(url, item, "status", {}, None)
                return Response(item)
        raise AssertionError(url)
    return open_url


def http_error(code):
    return urllib.error.HTTPError("https://example.invalid", code, "status", {}, None)


def migration_plan(before_container: dict | None = None) -> dict:
    container = before_container or {"name": "bujo-migrate", "cpu": 0.25, "memory": "0.5Gi",
                                      "image": "inert", "command": ["/bootstrap-disabled"], "args": [], "env": []}
    before = {"identity": [{"type": "UserAssigned", "identity_ids": ["identity"]}],
              "container_app_environment_id": "environment", "replica_timeout_in_seconds": 1800,
              "workload_profile_name": "Consumption",
              "template": [{"container": [container], "init_container": []}]}
    after = copy.deepcopy(before)
    after["template"][0]["container"][0].update(
        image=IMAGE, command=["dotnet", "Bujo.Host.dll"], args=["--migrate-only"],
        env=[{"name": "DOTNET_ENVIRONMENT", "value": "Production"},
             {"name": "ConnectionStrings__journal", "value": MIGRATION_SQL}],
    )
    return {"resource_changes": [
        {"mode": "managed", "address": gate.JOB_ADDRESS,
         "change": {"actions": ["update"], "before": before, "after": after, "after_unknown": {"id": False}}},
        {"mode": "managed", "address": "module.sql_server.azapi_resource.this", "change": {"actions": ["no-op"]}},
        {"mode": "data", "address": "data.azurerm_client_config.current", "change": {"actions": ["read"]}},
    ], "output_changes": {"migration_sql_connection_string": {"actions": ["no-op"]}}}


def noop_migration_plan() -> dict:
    plan = migration_plan()
    job = plan["resource_changes"][0]["change"]
    job["before"] = copy.deepcopy(job["after"])
    job["actions"] = ["no-op"]
    return plan


def app_input(image: str, flag: bool, allowed: str = "principal-a") -> dict:
    env = [{"name": "ConnectionStrings__journal", "value": RUNTIME_SQL}]
    if flag:
        env.append({"name": gate.STARTUP_FLAG, "value": "true"})
    env += [{"name": "Auth__TenantId", "value": "tenant"}, {"name": "Auth__AllowedPrincipals", "value": allowed}]
    return {"min_replicas": 0, "max_replicas": 1,
            "containers": [{"name": "bujo-ai", "image": image, "cpu": 0.25, "memory": "0.5Gi", "env": env}]}


def promotion_plan(old_image: str = OLD_IMAGE, old_flag: bool = False) -> dict:
    before, after = app_input(old_image, old_flag), app_input(IMAGE, True)
    return {"resource_changes": [
        {"mode": "managed", "address": gate.APP_KEEPER_ADDRESS,
         "change": {"actions": ["update"], "before": {"input": before}, "after": {"input": after},
                    "after_unknown": {"input": False}}},
        {"mode": "managed", "address": gate.APP_UPDATE_ADDRESS,
         "change": {"actions": ["delete", "create"],
                    "before": {"method": "PATCH", "resource_id": APP_ID,
                               "body": {"properties": {"template": before}}},
                    "after": {"method": "PATCH", "resource_id": APP_ID,
                              "body": {"properties": {"template": after}}},
                    "after_unknown": {}}},
        noop_migration_plan()["resource_changes"][0],
        {"mode": "managed", "address": "module.container_app.azapi_resource.container_app",
         "change": {"actions": ["no-op"]}},
    ], "output_changes": {"runtime_sql_connection_string": {"actions": ["no-op"]},
                          "migration_sql_connection_string": {"actions": ["no-op"]}}}


def noop_promotion_plan() -> dict:
    plan = promotion_plan(IMAGE, True)
    plan["resource_changes"][0]["change"]["actions"] = ["no-op"]
    plan["resource_changes"][1]["change"]["actions"] = ["no-op"]
    return plan


def change(plan: dict, address: str) -> dict:
    return next(r["change"] for r in plan["resource_changes"] if r["address"] == address)


class MigrationPlanTests(unittest.TestCase):
    def test_placeholder_to_release_is_applied(self):
        self.assertTrue(validate_migration_plan(migration_plan(), IMAGE, MIGRATION_SQL))

    def test_rerun_with_release_already_on_job_is_noop(self):
        self.assertFalse(validate_migration_plan(noop_migration_plan(), IMAGE, MIGRATION_SQL))

    def test_previous_release_job_with_same_trusted_target_is_allowed(self):
        previous = copy.deepcopy(migration_plan()["resource_changes"][0]["change"]["after"]["template"][0]
                                 ["container"][0])
        previous["image"] = OLD_IMAGE
        self.assertTrue(validate_migration_plan(migration_plan(previous), IMAGE, MIGRATION_SQL))

    def test_rejects_unsafe_changes(self):
        def mutate(fn):
            plan = migration_plan()
            fn(plan)
            return plan

        job = lambda p: change(p, gate.JOB_ADDRESS)  # noqa: E731
        container = lambda p: job(p)["after"]["template"][0]["container"][0]  # noqa: E731
        cases = {
            "identity": lambda p: job(p)["after"].update(identity=[{"type": "SystemAssigned"}]),
            "environment": lambda p: job(p)["after"].update(container_app_environment_id="other"),
            "workload profile": lambda p: job(p)["after"].update(workload_profile_name=None),
            "timeout": lambda p: job(p)["after"].update(replica_timeout_in_seconds=10),
            "replace": lambda p: job(p).update(actions=["delete", "create"]),
            "drift": lambda p: p["resource_changes"][1]["change"].update(actions=["update"]),
            "image": lambda p: container(p).update(image=OLD_IMAGE),
            "command": lambda p: container(p).update(command=["sh", "-c", "x"]),
            "args": lambda p: container(p).update(args=[]),
            "cpu": lambda p: container(p).update(cpu=2),
            "sql": lambda p: container(p)["env"][1].update(value=RUNTIME_SQL),
            "secret": lambda p: container(p)["env"][1].update(secret_name="x"),
            "extra env": lambda p: container(p)["env"].append({"name": "X", "value": "1"}),
            "two containers": lambda p: job(p)["after"]["template"][0]["container"].append({}),
            "unknown": lambda p: job(p)["after_unknown"].update(template=True),
            "output": lambda p: p["output_changes"]["migration_sql_connection_string"].update(actions=["update"]),
            "errored": lambda p: p.update(errored=True),
            "missing job": lambda p: p["resource_changes"].pop(0),
        }
        for name, fn in cases.items():
            with self.subTest(name), self.assertRaises(DeploymentBlocked):
                validate_migration_plan(mutate(fn), IMAGE, MIGRATION_SQL)

    def test_unexpected_changes_report_only_resource_types_and_actions(self):
        plan = migration_plan()
        plan["resource_changes"].append({
            "mode": "managed",
            "address": "module.private_resource.secret_name",
            "type": "azurerm_mssql_database",
            "change": {
                "actions": ["update"],
                "after": {"administrator_password": "must-not-be-logged"},
            },
        })

        with self.assertRaisesRegex(
            DeploymentBlocked,
            r"azurerm_mssql_database \(update\)",
        ) as raised:
            validate_migration_plan(plan, IMAGE, MIGRATION_SQL)

        self.assertNotIn("secret_name", str(raised.exception))
        self.assertNotIn("must-not-be-logged", str(raised.exception))

    def test_rejects_retarget_from_untrusted_old_connection(self):
        previous = copy.deepcopy(migration_plan()["resource_changes"][0]["change"]["after"]["template"][0]
                                 ["container"][0])
        previous["env"][1]["value"] = RUNTIME_SQL
        with self.assertRaises(DeploymentBlocked):
            validate_migration_plan(migration_plan(previous), IMAGE, MIGRATION_SQL)

    def test_rejects_untrusted_target_or_mutable_image(self):
        for target in ("Server=tcp:x;Password=p;Authentication=Active Directory Managed Identity", "", "x"):
            with self.subTest(target), self.assertRaises(DeploymentBlocked):
                validate_migration_plan(migration_plan(), IMAGE, target)
        with self.assertRaises(DeploymentBlocked):
            validate_migration_plan(migration_plan(), "ghcr.io/cedricmendelin/bujo-ai:latest", MIGRATION_SQL)


class PromotionPlanTests(unittest.TestCase):
    def test_first_promotion_adds_image_and_startup_flag(self):
        self.assertTrue(validate_promotion_plan(promotion_plan(), IMAGE, RUNTIME_SQL))

    def test_later_promotion_changes_image_only(self):
        self.assertTrue(validate_promotion_plan(promotion_plan(old_flag=True), IMAGE, RUNTIME_SQL))

    def test_already_promoted_is_noop(self):
        self.assertFalse(validate_promotion_plan(noop_promotion_plan(), IMAGE, RUNTIME_SQL))

    def test_computed_replacement_outputs_are_allowed_but_not_unknown_patch_inputs(self):
        plan = promotion_plan()
        action = change(plan, gate.APP_UPDATE_ADDRESS)
        action["after_unknown"] = {"id": True, "output": True}
        self.assertTrue(validate_promotion_plan(plan, IMAGE, RUNTIME_SQL, APP_ID))
        action["after_unknown"]["body"] = True
        with self.assertRaises(DeploymentBlocked):
            validate_promotion_plan(plan, IMAGE, RUNTIME_SQL, APP_ID)

    def test_patch_cannot_hide_scale_or_sidecar_changes_outside_keeper(self):
        for field, value in (("scale", {"maxReplicas": 10}), ("initContainers", [{"image": OLD_IMAGE}])):
            plan = promotion_plan()
            change(plan, gate.APP_UPDATE_ADDRESS)["after"]["body"]["properties"]["template"][field] = value
            with self.subTest(field=field), self.assertRaises(DeploymentBlocked):
                validate_promotion_plan(plan, IMAGE, RUNTIME_SQL, APP_ID)

    def test_rejects_unsafe_changes(self):
        def mutate(fn, base=promotion_plan):
            plan = base()
            fn(plan)
            return plan

        keeper = lambda p: change(p, gate.APP_KEEPER_ADDRESS)  # noqa: E731
        update = lambda p: change(p, gate.APP_UPDATE_ADDRESS)  # noqa: E731
        after = lambda p: keeper(p)["after"]["input"]  # noqa: E731
        cases = {
            "job change": lambda p: change(p, gate.JOB_ADDRESS).update(actions=["update"]),
            "infra drift": lambda p: p["resource_changes"][3]["change"].update(actions=["update"]),
            "destroy": lambda p: p["resource_changes"].append(
                {"mode": "managed", "address": "module.sql_server.x", "change": {"actions": ["delete"]}}),
            "allowlist": lambda p: after(p)["containers"][0]["env"][-1].update(value="principal-b"),
            "runtime sql": lambda p: after(p)["containers"][0]["env"][0].update(value=MIGRATION_SQL),
            "scale": lambda p: after(p).update(max_replicas=5),
            "cpu": lambda p: after(p)["containers"][0].update(cpu=2),
            "wrong image": lambda p: after(p)["containers"][0].update(image=OLD_IMAGE),
            "unknown input": lambda p: keeper(p).update(after_unknown={"input": True}),
            "patch target": lambda p: update(p)["after"].update(resource_id=APP_ID + "x"),
            "patch method": lambda p: update(p)["after"].update(method="PUT"),
            "patch body": lambda p: update(p)["after"]["body"]["properties"]["template"]["containers"][0].update(
                image=OLD_IMAGE),
            "update not replace": lambda p: update(p).update(actions=["update"]),
            "sql output": lambda p: p["output_changes"]["runtime_sql_connection_string"].update(actions=["update"]),
            "missing job": lambda p: p["resource_changes"].pop(2),
        }
        for name, fn in cases.items():
            with self.subTest(name), self.assertRaises(DeploymentBlocked):
                validate_promotion_plan(mutate(fn), IMAGE, RUNTIME_SQL)
        with self.assertRaises(DeploymentBlocked):
            validate_promotion_plan(
                mutate(lambda p: update(p).update(actions=["delete", "create"]), noop_promotion_plan),
                IMAGE, RUNTIME_SQL)


class ArmClientTests(unittest.TestCase):
    def test_trusted_urls_are_limited_to_arm_scope(self):
        arm = arm_for(FakeArm({}))
        ok = f"https://management.azure.com{JOB_ID}?api-version=2024-03-01"
        self.assertEqual(arm.trusted(ok), ok)
        self.assertEqual(arm.trusted(OPERATION, allow_operation=True), OPERATION)
        bad = [
            ok.replace("https://", "http://"),
            ok.replace("management.azure.com", "management.azure.com:444"),
            ok.replace("management.azure.com", "evil.example"),
            ok + "#x", ok.replace("?api-version=2024-03-01", ""), ok + "&api-version=1",
            ok.replace("rg-test", "rg-other"), ok.replace("/jobs/", "/jobs/../"),
            OPERATION, OPERATION.replace(SUB, "11111111-1111-1111-1111-111111111111"), None,
        ]
        for url in bad:
            with self.subTest(url), self.assertRaises(DeploymentBlocked):
                arm.trusted(url, allow_operation=url != OPERATION)

    def test_reads_retry_throttling_with_capped_retry_after(self):
        sleeps = []
        fake = FakeArm({("GET", JOB_ID): [(429, {"retry-after": "7"}, {}), (503, {"retry-after": "999"}, {}),
                                          OSError("reset"), (200, {}, {"ok": True})]})
        self.assertEqual(arm_for(fake, sleeps).get_json(gate.arm_url(JOB_ID)), {"ok": True})
        self.assertEqual(sleeps, [7.0, 60.0, 4.0])

    def test_reads_are_bounded_and_non_transient_errors_are_not_retried(self):
        fake = FakeArm({("GET", JOB_ID): [(503, {}, {})]})
        with self.assertRaises(ArmUnavailable):
            arm_for(fake).get_json(gate.arm_url(JOB_ID))
        self.assertEqual(fake.count("GET", JOB_ID), 5)
        fake = FakeArm({("GET", JOB_ID): [(403, {}, {"error": "secret detail"})]})
        with self.assertRaises(DeploymentBlocked) as raised:
            arm_for(fake).get_json(gate.arm_url(JOB_ID))
        self.assertEqual(fake.count("GET", JOB_ID), 1)
        self.assertNotIn("secret", str(raised.exception))

    def test_token_is_acquired_from_azure_cli_without_printing(self):
        run = lambda *a, **k: SimpleNamespace(returncode=0, stdout="abc.def\n", stderr="")  # noqa: E731
        self.assertEqual(gate.azure_cli_token(run), "abc.def")
        failed = lambda *a, **k: SimpleNamespace(returncode=1, stdout="", stderr="token secret")  # noqa: E731
        with self.assertRaises(DeploymentBlocked) as raised:
            gate.azure_cli_token(failed)
        self.assertNotIn("secret", str(raised.exception))


class ExecutionListTests(unittest.TestCase):
    def test_paginates_and_blocks_active_or_unknown_status(self):
        page2 = f"https://management.azure.com{EXEC_LIST}?api-version=2024-03-01&$skiptoken=2"
        fake = FakeArm({("GET", EXEC_LIST): [(200, {}, {"value": [execution("a")], "nextLink": page2}),
                                             (200, {}, {"value": [execution("b", "Failed")]})]})
        ensure_no_active_executions(JOB_ID, rest=arm_for(fake).rest)
        self.assertEqual(fake.count("GET", EXEC_LIST), 2)
        for status in ("Running", "Processing", "Unknown", None):
            fake = FakeArm({("GET", EXEC_LIST): [idle_list("Succeeded", status)]})
            with self.subTest(status), self.assertRaises(DeploymentBlocked):
                ensure_no_active_executions(JOB_ID, rest=arm_for(fake).rest)

    def test_rejects_untrusted_next_link_and_read_failure(self):
        evil = f"https://evil.example{EXEC_LIST}?api-version=2024-03-01"
        fake = FakeArm({("GET", EXEC_LIST): [(200, {}, {"value": [], "nextLink": evil})]})
        with self.assertRaises(DeploymentBlocked):
            ensure_no_active_executions(JOB_ID, rest=arm_for(fake).rest)
        fake = FakeArm({("GET", EXEC_LIST): [(500, {}, {})]})
        with self.assertRaises(DeploymentBlocked):
            ensure_no_active_executions(JOB_ID, rest=arm_for(fake).rest)


class StartExecutionTests(unittest.TestCase):
    def start(self, routes):
        fake = FakeArm(routes)
        try:
            return start_execution(JOB_ID, arm_for(fake), sleep=lambda _: None, operation_attempts=3), fake
        finally:
            self.assertEqual(fake.count("POST", START), 1, "start must be posted exactly once")
            self.assertEqual(fake.count("GET", EXEC_LIST), 0, "never infer the latest execution")

    def test_200_returns_exact_execution(self):
        name, _ = self.start({("POST", START): [(200, {}, execution("exec-1"))]})
        self.assertEqual(name, "exec-1")

    def test_202_direct_execution_location(self):
        location = f"https://management.azure.com{JOB_ID}/executions/exec-2?api-version=2024-03-01"
        name, fake = self.start({("POST", START): [(202, {"location": location}, b"")]})
        self.assertEqual(name, "exec-2")
        self.assertEqual(len(fake.calls), 1)

    def test_202_operation_polls_until_exact_execution(self):
        op_path = OPERATION.split("?")[0].replace("https://management.azure.com", "")
        name, _ = self.start({
            ("POST", START): [(202, {"location": OPERATION}, b"")],
            ("GET", op_path): [(202, {}, b""), (200, {}, {"status": "InProgress"}), (200, {}, execution("exec-3"))],
        })
        self.assertEqual(name, "exec-3")

    def test_ambiguous_outcomes_never_repost_or_guess(self):
        op_path = OPERATION.split("?")[0].replace("https://management.azure.com", "")
        other_job = f"{SCOPE}/providers/Microsoft.App/jobs/other/executions/x"
        cases = {
            "200 without id": {("POST", START): [(200, {}, {"status": "ok"})]},
            "200 other job": {("POST", START): [(200, {}, {"id": other_job})]},
            "202 no location": {("POST", START): [(202, {}, b"")]},
            "202 untrusted location": {("POST", START): [(202, {"location": OPERATION.replace(
                "management.azure.com", "evil.example")}, b"")]},
            "operation succeeded without id": {("POST", START): [(202, {"location": OPERATION}, b"")],
                                               ("GET", op_path): [(200, {}, {"status": "Succeeded"})]},
            "operation never resolves": {("POST", START): [(202, {"location": OPERATION}, b"")],
                                         ("GET", op_path): [(202, {}, b"")]},
            "operation error": {("POST", START): [(202, {"location": OPERATION}, b"")],
                                ("GET", op_path): [(404, {}, b"")]},
            "500": {("POST", START): [(500, {}, b"")]},
            "transport error": {("POST", START): [TimeoutError("timed out")]},
        }
        for label, routes in cases.items():
            with self.subTest(label), self.assertRaises(AmbiguousStart):
                self.start(routes)

    def test_definitive_rejection(self):
        with self.assertRaises(StartRejected):
            self.start({("POST", START): [(409, {}, {"error": "conflict"})]})


class ProbeTests(unittest.TestCase):
    def test_liveness_retries_then_succeeds_or_fails(self):
        probe_liveness("https://" + FQDN, open_url=fake_open({"/health/live": [urllib.error.URLError("x"), 200]}),
                       sleep=lambda _: None)
        with self.assertRaises(DeploymentBlocked):
            probe_liveness("https://" + FQDN, open_url=fake_open({"/health/live": [503]}), sleep=lambda _: None,
                           attempts=3)

    def test_anonymous_api_must_return_401(self):
        probe_anonymous_rejected("https://" + FQDN, open_url=fake_open({"/api/journal": [503, 401]}),
                                 sleep=lambda _: None)
        for status in (200, 403, 404):
            calls = []
            with self.subTest(status), self.assertRaises(DeploymentBlocked):
                probe_anonymous_rejected("https://" + FQDN, open_url=fake_open({"/api/journal": [status]}, calls),
                                         sleep=lambda _: None)
            self.assertEqual(len(calls), 1)

    def test_deployed_revision_must_run_release_with_startup_check(self):
        routes = {("GET", APP_ID): [(200, {}, app_document())],
                  ("GET", f"{APP_ID}/revisions/app--r2"): [(200, {}, revision_document())]}
        self.assertEqual(verify_deployed_revision(APP_ID, IMAGE, arm_for(FakeArm(routes))),
                         ("https://" + FQDN, "app--r2"))
        bad = {
            "image": app_document(OLD_IMAGE), "flag": app_document(flag="false"),
            "not ready": {**app_document(), "properties": {**app_document()["properties"],
                                                           "latestReadyRevisionName": "app--r1"}},
        }
        for label, document in bad.items():
            with self.subTest(label), self.assertRaises(DeploymentBlocked):
                verify_deployed_revision(APP_ID, IMAGE, arm_for(FakeArm({**routes, ("GET", APP_ID): [(200, {}, document)]})))
        with self.assertRaises(DeploymentBlocked):
            verify_deployed_revision(APP_ID, IMAGE, arm_for(FakeArm(
                {**routes, ("GET", f"{APP_ID}/revisions/app--r2"): [(200, {}, revision_document(OLD_IMAGE))]})))


def record(phase: str, execution_name: str | None = None, release: Release | None = None) -> dict:
    release = release or Release(SHA, "100", IMAGE)
    return {"schema": 1, "release": release.as_dict(), "deploy": {"run_id": "200", "run_attempt": "1"},
            "phase": phase, "execution": execution_name,
            "dependencies": {
                "targets": gate.release_targets(JOB_ID, APP_ID, MIGRATION_SQL, RUNTIME_SQL),
                "job": gate.job_dependencies(migration_plan()),
            }}


class FakeBlobCli:
    def __init__(self, stored: dict | None = None, show_error: str = "", upload_ok: bool = True):
        self.stored = stored
        self.show_error = show_error
        self.upload_ok = upload_ok
        self.calls: list[list[str]] = []

    def __call__(self, args, **kwargs):
        self.calls.append(args)
        action = args[3]
        file_arg = args[args.index("--file") + 1] if "--file" in args else None
        if action == "show":
            if self.show_error or self.stored is None:
                return SimpleNamespace(returncode=3, stdout="", stderr=self.show_error or "ErrorCode:BlobNotFound")
            return SimpleNamespace(returncode=0, stdout='"etag-1"\n', stderr="")
        if action == "download":
            Path(file_arg).write_text(json.dumps(self.stored), encoding="utf-8")
            return SimpleNamespace(returncode=0, stdout="", stderr="")
        if action == "upload":
            if not self.upload_ok:
                return SimpleNamespace(returncode=1, stdout="", stderr="ConditionNotMet secret")
            self.stored = json.loads(Path(file_arg).read_text(encoding="utf-8"))
            return SimpleNamespace(returncode=0, stdout=json.dumps({"etag": '"etag-2"'}), stderr="")
        raise AssertionError(action)


class JournalTests(unittest.TestCase):
    def setUp(self):
        executable = patch.object(gate, "azure_executable", return_value="az")
        executable.start()
        self.addCleanup(executable.stop)
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.dir = Path(self.temp.name)

    def journal(self, cli):
        return BlobJournal("stateacct", "tfstate", self.dir, run=cli)

    def test_missing_journal_then_conditional_create_and_update(self):
        cli = FakeBlobCli()
        journal = self.journal(cli)
        self.assertEqual(journal.load(), (None, None))
        etag = journal.save(record("admitted"), None)
        self.assertEqual(etag, '"etag-2"')
        self.assertIn("--if-none-match", cli.calls[-1])
        journal.save(record("job-updated"), etag)
        self.assertEqual(cli.calls[-1][cli.calls[-1].index("--if-match") + 1], etag)
        self.assertEqual(cli.calls[-1][cli.calls[-1].index("--name") + 1], gate.JOURNAL_BLOB)
        self.assertIn("--auth-mode", cli.calls[-1])
        self.assertEqual(list(self.dir.iterdir()), [], "temporary journal files are removed")

    def test_load_returns_record_and_etag(self):
        stored = record("started", "exec-1")
        loaded, etag = self.journal(FakeBlobCli(stored)).load()
        self.assertEqual((loaded, etag), (stored, '"etag-1"'))

    def test_failures_and_invalid_records_block(self):
        with self.assertRaises(DeploymentBlocked):
            self.journal(FakeBlobCli(show_error="AuthorizationPermissionMismatch")).load()
        for bad in ({**record("started", "exec-1"), "phase": "unknown"}, record("started"),
                    record("admitted", "exec-1"), {**record("admitted"), "extra": 1}, ["x"]):
            with self.subTest(bad), self.assertRaises(DeploymentBlocked):
                self.journal(FakeBlobCli(bad)).load()
        with self.assertRaises(DeploymentBlocked) as raised:
            self.journal(FakeBlobCli(upload_ok=False)).save(record("admitted"), '"etag-1"')
        self.assertNotIn("secret", str(raised.exception))

    def test_next_step_resumes_from_durable_evidence_only(self):
        release = Release(SHA, "100", IMAGE)
        other = Release("d" * 40, "99", OLD_IMAGE)
        expected = {"admitted": "migrate", "job-updated": "migrate", "start-rejected": "migrate",
                    "failed": "migrate", "started": "poll", "migrated": "promote", "promoted": "verify",
                    "verified": "verify"}
        self.assertEqual(next_step(None, release), "migrate")
        for phase, step in expected.items():
            execution_name = "exec-1" if phase in gate.EXECUTION_PHASES else None
            with self.subTest(phase):
                self.assertEqual(next_step(record(phase, execution_name), release), step)
                self.assertEqual(next_step(record(phase, execution_name, other), release),
                                 "check-previous" if phase == "started" else "migrate")
        for owner in (release, other):
            with self.assertRaises(AmbiguousStart):
                next_step(record("start-requested", release=owner), release)


class SettingsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.manifest = Path(self.temp.name) / "release.json"
        self.manifest.write_text(json.dumps({
            "sha": SHA, "run_id": "100", "image": IMAGE, "run_attempt": 1,
        }), encoding="utf-8")
        self.env = {"TF_VAR_container_image": IMAGE, "BUJO_RELEASE_SHA": SHA, "BUJO_RELEASE_RUN_ID": "100",
                    "GITHUB_RUN_ID": "200", "GITHUB_RUN_ATTEMPT": "1", "TFSTATE_STORAGE_ACCOUNT": "stateacct",
                    "TFSTATE_CONTAINER": "tfstate", "TFSTATE_KEY": "prod.tfstate", "RUNNER_TEMP": self.temp.name,
                    "BUJO_RELEASE_MANIFEST": str(self.manifest)}

    def test_valid_settings(self):
        settings = load_settings(self.env)
        self.assertEqual(settings.release, Release(SHA, "100", IMAGE))
        self.assertEqual(settings.manifest, self.manifest.resolve())

    def test_owner_recovery_needs_no_release_manifest(self):
        env = {k: v for k, v in self.env.items() if k != "BUJO_RELEASE_MANIFEST"}
        with self.assertRaises(DeploymentBlocked):
            load_settings(env)
        self.assertEqual(load_settings(env, require_manifest=False).release, Release(SHA, "100", IMAGE))

    def test_formatted_release_inputs_must_match_the_admitted_manifest(self):
        for key, value in (
            ("TF_VAR_container_image", OLD_IMAGE), ("BUJO_RELEASE_SHA", "d" * 40),
            ("BUJO_RELEASE_RUN_ID", "101"), ("BUJO_RELEASE_RUN_ATTEMPT", "2"),
        ):
            with self.subTest(key=key), self.assertRaisesRegex(DeploymentBlocked, "manifest"):
                load_settings(self.env | {key: value})

    def test_rejects_untrusted_inputs(self):
        cases = {"TF_VAR_container_image": "ghcr.io/cedricmendelin/bujo-ai:latest",
                 "BUJO_RELEASE_SHA": "main", "BUJO_RELEASE_RUN_ID": "0", "GITHUB_RUN_ATTEMPT": "",
                 "TFSTATE_STORAGE_ACCOUNT": "Bad-Name", "RUNNER_TEMP": str(Path(self.temp.name) / "missing"),
                 "TFSTATE_KEY": gate.JOURNAL_BLOB,
                 "BUJO_RELEASE_MANIFEST": str(Path(self.temp.name).parent / "release.json")}
        for key, value in cases.items():
            with self.subTest(key), self.assertRaises(DeploymentBlocked):
                load_settings({**self.env, key: value})

    def test_recheck_uses_trusted_admission_without_printing(self):
        seen = []

        def run(args, **kwargs):
            seen.append((args, kwargs))
            return SimpleNamespace(returncode=1, stdout="token-value", stderr="detail")

        with self.assertRaises(DeploymentBlocked) as raised:
            release_recheck(self.manifest, run=run)
        args, kwargs = seen[0]
        self.assertEqual(args[0], sys.executable)
        self.assertEqual(Path(args[1]), gate.ADMISSION_SCRIPT)
        self.assertEqual(args[2:], ["recheck", "--manifest", str(self.manifest)])
        self.assertTrue(kwargs["capture_output"])
        self.assertNotIn("token-value", str(raised.exception))
        release_recheck(self.manifest, run=lambda *a, **k: SimpleNamespace(returncode=0, stdout="", stderr=""))

    def test_terraform_output_goes_to_runner_log_only(self):
        log_path = Path(self.temp.name) / "tf.log"

        def run(args, **kwargs):
            kwargs["stdout"].write("Server=tcp:secret")
            return SimpleNamespace(returncode=1)

        stderr = io.StringIO()
        with redirect_stderr(stderr), self.assertRaises(DeploymentBlocked) as raised:
            run_terraform(["apply", "plan"], log_path, run=run)
        self.assertNotIn("secret", str(raised.exception) + stderr.getvalue())


class FakeJournal:
    def __init__(self, events, stored=None):
        self.stored, self.events, self.phases, self.version = stored, events, [], 0

    def load(self):
        return copy.deepcopy(self.stored), (f"e{self.version}" if self.stored else None)

    def save(self, value, etag):
        gate.validate_record(value)
        if etag != (f"e{self.version}" if self.stored else None):
            raise DeploymentBlocked("conflict")
        self.stored, self.version = copy.deepcopy(value), self.version + 1
        self.phases.append(value["phase"])
        self.events.append(f"save:{value['phase']}")
        return f"e{self.version}"


class FakeTerraform:
    def __init__(self, events, plans=None):
        self.events = events
        self.plans = plans or {"migration": migration_plan(), "promotion": promotion_plan()}
        self.applied, self.discarded = [], []

    def output(self, name):
        self.events.append(f"output:{name}")
        return {"migration_sql_connection_string": MIGRATION_SQL, "runtime_sql_connection_string": RUNTIME_SQL}[name]

    def plan(self, label, extra):
        self.events.append(f"plan:{label}")
        if label == "migration":
            assert extra == [f"-target={gate.JOB_ADDRESS}"]
        return Path(label), copy.deepcopy(self.plans[label])

    def apply(self, path, label):
        self.events.append(f"apply:{label}")
        self.applied.append(label)

    def discard(self, path):
        self.discarded.append(str(path))


EXEC_1 = f"{JOB_ID}/executions/exec-1"


def release_routes(overrides=None):
    routes = {
        ("GET", EXEC_LIST): [idle_list("Succeeded")],
        ("POST", START): [(200, {}, execution("exec-1", "Running"))],
        ("GET", EXEC_1): [(200, {}, execution("exec-1", "Running")), (200, {}, execution("exec-1"))],
        ("GET", APP_ID): [(200, {}, app_document())],
        ("GET", f"{APP_ID}/revisions/app--r2"): [(200, {}, revision_document())],
    }
    routes.update(overrides or {})
    return routes


class ControllerTests(unittest.TestCase):
    def controller(self, routes=None, stored=None, plans=None, recheck_fail_at=None, status_attempts=5):
        self.events = []
        self.arm = FakeArm(routes or release_routes(), self.events)
        self.journal = FakeJournal(self.events, stored)
        self.terraform = FakeTerraform(self.events, plans)
        self.rechecks = 0

        def recheck():
            self.rechecks += 1
            self.events.append("recheck")
            if self.rechecks == recheck_fail_at:
                raise DeploymentBlocked("stale")

        settings = Settings(Release(SHA, "100", IMAGE), "200", "1", "stateacct", "tfstate", Path("."),
                            Path("release.json"))
        return ReleaseController(
            settings, arm=arm_for(self.arm), journal=self.journal, terraform=self.terraform, job_id=JOB_ID,
            app_id=APP_ID, recheck=recheck, sleep=lambda _: None, status_attempts=status_attempts,
            open_url=fake_open({"/health/live": [200], "/api/journal": [401]}),
        )

    def index(self, event):
        return self.events.index(event)

    def test_full_release_admits_before_mutation_and_journals_each_phase(self):
        self.assertEqual(self.controller().run(), "app--r2")
        self.assertEqual(self.journal.phases, ["admitted", "job-updated", "start-requested", "started",
                                               "migrated", "promoted", "verified"])
        self.assertEqual(self.terraform.applied, ["migration", "promotion"])
        self.assertEqual(self.rechecks, 4)
        self.assertLess(self.index(f"GET:{EXEC_LIST}"), self.index("save:admitted"))
        self.assertLess(self.index("save:admitted"), self.index("apply:migration"))
        self.assertLess(self.index("save:start-requested"), self.index(f"POST:{START}"))
        self.assertLess(self.index("save:migrated"), self.index("apply:promotion"))
        self.assertEqual(self.arm.count("GET", EXEC_LIST), 4, "activity is checked before job mutation, start and promotion")
        self.assertEqual(self.arm.count("POST", START), 1)
        self.assertEqual(self.journal.stored["execution"], "exec-1")

    def test_active_execution_blocks_before_any_job_change(self):
        controller = self.controller(release_routes({("GET", EXEC_LIST): [idle_list("Running")]}))
        with self.assertRaises(DeploymentBlocked):
            controller.run()
        self.assertEqual((self.journal.phases, self.terraform.applied, self.arm.count("POST", START)), ([], [], 0))
        self.assertNotIn("plan:migration", self.events)

    def test_external_start_after_job_update_blocks_start(self):
        controller = self.controller(release_routes({("GET", EXEC_LIST): [idle_list("Succeeded"),
                                                                             idle_list("Succeeded"),
                                                                             idle_list("Running")]}))
        with self.assertRaises(DeploymentBlocked):
            controller.run()
        self.assertEqual(self.journal.phases[-1], "job-updated")
        self.assertEqual(self.arm.count("POST", START), 0)

    def test_external_start_during_plan_blocks_job_apply(self):
        controller = self.controller(release_routes({
            ("GET", EXEC_LIST): [idle_list("Succeeded"), idle_list("Running")],
        }))
        with self.assertRaises(DeploymentBlocked):
            controller.run()
        self.assertEqual(self.terraform.applied, [])
        self.assertEqual(self.arm.count("POST", START), 0)

    def test_failed_recheck_blocks_each_cloud_write(self):
        cases = {2: (["admitted"], [], 0), 3: (["admitted", "job-updated"], ["migration"], 0),
                 4: (["admitted", "job-updated", "start-requested", "started", "migrated"], ["migration"], 1)}
        for fail_at, (phases, applied, posts) in cases.items():
            controller = self.controller(recheck_fail_at=fail_at)
            with self.subTest(fail_at), self.assertRaises(DeploymentBlocked):
                controller.run()
            self.assertEqual((self.journal.phases, self.terraform.applied, self.arm.count("POST", START)),
                             (phases, applied, posts))

    def test_rerun_with_job_already_updated_is_noop_apply(self):
        self.controller(plans={"migration": noop_migration_plan(), "promotion": promotion_plan()}).run()
        self.assertEqual(self.terraform.applied, ["promotion"])
        self.assertIn("migration", self.terraform.discarded)

    def test_ambiguous_start_is_persisted_and_blocks_next_run_without_repost(self):
        controller = self.controller(release_routes({("POST", START): [(202, {}, b"")]}))
        with self.assertRaises(AmbiguousStart):
            controller.run()
        self.assertEqual(self.journal.stored["phase"], "start-requested")
        stored = self.journal.stored
        controller = self.controller(stored=stored)
        with self.assertRaises(AmbiguousStart):
            controller.run()
        self.assertEqual((self.arm.calls, self.terraform.applied), ([], []))

    def test_rejected_start_is_recorded_for_safe_retry(self):
        with self.assertRaises(StartRejected):
            self.controller(release_routes({("POST", START): [(409, {}, {})]})).run()
        self.assertEqual(self.journal.stored["phase"], "start-rejected")

    def test_resumes_known_execution_without_new_start(self):
        controller = self.controller(stored=record("started", "exec-1"))
        controller.run()
        self.assertEqual(self.arm.count("POST", START), 0)
        self.assertEqual(self.terraform.applied, ["promotion"])
        self.assertEqual(self.journal.phases, ["migrated", "promoted", "verified"])

    def test_resumed_migration_cannot_promote_after_sql_or_identity_drift(self):
        plan = promotion_plan()
        change(plan, gate.JOB_ADDRESS)["after"]["identity"] = [{"type": "SystemAssigned"}]
        controller = self.controller(
            stored=record("migrated", "exec-1"),
            plans={"migration": migration_plan(), "promotion": plan},
        )
        with self.assertRaisesRegex(DeploymentBlocked, "dependencies"):
            controller.run()
        self.assertEqual(self.terraform.applied, [])
        controller = self.controller(stored=record("migrated", "exec-1"))
        with patch.object(self.terraform, "output", side_effect=lambda name: {
            "runtime_sql_connection_string": RUNTIME_SQL,
            "migration_sql_connection_string": MIGRATION_SQL.replace("sql.example", "different.example"),
        }[name]), self.assertRaises(DeploymentBlocked):
            controller.run()
        self.assertEqual(self.terraform.applied, [])

    def test_execution_with_same_image_but_different_sql_never_promotes(self):
        wrong = execution("exec-1")
        wrong["properties"]["template"]["containers"][0]["env"][1]["value"] = RUNTIME_SQL
        controller = self.controller(
            release_routes({("GET", EXEC_1): [(200, {}, wrong)]}), stored=record("started", "exec-1"),
        )
        with self.assertRaises(ExecutionFailed):
            controller.run()
        self.assertEqual(self.terraform.applied, [])

    def test_verification_waits_for_exact_revision_but_rejects_wrong_traffic(self):
        pending = app_document()
        pending["properties"]["latestReadyRevisionName"] = "previous-revision"
        controller = self.controller(
            release_routes({("GET", APP_ID): [(200, {}, pending), (200, {}, app_document())]}),
            stored=record("promoted", "exec-1"),
        )
        self.assertEqual(controller.run(), "app--r2")
        wrong_traffic = app_document()
        wrong_traffic["properties"]["configuration"]["ingress"]["traffic"] = [
            {"weight": 100, "revisionName": "previous-revision"},
        ]
        controller = self.controller(
            release_routes({("GET", APP_ID): [(200, {}, wrong_traffic)]}),
            stored=record("promoted", "exec-1"),
        )
        with self.assertRaises(DeploymentBlocked):
            controller.run()
        self.assertEqual(self.journal.phases, [])

    def test_timeout_keeps_started_for_resume(self):
        controller = self.controller(release_routes({("GET", EXEC_1): [(200, {}, execution("exec-1", "Running"))]}),
                                     stored=record("started", "exec-1"), status_attempts=2)
        with self.assertRaises(DeploymentBlocked):
            controller.run()
        self.assertEqual((self.journal.phases, self.journal.stored["phase"]), ([], "started"))

    def test_failed_or_wrong_image_execution_never_promotes(self):
        for document in (execution("exec-1", "Failed"), execution("exec-1", "Succeeded", OLD_IMAGE)):
            controller = self.controller(release_routes({("GET", EXEC_1): [(200, {}, document)]}),
                                         stored=record("started", "exec-1"))
            with self.subTest(document["properties"]["status"]), self.assertRaises(ExecutionFailed):
                controller.run()
            self.assertEqual((self.journal.phases, self.terraform.applied), (["failed"], []))
        controller = self.controller(release_routes({("GET", EXEC_1): [(200, {}, {"properties": {}})]}),
                                     stored=record("started", "exec-1"))
        with self.assertRaises(DeploymentBlocked):
            controller.run()
        self.assertEqual(self.journal.phases, [])

    def test_duplicate_verified_release_only_reverifies(self):
        self.controller(stored=record("verified", "exec-1")).run()
        self.assertEqual(self.terraform.applied, [])
        self.assertEqual(self.arm.count("POST", START), 0)
        self.assertEqual(self.journal.phases, ["verified"])

    def test_previous_release_running_blocks_new_release(self):
        previous = record("started", "exec-1", Release("d" * 40, "99", OLD_IMAGE))
        with self.assertRaises(DeploymentBlocked):
            self.controller(release_routes({("GET", EXEC_1): [(200, {}, execution("exec-1", "Running"))]}),
                            stored=previous).run()
        self.assertEqual((self.journal.phases, self.terraform.applied), ([], []))
        self.controller(release_routes({("GET", EXEC_1): [(200, {}, execution("exec-1", "Failed", OLD_IMAGE)),
                                                            (200, {}, execution("exec-1", "Running")),
                                                            (200, {}, execution("exec-1"))]}),
                        stored=previous).run()
        self.assertEqual(self.journal.phases[-1], "verified")

    def test_promotion_drift_and_verification_failures_block(self):
        drift = promotion_plan()
        drift["resource_changes"][3]["change"]["actions"] = ["update"]
        with self.assertRaises(DeploymentBlocked):
            self.controller(plans={"migration": migration_plan(), "promotion": drift}).run()
        self.assertEqual(self.terraform.applied, ["migration"])
        self.assertIn("promotion", self.terraform.discarded)
        self.assertEqual(self.journal.stored["phase"], "migrated")
        controller = self.controller(stored=record("promoted", "exec-1"))
        controller.open_url = fake_open({"/health/live": [200], "/api/journal": [200]})
        with self.assertRaises(DeploymentBlocked):
            controller.run()
        self.assertEqual(self.journal.phases, [])

    def test_owner_recovery_of_ambiguous_start(self):
        controller = self.controller(release_routes({("GET", EXEC_1): [(200, {}, execution("exec-1", "Running"))]}),
                                     stored=record("start-requested"))
        recover_start(controller, "exec-1")
        self.assertEqual((self.journal.stored["phase"], self.journal.stored["execution"]), ("started", "exec-1"))
        controller = self.controller(stored=record("start-requested"))
        recover_start(controller, None)
        self.assertEqual(self.journal.stored["phase"], "start-rejected")
        controller = self.controller(release_routes({("GET", EXEC_1): [(200, {}, execution("exec-1", "Running",
                                                                                              OLD_IMAGE))]}),
                                     stored=record("start-requested"))
        with self.assertRaises(DeploymentBlocked):
            recover_start(controller, "exec-1")
        with self.assertRaises(DeploymentBlocked):
            recover_start(self.controller(stored=record("started", "exec-1")), None)


class MainTests(unittest.TestCase):
    def run_main(self, argv, env):
        stderr = io.StringIO()
        with redirect_stderr(stderr):
            code = main(argv, env)
        return code, stderr.getvalue()

    def test_requires_validated_and_enabled_before_any_azure_access(self):
        with patch.object(gate, "build_controller", side_effect=AssertionError("must not run")):
            for env in ({}, {"BUJO_RELEASE_VALIDATED": "true"}, {"BUJO_DEPLOYMENT_ENABLED": "true"},
                        {"BUJO_RELEASE_VALIDATED": "1", "BUJO_DEPLOYMENT_ENABLED": "true"}):
                code, output = self.run_main([], env)
                self.assertEqual(code, 1)
                self.assertIn("no Azure changes were made", output)
            code, _ = self.run_main(["recover-start", "--no-execution"], {})
            self.assertEqual(code, 1)

    def test_unexpected_errors_are_withheld(self):
        env = {"BUJO_RELEASE_VALIDATED": "true", "BUJO_DEPLOYMENT_ENABLED": "true"}
        with patch.object(gate, "build_controller", side_effect=RuntimeError("Server=tcp:secret;token")):
            code, output = self.run_main([], env)
        self.assertEqual(code, 1)
        self.assertNotIn("secret", output)
        self.assertNotIn("Traceback", output)

    def test_runs_unified_release_and_routes_recovery(self):
        env = {"BUJO_RELEASE_VALIDATED": "true", "BUJO_DEPLOYMENT_ENABLED": "true", "BUJO_OWNER_RECOVERY": "true"}
        controller = SimpleNamespace(run=lambda: "rev")
        with patch.object(gate, "build_controller", return_value=controller), \
                patch.object(gate, "recover_start") as recover:
            self.assertEqual(self.run_main([], env)[0], 0)
            self.assertEqual(self.run_main(["recover-start", "--execution", "exec-1"], env)[0], 0)
            recover.assert_called_with(controller, "exec-1")
            self.assertEqual(self.run_main(["recover-start", "--no-execution"], env)[0], 0)
            recover.assert_called_with(controller, None)
        self.assertEqual(self.run_main(["provision-migration-job"], env)[0], 1)
        self.assertEqual(self.run_main(["unknown"], env)[0], 2)

    def test_build_controller_rejects_out_of_scope_ids(self):
        settings_env = {}
        outputs = {"migration_job_id": JOB_ID,
                   "container_app_id": APP_ID.replace("rg-test", "rg-other")}
        with patch.object(gate, "load_settings", return_value=Settings(
                Release(SHA, "100", IMAGE), "200", "1", "stateacct", "tfstate", Path("."), Path("m"))), \
                patch.object(gate.TerraformRunner, "output", side_effect=lambda self_name: outputs[self_name]):
            with self.assertRaises(DeploymentBlocked):
                gate.build_controller(settings_env)


if __name__ == "__main__":
    unittest.main()
