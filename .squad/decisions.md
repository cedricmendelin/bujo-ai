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

### 2026-11-14: Infrastructure and deployment

- Terraform with Azure Verified Modules wherever available; versions pinned.
- One `prod` environment in Switzerland North; merge to `main` deploys through
  exactly one GitHub OIDC deployment identity with an immutable subject. PR
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

## Governance

- Significant product and architecture decisions require user approval
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
