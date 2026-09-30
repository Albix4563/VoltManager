import test from 'node:test';
import assert from 'node:assert/strict';
import { createFrontendHarness, event, flush } from './helpers/frontend-characterization.mjs';

const widgetKinds = {
  clock: 'clock-time', calendar: 'calendar-grid', usage: 'usage-cpu', temps: 'temp-cpu',
  power: 'power-watts', plans: 'plan-pill', launcher: 'launcher-grid', apps: 'launcher-grid',
  actions: 'action-grid', brightness: 'brightness-slider', processes: 'proc-list', memory: 'memory-purge',
};

function widgetHarness(type, extra = {}) {
  const settings = {
    animationLevel: 'low', font: 'inter', themeColor: 'cyan',
    widgets: {
      appearance: { material: 'acrylic', tint: 'graphite', gradient: 'radial', intensity: 40 },
      items: [{ type, pinned: true, appearance: { material: 'transparent', tint: 'ember', gradient: 'flat', intensity: 75 } }],
    },
  };
  return createFrontendHarness({
    script: 'widgets.js',
    search: `?w=${type}&s=medium`,
    responses: {
      getSettings: { settings }, getSystemInfo: { ramTotalGb: 16, logicalCores: 8 },
      getKeepAwakeState: { enabled: false }, getActivePlan: { planId: 'balanced' },
      getGamingMode: { active: false }, getScheduledPowerAction: null, getBrightness: { percent: 50 },
      getTopProcesses: [], getMemoryStatus: { totalGb: 16, usedGb: 8, standbyGb: 2, freeGb: 6 },
      getLaunchers: [], ...extra.responses,
    },
    windowValues: {
      VoltFont: { apply() {} }, VoltTheme: { apply() {} },
      VoltAnimationLevel: { classifyHardwareTier: () => 'balanced', resolveLevel: level => level },
      ...extra.windowValues,
    },
  });
}

test('every shipped widget kind boots through its real renderer', async () => {
  for (const [type, marker] of Object.entries(widgetKinds)) {
    const h = widgetHarness(type);
    await flush();
    assert.ok(h.document.getElementById(marker), `${type} should render ${marker}`);
    assert.equal(h.document.documentElement.dataset.size, 'medium');
  }
});

test('widget settings hydrate per-widget appearance and react to host appearance changes', async () => {
  const h = widgetHarness('clock');
  await flush();
  const root = h.document.getElementById('widget-root');
  assert.equal(root.dataset.material, 'transparent');
  assert.equal(root.dataset.tint, 'ember');
  assert.equal(root.dataset.gradient, 'flat');
  assert.equal(root.style['--vm-widget-intensity'], '0.75');

  h.host.handlers.get('widgetAppearanceChanged')[0]({ material: 'solid', tint: 'neutral', gradient: 'diagonal', intensity: 25 });
  assert.equal(root.dataset.material, 'solid');
  assert.equal(root.style['--vm-widget-intensity'], '0.25');
});

test('widget lifecycle pauses polling while hidden and resumes when visible', async () => {
  const h = widgetHarness('power');
  await flush();
  assert.equal(h.intervals.size, 1);
  const pollsBeforeHide = h.host.calls.filter(call => call.method === 'getBatteryPower').length;
  assert.ok(pollsBeforeHide > 0);
  h.document.hidden = true;
  h.document.dispatchEvent(event('visibilitychange'));
  assert.equal(h.intervals.size, 0);
  h.runTimers('interval');
  await flush();
  assert.equal(h.host.calls.filter(call => call.method === 'getBatteryPower').length, pollsBeforeHide);
  h.document.hidden = false;
  h.document.dispatchEvent(event('visibilitychange'));
  await flush();
  assert.equal(h.intervals.size, 1);
  assert.equal(h.host.calls.filter(call => call.method === 'getBatteryPower').length, pollsBeforeHide + 1);
});
