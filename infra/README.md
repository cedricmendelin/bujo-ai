# Terraform

See [Production infrastructure](../docs/infrastructure.md) for the architecture,
bootstrap, API registration, cost estimate, deployment variables, and teardown.
The [deployment guide](../docs/deployment.md) describes automatic plan/apply for
merged infrastructure changes, on-demand reconciliation, and the separate
application migration and promotion gate. Deployment workflows use OIDC only.

The Foundry account, project, and model deployments are enabled in
`prod.tfvars`; the Terraform variable default remains disabled. The approved
model/region/SKU settings and owner-managed runtime identity grant are
documented in [Optional Microsoft Foundry model provider](../docs/infrastructure.md#optional-microsoft-foundry-model-provider).
Provisioning these resources does not enable model inference or journal access.

## PWA BFF client

The PWA registration has two HTTPS callbacks: the Container App's
`/signin-oidc` and `https://localhost:7168/signin-oidc`. Terraform provisions
the federated trust for the runtime managed identity and configures the app's
client ID, identity client ID and `journal.access` scope. No production client
secret or Key Vault networking change is needed. Admin consent for
`journal.access` and assignment of approved users to the API's `JournalUser`
role are separate owner actions; neither Terraform registration nor client ID
replaces the API's approved-user object-ID allowlist. This registration is for
the PWA BFF only, not an MCP client. See the [deployment guide](../docs/deployment.md#pwa-bff-client-registration).
