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
4. For cloud PRs, the shepherd requires successful-session readiness, marks
   finished Copilot PRs ready, and dispatches initial CI and Squad Review
   (Architect, Security, Verifier, DevOps). The owner enables GitHub.com Copilot
   automations and Watch and fix per applicable PR, or configures its account
   default; the default covers PRs the user creates, not assumed
   Copilot-created PRs. Native Watch and fix owns failed Actions and base-branch
   conflicts, not review comments. The shepherd forwards eligible check
   findings and submitted review feedback to `@copilot` in bounded requests,
   handles explicit metadata proposals and bounded interrupted-session
   recovery, and escalates. Review feedback is limited to unresolved,
   non-outdated trusted review threads and change requests, polled at
   `Squad Plan Binding` completion (about every ten minutes) and hourly; do not
   act on arbitrary top-level comments. Local PRs receive fresh reviews on
   every push.
5. The trusted gate validates the live plan and review results and publishes
   aggregate `Squad Merge Readiness`; it only arms GitHub squash auto-merge and
   never directly merges. `main` protections must require `ci`, all four
   `Squad Review / <Role>` checks, `Squad Plan Binding`, and
   `Squad Merge Readiness`. GitHub auto-merge executes the squash merge under
   those protections; owner ruleset setup is separate.
6. `needs:human` stops automation and is never silently cleared just because
   checks are green. When pausing, the owner must also stop native Watch and fix;
   the label does not control GitHub's feature. For an optional local takeover,
   use `handoff:local`, keep Agent merge's Merge action off, and have the owner
   disable native Watch and fix to prevent concurrent fixes.
7. Human in the loop: @cedricmendelin. Stuck loops get `needs:human` plus a
   mention on the PR; workflow failures and one-time setup become `needs:human`
   issues assigned to him; implementers write `## 🙋 Needs human` in the PR.
   The metadata bridge retains body/head/hash guards; optional title updates
   require `title` and `baseTitleSha256` together. Owner-waived description
   scope changes remain owner-only.

## Dispatch and approval rules

- The coordinator handles simple tasks directly. Invoke specialists only for
  bounded work that benefits from their context; no speculative fan-out.
- Labels describe ownership; only `plan:approved` applied by the repository
  owner authorizes implementation of the plan it follows.
- No local watchers, polling loops, scheduled retrospectives, or implementation
  without an approved plan. GitHub delivery automation is governed by
  `docs/workflow.md` and the approved PR-finalization decision: native Watch and
  fix is owner-enabled; the cloud shepherd uses event-driven triggers and an
  hourly fallback for its bounded responsibilities; `needs:human` stops
  automation.
- Capture unresolved choices as questions, not decisions. User approval resolves
  product scope and significant architectural trade-offs.
- Keep independent review separate from implementation: a reviewer never fixes
  the PR it reviews.
- Locally: do not provision resources, deploy, push, or merge without explicit
  authorization. In GitHub, deployment happens only through merged, reviewed PRs.
- Application reminders and ceremonies are product features, not permission to
  automate this repository.
