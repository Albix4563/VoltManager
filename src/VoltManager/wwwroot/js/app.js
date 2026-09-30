/**
 * Tab router + nav indicator animation + shared boot.
 * System tab: scheduled shutdown/restart/sleep and Windows startup apps.
 */
(function () {
    const navList = document.getElementById('nav-list');
    const navIndicator = document.getElementById('nav-indicator');
    const mainContent = document.getElementById('main-content');


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
        return window.I18n && I18n.feature ? I18n.feature('system', key, lang) : key;
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
        // Rect-based so the indicator stays aligned even with the new
        // grouped section-label rows between nav items.
        const parent = navIndicator.offsetParent || navIndicator.parentElement;
        if (!parent) return;
        const pr = parent.getBoundingClientRect();
        const lr = link.getBoundingClientRect();
        window.Volt.style.runtime.setMany(navIndicator, {
            top: (lr.top - pr.top) + 'px',
            height: lr.height + 'px',
        });
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
        Array.from(container.children).forEach((el, i) => window.Volt.style.runtime.set(el, '--vm-i', i));
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

    // Settings bootstrap is eager; only the advanced editor waits for navigation.
    const deferredPowerScripts = ['js/advanced.js'];
    const loadedScripts = new Map();
    function loadScriptOnce(src) {
        if (loadedScripts.has(src)) return loadedScripts.get(src);
        const loading = new Promise((resolve, reject) => {
            const s = document.createElement('script');
            s.src = window.VM_ASSET_URL(src);
            s.async = false;
            s.dataset.vmLazy = src;
            s.onload = resolve;
            s.onerror = () => {
                s.remove();
                loadedScripts.delete(src);
                reject(new Error('load failed: ' + src));
            };
            document.body.appendChild(s);
        });
        loadedScripts.set(src, loading);
        return loading;
    }
    function ensurePowerScripts() {
        return deferredPowerScripts.reduce(
            (p, src) => p.then(() => loadScriptOnce(src)), Promise.resolve());
    }
    function needsPowerScripts(name) {
        return name === 'power' || name === 'settings' || name === 'system'
            || name === 'power-plans' || name === 'automations' || name === 'system-tools';
    }
    // Reorg shell navigates without showView — still pull power scripts on demand.
    window.__voltEnsurePowerScripts = ensurePowerScripts;
    document.addEventListener('viewchange', (e) => {
        if (needsPowerScripts(e.detail && e.detail.view)) ensurePowerScripts().catch(() => {});
    });
    document.addEventListener('voltuiviewchanged', (e) => {
        if (needsPowerScripts(e.detail && e.detail.view)) ensurePowerScripts().catch(() => {});
    });

    let viewTransitionEpoch = 0;

    function cssTimeListMax(value) {
        return String(value || '')
            .split(',')
            .map(part => {
                const text = part.trim();
                const amount = Number.parseFloat(text);
                if (!Number.isFinite(amount)) return 0;
                return text.endsWith('ms') ? amount : amount * 1000;
            })
            .reduce((max, value) => Math.max(max, value), 0);
    }

    function showView(name) {
        const requestId = ++viewTransitionEpoch;
        const views = getViews();
        const next = views[name];
        const current = Object.values(views).find(el => !el.classList.contains('hidden'));
        const reduce = prefersReducedMotion();

        const swap = () => {
            if (requestId !== viewTransitionEpoch) return;
            Object.entries(views).forEach(([key, el]) => el.classList.toggle('hidden', key !== name));
            if (!reduce) staggerIn(next);
            const route = window.VoltViewLifecycle?.route?.() || { subviews: {} };
            window.VoltViewLifecycle?.transition({ view: name, subviews: route.subviews || {} });
            document.dispatchEvent(new CustomEvent('viewchange', { detail: { view: name } }));
        };

        // Ensure deferred tab code is present before the first paint of that tab.
        const afterScripts = () => {
            if (requestId !== viewTransitionEpoch) return;
            if (reduce || !current || current === next) {
                swap();
                return;
            }
            current.classList.add('vm-leaving');
            let settled = false;
            let fallback = 0;
            const finishLeave = () => {
                if (settled) return;
                settled = true;
                current.removeEventListener('animationend', onLeaveEnd);
                clearTimeout(fallback);
                current.classList.remove('vm-leaving');
                swap();
            };
            const onLeaveEnd = (event) => {
                if (event.target !== current || event.animationName !== 'vmLeave') return;
                finishLeave();
            };
            current.addEventListener('animationend', onLeaveEnd);

            const style = getComputedStyle(current);
            const animationMs = cssTimeListMax(style.animationDuration)
                + cssTimeListMax(style.animationDelay);
            fallback = setTimeout(finishLeave, Math.max(animationMs + 120, 450));
        };

        if (needsPowerScripts(name)) {
            ensurePowerScripts().then(afterScripts).catch((err) => {
                console.error(err);
                afterScripts();
            });
            return;
        }
        afterScripts();
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
        const route = window.VoltViewLifecycle?.route?.() || { view: 'power', subviews: {} };
        window.VoltViewLifecycle?.transition({
            view: route.view || 'power',
            subviews: { ...(route.subviews || {}), power: key }
        });
    }

    function mountSystemTab() {
        if (!navList || document.querySelector('#nav-list a[data-view="system"]')) return;
        // Place the System item under the CONTROL group (right after Power).
        const powerLi = document.querySelector('#nav-list a[data-view="power"]')?.parentElement;
        const item = document.createElement('li');
        item.innerHTML = '<a class="nav-item flex items-center gap-3 text-on-surface-variant font-medium px-4 py-3 opacity-80 hover:bg-white/5 hover:text-secondary-fixed rounded-lg active:scale-[0.98]" data-view="system" href="#"><span class="material-symbols-outlined">power_settings_new</span><span class="text-body-md system-nav-label"></span></a>';
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

    function systemViewHtml() {
        return '<div class="max-w-4xl mx-auto space-y-lg relative z-10 w-full">' +
            '<div class="mb-xl"><h2 class="text-headline-lg text-on-surface mb-xs system-title"></h2><p class="text-body-md text-on-surface-variant system-sub"></p></div>' +
            '<div class="grid grid-cols-12 gap-gutter">' +
            // Schedule panel — new: relative + daily dual-mode
            '<div class="col-span-12 lg:col-span-6 flex flex-col gap-gutter">' +
            '<div class="glass-panel rounded-xl p-lg space-y-md" id="schedule-panel"><h3 class="text-title-lg text-on-surface flex items-center gap-xs"><span class="material-symbols-outlined text-secondary-container">schedule</span><span class="system-schedule-title"></span></h3><p class="text-body-md text-on-surface-variant system-schedule-sub"></p>' +
            '<div class="pt-sm border-t border-white/10"><p class="text-label-md font-medium text-on-surface system-immediate-title"></p><p class="text-label-sm text-on-surface-variant mt-1 system-immediate-sub"></p><div class="flex items-center gap-sm mt-sm">' +
            '<button type="button" data-power-action="shutdown" class="system-icon-action system-icon-action--danger system-shutdown-now"><span class="material-symbols-outlined" aria-hidden="true">power_settings_new</span></button>' +
            '<button type="button" data-power-action="restart" class="system-icon-action system-icon-action--accent system-restart-now"><span class="material-symbols-outlined" aria-hidden="true">restart_alt</span></button>' +
            '</div></div>' +
            // Mode tabs — compact labels, same segmented language as subnav
            '<div class="schedule-mode-tabs" id="schedule-mode-tabs" role="tablist">' +
            '<button type="button" class="schedule-mode-tab schedule-mode-relative" data-mode="relative" role="tab" aria-selected="true"></button>' +
            '<button type="button" class="schedule-mode-tab schedule-mode-daily" data-mode="daily" role="tab" aria-selected="false"></button>' +
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
            '</div>' +
            // Startup apps panel
            '<div class="col-span-12">' +
            '<div class="glass-panel rounded-xl p-lg">' +
            '<div class="flex items-start justify-between gap-md mb-md">' +
            '<div><h3 class="text-title-lg text-on-surface flex items-center gap-xs"><span class="material-symbols-outlined text-secondary-container">apps</span><span class="system-startup-title"></span></h3><p class="text-body-md text-on-surface-variant mt-1 system-startup-sub"></p></div>' +
            '<button class="system-icon-action system-icon-action--neutral system-icon-action--edge system-startup-refresh" id="btn-refresh-startup-apps" type="button"><span class="material-symbols-outlined" aria-hidden="true">refresh</span></button>' +
            '</div>' +
            '<div class="grid grid-cols-2 gap-sm mb-md">' +
            '<div class="startup-summary-card" data-tone="on"><div class="startup-summary-icon"><span class="material-symbols-outlined text-[20px]">rocket_launch</span></div><div><p class="text-title-lg text-on-surface" id="startup-enabled-count">--</p><p class="text-label-sm text-on-surface-variant system-startup-enabled"></p></div></div>' +
            '<div class="startup-summary-card" data-tone="off"><div class="startup-summary-icon"><span class="material-symbols-outlined text-[20px]">pause_circle</span></div><div><p class="text-title-lg text-on-surface" id="startup-disabled-count">--</p><p class="text-label-sm text-on-surface-variant system-startup-disabled"></p></div></div>' +
            '</div>' +
            '<input id="startup-search" type="search" class="w-full bg-surface-container-low/50 text-on-surface border border-white/10 rounded-lg py-2.5 px-4 mb-md text-body-md focus:outline-none focus:border-secondary-container" />' +
            '<button class="btn-glow w-full bg-secondary-container text-on-secondary-container text-label-md font-bold px-5 py-3 rounded-lg flex items-center justify-center gap-sm" id="btn-add-startup-app" type="button"><span class="material-symbols-outlined text-[18px]">add</span><span class="system-startup-add"></span></button>' +
            '<div class="space-y-lg mt-md">' +
            '<div><h4 class="text-label-md uppercase tracking-wider text-secondary-container mb-sm system-startup-enabled"></h4><div class="space-y-sm" id="startup-enabled-list"></div></div>' +
            '<div><h4 class="text-label-md uppercase tracking-wider text-on-surface-variant mb-sm system-startup-disabled"></h4><div class="space-y-sm" id="startup-disabled-list"></div></div>' +
            '</div></div></div></div></div>';
    }

    function refreshSystemLabels() {
        document.querySelectorAll('.system-nav-label').forEach(el => el.textContent = t('nav'));
        const pairs = [
            ['.system-title','title'], ['.system-sub','sub'],
            ['.system-schedule-title','scheduleTitle'], ['.system-schedule-sub','scheduleSub'],
            ['.system-immediate-title','immediateTitle'], ['.system-immediate-sub','immediateSub'],
            ['.system-action','action'], ['.system-time','time'],
            ['.system-keepawake-title','keepAwakeTitle'], ['.system-keepawake-sub','keepAwakeSub'],
            ['.system-confirm','confirm'], ['.system-cancel','cancel'],
            ['.system-active-title','activeTitle'],
            ['.schedule-mode-relative','relative'], ['.schedule-mode-daily','daily'],
            ['.system-switch-on','on'], ['.system-switch-off','off'],
            ['.schedule-hours','hours'], ['.schedule-minutes','minutes'],
            ['.system-startup-title','startupTitle'], ['.system-startup-sub','startupSub'],
            ['.system-startup-add','add'],
            ['.system-startup-enabled','enabled'], ['.system-startup-disabled','disabled']
        ];
        pairs.forEach(([sel, key]) => document.querySelectorAll(sel).forEach(el => el.textContent = t(key)));
        const iconActions = [
            ['.system-shutdown-now', 'shutdownNow'],
            ['.system-restart-now', 'restartNow'],
            ['.system-startup-refresh', 'refresh']
        ];
        iconActions.forEach(([sel, key]) => document.querySelectorAll(sel).forEach(el => {
            const label = t(key);
            el.setAttribute('aria-label', label);
            el.setAttribute('data-tooltip', label);
        }));
        const startupSearch = document.getElementById('startup-search');
        if (startupSearch) {
            startupSearch.placeholder = t('searchStartup');
            startupSearch.setAttribute('aria-label', t('searchStartup'));
        }
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
            return '<button type="button" class="' + cls + '" data-minutes="' + p.mins + '" aria-pressed="false">' + esc(label) + '</button>';
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

            var action = String(state.action || '').toLowerCase();
            var mode = String(state.mode || '').toLowerCase();
            var actionName = t(action === 'sleep' ? 'sleep' : (action === 'restart' ? 'restart' : 'shutdown'));
            var activeText = document.getElementById('schedule-active-text');
            if (activeText) activeText.textContent = actionName;

            if (mode === 'relative' && state.executeAtUtc && state.remainingSeconds > 0) {
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
            } else if (mode === 'daily' && state.dailyTime) {
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
            btn.classList.toggle('active', isActive);
            btn.setAttribute('aria-selected', String(isActive));
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
            var immediateBtn = e.target.closest('[data-power-action]');
            if (immediateBtn && Host.available) {
                var immediateAction = immediateBtn.dataset.powerAction;
                var confirmKey = immediateAction === 'restart' ? 'confirmRestart' : 'confirmShutdown';
                if (!window.confirm(t(confirmKey))) return;
                immediateBtn.disabled = true;
                try {
                    await Host.call('executePowerAction', { action: immediateAction });
                } catch (err) {
                    setSystemStatus(err.message, true);
                } finally {
                    immediateBtn.disabled = false;
                }
                return;
            }

            // Mode tabs
            var modeBtn = e.target.closest('#schedule-mode-tabs button');
            if (modeBtn) {
                scheduleMode = modeBtn.dataset.mode;
                updateScheduleModeUI();
                return;
            }

            // Preset buttons (relative mode)
            var preset = e.target.closest('.schedule-preset-btn');
            if (preset) {
                document.querySelectorAll('.schedule-preset-btn').forEach(function(btn) {
                    btn.setAttribute('aria-pressed', String(btn === preset));
                });
                document.getElementById('schedule-custom-fields')?.classList.add('hidden');
                return;
            }

            // Custom preset
            var custom = e.target.closest('.schedule-preset-custom');
            if (custom) {
                document.querySelectorAll('.schedule-preset-btn').forEach(function(btn) { btn.setAttribute('aria-pressed', 'false'); });
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
                    var activePreset = document.querySelector('.schedule-preset-btn[aria-pressed="true"]');
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

        document.addEventListener('input', function(e) {
            if (e.target.id === 'startup-search') filterStartupApps(e.target.value);
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
            filterStartupApps(document.getElementById('startup-search')?.value || '');
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

    function filterStartupApps(query) {
        const normalized = String(query || '').trim().toLowerCase();
        document.querySelectorAll('#startup-enabled-list .startup-card, #startup-disabled-list .startup-card').forEach(card => {
            card.hidden = normalized !== '' && !card.textContent.toLowerCase().includes(normalized);
        });
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

    const _sidebarReposition = () => {
        const activeLink = document.querySelector('#nav-list a.text-secondary-container[data-view]') || getNavLinks()[0];
        if (activeLink) positionIndicator(activeLink);
    };
    (function wireSidebarCollapse() {
        const KEY = 'volt.sidebarCollapsed';
        const nav = document.getElementById('side-nav');
        const appMain = document.getElementById('app-main');
        const collapseBtn = document.getElementById('btn-sidebar-toggle');
        const expandBtn = document.getElementById('btn-sidebar-expand');
        const icon = document.getElementById('sidebar-toggle-icon');
        if (!nav || !collapseBtn) return;

        let collapsed = false;
        let transitioning = false;
        let targetCollapsed = false;

        function syncControls(nextCollapsed) {
            collapseBtn.setAttribute('aria-expanded', nextCollapsed ? 'false' : 'true');
            const label = nextCollapsed ? 'Espandi barra laterale' : 'Comprimi barra laterale';
            collapseBtn.title = label;
            collapseBtn.setAttribute('aria-label', label);
            if (expandBtn) {
                expandBtn.title = 'Espandi barra laterale';
                expandBtn.setAttribute('aria-label', 'Espandi barra laterale');
            }
            if (icon) icon.textContent = nextCollapsed ? 'left_panel_open' : 'left_panel_close';
        }

        function clearTransitionHints() {
            document.body.classList.remove('sidebar-resizing');
        }

        function persist(nextCollapsed) {
            try { localStorage.setItem(KEY, nextCollapsed ? '1' : '0'); } catch (_) {}
        }

        function applyStable(nextCollapsed, shouldPersist) {
            targetCollapsed = nextCollapsed;
            collapsed = nextCollapsed;
            transitioning = false;
            document.body.classList.remove('sidebar-expanding', 'sidebar-collapsing');
            document.body.classList.toggle('sidebar-collapsed', nextCollapsed);
            nav.dataset.collapsed = nextCollapsed ? 'true' : 'false';
            syncControls(nextCollapsed);
            clearTransitionHints();
            if (shouldPersist) persist(nextCollapsed);
            _sidebarReposition();
        }

        function finishTransition() {
            collapsed = targetCollapsed;
            transitioning = false;
            document.body.classList.remove('sidebar-expanding', 'sidebar-collapsing');
            clearTransitionHints();
            persist(collapsed);
            _sidebarReposition();
        }

        function transitionTo(nextCollapsed) {
            if (transitioning || collapsed === nextCollapsed) return;
            if (prefersReducedMotion() || window.matchMedia?.('(max-width: 900px)').matches) {
                applyStable(nextCollapsed, true);
                return;
            }

            transitioning = true;
            targetCollapsed = nextCollapsed;
            document.body.classList.remove(nextCollapsed ? 'sidebar-expanding' : 'sidebar-collapsing');
            document.body.classList.add(nextCollapsed ? 'sidebar-collapsing' : 'sidebar-expanding');
            document.body.classList.add('sidebar-resizing');

            // Expansion starts from the visually-hidden collapsed label state.
            // Flush that start frame once, then let CSS transition into the
            // delayed opacity/translate state without a timer.
            if (!nextCollapsed) void nav.offsetWidth;

            document.body.classList.toggle('sidebar-collapsed', nextCollapsed);
            nav.dataset.collapsed = nextCollapsed ? 'true' : 'false';
            syncControls(nextCollapsed);
        }

        nav.addEventListener('transitionend', (event) => {
            if (!transitioning || event.target !== nav || event.propertyName !== 'width') return;
            finishTransition();
        });

        try { collapsed = localStorage.getItem(KEY) === '1'; } catch (_) {}
        applyStable(collapsed, false);

        collapseBtn.addEventListener('click', () => transitionTo(true));
        expandBtn?.addEventListener('click', () => transitionTo(false));
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
            document.getElementById('sidebar-version').textContent = 'VOLT MANAGER v' + info.appVersion;
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
            monitoringDot.className = 'w-2 h-2 rounded-full bg-secondary-container animate-pulse shadow-[0_0_8px_var(--vm-accent)]';
            monitoringLabel.dataset.i18n = 'nav_gaming_mode';
            monitoringLabel.textContent = I18n.t('nav_gaming_mode');
            return;
        }

        // It's active only if master automation is on AND no manual override is active
        const isPaused = !settings.masterAutomationEnabled || !!settings.override;
        
        if (!isPaused) {
            monitoringDot.className = 'w-2 h-2 rounded-full bg-secondary-container animate-pulse shadow-[0_0_8px_var(--vm-accent)]';
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
    });

    document.addEventListener('viewchange', (e) => {
        if (e.detail && e.detail.view === 'system') {
            mountSystemTab();
                loadStartupApps(false);
        }
    });

    document.addEventListener('langchanged', () => {
        refreshSystemLabels();
        renderMonitoringState();
        if (startupLoaded) loadStartupApps(true);
    });

})();
