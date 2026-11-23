import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import { createDocumentHarness, event, FakeNode } from './helpers/dom-harness.mjs';

const read = file => readFileSync(new URL(`../src/VoltManager/wwwroot/${file}`, import.meta.url), 'utf8');
const tourSource = read('js/tour.feature.js');

function createTourHarness({ hasBattery = false, collapsedSidebar = false } = {}) {
  const document = createDocumentHarness();
  document.head = new FakeNode();
  document.documentElement.dataset.vmHasBattery = hasBattery ? 'true' : 'false';
  if (collapsedSidebar) document.body.classList.add('sidebar-collapsed');

  const viewActivations = [];
  const subviewActivations = [];
  const viewNodes = {
    overview: ['.vm-overview-grid', ...(hasBattery ? ['#vm-brightness-card'] : [])],
    monitoring: [
      { subview: 'hardware', selector: '#dash-taskmanager' },
      { subview: 'processes', selector: '#processes-section' },
      { subview: 'temperatures', selector: '#vm-panel-monitoring-temperatures' },
      { subview: 'storage', selector: '#vm-panel-monitoring-storage' },
      ...(hasBattery ? [{ subview: 'battery', selector: '#vm-panel-monitoring-battery' }] : []),
    ],
    'power-plans': [{ subview: 'active', selector: '[data-vm-view="power-plans"] .vm-subnav' }],
    automations: [{ subview: 'rules', selector: '[data-vm-view="automations"] .vm-subnav' }],
    'system-tools': [{ subview: 'scheduled', selector: '[data-vm-view="system-tools"] .vm-subnav' }],
    'remote-control': ['#lan-remote-hero'],
    widgets: [{ subview: 'widget', selector: '#widgets-card' }],
    settings: [{ subview: 'general', selector: '#pref-show-tour' }],
  };

  for (const selector of ['#nav-list', '#vm-top-status', '#btn-energy-tips', '#btn-monitoring-toggle']) {
    document.registerSelector(selector, new FakeNode());
  }

  for (const [view, entries] of Object.entries(viewNodes)) {
    const viewRoot = new FakeNode({ classes: ['vm-reorg-view'] });
    viewRoot.dataset.vmView = view;
    for (const entry of entries) {
      if (typeof entry === 'string') {
        const node = new FakeNode();
        node.parentElement = viewRoot;
        node.getBoundingClientRect = () => ({ left: 320, top: 100, right: 720, bottom: 260, width: 400, height: 160 });
        document.registerSelector(entry, node);
      } else {
        const subviewPanel = new FakeNode({ classes: ['vm-subview', ...(entry.subview === 'hardware' ? [] : ['hidden'])] });
        subviewPanel.dataset.vmPanelGroup = view;
        subviewPanel.dataset.vmPanel = entry.subview;
        subviewPanel.parentElement = viewRoot;
        subviewPanel.getBoundingClientRect = () => ({ left: 320, top: 100, right: 720, bottom: 260, width: 400, height: 160 });
        if (entry.selector.startsWith('#vm-panel-monitoring-')) {
          document.registerSelector(entry.selector, subviewPanel);
        } else {
          const inner = new FakeNode();
          inner.parentElement = subviewPanel;
          inner.getBoundingClientRect = () => ({ left: 320, top: 100, right: 720, bottom: 260, width: 400, height: 160 });
          document.registerSelector(entry.selector, inner);
        }
      }
    }
  }

  for (const selector of ['#nav-list', '#vm-top-status', '#btn-energy-tips', '#btn-monitoring-toggle']) {
    const node = document.querySelector(selector);
    node.getBoundingClientRect = () => ({ left: 16, top: 100, right: 240, bottom: 220, width: 224, height: 120 });
  }

  document.createElement = tag => {
    const node = new FakeNode({ tagName: tag.toUpperCase() });
    node.getBoundingClientRect = () => ({ left: 0, top: 0, right: 360, bottom: 180, width: 360, height: 180 });
    node.querySelector = selector => {
      if (!node.queries.has(selector)) node.registerQuery(selector, new FakeNode());
      return node.queries.get(selector)[0];
    };
    return node;
  };

  const window = {
    document,
    addEventListener() {},
    removeEventListener() {},
    innerWidth: 1200,
    innerHeight: 800,
    VoltUiReorg: {
      ready: true,
      activateView(view) { viewActivations.push(view); },
      activateSubview(group, name) { subviewActivations.push([group, name]); },
    },
  };
  const timers = [];
  const context = vm.createContext({
    window,
    document,
    Host: { available: true },
    I18n: { t: key => key },
    CustomEvent: class { constructor(type, options) { this.type = type; this.detail = options?.detail; } },
    setTimeout: cb => { timers.push(cb); return timers.length; },
    clearTimeout() {},
    requestAnimationFrame: cb => { cb(); return 1; },
    cancelAnimationFrame() {},
    console,
    Math,
  });
  window.Host = context.Host;
  window.I18n = context.I18n;
  vm.runInContext(tourSource, context);

  return {
    document,
    window,
    timers,
    viewActivations,
    subviewActivations,
    flushTimers() {
      while (timers.length) timers.shift()();
    },
  };
}

test('guided tour covers every major section and subview on desktop and laptop', () => {
  for (const hasBattery of [false, true]) {
    const h = createTourHarness({ hasBattery });
    h.window.__tour.open();
    const pop = h.document.body.children.at(-1).children.at(-1);
    const next = pop.querySelector('[data-act="next"]');
    const title = pop.querySelector('.vm-tour-pop__title-text');
    const icon = pop.querySelector('.vm-tour-pop__title .material-symbols-outlined');
    const counter = pop.querySelector('.vm-tour-counter');

    const visitedTitles = [];
    const visitedIcons = [];
    const expectedCount = hasBattery ? 19 : 17;
    for (let i = 0; i < expectedCount; i++) {
      assert.equal(counter.textContent, `${i + 1} / ${expectedCount}`);
      visitedTitles.push(title.textContent);
      visitedIcons.push(icon.textContent);
      h.flushTimers();
      if (i < expectedCount - 1) next.dispatchEvent(event('click'));
    }

    assert.equal(visitedTitles[0], 'tour_intro_title');
    assert.equal(visitedTitles.at(-1), 'tour_done_title');
    for (const key of [
      'tour_nav_title', 'tour_topbar_title', 'tour_tips_title', 'tour_overview_title',
      'tour_monitor_title', 'tour_metrics_title', 'tour_processes_title', 'tour_temperatures_title',
      'tour_storage_title', 'tour_plan_title', 'tour_power_title', 'tour_system_tools_title',
      'tour_remote_title', 'tour_widgets_title', 'tour_settings_title',
    ]) {
      assert.ok(visitedTitles.includes(key), `missing tour section ${key}`);
    }
    assert.equal(visitedTitles.includes('tour_brightness_title'), hasBattery);
    assert.equal(visitedTitles.includes('tour_battery_title'), hasBattery);
    assert.ok(visitedIcons.every(value => typeof value === 'string' && value.length > 0));

    assert.deepEqual(h.subviewActivations, [
      ['monitoring', 'hardware'],
      ['monitoring', 'processes'],
      ['monitoring', 'temperatures'],
      ['monitoring', 'storage'],
      ...(hasBattery ? [['monitoring', 'battery']] : []),
      ['power-plans', 'active'],
      ['automations', 'rules'],
      ['system-tools', 'scheduled'],
      ['widgets', 'widget'],
      ['settings', 'general'],
    ]);
    assert.ok(h.viewActivations.includes('remote-control'));
  }
});

test('every tour target selector resolves in index.html or ui-reorganization.layout.js', () => {
  const indexHtml = read('index.html');
  const layoutJs = read('js/ui-reorganization.layout.js');
  const combined = indexHtml + '\n' + layoutJs;

  const ids = [
    'nav-list', 'vm-top-status', 'btn-energy-tips', 'vm-brightness-card',
    'btn-monitoring-toggle', 'dash-taskmanager', 'processes-section',
    'vm-panel-monitoring-temperatures', 'vm-panel-monitoring-storage',
    'vm-panel-monitoring-battery', 'lan-remote-hero', 'widgets-card', 'pref-show-tour',
  ];
  for (const id of ids) {
    assert.ok(
      combined.includes(`id="${id}"`) ||
      combined.includes(`id='${id}'`) ||
      combined.includes(`.id = '${id}'`) ||
      id.startsWith('vm-panel-monitoring-'),
      `missing id ${id}`
    );
  }
  assert.match(layoutJs, /id="vm-panel-\$\{group\}-\$\{id\}"/);
  assert.match(layoutJs, /class="vm-overview-grid"/);
  assert.match(layoutJs, /class="vm-subnav"/);
  assert.match(layoutJs, /data-vm-view=\$\{?""\}?|section\.dataset\.vmView = id/);
});

test('collapsed sidebar allows tooltip to sit closer to the 64px rail', () => {
  const h = createTourHarness({ hasBattery: false, collapsedSidebar: true });
  h.document.querySelector('#nav-list').getBoundingClientRect = () => ({ left: 8, top: 80, right: 60, bottom: 400, width: 52, height: 320 });
  h.window.__tour.open();
  const pop = h.document.body.children.at(-1).children.at(-1);
  pop.querySelector('[data-act="next"]').dispatchEvent(event('click'));
  h.flushTimers();
  assert.equal(pop.style.left, '80px');
});
