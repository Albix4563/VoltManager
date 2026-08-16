import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

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

test('process meter refreshes do not run long compositor transitions', () => {
  const body = rule(reorgCss, '#vm-monitoring-processes .process-meter-fill');
  assert.match(body, /transition\s*:\s*none/);
  assert.match(body, /will-change\s*:\s*auto/);
});
