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
            nav: 'Gestione Energetica', title: 'Gestione Energetica', sub: 'Programma spegnimento, riavvio e sospensione del PC, e mantienilo attivo quando serve.',
            scheduleTitle: 'Azione automatica del PC', scheduleSub: 'Spegni, sospendi o riavvia il PC dopo un conto alla rovescia, oppure ogni giorno a un orario fisso.',
            enable: 'Attiva pianificazione', action: 'Azione', shutdown: 'Spegni', restart: 'Riavvia', sleep: 'Sospendi', time: 'Orario',
            note: 'Spegnimento e riavvio non forzano il salvataggio del lavoro aperto. La sospensione usa lo stato sospensione di Windows.',
            keepAwakeTitle: 'Mantieni il PC attivo', keepAwakeSub: 'Impedisce al PC di andare in sospensione automatica finché è attivo.',
            startupTitle: 'Applicazioni di avvio', startupSub: 'Controlla le applicazioni che partono o risultano disattivate all\'avvio di Windows.',
            addTitle: 'Aggiungi app personalizzata', addSub: 'Seleziona un file .exe, .lnk, .bat o .cmd. Verrà registrato come app gestita da Miliano\'s App.',
            add: 'Aggiungi', refresh: 'Aggiorna', enabled: 'Avvio attivo', disabled: 'Avvio disattivato', loading: 'Caricamento…', empty: 'Nessuna applicazione trovata.',
            managed: 'Miliano\'s App', remove: 'Rimuovi', enableStartup: 'Attiva', disableStartup: 'Disattiva', unknown: 'App sconosciuta',
            on: 'ON', off: 'OFF', active: 'Attivo', inactive: 'Disattivato', switchHint: 'Switch animato', source: 'Origine', command: 'Percorso',
            added: 'Applicazione aggiunta all\'avvio.', removed: 'Applicazione rimossa dall\'avvio.', toggled: 'Stato applicazione aggiornato.',
            loadErr: 'Errore caricamento app di avvio: ', addErr: 'Errore aggiunta app: ', removeErr: 'Errore rimozione app: ', toggleErr: 'Errore modifica stato app: ',
            confirm: 'Programma azione', cancel: 'Annulla', activeTitle: 'Azione programmata',
            relative: 'Una volta, tra…', daily: 'Ogni giorno, alle…',
            preset30: '30 minuti', preset45: '45 minuti', preset1h: '1 ora', preset2h: '2 ore', preset4h: '4 ore', custom: 'Personalizzato',
            hours: 'Ore', minutes: 'Minuti',
            remaining: 'Tra', at: 'Esecuzione prevista:',
            scheduled: 'Azione programmata.', cancelled: 'Pianificazione annullata.',
            invalidDuration: 'Durata minima: 1 minuto.', invalidTime: 'Orario non valido (usa HH:mm).'
        },
        en: {
            nav: 'Power Schedule', title: 'Power Schedule', sub: 'Schedule PC shutdown, restart and sleep, and keep it awake when needed.',
            scheduleTitle: 'Automatic PC action', scheduleSub: 'Shut down, sleep or restart the PC after a countdown, or every day at a fixed time.',
            enable: 'Enable schedule', action: 'Action', shutdown: 'Shut down', restart: 'Restart', sleep: 'Sleep', time: 'Time',
            note: 'Shutdown and restart do not force-save open work. Sleep uses the Windows suspend state.',
            keepAwakeTitle: 'Keep PC awake', keepAwakeSub: 'Prevents the PC from automatically sleeping while it is active.',
            startupTitle: 'Startup applications', startupSub: 'Review applications that start, or are disabled, when Windows starts.',
            addTitle: 'Add custom app', addSub: 'Select an .exe, .lnk, .bat, or .cmd file. It will be registered as a Miliano\'s App managed entry.',
            add: 'Add', refresh: 'Refresh', enabled: 'Enabled startup', disabled: 'Disabled startup', loading: 'Loading…', empty: 'No applications found.',
            managed: 'Miliano\'s App', remove: 'Remove', enableStartup: 'Enable', disableStartup: 'Disable', unknown: 'Unknown app',
            on: 'ON', off: 'OFF', active: 'Active', inactive: 'Disabled', switchHint: 'Animated switch', source: 'Source', command: 'Path',
            added: 'Application added to startup.', removed: 'Application removed from startup.', toggled: 'Application state updated.',
            loadErr: 'Error loading startup apps: ', addErr: 'Error adding app: ', removeErr: 'Error removing app: ', toggleErr: 'Error changing app state: ',
            confirm: 'Schedule action', cancel: 'Cancel', activeTitle: 'Scheduled action',
            relative: 'Once, after…', daily: 'Every day at…',
            preset30: '30 minutes', preset45: '45 minutes', preset1h: '1 hour', preset2h: '2 hours', preset4h: '4 hours', custom: 'Custom',
            hours: 'Hours', minutes: 'Minutes',
            remaining: 'In', at: 'Expected execution:',
            scheduled: 'Action scheduled.', cancelled: 'Schedule cancelled.',
            invalidDuration: 'Minimum duration: 1 minute.', invalidTime: 'Invalid time (use HH:mm).'
        },
        zh: {
            nav: '电源计划', title: '电源计划', sub: '计划电脑的关机、重启和睡眠，并在需要时保持唤醒。',
            scheduleTitle: '电脑自动操作', scheduleSub: '倒计时后关机、睡眠或重启，或每天在固定时间执行。',
            enable: '启用计划', action: '操作', shutdown: '关机', restart: '重启', sleep: '睡眠', time: '时间',
            note: '关机和重启不会强制保存打开的工作。睡眠使用 Windows 的挂起状态。',
            keepAwakeTitle: '保持电脑唤醒', keepAwakeSub: '在电脑处于活动状态时阻止其自动进入睡眠。',
            startupTitle: '启动应用', startupSub: '查看 Windows 启动时启动或被禁用的应用。',
            addTitle: '添加自定义应用', addSub: '选择 .exe、.lnk、.bat 或 .cmd 文件。它会注册为由 Miliano\'s App 管理的启动项。',
            add: '添加', refresh: '刷新', enabled: '启动已启用', disabled: '启动已禁用', loading: '正在加载…', empty: '未找到应用。',
            managed: 'Miliano\'s App', remove: '移除', enableStartup: '启用', disableStartup: '禁用', unknown: '未知应用',
            on: '开', off: '关', active: '启用', inactive: '禁用', switchHint: '动画开关', source: '来源', command: '路径',
            added: '应用已添加到启动项。', removed: '应用已从启动项移除。', toggled: '应用状态已更新。',
            loadErr: '加载启动应用时出错：', addErr: '添加应用时出错：', removeErr: '移除应用时出错：', toggleErr: '修改应用状态时出错：',
            confirm: '安排操作', cancel: '取消', activeTitle: '已安排的操作',
            relative: '一次，在…后', daily: '每天，在…',
            preset30: '30 分钟', preset45: '45 分钟', preset1h: '1 小时', preset2h: '2 小时', preset4h: '4 小时', custom: '自定义',
            hours: '小时', minutes: '分钟',
            remaining: '剩余', at: '预计执行：',
            scheduled: '操作已安排。', cancelled: '计划已取消。',
            invalidDuration: '持续时间至少 1 分钟。', invalidTime: '时间格式无效（请使用 HH:mm）。'
        },
        es: {
            nav: 'Programación de energía', title: 'Programación de energía', sub: 'Programa apagado, reinicio y suspensión del PC, y mantenlo activo cuando sea necesario.',
            scheduleTitle: 'Acción automática del PC', scheduleSub: 'Apaga, suspende o reinicia el PC tras una cuenta atrás, o cada día a una hora fija.',
            enable: 'Activar programación', action: 'Acción', shutdown: 'Apagar', restart: 'Reiniciar', sleep: 'Suspender', time: 'Hora',
            note: 'Apagar y reiniciar no fuerzan el guardado del trabajo abierto. La suspensión usa el estado de suspensión de Windows.',
            keepAwakeTitle: 'Mantener el PC activo', keepAwakeSub: 'Impide que el PC entre en suspensión automática mientras está activo.',
            startupTitle: 'Aplicaciones de inicio', startupSub: 'Revisa las aplicaciones que se inician o están desactivadas al arrancar Windows.',
            addTitle: 'Añadir app personalizada', addSub: 'Selecciona un archivo .exe, .lnk, .bat o .cmd. Se registrará como entrada gestionada por VoltManager.',
            add: 'Añadir', refresh: 'Actualizar', enabled: 'Inicio activo', disabled: 'Inicio desactivado', loading: 'Cargando…', empty: 'No se encontraron aplicaciones.',
            managed: 'VoltManager', remove: 'Eliminar', enableStartup: 'Activar', disableStartup: 'Desactivar', unknown: 'App desconocida',
            on: 'ON', off: 'OFF', active: 'Activo', inactive: 'Desactivado', switchHint: 'Interruptor animado', source: 'Origen', command: 'Ruta',
            added: 'Aplicación añadida al inicio.', removed: 'Aplicación eliminada del inicio.', toggled: 'Estado de la aplicación actualizado.',
            loadErr: 'Error al cargar apps de inicio: ', addErr: 'Error al añadir app: ', removeErr: 'Error al eliminar app: ', toggleErr: 'Error al cambiar estado de la app: ',
            confirm: 'Programar acción', cancel: 'Cancelar', activeTitle: 'Acción programada',
            relative: 'Una vez, dentro de…', daily: 'Cada día, a las…',
            preset30: '30 minutos', preset45: '45 minutos', preset1h: '1 hora', preset2h: '2 horas', preset4h: '4 horas', custom: 'Personalizado',
            hours: 'Horas', minutes: 'Minutos',
            remaining: 'En', at: 'Ejecución prevista:',
            scheduled: 'Acción programada.', cancelled: 'Planificación cancelada.',
            invalidDuration: 'Duración mínima: 1 minuto.', invalidTime: 'Hora no válida (usa HH:mm).'
        }
    };

    let systemWired = false;
    let startupLoaded = false;
    let gamingModeActive = false;

    function coerceGamingModeState(data) {
        if (data && data.state) return data.state;
        return data || { active: false };
    }

    function applyGamingModeState(data) {
        const state = coerceGamingModeState(data);
        gamingModeActive = !!state.active;
        document.dispatchEvent(new CustomEvent('gamingmodechanged', { detail: state }));
        renderMonitoringState();
    }

    async function setGamingMode(enabled) {
        if (!Host.available) return { success: false, state: { active: gamingModeActive } };
        const res = await Host.call('setGamingMode', { enabled: !!enabled });
        if (res && res.success === false) throw new Error('Modalità gaming non aggiornata');
        applyGamingModeState(res);
        return res;
    }

    window.__voltGamingMode = {
        isActive: () => gamingModeActive,
        apply: applyGamingModeState,
        setEnabled: setGamingMode,
    };

    function t(key) {
        const lang = window.I18n && I18n.getLang ? I18n.getLang() : 'it';
        return (labels[lang] && labels[lang][key]) || (labels.en && labels.en[key]) || key;
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
.startup-card{position:relative;overflow:hidden;border:1px solid rgba(255,255,255,.1);border-radius:16px;background:linear-gradient(135deg,rgba(18,33,49,.66),rgba(10,17,40,.54));padding:16px;display:flex;flex-direction:column;gap:12px;animation:startupCardIn .32s cubic-bezier(.2,.8,.2,1) both;transition:border-color .25s ease,transform .25s ease,background .25s ease,box-shadow .25s ease;container-type:inline-size;}
.startup-card:hover{transform:translateY(-1px);border-color:rgba(0,241,254,.26);background:linear-gradient(135deg,rgba(18,33,49,.82),rgba(10,17,40,.66));box-shadow:0 18px 35px rgba(0,0,0,.18),0 0 0 1px rgba(0,241,254,.04);}
.startup-card__accent{position:absolute;left:0;top:14px;bottom:14px;width:3px;border-radius:999px;background:rgba(148,163,184,.45);box-shadow:none;transition:background .25s ease,box-shadow .25s ease;}
.startup-card[data-state="on"] .startup-card__accent{background:#00f1fe;box-shadow:0 0 16px rgba(0,241,254,.58);}
.startup-card__header{display:flex;align-items:flex-start;justify-content:space-between;gap:16px;flex-wrap:wrap;}
.startup-card__title-wrap{min-width:0;max-width:100%;display:flex;align-items:flex-start;gap:12px;flex:1 1 120px;}
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
.startup-actions{display:flex;align-items:center;justify-content:flex-end;gap:10px;flex:0 1 auto;flex-wrap:wrap;max-width:100%;min-width:0;}
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
.startup-pin-btn--active{color:#00f1fe;border-color:rgba(0,241,254,.32);background:rgba(0,241,254,.12);}
.startup-pin-btn--active:hover{color:#9ffbff;border-color:rgba(0,241,254,.42);background:rgba(0,241,254,.18);transform:translateY(-1px);}
.startup-remove-btn:disabled{opacity:.4;cursor:not-allowed;transform:none;}
.startup-remove-btn:disabled:hover{color:rgba(211,222,239,.72);border-color:rgba(255,255,255,.1);background:rgba(255,255,255,.04);transform:none;}
@media (max-width:720px){.startup-card__header{flex-direction:column}.startup-actions{align-self:stretch;justify-content:space-between}.startup-card__details{padding-left:0}.startup-switch{width:104px}.startup-switch[data-state="on"],.startup-switch[data-on="true"]{--knob-x:64px}}
@container (max-width:320px){.startup-card__header{display:grid;grid-template-columns:minmax(0,1fr);align-items:start;gap:12px}.startup-actions{width:100%;justify-content:flex-start}.startup-card__details{padding-left:0}.startup-card__title-wrap{width:100%;}}
@container (max-width:260px){.startup-actions{gap:8px}.startup-switch{width:88px;height:38px}.startup-switch[data-state="on"],.startup-switch[data-on="true"]{--knob-x:48px}.startup-remove-btn{width:38px;height:38px}}
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
        // Rect-based so the indicator stays aligned even with the new
        // grouped section-label rows between nav items.
        const parent = navIndicator.offsetParent || navIndicator.parentElement;
        if (!parent) return;
        const pr = parent.getBoundingClientRect();
        const lr = link.getBoundingClientRect();
        navIndicator.style.top = (lr.top - pr.top) + 'px';
        navIndicator.style.height = lr.height + 'px';
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

    function prefersReducedMotion() {
        return !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
    }

    // Cascade a container's direct children in via the .vm-stagger keyframe.
    function playStagger(container) {
        if (!container) return;
        Array.from(container.children).forEach((el, i) => el.style.setProperty('--vm-i', i));
        container.classList.remove('vm-stagger');
        void container.offsetWidth;
        container.classList.add('vm-stagger');
    }

    // Soft scale/fade the whole view in, then cascade its real row group.
    function staggerIn(view) {
        if (!view) return;
        view.classList.remove('vm-enter');
        void view.offsetWidth;
        view.classList.add('vm-enter');
        let c = view;
        while (c.children.length === 1 && c.firstElementChild && c.firstElementChild.children.length > 1) {
            c = c.firstElementChild;
        }
        playStagger(c);
    }

    function showView(name) {
        const views = getViews();
        const next = views[name];
        const current = Object.values(views).find(el => !el.classList.contains('hidden'));
        const reduce = prefersReducedMotion();

        const swap = () => {
            Object.entries(views).forEach(([key, el]) => el.classList.toggle('hidden', key !== name));
            if (!reduce) staggerIn(next);
            document.dispatchEvent(new CustomEvent('viewchange', { detail: { view: name } }));
        };

        // Instant swap when motion is reduced, on first paint, or re-selecting
        // the same tab. Otherwise: animate the current view out, then swap in.
        if (reduce || !current || current === next) {
            swap();
            return;
        }

        current.classList.add('vm-leaving');
        // vmLeave runs .24s; swap just after it completes. A timer (not
        // animationend) avoids bubbled child-animation events ending it early.
        setTimeout(() => {
            current.classList.remove('vm-leaving');
            swap();
        }, 250);
    }

    // Power Management sub-nav: switch which .vm-acc-item panel is shown.
    function activatePowerPanel(key, animate) {
        const view = document.getElementById('view-power');
        if (!view || !key) return;
        view.querySelectorAll('.pm-seg').forEach(seg => {
            const on = seg.dataset.pm === key;
            seg.classList.toggle('active', on);
            seg.setAttribute('aria-selected', on ? 'true' : 'false');
        });
        let active = null;
        view.querySelectorAll('.vm-acc-item[data-pm]').forEach(item => {
            const on = item.dataset.pm === key;
            item.classList.toggle('pm-active', on);
            item.dataset.open = on ? 'true' : 'false';
            if (on) active = item;
        });
        if (animate && active && !prefersReducedMotion()) {
            active.classList.remove('vm-enter');
            void active.offsetWidth;
            active.classList.add('vm-enter');
            playStagger(active.querySelector('.vm-acc-body-inner'));
        }
    }

    function mountSystemTab() {
        ensureSystemStyles();
        if (!navList || document.querySelector('#nav-list a[data-view="system"]')) return;
        // Place the System item under the CONTROL group (right after Power).
        const powerLi = document.querySelector('#nav-list a[data-view="power"]')?.parentElement;
        const item = document.createElement('li');
        item.innerHTML = '<a class="nav-item flex items-center gap-3 text-on-surface-variant font-medium px-4 py-3 opacity-80 hover:bg-white/5 hover:text-secondary-fixed transition-all duration-300 rounded-lg active:scale-[0.98]" data-view="system" href="#"><span class="material-symbols-outlined">power_settings_new</span><span class="text-body-md system-nav-label"></span></a>';
        if (powerLi) powerLi.parentElement.insertBefore(item, powerLi.nextSibling);
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
        return '<div class="max-w-4xl mx-auto space-y-lg relative z-10 w-full">' +
            '<div class="mb-xl"><h2 class="text-headline-lg text-on-surface mb-xs system-title"></h2><p class="text-body-md text-on-surface-variant system-sub"></p></div>' +
            '<div class="grid grid-cols-12 gap-gutter">' +
            // Schedule panel — new: relative + daily dual-mode
            '<div class="col-span-12 lg:col-span-6 flex flex-col gap-gutter">' +
            '<div class="glass-panel rounded-xl p-lg space-y-md" id="schedule-panel"><h3 class="text-title-lg text-on-surface flex items-center gap-xs"><span class="material-symbols-outlined text-secondary-container">schedule</span><span class="system-schedule-title"></span></h3><p class="text-body-md text-on-surface-variant system-schedule-sub"></p>' +
            // Mode tabs
            '<div class="flex rounded-lg bg-surface-container-low/30 p-1" id="schedule-mode-tabs">' +
            '<button class="flex-1 py-2 px-3 rounded-md text-label-md font-medium transition-colors schedule-mode-relative" data-mode="relative"></button>' +
            '<button class="flex-1 py-2 px-3 rounded-md text-label-md font-medium transition-colors schedule-mode-daily" data-mode="daily"></button>' +
            '</div>' +
            // Relative mode content
            '<div id="schedule-relative-content">' +
            '<div class="flex flex-wrap gap-xs pt-sm" id="schedule-presets"></div>' +
            '<div id="schedule-custom-fields" class="hidden flex items-center gap-sm pt-sm"><input id="schedule-custom-hours" type="number" min="0" max="168" value="0" class="bg-surface-container-low/50 text-secondary-container font-medium border border-white/10 rounded-lg py-2 px-3 w-20 text-body-md focus:outline-none focus:border-secondary-container" placeholder="h" /> <span class="text-label-sm text-on-surface-variant schedule-hours"></span> <input id="schedule-custom-minutes" type="number" min="0" max="59" value="30" class="bg-surface-container-low/50 text-secondary-container font-medium border border-white/10 rounded-lg py-2 px-3 w-20 text-body-md focus:outline-none focus:border-secondary-container" placeholder="min" /> <span class="text-label-sm text-on-surface-variant schedule-minutes"></span></div>' +
            '<div class="flex items-center gap-sm pt-sm"><span class="text-label-sm text-on-surface-variant system-action"></span><select id="scheduled-power-action" class="bg-surface-container-low/50 text-secondary-container font-medium border border-white/10 rounded-lg py-2 px-3 text-body-md focus:outline-none focus:border-secondary-container"><option value="shutdown" class="sys-opt-shutdown"></option><option value="sleep" class="sys-opt-sleep"></option></select></div>' +
            '<p id="schedule-summary" class="text-label-md text-on-surface-variant hidden pt-xs"></p>' +
            '<button id="btn-schedule-action" class="w-full mt-md py-2.5 px-4 rounded-lg font-medium text-body-md bg-secondary-container/20 text-secondary-container border border-secondary-container/30 hover:bg-secondary-container/30 transition-colors system-confirm"></button>' +
            '</div>' +
            // Daily mode content
            '<div id="schedule-daily-content" class="hidden">' +
            '<label class="flex items-center justify-between gap-md pt-sm"><span class="text-label-sm text-on-surface-variant system-time"></span><input id="scheduled-power-time" type="time" class="bg-surface-container-low/50 text-secondary-container font-medium border border-white/10 rounded-lg py-2 px-3 text-body-md focus:outline-none focus:border-secondary-container" /></label>' +
            '<label class="flex items-center justify-between gap-md pt-sm"><span class="text-label-sm text-on-surface-variant system-action"></span><select id="scheduled-daily-action" class="bg-surface-container-low/50 text-secondary-container font-medium border border-white/10 rounded-lg py-2 px-3 text-body-md focus:outline-none focus:border-secondary-container"><option value="shutdown" class="sys-opt-shutdown"></option><option value="restart" class="sys-opt-restart"></option><option value="sleep" class="sys-opt-sleep"></option></select></label>' +
            '<p id="schedule-daily-status" class="text-label-md text-on-surface-variant hidden pt-xs"></p>' +
            '<button id="btn-schedule-daily" class="w-full mt-md py-2.5 px-4 rounded-lg font-medium text-body-md bg-secondary-container/20 text-secondary-container border border-secondary-container/30 hover:bg-secondary-container/30 transition-colors system-confirm"></button>' +
            '</div>' +
            // Active schedule display
            '<div id="schedule-active" class="hidden pt-md border-t border-white/10"><p class="text-label-sm text-on-surface-variant system-active-title"></p><div class="flex items-center gap-sm pt-xs"><div class="flex-1"><p id="schedule-active-text" class="text-body-md text-on-surface font-medium"></p><p id="schedule-active-countdown" class="text-label-md text-secondary-container"></p></div><button id="btn-cancel-schedule" class="py-1.5 px-3 rounded-lg text-label-md font-medium text-error border border-error/30 hover:bg-error/10 transition-colors system-cancel"></button></div></div>' +
            '<p class="text-label-md text-on-surface-variant hidden" id="system-status"></p></div>' +
            '</div>' +
            // Keep-awake panel
            '<div class="col-span-12 lg:col-span-6">' +
            '<div class="glass-panel rounded-xl p-lg space-y-md"><h3 class="text-title-lg text-on-surface flex items-center gap-xs"><span class="material-symbols-outlined text-secondary-container">bedtime_off</span><span class="system-keepawake-title"></span></h3><p class="text-body-md text-on-surface-variant system-keepawake-sub"></p>' +
            '<div id="keep-awake-mount"></div>' +
            '</div>' +
            '</div></div></div>';
    }

    function refreshSystemLabels() {
        document.querySelectorAll('.system-nav-label').forEach(el => el.textContent = t('nav'));
        const pairs = [
            ['.system-title','title'], ['.system-sub','sub'],
            ['.system-schedule-title','scheduleTitle'], ['.system-schedule-sub','scheduleSub'],
            ['.system-action','action'], ['.system-time','time'],
            ['.system-keepawake-title','keepAwakeTitle'], ['.system-keepawake-sub','keepAwakeSub'],
            ['.system-confirm','confirm'], ['.system-cancel','cancel'],
            ['.system-active-title','activeTitle'],
            ['.schedule-mode-relative','relative'], ['.schedule-mode-daily','daily'],
            ['.system-switch-on','on'], ['.system-switch-off','off'],
            ['.schedule-hours','hours'], ['.schedule-minutes','minutes']
        ];
        pairs.forEach(([sel, key]) => document.querySelectorAll(sel).forEach(el => el.textContent = t(key)));
        const opts = { '.sys-opt-shutdown': 'shutdown', '.sys-opt-restart': 'restart', '.sys-opt-sleep': 'sleep' };
        Object.entries(opts).forEach(([sel, key]) => document.querySelectorAll(sel).forEach(el => el.textContent = t(key)));
        // Re-render preset buttons with translated labels
        renderPresetButtons();
        // Apply schedule state
        if (currentScheduleState) applyScheduledPowerActionState(currentScheduleState);
    }

    // -- New schedule state management --

    var currentScheduleState = null;
    var scheduleCountdownTimer = null;
    var scheduleMode = 'relative';

    function renderPresetButtons() {
        var container = document.getElementById('schedule-presets');
        if (!container) return;
        var presets = [
            { mins: 30, key: 'preset30' },
            { mins: 45, key: 'preset45' },
            { mins: 60, key: 'preset1h' },
            { mins: 120, key: 'preset2h' },
            { mins: 240, key: 'preset4h' },
            { mins: -1, key: 'custom' }
        ];
        container.innerHTML = presets.map(function(p) {
            var label = t(p.key);
            var cls = 'py-1.5 px-3 rounded-lg text-label-md font-medium border border-white/10 hover:bg-secondary-container/20 transition-colors cursor-pointer';
            if (p.mins === -1) cls += ' schedule-preset-custom';
            else cls += ' schedule-preset-btn';
            return '<button class="' + cls + '" data-minutes="' + p.mins + '">' + esc(label) + '</button>';
        }).join('');
    }

    function applyScheduledPowerActionState(state) {
        currentScheduleState = state;
        clearInterval(scheduleCountdownTimer);
        scheduleCountdownTimer = null;

        var activeEl = document.getElementById('schedule-active');
        var relativeContent = document.getElementById('schedule-relative-content');
        var dailyContent = document.getElementById('schedule-daily-content');
        var summary = document.getElementById('schedule-summary');
        var cancelBtn = document.getElementById('btn-cancel-schedule');

        if (!activeEl) return;

        if (state && state.enabled) {
            activeEl.classList.remove('hidden');
            relativeContent.classList.add('hidden');
            dailyContent.classList.add('hidden');
            cancelBtn.classList.remove('hidden');

            var actionName = t(state.action === 'Sleep' ? 'sleep' : (state.action === 'Restart' ? 'restart' : 'shutdown'));
            var activeText = document.getElementById('schedule-active-text');
            if (activeText) activeText.textContent = actionName;

            if (state.mode === 'Relative' && state.executeAtUtc && state.remainingSeconds > 0) {
                var countdownEl = document.getElementById('schedule-active-countdown');
                var updateCountdown = function() {
                    if (!currentScheduleState || !currentScheduleState.executeAtUtc) return;
                    var remaining = Math.max(0, Math.floor((new Date(currentScheduleState.executeAtUtc).getTime() - Date.now()) / 1000));
                    currentScheduleState.remainingSeconds = remaining;
                    if (countdownEl) {
                        var h = Math.floor(remaining / 3600);
                        var m = Math.floor((remaining % 3600) / 60);
                        var s = remaining % 60;
                        countdownEl.textContent = t('remaining') + ' ' + h + 'h ' + m + 'm ' + s + 's';
                    }
                    if (remaining <= 0 && scheduleCountdownTimer) {
                        clearInterval(scheduleCountdownTimer);
                        scheduleCountdownTimer = null;
                    }
                };
                updateCountdown();
                scheduleCountdownTimer = setInterval(updateCountdown, 1000);
            } else if (state.mode === 'Daily' && state.dailyTime) {
                var countdownEl = document.getElementById('schedule-active-countdown');
                if (countdownEl) countdownEl.textContent = t('at') + ' ' + state.dailyTime;
            }
        } else {
            activeEl.classList.add('hidden');
            relativeContent.classList.remove('hidden');
            scheduleMode = 'relative';
            updateScheduleModeUI();
        }
    }

    function updateScheduleModeUI() {
        var relativeContent = document.getElementById('schedule-relative-content');
        var dailyContent = document.getElementById('schedule-daily-content');
        var tabs = document.querySelectorAll('#schedule-mode-tabs button');

        if (scheduleMode === 'relative') {
            relativeContent.classList.remove('hidden');
            dailyContent.classList.add('hidden');
        } else {
            relativeContent.classList.add('hidden');
            dailyContent.classList.remove('hidden');
        }

        tabs.forEach(function(btn) {
            var isActive = btn.dataset.mode === scheduleMode;
            btn.classList.toggle('bg-secondary-container/40', isActive);
            btn.classList.toggle('text-on-surface-variant', !isActive);
            btn.setAttribute('aria-pressed', String(isActive));
        });
    }

    function setSystemStatus(text, isError) {
        var el = document.getElementById('system-status');
        if (!el) return;
        el.textContent = text;
        el.classList.remove('hidden', 'ok', 'err');
        el.classList.add(isError ? 'err' : 'ok');
        if (text) {
            setTimeout(function() {
                if (el.textContent === text) el.classList.add('hidden');
            }, 4000);
        }
    }

    function wireSystemUi() {
        if (systemWired) return;

        document.addEventListener('click', async function(e) {
            // Mode tabs
            var modeBtn = e.target.closest('#schedule-mode-tabs button');
            if (modeBtn) {
                scheduleMode = modeBtn.dataset.mode;
                updateScheduleModeUI();
                return;
            }

            // Preset buttons (relative mode)
            var preset = e.target.closest('.schedule-preset-btn');
            if (preset && Host.available) {
                var minutes = parseInt(preset.dataset.minutes);
                if (isNaN(minutes)) return;
                var actionEl = document.getElementById('scheduled-power-action');
                var action = actionEl ? actionEl.value : 'shutdown';
                await scheduleRelativeAction(minutes, action);
                return;
            }

            // Custom preset
            var custom = e.target.closest('.schedule-preset-custom');
            if (custom) {
                var fields = document.getElementById('schedule-custom-fields');
                if (fields) fields.classList.toggle('hidden');
                return;
            }

            // Schedule button (relative mode)
            var scheduleBtn = e.target.closest('#btn-schedule-action');
            if (scheduleBtn && Host.available) {
                var hoursEl = document.getElementById('schedule-custom-hours');
                var minsEl = document.getElementById('schedule-custom-minutes');
                var hours = hoursEl ? parseInt(hoursEl.value) || 0 : 0;
                var mins = minsEl ? parseInt(minsEl.value) || 0 : 0;
                var totalMins = hours * 60 + mins;

                // Check if custom fields are visible, otherwise check if a preset was selected
                var customFields = document.getElementById('schedule-custom-fields');
                if (!customFields || customFields.classList.contains('hidden')) {
                    // Use default of 30 if no preset active
                    var activePreset = document.querySelector('.schedule-preset-btn.bg-secondary-container\\/40');
                    totalMins = activePreset ? parseInt(activePreset.dataset.minutes) : 30;
                }

                var actionEl = document.getElementById('scheduled-power-action');
                var action = actionEl ? actionEl.value : 'shutdown';
                if (totalMins < 1) { setSystemStatus(t('invalidDuration'), true); return; }
                await scheduleRelativeAction(totalMins, action);
                return;
            }

            // Schedule button (daily mode)
            var dailyBtn = e.target.closest('#btn-schedule-daily');
            if (dailyBtn && Host.available) {
                var timeEl = document.getElementById('scheduled-power-time');
                var dailyActionEl = document.getElementById('scheduled-daily-action');
                var time = timeEl ? timeEl.value : '23:00';
                var action = dailyActionEl ? dailyActionEl.value : 'shutdown';
                if (!/^\d{2}:\d{2}$/.test(time)) { setSystemStatus(t('invalidTime'), true); return; }
                try {
                    var result = await Host.call('schedulePowerAction', { mode: 'daily', action: action, time: time });
                    applyScheduledPowerActionState(result);
                    setSystemStatus(t('scheduled'), false);
                } catch (err) {
                    setSystemStatus(err.message, true);
                }
                return;
            }

            // Cancel button
            var cancelBtn = e.target.closest('#btn-cancel-schedule');
            if (cancelBtn && Host.available) {
                try {
                    var result = await Host.call('cancelScheduledPowerAction');
                    applyScheduledPowerActionState(result);
                    setSystemStatus(t('cancelled'), false);
                } catch (err) {
                    setSystemStatus(err.message, true);
                }
                return;
            }

            var refresh = e.target.closest('#btn-refresh-startup-apps');
            if (refresh) { await loadStartupApps(true); return; }

            var add = e.target.closest('#btn-add-startup-app');
            if (add && Host.available) {
                add.disabled = true;
                try {
                    var picked = await Host.call('pickStartupExecutable');
                    if (picked && picked.path) {
                        await Host.call('addStartupApp', { path: picked.path });
                        setSystemStatus(t('added'), false);
                        await loadStartupApps(true);
                    }
                } catch (err) { setSystemStatus(t('addErr') + err.message, true); }
                finally { add.disabled = false; }
                return;
            }

            var startupToggle = e.target.closest('[data-toggle-startup-id]');
            if (startupToggle && Host.available) {
                startupToggle.disabled = true;
                try {
                    await Host.call('setStartupAppEnabled', {
                        id: startupToggle.dataset.toggleStartupId,
                        enabled: startupToggle.dataset.toggleStartupEnabled === 'true',
                    });
                    setSystemStatus(t('toggled'), false);
                    await loadStartupApps(true);
                } catch (err) { setSystemStatus(t('toggleErr') + err.message, true); }
                finally { startupToggle.disabled = false; }
                return;
            }

            var remove = e.target.closest('[data-remove-startup-id]');
            if (remove && Host.available) {
                remove.disabled = true;
                try {
                    await Host.call('removeStartupApp', { id: remove.dataset.removeStartupId });
                    setSystemStatus(t('removed'), false);
                    await loadStartupApps(true);
                } catch (err) { setSystemStatus(t('removeErr') + err.message, true); }
                finally { remove.disabled = false; }
            }
        });

        systemWired = true;
    }

    async function scheduleRelativeAction(minutes, action) {
        try {
            var result = await Host.call('schedulePowerAction', { mode: 'relative', action: action, delayMinutes: minutes });
            applyScheduledPowerActionState(result);
            setSystemStatus(t('scheduled'), false);
        } catch (err) {
            setSystemStatus(err.message, true);
        }
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

    document.addEventListener('click', (e) => {
        const seg = e.target.closest('#view-power .pm-seg');
        if (!seg) return;
        activatePowerPanel(seg.dataset.pm, true);
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

    // Collapsible side rail: icons remain, labels compact away.
    (function wireSidebarCollapse() {
        const KEY = 'volt.sidebarCollapsed';
        const nav = document.getElementById('side-nav');
        const collapseBtn = document.getElementById('btn-sidebar-toggle');
        const expandBtn = document.getElementById('btn-sidebar-expand');
        const icon = document.getElementById('sidebar-toggle-icon');
        if (!nav || !collapseBtn) return;

        function apply(collapsed) {
            document.body.classList.toggle('sidebar-collapsed', collapsed);
            nav.dataset.collapsed = collapsed ? 'true' : 'false';
            collapseBtn.setAttribute('aria-expanded', collapsed ? 'false' : 'true');
            const label = collapsed ? 'Espandi barra laterale' : 'Comprimi barra laterale';
            collapseBtn.title = label;
            collapseBtn.setAttribute('aria-label', label);
            if (expandBtn) {
                expandBtn.title = 'Espandi barra laterale';
                expandBtn.setAttribute('aria-label', 'Espandi barra laterale');
            }
            if (icon) icon.textContent = collapsed ? 'left_panel_open' : 'left_panel_close';
            try { localStorage.setItem(KEY, collapsed ? '1' : '0'); } catch (_) {}
            requestAnimationFrame(() => {
                const activeLink = document.querySelector('#nav-list a.text-secondary-container[data-view]') || getNavLinks()[0];
                if (activeLink) positionIndicator(activeLink);
            });
        }

        let collapsed = false;
        try { collapsed = localStorage.getItem(KEY) === '1'; } catch (_) {}
        apply(collapsed);

        collapseBtn.addEventListener('click', () => apply(true));
        expandBtn?.addEventListener('click', () => apply(false));
    })();

    async function boot() {
        if (!Host.available) return;
        try {
            const info = await Host.call('getSystemInfo');
            window.VoltSystemInfo = info;
            document.dispatchEvent(new CustomEvent('systeminfoloaded', { detail: info }));
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
        if (gamingModeActive) {
            monitoringDot.className = 'w-2 h-2 rounded-full bg-secondary-container animate-pulse shadow-[0_0_8px_#00f1fe]';
            monitoringLabel.dataset.i18n = 'nav_gaming_mode';
            monitoringLabel.textContent = I18n.t('nav_gaming_mode');
            return;
        }

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
            if (gamingModeActive) {
                try {
                    await setGamingMode(false);
                } catch (e) {
                    console.error('Failed to disable gaming mode', e);
                }
                return;
            }

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
        Host.on('gamingModeChanged', applyGamingModeState);
        Host.call('getGamingMode').then(applyGamingModeState).catch(() => {});

        Host.on('scheduledPowerActionChanged', function(state) {
            applyScheduledPowerActionState(state);
        });
        // Load initial schedule state
        Host.call('getScheduledPowerAction').then(function(state) {
            applyScheduledPowerActionState(state);
        }).catch(function() {});

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
        renderMonitoringState();
        setTimeout(removeLegacyAutoShutdownPanel, 0);
    });

    document.addEventListener('viewchange', (e) => {
        if (e.detail && e.detail.view === 'system') {
            mountSystemTab();
        }
        // Startup apps now live in Settings & Info.
        if (e.detail && e.detail.view === 'settings') {
            ensureSystemStyles();
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
