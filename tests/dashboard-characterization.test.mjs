import test from 'node:test';
import assert from 'node:assert/strict';
import { createFrontendHarness, event, flush, richNode } from './helpers/frontend-characterization.mjs';

function dashboardHarness() {
  const planCalls = [];
  const h = createFrontendHarness({
    script: 'dashboard.js',
    preload: ['battery-history-request.js'],
    responses: {
      getActivePlan: { planId: 'balanced' }, getActivePlanReason: { source: 'system', detail: '' },
      getSettings: { settings: { override: null, powerSourcePlan: { enabled: true, lowBatteryThresholdPercent: 20 } } },
      getPowerSourcePlanState: { enabled: true, lowBatteryThresholdPercent: 20 }, getGamingMode: { active: false },
      getBatteryHealth: { available: false }, getPowerFlow: {},
      getBatteryHistory: { samples: [{ t: 0, pct: 80, w: -10, temp: 40, ac: false }, { t: 1, pct: 79, w: -12, temp: 41, ac: false }] },
      getTopProcesses: [],
      setManualOverride: payload => { planCalls.push(payload); return { success: true, override: { plan: payload.plan } }; },
    },
    windowValues: { VoltResourceProfile: {} },
    beforeRun({ document }) {
      const saver = richNode(document, { tagName: 'BUTTON', dataset: { plan: 'powerSaver' } });
      const balanced = richNode(document, { tagName: 'BUTTON', dataset: { plan: 'balanced' } });
      balanced.classList.add('text-secondary-container');
      const performance = richNode(document, { tagName: 'BUTTON', dataset: { plan: 'performance' } });
      document.registerSelector('#plan-control button', [saver, balanced, performance]);
      const oneHour = richNode(document, { tagName: 'BUTTON', dataset: { hours: '1', forever: 'false' } });
      const forever = richNode(document, { tagName: 'BUTTON', dataset: { forever: 'true' } });
      document.registerSelector('.manual-override-option', [oneHour, forever]);
      const home = document.getElementById('view-home');
      home.classList.remove('hidden');
    },
  });
  return { h, planCalls };
}

test('dashboard registers route-owned pollers and visibility pauses all active polling', async () => {
  const { h } = dashboardHarness();
  await flush();
  const process = h.lifecycleEntries.get('dashboard-processes');
  const battery = h.lifecycleEntries.get('dashboard-battery');
  assert.ok(process && battery);
  process.activate();
  battery.activate();
  assert.ok(h.intervals.size >= 1);
  h.document.hidden = true;
  h.document.dispatchEvent(event('visibilitychange'));
  assert.equal(h.intervals.size, 0);
  h.document.hidden = false;
  h.document.dispatchEvent(event('visibilitychange'));
  assert.ok(h.intervals.size >= 1);
});

test('dashboard metrics events update the live overview state through the production handler', () => {
  const { h } = dashboardHarness();
  const metrics = h.host.handlers.get('metrics')[0];
  metrics({
    cpu: 42.4, gpuAvailable: true, gpu: 31.6, ramPct: 55.2, disk: 67.8,
    ramUsedGb: 8.5, ramTotalGb: 16, cpuTemp: 61, gpuTemp: 58,
    cpuClock: 4200, ramClock: 3200, sensorsAvailable: false, sensors: [],
  });
  assert.equal(h.document.getElementById('cpu-pct').textContent, '42%');
  assert.equal(h.document.getElementById('gpu-pct').textContent, '32%');
  assert.equal(h.document.getElementById('ram-pct').textContent, '55%');
  assert.equal(h.document.getElementById('disk-pct').textContent, '68%');
  assert.equal(h.document.getElementById('ram-detail').textContent, '8.5 GB / 16.0 GB In Use');
});

test('dashboard battery history renders actual samples and selected range state', async () => {
  const { h } = dashboardHarness();
  await flush();
  h.window.VoltSystemInfo = { hasBattery: true };
  h.lifecycleEntries.get('dashboard-battery').activate();
  h.document.dispatchEvent(event('systeminfoloaded', { detail: { hasBattery: true } }));
  await flush();
  const historyCalls = h.host.calls.filter(call => call.method === 'getBatteryHistory');
  assert.ok(historyCalls.length >= 1);
  assert.deepEqual(JSON.parse(JSON.stringify(historyCalls.at(-1).payload)), { hours: 24 });
  const line = h.document.getElementById('battery-history-line');
  assert.match(line.getAttribute('d'), /^M/);
  assert.equal(h.document.getElementById('battery-history-current').textContent, '79%');
});

test('dashboard manual override modal keeps plan and duration RPC payloads stable', async () => {
  const { h, planCalls } = dashboardHarness();
  await flush();
  h.window.openOverrideModal('performance');
  assert.equal(h.document.getElementById('manual-override-overlay').classList.contains('hidden'), false);
  assert.equal(h.document.getElementById('manual-override-overlay').classList.contains('flex'), true);
  const option = h.document.querySelectorAll('.manual-override-option')[0];
  option.dispatchEvent(event('click', { currentTarget: option }));
  await flush();
  assert.deepEqual(JSON.parse(JSON.stringify(planCalls)), [{ plan: 'performance', hours: 1 }]);
  assert.equal(h.document.getElementById('manual-override-overlay').classList.contains('hidden'), true);
  assert.equal(h.document.getElementById('manual-override-overlay').classList.contains('flex'), false);
});
