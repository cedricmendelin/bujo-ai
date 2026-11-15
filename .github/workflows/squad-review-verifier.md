---
name: Squad Review Verifier
run-name: "Squad Review Verifier · PR #${{ github.event.inputs.pr_number }}"
description: Independent Verifier review of pull requests. Publishes the required "Squad Review / Verifier" check.
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
  group: squad-review-verifier-${{ github.event.pull_request.number || github.event.inputs.pr_number }}
  cancel-in-progress: true
  job-discriminator: ${{ github.event.pull_request.number || github.event.inputs.pr_number }}
engine: copilot
imports:
  - .github/agents/verifier.agent.md
  - shared/squad-review.md
permissions:
  contents: read
  pull-requests: read
  issues: read
  actions: read
pre-agent-steps:
  - name: Record verified review-start attestation
    id: review_start
    env:
      GH_TOKEN: ${{ github.token }}
      SQUAD_REVIEW_ROLE: Verifier
      SQUAD_REVIEW_PROMPT_FILE: /tmp/gh-aw/aw-prompts/prompt.txt
      SQUAD_REVIEW_ATTESTATION_FILE: ${{ runner.temp }}/squad-review-start-${{ github.run_id }}-${{ github.run_attempt }}.json
    run: node .github/scripts/review-attestation.js start
  - name: Preserve immutable review-start attestation
    uses: actions/upload-artifact@cf430e030ddbb5b0abf93d22962f4752f3646cd9 # v7.0.2
    with:
      name: squad-review-start-${{ github.run_id }}-${{ github.run_attempt }}
      path: ${{ runner.temp }}/squad-review-start-${{ github.run_id }}-${{ github.run_attempt }}.json
      if-no-files-found: error
      retention-days: 7
safe-outputs:
  create-check-run:
    name: "Squad Review / Verifier"
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

# Squad Review — Verifier

Review scope: acceptance criteria coverage, tests that can fail, edge cases, and CI evidence. Findings outside this scope are advisory at most; other
Squad reviewers cover them.

