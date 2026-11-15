# Bujo-AI

Organize your thoughts. Plan your tasks. Live mindfully.

An AI-assisted bullet journal for capturing tasks and thoughts, intentional
planning, and reliable follow-through through a web app or your preferred AI
assistant.

**Status:** Squad configured; product decisions recorded; issues are delivered
through the governed, agent-driven workflow described in [How we work](#how-we-work).
The application workload is not yet provisioned; its manually bootstrapped Azure
prerequisites and deployment workflow are documented in
[the infrastructure guide](docs/infrastructure.md).

## Product and design

The [product concept](docs/concept.md) preserves the proposed capabilities,
examples, agreed first-release scope, technology direction, and unresolved
questions.

The first release is a single-user, Entra ID-protected, online-first journal:
capture through PWA or MCP, Future Log/custom/weekly lists, list-local migration
history, optional planned periods, and a daily-start review. A
[knowledge library](docs/knowledge.md) stores notes/concepts/quotes with full-text
search and tags in the same Azure SQL database. No separate deadline or reminder
timestamps; ceremonies resurface intentions. AI-drafted planning follows later.
Knowledge synchronization into an AI search service and grounded in-app RAG/Q&A
are **release 2**; original Azure SQL entries remain authoritative.

We are proceeding in order: initialize Squad, challenge the idea together,
document decisions, create a GitHub issues backlog, then set up workflows.

The [initial backlog](docs/backlog.md) separates the first usable release,
AI-assisted planning, and the longer-term vision, with explicit prerequisites.

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

Humans decide **what** gets built and **approve plans**; agents build, review,
fix, and merge. A human (currently @cedricmendelin) is only pulled in when the
automation cannot continue. Details, commands, and one-time setup are in the
[governed delivery workflow](docs/workflow.md).

### 1. Plan (always)

1. Write or pick an issue. Comment `/plan` (not `/squad plan`): the Architect
   posts `## 📋 Implementation plan vN · owner: <member>` and labels
   `plan:proposed`. No label is needed to draft or revise a plan.
2. Iterate with `/plan revise <feedback>` or `/plan route <member>` until it is
   right. Review the latest version before approving it.
3. Choose **where** the work happens, then apply `plan:approved` (owner only)
   to the latest reviewed plan:
   - no extra label → **cloud**: Copilot cloud agent implements it;
   - add `handoff:local` first → **local**: you implement it in the GitHub
     Copilot app.

#### Proposed: plan locally, implement locally or in the cloud

This is a **proposed workflow, not yet supported**. Today the approval
automation only accepts plan comments posted by `github-actions[bot]`; posting a
local plan as the owner and applying `plan:approved` will not hand it off.

The intended flow is:

1. Draft and revise the plan in a local Copilot session, using the same
   versioned heading, owner, completion contract and acceptance checks as
   `/plan`. Review and approve the exact text locally.
2. Publish that text as a new versioned issue comment, then select the
   implementation path: apply `handoff:local` first for local work, or leave
   it off for cloud work. The repository owner applies `plan:approved` to the
   published version. Publishing alone must not start implementation.
3. For cloud work, the existing handoff assigns Copilot with the plan owner's
   custom agent; for local work, open the issue in the Copilot app. Both paths
   use the same published, approved plan and existing review and completion
   rules.
4. To change the plan, publish a new version and obtain fresh owner approval
   before continuing. Approval must not silently carry over to an edited or
   newer plan.

Implementing this requires changes to the approval workflow to accept trusted
owner-authored plans as well as existing bot-authored plans, while retaining the
owner-only approval check and `/plan` compatibility. It must bind approval to
the exact issue, comment, version and content, invalidate it on edits or newer
plans, and verify that binding at assignment and review time, including
concurrent events. The current label and newest-comment checks alone do not
provide that binding. Automated actions using the owner's token appear as the
owner, so author identity alone is not evidence of a separate human approval.

Update the shared review protocol, agent instructions, this guide and
`docs/workflow.md` to use the same approved-plan rules; regenerate affected
gh-aw lock files and add focused tests for authorization, revisions, stale
approval and both handoff paths. Changes to `.github/workflows/` and
`.github/agents/` require an owner-approved implementation plan that
explicitly lists those files.

### 2a. Cloud path (default, hands-off)

```text
plan:approved ─▶ Copilot cloud agent (custom agent = plan owner) works on a draft PR
              ─▶ session ends ─▶ Squad PR Shepherd marks PR ready, starts CI + Squad Review
              ─▶ Squad Review Gate
                   all green   ─▶ squash auto-merge ─▶ main ─▶ deploy
                   blocking    ─▶ "@copilot fix …" ─▶ Copilot fixes ─▶ shepherd re-reviews
                   3 rounds / stuck / conflicts unresolved ─▶ needs:human + @cedricmendelin
```

Copilot also resolves merge conflicts with `main` when asked by the shepherd,
and you can steer it at any time with an `@copilot …` comment on the PR.

### 2b. Local path (GitHub Copilot app)

Use this when you want to watch or steer the work, or for work that is not an
issue plan.

1. **From an approved issue** (`handoff:local` + `plan:approved`): open the issue
   in the GitHub Copilot app, start a session with the plan owner's custom agent
   (`builder`, `ai`, `devops`, or `architect`), and ask it to implement the
   approved plan.
2. **Other work** (no issue plan): describe it in a session; the owner applies
   `plan:waived` to the PR so reviewers use the PR description as the scope.
3. The session opens a **ready (non-draft) PR** with one whole-line
   `Completion: full` or `Completion: partial` declaration and the plan version.
   Use `Closes #N` only when every acceptance criterion is delivered with
   evidence, including code and relevant tests. A partial PR uses `Relates to #N`
   (or another non-closing reference) and keeps the issue open.
4. Turn on **Agent merge** for the session with *Address reviews*, *Fix CI*, and
   *Resolve conflicts*. Leave *Merge* off: the gate enables squash auto-merge
   once every check passes.
5. Every push re-runs CI and Squad Review; the gate posts blocking findings for
   Agent merge to fix (max 3 rounds).

**Taking over a cloud PR locally:** add `handoff:local` to the PR, open it in
the GitHub Copilot app, and turn on Agent merge. The shepherd and `@copilot`
fix requests then stop for that PR.

### 3. Human in the loop

| Signal | Meaning | What to do |
|--------|---------|------------|
| `needs:human` label + `@cedricmendelin` comment on a PR | 3 failed fix rounds, Copilot stopped without fixing, unresolved conflicts, or a reviewer did not report | Decide: fix, re-dispatch reviewers, revise the plan, or close. Remove `needs:human` to hand back to automation. |
| `## 🙋 Needs human` in a PR description | The implementer needs a secret, setting, or product decision | Provide it, then comment `@copilot continue` (cloud) or tell the session (local). |
| Issue `Squad automation failing: …` (`needs:human`) | A workflow itself failed (for example an expired token) | Fix the cause from the linked run, then close the issue. |
| Issue labeled `needs:human` for setup | Something must be configured once in GitHub or Azure | Follow the checklist in the issue. |

### Partial deliveries and reopened issues

A partial/specification-only plan fallback authorizes a milestone, not completion
of the issue. Mark the PR `Completion: partial`, link with `Relates to #N` rather
than a closing keyword, and include a `Remaining work` section naming unfinished
acceptance criteria and their dependencies. Reviewers block unmet completion
criteria; they are never advisory. The issue stays open while partial PRs merge
and is closed only by the final `Completion: full` PR after code, relevant tests,
and evidence cover every acceptance criterion.

To resume a reopened issue, inspect its latest approved plan, prior merged PRs,
and remaining acceptance criteria. If scope or the completion contract changed,
comment `/plan revise <feedback>`, review the new version, and have the owner
apply `plan:approved` again; a revision clears approval. If the plan is unchanged,
the owner must still ensure `plan:approved` applies to the latest reviewed version
before implementation starts.

## Delivery workflow

Issues are implemented through the [governed delivery workflow](docs/workflow.md):
`/plan` → owner approves with `plan:approved` → Copilot cloud agent (or a local
GitHub Copilot app session) with the owning Squad custom agent → Squad Review
(Architect, Security, Verifier, DevOps) plus CI → squash auto-merge → deploy from
`main`. Infrastructure is Terraform with Azure Verified Modules in Switzerland
North. PR checks do not access Azure; the owner runs infrastructure plans locally.
The `main` deployment uses GitHub OIDC, budgeted under 50 USD/month.

The [infrastructure guide](docs/infrastructure.md) documents the Terraform
resources, OIDC bootstrap, owner-run plans, runtime Key Vault role assignment,
costs, and teardown steps.

Installation reference:
[Squad installation and design choices](https://github.com/cedricmendelin/Agentic-Design-Patterns-MAF/blob/main/docs/squad-installation.md).
