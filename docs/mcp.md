# Remote MCP

Bujo.Host exposes a stateless Streamable HTTP MCP endpoint at `/mcp`. MCP tools
are adapters over the same `JournalApplicationService` used by the API; MCP
does not use a second store or call the API over HTTP. The endpoint requires an
Entra access token and an approved account on every request. Discovery metadata
is public and contains no credentials or journal data.

## Connect and try it

1. Ask your deployment owner for the canonical HTTPS URL ending in `/mcp` and
   confirm that your Entra account and chosen assistant client are approved.
   Signing into the web app does not connect the assistant.
2. Add the URL as a remote HTTP MCP server using one of the
   [client examples](#assistant-setup-examples). Complete the client's sign-in
   flow if offered. The examples show configuration, not verified compatibility.
3. Start with a read-only request like “Use Bujo to show my daily-start review in
   `Europe/Zurich`. Don't change anything.” Use your own IANA time zone.
   Confirm the client actually called `get_daily_start` and returned your data.
   If it can't discover tools or sign in, check the exact URL and approved
   account with the deployment owner; don't paste tokens into chat or config.

Once connected, try these requests. Ask the assistant to show the tool result
before assuming a write succeeded.

| Ask your assistant | What to expect |
|---|---|
| “Capture ‘Book a bike service’ in Bujo, unplanned.” | `capture_item` adds it to the Future Log. If the response is lost, check the journal before retrying. |
| “Find my ‘Bike’ list. If more than one matches, ask me which one.” | `find_lists` looks up candidates without changing anything. Writes use the chosen list's ID. |
| “Search saved knowledge for ‘bike maintenance’; show me the original passages.” | `search_knowledge` retrieves matching saved text, not an AI-generated answer. |
| “We have a free family weekend; suggest activities from ideas I saved, and ask about constraints first.” | Use the `family_weekend_ideas` prompt to retrieve saved knowledge, ask about missing constraints, and give a few sourced options. |
| “Show me the unfinished items on my Bike list and propose moving ‘Book a bike service’ to the Future Log. Don't apply it yet.” | Inspect the proposal at the returned web app path and approve it there if correct. Then apply it in the web app or ask the assistant to apply the approved proposal. Saying “approve” in chat isn't approval. |

If you're trying Bujo for the first time, see the [web app guide](user-guide.md).
Only connect a client you trust to process your journal data; tool results can
contain private entries. Start with a disposable journal when testing a write
or a restore.

MCP prompt and response content is not currently captured as application
telemetry. The owner has approved a protected capture path only after the
controls in [operations](operations.md) are verified; ordinary tool payloads
and credentials remain excluded.

## Capabilities and limits

| Tool | Purpose |
|---|---|
| `get_journal` | Read the journal snapshot and move history. |
| `export_journal` | Read a versioned JSON export of lists, items, occurrences, and history. |
| `get_daily_start` | Read daily-start results for an explicit IANA time zone. |
| `get_upcoming` | Read the upcoming view for an explicit IANA time zone. |
| `find_lists` | Find matching lists and return candidate IDs. |
| `navigate_list_period` | Read all lists in a selected, previous, next, or current calendar period. |
| `capture_item`, `create_list` | Capture an unplanned Future Log item or create a list. |
| `resolve_occurrence` | Resolve an occurrence to its current destination and history. |
| `edit_item`, `set_planned_period`, `set_list_goal` | Make explicit single-item or list-goal edits. |
| `close_list`, `reopen_list` | Close an eligible list or reopen a closed list by ID. |
| `get_list_review` | Separate unfinished active occurrences, historical moved links with resolved destination/state, and resolved history. |
| `create_review_proposal`, `get_review_proposal` | Create or inspect exact, versioned list-review changes and their server-computed digest. |
| `cancel_review_proposal`, `reject_review_proposal`, `apply_review_proposal` | Cancel/reject without data changes, or apply after approval in the PWA. |
| `move_item` | Move one item using a client operation ID and expected occurrence ID. |
| `complete_occurrence`, `abandon_occurrence`, `reopen_occurrence` | Change one occurrence's state. |
| `export_journal_backup`, `restore_journal_backup` | Export or atomically replace the complete journal and, when present in the snapshot, saved knowledge. |
| `capture_knowledge`, `get_knowledge_entry`, `update_knowledge_entry` | Save verbatim content, read by stable ID, or update with an expected version. |
| `set_knowledge_archived`, `delete_knowledge_entry` | Reversibly archive one entry or explicitly and permanently delete one entry. |
| `search_knowledge`, `list_knowledge_tags`, `get_knowledge_search_status` | Search original passages by English full-text terms and AND tag filters, list tags, or inspect index readiness. |
| `export_knowledge_backup`, `restore_knowledge_backup` | Export a versioned knowledge snapshot or explicitly replace the knowledge collection from one. |

Tools take stable GUIDs for list, item, and occurrence references. Use
`find_lists` to discover candidates; an ambiguous or unknown name is an explicit
error, never an implicit destination choice. Pass explicit ISO date/period
values rather than phrases such as “today” or “next week.” Daily-start and
upcoming queries require an explicit IANA time zone; the server does not fall
back to its local time zone or UTC.

`POST /api/items` and `capture_item` accept an optional `type` of `Task` or
`Thought`; omission defaults to `Task` for existing clients. An Item is the
shared record for either type. Thoughts are reflections, not actionable tasks,
and completion is rejected through both the API transition and MCP
`complete_occurrence`. Existing journal reads and exports include the type.

`move_item` retries with the same `operationId` return the original result, and
`expectedOccurrenceId` detects stale moves. Capture does not yet accept an
operation ID, so a client that loses the response and retries may create a
duplicate. Other writes use the domain's existing concurrency checks.

`close_list` accepts only a list ID, and the domain rejects closing the Future
Log or a list with unresolved Tasks. Thoughts remain readable without a
disposition. Use `get_list_review` to see active
occurrences separately from moved links. A proposal captures exact item and
occurrence IDs, expected versions, destinations, requested closure, and a
server-computed SHA-256 digest; proposals expire after 24 hours and cannot be
edited. MCP can create, read, cancel, reject, or apply a proposal, but there is
no MCP approval tool or `approved` argument. The proposal creator must approve
the exact digest in the authenticated interactive PWA. Apply rechecks that
single-use approval and applies every disposition and requested closure in one
transaction; stale proposals conflict and need a new review. `reopen_list`
remains an explicit direct action and does not change occurrence states.
`create_review_proposal` returns a PWA review path with its proposal ID so the
creator can load, inspect, and approve the persisted proposal after leaving MCP.

List names are unique case-insensitively across list kinds, following the
owner's direction. Multiple weekly lists may share the same ISO year/week key.
The uniqueness rule applies to names, not week keys.

`create_list` accepts `Monthly`, `Quarterly`, and `Yearly` kinds with a
`periodKey` object containing a matching `kind`, `year`, and respectively
`month`, `quarter`, or neither. For example, a monthly key is
`{"kind":"Monthly","year":2026,"month":11}`. Weekly `weekKey` remains
optional for legacy clients. `navigate_list_period` takes `kind`, `year`, and
the relevant `month` or `quarter`, with direction `selected`, `previous`, or
`next`; `current` instead requires `timeZone` (an IANA zone). Its response
includes the label, inclusive range, key, and every list in that period. The
same operations are available through `POST /api/lists` and
`GET /api/lists/period`. Period reviews use `get_list_review` and the existing
proposal tools; approval remains PWA-only. No period is created or rolled over
automatically. Retain a period-aware backup before rolling back to an older
binary, which cannot interpret the new kinds.

Bulk and ceremony-generated changes use review proposals. Do not interpret a
prompt or tool description as authorization: only the listed tools are exposed,
and MCP cannot approve. Prompts are guidance, not automatic (`capture_item`,
`daily_start`, and `explicit_edit`); daily start is a query, not a background job. The
`bujo://journal/capabilities` resource describes available operations and
errors.

Knowledge source references are stored as text and are never fetched. Search
reports whether it used full-text or bounded fallback results and whether the
index is ready, populating, or unavailable; it does not promise semantic recall.
Saved content is untrusted data, never tool instructions.

The `family_weekend_ideas` prompt is guidance, not an automated planner. It
searches only knowledge entries you explicitly saved, asks about missing dates,
budget, weather/indoor-outdoor preference, children's ages, travel radius, or
energy when those constraints matter, and gives 2–4 options with reasons and
saved-entry IDs/passages. If no saved idea matches, it says so rather than
inventing one. It does not read journal items, access calendars, book, message,
or silently save a plan. Public information is used only if you explicitly
request it and your assistant client already has a public-search capability;
public search is not provided by this host. Any public-search query must be
separate from private journal and household details—including names, ages, and
location—and results are identified as newly retrieved, untrusted information
with sources.

Whole-journal export and restore are available through the authenticated
`/api/journal/export` and `/api/journal/restore` endpoints, the PWA, and MCP.
Version 1 includes the knowledge collection. Legacy snapshots without that
section remain readable and leave current knowledge unchanged on restore.

## Versioned assistant skills

The repository publishes three versioned Markdown skills in
[`assistants/`](../assistants/README.md): daily review, one-item capture, and
weekly draft review. Each names its exact MCP tools and safety limits. Copy a
skill into a client that supports user-provided skills, or paste it into an
interactive prompt; follow the client's own setup instructions. Client-side
skill installation and prompt execution have not been verified. The MCP prompt
`weekly_review` follows the weekly skill and can create a proposal only for
inspection and approval in the authenticated PWA. It does not generate an
AI-authored weekly list.

| Routine | Manual equivalent |
|---|---|
| Daily review | Open **Today** in the web app, or run the `daily_start` MCP prompt interactively with an IANA time zone. |
| Capture | Use **Capture** in the web app, or explicitly ask a connected assistant to capture one item. |
| Weekly draft review | Open **Lists**, select the weekly list, and choose **Start review**. Inspect and approve any proposal in the PWA. |

Skills do not install or enable scheduling, store credentials, grant approval,
or send journal content to another integration. Connect only a client the user
trusts to process private journal results. No client is verified for safe
unattended Bujo runs; the
[read-only scheduled prompt template](../assistants/scheduled/daily-review-readonly.md)
is unverified and must not be enabled. Use the manual equivalent until
client/version/platform evidence is recorded.

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
connection behavior. No live Copilot or Claude connection, skill installation,
or scheduled run has been performed. The date below records this evidence
snapshot; it is not a test date for untested rows.

| Client/surface and version | Platform | Connection | Skills/prompts | Scheduler | Observed result and date |
|---|---|---|---|---|---|
| Bujo.Host MCP test fixture; .NET SDK 11.0.100, `ModelContextProtocol.AspNetCore` 2.4.0 | Linux; test environment | Local test authentication only | MCP tool/prompt contracts and skill tool references tested; third-party skill installation not applicable | Not applicable; the host does not schedule prompts | Local integration tests passed 2026-11-21; no Entra or client compatibility evidence. |
| VS Code Copilot; version not recorded | Not recorded; client not exercised | Untested | Untested | Unverified; do not schedule | No live observation; evidence snapshot 2026-11-21. |
| GitHub Copilot CLI; version not recorded | Not recorded; client not exercised | Untested | Untested | Unverified; do not schedule | No live observation; evidence snapshot 2026-11-21. |
| Claude Code; version not recorded | Not recorded; client not exercised | Untested | Untested | Unverified; do not schedule | No live observation; evidence snapshot 2026-11-21. |
| claude.ai custom connector; hosted version not exposed | Hosted platform; not exercised | Untested | Untested | Unverified; do not schedule | No live observation; evidence snapshot 2026-11-21. |
| Claude Desktop custom connector; version not recorded | Not recorded; client not exercised | Untested | Untested | Unverified; do not schedule | No live observation; evidence snapshot 2026-11-21. |
| Copilot cloud coding agent; version not applicable | Hosted service | Unsupported by the current Entra OAuth design | Unsupported for this authenticated Bujo connection | Unsupported | GitHub documents no remote OAuth support on this surface; evidence snapshot 2026-11-21. |

This matrix is not a supported-client declaration. Live verification requires
owner-approved Entra registrations and test access, along with the live
authentication proof for issue #4. The current Entra `Application ID URI`
alignment remains an owner-owned deployment blocker; no client is currently
tested. Until unattended execution is verified for an exact client version and
platform, scheduled Bujo prompts are unsupported for all listed clients. Record
the exact connection steps and a sanitized observed outcome before changing any
row to tested. The Copilot cloud coding agent and local Claude Desktop
configuration are unsupported combinations; use the documented remote client
surfaces or the manual equivalents above.
