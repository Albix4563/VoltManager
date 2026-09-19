import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import {
  classifyResourceValidationProcess,
  persistResourceValidationFailure,
  selectResourceValidationShell,
} from './helpers/resource-validation-process.mjs';

test('selects Windows PowerShell 5.1 by default and honors an override', () => {
  assert.equal(selectResourceValidationShell('win32'), 'powershell.exe');
  assert.equal(selectResourceValidationShell('win32', 'pwsh-preview'), 'pwsh-preview');
  assert.equal(selectResourceValidationShell('linux'), 'pwsh');
});

test('classifies benchmark exits separately from CLR crashes and timeouts', () => {
  assert.deepEqual(
    classifyResourceValidationProcess({ status: 2, signal: null }, 2),
    { kind: 'benchmark', status: 2, signal: null, matchesExpectedExit: true },
  );
  assert.deepEqual(
    classifyResourceValidationProcess(
      { status: 3221225477, signal: null, stderr: 'Internal CLR error.' },
      2,
    ),
    { kind: 'crash', status: 3221225477, signal: null },
  );
  assert.deepEqual(
    classifyResourceValidationProcess(
      { status: null, signal: 'SIGTERM', error: { code: 'ETIMEDOUT' } },
      0,
    ),
    { kind: 'timeout', status: null, signal: 'SIGTERM' },
  );
  assert.deepEqual(
    classifyResourceValidationProcess({ status: 17, signal: null }, 0),
    { kind: 'support-process-error', status: 17, signal: null },
  );
});

test('persists process evidence and partial benchmark output', () => {
  const root = mkdtempSync(join(tmpdir(), 'volt-validation-diagnostics-'));
  try {
    const output = join(root, 'partial');
    mkdirSync(output);
    writeFileSync(join(output, 'benchmark-summary.json'), '{"partial":true}');

    const diagnosticDir = persistResourceValidationFailure({
      artifactRoot: join(root, 'artifacts'),
      scenario: 'unstable',
      short: false,
      expectedExitCode: 2,
      classification: { kind: 'crash', status: 3221225477, signal: null },
      result: {
        status: 3221225477,
        signal: null,
        stdout: 'captured stdout',
        stderr: 'Internal CLR error.',
        error: undefined,
      },
      shellInfo: {
        executable: 'pwsh',
        version: '7.6.6',
        runtime: '.NET 10.0.12',
        os: 'Microsoft Windows 10.0.19044',
      },
      output,
    });

    const metadata = JSON.parse(readFileSync(join(diagnosticDir, 'diagnostics.json'), 'utf8'));
    assert.equal(metadata.scenario, 'unstable');
    assert.equal(metadata.expectedExitCode, 2);
    assert.equal(metadata.actualExitCode, 3221225477);
    assert.equal(metadata.classification.kind, 'crash');
    assert.equal(metadata.shell.version, '7.6.6');
    assert.equal(readFileSync(join(diagnosticDir, 'stdout.log'), 'utf8'), 'captured stdout');
    assert.equal(readFileSync(join(diagnosticDir, 'stderr.log'), 'utf8'), 'Internal CLR error.');
    assert.equal(
      JSON.parse(readFileSync(join(diagnosticDir, 'output', 'benchmark-summary.json'), 'utf8')).partial,
      true,
    );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
