'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { spawnSync } = require('node:child_process');
const path = require('node:path');
const fs = require('node:fs');

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

test('post-apply state refresh converges diagnostics before marker advancement', () => {
  const source = fs.readFileSync(script, 'utf8');
  const applied = source.indexOf('"Terraform infrastructure apply"');
  const refreshed = source.indexOf('"Terraform state refresh"');
  const planned = source.indexOf('"Terraform post-apply diagnostic plan"');
  const driftCheck = source.indexOf('diagnostic_setting_drift(refresh_plan');
  const marker = source.indexOf('marker.write_text(revision');

  assert.ok(applied >= 0);
  assert.ok(refreshed > applied);
  assert.ok(planned > refreshed);
  assert.ok(driftCheck > planned);
  assert.ok(marker > driftCheck);
});

test('diagnostic-setting drift predicate rejects updates but accepts no-op or unrelated changes', () => {
  const result = spawnSync(process.platform === 'win32' ? 'python' : 'python3', ['-c', `
import importlib.util
import json
spec = importlib.util.spec_from_file_location("apply_infrastructure", ${JSON.stringify(script)})
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

def plan(document):
    module.command = lambda *args, **kwargs: json.dumps(document)
    module.diagnostic_setting_drift("unused.tfplan", env={})

plan({"resource_changes": [
    {"type": "azurerm_monitor_diagnostic_setting", "change": {"actions": ["no-op"]}},
    {"type": "azurerm_consumption_budget_resource_group", "change": {"actions": ["update"]}},
]})

try:
    plan({"resource_changes": [
        {"type": "azurerm_monitor_diagnostic_setting", "change": {"actions": ["update"]}},
    ]})
except module.InfrastructureError:
    print("update rejected")
else:
    raise SystemExit("update was not rejected")
`], { encoding: 'utf8' });

  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /update rejected/);
});
