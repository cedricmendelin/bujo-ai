"""Production release gate: migrate with the exact release image, then promote.

Unified entrypoint for the trusted deployment workflow:

    python3 infra/deploy_gate.py

The workflow sets BUJO_RELEASE_VALIDATED=true only after trusted CI release
admission and BUJO_DEPLOYMENT_ENABLED=true only after the owner activates
deployment. Before each cloud-write stage the gate rechecks release provenance
and freshness with release_admission.py. Errors are categorical; raw REST
bodies, Terraform plans/logs, connection strings, tokens and Azure IDs are never
printed.
"""
import copy
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Mapping


API_VERSION = "2024-03-01"
ARM_HOST = "management.azure.com"
ARM_RESOURCE = "https://management.azure.com/"
PENDING_STATUSES = {"Processing", "Running"}
INACTIVE_STATUSES = {"Succeeded", "Failed", "Stopped", "Degraded"}
FAILED_STATUSES = {"Degraded", "Failed", "Stopped"}
RETRYABLE_STATUSES = {408, 429, 500, 502, 503, 504}
REJECTED_START_STATUSES = {400, 401, 403, 404, 409}
REPOSITORY_ROOT = Path(__file__).resolve().parent.parent
ADMISSION_SCRIPT = REPOSITORY_ROOT / ".github" / "scripts" / "release_admission.py"

IMAGE_RE = re.compile(r"ghcr\.io/cedricmendelin/bujo-ai@sha256:[0-9a-f]{64}")
SHA_RE = re.compile(r"[0-9a-f]{40}")
RUN_ID_RE = re.compile(r"[1-9][0-9]{0,19}")
ACCOUNT_RE = re.compile(r"[a-z0-9]{3,24}")
CONTAINER_RE = re.compile(r"[a-z0-9](?:[a-z0-9]|-(?=[a-z0-9])){2,62}")
NAME_RE = re.compile(r"[-\w.()]{1,260}")
SCOPE_RE = r"/subscriptions/[0-9A-Fa-f-]{36}/resourceGroups/[-\w.()]{1,90}"
JOB_ID_RE = re.compile(SCOPE_RE + r"/providers/Microsoft\.App/jobs/[-\w.()]{1,260}", re.IGNORECASE)
APP_ID_RE = re.compile(SCOPE_RE + r"/providers/Microsoft\.App/containerApps/[-\w.()]{1,260}", re.IGNORECASE)
OPERATION_PATH_RE = re.compile(
    r"/subscriptions/(?P<sub>[0-9A-Fa-f-]{36})/providers/Microsoft\.App/locations/[-\w.]{1,90}"
    r"/[A-Za-z]{1,80}(?:OperationResults|OperationStatuses)/[-\w.()]{1,260}",
    re.IGNORECASE,
)
APP_FQDN_RE = re.compile(r"[a-z0-9][-a-z0-9.]{0,250}\.azurecontainerapps\.io")

JOB_ADDRESS = "azurerm_container_app_job.migration"
APP_KEEPER_ADDRESS = "module.container_app.terraform_data.update_keeper"
APP_UPDATE_ADDRESS = "module.container_app.azapi_resource_action.post_creation_update"
MIGRATION_COMMAND = ["dotnet", "Bujo.Host.dll"]
MIGRATION_ARGS = ["--migrate-only"]
STARTUP_FLAG = "Deployment__VerifyDatabaseOnStart"
JOURNAL_BLOB = "deployments/bujo-ai-prod-release.json"
JOURNAL_SCHEMA = 1
EXECUTION_PHASES = {"started", "failed", "migrated", "promoted", "verified"}
PHASES = (
    "admitted",
    "job-updated",
    "start-requested",
    "start-rejected",
    "started",
    "failed",
    "migrated",
    "promoted",
    "verified",
)
REDACT_ENVIRONMENT_KEYS = (
    "ARM_SUBSCRIPTION_ID",
    "ARM_TENANT_ID",
    "ARM_CLIENT_ID",
    "TF_VAR_sql_entra_admin_object_id",
    "TF_VAR_sql_entra_admin_login",
    "TF_VAR_auth_allowed_principal_object_ids",
    "TF_VAR_budget_alert_email",
    "TFSTATE_STORAGE_ACCOUNT",
    "TFSTATE_CONTAINER",
)

RestCall = Callable[[str, str, dict | None], dict]
Transport = Callable[[str, str, dict, bytes | None], tuple[int, dict, bytes]]


class DeploymentBlocked(RuntimeError):
    """A safe, categorical reason to stop before or without promotion."""


class AmbiguousStart(DeploymentBlocked):
    pass


class StartRejected(DeploymentBlocked):
    pass


class ExecutionFailed(DeploymentBlocked):
    pass


class ArmUnavailable(DeploymentBlocked):
    pass


def log(message: str) -> None:
    print(message, flush=True)


# --------------------------------------------------------------------------- settings


@dataclass(frozen=True)
class Release:
    sha: str
    ci_run_id: str
    image: str

    def as_dict(self) -> dict:
        return {"sha": self.sha, "ci_run_id": self.ci_run_id, "image": self.image}


@dataclass(frozen=True)
class Settings:
    release: Release
    deploy_run_id: str
    run_attempt: str
    state_account: str
    state_container: str
    temp_dir: Path
    manifest: Path


def load_settings(env: Mapping[str, str], *, require_manifest: bool = True) -> Settings:
    checks = {
        "TF_VAR_container_image": IMAGE_RE,
        "BUJO_RELEASE_SHA": SHA_RE,
        "BUJO_RELEASE_RUN_ID": RUN_ID_RE,
        "GITHUB_RUN_ID": RUN_ID_RE,
        "GITHUB_RUN_ATTEMPT": RUN_ID_RE,
        "TFSTATE_STORAGE_ACCOUNT": ACCOUNT_RE,
        "TFSTATE_CONTAINER": CONTAINER_RE,
    }
    for name, pattern in checks.items():
        if not pattern.fullmatch(env.get(name, "")):
            raise DeploymentBlocked(f"Release setting {name} is missing or malformed.")
    if not env.get("TFSTATE_KEY", "").strip() or env["TFSTATE_KEY"] == JOURNAL_BLOB:
        raise DeploymentBlocked("Release journal must be distinct from the configured Terraform state blob.")
    temp = env.get("RUNNER_TEMP", "")
    if not temp or not Path(temp).is_dir():
        raise DeploymentBlocked("Release setting RUNNER_TEMP is missing or not a directory.")
    temp_dir = Path(temp).resolve()
    manifest = Path(env.get("BUJO_RELEASE_MANIFEST", "")).resolve() if env.get("BUJO_RELEASE_MANIFEST") else None
    if require_manifest and (manifest is None or manifest.parent != temp_dir or not manifest.is_file()):
        raise DeploymentBlocked("Release setting BUJO_RELEASE_MANIFEST must name a file directly in RUNNER_TEMP.")
    if require_manifest:
        try:
            if manifest.stat().st_size > 4096:
                raise ValueError
            admitted = json.loads(manifest.read_text(encoding="utf-8"))
            if (
                not isinstance(admitted, dict)
                or admitted.get("sha") != env["BUJO_RELEASE_SHA"]
                or admitted.get("run_id") != env["BUJO_RELEASE_RUN_ID"]
                or admitted.get("image") != env["TF_VAR_container_image"]
                or (env.get("BUJO_RELEASE_RUN_ATTEMPT")
                    and str(admitted.get("run_attempt")) != env["BUJO_RELEASE_RUN_ATTEMPT"])
            ):
                raise ValueError
        except (OSError, ValueError):
            raise DeploymentBlocked("Gate release inputs do not match the admitted manifest.") from None
    return Settings(
        release=Release(env["BUJO_RELEASE_SHA"], env["BUJO_RELEASE_RUN_ID"], env["TF_VAR_container_image"]),
        deploy_run_id=env["GITHUB_RUN_ID"],
        run_attempt=env["GITHUB_RUN_ATTEMPT"],
        state_account=env["TFSTATE_STORAGE_ACCOUNT"],
        state_container=env["TFSTATE_CONTAINER"],
        temp_dir=temp_dir,
        manifest=manifest,
    )


def require_write_authorization(env: Mapping[str, str]) -> None:
    if env.get("BUJO_RELEASE_VALIDATED") != "true":
        raise DeploymentBlocked("Release admission has not validated this release; no Azure changes were made.")
    if env.get("BUJO_DEPLOYMENT_ENABLED") != "true":
        raise DeploymentBlocked("Production deployment is not enabled; no Azure changes were made.")


def release_recheck(manifest: Path, *, run=subprocess.run) -> None:
    """Re-run trusted provenance/freshness admission; output is never printed."""
    result = run(
        [sys.executable, str(ADMISSION_SCRIPT), "recheck", "--manifest", str(manifest)],
        check=False, capture_output=True, text=True, cwd=str(REPOSITORY_ROOT), timeout=180,
    )
    if result.returncode:
        raise DeploymentBlocked(
            "Release is no longer the current trusted main release (or could not be rechecked); stopped."
        )


# --------------------------------------------------------------------------- ARM


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):  # never forward the bearer token
        return None


def urllib_transport(method: str, url: str, headers: dict, body: bytes | None) -> tuple[int, dict, bytes]:
    opener = urllib.request.build_opener(_NoRedirect)
    request = urllib.request.Request(url, data=body, method=method.upper(), headers=headers)
    try:
        with opener.open(request, timeout=60) as response:
            payload = response.read(8388609)
            if len(payload) > 8388608:
                raise DeploymentBlocked("Azure response exceeded the inspection limit.")
            return response.status, {k.lower(): v for k, v in response.headers.items()}, payload
    except urllib.error.HTTPError as error:
        with error:
            payload = error.read(8388609) or b""
            if len(payload) > 8388608:
                raise DeploymentBlocked("Azure response exceeded the inspection limit.")
            return error.code, {k.lower(): v for k, v in (error.headers or {}).items()}, payload


def azure_executable() -> str:
    executable = shutil.which("az")
    if not executable:
        raise DeploymentBlocked("Azure CLI is unavailable.")
    return executable


def azure_cli_token(run=subprocess.run) -> str:
    result = run(
        [azure_executable(), "account", "get-access-token", "--resource", ARM_RESOURCE,
         "--query", "accessToken", "--output", "tsv"],
        check=False, capture_output=True, text=True, timeout=120,
    )
    token = result.stdout.strip() if result.returncode == 0 else ""
    if not token or any(ch.isspace() for ch in token):
        raise DeploymentBlocked("Azure Resource Manager access token is unavailable; details withheld.")
    return token


def resource_scope(resource_id: str) -> tuple[str, str]:
    match = re.match(r"/subscriptions/([^/]+)/resourceGroups/([^/]+)/", resource_id or "", re.IGNORECASE)
    if not match:
        raise DeploymentBlocked("Azure resource ID is malformed.")
    return match.group(1).casefold(), match.group(2).casefold()


def arm_url(path: str) -> str:
    return f"https://{ARM_HOST}{path}?api-version={API_VERSION}"


class ArmClient:
    """Minimal ARM client retaining status and headers (az rest drops Location)."""

    def __init__(
        self,
        scope_id: str,
        *,
        transport: Transport = urllib_transport,
        token: Callable[[], str] = azure_cli_token,
        sleep: Callable[[float], None] = time.sleep,
        clock: Callable[[], float] = time.monotonic,
        read_attempts: int = 5,
        max_retry_after: int = 60,
        token_lifetime: int = 1200,
    ) -> None:
        self.subscription, self.resource_group = resource_scope(scope_id)
        self._transport = transport
        self._token_source = token
        self._sleep = sleep
        self._clock = clock
        self._read_attempts = read_attempts
        self._max_retry_after = max_retry_after
        self._token_lifetime = token_lifetime
        self._token: str | None = None
        self._token_at = 0.0

    def trusted(self, url, *, allow_operation: bool = False) -> str:
        if not isinstance(url, str):
            raise DeploymentBlocked("Azure returned an untrusted URL.")
        parsed = urllib.parse.urlsplit(url)
        path = parsed.path
        rg_prefix = f"/subscriptions/{self.subscription}/resourceGroups/{self.resource_group}/".casefold()
        operation = OPERATION_PATH_RE.fullmatch(path)
        in_scope = path.casefold().startswith(rg_prefix) or (
            allow_operation and operation is not None and operation.group("sub").casefold() == self.subscription
        )
        query = urllib.parse.parse_qs(parsed.query)
        if (
            parsed.scheme != "https"
            or parsed.netloc != ARM_HOST
            or parsed.fragment
            or "//" in path
            or "%" in path
            or "\\" in path
            or any(part in {".", ".."} for part in path.split("/"))
            or not in_scope
            or len(query.get("api-version", [])) != 1
        ):
            raise DeploymentBlocked("Azure returned an untrusted URL.")
        return url

    def _headers(self) -> dict:
        now = self._clock()
        if self._token is None or now - self._token_at > self._token_lifetime:
            self._token = self._token_source()
            self._token_at = now
        return {"Authorization": f"Bearer {self._token}", "Accept": "application/json",
                "Content-Type": "application/json"}

    def _retry_delay(self, headers: dict, attempt: int) -> float:
        value = (headers or {}).get("retry-after", "")
        if isinstance(value, str) and value.isdigit():
            return float(min(int(value), self._max_retry_after))
        return float(min(2 ** attempt, self._max_retry_after))

    def get(self, url: str, *, allow_operation: bool = False) -> tuple[int, dict, bytes]:
        """Bounded read retries for throttling, transient server and network errors."""
        url = self.trusted(url, allow_operation=allow_operation)
        for attempt in range(self._read_attempts):
            try:
                status, headers, body = self._transport("GET", url, self._headers(), None)
            except (urllib.error.URLError, TimeoutError, OSError):
                status, headers, body = 0, {}, b""
            if status != 0 and status not in RETRYABLE_STATUSES:
                return status, headers, body
            if attempt + 1 < self._read_attempts:
                self._sleep(self._retry_delay(headers, attempt))
        raise ArmUnavailable("Azure Resource Manager read did not succeed after bounded retries.")

    def get_json(self, url: str) -> dict:
        status, _, body = self.get(url)
        if status != 200:
            raise DeploymentBlocked(f"Azure Resource Manager read returned HTTP {status}.")
        return parse_json(body)

    def post_once(self, url: str, payload: dict) -> tuple[int, dict, bytes]:
        """A single, never-retried POST. Transport errors surface as exceptions."""
        url = self.trusted(url)
        return self._transport("POST", url, self._headers(), json.dumps(payload).encode())

    def rest(self, method: str, uri: str, body: dict | None = None) -> dict:
        if method.casefold() != "get" or body is not None:
            raise DeploymentBlocked("Only read requests use the generic REST adapter.")
        return self.get_json(uri)


def parse_json(body: bytes) -> dict:
    try:
        value = json.loads(body or b"{}")
    except (ValueError, UnicodeDecodeError):
        raise DeploymentBlocked("Azure returned an unreadable response; details withheld.") from None
    if not isinstance(value, dict):
        raise DeploymentBlocked("Azure returned an unexpected response; details withheld.")
    return value


# --------------------------------------------------------------------------- executions


def ensure_no_active_executions(job_id: str, *, rest: RestCall) -> None:
    path = f"{job_id}/executions"
    uri = arm_url(path)
    seen = set()
    while uri:
        parsed = urllib.parse.urlsplit(uri)
        if (
            parsed.scheme != "https"
            or parsed.netloc != ARM_HOST
            or parsed.path.casefold() != path.casefold()
            or uri in seen
            or len(seen) >= 100
        ):
            raise DeploymentBlocked("Migration execution pagination is invalid; deployment is blocked.")
        seen.add(uri)
        try:
            page = rest("get", uri, None)
        except Exception:
            raise DeploymentBlocked("Migration executions could not be verified; deployment is blocked.") from None
        executions = page.get("value") if isinstance(page, dict) else None
        if not isinstance(executions, list):
            raise DeploymentBlocked("Migration executions could not be verified; deployment is blocked.")
        for execution in executions:
            properties = execution.get("properties") if isinstance(execution, dict) else None
            status = properties.get("status") if isinstance(properties, dict) else None
            if not isinstance(status, str) or status not in INACTIVE_STATUSES:
                raise DeploymentBlocked(
                    "A migration execution is active or has an indeterminate status; deployment is blocked."
                )
        next_link = page.get("nextLink")
        if next_link is not None and (not isinstance(next_link, str) or not next_link):
            raise DeploymentBlocked("Migration execution pagination is invalid; deployment is blocked.")
        uri = next_link


def execution_name_from(job_id: str, body: dict) -> str | None:
    """Return the execution name only when the body names an execution of this exact job."""
    candidates = [body]
    if isinstance(body.get("properties"), dict):
        candidates.append(body["properties"])
    prefix = f"{job_id}/executions/".casefold()
    for candidate in candidates:
        for key in ("id", "resourceId"):
            value = candidate.get(key)
            if isinstance(value, str) and value.casefold().startswith(prefix):
                name = value[len(prefix):]
                if NAME_RE.fullmatch(name) and candidate.get("name") in (None, name):
                    return name
    return None


def start_execution(
    job_id: str,
    arm: ArmClient,
    *,
    sleep: Callable[[float], None] = time.sleep,
    operation_attempts: int = 60,
    poll_interval: int = 5,
) -> str:
    """Start exactly once and return the authoritative execution name.

    HTTP 200 must name the execution. HTTP 202 must carry a trusted Location that
    names, or resolves to, the execution. Anything else is ambiguous: the start is
    never re-posted and the latest execution is never inferred.
    """
    try:
        status, headers, body = arm.post_once(arm_url(f"{job_id}/start"), {})
    except DeploymentBlocked:
        raise
    except Exception:
        raise AmbiguousStart("Migration start request outcome is unknown; owner recovery is required.") from None

    if status in REJECTED_START_STATUSES:
        raise StartRejected(f"Azure rejected the migration start with HTTP {status}; no execution was started.")
    if status == 200:
        try:
            name = execution_name_from(job_id, parse_json(body))
        except DeploymentBlocked:
            name = None
        if name:
            return name
        raise AmbiguousStart("Migration start did not identify its execution; owner recovery is required.")
    if status != 202:
        raise AmbiguousStart(f"Migration start returned HTTP {status}; owner recovery is required.")

    try:
        location = arm.trusted((headers or {}).get("location"), allow_operation=True)
    except DeploymentBlocked:
        raise AmbiguousStart("Migration start returned no trusted operation location; owner recovery is required.") from None
    path = urllib.parse.urlsplit(location).path
    direct = f"{job_id}/executions/"
    if path.casefold().startswith(direct.casefold()) and NAME_RE.fullmatch(path[len(direct):]):
        return path[len(direct):]

    for attempt in range(operation_attempts):
        try:
            op_status, _, op_body = arm.get(location, allow_operation=True)
        except DeploymentBlocked:
            raise AmbiguousStart("Migration start operation could not be read; owner recovery is required.") from None
        if op_status == 200:
            try:
                document = parse_json(op_body)
            except DeploymentBlocked:
                document = {}
            name = execution_name_from(job_id, document)
            if name:
                return name
            if document.get("status") not in {"InProgress", "Accepted", "Running"}:
                raise AmbiguousStart(
                    "Migration start operation did not identify its execution; owner recovery is required."
                )
        elif op_status != 202:
            raise AmbiguousStart(f"Migration start operation returned HTTP {op_status}; owner recovery is required.")
        if attempt + 1 < operation_attempts:
            sleep(poll_interval)
    raise AmbiguousStart("Migration start operation did not resolve in time; owner recovery is required.")


def execution_status(job_id: str, execution_name: str, arm: ArmClient) -> tuple[str | None, dict]:
    execution = arm.get_json(arm_url(f"{job_id}/executions/{execution_name}"))
    if (
        execution.get("id", "").casefold() != f"{job_id}/executions/{execution_name}".casefold()
        or execution.get("name") != execution_name
    ):
        raise DeploymentBlocked("Execution response does not identify the exact requested migration.")
    properties = execution.get("properties")
    if not isinstance(properties, dict):
        return None, {}
    status = properties.get("status")
    return (status if isinstance(status, str) else None), properties


def execution_images(properties: dict) -> list:
    template = properties.get("template")
    containers = template.get("containers") if isinstance(template, dict) else None
    if not isinstance(containers, list):
        return []
    return [c.get("image") if isinstance(c, dict) else None for c in containers]


def wait_for_execution(
    job_id: str,
    execution_name: str,
    image: str,
    arm: ArmClient,
    *,
    sleep: Callable[[float], None] = time.sleep,
    status_attempts: int = 360,
    poll_interval: int = 5,
    trusted_sql: str | None = None,
) -> None:
    for attempt in range(status_attempts):
        status, properties = execution_status(job_id, execution_name, arm)
        if status == "Succeeded":
            if execution_images(properties) != [image]:
                raise ExecutionFailed("The migration execution did not run the release image; promotion is blocked.")
            if trusted_sql is not None:
                try:
                    container = _single_container(properties["template"])
                    if (
                        container.get("command") != MIGRATION_COMMAND
                        or container.get("args") != MIGRATION_ARGS
                        or env_map(container.get("env")) != {
                            "DOTNET_ENVIRONMENT": "Production",
                            "ConnectionStrings__journal": trusted_sql,
                        }
                    ):
                        raise ValueError
                except (KeyError, TypeError, ValueError):
                    raise ExecutionFailed("The migration execution target or command does not match the release.") from None
            return
        if status in FAILED_STATUSES:
            raise ExecutionFailed(
                f"Migration execution finished with status {status}; check the execution logs before retrying."
            )
        if status not in PENDING_STATUSES:
            raise DeploymentBlocked("Migration execution returned an indeterminate status; promotion is blocked.")
        if attempt + 1 < status_attempts:
            sleep(poll_interval)
    raise DeploymentBlocked(
        f"Migration execution did not finish within {status_attempts * poll_interval} seconds; "
        "a rerun resumes the same execution."
    )


# --------------------------------------------------------------------------- journal


def validate_record(record: object) -> dict:
    try:
        if not isinstance(record, dict) or record.get("schema") != JOURNAL_SCHEMA:
            raise ValueError
        release, deploy = record["release"], record["deploy"]
        if (
            set(record) != {"schema", "release", "deploy", "phase", "execution", "dependencies"}
            or set(release) != {"sha", "ci_run_id", "image"}
            or not SHA_RE.fullmatch(release["sha"])
            or not RUN_ID_RE.fullmatch(release["ci_run_id"])
            or not IMAGE_RE.fullmatch(release["image"])
            or set(deploy) != {"run_id", "run_attempt"}
            or not RUN_ID_RE.fullmatch(deploy["run_id"])
            or not RUN_ID_RE.fullmatch(deploy["run_attempt"])
            or record["phase"] not in PHASES
        ):
            raise ValueError
        if (
            not isinstance(record["dependencies"], dict)
            or set(record["dependencies"]) != {"targets", "job"}
            or any(not re.fullmatch(r"[a-f0-9]{64}", value) for value in record["dependencies"].values())
        ):
            raise ValueError
        execution = record["execution"]
        if (record["phase"] in EXECUTION_PHASES) != isinstance(execution, str):
            raise ValueError
        if execution is not None and not NAME_RE.fullmatch(execution):
            raise ValueError
    except (KeyError, TypeError, ValueError):
        raise DeploymentBlocked("The release journal is unreadable or inconsistent; owner recovery is required.") from None
    return record


class BlobJournal:
    """Release journal blob in the state container, separate from the Terraform state blob.

    Writes are conditional on the previously read ETag (or on absence), so a
    concurrent or stale writer fails closed instead of overwriting evidence.
    """

    def __init__(self, account: str, container: str, temp_dir: Path, *, run=subprocess.run, blob: str = JOURNAL_BLOB):
        self._base = ["--auth-mode", "login", "--account-name", account, "--container-name", container, "--name", blob]
        self._temp_dir = temp_dir
        self._run = run

    def _az(self, args: list[str]):
        return self._run(
            [azure_executable(), "storage", "blob", *args, *self._base],
            check=False, capture_output=True, text=True, timeout=120,
        )

    def _temp_file(self) -> Path:
        handle, name = tempfile.mkstemp(prefix="bujo-journal-", suffix=".json", dir=self._temp_dir)
        os.close(handle)
        return Path(name)

    def load(self) -> tuple[dict | None, str | None]:
        shown = self._az(["show", "--query", "properties.etag", "--output", "tsv"])
        if shown.returncode:
            if re.search(r"\bBlobNotFound\b", shown.stderr or ""):
                return None, None
            raise DeploymentBlocked("The release journal could not be read; deployment is blocked.")
        etag = (shown.stdout or "").strip()
        if not etag:
            raise DeploymentBlocked("The release journal could not be read; deployment is blocked.")
        path = self._temp_file()
        try:
            downloaded = self._az(["download", "--file", str(path), "--if-match", etag, "--no-progress", "--output", "none"])
            if downloaded.returncode:
                raise DeploymentBlocked("The release journal changed while reading; deployment is blocked.")
            try:
                record = json.loads(path.read_text(encoding="utf-8"))
            except (ValueError, OSError):
                record = None
            return validate_record(record), etag
        finally:
            path.unlink(missing_ok=True)

    def save(self, record: dict, etag: str | None) -> str:
        validate_record(record)
        path = self._temp_file()
        path.write_text(json.dumps(record, sort_keys=True), encoding="utf-8")
        condition = ["--if-match", etag] if etag else ["--if-none-match", "*"]
        try:
            result = self._az(["upload", "--file", str(path), "--overwrite", "true", *condition, "--output", "json"])
        finally:
            path.unlink(missing_ok=True)
        try:
            new_etag = json.loads(result.stdout).get("etag") if result.returncode == 0 else None
        except (ValueError, AttributeError, TypeError):
            new_etag = None
        if not isinstance(new_etag, str) or not new_etag:
            raise DeploymentBlocked(
                "The release journal could not be updated (concurrent change or access failure); deployment stopped."
            )
        return new_etag


def next_step(record: dict | None, release: Release) -> str:
    """Decide how to continue from durable evidence; never guesses an execution."""
    if record is None:
        return "migrate"
    phase = record["phase"]
    if phase == "start-requested":
        raise AmbiguousStart(
            "A previous migration start has no confirmed execution; owner recovery is required before any release."
        )
    if record["release"] != release.as_dict():
        return "check-previous" if phase == "started" else "migrate"
    return {
        "admitted": "migrate",
        "job-updated": "migrate",
        "start-rejected": "migrate",
        "failed": "migrate",
        "started": "poll",
        "migrated": "promote",
        "promoted": "verify",
        "verified": "verify",
    }[phase]


# --------------------------------------------------------------------------- Terraform


def redacted_log(text: str) -> str:
    for key in REDACT_ENVIRONMENT_KEYS:
        value = os.environ.get(key)
        if value:
            text = text.replace(value, "[REDACTED]")
    return text


def run_terraform(args: list[str], log_path: Path, *, run=subprocess.run) -> None:
    """Run Terraform with output kept in the ephemeral runner log, never printed."""
    with log_path.open("w", encoding="utf-8") as stream:
        result = run(
            ["terraform", "-chdir=infra", *args], stdout=stream, stderr=subprocess.STDOUT,
            check=False, text=True, timeout=1800,
        )
    if result.returncode:
        raise DeploymentBlocked(f"Terraform {args[0]} failed with exit code {result.returncode}; output withheld.")


class TerraformRunner:
    def __init__(self, temp_dir: Path, *, run=subprocess.run) -> None:
        self.temp_dir = temp_dir
        self._run = run

    def output(self, name: str) -> str:
        result = self._run(
            ["terraform", "-chdir=infra", "output", "-raw", name],
            check=False, capture_output=True, text=True, timeout=120,
        )
        if result.returncode or not (result.stdout or "").strip():
            raise DeploymentBlocked(f"Trusted Terraform output {name} is unavailable; owner bootstrap may be incomplete.")
        return result.stdout.strip()

    def plan(self, label: str, extra: list[str]) -> tuple[Path, dict]:
        plan_path = self.temp_dir / f"{label}.tfplan"
        run_terraform(
            ["plan", "-input=false", "-no-color", "-lock-timeout=5m", "-var-file=prod.tfvars", *extra,
             f"-out={plan_path}"],
            self.temp_dir / f"terraform-{label}-plan.log",
            run=self._run,
        )
        shown = self._run(
            ["terraform", "-chdir=infra", "show", "-json", str(plan_path)],
            check=False, capture_output=True, text=True, timeout=120,
        )
        try:
            if shown.returncode:
                raise ValueError
            plan = json.loads(shown.stdout)
            if not isinstance(plan, dict):
                raise ValueError
        except (ValueError, TypeError):
            plan_path.unlink(missing_ok=True)
            raise DeploymentBlocked(f"Unable to inspect the {label} plan; details withheld.") from None
        return plan_path, plan

    def apply(self, plan_path: Path, label: str) -> None:
        try:
            run_terraform(["apply", "-input=false", "-no-color", "-lock-timeout=5m", str(plan_path)],
                          self.temp_dir / f"terraform-{label}-apply.log", run=self._run)
        finally:
            plan_path.unlink(missing_ok=True)

    def discard(self, plan_path: Path) -> None:
        plan_path.unlink(missing_ok=True)


def has_unknown(value) -> bool:
    if isinstance(value, dict):
        return any(has_unknown(item) for item in value.values())
    if isinstance(value, list):
        return any(has_unknown(item) for item in value)
    return value is True


def managed_changes(plan: dict) -> list[tuple[str, list, dict]]:
    if plan.get("errored") or plan.get("deferred_changes") or not isinstance(plan.get("resource_changes"), list):
        raise DeploymentBlocked("Plan inventory is incomplete or errored.")
    result = []
    for resource in plan["resource_changes"]:
        if not isinstance(resource, dict):
            raise DeploymentBlocked("Plan inventory is incomplete or errored.")
        if resource.get("mode") != "managed":
            continue
        change = resource.get("change")
        if not isinstance(change, dict) or not isinstance(change.get("actions"), list):
            raise DeploymentBlocked("Plan inventory is incomplete or errored.")
        result.append((resource.get("address"), change["actions"], change))
    return result


def require_output_unchanged(plan: dict, name: str) -> None:
    change = (plan.get("output_changes") or {}).get(name)
    if change is not None and (not isinstance(change, dict) or change.get("actions") != ["no-op"]):
        raise DeploymentBlocked("Plan changes a trusted SQL connection target.")


def fingerprint(value) -> str:
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def release_targets(job_id: str, app_id: str, migration_sql: str, runtime_sql: str) -> str:
    require_trusted_sql_target(migration_sql)
    require_trusted_sql_target(runtime_sql)
    return fingerprint([job_id, app_id, migration_sql, runtime_sql])


def job_dependencies(plan: dict) -> str:
    for address, _, change in managed_changes(plan):
        if address == JOB_ADDRESS:
            job = copy.deepcopy(change.get("after"))
            if not isinstance(job, dict):
                break
            for key in ("id", "outbound_ip_addresses", "event_stream_endpoint"):
                job.pop(key, None)
            try:
                container = job["template"][0]["container"][0]
                for key in ("image", "command", "args", "env"):
                    container.pop(key, None)
            except (KeyError, IndexError, TypeError):
                break
            return fingerprint(job)
    raise DeploymentBlocked("The plan cannot prove the migrated job dependencies.")


def require_trusted_sql_target(value: str) -> None:
    if (
        not isinstance(value, str)
        or not value.startswith("Server=tcp:")
        or "Authentication=Active Directory Managed Identity" not in value
        or re.search(r"(?i)password|pwd=", value)
    ):
        raise DeploymentBlocked("Trusted SQL connection target is malformed; owner bootstrap may be incomplete.")


def env_map(entries) -> dict:
    if entries in (None, []):
        return {}
    if not isinstance(entries, list):
        raise ValueError
    mapping = {}
    for entry in entries:
        name = entry.get("name") if isinstance(entry, dict) else None
        if not isinstance(name, str) or name in mapping or entry.get("secret_name") or entry.get("secretRef"):
            raise ValueError
        mapping[name] = entry.get("value")
    return mapping


def validate_migration_plan(plan: dict, image: str, trusted_sql: str) -> bool:
    """Allow only the job's image/command/args/env moving to the release, or a no-op.

    The SQL target must equal the trusted Terraform output from the owner-applied
    state; identity, environment and every other dependency must be unchanged.
    Returns True when the saved plan needs applying.
    """
    if not IMAGE_RE.fullmatch(image or ""):
        raise DeploymentBlocked("The migration image is not an immutable approved release digest.")
    require_trusted_sql_target(trusted_sql)
    job = None
    for address, actions, change in managed_changes(plan):
        if address == JOB_ADDRESS:
            if job is not None or actions not in (["no-op"], ["update"]):
                raise DeploymentBlocked("Migration plan contains an unsafe job action.")
            job = (actions, change)
        elif actions != ["no-op"]:
            raise DeploymentBlocked("Migration plan contains an unexpected resource change.")
    if job is None:
        raise DeploymentBlocked("Migration plan does not contain the bootstrapped job.")
    require_output_unchanged(plan, "migration_sql_connection_string")
    actions, change = job
    before, after = change.get("before"), change.get("after")
    unknown = change.get("after_unknown") or {}
    if not isinstance(before, dict) or not isinstance(after, dict) or not isinstance(unknown, dict):
        raise DeploymentBlocked("Migration job state is unavailable; owner bootstrap may be incomplete.")
    computed = {"id", "outbound_ip_addresses", "event_stream_endpoint"}
    if any(has_unknown(value) for key, value in unknown.items() if key not in computed):
        raise DeploymentBlocked("Migration plan has unknown execution dependencies.")
    if any(before.get(key) != after.get(key) for key in set(before) | set(after) if key != "template"):
        raise DeploymentBlocked("Migration plan changes job identity, environment or configuration.")
    try:
        if len(before["template"]) != 1 or len(after["template"]) != 1:
            raise ValueError
        old_template, new_template = before["template"][0], after["template"][0]
        if len(old_template["container"]) != 1 or len(new_template["container"]) != 1:
            raise ValueError
        if any(old_template.get(k) != new_template.get(k)
               for k in set(old_template) | set(new_template) if k != "container"):
            raise ValueError
        old_container, new_container = old_template["container"][0], new_template["container"][0]
        allowed = {"image", "command", "args", "env"}
        if any(old_container.get(k) != new_container.get(k)
               for k in set(old_container) | set(new_container) if k not in allowed):
            raise ValueError
        if (
            new_container.get("image") != image
            or new_container.get("command") != MIGRATION_COMMAND
            or new_container.get("args") != MIGRATION_ARGS
            or env_map(new_container.get("env")) != {
                "DOTNET_ENVIRONMENT": "Production",
                "ConnectionStrings__journal": trusted_sql,
            }
        ):
            raise ValueError
        old_env = env_map(old_container.get("env"))
        if old_env and old_env.get("ConnectionStrings__journal") != trusted_sql:
            raise ValueError
    except (KeyError, IndexError, TypeError, ValueError, AttributeError):
        raise DeploymentBlocked("Migration plan has an unexpected container template or SQL target.") from None
    return actions == ["update"]


def _single_container(template) -> dict:
    containers = template.get("containers") if isinstance(template, dict) else None
    if not isinstance(containers, list) or len(containers) != 1 or not isinstance(containers[0], dict):
        raise ValueError
    return containers[0]


def validate_promotion_plan(plan: dict, image: str, trusted_runtime_sql: str, expected_app_id: str | None = None) -> bool:
    """Allow only the app image (and the startup verification flag) to change.

    The migration job must be a no-op so promotion cannot alter the migrated
    image, SQL target or identity. Returns True when the saved plan needs applying.
    """
    if not IMAGE_RE.fullmatch(image or ""):
        raise DeploymentBlocked("The application image is not an immutable approved release digest.")
    require_trusted_sql_target(trusted_runtime_sql)
    found: dict[str, tuple[list, dict]] = {}
    for address, actions, change in managed_changes(plan):
        if address in {APP_KEEPER_ADDRESS, APP_UPDATE_ADDRESS, JOB_ADDRESS}:
            if address in found:
                raise DeploymentBlocked("Promotion plan has duplicate application resources.")
            found[address] = (actions, change)
        elif actions != ["no-op"]:
            raise DeploymentBlocked("Promotion plan contains infrastructure drift or an unexpected change.")
    if set(found) != {APP_KEEPER_ADDRESS, APP_UPDATE_ADDRESS, JOB_ADDRESS}:
        raise DeploymentBlocked("Promotion plan is missing the application or migration job.")
    if found[JOB_ADDRESS][0] != ["no-op"]:
        raise DeploymentBlocked("Promotion plan would change the migrated job.")
    require_output_unchanged(plan, "runtime_sql_connection_string")
    require_output_unchanged(plan, "migration_sql_connection_string")

    keeper_actions, keeper = found[APP_KEEPER_ADDRESS]
    update_actions, update = found[APP_UPDATE_ADDRESS]
    try:
        if has_unknown((keeper.get("after_unknown") or {}).get("input")):
            raise ValueError
        after_input = keeper["after"]["input"]
        after_container = _single_container(after_input)
        after_env = env_map(after_container.get("env"))
        if (
            after_container.get("image") != image
            or after_env.get(STARTUP_FLAG) != "true"
            or after_env.get("ConnectionStrings__journal") != trusted_runtime_sql
        ):
            raise ValueError
        if keeper_actions == ["no-op"]:
            if update_actions != ["no-op"]:
                raise ValueError
            return False
        if keeper_actions != ["update"] or update_actions not in (["delete", "create"], ["create", "delete"]):
            raise ValueError
        before_input = keeper["before"]["input"]
        before_container = _single_container(before_input)
        before_env = env_map(before_container.get("env"))
        if any(before_input.get(k) != after_input.get(k)
               for k in set(before_input) | set(after_input) if k != "containers"):
            raise ValueError
        if any(before_container.get(k) != after_container.get(k)
               for k in set(before_container) | set(after_container) if k not in {"image", "env"}):
            raise ValueError
        without_flag = lambda env: {k: v for k, v in env.items() if k != STARTUP_FLAG}  # noqa: E731
        if without_flag(before_env) != without_flag(after_env):
            raise ValueError
        unknown_action = copy.deepcopy(update.get("after_unknown") or {})
        computed_action_fields = {"id", "output", "exist", "sensitive_output", "when"}
        computed_retry_fields = {"max_interval_seconds", "multiplier", "randomization_factor"}
        for key in computed_action_fields:
            unknown_action.pop(key, None)
        if isinstance(unknown_action.get("retry"), dict):
            for key in computed_retry_fields:
                unknown_action["retry"].pop(key, None)
        if has_unknown(unknown_action):
            raise ValueError
        old_action, new_action = update.get("before") or {}, update["after"]
        if (
            new_action.get("method") != "PATCH"
            or not isinstance(new_action.get("resource_id"), str)
            or not APP_ID_RE.fullmatch(new_action["resource_id"])
            or (old_action and old_action.get("resource_id") != new_action["resource_id"])
            or (expected_app_id is not None and new_action["resource_id"] != expected_app_id)
        ):
            raise ValueError
        patched = _single_container(new_action["body"]["properties"]["template"])
        if patched.get("image") != image or env_map(patched.get("env")) != after_env:
            raise ValueError
        old_patch, new_patch = copy.deepcopy(old_action), copy.deepcopy(new_action)
        for action in (old_patch, new_patch):
            for key in {"id", "output", "exist", "sensitive_output"}:
                action.pop(key, None)
            unknown = update.get("after_unknown") or {}
            if unknown.get("when") is True:
                action.pop("when", None)
            if isinstance(action.get("retry"), dict):
                for key in computed_retry_fields:
                    if (unknown.get("retry") or {}).get(key) is True:
                        action["retry"].pop(key, None)
            container = _single_container(action["body"]["properties"]["template"])
            container.pop("image", None)
            container["env"] = [
                entry for entry in container.get("env") or [] if entry.get("name") != STARTUP_FLAG
            ]
        if old_patch != new_patch:
            raise ValueError
    except (KeyError, IndexError, TypeError, ValueError, AttributeError):
        raise DeploymentBlocked(
            "Promotion plan changes more than the application image and startup verification flag."
        ) from None
    return True


# --------------------------------------------------------------------------- probes


def public_open(url: str, *, timeout: int):
    return urllib.request.build_opener(_NoRedirect).open(url, timeout=timeout)


def probe_liveness(
    app_url: str,
    *,
    open_url: Callable[..., object] = public_open,
    sleep: Callable[[float], None] = time.sleep,
    attempts: int = 60,
    poll_interval: int = 5,
) -> None:
    url = f"{app_url.rstrip('/')}/health/live"
    for attempt in range(attempts):
        try:
            with open_url(url, timeout=10) as response:
                if 200 <= response.status < 300:
                    return
        except urllib.error.HTTPError as error:
            error.close()
        except (urllib.error.URLError, TimeoutError, OSError):
            pass
        if attempt + 1 < attempts:
            sleep(poll_interval)
    raise DeploymentBlocked("Public liveness probe failed; endpoint and error details withheld.")


def probe_anonymous_rejected(
    app_url: str,
    *,
    open_url: Callable[..., object] = public_open,
    sleep: Callable[[float], None] = time.sleep,
    attempts: int = 5,
    poll_interval: int = 5,
) -> None:
    url = f"{app_url.rstrip('/')}/api/journal"
    for attempt in range(attempts):
        try:
            with open_url(url, timeout=10) as response:
                status = response.status
        except urllib.error.HTTPError as error:
            status = error.code
            error.close()
        except (urllib.error.URLError, TimeoutError, OSError):
            status = None
        if status == 401:
            return
        if status is not None and status < 500:
            raise DeploymentBlocked(
                f"Anonymous API request returned HTTP {status} instead of 401; release is not verified."
            )
        if attempt + 1 < attempts:
            sleep(poll_interval)
    raise DeploymentBlocked("Anonymous API authorization probe did not complete; release is not verified.")


def verify_deployed_revision(
    app_id: str, image: str, arm: ArmClient, trusted_runtime_sql: str | None = None,
) -> tuple[str, str]:
    """Return (public URL, revision) when the latest ready revision runs the release."""
    app = arm.get_json(arm_url(app_id))
    try:
        properties = app["properties"]
        container = _single_container(properties["template"])
        env = {e.get("name"): e.get("value") for e in container.get("env") or [] if isinstance(e, dict)}
        revision = properties["latestRevisionName"]
        fqdn = properties["configuration"]["ingress"]["fqdn"]
        traffic = properties["configuration"]["ingress"]["traffic"]
        if (
            properties.get("provisioningState") != "Succeeded"
            or container.get("image") != image
            or env.get(STARTUP_FLAG) != "true"
            or (trusted_runtime_sql is not None and env.get("ConnectionStrings__journal") != trusted_runtime_sql)
            or properties["configuration"].get("activeRevisionsMode") != "Single"
            or not isinstance(traffic, list) or len(traffic) != 1
            or traffic[0].get("weight") != 100
            or not (traffic[0].get("latestRevision") is True or traffic[0].get("revisionName") == revision)
            or not isinstance(revision, str)
            or not NAME_RE.fullmatch(revision)
            or properties.get("latestReadyRevisionName") != revision
            or not isinstance(fqdn, str)
            or not APP_FQDN_RE.fullmatch(fqdn)
        ):
            raise ValueError
        detail = arm.get_json(arm_url(f"{app_id}/revisions/{revision}"))["properties"]
        revision_container = _single_container(detail["template"])
        revision_env = env_map(revision_container.get("env"))
        if (
            revision_container.get("image") != image
            or revision_env.get(STARTUP_FLAG) != "true"
            or (trusted_runtime_sql is not None
                and revision_env.get("ConnectionStrings__journal") != trusted_runtime_sql)
            or detail.get("provisioningState") != "Provisioned"
            or detail.get("healthState") != "Healthy"
            or detail.get("active") is not True
        ):
            raise ValueError
    except (KeyError, IndexError, TypeError, ValueError, AttributeError):
        raise DeploymentBlocked("The deployed revision does not run the verified release image.") from None
    return f"https://{fqdn}", revision


# --------------------------------------------------------------------------- controller


class ReleaseController:
    """Journal-backed release: admit, update job, start once, poll, promote, verify."""

    def __init__(
        self,
        settings: Settings,
        *,
        arm: ArmClient,
        journal,
        terraform,
        job_id: str,
        app_id: str,
        recheck: Callable[[], None],
        open_url: Callable[..., object] = public_open,
        sleep: Callable[[float], None] = time.sleep,
        status_attempts: int = 360,
        poll_interval: int = 5,
    ) -> None:
        self.settings = settings
        self.arm = arm
        self.journal = journal
        self.terraform = terraform
        self.job_id = job_id
        self.app_id = app_id
        self.recheck = recheck
        self.open_url = open_url
        self.sleep = sleep
        self.status_attempts = status_attempts
        self.poll_interval = poll_interval
        self.etag: str | None = None
        self.execution: str | None = None
        self.dependencies: dict | None = None

    @property
    def image(self) -> str:
        return self.settings.release.image

    def _save(self, phase: str) -> None:
        record = {
            "schema": JOURNAL_SCHEMA,
            "release": self.settings.release.as_dict(),
            "deploy": {"run_id": self.settings.deploy_run_id, "run_attempt": self.settings.run_attempt},
            "phase": phase,
            "execution": self.execution if phase in EXECUTION_PHASES else None,
            "dependencies": self.dependencies,
        }
        self.etag = self.journal.save(record, self.etag)

    def _no_active(self) -> None:
        ensure_no_active_executions(self.job_id, rest=self.arm.rest)

    def run(self) -> str:
        record, self.etag = self.journal.load()
        step = next_step(record, self.settings.release)
        if step == "check-previous":
            status, _ = execution_status(self.job_id, record["execution"], self.arm)
            if status not in INACTIVE_STATUSES:
                raise DeploymentBlocked("A previous release's migration is still running or indeterminate.")
            log("A previous release's migration execution has finished; it was not promoted.")
            step = "migrate"
        if record is not None and step in {"poll", "promote", "verify"}:
            self.execution = record["execution"]
            self.dependencies = record["dependencies"]
            log(f"Resuming this release from journal phase '{record['phase']}'.")

        if step == "migrate":
            self._migrate()
            step = "poll"
        if step == "poll":
            self._poll()
            step = "promote"
        if step == "promote":
            self._promote()
        return self._verify()

    def _migrate(self) -> None:
        self.recheck()
        self._no_active()  # admission happens before any job template change
        trusted_sql = self.terraform.output("migration_sql_connection_string")
        runtime_sql = self.terraform.output("runtime_sql_connection_string")
        plan_path, plan = self.terraform.plan("migration", [f"-target={JOB_ADDRESS}"])
        try:
            changed = validate_migration_plan(plan, self.image, trusted_sql)
            self.dependencies = {
                "targets": release_targets(self.job_id, self.app_id, trusted_sql, runtime_sql),
                "job": job_dependencies(plan),
            }
            self._save("admitted")
            self.recheck()
            self._no_active()
        except DeploymentBlocked:
            self.terraform.discard(plan_path)
            raise
        if changed:
            self.terraform.apply(plan_path, "migration")
            log("Migration job updated to the release image.")
        else:
            self.terraform.discard(plan_path)
            log("Migration job already uses the release image.")
        self._save("job-updated")
        self.recheck()
        self._no_active()  # detect external starts after the template change
        self._save("start-requested")
        try:
            self.execution = start_execution(self.job_id, self.arm, sleep=self.sleep, poll_interval=self.poll_interval)
        except StartRejected:
            self._save("start-rejected")
            raise
        self._save("started")
        log("Migration execution started and correlated.")

    def _poll(self) -> None:
        try:
            wait_for_execution(
                self.job_id, self.execution, self.image, self.arm,
                sleep=self.sleep, status_attempts=self.status_attempts, poll_interval=self.poll_interval,
                trusted_sql=self.terraform.output("migration_sql_connection_string"),
            )
        except ExecutionFailed:
            self._save("failed")
            raise
        self._save("migrated")
        log("Migration execution succeeded with the release image.")

    def _promote(self) -> None:
        trusted_runtime = self.terraform.output("runtime_sql_connection_string")
        plan_path, plan = self.terraform.plan("promotion", [])
        try:
            changed = validate_promotion_plan(plan, self.image, trusted_runtime, self.app_id)
            targets = release_targets(
                self.job_id, self.app_id,
                self.terraform.output("migration_sql_connection_string"), trusted_runtime,
            )
            if self.dependencies != {"targets": targets, "job": job_dependencies(plan)}:
                raise DeploymentBlocked("SQL, identity or environment dependencies changed after migration.")
            if changed:
                self.recheck()
                self._no_active()
        except DeploymentBlocked:
            self.terraform.discard(plan_path)
            raise
        if changed:
            self.terraform.apply(plan_path, "promotion")
            log("Application promoted to the release image.")
        else:
            self.terraform.discard(plan_path)
            log("Application already uses the release image.")
        self._save("promoted")

    def _verify(self) -> str:
        runtime_sql = self.terraform.output("runtime_sql_connection_string")
        targets = release_targets(
            self.job_id, self.app_id, self.terraform.output("migration_sql_connection_string"), runtime_sql,
        )
        if self.dependencies is None or self.dependencies["targets"] != targets:
            raise DeploymentBlocked("Trusted release targets changed before runtime verification.")
        for attempt in range(60):
            try:
                url, revision = verify_deployed_revision(self.app_id, self.image, self.arm, runtime_sql)
                break
            except DeploymentBlocked:
                if attempt == 59:
                    raise
                self.sleep(5)
        probe_liveness(url, open_url=self.open_url, sleep=self.sleep)
        probe_anonymous_rejected(url, open_url=self.open_url, sleep=self.sleep)
        _, after = verify_deployed_revision(self.app_id, self.image, self.arm, runtime_sql)
        if after != revision:
            raise DeploymentBlocked("The active revision changed during verification; release is not verified.")
        self._save("verified")
        log("Release verified: exact image revision, startup database check, liveness and anonymous 401.")
        return revision


def recover_start(controller: ReleaseController, execution: str | None) -> None:
    """Owner-only recovery from an ambiguous start of the same release."""
    record, controller.etag = controller.journal.load()
    if (
        record is None
        or record["phase"] != "start-requested"
        or record["release"] != controller.settings.release.as_dict()
    ):
        raise DeploymentBlocked("No ambiguous start for this release is recorded.")
    controller.dependencies = record["dependencies"]
    if execution is None:
        controller._no_active()
        controller._save("start-rejected")
        log("Recorded owner confirmation that no execution is running; the next release run may start once.")
        return
    if not NAME_RE.fullmatch(execution):
        raise DeploymentBlocked("Execution name is malformed.")
    status, properties = execution_status(controller.job_id, execution, controller.arm)
    if status is None or execution_images(properties) != [controller.image]:
        raise DeploymentBlocked("The named execution does not run this release image.")
    controller.execution = execution
    controller._save("started")
    log("Recorded the owner-confirmed execution; the next release run resumes it.")


def build_controller(env: Mapping[str, str], *, recovery: bool = False) -> ReleaseController:
    # Owner recovery only records evidence about an existing start; it never
    # updates the job, starts, or promotes, so it needs no release manifest.
    settings = load_settings(env, require_manifest=not recovery)
    terraform = TerraformRunner(settings.temp_dir)
    job_id = terraform.output("migration_job_id")
    app_id = terraform.output("container_app_id")
    if (
        not JOB_ID_RE.fullmatch(job_id)
        or not APP_ID_RE.fullmatch(app_id)
        or resource_scope(job_id) != resource_scope(app_id)
    ):
        raise DeploymentBlocked("Trusted Terraform resource IDs are malformed or out of scope.")
    return ReleaseController(
        settings,
        arm=ArmClient(job_id),
        journal=BlobJournal(settings.state_account, settings.state_container, settings.temp_dir),
        terraform=terraform,
        job_id=job_id,
        app_id=app_id,
        recheck=(_recovery_has_no_writes if recovery else (lambda: release_recheck(settings.manifest))),
    )


def _recovery_has_no_writes() -> None:
    raise DeploymentBlocked("Owner recovery cannot perform release cloud writes.")


USAGE = (
    "Usage: deploy_gate.py                     (release: migrate, promote, verify)\n"
    "       deploy_gate.py recover-start (--execution <name> | --no-execution)   (owner only)"
)


def main(argv: list[str] | None = None, env: Mapping[str, str] | None = None) -> int:
    argv = sys.argv[1:] if argv is None else argv
    env = os.environ if env is None else env
    try:
        if not argv:
            require_write_authorization(env)
            build_controller(env).run()
            return 0
        if argv[0] == "recover-start":
            if env.get("BUJO_OWNER_RECOVERY") != "true":
                raise DeploymentBlocked("Start recovery requires BUJO_OWNER_RECOVERY=true set by the owner.")
            if argv[1:] == ["--no-execution"]:
                recover_start(build_controller(env, recovery=True), None)
                return 0
            if len(argv) == 3 and argv[1] == "--execution":
                recover_start(build_controller(env, recovery=True), argv[2])
                return 0
        if argv == ["provision-migration-job"]:
            raise DeploymentBlocked("provision-migration-job was superseded by the unified deploy_gate.py release.")
        print(USAGE, file=sys.stderr)
        return 2
    except DeploymentBlocked as error:
        print(f"Deployment blocked: {error}", file=sys.stderr)
        return 1
    except Exception:  # noqa: BLE001 - never print tracebacks that may contain values
        print("Deployment gate failed unexpectedly; details withheld.", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
