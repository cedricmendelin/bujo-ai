'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { evaluateCompletion, LEGACY_PR_CUTOFF } = require('./completion-declaration');

const current = { createdAt: '2026-11-15T12:58:07Z' };
const legacy = { createdAt: '2026-11-15T12:49:18Z' };

test('blocks a partial delivery with a closing reference anywhere in the body', () => {
  const result = evaluateCompletion([
    'Completion: partial',
    '## Remaining work',
    'Implement domain code and tests after the technology decision.',
    '',
    'Copilot-generated suffix: Fixes cedricmendelin/bujo-ai#3',
  ].join('\n'), current);

  assert.equal(result.ok, false);
  assert.match(result.findings.join(' '), /partial PR must not close/i);
});

test('allows a linked partial docs milestone to leave the issue open', () => {
  const result = evaluateCompletion([
    'Completion: partial',
    'Relates to cedricmendelin/bujo-ai#3',
    '## Remaining work',
    'Implement the domain code and tests after the technology decision is approved.',
  ].join('\n'), current);

  assert.deepEqual(result, { ok: true, findings: [] });
});

test('allows full completion linkage for the independent Verifier to assess', () => {
  assert.deepEqual(
    evaluateCompletion('Completion: full\nCloses #3\nCode and relevant tests are included.', current),
    { ok: true, findings: [] },
  );
});

test('requires a completion declaration for new PRs with a closing reference', () => {
  const result = evaluateCompletion('Closes #3', current);
  assert.equal(result.ok, false);
  assert.match(result.findings.join(' '), /Completion: full.*Completion: partial/);
});

test('does not require a declaration when no issue is being closed', () => {
  assert.deepEqual(evaluateCompletion('Relates to #3', current), { ok: true, findings: [] });
});

test('requires remaining work for declared partial delivery', () => {
  const result = evaluateCompletion('Completion: partial\nRelates to #3', current);
  assert.equal(result.ok, false);
  assert.match(result.findings.join(' '), /Remaining work/i);
});

test('checks closing-reference variants and ignores code and comments', () => {
  const suffix = evaluateCompletion('cOmPlEtIoN: PARTIAL\n## Remaining work\nDone later.\nrEsOlVeS https://github.com/cedricmendelin/bujo-ai/issues/3', current);
  assert.equal(suffix.ok, false);

  const proseOnly = evaluateCompletion([
    'Completion: partial',
    '## Remaining work',
    'Use `Fixes #3` in the example.',
    '```text',
    'Closes cedricmendelin/bujo-ai#3',
    '```',
    '<!-- `Fixes #3` Resolves https://github.com/cedricmendelin/bujo-ai/issues/3 -->',
  ].join('\n'), current);
  assert.deepEqual(proseOnly, { ok: true, findings: [] });
});

test('legacy PRs are allowed unless closing text explicitly describes partial work', () => {
  assert.deepEqual(evaluateCompletion('Fixes #3', { ...legacy, legacyCutoff: LEGACY_PR_CUTOFF }), { ok: true, findings: [] });

  const result = evaluateCompletion([
    'PR changes only the specification.',
    'Domain code and tests were remaining work.',
    'Fixes cedricmendelin/bujo-ai#3',
  ].join('\n'), { ...legacy, legacyCutoff: LEGACY_PR_CUTOFF });
  assert.equal(result.ok, false);
  assert.match(result.findings.join(' '), /pre-rule PR describes partial/i);

  assert.deepEqual(evaluateCompletion([
    '> The original issue described domain code and tests as remaining work.',
    '> Fixes #3',
    'Fixes #33',
  ].join('\n'), { ...legacy, legacyCutoff: LEGACY_PR_CUTOFF }), { ok: true, findings: [] });
});

test('fails closed for malformed input and invalid cutoff dates', () => {
  assert.equal(evaluateCompletion(undefined, current).ok, false);
  assert.equal(evaluateCompletion('Fixes #3', { ...current, legacyCutoff: 'not-a-date' }).ok, false);

  const options = new Proxy({}, { get() { throw new Error('unexpected input'); } });
  assert.equal(evaluateCompletion('Fixes #3', options).ok, false);
});

test('an empty description does not close an issue', () => {
  assert.deepEqual(evaluateCompletion('', current), { ok: true, findings: [] });
});
