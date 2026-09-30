import test from 'node:test';
import assert from 'node:assert/strict';
import { createFrontendHarness, event, flush, richNode } from './helpers/frontend-characterization.mjs';

function settingsHarness() {
  const settings = {
    font: 'inter',
    animationLevel: 'auto',
    themeColor: 'cyan',
    autoUpdates: { enabled: true, updateChannel: 'stable' },
    widgets: { enabled: false, appearance: { material: 'solid', tint: 'theme', gradient: 'vertical', intensity: 60 }, items: [] },
  };
  let saves = 0;
  const h = createFrontendHarness({
    script: 'settings.js',
    preload: ['widget-appearance-helpers.js'],
    responses: {
      getWidgetsState: () => structuredClone(settings.widgets),
      setWidgetsMaster: payload => ({ ...structuredClone(settings.widgets), enabled: payload.enabled }),
      setWidgetAppearanceOverride: ({ type, appearance }) => ({
        ...structuredClone(settings.widgets),
        items: [{ type, enabled: false, pinned: false, size: 'medium', appearance }],
      }),
    },
    windowValues: {
      VoltFont: { apply: value => value, stackFor: value => value },
      VoltAnimationLevel: {
        recommended: () => 'medium', resolveLevel: level => level === 'auto' ? 'medium' : level,
        exceedsRecommended: () => false, hardwareTier: () => 'balanced',
      },
      VoltTheme: { apply() {} },
    },
    beforeRun({ document, window }) {
      const font = document.getElementById('font-select');
      font.appendChild(richNode(document, { tagName: 'OPTION' }));
      font.children[0].value = 'inter';
      const jet = richNode(document, { tagName: 'OPTION' });
      jet.value = 'jetbrains';
      font.appendChild(jet);
      const animation = document.getElementById('animation-level-select');
      const auto = richNode(document, { tagName: 'OPTION' });
      auto.value = 'auto';
      animation.appendChild(auto);
      window.__voltSettings = { get: () => settings, save: () => { saves += 1; } };
    },
  });
  return { h, settings, get saves() { return saves; } };
}

test('settingsloaded hydrates controls once and keeps listener mounting idempotent', async () => {
  const fixture = settingsHarness();
  const { h, settings } = fixture;
  h.document.dispatchEvent(event('settingsloaded'));
  h.document.dispatchEvent(event('settingsloaded'));
  await flush();

  assert.equal(h.document.getElementById('font-select').value, 'inter');
  assert.equal(h.document.getElementById('font-select').listenerCount('change'), 1);
  assert.equal(h.document.getElementById('lang-select').listenerCount('change'), 1);
  assert.equal(h.document.getElementById('toggle-widgets-master').listenerCount('click'), 1);
  assert.equal(h.host.handlers.get('fontChanged')?.length, 1);
  assert.equal(h.host.handlers.get('themeChanged')?.length, 1);
  assert.equal(settings.widgets.items.length, 12, 'hydration canonicalizes all shipped widget types');
});

test('settings controls preserve bridge payload contracts and local setting mutations', async () => {
  const fixture = settingsHarness();
  const { h, settings } = fixture;
  h.document.dispatchEvent(event('settingsloaded'));
  await flush();

  const font = h.document.getElementById('font-select');
  font.value = 'jetbrains';
  font.dispatchEvent(event('change'));
  assert.equal(settings.font, 'jetbrains');
  assert.equal(fixture.saves, 1);

  const master = h.document.getElementById('toggle-widgets-master');
  master.dataset.on = 'false';
  master.dispatchEvent(event('click'));
  await flush();
  const call = h.host.calls.find(item => item.method === 'setWidgetsMaster');
  assert.deepEqual(JSON.parse(JSON.stringify(call?.payload)), { enabled: true });
  assert.equal(settings.widgets.enabled, true);
});

test('per-widget appearance override sends the effective appearance payload unchanged', async () => {
  const { h } = settingsHarness();
  h.document.dispatchEvent(event('settingsloaded'));
  await flush();

  const card = richNode(h.document, { dataset: { widgetType: 'clock', widgetRow: '' } });
  const choice = richNode(h.document, {
    tagName: 'BUTTON',
    dataset: { widgetType: 'clock', widgetAppearanceChoice: 'acrylic' },
  });
  choice.setAttribute('aria-checked', 'false');
  card.querySelectorAll = selector => selector === '[data-widget-appearance-choice]' ? [choice] : [];
  const target = {
    closest(selector) {
      if (selector === '[data-widget-row]') return card;
      if (selector === '[data-widget-appearance-choice]') return choice;
      return null;
    },
  };
  h.document.getElementById('widgets-card').dispatchEvent(event('click', { target }));
  await flush();

  const call = h.host.calls.find(item => item.method === 'setWidgetAppearanceOverride');
  assert.deepEqual(JSON.parse(JSON.stringify(call?.payload)), {
    type: 'clock',
    appearance: { material: 'acrylic', tint: 'theme', gradient: 'vertical', intensity: 60 },
  });
});
