'use strict';

const PLAN_HEADING = /^## 📋 Implementation plan v([1-9]\d*) · owner: `?(architect|builder|ai|devops)`?\s*$/;
const PLAN_AUTHORS = new Set(['github-actions[bot]']);
const COPILOT_ASSIGNEE = 'copilot-swe-agent[bot]';

function isCopilotAssignee(assignee) {
  // REST can return the bot's display name instead of its assignment handle.
  return assignee?.login === COPILOT_ASSIGNEE
    || (assignee?.login === 'Copilot' && assignee.type === 'Bot');
}

function parsePlan(comment, owner) {
  if (!comment || typeof comment.body !== 'string'
      || comment.user?.login !== owner && !PLAN_AUTHORS.has(comment.user?.login)) return null;
  const heading = comment.body.replace(/\r\n/g, '\n').trim().split('\n', 1)[0].match(PLAN_HEADING);
  if (!heading) return null;
  const version = Number(heading[1]);
  if (!Number.isSafeInteger(version) || !Number.isSafeInteger(comment.id) || comment.id <= 0) return null;
  return { comment, version, member: heading[2] };
}

function latestPlan(comments, owner) {
  const trusted = comments.filter(comment => comment.user?.login === owner
    || PLAN_AUTHORS.has(comment.user?.login));
  if (trusted.some(comment => typeof comment.body === 'string'
      && comment.body.trimStart().startsWith('## 📋 Implementation plan v')
      && !parsePlan(comment, owner))) {
    return { ok: false, reason: 'A trusted numbered plan heading is malformed.' };
  }
  const candidates = trusted.map(comment => parsePlan(comment, owner)).filter(Boolean)
    .sort((a, b) => a.comment.id - b.comment.id);
  if (!candidates.length) return { ok: false, reason: 'No owner- or workflow-authored versioned plan was found.' };
  if (candidates[0].version !== 1) return { ok: false, reason: 'Plan history must begin with version 1.' };
  for (let i = 1; i < candidates.length; i++) {
    if (candidates[i].version !== candidates[i - 1].version + 1) {
      return { ok: false, reason: 'Plan comments must have consecutive, append-only version numbers.' };
    }
  }
  return { ok: true, plan: candidates.at(-1) };
}

function approvalPath({ labels, assignees = [] }) {
  if (!labels.includes('plan:approved')) return { action: 'none' };
  if (labels.includes('handoff:local')) return { action: 'local' };
  if (assignees.some(isCopilotAssignee)) return { action: 'already-assigned' };
  return { action: 'assign-cloud' };
}

function isEligiblePermission(permission) {
  return ['admin', 'maintain', 'write'].includes(permission);
}

module.exports = {
  COPILOT_ASSIGNEE, PLAN_HEADING, approvalPath, isCopilotAssignee, isEligiblePermission, latestPlan, parsePlan,
};
