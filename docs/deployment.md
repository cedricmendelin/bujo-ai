# Production deployment

Production deployment is manually dispatched by the repository owner from
`main` using `.github/workflows/deploy.yml`. The workflow builds an image tagged
with the commit SHA, uses GitHub OIDC for Azure access, and serializes Terraform
deployments. Do not use this workflow for stage-one bootstrap. See
[production infrastructure](infrastructure.md) for the owner-managed bootstrap
and identity prerequisites.

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
python .github\scripts\bootstrap_review.py --plan "$plan" --subscription "$subscription" --expected-sha256 "$reviewedHash"
if ($LASTEXITCODE -ne 0) { throw "Bootstrap plan safety review failed" }
terraform -chdir=infra show -no-color "$plan"
if ($LASTEXITCODE -ne 0) { throw "Terraform plan inspection failed" }
```

The helper checks the Azure CLI user and selected subscription, blocks
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
python .github\scripts\bootstrap_review.py --plan "$plan" --subscription "$subscription" --expected-sha256 "$reviewedHash"
if ($LASTEXITCODE -ne 0) { throw "Plan or identity changed since review; do not apply" }
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

The owner may instead use an existing OIDC-authenticated Terraform session,
with backend and variables configured as in the infrastructure guide. Confirm
the deployment identity and OIDC trust have the required access. The
PowerShell review helper above is specific to the local user-login path; for
this alternative use the plan checker below and review the exact saved plan
privately.

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
