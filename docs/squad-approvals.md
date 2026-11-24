# Squad autonomy and owner decisions

## What stays simple

GitHub is the durable work and decision record. Architect plans, the owner
approves the initial plan, and the existing assignment workflow starts Copilot
as the named specialist. Verifier plus up to two applicable specialists review;
the existing PR lifecycle controls readiness and bounded review repairs.
Native Watch and fix owns CI/conflicts; protected GitHub auto-merge alone merges.
Ralph monitors authorized work, not a second assignment or repair loop.

**Owner decision, 2026-11-24:** this policy supersedes bootstrap-only Ralph
restrictions and the earlier requirement to replace a numbered plan for every
scope change. Initial plan approval remains required. Routine choices within
approved outcomes are autonomous. Changed criteria, user-visible behavior or
contracts, security/privacy, cost or dependencies need an explicit owner decision.
An amendment may be substantial; a consolidated replacement plan is optional.
Historical decisions and acceptance evidence remain visible.

This changes development workflows only. Journal API endpoints, MCP tools,
app usage and product concept are unaffected.

## Labels and intake

| Existing/new label | Meaning and migration preview |
|---|---|
| `plan:proposed`, `plan:approved` | Keep: proposed versus approved initial plan; assignment acceptance removes proposed. |
| `squad:architect`, `squad:builder`, `squad:ai`, `squad:devops` | Keep: accountable specialist; not another cloud-executor selector. |
| `priority:p0`, `priority:p1`, `priority:p2` | Add on owner-enrolled intake; one priority per issue, default p2. Never approval. |
| `needs:human`, `handoff:local` | Keep: explicit human pause and local handoff. Neither cancels external running agents. |
| PR-only `review:none` | Keep existing owner/maintainer exception; never bypass CI, human blocks or scope approval. |
| `plan:waived`, `plan:bound` | Obsolete; inventory uses before separately authorized removal. No live deletion in this change. |
| Source, release, phase labels | Keep as descriptive metadata, not automation states. |

On an open issue the owner comments `/triage p1` (or `/triage`, `/triage p0`,
`/triage p2`). The trusted intake sets priority and records enrollment once.
With no existing plan or newer planning questions it posts `/plan` using the
existing agent token. This is deterministic intake, not Architect analysis.
Architect still determines owner, dependencies and missing information. Public
issue text and priority cannot grant coding permission. Direct `/plan` remains
available. Verify that the token account can invoke the planning workflow during
activation; intake acceptance does not prove that the Architect job ran.

## Request an amendment on the current PR

Open a draft PR if necessary. Its description must include a standalone
`Relates to #123` (also supported: Refs, Fixes, Closes, Resolves) for the approved
issue. Use a non-closing reference for partial delivery. The request names the
latest numbered plan's **comment ID**, not its version number or issue number.
The issue must remain open with `plan:approved`.

The owner or an eligible coding/workflow bot posts an **unedited** PR comment:

````markdown
## Scope change request

```json
{
  "issue": 123,
  "plan": 456789,
  "before": "Existing criterion or behavior.",
  "after": "Exact proposed replacement or addition; cite prior amendments if superseding them.",
  "reason": "Why this is needed.",
  "impact": "Acceptance, contracts, security/privacy, cost and dependency impact, including none where applicable.",
  "verification": "Specific checks and acceptance evidence for the changed outcome.",
  "expires": "2026-12-01T12:00:00Z"
}
```
````

Replace example numbers and expiry. Expiry must follow creation by at most
seven days. All fields are required; prose fields are bounded to 4,000 characters
each, total comment to 16,000 UTF-8 bytes. The comment ID is the request ID.
Do not include secrets, journal data or full source code.

Pause the affected change before implementing it. Other authorized work may
continue, but pending, invalid, expired or outdated requests block merge and
new custom continuation. The request does not stop an active cloud session or
native Watch and fix: explicitly tell the current agent to pause that change;
disable Watch and fix separately if it could touch the affected work. Inspect
the actual session; a label or notification is not a cancellation.

## Decide once in GitHub or Teams

The repository owner posts a new, unedited comment:

```text
/approve-change 987654
```

or `/reject-change 987654 optional reason`. Free text, reactions, labels,
agent claims and other users' commands are not decisions. The trusted controller
re-reads the live request and baseline before recording a content-specific
`squad-decision` receipt as `github-actions[bot]`. Eligible bot receipts are the
durable authority; later editing the command does not revoke a recorded decision.
Do not manually forge, edit or delete receipts.

The first admitted valid decision wins. Duplicate responses do not change it.
Pending requests cannot inherit approval after a new numbered plan. An expired
request cannot be approved. To amend an accepted decision, publish another
explicit request naming what it replaces; do not edit the earlier one.
Effective scope is the baseline plus accepted amendments in receipt order.
The controller includes these in review evidence; reviewers verify their tests
and apply explicit supersession rather than treating stale metadata as a defect.
For a later milestone, read the approved amendment links recorded on the issue
and prior merged handoffs, cite them in the new PR's scope and acceptance evidence,
and preserve their explicit changes. The controller admits requests on the
current PR; independent reviewers verify the inherited scope references.

Approval removes only this request's blocker. It does not remove `needs:human`,
reset repair budgets, override trusted human findings, bypass CI/reviews, or
authorize a merge. On an inactive successful cloud PR, the existing lifecycle
claims one continuation and comments to the current Copilot PR. It waits for a
later successful session before readiness; active sessions and local handoffs
are left alone. Local delivery owners consume the same accepted scope themselves.

## Optional personal Teams setup

**Repository adapter available; live activation is separate and off by default.**
Adding a human to `team.md` does not deliver messages. No local notification MCP,
new Azure service, tenant-wide consent or paid connector is installed here.
Initial plan-ready and planning-question cards link to GitHub; initial plan
approval still uses `plan:approved`. Pending amendment cards can be interactive.
Owner-only PR blockers get a link; routine progress stays quiet.

### Start with notification-only delivery

Create an automated cloud flow with the Microsoft Teams trigger **When a Teams
webhook request is received**. For the current GitHub sender, select **Anyone**
under **Who can trigger the flow?**: it sends no Microsoft sign-in token.
The generated URL is a bearer credential; anyone who obtains it can inject
notifications. Protect it as a secret. This inbound setting never authorizes
GitHub decisions.

Add **Post adaptive card in a chat or channel** with **Post as: Flow bot**,
**Post in: Chat with Flow bot**, and yourself as Recipient. In the Adaptive
Card field, use the expression `string(triggerBody()?['card'])`. Save the flow
and send a synthetic notification privately before storing the URL directly
in the repository secret below; never paste it into a conversation or commit.

Keep both feature flags `false` during setup. This starter flow is notification
only; do not enable approvals until authenticated response handling and receipt
confirmation below are configured and tested.

### Add verified response handling

The HTTPS receiver accepts `{ "pr": 123, "card": { ... } }` or
`{ "issue": 123, "card": { ... } }`. Build an owner-managed Teams Workflows /
Power Automate flow for personal chat delivery:

1. Use a secret-protected HTTPS trigger accessible to the trusted GitHub job.
   Do not log its signed URL. Post the supplied Adaptive Card to the owner's
   **personal chat**, using **Post adaptive card and wait for a response** for
   interactive requests. Link-only cards need no callback.
2. Read the actual responder identity from the Teams connector's authenticated
   response metadata, never from card `data`, a user-entered field, or the inbound
   webhook. Compare it to the owner identity in the flow; reject all others.
3. Use a dedicated authenticated GitHub connection whose caller login matches
   the configured transport actor. It dispatches only `squad-decisions.yml`
   on the default branch, sending `pr_number`, `request`, `key`, `decision` and
   the connector-verified `responder`. Request/key/decision come from the returned
   card, but GitHub revalidates the original live request; they grant no authority
   on their own. Never expose the callback credential in a card or client.
4. Wait for the corresponding GitHub Actions run and inspect the durable receipt.
   Replace the Teams card with Approved/Rejected only after receipt confirmation.
   For stale, duplicate or refused responses, show an explicit no-op/refusal and
   link to GitHub; do not display a successful decision just because dispatch
   returned HTTP 204. Disable the old buttons.

The flow is a trusted approval proxy: GitHub verifies its authenticated actor
plus the configured responder match, not a Teams signature. Anyone controlling
that connection can impersonate its assertions. **Do not enable callbacks**
until the connector supports this verified-responder mapping and acceptable
least-privilege origin authentication. Standard GitHub connections may not expose
workflow dispatch; HTTP/custom connectors can need extra licensing or permissions.
Verify available capabilities and obtain owner consent before selecting transport.
If these cannot be verified, keep link-only Teams notifications and GitHub
commands; never substitute an anonymous approval webhook.

Configure names only through repository Actions settings:

| Setting | Purpose |
|---|---|
| Variable `SQUAD_TEAMS_NOTIFICATIONS=true` | Enable outbound delivery; absent/false keeps it off. |
| Secret `SQUAD_TEAMS_WEBHOOK_URL` | HTTPS trigger URL; redirects are refused. |
| Variable `SQUAD_TEAMS_APPROVALS=true` | Independently enable callback admission and interactive buttons. |
| Variable `SQUAD_TEAMS_APPROVAL_ACTOR` | Exact authenticated GitHub caller login of the trusted flow. |
| Secret `SQUAD_TEAMS_APPROVER_ID` | Exact connector-verified owner responder identifier. |
| Existing secret `GH_AW_AGENT_TOKEN` | Existing assignment/lifecycle identity, also dispatches reevaluation. |

The callback connection needs workflow-dispatch capability for this repository;
GitHub permissions are not workflow-granular. Prefer a narrowly scoped dedicated
GitHub App installation, keep its credentials in the owner's connection store,
and explicitly review the resulting privilege. No credentials or identity IDs
belong in source control or Squad memory.

## Failures, recovery and rollout

GitHub approval works without Teams. Each notification has durable attempt/sent
markers, a 15-second timeout and at most three attempts. A successful send means
only endpoint acceptance, not delivery. Failures are explicit and sanitized in
GitHub; uncertain transport is never approval. Repeated reconciliation may retry
within that cap. After exhaustion, inspect the flow and use GitHub; a fresh
request gives a fresh notification identity, not automatic approval.

Reject an invalid, expired or outdated request with `/reject-change <id>` and
post a new immutable request if still needed. If an approved request was deleted,
the retained receipt blocks merge until the owner explicitly rejects that obsolete
record. Check the diff and remove any no-longer-authorized changes. Editing the
request is not a recovery path.

If a continuation claim exists but no later session started, inspect GitHub's
session and the attempted comment before an owner manually comments to the same
Copilot PR. Never delete claims or blindly redispatch; ambiguous API delivery
cannot guarantee exactly-once external execution. Inspect unrelated pauses
separately using the existing recovery procedure.

Before activation, inventory obsolete label uses with `gh issue list --state all
--label plan:waived` and the corresponding `plan:bound` query; record the uses
before any separately approved live removal. No ruleset migration is needed
when `ci` and `Squad Merge Readiness` already protect the default branch.

### Needs human before activation

@cedricmendelin: verify Teams/Power Automate availability, licensing, personal
chat destination, connection permissions, trusted callback actor and authenticated
responder mapping. Configure secrets externally and authorize a synthetic test.
Prove plan-ready delivery, approval, rejection, wrong-responder refusal,
stale-card refusal, duplicate no-op, safe inactive-PR resume and GitHub fallback.
Check the Architect job actually starts from enrolled intake and Watch and fix
eligibility on a synthetic PR. Keep redacted evidence in GitHub.

Disable `SQUAD_TEAMS_APPROVALS` to reject external decisions, and disable
`SQUAD_TEAMS_NOTIFICATIONS` to stop alerts. GitHub commands, pending blockers and
history remain. Disable **Squad Intake** to stop new enrolled triage without
changing existing approved work. Do not remove protections or enable app Agent
merge's Merge action as rollback.

References: [upstream Squad workflow](https://github.github.com/squad/docs/concepts/github-workflow/),
[human members](https://github.github.com/squad/docs/features/human-team-members/),
[coding agent](https://github.github.com/squad/docs/features/copilot-coding-agent/),
[notifications](https://github.github.com/squad/docs/features/notifications/),
[Power Automate cards](https://learn.microsoft.com/en-us/power-automate/create-adaptive-cards).
