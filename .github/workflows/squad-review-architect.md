---
name: Squad Review Architect
description: Independent Architect review of pull requests. Publishes the required "Squad Review / Architect" check.
on:
  pull_request:
    types: [opened, synchronize, reopened, ready_for_review, review_requested]
  workflow_dispatch:
    inputs:
      pr_number:
        description: Pull request number to review
        required: true
        type: string
  bots: [Copilot, copilot-swe-agent]
if: >-
  github.event_name == 'workflow_dispatch' ||
  (!github.event.pull_request.draft &&
  (!startsWith(github.event.pull_request.head.ref, 'copilot/') || contains(github.event.pull_request.labels.*.name, 'handoff:local')) &&
  (github.event.action != 'review_requested' || github.event.requested_reviewer.login == github.repository_owner))
concurrency:
  group: squad-review-architect-${{ github.event.pull_request.number || github.event.inputs.pr_number }}
  cancel-in-progress: true
  job-discriminator: ${{ github.event.pull_request.number || github.event.inputs.pr_number }}
engine: copilot
imports:
  - .github/agents/architect.agent.md
  - shared/squad-review.md
permissions:
  contents: read
  pull-requests: read
  issues: read
  actions: read
safe-outputs:
  create-check-run:
    name: "Squad Review / Architect"
    target: "*"
    max: 1
  create-pull-request-review-comment:
    max: 10
    target: "*"
  submit-pull-request-review:
    max: 1
    target: "*"
    allowed-events: [COMMENT]
timeout-minutes: 30
---

# Squad Review — Architect

Review scope: plan conformance, scope, architecture, domain semantics, and recorded decisions. Findings outside this scope are advisory at most; other
Squad reviewers cover them.

