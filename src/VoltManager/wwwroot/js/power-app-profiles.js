/** App power profiles module. */
(function () {
    const factories = window.VoltPowerFeatureFactories = window.VoltPowerFeatureFactories || {};
    factories.appProfiles = function (core) {
        let settings = null;
        let appProfileWired = false;
        let appProfileStatus = null;
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

    function normalizeAppPowerProfiles() {
        if (!settings.appPowerProfiles) {
            settings.appPowerProfiles = {
                enabled: true,
                rules: []
            };
        }

        const cfg = settings.appPowerProfiles;
        cfg.enabled = cfg.enabled !== false;
        if (!Array.isArray(cfg.rules)) cfg.rules = [];
        const seen = new Set();
        cfg.rules = cfg.rules.filter(rule => {
            if (!rule || !rule.path) return false;
            rule.path = String(rule.path).trim().replace(/^"+|"+$/g, '');
            const key = rule.path.toLowerCase();
            if (!key || seen.has(key)) return false;
            seen.add(key);
            if (!rule.id) rule.id = (window.crypto && crypto.randomUUID) ? crypto.randomUUID() : String(Date.now() + Math.random());
            if (!rule.name) rule.name = appNameFromPath(rule.path);
            if (!planIds.includes(rule.targetPlan)) rule.targetPlan = 'performance';
            rule.enabled = rule.enabled !== false;
            rule.keepAwake = rule.keepAwake === true;
            return true;
        });
        return cfg;
    }

    function planPriority(plan) {
        return { performance: 3, balanced: 2, powerSaver: 1 }[plan] || 0;
    }

    function mountAppPowerProfileUi() {
        const mount = document.getElementById('vm-automation-profiles') || document.getElementById('app-power-profile-mount');
        const existing = document.getElementById('app-power-profile-panel');
        if (existing) {
            if (mount && existing.parentElement !== mount) mount.appendChild(existing);
            return;
        }
        ensurePowerStyles();
        if (!mount) return;

        mount.innerHTML =
            '<div class="app-profile-panel app-profile-panel-inner rounded-xl p-lg" id="app-power-profile-panel">' +
            '<div class="flex flex-col sm:flex-row sm:items-start justify-between gap-md mb-lg relative z-10">' +
            '<div><p class="text-body-md text-on-surface-variant max-w-2xl" id="app-profile-sub"></p>' +
            '<div class="mt-sm flex items-center gap-sm"><span class="heavy-app-badge" id="app-profile-state-badge" data-active="false">' +
            '<span class="material-symbols-outlined text-[16px]">radio_button_checked</span>' +
            '<span id="app-profile-state-label"></span></span>' +
            '<span class="text-label-md text-on-surface-variant"><span id="app-profile-count">0</span> <span id="app-profile-detected-label"></span></span></div></div>' +
            '<button class="btn-primary rounded-lg py-2 px-4 text-label-md flex items-center gap-xs whitespace-nowrap" id="btn-app-profile-add" type="button">' +
            '<span class="material-symbols-outlined text-[18px]">add</span><span id="app-profile-add-label"></span></button></div>' +
            '<div class="space-y-sm mb-md relative z-10">' +
            optionHtml('app-profile-main', 'appProfileToggle', 'appProfileToggleSub', 'app_shortcut', true) +
            '</div>' +
            '<div class="app-profile-list" id="app-profile-list"></div></div>';
        refreshPowerLabels();
    }

    function syncAppPowerProfileUi() {
        setToggle(document.getElementById('toggle-app-profile-main'), normalizeAppPowerProfiles().enabled);
        renderAppPowerProfiles();
        renderAppPowerProfileStatus(appProfileStatus);
    }

    function renderAppPowerProfileStatus(status) {
        const cfg = settings ? normalizeAppPowerProfiles() : { enabled: true };
        const badge = document.getElementById('app-profile-state-badge');
        const label = document.getElementById('app-profile-state-label');
        const count = document.getElementById('app-profile-count');
        if (!badge || !label || !count) return;

        const active = !!(status && status.active && cfg.enabled);
        badge.dataset.active = active ? 'true' : 'false';
        label.textContent = !cfg.enabled ? tt('appProfileStatusDisabled') : (active ? tt('appProfileStatusActive') : tt('appProfileStatusIdle'));
        count.textContent = status && typeof status.detectedCount === 'number' ? String(status.detectedCount) : '0';
    }

    function renderAppPowerProfiles() {
        if (!settings) return;
        const list = document.getElementById('app-profile-list');
        if (!list) return;

        const cfg = normalizeAppPowerProfiles();
        setToggle(document.getElementById('toggle-app-profile-main'), cfg.enabled);

        if (!cfg.rules.length) {
            list.innerHTML = '<p class="text-label-md text-on-surface-variant opacity-70 py-3">' + esc(tt('appProfileEmpty')) + '</p>';
            return;
        }

        const activeIds = new Set((appProfileStatus && Array.isArray(appProfileStatus.activeProfiles)
            ? appProfileStatus.activeProfiles
            : []).map(p => p.ruleId));

        list.innerHTML = cfg.rules
            .slice()
            .sort((a, b) => Number(activeIds.has(b.id)) - Number(activeIds.has(a.id)) || planPriority(b.targetPlan) - planPriority(a.targetPlan) || a.name.localeCompare(b.name))
            .map(rule => {
                const missing = rule.fileExists === false;
                const active = activeIds.has(rule.id);
                return '<div class="app-profile-row" data-rule-id="' + esc(rule.id) + '">' +
                    '<div class="min-w-0"><div class="flex items-center gap-xs">' +
                    '<span class="material-symbols-outlined text-secondary-container text-[18px]">' + (active ? 'bolt' : 'app_shortcut') + '</span>' +
                    '<span class="text-body-md text-on-surface truncate">' + esc(rule.name || appNameFromPath(rule.path)) + '</span>' +
                    (missing ? '<span class="text-label-sm app-profile-missing">' + esc(tt('appProfileMissing')) + '</span>' : '') +
                    '</div><span class="app-profile-path" title="' + esc(rule.path) + '">' + esc(rule.path) + '</span></div>' +
                    '<select class="app-profile-plan bg-surface-container-low/50 text-secondary-container font-medium border border-white/10 rounded-lg py-2 px-3 text-body-md focus:outline-none focus:border-secondary-container" data-rule-id="' + esc(rule.id) + '">' +
                    '<option value="performance"' + (rule.targetPlan === 'performance' ? ' selected' : '') + '>' + esc(tt('plan_performance')) + '</option>' +
                    '<option value="balanced"' + (rule.targetPlan === 'balanced' ? ' selected' : '') + '>' + esc(tt('plan_balanced')) + '</option>' +
                    '<option value="powerSaver"' + (rule.targetPlan === 'powerSaver' ? ' selected' : '') + '>' + esc(tt('plan_powerSaver')) + '</option></select>' +
                    '<button class="app-profile-icon-btn app-profile-keep-awake' + (rule.keepAwake ? ' text-secondary-container' : '') + '" data-rule-id="' + esc(rule.id) + '" type="button" aria-pressed="' + (rule.keepAwake ? 'true' : 'false') + '" title="' + esc(tt('appProfileKeepAwake')) + '">' +
                    '<span class="material-symbols-outlined text-[20px]">' + (rule.keepAwake ? 'lock_clock' : 'bedtime') + '</span></button>' +
                    '<button class="app-profile-icon-btn app-profile-toggle-rule" data-rule-id="' + esc(rule.id) + '" type="button" title="' + esc(rule.enabled ? 'On' : 'Off') + '">' +
                    '<span class="material-symbols-outlined text-[20px]">' + (rule.enabled ? 'toggle_on' : 'toggle_off') + '</span></button>' +
                    '<button class="app-profile-icon-btn app-profile-remove-rule" data-rule-id="' + esc(rule.id) + '" type="button" title="' + esc(tt('appProfileRemove')) + '">' +
                    '<span class="material-symbols-outlined text-[20px]">delete</span></button></div>';
            }).join('');
    }

    function updateAppPowerProfiles(update) {
        const cfg = normalizeAppPowerProfiles();
        update(cfg);
        syncAppPowerProfileUi();
        scheduleSave();
    }

    function wireAppPowerProfileUi() {
        if (appProfileWired) return;

        listen(document, 'click', async (e) => {
            const main = e.target.closest('#pref-app-profile-main');
            if (main && settings) {
                updateAppPowerProfiles(cfg => { cfg.enabled = !cfg.enabled; });
                return;
            }

            const add = e.target.closest('#btn-app-profile-add');
            if (add && settings) {
                add.disabled = true;
                try {
                    const res = await Host.call('pickAppPowerProfileExecutable');
                    if (!res || !res.path) return;
                    const cfg = normalizeAppPowerProfiles();
                    const path = String(res.path).trim();
                    if (cfg.rules.some(r => r.path.toLowerCase() === path.toLowerCase())) return;
                    const id = (window.crypto && crypto.randomUUID) ? crypto.randomUUID() : String(Date.now() + Math.random());
                    cfg.rules.push({
                        id,
                        enabled: true,
                        name: appNameFromPath(path),
                        path,
                        targetPlan: 'performance',
                        keepAwake: false
                    });
                    syncAppPowerProfileUi();
                    scheduleSave();
                } catch (err) {
                    const label = document.getElementById('app-profile-state-label');
                    Host.fail(err, (msg) => {
                        if (label) label.textContent = msg;
                    });
                } finally {
                    add.disabled = false;
                }
                return;
            }

            const toggle = e.target.closest('.app-profile-toggle-rule');
            if (toggle && settings) {
                const id = toggle.dataset.ruleId;
                updateAppPowerProfiles(cfg => {
                    const rule = cfg.rules.find(r => r.id === id);
                    if (rule) rule.enabled = !rule.enabled;
                });
                return;
            }

            const keepAwake = e.target.closest('.app-profile-keep-awake');
            if (keepAwake && settings) {
                const id = keepAwake.dataset.ruleId;
                updateAppPowerProfiles(cfg => {
                    const rule = cfg.rules.find(r => r.id === id);
                    if (rule) rule.keepAwake = !rule.keepAwake;
                });
                return;
            }

            const remove = e.target.closest('.app-profile-remove-rule');
            if (remove && settings) {
                const id = remove.dataset.ruleId;
                updateAppPowerProfiles(cfg => {
                    cfg.rules = cfg.rules.filter(r => r.id !== id);
                });
            }
        });

        listen(document, 'change', (e) => {
            if (!settings || !e.target?.classList?.contains('app-profile-plan')) return;
            const id = e.target.dataset.ruleId;
            const value = planIds.includes(e.target.value) ? e.target.value : 'performance';
            updateAppPowerProfiles(cfg => {
                const rule = cfg.rules.find(r => r.id === id);
                if (rule) rule.targetPlan = value;
            });
        });

        onHost('appPowerProfileActivityChanged', (status) => {
            appProfileStatus = status;
            renderAppPowerProfileStatus(status);
            renderAppPowerProfiles();
        });
        appProfileWired = true;
    }


        function refreshStatus() {
            if (!settings || !Host.available) return Promise.resolve();
            return Host.call('getAppPowerProfileStatus').then(status => {
                appProfileStatus = status;
                renderAppPowerProfileStatus(status);
                renderAppPowerProfiles();
            }).catch(err => console.error('getAppPowerProfileStatus failed', err));
        }
        function setup() {
            if (!settings) return;
            mountAppPowerProfileUi();
            wireAppPowerProfileUi();
            syncAppPowerProfileUi();
            if (!statusLoaded) { statusLoaded = true; refreshStatus(); }
        }
        const descriptor = {
            matches(route) {
                return (route.view === 'power' && route.subviews.power === 'apps') ||
                    (route.view === 'automations' && route.subviews.automations === 'profiles');
            },
            init() { initialized = true; setup(); },
            activate: setup,
            deactivate() {},
            dispose() { disposeListeners(); appProfileWired = false; }
        };
        core.lifecycle.register('app-power-profiles', descriptor);
        return {
            load(nextSettings) { settings = nextSettings; if (initialized) setup(); },
            languageChanged() { if (initialized) { refreshPowerLabels(); renderAppPowerProfiles(); renderAppPowerProfileStatus(appProfileStatus); } },
            refreshAfterSave: refreshStatus,
            descriptor,
        };
    };
})();
