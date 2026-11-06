import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import { createDocumentHarness, event, FakeNode } from './helpers/dom-harness.mjs';

const source = readFileSync(
  new URL('../src/VoltManager/wwwroot/js/energy-suggestions.js', import.meta.url), 'utf8');
const catalogs = readFileSync(
  new URL('../src/VoltManager/wwwroot/js/i18n.catalogs.js', import.meta.url), 'utf8');
const tipsFeature = readFileSync(
  new URL('../src/VoltManager/wwwroot/js/tips.feature.js', import.meta.url), 'utf8');

const CATALOG_KEYS = [
  'card_title', 'card_subtitle', 'view_all', 'badge_title',
  'priority_now', 'priority_recommended', 'priority_general',
  'lowBattery_title', 'lowBattery_body', 'lowBattery_cta',
  'performance_title', 'performance_body', 'performance_cta',
  'keepAwake_title', 'keepAwake_body', 'keepAwake_cta',
  'gaming_title', 'gaming_body', 'gaming_cta',
  'override_title', 'override_body', 'override_cta',
  'brightness_title', 'brightness_body', 'brightness_cta',
  'autoBoost_title', 'autoBoost_body', 'autoBoost_cta',
  'automation_title', 'automation_body', 'automation_cta',
  'general_title', 'general_body', 'general_cta',
];

function flush() {
  return new Promise(resolve => setImmediate(resolve));
}

function bootModule(options = {}) {
  const document = createDocumentHarness();
  document.documentElement.dataset.vmHasBattery = options.hasBattery ?? 'true';

  const view = new FakeNode({ id: 'view-overview' });
  const grid = new FakeNode();
  view.appendChild(grid);
  document.registerId(view);
  document.registerSelector('#view-overview .vm-overview-grid', grid);

  const planButtons = ['powerSaver', 'balanced', 'performance'].map(plan =>
    new FakeNode({ dataset: { plan }, classes: plan === (options.plan || 'balanced')
      ? ['text-secondary-container'] : [] }));
  document.registerSelector('#plan-control [data-plan]', planButtons);

  const button = document.registerId(new FakeNode({ id: 'btn-energy-tips' }));
  const badge = document.registerId(new FakeNode({ id: 'vm-tips-badge', classes: ['hidden'] }));
  document.registerId(new FakeNode({ id: 'manual-override-chip', classes: ['hidden'] }));
  const brightness = document.registerId(new FakeNode({ id: 'vm-brightness-value' }));
  brightness.textContent = options.brightness == null ? '--%' : options.brightness + '%';
  document.registerId(new FakeNode({ id: 'vm-brightness-range' }));
  document.registerId(new FakeNode({ id: 'master-toggle' }));
  document.registerId(new FakeNode({ id: 'vm-brightness-decrease' }));

  const handlers = new Map();
  const calls = [];
  const settings = options.settings || {
    masterAutomationEnabled: true,
    powerSourcePlan: { enabled: true },
    keepAwake: { enabled: false },
  };
  const powerState = options.powerState === undefined
    ? {
        enabled: true,
        powerSourceKnown: true,
        pluggedIn: false,
        batteryPercent: 15,
        manualOverrideActive: false,
      }
    : options.powerState;

  const window = {
    document,
    innerWidth: 1200,
    addEventListener() {},
    removeEventListener() {},
    VoltUiReorg: { activateView() {}, activateSubview() {} },
  };
  const Host = {
    available: true,
    call: async name => {
      calls.push(name);
      if (name === 'getPowerSourcePlanState') return powerState;
      if (name === 'getKeepAwakeState') return { enabled: !!options.keepAwake };
      return {};
    },
    on(name, handler) {
      handlers.set(name, handler);
      return () => handlers.delete(name);
    },
    fail() {},
  };
  window.Host = Host;
  window.__voltSettings = { get: () => settings };
  window.I18n = { feature: (_ns, key) => key, t: key => key, getLang: () => 'en' };
  window.__voltGamingMode = { isActive: () => !!options.gaming, setEnabled: async () => ({}) };

  const context = vm.createContext({
    window,
    document,
    Host,
    I18n: window.I18n,
    CustomEvent: class { constructor(type, detail) { this.type = type; this.detail = detail?.detail; } },
    MouseEvent: class { constructor(type, init = {}) { this.type = type; Object.assign(this, init); } },
    Event: class { constructor(type, init = {}) { this.type = type; Object.assign(this, init); } },
    Math,
    JSON,
    Date,
    console,
    getComputedStyle: () => ({ display: 'block' }),
  });
  vm.runInContext(source, context);
  document.dispatchEvent(event('DOMContentLoaded'));
  document.dispatchEvent(event('voltuiready'));

  return {
    api: window.__energySuggestions,
    document, window, Host, view, grid, planButtons, badge, button, handlers, calls,
  };
}

const baseState = {
  hasBattery: true,
  onAc: false,
  batteryPercent: 50,
  plan: 'balanced',
  automation: true,
  powerSourcePlan: true,
  override: false,
  overridePlan: null,
  keepAwake: false,
  gaming: false,
  brightness: 40,
};

test('every suggestion string is localized in all supported languages', () => {
  const context = vm.createContext({ window: {} });
  vm.runInContext(catalogs, context);
  const namespace = context.window.VoltI18nCatalogs.namespaces.energySuggestions;
  assert.ok(namespace, 'energySuggestions namespace must exist');
  for (const locale of ['it', 'en', 'es', 'zh']) {
    for (const key of CATALOG_KEYS) {
      assert.ok(namespace[locale] && namespace[locale][key],
        `missing energySuggestions.${locale}.${key}`);
    }
  }
});

test('low battery proposes the saver plan and the saver tip', () => {
  const { api } = bootModule();
  const suggestions = api.evaluate({
    ...baseState, batteryPercent: 12, plan: 'balanced',
  });
  assert.equal(suggestions[0].id, 'low-battery');
  assert.deepEqual({ ...suggestions[0].action }, { kind: 'switch-plan', plan: 'powerSaver' });
  assert.equal(suggestions[0].tipId, 'tip1');
  assert.equal(api.getPreferredTipIds({ ...baseState, batteryPercent: 12, plan: 'balanced' })[0], 'tip1');
});

test('desktop systems only receive the general advice', () => {
  const { api } = bootModule({ hasBattery: 'false' });
  const ids = api.evaluate({
    ...baseState, hasBattery: false, onAc: null, batteryPercent: null,
    brightness: 90, keepAwake: true, gaming: true,
  }).map(item => item.id);
  assert.deepEqual([...ids], ['general-tip']);
  assert.deepEqual([...api.getPreferredTipIds()], []);
});

test('battery-specific rules stay active and ordered by urgency', () => {
  const { api } = bootModule();
  const perf = api.evaluate({
    ...baseState, plan: 'performance', powerSourcePlan: false, batteryPercent: 70,
  }).map(item => item.id);
  assert.deepEqual([...perf], ['performance-on-battery', 'auto-boost-off']);

  const wake = api.evaluate({
    ...baseState, keepAwake: true, gaming: true, brightness: 30,
  }).map(item => item.id);
  assert.deepEqual([...wake], ['keep-awake-on-battery', 'gaming-on-battery']);

  const override = api.evaluate({
    ...baseState, plan: 'performance', override: true, overridePlan: 'performance',
    brightness: 80, batteryPercent: 45,
  }).map(item => item.id);
  assert.deepEqual([...override], ['override-on-battery', 'brightness-high']);
});

test('automation advice exists even on AC and suppresses plan nagging', () => {
  const { api } = bootModule();
  const ids = api.evaluate({
    ...baseState, onAc: true, automation: false, plan: 'performance',
  }).map(item => item.id);
  assert.deepEqual([...ids], ['automation-off']);
  assert.deepEqual({ ...api.evaluate({
    ...baseState, onAc: true, automation: false,
  })[0].action }, { kind: 'open-automations' });
});

test('Home projects the live suggestion, updates the badge and mounts once', async () => {
  const h = bootModule();
  await flush();
  await flush();

  assert.equal(h.api.isMounted(), true);
  assert.equal(h.grid.children.filter(node => node.id === 'vm-energy-suggestions').length, 1);
  assert.equal(h.api.getRenderedSuggestions()[0].id, 'low-battery');
  assert.equal(h.badge.textContent, '1');
  assert.equal(h.badge.classList.contains('hidden'), false);
  assert.equal(h.button.getAttribute('title'), 'badge_title');
  assert.ok(h.calls.includes('getPowerSourcePlanState'));
});

test('a healthy state hides the badge but keeps the general tip card', async () => {
  const h = bootModule({
    powerState: {
      enabled: true, powerSourceKnown: true, pluggedIn: true, batteryPercent: 100,
      manualOverrideActive: false,
    },
  });
  await flush();

  assert.equal(h.api.getRenderedSuggestions()[0].id, 'general-tip');
  assert.equal(h.badge.textContent, '0');
  assert.equal(h.badge.classList.contains('hidden'), true);
  assert.equal(h.button.getAttribute('title'), 'tips_btn_title');
});

test('host power events refresh the projection without remounting', async () => {
  const h = bootModule();
  await flush();
  h.handlers.get('powerSourcePlanChanged')({
    enabled: true, powerSourceKnown: true, pluggedIn: false, batteryPercent: 80,
  });
  assert.equal(h.api.getRenderedSuggestions()[0].id, 'general-tip');
  h.handlers.get('powerSourcePlanChanged')({
    enabled: true, powerSourceKnown: true, pluggedIn: false, batteryPercent: 12,
  });
  assert.equal(h.api.getRenderedSuggestions()[0].id, 'low-battery');
  assert.equal(h.grid.children.filter(node => node.id === 'vm-energy-suggestions').length, 1);
});

test('the carousel leads with the tip that matches the current state', () => {
  const document = createDocumentHarness();
  for (const id of ['energy-tips-overlay', 'energy-tips-modal', 'energy-tip-icon', 'energy-tip-title',
    'energy-tip-body', 'energy-tip-counter', 'energy-tip-dots', 'energy-tip-prev', 'energy-tip-next',
    'energy-tips-close', 'btn-energy-tips']) document.registerId(new FakeNode({ id }));
  document.documentElement.dataset.vmHasBattery = 'true';
  const window = {
    document,
    addEventListener() {},
    removeEventListener() {},
    I18n: { t: key => key, getLang: () => 'en' },
    __energySuggestions: { getPreferredTipIds: () => ['tip3'] },
  };
  const context = vm.createContext({
    window,
    document,
    I18n: window.I18n,
    CustomEvent: class { constructor(type, init) { this.type = type; this.detail = init?.detail; } },
    Math,
    console,
  });
  vm.runInContext(tipsFeature, context);
  document.dispatchEvent(event('DOMContentLoaded'));
  window.__tips.open();

  assert.equal(document.getElementById('energy-tip-title').textContent, 'tip3_title');
  assert.equal(document.getElementById('energy-tip-dots').children.length, 8);
  assert.match(document.getElementById('energy-tip-counter').textContent, /^1 \/ 8$/);
});

test('a laptop-only preferred tip is skipped on desktops', () => {
  const document = createDocumentHarness();
  for (const id of ['energy-tips-overlay', 'energy-tips-modal', 'energy-tip-icon', 'energy-tip-title',
    'energy-tip-body', 'energy-tip-counter', 'energy-tip-dots', 'energy-tip-prev', 'energy-tip-next',
    'energy-tips-close', 'btn-energy-tips']) document.registerId(new FakeNode({ id }));
  document.documentElement.dataset.vmHasBattery = 'false';
  const window = {
    document,
    addEventListener() {},
    removeEventListener() {},
    I18n: { t: key => key, getLang: () => 'en' },
    __energySuggestions: { getPreferredTipIds: () => ['tip1', 'tip4'] },
  };
  const context = vm.createContext({
    window,
    document,
    I18n: window.I18n,
    CustomEvent: class { constructor(type, init) { this.type = type; this.detail = init?.detail; } },
    Math,
    console,
  });
  vm.runInContext(tipsFeature, context);
  document.dispatchEvent(event('DOMContentLoaded'));
  window.__tips.open();

  assert.equal(document.getElementById('energy-tip-title').textContent, 'tip4_title');
  assert.equal(document.getElementById('energy-tip-dots').children.length, 4);
});
