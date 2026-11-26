# Production deployment

Application releases are automated but **disabled by default**. After a
successful CI run for a push to `main`, `.github/workflows/deploy.yml` deploys
that exact build, provided the owner has finished the one-time setup in
[#55](https://github.com/cedricmendelin/bujo-ai/issues/55) and set both
activation variables. Infrastructure applies are independent of those flags.
Bujo-AI is a demo for testing agent capabilities and SDLC practices:
trusted `main` changes are the authorization for infrastructure automation,
with no separate Terraform approval step.

The journal export and recovery procedures are documented in
[operations.md](operations.md). They do not authorize deployment or live
database changes.

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
   rechecks admission, signs in with GitHub OIDC, and initializes remote
   state. It applies pending `infra/` changes before checking application
   activation and readiness, then runs `python3 infra/deploy_gate.py`.
4. **Gate (`infra/deploy_gate.py`).** Described below. It writes nothing unless
   `BUJO_RELEASE_VALIDATED=true` and `BUJO_DEPLOYMENT_ENABLED=true`, and reruns
   `release_admission.py recheck` immediately before each cloud write (job
   update, job start, app promotion), so a newer main release or a replaced
   attempt stops an older application release. These checks do not gate the
   separate full infrastructure plan/apply.

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

## Infrastructure plan and apply

<a id="infrastructure-first-bootstrap"></a>

- **Automatic:** after successful main-push CI, **Deploy production** runs a
  full Terraform plan and applies that saved plan if the `infra/` tree differs
  from the last successful infrastructure apply. The first run with no marker
  also applies. After apply, the workflow refreshes Terraform state and
  verifies that no diagnostic-setting drift remains before advancing the
  marker. The marker lives beside the private state, advances only on
  success, and lets a newer app-only release pick up a pending infra change.
- **On demand:** the owner can run **Provision infrastructure** on `main` at
  any time (for example, to reconcile drift with no source change). The workflow
  always plans and applies in one run. There is no issue approval, plan run ID,
  or hash to enter. Both workflows use the same state and concurrency group.

These applies retain the current app and migration job images. If the migration
job has not yet received a release, it stays inert. They do not start a migration,
promote an image, or require the application activation flags. A failed plan or
apply fails the run and stops the application release. Terraform may have
partially applied changes before an error; fix the issue and rerun. On-demand
provisioning does not start an application release: retry **Deploy production**
with the successful CI run ID for current `main` if one was blocked.

Terraform can replace or delete resources as part of a merged change. Review
resource and cost implications in the PR; there is no second approval at apply
time. No saved plan or raw plan output is published to Actions. If a failure
needs deeper diagnosis, inspect a plan privately using the documented state
backend and production inputs. Never attach plans, state, or unredacted logs to
issues or PRs.

### Monitoring changes

Infrastructure changes merged to `main` can be applied automatically by the
existing workflow, independently of the application activation flags. Review
monitoring resources, diagnostic categories, privacy, permissions, and current
cost before authorizing a merge. The owner-approved prompt/response capture
exception remains disabled until its separate live protection checks pass; see
the [protected prompt telemetry release gate](operations.md#protected-prompt-telemetry-release-gate).
Do not treat an Application Insights resource or a successful apply as evidence
that protected content routing is safe or that Foundry inference is enabled.

## First-release baseline

The infrastructure baseline provisions API registration/configuration
and the trusted outputs (`migration_job_id`, `container_app_id`,
`migration_sql_connection_string`, `runtime_sql_connection_string`) before
manual app setup. It retains the image observed on the existing app, without
starting the inert job or deploying an unmigrated Bujo image. Hosting
configuration changes can leave that app unhealthy; record that separately.

### PWA BFF client registration

The Terraform apply provisions a
dedicated single-tenant PWA web client and service principal. It registers
the Container App HTTPS `/signin-oidc` callback and
`https://localhost:7168/signin-oidc` for Aspire. The client requests only the
API's delegated `journal.access` scope; it is not an MCP client.

Terraform configures the Container App with the PWA client ID, delegated scope,
and runtime managed identity client ID. The Entra app trusts that identity's
principal ID to authenticate the authorization-code exchange. Production
does not require a client secret, Key Vault access, or private networking.
For local development, use a separate credential stored in
[.NET user secrets](development.md), never in Terraform, GitHub, or source.
Admin consent for `journal.access` and assignment of approved users to the API's
`JournalUser` role remain separate owner actions. The API's approved-user
object-ID allowlist remains an independent authorization check.

Once manual configuration is complete and the infrastructure is applied, enable the application-release
flags. The first release then updates and runs the migration job before
promoting the exact tested Bujo image. Unknown execution correlation still
fails closed. Live infrastructure and application acceptance remain separate
blocking evidence on #5/#55; local mock plans are not a production deployment.

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
timeout. Both the migration job and web host use the SqlClient Azure extension
for Microsoft Entra managed-identity authentication. The web host never
migrates at startup. Migrations must be additive
and backward-compatible so the current revision keeps serving while the
migration runs and if promotion fails.

If migration fails, inspect the execution logs privately. The job emits a
console start marker and either a completion marker or a categorical failure
for handled errors (including the SQL error number when available). If the
console is empty, check `ContainerAppSystemLogs_CL` in the environment's
Log Analytics workspace for `JobName_s` and `ExecutionName_s`; it can show
image pull and container termination events even without console output.
Fix the cause (for example a missing SQL grant) and rerun with
`workflow_dispatch`. Never start a
parallel execution. A failure before promotion leaves the current revision
untouched. A failure after promotion does not restore the previous image or
reverse schema changes: the app uses single-revision mode, so recover by
releasing a fixed commit through `main`. If database restoration is required, follow the
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
