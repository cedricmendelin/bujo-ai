'use strict';

const { createHash } = require('node:crypto');
const { latestPlan } = require('./plan-approval');

const REQUEST = '## Scope change request';
const BOT = 'github-actions[bot]';
const digest = value => createHash('sha256').update(value).digest('hex');
const unchanged = comment => !!comment.created_at && comment.created_at === comment.updated_at;
const human = (comment, owner) => comment.user?.login === owner && comment.user?.type === 'User';
const authoredRequest = (comment, owner) => human(comment, owner)
  || ['copilot-swe-agent[bot]', 'Copilot', BOT].includes(comment.user?.login) && comment.user?.type === 'Bot';

function requestComment(comment, owner) {
  if (!authoredRequest(comment, owner) || !comment.body?.startsWith(`${REQUEST}\n`)) return null;
  const invalid = reason => ({ id: comment.id, key: digest(comment.body || ''), created: comment.created_at, invalid: reason });
  if (!Number.isSafeInteger(comment.id) || !unchanged(comment)) return invalid('Request was edited or has no immutable timestamp.');
  const match = comment.body.match(/^## Scope change request\n\n```json\n([\s\S]+)\n```\s*$/);
  if (!match) return invalid('Use the documented JSON request format.');
  let data;
  try { data = JSON.parse(match[1]); } catch { return invalid('Request JSON is invalid.'); }
  if (!data || Array.isArray(data)) return invalid('Request must be an object.');
  const fields = ['issue', 'plan', 'before', 'after', 'reason', 'impact', 'verification', 'expires'];
  if (Object.keys(data).length !== fields.length || fields.some(key => !Object.hasOwn(data, key))) {
    return invalid('Request fields do not match the documented contract.');
  }
  if (!['issue', 'plan'].every(key => Number.isSafeInteger(data[key]) && data[key] > 0)
      || !['before', 'after', 'reason', 'impact', 'verification'].every(key =>
        typeof data[key] === 'string' && data[key].trim() && data[key].length <= 4000)
      || typeof data.expires !== 'string' || !Number.isFinite(Date.parse(data.expires))
      || Date.parse(data.expires) <= Date.parse(comment.created_at)
      || Date.parse(data.expires) - Date.parse(comment.created_at) > 7 * 86400000
      || Buffer.byteLength(comment.body) > 16000) return invalid('Invalid fields, length or expiry (maximum seven days).');
  return { id: comment.id, key: digest(comment.body), data, url: comment.html_url, created: comment.created_at };
}

function command(comment, owner) {
  if (!human(comment, owner) || !unchanged(comment)) return null;
  const match = comment.body?.trim().match(/^\/(approve|reject)-change ([1-9]\d*)(?:[ \t]+([^\r\n]+))?$/);
  if (!match || !Number.isSafeInteger(Number(match[2]))) return null;
  return { request: Number(match[2]), decision: match[1], reason: match[3] || '', comment };
}

function receipt(comment) {
  if (comment.user?.login !== BOT || comment.user?.type !== 'Bot' || !unchanged(comment)) return null;
  const match = comment.body?.match(/^<!-- squad-decision:([1-9]\d*):([a-f0-9]{64}):(approve|reject) -->\n/);
  return match && { request: Number(match[1]), key: match[2], decision: match[3], comment };
}

function decisionState(comments, owner, baselines, now = Date.now()) {
  const requests = comments.map(comment => requestComment(comment, owner)).filter(Boolean);
  const receipts = comments.map(receipt).filter(Boolean).sort((a, b) => a.comment.id - b.comment.id);
  const results = requests.map(request => {
    const matching = receipts.filter(item => item.request === request.id && item.key === request.key
      && Date.parse(item.comment.created_at) >= Date.parse(request.created));
    const rejection = matching.find(item => item.decision === 'reject');
    const first = matching.find(item => item.decision === 'reject'
      || !request.invalid && Date.parse(item.comment.created_at) < Date.parse(request.data.expires));
    if (first?.decision === 'reject' || request.invalid && rejection) {
      return { ...request, status: 'rejected', receipt: rejection.comment.id };
    }
    if (request.invalid) return { ...request, status: 'invalid' };
    const baseline = baselines.get(request.data.issue);
    if (!baseline?.approved || baseline.plan !== request.data.plan) {
      return { ...request, status: rejection ? 'rejected' : 'stale', ...(rejection && { receipt: rejection.comment.id }) };
    }
    if (first) return { ...request, status: 'approved', receipt: first.comment.id };
    return { ...request, status: now >= Date.parse(request.data.expires) ? 'expired' : 'pending' };
  });
  // An edited/deleted approved request must not silently disappear from effective scope.
  for (const item of receipts) {
    if (item.decision === 'approve' && !results.some(request => request.id === item.request)) {
      const rejected = receipts.some(other => other.request === item.request && other.key === item.key
        && other.decision === 'reject' && other.comment.id > item.comment.id);
      results.push({ id: item.request, key: item.key, status: rejected ? 'rejected' : 'invalid',
        invalid: 'Approved request is missing or changed; owner must explicitly reject the obsolete record.' });
    }
  }
  return {
    requests: results,
    blocked: results.some(item => ['pending', 'invalid', 'expired', 'stale'].includes(item.status)),
    amendments: results.filter(item => item.status === 'approved').sort((a, b) => a.receipt - b.receipt),
    key: results.length ? digest(JSON.stringify(results)) : '',
  };
}

async function loadState({ github, owner, repo, pr, comments, now }) {
  const requests = comments.map(comment => requestComment(comment, owner)).filter(item => item?.data);
  const baselines = new Map();
  for (const number of new Set(requests.map(item => item.data.issue))) {
    const { data: issue } = await github.rest.issues.get({ owner, repo, issue_number: number });
    const history = await github.paginate(github.rest.issues.listComments, { owner, repo, issue_number: number, per_page: 100 });
    const selected = latestPlan(history, owner);
    const linked = pr?.body?.split(/\r?\n/).some(line =>
      new RegExp(`^(?:Relates to|Refs|Fixes|Closes|Resolves):?\\s+#${number}\\b`, 'i').test(line.trim()));
    baselines.set(number, {
      approved: linked && !issue.pull_request && issue.state === 'open' && issue.labels.some(label => label.name === 'plan:approved'),
      plan: selected.ok ? selected.plan.comment.id : null,
    });
  }
  return decisionState(comments, owner, baselines, now);
}

function teamsDecision({ actor, expectedActor, responder, expectedResponder, enabled, decision, request, key }) {
  if (enabled !== 'true' || !expectedActor || !expectedResponder
      || actor !== expectedActor || responder !== expectedResponder) {
    throw new Error('Teams approval is disabled or its trusted flow/responder identity did not match.');
  }
  if (!['approve', 'reject'].includes(decision) || !/^[1-9]\d*$/.test(request)
      || !Number.isSafeInteger(Number(request)) || !/^[a-f0-9]{64}$/.test(key)) throw new Error('Invalid Teams decision payload.');
  return { request: Number(request), decision, key };
}

function card({ request, title }) {
  const safe = value => String(value).replace(/[<>]/g, '').slice(0, 4000);
  return {
    type: 'AdaptiveCard', version: '1.4', $schema: 'http://adaptivecards.io/schemas/adaptive-card.json',
    body: [
      { type: 'TextBlock', text: safe(title), weight: 'Bolder', wrap: true },
      ...['before', 'after', 'reason', 'impact', 'verification', 'expires'].map(field => ({
        type: 'TextBlock', text: `${field}: ${safe(request.data[field])}`, wrap: true,
      })),
    ],
    actions: [
      ...['approve', 'reject'].map(decision => ({
        type: 'Action.Submit', title: decision === 'approve' ? 'Approve' : 'Reject',
        data: { request: String(request.id), key: request.key, decision },
      })),
      { type: 'Action.OpenUrl', title: 'Open in GitHub', url: request.url },
    ],
  };
}

module.exports = { BOT, REQUEST, card, command, decisionState, digest, loadState, receipt, requestComment, teamsDecision };
