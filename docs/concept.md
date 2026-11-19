# Bujo-AI product concept

## Vision

Organize your thoughts, plan your tasks, and avoid losing important intentions.
Support a mindful life rather than adding another demanding productivity system.

This preserves the original vision alongside the first-release choices made
during discovery. Sections marked agreed define the current scope; other features
remain candidates, not commitments. The decision history lives in
[Squad decisions](../.squad/decisions.md).

## Interaction and architecture direction

**Agreed first-release audience:** personal, single-user use with authenticated
access and user-owned data. Sharing, household collaboration, and public signup
are deferred. Authentication uses single-tenant Microsoft Entra ID with the
approved account allowlisted. Exact remote MCP client authentication and
deployment configuration still require validation; no tenant/account identifiers
or credentials belong in the repository.

- Blazor progressive web app and API hosted with Azure Container Apps.
- Agreed boundary: one modular ASP.NET Core application hosts PWA, API, and remote
  MCP over shared domain/application services; external Azure SQL owns durable
  data. Foundry is a later optional adapter, not the journal's source of truth.
- Agreed first-release connectivity: installable but online-only, minimal
  circuit-loss recovery, and server-confirmed saves; no offline write/sync queue.
- Azure SQL is the durable source for journal and knowledge data; hosting
  configuration, local development and operating cost still require validation.
- **Agreed first in-app AI experience:** a weekly-planning coach launched from
  the weekly view. It receives relevant unfinished items and the user's stated
  constraints, then returns a structured plan for review; no journal changes
  happen before the user approves the exact proposal. General journal Q&A and
  proactive actions are outside this first AI slice.
- **Accepted chat architecture:** app-owned orchestration and authorization in
  the existing ASP.NET Core host, with AG-UI for the Blazor Interactive Server
  experience and an optional server-side Foundry model provider. Foundry is not
  the journal source of truth or a required hosted-agent runtime. A local
  .NET 11 spike validated the mock Preview path; production readiness remains
  unverified. See the [accepted ADR](adr/0002-foundry-agui-chat.md).
  See the [issue #6 research](ai-chat.md) and
  [proposed ADR](adr/0002-foundry-agui-chat.md).
- MCP access for assistants such as Copilot and Claude, sharing application
  capabilities rather than maintaining a second copy of the user's journal.
- Scheduling configurable through the application or assistant.
- Expose assistant setup guidance through MCP prompts and/or skills, including
  examples for configuring scheduled prompts. Client support and execution
  capabilities must be verified; exposing a prompt does not install a scheduler.
- Longer-term app interaction candidates include simple text, voice and
  web-search flows; more sophisticated orchestration can remain in external
  assistants.

Container Apps is a hosting direction, not a decision to put the database or
durable scheduler state in an application's ephemeral filesystem.

## Proposed domain

**Agreed first usable slice:** capture tasks/thoughts, Future Log/custom/weekly
lists, optional planned periods, and a daily-start view that resurfaces relevant
items, accessible through both PWA and MCP. AI-drafted weekly planning is the next
milestone. Optional list goal statements and customizable item tags are included;
standalone goal systems and dashboards are deferred. Other capabilities below
remain later candidates unless explicitly agreed.

First-slice resurfacing is pull-based: ceremonies show relevant items when opened
in the app or requested through an assistant. Email, web push, proactive delivery,
and waking external assistants are not required.

Captures without an explicit list go to Future Log and are unplanned unless the
request supplies a planned period. A separate Inbox is not part of this slice.

### Agreed planning model

Each item has one optional planned period with day, week, month, quarter, or year
precision, or is unplanned. There are no separate planned/due/reminder dates.
"In two weeks" selects a week; "next Tuesday" selects a day.

Planned period expresses intention, not a hard deadline. It is separate from list
placement: an item can be in a weekly list and planned for Tuesday. Editing the
planned period alone does not create a list migration.

Daily start surfaces today's plans, unfinished earlier plans, and this week's
items without a specific day. Weekly planning revisits unfinished work, upcoming
periods, and backlog; longer ceremonies revisit broader intentions. Missed plans
remain visible for an explicit decision, not automatic rescheduling. Initial
ceremonies are user-invoked; scheduled execution is deferred.

| Concept | Initial proposal |
|---------|------------------|
| Fixed lists | One ongoing Future Log; weekly, monthly, quarterly, yearly lists per period |
| Custom lists | Name, open/closed status, goal, items |
| Items | Name, description, tags, status, type |
| Item statuses | Open, moved, done, abandoned |
| Item types | Task, thought |
| Tags | User-defined categories such as Work and Family |
| Goals | Agreed initially: optional list goal statement; standalone goal system deferred |
| Ceremonies | Weekly, monthly, quarterly, yearly retrospective and planning |

The agreed invariants below constrain the schema; remaining edge cases must be
resolved explicitly during domain design rather than silently assumed.

### Agreed identity and list-local state direction

An item has one identity but can be linked from different lists with different
states. Planning an MVP item into a weekly list leaves the MVP occurrence marked
`moved` or `planned`, linked to its destination; the weekly occurrence is `open`.
Completion happens on the weekly occurrence. Multiple moves must remain
traceable, and any linked list must make the final destination/state visible.
There is exactly one active destination per item; earlier occurrences retain
their local state and resolve to that latest occurrence and its current state.
For MVP -> Week 47 -> Week 48, completing in Week 48 lets either earlier entry
show "moved -> Week 48, done". Parallel active branches are excluded. Exact
state terminology still needs clarification; do not model status as a single
shared field on the item.

A list can close only after all unfinished active occurrences have an explicit
disposition: moved, done, or abandoned. Historical moved links do not block
closure. No automatic carry-forward; a ceremony may propose changes for approval.

## Personal knowledge library

Persist explicitly captured concepts, quotes, notes and other learnings, then
support questions over that saved knowledge. This is distinct from task planning
and from automatically inferred assistant memory. See the
[initial investigation](knowledge.md) for existing options, a recommended
application-owned store, and open scope/retrieval decisions. Basic capture/search
through PWA and MCP is included in the first release; in-app AI Q&A follows the Foundry
milestone. Store original knowledge entries in application-owned Azure SQL tables,
with full-text search and tags initially. No dedicated memory platform or
semantic index is required. Detailed entry lifecycle and retrieval configuration
remain to be specified.

**Release 2:** synchronize the saved knowledge into an AI search service and
enable RAG over that derived index. Original Azure SQL entries remain the source
of truth; index rebuilds and update/delete propagation must be supported. Search
provider and configuration are still to be selected. No AI search service,
embeddings or synchronization jobs are required in release 1.

## Planning and follow-through

**Agreed assistant authority:** direct execution for explicit individual edits;
approval before applying ceremony-generated proposals, bulk moves, or destructive
changes. Capture should stay frictionless; AI must not silently reorganize data.

Ceremonies can draft the next period's list from existing lists and backlog,
with customizable schedules and additional instructions. Drafting must be
distinguished from committing changes.

Desired integrations include Gmail and Google Calendar, reminders, and
assistant-driven actions. Whether these integrations belong in Bujo-AI or in
external assistants is unresolved. Connecting an assistant does not implicitly
authorize reading private sources or sending messages.

Conditional reminders such as "if I have not heard from someone" require an
explicit evidence source, permission, and defined behavior when evidence is
unavailable. The system must not silently treat missing evidence as proof.

## Example journeys

- Capture a thought or task with minimal friction.
- "Add durable task scheduler to my MVP list."
- "I read about RTK; it sounds interesting." Capture it for later review.
- "In two weeks I need to order diapers." Capture an item planned for that week,
  resurfaced by the relevant ceremony.
- "If I have not heard from Peter, ask him in two weeks how he is."
  Clarify whether to remind the user or actually send a message.
- "What do I need to do today?"
- Set goals and plan the week.
- Review older backlog items and decide whether to act, move, or abandon them.
- "We have a free family weekend next week; what could we do?"
  Consult saved ideas and, if authorized, calendar and web information.

## Decisions to challenge together

- How do calendar conventions, timezone, and contradictory list/period choices work?
- What are the exact list-local states and reopening rules?
- Who executes later proactive reminders, through which delivery channel, and with what
  timezone, retry, deduplication, and missed-run guarantees?
- Do target-browser installability and reconnect behavior work for the selected
  Interactive Server mode, and is the selected Azure SQL configuration eligible
  and affordable? The [accepted technology baseline](adr/0001-technology-baseline.md)
  records these choices and the verification gates before provisioning.
- How does Entra ID authorization work in the actual Copilot and Claude clients?
  See the [proposed auth design and blocked client proofs](auth.md).
- How are retention, export/deletion, and backups handled?
- Which knowledge entry fields/lifecycle and search-language behavior are needed?
- Which integrations and operating costs are acceptable in later phases?

## Delivery sequence

1. Initialize a local Squad team without agentic workflows.
2. Resolve product and architecture choices interactively.
3. Evolve this concept and record accepted decisions.
4. Create scoped GitHub issues with acceptance criteria and dependencies.
5. Run every implementation issue through the governed workflow
   ([workflow.md](workflow.md)): owner-approved plan, Copilot implementation,
   automated Squad Review, auto-merge, and CI-triggered deployment from `main`.
