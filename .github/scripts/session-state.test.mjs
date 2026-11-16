import test from 'node:test';
import assert from 'node:assert/strict';
import { classifySession } from './session-state.mjs';

test('only the newest cloud workflow run determines whether implementation is ready', () => {
  const runs = [
    { id: 1, created_at: '2026-11-16T10:00:00Z', status: 'completed', conclusion: 'success' },
    { id: 2, created_at: '2026-11-16T11:00:00Z', status: 'in_progress' },
  ];
  assert.equal(classifySession(runs).state, 'active');
  assert.equal(classifySession([runs[0]]).state, 'ok');
  assert.equal(classifySession([{ ...runs[1], status: 'completed', conclusion: 'cancelled' }]).state, 'interrupted');
  assert.equal(classifySession([]).state, 'none');
});
