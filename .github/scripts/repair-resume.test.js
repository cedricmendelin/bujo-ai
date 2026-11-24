'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { decisionState, REQUEST } = require('./squad-decisions');
const { admitRepairResume } = require('./repair-resume');

const owner = 'owner';
const now = Date.parse('2026-11-24T10:01:00Z');
const baselines = new Map([[1, { approved: true, plan: 10 }]]);

test('a scope request arriving after the initial read blocks resume side effects', async () => {
  const comments = [];
  const request = {
    id: 20,
    body: `${REQUEST}\n\n\`\`\`json\n${JSON.stringify({
      issue: 1, plan: 10, before: 'Old', after: 'New', reason: 'Reason',
      impact: 'Scope impact', verification: 'Focused tests', expires: '2026-11-25T10:00:00Z',
    })}\n\`\`\``,
    user: { login: owner, type: 'User' },
    created_at: '2026-11-24T10:00:00Z',
    updated_at: '2026-11-24T10:00:00Z',
  };
  assert.equal(decisionState(comments, owner, baselines, now).blocked, false);

  const effects = [];
  comments.push(request);
  const admitted = await admitRepairResume({
    loadScope: async () => decisionState(comments, owner, baselines, now),
    onBlocked: async () => effects.push('pause-and-report'),
    resume: async () => effects.push('write-resume-marker', 'clear-human-pause'),
  });

  assert.equal(admitted, false);
  assert.deepEqual(effects, ['pause-and-report']);
});
