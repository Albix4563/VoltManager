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
    innerHTML: '', textContent: '', dataset: {}, style: {}, children: [], hidden: false,
    append(...children) { this.children.push(...children); },
    replaceChildren(...children) { this.children = children; },
    get lastElementChild() { return this.children.at(-1); },
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
      hidden: false, documentElement: { dataset: {} }, createElement: element,
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
    for (const key of ['widget_cpu_temp', 'widget_cpu_cores', 'widget_temp_unavailable', 'dash_temp_unavailable']) {
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

test('i core supportati sono aggiornati senza ricreare la lista e scompaiono se assenti', () => {
  const { elements, metrics } = loadWidget('cpuTemp');
  const cores = Array.from({ length: 64 }, (_, index) => ({
    identifier: `/cpu/0/temp/${index}`, hardware: 'CPU 0', name: `CPU Core #${index + 1}`, value: 40 + index / 10,
  }));
  metrics({ cpuTemp: 55, cpuCoreTemps: cores });
  const list = elements.get('cpu-core-temps');
  assert.equal(list.hidden, false);
  assert.equal(list.children.length, 64);
  assert.equal(list.children[63].children[0].textContent, 'CPU Core #64');
  assert.equal(list.children[0].children[0].title, 'CPU 0');
  const firstRow = list.children[0];
  metrics({ cpuTemp: 56, cpuCoreTemps: cores.map(core => ({ ...core, value: 57.7 })) });
  assert.equal(list.children[0], firstRow);
  assert.equal(firstRow.lastElementChild.textContent, '58°C');
  for (const missing of [undefined, null, [], 'invalid']) {
    metrics({ cpuTemp: 56, cpuCoreTemps: missing });
    assert.equal(list.hidden, true);
    assert.equal(list.children.length, 0);
    assert.equal(elements.get('temp-cpu').textContent, '56°C');
    assert.equal(elements.get('temp-cpu').classList.contains('has-core-temps'), false);
  }
  metrics({ cpuTemp: 50, cpuCoreTemps: cores.slice(0, 2) });
  assert.equal(list.hidden, false);
  assert.equal(list.children.length, 2);
});

test('la lista core valida i valori e inserisce i nomi come testo, non HTML', () => {
  const { elements, metrics } = loadWidget('cpuTemp');
  const name = '<img src=x onerror=alert(1)>';
  metrics({ cpuTemp: 50, cpuCoreTemps: [
    ...invalidValues.map(value => ({ name: 'Core #1', value })),
    { name: 'Core #2', value: 0 }, { name: 'Core #3', value: -10 },
    null, { value: 40 }, { name: {}, value: 40 }, { name: '', value: 40 },
    { name, value: 45.2 },
  ] });
  const list = elements.get('cpu-core-temps');
  assert.equal(list.children.length, 1);
  const label = list.children[0].children[0];
  assert.equal(label.textContent, name);
  assert.equal(label.innerHTML, '');
  assert.equal(list.children[0].lastElementChild.textContent, '45°C');
});

test('la lista core è scorrevole e accessibile anche nelle dimensioni mini', () => {
  const css = readFileSync(new URL('../src/VoltManager/wwwroot/css/widgets.css', import.meta.url), 'utf8');
  assert.match(widgets, /id="cpu-core-temps" tabindex="0" hidden/);
  assert.match(widgets, /list\.setAttribute\('aria-label', t\('widget_cpu_cores'/);
  assert.match(css, /\.cpu-core-temps\s*\{[^}]*min-height:\s*0;[^}]*overflow-y:\s*auto;/);
  assert.match(css, /\.cpu-core-temps:focus-visible/);
  assert.match(css, /html\[data-size=mini\][^{]*cpuTemp[^}]*has-core-temps[^}]*font-size:\s*20px/);
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
