import assert from 'node:assert/strict';
import test from 'node:test';
import vm from 'node:vm';
import { readFile } from 'node:fs/promises';

const source = await readFile(
  new URL('../src/VoltManager/wwwroot/js/widgets.js', import.meta.url),
  'utf8',
);

function loadWidget(type = 'clock', initialTime = new Date(2026, 8, 5, 12, 34, 17).getTime()) {
  const handlers = new Map();
  const events = new Map();
  const timers = new Map();
  const elements = new Map();
  const calls = [];
  const powerReplies = [];
  let nextTimer = 1;
  let now = initialTime;
  class ClockDate extends Date {
    constructor(...args) { super(...(args.length ? args : [now])); }
    static now() { return now; }
  }
  const document = {
    hidden: false,
    documentElement: { dataset: {} },
    getElementById(id) {
      if (id === 'widget-keep-awake' && type !== 'plans') return null;
      if (!elements.has(id)) elements.set(id, {
        innerHTML: '', textContent: '', dataset: {}, style: {},
        addEventListener() {}, setAttribute() {},
        classList: { toggle() {} },
      });
      return elements.get(id);
    },
    querySelectorAll() { return []; },
    querySelector() { return null; },
    addEventListener(name, handler) { events.set(name, handler); },
  };
  const Host = {
    available: false,
    call(name) {
      calls.push(name);
      if (name === 'getBatteryPower') return new Promise(resolve => powerReplies.push(resolve));
      return Promise.resolve({});
    },
    on(name, handler) {
      if (!handlers.has(name)) handlers.set(name, []);
      handlers.get(name).push(handler);
    },
  };
  const window = {};
  vm.runInContext(source, vm.createContext({
    window,
    document,
    Host,
    location: { search: `?w=${type}&s=mini` },
    URLSearchParams,
    Date: ClockDate,
    Intl,
    Promise,
    console,
    setInterval(handler, delay) {
      const id = nextTimer++;
      timers.set(id, { handler, delay });
      return id;
    },
    clearInterval(id) { timers.delete(id); },
    setTimeout(handler, delay) {
      const id = nextTimer++;
      timers.set(id, { handler, delay, once: true });
      return id;
    },
    clearTimeout(id) { timers.delete(id); },
  }));
  return {
    timers,
    elements,
    calls,
    handlers,
    fireTimer() {
      const [id, timer] = timers.entries().next().value;
      if (timer.once) timers.delete(id);
      timer.handler();
    },
    setNow(value) { now = value; },
    setHidden(value) { document.hidden = value; events.get('visibilitychange')(); },
    profile(profile) { for (const handler of handlers.get('resourceProfileChanged') || []) handler({ profile }); },
    async finishPowerRead() {
      powerReplies.shift()({ available: false });
      await new Promise(setImmediate);
    },
    languageChanged(data) {
      for (const handler of handlers.get('languageChanged') || []) handler(data);
    },
  };
}

test('clock widget keeps one timer across language changes', () => {
  const widget = loadWidget();

  for (const locale of ['en-US', 'es-ES', 'it-IT']) {
    widget.languageChanged({ language: locale.slice(0, 2), locale });
  }

  assert.equal(widget.timers.size, 1);
  assert.deepEqual([...widget.timers.values()].map(timer => timer.delay), [43000]);
});

test('clock wakes at the minute boundary and refreshes immediately after being hidden', () => {
  const widget = loadWidget();
  widget.setNow(new Date(2026, 8, 5, 12, 35).getTime());
  widget.fireTimer();
  assert.match(widget.elements.get('clock-time').textContent, /12:35/);
  assert.equal([...widget.timers.values()][0].delay, 60000);
  widget.setHidden(true);
  assert.equal(widget.timers.size, 0);
  widget.setNow(new Date(2026, 8, 5, 14, 1, 12).getTime());
  widget.setHidden(false);
  assert.match(widget.elements.get('clock-time').textContent, /14:01/);
  assert.equal([...widget.timers.values()][0].delay, 48000);
});

test('calendar rolls over at local midnight without reopening the widget', () => {
  const widget = loadWidget('calendar', new Date(2026, 8, 5, 23, 59, 55).getTime());
  assert.equal([...widget.timers.values()][0].delay, 5000);
  widget.setNow(new Date(2026, 8, 6).getTime());
  widget.fireTimer();
  assert.equal(widget.elements.get('calendar-mini-day').textContent, '06');
  assert.equal(widget.timers.size, 1);
});

test('power widget adapts its timer, prevents overlapping reads, and parks while hidden', async () => {
  const widget = loadWidget('power');
  assert.equal([...widget.timers.values()][0].delay, 5000);
  for (let i = 0; i < 10; i++) widget.fireTimer();
  widget.profile('gaming');
  assert.equal([...widget.timers.values()][0].delay, 10000);
  assert.equal(widget.calls.filter(name => name === 'getBatteryPower').length, 1);
  await widget.finishPowerRead();
  widget.fireTimer();
  assert.equal(widget.calls.filter(name => name === 'getBatteryPower').length, 2);
  widget.profile('critical');
  assert.equal([...widget.timers.values()][0].delay, 15000);
  widget.setHidden(true);
  assert.equal(widget.timers.size, 0);
  widget.setHidden(false);
  assert.equal(widget.timers.size, 1);
});

test('language changes never accumulate power-plan event listeners', () => {
  const widget = loadWidget('plans');
  for (const locale of ['en-US', 'es-ES', 'it-IT'])
    widget.languageChanged({ language: locale.slice(0, 2), locale });
  assert.equal(widget.handlers.get('activePlanChanged').length, 1);
  assert.equal(widget.timers.size, 0);
});
