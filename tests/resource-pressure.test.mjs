import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';

const perfGuard = readFileSync(
  new URL('../src/VoltManager/wwwroot/js/perf-guard.js', import.meta.url),
  'utf8'
);
const bridge = readFileSync(
  new URL('../src/VoltManager/wwwroot/js/bridge.js', import.meta.url),
  'utf8'
);
const effectsJs = readFileSync(
  new URL('../src/VoltManager/wwwroot/js/effects.js', import.meta.url),
  'utf8'
);
const effectsCss = readFileSync(
  new URL('../src/VoltManager/wwwroot/css/effects.css', import.meta.url),
  'utf8'
);
const mainWindowHost = readFileSync(
  new URL('../src/VoltManager/MainWindow.xaml.cs', import.meta.url),
  'utf8'
);

test('same profile updates polling on focus and visibility changes without duplicate events', () => {
  const handlers = new Map();
  const events = [];
  const document = {
    documentElement: { dataset: {} },
    addEventListener() {},
    dispatchEvent(event) { events.push(event); },
  };
  const Host = { on(name, callback) { handlers.set(name, callback); } };
  const window = { Host };
  runInNewContext(perfGuard, {
    window, document, Host, console,
    CustomEvent: class { constructor(type, options) { this.type = type; this.detail = options.detail; } },
  });
  const emit = handlers.get('resourceProfileChanged');
  const active = { profile: 'full', uiActive: true, uiVisible: true, allowProcessPolling: true,
    processPollingIntervalMs: 3000, metricsIntervalMs: 1000, reducedEffects: false };
  emit(active);
  emit({ ...active });
  emit({ ...active, uiActive: false, processPollingIntervalMs: 10000, reducedEffects: true });
  assert.equal(document.documentElement.dataset.perf, 'lite');
  emit({ ...active, uiVisible: false, allowProcessPolling: false, processPollingIntervalMs: 0 });
  emit(active);
  assert.equal(document.documentElement.dataset.perf, '');
  const changes = events.filter(event => event.type === 'resourceprofilechange');
  assert.deepEqual(changes.map(event => event.detail.processPollingIntervalMs), [3000, 10000, 0, 3000]);
});

test('frontend consumes one host resource profile signal', () => {
  assert.match(perfGuard, /Host\.on\(['"]resourceProfileChanged['"]/);
  assert.match(perfGuard, /dataset\.resourceProfile/);
  assert.match(perfGuard, /resourceprofilechange/);
});

test('gaming workload and critical profiles reuse the proven lite rendering path', () => {
  assert.match(perfGuard, /state\.reducedEffects/);
  assert.match(perfGuard, /profile === 'gaming' \|\| profile === 'workload' \|\| profile === 'critical'/);
  assert.match(perfGuard, /dataset\.perf\s*=\s*effectiveLite \? 'lite'/);
  assert.match(effectsJs, /dataset\.perf === 'lite'/);
  assert.match(effectsCss, /data-perf="lite"/);
  assert.match(perfGuard, /VoltFx\.stopMotion/);
});

test('top-process RPC is elastic while safety RPCs remain ungated', () => {
  assert.match(bridge, /method === 'getTopProcesses'/);
  assert.match(bridge, /allowProcessPolling === false/);
  assert.match(bridge, /processPollingIntervalMs/);
  assert.match(bridge, /return rawCall\(method, payload\)/);
});

test('WebView lifecycle uses suspend-resume without mixing manual memory target levels', () => {
  assert.match(mainWindowHost, /TrySuspendWebView\(\)/);
  assert.match(mainWindowHost, /ResumeWebView\(\)/);
  assert.doesNotMatch(mainWindowHost, /MemoryUsageTargetLevel/);
  assert.doesNotMatch(mainWindowHost, /SetWebViewMemoryLevel/);
});

test('tray lifecycle does not trim the host or WebView working sets', () => {
  assert.doesNotMatch(mainWindowHost, /ScheduleWorkingSetTrim|TrimParkedWorkingSets/);
});

test('minimize hides the renderer before suspending and reserves teardown for the tray', () => {
  const visibility = mainWindowHost.slice(mainWindowHost.indexOf('private void UpdateWebViewVisibility()'),
    mainWindowHost.indexOf('private void OnMetricsUpdated('));
  assert.match(visibility, /WebView\.Visibility = visible \? Visibility\.Visible : Visibility\.Hidden;/);
  assert.match(visibility, /TrySuspendWebView\(\);\s*\/\/[^\n]*\n\s*if \(!IsVisible\) ScheduleTrayTeardown\(\);/);
  assert.match(mainWindowHost, /WebView\.Visibility = Visibility\.Hidden;[\s\S]*?bool suspended = await core\.TrySuspendAsync\(\);/);
  assert.match(mainWindowHost, /if \(!suspended && !_webViewVisible\)/);
  assert.match(mainWindowHost, /private void ResumeWebView\(\)\s*\{\s*if \(!_webViewVisible\) return;/);
  assert.match(mainWindowHost, /if \(_webViewVisible \|\| _exiting\) return;/);
});

test('idle decorative animations require rich effects, while progress animations stay independent', () => {
  for (const selector of ['.fx-title', '.nav-indicator', '#monitoring-dot', '#monitoring-dot::after'])
    assert.ok(effectsCss.includes(`html:not([data-fx="rich"]) ${selector}`));
  assert.match(effectsCss, /html\[data-perf-tier="lite"\] #monitoring-dot \{ animation: none !important; \}/);
  assert.doesNotMatch(effectsCss, /html:not\(\[data-fx="rich"\]\)\s+\.animate-spin/);
});
