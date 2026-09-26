(function () {
    const TYPES = ['clock', 'calendar', 'usage', 'temps', 'power', 'plans', 'launcher', 'apps', 'actions', 'brightness', 'processes', 'memory'];
    const SIZES = ['mini', 'medium', 'large'];
    const LAUNCHER_SIZES = ['mini', 'medium', 'large', 'bar', 'column'];
    const MAX_CUSTOM_PER_CATEGORY = 10;
    const PLAN_ORDER = ['powerSaver', 'balanced', 'performance'];
    const params = new URLSearchParams(location.search);
    const type = TYPES.includes(params.get('w')) ? params.get('w') : 'clock';
    const isLauncherWidget = type === 'launcher' || type === 'apps';
    const launcherCategory = type === 'apps' ? 'apps' : 'games';
    const validSizes = isLauncherWidget ? LAUNCHER_SIZES : SIZES;
    const size = validSizes.includes(params.get('s')) ? params.get('s') : 'medium';
    const root = document.getElementById('widget-root');
    let pinned = false;
    let switchingPlan = false;
    let keepAwake = false;
    let settingKeepAwake = false;
    let clockTimer = null;
    let dateTick = null;
    let pollTimer = null;
    let polling = false;
    let launcherItems = [];
    let launcherAllItems = [];
    let launcherLoaded = false;
    let launcherLoading = false;
    let launcherReloadPending = false;
    let gamingActive = false;
    let settingGaming = false;
    let scheduleState = null;
    let scheduleTimer = null;
    let purging = false;
    let brightnessTimer = null;
    let brightnessDragging = false;
    let resourceProfile = 'full';
    let resourceReducedEffects = false;
    let animationSetting = 'auto';
    let animationHardwareTier = null;
    let locale = (window.I18n && I18n.getLocale ? I18n.getLocale() : 'it-IT');
    document.documentElement.dataset.size = size;

    const labels = {
        clock: ['schedule', 'widget_clock'],
        calendar: ['calendar_month', 'widget_calendar'],
        usage: ['monitor_heart', 'widget_usage'],
        temps: ['device_thermostat', 'widget_temps'],
        power: ['bolt', 'widget_power'],
        plans: ['tune', 'widget_plans'],
        launcher: ['apps', 'widget_launcher'],
        apps: ['grid_view', 'widget_apps'],
        actions: ['bolt', 'widget_actions'],
        brightness: ['brightness_6', 'widget_brightness'],
        processes: ['list_alt', 'widget_processes'],
        memory: ['memory', 'widget_memory'],
    };

    function t(key, fallback) {
        if (!window.I18n || !I18n.t) return fallback || key;
        const value = I18n.t(key);
        return value === key ? (fallback || key) : value;
    }

    function shell(bodyHtml) {
        const meta = labels[type] || labels.clock;
        const keepAwakeBtn = type === 'plans'
            ? '    <button class="widget-action" id="widget-keep-awake" type="button" title="' + t('power_group_keepawake', 'Keep PC awake') + '" aria-label="' + t('power_group_keepawake', 'Keep PC awake') + '" aria-pressed="false"><span class="material-symbols-outlined">bedtime_off</span></button>'
            : '';
        const launcherChrome = isLauncherWidget
            ? '  <div class="launcher-drop-overlay" id="launcher-drop-overlay" aria-hidden="true"><span id="launcher-drop-text"></span></div>' +
              '  <div class="launcher-toast hidden" id="launcher-toast" role="status"></div>' +
              '  <button class="widget-resize-grip" id="widget-resize" type="button" title="' + esc(t('widget_resize', 'Resize')) + '" aria-label="' + esc(t('widget_resize', 'Resize')) + '"><span class="material-symbols-outlined" aria-hidden="true">south_east</span></button>'
            : '';
        root.innerHTML =
            '<article class="desktop-widget" data-size="' + size + '" data-widget-type="' + type + '">' +
            '  <header class="widget-header" id="widget-drag">' +
            '    <div class="widget-title"><span class="material-symbols-outlined">' + meta[0] + '</span><span data-i18n="' + meta[1] + '">' + t(meta[1], type) + '</span></div>' +
            '    <button class="widget-action" id="widget-pin" type="button" title="' + t('widget_pin', 'Pin') + '" aria-label="' + t('widget_pin', 'Pin') + '"><span class="material-symbols-outlined">push_pin</span></button>' +
            keepAwakeBtn +
            '    <button class="widget-action" id="widget-close" type="button" title="' + t('widget_close', 'Close') + '" aria-label="' + t('widget_close', 'Close') + '"><span class="material-symbols-outlined">close</span></button>' +
            '  </header>' +
            '  <section class="widget-body">' + bodyHtml + '</section>' +
            launcherChrome +
            '</article>';
        if (window.I18n && I18n.apply) I18n.apply();
        wireChrome();
    }

    function wireChrome() {
        const widget = root.querySelector ? root.querySelector('.desktop-widget') : null;
        document.getElementById('widget-drag')?.addEventListener('pointerdown', (e) => {
            if (e.target.closest('button')) return;
            Host.call('beginWidgetDrag').catch(() => {});
        });
        document.getElementById('widget-resize')?.addEventListener('pointerdown', (e) => {
            e.preventDefault();
            e.stopPropagation();
            Host.call('beginWidgetResize').catch(() => {});
        });
        widget?.addEventListener('pointerdown', (e) => {
            if (!isLauncherWidget || widget.dataset.layout === 'grid' || e.target.closest('button')) return;
            Host.call('beginWidgetDrag').catch(() => {});
        });
        document.getElementById('widget-pin')?.addEventListener('click', () => {
            pinned = !pinned;
            reflectPin();
            Host.call('setWidgetTopmost', { topmost: pinned }).catch(() => {
                pinned = !pinned;
                reflectPin();
            });
        });
        const kwBtn = document.getElementById('widget-keep-awake');
        if (kwBtn) {
            kwBtn.addEventListener('click', () => {
                if (settingKeepAwake) return;
                settingKeepAwake = true;
                const targetState = !keepAwake;
                keepAwake = targetState;
                reflectKeepAwake();
                Host.call('setKeepAwake', { enabled: targetState }).then((state) => {
                    keepAwake = !!(state && state.enabled);
                    reflectKeepAwake();
                }).catch(() => {
                    keepAwake = !targetState;
                    reflectKeepAwake();
                }).finally(() => {
                    settingKeepAwake = false;
                });
            });
            if (Host.available) {
                Host.call('getKeepAwakeState').then((state) => {
                    keepAwake = !!(state && state.enabled);
                    reflectKeepAwake();
                }).catch(() => {});
            }
        }
        document.getElementById('widget-close')?.addEventListener('click', () => {
            Host.call('closeWidget').catch(() => {});
        });
        wireDropSafety(widget);
        if (isLauncherWidget) observeLauncherLayout(widget);
    }

    function reflectPin() {
        const btn = document.getElementById('widget-pin');
        if (!btn) return;
        btn.dataset.on = pinned ? 'true' : 'false';
        btn.setAttribute('aria-pressed', pinned ? 'true' : 'false');
    }

    function reflectKeepAwake() {
        for (const id of ['widget-keep-awake', 'action-awake']) {
            const btn = document.getElementById(id);
            if (!btn) continue;
            btn.dataset.on = keepAwake ? 'true' : 'false';
            btn.setAttribute('aria-pressed', keepAwake ? 'true' : 'false');
        }
    }

    function pct(value) {
        value = Number(value);
        if (!Number.isFinite(value)) return 0;
        return Math.max(0, Math.min(100, value));
    }

    function temp(value) {
        return value == null ? '--' : Math.round(value) + '\u00b0C';
    }

    function planName(plan) {
        const key = {
            powerSaver: 'dash_plan_saver',
            balanced: 'dash_plan_balanced',
            performance: 'dash_plan_performance',
        }[plan];
        return key ? t(key, plan) : (plan || '--');
    }

    function startClock() {
        shell('<div class="widget-value" id="clock-time">--:--</div>' + (size === 'mini' ? '' : '<div class="widget-muted" id="clock-date">--</div>'));
        const timeEl = document.getElementById('clock-time');
        const dateEl = document.getElementById('clock-date');
        const timeFormat = new Intl.DateTimeFormat(locale, { hour: '2-digit', minute: '2-digit' });
        const dateFormat = new Intl.DateTimeFormat(locale, { weekday: 'long', day: '2-digit', month: 'long' });
        dateTick = () => {
            const now = new Date();
            timeEl.textContent = timeFormat.format(now);
            if (dateEl) dateEl.textContent = dateFormat.format(now);
        };
        scheduleDateTick();
    }

    function scheduleDateTick() {
        if (clockTimer != null) clearTimeout(clockTimer);
        clockTimer = null;
        if (!dateTick || document.hidden) return;
        dateTick();
        const now = new Date();
        const next = type === 'calendar'
            ? new Date(now.getFullYear(), now.getMonth(), now.getDate() + 1).getTime()
            : (Math.floor(now.getTime() / 60000) + 1) * 60000;
        clockTimer = setTimeout(scheduleDateTick, next - now.getTime());
    }

    function startCalendar() {
        if (size === 'mini') {
            shell('<div class="calendar-mini"><div class="widget-muted" id="calendar-mini-weekday">--</div><div class="calendar-mini-day" id="calendar-mini-day">--</div><div class="widget-muted" id="calendar-mini-month">--</div></div>');
            dateTick = renderCalendarMini;
            scheduleDateTick();
            return;
        }
        shell('<div class="widget-muted" id="calendar-title" style="margin-bottom:10px"></div><div class="calendar-head" id="calendar-head"></div><div class="calendar-grid" id="calendar-grid"></div>');
        dateTick = renderCalendar;
        scheduleDateTick();
    }

    function renderCalendarMini() {
        const now = new Date();
        document.getElementById('calendar-mini-weekday').textContent = new Intl.DateTimeFormat(locale, { weekday: 'long' }).format(now);
        document.getElementById('calendar-mini-day').textContent = new Intl.DateTimeFormat(locale, { day: '2-digit' }).format(now);
        document.getElementById('calendar-mini-month').textContent = new Intl.DateTimeFormat(locale, { month: 'long', year: 'numeric' }).format(now);
    }

    function renderCalendar() {
        const now = new Date();
        const title = document.getElementById('calendar-title');
        const head = document.getElementById('calendar-head');
        const grid = document.getElementById('calendar-grid');
        title.textContent = new Intl.DateTimeFormat(locale, { month: 'long', year: 'numeric' }).format(now);

        const monday = new Date(2026, 0, 5);
        head.innerHTML = '';
        for (let i = 0; i < 7; i++) {
            const d = new Date(monday);
            d.setDate(monday.getDate() + i);
            const span = document.createElement('span');
            span.textContent = new Intl.DateTimeFormat(locale, { weekday: 'short' }).format(d).slice(0, 2);
            head.appendChild(span);
        }

        const first = new Date(now.getFullYear(), now.getMonth(), 1);
        const offset = (first.getDay() + 6) % 7;
        const start = new Date(first);
        start.setDate(first.getDate() - offset);
        grid.innerHTML = '';
        for (let i = 0; i < 42; i++) {
            const d = new Date(start);
            d.setDate(start.getDate() + i);
            const cell = document.createElement('div');
            cell.className = 'calendar-day';
            if (d.getMonth() !== now.getMonth()) cell.classList.add('is-muted');
            if (d.toDateString() === now.toDateString()) cell.classList.add('is-today');
            cell.textContent = d.getDate();
            grid.appendChild(cell);
        }
    }

    function startUsage() {
        shell(
            '<div class="widget-grid">' +
            statHtml('CPU', 'usage-cpu') +
            statHtml('RAM', 'usage-ram') +
            (size === 'mini' ? '' : statHtml('GPU', 'usage-gpu') + statHtml('Disk', 'usage-disk')) +
            '</div>');
        Host.on('metrics', renderUsage);
    }

    function statHtml(label, id) {
        return '<div class="widget-stat"><label>' + label + '</label><strong id="' + id + '">--</strong><div class="widget-bar"><span id="' + id + '-bar"></span></div></div>';
    }

    function renderUsage(m) {
        setStat('usage-cpu', m.cpu);
        setStat('usage-gpu', m.gpuAvailable ? m.gpu : null);
        setStat('usage-ram', m.ramPct);
        setStat('usage-disk', m.disk);
    }

    function setStat(id, value) {
        const valueEl = document.getElementById(id);
        const bar = document.getElementById(id + '-bar');
        if (!valueEl || !bar) return;
        if (value == null) {
            valueEl.textContent = 'N/D';
            bar.style.width = '0%';
            return;
        }
        const v = pct(value);
        valueEl.textContent = Math.round(v) + '%';
        bar.style.width = v + '%';
    }

    function startTemps() {
        shell(
            '<div class="temp-row"><span class="widget-muted">CPU</span><strong id="temp-cpu">--</strong></div>' +
            '<div class="temp-row"><span class="widget-muted">GPU</span><strong id="temp-gpu">--</strong></div>');
        Host.on('metrics', (m) => {
            document.getElementById('temp-cpu').textContent = temp(m.cpuTemp);
            document.getElementById('temp-gpu').textContent = temp(m.gpuTemp);
        });
    }

    function startPower() {
        shell(
            '<div class="power-row"><span class="widget-muted" data-i18n="widget_power_now">Power</span><strong id="power-watts">--</strong></div>' +
            '<div class="power-row" id="power-battery-row"><span class="widget-muted" data-i18n="widget_battery">Battery</span><strong id="power-battery">--</strong></div>' +
            (size === 'mini' ? '' :
                '<div class="power-row"><span class="widget-muted" data-i18n="widget_plan">Plan</span><strong id="power-plan">--</strong></div>' +
                '<div class="power-row"><span class="widget-muted" data-i18n="widget_cpu_auto">CPU avg</span><strong id="power-auto-cpu">--</strong></div>' +
                '<div class="power-row"><span class="widget-muted" data-i18n="widget_sample_interval">Sample</span><strong id="power-auto-sample">--</strong></div>'));
        if (window.I18n && I18n.apply) I18n.apply();
        syncPolling();
        if (size !== 'mini') {
            pollPlan();
            pollCpuAutomation();
        }
        Host.on('activePlanChanged', (data) => renderPlan(data && data.plan));
        Host.on('cpuAutomationStateChanged', renderCpuAutomation);
    }

    async function pollPower() {
        renderPower(await Host.call('getBatteryPower'));
    }

    // One poller per data widget; the cadence follows the host resource profile.
    async function runPoll() {
        const poll = POLLERS[type];
        if (!poll || document.hidden || polling) return;
        polling = true;
        try { await poll(); } catch { }
        finally { polling = false; }
    }

    function syncPolling() {
        if (pollTimer != null) clearInterval(pollTimer);
        pollTimer = null;
        if (!POLLERS[type] || document.hidden) return;
        runPoll();
        pollTimer = setInterval(runPoll,
            resourceProfile === 'critical' ? 15000 :
            (resourceProfile === 'gaming' || resourceProfile === 'workload') ? 10000 : 5000);
    }

    async function pollPlan() {
        try {
            const plan = await Host.call('getActivePlan');
            renderPlan(plan && plan.planId);
        } catch { }
    }

    async function pollCpuAutomation() {
        try {
            const state = await Host.call('getCpuAutomationState');
            renderCpuAutomation(state);
        } catch { }
    }

    function renderPower(state) {
        const watts = document.getElementById('power-watts');
        const battery = document.getElementById('power-battery');
        const batteryRow = document.getElementById('power-battery-row');
        if (batteryRow) {
            const noBattery = state?.message === 'no_battery';
            batteryRow.classList.toggle('hidden', noBattery);
            batteryRow.style.display = noBattery ? 'none' : '';
            batteryRow.setAttribute('aria-hidden', noBattery ? 'true' : 'false');
        }
        if (!state || !state.available) {
            watts.textContent = '--';
            battery.textContent = 'AC';
            return;
        }
        watts.textContent = state.powerWatts == null ? '--' : (state.powerWatts > 0 ? '+' : '') + Number(state.powerWatts).toFixed(1) + ' W';
        // Prefer % + short ETA when the host has a stable runtime estimate.
        if (state.batteryPercent != null) {
            let label = state.batteryPercent + '%';
            if ((state.timeKind === 'toEmpty' || state.timeKind === 'toFull') && state.minutesRemaining != null) {
                const m = state.minutesRemaining;
                const h = Math.floor(m / 60);
                const mm = m % 60;
                label += h > 0 ? ' · ' + h + 'h' + mm + 'm' : ' · ' + mm + 'm';
            }
            battery.textContent = label;
        } else {
            battery.textContent = '--';
        }
    }

    function renderPlan(plan) {
        const planEl = document.getElementById('power-plan');
        if (planEl) planEl.textContent = planName(plan);
    }

    function renderCpuAutomation(state) {
        const cpuEl = document.getElementById('power-auto-cpu');
        const sampleEl = document.getElementById('power-auto-sample');
        if (cpuEl) {
            const avg = Number(state && state.averageCpu);
            cpuEl.textContent = Number.isFinite(avg) ? Math.round(avg) + '%' : '--';
        }
        if (sampleEl) {
            const seconds = Number(state && state.sampleIntervalSeconds);
            sampleEl.textContent = Number.isFinite(seconds) ? Math.round(seconds) + 's' : '--';
        }
    }

    function startPlans() {
        const short = size === 'mini';
        const options = [
            { id: 'powerSaver', icon: 'eco', key: 'dash_plan_saver', short: 'Eco' },
            { id: 'balanced', icon: 'balance', key: 'dash_plan_balanced', short: 'Bal' },
            { id: 'performance', icon: 'speed', key: 'dash_plan_performance', short: 'Perf' },
        ];
        shell(
            '<div class="plan-selector" role="radiogroup" aria-label="' + t('widget_plans', 'Power plans') + '">' +
            '<div class="plan-pill" id="plan-pill" aria-hidden="true"></div>' +
            options.map(function (opt) {
                const label = short ? opt.short : t(opt.key, opt.id);
                const i18nAttr = short ? '' : ' data-i18n="' + opt.key + '"';
                return '<button class="plan-option" type="button" role="radio" aria-checked="false" tabindex="-1" data-plan="' + opt.id + '">' +
                    '<span class="material-symbols-outlined">' + opt.icon + '</span>' +
                    '<span class="plan-option-label"' + i18nAttr + '>' + label + '</span>' +
                    '</button>';
            }).join('') +
            '</div>');
        if (window.I18n && I18n.apply && !short) I18n.apply();
        document.querySelectorAll('.plan-option').forEach(function (btn) {
            btn.addEventListener('click', function () {
                selectPlan(btn.dataset.plan);
            });
        });

        const selector = document.querySelector('.plan-selector');
        if (selector) {
            selector.addEventListener('keydown', function (e) {
                const btns = Array.from(document.querySelectorAll('.plan-option'));
                const activeIndex = btns.findIndex(btn => btn.getAttribute('aria-checked') === 'true');
                if (activeIndex < 0) return;

                let nextIndex = activeIndex;
                if (e.key === 'ArrowRight' || e.key === 'ArrowDown') {
                    nextIndex = (activeIndex + 1) % btns.length;
                    e.preventDefault();
                } else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') {
                    nextIndex = (activeIndex - 1 + btns.length) % btns.length;
                    e.preventDefault();
                }

                if (nextIndex !== activeIndex) {
                    const targetBtn = btns[nextIndex];
                    selectPlan(targetBtn.dataset.plan);
                    targetBtn.focus();
                }
            });
        }

        pollPlanSelector();
    }

    async function pollPlanSelector() {
        try {
            const plan = await Host.call('getActivePlan');
            reflectPlanSelector(plan && plan.planId);
        } catch { }
    }

    function reflectPlanSelector(plan) {
        const index = PLAN_ORDER.indexOf(plan);
        const pill = document.getElementById('plan-pill');
        const btns = document.querySelectorAll('.plan-option');
        btns.forEach(function (btn, i) {
            const on = i === index;
            btn.classList.toggle('is-active', on);
            btn.setAttribute('aria-checked', on ? 'true' : 'false');
            btn.setAttribute('tabindex', on ? '0' : '-1');
        });
        if (index < 0 && btns.length > 0) {
            btns[0].setAttribute('tabindex', '0');
        }
        if (!pill) return;
        if (index < 0) {
            pill.style.opacity = '0';
            return;
        }
        pill.style.opacity = '1';
        pill.style.transform = 'translateX(' + (index * 100) + '%)';
    }

    async function selectPlan(plan) {
        if (!plan || switchingPlan) return;
        switchingPlan = true;
        reflectPlanSelector(plan);
        try {
            const res = await Host.call('setManualOverride', { plan: plan });
            if (!res || !res.success) await pollPlanSelector();
        } catch {
            await pollPlanSelector();
        } finally {
            switchingPlan = false;
        }
    }

    function esc(value) {
        return String(value == null ? '' : value).replace(/[&<>"']/g, (c) => ({
            '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
        }[c]));
    }

    function gb(value) {
        value = Number(value);
        return Number.isFinite(value) ? value.toFixed(value >= 10 ? 0 : 1) + ' GB' : '--';
    }

    function mb(value) {
        value = Number(value);
        if (!Number.isFinite(value)) return '--';
        return value >= 1024 ? (value / 1024).toFixed(1) + ' GB' : Math.round(value) + ' MB';
    }

    function setText(id, text) {
        const el = document.getElementById(id);
        if (el) el.textContent = text;
    }

    // Transient feedback on a button: launching -> launched | error, then back to idle.
    function flashState(btn, ok) {
        if (!btn) return;
        btn.dataset.state = ok ? 'launched' : 'error';
        setTimeout(() => {
            if (btn.dataset.state !== 'launching') delete btn.dataset.state;
        }, ok ? 1400 : 2400);
    }

    // ---- Launcher -------------------------------------------------------

    function launcherCategoryOf(item) {
        return item && item.category === 'apps' ? 'apps' : 'games';
    }

    function customCategoryCount() {
        return launcherAllItems.filter(item => item && item.source === 'custom' && launcherCategoryOf(item) === launcherCategory).length;
    }

    function launcherDropText(full) {
        const count = Math.min(MAX_CUSTOM_PER_CATEGORY, customCategoryCount());
        const key = full ? 'widget_drop_full' : 'widget_drop_add';
        const fallback = full ? 'List full ({count}/10)' : 'Drop to add ({count}/10)';
        return t(key, fallback).replace('{count}', String(count));
    }

    function setDropOverlay(visible) {
        if (!isLauncherWidget) return;
        const overlay = document.getElementById('launcher-drop-overlay');
        const text = document.getElementById('launcher-drop-text');
        if (!overlay || !text) return;
        const full = customCategoryCount() >= MAX_CUSTOM_PER_CATEGORY;
        text.textContent = launcherDropText(full);
        overlay.classList.toggle('is-full', full);
        overlay.classList.toggle('is-visible', visible);
        overlay.setAttribute('aria-hidden', visible ? 'false' : 'true');
    }

    function showLauncherToast(result) {
        const toast = document.getElementById('launcher-toast');
        if (!toast || !result) return;
        const parts = [];
        const added = Number(result.added) || 0;
        const duplicates = Number(result.duplicates) || 0;
        const rejected = Number(result.rejected) || 0;
        if (added > 0) parts.push(t('widget_drop_added', 'Added {count}').replace('{count}', String(added)));
        if (duplicates > 0) parts.push(t('widget_drop_duplicate', 'Already present: {count}').replace('{count}', String(duplicates)));
        if (rejected > 0) parts.push(t('widget_drop_rejected', 'Unsupported file: {count}').replace('{count}', String(rejected)));
        if (result.limitReached) parts.push(t('widget_drop_full', 'List full ({count}/10)').replace('{count}', String(MAX_CUSTOM_PER_CATEGORY)));
        if (!parts.length) return;
        toast.textContent = parts.join(' · ');
        toast.classList.remove('hidden');
        toast.classList.add('is-visible');
        clearTimeout(showLauncherToast.timer);
        clearTimeout(showLauncherToast.hideTimer);
        showLauncherToast.timer = setTimeout(() => {
            toast.classList.remove('is-visible');
            showLauncherToast.hideTimer = setTimeout(() => {
                toast.classList.add('hidden');
                showLauncherToast.hideTimer = null;
            }, 180);
        }, 2200);
    }

    // A file dropped anywhere outside the widget must never navigate the WebView.
    document.addEventListener('dragover', e => e.preventDefault());
    document.addEventListener('drop', e => e.preventDefault());

    function wireDropSafety(widget) {
        if (!widget) return;
        let dragDepth = 0;
        const hasFiles = dataTransfer => {
            if (!dataTransfer) return false;
            if (dataTransfer.files && dataTransfer.files.length) return true;
            return Array.from(dataTransfer.types || []).includes('Files');
        };
        widget.addEventListener('dragenter', e => {
            if (!hasFiles(e.dataTransfer)) return;
            if (!isLauncherWidget) return;
            e.preventDefault();
            dragDepth++;
            setDropOverlay(true);
        });
        widget.addEventListener('dragover', e => {
            e.preventDefault();
            if (isLauncherWidget && hasFiles(e.dataTransfer)) setDropOverlay(true);
        });
        widget.addEventListener('dragleave', e => {
            if (!isLauncherWidget || !hasFiles(e.dataTransfer)) return;
            dragDepth = Math.max(0, dragDepth - 1);
            if (dragDepth === 0) setDropOverlay(false);
        });
        widget.addEventListener('drop', e => {
            e.preventDefault();
            dragDepth = 0;
            setDropOverlay(false);
            if (!isLauncherWidget) return;
            const files = e.dataTransfer && e.dataTransfer.files;
            if (!files || !files.length) return;
            const webview = window.chrome && window.chrome.webview;
            if (!webview || typeof webview.postMessageWithAdditionalObjects !== 'function') {
                showLauncherToast({ rejected: files.length });
                return;
            }
            webview.postMessageWithAdditionalObjects({ kind: 'launcherDropFiles' }, files);
        });
    }

    function observeLauncherLayout(widget) {
        if (!widget) return;
        let current = '';
        const apply = (width, height) => {
            let next = 'grid';
            if (width >= height * 2.2) next = 'horizontal';
            else if (height >= width * 2.2) next = 'vertical';
            if (next === current) return;
            current = next;
            widget.dataset.layout = next;
            renderLaunchers();
        };
        widget.dataset.layout = size === 'bar' ? 'horizontal' : size === 'column' ? 'vertical' : 'grid';
        current = widget.dataset.layout;
        if (typeof ResizeObserver === 'function') {
            const observer = new ResizeObserver(entries => {
                const rect = entries[0] && entries[0].contentRect;
                if (rect) apply(rect.width, rect.height);
            });
            observer.observe(widget);
        }
    }

    function startLauncher() {
        const emptyKey = type === 'apps' ? 'widget_apps_empty' : 'widget_launcher_empty';
        shell('<div class="launcher-grid" id="launcher-grid" role="list"></div>' +
            '<div class="launcher-empty hidden" id="launcher-empty">' +
            '<span class="widget-muted" data-i18n="' + emptyKey + '">' + t(emptyKey, 'Drag shortcuts here to add them') + '</span>' +
            '<button class="widget-button" id="launcher-open" type="button"><span class="material-symbols-outlined">open_in_new</span><span data-i18n="widget_launcher_manage">Manage apps</span></button>' +
            '</div>');
        document.getElementById('launcher-grid').addEventListener('click', (e) => {
            const tile = e.target && e.target.closest ? e.target.closest('[data-launch-id]') : null;
            if (tile) launchTile(tile);
        });
        document.getElementById('launcher-grid').addEventListener('contextmenu', async (e) => {
            const tile = e.target && e.target.closest ? e.target.closest('[data-launch-id]') : null;
            if (!tile) return;
            const item = launcherItems.find(entry => entry.id === tile.dataset.launchId);
            if (!item) return;
            e.preventDefault();
            const name = item.name || '';
            if (item.source === 'custom') {
                if (!window.confirm(t('widget_launcher_remove_confirm', 'Remove {name}?').replace('{name}', name))) return;
                await Host.call('removeCustomLauncher', { id: item.id }).catch(() => {});
            } else {
                if (!window.confirm(t('widget_launcher_hide_confirm', 'Hide {name} from this widget?').replace('{name}', name))) return;
                await Host.call('setLauncherHidden', { id: item.id, hidden: true }).catch(() => {});
            }
        });
        document.getElementById('launcher-open').addEventListener('click', () => {
            Host.call('showMainWindow').catch(() => {});
        });
        if (launcherLoaded) renderLaunchers();
        else loadLaunchers();
    }

    async function loadLaunchers() {
        if (launcherLoading) {
            launcherReloadPending = true;
            return;
        }
        launcherLoading = true;
        try {
            const list = await Host.call('getLaunchers');
            launcherAllItems = Array.isArray(list) ? list.filter(item => item && item.id) : [];
            launcherItems = launcherAllItems.filter(item => !item.hidden && launcherCategoryOf(item) === launcherCategory);
        } catch {
            launcherAllItems = [];
            launcherItems = [];
        } finally {
            launcherLoading = false;
            launcherLoaded = true;
        }
        renderLaunchers();
        if (launcherReloadPending) {
            launcherReloadPending = false;
            loadLaunchers();
        }
    }

    function launcherIcon(item) {
        const url = typeof item.iconDataUrl === 'string' && item.iconDataUrl.startsWith('data:image/png;base64,')
            ? item.iconDataUrl : '';
        if (url) return '<img src="' + esc(url) + '" alt="" draggable="false">';
        return '<span class="material-symbols-outlined">' + (item.source === 'custom' ? 'apps' : 'sports_esports') + '</span>';
    }

    function renderLaunchers() {
        const grid = document.getElementById('launcher-grid');
        const empty = document.getElementById('launcher-empty');
        if (!grid || !empty) return;
        const widget = grid.closest('.desktop-widget');
        const showNames = size !== 'mini' && (!widget || widget.dataset.layout === 'grid');
        grid.innerHTML = launcherItems.map((item) => {
            const available = item.available !== false;
            const name = item.name || '';
            const label = available
                ? t('widget_launcher_launch', 'Open {name}').replace('{name}', name)
                : t('widget_launcher_missing', '{name} is no longer installed').replace('{name}', name);
            return '<button class="launcher-tile" type="button" role="listitem" data-launch-id="' + esc(item.id) + '"' +
                ' title="' + esc(label) + '" aria-label="' + esc(label) + '"' + (available ? '' : ' disabled') + '>' +
                '<span class="launcher-icon">' + launcherIcon(item) + '</span>' +
                (showNames ? '<span class="launcher-name">' + esc(name) + '</span>' : '') +
                '</button>';
        }).join('');
        const isEmpty = launcherLoaded && launcherItems.length === 0;
        grid.classList.toggle('hidden', isEmpty);
        empty.classList.toggle('hidden', !isEmpty);
    }

    async function launchTile(tile) {
        if (tile.dataset.state === 'launching') return;
        tile.dataset.state = 'launching';
        let ok = false;
        try {
            const result = await Host.call('launchApp', { id: tile.dataset.launchId });
            ok = !!(result && result.success);
            if (!ok && result && (result.error === 'unknown' || result.error === 'missing')) loadLaunchers();
        } catch { }
        flashState(tile, ok);
    }

    // ---- Quick actions --------------------------------------------------

    const TIMER_PRESETS = [30, 60, 120];

    function actionTile(id, icon, key, fallback, toggle) {
        const label = esc(t(key, fallback));
        return '<button class="action-tile" id="action-' + id + '" type="button" data-action="' + id + '"' +
            (toggle ? ' aria-pressed="false"' : '') + ' title="' + label + '" aria-label="' + label + '">' +
            '<span class="material-symbols-outlined">' + icon + '</span>' +
            (size === 'mini' ? '' : '<span class="action-label" data-i18n="' + key + '">' + label + '</span>') +
            '</button>';
    }

    function startActions() {
        const timer = size === 'mini' ? '' :
            '<div class="action-timer" id="action-timer" data-on="false">' +
            '<span class="material-symbols-outlined" aria-hidden="true">timer</span>' +
            '<span class="action-timer-label" id="action-timer-label">' + esc(t('widget_action_timer', 'Shut down in')) + '</span>' +
            '<div class="action-timer-controls" id="action-timer-controls"></div>' +
            '</div>';
        shell('<div class="action-grid" id="action-grid">' +
            actionTile('purge', 'cleaning_services', 'widget_action_purge', 'Free RAM', false) +
            actionTile('awake', 'bedtime_off', 'power_group_keepawake', 'Keep PC awake', true) +
            actionTile('gaming', 'sports_esports', 'widget_action_gaming', 'Gaming mode', true) +
            actionTile('open', 'open_in_new', 'widget_action_open', 'Open VoltManager', false) +
            timer + '</div>');
        document.getElementById('action-grid').addEventListener('click', (e) => {
            const target = e.target && e.target.closest ? e.target.closest('[data-action],[data-timer]') : null;
            if (!target) return;
            if (target.dataset.timer) runTimerAction(target.dataset.timer);
            else runAction(target.dataset.action);
        });
        reflectKeepAwake();
        reflectGaming();
        renderSchedule();
        if (!Host.available) return;
        Host.call('getKeepAwakeState').then((state) => {
            keepAwake = !!(state && state.enabled);
            reflectKeepAwake();
        }).catch(() => {});
        Host.call('getGamingMode').then(applyGamingState).catch(() => {});
        if (size !== 'mini') Host.call('getScheduledPowerAction').then(applySchedule).catch(() => {});
    }

    async function runAction(action) {
        if (action === 'open') {
            Host.call('showMainWindow').catch(() => {});
        } else if (action === 'purge') {
            await purgeMemory('action-purge');
        } else if (action === 'awake') {
            if (settingKeepAwake) return;
            settingKeepAwake = true;
            const target = !keepAwake;
            keepAwake = target;
            reflectKeepAwake();
            try {
                const state = await Host.call('setKeepAwake', { enabled: target });
                keepAwake = !!(state && state.enabled);
            } catch {
                keepAwake = !target;
            } finally {
                settingKeepAwake = false;
                reflectKeepAwake();
            }
        } else if (action === 'gaming') {
            if (settingGaming) return;
            settingGaming = true;
            const target = !gamingActive;
            gamingActive = target;
            reflectGaming();
            try {
                await Host.call('setGamingMode', { enabled: target });
                applyGamingState(await Host.call('getGamingMode'));
            } catch {
                gamingActive = !target;
                reflectGaming();
                flashState(document.getElementById('action-gaming'), false);
            } finally {
                settingGaming = false;
            }
        }
    }

    // Shared by the quick-actions and memory widgets. Resolves to the fresh memory status.
    async function purgeMemory(buttonId) {
        if (purging) return null;
        purging = true;
        const btn = document.getElementById(buttonId);
        if (btn) btn.dataset.state = 'launching';
        let memory = null;
        let ok = false;
        try {
            const res = await Host.call('purgeStandbyList');
            ok = !!(res && res.success);
            memory = (res && res.memory) || null;
        } catch { }
        finally { purging = false; }
        flashState(btn, ok);
        return memory;
    }

    function applyGamingState(state) {
        gamingActive = !!(state && state.active);
        reflectGaming();
    }

    function reflectGaming() {
        const btn = document.getElementById('action-gaming');
        if (!btn) return;
        btn.dataset.on = gamingActive ? 'true' : 'false';
        btn.setAttribute('aria-pressed', gamingActive ? 'true' : 'false');
    }

    function applySchedule(state) {
        scheduleState = state && typeof state === 'object' ? state : null;
        renderSchedule();
    }

    function scheduleRemainingSeconds() {
        if (!scheduleState || !scheduleState.enabled) return 0;
        const at = scheduleState.executeAtUtc ? new Date(scheduleState.executeAtUtc).getTime() : NaN;
        if (Number.isFinite(at)) return Math.max(0, Math.floor((at - Date.now()) / 1000));
        return Math.max(0, Math.floor(Number(scheduleState.remainingSeconds) || 0));
    }

    function formatCountdown(seconds) {
        const h = Math.floor(seconds / 3600);
        const m = Math.floor((seconds % 3600) / 60);
        const sec = seconds % 60;
        const pad = (n) => (n < 10 ? '0' : '') + n;
        return h > 0 ? h + ':' + pad(m) + ':' + pad(sec) : m + ':' + pad(sec);
    }

    function scheduleLabel(action) {
        const normalized = String(action || '').toLowerCase();
        if (normalized === 'sleep') return t('widget_action_sleep_in', 'Sleep in');
        if (normalized === 'restart') return t('widget_action_restart_in', 'Restart in');
        return t('widget_action_timer', 'Shut down in');
    }

    function renderSchedule() {
        if (scheduleTimer != null) clearInterval(scheduleTimer);
        scheduleTimer = null;
        const box = document.getElementById('action-timer');
        const controls = document.getElementById('action-timer-controls');
        if (!box || !controls) return;
        const active = !!(scheduleState && scheduleState.enabled && !scheduleState.expired);
        box.dataset.on = active ? 'true' : 'false';
        setText('action-timer-label', active ? scheduleLabel(scheduleState.action) : t('widget_action_timer', 'Shut down in'));
        if (!active) {
            controls.innerHTML = TIMER_PRESETS.map((minutes) =>
                '<button class="timer-chip" type="button" data-timer="' + minutes + '">' +
                (minutes >= 60 ? (minutes / 60) + 'h' : minutes + 'm') + '</button>').join('');
            return;
        }
        const daily = String(scheduleState.mode || '').toLowerCase() === 'daily';
        const cancel = esc(t('widget_action_timer_cancel', 'Cancel timer'));
        controls.innerHTML =
            '<strong class="timer-countdown" id="action-timer-countdown">' +
            (daily ? esc(scheduleState.dailyTime || '--:--') : formatCountdown(scheduleRemainingSeconds())) + '</strong>' +
            '<button class="timer-chip timer-cancel" type="button" data-timer="cancel" title="' + cancel + '" aria-label="' + cancel + '">' +
            '<span class="material-symbols-outlined">close</span></button>';
        if (daily || document.hidden) return;
        scheduleTimer = setInterval(() => {
            const remaining = scheduleRemainingSeconds();
            setText('action-timer-countdown', formatCountdown(remaining));
            if (remaining <= 0 && scheduleTimer != null) {
                clearInterval(scheduleTimer);
                scheduleTimer = null;
            }
        }, 1000);
    }

    async function runTimerAction(value) {
        try {
            if (value === 'cancel') {
                applySchedule(await Host.call('cancelScheduledPowerAction'));
                return;
            }
            const minutes = parseInt(value, 10);
            if (!TIMER_PRESETS.includes(minutes)) return;
            applySchedule(await Host.call('schedulePowerAction', { mode: 'relative', action: 'shutdown', delayMinutes: minutes }));
        } catch {
            Host.call('getScheduledPowerAction').then(applySchedule).catch(() => {});
        }
    }

    // ---- Brightness -----------------------------------------------------

    function startBrightness() {
        const label = esc(t('widget_brightness', 'Brightness'));
        shell('<div class="brightness-control" id="brightness-control">' +
            '<span class="material-symbols-outlined brightness-icon" id="brightness-icon" aria-hidden="true">brightness_medium</span>' +
            '<input class="brightness-slider" id="brightness-slider" type="range" min="0" max="100" step="1" value="50" aria-label="' + label + '">' +
            '<strong class="brightness-value" id="brightness-value">--</strong>' +
            '</div>' +
            '<p class="widget-muted brightness-unsupported hidden" id="brightness-unsupported" data-i18n="widget_brightness_unsupported">This display does not support brightness control.</p>');
        const slider = document.getElementById('brightness-slider');
        slider.addEventListener('input', () => {
            brightnessDragging = true;
            reflectBrightness(Number(slider.value));
            if (brightnessTimer != null) clearTimeout(brightnessTimer);
            brightnessTimer = setTimeout(commitBrightness, 150);
        });
        slider.addEventListener('change', () => {
            if (brightnessTimer != null) clearTimeout(brightnessTimer);
            commitBrightness();
        });
        document.getElementById('brightness-control').addEventListener('pointerenter', readBrightness);
        readBrightness();
    }

    async function readBrightness() {
        if (brightnessDragging || document.hidden) return;
        try { applyBrightness(await Host.call('getDisplayBrightness')); } catch { }
    }

    async function commitBrightness() {
        brightnessTimer = null;
        const slider = document.getElementById('brightness-slider');
        if (!slider) return;
        let state = null;
        try { state = await Host.call('setDisplayBrightness', { percent: Number(slider.value) }); } catch { }
        // A newer drag may have started while the call was in flight.
        if (brightnessTimer != null) return;
        brightnessDragging = false;
        if (state) applyBrightness(state);
        else readBrightness();
    }

    function applyBrightness(state) {
        const supported = !!(state && state.supported && state.percent != null);
        document.getElementById('brightness-control')?.classList.toggle('hidden', !supported);
        document.getElementById('brightness-unsupported')?.classList.toggle('hidden', supported);
        if (!supported || brightnessDragging) return;
        const value = Math.round(pct(state.percent));
        const slider = document.getElementById('brightness-slider');
        if (slider) slider.value = String(value);
        reflectBrightness(value);
    }

    function reflectBrightness(value) {
        setText('brightness-icon', value < 34 ? 'brightness_low' : value < 67 ? 'brightness_medium' : 'brightness_high');
        setText('brightness-value', Math.round(value) + '%');
        const slider = document.getElementById('brightness-slider');
        if (slider && slider.style && typeof slider.style.setProperty === 'function') {
            slider.style.setProperty('--fill', Math.round(value) + '%');
        }
    }

    // ---- Top processes --------------------------------------------------

    function startProcesses() {
        shell('<div class="proc-list" id="proc-list" role="list"></div>');
        syncPolling();
    }

    async function pollProcesses() {
        const count = size === 'mini' ? 3 : size === 'large' ? 8 : 5;
        renderProcesses(await Host.call('getTopProcesses', { count }));
    }

    function renderProcesses(list) {
        const host = document.getElementById('proc-list');
        if (!host) return;
        const rows = Array.isArray(list) ? list : [];
        if (rows.length === 0) {
            host.innerHTML = '<p class="widget-muted" data-i18n="widget_processes_empty">Waiting for process data.</p>';
            if (window.I18n && I18n.apply) I18n.apply();
            return;
        }
        host.innerHTML = rows.map((p) => {
            const cpu = pct(p.cpuPercent);
            const name = (p.name || '?') + (p.instances > 1 ? ' ×' + p.instances : '');
            return '<div class="proc-row" role="listitem">' +
                '<span class="proc-name" title="' + esc(name) + '">' + esc(name) + '</span>' +
                (size === 'mini' ? '' : '<span class="proc-ram">' + mb(p.ramMb) + '</span>') +
                '<strong class="proc-cpu">' + (cpu < 10 ? cpu.toFixed(1) : Math.round(cpu)) + '%</strong>' +
                '<div class="proc-bar" aria-hidden="true"><span style="width:' + cpu + '%"></span></div>' +
                '</div>';
        }).join('');
    }

    // ---- Memory ---------------------------------------------------------

    function startMemory() {
        const label = esc(t('widget_action_purge', 'Free RAM'));
        const purgeBtn = '<button class="widget-button memory-purge" id="memory-purge" type="button" title="' + label + '" aria-label="' + label + '">' +
            '<span class="material-symbols-outlined">cleaning_services</span>' +
            (size === 'mini' ? '' : '<span data-i18n="widget_action_purge">' + label + '</span>') + '</button>';
        shell('<div class="memory-head"><strong class="memory-pct" id="memory-pct">--</strong>' +
            '<span class="widget-muted memory-detail" id="memory-detail">--</span>' + (size === 'mini' ? purgeBtn : '') + '</div>' +
            '<div class="memory-bar" aria-hidden="true"><span class="memory-used" id="memory-bar"></span><span class="memory-standby" id="memory-standby-bar"></span></div>' +
            (size === 'mini' ? '' :
                '<div class="power-row"><span class="widget-muted memory-key memory-key-standby" data-i18n="widget_memory_standby">Standby</span><strong id="memory-standby">--</strong></div>' +
                (size === 'large' ? '<div class="power-row"><span class="widget-muted memory-key" data-i18n="widget_memory_free">Free</span><strong id="memory-free">--</strong></div>' : '') +
                purgeBtn));
        document.getElementById('memory-purge').addEventListener('click', async () => {
            const memory = await purgeMemory('memory-purge');
            if (memory) renderMemory(memory);
        });
        syncPolling();
    }

    async function pollMemory() {
        renderMemory(await Host.call('getMemoryStatus'));
    }

    function renderMemory(m) {
        if (!m) return;
        const used = pct(m.inUsePct);
        const standby = Math.min(100 - used, pct(m.standbyPct));
        setText('memory-pct', Math.round(used) + '%');
        setText('memory-detail', gb(m.inUseGb) + ' / ' + gb(m.totalGb));
        setText('memory-standby', gb(m.standbyGb));
        setText('memory-free', gb(m.freeGb));
        const bar = document.getElementById('memory-bar');
        const standbyBar = document.getElementById('memory-standby-bar');
        if (bar) bar.style.width = used + '%';
        if (standbyBar) standbyBar.style.width = standby + '%';
    }

    const POLLERS = { power: pollPower, processes: pollProcesses, memory: pollMemory };

    function applyAnimationLevel(level) {
        animationSetting = ['auto', 'low', 'medium', 'high'].includes(level) ? level : 'auto';
        if (animationSetting !== 'auto') {
            document.documentElement.dataset.animationLevel = animationSetting;
            return;
        }
        if (!animationHardwareTier || !window.VoltAnimationLevel) return;
        document.documentElement.dataset.animationLevel =
            VoltAnimationLevel.resolveLevel(animationSetting, animationHardwareTier);
    }

    function applyAnimationHardware(info) {
        if (!info || !window.VoltAnimationLevel || !VoltAnimationLevel.classifyHardwareTier) return;
        animationHardwareTier = VoltAnimationLevel.classifyHardwareTier(info.ramTotalGb, info.logicalCores);
        document.documentElement.dataset.hwTier = animationHardwareTier;
        applyAnimationLevel(animationSetting);
    }

    function applySettings(res) {
        if (!res || !res.settings) return;
        applyAnimationLevel(res.settings.animationLevel || 'auto');
        if (window.VoltFont && VoltFont.apply) {
            VoltFont.apply(res.settings.font || 'inter');
        }
        locale = (window.I18n && I18n.getLocale ? I18n.getLocale() : locale);
        window.__voltThemeCatalog = res.themeCatalog || {};
        if (res.theme && window.VoltTheme) {
            VoltTheme.apply(res.theme.themeColor || res.settings.themeColor, res.theme.palette);
        }
        const item = res.settings.widgets && Array.isArray(res.settings.widgets.items)
            ? res.settings.widgets.items.find(i => i.type === type)
            : null;
        pinned = !!(item && item.pinned);
        reflectPin();
    }

    Host.on('themeChanged', (data) => {
        if (!data || !data.themeColor || !data.palette || !window.VoltTheme) return;
        window.__voltThemeCatalog = window.__voltThemeCatalog || {};
        window.__voltThemeCatalog[data.themeColor] = data.palette;
        VoltTheme.apply(data.themeColor, data.palette);
    });
    Host.on('fontChanged', (data) => {
        if (window.VoltFont && VoltFont.apply && data && data.font) {
            VoltFont.apply(data.font);
        }
    });
    Host.on('animationLevelChanged', (data) => {
        applyAnimationLevel(data && data.level);
    });
    Host.on('widgetTopmostChanged', (data) => {
        pinned = !!(data && data.topmost);
        reflectPin();
    });
    Host.on('keepAwakeChanged', (state) => {
        keepAwake = !!(state && state.enabled);
        reflectKeepAwake();
    });
    Host.on('languageChanged', (data) => {
        if (!data || !data.language) return;
        locale = data.locale || locale;
        if (window.I18n && I18n.onHostLanguageChanged) I18n.onHostLanguageChanged(data);
        // Re-render date-dependent widgets
        switch (type) {
            case 'clock': startClock(); break;
            case 'calendar': startCalendar(); break;
            case 'plans': startPlans(); break;
            case 'launcher': renderLaunchers(); break;
            case 'apps': renderLaunchers(); break;
            case 'actions': renderSchedule(); break;
        }
    });

    if (type === 'plans') Host.on('activePlanChanged', data => reflectPlanSelector(data && data.plan));
    if (isLauncherWidget) {
        Host.on('launchersChanged', () => loadLaunchers());
        Host.on('launcherDropResult', result => showLauncherToast(result));
    }
    if (type === 'actions') {
        Host.on('gamingModeChanged', applyGamingState);
        Host.on('scheduledPowerActionChanged', applySchedule);
    }
    Host.on('resourceProfileChanged', state => {
        const profile = state && state.profile;
        if (!['full', 'balanced', 'gaming', 'workload', 'critical'].includes(profile)) return;
        const reducedEffects = !!(state && state.reducedEffects);
        if (profile === resourceProfile && reducedEffects === resourceReducedEffects) return;
        resourceProfile = profile;
        resourceReducedEffects = reducedEffects;
        document.documentElement.dataset.resourceProfile = profile;
        document.documentElement.dataset.perf = reducedEffects ? 'lite' : 'full';
        syncPolling();
    });
    document.addEventListener('visibilitychange', () => {
        scheduleDateTick();
        syncPolling();
        if (type === 'actions') renderSchedule();
        if (type === 'brightness') readBrightness();
    });

    ({
        clock: startClock, calendar: startCalendar, usage: startUsage, temps: startTemps, power: startPower, plans: startPlans,
        launcher: startLauncher, apps: startLauncher, actions: startActions, brightness: startBrightness, processes: startProcesses, memory: startMemory,
    }[type] || startClock)();

    if (Host.available) {
        Host.call('getSystemInfo').then(applyAnimationHardware).catch(() => {});
        Host.call('getSettings').then(applySettings).catch(() => {});
    }
})();
