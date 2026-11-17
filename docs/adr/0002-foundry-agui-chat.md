# ADR 0002: Optional Foundry-backed chat with AG-UI

**Status:** Proposed — pending owner approval. This ADR records a candidate
architecture for issue #6; it is not an accepted decision and authorizes no
packages, inference, Foundry resources, deployment or journal access.
**Date:** 2026-11-17

## Context

Bujo-AI is a modular ASP.NET Core application with a Blazor PWA, API, MCP and
shared application services. Foundry is optional and is not the source of truth
for journal data. ADR 0001 leaves Blazor render mode proposed; it currently
proposes Interactive Server. AI-drafted weekly planning is a later milestone,
and direct explicit edits must remain distinct from ceremony/bulk/destructive
proposals.

The discovery found a .NET AG-UI client and a Microsoft Agent Framework
Interactive Server sample using it without JavaScript interop. The sample is
not production-authenticated and does not establish WebAssembly support,
durable replay, .NET 11 compatibility, or current Foundry regional pricing.
See the [research report and compatibility matrix](../ai-chat.md).

## Decision proposed

Use an **application-owned agent/chat adapter in the existing ASP.NET Core
host**, with:

1. An optional model-client adapter for an approved model endpoint; a Foundry
   model deployment is the first candidate, subject to the verification below.
2. A same-host AG-UI endpoint for the proposed Interactive Server UI, using
   the Microsoft Agent Framework AG-UI ASP.NET Core integration and its
   upstream .NET `AGUI.Client`.
3. Narrow, typed tools that call the existing authorization-aware application
   services. No database access, free-form SQL, unrestricted network tools, or
   model-owned writes.
4. A server-side proposal/approval API for ceremony, bulk and destructive
   changes. Explicit edits execute only when unambiguous and authorized; all
   other changes require approval of the immutable, version-checked change set.
5. A feature switch defaulting off. The host and all journal/API/MCP features
   work without Foundry configuration.

Do **not** require a Foundry-hosted/persistent agent or a JavaScript bridge.
The AG-UI host is a protocol adapter within the existing application, not a
new service boundary. If the preview host package or .NET 11 compatibility
fails a focused spike, fall back to option A (the application-owned chat
endpoint without AG-UI) and return for approval before widening scope.

## Alternatives considered

| Option | Assessment |
|---|---|
| App-owned endpoint + `IChatClient` only | Smallest overall and the fallback. It avoids AG-UI package lifecycle risk but Bujo-AI must define and maintain its own event, tool-status and approval UI contract. |
| App-owned endpoint + AG-UI (proposed) | Best fit if AG-UI remains the product direction: standardized event vocabulary and a native .NET Interactive Server sample; remains one host and keeps policy/tools in application code. Preview hosting package and render/runtime compatibility must be proven. |
| Foundry-hosted agent service | Not required by the journal architecture or current evidence. It adds a remote runtime and identity/session boundary. Reconsider only if a verified requirement needs managed lifecycle/state not supplied by the app-owned host. |
| JavaScript AG-UI bridge | Not required for the proposed Interactive Server render mode. It adds JS interop and browser-specific state/stream behavior; reconsider only if a validated WASM design needs it. |

## Consequences and risks

- The Foundry/model adapter can be disabled or replaced without changing
  journal persistence or application use cases. This proposed boundary does
  not depend on verified Foundry model, region or pricing claims.
- AG-UI's HTTP POST/SSE flow provides streamed server events, but not automatic
  circuit recovery or event replay. An interrupted run is shown as interrupted;
  retry is a new run, never an automatic replay of a mutation.
- The cited Microsoft Agent Framework host package has a preview marker. The
  sample targets .NET 10 while Bujo-AI's approved baseline is .NET 11. Verify
  exact package targets, runtime compatibility, authorization, caller isolation,
  reverse-proxy streaming, cancellation and browser behavior before adoption.
- The sample's missing authentication is a security warning, not a pattern to
  copy. Authenticate/authorize every endpoint and isolate any persisted agent
  state by trusted user identity. Do not rely on a thread ID as identity.
- Journal text sent to a model is an external data-processing decision.
  Minimize context, redact telemetry and verify current provider/model-specific
  retention, abuse-monitoring and regional terms before enabling it.
- Exact model availability and price in Switzerland North are unverified.
  Therefore this ADR does not assert compliance with the USD 50/month target.

## Verification still required

Before implementation uses a Foundry deployment, recheck current official
Foundry model/agent offerings, model and deployment availability for the
selected region, privacy/retention terms, and price. `AGUI.Client` is pinned to
1.0.0 in the cited MAF repo while its upstream source version is 1.0.1; confirm
the intended NuGet package/version and hosting-package lifecycle. Run a focused
integration against Bujo-AI's .NET 11/selected render mode, including auth,
tool isolation, streaming, cancellation, concurrent proposal approval,
interrupted runs and redacted telemetry. No result from the source-code sample
alone substitutes for these checks.

## References

- [Discovery report and compatibility matrix](../ai-chat.md)
- [ADR 0001: Technology baseline](0001-technology-baseline.md)
- [Microsoft Agent Framework AG-UI Blazor sample](https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/05-end-to-end/AGUIWebChat)
- [AG-UI protocol and SDK repository](https://github.com/ag-ui-protocol/ag-ui)
- [Foundry overview](https://learn.microsoft.com/en-us/azure/ai-foundry/what-is-foundry)
