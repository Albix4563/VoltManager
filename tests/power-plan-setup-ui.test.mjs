import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const setupSource = readFileSync(new URL('../src/VoltManager/wwwroot/js/setup.js', import.meta.url), 'utf8');
const cleanupSource = readFileSync(new URL('../src/VoltManager/wwwroot/js/setup-extra-plans.js', import.meta.url), 'utf8');
const reorgSource = readFileSync(new URL('../src/VoltManager/wwwroot/js/ui-reorganization.js', import.meta.url), 'utf8');
const layoutSource = readFileSync(new URL('../src/VoltManager/wwwroot/js/ui-reorganization.layout.js', import.meta.url), 'utf8');
const html = readFileSync(new URL('../src/VoltManager/wwwroot/index.html', import.meta.url), 'utf8');
const css = readFileSync(new URL('../src/VoltManager/wwwroot/css/power-features.css', import.meta.url), 'utf8');

class FakeClassList {
  constructor(initial = []) { this.values = new Set(initial); }
  add(...names) { names.forEach(name => this.values.add(name)); }
  remove(...names) { names.forEach(name => this.values.delete(name)); }
  contains(name) { return this.values.has(name); }
  toggle(name, force) {
    if (force === undefined) force = !this.values.has(name);
    if (force) this.values.add(name); else this.values.delete(name);
    return force;
  }
}

class FakeElement {
  constructor(document, tag = 'div', id = '') {
    this.ownerDocument = document;
    this.tagName = tag.toUpperCase();
    this.id = id;
    this.children = [];
    this.parentElement = null;
    this.classList = new FakeClassList();
    this.dataset = {};
    this.attributes = new Map();
    this.listeners = new Map();
    this.disabled = false;
    this.checked = false;
    this.value = '';
    this.textContent = '';
    this.offsetParent = {};
  }
  appendChild(child) { child.parentElement = this; this.children.push(child); return child; }
  append(...children) { children.forEach(child => this.appendChild(child)); }
  replaceChildren(...children) { this.children = []; children.forEach(child => this.appendChild(child)); }
  addEventListener(type, handler) {
    if (!this.listeners.has(type)) this.listeners.set(type, []);
    this.listeners.get(type).push(handler);
  }
  dispatch(type) { for (const handler of this.listeners.get(type) || []) handler({ target: this }); }
  click() { this.dispatch('click'); }
  focus() { this.ownerDocument.activeElement = this; }
  setAttribute(name, value) { this.attributes.set(name, String(value)); }
  getAttribute(name) { return this.attributes.get(name) ?? null; }
  descendants() { return this.children.flatMap(child => [child, ...child.descendants()]); }
  querySelectorAll(selector) {
    const nodes = this.descendants();
    if (selector === 'select[data-plan-id]')
      return nodes.filter(node => node.tagName === 'SELECT' && node.dataset.planId);
    if (selector.includes('input[type="checkbox"]:checked'))
      return nodes.filter(node => node.tagName === 'INPUT' && node.checked);
    if (selector.includes('input[type="checkbox"]'))
      return nodes.filter(node => node.tagName === 'INPUT');
    if (selector.includes('button:not([disabled])') || selector.includes('select:not([disabled])'))
      return nodes.filter(node => ['BUTTON', 'SELECT', 'INPUT'].includes(node.tagName) && !node.disabled && !node.classList.contains('hidden'));
    return [];
  }
  querySelector(selector) {
    if (selector.includes('option:checked:not([value=""])'))
      return this.querySelectorAll('select[data-plan-id]').find(select => select.value) || null;
    return this.querySelectorAll(selector)[0] || null;
  }
}

function createSetupHarness(checkResponses) {
  const elements = new Map();
  const document = {
    activeElement: null,
    listeners: new Map(),
    getElementById(id) { return elements.get(id) || null; },
    createElement(tag) { return new FakeElement(document, tag); },
    addEventListener(type, handler) {
      if (!this.listeners.has(type)) this.listeners.set(type, []);
      this.listeners.get(type).push(handler);
    },
  };
  const add = (id, tag = 'div', hidden = false) => {
    const node = new FakeElement(document, tag, id);
    if (hidden) node.classList.add('hidden');
    elements.set(id, node);
    return node;
  };

  const overlay = add('setup-overlay', 'div', true);
  const modal = add('setup-modal', 'main');
  const status = add('setup-status', 'p', true);
  const association = add('setup-association', 'section', true);
  const associationList = add('setup-association-list');
  const btnAssociate = add('btn-setup-associate', 'button');
  const btnInstall = add('btn-setup-install', 'button');
  const btnRetry = add('btn-setup-retry', 'button', true);
  const btnExit = add('btn-setup-exit', 'button');
  association.append(associationList, btnAssociate);
  modal.append(status, association, btnExit, btnRetry, btnInstall);
  overlay.appendChild(modal);
  document.activeElement = new FakeElement(document, 'button', 'before-setup');

  const calls = [];
  const queue = [...checkResponses];
  const Host = {
    available: true,
    async call(method, payload) {
      calls.push({ method, payload });
      if (method === 'checkDefaultPlans') {
        const next = queue.shift();
        if (next instanceof Error) throw next;
        return next;
      }
      if (method === 'associateDefaultPlans') return { success: true };
      if (method === 'restoreDefaultPlans') return { success: true };
      if (method === 'exitApp') return { success: true };
      throw new Error(`Unexpected Host.call(${method})`);
    },
  };
  const I18n = { t: key => key };
  const immediateTimer = callback => { queueMicrotask(callback); return 1; };
  const window = { Host, I18n, setTimeout: immediateTimer };
  window.window = window;
  const quietConsole = { ...console, error() {} };
  const context = vm.createContext({
    window, document, Host, I18n, console: quietConsole,
    requestAnimationFrame: callback => queueMicrotask(callback),
    queueMicrotask,
    Promise,
  });
  vm.runInContext(setupSource, context);
  return { window, document, elements, calls };
}

async function settle() {
  await new Promise(resolve => setImmediate(resolve));
  await new Promise(resolve => setImmediate(resolve));
}

test('setup readiness resolves immediately after a successful all-present check', async () => {
  const harness = createSetupHarness([{ allPresent: true, missing: [], installed: [] }]);
  await harness.window.VoltSetupReady;
  assert.deepEqual(harness.calls.map(call => call.method), ['checkDefaultPlans']);
  assert.equal(harness.elements.get('setup-overlay').classList.contains('hidden'), true);
});

test('setup read errors expose retry and never expose restore until a successful check', async () => {
  const harness = createSetupHarness([
    new Error('read failed'),
    new Error('read failed again'),
    { allPresent: true, missing: [], installed: [] },
  ]);
  await settle();
  assert.equal(harness.elements.get('btn-setup-retry').classList.contains('hidden'), false);
  assert.equal(harness.elements.get('btn-setup-install').classList.contains('hidden'), true);
  assert.equal(harness.elements.get('setup-status').textContent, 'setup_check_error');
  assert.equal(harness.document.activeElement.id, 'btn-setup-retry');

  harness.elements.get('btn-setup-retry').click();
  await harness.window.VoltSetupReady;
  assert.equal(harness.calls.filter(call => call.method === 'checkDefaultPlans').length, 3);
});

test('setup is already hidden when association releases the next dialog', async () => {
  const guid = '11111111-1111-4111-8111-111111111111';
  const harness = createSetupHarness([
    { allPresent: false, missing: ['PowerSaver'], installed: [{ planId: null, guid, name: 'Existing copy' }] },
    { allPresent: true, missing: [], installed: [] },
  ]);
  await settle();
  harness.window.setTimeout = () => 1;
  const select = harness.elements.get('setup-association-list').querySelectorAll('select[data-plan-id]')[0];
  select.value = guid;
  select.dispatch('change');
  harness.elements.get('btn-setup-associate').click();
  await harness.window.VoltSetupReady;
  assert.equal(harness.elements.get('setup-overlay').classList.contains('hidden'), true);
});

test('a later successful recheck also closes an already completed setup', async () => {
  const harness = createSetupHarness([
    { allPresent: true, missing: [], installed: [] },
    { allPresent: false, missing: ['PowerSaver'], installed: [] },
    { allPresent: true, missing: [], installed: [] },
  ]);
  await harness.window.VoltSetupReady;
  await harness.window.VoltSetup.recheck();
  assert.equal(harness.elements.get('setup-overlay').classList.contains('hidden'), false);
  await harness.window.VoltSetup.recheck();
  assert.equal(harness.elements.get('setup-overlay').classList.contains('hidden'), true);
});

test('selecting an existing plan blocks restore until the selection is applied or cleared', async () => {
  const guid = '11111111-1111-4111-8111-111111111111';
  const harness = createSetupHarness([
    { allPresent: false, missing: ['PowerSaver'], installed: [{ planId: null, guid, name: 'Existing copy' }] },
  ]);
  await settle();
  const select = harness.elements.get('setup-association-list').querySelectorAll('select[data-plan-id]')[0];
  select.value = guid;
  select.dispatch('change');
  assert.equal(harness.elements.get('btn-setup-install').disabled, true);
  harness.elements.get('btn-setup-install').click();
  await settle();
  assert.equal(harness.calls.some(call => call.method === 'restoreDefaultPlans'), false);
  select.value = '';
  select.dispatch('change');
  assert.equal(harness.elements.get('btn-setup-install').disabled, false);
});

test('setup can associate an explicit installed plan and readiness resolves only after recheck', async () => {
  const copyGuid = '11111111-1111-4111-8111-111111111111';
  const harness = createSetupHarness([
    {
      allPresent: false,
      missing: ['PowerSaver'],
      installed: [{ planId: null, guid: copyGuid, name: 'Existing saver copy' }],
    },
    { allPresent: true, missing: [], installed: [] },
  ]);
  await settle();

  const select = harness.elements.get('setup-association-list').querySelectorAll('select[data-plan-id]')[0];
  assert.ok(select, 'missing plan should get a native select');
  select.value = copyGuid;
  select.dispatch('change');
  harness.elements.get('btn-setup-associate').click();
  await harness.window.VoltSetupReady;

  const associationCall = harness.calls.find(call => call.method === 'associateDefaultPlans');
  assert.deepEqual(JSON.parse(JSON.stringify(associationCall.payload)), {
    associations: [{ planId: 'PowerSaver', guid: copyGuid }],
  });
  assert.equal(harness.calls.filter(call => call.method === 'checkDefaultPlans').length, 2);
});

test('restore success is followed by an authoritative default-plan recheck', async () => {
  const harness = createSetupHarness([
    { allPresent: false, missing: ['Performance'], installed: [] },
    { allPresent: true, missing: [], installed: [] },
  ]);
  await settle();

  harness.elements.get('btn-setup-install').click();
  await harness.window.VoltSetupReady;

  assert.deepEqual(harness.calls.map(call => call.method), [
    'checkDefaultPlans',
    'restoreDefaultPlans',
    'checkDefaultPlans',
  ]);
});

test('extra-plan startup consumes the single setup readiness promise instead of running a second default check', () => {
  assert.match(cleanupSource, /await window\.VoltSetupReady/);
  assert.doesNotMatch(cleanupSource, /Host\.call\('checkDefaultPlans'/);
  assert.doesNotMatch(cleanupSource, /waitForSetupToFinish|setupOverlay/);
});

test('cleanup action is relocated into the new Advanced view as one existing node', () => {
  assert.equal((html.match(/id="btn-clean-extra-plans"/g) || []).length, 1);
  assert.match(layoutSource, /id="vm-power-advanced-actions"/);
  assert.match(reorgSource, /move\(\$\('btn-clean-extra-plans'\), \$\('vm-power-advanced-actions'\)\)/);
});

test('duplicate reminder owns selection state, focus restoration, focus trap and a scrollable dialog body', () => {
  assert.match(html, /id="btn-extra-plans-delete"[^>]*disabled/);
  assert.match(html, /id="extra-plans-selected-count"/);
  assert.match(cleanupSource, /querySelectorAll\('input\[type="checkbox"\]:checked'\)\.length/);
  assert.match(cleanupSource, /btnDelete\.disabled = busy \|\| closing \|\| selected === 0/);
  assert.match(cleanupSource, /previousFocus.*focus/);
  assert.match(cleanupSource, /event\.key !== 'Tab'/);
  assert.match(css, /\.vm-dialog-shell\s*\{[\s\S]*max-height:\s*calc\(100dvh - 32px\)/);
  assert.match(css, /\.vm-dialog-body\s*\{[\s\S]*overflow-y:\s*auto/);
  assert.match(css, /\.vm-setup-association\.hidden[\s\S]*display:\s*none/);
});
