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
| Ralph | Backlog reviewer | `.squad/agents/ralph/charter.md` | On request only |
| Rai | RAI Reviewer | `.squad/agents/rai/charter.md` | 🛡️ RAI |
| Fact Checker | Devil's Advocate & Verification Agent | `.squad/agents/fact-checker/charter.md` | 🔍 Verifier |
| Security | Security Engineer | `.squad/agents/security/charter.md` | ✅ Active |

## Project Context

- **Project:** Bujo-AI
- **Created:** 2026-11-14
- **Concept:** [Product concept](../docs/concept.md)
- **Stack direction:** Blazor PWA, ASP.NET Core API/MCP, Azure SQL, Azure Container Apps (Switzerland North, single prod environment, Terraform with Azure Verified Modules, GitHub OIDC CD, budget under 50 USD/month); Microsoft Foundry later. Runtime and render mode remain open.
- **Phase:** Governed delivery. Foundation (team, design, docs, backlog) done; issues flow through owner-approved plans.
- **Operating mode:** Governed GitHub flow (`docs/workflow.md`, README → *How we work*): `/plan` → owner applies `plan:approved` → Copilot cloud agent with the owning custom agent → successful-session readiness, initial CI/review dispatch, then native GitHub.com Watch and fix for failed Actions/base conflicts; local app sessions with `handoff:local` are optional manual takeovers. The shepherd forwards bounded check and eligible submitted review feedback to `@copilot`, handles bounded recovery/metadata/escalation, and does not repair conflicts. `needs:human` stops automation; it does not stop native Watch and fix. No implementation without an approved (or owner-waived) plan.
- **PR merge authority:** GitHub auto-merge performs squash merges under protections. The trusted gate only validates/arms auto-merge and publishes `Squad Merge Readiness`; it never directly merges. `main` requires `ci`, `Squad Review / Architect`, `Squad Review / Security`, `Squad Review / Verifier`, `Squad Review / DevOps`, `Squad Plan Binding`, and `Squad Merge Readiness`. The owner separately enables repository Copilot automations and Watch and fix for each applicable PR or configures the account default (which covers user-created PRs, not assumed Copilot-created PRs). For local takeover, Agent merge's Merge action stays off and the owner disables native Watch and fix. Human in the loop: @cedricmendelin.
- **Roles:** Architect also owns product discovery and implementation plans; Builder owns accessible UI and application scheduling; AI owns assistant interoperability; Verifier provides independent acceptance review; Security reviews threats, auth, privacy, and supply chain; DevOps owns IaC, CI/CD, and cost.
- **Knowledge:** Application-owned explicit notes/concepts/quotes in Azure SQL, full-text search and tags first; in-app AI Q&A later. This is product data, not Squad's development memory.
