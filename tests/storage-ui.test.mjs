import assert from 'node:assert/strict';
import test from 'node:test';
import vm from 'node:vm';
import { readFileSync } from 'node:fs';

const read = file => readFileSync(new URL(`../src/VoltManager/wwwroot/js/${file}`, import.meta.url), 'utf8');
const source = read('storage.js');
const flush = () => new Promise(resolve => setImmediate(resolve));

function harness(call) {
  function node() {
    return {
      children: [], value: '', textContent: '', disabled: false, handlers: {},
      append(...children) {
        this.children.push(...children);
        if (!this.value && children[0]?.value) this.value = children[0].value;
      },
      replaceChildren() { this.children = []; this.value = ''; },
      addEventListener(name, handler) { this.handlers[name] = handler; },
    };
  }
  const elements = new Map();
  const events = {};
  const calls = [];
  const api = {
    state: { view: 'overview', subviews: { monitoring: 'hardware' } },
    t: (key, values) => key + (values ? JSON.stringify(values) : ''),
  };
  const Host = { available: true, call: async (...args) => { calls.push(args); return call(...args); } };
  const context = {
    window: { VoltUiReorg: api, Host }, Host, Intl,
    document: {
      getElementById(id) { if (!elements.has(id)) elements.set(id, node()); return elements.get(id); },
      createElement: node,
      addEventListener(name, handler) { events[name] = handler; },
    },
  };
  vm.runInNewContext(source, context);
  return {
    elements, calls, events,
    async activate() {
      api.state.view = 'monitoring'; api.state.subviews.monitoring = 'storage';
      events.voltuisubviewchanged(); await flush();
    },
  };
}

test('pulizia attende conferma host, blocca duplicati e conserva l’esito dopo aggiornamento', async () => {
  let complete;
  const h = harness(method => method === 'getStorageDrives' ? [drive] : new Promise(resolve => { complete = resolve; }));
  await h.activate();
  const clean = h.elements.get('storage-clean-temp');
  const pending = clean.handlers.click();
  assert.equal(clean.disabled, true);
  assert.equal(h.calls.at(-1)[0], 'cleanStorageTemp');
  assert.equal(h.calls.at(-1)[2].timeoutMs, 0);
  await clean.handlers.click();
  await h.elements.get('storage-analyze').handlers.click();
  await h.elements.get('storage-refresh').handlers.click();
  await h.elements.get('storage-defrag').handlers.click();
  assert.equal(h.calls.length, 2);
  complete({ confirmed: true, deletedFiles: 2, freedBytes: 2048, skippedCount: 1, partial: true });
  await pending;
  assert.equal(h.calls.at(-1)[0], 'getStorageDrives');
  assert.match(h.elements.get('storage-status').textContent, /^storage_clean_partial/);
  h.events.langchanged();
  assert.match(h.elements.get('storage-status').textContent, /^storage_clean_partial/);
  assert.equal(clean.disabled, false);
});

test('annullamento, errore e risultato non valido non rilanciano pulizia o aggiornamenti', async () => {
  for (const result of [{ confirmed: false }, new Error('offline'), { confirmed: true }]) {
    const h = harness(method => {
      if (method === 'getStorageDrives') return [drive];
      if (result instanceof Error) throw result;
      return result;
    });
    await h.activate();
    await h.elements.get('storage-clean-temp').handlers.click();
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
