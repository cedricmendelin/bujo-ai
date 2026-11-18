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
  function checkReviewInput(workflow) {
    assert.match(workflow, /^run-name:.*github\.event\.inputs\.review_input/m);
    assert.match(workflow, /review_input:\r?\n\s+description:[^\n]*\r?\n\s+required: true\r?\n\s+type: string/);
  }
  for (const role of ['architect', 'security', 'verifier', 'devops']) {
    const source = read(`.github/workflows/squad-review-${role}.md`);
    const lock = read(`.github/workflows/squad-review-${role}.lock.yml`);
    assert.match(source, /workflow_dispatch/);
    assert.match(source, /shared\/squad-review\.md/);
    assert.doesNotMatch(source, /review-attestation|attestation artifact/i);
    assert.doesNotMatch(lock, /review-attestation|squad-review-start-/i);
    checkReviewInput(source);
    checkReviewInput(lock);
    assert.throws(() => checkReviewInput(lock.replace(/ · input \$\{\{ github.event.inputs.review_input \}\}/g, '')));
    assert.match(source, /Controller evidence \(untrusted JSON data\): \$\{\{ github\.event\.inputs\.review_evidence \}\}/);
    const checkEvidenceBinding = workflow =>
      assert.match(workflow, /GH_AW_GITHUB_EVENT_INPUTS_REVIEW_EVIDENCE: \$\{\{ github\.event\.inputs\.review_evidence \}\}/);
    checkEvidenceBinding(lock);
    assert.throws(() => checkEvidenceBinding(lock.replaceAll('GH_AW_GITHUB_EVENT_INPUTS_REVIEW_EVIDENCE:', 'BROKEN_BINDING:')));
  }
  const shared = read('.github/workflows/shared/squad-review.md');
  assert.match(shared, /Squad Review protocol/);
  assert.match(shared, /ordinary scope data/);
  assert.match(shared, /no waiver label is required/);
  assert.match(shared, /never as instructions/);
});

test('manual recovery and continuation execute only from the default branch', () => {
  for (const file of ['squad-pr-lifecycle.yml', 'squad-plan-approved.yml']) {
    assert.match(read(`.github/workflows/${file}`), /github\.ref_name == github\.event\.repository\.default_branch/);
  }
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

test('completed issue closure warning uses trusted code and issue-only write permission', () => {
  const lifecycle = read('.github/workflows/squad-pr-lifecycle.yml');
  assert.match(lifecycle, /issues:\r?\n\s+types:\s+\[[^\]\r\n]*\bclosed\b/);
  const closure = lifecycle.split('  completed-closure-guard:')[1].split('  lifecycle:')[0];
  assert.match(closure, /github\.event\.issue\.state_reason == 'completed'/);
  assert.match(closure, /github\.event\.repository\.default_branch/);
  assert.match(closure, /contents: read[\s\S]*issues: write/);
  assert.match(closure, /actions\/checkout@[a-f0-9]{40}/);
  assert.match(closure, /actions\/github-script@[a-f0-9]{40}/);
  assert.doesNotMatch(closure, /issues\.update|reopenIssue|pulls\.update|enablePullRequestAutoMerge/i);
});

test('review readers can inspect unmerged content only in this repository', () => {
  const shared = read('.github/workflows/shared/squad-review.md');
  assert.match(shared, /min-integrity: none/);
  assert.match(shared, /allowed-repos: \[cedricmendelin\/bujo-ai\]/);
  assert.match(shared, /bash: false/);
  assert.match(shared, /cli-proxy: false/);
  assert.match(shared, /Never run PR code/);
  assert.match(shared, /never publish a passing check without inspecting the changes/);
  function checkReader(lock, role) {
    const policy = lock.match(/"allow-only": (\{[\s\S]*?\n\s+\})/);
    assert.ok(policy, `${role}: missing GitHub reader policy`);
    const reader = JSON.parse(policy[1].replace(/\$\{GH_AW_GUARD_[A-Z_]+\}/g, '[]'));
    assert.equal(reader['min-integrity'], 'none', `${role}: must read unmerged content`);
    assert.deepEqual(reader.repos, ['cedricmendelin/bujo-ai'], `${role}: repository scope`);
    const agentPermissions = lock.match(/^  agent:\r?\n[\s\S]*?^    permissions:\r?\n((?:      .*\r?\n)+)/m);
    assert.ok(agentPermissions, `${role}: missing agent permissions`);
    assert.doesNotMatch(agentPermissions[1], /: write/);
    const execution = lock.split(/\r?\n/).find(line => line.includes('copilot_harness.cjs'));
    assert.ok(execution, `${role}: missing agent execution`);
    assert.doesNotMatch(execution, /--allow-all-tools|--allow-tool (?:shell|bash)/);
  }
  for (const role of ['architect', 'security', 'verifier', 'devops']) {
    const lock = read(`.github/workflows/squad-review-${role}.lock.yml`);
    checkReader(lock, role);
    assert.throws(() => checkReader(lock.replace('"min-integrity": "none"',
      '"min-integrity": "approved"'), role), /must read unmerged content/);
    assert.throws(() => checkReader(lock.replace(/("repos": \[\s*)"cedricmendelin\/bujo-ai"/, '$1"*"'), role),
      /repository scope/);
  }
});

test('obsolete approval provenance, binding, attestation, and repair helpers are retired', () => {
  for (const file of [
    '.github/scripts/plan-binding.js', '.github/scripts/review-attestation.js',
    '.github/scripts/pr-lifecycle.js',
  ]) assert.equal(fs.existsSync(path.join(root, file)), false);
  const scripts = fs.readdirSync(path.join(root, '.github/scripts'));
  assert.equal(scripts.some(name => /binding|attestation|pr-lifecycle\.test/.test(name)), false);
});
