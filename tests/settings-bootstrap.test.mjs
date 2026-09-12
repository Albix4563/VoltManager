import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = name => readFileSync(new URL('../src/VoltManager/wwwroot/' + name, import.meta.url), 'utf8');
const power = source('js/power.js');

test('settings bootstrap loads and wires the shipped rule inputs before notifying preferences', async () => {
    // Derive controls from the actual markup: fabricated IDs would hide a cleanup regression.
    const nodes = new Map([...source('index.html').matchAll(/<input\b[^>]*\bid="([^"]+)"[^>]*>/g)]
        .map(([, id]) => [id, { addEventListener(event, handler) { this[event] = handler; } }]));
    const rules = ['saver', 'balanced', 'performance'].map((id, i) => ({
        id, thresholdPct: 10 + i * 20, durationMinutes: i + 1, enabled: true,
    }));
    const settings = { rules, masterAutomationEnabled: true };
    const events = [];
    const errors = [];
    let saves = 0;
    const context = vm.createContext({
        window: {},
        document: {
            getElementById: id => nodes.get(id) || null,
            dispatchEvent: event => events.push(event.type),
        },
        CustomEvent: class { constructor(type) { this.type = type; } },
        Host: { call: async method => method === 'getSettings' ? { settings, startWithWindows: true } : {} },
        console: { error: (...args) => errors.push(args) },
        scheduleSave: () => saves++,
        normalizeCpuAutomation: () => ({ sampleIntervalSeconds: 3 }),
        normalizeKeepAwake: () => ({ enabled: false }),
    });
    // Peripheral panels have separate tests; execute the actual rule load/wiring and bootstrap.
    for (const name of [
        'mountAppPowerProfileUi', 'mountHeavyAppUi', 'mountKeepAwakeUi', 'mountThermalGuardUi', 'mountIdlePowerGuardUi',
        'syncAppPowerProfileUi', 'syncHeavyAppUi', 'syncKeepAwakeUi', 'syncThermalUi', 'syncIdleUi',
        'wireAppPowerProfileUi', 'wireHeavyAppUi', 'wireKeepAwakeUi', 'wireThermalGuardUi', 'wireIdlePowerGuardUi',
        'renderAppPowerProfileStatus', 'renderAppPowerProfiles', 'renderHeavyAppStatus', 'renderThermalState',
        'renderIdleState', 'renderKeepAwakeState',
    ]) context[name] = () => {};
    vm.runInContext(
        'let settings, appProfileStatus, heavyAppStatus, thermalState, idleState, keepAwakeState;' +
        "const ruleIds = ['saver', 'balanced', 'performance'];" +
        power.slice(power.indexOf('    function ruleById'), power.indexOf('    function setToggle')) +
        power.slice(power.indexOf('    function loadIntoUi'), power.indexOf('    function saveSettingsNow')) +
        power.slice(power.indexOf('    function clamp'), power.indexOf('    function historyLocale')) +
        power.slice(power.indexOf("    Host.call('getSettings')"), power.indexOf("    document.addEventListener('langchanged'")),
        context,
    );
    await new Promise(resolve => setImmediate(resolve));
    assert.deepEqual(errors, [], 'a bootstrap error also disconnects appearance, widgets and app profiles');
    assert.deepEqual(events, ['settingsloaded']);
    assert.equal(context.window.__voltSettings.get(), settings);
    assert.equal(context.window.__voltSettings.startWithWindows, true);
    for (const rule of rules) {
        const threshold = nodes.get(`rule-${rule.id}-threshold`);
        const minutes = nodes.get(`rule-${rule.id}-minutes`);
        const toggle = nodes.get(`rule-${rule.id}-toggle`);
        assert.equal(threshold.value, rule.thresholdPct);
        assert.equal(minutes.value, rule.durationMinutes);
        assert.equal(toggle.checked, true);
        threshold.value = '42';
        threshold.change({ target: threshold });
        assert.equal(rule.thresholdPct, 42);
        minutes.value = '7';
        minutes.change({ target: minutes });
        assert.equal(rule.durationMinutes, 7);
        threshold.value = '100';
        threshold.change({ target: threshold });
        assert.equal(rule.thresholdPct, 42, 'invalid edits preserve the previous value');
        toggle.checked = false;
        toggle.change({ target: toggle });
        assert.equal(rule.enabled, false);
    }
    const master = nodes.get('master-toggle');
    master.checked = false;
    master.change({ target: master });
    assert.equal(settings.masterAutomationEnabled, false);
    assert.equal(saves, 13);
});
