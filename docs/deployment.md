# Production deployment

Production deployment is manually dispatched by the repository owner from
`main` using `.github/workflows/deploy.yml`. The workflow builds an image tagged
with the commit SHA, uses GitHub OIDC for Azure access, and serializes Terraform
deployments. Do not use this workflow for stage-one bootstrap. See
[production infrastructure](infrastructure.md) for the owner-managed bootstrap
and identity prerequisites.

## Stage-one bootstrap

The owner plans and applies the bootstrap separately, using the existing remote
backend and an OIDC-authenticated Terraform session. Never run this path from
PR CI or apply it on merge. Before starting, select the intended subscription,
configure the backend and required variables as described in the infrastructure
guide, and confirm the deployment identity and OIDC trust have the required
access. `API_ALLOWED_PRINCIPAL_OBJECT_IDS` is not required for bootstrap; it
remains mandatory for a normal application deployment.

Choose an absolute `BOOTSTRAP_PLAN` path outside the repository. Plan files and
unredacted plan output can contain sensitive infrastructure data; keep them
private and do not commit them or publish them in a PR.

```sh
set -euo pipefail
terraform -chdir=infra plan -input=false -var-file=prod.tfvars \
  -var='bootstrap_only=true' \
  -target=azurerm_container_app_job.migration \
  -out="$BOOTSTRAP_PLAN"
terraform -chdir=infra show -json "$BOOTSTRAP_PLAN" \
  | python3 .github/scripts/check_bootstrap_plan.py
terraform -chdir=infra show -no-color "$BOOTSTRAP_PLAN"
```

The checker rejects deletions/replacements, unexpected managed resources,
role assignments, application changes and an executable migration job. It
does not replace reviewing every change in the exact saved plan. The target
includes the SQL server/database, the two identities, migration job, Container
Apps environment, Log Analytics workspace and the existing SQL firewall rule;
the latter dependencies support those resources. No API registration, Key Vault
or application revision is targeted. The job has a manual trigger only and
uses a pinned public placeholder with a deliberately nonexistent command and no
database configuration. Creating it does not start it. Microsoft documents
[manual job creation](https://learn.microsoft.com/en-us/azure/container-apps/jobs-get-started-cli);
live image pull and creation remain unverified. If Azure rejects the
placeholder, stop and ask the owner before moving the job to stage two (B6).

Before any apply, the owner reviews the exact saved plan, publishes a
redacted review package without IDs, and reviews the cost and prerequisites.
SQL free-offer eligibility and limits, job execution consumption, and Log
Analytics ingestion can affect costs; see the infrastructure guide. The owner
alone then applies that same saved plan in an OIDC-authenticated session:

```sh
terraform -chdir=infra apply "$BOOTSTRAP_PLAN"
```

Do not regenerate the plan after review and treat it as the same plan. If the
plan is stale or rejected, generate, check and review a new one. Do not start
the job, run migrations, deploy the app, or change traffic in stage one.
Afterward the owner performs SQL grants before any stage-two work.

## Migration gate

The workflow first plans and applies only the Container Apps migration job and
its dependencies. This stage does not update the web app. The workflow then
checks all existing execution pages and refuses to start if an execution is
active or its status is indeterminate. It then starts the manual job and
requires the start response to identify the execution and its job. It polls
that exact execution for up to 30 minutes and promotes the
app only after that execution reports
`Succeeded`. Failed, stopped, unknown, ambiguous, missing, or timed-out
executions stop the workflow before the full Terraform apply.

The Container Apps start API can return HTTP 202 without an execution ID. This
gate cannot yet correlate that response to a specific execution and therefore
stops without promotion; do not infer the execution from the job's execution
list, since another actor could start a different execution concurrently.
The pre-start check cannot prevent an external actor starting an execution
after the check; exact response-based execution identification remains required.

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
