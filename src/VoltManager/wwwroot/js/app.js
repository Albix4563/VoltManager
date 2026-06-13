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

    function systemViewHtml() {
        return '<div class="max-w-5xl mx-auto space-y-lg relative z-10 w-full">' +
            '<div class="mb-xl"><h2 class="text-headline-lg text-on-surface mb-xs system-title"></h2><p class="text-body-md text-on-surface-variant system-sub"></p></div>' +
            '<div class="grid grid-cols-12 gap-gutter">' +
            '<div class="col-span-12 lg:col-span-5 flex flex-col gap-gutter">' +
            '<div class="glass-panel rounded-xl p-lg space-y-md"><h3 class="text-title-lg text-on-surface flex items-center gap-xs"><span class="material-symbols-outlined text-secondary-container">schedule</span><span class="system-schedule-title"></span></h3><p class="text-body-md text-on-surface-variant system-schedule-sub"></p>' +
            '<div class="flex items-center justify-between group pt-sm"><div><p class="text-body-md text-on-surface system-enable"></p><p class="text-label-sm text-on-surface-variant system-note"></p></div><div class="mini-toggle cursor-pointer" data-on="false" id="toggle-scheduled-power"><div class="mini-toggle-knob"></div></div></div>' +
            '<label class="flex items-center justify-between gap-md"><span class="text-label-sm text-on-surface-variant system-action"></span><select id="scheduled-power-action" class="bg-surface-container-low/50 text-secondary-container font-medium border border-white/10 rounded-lg py-2 px-3 text-body-md focus:outline-none focus:border-secondary-container"><option value="shutdown" class="sys-opt-shutdown"></option><option value="restart" class="sys-opt-restart"></option><option value="sleep" class="sys-opt-sleep"></option></select></label>' +
            '<label class="flex items-center justify-between gap-md"><span class="text-label-sm text-on-surface-variant system-time"></span><input id="scheduled-power-time" type="time" class="bg-surface-container-low/50 text-secondary-container font-medium border border-white/10 rounded-lg py-2 px-3 text-body-md focus:outline-none focus:border-secondary-container" /></label>' +
            '<p class="text-label-md text-on-surface-variant hidden" id="system-status"></p></div>' +
            '<div class="glass-panel rounded-xl p-lg"><h3 class="text-title-lg text-on-surface flex items-center gap-xs"><span class="material-symbols-outlined text-secondary-container">add_circle</span><span class="system-add-title"></span></h3><p class="text-body-md text-on-surface-variant mt-1 system-add-sub"></p><button class="btn-glow mt-md bg-secondary-container text-on-secondary-container text-label-md font-bold px-5 py-3 rounded-lg flex items-center gap-sm" id="btn-add-startup-app"><span class="material-symbols-outlined text-[18px]">add</span><span class="system-add-btn"></span></button></div>' +
            '</div><div class="col-span-12 lg:col-span-7"><div class="glass-panel rounded-xl p-lg"><div class="flex items-start justify-between gap-md mb-lg"><div><h3 class="text-title-lg text-on-surface flex items-center gap-xs"><span class="material-symbols-outlined text-secondary-container">apps</span><span class="system-startup-title"></span></h3><p class="text-body-md text-on-surface-variant mt-1 system-startup-sub"></p></div><button class="btn-ghost rounded-lg py-2 px-4 text-label-md flex items-center gap-xs" id="btn-refresh-startup-apps"><span class="material-symbols-outlined text-[18px]">refresh</span><span class="system-refresh"></span></button></div>' +
            '<div class="space-y-lg"><div><h4 class="text-label-md uppercase tracking-wider text-secondary-container mb-sm system-enabled"></h4><div class="space-y-sm" id="startup-enabled-list"></div></div><div><h4 class="text-label-md uppercase tracking-wider text-on-surface-variant mb-sm system-disabled"></h4><div class="space-y-sm" id="startup-disabled-list"></div></div></div>' +
            '</div></div></div></div>';
    }

    function refreshSystemLabels() {
        document.querySelectorAll('.system-nav-label').forEach(el => el.textContent = t('nav'));
        const pairs = [
            ['.system-title','title'], ['.system-sub','sub'], ['.system-schedule-title','scheduleTitle'], ['.system-schedule-sub','scheduleSub'],
            ['.system-enable','enable'], ['.system-note','note'], ['.system-action','action'], ['.system-time','time'], ['.system-add-title','addTitle'],
            ['.system-add-sub','addSub'], ['.system-add-btn','add'], ['.system-startup-title','startupTitle'], ['.system-startup-sub','startupSub'],
            ['.system-refresh','refresh'], ['.system-enabled','enabled'], ['.system-disabled','disabled']
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
        if (el) el.dataset.on = enabled ? 'true' : 'false';
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
        try {
            const data = await Host.call('getStartupApps');
            renderStartupList(enabledList, data.enabled || []);
            renderStartupList(disabledList, data.disabled || []);
            startupLoaded = true;
        } catch (err) {
            enabledList.innerHTML = errorRow(t('loadErr') + err.message);
            disabledList.innerHTML = '';
        }
    }

    function loadingRow() {
        return '<div class="text-body-md text-on-surface-variant opacity-70 py-3">' + esc(t('loading')) + '</div>';
    }

    function errorRow(text) {
        return '<div class="text-body-md text-on-surface-variant opacity-70 py-3">' + esc(text) + '</div>';
    }

    function renderStartupList(container, apps) {
        if (!apps.length) {
            container.innerHTML = '<div class="text-body-md text-on-surface-variant opacity-70 py-3">' + esc(t('empty')) + '</div>';
            return;
        }
        container.innerHTML = apps.map(app => {
            const managedBadge = app.isManaged ? '<span class="text-label-sm px-2 py-1 rounded bg-secondary-container/10 text-secondary-container border border-secondary-container/20">' + esc(t('managed')) + '</span>' : '';
            const toggleButton = '<button class="btn-ghost rounded-lg py-1.5 px-3 text-label-md" data-toggle-startup-id="' + escAttr(app.id) + '" data-toggle-startup-enabled="' + (app.enabled ? 'false' : 'true') + '" type="button">' + esc(app.enabled ? t('disableStartup') : t('enableStartup')) + '</button>';
            const removeButton = app.isManaged ? '<button class="btn-ghost rounded-lg py-1.5 px-3 text-label-md" data-remove-startup-id="' + escAttr(app.id) + '" type="button">' + esc(t('remove')) + '</button>' : '';
            const actionButtons = '<div class="flex items-center gap-xs shrink-0">' + toggleButton + removeButton + '</div>';
            return '<div class="rounded-lg border border-white/10 bg-surface-container-low/40 p-md flex flex-col gap-xs">' +
                '<div class="flex items-start justify-between gap-md"><div class="min-w-0"><div class="flex items-center gap-sm flex-wrap">' +
                '<p class="text-body-md text-on-surface font-medium truncate">' + esc(app.name || t('unknown')) + '</p>' + managedBadge +
                '</div><p class="text-label-sm text-on-surface-variant mt-1">' + esc(app.source || '') + '</p></div>' + actionButtons + '</div>' +
                '<p class="text-label-sm text-on-surface-variant opacity-70 break-all">' + esc(app.path || app.command || '') + '</p></div>';
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

    mountSystemTab();
    wireSystemUi();
    boot();

    document.addEventListener('settingsloaded', () => {
        mountSystemTab();
        applyScheduledUi();
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
        if (startupLoaded) loadStartupApps(true);
    });

    const observer = new MutationObserver(removeLegacyAutoShutdownPanel);
    observer.observe(document.body, { childList: true, subtree: true });
})();