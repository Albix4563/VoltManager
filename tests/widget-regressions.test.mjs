import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const root = new URL('../', import.meta.url);
const read = path => readFileSync(new URL(path, root), 'utf8');

const app = read('src/VoltManager/wwwroot/js/app.js');
const widgets = read('src/VoltManager/wwwroot/js/widgets.js');
const styleController = read('src/VoltManager/wwwroot/js/style-controller.js');
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
  const controllerIndex = widgetHtml.indexOf('js/style-controller.js');
  const widgetsIndex = widgetHtml.indexOf('js/widgets.js');
  assert.ok(controllerIndex >= 0 && controllerIndex < widgetsIndex);
  assert.match(widgets, /window\.Volt\?\.style\?\.setHardwareInfo\(info\)/);
  assert.match(widgets, /window\.Volt\?\.style\?\.hydrateSettings\(res\)/);
  assert.match(widgets, /Host\.call\('getSystemInfo'\)\.then\(applyAnimationHardware\)/);
  assert.match(widgets, /window\.Volt\?\.style\?\.bindHost\(Host\)/);
  assert.match(styleController, /safeOn\('animationLevelChanged', data => setAnimationLevel\(data && data\.level\)\)/);
  // motion.css (shared timing tokens) keys off data-anim, widgets.css off data-animation-level.
  assert.match(styleController, /html\.dataset\.anim = resolved/);
  assert.match(styleController, /html\.dataset\.animationLevel = resolved/);
  assert.match(widgetManager, /PushEvent\(BridgeEventNames\.AnimationLevelChanged, data\)/);
  assert.match(bridgeEvents, /AnimationLevelChanged = "animationLevelChanged"/);
});
