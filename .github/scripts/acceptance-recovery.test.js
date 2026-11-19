'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const { recoveryInput } = require('./acceptance-recovery');
const { acceptanceTracking } = require('./completion-declaration');
const { publishTracking } = require('./publish-acceptance-tracking');

test('recovery writer appends only unchecked original criteria and rejects stale or invalid results', async t => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'squad-acceptance-'));
  const agentOutput = path.join(dir, 'output.json');
  t.after(() => {
    fs.rmSync(agentOutput, { force: true });
    fs.rmdirSync(dir);
  });
  const owner = 'cedricmendelin';
  const repo = 'bujo-ai';
  const pr = { number: 12, state: 'open', draft: false, created_at: '2026-11-18T21:00:00Z', labels: [],
    head: { sha: 'a'.repeat(40), repo: { full_name: `${owner}/${repo}` } },
    body: 'Completion: partial\nRefs #11\n## Remaining work\nRun SQL tests.' };
  const original = 'Original context.\n\n## Acceptance criteria\n- Run real SQL tests.\n- Verify the restore.';
  const issue = { number: 11, state: 'open', body: original, labels: [{ name: 'plan:approved' }] };
  const plan = { id: 123, user: { login: owner }, body: '## 📋 Implementation plan v1 · owner: builder' };
  const comments = [plan];
  const { snapshot } = recoveryInput(pr, issue, comments, owner, repo);
  comments.push({ id: 124, user: { login: owner },
    body: `<!-- squad-acceptance-recovery:123 -->\nPR #12: Request\n<!-- squad-acceptance-snapshot:${snapshot} -->` });
  const writes = [];
  const github = {
    paginate: async () => comments,
    rest: {
      pulls: { get: async () => ({ data: structuredClone(pr) }) },
      issues: {
        get: async () => ({ data: structuredClone(issue) }),
        update: async args => { writes.push(args); issue.body = args.body; },
      },
    },
  };
  const context = { repo: { owner, repo }, eventName: 'workflow_dispatch', ref: 'refs/heads/main',
    payload: { repository: { default_branch: 'main' }, inputs: {
      pr_number: '12', issue_number: '11', plan_id: '123', delivery_owner: 'builder',
    } } };
  const writeOutput = actions => fs.writeFileSync(agentOutput, JSON.stringify({
    items: [{ type: 'append_acceptance_tracking', actions_json: JSON.stringify(actions) }],
  }));
  const publish = () => publishTracking({ github, context, core: { info() {} }, agentOutput });
  writeOutput(['Run tagged tests.\n- [x] Fabricated', 'Compare restored IDs.']);
  await assert.rejects(publish, /one short, plain-text next action/);
  writeOutput(['Run tagged tests.', 'Compare restored IDs.']);
  issue.body = original.replace('Verify the restore.', 'Verify the restore twice.');
  await assert.rejects(publish, /no longer qualifies/);
  assert.equal(writes.length, 0);
  issue.body = original;
  await publish();
  assert.equal(writes.length, 1);
  assert.equal(writes[0].issue_number, 11);
  assert.equal(issue.body, `${original}\n\n## Acceptance tracking\n\n`
    + '- [ ] open; owner: builder; next: Run tagged tests. — Criterion: Run real SQL tests.\n'
    + '- [ ] open; owner: builder; next: Compare restored IDs. — Criterion: Verify the restore.\n');
  assert.equal(acceptanceTracking(issue.body).incomplete.length, 2);
  await assert.rejects(publish, /no longer qualifies/);
  assert.equal(writes.length, 1);
});
