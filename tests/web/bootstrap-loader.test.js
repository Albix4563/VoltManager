const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const { readFileSync } = require('node:fs');
const path = require('node:path');

const assetVersionSource = readFileSync(
    path.join(__dirname, '../../src/VoltManager/wwwroot/js/asset-version.js'), 'utf8');
const loaderSource = readFileSync(
    path.join(__dirname, '../../src/VoltManager/wwwroot/js/bootstrap-loader.js'), 'utf8');

function createHarness() {
    const appendedScripts = [];
    const timers = [];
    const hostCalls = [];
    const windowListeners = new Map();
    let styleNode = null;

    const document = {
        readyState: 'loading',
        getElementById(id) {
            return id === 'vm-ui-reorganization-style' ? styleNode : null;
        },
        createElement(tag) {
            const node = {
                tagName: String(tag).toUpperCase(),
                dataset: {},
                parentNode: null,
                remove() {
                    if (this.tagName === 'SCRIPT') {
                        const index = appendedScripts.indexOf(this);
                        if (index >= 0) appendedScripts.splice(index, 1);
                    }
                    this.parentNode = null;
                },
            };
            return node;
        },
        querySelector(selector) {
            const scriptMatch = selector.match(/^script\[data-vm-reorg-src="(.+)"\]$/);
            if (scriptMatch)
                return appendedScripts.find(node => node.dataset.vmReorgSrc === scriptMatch[1]) || null;
            return null;
        },
        head: {
            insertBefore(node) {
                styleNode = node;
                node.parentNode = this;
            },
        },
        body: {
            appendChild(node) {
                node.parentNode = this;
                appendedScripts.push(node);
            },
        },
    };

    const window = {
        Host: {
            call(method, payload) {
                hostCalls.push({ method, payload });
                return Promise.resolve();
            },
        },
        addEventListener(name, handler) { windowListeners.set(name, handler); },
        setTimeout(handler, delay) { timers.push({ handler, delay }); return timers.length; },
    };
    const context = vm.createContext({ window, document, console, Promise, Error, encodeURIComponent });
    vm.runInContext(assetVersionSource, context);
    vm.runInContext(loaderSource, context);

    return { window, document, appendedScripts, timers, hostCalls };
}

test('failed reorganization script is removed and a fresh node succeeds on retry', async () => {
    const harness = createHarness();
    const promise = harness.window.VoltBootstrapLoader.loadScriptWithRetry('js/example.js', {
        setTimeout(handler, delay) { harness.timers.push({ handler, delay }); },
    });

    const first = harness.appendedScripts[0];
    first.onerror();
    assert.equal(first.parentNode, null, 'failed script node must be removed');
    assert.equal(harness.appendedScripts.length, 0);
    assert.equal(harness.timers[0].delay, 250);

    harness.timers.shift().handler();
    const second = harness.appendedScripts[0];
    assert.notEqual(second, first, 'retry must create a new script node');
    second.onload();
    assert.equal(await promise, second);
});

test('exhausting reorganization retries reports through the host logError bridge', async () => {
    const harness = createHarness();
    const promise = harness.window.VoltBootstrapLoader.loadReorganization({
        setTimeout(handler, delay) { harness.timers.push({ handler, delay }); },
    });

    for (let attempt = 0; attempt < 3; attempt += 1) {
        const script = harness.appendedScripts[0];
        assert.ok(script, `missing attempt ${attempt + 1}`);
        script.onerror();
        if (attempt < 2) harness.timers.shift().handler();
    }

    assert.equal(await promise, false);
    assert.equal(harness.appendedScripts.length, 0);
    assert.equal(harness.hostCalls.length, 1);
    assert.equal(harness.hostCalls[0].method, 'logError');
    assert.match(harness.hostCalls[0].payload.message, /after 3 attempts/);
});
