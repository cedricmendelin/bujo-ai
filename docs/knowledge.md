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

## Open decisions

- Which entry fields and linking/edit/archive/delete behavior are needed initially?
- Which languages and retrieval examples must work?
- Which answers require strict saved-source grounding versus clearly labeled
  general knowledge? How should contradictory or outdated notes be presented?
- Which release-two search service, synchronization guarantees, chunking,
  retrieval and embedding configuration fit the data and operating budget?
- When, if ever, are automatic extraction, document ingestion or a temporal
  knowledge graph worth their additional complexity?
