/**
 * Welcome / onboarding overlay.
 * Multi-step carousel shown only on first launch (settings.welcomeCompleted !== true).
 * Lets the user pick theme, language and master automation up-front.
 * Re-openable from Settings via window.__welcome.open().
 */
(function () {
    if (!window.Host || !Host.available) return;

    const STEP_COUNT = 4; // 0 intro, 1 theme, 2 features, 3 preferences
    let step = 0;
    let wired = false;

    const overlay = document.getElementById('welcome-overlay');
    if (!overlay) return;

    const steps = Array.from(overlay.querySelectorAll('.welcome-step'));
    const dots = Array.from(overlay.querySelectorAll('.welcome-dot'));
    const btnBack = document.getElementById('welcome-btn-back');
    const btnNext = document.getElementById('welcome-btn-next');
    const btnStart = document.getElementById('welcome-btn-start');
    const btnSkip = document.getElementById('welcome-btn-skip');

    function getSettings() {
        const s = window.__voltSettings;
        if (!s) return null;
        return s.get ? s.get() : s;
    }

    function save() {
        const s = window.__voltSettings;
        if (s && s.save) s.save();
    }

    function saveNow() {
        const s = window.__voltSettings;
        if (s && s.saveNow) return s.saveNow();
        if (s && s.save) {
            s.save();
            return Promise.resolve();
        }
        return Promise.resolve();
    }

    function isOpen() {
        return !overlay.classList.contains('hidden');
    }

    function isLast(i) { return i === STEP_COUNT - 1; }

    function render() {
        steps.forEach((el) => {
            const n = Number(el.dataset.step);
            const show = n === step;
            el.classList.toggle('hidden', !show);
            el.classList.toggle('flex', show);
        });
        dots.forEach((d, i) => d.dataset.active = (i === step) ? 'true' : 'false');

        if (btnBack) btnBack.classList.toggle('hidden', step === 0);
        if (btnNext) btnNext.classList.toggle('hidden', isLast(step));
        if (btnStart) {
            btnStart.classList.toggle('hidden', !isLast(step));
            btnStart.classList.toggle('flex', isLast(step));
        }
    }

    function goto(i) {
        step = Math.max(0, Math.min(STEP_COUNT - 1, i));
        render();
    }

    function next() { goto(step + 1); }
    function prev() { goto(step - 1); }

    function open() {
        const wasOpen = isOpen();
        step = 0;
        syncControlsFromSettings();
        render();
        overlay.classList.remove('hidden');
        overlay.classList.add('flex');
        if (!wasOpen) document.dispatchEvent(new CustomEvent('welcomeopened'));
    }

    function close() {
        const wasOpen = isOpen();
        overlay.classList.add('hidden');
        overlay.classList.remove('flex');
        if (wasOpen) document.dispatchEvent(new CustomEvent('welcomeclosed'));
    }

    // Mark welcome as completed and dismiss.
    function complete() {
        const settings = getSettings();
        if (settings) {
            settings.welcomeCompleted = true;
            saveNow().catch(err => console.error('saveSettings failed', err));
        }
        close();
        // Hand off to the guided feature tour (tour.js decides whether to run,
        // gated on settings.tourCompleted). Deferred so the overlay finishes
        // hiding before the spotlight measures element positions.
        setTimeout(() => document.dispatchEvent(new CustomEvent('welcomecompleted')), 60);
    }

    // ---- Control wiring (reuses existing mechanisms) ----

    function normThemeColor(val) {
        return window.VoltTheme && VoltTheme.normalize ? VoltTheme.normalize(val) : 'blue';
    }

    function updateThemeCards(themeColor) {
        themeColor = normThemeColor(themeColor);
        overlay.querySelectorAll('.welcome-theme-card').forEach(card => {
            const selected = card.dataset.welcomeTheme === themeColor;
            card.dataset.selected = selected ? 'true' : 'false';
            card.setAttribute('aria-pressed', selected ? 'true' : 'false');
        });
    }

    function applyTheme(val) {
        const themeColor = normThemeColor(val);
        if (window.VoltTheme && VoltTheme.apply) VoltTheme.apply(themeColor);
        updateThemeCards(themeColor);
        const settingsSelect = document.getElementById('theme-select');
        if (settingsSelect) settingsSelect.value = themeColor;
    }

    function setTheme(val) {
        const themeColor = normThemeColor(val);
        applyTheme(themeColor);
        const settings = getSettings();
        if (settings) {
            settings.themeColor = themeColor;
            Host.call('setThemeColor', { themeColor })
                .then(data => {
                    if (data && data.themeColor && data.palette && window.VoltTheme) {
                        VoltTheme.apply(data.themeColor, data.palette);
                    }
                })
                .catch(error => console.error('setThemeColor failed', error));
        }
    }

    function normalizeAutoUpdates(settings) {
        if (!settings.autoUpdates) {
            settings.autoUpdates = { enabled: true, silentInstallEnabled: true, updateChannel: 'stable', intervalMinutes: 30, snoozedUntilUtc: null, skippedVersion: null };
        }
        if (typeof settings.autoUpdates.silentInstallEnabled !== 'boolean') {
            settings.autoUpdates.silentInstallEnabled = true;
        }
        return settings.autoUpdates;
    }

    function syncControlsFromSettings() {
        const settings = getSettings();

        // Theme color
        updateThemeCards(settings ? normThemeColor(settings.themeColor) : 'blue');

        // Language
        const langSelect = document.getElementById('welcome-lang-select');
        if (langSelect && window.I18n && I18n.getLang) langSelect.value = I18n.getLang();

        // Auto-switch
        const master = document.getElementById('welcome-master-toggle');
        if (master && settings) master.checked = settings.masterAutomationEnabled !== false;

        // Silent auto updates
        const silentUpdates = document.getElementById('welcome-silent-update-toggle');
        if (silentUpdates && settings) silentUpdates.checked = normalizeAutoUpdates(settings).silentInstallEnabled !== false;

        // Desktop widgets
        const widgetsToggle = document.getElementById('welcome-widgets-toggle');
        if (widgetsToggle && settings) {
            const ws = settings.widgets;
            widgetsToggle.checked = !!(ws && ws.enabled);
        }
    }

    function wireControls() {
        if (wired) return;
        wired = true;

        // Navigation
        btnNext?.addEventListener('click', next);
        btnBack?.addEventListener('click', prev);
        btnStart?.addEventListener('click', complete);
        btnSkip?.addEventListener('click', complete);
        dots.forEach((d) => {
            d.addEventListener('click', () => goto(Number(d.dataset.go)));
        });

        // Theme
        overlay.querySelectorAll('.welcome-theme-card').forEach(card => {
            card.addEventListener('click', () => setTheme(card.dataset.welcomeTheme));
        });

        // Language
        const langSelect = document.getElementById('welcome-lang-select');
        langSelect?.addEventListener('change', (e) => {
            if (window.I18n && I18n.setLang) I18n.setLang(e.target.value);
        });

        // Auto-switch
        const master = document.getElementById('welcome-master-toggle');
        master?.addEventListener('change', (e) => {
            const settings = getSettings();
            if (settings) {
                settings.masterAutomationEnabled = e.target.checked;
                save();
            }
            // Keep the Power view master toggle in sync if mounted.
            const powerToggle = document.getElementById('master-toggle');
            if (powerToggle) powerToggle.checked = e.target.checked;
        });

        // Silent auto updates
        const silentUpdates = document.getElementById('welcome-silent-update-toggle');
        silentUpdates?.addEventListener('change', (e) => {
            const settings = getSettings();
            if (settings) {
                normalizeAutoUpdates(settings).silentInstallEnabled = e.target.checked;
                save();
            }
            const settingsToggle = document.getElementById('toggle-silent-auto-updates');
            if (settingsToggle) settingsToggle.dataset.on = e.target.checked ? 'true' : 'false';
        });

        // Desktop widgets
        const widgetsToggle = document.getElementById('welcome-widgets-toggle');
        widgetsToggle?.addEventListener('change', async (e) => {
            const enabled = e.target.checked;
            try {
                if (window.Host && Host.call) {
                    await Host.call('setWidgetsMaster', { enabled });
                }
            } catch { /* best-effort */ }
            // Keep the Settings page mini-toggle in sync if mounted.
            const settingsToggle = document.getElementById('toggle-widgets-master');
            if (settingsToggle) settingsToggle.dataset.on = enabled ? 'true' : 'false';
        });
    }

    // ---- Bootstrap ----

    document.addEventListener('settingsloaded', () => {
        wireControls();
        const settings = getSettings();
        if (settings && settings.welcomeCompleted !== true) open();
    });

    // Settings → "Show welcome" button (preview only, does not flip welcomeCompleted).
    document.getElementById('btn-show-welcome')?.addEventListener('click', () => open());

    // Re-apply i18n to the welcome overlay when the language changes (controls persist).
    document.addEventListener('langchanged', () => {
        if (window.I18n && I18n.apply) I18n.apply();
    });

    window.__welcome = { open, close, isOpen };
})();
