import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import vm from 'node:vm';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const source = fs.readFileSync(path.join(root, 'src/VoltManager/wwwroot/js/widget-appearance-helpers.js'), 'utf8');

function helpers() {
  const context = { window: {} };
  vm.runInContext(source, vm.createContext(context));
  return context.window.VoltWidgetAppearanceHelpers;
}

const translate = (key, fallback) => fallback;
const format = (template, values) => template.replace(/\{(\w+)\}/g, (_, key) => String(values[key]));

test('widget appearance status text handles global and override singular/plural counts', () => {
  const api = helpers();
  assert.equal(api.statusText([{ appearance: null }], translate, format), 'Applicato a 1 widget.');
  assert.equal(
    api.statusText([{ appearance: null }, { appearance: null }, { appearance: { material: 'acrylic' } }], translate, format),
    'Applicato a 2 widget. 1 usa un materiale personalizzato.',
  );
  assert.equal(
    api.statusText([{ appearance: null }, { appearance: { material: 'solid' } }, { appearance: { material: 'transparent' } }], translate, format),
    'Applicato a 1 widget. 2 usano un materiale personalizzato.',
  );
});

test('widget appearance choice maps global to null and material to merged appearance', () => {
  const api = helpers();
  const desired = { material: 'solid', tint: 'theme', gradient: 'vertical', intensity: 60 };
  assert.equal(api.choicePayload('global', desired), null);
  assert.deepEqual(
    JSON.parse(JSON.stringify(api.choicePayload('acrylic', desired))),
    { material: 'acrylic', tint: 'theme', gradient: 'vertical', intensity: 60 },
  );
});

test('widget appearance keyboard helper supports arrows, Home and End', () => {
  const api = helpers();
  assert.equal(api.rovingIndex(1, 'ArrowRight', 4), 2);
  assert.equal(api.rovingIndex(0, 'ArrowLeft', 4), 3);
  assert.equal(api.rovingIndex(2, 'ArrowUp', 4), 1);
  assert.equal(api.rovingIndex(2, 'ArrowDown', 4), 3);
  assert.equal(api.rovingIndex(2, 'Home', 4), 0);
  assert.equal(api.rovingIndex(1, 'End', 4), 3);
});

test('reset all clears overridden widgets sequentially and refetches after a failure', async () => {
  const api = helpers();
  const items = [
    { type: 'clock', appearance: { material: 'solid' } },
    { type: 'calendar', appearance: null },
    { type: 'usage', appearance: { material: 'acrylic' } },
  ];
  const calls = [];
  const final = await api.resetAll(
    items,
    async type => {
      calls.push(type);
      return { type };
    },
    async () => {
      calls.push('refetch');
      return { refetched: true };
    },
  );
  assert.deepEqual(calls, ['clock', 'usage']);
  assert.deepEqual(final, { type: 'usage' });

  const failedCalls = [];
  const recovered = await api.resetAll(
    items,
    async type => {
      failedCalls.push(type);
      if (type === 'usage') throw new Error('boom');
      return { type };
    },
    async () => {
      failedCalls.push('refetch');
      return { refetched: true };
    },
  );
  assert.deepEqual(failedCalls, ['clock', 'usage', 'refetch']);
  assert.deepEqual(recovered, { refetched: true });
});

test('every card of the legacy widgets view is relocated into the visible reorganized view', () => {
  const read = rel => fs.readFileSync(path.join(root, rel), 'utf8');
  const html = read('src/VoltManager/wwwroot/index.html');
  const reorg = read('src/VoltManager/wwwroot/js/ui-reorganization.js');
  const layout = read('src/VoltManager/wwwroot/js/ui-reorganization.layout.js');
  const start = html.indexOf('id="view-widgets"');
  assert.ok(start > 0);
  const end = html.indexOf('<!-- ============ VIEW', start);
  const legacyView = html.slice(start, end > 0 ? end : undefined);
  const cardIds = Array.from(legacyView.matchAll(/<div class="glass-panel[^"]*" id="([^"]+)"/g), m => m[1]);
  assert.deepEqual(cardIds.sort(), ['launcher-apps-card', 'widget-appearance-card', 'widgets-card']);
  for (const id of cardIds) {
    const call = `move($('${id}'), $('`;
    const at = reorg.indexOf(call);
    const moved = at >= 0 ? [null, reorg.slice(at + call.length, reorg.indexOf("'", at + call.length))] : null;
    assert.ok(moved, `${id} must be moved out of the hidden legacy view`);
    assert.match(layout, new RegExp(`id="${moved[1]}"`), `${moved[1]} target must exist in the widgets layout`);
  }
  assert.ok(reorg.indexOf("move($('widget-appearance-card')") < reorg.indexOf("move($('widgets-card')"),
    'the global style card comes before the widget list');
});
