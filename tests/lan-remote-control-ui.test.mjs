import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import vm from 'node:vm';
import { FakeNode, createDocumentHarness } from './helpers/dom-harness.mjs';

const root = new URL('../', import.meta.url);

function read(relativePath) {
  return readFileSync(new URL(relativePath, root), 'utf8');
}

function deferred() {
  let resolve;
  const promise = new Promise(r => { resolve = r; });
  return { promise, resolve };
}

function jsonResponse(payload, status = 200) {
  return {
    status,
    ok: status >= 200 && status < 300,
    json: async () => payload,
  };
}

function withAppend(node) {
  node.append = (...children) => {
    for (const child of children) node.appendChild(child);
  };
  return node;
}

function createRemoteHarness(fetchImpl) {
  const document = createDocumentHarness({ readyState: 'complete' });
  const originalCreateElement = document.createElement;
  document.createElement = tagName => withAppend(originalCreateElement(tagName));

  const ids = [
    'login-view', 'app-view', 'login-form', 'pin-input', 'login-button', 'login-error',
    'actions-grid', 'actions-empty', 'action-feedback', 'connection-pill', 'connection-label',
    'device-name', 'device-version', 'active-plan', 'logout-button',
  ];
  for (const id of ids) document.registerId(withAppend(new FakeNode({ id })));

  class FakeEventSource {
    constructor(url) {
      this.url = url;
      this.listeners = new Map();
      this.onerror = null;
    }
    addEventListener(name, handler) { this.listeners.set(name, handler); }
    close() {}
  }

  const window = {
    confirm: () => true,
    setTimeout: () => 0,
  };
  const context = vm.createContext({
    window,
    document,
    fetch: fetchImpl,
    EventSource: FakeEventSource,
    console,
  });
  vm.runInContext(read('src/VoltManager/wwwroot/remote/remote.js'), context);
  return { app: window.VoltLanRemoteApp, document };
}

test('welcome onboarding has a fifth LAN remote step wired through dedicated RPC methods', () => {
  const html = read('src/VoltManager/wwwroot/index.html');
  const source = read('src/VoltManager/wwwroot/js/welcome.js');

  assert.match(source, /const STEP_COUNT = 5/);
  assert.match(source, /Host\.call\('getLanRemoteControlState'/);
  assert.match(source, /Host\.call\('setLanRemoteControlPermissions'/);
  assert.match(source, /Host\.call\('setLanRemoteControlEnabled'/);
  assert.match(source, /generatedPin/);
  assert.match(html, /data-step="4"/);
  assert.equal((html.match(/class="welcome-dot"/g) || []).length, 5);
});

test('welcome LAN remote switch is contained so it cannot cover permissions or Start', () => {
  const html = read('src/VoltManager/wwwroot/index.html');
  const step = html.match(/<section class="welcome-step hidden flex-col gap-lg" data-step="4">([\s\S]*?)<\/section>/)?.[1];
  assert.ok(step);
  assert.match(step, /<div class="relative[^\"]*">\s*<input class="toggle-large" id="welcome-remote-enabled" type="checkbox"\/>\s*<label class="toggle-label-large[^\"]*" for="welcome-remote-enabled">/);
  assert.match(html, /id="welcome-btn-start"/);
});

test('remote web client is a self-contained local bundle with no external resources', () => {
  const htmlPath = new URL('src/VoltManager/wwwroot/remote/index.html', root);
  const cssPath = new URL('src/VoltManager/wwwroot/remote/remote.css', root);
  const jsPath = new URL('src/VoltManager/wwwroot/remote/remote.js', root);

  assert.equal(existsSync(htmlPath), true);
  assert.equal(existsSync(cssPath), true);
  assert.equal(existsSync(jsPath), true);

  const html = read('src/VoltManager/wwwroot/remote/index.html');
  assert.match(html, /remote\.css/);
  assert.match(html, /remote\.js/);
  assert.doesNotMatch(html, /https?:\/\//i);
  assert.doesNotMatch(html, /<script[^>]+src=["']\/\//i);
});

test('remote client renders only permitted actions from executed state', () => {
  const { app, document } = createRemoteHarness(async () => {
    throw new Error('unexpected fetch');
  });

  app.renderState({
    device: 'Desk PC',
    version: '1.2.3',
    plan: 'balanced',
    permissions: {
      planChange: true,
      shutdown: false,
      restart: true,
      sleep: true,
      hibernate: true,
    },
    capabilities: {
      sleep: true,
      hibernate: false,
    },
  });

  const actions = document.getElementById('actions-grid').children;
  const headings = actions.map(card => card.children[0].children[1].textContent);
  assert.deepEqual(headings, ['Power plan', 'Restart', 'Sleep']);
  assert.equal(document.getElementById('actions-empty').classList.contains('hidden'), true);
});

test('remote client keeps newer state when an older loadState response resolves last', async () => {
  const requests = [];
  const { app, document } = createRemoteHarness(() => {
    const pending = deferred();
    requests.push(pending);
    return pending.promise;
  });

  const older = app.loadState();
  const newer = app.loadState();
  assert.equal(requests.length, 2);

  requests[1].resolve(jsonResponse({
    device: 'Newer PC',
    version: '2',
    plan: 'performance',
    permissions: {},
    capabilities: {},
  }));
  await newer;

  requests[0].resolve(jsonResponse({
    device: 'Older PC',
    version: '1',
    plan: 'powerSaver',
    permissions: {},
    capabilities: {},
  }));
  await older;

  assert.equal(document.getElementById('device-name').textContent, 'Newer PC');
  assert.equal(document.getElementById('device-version').textContent, '2');
});

test('remote client ignores an in-flight load after a newer state is applied', async () => {
  const request = deferred();
  const { app, document } = createRemoteHarness(() => request.promise);

  const staleLoad = app.loadState();
  app.renderState({
    device: 'SSE state',
    version: '3',
    plan: 'balanced',
    permissions: {},
    capabilities: {},
  });

  request.resolve(jsonResponse({
    device: 'Stale response',
    version: '2',
    plan: 'powerSaver',
    permissions: {},
    capabilities: {},
  }));
  await staleLoad;

  assert.equal(document.getElementById('device-name').textContent, 'SSE state');
  assert.equal(document.getElementById('device-version').textContent, '3');
});

test('local LAN remote sleep and hibernate permissions start hidden and are capability gated', () => {
  const layout = read('src/VoltManager/wwwroot/js/ui-reorganization.layout.js');
  const source = read('src/VoltManager/wwwroot/js/lan-remote-control.js');

  assert.match(layout, /lan-remote-permission hidden[^>]+id="lan-remote-perm-sleep-row"/);
  assert.match(layout, /lan-remote-permission hidden[^>]+id="lan-remote-perm-hibernate-row"/);
  assert.match(source, /sleep-row'\)\?\.classList\.toggle\('hidden', !next\.sleepAvailable\)/);
  assert.match(source, /hibernate-row'\)\?\.classList\.toggle\('hidden', !next\.hibernateAvailable\)/);
});

test('LAN remote desktop and onboarding strings exist in every supported locale', () => {
  const window = {};
  vm.runInContext(read('src/VoltManager/wwwroot/js/i18n.catalogs.js'), vm.createContext({ window }));
  const catalogs = window.VoltI18nCatalogs.namespaces;
  const locales = ['it', 'en', 'es', 'zh'];
  const coreKeys = [
    'welcome_remote_title', 'welcome_remote_sub', 'welcome_remote_enable', 'welcome_remote_plan',
    'welcome_remote_shutdown', 'welcome_remote_restart', 'welcome_remote_sleep', 'welcome_remote_hibernate',
    'welcome_remote_pin_once', 'welcome_remote_copy'
  ];
  const reorgKeys = [
    'nav_remote_control', 'remote_title', 'remote_subtitle', 'remote_status_disabled', 'remote_status_running',
    'remote_status_waiting', 'remote_local_only', 'remote_enable', 'remote_address', 'remote_port',
    'remote_fingerprint', 'remote_copy', 'remote_pin_title', 'remote_pin_help', 'remote_pin_status',
    'remote_generate_pin', 'remote_pin_placeholder', 'remote_set_pin', 'remote_generated_once', 'remote_copy_pin',
    'remote_permissions_title', 'remote_permissions_help', 'remote_allow_plan', 'remote_allow_plan_sub',
    'remote_allow_shutdown', 'remote_allow_shutdown_sub', 'remote_allow_restart', 'remote_allow_restart_sub',
    'remote_allow_sleep', 'remote_allow_sleep_sub', 'remote_allow_hibernate', 'remote_allow_hibernate_sub',
    'remote_pin_missing', 'remote_error', 'remote_saved', 'remote_started', 'remote_stopped', 'remote_pin_replaced',
    'remote_pin_invalid', 'remote_copied', 'remote_copy_failed', 'search_desc_remote', 'search_kw_remote', 'search_kw_lan'
  ];

  for (const locale of locales) {
    for (const key of coreKeys) assert.ok(catalogs.core[locale][key], `missing core.${locale}.${key}`);
    for (const key of reorgKeys) assert.ok(catalogs.uiReorganization[locale][key], `missing uiReorganization.${locale}.${key}`);
  }
});

test('global search indexes the LAN remote control page', () => {
  const source = read('src/VoltManager/wwwroot/js/global-search.js');
  assert.match(source, /id: 'remote-control'/);
  assert.match(source, /view: 'remote-control'/);
  assert.match(source, /descriptionKey: 'search_desc_remote'/);
});
