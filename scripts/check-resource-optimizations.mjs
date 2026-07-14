import fs from 'node:fs';
import assert from 'node:assert/strict';

const dashboard = fs.readFileSync('src/VoltManager/wwwroot/js/dashboard.js', 'utf8');
const mainWindow = fs.readFileSync('src/VoltManager/MainWindow.xaml.cs', 'utf8');
const xaml = fs.readFileSync('src/VoltManager/MainWindow.xaml', 'utf8');
const effects = fs.readFileSync('src/VoltManager/wwwroot/js/effects.js', 'utf8');

assert.match(mainWindow, /private volatile bool _webViewVisible;/,
    'la visibilità WebView deve essere memorizzata senza accessi WPF cross-thread');
assert.match(mainWindow, /if \(_webViewVisible\)\s*_bridge\?\.PushEvent\("metrics", metrics\);/,
    'le metriche WebView devono usare lo stato thread-safe');
assert.doesNotMatch(mainWindow, /OnMetricsUpdated\(MetricsSnapshot metrics\)[\s\S]{0,200}IsVisible/,
    'OnMetricsUpdated non deve leggere proprietà WPF dal timer thread');
assert.match(dashboard, /hasBattery !== true/,
    'i poll batteria devono attendere il rilevamento della batteria');
assert.match(dashboard, /if \(activeOverride\.expiresAtUtc && !document\.hidden\)/,
    'il countdown override deve fermarsi quando la pagina è nascosta');
const pollingBoot = dashboard.lastIndexOf('syncDashboardPolling();');
assert.ok(pollingBoot > dashboard.indexOf("const overrideChip = document.getElementById('manual-override-chip');"),
    'il boot polling deve iniziare dopo tutte le const UI');
assert.match(dashboard, /function stopDashboardPolling\(\)/,
    'dashboard deve esporre uno stop centralizzato dei poll');
assert.match(dashboard, /document\.addEventListener\('visibilitychange', syncDashboardPolling\)/,
    'dashboard deve sincronizzare i poll con visibilitychange');
assert.match(dashboard, /if \(document\.hidden\) return;/,
    'ogni poll deve evitare lavoro quando la pagina è nascosta');
assert.match(xaml, /MinHeight="480" MinWidth="640"/,
    'la finestra deve supportare almeno 640x480');
assert.match(effects, /function syncRichEffects\(\)/,
    'gli effetti ricchi devono seguire tier e visibilità');
assert.doesNotMatch(effects, /querySelector\('\.vm-aurora'\)\?\.remove\(\)/,
    'aurora deve essere riusata, non ricreata a ogni visibilitychange');
assert.match(dashboard, /function processPollInterval\(\)/,
    'il polling processi deve adattarsi al perf tier host');

console.log('resource optimization checks passed');
