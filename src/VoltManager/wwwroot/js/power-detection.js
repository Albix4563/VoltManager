/** Game/heavy application detection module. */
(function () {
    const factories = window.VoltPowerFeatureFactories = window.VoltPowerFeatureFactories || {};
    factories.detection = function (core) {
        let settings = null;
        let heavyAppWired = false;
        let heavyAppStatus = null;
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

    function normalizeHeavyAppDetection() {
        if (!settings.heavyAppDetection) {
            settings.heavyAppDetection = {
                enabled: true,
                targetPlan: 'performance',
                useWindowsGpuPreferences: true,
                useGameInstallHeuristics: true,
                useResourceHeuristics: true,
                minWorkingSetMb: 1536
            };
        }

        const cfg = settings.heavyAppDetection;
        if (!planIds.includes(cfg.targetPlan)) cfg.targetPlan = 'performance';
        if (!Number.isFinite(Number(cfg.minWorkingSetMb))) cfg.minWorkingSetMb = 1536;
        cfg.minWorkingSetMb = Math.max(256, Math.min(8192, Number(cfg.minWorkingSetMb)));
        if (!cfg.useWindowsGpuPreferences && !cfg.useGameInstallHeuristics && !cfg.useResourceHeuristics) {
            cfg.useWindowsGpuPreferences = true;
        }
        cfg.alwaysGamePaths = normalizeUserPathList(cfg.alwaysGamePaths);
        cfg.neverGamePaths = normalizeUserPathList(cfg.neverGamePaths);
        cfg.priorityApplicationPaths = normalizeUserPathList(cfg.priorityApplicationPaths);
        return cfg;
    }

    // Same rules as SettingsService.NormalizeUserPathList: no blanks, case-insensitive
    // dedupe, hard cap so a runaway list cannot slow every classification down.

    function normalizeUserPathList(list) {
        if (!Array.isArray(list)) return [];
        const seen = new Set();
        return list.reduce((out, entry) => {
            const path = String(entry == null ? '' : entry).trim().replace(/^"+|"+$/g, '');
            const key = path.toLowerCase();
            if (path && !seen.has(key) && out.length < 200) {
                seen.add(key);
                out.push(path);
            }
            return out;
        }, []);
    }

    function samePath(a, b) {
        return String(a || '').toLowerCase() === String(b || '').toLowerCase();
    }

    function heavyRulesCardHtml(list, icon) {
        return '<div class="heavy-rules-card">' +
            '<div class="flex items-start justify-between gap-sm">' +
            '<div class="flex items-center gap-sm min-w-0">' +
            '<span class="material-symbols-outlined text-secondary-container text-[20px]">' + icon + '</span>' +
            '<div class="min-w-0"><p class="text-body-md text-on-surface" id="heavy-rules-' + list + '-title"></p>' +
            '<p class="text-label-sm text-on-surface-variant" id="heavy-rules-' + list + '-sub"></p></div></div>' +
            '<button class="btn-ghost rounded-lg py-1 px-3 text-label-md flex items-center gap-xs whitespace-nowrap heavy-rules-add" data-list="' + list + '" type="button">' +
            '<span class="material-symbols-outlined text-[18px]">add</span><span id="heavy-rules-' + list + '-add"></span></button></div>' +
            '<div class="heavy-rules-list" id="heavy-rules-' + list + '-list"></div></div>';
    }

    function mountHeavyAppUi() {
        const mount = document.getElementById('vm-automation-gaming') || document.getElementById('heavy-app-mount');
        const existing = document.getElementById('heavy-app-detection-panel');
        if (existing) {
            if (mount && existing.parentElement !== mount) mount.appendChild(existing);
            return;
        }
        ensurePowerStyles();
        if (!mount) return;

        mount.innerHTML =
            '<div class="heavy-app-panel-inner" id="heavy-app-detection-panel">' +
            '<div class="flex flex-col sm:flex-row sm:items-start justify-between gap-md mb-lg relative z-10">' +
            '<p class="text-body-md text-on-surface-variant max-w-2xl" id="heavy-app-sub"></p>' +
            '<button class="btn-ghost rounded-lg py-2 px-4 text-label-md flex items-center gap-xs whitespace-nowrap" id="btn-heavy-app-refresh" type="button">' +
            '<span class="material-symbols-outlined text-[18px]">refresh</span><span id="heavy-app-refresh-label"></span></button></div>' +
            '<div class="heavy-app-grid"><div class="space-y-sm">' +
            optionHtml('heavy-main', 'heavyToggle', 'heavyToggleSub', 'bolt', true) +
            '<div class="heavy-app-option"><div class="flex items-center gap-md">' +
            '<div class="w-11 h-11 rounded-xl bg-surface-container-lowest border border-white/5 flex items-center justify-center">' +
            '<span class="material-symbols-outlined text-secondary-container">speed</span></div>' +
            '<div><p class="text-body-md text-on-surface" id="heavy-app-target-title"></p>' +
            '<p class="text-label-sm text-on-surface-variant" id="heavy-app-target-sub"></p></div></div>' +
            '<select id="heavy-app-target-plan" class="bg-surface-container-low/50 text-secondary-container font-medium border border-white/10 rounded-lg py-2 px-3 text-body-md focus:outline-none focus:border-secondary-container">' +
            '<option value="performance" id="heavy-plan-performance"></option>' +
            '<option value="balanced" id="heavy-plan-balanced"></option>' +
            '<option value="powerSaver" id="heavy-plan-powerSaver"></option></select></div>' +
            optionHtml('heavy-windows', 'heavyWindows', 'heavyWindowsSub', 'display_settings', true) +
            optionHtml('heavy-gamepaths', 'heavyGamePaths', 'heavyGamePathsSub', 'folder_special', true) +
            optionHtml('heavy-resources', 'heavyResources', 'heavyResourcesSub', 'memory', true) +
            '</div><aside class="glass-card rounded-xl p-md border border-white/10 bg-surface-container-low/30">' +
            '<div class="flex items-center justify-between gap-md mb-md">' +
            '<span class="heavy-app-badge" id="heavy-app-state-badge" data-active="false">' +
            '<span class="material-symbols-outlined text-[16px]">radio_button_checked</span>' +
            '<span id="heavy-app-state-label"></span></span>' +
            '<span class="text-label-md text-on-surface-variant"><span id="heavy-app-count">0</span> ' +
            '<span id="heavy-app-detected-label"></span></span></div>' +
            '<div class="heavy-app-list" id="heavy-app-list"></div></aside></div>' +
            '<div class="heavy-rules-grid">' +
            heavyRulesCardHtml('always', 'sports_esports') +
            heavyRulesCardHtml('priority', 'workspace_premium') +
            heavyRulesCardHtml('never', 'block') +
            '</div></div>';
        refreshPowerLabels();
    }

    function syncHeavyAppUi() {
        const cfg = normalizeHeavyAppDetection();
        setToggle(document.getElementById('toggle-heavy-main'), cfg.enabled);
        setToggle(document.getElementById('toggle-heavy-windows'), cfg.useWindowsGpuPreferences);
        setToggle(document.getElementById('toggle-heavy-gamepaths'), cfg.useGameInstallHeuristics);
        setToggle(document.getElementById('toggle-heavy-resources'), cfg.useResourceHeuristics);
        const select = document.getElementById('heavy-app-target-plan');
        if (select) select.value = cfg.targetPlan;
        renderHeavyAppRules();
    }

    function renderHeavyAppStatus(status) {
        const cfg = settings ? normalizeHeavyAppDetection() : null;
        const badge = document.getElementById('heavy-app-state-badge');
        const label = document.getElementById('heavy-app-state-label');
        const count = document.getElementById('heavy-app-count');
        const list = document.getElementById('heavy-app-list');
        if (!badge || !label || !count || !list) return;

        const active = !!(status && status.active);
        badge.dataset.active = active ? 'true' : 'false';
        label.textContent = cfg && !cfg.enabled && !active ? tt('statusDisabled') : (active ? tt('statusActive') : tt('statusIdle'));
        count.textContent = status && typeof status.detectedCount === 'number' ? String(status.detectedCount) : '0';

        const apps = status && Array.isArray(status.activeProcesses) ? status.activeProcesses : [];
        if (!apps.length) {
            list.innerHTML = '<p class="text-label-md text-on-surface-variant opacity-70 py-3">' + esc(tt('noneDetected')) + '</p>';
            return;
        }

        list.innerHTML = apps.map(app => {
            const isGame = app.kind === 'game';
            const isPriority = app.kind === 'priorityApp';
            const level = String(app.confidenceLevel || '');
            const chip = isPriority ? tt('kindPriorityApp') : !isGame ? tt('kindHeavyApp')
                : (level === 'confirmed' || level === 'probable') ? tt('level_' + level) : tt('kindGame');
            const reason = tt('reason_' + app.reason);
            const mb = Number.isFinite(Number(app.workingSetMb)) ? ' · ' + Number(app.workingSetMb) + ' MB' : '';
            // Evidence codes stay raw: they are diagnostics, not copy, and they must match
            // what the detection log reports.
            const codes = Array.isArray(app.evidence) ? app.evidence.map(e => e.code).join(', ') : '';
            const hint = tt('heavyScore') + ' ' + (Number(app.confidenceScore) || 0) + '/100' + (codes ? ' · ' + codes : '');
            return '<div class="heavy-app-row" title="' + esc(hint) + '">' +
                '<div class="flex items-center justify-between gap-sm">' +
                '<span class="flex items-center gap-xs min-w-0">' +
                '<span class="material-symbols-outlined text-secondary-container text-[16px]">' + (isGame ? 'sports_esports' : 'memory') + '</span>' +
                '<span class="text-body-md text-on-surface truncate">' + esc(app.name || 'App') + '</span></span>' +
                '<span class="heavy-app-chip" data-level="' + esc(isGame ? level : 'heavyApp') + '">' + esc(chip) + '</span></div>' +
                '<span class="heavy-app-path" title="' + esc(app.path || '') + '">' + esc(app.path || '') + '</span>' +
                '<span class="heavy-app-meta">' + esc(reason + mb) + '</span></div>';
        }).join('');
    }

    function renderHeavyAppRules() {
        if (!settings) return;
        const cfg = normalizeHeavyAppDetection();

        [['always', cfg.alwaysGamePaths], ['priority', cfg.priorityApplicationPaths], ['never', cfg.neverGamePaths]].forEach(([name, paths]) => {
            const list = document.getElementById('heavy-rules-' + name + '-list');
            if (!list) return;

            if (!paths.length) {
                list.innerHTML = '<p class="text-label-sm text-on-surface-variant opacity-70 py-2">' + esc(tt('heavyRulesEmpty')) + '</p>';
                return;
            }

            list.innerHTML = paths.map(path =>
                '<div class="heavy-rule-row"><div class="min-w-0 flex-1">' +
                '<span class="text-label-md text-on-surface truncate block">' + esc(appNameFromPath(path)) + '</span>' +
                '<span class="heavy-app-path" title="' + esc(path) + '">' + esc(path) + '</span></div>' +
                '<button class="app-profile-icon-btn heavy-rules-remove" data-list="' + name + '" data-path="' + esc(path) + '" type="button" title="' + esc(tt('heavyRulesRemove')) + '">' +
                '<span class="material-symbols-outlined text-[18px]">delete</span></button></div>').join('');
        });
    }

    function renderPlanConflictToast(data) {
        if (!data || data.shouldNotifyUser === false) return;

        const previous = document.getElementById('power-plan-conflict-toast');
        if (previous) previous.remove();

        const suspects = Array.isArray(data.suspects) ? data.suspects : [];
        const suspect = suspects[0];
        const confidence = String((suspect && suspect.confidence) || '').toLowerCase();
        const processLine = suspect
            ? (confidence === 'known' ? tt('planConflictKnown') : tt('planConflictProbable')) + ': ' + (suspect.name || 'App')
            : tt('planConflictExternal');
        const expected = tt('plan_' + data.expectedPlan) || data.expectedPlan || '';

        const toast = document.createElement('div');
        toast.id = 'power-plan-conflict-toast';
        toast.style.cssText = 'position:fixed;right:22px;bottom:22px;z-index:9999;max-width:390px;border:1px solid rgb(var(--vm-accent-rgb) / .32);background:linear-gradient(135deg,rgba(18,33,49,.96),rgba(10,17,40,.96));color:#d3deef;border-radius:16px;padding:14px 16px;box-shadow:0 18px 45px rgba(0,0,0,.38),0 0 0 1px rgb(var(--vm-accent-rgb) / .08);display:flex;gap:12px;align-items:flex-start;';
        toast.innerHTML =
            '<span class="material-symbols-outlined text-secondary-container" style="font-size:24px;line-height:1;">admin_panel_settings</span>' +
            '<div style="min-width:0;flex:1;display:grid;gap:4px;">' +
            '<strong style="color:var(--vm-accent-dim);font-size:14px;">' + esc(tt('planConflictTitle')) + '</strong>' +
            '<span style="font-size:13px;line-height:1.35;color:rgba(211,222,239,.86);">' + esc(processLine) + '</span>' +
            '<span style="font-size:12px;line-height:1.35;color:rgba(211,222,239,.66);">' + esc(tt('planConflictExpected')) + ': ' + esc(expected) + '</span>' +
            '</div>' +
            '<button type="button" aria-label="close" style="background:none;border:0;color:#94a3b8;cursor:pointer;font-size:18px;line-height:1;padding:0;">x</button>';
        toast.querySelector('button')?.addEventListener('click', () => toast.remove());
        document.body.appendChild(toast);
        setTimeout(() => { if (toast.parentElement) toast.remove(); }, 12000);
    }

    function updateHeavySetting(update) {
        const cfg = normalizeHeavyAppDetection();
        update(cfg);
        syncHeavyAppUi();
        scheduleSave();
    }

    function wireHeavyAppUi() {
        if (heavyAppWired) return;

        listen(document, 'click', async (e) => {
            const pref = e.target.closest('#pref-heavy-main,#pref-heavy-windows,#pref-heavy-gamepaths,#pref-heavy-resources');
            if (pref && settings) {
                updateHeavySetting(cfg => {
                    if (pref.id === 'pref-heavy-main') cfg.enabled = !cfg.enabled;
                    if (pref.id === 'pref-heavy-windows') cfg.useWindowsGpuPreferences = !cfg.useWindowsGpuPreferences;
                    if (pref.id === 'pref-heavy-gamepaths') cfg.useGameInstallHeuristics = !cfg.useGameInstallHeuristics;
                    if (pref.id === 'pref-heavy-resources') cfg.useResourceHeuristics = !cfg.useResourceHeuristics;
                });
                return;
            }

            const addRule = e.target.closest('.heavy-rules-add');
            if (addRule && settings) {
                addRule.disabled = true;
                try {
                    const res = await Host.call('pickAppPowerProfileExecutable');
                    if (!res || !res.path) return;
                    const path = String(res.path).trim();
                    updateHeavySetting(cfg => {
                        // A path can only sit in one list: adding it here removes it from the other.
                        cfg.alwaysGamePaths = cfg.alwaysGamePaths.filter(p => !samePath(p, path));
                        cfg.neverGamePaths = cfg.neverGamePaths.filter(p => !samePath(p, path));
                        cfg.priorityApplicationPaths = cfg.priorityApplicationPaths.filter(p => !samePath(p, path));
                        const target = addRule.dataset.list === 'never'
                            ? cfg.neverGamePaths
                            : addRule.dataset.list === 'priority' ? cfg.priorityApplicationPaths : cfg.alwaysGamePaths;
                        target.push(path);
                    });
                } catch (err) {
                    const label = document.getElementById('heavy-app-state-label');
                    Host.fail(err, (msg) => {
                        if (label) label.textContent = msg;
                    });
                } finally {
                    addRule.disabled = false;
                }
                return;
            }

            const removeRule = e.target.closest('.heavy-rules-remove');
            if (removeRule && settings) {
                const path = removeRule.dataset.path;
                const which = removeRule.dataset.list;
                updateHeavySetting(cfg => {
                    if (which === 'never') cfg.neverGamePaths = cfg.neverGamePaths.filter(p => !samePath(p, path));
                    else if (which === 'priority') cfg.priorityApplicationPaths = cfg.priorityApplicationPaths.filter(p => !samePath(p, path));
                    else cfg.alwaysGamePaths = cfg.alwaysGamePaths.filter(p => !samePath(p, path));
                });
                return;
            }

            const refresh = e.target.closest('#btn-heavy-app-refresh');
            if (refresh) {
                refresh.disabled = true;
                try {
                    heavyAppStatus = await Host.call('refreshHeavyAppDetection');
                    renderHeavyAppStatus(heavyAppStatus);
                } catch (err) {
                    const label = document.getElementById('heavy-app-state-label');
                    Host.fail(err, (msg) => {
                        if (label) label.textContent = msg;
                    });
                } finally {
                    refresh.disabled = false;
                }
            }
        });

        listen(document, 'change', (e) => {
            if (!settings || e.target?.id !== 'heavy-app-target-plan') return;
            const value = planIds.includes(e.target.value) ? e.target.value : 'performance';
            updateHeavySetting(cfg => { cfg.targetPlan = value; });
        });

        onHost('heavyAppActivityChanged', (status) => {
            heavyAppStatus = status;
            renderHeavyAppStatus(status);
        });
        onHost('powerPlanConflictDetected', renderPlanConflictToast);
        heavyAppWired = true;
    }


        function refreshStatus() {
            if (!settings || !Host.available) return Promise.resolve();
            return Host.call('getHeavyAppStatus').then(status => {
                heavyAppStatus = status;
                renderHeavyAppStatus(status);
            }).catch(err => console.error('getHeavyAppStatus failed', err));
        }
        function setup() {
            if (!settings) return;
            mountHeavyAppUi();
            wireHeavyAppUi();
            syncHeavyAppUi();
            if (!statusLoaded) { statusLoaded = true; refreshStatus(); }
        }
        const descriptor = {
            matches(route) {
                return (route.view === 'power' && route.subviews.power === 'games') ||
                    (route.view === 'automations' && route.subviews.automations === 'gaming');
            },
            init() { initialized = true; setup(); },
            activate: setup,
            deactivate() {},
            dispose() { disposeListeners(); heavyAppWired = false; }
        };
        core.lifecycle.register('heavy-app-detection', descriptor);
        return {
            load(nextSettings) { settings = nextSettings; if (initialized) setup(); },
            languageChanged() { if (initialized) { refreshPowerLabels(); renderHeavyAppStatus(heavyAppStatus); renderHeavyAppRules(); } },
            refreshAfterSave: refreshStatus,
            descriptor,
        };
    };
})();
