'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const root = path.resolve(__dirname, '..', '..');
const read = (file) => fs.readFileSync(path.join(root, file), 'utf8');

test('binding caller uses trusted default-branch checkout, plan core, GITHUB_TOKEN writes and owner-only external writes', () => {
  const workflow = read('.github/workflows/squad-plan-binding.yml');
  assert.match(workflow, /workflow_run:\s*\n\s+workflows:\s*\[Squad Plan, Squad Plan Approved\]/);
  assert.match(workflow, /types:\s*\[created, edited, deleted\]/);
  assert.match(workflow, /cron:\s*"\*\/10 \* \* \* \*"/);
  assert.match(workflow, /workflow_dispatch:/);
  assert.match(workflow, /ref:\s*\$\{\{\s*github\.event\.repository\.default_branch\s*\}\}/);
  assert.match(workflow, /require\('\.\/\.github\/scripts\/plan-binding'\)/);
  assert.match(workflow, /checks:\s*write/);
  assert.match(workflow, /pull-requests:\s*write/,
    'GITHUB_TOKEN needs PR write permission for bot comments on linked PRs, even through the issues/comments endpoint');
  assert.match(workflow, /getOctokit\(process\.env\.AGENT_TOKEN\)/);
  assert.match(workflow, /pullRequests\(first:100,after:\$after,states:\[OPEN,MERGED\]\)/);
  assert.match(workflow, /cross-referenced/);
  assert.match(workflow, /plan:waived/);
  assert.match(workflow, /disablePullRequestAutoMerge/);
  assert.doesNotMatch(workflow, /checkout@[^ \n]+\n\s+with:\s*\n\s+ref:\s*\$\{\{\s*github\.event\.pull_request\.head/);
});

test('plan approval writes a bot record before assignment and rechecks both sides of assignment', () => {
  const workflow = read('.github/workflows/squad-plan-approved.yml');
  assert.match(workflow, /prepareApproval\(/);
  assert.match(workflow, /formatRecord\(prepared\.record\)/);
  assert.match(workflow, /issues\.addLabels[\s\S]*plan:bound/);
  assert.match(workflow, /handoff:local/);
  assert.match(workflow, /before the owner-token assignment/);
  assert.match(workflow, /after the bot record and durable label write/);
  assert.match(workflow, /after assignment/);
  assert.match(workflow, /agent_assignment/);
  assert.match(workflow, /waiver/);
});

test('review workflows start only from trusted dispatch and attest before the model', () => {
  const roles = ['architect', 'security', 'verifier', 'devops'];
  for (const role of roles) {
    const source = read(`.github/workflows/squad-review-${role}.md`);
    const lock = read(`.github/workflows/squad-review-${role}.lock.yml`);
    assert.match(source, /workflow_dispatch:/);
    assert.doesNotMatch(source, /^\s+pull_request:/m);
    assert.match(source, /pre-agent-steps:/);
    assert.match(source, /review-attestation\.js start/);
    assert.match(source, /SQUAD_REVIEW_ROLE:/);
    assert.match(lock, /Record verified review-start attestation/);
    assert.match(lock, /GITHUB_RUN_ATTEMPT/);
  }
});

test('the generated Squad Plan lock and binding trigger agree on token-suppressed bot updates', () => {
  const lock = read('.github/workflows/squad-plan.lock.yml');
  const binding = read('.github/workflows/squad-plan-binding.yml');
  assert.match(lock, /^\s*name: "?Squad Plan"?\s*$/m);
  assert.match(lock, /GITHUB_TOKEN/);
  assert.match(lock, /Process Safe Outputs/);
  assert.match(binding, /workflows:\s*\[Squad Plan, Squad Plan Approved\]/);
});

test('gate requires binding and links each result through safe-output job logs rather than check timestamps', () => {
  const gate = read('.github/workflows/squad-review-gate.yml');
  const lock = read('.github/workflows/squad-review-architect.lock.yml');
  assert.match(gate, /REQUIRED_CHECKS: "[^"]*Squad Plan Binding/);
  assert.match(gate, /resolveScopeFromGitHub/);
  assert.match(gate, /publishedCheckIdFromJobLog/);
  assert.match(gate, /Process Safe Outputs/);
  assert.match(gate, /workflowRunAttempt/);
  assert.match(gate, /expectedHeadOid/);
  assert.match(gate, /disablePullRequestAutoMerge/);
  assert.doesNotMatch(gate, /reviewsFinishedAfter/);
  assert.match(lock, /GH_AW_JOB_CHECK_RUN_ID: \$\{\{ job\.check_run_id \}\}/);
  assert.match(lock, /Process Safe Outputs/);
});

function assertPromptDataflow(lock) {
  const activation = lock.match(/\n  activation:\n([\s\S]*?)(?=\n  [a-z_]+:\n)/)?.[1];
  const agent = lock.match(/\n  agent:\n([\s\S]*?)(?=\n  [a-z_]+:\n)/)?.[1];
  assert.ok(activation && agent, 'generated activation and agent jobs must exist');
  assert.doesNotMatch(activation, /review_start\.outputs|SQUAD_REVIEW_PROMPT_FILE/);
  const downloaded = agent.indexOf('name: Download activation artifact');
  const attested = agent.indexOf('name: Record verified review-start attestation');
  const preserved = agent.indexOf('name: Preserve immutable review-start attestation');
  const engine = agent.indexOf('name: Execute GitHub Copilot CLI');
  assert.ok(downloaded >= 0 && downloaded < attested && attested < preserved && preserved < engine,
    'download → attest/inject → preserve → engine must all execute in the same agent job');
  const prompt = agent.match(/SQUAD_REVIEW_PROMPT_FILE: (\S+)/)?.[1];
  const consumed = agent.slice(engine).match(/--prompt-file ([^'\s]+)/)?.[1];
  assert.ok(prompt && consumed, 'both producer and consumer paths must be explicit');
  assert.equal(prompt, consumed, 'trusted step must update exactly the file consumed by the engine');
  assert.doesNotMatch(agent.slice(attested, engine), /name: (Create prompt|Download activation artifact)/,
    'no prompt reconstruction may overwrite the scope between injection and consumption');
}

test('all generated review locks have a real same-job prompt dataflow and reject the old cross-job bug', () => {
  for (const role of ['architect', 'security', 'verifier', 'devops']) {
    const lock = read(`.github/workflows/squad-review-${role}.lock.yml`);
    assertPromptDataflow(lock);
    assert.throws(() => assertPromptDataflow(lock.replace('SQUAD_REVIEW_PROMPT_FILE: /tmp/gh-aw/aw-prompts/prompt.txt',
      'SQUAD_REVIEW_PROMPT_FILE: /tmp/wrong.txt')));
    assert.throws(() => assertPromptDataflow(lock.replace('name: Record verified review-start attestation',
      'name: Missing attestation')));
    assert.throws(() => assertPromptDataflow(lock.replace('  activation:\n', '  activation:\n    stale: ${{ steps.review_start.outputs.scope }}\n')));
  }
});

function workflowScript(file) {
  const workflow = read(`.github/workflows/${file}`).replace(/\r\n/g, '\n');
  const block = workflow.match(/          script: \|\n([\s\S]*?)(?=\n {0,10}\S|$)/)?.[1];
  assert.ok(block, `missing script for ${file}`);
  return block.split('\n').map(line => line.slice(12)).join('\n');
}

async function runCaller(file, github, context, { agent = github, env = {}, requireOverride } = {}) {
  const errors = [];
  const core = { info() {}, error() {}, setFailed: text => errors.push(text) };
  const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
  const load = name => requireOverride?.(name) ?? (name.startsWith('./.github/scripts/')
    ? require(path.join(root, name)) : require(name));
  await new AsyncFunction('require', 'github', 'getOctokit', 'core', 'context', 'process', workflowScript(file))(
    load, github, () => agent, core, context, { env });
  return errors;
}

const approval = require('./plan-approval');

function approvalHarness({ local = false, change } = {}) {
  const plan = {
    id: 10, user: { login: 'owner' }, body: '## 📋 Implementation plan v1 · owner: `devops`\n\nExact original bytes.',
    updated_at: '2026-01-01T10:00:00Z', html_url: 'https://github.test/owner/repo/issues/41#issuecomment-10',
  };
  const event = {
    id: 20, event: 'labeled', actor: { login: 'owner' }, label: { name: 'plan:approved' },
    created_at: '2026-01-01T10:00:02Z',
  };
  const state = {
    comments: [plan], events: Array.from({ length: 100 }, (_, id) => ({ id: id + 100, event: 'commented' })).concat(event),
    labels: ['plan:approved', ...(local ? ['handoff:local'] : [])], calls: [], reads: 0,
  };
  state.replaceScope = () => {
    state.comments.push({ ...plan, id: 3000, body: plan.body.replace('v1', 'v2').replace('`devops`', '`builder`') });
    const newEvent = { ...event, id: 1001, created_at: '2026-01-01T10:00:04Z' };
    state.events.push(newEvent);
    const prepared = approval.prepareApproval({
      issue: 41, owner: 'owner', sender: 'owner', comments: state.comments, labels: state.labels, timeline: state.events,
    });
    assert.equal(prepared.ok, true);
    state.comments.push({ id: 4000, user: { login: approval.BOT }, body: approval.formatRecord(prepared.record) });
  };
  const issues = {
    get: async () => {
      state.reads++;
      change?.(state, state.reads);
      return { data: { labels: state.labels.map(name => ({ name })) } };
    },
    listComments: async () => ({ data: structuredClone(state.comments) }),
    createComment: async ({ body }) => {
      state.calls.push(['comment', body]);
      state.comments.push({ id: 2000 + state.comments.length, user: { login: approval.BOT }, body });
    },
    addLabels: async ({ labels }) => { state.labels.push(...labels); state.calls.push(['labels', labels]); },
    removeLabel: async ({ name }) => { state.labels = state.labels.filter(label => label !== name); state.calls.push(['remove', name]); },
  };
  const github = {
    rest: { issues },
    paginate: async (method, args) => {
      if (typeof method === 'string') {
        assert.ok(method.endsWith('/timeline'));
        state.calls.push(['timeline', state.events.length]);
        return structuredClone(state.events);
      }
      return (await method(args)).data;
    },
    request: async (route, args) => { state.calls.push([route.startsWith('POST') ? 'assign' : 'unassign', args]); },
  };
  const context = {
    repo: { owner: 'owner', repo: 'repo' }, payload: {
      issue: { number: 41 }, repository: { owner: { login: 'owner' }, default_branch: 'main' }, sender: { login: 'owner' },
    },
  };
  state.run = () => runCaller('squad-plan-approved.yml', github, context);
  return state;
}

test('real approval caller paginates owner events and publishes fixed-size canonical handoff records', async () => {
  for (const local of [false, true]) {
    const state = approvalHarness({ local });
    assert.deepEqual(await state.run(), []);
    assert.equal(state.calls.filter(c => c[0] === 'assign').length, local ? 0 : 1);
    const records = state.comments.map(c => approval.parseRecord(c, 41)).filter(Boolean);
    assert.equal(records.at(-1).state, local ? 'local' : 'assigned');
    assert.equal(records.at(-1).approvedAt, '2026-01-01T10:00:02Z');
    const persisted = JSON.parse(records.at(-1).recordComment.body.match(/\{.*\}/)[0]);
    assert.equal(persisted.recordComment, undefined);
    assert.equal(persisted.createdAt, records[0].createdAt);
    const priorAssignments = state.calls.filter(c => c[0] === 'assign').length;
    assert.deepEqual(await state.run(), []);
    assert.equal(state.calls.filter(c => c[0] === 'assign').length, priorAssignments);
  }
});

test('real approval caller cannot mix an old plan body with a newer independently valid approval', async () => {
  for (const read of [2, 3, 4]) {
    const state = approvalHarness({ change: (state, n) => { if (n === read) state.replaceScope(); } });
    const errors = await state.run();
    assert.equal(errors.length, 1);
    const assignments = state.calls.filter(c => c[0] === 'assign');
    assert.equal(assignments.length, read === 4 ? 1 : 0);
    if (assignments.length) {
      assert.equal(assignments[0][1].agent_assignment.custom_agent, 'devops');
      assert.match(assignments[0][1].agent_assignment.custom_instructions, /Exact original bytes/);
      assert.equal(state.calls.filter(c => c[0] === 'unassign').length, 1);
    }
    assert.equal(state.labels.includes('plan:approved'), false);
  }
});

test('real approval caller rejects handoff changes before publication or assignment', async () => {
  for (const local of [false, true]) {
    for (const read of [2, 3]) {
      const state = approvalHarness({ local, change: (state, n) => {
        if (n === read) state.labels = local ? state.labels.filter(l => l !== 'handoff:local') : [...state.labels, 'handoff:local'];
      } });
      assert.equal((await state.run()).length, 1);
      assert.equal(state.calls.some(c => c[0] === 'assign'), false);
    }
  }
});

test('binding caller leaves an owner approval-label event to the approval workflow', async () => {
  const errors = await runCaller('squad-plan-binding.yml', {}, {
    repo: { owner: 'owner', repo: 'repo' }, eventName: 'issues',
    payload: {
      issue: { number: 41 }, action: 'labeled', label: { name: 'plan:approved' },
      sender: { login: 'owner' }, repository: { owner: { login: 'owner' } },
    },
  });
  assert.deepEqual(errors, [], 'no migration/API access is allowed before the approval caller finishes');
});

test('real binding workflow follows PR connection cursors beyond 100 lifetime PRs and paginates timeline evidence', async () => {
  const plan = {
    id: 10, user: { login: 'owner' }, body: '## 📋 Implementation plan v1 · owner: `devops`\n\nExact original bytes.',
    updated_at: '2026-01-01T10:00:00Z',
  };
  const event = { id: 20, event: 'labeled', actor: { login: 'owner' },
    label: { name: 'plan:approved' }, created_at: '2026-01-01T10:00:02Z' };
  const record = approval.prepareApproval({
    issue: 41, owner: 'owner', sender: 'owner', comments: [plan], labels: ['plan:approved'], timeline: [event],
  }).record;
  const calls = [];
  const github = {
    graphql: async (query, variables) => {
      calls.push(['graph', variables.after]);
      assert.match(query, /after:\$after/);
      return { repository: { pullRequests: {
        pageInfo: { hasNextPage: !variables.after, endCursor: variables.after ? 'done' : 'page2' },
        nodes: variables.after ? [{
          number: 151, state: 'OPEN', body: 'Relates to #41\nPlan v1',
          closingIssuesReferences: { nodes: [], pageInfo: { hasNextPage: false } },
        }] : Array.from({ length: 100 }, (_, i) => ({
          number: 200 + i, state: 'MERGED', body: 'Unrelated',
          closingIssuesReferences: { nodes: [], pageInfo: { hasNextPage: false } },
        })),
      } } };
    },
    paginate: async (method, args) => {
      if (typeof method === 'string') {
        assert.ok(method.endsWith('/timeline'));
        calls.push(['timeline', args.issue_number]);
        return args.issue_number === 41 ? [event] : [];
      }
      return (await method(args)).data;
    },
    rest: {
      issues: {
        get: async () => ({ data: { labels: [{ name: 'plan:approved' }, { name: 'plan:bound' }] } }),
        listComments: async () => ({ data: [plan, { id: 11, user: { login: approval.BOT }, body: approval.formatRecord({ ...record, state: 'assigned' }) }] }),
      },
      pulls: { get: async () => ({ data: {
        number: 151, body: 'Relates to #41\nPlan v1', labels: [], head: { sha: 'b'.repeat(40) },
        base: { repo: { name: 'repo', full_name: 'owner/repo' } },
      } }) },
      checks: { create: async args => calls.push(['check', args]) },
    },
  };
  const errors = await runCaller('squad-plan-binding.yml', github, {
    repo: { owner: 'owner', repo: 'repo' }, eventName: 'issue_comment',
    payload: { issue: { number: 41 }, repository: { owner: { login: 'owner' } } },
  });
  assert.deepEqual(errors, []);
  assert.deepEqual(calls.filter(c => c[0] === 'graph').map(c => c[1]), [null, 'page2']);
  const check = calls.find(c => c[0] === 'check')?.[1];
  assert.equal(check?.conclusion, 'success', 'the PR beyond page one must get a live binding status');
  assert.match(check.output.summary, /approval event 20/);
  assert.ok(calls.some(c => c[0] === 'timeline' && c[1] === 151));
});

function waiverHarness({ actor = 'owner', latestEvent = 'labeled', onlyNonowner = false, afterPublish } = {}) {
  const state = {
    calls: [], checks: [],
    pr: { number: 8, state: 'open', draft: false, node_id: 'PR8', auto_merge: {},
      body: 'Completion: partial\nOwner-waived maintenance.\n## Remaining work\nLive rollout.',
      head: { sha: 'b'.repeat(40) }, labels: [{ name: 'plan:waived' }] },
    timeline: [{ id: 20, event: 'labeled', actor: { login: 'owner' }, label: { name: 'plan:waived' } }],
  };
  if (actor !== 'owner' || latestEvent !== 'labeled') {
    state.timeline.push({ id: 21, event: latestEvent, actor: { login: actor }, label: { name: 'plan:waived' } });
  }
  if (onlyNonowner) state.timeline = state.timeline.filter(event => event.actor.login !== 'owner');
  const github = {
    rest: {
      pulls: {
        get: async () => { state.calls.push(['readPr']); return { data: structuredClone(state.pr) }; },
        list: async () => ({ data: [structuredClone(state.pr)] }),
      },
      issues: {
        listForRepo: async () => ({ data: [] }),
        get: async () => { throw new Error('Waiver must not read linked issues'); },
        listComments: async () => { throw new Error('Waiver must not look up an issue approval'); },
        createComment: async () => { throw new Error('Waiver must not fabricate a bot approval record'); },
        addLabels: async () => { throw new Error('Waiver must not manufacture issue approval labels'); },
      },
      checks: {
        listForRef: async ({ ref, check_name }) => {
          assert.equal(check_name, 'Squad Plan Binding');
          return { data: state.checks.filter(check => check.head_sha === ref) };
        },
        create: async args => {
          state.calls.push(['check', args]);
          state.checks.push({ id: state.checks.length + 1, ...args });
          if (args.conclusion === 'success') afterPublish?.(state);
        },
      },
      actions: { createWorkflowDispatch: async args => state.calls.push(['dispatch', args]) },
    },
    paginate: async (method, args) => {
      assert.equal(args.per_page, 100);
      if (typeof method === 'string') {
        assert.ok(method.endsWith('/timeline'));
        assert.equal(args.issue_number, 8, 'only the PR timeline may be read');
        return structuredClone(state.timeline);
      }
      return (await method(args)).data;
    },
    graphql: async query => {
      if (query.includes('disablePullRequestAutoMerge')) {
        state.calls.push(['disable']);
        state.pr.auto_merge = null;
        return {};
      }
      // Only a removed waiver may fall back to real issue-binding discovery.
      assert.equal(state.pr.labels.some(label => label.name === 'plan:waived'), false);
      return { repository: { pullRequest: {
        baseRepository: { nameWithOwner: 'owner/repo' },
        closingIssuesReferences: { nodes: [], pageInfo: { hasNextPage: false } },
      } } };
    },
  };
  state.run = (eventName = 'pull_request_target', action = 'labeled', label = 'plan:waived') => runCaller(
    'squad-plan-binding.yml', github, {
      repo: { owner: 'owner', repo: 'repo' }, eventName,
      payload: {
        action, label: { name: label }, pull_request: { number: 8 },
        repository: { owner: { login: 'owner' }, default_branch: 'main' },
      },
    }, { env: { GH_TOKEN: 'fake', REVIEWS: 'architect,security,verifier,devops' } },
  );
  return state;
}

test('real caller satisfies the globally required check with an exact owner-waiver exemption, not an issue approval', async t => {
  t.mock.method(global, 'fetch', () => { throw new Error('Waiver resolver must not fetch linked issues'); });
  const state = waiverHarness();
  assert.deepEqual(await state.run(), []);
  const check = state.checks.at(-1);
  assert.equal(check.name, 'Squad Plan Binding');
  assert.equal(check.conclusion, 'success');
  assert.equal(check.output.title, 'Owner waiver verified (issue-plan exempt)');
  assert.equal(check.head_sha, state.pr.head.sha);
  assert.equal(check.output.summary, require('./plan-binding').waiverBindingSummary(state.pr, { waiverLabelEvent: 20 }));
  assert.match(check.output.summary, /^Exempt: owner-waived PR #8; waiver event 20; body SHA-256 [a-f0-9]{64}; head b{40}\.$/);
  assert.equal(state.calls.filter(c => c[0] === 'dispatch').length, 0, 'initial waiver dispatch remains owned by the approval caller');
});

test('real caller rejects forged or latest-nonowner/unlabeled waivers and revokes auto-merge', async () => {
  for (const options of [{ actor: 'intruder', onlyNonowner: true }, { actor: 'intruder' }, { latestEvent: 'unlabeled' }]) {
    const state = waiverHarness(options);
    assert.deepEqual(await state.run(), []);
    assert.equal(state.checks.some(check => check.conclusion === 'success'), false);
    assert.equal(state.checks.at(-1).conclusion, 'failure');
    assert.equal(state.calls[1][0], 'disable');
    assert.equal(state.calls.some(c => c[0] === 'dispatch'), false);
  }
});

test('real caller refreshes exemption and reviews after body/head edits and revokes a removed waiver', async () => {
  const state = waiverHarness();
  assert.deepEqual(await state.run(), []);
  for (const action of ['edited', 'synchronize']) {
    state.pr.auto_merge = {};
    if (action === 'edited') state.pr.body += '\nNew exact scope.';
    else state.pr.head.sha = 'c'.repeat(40);
    assert.deepEqual(await state.run('pull_request_target', action), []);
    assert.equal(state.checks.at(-1).head_sha, state.pr.head.sha);
    assert.equal(state.checks.at(-1).output.summary,
      require('./plan-binding').waiverBindingSummary(state.pr, { waiverLabelEvent: 20 }));
    assert.equal(state.pr.auto_merge, null, 'old green reviews must not keep auto-merge enabled after scope changes');
  }
  assert.equal(state.calls.filter(c => c[0] === 'dispatch').length, 8);
  state.pr.labels = [];
  state.pr.auto_merge = {};
  state.timeline.push({ id: 22, event: 'unlabeled', actor: { login: 'owner' }, label: { name: 'plan:waived' } });
  assert.deepEqual(await state.run('pull_request_target', 'unlabeled'), []);
  assert.equal(state.checks.at(-1).name, 'Squad Plan Binding');
  assert.equal(state.checks.at(-1).conclusion, 'failure', 'prior success on the same head must not survive removal');
  assert.equal(state.pr.auto_merge, null);
});

test('manual/periodic/workflow-run sweeps cover existing waivers and recover a lost removal event without changing scope', async () => {
  for (const event of ['workflow_dispatch', 'schedule', 'workflow_run']) {
    const state = waiverHarness();
    const body = state.pr.body;
    assert.deepEqual(await state.run(event), []);
    assert.equal(state.checks.at(-1).conclusion, 'success');
    assert.equal(state.pr.body, body);
    assert.deepEqual(state.pr.labels, [{ name: 'plan:waived' }]);
    const count = state.checks.length;
    assert.deepEqual(await state.run(event), []);
    assert.equal(state.checks.length, count, 'unchanged exemption is idempotent');
    state.pr.labels = [];
    state.timeline.push({ id: 23, event: 'unlabeled', label: { name: 'plan:waived' }, actor: { login: 'owner' } });
    assert.deepEqual(await state.run(event), []);
    assert.equal(state.checks.at(-1).conclusion, 'failure');
  }
});

test('unrelated PR labels cannot cause reconciliation or dispatch loops', async () => {
  for (const action of ['labeled', 'unlabeled']) {
    const state = waiverHarness();
    assert.deepEqual(await state.run('pull_request_target', action, 'needs:human'), []);
    assert.deepEqual(state.calls, []);
  }
  const workflow = read('.github/workflows/squad-plan-binding.yml');
  assert.match(workflow, /ready_for_review, edited, labeled, unlabeled/);
  assert.match(workflow, /github\.event\.label\.name == 'plan:waived'/);
});

test('waiver publication rechecks owner event, exact body and head before dispatch', async () => {
  for (const change of [
    state => { state.pr.body += '\nRaced body'; },
    state => { state.pr.head.sha = 'd'.repeat(40); },
    state => { state.pr.labels = []; },
    state => { state.timeline.push({ id: 99, event: 'labeled', label: { name: 'plan:waived' }, actor: { login: 'intruder' } }); },
    state => { state.timeline.push({ id: 99, event: 'labeled', label: { name: 'plan:waived' }, actor: { login: 'owner' } }); },
  ]) {
    const state = waiverHarness({ afterPublish: change });
    assert.deepEqual(await state.run('pull_request_target', 'edited'), []);
    assert.equal(state.checks.at(-1).head_sha, state.pr.head.sha);
    assert.equal(state.checks.at(-1).conclusion, 'failure');
    assert.equal(state.calls.some(c => c[0] === 'dispatch'), false);
  }
});

function gateHarness({ driftAt, directMerge = false, wrongRun = false, missingOwnCheck = false,
  waived = false, staleWaiverBinding = false, missingBinding = false, missingReadinessRule = false,
  failedCi = false, feedback = [] } = {}) {
  const attestation = require('./review-attestation');
  const sha = 'b'.repeat(40);
  const body = 'Completion: partial\nRelates to #41\nPlan v1\n## Remaining work\nLive rollout.';
  const roles = ['Architect', 'Security', 'Verifier', 'DevOps'];
  const planText = '## 📋 Implementation plan v1 · owner: `devops`\n\nApproved scope.';
  const scope = waived ? { ok: true, waived: true, waiverLabelEvent: 20, scopeText: body } : {
    ok: true, waived: false, issueNumber: 41, planCommentId: 10, planVersion: 1,
    planSha256: approval.digest(planText), ownerLabelEvent: 20, scopeText: planText,
  };
  const state = {
    calls: [], comments: [], scopeReads: 0,
    pr: { number: 8, state: 'open', body, head: { sha, ref: 'feature', repo: { full_name: 'owner/repo' } },
      base: { ref: 'main' }, labels: waived ? [{ name: 'plan:waived' }] : [], node_id: 'PR8',
      created_at: '2026-01-01T10:00:00Z', auto_merge: null, draft: false },
  };
  const artifacts = roles.map((role, i) => JSON.stringify({
    attestation: attestation.makeAttestation({ ...scope, prNumber: 8, body, headSha: sha,
      runId: wrongRun ? 999 : 100 + i, runAttempt: 2, role }),
    scopeText: scope.scopeText,
  }));
  const checks = [
    { id: 1, name: 'ci', status: 'completed', conclusion: failedCi ? 'failure' : 'success' },
    { id: 2, name: 'Squad Plan Binding', status: 'completed', conclusion: 'success', output: {
      summary: waived || staleWaiverBinding === 'removed'
        ? require('./plan-binding').waiverBindingSummary({
          ...state.pr,
          body: staleWaiverBinding === 'body' ? `${state.pr.body}\nOld description` : state.pr.body,
          head: { sha: staleWaiverBinding === 'head' ? 'f'.repeat(40) : sha },
        }, { waiverLabelEvent: staleWaiverBinding === 'event' ? 19 : 20 })
        : `Bound to issue #41, plan v1, SHA-256 ${scope.planSha256}, approval event 20.`,
    } },
    ...roles.map((role, i) => ({
      id: 500 + i, name: `Squad Review / ${role}`, head_sha: sha, status: 'completed', conclusion: 'success',
    })),
  ];
  if (missingOwnCheck) checks.splice(checks.findIndex(check => check.name === 'Squad Review / Architect'), 1);
  if (missingBinding) checks.splice(checks.findIndex(check => check.name === 'Squad Plan Binding'), 1);
  const reviewRun = i => ({ id: 100 + i, run_attempt: 2, event: 'workflow_dispatch', name: `Squad Review ${roles[i]}`,
    status: 'completed', conclusion: 'success' });
  const github = {
    rest: {
      actions: {
        listRepoWorkflows: async () => ({ data: roles.map((role, i) => ({ id: 200 + i, path: `.github/workflows/squad-review-${role.toLowerCase()}.lock.yml` })) }),
        listWorkflowRuns: async ({ workflow_id }) => ({ data: { workflow_runs: [reviewRun(workflow_id - 200)] } }),
        listWorkflowRunArtifacts: async ({ run_id }) => ({ data: { artifacts: [{
          id: 400 + run_id - 100, name: `squad-review-start-${run_id}-2`, expired: false,
        }] } }),
        createWorkflowDispatch: async args => state.calls.push(['dispatch', args]),
      },
      pulls: {
        get: async () => ({ data: structuredClone(state.pr) }),
        list: async () => ({ data: [state.pr] }),
        listReviews: async () => ({ data: [] }),
        merge: async args => { state.calls.push(['merge', args]); state.pr.merged_at = '2026-01-01T11:00:00Z'; },
      },
      issues: {
        listComments: async () => ({ data: state.comments }),
        createComment: async ({ body }) => { state.calls.push(['comment', body]); state.comments.push({
          body, user: { login: 'owner', type: 'User' },
        }); },
        addLabels: async args => state.calls.push(['label', args]),
        addAssignees: async () => {},
      },
      checks: {
        create: async args => {
          state.calls.push(['readiness', args]);
          return { data: { id: 900 } };
        },
        update: async args => { state.calls.push(['readiness-result', args]); },
        listForRef: async () => ({ data: checks }),
        get: async ({ check_run_id }) => ({ data: checks.find(c => c.id === check_run_id) }),
      },
    },
    paginate: async (method, args) => typeof method === 'string'
      ? [] : (await method(args)).data,
    request: async (route, args) => {
      if (route.includes('/rules/branches/')) return { data: [{
        type: 'required_status_checks', parameters: { required_status_checks:
          [...checks.map(check => check.name), ...(missingReadinessRule ? [] : ['Squad Merge Readiness'])]
            .map(context => ({ context })) },
      }] };
      if (route.endsWith('/jobs')) return { data: { jobs: [{
        id: 300 + args.run_id - 100, steps: [{ name: 'Process Safe Outputs' }],
      }] } };
      if (route.includes('/attempts/')) return { data: reviewRun(args.run_id - 100) };
      return { data: [] };
    },
    graphql: async (query, args) => {
      state.calls.push(['graphql', query, args]);
      if (query.includes('reviewThreads')) return { repository: { pullRequest: {
        reviewThreads: { nodes: feedback, pageInfo: { hasNextPage: false } },
      } } };
      if (directMerge && query.includes('enablePullRequestAutoMerge')) throw new Error('Pull request is in clean status');
      return {};
    },
  };
  const env = {
    HUMAN: 'owner', GH_TOKEN: 'fake',
    REQUIRED_CHECKS: ['ci', 'Squad Plan Binding', ...roles.map(role => `Squad Review / ${role}`)].join(','),
    REVIEW_WORKFLOWS: roles.map(role => `${role}:squad-review-${role.toLowerCase()}.lock.yml`).join(','),
    MAX_ROUNDS: '3',
  };
  state.fetch = async url => {
    const artifact = url.match(/\/artifacts\/(\d+)\/zip$/);
    if (artifact) return new Response(artifacts[Number(artifact[1]) - 400]);
    const job = url.match(/\/jobs\/(\d+)\/logs$/);
    assert.ok(job, `unexpected API ${url}`);
    const i = Number(job[1]) - 300;
    return {
      ok: true, status: 200, text: async () => `2026-01-01T11:00:00Z ✓ Created check run "Squad Review / ${roles[i]}" #${500 + i}: https://github.test/owner/repo/runs/0\n`,
      arrayBuffer: async () => { throw new Error('plaintext job log must never be treated as ZIP'); },
    };
  };
  state.run = (runOverrides = {}) => runCaller('squad-review-gate.yml', github, {
    repo: { owner: 'owner', repo: 'repo' }, payload: {
      repository: { default_branch: 'main' }, workflow_run: {
        ...reviewRun(0), head_branch: 'main', head_sha: 'a'.repeat(40), pull_requests: [],
        display_title: 'Squad Review Architect · PR #8', html_url: 'https://github.test/owner/repo/actions/runs/100',
        ...runOverrides,
      },
    },
  }, { env, requireOverride: name => {
    if (name.endsWith('/review-attestation')) return { ...attestation, resolveScopeFromGitHub: async () => {
      state.scopeReads++;
      return state.scopeReads === driftAt ? { ok: false, reason: 'Plan drift' } : scope;
    } };
    if (name === 'node:child_process') return { execFileSync: (program, args) => {
      // Only the uploaded artifact archive goes through the ZIP transport adapter.
      assert.equal(program, 'unzip');
      assert.match(args[1], /review-start-\d+-2\.zip$/);
      return fs.readFileSync(args[1], 'utf8');
    } };
    return undefined;
  } });
  return state;
}

test('real gate links dispatched main-branch review results through artifact and plaintext job logs before enabling auto-merge', async t => {
  const state = gateHarness();
  t.mock.method(global, 'fetch', state.fetch);
  assert.deepEqual(await state.run(), []);
  const enabled = state.calls.find(c => c[0] === 'graphql' && c[1].includes('enablePullRequestAutoMerge'));
  assert.ok(enabled);
  assert.equal(enabled[2].head, 'b'.repeat(40));
  assert.equal(state.scopeReads, 4, 'scope must be read during evaluation and before/after enabling auto-merge');
});

test('real gate requires and accepts the owner-waiver exemption alongside all other globally required checks', async t => {
  const state = gateHarness({ waived: true });
  t.mock.method(global, 'fetch', state.fetch);
  assert.deepEqual(await state.run(), []);
  assert.ok(state.calls.some(c => c[0] === 'graphql' && c[1].includes('enablePullRequestAutoMerge')));
});

test('real gate cannot bypass the required binding check for missing/stale/removed waivers', async t => {
  for (const options of [
    { waived: true, missingBinding: true },
    ...['event', 'body', 'head'].map(staleWaiverBinding => ({ waived: true, staleWaiverBinding })),
    { staleWaiverBinding: 'removed' },
  ]) {
    const state = gateHarness(options);
    state.pr.auto_merge = {};
    t.mock.method(global, 'fetch', state.fetch);
    assert.deepEqual(await state.run(), []);
    assert.equal(state.calls.some(c => c[0] === 'graphql' && c[1].includes('enablePullRequestAutoMerge')), false);
    assert.equal(state.calls.some(c => c[0] === 'merge'), false);
    assert.ok(state.calls.some(c => c[0] === 'graphql' && c[1].includes('disablePullRequestAutoMerge')));
  }
});

test('real gate rejects wrong-run artifacts and re-dispatches at most once for the exact revision', async t => {
  const state = gateHarness({ wrongRun: true });
  t.mock.method(global, 'fetch', state.fetch);
  assert.deepEqual(await state.run(), []);
  assert.deepEqual(await state.run(), []);
  assert.equal(state.calls.filter(c => c[0] === 'dispatch').length, 4);
  assert.equal(state.calls.some(c => c[0] === 'graphql' && c[1].includes('enablePullRequestAutoMerge')), false);
});

test('real gate protects native auto-merge against live plan changes without direct merging', async t => {
  for (const driftAt of [3, 4]) {
    const state = gateHarness({ driftAt });
    state.pr.auto_merge = {};
    t.mock.method(global, 'fetch', state.fetch);
    assert.equal((await state.run()).length, 1, 'merge-time drift must be reported explicitly');
    assert.equal(state.calls.filter(c => c[0] === 'merge').length, 0);
    assert.ok(state.calls.some(c => c[0] === 'graphql' && c[1].includes('disablePullRequestAutoMerge')));
    assert.equal(state.calls.some(c => c[0] === 'readiness-result'), false);
  }
});

test('native auto-merge is armed only while the required readiness check is pending', async t => {
  const state = gateHarness();
  t.mock.method(global, 'fetch', state.fetch);
  assert.deepEqual(await state.run(), []);
  const pending = state.calls.findIndex(c => c[0] === 'readiness');
  const armed = state.calls.findIndex(c => c[0] === 'graphql' && c[1].includes('enablePullRequestAutoMerge'));
  const passed = state.calls.findIndex(c => c[0] === 'readiness-result');
  assert.ok(pending >= 0 && armed > pending && passed > armed);
  assert.equal(state.calls[passed][1].conclusion, 'success');
  assert.equal(state.calls.some(c => c[0] === 'merge'), false);
});

test('gate fails closed for missing rules and clean-status API failures instead of merging directly', async t => {
  for (const options of [{ missingReadinessRule: true }, { directMerge: true }]) {
    const state = gateHarness(options);
    t.mock.method(global, 'fetch', state.fetch);
    assert.equal((await state.run()).length, 1);
    assert.equal(state.calls.some(c => c[0] === 'merge' || c[0] === 'readiness-result'), false);
  }
});

test('CI-only failure belongs to Watch and fix and consumes no review repair round', async t => {
  const state = gateHarness({ failedCi: true });
  t.mock.method(global, 'fetch', state.fetch);
  assert.deepEqual(await state.run(), []);
  assert.equal(state.calls.some(c => c[0] === 'comment' || c[0] === 'readiness-result'), false);
});

test('needs:human is a stop, never automatically cleared by green checks', async () => {
  const state = gateHarness();
  state.pr.labels.push({ name: 'needs:human' });
  state.pr.auto_merge = {};
  assert.deepEqual(await state.run(), []);
  assert.equal(state.calls.some(c => c[0] === 'readiness-result'), false);
  assert.ok(state.calls.some(c => c[0] === 'graphql' && c[1].includes('disablePullRequestAutoMerge')));
});

test('unresolved trusted reviewer feedback blocks merge readiness even with all checks green', async t => {
  const state = gateHarness({ feedback: [{
    id: 'THREAD1', isResolved: false, isOutdated: false,
    comments: { nodes: [{ body: 'Fix this edge case.', url: 'https://github.test/thread/1',
      updatedAt: '2026-01-01T10:00:00Z', author: { login: 'owner', __typename: 'User' },
      authorAssociation: 'OWNER', pullRequestReview: { state: 'COMMENTED' } }] },
  }] });
  t.mock.method(global, 'fetch', state.fetch);
  assert.deepEqual(await state.run(), []);
  assert.equal(state.calls.some(c => c[0] === 'readiness-result'), false);
  assert.equal(state.calls.some(c => c[0] === 'graphql' && c[1].includes('enablePullRequestAutoMerge')), false);
});

test('failed dispatched reviewer with no PR head association escalates once before pending-check handling', async t => {
  const state = gateHarness();
  t.mock.method(global, 'fetch', () => { throw new Error('A pre-attestation crash has no artifact or result'); });
  const failed = { conclusion: 'failure', pull_requests: [], head_sha: 'a'.repeat(40) };
  assert.deepEqual(await state.run(failed), []);
  assert.deepEqual(await state.run(failed), []);
  assert.equal(state.calls.filter(c => c[0] === 'label').length, 1);
  assert.equal(state.calls.filter(c => c[0] === 'comment').length, 1);
  assert.match(state.calls.find(c => c[0] === 'comment')[1], /pr_number=8/);
  assert.equal(state.scopeReads, 0, 'failure must be visible even when no review-start attestation exists');
});

test('completed dispatch missing its own result does not silently wait for an unassociated check', async () => {
  const state = gateHarness({ missingOwnCheck: true });
  state.pr.auto_merge = {};
  assert.deepEqual(await state.run(), []);
  assert.deepEqual(await state.run(), []);
  assert.equal(state.calls.filter(c => c[0] === 'label').length, 1);
  assert.equal(state.calls.filter(c => c[0] === 'comment').length, 1);
  assert.ok(state.calls.some(c => c[0] === 'graphql' && c[1].includes('disablePullRequestAutoMerge')));
  assert.equal(state.calls.some(c => c[0] === 'dispatch'), false, 'do not create an unbounded reviewer retry loop');
});

test('the existing plan safe-output artifact is not modified and agent scope uses verified records', () => {
  const planLock = read('.github/workflows/squad-plan.lock.yml');
  assert.match(planLock, /safe-outputs/);
  for (const role of ['architect', 'security', 'verifier', 'devops']) {
    const agent = read(`.github/agents/${role}.agent.md`);
    assert.match(agent, /verified approval record/);
  }
});
