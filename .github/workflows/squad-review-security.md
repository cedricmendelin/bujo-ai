---
name: Squad Review Security
description: Independent Security review of pull requests. Publishes the required "Squad Review / Security" check.
on:
  pull_request:
    types: [opened, ready_for_review, review_requested]
  workflow_dispatch:
    inputs:
      pr_number:
        description: Pull request number to review
        required: true
        type: string
  bots: [Copilot, copilot-swe-agent]
if: >-
  github.event_name == 'workflow_dispatch' ||
  (github.event.action == 'review_requested' && github.event.requested_reviewer.login == github.repository_owner) ||
  (github.event.action != 'review_requested' && !github.event.pull_request.draft && github.event.pull_request.user.login != 'Copilot')
concurrency:
  group: squad-review-security-${{ github.event.pull_request.number || github.event.inputs.pr_number }}
  cancel-in-progress: true
  job-discriminator: ${{ github.event.pull_request.number || github.event.inputs.pr_number }}
engine: copilot
imports:
  - .github/agents/security.agent.md
  - shared/squad-review.md
permissions:
  contents: read
  pull-requests: read
  issues: read
  actions: read
safe-outputs:
  create-check-run:
    name: "Squad Review / Security"
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

# Squad Review — Security

Review scope: authentication/authorization, secrets, privacy, injection, permissions, and supply chain. Findings outside this scope are advisory at most; other
Squad reviewers cover them.

