import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { resolve } from 'node:path';

const repoRoot = resolve(import.meta.dirname, '..');
const releaseSetup = resolve(repoRoot, 'src/VoltManager.Setup/bin/Release/net48/VoltManagerSetup.exe');
const debugSetup = resolve(repoRoot, 'src/VoltManager.Setup/bin/Debug/net48/VoltManagerSetup.exe');

test('setup options page supports vertical gesture panning when widget choices overflow', { skip: process.platform !== 'win32' }, () => {
  const setupAssembly = existsSync(releaseSetup) ? releaseSetup : debugSetup;
  assert.ok(existsSync(setupAssembly), 'VoltManager.Setup must be built before this test runs');

  const escapedAssembly = setupAssembly.replaceAll("'", "''");
  const script = String.raw`
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase
$asm = [System.Reflection.Assembly]::LoadFrom('${escapedAssembly}')
$app = [Activator]::CreateInstance($asm.GetType('VoltManager.Setup.App', $true))
$app.InitializeComponent()
$opts = [Activator]::CreateInstance($asm.GetType('VoltManager.Setup.Engine.InstallOptions', $true))
$page = [Activator]::CreateInstance($asm.GetType('VoltManager.Setup.Pages.OptionsPage', $true), @($opts))
$size = New-Object System.Windows.Size(486, 472)
$rect = New-Object System.Windows.Rect(0, 0, 486, 472)
$page.Measure($size)
$page.Arrange($rect)
$page.FindName('ChkWidgets').IsChecked = $true
$page.UpdateLayout()
$viewer = [System.Windows.Controls.ScrollViewer]$page.Content
Write-Output ('scrollable=' + ($viewer.ScrollableHeight -gt 0).ToString().ToLowerInvariant())
Write-Output ('panning=' + $viewer.PanningMode.ToString())
$app.Shutdown()
`;

  const result = spawnSync(
    'powershell.exe',
    ['-NoProfile', '-STA', '-ExecutionPolicy', 'Bypass', '-Command', script],
    { cwd: repoRoot, encoding: 'utf8' },
  );

  assert.equal(result.status, 0, result.stderr || result.stdout);
  const state = Object.fromEntries(
    result.stdout.trim().split(/\r?\n/).map(line => line.split('=')),
  );
  assert.equal(state.scrollable, 'true', 'expanded widget picker must overflow the available setup viewport');
  assert.equal(state.panning, 'VerticalOnly', 'overflowing setup options must accept vertical touch/gesture panning');
});
