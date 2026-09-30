import test from 'node:test';
import assert from 'node:assert/strict';
import { createFrontendHarness, event, flush, richNode } from './helpers/frontend-characterization.mjs';

function advancedHarness({ failLoad = false } = {}) {
  const h = createFrontendHarness({
    script: 'advanced.js',
    responses: {
      listPowerPlans: [{ guid: 'A', name: 'Balanced', isActive: true }, { guid: 'B', name: 'Perf', isActive: false }],
      getPlanParameters: payload => {
        if (failLoad) throw new Error('bridge unavailable');
        return { planGuid: payload.planGuid || 'A' };
      },
      getMemoryStatus: {}, getStandbyAutoCleanSettings: { enabled: false },
    },
    beforeRun({ document }) {
      const advSelect = richNode(document, { id: 'adv-plan-select', tagName: 'SELECT' });
      document.registerId(advSelect);
      const mount = document.getElementById('vm-power-advanced');
      const panel = document.getElementById('adv-panel');
      panel.parentElement = mount;
      const timeoutSelect = richNode(document, { id: 'power-timeout-plan-select', tagName: 'SELECT' });
      document.registerId(timeoutSelect);
    },
  });
  return h;
}

test('advanced lifecycle mount/wire is idempotent and route definitions remain stable', () => {
  const h = advancedHarness();
  const advanced = h.lifecycleEntries.get('advanced-parameters');
  assert.ok(advanced.matches({ view: 'power-plans', subviews: { 'power-plans': 'advanced' } }));
  assert.equal(advanced.matches({ view: 'home', subviews: {} }), false);
  advanced.init();
  const clicks = h.document.listenerCount('click');
  const changes = h.document.listenerCount('change');
  advanced.init();
  assert.equal(h.document.listenerCount('click'), clicks);
  assert.equal(h.document.listenerCount('change'), changes);
});

test('advanced plan navigation sends selected planGuid through the real change handler', async () => {
  const h = advancedHarness();
  const advanced = h.lifecycleEntries.get('advanced-parameters');
  advanced.init();
  await advanced.activate();
  await flush();
  const select = h.document.getElementById('adv-plan-select');
  select.value = 'B';
  h.document.dispatchEvent(event('change', { target: select }));
  await flush();
  const calls = h.host.calls.filter(call => call.method === 'getPlanParameters');
  assert.ok(calls.some(call => call.payload?.planGuid === 'B'));
});

test('advanced RPC load failure exposes recovery UI instead of leaving the panel loading', async () => {
  const h = advancedHarness({ failLoad: true });
  const advanced = h.lifecycleEntries.get('advanced-parameters');
  advanced.init();
  await advanced.activate();
  await flush();
  assert.equal(h.document.getElementById('adv-loading').classList.contains('hidden'), true);
  assert.equal(h.document.getElementById('adv-rows').classList.contains('hidden'), false);
  assert.equal(h.document.getElementById('adv-status-msg').classList.contains('hidden'), false);
  assert.ok(h.host.calls.some(call => call.method === 'getPlanParameters'));
});
