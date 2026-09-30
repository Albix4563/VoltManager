import test from 'node:test';
import assert from 'node:assert/strict';
import { createFrontendHarness, event, flush, richNode } from './helpers/frontend-characterization.mjs';

function appHarness() {
  const settings = { masterAutomationEnabled: true, override: null };
  const transitions = [];
  const h = createFrontendHarness({
    script: 'app.js',
    responses: {
      getSystemInfo: { cpuName: 'CPU', gpuName: 'GPU', ramTotalGb: 16, osVersion: 'Windows', appVersion: '1.2.3' },
      getGamingMode: { active: false }, getScheduledPowerAction: null, getSettings: { settings },
    },
    beforeRun({ document, window, lifecycleEntries }) {
      const systemLink = richNode(document, { tagName: 'A', dataset: { view: 'system' } });
      document.registerSelector('#nav-list a[data-view="system"]', systemLink);
      const homeLink = richNode(document, { tagName: 'A', dataset: { view: 'home' } });
      const monitoringLink = richNode(document, { tagName: 'A', dataset: { view: 'monitoring' } });
      document.registerSelector('#nav-list a[data-view]', [homeLink, monitoringLink, systemLink]);
      const home = richNode(document, { id: 'view-home' });
      const monitoringView = richNode(document, { id: 'view-monitoring' });
      document.registerId(home);
      document.registerId(monitoringView);
      home.classList.remove('hidden');
      monitoringView.classList.add('hidden');
      document.registerSelector('#main-content .view[id^="view-"]', [home, monitoringView]);
      const nav = document.getElementById('nav-list');
      nav.contains = node => [homeLink, monitoringLink, systemLink].includes(node);
      window.__voltSettings = { get: () => settings, save() {} };
      window.VoltViewLifecycle.transition = route => transitions.push(route);
    },
  });
  return { h, settings, transitions };
}

test('app startup hydrates system info and settings reconnect mutates the existing settings store', async () => {
  const { h, settings } = appHarness();
  await flush();
  assert.equal(h.document.getElementById('cpu-name').textContent, 'CPU');
  assert.equal(h.document.getElementById('sidebar-version').textContent, 'VOLT MANAGER v1.2.3');

  const reconnect = h.host.handlers.get('automationStateChanged')[0];
  h.host.Host.call = (method, payload) => {
    h.host.calls.push({ method, payload });
    return Promise.resolve(method === 'getSettings' ? { settings: { masterAutomationEnabled: false, override: { plan: 'balanced' } } } : {});
  };
  reconnect();
  await flush();
  assert.equal(settings.masterAutomationEnabled, false);
  assert.deepEqual(settings.override, { plan: 'balanced' });
});

test('app route click switches the real views, emits viewchange and transitions lifecycle state', async () => {
  const { h, transitions } = appHarness();
  await flush();
  const monitoringLink = h.document.querySelectorAll('#nav-list a[data-view]')[1];
  const target = { closest: selector => selector === 'a[data-view]' ? monitoringLink : null };
  h.document.getElementById('nav-list').dispatchEvent(event('click', { target }));
  await flush();
  assert.equal(h.document.getElementById('view-home').classList.contains('hidden'), true);
  assert.equal(h.document.getElementById('view-monitoring').classList.contains('hidden'), false);
  assert.ok(transitions.some(route => route.view === 'monitoring'));
});

test('settingsloaded remount path stays idempotent when the system navigation already exists', () => {
  const { h } = appHarness();
  const before = h.document.getElementById('nav-list').children.length;
  h.document.dispatchEvent(event('settingsloaded'));
  h.document.dispatchEvent(event('settingsloaded'));
  assert.equal(h.document.getElementById('nav-list').children.length, before);
});
