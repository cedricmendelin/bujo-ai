# Bujo-AI initial backlog

## Outcome and boundaries

Build a personal, single-user, Entra ID-protected journal with online-first PWA
and remote MCP access. Both use shared application rules and external Azure SQL
in one modular ASP.NET Core deployment.

The first release supports capture, Future Log/custom/weekly lists, list goal
text, tags, planned periods, daily start, and explicit weekly review. Canonical
items have list-local occurrences and one current destination; earlier moved
entries resolve the final destination and state.

The first release also includes a personal knowledge library: original
notes/concepts/quotes in application-owned Azure SQL tables, full-text search and
tags through PWA/MCP. External assistants can synthesize retrieved entries; in-app
AI Q&A follows later. No dedicated memory service or semantic index is required.

No separate deadlines/reminder timestamps, closed-app notifications, automatic
rollover, offline writes, public signup, sharing, or AI dependency in the core.
Individual explicit assistant edits are direct; bulk, destructive and
ceremony-generated changes require approval of the concrete proposal.

This is the initial dependency map, not a second status tracker. GitHub issues
are authoritative for progress. The [concept](concept.md) explains the vision
and [.squad/decisions.md](../.squad/decisions.md) records agreed choices.

## First usable release

| Issue | Outcome | Prerequisites |
|-------|---------|---------------|
| [#2](https://github.com/cedricmendelin/bujo-ai/issues/2) | Approve runtime/rendering and validate Azure SQL configuration | None |
| [#4](https://github.com/cedricmendelin/bujo-ai/issues/4) | Prove Entra authentication for app and actual MCP clients | None for discovery |
| [#3](https://github.com/cedricmendelin/bujo-ai/issues/3) | Specify domain transitions and implement item/occurrence/planned-period invariants | #2 before implementation; specification can begin independently |
| [#5](https://github.com/cedricmendelin/bujo-ai/issues/5) | Modular host, persistent shared services, authenticated API | #2, #3, #4 |
| [#9](https://github.com/cedricmendelin/bujo-ai/issues/9) | Calendar contract and deterministic daily-start query | #3, #5 |
| [#10](https://github.com/cedricmendelin/bujo-ai/issues/10) | Installable online-first Blazor PWA | #2, #4, #5, #9 |
| [#11](https://github.com/cedricmendelin/bujo-ai/issues/11) | Authenticated MCP tools, parity and setup guidance | #4, #5, #9 |
| [#12](https://github.com/cedricmendelin/bujo-ai/issues/12) | Explicit weekly review, concrete approvals, safe closure | #3, #5, #10, #11 |
| [#14](https://github.com/cedricmendelin/bujo-ai/issues/14) | Persistent knowledge capture, full-text/tag retrieval | #2, #4, #5, #10, #11 |
| [#17](https://github.com/cedricmendelin/bujo-ai/issues/17) | Terraform (AVM) infrastructure and OIDC continuous deployment; pilots the delivery workflow ([infrastructure guide](infrastructure.md)) | #24 bootstrap |
| [#13](https://github.com/cedricmendelin/bujo-ai/issues/13) | Acceptance evidence, recovery and Container Apps readiness | #2, #4, #10, #11, #12, #14 |

Technology/auth discovery can start independently. Resolve ambiguous state,
calendar and reopening semantics before dependent implementation, rather than
turning guesses into behavior. No implementation is dispatched by these labels.

## Next AI milestone and release 2 knowledge retrieval

| Issue | Outcome | Prerequisites |
|-------|---------|---------------|
| [#6](https://github.com/cedricmendelin/bujo-ai/issues/6) | Verify Foundry/AG-UI/Blazor integration and approve approach | #2, #4 |
| [#8](https://github.com/cedricmendelin/bujo-ai/issues/8) | User-invoked AI drafts for weekly retro/planning | #5, #6, #10, #11, #12, #13 |
| [#16](https://github.com/cedricmendelin/bujo-ai/issues/16) | Release 2: SQL-to-AI-search synchronization and retrieval for RAG | #4, #13, #14 |
| [#15](https://github.com/cedricmendelin/bujo-ai/issues/15) | Release 2: source-grounded in-app knowledge Q&A over the index | #6, #13, #14, #16 |

AI drafts never silently become committed plans. Foundry project/model selection
and paid live calls need approval at execution time.

Knowledge synchronization and RAG are explicitly release 2. Original Azure SQL
entries remain authoritative; AI search is a rebuildable derived index. Provider,
configuration, cost and live-data upload still require approval.

## Later vision

[#7](https://github.com/cedricmendelin/bujo-ai/issues/7) preserves longer-period
lists/ceremonies, durable scheduling, assistant skills and scheduled-prompt
guidance, Gmail/Calendar, conditional follow-ups, voice, web search, optional
proactive delivery, richer goals and family idea discovery. Refine each adopted
workstream into focused issues before implementation.

## Review and workflow gate

The [GitHub roadmap](https://github.com/cedricmendelin/bujo-ai/issues/1) tracks this
backlog. Implementation of any issue requires an owner-approved plan
(`plan:approved`) per the [delivery workflow](workflow.md). Cloud provisioning
and deployment happen only through reviewed, merged PRs.

The Terraform/OIDC continuous-deployment issue [#17](https://github.com/cedricmendelin/bujo-ai/issues/17) pilots the workflow and provides
the Azure environment that #5 and #13 build on.
