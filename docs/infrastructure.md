# Production infrastructure

> Automatic releases stay disabled until the owner completes
> [#55](https://github.com/cedricmendelin/bujo-ai/issues/55) and sets
> `BUJO_OWNER_SETUP_CONFIRMED` and `BUJO_DEPLOYMENT_ENABLED` (see
> [production deployment](deployment.md#activation-owner-only)). Grants,
> consent, the migration bootstrap placeholder (B6), initial serving configuration and the first live release are not
> yet evidenced; production still runs the legacy quickstart image.

The `infra/` Terraform root provisions one production environment in Switzerland
North. It uses pinned Azure Verified Modules, an owner-created resource group,
Azure Blob remote state, and one GitHub Actions OIDC deployment identity. The
same identity manages the Entra API registration using a separately consented,
least-privilege Microsoft Graph application permission. Pull request workflows
do not authenticate to Azure; the owner runs plans locally.

Terraform creates a consumption Container Apps environment and app, a
user-assigned runtime identity, a separate migration identity and manually
triggered Container Apps Job, an Entra API registration and service principal,
a capped Log Analytics workspace, a standard RBAC-enabled Key Vault, an
Entra-only Azure SQL server and free-offer database, and a monthly
resource-group budget. The SQL logical server has a system-assigned identity
for directory lookups. There are no VNets, private endpoints, NAT, ACR, SQL
passwords, or Terraform-managed Key Vault values.

The app resource group `bujo-ai-rg` must be created by the owner before the
first deployment. Terraform looks it up as data rather than managing it. The
owner reviews the exact infrastructure-first plan and authorizes its saved hash;
the owner-only workflow or local helper can then provision the API/hosting baseline
before application-specific manual setup. Afterwards each routine release first moves
only the migration job to the release digest, runs one correlated execution,
and only after it succeeds promotes the app image, then verifies the exact
revision, the startup database check, `/health/live` and an anonymous 401 (see
[production deployment](deployment.md#migration-and-promotion-gate)). The app's
external ingress forwards to container port 8080, with 0.25 vCPU/0.5 GiB and
zero to one replica. Releases use only the CI-built
`ghcr.io/cedricmendelin/bujo-ai@sha256:<digest>` image; `container_image` must
be such a digest. During the explicitly reviewed `bootstrap_only=true` baseline,
the existing app image is read live and retained, and the migration job remains
inert. No new app placeholder or real-release quickstart fallback is introduced.
Application health may be incomplete while the owner configures manual setup.

## Resources and modules

| Resource | Implementation |
| --- | --- |
| User-assigned managed identity | `Azure/avm-res-managedidentity-userassignedidentity` |
| Migration identity | `Azure/avm-res-managedidentity-userassignedidentity`, separate from runtime identity |
| Log Analytics workspace | `Azure/avm-res-operationalinsights-workspace`, PerGB2018, 0.1 GB/day cap, 30-day retention |
| Container Apps environment | `Azure/avm-res-app-managedenvironment`, default Consumption profile |
| Container app | `Azure/avm-res-app-containerapp`, public ingress, scale to zero, UAMI attached |
| API registration | AzureAD provider, single-tenant delegated `journal.access` scope, assignment-required service principal |
| Migration job | AzureRM Container Apps Job, manual trigger, migration identity, no retries |
| Key Vault | `Azure/avm-res-keyvault-vault`, Standard, RBAC, 7-day soft delete, purge protection, public endpoint |
| SQL server | `Azure/avm-res-sql-server`, TLS 1.2, Entra-only group admin, server identity, Azure-services firewall rule |
| SQL database | AzureRM plus AzAPI free-offer property update |
| Monthly budget | AzureRM resource-group budget, $50 with forecast/actual alerts |
| Terraform state | Owner-bootstrapped Azure Storage container, remote backend with Entra RBAC |

All Terraform modules use exact versions in their `source` blocks. Provider
requirements are constrained in `infra/versions.tf` and selected provider
versions are recorded in `infra/.terraform.lock.hcl`.

## Non-AVM justification

| Resource or operation | Reason |
| --- | --- |
| Resource group lookup | The owner pre-creates the app resource group so deployment needs no subscription-scope Contributor. A Terraform data source references it without managing it. |
| `azurerm_container_app_job` | The workload needs a separately triggered one-shot job with its own identity and execution lifecycle; it is separate from the Container App module's app resource. |
| `azapi_resource` for the SQL database | The SQL-server AVM does not expose the database free-offer settings, and AzureRM 4.81.0 has no free-limit attributes. Azure only accepts `useFreeLimit` when a database is created ("Cannot update paid database to free database"), so AzAPI creates the serverless database with `useFreeLimit` and `freeLimitExhaustionBehavior = AutoPause` in one request. |
| `azurerm_consumption_budget_resource_group` | No `avm-res-consumption-budget` module is published in the Terraform Registry. |
| State storage | The storage account and private state container are owner-bootstrapped (#24), avoiding a bootstrap dependency on Terraform state itself. |

## Cost estimate

Indicative monthly costs for Switzerland North, excluding Microsoft Foundry.
Prices depend on Azure billing currency, use, and current free grants; verify
them with the Azure pricing calculator before provisioning. Idle assumes no
Container Apps replicas and negligible Key Vault operations. Expected personal
use assumes activity remains within documented free grants.

| Resource | Idle estimate | Expected personal-use estimate |
| --- | ---: | ---: |
| Resource group and managed identity | $0 | $0 |
| Container Apps consumption | $0 | $0–$4 |
| Azure SQL serverless, free offer, auto-pause | $0 | $0–$3 |
| Log Analytics, maximum 0.1 GB/day | $0 | $0–$1 |
| Standard Key Vault | $0 | $0–$1 |
| Monthly Azure budget | $0 | $0 |
| State storage, Standard_LRS | <$1 | <$1 |
| **Estimated total** | **<$1/month** | **<$10/month** |

The estimates leave at least $40/month of headroom under the $50 budget. The
budget is scoped to the production resource group (not the separate state
resource group), alerts at 80% forecast spend and 100% actual spend, and emails
the configured `BUDGET_ALERT_EMAIL`. Container Apps and SQL can incur costs if
usage exceeds free grants or the SQL free offer is unavailable. The SQL offer
is limited by Azure eligibility and quota; monitor usage and billing after
deployment. The budget is an alert, not a spending cap.

## Manual prerequisites (#24)

The owner completes these steps once before using the deployment workflow.
Never put real Azure IDs, secrets, or the real SQL group name in source or this
guide.

### Azure resources and state

1. Sign in to Azure and select the target subscription.
2. Create the production resource group `bujo-ai-rg` in Switzerland North.
3. In a separate bootstrap resource group, create a Standard_LRS StorageV2
   account and a private Blob container named `tfstate`. Require HTTPS/TLS 1.2
   or later, disable public blob access and shared-key access, and enable Blob
   versioning. The state blob key defaults to `prod.tfstate`. Do not provision
   this state account through the Terraform stack.
4. Confirm the storage container and resource group exist before running
   `terraform init`. Preserve prior blob versions so state can be restored.

### One immutable-subject deployment application

1. Enable the repository's GitHub immutable OIDC subject format and use the
   GitHub-provided numeric owner and repository IDs when constructing the
   federated credential. Never copy those real IDs into this repository.
2. Create exactly one Microsoft Entra app registration and service principal
   for deployment. Do not create a client secret or certificate.
3. Add exactly one federated credential:
   * Issuer: `https://token.actions.githubusercontent.com`
   * Audience: `api://AzureADTokenExchange`
   * Subject: `repo:<owner>@<owner-id>/<repo>@<repo-id>:environment:prod`
4. Create the GitHub Actions environment `prod`, restrict deployment branches
   to `main`, and configure branch protection. The environment-bound apply
   workflow receives the immutable `environment:prod` subject.
5. Grant the deployment service principal only:
   * **Contributor** on `bujo-ai-rg`
   * **Storage Blob Data Contributor** on the `tfstate` container only (state
     and the release journal blob `deployments/bujo-ai-prod-release.json`)

Contributor does not include Azure RBAC role-assignment write actions or
Key Vault data actions. Do not grant the deployment identity Owner, User Access
Administrator, Role Based Access Control Administrator, or any Key Vault
data-plane role. Terraform contains no Azure RBAC role assignments. The owner
performs the runtime Key Vault role grant after the first apply (see below).

### Microsoft Graph permission for API registration

The AzureAD Terraform provider creates the API application and service
principal. Grant the deployment service principal the Microsoft Graph
**Application.ReadWrite.OwnedBy** application permission and admin consent it
in the target tenant. The Terraform-created application and service principal
list that deployment principal as an owner. The pinned AzureAD 3.10.0 provider
documents this permission for both resources when the Terraform principal owns
them; `infra/api.tf` resolves `deployment_client_id` to the intended deployment
service principal and sets it as owner on each, including when a local user
creates the baseline. Provisioning and release workflows pass `AZURE_CLIENT_ID`
through `TF_VAR_deployment_client_id`; this avoids changing ownership when
switching from local bootstrap to OIDC. This verifies the provider's
documented permission contract, not a live tenant apply. Do not grant
`Application.ReadWrite.All`; the deployment identity otherwise retains only
its resource-group and state-container roles described above.

### GitHub Actions variables

Add these as repository Actions variables (not secrets); actual IDs stay in
GitHub settings and are never committed:

| Variable | Value |
| --- | --- |
| `AZURE_TENANT_ID` | Entra tenant ID |
| `AZURE_SUBSCRIPTION_ID` | Subscription containing the app RG and state storage |
| `AZURE_CLIENT_ID` | Client/application ID of the single deployment app |
| `AZURE_RESOURCE_GROUP` | `bujo-ai-rg` |
| `TFSTATE_RESOURCE_GROUP` | Resource group containing the state account |
| `TFSTATE_STORAGE_ACCOUNT` | State storage account name |
| `TFSTATE_CONTAINER` | Private state container, normally `tfstate` |
| `TFSTATE_KEY` | State blob name, normally `prod.tfstate` |
| `SQL_ENTRA_ADMIN_OBJECT_ID` | Object ID of the Entra SQL administrator security group |
| `SQL_ENTRA_ADMIN_LOGIN` | Display name of that Entra security group |
| `API_ALLOWED_PRINCIPAL_OBJECT_IDS` | Comma-separated API account object IDs; can be absent for initial IaC (deny all), required before application activation |
| `BUDGET_ALERT_EMAIL` | Address to receive Azure budget alerts |
| `BUJO_OWNER_SETUP_CONFIRMED` | `true` only after the owner completes the #55 checklist; an explicit attestation, not automatic permission proof |
| `BUJO_DEPLOYMENT_ENABLED` | `true` only once setup is verified; enables automatic releases (unset to stop) |

Create an Entra security group for the SQL administrator, add the owner account
as a member, and use the group's object ID and display name for the two SQL
variables. The SQL module accepts the group object ID and display name; Azure
resolves them as the Entra group administrator. No actual group name or ID
belongs in source control.

In `infra/prod.tfvars`, change `key_vault_name` to a globally unique name if the
example name is already in use. The name must be 3–24 letters, numbers, or
hyphens, start with a letter, and contain no consecutive hyphens.

The API registration exposes the delegated `journal.access` scope and a
`JournalUser` app role. Its service principal requires users to be assigned
before Entra issues tokens. After the first successful deployment, assign each
approved user or group to the API enterprise application with the `Journal
user` role. The API also checks the configured tenant/object-ID allowlist and
the `journal.access` scope. A client application must request that delegated
scope and receive delegated-permission consent; use tenant admin consent if
user consent is disabled. User assignment to this API and scope consent are
independent requirements. The API registration does not create or configure a
client application. If a separate client application's service principal also
requires assignment, users must be assigned to that client independently; this
Terraform configuration does not set that client-side requirement.

## Owner-run local plans

PR checks only run non-Azure Terraform formatting and validation. They do not
have `id-token: write`, Azure credentials, or access to the remote state. Before
approving an infrastructure PR, the owner runs a plan locally:

1. Sign in using the owner account with `az login` and select the subscription.
   The owner needs Reader on `bujo-ai-rg` and Storage Blob Data Reader on the
   `tfstate` container.
2. From the repository root, initialize the backend with the owner-provisioned
   values:

   ```sh
   terraform -chdir=infra init \
     -backend-config="resource_group_name=<state-resource-group>" \
     -backend-config="storage_account_name=<state-account>" \
     -backend-config="container_name=tfstate" \
     -backend-config="key=prod.tfstate"
   ```
3. Supply the SQL group object ID/display name, API allowlist, and budget alert
   email as `TF_VAR_sql_entra_admin_object_id`, `TF_VAR_sql_entra_admin_login`,
   `TF_VAR_auth_allowed_principal_object_ids`, `TF_VAR_budget_alert_email`, and
   the resource group as `TF_VAR_resource_group_name` in the local shell, then
   run:

   ```sh
   terraform -chdir=infra plan -lock=false -var-file=prod.tfvars
   ```

Do not post plan files or state blobs in pull requests. The approved PR may be
merged after the owner has reviewed its local plan.

## First deployment and identity configuration

Releases run automatically from `deploy.yml` after a successful CI push run on
`main`, once activated; a manual dispatch accepts only a previous successful
main CI run ID for recovery. The workflow admits the exact CI release before
Azure access, authenticates through the single immutable-subject OIDC identity,
serializes on `terraform-prod`, and runs `infra/deploy_gate.py`. The gate only
updates the migration job and the app image; any other pending infrastructure
change blocks the release and must be applied by the owner first (with the
currently deployed digest as `container_image`). Live OIDC authorization
remains untested until the owner's first release.

If SQL cannot resolve a managed identity while creating its database principal,
the tenant administrator must inspect the required directory lookup permissions.
Terraform enables a system-assigned identity on the server, but does not manage
Microsoft Entra role assignments. Grant only the SQL server identity's necessary
lookup permissions, using Microsoft's least-privilege guidance; do not assume
the broad **Directory Readers** role is always required.
This permission is not granted to the runtime, migration, or deployment
identity. Get the server principal ID from
`terraform -chdir=infra output -raw sql_server_identity_principal_id`.

The owner-reviewed infrastructure baseline comes first. Follow the
[infrastructure-first plan and review procedure](deployment.md#infrastructure-first-bootstrap), then these
grants and the [application baseline](deployment.md#first-release-baseline); do not
activate releases until all grants and other prerequisites are complete. Also grant
the deployment application the Microsoft Graph permission described above.

For the owner's local grant session, Azure SQL must be reachable from the
workstation. If needed, the owner temporarily permits their public IP in the SQL
server firewall and removes that rule immediately after the grants. No firewall
change or grant is automated. With the `SqlServer` PowerShell module installed,
the owner can connect using an Entra token (never a SQL password):

```powershell
az account set --subscription "<subscription-id>"
if ($LASTEXITCODE -ne 0) { throw "Subscription selection failed" }
$token = az account get-access-token --resource "https://database.windows.net/" --query accessToken --output tsv
if ($LASTEXITCODE -ne 0 -or -not $token) { throw "SQL token acquisition failed" }
Invoke-Sqlcmd -ServerInstance "<sql-server-fqdn>" -Database "<database>" `
  -AccessToken $token -Query "SELECT ORIGINAL_LOGIN() AS login_name"
```

Use the same token authentication to run the following statements, inspect the
membership results, and run `Remove-Variable token` afterward. The owner
must be the configured SQL administrator.

Connect to Azure SQL as a member of the configured Entra admin group and create
the database principals and roles:

```sql
CREATE USER [<runtime-identity-name>] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [<runtime-identity-name>];
ALTER ROLE db_datawriter ADD MEMBER [<runtime-identity-name>];

CREATE USER [<migration-identity-name>] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [<migration-identity-name>];
ALTER ROLE db_datawriter ADD MEMBER [<migration-identity-name>];
ALTER ROLE db_ddladmin ADD MEMBER [<migration-identity-name>];

SELECT role_principal.name AS role_name, member_principal.name AS member_name
FROM sys.database_role_members AS membership
JOIN sys.database_principals AS role_principal
  ON role_principal.principal_id = membership.role_principal_id
JOIN sys.database_principals AS member_principal
  ON member_principal.principal_id = membership.member_principal_id
WHERE member_principal.name IN (N'<runtime-identity-name>', N'<migration-identity-name>')
ORDER BY member_principal.name, role_principal.name;
```

Get the names with `terraform -chdir=infra output runtime_identity_name` and
`terraform -chdir=infra output migration_identity_name`. Runtime identity
permissions are limited to data access; only the one-shot migration job uses
the schema-change identity. Repeat this setup if the database or either
identity is replaced. Before rerunning the statements, inspect existing users
and memberships; `CREATE USER` is not idempotent. Verify two rows for the
runtime identity (`db_datareader`, `db_datawriter`) and three for the
migration identity (the same two plus `db_ddladmin`). Until these steps are
complete, the workflow stops at the migration gate and leaves the application
revision unchanged.

After the first successful apply, grant the runtime identity access to the
workload vault. Terraform intentionally does not create this role assignment:

```sh
az role assignment create \
  --assignee-object-id <runtime-identity-principal-id> \
  --assignee-principal-type ServicePrincipal \
  --role "Key Vault Secrets User" \
  --scope <key-vault-id>
```

Obtain the placeholders from `terraform -chdir=infra output
runtime_identity_principal_id` and `terraform -chdir=infra output key_vault_id`.
Verify the grant with:

```sh
az role assignment list --scope <key-vault-id> --all
```

This assignment is owner-managed, not Terraform-managed, so it remains in
place across applies and is not shown as Terraform drift. It is required before
issue #5 introduces Key Vault secret references. Destroying and recreating the
vault requires repeating the assignment. The deployment identity receives no
Key Vault data-plane access.

## Teardown and recovery

Run `terraform destroy -var-file=prod.tfvars` locally using the same backend
configuration and an identity with sufficient rights on the app resource
group and state. Review the destroy plan first. Terraform does not delete the
owner-created app resource group or state storage. Key Vault soft delete is
seven days and purge protection is enabled; a deleted vault name remains
reserved during recovery. Use a new vault name if recreating before purge.
Repeat the runtime role assignment and SQL principal setup after recreating
resources. Remove state storage and its bootstrap resource group only after
production resources are gone and restore/audit needs have been met. Restore a
prior state version from Blob versioning if state is corrupted.

## Operations and accepted trade-offs

- Networking is public as required; SQL and Key Vault require Entra/RBAC access.
- The production resource group and the backend storage are separate. State
  uses Azure AD authentication and OIDC in GitHub; no keys or SAS are used.
- A repository rename or transfer requires updating the immutable federated
  subject. A failed manual role grant does not block the first deployment
  because the app has no vault secret references yet.
- Schema changes run through the production migration job before an app
  revision is promoted. The app process never applies migrations at startup;
  local `--migrate` remains Development-only.
- GHCR is expected to be public. Do not add a registry password to Terraform;
  revisit managed-identity image pulls before making the image private.
