# General chat and speech

## Routes

- `/` is the authenticated Chat start page.
- `/today` is the dedicated Today view.
- Capture, Lists, Upcoming, item detail and list review remain available.
- Anonymous users keep the sign-in landing.
- When `Chat:Enabled=false`, `/` shows a clear fallback with links to Today,
  Capture, Lists and Upcoming.

## Session and privacy

Chat history lives only in the Blazor Interactive Server circuit. New chat and
reload clear it. The application does not add a chat repository, browser
transcript storage or audio storage. Provider-side processing and retention
follow the configured provider terms and are not promised to be zero-retention.

Prompts, transcripts, tool arguments and tool results are not logged or stored
in application telemetry. Monitor only run counts, latency, status categories
and redacted error categories.

## Text runtime

The CI-safe default provider is `Mock`. It recognizes common journal reads,
captures, list commands and explicit item operations while still routing every
supported operation through the shared typed registry. Model and mock calls
cannot execute approval-gated tools; use journal views for moves and abandonment.
Foundry model calls cannot execute other writes either, pending a server-side
mechanism to bind an explicit user request to each direct edit.

Next-week capture is resolved by the shared runtime using the journal clock,
the caller's validated IANA time zone, and the ISO week model. One matching
open weekly list is used directly. Multiple matches require a numbered choice;
no match requires confirmation before creating a weekly list. If the time zone
is missing or invalid, chat asks for a valid IANA zone before proceeding.
Pending choices are bounded, expire, and are scoped to the authenticated user
and chat conversation. The destination is revalidated before capture, and chat
confirms the task and destination only after the save succeeds.

`POST /api/chat/agui` is the app-owned HTTP/SSE run adapter. It requires the
approved-principal policy and `Chat:Enabled=true`. Thread IDs are run labels,
never identity. It currently emits tool and response events after the run
completes, rather than streaming them during execution.

An optional Foundry provider is disabled until endpoint, deployment and managed
identity configuration are verified. It is server-side only; no provider token
or journal credential is sent to the browser. The `get_journal` tool can return
the whole journal to Foundry when the model calls it, subject to the tool-result
size limit; narrower tools should be preferred where possible.

## Tool parity

The registry exposes exactly these 22 current MCP tools:

- Reads: `get_journal`, `get_daily_start`, `get_upcoming`, `find_lists`,
  `resolve_occurrence`, `get_list_review`, `get_review_proposal`
- Writes: `capture_item`, `create_list`, `edit_item`, `set_planned_period`,
  `set_list_goal`, `close_list`, `reopen_list`, `move_item`,
  `complete_occurrence`, `abandon_occurrence`, `reopen_occurrence`
- Review: `create_review_proposal`, `cancel_review_proposal`,
  `reject_review_proposal`, `apply_review_proposal`

Automated parity tests fail if MCP tool names and the chat registry drift.
The journal API and MCP remain the source surfaces for all journal capabilities.

## Approvals

The mock provider can execute its supported direct captures and individual
edits after domain checks. Foundry writes fail closed except for pending review
proposal creation until direct intent can be verified server-side.
Review proposals render an approval card showing their exact changes and
expiration. Approving applies only that digest and exact version set. Reject,
cancel and stale or conflicted proposals perform no journal write. Direct
model and mock calls to gated actions are refused; arbitrary bulk moves and
destructive actions do not yet have a general chat approval flow.

No uncertain mutation is automatically retried. If a result is interrupted,
the UI reconciles against current journal state or reports unknown rather than
claiming success.

## Speech

The mic button is explicit and single-shot. The recognition language is `en-US`.
Partial hypotheses are ignored, duplicate final events submit once, and empty
results submit nothing. Stop, cancel, navigation, error and dispose release the
recognizer and never resume automatically.

This implementation uses the browser-supported recognition API. The live G2
microphone matrix is deferred: no live-microphone result is claimed until the
owner runs it on a supported browser.

`POST /api/chat/speech-token` is authenticated and feature-gated. It is not
cached and is not available until server-side Speech configuration is present.
It fails closed without returning credentials in logs or UI state.
