'use strict';

const LEGACY_PR_CUTOFF = '2026-11-15T12:58:06Z';

function stripFencedCode(text) {
  const lines = text.split(/\r?\n/);
  const result = [];
  let fence = null;

  for (const line of lines) {
    if (fence) {
      const closing = line.match(/^ {0,3}(`+|~+)[ \t]*$/);
      if (closing && closing[1][0] === fence.character && closing[1].length >= fence.length) {
        fence = null;
      }
      continue;
    }

    const opening = line.match(/^ {0,3}(`{3,}|~{3,})(.*)$/);
    if (opening && !(opening[1][0] === '`' && opening[2].includes('`'))) {
      fence = { character: opening[1][0], length: opening[1].length };
      continue;
    }

    result.push(line);
  }

  return result.join('\n');
}

function stripInlineCode(text) {
  let result = '';
  let cursor = 0;

  while (cursor < text.length) {
    const opening = text.indexOf('`', cursor);
    if (opening < 0) break;

    result += text.slice(cursor, opening);
    let openingEnd = opening;
    while (text[openingEnd] === '`') openingEnd++;
    const delimiter = text.slice(opening, openingEnd);

    let closing = text.indexOf(delimiter, openingEnd);
    while (closing >= 0
      && (text[closing - 1] === '`' || text[closing + delimiter.length] === '`')) {
      closing = text.indexOf(delimiter, closing + delimiter.length);
    }

    if (closing < 0) {
      result += delimiter;
      cursor = openingEnd;
      continue;
    }

    cursor = closing + delimiter.length;
  }

  return result + text.slice(cursor);
}

function removeNonProse(body) {
  const withoutComments = stripFencedCode(body)
    .replace(/<!--[\s\S]*?(?:-->|$)/g, '');
  return stripInlineCode(withoutComments);
}

function evaluateCompletion(body, options = {}) {
  try {
    if (typeof body !== 'string') {
      return { ok: false, findings: ['PR description is missing or is not text; declare completion and describe the delivery.'] };
    }

    const { createdAt, legacyCutoff = LEGACY_PR_CUTOFF } = options;
    const created = Date.parse(createdAt);
    const cutoff = Date.parse(legacyCutoff);
    if (!Number.isFinite(created) || !Number.isFinite(cutoff)) {
      return { ok: false, findings: ['Could not determine whether this PR predates the completion rule; fail-closed check requires a valid PR creation date and cutoff.'] };
    }

    const prose = removeNonProse(body);
    const deliveryProse = prose.split('\n')
      .filter(line => !/^\s{0,3}>/.test(line))
      .join('\n');
    const declarations = [...prose.matchAll(/^\s*Completion\s*:\s*(full|partial)\s*$/gim)]
      .map(match => match[1].toLowerCase());
    if (declarations.length > 1) {
      return { ok: false, findings: ['Use exactly one whole-line declaration: `Completion: full` or `Completion: partial`.'] };
    }

    const completion = declarations[0];
    const hasClosingReference = /\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\s+(?:https?:\/\/github\.com\/[a-z0-9_.-]+\/[a-z0-9_.-]+\/issues\/\d+|[a-z0-9_.-]+\/[a-z0-9_.-]+#\d+|#\d+)\b/i.test(prose);
    const hasRemainingWork = /^\s*#{0,6}\s*remaining\s+work\b/im.test(deliveryProse);
    const legacyPartialWording = /\b(?:partial(?:ly)?|remaining\s+(?:work|implementation)|follow[\s-]?up|not\s+implemented)\b/i.test(deliveryProse);

    if (completion === 'partial') {
      const findings = [];
      if (!hasRemainingWork) {
        findings.push('`Completion: partial` requires a "Remaining work" section or line stating the unfinished work and its dependency.');
      }
      if (hasClosingReference) {
        findings.push('A partial PR must not close its issue. Replace closing references with `Relates to #N` or `Part of #N`, and remove closing keywords from the entire description, including generated suffixes.');
      }
      return { ok: findings.length === 0, findings };
    }

    if (completion === 'full') return { ok: true, findings: [] };
    if (!hasClosingReference) return { ok: true, findings: [] };

    if (created < cutoff) {
      if (legacyPartialWording) {
        return {
          ok: false,
          findings: ['This pre-rule PR describes partial or unfinished work but contains a closing reference. Remove the closing keyword, use a non-closing issue reference, and preserve the remaining work.'],
        };
      }
      return { ok: true, findings: [] };
    }

    return {
      ok: false,
      findings: ['Add a whole-line `Completion: full` or `Completion: partial` declaration. Full completion still requires evidence for every acceptance criterion; partial delivery must state remaining work and its dependency, use non-closing issue linkage, and remove closing keywords everywhere in the description.'],
    };
  } catch {
    return { ok: false, findings: ['The completion declaration could not be evaluated; correct the PR description and re-run a check.'] };
  }
}

function repairGeneratedClosingReferences(body, options) {
  if (typeof body !== 'string') return body;
  const prose = removeNonProse(body);
  const declarations = [...prose.matchAll(/^\s*Completion\s*:\s*(full|partial)\s*$/gim)];
  if (declarations.length !== 1 || declarations[0][1].toLowerCase() !== 'partial') return body;
  // Only the known generated suffix is mutable; prose and examples are not.
  const start = '<!-- START COPILOT CODING AGENT SUFFIX -->';
  const end = '<!-- END COPILOT CODING AGENT SUFFIX -->';
  if (body.split(start).length !== 2 || body.split(end).length > 2) return body;
  const offset = body.indexOf(start) + start.length;
  if (body.includes(end) && body.indexOf(end) < offset) return body;
  const endOffset = body.indexOf(end, offset);
  const suffix = body.slice(offset, endOffset < 0 ? body.length : endOffset);
  if (/`|~~~|<!--/.test(suffix)) return body;
  const repaired = suffix.replace(
    /^( {0,3}-[ \t]+)(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)([ \t]+(?:#[1-9]\d*|[a-z0-9_.-]+\/[a-z0-9_.-]+#[1-9]\d*|https:\/\/github\.com\/[a-z0-9_.-]+\/[a-z0-9_.-]+\/issues\/[1-9]\d*))[ \t]*(?=\r?$)/gim,
    '$1Refs$2',
  );
  const candidate = body.slice(0, offset) + repaired + body.slice(offset + suffix.length);
  return candidate !== body && evaluateCompletion(candidate, options).ok ? candidate : body;
}

function partialHandoff(body, repository) {
  if (typeof body !== 'string') return null;
  const prose = removeNonProse(body);
  if (!/^\s*Completion:\s*partial\s*$/im.test(prose)) return null;
  const refs = [...prose.matchAll(/\b(?:Refs|Relates to|Part of)\s+(?:([a-z0-9_.-]+\/[a-z0-9_.-]+))?#([1-9]\d*)\b/gi)]
    .filter(match => !match[1] || match[1].toLowerCase() === repository.toLowerCase());
  const issues = [...new Set(refs.map(match => Number(match[2])))];
  if (issues.length !== 1 || !Number.isSafeInteger(issues[0])) return null;
  const lines = body.split(/\r?\n/);
  const start = lines.findIndex(line => /^#{1,6}\s+Remaining work\s*$/i.test(line));
  if (start < 0) return null;
  const level = lines[start].match(/^#+/)[0].length;
  let end = start + 1;
  while (end < lines.length && !new RegExp(`^#{1,${level}}\\s`).test(lines[end])
    && !lines[end].includes('<!-- START COPILOT CODING AGENT SUFFIX -->')) end++;
  const remaining = lines.slice(start + 1, end).join('\n').trim();
  return remaining ? { issue: issues[0], remaining } : null;
}

module.exports = { evaluateCompletion, repairGeneratedClosingReferences, partialHandoff, LEGACY_PR_CUTOFF };
