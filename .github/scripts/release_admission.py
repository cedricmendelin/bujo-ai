"""Admit only immutable images published by this repository's successful main CI."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tarfile
import tempfile


REPOSITORY = "cedricmendelin/bujo-ai"
WORKFLOW = ".github/workflows/ci.yml"
PACKAGE = f"ghcr.io/{REPOSITORY}"


class AdmissionError(ValueError):
    def __init__(self, category, message):
        super().__init__(message)
        self.category = category


def github(path):
    result = subprocess.run(
        ["gh", "api", f"repos/{REPOSITORY}/{path}"],
        capture_output=True, text=True, check=False, timeout=120,
    )
    if result.returncode:
        status = re.search(r"\bHTTP (401|403|404|429|5\d\d)\b", result.stderr)
        category = f"github-api-http-{status[1]}" if status else "github-api-command"
        raise AdmissionError(category, "GitHub release evidence could not be read.")
    try:
        return json.loads(result.stdout)
    except ValueError:
        raise AdmissionError("github-api-json", "GitHub release evidence was not JSON.") from None


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
        raise AdmissionError("ci-run-trust", "Release requires a successful same-repository main-push CI run.")
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
        raise AdmissionError("manifest-binding", "Release manifest is not bound to the exact trusted CI run and image.")
    return manifest


def validate_event(event_name, event, env):
    if env.get("GITHUB_REPOSITORY") != REPOSITORY:
        raise AdmissionError("event-repository", "Unexpected deployment repository.")
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
            raise AdmissionError("event-workflow-run", "Untrusted CI completion event.")
        return run["id"], run["run_attempt"]
    owner = REPOSITORY.split("/")[0]
    if (
        event_name != "workflow_dispatch"
        or env.get("GITHUB_REF") != "refs/heads/main"
        or env.get("GITHUB_ACTOR", "").casefold() != owner
        or env.get("GITHUB_TRIGGERING_ACTOR", "").casefold() != owner
        or not re.fullmatch(r"[1-9][0-9]*", str(event.get("inputs", {}).get("ci_run_id", "")))
    ):
        raise AdmissionError("event-manual-recovery", "Manual recovery requires an owner dispatch from main with a CI run ID.")
    return int(event["inputs"]["ci_run_id"]), None


def admit(run_id, expected_attempt=None):
    run = github(f"actions/runs/{run_id}")
    if run.get("id") != run_id:
        raise AdmissionError("ci-run-identity", "CI run identity does not match the requested release.")
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
        raise AdmissionError("artifact-metadata", "The exact CI-attempt release artifact is missing or ambiguous.")
    with tempfile.TemporaryDirectory(prefix="bujo-release-") as directory:
        result = subprocess.run(
            ["gh", "run", "download", str(run_id), "--repo", REPOSITORY,
             "--name", name, "--dir", directory],
            capture_output=True, text=True, check=False, timeout=120,
        )
        if result.returncode:
            raise AdmissionError("artifact-download", "The exact release artifact could not be downloaded.")
        files = list(Path(directory).iterdir())
        manifest_path = Path(directory) / "release.json"
        if (
            files != [manifest_path] or manifest_path.is_symlink()
            or not manifest_path.is_file() or manifest_path.stat().st_size > 4096
        ):
            raise AdmissionError("artifact-layout", "Release artifact must contain only the bounded release manifest.")
        return validate_manifest(json.loads(manifest_path.read_text(encoding="utf-8")), run)


def tested_config_digest(archive_path):
    with tarfile.open(archive_path, "r:") as archive:
        def read_json(name, limit):
            members = [member for member in archive.getmembers() if member.name == name]
            if len(members) != 1 or not members[0].isfile() or members[0].size > limit:
                raise AdmissionError("tested-image-archive", "Tested image archive has invalid metadata.")
            return archive.extractfile(members[0]).read()

        entries = json.loads(read_json("manifest.json", 1024 * 1024))
        if not isinstance(entries, list) or len(entries) != 1 or not isinstance(entries[0], dict):
            raise AdmissionError("tested-image-archive", "Tested image archive must contain exactly one image.")
        entry = entries[0]
        if entry.get("RepoTags") != ["bujo-host-test:latest"]:
            raise AdmissionError("tested-image-archive", "Tested image archive has an unexpected tag.")
        name = entry.get("Config")
        if not isinstance(name, str):
            raise AdmissionError("tested-image-archive", "Tested image configuration is missing.")
        match = re.fullmatch(r"(?:blobs/sha256/([0-9a-f]{64})|([0-9a-f]{64})\.json)", name)
        if not match:
            raise AdmissionError("tested-image-archive", "Tested image configuration path is invalid.")
        content = read_json(name, 2 * 1024 * 1024)
        digest = hashlib.sha256(content).hexdigest()
        if digest != (match[1] or match[2]):
            raise AdmissionError("tested-image-archive", "Tested image configuration digest does not match.")
        config = json.loads(content)
        if not isinstance(config, dict) or config.get("os") != "linux" or config.get("architecture") != "amd64":
            raise AdmissionError("tested-image-archive", "Tested image must target Linux amd64.")
        return f"sha256:{digest}"


def publish_manifest(path, tested_image):
    run_id = os.environ["GITHUB_RUN_ID"]
    attempt = int(os.environ["GITHUB_RUN_ATTEMPT"])
    if not re.fullmatch(r"[1-9][0-9]*", run_id) or attempt < 1:
        raise AdmissionError("publication-identity", "Invalid CI publication identity.")
    tag = f"{PACKAGE}:ci-{run_id}-{attempt}"
    result = subprocess.run(
        ["docker", "image", "inspect", tag], capture_output=True, text=True, check=False, timeout=30,
    )
    if result.returncode:
        raise AdmissionError("publication-inspection", "Published image inspection failed.")
    image = json.loads(result.stdout)[0]
    digests = [digest for digest in image.get("RepoDigests", []) if digest.startswith(PACKAGE + "@")]
    if len(digests) != 1:
        raise AdmissionError("publication-digest", "Published image digest is missing or ambiguous.")
    manifest = {
        "schema": 1, "repository": REPOSITORY, "sha": os.environ["GITHUB_SHA"],
        "run_id": run_id, "run_attempt": attempt, "image": digests[0],
        "config_digest": tested_config_digest(tested_image),
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
    parser.add_argument("--tested-image", type=Path)
    args = parser.parse_args()
    try:
        if args.mode == "publish":
            if args.tested_image is None:
                raise AdmissionError("tested-image-archive", "The tested image archive is required.")
            publish_manifest(args.manifest, args.tested_image)
            return 0
        event = json.loads(Path(os.environ["GITHUB_EVENT_PATH"]).read_text(encoding="utf-8"))
        run_id, attempt = validate_event(os.environ["GITHUB_EVENT_NAME"], event, os.environ)
        manifest = admit(run_id, attempt)
        if args.mode == "recheck":
            if manifest is None or manifest != json.loads(args.manifest.read_text(encoding="utf-8")):
                raise AdmissionError("release-drift", "Release was superseded or its provenance changed; deployment stopped.")
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
    except AdmissionError as error:
        print(f"Release admission failed [{error.category}]: {error}", file=sys.stderr)
        return 1
    except (OSError, ValueError, KeyError, TypeError, IndexError, tarfile.TarError, subprocess.TimeoutExpired) as error:
        if isinstance(error, subprocess.TimeoutExpired):
            category = "inspection-timeout"
        elif isinstance(error, OSError):
            category = "inspection-io"
        elif isinstance(error, json.JSONDecodeError):
            category = "response-json"
        elif isinstance(error, (KeyError, TypeError, IndexError)):
            category = "response-shape"
        else:
            category = "invalid-input"
        print(f"Release admission failed [{category}]: verify trusted main CI and its exact release artifact.",
              file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
