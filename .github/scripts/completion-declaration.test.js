'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const {
  acceptanceTracking, evaluateCompletion, hasPartialDeclaration, repairGeneratedClosingReferences,
  partialHandoff, LEGACY_PR_CUTOFF,
} = require('./completion-declaration');

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

test('repairs only generated standalone closing links for an unambiguous partial delivery', () => {
  const body = 'Completion: partial\r\n## Remaining work\r\nOwner: AI; live client proof after access.\r\n'
    + '<!-- START COPILOT CODING AGENT SUFFIX -->\r\n\r\n- Fixes #11\r\n'
    + '- Resolves cedricmendelin/bujo-ai#12\r\n<!-- END COPILOT CODING AGENT SUFFIX -->';
  const repaired = repairGeneratedClosingReferences(body, current);
  assert.equal(repaired, body.replace('- Fixes #11', '- Refs #11')
    .replace('- Resolves cedricmendelin/bujo-ai#12', '- Refs cedricmendelin/bujo-ai#12'));
  assert.equal(evaluateCompletion(repaired, current).ok, true);
  assert.equal(repairGeneratedClosingReferences(repaired, current), repaired);
  for (const unsafe of [
    body.replace('Completion: partial', 'Completion: full'),
    body.replace('## Remaining work', '## Notes'),
    body.replace('Completion: partial', 'Completion: partial\nCompletion: full'),
    body.replace('- Fixes #11', '- Fixes #11 and unrelated prose'),
    body.replace('## Remaining work', 'Fixes #13\n## Remaining work'),
    body.replace('- Fixes #11', '```text\n- Fixes #11\n```'),
    body.replace('- Fixes #11', '<!--\n- Fixes #11\n-->'),
    body.replace('- Fixes #11', '    - Fixes #11'),
    body.replace('<!-- START COPILOT CODING AGENT SUFFIX -->', '<!-- END COPILOT CODING AGENT SUFFIX -->\n<!-- START COPILOT CODING AGENT SUFFIX -->'),
    body.replace('<!-- START COPILOT CODING AGENT SUFFIX -->', ''),
  ]) assert.equal(repairGeneratedClosingReferences(unsafe, current), unsafe);
});

test('partial handoff extracts one same-repository issue and stops at the next section', () => {
  const body = 'Completion: partial\nRefs #11\n## Remaining work\nOwner: AI. Test clients after access.\n\n## Needs human\nApprove access.';
  assert.deepEqual(partialHandoff(body, 'cedricmendelin/bujo-ai'),
    { issue: 11, remaining: 'Owner: AI. Test clients after access.' });
  assert.equal(partialHandoff(body.replace('Refs #11', 'Refs other/repo#11'), 'cedricmendelin/bujo-ai'), null);
  assert.equal(partialHandoff(body.replace('Refs #11', 'Refs #11\nPart of other/repo#12'), 'cedricmendelin/bujo-ai'), null);
  assert.equal(partialHandoff(body.replace('Refs #11', 'Refs #11\nRefs #12'), 'cedricmendelin/bujo-ai'), null);
  assert.equal(partialHandoff(body.replace('partial', 'full'), 'cedricmendelin/bujo-ai'), null);
});

test('acceptance tracking requires one canonical checklist with evidence and actionable unchecked rows', () => {
  const body = [
    '## Acceptance tracking',
    '',
    '- [x] Local sign-in works — evidence: PR #125 and sign-in test.',
    '- [ ] Browser installation — blocked on target selection; owner: Verifier; next: record evidence on agreed browsers.',
    '- [ ] Journal capture — open; owner: Builder; next: deliver and test the flow.',
    '',
    '## Notes',
    '- [ ] Not a tracked criterion',
  ].join('\n');
  const result = acceptanceTracking(body);
  assert.equal(result.ok, true);
  assert.equal(result.criteria.length, 3);
  assert.equal(result.incomplete.length, 2);
  assert.equal(acceptanceTracking(body.replace('- [x] Local sign-in works — evidence: PR #125 and sign-in test.', '- [x] Local sign-in works')).ok, false);
  assert.equal(acceptanceTracking(body.replace('blocked on target selection; owner: Verifier; next:', 'waiting; next:')).ok, false);
  assert.equal(acceptanceTracking(body.replace('owner: Verifier', 'owner:;')).ok, false);
  assert.equal(acceptanceTracking(body.replace('## Acceptance tracking', '## Checklist')).ok, false);
  const duplicate = acceptanceTracking(`${body}\n## Acceptance tracking\n- [ ] duplicate; open; owner: Builder; next: resolve`);
  assert.equal(duplicate.ok, false);
  assert.equal(duplicate.incomplete.length, 3, 'closure detection must inspect every duplicate checklist section');
  assert.equal(acceptanceTracking(body.replace(/\n- \[ \].*$/gm, '')).incomplete.length, 0);
});

test('partial declaration and issue reference parsing fail closed for absent, duplicate, or foreign linkage', () => {
  const body = 'Completion: partial\nRefs #11\n## Remaining work\nOwner: AI. Test clients after access.';
  assert.equal(hasPartialDeclaration(body), true);
  assert.equal(hasPartialDeclaration(body.replace('partial', 'full')), false);
  assert.equal(hasPartialDeclaration(`${body}\nCompletion: partial`), false);
  assert.equal(partialHandoff(body.replace('Refs #11', 'Relates to other/repo#12\nRefs #11'), 'cedricmendelin/bujo-ai'), null);
  assert.equal(partialHandoff(body.replace('Refs #11', 'Notes only'), 'cedricmendelin/bujo-ai'), null);
  assert.equal(partialHandoff('Completion: partial\nRefs #11\n## Remaining work\n```\nnot an actual handoff\n```',
    'cedricmendelin/bujo-ai'), null);
});
