import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';

const uiPath = new URL('../src/VoltManager/wwwroot/js/update-suspension.js', import.meta.url);
const mainWindowPath = new URL('../src/VoltManager/MainWindow.xaml.cs', import.meta.url);
const settingsPath = new URL('../src/VoltManager/wwwroot/js/settings.js', import.meta.url);

const uiSource = fs.readFileSync(uiPath, 'utf8');
const mainWindowSource = fs.readFileSync(mainWindowPath, 'utf8');
const settingsSource = fs.readFileSync(settingsPath, 'utf8');

test('update suspension UI exposes exactly the requested day presets', () => {
    assert.match(uiSource, /SUPPORTED_DAYS\s*=\s*\[1, 5, 7, 12\]/);
    for (const days of [1, 5, 7, 12]) {
        assert.match(uiSource, new RegExp(`option value="${days}"`));
    }
});

test('update suspension uses the host snooze deadline and can resume immediately', () => {
    assert.match(uiSource, /Host\.call\('snoozeUpdate', \{ minutes: days \* DAY_MINUTES \}\)/);
    assert.match(uiSource, /Host\.call\('setAutoUpdateChecks', \{ enabled: true \}\)/);
    assert.match(uiSource, /Manual update checks remain available/);
});

test('settings copy reports the fifteen-minute automatic cadence', () => {
    assert.match(uiSource, /every 15 minutes/);
    assert.match(uiSource, /ogni 15 minuti/);
});

test('main WebView injects the suspension settings module after navigation', () => {
    assert.match(mainWindowSource, /LoadUpdateSuspensionUi\(core\)/);
    assert.match(mainWindowSource, /update-suspension\.js\?v=suspend1/);
});

test('manual update check remains wired to the normal checkForUpdates RPC', () => {
    assert.match(settingsSource, /Host\.call\('checkForUpdates'\)/);
});
