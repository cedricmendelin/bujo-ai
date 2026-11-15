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

Use `/plan` to draft and `/plan revise <feedback>` to revise; `/squad plan` is
not a supported command. No label is needed to draft or revise. Only the
repository owner can approve or waive (`squad-plan-approved.yml` removes the
label otherwise). `plan:approved` is required after reviewing the newest plan
comment; approval binds to that version and every revision clears it. You can
also edit the plan comment directly before approving it.

## Full completion and partial delivery

Every PR declares exactly one whole-line `Completion: full` or
`Completion: partial`. A full PR may close its issue only when it provides
evidence for every acceptance criterion, including the implementation code and
relevant automated tests. The independent Verifier checks this evidence; the
declaration and gate parser are not semantic proof of completion.

A partial or specification-only fallback is only a milestone. Use a non-closing
reference such as `Relates to #N` or `Part of #N`, include a `Remaining work`
section naming unfinished work and its dependency, and remove all closing
keywords from the whole PR description, including generated suffixes. The issue
remains open until the final full-completion PR delivers every criterion.
Unmet completion criteria block closure and cannot be downgraded to advisory.
This also applies when resuming an issue reopened after a partial or premature
closure: inspect prior merged work and remaining criteria; if the plan changes,
use `/plan revise <feedback>`, review the new version, and re-apply
`plan:approved`.

The gate re-reads the current PR description whenever CI or a Squad Review check
completes. For same-repository cloud PRs, an owner/Copilot description edit also
triggers the shepherd using default-branch code; the hourly fallback detects
missed edits. Reviews are keyed by both head and description hash. The gate
waits for all four reviews of that revision, not old passing checks of the same
commit. Local (`handoff:local` or other branches) PRs retain push-based reviews;
re-run the reviewers manually after a description-only correction.

### Cloud PR-description handoff

The two PATs have distinct jobs: `COPILOT_GITHUB_TOKEN` permits Copilot
inference; `GH_AW_AGENT_TOKEN` permits trusted workflow GitHub operations.
Neither means the implementing cloud session has a PR-update tool. The session
for #38 exposed read-only GitHub MCP tools and a comment-reply tool; it did not
demonstrate a missing owner-token scope. Do not expose `GH_AW_AGENT_TOKEN` to the
cloud session or add write-enabled MCP tools just to repair descriptions.

An implementer first uses a native PR-update tool, if available, and reads back
the result. Otherwise it replies on the PR with an explicit proposal:

````text
<!-- squad-pr-metadata -->
```json
{
  "headSha": "<current 40-character head SHA>",
  "baseBodySha256": "<SHA-256 of the exact current description's UTF-8 bytes>",
  "body": "Completion: partial\nImplements plan v1 for issue #N.\n\n## Remaining work\nState unfinished scope and its dependency.\n\nRelates to #N"
}
```
````

Replace placeholders using the GitHub read tools. Encode the full replacement as
a JSON string, with no trailing prose. The replacement begins with a completion
declaration, retains the current approved issue/plan linkage, and remains subject
to independent completion review. Publishing a proposal is not applying it.

The shepherd accepts proposals only from the repository owner or the known
`Copilot`/`copilot-swe-agent[bot]` bot identities. It accepts only a description,
not titles, labels, draft changes or arbitrary API operations. The maximum
replacement is 60,000 UTF-8 bytes. It rejects stale head/body snapshots,
malformed JSON and invalid completion declarations. It resolves the current
approved issue plan and re-fetches state and approval immediately before the
REST write; GitHub does not provide an atomic body-hash compare-and-swap, so an
external edit in the final read/write window still requires inspection.
For `plan:waived`, only the owner may propose a replacement because the
description is the approved scope. PR code is never checked out or executed
with the owner PAT.

An applied proposal disables prior auto-merge and dispatches all four reviews
once per head/description revision after older reviews have drained. If dispatch
partially fails, the workflow fails visibly; missing revision results time out
to `needs:human` and must be re-run manually. Invalid/stale proposals escalate
once with an actionable rejection; publish a corrected proposal and remove
`needs:human` to resume.

The shepherd recognizes both legacy and body-hashed fix-request markers from
the owner/workflow bot. Every completion event, hourly tick and manual dispatch
uses the latest session timestamps. A successful session that started after a
fix request but changed neither head nor description escalates once, instead of
silently waiting or issuing unbounded fixes. Active/interrupted sessions never
apply metadata or start reviews. Read the reply, fix metadata or dispute the
finding and re-run reviews; use `handoff:local` when taking over locally.

## Workflows

| File | Kind | Purpose |
|------|------|---------|
| `squad-plan.md` | gh-aw (Copilot engine) | `/plan` command; Architect plan comment and labels |
| `squad-plan-approved.yml` | Actions | Owner check, assigns `copilot-swe-agent[bot]` with `custom_agent` (skipped for `handoff:local`); guards `plan:waived` |
| `squad-review-{architect,security,verifier,devops}.md` | gh-aw | Independent review, inline comments, required check `Squad Review / <Role>`. Copilot branches are reviewed only via dispatch from the shepherd; other PRs on open/push/ready |
| `shared/squad-review.md` | gh-aw import | Common review protocol |
| `squad-pr-shepherd.yml` | Actions (`workflow_run` on *Copilot cloud agent*, guarded description edits, push to `main`, hourly fallback) | Marks successful Copilot PRs ready, applies explicit metadata proposals, dispatches reviews per head/description revision, resolves conflicts and escalates stalls |
| `squad-review-gate.yml` | Actions (`workflow_run`) | Aggregates checks, validates completion declarations, auto-merge or fix loop |
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
   Copilot push leaves CI as *action required*. You never need to approve those
   runs: the shepherd dispatches CI and the reviews itself for every Copilot head,
   and the gate merges directly when GitHub already reports the PR as mergeable
   (auto-merge cannot be enabled then). This toggle has no API.
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
