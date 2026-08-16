import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const dashboardJs = readFileSync(
  new URL('../src/VoltManager/wwwroot/js/dashboard.js', import.meta.url),
  'utf8'
);
const appCss = readFileSync(
  new URL('../src/VoltManager/wwwroot/css/app.css', import.meta.url),
  'utf8'
);
const reorgCss = readFileSync(
  new URL('../src/VoltManager/wwwroot/css/ui-reorganization.css', import.meta.url),
  'utf8'
);

function rule(css, selector) {
  const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const match = css.match(new RegExp(`${escaped}\\s*\\{([^}]*)\\}`, 's'));
  assert.ok(match, `${selector} rule must exist`);
  return match[1];
}

test('process subview does not animate the whole live panel when it becomes active', () => {
  const body = rule(reorgCss, '#vm-monitoring-processes.vm-subview.active');
  assert.match(body, /animation\s*:\s*none/);
  assert.match(body, /transform\s*:\s*none/);
});

test('process meters update without long compositor transform transitions', () => {
  const fn = dashboardJs.match(/function setProcessMeter\(fill, pct\)\s*\{([\s\S]*?)\n\s*\}/);
  assert.ok(fn, 'setProcessMeter must exist');
  assert.match(fn[1], /fill\.style\.width/);
  assert.doesNotMatch(fn[1], /fill\.style\.transform/);

  const body = rule(appCss, '.process-meter-fill');
  assert.doesNotMatch(body, /transition\s*:\s*transform/);
});

test('process rows can actually be removed from layout without fighting display grid', () => {
  const body = rule(appCss, '.process-row.hidden');
  assert.match(body, /display\s*:\s*none/);
});

test('language changes translate the stable process DOM instead of rebuilding it', () => {
  const handler = dashboardJs.match(/document\.addEventListener\('langchanged',\s*\(\)\s*=>\s*\{([\s\S]*?)\n\s*\}\);/);
  assert.ok(handler, 'process langchanged handler must exist');
  assert.doesNotMatch(handler[1], /procBuilt\s*=\s*false/);
  assert.doesNotMatch(handler[1], /procRows\s*=\s*\[\]/);
});
