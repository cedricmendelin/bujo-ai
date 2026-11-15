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
| Publish a local plan | Owner comments a versioned plan, e.g. `gh issue comment <N> --body-file <plan-file>` | Creates a candidate only; publishing never assigns work |
| Approve | Owner applies `plan:approved` after selecting `handoff:local` or cloud | Bot records issue/comment/version/hash/owner label event, adds `plan:bound`, then either local-handoff or Copilot assignment |
| Approve for local work | Owner applies `handoff:local`, then `plan:approved` | No cloud assignment. Implement in the GitHub Copilot app |
| Hand over a cloud PR | Add `handoff:local` to a `copilot/*` PR | Shepherd and `@copilot` fix requests stop; reviews run on every push; use Agent merge |
| Waive the plan | Owner applies `plan:waived` to a PR | For work without an issue plan; reviewers use the PR description as scope; reviews start |
| Re-review | Automatic after each Copilot session (shepherd) or push (local PRs); manually: Actions → *Squad Review X* → Run with `pr_number` | Reviewer check re-published for the PR head |
| Escalation | `needs:human` + `@cedricmendelin` comment | 3 failed fix rounds, interrupted recovery, exhausted recovery bound, unapproved recovery scope, unresolved conflicts, reviewer crash/timeout. Remove the label to resume automation |
| Automation failure | Issue `Squad automation failing: <workflow>` | Shepherd or gate itself failed (for example an expired token) |

Use `/plan` to draft and `/plan revise <feedback>` to revise; `/squad plan` is
not a supported command. No label is needed to draft or revise. Only the
repository owner can approve or waive (`squad-plan-approved.yml` removes the
label otherwise). Versioned plans may be authored by `github-actions[bot]` via
`/plan` or by the repository owner when published locally. Keep the exact heading
`## 📋 Implementation plan v<N> · owner: <architect|builder|ai|devops>`.

For a local plan, save the full approved text in a local file and publish it as
an issue comment:

```powershell
gh issue comment 41 --body-file .\plan.md
gh issue edit 41 --add-label handoff:local
gh issue edit 41 --add-label plan:approved
```

Replace `41` with the issue number. Choose the `handoff:local` path **before**
applying `plan:approved`; leaving it off selects cloud assignment. Confirm the
authenticated `gh` account is the repository owner. Publishing a comment,
applying `handoff:local`, and applying `plan:approved` are separate actions:
publishing or selecting a path never assigns work. `plan:bound` is automation
managed and must not be applied manually.

The approval record is a bot-authored issue comment containing the issue number,
plan comment ID/version, owner member, normalized-body SHA-256, owner
`plan:approved` timeline event ID/time, record time, and state. Normalization
converts CRLF to LF and trims outer whitespace. The local-handoff comment also
shows the hash; the machine-readable record is embedded in an HTML comment
(`<!-- squad-plan-approval {...} -->`). Approval requires the latest trusted
plan, a strictly increasing version, a current owner label event and a plan
comment update time strictly earlier than the event (same-second edits fail
closed). Owner edits/deletes, newer owner/bot plans, label changes or live drift
revoke approval; re-publish a higher version and re-apply the labels to resume.
`/plan` bot safe-output changes are reconciled on the completed `Squad Plan`
workflow run because their `GITHUB_TOKEN` writes do not fire issue-comment or
label workflows. Owner-authored edits/deletes reconcile directly. A ten-minute
sweep and manual `Squad Plan Binding` dispatch are backstops and migration tools.

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
commit. Local (`handoff:local` or other branches) PRs dispatch trusted reviews
through binding reconciliation on PR head/body changes; the model workflows
themselves run only on the default branch, never from PR workflow code.

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

## Plan binding, review attestations and race limits

The `Squad Plan Binding` workflow checks live issue comments, labels and
timeline events from trusted default-branch code. It discovers linked PRs from
explicit body references (closing and non-closing), GraphQL
`closingIssuesReferences`, and issue timeline cross-references, and only writes
within this repository. It publishes the required `Squad Plan Binding` check on
each eligible PR head, fails closed for unbound label-only approvals during
migration, removes stale approval/binding labels, appends a superseded bot
record, and disables auto-merge on linked open PRs when drift is detected.
`plan:waived` PRs remain outside issue binding and continue to use owner-approved
PR descriptions, but **not** outside the globally required check. The same
`Squad Plan Binding` check publishes an explicit **owner-waiver-verified
exemption** whose summary binds the latest owner waiver label event, exact
description SHA-256 and PR head. It creates no issue approval record or labels.
The existing scope resolver validates the PR's paginated waiver timeline
without reading linked issues. The gate requires this exact exemption as well
as all four run-attested reviews; filtering the check out locally would not
satisfy a repository-wide required status.

Waiver additions/removals and PR body/head changes reconcile from
`pull_request_target`; unrelated label events are filtered before any writes
or dispatches. A changed exemption disables pending auto-merge before publishing
success, so old green reviews cannot merge a newly edited scope. Forged/latest
non-owner waivers or removal publish failure and disable auto-merge. Publication
rechecks the event/body/head before dispatching. Initial label-driven review
dispatch remains owned by the approval workflow to avoid duplicate runs; body
and head changes start fresh trusted reviews. Manual/ten-minute/workflow-run
sweeps also enumerate open PRs, preserve existing owner-waived scopes and
populate their exemption checks, and use waiver history to recover lost removal
events. A removed waiver can instead qualify only through a genuine issue
binding and fresh reviews, never through an old exemption.

Each reviewer runs only through a default-branch `workflow_dispatch`. A
non-model pre-agent step verifies the current plan record (equivalent to
`node .github/scripts/plan-approval.js verify --issue <N>`) and writes an
immutable, run/attempt-scoped Actions artifact before the model runs. The attestation
captures the exact issue/comment/version/hash/owner label-event, PR head and
description hashes, reviewer role, workflow run ID and attempt. It includes
only the verified plan text (or the exact owner-waived PR description) as
review scope. In pinned gh-aw v1.2.2, activation builds the prompt before the
agent job exists. The pre-agent step therefore appends the verified bytes to
the **downloaded engine prompt file**, `/tmp/gh-aw/aw-prompts/prompt.txt`, after
verification and before the Copilot harness consumes that exact file through
`--prompt-file`. It does not reference agent-step outputs in activation.
Generated-lock ordering/path tests and a fake-API execution of the producer
guard this dataflow; missing or stale scope fails the step before model startup.

Do not treat a review check's `started_at` as the review start: gh-aw posts the
check as a safe output after the model. In pinned gh-aw v1.2.2,
`actions/setup/js/create_check_run.cjs` creates the check with the Checks API but
does not set `external_id` or `details_url`. The trusted gate therefore obtains
the exact check-run ID from the `Process Safe Outputs` job log for the recorded
workflow run **and attempt** (the job-log endpoint redirects to plaintext,
not a ZIP; only the attestation artifact uses archive extraction), then fetches that ID from the Checks API and
requires it to equal the required review check on the attested PR head. A
missing/mismatched ID, run, attempt, plan hash, owner event, PR head or body
fails closed and dispatches the four reviews once for that binding/head/body.
The source workflow writes the artifact before model invocation; model output
cannot author or alter that identity. The gate re-reads the live plan immediately
before enabling auto-merge (with `expectedHeadOid`) or direct squash merge and
again after enabling auto-merge; drift disables auto-merge, and drift discovered
after a direct merge is escalated to @cedricmendelin.
The compiler-owned run title includes the target PR number, so a failed
default-branch dispatch with no PR head association still disables auto-merge
and creates one actionable `needs:human` escalation per run/attempt, even if it
failed before producing an attestation. Fix its failed step, then dispatch a
fresh review; an older green check is not a substitute.

Approval and binding workflows share one repository-wide concurrency group,
which serializes approval, invalidation and sweeps (stronger, but less
concurrent, than per-issue serialization). Queued run order is not guaranteed:
owner approval-label events are left to the approval caller, its completion
triggers binding reconciliation, and legacy migration defers while approval
runs are queued/running. Invalidation first attempts to disable auto-merge and
fail binding on **every** linked open PR. Only after these safety writes succeed
does it unassign/supersede/clean up; `plan:bound` is removed last so partial API
failures remain discoverable for retry. The approval caller likewise retains
this marker on a raced/revoked approval until reconciliation cleans it up;
it is never sufficient without `plan:approved` and a valid record.
GitHub does not provide an atomic
transaction spanning a plan-comment edit, label/record writes, workflow runs,
review dispatch and merge. There remains an event-to-reconcile window: a plan
edit can race a previously green pending auto-merge before its event is
processed (typically seconds/minutes, up to the ten-minute backstop if events
are lost). Pre/post merge reads narrow but cannot eliminate that interval.
The gate cannot undo a merge already completed before reconciliation; it notifies
the owner. The guarantees are owner-account provenance, exact-snapshot
verification and bounded fail-closed reconciliation—not proof that a human
clicked (the owner PAT is indistinguishable from the owner account), nor a
strict atomic guarantee.

### GitHub event and rollout facts

GitHub documents that events caused by `GITHUB_TOKEN` generally do not trigger
new workflow runs; `workflow_dispatch` and `repository_dispatch` are exceptions.
This is why binding reconciliation listens for `workflow_run` completion of the
`Squad Plan` bot workflow and `Squad Plan Approved` caller as well as direct owner comment/label events. A
`workflow_run`-triggered workflow must be present on the default branch and uses
default-branch workflow code. See [Triggering a workflow](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/trigger-a-workflow)
and [Events that trigger workflows](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#workflow_run).

On rollout, manually dispatch `Squad Plan Binding` once. Existing `plan:approved`
issues with no bot record are legacy/unbound: they fail binding, linked open
PRs get a failing binding check, and approval is removed so the owner can
re-apply it against the current plan. Existing `plan:waived` scopes are retained
and get the same required check's owner-verified exemption; the sweep does not
revoke their scope or invent issue approvals.

**Coordinate the existing cloud work before deployment/first sweep:** open
PR **#44 (issue #5)** is in the legacy-approved rollout inventory. Pause that
cloud handoff and auto-merge, review its current plan with the owner, then
re-apply approval after deployment to establish the bot record and `plan:bound`
before resuming the chosen handoff. Confirm live PR state during rollout.
Without that coordination, legacy reconciliation intentionally fails its
binding/removes stale approval and may unassign an existing Copilot handoff;
do not surprise the ongoing implementation or waive it to bypass migration.

Verify
the check on a throwaway issue/PR before adding `Squad Plan Binding` to the
`main` ruleset. The current ruleset still has only the original five checks;
do not assume this code deploys or changes repository settings. Human setup is
tracked separately in issue #42.

After deployment, validate on throwaway work: local publish → local label
approval (no cloud assignment), owner edit/delete/new version (binding
revocation), bot `/plan revise` during a pending review/auto-merge, and a fresh
review run whose workflow ID/attempt and safe-output check ID match. Also test
an existing owner-waived PR getting its exempt success, forged/latest non-owner
waivers failing, body/head changes getting fresh scope/reviews, and waiver
removal overwriting success with failure. Verify
auto-merge is disabled on drift, then separately configure the binding check as
required after observing it. Rollback by reverting the workflow change; remove
or re-evaluate `plan:bound` markers during recovery rather than restoring
label-only approvals blindly.

## Workflows

| File | Kind | Purpose |
|------|------|---------|
| `squad-plan.md` | gh-aw (Copilot engine) | `/plan` command; Architect plan comment and labels |
| `squad-plan-approved.yml` | Actions | Owner timeline check, bot record + `plan:bound`, live handoff/assignment rechecks; preserves owner-only `plan:waived` |
| `squad-plan-binding.yml` | Actions | Owner issue events, bot `workflow_run` invalidation, PR link reconciliation, migration, ten-minute sweep and binding status |
| `squad-review-{architect,security,verifier,devops}.md` | gh-aw | Default-branch dispatch-only independent reviews; non-model start attestation, inline comments, required check `Squad Review / <Role>` |
| `shared/squad-review.md` | gh-aw import | Common review protocol |
| `squad-pr-shepherd.yml` | Actions (`workflow_run` on *Copilot cloud agent*, guarded description edits, push to `main`, hourly fallback) | Requires a successful session and verified live binding before readiness/review dispatch; bounded recovery, metadata, conflicts and escalation |
| `squad-review-gate.yml` | Actions (`workflow_run` on Copilot cloud agent, CI, Squad Review and Plan Binding) | Rechecks live scope and run-attested review results; requires `Squad Plan Binding` for issue plans, validates completion, auto-merge or fix loop |
| `ci.yml` | Actions | Required check `ci`: .NET build/test, Terraform fmt/validate |
| `copilot-setup-steps.yml` | Actions | .NET 10 and Terraform in the Copilot cloud agent environment |

Squad agents live in `.github/agents/<member>.agent.md`; the same files drive
Copilot implementation and gh-aw reviews. Recompile after editing a `.md`
workflow or an imported agent: `gh aw compile --strict` (gh-aw v1.2.2), and
commit the `.lock.yml` files.

Validate with `node --test .github/scripts/*.test.*` (both `.js` and `.mjs`).
Use a duplicate-key-rejecting YAML parser, not `yaml.safe_load` alone, and run
actionlint on the authored Actions workflows. gh-aw's `--actionlint` runner
needs Docker; a standalone actionlint binary can validate them without Docker.
Actionlint v1.7.12 still reports compiler-owned `job.workflow_ref`,
`job.workflow_sha`, `cache-mode` and concurrency `queue` in the v1.2.2 generated
locks; do not hand-edit locks or suppress unrelated author-workflow failures.
Strict gh-aw compilation and the generated dataflow contracts are separate
checks, not a claim that the workflows have executed successfully on GitHub.

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
2. **Labels**: `plan:proposed`, `plan:approved`, `plan:bound`, `plan:waived`, `handoff:local`,
   `squad:architect`, `squad:builder`, `squad:ai`, `squad:devops`, `needs:human`.
3. **Repository settings**: allow auto-merge, squash merge, delete head branches.
4. **Ruleset on `main`**: after deployment and a successful throwaway-PR
   observation (see [binding rollout](#plan-binding-review-attestations-and-race-limits)),
   require a pull request and status checks `ci`,
   `Squad Review / Architect`, `Squad Review / Security`,
   `Squad Review / Verifier`, `Squad Review / DevOps`, and
   `Squad Plan Binding` (exact case); block force pushes; repository admin
   bypass for emergencies only. This is a separate human settings change; code
   cannot modify or verify the ruleset.
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
