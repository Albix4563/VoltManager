import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import vm from 'node:vm';

const root = new URL('../src/VoltManager/wwwroot/', import.meta.url);
const read = path => readFileSync(new URL(path, root), 'utf8');
const script = read('js/style-controller.js');
const palette = {
  background: '#101010', surface: '#202020', surfaceElevated: '#303030', primary: '#FF0000',
  secondary: '#CC0000', hover: '#EE0000', text: '#FFFFFF', mutedText: '#AAAAAA',
  border: '#404040', onPrimary: '#000000',
};

function mutableSheet() {
  const cssRules = [];
  const insertions = [];
  return {
    cssRules,
    insertions,
    insertRule(text, index = cssRules.length) {
      const values = new Map();
      const declaration = {
        values,
        setProperty(name, value) { values.set(name, String(value)); },
        removeProperty(name) { values.delete(name); },
      };
      const wrapper = { cssRules: [{ style: declaration }] };
      cssRules.splice(index, 0, wrapper);
      insertions.push(text);
      return index;
    },
    deleteRule(index) { cssRules.splice(index, 1); },
  };
}

function fixture({
  bootstrap,
  reduced = false,
  sheet = true,
  cssReadThrows = false,
  cssWriteThrows = false,
  hostAvailable = true,
  hostThrows = [],
  runtimeFallback = false,
} = {}) {
  const listeners = new Map();
  const hostListeners = new Map();
  const cssValues = new Map();
  const mediaListeners = [];
  const media = { matches: reduced, addEventListener(_name, fn) { mediaListeners.push(fn); } };
  const customRule = {
    selectorText: ':root[data-theme="custom"]',
    style: {
      setProperty(k, v) {
        if (cssWriteThrows) throw Error('CSSOM write denied');
        cssValues.set(k, v);
      },
    },
  };
  const tokenSheet = cssReadThrows
    ? { href: '/css/tokens.css', get cssRules() { throw Error('CSSOM read denied'); } }
    : { href: '/css/tokens.css', cssRules: [{ cssRules: [customRule] }] };
  const runtimeSheet = runtimeFallback ? mutableSheet() : null;
  const document = {
    hidden: false, documentElement: { dataset: {}, style: { setProperty() { throw Error('root inline token'); } } },
    styleSheets: sheet ? [tokenSheet, ...(runtimeSheet ? [runtimeSheet] : [])] : [],
    adoptedStyleSheets: [],
    addEventListener(name, fn) { listeners.set(name, [...(listeners.get(name) || []), fn]); },
    dispatchEvent(event) { for (const fn of listeners.get(event.type) || []) fn(event); },
  };
  const Host = {
    on(name, fn) {
      if (hostThrows.includes(name)) throw Error(`host listener denied: ${name}`);
      hostListeners.set(name, [...(hostListeners.get(name) || []), fn]);
    },
  };
  const window = { matchMedia: () => media, __voltThemeState: bootstrap };
  if (hostAvailable) window.Host = Host;
  const context = vm.createContext({ window, document, CustomEvent: class {
    constructor(type, options) { this.type = type; this.detail = options.detail; }
  } });
  vm.runInContext(script, context);
  return { window, document, Host, context, listeners, hostListeners, cssValues, mediaListeners, media, runtimeSheet,
    fire(name, detail) { document.dispatchEvent({ type: name, detail }); },
    host(name, value) { for (const fn of hostListeners.get(name) || []) fn(value); } };
}

test('all seven native preset accents resolve through tokens and root attributes', () => {
  const f = fixture();
  const tokens = read('css/tokens.css');
  const accents = { blue: '#3B82F6', red: '#EF4444', green: '#22C55E', orange: '#F97316',
    purple: '#A855F7', pink: '#EC4899', gray: '#94A3B8' };
  for (const [name, color] of Object.entries(accents)) {
    assert.equal(f.window.Volt.style.applyTheme(name), name);
    assert.equal(f.document.documentElement.dataset.theme, name);
    assert.match(tokens, new RegExp(`:root\\[data-theme="${name}"\\]\\s*\\{[^}]*--vm-accent:\\s*${color}`, 'i'));
  }
  assert.equal(f.cssValues.size, 0);
});

test('bootstrap wins over default and custom CSSOM accepts only complete safe palettes', () => {
  const f = fixture({ bootstrap: { themeColor: 'custom', palette, customColor: '#F00' } });
  assert.equal(f.document.documentElement.dataset.theme, 'custom');
  assert.equal(f.cssValues.get('--vm-accent'), '#FF0000');
  assert.equal(f.cssValues.get('--vm-accent-rgb'), '255 0 0');
  const before = f.cssValues.size;
  for (const invalid of [null, {}, { ...palette, primary: 'url(javascript:alert(1))' },
    { ...palette, border: '#1234' }]) {
    assert.equal(f.window.Volt.style.applyTheme('custom', invalid), 'blue');
    assert.equal(f.cssValues.size, before);
  }
  assert.equal(fixture({ sheet: false }).window.Volt.style.applyTheme('custom', palette), 'blue');
  assert.equal(fixture().document.documentElement.dataset.theme, 'blue');
});

test('custom color normalization rejects empty and unsafe input while preserving native alpha compositing', () => {
  const style = fixture().window.Volt.style;
  assert.equal(style.normalizeCustomColor('#abc'), '#AABBCC');
  assert.equal(style.normalizeCustomColor(' #123456 '), '#123456');
  assert.equal(style.normalizeCustomColor('#F008'), '#8D080F');
  for (const invalid of ['', '   ', null, '#12', '#12345', '#GGG', 'red', 'url(javascript:alert(1))']) {
    assert.equal(style.normalizeCustomColor(invalid), null);
  }
});

test('missing or throwing CSSOM and host listeners fail closed without breaking initialization', () => {
  assert.doesNotThrow(() => fixture({ hostAvailable: false }));
  const readFailure = fixture({ cssReadThrows: true });
  assert.equal(readFailure.window.Volt.style.applyTheme('custom', palette), 'blue');
  const writeFailure = fixture({ cssWriteThrows: true });
  assert.equal(writeFailure.window.Volt.style.applyTheme('custom', palette), 'blue');

  const hostFailure = fixture({ hostThrows: ['resourceProfileChanged'] });
  assert.equal(hostFailure.hostListeners.get('metrics')?.length, 1);
  assert.equal(hostFailure.hostListeners.get('resourceProfileChanged'), undefined);
  assert.equal(hostFailure.hostListeners.get('themeChanged')?.length, 1);
  assert.equal(hostFailure.hostListeners.get('animationLevelChanged')?.length, 1);
  assert.doesNotThrow(() => hostFailure.window.Volt.style.init());
  assert.equal(hostFailure.hostListeners.get('themeChanged')?.length, 1);
});

test('host binding retries only missing channels after transient registration failures', () => {
  const hostThrows = ['resourceProfileChanged'];
  const f = fixture({ hostThrows });
  const style = f.window.Volt.style;

  assert.equal(style.bindHost(f.Host), false);
  assert.equal(f.hostListeners.get('metrics')?.length, 1);
  assert.equal(f.hostListeners.get('resourceProfileChanged'), undefined);
  assert.equal(f.hostListeners.get('themeChanged')?.length, 1);
  assert.equal(f.hostListeners.get('animationLevelChanged')?.length, 1);

  hostThrows.length = 0;
  assert.equal(style.bindHost(f.Host), true);
  assert.equal(f.hostListeners.get('resourceProfileChanged')?.length, 1);
  for (const callbacks of f.hostListeners.values()) assert.equal(callbacks.length, 1);

  f.host('resourceProfileChanged', { profile: 'gaming', reducedEffects: true });
  assert.equal(style.getState().resourceProfile, 'gaming');
  assert.equal(style.getState().resourceLite, true);

  style.init();
  for (const callbacks of f.hostListeners.values()) assert.equal(callbacks.length, 1);
});

test('RAM hysteresis, resource profiles, hardware levels and reduced motion share one resolver', () => {
  const f = fixture();
  const style = f.window.Volt.style;
  style.setHardwareInfo({ ramTotalGb: 32, logicalCores: 8 });
  assert.equal(f.document.documentElement.dataset.effects, 'full');
  style.setRamPercent(85);
  assert.equal(f.document.documentElement.dataset.effects, 'off');
  style.setRamPercent(80);
  assert.equal(f.document.documentElement.dataset.perf, 'lite');
  style.setRamPercent(75);
  assert.equal(f.document.documentElement.dataset.effects, 'full');
  style.setResourceProfile({ profile: 'gaming', reducedEffects: true });
  assert.equal(f.document.documentElement.dataset.effects, 'off');
  style.setResourceProfile({ profile: 'full', reducedEffects: false });
  style.setHardwareInfo({ ramTotalGb: 4, logicalCores: 2 });
  assert.equal(f.document.documentElement.dataset.anim, 'low');
  style.setAnimationLevel('high');
  assert.equal(f.document.documentElement.dataset.effects, 'full');
  f.mediaListeners[0]({ matches: true });
  assert.equal(f.document.documentElement.dataset.motion, 'reduced');
  assert.equal(f.document.documentElement.dataset.effects, 'off');
  f.mediaListeners[0]({ matches: false });
  f.document.hidden = true;
  f.fire('visibilitychange');
  assert.equal(f.document.documentElement.dataset.effects, 'off');
  f.document.hidden = false;
  f.fire('visibilitychange');
  assert.equal(f.document.documentElement.dataset.effects, 'full');
});

test('repeated initialization binds host, document and media listeners once', () => {
  const f = fixture();
  vm.runInContext(script, f.context);
  f.window.Volt.style.init();
  for (const callbacks of f.hostListeners.values()) assert.equal(callbacks.length, 1);
  for (const callbacks of f.listeners.values()) assert.equal(callbacks.length, 1);
  assert.equal(f.mediaListeners.length, 1);
  f.host('animationLevelChanged', { level: 'low' });
  assert.equal(f.window.Volt.style.getState().animationSetting, 'low');
});

test('runtime style owner falls back to layered CSSOM and prunes repeated disconnected renders', () => {
  const f = fixture({ runtimeFallback: true });
  const runtime = f.window.Volt.style.runtime;
  let element = { dataset: {}, isConnected: true };

  assert.equal(runtime.set(element, 'width', '25%'), true);
  assert.equal(runtime.stats().backend, 'existing');
  assert.equal(runtime.stats().constructable, false);
  assert.match(f.runtimeSheet.insertions[0], /^@layer overrides\s*\{/);
  assert.equal(f.runtimeSheet.cssRules[0].cssRules[0].style.values.get('width'), '25%');

  for (let i = 0; i < 500; i += 1) {
    element.isConnected = false;
    element = { dataset: {}, isConnected: true };
    assert.equal(runtime.set(element, 'width', (i % 101) + '%'), true);
  }

  const stats = runtime.stats();
  assert.equal(stats.records, 1);
  assert.equal(stats.rules, 1);
});

test('shared CSS ownership, loading order and LAN static routes are explicit', () => {
  const files = readdirSync(new URL('css/', root)).filter(name => name.endsWith('.css'));
  assert.equal(files.flatMap(name => [...read(`css/${name}`).matchAll(/@layer\s+[\w,\s]+;/g)]).length, 1);
  for (const name of files.filter(name => name !== 'tokens.css')) {
    if (name === 'layers.css') continue;
    assert.doesNotMatch(read(`css/${name}`), /^\s*--vm-(?:bg|accent|motion|widget-|space-|radius-)[\w-]*\s*:/m);
  }
  for (const page of ['index.html', 'widgets.html', 'remote/index.html']) {
    const html = read(page);
    assert.ok(html.indexOf('layers.css') < html.indexOf('tokens.css'));
    assert.ok(html.indexOf('tokens.css') < html.indexOf(page.startsWith('remote/') ? 'remote.css' : 'app.css'));
  }
  const index = read('index.html');
  assert.ok(index.indexOf('ui-reorganization.css') < index.indexOf('theme-colors.css'));
  assert.doesNotMatch(read('js/bootstrap-loader.js'), /createElement\(['"](?:link|style)['"]\)/);
  const service = readFileSync(new URL('../Services/LanRemote/LanRemoteControlService.cs', root), 'utf8');
  assert.match(service, /MapGet\("\/css\/layers\.css"/);
  assert.match(service, /MapGet\("\/css\/tokens\.css"/);
});
