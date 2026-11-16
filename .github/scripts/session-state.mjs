export function classifySession(runs) {
  if (!Array.isArray(runs) || runs.length === 0) return { state: 'none', outcome: 'none', run: null };
  const latest = [...runs].sort((a, b) => Date.parse(b.created_at || '') - Date.parse(a.created_at || '')
    || (b.run_number || 0) - (a.run_number || 0) || (b.id || 0) - (a.id || 0))[0];
  if (latest.status !== 'completed') return { state: 'active', outcome: latest.status, run: latest };
  const outcome = latest.conclusion || 'unknown';
  return { state: outcome === 'success' ? 'ok' : 'interrupted', outcome, run: latest };
}
