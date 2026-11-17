# Terraform

See [Production infrastructure](../docs/infrastructure.md) for the architecture,
bootstrap, API registration, cost estimate, deployment variables, and teardown.
The [deployment guide](../docs/deployment.md) describes automatic plan/apply for
merged infrastructure changes, on-demand reconciliation, and the separate
application migration and promotion gate. Deployment workflows use OIDC only.

## PWA BFF client

The approved PWA client registration is provisioned with the Container App. Its
redirect URI is the app's HTTPS FQDN followed by `/signin-oidc`. Terraform
requests only the API's delegated `journal.access` scope and does not create,
store, or output a client credential. No new Terraform outputs expose the client
ID or redirect URI; both are available from the managed Entra application
registration.

After reviewing and applying the owner-run infrastructure plan, the owner
creates the client secret in Entra and stores it only in the existing Key Vault.
Rotate it by creating the replacement secret, updating the Key Vault version,
verifying the BFF uses it, and then deleting the old secret. Admin consent for
`journal.access` and assignment of approved users to the API's `JournalUser`
role are separate owner actions; neither Terraform registration nor client ID
replaces the API's approved-user object-ID allowlist. This registration is for
the PWA BFF only, not an MCP client. See the [deployment guide](../docs/deployment.md#pwa-bff-client-registration).
