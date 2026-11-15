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

Every implementation issue follows `docs/workflow.md` and README → *How we work*:

1. `/plan` on the issue: Architect drafts a versioned implementation plan and
   proposes an owner (`squad:builder`, `squad:ai`, `squad:devops`,
   `squad:architect`). `/plan revise <feedback>` and `/plan route <member>` adjust it.
2. The repository owner applies `plan:approved`. Nothing is implemented before
   that label. Without `handoff:local`, the approval workflow assigns the Copilot
   cloud agent with the owner's custom agent; with `handoff:local`, the owner
   runs the same custom agent in a GitHub Copilot app session.
3. The implementer implements only the approved plan (or, for owner-waived work
   with `plan:waived`, the PR description's scope) and opens a PR.
4. Squad PR Shepherd marks finished Copilot PRs ready and starts CI and Squad
   Review (Architect, Security, Verifier, DevOps); local PRs are reviewed on
   every push. Blocking findings go back to `@copilot` or to Agent merge in the
   app (max 3 rounds).
5. When every check passes, the gate enables squash auto-merge; merge to `main`
   deploys.
6. Human in the loop: @cedricmendelin. Stuck loops get `needs:human` plus a
   mention on the PR; workflow failures and one-time setup become `needs:human`
   issues assigned to him; implementers write `## 🙋 Needs human` in the PR.

## Dispatch and approval rules

- The coordinator handles simple tasks directly. Invoke specialists only for
  bounded work that benefits from their context; no speculative fan-out.
- Labels describe ownership; only `plan:approved` applied by the repository
  owner authorizes implementation of the plan it follows.
- No watchers, polling loops, scheduled retrospectives, or implementation
  without an approved plan. Exceptions are the GitHub delivery automation in
  `docs/workflow.md`: Squad PR Shepherd (event-driven with an hourly fallback)
  and `needs:human` escalation issues.
- Capture unresolved choices as questions, not decisions. User approval resolves
  product scope and significant architectural trade-offs.
- Keep independent review separate from implementation: a reviewer never fixes
  the PR it reviews.
- Locally: do not provision resources, deploy, push, or merge without explicit
  authorization. In GitHub, deployment happens only through merged, reviewed PRs.
- Application reminders and ceremonies are product features, not permission to
  automate this repository.
