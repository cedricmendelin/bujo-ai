'use strict';

const { createHash } = require('node:crypto');
const { evaluateCompletion } = require('./completion-declaration');

const METADATA_MARKER = '<!-- squad-pr-metadata -->';
const MAX_BODY_BYTES = 60000;

function bodyHash(body) {
  return createHash('sha256').update(body || '', 'utf8').digest('hex');
}

function trustedComment(comment, owner) {
  return comment.user?.login === owner
    || (comment.user?.type === 'Bot'
      && ['Copilot', 'copilot-swe-agent[bot]', 'github-actions[bot]'].includes(comment.user.login));
}

function workflowComment(comment, owner) {
  return comment.user?.login === owner || comment.user?.login === 'github-actions[bot]'
    && comment.user?.type === 'Bot';
}

function latestComment(comments) {
  return [...comments].sort((a, b) => Date.parse(b.created_at) - Date.parse(a.created_at)
    || b.id - a.id)[0] || null;
}

function fixRequests(comments, owner) {
  return comments.filter(c => workflowComment(c, owner)).flatMap(comment => {
    const match = (comment.body || '').match(/^<!-- squad-gate:fix:([a-f0-9]{40})(?::([a-f0-9]{12}))? -->$/m);
    return match ? [{ ...comment, headSha: match[1], bodyHash: match[2] || null }] : [];
  });
}

function latestFix(comments, owner, sha) {
  return latestComment(fixRequests(comments, owner).filter(c => c.headSha === sha));
}

function reviewMarker(sha, body) {
  return `<!-- squad-shepherd:review:${sha}:${bodyHash(body).slice(0, 12)} -->`;
}

function currentReview(comments, owner, sha, body) {
  const marker = reviewMarker(sha, body);
  return latestComment(comments.filter(c => workflowComment(c, owner)
    && (c.body || '').split(/\r?\n/).includes(marker)));
}

function latestSession(runs) {
  return [...runs].sort((a, b) => Date.parse(b.created_at) - Date.parse(a.created_at)
    || (b.run_number || 0) - (a.run_number || 0) || b.id - a.id)[0] || null;
}

function stalledSession({ session, fix, body, review }) {
  if (!fix || !session || session.status !== 'completed' || session.conclusion !== 'success') return false;
  // A new description needs fresh reviews, not another code commit.
  if (fix.bodyHash && fix.bodyHash !== bodyHash(body).slice(0, 12)) return false;
  if (review && Date.parse(review.created_at) > Date.parse(fix.created_at)) return false;
  const started = Date.parse(session.run_started_at || session.created_at);
  const completed = Date.parse(session.updated_at);
  const requested = Date.parse(fix.created_at);
  return Number.isFinite(started) && Number.isFinite(completed) && Number.isFinite(requested)
    && started > requested && completed >= started;
}

function metadataProposal(comments, owner) {
  return latestComment(comments.filter(c => trustedComment(c, owner)
    && c.user?.login !== 'github-actions[bot]'
    && (c.body || '').split(/\r?\n/).includes(METADATA_MARKER)));
}

function referencesIssue(body, issueNumber, repoFullName) {
  const escaped = repoFullName.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  return new RegExp(`(?:\\b${escaped}|(?<![\\w/.-]))#${issueNumber}\\b|https://github\\.com/${escaped}/issues/${issueNumber}\\b`, 'i').test(body);
}

function validateProposal(comment, pr, scope) {
  const reject = reason => ({ ok: false, reason });
  if (Buffer.byteLength(comment.body || '', 'utf8') > MAX_BODY_BYTES + 10000) {
    return reject('Metadata proposal is too large.');
  }
  const parts = (comment.body || '').split(METADATA_MARKER);
  if (parts.length !== 2) return reject('Publish exactly one metadata marker and JSON object.');
  const match = parts[1].match(/^\s*```json\r?\n([\s\S]*?)\r?\n```[ \t]*(?:\r?\n)?$/);
  if (!match) return reject('Put one fenced JSON object immediately after the metadata marker, with no trailing text.');
  let proposal;
  try {
    proposal = JSON.parse(match[1]);
  } catch {
    return reject('Metadata proposal JSON is malformed.');
  }
  if (!proposal || Array.isArray(proposal) || typeof proposal !== 'object'
    || Object.keys(proposal).sort().join(',') !== 'baseBodySha256,body,headSha'
    || typeof proposal.body !== 'string'
    || !/^[a-f0-9]{40}$/.test(proposal.headSha)
    || !/^[a-f0-9]{64}$/.test(proposal.baseBodySha256)) {
    return reject('Only headSha, baseBodySha256 and a text body are allowed.');
  }
  if (pr.state !== 'open' || proposal.headSha !== pr.head.sha
    || proposal.baseBodySha256 !== bodyHash(pr.body)) {
    return reject('The PR head, description or open state changed; read the PR again and publish a fresh proposal.');
  }
  if (!proposal.body.trim() || Buffer.byteLength(proposal.body, 'utf8') > MAX_BODY_BYTES) {
    return reject(`Replacement description must be nonempty and at most ${MAX_BODY_BYTES} UTF-8 bytes.`);
  }
  if (proposal.body === pr.body) return reject('The proposal does not change the description.');
  if (!/^Completion: (?:full|partial)(?:\r?\n|$)/.test(proposal.body.trimStart())) {
    return reject('Begin the replacement with a whole-line Completion: full or Completion: partial declaration.');
  }
  const completion = evaluateCompletion(proposal.body, { createdAt: pr.created_at });
  if (!completion.ok) return reject(completion.findings.join(' '));
  if (!scope) return reject('No current owner-approved issue plan or owner-waived scope was found.');
  if (scope.waived && comment.user?.login !== scope.owner) {
    return reject('Only the owner may propose changes to a plan-waived description, because that description is the approved scope.');
  }
  if (!scope.waived && (!referencesIssue(proposal.body, scope.issueNumber, scope.repoFullName)
    || !new RegExp(`\\bplan\\s+v${scope.planVersion}\\b`, 'i').test(proposal.body))) {
    return reject('Preserve the approved issue reference and implementation plan version.');
  }
  return { ok: true, body: proposal.body };
}

function reviewsFinishedAfter(latest, names, review) {
  const since = Date.parse(review?.created_at);
  return Number.isFinite(since) && names.every(name => latest[name]?.status === 'completed'
    && Date.parse(latest[name].completed_at) >= since);
}

module.exports = {
  METADATA_MARKER, MAX_BODY_BYTES, bodyHash, trustedComment, workflowComment,
  fixRequests, latestFix, reviewMarker, currentReview, latestSession, stalledSession,
  metadataProposal, referencesIssue, validateProposal, reviewsFinishedAfter,
};
