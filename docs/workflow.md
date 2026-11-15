# Governed delivery workflow

Every implementation issue goes through an owner-approved plan, Copilot
implementation with the owning Squad member's custom agent, and automated
specialist review. The owner reviews plans, not code.

```text
issue ──/plan──▶ Architect plan comment (v1, owner: builder|ai|devops|architect)
   ▲   /plan revise <feedback> · /plan route <member>  (removes plan:approved)
   │
   └─ owner applies plan:approved
        ├─ cloud (default) ──▶ Copilot cloud agent (custom agent = owner) ─▶ draft PR
        │                        │ session ends (workflow_run "Copilot cloud agent")
        │                        ▼
        │                  Squad PR Shepherd: mark ready, dispatch CI + 4 reviews
        └─ handoff:local ──▶ GitHub Copilot app session ─▶ ready PR + Agent merge
                                         │
          CI + Squad Review / Architect · Security · Verifier · DevOps
                                         │
                      Squad Review Gate (workflow_run aggregator)
           all success ──▶ ready + squash auto-merge ──▶ main ──▶ deploy
           any failure ──▶ findings to @copilot (cloud) or Agent merge (local)
                           max 3 rounds ──▶ needs:human + @cedricmendelin
```

See [README → How we work](../README.md#how-we-work) for the day-to-day flow.

## Commands and labels

| Action | How | Effect |
|--------|-----|--------|
| Draft a plan | Comment `/plan` on the issue | Architect posts `## 📋 Implementation plan vN · owner: <member>`, labels `plan:proposed` + `squad:<member>` |
| Revise | `/plan revise <feedback>` | New version; `plan:approved` removed; older plan comments hidden |
| Re-route | `/plan route builder\|ai\|devops\|architect` | Same plan, new owner and version |
| Approve | Owner applies `plan:approved` | Copilot assigned with the plan embedded and custom agent `<member>` |
| Approve for local work | Owner applies `handoff:local`, then `plan:approved` | Approval recorded; no cloud assignment. Implement in the GitHub Copilot app |
| Hand over a cloud PR | Add `handoff:local` to a `copilot/*` PR | Shepherd and `@copilot` fix requests stop; reviews run on every push; use Agent merge |
| Waive the plan | Owner applies `plan:waived` to a PR | For work without an issue plan; reviewers use the PR description as scope; reviews start |
| Re-review | Automatic after each Copilot session (shepherd) or push (local PRs); manually: Actions → *Squad Review X* → Run with `pr_number` | Reviewer check re-published for the PR head |
| Escalation | `needs:human` + `@cedricmendelin` comment | 3 failed fix rounds, stalled Copilot session, unresolved conflicts, reviewer crash/timeout. Remove the label to resume automation |
| Automation failure | Issue `Squad automation failing: <workflow>` | Shepherd or gate itself failed (for example an expired token) |

Only the repository owner can approve or waive (`squad-plan-approved.yml`
removes the label otherwise). Approval binds to the newest plan comment; any
revision clears it. You can also edit the plan comment directly before approving.

## Workflows

| File | Kind | Purpose |
|------|------|---------|
| `squad-plan.md` | gh-aw (Copilot engine) | `/plan` command; Architect plan comment and labels |
| `squad-plan-approved.yml` | Actions | Owner check, assigns `copilot-swe-agent[bot]` with `custom_agent` (skipped for `handoff:local`); guards `plan:waived` |
| `squad-review-{architect,security,verifier,devops}.md` | gh-aw | Independent review, inline comments, required check `Squad Review / <Role>`. Copilot branches are reviewed only via dispatch from the shepherd; other PRs on open/push/ready |
| `shared/squad-review.md` | gh-aw import | Common review protocol |
| `squad-pr-shepherd.yml` | Actions (`workflow_run` on *Copilot cloud agent*, push to `main`, hourly fallback) | Marks finished Copilot PRs ready, dispatches CI and reviews per head, asks Copilot to resolve conflicts, escalates stalls |
| `squad-review-gate.yml` | Actions (`workflow_run`) | Aggregates checks, auto-merge or fix loop |
| `ci.yml` | Actions | Required check `ci`: .NET build/test, Terraform fmt/validate |
| `copilot-setup-steps.yml` | Actions | .NET 10 and Terraform in the Copilot cloud agent environment |

Squad agents live in `.github/agents/<member>.agent.md`; the same files drive
Copilot implementation and gh-aw reviews. Recompile after editing a `.md`
workflow or an imported agent: `gh aw compile --strict` (gh-aw v1.2.2), and
commit the `.lock.yml` files.

Reviewers never edit, approve, or merge; they publish a check and COMMENT review.
Blocking findings must be concrete and within the reviewer's scope.

The shepherd and gate read checks and runs with `GITHUB_TOKEN` (fine-grained
PATs cannot be granted the Checks permission) and write with `GH_AW_AGENT_TOKEN`
so that `@copilot` comments, workflow dispatches, and auto-merge act as the owner.
The Copilot cloud agent always opens draft PRs and does not request reviews, so
the shepherd is what starts the review loop.

## One-time setup (after this workflow is on `main`)

1. **Secrets**: two fine-grained PATs owned by the repository owner. See
   [Creating the tokens](#creating-the-tokens) below.
2. **Labels**: `plan:proposed`, `plan:approved`, `plan:waived`, `handoff:local`,
   `squad:architect`, `squad:builder`, `squad:ai`, `squad:devops`, `needs:human`.
3. **Repository settings**: allow auto-merge, squash merge, delete head branches.
4. **Ruleset on `main`**: require a pull request and status checks `ci`,
   `Squad Review / Architect`, `Squad Review / Security`,
   `Squad Review / Verifier`, `Squad Review / DevOps` (exact case); block force
   pushes; repository admin bypass for emergencies only.
5. **Copilot cloud agent**: enable for the repository. In **Settings → Copilot →
   Cloud agent**, turn off **Require approval for workflow runs**; otherwise every
   Copilot push leaves CI as *action required* (the shepherd still dispatches CI
   per head, so the loop works, but with noise). This toggle has no API.
   Firewall allowlist `registry.terraform.io`, `releases.hashicorp.com`,
   `api.nuget.org` as needed.
6. **Azure (with the IaC issue)**: OIDC federated credentials and the variables
   documented by that issue's plan. No Azure secrets are stored in GitHub.

## Creating the tokens

Both secrets are fine-grained personal access tokens (PATs) created by the
repository owner, who needs an active Copilot license. OAuth tokens (`gho_…`)
and GitHub App tokens are rejected. Never commit or paste a token anywhere
except the secret prompt.

| Secret | Used by | Purpose |
| --- | --- | --- |
| `COPILOT_GITHUB_TOKEN` | gh-aw workflows (`/plan`, Squad reviewers) | Copilot inference for the agentic workflows |
| `GH_AW_AGENT_TOKEN` | `squad-plan-approved.yml`, `squad-pr-shepherd.yml`, `squad-review-gate.yml` | Assign the Copilot cloud agent, post `@copilot` comments, mark ready, dispatch CI/reviews, enable auto-merge |

### 1. Create `COPILOT_GITHUB_TOKEN`

1. Open <https://github.com/settings/personal-access-tokens/new> (Settings →
   Developer settings → Personal access tokens → Fine-grained tokens →
   **Generate new token**).
2. **Token name**: `bujo-ai copilot inference`. **Expiration**: 90 days (set a
   rotation reminder).
3. **Resource owner**: your personal account (not an organization).
4. **Repository access**: *Public repositories* (no repository permissions are
   needed).
5. **Permissions → Account permissions → Copilot Requests**: *Read*. Leave all
   other permissions at *No access*.
6. Click **Generate token** and copy the value.

### 2. Create `GH_AW_AGENT_TOKEN`

1. Open <https://github.com/settings/personal-access-tokens/new> again.
2. **Token name**: `bujo-ai agent automation`. **Expiration**: 90 days.
3. **Resource owner**: your personal account.
4. **Repository access**: *Only select repositories* → `cedricmendelin/bujo-ai`.
5. **Permissions → Repository permissions**:
   - Metadata: *Read* (selected automatically)
   - Actions: *Read and write*
   - Contents: *Read and write*
   - Issues: *Read and write*
   - Pull requests: *Read and write*
6. Click **Generate token** and copy the value.

Actions taken with this token appear as the owner. That is intentional: Copilot
only acts on `@copilot` comments from users with write access, and comments
posted by `GITHUB_TOKEN` do not trigger workflows.

### 3. Add the secrets to the repository

With the GitHub CLI (it prompts for the value, so the token stays out of your
shell history):

```powershell
gh secret set COPILOT_GITHUB_TOKEN --repo cedricmendelin/bujo-ai
gh secret set GH_AW_AGENT_TOKEN --repo cedricmendelin/bujo-ai
gh secret list --repo cedricmendelin/bujo-ai
```

Or in the browser: repository **Settings → Secrets and variables → Actions →
New repository secret**. Enter the name exactly as shown, paste the value, and
click **Add secret**. Use repository secrets, not secrets in the `copilot`
environment (that environment is only for the cloud agent's runtime).

### 4. Verify and rotate

- Verify by commenting `/plan` on an issue: the *Squad Plan* run must pass its
  activation step. Then add `plan:approved`: the issue must be assigned to
  Copilot.
- Authentication failures appear in the run log with a remedy link. Check the
  resource owner, permissions, expiration, and Copilot license.
- To rotate, use **Regenerate token** on the token's settings page and run
  `gh secret set` again with the same name. No workflow changes are needed.

## Limits and assumptions

- Agentic reviews cost Copilot premium requests per PR round; the 3-round cap
  bounds it. Local PRs re-run all four reviews on every push, so push in
  meaningful increments.
- The shepherd reacts to the *Copilot cloud agent* run completing; its hourly
  schedule is a fallback (about 720 short Actions runs per month on this private
  repository) that also catches conflicts after merges and silent reviewers.
- Required checks guarantee review of the merged head; a push after the gate
  enabled auto-merge waits for fresh checks.
- The Copilot app's Agent merge works only for local sessions; cloud PRs rely
  on the shepherd and gate instead.
- Infrastructure deploys from `main`; see [infrastructure operations](infrastructure.md).
