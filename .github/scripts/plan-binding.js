'use strict';

const { formatRecord, latestRecord, verify } = require('./plan-approval');
const { hash } = require('./review-attestation');

const CHECK = 'Squad Plan Binding';
const NOTICE = '<!-- squad-plan-binding:';
const PLAN_VERSION = /\bplan\s+v([1-9]\d*)\b/i;

function prPlanVersion(body) {
  return Number((body || '').match(PLAN_VERSION)?.[1]) || null;
}

function linkedPrEligible(pr, issueNumber, owner) {
  if (!pr || !['open', 'merged'].includes(pr.state) || (pr.labels || []).includes('plan:waived')) return false;
  const sameRepo = !pr.base?.repo?.full_name || pr.base.repo.full_name === `${owner}/${pr.base.repo.name}`;
  if (!sameRepo) return false;
  const references = (pr.body || '').matchAll(/(?:issue\s+|relates to\s+|part of\s+|closes\s+|fixes\s+|resolves\s+)(?:[a-z0-9_.-]+\/[a-z0-9_.-]+)?#(\d+)\b/gi);
  return [...references].some(match => Number(match[1]) === issueNumber)
    || pr.linkedFromIssueTimeline === true
    || (pr.closingIssuesReferences || []).some(issue => issue.number === issueNumber)
    || (pr.timeline || []).some(event => event.event === 'cross-referenced'
      && event.source?.issue?.number === issueNumber);
}

async function setPrFailure(api, pr, reason) {
  const errors = [];
  if (pr.auto_merge) {
    try { await api.disableAutoMerge(pr); } catch (error) { errors.push(error.message); }
  }
  try { await api.setStatus(pr.head.sha, 'failure', reason); } catch (error) { errors.push(error.message); }
  if (errors.length) throw new Error(errors.join('; '));
}

function waiverBindingSummary(pr, scope) {
  return `Exempt: owner-waived PR #${pr.number}; waiver event ${scope.waiverLabelEvent}; body SHA-256 ${hash(pr.body || '')}; head ${pr.head.sha}.`;
}

async function reconcileWaiver(api, { prNumber, force = false }) {
  const pr = await api.readPullRequest(prNumber);
  if (pr.state !== 'open') return { state: 'closed', pr };
  const isWaived = current => current.labels.some(label => label.name === 'plan:waived');
  if (!isWaived(pr) && !force) return { state: 'not-waived', pr };
  // The existing resolver's waiver branch reads only the PR timeline. Never
  // manufacture an issue approval or consult linked issue plans for this path.
  const scope = isWaived(pr)
    ? await api.resolveScope(pr, await api.listTimeline(prNumber))
    : { ok: false, reason: 'plan:waived was removed; the prior exemption is revoked.' };
  if (!scope.ok || !scope.waived) {
    await setPrFailure(api, pr, scope.reason || 'Missing owner-backed waiver.');
    return { state: 'waiver-invalid', pr };
  }
  const summary = waiverBindingSummary(pr, scope);
  const previous = await api.latestBinding(pr.head.sha);
  if (previous?.conclusion !== 'success' || previous.output?.summary !== summary) {
    // A changed body/event on the same head must not inherit pending auto-merge
    // from old green reviews. The gate can re-enable it after fresh attestations.
    if (pr.auto_merge) await setPrFailure(api, pr, 'Waiver scope changed; current attested reviews are required.');
    await api.setStatus(pr.head.sha, 'success', summary);
  }
  const current = await api.readPullRequest(prNumber);
  const currentScope = isWaived(current)
    ? await api.resolveScope(current, await api.listTimeline(prNumber))
    : { ok: false };
  if (current.state !== 'open' || !currentScope.ok || !currentScope.waived
      || waiverBindingSummary(current, currentScope) !== summary) {
    await setPrFailure(api, current, 'Owner waiver, PR body or head changed during exemption publication.');
    return { state: 'waiver-invalid', pr: current };
  }
  return { state: 'waived', pr: current, scope: currentScope };
}

async function setPrSuccess(api, pr, record) {
  if (prPlanVersion(pr.body) !== record.version) {
    await setPrFailure(api, pr, `PR description must reference approved plan v${record.version}.`);
    return false;
  }
  await api.setStatus(pr.head.sha, 'success', `Bound to issue #${record.issue}, plan v${record.version}, SHA-256 ${record.sha256}, approval event ${record.labelEvent}.`);
  return true;
}

async function reconcileIssue(api, { issueNumber, owner, now = new Date().toISOString() }) {
  const issue = await api.readIssue(issueNumber);
  const labels = (issue.labels || []).map(label => typeof label === 'string' ? label : label.name);
  const comments = await api.listComments(issueNumber);
  const timeline = await api.listTimeline(issueNumber);
  const record = latestRecord(comments, issueNumber);
  let checked = verify({ issue: issueNumber, owner, comments, labels, timeline });
  const prs = (await api.findLinkedPullRequests(issueNumber)).filter(pr => linkedPrEligible(pr, issueNumber, owner));
  if (checked.ok && record) {
    const expectedState = labels.includes('handoff:local') ? 'local' : 'assigned';
    if (record.state !== expectedState) {
      checked = { ok: false, reason: `Handoff changed after approval (${record.state} record, current path ${expectedState}); re-approval is required.` };
    }
  }

  if (checked.ok) {
    const results = [];
    for (const pr of prs.filter(pr => pr.state === 'open')) {
      results.push({ number: pr.number, ok: await setPrSuccess(api, pr, record) });
    }
    return { state: 'bound', record, prs: results };
  }

  const hasBinding = labels.includes('plan:bound');
  const migration = labels.includes('plan:approved') && !record;
  // Approval and binding share a concurrency lock but queued workflow ordering
  // is not guaranteed. A newly labeled issue is not a legacy migration while
  // its approval workflow is still queued/running.
  if (migration && api.approvalPending && await api.approvalPending()) {
    return { state: 'pending-approval', reason: 'Waiting for the trusted approval workflow to finish.', prs: [] };
  }
  const invalidate = hasBinding || migration || (record && labels.includes('plan:approved'));
  if (!invalidate) return { state: 'unbound', reason: checked.reason, prs: [] };

  const reason = migration
    ? 'Legacy label-only approvals are not bound. Re-apply plan:approved after deployment to create a bot approval record.'
    : checked.reason;
  // Fail merge eligibility before removing the labels that make this issue
  // discoverable to sweep/retry. Try every linked PR even if one API call fails.
  const safetyErrors = [];
  for (const pr of prs.filter(pr => pr.state === 'open')) {
    try { await setPrFailure(api, pr, `Issue #${issueNumber} is not bound to a current owner-approved plan: ${reason}`); }
    catch (error) { safetyErrors.push(`#${pr.number}: ${error.message}`); }
  }
  if (safetyErrors.length) throw new Error(`Could not revoke PR merge eligibility; binding retained for retry: ${safetyErrors.join('; ')}`);
  if (record?.state === 'assigned'
      || (issue.assignees || []).some(assignee => assignee.login === 'copilot-swe-agent[bot]')) {
    await api.unassign(issueNumber);
  }
  if (record && record.state !== 'superseded') {
    const superseded = { ...record, state: 'superseded', supersededAt: now };
    delete superseded.recordComment;
    await api.writeRecord(issueNumber, formatRecord(superseded));
  }
  const noticeKey = `${issueNumber}:${record?.comment || 'legacy'}:${record?.sha256 || 'none'}`;
  const marker = `${NOTICE}${noticeKey} -->`;
  if (!comments.some(comment => (comment.body || '').includes(marker))) {
    await api.commentIssue(issueNumber, `${marker}\n⚠️ Plan binding revoked: ${reason}`);
  }

  const outcomes = [];
  for (const pr of prs) {
    outcomes.push({ number: pr.number, ok: false });
    if (pr.state === 'merged') {
      await api.commentPr(pr.number, `🙋 @${owner} Plan #${issueNumber} drift was detected after this PR merged. Review the merge and revert if needed.`);
    }
  }
  // plan:bound is the final cleanup write, so any earlier failure remains
  // sweep-discoverable even when plan:approved was already removed.
  if (labels.includes('plan:approved')) await api.removeLabel(issueNumber, 'plan:approved');
  if (hasBinding) await api.removeLabel(issueNumber, 'plan:bound');
  return { state: migration ? 'migration-required' : 'revoked', reason, prs: outcomes };
}

async function sweep(api, { owner, issueNumbers, now }) {
  const outcomes = [];
  for (const issueNumber of issueNumbers) {
    try {
      outcomes.push({ issueNumber, ...(await reconcileIssue(api, { issueNumber, owner, now })) });
    } catch (error) {
      outcomes.push({ issueNumber, state: 'error', reason: error.message });
    }
  }
  return outcomes;
}

module.exports = { CHECK, linkedPrEligible, prPlanVersion, reconcileIssue, reconcileWaiver, sweep, waiverBindingSummary };
