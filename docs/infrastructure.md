# Production infrastructure

The `infra/` root describes one production environment in Switzerland North. It
uses pinned Azure Verified Modules, a pre-created resource group, and OIDC for
GitHub Actions. It creates a consumption Container Apps environment and app, a
user-assigned runtime identity, a capped Log Analytics workspace, a standard
RBAC-enabled Key Vault, an Entra-only Azure SQL server and database, and a
monthly resource-group budget. There are no VNets, private endpoints, NAT, ACR,
SQL passwords, or Terraform-managed Key Vault values.

The resource group `rg-bujo-prod` must exist before Terraform runs. Terraform
looks it up as data rather than creating it, keeping deployment permissions
scoped to that group. The app starts with
`mcr.microsoft.com/k8se/quickstart:latest`, external ingress on port 80, 0.25
vCPU/0.5 GiB, and zero to one replica. Once a root `Dockerfile` exists, the
deploy workflow publishes its image to GHCR and updates the app to the commit
tag.

## Resources and modules

| Resource | Implementation |
| --- | --- |
| User-assigned managed identity | `Azure/avm-res-managedidentity-userassignedidentity` |
| Log Analytics workspace | `Azure/avm-res-operationalinsights-workspace`, PerGB2018, 0.1 GB/day cap, 30-day retention |
| Container Apps environment | `Azure/avm-res-app-managedenvironment`, default Consumption profile |
| Container app | `Azure/avm-res-app-containerapp`, public ingress, scale to zero, UAMI attached |
| Key Vault | `Azure/avm-res-keyvault-vault`, Standard, RBAC, purge protection, public endpoint |
| SQL server | `Azure/avm-res-sql-server`, TLS 1.2, Entra-only admin, Azure-services firewall rule |
| SQL database | AzureRM plus AzAPI free-offer property update |
| Monthly budget | AzureRM resource-group budget, $50 with forecast/actual alerts |
| Terraform state | Owner-bootstrapped Azure Storage container, remote backend with Entra RBAC |

All Terraform modules use exact versions in their `source` blocks. Provider
requirements are constrained in `infra/versions.tf` and selected provider
versions are recorded in `infra/.terraform.lock.hcl`.

## Non-AVM justification

| Resource or operation | Reason |
| --- | --- |
| Resource group lookup | The owner pre-creates the empty app resource group so the apply identity does not need subscription-scope permissions. A Terraform data source references it without managing it. |
| `azurerm_mssql_database` | The SQL-server AVM does not expose the database free-offer settings. AzureRM manages the database's serverless SKU and auto-pause configuration. |
| `azapi_update_resource` for SQL free offer | AzureRM 4.81.0 has no `use_free_limit` or free-limit exhaustion behavior attributes. AzAPI sets the corresponding SQL ARM API properties (`useFreeLimit` and `freeLimitExhaustionBehavior`) to enable the free offer and pause on exhaustion. |
| `azurerm_consumption_budget_resource_group` | No `avm-res-consumption-budget` module is published in the Terraform Registry. |
| State storage | The storage account and private state container are owner-bootstrapped, not part of the production stack. This avoids a bootstrap dependency on Terraform state itself. |

## Cost estimate

Indicative monthly costs for Switzerland North, excluding Microsoft Foundry.
Prices depend on Azure billing currency, use, and current free grants; verify
them with the Azure pricing calculator before provisioning. Idle assumes no
Container Apps replicas and negligible Key Vault operations. Expected personal
use assumes activity remains within the documented free grants.

| Resource | Idle estimate | Expected personal-use estimate |
| --- | ---: | ---: |
| Resource group and managed identity | $0 | $0 |
| Container Apps consumption | $0 | $0–$10 |
| Azure SQL serverless, free offer, auto-pause | $0 | $0–$5 |
| Log Analytics, maximum 0.1 GB/day | $0 | $0–$1 |
| Standard Key Vault | $0 | $0–$1 |
| Monthly Azure budget | $0 | $0 |
| State storage, Standard_LRS | <$1 | <$1 |
| **Estimated total** | **<$2/month** | **<$18/month** |

The estimates leave at least $32/month of headroom under the $50 budget. The
budget is scoped to the production resource group (not the separate state
resource group), alerts at 80% forecast spend and 100% actual spend, and emails
the configured `BUDGET_ALERT_EMAIL`. Container Apps and SQL can incur costs if
usage exceeds free grants or the free SQL offer is unavailable. The SQL offer
is limited by Azure eligibility and quota; monitor usage and billing after
deployment. The Log Analytics daily cap limits ingestion to 0.1 GB/day.

## Bootstrap (owner, one time)

Terraform does not create its own state storage or Entra applications. Complete
these steps once before running either workflow.

### 1. Create remote state storage

Use the Azure owner account and select the intended subscription. Create a
separate Switzerland North resource group and a Standard_LRS StorageV2 account.
Use a unique storage-account name. Configure HTTPS-only access, TLS 1.2 or
later, disable shared-key access, enable blob versioning and soft delete, and
create a private `tfstate` blob container. Do not enable public blob access or
commit state files. The backend uses OIDC and Entra data-plane authorization;
it does not use account keys or SAS tokens.

Assign the apply app **Storage Blob Data Contributor** on the state container
or storage account, and the plan app **Storage Blob Data Reader** at the same
scope. The apply identity needs blob write access to lock and update state.
The plan workflow uses `-lock=false` and has read-only blob access.

### 2. Register two OIDC applications

Create separate Microsoft Entra app registrations for the plan and apply
workflows. For each app, create its service principal and one federated
credential. Do not create a client secret.

| App | Issuer | Audience | Subject |
| --- | --- | --- | --- |
| Apply | `https://token.actions.githubusercontent.com` | `api://AzureADTokenExchange` | `repo:cedricmendelin/bujo-ai:ref:refs/heads/main` |
| Plan | `https://token.actions.githubusercontent.com` | `api://AzureADTokenExchange` | `repo:cedricmendelin/bujo-ai:pull_request` |

These are exact subjects: the apply credential is restricted to `main`, and
the plan credential is restricted to pull-request runs. The plan workflow uses
`pull_request`, not `pull_request_target`, and skips fork PRs.

### 3. Assign minimum Azure roles

Create `rg-bujo-prod` in Switzerland North before the first plan. Grant:

* Apply app: **Contributor** and **User Access Administrator** on `rg-bujo-prod`.
  When creating the User Access Administrator assignment, set condition version
  `2.0` and use this condition to allow only the **Key Vault Secrets User** role:

  ```text
  (
    @Request[Microsoft.Authorization/roleAssignments:RoleDefinitionId]
    ForAnyOfAnyValues:GuidEquals {4633458b-17de-408a-b874-0445c86b69e6}
  )
  ```

  Terraform grants that role to the runtime UAMI on the Key Vault.
* Plan app: **Reader** on `rg-bujo-prod`.
* Both app registrations: the state-storage data roles described above.

The apply identity does not need subscription-scope Contributor. The plan
identity cannot create or change production resources. The budget is scoped to
the production resource group; verify the owner account has rights to read the
billing scope used by Azure Consumption.

### 4. Create the production resource group and SQL admin

Create the empty `rg-bujo-prod` resource group in Switzerland North. Choose a
SQL Entra administrator user or group and record its display name and object
ID. The administrator must be able to connect to the server after deployment.
The database user for the container app identity is added manually after the
first apply; no SQL credentials are used by automation.

### 5. Configure GitHub

Create a GitHub Actions environment named `prod` and limit its deployment
branch to `main`. The deploy workflow uses it as a gate before image build and
apply. The apply job itself deliberately has no environment because GitHub
changes the OIDC subject to `environment:prod` for environment-bound jobs; the
apply credential must retain the exact `ref:refs/heads/main` subject. Add these
**repository variables** (not secrets):

| Variable | Value |
| --- | --- |
| `AZURE_APPLY_CLIENT_ID` | Client/application ID of the apply app registration |
| `AZURE_PLAN_CLIENT_ID` | Client/application ID of the plan app registration |
| `AZURE_TENANT_ID` | Entra tenant ID |
| `AZURE_SUBSCRIPTION_ID` | Subscription ID containing the production group and state storage |
| `TF_STATE_RESOURCE_GROUP` | Resource group containing the state storage account |
| `TF_STATE_STORAGE_ACCOUNT` | State storage account name |
| `TF_STATE_CONTAINER` | Private blob container name, normally `tfstate` |
| `SQL_ENTRA_ADMIN_NAME` | Entra administrator display name |
| `SQL_ENTRA_ADMIN_OBJECT_ID` | Entra user/group object ID |
| `BUDGET_ALERT_EMAIL` | Address to receive Azure budget alerts |

Set `name_suffix` in `infra/prod.tfvars` to a unique 4–8 character lowercase
alphanumeric value before applying. The Key Vault and SQL server names are
globally unique and deployment fails if another Azure customer already uses
them. Keep tenant IDs, subscription IDs, credentials, and state out of Git.

GHCR images are intended to be public so Container Apps needs no image-pull
credential. If the first application image is private, stop before deployment
and design a managed-identity pull strategy; do not add a registry password to
Terraform.

### 6. Run and verify deployment

Open a PR touching `infra/` to run formatting, validation, and an OIDC-backed
plan. The plan comment includes the base commit and redacted plan output. Merge
to `main` to build/push the image when a `Dockerfile` exists, apply Terraform,
and update the container app revision. Until a `Dockerfile` exists, deployment
uses the public placeholder image.

After the first apply, connect to Azure SQL as the configured Entra
administrator and create a database principal for the runtime UAMI using
`CREATE USER [<identity-name>] FROM EXTERNAL PROVIDER;`. Grant only the database
roles the application requires; this task is intentionally not automated.

## Teardown

Run `terraform destroy -var-file=prod.tfvars` from `infra/` using the apply
identity and the same remote backend settings. Review the destroy plan before
applying it. Terraform does not delete the owner-created resource group or
state storage. Key Vault purge protection is irreversible and deleted vaults
remain recoverable during the retention period; do not attempt to purge it
until the retention period and recovery requirements have been considered.
Keep or explicitly remove the state account only after the production
resources are gone and recovery/audit needs are met.

## State and plan safety

The approved plan v2 requested committing local Terraform state. State contains
resource IDs with tenant/subscription identifiers, so it is kept in an
Azure Storage backend protected by Entra RBAC instead of being committed to
the repository. The state account is outside the production stack and uses
OIDC with no access keys. Terraform state and plan files are ignored by
`.gitignore`.

The deploy workflow also uses `prod` as a gate job before the OIDC apply job.
GitHub replaces the default `ref:refs/heads/main` OIDC subject with
`environment:prod` when the environment is attached directly to the apply job,
which would not match the approved main-ref federated credential.

These are intentional deviations from plan v2. The owner requested proper
remote state in an earlier issue comment; remote state also prevents publishing
Azure identifiers contained in state.
