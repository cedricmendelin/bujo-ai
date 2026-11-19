"""Apply the merged demo infrastructure without promoting an application release."""

import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile


class InfrastructureError(RuntimeError):
    pass


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


def redact_output(text):
    for key in REDACT_ENVIRONMENT_KEYS:
        value = os.environ.get(key)
        if value:
            text = text.replace(value, "[REDACTED]")
    return text


def command(label, args, *, env=None, visible=False):
    result = subprocess.run(
        args,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        env=env,
        timeout=1800,
    )
    output = redact_output(result.stdout or "")
    if visible:
        if output:
            print(output, end="" if output.endswith("\n") else "\n", flush=True)
    if result.returncode:
        raise InfrastructureError(f"{label} failed (exit {result.returncode}).")
    return result.stdout


def az(*args):
    return json.loads(command("Azure CLI", ["az", *args, "--only-show-errors", "--output", "json"]))


def retained_images():
    outputs = json.loads(command("Read Terraform outputs", ["terraform", "-chdir=infra", "output", "-json"]))
    app_id = outputs.get("container_app_id", {}).get("value")
    if app_id:
        app = az("resource", "show", "--ids", app_id, "--api-version", "2024-03-01")
    else:
        apps = az("containerapp", "list", "--resource-group", os.environ["TF_VAR_resource_group_name"])
        if len(apps) != 1:
            raise InfrastructureError("Initial provisioning needs exactly one existing app to retain its image.")
        app = apps[0]
    app_image = app["properties"]["template"]["containers"][0]["image"]
    job_id = outputs.get("migration_job_id", {}).get("value")
    if not job_id:
        return app_image, None
    job = az("resource", "show", "--ids", job_id, "--api-version", "2024-03-01")
    container = job["properties"]["template"]["containers"][0]
    return app_image, None if container.get("command") == ["/bootstrap-disabled"] else container["image"]


def apply_infrastructure(*, always):
    # A tree ID covers changes to infra/ even when newer main CI supersedes a failed run.
    revision = command("Read infra tree", ["git", "rev-parse", "HEAD:infra"]).strip()
    marker_name = "deployments/" + os.environ["TFSTATE_KEY"] + ".infra-tree"
    blob = [
        "--auth-mode", "login", "--account-name", os.environ["TFSTATE_STORAGE_ACCOUNT"],
        "--container-name", os.environ["TFSTATE_CONTAINER"], "--name", marker_name,
    ]
    with tempfile.TemporaryDirectory(dir=os.environ["RUNNER_TEMP"]) as temp:
        marker = Path(temp) / "infra-tree"
        if not always and az("storage", "blob", "exists", *blob)["exists"]:
            az("storage", "blob", "download", *blob, "--file", str(marker))
            if marker.read_text(encoding="ascii").strip() == revision:
                print("Infrastructure unchanged since the last successful apply.", flush=True)
                return

        app_image, job_image = retained_images()
        if not app_image or (job_image is not None and not job_image):
            raise InfrastructureError("The running app or migration job image could not be retained.")
        env = dict(os.environ, TF_VAR_retained_app_image=app_image, TF_VAR_container_image=job_image or "")
        plan = Path(temp) / "infra.tfplan"
        tf = ["terraform", "-chdir=infra"]
        print("Planning infrastructure with the existing app and migration job images.", flush=True)
        command("Terraform infrastructure plan", [
            *tf, "plan", "-input=false", "-no-color", "-lock-timeout=5m", "-var-file=prod.tfvars",
            f"-var=bootstrap_only={'true' if job_image is None else 'false'}", f"-out={plan}",
        ], env=env, visible=True)
        print("Applying the saved infrastructure plan.", flush=True)
        command("Terraform infrastructure apply", [
            *tf, "apply", "-input=false", "-no-color", "-lock-timeout=5m", str(plan),
        ], env=env, visible=True)
        marker.write_text(revision + "\n", encoding="ascii")
        az("storage", "blob", "upload", *blob, "--file", str(marker), "--overwrite", "true")
        print("Infrastructure applied.", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--always", action="store_true", help="Reconcile infrastructure even without a source change.")
    args = parser.parse_args()
    try:
        apply_infrastructure(always=args.always)
    except InfrastructureError as error:
        parser.exit(1, f"{error}\n")
    except (OSError, ValueError, KeyError, TypeError, IndexError, subprocess.TimeoutExpired):
        parser.exit(1, "Infrastructure operation could not complete; check runner tools, state and Azure access.\n")


if __name__ == "__main__":
    main()
