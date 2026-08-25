import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

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

test('frontend consumes one host resource profile signal', () => {
  assert.match(perfGuard, /Host\.on\(['"]resourceProfileChanged['"]/);
  assert.match(perfGuard, /dataset\.resourceProfile/);
  assert.match(perfGuard, /resourceprofilechange/);
});

test('gaming and critical profiles reuse the proven lite rendering path', () => {
  assert.match(perfGuard, /profile === 'gaming' \|\| profile === 'critical'/);
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
