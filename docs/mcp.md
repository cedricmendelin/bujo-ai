# Remote MCP

Bujo.Host exposes a stateless Streamable HTTP MCP endpoint at `/mcp`. MCP tools
are adapters over the same `JournalApplicationService` used by the API; MCP
does not use a second store or call the API over HTTP. The endpoint requires an
Entra access token and an approved account on every request. Discovery metadata
is public and contains no credentials or journal data.

## Capabilities and limits

| Tool | Purpose |
|---|---|
| `get_journal` | Read the journal snapshot and move history. |
| `export_journal` | Read a versioned JSON export of lists, items, occurrences, and history. |
| `get_daily_start` | Read daily-start results for an explicit IANA time zone. |
| `get_upcoming` | Read the upcoming view for an explicit IANA time zone. |
| `find_lists` | Find matching lists and return candidate IDs. |
| `capture_item`, `create_list` | Capture an unplanned Future Log item or create a list. |
| `resolve_occurrence` | Resolve an occurrence to its current destination and history. |
| `edit_item`, `set_planned_period`, `set_list_goal` | Make explicit single-item or list-goal edits. |
| `close_list`, `reopen_list` | Close an eligible list or reopen a closed list by ID. |
| `move_item` | Move one item using a client operation ID and expected occurrence ID. |
| `complete_occurrence`, `abandon_occurrence`, `reopen_occurrence` | Change one occurrence's state. |

Tools take stable GUIDs for list, item, and occurrence references. Use
`find_lists` to discover candidates; an ambiguous or unknown name is an explicit
error, never an implicit destination choice. Pass explicit ISO date/period
values rather than phrases such as “today” or “next week.” Daily-start and
upcoming queries require an explicit IANA time zone; the server does not fall
back to its local time zone or UTC.

`move_item` retries with the same `operationId` return the original result, and
`expectedOccurrenceId` detects stale moves. Capture does not yet accept an
operation ID, so a client that loses the response and retries may create a
duplicate. Other writes use the domain's existing concurrency checks.

`close_list` accepts only a list ID, and the domain rejects closing the Future
Log or a list with unresolved occurrences. `reopen_list` accepts only a closed
list ID. Both return the shared domain result and errors; neither creates a
proposal or performs a bulk operation. These tools are implemented in open PR
#133 as a proposed resolution to the former API-only list-lifecycle exception;
owner confirmation of the API-MCP parity contract remains pending.

List names are unique case-insensitively across list kinds, following the
owner's direction. Multiple weekly lists may share the same ISO year/week key.
The uniqueness rule applies to names, not week keys.

There are no bulk or ceremony-generated tools. Do not interpret a prompt or
tool description as authorization: only the listed single-operation tools are
exposed. Prompts are guidance, not automatic (`capture_item`, `daily_start`,
and `explicit_edit`); daily start is a query, not a background job. The
`bujo://journal/capabilities` resource describes available operations and
errors.

## Authentication and safe setup

The MCP server publishes RFC 9728 protected-resource metadata and advertises
`resource_metadata` on unauthenticated MCP requests. Configure the Entra API
resource and approved account using the existing [authentication
guidance](auth.md); the audience (API application ID) and MCP resource URI are
different values. A client must obtain an access token for this API and be
authorized by the configured approved-account policy. An MCP session ID is not
authentication. Do not add a shared token, static key, or bearer value to a
client configuration.

For local development, follow [the Aspire setup](development.md), which keeps
SQL credentials and auth configuration in the AppHost user-secrets store.
Start the app once to see the host origin in the Aspire dashboard, stop it,
then set `Mcp:PublicBaseUrl` to that canonical origin (without `/mcp`). This
ensures discovery and 401 challenges never depend on an untrusted request
`Host` header:

```sh
ASPIRE_CONTAINER_RUNTIME=podman dotnet run --project src/Bujo.AppHost
```

For example, set the value to `http://localhost:<host-port>` from the
dashboard, then restart AppHost with the command above.

Use the host's `/mcp` URL shown by the Aspire dashboard. If a client sends an
`Origin` header, configure that exact origin in local AppHost user secrets; do
not use wildcards:

```sh
dotnet user-secrets set "Mcp:AllowedOrigins:0" "http://localhost:<client-origin-port>" --project src/Bujo.AppHost
```

The AppHost passes this allowlist to the host. Requests without an `Origin`
header do not need an entry. For a reverse-proxied deployment, configure
`Mcp:PublicBaseUrl` to the canonical public origin and allow only the
specific browser origins that need access. Never commit user-secrets files,
access tokens, connection strings, or journal contents. Use a disposable local
journal for manual experiments.

### Deployment configuration blocker

The current Terraform API registration uses
`api://bujo-ai-${var.name_suffix}` as its Application ID URI, while MCP
discovery advertises the canonical HTTPS `/mcp` resource and its
`journal.access` scope. These identifiers must be aligned with the approved
Entra registration before clients can complete the resource-indicated OAuth
flow. The approved plan excludes deployment/registration changes, so this
remains an owner decision and this implementation does not claim live Entra
compatibility. The deployed host must also receive `Mcp:PublicBaseUrl`; without
it, metadata and unauthorized challenges fail closed rather than reflecting an
untrusted Host header.

## Assistant setup examples

The following are configuration examples from the vendors' documentation.
They show how to register a remote Streamable HTTP URL; they have **not** been
connected to this Bujo server. OAuth discovery, Entra sign-in, and tool calls
remain unverified for every client below. Use an owner-approved client
registration and test tenant only; do not substitute a static Authorization
header.

### VS Code with Copilot

The VS Code Copilot MCP configuration supports a remote HTTP server. Add this
to the user's `~/.copilot/mcp-config.json` (or `$COPILOT_HOME/mcp-config.json`):

```json
{
  "mcpServers": {
    "bujo-journal": {
      "type": "http",
      "url": "https://journal.example/mcp",
      "tools": ["*"]
    }
  }
}
```

Replace the example URL with the server's canonical HTTPS URL. See the [VS Code
MCP configuration reference](https://github.com/microsoft/vscode-docs/blob/main/docs/agents/reference/mcp-configuration.md).

### GitHub Copilot CLI

Register the remote HTTP endpoint:

```sh
copilot mcp add --transport http bujo-journal https://journal.example/mcp
```

See [GitHub's Copilot CLI MCP setup](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-mcp-servers).
Copilot CLI is not the Copilot cloud coding agent; the cloud coding agent's
documented remote OAuth limitation makes it unsupported with this authentication
design.

### Claude Code

Register the remote HTTP endpoint at user scope:

```sh
claude mcp add --scope user --transport http bujo-journal https://journal.example/mcp
```

See Anthropic's [Claude Code MCP
guide](https://code.claude.com/docs/en/mcp-quickstart). This documents remote
HTTP setup, not compatibility with Bujo's Entra flow.

### claude.ai and Claude Desktop custom connectors

In the product's **Settings → Customize → Connectors**, choose **Add custom
connector**, enter a name and the exact HTTPS URL ending in `/mcp`, and follow
the product's authentication flow. Claude Desktop custom connectors use the
remote connector surface; this example is not a local
`claude_desktop_config.json` setup. See Anthropic's [custom connector
guide](https://support.claude.com/en/articles/11175166-get-started-with-custom-connectors-using-remote-mcp).

## Compatibility evidence

Local protocol/contract tests exercise the MCP SDK against a test authentication
scheme. They do not prove Entra interoperability or a third-party client's
connection behavior. No live Copilot or Claude connection has been performed.
Version numbers below are local implementation/test dependencies, not client
compatibility claims. Product versions were not recorded because those clients
were not installed and exercised.

| Surface | Version recorded | Status |
|---|---|---|
| Bujo.Host / MCP SDK | .NET SDK 11.0.100 / `ModelContextProtocol.AspNetCore` 2.4.0 | Local build and protocol contract tests passed; real Entra not tested. |
| VS Code Copilot | Not recorded | Untested against Bujo; vendor docs describe remote HTTP configuration. |
| GitHub Copilot CLI | Not recorded | Untested against Bujo; vendor docs describe remote HTTP configuration. |
| Claude Code | Not recorded | Untested against Bujo; vendor docs describe remote HTTP configuration. |
| claude.ai custom connector | Hosted version not exposed | Untested against Bujo; vendor docs describe remote custom connectors. |
| Claude Desktop custom connector | Not recorded | Untested against Bujo; vendor docs describe remote custom connectors. |
| Copilot cloud coding agent | Not applicable | Unsupported by the current Entra OAuth design; GitHub documents no remote OAuth support on this surface. |

This matrix is not a supported-client declaration. Live verification requires
owner-approved Entra registrations and test access, along with the live
authentication proof for issue #4. Record the exact client version, platform,
connection steps, and sanitized observed outcome before changing a row to
tested.
