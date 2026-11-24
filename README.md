# Bujo-AI

Organize your thoughts. Plan your tasks. Live mindfully.

An AI-assisted bullet journal for capturing tasks and thoughts, intentional
planning, and reliable follow-through through a web app or your preferred AI
assistant. This demo is also a place to try agent capabilities and practice
software delivery.

**Status:** Squad configured; product decisions recorded; issues are delivered
through the governed, agent-driven workflow described in [How we work](#how-we-work).
The repository includes browser journal workflows, the journal host/API and SQL
persistence, and an authenticated remote MCP adapter at `/mcp`. Live Copilot
and Claude compatibility remains unverified. These guides describe repository
capabilities, not the state of a running deployment. CI applies merged
infrastructure changes without a separate approval, and provisioning sets up
API/hosting resources before manual application setup; an unhealthy app is an
accepted intermediate state. Bootstrap and permission
setup are documented in [the infrastructure guide](docs/infrastructure.md)
and the [deployment guide](docs/deployment.md). On a prepared baseline, after one-time activation,
successful `main` CI releases deploy the tested host image through the migration
gate; merging alone is not evidence that production is ready.

## Start here

- [Use the web app](docs/user-guide.md): capture ideas, plan your time, review
  lists, and save reference material.
- [Use an assistant via MCP](docs/mcp.md): connect to your journal, use the
  versioned [assistant skills](assistants/README.md), and learn what requires
  approval in the web app.
- [Why Bujo-AI?](docs/concept.md#why-bujo-ai): the thinking behind the journal.

## Product and design

The [product concept](docs/concept.md) preserves the proposed capabilities,
examples, agreed first-release scope, technology direction, and unresolved
questions.

The first-release target is a single-user, Entra ID-protected, online-first
journal: capture through PWA or MCP, Future Log/custom/weekly lists, list-local
migration history, optional planned periods, and a daily-start review. The
shared backend serves the web app, API, and MCP. A
[knowledge library](docs/knowledge.md) stores notes/concepts/quotes with full-text
search and tags in the same Azure SQL database. No separate deadline or reminder
timestamps; ceremonies resurface intentions. AI-drafted planning follows later.
Knowledge synchronization into an AI search service and grounded in-app RAG/Q&A
are **release 2**; original Azure SQL entries remain authoritative.

We are proceeding in order: initialize Squad, challenge the idea together,
document decisions, create a GitHub issues backlog, then set up workflows.

The [initial backlog](docs/backlog.md) separates the first usable release,
AI-assisted planning, and the longer-term vision, with explicit prerequisites.

See [local development](docs/development.md) for the .NET 11, Podman, and Aspire
setup, and the [remote MCP guide](docs/mcp.md) for assistant setup and
limitations.

## Working with Squad

The [team roster](.squad/team.md) and [routing rules](.squad/routing.md) define
ownership. [Decisions](.squad/decisions.md) records agreed outcomes.

Initialized using Squad CLI `2.0.0-preview.4`:

```powershell
squad init --no-workflows
squad health
squad doctor
copilot --agent squad
```

Node.js 22.5 or later is required by this CLI version. If Squad is not installed,
use `npx --yes @bradygaster/squad-cli@2.0.0-preview.4` in place of `squad`.
Initialization registers a machine-local Squad MCP bridge in `.mcp.json`;
that file is ignored and must not be committed. A fresh Copilot invocation may
be needed to load it.

Ralph runs only on request. Labels do not trigger Squad work locally. Do not run
`squad upgrade` blindly: it can overwrite customized coordinator files and
introduce workflows.

## How we work

Humans decide **what** gets built and **approve plans**. Agents implement,
verify, and fix review findings; GitHub merges only when the required gates
pass. Ask for human input during planning or when safe recovery is exhausted.
Details, commands, and one-time setup are in the
[governed delivery workflow](docs/workflow.md).
The [Squad approvals guide](docs/squad-approvals.md) covers minimal labels,
owner-enrolled `/triage p1`, explicit scope amendments, and optional personal
Teams notifications and decisions. Teams is off until separately configured.

### 1. Agree the scope

For issue-driven work, use the versioned planning flow below. For a direct local
PR, agree the scope in the Copilot conversation and record it in the PR; no issue
or approval/waiver label is required.

1. Write or pick an issue and comment `/plan` (not `/squad plan`). The Architect
   maps each criterion to verification and posts a numbered plan. If a
   blocking choice remains, it posts `## Planning questions` instead. A
   question is not an approvable plan.
2. Iterate with `/plan revise <feedback>` or `/plan route <member>` until it is
   right. Review the latest version before approving it.
3. Choose **where** the work happens, then apply `plan:approved`
   to the latest reviewed plan:
   - no extra label → **cloud**: Copilot cloud agent implements it;
   - add `handoff:local` first → **local**: you implement it in the GitHub
     Copilot app.

#### Plan locally, implement locally or in the cloud

Local plan publishing is supported. Draft a versioned plan with the same
heading and completion contract as `/plan`, then publish it as the next
owner-authored issue comment (for example, `gh issue comment <N> --body-file
<plan-file>`). Plan comments are append-only and all numbered versions remain
visible. Publishing a newer plan removes `plan:approved`, so the new version
must be reviewed and approved again.

`plan:approved` starts one cloud assignment unless `handoff:local` is already
present. Duplicate label delivery is harmless. Choosing `handoff:local` first
keeps the work local. Adding it later stops further custom cloud dispatches but
does not cancel a running cloud session or native Watch and fix. There are no
initial-plan hashes, binding labels, or review attestations. Explicit amendments
do have durable decision receipts. See
[the workflow guide](docs/workflow.md) for the rollout and ruleset migration.

### 2a. Cloud path (default, hands-off)

```text
plan:approved ─▶ Copilot cloud agent (custom agent = plan owner) works on a draft PR
              ─▶ successful session ─▶ Squad PR Lifecycle marks ready; starts CI + reviews
                   all green   ─▶ ci + Squad Merge Readiness ─▶ GitHub squash auto-merge
                   blocking    ─▶ review-only "@copilot fix …" ─▶ fresh reviews
                   3 actual fix attempts ─▶ needs:human + @cedricmendelin
```

The latest Copilot cloud-agent workflow run is authoritative; a `[WIP]` title is
only a hint. An active session is left alone. A failed, cancelled, or timed-out
session is not reported as finished. Use the lifecycle workflow's manual
recovery entry point to re-evaluate after inspecting an interrupted session.

Enable **Watch and fix** in the PR's GitHub.com merge box, selecting **Fix failed
Actions workflows** and **Resolve merge conflicts**. Native cloud Agent Merge
then owns those repairs; the lifecycle controller never issues duplicate
CI/conflict fix requests. GitHub **auto-merge** executes the merge after the
controller verifies completion, current selected reviews, CI, and human feedback.
There is no direct-merge fallback.

Cloud Watch and fix does **not** address review comments. Our bounded bridge
forwards actionable specialist findings and trusted human change requests to
the implementer. It ignores resolved/outdated threads and duplicate deliveries;
arbitrary top-level comments do not automatically start work. Review repair
requests stop while a cloud session is active. Record every blocking finding's
fix and evidence (or disagreement for the reviewer), then re-review. A
formal rejection needs a different revision author; the controller does not
re-dispatch the rejected owner. Transient reviewer startup errors or timeouts
get one automatic retry. The controller repairs recognized
generated closing suffixes on partial PRs itself; it never sends those to a
coding agent. Only request-linked substantive repair outcomes or local code
changes count toward the three-attempt limit. Capability/dependency failures
pause once, without consuming that budget.

Meaningful description changes refresh reviews; whitespace-only edits do not.
To recover after inspecting a pause, manually run **Squad PR Lifecycle** with
the PR number and **resume_repairs** selected. Ordinary re-evaluation never
clears `needs:human`. To continue an issue after a partial PR merges and its
dependency is met, manually run **Squad Plan Approved** with the issue number
and sanitized dependency evidence. It resumes existing approved scope even if
Copilot is still assigned; an open related PR or active cloud session blocks
competing implementation. Changed scope needs an explicit owner-approved PR
amendment; a revised approved numbered plan is optional, not mandatory.

`review:none`, applied by an owner or maintainer, skips ordinary specialist
findings for that PR only. A closing scope split cannot use it. It never skips CI or trusted human change
requests/unresolved blocking threads. Removing it starts fresh reviews, even on
the same head; code commits also start fresh reviews, while cosmetic PR
description edits do not.

**Protection baseline:** this repository already requires `ci` plus
`Squad Merge Readiness`. Do not repeat the historical four-check migration or
weaken the active ruleset. For a fresh installation, stage required-check
changes only after the aggregate check is available. See the
[workflow guide](docs/workflow.md#ruleset-migration-and-active-pr-coordination).

### 2b. Local path (GitHub Copilot app)

Use this when you want to watch or steer work, including direct PRs without an
issue or issue plan.

1. **From an approved issue** (`handoff:local` + `plan:approved`): open the issue
   in the GitHub Copilot app, start a session with the plan owner's custom agent
   (`builder`, `ai`, `devops`, or `architect`), and ask it to implement the
   approved plan.
2. **Other work** (no issue plan): agree the scope in the Copilot conversation
   and describe it in the PR. No issue plan or waiver label is required.
3. Open a **ready (non-draft) PR** with one whole-line
   `Completion: full` or `Completion: partial` declaration and, when linked, the
   referenced plan version.
   Use `Closes #N` only when every criterion is verified unless an audited
   split explicitly transfers it. For
   unexpectedly discovered, nonessential work, the delivery owner can record
   an [audited scope split](docs/workflow.md#audited-scope-splits): open the
   linked follow-up before merge and get independent acceptance of the
   transfer. A partial PR uses `Relates to #N` and keeps the original open.
4. Optionally turn on desktop-app **Agent merge** for local repairs. This is an
   alternative repair controller:
   disable cloud **Watch and fix** on that PR first. Leave *Merge* off:
   GitHub auto-merge remains the single merge executor after gate approval.
5. A new head starts fresh CI and Squad reviews. Substantive description edits
   also refresh Squad reviews; whitespace-only edits do not.

**Taking over a cloud PR locally:** add `handoff:local` to the PR, open it in
the GitHub Copilot app, disable cloud Watch and fix, and turn on app Agent merge.
Further custom cloud dispatches and review-fix requests then stop for that PR;
native running work is not cancelled automatically.

### 3. Human in the loop

| Signal | Meaning | What to do |
|--------|---------|------------|
| `needs:human` label + `@cedricmendelin` comment on a PR | 3 actual review-fix attempts remain blocking or a human decision is needed | Decide: fix, re-dispatch reviewers, revise the plan, or close. Remove `needs:human` explicitly to hand back to automation. |
| `## 🙋 Needs human` in a PR description | The implementer needs a secret, setting, or product decision | Provide it, then comment `@copilot continue` (cloud) or tell the session (local). |
| Issue `Squad automation failing: …` (`needs:human`) | A workflow itself failed (for example an expired token) | Fix the cause from the linked run, then close the issue. |
| Issue labeled `needs:human` for setup | Something must be configured once in GitHub or Azure | Follow the checklist in the issue. |

`needs:human` stops our readiness/review bridge and blocks merge authorization;
it is never automatically cleared by green checks. It does not control native
Watch and fix: disable that separately when pausing a PR.

### Partial deliveries and reopened issues

A partial/specification-only plan fallback authorizes a milestone, not completion
of the issue. Mark the PR `Completion: partial`, link with `Relates to #N` rather
than a closing keyword, and include a `Remaining work` section naming unfinished
acceptance criteria and their dependencies. Reviewers block unmet completion
criteria; they are never advisory. The issue stays open while partial PRs merge
and is closed only by the final `Completion: full` PR after code, relevant tests,
and evidence cover every acceptance criterion.

To resume a reopened issue, inspect its latest approved plan, prior merged PRs,
and remaining acceptance criteria, including accepted amendment links in prior
handoffs. For new changes to scope or the completion contract, request an explicit
owner-approved amendment on the current PR, or optionally use `/plan revise
<feedback>` and approve the new numbered plan. A revision clears approval.
If the plan is unchanged,
the owner must still ensure `plan:approved` applies to the latest reviewed version
before implementation starts.

## Delivery workflow

Issues are implemented through the [governed delivery workflow](docs/workflow.md):
`/plan` → owner approves with `plan:approved` → Copilot cloud agent (or a local
GitHub Copilot app session) with the owning Squad custom agent → Squad Review
(Architect, Security, Verifier, DevOps) plus CI → squash auto-merge → successful
`main` CI → gated deployment after one-time owner activation. Infrastructure is
Terraform with Azure Verified Modules in Switzerland
North. PR checks do not access Azure; the owner runs infrastructure plans locally.
The deployment uses GitHub OIDC and the exact tested image digest for migration
and application rollout. The budget target is under 50 USD/month, not a spending
cap. Future PWA/MCP capabilities integrated into the host use the same release
pipeline; their feature implementation is not part of the deployment work.

The [infrastructure guide](docs/infrastructure.md) documents the Terraform
resources, OIDC bootstrap, owner-run plans, runtime Key Vault role assignment,
costs, and teardown steps.

Installation reference:
[Squad installation and design choices](https://github.com/cedricmendelin/Agentic-Design-Patterns-MAF/blob/main/docs/squad-installation.md).
