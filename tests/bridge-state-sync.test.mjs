import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const root = new URL('../', import.meta.url);
const read = path => readFileSync(new URL(path, root), 'utf8');

const keepAwake = read('src/VoltManager/wwwroot/js/power-keep-awake.js');
const reorg = read('src/VoltManager/wwwroot/js/ui-reorganization.js');
const status = read('src/VoltManager/wwwroot/js/ui-reorganization.status.js');
const search = read('src/VoltManager/wwwroot/js/global-search.js');
const mainWindow = read('src/VoltManager/MainWindow.xaml.cs');
const adaptive = read('src/VoltManager/MainWindow.AdaptiveResources.cs');

function methodBody(source, signature, nextSignature) {
  const start = source.indexOf(signature);
  assert.notEqual(start, -1, `missing method ${signature}`);
  const end = nextSignature ? source.indexOf(nextSignature, start + signature.length) : source.length;
  assert.notEqual(end, -1, `missing method boundary ${nextSignature}`);
  return source.slice(start, end);
}

test('Keep Awake activation reads authoritative host state and rejects stale RPC replies', () => {
  assert.match(keepAwake, /Host\.call\('getKeepAwakeState'\)/);
  assert.match(keepAwake, /keepAwakeRequestSequence/);
  assert.match(keepAwake, /keepAwakeEventRevision/);
  assert.match(
    keepAwake,
    /activate\([^)]*\)\s*\{[\s\S]*?refreshKeepAwakeState\(\)/
  );
  assert.match(
    keepAwake,
    /keepAwakeEventRevision\s*\+=\s*1[\s\S]*?applyKeepAwakeState\(state\)/
  );
  assert.match(
    keepAwake,
    /Host\.call\('setKeepAwake'[\s\S]*?catch \(err\) \{\s*if \(eventRevision !== keepAwakeEventRevision\) return;/
  );
});

test('Overview Keep Awake quick action is bridge-driven and concurrency guarded', () => {
  const quickAction = methodBody(reorg, 'function quickAction(action)', 'function configureWidgets()');
  assert.match(quickAction, /keepAwakeInFlight/);
  assert.match(quickAction, /Host\.call\('getKeepAwakeState'\)/);
  assert.match(quickAction, /Host\.call\('setKeepAwake',\s*\{\s*enabled:/);
  assert.doesNotMatch(quickAction, /querySelector\([^)]*checkbox|keep-awake-mount/);
});

test('Overview status uses bridge snapshots for Keep Awake and App Profiles', () => {
  assert.match(status, /Host\.call\('getKeepAwakeState'\)/);
  assert.match(status, /Host\.on\('keepAwakeChanged'/);
  assert.match(status, /Host\.call\('getAppPowerProfileStatus'\)/);
  assert.match(status, /Host\.on\('appPowerProfileActivityChanged'/);
  assert.match(status, /appProfileStatus\?\.active/);
  assert.match(status, /keepAwakeState\?\.enabled/);
  assert.doesNotMatch(status, /function appProfileActive\([\s\S]*?app-power-profile-mount/);
  assert.doesNotMatch(status, /function keepAwakeActive\([\s\S]*?keep-awake-mount/);
});

test('global search targets the current Keep Awake mount', () => {
  assert.match(
    search,
    /id:\s*'power-keep-awake'[\s\S]*?targetId:\s*'vm-keep-awake'/
  );
});

test('fresh WebView state replays all stateful bridge snapshots', () => {
  const publish = methodBody(
    adaptive,
    'private void PublishFreshAdaptiveStateAfterResume()',
    'private void OnAdaptiveWindowClosed'
  );
  for (const eventName of [
    'ActivePlanChanged',
    'ActivePlanReasonChanged',
    'AutomationStateChanged',
    'ManualOverrideChanged',
    'CpuAutomationStateChanged',
    'GamingModeChanged',
    'KeepAwakeChanged',
    'PowerSourcePlanChanged',
    'ThermalGuardChanged',
    'IdlePowerGuardChanged',
    'ScheduledPowerActionChanged',
    'WidgetsStateChanged',
    'AppPowerProfileActivityChanged',
    'HeavyAppActivityChanged',
    'ThemeChanged',
    'LanguageChanged',
    'FontChanged',
    'GlobalHotkeysChanged',
  ]) {
    assert.match(publish, new RegExp(`BridgeEventNames\\.${eventName}`), `missing ${eventName}`);
  }
  assert.match(publish, /PushAdaptiveResourceProfile\(/);
});

test('global hotkey registration result is cached for bridge replay', () => {
  assert.match(mainWindow, /_lastHotkeyRegistrations/);
  assert.match(mainWindow, /_lastHotkeyRegistrations\s*=\s*_globalHotkeys\.Rebind/);
});
