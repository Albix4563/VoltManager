import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const read = relative => readFileSync(path.join(root, relative), 'utf8');

const jsDirectory = path.join(root, 'src/VoltManager/wwwroot/js');
const jsSources = readdirSync(jsDirectory)
  .filter(file => file.endsWith('.js'))
  .map(file => readFileSync(path.join(jsDirectory, file), 'utf8'));
const frontend = jsSources.join('\n');

const handlersDirectory = path.join(root, 'src/VoltManager/Bridge/Handlers');
const handlerSources = readdirSync(handlersDirectory)
  .filter(file => file.endsWith('.cs'))
  .map(file => readFileSync(path.join(handlersDirectory, file), 'utf8'))
  .join('\n');

const eventSource = read('src/VoltManager/Bridge/BridgeEventNames.cs');

function registeredMethods() {
  return [...handlerSources.matchAll(
    /static readonly string\[\] (?:RegisteredMethods|MethodNames)\s*=\s*\[([\s\S]*?)\];/g
  )].flatMap(block => [...block[1].matchAll(/"([^"]+)"/g)].map(match => match[1]));
}

function frontendCalls() {
  return [...new Set(
    [...frontend.matchAll(/Host\.call\(\s*['"]([^'"]+)['"]/g)].map(match => match[1])
  )];
}

function frontendSubscriptions() {
  const direct = [...frontend.matchAll(/Host\.on\(\s*['"]([^'"]+)['"]/g)].map(match => match[1]);
  const viaHelper = [...frontend.matchAll(/\bonHost\(\s*['"]([^'"]+)['"]/g)].map(match => match[1]);
  return [...new Set([...direct, ...viaHelper])];
}

function eventInventory() {
  const constants = [...eventSource.matchAll(/public const string (\w+) = "([^"]+)";/g)]
    .map(match => ({ name: match[1], value: match[2] }));
  const allBlock = eventSource.match(/Array\.AsReadOnly\(new\[\]\s*\{([\s\S]*?)\}\);/)?.[1];
  assert.ok(allBlock, 'BridgeEventNames.All inventory must exist');
  const allNames = [...allBlock.matchAll(/\b[A-Z]\w*\b/g)].map(match => match[0]);
  return { constants, allNames };
}

function createBridge({ profile, now = 1000 } = {}) {
  const messageHandlers = [];
  const sent = [];
  const documentEvents = [];
  const windowListeners = new Map();
  const timers = new Map();
  let nextTimer = 1;

  const document = {
    addEventListener() {},
    dispatchEvent(event) { documentEvents.push(event); },
  };
  const window = {
    crypto: { randomUUID: () => 'bridge-contract-session' },
    chrome: {
      webview: {
        addEventListener(name, handler) {
          if (name === 'message') messageHandlers.push(handler);
        },
        postMessage(message) { sent.push(message); },
      },
    },
    addEventListener(name, handler) {
      if (!windowListeners.has(name)) windowListeners.set(name, []);
      windowListeners.get(name).push(handler);
    },
    dispatchEvent(event) {
      for (const handler of windowListeners.get(event.type) || []) handler(event);
    },
    VoltResourceProfile: profile,
  };

  class CustomEvent {
    constructor(type, options) {
      this.type = type;
      this.detail = options?.detail;
    }
  }

  const FakeDate = class extends Date {
    static now() { return now; }
  };

  const context = vm.createContext({
    window,
    document,
    CustomEvent,
    Date: FakeDate,
    Error,
    Map,
    Promise,
    Uint32Array,
    console: { error() {}, warn() {}, log() {} },
    setTimeout(handler, delay) {
      const id = nextTimer++;
      timers.set(id, { handler, delay });
      return id;
    },
    clearTimeout(id) { timers.delete(id); },
  });
  vm.runInContext(read('src/VoltManager/wwwroot/js/bridge.js'), context);

  return {
    window,
    sent,
    documentEvents,
    setProfile(value) { window.VoltResourceProfile = value; },
    setNow(value) { now = value; },
    emit(name, data) {
      for (const handler of messageHandlers) handler({ data: { event: name, data } });
    },
    respond(id, result) {
      for (const handler of messageHandlers) handler({ data: { id, ok: true, result } });
    },
  };
}

test('every frontend RPC method is registered by a native bridge handler', () => {
  const methods = new Set(registeredMethods());
  const missing = frontendCalls().filter(method => !methods.has(method));
  assert.deepEqual(missing, [], 'frontend calls without a native handler');
});

test('native RPC inventory is unique, non-empty and includes the bridge health probes', () => {
  const methods = registeredMethods();
  assert.ok(methods.length >= 105, `expected a broad RPC inventory, got ${methods.length}`);
  assert.equal(new Set(methods).size, methods.length, 'duplicate RPC registration would break dispatcher construction');
  for (const method of methods) {
    assert.match(method, /^[a-z][A-Za-z0-9.]*$/, `unexpected RPC method shape: ${method}`);
  }
  assert.ok(methods.includes('bridge.ping'));
  assert.ok(methods.includes('bridge.requestFreshState'));
});

test('bridge health probes are the only dotted RPC methods', () => {
  const dotted = [...new Set(registeredMethods().filter(method => method.includes('.')))].sort();
  assert.deepEqual(dotted, ['bridge.ping', 'bridge.requestFreshState']);
});

test('every frontend host event subscription belongs to the published event contract', () => {
  const published = new Set(eventInventory().constants.map(entry => entry.value));
  const unknown = frontendSubscriptions().filter(name => !published.has(name));
  assert.deepEqual(unknown, [], 'frontend subscribes to events the host never publishes');
});

test('every published host event is consumed by the frontend', () => {
  const { constants } = eventInventory();
  const published = constants.map(entry => entry.value).sort();
  const subscribed = frontendSubscriptions().sort();
  assert.deepEqual(subscribed, published);
});

test('event constants and the All inventory expose the same unique camelCase contract', () => {
  const { constants, allNames } = eventInventory();
  assert.equal(constants.length, 31);
  assert.equal(new Set(constants.map(entry => entry.value)).size, constants.length, 'duplicate event value');
  assert.equal(new Set(constants.map(entry => entry.name)).size, constants.length, 'duplicate constant name');
  assert.deepEqual(
    allNames.slice().sort(),
    constants.map(entry => entry.name).sort(),
    'BridgeEventNames.All must list every declared event exactly once'
  );
  for (const { name, value } of constants) {
    assert.match(value, /^[a-z][A-Za-z0-9]*$/, `${name} is not camelCase`);
  }
});

test('host event listeners are isolated, can be removed once and still notify the document', () => {
  const bridge = createBridge();
  const calls = [];
  const unsubscribeFirst = bridge.window.Host.on('demoEvent', value => calls.push(`first:${value}`));
  bridge.window.Host.on('demoEvent', () => { throw new Error('listener boom'); });
  bridge.window.Host.on('demoEvent', value => calls.push(`last:${value}`));

  bridge.emit('demoEvent', 1);
  assert.deepEqual(calls, ['first:1', 'last:1']);

  unsubscribeFirst();
  bridge.emit('demoEvent', 2);
  assert.deepEqual(calls, ['first:1', 'last:1', 'last:2']);

  unsubscribeFirst();
  bridge.emit('demoEvent', 3);
  assert.deepEqual(calls, ['first:1', 'last:1', 'last:2', 'last:3']);
  assert.equal(bridge.documentEvents.filter(event => event.type === 'demoEvent').length, 3);
});

test('Host.fail surfaces the message once, returns it and never masks the original failure', () => {
  const bridge = createBridge();
  const shown = [];

  assert.equal(
    bridge.window.Host.fail(new Error('bridge exploded'), (message, isError) => shown.push([message, isError])),
    'bridge exploded'
  );
  assert.deepEqual(shown, [['bridge exploded', true]]);
  assert.equal(bridge.window.Host.fail(null), 'Error');
  assert.equal(
    bridge.window.Host.fail(new Error('still returned'), () => { throw new Error('status UI broke'); }),
    'still returned'
  );
});

test('top-process polling caches within the resource interval and honors the disabled policy', async () => {
  const bridge = createBridge({
    profile: { allowProcessPolling: true, processPollingIntervalMs: 3000 },
  });

  const first = bridge.window.Host.call('getTopProcesses', { count: 8 });
  assert.equal(bridge.sent.length, 1);
  bridge.respond(bridge.sent[0].id, [{ name: 'alpha' }]);
  assert.deepEqual(await first, [{ name: 'alpha' }]);

  assert.deepEqual(await bridge.window.Host.call('getTopProcesses', { count: 8 }), [{ name: 'alpha' }]);
  assert.equal(bridge.sent.length, 1, 'cached result must not force native process enumeration');

  bridge.setNow(4000);
  const third = bridge.window.Host.call('getTopProcesses', { count: 8 });
  assert.equal(bridge.sent.length, 2);
  bridge.respond(bridge.sent[1].id, [{ name: 'beta' }]);
  assert.deepEqual(await third, [{ name: 'beta' }]);

  bridge.setProfile({ allowProcessPolling: false, processPollingIntervalMs: 0 });
  assert.deepEqual(await bridge.window.Host.call('getTopProcesses', { count: 8 }), [{ name: 'beta' }]);
  assert.equal(bridge.sent.length, 2, 'disabled polling must return the cache without an RPC');
});

test('concurrent top-process requests share one native enumeration', async () => {
  const bridge = createBridge({
    profile: { allowProcessPolling: true, processPollingIntervalMs: 0 },
  });

  const first = bridge.window.Host.call('getTopProcesses', { count: 8 });
  const second = bridge.window.Host.call('getTopProcesses', { count: 8 });
  assert.equal(bridge.sent.length, 1, 'in-flight requests must be coalesced');

  bridge.respond(bridge.sent[0].id, [{ name: 'gamma' }]);
  const results = await Promise.all([first, second]);
  assert.deepEqual(results[0], [{ name: 'gamma' }]);
  assert.deepEqual(results[1], [{ name: 'gamma' }]);
});
