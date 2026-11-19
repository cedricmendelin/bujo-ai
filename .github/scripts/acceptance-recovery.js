'use strict';

const { createHash } = require('node:crypto');
const { acceptanceTracking, partialHandoff, evaluateCompletion } = require('./completion-declaration');
const { latestPlan } = require('./plan-approval');

function recoveryInput(pr, issue, comments, owner, repo) {
  const handoff = partialHandoff(pr.body, `${owner}/${repo}`);
  const paused = item => item.labels.some(label => ['needs:human', 'handoff:local'].includes(label.name));
  if (!handoff || handoff.issue !== issue.number || pr.state !== 'open' || pr.draft
      || pr.head.repo?.full_name !== `${owner}/${repo}` || issue.state !== 'open' || issue.pull_request
      || paused(pr) || paused(issue) || !issue.labels.some(label => label.name === 'plan:approved')
      || !evaluateCompletion(pr.body, { createdAt: pr.created_at }).ok
      || /^ {0,3}#{1,6}\s+Acceptance tracking\b/im.test(issue.body || '')) return null;
  const selected = latestPlan(comments, owner);
  if (!selected.ok) return null;
  const { member, comment, version } = selected.plan;
  // Only recover issues with a single, explicit flat acceptance list.
  const sections = (issue.body || '').split(/^##[ \t]+Acceptance criteria[ \t]*\r?$/im);
  if (sections.length !== 2) return null;
  const criteria = sections[1].split(/^ {0,3}#{1,6}[ \t]+/m, 1)[0].trim().split(/\r?\n/).filter(line => line.trim());
  if (!criteria.length || criteria.length > 30 || criteria.some(line => !/^[-*] [^\s[]/.test(line))) return null;
  const snapshot = createHash('sha256')
    .update(JSON.stringify([pr.head.sha, pr.body, issue.body, comment.id, comment.body])).digest('hex');
  return { member, version, plan: String(comment.id), snapshot, criteria: criteria.map(line => line.slice(2)) };
}

function appendTracking(body, input, actions) {
  if (!Array.isArray(actions) || actions.length !== input.criteria.length
      || actions.some(action => typeof action !== 'string' || !action.trim() || action.length > 300
        || /[\u0000-\u001f\u007f<>@\[\]\\`]/.test(action))) {
    throw new Error('Provide exactly one short, plain-text next action per acceptance criterion.');
  }
  const rows = input.criteria.map((criterion, index) =>
    `- [ ] open; owner: ${input.member}; next: ${actions[index].trim()} — Criterion: ${criterion}`);
  const result = `${body.trimEnd()}\n\n## Acceptance tracking\n\n${rows.join('\n')}\n`;
  const tracking = acceptanceTracking(result);
  if (!tracking.ok || tracking.incomplete.length !== input.criteria.length || result.length > 65536) {
    throw new Error('Recovered checklist did not pass acceptance tracking validation.');
  }
  return result;
}

module.exports = { appendTracking, recoveryInput };
