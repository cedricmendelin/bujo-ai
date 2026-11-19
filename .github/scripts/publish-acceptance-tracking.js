'use strict';

const { readFileSync } = require('node:fs');
const { appendTracking, recoveryInput } = require('./acceptance-recovery');
const { isControllerComment } = require('./delivery-lifecycle');

async function publishTracking({ github, context, core, agentOutput }) {
  const { owner, repo } = context.repo;
  const inputs = context.payload.inputs;
  if (context.eventName !== 'workflow_dispatch' || context.ref !== `refs/heads/${context.payload.repository.default_branch}`
      || ![inputs?.pr_number, inputs?.issue_number, inputs?.plan_id].every(value =>
        /^[1-9]\d*$/.test(value) && Number.isSafeInteger(Number(value)))) {
    throw new Error('Invalid acceptance recovery dispatch.');
  }
  const items = JSON.parse(readFileSync(agentOutput, 'utf8')).items
    .filter(item => item.type === 'append_acceptance_tracking');
  if (items.length !== 1) throw new Error('Expected exactly one checklist output.');
  const actions = JSON.parse(items[0].actions_json);
  const issue_number = Number(inputs.issue_number);
  const pull_number = Number(inputs.pr_number);
  const marker = `<!-- squad-acceptance-recovery:${inputs.plan_id} -->`;
  const getIssue = () => github.rest.issues.get({ owner, repo, issue_number });
  const getPr = () => github.rest.pulls.get({ owner, repo, pull_number }).then(result => result.data);
  const readComments = number => github.paginate(github.rest.issues.listComments, { owner, repo, issue_number: number, per_page: 100 });
  const [pr, issueComments, { data: issue }] = await Promise.all([getPr(), readComments(issue_number), getIssue()]);
  const claim = issueComments.filter(comment => isControllerComment(comment, owner)
    && (comment.body || '').startsWith(`${marker}\n`)).sort((a, b) => a.id - b.id)[0];
  const input = recoveryInput(pr, issue, issueComments, owner, repo);
  if (!input || input.plan !== inputs.plan_id || input.member !== inputs.delivery_owner
      || !claim?.body.startsWith(`${marker}\nPR #${pull_number}:`)
      || !claim.body.endsWith(`\n<!-- squad-acceptance-snapshot:${input.snapshot} -->`)) {
    throw new Error('Issue, PR or approved plan no longer qualifies for automatic checklist recovery.');
  }
  const body = appendTracking(issue.body, input, actions);
  const [latestPr, latestComments] = await Promise.all([getPr(), readComments(issue_number)]);
  const { data: live } = await getIssue();
  if (live.updated_at !== issue.updated_at || live.body !== issue.body
      || recoveryInput(latestPr, live, latestComments, owner, repo)?.snapshot !== input.snapshot
      || !latestComments.some(comment => comment.id === claim.id && comment.body === claim.body)) {
    throw new Error('Issue, PR or plan changed during recovery; did not overwrite the issue.');
  }
  await github.rest.issues.update({ owner, repo, issue_number, body });
  if ((await getIssue()).data.body !== body) throw new Error('Checklist write could not be verified.');
  core.info(`Appended unchecked acceptance criteria to issue #${issue_number}.`);
}

module.exports = { publishTracking };
