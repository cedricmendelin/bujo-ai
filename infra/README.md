# Terraform

See [Production infrastructure](../docs/infrastructure.md) for the architecture,
bootstrap, API registration, cost estimate, deployment variables, and teardown.
The [deployment guide](../docs/deployment.md) describes automatic plan/apply for
merged infrastructure changes, on-demand reconciliation, and the separate
application migration and promotion gate. Deployment workflows use OIDC only.

The optional Foundry account, project, and model deployments are disabled by
default. Model/region/SKU choices and owner-only enablement requirements are
documented in [Optional Microsoft Foundry model provider](../docs/infrastructure.md#optional-microsoft-foundry-model-provider).
No model inference or journal access is enabled by provisioning these resources.
Issue #128 plan v1 milestone A keeps `foundry_enabled = false` and configures
only a reference to the existing `bujo-ai-foundry-admins` Entra group through
nullable `foundry_access_group` (`object_id` GUID and non-empty `display_name`).
Terraform does not manage or look up the group or membership.

When enabled with a configured group, Terraform assigns **Cognitive Services
OpenAI User** and **Foundry User** (the renamed **Azure AI User** role) at the
Foundry account scope. It grants no
management roles and no access to the deployment identity. Applying these
assignments requires an owner-approved, out-of-band, constrained RBAC
Administrator grant for the deployment identity; its current Contributor
permission is insufficient. Alternatively, leave the group variable null and
have an authorized administrator manage the roles manually. Runtime identity
OpenAI User access is still a separate administrator action.

Before a separate authorized enablement PR, follow the linked
[evidence checklist](../docs/infrastructure.md#remaining-work-and-acceptance-evidence)
for model/region/SKU/privacy/cost approval, an enabled plan, workflow apply and
sanitized account-scoped group/runtime role verification.

Local checks (from this directory):

```sh
terraform init -backend=false -input=false -lockfile=readonly
terraform fmt -check -recursive
terraform validate
terraform test
```

The repository infrastructure CI suite also runs the Foundry mock contracts:
`python3 -m unittest discover -s tests/infrastructure -p 'test_*.py'` from the
repository root. These tests are not evidence of live Azure provisioning.

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
