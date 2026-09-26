import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const root = new URL('../', import.meta.url);
const read = path => readFileSync(new URL(path, root), 'utf8');

const app = read('src/VoltManager/wwwroot/js/app.js');
const widgets = read('src/VoltManager/wwwroot/js/widgets.js');
const widgetHtml = read('src/VoltManager/wwwroot/widgets.html');
const widgetManager = read('src/VoltManager/Services/WidgetManager.cs');
const bridgeEvents = read('src/VoltManager/Bridge/BridgeEventNames.cs');

test('scheduled power UI normalizes bridge enum casing', () => {
  assert.match(app, /String\(state\.action \|\| ''\)\.toLowerCase\(\)/);
  assert.match(app, /String\(state\.mode \|\| ''\)\.toLowerCase\(\)/);
  assert.doesNotMatch(app, /state\.(?:action|mode) === '(?:Sleep|Restart|Relative|Daily)'/);
  assert.match(widgets, /String\(action \|\| ''\)\.toLowerCase\(\)/);
  assert.match(widgets, /String\(scheduleState\.mode \|\| ''\)\.toLowerCase\(\) === 'daily'/);
});

test('launcher toast cancels a pending fade hide before showing a new result', () => {
  assert.match(widgets, /clearTimeout\(showLauncherToast\.hideTimer\)/);
  assert.match(widgets, /showLauncherToast\.hideTimer = setTimeout\(\(\) => \{/);
});

test('widgets resolve auto animation level and receive live setting changes', () => {
  const helperIndex = widgetHtml.indexOf('js/animation-level.js');
  const widgetsIndex = widgetHtml.indexOf('js/widgets.js');
  assert.ok(helperIndex >= 0 && helperIndex < widgetsIndex);
  assert.match(widgets, /VoltAnimationLevel\.classifyHardwareTier\(info\.ramTotalGb, info\.logicalCores\)/);
  assert.match(widgets, /VoltAnimationLevel\.resolveLevel\(animationSetting, animationHardwareTier\)/);
  assert.match(widgets, /Host\.call\('getSystemInfo'\)\.then\(applyAnimationHardware\)/);
  assert.match(widgets, /Host\.on\('animationLevelChanged'/);
  assert.match(widgetManager, /PushEvent\(BridgeEventNames\.AnimationLevelChanged, data\)/);
  assert.match(bridgeEvents, /AnimationLevelChanged = "animationLevelChanged"/);
});
