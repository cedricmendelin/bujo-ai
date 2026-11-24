'use strict';

async function admitRepairResume({ loadScope, onBlocked, resume }) {
  const scope = await loadScope();
  if (scope.blocked) {
    await onBlocked(scope);
    return false;
  }
  return (await resume()) !== false;
}

module.exports = { admitRepairResume };
