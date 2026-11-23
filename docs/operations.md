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

Follow-up titles, conditions, evidence and checked scopes, and drafts are also
personal data; do not include them in diagnostics or public evidence.

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
| `knowledgeEntries` | Empty array in this logical export. |
| `followUps` | Follow-up records, evidence and change history, draft, status, and optional item links. |

The export is a logical portable representation, not a SQL backup, restore
loader, or guarantee that a complete production recovery has been rehearsed.
The opt-in SQL durability test includes a test-only loader that restores a
synthetic export to an empty database and compares the canonical snapshot; it
is not an operational import command. Knowledge entries are absent from this
logical export; use the portable backup below to include them. Do not edit
exported IDs or duplicate list names before restoration. A future schema
version must define migration behavior before it replaces version 1.

### Follow-up backup and rollback warning

The portable journal backup (`GET /api/journal/backup/export`, the PWA, or
`export_journal_backup`) includes follow-ups, evidence and change history,
drafts, and item links. A follow-up-aware restore replaces that collection
when the backup contains it. If the section is absent, current follow-ups are
retained; the restore is rejected without applying the backup if a retained
follow-up links to an item absent from the restored journal. An included
follow-up section with invalid item links is also rejected. Export a fresh
backup and keep it securely before attempting a restore.

The migration adds follow-up tables with a restrictive item foreign key. An
older binary does not understand follow-up data: its delete-and-reinsert
restore can fail when an item is linked, or it can fail to restore follow-up
data omitted from the older schema. Do not rely on an older application's
restore to recover follow-ups. Preserve a verified follow-up-aware backup,
restore with a compatible follow-up-aware version, and verify the result.
This is separate from the Thought-type rollback caveat in
[domain semantics](domain.md#identity-and-lists); a type-aware binary alone
is not necessarily follow-up-aware.

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
overages. The $50 budget is an alert, not a spending cap. These estimates are
not a quote and do not include unverified restore charges. Before provisioning
or deployment, obtain approval for resource choices, a current calculator
estimate, and a cost-control/monitoring plan. See
[production deployment](deployment.md) and
[infrastructure cost assumptions](infrastructure.md#cost-estimate).

## Evidence and known limitations

| Area | Evidence in this repository | Limitation |
| --- | --- | --- |
| API/MCP export | Integration contract tests exercise both adapters over shared services. | Logical export has an empty knowledge section and is not the portable restore backup. |
| Follow-up backup | Application and browser tests cover backup round-trip; SQL migration/restore coverage is opt-in. | Production recovery and any particular SQL test run remain unverified here. |
| PWA | Browser tests use a synthetic identity and in-memory repository; the worker's asset allowlist is covered. | Does not establish SQL durability, live sign-in, target-browser installation, or deployed sign-out behavior. |
| SQL | Run the opt-in `SqlDurability` tests described in [development.md](development.md). | Results are local evidence only; Azure SQL parity and production restore remain unverified. |
| Authentication | Automated tests cover endpoint fallback authorization and approved-principal policies. | No live Entra or unrelated-tenant production check is claimed. |
| Assistant clients | [Remote MCP documentation](mcp.md) lists configuration guidance for VS Code Copilot, Copilot CLI, Claude Code, and Claude Desktop. | None has a recorded live connection test. The Copilot cloud coding agent is unsupported by the current OAuth flow. |
| Hosting/cost | Terraform and the deployment gate document the proposed topology and release checks. | No new resource approval, provisioning, rollout, multi-replica, production restart, or cost measurement is included here. |

Record only the command, tool/test result, timestamp, and sanitized environment
details as evidence. Keep real journal data and credentials out of evidence.
