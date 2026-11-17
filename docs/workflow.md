# Governed delivery workflow

The workflow keeps human approval of issue plans, four independent specialist
reviews by default, bounded review repair, and GitHub-protected auto-merge. It
removes cryptographic approval records, plan binding, review attestations,
scheduled reconciliation, and duplicate lifecycle controllers.

```text
issue ── /plan ──▶ Architect posts visible plan v1
      /plan revise <feedback> | /plan route <member> ─▶ append v2; clear approval
      owner publishes next numbered comment ─────────▶ same heading/version rules
      owner applies plan:approved
          ├─ handoff:local present ─▶ no cloud assignment
          └─ otherwise ────────────▶ Copilot cloud assignment once

cloud session success or local ready PR
      ─▶ one default-branch Squad PR Lifecycle controller
          ├─ start CI as needed and selected specialist reviews
          ├─ aggregate current-head CI + review + human feedback
          ├─ forward review findings (not CI/conflict repairs)
          └─ ci + Squad Merge Readiness green ─▶ GitHub squash auto-merge
                                                 └─ protections execute merge
```

See [README → How we work](../README.md#how-we-work) for the day-to-day flow.

## Plan and approval

`/plan`, `/plan revise <feedback>`, and `/plan route <member>` remain available.
Each plan is an append-only issue comment headed
`## 📋 Implementation plan vN · owner: <member>`. The gh-aw plan workflow does
not hide or edit older versions. A new numbered plan clears `plan:approved`;
review and re-apply the label to start cloud implementation. Chat-authored plans
use the exact same heading, required sections, version increment, and completion
contract; publish them as issue comments. Publishing alone never starts work.

The repository owner or an ordinary repository writer may apply `plan:approved`.
The default-branch `squad-plan-approved.yml` reads the latest numbered plan,
then assigns Copilot with its named custom agent. The ordinary issue assignee
state makes duplicate delivery idempotent. If `handoff:local` is already
present, no cloud assignment is made. Adding it later stops subsequent custom
cloud dispatches, but does **not** cancel an active cloud session or native
Watch and fix. No plan SHA, approval record, `plan:bound` label, binding check,
or timeline-provenance protocol is used.

Initial-assignment idempotency is not continuation. After a partial delivery's
PR is merged or closed and its dependency is met, a maintainer can manually run
**Squad Plan Approved** with `issue_number` and sanitized `dependency_evidence`.
The issue must still be open with its latest plan approved. `handoff:local`
still suppresses cloud work. An open PR cross-referencing the issue blocks a
new assignment: continue that PR or use its repair resume instead.

Continuation conservatively refuses to start while **any** cloud session in
this repository is active, queued, or waiting. Once safe, it records admission,
deliberately clears the issue's human pause if present, removes the existing
Copilot assignment if necessary, and reassigns the same approved scope. A rerun
of that Actions run cannot create a duplicate session. If reassignment fails,
inspect the admitted run before deliberately starting a new continuation.
Dependency evidence is scope data, not permission to change the plan. Changed
scope requires a new numbered plan and approval. No secrets belong in evidence.

To publish and start local work:

```powershell
gh issue comment <N> --body-file .\plan.md
gh issue edit <N> --add-label handoff:local
gh issue edit <N> --add-label plan:approved
```

For chat-authored plans, use the next version number, preserve previous comments,
and verify the account before applying the label. An owner-published newer plan
automatically removes `plan:approved`; `/plan revise` and `/plan route` also
remove it. The label authorizes initial cloud assignment, not a cryptographic
scope hash.

## Manual Squad Retro

The **Squad Retro** workflow is available in GitHub Actions and runs only when a
user selects **Run workflow**. Choose a 7-, 14-, or 30-day lookback; the workflow
publishes one report issue with evidence, friction, likely causes, and proposed
improvements. It does not create separate action-item issues, assign work,
dispatch another workflow, modify the repository, or schedule another run.
Proposals are not approved or applied by publishing the report.

Direct local PRs may use scope agreed in the Copilot conversation without an
issue, plan, approval, or waiver prerequisite. If a PR links an issue plan, cite
the plan version. Reviewers read the cited plan and issue acceptance criteria as
ordinary untrusted data; they are not injected as instructions. There is no
`plan:waived` path. `Completion: full`/`Completion: partial`, evidence for
acceptance criteria, and non-closing linkage plus `Remaining work` for partial
PRs remain required.

## Lifecycle ownership

| Responsibility | Owner |
|---|---|
| Approved issue scope | Human-approved versioned plan and the PR's ordinary plan reference |
| Mechanical completion/linking errors | Trusted controller for recognized generated suffixes; explicit maintainer feedback otherwise |
| CI failures and target-branch conflicts (cloud) | GitHub.com Copilot **Watch and fix**, when enabled |
| CI failures and conflicts (local) | Desktop-app Agent merge, if explicitly enabled |
| Specialist reviews and aggregate decision | Architect, Security, Verifier, DevOps; one `Squad PR Lifecycle` controller |
| Actual merge | GitHub auto-merge under branch protections; never a direct REST merge |

The only custom lifecycle is the event-driven
`.github/workflows/squad-pr-lifecycle.yml`. It reads and writes PR metadata,
checks, comments, and labels using default-branch code. It never checks out or
executes PR code in a privileged job. It makes no scheduled sweeps. It marks a
successful cloud PR ready, dispatches initial CI if the Copilot approval gate
prevented CI from starting, starts selected reviews, forwards actionable review
findings, publishes `Squad Merge Readiness`, and enables native GitHub squash
auto-merge only when eligible. `workflow_dispatch` with a PR number remains the
manual recovery/re-evaluation entry point.

When all inputs pass and the required rules are present, the controller keeps
`Squad Merge Readiness` pending while enabling GitHub squash auto-merge, then
publishes success. Publishing success first can make the PR clean, in which
case GitHub rejects the auto-merge request. An arming failure must not publish
green readiness; no direct-merge fallback is permitted.

An infrastructure failure or approval block in a reviewer pauses once with
`needs:human` rather than repeatedly invoking the model. Fix the cause, remove
the label, and re-evaluate. Resolving a blocking inline thread without another
PR/review event requires a manual lifecycle dispatch; there is no polling loop.

The shared reviewer GitHub reader explicitly uses `min-integrity: none` scoped
to `cedricmendelin/bujo-ai`. gh-aw otherwise defaults public repositories to
`approved`, which filters unmerged diffs and files read at a PR head SHA even
when the PR author is trusted. Reviewers must inspect that untrusted content,
but never execute it or follow instructions from it. Shell access and CLI
mounting are explicitly disabled; GitHub reads and safe outputs use MCP. Agent
permissions remain read-only and publication remains restricted to safe review
outputs; no private cross-repository access is enabled. Missing review input
must produce an incomplete review, not a passing check.

After deploying a reviewer-reader fix to `main`, recover a PR paused solely by
that infrastructure failure by deliberately removing `needs:human`, then
dispatching `Squad PR Lifecycle` with its PR number. Confirm all four reviewer
runs complete and publish checks on the current head. Do not remove the label
for an unrelated human decision or bypass reviews/protections to recover.

The controller does not repair CI failures or merge conflicts; it leaves those
to the native cloud/local repair owner. It does not enable desktop-app Agent
merge's **Merge** action. For local takeover of a cloud PR, the owner disables
Watch and fix first to prevent competing repair agents. Adding `handoff:local`
does not claim to stop or cancel native running work.

### Reviews and `review:none`

Architect, Security, Verifier, and DevOps workflows remain independent and run
by default. A code commit starts fresh reviews for the new head. A cosmetic PR
description edit does not re-run model reviews. Results must match the current
head and description input; after `review:none` is removed, an in-flight pre-removal result cannot
satisfy the newly selected review generation.

Only line endings, blank lines, and trailing whitespace are ignored as cosmetic.
Changes to completion, scope, evidence, references, or other substantive body
content dispatch fresh reviews even without a code commit. A review-input key
in the workflow run title identifies the selected description; it is a cache
key, not a plan approval, signature, or proof of correctness. Legacy unkeyed
reviews are not adopted. Old in-flight runs cannot satisfy a new input.
Before merging, the controller re-reads both the head and description input.
Reviewers must inspect their applicable diff even if metadata is wrong; an
incomplete inspection must never publish success.

Both check timestamps must fall inside the selected successful workflow run.
Their relative ordering is not a freshness signal: GitHub can assign an implicit
`started_at` just after a safe-output publisher's `completed_at`. Such a result
must neither trigger a duplicate review nor hide genuine blocking findings.

An eligible owner/maintainer can add `review:none` to one PR. It skips pending
and existing specialist findings for that PR, but never CI, trusted human
`CHANGES_REQUESTED` reviews, or unresolved trusted human inline threads marked
`[blocking]`. It is not an issue label. Removing it starts fresh reviews on the
same head. Review selection does not change required branch protection checks;
while old reviewer contexts remain required, GitHub still enforces them.

The controller forwards blocking specialist findings and trusted human review
feedback to the implementer. It suppresses cloud fix requests while a session is
active. Three **attributed substantive** review-fix attempts (or local code changes
after a request) are allowed per repair episode. The cloud agent reports the
request token, actual Actions run ID, outcome, and evidence in a top-level PR
comment using the exact marker supplied in the repair request. Only the known
Copilot bot's report paired with a terminal run can count. A no-change
investigation can be substantive; a commit alone is not proof of attribution.
Queued runs, cancellations, metadata fixes, unrelated Watch and fix runs, and
capability/dependency blocks do not consume attempts. Missing attribution after
a later session completes pauses for inspection rather than inferring a fix.
After three actual attempts with
blocking findings still present, it assigns/mentions @cedricmendelin and adds
`needs:human`. That label is an explicit pause and is never silently removed.
Capability/dependency outcomes and human-only review findings pause once instead
of issuing the same impossible request repeatedly. Shared reviews declare
`Repair route: implementation`, `Repair route: human`, or `Repair route: none`.
Implementation work is addressed first when both code and human actions remain.

Resolve the cause, then deliberately run **Squad PR Lifecycle** with its PR
number and `resume_repairs: true` to clear the pause and start a fresh bounded
episode. This operation requires repository write permission, refuses an active
cloud session, and is idempotent for a rerun of the same Actions run. Normal
dispatches and code commits do not reset attempts or clear a pause. Removing the
label alone preserves the previous budget. Human review blocks, CI, completion,
and scope approval still apply. `needs:human` does not stop native Watch and fix,
which must be disabled separately when pausing.

The resume operation removes the PR label with the existing `GH_AW_AGENT_TOKEN`
PR-write credential. If a run records a resume marker but fails to remove the
label, fix the reported cause and start a **new** dispatch; rerunning the same
Actions run must not clear a later pause.

## Completion and partial delivery

Every PR declares exactly one whole-line `Completion: full` or
`Completion: partial`. Full completion requires evidence for every issue
acceptance criterion, including implementation and relevant automated tests.
The declaration is not semantic proof; the independent Verifier checks the
evidence.

A partial milestone uses a non-closing issue reference, includes a
`Remaining work` section naming unfinished criteria, responsible owner,
dependency/evidence needed, and next action, and leaves the issue open. The
milestone must have been explicitly agreed; a partial fallback does not silently
shrink scope. Unmet issue criteria prevent issue closure, not merge of an
otherwise complete authorized milestone. Full completion still requires every
criterion; no automation promotes a partial declaration to full.

Before spending model reviews, completion preflight replaces standalone closing
references only inside the recognized Copilot-generated suffix of an
unambiguous partial description, and only if the resulting completion check
passes. It preserves unrelated body text, pauses auto-merge, immediately
re-reads live body/head/labels before writing, and verifies the result. A detected concurrent change or
failed write does not fall back to overwriting or asking a coding agent to
change repository files. A paused or active cloud PR is not mutated.
GitHub does not support conditional PATCH for this endpoint: these are
optimistic pre/post checks, not an atomic compare-and-swap guarantee against
an external edit inside the HTTP read/write window.
Other syntax/closing-reference errors receive explicit completion feedback and
blocked readiness, even if no specialist check has failed. They consume no
code-repair budget. Correcting the description triggers re-evaluation.

For an unambiguous single same-repository `Refs #N`, `Relates to #N`, or
`Part of #N` and a `## Remaining work` section, the controller copies a quoted
handoff to the open issue, once per changed remaining-work text. It never
reopens issues or marks them complete. Ambiguous/multiple linkage needs an
explicit owner-maintained handoff; specialists verify it. This makes remaining
scope durable without creating duplicate follow-up issues.

### Recovering the observed partial-delivery loop

After deployment, inspect the **live** state of PRs #108/#109; they may have
changed since this investigation. If a PR is still paused solely by the
generated-suffix loop, explicitly resume repairs. Preflight can then repair
the suffix, preserve partial scope, and dispatch complete keyed reviews.
Do not adopt the old metadata-only reviews or use `review:none` to bypass them.
If another human pause remains, resolve it first. Reconcile #108's old
rendering-approval premise with accepted ADR 0001 without claiming live-browser
verification; retain #109's actual client-verification work. This code change
does not edit live PRs, remove their labels, or perform that rollout.

## Workflows and tests

| File | Purpose |
|---|---|
| `squad-plan.md` | gh-aw `/plan` command; append-only numbered plans |
| `squad-plan-approved.yml` | Issue plan approval, new-version invalidation, idempotent cloud assignment/local suppression |
| `squad-review-{architect,security,verifier,devops}.md` | Independent default-branch specialist reviews |
| `shared/squad-review.md` | Shared untrusted-data review protocol |
| `squad-pr-lifecycle.yml` | Single event-driven readiness/review-feedback/auto-merge controller |
| `ci.yml` | Required `ci` check; .NET build/test and infrastructure validation |

Recompile agentic workflow sources and commit generated locks:

```powershell
gh aw compile --strict
node --test .github/scripts/*.test.*
```

Use a duplicate-key-rejecting YAML parser and actionlint when available. Do not
hand-edit generated `.lock.yml` files. Compilation and local tests do not prove
that GitHub settings or live Actions behavior are deployed.

## Ruleset migration and active PR coordination

**Owner action required; this change does not edit repository settings, labels,
or active PRs.** The observed effective `main` checks currently require `ci`
and the four `Squad Review / <Role>` contexts; they do not require plan binding
or `Squad Merge Readiness`. After this controller is deployed:

1. Keep all existing required checks. Use an isolated test PR to confirm the new
   controller publishes current-head `Squad Merge Readiness`, respects human
   blocks, and never enables auto-merge when rules are missing.
2. Add `Squad Merge Readiness` as a required check alongside `ci` and the four
   existing reviewer checks. Do not remove protections or use an admin bypass.
3. Coordinate in-flight PRs and observe readiness under the staged ruleset.
   In particular, inspect the live state of **PR #58**
   (`copilot/implement-planned-period-queries`) before rollout; its head, native
   sessions, review results, and auto-merge state can change. Do not cancel
   native work or discard genuine review findings. After controller deployment,
   coordinate with its owner, re-run the applicable current-head
   reviews/readiness, and preserve its findings. An old Plan Binding failure is
   not a requirement in the new lifecycle.
4. Once the aggregate check exists, is required, and is observed passing, remove
   the four individually required specialist contexts. Retain `ci` and
   `Squad Merge Readiness`. GitHub protections remain authoritative throughout.
5. Create or verify the `review:none` label through an explicit owner settings
   action before using it. Legacy `plan:bound` and `plan:waived` labels, if
   present, are inert; no workflow depends on them.

The repository owner must explicitly perform and verify these changes in GitHub
Settings → Rulesets. Do not report the staged migration or PR #58 coordination
as completed until the owner performs it and the checks are observed live.

## One-time setup and secrets

The owner configures the GitHub auto-merge/squash settings, Copilot cloud agent,
and native Watch and fix policy outside this PR. Enable **Fix failed Actions
workflows** and **Resolve merge conflicts** on each applicable cloud PR or use a
supported account default; do not assume the account default covers PRs created
by Copilot. Keep workflow approvals and protections as configured by the owner.
No Azure credentials are stored in Actions; Azure deployment uses OIDC only.

The existing gh-aw workflows use two repository secrets:

| Secret | Purpose |
|---|---|
| `COPILOT_GITHUB_TOKEN` | Copilot inference for `/plan` and specialist reviews |
| `GH_AW_AGENT_TOKEN` | Cloud issue assignment, owner-authorized comments, dispatch, ready state, and GitHub auto-merge |

The owner creates and rotates these fine-grained PATs in GitHub Settings and
stores them as repository Actions secrets. Never print, paste into PRs, or
commit them. The agent token is not exposed to model review prompts. Privileged
workflow reads use `GITHUB_TOKEN` for checks and PR metadata; writes use the
agent token only where owner-authorized operations require it.

After code changes, inspect `gh aw compile --strict`, Node tests, and CI. The
owner separately verifies the ruleset migration, Watch and fix, and live
auto-merge behavior. No code delivery alone establishes that external setup is
complete.

## Limits

- Four model reviewers remain the default; `review:none` is a scoped,
  owner/maintainer-controlled cost opt-out.
- `needs:human` pauses the custom lifecycle only. Disable native Watch and fix
  separately when pausing a cloud PR.
- Required checks protect the merged head; a new code head needs new checks.
- Successful `main` CI and the separately owner-activated deployment process
  are described in [deployment operations](deployment.md).
