import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import vm from 'node:vm';

const root = new URL('../', import.meta.url);

function read(relativePath) {
  return readFileSync(new URL(relativePath, root), 'utf8');
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

test('remote client omits unauthorized actions, refreshes on SSE, confirms destructive actions and handles expired sessions', () => {
  const source = read('src/VoltManager/wwwroot/remote/remote.js');

  assert.match(source, /permissions\.planChange/);
  assert.match(source, /permissions\.shutdown/);
  assert.match(source, /permissions\.restart/);
  assert.match(source, /new EventSource\(['"]\/api\/events['"]\)/);
  assert.match(source, /addEventListener\(['"]state['"]/);
  assert.match(source, /confirm\(/);
  assert.match(source, /response\.status === 401/);
  assert.match(source, /showLogin/);
});

test('LAN remote desktop and onboarding strings exist in every supported locale', () => {
  const window = {};
  vm.runInContext(read('src/VoltManager/wwwroot/js/i18n.catalogs.js'), vm.createContext({ window }));
  const catalogs = window.VoltI18nCatalogs.namespaces;
  const locales = ['it', 'en', 'es', 'zh'];
  const coreKeys = [
    'welcome_remote_title', 'welcome_remote_sub', 'welcome_remote_enable', 'welcome_remote_plan',
    'welcome_remote_shutdown', 'welcome_remote_restart', 'welcome_remote_pin_once', 'welcome_remote_copy'
  ];
  const reorgKeys = [
    'nav_remote_control', 'remote_title', 'remote_subtitle', 'remote_status_disabled', 'remote_status_running',
    'remote_status_waiting', 'remote_local_only', 'remote_enable', 'remote_address', 'remote_port',
    'remote_fingerprint', 'remote_copy', 'remote_pin_title', 'remote_pin_help', 'remote_pin_status',
    'remote_generate_pin', 'remote_pin_placeholder', 'remote_set_pin', 'remote_generated_once', 'remote_copy_pin',
    'remote_permissions_title', 'remote_permissions_help', 'remote_allow_plan', 'remote_allow_plan_sub',
    'remote_allow_shutdown', 'remote_allow_shutdown_sub', 'remote_allow_restart', 'remote_allow_restart_sub',
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
