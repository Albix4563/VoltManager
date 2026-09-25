import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import { createDocumentHarness, event, FakeNode } from './helpers/dom-harness.mjs';

const read = name => readFileSync(new URL(`../src/VoltManager/wwwroot/js/${name}`, import.meta.url), 'utf8');
const run = (name, context) => vm.runInContext(read(name), context);

function contextFor(document) {
  const window = { document, addEventListener() {}, removeEventListener() {}, innerWidth: 1200, innerHeight: 800 };
  const context = vm.createContext({ window, document, Host: { available: true },
    CustomEvent: class { constructor(type, options) { this.type = type; this.detail = options?.detail; } },
    setTimeout: () => 1, clearTimeout() {}, requestAnimationFrame() {}, cancelAnimationFrame() {},
    console, Math });
  window.Host = context.Host;
  window.I18n = { t: key => key };
  context.I18n = window.I18n;
  return context;
}

test('central capability state hides laptop and brightness nodes until supported', () => {
  const document = createDocumentHarness();
  const laptop = new FakeNode();
  const brightness = new FakeNode();
  const shared = new FakeNode();
  shared.setAttribute('data-vm-laptop-only', '');
  document.registerSelector('[data-vm-laptop-only]', [laptop, shared]);
  document.registerSelector('[data-vm-brightness-only]', [brightness, shared]);
  const context = contextFor(document);
  run('ui-reorganization.js', context);
  const api = context.window.VoltUiReorg;

  api.syncLaptopOnly();
  assert.equal(document.documentElement.dataset.vmHasBattery, 'unknown');
  assert.equal(laptop.style.display, 'none');
  assert.equal(laptop.getAttribute('aria-hidden'), 'true');
  api.state.hasBattery = false;
  api.setBrightnessSupport(false);
  assert.equal(document.documentElement.dataset.vmHasBattery, 'false');
  assert.equal(document.documentElement.dataset.vmBrightness, 'unsupported');
  assert.equal(brightness.style.display, 'none');
  assert.equal(brightness.classList.contains('hidden'), true);

  api.setBrightnessSupport(true);
  assert.equal(shared.style.display, 'none');
  assert.equal(shared.getAttribute('aria-hidden'), 'true');

  api.state.hasBattery = true;
  api.syncLaptopOnly();
  assert.equal(laptop.style.display, '');
  assert.equal(brightness.style.display, '');
});

test('desktop search removes battery results and its power source keyword', () => {
  const document = createDocumentHarness();
  document.documentElement.dataset.vmHasBattery = 'false';
  const context = contextFor(document);
  run('global-search.js', context);
  const entries = context.window.VoltGlobalSearch.localizedCatalog();
  assert.equal(entries.some(item => item.id === 'monitor-battery'), false);
  assert.equal(entries.find(item => item.id === 'power-source').keywords.includes('search_kw_battery'), false);
  document.documentElement.dataset.vmHasBattery = 'true';
  assert.equal(context.window.VoltGlobalSearch.localizedCatalog().some(item => item.id === 'monitor-battery'), true);
});

test('tips carousel hides laptop tips until battery is confirmed and refreshes on availability', () => {
  const document = createDocumentHarness();
  for (const id of ['energy-tips-overlay', 'energy-tips-modal', 'energy-tip-icon', 'energy-tip-title',
    'energy-tip-body', 'energy-tip-counter', 'energy-tip-dots', 'energy-tip-prev', 'energy-tip-next',
    'energy-tips-close', 'btn-energy-tips']) document.registerId(new FakeNode({ id }));
  const context = contextFor(document);
  run('tips.feature.js', context);
  document.dispatchEvent(event('DOMContentLoaded'));
  context.window.__tips.open();
  assert.equal(document.getElementById('energy-tip-dots').children.length, 4);
  assert.match(document.getElementById('energy-tip-counter').textContent, /^1 \/ 4$/);
  const shown = [];
  for (let index = 0; index < 4; index++) {
    shown.push(document.getElementById('energy-tip-title').textContent);
    if (index < 3) document.getElementById('energy-tip-next').dispatchEvent(event('click'));
  }
  assert.deepEqual(shown.sort(), ['tip4_title', 'tip6_title', 'tip7_title', 'tip8_title']);
  document.documentElement.dataset.vmHasBattery = 'true';
  document.dispatchEvent(event('voltbatteryavailabilitychanged'));
  assert.match(document.getElementById('energy-tip-counter').textContent, /^4 \/ 8$/);
});

test('tour navigation skips hidden brightness step in both directions', () => {
  const document = createDocumentHarness();
  document.head = new FakeNode();
  document.documentElement.dataset.vmHasBattery = 'true';
  const hiddenBrightness = new FakeNode({ classes: ['hidden'] });
  for (const selector of ['#nav-list', '#plan-control', '#dash-taskmanager', '#btn-monitoring-toggle']) {
    document.registerSelector(selector, new FakeNode());
  }
  document.registerSelector('#vm-brightness-card', hiddenBrightness);
  document.createElement = tag => {
    const node = new FakeNode({ tagName: tag.toUpperCase() });
    node.querySelector = selector => {
      if (!node.queries.has(selector)) node.registerQuery(selector, new FakeNode());
      return node.queries.get(selector)[0];
    };
    return node;
  };
  const context = contextFor(document);
  run('tour.feature.js', context);
  context.window.__tour.open();
  const pop = document.body.children.at(-1).children.at(-1);
  const next = pop.querySelector('[data-act="next"]');
  const back = pop.querySelector('[data-act="back"]');
  for (let index = 0; index < 3; index++) next.dispatchEvent(event('click'));
  assert.equal(pop.querySelector('.vm-tour-pop__title-text').textContent, 'tour_metrics_title');
  next.dispatchEvent(event('click'));
  assert.equal(pop.querySelector('.vm-tour-pop__title-text').textContent, 'tour_monitor_title');
  assert.equal(pop.querySelector('.vm-tour-counter').textContent, '5 / 6');
  back.dispatchEvent(event('click'));
  assert.equal(pop.querySelector('.vm-tour-pop__title-text').textContent, 'tour_metrics_title');
});

test('brightness buttons step by ten and show the real host value', async () => {
  const document = createDocumentHarness();
  const slider = document.registerId(new FakeNode({ id: 'vm-brightness-range' }));
  const output = document.registerId(new FakeNode({ id: 'vm-brightness-value' }));
  const increase = document.registerId(new FakeNode({ id: 'vm-brightness-increase' }));
  document.registerId(new FakeNode({ id: 'vm-brightness-decrease' }));
  const context = contextFor(document);
  const calls = [];
  let timer;
  context.setTimeout = callback => { timer = callback; return 1; };
  context.Host.call = async (name, payload) => {
    calls.push({ name, payload });
    return name === 'getDisplayBrightness' ? { supported: true, percent: 37 } : { supported: true, percent: 45 };
  };
  run('brightness.js', context);
  document.dispatchEvent(event('voltuiready'));
  document.dispatchEvent(event('DOMContentLoaded'));
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(output.textContent, '37%');
  increase.dispatchEvent(event('click'));
  assert.equal(output.textContent, '47%');
  await timer();
  assert.equal(output.textContent, '45%');
  assert.equal(calls.at(-1).name, 'setDisplayBrightness');
  assert.equal(calls.at(-1).payload.percent, 47);
  assert.equal(slider.value, '45');
});

test('brightness applies a value fetched before the overview card is rendered', async () => {
  const document = createDocumentHarness();
  const context = contextFor(document);
  context.Host.call = async name => {
    assert.equal(name, 'getDisplayBrightness');
    return { supported: true, percent: 37 };
  };
  run('brightness.js', context);
  document.dispatchEvent(event('DOMContentLoaded'));
  await new Promise(resolve => setImmediate(resolve));

  const slider = document.registerId(new FakeNode({ id: 'vm-brightness-range' }));
  const output = document.registerId(new FakeNode({ id: 'vm-brightness-value' }));
  document.registerId(new FakeNode({ id: 'vm-brightness-increase' }));
  document.registerId(new FakeNode({ id: 'vm-brightness-decrease' }));
  document.dispatchEvent(event('voltuiready'));

  assert.equal(slider.value, '37');
  assert.equal(output.textContent, '37%');

  const rerenderedSlider = document.registerId(new FakeNode({ id: 'vm-brightness-range' }));
  const rerenderedOutput = document.registerId(new FakeNode({ id: 'vm-brightness-value' }));
  document.registerId(new FakeNode({ id: 'vm-brightness-increase' }));
  document.registerId(new FakeNode({ id: 'vm-brightness-decrease' }));
  document.dispatchEvent(event('voltuiready'));
  assert.equal(rerenderedSlider.value, '37');
  assert.equal(rerenderedOutput.textContent, '37%');
  assert.equal(rerenderedSlider.listenerCount('input'), 1);
});

test('brightness refresh does not override a pending or in-flight set', async () => {
  const document = createDocumentHarness();
  const slider = document.registerId(new FakeNode({ id: 'vm-brightness-range' }));
  const output = document.registerId(new FakeNode({ id: 'vm-brightness-value' }));
  document.registerId(new FakeNode({ id: 'vm-brightness-increase' }));
  document.registerId(new FakeNode({ id: 'vm-brightness-decrease' }));
  const context = contextFor(document);
  const windowEvents = new Map();
  context.window.addEventListener = (name, handler) => windowEvents.set(name, handler);
  let timer;
  let resolveSet;
  let getCalls = 0;
  context.setTimeout = callback => { timer = callback; return 1; };
  context.Host.call = async (name) => {
    if (name === 'getDisplayBrightness') {
      getCalls++;
      return { supported: true, percent: 37 };
    }
    return new Promise(resolve => { resolveSet = resolve; });
  };
  run('brightness.js', context);
  document.dispatchEvent(event('voltuiready'));
  await new Promise(resolve => setImmediate(resolve));
  const initialGets = getCalls;

  slider.value = '70';
  slider.dispatchEvent(event('input'));
  windowEvents.get('focus')();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(getCalls, initialGets);
  assert.equal(output.textContent, '70%');

  const setPromise = timer();
  windowEvents.get('focus')();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(getCalls, initialGets);
  assert.equal(output.textContent, '70%');
  resolveSet({ supported: true, percent: 68 });
  await setPromise;
  assert.equal(output.textContent, '68%');
});
