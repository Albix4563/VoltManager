/**
 * Gestione Energetica: automation rules editor, debounced save.
 * Heavy app detection: Windows GPU preferences + generic game/heavy workload heuristics.
 */
(function () {
    if (!Host.available) return;

    let settings = null;
    let saveTimer = null;
    let heavyAppWired = false;
    let heavyAppStatus = null;

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

    function esc(s) {
        const div = document.createElement('div');
        div.textContent = s == null ? '' : String(s);
        return div.innerHTML;
    }

    function ruleById(id) {
        return settings.rules.find(r => r.id === id);
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

    function ensureHeavyAppStyles() {
        if (document.getElementById('heavy-app-detection-styles')) return;
        const style = document.createElement('style');
        style.id = 'heavy-app-detection-styles';
        style.textContent = `
@keyframes heavyAppGlow{0%{box-shadow:0 0 0 0 rgba(0,241,254,.26)}70%{box-shadow:0 0 0 13px rgba(0,241,254,0)}100%{box-shadow:0 0 0 0 rgba(0,241,254,0)}}
.heavy-app-panel{position:relative;overflow:hidden;border:1px solid rgba(0,241,254,.13);background:linear-gradient(135deg,rgba(18,33,49,.82),rgba(10,17,40,.68));}
.heavy-app-panel:before{content:"";position:absolute;inset:-40% auto auto -12%;width:320px;height:320px;border-radius:999px;background:radial-gradient(circle,rgba(0,241,254,.14),transparent 66%);pointer-events:none;}
.heavy-app-grid{display:grid;grid-template-columns:minmax(0,1.15fr) minmax(260px,.85fr);gap:18px;position:relative;z-index:1;}
.heavy-app-option{border:1px solid rgba(255,255,255,.09);background:rgba(255,255,255,.035);border-radius:16px;padding:14px;display:flex;align-items:center;justify-content:space-between;gap:14px;transition:border-color .22s ease,background .22s ease,transform .22s ease;}
.heavy-app-option:hover{border-color:rgba(0,241,254,.24);background:rgba(255,255,255,.055);transform:translateY(-1px);}
.heavy-app-badge{display:inline-flex;align-items:center;gap:7px;padding:5px 10px;border-radius:999px;border:1px solid rgba(255,255,255,.1);background:rgba(255,255,255,.05);color:rgba(211,222,239,.74);font-size:12px;line-height:1;}
.heavy-app-badge[data-active="true"]{border-color:rgba(0,241,254,.32);background:rgba(0,241,254,.1);color:#00f1fe;animation:heavyAppGlow .9s ease-out;}
.heavy-app-list{display:grid;gap:8px;max-height:190px;overflow:auto;padding-right:2px;}
.heavy-app-row{border:1px solid rgba(255,255,255,.08);background:rgba(255,255,255,.035);border-radius:12px;padding:10px 12px;}
.heavy-app-path{display:block;max-width:100%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;color:rgba(211,222,239,.58);font-size:11px;margin-top:3px;}
@media (max-width:960px){.heavy-app-grid{grid-template-columns:1fr}}
        `.trim();
        document.head.appendChild(style);
    }

    function mountHeavyAppUi() {
        if (document.getElementById('heavy-app-detection-panel')) return;
        ensureHeavyAppStyles();
        const rulesWrap = document.querySelector('#view-power .space-y-md');
        if (!rulesWrap) return;

        rulesWrap.insertAdjacentHTML('beforebegin',
            '<section class="glass-panel rounded-xl p-lg heavy-app-panel" id="heavy-app-detection-panel">' +
            '  <div class="flex flex-col sm:flex-row sm:items-start justify-between gap-md mb-lg relative z-10">' +
            '    <div>' +
            '      <h3 class="text-title-lg text-on-surface flex items-center gap-xs"><span class="material-symbols-outlined text-secondary-container">sports_esports</span><span id="heavy-app-title"></span></h3>' +
            '      <p class="text-body-md text-on-surface-variant mt-1 max-w-2xl" id="heavy-app-sub"></p>' +
            '    </div>' +
            '    <button class="btn-ghost rounded-lg py-2 px-4 text-label-md flex items-center gap-xs" id="btn-heavy-app-refresh" type="button"><span class="material-symbols-outlined text-[18px]">refresh</span><span id="heavy-app-refresh-label"></span></button>' +
            '  </div>' +
            '  <div class="heavy-app-grid">' +
            '    <div class="space-y-sm">' +
            optionHtml('heavy-main', 'heavyToggle', 'heavyToggleSub', 'bolt', true) +
            '      <div class="heavy-app-option">' +
            '        <div class="flex items-center gap-md"><div class="w-11 h-11 rounded-xl bg-surface-container-lowest border border-white/5 flex items-center justify-center"><span class="material-symbols-outlined text-secondary-container">speed</span></div><div><p class="text-body-md text-on-surface" id="heavy-app-target-title"></p><p class="text-label-sm text-on-surface-variant" id="heavy-app-target-sub"></p></div></div>' +
            '        <select id="heavy-app-target-plan" class="bg-surface-container-low/50 text-secondary-container font-medium border border-white/10 rounded-lg py-2 px-3 text-body-md focus:outline-none focus:border-secondary-container">' +
            '          <option value="performance" id="heavy-plan-performance"></option>' +
            '          <option value="balanced" id="heavy-plan-balanced"></option>' +
            '          <option value="powerSaver" id="heavy-plan-powerSaver"></option>' +
            '        </select>' +
            '      </div>' +
            optionHtml('heavy-windows', 'heavyWindows', 'heavyWindowsSub', 'display_settings', true) +
            optionHtml('heavy-gamepaths', 'heavyGamePaths', 'heavyGamePathsSub', 'folder_special', true) +
            optionHtml('heavy-resources', 'heavyResources', 'heavyResourcesSub', 'memory', true) +
            '    </div>' +
            '    <aside class="glass-card rounded-xl p-md border border-white/10 bg-surface-container-low/30">' +
            '      <div class="flex items-center justify-between gap-md mb-md"><span class="heavy-app-badge" id="heavy-app-state-badge" data-active="false"><span class="material-symbols-outlined text-[16px]">radio_button_checked</span><span id="heavy-app-state-label"></span></span><span class="text-label-md text-on-surface-variant"><span id="heavy-app-count">0</span> <span id="heavy-app-detected-label"></span></span></div>' +
            '      <div class="heavy-app-list" id="heavy-app-list"></div>' +
            '    </aside>' +
            '  </div>' +
            '</section>');
        refreshHeavyAppLabels();
    }

    function optionHtml(id, titleKey, subKey, icon, on) {
        return '<div class="heavy-app-option" id="pref-' + id + '">' +
            '<div class="flex items-center gap-md"><div class="w-11 h-11 rounded-xl bg-surface-container-lowest border border-white/5 flex items-center justify-center"><span class="material-symbols-outlined text-secondary-container">' + icon + '</span></div><div><p class="text-body-md text-on-surface" id="' + id + '-title"></p><p class="text-label-sm text-on-surface-variant" id="' + id + '-sub"></p></div></div>' +
            '<div class="mini-toggle cursor-pointer" data-on="' + (on ? 'true' : 'false') + '" id="toggle-' + id + '"><div class="mini-toggle-knob"></div></div>' +
            '</div>';
    }

    function refreshHeavyAppLabels() {
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
            'heavy-plan-performance': 'plan_performance'
        };
        Object.entries(map).forEach(([id, key]) => {
            const el = document.getElementById(id);
            if (el) el.textContent = tt(key);
        });
        renderHeavyAppStatus(heavyAppStatus);
    }

    function setToggle(el, on) {
        if (el) el.dataset.on = on ? 'true' : 'false';
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
            return '<div class="heavy-app-row"><div class="flex items-center justify-between gap-sm"><span class="text-body-md text-on-surface truncate">' + esc(app.name || 'App') + '</span><span class="text-label-sm text-secondary-container whitespace-nowrap">' + esc(reason) + mb + '</span></div><span class="heavy-app-path" title="' + esc(app.path || '') + '">' + esc(app.path || '') + '</span></div>';
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
        syncHeavyAppUi();
        wireHeavyAppUi();
        Host.call('getHeavyAppStatus').then(status => {
            heavyAppStatus = status;
            renderHeavyAppStatus(status);
        }).catch(err => console.error('getHeavyAppStatus failed', err));
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
        // Expose for settings.js (preferences card shares the same object).
        window.__voltSettings = {
            get: () => settings,
            save: scheduleSave,
            startWithWindows: res.startWithWindows,
        };
        document.dispatchEvent(new CustomEvent('settingsloaded'));
    }).catch(err => console.error('getSettings failed', err));

    document.addEventListener('langchanged', refreshHeavyAppLabels);
})();
