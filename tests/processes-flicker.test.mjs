import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const reorgCss = readFileSync(
  new URL('../src/VoltManager/wwwroot/css/ui-reorganization.css', import.meta.url),
  'utf8'
);
const polishCss = readFileSync(
  new URL('../src/VoltManager/wwwroot/css/polish.css', import.meta.url),
  'utf8'
);
const reorgLayoutJs = readFileSync(
  new URL('../src/VoltManager/wwwroot/js/ui-reorganization.layout.js', import.meta.url),
  'utf8'
);
const effectsJs = readFileSync(
  new URL('../src/VoltManager/wwwroot/js/effects.js', import.meta.url),
  'utf8'
);

function rule(css, selector) {
  const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const match = css.match(new RegExp(`${escaped}\\s*\\{([^}]*)\\}`, 's'));
  assert.ok(match, `${selector} rule must exist`);
  return match[1];
}

function sourceSection(source, startMarker, endMarker) {
  const start = source.indexOf(startMarker);
  assert.notEqual(start, -1, `${startMarker} section must exist`);
  const end = source.indexOf(endMarker, start);
  assert.notEqual(end, -1, `${endMarker} section boundary must exist`);
  return source.slice(start, end);
}

test('process subview does not animate the whole live panel when it becomes active', () => {
  assert.match(
    reorgLayoutJs,
    /panel\('monitoring',\s*'processes',/,
    'layout must keep processes in the monitoring process subview'
  );

  const selector = '.vm-subview[data-vm-panel-group="monitoring"][data-vm-panel="processes"].active';
  const body = rule(reorgCss, selector);
  assert.match(body, /animation\s*:\s*none/);
  assert.match(body, /transform\s*:\s*none/);
});

test('process meter refreshes do not run long compositor transitions', () => {
  const body = rule(reorgCss, '#vm-monitoring-processes .process-meter-fill');
  assert.match(body, /transition\s*:\s*none/);
  assert.match(body, /will-change\s*:\s*auto/);
});

test('process card is excluded from pointer-tracking spotlight and sheen repaints', () => {
  const pointerSection = sourceSection(
    effectsJs,
    'function onPointerMove(e)',
    '// ---- Button ripple ----'
  );

  assert.match(
    pointerSection,
    /processes-card/,
    'pointermove must explicitly exclude the high-churn process card'
  );
});

test('process row hover does not create a moving compositor layer', () => {
  const baseBody = rule(polishCss, '#vm-monitoring-processes .process-row');
  const hoverBody = rule(polishCss, '#vm-monitoring-processes .process-row:hover');

  assert.match(hoverBody, /transform\s*:\s*none/);
  assert.match(baseBody, /transition\s*:/);
  assert.doesNotMatch(
    baseBody,
    /transition\s*:[^;]*\btransform\b/,
    'the live row must not transition transform while its meters refresh'
  );
});
