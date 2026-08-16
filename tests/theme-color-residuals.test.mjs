import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const themeCss = readFileSync(
  new URL('../src/VoltManager/wwwroot/css/theme-colors.css', import.meta.url),
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
