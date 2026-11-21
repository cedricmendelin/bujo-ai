'use strict';

const ROLES = ['Architect', 'Security', 'Verifier', 'DevOps'];
const REVIEW_NONE = 'review:none';
const NEEDS_HUMAN = 'needs:human';
const MAX_REPAIR_ATTEMPTS = 3;

function reviewInputKey(body = '') {
  // Ignore only line endings, blank lines, and trailing whitespace. Indentation,
  // code, scope, and evidence are review inputs, not cosmetic text.
  const text = body.replace(/\r\n/g, '\n').split('\n')
    .map(line => line.trimEnd()).filter(line => line.length).join('\n');
  return require('node:crypto').createHash('sha256').update(text).digest('hex');
}

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

function reviewRunsForPr(runs, role, number, inputKey = null) {
  const title = `Squad Review ${role} · PR #${number}${inputKey ? ` · input ${inputKey}` : ''}`;
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
  const runStarted = Date.parse(run.run_started_at || run.created_at);
  // Safe-output checks must belong to this completed run's time window, not
  // merely share its head. In particular, a cancelled run cannot adopt an old
  // passing check and a late old-generation result cannot satisfy a new run.
  // GitHub may assign started_at after the publisher's completed_at. Bind
  // both independently to the run rather than assuming their API ordering.
  return [created, started, completed, runCompleted, runStarted].every(Number.isFinite)
    && (!removedAt || created >= Date.parse(removedAt))
    && started >= runStarted && started <= runCompleted
    && completed >= runStarted && completed <= runCompleted;
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

const specialistRules = {
  Security: /(?:^|\/)(?:infra\/(?:identity|api|pwa_client|keyvault|sql|data)\.tf$|[^/]*(?:auth|identity|permission|secret|mcp|repository|dbcontext|serializer|migration|privacy|dependabot)[^/]*(?:\/|$))|\.csproj$|(?:Directory\.(?:Packages|Build)\.props|global\.json|Dockerfile|\.terraform\.lock\.hcl|(?:package|packages)[^/]*lock\.json)$|^(?:\.github\/(?:workflows|scripts|actions)\/|src\/Bujo\.Host\/Program\.cs$)/i,
  DevOps: /^(?:infra\/|\.github\/(?:workflows|scripts|actions)\/|Dockerfile$|\.dockerignore$|global\.json$|src\/Bujo\.(?:AppHost|ServiceDefaults)\/)|(?:appsettings[^/]*\.json|launchSettings\.json)$/i,
  Architect: /^(?:src\/Bujo\.(?:Domain|Application)\/|docs\/(?:adr\/|(?:domain|architecture|concept|knowledge|decisions|ai-chat)\.md$)|\.squad\/decisions\.md$)|(?:Endpoints|Contracts?|Mcp)[^/]*\.(?:cs|json|ya?ml)$/i,
};
const knownReviewPaths = /^(?:src\/Bujo\.(?:Domain|Application|Host|Infrastructure|Web|AppHost|ServiceDefaults)\/|tests\/|docs\/|infra\/|\.github\/|\.squad\/|(?:README\.md|LICENSE|\.gitignore|\.gitattributes|\.dockerignore|Dockerfile|Bujo\.slnx|Directory\.(?:Build|Packages)\.props|global\.json)$)/;

function routeReviews({ labels, files, changedFiles, reviews, owner, sha, baseSha, inputKey }) {
  if (isReviewNone(labels)) return { roles: [], omitted: [], blocked: '', reasons: {} };
  const routes = ['Security', 'DevOps', 'Architect'];
  const reasons = { Verifier: 'default' };
  const ownerReviews = reviews.filter(review => review.user?.login === owner
    && review.user?.type === 'User' && review.commit_id === sha
    && ['APPROVED', 'COMMENTED'].includes(review.state));
  const paths = files.flatMap(file => [file.filename, ...(file.status === 'renamed' ? [file.previous_filename] : [])]);
  const incomplete = !Number.isInteger(changedFiles) || changedFiles < 1 || changedFiles !== files.length
    || new Set(files.map(file => file.filename)).size !== files.length
    || paths.some(path => typeof path !== 'string' || !path);
  for (const role of routes) {
    if (paths.some(path => typeof path === 'string' && specialistRules[role].test(path))) reasons[role] = 'changed paths';
    if (ownerReviews.some(review => review.body?.split(/\r?\n/).includes(`Squad review: ${role}`))) reasons[role] = 'owner escalation';
  }
  const specialists = routes.filter(role => reasons[role]);
  const roles = ['Verifier', ...specialists.slice(0, 2)];
  const omitted = specialists.slice(2);
  const coverageKey = reviewInputKey(JSON.stringify([sha, baseSha, inputKey, roles, omitted]));
  const covered = ownerReviews.some(review => review.state === 'APPROVED'
    && review.body?.split(/\r?\n/).includes(`Squad coverage: ${omitted.join(', ')} ${coverageKey}`));
  const blocked = incomplete || paths.some(path => !knownReviewPaths.test(path))
    ? 'Reviewer routing needs human attention: the changed-file inventory is incomplete or contains an unmapped path.'
    : omitted.length && !covered
      ? `Reviewer cap reached. Split the PR or have the repository owner cover ${omitted.join(', ')} and submit an approving review with this exact line: Squad coverage: ${omitted.join(', ')} ${coverageKey}`
      : '';
  return { roles, omitted, blocked, reasons };
}

function reviewFailures(checks, sha, labels = [], runs = new Map(), removedAt = null, roles = reviewSelection(labels)) {
  return roles.flatMap(role => {
    const check = currentHeadCheck(checks, `Squad Review / ${role}`, sha);
    return reviewResultIsFresh({ check, run: runs.get(role), sha, removedAt })
      && check.conclusion !== 'success'
      ? [{ role, check }] : [];
  });
}

function reviewEvidence({ pr, files, ci, scope }) {
  const evidence = {
    head: pr.head.sha,
    bodyKey: reviewInputKey(pr.body || ''),
    files: Array.isArray(files) && files.length > 0 && files.length === pr.changed_files && files.length <= 75
      ? files.map(file => [file.status, file.filename, file.previous_filename].filter(Boolean)) : null,
    ci: ci ? { status: ci.status, conclusion: ci.conclusion, checkId: ci.id } : null,
    scope,
  };
  const serialize = () => JSON.stringify(evidence).replace(/[<>`$]/g, char =>
    `\\u${char.charCodeAt(0).toString(16).padStart(4, '0')}`);
  // Omit whole sections, never truncate an issue's requirements or an inventory.
  for (const section of ['files', 'scope']) {
    if (Buffer.byteLength(serialize()) <= 8000) break;
    evidence[section] = null;
  }
  return serialize();
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
  return match ? {
    headSha: match[1], mode: match[2] || 'cloud', createdAt: comment.created_at, id: comment.id,
    token: (comment.body || '').match(/^<!-- squad-repair-request:([a-f0-9-]{36}) -->$/m)?.[1] || null,
  } : null;
}

function repairEpisode(comments, owner) {
  const resume = comments.filter(comment => isControllerComment(comment, owner)
    && /^<!-- squad-repair-resume:\d+ -->$/m.test(comment.body || ''))
    .sort((a, b) => Date.parse(a.created_at) - Date.parse(b.created_at) || a.id - b.id).at(-1);
  return resume ? comments.filter(comment => Date.parse(comment.created_at) >= Date.parse(resume.created_at)
    && comment.id > resume.id) : comments;
}

function repairOutcomes(comments, sessions, owner) {
  const requests = comments.map(comment => parseFixRequest(comment, owner))
    .filter(request => request?.mode === 'cloud');
  const outcomes = [];
  for (const comment of comments) {
    if (!['copilot-swe-agent[bot]', 'copilot-swe-agent', 'Copilot'].includes(comment.user?.login)
        || comment.user?.type !== 'Bot') continue;
    const match = (comment.body || '').match(
      /^<!-- squad-repair-outcome:([a-f0-9-]{36}):([1-9]\d*):(attempted|capability-blocked|external-blocked) -->$/m,
    );
    if (!match) continue;
    const request = requests.find(item => item.token === match[1]);
    const session = sessions.find(run => String(run.id) === match[2]);
    const requested = Date.parse(request?.createdAt);
    const started = Date.parse(session?.run_started_at || session?.created_at);
    const reported = Date.parse(comment.created_at);
    if (!request || !session || session.status !== 'completed'
        || !['success', 'failure'].includes(session.conclusion)
        || ![requested, started, reported].every(Number.isFinite)
        || started < requested || reported < requested) continue;
    outcomes.push({ request, session, outcome: match[3], comment });
  }
  return outcomes;
}

function actualRepairAttempts(comments, sessions, currentSha, cloud, owner) {
  const requests = comments.map(comment => parseFixRequest(comment, owner))
    .filter(request => request?.mode === (cloud ? 'cloud' : 'local'))
    .sort((a, b) => Date.parse(a.createdAt) - Date.parse(b.createdAt));
  if (!cloud) return requests.filter(request => request.headSha !== currentSha).length;

  const outcomes = repairOutcomes(comments, sessions, owner);
  const blocked = new Set(outcomes.filter(item => item.outcome !== 'attempted').map(item => item.request.token));
  const countedRuns = new Set();
  const countedRequests = new Set();
  for (const item of outcomes) {
    if (item.outcome !== 'attempted' || blocked.has(item.request.token)
        || countedRequests.has(item.request.token) || countedRuns.has(item.session.id)) continue;
    countedRequests.add(item.request.token);
    countedRuns.add(item.session.id);
  }
  return countedRuns.size;
}

function outstandingFixRequest(comments, sessions, cloud, owner) {
  const requests = comments.map(comment => ({ comment, request: parseFixRequest(comment, owner) }))
    .filter(item => item.request && item.request.mode === 'cloud')
    .sort((a, b) => Date.parse(a.request.createdAt) - Date.parse(b.request.createdAt));
  const latestRequest = requests.at(-1);
  if (!latestRequest) return false;
  if (!cloud) return true;
  return !repairOutcomes(comments, sessions, owner).some(item => item.request.id === latestRequest.request.id);
}

function readiness({ draft, needsHuman, sessionOk, ci, reviewsOk, humanBlocks, rulesOk }) {
  if (draft) return { status: 'completed', conclusion: 'failure', reason: 'Draft pull requests are not merge-ready.' };
  if (needsHuman) return { status: 'completed', conclusion: 'failure', reason: 'Paused by needs:human.' };
  if (humanBlocks) return { status: 'completed', conclusion: 'failure', reason: 'Trusted human review feedback remains unresolved.' };
  if (!sessionOk) return { status: 'in_progress', reason: 'Waiting for a successful cloud session.' };
  if (!ci) return { status: 'in_progress', reason: 'Waiting for current-head CI.' };
  if (!reviewsOk) return { status: 'in_progress', reason: 'Waiting for selected current-head specialist reviews.' };
  return { status: 'completed', conclusion: 'success', reason: 'Current CI, selected reviews, and human-review checks passed with no needs:human pause.' };
}

module.exports = {
  MAX_REPAIR_ATTEMPTS, NEEDS_HUMAN, REVIEW_NONE, ROLES,
  actualRepairAttempts, currentHeadCheck, hasHumanBlocks, hasRequiredMergeRules, isReviewNone,
  humanBlockingFeedback, isControllerComment, isTrustedHuman, outstandingFixRequest, parseFixRequest, readiness, reviewFailures,
  repairEpisode, repairOutcomes, reviewInputKey,
  reviewDispatchNeeded, reviewEvidence, reviewNoneRemovedAt, reviewResultIsFresh, reviewRunsForPr, reviewSelection, routeReviews,
};
