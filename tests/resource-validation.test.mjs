import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';

// Exercise the complete report/exit-code path with deterministic measurements,
// without launching VoltManager or requiring GPU hardware.
test('resource benchmark rejects regressions and short protocols', { skip: process.platform !== 'win32' }, () => {
  const root = mkdtempSync(join(tmpdir(), 'volt-validation-'));
  const harness = join(root, 'measure.ps1');
  writeFileSync(harness, `
# Accept the executable-style argument vector.
$values = @{}
for ($i = 0; $i -lt $args.Count - 1; $i += 2) { $values[$args[$i]] = $args[$i + 1] }
$mode = $values['--mode']; $output = $values['--output']
$renderer = $values['--renderer']; $scenario = $values['--scenario']
$cpu = 1.0; $draws = 100.0
if ($renderer -eq 'hardware') {
  $cpu = 0.8; $draws = 110.0
  if ($env:VOLT_TEST_CASE -eq 'cpu-regression') { $cpu = 2.0 }
  if ($env:VOLT_TEST_CASE -eq 'throughput-regression') { $draws = 80.0 }
  if ($env:VOLT_TEST_CASE -eq 'unstable') { $draws = 80 + 30 * [int]$values['--iteration'] }
}
if ($mode -eq 'app-benchmark' -and $values['--label'] -eq 'candidate' -and $env:VOLT_TEST_CASE -eq 'app-regression') { $cpu = 2.0 }
$run = @{
  Label=$values['--label']; Scenario=$scenario; Iteration=[int]$values['--iteration']; Renderer=$renderer
  Commit='test'; SupervisorCommit='test'; CpuAveragePercent=$cpu; CpuP95Percent=$cpu
  PrivateBytesAverage=104857600; PrivateBytesMax=104857600; WorkingSetAverage=104857600
  PrivateWorkingSetAverage=104857600; SampleCount=120; MaxProcessCount=4
  SettledSeconds=[int]$values['--settle-seconds']; MeasuredSeconds=[int]$values['--measure-seconds']
  SyntheticOperationsPerSecond=100; FreshDataLatencyMs=500; ProtectedWorkloadObserved=$true
  DrawsPerSecond=$draws; Status='passed'; VramStatus='measured'; VramBytesMax=33554432
  ProviderActivity=@{}; Machine='fixture'; Os='fixture'; Runtime='fixture'; WebViewRuntime='fixture'
}
$name = if ($mode -eq 'app-benchmark') { 'benchmark-run.json' } else { 'graphics-benchmark.json' }
$run | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $output $name)
$global:LASTEXITCODE = 0
`);
  const script = resolve('scripts/Run-ResourceValidation.ps1');
  try {
    for (const [scenario, short, exitCode, decision, status] of [
      ['ok', false, 0, 'use_hardware', 'passed'],
      ['cpu-regression', false, 0, 'keep_swiftshader', 'passed'],
      ['throughput-regression', false, 0, 'keep_swiftshader', 'passed'],
      ['app-regression', false, 1, 'keep_swiftshader', 'failed'],
      ['unstable', false, 2, 'keep_swiftshader', 'inconclusive'],
      ['ok', true, 2, 'keep_swiftshader', 'inconclusive'],
    ]) {
      const output = join(root, `${scenario}-${short}`);
      const result = spawnSync('pwsh', ['-NoProfile', '-File', script,
        '-BaselineApp', process.execPath, '-CandidateApp', process.execPath,
        '-Supervisor', process.execPath, '-Harness', harness, '-Output', output,
        '-StabilizationSeconds', short ? '1' : '30', '-MeasurementSeconds', short ? '1' : '120',
        '-Repetitions', short ? '1' : '5', '-UnstableExtraRepetitions', '0'],
        { encoding: 'utf8', timeout: 60000, env: { ...process.env, VOLT_TEST_CASE: scenario } });
      assert.equal(result.status, exitCode, `${scenario}: ${result.stderr}\n${result.stdout}`);
      const summary = JSON.parse(readFileSync(join(output, 'benchmark-summary.json'), 'utf8').replace(/^\uFEFF/, ''));
      assert.equal(summary.status, status, scenario);
      assert.equal(summary.renderer.decision, decision, scenario);
      if (short) assert.doesNotMatch(readFileSync(join(output, 'benchmark-summary.md'), 'utf8'), /\| PASS \|/);
    }
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
