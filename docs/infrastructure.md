# Production infrastructure

The `infra/` Terraform root provisions one production environment in Switzerland
North. It uses pinned Azure Verified Modules, an owner-created resource group,
Azure Blob remote state, and one GitHub Actions OIDC deployment identity. Pull
request workflows do not authenticate to Azure; the owner runs plans locally.

Terraform creates a consumption Container Apps environment and app, a
user-assigned runtime identity, a capped Log Analytics workspace, a standard
RBAC-enabled Key Vault, an Entra-only Azure SQL server and free-offer database,
and a monthly resource-group budget. There are no VNets, private endpoints,
NAT, ACR, SQL passwords, or Terraform-managed Key Vault values.

The app resource group `bujo-ai-rg` must be created by the owner before the
first deployment. Terraform looks it up as data rather than managing it. The
app starts with `mcr.microsoft.com/k8se/quickstart:latest`, external ingress on
port 80, 0.25 vCPU/0.5 GiB, and zero to one replica. Once a root `Dockerfile`
exists, deployment publishes its image to GHCR and updates the app to the
commit-SHA tag.

## Resources and modules

| Resource | Implementation |
| --- | --- |
| User-assigned managed identity | `Azure/avm-res-managedidentity-userassignedidentity` |
| Log Analytics workspace | `Azure/avm-res-operationalinsights-workspace`, PerGB2018, 0.1 GB/day cap, 30-day retention |
| Container Apps environment | `Azure/avm-res-app-managedenvironment`, default Consumption profile |
| Container app | `Azure/avm-res-app-containerapp`, public ingress, scale to zero, UAMI attached |
| Key Vault | `Azure/avm-res-keyvault-vault`, Standard, RBAC, 7-day soft delete, purge protection, public endpoint |
| SQL server | `Azure/avm-res-sql-server`, TLS 1.2, Entra-only group admin, Azure-services firewall rule |
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
   * **Storage Blob Data Contributor** on the `tfstate` container only

Contributor does not include Azure RBAC role-assignment write actions or
Key Vault data actions. Do not grant the deployment identity Owner, User Access
Administrator, Role Based Access Control Administrator, or any Key Vault
data-plane role. Terraform contains no role assignments. The owner performs
the one runtime Key Vault role grant after the first apply (see below).

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
| `BUDGET_ALERT_EMAIL` | Address to receive Azure budget alerts |

Create an Entra security group for the SQL administrator, add the owner account
as a member, and use the group's object ID and display name for the two SQL
variables. The SQL module accepts the group object ID and display name; Azure
resolves them as the Entra group administrator. No actual group name or ID
belongs in source control.

In `infra/prod.tfvars`, change `key_vault_name` to a globally unique name if the
example name is already in use. The name must be 3–24 letters, numbers, or
hyphens, start with a letter, and contain no consecutive hyphens.

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
3. Supply the SQL group object ID/display name and budget alert email as
   `TF_VAR_sql_entra_admin_object_id`, `TF_VAR_sql_entra_admin_login`,
   `TF_VAR_budget_alert_email`, and the resource group as
   `TF_VAR_resource_group_name` in the local shell, then run:

   ```sh
   terraform -chdir=infra plan -lock=false -var-file=prod.tfvars
   ```

Do not post plan files or state blobs in pull requests. The approved PR may be
merged after the owner has reviewed its local plan.

## First deployment and runtime Key Vault access

Merge to `main` to run `deploy.yml`. It builds and pushes a GHCR image only
when a root `Dockerfile` exists, authenticates to Azure through the single
immutable-subject OIDC identity, locks remote state, applies Terraform, and
updates the container app revision. Without a `Dockerfile`, it deploys the
public placeholder image.

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

After deployment, the owner connects to Azure SQL as a member of the configured
Entra admin group and creates a database principal for the runtime UAMI with
`CREATE USER [<runtime-identity-name>] FROM EXTERNAL PROVIDER;`. Grant only the
database roles the application requires; this remains manual until #5.

## Teardown and recovery

Run `terraform destroy -var-file=prod.tfvars` locally using the same backend
configuration and an identity with sufficient rights on the app resource
group and state. Review the destroy plan first. Terraform does not delete the
owner-created app resource group or state storage. Key Vault soft delete is
seven days and purge protection is enabled; a deleted vault name remains
reserved during recovery. Use a new vault name if recreating before purge.
Repeat the runtime role assignment after recreating the vault. Remove state
storage and its bootstrap resource group only after production resources are
gone and restore/audit needs have been met. Restore a prior state version from
Blob versioning if state is corrupted.

## Operations and accepted trade-offs

- Networking is public as required; SQL and Key Vault require Entra/RBAC access.
- The production resource group and the backend storage are separate. State
  uses Azure AD authentication and OIDC in GitHub; no keys or SAS are used.
- A repository rename or transfer requires updating the immutable federated
  subject. A failed manual role grant does not block the first deployment
  because the app has no vault secret references yet.
- GHCR is expected to be public. Do not add a registry password to Terraform;
  revisit managed-identity image pulls before making the image private.
