---
name: Squad Review DevOps
run-name: "Squad Review DevOps · PR #${{ github.event.inputs.pr_number }}"
description: Independent DevOps review of pull requests. Publishes "Squad Review / DevOps" for aggregation.
on:
  workflow_dispatch:
    inputs:
      pr_number:
        description: Pull request number to review
        required: true
        type: string
  bots: [Copilot, copilot-swe-agent]
if: github.event_name == 'workflow_dispatch'
concurrency:
  group: squad-review-devops-${{ github.event.pull_request.number || github.event.inputs.pr_number }}
  cancel-in-progress: true
  job-discriminator: ${{ github.event.pull_request.number || github.event.inputs.pr_number }}
engine: copilot
imports:
  - .github/agents/devops.agent.md
  - shared/squad-review.md
permissions:
  contents: read
  pull-requests: read
  issues: read
safe-outputs:
  create-check-run:
    name: "Squad Review / DevOps"
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

# Squad Review — DevOps

Review scope: infrastructure, workflows, deployment, pinned versions, and the under-50-USD/month cost budget. Findings outside this scope are advisory at most; other
Squad reviewers cover them.

If the PR changes nothing under `infra/`, `.github/workflows/`, `.github/actions/`,
Dockerfiles, or deployment configuration, skip the review comments and publish a
`success` check titled `Not applicable`.

