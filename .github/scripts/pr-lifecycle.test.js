'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const {
  MAX_BODY_BYTES, bodyHash, fixRequests, latestFix, currentReview, reviewMarker,
  latestSession, stalledSession, metadataProposal, validateProposal, reviewsFinishedAfter,
} = require('./pr-lifecycle');

const owner = 'owner';
const sha = 'a'.repeat(40);
const body = 'Completion: partial\nImplements plan v1 for issue #39.\n## Remaining work\nLive validation depends on deploying the workflows.\nRelates to #39';
const pr = { state: 'open', head: { sha }, body, created_at: '2026-11-15T14:00:00Z' };
const scope = { issueNumber: 39, planVersion: 1, repoFullName: 'owner/repo' };
const replacement = body.replace('Live validation', 'An isolated live exercise');
const comment = (text, overrides = {}) => ({
  id: 1, body: text, created_at: '2026-11-15T15:00:00Z', user: { login: owner, type: 'User' }, ...overrides,
});
const fix = comment(`<!-- squad-gate:fix:${sha}:${bodyHash(body).slice(0, 12)} -->`);
const session = {
  id: 10, status: 'completed', conclusion: 'success', created_at: '2026-11-15T15:01:00Z',
  updated_at: '2026-11-15T15:05:00Z',
};
const proposal = (changes = {}) => comment(`<!-- squad-pr-metadata -->\n\`\`\`json\n${JSON.stringify({
  headSha: sha, baseBodySha256: bodyHash(body), body: replacement, ...changes,
})}\n\`\`\``);

test('recognizes legacy and body-hashed fix requests, never quoted or spoofed markers', () => {
  const legacy = comment(`<!-- squad-gate:fix:${sha} -->`);
  const spoof = comment(fix.body, { user: { login: 'stranger', type: 'User' } });
  const quoted = comment(`> ${fix.body}`);
  assert.equal(fixRequests([legacy, fix, spoof, quoted], owner).length, 2);
  assert.equal(latestFix([fix, comment(fix.body, { id: 2 })], owner, sha).id, 2);
  assert.equal(latestFix([fix], owner, 'b'.repeat(40)), null);
  assert.equal(fixRequests([comment(fix.body, { user: { login: 'Copilot', type: 'Bot' } })], owner).length, 0);
});

test('selects the latest session and escalates a successful no-change session on any trigger', () => {
  const parsed = latestFix([fix], owner, sha);
  const old = { ...session, id: 9, created_at: '2026-11-15T14:59:00Z' };
  assert.equal(latestSession([session, old]).id, 10);
  assert.equal(stalledSession({ session, fix: parsed, body }), true);
  assert.equal(stalledSession({ session: old, fix: parsed, body }), false);
  for (const state of [{ status: 'in_progress' }, { conclusion: 'cancelled' }, { conclusion: 'failure' }]) {
    assert.equal(stalledSession({ session: { ...session, ...state }, fix: parsed, body }), false);
  }
  assert.equal(stalledSession({ session, fix: null, body }), false);
  assert.equal(stalledSession({ session: { ...session, updated_at: 'bad' }, fix: parsed, body }), false);
});

test('description changes and fresh re-reviews are progress, old revisions are not', () => {
  const parsed = latestFix([fix], owner, sha);
  assert.equal(stalledSession({ session, fix: parsed, body: replacement }), false);
  const reviewed = comment(reviewMarker(sha, body), { created_at: '2026-11-15T15:06:00Z' });
  assert.equal(stalledSession({ session, fix: parsed, body, review: reviewed }), false);
  assert.equal(currentReview([reviewed], owner, sha, body).id, 1);
  assert.equal(currentReview([reviewed], owner, sha, replacement), null);
  assert.equal(currentReview([comment(reviewed.body, { user: { login: 'stranger' } })], owner, sha, body), null);
});

test('accepts explicit authorized metadata handoff without accepting quoted bot markers', () => {
  const valid = proposal();
  const bot = proposal();
  bot.user = { login: 'Copilot', type: 'Bot' };
  assert.equal(metadataProposal([bot], owner).id, 1);
  assert.equal(metadataProposal([comment(valid.body, { user: { login: 'Copilot', type: 'User' } })], owner), null);
  assert.equal(metadataProposal([comment(`> ${valid.body}`)], owner), null);
  assert.deepEqual(validateProposal(valid, pr, scope), { ok: true, body: replacement });
});

test('rejects stale snapshots, closed PRs and hidden extra operations', () => {
  for (const candidate of [
    proposal({ headSha: 'b'.repeat(40) }), proposal({ baseBodySha256: 'b'.repeat(64) }),
    proposal({ labels: ['plan:waived'] }), proposal({ body: null }),
    proposal({ headSha: 10 }), proposal({ body }),
  ]) assert.equal(validateProposal(candidate, pr, scope).ok, false);
  assert.equal(validateProposal(proposal(), { ...pr, state: 'closed' }, scope).ok, false);
  assert.equal(validateProposal(proposal(), { ...pr, body: replacement }, scope).ok, false);
});

test('rejects malformed, ambiguous and oversized payloads with actionable errors', () => {
  for (const text of [
    '<!-- squad-pr-metadata -->\n```json\nbad\n```',
    `${proposal().body}\nTrailing instructions`,
    `${proposal().body}\n${proposal().body}`,
    '<!-- squad-pr-metadata -->\n```json\nnull\n```',
  ]) {
    const result = validateProposal(comment(text), pr, scope);
    assert.equal(result.ok, false);
    assert.ok(result.reason);
  }
  assert.equal(validateProposal(proposal({ body: 'x'.repeat(MAX_BODY_BYTES + 1) }), pr, scope).ok, false);
  assert.equal(validateProposal(proposal({ body: 'é'.repeat(MAX_BODY_BYTES) }), pr, scope).ok, false);
});

test('enforces explicit completion and preserves current approved issue/plan scope', () => {
  for (const candidate of [
    replacement.replace('Completion: partial\n', ''),
    `\`\`\`\nCompletion: partial\n\`\`\`\n${replacement.replace('Completion: partial\n', '')}`,
    `${replacement}\nFixes #39`,
    replacement.replace(/#39/g, '#40'),
    replacement.replace('plan v1', 'plan v2'),
    replacement.replace('## Remaining work', '## Notes'),
  ]) assert.equal(validateProposal(proposal({ body: candidate }), pr, scope).ok, false);
  assert.equal(validateProposal(proposal(), pr, null).ok, false);
  assert.equal(validateProposal(proposal(), pr, { waived: true, owner }).ok, true);
  assert.equal(validateProposal(comment(proposal().body, { user: { login: 'Copilot', type: 'Bot' } }), pr, { waived: true, owner }).ok, false);
  assert.equal(validateProposal(proposal({ body: replacement.replace(/#39/g, 'owner/repo#39') }), pr, scope).ok, true);
  assert.equal(validateProposal(proposal({ body: replacement.replace(/#39/g, 'unrelated/repo#39') }), pr, scope).ok, false);
});

test('old successful checks cannot authorize a new description revision', () => {
  const review = comment(reviewMarker(sha, replacement));
  const checks = {
    Architect: { status: 'completed', completed_at: '2026-11-15T15:00:01Z' },
    Verifier: { status: 'completed', completed_at: '2026-11-15T14:59:59Z' },
  };
  assert.equal(reviewsFinishedAfter(checks, ['Architect', 'Verifier'], review), false);
  checks.Verifier.completed_at = '2026-11-15T15:02:00Z';
  assert.equal(reviewsFinishedAfter(checks, ['Architect', 'Verifier'], review), true);
  assert.equal(reviewsFinishedAfter(checks, ['Architect', 'Security'], review), false);
  assert.equal(reviewsFinishedAfter(checks, ['Architect'], null), false);
});

function workflowScript(file) {
  const source = readFileSync(join(__dirname, '..', 'workflows', file), 'utf8');
  const script = source.match(/          script: \|\r?\n([\s\S]*?)(?=\r?\n {6}- name:|$)/)[1];
  return script.split(/\r?\n/).map(line => line.slice(12)).join('\n');
}

function harness({ comments = [], sessions = [session], activeReview = false, checks = [], beforeRead } = {}) {
  const state = {
    pr: { ...structuredClone(pr), number: 38, draft: false, mergeable: true,
      node_id: 'PR_38', labels: [], head: { sha, ref: 'copilot/test', repo: { full_name: 'owner/repo' } },
      base: { ref: 'main' } },
    comments: structuredClone(comments), writes: [], logs: [], sessions,
  };
  const now = new Date().toISOString();
  const issues = {
    listComments: async ({ issue_number }) => issue_number === 39
      ? [comment('## 📋 Implementation plan v1 · owner: `devops`')] : state.comments,
    get: async () => ({ data: { labels: [{ name: 'plan:approved' }] } }),
    createComment: async ({ body: text }) => {
      state.writes.push({ operation: 'comment', body: text });
      state.comments.push(comment(text, { id: state.comments.length + 100, created_at: now }));
    },
    addLabels: async ({ labels }) => {
      state.writes.push({ operation: 'labels', labels });
      state.pr.labels.push(...labels.map(name => ({ name })));
    },
    addAssignees: async () => {},
    removeLabel: async () => {},
  };
  const actions = {
    listRepoWorkflows: async () => [{ id: 1, path: 'dynamic/copilot-swe-agent.yml' }],
    listWorkflowRuns: async ({ workflow_id }) => ({ data: { workflow_runs:
      workflow_id === 1 ? state.sessions
        : workflow_id === 'ci.yml' ? [{ conclusion: 'success' }]
          : activeReview ? [{ status: 'in_progress' }] : [],
    } }),
    createWorkflowDispatch: async args => state.writes.push({ operation: 'dispatch', ...args }),
  };
  let reads = 0;
  const pulls = {
    get: async () => {
      beforeRead?.(state, ++reads);
      return { data: structuredClone(state.pr) };
    },
    list: async () => ({ data: [state.pr] }),
    update: async ({ body: text }) => {
      state.writes.push({ operation: 'update', body: text });
      state.pr.body = text;
    },
    merge: async () => state.writes.push({ operation: 'merge' }),
  };
  const github = {
    rest: {
      issues, actions, pulls, checks: { listForRef: async () => checks },
      repos: { get: async () => ({ data: { default_branch: 'main' } }) },
    },
    paginate: async (method, args) => {
      const result = await method(args);
      return result.data || result;
    },
  };
  const agent = { rest: github.rest, graphql: async query => state.writes.push({ operation: 'graphql', query }) };
  const core = {
    info: text => state.logs.push(text),
    error: text => state.logs.push(text),
    setFailed: text => { throw new Error([text, ...state.logs].join('\n')); },
  };
  const processMock = { env: {
    HUMAN: owner, REVIEWS: 'Architect:architect.yml,Security:security.yml,Verifier:verifier.yml,DevOps:devops.yml',
    REVIEW_TIMEOUT_MINUTES: '60', MAX_ROUNDS: '3',
    REQUIRED_CHECKS: 'ci,Squad Review / Architect,Squad Review / Security,Squad Review / Verifier,Squad Review / DevOps',
  } };
  state.run = async (file, eventName = 'schedule', script = workflowScript(file)) => {
    const context = {
      repo: { owner, repo: 'repo' }, eventName,
      payload: { inputs: eventName === 'workflow_dispatch' ? { pr_number: '38' } : undefined, workflow_run: {
        head_sha: sha, head_branch: 'copilot/test', pull_requests: [{ number: 38 }], name: 'CI', event: 'workflow_dispatch',
      }, pull_request: { number: 38 } },
    };
    const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
    await new AsyncFunction('require', 'github', 'getOctokit', 'core', 'context', 'process', script)(
      path => require(path.replace('./.github/scripts/', './')), github, () => agent, core, context, processMock,
    );
  };
  return state;
}

test('real shepherd script detects no-progress on event, schedule and dispatch, once', async () => {
  for (const event of ['workflow_run', 'schedule', 'workflow_dispatch']) {
    const state = harness({ comments: [fix] });
    await state.run('squad-pr-shepherd.yml', event);
    assert.deepEqual(state.writes.find(w => w.operation === 'labels').labels, ['needs:human']);
    assert.match(state.writes.find(w => w.operation === 'comment').body, /stalled:10:1/);
    assert.equal(state.writes.some(w => w.operation === 'dispatch'), false);
    const count = state.writes.length;
    await state.run('squad-pr-shepherd.yml', event);
    assert.equal(state.writes.length, count);
  }
});

test('real shepherd applies an explicit proposal before stall handling and dispatches once', async () => {
  const state = harness({ comments: [fix, proposal()] });
  await state.run('squad-pr-shepherd.yml');
  assert.equal(state.pr.body, replacement);
  assert.equal(state.writes.filter(w => w.operation === 'update').length, 1);
  assert.equal(state.writes.filter(w => w.operation === 'dispatch').length, 4);
  assert.equal(state.writes.some(w => w.operation === 'labels'), false);
  assert.ok(state.writes.some(w => w.body?.includes(reviewMarker(sha, replacement))));
  const count = state.writes.length;
  await state.run('squad-pr-shepherd.yml');
  assert.equal(state.writes.length, count);
});

test('real shepherd rejects stale proposals and never writes during an active/interrupted session', async () => {
  const rejected = harness({ comments: [proposal({ baseBodySha256: 'f'.repeat(64) })] });
  await rejected.run('squad-pr-shepherd.yml');
  assert.equal(rejected.writes.some(w => w.operation === 'update'), false);
  assert.match(rejected.writes.find(w => w.operation === 'comment').body, /metadata-rejected:1/);
  for (const override of [{ status: 'in_progress' }, { conclusion: 'cancelled' }]) {
    const waiting = harness({ comments: [proposal()], sessions: [{ ...session, ...override }] });
    await waiting.run('squad-pr-shepherd.yml');
    assert.deepEqual(waiting.writes, []);
  }
});

test('real shepherd re-reads the PR and rejects a concurrent description edit before writing', async () => {
  const state = harness({
    comments: [proposal()],
    beforeRead: (state, count) => { if (count === 2) state.pr.body = 'Owner concurrent edit'; },
  });
  await state.run('squad-pr-shepherd.yml');
  assert.equal(state.pr.body, 'Owner concurrent edit');
  assert.equal(state.writes.some(w => w.operation === 'update'), false);
  assert.match(state.writes.find(w => w.operation === 'comment').body, /metadata-rejected/);
});

test('real shepherd re-reviews a description-only correction and drains older reviews', async () => {
  const state = harness({ comments: [fix] });
  state.pr.body = replacement;
  await state.run('squad-pr-shepherd.yml', 'pull_request_target');
  assert.equal(state.writes.filter(w => w.operation === 'dispatch').length, 4);
  const waiting = harness({ activeReview: true });
  await waiting.run('squad-pr-shepherd.yml');
  assert.equal(waiting.writes.some(w => w.operation === 'dispatch'), false);
  assert.equal(waiting.writes.some(w => w.body?.includes('squad-shepherd:review:')), false);
});

test('real gate refuses old green checks for a new body and accepts only fresh revision results', async () => {
  const names = ['ci', 'Squad Review / Architect', 'Squad Review / Security', 'Squad Review / Verifier', 'Squad Review / DevOps'];
  const checks = names.map((name, id) => ({ id, name, status: 'completed', conclusion: 'success', completed_at: '2026-11-15T14:59:00Z' }));
  const reviewed = comment(reviewMarker(sha, body));
  const state = harness({ comments: [reviewed], checks });
  state.pr.auto_merge = {};
  await state.run('squad-review-gate.yml');
  assert.equal(state.writes.some(w => w.query?.includes('enablePullRequestAutoMerge')), false);
  assert.ok(state.writes.some(w => w.query?.includes('disablePullRequestAutoMerge')));
  state.writes.length = 0;
  for (const check of checks) check.completed_at = '2026-11-15T15:01:00Z';
  await state.run('squad-review-gate.yml');
  assert.ok(state.writes.some(w => w.query?.includes('enablePullRequestAutoMerge')));
});

test('mutation of the real shepherd marker lookup recreates the regression', async () => {
  const source = workflowScript('squad-pr-shepherd.yml');
  const mutated = source.replace('const fix = latestFix(comments, human, sha);',
    'const fix = findMarker(`<!-- squad-gate:fix:${sha} -->`);');
  assert.notEqual(source, mutated);
  const state = harness({ comments: [fix] });
  await state.run('squad-pr-shepherd.yml', 'schedule', mutated);
  // This mutation must change the observable outcome protected by the real-script test.
  assert.equal(state.writes.some(w => w.operation === 'labels'), false);
  assert.equal(state.writes.filter(w => w.operation === 'dispatch').length, 4);
});
