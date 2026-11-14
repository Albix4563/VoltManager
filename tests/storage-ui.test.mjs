import assert from 'node:assert/strict';
import test from 'node:test';
import vm from 'node:vm';
import { readFileSync } from 'node:fs';

const read = file => readFileSync(new URL(`../src/VoltManager/wwwroot/js/${file}`, import.meta.url), 'utf8');
const source = read('storage.js');
const flush = () => new Promise(resolve => setImmediate(resolve));

function harness(call, { missingOverlay = false, locale = 'it-IT' } = {}) {
  const document = {
    activeElement: null,
    body: {},
    contains: () => true,
    listeners: new Map(),
    getElementById(id) {
      if (missingOverlay && id === 'storage-clean-overlay') return null;
      if (!elements.has(id)) elements.set(id, node(id));
      return elements.get(id);
    },
    createElement: () => node(),
    addEventListener(name, handler) {
      if (!this.listeners.has(name)) this.listeners.set(name, []);
      this.listeners.get(name).push(handler);
    },
    removeEventListener(name, handler) {
      const list = this.listeners.get(name);
      if (list) this.listeners.set(name, list.filter(fn => fn !== handler));
    },
    emit(name, eventObj = {}) {
      for (const fn of [...(this.listeners.get(name) || [])]) fn(eventObj);
    },
  };
  function node(id = '') {
    const classes = new Set(id === 'storage-clean-overlay' ? ['hidden'] : []);
    const listeners = new Map();
    const attributes = new Map();
    const el = {
      id, children: [], value: '', textContent: '', disabled: false, handlers: {}, focused: false,
      classList: {
        add: (...names) => names.forEach(name => classes.add(name)),
        remove: (...names) => names.forEach(name => classes.delete(name)),
        contains: name => classes.has(name),
      },
      setAttribute: (name, value) => attributes.set(name, String(value)),
      getAttribute: name => attributes.get(name),
      focus() { this.focused = true; document.activeElement = this; },
      append(...children) {
        this.children.push(...children);
        if (!this.value && children[0]?.value) this.value = children[0].value;
      },
      replaceChildren() { this.children = []; this.value = ''; },
      addEventListener(name, handler) {
        this.handlers[name] = handler;
        if (!listeners.has(name)) listeners.set(name, []);
        listeners.get(name).push(handler);
      },
      removeEventListener(name, handler) {
        if (this.handlers[name] === handler) delete this.handlers[name];
        const list = listeners.get(name);
        if (list) listeners.set(name, list.filter(fn => fn !== handler));
      },
      dispatch(name, eventObj = {}) {
        for (const fn of [...(listeners.get(name) || [])]) fn(eventObj);
      },
    };
    return el;
  }
  const elements = new Map();
  const calls = [];
  const api = {
    state: { view: 'overview', subviews: { monitoring: 'hardware' } },
    t: (key, values) => key + (values ? JSON.stringify(values) : ''),
  };
  const Host = { available: true, call: async (...args) => { calls.push(args); return call(...args); } };
  const I18n = { getLocale: () => locale };
  const context = {
    window: { VoltUiReorg: api, Host, I18n }, Host, Intl, document,
  };
  vm.runInNewContext(source, context);
  return {
    elements, calls, document,
    events: {
      langchanged(nextLocale) {
        if (nextLocale) locale = nextLocale;
        document.emit('langchanged');
      },
      voltuisubviewchanged: () => document.emit('voltuisubviewchanged'),
    },
    async activate() {
      api.state.view = 'monitoring'; api.state.subviews.monitoring = 'storage';
      document.emit('voltuisubviewchanged'); await flush();
    },
  };
}

test('pulizia apre conferma integrata predefinita su Annulla, blocca duplicati e invia conferma esplicita', async () => {
  let complete;
  const h = harness(method => method === 'getStorageDrives' ? [drive] : new Promise(resolve => { complete = resolve; }));
  await h.activate();
  const clean = h.elements.get('storage-clean-temp');
  const overlay = h.elements.get('storage-clean-overlay');
  const cancel = h.elements.get('storage-clean-cancel');
  const confirm = h.elements.get('storage-clean-confirm');
  clean.focus();
  const pending = clean.handlers.click();
  assert.equal(overlay.classList.contains('hidden'), false);
  assert.equal(overlay.classList.contains('flex'), true);
  assert.equal(overlay.getAttribute('aria-hidden'), 'false');
  assert.equal(h.document.activeElement, cancel);
  assert.equal(clean.disabled, true);
  assert.equal(h.calls.length, 1);
  await clean.handlers.click();
  await h.elements.get('storage-analyze').handlers.click();
  await h.elements.get('storage-refresh').handlers.click();
  await h.elements.get('storage-defrag').handlers.click();
  assert.equal(h.calls.length, 1);
  confirm.dispatch('click');
  await flush();
  assert.equal(overlay.classList.contains('hidden'), true);
  assert.equal(overlay.getAttribute('aria-hidden'), 'true');
  assert.equal(h.calls.length, 2);
  assert.equal(h.calls.at(-1)[0], 'cleanStorageTemp');
  assert.deepEqual(JSON.parse(JSON.stringify(h.calls.at(-1)[1])), { confirmed: true });
  assert.equal(h.calls.at(-1)[2].timeoutMs, 35000);
  complete({ confirmed: true, deletedFiles: 2, freedBytes: 2048, skippedCount: 1, partial: true });
  await pending;
  assert.equal(h.calls.at(-1)[0], 'getStorageDrives');
  assert.match(h.elements.get('storage-status').textContent, /^storage_clean_partial/);
  h.document.emit('langchanged');
  assert.match(h.elements.get('storage-status').textContent, /^storage_clean_partial/);
  assert.equal(clean.disabled, false);
});

test('annullamento dal modale, Escape o click sullo sfondo non avvia la pulizia e ripristina il focus', async () => {
  for (const mode of ['cancel', 'escape', 'backdrop']) {
    const h = harness(() => [drive]);
    await h.activate();
    const clean = h.elements.get('storage-clean-temp');
    const overlay = h.elements.get('storage-clean-overlay');
    clean.focus();
    const pending = clean.handlers.click();
    if (mode === 'cancel') h.elements.get('storage-clean-cancel').dispatch('click');
    else if (mode === 'escape') {
      let prevented = false;
      h.document.emit('keydown', { key: 'Escape', preventDefault() { prevented = true; } });
      assert.equal(prevented, true);
    } else overlay.dispatch('click', { target: overlay });
    await pending;
    assert.equal(h.calls.length, 1);
    assert.equal(overlay.classList.contains('hidden'), true);
    assert.equal(h.document.activeElement, clean);
    assert.equal(clean.disabled, false);
    assert.equal(h.elements.get('storage-status').textContent, 'storage_clean_cancelled');
  }
});

test('il modale intrappola Tab tra Annulla e Conferma e aggiorna i testi al cambio lingua', async () => {
  const h = harness(() => [drive]);
  await h.activate();
  const clean = h.elements.get('storage-clean-temp');
  const cancel = h.elements.get('storage-clean-cancel');
  const confirm = h.elements.get('storage-clean-confirm');
  clean.focus();
  const pending = clean.handlers.click();
  assert.equal(h.document.activeElement, cancel);
  let prevented = false;
  h.document.emit('keydown', { key: 'Tab', shiftKey: true, preventDefault() { prevented = true; } });
  assert.equal(prevented, true);
  assert.equal(h.document.activeElement, confirm);
  prevented = false;
  h.document.emit('keydown', { key: 'Tab', shiftKey: false, preventDefault() { prevented = true; } });
  assert.equal(prevented, true);
  assert.equal(h.document.activeElement, cancel);
  h.document.emit('langchanged');
  assert.equal(h.elements.get('storage-clean-title').textContent, 'storage_clean_modal_title');
  assert.equal(h.elements.get('storage-clean-confirm').textContent, 'storage_clean_modal_confirm');
  cancel.dispatch('click');
  await pending;
});

test('errore, risultato non valido o modale mancante gestiti senza rilanciare aggiornamenti', async () => {
  const missing = harness(() => [drive], { missingOverlay: true });
  await missing.activate();
  await missing.elements.get('storage-clean-temp').handlers.click();
  assert.equal(missing.calls.length, 1);
  assert.equal(missing.elements.get('storage-status').textContent, 'storage_clean_cancelled');

  for (const result of [{ confirmed: false }, new Error('offline'), { confirmed: true }]) {
    const h = harness(method => {
      if (method === 'getStorageDrives') return [drive];
      if (result instanceof Error) throw result;
      return result;
    });
    await h.activate();
    const pending = h.elements.get('storage-clean-temp').handlers.click();
    h.elements.get('storage-clean-confirm').dispatch('click');
    await pending;
    assert.equal(h.calls.length, 2);
    assert.equal(h.elements.get('storage-clean-temp').disabled, false);
    assert.equal(h.elements.get('storage-status').textContent,
      result.confirmed === false ? 'storage_clean_cancelled' : 'storage_clean_error');
  }
});

test('strumenti Windows disponibili anche senza unità, payload limitati e gestione errore', async () => {
  const h = harness(method => method === 'getStorageDrives' ? [] : { launched: true });
  await h.activate();
  for (const [id, tool] of [['storage-cleanmgr', 'cleanmgr'], ['storage-defrag', 'dfrgui']]) {
    assert.equal(h.elements.get(id).disabled, false);
    await h.elements.get(id).handlers.click();
    assert.equal(h.calls.at(-1)[0], 'openStorageTool');
    assert.equal(h.calls.at(-1)[1].tool, tool);
    assert.equal(h.elements.get('storage-status').textContent, 'storage_tool_opened');
  }
  const failed = harness(method => { if (method === 'getStorageDrives') return []; throw new Error('missing'); });
  await failed.activate();
  await failed.elements.get('storage-cleanmgr').handlers.click();
  assert.equal(failed.elements.get('storage-status').textContent, 'storage_tool_error');
  assert.equal(failed.elements.get('storage-cleanmgr').disabled, false);
});

const drive = { root: 'C:\\', label: 'System', format: 'NTFS', totalBytes: 1024 ** 3, usedBytes: 768 * 1024 ** 2, freeBytes: 256 * 1024 ** 2 };

test('Archiviazione carica le unità solo quando aperta, senza analisi automatica', async () => {
  const h = harness(() => [drive]);
  assert.equal(h.calls.length, 0);
  await h.activate();
  assert.equal(h.calls.length, 1);
  assert.equal(h.calls[0][0], 'getStorageDrives');
  assert.equal(h.elements.get('storage-summary').children.length, 4);
  assert.match(h.elements.get('storage-summary').children[0].children[1].textContent, /1 GiB/);
  assert.equal(h.elements.get('storage-analyze').disabled, false);
  await h.activate();
  assert.equal(h.calls.length, 1);
});

test('analisi su richiesta, ordinamento e nomi trattati come testo', async () => {
  const name = '<img src=x onerror=alert(1)>';
  const h = harness(method => method === 'getStorageDrives' ? [drive] : {
    entries: [{ name, bytes: 100 }, { name: 'Windows', bytes: 400 }, { name: 'invalid', bytes: NaN }],
    scannedBytes: 500, skippedCount: 2, partial: true,
  });
  await h.activate();
  await h.elements.get('storage-analyze').handlers.click();
  const rows = h.elements.get('storage-entries').children;
  assert.equal(h.calls[1][0], 'getStorageAnalysis');
  assert.equal(h.calls[1][1].root, drive.root);
  assert.equal(h.calls[1][2].timeoutMs, 35000);
  assert.equal(rows.length, 2);
  assert.equal(rows[0].children[0].textContent, 'Windows');
  assert.equal(rows[1].children[0].textContent, name);
  assert.match(h.elements.get('storage-status').textContent, /^storage_partial/);
  assert.equal(h.elements.get('storage-analyze').disabled, false);
  h.events.langchanged();
  assert.match(h.elements.get('storage-status').textContent, /^storage_partial/);
});

test('cambio lingua riformatta i byte del risultato e delle righe senza ripetere l’analisi', async () => {
  const scannedBytes = 1536;
  const h = harness(method => method === 'getStorageDrives' ? [drive] : {
    entries: [{ name: 'Dati', bytes: scannedBytes }],
    scannedBytes, skippedCount: 0, partial: false,
  });
  await h.activate();
  await h.elements.get('storage-analyze').handlers.click();
  const expected = locale => new Intl.NumberFormat(locale, { maximumFractionDigits: 1 }).format(1.5) + ' KiB';
  const statusSize = () => JSON.parse(h.elements.get('storage-status').textContent.slice('storage_complete'.length)).size;
  const rowSize = () => h.elements.get('storage-entries').children[0].children[1].textContent;
  assert.equal(statusSize(), expected('it-IT'));
  assert.equal(rowSize(), expected('it-IT'));
  h.events.langchanged('en-US');
  assert.equal(statusSize(), expected('en-US'));
  assert.equal(rowSize(), expected('en-US'));
  assert.equal(h.calls.length, 2);
  h.elements.get('storage-drive').dispatch('change');
  h.events.langchanged('it-IT');
  assert.equal(h.elements.get('storage-entries').children.length, 0);
});

test('dimensioni totali non valide restano indisponibili anche dopo un cambio lingua', async () => {
  for (const scannedBytes of [undefined, '1536', NaN, Infinity, -1]) {
    const h = harness(method => method === 'getStorageDrives' ? [drive] : {
      entries: [], scannedBytes, skippedCount: 0, partial: false,
    });
    await h.activate();
    await h.elements.get('storage-analyze').handlers.click();
    const statusSize = () => JSON.parse(h.elements.get('storage-status').textContent.slice('storage_complete'.length)).size;
    assert.equal(statusSize(), '—');
    h.events.langchanged('en-US');
    assert.equal(statusSize(), '—');
  }
});

test('scansioni non sovrapposte e controlli ripristinati dopo errore', async () => {
  let reject;
  const h = harness(method => method === 'getStorageDrives' ? [drive] : new Promise((_, fail) => { reject = fail; }));
  await h.activate();
  const analyze = h.elements.get('storage-analyze');
  const pending = analyze.handlers.click();
  assert.equal(analyze.disabled, true);
  assert.equal(h.elements.get('storage-drive').disabled, true);
  await analyze.handlers.click();
  assert.equal(h.calls.filter(call => call[0] === 'getStorageAnalysis').length, 1);
  reject(new Error('access denied'));
  await pending;
  assert.equal(analyze.disabled, false);
  assert.equal(h.elements.get('storage-refresh').disabled, false);
  assert.equal(h.elements.get('storage-status').textContent, 'storage_error');
});

test('unità assenti o errore non bloccano l’interfaccia', async () => {
  const h = harness(() => []);
  await h.activate();
  assert.equal(h.elements.get('storage-analyze').disabled, true);
  assert.equal(h.elements.get('storage-status').textContent, 'storage_empty');
  const failed = harness(() => { throw new Error('offline'); });
  await failed.activate();
  assert.equal(failed.elements.get('storage-refresh').disabled, false);
  assert.equal(failed.elements.get('storage-status').textContent, 'storage_error');
});

test('sezione integrata in Monitoraggio con tabella semantica e caricamento modulo', () => {
  const layout = read('ui-reorganization.layout.js');
  assert.match(layout, /id: 'storage', icon: 'hard_drive', label: 'tab_storage'/);
  assert.match(layout, /panel\('monitoring', 'storage'/);
  assert.match(layout, /<label for="storage-drive"/);
  assert.match(layout, /id="storage-status" role="status" aria-live="polite"/);
  assert.match(layout, /<th scope="col" data-vm-i18n="storage_name"/);
  assert.match(read('bootstrap-loader.js'), /'js\/storage.js'/);
  assert.doesNotMatch(source, /setInterval|setTimeout|innerHTML/);
});

test('restyling archiviazione integra pulsanti standard, selettore accessibile e gerarchia visiva', () => {
  const layout = read('ui-reorganization.layout.js');
  assert.match(layout, /<button type="button" class="btn-ghost vm-ui-control" id="storage-refresh"><span class="material-symbols-outlined" aria-hidden="true">refresh<\/span><span data-vm-i18n="storage_refresh"><\/span><\/button>/);
  assert.match(layout, /<div class="vm-storage-drive-picker">/);
  assert.match(layout, /<label for="storage-drive" class="vm-storage-drive-label" data-vm-i18n="storage_drive"><\/label>/);
  assert.match(layout, /<select id="storage-drive" class="vm-storage-drive-select"><\/select>/);
  assert.match(layout, /<dl id="storage-summary" class="vm-storage-summary"><\/dl>/);
  assert.match(layout, /<div class="vm-divider"><\/div>/);
  assert.match(layout, /<h4 class="vm-section-title"><span class="material-symbols-outlined" aria-hidden="true">folder_open<\/span><span data-vm-i18n="storage_details"><\/span><\/h4>/);
  assert.match(layout, /<button type="button" class="btn-primary vm-ui-control" id="storage-analyze" disabled><span class="material-symbols-outlined" aria-hidden="true">search<\/span><span data-vm-i18n="storage_analyze"><\/span><\/button>/);
  assert.match(layout, /<p class="vm-panel-hint text-on-surface-variant" data-vm-i18n="storage_hint"><\/p>/);
  assert.match(layout, /<p id="storage-status" role="status" aria-live="polite"><\/p>/);
  assert.match(layout, /<div class="vm-storage-table-wrap"><table class="vm-storage-table"><caption class="sr-only" data-vm-i18n="storage_details"><\/caption>/);
});

test('regole CSS archiviazione rispettano i token di tema e gestiscono stati vuoti/focus/disabled', () => {
  const css = readFileSync(new URL('../src/VoltManager/wwwroot/css/ui-reorganization.css', import.meta.url), 'utf8');
  assert.match(css, /\.vm-storage-table-wrap:has\(tbody:empty\)\s*\{\s*display:\s*none;\s*\}/);
  assert.match(css, /\.vm-storage-summary\s*\{[^}]*background:\s*var\(--vm-border\)/);
  assert.match(css, /\.vm-storage-summary dd\s*\{[^}]*font-variant-numeric:\s*tabular-nums/);
  assert.match(css, /\.vm-storage select:focus-visible[^}]*outline:\s*2px solid var\(--vm-accent\)/);
  assert.match(css, /\.vm-storage button:disabled\s*\{[^}]*opacity:\s*\.5/);
  assert.match(css, /#storage-status:empty\s*\{\s*display:\s*none;\s*\}/);
  assert.match(css, /#storage-status\s*\{[^}]*background:\s*var\(--vm-surface-high\)/);
  assert.match(css, /\.vm-storage-summary:empty,\s*\.vm-storage-table-wrap:has\(tbody:empty\)/);
  assert.match(css, /\.vm-storage button:focus-visible\s*\{[^}]*outline:\s*2px solid var\(--vm-accent\)/);
  assert.match(css, /\.vm-storage \.btn-primary\s*\{[^}]*white-space:\s*normal/);
  assert.match(css, /\.vm-storage-summary\s*\{[^}]*minmax\(min\(100%, 150px\), 1fr\)/);
  const storageRules = css.slice(css.indexOf('.vm-storage {'), css.indexOf('.vm-view-heading {'));
  assert.doesNotMatch(storageRules, /#[0-9a-f]{3,8}\b/i);
  assert.doesNotMatch(storageRules, /\b(?:animation|transition)\s*:/);
});

test('conferma pulizia usa modale in-app tematizzato invece di MessageBox host', () => {
  const html = readFileSync(new URL('../src/VoltManager/wwwroot/index.html', import.meta.url), 'utf8');
  const dialogCss = readFileSync(new URL('../src/VoltManager/wwwroot/css/power-features.css', import.meta.url), 'utf8');
  const factory = readFileSync(new URL('../src/VoltManager/Bridge/BridgeHandlerFactory.cs', import.meta.url), 'utf8');
  assert.match(html, /id="storage-clean-overlay"[^>]*aria-hidden="true"/);
  assert.match(html, /class="vm-dialog-shell vm-storage-clean-dialog glass-modal"[^>]*role="dialog" aria-modal="true"/s);
  for (const key of [
    'storage_clean_modal_title', 'storage_clean_modal_subtitle', 'storage_clean_modal_scope',
    'storage_clean_modal_warning', 'storage_clean_modal_cancel', 'storage_clean_modal_confirm',
  ]) {
    assert.match(html, new RegExp(`data-vm-i18n="${key}"`));
  }
  assert.match(dialogCss, /#storage-clean-overlay:not\(\.hidden\)\s*\{\s*display:\s*flex;/);
  assert.match(dialogCss, /\.vm-storage-clean-dialog\s*\{\s*max-width:\s*540px;/);
  assert.match(dialogCss, /\.vm-storage-clean-dialog\s+\.vm-dialog-note\s*\{[^}]*--vm-border[^}]*--vm-surface-high/s);
  assert.match(factory, /new StorageRpcHandler\(new StorageService\(\)\)/);
  assert.doesNotMatch(factory, /Storage_ConfirmCleanTemp/);
});
