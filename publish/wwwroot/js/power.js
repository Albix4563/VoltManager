/**
 * Gestione Energetica: automation rules editor, debounced save.
 */
(function () {
    if (!Host.available) return;

    let settings = null;
    let saveTimer = null;

    const ruleIds = ['saver', 'balanced', 'performance'];

    function ruleById(id) {
        return settings.rules.find(r => r.id === id);
    }

    function loadIntoUi() {
        ruleIds.forEach(id => {
            const rule = ruleById(id);
            if (!rule) return;
            document.getElementById('rule-' + id + '-threshold').value = rule.thresholdPct;
            document.getElementById('rule-' + id + '-minutes').value = rule.durationMinutes;
            document.getElementById('rule-' + id + '-toggle').checked = rule.enabled;
        });
        document.getElementById('master-toggle').checked = settings.masterAutomationEnabled;
    }

    function scheduleSave() {
        clearTimeout(saveTimer);
        saveTimer = setTimeout(() => {
            Host.call('saveSettings', settings).catch(err => console.error('saveSettings failed', err));
        }, 400);
    }

    function clamp(value, min, max, fallback) {
        const n = Number(value);
        if (!isFinite(n) || n < min || n > max) return fallback;
        return n;
    }

    function wireUi() {
        ruleIds.forEach(id => {
            document.getElementById('rule-' + id + '-threshold').addEventListener('change', (e) => {
                const rule = ruleById(id);
                rule.thresholdPct = clamp(e.target.value, 1, 99, rule.thresholdPct);
                e.target.value = rule.thresholdPct;
                scheduleSave();
            });
            document.getElementById('rule-' + id + '-minutes').addEventListener('change', (e) => {
                const rule = ruleById(id);
                rule.durationMinutes = clamp(e.target.value, 1, 60, rule.durationMinutes);
                e.target.value = rule.durationMinutes;
                scheduleSave();
            });
            document.getElementById('rule-' + id + '-toggle').addEventListener('change', (e) => {
                ruleById(id).enabled = e.target.checked;
                scheduleSave();
            });
        });
        document.getElementById('master-toggle').addEventListener('change', (e) => {
            settings.masterAutomationEnabled = e.target.checked;
            scheduleSave();
        });
    }

    Host.call('getSettings').then(res => {
        settings = res.settings;
        loadIntoUi();
        wireUi();
        // Expose for settings.js (preferences card shares the same object).
        window.__voltSettings = {
            get: () => settings,
            save: scheduleSave,
            startWithWindows: res.startWithWindows,
        };
        document.dispatchEvent(new CustomEvent('settingsloaded'));
    }).catch(err => console.error('getSettings failed', err));
})();
