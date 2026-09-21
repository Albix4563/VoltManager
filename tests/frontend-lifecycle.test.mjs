import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(
  new URL('../src/VoltManager/wwwroot/js/view-lifecycle.js', import.meta.url),
  'utf8'
);
const advanced = readFileSync(
  new URL('../src/VoltManager/wwwroot/js/advanced.js', import.meta.url),
  'utf8'
);
const reorgRouter = readFileSync(
  new URL('../src/VoltManager/wwwroot/js/ui-reorganization.js', import.meta.url),
  'utf8'
);
const dashboard = readFileSync(
  new URL('../src/VoltManager/wwwroot/js/dashboard.js', import.meta.url),
  'utf8'
);
const settings = readFileSync(
  new URL('../src/VoltManager/wwwroot/js/settings.js', import.meta.url),
  'utf8'
);
const settingsPreferencesTools = readFileSync(
  new URL('../src/VoltManager/wwwroot/js/settings-preferences-tools.js', import.meta.url),
  'utf8'
);
const indexHtml = readFileSync(
  new URL('../src/VoltManager/wwwroot/index.html', import.meta.url),
  'utf8'
);
const powerModules = [
  ['power-app-profiles.js', 'app-power-profiles'],
  ['power-detection.js', 'heavy-app-detection'],
  ['power-keep-awake.js', 'keep-awake'],
  ['power-protections.js', 'power-protections'],
  ['power-history.js', 'plan-history'],
].map(([file, id]) => [
  readFileSync(new URL(`../src/VoltManager/wwwroot/js/${file}`, import.meta.url), 'utf8'),
  id,
]);

function loadLifecycle() {
  const windowEvents = new Map();
  const window = {
    addEventListener(name, handler) {
      if (!windowEvents.has(name)) windowEvents.set(name, []);
      windowEvents.get(name).push(handler);
    },
  };
  window.window = window;
  vm.runInContext(source, vm.createContext({ window, console }));
  return { lifecycle: window.VoltViewLifecycle, windowEvents };
}

test('view lifecycle initializes once and does not duplicate activation for the same route', () => {
  const { lifecycle } = loadLifecycle();
  const calls = [];
  lifecycle.register('history', {
    matches: route => route.view === 'power-plans' && route.subviews['power-plans'] === 'history',
    init: () => calls.push('init'),
    activate: () => calls.push('activate'),
    deactivate: () => calls.push('deactivate'),
    dispose: () => calls.push('dispose'),
  });

  lifecycle.transition({ view: 'power-plans', subviews: { 'power-plans': 'history' } });
  lifecycle.transition({ view: 'power-plans', subviews: { 'power-plans': 'history' } });

  assert.deepEqual(calls, ['init', 'activate']);
});

test('view lifecycle deactivates the old module before activating the new route owner', () => {
  const { lifecycle } = loadLifecycle();
  const calls = [];
  lifecycle.register('history', {
    matches: route => route.subviews['power-plans'] === 'history',
    init() {},
    activate: () => calls.push('history:on'),
    deactivate: () => calls.push('history:off'),
  });
  lifecycle.register('advanced', {
    matches: route => route.subviews['power-plans'] === 'advanced',
    init() {},
    activate: () => calls.push('advanced:on'),
    deactivate: () => calls.push('advanced:off'),
  });

  lifecycle.transition({ view: 'power-plans', subviews: { 'power-plans': 'history' } });
  lifecycle.transition({ view: 'power-plans', subviews: { 'power-plans': 'advanced' } });

  assert.deepEqual(calls, ['history:on', 'history:off', 'advanced:on']);
});

test('disposing the lifecycle releases every initialized module exactly once', () => {
  const { lifecycle } = loadLifecycle();
  let disposals = 0;
  lifecycle.register('tools', {
    matches: () => true,
    init() {},
    activate() {},
    deactivate() {},
    dispose: () => { disposals += 1; },
  });

  lifecycle.transition({ view: 'system-tools', subviews: {} });
  lifecycle.dispose();
  lifecycle.dispose();

  assert.equal(disposals, 1);
});

test('advanced and RAM lazy views are lifecycle-owned instead of route-event owned', () => {
  assert.match(advanced, /VoltViewLifecycle\.register\('advanced-parameters'/);
  assert.match(advanced, /VoltViewLifecycle\.register\('memory-cleaner'/);
  assert.match(advanced, /VoltViewLifecycle\.register\('power-timeouts'/);
  assert.match(advanced, /getElementById\('vm-power-advanced'\) \|\| document\.getElementById\('advanced-params-mount'\)/);
  assert.match(advanced, /getElementById\('vm-system-memory'\) \|\| document\.getElementById\('ram-cleaner-mount'\)/);
  assert.match(advanced, /if \(!ramAutoRefresh\) ramAutoRefresh = setInterval\(loadRamStatus, 5000\)/);
  assert.doesNotMatch(advanced, /addEventListener\('viewchange'/);
  assert.doesNotMatch(advanced, /addEventListener\('voltuiviewchanged'/);
  assert.doesNotMatch(advanced, /addEventListener\('voltuisubviewchanged'/);
  assert.doesNotMatch(reorgRouter, /activateLegacyPowerPanel\('advanced'\)/);
  assert.doesNotMatch(reorgRouter, /activateLegacyPowerPanel\('ram'\)/);
  assert.doesNotMatch(reorgRouter, /move\(\$\('advanced-params-mount'\), \$\('vm-power-advanced'\)\)/);
  assert.doesNotMatch(reorgRouter, /move\(\$\('ram-cleaner-mount'\), \$\('vm-system-memory'\)\)/);
});

test('dashboard polling is route-owned and does not subscribe to parallel routing events', () => {
  assert.match(dashboard, /VoltViewLifecycle\.register\('dashboard-processes'/);
  assert.match(dashboard, /VoltViewLifecycle\.register\('dashboard-battery'/);
  assert.doesNotMatch(dashboard, /\['viewchange', 'voltuiviewchanged', 'voltuisubviewchanged', 'voltuiready'\]/);
  assert.match(dashboard, /if \(!processRouteActive\) return/);
  assert.match(dashboard, /if \(!batteryRouteActive\) return/);
});

test('settings preferences and maintenance tools have one lifecycle owner with cleanup', () => {
  assert.match(settingsPreferencesTools, /VoltViewLifecycle\.register\('settings-preferences-tools'/);
  assert.match(settingsPreferencesTools, /removeEventListener\(type, handler, options\)/);
  for (const method of ['setStartWithWindows', 'setCloseToTray', 'exportSettings', 'importSettings', 'exportDiagnostics', 'openLogFolder']) {
    assert.match(settingsPreferencesTools, new RegExp(`Host\\.call\\('${method}'`));
  }
  assert.doesNotMatch(settings, /getElementById\('pref-autostart'\)\?\.addEventListener/);
  assert.doesNotMatch(settings, /getElementById\('btn-export-settings'\)\?\.addEventListener/);
  assert.ok(indexHtml.indexOf('js/settings.js') < indexHtml.indexOf('js/settings-preferences-tools.js'));
});

test('power feature styles are owned by one stylesheet instead of runtime injection', () => {
  const power = readFileSync(
    new URL('../src/VoltManager/wwwroot/js/power.js', import.meta.url),
    'utf8'
  );
  assert.match(indexHtml, /css\/power-features\.css/);
  assert.doesNotMatch(power, /power-feature-styles/);
  assert.doesNotMatch(power, /style\.textContent\s*=/);
});

test('extracted power features declare lifecycle ownership and release subscriptions', () => {
  for (const [moduleSource, id] of powerModules) {
    assert.match(moduleSource, new RegExp(`register\\('${id}'`));
    assert.match(moduleSource, /matches\(route\)/);
    assert.match(moduleSource, /deactivate/);
    assert.match(moduleSource, /dispose/);
  }
  for (const [moduleSource] of powerModules.slice(0, 4)) {
    assert.match(moduleSource, /disposeListeners\(\)/);
  }
  assert.match(powerModules[4][0], /planHistoryUnsubscribe\(\)/);
});
