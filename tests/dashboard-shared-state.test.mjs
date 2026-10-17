import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const dashboard = readFileSync(new URL('../src/VoltManager/wwwroot/js/dashboard.js', import.meta.url), 'utf8');
const settingsHelpers = dashboard.slice(
    dashboard.indexOf('    function setMiniToggle('),
    dashboard.indexOf('    async function setGamingModeFromHome('),
);
const powerSourceControls = dashboard.slice(
    dashboard.indexOf("    powerSourcePlanHome?.addEventListener('click'"),
    dashboard.indexOf("    gamingModeHome?.addEventListener('click'"),
);
const stateBootstrap = dashboard.slice(
    dashboard.indexOf('    let powerSourcePlanRevision = 0;'),
    dashboard.indexOf("    document.addEventListener('langchanged', () => {", dashboard.indexOf('    let powerSourcePlanRevision = 0;')),
);

function createToggle() {
    return { dataset: {}, setAttribute(name, value) { this[name] = value; } };
}

function createControl() {
    const callbacks = new Map();
    return {
        addEventListener(name, callback) { callbacks.set(name, callback); },
        emit(name) { return callbacks.get(name)?.(); },
    };
}

function createHarness({ loaded = false, gamingActive = false, settings = {
    override: { plan: 'balanced' },
    powerSourcePlan: { enabled: false, lowBatteryThresholdPercent: 30 },
} } = {}) {
    const documentListeners = new Map();
    const hostListeners = new Map();
    const calls = [];
    const pending = [];
    const failures = [];
    const renderedOverrides = [];
    let saves = 0;
    const window = {
        __voltGamingMode: { isActive: () => gamingActive },
    };
    function publishSettings() {
        window.__voltSettings = { get: () => settings, save: () => saves++ };
    }
    if (loaded) publishSettings();

    const planToggle = createToggle();
    const gamingToggle = createToggle();
    const planControl = createControl();
    const threshold = { ...createControl(), value: '' };
    const context = vm.createContext({
        window,
        document: { addEventListener(name, handler) { documentListeners.set(name, handler); } },
        Host: {
            available: true,
            on(name, handler) { hostListeners.set(name, handler); },
            call(method, payload) {
                calls.push(method);
                if (method === 'getPowerSourcePlanState')
                    return new Promise((resolve, reject) => pending.push({ resolve, reject }));
                if (method === 'setPowerSourcePlanSwitch')
                    return Promise.resolve({ enabled: payload.enabled, lowBatteryThresholdPercent: settings.powerSourcePlan.lowBatteryThresholdPercent });
                throw new Error('Unexpected RPC: ' + method);
            },
            fail(err) { failures.push(err); },
        },
        powerSourcePlanHome: planControl,
        powerSourcePlanHomeToggle: planToggle,
        lowBatteryThresholdInput: threshold,
        gamingModeHomeToggle: gamingToggle,
        renderOverrideStatus(value) { renderedOverrides.push(value); },
    });
    vm.runInContext(settingsHelpers + powerSourceControls + stateBootstrap, context);
    return {
        window, settings, calls, pending, planToggle, gamingToggle, threshold, planControl,
        failures, renderedOverrides, get saves() { return saves; },
        publishSettings,
        emitDocument(type, detail) { documentListeners.get(type)?.({ detail }); },
        emitHost(type, state) { hostListeners.get(type)?.(state); },
    };
}

const flushPromises = () => new Promise(resolve => setImmediate(resolve));

test('dashboard uses shared settings and gaming mode state without duplicated bootstrap RPCs', async () => {
    assert.doesNotMatch(dashboard, /Host\.call\(['"](?:getSettings|getGamingMode)['"]\)/);
    const h = createHarness({ loaded: true, gamingActive: true });

    assert.deepEqual(h.calls, ['getPowerSourcePlanState']);
    assert.equal(h.renderedOverrides[0], h.settings.override);
    assert.equal(h.planToggle.dataset.on, 'false');
    assert.equal(h.threshold.value, '30');
    assert.equal(h.gamingToggle.dataset.on, 'true', 'gaming event can have fired before dashboard loads');

    h.pending[0].resolve({ enabled: true, lowBatteryThresholdPercent: 25 });
    await flushPromises();
    assert.equal(h.planToggle.dataset.on, 'true');
    assert.equal(h.settings.powerSourcePlan.enabled, true);
    assert.equal(h.threshold.value, '25');

    h.emitDocument('gamingmodechanged', { active: false });
    assert.equal(h.gamingToggle.dataset.on, 'false');
    h.emitDocument('gamingmodechanged', { state: { active: true } });
    assert.equal(h.gamingToggle.dataset.on, 'true');
    h.emitDocument('settingsloaded');
    assert.deepEqual(h.calls, ['getPowerSourcePlanState'], 'repeat settings events must not trigger another RPC');
});

test('dashboard waits for settingsloaded and preserves power-source changes emitted beforehand', async () => {
    const h = createHarness();
    assert.deepEqual(h.calls, []);

    h.emitHost('powerSourcePlanChanged', { enabled: true, lowBatteryThresholdPercent: 37 });
    assert.equal(h.planToggle.dataset.on, 'true');
    h.emitDocument('settingsloaded');
    assert.deepEqual(h.calls, [], 'the event alone is insufficient without the shared settings');

    h.publishSettings();
    h.emitDocument('settingsloaded');
    assert.deepEqual(h.calls, ['getPowerSourcePlanState']);
    assert.equal(h.planToggle.dataset.on, 'true');
    assert.equal(h.threshold.value, '37');
    assert.equal(h.settings.powerSourcePlan.enabled, true);

    h.emitHost('powerSourcePlanChanged', { enabled: false, lowBatteryThresholdPercent: 21 });
    h.pending[0].resolve({ enabled: true, lowBatteryThresholdPercent: 49 });
    await flushPromises();
    assert.equal(h.planToggle.dataset.on, 'false', 'stale state RPC must not override the live event');
    assert.equal(h.settings.powerSourcePlan.lowBatteryThresholdPercent, 21);
});

test('dashboard preserves cached settings if the power-source query rejects', async () => {
    const h = createHarness({ loaded: true });
    h.pending[0].reject(new Error('host offline'));
    await flushPromises();

    assert.equal(h.planToggle.dataset.on, 'false');
    assert.equal(h.threshold.value, '30');
    assert.deepEqual(h.calls, ['getPowerSourcePlanState']);
    assert.deepEqual(h.failures, []);
});

test('dashboard discards an in-flight initial query after local power-source edits', async () => {
    const h = createHarness({ loaded: true });
    await h.planControl.emit('click');
    h.threshold.value = '28';
    h.threshold.emit('change');
    h.pending[0].resolve({ enabled: false, lowBatteryThresholdPercent: 15 });
    await flushPromises();

    assert.equal(h.planToggle.dataset.on, 'true');
    assert.equal(h.threshold.value, '28');
    assert.equal(h.settings.powerSourcePlan.lowBatteryThresholdPercent, 28);
    assert.equal(h.saves, 1);
    assert.deepEqual(h.calls, ['getPowerSourcePlanState', 'setPowerSourcePlanSwitch']);
});
