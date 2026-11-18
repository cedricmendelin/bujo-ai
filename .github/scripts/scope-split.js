'use strict';

const { removeNonProse, stripFencedCode } = require('./completion-declaration');

const values = (body, prefix) => stripFencedCode(body || '')
  .replace(/<!--[\s\S]*?(?:-->|$)/g, '').split('\n')
  .filter(line => line.startsWith(prefix)).map(line => line.slice(prefix.length).trim());
const exactly = (body, prefix, value) => {
  const found = values(body, prefix);
  return found.length === 1 && found[0] === value;
};

function readSplit(body) {
  const followUps = values(body, 'Scope split:');
  if (!followUps.length) return null;
  const invalid = { reason: 'A scope split requires Completion: full, one closing source issue, one Follow-up, a plan version, and unique Deferred records.' };
  const target = followUps.length === 1 && /^#([1-9]\d*)$/.exec(followUps[0]);
  const prose = removeNonProse(body || '');
  const references = [...prose.matchAll(/\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\s+(#\d+|[a-z0-9_.-]+\/[a-z0-9_.-]+#\d+|https?:\/\/github\.com\/[a-z0-9_.-]+\/[a-z0-9_.-]+\/issues\/\d+)/gi)];
  const sources = [...new Set(references.map(match => match[1]))];
  const source = sources.length === 1 && /^#([1-9]\d*)$/.exec(sources[0]);
  const version = values(body, 'Plan:').map(value => /^v([1-9]\d*)$/.exec(value));
  const records = values(body, 'Deferred:');
  const criteria = records.map(record => /^(.+?) \| reason: \S.+? \| owner: (?:architect|builder|ai|devops|verifier) \| next: \S.+$/.exec(record)?.[1]);
  const ids = [source?.[1], target?.[1], version[0]?.[1]].map(Number);
  if (!exactly(body, 'Completion:', 'full') || version.length !== 1
      || ids.some(id => !Number.isSafeInteger(id) || id < 1) || ids[0] === ids[1]
      || !records.length || criteria.some(criterion => !criterion) || new Set(criteria).size !== criteria.length) return invalid;
  return { source: ids[0], followUp: ids[1], version: ids[2], records };
}

function validateSplit(split, source, followUp, plan, pr) {
  if (![source, followUp].every(issue => issue?.state === 'open' && !issue.pull_request)
      || source.number !== split.source || followUp.number !== split.followUp) return 'Both scope-split references must be open issues in this repository.';
  if (!plan?.ok || plan.plan.version !== split.version || !source.labels?.some(label => label.name === 'plan:approved')) {
    return 'The scope split must reference the latest approved plan.';
  }
  const matches = body => JSON.stringify(values(body, 'Deferred:').sort()) === JSON.stringify([...split.records].sort());
  if (!exactly(source.body, 'Scope split:', `#${split.followUp}`)
      || !exactly(followUp.body, 'Split from:', `#${split.source}`)
      || ![source, followUp].every(issue => exactly(issue.body, 'Split PR:', `#${pr}`) && matches(issue.body))) {
    return 'Link both issues and this PR to one another and copy the exact Deferred records into both issues.';
  }
  return null;
}

function splitReviewPasses(role, check) {
  return !['Architect', 'Verifier'].includes(role)
    || /^Scope split: verified[ \t]*$/m.test(check.output?.summary || '');
}

module.exports = { readSplit, splitReviewPasses, validateSplit };
