/**
 * Gestione Energetica: automation rules editor, debounced save.
 * Heavy app detection: Windows GPU preferences + generic game/heavy workload heuristics.
 * Keep-awake mode: runtime Windows power request to prevent automatic sleep.
 */
(function () {
    if (!Host.available) return;

    let settings = null;
    let saveTimer = null;
    let heavyAppWired = false;
    let heavyAppStatus = null;
    let keepAwakeWired = false;
    let keepAwakeState = null;

    const ruleIds = ['saver', 'balanced', 'performance'];
    const planIds = ['powerSaver', 'balanced', 'performance'];

    const text = {
        it: {
            heavyTitle: 'Rilevamento giochi e app pesanti',
            heavySub: 'Quando VoltManager rileva un gioco o un carico pesante applica automaticamente il piano scelto, senza creare liste infinite di applicazioni.',
            heavyToggle: 'Attiva rilevamento automatico',
            heavyToggleSub: 'Usa le Preferenze grafiche di Windows e euristiche locali generiche.',
            heavyTarget: 'Piano da usare',
            heavyTargetSub: 'Predefinito: Prestazioni elevate.',
            heavyWindows: 'Preferenze grafiche Windows',
            heavyWindowsSub: 'Rileva app marcate come “Prestazioni elevate” in Windows.',
            heavyGamePaths: 'Percorsi giochi installati',
            heavyGamePathsSub: 'Rileva Steam, Epic, GOG, Xbox, Riot, Battle.net e simili senza database dei giochi.',
            heavyResources: 'Carichi pesanti generici',
            heavyResourcesSub: 'Rileva processi utente con memoria elevata quando non esiste una preferenza Windows.',
            keepTitle: 'Tieni il PC attivo',
            keepSub: 'Blocca la sospensione automatica senza modificare permanentemente i timeout dei piani energetici.',
            keepToggle: 'Impedisci autosospensione',
            keepToggleSub: 'Utile per download notturni, rendering, training AI e task lunghi.',
            keepStatusActive: 'Attivo: il PC non andrà in sospensione automatica.',
            keepStatusIdle: 'Disattivo: valgono le normali regole del piano energetico.',
            keepBadgeActive: 'No sospensione',
            keepBadgeIdle: 'Sospensione normale',
            keepNote: 'Lo schermo continua a seguire le impostazioni di Windows; viene bloccata solo la sospensione del sistema.',
            statusIdle: 'In ascolto',
            statusDisabled: 'Disattivato',
            statusActive: 'Modalità app pesante attiva',
            detected: 'Rilevate',
            noneDetected: 'Nessuna app pesante rilevata.',
            refresh: 'Aggiorna stato',
            reason_windowsGpuPreference: 'Preferenza GPU Windows',
            reason_gameInstallPath: 'Percorso gioco',
            reason_resourceHeuristic: 'Carico risorse',
            plan_powerSaver: 'Risparmio energia',
            plan_balanced: 'Bilanciato',
            plan_performance: 'Prestazioni elevate'
        },
        en: {
            heavyTitle: 'Game and heavy app detection',
            heavySub: 'When VoltManager detects a game or heavy workload, it applies the selected plan automatically without maintaining a huge app list.',
            heavyToggle: 'Enable automatic detection',
            heavyToggleSub: 'Uses Windows Graphics preferences and local generic heuristics.',
            heavyTarget: 'Power plan to use',
            heavyTargetSub: 'Default: High performance.',
            heavyWindows: 'Windows Graphics preferences',
            heavyWindowsSub: 'Detects apps marked as “High performance” in Windows.',
            heavyGamePaths: 'Installed game locations',
            heavyGamePathsSub: 'Detects Steam, Epic, GOG, Xbox, Riot, Battle.net, and similar paths without a game database.',
            heavyResources: 'Generic heavy workloads',
            heavyResourcesSub: 'Detects user processes with high memory usage when no Windows preference exists.',
            keepTitle: 'Keep PC awake',
            keepSub: 'Prevents automatic system sleep without permanently changing power-plan timeout values.',
            keepToggle: 'Prevent automatic sleep',
            keepToggleSub: 'Useful for overnight downloads, rendering, AI training, and long-running jobs.',
            keepStatusActive: 'Active: the PC will not automatically go to sleep.',
            keepStatusIdle: 'Off: the current power plan controls sleep normally.',
            keepBadgeActive: 'Sleep blocked',
            keepBadgeIdle: 'Normal sleep',
            keepNote: 'The display still follows Windows settings; only system sleep is blocked.',
            statusIdle: 'Listening',
            statusDisabled: 'Disabled',
            statusActive: 'Heavy app mode active',
            detected: 'Detected',
            noneDetected: 'No heavy app detected.',
            refresh: 'Refresh status',
            reason_windowsGpuPreference: 'Windows GPU preference',
            reason_gameInstallPath: 'Game path',
            reason_resourceHeuristic: 'Resource load',
            plan_powerSaver: 'Power saver',
            plan_balanced: 'Balanced',
            plan_performance: 'High performance'
        }
    };

    function lang() {
        return window.I18n && I18n.getLang ? I18n.getLang() : 'it';
    }

    function tt(key) {
        const l = lang();
        return (text[l] && text[l][key]) || text.it[key] || key;
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
        return cfg;
    }

    function normalizeKeepAwake() {
        if (!settings.keepAwake) settings.keepAwake = { enabled: false, lastChangedUtc: null };
        settings.keepAwake.enabled = !!settings.keepAwake.enabled;
        return settings.keepAwake;
    }

    function ensurePowerStyles() {
        if (document.getElementById('power-feature-styles')) return;

        const style = document.createElement('style');
        style.id = 'power-feature-styles';
        style.textContent = `
@keyframes heavyAppGlow{0%{box-shadow:0 0 0 0 rgba(0,241,254,.26)}70%{box-shadow:0 0 0 13px rgba(0,241,254,0)}100%{box-shadow:0 0 0 0 rgba(0,241,254,0)}}
.heavy-app-panel,.keep-awake-panel{position:relative;overflow:hidden;border:1px solid rgba(0,241,254,.13);background:linear-gradient(135deg,rgba(18,33,49,.82),rgba(10,17,40,.68));}
.heavy-app-panel:before,.keep-awake-panel:before{content:"";position:absolute;inset:-40% auto auto -12%;width:320px;height:320px;border-radius:999px;background:radial-gradient(circle,rgba(0,241,254,.14),transparent 66%);pointer-events:none;}
.heavy-app-grid{display:grid;grid-template-columns:minmax(0,1.15fr) minmax(260px,.85fr);gap:18px;position:relative;z-index:1;}
.heavy-app-option{border:1px solid rgba(255,255,255,.09);background:rgba(255,255,255,.035);border-radius:16px;padding:14px;display:flex;align-items:center;justify-content:space-between;gap:14px;transition:border-color .22s ease,background .22s ease,transform .22s ease;}
.heavy-app-option:hover{border-color:rgba(0,241,254,.24);background:rgba(255,255,255,.055);transform:translateY(-1px);}
.heavy-app-badge,.keep-awake-badge{display:inline-flex;align-items:center;gap:7px;padding:5px 10px;border-radius:999px;border:1px solid rgba(255,255,255,.1);background:rgba(255,255,255,.05);color:rgba(211,222,239,.74);font-size:12px;line-height:1;}
.heavy-app-badge[data-active="true"],.keep-awake-badge[data-active="true"]{border-color:rgba(0,241,254,.32);background:rgba(0,241,254,.1);color:#00f1fe;animation:heavyAppGlow .9s ease-out;}
.heavy-app-list{display:grid;gap:8px;max-height:190px;overflow:auto;padding-right:2px;}
.heavy-app-row{border:1px solid rgba(255,255,255,.08);background:rgba(255,255,255,.035);border-radius:12px;padding:10px 12px;}
.heavy-app-path{display:block;max-width:100%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;color:rgba(211,222,239,.58);font-size:11px;margin-top:3px;}
.keep-awake-grid{display:grid;grid-template-columns:minmax(0,1fr) minmax(240px,.38fr);gap:18px;position:relative;z-index:1;align-items:stretch;}
.keep-awake-status{border:1px solid rgba(255,255,255,.08);background:rgba(255,255,255,.035);border-radius:16px;padding:16px;display:flex;flex-direction:column;justify-content:space-between;gap:12px;}
.vm-acc-item{overflow:hidden;}
.vm-acc-header{display:flex;align-items:center;gap:12px;width:100%;padding:18px 24px;background:transparent;border:0;cursor:pointer;text-align:left;color:inherit;font:inherit;transition:background .2s ease;}
.vm-acc-header:hover{background:rgba(255,255,255,.04);}
.vm-acc-title{flex:1;min-width:0;}
.vm-acc-chevron{margin-left:auto;color:rgba(198,198,206,.8);transition:transform .3s cubic-bezier(.4,0,.2,1);flex-shrink:0;}
.vm-acc-item[data-open="true"] .vm-acc-chevron{transform:rotate(180deg);color:#00f1fe;}
.vm-acc-body{display:grid;grid-template-rows:0fr;transition:grid-template-rows .32s cubic-bezier(.4,0,.2,1);}
.vm-acc-item[data-open="true"] .vm-acc-body{grid-template-rows:1fr;}
.vm-acc-body-inner{overflow:hidden;min-height:0;padding:0 24px 24px;}
.heavy-app-panel-inner,.keep-awake-panel-inner{position:relative;}
@media (max-width:960px){.heavy-app-grid,.keep-awake-grid{grid-template-columns:1fr}}
        `.trim();
        document.head.appendChild(style);
    }

    function optionHtml(id, titleKey, subKey, icon, on) {
        return '<div class="heavy-app-option" id="pref-' + id + '">' +
            '<div class="flex items-center gap-md">' +
            '<div class="w-11 h-11 rounded-xl bg-surface-container-lowest border border-white/5 flex items-center justify-center">' +
            '<span class="material-symbols-outlined text-secondary-container">' + icon + '</span>' +
            '</div><div><p class="text-body-md text-on-surface" id="' + id + '-title"></p>' +
            '<p class="text-label-sm text-on-surface-variant" id="' + id + '-sub"></p></div></div>' +
            '<div class="mini-toggle cursor-pointer" data-on="' + (on ? 'true' : 'false') + '" id="toggle-' + id + '">' +
            '<div class="mini-toggle-knob"></div></div></div>';
    }

    function mountKeepAwakeUi() {
        if (document.getElementById('keep-awake-panel')) return;
        ensurePowerStyles();

        const mount = document.getElementById('keep-awake-mount');
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
            '</div><aside class="keep-awake-status">' +
            '<p class="text-body-md text-on-surface" id="keep-awake-status"></p>' +
            '<p class="text-label-sm text-on-surface-variant opacity-80" id="keep-awake-note"></p>' +
            '</aside></div></div>';
        refreshPowerLabels();
    }

    function mountHeavyAppUi() {
        if (document.getElementById('heavy-app-detection-panel')) return;
        ensurePowerStyles();

        const mount = document.getElementById('heavy-app-mount');
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
            '<div class="heavy-app-list" id="heavy-app-list"></div></aside></div></div>';
        refreshPowerLabels();
    }

    function refreshPowerLabels() {
        const map = {
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
            'keep-awake-note': 'keepNote'
        };

        Object.entries(map).forEach(([id, key]) => {
            const el = document.getElementById(id);
            if (el) el.textContent = tt(key);
        });
        renderHeavyAppStatus(heavyAppStatus);
        renderKeepAwakeState(keepAwakeState);
    }

    function syncHeavyAppUi() {
        const cfg = normalizeHeavyAppDetection();
        setToggle(document.getElementById('toggle-heavy-main'), cfg.enabled);
        setToggle(document.getElementById('toggle-heavy-windows'), cfg.useWindowsGpuPreferences);
        setToggle(document.getElementById('toggle-heavy-gamepaths'), cfg.useGameInstallHeuristics);
        setToggle(document.getElementById('toggle-heavy-resources'), cfg.useResourceHeuristics);
        const select = document.getElementById('heavy-app-target-plan');
        if (select) select.value = cfg.targetPlan;
    }

    function syncKeepAwakeUi() {
        setToggle(document.getElementById('toggle-keep-awake-toggle'), normalizeKeepAwake().enabled);
        renderKeepAwakeState(keepAwakeState);
    }

    function renderKeepAwakeState(state) {
        const cfg = settings ? normalizeKeepAwake() : { enabled: false };
        const active = !!(state ? state.enabled : cfg.enabled);
        const badge = document.getElementById('keep-awake-badge');
        const badgeLabel = document.getElementById('keep-awake-badge-label');
        const status = document.getElementById('keep-awake-status');

        setToggle(document.getElementById('toggle-keep-awake-toggle'), active);
        if (badge) badge.dataset.active = active ? 'true' : 'false';
        if (badgeLabel) badgeLabel.textContent = active ? tt('keepBadgeActive') : tt('keepBadgeIdle');
        if (status) status.textContent = active ? tt('keepStatusActive') : tt('keepStatusIdle');
    }

    function renderHeavyAppStatus(status) {
        const cfg = settings ? normalizeHeavyAppDetection() : null;
        const badge = document.getElementById('heavy-app-state-badge');
        const label = document.getElementById('heavy-app-state-label');
        const count = document.getElementById('heavy-app-count');
        const list = document.getElementById('heavy-app-list');
        if (!badge || !label || !count || !list) return;

        const active = !!(status && status.active && (!cfg || cfg.enabled));
        badge.dataset.active = active ? 'true' : 'false';
        label.textContent = cfg && !cfg.enabled ? tt('statusDisabled') : (active ? tt('statusActive') : tt('statusIdle'));
        count.textContent = status && typeof status.detectedCount === 'number' ? String(status.detectedCount) : '0';

        const apps = status && Array.isArray(status.activeProcesses) ? status.activeProcesses : [];
        if (!apps.length) {
            list.innerHTML = '<p class="text-label-md text-on-surface-variant opacity-70 py-3">' + esc(tt('noneDetected')) + '</p>';
            return;
        }

        list.innerHTML = apps.map(app => {
            const reason = tt('reason_' + app.reason);
            const mb = Number.isFinite(Number(app.workingSetMb)) ? ' · ' + Number(app.workingSetMb) + ' MB' : '';
            return '<div class="heavy-app-row"><div class="flex items-center justify-between gap-sm">' +
                '<span class="text-body-md text-on-surface truncate">' + esc(app.name || 'App') + '</span>' +
                '<span class="text-label-sm text-secondary-container whitespace-nowrap">' + esc(reason) + mb + '</span>' +
                '</div><span class="heavy-app-path" title="' + esc(app.path || '') + '">' + esc(app.path || '') + '</span></div>';
        }).join('');
    }

    function updateHeavySetting(update) {
        const cfg = normalizeHeavyAppDetection();
        update(cfg);
        syncHeavyAppUi();
        scheduleSave();
    }

    function wireHeavyAppUi() {
        if (heavyAppWired) return;

        document.addEventListener('click', async (e) => {
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

            const refresh = e.target.closest('#btn-heavy-app-refresh');
            if (refresh) {
                refresh.disabled = true;
                try {
                    heavyAppStatus = await Host.call('refreshHeavyAppDetection');
                    renderHeavyAppStatus(heavyAppStatus);
                } catch (err) {
                    console.error('refreshHeavyAppDetection failed', err);
                } finally {
                    refresh.disabled = false;
                }
            }
        });

        document.addEventListener('change', (e) => {
            if (!settings || e.target?.id !== 'heavy-app-target-plan') return;
            const value = planIds.includes(e.target.value) ? e.target.value : 'performance';
            updateHeavySetting(cfg => { cfg.targetPlan = value; });
        });

        Host.on('heavyAppActivityChanged', (status) => {
            heavyAppStatus = status;
            renderHeavyAppStatus(status);
        });
        heavyAppWired = true;
    }

    function wireKeepAwakeUi() {
        if (keepAwakeWired) return;

        document.addEventListener('click', (e) => {
            const pref = e.target.closest('#pref-keep-awake-toggle');
            if (!pref || !settings) return;

            const cfg = normalizeKeepAwake();
            cfg.enabled = !cfg.enabled;
            cfg.lastChangedUtc = new Date().toISOString();
            keepAwakeState = { enabled: cfg.enabled, applied: cfg.enabled };
            syncKeepAwakeUi();
            scheduleSave();
        });

        Host.on('keepAwakeChanged', (state) => {
            keepAwakeState = state;
            if (settings) normalizeKeepAwake().enabled = !!state.enabled;
            renderKeepAwakeState(state);
        });
        keepAwakeWired = true;
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
        mountHeavyAppUi();
        mountKeepAwakeUi();
        syncHeavyAppUi();
        syncKeepAwakeUi();
        wireHeavyAppUi();
        wireKeepAwakeUi();

        Host.call('getHeavyAppStatus').then(status => {
            heavyAppStatus = status;
            renderHeavyAppStatus(status);
        }).catch(err => console.error('getHeavyAppStatus failed', err));

        keepAwakeState = { enabled: normalizeKeepAwake().enabled, applied: normalizeKeepAwake().enabled };
        renderKeepAwakeState(keepAwakeState);
    }

    function scheduleSave() {
        clearTimeout(saveTimer);
        saveTimer = setTimeout(() => {
            Host.call('saveSettings', settings)
                .then(() => Host.call('getHeavyAppStatus'))
                .then(status => {
                    heavyAppStatus = status;
                    renderHeavyAppStatus(status);
                })
                .catch(err => console.error('saveSettings failed', err));
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
        window.__voltSettings = {
            get: () => settings,
            save: scheduleSave,
            startWithWindows: res.startWithWindows,
        };
        document.dispatchEvent(new CustomEvent('settingsloaded'));
    }).catch(err => console.error('getSettings failed', err));

    document.addEventListener('langchanged', refreshPowerLabels);

    // Accordion: collapse/expand the power feature groups.
    ensurePowerStyles();
    document.addEventListener('click', (e) => {
        const header = e.target.closest('#view-power .vm-acc-header');
        if (!header) return;
        const item = header.closest('.vm-acc-item');
        if (item) item.dataset.open = item.dataset.open === 'true' ? 'false' : 'true';
    });
})();
