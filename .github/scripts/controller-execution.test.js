'use strict';

// Execute the production github-script bodies, not copied controller logic.
// The fake API is intentionally strict: an unexpected call fails the test.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { createRequire } = require('node:module');
const { ROLES } = require('./delivery-lifecycle');

const root = path.resolve(__dirname, '../..');
const workflowRequire = createRequire(path.join(root, 'workflow-test.cjs'));
const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
const owner = 'cedricmendelin';
const sha = 'a'.repeat(40);
const stamp = minute => `2026-11-16T12:${String(minute).padStart(2, '0')}:00Z`;

function source(workflow, step) {
  const lines = fs.readFileSync(path.join(root, '.github/workflows', workflow), 'utf8').split(/\r?\n/);
  const name = lines.findIndex(line => line.trim() === `- name: ${step}`);
  assert.notEqual(name, -1, `Missing production step ${step}`);
  const start = lines.findIndex((line, index) => index > name && line.trim() === 'script: |');
  assert.notEqual(start, -1, `Missing script for ${step}`);
  let end = start + 1;
  while (end < lines.length && (!lines[end].trim() || lines[end].startsWith('            '))) end++;
  return lines.slice(start + 1, end).map(line => line.slice(12)).join('\n');
}

const steps = {
  evaluate: ['squad-pr-lifecycle.yml', 'Evaluate readiness and bridge review findings'],
  route: ['squad-pr-lifecycle.yml', 'Route the event to pull request numbers (read only)'],
  plan: ['squad-plan-approved.yml', 'Remove approval when a newer plan is published'],
  assign: ['squad-plan-approved.yml', 'Assign Copilot once unless local handoff is selected'],
};

function fixture() {
  const pr = {
    number: 81, node_id: 'PR_81', state: 'open', draft: false,
    head: { sha, ref: 'local-work', repo: { full_name: `${owner}/bujo-ai` } },
    base: { ref: 'main' }, labels: [], body: '', created_at: '2026-11-16T10:00:00Z',
    auto_merge: null,
  };
  const workflows = [
    { id: 'ci', path: '.github/workflows/ci.yml' },
    { id: 'copilot', path: 'dynamic/copilot-swe-agent/copilot.yml' },
    ...ROLES.map(role => ({ id: role, path: `.github/workflows/squad-review-${role.toLowerCase()}.lock.yml` })),
  ];
  return {
    pr, workflows,
    checks: [
      { id: 1, name: 'ci', head_sha: sha, status: 'completed', conclusion: 'success' },
      ...ROLES.map((role, i) => ({
        id: 10 + i, name: `Squad Review / ${role}`, head_sha: sha,
        status: 'completed', conclusion: 'success', started_at: stamp(2), completed_at: stamp(4),
        output: { title: 'Review passed', summary: 'No blockers.' }, html_url: `https://example.test/check/${role}`,
      })),
    ],
    runs: {
      ...Object.fromEntries(ROLES.map(role => [role, [{
        id: role, name: `Squad Review ${role} · PR #81`, display_title: `Squad Review ${role} · PR #81`,
        created_at: stamp(1), updated_at: stamp(5), status: 'completed', conclusion: 'success',
      }]])),
      ci: [],
      copilot: [{
        id: 100, status: 'completed', conclusion: 'success',
        created_at: '2026-11-16T09:00:00Z', run_started_at: '2026-11-16T09:00:00Z',
      }],
    },
    rules: [{ type: 'required_status_checks', parameters: {
      required_status_checks: [{ context: 'ci' }, { context: 'Squad Merge Readiness' }],
    } }],
    comments: [], reviews: [], threads: [], timeline: [],
    permission: 'write', issue: { number: 81, labels: [], assignees: [] },
    context: {
      repo: { owner, repo: 'bujo-ai' }, eventName: 'pull_request_target',
      payload: {
        action: 'edited', pull_request: { number: 81, updated_at: stamp(0) },
        sender: { login: owner }, repository: { default_branch: 'main', owner: { login: owner } },
      },
    },
  };
}

async function execute(state, step = 'evaluate') {
  const calls = [];
  const outputs = {};
  const logs = [];
  const errors = [];
  const endpoint = (name, handler) => Object.assign(async args => {
    calls.push({ name, args: structuredClone(args) });
    if (state.before) await state.before(name, args, calls);
    if (state.fail === name) throw new Error(`API failure: ${name}`);
    return handler(args);
  }, { endpoint: name });
  const rest = {
    actions: {
      listRepoWorkflows: endpoint('workflows', () => ({ data: { workflows: state.workflows } })),
      listWorkflowRuns: endpoint('runs', args => ({ data: { workflow_runs: state.runs[args.workflow_id] || [] } })),
      createWorkflowDispatch: endpoint('dispatch', args => {
        // GitHub makes the new run visible to the next serialized evaluation.
        const role = args.workflow_id;
        if (ROLES.includes(role)) state.runs[role].unshift({
          id: `new-${role}`, name: `Squad Review ${role} · PR #81`, display_title: `Squad Review ${role} · PR #81`,
          created_at: stamp(20), status: 'queued',
        });
        return { status: 204 };
      }),
    },
    pulls: {
      get: endpoint('pr', () => ({ data: structuredClone(state.pr) })),
      list: endpoint('pulls', () => ({ data: state.associated || [] })),
      listReviews: endpoint('reviews', () => ({ data: state.reviews })),
    },
    repos: {
      getCollaboratorPermissionLevel: endpoint('permission', () => ({ data: { permission: state.permission } })),
    },
    checks: {
      listForRef: endpoint('checks', args => ({
        data: { check_runs: state.checks.filter(check => !args.check_name || check.name === args.check_name) },
      })),
      create: endpoint('createCheck', args => {
        const result = { ...args, id: Math.max(999, ...state.checks.map(check => check.id)) + 1, head_sha: args.head_sha };
        state.checks.push(result);
        return { data: result };
      }),
      update: endpoint('updateCheck', args => {
        const result = state.checks.find(check => check.id === args.check_run_id);
        Object.assign(result, args);
        if (args.status !== 'completed') delete result.conclusion;
        return { data: result };
      }),
    },
    issues: {
      listComments: endpoint('comments', () => ({ data: state.comments })),
      get: endpoint('issue', () => ({ data: state.issue })),
      createComment: endpoint('comment', args => {
        state.comments.push({ ...args, id: 3000 + state.comments.length, user: { login: owner }, created_at: stamp(30) });
        return { data: {} };
      }),
      addLabels: endpoint('addLabels', args => {
        state.pr.labels.push(...args.labels.map(name => ({ name })));
        return { data: {} };
      }),
      removeLabel: endpoint('removeLabel', args => {
        state.pr.labels = state.pr.labels.filter(label => label.name !== args.name);
        state.issue.labels = state.issue.labels.filter(label => label.name !== args.name);
        return { data: {} };
      }),
      addAssignees: endpoint('assignees', () => ({ data: {} })),
    },
  };
  const github = {
    rest,
    paginate: async (method, args) => {
      if (typeof method === 'string') {
        assert.match(method, /\/timeline$/);
        calls.push({ name: 'timeline', args });
        if (state.before) await state.before('timeline', args, calls);
        return state.timeline;
      }
      const { data } = await method(args);
      if (method === rest.actions.listRepoWorkflows) return data.workflows;
      if (method === rest.checks.listForRef) return data.check_runs;
      return data;
    },
    request: endpoint('request', (args) => ({ data: args })),
    graphql: endpoint('threads', () => ({
      repository: { pullRequest: { reviewThreads: {
        nodes: state.threads, pageInfo: { hasNextPage: false, endCursor: null },
      } } },
    })),
  };
  // request takes a route and parameters, unlike the one-argument REST methods.
  github.request = async (route, args) => {
    calls.push({ name: 'request', route, args });
    if (route.includes('/rules/')) return { data: state.rules };
    if (route.includes('/commits/')) return { data: state.associated || [] };
    throw new Error(`Unexpected request ${route}`);
  };
  const agent = {
    rest,
    graphql: async (query, args) => {
      calls.push({ name: 'mutation', query, args });
      if (state.fail === 'mutation') throw new Error('API failure: mutation');
      if (query.includes('disablePullRequestAutoMerge')) state.pr.auto_merge = null;
      else if (query.includes('enablePullRequestAutoMerge')) state.pr.auto_merge = { merge_method: 'squash' };
      else if (query.includes('markPullRequestReadyForReview')) state.pr.draft = false;
      else throw new Error(`Unexpected mutation ${query}`);
      return {};
    },
    request: async (route, args) => { calls.push({ name: 'agentRequest', route, args }); },
  };
  const core = {
    info: message => logs.push(message),
    setOutput: (key, value) => { outputs[key] = value; },
    setFailed: message => errors.push(message),
  };
  const code = source(...steps[step]);
  await new AsyncFunction('require', 'github', 'context', 'core', 'getOctokit', 'process', code)(
    workflowRequire, github, state.context, core, () => agent,
    { env: { HUMAN: owner, AGENT_TOKEN: 'test-only', PR_NUMBER: '81' } },
  );
  return { calls, logs, outputs, errors, state };
}

const commentsFrom = result => result.calls.filter(call => call.name === 'comment').map(call => call.args.body);
const readinessFrom = state => state.checks.filter(check => check.name === 'Squad Merge Readiness')
  .sort((a, b) => b.id - a.id)[0];
const enables = result => result.calls.filter(call => call.query?.includes('enablePullRequestAutoMerge'));
function fixComment(time, login = owner, head = sha) {
  return { id: Date.parse(time), created_at: time, user: { login }, body: `<!-- squad-review-fix:${head}:cloud -->` };
}
function humanReview(state = 'CHANGES_REQUESTED', id = 1, body = 'Fix the missing acceptance test') {
  return { id, state, body, user: { login: owner, type: 'User' }, author_association: 'OWNER', html_url: 'https://example.test/review/1' };
}
function removal(state, minute = 10) {
  state.timeline.push({ event: 'unlabeled', label: { name: 'review:none' }, created_at: stamp(minute) });
}
function cloud(state) { state.pr.head.ref = 'copilot/issue-81'; }

test('real label controller imports permission helper and honors eligible/ineligible review:none actions', async () => {
  for (const [action, permission, initiallyPresent, expected] of [
    ['labeled', 'write', true, true], ['labeled', 'read', true, false],
    ['unlabeled', 'read', false, true], ['unlabeled', 'maintain', false, false],
  ]) {
    const state = fixture();
    state.context.payload.action = action;
    state.context.payload.label = { name: 'review:none' };
    state.context.payload.sender = { login: 'someone' };
    state.permission = permission;
    if (initiallyPresent) state.pr.labels.push({ name: 'review:none' });
    const result = await execute(state);
    assert.equal(result.errors.length, 0);
    assert.equal(state.pr.labels.some(label => label.name === 'review:none'), expected);
    assert.equal(result.calls.some(call => call.name === 'permission'), true);
  }
});

test('interrupted-session controller uses owner login without a temporal-dead-zone failure', async () => {
  const state = fixture();
  cloud(state);
  state.runs.copilot[0].conclusion = 'cancelled';
  const result = await execute(state);
  assert.match(commentsFrom(result).join('\n'), /🙋 @cedricmendelin The latest cloud/);
  assert.notEqual(readinessFrom(state).conclusion, 'success');
  assert.equal(enables(result).length, 0);
});

test('controller escalates once after three actual sessions using the login, not feedback data', async () => {
  const state = fixture();
  cloud(state);
  state.reviews = [humanReview()];
  state.comments = [0, 5, 10].map(minute => fixComment(stamp(minute)));
  state.runs.copilot = [1, 6, 11].map((minute, id) => ({
    id, status: 'completed', conclusion: 'success', created_at: stamp(minute),
  }));
  const result = await execute(state);
  assert.deepEqual(result.calls.find(call => call.name === 'assignees').args.assignees, [owner]);
  assert.match(commentsFrom(result)[0], /@cedricmendelin .*3 actual attempts/);
  assert.doesNotMatch(commentsFrom(result)[0], /\[object Object\]/);
  const again = await execute(state);
  assert.equal(again.calls.some(call => call.name === 'assignees' || call.name === 'comment'), false);
  assert.equal(readinessFrom(state).conclusion, 'failure');
});

test('fresh run cannot adopt stale success, stale failure, or late old-generation check', async () => {
  for (const scenario of ['stale success', 'stale failure', 'late old completion', 'cancelled run']) {
    const state = fixture();
    removal(state);
    for (const role of ROLES) {
      state.runs[role][0].created_at = stamp(11);
      state.runs[role][0].updated_at = stamp(15);
      if (scenario === 'cancelled run') state.runs[role][0].conclusion = 'cancelled';
      const check = state.checks.find(item => item.name === `Squad Review / ${role}`);
      if (scenario === 'stale failure') check.conclusion = 'failure';
      if (scenario === 'late old completion') check.completed_at = stamp(14);
      if (scenario === 'cancelled run') {
        check.started_at = stamp(12);
        check.completed_at = stamp(14);
      }
    }
    const result = await execute(state);
    assert.notEqual(readinessFrom(state).conclusion, 'success', scenario);
    assert.equal(enables(result).length, 0, scenario);
    assert.equal(commentsFrom(result).some(body => body.includes('squad-review-fix:')), false, scenario);
  }
});

test('genuine new-generation checks pass; cosmetic re-evaluation does not redispatch or repeat success comments', async () => {
  const state = fixture();
  removal(state, 0);
  const first = await execute(state);
  assert.equal(readinessFrom(state).conclusion, 'success');
  assert.equal(enables(first).length, 1);
  assert.equal(first.calls.some(call => call.name === 'dispatch'), false);
  const second = await execute(state);
  assert.equal(enables(second).length, 0);
  assert.equal(commentsFrom(second).length, 0);
});

test('required readiness stays pending while native auto-merge is armed', async () => {
  const state = fixture();
  const result = await execute(state);
  const arm = result.calls.findIndex(call => call.query?.includes('enablePullRequestAutoMerge'));
  const success = result.calls.findIndex(call => ['createCheck', 'updateCheck'].includes(call.name)
    && call.args.conclusion === 'success');
  assert.ok(arm >= 0);
  assert.ok(success > arm, 'Do not make the PR clean before requesting native auto-merge');
  assert.equal(readinessFrom(state).conclusion, 'success');

  const denied = fixture();
  denied.fail = 'mutation';
  await assert.rejects(execute(denied), /API failure: mutation/);
  assert.equal(readinessFrom(denied).status, 'in_progress');
  assert.notEqual(readinessFrom(denied).conclusion, 'success');
});

test('active GitHub-named reviewer runs are not redispatched on completion events', async () => {
  const state = fixture();
  for (const role of ROLES) state.runs[role][0].status = 'in_progress';
  state.context.eventName = 'workflow_run';
  state.context.payload.workflow_run = { display_title: 'Squad Review Architect · PR #81' };
  const result = await execute(state);
  assert.equal(result.calls.filter(call => call.name === 'dispatch').length, 0);
  assert.notEqual(readinessFrom(state).conclusion, 'success');
});

test('GitHub-assigned check starts after completion neither rerun reviews nor hide blockers', async () => {
  for (const conclusion of ['success', 'failure']) {
    const state = fixture();
    cloud(state);
    const check = state.checks.find(item => item.name === 'Squad Review / Verifier');
    check.started_at = '2026-11-16T12:04:01Z';
    check.conclusion = conclusion;
    check.output = { title: 'Review result', summary: 'docs/ai-chat.md: verify the claimed completion.' };
    const result = await execute(state);
    assert.equal(result.calls.some(call => call.name === 'dispatch'), false);
    if (conclusion === 'success') {
      assert.equal(readinessFrom(state).conclusion, 'success');
      assert.equal(enables(result).length, 1);
    } else {
      assert.notEqual(readinessFrom(state).conclusion, 'success');
      assert.equal(enables(result).length, 0);
      assert.match(commentsFrom(result).join('\n'), /docs\/ai-chat.md: verify the claimed completion/);
    }
  }
});

test('re-evaluation creates a new pending readiness check instead of reopening a completed result', async () => {
  for (const conclusion of ['success', 'failure']) {
    const state = fixture();
    state.checks.push({ id: 500, name: 'Squad Merge Readiness', head_sha: sha, status: 'completed', conclusion });
    for (const role of ROLES) state.runs[role][0].status = 'in_progress';
    const result = await execute(state);
    const check = readinessFrom(state);
    assert.equal(result.calls.some(call => call.name === 'updateCheck' && call.args.check_run_id === 500), false);
    assert.ok(check.id > 500);
    assert.equal(check.status, 'in_progress');
    assert.notEqual(check.conclusion, 'success');
  }
});

test('reviewer infrastructure failures pause once rather than starting an automatic retry loop', async () => {
  const state = fixture();
  state.runs.Architect[0].conclusion = 'failure';
  const first = await execute(state);
  assert.equal(first.calls.some(call => call.name === 'dispatch'), false);
  assert.match(commentsFrom(first).join('\n'), /No automatic reviewer retry loop/);
  assert.equal(readinessFrom(state).conclusion, 'failure');
  const paused = await execute(state);
  assert.equal(commentsFrom(paused).length, 0);
  state.pr.labels = [];
  const resumed = await execute(state);
  assert.equal(resumed.calls.filter(call => call.name === 'dispatch').length, 1);
});

test('only genuine selected-generation failure is forwarded; review:none suppresses it even during an active review', async () => {
  const state = fixture();
  removal(state, 0);
  const check = state.checks.find(item => item.name === 'Squad Review / Verifier');
  check.conclusion = 'failure';
  check.output = { title: 'Missing regression test', summary: 'tests/Example.cs:12: add the failing acceptance case.' };
  const result = await execute(state);
  assert.match(commentsFrom(result).join('\n'), /tests\/Example.cs:12/);
  assert.notEqual(readinessFrom(state).conclusion, 'success');
  state.pr.labels = [{ name: 'review:none' }];
  state.comments = [];
  state.runs.Verifier[0].status = 'in_progress';
  const optedOut = await execute(state);
  assert.equal(readinessFrom(state).conclusion, 'success');
  assert.equal(optedOut.calls.some(call => call.name === 'dispatch'), false);
  assert.equal(commentsFrom(optedOut).some(body => body.includes('squad-review-fix')), false);
});

test('COMMENTED does not dismiss a human change request; summary-only feedback is forwarded even with review:none', async () => {
  const state = fixture();
  state.pr.labels.push({ name: 'review:none' });
  state.reviews = [humanReview(), humanReview('COMMENTED', 2, 'Following up')];
  const result = await execute(state);
  assert.equal(readinessFrom(state).conclusion, 'failure');
  assert.match(commentsFrom(result).join('\n'), /Fix the missing acceptance test/);
  assert.equal(enables(result).length, 0);
  for (const superseding of ['APPROVED', 'DISMISSED']) {
    const cleared = fixture();
    cleared.reviews = [...state.reviews, humanReview(superseding, 3)];
    assert.equal(readinessFrom((await execute(cleared)).state).conclusion, 'success', superseding);
  }
});

test('completed same-head repairs can request another bounded attempt; active and cancelled sessions do not loop', async () => {
  const state = fixture();
  cloud(state);
  state.reviews = [humanReview()];
  state.comments = [fixComment(stamp(0))];
  state.runs.copilot = [{ id: 1, status: 'completed', conclusion: 'success', created_at: stamp(1) }];
  const result = await execute(state);
  assert.equal(commentsFrom(result).filter(body => body.includes('@copilot Fix')).length, 1);
  // The new request is outstanding: a second event must not dispatch again.
  assert.equal(commentsFrom(await execute(state)).length, 0);
  for (const status of ['in_progress', 'cancelled']) {
    const blocked = fixture();
    cloud(blocked);
    blocked.reviews = [humanReview()];
    blocked.comments = [fixComment(stamp(0))];
    blocked.runs.copilot = [{ id: 1, created_at: stamp(1),
      status: status === 'cancelled' ? 'completed' : status, conclusion: status === 'cancelled' ? status : null }];
    assert.equal(commentsFrom(await execute(blocked)).some(body => body.includes('@copilot Fix')), false);
    if (status === 'cancelled') {
      blocked.context.eventName = 'workflow_dispatch';
      blocked.context.payload.inputs = { pr_number: '81' };
      assert.equal(commentsFrom(await execute(blocked)).some(body => body.includes('@copilot Fix')), true);
    }
  }
});

test('three successive same-head actual repair sessions exhaust the budget once, without an unbounded repair loop', async () => {
  const state = fixture();
  cloud(state);
  state.reviews = [humanReview()];
  const initial = await execute(state);
  assert.equal(commentsFrom(initial).filter(body => body.includes('@copilot Fix')).length, 1);
  for (let attempt = 1; attempt <= 3; attempt++) {
    // The test clock for created request comments is minute 30. Assign unique
    // real session IDs/start times and retain the same code head after each run.
    state.comments.at(-1).created_at = stamp(attempt * 5);
    state.runs.copilot.unshift({
      id: attempt, status: 'completed', conclusion: 'success',
      created_at: stamp(attempt * 5 + 1),
    });
    const result = await execute(state);
    if (attempt < 3) {
      assert.equal(commentsFrom(result).filter(body => body.includes('@copilot Fix')).length, 1, `attempt ${attempt}`);
    } else {
      assert.equal(commentsFrom(result).filter(body => body.includes('@copilot Fix')).length, 0);
      assert.match(commentsFrom(result).join('\n'), /@cedricmendelin .*3 actual attempts/);
    }
  }
  assert.equal(commentsFrom(await execute(state)).length, 0);
});

test('resolving a blocking thread uses manual recovery; arbitrary issue comments are not repair input', async () => {
  const state = fixture();
  state.pr.labels = [{ name: 'review:none' }];
  state.comments = [{ id: 1, user: { login: owner }, body: '[blocking] arbitrary top-level comment' }];
  state.threads = [{
    id: 'thread', isResolved: false, isOutdated: false,
    comments: { nodes: [{
      body: '[blocking] missing regression test', url: 'https://example.test/thread',
      author: { login: owner, __typename: 'User' }, authorAssociation: 'OWNER',
    }] },
  }];
  const blocked = await execute(state);
  assert.equal(readinessFrom(state).conclusion, 'failure');
  assert.match(commentsFrom(blocked).join('\n'), /missing regression test/);
  assert.doesNotMatch(commentsFrom(blocked).join('\n'), /arbitrary top-level/);
  state.threads[0].isResolved = true;
  state.context.eventName = 'workflow_dispatch';
  state.context.payload.inputs = { pr_number: '81' };
  assert.equal(readinessFrom((await execute(state)).state).conclusion, 'success');
});

test('external repair/session markers cannot exhaust budget, suppress feedback, or hide interrupted session', async () => {
  const state = fixture();
  cloud(state);
  state.reviews = [humanReview()];
  state.comments = [0, 5, 10].map(minute => fixComment(stamp(minute), 'outsider'));
  state.runs.copilot = [1, 6, 11].map((minute, id) => ({
    id, status: 'completed', conclusion: 'success', created_at: stamp(minute),
  }));
  const result = await execute(state);
  assert.equal(result.calls.some(call => call.name === 'assignees'), false);
  assert.equal(commentsFrom(result).some(body => body.includes('@copilot Fix')), true);
  const waiting = fixture();
  cloud(waiting);
  waiting.runs.copilot[0].conclusion = 'cancelled';
  waiting.comments.push({ user: { login: 'outsider' }, body: '<!-- squad-pr-lifecycle:session-wait -->' });
  assert.equal(commentsFrom(await execute(waiting)).some(body => body.includes('latest cloud implementation')), true);
});

test('API failures removing approval/labels or assigning escalation are not suppressed', async () => {
  const label = fixture();
  label.context.payload.action = 'labeled';
  label.context.payload.label = { name: 'review:none' };
  label.context.payload.sender = { login: 'outsider' };
  label.permission = 'read';
  label.fail = 'removeLabel';
  await assert.rejects(execute(label), /API failure: removeLabel/);
  const escalation = fixture();
  cloud(escalation);
  escalation.reviews = [humanReview()];
  escalation.comments = [0, 5, 10].map(minute => fixComment(stamp(minute)));
  escalation.runs.copilot = [1, 6, 11].map((minute, id) => ({
    id, status: 'completed', conclusion: 'success', created_at: stamp(minute),
  }));
  escalation.fail = 'assignees';
  await assert.rejects(execute(escalation), /API failure: assignees/);
});

test('final readiness re-read invalidates raced review:none, CI, or review results even during bootstrap', async () => {
  for (const mutation of ['label', 'ci', 'review', 'generation']) {
    for (const bootstrap of [false, true]) {
      const state = fixture();
      state.pr.labels = [{ name: 'review:none' }];
      state.pr.auto_merge = { merge_method: 'squash' };
      if (bootstrap) state.rules = [];
      let reads = 0;
      state.before = async name => {
        if (name === 'pr' && ++reads === 3) {
          if (mutation === 'label') state.pr.labels = [];
          if (mutation === 'ci') state.checks[0].conclusion = 'failure';
          if (mutation === 'review') state.reviews.push(humanReview());
          if (mutation === 'generation') removal(state);
        }
      };
      const result = await execute(state);
      assert.notEqual(readinessFrom(state).conclusion, 'success', `${mutation}/${bootstrap}`);
      assert.equal(state.pr.auto_merge, null, `${mutation}/${bootstrap}`);
      assert.equal(enables(result).length, 0);
    }
  }
});

test('final readiness re-read rejects a newly active review run or failed check without a label change', async () => {
  for (const mutation of ['active run', 'failed check']) {
    const state = fixture();
    state.pr.auto_merge = { merge_method: 'squash' };
    let reads = 0;
    state.before = async name => {
      if (name === 'pr' && ++reads === 3) {
        if (mutation === 'active run') state.runs.Architect[0].status = 'in_progress';
        else state.checks.find(check => check.name === 'Squad Review / Security').conclusion = 'failure';
      }
    };
    const result = await execute(state);
    assert.notEqual(readinessFrom(state).conclusion, 'success', mutation);
    assert.equal(state.pr.auto_merge, null);
    assert.equal(enables(result).length, 0);
  }
});

test('review:none removal starts fresh reviews, ignores bypassed failures, and duplicate delivery is idempotent', async () => {
  const state = fixture();
  removal(state);
  state.context.payload.action = 'unlabeled';
  state.context.payload.label = { name: 'review:none' };
  state.checks.filter(check => check.name.startsWith('Squad Review /')).forEach(check => { check.conclusion = 'failure'; });
  const first = await execute(state);
  assert.equal(first.calls.filter(call => call.name === 'dispatch').length, 4);
  assert.equal(commentsFrom(first).length, 0);
  const duplicate = await execute(state);
  assert.equal(duplicate.calls.filter(call => call.name === 'dispatch').length, 0);
  assert.equal(commentsFrom(duplicate).length, 0);
});

test('read-only router maps PR, manual, all reviewer, CI and cloud events to the same PR number', async () => {
  const events = [
    ['pull_request_target', { pull_request: { number: 81 } }],
    ['pull_request_review', { pull_request: { number: 81 } }],
    ['workflow_dispatch', { inputs: { pr_number: '81' } }],
    ...ROLES.map(role => ['workflow_run', { workflow_run: { display_title: `Squad Review ${role} · PR #81`, head_branch: 'main' } }]),
    ['workflow_run', { workflow_run: { pull_requests: [{ number: 81 }], head_branch: 'copilot/issue-81' } }],
    ['workflow_run', { workflow_run: { head_sha: sha } }],
    ['workflow_run', { workflow_run: { head_branch: 'copilot/issue-81' } }],
  ];
  for (const [eventName, payload] of events) {
    const state = fixture();
    state.context = { ...state.context, eventName, payload };
    state.associated = [{ number: 81, state: 'open' }];
    const result = await execute(state, 'route');
    assert.equal(result.outputs.numbers, '[81]', eventName);
    assert.equal(result.outputs.found, 'true', eventName);
    assert.equal(result.calls.some(call => ['comment', 'dispatch', 'mutation'].includes(call.name)), false);
  }
  const workflow = fs.readFileSync(path.join(root, '.github/workflows/squad-pr-lifecycle.yml'), 'utf8');
  assert.match(workflow, /needs: route/);
  assert.match(workflow, /pr_number: \$\{\{ fromJSON\(needs\.route\.outputs\.numbers\) \}\}/);
  assert.match(workflow, /group: squad-pr-lifecycle-\$\{\{ github\.repository \}\}-\$\{\{ matrix\.pr_number \}\}/);
  assert.doesNotMatch(workflow, /group:.*(?:head_branch|run_id)/);
});

test('first valid plan clears existing approval; malformed and gapped history clear approval and report failure', async () => {
  for (const version of ['1', '3', 'oops']) {
    const state = fixture();
    const comment = { id: 1, user: { login: owner }, body: `## 📋 Implementation plan v${version} · owner: builder\n\nScope` };
    state.comments = [comment];
    state.issue.labels = [{ name: 'plan:approved' }];
    state.context.eventName = 'issue_comment';
    state.context.payload.issue = state.issue;
    state.context.payload.comment = comment;
    const result = await execute(state, 'plan');
    assert.equal(state.issue.labels.length, 0, version);
    assert.equal(result.calls.filter(call => call.name === 'removeLabel').length, 1);
    assert.equal(result.errors.length, version === '1' ? 0 : 1);
    assert.match(commentsFrom(result)[0], version === '1' ? /newer plan v1/ : /Approval cleared:/);
  }
});

test('approval assignment controller honors local handoff/duplicate delivery and propagates label API errors', async () => {
  for (const mode of ['local', 'assigned', 'cloud', 'ineligible']) {
    const state = fixture();
    state.issue.labels = [{ name: 'plan:approved' }];
    if (mode === 'local') state.issue.labels.push({ name: 'handoff:local' });
    if (mode === 'assigned') state.issue.assignees = [{ login: 'copilot-swe-agent[bot]' }];
    state.comments = [{ id: 1, user: { login: owner }, body: '## 📋 Implementation plan v1 · owner: builder' }];
    state.context.eventName = 'issues';
    state.context.payload.issue = state.issue;
    if (mode === 'ineligible') {
      state.context.payload.sender = { login: 'outsider' };
      state.permission = 'read';
      state.fail = 'removeLabel';
      await assert.rejects(execute(state, 'assign'), /API failure: removeLabel/);
    } else {
      const result = await execute(state, 'assign');
      assert.equal(result.calls.filter(call => call.name === 'agentRequest').length, mode === 'cloud' ? 1 : 0, mode);
    }
  }
});
