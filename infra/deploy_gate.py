import json
import os
import re
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path
from typing import Callable


API_VERSION = "2024-03-01"
PENDING_STATUSES = {"Processing", "Running"}
REDACT_ENVIRONMENT_KEYS = (
    "ARM_SUBSCRIPTION_ID",
    "ARM_TENANT_ID",
    "ARM_CLIENT_ID",
    "TF_VAR_sql_entra_admin_object_id",
    "TF_VAR_sql_entra_admin_login",
    "TF_VAR_auth_allowed_principal_object_ids",
    "TF_VAR_budget_alert_email",
)

RestCall = Callable[[str, str, dict | None], dict]
Promotion = Callable[[], None]


def ensure_no_active_executions(job_id: str, *, rest: RestCall) -> None:
    path = f"{job_id}/executions"
    uri = f"https://management.azure.com{path}?api-version={API_VERSION}"
    seen = set()
    while uri:
        parsed = urllib.parse.urlsplit(uri)
        if (
            parsed.scheme != "https"
            or parsed.netloc != "management.azure.com"
            or parsed.path.casefold() != path.casefold()
            or uri in seen
            or len(seen) >= 100
        ):
            raise RuntimeError("Migration execution pagination is invalid; deployment is blocked.")
        seen.add(uri)
        page = rest("get", uri, None)
        if not isinstance(page, dict):
            raise RuntimeError("Migration executions could not be verified; deployment is blocked.")
        executions = page.get("value")
        if not isinstance(executions, list):
            raise RuntimeError("Migration executions could not be verified; deployment is blocked.")
        for execution in executions:
            properties = execution.get("properties") if isinstance(execution, dict) else None
            status = properties.get("status") if isinstance(properties, dict) else None
            if not isinstance(status, str) or status not in {"Succeeded", "Failed", "Stopped"}:
                raise RuntimeError(
                    "A migration execution is active or has an indeterminate status; "
                    "deployment is blocked."
                )
        next_link = page.get("nextLink")
        if next_link is not None and (not isinstance(next_link, str) or not next_link):
            raise RuntimeError("Migration execution pagination is invalid; deployment is blocked.")
        uri = next_link


def az_rest(method: str, uri: str, body: dict | None = None) -> dict:
    command = ["az", "rest", "--method", method, "--uri", uri]
    if body is not None:
        command.extend(["--body", json.dumps(body)])
    result = subprocess.run(command, check=False, capture_output=True, text=True)
    if result.returncode:
        raise RuntimeError(
            f"Azure REST request failed ({method} {uri}): {result.stderr.strip()}"
        )
    return json.loads(result.stdout) if result.stdout.strip() else {}


def wait_for_migration(
    job_id: str,
    *,
    rest: RestCall = az_rest,
    sleep: Callable[[float], None] = time.sleep,
    status_attempts: int = 360,
    poll_interval: int = 5,
) -> str:
    ensure_no_active_executions(job_id, rest=rest)
    job_uri = f"https://management.azure.com{job_id}"
    started = rest("post", f"{job_uri}/start?api-version={API_VERSION}", {})
    execution_name = started.get("name")
    execution_id = started.get("id")
    if (
        not isinstance(execution_name, str)
        or not re.fullmatch(r"[-a-zA-Z0-9_.()]+", execution_name)
        or not isinstance(execution_id, str)
        or execution_id.casefold() != f"{job_id}/executions/{execution_name}".casefold()
    ):
        raise RuntimeError(
            "Migration start did not return a verifiable execution ID; "
            "application promotion is blocked. Check the job before retrying."
        )

    execution_uri = (
        f"{job_uri}/executions/{execution_name}?api-version={API_VERSION}"
    )
    for attempt in range(status_attempts):
        execution = rest("get", execution_uri, None)
        properties = execution.get("properties")
        status = properties.get("status") if isinstance(properties, dict) else None
        if status == "Succeeded":
            return execution_name
        if status in {"Degraded", "Failed", "Stopped"}:
            raise RuntimeError(
                f"Migration execution {execution_name} finished with status {status}; "
                "check the Container Apps execution logs before retrying."
            )
        if status not in PENDING_STATUSES:
            raise RuntimeError(
                f"Migration execution {execution_name} returned an indeterminate "
                f"status: {status or 'missing'}."
            )
        if attempt + 1 < status_attempts:
            sleep(poll_interval)

    raise TimeoutError(
        f"Migration execution {execution_name} did not finish within "
        f"{status_attempts * poll_interval} seconds."
    )


def deploy_after_migration(
    job_id: str,
    *,
    rest: RestCall = az_rest,
    promote: Promotion,
    sleep: Callable[[float], None] = time.sleep,
    status_attempts: int = 360,
    poll_interval: int = 5,
) -> str:
    execution_name = wait_for_migration(
        job_id,
        rest=rest,
        sleep=sleep,
        status_attempts=status_attempts,
        poll_interval=poll_interval,
    )
    promote()
    return execution_name


def terraform_output(name: str) -> str:
    result = subprocess.run(
        ["terraform", "-chdir=infra", "output", "-raw", name],
        check=False,
        capture_output=True,
        text=True,
    )
    if result.returncode:
        raise RuntimeError(
            f"Unable to read Terraform output {name}: {result.stderr.strip()}"
        )
    return result.stdout.strip()


def redacted_log(text: str) -> str:
    for key in REDACT_ENVIRONMENT_KEYS:
        value = os.environ.get(key)
        if value:
            text = text.replace(value, "[REDACTED]")
    return text


def run_terraform(args: list[str], log_path: Path) -> None:
    with log_path.open("w", encoding="utf-8") as log:
        result = subprocess.run(
            ["terraform", "-chdir=infra", *args],
            stdout=log,
            stderr=subprocess.STDOUT,
            check=False,
            text=True,
        )
    if result.returncode == 0:
        return

    lines = redacted_log(log_path.read_text(encoding="utf-8")).splitlines()
    print("\n".join(lines[-40:]), file=sys.stderr)
    raise RuntimeError(f"Terraform command failed with exit code {result.returncode}.")


def probe_liveness(
    app_url: str,
    *,
    open_url: Callable[..., object] = urllib.request.urlopen,
    sleep: Callable[[float], None] = time.sleep,
    attempts: int = 30,
    poll_interval: int = 5,
) -> None:
    url = f"{app_url.rstrip('/')}/health/live"
    last_error = "no response"
    for attempt in range(attempts):
        try:
            with open_url(url, timeout=10) as response:
                status = response.status
            if 200 <= status < 300:
                return
            last_error = f"HTTP {status}"
        except (urllib.error.URLError, TimeoutError) as error:
            last_error = str(error)
        if attempt + 1 < attempts:
            sleep(poll_interval)
    raise RuntimeError(f"Public liveness probe failed for {url}: {last_error}.")


def promote_application() -> None:
    temp_dir = Path(os.environ.get("RUNNER_TEMP", tempfile.gettempdir()))
    plan_path = temp_dir / "prod.tfplan"
    log_path = temp_dir / "terraform-promote.log"
    run_terraform(
        [
            "plan",
            "-input=false",
            "-no-color",
            "-var-file=prod.tfvars",
            f"-out={plan_path}",
        ],
        log_path,
    )
    run_terraform(
        ["apply", "-input=false", "-auto-approve", str(plan_path)],
        log_path,
    )
    probe_liveness(terraform_output("container_app_url"))
    print("Application promotion and liveness probe succeeded.")


def provision_migration_job() -> None:
    temp_dir = Path(os.environ.get("RUNNER_TEMP", tempfile.gettempdir()))
    plan_path = temp_dir / "migration.tfplan"
    log_path = temp_dir / "terraform-migration.log"
    run_terraform(
        [
            "plan",
            "-input=false",
            "-no-color",
            "-var-file=prod.tfvars",
            "-target=azurerm_container_app_job.migration",
            f"-out={plan_path}",
        ],
        log_path,
    )
    run_terraform(
        ["apply", "-input=false", "-auto-approve", str(plan_path)],
        log_path,
    )
    print("Migration job infrastructure is ready.")


def main() -> None:
    if sys.argv[1:] == ["provision-migration-job"]:
        provision_migration_job()
        return
    if sys.argv[1:]:
        raise SystemExit("Usage: deploy_gate.py [provision-migration-job]")

    job_id = terraform_output("migration_job_id")
    execution_name = deploy_after_migration(job_id, promote=promote_application)
    print(f"Database migration execution {execution_name} succeeded.")


if __name__ == "__main__":
    main()
