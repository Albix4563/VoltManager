import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(
  new URL('../src/VoltManager/wwwroot/js/settings-preferences-tools.js', import.meta.url),
  'utf8'
);

function createElement(id, initialOn) {
  const listeners = new Map();
  return {
    id,
    dataset: initialOn === undefined ? {} : { on: initialOn ? 'true' : 'false' },
    textContent: '',
    addEventListener(type, handler) {
      if (!listeners.has(type)) listeners.set(type, new Set());
      listeners.get(type).add(handler);
    },
    removeEventListener(type, handler) {
      listeners.get(type)?.delete(handler);
    },
    async trigger(type, event = {}) {
      const handlers = [...(listeners.get(type) || [])];
      const results = handlers.map(handler => handler(event));
      await Promise.all(results);
      await new Promise(resolve => setImmediate(resolve));
    },
  };
}

function createHarness({
  startWithWindows = false,
  startMaximized = false,
  closeToTray = true,
  callHandler = async () => ({ success: true }),
} = {}) {
  const elements = new Map([
    ['pref-autostart', createElement('pref-autostart')],
    ['toggle-autostart', createElement('toggle-autostart', false)],
    ['pref-start-maximized', createElement('pref-start-maximized')],
    ['toggle-start-maximized', createElement('toggle-start-maximized', false)],
    ['pref-tray', createElement('pref-tray')],
    ['toggle-tray', createElement('toggle-tray', true)],
    ['btn-export-settings', createElement('btn-export-settings')],
    ['btn-import-settings', createElement('btn-import-settings')],
    ['btn-export-diagnostics', createElement('btn-export-diagnostics')],
    ['btn-open-logs', createElement('btn-open-logs')],
    ['update-status', createElement('update-status')],
  ]);

  const documentListeners = new Map();
  const document = {
    getElementById(id) {
      return elements.get(id) || null;
    },
    addEventListener(type, handler) {
      if (!documentListeners.has(type)) documentListeners.set(type, new Set());
      documentListeners.get(type).add(handler);
    },
    removeEventListener(type, handler) {
      documentListeners.get(type)?.delete(handler);
    },
    dispatchEvent(event) {
      for (const handler of [...(documentListeners.get(event.type) || [])]) {
        handler(event);
      }
    },
  };

  const settings = {
    startWithWindows,
    startMaximized,
    closeToTray,
  };

  const calls = [];
  const statuses = [];
  let registered = null;

  const Host = {
    available: true,
    async call(method, payload) {
      const normalizedPayload = payload === undefined
        ? undefined
        : JSON.parse(JSON.stringify(payload));
      calls.push({ method, payload: normalizedPayload });
      return callHandler(method, normalizedPayload);
    },
    fail(err, cb) {
      const msg = err && err.message ? err.message : 'Error';
      if (cb) cb(msg, true);
      return msg;
    },
  };

  const VoltSettingsCore = {
    setToggle(el, on) {
      if (el) el.dataset.on = on ? 'true' : 'false';
    },
    setStatus(msg, isError) {
      statuses.push({ msg: String(msg), isError: !!isError });
    },
    tr(_key, fallback) {
      return fallback || '';
    },
    lt(key) {
      return key === 'err' ? 'Error: ' : key;
    },
  };

  const VoltViewLifecycle = {
    register(id, descriptor) {
      registered = { id, ...descriptor };
    },
  };

  const window = {
    Host,
    VoltSettingsCore,
    VoltViewLifecycle,
    __voltSettings: {
      get: () => settings,
      startWithWindows,
    },
    location: { reload() {} },
  };
  window.window = window;

  const context = vm.createContext({
    window,
    document,
    Host,
    VoltSettingsCore,
    VoltViewLifecycle,
    location: window.location,
    console: { error() {}, warn() {}, log() {} },
  });

  vm.runInContext(source, context);
  assert.ok(registered, 'settings-preferences-tools must register with VoltViewLifecycle');
  assert.equal(registered.id, 'settings-preferences-tools');
  assert.equal(registered.matches({ view: 'settings' }), true);
  assert.equal(registered.matches({ view: 'overview' }), false);

  registered.init();
  registered.activate();

  return {
    window,
    document,
    settings,
    elements,
    calls,
    statuses,
    registered,
  };
}

test('pref-autostart updates both __voltSettings and settings and persists across re-sync', async () => {
  const h = createHarness({
    startWithWindows: false,
    callHandler: async (_method, payload) => ({ success: true, enabled: payload.enabled }),
  });

  assert.equal(h.elements.get('toggle-autostart').dataset.on, 'false');

  await h.elements.get('pref-autostart').trigger('click');

  assert.deepEqual(h.calls[0], {
    method: 'setStartWithWindows',
    payload: { enabled: true },
  });
  assert.equal(h.elements.get('toggle-autostart').dataset.on, 'true');
  assert.equal(h.window.__voltSettings.startWithWindows, true);
  assert.equal(h.settings.startWithWindows, true);

  h.registered.activate();
  h.document.dispatchEvent({ type: 'settingsloaded' });
  assert.equal(h.elements.get('toggle-autostart').dataset.on, 'true');

  await h.elements.get('pref-autostart').trigger('click');

  assert.deepEqual(h.calls[1], {
    method: 'setStartWithWindows',
    payload: { enabled: false },
  });
  assert.equal(h.elements.get('toggle-autostart').dataset.on, 'false');
  assert.equal(h.window.__voltSettings.startWithWindows, false);
  assert.equal(h.settings.startWithWindows, false);

  h.registered.activate();
  assert.equal(h.elements.get('toggle-autostart').dataset.on, 'false');
});

test('pref-autostart rolls back and aligns state when host returns success: false', async () => {
  const h = createHarness({
    startWithWindows: true,
    callHandler: async () => ({ success: false, enabled: true, message: 'Access denied' }),
  });

  assert.equal(h.elements.get('toggle-autostart').dataset.on, 'true');

  await h.elements.get('pref-autostart').trigger('click');

  assert.deepEqual(h.calls[0], {
    method: 'setStartWithWindows',
    payload: { enabled: false },
  });
  assert.equal(h.elements.get('toggle-autostart').dataset.on, 'true');
  assert.equal(h.window.__voltSettings.startWithWindows, true);
  assert.equal(h.settings.startWithWindows, true);
  assert.equal(h.statuses.some(entry => entry.isError), true);

  h.registered.activate();
  assert.equal(h.elements.get('toggle-autostart').dataset.on, 'true');
});

test('pref-autostart rolls back toggle when Host.call rejects', async () => {
  const h = createHarness({
    startWithWindows: false,
    callHandler: async () => {
      throw new Error('bridge unavailable');
    },
  });

  await h.elements.get('pref-autostart').trigger('click');

  assert.equal(h.elements.get('toggle-autostart').dataset.on, 'false');
  assert.equal(h.window.__voltSettings.startWithWindows, false);
  assert.equal(h.settings.startWithWindows, false);
  assert.equal(h.statuses.some(entry => entry.isError && entry.msg.includes('bridge unavailable')), true);
});

test('pref-start-maximized syncs initial value, persists clicks, and rolls back on failure', async () => {
  let shouldFail = false;
  const h = createHarness({
    startMaximized: true,
    callHandler: async () => {
      if (shouldFail) throw new Error('disk full');
      return { success: true };
    },
  });

  assert.equal(h.elements.get('toggle-start-maximized').dataset.on, 'true');

  await h.elements.get('pref-start-maximized').trigger('click');

  assert.deepEqual(h.calls[0], {
    method: 'setStartMaximized',
    payload: { enabled: false },
  });
  assert.equal(h.elements.get('toggle-start-maximized').dataset.on, 'false');
  assert.equal(h.settings.startMaximized, false);

  h.registered.activate();
  h.document.dispatchEvent({ type: 'settingsloaded' });
  assert.equal(h.elements.get('toggle-start-maximized').dataset.on, 'false');

  shouldFail = true;
  await h.elements.get('pref-start-maximized').trigger('click');

  assert.deepEqual(h.calls[1], {
    method: 'setStartMaximized',
    payload: { enabled: true },
  });
  assert.equal(h.elements.get('toggle-start-maximized').dataset.on, 'false');
  assert.equal(h.settings.startMaximized, false);
  assert.equal(h.statuses.some(entry => entry.isError && entry.msg.includes('disk full')), true);
});

test('dispose removes preference click listeners cleanly', async () => {
  const h = createHarness();
  h.registered.dispose();

  await h.elements.get('pref-autostart').trigger('click');
  await h.elements.get('pref-start-maximized').trigger('click');

  assert.equal(h.calls.length, 0);
});
