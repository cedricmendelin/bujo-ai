'use strict';

const { createHash } = require('node:crypto');
const { execFileSync } = require('node:child_process');

const BOT = 'github-actions[bot]';
const PLAN_HEADING = /^## 📋 Implementation plan v([1-9]\d*) · owner: `?(architect|builder|ai|devops)`?\s*$/;
const RECORD = /^<!-- squad-plan-approval (\{[^\n]*\}) -->$/m;

function normalize(body) {
  if (typeof body !== 'string') throw new TypeError('Plan body must be text.');
  return body.replace(/\r\n/g, '\n').trim();
}

function digest(body) {
  return createHash('sha256').update(normalize(body), 'utf8').digest('hex');
}

function parsePlan(comment, owner) {
  if (![BOT, owner].includes(comment?.user?.login) || typeof comment.body !== 'string') return null;
  const heading = normalize(comment.body).split('\n', 1)[0].match(PLAN_HEADING);
  if (!heading || !Number.isSafeInteger(comment.id) || comment.id <= 0) return null;
  const version = Number(heading[1]);
  if (!Number.isSafeInteger(version)) return null;
  return { comment, version, member: heading[2], sha256: digest(comment.body) };
}

function selectPlan(comments, owner) {
  const invalid = comments.some(comment => [BOT, owner].includes(comment?.user?.login)
    && typeof comment.body === 'string'
    && comment.body.trimStart().startsWith('## 📋 Implementation plan v')
    && !parsePlan(comment, owner));
  if (invalid) return { ok: false, reason: 'A trusted plan heading is malformed.' };
  const plans = comments.map(comment => parsePlan(comment, owner)).filter(Boolean)
    .sort((a, b) => a.comment.id - b.comment.id);
  for (let i = 1; i < plans.length; i++) {
    if (plans[i].version <= plans[i - 1].version) {
      return { ok: false, reason: 'Plan versions must increase with each new plan comment.' };
    }
  }
  if (!plans.length) return { ok: false, reason: 'No trusted versioned plan found.' };
  return { ok: true, plan: plans[plans.length - 1] };
}

function parseRecord(comment, issue) {
  if (comment?.user?.login !== BOT || typeof comment.body !== 'string') return null;
  const match = comment.body.match(RECORD);
  if (!match) return null;
  let record;
  try {
    record = JSON.parse(match[1]);
  } catch (error) {
    if (error instanceof SyntaxError) return null;
    throw error;
  }
  if (!record || record.issue !== issue || !Number.isSafeInteger(record.comment)
      || !Number.isSafeInteger(record.version) || !/^[a-f0-9]{64}$/.test(record.sha256)
      || !['architect', 'builder', 'ai', 'devops'].includes(record.member)
      || !['approved', 'local', 'assigned', 'superseded'].includes(record.state)
      || !Number.isSafeInteger(record.labelEvent)
      || !Number.isFinite(Date.parse(record.approvedAt))
      || !Number.isFinite(Date.parse(record.createdAt))) return null;
  return { ...record, recordComment: comment };
}

function latestRecord(comments, issue) {
  return comments.map(comment => parseRecord(comment, issue)).filter(Boolean)
    .sort((a, b) => a.recordComment.id - b.recordComment.id).at(-1) || null;
}

function latestApprovalEvent(timeline) {
  return timeline.filter(item => ['labeled', 'unlabeled'].includes(item.event)
    && item.label?.name === 'plan:approved').sort((a, b) => a.id - b.id).at(-1) || null;
}

function verify({ issue, owner, comments, labels, timeline }) {
  if (!labels.includes('plan:approved') || !labels.includes('plan:bound')) {
    return { ok: false, reason: 'The plan approval or binding label is missing.' };
  }
  const selected = selectPlan(comments, owner);
  if (!selected.ok) return selected;
  const record = latestRecord(comments, issue);
  if (!record || !['approved', 'local', 'assigned'].includes(record.state)) {
    return { ok: false, reason: 'No active trusted approval record.' };
  }
  const event = timeline.find(item => item.id === record.labelEvent);
  const currentApproval = latestApprovalEvent(timeline);
  if (!event || event !== currentApproval || event.event !== 'labeled'
      || event.actor?.login !== owner || event.created_at !== record.approvedAt) {
    return { ok: false, reason: 'The approval record does not match the current owner label event.' };
  }
  const plan = selected.plan;
  if (record.comment !== plan.comment.id || record.version !== plan.version
      || record.member !== plan.member || record.sha256 !== plan.sha256) {
    return { ok: false, reason: 'The approved plan no longer matches the latest plan.' };
  }
  const updated = Date.parse(plan.comment.updated_at);
  const approved = Date.parse(record.approvedAt);
  if (!Number.isFinite(updated) || updated >= approved) {
    return { ok: false, reason: 'The plan was changed at or after the approval event.' };
  }
  return { ok: true, plan, record };
}

function prepareApproval({ issue, owner, sender, comments, labels, timeline, now }) {
  if (sender !== owner) return { ok: false, reason: 'Only the repository owner can approve a plan.' };
  if (!labels.includes('plan:approved')) return { ok: false, reason: 'The approval label is no longer present.' };
  const event = latestApprovalEvent(timeline);
  if (!event || event.event !== 'labeled' || event.actor?.login !== owner
      || !Number.isSafeInteger(event.id) || !Number.isFinite(Date.parse(event.created_at))) {
    return { ok: false, reason: 'No current owner approval event found.' };
  }
  const selected = selectPlan(comments, owner);
  if (!selected.ok) return selected;
  const plan = selected.plan;
  const updated = Date.parse(plan.comment.updated_at);
  if (!Number.isFinite(updated) || updated >= Date.parse(event.created_at)) {
    return { ok: false, reason: 'Plan creation or edit must precede approval.' };
  }
  const record = {
    issue, comment: plan.comment.id, version: plan.version, sha256: plan.sha256,
    member: plan.member, labelEvent: event.id, approvedAt: event.created_at,
    createdAt: now || new Date().toISOString(), state: 'approved',
  };
  return { ok: true, plan, record };
}

function formatRecord(record) {
  // Parsed records carry API evidence (recordComment). Never serialize that
  // evidence, caller metadata, or recursively nested previous records.
  const fields = ['issue', 'comment', 'version', 'sha256', 'member', 'labelEvent',
    'approvedAt', 'createdAt', 'state', 'supersededAt'];
  const canonical = Object.fromEntries(fields.filter(key => record[key] !== undefined)
    .map(key => [key, record[key]]));
  return `<!-- squad-plan-approval ${JSON.stringify(canonical)} -->`;
}

function sameApproval(prepared, checked) {
  return checked.ok && ['issue', 'comment', 'version', 'sha256', 'member', 'labelEvent', 'approvedAt']
    .every(key => prepared.record[key] === checked.record[key])
    // Hash normalization is intentional for approval; assignment must nevertheless
    // use exactly the snapshot's bytes, never bytes from another verified record.
    && prepared.plan.comment.body === checked.plan.comment.body;
}

function parseArgs(argv) {
  if (argv[0] !== 'verify' || argv[1] !== '--issue' || !/^[1-9]\d*$/.test(argv[2] || '')) {
    throw new Error('Usage: node .github/scripts/plan-approval.js verify --issue <number>');
  }
  return { issue: Number(argv[2]) };
}

async function fetchGitHubJson(url, token) {
  const response = await fetch(url, {
    headers: { authorization: `Bearer ${token}`, accept: 'application/vnd.github+json', 'x-github-api-version': '2022-11-28' },
  });
  if (!response.ok) throw new Error(`GitHub API ${response.status} for ${url}`);
  return response.json();
}

async function fetchAll(url, token) {
  const results = [];
  let next = url;
  while (next) {
    const response = await fetch(next, {
      headers: { authorization: `Bearer ${token}`, accept: 'application/vnd.github+json', 'x-github-api-version': '2022-11-28' },
    });
    if (!response.ok) throw new Error(`GitHub API ${response.status} for ${next}`);
    results.push(...await response.json());
    const link = response.headers.get('link') || '';
    next = link.match(/<([^>]+)>;\s*rel="next"/)?.[1] || null;
  }
  return results;
}

async function verifyFromGitHub(issueNumber, env = process.env) {
  const token = env.GH_TOKEN || env.GITHUB_TOKEN;
  const repo = env.GITHUB_REPOSITORY;
  const api = env.GITHUB_API_URL || 'https://api.github.com';
  if (!token || !repo || !/^[1-9]\d*$/.test(String(issueNumber))) {
    throw new Error('GH_TOKEN, GITHUB_REPOSITORY, and a valid issue number are required.');
  }
  const [owner, name] = repo.split('/');
  if (!owner || !name) throw new Error('GITHUB_REPOSITORY must be owner/name.');
  const base = `${api}/repos/${owner}/${name}`;
  const [issue, comments, timeline] = await Promise.all([
    fetchGitHubJson(`${base}/issues/${issueNumber}`, token),
    fetchAll(`${base}/issues/${issueNumber}/comments?per_page=100`, token),
    fetchAll(`${base}/issues/${issueNumber}/timeline?per_page=100`, token),
  ]);
  const checked = verify({
    issue: issueNumber, owner,
    comments, labels: issue.labels.map(label => label.name),
    timeline: timeline.filter(item => item.event && item.label),
  });
  if (!checked.ok) throw new Error(checked.reason);
  return checked;
}

module.exports = {
  BOT, digest, fetchAll, fetchGitHubJson, formatRecord, latestApprovalEvent,
  latestRecord, normalize, parsePlan, parseRecord, prepareApproval, selectPlan,
  sameApproval, verify, verifyFromGitHub,
};

if (require.main === module) {
  (async () => {
    const { issue } = parseArgs(process.argv.slice(2));
    const checked = await verifyFromGitHub(issue);
    process.stdout.write(checked.plan.comment.body);
    if (!checked.plan.comment.body.endsWith('\n')) process.stdout.write('\n');
  })().catch(error => {
    process.stderr.write(`${error.message}\n`);
    process.exitCode = 1;
  });
}
