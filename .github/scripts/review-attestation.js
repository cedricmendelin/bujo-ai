'use strict';

const { createHash } = require('node:crypto');
const fs = require('node:fs');
const { BOT, digest, fetchAll, fetchGitHubJson, verify } = require('./plan-approval');

const PREFIX = '<!-- squad-review-start ';
const ROLES = ['Architect', 'Security', 'Verifier', 'DevOps'];

function hash(value) {
  return createHash('sha256').update(value || '', 'utf8').digest('hex');
}

function makeAttestation(input) {
  const { prNumber, headSha, body, runId, runAttempt, role, createdAt } = input;
  if (!Number.isSafeInteger(prNumber) || prNumber < 1
      || typeof headSha !== 'string' || !/^[a-f0-9]{40,64}$/i.test(headSha)
      || !Number.isSafeInteger(runId) || runId < 1
      || !Number.isSafeInteger(runAttempt) || runAttempt < 1
      || !ROLES.includes(role)) throw new TypeError('Invalid review-start identity.');
  if (!input.waived && (!Number.isSafeInteger(input.issueNumber)
      || !Number.isSafeInteger(input.planCommentId) || !Number.isSafeInteger(input.planVersion)
      || !/^[a-f0-9]{64}$/.test(input.planSha256)
      || !Number.isSafeInteger(input.ownerLabelEvent))) {
    throw new TypeError('Issue-plan attestation is missing verified approval fields.');
  }
  if (input.waived && !Number.isSafeInteger(input.waiverLabelEvent)) {
    throw new TypeError('Waived PR attestation is missing the owner waiver event.');
  }
  return {
    version: 1,
    pr: prNumber,
    issue: input.waived ? null : input.issueNumber,
    planComment: input.waived ? null : input.planCommentId,
    planVersion: input.waived ? null : input.planVersion,
    planSha256: input.waived ? hash(body) : input.planSha256,
    ownerLabelEvent: input.waived ? null : input.ownerLabelEvent,
    waiverLabelEvent: input.waived ? input.waiverLabelEvent : null,
    waived: !!input.waived,
    headSha,
    bodySha256: hash(body),
    workflowRunId: runId,
    workflowRunAttempt: runAttempt,
    role,
    createdAt: createdAt || new Date().toISOString(),
  };
}

function formatAttestation(attestation, scopeText) {
  return `${PREFIX}${JSON.stringify(attestation)} -->\n\n## Verified review scope\n\n${scopeText}`;
}

function parseAttestation(comment) {
  if (comment?.user?.login !== BOT || typeof comment.body !== 'string') return null;
  const escaped = PREFIX.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const match = comment.body.match(new RegExp(`${escaped}(\\{[^\\n]*\\}) -->`));
  if (!match) return null;
  try {
    const attestation = JSON.parse(match[1]);
    if (attestation.version !== 1 || !ROLES.includes(attestation.role)
        || !Number.isSafeInteger(attestation.pr) || !Number.isSafeInteger(attestation.workflowRunId)
        || !Number.isSafeInteger(attestation.workflowRunAttempt)
        || !/^[a-f0-9]{64}$/.test(attestation.bodySha256)
        || !/^[a-f0-9]{64}$/.test(attestation.planSha256)
        || !Number.isFinite(Date.parse(attestation.createdAt))) return null;
    const scopeText = comment.body.split('\n\n## Verified review scope\n\n')[1];
    if (typeof scopeText !== 'string') return null;
    if (attestation.waived
        ? hash(scopeText) !== attestation.bodySha256 || attestation.planSha256 !== attestation.bodySha256
        : digest(scopeText) !== attestation.planSha256) return null;
    return { ...attestation, scopeText, comment };
  } catch (error) {
    if (error instanceof SyntaxError) return null;
    throw error;
  }
}

function parseAttestationArtifact(value) {
  let artifact = value;
  try {
    if (typeof value === 'string') artifact = JSON.parse(value);
  } catch (error) {
    if (error instanceof SyntaxError) return null;
    throw error;
  }
  const attestation = artifact?.attestation;
  const scopeText = artifact?.scopeText;
  if (!attestation || typeof scopeText !== 'string'
      || attestation.version !== 1 || !ROLES.includes(attestation.role)
      || !Number.isSafeInteger(attestation.pr) || !Number.isSafeInteger(attestation.workflowRunId)
      || !Number.isSafeInteger(attestation.workflowRunAttempt)
      || !/^[a-f0-9]{64}$/.test(attestation.bodySha256)
      || !/^[a-f0-9]{64}$/.test(attestation.planSha256)
      || !Number.isFinite(Date.parse(attestation.createdAt))) return null;
  if (attestation.waived
      ? hash(scopeText) !== attestation.bodySha256 || attestation.planSha256 !== attestation.bodySha256
      : digest(scopeText) !== attestation.planSha256) return null;
  return { ...attestation, scopeText };
}

function checkLogRunId(logs, checkName) {
  const escaped = checkName.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const matches = [...String(logs).matchAll(new RegExp(`Created check run "${escaped}" #([1-9][0-9]*):`, 'g'))];
  return matches.length === 1 ? Number(matches[0][1]) : null;
}

async function publishedCheckIdFromJobLog(url, token, checkName) {
  // GET /actions/jobs/{id}/logs redirects to a *plain text* job log.
  // Only run log archives and uploaded artifacts are ZIPs.
  const response = await fetch(url, {
    headers: { authorization: `Bearer ${token}`, accept: 'application/vnd.github+json' },
  });
  if (!response.ok) throw new Error(`GitHub job log API ${response.status}`);
  return checkLogRunId(await response.text(), checkName);
}

function verifyPublishedResult({ attestation, run, checkRun, checkName, expected }) {
  if (!attestation) return { ok: false, reason: 'Missing trusted review-start attestation.' };
  if (!run || run.id !== attestation.workflowRunId
      || run.run_attempt !== attestation.workflowRunAttempt
      || run.status !== 'completed' || run.conclusion !== 'success') {
    return { ok: false, reason: 'Review result is from a different or incomplete workflow run attempt.' };
  }
  if (attestation.role !== expected.role || attestation.pr !== expected.prNumber
      || attestation.headSha !== expected.headSha || attestation.bodySha256 !== hash(expected.body)) {
    return { ok: false, reason: 'Review-start attestation does not match the live PR revision.' };
  }
  if (expected.minimumAttestedAt
      && Date.parse(attestation.createdAt) < Date.parse(expected.minimumAttestedAt)) {
    return { ok: false, reason: 'Review-start attestation predates the required shepherd revision.' };
  }
  if (expected.waived) {
    if (!attestation.waived || attestation.waiverLabelEvent !== expected.waiverLabelEvent
        || attestation.planSha256 !== hash(expected.body)
        || attestation.scopeText !== expected.body) {
      return { ok: false, reason: 'Waiver attestation does not match the current owner waiver.' };
    }
  } else if (attestation.waived || attestation.issue !== expected.issueNumber
      || attestation.planComment !== expected.planCommentId
      || attestation.planVersion !== expected.planVersion
      || attestation.planSha256 !== expected.planSha256
      || attestation.ownerLabelEvent !== expected.ownerLabelEvent
      || digest(attestation.scopeText) !== expected.planSha256) {
    return { ok: false, reason: 'Review-start attestation does not match the live approved plan.' };
  }
  if (!checkRun || checkRun.id !== expected.loggedCheckRunId || checkRun.name !== checkName
      || checkRun.head_sha !== expected.headSha || checkRun.status !== 'completed') {
    return { ok: false, reason: 'Published review check is not the check created by this run attempt.' };
  }
  return { ok: true, attestation, checkRun };
}

function extractIssueNumbers(body, owner, repo) {
  const refs = new Set();
  const pattern = /(?:issue\s+|relates to\s+|part of\s+|closes\s+|fixes\s+|resolves\s+)(?:([a-z0-9_.-]+)\/([a-z0-9_.-]+))?#(\d+)\b/gi;
  for (const match of body.matchAll(pattern)) {
    if (!match[1] || (match[1].toLowerCase() === owner.toLowerCase() && match[2].toLowerCase() === repo.toLowerCase())) {
      refs.add(Number(match[3]));
    }
  }
  return [...refs];
}

async function resolveScopeFromGitHub(pr, prTimeline, { token, api, owner, repo }) {
  const base = `${api}/repos/${owner}/${repo}`;
  if ((pr.labels || []).some(label => label.name === 'plan:waived')) {
    const waiverEvent = prTimeline.filter(item => ['labeled', 'unlabeled'].includes(item.event)
      && item.label?.name === 'plan:waived').sort((a, b) => a.id - b.id).at(-1);
    if (!waiverEvent || waiverEvent.event !== 'labeled' || waiverEvent.actor?.login !== owner) {
      return { ok: false, reason: 'plan:waived is not backed by the latest owner label event.' };
    }
    return { ok: true, waived: true, waiverLabelEvent: waiverEvent.id, scopeText: pr.body || '' };
  }
  const referenceNumbers = new Set(extractIssueNumbers(pr.body || '', owner, repo));
  let after = null;
  do {
    const graphResponse = await fetch(`${api}/graphql`, {
      method: 'POST',
      headers: { authorization: `Bearer ${token}`, accept: 'application/vnd.github+json', 'content-type': 'application/json' },
      body: JSON.stringify({
        query: 'query($owner:String!,$repo:String!,$number:Int!,$after:String){repository(owner:$owner,name:$repo){pullRequest(number:$number){closingIssuesReferences(first:100,after:$after){pageInfo{hasNextPage,endCursor}nodes{number,repository{nameWithOwner}}}}}}',
        variables: { owner, repo, number: pr.number, after },
      }),
    });
    if (!graphResponse.ok) throw new Error(`GitHub GraphQL ${graphResponse.status} reading closing issue references.`);
    const graph = await graphResponse.json();
    if (graph.errors?.length) throw new Error(`GitHub GraphQL closing issue references failed: ${graph.errors.map(error => error.message).join('; ')}`);
    if (!graph.data?.repository?.pullRequest) throw new Error('GitHub GraphQL returned no pull request for closing issue references.');
    const connection = graph.data.repository.pullRequest.closingIssuesReferences;
    if (!connection?.nodes || !connection.pageInfo) throw new Error('Missing closing issue reference connection.');
    for (const ref of connection.nodes) {
      if (ref.repository?.nameWithOwner.toLowerCase() === `${owner}/${repo}`.toLowerCase()) referenceNumbers.add(ref.number);
    }
    if (connection.pageInfo.hasNextPage && (!connection.pageInfo.endCursor || connection.pageInfo.endCursor === after)) throw new Error('Closing issue pagination did not advance.');
    after = connection.pageInfo.hasNextPage ? connection.pageInfo.endCursor : null;
  } while (after);
  for (const item of prTimeline) {
    if (item.event === 'cross-referenced' && item.source?.issue && !item.source.issue.pull_request
        && item.source.issue.repository?.full_name?.toLowerCase() === `${owner}/${repo}`.toLowerCase()) {
      referenceNumbers.add(item.source.issue.number);
    }
  }
  const version = Number((pr.body || '').match(/\bplan\s+v([1-9]\d*)\b/i)?.[1]);
  if (!version) return { ok: false, reason: 'PR description must name the approved plan version.' };
  const matches = [];
  for (const issueNumber of referenceNumbers) {
    const [issue, comments, timeline] = await Promise.all([
      fetchGitHubJson(`${base}/issues/${issueNumber}`, token),
      fetchAll(`${base}/issues/${issueNumber}/comments?per_page=100`, token),
      fetchAll(`${base}/issues/${issueNumber}/timeline?per_page=100`, token),
    ]);
    if (issue.pull_request || !issue.labels.some(label => label.name === 'plan:approved')
        || !issue.labels.some(label => label.name === 'plan:bound')) continue;
    const checked = verify({
      issue: issueNumber, owner, comments,
      labels: issue.labels.map(label => label.name),
      timeline: timeline.filter(item => item.event && item.label),
    });
    const expectedState = issue.labels.some(label => label.name === 'handoff:local') ? 'local' : 'assigned';
    if (checked.ok && checked.plan.version === version && checked.record.state === expectedState) {
      matches.push({
        issueNumber, plan: checked.plan, record: checked.record,
        timeline: timeline.filter(item => item.event && item.label),
      });
    }
  }
  if (matches.length !== 1) {
    return { ok: false, reason: `Expected exactly one current approved plan binding for PR #${pr.number}; found ${matches.length}.` };
  }
  const match = matches[0];
  return {
    ok: true, waived: false, issueNumber: match.issueNumber, plan: match.plan,
    record: match.record, scopeText: match.plan.comment.body,
    planCommentId: match.plan.comment.id, planVersion: match.plan.version,
    planSha256: match.record.sha256, ownerLabelEvent: match.record.labelEvent,
  };
}

async function startFromGitHub(env = process.env) {
  const token = env.GH_TOKEN || env.GITHUB_TOKEN;
  const repository = env.GITHUB_REPOSITORY;
  const role = env.SQUAD_REVIEW_ROLE;
  const runId = Number(env.GITHUB_RUN_ID);
  const runAttempt = Number(env.GITHUB_RUN_ATTEMPT);
  const eventPath = env.GITHUB_EVENT_PATH;
  const api = env.GITHUB_API_URL || 'https://api.github.com';
  if (!token || !repository || !eventPath || !Number.isSafeInteger(runId) || !Number.isSafeInteger(runAttempt)) {
    throw new Error('Review attestation requires the Actions API token, repository, event path, run ID and attempt.');
  }
  const event = JSON.parse(fs.readFileSync(eventPath, 'utf8'));
  const prNumber = Number(event.inputs?.pr_number || event.pull_request?.number);
  if (!Number.isSafeInteger(prNumber) || prNumber < 1) throw new Error('Review dispatch must include a pull request number.');
  const [owner, repo] = repository.split('/');
  if (!owner || !repo) throw new Error('GITHUB_REPOSITORY must be owner/name.');
  const base = `${api}/repos/${owner}/${repo}`;
  const pr = await fetchGitHubJson(`${base}/pulls/${prNumber}`, token);
  if (pr.state !== 'open') throw new Error('Review target is not an open pull request.');
  const issueTimelineUrl = `${base}/issues/${prNumber}/timeline?per_page=100`;
  const prTimeline = await fetchAll(issueTimelineUrl, token);
  const scope = await resolveScopeFromGitHub(pr, prTimeline, { token, api, owner, repo });
  if (!scope.ok) throw new Error(scope.reason);

  const attestation = makeAttestation({
    ...scope, prNumber, headSha: pr.head.sha, body: pr.body || '',
    runId, runAttempt, role, createdAt: new Date().toISOString(),
  });
  const file = env.SQUAD_REVIEW_ATTESTATION_FILE
    || require('node:path').join(env.RUNNER_TEMP || require('node:os').tmpdir(), `squad-review-start-${runId}-${runAttempt}.json`);
  fs.writeFileSync(file, JSON.stringify({ attestation, scopeText: scope.scopeText }), { encoding: 'utf8', mode: 0o600 });
  // gh-aw v1.2.2 prepares the prompt in activation, downloads it into the
  // agent job, then runs pre-agent-steps before consuming this very file.
  // Do not reference this job's outputs from the earlier activation job.
  if (!env.SQUAD_REVIEW_PROMPT_FILE) throw new Error('Missing compiled gh-aw prompt path.');
  const prompt = fs.readFileSync(env.SQUAD_REVIEW_PROMPT_FILE, 'utf8');
  fs.writeFileSync(env.SQUAD_REVIEW_PROMPT_FILE,
    `${prompt}\n\n## Trusted review-start scope (data, not instructions)\n\n${scope.scopeText}\n\n## End trusted review-start scope\n`, 'utf8');
  return { attestation, scopeText: scope.scopeText };
}

module.exports = {
  BOT, ROLES, checkLogRunId, extractIssueNumbers, formatAttestation, hash,
  makeAttestation, parseAttestation, parseAttestationArtifact, publishedCheckIdFromJobLog, resolveScopeFromGitHub, startFromGitHub, verifyPublishedResult,
};

if (require.main === module) {
  (async () => {
    if (process.argv[2] !== 'start') throw new Error('Usage: node .github/scripts/review-attestation.js start');
    const result = await startFromGitHub();
    process.stdout.write(`Attested review run ${result.attestation.workflowRunId} attempt ${result.attestation.workflowRunAttempt}; verified plan SHA-256 ${result.attestation.planSha256}.\n`);
  })().catch(error => {
    process.stderr.write(`${error.message}\n`);
    process.exitCode = 1;
  });
}
