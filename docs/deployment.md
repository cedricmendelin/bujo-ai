# Production deployment

Production releases are automated but **disabled by default**. After a
successful CI run for a push to `main`, `.github/workflows/deploy.yml` deploys
that exact build, provided the owner has finished the one-time setup in
[#55](https://github.com/cedricmendelin/bujo-ai/issues/55) and set both
activation variables. Until then the application-release workflow stops before
any Azure write. The separate owner-only **Provision infrastructure** workflow
does not require completed application setup or either activation flag.
Production currently still runs the legacy quickstart image; no application
release has been evidenced live yet.

This is a partial delivery. Plan v9 separates **IaC provisioning -> manual
application setup -> application release**. Provisioning includes the API
registration and hosting baseline, retaining the existing app image; an
unhealthy app is an accepted intermediate state, not a successful Bujo release.
Routine-release safety checks still reject infrastructure/authorization drift.
Use the reviewed [infrastructure-first path](#infrastructure-first-bootstrap),
not an unrestricted apply or a weaker release gate.

## Release pipeline

1. **Build once (CI).** CI builds and smoke-tests the root `Dockerfile` image
   once. For main pushes only, a separate publish job pushes it to GHCR and
   records the immutable digest with its provenance (source SHA, CI run ID,
   attempt and Linux/amd64 configuration digest read from the saved tested
   image archive) in a release manifest artifact. Pull requests get no publish or
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

## Infrastructure-first bootstrap

After this code is merged, run **Provision infrastructure** on `main` as the
repository owner. It uses the same scoped `prod` OIDC identity and existing
private state backend as application releases, and shares `terraform-prod`
serialization without cancellation. PRs and non-owner dispatches cannot run it.
It validates the live v9 binding, exact merged source, target subscription,
deployment-principal ownership, retained live image, inert migration job,
absence of unfinished/ambiguous releases, and the bounded saved plan.
The configured application resource group must contain exactly one existing
Container App; an absent/ambiguous app or image stops planning rather than
substituting a placeholder. Active/unknown executions of any job in that group
also block provisioning. The state container's actual private access is checked
before saving plans or diagnostics; configuration presence alone is insufficient.

The privacy check queries `{publicAccess:properties.publicAccess}` so Azure CLI
returns a JSON object even when the container is private. A scalar query for the
private container's null access setting can return empty output instead of JSON.
Only an explicit null `publicAccess` field is accepted; public access, malformed
responses and failed inspections still block provisioning.

Pipeline initialization uses `-lockfile=readonly` to preserve the committed
provider selections and checksums. The clean-checkout review guard still rejects
any tracked modification or untracked source file. When intentionally updating
providers, use the pinned Terraform version to initialize modules and run
`terraform -chdir=infra providers lock -platform=linux_amd64 -platform=windows_amd64`,
then review and commit the resulting lockfile. Do not ignore or restore a changed
lockfile during a provisioning run.

Only Azure/Graph/state access and private infrastructure inputs are needed now.
The deployment principal must already be able to create/manage Entra
applications; the workflow cannot grant its own Graph permission. Configure
`TFSTATE_STORAGE_ACCOUNT` for the existing backend if absent. SQL users/grants,
caller consent/assignment, `API_ALLOWED_PRINCIPAL_OBJECT_IDS`, a published Bujo
image, application health and the release activation flags are **not**
provisioning prerequisites. An initially empty allowlist denies everyone.
Infrastructure planning explicitly overrides the allowlist to empty even when
`API_ALLOWED_PRINCIPAL_OBJECT_IDS` is already configured for later application
setup. The configured release value is preserved; no environment variable needs
to be removed to provision safely.

For a newly created migration identity, AzureRM can mark the job's entire
`identity_ids` set unknown rather than an individual element. Baseline validation
accepts either form only with the approved migration identity ID dependency.
Unknown identity types, execution settings and unrelated inputs remain blocked.

AzureAD also computes the application's `feature_tags` and the service
principal's `feature_tags`/legacy `features` blocks from Entra tag metadata.
These whole computed blocks are permitted, not arbitrary nested feature inputs
or unknown permissions. Unknown-input errors identify only the reviewed resource,
never private values or arbitrary map keys.

For an existing app, AVM can retain the exact previous template in the main
resource while scheduling the desired environment through its separate PATCH.
Baseline review validates those effective settings only when the old template
is unchanged and a PATCH is scheduled against that same app, or the refreshed
live output proves the no-op PATCH's exact environment is already applied.
The SQL, audience, deny-all authorization, image and inert-job checks still apply.

### Owner-dispatched OIDC plan and apply

1. Start a private plan; this performs no Terraform apply or migration start:

   ```powershell
   gh workflow run provision.yml --ref main -f operation=plan
   ```

2. Read the redacted summary and source/hash from the completed workflow.
   The actual saved plan is **not** an Actions artifact or log: it is stored
   using Entra authentication in the existing private state container as
   `bootstrap/plans/<plan-run-id>/plan.tfplan`, with `review.json` alongside it.
   The reserved `bootstrap/` prefix must not contain the Terraform state key.
   Missing/inaccessible state or release-journal evidence stops provisioning.

3. In a private local terminal, initialize the same backend/providers on that
   exact merged source. Download and inspect the saved plan using the owner
   state-container access. Use a private directory outside the repository with
   owner-only access, as in the local authentication procedure below:

   ```powershell
   $plan = "<private-directory>\baseline.tfplan"
   az storage blob download --auth-mode login `
     --account-name "<existing-state-account>" --container-name "<state-container>" `
     --name "bootstrap/plans/<plan-run-id>/plan.tfplan" --file "$plan" --output none
   if ($LASTEXITCODE -ne 0) { throw "Private plan download failed" }
   terraform -chdir=infra show -no-color "$plan"
   if ($LASTEXITCODE -ne 0) { throw "Private plan inspection failed" }
   $reviewedHash = (Get-FileHash -LiteralPath $plan -Algorithm SHA256).Hash.ToLowerInvariant()
   ```

   Review the full resource/configuration changes, costs and hash privately.
   Never paste unredacted plans, identifiers or connection strings into GitHub.
   A redacted summary alone is not the complete plan review.

4. Only after that review, authorize the exact saved plan:

   ```powershell
   gh workflow run provision.yml --ref main -f operation=apply `
     -f plan_run_id="<plan-run-id>" -f plan_sha256="$reviewedHash"
   ```

   Apply recovers only a successful owner workflow's plan for the exact current
   merged commit, checks the owner-reviewed hash, live scope, image/target,
   migration admission and plan safety again, and applies that saved file.
   If `main`, state, the image or dependencies change, stop and prepare/review a
   new plan; do not silently regenerate it. Use a new plan run rather than
   overwriting a previous run's private files.

5. Inspect the result. A failed Terraform apply remains **failed**, even when
   some infrastructure exists or the owner accepts an unhealthy app. Unredacted
   init/plan/apply diagnostics are stored privately under
   `bootstrap/diagnostics/<run-id>/<attempt>/<phase>.log`, never in public artifacts.
   Reconcile actual state before retrying; no automatic destroy/rollback occurs.
   A successful infrastructure apply does not certify application readiness.

After successful apply, use the private Terraform outputs to finish #55:
runtime/migration SQL principals/grants, conditional SQL directory lookup,
approved client consent and API user assignment/allowlist, and actual image
access. Review any subsequent allowlist/configuration baseline change through
this same plan/apply path **before** activating application releases; the
routine release gate does not absorb authorization changes. The workflow never
grants permissions, starts a migration, or sets either activation flag.
Remove the specific private plan/review/diagnostic blobs and local files when
their review/recovery purpose ends; do not delete the state container.

### Local application of the same reviewed baseline

The local-user authentication, backend setup and owner-only directory procedure
below also work with the infrastructure mode. Use a clean checkout of current
merged `main`, set `TF_VAR_deployment_client_id` to the intended deployment
application's client ID (not the interactive user's object ID), and configure
the existing private state settings. For live issue-scope verification, capture
`gh auth token` into process-only `GH_TOKEN`, and set
`GITHUB_REPOSITORY=cedricmendelin/bujo-ai`; do not print the token.
Use only the authentication/backend/private-directory setup from the legacy
procedure below, not its targeted plan/apply commands. Keep automatic releases
disabled and do not run a local apply concurrently with a deployment workflow.

```powershell
$env:GH_TOKEN = gh auth token
if ($LASTEXITCODE -ne 0) { throw "GitHub authentication failed" }
$env:GITHUB_REPOSITORY = "cedricmendelin/bujo-ai"
$env:TF_VAR_deployment_client_id = "<intended-deployment-client-id>"
$env:TFSTATE_RESOURCE_GROUP = "<state-resource-group>"
$env:TFSTATE_STORAGE_ACCOUNT = "<state-storage-account>"
$env:TFSTATE_CONTAINER = "<state-container>"
$env:TFSTATE_KEY = "<state-key>"
$reviewedCommit = git rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw "Commit lookup failed" }
$plan = "<private-directory>\baseline.tfplan"
python .github\scripts\bootstrap_review.py --mode infrastructure --operation plan `
  --plan "$plan" --subscription "$subscription" --expected-commit "$reviewedCommit"
if ($LASTEXITCODE -ne 0) { throw "Infrastructure planning failed" }
terraform -chdir=infra show -no-color "$plan"
if ($LASTEXITCODE -ne 0) { throw "Private review failed" }
$reviewedHash = (Get-FileHash -LiteralPath $plan -Algorithm SHA256).Hash
# Only after the owner has reviewed the exact plan and costs:
python .github\scripts\bootstrap_review.py --mode infrastructure --operation apply `
  --plan "$plan" --subscription "$subscription" --expected-commit "$reviewedCommit" `
  --expected-sha256 "$reviewedHash"
if ($LASTEXITCODE -ne 0) { throw "Infrastructure apply failed; inspect private diagnostics and state" }
```

Keep the `.plan.log`/`.apply.log` beside the private local plan for diagnosis,
then remove the specific files and temporary token/configuration environment
variables after review. Local and OIDC paths share the same validator and
redaction; neither makes app setup a provisioning prerequisite.

### Diagnosing admission and approval failures

Release admission and infrastructure approval verification report bracketed,
value-redacted failure categories. They remain blocking; a category is not
permission to bypass a check.

| Category | Next investigation |
| --- | --- |
| `github-api-http-401` / `github-api-http-403` | Inspect the failing workflow token's authentication and endpoint permissions, not just local owner access. |
| `github-api-http-404` | Check the requested evidence and token visibility; a 404 alone does not distinguish absence from denied access. |
| `github-api-http-429` / `github-api-http-5xx` / `github-api-network` | Check rate limits or service/network availability before a controlled retry. |
| `event-*` / `ci-run-*` | Verify the actual event, main-push run, source and attempt against the release trust requirements. |
| `artifact-*` / `manifest-binding` / `response-*` | Inspect the exact CI-attempt artifact's availability, download, layout or data binding privately. |
| `approval-labels-missing` / `approval-record-missing` / `approval-*-mismatch` / `approval-plan-edited` | Verify live issue scope with the trusted default-branch approval script before requesting any owner approval action. |
| `approval-configuration` / `command-unavailable` / `command-timeout` / `command-failed` / `approval-verifier` / `inspection-*` / `invalid-input` | Check verifier configuration, tooling or execution. These do not establish that approval is missing. |

The shared bootstrap helper distinguishes command failure from invalid approval.
In particular, failure to execute the verifier or read GitHub evidence must not
be treated as proof that the owner needs to reapply approval. Local verification
with owner credentials does not prove that the Actions token can read the same
evidence. Raw command output, API responses, URLs, credentials and private paths
are not included in these diagnostics; existing private Terraform logs remain
the place to inspect detailed infrastructure failures.

## Legacy targeted job-only bootstrap

<a id="stage-one-bootstrap"></a>

The older narrow profile below remains available for reviewing a job-only plan;
it does **not** provision the complete API/hosting baseline. Prefer the
infrastructure-first procedure above for the first complete IaC setup.

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
terraform -chdir=infra init -input=false -lockfile=readonly -reconfigure `
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

The infrastructure-first v9 baseline provisions API registration/configuration
and the trusted outputs (`migration_job_id`, `container_app_id`,
`migration_sql_connection_string`, `runtime_sql_connection_string`) before
manual app setup. It retains the image observed on the existing app, without
starting the inert job or deploying an unmigrated Bujo image. Hosting
configuration changes can leave that app unhealthy; record that separately.

### PWA BFF client registration

After issue #80 is merged, the owner-reviewed Terraform plan provisions a
dedicated single-tenant web client and service principal for the PWA BFF. Its
redirect is the existing Container App HTTPS FQDN plus `/signin-oidc`. The
client requests only the API's delegated `journal.access` scope. Terraform
does not add outputs for the client ID or redirect URI, and never creates or
stores a client credential. Both non-secret values can be reviewed on the
managed Entra application registration. This registration is not an MCP client
and must not be reused for MCP without the separate compatibility proof.

The owner creates the client secret in Entra and stores it only in the existing
Key Vault; do not put its value in Terraform, state, outputs, GitHub variables
or secrets, or source. Rotate by adding a replacement secret, updating the Key
Vault version, verifying the BFF consumes it, and then deleting the old secret.
Admin consent for `journal.access` and assignment of approved users to the API's
`JournalUser` role remain separate owner actions. The API's approved-user
object-ID allowlist remains an independent authorization check.

Once manual configuration is complete and a fresh reviewed baseline has no
pending authorization/identity/SQL changes, enable the application-release
flags. The first release then updates and runs the migration job before
promoting the exact tested Bujo image. Unknown execution correlation still
fails closed. Live infrastructure and application acceptance remain separate
blocking evidence on #5/#55; local mock plans are not a production deployment.

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
   environment, the Consumption workload profile, timeouts, other resources and
   SQL outputs must be unchanged;
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
