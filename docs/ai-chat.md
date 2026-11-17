# Foundry, AG-UI and Blazor chat discovery

**Status:** Discovery for issue [#6](https://github.com/cedricmendelin/bujo-ai/issues/6).
This document records evidence and a proposed direction, not an implementation
decision. The ADR is
[proposed](adr/0002-foundry-agui-chat.md); owner approval is still required.

Research was checked against primary sources on **2026-11-17 UTC**. A row is
**Verified** only for the specific claim and version stated; a sample does not
prove production readiness or compatibility with every hosting/render mode.
Microsoft Learn, NuGet.org and the AG-UI documentation site could not be
retrieved from this environment. Claims requiring those pages are marked
**Unverified**, with a reproducible check below. No resources, live models,
journal data, or paid inference were used.

## Compatibility matrix

| Component / option | Evidence and version | Status and Blazor relationship |
|---|---|---|
| AG-UI protocol and event model | [Upstream AG-UI README](https://github.com/ag-ui-protocol/ag-ui/blob/main/README.md) identifies 1.0 as a stable, frozen spec with JSON Schema and lists lifecycle, text, tool, state, reasoning, activity, subagent, and passthrough event families. | **Verified** from the upstream project README. This verifies the protocol description, not every framework implementation. |
| AG-UI transports | The same [upstream README](https://github.com/ag-ui-protocol/ag-ui/blob/main/README.md) links HTTP + SSE and HTTP + Protobuf bindings and describes an event stream. The Microsoft [AG-UI samples README](https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/02-agents/AGUI/README.md) describes HTTP POST requests with SSE responses. | **Verified** for those documented bindings. SSE is server-to-client for a run; it is not a bidirectional connection. Continuations are separate requests. |
| AG-UI .NET client | Upstream [AG-UI .NET SDK package metadata](https://github.com/ag-ui-protocol/ag-ui/blob/main/sdks/dotnet/Directory.Build.props) and [client project](https://github.com/ag-ui-protocol/ag-ui/blob/main/sdks/dotnet/src/AGUI.Client/AGUI.Client.csproj) describe `AGUI.Client` as an HTTP/SSE client implementing `IChatClient`; its default source version is 1.0.1. The Microsoft Agent Framework [central package pins](https://github.com/microsoft/agent-framework/blob/main/dotnet/Directory.Packages.props) and sample project use `AGUI.Client` 1.0.0. | **Verified** as a .NET client and MAF sample dependency. NuGet publication/latest-version metadata could not be retrieved; do not infer registry availability from project files. It is not a Blazor component library. |
| Microsoft Agent Framework AG-UI host | [ASP.NET Core host project](https://github.com/microsoft/agent-framework/blob/main/dotnet/src/Microsoft.Agents.AI.Hosting.AGUI.AspNetCore/Microsoft.Agents.AI.Hosting.AGUI.AspNetCore.csproj) has `VersionSuffix` `preview`; the MAF [1.28.0 release](https://github.com/microsoft/agent-framework/releases/tag/dotnet-1.28.0) is dated 2026-11-06. The [server sample](https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/05-end-to-end/AGUIWebChat/Server/Program.cs) uses `AddAGUIServer()` and `MapAGUIServer()`. | **Verified** for the source and preview marker. This is .NET hosting support, not proof that the host package is GA or that an entire production configuration is ready. |
| Blazor Interactive Server client | The MAF [AGUIWebChat sample](https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/05-end-to-end/AGUIWebChat) has a Blazor Server client; its [client startup](https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/05-end-to-end/AGUIWebChat/Client/Program.cs) registers `AGUIChatClient` as an `IChatClient` and enables Interactive Server components. The project targets `net10.0`. | **Verified** as an official .NET/Blazor Server sample without a JavaScript bridge. It is a demonstration, not production authentication, durable replay, or proof against this repository's proposed .NET 11 baseline. |
| Blazor WebAssembly client | No official WASM-specific sample or browser validation was found in the cited MAF sources. | **Unverified** — do not call it natively supported or unsupported. Test package target compatibility, browser streaming, CORS, auth propagation, cancellation and reconnection on the chosen WASM hosting mode. |
| JavaScript AG-UI client / bridge | The [upstream README](https://github.com/ag-ui-protocol/ag-ui/blob/main/README.md) lists the TypeScript `@ag-ui/client` SDK. | **Verified** that a JavaScript client exists. Using it from Blazor requires an application-owned JavaScript interop bridge; no evidence here establishes a first-party Blazor component binding. |
| Foundry model deployment vs. hosted agent | The MAF [AGUIWebChat server sample](https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/05-end-to-end/AGUIWebChat/Server/Program.cs) constructs a chat client for a configured Azure OpenAI endpoint and deployment and hosts the agent in ASP.NET Core; it does not use a Foundry-hosted agent. Foundry [overview](https://learn.microsoft.com/en-us/azure/ai-foundry/what-is-foundry) and [Agent Service overview](https://learn.microsoft.com/en-us/azure/ai-foundry/agents/overview) were not retrievable here. | **Verified** that an app-owned host can be demonstrated without a hosted agent. **Unverified** here: current Foundry product boundaries, exact model/agent offerings, regional availability and their GA status. Do not assume a hosted agent is required. |
| `IChatClient` / Microsoft.Extensions.AI | MAF's [AG-UI client sample](https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/05-end-to-end/AGUIWebChat/Client/Program.cs) consumes `AGUIChatClient` through `IChatClient`; the MAF repo pins `Microsoft.Extensions.AI` 11.0.0. | **Verified** as the sample's abstraction and repo pin. Exact package availability and current provider-specific Foundry adapters should be checked against NuGet and Microsoft Learn before implementation. |

### Compatibility check before implementation

1. Recheck the Microsoft Learn [Foundry overview](https://learn.microsoft.com/en-us/azure/ai-foundry/what-is-foundry), [Agent Service overview](https://learn.microsoft.com/en-us/azure/ai-foundry/agents/overview), [model region availability](https://learn.microsoft.com/en-us/azure/ai-foundry/region-availability-models), and official [Foundry model pricing](https://azure.microsoft.com/en-us/pricing/details/ai-foundry-models/) from a network that can retrieve them. Record the exact model, deployment type, region and date; do not infer Switzerland North availability from a global model catalog entry.
2. Confirm current NuGet versions, target frameworks and package lifecycle for `AGUI.Client`, `AGUI.Server`, `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore` and the selected `Microsoft.Extensions.AI` provider. Build a minimal integration against this repository's .NET 11 baseline.
3. If evaluating WASM, run a browser test against the actual deployment topology: authenticated POST, SSE response, tool continuation, cancellation, proxy buffering, CORS where cross-origin, interrupted stream and reconnect. A passing Server sample is not that proof.

## Integration options

| Option | What it is | Benefits | Costs / limitations |
|---|---|---|---|
| **A. App-owned chat endpoint and model client** | Add an optional endpoint to the existing ASP.NET Core host. Call a Foundry model deployment through an `IChatClient` provider; map narrowly scoped tools to existing application services. | Smallest deployment and trust boundary; Foundry can be absent; no hosted agent or additional process; UI can use the existing server circuit. | Application owns conversation, streaming contract, tool UX, cancellation, and recovery. No AG-UI event compatibility unless added later. |
| **B. App-owned agent plus AG-UI endpoint in the existing host** | Same model and service boundary as A, plus Microsoft Agent Framework's AG-UI ASP.NET Core host and `AGUI.Client` for the Blazor Server UI. | Standard event model for streamed text, tool events, results and human-interaction UX; official Blazor Server sample; no JavaScript bridge or separate deployment is required. | Host package is marked preview in the cited source. The sample targets .NET 10, omits auth, and has no production replay guarantee. Adds protocol/session compatibility work and does not prove WASM support. |
| **C. Foundry-hosted/persistent agent** | Run agent orchestration in a Foundry-managed agent runtime and call it from the Bujo-AI host/client. | Could be useful if managed agent lifecycle, durable agent sessions or platform tools are needed. | Adds a remote runtime, identity/session boundary, service configuration, cost and operational dependency. The discovery found no requirement that needs it; Foundry feature and region claims remain to be verified before adoption. |
| **D. JavaScript AG-UI client in Blazor** | Embed a TypeScript AG-UI client (for example `@ag-ui/client`) with JS interop. | Uses the JS ecosystem's client directly; may suit a future WASM-first UI. | Adds JS interop, duplicated client/UI state and another boundary to test. No evidence that it is needed for Interactive Server, for which a .NET sample exists. |

**Proposed selection:** B, limited to the existing ASP.NET Core host and the
Blazor Interactive Server path already proposed in ADR 0001. It supplies AG-UI
events without requiring a hosted agent, extra service, or JS bridge. Keep A as
the fallback if the preview host package or its .NET 11 compatibility fails the
spike. This is a proposed design, not approval to add packages or start
inference.

## Rendering, streaming and error UX

AG-UI HTTP/SSE carries one run's server-emitted events after an HTTP request.
The framework's current server sample shows text streaming and tool/HIL samples;
it does not establish that Blazor reconnect replays lost events or that every
AG-UI event is supported by every client version.

| Render mode | Streaming and cancellation proposal | Reconnection and recovery |
|---|---|---|
| **Interactive Server (ADR 0001 proposal)** | The Blazor server-side .NET client consumes the AG-UI response and updates the UI through its SignalR circuit. A Stop action cancels a per-run `CancellationToken`; propagate it to the HTTP request and the model call. On component disposal, cancel the run. Do not claim that a dropped circuit always cancels server work; verify propagation in the integration test. | A SignalR circuit reconnect only reconnects the UI circuit; it does not resume/replay an AG-UI event stream. Display “response interrupted” with the last completed message and Retry. Do not replay tool calls automatically. |
| **Interactive/standalone WebAssembly (not selected)** | Browser-side client would own the HTTP request and abort it on Stop/disposal. Verify the AG-UI .NET client's browser compatibility; otherwise use a JavaScript fetch/SSE adapter only as a separately reviewed option. A server proxy remains responsible for model-call cancellation. | No Blazor Server circuit for the client component. Resume/retry is application logic. On interruption, show the same explicit retry state; no implicit replay. |

For either mode, show a pending state, stream only user-facing text, and show
tool activity/results as clearly labeled status cards. Never render internal
reasoning or treat streamed tool arguments/results as authority. An approval
card displays a server-created proposal's exact before/after diff, affected
items, expiry and stale-state outcome.

Error states must distinguish: AI disabled/not configured (“AI unavailable;
journal still works”), model/network unavailable, rate limit (retry guidance),
content filter (safe explanation, no raw policy internals), authorization
expired (reauthenticate), offline (no send), cancellation, interrupted stream,
and invalid/stale proposal (refresh and review). No chat failure blocks journal,
API or MCP operations.

## Application services, tools and write approval

The model runtime is an adapter only. It receives no database credentials,
connection string, raw SQL tool, or unrestricted network tool. Reads and writes
call the same authorization-aware application services/commands used by the
PWA and MCP. The tool surface is allowlisted, typed and bounded; tool outputs
are size-limited and treated as untrusted input.

- **Explicit individual edits** (for example, capture a clearly specified item
  or mark the named active occurrence done) may execute through the shared
  service after server-side authorization, validation and concurrency checks.
  If the item/reference/action is ambiguous, ask a clarifying question instead.
- **Ceremonies, bulk moves and destructive actions** return a structured
  proposal; they never execute from a model tool call. The UI shows the exact
  diff and the user explicitly approves or rejects it.
- The server persists each proposal as an immutable record scoped to its
  authenticated owner, with a proposal ID, canonical ordered change set,
  expected item versions/ETags, creation/expiry and state. Compute a
  cryptographic hash over the canonical owner-bound change set and expected
  versions; on approval recompute and compare it. Approval names that proposal
  and hash, not a blanket permission.
- Apply all accepted changes atomically through shared application services.
  Reject and request a refreshed proposal if any expected version changed.
  Enforce one-use/idempotent approval with a server-side idempotency record;
  expire pending proposals. Approval of one proposal can never authorize a
  later or modified change set.

The model can suggest a mutation, but only server-side policy decides whether
it is a direct explicit edit or a proposal requiring approval. Model-generated
claims that a user approved something are not approval evidence.

## Privacy, prompt injection and telemetry

- Send only context required for the current request; do not attach the full
  journal by default. The user invokes the feature; no mail, calendar, web or
  release-2 knowledge/RAG ingestion is included.
- Journal items, knowledge entries and tool results are untrusted data, not
  instructions. Delimit them as quoted data, constrain model output to typed
  schemas, and enforce authorization, allowed operations, approval and
  concurrency in server code. No outbound network/browser tool is exposed.
- Do not log prompts, completions, item text, tool arguments/results, raw token
  values, credentials, or approval payloads. Default telemetry is event name,
  opaque request/proposal IDs, duration, input/output token counts if available,
  outcome/error category and model/deployment identifier. Restrict access and
  retention; make content capture unavailable by default.
- Use the user's approved Entra identity for application authorization. A
  server-side managed identity or secret-backed credential authenticates to
  the model provider; never put model credentials in the browser or forward
  the user's Entra bearer token to Foundry. AG-UI endpoint authorization and
  per-user session isolation are separate controls, as documented in the
  [MAF AG-UI security guidance](https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/02-agents/AGUI/README.md).
- Microsoft Foundry privacy, abuse monitoring, retention, processing region
  and Switzerland North availability for the selected deployment were not
  independently verified in this environment. Re-read the current [Foundry
  data/privacy guidance](https://learn.microsoft.com/en-us/azure/ai-foundry/responsible-ai/openai/data-privacy)
  and [abuse monitoring guidance](https://learn.microsoft.com/en-us/azure/ai-foundry/responsible-ai/openai/abuse-monitoring)
  and record model/deployment-specific terms before sending any journal text.
  Do not promise Swiss data residency or zero retention based only on the
  service name.

## Cost and operating configuration

Use a small/mini-class model only as a planning scenario; no model is selected.
Illustrative monthly usage is four weekly planning runs at **12,000 input /
2,000 output tokens each** plus twenty short chat turns at **2,000 input /
500 output tokens each**: **88,000 input and 18,000 output tokens/month**.
This assumes bounded context, no repeated tool loop, and no retries; actual
usage must be measured and capped.

Monthly inference estimate:

`(88,000 × verified input USD / 1,000,000) + (18,000 × verified output USD / 1,000,000)`

The official [Foundry model pricing page](https://azure.microsoft.com/en-us/pricing/details/ai-foundry-models/)
and [Azure pricing calculator](https://azure.microsoft.com/en-us/pricing/calculator/)
could not be retrieved here, so per-model, region/deployment-specific rates and
a total USD estimate are **Unverified**. Before selecting a model, record the
official input/output rates for the actual model, deployment type and region,
recalculate the above scenario, and confirm the combined runtime/database/
telemetry bill against the existing **USD 50/month target**. The proposed
architecture does not depend on a particular model or price, and no claim that
the target is met is made.

| Configuration | Requirement |
|---|---|
| AI feature switch | `AI__Enabled`, default `false`; the host starts and journal/API/MCP remain usable when disabled or unconfigured. |
| Provider/model | Configure only when enabled; record provider, deployment name, region and API version as non-secret configuration. |
| Credentials | Managed identity where supported; otherwise server-side secret storage. Never browser configuration, source, Actions logs or committed files. |
| Limits | Per-request input/output token ceilings, bounded context/tool results, per-user rate limit, daily/monthly spend alert and hard application-side usage cap. Provider quotas/alerts are not assumed to be a bill cap. |
| Telemetry | Redacted by default; monitor latency, token usage, rate limits, errors and budget without journal text. |

## Evaluation cases for implementation issue #8

These are planned acceptance tests, not tests run in this discovery.

| Case | Expected outcome |
|---|---|
| Clear capture request | One item is created through the shared service in the default/explicit list; no duplicate on a repeated request with the same idempotency key. |
| Explicit “mark done” | Only the identified active occurrence changes; ambiguous or historical references prompt clarification. |
| Ambiguous item reference | No tool mutation; ask the user to identify the item. |
| Weekly planning | Return a structured proposal and exact diff; no list changes before approval. |
| Modified/expired approval | Hash/expiry check rejects it; approval cannot be reused for another proposal. |
| Stale item version | Concurrency check rejects the full atomic apply and returns a refreshable conflict; no partial mutation. |
| Injection text stored in an item | Treat text as data; do not invoke unauthorized tools or follow embedded instructions. |
| Stop during streamed response | Cancel is visible, the upstream run stops when supported, and no incomplete tool is replayed. |
| Foundry/provider outage or missing config | Show an actionable unavailable state; all non-AI journal and MCP paths remain operational. |
| Telemetry redaction | Captured logs/traces contain IDs, counts and categories only, not journal text, prompts, completions or tool payloads. |
