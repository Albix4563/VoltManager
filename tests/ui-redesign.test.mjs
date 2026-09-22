import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import { createDocumentHarness, FakeNode } from './helpers/dom-harness.mjs';

const layout = readFileSync(
  new URL('../src/VoltManager/wwwroot/js/ui-reorganization.layout.js', import.meta.url),
  'utf8'
);
const router = readFileSync(
  new URL('../src/VoltManager/wwwroot/js/ui-reorganization.js', import.meta.url),
  'utf8'
);
const lifecycleSource = readFileSync(
  new URL('../src/VoltManager/wwwroot/js/view-lifecycle.js', import.meta.url),
  'utf8'
);
const keepAwakeSource = readFileSync(
  new URL('../src/VoltManager/wwwroot/js/power-keep-awake.js', import.meta.url),
  'utf8'
);
const css = readFileSync(
  new URL('../src/VoltManager/wwwroot/css/ui-reorganization.css', import.meta.url),
  'utf8'
);
const indexHtml = readFileSync(
  new URL('../src/VoltManager/wwwroot/index.html', import.meta.url),
  'utf8'
);

test('dense views use the shared rail shell and keep compact navigation responsive', () => {
  for (const view of ['power-plans', 'automations', 'system-tools', 'settings']) {
    assert.match(layout, new RegExp(`denseShell\\('${view}'`));
  }

  assert.match(css, /\.vm-dense-shell\s*\{[^}]*grid-template-columns\s*:\s*220px\s+minmax\(0,\s*1fr\)/s);
  assert.match(css, /@media\s*\(max-width:\s*1100px\)[\s\S]*\.vm-dense-shell\s*\{[^}]*grid-template-columns\s*:\s*minmax\(0,\s*1fr\)/s);
  assert.match(css, /@media\s*\(max-width:\s*1100px\)[\s\S]*\.vm-subnav\s*\{[^}]*overflow-x\s*:\s*auto/s);
});

test('overview status uses unboxed rows instead of nested square tiles', () => {
  assert.match(css, /\.vm-status-card\s*\{[^}]*display\s*:\s*grid[^}]*border\s*:\s*0[^}]*background\s*:\s*transparent/s);
  assert.match(css, /\.vm-status-card__header\s*\{[^}]*display\s*:\s*contents/s);
  assert.match(css, /\.vm-status-card__header\s*>\s*\.material-symbols-outlined\s*\{[^}]*background\s*:\s*transparent/s);
});

test('top status stays readable without boxed rails, dividers or shortcut tiles', () => {
  assert.match(layout, /class="vm-status-rail"[\s\S]*id="vm-top-plan"[\s\S]*id="vm-top-battery"[\s\S]*id="vm-top-automation"/);
  assert.match(layout, /<\/div>\s*<button type="button" id="vm-global-search-button"/);
  assert.doesNotMatch(layout, /class="vm-top-chip"/);
  assert.doesNotMatch(layout, /<kbd[^>]*>Ctrl K<\/kbd>/);

  assert.match(css, /\.vm-status-rail\s*\{[^}]*display\s*:\s*inline-flex[^}]*border\s*:\s*0[^}]*background\s*:\s*transparent/s);
  assert.doesNotMatch(css, /\.vm-status-item\s*\+\s*\.vm-status-item\s*\{[^}]*border-inline-start\s*:/s);
  assert.match(css, /\.vm-status-item--plan\s+strong\s*\{[^}]*color\s*:\s*var\(--vm-accent\)/s);
  assert.match(css, /\.vm-search-trigger\s*\{[^}]*width\s*:\s*40px[^}]*height\s*:\s*40px[^}]*border\s*:\s*0[^}]*border-radius\s*:\s*50%[^}]*background\s*:\s*transparent/s);
  assert.doesNotMatch(css, /\.vm-status-item\s+strong\s*\{[^}]*max-width\s*:/s);
  assert.doesNotMatch(css, /\.vm-status-item\s+strong\s*\{[^}]*text-overflow\s*:\s*ellipsis/s);
  assert.match(css, /@media\s*\(max-width:\s*1280px\)[\s\S]*\.vm-status-item__label\s*\{[^}]*display\s*:\s*none/s);
  assert.match(css, /@media\s*\(max-width:\s*700px\)[\s\S]*\.vm-status-item--automation\s*\{[^}]*display\s*:\s*none/s);
});

test('Power Plans exposes Keep Awake as its own persisted subview', () => {
  assert.match(layout, /\{ id: 'keep-awake', icon: 'bedtime_off', label: 'tab_keep_awake' \}/);
  assert.match(layout, /panel\('power-plans', 'keep-awake',[\s\S]*id="vm-keep-awake"/);
  assert.doesNotMatch(router, /move\(\$\('keep-awake-mount'\), \$\('vm-keep-awake'\)\)/);
  assert.match(
    keepAwakeSource,
    /getElementById\('vm-keep-awake'\) \|\| document\.getElementById\('keep-awake-mount'\)/
  );
});

test('Settings exposes Maintenance and relocates backup and diagnostics into it', () => {
  assert.match(layout, /\{ id: 'maintenance', icon: 'build', label: 'tab_maintenance' \}/);
  assert.match(layout, /panel\('settings', 'maintenance',[\s\S]*id="vm-settings-maintenance"/);
  assert.match(router, /move\(\$\('pref-backup'\), \$\('vm-settings-maintenance'\)\)/);
  assert.match(router, /btn-export-diagnostics[\s\S]*vm-settings-maintenance/);
});

test('motion tiers keep the live Processes panel out of transform animation', () => {
  assert.match(css, /\.vm-subview\.active\s*\{[^}]*animation/s);
  assert.match(css, /data-perf-tier="balanced"/);
  assert.match(css, /data-perf-tier="lite"/);
  assert.match(css, /prefers-reduced-motion:\s*reduce/);
  assert.match(
    css,
    /\.vm-subview\[data-vm-panel-group="monitoring"\]\[data-vm-panel="processes"\]\.active\s*\{[^}]*animation\s*:\s*none[^}]*transform\s*:\s*none/s
  );
});

test('motion timings match the rich, balanced and lite tiers', () => {
  assert.ok(css.includes('animation: vmReorgViewIn 600ms var(--vm-ease-standard)'));
  assert.ok(css.includes('animation: vmReorgPanelIn 560ms var(--vm-ease-emphasized)'));
  assert.ok(css.includes('animation-delay: 80ms'));
  assert.ok(css.includes('animation-duration: .28s'));
  assert.ok(css.includes('animation-duration: .3s'));
  assert.ok(css.includes('animation-duration: .16s'));
});

test('subviews expose accessible tab semantics', () => {
  assert.match(layout, /aria-controls="vm-panel-\$\{group\}-\$\{item\.id\}"/);
  assert.match(layout, /role="tabpanel"/);
  assert.match(layout, /aria-labelledby="vm-tab-\$\{group\}-\$\{id\}"/);
});

test('relocated UI keeps literal DOM ids unique', () => {
  const literalIds = [indexHtml, layout]
    .flatMap(source => [...source.matchAll(/\bid="([^"$]+)"/g)].map(match => match[1]));
  const duplicates = literalIds.filter((id, index) => literalIds.indexOf(id) !== index);
  assert.deepEqual([...new Set(duplicates)], []);
});

test('reorganized views avoid collisions with legacy view ids', () => {
  assert.ok(layout.includes("section.id = api.el('view-' + id) ? 'vm-view-' + id : 'view-' + id;"));
  assert.ok(router.includes('.vm-reorg-view[data-vm-view='));
});

function loadRouterHarness() {
  const document = createDocumentHarness();
  const location = { hash: '' };
  const windowEvents = new Map();
  const window = {
    VoltUiReorg: {},
    addEventListener(name, handler) { windowEvents.set(name, handler); },
  };
  const emitted = [];
  const originalDispatch = document.dispatchEvent.bind(document);
  document.dispatchEvent = evt => {
    emitted.push({ type: evt.type, detail: evt.detail });
    return originalDispatch(evt);
  };
  const context = vm.createContext({
    window,
    document,
    location,
    history: { replaceState(_state, _title, hash) { location.hash = hash; } },
    CustomEvent: class { constructor(type, options = {}) { this.type = type; this.detail = options.detail; } },
    MouseEvent: class { constructor(type, options = {}) { this.type = type; Object.assign(this, options); } },
    Event: class { constructor(type, options = {}) { this.type = type; Object.assign(this, options); } },
    MutationObserver: class { observe() {} },
    getComputedStyle: () => ({ display: 'block' }),
    setTimeout: handler => { handler(); return 1; },
    clearTimeout() {},
    console,
  });
  window.window = window;
  window.document = document;
  vm.runInContext(lifecycleSource, context);
  vm.runInContext(router, context);
  return { api: window.VoltUiReorg, lifecycle: window.VoltViewLifecycle, document, emitted, location };
}

test('activating a main view updates visible state, navigation state, hash and emitted events', () => {
  const h = loadRouterHarness();
  const overview = new FakeNode({ dataset: { vmView: 'overview' }, classes: ['vm-reorg-view', 'flex'] });
  const settings = new FakeNode({ dataset: { vmView: 'settings' }, classes: ['vm-reorg-view', 'hidden'] });
  const legacyView = new FakeNode({ classes: ['vm-legacy-view', 'flex'] });
  const overviewLink = new FakeNode({ dataset: { view: 'overview' } });
  const settingsLink = new FakeNode({ dataset: { view: 'settings' } });
  const main = h.document.registerId(new FakeNode({ id: 'main-content' }));

  h.document.registerSelector('.vm-reorg-view[data-vm-view="settings"]', settings);
  h.document.registerSelector('.vm-reorg-view', [overview, settings]);
  h.document.registerSelector('.vm-legacy-view', [legacyView]);
  h.document.registerSelector('#nav-list .nav-item[data-view]', [overviewLink, settingsLink]);

  h.api.activateView('settings', true);

  assert.equal(h.api.state.view, 'settings');
  assert.equal(settings.classList.contains('flex'), true);
  assert.equal(settings.classList.contains('hidden'), false);
  assert.equal(settings.getAttribute('aria-hidden'), 'false');
  assert.equal(overview.classList.contains('hidden'), true);
  assert.equal(legacyView.classList.contains('hidden'), true);
  assert.equal(settingsLink.classList.contains('font-bold'), true);
  assert.equal(overviewLink.classList.contains('font-bold'), false);
  assert.equal(main.scrollTop, 0);
  assert.equal(h.location.hash, '#settings');
  assert.equal(h.emitted.some(evt => evt.type === 'voltuiviewchanged' && evt.detail.view === 'settings'), true);
});

test('activating a subview updates tab/panel state and emits the resulting selection', () => {
  const h = loadRouterHarness();
  const generalTab = new FakeNode({ dataset: { vmSubnavTarget: 'general' }, classes: ['active'] });
  const appearanceTab = new FakeNode({ dataset: { vmSubnavTarget: 'appearance' } });
  const generalPanel = new FakeNode({ dataset: { vmPanel: 'general' }, classes: ['active'] });
  const appearancePanel = new FakeNode({ dataset: { vmPanel: 'appearance' }, classes: ['hidden'] });
  h.document.registerSelector('[data-vm-subnav-group="settings"]', [generalTab, appearanceTab]);
  h.document.registerSelector('[data-vm-panel-group="settings"]', [generalPanel, appearancePanel]);

  h.api.activateSubview('settings', 'appearance');

  assert.equal(h.api.state.subviews.settings, 'appearance');
  assert.equal(appearanceTab.classList.contains('active'), true);
  assert.equal(appearanceTab.getAttribute('aria-selected'), 'true');
  assert.equal(appearanceTab.tabIndex, 0);
  assert.equal(generalTab.getAttribute('aria-selected'), 'false');
  assert.equal(generalTab.tabIndex, -1);
  assert.equal(appearancePanel.classList.contains('active'), true);
  assert.equal(appearancePanel.classList.contains('hidden'), false);
  assert.equal(generalPanel.classList.contains('hidden'), true);
  assert.equal(h.emitted.some(evt => evt.type === 'voltuisubviewchanged'
    && evt.detail.group === 'settings' && evt.detail.view === 'appearance'), true);
});

test('reorganized routing drives one lifecycle activation per effective subview change', () => {
  const h = loadRouterHarness();
  const calls = [];
  h.lifecycle.register('settings-appearance', {
    matches: route => route.view === 'settings' && route.subviews.settings === 'appearance',
    init: () => calls.push('init'),
    activate: () => calls.push('activate'),
    deactivate: () => calls.push('deactivate'),
  });

  const settings = new FakeNode({ dataset: { vmView: 'settings' }, classes: ['vm-reorg-view', 'hidden'] });
  const main = h.document.registerId(new FakeNode({ id: 'main-content' }));
  const generalTab = new FakeNode({ dataset: { vmSubnavTarget: 'general' }, classes: ['active'] });
  const appearanceTab = new FakeNode({ dataset: { vmSubnavTarget: 'appearance' } });
  const generalPanel = new FakeNode({ dataset: { vmPanel: 'general' }, classes: ['active'] });
  const appearancePanel = new FakeNode({ dataset: { vmPanel: 'appearance' }, classes: ['hidden'] });
  h.document.registerSelector('.vm-reorg-view[data-vm-view="settings"]', settings);
  h.document.registerSelector('.vm-reorg-view', [settings]);
  h.document.registerSelector('.vm-legacy-view', []);
  h.document.registerSelector('#nav-list .nav-item[data-view]', []);
  h.document.registerSelector('[data-vm-subnav-group="settings"]', [generalTab, appearanceTab]);
  h.document.registerSelector('[data-vm-panel-group="settings"]', [generalPanel, appearancePanel]);

  h.api.activateView('settings', false);
  h.api.activateSubview('settings', 'appearance');
  h.api.activateSubview('settings', 'appearance');
  h.api.activateSubview('settings', 'general');

  assert.equal(main.scrollTop, 0);
  assert.deepEqual(calls, ['init', 'activate', 'deactivate']);
});
