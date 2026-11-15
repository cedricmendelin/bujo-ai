'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { assignCopilot, COPILOT_ASSIGNEE } = require('./assign-copilot');

function createGithub(assignees) {
  const calls = [];
  return {
    calls,
    api: {
      rest: {
        issues: {
          get: async params => {
            calls.push(['get', params]);
            return { data: { assignees } };
          },
          removeAssignees: async params => calls.push(['remove', params]),
        },
      },
      request: async (route, params) => calls.push(['assign', route, params]),
    },
  };
}

test('removes an existing Copilot assignee before submitting the new plan assignment', async () => {
  const github = createGithub([{ login: COPILOT_ASSIGNEE }]);
  const agentAssignment = { custom_agent: 'builder', custom_instructions: 'Plan v2' };

  await assignCopilot({
    github: github.api,
    owner: 'owner',
    repo: 'repo',
    issueNumber: 3,
    agentAssignment,
  });

  assert.deepEqual(github.calls.map(call => call[0]), ['get', 'remove', 'assign']);
  assert.deepEqual(github.calls[1][1].assignees, [COPILOT_ASSIGNEE]);
  assert.equal(github.calls[2][2].agent_assignment, agentAssignment);
});

test('does not remove an assignee when Copilot is not currently assigned', async () => {
  const github = createGithub([{ login: 'other-user' }]);

  await assignCopilot({
    github: github.api,
    owner: 'owner',
    repo: 'repo',
    issueNumber: 3,
    agentAssignment: { custom_agent: 'builder' },
  });

  assert.deepEqual(github.calls.map(call => call[0]), ['get', 'assign']);
});

test('does not submit an assignment if removal fails', async () => {
  const github = createGithub([{ login: COPILOT_ASSIGNEE }]);
  github.api.rest.issues.removeAssignees = async () => {
    throw new Error('removal failed');
  };

  await assert.rejects(assignCopilot({
    github: github.api,
    owner: 'owner',
    repo: 'repo',
    issueNumber: 3,
    agentAssignment: { custom_agent: 'builder' },
  }), /removal failed/);
  assert.deepEqual(github.calls.map(call => call[0]), ['get']);
});
