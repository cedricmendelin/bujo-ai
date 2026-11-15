'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { BOT, digest, formatRecord, prepareApproval, selectPlan, verify } = require('./plan-approval');

const owner = 'owner';
const issue = 12;
const plan = (id, version, login = owner, updated_at = '2026-01-01T10:00:00Z') => ({
  id, user: { login }, updated_at, body: `## 📋 Implementation plan v${version} · owner: \`devops\`\n\nPlan ${version}`,
});
const label = (id = 20, login = owner, event = 'labeled') => ({
  id, event, actor: { login }, label: { name: 'plan:approved' }, created_at: '2026-01-01T10:00:02Z',
});
const input = () => ({
  issue, owner, sender: owner, comments: [plan(10, 1)],
  labels: ['plan:approved', 'plan:bound'], timeline: [label()],
});
function approved() {
  const data = input();
  const result = prepareApproval(data);
  assert.equal(result.ok, true);
  data.comments.push({ id: 11, user: { login: BOT }, body: formatRecord(result.record) });
  return data;
}

test('accepts an owner plan and a bot plan but ignores untrusted plan authors', () => {
  assert.equal(selectPlan([plan(1, 1, BOT)], owner).plan.version, 1);
  assert.equal(selectPlan([plan(1, 1), plan(2, 2, 'collaborator')], owner).plan.version, 1);
  assert.equal(selectPlan([plan(1, 1, 'other[bot]')], owner).ok, false);
  assert.equal(selectPlan([plan(1, 2), plan(2, 1)], owner).ok, false);
});

test('binds exact plan contents to the latest owner label event', () => {
  const data = approved();
  const result = verify(data);
  assert.equal(result.ok, true);
  assert.equal(result.record.comment, 10);
  assert.equal(result.record.sha256, digest(data.comments[0].body));
  assert.equal(result.record.member, 'devops');
  data.comments[0].body += '\nChanged';
  assert.match(verify(data).reason, /no longer matches/);
});

test('rejects approval of a plan edited in or after the label second', () => {
  for (const updated_at of ['2026-01-01T10:00:02Z', '2026-01-01T10:00:03Z']) {
    const data = input();
    data.comments = [plan(10, 1, owner, updated_at)];
    assert.equal(prepareApproval(data).ok, false);
  }
});

test('rejects non-owner approval, stale labels and forged records', () => {
  const data = input();
  assert.equal(prepareApproval({ ...data, sender: 'collaborator' }).ok, false);
  assert.equal(prepareApproval({ ...data, timeline: [label(20, 'collaborator')] }).ok, false);
  assert.equal(prepareApproval({ ...data, labels: [] }).ok, false);
  const valid = approved();
  valid.comments[1].user.login = owner;
  assert.equal(verify(valid).ok, false);
  valid.comments[1].user.login = BOT;
  valid.timeline.push(label(21, owner, 'unlabeled'));
  assert.equal(verify(valid).ok, false);
});

test('rejects a record copied from a different issue or without a binding label', () => {
  const data = approved();
  assert.equal(verify({ ...data, issue: issue + 1 }).ok, false);
  assert.equal(verify({ ...data, labels: ['plan:approved'] }).ok, false);
});

test('newer or deleted plans and superseded records invalidate the binding', () => {
  const data = approved();
  data.comments.push(plan(12, 2));
  assert.equal(verify(data).ok, false);
  data.comments.pop();
  data.comments.shift();
  assert.equal(verify(data).ok, false);
  const revoked = approved();
  const record = JSON.parse(revoked.comments[1].body.match(/\{.*\}/)[0]);
  record.state = 'superseded';
  revoked.comments[1].body = formatRecord(record);
  assert.equal(verify(revoked).ok, false);
});

test('a malformed newer trusted plan cannot leave an older plan approved', () => {
  const data = approved();
  data.comments.push({ id: 12, user: { login: owner }, body: '## 📋 Implementation plan v2 · owner: unknown' });
  assert.match(verify(data).reason, /malformed/);
  data.comments[2].user.login = 'collaborator';
  assert.equal(verify(data).ok, true);
});

test('reapproval invalidates records from earlier label events', () => {
  const data = approved();
  data.timeline.push(label(21, owner, 'unlabeled'), label(22));
  assert.equal(verify(data).ok, false);
});

test('normalizes line endings consistently', () => {
  assert.equal(digest('  example\r\ntext  '), digest('example\ntext'));
});
