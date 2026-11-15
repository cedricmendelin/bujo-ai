'use strict';

const COPILOT_ASSIGNEE = 'copilot-swe-agent[bot]';

async function assignCopilot({ github, owner, repo, issueNumber, agentAssignment }) {
  const { data: issue } = await github.rest.issues.get({ owner, repo, issue_number: issueNumber });
  if (issue.assignees.some(assignee => assignee.login === COPILOT_ASSIGNEE)) {
    await github.rest.issues.removeAssignees({
      owner,
      repo,
      issue_number: issueNumber,
      assignees: [COPILOT_ASSIGNEE],
    });
  }

  return github.request('POST /repos/{owner}/{repo}/issues/{issue_number}/assignees', {
    owner,
    repo,
    issue_number: issueNumber,
    assignees: [COPILOT_ASSIGNEE],
    agent_assignment: agentAssignment,
  });
}

module.exports = { assignCopilot, COPILOT_ASSIGNEE };
