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

const settingsJs = readFileSync(
  new URL('../src/VoltManager/wwwroot/js/settings.js', import.meta.url),
  'utf8'
);
const mainWindowXaml = readFileSync(
  new URL('../src/VoltManager/MainWindow.xaml', import.meta.url),
  'utf8'
);
const mainWindowCs = readFileSync(
  new URL('../src/VoltManager/MainWindow.xaml.cs', import.meta.url),
  'utf8'
);
const widgetWindowCs = readFileSync(
  new URL('../src/VoltManager/WidgetWindow.xaml.cs', import.meta.url),
  'utf8'
);

const widgetTypes = ['clock', 'calendar', 'usage', 'temps', 'power', 'plans', 'launcher', 'apps', 'actions', 'brightness', 'processes', 'memory'];

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

test('launcher and apps widgets split categories and use secure WebView2 file drops', () => {
  assert.match(widgetsJs, /'launcher',\s*'apps'/, 'apps must follow launcher in the widget type list');
  assert.match(
    widgetsJs,
    /launcherItems\s*=\s*launcherAllItems\.filter\(item\s*=>\s*!item\.hidden\s*&&\s*launcherCategoryOf\(item\)\s*===\s*launcherCategory\)/,
    'launcher entries must be filtered by the active games/apps category'
  );
  assert.match(widgetsJs, /postMessageWithAdditionalObjects\(\{\s*kind:\s*'launcherDropFiles'\s*\},\s*files\)/);
  assert.match(widgetsJs, /addEventListener\('dragover',[\s\S]*?e\.preventDefault\(\)/);
  assert.match(widgetsJs, /addEventListener\('drop',[\s\S]*?e\.preventDefault\(\)/);
  assert.match(widgetsCss, /\.launcher-drop-overlay\s*\{[^}]*border:\s*2px dashed[^}]*opacity:\s*0/s);
  assert.match(widgetsCss, /\.desktop-widget\[data-layout=horizontal\][^\{]*\.launcher-grid\s*\{[^}]*grid-auto-flow:\s*column/s);
  assert.match(widgetsCss, /\.desktop-widget\[data-layout=vertical\][^\{]*\.launcher-grid\s*\{[^}]*grid-auto-flow:\s*row/s);
});

test('launcher settings expose orientation separately from the three size presets', () => {
  assert.match(settingsJs, /const WIDGET_PRESETS = \['mini', 'medium', 'large'\]/);
  assert.doesNotMatch(settingsJs, /LAUNCHER_WIDGET_PRESETS/);
  assert.match(settingsJs, /const WIDGET_ORIENTATIONS = \['horizontal', 'vertical'\]/);
  assert.match(settingsJs, /data-widget-orientation/);
  assert.match(settingsJs, /Host\.call\('setWidgetOrientation', \{ type, orientation \}\)/);
  assert.match(settingsJs, /var selected = preset === sizeKey/);
});

test('launcher layout is deterministic, fixed-size and scrolls instead of shrinking icons', () => {
  assert.match(widgetsJs, /params\.get\('o'\)/);
  assert.match(widgetsJs, /data-layout="' \+ orientation \+ '"/);
  assert.doesNotMatch(widgetsJs, /ResizeObserver|observeLauncherLayout/);
  assert.match(widgetsJs, /launcherGrid\.scrollLeft \+= e\.deltaY/);
  assert.doesNotMatch(widgetsJs, /launcher-name/);
  assert.match(widgetsJs, /class="widget-resize-cap" id="widget-resize"/);
  assert.doesNotMatch(widgetsJs, /widget-resize-grip|south_east/);
  assert.match(widgetsJs, /id="widget-drag"/);
  assert.match(widgetsJs, /if \(e\.target\.closest\('button'\)\) return;[\s\S]*?beginWidgetDrag/);
  assert.match(widgetsCss, /--launcher-icon-size:\s*32px/);
  assert.match(widgetsCss, /--launcher-icon-size:\s*40px/);
  assert.match(widgetsCss, /--launcher-icon-size:\s*52px/);
  assert.match(widgetsCss, /--launcher-leading-cap:\s*34px/);
  assert.match(widgetsCss, /--launcher-resize-cap:\s*14px/);
  assert.match(widgetsCss, /--launcher-body-padding:\s*6px/);
  assert.match(widgetsCss, /grid-auto-columns:\s*var\(--launcher-slot-size\)/);
  assert.match(widgetsCss, /grid-auto-rows:\s*var\(--launcher-slot-size\)/);
  assert.match(widgetsCss, /width:\s*calc\(var\(--launcher-slot-size\) - 6px\)/);
  assert.match(widgetsCss, /\.desktop-widget\[data-layout=horizontal\]\s*\{[^}]*flex-direction:\s*row/s);
  assert.match(widgetsCss, /\.desktop-widget\[data-layout=vertical\] \.launcher-empty \.widget-muted\s*\{[^}]*display:\s*none/s);
  assert.match(widgetsCss, /\.widget-resize-cap\s*\{[^}]*flex:\s*0 0 var\(--launcher-resize-cap\)/s);
  assert.match(widgetsJs, /const manageLabel = t\('widget_launcher_manage', 'Manage apps'\)/);
  assert.match(widgetsJs, /orientation === 'vertical' \? ' title="' \+ esc\(emptyHint\)/);
});

test('widget chrome and the main window enforce readable minimums', () => {
  assert.match(widgetsCss, /html\[data-size=mini\] \.widget-title\s*\{[^}]*font-size:\s*11px/s);
  assert.match(widgetsCss, /html\[data-size=mini\] \.widget-action\s*\{[^}]*width:\s*28px[^}]*height:\s*28px/s);
  assert.match(widgetsCss, /\.widget-stat label\s*\{[^}]*font-size:\s*11px/s);
  assert.match(mainWindowXaml, /MinHeight="600" MinWidth="900"/);
  assert.match(mainWindowCs, /UpdateAdaptiveMinimumSize\(\)/);
  assert.match(mainWindowCs, /MonitorFromWindow/);
  assert.match(mainWindowCs, /GetDpiForMonitor/);
  assert.match(mainWindowCs, /msg == WmExitSizeMove/);
  assert.doesNotMatch(mainWindowCs, /LocationChanged\s*\+=/);
  assert.match(mainWindowCs, /Math\.Min\(DesiredMinWidth, workWidth\)/);
  assert.match(widgetWindowCs, /HtRight = new\(0xB\)/);
  assert.match(widgetWindowCs, /HtBottom = new\(0xF\)/);
  assert.match(widgetWindowCs, /if \(_nativeResizeInProgress\)[\s\S]*?_relayoutPendingDuringNativeResize = true;[\s\S]*?return;/);
});
