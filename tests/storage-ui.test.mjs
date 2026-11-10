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
