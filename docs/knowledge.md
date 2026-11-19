# Personal knowledge library: initial investigation

## Requested capability

Persist deliberately captured concepts, quotes, notes, and anything worth
remembering. Let the user ask questions about that knowledge through the app
and/or an assistant. Basic capture/search through PWA and MCP is agreed for the
first release; in-app AI Q&A follows the Foundry milestone. Azure SQL is selected
for application-owned entries, with full-text search and tags initially.
Detailed entry fields/lifecycle and operational configuration still need design.

**Release 2:** synchronize these entries into an AI search service and support
RAG over the derived index. This is explicitly not part of release 1. The search
provider/configuration remains to be selected; Azure AI Search is a candidate.

Distinguish user-curated knowledge from an assistant automatically inferring
preferences or facts from conversation. No personal knowledge is being captured
into the coding assistant's own memory by this investigation.

## Agreed direction and proposed details

- Add a small application-owned Knowledge Entry model to the existing modular
  application and Azure SQL database, not a separate memory service.
- Preserve original content, stable ID, optional title/source reference, tags,
  and creation/update metadata. Concepts/quotes/notes need not require the user
  to classify every capture.
- Do not impose task completion, movement, or planned-period semantics on
  knowledge. Optional links to tasks/lists can be considered separately.
- Save and retrieve without a model call. Use Azure SQL full-text search plus
  tags initially. Define and test search languages, index readiness and actual
  retrieval quality; full-text search is not guaranteed semantic recall.
- Expose authenticated capture, retrieval, and search through the same PWA/API/MCP
  boundaries as the journal. Existing assistants can synthesize retrieved entries;
  that does not require Bujo-AI to host its own answer-generation model.
- In-app grounded Q&A can follow the Foundry integration milestone. Answers
  should reference actual saved entry IDs/passages and explicitly distinguish
  missing evidence from general model knowledge.
- In release 2, use existing search/indexing/embedding components for the approved
  AI search and RAG approach. Indexes and summaries are rebuildable derived data;
  they must never replace the original saved text.
- Apply edits/deletions to retrieval and derived indexes consistently. Define
  retention, backup/export, and deletion behavior before promising guarantees.
- Saved content is untrusted data, never privileged instructions. No automatic
  ingestion of chats, mail, calendar, URLs or documents is implied by saving a
  note or source reference.

## Existing options checked

Primary documentation was read on 2026-11-14. These are suitability observations,
not performance/security certifications or completed integrations.

| Option | Documented capabilities | Fit for this requirement |
|--------|-------------------------|--------------------------|
| [Mem0](https://github.com/mem0ai/mem0) | Assistant/user/session memory, extracted memories, retrieval, library/self-hosted/managed options; model dependency documented | Worth evaluating if inferred assistant memory becomes a requirement. Not necessary just to preserve explicit notes and exact quotes. |
| [Graphiti](https://github.com/getzep/graphiti) | Python temporal context-graph framework with provenance, hybrid retrieval, graph database and LLM/embedder requirements | Useful for changing relationships and temporal reasoning; introduces additional service/runtime/operations complexity beyond a first personal knowledge library. |
| [MCP reference memory server](https://github.com/modelcontextprotocol/servers/tree/main/src/memory) | Entities, observations, relations and local JSONL graph persistence exposed via MCP | Useful reference/prototype, not a drop-in external-database-backed ASP.NET service with our Entra access controls and shared application contracts. |

No evidence here establishes native .NET, Entra ID, remote-client or Foundry
compatibility for these products. Validate those aspects before adopting one.
Open-source and managed-service capabilities must not be assumed identical.

Azure SQL's [full-text search documentation](https://learn.microsoft.com/en-us/sql/relational-databases/search/full-text-search?view=azuresqldb-current)
confirms support for language-aware word/phrase queries. It does not establish
that arbitrary paraphrases or concept-level questions will retrieve every
relevant note. Search configuration, local SQL Server parity and index-population
behavior need validation in the implementation.

## Release 1 entry contract and lifecycle

The approved release 1 design stores user-authored knowledge in the journal's
existing database and identity boundary. It does not create task, list, or
planned-period semantics for knowledge.

Each entry has:

- An immutable, stable GUID `Id`.
- Required `Content`, stored and returned verbatim, including quotes, Unicode,
  newlines, and markup. It is never replaced with a summary or extracted claim.
- Optional `Title` and `SourceReference`. A source reference is data only: the
  application never fetches it.
- Zero or more tags using the journal's existing tag normalization rules.
- `CreatedAt`, `UpdatedAt`, nullable `ArchivedAt`, and a concurrency `Version`.

An edit updates content, title, source, and tags in place, preserves the ID and
creation time, increments the version, and requires the expected version.
Archive is reversible; archived entries remain readable by ID and can be
included explicitly in search, but are omitted by default. Delete is an
explicit, irreversible hard delete; a backup restore is the only recovery
path. Release 1 does not retain content history.

Search accepts optional text, zero or more tag filters (all requested tags must
match), and an include-archived flag. Results use full-text rank followed by
`UpdatedAt` descending and ID as a deterministic tie-breaker; tag-only results
use `UpdatedAt` descending and ID. Pagination uses an opaque keyset cursor and
a bounded page size. Invalid or empty searches are explicit validation errors.
The query builder bounds and quotes user terms and passes the resulting query
as a SQL parameter; input is never interpolated into a full-text expression.

English (LCID 1033) is the initial full-text language. It is a configurable
database index choice, not a claim that other languages are supported or that
full-text search provides semantic recall. Full-text readiness is reported as
`Ready`, `Populating`, or `Unavailable` alongside the `FullText` or `Fallback`
search mode. While the index is not ready, a bounded, parameterized substring
search over recent entries keeps new entries retrievable and discloses that
bounded mode. Fallback results do not claim to cover entries outside that
window. Index errors are reported as degraded/unavailable, never as an
apparently successful empty result.

Search results carry the stable entry ID, original content passage, title,
source reference, tags, and timestamps so an assistant can identify saved
evidence. Saved entries are untrusted user data, not tool instructions.

Knowledge export currently uses a versioned knowledge snapshot containing
original content, IDs, source, tags, archive state, timestamps, and concurrency
versions. The authenticated API exposes `GET /api/knowledge/export` and
`POST /api/knowledge/restore`; the PWA provides export and restore controls.
Restore validates the complete knowledge snapshot before writing, rejects
duplicate IDs/tags, and replaces the knowledge set atomically.
Snapshots are limited to 10,000 entries and 25 MB; export reports an explicit
size error rather than emitting a snapshot that restore cannot accept.

The repository did not have a journal-wide backup/export/restore contract when
this capability was implemented. The knowledge-only endpoints therefore do
not claim to back up or restore journal lists and items, nor do they yet make
knowledge a section of a whole-journal backup. Integrating this versioned
knowledge section with a journal-wide backup format remains required before
the full backup acceptance criterion is complete. Older full-journal snapshots
are not currently accepted by these knowledge-only endpoints.

The SQL Server migration creates a full-text catalog and English (LCID 1033)
full-text index when the database supports full-text search. The DDL is guarded
so unsupported local SQL Server installations remain usable with the explicit
fallback. A local SQL Server 2022 container verified migration and persistence;
its full-text service was unavailable, so readiness reported `Unavailable` and
the bounded fallback retrieved newly saved entries across repository restart.
That check does not verify full-text population or parity with Azure SQL. The
selected Azure SQL free-offer tier, region, LCID availability and population
latency remain unverified; no Azure result is claimed until it is observed and
recorded.

## Additional Agent Finder discovery

The narrower registry search returned these candidates. They have not been
independently evaluated, installed, connected, or selected. Scores measure search
relevance only, not trust or safety.

1. **memo** — MCP server (`application/mcp-server+json`), score **100**:
   https://api.mcp.github.com/v0.1/servers/io.github.jagoff%2Fmemo/versions/4.16.0
2. **GrayMatter** — MCP server (`application/mcp-server+json`), score **83**:
   https://api.mcp.github.com/v0.1/servers/io.github.ValkyrLabs%2Fgraymatter/versions/0.3.3
3. **Memxus** — MCP server (`application/mcp-server+json`), score **81**:
   https://api.mcp.github.com/v0.1/servers/com.memxus%2Fmemxus/versions/1.3.2

Registry discovery is not a reason to add another service. Inspect implementation,
license, hosting, auth, privacy, portability, operational cost and retrieval
quality against concrete examples before choosing any integration.

## Deferred and verification decisions

- Generated answers, embeddings, vector search, automatic ingestion, temporal
  graphs, and source-page fetching are outside release 1.
- Which release-two search service, synchronization guarantees, chunking,
  retrieval and embedding configuration fit the data and operating budget?
- Azure SQL free-offer full-text availability and pause/resume behavior must be
  verified against the actual owner-authorized database before release.
