'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { spawnSync } = require('node:child_process');
const path = require('node:path');

const script = path.resolve(__dirname, 'apply_infrastructure.py');

test('deployment diagnostics redact list and case-varied values', () => {
  const result = spawnSync(process.platform === 'win32' ? 'python' : 'python3', ['-c', `
import importlib.util
import os
spec = importlib.util.spec_from_file_location("apply_infrastructure", ${JSON.stringify(script)})
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
env = {
    "TF_VAR_auth_allowed_principal_object_ids": "Aaaaaaaa-Bbbb-Cccc-Dddd-Eeeeeeeeeeee,ffffffff-1111-2222-3333-444444444444",
    "ARM_TENANT_ID": "Tenant-Id-Secret",
}
text = "Allowed aAAAAAAA-bBBB-cCCC-dDDD-eEEEEEEEEEEE and FFFFFFFF-1111-2222-3333-444444444444 tenant tenant-id-secret"
print(module.redact_output(text, env))
`], { encoding: 'utf8' });

  assert.equal(result.status, 0, result.stderr);
  assert.doesNotMatch(result.stdout, /aaaaaaaa-bbbb|tenant-id-secret|ffffffff-1111/i);
  assert.match(result.stdout, /\[REDACTED\]/);
});

test('non-visible commands return stdout without merged stderr', () => {
  const result = spawnSync(process.platform === 'win32' ? 'python' : 'python3', ['-c', `
import importlib.util
import subprocess
spec = importlib.util.spec_from_file_location("apply_infrastructure", ${JSON.stringify(script)})
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
subprocess.run = lambda *args, **kwargs: subprocess.CompletedProcess(args, 0, '{"ok":true}', "warning")
print(module.command("Azure CLI", ["unused"]))
`], { encoding: 'utf8' });

  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.stdout.trim(), '{"ok":true}');
  assert.doesNotMatch(result.stdout, /warning/);
});
