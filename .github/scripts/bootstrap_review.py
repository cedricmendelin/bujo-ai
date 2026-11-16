import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

from check_bootstrap_plan import ALLOWED, validate_plan


ROOT = Path(__file__).resolve().parents[2]
FORBIDDEN_AUTH = (
    "ARM_CLIENT_ID", "ARM_CLIENT_SECRET", "ARM_CLIENT_CERTIFICATE_PATH",
    "ARM_CLIENT_CERTIFICATE_PASSWORD", "ARM_OIDC_TOKEN", "ARM_OIDC_TOKEN_FILE_PATH",
    "ARM_OIDC_REQUEST_TOKEN", "ARM_OIDC_REQUEST_URL", "ARM_USE_OIDC", "ARM_USE_MSI",
    "ARM_USE_AKS_WORKLOAD_IDENTITY", "AZURE_CLIENT_ID", "AZURE_CLIENT_SECRET",
    "AZURE_CLIENT_CERTIFICATE_PATH", "AZURE_FEDERATED_TOKEN_FILE",
    "ACTIONS_ID_TOKEN_REQUEST_URL", "ACTIONS_ID_TOKEN_REQUEST_TOKEN",
    "ARM_ACCESS_KEY", "ARM_SAS_TOKEN",
)

# Only fixed provider attribute names may appear in the shareable output. Values,
# arbitrary map keys, provider diagnostics, and data-source content are omitted.
SAFE_FIELDS = frozenset({
    "location", "name", "resource_group_name", "tags", "identity", "identity_ids",
    "type", "template", "container", "image", "command", "args", "env", "cpu",
    "memory", "manual_trigger_config", "event_trigger_config", "schedule_trigger_config",
    "replica_timeout_in_seconds", "replica_retry_limit", "parallelism",
    "replica_completion_count", "container_app_environment_id", "server_id", "body",
    "sku", "sku_name", "properties", "administrator_login", "azuread_administrator",
    "minimum_tls_version", "public_network_access_enabled", "retention_in_days",
    "daily_quota_gb", "start_ip_address", "end_ip_address",
})
PROVIDERS = frozenset({
    "registry.terraform.io/azure/azapi", "registry.terraform.io/azure/modtm",
    "registry.terraform.io/hashicorp/azuread", "registry.terraform.io/hashicorp/azurerm",
    "registry.terraform.io/hashicorp/random", "registry.terraform.io/hashicorp/time",
})
INFRASTRUCTURE_PLAN_SHA256 = "ba70852c7ab9673a51014e136165e540d01fa6586b3da79506dfee617738542a"


class CommandFailure(ValueError):
    def __init__(self, category, message):
        super().__init__(message)
        self.category = category


def command_failure_category(stderr):
    status = re.search(r"\b(?:GitHub API|HTTP) (401|403|404|429|5\d\d)\b", stderr)
    if status:
        return f"github-api-http-{status[1]}"
    return {
        "The plan approval or binding label is missing.": "approval-labels-missing",
        "No active trusted approval record.": "approval-record-missing",
        "The approval record does not match the current owner label event.": "approval-event-mismatch",
        "The approved plan no longer matches the latest plan.": "approval-plan-mismatch",
        "The plan was changed at or after the approval event.": "approval-plan-edited",
        "GH_TOKEN, GITHUB_REPOSITORY, and a valid issue number are required.": "approval-configuration",
        "fetch failed": "github-api-network",
    }.get(stderr.strip(), "command-failed")


def check_auth(account, expected_subscription, env):
    if any(env.get(name) for name in FORBIDDEN_AUTH):
        raise ValueError("Service-principal, managed-identity or OIDC environment variables must be unset.")
    if env.get("ARM_USE_CLI", "true").lower() != "true":
        raise ValueError("Azure CLI authentication must be enabled.")
    if env.get("ARM_USE_AZUREAD", "true").lower() != "true":
        raise ValueError("The backend must use Azure AD authentication.")
    if not isinstance(account, dict) or not isinstance(account.get("user"), dict) or account["user"].get("type") != "user":
        raise ValueError("Azure CLI must be signed in as an interactive user.")
    if not expected_subscription or not isinstance(account.get("id"), str) or account["id"].lower() != expected_subscription.lower():
        raise ValueError("The selected Azure subscription does not match the expected subscription.")
    if env.get("ARM_SUBSCRIPTION_ID", "").lower() != expected_subscription.lower():
        raise ValueError("ARM_SUBSCRIPTION_ID does not match the expected subscription.")
    if not isinstance(account.get("tenantId"), str) or not account["tenantId"] or env.get("ARM_TENANT_ID", "").lower() != account["tenantId"].lower():
        raise ValueError("ARM_TENANT_ID does not match the Azure CLI account.")


def check_oidc_auth(account, expected_subscription, env):
    forbidden = (
        "ARM_CLIENT_SECRET", "AZURE_CLIENT_SECRET", "ARM_CLIENT_CERTIFICATE_PATH",
        "ARM_CLIENT_CERTIFICATE_PASSWORD", "AZURE_CLIENT_CERTIFICATE_PATH",
        "ARM_ACCESS_KEY", "ARM_SAS_TOKEN",
    )
    if any(env.get(name) for name in forbidden):
        raise ValueError("OIDC review must not use persistent credentials or storage keys.")
    if (
        env.get("ARM_USE_OIDC") != "true" or env.get("ARM_USE_AZUREAD") != "true"
        or env.get("ARM_USE_CLI") != "false"
        or any(env.get(name, "false").lower() != "false" for name in (
            "ARM_USE_MSI", "ARM_USE_AKS_WORKLOAD_IDENTITY",
        ))
        or not env.get("ARM_CLIENT_ID")
        or not isinstance(account, dict)
        or account.get("user", {}).get("type") != "servicePrincipal"
        or account.get("user", {}).get("name", "").casefold() != env["ARM_CLIENT_ID"].casefold()
        or not expected_subscription
        or account.get("id", "").casefold() != expected_subscription.casefold()
        or env.get("ARM_SUBSCRIPTION_ID", "").casefold() != expected_subscription.casefold()
        or not account.get("tenantId")
        or env.get("ARM_TENANT_ID", "").casefold() != account["tenantId"].casefold()
    ):
        raise ValueError("OIDC review requires the exact configured principal, subscription and tenant.")


def safe_fields(value):
    if isinstance(value, dict):
        return {
            key: safe_fields(value[key])
            for key in sorted(value)
            if key in SAFE_FIELDS
        } or "<redacted>"
    if isinstance(value, list):
        return [safe_fields(item) for item in value]
    return "<redacted>"


def allowed_resources(mode):
    if mode == "infrastructure":
        from check_bootstrap_plan import BASELINE_ALLOWED
        return BASELINE_ALLOWED
    return ALLOWED


def check_plan_subscription(plan, expected_subscription, mode="job"):
    seen = set()

    def scan(value):
        if isinstance(value, dict):
            for item in value.values():
                scan(item)
        elif isinstance(value, list):
            for item in value:
                scan(item)
        elif isinstance(value, str):
            seen.update(match.lower() for match in re.findall(
                r"(?i)/subscriptions/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})(?=/|$)",
                value,
            ))

    for resource in plan.get("resource_changes", []):
        if resource.get("mode") == "managed" and resource.get("address") in allowed_resources(mode):
            change = resource.get("change") or {}
            scan(change.get("before"))
            scan(change.get("after"))
    if not seen or seen != {expected_subscription.lower()}:
        raise ValueError("The plan does not prove that its resource IDs belong to the selected subscription.")


def redact_terraform_show(text, mode="job"):
    lines = []
    current = None
    for line in text.splitlines():
        header = re.match(r"^\s*# (\S+)(?:\s|$)", line)
        if header:
            current = header.group(1) if header.group(1) in allowed_resources(mode) else None
            lines.append(f"# {current}" if current else "<redacted>")
            continue
        if current:
            field = re.match(r"^\s*([+~-]?)\s*([a-z][a-z0-9_]*)\s*=", line)
            if field and field.group(2) in SAFE_FIELDS:
                lines.append(f"  {field.group(1)} {field.group(2)} = <redacted>")
                continue
        lines.append("<redacted>")
    return lines


def review_package(plan, plan_hash, terraform_version, provider_versions, subscription_name,
                   show_text="", *, mode="job", evidence=None):
    if mode == "infrastructure":
        from check_bootstrap_plan import validate_baseline_plan
        inventory = validate_baseline_plan(plan, **evidence)
    else:
        inventory = validate_plan(plan)
    if not isinstance(subscription_name, str) or not subscription_name.strip():
        raise ValueError("The Azure subscription name is missing.")
    if terraform_version != "1.17.0":
        raise ValueError("Terraform 1.17.0 is required.")
    if not isinstance(provider_versions, dict) or set(provider_versions) != PROVIDERS or any(
        not isinstance(version, str) or not re.fullmatch(r"\d+\.\d+\.\d+", version)
        for version in provider_versions.values()
    ):
        raise ValueError("Provider versions are missing or unexpected.")
    changes = {resource["address"]: resource["change"] for resource in plan["resource_changes"]}
    reviewed = []
    for address, action in inventory:
        change = changes[address]
        reviewed.append({
            "address": address,
            "action": action,
            "before": safe_fields(change.get("before")),
            "after": safe_fields(change.get("after")),
            "after_unknown": safe_fields(change.get("after_unknown")),
        })
    return {
        "plan_sha256": plan_hash,
        "terraform_version": terraform_version,
        "provider_versions": provider_versions,
        "subscription": "Confirmed privately against the Azure CLI account.",
        "changes": reviewed,
        "redacted_terraform_show": redact_terraform_show(show_text, mode),
        "redaction": "All attribute values and unrecognized keys are hidden. Review the complete saved plan privately.",
        "cost": "Review SQL free-offer eligibility and limits, Container Apps job consumption and Log Analytics ingestion.",
        "prerequisites": "Review Azure permissions, private state access, the target subscription, and the owner SQL grant steps.",
    }


def run_text(args, *, input_text=None, private_log=None):
    executable = shutil.which(args[0])
    if not executable:
        raise CommandFailure("command-unavailable", f"Required executable is unavailable: {args[0]}")
    try:
        result = subprocess.run(
            [executable, *args[1:]], cwd=ROOT, capture_output=True, text=True,
            encoding="utf-8", input=input_text, timeout=1800,
        )
    except subprocess.TimeoutExpired as error:
        if private_log:
            parts = [
                value.decode("utf-8", errors="replace") if isinstance(value, bytes) else value or ""
                for value in (error.stdout, error.stderr)
            ]
            private_log.write_text("\n".join(parts), encoding="utf-8")
        raise CommandFailure("command-timeout", f"{args[0]} timed out; inspect the operation privately before retrying.") from error
    if private_log:
        private_log.write_text(result.stdout + "\n" + result.stderr, encoding="utf-8")
    if result.returncode:
        raise CommandFailure(
            command_failure_category(result.stderr),
            f"{args[0]} failed; inspect it privately before retrying.",
        )
    return result.stdout


def run_json(args):
    return json.loads(run_text(args))


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def check_checkout(expected_commit=None):
    if run_text(["git", "status", "--porcelain", "--untracked-files=all"]).strip():
        raise ValueError("Bootstrap review requires a clean committed checkout.")
    commit = run_text(["git", "rev-parse", "HEAD"]).strip()
    if not re.fullmatch(r"[a-f0-9]{40}", commit) or (
        expected_commit is not None and commit != expected_commit
    ):
        raise ValueError("Bootstrap source commit changed or does not match the reviewed commit.")
    return commit


def check_workflow_context(env):
    if not env.get("GITHUB_ACTIONS"):
        return
    if (
        env.get("GITHUB_REPOSITORY") != "cedricmendelin/bujo-ai"
        or env.get("GITHUB_EVENT_NAME") != "workflow_dispatch"
        or env.get("GITHUB_REF") != "refs/heads/main"
        or env.get("GITHUB_ACTOR") != env.get("GITHUB_REPOSITORY_OWNER")
        or env.get("GITHUB_TRIGGERING_ACTOR") != env.get("GITHUB_REPOSITORY_OWNER")
    ):
        raise ValueError("Infrastructure provisioning requires an owner dispatch on main.")


def check_infrastructure_approval():
    if run_text(["git", "diff", "origin/main", "--", ".github/scripts/plan-approval.js"]).strip():
        raise ValueError("Approval verification must use the trusted default-branch script.")
    try:
        body = run_text(["node", ".github/scripts/plan-approval.js", "verify", "--issue", "5"])
    except CommandFailure as error:
        raise ValueError(
            f"Infrastructure approval verification could not complete [{error.category}]; "
            "provisioning is blocked. Inspect this category before changing approval or permissions."
        ) from error
    except ValueError as error:
        raise ValueError("Infrastructure approval verification could not complete [approval-verifier]; provisioning is blocked.") from error
    digest = hashlib.sha256(body.replace("\r\n", "\n").strip().encode("utf-8")).hexdigest()
    if digest != INFRASTRUCTURE_PLAN_SHA256:
        raise ValueError("The live approved scope is not this infrastructure-first plan v9.")


def check_current_main(source_commit):
    current = run_text(["gh", "api", "repos/cedricmendelin/bujo-ai/commits/main", "--jq", ".sha"]).strip()
    if current != source_commit:
        raise ValueError("Infrastructure provisioning requires the current merged main commit; re-plan after source changes.")


def check_release_quiescent(temp_dir):
    sys.path.insert(0, str(ROOT / "infra"))
    from deploy_gate import BlobJournal, DeploymentBlocked
    try:
        record, _etag = BlobJournal(
            os.environ["TFSTATE_STORAGE_ACCOUNT"], os.environ["TFSTATE_CONTAINER"], temp_dir,
        ).load()
    except (DeploymentBlocked, subprocess.TimeoutExpired) as error:
        raise ValueError("The release journal could not be verified before infrastructure changes.") from error
    if record and record["phase"] not in ("verified", "failed", "start-rejected"):
        raise ValueError("An unfinished or ambiguous application release blocks infrastructure changes; reconcile it first.")


def check_private_backend(path=None):
    metadata = json.loads((path or ROOT / "infra" / ".terraform" / "terraform.tfstate").read_text(encoding="utf-8"))
    backend = metadata.get("backend") or {}
    config = backend.get("config") or {}
    expected = {
        "resource_group_name": "TFSTATE_RESOURCE_GROUP",
        "storage_account_name": "TFSTATE_STORAGE_ACCOUNT",
        "container_name": "TFSTATE_CONTAINER", "key": "TFSTATE_KEY",
    }
    if backend.get("type") != "azurerm" or config.get("use_azuread_auth") is not True or any(
        not os.environ.get(setting) or config.get(field) != os.environ[setting]
        for field, setting in expected.items()
    ) or any(config.get(name) for name in ("access_key", "sas_token", "client_secret", "client_certificate_path")):
        raise ValueError("The initialized Entra backend must match the configured private state target without persistent credentials.")
    private_plan_blob("1", "plan.tfplan")
    if os.environ["TFSTATE_KEY"].casefold() == "deployments/bujo-ai-prod-release.json":
        raise ValueError("Terraform state must not overlap the application release journal.")


def check_private_storage():
    from deployment_preflight import require_private_storage
    try:
        require_private_storage(os.environ)
    except subprocess.TimeoutExpired as error:
        raise ValueError("Private storage inspection timed out; access and privacy remain unverified.") from error


def baseline_evidence():
    client_id = os.environ.get("TF_VAR_deployment_client_id", "")
    if not re.fullmatch(r"[a-fA-F0-9-]{36}", client_id):
        raise ValueError("Configure the intended deployment client ID privately before provisioning.")
    group = os.environ.get("TF_VAR_resource_group_name", "")
    if not group:
        raise ValueError("Configure the existing infrastructure resource group privately.")
    try:
        apps = run_json(["az", "containerapp", "list", "--resource-group", group, "--output", "json"])
    except ValueError as error:
        raise ValueError("The owner must verify the configured application resource group and deployment-principal Azure access.") from error
    if not isinstance(apps, list) or len(apps) != 1:
        raise ValueError("The configured group must identify exactly one existing app; no app or image is guessed.")
    app = apps[0]
    containers = app.get("properties", {}).get("template", {}).get("containers", [])
    if len(containers) != 1 or not isinstance(containers[0].get("image"), str) or not containers[0]["image"]:
        raise ValueError("The existing application must have exactly one known image; no placeholder is substituted.")
    try:
        owner = run_json(["az", "ad", "sp", "show", "--id", client_id, "--query", "id", "--output", "json"])
    except ValueError as error:
        raise ValueError("The tenant administrator must verify the deployment client ID and its scoped Graph access; do not broaden to .All.") from error
    if not isinstance(owner, str) or not re.fullmatch(r"[a-fA-F0-9-]{36}", owner):
        raise ValueError("The intended deployment service principal could not be resolved.")
    expected_prefix = (
        f"/subscriptions/{os.environ['ARM_SUBSCRIPTION_ID']}/resourceGroups/"
        f"{group}/providers/Microsoft.App/containerApps/"
    )
    if not app.get("id", "").casefold().startswith(expected_prefix.casefold()):
        raise ValueError("The existing application is outside the selected infrastructure target.")
    jobs = run_json(["az", "containerapp", "job", "list", "--resource-group", group, "--output", "json"])
    if not isinstance(jobs, list):
        raise ValueError("Migration job inventory is unavailable.")
    for job in jobs:
        if not isinstance(job.get("name"), str) or not job["name"]:
            raise ValueError("Job inventory contains an indeterminate target.")
        executions = run_json([
            "az", "containerapp", "job", "execution", "list",
            "--resource-group", group, "--name", job["name"], "--output", "json",
        ])
        if not isinstance(executions, list) or any(
            item.get("properties", {}).get("status") not in ("Succeeded", "Failed", "Stopped")
            for item in executions
        ):
            raise ValueError("An active or indeterminate migration execution blocks infrastructure changes.")
    return {
        "expected_image": containers[0]["image"], "expected_owner": owner,
        "expected_app_id": app["id"],
    }


def private_plan_blob(run_id, filename):
    if not re.fullmatch(r"[1-9][0-9]*", run_id or ""):
        raise ValueError("A valid infrastructure plan run ID is required.")
    if filename not in ("plan.tfplan", "review.json"):
        raise ValueError("Unexpected private plan file.")
    blob = f"bootstrap/plans/{run_id}/{filename}"
    state_key = os.environ.get("TFSTATE_KEY", "")
    if not state_key or state_key.replace("\\", "/").lstrip("/").casefold().startswith("bootstrap/"):
        raise ValueError("Private plan storage must not overlap Terraform state.")
    return blob


def transfer_plan(operation, run_id, filename, path):
    account = os.environ.get("TFSTATE_STORAGE_ACCOUNT", "")
    container = os.environ.get("TFSTATE_CONTAINER", "")
    if not account or not container:
        raise ValueError("Configure the existing private state storage account and container.")
    run_text([
        "az", "storage", "blob", operation, "--auth-mode", "login",
        "--account-name", account, "--container-name", container,
        "--name", private_plan_blob(run_id, filename), "--file", str(path),
        "--overwrite", "false", "--only-show-errors", "--output", "none",
    ])


def download_reviewed_plan(args, source_commit):
    if not os.environ.get("GITHUB_ACTIONS"):
        raise ValueError("Remote saved-plan recovery is available only in the trusted owner workflow.")
    if not args.expected_sha256 or not re.fullmatch(r"[a-fA-F0-9]{64}", args.expected_sha256):
        raise ValueError("Owner application requires the exact privately reviewed plan SHA-256.")
    run_id = args.plan_run_id
    private_plan_blob(run_id, "plan.tfplan")
    previous = run_json([
        "gh", "api", f"repos/cedricmendelin/bujo-ai/actions/runs/{run_id}",
    ])
    if (
        previous.get("event") != "workflow_dispatch"
        or previous.get("path") != ".github/workflows/provision.yml"
        or previous.get("head_branch") != "main"
        or previous.get("head_sha") != source_commit
        or previous.get("repository", {}).get("full_name") != "cedricmendelin/bujo-ai"
        or previous.get("conclusion") != "success"
        or previous.get("status") != "completed"
        or previous.get("actor", {}).get("login") != os.environ.get("GITHUB_REPOSITORY_OWNER")
        or previous.get("triggering_actor", {}).get("login") != os.environ.get("GITHUB_REPOSITORY_OWNER")
    ):
        raise ValueError("The saved plan must originate from a successful owner provisioning run for this exact main commit.")
    metadata_path = args.plan.with_suffix(".review.json")
    transfer_plan("download", run_id, "review.json", metadata_path)
    if metadata_path.stat().st_size > 1048576:
        raise ValueError("The private plan review metadata exceeds its size limit.")
    metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
    if (
        metadata.get("purpose") != "infrastructure-plan"
        or metadata.get("source_commit") != source_commit
        or metadata.get("plan_sha256") != args.expected_sha256.lower()
        or metadata.get("plan_run_id") != run_id
        or metadata.get("plan_run_attempt") != str(previous.get("run_attempt"))
    ):
        raise ValueError("The private plan metadata differs from the owner-reviewed source and hash.")
    transfer_plan("download", run_id, "plan.tfplan", args.plan)


def infrastructure_main(args, account):
    check_workflow_context(os.environ)
    check_infrastructure_approval()
    source_commit = check_checkout(args.expected_commit)
    check_current_main(source_commit)
    check_private_backend()
    check_private_storage()
    check_release_quiescent(args.plan.parent)
    if args.operation == "apply":
        if not args.expected_sha256 or not args.expected_commit:
            raise ValueError("Apply requires the owner-reviewed plan hash and exact source commit.")
        if args.plan_run_id:
            download_reviewed_plan(args, source_commit)
    evidence = baseline_evidence()
    if args.operation == "plan":
        if args.plan.exists():
            raise ValueError("Choose a new private plan path; existing plans are not overwritten.")
        run_text([
            "terraform", "-chdir=infra", "plan", "-input=false", "-lock-timeout=5m",
            "-var-file=prod.tfvars", "-var=bootstrap_only=true",
            f"-var=container_image={evidence['expected_image']}", f"-out={args.plan}",
        ], private_log=args.plan.with_suffix(".plan.log"))
    digest = sha256(args.plan)
    if args.expected_sha256 and digest != args.expected_sha256.lower():
        raise ValueError("The saved plan has changed since owner review.")
    version = run_json(["terraform", "-chdir=infra", "version", "-json"])
    plan = run_json(["terraform", "-chdir=infra", "show", "-json", str(args.plan)])
    if plan.get("variables", {}).get("deployment_client_id", {}).get("value") != os.environ.get("TF_VAR_deployment_client_id"):
        raise ValueError("The saved plan targets a different deployment principal.")
    check_plan_subscription(plan, args.subscription, "infrastructure")
    package = review_package(
        plan, digest, version.get("terraform_version"), version.get("provider_selections"),
        account.get("name"), mode="infrastructure", evidence=evidence,
    )
    check_checkout(source_commit)
    if sha256(args.plan) != digest:
        raise ValueError("The saved infrastructure plan changed during review.")
    package.update(source_commit=source_commit, purpose="infrastructure-plan")
    if args.publish_plan:
        if args.operation != "plan" or not os.environ.get("GITHUB_ACTIONS"):
            raise ValueError("Only a trusted workflow plan operation may publish a private plan.")
        check_infrastructure_approval()
        check_current_main(source_commit)
        check_private_storage()
        run_id = os.environ.get("GITHUB_RUN_ID", "")
        package.update(plan_run_id=run_id, plan_run_attempt=os.environ.get("GITHUB_RUN_ATTEMPT", ""))
        metadata_path = args.plan.with_suffix(".review.json")
        metadata_path.write_text(json.dumps(package), encoding="utf-8")
        transfer_plan("upload", run_id, "plan.tfplan", args.plan)
        transfer_plan("upload", run_id, "review.json", metadata_path)
        package["private_review"] = "Read the saved plan privately from the existing state container using this workflow run ID."
    print(json.dumps(package, indent=2))
    if args.operation == "apply":
        check_release_quiescent(args.plan.parent)
        if baseline_evidence() != evidence:
            raise ValueError("The existing image, application or migration admission changed before apply.")
        check_checkout(source_commit)
        if sha256(args.plan) != digest:
            raise ValueError("The saved plan changed immediately before apply.")
        check_infrastructure_approval()
        check_current_main(source_commit)
        check_private_backend()
        check_private_storage()
        try:
            run_text(
                ["terraform", "-chdir=infra", "apply", "-input=false", "-lock-timeout=5m", str(args.plan)],
                private_log=args.plan.with_suffix(".apply.log"),
            )
        except ValueError as error:
            raise ValueError(
                "Infrastructure apply failed; some resources may exist. Inspect the private plan-adjacent .apply.log and Terraform state "
                "and re-plan; application failure is not infrastructure success."
            ) from error
        print("Infrastructure apply succeeded. Application readiness is not certified; complete the owner checklist in #55.")


def main():
    parser = argparse.ArgumentParser(description="Review or apply an explicitly owner-reviewed Terraform bootstrap plan.")
    parser.add_argument("--plan", type=Path, required=True)
    parser.add_argument("--subscription", required=True, help="Expected Azure subscription ID; never share the command output without review.")
    parser.add_argument("--expected-sha256", help="Previously reviewed plan hash; required before apply.")
    parser.add_argument("--expected-commit", help="Previously reviewed clean source commit; required before apply.")
    parser.add_argument("--auth-mode", choices=("local-user", "oidc"), default="local-user")
    parser.add_argument("--mode", choices=("job", "infrastructure"), default="job")
    parser.add_argument("--operation", choices=("review", "plan", "apply"), default="review")
    parser.add_argument("--publish-plan", action="store_true")
    parser.add_argument("--plan-run-id", help="Successful owner workflow plan to recover privately before apply.")
    args = parser.parse_args()
    try:
        plan_path = args.plan.resolve()
        if plan_path.is_relative_to(ROOT):
            raise ValueError("The plan must be stored outside the repository.")
        args.plan = plan_path
        account = run_json(["az", "account", "show", "--output", "json"])
        auth_check = check_oidc_auth if args.auth_mode == "oidc" else check_auth
        auth_check(account, args.subscription, os.environ)
        if args.mode == "infrastructure":
            infrastructure_main(args, account)
            return 0
        if args.operation != "review" or args.publish_plan or args.plan_run_id:
            raise ValueError("The job-only mode supports review only; applying requires the infrastructure path.")
        digest = sha256(plan_path)
        if args.expected_sha256 and not re.fullmatch(r"[a-fA-F0-9]{64}", args.expected_sha256):
            raise ValueError("The expected plan hash is invalid.")
        if args.expected_sha256 and digest.lower() != args.expected_sha256.lower():
            raise ValueError("The saved plan has changed since review.")
        source_commit = check_checkout(args.expected_commit)
        version = run_json(["terraform", "-chdir=infra", "version", "-json"])
        plan = run_json(["terraform", "-chdir=infra", "show", "-json", str(plan_path)])
        check_plan_subscription(plan, args.subscription)
        show_text = run_text(["terraform", "-chdir=infra", "show", "-no-color", str(plan_path)])
        if sha256(plan_path) != digest:
            raise ValueError("The saved plan changed while it was being reviewed.")
        check_checkout(source_commit)
        package = review_package(plan, digest, version.get("terraform_version"), version.get("provider_selections"), account.get("name"), show_text)
        package["source_commit"] = source_commit
        print(json.dumps(package, indent=2))
    except (OSError, ValueError, KeyError, TypeError, json.JSONDecodeError, subprocess.TimeoutExpired) as error:
        print(f"Bootstrap review failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
