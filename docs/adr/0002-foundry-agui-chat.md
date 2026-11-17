# ADR 0002: Optional Foundry-backed chat with AG-UI

**Status:** Accepted on 2026-11-17 for the architecture direction. The owner
selected the app-owned runtime and optional Foundry model provider, and accepted
Preview status for a local AG-UI/.NET 11 experiment. Its mock Interactive
Server path passed the local compatibility check. This acceptance does not
authorize production packages, inference, Foundry resources, deployment or
journal access.
**Date:** 2026-11-17

## Context

Bujo-AI is a modular ASP.NET Core application with a Blazor PWA, API, MCP and
shared application services. Foundry is optional and is not the source of truth
for journal data. The owner selected Blazor Interactive Server in ADR 0001.
The [product concept](../concept.md) defines AI-assisted weekly planning as the
first in-app AI experience; this ADR decides the technical boundary, not the
feature's full user journey.

The discovery found a .NET AG-UI client and a Microsoft Agent Framework
Interactive Server sample using it without JavaScript interop. A separate local
spike targeting `net11.0` built and ran with .NET SDK 11.0.100/runtime 11.0.0,
using `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore` 1.28.0-preview.261106.1 and
`AGUI.Client` 1.0.0 with `net10.0` package assets. It verified a mock SSE
round-trip with lifecycle events, a scripted tool call/result and streamed text,
plus fresh-approval and stale-version UI cases. This demonstrates only the
tested local path; it does not establish GA status, production authentication,
durable replay, WebAssembly support, or the regional pricing of a selected
Foundry model. See the [research report and compatibility matrix](../ai-chat.md).

## Decision

Use an **application-owned orchestration and chat adapter in the existing
ASP.NET Core host**, with:

1. An optional server-side model-client adapter for a Foundry model deployment.
   The exact model, deployment type and region are not selected here. The app
   owns orchestration and conversation/session state; Foundry Agent Service is
   not a required runtime.
2. A same-host AG-UI endpoint for the owner-selected Interactive Server UI,
   using the Microsoft Agent Framework AG-UI ASP.NET Core integration and its
   .NET `AGUI.Client`. The local spike validated a mock path on .NET 11. The
   plain app-owned endpoint remains the fallback if production validation fails.
3. Narrow, typed tools that call existing authorization-aware application
   services. No database access, free-form SQL, unrestricted network tools, or
   model-owned writes.
4. For the initial weekly-planning use case, tools return a structured proposal;
   no journal changes occur before the user approves that exact, version-checked
   proposal. The server applies approved changes atomically through shared
   application services.
5. A feature switch defaulting off. The host and all journal/API/MCP features
   work without Foundry configuration. Chat transcript persistence and
   retention are not decided by this ADR and must not be added implicitly.

Do **not** require a Foundry-hosted/persistent agent or a JavaScript bridge.
AG-UI is the app-to-agent protocol, not the model, agent runtime, approval
policy, or generated UI. Its endpoint remains a protocol adapter in the
existing application, not a new service boundary. The owner approved this
architecture after reviewing the local spike. Production integration remains
subject to the security, privacy, operational and cost verification below.

## Alternatives considered

| Option | Assessment |
|---|---|
| App-owned endpoint + `IChatClient` only | Smallest overall and the fallback. It avoids AG-UI package lifecycle risk but Bujo-AI must define and maintain its own event, tool-status and approval UI contract. |
| App-owned endpoint + AG-UI (accepted) | Standard event vocabulary for streamed text, tool events, results and human-interaction UX; official .NET Interactive Server sample; remains one host and keeps policy/tools in application code. The local spike verified a .NET 11 mock path, but the hosting package remains Preview and production readiness is unproven. |
| Foundry Agent Service runtime | Not selected. Agent Service offers managed prompt and hosted agents, but adds a remote runtime, identity/session boundary, and potential persisted agent state. Reconsider only if a verified requirement needs managed lifecycle/state not supplied by the app-owned host. |
| JavaScript AG-UI bridge | Not required for the selected Interactive Server render mode. It adds JS interop and browser-specific state/stream behavior; reconsider only if a validated WASM design needs it. |

## Consequences and risks

- The Foundry/model adapter can be disabled or replaced without changing
  journal persistence or application use cases. This accepted boundary does
  not depend on verified Foundry model, region or pricing claims.
- AG-UI's HTTP POST/SSE flow provides streamed server events, but not automatic
  circuit recovery or event replay. An interrupted run is shown as interrupted;
  retry is a new run, never an automatic replay of a mutation.
- The cited Microsoft Agent Framework host package has a preview marker. The
  sample targets .NET 10 while Bujo-AI's approved baseline is .NET 11. The local
  spike verified the tested package versions on a `net11.0` mock path, with
  package assets resolving from `net10.0`; this is not a vendor support or
  production-readiness guarantee. Verify authorization, caller isolation,
  reverse-proxy streaming, cancellation and browser behavior before adoption.
- The sample's missing authentication is a security warning, not a pattern to
  copy. Authenticate/authorize every endpoint and isolate any persisted agent
  state by trusted user identity. Do not rely on a thread ID as identity.
- Journal text sent to a model is an external data-processing decision.
  Minimize context, redact telemetry and verify current provider/model-specific
  retention, abuse-monitoring and regional terms before enabling it.
- Microsoft's current guidance says Models sold by Azure do not use prompts
  and completions to train base models without permission, while abuse
  monitoring can review selected prompts and completions. Optional stateful
  features can store content. This design does not select a model or deployment
  type, so it makes no Swiss processing, data-residency or retention promise.
- Exact model availability and price in Switzerland North are unverified.
  Therefore this ADR does not assert compliance with the USD 50/month target.

## Verification still required

The local spike confirmed the NuGet versions and a functional deterministic-mock
AG-UI round trip on the .NET 11/Interactive Server baseline. It used
`AGUI.Client` 1.0.0, matching the MAF repository pin; upstream source defaults
to 1.0.1, so 1.0.0 is a tested version, not a production version selection.
Before production uses a Foundry deployment, select a model and deployment
type, then recheck its availability in the selected region, processing scope,
privacy/retention terms and official price. A production integration must also
validate endpoint authorization, per-user state isolation, tool restrictions,
cancellation, interrupted runs and redacted telemetry. No source sample or
local mock substitutes for these checks.

## References

- [Discovery report and compatibility matrix](../ai-chat.md)
- [ADR 0001: Technology baseline](0001-technology-baseline.md)
- [Microsoft Agent Framework AG-UI Blazor sample](https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/05-end-to-end/AGUIWebChat)
- [AG-UI protocol and SDK repository](https://github.com/ag-ui-protocol/ag-ui)
- [Foundry overview](https://learn.microsoft.com/en-us/azure/ai-foundry/what-is-foundry)
