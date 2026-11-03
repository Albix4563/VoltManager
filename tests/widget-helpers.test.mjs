import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const read = relative => readFileSync(new URL(relative, import.meta.url), 'utf8');

// Helper results are created inside the vm realm, so compare plain data.
const plain = value => JSON.parse(JSON.stringify(value));

function loadAppearanceHelpers() {
  const window = {};
  vm.runInNewContext(read('../src/VoltManager/wwwroot/js/widget-appearance-helpers.js'), { window });
  return window.VoltWidgetAppearanceHelpers;
}

function loadPlanOverride({ search = '' } = {}) {
  const window = { URLSearchParams, location: { search } };
  vm.runInNewContext(
    read('../src/VoltManager/wwwroot/js/widget-plan-override.js'),
    { window, URLSearchParams, console }
  );
  return window.VoltWidgetPlanOverride;
}

test('choicePayload sends no override for the global material', () => {
  const helpers = loadAppearanceHelpers();
  assert.equal(helpers.choicePayload('global', { material: 'solid' }), null);
});

test('choicePayload clones the desired appearance and overrides only the material', () => {
  const helpers = loadAppearanceHelpers();
  const desired = { material: 'solid', tint: 'accent', intensity: 0.4 };
  const payload = helpers.choicePayload('acrylic', desired);

  assert.deepEqual(plain(payload), { material: 'acrylic', tint: 'accent', intensity: 0.4 });
  assert.notEqual(payload, desired);
  assert.equal(desired.material, 'solid');
});

test('rovingIndex wraps arrows, honors Home/End and ignores unknown keys', () => {
  const helpers = loadAppearanceHelpers();

  assert.equal(helpers.rovingIndex(0, 'ArrowLeft', 4), 3);
  assert.equal(helpers.rovingIndex(0, 'ArrowUp', 4), 3);
  assert.equal(helpers.rovingIndex(3, 'ArrowRight', 4), 0);
  assert.equal(helpers.rovingIndex(3, 'ArrowDown', 4), 0);
  assert.equal(helpers.rovingIndex(2, 'Home', 4), 0);
  assert.equal(helpers.rovingIndex(2, 'End', 4), 3);
  assert.equal(helpers.rovingIndex(2, 'Enter', 4), 2);
  assert.equal(helpers.rovingIndex(0, 'Home', 0), -1);
});

test('counts separates global defaults from per-widget overrides', () => {
  const helpers = loadAppearanceHelpers();

  assert.deepEqual(plain(helpers.counts([])), { globalCount: 0, overrideCount: 0 });
  assert.deepEqual(plain(helpers.counts(null)), { globalCount: 0, overrideCount: 0 });
  assert.deepEqual(
    plain(helpers.counts([
      { type: 'clock', appearance: null },
      { type: 'usage', appearance: { material: 'solid' } },
      { type: 'plans' },
      null,
    ])),
    { globalCount: 3, overrideCount: 1 }
  );
});

test('statusText uses singular and plural copy for the applied and custom counts', () => {
  const helpers = loadAppearanceHelpers();
  const translate = (key, fallback) => fallback;
  const format = (template, values) => template.replace('{count}', String(values.count));

  assert.equal(
    helpers.statusText([{ appearance: null }], translate, format),
    'Applicato a 1 widget.'
  );
  assert.equal(
    helpers.statusText([{ appearance: null }, { appearance: null }], translate, format),
    'Applicato a 2 widget.'
  );
  assert.equal(
    helpers.statusText([{ appearance: null }, { appearance: {} }], translate, format),
    'Applicato a 1 widget. 1 usa un materiale personalizzato.'
  );
  assert.equal(
    helpers.statusText([{ appearance: {} }, { appearance: {} }], translate, format),
    'Applicato a 0 widget. 2 usano un materiale personalizzato.'
  );
});

test('resetAll clears only overridden typed widgets and returns the final state', async () => {
  const helpers = loadAppearanceHelpers();
  const items = [
    { type: 'clock', appearance: { material: 'solid' } },
    { type: 'usage' },
    { type: 'plans', appearance: { material: 'acrylic' } },
    null,
  ];
  const cleared = [];

  const result = await helpers.resetAll(items, async type => {
    cleared.push(type);
    return `state:${type}`;
  }, async () => {
    throw new Error('refetch must not run');
  });

  assert.deepEqual(cleared, ['clock', 'plans']);
  assert.equal(result, 'state:plans');
});

test('resetAll refetches authoritative state when a clear request fails', async () => {
  const helpers = loadAppearanceHelpers();
  let refetches = 0;

  const result = await helpers.resetAll(
    [{ type: 'clock', appearance: {} }, { type: 'plans', appearance: {} }],
    async type => {
      if (type === 'plans') throw new Error('host offline');
      return { type };
    },
    async () => {
      refetches += 1;
      return { refetched: true };
    }
  );

  assert.equal(refetches, 1);
  assert.deepEqual(result, { refetched: true });
});

test('resetAll without overrides stays a no-op', async () => {
  const helpers = loadAppearanceHelpers();
  let calls = 0;

  const result = await helpers.resetAll([{ type: 'clock' }, null], async () => {
    calls += 1;
  }, async () => {
    calls += 1;
    return null;
  });

  assert.equal(result, null);
  assert.equal(calls, 0);
});

test('shouldPrompt intercepts only plan-widget overrides without an explicit duration', () => {
  const override = loadPlanOverride();

  assert.equal(override.shouldPrompt('setManualOverride', { plan: 'balanced' }, true), true);
  assert.equal(override.shouldPrompt('setManualOverride', { plan: 'balanced' }, false), false);
  assert.equal(override.shouldPrompt('getSettings', { plan: 'balanced' }, true), false);
  assert.equal(override.shouldPrompt('setManualOverride', { plan: '' }, true), false);
  assert.equal(override.shouldPrompt('setManualOverride', { plan: 42 }, true), false);
  assert.equal(override.shouldPrompt('setManualOverride', null, true), false);
  assert.equal(override.shouldPrompt('setManualOverride', { plan: 'balanced', hours: 0 }, true), false);
  assert.equal(override.shouldPrompt('setManualOverride', { plan: 'balanced', hours: null }, true), false);
});

test('buildPayload keeps forever overrides and validates positive finite hours', () => {
  const override = loadPlanOverride();

  assert.deepEqual(plain(override.buildPayload('balanced', { forever: true })), { plan: 'balanced' });
  assert.equal('hours' in override.buildPayload('balanced', { forever: true }), false);
  assert.deepEqual(plain(override.buildPayload('balanced', { hours: '10' })), { plan: 'balanced', hours: 10 });
  assert.deepEqual(plain(override.buildPayload('balanced', { hours: 1.5 })), { plan: 'balanced', hours: 1.5 });
  assert.equal(override.buildPayload('balanced', null), null);

  for (const choice of [{ hours: 0 }, { hours: -1 }, { hours: NaN }, { hours: Infinity }, { hours: 'abc' }, { hours: undefined }]) {
    assert.throws(
      () => override.buildPayload('balanced', choice),
      error => error?.name === 'TypeError' && /Invalid widget override duration/.test(error.message)
    );
  }
});

test('createCallInterceptor requires a callable original bridge call', () => {
  const override = loadPlanOverride();
  assert.throws(() => override.createCallInterceptor(null, {}), /must be a function/);
  assert.throws(() => override.createCallInterceptor({}, {}), /must be a function/);
});

test('interceptor forwards unrelated calls without consulting the duration dialog', async () => {
  const override = loadPlanOverride();
  const calls = [];
  let prompted = 0;
  const call = override.createCallInterceptor(
    async (method, payload) => { calls.push([method, payload]); return 'host-result'; },
    { isPlanWidget: true, chooseDuration: async () => { prompted += 1; return { hours: 1 }; } }
  );

  assert.equal(await call('getSettings'), 'host-result');
  assert.deepEqual(calls, [['getSettings', undefined]]);
  assert.equal(prompted, 0);
});

test('interceptor forwards the chosen duration and the host result', async () => {
  const override = loadPlanOverride();
  const calls = [];
  const call = override.createCallInterceptor(
    async (method, payload) => { calls.push([method, payload]); return { applied: true }; },
    { isPlanWidget: true, chooseDuration: async plan => ({ hours: plan === 'balanced' ? 10 : 1 }) }
  );

  const result = await call('setManualOverride', { plan: 'balanced' });

  assert.deepEqual(result, { applied: true });
  assert.deepEqual(plain(calls), [['setManualOverride', { plan: 'balanced', hours: 10 }]]);
});

test('interceptor sends a forever override without an hours key', async () => {
  const override = loadPlanOverride();
  const calls = [];
  const call = override.createCallInterceptor(
    async (method, payload) => { calls.push(payload); },
    { isPlanWidget: true, chooseDuration: async () => ({ forever: true }) }
  );

  await call('setManualOverride', { plan: 'performance' });

  assert.deepEqual(plain(calls), [{ plan: 'performance' }]);
  assert.equal('hours' in calls[0], false);
});

test('interceptor throws a typed cancellation when the dialog closes', async () => {
  const override = loadPlanOverride();
  let forwarded = 0;
  const call = override.createCallInterceptor(
    async () => { forwarded += 1; },
    { isPlanWidget: true, chooseDuration: async () => null }
  );

  const error = await call('setManualOverride', { plan: 'balanced' }).then(
    () => null,
    reason => reason
  );

  assert.ok(error && typeof error === 'object');
  assert.equal(error.code, 'WIDGET_OVERRIDE_CANCELLED');
  assert.match(error.message, /cancelled/i);
  assert.equal(forwarded, 0);
});

test('interceptor fails closed when the duration chooser is unavailable', async () => {
  const override = loadPlanOverride();
  const call = override.createCallInterceptor(
    async () => 'must-not-run',
    { isPlanWidget: true }
  );

  await assert.rejects(call('setManualOverride', { plan: 'balanced' }), /chooser is unavailable/);
});

test('interceptor evaluates dynamic plan-widget state on every call', async () => {
  const override = loadPlanOverride();
  const calls = [];
  let isPlanWidget = false;
  const call = override.createCallInterceptor(
    async (method, payload) => { calls.push(payload); return payload; },
    { isPlanWidget: () => isPlanWidget, chooseDuration: async () => ({ hours: 5 }) }
  );

  await call('setManualOverride', { plan: 'balanced' });
  isPlanWidget = true;
  await call('setManualOverride', { plan: 'balanced' });

  assert.deepEqual(plain(calls), [
    { plan: 'balanced' },
    { plan: 'balanced', hours: 5 },
  ]);
});

test('install patches only the plan widget and never wraps Host.call twice', () => {
  const usageWindow = {
    URLSearchParams,
    location: { search: '?w=usage' },
    document: {},
    Host: { call() { return Promise.resolve(); } },
  };
  vm.runInNewContext(
    read('../src/VoltManager/wwwroot/js/widget-plan-override.js'),
    { window: usageWindow, URLSearchParams, console }
  );
  assert.equal(usageWindow.Host.call.__voltWidgetPlanOverride, undefined);
  assert.equal(usageWindow.VoltWidgetPlanOverride.install(), false);

  const planWindow = {
    URLSearchParams,
    location: { search: '?w=plans' },
    document: {},
    Host: { call() { return Promise.resolve(); } },
  };
  vm.runInNewContext(
    read('../src/VoltManager/wwwroot/js/widget-plan-override.js'),
    { window: planWindow, URLSearchParams, console }
  );

  const patched = planWindow.Host.call;
  assert.equal(patched.__voltWidgetPlanOverride, true);
  assert.equal(planWindow.VoltWidgetPlanOverride.install(), true);
  assert.equal(planWindow.Host.call, patched);
});
