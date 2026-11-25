# Journal operations and recovery

This runbook covers the existing personal-release journal. It does not authorize
provisioning, deployment, identity changes, or a production restore. Obtain the
owner's explicit approval of the target resource and cost before any live
operation.

## Data ownership and privacy

The approved account owns its journal. The API and remote MCP both require the
configured tenant and approved-principal allowlist; an MCP session is not an
authorization boundary. Journal content, tags, goals, occurrence history, and
planned periods are private personal data.

The service has no automatic journal-retention policy or user-facing delete
operation. Data remains in Azure SQL until an owner-authorized database deletion
or restore action removes it. Azure SQL automated backup retention is a separate
platform policy; the approved baseline documents seven-day retention for its
free offer and does not provide long-term retention. Verify the deployed
database's actual backup policy before relying on that duration. Restored
databases and retained backups can keep deleted content; coordinate backup
expiry and deletion requirements before promising complete erasure.

The PWA service worker caches only public static assets. It does not cache
navigations, account pages, API/journal responses, or MCP responses, and does
not queue offline writes. Signing out therefore has no client-cached journal
data to clear. Browser memory may retain the currently rendered page until it
is closed or reloaded; do not use a shared device without signing out and
closing the browser.

Diagnostics must not include access tokens, authorization headers, connection
strings, descriptions, tags, list goals, or journal text. Query failures return
an explicit error rather than an empty success response. The host logs a
constant failure message for journal snapshot/export errors and does not attach
exception text. Use resource names, operation IDs, status codes, and sanitized
counts for correlation. Never attach raw logs, exports, backups, Terraform
state/plans, or database content to issues or pull requests.

The owner approved a future exception for chat, Foundry and MCP prompt/response
content, potentially including journal text, with 30-day retention. It must
remain disabled until protected routing, privileged access and the absence of
copies in ordinary telemetry are verified for each path using synthetic data.
This does not permit secrets or independent tool payloads in telemetry. Azure's
[protected GenAI content table](https://learn.microsoft.com/en-us/azure/azure-monitor/app/protect-sensitive-content)
is a preview; it does not automatically protect copies in other tables or
previously ingested data. No protected content capture has been verified here.

## Portable journal export

The owner-authorized, read-only export is available at `GET
/api/journal/export` and through the MCP `export_journal` tool. HTTP responses
are `Cache-Control: no-store`; both adapters return the same schema and no
authentication data. The export is sensitive and should be written only to an
owner-controlled encrypted location with access limited to the owner.

Schema version 1 is UTF-8 JSON with these top-level fields:

| Field | Contents |
| --- | --- |
| `schemaVersion` | Integer version (`1`). |
| `exportedAt` | UTC export timestamp. |
| `lists` | Stable IDs, names, kind/state, goal text, optional ISO week, and version. |
| `items` | Stable IDs, canonical name/description, tag strings, planned-period fields, current occurrence ID, and version. |
| `occurrences` | Stable IDs, item/list IDs, sequence, local state, timestamps, links, and version. |
| `history` | Ordered journal events with operation and occurrence IDs. |
| `knowledgeEntries` | Reserved empty array until the knowledge domain is delivered. |

The export is a logical portable representation, not a SQL backup, restore
loader, or guarantee that a complete production recovery has been rehearsed.
The opt-in SQL durability test includes a test-only loader that restores a
synthetic export to an empty database and compares the canonical snapshot; it
is not an operational import command. Knowledge entries remain absent until
the knowledge domain is delivered. Do not edit exported IDs or duplicate list
names before restoration. A future schema version must define migration
behavior before it replaces version 1.

## SQL backup and restore

Use Azure SQL point-in-time restore for database-level recovery. A restore
creates a separate target database; it is not an in-place rollback. For the
approved free-offer configuration, the baseline states that restored databases
cannot use the free offer, so budget for a paid restore target. Local SQL
durability tests and JSON export checks do not prove Azure SQL PITR behavior.

Before any production restore:

1. Get owner approval for the recovery point, target database, expected cost,
   downtime, and whether writes should be stopped.
2. Record the current app revision and database target using sanitized IDs only.
   Do not copy connection strings or logs to the record.
3. Use the Azure portal or approved owner-operated procedure to restore the
   selected point in time into a new database. Do not delete or overwrite the
   original database.
4. Verify the restored database with the release's `--verify-database` mode.
   Confirm every expected migration is present and the runtime identity has
   journal-table read/insert/update/delete access.
5. Before switching traffic, compare representative synthetic item/list IDs,
   occurrence chains, tags, planned periods, and event history through the PWA
   and MCP. Do not expose real content in verification evidence.
6. With explicit owner approval, update the runtime database target, promote a
   compatible app revision, and verify readiness, liveness, API authorization,
   and MCP authorization. Preserve the original database until recovery is
   accepted.
7. Record the sanitized recovery point, elapsed time, validation result, and
   any data-loss window. Remove temporary resources only after the owner
   confirms that recovery and retention requirements are met.

The production database is not yet authorized for a restore rehearsal. Confirm
the exact Azure SQL backup retention, restore target eligibility, RPO/RTO, and
paid-target cost before treating this procedure as operationally proven.

## Container Apps readiness and operation

The current infrastructure proposal is in
[infrastructure.md](infrastructure.md): Switzerland North, external HTTPS
ingress to port 8080, a user-assigned runtime identity, Azure SQL outside the
container, Log Analytics, and a Consumption app configured for zero-to-one
0.25-vCPU/0.5-GiB replicas. Key Vault exists, but the current app does not use
Key Vault references or require an application secret; runtime SQL access uses
managed identity. Any future secret must be externalized into an approved
secret store, not embedded in an image or committed configuration.
The host verifies database migrations and data permissions before serving;
only `/health/live` is anonymous, while readiness is protected. The existing
Terraform configuration uses scale-to-zero and single-revision mode. A cold
start and server-circuit loss may require a reload; do not promise circuit
survival or unsaved-input recovery across scale-to-zero or restart.

Keep the database outside the app container and its ephemeral filesystem.
Runtime connections use managed identity, TLS, and the Entra-only SQL server;
the application must not store credentials in images or environment files.
The current app is single replica: circuits and server-side authentication
sessions are not shared. Do not increase replicas until session affinity,
authentication-session behavior, concurrent write recovery, SQL connection
limits, and cost have been reviewed. EF Core uses SQL retry-on-failure; retry
only idempotent operations with the original operation ID.

The existing monthly estimate is under $1 idle and under $10 for expected
personal use, subject to current free grants, region, actual usage, and
overages. The monthly budget amount of 80 is an alert, not a spending cap;
Azure uses the subscription billing currency, which is not yet verified.
These estimates are
not a quote and do not include unverified restore charges. Before provisioning
or deployment, obtain approval for resource choices, a current calculator
estimate, and a cost-control/monitoring plan. See
[production deployment](deployment.md) and
[infrastructure cost assumptions](infrastructure.md#cost-estimate).

## Monitor ingestion and caps

The existing Log Analytics cap is 0.1 GB/day. Application Insights and resource
diagnostics share workspace ingestion; their combined volume can exhaust this
limit before the day ends. For workspace-based Application Insights the
effective cap is the lower of its own cap and the workspace cap. A reached cap
stops applicable billable log ingestion until the workspace reset; some excess
may still be ingested and billed. It is not a reliable spending ceiling or a
guarantee that all prompts and requests are recorded.

Before deploying monitoring, obtain owner approval for alert creation and its
cost. In Azure Monitor, configure a log-search alert scoped to the workspace,
with an action group delivering to the owner-approved budget alert email:

- For early warning, evaluate the following query over the last 24 hours and
  alert when `DataGB` exceeds 0.08. This is a rolling, delayed estimate, not the
  workspace's exact reset window; it may warn late or not at all.
  `Usage | where IsBillable | summarize DataGB = sum(Quantity / 1000.0)`
- For cap reached, use Microsoft's documented query
  `_LogOperation | where Category =~ "Ingestion" | where Detail contains "OverQuota"`,
  count rows over five minutes, and alert when the count exceeds zero.

Follow [Microsoft's daily cap guidance](https://learn.microsoft.com/en-us/azure/azure-monitor/logs/daily-cap)
to verify the signal, action group and price. Neither alert is claimed to be
deployed. Investigate sustained volume, review the workspace reset time and
measure synthetic ingestion before proposing a cap change; obtain explicit
owner approval before changing the cap. Never include captured content in
alert messages or evidence.

## Protected prompt telemetry release gate

The approved prompt-content exception is not permission to turn on unprotected
collection. Before collecting real chat, Foundry or MCP prompt/response content,
an owner-authorized operator must:

1. Verify that the Application Insights connection uses regional ingestion and
   that dedicated GenAI-content routing is available for this subscription.
   Registering a preview feature, if needed, is a separate owner action.
2. Set `AppGenAIContent` protection and 30-day retention; review workspace
   access mode and grant only authorized readers the necessary data actions.
   Check for indirect access via control-plane roles and follow Microsoft's
   guidance on `DataActionsOnly` where applicable. Role or authorization-mode
   changes require separate owner approval.
3. Use synthetic data for every chat, direct Foundry and MCP prompt path.
   Confirm an authorized reader can see the intended records, an ordinary
   reader cannot, and the same content is absent from other Application
   Insights, Log Analytics, container and diagnostic tables and from metric
   dimensions. Do not copy real data into tests or evidence.
4. Verify credentials and other secrets cannot reach telemetry, including via
   prompt and response content, exceptions, URL queries and SQL statements.
   If any path or guarantee cannot be verified, keep that path disabled and
   record the blocker. Disabling future capture does not remove existing
   copies; follow an owner-approved deletion and retention procedure.

The current app calls Foundry models directly, not through a hosted Foundry
agent. A project connection alone does not establish that its model requests or
MCP prompt messages are correctly protected. The preview's tested framework
list does not establish coverage for these paths. Do not enable collection on
the basis of a configuration flag or portal connection alone.

## 🙋 Needs human

@cedricmendelin: before a live telemetry rollout, confirm the subscription's
billing currency, approve the estimated monitoring and alert costs, and name
the target environment and authorized operator. Separately authorize the
preview feature registration and restricted reader assignments if required.
The operator must then verify the protected-content release gate above with
synthetic data. The current implementation does not provide a protected
content exporter or Foundry project connection; passing those checks alone does
not enable either, and further implementation and verification are required.

## Evidence and known limitations

| Area | Evidence in this repository | Limitation |
| --- | --- | --- |
| API/MCP export | Integration contract tests exercise both adapters over the shared journal service. | Export schema v1 has an empty knowledge section; no public import tool exists. |
| PWA | Browser tests use a synthetic identity and in-memory repository; the worker's asset allowlist is covered. | Does not establish SQL durability, live sign-in, target-browser installation, or deployed sign-out behavior. |
| SQL | Run the opt-in `SqlDurability` tests described in [development.md](development.md). | Results are local evidence only; Azure SQL parity and production restore remain unverified. |
| Authentication | Automated tests cover endpoint fallback authorization and approved-principal policies. | No live Entra or unrelated-tenant production check is claimed. |
| Assistant clients | [Remote MCP documentation](mcp.md) lists configuration guidance for VS Code Copilot, Copilot CLI, Claude Code, and Claude Desktop. | None has a recorded live connection test. The Copilot cloud coding agent is unsupported by the current OAuth flow. |
| Hosting/cost | Terraform and the deployment gate document the proposed topology and release checks. | No new resource approval, provisioning, rollout, multi-replica, production restart, or cost measurement is included here. |

Record only the command, tool/test result, timestamp, and sanitized environment
details as evidence. Keep real journal data and credentials out of evidence.
