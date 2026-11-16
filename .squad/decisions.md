# Squad Decisions

## Active Decisions

### 2026-11-14: Local-first team bootstrap

- Use the installed Squad CLI 2.0.0-preview.4 and `squad init --no-workflows`.
- User approved descriptive specialists: Architect, Builder, AI, Verifier, DevOps,
  with Scribe, Ralph, Rai, and Fact Checker as support.
- Architect covers product discovery as well as architecture.
- Ralph is on-request only; no autonomous polling or implementation.
- Sequence: initialize team; challenge product/design together; document agreed
  decisions; create GitHub backlog issues; then separately set up workflows.
- The reference repository is installation guidance, not a workflow package to
  copy now. Do not install gh-aw or cloud workflows in this phase.
- The product concept is in `docs/concept.md`; candidate technologies and open
  questions there are not approved implementation decisions.

### 2026-11-14: Personal first release

- User selected a personal, single-user first release with authenticated access
  and user-owned data.
- Defer sharing, household collaboration, and public signup.
- This does not yet select an identity provider, database, or deployment model.

### 2026-11-14: First usable vertical slice

- User selected capture of tasks/thoughts, Future Log/custom/weekly lists, today's
  dated tasks, and explicit date-based reminders through both PWA and MCP.
- AI-drafted weekly planning follows as the next milestone; Foundry, Gmail,
  Calendar, voice, and all ceremony types are not prerequisites for this slice.
- Reminder delivery, date semantics, offline behavior, and auth remain open.

### 2026-11-14: Online-first PWA

- User selected an installable, online-first PWA with explicit offline state.
- Confirm saves only after the server acknowledges persistence.
- Offline capture, editing, synchronization, and conflict resolution are deferred.

### 2026-11-14: Pull-based reminders in the first release

- User selected showing due reminders when opening the app or asking an assistant.
- Do not require email, web push, a notification provider, or waking an assistant
  for the first slice.
- The initial reminder-date proposal was subsequently replaced by planned
  periods and ceremony-based resurfacing; closed-app delivery remains excluded.
- Background execution of later ceremonies is a separate scheduling decision.

### 2026-11-14: Canonical identity with list-local states

- User clarified that the item is unique but list occurrences have independent
  states: the source can be moved/planned while the weekly destination is open.
- Complete the item on its weekly occurrence, not by replacing every source's
  state with done.
- Preserve multiple moves and expose the final destination/state from any linked
  list. A single global status field is insufficient.
- Exact state names, active-destination cardinality, and resolution rules remain
  under discussion.

### 2026-11-14: One active destination per item

- User selected one active list occurrence/destination per item.
- Earlier occurrences retain their moved/planned state and resolve to the latest
  destination and its current state.
- Example: MVP -> Week 47 -> Week 48; finishing in Week 48 displays a link to
  Week 48 and done from either earlier occurrence.
- Parallel active branches and independent completion destinations are excluded.

### 2026-11-14: Planned periods and ceremony-based resurfacing

- User selected one optional planned period: day, week, month, quarter, year, or
  unplanned. No separate due-date or reminder-timestamp fields in the first release.
- Planned period expresses intention, not a deadline, and is separate from list
  placement. Changing it alone does not create a list move.
- Daily start surfaces today's plans, unfinished earlier plans, and this week's
  items without a specific day. Longer ceremonies revisit broader periods.
- Missed plans remain visible for an explicit decision; never silently reschedule.
- First-release ceremonies are opened by the user or assistant, not automatically
  scheduled. Daily start is a deterministic view; AI-drafted weekly planning
  remains a later milestone.
- This supersedes separate dated reminders in the initial vertical-slice wording.

### 2026-11-14: Assistant write authority

- User selected direct execution of explicit, individual edits such as capture
  and marking an item done.
- Require approval before applying ceremony-generated proposals, bulk moves,
  or destructive changes.
- Keep capture frictionless without silently reorganizing the journal.
- Approval must be tied to the proposed changes, not blanket permission for
  future unrelated writes.

### 2026-11-14: Explicit disposition before list closure

- User selected resolving all unfinished active occurrences before closing a list.
- Each must be moved elsewhere, completed, or abandoned.
- Ceremonies can propose dispositions but require approval before applying them.
- Historical moved links do not block closure; no silent carry-forward.

### 2026-11-14: Default capture location

- User selected Future Log as the default list for captures without an explicit
  list. Items are unplanned unless the request supplies a planned period.
- No separate Inbox is required in the first release.

### 2026-11-14: Modular application boundary

- User selected one modular ASP.NET Core application hosting Blazor PWA, API, and
  remote MCP, sharing application/domain services and an external durable database.
- Foundry is a later optional adapter, not the owner or source of journal data.
- Separate microservices and deployment units are not required initially.

### 2026-11-14: Single-tenant Microsoft Entra ID

- User selected Microsoft Entra ID in an existing tenant for authentication.
- Personal-release access remains restricted to the approved account; membership
  in the tenant alone must not grant access.
- Do not record tenant identifiers, credentials, or account details in source.
- Prove remote MCP authentication with target clients before finalizing its flow;
  app login alone does not establish MCP client compatibility.

### 2026-11-14: Lightweight goals and tags

- User selected optional goal statements on lists and customizable item tags.
- Standalone goal hierarchies, progress dashboards, and AI goal coaching are
  deferred beyond the first slice.

### 2026-11-14: Knowledge capture and search in the first release

- User added a personal knowledge library for explicitly saved learnings,
  concepts, quotes and notes.
- Include basic knowledge capture/search in PWA and MCP in the first release.
- Existing assistants can synthesize retrieved entries; in-app AI Q&A follows
  with the Foundry milestone, not as a first-release prerequisite.
- Existing memory/graph options and the proposed application-owned approach are
  recorded in `docs/knowledge.md`. Exact model and retrieval choices remain open.

### 2026-11-14: Azure SQL for journal and knowledge

- User selected Azure SQL as the shared relational database for journal and
  application-owned knowledge entries, preferring the Microsoft stack.
- Keep original knowledge text as durable source data; no model-inferred memory
  platform, graph database or vector-only store is required.
- Runtime, rendering mode, Azure SQL configuration, local development parity,
  operating costs and restore behavior still need validation before deployment.

### 2026-11-14: Initial knowledge retrieval

- User selected full-text search plus tags for the first release.
- Do not promise semantic recall for differently worded concepts; assistants may
  reformulate queries, but that does not guarantee retrieval.
- Evaluate semantic retrieval later against actual missed examples. Any derived
  indexes/summaries must remain rebuildable from the original saved entries.

### 2026-11-14: Release 2 AI search synchronization and RAG

- User explicitly assigned knowledge synchronization into an AI search service
  and RAG over that index to release 2.
- Release 1 retains Azure SQL full-text search and tags, without a separate AI
  search service, embeddings or synchronization jobs.
- Azure SQL original entries remain authoritative. Release 2 indexes are
  rebuildable derived data with source/version links and update/delete handling.
- Search provider/configuration, models, cost and sync/retrieval behavior require
  validation before provisioning. No provider or live upload is implicitly chosen.

### 2026-11-14: Governed delivery workflow

- Engine: Copilot cloud agent implements; Squad charters are exposed as Copilot
  custom agents (`.github/agents/<member>.agent.md`); gh-aw v1.2.2 runs planning
  and reviews. The Squad gh-aw package and classic Squad workflows are not used.
- Every issue needs an owner-approved implementation plan. The Architect posts a
  versioned plan comment via `/plan`; `/plan revise` and `/plan route` adjust it.
  Only the repository owner's `plan:approved` label assigns Copilot, using the
  plan owner's custom agent. Revisions clear approval.
- Security joins the team as an independent reviewer. Architect, Security,
  Verifier, and DevOps publish required `Squad Review / <Role>` checks; with CI
  green the gate enables squash auto-merge. Blocking findings go back to Copilot
  for at most 3 rounds, then `needs:human`. The owner reviews plans, not code.
- PATs are acceptable for Copilot inference and assignment/merge automation.

### 2026-11-15: Hands-off PR lifecycle and human in the loop

- After `plan:approved`, no human action is expected until merge. Squad PR
  Shepherd marks finished Copilot cloud agent PRs ready, dispatches CI and the
  four reviews per head commit, and asks Copilot to resolve conflicts. The gate
  sends blocking findings to `@copilot` (or to Agent merge for local PRs) and
  squash auto-merges when green.
- Two equal entry points: cloud (default) and local GitHub Copilot app sessions
  (`handoff:local`, Agent merge with reviews/CI/conflicts, merge left to the
  gate). Owner-only `plan:waived` covers PRs without an issue plan.
- Human in the loop is @cedricmendelin: `needs:human` plus a PR mention for stuck
  loops, `needs:human` issues for workflow failures and one-time setup, and
  `## 🙋 Needs human` in PR descriptions from implementers.
- Automation reads checks with `GITHUB_TOKEN` (fine-grained PATs cannot read
  Checks) and writes with the owner PAT.

### 2026-11-15: Issue completion requires acceptance evidence

- Every PR declares `Completion: full` or `Completion: partial`. Full completion
  requires evidence for every issue acceptance criterion, including code and
  relevant tests.
- Partial deliveries use non-closing issue references, preserve unfinished work
  and dependencies, and leave the issue open. A plan's partial fallback never
  satisfies the issue; unmet completion criteria block closure and are not
  advisory.
- The merge gate rejects partial declarations with closing references and checks
  the entire PR description, including generated suffixes. Its text check is
  deterministic, not semantic proof; the independent Verifier remains
  responsible for acceptance evidence.

### 2026-11-15: Copilot session outcome gating

- The latest Copilot cloud-agent workflow run is authoritative for session
  completion; active sessions wait, and only a successful latest session can
  proceed to PR readiness and review dispatch. WIP text is never an outcome.
- Interrupted sessions receive bounded, idempotent recovery only under approved
  plan scope. A failed recovery, exhausted bound, or unapproved scope escalates
  to `needs:human`; the gate independently requires session success and the
  shepherd's current-head marker, and disables prior auto-merge after a newer
  interruption.
- GitHub `action_required` is an approval block, not a passing check. Keep
  workflow approvals unchanged; diagnose the event and rely on owner-dispatched
  CI/reviews without weakening required checks.

### 2026-11-14: Infrastructure and deployment

- Terraform with Azure Verified Modules wherever available; versions pinned.
- One `prod` environment in Switzerland North; the original merge-to-deploy
  decision is superseded by the issue #5 staged-bootstrap decision below.
  Deployment uses exactly one GitHub OIDC identity with an immutable subject. PR
  workflows never authenticate to Azure; the owner runs plans locally.
- No private networking. Cheapest reasonable SKUs, target under 50 USD/month:
  Container Apps consumption (scale to zero), Azure SQL free offer
  (serverless/auto-pause), GHCR instead of ACR, capped Log Analytics, Key Vault
  standard, user-assigned managed identity, budget alert. Foundry excluded until
  its milestone.
- Azure Blob remote state is bootstrapped manually. The production resource group
  and state storage exist before Terraform; the deployment identity has only
  resource-group Contributor and state-container Blob Data Contributor.
- Terraform creates the RBAC-enabled workload Key Vault and runtime identity but
  creates no role assignments. The owner grants Key Vault Secrets User to the
  runtime identity at vault scope after first apply. The Azure SQL Entra-only
  administrator is an owner-created security group.

### 2026-11-14: Domain state and transition semantics

- Issue #3 plan v1 defines canonical items with list-local `Open`, `Done`,
  `Abandoned`, and `Moved` occurrences and one linear move chain per item.
- Done and abandoned occurrences can be explicitly reopened only on an open
  list while still current; returning to an earlier list creates a new visit.
  Historical views use current canonical text and tags.
- Planned periods are independent calendar values; weeks use ISO 8601 and
  today is derived in the user's IANA time zone. A conflicting planned day or
  week in a weekly list is flagged without rewriting either value.
- The Future Log is ongoing; closure of other lists requires disposition of
  every open occurrence. Moves are atomic, idempotent and concurrency-checked.
- The full specification is in `docs/domain.md`; the issue #5 technology
  baseline is recorded below.

### 2026-11-15: Issue #5 technology baseline

- The owner-approved issue #5 plan v4 selects .NET 11, EF Core SQL Server, and a
  single modular ASP.NET Core host with shared domain/application services.
- Podman and Aspire are selected for local development only. Production remains
  a single host deployment; the AppHost is not deployed.
- This issue-specific approval does not finalize the Blazor render mode, Azure
  SQL free-tier behavior, or MCP client compatibility. Those remain separate
  decisions and proofs.
- Production migrations use a separate manually triggered Container Apps job
  with a dedicated identity; the web runtime identity retains data access only.
  Infrastructure, permissions, and rollout feasibility must be verified before
  production changes, and no Azure setup is authorized by this decision.

### 2026-11-16: Issue #5 staged bootstrap

- Approved plan v5 separates an owner-reviewed, owner-applied bootstrap from
  SQL grants and the later migration and application deployment.
- Bootstrap must not run a migration or change an app revision. Production
  deployment is manually dispatched by the owner from `main`; merge alone does
  not deploy. The stage-one plan path remains to be implemented.

## Governance

- Significant product and architecture decisions require user approval
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
