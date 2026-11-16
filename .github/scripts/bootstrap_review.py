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


def check_plan_subscription(plan, expected_subscription):
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
        if resource.get("mode") == "managed" and resource.get("address") in ALLOWED:
            change = resource.get("change") or {}
            scan(change.get("before"))
            scan(change.get("after"))
    if not seen or seen != {expected_subscription.lower()}:
        raise ValueError("The plan does not prove that its resource IDs belong to the selected subscription.")


def redact_terraform_show(text):
    lines = []
    current = None
    for line in text.splitlines():
        header = re.match(r"^\s*# (\S+)(?:\s|$)", line)
        if header:
            current = header.group(1) if header.group(1) in ALLOWED else None
            lines.append(f"# {current}" if current else "<redacted>")
            continue
        if current:
            field = re.match(r"^\s*([+~-]?)\s*([a-z][a-z0-9_]*)\s*=", line)
            if field and field.group(2) in SAFE_FIELDS:
                lines.append(f"  {field.group(1)} {field.group(2)} = <redacted>")
                continue
        lines.append("<redacted>")
    return lines


def review_package(plan, plan_hash, terraform_version, provider_versions, subscription_name, show_text=""):
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
        "redacted_terraform_show": redact_terraform_show(show_text),
        "redaction": "All attribute values and unrecognized keys are hidden. Review the complete saved plan privately.",
        "cost": "Review SQL free-offer eligibility and limits, Container Apps job consumption and Log Analytics ingestion.",
        "prerequisites": "Review Azure permissions, private state access, the target subscription, and the owner SQL grant steps.",
    }


def run_text(args):
    executable = shutil.which(args[0])
    if not executable:
        raise ValueError(f"Required executable is unavailable: {args[0]}")
    result = subprocess.run([executable, *args[1:]], cwd=ROOT, capture_output=True, text=True, encoding="utf-8")
    if result.returncode:
        raise ValueError(f"{args[0]} failed; inspect it privately before retrying.")
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


def main():
    parser = argparse.ArgumentParser(description="Review a saved stage-one Terraform plan privately.")
    parser.add_argument("--plan", type=Path, required=True)
    parser.add_argument("--subscription", required=True, help="Expected Azure subscription ID; never share the command output without review.")
    parser.add_argument("--expected-sha256", help="Previously reviewed plan hash; required before apply.")
    parser.add_argument("--expected-commit", help="Previously reviewed clean source commit; required before apply.")
    parser.add_argument("--auth-mode", choices=("local-user", "oidc"), default="local-user")
    args = parser.parse_args()
    try:
        plan_path = args.plan.resolve(strict=True)
        if plan_path.is_relative_to(ROOT):
            raise ValueError("The plan must be stored outside the repository.")
        account = run_json(["az", "account", "show", "--output", "json"])
        auth_check = check_oidc_auth if args.auth_mode == "oidc" else check_auth
        auth_check(account, args.subscription, os.environ)
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
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print(f"Bootstrap review failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
