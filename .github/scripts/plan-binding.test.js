'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { BOT, formatRecord, prepareApproval } = require('./plan-approval');
const { linkedPrEligible, reconcileIssue } = require('./plan-binding');

const owner = 'owner';
const issueNumber = 41;
const approvedAt = '2026-01-01T10:00:02Z';
const plan = {
  id: 10, user: { login: owner }, updated_at: '2026-01-01T10:00:00Z',
  body: '## 📋 Implementation plan v1 · owner: `devops`\n\nApproved plan.',
};
const labelEvent = {
  id: 20, event: 'labeled', actor: { login: owner },
  label: { name: 'plan:approved' }, created_at: approvedAt,
};
const pr = (overrides = {}) => ({
  number: 9, state: 'open', body: 'Completion: partial\nRelates to #41\nPlan v1',
  head: { sha: 'abc123' }, labels: [], auto_merge: true,
  closingIssuesReferences: [{ number: issueNumber }],
  base: { repo: { name: 'repo', full_name: 'owner/repo' } },
  ...overrides,
});

function fixture({ bound = true, extraComments = [], prs = [pr()] } = {}) {
  const approvalInput = {
    issue: issueNumber, owner, sender: owner,
    comments: [plan], labels: ['plan:approved'],
    timeline: [labelEvent], now: approvedAt,
  };
  const approval = prepareApproval(approvalInput);
  assert.equal(approval.ok, true);
  const comments = [
    plan,
    { id: 11, user: { login: BOT }, body: formatRecord({ ...approval.record, state: 'assigned' }) },
    ...extraComments,
  ];
  const labels = bound ? ['plan:approved', 'plan:bound'] : ['plan:approved'];
  const calls = [];
  const api = {
    readIssue: async () => ({ labels: [...labels] }),
    listComments: async () => comments,
    listTimeline: async () => [labelEvent],
    findLinkedPullRequests: async () => prs,
    writeRecord: async (number, body) => {
      calls.push(['writeRecord', number, body]);
      comments.push({ id: 100 + comments.length, user: { login: BOT }, body });
    },
    removeLabel: async (number, name) => {
      calls.push(['removeLabel', number, name]);
      labels.splice(labels.indexOf(name), 1);
    },
    commentIssue: async (number, body) => {
      calls.push(['commentIssue', number, body]);
      comments.push({ id: 200 + comments.length, user: { login: BOT }, body });
    },
    commentPr: async (number, body) => calls.push(['commentPr', number, body]),
    unassign: async (number) => calls.push(['unassign', number]),
    disableAutoMerge: async pull => calls.push(['disableAutoMerge', pull.number]),
    setStatus: async (sha, state, description) => calls.push(['setStatus', sha, state, description]),
  };
  return { api, calls, labels, comments };
}

test('a live matching binding publishes success for the exact linked PR head', async () => {
  const { api, calls } = fixture();
  const result = await reconcileIssue(api, { issueNumber, owner });
  assert.equal(result.state, 'bound');
  assert.equal(result.prs[0].ok, true);
  assert.deepEqual(calls.map(call => call[0]), ['setStatus']);
  assert.equal(calls[0][1], 'abc123');
  assert.equal(calls[0][2], 'success');
  assert.match(calls[0][3], /plan v1/);
});

test('edited plan supersedes record, removes both labels, fails binding and disables auto-merge', async () => {
  const data = fixture();
  data.comments[0] = { ...plan, body: `${plan.body}\nEdited` };
  const result = await reconcileIssue(data.api, { issueNumber, owner, now: '2026-01-01T11:00:00Z' });
  assert.equal(result.state, 'revoked');
  assert.deepEqual(data.calls.filter(c => c[0] === 'removeLabel').map(c => c[2]), ['plan:approved', 'plan:bound']);
  assert.ok(data.calls.some(c => c[0] === 'writeRecord' && /"state":"superseded"/.test(c[2])));
  assert.ok(data.calls.some(c => c[0] === 'disableAutoMerge' && c[1] === 9));
  assert.ok(data.calls.some(c => c[0] === 'setStatus' && c[2] === 'failure'));
});

test('duplicate and out-of-order reconciliation is idempotent after revocation', async () => {
  const data = fixture();
  data.comments[0] = { ...plan, body: `${plan.body}\nEdited` };
  await reconcileIssue(data.api, { issueNumber, owner });
  const prior = data.calls.length;
  const result = await reconcileIssue(data.api, { issueNumber, owner });
  assert.equal(result.state, 'unbound');
  assert.equal(data.calls.length, prior);
});

test('migration fails label-only issue approvals closed without affecting waived PRs', async () => {
  const data = fixture({ bound: false, extraComments: [], prs: [
    pr({ labels: ['plan:waived'] }),
    pr({ number: 10, body: 'Relates to #41', labels: [] }),
  ] });
  data.comments.splice(1, 1);
  const result = await reconcileIssue(data.api, { issueNumber, owner });
  assert.equal(result.state, 'migration-required');
  assert.ok(data.calls.some(c => c[0] === 'setStatus' && c[2] === 'failure'));
  assert.ok(!data.calls.some(c => c[0] === 'disableAutoMerge' && c[1] === 9));
});

test('plan-version drift and deleted approval fail closed; merged PR gets owner notice', async () => {
  const data = fixture({ prs: [pr({ number: 11, body: 'Closes #41\nPlan v2' }), pr({
    number: 12, state: 'merged', merged: true, body: 'Closes #41\nPlan v1',
  })] });
  const result = await reconcileIssue(data.api, { issueNumber, owner });
  assert.equal(result.state, 'bound');
  assert.ok(data.calls.some(c => c[0] === 'setStatus' && c[2] === 'failure'));
  data.comments.splice(0, 1);
  const revoked = await reconcileIssue(data.api, { issueNumber, owner });
  assert.equal(revoked.state, 'revoked');
  assert.ok(data.calls.some(c => c[0] === 'commentPr' && c[1] === 12));
});

test('only same-repository issue links qualify and cross-references are honored', () => {
  assert.equal(linkedPrEligible(pr(), issueNumber, owner), true);
  assert.equal(linkedPrEligible(pr({ body: 'Plan v1', closingIssuesReferences: [], base: { repo: { name: 'other', full_name: 'else/other' } } }), issueNumber, owner), false);
  assert.equal(linkedPrEligible(pr({ body: 'Plan v1', closingIssuesReferences: [], timeline: [
    { event: 'cross-referenced', source: { issue: { number: issueNumber } } },
  ] }), issueNumber, owner), true);
});

test('REST issue labels work and invalidation stops every PR before changing discoverable issue state', async () => {
  const data = fixture({ prs: [pr(), pr({ number: 10 })] });
  data.api.readIssue = async () => ({ labels: data.labels.map(name => ({ name })) });
  assert.equal((await reconcileIssue(data.api, { issueNumber, owner })).state, 'bound');
  data.calls.length = 0;
  data.comments[0] = { ...plan, body: `${plan.body}\nEdited` };
  await reconcileIssue(data.api, { issueNumber, owner });
  assert.deepEqual(data.calls.slice(0, 4).map(c => c[0]), [
    'disableAutoMerge', 'setStatus', 'disableAutoMerge', 'setStatus',
  ]);
  assert.deepEqual(data.calls.slice(-2).map(c => c[2]), ['plan:approved', 'plan:bound']);
});

test('safety and cleanup failures retain labels for sweep retry, including failure on a later PR', async () => {
  for (const failAt of ['disableAutoMerge', 'setStatus', 'unassign', 'writeRecord', 'commentIssue', 'removeLabel']) {
    const data = fixture({ prs: [pr(), pr({ number: 10 })] });
    data.comments[0] = { ...plan, body: `${plan.body}\nEdited` };
    const original = data.api[failAt];
    data.api[failAt] = async (...args) => { throw new Error(`transient ${failAt}`); };
    await assert.rejects(reconcileIssue(data.api, { issueNumber, owner }), /transient/);
    assert.ok(data.labels.includes('plan:bound'), `${failAt} must leave sweep discovery intact`);
    if (['disableAutoMerge', 'setStatus'].includes(failAt)) {
      assert.equal(data.calls.some(c => c[0] === 'unassign' || c[0] === 'writeRecord'), false);
    }
    if (failAt === 'disableAutoMerge') {
      assert.equal(data.calls.filter(c => c[0] === 'setStatus' && c[2] === 'failure').length, 2,
        'failed auto-merge disable must not prevent failure statuses on either linked PR');
    }
    data.api[failAt] = original;
    await reconcileIssue(data.api, { issueNumber, owner });
    assert.equal(data.labels.includes('plan:bound'), false, `${failAt} must recover on retry`);
  }
});

test('a failed post-assignment cleanup remains retryable even before the assigned record was published', async () => {
  const data = fixture();
  const { parseRecord } = require('./plan-approval');
  data.comments[1].body = formatRecord({ ...parseRecord(data.comments[1], issueNumber), state: 'approved' });
  data.labels.splice(data.labels.indexOf('plan:approved'), 1);
  data.api.readIssue = async () => ({ labels: [...data.labels], assignees: [{ login: 'copilot-swe-agent[bot]' }] });
  await reconcileIssue(data.api, { issueNumber, owner });
  assert.ok(data.calls.some(c => c[0] === 'unassign'));
  assert.deepEqual(data.labels, []);
});

test('new unbound approvals are not migrated while an approval caller is queued', async () => {
  const data = fixture({ bound: false });
  data.comments.splice(1, 1);
  data.api.approvalPending = async () => true;
  assert.equal((await reconcileIssue(data.api, { issueNumber, owner })).state, 'pending-approval');
  assert.deepEqual(data.calls, []);
  data.api.approvalPending = async () => false;
  assert.equal((await reconcileIssue(data.api, { issueNumber, owner })).state, 'migration-required');
});

test('failure removing the last discovery label is retryable after approval was removed', async () => {
  const data = fixture();
  data.comments[0] = { ...plan, body: `${plan.body}\nEdited` };
  const remove = data.api.removeLabel;
  data.api.removeLabel = async (issue, name) => {
    if (name === 'plan:bound') throw new Error('temporary final cleanup failure');
    return remove(issue, name);
  };
  await assert.rejects(reconcileIssue(data.api, { issueNumber, owner }), /final cleanup failure/);
  assert.deepEqual(data.labels, ['plan:bound']);
  data.api.removeLabel = remove;
  await reconcileIssue(data.api, { issueNumber, owner });
  assert.deepEqual(data.labels, []);
});
