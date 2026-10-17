import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const status = readFileSync(new URL('../src/VoltManager/wwwroot/js/ui-reorganization.status.js', import.meta.url), 'utf8');

test('status projection listens for host metrics without observing metric text nodes', () => {
    const observed = [];
    const listeners = new Map();
    const subscriptions = new Map();
    const observers = [];
    let frames = 0;
    const ids = new Map();
    for (const id of ['plan-control', 'manual-override-chip', 'monitoring-label', 'cpu-pct', 'gpu-pct', 'ram-pct', 'disk-pct', 'power-flow-percent', 'power-flow-status', 'schedule-active', 'master-toggle']) {
        ids.set(id, { id, classList: { contains: () => false }, textContent: '--', getAttribute: () => null });
    }
    const api = { ready: true, t: value => value, activateView() {}, activateSubview() {} };
    const context = vm.createContext({
        window: { VoltUiReorg: api, Host: { available: false, on: (name, handler) => subscriptions.set(name, handler) } },
        Host: { available: false, on: (name, handler) => subscriptions.set(name, handler) },
        document: {
            hidden: false,
            body: { id: 'body' },
            getElementById: id => ids.get(id) || null,
            querySelectorAll: () => [],
            querySelector: () => null,
            addEventListener: (name, handler) => listeners.set(name, handler),
        },
        MutationObserver: class {
            constructor(callback) { this.callback = callback; this.nodes = []; observers.push(this); }
            observe(node, options) { this.nodes.push(node); observed.push({ node, options }); }
            fire(records) { this.callback(records); }
            disconnect() {}
        },
        requestAnimationFrame: callback => { frames++; callback(); },
        setInterval: () => 1,
        getComputedStyle: () => ({ display: 'block' }),
    });
    vm.runInContext(status, context);
    assert.equal(typeof subscriptions.get('metrics'), 'function');
    assert.equal(observed.filter(({ node }) => /^(cpu|gpu|ram|disk)-pct$/.test(node.id)).length, 0);
    assert.deepEqual(observed.filter(({ node }) => node.id === 'body').map(({ options }) => Object.keys(options)), [['childList', 'subtree']]);
    const before = frames;
    ids.get('cpu-pct').textContent = '20%';
    for (const observer of observers) {
        if (observer.nodes.some(node => node === ids.get('cpu-pct'))) observer.fire([{ target: ids.get('cpu-pct') }]);
    }
    assert.equal(frames, before, 'metric DOM mutation must not enqueue rendering');
    subscriptions.get('metrics')({ cpu: 20 });
    assert.equal(frames, before + 1);
    assert.equal(typeof listeners.get('visibilitychange'), 'function');
});

test('decorative theme pulse follows the reduced-motion and performance tiers', () => {
    const css = readFileSync(new URL('../src/VoltManager/wwwroot/css/effects.css', import.meta.url), 'utf8');
    for (const selector of ['html:not([data-fx="rich"]) .theme-pulse-dot', 'html[data-perf="lite"] .theme-pulse-dot', 'html[data-perf-tier="balanced"] .theme-pulse-dot', 'html[data-perf-tier="lite"] .theme-pulse-dot']) {
        assert.ok(css.includes(selector), selector);
    }
    assert.match(css, /@media\s*\(prefers-reduced-motion:\s*reduce\)[\s\S]*?\.theme-pulse-dot,[\s\S]*?animation:\s*none\s*!important/);
});
