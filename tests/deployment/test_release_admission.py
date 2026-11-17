import copy
import contextlib
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tarfile
import tempfile
import types
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location(
    "release_admission", ROOT / ".github/scripts/release_admission.py",
)
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)
preflight_spec = importlib.util.spec_from_file_location(
    "deployment_preflight", ROOT / ".github/scripts/deployment_preflight.py",
)
preflight = importlib.util.module_from_spec(preflight_spec)
preflight_spec.loader.exec_module(preflight)


def trusted_run():
    return {
        "id": 123, "run_attempt": 2, "event": "push", "head_branch": "main",
        "status": "completed", "conclusion": "success", "head_sha": "a" * 40,
        "repository": {"full_name": release.REPOSITORY},
        "head_repository": {"full_name": release.REPOSITORY},
        "path": release.WORKFLOW, "workflow_id": 456,
    }


def manifest():
    return {
        "schema": 1, "repository": release.REPOSITORY, "sha": "a" * 40,
        "run_id": "123", "run_attempt": 2,
        "image": f"{release.PACKAGE}@sha256:" + "b" * 64,
        "config_digest": "sha256:" + "c" * 64,
    }


def write_tested_image(path, config, *, config_path=None, tags=None, entries=None):
    content = json.dumps(config).encode()
    digest = hashlib.sha256(content).hexdigest()
    name = config_path or f"blobs/sha256/{digest}"
    entries = entries if entries is not None else [{
        "Config": name, "RepoTags": ["bujo-host-test:latest"] if tags is None else tags,
    }]
    with tarfile.open(path, "w") as archive:
        for filename, body in (("manifest.json", json.dumps(entries).encode()), (name, content)):
            info = tarfile.TarInfo(filename)
            info.size = len(body)
            archive.addfile(info, io.BytesIO(body))
    return f"sha256:{digest}"


class ReleaseAdmissionTests(unittest.TestCase):
    def test_trust_matrix_rejects_non_main_push_or_unsuccessful_or_foreign_runs(self):
        run = trusted_run()
        workflow = {"id": 456, "path": release.WORKFLOW}
        self.assertTrue(release.validate_run(run, workflow, "a" * 40, 2))
        for key, value in (
            ("event", "pull_request"), ("event", "workflow_dispatch"),
            ("head_branch", "feature"), ("status", "in_progress"),
            ("conclusion", "failure"), ("head_repository", {"full_name": "other/repo"}),
            ("repository", {"full_name": "other/repo"}), ("workflow_id", 999),
            ("path", ".github/workflows/other.yml"), ("head_sha", "invalid"),
            ("run_attempt", 1), ("id", 0),
        ):
            altered = copy.deepcopy(run)
            altered[key] = value
            with self.subTest(key=key, value=value), self.assertRaises(ValueError):
                release.validate_run(altered, workflow, "a" * 40, 2)
        with self.assertRaises(ValueError):
            release.validate_run(run, {"id": 456, "path": "other"}, "a" * 40)
        with self.assertRaises(ValueError):
            release.validate_run(run, workflow, None)

    def test_superseded_release_is_skipped(self):
        self.assertFalse(release.validate_run(
            trusted_run(), {"id": 456, "path": release.WORKFLOW}, "d" * 40,
        ))

    def test_manifest_binds_run_attempt_source_and_both_image_digests(self):
        self.assertEqual(manifest(), release.validate_manifest(manifest(), trusted_run()))
        for key, value in (
            ("schema", 2), ("schema", True), ("repository", "other/repo"), ("sha", "d" * 40),
            ("run_id", "124"), ("run_attempt", 1),
            ("image", f"{release.PACKAGE}:latest"),
            ("image", "ghcr.io/other/repo@sha256:" + "b" * 64),
            ("config_digest", "unverified"), ("command", "untrusted"),
        ):
            altered = manifest()
            altered[key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                release.validate_manifest(altered, trusted_run())

    def test_event_requires_trusted_completion_or_owner_main_recovery(self):
        env = {
            "GITHUB_REPOSITORY": release.REPOSITORY, "GITHUB_REF": "refs/heads/main",
            "GITHUB_ACTOR": "cedricmendelin", "GITHUB_TRIGGERING_ACTOR": "cedricmendelin",
        }
        event = {"action": "completed", "workflow_run": trusted_run()}
        self.assertEqual((123, 2), release.validate_event("workflow_run", event, env))
        self.assertEqual((123, None), release.validate_event(
            "workflow_dispatch", {"inputs": {"ci_run_id": "123"}}, env,
        ))
        for key, value in (
            ("GITHUB_REPOSITORY", "other/repo"), ("GITHUB_REF", "refs/heads/feature"),
            ("GITHUB_ACTOR", "collaborator"), ("GITHUB_TRIGGERING_ACTOR", "collaborator"),
        ):
            with self.subTest(key=key), self.assertRaises(ValueError):
                release.validate_event("workflow_dispatch", {"inputs": {"ci_run_id": "123"}}, env | {key: value})
        for value in ("", "123;echo unsafe", "-1"):
            with self.assertRaises(ValueError):
                release.validate_event("workflow_dispatch", {"inputs": {"ci_run_id": value}}, env)
        event["workflow_run"]["event"] = "pull_request"
        with self.assertRaises(ValueError):
            release.validate_event("workflow_run", event, env)

    def test_exact_attempt_artifact_is_required_and_only_data_is_accepted(self):
        run = trusted_run()
        evidence = {
            "actions/runs/123": run,
            "actions/workflows/ci.yml": {"id": 456, "path": release.WORKFLOW},
            "git/ref/heads/main": {"object": {"sha": "a" * 40}},
            "actions/runs/123/artifacts?name=release-metadata-123-2&per_page=100": {
                "total_count": 1, "artifacts": [{
                    "name": "release-metadata-123-2", "expired": False,
                    "workflow_run": {"id": 123, "head_sha": "a" * 40},
                }],
            },
        }

        def download(command, **_):
            directory = Path(command[command.index("--dir") + 1])
            (directory / "release.json").write_text(json.dumps(manifest()), encoding="utf-8")
            return types.SimpleNamespace(returncode=0)

        with patch.object(release, "github", side_effect=evidence.__getitem__), \
             patch.object(release.subprocess, "run", side_effect=download):
            self.assertEqual(manifest(), release.admit(123, 2))
            artifacts = evidence["actions/runs/123/artifacts?name=release-metadata-123-2&per_page=100"]
            artifacts["total_count"] = 2
            with self.assertRaises(ValueError):
                release.admit(123, 2)
            artifacts["total_count"] = 1
            artifacts["artifacts"][0]["expired"] = True
            with self.assertRaises(ValueError):
                release.admit(123, 2)
            artifacts["artifacts"][0]["expired"] = False
            for result, category in (
                (types.SimpleNamespace(returncode=1, stderr="private-download-error"), "artifact-download"),
                (types.SimpleNamespace(returncode=0), "artifact-layout"),
            ):
                with self.subTest(category=category), \
                     patch.object(release.subprocess, "run", return_value=result):
                    with self.assertRaises(ValueError) as caught:
                        release.admit(123, 2)
                    self.assertEqual(category, caught.exception.category)
                    self.assertNotIn("private-download-error", str(caught.exception))

    def test_api_run_identity_cannot_drift_from_requested_run(self):
        with patch.object(release, "github", return_value=trusted_run() | {"id": 124}):
            with self.assertRaises(ValueError):
                release.admit(123, 2)

    def test_publication_records_loaded_image_not_a_tag_or_rebuilt_image(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "release.json"
            archive = Path(directory) / "tested-host.tar"
            config_digest = write_tested_image(archive, {"os": "linux", "architecture": "amd64"})
            env = {"GITHUB_RUN_ID": "123", "GITHUB_RUN_ATTEMPT": "2", "GITHUB_SHA": "a" * 40}
            result = types.SimpleNamespace(returncode=0, stdout=json.dumps([{
                "RepoDigests": [manifest()["image"]], "Id": manifest()["image"].split("@")[1],
            }]))
            with patch.dict(release.os.environ, env), \
                 patch.object(release.subprocess, "run", return_value=result) as inspect:
                release.publish_manifest(path, archive)
                self.assertEqual(manifest() | {"config_digest": config_digest}, json.loads(path.read_text()))
                self.assertEqual(
                    ["docker", "image", "inspect", f"{release.PACKAGE}:ci-123-2"],
                    inspect.call_args.args[0],
                )
                result.stdout = json.dumps([{"RepoDigests": [], "Id": manifest()["config_digest"]}])
                with self.assertRaises(ValueError):
                    release.publish_manifest(path, archive)

    def test_tested_archive_rejects_wrong_image_or_configuration(self):
        with tempfile.TemporaryDirectory() as directory:
            archive = Path(directory) / "image.tar"
            valid = {"os": "linux", "architecture": "amd64"}
            for config, options in (
                (valid, {"tags": ["other:latest"]}),
                (valid, {"entries": []}),
                (valid, {"config_path": "blobs/sha256/" + "0" * 64}),
                (valid, {"config_path": "../config.json"}),
                ({"os": "windows", "architecture": "amd64"}, {}),
                ({"os": "linux", "architecture": "arm64"}, {}),
            ):
                with self.subTest(config=config, options=options):
                    write_tested_image(archive, config, **options)
                    with self.assertRaises(ValueError):
                        release.tested_config_digest(archive)
            digest = write_tested_image(archive, valid)
            classic_name = digest.removeprefix("sha256:") + ".json"
            write_tested_image(archive, valid, config_path=classic_name)
            self.assertEqual(digest, release.tested_config_digest(archive))

    def test_published_provenance_matches_registry_index_and_rejects_changed_config(self):
        config = {"os": "linux", "architecture": "amd64"}
        with tempfile.TemporaryDirectory() as directory:
            archive = Path(directory) / "tested-host.tar"
            config_digest = write_tested_image(archive, config)
            child = json.dumps({"config": {"digest": config_digest}}).encode()
            child_digest = "sha256:" + hashlib.sha256(child).hexdigest()
            index = json.dumps({"manifests": [{
                "digest": child_digest, "platform": {"os": "linux", "architecture": "amd64"},
            }]}).encode()
            index_digest = "sha256:" + hashlib.sha256(index).hexdigest()
            inspected = types.SimpleNamespace(returncode=0, stdout=json.dumps([{
                "RepoDigests": [f"{release.PACKAGE}@{index_digest}"], "Id": index_digest,
            }]))
            path = Path(directory) / "release.json"
            with patch.dict(release.os.environ, {
                "GITHUB_RUN_ID": "123", "GITHUB_RUN_ATTEMPT": "2", "GITHUB_SHA": "a" * 40,
            }), patch.object(release.subprocess, "run", return_value=inspected):
                release.publish_manifest(path, archive)
            provenance = json.loads(path.read_text())
            self.assertEqual(config_digest, provenance["config_digest"])
            def verify(value):
                responses = iter((b'{"token":"test-token"}', index, child))
                preflight.verify_public_image(value, lambda *_args, **_kwargs: io.BytesIO(next(responses)))
            verify(provenance)
            with self.assertRaisesRegex(ValueError, "not the CI-tested"):
                verify(provenance | {"config_digest": "sha256:" + "0" * 64})

    def test_stale_release_never_downloads_artifacts(self):
        evidence = {
            "actions/runs/123": trusted_run(),
            "actions/workflows/ci.yml": {"id": 456, "path": release.WORKFLOW},
            "git/ref/heads/main": {"object": {"sha": "d" * 40}},
        }
        with patch.object(release, "github", side_effect=evidence.__getitem__), \
             patch.object(release.subprocess, "run") as download:
            self.assertIsNone(release.admit(123, 2))
            download.assert_not_called()

    def test_queue_recheck_rejects_manifest_drift(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "release.json"
            path.write_text(json.dumps(manifest()), encoding="utf-8")
            event = Path(directory) / "event.json"
            event.write_text(json.dumps({"action": "completed", "workflow_run": trusted_run()}))
            with patch.object(release.sys, "argv", [
                "release_admission.py", "recheck", "--manifest", str(path),
            ]), patch.dict(release.os.environ, {
                "GITHUB_EVENT_PATH": str(event), "GITHUB_EVENT_NAME": "workflow_run",
                "GITHUB_REPOSITORY": release.REPOSITORY,
            }), patch.object(release, "admit", return_value=manifest()):
                self.assertEqual(0, release.main())
                path.write_text(json.dumps(manifest() | {"image": "untrusted"}))
                self.assertEqual(1, release.main())

    def test_admission_reports_safe_failure_categories_without_command_output(self):
        private = "private-token-and-response"
        for returncode, stdout, stderr, category in (
            (1, "", f"gh: forbidden (HTTP 403) {private}", "github-api-http-403"),
            (1, "", private, "github-api-command"),
            (0, private, "", "github-api-json"),
        ):
            with self.subTest(category=category), tempfile.TemporaryDirectory() as directory:
                event = Path(directory) / "event.json"
                event.write_text(json.dumps({"action": "completed", "workflow_run": trusted_run()}))
                output = io.StringIO()
                with patch.object(release.sys, "argv", [
                    "release_admission.py", "admit", "--manifest", str(Path(directory) / "release.json"),
                ]), patch.dict(release.os.environ, {
                    "GITHUB_EVENT_PATH": str(event), "GITHUB_EVENT_NAME": "workflow_run",
                    "GITHUB_REPOSITORY": release.REPOSITORY,
                }), patch.object(release.subprocess, "run", return_value=types.SimpleNamespace(
                    returncode=returncode, stdout=stdout, stderr=stderr,
                )), contextlib.redirect_stderr(output):
                    self.assertEqual(1, release.main())
                self.assertIn(f"[{category}]", output.getvalue())
                self.assertNotIn(private, output.getvalue())
                self.assertFalse((Path(directory) / "release.json").exists())

    def test_event_and_unexpected_failures_have_distinct_redacted_categories(self):
        private = "private-event-or-path"
        with tempfile.TemporaryDirectory() as directory:
            event = Path(directory) / "event.json"
            event.write_text(json.dumps({"action": private, "workflow_run": trusted_run()}))
            for failure, category in (
                (None, "event-workflow-run"),
                (OSError(private), "inspection-io"),
                (ValueError(private), "invalid-input"),
                (release.subprocess.TimeoutExpired([private], 1), "inspection-timeout"),
            ):
                output = io.StringIO()
                with self.subTest(category=category), patch.object(release.sys, "argv", [
                    "release_admission.py", "admit", "--manifest", str(Path(directory) / "release.json"),
                ]), patch.dict(release.os.environ, {
                    "GITHUB_EVENT_PATH": str(event), "GITHUB_EVENT_NAME": "workflow_run",
                    "GITHUB_REPOSITORY": release.REPOSITORY,
                }), patch.object(release, "validate_event", wraps=release.validate_event,
                                 side_effect=failure), contextlib.redirect_stderr(output):
                    self.assertEqual(1, release.main())
                self.assertIn(f"[{category}]", output.getvalue())
                self.assertNotIn(private, output.getvalue())

    def test_workflows_preserve_ci_image_and_azure_trust_boundaries(self):
        ci = (ROOT / ".github/workflows/ci.yml").read_text()
        deploy = (ROOT / ".github/workflows/deploy.yml").read_text()
        publish = ci.split("\n  publish:", 1)[1]
        self.assertIn("needs: ci", publish)
        self.assertIn("github.event_name == 'push'", publish)
        self.assertIn("docker load", publish)
        self.assertIn('--tested-image "$RUNNER_TEMP/tested-image/tested-host.tar"', publish)
        self.assertNotIn("docker build", publish)
        self.assertNotIn("id-token: write", ci)
        self.assertIn("workflow_run:", deploy)
        self.assertIn("release_admission.py recheck", deploy)
        self.assertIn("BUJO_DEPLOYMENT_ENABLED", deploy)
        self.assertIn("cancel-in-progress: false", deploy)
        apply_job = deploy.split("\n  apply:", 1)[1]
        self.assertNotIn("runner.temp", apply_job.split("    steps:", 1)[0])
        self.assertIn("BUJO_RELEASE_MANIFEST: ${{ runner.temp }}/release.json", apply_job)
        self.assertLess(apply_job.index("release_admission.py recheck"), apply_job.index("apply_infrastructure.py"))
        self.assertLess(apply_job.index("apply_infrastructure.py"), apply_job.index("infra/deploy_gate.py"))
        self.assertNotIn("docker build", deploy)
        self.assertNotIn("quickstart:latest", deploy)


if __name__ == "__main__":
    unittest.main()
