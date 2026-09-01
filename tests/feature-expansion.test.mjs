import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

function source(path) {
  return readFileSync(new URL('../' + path, import.meta.url), 'utf8');
}

const html = source('src/VoltManager/wwwroot/index.html');
const dashboard = source('src/VoltManager/wwwroot/js/dashboard.js');
const power = source('src/VoltManager/wwwroot/js/power.js');
const settings = source('src/VoltManager/wwwroot/js/settings.js');
const app = source('src/VoltManager/wwwroot/js/app.js');

test('advanced battery history exposes range, metrics and CSV export', () => {
  assert.match(html, /data-hours="6"/);
  assert.match(html, /data-hours="24"/);
  assert.match(html, /data-hours="48"/);
  assert.match(html, /battery-history-watt-line/);
  assert.match(html, /battery-history-temp-line/);
  assert.match(html, /battery-history-source-strip/);
  assert.match(dashboard, /Host\.call\('getBatteryHistory', \{ hours: batteryHistoryHours \}\)/);
  assert.match(dashboard, /Host\.call\('exportBatteryHistory'\)/);
});

test('power-plan reason and low-battery threshold are wired end to end in the UI', () => {
  assert.match(html, /id="active-plan-reason-text"/);
  assert.match(html, /id="low-battery-threshold-input"[^>]*min="5"[^>]*max="50"/);
  assert.match(dashboard, /Host\.call\('getActivePlanReason'\)/);
  assert.match(dashboard, /Host\.on\('activePlanReasonChanged'/);
  assert.match(dashboard, /lowBatteryThresholdPercent = value/);
});

test('app profiles can request Keep Awake without adding another subsystem', () => {
  assert.match(power, /rule\.keepAwake = rule\.keepAwake === true/);
  assert.match(power, /app-profile-keep-awake/);
  assert.match(power, /keepAwake: false/);
});

test('global shortcut UI captures and persists native hotkey gestures', () => {
  assert.match(settings, /globalHotkeys/);
  assert.match(settings, /data-hotkey-field/);
  assert.match(settings, /Ctrl\+Alt\+1/);
  assert.match(settings, /window\.__voltSettings\.save\?\.\(\)/);
});

test('power action presets select a delay without scheduling immediately', () => {
  assert.match(app, /data-minutes=.*aria-pressed="false"/);
  assert.match(app, /schedule-preset-btn\[aria-pressed="true"\]/);
  assert.equal((app.match(/scheduleRelativeAction\(/g) || []).length, 2);
});

test('startup applications expose a client-side search filter', () => {
  assert.match(app, /id="startup-search" type="search"/);
  assert.match(app, /function filterStartupApps\(query\)/);
  assert.match(app, /card\.hidden = normalized !== ''/);
});
