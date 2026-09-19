import assert from 'node:assert/strict';
import test from 'node:test';
import vm from 'node:vm';
import { readFile } from 'node:fs/promises';

const source = await readFile(
  new URL('../src/VoltManager/wwwroot/js/bridge.js', import.meta.url),
  'utf8',
);

function loadBridge() {
  let messageHandler;
  let nextTimer = 1;
  const timers = new Map();
  const sent = [];
  const document = {
    addEventListener() {},
    dispatchEvent() {},
  };
  const window = {
    chrome: {
      webview: {
        addEventListener(name, handler) {
          if (name === 'message') messageHandler = handler;
        },
        postMessage(message) { sent.push(message); },
      },
    },
    addEventListener() {},
  };
  const context = vm.createContext({
    window,
    document,
    CustomEvent: class {},
    Error,
    Map,
    Promise,
    console,
    setTimeout(handler) {
      const id = nextTimer++;
      timers.set(id, handler);
      return id;
    },
    clearTimeout(id) { timers.delete(id); },
  });
  vm.runInContext(source, context);
  return { window, sent, timers, respond: data => messageHandler({ data }) };
}

test('bridge clears a resolved RPC timeout immediately', async () => {
  const bridge = loadBridge();
  const result = bridge.window.Host.call('getSettings');

  assert.equal(bridge.timers.size, 1);
  bridge.respond({ id: bridge.sent[0].id, ok: true, result: { language: 'it' } });

  assert.deepEqual(await result, { language: 'it' });
  assert.equal(bridge.timers.size, 0);
});

test('bridge clears a rejected RPC timeout immediately', async () => {
  const bridge = loadBridge();
  const result = bridge.window.Host.call('getSettings');

  bridge.respond({ id: bridge.sent[0].id, ok: false, error: 'failure' });

  await assert.rejects(result, /failure/);
  assert.equal(bridge.timers.size, 0);
});

test('bridge resolves concurrent RPC replies by id even when replies arrive out of order', async () => {
  const bridge = loadBridge();
  const first = bridge.window.Host.call('firstMethod');
  const second = bridge.window.Host.call('secondMethod');

  bridge.respond({ id: bridge.sent[1].id, ok: true, result: 'second' });
  bridge.respond({ id: bridge.sent[0].id, ok: true, result: 'first' });

  assert.equal(await first, 'first');
  assert.equal(await second, 'second');
  assert.equal(bridge.timers.size, 0);
});

test('bridge event subscriptions stop receiving events after unsubscribe', () => {
  const bridge = loadBridge();
  const received = [];
  const unsubscribe = bridge.window.Host.on('themeChanged', data => received.push(data.themeColor));

  bridge.respond({ event: 'themeChanged', data: { themeColor: 'red' } });
  unsubscribe();
  bridge.respond({ event: 'themeChanged', data: { themeColor: 'green' } });

  assert.deepEqual(received, ['red']);
});
