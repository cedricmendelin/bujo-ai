'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { intake } = require('./squad-intake');

function fixture() {
  const time = new Date().toISOString();
  const comments = [{ id: 1, body: '/triage p1', user: { login: 'owner', type: 'User' },
    created_at: time, updated_at: time }];
  const effects = [];
  const issue = { state: 'open', labels: [{ name: 'priority:p2' }] };
  const github = {
    paginate: async () => comments,
    rest: { issues: {
      get: async () => ({ data: issue }), listComments: () => {},
      getLabel: async () => ({}),
      createLabel: async args => effects.push({ create: args }),
      removeLabel: async args => effects.push({ remove: args.name }),
      addLabels: async args => effects.push({ labels: args.labels }),
      createComment: async args => {
        effects.push(args);
        comments.push({ ...args, user: { login: 'github-actions[bot]', type: 'Bot' } });
      },
    } },
  };
  const agent = { rest: { issues: { createComment: async args => effects.push({ plan: args }) } } };
  const context = { repo: { owner: 'owner', repo: 'repo' },
    payload: { issue: { number: 2 }, comment: comments[0] } };
  return { github, agent, context, core: { info: () => {} }, env: {}, comments, effects, issue };
}

test('owner intake replaces priority once and delegates analysis only to existing /plan', async () => {
  const f = fixture();
  await intake(f);
  assert.ok(f.effects.some(item => item.remove === 'priority:p2'));
  assert.deepEqual(f.effects.find(item => item.labels).labels, ['priority:p1']);
  assert.equal(f.effects.find(item => item.plan).plan.body, '/plan');
  assert.ok(f.effects.some(item => item.body?.includes('grants no implementation permission')));
  const count = f.effects.length;
  await intake(f);
  assert.equal(f.effects.length, count);
  assert.equal(JSON.stringify(f.effects).includes('plan:approved'), false);
});

test('public, edited, closed and PR intake cannot start planning or mutate labels', async () => {
  for (const change of [
    f => { f.comments[0].user.login = 'stranger'; },
    f => { f.comments[0].updated_at = 'changed'; },
    f => { f.issue.state = 'closed'; },
    f => { f.issue.pull_request = {}; },
  ]) {
    const f = fixture();
    change(f);
    await intake(f);
    assert.deepEqual(f.effects, []);
  }
});

test('intake preserves an existing plan and questions instead of creating parallel analysis', async () => {
  for (const body of ['## 📋 Implementation plan v1 · owner: builder\n\nScope', '## Planning questions\nWhich scope?']) {
    const f = fixture();
    f.comments.push({ id: 2, body, user: { login: 'github-actions[bot]', type: 'Bot' } });
    await intake(f);
    assert.equal(f.effects.filter(item => item.plan).length, 0);
  }
});

test('missing priority labels are created but unrelated API validation failures remain explicit', async () => {
  const f = fixture();
  f.github.rest.issues.getLabel = async () => { throw Object.assign(new Error('Missing'), { status: 404 }); };
  await intake(f);
  assert.equal(f.effects.filter(item => item.create).length, 3);
  const broken = fixture();
  broken.github.rest.issues.getLabel = async () => { throw Object.assign(new Error('Invalid request'), { status: 422 }); };
  await assert.rejects(intake(broken), /Invalid request/);
  assert.deepEqual(broken.effects, []);
});
