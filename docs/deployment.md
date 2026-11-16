# Production deployment

Production releases are automated but **disabled by default**. After a
successful CI run for a push to `main`, `.github/workflows/deploy.yml` deploys
that exact build, provided the owner has finished the one-time setup in
[#55](https://github.com/cedricmendelin/bujo-ai/issues/55) and set both
activation variables. Until then the workflow stops before any Azure write.
Production currently still runs the legacy quickstart image; no application
release has been evidenced live yet.

This is a partial delivery. The initial API/application configuration baseline
is also unresolved: routine-release safety checks reject non-image
infrastructure or authorization changes. Do not enable releases or apply the
whole stack as a workaround; see [first-release baseline](#first-release-baseline).

## Release pipeline

1. **Build once (CI).** CI builds and smoke-tests the root `Dockerfile` image
   once. For main pushes only, a separate publish job pushes it to GHCR and
   records the immutable digest with its provenance (source SHA, CI run ID and
   attempt) in a release manifest artifact. Pull requests get no publish or
   Azure credentials. There is no quickstart fallback.
   When retrying CI, rerun **all** jobs: the tested-image artifact is bound to
   the producing run attempt, so rerunning publication alone cannot reuse a
   different attempt's image.
2. **Admission (`authorize` job).** A `workflow_run` event for a successful
   same-repository CI push run on `main` is admitted by
   `.github/scripts/release_admission.py` before any Azure credential exists.
   It checks the exact SHA, run, attempt and digest metadata, rejects stale
   releases (main has moved) and replaced attempts, and never executes code
   from the artifact. A duplicate event for an already verified release only
   re-verifies it (see the journal below). A manual `workflow_dispatch` takes only a
   previous successful main CI run ID for recovery; it cannot supply an
   arbitrary image or SHA.
3. **Serialized apply job (`prod` environment).** All releases share the
   `terraform-prod` concurrency group with `cancel-in-progress: false`. The job
   rechecks admission, runs `deployment_preflight.py` (requires the owner
   activation variables), signs in with GitHub OIDC, initializes the remote
   state, runs the live preflight (target, OIDC/bootstrap identity, anonymous
   GHCR digest), and runs the unified gate `python3 infra/deploy_gate.py`.
4. **Gate (`infra/deploy_gate.py`).** Described below. It writes nothing unless
   `BUJO_RELEASE_VALIDATED=true` and `BUJO_DEPLOYMENT_ENABLED=true`, and reruns
   `release_admission.py recheck` immediately before each cloud write (job
   update, job start, app promotion), so a newer main release or a replaced
   attempt stops an older run before it changes anything.

## Activation (owner only)

Set these `prod` environment (or repository) variables only after completing
and personally verifying every #55 item:

| Variable | Meaning |
| --- | --- |
| `BUJO_OWNER_SETUP_CONFIRMED` | `true` is the owner's explicit checklist attestation. It is **not** automatic proof of permissions; preflight labels it as such. |
| `BUJO_DEPLOYMENT_ENABLED` | `true` enables automatic releases once setup is verified. Remove it (or set any other value) to stop all releases. |

Run the non-mutating configuration check with
`python .github/scripts/deployment_preflight.py`. It reports each check as
verified, missing or unknown by setting name only, never values, links #55 for
remediation and never reports success by fallback.

## Stage-one bootstrap

The owner plans and applies the bootstrap separately, using the existing remote
backend. Never run this path from PR CI or apply it on merge.
`API_ALLOWED_PRINCIPAL_OBJECT_IDS` is not required for bootstrap; it remains
mandatory for a normal application deployment.

### Owner-run Windows PowerShell path

After this bootstrap code is merged, use a clean checkout of `main`, Terraform
1.17.0, Azure CLI, Python 3.11 or later, and Windows PowerShell 5.1 or
PowerShell 7. The owner's Azure user needs the required resource permissions and
Storage Blob Data Contributor on the state container. User authentication can
have broader rights than the deployment identity; verify the subscription and
every planned change. Values below are private placeholders, not repository
content. Run the commands yourself in one PowerShell session.

Sign in interactively and explicitly select and confirm the intended account:

```powershell
Set-Location "<repository-root>"
$subscription = "<subscription-id>"
az login --output none
if ($LASTEXITCODE -ne 0) { throw "Azure login failed" }
az account set --subscription $subscription
if ($LASTEXITCODE -ne 0) { throw "Subscription selection failed" }
az account show --query "{subscription:name,user:user.name}" --output table
if ($LASTEXITCODE -ne 0) { throw "Account lookup failed" }
$env:ARM_SUBSCRIPTION_ID = $subscription
$env:ARM_TENANT_ID = az account show --query tenantId --output tsv
if ($LASTEXITCODE -ne 0 -or -not $env:ARM_TENANT_ID) { throw "Tenant lookup failed" }
$env:ARM_USE_CLI = "true"
$env:ARM_USE_AZUREAD = "true"
$blocked = @(
  "ARM_CLIENT_ID", "ARM_CLIENT_SECRET", "ARM_CLIENT_CERTIFICATE_PATH",
  "ARM_CLIENT_CERTIFICATE_PASSWORD", "ARM_OIDC_TOKEN", "ARM_OIDC_TOKEN_FILE_PATH",
  "ARM_OIDC_REQUEST_TOKEN", "ARM_OIDC_REQUEST_URL", "ARM_USE_OIDC", "ARM_USE_MSI",
  "ARM_USE_AKS_WORKLOAD_IDENTITY", "AZURE_CLIENT_ID", "AZURE_CLIENT_SECRET",
  "AZURE_CLIENT_CERTIFICATE_PATH", "AZURE_FEDERATED_TOKEN_FILE",
  "ACTIONS_ID_TOKEN_REQUEST_URL", "ACTIONS_ID_TOKEN_REQUEST_TOKEN",
  "ARM_ACCESS_KEY", "ARM_SAS_TOKEN"
)
foreach ($name in $blocked) {
  if ([Environment]::GetEnvironmentVariable($name, "Process")) {
    throw "Unset the conflicting credential variable $name before proceeding"
  }
}
```

Confirm the displayed user and subscription are yours. Unset any pre-existing
service-principal, OIDC, managed-identity, storage key or SAS environment
variables in this shell. The review helper refuses these overrides. Set the
required Terraform variables using the privately configured values:

```powershell
$env:TF_VAR_resource_group_name = "<resource-group>"
$env:TF_VAR_sql_entra_admin_object_id = "<sql-admin-group-object-id>"
$env:TF_VAR_sql_entra_admin_login = "<sql-admin-group-name>"
$env:TF_VAR_budget_alert_email = "<budget-alert-email>"
terraform -chdir=infra init -input=false -reconfigure `
  -backend-config="resource_group_name=<state-resource-group>" `
  -backend-config="storage_account_name=<state-storage-account>" `
  -backend-config="container_name=<state-container>" `
  -backend-config="key=<state-key>" `
  -backend-config="use_azuread_auth=true"
if ($LASTEXITCODE -ne 0) { throw "Terraform initialization failed" }
```

Use a new private directory outside the repository. The ACL removes inherited
access and grants only the current Windows user access. Stop if the ACL command
fails. The saved plan and unredacted output can contain sensitive data.

```powershell
$priv = Join-Path $env:LOCALAPPDATA ("bujo-tfplan-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $priv -ErrorAction Stop | Out-Null
$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
icacls $priv /inheritance:r /grant:r "*${sid}:(OI)(CI)F" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not restrict plan directory permissions" }
$plan = Join-Path $priv "stage1.tfplan"
terraform -chdir=infra plan -input=false -lock-timeout=5m -var-file=prod.tfvars `
  -var="bootstrap_only=true" -target=azurerm_container_app_job.migration -out="$plan"
if ($LASTEXITCODE -ne 0) { throw "Terraform plan failed" }
$reviewedHash = (Get-FileHash -LiteralPath $plan -Algorithm SHA256).Hash
$reviewedCommit = git rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw "Commit lookup failed" }
python .github\scripts\bootstrap_review.py --plan "$plan" --subscription "$subscription" --expected-sha256 "$reviewedHash" --expected-commit "$reviewedCommit"
if ($LASTEXITCODE -ne 0) { throw "Bootstrap plan safety review failed" }
terraform -chdir=infra show -no-color "$plan"
if ($LASTEXITCODE -ne 0) { throw "Terraform plan inspection failed" }
```

The helper requires a clean, committed checkout, records `source_commit` in
the review package, checks the Azure CLI user and selected subscription, blocks
credential overrides, checks the saved plan against the bootstrap resource and
job safety rules, and prints its SHA-256, provider versions, a value-redacted
structural summary, a redacted transcription of `terraform show`, cost
considerations and prerequisites. Its output hides all values and arbitrary keys; it is
**not** the complete plan. Inspect the
full output from `terraform show` privately, including all values and
unknowns. Review the summary before sharing: never publish the saved plan or
unredacted output. The account name is deliberately shown only in the private
account confirmation, not in the summary.

After reviewing the exact saved plan, costs and prerequisites, only the owner
may apply it. Recheck the account and plan hash immediately before apply:

```powershell
python .github\scripts\bootstrap_review.py --plan "$plan" --subscription "$subscription" --expected-sha256 "$reviewedHash" --expected-commit "$reviewedCommit"
if ($LASTEXITCODE -ne 0) { throw "Plan, source commit or identity changed since review; do not apply" }
terraform -chdir=infra apply -input=false "$plan"
if ($LASTEXITCODE -ne 0) { throw "Apply failed; inspect privately and re-plan if needed" }
```

If state or configuration changes, discard the plan and repeat the plan and
review. A saved-plan apply does not ask for another Terraform approval prompt:
the owner explicitly starts it only after review. Remove the specific private
plan files and directory after successful apply or abandonment; do not delete
other files. Remove shell-only variables when finished:

```powershell
Remove-Item -LiteralPath $plan
Remove-Item -LiteralPath $priv
Remove-Item Env:ARM_SUBSCRIPTION_ID, Env:ARM_TENANT_ID, Env:ARM_USE_CLI, Env:ARM_USE_AZUREAD
Remove-Item Env:TF_VAR_resource_group_name, Env:TF_VAR_sql_entra_admin_object_id, Env:TF_VAR_sql_entra_admin_login, Env:TF_VAR_budget_alert_email
git status --short
```

The owner then performs the [SQL directory and grant steps](infrastructure.md#first-deployment-and-identity-configuration).
If a temporary SQL firewall rule is needed for the owner's workstation, the
owner creates it for their IP and removes it immediately after completing the
grants. Do not start the job or run migrations during bootstrap.

### OIDC-authenticated alternative

The owner may instead use an OIDC-authenticated session for the deployment
identity (for example a short-lived federated token), with the backend and
variables configured as above. The same helper reviews the plan with
`--auth-mode oidc`: it requires the exact client, subscription and tenant
(`ARM_CLIENT_ID`, `ARM_SUBSCRIPTION_ID`, `ARM_TENANT_ID`), refuses persistent
credentials (client secrets, certificates, storage keys, SAS), and uses the
same plan checker, redaction, clean-commit and `source_commit` rules as the
local path. Choose an absolute `BOOTSTRAP_PLAN` path outside the repository and
keep the plan and unredacted output private.

```sh
set -euo pipefail
terraform -chdir=infra plan -input=false -lock-timeout=5m -var-file=prod.tfvars \
  -var='bootstrap_only=true' \
  -target=azurerm_container_app_job.migration \
  -out="$BOOTSTRAP_PLAN"
REVIEWED_SHA256="$(sha256sum "$BOOTSTRAP_PLAN" | cut -d' ' -f1)"
REVIEWED_COMMIT="$(git rev-parse HEAD)"
python3 .github/scripts/bootstrap_review.py --auth-mode oidc --plan "$BOOTSTRAP_PLAN" \
  --subscription "<subscription-id>" --expected-sha256 "$REVIEWED_SHA256" \
  --expected-commit "$REVIEWED_COMMIT"
terraform -chdir=infra show -no-color "$BOOTSTRAP_PLAN"
# Owner only, after private review of the exact saved plan:
python3 .github/scripts/bootstrap_review.py --auth-mode oidc --plan "$BOOTSTRAP_PLAN" \
  --subscription "<subscription-id>" --expected-sha256 "$REVIEWED_SHA256" \
  --expected-commit "$REVIEWED_COMMIT"
terraform -chdir=infra apply -input=false "$BOOTSTRAP_PLAN"
```

The checker rejects deletions/replacements, unexpected managed resources, role
assignments, application changes and an executable migration job. The target
includes the SQL server/database, both identities, the migration job, the
Container Apps environment, Log Analytics and the existing SQL firewall rule.
The job has a manual trigger only and a pinned public placeholder with a
deliberately nonexistent command and no database configuration; creating it
does not start it. Whether Azure accepts the placeholder is unverified (B6): if
it is rejected, stop and ask the owner. Never regenerate a plan after review and
treat it as the same plan. Do not start the job, run migrations, or change the
app in stage one. The owner then performs the SQL grants.

## First-release baseline

The narrow stage-one plan does not authorize a full-stack apply. The routine
gate deliberately rejects API-registration, identity, authorization,
environment, SQL and other infrastructure drift; it does not provision this
missing baseline implicitly or deploy a different placeholder app.

Before the first release, the owner must privately inspect the actual state and
provide a separately reviewed, bounded preparation plan for the missing API
registration/configuration and trusted outputs (`migration_job_id`,
`container_app_id`, `migration_sql_connection_string`,
`runtime_sql_connection_string`). No initial serving-configuration transition
has been verified yet. Preparing it must not start migrations, promote the host,
change app traffic, widen permissions or bypass the routine plan validator.
If those constraints cannot be met within v8, obtain a revised owner-approved
and bound issue plan before implementing that transition. This remains
blocking work on #5/#55, not an instruction to apply the whole stack.

After a verified release exists, separately reviewed owner infrastructure changes use
`TF_VAR_container_image=<currently deployed ghcr.io/cedricmendelin/bujo-ai@sha256:digest>`
and must be applied before the next release, because the gate rejects any other
pending change.

## Migration and promotion gate

`python3 infra/deploy_gate.py` performs one journal-backed release. Its
inputs are `TF_VAR_container_image` (immutable digest), `BUJO_RELEASE_SHA`,
`BUJO_RELEASE_RUN_ID` (CI run), `GITHUB_RUN_ID`/`GITHUB_RUN_ATTEMPT` (this
deployment), `TFSTATE_STORAGE_ACCOUNT`, `TFSTATE_CONTAINER`, `RUNNER_TEMP` and
`BUJO_RELEASE_MANIFEST` (a file directly in `RUNNER_TEMP`). It uses the
existing Azure CLI OIDC login for ARM tokens and the release journal; tokens,
REST bodies, plans, Terraform output and connection strings are never printed.
Errors are categorical and Terraform logs stay in the ephemeral runner
directory.

The journal is the blob `deployments/bujo-ai-prod-release.json` in the state
container (separate from the Terraform state blob), written with ETag
conditions so a concurrent writer fails closed. It stores only release metadata,
execution identity and hashes binding both SQL targets and immutable job
dependencies. Resume/promotion fails if those dependencies change; a matching
image alone is insufficient. Phases and resume behavior:

| Phase | Next run of the **same** release |
| --- | --- |
| `admitted`, `job-updated`, `start-rejected`, `failed` | re-admit and migrate again (no execution is running) |
| `start-requested` | **blocked**: ambiguous start, owner recovery required (any release) |
| `started` | resume polling that exact execution, never start another |
| `migrated` | promote |
| `promoted`, `verified` | re-verify and update journal evidence only; no provisioning or execution |

A different release starts fresh, except that a recorded `started` execution
of the previous release must have finished first.

Steps for a new release:

1. Recheck the release; list every execution page and stop if any execution is
   active or has an unknown status. Only then record `admitted`.
2. Read the trusted `migration_sql_connection_string` output and plan only
   `-target=azurerm_container_app_job.migration`. The saved plan may change
   only the job's image, command, arguments and environment to the release
   digest, `dotnet Bujo.Host.dll --migrate-only`, `DOTNET_ENVIRONMENT` and the
   trusted SQL target (passwordless managed-identity form). Identity,
   environment, timeouts, other resources and SQL outputs must be unchanged;
   any previous SQL target must equal the trusted one; unknown values are
   rejected. A no-op (job already on the release) is valid.
3. Recheck, apply that saved plan, record `job-updated`, recheck again and
   list executions again (detects external starts).
4. Record `start-requested`, then POST the start exactly once. HTTP 200 must
   name an execution of this job. HTTP 202 needs a `Location` on
   `https://management.azure.com` within this resource group (or the
   subscription's `Microsoft.App` operation path): a direct execution path is
   used as is; otherwise the operation is polled with bounded retries until it
   names the exact execution. 400/401/403/404/409 are a definitive rejection
   (`start-rejected`). Anything else is ambiguous: no retry, no
   latest-by-time guess. Reads retry 408/429/5xx/network errors with capped
   `Retry-After`.
5. Record `started` with the execution and poll it with bounded reads. Only
   `Succeeded` with the exact release image, migration command and SQL target counts;
   `Failed`, `Stopped` or `Degraded` record `failed`. A timeout or unknown
   status leaves `started` so the next run resumes the same execution.
6. Record `migrated`, read the trusted `runtime_sql_connection_string`, and
   run a full plan. It may change only the app's image (and add
   `Deployment__VerifyDatabaseOnStart=true`): the job must be a no-op, every
   other resource and both SQL outputs unchanged, env/scale/resources
   identical, and the AVM PATCH must target the same app. Recheck, apply,
   record `promoted`.
7. Wait boundedly for startup and verify: the app is `Succeeded`, the latest revision is also the latest
   ready revision and runs exactly the release image with the startup check,
   the revision is active, provisioned and healthy, ingress sends all traffic
   to it, the runtime SQL target matches, `/health/live` succeeds,
   anonymous `/api/journal` returns 401 (any other non-5xx fails), and the
   revision is unchanged afterwards. Record `verified`.

With `Deployment__VerifyDatabaseOnStart=true` the host checks all expected
migrations and runtime data access before serving, so a revision that cannot
use the migrated schema does not become ready.

### Ambiguous start recovery (owner only)

If a run stops with an ambiguous start, inspect the job's executions in the
Azure portal privately. Then, with the same release inputs as the stopped run
and an Azure CLI login that has the deployment identity's access:

```sh
export BUJO_OWNER_RECOVERY=true
# An execution started by that request exists and runs the release image:
python3 infra/deploy_gate.py recover-start --execution "<execution-name>"
# Or: nothing was started and no execution is active:
python3 infra/deploy_gate.py recover-start --no-execution
```

Recovery checks that the journal records `start-requested` for this release,
then records `started` (after verifying the execution's image) or
`start-rejected` (after verifying no execution is active). It never starts,
updates or promotes. Rerun the deployment with `workflow_dispatch` and the
same CI run ID; it resumes from the recorded phase.

### Evidence gap: HTTP 202 correlation

Microsoft documents the job [start operation](https://learn.microsoft.com/en-us/rest/api/resource-manager/containerapps/jobs/start?view=rest-resource-manager-containerapps-2024-03-01)
returning 200 with the execution or 202 with `Location`. Whether the live
operation document names the execution has not been observed. If it does not,
the gate fails closed as ambiguous and the owner recovery above applies. Live
execution GET responses are assumed to include `properties.template.containers`;
if not, the gate refuses to promote.

## Schema rollout and recovery

The migration job runs `Bujo.Host.dll --migrate-only` from the release image
with a separate managed identity, no automatic retries and a 30-minute replica
timeout. The web host never migrates at startup. Migrations must be additive
and backward-compatible so the current revision keeps serving while the
migration runs and if promotion fails.

If migration fails, inspect the execution logs privately, fix the cause (for
example a missing SQL grant) and rerun with `workflow_dispatch`. Never start a
parallel execution. A failure before promotion leaves the current revision
untouched. A failure after promotion does not restore the previous image or
reverse schema changes: the app uses single-revision mode, so recover by
releasing a fixed commit through `main`, or through an owner-reviewed infrastructure apply with the
previously verified digest. If database restoration is required, follow the
Azure SQL point-in-time restore guidance in the
[technology baseline](adr/0001-technology-baseline.md).

## Verification boundary

Pull-request CI runs the gate's unit tests with simulated ARM responses,
journal, Terraform plans and probes, plus `terraform validate` and the mocked
Terraform tests. No PR check authenticates to Azure. Not yet evidenced live
(owner, #55): OIDC subject and resource-group/state-container roles (including
Storage Blob Data Contributor for the journal), Graph `Application.ReadWrite.OwnedBy`
and ownership, SQL principals and grants, `API_ALLOWED_PRINCIPAL_OBJECT_IDS`,
anonymous GHCR pull, placeholder acceptability (B6), initial baseline preparation, delegated
consent, 202 correlation and owner smoke tests of persistence and negative
authorization.
