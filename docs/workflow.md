# Governed delivery workflow

Every implementation issue goes through an owner-approved plan, Copilot
implementation with the owning Squad member's custom agent, and automated
specialist review. The owner reviews plans, not code.

```text
issue ──/plan──▶ Architect plan comment (v1, owner: builder|ai|devops|architect)
   ▲   /plan revise <feedback> · /plan route <member>  (removes plan:approved)
   │
   └─ owner applies plan:approved ──▶ Copilot cloud agent (custom agent = owner)
                                         │ opens PR, requests owner review
                                         ▼
          CI + Squad Review / Architect · Security · Verifier · DevOps
                                         │
                      Squad Review Gate (workflow_run aggregator)
           all success ──▶ ready + squash auto-merge ──▶ main ──▶ deploy
           any failure ──▶ @copilot fix comment (max 3 rounds) ──▶ needs:human
```

## Commands and labels

| Action | How | Effect |
|--------|-----|--------|
| Draft a plan | Comment `/plan` on the issue | Architect posts `## 📋 Implementation plan vN · owner: <member>`, labels `plan:proposed` + `squad:<member>` |
| Revise | `/plan revise <feedback>` | New version; `plan:approved` removed; older plan comments hidden |
| Re-route | `/plan route builder\|ai\|devops\|architect` | Same plan, new owner and version |
| Approve | Owner applies `plan:approved` | Copilot assigned with the plan embedded and custom agent `<member>` |
| Re-review | Copilot requests owner review, or Actions → *Squad Review X* → Run with `pr_number` | Reviewer check re-published for the PR head |
| Escalation | `needs:human` | Reviewer crash or 3 failed fix rounds; owner decides |

Only the repository owner can approve (`squad-plan-approved.yml` removes the
label otherwise). Approval binds to the newest plan comment; any revision clears it.
You can also edit the plan comment directly before approving.

## Workflows

| File | Kind | Purpose |
|------|------|---------|
| `squad-plan.md` | gh-aw (Copilot engine) | `/plan` command; Architect plan comment and labels |
| `squad-plan-approved.yml` | Actions | Owner check, assigns `copilot-swe-agent[bot]` with `custom_agent` |
| `squad-review-{architect,security,verifier,devops}.md` | gh-aw | Independent review, inline comments, required check `Squad Review / <Role>` |
| `shared/squad-review.md` | gh-aw import | Common review protocol |
| `squad-review-gate.yml` | Actions (`workflow_run`) | Aggregates checks, auto-merge or fix loop |
| `ci.yml` | Actions | Required check `ci`: .NET build/test, Terraform fmt/validate |
| `copilot-setup-steps.yml` | Actions | .NET 10 and Terraform in the Copilot cloud agent environment |

Squad agents live in `.github/agents/<member>.agent.md`; the same files drive
Copilot implementation and gh-aw reviews. Recompile after editing a `.md`
workflow or an imported agent: `gh aw compile --strict` (gh-aw v1.2.2), and
commit the `.lock.yml` files.

Reviewers never edit, approve, or merge; they publish a check and COMMENT review.
Blocking findings must be concrete and within the reviewer's scope.

## One-time setup (after this workflow is on `main`)

1. **Secrets** (fine-grained PATs scoped to this repository; never commit them):
   - `COPILOT_GITHUB_TOKEN`: Account permission *Copilot Requests* (gh-aw inference).
   - `GH_AW_AGENT_TOKEN`: Repository permissions Metadata read; Actions, Contents,
     Issues, Pull requests read/write (Copilot assignment, `@copilot` fix
     comments, auto-merge).
   - `gh secret set COPILOT_GITHUB_TOKEN` and `gh secret set GH_AW_AGENT_TOKEN`.
2. **Labels**: `plan:proposed`, `plan:approved`, `squad:architect`,
   `squad:builder`, `squad:ai`, `squad:devops`, `needs:human`.
3. **Repository settings**: allow auto-merge, squash merge, delete head branches.
4. **Ruleset on `main`**: require a pull request and status checks `ci`,
   `Squad Review / Architect`, `Squad Review / Security`,
   `Squad Review / Verifier`, `Squad Review / DevOps`; block force pushes;
   repository admin bypass for emergencies only.
5. **Copilot cloud agent**: enable for the repository; allow Actions workflows
   on Copilot PRs to run without manual approval (otherwise approve each run);
   firewall allowlist `registry.terraform.io`, `releases.hashicorp.com`,
   `api.nuget.org` as needed.
6. **Azure (with the IaC issue)**: OIDC federated credentials and the variables
   documented by that issue's plan. No Azure secrets are stored in GitHub.

## Limits and assumptions

- Agentic reviews cost Copilot premium requests per PR round; the 3-round cap
  bounds it.
- Copilot requesting the owner's review after each session is how re-review is
  triggered; if a round stalls, dispatch the reviewers manually.
- Required checks guarantee review of the merged head; a push after the gate
  enabled auto-merge waits for fresh checks.
- Deployment from `main` is part of the IaC/CD issue, not this workflow.
