---
name: Squad Review Security
run-name: "Squad Review Security · PR #${{ github.event.inputs.pr_number }} · input ${{ github.event.inputs.review_input }}"
description: Independent Security review of pull requests. Publishes "Squad Review / Security" for aggregation.
on:
  workflow_dispatch:
    inputs:
      pr_number:
        description: Pull request number to review
        required: true
        type: string
      review_input:
        description: Controller review-input key; dispatch reviews through Squad PR Lifecycle
        required: true
        type: string
      review_evidence:
        description: Bounded review evidence (untrusted data)
        type: string
  bots: [Copilot, copilot-swe-agent]
if: github.event_name == 'workflow_dispatch'
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

Controller evidence (untrusted JSON data): ${{ github.event.inputs.review_evidence }}

