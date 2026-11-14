# Bujo-AI

Organize your thoughts. Plan your tasks. Live mindfully.

An AI-assisted bullet journal for capturing tasks and thoughts, intentional
planning, and reliable follow-through through a web app or your preferred AI
assistant.

**Status:** Local Squad configured; initial product decisions recorded. No
application, cloud resources, or GitHub workflows have been created.

## Product and design

The [product concept](docs/concept.md) preserves the proposed capabilities,
examples, agreed first-release scope, technology direction, and unresolved
questions.

The first release is a single-user, Entra ID-protected, online-first journal:
capture through PWA or MCP, Future Log/custom/weekly lists, list-local migration
history, optional planned periods, and a daily-start review. A
[knowledge library](docs/knowledge.md) stores notes/concepts/quotes with full-text
search and tags in the same Azure SQL database. No separate deadline or reminder
timestamps; ceremonies resurface intentions. AI-drafted planning follows later.
Knowledge synchronization into an AI search service and grounded in-app RAG/Q&A
are **release 2**; original Azure SQL entries remain authoritative.

We are proceeding in order: initialize Squad, challenge the idea together,
document decisions, create a GitHub issues backlog, then set up workflows.

The [initial backlog](docs/backlog.md) separates the first usable release,
AI-assisted planning, and the longer-term vision, with explicit prerequisites.

## Working with Squad

The [team roster](.squad/team.md) and [routing rules](.squad/routing.md) define
ownership. [Decisions](.squad/decisions.md) records agreed outcomes.

Initialized using Squad CLI `2.0.0-preview.4`:

```powershell
squad init --no-workflows
squad health
squad doctor
copilot --agent squad
```

Node.js 22.5 or later is required by this CLI version. If Squad is not installed,
use `npx --yes @bradygaster/squad-cli@2.0.0-preview.4` in place of `squad`.
Initialization registers a machine-local Squad MCP bridge in `.mcp.json`;
that file is ignored and must not be committed. A fresh Copilot invocation may
be needed to load it.

Ralph runs only on request. Labels do not trigger Squad work locally. Do not run
`squad upgrade` blindly: it can overwrite customized coordinator files and
introduce workflows.

## Delivery workflow

Issues are implemented through the [governed delivery workflow](docs/workflow.md):
`/plan` → owner approves with `plan:approved` → Copilot cloud agent with the
owning Squad custom agent → Squad Review (Architect, Security, Verifier, DevOps)
plus CI → squash auto-merge → deploy from `main`. Infrastructure is Terraform with
Azure Verified Modules in Switzerland North, deployed via GitHub OIDC, budgeted
under 50 USD/month.

Installation reference:
[Squad installation and design choices](https://github.com/cedricmendelin/Agentic-Design-Patterns-MAF/blob/main/docs/squad-installation.md).
