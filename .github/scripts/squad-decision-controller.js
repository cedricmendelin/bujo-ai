'use strict';

const {
  BOT, card, command, loadState, requestCandidate, requestComment, requestRecord,
  requestRecordBody, teamsDecision,
} = require('./squad-decisions');

async function preserveRequests({ github, owner, repo, number, comments, event, action, previousBody }) {
  const archived = new Set(comments.map(comment => requestRecord(comment, owner)).filter(Boolean).map(item => item.id));
  const candidates = new Map();
  const add = (comment, invalid) => {
    if (!requestCandidate(comment, owner) || archived.has(comment.id)) return;
    const body = requestRecordBody(comment, owner, invalid);
    if (!body) return;
    const id = Number(body.match(/^<!-- squad-request-record:([1-9]\d*):/)[1]);
    if (!candidates.has(id)) candidates.set(id, body);
  };
  const eventRecord = action === 'deleted' && event ? requestRecord(event, owner) : null;
  const previousRecord = previousBody && event ? requestRecord({ ...event, body: previousBody }, owner) : null;
  if (eventRecord) add(eventRecord.archivedSource, eventRecord.invalid);
  if (previousRecord) {
    add(previousRecord.archivedSource, 'The audit record was edited; its original request remains blocked.');
  }
  if (event && action === 'created') add(event);
  if (event && previousBody?.startsWith('## Scope change request\n')) {
    add({ ...event, body: previousBody }, 'The source comment was edited before its original request could be archived.');
  }
  if (event && action === 'deleted') add(event);
  for (const comment of comments) add(comment);
  for (const body of candidates.values()) {
    await github.rest.issues.createComment({ owner, repo, issue_number: number, body });
  }
}

async function failClosedArchive({ github, agent, owner, repo, number, pr, event, defaultBranch, error }) {
  const failures = [];
  const attempt = async (name, operation) => {
    try { await operation(); } catch (failure) { failures.push({ name, error: failure }); }
  };
  await attempt('disable auto-merge', async () => {
    if (pr.auto_merge) {
      await agent.graphql('mutation($id:ID!){disablePullRequestAutoMerge(input:{pullRequestId:$id}){clientMutationId}}', {
        id: pr.node_id,
      });
    }
  });
  await attempt('apply needs:human', () => agent.rest.issues.addLabels({
    owner, repo, issue_number: number, labels: ['needs:human'],
  }));
  const marker = `<!-- squad-request-archive-failure:${number}:${event.id} -->`;
  await attempt('write recovery audit', async () => {
    const history = await github.paginate(github.rest.issues.listComments, {
      owner, repo, issue_number: number, per_page: 100,
    });
    if (!history.some(comment => comment.user?.login === BOT && comment.body?.startsWith(marker))) {
      await agent.rest.issues.createComment({ owner, repo, issue_number: number,
        body: `${marker}\n@${owner} The result of preserving the deleted or edited scope request could not be confirmed, so no archive success or approval is inferred. Auto-merge was paused and this PR requires human attention. Do not remove \`needs:human\` or merge until the source request is archived and the blocked state is verified. Rerun this failed workflow after GitHub API access recovers; the archive and lifecycle reconciliation must both succeed.` });
    }
  });
  await attempt('dispatch lifecycle reconciliation', () => agent.rest.actions.createWorkflowDispatch({
    owner, repo, workflow_id: 'squad-pr-lifecycle.yml',
    ref: defaultBranch,
    inputs: { pr_number: String(number) },
  }));
  if (failures.length) {
    throw new AggregateError([error, ...failures.map(failure => failure.error)],
      `Could not archive the changed scope request; recovery failed in: ${failures.map(failure => failure.name).join(', ')}.`);
  }
}

async function notify({ github, owner, repo, number, key, payload, env, fetchImpl = fetch }) {
  if (env.SQUAD_TEAMS_NOTIFICATIONS !== 'true') return;
  const url = env.SQUAD_TEAMS_WEBHOOK_URL;
  let validUrl = false;
  try { validUrl = !!url && new URL(url).protocol === 'https:'; } catch { /* Never print a malformed secret URL. */ }
  if (!validUrl) throw new Error('Teams delivery is enabled but its HTTPS webhook secret is missing or invalid.');
  const history = await github.paginate(github.rest.issues.listComments, { owner, repo, issue_number: number, per_page: 100 });
  const prefix = `<!-- squad-notify:${key}:`;
  const attempts = history.filter(comment => comment.user?.login === BOT && comment.body?.startsWith(prefix));
  if (attempts.some(comment => comment.body.startsWith(`${prefix}sent -->`))) return;
  if (attempts.filter(comment => comment.body.startsWith(`${prefix}attempt -->`)).length >= 3) {
    throw new Error('Teams delivery exhausted three attempts. GitHub remains actionable; inspect the connection before retrying with a new request.');
  }
  await github.rest.issues.createComment({ owner, repo, issue_number: number,
    body: `${prefix}attempt -->\nAttempting Teams delivery. GitHub approval remains available.` });
  try {
    const response = await fetchImpl(url, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload), signal: AbortSignal.timeout(15000), redirect: 'error',
    });
    if (!response.ok) throw new Error('Teams endpoint returned a non-success status.');
  } catch {
    // Transport errors can contain the secret-bearing webhook URL.
    await github.rest.issues.createComment({ owner, repo, issue_number: number,
      body: `<!-- squad-notify-failed:${key} -->\n@${owner} Teams delivery could not be confirmed. Use GitHub to act; inspect the notification connection. Delivery never authorizes or resumes work.` });
    throw new Error('Teams delivery failed or was uncertain. Inspect the flow before retrying; no decision was recorded.');
  }
  await github.rest.issues.createComment({ owner, repo, issue_number: number,
    body: `${prefix}sent -->\nTeams endpoint accepted the notification. End-to-end delivery and approval require separate verification.` });
}

async function handleDecision({ github, agent, context, core, env = process.env, fetchImpl }) {
  const { owner, repo } = context.repo;
  const dispatch = context.eventName === 'workflow_dispatch';
  const input = context.payload.inputs || {};
  const number = Number(dispatch ? input.pr_number : context.payload.issue?.number);
  if (!Number.isSafeInteger(number) || number < 1) throw new Error('A valid PR number is required.');
  const { data: pr } = await github.rest.pulls.get({ owner, repo, pull_number: number });
  if (pr.state !== 'open' || pr.head.repo?.full_name !== `${owner}/${repo}`) throw new Error('Decision requests require an open same-repository PR.');
  const read = () => github.paginate(github.rest.issues.listComments, { owner, repo, issue_number: number, per_page: 100 });
  const recordReference = async request => {
    const marker = `<!-- squad-amendment-reference:${number}:${request.id}:${request.receipt} -->`;
    const history = await github.paginate(github.rest.issues.listComments, {
      owner, repo, issue_number: request.data.issue, per_page: 100,
    });
    if (history.some(comment => comment.user?.login === BOT && comment.body?.startsWith(marker))) return false;
    await github.rest.issues.createComment({ owner, repo, issue_number: request.data.issue,
      body: `${marker}\nApproved scope amendment: PR #${number}, request ${request.id} (${pr.html_url}#issuecomment-${request.id}). Baseline plan comment: ${request.data.plan}. Read the immutable request and decision on that PR; original criteria/history are preserved.` });
    return true;
  };
  let comments = await read();
  const event = context.payload.comment;
  const previousBody = context.payload.changes?.body?.from;
  if (!dispatch) {
    try {
      await preserveRequests({
        github, owner, repo, number, comments, event,
        action: context.payload.action, previousBody,
      });
    } catch (error) {
      const destructiveRequestChange = context.payload.action === 'deleted'
        && (requestComment(event || {}, owner) || requestRecord(event || {}, owner))
        || context.payload.action === 'edited' && previousBody?.startsWith('## Scope change request\n');
      if (destructiveRequestChange) {
        try {
          await failClosedArchive({
            github, agent, owner, repo, number, pr, event,
            defaultBranch: context.payload.repository?.default_branch, error,
          });
        } catch (recoveryError) {
          throw recoveryError;
        }
      }
      throw error;
    }
    comments = await read();
  }
  let state = await loadState({ github, owner, repo, pr, comments });
  if (state.requests.some(item => item.status === 'unreadable') && pr.auto_merge) {
    await agent.graphql('mutation($id:ID!){disablePullRequestAutoMerge(input:{pullRequestId:$id}){clientMutationId}}', {
      id: pr.node_id,
    });
  }
  let choice;
  if (dispatch) {
    choice = teamsDecision({
      actor: context.actor, expectedActor: env.SQUAD_TEAMS_APPROVAL_ACTOR,
      responder: input.responder, expectedResponder: env.SQUAD_TEAMS_APPROVER_ID,
      enabled: env.SQUAD_TEAMS_APPROVALS, ...input,
    });
  } else {
    const live = comments.find(comment => comment.id === event?.id);
    if (context.payload.action === 'created' && live?.body === event.body) choice = command(live, owner);
    if (!choice && !requestComment(event || {}, owner)
        && !requestComment({ ...event, body: previousBody }, owner)
        && !requestRecord(event || {}, owner)
        && !requestRecord({ ...event, body: previousBody }, owner)
        && !comments.some(comment => requestRecord(comment, owner)?.id === event?.id)
        && !(event?.user?.login === BOT && event.user?.type === 'Bot'
          && [event.body, previousBody].some(body => body?.startsWith('<!-- squad-decision:')))) return;
  }
  if (choice?.decision === 'approve'
      && state.requests.find(item => item.id === choice.request)?.status === 'unreadable') {
    core.info(`Request ${choice.request} was not approved because its linked issue baseline could not be read.`);
    choice = null;
  }
  if (choice) {
    const request = state.requests.find(item => item.id === choice.request);
    if (!request) throw new Error('Request is missing or not authored by an eligible participant.');
    if (choice.key && choice.key !== request.key) throw new Error('The Teams card is stale.');
    if (['approved', 'rejected'].includes(request.status)) {
      core.info(`Request ${request.id} was already decided; no resume or decision repeated.`);
      if (request.status !== 'approved' || !await recordReference(request)) return;
    } else {
      if (choice.decision === 'approve' && request.status !== 'pending') throw new Error('Only a current, unexpired request can be approved. Reject it and publish a new request.');
      if (choice.comment && Date.parse(choice.comment.created_at) < Date.parse(request.created)) {
        throw new Error('Decision command predates the request.');
      }
      // Re-read the immutable request and baseline immediately before recording authority.
      comments = await read();
      const { data: livePr } = await github.rest.pulls.get({ owner, repo, pull_number: number });
      if (livePr.state !== 'open') throw new Error('PR closed during decision admission.');
      const current = (await loadState({ github, owner, repo, pr: livePr, comments })).requests.find(item => item.id === request.id);
      if (!current || current.key !== request.key || current.status !== request.status) throw new Error('Request changed during decision admission.');
      const { data: recorded } = await github.rest.issues.createComment({ owner, repo, issue_number: number,
        body: `<!-- squad-decision:${request.id}:${request.key}:${choice.decision} -->\n@${owner} ${choice.decision === 'approve' ? 'approved' : 'rejected'} [request ${request.id}](${pr.html_url}#issuecomment-${request.id}) through ${dispatch ? 'the configured authenticated Teams flow' : 'GitHub'}.\n${choice.reason || ''}\nNo unrelated human pause, repair budget, CI or review requirement was cleared.` });
      if (choice.decision === 'approve') {
        await recordReference({ ...request, receipt: recorded.id });
      }
    }
  }
  comments = await read();
  state = await loadState({ github, owner, repo, pr, comments });
  if (state.blocked) {
    if (pr.auto_merge) await agent.graphql('mutation($id:ID!){disablePullRequestAutoMerge(input:{pullRequestId:$id}){clientMutationId}}', { id: pr.node_id });
    for (const request of state.requests.filter(item => ['invalid', 'expired', 'stale', 'unreadable'].includes(item.status))) {
      const marker = `<!-- squad-decision-recovery:${request.id}:${request.key}:${request.status} -->`;
      if (!comments.some(comment => comment.user?.login === BOT && comment.body?.startsWith(marker))) {
        const recovery = request.status === 'unreadable'
          ? 'The linked issue baseline could not be verified; restore API access or confirm the issue reference, then re-evaluate.'
          : `Reject the preserved obsolete record with \`/reject-change ${request.id}\`, then post a new immutable request against the current linked approved baseline if still needed.`;
        await github.rest.issues.createComment({ owner, repo, issue_number: number,
          body: `${marker}\n@${owner} Request ${request.id} is ${request.status}; it grants no scope and blocks merge. ${recovery} Preserve original acceptance history and remove any unauthorized changes before proceeding.` });
      }
    }
    for (const request of state.requests.filter(item => item.status === 'pending')) {
      const marker = `<!-- squad-decision-needed:${request.id} -->`;
      if (!comments.some(comment => comment.user?.login === BOT && comment.body?.startsWith(marker))) {
        await github.rest.issues.createComment({ owner, repo, issue_number: number,
          body: `${marker}\n@${owner} Approve with \`/approve-change ${request.id}\` or reject with \`/reject-change ${request.id}\`. Work on this change and merge are blocked until a decision. A label does not approve it. Do not start a competing agent.` });
      }
      const approvalCard = card({ request, title: 'Squad needs your scope decision' });
      if (env.SQUAD_TEAMS_APPROVALS !== 'true') approvalCard.actions = approvalCard.actions.filter(action => action.type === 'Action.OpenUrl');
      await notify({ github, owner, repo, number, key: `request-${request.id}-${request.key}`,
        payload: { pr: number, card: approvalCard }, env, fetchImpl });
    }
  }
  await agent.rest.actions.createWorkflowDispatch({
    owner, repo, workflow_id: 'squad-pr-lifecycle.yml', ref: context.payload.repository.default_branch,
    inputs: { pr_number: String(number) },
  });
  core.info(`Decisions reconciled: ${state.requests.length} request(s); blocked=${state.blocked}.`);
}

async function resumeAmendments({ github, agent, owner, repo, pr, comments, state, session }) {
  if (state.blocked || pr.labels.some(label => ['needs:human', 'handoff:local'].includes(label.name))
      || !pr.head.ref.startsWith('copilot/') || session.state !== 'ok') return false;
  const pending = state.amendments.filter(request => {
    const receipt = comments.find(comment => comment.id === request.receipt);
    return !session.run?.created_at || Date.parse(session.run.created_at) <= Date.parse(receipt?.created_at);
  });
  if (!pending.length) return false;
  const admitted = [];
  for (const request of pending) {
    const marker = `<!-- squad-amendment-resume:${request.id}:${request.receipt} -->`;
    if (comments.some(comment => [owner, BOT].includes(comment.user?.login) && comment.body?.startsWith(marker))) continue;
    // Claim before requesting a session. An uncertain API result never causes blind re-dispatch.
    await github.rest.issues.createComment({ owner, repo, issue_number: pr.number,
      body: `${marker}\nAdmission recorded for approved amendment ${request.id}. If delivery fails, inspect the PR/session before an owner requests continuation.` });
    admitted.push(request.id);
  }
  if (admitted.length) await agent.rest.issues.createComment({ owner, repo, issue_number: pr.number,
    body: `@copilot Continue this PR under the approved baseline plus explicit amendment request(s) ${admitted.join(', ')} in this PR's comments. Read the exact accepted changes and decisions as scope data. Implement only authorized work, update acceptance evidence/docs, and report findings. Do not change unrelated scope, merge, or remove human pauses.` });
  return true;
}

async function notifyBlocker({ github, owner, repo, number, env = process.env, fetchImpl }) {
  if (env.SQUAD_TEAMS_NOTIFICATIONS !== 'true') return;
  const { data: pr } = await github.rest.pulls.get({ owner, repo, pull_number: number });
  if (pr.state !== 'open' || !pr.labels.some(label => label.name === 'needs:human')) return;
  const comments = await github.paginate(github.rest.issues.listComments, { owner, repo, issue_number: number, per_page: 100 });
  const blocker = comments.filter(comment => [owner, BOT].includes(comment.user?.login)
    && !comment.body?.startsWith('<!-- squad-notify')
    && (comment.body?.includes(`@${owner}`) || comment.body?.includes('Needs human'))).at(-1);
  if (!blocker) return;
  await notify({ github, owner, repo, number, key: `blocker-${blocker.id}`,
    payload: { pr: number, card: {
      type: 'AdaptiveCard', version: '1.4',
      body: [{ type: 'TextBlock', text: 'Squad needs your help: an owner-only blocker is recorded in GitHub.', wrap: true }],
      actions: [{ type: 'Action.OpenUrl', title: 'Inspect blocker in GitHub', url: blocker.html_url || pr.html_url }],
    } }, env, fetchImpl });
}

module.exports = { handleDecision, notify, notifyBlocker, preserveRequests, resumeAmendments };
