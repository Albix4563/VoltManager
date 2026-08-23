import assert from 'node:assert/strict';
import test from 'node:test';
import vm from 'node:vm';
import { readFile } from 'node:fs/promises';

const source = await readFile(
  new URL('../src/VoltManager/wwwroot/js/widgets.js', import.meta.url),
  'utf8',
);

function loadClockWidget() {
  const handlers = new Map();
  const timers = new Map();
  let nextTimer = 1;
  const root = { innerHTML: '' };
  const clock = { textContent: '' };
  const document = {
    documentElement: { dataset: {} },
    getElementById(id) {
      if (id === 'widget-root') return root;
      if (id === 'clock-time') return clock;
      return null;
    },
    querySelectorAll() { return []; },
  };
  const Host = {
    available: false,
    call() { return Promise.resolve({}); },
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
    location: { search: '?w=clock&s=mini' },
    URLSearchParams,
    Date,
    Intl,
    Promise,
    console,
    setInterval(handler, delay) {
      const id = nextTimer++;
      timers.set(id, { handler, delay });
      return id;
    },
    clearInterval(id) { timers.delete(id); },
  }));
  return {
    timers,
    languageChanged(data) {
      for (const handler of handlers.get('languageChanged') || []) handler(data);
    },
  };
}

test('clock widget keeps one timer across language changes', () => {
  const widget = loadClockWidget();

  for (const locale of ['en-US', 'es-ES', 'it-IT']) {
    widget.languageChanged({ language: locale.slice(0, 2), locale });
  }

  assert.equal(widget.timers.size, 1);
  assert.deepEqual([...widget.timers.values()].map(timer => timer.delay), [1000]);
});
