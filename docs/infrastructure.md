# Production infrastructure

> Automatic application releases stay disabled until the owner completes
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
do not authenticate to Azure; merged infrastructure changes are applied by CI/CD.

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
owner can also dispatch **Provision infrastructure** to establish or reconcile
the API/hosting baseline before application-specific manual setup. Each routine release first moves
only the migration job to the release digest, runs one correlated execution,
and only after it succeeds promotes the app image, then verifies the exact
revision, the startup database check, `/health/live` and an anonymous 401 (see
[production deployment](deployment.md#migration-and-promotion-gate)). The app's
external ingress forwards to container port 8080, with 0.25 vCPU/0.5 GiB and
zero to one replica. Releases use only the CI-built
`ghcr.io/cedricmendelin/bujo-ai@sha256:<digest>` image; `container_image` must
be such a digest for releases. During the `bootstrap_only=true` baseline,
the existing app image is read live and retained, and the migration job remains
inert. No new app placeholder or real-release quickstart fallback is introduced.
Application health may be incomplete while the owner configures manual setup.

## Resources and modules

| Resource | Implementation |
| --- | --- |
| User-assigned managed identity | `Azure/avm-res-managedidentity-userassignedidentity` |
| Migration identity | `Azure/avm-res-managedidentity-userassignedidentity`, separate from runtime identity |
| Log Analytics workspace | `Azure/avm-res-operationalinsights-workspace`, PerGB2018, 0.1 GB/day cap, 30-day retention, public network access for ingestion and queries |
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

Terraform enables both Log Analytics public network endpoints without a network
perimeter restriction. This does not grant anonymous access or change workspace
authentication and permissions. Change these settings in Terraform, not in the
portal, to keep them across applies.

## Non-AVM justification

| Resource or operation | Reason |
| --- | --- |
| Resource group lookup | The owner pre-creates the app resource group so deployment needs no subscription-scope Contributor. A Terraform data source references it without managing it. |
| `azurerm_container_app_job` | The workload needs a separately triggered one-shot job with its own identity and execution lifecycle; it is separate from the Container App module's app resource. |
| `azapi_resource` for the SQL database | The SQL-server AVM does not expose the database free-offer settings, and AzureRM 4.81.0 has no free-limit attributes. Azure only accepts `useFreeLimit` when a database is created ("Cannot update paid database to free database"), so AzAPI creates the serverless database with `useFreeLimit` and `freeLimitExhaustionBehavior = BillOverUsage` in one request. |
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
| Azure SQL serverless, free offer (while within monthly free limits) | $0 | $0–$3 |
| Log Analytics, maximum 0.1 GB/day | $0 | $0–$1 |
| Standard Key Vault | $0 | $0–$1 |
| Monthly Azure budget | $0 | $0 |
| State storage, Standard_LRS | <$1 | <$1 |
| **Estimated total** | **<$1/month** | **<$10/month** |

The estimates leave at least $40/month of headroom under the $50 budget only
while usage stays within the applicable free grants. The SQL estimate assumes
that monthly free compute is not exhausted; paid compute overage is not included.
When the monthly SQL free compute allowance is exhausted,
`freeLimitExhaustionBehavior = BillOverUsage` keeps the database accessible and bills
continued compute at paid rates, which can increase costs beyond the estimate,
as documented by Microsoft's [Create or Update API](https://learn.microsoft.com/en-us/rest/api/sql/databases/create-or-update?view=rest-sql-2023-08-01-preview).
This is separate from idle auto-pause: `autoPauseDelay = 60` pauses an otherwise
idle serverless database after 60 minutes, but it does not pause the database
for the rest of the month at free-limit exhaustion. The budget is scoped to the
production resource group (not the separate state resource group), alerts at
80% forecast spend and 100% actual spend, and emails the configured
`BUDGET_ALERT_EMAIL`. Container Apps and SQL can also incur costs if the SQL
free offer is unavailable. The SQL offer is limited by Azure eligibility and
quota; monitor usage and billing after deployment. The budget is an alert, not
a spending cap.

Changing only `freeLimitExhaustionBehavior` changes the AzAPI request body, not
the database identity; the [AzAPI 2.12 resource documentation](https://registry.terraform.io/providers/Azure/azapi/2.12.0/docs/resources/resource)
documents `body` as an input and name/parent changes as replacement triggers.
With the identity unchanged and no replacement triggers configured, Terraform
plans this body change as an in-place update, not a replacement. Azure's
acceptance of changing this property on an existing
free-tier database has not been verified without a live API call. The selected
change does not alter `useFreeLimit`, which Azure only accepts at database
creation. Before any apply, review the actual plan and stop if it proposes a
replacement or Azure rejects the update. A replacement requires owner approval
and a migration plan: export the database (for example, as a BACPAC) or take a
backup, create a new database, restore the data, switch the application
connection, verify operation, then remove the old database. Do not apply a
replacement before that approval and migration plan.

## Optional Microsoft Foundry model provider

`infra/foundry.tf` defines one Azure AI Services account (`AIServices`), one
Foundry project, and the `chat`, `sol`, `astra`, and `embedding` model deployments.
The deployment map is flat and can only use pay-per-token SKUs:
`DataZoneStandard`, `GlobalStandard`, or `Standard`. The production setting is
`foundry_enabled = true` in `infra/prod.tfvars`; the variable default remains
`false`, so other configurations create none of these resources unless enabled.
The owner approved provisioning these model selections in this PR. Issue #128
plan v1 authorizes milestone A only; this follows the owner's later direct
instruction to enable the production configuration. Neither approval authorizes
sending journal data.

Evidence reviewed on 2026-11-18 includes indexed Microsoft catalog, GA,
pricing and privacy excerpts; direct live-page retrieval was blocked. This is
not fresh verification of target-subscription availability, quota or a quote.
In particular, verify the Switzerland North / Data Zone Standard combination
for each version and the Terra Data Zone price before apply. Do not silently
substitute another model or deployment type.

| Purpose | Approved model and version | Catalog / Data Zone Standard availability | Capacity | Public price assumption (USD per 1M tokens) | Fallback if Data Zone Standard is unavailable |
| --- | --- | --- | ---: | --- | --- |
| Chat | `gpt-6.1-terra` / `2026-10-26` | Owner-selected production deployment; Data Zone Standard | 1 | Verify current pricing | No automatic fallback |
| Sol | `gpt-6.2-sol` / `2026-11-17` | Owner-selected production deployment; Data Zone Standard | 1 | Verify current pricing | No automatic fallback |
| Astra | `gpt-6.2-astra` / `2026-11-17` | Owner-selected production deployment; Data Zone Standard | 1 | Verify current pricing | No automatic fallback |
| Embedding | `text-embedding-3-large` / `1` | Owner-selected production deployment; Standard | 1 | Verify current pricing | No automatic fallback |

The public Azure OpenAI pricing page is the source to recheck these assumptions;
the figures above are not an approved quote. The owner approved a monthly
planning illustration of 1M uncached input and 1M output tokens for each text
deployment plus 1M embedding input tokens, estimated at about $9.10. This is
not a monthly worst-case or cap: standard deployments bill by token, capacity
is a throughput/quota setting rather than a spend limit, and the existing $50
resource-group budget only alerts. No finite monthly maximum is enforced.

Data Zone Standard processes prompts and responses within the Microsoft-defined
EU data zone. The Foundry resource can be located in Switzerland North, but
processing can take place elsewhere in the EU; this is **not Swiss data
residency**. If a model requires `GlobalStandard`, processing can occur in any
geography where that model is deployed. Before an apply, check the official
catalog and regional availability because resource-region support and model
SKU availability can change independently.

For Azure-sold models, Microsoft describes the models as stateless and says
prompts and completions are not used to train the base models without
permission. Abuse monitoring can select samples for additional review,
including human review, under Microsoft's terms. Do not represent this as a
zero-retention or no-review guarantee; review the current [data privacy](https://learn.microsoft.com/en-us/azure/foundry/responsible-ai/openai/data-privacy)
and [abuse monitoring](https://learn.microsoft.com/en-us/azure/foundry/openai/concepts/abuse-monitoring)
terms before any sensitive content is sent.

The owner accepted these documented terms for provisioning only; this does not
authorize sending journal content.

The account disables local/key authentication and keeps public network access
enabled, consistent with the existing no-private-networking design. The
Container App's runtime user-assigned identity remains the intended application
inference principal. Before use, the owner must grant or verify **Cognitive
Services OpenAI User** for that identity at the Foundry account scope. This
remains external to Terraform; the group grants below do not replace it.
The GitHub OIDC deployment identity's built-in Contributor role, even at
subscription scope, cannot create role assignments. The production
`foundry_access_group` is set to `null` so Terraform does not attempt group
assignments. Do not add role-assignment permissions to GitHub OIDC. Account
creation alone does not enable the application adapter, inference, or
authorization to send journal content.

### Existing group access

`foundry_access_group` is a nullable object containing `object_id` (GUID) and
`display_name` (non-blank descriptive metadata, not used to resolve the group).
When populated, it refers to an owner-supplied Entra group. Its directory
object ID is a non-secret group identifier, not a tenant or subscription ID.
Terraform never creates, imports, looks up, or manages the group or its
membership. The production variables set it to `null`, so Terraform does not
create group role assignments. No new `azuread` dependency is added; the
existing provider is still needed for application registrations.

Only when Foundry is enabled and the group is non-null does Terraform assign:

| Role | Scope | Purpose |
| --- | --- | --- |
| Cognitive Services OpenAI User | Foundry account | Call/test model deployments |
| Foundry User (formerly Azure AI User) | Foundry account (inherited by project) | Project reader access and project data actions |

Microsoft's [Foundry RBAC documentation](https://learn.microsoft.com/en-us/azure/foundry/concepts/rbac-foundry)
records the rename from Azure AI User to Foundry User. Terraform uses the
current name for the same approved built-in role, not a broader replacement.
The [built-in role reference](https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles/ai-machine-learning#foundry-user)
identifies it as `53ca6127-db72-4b80-b1b0-d745d6d5456d`. These project data
actions are distinct from the OpenAI inference grant; no app or agent is
enabled by assigning either role.

Assignments target `principal_type = "Group"` and have deterministic names
derived from the account ID, group ID and role. The group name does not imply
management rights: no Contributor, Owner or RBAC administration role is granted.
The deployment identity receives neither of these model roles.

Contributor cannot write role assignments, even at subscription scope, and
this PR does not change GitHub OIDC permissions. If group access is needed,
have an authorized Azure administrator create and verify the two account-scoped
group assignments manually. Do not populate `foundry_access_group` in the
production configuration unless a separately approved deployment identity
permission path is established.

### Owner approval and teardown

## 🙋 Needs human

@cedricmendelin: before the next apply, verify current model availability,
region/SKU support, subscription quota and pricing. After the account is
provisioned, manually grant **Cognitive Services OpenAI User** to the runtime
Container App identity at the Foundry account scope before inference. If group
access is needed, assign its roles manually as described above. This
configuration does not enable chat or authorize sending journal content.

### Remaining work and acceptance evidence

Issue #128 milestone A remains partial; keep the rollout issue open. This PR
enables the production configuration by the owner's later direct instruction,
but no live plan, apply or role verification is claimed by mocked Terraform
tests.

- [ ] @cedricmendelin / Azure administrator: verify dated model availability,
  pricing and subscription quota evidence before an apply. Any model, region or
  SKU fallback needs separate owner approval.
- [ ] @cedricmendelin / Azure administrator: manually assign the runtime
  identity's OpenAI User role before inference. If group access is needed,
  manually assign both group roles at account scope.
- [ ] DevOps: review a sanitized enabled plan showing only intended changes and
  record the existing workflow apply evidence; do not attach state or binary
  plan files.
- [ ] @cedricmendelin / Azure administrator: after provisioning, verify the
  runtime identity's OpenAI User role at the exact account scope. If group
  access is needed, verify both group roles too. Link sanitized role-name,
  principal-type and scope evidence, redacting real subscription/tenant/
  principal IDs, before claiming completion.

For each intended principal, an authorized administrator can inspect direct
account-scoped assignments locally (substitute IDs without posting them):

```sh
az role assignment list --assignee-object-id "<principal-object-id>" \
  --scope "<foundry-account-resource-id>" \
  --query "[].{role:roleDefinitionName,principalType:principalType,scope:scope}" \
  --output table
```

Tests: `terraform fmt -check -recursive`, `terraform validate` and
`terraform test` in `infra/`. These are Terraform-native checks; the team does
not use Python test suites for infrastructure or deployment behavior. No
journal capability is added, so no API endpoint or MCP tool is required.

To tear down, set `foundry_enabled = false` and apply; Terraform removes the
managed group roles, deployments, project and account. Have the administrator
remove externally managed group roles and the runtime identity's account-scoped
inference role grant separately. If Azure retains the deleted Cognitive
Services account as soft-deleted, use the Azure recovery/purge process only
after confirming no recovery or retention requirement remains.

Sources reviewed through indexed excerpts on 2026-11-18; live confirmation
remains an enablement gate:

- [Microsoft GPT-6.1 Terra GA announcement](https://techcommunity.microsoft.com/blog/azure-ai-foundry-blog/gpt-6-1-terra-now-generally-available-in-microsoft-foundry-build-agents-that-see/4567769)
- [GPT-6.1 Terra model catalog](https://ai.azure.com/catalog/models/gpt-6.1-terra)
- [GPT-6.2 Sol model catalog](https://ai.azure.com/catalog/models/gpt-6.2-sol)
- [GPT-6.2 Astra model catalog](https://ai.azure.com/catalog/models/gpt-6.2-astra)
- [Text embedding 3 large model catalog](https://ai.azure.com/catalog/models/text-embedding-3-large)
- [Foundry Models region availability](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/models-sold-directly-by-azure-region-availability)
- [Azure OpenAI public pricing](https://azure.microsoft.com/en-us/pricing/details/azure-openai/)
- [Data Zone Standard deployment type](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/deployment-types)

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

`API_ALLOWED_PRINCIPAL_OBJECT_IDS` contains approved users' object IDs from this
tenant, not group IDs or application/client IDs. The API checks the delegated
token's `oid` claim, not its group membership. No new Entra group is needed for
this setting; the SQL administrator group below has a separate purpose.

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
   terraform -chdir=infra init -lockfile=readonly \
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

Do not post plan files or state blobs in pull requests. Local plans are optional
for diagnosis or PR review; production applies do not require a separate approval.

## First deployment and identity configuration

Releases run automatically from `deploy.yml` after a successful CI push run on
`main`, once activated; a manual dispatch accepts only a previous successful
main CI run ID for recovery. The workflow admits the exact CI release before
Azure access, authenticates through the single immutable-subject OIDC identity,
serializes on `terraform-prod`, and applies pending `infra/` changes without
changing deployed images before running `infra/deploy_gate.py`. The release gate
updates only the migration job and the app image. To reconcile live drift when
the source tree is unchanged, manually dispatch **Provision infrastructure**.

If SQL cannot resolve a managed identity while creating its database principal,
the tenant administrator must inspect the required directory lookup permissions.
Terraform enables a system-assigned identity on the server, but does not manage
Microsoft Entra role assignments. Grant only the SQL server identity's necessary
lookup permissions, using Microsoft's least-privilege guidance; do not assume
the broad **Directory Readers** role is always required.
This permission is not granted to the runtime, migration, or deployment
identity. Get the server principal ID from
`terraform -chdir=infra output -raw sql_server_identity_principal_id`.

The infrastructure baseline comes first. Follow the
[infrastructure plan and apply procedure](deployment.md#infrastructure-plan-and-apply), then these
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
