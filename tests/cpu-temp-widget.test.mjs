import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const read = file => readFileSync(new URL(`../src/VoltManager/wwwroot/js/${file}`, import.meta.url), 'utf8');
const widgets = read('widgets.js');
const dashboard = read('dashboard.js');
const invalidValues = [null, undefined, NaN, Infinity, -Infinity, '', ' ', '48', 'invalid', false, true, [], {}];

function element() {
  const classes = new Set();
  return {
    innerHTML: '', textContent: '', dataset: {}, style: {},
    classList: {
      contains: name => classes.has(name),
      remove: name => classes.delete(name),
      toggle(name, force) { if (force) classes.add(name); else classes.delete(name); },
    },
    addEventListener() {}, setAttribute() {},
  };
}

function loadWidget(type) {
  const elements = new Map();
  const handlers = new Map();
  const context = {
    window: {}, URLSearchParams, location: { search: `?w=${type}&s=medium` },
    document: {
      hidden: false, documentElement: { dataset: {} },
      getElementById(id) {
        if (!elements.has(id)) elements.set(id, element());
        return elements.get(id);
      },
      querySelector: () => null, querySelectorAll: () => [], addEventListener() {},
    },
    Host: { available: false, on: (name, handler) => handlers.set(name, handler) },
  };
  vm.runInNewContext(read('i18n.catalogs.js'), context);
  context.I18n = context.window.I18n = { t: key => context.window.VoltI18nCatalogs.namespaces.core.it[key] || key };
  vm.runInNewContext(widgets, context);
  return { elements, metrics: handlers.get('metrics') };
}

test('il widget CPU è selezionabile e localizzato in tutte le lingue', () => {
  const settings = read('settings.js');
  assert.match(settings, /WIDGET_TYPES = \[[^\]]*'cpuTemp'/);
  assert.match(settings, /cpuTemp:\s*'widget_cpu_temp'/);
  const context = { window: {} };
  vm.runInNewContext(read('i18n.catalogs.js'), context);
  for (const lang of ['it', 'en', 'es', 'zh']) {
    const catalog = context.window.VoltI18nCatalogs.namespaces.core[lang];
    for (const key of ['widget_cpu_temp', 'widget_temp_unavailable', 'dash_temp_unavailable']) {
      assert.ok(catalog[key], `${lang}/${key}`);
    }
  }
});

test('il widget CPU riceve le metriche e ripristina il valore dopo un dato assente', () => {
  const { elements, metrics } = loadWidget('cpuTemp');
  assert.match(elements.get('widget-root').innerHTML, /data-widget-type="cpuTemp"/);
  assert.equal(typeof metrics, 'function');
  metrics({ cpuTemp: 48.6 });
  const value = elements.get('temp-cpu');
  assert.equal(value.textContent, '49°C');
  for (const invalid of invalidValues) {
    metrics({ cpuTemp: invalid });
    assert.equal(value.textContent, 'Non disponibile');
    assert.equal(value.classList.contains('widget-value-unavailable'), true);
  }
  for (const [input, expected] of [[48.6, '49°C'], [0, '0°C']]) {
    metrics({ cpuTemp: input });
    assert.equal(value.textContent, expected);
    assert.equal(value.classList.contains('widget-value-unavailable'), false);
  }
});

test('il widget temperature combinato conserva CPU e GPU', () => {
  const { elements, metrics } = loadWidget('temps');
  metrics({ cpuTemp: 56.2, gpuTemp: 42.8 });
  assert.equal(elements.get('temp-cpu').textContent, '56°C');
  assert.equal(elements.get('temp-gpu').textContent, '43°C');
  metrics({ cpuTemp: null, gpuTemp: NaN });
  assert.equal(elements.get('temp-cpu').textContent, 'Non disponibile');
  assert.equal(elements.get('temp-gpu').textContent, 'Non disponibile');
});

test('il badge nell’app mostra temperatura e stato non disponibile senza falsi zero', () => {
  const source = dashboard.slice(dashboard.indexOf('    function setText('), dashboard.indexOf('    function setClockText('));
  const I18n = { t: () => 'Non disponibile' };
  const setTempBadge = vm.runInNewContext(`${source}; setTempBadge;`, { window: { I18n }, I18n });
  const badge = element();
  const label = element();
  for (const invalid of invalidValues) {
    badge.classList.toggle('hidden', true);
    setTempBadge(badge, label, invalid);
    assert.equal(badge.classList.contains('hidden'), false);
    assert.equal(label.textContent, 'Non disponibile');
  }
  setTempBadge(badge, label, 54.3);
  assert.equal(label.textContent, '54°C');
  setTempBadge(badge, label, 0);
  assert.equal(label.textContent, '0°C');
});
