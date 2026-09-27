const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const { readFileSync } = require('node:fs');
const path = require('node:path');

const source = readFileSync(
    path.join(__dirname, '../../src/VoltManager/wwwroot/js/bridge.js'),
    'utf8');

function createBridge(session = 'session-a') {
    let messageHandler;
    let nextTimer = 1;
    let now = 1_000;
    const timers = new Map();
    const sent = [];
    const windowListeners = new Map();
    const documentEvents = [];

    class CustomEvent {
        constructor(type, options) {
            this.type = type;
            this.detail = options && options.detail;
        }
    }

    const document = {
        addEventListener() {},
        dispatchEvent(event) { documentEvents.push(event); },
    };
    const window = {
        crypto: { randomUUID: () => session },
        chrome: {
            webview: {
                addEventListener(name, handler) {
                    if (name === 'message') messageHandler = handler;
                },
                postMessage(message) { sent.push(message); },
            },
        },
        addEventListener(name, handler) {
            if (!windowListeners.has(name)) windowListeners.set(name, []);
            windowListeners.get(name).push(handler);
        },
        dispatchEvent(event) {
            for (const handler of windowListeners.get(event.type) || [])
                handler(event);
        },
    };

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
        console,
        setTimeout(handler, delay) {
            const id = nextTimer++;
            timers.set(id, { handler, delay });
            return id;
        },
        clearTimeout(id) { timers.delete(id); },
    });
    vm.runInContext(source, context);

    function fireTimer(id) {
        const timer = timers.get(id);
        assert.ok(timer, `missing timer ${id}`);
        timers.delete(id);
        timer.handler();
    }

    function fireFirstDelay(delay) {
        const found = [...timers.entries()].find(([, timer]) => timer.delay === delay);
        assert.ok(found, `missing timer with delay ${delay}`);
        fireTimer(found[0]);
    }

    function emitWindow(name, event) {
        for (const handler of windowListeners.get(name) || [])
            handler(event);
    }

    return {
        window,
        sent,
        timers,
        documentEvents,
        respond: data => messageHandler({ data }),
        fireTimer,
        fireFirstDelay,
        emitWindow,
        setNow(value) { now = value; },
    };
}

async function timeoutCall(bridge, method = 'backgroundWork') {
    const promise = bridge.window.Host.call(method, {}, { timeoutMs: 10, background: true });
    bridge.fireFirstDelay(10);
    await assert.rejects(promise, /Timeout:/);
}

test('request ids are unique across page sessions', () => {
    const first = createBridge('page-one');
    const second = createBridge('page-two');

    first.window.Host.call('getSettings');
    second.window.Host.call('getSettings');

    assert.equal(first.sent[0].id, 'rpc-page-one-1');
    assert.equal(second.sent[0].id, 'rpc-page-two-1');
    assert.notEqual(first.sent[0].id, second.sent[0].id);
});

test('stale response from a previous page session is ignored', async () => {
    const bridge = createBridge('current-page');
    const pending = bridge.window.Host.call('getSettings');
    const id = bridge.sent[0].id;

    bridge.respond({ id: 'rpc-previous-page-1', ok: true, result: 'stale' });
    bridge.respond({ id, ok: true, result: 'fresh' });

    assert.equal(await pending, 'fresh');
});

test('three consecutive rpc timeouts enter degraded and background work fails fast', async () => {
    const bridge = createBridge();

    await timeoutCall(bridge);
    await timeoutCall(bridge);
    await timeoutCall(bridge);
    const sentBefore = bridge.sent.length;

    await assert.rejects(
        bridge.window.Host.call('getTopProcesses', { count: 8 }),
        error => error && error.code === 'bridge_degraded');
    assert.equal(bridge.sent.length, sentBefore);

    const userAction = bridge.window.Host.call('setKeepAwake', { enabled: true }, { timeoutMs: 10 });
    assert.equal(bridge.sent.at(-1).method, 'setKeepAwake');
    bridge.respond({ id: bridge.sent.at(-1).id, ok: true, result: { enabled: true } });
    assert.deepEqual(await userAction, { enabled: true });
});

test('successful response resets consecutive timeout count', async () => {
    const bridge = createBridge();
    await timeoutCall(bridge);
    await timeoutCall(bridge);

    const healthy = bridge.window.Host.call('getSettings', {}, { timeoutMs: 10 });
    bridge.respond({ id: bridge.sent.at(-1).id, ok: true, result: {} });
    await healthy;

    await timeoutCall(bridge);
    await timeoutCall(bridge);
    const before = bridge.sent.length;
    const background = bridge.window.Host.call('getTopProcesses', { count: 8 }, { timeoutMs: 10 });
    assert.equal(bridge.sent.length, before + 1);
    bridge.respond({ id: bridge.sent.at(-1).id, ok: true, result: [] });
    assert.deepEqual(await background, []);
});

test('degraded probes use 1s 2s 5s backoff sequence', async () => {
    const bridge = createBridge();
    await timeoutCall(bridge);
    await timeoutCall(bridge);
    await timeoutCall(bridge);

    bridge.fireFirstDelay(1000);
    assert.equal(bridge.sent.at(-1).method, 'bridge.ping');
    bridge.fireFirstDelay(2000);
    await Promise.resolve();
    assert.ok([...bridge.timers.values()].some(timer => timer.delay === 2000));

    bridge.fireFirstDelay(2000);
    assert.equal(bridge.sent.at(-1).method, 'bridge.ping');
    bridge.fireFirstDelay(2000);
    await Promise.resolve();
    assert.ok([...bridge.timers.values()].some(timer => timer.delay === 5000));

    bridge.fireFirstDelay(5000);
    assert.equal(bridge.sent.at(-1).method, 'bridge.ping');
});

test('probe recovery emits reconnected once and requests fresh host state', async () => {
    const bridge = createBridge();
    let reconnects = 0;
    bridge.window.Host.on('bridge:reconnected', () => reconnects++);
    await timeoutCall(bridge);
    await timeoutCall(bridge);
    await timeoutCall(bridge);

    bridge.fireFirstDelay(1000);
    const ping = bridge.sent.at(-1);
    assert.equal(ping.method, 'bridge.ping');
    bridge.respond({ id: ping.id, ok: true, result: { alive: true } });
    await Promise.resolve();

    assert.equal(reconnects, 1);
    assert.equal(bridge.sent.at(-1).method, 'bridge.requestFreshState');
    const fresh = bridge.sent.at(-1);
    bridge.respond({ id: fresh.id, ok: true, result: { published: true } });

    const normal = bridge.window.Host.call('getSettings');
    bridge.respond({ id: bridge.sent.at(-1).id, ok: true, result: {} });
    await normal;
    assert.equal(reconnects, 1);
    assert.equal(
        bridge.documentEvents.filter(event => event.type === 'bridge:reconnected').length,
        1);
});

test('late response after timeout is ignored', async () => {
    const bridge = createBridge();
    const old = bridge.window.Host.call('oldMethod', {}, { timeoutMs: 10 });
    const oldId = bridge.sent.at(-1).id;
    bridge.fireFirstDelay(10);
    await assert.rejects(old, /Timeout:/);

    bridge.respond({ id: oldId, ok: true, result: 'too-late' });
    const current = bridge.window.Host.call('newMethod');
    bridge.respond({ id: bridge.sent.at(-1).id, ok: true, result: 'current' });
    assert.equal(await current, 'current');
});

test('javascript error forwarding is limited to 20 per minute plus one summary', () => {
    const bridge = createBridge();

    for (let i = 0; i < 30; i++)
        bridge.emitWindow('error', { message: `boom-${i}`, error: new Error(`boom-${i}`) });

    const logs = bridge.sent.filter(message => message.method === 'logError');
    assert.equal(logs.length, 21);
    assert.match(logs.at(-1).payload.message, /rate limit reached/);

    bridge.setNow(62_000);
    bridge.emitWindow('error', { message: 'new-window', error: new Error('new-window') });
    assert.equal(
        bridge.sent.filter(message => message.method === 'logError').length,
        22);
});
