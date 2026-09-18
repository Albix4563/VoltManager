import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const layout = readFileSync(
  new URL('../src/VoltManager/wwwroot/js/ui-reorganization.layout.js', import.meta.url),
  'utf8'
);
const router = readFileSync(
  new URL('../src/VoltManager/wwwroot/js/ui-reorganization.js', import.meta.url),
  'utf8'
);
const css = readFileSync(
  new URL('../src/VoltManager/wwwroot/css/ui-reorganization.css', import.meta.url),
  'utf8'
);

test('dense views use the shared rail shell and keep compact navigation responsive', () => {
  for (const view of ['power-plans', 'automations', 'system-tools', 'settings']) {
    assert.match(layout, new RegExp(`denseShell\\('${view}'`));
  }

  assert.match(css, /\.vm-dense-shell\s*\{[^}]*grid-template-columns\s*:\s*220px\s+minmax\(0,\s*1fr\)/s);
  assert.match(css, /@media\s*\(max-width:\s*780px\)[\s\S]*\.vm-dense-shell\s*\{[^}]*grid-template-columns\s*:\s*1fr/s);
  assert.match(css, /@media\s*\(max-width:\s*780px\)[\s\S]*\.vm-subnav\s*\{[^}]*overflow-x\s*:\s*auto/s);
});

test('Power Plans exposes Keep Awake as its own persisted subview', () => {
  assert.match(layout, /\{ id: 'keep-awake', icon: 'bedtime_off', label: 'tab_keep_awake' \}/);
  assert.match(layout, /panel\('power-plans', 'keep-awake',[\s\S]*id="vm-keep-awake"/);
  assert.match(router, /move\(\$\('keep-awake-mount'\), \$\('vm-keep-awake'\)\)/);
});

test('Settings exposes Maintenance and relocates backup and diagnostics into it', () => {
  assert.match(layout, /\{ id: 'maintenance', icon: 'build', label: 'tab_maintenance' \}/);
  assert.match(layout, /panel\('settings', 'maintenance',[\s\S]*id="vm-settings-maintenance"/);
  assert.match(router, /move\(\$\('pref-backup'\), \$\('vm-settings-maintenance'\)\)/);
  assert.match(router, /btn-export-diagnostics[\s\S]*vm-settings-maintenance/);
});

test('motion tiers keep the live Processes panel out of transform animation', () => {
  assert.match(css, /\.vm-subview\.active\s*\{[^}]*animation/s);
  assert.match(css, /data-perf-tier="balanced"/);
  assert.match(css, /data-perf-tier="lite"/);
  assert.match(css, /prefers-reduced-motion:\s*reduce/);
  assert.match(
    css,
    /\.vm-subview\[data-vm-panel-group="monitoring"\]\[data-vm-panel="processes"\]\.active\s*\{[^}]*animation\s*:\s*none[^}]*transform\s*:\s*none/s
  );
});
