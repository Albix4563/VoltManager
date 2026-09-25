/**
 * Gestione Energetica: automation rules editor, debounced save.
 * Heavy app detection: Windows GPU preferences + generic game/heavy workload heuristics.
 * Keep-awake mode: runtime Windows power request to prevent automatic sleep.
 */
(function () {
    if (!Host.available) return;

    let settings = null;
    let saveTimer = null;
    const ruleIds = ['saver', 'balanced', 'performance'];
    const planIds = ['powerSaver', 'balanced', 'performance'];


    function lang() {
        return window.I18n && I18n.getLang ? I18n.getLang() : 'it';
    }

    function tt(key) {
        return window.I18n && I18n.feature ? I18n.feature('power', key, lang()) : key;
    }


    function esc(value) {
        const div = document.createElement('div');
        div.textContent = value == null ? '' : String(value);
        return div.innerHTML;
    }

    function ruleById(id) {
        return settings.rules.find(r => r.id === id);
    }

    function setToggle(el, on) {
        if (el) el.dataset.on = on ? 'true' : 'false';
    }

    function appNameFromPath(path) {
        const file = String(path || '').split(/[\\/]/).pop() || 'App';
        return file.replace(/\.[^.]+$/, '') || 'App';
    }

    function normalizeCpuAutomation() {
        if (!settings.cpuAutomation) settings.cpuAutomation = { sampleIntervalSeconds: 1 };
        const n = Number(settings.cpuAutomation.sampleIntervalSeconds);
        settings.cpuAutomation.sampleIntervalSeconds = Number.isFinite(n)
            ? Math.max(1, Math.min(60, Math.round(n)))
            : 1;
        return settings.cpuAutomation;
    }

    function ensurePowerStyles() {
        // Styles are loaded once from css/power-features.css.
    }

    function optionHtml(id, titleKey, subKey, icon, on) {
        return '<div class="heavy-app-option" id="pref-' + id + '"' + (id.endsWith('-battery') ? ' data-vm-laptop-only' : '') + '>' +
            '<div class="flex items-center gap-md">' +
            '<div class="w-11 h-11 rounded-xl bg-surface-container-lowest border border-white/5 flex items-center justify-center">' +
            '<span class="material-symbols-outlined text-secondary-container">' + icon + '</span>' +
            '</div><div><p class="text-body-md text-on-surface" id="' + id + '-title"></p>' +
            '<p class="text-label-sm text-on-surface-variant" id="' + id + '-sub"></p></div></div>' +
            '<div class="mini-toggle cursor-pointer" data-on="' + (on ? 'true' : 'false') + '" id="toggle-' + id + '">' +
            '<div class="mini-toggle-knob"></div></div></div>';
    }

    function checkBatteryPresence() {
        window.VoltUiReorg?.syncLaptopOnly?.();
    }

    function refreshPowerLabels() {
        const map = {
            'app-profile-sub': 'appProfileSub',
            'app-profile-main-title': 'appProfileToggle',
            'app-profile-main-sub': 'appProfileToggleSub',
            'app-profile-add-label': 'appProfileAdd',
            'app-profile-detected-label': 'appProfileDetected',
            'heavy-app-title': 'heavyTitle',
            'heavy-app-sub': 'heavySub',
            'heavy-main-title': 'heavyToggle',
            'heavy-main-sub': 'heavyToggleSub',
            'heavy-app-target-title': 'heavyTarget',
            'heavy-app-target-sub': 'heavyTargetSub',
            'heavy-windows-title': 'heavyWindows',
            'heavy-windows-sub': 'heavyWindowsSub',
            'heavy-gamepaths-title': 'heavyGamePaths',
            'heavy-gamepaths-sub': 'heavyGamePathsSub',
            'heavy-resources-title': 'heavyResources',
            'heavy-resources-sub': 'heavyResourcesSub',
            'heavy-app-refresh-label': 'refresh',
            'heavy-app-detected-label': 'detected',
            'heavy-plan-powerSaver': 'plan_powerSaver',
            'heavy-plan-balanced': 'plan_balanced',
            'heavy-plan-performance': 'plan_performance',
            'keep-awake-title': 'keepTitle',
            'keep-awake-sub': 'keepSub',
            'keep-awake-toggle-title': 'keepToggle',
            'keep-awake-toggle-sub': 'keepToggleSub',
            'keep-awake-battery-title': 'keepBatteryGuard',
            'keep-awake-battery-sub': 'keepBatteryGuardSub',
            'keep-awake-max-title': 'keepMaxDuration',
            'keep-awake-max-sub': 'keepMaxDurationSub',
            'keep-awake-max-unit': 'keepMaxMinutesUnit',
            'keep-awake-note': 'keepNote',
            'thermal-sub': 'thermalSub',
            'thermal-main-title': 'thermalToggle',
            'thermal-main-sub': 'thermalToggleSub',
            'thermal-gpu-title': 'thermalWatchGpu',
            'thermal-gpu-sub': 'thermalWatchGpuSub',
            'thermal-thr-title': 'thermalThreshold',
            'thermal-cool-title': 'thermalCool',
            'thermal-hold-title': 'thermalHold',
            'thermal-hold-unit': 'thermalHoldUnit',
            'thermal-target-title': 'thermalTarget',
            'thermal-plan-powerSaver': 'plan_powerSaver',
            'thermal-plan-balanced': 'plan_balanced',
            'thermal-plan-performance': 'plan_performance',
            'idle-sub': 'idleSub',
            'idle-main-title': 'idleToggle',
            'idle-main-sub': 'idleToggleSub',
            'idle-battery-title': 'idleBatteryOnly',
            'idle-battery-sub': 'idleBatteryOnlySub',
            'idle-min-title': 'idleMinutes',
            'idle-min-unit': 'idleMinutesUnit',
            'idle-target-title': 'idleTarget',
            'idle-plan-powerSaver': 'plan_powerSaver',
            'idle-plan-balanced': 'plan_balanced',
            'idle-plan-performance': 'plan_performance',
            'heavy-rules-always-title': 'heavyAlwaysTitle',
            'heavy-rules-always-sub': 'heavyAlwaysSub',
            'heavy-rules-always-add': 'heavyRulesAdd',
            'heavy-rules-never-title': 'heavyNeverTitle',
            'heavy-rules-never-sub': 'heavyNeverSub',
            'heavy-rules-never-add': 'heavyRulesAdd',
            'heavy-rules-priority-title': 'heavyPriorityTitle',
            'heavy-rules-priority-sub': 'heavyPrioritySub',
            'heavy-rules-priority-add': 'heavyRulesAdd'
        };

        Object.entries(map).forEach(([id, key]) => {
            const el = document.getElementById(id);
            if (el) el.textContent = tt(key);
        });
        renderAppPowerProfiles();
        renderAppPowerProfileStatus(appProfileStatus);
        renderHeavyAppStatus(heavyAppStatus);
        renderHeavyAppRules();
        renderKeepAwakeState(keepAwakeState);
        renderThermalState(thermalState);
        renderIdleState(idleState);
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
        const cpuAutomation = normalizeCpuAutomation();
        const sampleInput = document.getElementById('cpu-sample-interval');
        if (sampleInput) sampleInput.value = cpuAutomation.sampleIntervalSeconds;
        powerFeatureModules.forEach(module => module.load?.(settings));
    }

    function saveSettingsNow() {
        clearTimeout(saveTimer);
        if (window.I18n && I18n.getLang && settings) settings.language = I18n.getLang();
        return Host.call('saveSettings', settings)
            .then(() => Promise.all(powerFeatureModules
                .map(module => module.refreshAfterSave?.())
                .filter(Boolean)));
    }

    function scheduleSave() {
        clearTimeout(saveTimer);
        saveTimer = setTimeout(() => {
            saveSettingsNow().catch(err => console.error('saveSettings failed', err));
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

        const sampleInput = document.getElementById('cpu-sample-interval');
        if (sampleInput) {
            sampleInput.addEventListener('change', (e) => {
                const cfg = normalizeCpuAutomation();
                cfg.sampleIntervalSeconds = Math.round(clamp(e.target.value, 1, 60, cfg.sampleIntervalSeconds));
                e.target.value = cfg.sampleIntervalSeconds;
                scheduleSave();
            });
        }
    }

    const powerFeatureCore = {
        planIds,
        lifecycle: window.VoltViewLifecycle,
        tt,
        esc,
        setToggle,
        ensurePowerStyles,
        optionHtml,
        refreshPowerLabels,
        checkBatteryPresence,
        scheduleSave,
        saveNow: saveSettingsNow,
        clamp,
        appNameFromPath,
    };
    const powerFeatureModules = Object.values(window.VoltPowerFeatureFactories || {})
        .map(factory => factory(powerFeatureCore));

    Host.call('getSettings').then(res => {
        settings = res.settings;
        if (window.I18n && I18n.initFromSettings) I18n.initFromSettings(res);
        if (window.I18n && I18n.getLang && settings) settings.language = I18n.getLang();
        window.__voltThemeCatalog = res.themeCatalog || {};
        window.__voltThemeState = res.theme || null;
        if (window.VoltTheme && VoltTheme.apply) {
            settings.themeColor = VoltTheme.apply(
                settings.themeColor || (res.theme && res.theme.themeColor),
                res.theme && res.theme.palette);
        }
        if (window.VoltFont && VoltFont.apply && settings) {
            settings.font = VoltFont.apply(settings.font);
        }
        loadIntoUi();
        wireUi();
        window.__voltSettings = {
            get: () => settings,
            save: scheduleSave,
            saveNow: () => saveSettingsNow().catch(err => {
                console.error('saveSettings failed', err);
                throw err;
            }),
            startWithWindows: res.startWithWindows,
        };
        document.dispatchEvent(new CustomEvent('settingsloaded'));
    }).catch(err => console.error('getSettings failed', err));

    document.addEventListener('langchanged', () => {
        refreshPowerLabels();
        powerFeatureModules.forEach(module => module.languageChanged?.());
    });

    // Accordion: collapse/expand the power feature groups.
    ensurePowerStyles();
    document.addEventListener('click', (e) => {
        const header = e.target.closest('#view-power .vm-acc-header');
        if (!header) return;
        const item = header.closest('.vm-acc-item');
        if (item) item.dataset.open = item.dataset.open === 'true' ? 'false' : 'true';
    });

})();
