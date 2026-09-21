import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import vm from 'node:vm';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const read = relative => fs.readFileSync(path.join(root, relative), 'utf8');

function loadCatalogs() {
  const context = { window: {} };
  vm.createContext(context);
  vm.runInContext(read('src/VoltManager/wwwroot/js/i18n.catalogs.js'), context);
  return context.window.VoltI18nCatalogs;
}

function placeholders(value) {
  return [...String(value).matchAll(/\{(?:\d+|[A-Za-z_][A-Za-z0-9_]*)\}/g)]
    .map(match => match[0])
    .sort();
}

test('centralized web catalogs have key, value and placeholder parity', () => {
  const catalogs = loadCatalogs();
  assert.deepEqual(Object.keys(catalogs.metadata).sort(), ['en', 'es', 'it', 'zh']);

  for (const [namespace, catalog] of Object.entries(catalogs.namespaces)) {
    const languages = Object.keys(catalog).sort();
    assert.deepEqual(languages, ['en', 'es', 'it', 'zh'], `${namespace}: languages`);
    const englishKeys = Object.keys(catalog.en).sort();
    assert.ok(englishKeys.length > 0, `${namespace}: empty English catalog`);
    for (const language of languages) {
      assert.deepEqual(Object.keys(catalog[language]).sort(), englishKeys, `${namespace}/${language}: key parity`);
      for (const key of englishKeys) {
        const value = catalog[language][key];
        assert.equal(typeof value, 'string', `${namespace}/${language}/${key}: not a string`);
        assert.notEqual(value.trim(), '', `${namespace}/${language}/${key}: empty text`);
        assert.deepEqual(placeholders(value), placeholders(catalog.en[key]), `${namespace}/${language}/${key}: placeholders`);
      }
    }
  }
});

test('feature modules no longer own translation dictionaries', () => {
  const settings = read('src/VoltManager/wwwroot/js/settings.js');
  const power = read('src/VoltManager/wwwroot/js/power.js');
  const app = read('src/VoltManager/wwwroot/js/app.js');
  const suspension = read('src/VoltManager/wwwroot/js/update-suspension.js');
  const reorganization = read('src/VoltManager/wwwroot/js/ui-reorganization.i18n.js');

  assert.doesNotMatch(settings, /const\s+localText\s*=\s*\{/);
  assert.doesNotMatch(power, /const\s+text\s*=\s*\{/);
  assert.doesNotMatch(power, /const\s+historyText\s*=\s*\{/);
  assert.doesNotMatch(app, /const\s+labels\s*=\s*\{[\s\S]*?\bit\s*:\s*\{/);
  assert.doesNotMatch(suspension, /const\s+text\s*=\s*\{/);
  assert.doesNotMatch(reorganization, /VoltUiReorgStrings\s*=\s*\{/);
});

test('I18n namespaced lookup has explicit fallback and shared formatting', () => {
  const localStore = new Map([['volt_lang', 'es']]);
  const context = {
    console,
    Intl,
    CustomEvent: class CustomEvent { constructor(type, init) { this.type = type; this.detail = init?.detail; } },
    localStorage: {
      getItem: key => localStore.get(key) ?? null,
      setItem: (key, value) => localStore.set(key, value),
    },
    document: {
      documentElement: { lang: '' },
      querySelectorAll: () => [],
      addEventListener: () => {},
      dispatchEvent: () => {},
    },
    window: { addEventListener: () => {} },
  };
  vm.createContext(context);
  vm.runInContext(read('src/VoltManager/wwwroot/js/i18n.catalogs.js'), context);
  vm.runInContext(read('src/VoltManager/wwwroot/js/i18n.js'), context);

  const api = context.window.I18n;
  assert.equal(api.feature('settings', 'autoUpdates'), api.catalog('settings').es.autoUpdates);
  assert.equal(api.feature('settings', 'autoUpdates', 'en'), api.catalog('settings').en.autoUpdates);
  assert.equal(api.feature('missing', 'unknown', 'en', 'safe fallback'), 'safe fallback');
  assert.equal(api.format('{count} files', { count: 3 }), '3 files');
  assert.equal(api.number(1234.5, 'en'), new Intl.NumberFormat('en-GB').format(1234.5));
  assert.equal(api.plural(2, { one: '{count} item', other: '{count} items' }, 'en'), '2 items');
});
