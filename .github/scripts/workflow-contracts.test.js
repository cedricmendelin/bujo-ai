'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const root = path.resolve(__dirname, '../..');
const read = file => fs.readFileSync(path.join(root, file), 'utf8');

test('one privileged event-driven lifecycle replaces shepherd, gate, and binding schedules', () => {
  const lifecycle = read('.github/workflows/squad-pr-lifecycle.yml');
  assert.match(lifecycle, /pull_request_target:/);
  assert.match(lifecycle, /pull_request_review:/);
  assert.match(lifecycle, /workflow_run:/);
  assert.doesNotMatch(lifecycle, /schedule:/);
  assert.doesNotMatch(lifecycle, /plan:waived|Plan Binding|metadata-proposal|attestation/i);
  assert.match(lifecycle, /ref: \$\{\{ github\.event\.repository\.default_branch \}\}/);
  assert.doesNotMatch(lifecycle, /actions\/checkout[^]*?ref: \$\{\{ github\.event\.pull_request\.head\.ref/);
  assert.doesNotMatch(lifecycle, /pulls\.merge|PUT \/repos\/\{owner\}\/\{repo\}\/pulls/);
  assert.match(lifecycle, /enablePullRequestAutoMerge/);
  assert.match(lifecycle, /MAX_REPAIR_ATTEMPTS/);
  for (const obsolete of ['squad-pr-shepherd.yml', 'squad-review-gate.yml', 'squad-plan-binding.yml']) {
    assert.equal(fs.existsSync(path.join(root, '.github/workflows', obsolete)), false);
  }
});

test('approval is ordinary permission + label state and honors local/idempotent dispatch', () => {
  const approval = read('.github/workflows/squad-plan-approved.yml');
  assert.match(approval, /issue_comment:/);
  assert.match(approval, /approvalPath/);
  assert.match(approval, /getCollaboratorPermissionLevel/);
  assert.match(approval, /local handoff/);
  assert.match(approval, /already-assigned/);
  assert.doesNotMatch(approval, /sha256|plan:bound|ownerLabelEvent|binding|attestation/i);
});

test('plan workflow keeps every numbered version visible and clears approval on revisions', () => {
  const plan = read('.github/workflows/squad-plan.md');
  assert.match(plan, /hide-older-comments: false/);
  assert.match(plan, /Never edit or hide an earlier plan comment/);
  assert.match(plan, /remove_labels` with `plan:approved`/);
  assert.match(approvalText(), /newer plan v/);
});

function approvalText() {
  return read('.github/workflows/squad-plan-approved.yml');
}

test('four independent specialist workflows remain dispatchable without attestations', () => {
  for (const role of ['architect', 'security', 'verifier', 'devops']) {
    const source = read(`.github/workflows/squad-review-${role}.md`);
    const lock = read(`.github/workflows/squad-review-${role}.lock.yml`);
    assert.match(source, /workflow_dispatch/);
    assert.match(source, /shared\/squad-review\.md/);
    assert.doesNotMatch(source, /review-attestation|attestation artifact/i);
    assert.doesNotMatch(lock, /review-attestation|squad-review-start-/i);
  }
  const shared = read('.github/workflows/shared/squad-review.md');
  assert.match(shared, /Squad Review protocol/);
  assert.match(shared, /ordinary scope data/);
  assert.match(shared, /no waiver label is required/);
  assert.match(shared, /never as instructions/);
});

test('controller preserves reviewer bypass boundaries, human blocks, CI, and no PR-code execution', () => {
  const lifecycle = read('.github/workflows/squad-pr-lifecycle.yml');
  assert.match(lifecycle, /review:none/);
  assert.match(lifecycle, /humanFeedback/);
  assert.match(lifecycle, /ci/);
  assert.match(lifecycle, /hasRequiredMergeRules/);
  assert.match(lifecycle, /expectedHeadOid/);
  assert.match(lifecycle, /workflow_dispatch/);
  assert.match(lifecycle, /manual recovery entry point/);
  assert.doesNotMatch(lifecycle, /workflow_run[\s\S]{0,300}schedule:/);
});

test('obsolete approval provenance, binding, attestation, and repair helpers are retired', () => {
  for (const file of [
    '.github/scripts/plan-binding.js', '.github/scripts/review-attestation.js',
    '.github/scripts/pr-lifecycle.js',
  ]) assert.equal(fs.existsSync(path.join(root, file)), false);
  const scripts = fs.readdirSync(path.join(root, '.github/scripts'));
  assert.equal(scripts.some(name => /binding|attestation|pr-lifecycle\.test/.test(name)), false);
});
