'use strict';

const { latestPlan } = require('./plan-approval');
const { digest } = require('./squad-decisions');
const { notify } = require('./squad-decision-controller');

async function intake({ github, agent, context, core, env = process.env, fetchImpl }) {
  const { owner, repo } = context.repo;
  const number = context.payload.issue.number;
  const { data: issue } = await github.rest.issues.get({ owner, repo, issue_number: number });
  if (issue.pull_request || issue.state !== 'open') return;
  const comments = await github.paginate(github.rest.issues.listComments, { owner, repo, issue_number: number, per_page: 100 });
  const event = context.payload.comment;
  const live = comments.find(item => item.id === event?.id);
  if (!live || live.body !== event.body || live.created_at !== live.updated_at) return;
  const triage = live.user?.login === owner && live.user?.type === 'User'
    && live.body.trim().match(/^\/triage(?: (p[012]))?$/);
  if (triage) {
    const marker = `<!-- squad-intake:${live.id} -->`;
    if (comments.some(comment => comment.user?.login === 'github-actions[bot]' && comment.body?.startsWith(marker))) return;
    const priority = `priority:${triage[1] || 'p2'}`;
    for (const label of ['priority:p0', 'priority:p1', 'priority:p2']) {
      try {
        await github.rest.issues.getLabel({ owner, repo, name: label });
      } catch (error) {
        if (error.status !== 404) throw error;
        await github.rest.issues.createLabel({ owner, repo, name: label, color: 'FBCA04',
          description: 'Issue priority only; never implementation approval' });
      }
    }
    for (const label of issue.labels.filter(label => /^priority:p[012]$/.test(label.name) && label.name !== priority)) {
      await github.rest.issues.removeLabel({ owner, repo, issue_number: number, name: label.name });
    }
    await github.rest.issues.addLabels({ owner, repo, issue_number: number, labels: [priority] });
    await github.rest.issues.createComment({ owner, repo, issue_number: number,
      body: `${marker}\nIntake requested by @${owner}. Set ${priority}; this grants no implementation permission. Architect analysis comes from the planning workflow, not keyword routing.` });
    const selected = latestPlan(comments, owner);
    if (!selected.ok && !selected.waiting) {
      await agent.rest.issues.createComment({ owner, repo, issue_number: number, body: '/plan' });
    }
    core.info('Owner-enrolled issue triaged; implementation remains gated by plan approval.');
    return;
  }
  const selected = latestPlan(comments, owner);
  const planReady = selected.ok && selected.plan.comment.id === live.id;
  const questions = live.user?.login === 'github-actions[bot]' && live.body.startsWith('## Planning questions');
  if (!planReady && !questions) return;
  await notify({ github, owner, repo, number, key: `planning-${live.id}-${digest(live.body)}`,
    payload: { issue: number, card: {
      type: 'AdaptiveCard', version: '1.4',
      body: [{ type: 'TextBlock', text: planReady ? 'Squad plan ready for approval' : 'Squad needs a planning decision', wrap: true }],
      actions: [{ type: 'Action.OpenUrl', title: 'Review in GitHub', url: live.html_url }],
    } }, env, fetchImpl });
}

module.exports = { intake };
