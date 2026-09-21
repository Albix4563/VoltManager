/** Keep-awake module. */
(function () {
    const factories = window.VoltPowerFeatureFactories = window.VoltPowerFeatureFactories || {};
    factories.keepAwake = function (core) {
        let settings = null;
        let keepAwakeWired = false;
        let keepAwakeState = null;
        let initialized = false;
    const disposers = [];
    function listen(target, type, handler, options) {
        target.addEventListener(type, handler, options);
        disposers.push(() => target.removeEventListener(type, handler, options));
    }
    function onHost(name, handler) {
        const unsubscribe = Host.on(name, handler);
        if (typeof unsubscribe === 'function') disposers.push(unsubscribe);
        return unsubscribe;
    }
    function disposeListeners() {
        while (disposers.length) {
            try { disposers.pop()(); } catch (_) {}
        }
    }
    const planIds = core.planIds;
    const tt = core.tt;
    const esc = core.esc;
    const setToggle = core.setToggle;
    const ensurePowerStyles = core.ensurePowerStyles;
    const optionHtml = core.optionHtml;
    const refreshPowerLabels = core.refreshPowerLabels;
    const checkBatteryPresence = core.checkBatteryPresence;
    const scheduleSave = core.scheduleSave;
    const clamp = core.clamp;
    const appNameFromPath = core.appNameFromPath;
    function saveSettingsNow() { return core.saveNow(); }

    function normalizeKeepAwake() {
        if (!settings.keepAwake) settings.keepAwake = { enabled: false, lastChangedUtc: null };
        settings.keepAwake.enabled = !!settings.keepAwake.enabled;
        if (typeof settings.keepAwake.autoDisableOnBattery !== 'boolean')
            settings.keepAwake.autoDisableOnBattery = true;
        let maxM = Number(settings.keepAwake.maxMinutes);
        if (!Number.isFinite(maxM) || maxM < 0) maxM = 0;
        if (maxM > 24 * 60) maxM = 24 * 60;
        settings.keepAwake.maxMinutes = Math.round(maxM);
        return settings.keepAwake;
    }

    function formatKeepRemaining(seconds) {
        const s = Math.max(0, Math.floor(Number(seconds) || 0));
        const h = Math.floor(s / 3600);
        const m = Math.floor((s % 3600) / 60);
        if (h > 0) return h + 'h ' + m + 'm';
        if (m > 0) return m + 'm';
        return s + 's';
    }

    async function pushKeepAwakeSafety() {
        const cfg = normalizeKeepAwake();
        if (!Host.available) {
            await saveSettingsNow().catch(() => {});
            return;
        }
        try {
            const state = await Host.call('setKeepAwakeSafety', {
                autoDisableOnBattery: !!cfg.autoDisableOnBattery,
                maxMinutes: cfg.maxMinutes | 0,
            });
            keepAwakeState = state;
            renderKeepAwakeState(state);
            await saveSettingsNow().catch(() => {});
        } catch (err) {
            console.error('setKeepAwakeSafety failed', err);
            await saveSettingsNow().catch(() => {});
        }
    }

    function mountKeepAwakeUi() {
        const mount = document.getElementById('vm-keep-awake') || document.getElementById('keep-awake-mount');
        const existing = document.getElementById('keep-awake-panel');
        if (existing) {
            if (mount && existing.parentElement !== mount) mount.appendChild(existing);
            return;
        }
        ensurePowerStyles();
        if (!mount) return;

        mount.innerHTML =
            '<div class="keep-awake-panel-inner" id="keep-awake-panel">' +
            '<div class="flex flex-col sm:flex-row sm:items-start justify-between gap-md mb-lg relative z-10">' +
            '<p class="text-body-md text-on-surface-variant max-w-2xl" id="keep-awake-sub"></p>' +
            '<span class="keep-awake-badge" id="keep-awake-badge" data-active="false">' +
            '<span class="material-symbols-outlined text-[16px]">power_settings_new</span>' +
            '<span id="keep-awake-badge-label"></span></span></div>' +
            '<div class="keep-awake-grid"><div class="space-y-sm">' +
            optionHtml('keep-awake-toggle', 'keepToggle', 'keepToggleSub', 'lock_clock', false) +
            optionHtml('keep-awake-battery', 'keepBatteryGuard', 'keepBatteryGuardSub', 'battery_alert', true) +
            '<div class="heavy-app-option" id="pref-keep-awake-max">' +
            '<div class="flex items-center gap-md min-w-0">' +
            '<div class="w-11 h-11 rounded-xl bg-surface-container-lowest border border-white/5 flex items-center justify-center shrink-0">' +
            '<span class="material-symbols-outlined text-secondary-container">timer</span></div>' +
            '<div class="min-w-0"><p class="text-body-md text-on-surface" id="keep-awake-max-title"></p>' +
            '<p class="text-label-sm text-on-surface-variant" id="keep-awake-max-sub"></p></div></div>' +
            '<div class="flex items-center gap-xs shrink-0">' +
            '<input type="number" min="0" max="1440" step="15" id="keep-awake-max-input" ' +
            'class="w-20 bg-surface-container-low/50 text-secondary-container font-medium border border-white/10 rounded-lg py-2 px-2 text-body-md text-center focus:outline-none focus:border-secondary-container" />' +
            '<span class="text-label-sm text-on-surface-variant" id="keep-awake-max-unit"></span></div></div>' +
            '</div><aside class="keep-awake-status">' +
            '<p class="text-body-md text-on-surface" id="keep-awake-status"></p>' +
            '<p class="text-label-sm text-on-surface-variant opacity-80" id="keep-awake-note"></p>' +
            '</aside></div></div>';
        refreshPowerLabels();
        checkBatteryPresence();
    }

    function syncKeepAwakeUi() {
        const cfg = normalizeKeepAwake();
        setToggle(document.getElementById('toggle-keep-awake-toggle'), cfg.enabled);
        setToggle(document.getElementById('toggle-keep-awake-battery'), cfg.autoDisableOnBattery !== false);
        const maxInput = document.getElementById('keep-awake-max-input');
        if (maxInput && document.activeElement !== maxInput)
            maxInput.value = String(cfg.maxMinutes | 0);
        renderKeepAwakeState(keepAwakeState);
    }

    function renderKeepAwakeState(state) {
        const cfg = settings ? normalizeKeepAwake() : { enabled: false, autoDisableOnBattery: true, maxMinutes: 0 };
        const active = !!(state ? state.enabled : cfg.enabled);
        const badge = document.getElementById('keep-awake-badge');
        const badgeLabel = document.getElementById('keep-awake-badge-label');
        const status = document.getElementById('keep-awake-status');

        setToggle(document.getElementById('toggle-keep-awake-toggle'), active);
        if (state && typeof state.autoDisableOnBattery === 'boolean')
            setToggle(document.getElementById('toggle-keep-awake-battery'), state.autoDisableOnBattery);
        else
            setToggle(document.getElementById('toggle-keep-awake-battery'), cfg.autoDisableOnBattery !== false);

        if (badge) badge.dataset.active = active ? 'true' : 'false';
        if (badgeLabel) badgeLabel.textContent = active ? tt('keepBadgeActive') : tt('keepBadgeIdle');
        if (status) {
            let text = active ? tt('keepStatusActive') : tt('keepStatusIdle');
            if (active && state && state.remainingSeconds != null && state.remainingSeconds >= 0 && (state.maxMinutes | 0) > 0) {
                text = tt('keepStatusActiveTimed').replace('{time}', formatKeepRemaining(state.remainingSeconds));
            } else if (!active && state && state.lastAutoDisableReason === 'battery') {
                text = tt('keepStatusBattery');
            } else if (!active && state && state.lastAutoDisableReason === 'timeout') {
                text = tt('keepStatusTimeout');
            } else if (!active && state && state.message === 'auto_off_battery') {
                text = tt('keepStatusBattery');
            } else if (!active && state && state.message === 'auto_off_timeout') {
                text = tt('keepStatusTimeout');
            }
            status.textContent = text;
        }
    }

    function wireKeepAwakeUi() {
        if (keepAwakeWired) return;

        listen(document, 'click', async (e) => {
            if (!settings) return;

            const batteryPref = e.target.closest('#pref-keep-awake-battery');
            if (batteryPref) {
                const cfg = normalizeKeepAwake();
                cfg.autoDisableOnBattery = !cfg.autoDisableOnBattery;
                setToggle(document.getElementById('toggle-keep-awake-battery'), cfg.autoDisableOnBattery);
                await pushKeepAwakeSafety();
                return;
            }

            const pref = e.target.closest('#pref-keep-awake-toggle');
            if (!pref) return;

            const cfg = normalizeKeepAwake();
            const next = !cfg.enabled;
            cfg.enabled = next;
            cfg.lastChangedUtc = new Date().toISOString();
            keepAwakeState = { enabled: next, applied: next };
            syncKeepAwakeUi();
            if (Host.available) {
                try {
                    keepAwakeState = await Host.call('setKeepAwake', { enabled: next });
                    if (settings) {
                        normalizeKeepAwake().enabled = !!(keepAwakeState && keepAwakeState.enabled);
                        if (keepAwakeState && typeof keepAwakeState.autoDisableOnBattery === 'boolean')
                            normalizeKeepAwake().autoDisableOnBattery = keepAwakeState.autoDisableOnBattery;
                    }
                    renderKeepAwakeState(keepAwakeState);
                } catch (err) {
                    cfg.enabled = !next;
                    keepAwakeState = { enabled: !next, applied: !next };
                    const status = document.getElementById('keep-awake-status');
                    Host.fail(err, (msg) => {
                        if (status) status.textContent = msg;
                    });
                    syncKeepAwakeUi();
                }
            } else {
                scheduleSave();
            }
        });

        listen(document, 'change', (e) => {
            if (!settings || e.target.id !== 'keep-awake-max-input') return;
            let v = parseInt(e.target.value, 10);
            if (!Number.isFinite(v) || v < 0) v = 0;
            if (v > 1440) v = 1440;
            normalizeKeepAwake().maxMinutes = v;
            e.target.value = String(v);
            pushKeepAwakeSafety();
        });

        onHost('keepAwakeChanged', (state) => {
            keepAwakeState = state;
            if (settings) {
                const cfg = normalizeKeepAwake();
                cfg.enabled = !!state.enabled;
                if (typeof state.autoDisableOnBattery === 'boolean')
                    cfg.autoDisableOnBattery = state.autoDisableOnBattery;
                if (typeof state.maxMinutes === 'number')
                    cfg.maxMinutes = state.maxMinutes;
            }
            renderKeepAwakeState(state);
            syncKeepAwakeUi();
        });
        keepAwakeWired = true;
    }


        function setup() {
            if (!settings) return;
            mountKeepAwakeUi();
            wireKeepAwakeUi();
            syncKeepAwakeUi();
            if (!keepAwakeState) {
                const cfg = normalizeKeepAwake();
                keepAwakeState = { enabled: cfg.enabled, applied: cfg.enabled };
            }
            renderKeepAwakeState(keepAwakeState);
        }
        const descriptor = {
            matches(route) {
                return (route.view === 'power' && route.subviews.power === 'awake') ||
                    (route.view === 'power-plans' && route.subviews['power-plans'] === 'keep-awake');
            },
            init() { initialized = true; setup(); },
            activate: setup,
            deactivate() {},
            dispose() { disposeListeners(); keepAwakeWired = false; }
        };
        core.lifecycle.register('keep-awake', descriptor);
        return {
            load(nextSettings) { settings = nextSettings; if (initialized) setup(); },
            languageChanged() { if (initialized) { refreshPowerLabels(); renderKeepAwakeState(keepAwakeState); } },
            descriptor,
        };
    };
})();
