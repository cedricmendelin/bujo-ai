# Single-tenant authentication design

## Issue #10 v3 rendering/BFF checkpoint

The host now implements a **thin proof**, not the complete journal UI or a
verified Entra deployment. Static SSR renders anonymous `/` and safe account
states; only `/rendering-proof` creates an Interactive Server circuit. Its
synthetic text and connection check never access SQL or save journal items.
Interactive Server with static SSR where appropriate is Accepted in
[ADR 0001](adr/0001-technology-baseline.md) as of 2026-11-17. This checkpoint
provides implementation evidence, not live verification. Owner and Architect
review of that evidence, required by the [PR #108 handoff](https://github.com/cedricmendelin/bujo-ai/pull/108),
remains outstanding before journal UI work. Issue #10 was
closed after this partial proof; [issue #124](https://github.com/cedricmendelin/bujo-ai/issues/124)
now tracks the unfinished journal UI and proof evidence in the
[delivery matrix](delivery-matrix-124.md). Live Entra sign-in, target-browser
installation, cost and multi-replica recovery remain unverified. The original
issue #4 MCP/client proposal below remains provisional.

### Implemented boundaries

- UI uses named `Bujo.Cookie` and `ApprovedUi`; APIs, MCP, and readiness
  explicitly use JWT bearer. An otherwise-valid UI cookie cannot authorize
  `/api` or `/mcp`.
- OIDC `Bujo.Oidc` uses authorization code + PKCE, framework state/nonce
  validation, tenant-specific HTTPS metadata, and the fixed `/signin-oidc`
  callback. The ID-token identity must match the independently validated
  delegated access token's `tid` and `oid`; `journal.access` is required.
- `__Host-Bujo` is Secure, HttpOnly, SameSite=Lax, path `/`, non-sliding, with
  a maximum one-hour lifetime. `SaveTokens=false`: the cookie contains no access,
  refresh, or ID token. A bounded, process-memory-only server cache retains the
  delegated token until its expiry or one hour, whichever comes first.
  Tokens are not refreshed; expiry, eviction, restart or logout requires sign-in.
- Every protected UI HTTP request, circuit open/reconnect, and inbound circuit
  activity checks current configuration's immutable approved `(tid, oid)` pair,
  delegated scope and server-held token signature/issuer/audience/lifetime.
  Revocation rejects the next request/event/reconnect; this is not continuous
  Entra revocation notification and does not erase previously rendered DOM.
- `/auth/sign-in` and `/auth/sign-out` GETs only render forms. Their POSTs require
  antiforgery; sign-out removes the server session immediately and clears the
  local cookie. Upstream Entra SSO is **not** terminated; another sign-in may
  reuse the identity provider's SSO session.
- Anonymous `/auth/signed-out`, `/auth/denied`, `/auth/error` contain generic
  messages only. Account/protected responses are private/no-store. Static
  installation assets are anonymous; the root `/service-worker.js` serves the
  actual worker with root scope. The worker only caches allowlisted public
  assets, never HTML, authenticated responses, API responses, or tokens.
- CSP restricts scripts, styles and connections to same origin, prohibits
  framing and objects; anti-sniffing, referrer and permissions headers are set.
  Production enables HSTS. The Azure Container Apps deployment exposes the app
  port only through HTTP ingress. When the platform sets `CONTAINER_APP_NAME`,
  the host accepts one `X-Forwarded-Proto` hop because
  [Container Apps overwrites that header](https://learn.microsoft.com/en-us/azure/container-apps/ingress-overview#http-headers).
  Forwarded host and client IP are not used by this middleware. Do not expose
  the app port directly, such as through an additional TCP port. For other
  reverse proxies, configure exact comma-separated ingress IPs in
  `Hosting:KnownProxies`; an explicit list also overrides the Container Apps default.

### Runtime configuration and deployment prerequisites

Supply through protected runtime configuration, never committed values:

| Key | Purpose |
| --- | --- |
| `Auth:TenantId`, `Auth:Audience`, `Auth:AllowedPrincipals` | Existing API tenant, exact API audience, immutable approved tenant/object pairs |
| `Auth:Pwa:ClientId` | Single-tenant confidential web-client registration |
| `Auth:Pwa:ManagedIdentityClientId` | Production user-assigned managed identity client ID; used to request the federated client assertion |
| `Auth:Pwa:ClientSecret` | Local-only server-side client credential, used when managed identity is not configured |
| `Auth:Pwa:Scope` | Fully qualified delegated API scope ending in `/journal.access` |
| `Hosting:KnownProxies` | Exact trusted ingress proxy IPs for other TLS-terminating reverse proxies, or to override the Container Apps default |

The API and browser use distinct Entra registrations. `Auth:Audience` is the
API registration's Application (client) ID; that registration exposes the
delegated `journal.access` scope. `Auth:Pwa:ClientId` identifies the separate
confidential Web client, whose server-only secret is `Auth:Pwa:ClientSecret`.
`Auth:Pwa:Scope` is the API's fully qualified delegated scope, not an audience.
Register the exact externally visible HTTPS `/signin-oidc` callback on the Web
client. Locally, keep these values in AppHost user secrets; PR #125 forwards
configured PWA settings to `journal-host`. In a deployment, provide them only
through protected server-side runtime configuration. Neither path authorizes
changing Entra registrations, granting consent, or deploying.

`Auth:AllowedPrincipals` is a comma-separated list of complete
`tenant-guid:object-guid` pairs, and the tenant must match `Auth:TenantId`.
Bare Object IDs are not accepted. The current parser silently ignores malformed
entries, leaving no matching principals; clear configuration diagnostics and a
focused bare-ID rejection test remain outstanding in issue #124. Do not change
the authorization contract by accepting bare IDs.

Register the exact externally visible HTTPS `/signin-oidc` redirect URI.
Use HTTPS locally (`dotnet dev-certs https --trust` where supported); Secure
antiforgery cookies intentionally prevent HTTP sign-in. Ensure the approved
proxy forwards the real HTTPS scheme so OIDC does not produce an HTTP callback.
Do not treat Container Apps deployment or proxy configuration as verified here.
Protect Data Protection keys appropriately in production. This checkpoint
assumes **one replica**: cookies, server token sessions and circuits do not
provide cross-replica/restart continuity. No infrastructure or tenant change is
made here.

### Evidence limits / human gate

Automated tests use locally generated signing keys for access-token validation
and test-host-only authentication for browser rendering. They are not real Entra
sign-in, consent, client-secret, redirect, remote-client or Azure deployment
evidence. The proof action is only a connection roundtrip, not persistence.

## 🙋 Needs human

@cedricmendelin: provide/verify the registered HTTPS redirect, protected PWA
client configuration, trusted ingress scheme forwarding and approved account,
then perform real Entra sign-in/denial/sign-out and target-browser installation.
Owner and Architect must review the proof before further journal UI work.
Issue #10 remains closed as a historical partial delivery; issue #124 tracks
the remaining UI and acceptance work. Do not infer proof-gate completion from
the PR merge or reported local sign-in.

---

**Status:** proposal for issue [#4](https://github.com/cedricmendelin/bujo-ai/issues/4),
implementation plan v1; not an approved decision or proof of interoperability.
Owner: AI; review: Architect, Security, Verifier. The owner must select the
required clients and approve both the design and a matrix backed by observed
results before the access-control/MCP exit gate is complete. Nothing here
authorizes registrations, tenant changes, credential access, or live tests.

## Evidence and versions

Sources below were checked **2026-11-14**. Product documentation, not installed
product binaries, was checked; version numbers are recorded only where the
source supplies one. Hosted products have no pinned version here. GitHub source
links are primary documentation read through GitHub when the published site was
unreachable. Direct fetches of the RFC sites and some product sites were blocked;
RFC links are references, not claims that their full texts were independently
checked. No live identity or client interaction was performed.

| Source | Evidence checked |
| --- | --- |
| [MCP 2025-11-25 authorization](https://github.com/modelcontextprotocol/modelcontextprotocol/blob/main/docs/specification/2025-11-25/basic/authorization.mdx), [transport](https://github.com/modelcontextprotocol/modelcontextprotocol/blob/main/docs/specification/2025-11-25/basic/transports.mdx), [2025-06-18 authorization](https://github.com/modelcontextprotocol/modelcontextprotocol/blob/main/docs/specification/2025-06-18/basic/authorization.mdx) | Versioned spec text read. 2025-11-25 uses Streamable HTTP and recommends CIMD; 2025-06-18 is an earlier authorization baseline. |
| [MCP 2026-07-28 changelog](https://github.com/modelcontextprotocol/modelcontextprotocol/blob/main/docs/specification/2026-07-28/changelog.mdx) | Newer versioned text deprecates DCR in favor of CIMD and changes transport/lifecycle; release status **unverified** because it also refers to a draft. Do not silently assume client support. |
| [RFC 9728](https://www.rfc-editor.org/rfc/rfc9728.html), [RFC 8414](https://www.rfc-editor.org/rfc/rfc8414.html), [RFC 7591](https://www.rfc-editor.org/rfc/rfc7591.html), [RFC 8707](https://www.rfc-editor.org/rfc/rfc8707.html), [RFC 7636](https://www.rfc-editor.org/rfc/rfc7636.html) | Protected-resource metadata, authorization-server metadata, dynamic client registration (DCR), resource indicators, and PKCE respectively; full RFC texts not fetched. The MCP and Microsoft sources below were read for their application. |
| [Microsoft: secure an MCP server with Entra](https://github.com/MicrosoftDocs/entra-docs/blob/main/docs/agent-id/secure-mcp-server-with-entra-id.md), [App Service MCP authorization](https://github.com/MicrosoftDocs/azure-docs/blob/main/articles/app-service/configure-authentication-mcp.md), [token version](https://github.com/MicrosoftDocs/entra-docs/blob/main/docs/identity-platform/includes/scenarios/accepted-token-versions.md) | Documentation read, not a deployment test. The App Service article is evidence about Entra, not a recommendation to deploy App Service. |
| [VS Code configuration](https://github.com/microsoft/vscode-docs/blob/main/docs/agents/reference/mcp-configuration.md), [Copilot CLI](https://github.com/github/docs/blob/main/content/copilot/how-tos/copilot-cli/customize-copilot/add-mcp-servers.md), [cloud agent limitations](https://github.com/github/docs/blob/main/data/reusables/copilot/mcp/repo-mcp-limitations.md), [Copilot connectors](https://github.com/github/docs/blob/main/content/copilot/concepts/customizations/copilot-connectors.md) | Product documentation read; client capabilities and deployment outcomes are distinct. |
| [Anthropic remote MCP setup](https://github.com/anthropics/claude-plugins-official/blob/main/plugins/mcp-server-dev/skills/build-mcp-server/references/remote-http-scaffold.md), [Claude auth reference](https://github.com/anthropics/claude-plugins-official/blob/main/plugins/mcp-server-dev/skills/build-mcp-server/references/auth.md), [Claude Code example](https://github.com/anthropics/claude-plugins-official/blob/main/external_plugins/asana/README.md) | Anthropic-maintained references read; the Code example is a specific integration, not proof of Entra compatibility. |

## Candidate client matrix

These are candidates for the owner to prune, **not a supported-client list**.
Every row is **Blocked — awaiting separate owner approval** for live setup
and proof. “Documented” describes only the cited product surface. No
application/client version has been tested; record exact version, platform,
entitlement, policy and date when testing.

| Product and connection surface (documentation checked 2026-11-14) | Documented transport and auth; uncertainties | Result |
| --- | --- | --- |
| VS Code Copilot agent, editor-managed MCP config | [VS Code](https://github.com/microsoft/vscode-docs/blob/main/docs/agents/reference/mcp-configuration.md) documents Streamable HTTP, legacy SSE fallback, OAuth and `oauth.clientId`; Copilot harness portable config instead uses `oauthClientId`. Minimum version and exact Entra behavior unverified. | Blocked |
| GitHub Copilot CLI, interactive `/mcp add` or user config | [CLI](https://github.com/github/docs/blob/main/content/copilot/how-tos/copilot-cli/customize-copilot/add-mcp-servers.md) documents HTTP and legacy SSE; [VS Code reference](https://github.com/microsoft/vscode-docs/blob/main/docs/agents/reference/mcp-configuration.md) describes the portable `oauthClientId` field. Arbitrary-server OAuth, scopes and redirects need proof; managed connectors are a separate preview surface. | Blocked |
| GitHub Copilot cloud coding agent, repository settings/custom-agent profile | [GitHub](https://github.com/github/docs/blob/main/data/reusables/copilot/mcp/repo-mcp-limitations.md) says remote OAuth is not currently supported on this surface; only MCP tools, not prompts/resources. [MCP config](https://github.com/github/docs/blob/main/content/copilot/concepts/customizations/mcp.md) describes remote HTTP/SSE configuration. This is a documented incompatibility with the proposed OAuth flow, not a live failure. | Blocked |
| Copilot Chat on github.com, cloud sandbox | [GitHub](https://github.com/github/docs/blob/main/content/copilot/concepts/copilot-surfaces/copilot-on-github.md) distinguishes sandbox sessions; [verified connectors](https://github.com/github/docs/blob/main/content/copilot/concepts/customizations/copilot-connectors.md) use service sign-in and are not available in Actions sessions. Custom personal server OAuth/transport for this specific surface is unverified. | Blocked |
| Microsoft Copilot Studio, MCP tool/connector | [Microsoft's SAP reference](https://github.com/MicrosoftDocs/azure-docs/blob/main/articles/sap/microsoft-ai/copilot-studio/architecture-mcp-gateway-integration-suite.md) documents Streamable HTTP and OAuth authorization code for that integration. General Entra discovery, custom-client registration and legacy SSE are unverified. Hosted version unspecified. | Blocked |
| claude.ai, custom connector | [Anthropic](https://github.com/anthropics/claude-plugins-official/blob/main/plugins/mcp-server-dev/skills/build-mcp-server/references/remote-http-scaffold.md) documents remote HTTP via Settings → Connectors; its [auth reference](https://github.com/anthropics/claude-plugins-official/blob/main/plugins/mcp-server-dev/skills/build-mcp-server/references/auth.md) lists DCR, CIMD and partner-held OAuth credentials. Pre-registered Entra client configuration and legacy SSE unverified. | Blocked |
| Claude Desktop, custom connector | Same [remote connector setup](https://github.com/anthropics/claude-plugins-official/blob/main/plugins/mcp-server-dev/skills/build-mcp-server/references/remote-http-scaffold.md), not the Desktop local-server config; [auth reference](https://github.com/anthropics/claude-plugins-official/blob/main/plugins/mcp-server-dev/skills/build-mcp-server/references/auth.md). Installed version and direct Entra behavior unverified. | Blocked |
| Claude Code, terminal MCP configuration | Anthropic documents [HTTP setup](https://github.com/anthropics/claude-plugins-official/blob/main/plugins/mcp-server-dev/skills/build-mcp-server/references/remote-http-scaffold.md) and a [pre-registered OAuth example](https://github.com/anthropics/claude-plugins-official/blob/main/external_plugins/asana/README.md) with a local callback. Public-client Entra flow and legacy SSE unverified. | Blocked |
| Claude mobile, connector surface | [Claude connector help](https://support.claude.com/en/articles/11175166-get-started-with-custom-connectors-using-remote-mcp) could not be fetched; custom setup, transport, OAuth and version **unverified**. Do not infer them from Desktop. | Blocked |

## Requirements: protocol, identity provider and client

| Layer | Requirement or behavior | Verification |
| --- | --- | --- |
| MCP spec | For HTTP authorization, [2025-11-25](https://github.com/modelcontextprotocol/modelcontextprotocol/blob/main/docs/specification/2025-11-25/basic/authorization.mdx) requires protected-resource metadata containing `authorization_servers`; a challenge metadata URL or well-known discovery is required. Clients support both. Authorization itself is optional in the protocol, but mandatory for this app. | Spec text read; no implementation tested. |
| MCP spec | Clients send RFC 8707 `resource` in both authorization and token requests, use PKCE (`S256` when capable), and servers validate token audience. Pre-registration is a supported option; CIMD is recommended and DCR is a fallback. | Same spec text read; verify negotiated version and actual client behavior. |
| MCP spec | [Streamable HTTP](https://github.com/modelcontextprotocol/modelcontextprotocol/blob/main/docs/specification/2025-11-25/basic/transports.mdx) supersedes 2024-11-05 HTTP+SSE; SSE can also be used *within* Streamable HTTP responses. Validate `Origin` when present. | Spec text read; no client tested. |
| Entra behavior | [Microsoft](https://github.com/MicrosoftDocs/entra-docs/blob/main/docs/agent-id/secure-mcp-server-with-entra-id.md) now documents matching the `resource` URI to the API's HTTPS Application ID URI, and issuing a v2 token with the API client ID as `aud`. It documents exact URI matching and scope/resource alignment. This contradicts plan v1's initial risk assumption that Entra does not honor `resource`; test it in the chosen tenant. | Documentation read; tenant behavior untested. |
| Entra behavior | [Microsoft](https://github.com/MicrosoftDocs/azure-docs/blob/main/articles/app-service/configure-authentication-mcp.md) says Entra does not offer RFC 7591 DCR; a preconfigured client ID and consent/preauthorization are needed. CIMD support in this tenant is unverified. | Documentation read; tenant and each client's registration/consent untested. |
| Client behavior | Redirect URIs, client-ID configuration, discovery, PKCE metadata, consent prompting and permitted scopes vary by product/version. GitHub's cloud-agent OAuth limitation is scoped to repository settings/custom-agent profiles, not every Copilot surface. | See matrix; no end-to-end pass. |

## Flow options and recommendation

**Provisional recommendation: A + C**, subject to tests and owner approval:
the shared host is an OAuth protected resource; use Entra directly as
authorization server (A), and pre-register public clients where each client can
accept its own ID and supported redirect URI (C). This avoids a custom token
issuer but does not solve a client that requires DCR/CIMD, has no configurable
client ID, or cannot complete tenant consent. The plan's originally assumed
absence of Entra `resource` support is not supported by the current Microsoft
documentation; exact canonical URI matching is a practical constraint instead.

For an MCP URL `(mcp-url)`, publish RFC 9728 metadata at the path-specific
well-known location, and/or explicitly advertise its URL in `401
WWW-Authenticate` via `resource_metadata`; publish both for compatibility.
Metadata identifies `(mcp-url)` as `resource`, the tenant-specific Entra v2
issuer as `authorization_servers`, and the fully qualified delegated scope in
`scopes_supported`. For a path-bearing URL, use the RFC 9728 path-derived
location as well as a root location if appropriate. Clients discover the
tenant-specific authorization/token endpoints and keys from its OIDC/RFC 8414
metadata; validate issuer consistency. Send an appropriate scope challenge.
No token or journal data belongs in discovery metadata.

The client uses authorization code + PKCE, its registered redirect and its
resource-qualified scope; it sends the canonical resource in both requests.
The host validates the resulting access token on **every** MCP HTTP request,
then executes the same application policy used by API and PWA. An MCP session
ID is not authentication. Verify actual metadata, `S256` advertisement, consent
and redirect compatibility before relying on this flow.

**B: OAuth facade** is a fallback proposal only, not a committed design. A
separate, properly reviewed authorization server could publish its own issuer
and metadata, implement CIMD or DCR as required, use Entra for upstream
identity, and issue its own short-lived, MCP-audience-bound tokens. It must
validate upstream issuer/tenant/identity, securely bind the mapped `tid` and
`oid`, protect client registration and redirects, PKCE/state, consent, keys,
refresh/revocation and tokens, and defend metadata retrieval against SSRF.
The resource must validate the facade's own issuer, signing keys, audience,
scope and mapped identity—not pretend facade tokens are Entra tokens. This adds
security-critical code, operations and cost; obtain a separate approved design
and security review before building it. Merely proxying metadata or forwarding
an Entra token does not supply these guarantees.

### PWA choice

Recommend a same-origin server-side BFF/cookie session in the agreed modular
host, using Entra authorization code + PKCE and server-side token handling.
The browser sends a Secure, HttpOnly, SameSite cookie to the host; state-changing
requests require CSRF protection. Validate OIDC state/nonce and redirect URI;
protect session data, expire/revoke sessions, and re-evaluate the allowlist on
every protected request. The BFF obtains a delegated API access token for
`journal.access`, validates it as described below, and binds its validated
`tid` and `oid` to the validated sign-in identity. Keep the token server-side.
Before each protected operation, validate its lifetime, audience, issuer,
signature, tenant and scope and ensure its identity still matches the session;
refresh or reacquire it securely when needed, otherwise deny access. The BFF
then invokes shared services under that authorized identity or calls the API
with that token. An ID token alone is not evidence of an API scope. Protect
Blazor circuits and reconnects as well as HTTP requests. Rendering mode and
session persistence across replicas remain foundation decisions.

Alternative: SPA authorization code + PKCE with a SPA redirect and an
API-audience bearer token. This avoids server sessions but exposes browser-held
tokens to XSS and needs secure storage/renewal. Neither choice makes a remote
MCP client authenticated; this recommendation remains subject to host review.

## Registrations, scopes and configuration

All values are placeholders. **Do not create or inspect registrations or
credentials without separate owner approval.** No tenant IDs, account IDs,
tokens, connection strings or secrets belong in source or test reports.

| Component | Proposed registration/configuration | Token or authorization use |
| --- | --- | --- |
| API + MCP | One single-tenant API resource registration; v2 access tokens (`accessTokenAcceptedVersion = 2`, equivalent Graph manifest `api.requestedAccessTokenVersion = 2`); HTTPS Application ID URI exactly `(mcp-url)`; expose `(mcp-url)/journal.access`. | Entra v2 `aud` is `(api-client-id)`, **not** the URL; `scp` includes `journal.access`. Separate knowledge/MCP scopes only if later approved. |
| PWA BFF | Separate single-tenant web client with its approved HTTPS redirect, authorization code + PKCE and server-managed credentials if required. | ID token signs in the BFF only; API access requires the delegated API scope and an API access token. A cookie is not an MCP bearer token. |
| Each MCP client | Appropriate public-client registration and product-specific redirect URI, where supported; client ID supplied through that client's secure configuration. Public clients must not embed a shared secret. | Obtain a delegated token for the API, not an ID token, Graph token, or token for another API. Some client surfaces may require a different registration model; verify before use. |
| Deployment | `(tenant-id)`, `(api-client-id)`, canonical `(mcp-url)`, exact issuer, scope and approved `(tenant-id, object-id)` pairs in protected runtime configuration/Key Vault as appropriate; no values committed. | Default deny if config is absent or invalid. |

Configure consent for the delegated scope for each client; user or administrator
consent depends on tenant policy and permission settings. Preauthorization of
known clients may avoid prompts but is not user authorization. Where applicable,
require assignment on the relevant enterprise applications as defense in depth;
evaluate API and client enterprise apps separately. Assignment required can
require tenant-wide admin consent and, for the API, client app role assignments
before token issuance. Verify both effects in the approved tenant; never treat
assignment alone as the allowlist.
See [Microsoft consent](https://learn.microsoft.com/en-us/entra/identity-platform/permissions-consent-overview)
(published page not directly fetched), [assignment](https://github.com/MicrosoftDocs/entra-docs/blob/main/docs/identity-platform/howto-restrict-your-app-to-a-set-of-users.md)
and [API scope configuration](https://github.com/MicrosoftDocs/entra-docs/blob/main/docs/identity-platform/scenario-protected-web-api-expose-scopes.md)
(repository documentation checked 2026-11-14).

## Shared authorization and rejection vectors

Authentication middleware validates signature using the tenant's trusted Entra
keys, exact v2 issuer and `tid`, API `aud`, token lifetime and token type.
Require a delegated user token with `oid` and `scp`; reject app-only tokens,
ID tokens, missing subject and unexpected issuers. Then a shared application
policy checks the configured immutable `(tid, oid)` pair and required scope on
every operation, including PWA, API and MCP. Do not use email, UPN, display name
or tenant membership as identity authorization. An authenticated BFF session
must carry a validated identity and evidence of the required delegated
permission; it is rechecked against current configuration, not trusted forever.

These are **future test vectors**, not executed tests. API and MCP receive
bearer tokens and return the shown HTTP status; MCP `401` includes the
discovery challenge. The PWA receives a cookie, not a bearer token. For PWA
tests, use a test identity provider/token validator to create a BFF session
with the corresponding underlying delegated-token condition; rejected token
conditions must prevent session creation or further access. The PWA statuses
refer to protected HTTP/data operations; interactive navigation may instead
initiate sign-in.

| Input on PWA, API and MCP | Expected result |
| --- | --- |
| No token / no valid session | 401; no protected data or tool execution |
| Malformed, expired, bad-signature or wrong token type | 401 |
| Valid signature but wrong API audience | 401 |
| Valid token from a different tenant or issuer | 401 |
| Valid delegated in-tenant identity, `oid` not allowlisted | 403 |
| Valid allowlisted identity, missing required delegated scope | 403, insufficient scope |
| Valid tenant, audience, lifetime, scope and allowlisted `oid` | Allow the requested operation subject to domain rules |

Also test missing `tid`/`oid`, missing allowlist, revoked/expired sessions,
removed allowlist entries, and a mismatched session/token identity. An MCP
request with an invalid `Origin` receives 403. Never invoke a service before
authorization succeeds. A BFF request without an access token is evaluated
through its validated session and server-held token, not as a raw bearer request.

## Downstream consent, privacy and logging

App sign-in and journal API/MCP authorization do not consent to Gmail,
Calendar, Foundry or any other downstream service. Do not pass an incoming
journal token to another resource. Any future integration requires its own
resource-specific consent and token obtained via a supported on-behalf-of
exchange or an appropriately scoped managed identity, as applicable; this is
not an implementation decision for those integrations.

Do not log tokens, authorization headers, cookies, journal/knowledge content,
prompts, tool arguments or responses. Log only the authorization decision,
reason code, and a keyed hash or opaque subject identifier; keep the key and
any identifier mapping out of logs. Review infrastructure, proxy and client
diagnostic logging for the same exclusions.

## Gated proof runbook and results

**Every live step below requires separate explicit owner approval**, including
creating registrations, changing tenant settings, accessing credentials,
deploying a test server, signing in, granting consent and running clients.
Before approval, all results remain **Blocked — awaiting approval**; no
compatibility pass or failure has been observed. The owner must name the
operator, tenant, permitted client surfaces and test window. Use synthetic
non-sensitive data and an isolated approved test deployment.

After approval, the operator should:

1. Register/configure the API, web client and each applicable MCP client as
   above, enable only necessary delegated scopes and permitted redirects, and
   arrange required admin/user consent and assignment. Never record IDs or
   credentials in the repository.
2. Deploy the protected host. Verify its TLS URL, unauthenticated `401`,
   protected-resource metadata, issuer/discovery, canonical `resource`, scope,
   registered callback and PKCE. Use approved instrumentation that records
   only status/reason codes, not token values or journal contents.
3. For each client below, connect to `(mcp-url)`, sign in as the approved
   identity, approve only the journal scope, discover a harmless read-only
   synthetic tool and invoke it. Confirm the shared policy authorized the
   request, then repeat with the rejection vectors above using approved test
   identities/tokens and exercise PWA/API separately. A sign-in alone is not a
   pass. If a negative identity cannot be provisioned, mark that vector blocked.
4. Record client version/platform, date, exact surface/transport, mechanism,
   consent and discovery result, each negative-case status, and a sanitized
   success/failure reason. Record no identifiers, token values, headers or
   personal data. Tear down approved test resources when no longer needed.

| Surface | Minimal client-specific action after approval | Expected result / current observation |
| --- | --- | --- |
| VS Code | Add HTTP server in the chosen editor or harness configuration with its registered client ID; authenticate and invoke synthetic tool. | Tool works only for allowlisted user; **Blocked**. |
| Copilot CLI | Add HTTP server via `/mcp add` and configure the registered client ID in supported user config; authenticate and invoke. | Same; if client cannot perform required flow, record failure; **Blocked**. |
| Copilot cloud agent | Attempt approved repository/custom-agent configuration only if a future supported OAuth route is documented; run a synthetic tool from the agent. | Current documented OAuth incompatibility means no direct-flow pass expected; **Blocked**, not a live failure. |
| github.com Copilot Chat | In an eligible cloud sandbox locate a custom server/connector surface and attempt OAuth; record if only verified connectors or another auth method are available. | Synthetic tool call with approved identity, otherwise documented limitation; **Blocked**. |
| Copilot Studio | Add an MCP tool/connector with approved URL and OAuth registration; run the agent with the approved user. | Synthetic tool call with correct delegated identity; **Blocked**. |
| claude.ai | Settings → Connectors → Add custom connector; attempt direct Entra registration/consent and invoke. | Synthetic tool call; if client registration is unavailable, record incompatibility; **Blocked**. |
| Claude Desktop | Add remote custom connector via Settings → Connectors, authenticate independently or verify the shared connection, and invoke. | Same; record actual version and mechanism; **Blocked**. |
| Claude Code | Add remote HTTP MCP server with the approved client ID and registered local callback; authenticate via the client and invoke. | Same, with public-client behavior verified rather than assumed; **Blocked**. |
| Claude mobile | Locate custom connector or previously approved connector on the specific platform; attempt sign-in and invocation. | Same, or mark unavailable with observed reason; **Blocked**. |

### Fallbacks and exit gate

For a failed or unavailable client, capture the sanitized incompatibility and
request a revised owner-approved proposal: choose another required surface,
investigate a supported pre-registration/consent path, or separately review an
OAuth facade. Re-test the complete flow. Do **not** introduce anonymous
endpoints, shared static API keys, token passthrough, weaker tenant/audience
checks or silently label the client supported. Only a recorded end-to-end pass
and approval can promote a row to the supported matrix.

## Proposed decision for owner approval — not yet recorded

Use the modular host as the shared PWA/API/MCP authorization boundary, with
single-tenant Entra, a default-deny immutable `tid` + `oid` allowlist, delegated
scopes and audience validation on every request. Prefer a server-side PWA BFF
and direct Entra with individually pre-registered MCP clients. Publish protected
resource metadata and use Streamable HTTP. Treat the cloud-agent OAuth
limitation and all untested surfaces as unresolved; consider a separately
reviewed facade only if an approved target requires it. Approve the required
clients, design and observed matrix before closing the access-control/MCP exit
gate. The owner can record an accepted decision separately in
`.squad/decisions.md`; this proposal does not change it.
