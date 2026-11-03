import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const read = relative => readFileSync(path.join(root, relative), 'utf8');
const catalogSource = read('src/VoltManager/wwwroot/js/i18n.catalogs.js');
const i18nSource = read('src/VoltManager/wwwroot/js/i18n.js');

// Values created inside the vm use its realm prototypes, so compare plain data.
const plain = value => JSON.parse(JSON.stringify(value));

function createHarness({ stored = null, host = null } = {}) {
  const store = new Map();
  if (stored) store.set('volt_lang', stored);

  const documentEvents = [];
  const documentListeners = new Map();
  const windowListeners = new Map();
  const warnings = [];
  const documentElement = { lang: '' };

  const document = {
    documentElement,
    querySelectorAll: () => [],
    addEventListener(name, handler) {
      if (!documentListeners.has(name)) documentListeners.set(name, []);
      documentListeners.get(name).push(handler);
    },
    dispatchEvent(event) {
      documentEvents.push(event);
      for (const handler of documentListeners.get(event.type) || []) handler(event);
    },
  };
  const window = {
    addEventListener(name, handler) {
      if (!windowListeners.has(name)) windowListeners.set(name, []);
      windowListeners.get(name).push(handler);
    },
    dispatchEvent(event) {
      for (const handler of windowListeners.get(event.type) || []) handler(event);
    },
    Host: host || { available: false },
  };

  const context = vm.createContext({
    window,
    document,
    localStorage: {
      getItem: key => (store.has(key) ? store.get(key) : null),
      setItem: (key, value) => store.set(key, String(value)),
    },
    Host: window.Host,
    CustomEvent: class CustomEvent {
      constructor(type, options) {
        this.type = type;
        this.detail = options?.detail;
      }
    },
    console: { warn: (...args) => warnings.push(args), error() {}, log() {} },
    Intl,
    URLSearchParams,
  });
  vm.runInContext(catalogSource, context);
  vm.runInContext(i18nSource, context);

  return {
    api: window.I18n,
    window,
    document,
    documentElement,
    store,
    warnings,
    events: () => documentEvents.filter(event => event.type === 'langchanged'),
    emitDocument(type, detail) {
      const event = { type, detail };
      for (const handler of documentListeners.get(type) || []) handler(event);
      documentEvents.push(event);
    },
  };
}

function catalogKeysByBlock() {
  const languages = ['it', 'en', 'es', 'zh'];
  const lines = catalogSource.split(/\r?\n/);
  const blocks = new Map();
  const duplicates = [];
  let namespace = null;
  let language = null;

  for (const line of lines) {
    if (/^ {4}\},$/.test(line)) {
      namespace = null;
      language = null;
      continue;
    }
    const block = line.match(/^ {8}([A-Za-z_]\w*): \{$/);
    if (block) {
      if (languages.includes(block[1])) {
        language = block[1];
        if (namespace) blocks.set(`${namespace}/${language}`, new Set());
      } else {
        namespace = block[1];
        language = null;
      }
      continue;
    }
    if (/^ {8}\},$/.test(line)) {
      language = null;
      continue;
    }
    if (namespace && language) {
      const entry = line.match(/^ {12}(?:"([^"]+)"|([A-Za-z_]\w*)):/);
      if (entry) {
        const key = entry[1] || entry[2];
        const blockKey = `${namespace}/${language}`;
        if (!blocks.has(blockKey)) blocks.set(blockKey, new Set());
        if (blocks.get(blockKey).has(key)) duplicates.push(`${blockKey}.${key}`);
        blocks.get(blockKey).add(key);
      }
    }
  }

  return { blocks, duplicates };
}

test('initFromSettings prefers explicit settings over stored language and host OS', () => {
  const harness = createHarness({ stored: 'es' });

  harness.api.initFromSettings({
    settings: { language: 'zh' },
    resolvedLanguage: 'en-GB',
  });

  assert.equal(harness.api.getLang(), 'zh');
  assert.equal(harness.store.get('volt_lang'), 'zh');
  assert.deepEqual(harness.events().map(event => event.detail), ['zh']);
});

test('stored language migrates to the host when settings language is empty', () => {
  const calls = [];
  const harness = createHarness({
    stored: 'es',
    host: {
      available: true,
      call(method, payload) {
        calls.push([method, payload]);
        return Promise.resolve({});
      },
    },
  });

  harness.api.initFromSettings({
    settings: { language: '' },
    resolvedLanguage: 'en-GB',
  });

  assert.equal(harness.api.getLang(), 'es');
  assert.deepEqual(plain(calls), [['setLanguage', { language: 'es' }]]);
});

test('unsupported settings fall back to stored language and then host OS', () => {
  const harness = createHarness({ stored: '' });
  harness.api.initFromSettings({
    settings: { language: 'de' },
    resolvedLanguage: 'en-US',
  });
  assert.equal(harness.api.getLang(), 'en');

  const unsupported = createHarness({ stored: '' });
  unsupported.api.initFromSettings({
    settings: { language: 'de' },
    resolvedLanguage: 'fr-FR',
  });
  assert.equal(unsupported.api.getLang(), 'it');
  assert.deepEqual(unsupported.events(), []);
});

test('host language changes normalize cultures, persist once and ignore unsupported values', () => {
  const harness = createHarness();

  harness.api.onHostLanguageChanged({ language: 'es-ES' });
  assert.equal(harness.api.getLang(), 'es');
  assert.equal(harness.store.get('volt_lang'), 'es');
  assert.deepEqual(harness.events().map(event => event.detail), ['es']);

  harness.api.onHostLanguageChanged({ language: 'es-MX' });
  harness.api.onHostLanguageChanged({ language: 'de' });
  harness.api.onHostLanguageChanged(null);
  assert.equal(harness.api.getLang(), 'es');
  assert.equal(harness.events().length, 1);
});

test('bridge languageChanged documents reach the runtime listener', () => {
  const harness = createHarness();
  harness.emitDocument('languageChanged', { language: 'zh-CN' });

  assert.equal(harness.api.getLang(), 'zh');
  assert.equal(harness.documentElement.lang, 'zh');
  assert.deepEqual(harness.events().map(event => event.detail), ['zh']);
});

test('setLang rejects unsupported codes without persisting or notifying', () => {
  const harness = createHarness({ stored: 'it' });

  harness.api.setLang('de');

  assert.equal(harness.api.getLang(), 'it');
  assert.equal(harness.store.get('volt_lang'), 'it');
  assert.deepEqual(harness.events(), []);
  assert.equal(harness.warnings.length, 1);
});

test('setLang persists once, syncs the settings cache and is idempotent', async () => {
  const calls = [];
  const harness = createHarness({
    stored: 'en',
    host: {
      available: true,
      call(method, payload) {
        calls.push([method, payload]);
        return Promise.resolve({});
      },
    },
  });
  const settings = { language: 'en' };
  harness.window.__voltSettings = { get: () => settings };

  harness.api.setLang('es');
  assert.equal(harness.api.getLang(), 'es');
  assert.equal(harness.store.get('volt_lang'), 'es');
  assert.equal(settings.language, 'es');
  assert.deepEqual(plain(calls), [['setLanguage', { language: 'es' }]]);
  assert.equal(harness.events().length, 1);

  harness.api.setLang('es');
  assert.deepEqual(plain(calls), [['setLanguage', { language: 'es' }]]);
  assert.equal(harness.events().length, 1);
});

test('locale lookup follows catalog metadata and falls back to English formatting', () => {
  const harness = createHarness({ stored: 'zh' });
  assert.equal(harness.api.getLocale(), 'zh-CN');

  harness.api.onHostLanguageChanged({ language: 'es' });
  assert.equal(harness.api.getLocale(), 'es-ES');

  assert.equal(
    harness.api.number(1234.5, 'unsupported'),
    new Intl.NumberFormat('en-GB').format(1234.5)
  );
});

test('catalog blocks contain no duplicate keys in any language', () => {
  const { blocks, duplicates } = catalogKeysByBlock();

  assert.deepEqual(duplicates, []);
  assert.equal(blocks.size, 28, 'seven namespaces x four languages');
  for (const [block, keys] of blocks) {
    assert.ok(keys.size > 0, `${block}: empty catalog block`);
  }
  const total = [...blocks.values()].reduce((count, keys) => count + keys.size, 0);
  assert.ok(total > 3000, `expected full catalogs, got ${total} keys`);
});

test('literal I18n.t lookups resolve in the English core catalog', () => {
  const jsDirectory = path.join(root, 'src/VoltManager/wwwroot/js');
  const frontend = readdirSync(jsDirectory)
    .filter(file => file.endsWith('.js') && file !== 'i18n.catalogs.js')
    .map(file => readFileSync(path.join(jsDirectory, file), 'utf8'))
    .join('\n');

  const context = { window: {} };
  vm.runInNewContext(catalogSource, context);
  const englishCore = context.window.VoltI18nCatalogs.namespaces.core.en;

  const literals = [...new Set(
    [...frontend.matchAll(/I18n\.t\(\s*['"]([^'"]+)['"]/g)].map(match => match[1])
  )];
  assert.ok(literals.length > 20);
  const missing = literals.filter(key => !key.endsWith('_') && !(key in englishCore));
  assert.deepEqual(missing, []);
});

test('literal feature lookups name an existing namespace and English key', () => {
  const jsDirectory = path.join(root, 'src/VoltManager/wwwroot/js');
  const frontend = readdirSync(jsDirectory)
    .filter(file => file.endsWith('.js') && file !== 'i18n.catalogs.js')
    .map(file => readFileSync(path.join(jsDirectory, file), 'utf8'))
    .join('\n');

  const context = { window: {} };
  vm.runInNewContext(catalogSource, context);
  const namespaces = context.window.VoltI18nCatalogs.namespaces;

  const lookups = [...frontend.matchAll(
    /I18n\.feature\(\s*['"]([^'"]+)['"]\s*,\s*['"]([^'"]+)['"]/g
  )].map(match => [match[1], match[2]]);

  for (const [namespace, key] of lookups) {
    assert.ok(namespaces[namespace], `unknown feature namespace: ${namespace}`);
    assert.ok(key in namespaces[namespace].en, `missing ${namespace}.${key}`);
  }
});
