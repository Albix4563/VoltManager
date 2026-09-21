/** Thermal and idle power protections module. */
(function () {
    const factories = window.VoltPowerFeatureFactories = window.VoltPowerFeatureFactories || {};
    factories.protections = function (core) {
        let settings = null;
        let thermalWired = false;
        let thermalState = null;
        let idleWired = false;
        let idleState = null;
        let initialized = false;
        let statusLoaded = false;
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

    function normalizeThermalGuard() {
        if (!settings.thermalGuard) {
            settings.thermalGuard = {
                enabled: false,
                thresholdCelsius: 90,
                coolThresholdCelsius: 82,
                holdSeconds: 20,
                targetPlan: 'powerSaver',
                watchGpu: true,
            };
        }
        const t = settings.thermalGuard;
        t.enabled = !!t.enabled;
        t.watchGpu = t.watchGpu !== false;
        let thr = Number(t.thresholdCelsius);
        if (!Number.isFinite(thr)) thr = 90;
        t.thresholdCelsius = Math.max(60, Math.min(105, thr));
        let cool = Number(t.coolThresholdCelsius);
        if (!Number.isFinite(cool)) cool = t.thresholdCelsius - 8;
        t.coolThresholdCelsius = Math.max(45, Math.min(t.thresholdCelsius - 1, cool));
        let hold = Number(t.holdSeconds);
        if (!Number.isFinite(hold)) hold = 20;
        t.holdSeconds = Math.max(5, Math.min(300, Math.round(hold)));
        if (!planIds.includes(t.targetPlan)) t.targetPlan = 'powerSaver';
        return t;
    }

    async function pushThermalSettings() {
        const cfg = normalizeThermalGuard();
        if (!Host.available) {
            scheduleSave();
            return;
        }
        try {
            thermalState = await Host.call('setThermalGuardSettings', cfg);
            renderThermalState(thermalState);
            // Keep settings.json in sync for backup/export without re-applying host state.
            await saveSettingsNow().catch(() => {});
        } catch (err) {
            console.error('setThermalGuardSettings failed', err);
            scheduleSave();
        }
    }

    function normalizeIdlePowerGuard() {
        if (!settings.idlePowerGuard) {
            settings.idlePowerGuard = {
                enabled: false,
                idleMinutes: 10,
                targetPlan: 'powerSaver',
                onlyOnBattery: true,
            };
        }
        const t = settings.idlePowerGuard;
        t.enabled = !!t.enabled;
        t.onlyOnBattery = t.onlyOnBattery !== false;
        let m = Number(t.idleMinutes);
        if (!Number.isFinite(m)) m = 10;
        t.idleMinutes = Math.max(1, Math.min(120, Math.round(m)));
        if (!planIds.includes(t.targetPlan)) t.targetPlan = 'powerSaver';
        return t;
    }

    async function pushIdleSettings() {
        const cfg = normalizeIdlePowerGuard();
        if (!Host.available) {
            scheduleSave();
            return;
        }
        try {
            idleState = await Host.call('setIdlePowerGuardSettings', cfg);
            renderIdleState(idleState);
            await saveSettingsNow().catch(() => {});
        } catch (err) {
            console.error('setIdlePowerGuardSettings failed', err);
            scheduleSave();
        }
    }

    function mountThermalGuardUi() {
        const mount = document.getElementById('vm-automation-protections') || document.getElementById('thermal-guard-mount');
        const existing = document.getElementById('thermal-guard-panel');
        if (existing) {
            if (mount && existing.parentElement !== mount) mount.appendChild(existing);
            return;
        }
        ensurePowerStyles();
        if (!mount) return;

        mount.innerHTML =
            '<div class="keep-awake-panel-inner" id="thermal-guard-panel">' +
            '<div class="flex flex-col sm:flex-row sm:items-start justify-between gap-md mb-lg relative z-10">' +
            '<p class="text-body-md text-on-surface-variant max-w-2xl" id="thermal-sub"></p>' +
            '<span class="keep-awake-badge" id="thermal-badge" data-active="false">' +
            '<span class="material-symbols-outlined text-[16px]">device_thermostat</span>' +
            '<span id="thermal-badge-label"></span></span></div>' +
            '<div class="keep-awake-grid"><div class="space-y-sm">' +
            optionHtml('thermal-main', 'thermalToggle', 'thermalToggleSub', 'device_thermostat', false) +
            optionHtml('thermal-gpu', 'thermalWatchGpu', 'thermalWatchGpuSub', 'memory', true) +
            '<div class="heavy-app-option"><div class="flex items-center gap-md min-w-0">' +
            '<div class="w-11 h-11 rounded-xl bg-surface-container-lowest border border-white/5 flex items-center justify-center shrink-0">' +
            '<span class="material-symbols-outlined text-secondary-container">thermostat</span></div>' +
            '<div class="min-w-0"><p class="text-body-md text-on-surface" id="thermal-thr-title"></p>' +
            '<p class="text-label-sm text-on-surface-variant" id="thermal-peak-line"></p></div></div>' +
            '<div class="flex items-center gap-xs shrink-0">' +
            '<input type="number" min="60" max="105" step="1" id="thermal-threshold-input" ' +
            'class="w-20 bg-surface-container-low/50 text-secondary-container font-medium border border-white/10 rounded-lg py-2 px-2 text-body-md text-center focus:outline-none focus:border-secondary-container" />' +
            '<span class="text-label-sm text-on-surface-variant">°C</span></div></div>' +
            '<div class="heavy-app-option"><div class="flex items-center gap-md min-w-0">' +
            '<div class="w-11 h-11 rounded-xl bg-surface-container-lowest border border-white/5 flex items-center justify-center shrink-0">' +
            '<span class="material-symbols-outlined text-secondary-container">ac_unit</span></div>' +
            '<div class="min-w-0"><p class="text-body-md text-on-surface" id="thermal-cool-title"></p></div></div>' +
            '<div class="flex items-center gap-xs shrink-0">' +
            '<input type="number" min="45" max="104" step="1" id="thermal-cool-input" ' +
            'class="w-20 bg-surface-container-low/50 text-secondary-container font-medium border border-white/10 rounded-lg py-2 px-2 text-body-md text-center focus:outline-none focus:border-secondary-container" />' +
            '<span class="text-label-sm text-on-surface-variant">°C</span></div></div>' +
            '<div class="heavy-app-option"><div class="flex items-center gap-md min-w-0">' +
            '<div class="w-11 h-11 rounded-xl bg-surface-container-lowest border border-white/5 flex items-center justify-center shrink-0">' +
            '<span class="material-symbols-outlined text-secondary-container">timer</span></div>' +
            '<div class="min-w-0"><p class="text-body-md text-on-surface" id="thermal-hold-title"></p></div></div>' +
            '<div class="flex items-center gap-xs shrink-0">' +
            '<input type="number" min="5" max="300" step="5" id="thermal-hold-input" ' +
            'class="w-20 bg-surface-container-low/50 text-secondary-container font-medium border border-white/10 rounded-lg py-2 px-2 text-body-md text-center focus:outline-none focus:border-secondary-container" />' +
            '<span class="text-label-sm text-on-surface-variant" id="thermal-hold-unit"></span></div></div>' +
            '<div class="heavy-app-option"><div class="flex items-center gap-md min-w-0">' +
            '<div class="w-11 h-11 rounded-xl bg-surface-container-lowest border border-white/5 flex items-center justify-center shrink-0">' +
            '<span class="material-symbols-outlined text-secondary-container">bolt</span></div>' +
            '<div class="min-w-0"><p class="text-body-md text-on-surface" id="thermal-target-title"></p></div></div>' +
            '<select id="thermal-target-plan" class="bg-surface-container-low/50 text-secondary-container font-medium border border-white/10 rounded-lg py-2 px-3 text-body-md focus:outline-none focus:border-secondary-container">' +
            '<option value="powerSaver" id="thermal-plan-powerSaver"></option>' +
            '<option value="balanced" id="thermal-plan-balanced"></option>' +
            '<option value="performance" id="thermal-plan-performance"></option></select></div>' +
            '</div><aside class="keep-awake-status">' +
            '<p class="text-body-md text-on-surface" id="thermal-status"></p>' +
            '</aside></div></div>';
        refreshPowerLabels();
    }

    function mountIdlePowerGuardUi() {
        const mount = document.getElementById('vm-automation-protections') || document.getElementById('idle-power-guard-mount');
        const existing = document.getElementById('idle-power-guard-panel');
        if (existing) {
            if (mount && existing.parentElement !== mount) mount.appendChild(existing);
            return;
        }
        ensurePowerStyles();
        if (!mount) return;

        mount.innerHTML =
            '<div class="keep-awake-panel-inner" id="idle-power-guard-panel">' +
            '<div class="flex flex-col sm:flex-row sm:items-start justify-between gap-md mb-lg relative z-10">' +
            '<p class="text-body-md text-on-surface-variant max-w-2xl" id="idle-sub"></p>' +
            '<span class="keep-awake-badge" id="idle-badge" data-active="false">' +
            '<span class="material-symbols-outlined text-[16px]">hourglass_empty</span>' +
            '<span id="idle-badge-label"></span></span></div>' +
            '<div class="keep-awake-grid"><div class="space-y-sm">' +
            optionHtml('idle-main', 'idleToggle', 'idleToggleSub', 'hourglass_empty', false) +
            optionHtml('idle-battery', 'idleBatteryOnly', 'idleBatteryOnlySub', 'battery_android', true) +
            '<div class="heavy-app-option"><div class="flex items-center gap-md min-w-0">' +
            '<div class="w-11 h-11 rounded-xl bg-surface-container-lowest border border-white/5 flex items-center justify-center shrink-0">' +
            '<span class="material-symbols-outlined text-secondary-container">timer</span></div>' +
            '<div class="min-w-0"><p class="text-body-md text-on-surface" id="idle-min-title"></p></div></div>' +
            '<div class="flex items-center gap-xs shrink-0">' +
            '<input type="number" min="1" max="120" step="1" id="idle-minutes-input" ' +
            'class="w-20 bg-surface-container-low/50 text-secondary-container font-medium border border-white/10 rounded-lg py-2 px-2 text-body-md text-center focus:outline-none focus:border-secondary-container" />' +
            '<span class="text-label-sm text-on-surface-variant" id="idle-min-unit"></span></div></div>' +
            '<div class="heavy-app-option"><div class="flex items-center gap-md min-w-0">' +
            '<div class="w-11 h-11 rounded-xl bg-surface-container-lowest border border-white/5 flex items-center justify-center shrink-0">' +
            '<span class="material-symbols-outlined text-secondary-container">bolt</span></div>' +
            '<div class="min-w-0"><p class="text-body-md text-on-surface" id="idle-target-title"></p></div></div>' +
            '<select id="idle-target-plan" class="bg-surface-container-low/50 text-secondary-container font-medium border border-white/10 rounded-lg py-2 px-3 text-body-md focus:outline-none focus:border-secondary-container">' +
            '<option value="powerSaver" id="idle-plan-powerSaver"></option>' +
            '<option value="balanced" id="idle-plan-balanced"></option>' +
            '<option value="performance" id="idle-plan-performance"></option></select></div>' +
            '</div><aside class="keep-awake-status">' +
            '<p class="text-body-md text-on-surface" id="idle-status"></p>' +
            '</aside></div></div>';
        refreshPowerLabels();
        checkBatteryPresence();
    }

    function syncThermalUi() {
        const cfg = normalizeThermalGuard();
        setToggle(document.getElementById('toggle-thermal-main'), cfg.enabled);
        setToggle(document.getElementById('toggle-thermal-gpu'), cfg.watchGpu !== false);
        const thr = document.getElementById('thermal-threshold-input');
        const cool = document.getElementById('thermal-cool-input');
        const hold = document.getElementById('thermal-hold-input');
        const plan = document.getElementById('thermal-target-plan');
        if (thr && document.activeElement !== thr) thr.value = String(Math.round(cfg.thresholdCelsius));
        if (cool && document.activeElement !== cool) cool.value = String(Math.round(cfg.coolThresholdCelsius));
        if (hold && document.activeElement !== hold) hold.value = String(cfg.holdSeconds | 0);
        if (plan) plan.value = cfg.targetPlan;
        renderThermalState(thermalState);
    }

    function renderThermalState(state) {
        const cfg = settings ? normalizeThermalGuard() : { enabled: false };
        const badge = document.getElementById('thermal-badge');
        const badgeLabel = document.getElementById('thermal-badge-label');
        const status = document.getElementById('thermal-status');
        const peakLine = document.getElementById('thermal-peak-line');
        const enabled = !!(state ? state.enabled : cfg.enabled);
        const active = !!(state && state.active);

        setToggle(document.getElementById('toggle-thermal-main'), enabled);
        if (badge) badge.dataset.active = active ? 'true' : 'false';
        if (badgeLabel) {
            badgeLabel.textContent = !enabled ? tt('thermalBadgeOff')
                : (active ? tt('thermalBadgeActive') : tt('thermalBadgeIdle'));
        }
        if (status) {
            if (!enabled) status.textContent = tt('thermalBadgeOff');
            else if (state && state.message === 'no_sensors') status.textContent = tt('thermalStatusNoSensors');
            else if (active) status.textContent = tt('thermalStatusActive');
            else if (state && state.message === 'warming') {
                status.textContent = tt('thermalStatusWarming')
                    .replace('{held}', String(Math.round(state.hotHoldSeconds || 0)))
                    .replace('{need}', String(state.holdSeconds || cfg.holdSeconds || 20));
            } else status.textContent = tt('thermalStatusIdle');
        }
        if (peakLine) {
            const peak = state && state.peakTemp != null ? Number(state.peakTemp).toFixed(0) + ' °C' : '--';
            peakLine.textContent = tt('thermalPeak') + ': ' + peak;
        }
    }

    function syncIdleUi() {
        const cfg = normalizeIdlePowerGuard();
        setToggle(document.getElementById('toggle-idle-main'), cfg.enabled);
        setToggle(document.getElementById('toggle-idle-battery'), cfg.onlyOnBattery !== false);
        const minIn = document.getElementById('idle-minutes-input');
        const plan = document.getElementById('idle-target-plan');
        if (minIn && document.activeElement !== minIn) minIn.value = String(cfg.idleMinutes | 0);
        if (plan) plan.value = cfg.targetPlan;
        renderIdleState(idleState);
    }

    function renderIdleState(state) {
        const cfg = settings ? normalizeIdlePowerGuard() : { enabled: false, idleMinutes: 10 };
        const badge = document.getElementById('idle-badge');
        const badgeLabel = document.getElementById('idle-badge-label');
        const status = document.getElementById('idle-status');
        const enabled = !!(state ? state.enabled : cfg.enabled);
        const active = !!(state && state.active);

        setToggle(document.getElementById('toggle-idle-main'), enabled);
        if (badge) badge.dataset.active = active ? 'true' : 'false';
        if (badgeLabel) {
            badgeLabel.textContent = !enabled ? tt('idleBadgeOff')
                : (active ? tt('idleBadgeActive') : tt('idleBadgeIdle'));
        }
        if (status) {
            if (!enabled) status.textContent = tt('idleBadgeOff');
            else if (state && state.message === 'no_input') status.textContent = tt('idleStatusNoInput');
            else if (active) status.textContent = tt('idleStatusActive');
            else if (state && (state.message === 'battery_skip')) status.textContent = tt('idleStatusSkip');
            else if (state && state.message === 'waiting') {
                const idleMin = ((state.idleSeconds || 0) / 60).toFixed(1);
                const need = state.idleMinutes || cfg.idleMinutes || 10;
                status.textContent = tt('idleStatusWaiting')
                    .replace('{idle}', idleMin)
                    .replace('{need}', String(need));
            } else status.textContent = tt('idleBadgeIdle');
        }
    }

    function wireThermalGuardUi() {
        if (thermalWired) return;

        listen(document, 'click', async (e) => {
            if (!settings) return;
            if (e.target.closest('#pref-thermal-main')) {
                const cfg = normalizeThermalGuard();
                cfg.enabled = !cfg.enabled;
                setToggle(document.getElementById('toggle-thermal-main'), cfg.enabled);
                if (Host.available) {
                    try {
                        thermalState = await Host.call('setThermalGuardEnabled', { enabled: cfg.enabled });
                        if (thermalState && typeof thermalState.enabled === 'boolean')
                            cfg.enabled = thermalState.enabled;
                        renderThermalState(thermalState);
                    } catch (err) {
                        cfg.enabled = !cfg.enabled;
                        setToggle(document.getElementById('toggle-thermal-main'), cfg.enabled);
                        const status = document.getElementById('thermal-status');
                        Host.fail(err, (msg) => {
                            if (status) status.textContent = msg;
                        });
                    }
                } else scheduleSave();
                return;
            }
            if (e.target.closest('#pref-thermal-gpu')) {
                const cfg = normalizeThermalGuard();
                cfg.watchGpu = !cfg.watchGpu;
                setToggle(document.getElementById('toggle-thermal-gpu'), cfg.watchGpu);
                await pushThermalSettings();
            }
        });

        listen(document, 'change', (e) => {
            if (!settings) return;
            const id = e.target && e.target.id;
            if (!id || !id.startsWith('thermal-')) return;
            const cfg = normalizeThermalGuard();
            if (id === 'thermal-threshold-input') {
                cfg.thresholdCelsius = clamp(e.target.value, 60, 105, 90);
                e.target.value = String(cfg.thresholdCelsius);
                if (cfg.coolThresholdCelsius >= cfg.thresholdCelsius)
                    cfg.coolThresholdCelsius = cfg.thresholdCelsius - 8;
            } else if (id === 'thermal-cool-input') {
                cfg.coolThresholdCelsius = clamp(e.target.value, 45, cfg.thresholdCelsius - 1, cfg.thresholdCelsius - 8);
                e.target.value = String(cfg.coolThresholdCelsius);
            } else if (id === 'thermal-hold-input') {
                cfg.holdSeconds = Math.round(clamp(e.target.value, 5, 300, 20));
                e.target.value = String(cfg.holdSeconds);
            } else if (id === 'thermal-target-plan') {
                cfg.targetPlan = planIds.includes(e.target.value) ? e.target.value : 'powerSaver';
            } else return;
            pushThermalSettings();
        });

        onHost('thermalGuardChanged', (state) => {
            thermalState = state;
            if (settings && state) {
                const cfg = normalizeThermalGuard();
                if (typeof state.enabled === 'boolean') cfg.enabled = state.enabled;
                if (typeof state.thresholdCelsius === 'number') cfg.thresholdCelsius = state.thresholdCelsius;
                if (typeof state.coolThresholdCelsius === 'number') cfg.coolThresholdCelsius = state.coolThresholdCelsius;
                if (typeof state.holdSeconds === 'number') cfg.holdSeconds = state.holdSeconds;
                if (state.targetPlan) cfg.targetPlan = state.targetPlan;
                if (typeof state.watchGpu === 'boolean') cfg.watchGpu = state.watchGpu;
            }
            renderThermalState(state);
            syncThermalUi();
        });
        thermalWired = true;
    }

    function wireIdlePowerGuardUi() {
        if (idleWired) return;

        listen(document, 'click', async (e) => {
            if (!settings) return;
            if (e.target.closest('#pref-idle-main')) {
                const cfg = normalizeIdlePowerGuard();
                cfg.enabled = !cfg.enabled;
                setToggle(document.getElementById('toggle-idle-main'), cfg.enabled);
                if (Host.available) {
                    try {
                        idleState = await Host.call('setIdlePowerGuardEnabled', { enabled: cfg.enabled });
                        if (idleState && typeof idleState.enabled === 'boolean')
                            cfg.enabled = idleState.enabled;
                        renderIdleState(idleState);
                    } catch (err) {
                        cfg.enabled = !cfg.enabled;
                        setToggle(document.getElementById('toggle-idle-main'), cfg.enabled);
                        const status = document.getElementById('idle-status');
                        Host.fail(err, (msg) => {
                            if (status) status.textContent = msg;
                        });
                    }
                } else scheduleSave();
                return;
            }
            if (e.target.closest('#pref-idle-battery')) {
                const cfg = normalizeIdlePowerGuard();
                cfg.onlyOnBattery = !cfg.onlyOnBattery;
                setToggle(document.getElementById('toggle-idle-battery'), cfg.onlyOnBattery);
                await pushIdleSettings();
            }
        });

        listen(document, 'change', (e) => {
            if (!settings) return;
            const id = e.target && e.target.id;
            if (id === 'idle-minutes-input') {
                const cfg = normalizeIdlePowerGuard();
                cfg.idleMinutes = Math.round(clamp(e.target.value, 1, 120, 10));
                e.target.value = String(cfg.idleMinutes);
                pushIdleSettings();
            } else if (id === 'idle-target-plan') {
                const cfg = normalizeIdlePowerGuard();
                cfg.targetPlan = planIds.includes(e.target.value) ? e.target.value : 'powerSaver';
                pushIdleSettings();
            }
        });

        onHost('idlePowerGuardChanged', (state) => {
            idleState = state;
            if (settings && state) {
                const cfg = normalizeIdlePowerGuard();
                if (typeof state.enabled === 'boolean') cfg.enabled = state.enabled;
                if (typeof state.idleMinutes === 'number') cfg.idleMinutes = state.idleMinutes;
                if (state.targetPlan) cfg.targetPlan = state.targetPlan;
                if (typeof state.onlyOnBattery === 'boolean') cfg.onlyOnBattery = state.onlyOnBattery;
            }
            renderIdleState(state);
            syncIdleUi();
        });
        idleWired = true;
    }


        function refreshStatus() {
            if (!settings || !Host.available) return Promise.resolve();
            return Promise.all([
                Host.call('getThermalGuardState').then(state => {
                    thermalState = state; renderThermalState(state); syncThermalUi();
                }).catch(err => console.error('getThermalGuardState failed', err)),
                Host.call('getIdlePowerGuardState').then(state => {
                    idleState = state; renderIdleState(state); syncIdleUi();
                }).catch(err => console.error('getIdlePowerGuardState failed', err)),
            ]);
        }
        function setup() {
            if (!settings) return;
            mountThermalGuardUi();
            mountIdlePowerGuardUi();
            wireThermalGuardUi();
            wireIdlePowerGuardUi();
            syncThermalUi();
            syncIdleUi();
            if (!statusLoaded) { statusLoaded = true; refreshStatus(); }
        }
        const descriptor = {
            matches(route) {
                return (route.view === 'power' && (route.subviews.power === 'thermal' || route.subviews.power === 'idle')) ||
                    (route.view === 'automations' && route.subviews.automations === 'protections');
            },
            init() { initialized = true; setup(); },
            activate: setup,
            deactivate() {},
            dispose() { disposeListeners(); thermalWired = false; idleWired = false; }
        };
        core.lifecycle.register('power-protections', descriptor);
        return {
            load(nextSettings) { settings = nextSettings; if (initialized) setup(); },
            languageChanged() { if (initialized) { refreshPowerLabels(); renderThermalState(thermalState); renderIdleState(idleState); } },
            descriptor,
        };
    };
})();
