import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const widgetsJs = readFileSync(
  new URL('../src/VoltManager/wwwroot/js/widgets.js', import.meta.url),
  'utf8'
);
const widgetsCss = readFileSync(
  new URL('../src/VoltManager/wwwroot/css/widgets.css', import.meta.url),
  'utf8'
);
const appCss = readFileSync(
  new URL('../src/VoltManager/wwwroot/css/app.css', import.meta.url),
  'utf8'
);

const widgetTypes = ['clock', 'calendar', 'usage', 'temps', 'power', 'plans', 'launcher', 'actions', 'brightness', 'processes', 'memory'];

test('desktop widgets expose their type and keep fixed chrome shrink-safe', () => {
  assert.match(
    widgetsJs,
    /data-size="' \+ size \+ '" data-widget-type="' \+ type \+ '"/,
    'desktop widget markup must expose data-widget-type without changing its RPC contract'
  );

  assert.match(widgetsCss, /\.widget-header\s*\{[^}]*min-width:\s*0/s);
  assert.match(widgetsCss, /\.widget-title\s*\{[^}]*min-width:\s*0/s);
  assert.match(widgetsCss, /\.widget-body\s*\{[^}]*min-width:\s*0[^}]*overflow:\s*hidden/s);
  assert.match(widgetsCss, /\.widget-action\s*\{[^}]*flex:\s*0\s+0\s+auto/s);
  assert.match(
    widgetsCss,
    /\.widget-title\s*>\s*span:not\(\.material-symbols-outlined\)\s*\{[^}]*min-width:\s*0[^}]*overflow:\s*hidden[^}]*text-overflow:\s*ellipsis[^}]*white-space:\s*nowrap/s
  );
});

test('all widget types have size-aware layout rules inside the existing window sizes', () => {
  for (const type of widgetTypes) {
    assert.match(
      widgetsCss,
      new RegExp(`\\.desktop-widget\\[data-widget-type=${type}\\]`),
      `${type} needs a type-scoped responsive rule`
    );
  }

  assert.match(widgetsCss, /html\[data-size=medium\][^\{]*data-widget-type=power[^\{]*\.power-row\s*\{[^}]*padding:/s);
  assert.match(widgetsCss, /html\[data-size=large\][^\{]*data-widget-type=power[^\{]*\.power-row\s*\{[^}]*padding:/s);
  assert.match(widgetsCss, /html\[data-size=medium\][^\{]*data-widget-type=usage[^\{]*\.widget-stat\s*\{[^}]*padding:/s);
  assert.match(widgetsCss, /html\[data-size=mini\][^\{]*data-widget-type=temps[^\{]*\.temp-row\s*\{[^}]*padding:/s);
  assert.match(widgetsCss, /data-widget-type=calendar[^\{]*\.calendar-grid/s);
  assert.match(widgetsCss, /data-widget-type=plans[^\{]*\.plan-selector/s);
});

test('widget settings cards wrap dynamic values and respond to card width', () => {
  assert.match(
    appCss,
    /\.widgets-category-card\s+\.startup-detail-value\s*\{[^}]*max-width:\s*100%[^}]*white-space:\s*normal[^}]*overflow-wrap:\s*anywhere/s
  );
  assert.match(
    appCss,
    /\.widgets-category-card\s+\.widget-monitor-select\s*\{[^}]*min-width:\s*0[^}]*max-width:\s*100%[^}]*overflow-wrap:\s*anywhere/s
  );
  assert.match(appCss, /@container\s*\(max-width:\s*420px\)/);
  assert.match(appCss, /\.widgets-category-card\s+\.widget-size-row\s*\{[^}]*flex-direction:\s*column/s);
  assert.match(appCss, /\.widgets-category-card\s+\.widget-placement-row\s*\{[^}]*flex-direction:\s*column/s);
  assert.match(appCss, /\.widgets-category-card\s+\.widget-size-control\s*\{[^}]*max-width:\s*100%/s);
  assert.match(appCss, /\.widgets-category-card\s+\.widget-anchor-wrap\s*\{[^}]*max-width:\s*100%/s);
});
