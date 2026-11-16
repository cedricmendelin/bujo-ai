import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import {
  approvalRequiredRuns,
  classifySession,
  formatApprovalDiagnostic,
  gateEligibility,
  hasRunnableCiRun,
  MAX_RECOVERIES,
  recoveryAction,
} from './session-state.mjs';

const successfulRun = {
  id: 1,
  run_number: 1,
  created_at: '2026-11-15T10:00:00Z',
  status: 'completed',
  conclusion: 'success',
};

test('classifies no runs, active runs, and the latest run by created time', () => {
  assert.deepEqual(classifySession([]), { state: 'none', outcome: 'none', run: null });
  assert.equal(classifySession([{ ...successfulRun, title: '[WIP] example', conclusion: 'failure' }]).state, 'interrupted');
  assert.deepEqual(classifySession([{ ...successfulRun, status: 'in_progress' }]), {
    state: 'active',
    outcome: 'in_progress',
    run: { ...successfulRun, status: 'in_progress' },
  });
  assert.equal(classifySession([
    { ...successfulRun, conclusion: 'failure', created_at: '2026-11-15T11:00:00Z' },
    successfulRun,
  ]).outcome, 'failure');
});

test('uses run number and id to break ties and never lets an older success mask a newer failure', () => {
  const latestFailure = { ...successfulRun, id: 3, run_number: 3, conclusion: 'failure' };
  assert.deepEqual(classifySession([
    { ...successfulRun, conclusion: 'success' },
    latestFailure,
  ]), { state: 'interrupted', outcome: 'failure', run: latestFailure });
  assert.equal(classifySession([
    { ...successfulRun, id: 2 },
    { ...successfulRun, id: 3, conclusion: 'failure' },
  ]).outcome, 'failure');
  assert.equal(classifySession([
    { ...successfulRun, conclusion: 'failure', run_number: 2 },
    { ...successfulRun, run_number: 3 },
  ]).state, 'ok');
});

test('all non-success conclusions are interrupted, including approval-blocked runs', () => {
  for (const conclusion of ['failure', 'cancelled', 'timed_out', 'startup_failure', 'stale', 'action_required', 'skipped', 'neutral']) {
    const result = classifySession([{ ...successfulRun, conclusion }]);
    assert.equal(result.state, 'interrupted', conclusion);
    assert.equal(result.outcome, conclusion, conclusion);
  }
});

test('recovery is bounded and idempotent and interrupted recovery runs escalate', () => {
  assert.equal(MAX_RECOVERIES, 2);
  assert.equal(recoveryAction({ state: 'active' }), 'wait');
  assert.equal(recoveryAction({ state: 'none' }), 'wait');
  assert.equal(recoveryAction({ state: 'ok' }), 'continue');
  assert.equal(recoveryAction({ state: 'interrupted' }), 'recover');
  assert.equal(recoveryAction({ state: 'interrupted', hasMarker: true }), 'wait');
  assert.equal(recoveryAction({ state: 'interrupted', recoveryRun: true }), 'escalate');
  assert.equal(recoveryAction({ state: 'interrupted', recoveriesSoFar: MAX_RECOVERIES }), 'escalate');
  assert.equal(recoveryAction({ state: 'interrupted', canRecover: false }), 'escalate');
});

test('gate requires a successful session and current shepherd marker for Copilot PRs', () => {
  const base = { isAgentPr: true, draft: false, sessionState: 'ok', shepherdMarker: true };
  assert.equal(gateEligibility(base), 'eligible');
  assert.equal(gateEligibility({ ...base, draft: true }), 'wait');
  assert.equal(gateEligibility({ ...base, sessionState: 'active' }), 'wait');
  assert.equal(gateEligibility({ ...base, sessionState: 'none' }), 'wait');
  assert.equal(gateEligibility({ ...base, sessionState: 'interrupted' }), 'interrupted');
  assert.equal(gateEligibility({ ...base, sessionState: 'interrupted', checksGreen: true }), 'interrupted');
  assert.equal(gateEligibility({ ...base, shepherdMarker: false }), 'wait');
  assert.equal(gateEligibility({ ...base, isAgentPr: false }), 'eligible');
  assert.equal(gateEligibility({ ...base, isAgentPr: false, draft: true }), 'wait');
});

test('approval diagnostics identify only related action_required runs and report coverage accurately', () => {
  const found = approvalRequiredRuns([
    { name: 'CI\n@owner `notice`', event: 'pull_request', actor: { login: 'owner' }, head_branch: 'copilot/change', html_url: 'https://example.test/1', conclusion: 'action_required' },
    { name: 'Other', head_branch: 'other', conclusion: 'action_required' },
    { name: 'Passed', head_branch: 'copilot/change', conclusion: 'success' },
    { name: 'Shepherd', event: 'workflow_run', actor: { login: 'Copilot' }, pull_requests: [{ number: 7 }], html_url: 'https://example.test/2', conclusion: 'action_required' },
    { name: 'Unlinked Shepherd', event: 'workflow_run', actor: { login: 'Copilot' }, head_branch: 'main', html_url: 'https://example.test/3', conclusion: 'action_required' },
    { name: 'Unrelated', event: 'workflow_run', actor: { login: 'owner' }, head_branch: 'main', conclusion: 'action_required' },
  ], { number: 7, branch: 'copilot/change', sha: 'abc' });
  assert.equal(found.length, 3);
  assert.equal(found[2].related, false);
  const interruptedMessage = formatApprovalDiagnostic(found, { sessionState: 'interrupted' });
  assert.match(interruptedMessage, /approval-blocked, not failed/);
  assert.match(interruptedMessage, /repository-level, not linked to this PR/);
  assert.match(interruptedMessage, /waits for an active session to finish/);
  assert.match(interruptedMessage, /CI/);
  assert.match(interruptedMessage, /workflow_run/);
  assert.match(interruptedMessage, /Copilot/);
  assert.match(interruptedMessage, /https:\/\/example\.test\/1/);
  assert.doesNotMatch(interruptedMessage, /\n@owner/);
  assert.match(formatApprovalDiagnostic(found, { ciPassing: true, sessionState: 'ok' }), /required `ci` check is passing/);
  assert.equal(formatApprovalDiagnostic([], { sessionState: 'ok' }), '');
});

test('action_required CI does not suppress owner-dispatched CI', () => {
  assert.equal(hasRunnableCiRun([{ conclusion: 'action_required' }]), false);
  assert.equal(hasRunnableCiRun([{ conclusion: null }]), true);
  assert.equal(hasRunnableCiRun([{ conclusion: 'success' }]), true);
});

test('review gate workflow requires the shared shepherd/session decision and cannot mark an agent draft ready', async () => {
  const gate = await readFile(new URL('../workflows/squad-review-gate.yml', import.meta.url), 'utf8');
  assert.match(gate, /gateEligibility/);
  assert.match(gate, /shepherdMarker: !!currentReview\(comments, human, sha, pr\.body\)/);
  assert.match(gate, /classifySession/);
  assert.doesNotMatch(gate, /markPullRequestReadyForReview/);
  assert.ok(gate.indexOf('eligibility !== \'eligible\'') < gate.indexOf('if (!failed.length)'));
  assert.ok(gate.indexOf('eligibility !== \'eligible\'') < gate.indexOf('await merge(pr, liveScope)'));
  assert.match(gate, /- Copilot cloud agent/);
  assert.match(gate, /check_run_id: readiness\.id/);
  assert.doesNotMatch(gate, /rest\.pulls\.merge\(/);
  assert.match(gate, /sessionState: session\.state/);
  assert.match(gate, /REQUIRED_CHECKS: "ci,Squad Plan Binding,Squad Review \/ Architect,Squad Review \/ Security,Squad Review \/ Verifier,Squad Review \/ DevOps"/);
  assert.match(gate, /disablePullRequestAutoMerge/);
});

test('shepherd returns on interrupted sessions before readiness or CI/review dispatch', async () => {
  const shepherd = await readFile(new URL('../workflows/squad-pr-shepherd.yml', import.meta.url), 'utf8');
  const interruptedStart = shepherd.indexOf("if (session.state === 'interrupted')");
  const readyStart = shepherd.indexOf('markPullRequestReadyForReview');
  const interruptedBlock = shepherd.slice(interruptedStart, shepherd.indexOf('const proposal =', interruptedStart));
  assert.ok(interruptedStart >= 0);
  assert.ok(interruptedStart < readyStart);
  assert.match(interruptedBlock, /return core\.info/);
  assert.doesNotMatch(interruptedBlock, /agent\.rest|markPullRequestReadyForReview|dispatch\(/);
  assert.match(shepherd, /hasMarker: !!findMarker\(marker\)/);
  assert.match(shepherd, /DRY_RUN/);
  assert.match(shepherd, /if \(dryRun\)/);
});

test('CI runs both JavaScript test-file extensions', async () => {
  const ci = await readFile(new URL('../workflows/ci.yml', import.meta.url), 'utf8');
  assert.match(ci, /node --test \.github\/scripts\/\*\.test\.\*/);
});
