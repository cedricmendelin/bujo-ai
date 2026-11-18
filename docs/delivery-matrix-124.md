# Issue #124 delivery and acceptance matrix

**Status:** The local PWA/UI implementation is in open partial PR #133, based on
approved plan v3. The owner has decided that list names must be unique and that
only one keyed weekly list may exist per ISO week. Owner confirmation remains
pending for the allowlist contract, list close/reopen API-MCP parity, target
browsers, and final rendering-proof review. SQL-backed host-restart evidence
remains outstanding. Issue #124 remains open. This matrix is not evidence of
live sign-in, target-device installation, or production rollout.
Reconciled on 2026-11-18 against merged PRs, the current source, and linked
issue handoffs.
**Approved scope:** [Issue #124 implementation plan v3](https://github.com/cedricmendelin/bujo-ai/issues/124#issuecomment-6978079160).

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

Post-deployment manual verification is tracked separately in #132; no such
evidence is claimed here.

**Current local checks:** 60 integration tests and 24 headless Chrome E2E tests
pass. The SQL-persistence integration suite is not included in this change, so
SQL-backed host-restart evidence remains unverified. The E2E host uses a
synthetic identity and in-memory repository, so reload coverage is not SQL
persistence across a host restart. Terraform plan tests (29), formatting, and
validation pass.

## Issue #10 acceptance criteria

| Criterion | Delivered implementation | Local verification | External rollout | Remaining owner, dependency, and next action |
| --- | --- | --- | --- | --- |
| Approved-account sign-in and safe signed-out/denied/error states | PR [#108](https://github.com/cedricmendelin/bujo-ai/pull/108) implements the BFF, cookie/UI boundary, account pages, and server-side token handling. The API remains bearer-only. The current #124 implementation uses bare object IDs with the configured tenant check as a proposed allowlist contract; owner confirmation is pending. | 60 non-SQL integration tests pass, including allowlist, BFF, bearer-boundary, and MCP cases using synthetic principals. A prior owner comment allowed the limited rendering proof to unblock UI work; final owner/Architect acceptance review remains pending and no live sign-in is established. | Real approved/denied-account checks remain unverified. | **Owner:** confirm the allowlist format before authorizing a build against owner-managed configuration; then perform authorized live sign-in/denial/sign-out checks and record sanitized results. Do not provide credentials or tokens in issue comments. |
| Installable, responsive, keyboard-accessible online shell and explicit disconnected state | PR #108 adds manifest/icons, shell and static-only service-worker caching, and disconnect guidance. | Synthetic browser tests cover responsiveness, keyboard use, disconnected state and static-only caching; they do not prove installation on target devices. The worker does not cache journal/authenticated responses and does not queue writes. Chrome on Windows desktop and Android are proposed targets, not owner-approved selections. | Installation/accessibility and deployed disconnect behavior remain unverified. | **Owner + Verifier:** confirm target browsers, then record sanitized installation, keyboard, cache, disconnect, and reconnect evidence. |
| Capture name, description, tags, destination and optional period; default to unplanned Future Log | Shared capture exists in PR [#44](https://github.com/cedricmendelin/bujo-ai/pull/44), with API/MCP adapters in later PRs. The current #124 change adds the browser capture workflow over the shared application service. | E2E coverage verifies keyboard capture, Future Log/unplanned defaults, tags, period editing, and successful-save feedback. | No external journal UI rollout. | **Owner + Verifier:** verify the workflow after the change is reviewed and available on the target platforms. |
| Create, view and update custom/weekly lists, goals, and open/closed state | Shared list operations and API are delivered in PR #44. PR [#109](https://github.com/cedricmendelin/bujo-ai/pull/109) documented an API-only MCP exception; PR #133 implements `close_list` and `reopen_list` with shared-service delegation and browser list workflows as a parity proposal. Owner confirmation of the API-MCP parity approach remains pending. The shared domain rejects duplicate names (case-insensitively) and duplicate keyed weekly lists; a migration adds matching database constraints and stops with an explicit error if existing data violates them. | Application, API, MCP, and browser tests cover duplicate-name and duplicate-week errors alongside lifecycle invariants, goals, and draft retention on errors. | No external list UI verification. | **Owner + Verifier:** confirm the parity approach; resolve any pre-existing duplicate names/week keys before deploying the migration, then verify the errors and list lifecycle on owner-approved target platforms. |
| Current/history occurrences with local states and latest destination/state across moves | The shared occurrence chain, resolution query, API, and MCP tool are delivered across PRs #44 and #109. The current #124 change adds item detail/history views. | E2E coverage verifies list-local state and latest-destination resolution after two moves and reload. | No external browser evidence. | **Owner + Verifier:** verify history/detail behavior on the target platforms after review. |
| Edit, complete, abandon, eligible reopen, move, and separate planned-period changes | Shared operations, API routes, and corresponding MCP tools are delivered in PRs #44 and #109. The current #124 UI carries stable move operation IDs and expected occurrence IDs. | E2E coverage verifies actions, stale-move draft recovery, period edits separate from moves, and retry after a lost move acknowledgement. | No external browser evidence. | **Owner + Verifier:** verify item actions on the target platforms after review. |
| Daily-start and upcoming, with explicit IANA zone and no automatic rescheduling | Deterministic shared queries were delivered in PR [#94](https://github.com/cedricmendelin/bujo-ai/pull/94), with API and MCP adapters in PR #109. The current #124 UI consumes them with the browser's IANA zone. | E2E coverage verifies browser-zone display, missed intentions unchanged, upcoming results, and visible invalid-zone errors without UTC fallback. | No deployed/browser timezone verification on Android or actual installation. | **Owner + Verifier:** verify on Chrome for Windows desktop and Android after review. |
| End-to-end journal journey, reload/restart persistence, conflict/disconnect recovery, authorization and keyboard use | PR #108 tests the original rendering/authentication proof; PR #44 tests backend persistence and domain operations. The current #124 change adds the browser journey and recovery flows. | 24 E2E tests pass, including capture → planned period → move twice → complete → inspect earlier list, reload, keyboard, conflict, disconnect, synthetic authorization states, and static-only cache. Reload uses an in-memory test repository; SQL persistence across host restart is not established by this suite. | Live authentication, installation, and release readiness are not established. | **Owner + Verifier:** perform authorized live sign-in and target-platform installation checks. Host-restart persistence remains unverified; do not claim SQL durability from the browser suite. Keep production durability/recovery under #13. |

### Related dependency delivery

| Dependency | Delivered code/decision and evidence | Remaining verification or work | Owner and next action |
| --- | --- | --- | --- |
| [#2 technology baseline](https://github.com/cedricmendelin/bujo-ai/issues/2) | Accepted [ADR 0001](adr/0001-technology-baseline.md) selects .NET 11, Blazor Static SSR plus Interactive Server, one ASP.NET Core modular host, and conditional Azure SQL. | Runtime/Azure SQL operational eligibility and cost are not live-verified; the rendering decision is not reopened by the proof. | **DevOps + Verifier:** retain the accepted baseline and provide the separate #13 operational evidence. |
| [#4 authentication](https://github.com/cedricmendelin/bujo-ai/issues/4) | PR [#26](https://github.com/cedricmendelin/bujo-ai/pull/26) records the single-tenant BFF/API/MCP design; PR #108 implements the PWA proof. The current #124 change uses a bare-object-ID allowlist constrained by `Auth:TenantId` as a proposal pending owner confirmation. | 60 non-SQL integration tests pass, including authorization and OIDC coverage. Real Entra and supported-client compatibility remain unverified. Existing pair-format local/deployment values are owner-managed and are not changed here. | **Owner:** confirm the allowlist contract before any owner-managed configuration change; perform authorized live checks separately. |
| [#5 shared application and persistence](https://github.com/cedricmendelin/bujo-ai/issues/5) | PR #44 delivers shared services, API, SQL persistence and backend tests. PR [#125](https://github.com/cedricmendelin/bujo-ai/pull/125) is merged and delivers local Podman/HTTPS/user-secrets and PWA-setting forwarding fixes with AppHost regression coverage. | PR #125 reports a local build and focused tests; owner-reported local sign-in is not production evidence. The #5/.NET 11 AppHost vendor compatibility caveat remains documented in [development.md](development.md). | **Builder + Verifier:** use the documented setup and retain the compatibility caveat; do not overwrite or duplicate the merged #125 fixes. |
| [#9 daily-start queries](https://github.com/cedricmendelin/bujo-ai/issues/9) | PR #94 implements deterministic daily-start/upcoming query behavior and API contracts. The current #124 change integrates both views through the shared application service. | Synthetic browser-zone behavior and missed-plan invariants are covered by E2E tests; target-device verification remains pending. | **Owner + Verifier:** approve target browsers, then verify both views on those platforms. |
| [#11 remote MCP](https://github.com/cedricmendelin/bujo-ai/issues/11) | PR #109 delivers authenticated tools, shared-service delegation, and setup docs. PR #133 adds authenticated `close_list` and `reopen_list` tools with shared-service delegation and contract coverage as a parity proposal; owner confirmation remains pending. | Copilot/Claude live client compatibility and account/tenant registration alignment are unverified. | **Owner + Verifier:** confirm the API-MCP parity approach, then authorize and record selected client tests; do not change registrations as part of this UI milestone. |
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
supported open/closed state while honoring unique list names, one keyed weekly
list per ISO week, Future Log uniqueness, and closure invariants.
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
| Close/reopen list | `POST /api/lists/{listId}/close`, `POST /api/lists/{listId}/reopen` | `close_list`, `reopen_list` (implemented with parity/contract coverage in PR #133; owner confirmation of this parity approach remains pending). |

## Human gates and next milestone

The owner previously allowed PR #108's limited rendering proof to unblock UI
implementation. The final owner/Architect acceptance review remains pending;
the earlier permission to start UI work is not final proof-gate evidence. Chrome
on Windows desktop and Android are proposed browser targets, not owner-approved
selections. PR #133 contains the local UI implementation, but authorized
sign-in and installation checks remain pending.
Do not request or commit credentials, tokens, tenant IDs, or journal content.
Builder plan v3 is approved. Production readiness remains in #13; no Azure/Entra
change or rollout is authorized by this matrix.

Next, review the uniqueness implementation and preflight existing data before
any authorized migration; resolve the remaining owner decisions; then have the
Verifier independently record acceptance evidence and the Owner perform live
checks. Keep #124 open with
`Completion: partial` until every issue criterion is supported by evidence.
Production readiness remains in #13; no Azure/Entra change or rollout is
authorized by this matrix.
