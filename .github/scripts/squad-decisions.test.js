'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const {
  REQUEST, card, command, decisionState, loadState, requestComment, requestRecordBody, teamsDecision,
} = require('./squad-decisions');
const { handleDecision, notify, notifyBlocker, preserveRequests, resumeAmendments } = require('./squad-decision-controller');
const owner = 'owner';
const stamp = '2026-11-24T10:00:00Z';
const now = Date.parse(stamp) + 60000;
const human = (body, id = 20) => ({
  id, body, user: { login: owner, type: 'User' }, created_at: stamp, updated_at: stamp,
});
const bot = (body, id = 30) => ({ ...human(body, id), user: { login: 'github-actions[bot]', type: 'Bot' } });
const request = (overrides = {}) => human(`${REQUEST}\n\n\`\`\`json\n${JSON.stringify({
  issue: 1, plan: 10, before: 'Old scope', after: 'New scope', reason: 'Reason',
  impact: 'Explicit acceptance, security/privacy, cost and dependency impact',
  verification: 'Focused tests', expires: '2026-11-25T10:00:00Z', ...overrides,
})}\n\`\`\``);
const baselines = new Map([[1, { approved: true, plan: 10 }]]);
const receipt = (item, decision = 'approve', id = 30) =>
  ({ ...bot(`<!-- squad-decision:${item.id}:${requestComment(item, owner).key}:${decision} -->\nDecision`, id),
    created_at: item.created_at, updated_at: item.created_at });
const state = (comments, baseline = baselines) => decisionState(comments, owner, baseline, now);

test('only immutable eligible requests with exact fields, real baseline and bounded expiry are pending', () => {
  const item = request();
  assert.equal(state([item]).requests[0].status, 'pending');
  for (const invalid of [
    { ...item, updated_at: '2026-11-24T10:01:00Z' },
    request({ extra: true }), request({ plan: -1 }), request({ after: '' }),
    request({ expires: 'not a date' }), request({ expires: '2028-01-01' }),
  ]) {
    assert.equal(state([invalid]).blocked, true);
    assert.equal(state([invalid]).requests[0].status, 'invalid');
  }
  assert.equal(state([{ ...item, user: { login: 'stranger', type: 'User' } }]).requests.length, 0);
  assert.equal(state([item], new Map([[1, { approved: true, plan: 11 }]])).requests[0].status, 'stale');
  assert.equal(state([item], new Map([[1, { approved: false, plan: 10 }]])).blocked, true);
  assert.equal(decisionState([item], owner, baselines, now + 2 * 86400000).requests[0].status, 'expired');
});

test('approval binds exact content; forged, edited, late and deleted approval data never grants scope', () => {
  const item = request();
  const accepted = receipt(item);
  assert.equal(state([item, accepted]).amendments[0].data.after, 'New scope');
  assert.equal(state([item, accepted]).blocked, false);
  for (const invalid of [
    { ...accepted, user: { login: owner, type: 'User' } },
    { ...accepted, updated_at: '2026-11-24T10:01:00Z' },
    { ...accepted, body: accepted.body.replace(requestComment(item, owner).key, 'a'.repeat(64)) },
    { ...accepted, created_at: '2026-11-27T10:00:00Z', updated_at: '2026-11-27T10:00:00Z' },
  ]) assert.equal(state([item, invalid]).amendments.length, 0);
  assert.equal(state([accepted]).blocked, true);
  assert.equal(state([request({ after: 'Changed behind card' }), accepted]).blocked, true);
  assert.equal(state([item, receipt(item, 'reject')]).amendments.length, 0);
});

test('only an unedited owner command is an approval request, never natural language or a bot proxy', () => {
  assert.equal(command(human('/approve-change 20'), owner).decision, 'approve');
  assert.equal(command(human('/reject-change 20 use the old scope'), owner).reason, 'use the old scope');
  for (const invalid of [
    human('approved'), human('/approve-change 20\nand request 21'), bot('/approve-change 20'),
    { ...human('/approve-change 20'), user: { login: 'stranger', type: 'User' } },
    { ...human('/approve-change 20'), updated_at: '2026-11-24T11:00:00Z' },
  ]) assert.equal(command(invalid, owner), null);
});

test('first valid decision wins and expired or deleted records have an explicit rejection recovery', () => {
  const item = request();
  assert.equal(state([item, receipt(item), receipt(item, 'reject', 31)]).requests[0].status, 'approved');
  assert.equal(state([item, receipt(item, 'reject'), receipt(item, 'approve', 31)]).requests[0].status, 'rejected');
  const early = { ...receipt(item), created_at: '2026-11-23T10:00:00Z', updated_at: '2026-11-23T10:00:00Z' };
  assert.equal(state([item, early]).amendments.length, 0);
  const later = { ...receipt(item, 'reject', 31), created_at: '2026-11-27T10:00:00Z', updated_at: '2026-11-27T10:00:00Z' };
  assert.equal(state([item, later]).blocked, false);
  assert.equal(state([receipt(item), later]).requests[0].status, 'rejected');
  const edited = { ...item, body: item.body.replace('New scope', 'Changed scope'), updated_at: '2026-11-24T11:00:00Z' };
  const rejection = bot(`<!-- squad-decision:${edited.id}:${requestComment(edited, owner).key}:reject -->\nRejected`, 32);
  assert.equal(state([edited, receipt(item), rejection]).requests[0].status, 'rejected');
  const timestampEdit = { ...item, updated_at: '2026-11-24T11:00:00Z' };
  assert.equal(state([timestampEdit, receipt(item), receipt(item, 'reject', 32)]).blocked, false);
  const revised = new Map([[1, { approved: true, plan: 11 }]]);
  assert.equal(state([item, receipt(item)], revised).requests[0].status, 'stale');
  assert.equal(state([item, receipt(item), receipt(item, 'reject', 32)], revised).blocked, false);
});

test('archived requests retain their eligible source identity, including bot-authored requests', () => {
  for (const user of [
    { login: owner, type: 'User' },
    { login: 'copilot-swe-agent[bot]', type: 'Bot' },
    { login: 'Copilot', type: 'Bot' },
    { login: 'github-actions[bot]', type: 'Bot' },
  ]) {
    const item = { ...request(), user };
    const archived = bot(requestRecordBody(item, owner), 40);
    const approved = receipt(item, 'approve', 41);
    const result = state([item, archived, approved]);
    assert.equal(result.requests[0].status, 'approved', `${user.login} source should remain valid`);
    assert.equal(result.blocked, false, `${user.login} source should be approvable after archival`);
  }
});

test('archived request records cannot impersonate a source author or another bot marker', () => {
  const item = { ...request(), user: { login: 'github-actions[bot]', type: 'Bot' } };
  const archiveBody = requestRecordBody(item, owner);
  const archive = bot(archiveBody, 40);
  const approval = receipt(item, 'approve', 41);
  const changedAuthor = { ...item, user: { login: 'Copilot', type: 'Bot' } };
  assert.equal(state([changedAuthor, archive, approval]).requests[0].status, 'invalid');

  const forgedArchive = { ...bot(archiveBody, 42), user: { login: 'copilot-swe-agent[bot]', type: 'Bot' } };
  assert.equal(state([item, forgedArchive, approval]).requests[0].status, 'approved',
    'only the trusted archive bot can create an archive record');
});

test('Teams decisions require enabled trusted transport identity and verified responder, not card claims', () => {
  const input = {
    enabled: 'true', actor: 'trusted-flow', expectedActor: 'trusted-flow',
    responder: 'verified-owner', expectedResponder: 'verified-owner',
    request: '20', key: 'a'.repeat(64), decision: 'approve',
  };
  assert.equal(teamsDecision(input).request, 20);
  for (const mismatch of [{ enabled: '' }, { actor: owner }, { responder: 'someone-else' },
    { expectedActor: '' }, { key: 'bad' }, { request: '20x' }, { decision: 'yes' }]) {
    assert.throws(() => teamsDecision({ ...input, ...mismatch }));
  }
});

test('personal card contains exact change, impact, expiry and identifiers but no responder identity field', () => {
  const parsed = requestComment(request(), owner);
  parsed.url = 'https://github.com/owner/repo/pull/2#issuecomment-20';
  const result = card({ request: parsed, title: 'Decision needed' });
  assert.equal(result.actions[0].data.request, '20');
  assert.equal(result.actions[0].data.key, parsed.key);
  assert.equal(result.actions[0].data.responder, undefined);
  assert.equal(result.actions[2].url, parsed.url);
  assert.ok(result.body.some(item => item.text.includes('New scope')));
  assert.ok(result.body.some(item => item.text.includes('cost and dependency')));
});

function fixture() {
  const item = request({ expires: new Date(Date.now() + 86400000).toISOString() });
  item.created_at = item.updated_at = new Date().toISOString();
  item.html_url = 'https://github.com/owner/repo/pull/2#issuecomment-20';
  const comments = [item];
  const effects = [];
  const plan = human('## 📋 Implementation plan v1 · owner: builder\n\nScope', 10);
  const issueComments = [plan];
  const prLabels = [];
  const pr = { number: 2, state: 'open', html_url: 'https://github.com/owner/repo/pull/2',
    body: 'Relates to #1', labels: prLabels, auto_merge: null, node_id: 'PR_2',
    head: { repo: { full_name: 'owner/repo' } } };
  const github = {
    paginate: async (_fn, args) => args.issue_number === 1 ? structuredClone(issueComments) : structuredClone(comments),
    rest: {
      pulls: { get: async () => ({ data: structuredClone(pr) }) },
      issues: {
        get: async () => ({ data: { state: 'open', labels: [{ name: 'plan:approved' }] } }),
        listComments: () => {},
        createComment: async args => {
          effects.push(args);
          const entry = bot(args.body, 40 + comments.length);
          entry.created_at = entry.updated_at = new Date().toISOString();
          if (args.issue_number === 2) comments.push(entry);
          if (args.issue_number === 1) issueComments.push(entry);
          return { data: entry };
        },
      },
    },
  };
  const agent = {
    rest: { actions: { createWorkflowDispatch: async args => effects.push(args) },
      issues: {
        addLabels: async args => {
          effects.push(args);
          for (const name of args.labels) {
            if (!prLabels.some(label => label.name === name)) prLabels.push({ name });
          }
        },
        createComment: async args => {
          effects.push(args);
          const entry = bot(args.body, 400 + comments.length);
          entry.issue_number = args.issue_number;
          entry.created_at = entry.updated_at = new Date().toISOString();
          if (args.issue_number === 2) comments.push(entry);
          return { data: entry };
        },
      } },
    graphql: async (query, args) => {
      effects.push({ query, args });
      if (query.includes('disablePullRequestAutoMerge')) pr.auto_merge = null;
      return {};
    },
  };
  const context = {
    repo: { owner, repo: 'repo' }, eventName: 'issue_comment',
    payload: { issue: { number: 2 }, repository: { default_branch: 'main' }, action: 'created' },
  };
  return { github, agent, context, core: { info: () => {} }, env: {}, comments, effects, pr, prLabels };
}

test('production handler admits GitHub and Teams through the same receipt and rejects duplicates and stale cards', async () => {
  for (const surface of ['GitHub', 'Teams']) {
    const f = fixture();
    const decision = human('/approve-change 20', 21);
    decision.created_at = decision.updated_at = new Date().toISOString();
    f.comments.push(decision);
    f.context.payload.comment = decision;
    if (surface === 'Teams') {
      f.context.eventName = 'workflow_dispatch';
      f.context.actor = 'trusted-flow';
      f.context.payload.inputs = {
        pr_number: '2', request: '20', decision: 'approve',
        key: requestComment(f.comments[0], owner).key, responder: 'verified',
      };
      f.env = { SQUAD_TEAMS_APPROVALS: 'true', SQUAD_TEAMS_APPROVAL_ACTOR: 'trusted-flow', SQUAD_TEAMS_APPROVER_ID: 'verified' };
    }
    await handleDecision(f);
    assert.equal(f.effects.filter(item => item.body?.startsWith('<!-- squad-decision:')).length, 1);
    assert.equal(f.effects.filter(item => item.issue_number === 1).length, 1);
    await handleDecision(f);
    assert.equal(f.effects.filter(item => item.body?.startsWith('<!-- squad-decision:')).length, 1);
    if (surface === 'Teams') {
      f.context.payload.inputs.key = 'a'.repeat(64);
      await assert.rejects(handleDecision(f), /stale/);
    }
  }
});

test('production handler refuses a forged Teams responder without a receipt or workflow dispatch', async () => {
  const f = fixture();
  f.context.eventName = 'workflow_dispatch';
  f.context.actor = 'trusted-flow';
  f.context.payload.inputs = { pr_number: '2', request: '20', decision: 'approve', responder: 'forged', key: 'a'.repeat(64) };
  f.env = { SQUAD_TEAMS_APPROVALS: 'true', SQUAD_TEAMS_APPROVAL_ACTOR: 'trusted-flow', SQUAD_TEAMS_APPROVER_ID: 'verified' };
  await assert.rejects(handleDecision(f), /identity/);
  assert.deepEqual(f.effects, []);
});

test('rerunning an admitted approval repairs a failed issue reference without duplicating authority', async () => {
  const f = fixture();
  const choice = human('/approve-change 20', 21);
  choice.created_at = choice.updated_at = new Date().toISOString();
  f.comments.push(choice);
  f.context.payload.comment = choice;
  const create = f.github.rest.issues.createComment;
  let fail = true;
  f.github.rest.issues.createComment = async args => {
    if (args.issue_number === 1 && fail) { fail = false; throw new Error('Issue reference unavailable'); }
    return create(args);
  };
  await assert.rejects(handleDecision(f), /Issue reference unavailable/);
  await handleDecision(f);
  await handleDecision(f);
  assert.equal(f.effects.filter(item => item.body?.startsWith('<!-- squad-decision:')).length, 1);
  assert.equal(f.effects.filter(item => item.body?.startsWith('<!-- squad-amendment-reference:')).length, 1);
});

test('request baseline must be linked on this PR and live approval is rechecked before a receipt', async () => {
  const f = fixture();
  const args = { github: f.github, owner, repo: 'repo', comments: f.comments };
  assert.equal((await loadState({ ...args, pr: { body: 'Relates to #999' } })).blocked, true);
  assert.equal((await loadState({ ...args, pr: { body: 'Relates to #1' } })).requests[0].status, 'pending');
  const choice = human('/approve-change 20', 21);
  choice.created_at = choice.updated_at = new Date().toISOString();
  f.comments.push(choice);
  f.context.payload.comment = choice;
  let reads = 0;
  f.github.rest.issues.get = async () => ({ data: { state: 'open', labels: ++reads === 1 ? [{ name: 'plan:approved' }] : [] } });
  await assert.rejects(handleDecision(f), /changed during/);
  assert.equal(f.effects.some(item => item.body?.startsWith('<!-- squad-decision:')), false);
  assert.equal(f.effects.some(item => item.workflow_id === 'squad-pr-lifecycle.yml'), false);
});

test('unauthorized approval comments cannot trigger notifications or privileged lifecycle dispatch', async () => {
  const f = fixture();
  const choice = { ...human('/approve-change 20', 21), user: { login: 'stranger', type: 'User' } };
  f.comments.push(choice);
  f.context.payload.comment = choice;
  await handleDecision(f);
  assert.equal(f.effects.some(item => item.body?.startsWith('<!-- squad-decision:')), false);
  assert.equal(f.effects.some(item => item.workflow_id === 'squad-pr-lifecycle.yml'), false);
  assert.equal(f.effects.some(item => item.body?.startsWith('<!-- squad-request-record:')), true);
});

test('removing an approved request header still reconciles the previous body and pauses merge', async () => {
  const f = fixture();
  const item = f.comments[0];
  const previous = item.body;
  f.comments.push(receipt(item));
  item.body = 'Removed request content';
  item.updated_at = new Date(Date.now() + 1000).toISOString();
  f.context.payload.action = 'edited';
  f.context.payload.comment = structuredClone(item);
  f.context.payload.changes = { body: { from: previous } };
  const get = f.github.rest.pulls.get;
  f.github.rest.pulls.get = async () => {
    const result = await get();
    result.data.auto_merge = {};
    result.data.node_id = 'PR_2';
    return result;
  };
  f.agent.graphql = async query => f.effects.push({ query });
  await handleDecision(f);
  assert.ok(f.effects.some(item => item.query?.includes('disablePullRequestAutoMerge')));
  assert.ok(f.effects.some(item => item.body?.includes('/reject-change 20')));
  assert.equal(f.effects.filter(item => item.body?.startsWith('<!-- squad-decision:')).length, 0);
});

test('deleted pending requests remain auditable and blocked until the owner explicitly rejects them', async () => {
  const f = fixture();
  const original = f.comments.shift();
  f.context.payload.action = 'deleted';
  f.context.payload.comment = structuredClone(original);
  const get = f.github.rest.pulls.get;
  f.github.rest.pulls.get = async args => {
    const result = await get(args);
    result.data.auto_merge = {};
    result.data.node_id = 'PR_2';
    return result;
  };
  f.agent.graphql = async query => f.effects.push({ query });

  await handleDecision(f);
  const record = f.comments.find(comment => comment.body?.startsWith('<!-- squad-request-record:20:'));
  assert.ok(record, 'the deletion event must preserve the original request snapshot');
  let stateAfterDelete = await loadState({
    github: f.github, owner, repo: 'repo',
    pr: { body: 'Relates to #1' }, comments: f.comments,
  });
  assert.equal(stateAfterDelete.requests[0].status, 'invalid');
  assert.equal(stateAfterDelete.blocked, true);
  assert.ok(f.effects.some(item => item.query?.includes('disablePullRequestAutoMerge')));

  const rejection = human('/reject-change 20', 21);
  rejection.created_at = rejection.updated_at = new Date().toISOString();
  f.comments.push(rejection);
  f.context.payload.action = 'created';
  f.context.payload.comment = rejection;
  await handleDecision(f);
  stateAfterDelete = await loadState({
    github: f.github, owner, repo: 'repo',
    pr: { body: 'Relates to #1' }, comments: f.comments,
  });
  assert.equal(stateAfterDelete.blocked, false);
  assert.ok(f.comments.some(comment => comment.body?.startsWith('<!-- squad-decision:20:')));
});

test('failed archive of a deleted approved request pauses prior authorization, records recovery, and can be safely retried', async () => {
  const f = fixture();
  const original = f.comments.shift();
  f.comments.push(receipt(original));
  f.context.payload.action = 'deleted';
  f.context.payload.comment = structuredClone(original);
  f.pr.auto_merge = { merge_method: 'squash' };
  let archiveFailure = true;
  const createComment = f.github.rest.issues.createComment;
  f.github.rest.issues.createComment = async args => {
    if (args.body.startsWith('<!-- squad-request-record:') && archiveFailure) {
      archiveFailure = false;
      throw new Error('archive API temporarily unavailable');
    }
    return createComment(args);
  };

  await assert.rejects(handleDecision(f), /archive API temporarily unavailable/);
  assert.equal(f.pr.auto_merge, null, 'the previous auto-merge authorization is disabled before surfacing failure');
  assert.ok(f.prLabels.some(label => label.name === 'needs:human'));
  const recoveryAudit = f.comments.find(comment => comment.body?.startsWith('<!-- squad-request-archive-failure:2:20 -->'));
  assert.match(recoveryAudit?.body || '', /could not be confirmed.*no archive success or approval is inferred/,
    'the recovery audit does not claim the archive write definitely failed');
  assert.equal(f.comments.some(comment => comment.body?.startsWith('<!-- squad-request-record:20:')), false,
    'archive failure must not be represented as successful preservation');
  assert.ok(f.effects.some(item => item.workflow_id === 'squad-pr-lifecycle.yml'),
    'the lifecycle is dispatched to invalidate prior readiness');

  await handleDecision(f);
  const record = f.comments.find(comment => comment.body?.startsWith('<!-- squad-request-record:20:'));
  assert.ok(record, 'rerunning after recovery creates the immutable archive record');
  const recovered = await loadState({
    github: f.github, owner, repo: 'repo',
    pr: { body: 'Relates to #1' }, comments: f.comments,
  });
  assert.equal(recovered.requests[0].status, 'invalid');
  assert.equal(recovered.blocked, true);
  assert.ok(f.prLabels.some(label => label.name === 'needs:human'),
    'recovery does not clear the independent human pause');
});

test('failed archive of a newly created request pauses prior authorization and reconciles before retry', async () => {
  const f = fixture();
  const created = request({ expires: new Date(Date.now() + 86400000).toISOString() });
  created.id = 21;
  created.created_at = created.updated_at = new Date().toISOString();
  created.html_url = 'https://github.com/owner/repo/pull/2#issuecomment-21';
  f.comments.splice(0, f.comments.length, created);
  f.context.payload.comment = structuredClone(created);
  f.pr.auto_merge = { merge_method: 'squash' };
  let archiveFailure = true;
  const createComment = f.github.rest.issues.createComment;
  f.github.rest.issues.createComment = async args => {
    if (args.body.startsWith('<!-- squad-request-record:21:') && archiveFailure) {
      archiveFailure = false;
      throw new Error('archive API temporarily unavailable');
    }
    return createComment(args);
  };

  await assert.rejects(handleDecision(f), /archive API temporarily unavailable/);
  assert.equal(f.pr.auto_merge, null, 'the previous auto-merge authorization is disabled before surfacing failure');
  assert.ok(f.prLabels.some(label => label.name === 'needs:human'));
  const recoveryAudit = f.comments.find(comment => comment.body?.startsWith('<!-- squad-request-archive-failure:2:21 -->'));
  assert.match(recoveryAudit?.body || '', /created, edited, or deleted scope request/);
  assert.equal(f.comments.some(comment => comment.body?.startsWith('<!-- squad-request-record:21:')), false,
    'archive failure must not be represented as successful preservation');
  assert.ok(f.effects.some(item => item.workflow_id === 'squad-pr-lifecycle.yml'),
    'the lifecycle is dispatched to invalidate prior readiness');

  await handleDecision(f);
  assert.ok(f.comments.some(comment => comment.body?.startsWith('<!-- squad-request-record:21:')),
    'rerunning after recovery creates the immutable archive record');
  const recovered = await loadState({
    github: f.github, owner, repo: 'repo',
    pr: { body: 'Relates to #1' }, comments: f.comments,
  });
  assert.equal(recovered.requests[0].status, 'pending');
  assert.equal(recovered.blocked, true);
  assert.ok(f.prLabels.some(label => label.name === 'needs:human'),
    'recovery does not clear the independent human pause');
});

test('deleted request event without body still reconciles its archived record', async () => {
  const f = fixture();
  const original = f.comments[0];
  const { preserveRequests } = require('./squad-decision-controller');
  await preserveRequests({ github: f.github, owner, repo: 'repo', number: 2, comments: f.comments });
  f.comments.shift();
  f.context.payload.action = 'deleted';
  f.context.payload.comment = { id: original.id, user: original.user };
  await handleDecision(f);
  const stateAfterDelete = await loadState({
    github: f.github, owner, repo: 'repo',
    pr: { body: 'Relates to #1' }, comments: f.comments,
  });
  assert.equal(stateAfterDelete.requests[0].status, 'invalid');
  assert.equal(stateAfterDelete.blocked, true);
});

test('deleting the audit comment cannot erase a request whose source is already missing', async () => {
  const f = fixture();
  const original = f.comments.shift();
  await preserveRequests({ github: f.github, owner, repo: 'repo', number: 2, comments: f.comments, event: original, action: 'created' });
  const audit = f.comments.find(comment => comment.body?.startsWith('<!-- squad-request-record:20:'));
  assert.ok(audit);
  f.comments.splice(f.comments.indexOf(audit), 1);
  f.context.payload.action = 'deleted';
  f.context.payload.comment = audit;

  await handleDecision(f);
  assert.ok(f.comments.some(comment => comment.body?.startsWith('<!-- squad-request-record:20:')),
    'the delete event snapshot restores the request record');
  const stateAfterDelete = await loadState({
    github: f.github, owner, repo: 'repo',
    pr: { body: 'Relates to #1' }, comments: f.comments,
  });
  assert.equal(stateAfterDelete.requests[0].status, 'invalid');
  assert.equal(stateAfterDelete.blocked, true);
});

test('issue baseline 404 or transient read failure disables existing auto-merge and stays blocked', async () => {
  for (const failure of [
    Object.assign(new Error('not found'), { status: 404 }),
    new Error('temporary network failure'),
  ]) {
    const f = fixture();
    const approval = human('/approve-change 20', 21);
    approval.created_at = approval.updated_at = new Date().toISOString();
    f.comments.push(approval);
    f.context.payload.comment = approval;
    const get = f.github.rest.pulls.get;
    f.github.rest.pulls.get = async args => {
      const result = await get(args);
      result.data.auto_merge = {};
      result.data.node_id = 'PR_2';
      return result;
    };
    f.github.rest.issues.get = async () => { throw failure; };
    f.agent.graphql = async query => f.effects.push({ query });

    await handleDecision(f);
    const stateAfterFailure = await loadState({
      github: f.github, owner, repo: 'repo',
      pr: { body: 'Relates to #1' }, comments: f.comments,
    });
    assert.equal(stateAfterFailure.requests[0].status, 'unreadable');
    assert.equal(stateAfterFailure.blocked, true);
    assert.ok(f.effects.some(item => item.query?.includes('disablePullRequestAutoMerge')));
    assert.ok(f.effects.some(item => item.workflow_id === 'squad-pr-lifecycle.yml'));
    assert.equal(f.effects.filter(item => item.body?.startsWith('<!-- squad-decision:')).length, 0);
  }
});

test('notification failure is explicit and sanitized; delivery deduplicates without creating authority', async () => {
  const f = fixture();
  const input = { ...f, owner, repo: 'repo', number: 2, key: 'test',
    payload: { card: 'synthetic' }, env: { SQUAD_TEAMS_NOTIFICATIONS: 'true', SQUAD_TEAMS_WEBHOOK_URL: 'https://example.test/secret' } };
  await assert.rejects(notify({ ...input, fetchImpl: async () => { throw new Error('https://example.test/secret'); } }), /uncertain/);
  assert.ok(f.effects.some(item => item.body.includes('could not be confirmed')));
  assert.ok(f.effects.every(item => !item.body.includes('example.test/secret')));
  let calls = 0;
  const fetchImpl = async () => { calls++; return { ok: true }; };
  await notify({ ...input, fetchImpl });
  await notify({ ...input, fetchImpl });
  assert.equal(calls, 1);
  assert.equal(f.effects.filter(item => item.body.startsWith('<!-- squad-decision:')).length, 0);
});

test('amendment resume batches a single continuation, preserves pauses and waits for a new successful session', async () => {
  const f = fixture();
  const accepted = receipt(f.comments[0]);
  const args = { ...f, owner, repo: 'repo',
    pr: { number: 2, labels: [], head: { ref: 'copilot/work' } },
    comments: [...f.comments, accepted], state: state([f.comments[0], accepted]),
    session: { state: 'ok', run: { created_at: '2026-01-01' } } };
  // Use a current evaluation time for the production-shaped fixture.
  args.state = decisionState(args.comments, owner, baselines);
  assert.equal(await resumeAmendments(args), true);
  assert.equal(f.effects.filter(item => item.body.startsWith('@copilot')).length, 1);
  await resumeAmendments({ ...args, comments: [...args.comments, ...f.comments] });
  assert.equal(f.effects.filter(item => item.body.startsWith('@copilot')).length, 1);
  const count = f.effects.length;
  await resumeAmendments({ ...args, pr: { ...args.pr, labels: [{ name: 'needs:human' }] } });
  await resumeAmendments({ ...args, session: { state: 'active' } });
  await resumeAmendments({ ...args, session: { state: 'ok', run: { created_at: '2030-01-01' } } });
  assert.equal(f.effects.length, count);
});

test('notification retries are bounded and disabled delivery needs no API or network', async () => {
  await notify({ env: {} });
  await notifyBlocker({ env: {} });
  const f = fixture();
  const input = { ...f, owner, repo: 'repo', number: 2, key: 'retry', payload: {},
    env: { SQUAD_TEAMS_NOTIFICATIONS: 'true', SQUAD_TEAMS_WEBHOOK_URL: 'https://example.test/secret' },
    fetchImpl: async () => ({ ok: false }) };
  for (let i = 0; i < 3; i++) await assert.rejects(notify(input), /failed or was uncertain/);
  const before = f.effects.length;
  await assert.rejects(notify(input), /exhausted three/);
  assert.equal(f.effects.length, before);
  await assert.rejects(notify({ ...input, env: { ...input.env, SQUAD_TEAMS_WEBHOOK_URL: 'invalid secret' } }), /missing or invalid/);
});

test('link-only pending cards and owner blockers deduplicate without granting decision authority', async () => {
  const f = fixture();
  f.env = { SQUAD_TEAMS_NOTIFICATIONS: 'true', SQUAD_TEAMS_WEBHOOK_URL: 'https://example.test/secret' };
  f.context.payload.comment = f.comments[0];
  const sent = [];
  f.fetchImpl = async (_url, options) => { sent.push(JSON.parse(options.body)); return { ok: true }; };
  await handleDecision(f);
  assert.deepEqual(sent[0].card.actions.map(action => action.type), ['Action.OpenUrl']);
  const get = f.github.rest.pulls.get;
  f.github.rest.pulls.get = async () => {
    const result = await get();
    result.data.labels = [{ name: 'needs:human' }];
    return result;
  };
  f.comments.push(bot('@owner Needs human: connection unavailable', 100));
  const input = { ...f, owner, repo: 'repo', number: 2 };
  await notifyBlocker(input);
  await notifyBlocker(input);
  assert.equal(sent.length, 2);
  assert.equal(sent[1].card.actions[0].title, 'Inspect blocker in GitHub');
  assert.equal(f.effects.filter(item => item.body?.startsWith('<!-- squad-decision:')).length, 0);
});
