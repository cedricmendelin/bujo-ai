'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { approvalPath, isEligiblePermission, latestPlan, parsePlan } = require('./plan-approval');

const owner = 'cedricmendelin';
const plan = (version, id, login = owner, member = 'devops') => ({
  id, user: { login }, body: `## 📋 Implementation plan v${version} · owner: ${member}\n\nScope`,
});

test('reads owner- or workflow-authored numbered plans and preserves their exact comment', () => {
  const old = plan(1, 1);
  const next = plan(2, 2, 'github-actions[bot]', 'architect');
  const result = latestPlan([old, next], owner);
  assert.equal(result.ok, true);
  assert.equal(result.plan.comment, next);
  assert.equal(result.plan.version, 2);
  assert.equal(result.plan.member, 'architect');
  assert.equal(parsePlan(plan(3, 3, 'stranger'), owner), null);
});

test('rejects gaps and duplicate version numbers rather than hiding plan history', () => {
  assert.equal(latestPlan([plan(1, 1), plan(3, 3)], owner).ok, false);
  assert.equal(latestPlan([plan(1, 1), plan(1, 2)], owner).ok, false);
  assert.equal(latestPlan([plan(2, 1)], owner).ok, false);
  assert.equal(latestPlan([plan(1, 1), { ...plan(2, 2), body: '## 📋 Implementation plan v2 · owner: unknown\nbad' }], owner).ok, false);
});

test('approval dispatch is idempotent and local handoff always wins', () => {
  assert.deepEqual(approvalPath({ labels: [] }), { action: 'none' });
  assert.deepEqual(approvalPath({ labels: ['plan:approved', 'handoff:local'] }), { action: 'local' });
  assert.deepEqual(approvalPath({ labels: ['plan:approved'], assignees: [{ login: 'copilot-swe-agent[bot]' }] }),
    { action: 'already-assigned' });
  assert.deepEqual(approvalPath({ labels: ['plan:approved'], assignees: [{ login: 'Copilot', type: 'Bot' }] }),
    { action: 'already-assigned' });
  assert.deepEqual(approvalPath({ labels: ['plan:approved'], assignees: [{ login: 'Copilot', type: 'User' }] }),
    { action: 'assign-cloud' });
  assert.deepEqual(approvalPath({ labels: ['plan:approved'] }), { action: 'assign-cloud' });
});

test('approval uses ordinary repository write roles rather than provenance evidence', () => {
  assert.equal(isEligiblePermission('admin'), true);
  assert.equal(isEligiblePermission('maintain'), true);
  assert.equal(isEligiblePermission('write'), true);
  assert.equal(isEligiblePermission('triage'), false);
  assert.equal(isEligiblePermission('read'), false);
});
