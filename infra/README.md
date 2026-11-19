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
The owner approved this configuration after issue #128 plan v1's disabled
milestone A; the rollout issue remains open for live verification.

`foundry_access_group` can reference an existing Entra group, but production
sets it to `null` so Terraform does not attempt group role assignments. The
group and its membership are never managed or looked up by Terraform.

When enabled with a configured group, Terraform assigns **Cognitive Services
OpenAI User** and **Foundry User** (the renamed **Azure AI User** role) at the
Foundry account scope. The production configuration leaves these group roles
external; an authorized administrator can assign them manually if needed. The
runtime identity's OpenAI User role is a separate manual grant before inference.
Do not grant role-assignment permissions to GitHub OIDC; Contributor cannot
create role assignments, even at subscription scope.

Before the next apply, verify current model/region/SKU availability, quota and
pricing. Follow the [remaining evidence checklist](../docs/infrastructure.md#remaining-work-and-acceptance-evidence)
for an enabled plan, workflow apply, and sanitized account-scoped role
verification.

Local checks (from this directory):

```sh
terraform init -backend=false -input=false -lockfile=readonly
terraform fmt -check -recursive
terraform validate
terraform test
```

The Foundry mock contracts run through `terraform test`; no Python test suite
is used for infrastructure or deployment behavior. Mock tests do not establish
live Azure provisioning.

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
