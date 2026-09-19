import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const themeCss = readFileSync(
  new URL('../src/VoltManager/wwwroot/css/theme-colors.css', import.meta.url),
  'utf8'
);
const polishCss = readFileSync(
  new URL('../src/VoltManager/wwwroot/css/polish.css', import.meta.url),
  'utf8'
);
const widgetOverrideCss = readFileSync(
  new URL('../src/VoltManager/wwwroot/css/widget-plan-override.css', import.meta.url),
  'utf8'
);
const themeScript = readFileSync(
  new URL('../src/VoltManager/wwwroot/js/theme.js', import.meta.url),
  'utf8'
);

function expectThemeOwned(selector, requiredTokens) {
  const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const match = themeCss.match(new RegExp(`${escaped}\\s*\\{([^}]*)\\}`, 's'));
  assert.ok(match, `${selector} must be explicitly owned by theme-colors.css`);
  for (const token of requiredTokens) {
    assert.match(match[1], token, `${selector} must resolve through ${token}`);
  }
}

test('reported plan selector and process surfaces follow the active theme', () => {
  expectThemeOwned('.segmented-control-bg', [/--vm-surface/, /--vm-border/]);
  expectThemeOwned('.processes-card', [/--vm-surface-high/, /--vm-surface/]);
  expectThemeOwned('.process-row', [/--vm-surface-high/, /--vm-border/]);
  expectThemeOwned('.process-rank', [/--vm-accent-rgb/]);
  expectThemeOwned('.process-meter', [/--vm-bg/, /--vm-border/]);
});

test('legacy neutral controls no longer keep fixed navy fills', () => {
  expectThemeOwned('.toggle-label-large', [/--vm-surface-high/, /--vm-border/]);
  expectThemeOwned('.mini-toggle', [/--vm-surface-high/, /--vm-border/]);
});

test('successful update state uses the selected accent instead of prototype cyan', () => {
  expectThemeOwned('#update-status.ok', [/--vm-accent/]);
});

test('shared modal glow and changelog scrollbar follow the selected accent', () => {
  assert.match(polishCss, /\.glass-modal\s*\{[^}]*--vm-accent-rgb/s);
  assert.match(polishCss, /\.changelog-scroll::\-webkit-scrollbar-thumb\s*\{[^}]*--vm-border/s);
});

test('widget power-plan override uses the live palette instead of the prototype navy shell', () => {
  assert.match(widgetOverrideCss, /\.widget-override-overlay\s*\{[^}]*--vm-bg/s);
  assert.match(widgetOverrideCss, /\.widget-override-dialog\s*\{[^}]*--vm-surface-high[^}]*--vm-surface/s);
  assert.doesNotMatch(widgetOverrideCss, /rgba\(34,\s*50,\s*86|rgba\(14,\s*26,\s*46|rgba\(5,\s*12,\s*24/);
});

test('applying a theme updates the document state and live CSS variables', () => {
  const properties = new Map();
  const document = {
    documentElement: {
      dataset: { themeColor: 'blue' },
      style: { setProperty: (name, value) => properties.set(name, value) },
    },
  };
  const window = {};
  vm.runInContext(themeScript, vm.createContext({ window, document }));
  const palette = {
    background: '#101010', surface: '#202020', surfaceElevated: '#303030', border: '#404040',
    text: '#ffffff', mutedText: '#aaaaaa', primary: '#ff0000', secondary: '#cc0000',
    hover: '#ee0000', onPrimary: '#000000',
  };

  const applied = window.VoltTheme.apply(' RED ', palette);

  assert.equal(applied, 'red');
  assert.equal(document.documentElement.dataset.themeColor, 'red');
  assert.equal(properties.get('--vm-accent'), '#ff0000');
  assert.equal(properties.get('--vm-bg'), '#101010');
  assert.equal(properties.get('--md-sys-color-secondary-container'), '#ff0000');
  assert.equal(properties.get('--vm-accent-rgb'), '255 0 0');
});
