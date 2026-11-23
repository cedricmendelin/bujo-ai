'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const lifecycle = require('./delivery-lifecycle');

const sha = n => String(n).repeat(40);
const check = (name, head, conclusion = 'success') => ({
  id: Number(head[0]), name, head_sha: head, status: 'completed', conclusion,
  started_at: '2026-11-16T12:01:00Z', completed_at: '2026-11-16T12:02:00Z',
});
const run = (role, pr, created, status = 'completed', conclusion = 'success') => ({
  name: `Squad Review ${role} · PR #${pr}`, display_title: `Squad Review ${role} · PR #${pr}`,
  created_at: created, updated_at: '2026-11-16T12:03:00Z', status, conclusion,
});
const fix = (head, created_at, id) => ({
  id, created_at, body: `<!-- squad-review-fix:${head} -->\n<!-- squad-repair-request:00000000-0000-0000-0000-${String(id).padStart(12, '0')} -->\nPlease fix`,
  user: { login: 'cedricmendelin' },
});
const outcome = (request, session, result = 'attempted', login = 'copilot-swe-agent[bot]') => ({
  id: 100 + request.id, created_at: session.created_at || session.run_started_at,
  user: { login, type: 'Bot' },
  body: `<!-- squad-repair-outcome:${request.body.match(/squad-repair-request:([a-f0-9-]+)/)[1]}:${session.id}:${result} -->\nInvestigated the acceptance case.`,
});

test('Verifier is the default reviewer; review:none is a PR-only opt-out', () => {
  assert.deepEqual(lifecycle.reviewSelection([]), ['Verifier']);
  assert.deepEqual(lifecycle.reviewSelection(['review:none']), []);
  assert.deepEqual(lifecycle.reviewFailures([
    check('Squad Review / Architect', sha(1), 'failure'),
  ], sha(1), ['review:none']), []);
});

test('removing review:none starts a fresh generation even when old checks share the head', () => {
  const removedAt = '2026-11-16T12:00:00Z';
  const current = check('Squad Review / Security', sha(1));
  assert.equal(lifecycle.reviewResultIsFresh({
    check: current, run: run('Security', 8, '2026-11-16T11:59:59Z'), sha: sha(1), removedAt,
  }), false);
  assert.equal(lifecycle.reviewResultIsFresh({
    check: current, run: run('Security', 8, '2026-11-16T12:00:00Z'), sha: sha(1), removedAt,
  }), true);
  assert.equal(lifecycle.reviewResultIsFresh({
    check: check('Squad Review / Security', sha(2)),
    run: run('Security', 8, '2026-11-16T12:01:00Z'), sha: sha(1), removedAt,
  }), false);
});

test('review freshness requires complete timestamps within the selected successful run window', () => {
  const selected = run('Architect', 8, '2026-11-16T12:00:00Z');
  const result = check('Squad Review / Architect', sha(1));
  assert.equal(lifecycle.reviewResultIsFresh({ check: result, run: selected, sha: sha(1) }), true);
  for (const invalid of [
    { ...result, started_at: undefined },
    { ...result, completed_at: undefined },
    { ...result, completed_at: '2026-11-16T12:04:00Z' },
    { ...result, started_at: '2026-11-16T11:59:59Z' },
    { ...result, completed_at: '2026-11-16T11:59:59Z' },
    { ...result, started_at: '2026-11-16T12:04:00Z' },
  ]) {
    assert.equal(lifecycle.reviewResultIsFresh({ check: invalid, run: selected, sha: sha(1) }), false);
  }
  assert.equal(lifecycle.reviewResultIsFresh({ check: result, run: null, sha: sha(1) }), false);
});

test('safe-output completion may precede the GitHub-assigned start within the same run', () => {
  const selected = run('Verifier', 94, '2026-11-17T08:48:40Z');
  selected.updated_at = '2026-11-17T08:54:07Z';
  const result = {
    ...check('Squad Review / Verifier', sha(1)),
    started_at: '2026-11-17T08:53:35Z',
    completed_at: '2026-11-17T08:53:34Z',
  };
  assert.equal(lifecycle.reviewResultIsFresh({ check: result, run: selected, sha: sha(1) }), true);
  for (const invalid of [
    { ...result, started_at: '2026-11-17T08:48:39Z' },
    { ...result, completed_at: '2026-11-17T08:48:39Z' },
    { ...result, started_at: '2026-11-17T08:54:08Z' },
    { ...result, completed_at: '2026-11-17T08:54:08Z' },
  ]) {
    assert.equal(lifecycle.reviewResultIsFresh({ check: invalid, run: selected, sha: sha(1) }), false);
  }
});

test('new code heads and review:none removal dispatch a fresh review once without duplicate delivery', () => {
  const activeOldRun = [run('Architect', 8, '2026-11-16T11:00:00Z', 'in_progress')];
  assert.equal(lifecycle.reviewDispatchNeeded({
    fresh: false, runs: activeOldRun, forceFresh: true, eventAt: '2026-11-16T12:00:00Z',
  }), true);
  const dispatchedAfterEvent = [run('Architect', 8, '2026-11-16T12:00:01Z', 'in_progress')];
  assert.equal(lifecycle.reviewDispatchNeeded({
    fresh: false, runs: dispatchedAfterEvent, forceFresh: true, eventAt: '2026-11-16T12:00:00Z',
  }), false);
  assert.equal(lifecycle.reviewDispatchNeeded({
    fresh: false, runs: dispatchedAfterEvent, forceFresh: false,
  }), false);
});

test('review run selection matches GitHub run titles exactly for the requested role and PR', () => {
  const target = run('Architect', 8, '2026-11-16T12:00:00Z', 'in_progress');
  const otherPr = run('Architect', 80, '2026-11-16T12:01:00Z', 'in_progress');
  const otherRole = run('Security', 8, '2026-11-16T12:02:00Z', 'in_progress');
  assert.deepEqual(lifecycle.reviewRunsForPr([otherPr, otherRole, target], 'Architect', 8), [target]);
});

test('review:none removal time comes from the latest unlabeled event', () => {
  assert.equal(lifecycle.reviewNoneRemovedAt([
    { event: 'unlabeled', label: { name: 'review:none' }, created_at: '2026-11-16T10:00:00Z', id: 1 },
    { event: 'labeled', label: { name: 'review:none' }, created_at: '2026-11-16T10:30:00Z', id: 2 },
    { event: 'unlabeled', label: { name: 'review:none' }, created_at: '2026-11-16T11:00:00Z', id: 3 },
  ]), '2026-11-16T11:00:00Z');
});

test('cosmetic description edits do not change current-head review selection', () => {
  const shaNow = sha(4);
  const checks = lifecycle.ROLES.map(role => check(`Squad Review / ${role}`, shaNow));
  assert.equal(lifecycle.ROLES.every(role =>
    lifecycle.currentHeadCheck(checks, `Squad Review / ${role}`, shaNow)?.conclusion === 'success'), true);
  assert.equal(lifecycle.currentHeadCheck(checks, 'Squad Review / Architect', sha(5)), null);
});

test('human change requests and unresolved blocking threads remain blockers under review:none', () => {
  const inputs = {
    owner: 'cedricmendelin',
    reviews: [
      { id: 1, state: 'CHANGES_REQUESTED', user: { login: 'cedricmendelin', type: 'User' }, author_association: 'OWNER' },
    ],
    threads: [],
  };
  assert.equal(lifecycle.hasHumanBlocks(inputs), true);
  assert.equal(lifecycle.hasHumanBlocks({
    owner: 'cedricmendelin', reviews: [],
    threads: [{
      isResolved: false, isOutdated: false, body: '[blocking] Fix this',
      author: { login: 'maintainer', type: 'User', association: 'COLLABORATOR' },
    }],
  }), true);
  assert.equal(lifecycle.hasHumanBlocks({
    owner: 'cedricmendelin', reviews: [],
    threads: [{
      isResolved: true, isOutdated: false, body: '[blocking] Fixed',
      author: { login: 'maintainer', type: 'User', association: 'COLLABORATOR' },
    }],
  }), false);
});

test('CI, needs:human, humans, and cloud-session state independently block readiness', () => {
  const base = {
    draft: false, needsHuman: false, sessionOk: true,
    ci: true, reviewsOk: true, humanBlocks: false,
  };
  assert.equal(lifecycle.readiness(base).conclusion, 'success');
  assert.notEqual(lifecycle.readiness({ ...base, ci: false }).conclusion, 'success');
  assert.notEqual(lifecycle.readiness({ ...base, humanBlocks: true }).conclusion, 'success');
  assert.notEqual(lifecycle.readiness({ ...base, needsHuman: true }).conclusion, 'success');
  assert.equal(lifecycle.readiness({ ...base, sessionOk: false }).status, 'in_progress');
});

test('only ci and aggregate readiness are the final required branch contexts', () => {
  assert.equal(lifecycle.hasRequiredMergeRules([
    { type: 'required_status_checks', parameters: { required_status_checks: [
      { context: 'ci' }, { context: 'Squad Merge Readiness' },
    ] } },
  ]), true);
  assert.equal(lifecycle.hasRequiredMergeRules([
    { type: 'required_status_checks', parameters: { required_status_checks: [{ context: 'ci' }] } },
  ]), false);
});

test('repair attempts count completed coding sessions or actual local head changes, not queue noise', () => {
  const comments = [fix(sha(1), '2026-11-16T10:00:00Z', 1), fix(sha(2), '2026-11-16T11:00:00Z', 2)];
  const sessions = [
    { status: 'completed', conclusion: 'cancelled', created_at: '2026-11-16T10:10:00Z' },
    { status: 'in_progress', conclusion: null, created_at: '2026-11-16T10:20:00Z' },
    { id: 10, status: 'completed', conclusion: 'success', run_started_at: '2026-11-16T10:30:00Z' },
  ];
  comments.push(outcome(comments[0], sessions[2]));
  assert.equal(lifecycle.actualRepairAttempts(comments, sessions, sha(3), true, 'cedricmendelin'), 1);
  assert.equal(lifecycle.actualRepairAttempts(comments, [], sha(3), false, 'cedricmendelin'), 0, 'local takeover must not count cloud requests as local code changes');
  const local = comments.map(comment => ({ ...comment, body: comment.body.replace(/squad-review-fix:([a-f0-9]{40}) -->/, 'squad-review-fix:$1:local -->') }));
  assert.equal(lifecycle.actualRepairAttempts(local, [], sha(2), false, 'cedricmendelin'), 1);
  assert.equal(lifecycle.actualRepairAttempts(local, [], sha(3), false, 'cedricmendelin'), 2);
});

test('cloud repair pauses while active, and escalates after three actual attempts', () => {
  const comments = [fix(sha(1), '2026-11-16T10:00:00Z', 1)];
  assert.equal(lifecycle.outstandingFixRequest(comments, [
    { status: 'in_progress', created_at: '2026-11-16T10:01:00Z' },
  ], true, 'cedricmendelin'), true);
  assert.equal(lifecycle.outstandingFixRequest(comments, [
    { status: 'completed', conclusion: 'cancelled', created_at: '2026-11-16T10:01:00Z' },
  ], true, 'cedricmendelin'), true);
  const completed = [10, 11, 12].map(hour => ({
    id: hour, status: 'completed', conclusion: 'success',
    run_started_at: `2026-11-16T${hour}:00:00Z`,
  }));
  const requests = [10, 11, 12].map((hour, i) => fix(sha(i + 1), `2026-11-16T${String(hour - 1).padStart(2, '0')}:00:00Z`, i + 1));
  requests.push(...requests.map((request, i) => outcome(request, completed[i])));
  assert.equal(lifecycle.actualRepairAttempts(requests, completed, sha(4), true, 'cedricmendelin'), 3);
  assert.equal(lifecycle.MAX_REPAIR_ATTEMPTS, 3);
});

test('repair accounting accepts only owner/workflow comments and counts a real session ID once', () => {
  const owner = 'cedricmendelin';
  const owned = fix(sha(1), '2026-11-16T10:00:00Z', 1);
  const bot = { ...fix(sha(1), '2026-11-16T11:00:00Z', 2), user: { login: 'github-actions[bot]' } };
  const fake = { ...fix(sha(1), '2026-11-16T09:00:00Z', 3), user: { login: 'outsider' } };
  const session = { id: 100, status: 'completed', conclusion: 'success', created_at: '2026-11-16T12:00:00Z' };
  const report = outcome(owned, session);
  assert.equal(lifecycle.actualRepairAttempts([owned, bot, fake, report], [session, session], sha(1), true, owner), 1);
  assert.equal(lifecycle.actualRepairAttempts([fake], [session], sha(1), true, owner), 0);
  assert.equal(lifecycle.outstandingFixRequest([fake], [], true, owner), false);
  assert.equal(lifecycle.outstandingFixRequest([bot], [], true, owner), true);
  assert.equal(lifecycle.actualRepairAttempts([owned], [session], sha(1), true, owner), 0, 'unrelated later run is not evidence');
  assert.equal(lifecycle.actualRepairAttempts([owned, outcome(owned, session, 'capability-blocked')], [session], sha(1), true, owner), 0);
  assert.equal(lifecycle.actualRepairAttempts([owned, outcome(owned, session, 'attempted', 'outsider')], [session], sha(1), true, owner), 0);
  assert.equal(lifecycle.actualRepairAttempts([owned, { ...report, user: { login: 'copilot-swe-agent[bot]', type: 'User' } }], [session], sha(1), true, owner), 0);
  assert.equal(lifecycle.actualRepairAttempts([owned, report], [{ ...session, created_at: 'invalid' }], sha(1), true, owner), 0);
  assert.equal(lifecycle.actualRepairAttempts([owned, report], [{ ...session, id: 101 }], sha(1), true, owner), 0);
  assert.equal(lifecycle.actualRepairAttempts([owned, report, outcome(owned, session, 'external-blocked')], [session], sha(1), true, owner), 0, 'a blocked report must not also count as an attempt');
});
