export const MAX_RECOVERIES = 2;

export function classifySession(runs) {
  if (!Array.isArray(runs) || runs.length === 0) return { state: 'none', outcome: 'none', run: null };

  const latest = runs.reduce((current, candidate) => {
    const currentCreated = Date.parse(current.created_at || '') || 0;
    const candidateCreated = Date.parse(candidate.created_at || '') || 0;
    if (candidateCreated !== currentCreated) return candidateCreated > currentCreated ? candidate : current;
    if ((candidate.run_number || 0) !== (current.run_number || 0)) {
      return (candidate.run_number || 0) > (current.run_number || 0) ? candidate : current;
    }
    return (candidate.id || 0) > (current.id || 0) ? candidate : current;
  });

  if (latest.status !== 'completed') return { state: 'active', outcome: latest.status, run: latest };
  const outcome = latest.conclusion || 'unknown';
  return { state: outcome === 'success' ? 'ok' : 'interrupted', outcome, run: latest };
}

export function recoveryAction({ state, recoveriesSoFar = 0, hasMarker = false, recoveryRun = false, canRecover = true }) {
  if (state === 'active' || state === 'none') return 'wait';
  if (state === 'ok') return 'continue';
  if (state !== 'interrupted') return 'escalate';
  if (hasMarker) return 'wait';
  if (!canRecover || recoveryRun || recoveriesSoFar >= MAX_RECOVERIES) return 'escalate';
  return 'recover';
}

export function gateEligibility({ isAgentPr, draft, sessionState, shepherdMarker }) {
  if (draft) return 'wait';
  if (!isAgentPr) return 'eligible';
  if (sessionState === 'interrupted') return 'interrupted';
  if (sessionState !== 'ok' || !shepherdMarker) return 'wait';
  return 'eligible';
}

export function approvalRequiredRuns(runs, { number, branch, sha }) {
  return (Array.isArray(runs) ? runs : [])
    .filter(run => run.conclusion === 'action_required')
    .map(run => {
      const related = run.head_branch === branch
        || run.head_sha === sha
        || (run.pull_requests || []).some(pr => pr.number === number);
      const repositoryLevel = run.actor?.login === 'Copilot'
        && ['workflow_run', 'issue_comment'].includes(run.event);
      return related || repositoryLevel
        ? {
          name: run.name || 'Unknown workflow',
          event: run.event || 'unknown',
          actor: run.actor?.login || 'unknown',
          url: run.html_url || '',
          related,
        }
        : null;
    })
    .filter(Boolean);
}

export function formatApprovalDiagnostic(runs, { ciPassing = false, sessionState }) {
  if (!runs.length) return '';

  const inline = value => String(value).replace(/[`\\\r\n]/g, ' ').slice(0, 160);
  const details = runs.slice(0, 15)
    .map(run => `- \`${inline(run.name)}\` · \`${inline(run.event)}\` · actor \`${inline(run.actor)}\`${run.related ? '' : ' · repository-level, not linked to this PR'}${run.url ? ` · ${run.url}` : ''}`)
    .join('\n');
  const more = runs.length > 15 ? `\n- …and ${runs.length - 15} more approval-blocked runs.` : '';
  let coverage;

  if (ciPassing) {
    coverage = 'The required `ci` check is passing for this head; these approval-blocked runs are not additional results.';
  } else if (sessionState === 'ok') {
    coverage = 'The shepherd uses owner-token `workflow_dispatch` runs for CI and Squad Review; check for a passing `ci` result.';
  } else {
    coverage = 'The shepherd waits for an active session to finish or an interrupted session to be recovered before dispatching CI/reviews. These runs are not results, and no approval is bypassed.';
  }

  return `ℹ️ GitHub marked these workflow runs \`action_required\` (approval-blocked, not failed):\n${details}${more}\n\n${coverage}`;
}

export function hasRunnableCiRun(runs) {
  return runs.some(run => run.conclusion !== 'action_required');
}
