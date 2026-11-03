import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const themeSource = readFileSync(
  new URL('../src/VoltManager/wwwroot/js/theme.js', import.meta.url),
  'utf8'
);
const themeCss = readFileSync(
  new URL('../src/VoltManager/wwwroot/css/theme-colors.css', import.meta.url),
  'utf8'
);

const cssVariables = new Set(
  [...themeCss.matchAll(/(--[a-zA-Z0-9_-]+)\s*:/g)].map(match => match[1])
);

function palette(overrides = {}) {
  return {
    background: '#101010',
    surface: '#202020',
    surfaceElevated: '#303030',
    primary: '#ff0000',
    secondary: '#cc0000',
    hover: '#ee0000',
    text: '#ffffff',
    mutedText: '#aaaaaa',
    border: '#404040',
    onPrimary: '#000000',
    ...overrides,
  };
}

function loadTheme(extraWindow = {}) {
  const properties = new Map();
  const document = {
    documentElement: {
      dataset: {},
      style: { setProperty: (name, value) => properties.set(name, value) },
    },
  };
  const window = { ...extraWindow };
  vm.runInNewContext(themeSource, { window, document });
  return { api: window.VoltTheme, properties, document };
}

test('every CSS variable written by the frontend theme bridge is declared by theme-colors.css', () => {
  const written = [...new Set(
    [...themeSource.matchAll(/setProperty\('(--[^']+)'/g)].map(match => match[1])
  )];
  assert.ok(written.length >= 30, `expected a complete palette bridge, got ${written.length} variables`);
  const missing = written.filter(name => !cssVariables.has(name));
  assert.deepEqual(missing, [], 'theme.js would write variables with no stylesheet contract');
});

test('legacy Material tokens stay synchronized with their semantic counterparts', () => {
  const pairs = [
    ['--vm-bg', '--md-sys-color-background'],
    ['--vm-surface', '--md-sys-color-surface'],
    ['--vm-surface-high', '--md-sys-color-surface-container-high'],
    ['--vm-text', '--md-sys-color-on-surface'],
    ['--vm-muted', '--md-sys-color-on-surface-variant'],
    ['--vm-border', '--md-sys-color-outline'],
    ['--vm-accent', '--md-sys-color-secondary-container'],
    ['--vm-accent-text', '--md-sys-color-on-secondary-container'],
  ];
  for (const [semantic, legacy] of pairs) {
    assert.ok(themeSource.includes(`setProperty('${semantic}'`), `${semantic} must be applied`);
    assert.ok(themeSource.includes(`setProperty('${legacy}'`), `${legacy} must be applied`);
    assert.ok(cssVariables.has(semantic), `${semantic} must be declared in theme-colors.css`);
    assert.ok(cssVariables.has(legacy), `${legacy} must be declared in theme-colors.css`);
  }
});

test('theme normalization accepts only shipped keys and trims user input', () => {
  const { api } = loadTheme();
  for (const name of ['blue', 'red', 'green', 'orange', 'purple', 'pink', 'gray']) {
    assert.equal(api.normalize(name), name);
  }
  assert.equal(api.normalize(' RED '), 'red');
  assert.equal(api.normalize('custom'), 'blue');
  assert.equal(api.normalize('teal'), 'blue');
  assert.equal(api.normalize(null), 'blue');
  assert.equal(api.normalize(42), 'blue');
});

test('an injected native catalog drives the first paint and later theme changes', () => {
  const { api, properties, document } = loadTheme({
    __voltThemeCatalog: {
      blue: palette(),
      red: palette({ primary: '#00ff00', background: '#111111' }),
    },
  });

  assert.equal(api.apply('red'), 'red');
  assert.equal(document.documentElement.dataset.themeColor, 'red');
  assert.equal(properties.get('--vm-accent'), '#00ff00');
  assert.equal(properties.get('--vm-bg'), '#111111');
  assert.equal(properties.get('--md-sys-color-secondary-container'), '#00ff00');
});

test('a custom palette is applied verbatim and reports the custom theme', () => {
  const { api, properties, document } = loadTheme();
  const custom = palette({ primary: '#12ab34' });

  assert.equal(api.apply(' custom ', custom), 'custom');
  assert.equal(document.documentElement.dataset.themeColor, 'custom');
  assert.equal(properties.get('--vm-accent'), '#12ab34');
  assert.equal(properties.get('--vm-accent-rgb'), '18 171 52');
});

test('custom theme without a palette falls back to blue without mutating the page', () => {
  const { api, properties, document } = loadTheme();
  assert.equal(api.apply('custom'), 'blue');
  assert.equal(document.documentElement.dataset.themeColor, undefined);
  assert.equal(properties.size, 0);
});

test('unknown theme names normalize to blue while preserving a caller palette', () => {
  const { api, document } = loadTheme();
  assert.equal(api.apply('teal', palette()), 'blue');
  assert.equal(document.documentElement.dataset.themeColor, 'blue');
});

test('isSafePalette requires the full palette and six-digit hex colors', () => {
  const { api } = loadTheme();
  const complete = palette();
  assert.equal(api.isSafePalette(complete), true);
  assert.equal(api.isSafePalette({ ...complete, primary: '#f00' }), false);
  assert.equal(api.isSafePalette({ ...complete, primary: 'red' }), false);
  assert.equal(api.isSafePalette({ ...complete, primary: null }), false);
  assert.equal(api.isSafePalette(palette({ text: '#FFFFFF' })), true);

  for (const key of Object.keys(complete)) {
    const partial = { ...complete };
    delete partial[key];
    assert.equal(api.isSafePalette(partial), false, `missing ${key} must be rejected`);
  }
  assert.equal(api.isSafePalette(null), false);
});

test('unsafe palette values are rejected before any CSS variable is written', () => {
  const { api, properties, document } = loadTheme();
  const unsafe = palette({ primary: 'url(javascript:bad)' });

  assert.equal(api.apply('custom', unsafe), 'blue');
  assert.equal(properties.size, 0);
  assert.equal(document.documentElement.dataset.themeColor, undefined);
});

test('custom alpha colors composite over the shared base background', () => {
  const { api } = loadTheme();
  assert.equal(api.normalizeCustomColor('#fff0'), '#0B1120');
  assert.equal(api.normalizeCustomColor('#fffF'), '#FFFFFF');
  assert.equal(api.normalizeCustomColor('#abc'), '#AABBCC');
  assert.equal(api.normalizeCustomColor('#123456'), '#123456');
  assert.equal(api.normalizeCustomColor(' #123456 '), '#123456');
  assert.equal(api.normalizeCustomColor('#12'), null);
  assert.equal(api.normalizeCustomColor('#GGG'), null);
  assert.equal(api.normalizeCustomColor(null), null);
});

test('reapplying the same theme is idempotent', () => {
  const { api, properties } = loadTheme({ __voltThemeCatalog: { blue: palette() } });
  api.apply('blue');
  const snapshot = [...properties.entries()];
  api.apply('blue');
  assert.deepEqual([...properties.entries()], snapshot);
});
