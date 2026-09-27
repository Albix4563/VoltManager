import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const read = relative => fs.readFileSync(path.join(root, relative), 'utf8');

test('widget frontend supports solid, acrylic and transparent appearances', () => {
  const js = read('src/VoltManager/wwwroot/js/widgets.js');
  const css = read('src/VoltManager/wwwroot/css/widgets.css');

  assert.match(js, /APPEARANCE_MATERIALS\s*=\s*\['solid',\s*'acrylic',\s*'transparent'\]/);
  assert.match(js, /dataset\.material\s*=\s*widgetAppearance\.material/);
  assert.match(js, /--vm-widget-intensity/);
  for (const material of ['solid', 'acrylic', 'transparent']) {
    assert.match(css, new RegExp(`data-material=["']${material}["']`));
  }
  assert.match(css, /data-gradient="vertical"/);
  assert.match(css, /data-gradient="diagonal"/);
  assert.match(css, /data-gradient="radial"/);
  assert.match(css, /data-gradient="flat"/);
});

test('widget appearance persistence ignores stale responses and sends latest local state', () => {
  const js = read('src/VoltManager/wwwroot/js/settings.js');

  assert.match(js, /desiredWidgetAppearance\s*=\s*currentWidgetAppearance/);
  assert.match(js, /widgetAppearanceRequestSequence\s*=\s*0/);
  assert.match(js, /widgetAppearanceRequestsPending\s*=\s*0/);
  assert.match(js, /requestSequence\s*=\s*\+\+widgetAppearanceRequestSequence/);
  assert.match(js, /payload\s*=\s*normalizeWidgetAppearance\(next\)/);
  assert.match(js, /widgetAppearanceSliderTimer\s*=\s*0;\s*\n\s*widgetAppearanceRequestsPending\+\+/);
  assert.match(js, /requestSequence\s*===\s*widgetAppearanceRequestSequence/);
  assert.match(js, /renderWidgetsState\(state,\s*requestSequence\)/);
  assert.match(js, /!sequencedAppearance\s*&&\s*!appearanceRequestPending/);
  assert.match(js, /state\.appearance\s*=\s*normalizeWidgetAppearance\(desiredWidgetAppearance\)/);
});

test('widget native fallback remembers the requested appearance per hwnd', () => {
  const cs = read('src/VoltManager/WidgetWindow.xaml.cs');
  const manager = read('src/VoltManager/Services/WidgetManager.cs');

  assert.match(cs, /WidgetAppearance _requestedAppearance = new\(\)/);
  assert.match(cs, /IntPtr _requestedAppearanceHwnd/);
  assert.match(cs, /MatchesAppearance\(WidgetAppearance appearance\)[\s\S]*?_requestedAppearance\.ValueEquals\(appearance\)/);
  assert.match(cs, /_requestedAppearanceHwnd == source\.Handle/);
  assert.match(cs, /var requested = _requestedAppearance\.CloneNormalized\(\)/);
  assert.match(cs, /appearance '\{requested\.Material\}' failed; using solid/);
  assert.match(manager, /if \(!existing\.MatchesAppearance\(appearance\)\)\s*existing\.ApplyAppearance\(appearance\)/);
});
