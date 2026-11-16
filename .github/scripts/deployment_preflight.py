"""Value-redacted deployment prerequisites; no cloud writes or SQL grants."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import urllib.error
import urllib.request


SETTINGS = (
    "AZURE_CLIENT_ID", "AZURE_TENANT_ID", "AZURE_SUBSCRIPTION_ID",
    "AZURE_RESOURCE_GROUP", "TFSTATE_RESOURCE_GROUP", "TFSTATE_STORAGE_ACCOUNT",
    "TFSTATE_CONTAINER", "TFSTATE_KEY", "SQL_ENTRA_ADMIN_OBJECT_ID",
    "SQL_ENTRA_ADMIN_LOGIN", "BUDGET_ALERT_EMAIL", "API_ALLOWED_PRINCIPAL_OBJECT_IDS",
)

EXTERNAL_CHECKS = {
    "OIDC subject and resource-group/state-container access": (
        "repository owner / Azure administrator",
        "Verify deployment-principal prod OIDC subject, resource-group Contributor and state-container Blob Data Contributor.",
    ),
    "Entra application ownership and admin consent": (
        "tenant administrator",
        "Verify deployment-principal Application.ReadWrite.OwnedBy consent and ownership of the managed API application/service principal.",
    ),
    "runtime and migration SQL principals and grants": (
        "SQL Entra administrator",
        "Grant runtime reader/writer and separate migration reader/writer plus required DDL; remove temporary network access.",
    ),
    "GHCR public image pull": (
        "package owner",
        "Make the exact published package anonymously pullable; do not add a registry password workaround.",
    ),
    "bootstrap job and migration identity": (
        "repository owner",
        "Review costs and personally apply the exact saved, hash-bound inert bootstrap plan; resolve B6 first.",
    ),
    "delegated client consent and API assignment": (
        "tenant administrator / approved user",
        "Configure the approved caller assignment and delegated journal.access consent; verify the private API account allowlist.",
    ),
    "SQL free-offer eligibility and monthly cost": (
        "subscription owner",
        "Review SQL free-offer limits, availability behavior and deployment-resource costs before activation.",
    ),
}


def report(env, live_checks=None):
    results = [
        {"requirement": name, "status": "verified" if env.get(name, "").strip() else "missing",
         "evidence": "variable present" if env.get(name, "").strip() else "variable absent",
         "responsible": "repository owner",
         "action": f"Configure {name} privately in the prod environment."}
        for name in SETTINGS
    ]
    owner_confirmed = env.get("BUJO_OWNER_SETUP_CONFIRMED") == "true"
    results.extend({
        "requirement": name,
        "status": "verified" if owner_confirmed else "unknown",
        "evidence": "owner-confirmed checklist #55; not an independent permission test"
        if owner_confirmed else "owner verification and BUJO_OWNER_SETUP_CONFIRMED=true required",
        "responsible": owner, "action": action,
    } for name, (owner, action) in EXTERNAL_CHECKS.items())
    results.append({
        "requirement": "automatic deployment activation",
        "status": "verified" if env.get("BUJO_DEPLOYMENT_ENABLED") == "true" else "missing",
        "evidence": "owner activation flag" if env.get("BUJO_DEPLOYMENT_ENABLED") == "true"
        else "owner must set BUJO_DEPLOYMENT_ENABLED=true after setup",
    })
    if live_checks is not None:
        results.extend(live_checks)
    return {
        "checks": results,
        "remediation": "Owner/admin: https://github.com/cedricmendelin/bujo-ai/issues/55",
        "ready": all(item["status"] == "verified" for item in results),
    }


def run_json(args):
    executable = shutil.which(args[0])
    if not executable:
        raise ValueError("A prerequisite inspection tool is unavailable.")
    result = subprocess.run(
        [executable, *args[1:]], capture_output=True, text=True, check=False, timeout=120,
    )
    if result.returncode:
        raise ValueError("Read-only prerequisite inspection failed.")
    return json.loads(result.stdout)


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def verify_public_image(manifest, open_url=None):
    image = manifest.get("image", "")
    if not re.fullmatch(r"ghcr\.io/cedricmendelin/bujo-ai@sha256:[0-9a-f]{64}", image):
        raise ValueError("Invalid release image.")
    open_url = open_url or urllib.request.build_opener(NoRedirect).open
    with open_url(
        "https://ghcr.io/token?service=ghcr.io&scope=repository:cedricmendelin/bujo-ai:pull",
        timeout=30,
    ) as response:
        token = json.loads(response.read(65536)).get("token")
    if not isinstance(token, str) or not token:
        raise ValueError("Anonymous image access was not granted.")
    def read_manifest(digest):
        if not re.fullmatch(r"sha256:[0-9a-f]{64}", digest):
            raise ValueError("Invalid registry manifest digest.")
        request = urllib.request.Request(
            f"https://ghcr.io/v2/cedricmendelin/bujo-ai/manifests/{digest}",
            headers={
                "Authorization": f"Bearer {token}",
                "Accept": ", ".join((
                    "application/vnd.oci.image.manifest.v1+json",
                    "application/vnd.docker.distribution.manifest.v2+json",
                    "application/vnd.oci.image.index.v1+json",
                    "application/vnd.docker.distribution.manifest.list.v2+json",
                )),
            },
        )
        with open_url(request, timeout=30) as response:
            body = response.read(1048577)
        if len(body) > 1048576 or f"sha256:{hashlib.sha256(body).hexdigest()}" != digest:
            raise ValueError("Registry image digest does not match the admitted release.")
        return json.loads(body)

    registry_manifest = read_manifest(image.split("@")[1])
    if "manifests" in registry_manifest:
        candidates = [
            item for item in registry_manifest["manifests"]
            if item.get("platform", {}).get("os") == "linux"
            and item.get("platform", {}).get("architecture") == "amd64"
        ]
        if len(candidates) != 1:
            raise ValueError("Image index must identify exactly one CI-tested Linux x64 image.")
        registry_manifest = read_manifest(candidates[0]["digest"])
    if registry_manifest.get("config", {}).get("digest") != manifest.get("config_digest"):
        raise ValueError("Registry image is not the CI-tested container configuration.")


def inspect_live(env, manifest):
    checks = []

    def check(name, operation):
        try:
            operation()
            checks.append({"requirement": name, "status": "verified", "evidence": "read-only live check"})
        except (OSError, ValueError, KeyError, TypeError, urllib.error.URLError, TimeoutError, subprocess.TimeoutExpired):
            checks.append({
                "requirement": name, "status": "unknown",
                "evidence": "inspection failed or requirement missing; owner/admin must inspect privately",
            })

    def account():
        value = run_json(["az", "account", "show", "--output", "json"])
        if (
            value.get("id") != env.get("AZURE_SUBSCRIPTION_ID")
            or value.get("tenantId") != env.get("AZURE_TENANT_ID")
            or value.get("user", {}).get("type") != "servicePrincipal"
            or value.get("user", {}).get("name", "").casefold() != env.get("AZURE_CLIENT_ID", "").casefold()
        ):
            raise ValueError("OIDC account does not match configured deployment target.")

    def bootstrap():
        outputs = run_json(["terraform", "-chdir=infra", "output", "-json"])
        job_id = outputs["migration_job_id"]["value"]
        principal_id = outputs["migration_identity_principal_id"]["value"]
        runtime_principal_id = outputs["runtime_identity_principal_id"]["value"]
        expected_prefix = (
            f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/"
            f"{env['AZURE_RESOURCE_GROUP']}/providers/Microsoft.App/jobs/"
        )
        if not isinstance(job_id, str) or not job_id.casefold().startswith(expected_prefix.casefold()):
            raise ValueError("Unexpected migration job target.")
        job = run_json(["az", "resource", "show", "--ids", job_id, "--output", "json"])
        identities = job.get("identity", {}).get("userAssignedIdentities", {})
        config = job.get("properties", {}).get("configuration", {})
        if (
            job.get("id", "").casefold() != job_id.casefold()
            or job.get("properties", {}).get("provisioningState") != "Succeeded"
            or config.get("triggerType") != "Manual"
            or config.get("replicaRetryLimit") != 0
            or principal_id == runtime_principal_id
            or len(identities) != 1
            or not any(value.get("principalId") == principal_id for value in identities.values())
        ):
            raise ValueError("Bootstrap job or identity is not ready.")

    check("actual deployment target and OIDC session", account)
    check("actual bootstrap job, identity and state outputs", bootstrap)
    check("anonymous pull of exact CI-tested image digest", lambda: verify_public_image(manifest))
    return checks


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--live", action="store_true")
    parser.add_argument("--manifest", type=Path)
    args = parser.parse_args()
    try:
        if args.live and args.manifest is None:
            raise ValueError("A trusted release manifest is required for live checks.")
        live_checks = inspect_live(
            os.environ, json.loads(args.manifest.read_text(encoding="utf-8"))
        ) if args.live else None
        result = report(os.environ, live_checks)
    except (OSError, ValueError, KeyError, TypeError):
        print("Preflight failed: verify private configuration and the trusted release manifest.", file=sys.stderr)
        return 1
    print(json.dumps(result, indent=2))
    return 0 if result["ready"] else 1


if __name__ == "__main__":
    sys.exit(main())
