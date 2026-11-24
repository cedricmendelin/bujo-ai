# Squad Team

> Bujo-AI: mindful capture, intentional planning, and trusted follow-through.

## Coordinator

| Name | Role | Notes |
|------|------|-------|
| Squad | Coordinator | Routes work, enforces handoffs and reviewer gates. |

## Members

| Name | Role | Charter | Status |
|------|------|---------|--------|
| Architect | Lead / Architect | `.squad/agents/architect/charter.md` | ✅ Active |
| Builder | Full-Stack Developer | `.squad/agents/builder/charter.md` | ✅ Active |
| AI | AI / ML Engineer | `.squad/agents/ai/charter.md` | ✅ Active |
| Verifier | Test Engineer | `.squad/agents/verifier/charter.md` | ✅ Active |
| DevOps | DevOps Engineer | `.squad/agents/devops/charter.md` | ✅ Active |
| Scribe | Decision and history keeper | `.squad/agents/scribe/charter.md` | On request |
| Ralph | Governed work monitor | `.squad/agents/ralph/charter.md` | On request / GitHub events |
| Rai | RAI Reviewer | `.squad/agents/rai/charter.md` | 🛡️ RAI |
| Fact Checker | Devil's Advocate & Verification Agent | `.squad/agents/fact-checker/charter.md` | 🔍 Verifier |
| Security | Security Engineer | `.squad/agents/security/charter.md` | ✅ Active |

## Human Team Members

- **@cedricmendelin** — Product/architecture decision owner and escalation target.
  Approves initial plans and explicit scope amendments; not a routine code-review gate.
  GitHub is the decision record. Personal Teams delivery requires separate verified setup.

## Coding Agent

- **@copilot** — Asynchronous GitHub coding agent, using the approved plan owner's
  named specialist persona. The existing plan assignment workflow is its sole
  assignment authority; an ownership or priority label is not execution permission.
- **Good fit:** approved, testable fixes, features, documentation and refactoring.
- **Needs review:** domain/contracts, infrastructure and application trust boundaries.
- **Not suitable without a human decision:** ambiguous scope, unapproved product,
  privacy/security, cost or dependency changes, secrets and repository settings.

## Project Context

- **Project:** Bujo-AI
- **Created:** 2026-11-14
- **Concept:** [Product concept](../docs/concept.md)
- **Stack direction:** Blazor PWA, ASP.NET Core API/MCP, Azure SQL, Azure Container Apps (Switzerland North, single prod environment, Terraform with Azure Verified Modules, GitHub OIDC CD, budget under 50 USD/month); Microsoft Foundry later. Runtime and render mode remain open.
- **Phase:** Governed delivery. Foundation (team, design, docs, backlog) done; issues flow through owner-approved plans.
- **Operating mode:** Governed GitHub flow (`docs/workflow.md`): owner `/triage` or `/plan`, then `plan:approved` starts the named cloud specialist. Routine choices are autonomous. Explicit owner-approved scope amendments extend the baseline without a mandatory new plan version. A pending or invalid request blocks merge. `needs:human` is an independent pause; neither it nor `handoff:local` cancels native Watch and fix.
- **PR merge authority:** GitHub protected squash auto-merge only. Required checks are `ci` and `Squad Merge Readiness`; Verifier and relevant specialists feed the latter. The controller enrolls eligible PRs in native Watch and fix, and reports enrollment failures. During local takeover, disable native repair first and leave app Agent merge's Merge action off.
- **Roles:** Architect also owns product discovery and implementation plans; Builder owns accessible UI and application scheduling; AI owns assistant interoperability; Verifier provides independent acceptance review; Security reviews threats, auth, privacy, and supply chain; DevOps owns IaC, CI/CD, and cost.
- **User documentation:** Architect is the single accountable owner for app usage, MCP usage, and product concept/why documentation. Each feature's delivery owner updates affected guides in the same PR; Verifier checks their accuracy and coverage or accepts an explicit, justified N/A for each unaffected surface. See `docs/workflow.md` → *User documentation with every feature*.
- **Knowledge:** Application-owned explicit notes/concepts/quotes in Azure SQL, full-text search and tags first; in-app AI Q&A later. This is product data, not Squad's development memory.
