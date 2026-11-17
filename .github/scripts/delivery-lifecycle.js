'use strict';

const ROLES = ['Architect', 'Security', 'Verifier', 'DevOps'];
const REVIEW_NONE = 'review:none';
const NEEDS_HUMAN = 'needs:human';
const MAX_REPAIR_ATTEMPTS = 3;

function isReviewNone(labels = []) {
  return labels.includes(REVIEW_NONE);
}

function reviewNoneRemovedAt(timeline = []) {
  const event = timeline.filter(item => item.event === 'unlabeled'
      && item.label?.name === REVIEW_NONE)
    .sort((a, b) => Date.parse(a.created_at) - Date.parse(b.created_at)
      || (a.id || 0) - (b.id || 0)).at(-1);
  return event?.created_at || null;
}

function currentHeadCheck(checks, name, sha) {
  return checks.filter(check => check.name === name && check.head_sha === sha)
    .sort((a, b) => (b.id || 0) - (a.id || 0))[0] || null;
}

function reviewRunsForPr(runs, role, number) {
  const title = `Squad Review ${role} · PR #${number}`;
  return runs.filter(run => run.display_title === title)
    .sort((a, b) => Date.parse(b.created_at) - Date.parse(a.created_at)
      || (b.run_number || 0) - (a.run_number || 0));
}

function reviewResultIsFresh({ check, run, sha, removedAt = null }) {
  if (!check || check.head_sha !== sha || check.status !== 'completed') return false;
  if (!run || run.status !== 'completed' || run.conclusion !== 'success') return false;
  const created = Date.parse(run.created_at);
  const started = Date.parse(check.started_at);
  const completed = Date.parse(check.completed_at);
  const runCompleted = Date.parse(run.updated_at);
  // Safe-output checks must belong to this completed run's time window, not
  // merely share its head. In particular, a cancelled run cannot adopt an old
  // passing check and a late old-generation result cannot satisfy a new run.
  return [created, started, completed, runCompleted].every(Number.isFinite)
    && (!removedAt || created >= Date.parse(removedAt))
    && started >= Date.parse(run.run_started_at || run.created_at)
    && completed >= started && completed <= runCompleted;
}

function reviewDispatchNeeded({ fresh, runs = [], forceFresh = false, eventAt = null }) {
  if (fresh) return false;
  const active = runs.filter(run => run.status !== 'completed');
  if (!forceFresh) return active.length === 0;
  const eventTime = Date.parse(eventAt || '');
  return !active.some(run => Number.isFinite(eventTime) && Date.parse(run.created_at) >= eventTime);
}

function reviewSelection(labels = []) {
  return isReviewNone(labels) ? [] : [...ROLES];
}

function reviewFailures(checks, sha, labels = [], runs = new Map(), removedAt = null) {
  if (isReviewNone(labels)) return [];
  return ROLES.flatMap(role => {
    const check = currentHeadCheck(checks, `Squad Review / ${role}`, sha);
    return reviewResultIsFresh({ check, run: runs.get(role), sha, removedAt })
      && check.conclusion !== 'success'
      ? [{ role, check }] : [];
  });
}

function isTrustedHuman(author, owner) {
  return !!author && author.type !== 'Bot'
    && (author.login === owner || ['OWNER', 'MEMBER', 'COLLABORATOR'].includes(author.association));
}

function humanBlockingFeedback({ reviews = [], threads = [], owner }) {
  const latest = new Map();
  for (const review of [...reviews].sort((a, b) => (a.id || 0) - (b.id || 0))) {
    if (!['CHANGES_REQUESTED', 'APPROVED', 'DISMISSED'].includes(review.state) || !isTrustedHuman({
      login: review.user?.login,
      type: review.user?.type,
      association: review.author_association,
    }, owner)) continue;
    latest.set(review.user.login, review);
  }
  return [
    ...[...latest.values()].filter(review => review.state === 'CHANGES_REQUESTED')
      .map(review => ({
        body: review.body?.trim() || 'Resolve this trusted human change request with the reviewer.',
        url: review.html_url,
      })),
    ...threads.filter(thread => !thread.isResolved && !thread.isOutdated
    && isTrustedHuman({
      login: thread.author?.login,
      type: thread.author?.type,
      association: thread.author?.association || thread.authorAssociation,
    }, owner)
      && /^\s*\[blocking\]/i.test(thread.body || '')),
  ];
}

function hasHumanBlocks(input) {
  return humanBlockingFeedback(input).length > 0;
}

function hasRequiredMergeRules(rules) {
  const required = new Set((rules || []).filter(rule => rule.type === 'required_status_checks')
    .flatMap(rule => rule.parameters?.required_status_checks || [])
    .map(item => item.context));
  return required.has('ci') && required.has('Squad Merge Readiness');
}

function isControllerComment(comment, owner) {
  return !!owner && [owner, 'github-actions[bot]'].includes(comment.user?.login);
}

function parseFixRequest(comment, owner) {
  if (!isControllerComment(comment, owner)) return null;
  const match = (comment.body || '').match(/^<!-- squad-review-fix:([a-f0-9]{40})(?::(cloud|local))? -->$/m);
  return match ? { headSha: match[1], mode: match[2] || 'cloud', createdAt: comment.created_at, id: comment.id } : null;
}

function actualRepairAttempts(comments, sessions, currentSha, cloud, owner) {
  const requests = comments.map(comment => parseFixRequest(comment, owner))
    .filter(request => request && (cloud ? request.mode === 'cloud' : true))
    .sort((a, b) => Date.parse(a.createdAt) - Date.parse(b.createdAt));
  if (!cloud) return requests.filter(request => request.headSha !== currentSha).length;

  const terminal = [...new Map(sessions.map(run => [run.id || run.run_started_at || run.created_at, run])).values()]
    .filter(run => run.status === 'completed'
      && !['cancelled', 'action_required', 'skipped', 'neutral'].includes(run.conclusion))
    .map(run => Date.parse(run.run_started_at || run.created_at))
    .filter(Number.isFinite).sort((a, b) => a - b);
  let attempts = 0;
  let runIndex = 0;
  for (const request of requests) {
    const requested = Date.parse(request.createdAt);
    while (runIndex < terminal.length && terminal[runIndex] <= requested) runIndex++;
    if (runIndex < terminal.length) {
      attempts++;
      runIndex++;
    }
  }
  return attempts;
}

function outstandingFixRequest(comments, sessions, cloud, owner) {
  const requests = comments.map(comment => ({ comment, request: parseFixRequest(comment, owner) }))
    .filter(item => item.request && item.request.mode === 'cloud')
    .sort((a, b) => Date.parse(a.request.createdAt) - Date.parse(b.request.createdAt));
  const latestRequest = requests.at(-1);
  if (!latestRequest) return false;
  if (!cloud) return true;
  const requested = Date.parse(latestRequest.request.createdAt);
  return !sessions.some(run => run.status === 'completed'
    && !['cancelled', 'action_required', 'skipped', 'neutral'].includes(run.conclusion)
    && Date.parse(run.run_started_at || run.created_at) > requested);
}

function readiness({ draft, needsHuman, completionOk, sessionOk, ci, reviewsOk, humanBlocks, rulesOk }) {
  if (draft) return { status: 'completed', conclusion: 'failure', reason: 'Draft pull requests are not merge-ready.' };
  if (needsHuman) return { status: 'completed', conclusion: 'failure', reason: 'Paused by needs:human.' };
  if (!completionOk) return { status: 'completed', conclusion: 'failure', reason: 'Completion declaration is invalid.' };
  if (humanBlocks) return { status: 'completed', conclusion: 'failure', reason: 'Trusted human review feedback remains unresolved.' };
  if (!sessionOk) return { status: 'in_progress', reason: 'Waiting for a successful cloud session.' };
  if (!ci) return { status: 'in_progress', reason: 'Waiting for current-head CI.' };
  if (!reviewsOk) return { status: 'in_progress', reason: 'Waiting for selected current-head specialist reviews.' };
  return { status: 'completed', conclusion: 'success', reason: 'Current CI, selected reviews, completion, and human-review checks passed.' };
}

module.exports = {
  MAX_REPAIR_ATTEMPTS, NEEDS_HUMAN, REVIEW_NONE, ROLES,
  actualRepairAttempts, currentHeadCheck, hasHumanBlocks, hasRequiredMergeRules, isReviewNone,
  humanBlockingFeedback, isControllerComment, isTrustedHuman, outstandingFixRequest, parseFixRequest, readiness, reviewFailures,
  reviewDispatchNeeded, reviewNoneRemovedAt, reviewResultIsFresh, reviewRunsForPr, reviewSelection,
};
