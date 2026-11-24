import assert from 'node:assert/strict';
import test from 'node:test';
import vm from 'node:vm';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const widgetsPath = fileURLToPath(new URL('../src/VoltManager/wwwroot/js/widgets.js', import.meta.url));
const source = await readFile(widgetsPath, 'utf8');

function createElement(tagName = 'DIV') {
  const classes = new Set();
  const attributes = new Map();
  const listeners = new Map();
  const el = {
    tagName,
    className: '',
    textContent: '',
    value: '50',
    hidden: false,
    focused: false,
    dataset: {},
    style: { setProperty(k, v) { this[k] = v; } },
    children: [],
    classList: {
      add(...names) { names.forEach(n => classes.add(n)); },
      remove(...names) { names.forEach(n => classes.delete(n)); },
      contains(n) { return classes.has(n); },
      toggle(n, force) {
        const on = force === undefined ? !classes.has(n) : !!force;
        if (on) classes.add(n); else classes.delete(n);
        return on;
      },
    },
    setAttribute(k, v) { attributes.set(k, String(v)); },
    getAttribute(k) { return attributes.has(k) ? attributes.get(k) : null; },
    addEventListener(name, fn) {
      if (!listeners.has(name)) listeners.set(name, []);
      listeners.get(name).push(fn);
    },
    dispatch(name, event = {}) {
      const ev = { target: el, preventDefault() {}, stopPropagation() {}, ...event };
      for (const fn of listeners.get(name) || []) fn(ev);
      return ev;
    },
    append(...nodes) { this.children.push(...nodes); },
    appendChild(node) { this.children.push(node); return node; },
    replaceChildren(...nodes) { this.children = [...nodes]; },
    focus() { this.focused = true; },
    closest() { return null; },
    querySelectorAll(sel) {
      if (sel === '.proc-bar > span') {
        const count = (String(this._html || '').match(/class="proc-bar"/g) || []).length;
        this._bars = Array.from({ length: count }, () => createElement('SPAN'));
        return this._bars;
      }
      return [];
    },
    querySelector() { return null; },
  };
  Object.defineProperty(el, 'innerHTML', {
    get() { return this._html || ''; },
    set(val) {
      this._html = String(val);
      if (this._onHtml) this._onHtml(this._html);
    },
  });
  return el;
}

function loadWidget(type, { size = 'medium', hostAvailable = true, hostCall } = {}) {
  const elements = new Map();
  const handlers = new Map();
  const docListeners = new Map();
  const calls = [];
  const timeouts = [];
  const intervals = [];
  let nextTimerId = 1;
  let planSelector = null;
  let planOptions = [];

  const root = createElement('DIV');
  root.id = 'widget-root';
  root._onHtml = (html) => {
    if (html.includes('plan-selector')) {
      planSelector = createElement('DIV');
      planOptions = [...html.matchAll(/data-plan="([^"]+)"/g)].map(m => {
        const btn = createElement('BUTTON');
        btn.dataset.plan = m[1];
        btn.setAttribute('aria-checked', 'false');
        return btn;
      });
    }
  };
  elements.set('widget-root', root);

  const document = {
    hidden: false,
    documentElement: createElement('HTML'),
    getElementById(id) {
      if (id === 'widget-keep-awake' && type !== 'plans') return null;
      if (!elements.has(id)) {
        const node = createElement('DIV');
        node.id = id;
        elements.set(id, node);
      }
      return elements.get(id);
    },
    createElement(tag) { return createElement(tag.toUpperCase()); },
    querySelector(sel) {
      if (sel === '.plan-selector') return planSelector;
      return null;
    },
    querySelectorAll(sel) {
      if (sel === '.plan-option') return planOptions;
      return [];
    },
    addEventListener(name, fn) {
      if (!docListeners.has(name)) docListeners.set(name, []);
      docListeners.get(name).push(fn);
    },
  };

  const Host = {
    available: hostAvailable,
    call(name, payload) {
      calls.push({
        name,
        payload: payload === undefined ? undefined : JSON.parse(JSON.stringify(payload)),
      });
      if (hostCall) return hostCall(name, payload);
      return Promise.resolve({});
    },
    on(name, fn) {
      if (!handlers.has(name)) handlers.set(name, []);
      handlers.get(name).push(fn);
    },
  };

  vm.runInContext(source, vm.createContext({
    window: {},
    document,
    Host,
    location: { search: `?w=${type}&s=${size}` },
    URLSearchParams,
    Date,
    Intl,
    Promise,
    Number,
    Array,
    Math,
    console,
    setTimeout(fn, delay) {
      const id = nextTimerId++;
      timeouts.push({ id, fn, delay });
      return id;
    },
    clearTimeout(id) {
      const idx = timeouts.findIndex(t => t.id === id);
      if (idx >= 0) timeouts.splice(idx, 1);
    },
    setInterval(fn, delay) {
      const id = nextTimerId++;
      intervals.push({ id, fn, delay });
      return id;
    },
    clearInterval(id) {
      const idx = intervals.findIndex(t => t.id === id);
      if (idx >= 0) intervals.splice(idx, 1);
    },
  }), { filename: widgetsPath });

  return {
    elements,
    handlers,
    calls,
    timeouts,
    intervals,
    get planSelector() { return planSelector; },
    get planOptions() { return planOptions; },
    emit(name, payload) {
      for (const fn of handlers.get(name) || []) fn(payload);
    },
    async flush() {
      await new Promise(resolve => setImmediate(resolve));
      await new Promise(resolve => setImmediate(resolve));
    },
  };
}

test('medium calendar widget renders full weekday headers and 42 day cells', () => {
  const widget = loadWidget('calendar', { size: 'medium', hostAvailable: false });
  const head = widget.elements.get('calendar-head');
  const grid = widget.elements.get('calendar-grid');

  assert.equal(head.children.length, 7);
  assert.equal(grid.children.length, 42);
  assert.ok(grid.children.some(c => c.classList.contains('is-today')));
  assert.ok(widget.elements.get('calendar-title').textContent.length > 0);
});

test('medium power widget polls active plan and CPU automation state on startup', async () => {
  const widget = loadWidget('power', {
    size: 'medium',
    hostCall: async (name) => {
      if (name === 'getBatteryPower') return { available: true, powerWatts: -12.4, batteryPercent: 80, timeKind: 'toEmpty', minutesRemaining: 95 };
      if (name === 'getActivePlan') return { planId: 'balanced' };
      if (name === 'getCpuAutomationState') return { averageCpu: 23.6, sampleIntervalSeconds: 5 };
      return {};
    },
  });
  await widget.flush();

  assert.equal(widget.elements.get('power-watts').textContent, '-12.4 W');
  assert.equal(widget.elements.get('power-plan').textContent, 'balanced');
  assert.equal(widget.elements.get('power-auto-cpu').textContent, '24%');
  assert.equal(widget.elements.get('power-auto-sample').textContent, '5s');
});

test('plans widget selects plans via click and arrow key navigation', async () => {
  const widget = loadWidget('plans', {
    size: 'medium',
    hostCall: async (name) => {
      if (name === 'getActivePlan') return { planId: 'powerSaver' };
      if (name === 'setManualOverride') return { success: true };
      return {};
    },
  });
  await widget.flush();

  assert.equal(widget.planOptions[0].getAttribute('aria-checked'), 'true');

  widget.planOptions[2].dispatch('click');
  await widget.flush();
  assert.deepEqual(
    widget.calls.filter(c => c.name === 'setManualOverride').map(c => c.payload),
    [{ plan: 'performance' }],
  );
  assert.equal(widget.planOptions[2].getAttribute('aria-checked'), 'true');

  widget.planSelector.dispatch('keydown', { key: 'ArrowLeft' });
  await widget.flush();
  assert.deepEqual(
    widget.calls.filter(c => c.name === 'setManualOverride').map(c => c.payload),
    [{ plan: 'performance' }, { plan: 'balanced' }],
  );
  assert.equal(widget.planOptions[1].focused, true);
  assert.equal(widget.planOptions[1].getAttribute('aria-checked'), 'true');
});

test('launcher widget queues concurrent reloads, retries missing icons, reloads on missing launch error, and handles launchersChanged', async () => {
  let resolveFirstLoad;
  let loadCount = 0;
  const widget = loadWidget('launcher', {
    size: 'medium',
    hostCall: (name) => {
      if (name === 'getLaunchers') {
        loadCount++;
        if (loadCount === 1) {
          return new Promise(resolve => { resolveFirstLoad = resolve; });
        }
        return Promise.resolve([
          { id: 'game-1', name: 'Hades', category: 'games', available: true },
        ]);
      }
      if (name === 'launchApp') {
        return Promise.resolve({ success: false, error: 'missing' });
      }
      return Promise.resolve({});
    },
  });

  // Trigger a second load while the first getLaunchers call is still pending (covers launcherReloadPending)
  widget.emit('launchersChanged');
  assert.equal(loadCount, 1);

  resolveFirstLoad([{ id: 'game-1', name: 'Hades', category: 'games', available: true }]);
  await widget.flush();
  assert.equal(loadCount, 2);

  // Missing icon scheduled retry timer (second load bumped attempt to 900ms); firing it reloads launchers
  assert.equal(widget.timeouts.length, 1);
  assert.equal(widget.timeouts[0].delay, 900);
  widget.timeouts[0].fn();
  await widget.flush();
  assert.equal(loadCount, 3);

  // Clicking a launcher tile delegates to launchTile and reloads on error === 'missing'
  const grid = widget.elements.get('launcher-grid');
  const fakeTile = createElement('BUTTON');
  fakeTile.dataset.launchId = 'game-1';
  grid.dispatch('click', {
    target: { closest: (sel) => (sel === '[data-launch-id]' ? fakeTile : null) },
  });
  await widget.flush();
  assert.equal(widget.calls.some(c => c.name === 'launchApp' && c.payload.id === 'game-1'), true);
  assert.equal(loadCount, 4);
  assert.equal(fakeTile.dataset.state, 'error');
});

test('actions widget handles quick action clicks, timer preset clicks, and executeAtUtc schedule countdown', async () => {
  const futureUtc = new Date(Date.now() + 95 * 1000).toISOString();
  const widget = loadWidget('actions', {
    size: 'medium',
    hostCall: async (name, payload) => {
      if (name === 'getKeepAwakeState') return { enabled: false };
      if (name === 'getGamingMode') return { active: false };
      if (name === 'getScheduledPowerAction') return { enabled: false };
      if (name === 'setKeepAwake') return { enabled: payload.enabled };
      if (name === 'schedulePowerAction') {
        return { enabled: true, mode: 'relative', action: 'shutdown', executeAtUtc: futureUtc };
      }
      if (name === 'cancelScheduledPowerAction') return { enabled: false };
      return {};
    },
  });
  await widget.flush();

  const grid = widget.elements.get('action-grid');
  const awakeBtn = { dataset: { action: 'awake' } };
  grid.dispatch('click', { target: { closest: () => awakeBtn } });
  await widget.flush();
  assert.equal(widget.calls.some(c => c.name === 'setKeepAwake' && c.payload.enabled === true), true);

  const timerChip = { dataset: { timer: '30' } };
  grid.dispatch('click', { target: { closest: () => timerChip } });
  await widget.flush();
  assert.deepEqual(
    widget.calls.find(c => c.name === 'schedulePowerAction')?.payload,
    { mode: 'relative', action: 'shutdown', delayMinutes: 30 },
  );
  assert.equal(widget.elements.get('action-timer').dataset.on, 'true');
  assert.match(widget.elements.get('action-timer-controls').innerHTML, /timer-countdown/);

  const cancelChip = { dataset: { timer: 'cancel' } };
  grid.dispatch('click', { target: { closest: () => cancelChip } });
  await widget.flush();
  assert.equal(widget.elements.get('action-timer').dataset.on, 'false');
});

test('processes widget renders rows and sets proc-bar widths', async () => {
  const widget = loadWidget('processes', {
    size: 'medium',
    hostCall: async (name) => {
      if (name === 'getTopProcesses') {
        return [
          { name: 'node.exe', instances: 2, ramMb: 512, cpuPercent: 18.4 },
          { name: 'chrome.exe', instances: 1, ramMb: 2048, cpuPercent: 5.25 },
        ];
      }
      return {};
    },
  });
  await widget.flush();

  const host = widget.elements.get('proc-list');
  assert.match(host.innerHTML, /node\.exe ×2/);
  assert.match(host.innerHTML, /18%/);
  assert.match(host.innerHTML, /5\.3%/);
  assert.deepEqual(host._bars.map(b => b.style.width), ['18.4%', '5.25%']);
});
