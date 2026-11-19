# ADR 0003: General chat and speech

**Status:** Accepted for the implementation slice. Live Foundry inference,
speech-token minting and real-microphone recognition remain gated.

## Decision

Bujo-AI makes an authenticated, session-only general chat the `/` start page.
`/today` remains a dedicated Today view. The existing ASP.NET Core host owns
conversation state and a typed tool registry shared with the MCP implementation.

The chat runtime uses an app-owned SSE/AG-UI-style HTTP adapter:

- `POST /api/chat/agui` is authenticated and feature-gated.
- The default `Mock` provider is CI-safe and deterministic.
- An optional Foundry provider uses the managed application identity and an
  explicitly configured deployment. It performs no journal writes until a typed
  tool call is returned and validated by the server.
- Conversation history exists only in the Interactive Server circuit and is
  cleared by New chat or reload.

The browser speech control uses the browser-supported recognition API for the
implementation slice. Recognition is configured as `en-US`. It starts only from
an explicit button click, ignores partial hypotheses, submits one final
nonempty result, and releases the recognizer on end, cancel, error, dispose or
navigation. It does not auto-resume. G2 live-microphone verification is
explicitly deferred; no claim of a successful live microphone test is made.

`POST /api/chat/speech-token` is authenticated and feature-gated. It is
short-lived and fails closed when Speech configuration is missing. The UI does
not cache, log or persist the token.

## Tool and approval boundary

The chat registry contains the 22 current MCP tools. It maps to existing
`JournalApplicationService` and `ReviewApplicationService` operations; it has no
SQL, unrestricted network or database access.

Explicit individual reads and edits may execute directly after server-side
authorization and domain validation. Ceremony-generated review proposals,
bulk moves and destructive actions create an immutable, version-checked
proposal and render an approval card. Reject and cancel are terminal no-write
actions. Approval and apply remain PWA-only and reuse the existing digest,
expiry and concurrency checks.

## Privacy and resilience

No chat transcript, audio or review payload is persisted. The provider and
speech services receive only the context and text needed for the active run.
Prompts, transcripts, tool arguments and results are excluded from telemetry.
Interrupted runs show cancellation or interruption and are never replayed.
Mutations are not retried automatically after reconnection.

## Deferred gates

- G1: owner/maintainer deployment, runtime role and `Chat:Enabled` verification.
- G2: supported recognition-language confirmation and real-microphone matrix.

No live role grant or live Foundry inference is performed by this change.
