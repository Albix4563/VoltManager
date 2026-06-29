/**
 * Gestione Energetica: automation rules editor, debounced save.
 * Heavy app detection: Windows GPU preferences + generic game/heavy workload heuristics.
 * Keep-awake mode: runtime Windows power request to prevent automatic sleep.
 */
(function () {
    if (!Host.available) return;

    let settings = null;
    let saveTimer = null;
    let appProfileWired = false;
    let appProfileStatus = null;
    let heavyAppWired = false;
    let heavyAppStatus = null;
    let keepAwakeWired = false;
    let keepAwakeState = null;

    const ruleIds = ['saver', 'balanced', 'performance'];
    const planIds = ['powerSaver', 'balanced', 'performance'];

    const text = {
        it: {
            appProfileTitle: 'Piani energetici per app',
            appProfileSub: 'Scegli un file .exe e VoltManager applichera il piano energetico selezionato mentre quell app e aperta.',
            appProfileToggle: 'Attiva profili per app',
            appProfileToggleSub: 'Le regole funzionano solo quando l automazione background e attiva.',
            appProfileAdd: 'Aggiungi app',
            appProfileEmpty: 'Nessuna app configurata.',
            appProfileStatusIdle: 'In ascolto',
            appProfileStatusDisabled: 'Disattivato',
            appProfileStatusActive: 'Profilo app attivo',
            appProfileDetected: 'Attive',
            appProfileMissing: 'File non trovato',
            appProfileRemove: 'Rimuovi',
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
            planConflictTitle: 'Piano energetico ripristinato',
            planConflictExternal: 'Cambio piano esterno rilevato',
            planConflictKnown: 'Processo rilevato',
            planConflictProbable: 'Processo probabile',
            planConflictExpected: 'Piano corretto',
            plan_powerSaver: 'Risparmio energia',
            plan_balanced: 'Bilanciato',
            plan_performance: 'Prestazioni elevate'
        },
        en: {
            appProfileTitle: 'Per-app power plans',
            appProfileSub: 'Choose an .exe file and VoltManager will apply the selected power plan while that app is open.',
            appProfileToggle: 'Enable app profiles',
            appProfileToggleSub: 'Rules run only while background automation is enabled.',
            appProfileAdd: 'Add app',
            appProfileEmpty: 'No app configured.',
            appProfileStatusIdle: 'Listening',
            appProfileStatusDisabled: 'Disabled',
            appProfileStatusActive: 'App profile active',
            appProfileDetected: 'Active',
            appProfileMissing: 'File not found',
            appProfileRemove: 'Remove',
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
            planConflictTitle: 'Power plan restored',
            planConflictExternal: 'External power-plan change detected',
            planConflictKnown: 'Detected process',
            planConflictProbable: 'Likely process',
            planConflictExpected: 'Correct plan',
            plan_powerSaver: 'Power saver',
            plan_balanced: 'Balanced',
            plan_performance: 'High performance'
        },
        zh: {
            appProfileTitle: '按应用电源计划',
            appProfileSub: '选择一个 .exe 文件，VoltManager 会在该应用打开时应用所选电源计划。',
            appProfileToggle: '启用应用配置',
            appProfileToggleSub: '规则仅在后台自动化启用时运行。',
            appProfileAdd: '添加应用',
            appProfileEmpty: '未配置应用。',
            appProfileStatusIdle: '监听中',
            appProfileStatusDisabled: '已禁用',
            appProfileStatusActive: '应用配置已激活',
            appProfileDetected: '活动中',
            appProfileMissing: '文件未找到',
            appProfileRemove: '移除',
            heavyTitle: '游戏和重负载应用检测',
            heavySub: '当 VoltManager 检测到游戏或重负载时，会自动应用所选计划，无需维护庞大的应用列表。',
            heavyToggle: '启用自动检测',
            heavyToggleSub: '使用 Windows 图形偏好和本地通用启发式规则。',
            heavyTarget: '要使用的电源计划',
            heavyTargetSub: '默认：高性能。',
            heavyWindows: 'Windows 图形偏好',
            heavyWindowsSub: '检测 Windows 中标记为“高性能”的应用。',
            heavyGamePaths: '已安装游戏位置',
            heavyGamePathsSub: '无需游戏数据库即可检测 Steam、Epic、GOG、Xbox、Riot、Battle.net 及类似路径。',
            heavyResources: '通用重负载',
            heavyResourcesSub: '在没有 Windows 偏好时，检测内存占用较高的用户进程。',
            keepTitle: '保持电脑唤醒',
            keepSub: '防止系统自动睡眠，而不永久更改电源计划超时值。',
            keepToggle: '阻止自动睡眠',
            keepToggleSub: '适用于夜间下载、渲染、AI 训练和长时间任务。',
            keepStatusActive: '已启用：电脑不会自动进入睡眠。',
            keepStatusIdle: '关闭：当前电源计划正常控制睡眠。',
            keepBadgeActive: '睡眠已阻止',
            keepBadgeIdle: '正常睡眠',
            keepNote: '显示器仍遵循 Windows 设置；仅阻止系统睡眠。',
            statusIdle: '监听中',
            statusDisabled: '已禁用',
            statusActive: '重负载应用模式已激活',
            detected: '已检测到',
            noneDetected: '未检测到重负载应用。',
            refresh: '刷新状态',
            reason_windowsGpuPreference: 'Windows GPU 偏好',
            reason_gameInstallPath: '游戏路径',
            reason_resourceHeuristic: '资源负载',
            planConflictTitle: '电源计划已恢复',
            planConflictExternal: '检测到外部电源计划更改',
            planConflictKnown: '检测到的进程',
            planConflictProbable: '可能的进程',
            planConflictExpected: '正确计划',
            plan_powerSaver: '节能',
            plan_balanced: '平衡',
            plan_performance: '高性能'
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
            return true;
        });
        return cfg;
    }

    function appNameFromPath(path) {
        const file = String(path || '').split(/[\\/]/).pop() || 'App';
        return file.replace(/\.[^.]+$/, '') || 'App';
    }

    function planPriority(plan) {
        return { performance: 3, balanced: 2, powerSaver: 1 }[plan] || 0;
    }

    function normalizeKeepAwake() {
        if (!settings.keepAwake) settings.keepAwake = { enabled: false, lastChangedUtc: null };
        settings.keepAwake.enabled = !!settings.keepAwake.enabled;
        return settings.keepAwake;
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
        if (document.getElementById('power-feature-styles')) return;

        const style = document.createElement('style');
        style.id = 'power-feature-styles';
        style.textContent = `
@keyframes heavyAppGlow{0%{box-shadow:0 0 0 0 rgba(0,241,254,.26)}70%{box-shadow:0 0 0 13px rgba(0,241,254,0)}100%{box-shadow:0 0 0 0 rgba(0,241,254,0)}}
.app-profile-panel,.heavy-app-panel,.keep-awake-panel{position:relative;overflow:hidden;border:1px solid rgba(0,241,254,.13);background:linear-gradient(135deg,rgba(18,33,49,.82),rgba(10,17,40,.68));}
.app-profile-panel:before,.heavy-app-panel:before,.keep-awake-panel:before{content:"";position:absolute;inset:-40% auto auto -12%;width:320px;height:320px;border-radius:999px;background:radial-gradient(circle,rgba(0,241,254,.14),transparent 66%);pointer-events:none;}
.heavy-app-grid{display:grid;grid-template-columns:minmax(0,1.15fr) minmax(260px,.85fr);gap:18px;position:relative;z-index:1;}
.heavy-app-option{border:1px solid rgba(255,255,255,.09);background:rgba(255,255,255,.035);border-radius:16px;padding:14px;display:flex;align-items:center;justify-content:space-between;gap:14px;transition:border-color .22s ease,background .22s ease,transform .22s ease;}
.heavy-app-option:hover{border-color:rgba(0,241,254,.24);background:rgba(255,255,255,.055);transform:translateY(-1px);}
.heavy-app-badge,.keep-awake-badge{display:inline-flex;align-items:center;gap:7px;padding:5px 10px;border-radius:999px;border:1px solid rgba(255,255,255,.1);background:rgba(255,255,255,.05);color:rgba(211,222,239,.74);font-size:12px;line-height:1;}
.heavy-app-badge[data-active="true"],.keep-awake-badge[data-active="true"]{border-color:rgba(0,241,254,.32);background:rgba(0,241,254,.1);color:#00f1fe;animation:heavyAppGlow .9s ease-out;}
.heavy-app-list{display:grid;gap:8px;max-height:190px;overflow:auto;padding-right:2px;}
.heavy-app-row{border:1px solid rgba(255,255,255,.08);background:rgba(255,255,255,.035);border-radius:12px;padding:10px 12px;}
.heavy-app-path{display:block;max-width:100%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;color:rgba(211,222,239,.58);font-size:11px;margin-top:3px;}
.app-profile-list{display:grid;gap:10px;position:relative;z-index:1;}
.app-profile-row{display:grid;grid-template-columns:minmax(0,1fr) 170px 42px 42px;gap:10px;align-items:center;border:1px solid rgba(255,255,255,.08);background:rgba(255,255,255,.035);border-radius:14px;padding:12px;}
.app-profile-path{display:block;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;color:rgba(211,222,239,.58);font-size:11px;margin-top:3px;}
.app-profile-icon-btn{width:38px;height:38px;border-radius:10px;display:flex;align-items:center;justify-content:center;border:1px solid rgba(255,255,255,.08);background:rgba(255,255,255,.04);color:rgba(211,222,239,.72);transition:border-color .2s ease,color .2s ease,background .2s ease;}
.app-profile-icon-btn:hover{border-color:rgba(0,241,254,.26);color:#00f1fe;background:rgba(0,241,254,.08);}
.app-profile-missing{color:#ffb4ab;}
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
.vm-acc-body-inner{overflow:hidden;min-height:0;padding:0 24px;transition:padding .32s cubic-bezier(.4,0,.2,1);}
.vm-acc-item[data-open="true"] .vm-acc-body-inner{padding:0 24px 24px;}
.heavy-app-panel-inner,.keep-awake-panel-inner{position:relative;}
@media (max-width:960px){.heavy-app-grid,.keep-awake-grid{grid-template-columns:1fr}.app-profile-row{grid-template-columns:1fr 1fr 38px 38px}}
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

    function mountAppPowerProfileUi() {
        if (document.getElementById('app-power-profile-panel')) return;
        ensurePowerStyles();

        const mount = document.getElementById('app-power-profile-mount');
        if (!mount) return;

        mount.innerHTML =
            '<div class="app-profile-panel app-profile-panel-inner rounded-xl p-lg" id="app-power-profile-panel">' +
            '<div class="flex flex-col sm:flex-row sm:items-start justify-between gap-md mb-lg relative z-10">' +
            '<div><p class="text-body-md text-on-surface-variant max-w-2xl" id="app-profile-sub"></p>' +
            '<div class="mt-sm flex items-center gap-sm"><span class="heavy-app-badge" id="app-profile-state-badge" data-active="false">' +
            '<span class="material-symbols-outlined text-[16px]">radio_button_checked</span>' +
            '<span id="app-profile-state-label"></span></span>' +
            '<span class="text-label-md text-on-surface-variant"><span id="app-profile-count">0</span> <span id="app-profile-detected-label"></span></span></div></div>' +
            '<button class="btn-cyan rounded-lg py-2 px-4 text-label-md flex items-center gap-xs whitespace-nowrap" id="btn-app-profile-add" type="button">' +
            '<span class="material-symbols-outlined text-[18px]">add</span><span id="app-profile-add-label"></span></button></div>' +
            '<div class="space-y-sm mb-md relative z-10">' +
            optionHtml('app-profile-main', 'appProfileToggle', 'appProfileToggleSub', 'app_shortcut', true) +
            '</div>' +
            '<div class="app-profile-list" id="app-profile-list"></div></div>';
        refreshPowerLabels();
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
            'keep-awake-note': 'keepNote'
        };

        Object.entries(map).forEach(([id, key]) => {
            const el = document.getElementById(id);
            if (el) el.textContent = tt(key);
        });
        renderAppPowerProfiles();
        renderAppPowerProfileStatus(appProfileStatus);
        renderHeavyAppStatus(heavyAppStatus);
        renderKeepAwakeState(keepAwakeState);
    }

    function syncAppPowerProfileUi() {
        setToggle(document.getElementById('toggle-app-profile-main'), normalizeAppPowerProfiles().enabled);
        renderAppPowerProfiles();
        renderAppPowerProfileStatus(appProfileStatus);
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
                    '<button class="app-profile-icon-btn app-profile-toggle-rule" data-rule-id="' + esc(rule.id) + '" type="button" title="' + esc(rule.enabled ? 'On' : 'Off') + '">' +
                    '<span class="material-symbols-outlined text-[20px]">' + (rule.enabled ? 'toggle_on' : 'toggle_off') + '</span></button>' +
                    '<button class="app-profile-icon-btn app-profile-remove-rule" data-rule-id="' + esc(rule.id) + '" type="button" title="' + esc(tt('appProfileRemove')) + '">' +
                    '<span class="material-symbols-outlined text-[20px]">delete</span></button></div>';
            }).join('');
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
        toast.style.cssText = 'position:fixed;right:22px;bottom:22px;z-index:9999;max-width:390px;border:1px solid rgba(0,241,254,.32);background:linear-gradient(135deg,rgba(18,33,49,.96),rgba(10,17,40,.96));color:#d3deef;border-radius:16px;padding:14px 16px;box-shadow:0 18px 45px rgba(0,0,0,.38),0 0 0 1px rgba(0,241,254,.08);display:flex;gap:12px;align-items:flex-start;';
        toast.innerHTML =
            '<span class="material-symbols-outlined text-secondary-container" style="font-size:24px;line-height:1;">admin_panel_settings</span>' +
            '<div style="min-width:0;flex:1;display:grid;gap:4px;">' +
            '<strong style="color:#9ffbff;font-size:14px;">' + esc(tt('planConflictTitle')) + '</strong>' +
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

    function updateAppPowerProfiles(update) {
        const cfg = normalizeAppPowerProfiles();
        update(cfg);
        syncAppPowerProfileUi();
        scheduleSave();
    }

    function wireAppPowerProfileUi() {
        if (appProfileWired) return;

        document.addEventListener('click', async (e) => {
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
                        targetPlan: 'performance'
                    });
                    syncAppPowerProfileUi();
                    scheduleSave();
                } catch (err) {
                    console.error('pickAppPowerProfileExecutable failed', err);
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

            const remove = e.target.closest('.app-profile-remove-rule');
            if (remove && settings) {
                const id = remove.dataset.ruleId;
                updateAppPowerProfiles(cfg => {
                    cfg.rules = cfg.rules.filter(r => r.id !== id);
                });
            }
        });

        document.addEventListener('change', (e) => {
            if (!settings || !e.target?.classList?.contains('app-profile-plan')) return;
            const id = e.target.dataset.ruleId;
            const value = planIds.includes(e.target.value) ? e.target.value : 'performance';
            updateAppPowerProfiles(cfg => {
                const rule = cfg.rules.find(r => r.id === id);
                if (rule) rule.targetPlan = value;
            });
        });

        Host.on('appPowerProfileActivityChanged', (status) => {
            appProfileStatus = status;
            renderAppPowerProfileStatus(status);
            renderAppPowerProfiles();
        });
        appProfileWired = true;
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
        Host.on('powerPlanConflictDetected', renderPlanConflictToast);
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
        const cpuAutomation = normalizeCpuAutomation();
        const sampleInput = document.getElementById('cpu-sample-interval');
        if (sampleInput) sampleInput.value = cpuAutomation.sampleIntervalSeconds;
        mountAppPowerProfileUi();
        mountHeavyAppUi();
        mountKeepAwakeUi();
        syncAppPowerProfileUi();
        syncHeavyAppUi();
        syncKeepAwakeUi();
        wireAppPowerProfileUi();
        wireHeavyAppUi();
        wireKeepAwakeUi();

        Host.call('getAppPowerProfileStatus').then(status => {
            appProfileStatus = status;
            renderAppPowerProfileStatus(status);
            renderAppPowerProfiles();
        }).catch(err => console.error('getAppPowerProfileStatus failed', err));

        Host.call('getHeavyAppStatus').then(status => {
            heavyAppStatus = status;
            renderHeavyAppStatus(status);
        }).catch(err => console.error('getHeavyAppStatus failed', err));

        keepAwakeState = { enabled: normalizeKeepAwake().enabled, applied: normalizeKeepAwake().enabled };
        renderKeepAwakeState(keepAwakeState);
    }

    function saveSettingsNow() {
        clearTimeout(saveTimer);
        return Host.call('saveSettings', settings)
            .then(() => Host.call('getAppPowerProfileStatus'))
            .then(status => {
                appProfileStatus = status;
                renderAppPowerProfileStatus(status);
                renderAppPowerProfiles();
                return Host.call('getHeavyAppStatus');
            })
            .then(status => {
                heavyAppStatus = status;
                renderHeavyAppStatus(status);
            });
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

    Host.call('getSettings').then(res => {
        settings = res.settings;
        if (window.VoltTheme && VoltTheme.apply) VoltTheme.apply(settings.theme);
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

    document.addEventListener('langchanged', refreshPowerLabels);

    // Accordion: collapse/expand the power feature groups.
    ensurePowerStyles();
    document.addEventListener('click', (e) => {
        const header = e.target.closest('#view-power .vm-acc-header');
        if (!header) return;
        const item = header.closest('.vm-acc-item');
        if (item) item.dataset.open = item.dataset.open === 'true' ? 'false' : 'true';
    });

    // Sub-nav (pm-seg) switching — defensively (re)mount the JS-driven panels.
    // They normally mount during loadIntoUi, but if getSettings fails or the
    // segment is selected before init completes, mount on demand here.
    document.addEventListener('click', (e) => {
        const seg = e.target.closest('#view-power .pm-seg');
        if (!seg) return;
        setTimeout(() => {
            switch (seg.dataset.pm) {
                case 'apps':
                    mountAppPowerProfileUi();
                    wireAppPowerProfileUi();
                    if (settings) syncAppPowerProfileUi();
                    break;
                case 'games':
                    mountHeavyAppUi();
                    wireHeavyAppUi();
                    if (settings) syncHeavyAppUi();
                    break;
                // keep-awake (awake) moved to the dedicated energy tab; mount
                // happens via loadIntoUi() at settings boot, no segment here.
            }
        }, 20);
    });
})();
