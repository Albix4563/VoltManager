import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

function source(path) {
  return readFileSync(new URL('../' + path, import.meta.url), 'utf8');
}

const html = source('src/VoltManager/wwwroot/index.html');
const power = source('src/VoltManager/wwwroot/js/power.js');
const dashboard = source('src/VoltManager/wwwroot/js/dashboard.js');
const bridge = source('src/VoltManager/wwwroot/js/bridge.js');
const i18n = source('src/VoltManager/wwwroot/js/i18n.js');
const catalogsSource = source('src/VoltManager/wwwroot/js/i18n.catalogs.js');
const reorgLayout = source('src/VoltManager/wwwroot/js/ui-reorganization.layout.js');
const reorgI18n = catalogsSource;

test('plan history is reachable from the plan reason and both power navigation shells', () => {
  assert.match(html, /id="open-plan-history"/);
  assert.match(html, /data-pm="history"/);
  assert.match(html, /id="plan-history-mount"/);
  assert.match(reorgLayout, /id: 'history', icon: 'history', label: 'tab_plan_history'/);
  assert.match(reorgLayout, /id="vm-power-history"/);
  assert.match(dashboard, /activateSubview\('power-plans', 'history'\)/);
});

test('history filters, 50-row paging and problem semantics are explicit', () => {
  assert.match(power, /visibleCount: 50/);
  assert.match(power, /\['all', 'automatic', 'manual', 'external', 'problems'\]/);
  assert.match(power, /entry\.outcome === 'failed' \|\| entry\.outcome === 'unverifiable'/);
  assert.match(power, /filtered\.slice\(0, planHistoryState\.visibleCount\)/);
  assert.match(power, /planHistoryState\.visibleCount \+= 50/);
});

test('history revision handling rejects stale notifications and stale snapshots', () => {
  assert.match(power, /revision >= Math\.max\(planHistoryState\.revision, planHistoryState\.notifiedRevision\)/);
  assert.match(power, /revision <= planHistoryState\.revision\) return/);
  assert.match(power, /planHistoryState\.dirty = true/);
  assert.match(power, /if \(planHistoryVisible\(\)\) loadPlanHistory\(\)/);
});

test('history renders names as text and formats localized dates down to seconds', () => {
  const historyUi = power.slice(power.indexOf('function historyDate'), power.indexOf("Host.call('getSettings')"));
  assert.match(historyUi, /second: '2-digit'/);
  assert.match(historyUi, /I18n\.date/);
  assert.match(historyUi, /I18n\.number/);
  assert.match(historyUi, /node\.textContent = String\(textValue\)/);
  assert.doesNotMatch(historyUi, /innerHTML/);
});

test('history exposes empty, filtered-empty, load-error retry and clear states', () => {
  assert.match(power, /state\.textContent = ht\('empty'\)/);
  assert.match(power, /state\.textContent = ht\('noResults'\)/);
  assert.match(power, /state\.textContent = ht\('loadError'\)/);
  assert.match(power, /Host\.call\('clearPlanHistory'\)/);
  assert.match(power, /id = 'plan-history-retry'/);
});

test('history translations cover Italian, English, Spanish and Chinese', () => {
  assert.equal((catalogsSource.match(/"power_group_history"/g) || []).length, 4);
  assert.equal((catalogsSource.match(/"plan_history_link"/g) || []).length, 4);
  assert.equal((reorgI18n.match(/tab_plan_history:/g) || []).length, 4);
  assert.match(catalogsSource, /it: \{[\s\S]*?note: 'Ultimi 500 eventi della sessione/);
  assert.match(catalogsSource, /en: \{[\s\S]*?note: 'Last 500 events from this session/);
  assert.match(catalogsSource, /es: \{[\s\S]*?note: 'Últimos 500 eventos de la sesión/);
  assert.match(catalogsSource, /zh: \{[\s\S]*?note: '本次会话最近 500 个事件/);
});

test('bridge event subscription supports lifecycle cleanup', () => {
  assert.match(bridge, /return \(\) => \{/);
  assert.match(bridge, /current\.splice\(index, 1\)/);
  assert.match(power, /planHistoryUnsubscribe = Host\.on\('planHistoryChanged'/);
  assert.match(power, /window\.addEventListener\('unload'/);
  assert.match(power, /planHistoryUnsubscribe\(\)/);
});

// Execute the real history functions with a small DOM and deferred bridge replies.
function historyHarness(language = 'en') {
  const nodes = new Map();
  const events = new Map();
  const requests = [];
  const node = () => ({
    children: [], dataset: {}, textContent: '', hidden: false,
    classList: { contains: () => false },
    setAttribute() {},
    set innerHTML(_) { throw new Error('Dynamic history content must remain text'); },
    appendChild(child) { this.children.push(child); if (child.id) nodes.set(child.id, child); },
    replaceChildren() { this.children = []; },
    addEventListener(name, handler) { this[name] = handler; },
    closest() { return this; },
  });
  nodes.set('plan-history-mount', node());
  const panel = node();
  const document = {
    hidden: false,
    getElementById: id => nodes.get(id),
    createElement: node,
    querySelector: selector => selector.startsWith('[data-vm-panel-group=') ? panel : null,
    querySelectorAll: () => [],
    addEventListener: (name, handler) => events.set(name, handler),
    removeEventListener: name => events.delete(name),
  };
  const context = vm.createContext({
    document, window: { addEventListener() {} }, console: { error() {} },
    lang: () => language, I18n: { t: key => key },
    Host: {
      available: true,
      on(name, handler) { events.set(name, handler); return () => events.delete(name); },
      call(method) { return new Promise((resolve, reject) => requests.push({ method, resolve, reject })); },
    },
  });
  vm.runInContext(catalogsSource, context);
  vm.runInContext(`
    I18n.feature = function(namespace, key, requestedLanguage) {
      const catalog = (window.VoltI18nCatalogs.namespaces || {})[namespace] || {};
      return (catalog[requestedLanguage] && catalog[requestedLanguage][key]) ||
        (catalog.en && catalog.en[key]) || key;
    };
    window.I18n = I18n;
  `, context);
  vm.runInContext(
    power.slice(power.indexOf('    let planHistoryWired'), power.indexOf('    const ruleIds')) +
    power.slice(power.indexOf('    function ht'), power.indexOf('    function esc')) +
    power.slice(power.indexOf('    function historyLocale'), power.indexOf("    Host.call('getSettings')")) +
    '\nwirePlanHistoryUi(); globalThis.api = { loadPlanHistory, historyExplanation, historyNumber, renderPlanHistory, state: planHistoryState };',
    context,
  );
  return { ...context.api, nodes, events, requests, document };
}

const flush = () => new Promise(resolve => setImmediate(resolve));

test('a notification arriving during a read triggers a fresh snapshot without rendering stale data', async () => {
  const h = historyHarness();
  const loading = h.loadPlanHistory();
  h.events.get('planHistoryChanged')({ revision: 2 });
  h.requests[0].resolve({ revision: 1, entries: [{ id: 1 }] });
  await loading;
  assert.equal(h.state.revision, -1);
  assert.equal(h.requests.length, 2);
  h.requests[1].resolve({ revision: 2, entries: [] });
  await flush();
  assert.equal(h.state.revision, 2);
  assert.equal(h.state.dirty, false);
});

test('clearing history cannot lose a newer event while an older snapshot is in flight', async () => {
  const h = historyHarness();
  const loading = h.loadPlanHistory();
  const clearing = h.nodes.get('plan-history-mount').click({ target: {
    closest: selector => selector === '#plan-history-clear' ? {} : null,
  } });
  h.events.get('planHistoryChanged')({ revision: 3 });
  h.requests[1].resolve({ revision: 2 });
  await clearing;
  h.requests[0].resolve({ revision: 1, entries: [] });
  await loading;
  assert.equal(h.requests.length, 3);
  h.requests[2].resolve({ revision: 3, entries: [{ id: 3 }] });
  await flush();
  assert.equal(h.state.entries[0].id, 3);
  assert.equal(h.state.revision, 3);
});

test('history parks bridge reads while hidden and catches up when visible again', async () => {
  const h = historyHarness();
  h.document.hidden = true;
  h.events.get('planHistoryChanged')({ revision: 4 });
  assert.equal(h.requests.length, 0);
  assert.equal(h.state.dirty, true);
  h.document.hidden = false;
  h.events.get('visibilitychange')();
  h.requests[0].resolve({ revision: 4, entries: [] });
  await flush();
  assert.equal(h.state.revision, 4);
});

test('history retries failed reads without starting a request loop', async () => {
  const h = historyHarness();
  const loading = h.loadPlanHistory();
  h.requests[0].reject(new Error('bridge unavailable'));
  await loading;
  assert.equal(h.state.error, true);
  assert.equal(h.requests.length, 1);
  assert.equal(h.nodes.get('plan-history-retry').hidden, false);
  const retrying = h.loadPlanHistory();
  h.events.get('planHistoryChanged')({ revision: 1 });
  h.requests[1].resolve({ revision: 0, entries: [] });
  await retrying;
  assert.equal(h.requests.length, 3);
  h.requests[2].resolve({ revision: 1, entries: [] });
  await flush();
  assert.equal(h.state.error, false);
  assert.equal(h.state.revision, 1);
});

test('an invalid bridge snapshot fails once and offers retry', async () => {
  const h = historyHarness();
  const loading = h.loadPlanHistory();
  h.requests[0].resolve(null);
  await loading;
  assert.equal(h.state.error, true);
  assert.equal(h.requests.length, 1);
  assert.equal(h.nodes.get('plan-history-retry').hidden, false);
});

test('actual history renderer pages, filters, and keeps app names literal', () => {
  const h = historyHarness();
  const name = 'Editor $& <img src=x onerror=alert(1)>';
  h.state.entries = Array.from({ length: 60 }, (_, id) => ({
    id, category: 'automatic', source: 'appProfile', reasonCode: 'profile_applied',
    outcome: 'applied', details: { appName: name }, lastTimestampUtc: '2026-09-06T08:00:00Z',
  }));
  h.renderPlanHistory();
  const list = h.nodes.get('plan-history-list');
  assert.equal(list.children.length, 50);
  assert.equal(list.children[0].children[1].textContent, `App profile: ${name}`);
  assert.equal(h.nodes.get('plan-history-more').hidden, false);
  h.state.visibleCount += 50;
  h.renderPlanHistory();
  assert.equal(list.children.length, 60);
  h.state.filter = 'problems';
  h.renderPlanHistory();
  assert.equal(list.children.length, 0);
  assert.match(h.nodes.get('plan-history-state').textContent, /No events match/);
});

test('reason codes shared by different automations retain the right cause in every language', () => {
  for (const language of ['it', 'en', 'es', 'zh']) {
    const h = historyHarness(language);
    const explain = (source, reasonCode) => h.historyExplanation({ source, reasonCode });
    assert.notEqual(explain('idle', 'disabled'), explain('thermal', 'disabled'));
    assert.notEqual(explain('powerSource', 'disabled_restore'), explain('powerSource', 'unplugged_restore'));
    assert.notEqual(explain('idle', 'battery_skip'), explain('idle', 'resumed'));
    assert.equal(h.historyNumber(null), '—');
  }
});
