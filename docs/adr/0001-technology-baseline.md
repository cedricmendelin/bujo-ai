# ADR 0001: Technology baseline

**Status:** Accepted on 2026-11-17 for the selected baseline. The owner selected
Interactive Server for the initial interactive UI and the General Purpose
serverless Azure SQL free tier where eligible, with paid continuation at the
free limit. These choices do not authorize provisioning; implementation,
eligibility and operational checks below still apply. The rendering proof
provides implementation evidence, not live verification. The owner-approved
[implementation plan v4](https://github.com/cedricmendelin/bujo-ai/issues/5#issuecomment-6901233646)
selects .NET 11, EF Core SQL Server, and one modular host for that issue only.
MCP client compatibility and Azure SQL configuration availability remain
unverified. Evidence retrieved 2026-11-14; links below are primary Microsoft
sources. Prices, availability and product behavior must be rechecked before
provisioning.

## Context and decisions

The agreed boundary is one modular ASP.NET Core deployment for an installable,
online-first Blazor PWA, API and remote MCP, with durable journal and knowledge
data in external Azure SQL. The first release is personal and authenticated;
offline editing and synchronization are deferred. The agreed infrastructure
direction targets Switzerland North and less than USD 50/month, not a spending
guarantee. See [concept](../concept.md) and
[recorded decisions](../../.squad/decisions.md).

| Choice | Decision | Status |
| --- | --- | --- |
| Runtime | .NET 11, current supported servicing patch | Owner-approved for issue #5 |
| UI | Blazor Web App, Interactive Server for interactive UI, static SSR where appropriate; prove installability | Owner-approved 2026-11-17; installability and runtime behavior still require verification |
| Database | Azure SQL Database; General Purpose serverless free tier where eligible, continue at paid rates when the free allowance is exhausted | Owner-approved 2026-11-17; verify eligibility, region, configuration and cost before provisioning |
| Persistence | EF Core SQL Server provider and reviewed migrations | Owner-approved for issue #5 |
| Structure | One modular ASP.NET Core host with small in-process modules; no microservices | Owner-approved for issue #5 |

ADR acceptance selects a conditional Azure SQL baseline, not a validated
configuration or provisioning approval. A failure of the selected Azure SQL
configuration triggers review of another Azure SQL configuration; changing
database provider requires evidence of a blocker and separate approval.

## Runtime

Microsoft's [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
covers the SDK, runtime, ASP.NET Core and EF Core, and requires current patches.
Its supported releases on 2026-11-14 are:

| Release | Type | Released | End of support |
| --- | --- | --- | --- |
| .NET 10 | LTS | 2025-11-11 | 2028-11-14 |
| .NET 11 | STS | 2026-11-10 | 2028-11-14 |

.NET 8 LTS and .NET 9 STS ended support on 2026-11-10. LTS normally receives
three years of support and STS two; here both supported versions have the same
end date. The issue #5 implementation uses .NET 11 as selected in plan v4,
despite the earlier .NET 10 LTS proposal. Patch the SDK and container runtime
regularly and plan an upgrade before November 2028; a container image is not
patched automatically by updating the host.

## Blazor rendering and installability

### Issue #10 plan v3 proof checkpoint (2026-11-17)

A thin SSR + Interactive Server proof is implemented in the existing modular
host with a `Bujo.Web` Razor class library. Anonymous home/account states use
static SSR; the protected `/rendering-proof` page alone opens an authorized
Interactive Server circuit. Its synthetic text and read-only connection check
are not journal persistence. Public manifest/icons and a static-asset-only
service worker supply installation prerequisites without caching HTML, API
responses, identities or journal data. Offline writes are neither queued nor
replayed.

The approved issue #124 plan extends this baseline with Interactive Server
journal workflows for approved sessions at `/`; the original proof route
remains under Diagnostics. Local synthetic browser tests verify those UI
workflows, not live Entra sign-in, target-device installation, or SQL
persistence across a host restart. See the [issue #124 delivery
matrix](../delivery-matrix-124.md) for current implementation and evidence.

The BFF boundary validates server-held delegated tokens and current approved
identity on requests, circuit start/reconnect and inbound events; APIs remain
bearer-only. See [authentication checkpoint](../auth.md#issue-10-v3-renderingbff-checkpoint)
for configuration, single-replica/restart limitations and the human setup gate.

**Rendering decision: Accepted on 2026-11-17.** This proof supplies implementation
evidence for the selected Interactive Server mode, not a pending rendering
decision. Test-host rendering and locally signed token tests do not establish
real Entra sign-in, target-browser installation,
production proxy behavior, scale-to-zero cost, multi-replica recovery or durable
journal saves. Owner and Architect must review the proof in the PR before
further journal UI implementation. Missing live credentials/configuration are a
human verification gate, not a reversal of the accepted decision or permission
to silently adopt WASM.

[Render modes](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/render-modes?view=aspnetcore-10.0)
describe where components execute. The matrix compares a Blazor Web App using
each mode with a standalone WASM client served by the same ASP.NET Core host.
Authentication notes assume the agreed single-tenant Entra ID and approved-account
restriction; they do not choose the separate MCP authentication flow.

| Option | Installability and deployment | Entra authentication | Reconnect and future AG-UI |
| --- | --- | --- | --- |
| Static SSR | One server host; add and verify manifest/icons and browser install behavior yourself. | Server request authentication; no browser component auth state required. | HTTP requests, no interactive circuit; chat streaming needs additional client interactivity. |
| Interactive Server | One server host; same custom installability work. | Server-managed auth; authorize server operations and the approved account. | SignalR circuit reconnects if retained; state can be lost. Streaming UI is plausible server-side, but interruption recovery is not guaranteed. |
| Interactive WebAssembly in Web App | Host serves an additional client bundle; custom Web App installability work. | Server must secure APIs; client-visible authorization is not a security boundary. Auth state crosses server/client boundary. | No server circuit for client components; API/network retries and interrupted streams remain application concerns. Client-side streaming is a future integration to validate. |
| Interactive Auto | Host plus client bundle and both execution paths; custom Web App installability work. | Must handle auth on both paths and secure APIs. | Starts on Server and can use WASM on later visits; it does not transfer a running component or provide live offline failover. Both paths need AG-UI testing. |
| Standalone WASM PWA | Microsoft supplies the `blazorwasm --pwa` template; static client assets can be served by the same host, but client bundle and API boundary add complexity. | Client Entra/OIDC integration plus server-side API authorization; no secrets in browser. | No server circuit; network-dependent API and streaming recovery remain explicit work. Future AG-UI client integration must be validated. |

Microsoft documents the [standalone WASM PWA template](https://learn.microsoft.com/en-us/aspnet/core/blazor/progressive-web-app/?view=aspnetcore-10.0);
the Web App template's PWA option is still tracked in
[Microsoft's issue](https://github.com/dotnet/aspnetcore/issues/48935). A
manifest, icons, HTTPS and browser-specific installation checks are therefore
implementation work for the selected Web App, not a claimed template feature.
Installability **does not mean offline editing**. Show an explicit offline or
disconnected state, never queue offline writes, and confirm saves only after
server persistence. Avoid caching authenticated journal responses or secrets
in a service worker; a cached shell must not imply data is current.

Interactive Server minimizes client-side execution and keeps UI and application
services in one host. Its trade-off is a live connection and server-side circuit
state. [Microsoft's circuit guidance](https://learn.microsoft.com/en-us/aspnet/core/blazor/state-management/server?view=aspnetcore-10.0)
describes reconnect and state loss: circuit expiration, host restart or scale-to-zero
can require a reload; do not assume recovery of unsaved input or replay writes.
The owner selected Interactive Server for initial interactive components on
2026-11-17, with static SSR where appropriate. This is an online-first choice,
not an offline-write or recovery guarantee. Prove installability, authentication
and disconnect/reconnect on target browsers before production use. If a
requirement fails, bring the WASM alternative back for approval rather than
claiming that a manifest alone meets the requirement.

For later [AG-UI work](https://github.com/cedricmendelin/bujo-ai/issues/6),
Microsoft's [Interactive Server sample](https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/05-end-to-end/AGUIWebChat)
illustrates streamed updates, not a production authentication or replay
guarantee. Agent-to-server streaming and the browser-to-server circuit are
separate hops. AG-UI package compatibility and recovery behavior remain subject
to the focused .NET 11 spike and production validation.

## Azure SQL baseline and operations

### Local development and migrations

Use local SQL Server for persistence integration tests; a developer container
is disposable infrastructure, not the production database. SQL Server
containers and Windows LocalDB are developer alternatives
([containers](https://learn.microsoft.com/en-us/sql/linux/quickstart-install-connect-docker?view=sql-server-ver17),
[LocalDB](https://learn.microsoft.com/en-us/sql/database-engine/configure-windows/sql-server-express-localdb)).
LocalDB alone does not establish full-text parity. SQL Server and Azure SQL
are not identical: test SQL dialect, collation, full-text language and migration
behavior against the actual Azure SQL configuration before release.

Issue #5 implements the [EF Core SQL Server provider](https://learn.microsoft.com/en-us/ef/core/providers/sql-server/)
and versioned [migrations](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/).
The production migration job uses a separate identity rather than granting the
runtime schema-owner privileges. Avoid automatic application-start migrations.
The deployed runtime instead verifies all release-required migrations and its
table data permissions before opening HTTP, without DDL or test writes.
Azure SQL-specific parity, full-text behavior and restore procedures remain
separate verification work.

### Deployment, identity and availability

Issue #5 plan v9 separates owner-reviewed infrastructure provisioning from
manual application setup and app readiness. An owner-authorized workflow or
local helper applies the exact saved baseline, retaining the existing app image
and an inert migration job; an unhealthy app is an accepted intermediate state,
not a successful release. It then permits automatic routine deployment after successful trusted
`main` CI and explicit one-time activation. PR execution never obtains Azure
credentials. A single Container Apps host and the separately executed migration
job use the same tested immutable image. The app image is promoted only after
the exact migration execution succeeds; see the
[deployment guide](../deployment.md). The database remains a candidate General
Purpose serverless database with the [free tier explicitly enabled](https://learn.microsoft.com/en-us/azure/azure-sql/database/free-tier?view=azuresql);
ordinary serverless without the free-limit setting is billable. Verify eligible
subscription and regional availability. Use the Container App managed identity,
an Entra administrator and a least-privilege database principal; automated
principal creation may require directory permissions for the SQL logical server's
identity ([Microsoft guidance](https://learn.microsoft.com/en-us/azure/azure-sql/database/authentication-aad-service-principal-tutorial)).
Keep credentials and identifiers out of source; restrict network access as the
deployment design permits. API and MCP must enforce authorization independently.

Serverless idle auto-pause needs zero sessions and zero user-pool CPU throughout
the configured delay; open connections can prevent it. General Purpose defaults
to 60 minutes and permits a 15-minute minimum. Geo-replication, long-term backup
retention and server DNS aliases prevent idle pause. A login wakes the database;
the first connection can return 40613, with resume generally taking about a
minute. Use bounded transient retries and visible loading/failure states, and
avoid retrying ambiguous writes blindly. See
[pause/resume behavior](https://learn.microsoft.com/en-us/azure/azure-sql/database/serverless-tier-auto-pause-resume?view=azuresql).
Idle auto-pause is distinct from the free-limit behavior below.

### Minimum and operating cost

The [free-tier FAQ](https://learn.microsoft.com/en-us/azure/azure-sql/database/free-tier-faq?view=azuresql)
states that each eligible database has 100,000 vCore-seconds, 32 GB data and
32 GB backup storage per month; unused compute does not roll over. Within those
allowances the database cost can be USD 0, including zero compute while paused.
The free amount has **no SLA** and Microsoft recommends it primarily for
development and proof-of-concept use. Personal production use therefore carries
an explicit availability risk.

At the monthly free limit:

| Setting | Consequence |
| --- | --- |
| Auto-pause until next month | Database inaccessible until the next calendar month; no uninterrupted journal access. |
| Continue for additional charges | Usage continues at paid rates; this is not a hard budget cap. **Owner-selected for the initial design on 2026-11-17.** |

The owner selected paid continuation to preserve access and accepts that it is
not a hard budget cap. This records a policy preference, not approval to provision
or incur charges. Check eligibility, regional availability, storage and backup
overage behavior separately; do not assume they match compute exhaustion. Set a
budget alert and monitor free amount remaining and `app_cpu_billed`, but alerts
do not prevent charges.

For a concrete sensitivity check, Microsoft's
[billing example](https://learn.microsoft.com/en-us/azure/azure-sql/database/serverless-tier-billing?view=azuresql)
uses 0.5 minimum/4 maximum vCores with a 0.7-vCore billing floor due to memory.
At that floor, 100,000 / (0.7 × 3,600) is about **39.7 online hours/month**,
including idle delay, before heavier usage. An illustrative 30-day month with
two online hours/day at that floor consumes 151,200 vCore-seconds: 51,200 above
the allowance, if paid overage is enabled. This is arithmetic, not a forecast
or a Swiss price. Total monthly cost also includes Container Apps consumption,
storage/backup overages and restores, Log Analytics, Key Vault and networking;
Foundry is excluded. Obtain current Switzerland North rates and a calculator
estimate for both normal and non-pausing scenarios before provisioning.
Until then, meeting the USD 50 target is **unverified**, not a commitment.
[Pricing](https://azure.microsoft.com/en-us/pricing/details/azure-sql-database/single/)
and the [Azure calculator](https://azure.microsoft.com/en-us/pricing/calculator/)
depend on region and configuration.

### Backup, restore and full-text search

Automated backups support point-in-time restore. Under the free tier's
`AutoPause` free-limit option, the FAQ specifies seven-day retention, locally
redundant backup storage, a 4-vCore/32-GB limit and no long-term retention.
Paid-overage mode permits different backup redundancy. The free tier applies
only to a new database: a restore cannot be made into a free-tier database.
Rehearse restore to a paid target, verify data and reconnect the application;
budget for that target and decide the recovery point, retention and export
requirements before production. A backup is not a substitute for a tested
restore. See [automated backups](https://learn.microsoft.com/en-us/azure/azure-sql/database/automated-backups-overview?view=azuresql)
and the free-tier FAQ above.

[Azure SQL full-text guidance](https://learn.microsoft.com/en-us/sql/relational-databases/search/get-started-with-full-text-search?view=azuresqldb-current)
describes catalogs and indexes with a unique, single-column, non-nullable key.
Population is usually asynchronous; language, word breakers and stopwords affect
results. Search is not guaranteed immediately after a write and is not semantic
recall. The cited free-tier exclusions do not explicitly establish full-text
availability on the exact chosen configuration. Before deployment, verify catalog
and index creation, language behavior, search after updates and deletes, and
pause/resume with full-text enabled on that tier. Failure is a configuration
blocker to resolve within Azure SQL, not permission to change providers.

## Modules and tests

Proposed logical boundaries can start as a few class libraries; do not create
projects until the scaffolding plan is approved.

| Boundary | Responsibility |
| --- | --- |
| Host / Web | Composition, hosting, Blazor UI and authentication. |
| Domain | Canonical items, list-local states, one active destination and planned-period invariants. |
| Application | Use cases and authorization-aware orchestration shared by adapters. |
| Infrastructure | Azure SQL persistence and external adapters behind application interfaces. |
| API / MCP adapters | Transport contracts and authentication/authorization boundary; call the same application services. |

Dependencies point inward to Application and Domain; UI, API and MCP do not
maintain separate journal stores. These are logical boundaries, not separate
deployments.

| Test layer | Initial coverage |
| --- | --- |
| Domain unit | Identity, moves, one active destination, planned periods and list closure. |
| Application | Shared use cases, authorization and explicit approval rules. |
| Persistence integration | SQL Server integration tests cover empty-database migration, normalized journal rows, multi-hop persistence across restarts, idempotency, transactional rollback, concurrent moves and close/move races. Azure SQL parity, full-text behavior and restore/recovery checks remain unverified. |
| API / MCP contract | Authentication, approved-account restriction, error and shared behavior contracts; actual MCP clients under the separate auth issue. |
| PWA / E2E smoke, later | Target-browser installation, login, server-confirmed saves, offline indicator, disconnect/reconnect and resume behavior. |

## Consequences, risks and approval

- Server rendering reduces initial deployment complexity but requires connectivity
  and a custom installability proof; lost circuits and in-flight work need clear UX.
- The owner selected paid overage rather than pausing at the free compute limit
  to preserve database availability. This is not a hard spend cap; the free
  tier has no SLA, and actual overage, restore, backup and full-text costs and
  configuration still require verification.
- Verify local versus Azure SQL parity, language behavior, backup restore,
  permissions, and actual monthly spend before production. Do not copy personal
  journal data into test fixtures.
- Release-two AI search/RAG ([#16](https://github.com/cedricmendelin/bujo-ai/issues/16))
  and auth compatibility are separate decisions. Original SQL data remains
  authoritative.

**Approval boundary:** issue #5 approves .NET 11, EF Core SQL Server, and one
modular host with a separate production migration job. The owner selected
Interactive Server and the conditional Azure SQL free-tier/paid-continuation
baseline on 2026-11-17. MCP client compatibility, Azure SQL eligibility and
regional configuration, full-text parity, live authentication, installability,
multi-replica recovery, actual Azure permissions, cost and provisioning remain
unverified. No resources are provisioned by this ADR.
