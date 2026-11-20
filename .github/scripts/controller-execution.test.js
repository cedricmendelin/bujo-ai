'use strict';

// Execute the production github-script bodies, not copied controller logic.
// The fake API is intentionally strict: an unexpected call fails the test.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { createRequire } = require('node:module');
const { ROLES, reviewInputKey } = require('./delivery-lifecycle');

const root = path.resolve(__dirname, '../..');
const workflowRequire = createRequire(path.join(root, 'workflow-test.cjs'));
const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
const owner = 'cedricmendelin';
const sha = 'a'.repeat(40);
const stamp = minute => `2026-11-16T12:${String(minute).padStart(2, '0')}:00Z`;
const reviewRunInput = (role, key, head = sha) => role === 'DevOps' ? reviewInputKey(`${key}\n${head}`) : key;

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
  closure: ['squad-pr-lifecycle.yml', 'Warn when a completed issue retains unchecked criteria'],
  plan: ['squad-plan-approved.yml', 'Remove approval when a newer plan is published'],
  assign: ['squad-plan-approved.yml', 'Assign Copilot once unless local handoff is selected'],
};

function fixture() {
  const pr = {
    number: 81, node_id: 'PR_81', state: 'open', draft: false,
    head: { sha, ref: 'local-work', repo: { full_name: `${owner}/bujo-ai` } },
    base: { ref: 'main', sha: 'b'.repeat(40) }, labels: [], body: '', created_at: '2026-11-16T10:00:00Z',
    changed_files: 1,
    auto_merge: null,
  };
  const workflows = [
    { id: 'ci', path: '.github/workflows/ci.yml' },
    { id: 'copilot', path: 'dynamic/copilot-swe-agent/copilot.yml' },
    ...ROLES.map(role => ({ id: role, path: `.github/workflows/squad-review-${role.toLowerCase()}.lock.yml` })),
  ];
  return {
    pr, workflows, files: [{ filename: 'src/Bujo.Web/Pages/Journal.razor', status: 'modified' }],
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
        id: role, name: `Squad Review ${role} · PR #81`, display_title: `Squad Review ${role} · PR #81 · input ${reviewRunInput(role, reviewInputKey(''))}`,
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
    permission: 'write', issue: {
      number: 11, state: 'open', state_reason: null, labels: [], assignees: [],
      body: [
        '## Acceptance tracking',
        '',
        '- [x] Local sign-in works — evidence: PR #125 and sign-in test.',
        '- [ ] Browser installation — blocked on target selection; owner: Verifier; next: record evidence on agreed browsers.',
      ].join('\n'),
    },
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
  const removeLabel = args => {
    state.pr.labels = state.pr.labels.filter(label => label.name !== args.name);
    state.issue.labels = state.issue.labels.filter(label => label.name !== args.name);
    return { data: {} };
  };
  const rest = {
    actions: {
      listRepoWorkflows: endpoint('workflows', () => ({ data: { workflows: state.workflows } })),
      listWorkflowRuns: endpoint('runs', args => ({ data: {
        workflow_runs: (state.runs[args.workflow_id] || []).filter(run => !args.status || run.status === args.status),
      } })),
      createWorkflowDispatch: endpoint('dispatch', args => {
        // GitHub makes the new run visible to the next serialized evaluation.
        const role = args.workflow_id;
        if (ROLES.includes(role)) state.runs[role].unshift({
          id: `new-${role}`, name: `Squad Review ${role} · PR #81`, display_title: `Squad Review ${role} · PR #81 · input ${args.inputs.review_input}`,
          created_at: stamp(20), status: 'queued',
        });
        return { status: 204 };
      }),
    },
    pulls: {
      get: endpoint('pr', () => ({ data: structuredClone(state.pr), headers: { etag: '"pr-version"' } })),
      listFiles: endpoint('files', () => ({ data: state.files })),
      update: endpoint('updatePr', args => {
        state.pr.body = args.body;
        return { data: structuredClone(state.pr) };
      }),
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
      listComments: endpoint('comments', args => ({ data: state.scopedComments
        ? state.comments.filter(comment => comment.issue_number === args.issue_number)
        : state.comments })),
      get: endpoint('issue', args => ({ data: state.issues?.[args.issue_number] || state.issue })),
      createComment: endpoint('comment', args => {
        const comment = { ...args, id: 3000 + state.comments.length,
          user: { login: state.commentLogin || owner }, created_at: stamp(30) };
        state.comments.push(comment);
        return { data: comment };
      }),
      addLabels: endpoint('addLabels', args => {
        state.pr.labels.push(...args.labels.map(name => ({ name })));
        return { data: {} };
      }),
      removeLabel: endpoint('removeLabel', args => {
        if (state.denyWorkflowPrLabelWrite) throw new Error('403 Resource not accessible by integration');
        return removeLabel(args);
      }),
      addAssignees: endpoint('assignees', () => ({ data: {} })),
      removeAssignees: endpoint('removeAssignees', () => ({ data: {} })),
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
    rest: {
      ...rest,
      issues: {
        ...rest.issues,
        removeLabel: endpoint('agentRemoveLabel', removeLabel),
      },
    },
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
    warning: message => logs.push(message),
    setOutput: (key, value) => { outputs[key] = value; },
    setFailed: message => errors.push(message),
  };
  const productionCode = source(...steps[step]);
  const code = state.mutateSource ? state.mutateSource(productionCode) : productionCode;
  await new AsyncFunction('require', 'github', 'context', 'core', 'getOctokit', 'process', code)(
    workflowRequire, github, { runId: 900, ...state.context }, core, () => agent,
    { env: { HUMAN: owner, AGENT_TOKEN: 'test-only', PR_NUMBER: '81', REVIEW_ROUTING: state.reviewRouting } },
  );
  return { calls, logs, outputs, errors, state };
}

const commentsFrom = result => result.calls.filter(call => call.name === 'comment').map(call => call.args.body);
const readinessFrom = state => state.checks.filter(check => check.name === 'Squad Merge Readiness')
  .sort((a, b) => b.id - a.id)[0];
const enables = result => result.calls.filter(call => call.query?.includes('enablePullRequestAutoMerge'));
function fixComment(time, login = owner, head = sha) {
  const token = `00000000-0000-0000-0000-${Date.parse(time).toString(16).padStart(12, '0')}`;
  return { id: Date.parse(time), created_at: time, user: { login },
    body: `<!-- squad-review-fix:${head}:cloud -->\n<!-- squad-repair-request:${token} -->` };
}
function report(state, request, session, outcome = 'attempted') {
  const token = request.body.match(/squad-repair-request:([a-f0-9-]+)/)[1];
  state.comments.push({
    id: request.id + 1, created_at: session.created_at, user: { login: 'copilot-swe-agent[bot]', type: 'Bot' },
    body: `<!-- squad-repair-outcome:${token}:${session.id}:${outcome} -->\nInvestigated the failing acceptance case.`,
  });
}
function humanReview(state = 'CHANGES_REQUESTED', id = 1, body = 'Fix the missing acceptance test') {
  return { id, state, body, user: { login: owner, type: 'User' }, author_association: 'OWNER', html_url: 'https://example.test/review/1' };
}

test('observation preserves all reviewers; enforcement dispatches Verifier with one bounded evidence bundle', async () => {
  for (const mode of [undefined, 'enforce']) {
    const state = fixture();
    state.reviewRouting = mode;
    state.pr.body = 'Completion: full\n\nCloses #12\n\nPlan v2';
    state.issue = { number: 12, body: '## Acceptance criteria\n\n- New behavior' };
    state.comments = [
      { id: 11, user: { login: owner }, body: '## 📋 Implementation plan v2 · owner: builder\nExact scope' },
      { id: 12, user: { login: owner }, body: '## 📋 Implementation plan v3 · owner: builder\nLater scope' },
    ];
    const result = await execute(state);
    const dispatches = result.calls.filter(call => call.name === 'dispatch');
    assert.deepEqual(dispatches.map(call => call.args.workflow_id), mode ? ['Verifier'] : ROLES);
    assert.equal(result.calls.filter(call => call.name === 'issue').length, 1);
    assert.equal(new Set(dispatches.map(call => call.args.inputs.review_evidence)).size, 1);
    const packet = JSON.parse(dispatches[0].args.inputs.review_evidence);
    assert.equal(packet.head, sha);
    assert.equal(packet.bodyKey, reviewInputKey(state.pr.body));
    assert.deepEqual(packet.files, [['modified', state.files[0].filename]]);
    assert.equal(packet.ci.checkId, 1);
    assert.equal(packet.scope.planComment, 11);
    assert.match(packet.scope.issueBody, /New behavior/);
    assert.match(result.logs.join('\n'), /"selected":\["Verifier"\]/);
  }
});

test('routing counts both rename paths and caps all-domain PRs until current owner approval', async () => {
  const state = fixture();
  state.reviewRouting = 'enforce';
  state.files = [{ filename: 'docs/retired.txt', previous_filename: 'infra/identity.tf', status: 'renamed' },
    { filename: 'src/Bujo.Domain/Items/Item.cs', status: 'modified' }];
  state.pr.changed_files = 2;
  state.runs.Architect = [];
  let result = await execute(state);
  const marker = readinessFrom(state).output.summary.match(/Squad coverage: Architect [a-f0-9]{64}/)?.[0];
  assert.ok(marker, 'missing revision-bound overflow marker');
  assert.equal(readinessFrom(state).conclusion, 'failure');
  assert.deepEqual(result.calls.filter(call => call.name === 'runs').map(call => call.args.workflow_id),
    ['ci', 'Security', 'Verifier', 'DevOps']);
  assert.equal(enables(result).length, 0);
  state.reviews = [{ ...humanReview('APPROVED', 1, marker), commit_id: sha,
    user: { login: 'contributor', type: 'User' } }];
  assert.equal(enables(await execute(state)).length, 0);
  state.reviews[0].user.login = owner;
  result = await execute(state);
  assert.equal(enables(result).length, 1);
  state.pr.body = 'Completion: full\n\nSubstantive edit';
  result = await execute(state);
  assert.equal(enables(result).length, 0);
  assert.match(readinessFrom(state).output.summary, /Reviewer cap reached/);
});

test('enforcement fails closed on incomplete or raced inventory and an excluded failing check', async () => {
  for (const kind of ['incomplete', 'raced', 'excluded', 'unknown']) {
    const state = fixture();
    state.reviewRouting = 'enforce';
    if (kind === 'incomplete') state.pr.changed_files = 2;
    if (kind === 'unknown') state.files[0].filename = 'unmapped/handler.cs';
    if (kind === 'excluded') state.checks.find(check => check.name === 'Squad Review / Architect').conclusion = 'failure';
    if (kind === 'raced') state.before = (name, _args, calls) => {
      if (name === 'files' && calls.filter(call => call.name === 'files').length === 2) state.files = [];
    };
    const result = await execute(state);
    assert.equal(enables(result).length, 0, kind);
    assert.notEqual(readinessFrom(state).conclusion, 'success', kind);
  }
});
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
    id: id + 1, status: 'completed', conclusion: 'success', created_at: stamp(minute),
  }));
  [...state.comments].forEach((request, i) => report(state, request, state.runs.copilot[i]));
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
  report(state, state.comments[0], state.runs.copilot[0]);
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
      assert.equal(commentsFrom(await execute(blocked)).some(body => body.includes('@copilot Fix')), false);
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
    const request = state.comments.at(-1);
    request.created_at = stamp(attempt * 5);
    state.runs.copilot.unshift({
      id: attempt, status: 'completed', conclusion: 'success',
      created_at: stamp(attempt * 5 + 1),
    });
    report(state, request, state.runs.copilot[0]);
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
    id: id + 1, status: 'completed', conclusion: 'success', created_at: stamp(minute),
  }));
  [...escalation.comments].forEach((request, i) => report(escalation, request, escalation.runs.copilot[i]));
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
    ...ROLES.map(role => ['workflow_run', { workflow_run: { display_title: `Squad Review ${role} · PR #81 · input ${reviewInputKey('')}`, head_branch: 'main' } }]),
    ['workflow_run', { workflow_run: { pull_requests: [{ number: 81 }], head_branch: 'copilot/issue-81' } }],
    ['workflow_run', { workflow_run: { head_sha: sha } }],
    ['workflow_run', { workflow_run: { head_branch: 'copilot/issue-81' } }],
    ['workflow_run', { workflow_run: { display_title: 'Squad Acceptance Recovery · PR #81 · plan 123' } }],
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

test('hourly router skips the matrix when idle and selects only eligible open PRs', async () => {
  const state = fixture();
  state.context.eventName = 'schedule';
  const idle = await execute(state, 'route');
  assert.deepEqual(idle.outputs, { numbers: '[]', found: 'false' });
  assert.deepEqual(idle.calls.map(call => call.name), ['pulls']);
  assert.equal(idle.calls[0].args.state, 'open');

  const local = state.pr;
  const draft = { ...local, number: 82, draft: true, head: { ...local.head, ref: 'copilot/issue-11' } };
  state.associated = [
    local, draft,
    { ...local, number: 83, draft: true },
    { ...draft, number: 84, labels: [{ name: 'handoff:local' }] },
    { ...local, number: 85, labels: [{ name: 'needs:human' }] },
    { ...local, number: 86, head: { ...local.head, repo: null } },
  ];
  assert.deepEqual((await execute(state, 'route')).outputs, { numbers: '[81,82]', found: 'true' });
});

test('hourly fallback recovers a completed cloud draft without bypassing partial issue tracking', async () => {
  const state = fixture();
  cloud(state);
  state.context.eventName = 'schedule';
  state.pr.draft = true;
  state.pr.body = '- **Remaining work:** Builder to run SQL verification after access.\n\n'
    + 'Completion: partial\n\n<!-- START COPILOT CODING AGENT SUFFIX -->\n- Fixes #11';
  const resumed = await execute(state);
  assert.equal(state.pr.draft, false);
  assert.match(state.pr.body, /- Refs #11$/);
  assert.deepEqual(resumed.calls.filter(call => call.name === 'dispatch').map(call => call.args.workflow_id), ROLES);
  assert.equal(enables(resumed).length, 0, 'reviews still need to pass');
});

test('hourly fallback leaves active sessions alone and deduplicates conflict and CI notices', async () => {
  const state = fixture();
  cloud(state);
  state.context.eventName = 'schedule';
  state.pr.mergeable = false;
  state.checks.find(check => check.name === 'ci').conclusion = 'failure';
  state.runs.copilot[0].status = 'in_progress';
  const active = await execute(state);
  assert.deepEqual(active.calls.map(call => call.name), ['workflows', 'pr', 'runs']);
  state.runs.copilot[0].status = 'completed';
  const blocked = await execute(state);
  const [notice] = commentsFrom(blocked);
  assert.match(notice, /merge conflict.*`ci` check failed/s);
  assert.match(notice, /If no repair is already running, post this as a new PR comment:/);
  assert.match(notice, /\n```\n@copilot Resolve the merge conflicts with the target branch on this PR\. Keep the approved scope, push to this PR branch, and do not merge the PR\.\n```\n/);
  assert.match(notice, /For CI, check whether \*\*Watch and fix\*\*/);
  assert.equal(blocked.calls.some(call => call.name === 'dispatch' || call.name === 'mutation'), false);
  assert.equal(commentsFrom(await execute(state)).length, 0);

  state.pr.base.sha = 'c'.repeat(40);
  const changedBase = commentsFrom(await execute(state));
  assert.equal(changedBase.length, 1);
  assert.match(changedBase[0], /@copilot Resolve the merge conflicts/);
  assert.doesNotMatch(changedBase[0], /`ci` check failed|For CI/);
});

test('hourly fallback does not suggest a cloud conflict repair for CI alone or local takeover', async () => {
  const ciOnly = fixture();
  cloud(ciOnly);
  ciOnly.context.eventName = 'schedule';
  ciOnly.checks.find(check => check.name === 'ci').conclusion = 'failure';
  const [ciNotice] = commentsFrom(await execute(ciOnly));
  assert.match(ciNotice, /`ci` check failed.*Watch and fix/s);
  assert.doesNotMatch(ciNotice, /@copilot/);

  const local = fixture();
  cloud(local);
  local.context.eventName = 'schedule';
  local.pr.labels.push({ name: 'handoff:local' });
  local.pr.mergeable = false;
  const [localNotice] = commentsFrom(await execute(local));
  assert.match(localNotice, /merge conflict.*local delivery owner/s);
  assert.doesNotMatch(localNotice, /@copilot/);
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
    if (mode === 'assigned') state.issue.assignees = [{ login: 'Copilot', type: 'Bot' }];
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

test('completion preflight repairs the observed suffix once and refreshes reviews without coding sessions', async () => {
  const state = fixture();
  cloud(state);
  state.pr.body = '- **Remaining work:**\n  - **Builder:** Implement journal screens after the proof review.\n\n'
    + 'Completion: partial\n\n'
    + '<!-- START COPILOT CODING AGENT SUFFIX -->\n\n- Fixes #11';
  const first = await execute(state);
  assert.match(state.pr.body, /- Refs #11$/);
  assert.equal(first.calls.filter(call => call.name === 'updatePr').length, 1);
  assert.equal(first.calls.filter(call => call.name === 'dispatch').length, 4);
  for (const call of first.calls.filter(call => call.name === 'dispatch')) {
    assert.equal(call.args.inputs.review_input, reviewRunInput(call.args.workflow_id, reviewInputKey(state.pr.body)));
  }
  assert.equal(commentsFrom(first).some(body => body.includes('@copilot Fix')), false);
  assert.equal(enables(first).length, 0);
  const again = await execute(state);
  assert.equal(again.calls.some(call => call.name === 'updatePr' || call.name === 'dispatch'), false);
  assert.equal(commentsFrom(again).length, 0);
  state.pr.body = state.pr.body.replace('- Refs #11', '- Fixes #11');
  const regenerated = await execute(state);
  assert.equal(regenerated.calls.filter(call => call.name === 'updatePr').length, 1);
  assert.equal(regenerated.calls.some(call => call.name === 'dispatch'), false, 'regenerated suffix converges on the same review input');
});

test('metadata write failures never become successful readiness', async () => {
  for (const failure of ['updatePr']) {
    const state = fixture();
    state.pr.body = 'Completion: partial\n## Remaining work\nOwner: AI; live verification after access.\n'
      + '<!-- START COPILOT CODING AGENT SUFFIX -->\n- Fixes #11';
    state.fail = failure;
    await assert.rejects(execute(state), new RegExp(`API failure: ${failure}`));
    assert.notEqual(readinessFrom(state).conclusion, 'success');
  }
});

test('completion declarations and acceptance checklists never gate readiness; reviewers judge them', async () => {
  for (const body of ['Completion: partial\nFixes #11', 'Completion: partial\nRefs #11\n## Remaining work\nAfter access.']) {
    const state = fixture();
    state.pr.body = body;
    state.issue.body = '## Acceptance criteria\n- No tracking checklist.';
    for (const role of ROLES) {
      state.runs[role][0].display_title = `Squad Review ${role} · PR #81 · input ${reviewRunInput(role, reviewInputKey(state.pr.body))}`;
    }
    const result = await execute(state);
    assert.equal(readinessFrom(state).conclusion, 'success', readinessFrom(state).output.summary);
    assert.equal(enables(result).length, 1);
    assert.equal(commentsFrom(result).some(text => text.includes('squad-completion-blocked')), false);
  }
});

test('a person removing needs:human resumes the PR and starts a new repair episode; bots do not', async () => {
  for (const [type, resumed] of [['User', true], ['Bot', false]]) {
    const state = fixture();
    state.context.eventName = 'pull_request_target';
    state.context.payload = { ...state.context.payload, action: 'unlabeled', label: { name: 'needs:human' },
      sender: { login: owner, type } };
    const result = await execute(state);
    assert.equal(commentsFrom(result).some(text => text.includes('squad-repair-resume:')), resumed, type);
  }
});

test('completed issue closure warns idempotently only for explicitly completed state with unchecked criteria', async () => {
  const prepare = reason => {
    const state = fixture();
    state.commentLogin = 'github-actions[bot]';
    state.context.eventName = 'issues';
    state.context.payload = {
      action: 'closed',
      issue: { number: 11, state: 'closed', state_reason: reason },
      repository: { default_branch: 'main' },
    };
    state.issue.state = 'closed';
    state.issue.state_reason = reason;
    return state;
  };

  const ignored = prepare('not_planned');
  assert.equal(commentsFrom(await execute(ignored, 'closure')).length, 0);
  assert.equal(ignored.comments.length, 0);

  const completed = prepare('completed');
  const warning = await execute(completed, 'closure');
  assert.equal(commentsFrom(warning).length, 1);
  assert.match(commentsFrom(warning)[0], /closed as completed.*1 acceptance-tracking criterion/i);
  assert.match(commentsFrom(warning)[0], /does not reopen the issue or authorize/i);
  assert.equal(warning.calls.some(call => /update/i.test(call.name)), false);
  const duplicate = await execute(completed, 'closure');
  assert.equal(commentsFrom(duplicate).length, 0);

  const allComplete = prepare('completed');
  allComplete.issue.body = '## Acceptance tracking\n- [x] Browser installation — evidence: PR #125 and test.';
  assert.equal(commentsFrom(await execute(allComplete, 'closure')).length, 0);

  const stale = prepare('completed');
  stale.issue.state_reason = 'not_planned';
  assert.equal(commentsFrom(await execute(stale, 'closure')).length, 0);

  const failedPersistence = prepare('completed');
  failedPersistence.fail = 'comment';
  await assert.rejects(execute(failedPersistence, 'closure'), /API failure: comment/);
  assert.equal(failedPersistence.comments.length, 0);
});

test('metadata preflight stops on a changed description before writing and does not mutate paused PRs', async () => {
  for (const paused of [false, true]) {
    const state = fixture();
    state.pr.body = 'Completion: partial\n## Remaining work\nTest clients after access.\n'
      + '<!-- START COPILOT CODING AGENT SUFFIX -->\n- Fixes #11';
    if (paused) state.pr.labels.push({ name: 'needs:human' });
    let reads = 0;
    state.before = async name => {
      if (name === 'pr' && ++reads === 3) state.pr.body += '\nConcurrent owner edit.';
    };
    const result = await execute(state);
    assert.equal(result.calls.some(call => call.name === 'updatePr'), false);
    assert.equal(enables(result).length, 0);
  }
});

test('meaningful body-only edits refresh even passing reviews; whitespace does not', async () => {
  const whitespace = fixture();
  whitespace.pr.body = ' \r\n\r\n';
  assert.equal((await execute(whitespace)).calls.some(call => call.name === 'dispatch'), false);
  const changed = fixture();
  changed.pr.body = 'Completion: full\nNew acceptance evidence.';
  const result = await execute(changed);
  assert.equal(result.calls.filter(call => call.name === 'dispatch').length, 4);
  assert.equal(enables(result).length, 0);
});

test('a late body change prevents merge readiness even when its completion syntax stays valid', async () => {
  const state = fixture();
  let reads = 0;
  state.before = async name => {
    if (name === 'pr' && ++reads === 3) state.pr.body = 'New scope evidence.';
  };
  assert.equal(enables(await execute(state)).length, 0);
  assert.notEqual(readinessFrom(state).conclusion, 'success');
});

test('missing capability and unattributable later sessions pause once without consuming three retries', async () => {
  for (const outcome of ['capability-blocked', 'external-blocked', null]) {
    const state = fixture();
    cloud(state);
    state.reviews = [humanReview()];
    const request = fixComment(stamp(0));
    state.comments = [request];
    state.runs.copilot = [{ id: 1, status: 'completed', conclusion: 'success', created_at: stamp(1) }];
    if (outcome) report(state, request, state.runs.copilot[0], outcome);
    const result = await execute(state);
    assert.equal(commentsFrom(result).some(body => body.includes('@copilot Fix')), false);
    assert.match(commentsFrom(result).join('\n'), /No attempt was consumed/);
    assert.equal(state.pr.labels.some(label => label.name === 'needs:human'), true);
    assert.equal(commentsFrom(await execute(state)).length, 0);
  }
});

test('explicit maintainer resume resets the repair episode but normal re-evaluation never clears the pause', async () => {
  const state = fixture();
  state.denyWorkflowPrLabelWrite = true;
  cloud(state);
  state.pr.labels.push({ name: 'needs:human' });
  state.reviews = [humanReview()];
  state.comments = [fixComment(stamp(0))];
  state.runs.copilot = [{ id: 1, status: 'completed', conclusion: 'success', created_at: stamp(1) }];
  report(state, state.comments[0], state.runs.copilot[0], 'capability-blocked');
  assert.equal((await execute(state)).calls.some(call => ['removeLabel', 'agentRemoveLabel'].includes(call.name)), false);
  state.context.eventName = 'workflow_dispatch';
  state.context.payload.inputs = { pr_number: '81', resume_repairs: true };
  const resumed = await execute(state);
  assert.deepEqual(resumed.calls.filter(call => call.name === 'agentRemoveLabel').map(call => call.args), [
    { owner, repo: 'bujo-ai', issue_number: 81, name: 'needs:human' },
  ], 'an explicit PR resume must use the PR-write credential, not the workflow token that returned 403');
  assert.equal(state.pr.labels.some(label => label.name === 'needs:human'), false);
  assert.equal(commentsFrom(resumed).filter(body => body.includes('@copilot Fix')).length, 1);
  assert.equal(commentsFrom(await execute(state)).length, 0, 'duplicate resume run must not reset again');
  state.pr.labels.push({ name: 'needs:human' });
  assert.equal((await execute(state)).calls.some(call => ['removeLabel', 'agentRemoveLabel'].includes(call.name)), false, 'a rerun must not clear a newer pause');
});

test('using the workflow token for PR resume reproduces the live 403 and leaves the pause intact', async () => {
  const state = fixture();
  state.denyWorkflowPrLabelWrite = true;
  state.pr.labels.push({ name: 'needs:human' });
  state.context.eventName = 'workflow_dispatch';
  state.context.payload.inputs = { pr_number: '81', resume_repairs: true };
  state.mutateSource = code => code.replace("agent.rest.issues.removeLabel({ owner, repo, issue_number: number, name: 'needs:human' })",
    "github.rest.issues.removeLabel({ owner, repo, issue_number: number, name: 'needs:human' })");
  await assert.rejects(execute(state), /403 Resource not accessible by integration/);
  assert.equal(state.pr.labels.some(label => label.name === 'needs:human'), true);
  assert.equal(readinessFrom(state).conclusion, undefined);
});

test('human-only specialist findings are not sent to the coding agent', async () => {
  const state = fixture();
  cloud(state);
  const check = state.checks.find(item => item.name === 'Squad Review / Verifier');
  check.conclusion = 'failure';
  check.output.summary = 'Repair route: human\nOwner must provide the live-client test window.';
  const result = await execute(state);
  assert.equal(commentsFrom(result).some(body => body.includes('@copilot Fix')), false);
  assert.match(commentsFrom(result)[0], /live-client test window/);
  assert.equal(state.pr.labels.some(label => label.name === 'needs:human'), true);
});

test('explicit repair resume rejects ineligible actors and active cloud sessions without resetting history', async () => {
  for (const blocked of ['permission', 'active']) {
    const state = fixture();
    cloud(state);
    state.pr.labels.push({ name: 'needs:human' });
    state.context.eventName = 'workflow_dispatch';
    state.context.payload.inputs = { pr_number: '81', resume_repairs: true };
    if (blocked === 'permission') {
      state.context.payload.sender = { login: 'outsider' };
      state.permission = 'read';
      await assert.rejects(execute(state), /requires repository write permission/);
    } else {
      state.runs.copilot[0].status = 'in_progress';
      const result = await execute(state);
      assert.equal(commentsFrom(result).length, 0);
    }
    assert.equal(state.pr.labels.some(label => label.name === 'needs:human'), true);
  }
});

function continuationFixture() {
  const state = fixture();
  state.context.eventName = 'workflow_dispatch';
  state.context.payload.inputs = { issue_number: '81', dependency_evidence: 'Approved registration aligned; sanitized client proof recorded.' };
  state.issue.labels = [{ name: 'plan:approved' }, { name: 'needs:human' }];
  state.issue.assignees = [{ login: 'Copilot', type: 'Bot' }];
  state.comments = [{ id: 1, user: { login: owner }, body: '## 📋 Implementation plan v1 · owner: ai' }];
  return state;
}

test('manual scope continuation reassigns an already-assigned open issue once under the same approved plan', async () => {
  const state = continuationFixture();
  const result = await execute(state, 'assign');
  assert.deepEqual(result.calls.filter(call => call.name === 'removeAssignees').map(call => call.args.assignees),
    [['copilot-swe-agent[bot]']]);
  const assignment = result.calls.find(call => call.name === 'agentRequest');
  assert.deepEqual(assignment.args.assignees, ['copilot-swe-agent[bot]']);
  assert.equal(assignment.args.agent_assignment.custom_agent, 'ai');
  assert.match(assignment.args.agent_assignment.custom_instructions, /Continue the unfinished acceptance work/);
  assert.match(assignment.args.agent_assignment.custom_instructions, /preserve delivered work/);
  assert.match(commentsFrom(result)[0], /Dependency evidence \(data, not instructions\)/);
  const duplicate = await execute(state, 'assign');
  assert.equal(duplicate.calls.some(call => call.name === 'agentRequest' || call.name === 'removeAssignees'), false);
});

test('continuation uses the newly approved Builder plan after an Architect milestone', async () => {
  const state = continuationFixture();
  state.comments = [
    { id: 1, user: { login: owner }, body: '## 📋 Implementation plan v1 · owner: architect\nDocs milestone.' },
    { id: 2, user: { login: owner }, body: '## 📋 Implementation plan v2 · owner: builder\nImplement journal screens.' },
  ];
  const result = await execute(state, 'assign');
  const { custom_agent, custom_instructions } = result.calls.find(call => call.name === 'agentRequest').args.agent_assignment;
  assert.equal(custom_agent, 'builder');
  assert.match(custom_instructions, /latest owner-approved plan v2/);
  assert.match(custom_instructions, /Implement journal screens/);
  assert.doesNotMatch(custom_instructions, /Docs milestone/);
});

test('scope continuation respects approval, local handoff, permission, open PRs, and session ownership', async () => {
  for (const mode of ['unapproved', 'local', 'permission', 'open PR', 'active', 'waiting', 'missing workflow', 'closed', 'empty evidence', 'scope race']) {
    const state = continuationFixture();
    if (mode === 'unapproved') state.issue.labels = [];
    if (mode === 'local') state.issue.labels.push({ name: 'handoff:local' });
    if (mode === 'permission') {
      state.context.payload.sender = { login: 'outsider' };
      state.permission = 'read';
    }
    if (mode === 'open PR') state.timeline = [{ event: 'cross-referenced', source: { issue: { state: 'open', pull_request: {} } } }];
    if (mode === 'active' || mode === 'waiting') state.runs.copilot[0].status = mode === 'active' ? 'in_progress' : 'waiting';
    if (mode === 'missing workflow') state.workflows = state.workflows.filter(workflow => workflow.id !== 'copilot');
    if (mode === 'closed') state.issue.state = 'closed';
    if (mode === 'empty evidence') state.context.payload.inputs.dependency_evidence = ' ';
    if (mode === 'scope race') {
      let reads = 0;
      state.before = async name => {
        if (name === 'issue' && ++reads === 2) state.issue.labels = [];
      };
    }
    if (['unapproved', 'local'].includes(mode)) {
      const result = await execute(state, 'assign');
      assert.equal(result.calls.some(call => call.name === 'agentRequest' || call.name === 'removeAssignees'), false, mode);
    } else {
      await assert.rejects(execute(state, 'assign'), /permission|open PR|cloud session|workflow is missing|open issue|evidence|scope or approval changed/i, mode);
      assert.equal(state.comments.some(comment => comment.body.includes('squad-scope-resume:')), false, mode);
    }
  }
});

test('production-source mutations expose missing review-input and completion-preflight gates', async () => {
  const state = fixture();
  state.pr.body = 'New acceptance evidence.';
  state.mutateSource = code => code.replace(
    "const inputKey = split?.key || reviewInputKey(pr.body || '');",
    `const inputKey = '${reviewInputKey('')}';`,
  );
  assert.equal(enables(await execute(state)).length, 0, 'final live input guard must still reject the stale result');
  const bypass = fixture();
  bypass.pr.body = 'Completion: partial\nFixes #11';
  bypass.mutateSource = code => code.replace('if (!completion.ok || referenceFinding || split?.reason) {', 'if (false) {');
  const result = await execute(bypass);
  assert.equal(result.calls.filter(call => call.name === 'dispatch').length, 4,
    'removing preflight incorrectly spends reviews; the preflight regression test asserts zero');
});

test('blocking planning questions revoke approval and prevent assignment under the old plan', async () => {
 const state = fixture();
 const plan = { id: 1, user: { login: owner }, body: '## 📋 Implementation plan v1 · owner: builder' };
 const question = { id: 2, user: { login: owner }, body: '## Planning questions\nWhich browser is required?' };
 state.comments = [plan, question];
 state.issue.labels = [{ name: 'plan:approved' }];
 state.context.eventName = 'issue_comment';
 Object.assign(state.context.payload, { issue: state.issue, comment: question });
 assert.equal((await execute(state, 'plan')).calls.filter(call => call.name === 'removeLabel').length, 1);
 state.issue.labels = [{ name: 'plan:approved' }];
 state.context.eventName = 'issues';
 const result = await execute(state, 'assign');
 assert.match(result.errors.join('\n'), /Planning questions are unresolved/);
 assert.ok(result.calls.some(call => call.name === 'removeLabel'));
 state.comments.splice(1, 0, { ...question, id: 3, body: '## 📋 Implementation plan v2 · owner: builder\nSafari is required.' });
 state.issue.labels = [{ name: 'plan:approved' }];
 assert.equal((await execute(state, 'assign')).errors.length, 0);
});

function scopeSplitFixture() {
 const state = fixture();
 const record = 'Deferred: Add a dark-theme screenshot | reason: Existing help is sufficient | owner: builder | next: Check the updated help';
 state.pr.body = `Completion: full\nCloses #11\nPlan: v1\nScope split: #22\n${record}`;
 const source = { ...state.issue, labels: [{ name: 'plan:approved' }],
   body: `## Acceptance criteria\n- Existing help works\n- Add a dark-theme screenshot\n\nScope split: #22\nSplit PR: #81\n${record}` };
 const followUp = { number: 22, state: 'open', body: `Split from: #11\nSplit PR: #81\n${record}` };
 const plan = { id: 5, user: { login: owner }, body: '## 📋 Implementation plan v1 · owner: builder\nVerify existing help.' };
 state.comments = [plan];
 state.issues = { 11: source, 22: followUp };
 const key = reviewInputKey(JSON.stringify([reviewInputKey(state.pr.body), state.pr.base.sha,
   source.body, followUp.body, plan.id, plan.body]));
 for (const role of ROLES) state.runs[role][0].display_title = `Squad Review ${role} · PR #81 · input ${reviewRunInput(role, key)}`;
 for (const check of state.checks.filter(item => /Architect|Verifier/.test(item.name))) {
   check.output.summary += '\nScope split: verified';
 }
 return state;
}

test('a closing scope split needs live reciprocal records and cannot opt out of review', async () => {
 assert.equal(enables(await execute(scopeSplitFixture())).length, 1);
 for (const change of [
   state => { delete state.issues[22]; },
   state => { state.issues[22].state = 'closed'; },
   state => { state.issues[22].body = state.issues[22].body.replace('updated help', 'existing help'); },
   state => { state.pr.labels.push({ name: 'review:none' }); },
   state => { state.issues[11].labels = []; },
   state => { state.issues[11].body += '\n- Another acceptance criterion'; },
   state => { state.checks.find(check => check.name.endsWith('Verifier')).output.summary = 'Generic approval'; },
 ]) {
   const state = scopeSplitFixture();
   change(state);
   assert.equal(enables(await execute(state)).length, 0);
   assert.notEqual(readinessFrom(state).conclusion, 'success');
 }
 const state = scopeSplitFixture();
 let followUpReads = 0;
 state.before = (name, args) => {
   if (name === 'issue' && args.issue_number === 22 && ++followUpReads === 2) state.issues[22].state = 'closed';
 };
 assert.equal(enables(await execute(state)).length, 0);
 const bypass = scopeSplitFixture();
 bypass.issues[22].state = 'closed';
 bypass.mutateSource = code => code.replace('if (reason) return { reason };', '');
 assert.equal(enables(await execute(bypass)).length, 1,
   'removing live split validation wrongly permits closure with a closed follow-up');
});

test('transient reviewer failures get one retry then escalate if still failing', async () => {
 const state = fixture();
 state.runs.Architect[0].conclusion = 'timed_out';
 const first = await execute(state);
 assert.deepEqual(first.calls.filter(call => call.name === 'dispatch').map(call => call.args.workflow_id), ['Architect']);
 assert.equal(first.calls.some(call => call.name === 'addLabels'), false);
 assert.equal((await execute(state)).calls.some(call => call.name === 'dispatch'), false);
 Object.assign(state.runs.Architect[0], { status: 'completed', conclusion: 'timed_out' });
 const last = await execute(state);
 assert.equal(last.calls.some(call => call.name === 'dispatch'), false);
 assert.ok(last.calls.some(call => call.name === 'addLabels'));
});

test('a green DevOps run without its current-head check retries once, then pauses', async () => {
  const state = fixture();
  state.checks = state.checks.filter(check => check.name !== 'Squad Review / DevOps');
  const dispatches = result => result.calls.filter(call => call.name === 'dispatch');
  const first = await execute(state);
  assert.deepEqual(dispatches(first).map(call => call.args.workflow_id), ['DevOps']);
  assert.match(readinessFrom(state).output.summary, /Retrying DevOps.*Squad Review \/ DevOps check on aaaaaaa/);
  assert.equal(dispatches(await execute(state)).length, 0);

  Object.assign(state.runs.DevOps[0], { status: 'completed', conclusion: 'success' });
  const last = await execute(state);
  assert.equal(dispatches(last).length, 0);
  assert.equal(enables(last).length, 0);
  assert.deepEqual(state.pr.labels.map(label => label.name), ['needs:human']);
  assert.equal(readinessFrom(state).conclusion, 'failure');
  assert.match(commentsFrom(last).join('\n'), /without a matching Squad Review \/ DevOps check on aaaaaaa/);
  assert.equal(dispatches(await execute(state)).length, 0);

  const bypass = fixture();
  bypass.checks = bypass.checks.filter(check => check.name !== 'Squad Review / DevOps');
  bypass.mutateSource = code => code.replace("const missingCheck = item.role === 'DevOps'", 'const missingCheck = false');
  await execute(bypass);
  Object.assign(bypass.runs.DevOps[0], { status: 'completed', conclusion: 'success' });
  assert.equal(dispatches(await execute(bypass)).length, 1, 'removing missing-check detection causes another dispatch');
  assert.equal(bypass.pr.labels.length, 0);
});

test('a DevOps retry with an actual current-head check restores readiness', async () => {
  const state = fixture();
  const check = state.checks.find(item => item.name === 'Squad Review / DevOps');
  state.checks = state.checks.filter(item => item !== check);
  await execute(state);
  Object.assign(state.runs.DevOps[0], { status: 'completed', conclusion: 'success', updated_at: stamp(24) });
  state.checks.push({ ...check, started_at: stamp(21), completed_at: stamp(22) });
  assert.equal(enables(await execute(state)).length, 1);
  assert.equal(readinessFrom(state).conclusion, 'success');
});

test('an old-head DevOps run does not consume the current head retry', async () => {
  const state = fixture();
  state.checks = state.checks.filter(check => check.name !== 'Squad Review / DevOps');
  state.runs.DevOps[0].display_title =
    `Squad Review DevOps · PR #81 · input ${reviewRunInput('DevOps', reviewInputKey(''), 'c'.repeat(40))}`;
  const result = await execute(state);
  assert.deepEqual(result.calls.filter(call => call.name === 'dispatch').map(call => call.args.inputs.review_input),
    [reviewRunInput('DevOps', reviewInputKey(''))]);
  assert.doesNotMatch(commentsFrom(result).join('\n'), /Retrying DevOps/);
});

test('formal rejection and routine metadata findings never re-dispatch the original coding agent', async () => {
 for (const summary of ['Repair route: implementation\nRevision: independent', 'Repair route: metadata']) {
   const state = fixture();
   cloud(state);
   const check = state.checks.find(item => item.name === 'Squad Review / Verifier');
   check.conclusion = 'failure';
   check.output.summary = summary;
   const first = await execute(state);
   assert.equal(enables(first).length, 0);
   assert.equal(first.calls.some(call => call.name === 'addLabels'), false);
   assert.match(commentsFrom(first).join('\n'), /different eligible agent|correct these records/);
   assert.doesNotMatch(commentsFrom(first).join('\n'), /@copilot Fix/);
   assert.equal(commentsFrom(await execute(state)).length, 0);
 }
});

test('a later head cannot clear recorded rejection without current owner confirmation and review', async () => {
 const state = fixture();
 const rejectedSha = 'c'.repeat(40);
 state.comments = [{ id: 33, user: { login: owner },
   body: `<!-- squad-independent-revision:${rejectedSha}:${reviewInputKey('')} -->` }];
 const result = await execute(state);
 assert.equal(enables(result).length, 0);
 assert.match(readinessFrom(state).output.summary, /Independent revision pending/);
 state.reviews = [{ state: 'COMMENTED', commit_id: sha, user: { login: 'other', type: 'User' },
   body: 'Independent revision: 33 by builder/replacement' }];
 assert.equal(enables(await execute(state)).length, 0);
 state.reviews[0].user.login = owner;
 assert.equal(enables(await execute(state)).length, 1);
 state.pr.labels.push({ name: 'review:none' });
 assert.equal(enables(await execute(state)).length, 0);
});

test('missing specialist repair report triggers a review, not another coding session', async () => {
 const state = fixture();
 cloud(state);
 state.comments = [fixComment(stamp(0))];
 state.runs.copilot = [{ id: 1, status: 'completed', conclusion: 'success', created_at: stamp(1) }];
 const check = state.checks.find(item => item.name === 'Squad Review / Verifier');
 check.conclusion = 'failure';
 check.output.summary = 'Repair route: implementation';
 const first = await execute(state);
 assert.deepEqual(first.calls.filter(call => call.name === 'dispatch').map(call => call.args.workflow_id), ['Verifier']);
 assert.equal(first.calls.some(call => call.name === 'addLabels'), false);
 assert.doesNotMatch(commentsFrom(first).join('\n'), /@copilot Fix/);
 assert.equal((await execute(state)).calls.some(call => call.name === 'dispatch'), false);
});
