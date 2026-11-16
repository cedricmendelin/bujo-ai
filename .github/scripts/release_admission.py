"""Admit only immutable images published by this repository's successful main CI."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile


REPOSITORY = "cedricmendelin/bujo-ai"
WORKFLOW = ".github/workflows/ci.yml"
PACKAGE = f"ghcr.io/{REPOSITORY}"


def github(path):
    result = subprocess.run(
        ["gh", "api", f"repos/{REPOSITORY}/{path}"],
        capture_output=True, text=True, check=False, timeout=120,
    )
    if result.returncode:
        raise ValueError("GitHub release evidence could not be read.")
    try:
        return json.loads(result.stdout)
    except ValueError:
        raise ValueError("GitHub release evidence was not JSON.") from None


def validate_run(run, workflow, current_sha, expected_attempt=None):
    if (
        not isinstance(run, dict)
        or not isinstance(workflow, dict)
        or not isinstance(current_sha, str)
        or not re.fullmatch(r"[0-9a-f]{40}", current_sha)
        or run.get("event") != "push"
        or run.get("head_branch") != "main"
        or run.get("status") != "completed"
        or run.get("conclusion") != "success"
        or run.get("head_repository", {}).get("full_name") != REPOSITORY
        or run.get("repository", {}).get("full_name") != REPOSITORY
        or workflow.get("path") != WORKFLOW
        or type(workflow.get("id")) is not int
        or workflow["id"] < 1
        or run.get("workflow_id") != workflow.get("id")
        or run.get("path") != WORKFLOW
        or not re.fullmatch(r"[0-9a-f]{40}", run.get("head_sha", ""))
        or type(run.get("run_attempt")) is not int
        or run["run_attempt"] < 1
        or type(run.get("id")) is not int
        or run["id"] < 1
        or (expected_attempt is not None and run["run_attempt"] != expected_attempt)
    ):
        raise ValueError("Release requires a successful same-repository main-push CI run.")
    return run["head_sha"] == current_sha


def validate_manifest(manifest, run):
    expected = {
        "schema": 1,
        "repository": REPOSITORY,
        "sha": run["head_sha"],
        "run_id": str(run["id"]),
        "run_attempt": run["run_attempt"],
    }
    if (
        not isinstance(manifest, dict)
        or set(manifest) != {*expected, "image", "config_digest"}
        or type(manifest.get("schema")) is not int
        or type(manifest.get("run_attempt")) is not int
        or any(manifest.get(key) != value for key, value in expected.items())
        or not re.fullmatch(re.escape(PACKAGE) + r"@sha256:[0-9a-f]{64}", manifest.get("image", ""))
        or not re.fullmatch(r"sha256:[0-9a-f]{64}", manifest.get("config_digest", ""))
    ):
        raise ValueError("Release manifest is not bound to the exact trusted CI run and image.")
    return manifest


def validate_event(event_name, event, env):
    if env.get("GITHUB_REPOSITORY") != REPOSITORY:
        raise ValueError("Unexpected deployment repository.")
    if event_name == "workflow_run":
        run = event.get("workflow_run", {})
        if (
            event.get("action") != "completed"
            or run.get("event") != "push"
            or run.get("head_branch") != "main"
            or run.get("conclusion") != "success"
            or run.get("head_repository", {}).get("full_name") != REPOSITORY
            or type(run.get("id")) is not int
            or type(run.get("run_attempt")) is not int
        ):
            raise ValueError("Untrusted CI completion event.")
        return run["id"], run["run_attempt"]
    owner = REPOSITORY.split("/")[0]
    if (
        event_name != "workflow_dispatch"
        or env.get("GITHUB_REF") != "refs/heads/main"
        or env.get("GITHUB_ACTOR", "").casefold() != owner
        or env.get("GITHUB_TRIGGERING_ACTOR", "").casefold() != owner
        or not re.fullmatch(r"[1-9][0-9]*", str(event.get("inputs", {}).get("ci_run_id", "")))
    ):
        raise ValueError("Manual recovery requires an owner dispatch from main with a CI run ID.")
    return int(event["inputs"]["ci_run_id"]), None


def admit(run_id, expected_attempt=None):
    run = github(f"actions/runs/{run_id}")
    if run.get("id") != run_id:
        raise ValueError("CI run identity does not match the requested release.")
    workflow = github("actions/workflows/ci.yml")
    current_sha = github("git/ref/heads/main").get("object", {}).get("sha")
    if not validate_run(run, workflow, current_sha, expected_attempt):
        return None
    name = f"release-metadata-{run_id}-{run['run_attempt']}"
    artifacts = github(f"actions/runs/{run_id}/artifacts?name={name}&per_page=100")
    candidates = artifacts.get("artifacts", [])
    if (
        artifacts.get("total_count") != 1
        or len(candidates) != 1
        or candidates[0].get("expired") is not False
        or candidates[0].get("name") != name
        or candidates[0].get("workflow_run", {}).get("id") != run_id
        or candidates[0].get("workflow_run", {}).get("head_sha") != run["head_sha"]
    ):
        raise ValueError("The exact CI-attempt release artifact is missing or ambiguous.")
    with tempfile.TemporaryDirectory(prefix="bujo-release-") as directory:
        result = subprocess.run(
            ["gh", "run", "download", str(run_id), "--repo", REPOSITORY,
             "--name", name, "--dir", directory],
            capture_output=True, text=True, check=False, timeout=120,
        )
        files = list(Path(directory).iterdir())
        manifest_path = Path(directory) / "release.json"
        if (
            result.returncode or files != [manifest_path] or manifest_path.is_symlink()
            or not manifest_path.is_file() or manifest_path.stat().st_size > 4096
        ):
            raise ValueError("Release artifact must contain only the bounded release manifest.")
        return validate_manifest(json.loads(manifest_path.read_text(encoding="utf-8")), run)


def publish_manifest(path):
    run_id = os.environ["GITHUB_RUN_ID"]
    attempt = int(os.environ["GITHUB_RUN_ATTEMPT"])
    if not re.fullmatch(r"[1-9][0-9]*", run_id) or attempt < 1:
        raise ValueError("Invalid CI publication identity.")
    tag = f"{PACKAGE}:ci-{run_id}-{attempt}"
    result = subprocess.run(
        ["docker", "image", "inspect", tag], capture_output=True, text=True, check=False, timeout=30,
    )
    if result.returncode:
        raise ValueError("Published image inspection failed.")
    image = json.loads(result.stdout)[0]
    digests = [digest for digest in image.get("RepoDigests", []) if digest.startswith(PACKAGE + "@")]
    if len(digests) != 1:
        raise ValueError("Published image digest is missing or ambiguous.")
    manifest = {
        "schema": 1, "repository": REPOSITORY, "sha": os.environ["GITHUB_SHA"],
        "run_id": run_id, "run_attempt": attempt, "image": digests[0],
        "config_digest": image["Id"],
    }
    validate_manifest(manifest, {
        "id": int(run_id), "run_attempt": attempt, "head_sha": os.environ["GITHUB_SHA"],
    })
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(manifest), encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("admit", "recheck", "publish"))
    parser.add_argument("--manifest", type=Path, required=True)
    args = parser.parse_args()
    try:
        if args.mode == "publish":
            publish_manifest(args.manifest)
            return 0
        event = json.loads(Path(os.environ["GITHUB_EVENT_PATH"]).read_text(encoding="utf-8"))
        run_id, attempt = validate_event(os.environ["GITHUB_EVENT_NAME"], event, os.environ)
        manifest = admit(run_id, attempt)
        if args.mode == "recheck":
            if manifest is None or manifest != json.loads(args.manifest.read_text(encoding="utf-8")):
                raise ValueError("Release was superseded or its provenance changed; deployment stopped.")
            return 0
        with Path(os.environ["GITHUB_OUTPUT"]).open("a", encoding="utf-8") as output:
            output.write(f"admitted={'true' if manifest else 'false'}\n")
            if manifest:
                for key in ("sha", "image", "run_id", "run_attempt"):
                    output.write(f"{key}={manifest[key]}\n")
        if manifest:
            args.manifest.parent.mkdir(parents=True, exist_ok=True)
            args.manifest.write_text(json.dumps(manifest), encoding="utf-8")
        else:
            print("Superseded CI release skipped without Azure access.")
        return 0
    except (OSError, ValueError, KeyError, TypeError, IndexError, subprocess.TimeoutExpired):
        print("Release admission failed: verify trusted main CI and its exact release artifact.", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
