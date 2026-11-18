# Issue #124 delivery and acceptance matrix

**Status:** Architect design/evidence milestone only. Issue #124 remains open;
this matrix is not evidence that the journal UI, external sign-in, installation,
or production rollout is complete. Reconciled on 2026-11-18 against merged PRs,
the current source, and the linked issue handoffs.
**Approved scope:** [Issue #124 implementation plan v1](https://github.com/cedricmendelin/bujo-ai/issues/124#issuecomment-6974280111).

## What is delivered, verified, and still external

Issue #10 was closed after PR #108 merged, but PR #108 declares
`Completion: partial` and explicitly leaves journal screens and the journal
journey unfinished. This follow-up, [issue #124](https://github.com/cedricmendelin/bujo-ai/issues/124),
is the tracking location for that remaining scope. The historical issue and PR
are preserved; their closed/merged state is not treated as proof of the
unfulfilled UI criteria.

The matrix separates three kinds of evidence:

- **Implementation:** merged code or documentation, linked to its delivery.
- **Local verification:** automated tests or local observations, which do not
  prove a deployed identity or browser installation.
- **External rollout:** owner-authorized live identity, target-browser, and
  production checks. No such check is inferred from a local run.

## Issue #10 acceptance criteria

| Criterion | Delivered implementation | Local verification | External rollout | Remaining owner, dependency, and next action |
| --- | --- | --- | --- | --- |
| Approved-account sign-in and safe signed-out/denied/error states | PR [#108](https://github.com/cedricmendelin/bujo-ai/pull/108) implements the BFF, cookie/UI boundary, account pages, and server-side token handling. The API remains bearer-only. | Integration and synthetic-browser coverage is described in [auth.md](auth.md); it does not contact Entra. The owner reported local Entra sign-in on 2026-11-18, but no sanitized denied-account/sign-out evidence is recorded here. | Real approved/denied-account checks remain unverified. | **Owner + Architect:** record the explicit PR #108 proof review and authorized sign-in/denial/sign-out observations. Do not provide credentials or tokens in issue comments. |
| Installable, responsive, keyboard-accessible online shell and explicit disconnected state | PR #108 adds manifest/icons, shell and static-only service-worker caching, and disconnect guidance. | Browser proof coverage uses a test host; it is not evidence of installation on an owner-selected browser/platform. The worker does not cache journal/authenticated responses and does not queue writes. | Target-browser installation/accessibility and deployed disconnect behavior remain unverified. | **Owner + Verifier:** name the target browsers/platforms, then record sanitized installation, keyboard, cache, disconnect, and reconnect evidence. |
| Capture name, description, tags, destination and optional period; default to unplanned Future Log | Shared capture exists in PR [#44](https://github.com/cedricmendelin/bujo-ai/pull/44), with API/MCP adapters in later PRs. No browser capture workflow is delivered. | Backend and contract coverage is not a browser capture journey. | No external journal UI rollout. | **Builder:** implement capture over `JournalApplicationService`; add browser coverage for Future Log/unplanned defaults and explicit destination/period. |
| Create, view and update custom/weekly lists, goals, and open/closed state | Shared list operations and API are delivered in PR #44. The MCP list-lifecycle exception is documented and contract-tested in PR [#109](https://github.com/cedricmendelin/bujo-ai/pull/109): close/reopen stay API-only until concrete-proposal approval exists. | Domain/application and MCP parity tests cover backend rules; there is no browser list workflow. | No external list UI verification. | **Builder:** implement list screens and uniqueness/error recovery using existing services; preserve the tested API-only MCP exception unless its approval-flow dependency changes. |
| Current/history occurrences with local states and latest destination/state across moves | The shared occurrence chain, resolution query, API, and MCP tool are delivered across PRs #44 and #109. | Backend multi-hop and resolution coverage exists; no browser history/detail screen exists. | No external browser evidence. | **Builder:** render historical local states and link to the current resolved destination; verify two moves in the browser. |
| Edit, complete, abandon, eligible reopen, move, and separate planned-period changes | Shared operations, API routes, and corresponding MCP tools are delivered in PRs #44 and #109. Move requests carry an operation ID and expected occurrence ID. | Backend tests cover domain transitions, persistence, retry/concurrency behavior; no browser action/recovery flow exists. | No external browser evidence. | **Builder:** implement per-item actions, keep a retry's operation ID stable, display stale-occurrence conflicts, and prove period edits do not move an item. |
| Daily-start and upcoming, with explicit IANA zone and no automatic rescheduling | Deterministic shared queries were delivered in PR [#94](https://github.com/cedricmendelin/bujo-ai/pull/94), with API and MCP adapters in PR #109. | Query/service tests and the contract mapping exist. No PWA view currently invokes these queries. | No deployed/browser timezone verification. | **Builder:** read the browser IANA zone once per circuit, pass it explicitly, surface invalid/unknown zones without UTC fallback, and render missed intentions unchanged. |
| End-to-end journal journey, reload/restart persistence, conflict/disconnect recovery, authorization and keyboard use | PR #108 tests the rendering/authentication proof; PR #44 tests backend persistence and domain operations. Neither implements the required browser journal journey. | Existing proof and SQL persistence tests are narrower than capture → period edit → move twice → complete → inspect earlier list, followed by browser reload and host restart. | Live authentication, installation, and release readiness are not established. | **Builder + Verifier:** add and independently run the real journal E2E journey, failure/reconnect and keyboard coverage; keep production durability/recovery under #13. |

### Related dependency delivery

| Dependency | Delivered code/decision and evidence | Remaining verification or work | Owner and next action |
| --- | --- | --- | --- |
| [#2 technology baseline](https://github.com/cedricmendelin/bujo-ai/issues/2) | Accepted [ADR 0001](adr/0001-technology-baseline.md) selects .NET 11, Blazor Static SSR plus Interactive Server, one ASP.NET Core modular host, and conditional Azure SQL. | Runtime/Azure SQL operational eligibility and cost are not live-verified; the rendering decision is not reopened by the proof. | **DevOps + Verifier:** retain the accepted baseline and provide the separate #13 operational evidence. |
| [#4 authentication](https://github.com/cedricmendelin/bujo-ai/issues/4) | PR [#26](https://github.com/cedricmendelin/bujo-ai/pull/26) records the single-tenant BFF/API/MCP design; PR #108 implements the PWA proof. | Real Entra and supported-client compatibility remain unverified. A bare Object ID is not a valid allowlist entry; the current parser silently ignores malformed entries, so clear configuration diagnostics and a focused regression test remain needed. | **Owner + Security/Builder:** review authorized live checks; Builder to add configuration validation/test without changing the existing pair format. |
| [#5 shared application and persistence](https://github.com/cedricmendelin/bujo-ai/issues/5) | PR #44 delivers shared services, API, SQL persistence and backend tests. PR [#125](https://github.com/cedricmendelin/bujo-ai/pull/125) is merged and delivers local Podman/HTTPS/user-secrets and PWA-setting forwarding fixes with AppHost regression coverage. | PR #125 reports a local build and focused tests; owner-reported local sign-in is not production evidence. The #5/.NET 11 AppHost vendor compatibility caveat remains documented in [development.md](development.md). | **Builder + Verifier:** use the documented setup and retain the compatibility caveat; do not overwrite or duplicate the merged #125 fixes. |
| [#9 daily-start queries](https://github.com/cedricmendelin/bujo-ai/issues/9) | PR #94 implements deterministic daily-start/upcoming query behavior and API contracts. | PWA consumption and target-browser timezone behavior remain unbuilt/unverified. | **Builder:** integrate both views through the shared application service with explicit browser zone; test missed plans without mutation. |
| [#11 remote MCP](https://github.com/cedricmendelin/bujo-ai/issues/11) | PR #109 delivers authenticated tools, shared-service delegation, setup docs, and a contract-tested API/MCP parity exception for list close/reopen. | Copilot/Claude live client compatibility and account/tenant registration alignment are unverified. | **Owner + AI/Verifier:** authorize and record the selected client tests; do not change registrations as part of this UI milestone. |
| [#13 production durability/readiness](https://github.com/cedricmendelin/bujo-ai/issues/13) | Open tracking issue; it explicitly owns backup/restore, recovery, privacy, and Container Apps readiness. | No production journal-host rollout, restore rehearsal, cost evidence, or multi-replica recovery is claimed here. | **Owner + DevOps + Verifier:** continue #13 separately after UI evidence and approved setup; record dependencies before any rollout. |

## Integrated PWA design for the approved baseline

The PWA is an authenticated, online-first view over the existing application
kernel, not a second journal implementation. Keep static SSR for the shell and
anonymous sign-in/denied/error states; use Interactive Server for journal
interactions. Journal components call `JournalApplicationService` directly.
They do not call the app's own HTTP API. API and MCP remain separate adapters
over the same application operations and authorization/domain behavior.

After sign-in, `/` should show **Today**. The navigation is Today, Upcoming,
Capture, and Lists; selecting an item opens its detail/history. Keep
`/rendering-proof` only under an explicitly labelled **Diagnostics** link.
Today uses the deterministic daily-start query; Upcoming uses the bounded
upcoming query; both receive the browser's IANA zone from JS interop once per
circuit. Missing or invalid zone data is a visible error, never a silent UTC
fallback.

Capture accepts name, optional description, tags, destination, and optional
planned period. Unspecified destination/period means Future Log/Unplanned.
List screens create and view custom/weekly lists, edit goal text, and expose
supported open/closed state while honoring uniqueness and closure invariants.
Item detail shows canonical current text, each occurrence's list-local state,
and resolution to the latest destination/state. Actions edit an item, change
its planned period independently of moving it, transition an eligible
occurrence, or move it with the expected occurrence ID and a stable operation
ID reused for retries. Daily-start/upcoming preserve missed intentions and
show their existing reasons/conflicts/destinations; they never reschedule.

Every success message follows a successful application-service result.
Validation and stale-state errors remain visible with a refresh/retry path and
preserved form input where possible. After circuit loss, state plainly that
unsaved input may be lost; there is no offline queue or automatic replay.
The existing service worker remains static-asset-only. No journal or
authenticated response caching and no offline writes are introduced.

### API/MCP mapping and exception

| Workflow | API | MCP |
| --- | --- | --- |
| Journal/list retrieval | `GET /api/journal` | `get_journal`, `find_lists` |
| Capture/create list | `POST /api/items`, `POST /api/lists` | `capture_item`, `create_list` |
| Edit item/planned period | `PUT /api/items/{itemId}`, `PUT /api/items/{itemId}/planned-period` | `edit_item`, `set_planned_period` |
| Move | `POST /api/items/{itemId}/move` | `move_item` |
| Complete/abandon/reopen occurrence | `POST /api/occurrences/{occurrenceId}/transition` | `complete_occurrence`, `abandon_occurrence`, `reopen_occurrence` |
| Set list goal | `PUT /api/lists/{listId}/goal` | `set_list_goal` |
| Resolve occurrence history | `GET /api/occurrences/{occurrenceId}/resolution` | `resolve_occurrence` |
| Daily-start/upcoming | `GET /api/daily-start`, `GET /api/planning/upcoming` | `get_daily_start`, `get_upcoming` |
| Close/reopen list | `POST /api/lists/{listId}/close`, `POST /api/lists/{listId}/reopen` | No tools; tested exception in `JournalMcpContractTests` until a concrete-proposal approval flow exists. |

## Human gates and next milestone

The explicit owner/Architect review of PR #108's proof is still required by its
handoff and is not established by the merge, the generic reviews, or the
owner-reported local sign-in. The owner must name the target browser/platform
scope and perform any authorized live Entra/installation checks, recording
sanitized evidence. Do not request or commit credentials, tokens, tenant IDs,
or journal content. The issue #124 plan is approved for the documented
milestones, but Builder UI implementation remains dependent on the proof-gate
review and the Builder routing/continuation.

Next, Builder implements the browser workflows and tests against this design;
Verifier independently records acceptance evidence. Keep #124 open with
`Completion: partial` until every issue criterion is supported by evidence.
Production readiness remains in #13; no Azure/Entra change or rollout is
authorized by this matrix.
