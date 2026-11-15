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
        │                  Squad PR Shepherd: classify latest session
        │                      success ─▶ mark ready, dispatch CI + 4 reviews
        │                      interrupted ─▶ bounded recovery or needs:human
        └─ handoff:local ──▶ GitHub Copilot app session ─▶ ready PR + Agent merge
                                         │
          CI + Squad Review / Architect · Security · Verifier · DevOps
                                         │
                      Squad Review Gate (workflow_run aggregator)
           all success ──▶ squash auto-merge ──▶ main ──▶ deploy
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
| Escalation | `needs:human` + `@cedricmendelin` comment | 3 failed fix rounds, interrupted recovery, exhausted recovery bound, unapproved recovery scope, unresolved conflicts, reviewer crash/timeout. Remove the label to resume automation |
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

The gate re-reads the current PR description whenever a Copilot session, CI, or a
Squad Review run completes. For same-repository cloud PRs, an owner/Copilot description edit also
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
| `squad-pr-shepherd.yml` | Actions (`workflow_run` on *Copilot cloud agent*, guarded description edits, push to `main`, hourly fallback) | Requires a successful session before readiness, metadata proposals and reviews; bounded recovery, approval diagnostics, conflicts, and escalation |
| `squad-review-gate.yml` | Actions (`workflow_run` on Copilot cloud agent, CI, and Squad Review) | Requires a successful session and current head/description shepherd marker; aggregates checks, validates completion declarations, auto-merge or fix loop |
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
5. **Copilot cloud agent**: enable it for the repository. Keep **Require approval
   for workflow runs** enabled unless an administrator explicitly accepts the
   security tradeoff described below. The shepherd's owner-token dispatches are
   independent; do not treat an approval-blocked `pull_request` run as a CI result.
   Firewall allowlist `registry.terraform.io`, `releases.hashicorp.com`,
   `api.nuget.org` as needed.
6. **Azure (with the IaC issue)**: OIDC federated credentials and the variables
   documented by that issue's plan. No Azure secrets are stored in GitHub.

## Interrupted sessions and workflow approvals

The shepherd classifies the newest *Copilot cloud agent* workflow run for the PR
branch by creation time and run number. A running session waits; only `success`
continues the lifecycle. A failed, cancelled, timed-out, approval-blocked, skipped,
or otherwise non-success result is interrupted, regardless of the PR title or
description. Repeated triggers do not repeat a recovery comment for the same run.
The shepherd can request at most two recoveries for a PR, and only when its linked
issue has `plan:approved` or the PR has `plan:waived`. If a recovery session is
interrupted, recovery is exhausted, or the plan is not approved, it adds
`needs:human`, assigns and mentions @cedricmendelin, and links the run. A later
successful session supersedes the earlier interrupted result. A person who means
to stop work should add `needs:human` or `handoff:local` before cancelling; GitHub
does not identify whether a cancellation was human-initiated.

The gate independently re-reads the latest session. For Copilot PRs it waits
unless the session succeeded and the shepherd posted its marker for the current
head commit. An active or interrupted session cannot produce a fix/approval
marker or merge; if a newer non-success session appears after the gate enabled
auto-merge, the gate disables it when it next evaluates that PR. Only the
shepherd marks a Copilot draft ready.

### Approval-blocked runs

GitHub documents that Actions workflows do not run automatically when Copilot
pushes a PR by default. The PR merge box offers **Approve and run workflows**;
the repository administrator can optionally disable **Require approval for
workflow runs** under **Settings → Copilot → Cloud agent → Policies**. GitHub
warns that disabling it can let unreviewed Copilot code gain repository write
access or access Actions secrets. Review every workflow's `GITHUB_TOKEN`
permissions and secrets before changing this setting. The automation never
changes or bypasses it. This Copilot-specific policy is distinct from
**Settings → Actions → General** fork-pull-request approval policy.

See GitHub's [Copilot cloud-agent workflow approval
settings](https://docs.github.com/en/copilot/how-tos/use-copilot-agents/cloud-agent/configuring-agent-settings)
and [March 2026 changelog](https://github.blog/changelog/2026-03-13-optionally-skip-approval-for-copilot-coding-agent-actions-workflows/).

On 2026-11-15, [run 43717345116](https://github.com/cedricmendelin/bujo-ai/actions/runs/43717345116)
and [run 43718023727](https://github.com/cedricmendelin/bujo-ai/actions/runs/43718023727),
both *Squad PR Shepherd* `workflow_run`s, ended `action_required` with zero jobs.
The owner reported that Copilot-actor
`workflow_run`/`issue_comment` runs and Copilot-pushed PR workflows were gated;
owner-dispatched runs succeeded. These are approval-blocked runs, not failed
jobs or successful CI results. This evidence is consistent with GitHub's documented
default, but Actions cannot expose the current Copilot setting value, so its
current UI state and the reason it did not cover those events remain unverified.

The shepherd reports matching `action_required` runs once per PR head, including
workflow, event, actor, and URL. Copilot-actor `workflow_run`/`issue_comment`
runs with no PR association are explicitly labeled repository-level rather than
attributed to that PR. If `ci` already passes, those runs are not
additional results and no approval action is needed. Otherwise, when the latest
Copilot session succeeds, the shepherd dispatches CI and reviews as the owner;
while a session is active or interrupted, it waits or requests recovery without
dispatching them. A blocked run never satisfies a required check. If an owner
chooses to approve one manually, first inspect the PR changes and workflow
permissions; do not approve it merely to clear the status.

### Validating the lifecycle

1. First use **Actions → Squad PR Shepherd → Run workflow** on an open Copilot PR
   with `dry_run: true`. Inspect the logs for the decision and confirm it added no
   comments or labels, changed no draft state, and dispatched no workflow.
2. For a live recovery test, use a throwaway Copilot PR with an approved test plan
   (or owner-applied `plan:waived`). Cancel each Copilot session before it can
   complete, then dispatch the shepherd normally. Verify one recovery marker for
   each interrupted run, no ready transition or CI/review dispatch while
   interrupted, and `needs:human` after the recovery failure/bound. The gate must
   wait even if old checks are green. Do not allow a session on this PR to
   complete; close the test PR unmerged after verification.
3. Use the unit tests to verify the successful-session, later-retry-success, and
   gate-eligibility paths. If exercising a real successful merge path, use an
   isolated repository without production deployment or secrets.
4. Roll back by reverting the change; no data migration is required.

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
