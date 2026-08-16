import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import vm from 'node:vm';

const script = readFileSync(
    new URL('../src/VoltManager/wwwroot/js/widget-plan-override.js', import.meta.url),
    'utf8');

function loadApi() {
    const context = {
        window: {},
    };
    vm.runInNewContext(script, context);
    return context.window.VoltWidgetPlanOverride;
}

test('timed widget plan override asks for a duration and forwards hours', async () => {
    const api = loadApi();
    const calls = [];
    let promptedFor = null;
    const interceptedCall = api.createCallInterceptor(
        async (method, payload) => {
            calls.push({ method, payload });
            return { success: true };
        },
        {
            isPlanWidget: true,
            chooseDuration: async (plan) => {
                promptedFor = plan;
                return { hours: 10 };
            },
        });

    const result = await interceptedCall('setManualOverride', { plan: 'performance' });

    assert.equal(promptedFor, 'performance');
    assert.equal(result.success, true);
    assert.deepEqual(calls, [
        { method: 'setManualOverride', payload: { plan: 'performance', hours: 10 } },
    ]);
});

test('explicit duration bypasses the widget duration prompt', async () => {
    const api = loadApi();
    let promptCount = 0;
    const calls = [];
    const interceptedCall = api.createCallInterceptor(
        async (method, payload) => {
            calls.push({ method, payload });
            return { success: true };
        },
        {
            isPlanWidget: true,
            chooseDuration: async () => {
                promptCount++;
                return { hours: 1 };
            },
        });

    await interceptedCall('setManualOverride', { plan: 'balanced', hours: 12 });

    assert.equal(promptCount, 0);
    assert.deepEqual(calls, [
        { method: 'setManualOverride', payload: { plan: 'balanced', hours: 12 } },
    ]);
});

test('forever remains possible only after an explicit choice', async () => {
    const api = loadApi();
    const calls = [];
    const interceptedCall = api.createCallInterceptor(
        async (method, payload) => {
            calls.push({ method, payload });
            return { success: true };
        },
        {
            isPlanWidget: true,
            chooseDuration: async () => ({ forever: true }),
        });

    await interceptedCall('setManualOverride', { plan: 'powerSaver' });

    assert.deepEqual(calls, [
        { method: 'setManualOverride', payload: { plan: 'powerSaver' } },
    ]);
});

test('cancelling the duration prompt does not apply a manual override', async () => {
    const api = loadApi();
    let callCount = 0;
    const interceptedCall = api.createCallInterceptor(
        async () => {
            callCount++;
            return { success: true };
        },
        {
            isPlanWidget: true,
            chooseDuration: async () => null,
        });

    await assert.rejects(
        interceptedCall('setManualOverride', { plan: 'performance' }),
        /cancelled/i);
    assert.equal(callCount, 0);
});
