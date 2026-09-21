import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import { createDocumentHarness, event, FakeNode } from './helpers/dom-harness.mjs';

const root = new URL('../', import.meta.url);
const read = path => readFileSync(new URL(path, root), 'utf8');

function loadSearchModule() {
  const source = read('src/VoltManager/wwwroot/js/global-search.js');
  const document = {
    readyState: 'loading',
    addEventListener() {},
  };
  const context = vm.createContext({
    window: {},
    document,
    CustomEvent: class CustomEvent {},
    setTimeout,
    clearTimeout,
  });
  context.window.window = context.window;
  context.window.document = document;
  vm.runInContext(source, context);
  return context.window.VoltGlobalSearch;
}

test('search normalization is case and accent insensitive', () => {
  const search = loadSearchModule();
  assert.equal(search.normalize('Modalità Élite'), 'modalita elite');
  assert.equal(search.normalize('  PIANO  '), 'piano');
});

test('ranking follows label prefix, word prefix, label contains, keyword, description', () => {
  const search = loadSearchModule();
  const catalog = [
    { id: 'description', label: 'Alpha', description: 'contains turbo setting', keywords: [] },
    { id: 'keyword', label: 'Beta', description: '', keywords: ['turbo'] },
    { id: 'contains', label: 'Deturbo mode', description: '', keywords: [] },
    { id: 'word', label: 'CPU Turbo boost', description: '', keywords: [] },
    { id: 'prefix', label: 'Turbo Boost', description: '', keywords: [] },
  ];

  assert.deepEqual(
    Array.from(search.rankEntries('turbo', catalog), entry => entry.id),
    ['prefix', 'word', 'contains', 'keyword', 'description']
  );
});

test('ranking is stable and capped at eight results', () => {
  const search = loadSearchModule();
  const catalog = Array.from({ length: 12 }, (_, index) => ({
    id: `item-${index}`,
    label: `Power item ${index}`,
    description: '',
    keywords: [],
  }));

  assert.deepEqual(
    Array.from(search.rankEntries('power', catalog), entry => entry.id),
    Array.from({ length: 8 }, (_, index) => `item-${index}`)
  );
});

test('catalog uses stable navigation fields and covers every reorganized main view', () => {
  const search = loadSearchModule();
  const views = new Set(search.catalog.map(entry => entry.view));
  for (const view of ['overview', 'monitoring', 'power-plans', 'automations', 'system-tools', 'widgets', 'settings']) {
    assert.equal(views.has(view), true, `missing catalog view ${view}`);
  }
  for (const entry of search.catalog) {
    assert.equal(typeof entry.id, 'string');
    assert.equal(typeof entry.labelKey, 'string');
    assert.equal(typeof entry.descriptionKey, 'string');
    assert.equal(Array.isArray(entry.keywords), true);
    assert.equal(typeof entry.icon, 'string');
    assert.equal(typeof entry.category, 'string');
    assert.equal(typeof entry.view, 'string');
  }
});

test('palette ships its structural accessibility hooks and startup entry point', () => {
  const layout = read('src/VoltManager/wwwroot/js/ui-reorganization.layout.js');
  const bootstrap = read('src/VoltManager/wwwroot/js/changelog.js');

  assert.match(layout, /vm-global-search-button/);
  assert.match(bootstrap, /loadScript\('js\/global-search\.js/);
});

test('palette strings exist in every reorganized UI language and CSS handles reduced motion', () => {
  const strings = read('src/VoltManager/wwwroot/js/i18n.catalogs.js');
  const css = read('src/VoltManager/wwwroot/css/ui-reorganization.css');

  for (const key of [
    'search_button',
    'search_title',
    'search_placeholder',
    'search_empty',
    'search_hint',
    'search_category_overview',
  ]) {
    assert.equal((strings.match(new RegExp(`${key}:`, 'g')) || []).length, 4, `${key} must exist for four languages`);
  }

  assert.match(css, /\.vm-search-overlay/);
  assert.match(css, /\.vm-search-option\.is-active/);
  assert.match(css, /\.vm-search-target-highlight/);
  assert.match(css, /@media\s*\(max-width:\s*780px\)/);
  assert.match(css, /prefers-reduced-motion:\s*reduce/);
});

test('navigation waits for a lazy target then scrolls, focuses, and highlights it', async () => {
  const source = read('src/VoltManager/wwwroot/js/global-search.js');
  const calls = [];
  let lookups = 0;
  let scrollOptions = null;
  let focused = false;
  let highlighted = false;
  const focusable = {
    disabled: false,
    matches: () => true,
    focus: () => { focused = true; },
  };
  const target = {
    isConnected: true,
    disabled: false,
    matches: () => false,
    querySelector: () => focusable,
    scrollIntoView: options => { scrollOptions = options; },
    classList: {
      add: name => { if (name === 'vm-search-target-highlight') highlighted = true; },
      remove() {},
    },
  };
  const document = {
    readyState: 'loading',
    addEventListener() {},
    getElementById(id) {
      if (id !== 'lazy-target') return null;
      lookups += 1;
      return lookups >= 3 ? target : null;
    },
    querySelectorAll: () => [],
  };
  const context = vm.createContext({
    window: {
      VoltUiReorg: {
        activateView: (view, updateHash) => calls.push(['view', view, updateHash]),
        activateSubview: (view, subview) => calls.push(['subview', view, subview]),
      },
    },
    document,
    Date,
    setTimeout: () => 1,
    clearTimeout() {},
    requestAnimationFrame: callback => { callback(); return 1; },
    matchMedia: query => ({ matches: query === '(prefers-reduced-motion: reduce)' }),
  });
  context.window.window = context.window;
  context.window.document = document;
  vm.runInContext(source, context);

  const result = await context.window.VoltGlobalSearch.navigate({
    view: 'power-plans',
    subview: 'advanced',
    targetId: 'lazy-target',
  });

  assert.deepEqual(Array.from(calls, call => Array.from(call)), [
    ['view', 'power-plans', true],
    ['subview', 'power-plans', 'advanced'],
  ]);
  assert.ok(lookups >= 3);
  assert.equal(scrollOptions.behavior, 'auto');
  assert.equal(scrollOptions.block, 'center');
  assert.equal(focused, true);
  assert.equal(highlighted, true);
  assert.deepEqual({ navigated: result.navigated, focused: result.focused }, { navigated: true, focused: true });
});

test('search modal opens and closes through user events without duplicating its trigger listener', () => {
  const source = read('src/VoltManager/wwwroot/js/global-search.js');
  const document = createDocumentHarness({ readyState: 'complete' });
  const button = document.registerId(new FakeNode({ id: 'vm-global-search-button' }));
  const dialog = new FakeNode({ id: 'vm-global-search', classes: ['vm-search-overlay', 'hidden'] });
  const input = new FakeNode({ id: 'vm-global-search-input' });
  const results = new FakeNode({ id: 'vm-global-search-results' });
  const title = new FakeNode({ id: 'vm-search-title' });
  const footer = new FakeNode();
  dialog.registerQuery('#vm-global-search-input', input);
  dialog.registerQuery('#vm-global-search-results', results);
  dialog.registerQuery('#vm-search-title', title);
  dialog.registerQuery('.vm-search-footer span', footer);
  document.createElement = () => dialog;

  const context = vm.createContext({
    window: {},
    document,
    CustomEvent: class { constructor(type, options = {}) { this.type = type; this.detail = options.detail; } },
    requestAnimationFrame: callback => { callback(); return 1; },
    setTimeout: () => 1,
    clearTimeout() {},
    console,
  });
  context.window.window = context.window;
  context.window.document = document;
  vm.runInContext(source, context);

  document.dispatchEvent(event('voltuiready'));
  document.dispatchEvent(event('voltuiready'));
  assert.equal(button.listenerCount('click'), 1);

  button.dispatchEvent(event('click'));
  assert.equal(dialog.classList.contains('hidden'), false);
  assert.equal(dialog.getAttribute('aria-hidden'), 'false');
  assert.equal(document.documentElement.classList.contains('vm-search-open'), true);
  assert.equal(input.focused, true);

  const escape = event('keydown', { key: 'Escape', ctrlKey: false, altKey: false, shiftKey: false });
  document.dispatchEvent(escape);
  assert.equal(escape.defaultPrevented, true);
  assert.equal(dialog.classList.contains('hidden'), true);
  assert.equal(dialog.getAttribute('aria-hidden'), 'true');
  assert.equal(document.documentElement.classList.contains('vm-search-open'), false);
});

test('every search description and keyword used by the catalog is localized in all languages', () => {
  const searchSource = read('src/VoltManager/wwwroot/js/global-search.js');
  const strings = read('src/VoltManager/wwwroot/js/i18n.catalogs.js');
  const keys = [...new Set(
    [...searchSource.matchAll(/'(search_(?:desc|kw)_[a-z0-9_]+)'/g)].map(match => match[1])
  )];

  assert.ok(keys.length > 40);
  for (const key of keys) {
    assert.equal((strings.match(new RegExp(`\\b${key}:`, 'g')) || []).length, 4, `${key} must exist for four languages`);
  }
});

