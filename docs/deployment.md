# Production deployment

Production deployment is driven by `.github/workflows/deploy.yml` after a push
to `main`. The workflow builds an image tagged with the commit SHA, uses GitHub
OIDC for Azure access, and serializes Terraform deployments. See
[production infrastructure](infrastructure.md) for the owner-managed bootstrap
and identity prerequisites.

## Migration gate

The workflow first plans and applies only the Container Apps migration job and
its dependencies. This stage does not update the web app. The workflow then
starts the manual job and requires the start response to identify the execution
and its job. It polls that exact execution for up to 30 minutes and promotes the
app only after that execution reports
`Succeeded`. Failed, stopped, unknown, ambiguous, missing, or timed-out
executions stop the workflow before the full Terraform apply.

The Container Apps start API can return HTTP 202 without an execution ID. This
gate cannot yet correlate that response to a specific execution and therefore
stops without promotion; do not infer the execution from the job's execution
list, since another actor could start a different execution concurrently.

The implementation is in `infra/deploy_gate.py`; CI simulates success, failure,
timeout, an indeterminate status, and a first-deployment SQL-grant failure.
Each failure case verifies that application promotion is not called. The
production workflow also probes `/health/live` after promotion.

## Schema rollout and recovery

The migration job runs `Bujo.Host.dll --migrate-only` from the same commit's
image, with a separate managed identity. It has no automatic retries and a
30-minute replica timeout. The production web host never runs migrations at
startup. Migrations must remain additive and backward-compatible so the
existing app can serve traffic until migration succeeds and promotion begins.

If migration fails, inspect the Container Apps execution logs, correct the
reported prerequisite (for example, SQL principal or directory lookup
permissions), then rerun the failed deployment. Do not start a parallel
execution. Re-running an already-applied EF migration is safe.

The app uses single-revision mode. A failure before promotion leaves the
existing revision untouched; a liveness failure after promotion does not
automatically restore the prior image or reverse the database schema. Recovery
requires an explicitly reviewed deployment of a compatible app image. Schema
changes are never automatically rolled back. If database restoration is
necessary, follow the Azure SQL point-in-time restore guidance in the
[technology baseline](adr/0001-technology-baseline.md) and review the recovered
data before reconnecting the app.

## Verification boundary

CI validates the Terraform configuration and tests the deployment gate with
simulated execution responses. No live Azure migration or app promotion is
performed by pull-request checks. Live permissions, SQL setup, GHCR image pull
access, and the production Terraform plan remain owner-managed prerequisites.
The migration start call uses the documented Azure
[start operation](https://learn.microsoft.com/en-us/rest/api/resource-manager/containerapps/jobs/start?view=rest-resource-manager-containerapps-2024-03-01).
HTTP 202 correlation and the configured OIDC identity's live authorization
remain unverified.
