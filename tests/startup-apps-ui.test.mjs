import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../src/VoltManager/wwwroot/js/app.js', import.meta.url), 'utf8');
const systemHandlers = source.slice(
  source.indexOf('    function setSystemStatus('),
  source.indexOf('    async function scheduleRelativeAction('),
);

function createHarness(method, response) {
  const classes = new Set(['hidden']);
  const status = {
    textContent: '',
    classList: {
      add: (...names) => names.forEach(name => classes.add(name)),
      remove: (...names) => names.forEach(name => classes.delete(name)),
    },
  };
  const handlers = new Map();
  const calls = [];
  let refreshes = 0;
  const document = {
    addEventListener(name, handler) { handlers.set(name, handler); },
    getElementById(id) { return id === 'system-status' ? status : null; },
  };
  const Host = {
    available: true,
    async call(name, payload) {
      calls.push({ name, payload });
      if (name !== method) throw new Error(`Unexpected RPC ${name}`);
      if (response instanceof Error) throw response;
      return response;
    },
  };
  const context = vm.createContext({
    document, Host, window: {}, setTimeout() {},
    t: key => key,
    async loadStartupApps() { refreshes += 1; },
  });
  vm.runInContext(`let systemWired = false; ${systemHandlers} wireSystemUi();`, context);

  async function click(selector, dataset) {
    const button = {
      disabled: false,
      dataset,
      closest(candidate) { return candidate === selector ? this : null; },
    };
    await handlers.get('click')({ target: button });
    assert.equal(button.disabled, false, 'control must always be re-enabled');
  }

  return { click, status, classes, calls, get refreshes() { return refreshes; } };
}

test('removeStartupApp resolved false must not announce removal', async () => {
  const h = createHarness('removeStartupApp', { success: false });
  await h.click('[data-remove-startup-id]', { removeStartupId: 'Miliano App - removed' });
  assert.equal(h.calls.length, 1);
  assert.equal(h.status.textContent.startsWith('removeErr'), true);
  assert.equal(h.classes.has('err'), true);
  assert.notEqual(h.status.textContent, 'removed');
  assert.equal(h.refreshes, 0);
});

test('setStartupAppEnabled resolved false must not announce a changed state', async () => {
  const h = createHarness('setStartupAppEnabled', { success: false });
  await h.click('[data-toggle-startup-id]', { toggleStartupId: 'Item', toggleStartupEnabled: 'false' });
  assert.equal(h.calls.length, 1);
  assert.equal(h.status.textContent.startsWith('toggleErr'), true);
  assert.equal(h.classes.has('err'), true);
  assert.notEqual(h.status.textContent, 'toggled');
  assert.equal(h.refreshes, 0);
});

test('successful remove and toggle still announce success and refresh inventory', async () => {
  for (const { method, selector, dataset, status } of [
    { method: 'removeStartupApp', selector: '[data-remove-startup-id]', dataset: { removeStartupId: 'Item' }, status: 'removed' },
    { method: 'setStartupAppEnabled', selector: '[data-toggle-startup-id]', dataset: { toggleStartupId: 'Item', toggleStartupEnabled: 'true' }, status: 'toggled' },
  ]) {
    const h = createHarness(method, { success: true });
    await h.click(selector, dataset);
    assert.equal(h.status.textContent, status);
    assert.equal(h.classes.has('ok'), true);
    assert.equal(h.refreshes, 1);
  }
});

test('rejected remove and toggle RPC still use the existing error path', async () => {
  for (const { method, selector, dataset, prefix } of [
    { method: 'removeStartupApp', selector: '[data-remove-startup-id]', dataset: { removeStartupId: 'Item' }, prefix: 'removeErr' },
    { method: 'setStartupAppEnabled', selector: '[data-toggle-startup-id]', dataset: { toggleStartupId: 'Item', toggleStartupEnabled: 'true' }, prefix: 'toggleErr' },
  ]) {
    const h = createHarness(method, new Error('synthetic RPC rejection'));
    await h.click(selector, dataset);
    assert.equal(h.status.textContent, `${prefix}synthetic RPC rejection`);
    assert.equal(h.classes.has('err'), true);
    assert.equal(h.refreshes, 0);
  }
});
