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

### 2026-11-19: Release 1 knowledge contract and lifecycle

- Knowledge entries have an immutable stable GUID and preserve required original
  content verbatim. Title and source reference are optional data; source URLs are
  never fetched. Tags reuse journal normalization.
- Edit is versioned and updates the entry in place. Archive is reversible; hard
  delete is explicit, permanent, and recoverable only from backup. Release 1 does
  not retain content history.
- Search uses English (LCID 1033) full text for text queries, AND tag filters, and
  bounded keyset pagination ordered by rank, update time, and ID. Tag-only search
  orders by update time and ID.
- Readiness is `Ready`, `Populating`, or `Unavailable`. A disclosed, bounded
  recent-entry fallback keeps new saves retrievable while full text is not ready;
  fallback does not claim complete semantic or historical coverage.
- A versioned journal backup must include knowledge entries, tags, source
  references, archive state, timestamps, IDs, and exact text. Restore accepts
  older snapshots without the knowledge section and replaces validated journal
  and knowledge state atomically.
- Knowledge remains in the journal identity and Azure SQL boundary and is
  untrusted data, never privileged tool instructions.

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
  not deploy. The owner reviews the exact stage-one plan before applying it.
- Approved plan v6 also permits the owner to plan and apply stage one locally
  with an interactive Azure CLI user login. The owner checks the subscription,
  safety constraints and exact saved plan; GitHub deployments remain OIDC-only.

## Governance

- Significant product and architecture decisions require user approval
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction

### 2026-11-23: Automatic Watch and fix enrollment

- Owner decision superseding the manual per-PR enablement requirement in the
  2026-11-16 PR finalization ownership redesign: enroll eligible process PRs
  automatically in GitHub.com Watch and fix for failed Actions workflows and
  merge conflicts. Preserve the existing human-pause, formal-rejection and local
  takeover safeguards; do not undo a maintainer's later unassignment.
- The trusted controller, API permissions, eligibility limitations and
  operational recovery are recorded in `.github/workflows/squad-pr-lifecycle.yml`
  and `docs/workflow.md`. GitHub auto-merge remains the only merge executor.

### 2026-11-16: PR finalization ownership redesign

- This supersedes only the PR lifecycle ownership and merge-authority portions
  of the 2026-11-14 governed-delivery and 2026-11-15 hands-off-lifecycle
  decisions. Owner-approved plans, `plan:approved`, the existing acceptance
  evidence rules, and the independent review requirements remain in force.
- Cloud-first PR repair uses GitHub.com Copilot **Watch and fix** for failed
  Actions workflows and base-branch conflicts; it does not handle review
  comments. The owner must enable repository Copilot automations and Watch and
  fix for each applicable PR, or configure the account default. The account
  default applies to PRs the user creates and must not be assumed to cover
  Copilot-created PRs.
- GitHub auto-merge is the only merge executor and performs squash merges under
  branch protections. The trusted gate validates eligibility, arms auto-merge,
  and publishes the aggregate `Squad Merge Readiness` check; it must never
  directly merge as a fallback. The `main` ruleset must require `ci`, all four
  `Squad Review / <Role>` checks, `Squad Plan Binding`, and
  `Squad Merge Readiness`. Owner-side ruleset setup is separate from workflow
  delivery.
- The cloud shepherd is limited to successful-session readiness, initial CI
  and review dispatch, bounded interrupted-session recovery, explicit metadata
  proposal application, reviewer-feedback forwarding, and escalation. It no
  longer repairs conflicts. It continues bounded `@copilot` requests for
  eligible Squad check findings and metadata proposals. Submitted human/Copilot
  review feedback means unresolved, non-outdated trusted review threads or
  change requests; poll it on `Squad Plan Binding` completion (about every ten
  minutes) and an hourly fallback. Do not treat arbitrary top-level comments as
  review feedback.
- `needs:human` is a stop, not a status cleared by green checks. Automation must
  not silently remove it. The owner must separately stop native Watch and fix
  when pausing work; `needs:human` does not control that GitHub feature.
- The trusted metadata bridge keeps its body/head/hash guards. A title
  replacement is optional and is valid only when both `title` and
  `baseTitleSha256` are supplied; they are omitted together otherwise. A
  description that is the owner-waived scope remains owner-only to change.
- A local GitHub Copilot app session with `handoff:local` is an optional manual
  takeover. Agent merge's Merge action stays off so it cannot compete with
  GitHub auto-merge, and the owner disables native Watch and fix for that PR to
  prevent concurrent fixes.

### 2026-11-16: Simplified plan approval and PR lifecycle

- This supersedes the approval-provenance, plan-binding, review-attestation,
  metadata-proposal, scheduled-reconciliation, and required-check portions of
  the earlier delivery and PR-finalization decisions.
- Keep `/plan`, `/plan revise`, and `/plan route`, with append-only numbered
  issue comments. Chat-authored plans use the same version convention. A new
  version clears `plan:approved`; applying that label starts cloud assignment
  unless `handoff:local` is already present. No bot binding or hashes.
- Direct local PRs use chat-agreed scope without an issue or waiver prerequisite.
  Keep honest completion evidence and non-closing linkage for partial work.
- Architect, Security, Verifier, and DevOps remain default reviewers. PR-only
  `review:none` skips existing and pending specialist findings, but never CI or
  trusted human change requests. Removing it starts fresh reviews.
- Use one event-driven default-branch lifecycle controller and current-head
  results. Cosmetic PR-description edits do not restart model reviews.
  `ci` and aggregate `Squad Merge Readiness` become the required checks after
  the owner stages and verifies the ruleset migration.
- Forward blocking review findings for at most three actual repair attempts;
  queued jobs, cancellations, and orchestration retries do not consume them.
  `needs:human` remains an explicit pause and is never silently cleared.
- Native Watch and fix owns cloud CI/conflicts; desktop Agent merge owns local
  repairs after cloud takeover is coordinated. A handoff label does not cancel
  native running work. GitHub protected squash auto-merge is the only merge
  executor; no direct merge or protection bypass.
- The owner separately coordinates active PRs and performs the ruleset/label
  rollout. Code delivery does not imply live settings have changed.

### 2026-11-17: Interactive Server and conditional Azure SQL baseline

- The owner accepted Blazor Interactive Server for the initial interactive UI,
  with static SSR where appropriate.
- The owner selected Azure SQL General Purpose serverless free tier where
  eligible, with paid continuation at the free compute limit to preserve access.
  This is not a hard spend cap.
- Installability, Azure eligibility/region/configuration, permissions, full-text
  behavior, restore, and actual cost remain verification gates before
  provisioning. ADR acceptance does not authorize resource creation.
- See `docs/adr/0001-technology-baseline.md`.

### 2026-11-17: Weekly-planning coach and AG-UI architecture

- The owner selected a weekly-planning coach launched from the weekly view as
  the first in-app AI experience. It proposes a structured, version-checked
  plan; no journal changes occur before explicit approval. General journal Q&A
  and proactive actions are outside this first AI slice.
- The owner accepted app-owned orchestration, authorization and session state
  in the existing ASP.NET host, with an optional server-side Foundry model
  provider. Foundry Agent Service is not a required runtime.
- The owner accepted AG-UI for the Interactive Server path after a local
  .NET 11 mock spike. Production package adoption, inference and Foundry
  provisioning remain subject to ADR 0002's validation gates.
- Chat transcript persistence and retention remain undecided.
- See `docs/concept.md` and `docs/adr/0002-foundry-agui-chat.md`.

### 2026-11-17: Demo infrastructure delivery

- Bujo-AI is a demo for trying agent capabilities and practicing SDLC. Keep
  PR review and CI; do not add a second approval process for Terraform.
- Supersedes the separate infrastructure plan/hash approval: successful `main`
  CI applies pending `infra/` changes. The owner can also dispatch a single
  plan/apply run to reconcile drift.
- Keep GitHub OIDC, private state, and migration-before-promotion for app releases.

### 2026-11-17: Capability-aware partial delivery and repair recovery

- Implement the PR completion recovery proposal agreed in the Copilot
  conversation. This supersedes session-completion-based repair counting and
  head-only review freshness in the simplified lifecycle decision.
- Keep honest issue completion separate from an explicitly agreed mergeable
  milestone. Partial work never closes an unfinished issue or becomes full
  merely to unblock merging. Preserve unfinished criteria, owner, dependency,
  evidence needed, and next action on the issue.
- The trusted controller repairs narrowly recognized generated closing suffixes
  before specialist review. Other metadata errors receive explicit feedback,
  not impossible coding requests. Never broaden into arbitrary model-authored
  metadata patches or revive approval binding/provenance protocols.
- Key reviews to both head and substantive description input; ignore only
  whitespace-only cosmetic changes. Reviewers inspect the applicable code even
  when metadata is wrong; incomplete inspection cannot pass.
- Count only request-attributed substantive cloud repair outcomes or local code
  changes. Capability/dependency failures pause once, without consuming three
  attempts. Missing attribution requires inspection, not optimistic inference.
- A maintainer's explicit lifecycle resume deliberately clears the human pause
  and starts a bounded recovery episode; ordinary events never do. A code
  commit does not reset the budget. Preserve all prior outcomes in history.
- Manual issue continuation with dependency evidence works after a partial PR
  merges even if Copilot remains assigned. Keep the approved plan, reject
  competing open-PR/active-session work, and require a revised approval for
  changed scope. Settings, live PR recovery, and deployment remain separate.

### 2026-11-18: Adaptive PR reviews and application security scope

- Supersede the fixed reviewer requirement in “Governed delivery workflow”
  and “Simplified plan approval and PR lifecycle”. Start in observation mode:
  keep all four reviews and compare proposed routing with actual findings.
- After checking observation results, require only `ci` and
  `Squad Merge Readiness` in the ruleset before enabling
  `SQUAD_REVIEW_ROUTING=enforce`. Unset the variable to restore all four
  reviews through the aggregate check. CI, trusted human blockers, and the
  existing `review:none` opt-out are unchanged.
- In enforcement mode, route ready PRs to Verifier plus at most two
  specialists, based on changed paths and current-head owner escalation.
  Architect owns design and contracts; DevOps owns infrastructure, CI, and
  deployment. Security focuses on application risk: authorization, sensitive
  data access, MCP permissions, and application trust boundaries. Routine
  endpoint or infrastructure changes do not require Security review solely
  because of their location; escalate changes that affect those boundaries.
- If all three specialists apply, select Security and DevOps alongside
  Verifier. Block readiness until the PR is split or the repository owner
  personally reviews the omitted Architect scope and submits the exact
  `Squad coverage:` line in a GitHub approving review. The line is bound to
  the head, base, substantive description, and selected roles. This covers
  the omitted review only; it does not bind or approve issue plans, bypass
  branch protections, or revive the previous approval-provenance protocol.

### 2026-11-18: Optional Foundry infrastructure boundary

- At the time of this decision, Foundry model resources were disabled pending
  owner confirmation of the model, region, deployment SKU, pricing and privacy
  terms. The later approval below supersedes that pending status.
- The runtime Container App identity is the intended model-inference principal.
  The intended GitHub OIDC deployment scope remains resource-group Contributor;
  Contributor does not provide role-assignment administration rights or model
  access, even if assigned at subscription scope.
- The owner must grant `Cognitive Services OpenAI User` to the runtime identity
  at the Foundry account scope before inference. Provisioning does not authorize
  sending journal content and does not enable the application adapter.
- Data Zone Standard is EU-zone processing, not a Switzerland residency
  promise. No under-$50 monthly Foundry cost claim is made; the budget alerts
  but does not cap pay-per-token usage.

### 2026-11-18: Globally unique journal list names

- The owner reversed the earlier allowance for repeated list names and directed
  that list names be unique. This is a global, case-insensitive rule across
  list kinds, recorded from the owner's direct instruction in the Copilot
  conversation for issue #124 on 2026-11-18.
- The owner direction covers list names; it does not explicitly establish a
  separate uniqueness rule for keyed weekly lists by ISO year/week. PR #133
  currently implements that additional rule as an unconfirmed proposal. Do not
  treat it as an accepted domain invariant until the owner confirms it or it is
  removed from the change.
- The approved plan v3 says no migrations are expected. The database migration
  in PR #133 is a scope deviation required to enforce the owner-requested
  unique-name constraint in SQL. Any retained weekly-key constraint also needs
  to be addressed explicitly in an approved plan revision.

### 2026-11-18: Weekly lists may share a week key

- The owner decided not to enforce uniqueness of weekly-list ISO year/week
  keys, while retaining globally case-insensitive unique list names. This
  clarifies the prior proposal in the issue #124 Copilot conversation.
- Multiple weekly lists may therefore use the same ISO year/week key. Do not
  add a domain or database uniqueness constraint for the week key.

### 2026-11-18: No Python infrastructure or deployment test suites

- The owner directed the team to remove all Python tests for infrastructure and
  deployment behavior and not add such suites again.
- Keep appropriate Terraform-native tests and `fmt`/`validate`, Node workflow
  tests, and .NET tests. Python implementation and CI reporting scripts are not
  covered by this prohibition.

### 2026-11-18: Foundry model provisioning approved

- This supersedes the pending-approval status in the earlier Optional Foundry
  infrastructure boundary decision.
- The owner approved enabling production Foundry resources in `infra/prod.tfvars`
  with the configured Switzerland North models and Data Zone Standard SKU at
  capacity 1: chat `gpt-6.1-terra` / `2026-10-26`, decision `gpt-5-mini` /
  `2025-08-07`, and embedding `text-embedding-3-small` / `1`.
- The owner accepted the documented data-privacy and abuse-monitoring terms for
  provisioning and approved the documented ~$9.10 usage illustration as a
  planning assumption. It is not a cost cap; the $50 budget only alerts.
- The owner will manually grant `Cognitive Services OpenAI User` to the runtime
  Container App identity at the Foundry account scope before inference.
  Terraform does not manage that role assignment. Production leaves
  `foundry_access_group = null`, so group roles also remain external and the
  GitHub OIDC permissions are unchanged. Provisioning does not enable the
  application adapter or authorize sending journal data.

### 2026-11-19: General chat start page

- Chat is the authenticated `/` start page; Today is a separate `/today` view.
- The app-owned runtime uses a CI-safe mock provider by default and an optional
  server-configured Foundry provider. Feature state defaults to off.
- The 22-tool chat registry shares names and metadata with the MCP journal tools.
- Direct explicit edits execute through application services; ceremony, bulk
  and destructive mutations use existing immutable, digest/version-checked
  PWA approval and apply behavior.
- Speech recognition is single-shot and configured for `en-US`. Live G2
  microphone verification remains an owner gate; no live result is claimed.
- No transcript or audio store is added and no live Foundry role assignment is
  performed by application code.
### 2026-11-19: Foundry matrix supersedes unsupported deployment selections

- The owner supersedes the earlier Foundry model matrix after the production
  apply rejected its GPT decision and small-embedding deployments.
- Production will deploy `gpt-6.1-terra` / `2026-10-26`, `gpt-6.2-sol` /
  `2026-11-17`, and `gpt-6.2-astra` / `2026-11-17` with `DataZoneStandard`
  SKU at capacity 1.
- The embedding deployment will use `text-embedding-3-large` / `1` with the
  `Standard` SKU at capacity 1.
- This decision does not assert that all catalog combinations remain available,
  quota-approved, or cost-estimated in the subscription. The Azure apply is the
  live availability check. The existing $50 budget remains an alert-only limit,
  not a Foundry usage cap.

### 2026-11-20: Simplified Squad Merge Readiness and human resume

- The owner supersedes the earlier readiness gate that also validated the
  completion declaration, acceptance checklist, tracking recovery, and
  scope-split records. Cloud sessions cannot edit PR bodies, issue bodies, or old
  comments, so those gates dead-ended.
- `Squad Merge Readiness` now succeeds when `ci` is green, every selected
  reviewer's latest current-head check is an approval, and no `needs:human`
  label is present. Trusted human blocks and drafts still block it. The Verifier
  owns completion and acceptance review.
- Reviewer verdict titles are `✅ Approved` or `❌ Changes required: N blocking`.
  Fixes are append-only: new commits and new reviewer checks per head.
- A person removing `needs:human` resumes the PR and resets the repair-attempt
  budget; bot removals do not.
- The owner sets `SQUAD_REVIEW_ROUTING=enforce` (a repository variable) after
  merge. This decision authorizes only this scope; no ruleset change is implied.

### 2026-11-23: Automatic three-agent PR review limit

- Supersede the 2026-11-18 overflow gate. Every PR not using `review:none`
  automatically routes to Verifier plus at most two applicable specialists in
  both observation and enforcement modes.
- Resolve overflow by the existing deterministic priority: Security, DevOps,
  then Architect. An omitted specialist and that role's current-head check do
  not block readiness; no owner `Squad coverage:` approval is required.
- Enforcement still blocks incomplete or unmapped changed-file inventories.
  CI, trusted human blockers, drafts, `needs:human`, and `review:none` remain
  unchanged.
