/**
 * Tab router + nav indicator animation + shared boot.
 * System tab: scheduled shutdown/restart/sleep and Windows startup apps.
 */
(function () {
    const navList = document.getElementById('nav-list');
    const navIndicator = document.getElementById('nav-indicator');
    const mainContent = document.getElementById('main-content');

    const labels = {
        it: {
            nav: 'Sistema', title: 'Sistema', sub: 'Gestisci spegnimento, riavvio, sospensione e applicazioni avviate con Windows.',
            scheduleTitle: 'Azione automatica del PC', scheduleSub: 'Scegli cosa deve fare il computer a un orario preciso, se è acceso.',
            enable: 'Attiva pianificazione', action: 'Azione', shutdown: 'Spegni', restart: 'Riavvia', sleep: 'Sospendi', time: 'Orario',
            note: 'Spegnimento e riavvio non forzano il salvataggio del lavoro aperto. La sospensione usa lo stato sospensione di Windows.',
            startupTitle: 'Applicazioni di avvio', startupSub: 'Controlla le applicazioni che partono o risultano disattivate all\'avvio di Windows.',
            addTitle: 'Aggiungi app personalizzata', addSub: 'Seleziona un file .exe, .lnk, .bat o .cmd. Verrà registrato come app gestita da Miliano\'s App.',
            add: 'Aggiungi', refresh: 'Aggiorna', enabled: 'Avvio attivo', disabled: 'Avvio disattivato', loading: 'Caricamento…', empty: 'Nessuna applicazione trovata.',
            managed: 'Miliano\'s App', remove: 'Rimuovi', enableStartup: 'Attiva', disableStartup: 'Disattiva', unknown: 'App sconosciuta',
            on: 'ON', off: 'OFF', active: 'Attivo', inactive: 'Disattivato', switchHint: 'Switch animato', source: 'Origine', command: 'Percorso',
            added: 'Applicazione aggiunta all\'avvio.', removed: 'Applicazione rimossa dall\'avvio.', toggled: 'Stato applicazione aggiornato.',
            loadErr: 'Errore caricamento app di avvio: ', addErr: 'Errore aggiunta app: ', removeErr: 'Errore rimozione app: ', toggleErr: 'Errore modifica stato app: '
        },
        en: {
            nav: 'System', title: 'System', sub: 'Manage shutdown, restart, sleep, and Windows startup applications.',
            scheduleTitle: 'Automatic PC action', scheduleSub: 'Choose what the computer should do at a specific time, if it is on.',
            enable: 'Enable schedule', action: 'Action', shutdown: 'Shut down', restart: 'Restart', sleep: 'Sleep', time: 'Time',
            note: 'Shutdown and restart do not force-save open work. Sleep uses the Windows suspend state.',
            startupTitle: 'Startup applications', startupSub: 'Review applications that start, or are disabled, when Windows starts.',
            addTitle: 'Add custom app', addSub: 'Select an .exe, .lnk, .bat, or .cmd file. It will be registered as a Miliano\'s App managed entry.',
            add: 'Add', refresh: 'Refresh', enabled: 'Enabled startup', disabled: 'Disabled startup', loading: 'Loading…', empty: 'No applications found.',
            managed: 'Miliano\'s App', remove: 'Remove', enableStartup: 'Enable', disableStartup: 'Disable', unknown: 'Unknown app',
            on: 'ON', off: 'OFF', active: 'Active', inactive: 'Disabled', switchHint: 'Animated switch', source: 'Source', command: 'Path',
            added: 'Application added to startup.', removed: 'Application removed from startup.', toggled: 'Application state updated.',
            loadErr: 'Error loading startup apps: ', addErr: 'Error adding app: ', removeErr: 'Error removing app: ', toggleErr: 'Error changing app state: '
        }
    };

    let systemWired = false;
    let startupLoaded = false;

    function t(key) {
        const lang = window.I18n && I18n.getLang ? I18n.getLang() : 'it';
        return (labels[lang] && labels[lang][key]) || labels.it[key] || key;
    }

    function esc(s) {
        const div = document.createElement('div');
        div.textContent = s == null ? '' : String(s);
        return div.innerHTML;
    }

    function escAttr(s) {
        return esc(s).replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    function ensureSystemStyles() {
        if (document.getElementById('system-startup-switch-styles')) return;
        const style = document.createElement('style');
        style.id = 'system-startup-switch-styles';
        style.textContent = `
@keyframes startupCardIn{from{opacity:0;transform:translateY(10px) scale(.98)}to{opacity:1;transform:translateY(0) scale(1)}}
@keyframes startupSwitchPulse{0%{box-shadow:0 0 0 0 rgba(0,241,254,.34)}70%{box-shadow:0 0 0 12px rgba(0,241,254,0)}100%{box-shadow:0 0 0 0 rgba(0,241,254,0)}}
@keyframes startupKnobPop{0%{transform:translateX(var(--knob-x)) scale(.92)}55%{transform:translateX(var(--knob-x)) scale(1.08)}100%{transform:translateX(var(--knob-x)) scale(1)}}
@keyframes startupShimmer{0%{transform:translateX(-120%)}100%{transform:translateX(220%)}}
.startup-summary-card{position:relative;overflow:hidden;border:1px solid rgba(255,255,255,.1);border-radius:16px;background:linear-gradient(135deg,rgba(18,33,49,.72),rgba(10,17,40,.62));padding:14px 16px;display:flex;align-items:center;gap:12px;box-shadow:inset 0 1px 0 rgba(255,255,255,.05);}
.startup-summary-card:after{content:"";position:absolute;inset:0;background:radial-gradient(circle at 15% 0,rgba(0,241,254,.13),transparent 36%);opacity:.9;pointer-events:none;}
.startup-summary-card[data-tone="off"]:after{background:radial-gradient(circle at 15% 0,rgba(151,161,176,.12),transparent 36%);}
.startup-summary-icon{position:relative;z-index:1;width:36px;height:36px;border-radius:12px;display:flex;align-items:center;justify-content:center;background:rgba(0,241,254,.1);border:1px solid rgba(0,241,254,.22);color:#00f1fe;box-shadow:0 0 20px rgba(0,241,254,.1);}
.startup-summary-card[data-tone="off"] .startup-summary-icon{background:rgba(255,255,255,.06);border-color:rgba(255,255,255,.1);color:rgba(211,222,239,.78);box-shadow:none;}
.startup-summary-card>div:not(.startup-summary-icon){position:relative;z-index:1;}
.startup-card{position:relative;overflow:hidden;border:1px solid rgba(255,255,255,.1);border-radius:16px;background:linear-gradient(135deg,rgba(18,33,49,.66),rgba(10,17,40,.54));padding:16px;display:flex;flex-direction:column;gap:12px;animation:startupCardIn .32s cubic-bezier(.2,.8,.2,1) both;transition:border-color .25s ease,transform .25s ease,background .25s ease,box-shadow .25s ease;}
.startup-card:hover{transform:translateY(-1px);border-color:rgba(0,241,254,.26);background:linear-gradient(135deg,rgba(18,33,49,.82),rgba(10,17,40,.66));box-shadow:0 18px 35px rgba(0,0,0,.18),0 0 0 1px rgba(0,241,254,.04);}
.startup-card__accent{position:absolute;left:0;top:14px;bottom:14px;width:3px;border-radius:999px;background:rgba(148,163,184,.45);box-shadow:none;transition:background .25s ease,box-shadow .25s ease;}
.startup-card[data-state="on"] .startup-card__accent{background:#00f1fe;box-shadow:0 0 16px rgba(0,241,254,.58);}
.startup-card__header{display:flex;align-items:flex-start;justify-content:space-between;gap:16px;}
.startup-card__title-wrap{min-width:0;display:flex;align-items:flex-start;gap:12px;}
.startup-card__app-icon{width:42px;height:42px;border-radius:14px;display:flex;align-items:center;justify-content:center;flex-shrink:0;background:rgba(255,255,255,.06);border:1px solid rgba(255,255,255,.08);color:rgba(211,222,239,.8);transition:background .25s ease,border-color .25s ease,color .25s ease,box-shadow .25s ease;}
.startup-card[data-state="on"] .startup-card__app-icon{background:rgba(0,241,254,.1);border-color:rgba(0,241,254,.22);color:#00f1fe;box-shadow:0 0 18px rgba(0,241,254,.08);}
.startup-card__meta{min-width:0;}
.startup-card__name{font-weight:700;color:#d3deef;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;max-width:100%;}
.startup-card__badges{display:flex;align-items:center;gap:8px;flex-wrap:wrap;margin-top:6px;}
.startup-status-chip{display:inline-flex;align-items:center;gap:6px;padding:4px 9px;border-radius:999px;border:1px solid rgba(255,255,255,.1);background:rgba(255,255,255,.05);font-size:11px;line-height:1;color:rgba(211,222,239,.75);}
.startup-status-chip:before{content:"";width:6px;height:6px;border-radius:999px;background:rgba(148,163,184,.85);}
.startup-card[data-state="on"] .startup-status-chip{border-color:rgba(0,241,254,.2);background:rgba(0,241,254,.09);color:#00f1fe;}
.startup-card[data-state="on"] .startup-status-chip:before{background:#00f1fe;box-shadow:0 0 8px rgba(0,241,254,.7);}
.startup-managed-badge{display:inline-flex;align-items:center;gap:5px;padding:4px 9px;border-radius:999px;background:rgba(0,241,254,.08);color:#00f1fe;border:1px solid rgba(0,241,254,.18);font-size:11px;line-height:1;}
.startup-card__details{display:grid;gap:6px;padding-left:54px;}
.startup-detail-line{display:flex;gap:8px;min-width:0;font-size:12px;color:rgba(211,222,239,.62);}
.startup-detail-label{color:rgba(0,241,254,.78);font-weight:700;letter-spacing:.04em;text-transform:uppercase;flex:0 0 auto;}
.startup-detail-value{min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;}
.startup-actions{display:flex;align-items:center;gap:10px;flex-shrink:0;}
.startup-switch{--knob-x:3px;position:relative;width:96px;height:40px;border:0;padding:0;border-radius:999px;display:inline-flex;align-items:center;justify-content:center;outline:none;flex:0 0 auto;isolation:isolate;}
.startup-switch:focus-visible{box-shadow:0 0 0 3px rgba(0,241,254,.28);}
.startup-switch__track{position:absolute;inset:0;border-radius:999px;overflow:hidden;background:linear-gradient(135deg,rgba(50,61,78,.92),rgba(18,33,49,.92));border:1px solid rgba(255,255,255,.12);box-shadow:inset 0 1px 0 rgba(255,255,255,.08),0 8px 20px rgba(0,0,0,.22);transition:background .32s ease,border-color .32s ease,box-shadow .32s ease;}
.startup-switch__track:after{content:"";position:absolute;top:0;bottom:0;width:34px;background:linear-gradient(90deg,transparent,rgba(255,255,255,.24),transparent);opacity:0;animation:startupShimmer 2.4s ease-in-out infinite;}
.startup-switch__knob{position:absolute;z-index:2;left:3px;top:3px;width:34px;height:34px;border-radius:999px;background:linear-gradient(135deg,#f4fbff,#9fb4c8);display:flex;align-items:center;justify-content:center;color:#122131;box-shadow:0 8px 16px rgba(0,0,0,.32),inset 0 1px 0 rgba(255,255,255,.8);transform:translateX(var(--knob-x));transition:transform .32s cubic-bezier(.2,.85,.25,1.2),background .32s ease,color .32s ease;}
.startup-switch__icon{position:absolute;font-size:17px;line-height:1;transition:opacity .18s ease,transform .18s ease;}
.startup-switch__icon-on{opacity:0;transform:scale(.65) rotate(-45deg);}
.startup-switch__icon-off{opacity:1;transform:scale(1) rotate(0deg);}
.startup-switch__label{position:absolute;top:50%;transform:translateY(-50%);z-index:1;font-size:11px;font-weight:800;letter-spacing:.08em;line-height:1;transition:opacity .22s ease,transform .22s ease;color:rgba(211,222,239,.78);}
.startup-switch__label-on{left:15px;opacity:0;transform:translateY(-50%) translateX(-4px);color:#06262c;}
.startup-switch__label-off{right:13px;opacity:1;transform:translateY(-50%) translateX(0);}
.startup-switch[data-state="on"],.startup-switch[data-on="true"]{--knob-x:56px;animation:startupSwitchPulse .7s ease-out;}
.startup-switch[data-state="on"] .startup-switch__track,.startup-switch[data-on="true"] .startup-switch__track{background:linear-gradient(135deg,#00f1fe,#00a8b5);border-color:rgba(0,241,254,.78);box-shadow:inset 0 1px 0 rgba(255,255,255,.35),0 0 24px rgba(0,241,254,.28),0 10px 24px rgba(0,0,0,.2);}
.startup-switch[data-state="on"] .startup-switch__track:after,.startup-switch[data-on="true"] .startup-switch__track:after{opacity:1;}
.startup-switch[data-state="on"] .startup-switch__knob,.startup-switch[data-on="true"] .startup-switch__knob{background:linear-gradient(135deg,#f8ffff,#bffcff);color:#006a70;animation:startupKnobPop .34s ease-out;}
.startup-switch[data-state="on"] .startup-switch__icon-on,.startup-switch[data-on="true"] .startup-switch__icon-on{opacity:1;transform:scale(1) rotate(0deg);}
.startup-switch[data-state="on"] .startup-switch__icon-off,.startup-switch[data-on="true"] .startup-switch__icon-off{opacity:0;transform:scale(.65) rotate(45deg);}
.startup-switch[data-state="on"] .startup-switch__label-on,.startup-switch[data-on="true"] .startup-switch__label-on{opacity:1;transform:translateY(-50%) translateX(0);}
.startup-switch[data-state="on"] .startup-switch__label-off,.startup-switch[data-on="true"] .startup-switch__label-off{opacity:0;transform:translateY(-50%) translateX(4px);}
.startup-switch:disabled{opacity:.65;cursor:wait;filter:saturate(.65);}
.system-power-switch{width:106px;height:44px;}
.system-power-switch.startup-switch[data-on="true"]{--knob-x:62px;}
.startup-remove-btn{width:38px;height:38px;border-radius:12px;border:1px solid rgba(255,255,255,.1);display:inline-flex;align-items:center;justify-content:center;color:rgba(211,222,239,.72);background:rgba(255,255,255,.04);transition:color .2s ease,border-color .2s ease,background .2s ease,transform .2s ease;}
.startup-remove-btn:hover{color:#ffb4ab;border-color:rgba(255,180,171,.25);background:rgba(255,180,171,.08);transform:translateY(-1px);}
@media (max-width:720px){.startup-card__header{flex-direction:column}.startup-actions{align-self:stretch;justify-content:space-between}.startup-card__details{padding-left:0}.startup-switch{width:104px}.startup-switch[data-state="on"],.startup-switch[data-on="true"]{--knob-x:64px}}
        `.trim();
        document.head.appendChild(style);
    }

    function getNavLinks() {
        return Array.from(document.querySelectorAll('#nav-list a[data-view]'));
    }

    function getViews() {
        const views = {};
        document.querySelectorAll('#main-content .view[id^="view-"]').forEach(el => {
            views[el.id.replace(/^view-/, '')] = el;
        });
        return views;
    }

    function positionIndicator(link) {
        if (!link || !navIndicator) return;
        const li = link.parentElement;
        navIndicator.style.top = li.offsetTop + 'px';
        navIndicator.style.height = li.offsetHeight + 'px';
    }

    function activate(link) {
        getNavLinks().forEach(l => {
            l.classList.remove('text-secondary-container', 'font-bold', 'bg-surface-container-high/50');
            l.classList.add('text-on-surface-variant', 'font-medium', 'opacity-80');
            l.querySelector('.material-symbols-outlined')?.classList.remove('icon-fill');
        });
        link.classList.add('text-secondary-container', 'font-bold', 'bg-surface-container-high/50');
        link.classList.remove('text-on-surface-variant', 'font-medium', 'opacity-80');
        link.querySelector('.material-symbols-outlined')?.classList.add('icon-fill');
        positionIndicator(link);
    }

    function showView(name) {
        const views = getViews();
        Object.entries(views).forEach(([key, el]) => {
            el.classList.toggle('hidden', key !== name);
        });
        mainContent.classList.remove('animate-in');
        void mainContent.offsetWidth;
        mainContent.classList.add('animate-in');
        document.dispatchEvent(new CustomEvent('viewchange', { detail: { view: name } }));
    }

    function mountSystemTab() {
        ensureSystemStyles();
        if (!navList || document.querySelector('#nav-list a[data-view="system"]')) return;
        const settingsLi = document.querySelector('#nav-list a[data-view="settings"]')?.parentElement;
        const item = document.createElement('li');
        item.innerHTML = '<a class="nav-item flex items-center gap-3 text-on-surface-variant font-medium px-4 py-3 opacity-80 hover:bg-white/5 hover:text-secondary-fixed transition-all duration-300 rounded-lg active:scale-[0.98]" data-view="system" href="#"><span class="material-symbols-outlined">power_settings_new</span><span class="text-body-md system-nav-label"></span></a>';
        if (settingsLi) settingsLi.parentElement.insertBefore(item, settingsLi);
        else navList.appendChild(item);

        const settingsView = document.getElementById('view-settings');
        const section = document.createElement('section');
        section.className = 'view flex-1 flex-col hidden';
        section.id = 'view-system';
        section.innerHTML = systemViewHtml();
        if (settingsView) settingsView.parentElement.insertBefore(section, settingsView);
        else mainContent.appendChild(section);
        refreshSystemLabels();
        document.dispatchEvent(new CustomEvent('navmounted'));
    }

    function switchHtml(id, enabled, extraClass) {
        const state = enabled ? 'on' : 'off';
        const idAttr = id ? ' id="' + escAttr(id) + '"' : '';
        const dataOn = id === 'toggle-scheduled-power' ? ' data-on="' + (enabled ? 'true' : 'false') + '"' : '';
        return '<button class="startup-switch ' + (extraClass || '') + '"' + idAttr + dataOn + ' data-state="' + state + '" aria-pressed="' + (enabled ? 'true' : 'false') + '" type="button">' +
            '<span class="startup-switch__track"><span class="startup-switch__label startup-switch__label-on system-switch-on">' + esc(t('on')) + '</span><span class="startup-switch__label startup-switch__label-off system-switch-off">' + esc(t('off')) + '</span><span class="startup-switch__knob"><span class="material-symbols-outlined startup-switch__icon startup-switch__icon-on">check</span><span class="material-symbols-outlined startup-switch__icon startup-switch__icon-off">close</span></span></span>' +
            '</button>';
    }

    function systemViewHtml() {
        return '<div class="max-w-5xl mx-auto space-y-lg relative z-10 w-full">' +
            '<div class="mb-xl"><h2 class="text-headline-lg text-on-surface mb-xs system-title"></h2><p class="text-body-md text-on-surface-variant system-sub"></p></div>' +
            '<div class="grid grid-cols-12 gap-gutter">' +
            '<div class="col-span-12 lg:col-span-5 flex flex-col gap-gutter">' +
            '<div class="glass-panel rounded-xl p-lg space-y-md"><h3 class="text-title-lg text-on-surface flex items-center gap-xs"><span class="material-symbols-outlined text-secondary-container">schedule</span><span class="system-schedule-title"></span></h3><p class="text-body-md text-on-surface-variant system-schedule-sub"></p>' +
            '<div class="flex items-center justify-between group pt-sm gap-md"><div><p class="text-body-md text-on-surface system-enable"></p><p class="text-label-sm text-on-surface-variant system-note"></p></div>' + switchHtml('toggle-scheduled-power', false, 'system-power-switch cursor-pointer') + '</div>' +
            '<label class="flex items-center justify-between gap-md"><span class="text-label-sm text-on-surface-variant system-action"></span><select id="scheduled-power-action" class="bg-surface-container-low/50 text-secondary-container font-medium border border-white/10 rounded-lg py-2 px-3 text-body-md focus:outline-none focus:border-secondary-container"><option value="shutdown" class="sys-opt-shutdown"></option><option value="restart" class="sys-opt-restart"></option><option value="sleep" class="sys-opt-sleep"></option></select></label>' +
            '<label class="flex items-center justify-between gap-md"><span class="text-label-sm text-on-surface-variant system-time"></span><input id="scheduled-power-time" type="time" class="bg-surface-container-low/50 text-secondary-container font-medium border border-white/10 rounded-lg py-2 px-3 text-body-md focus:outline-none focus:border-secondary-container" /></label>' +
            '<p class="text-label-md text-on-surface-variant hidden" id="system-status"></p></div>' +
            '<div class="glass-panel rounded-xl p-lg"><h3 class="text-title-lg text-on-surface flex items-center gap-xs"><span class="material-symbols-outlined text-secondary-container">add_circle</span><span class="system-add-title"></span></h3><p class="text-body-md text-on-surface-variant mt-1 system-add-sub"></p><button class="btn-glow mt-md bg-secondary-container text-on-secondary-container text-label-md font-bold px-5 py-3 rounded-lg flex items-center gap-sm" id="btn-add-startup-app"><span class="material-symbols-outlined text-[18px]">add</span><span class="system-add-btn"></span></button></div>' +
            '</div><div class="col-span-12 lg:col-span-7"><div class="glass-panel rounded-xl p-lg"><div class="flex items-start justify-between gap-md mb-lg"><div><h3 class="text-title-lg text-on-surface flex items-center gap-xs"><span class="material-symbols-outlined text-secondary-container">apps</span><span class="system-startup-title"></span></h3><p class="text-body-md text-on-surface-variant mt-1 system-startup-sub"></p></div><button class="btn-ghost rounded-lg py-2 px-4 text-label-md flex items-center gap-xs" id="btn-refresh-startup-apps"><span class="material-symbols-outlined text-[18px]">refresh</span><span class="system-refresh"></span></button></div>' +
            '<div class="grid grid-cols-2 gap-sm mb-lg"><div class="startup-summary-card" data-tone="on"><div class="startup-summary-icon"><span class="material-symbols-outlined text-[20px]">rocket_launch</span></div><div><p class="text-title-lg text-on-surface" id="startup-enabled-count">--</p><p class="text-label-sm text-on-surface-variant system-enabled"></p></div></div><div class="startup-summary-card" data-tone="off"><div class="startup-summary-icon"><span class="material-symbols-outlined text-[20px]">pause_circle</span></div><div><p class="text-title-lg text-on-surface" id="startup-disabled-count">--</p><p class="text-label-sm text-on-surface-variant system-disabled"></p></div></div></div>' +
            '<div class="space-y-lg"><div><h4 class="text-label-md uppercase tracking-wider text-secondary-container mb-sm system-enabled"></h4><div class="space-y-sm" id="startup-enabled-list"></div></div><div><h4 class="text-label-md uppercase tracking-wider text-on-surface-variant mb-sm system-disabled"></h4><div class="space-y-sm" id="startup-disabled-list"></div></div></div>' +
            '</div></div></div></div>';
    }

    function refreshSystemLabels() {
        document.querySelectorAll('.system-nav-label').forEach(el => el.textContent = t('nav'));
        const pairs = [
            ['.system-title','title'], ['.system-sub','sub'], ['.system-schedule-title','scheduleTitle'], ['.system-schedule-sub','scheduleSub'],
            ['.system-enable','enable'], ['.system-note','note'], ['.system-action','action'], ['.system-time','time'], ['.system-add-title','addTitle'],
            ['.system-add-sub','addSub'], ['.system-add-btn','add'], ['.system-startup-title','startupTitle'], ['.system-startup-sub','startupSub'],
            ['.system-refresh','refresh'], ['.system-enabled','enabled'], ['.system-disabled','disabled'], ['.system-switch-on','on'], ['.system-switch-off','off']
        ];
        pairs.forEach(([sel, key]) => document.querySelectorAll(sel).forEach(el => el.textContent = t(key)));
        const opts = { '.sys-opt-shutdown': 'shutdown', '.sys-opt-restart': 'restart', '.sys-opt-sleep': 'sleep' };
        Object.entries(opts).forEach(([sel, key]) => document.querySelectorAll(sel).forEach(el => el.textContent = t(key)));
    }

    function normalizeScheduled(settings) {
        if (!settings.autoShutdown) settings.autoShutdown = { enabled: false, action: 'shutdown', time: '23:00', lastTriggeredLocalDate: null };
        if (!['shutdown', 'restart', 'sleep'].includes(settings.autoShutdown.action)) settings.autoShutdown.action = 'shutdown';
        if (!/^\d{2}:\d{2}$/.test(settings.autoShutdown.time || '')) settings.autoShutdown.time = '23:00';
        return settings.autoShutdown;
    }

    function setMiniToggle(el, enabled) {
        if (!el) return;
        el.dataset.on = enabled ? 'true' : 'false';
        el.dataset.state = enabled ? 'on' : 'off';
        el.setAttribute('aria-pressed', enabled ? 'true' : 'false');
    }

    function applyScheduledUi() {
        if (!window.__voltSettings) return;
        const scheduled = normalizeScheduled(window.__voltSettings.get());
        const toggle = document.getElementById('toggle-scheduled-power');
        const action = document.getElementById('scheduled-power-action');
        const time = document.getElementById('scheduled-power-time');
        setMiniToggle(toggle, scheduled.enabled);
        if (action) { action.value = scheduled.action; action.disabled = !scheduled.enabled; action.classList.toggle('opacity-50', !scheduled.enabled); }
        if (time) { time.value = scheduled.time; time.disabled = !scheduled.enabled; time.classList.toggle('opacity-50', !scheduled.enabled); }
    }

    function setSystemStatus(text, isError) {
        const el = document.getElementById('system-status');
        if (!el) return;
        el.textContent = text;
        el.classList.remove('hidden', 'ok', 'err');
        el.classList.add(isError ? 'err' : 'ok');
    }

    function wireSystemUi() {
        if (systemWired) return;
        document.addEventListener('click', async (e) => {
            const toggle = e.target.closest('#toggle-scheduled-power');
            if (toggle && window.__voltSettings) {
                const scheduled = normalizeScheduled(window.__voltSettings.get());
                scheduled.enabled = toggle.dataset.on !== 'true';
                applyScheduledUi();
                window.__voltSettings.save();
                return;
            }

            const refresh = e.target.closest('#btn-refresh-startup-apps');
            if (refresh) {
                await loadStartupApps(true);
                return;
            }

            const add = e.target.closest('#btn-add-startup-app');
            if (add && Host.available) {
                add.disabled = true;
                try {
                    const picked = await Host.call('pickStartupExecutable');
                    if (picked && picked.path) {
                        await Host.call('addStartupApp', { path: picked.path });
                        setSystemStatus(t('added'), false);
                        await loadStartupApps(true);
                    }
                } catch (err) {
                    setSystemStatus(t('addErr') + err.message, true);
                } finally {
                    add.disabled = false;
                }
                return;
            }

            const startupToggle = e.target.closest('[data-toggle-startup-id]');
            if (startupToggle && Host.available) {
                startupToggle.disabled = true;
                try {
                    await Host.call('setStartupAppEnabled', {
                        id: startupToggle.dataset.toggleStartupId,
                        enabled: startupToggle.dataset.toggleStartupEnabled === 'true',
                    });
                    setSystemStatus(t('toggled'), false);
                    await loadStartupApps(true);
                } catch (err) {
                    setSystemStatus(t('toggleErr') + err.message, true);
                } finally {
                    startupToggle.disabled = false;
                }
                return;
            }

            const remove = e.target.closest('[data-remove-startup-id]');
            if (remove && Host.available) {
                remove.disabled = true;
                try {
                    await Host.call('removeStartupApp', { id: remove.dataset.removeStartupId });
                    setSystemStatus(t('removed'), false);
                    await loadStartupApps(true);
                } catch (err) {
                    setSystemStatus(t('removeErr') + err.message, true);
                } finally {
                    remove.disabled = false;
                }
            }
        });

        document.addEventListener('change', (e) => {
            if (!window.__voltSettings) return;
            const settings = window.__voltSettings.get();
            const scheduled = normalizeScheduled(settings);
            if (e.target && e.target.id === 'scheduled-power-action') {
                scheduled.action = e.target.value;
                applyScheduledUi();
                window.__voltSettings.save();
            }
            if (e.target && e.target.id === 'scheduled-power-time') {
                if (!/^\d{2}:\d{2}$/.test(e.target.value)) return;
                scheduled.time = e.target.value;
                applyScheduledUi();
                window.__voltSettings.save();
            }
        });

        systemWired = true;
    }

    async function loadStartupApps(force) {
        if (!Host.available) return;
        if (startupLoaded && !force) return;
        const enabledList = document.getElementById('startup-enabled-list');
        const disabledList = document.getElementById('startup-disabled-list');
        if (!enabledList || !disabledList) return;
        enabledList.innerHTML = loadingRow();
        disabledList.innerHTML = loadingRow();
        updateStartupCounters(null, null);
        try {
            const data = await Host.call('getStartupApps');
            const enabled = data.enabled || [];
            const disabled = data.disabled || [];
            renderStartupList(enabledList, enabled, true);
            renderStartupList(disabledList, disabled, false);
            updateStartupCounters(enabled.length, disabled.length);
            startupLoaded = true;
        } catch (err) {
            enabledList.innerHTML = errorRow(t('loadErr') + err.message);
            disabledList.innerHTML = '';
            updateStartupCounters(null, null);
        }
    }

    function updateStartupCounters(enabled, disabled) {
        const enabledCount = document.getElementById('startup-enabled-count');
        const disabledCount = document.getElementById('startup-disabled-count');
        if (enabledCount) enabledCount.textContent = enabled == null ? '--' : String(enabled);
        if (disabledCount) disabledCount.textContent = disabled == null ? '--' : String(disabled);
    }

    function loadingRow() {
        return '<div class="text-body-md text-on-surface-variant opacity-70 py-3">' + esc(t('loading')) + '</div>';
    }

    function errorRow(text) {
        return '<div class="text-body-md text-on-surface-variant opacity-70 py-3">' + esc(text) + '</div>';
    }

    function renderStartupList(container, apps, fallbackEnabled) {
        if (!apps.length) {
            container.innerHTML = '<div class="text-body-md text-on-surface-variant opacity-70 py-3">' + esc(t('empty')) + '</div>';
            return;
        }
        container.innerHTML = apps.map(app => {
            const isEnabled = typeof app.enabled === 'boolean' ? app.enabled : !!fallbackEnabled;
            const state = isEnabled ? 'on' : 'off';
            const name = app.name || t('unknown');
            const source = app.source || '';
            const command = app.path || app.command || '';
            const nextEnabled = !isEnabled;
            const managedBadge = app.isManaged
                ? '<span class="startup-managed-badge"><span class="material-symbols-outlined text-[13px]">verified</span>' + esc(t('managed')) + '</span>'
                : '';
            const statusChip = '<span class="startup-status-chip">' + esc(isEnabled ? t('active') : t('inactive')) + '</span>';
            const toggleButton = '<button class="startup-switch" data-state="' + state + '" aria-pressed="' + (isEnabled ? 'true' : 'false') + '" aria-label="' + escAttr((isEnabled ? t('disableStartup') : t('enableStartup')) + ' ' + name) + '" title="' + escAttr(t('switchHint')) + '" data-toggle-startup-id="' + escAttr(app.id) + '" data-toggle-startup-enabled="' + (nextEnabled ? 'true' : 'false') + '" type="button">' +
                '<span class="startup-switch__track"><span class="startup-switch__label startup-switch__label-on">' + esc(t('on')) + '</span><span class="startup-switch__label startup-switch__label-off">' + esc(t('off')) + '</span><span class="startup-switch__knob"><span class="material-symbols-outlined startup-switch__icon startup-switch__icon-on">check</span><span class="material-symbols-outlined startup-switch__icon startup-switch__icon-off">close</span></span></span>' +
                '</button>';
            const removeButton = app.isManaged
                ? '<button class="startup-remove-btn" data-remove-startup-id="' + escAttr(app.id) + '" aria-label="' + escAttr(t('remove') + ' ' + name) + '" title="' + escAttr(t('remove')) + '" type="button"><span class="material-symbols-outlined text-[18px]">delete</span></button>'
                : '';
            return '<article class="startup-card" data-state="' + state + '">' +
                '<div class="startup-card__accent"></div>' +
                '<div class="startup-card__header"><div class="startup-card__title-wrap"><div class="startup-card__app-icon"><span class="material-symbols-outlined">apps</span></div><div class="startup-card__meta"><p class="startup-card__name">' + esc(name) + '</p><div class="startup-card__badges">' + statusChip + managedBadge + '</div></div></div>' +
                '<div class="startup-actions">' + toggleButton + removeButton + '</div></div>' +
                '<div class="startup-card__details">' +
                '<div class="startup-detail-line"><span class="startup-detail-label">' + esc(t('source')) + '</span><span class="startup-detail-value">' + esc(source) + '</span></div>' +
                '<div class="startup-detail-line"><span class="startup-detail-label">' + esc(t('command')) + '</span><span class="startup-detail-value" title="' + escAttr(command) + '">' + esc(command) + '</span></div>' +
                '</div></article>';
        }).join('');
    }

    function removeLegacyAutoShutdownPanel() {
        const panel = document.getElementById('auto-shutdown-panel');
        if (panel) panel.remove();
    }

    navList.addEventListener('click', (e) => {
        const link = e.target.closest('a[data-view]');
        if (!link || !navList.contains(link)) return;
        e.preventDefault();
        activate(link);
        showView(link.dataset.view);
    });

    const initialLink = getNavLinks()[0];
    if (initialLink) positionIndicator(initialLink);

    document.addEventListener('navmounted', () => {
        const activeLink = document.querySelector('#nav-list a.text-secondary-container[data-view]') || getNavLinks()[0];
        if (activeLink) positionIndicator(activeLink);
    });

    window.addEventListener('resize', () => {
        const activeLink = document.querySelector('#nav-list a.text-secondary-container[data-view]');
        if (activeLink) positionIndicator(activeLink);
    });

    document.getElementById('btn-minimize-tray').addEventListener('click', () => {
        Host.call('minimizeToTray').catch(() => {});
    });

    async function boot() {
        if (!Host.available) return;
        try {
            const info = await Host.call('getSystemInfo');
            document.getElementById('cpu-name').textContent = info.cpuName;
            document.getElementById('gpu-name').textContent = info.gpuName;
            document.getElementById('info-cpu').textContent = info.cpuName;
            document.getElementById('info-gpu').textContent = info.gpuName;
            document.getElementById('info-ram').textContent = info.ramTotalGb + ' GB';
            document.getElementById('info-os').textContent = info.osVersion;
            document.getElementById('info-version').textContent = 'v' + info.appVersion;
            document.getElementById('sidebar-version').textContent = 'VoltManager v' + info.appVersion;
            document.getElementById('version-badge').textContent = I18n.t('set_updates_curr') + 'v' + info.appVersion;
        } catch (err) {
            console.error('getSystemInfo failed', err);
        }
    }

    // --- Monitoring Toggle Logic ---
    const btnMonitoring = document.getElementById('btn-monitoring-toggle');
    const monitoringDot = document.getElementById('monitoring-dot');
    const monitoringLabel = document.getElementById('monitoring-label');

    function renderMonitoringState() {
        if (!window.__voltSettings || !btnMonitoring) return;
        const settings = window.__voltSettings.get();
        // It's active only if master automation is on AND no manual override is active
        const isPaused = !settings.masterAutomationEnabled || !!settings.override;
        
        if (!isPaused) {
            monitoringDot.className = 'w-2 h-2 rounded-full bg-secondary-container animate-pulse shadow-[0_0_8px_#00f1fe]';
            monitoringLabel.dataset.i18n = 'nav_monitoring';
            monitoringLabel.textContent = I18n.t('nav_monitoring');
        } else {
            monitoringDot.className = 'w-2 h-2 rounded-full bg-on-surface-variant';
            monitoringLabel.dataset.i18n = 'nav_monitoring_paused';
            monitoringLabel.textContent = I18n.t('nav_monitoring_paused');
        }
    }

    if (btnMonitoring) {
        btnMonitoring.addEventListener('click', async () => {
            if (!window.__voltSettings || !Host.available) return;
            const settings = window.__voltSettings.get();
            const isPaused = !settings.masterAutomationEnabled || !!settings.override;
            
            if (!isPaused) {
                // Currently active -> Ask user how long to pause
                if (window.openOverrideModal) {
                    window.openOverrideModal('balanced');
                }
            } else {
                // Currently paused -> Resume monitoring
                settings.masterAutomationEnabled = true;
                const masterToggle = document.getElementById('master-toggle');
                if (masterToggle) masterToggle.checked = true;

                try {
                    await Host.call('clearManualOverride');
                } catch (e) {
                    console.error('Failed to clear manual override', e);
                }
                
                renderMonitoringState();
                window.__voltSettings.save();
            }
        });
    }

    mountSystemTab();
    wireSystemUi();
    boot();

    if (Host.available) {
        Host.on('automationStateChanged', () => {
            // Re-fetch settings since they changed
            Host.call('getSettings').then(res => {
                if (res && res.settings && window.__voltSettings) {
                    // Update local copy
                    Object.assign(window.__voltSettings.get(), res.settings);
                    renderMonitoringState();
                }
            }).catch(() => {});
        });

        Host.on('manualOverrideChanged', () => {
            Host.call('getSettings').then(res => {
                if (res && res.settings && window.__voltSettings) {
                    Object.assign(window.__voltSettings.get(), res.settings);
                    renderMonitoringState();
                }
            }).catch(() => {});
        });
    }

    document.addEventListener('settingsloaded', () => {
        mountSystemTab();
        applyScheduledUi();
        renderMonitoringState();
        setTimeout(removeLegacyAutoShutdownPanel, 0);
    });

    document.addEventListener('viewchange', (e) => {
        if (e.detail && e.detail.view === 'system') {
            mountSystemTab();
            applyScheduledUi();
            loadStartupApps(false);
        }
    });

    document.addEventListener('langchanged', () => {
        refreshSystemLabels();
        renderMonitoringState();
        if (startupLoaded) loadStartupApps(true);
    });

    const observer = new MutationObserver(removeLegacyAutoShutdownPanel);
    observer.observe(document.body, { childList: true, subtree: true });
})();
