# Work Routing

## Work Type -> Agent

| Work Type | Primary | Secondary |
|-----------|---------|-----------|
| Product discovery, MVP scope, goals, domain model, architecture, decisions, implementation plans | Architect | Verifier |
| Blazor PWA, accessibility, API, persistence, application scheduling | Builder | Verifier |
| Foundry, AG-UI, MCP tools/prompts/resources, skills, assistant interoperability | AI | Builder |
| Acceptance criteria, regression tests, independent acceptance review | Verifier | Architect |
| Threat modeling, auth, privacy, secrets, supply chain, security review | Security | Verifier |
| Terraform/AVM, Container Apps, CI/CD, workflows, observability, cost | DevOps | Security |

## Support

Scribe records agreed decisions and useful history. Ralph reviews backlog status
only when explicitly asked. Rai advises on responsible AI and sensitive data.
Fact Checker verifies external claims and challenges unsupported assumptions.

## Governed delivery (GitHub)

Every implementation issue follows `docs/workflow.md`:

1. `/plan` on the issue: Architect drafts a versioned implementation plan and
   proposes an owner (`squad:builder`, `squad:ai`, `squad:devops`,
   `squad:architect`). `/plan revise <feedback>` and `/plan route <member>` adjust it.
2. The repository owner applies `plan:approved`. Nothing is implemented before
   that label; the approval workflow assigns Copilot with the owner's custom agent.
3. Copilot implements only the approved plan and opens a PR.
4. Squad Review (Architect, Security, Verifier, DevOps) and CI produce required
   checks. Blocking findings go back to Copilot (max 3 rounds, then `needs:human`).
5. When every check passes, the gate enables squash auto-merge; merge to `main`
   deploys.

## Dispatch and approval rules

- The coordinator handles simple tasks directly. Invoke specialists only for
  bounded work that benefits from their context; no speculative fan-out.
- Labels describe ownership; only `plan:approved` applied by the repository
  owner authorizes implementation of the plan it follows.
- No watchers, polling loops, scheduled retrospectives, automatic issue creation,
  or implementation without an approved plan.
- Capture unresolved choices as questions, not decisions. User approval resolves
  product scope and significant architectural trade-offs.
- Keep independent review separate from implementation: a reviewer never fixes
  the PR it reviews.
- Locally: do not provision resources, deploy, push, or merge without explicit
  authorization. In GitHub, deployment happens only through merged, reviewed PRs.
- Application reminders and ceremonies are product features, not permission to
  automate this repository.
