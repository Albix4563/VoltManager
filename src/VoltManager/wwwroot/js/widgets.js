(function () {
    const TYPES = ['clock', 'calendar', 'usage', 'temps', 'power'];
    const SIZES = ['mini', 'medium', 'large'];
    const params = new URLSearchParams(location.search);
    const type = TYPES.includes(params.get('w')) ? params.get('w') : 'clock';
    const size = SIZES.includes(params.get('s')) ? params.get('s') : 'medium';
    const root = document.getElementById('widget-root');
    let pinned = false;
    let locale = (window.I18n && I18n.getLang && I18n.getLang()) || 'it';
    document.documentElement.dataset.size = size;

    const labels = {
        clock: ['schedule', 'widget_clock'],
        calendar: ['calendar_month', 'widget_calendar'],
        usage: ['monitor_heart', 'widget_usage'],
        temps: ['device_thermostat', 'widget_temps'],
        power: ['bolt', 'widget_power'],
    };

    function t(key, fallback) {
        if (!window.I18n || !I18n.t) return fallback || key;
        const value = I18n.t(key);
        return value === key ? (fallback || key) : value;
    }

    function shell(bodyHtml) {
        const meta = labels[type] || labels.clock;
        root.innerHTML =
            '<article class="desktop-widget" data-size="' + size + '">' +
            '  <header class="widget-header" id="widget-drag">' +
            '    <div class="widget-title"><span class="material-symbols-outlined">' + meta[0] + '</span><span data-i18n="' + meta[1] + '">' + t(meta[1], type) + '</span></div>' +
            '    <button class="widget-action" id="widget-pin" type="button" title="' + t('widget_pin', 'Pin') + '" aria-label="' + t('widget_pin', 'Pin') + '"><span class="material-symbols-outlined">push_pin</span></button>' +
            '    <button class="widget-action" id="widget-close" type="button" title="' + t('widget_close', 'Close') + '" aria-label="' + t('widget_close', 'Close') + '"><span class="material-symbols-outlined">close</span></button>' +
            '  </header>' +
            '  <section class="widget-body">' + bodyHtml + '</section>' +
            '</article>';
        if (window.I18n && I18n.apply) I18n.apply();
        wireChrome();
    }

    function wireChrome() {
        document.getElementById('widget-drag')?.addEventListener('pointerdown', (e) => {
            if (e.target.closest('button')) return;
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
        document.getElementById('widget-close')?.addEventListener('click', () => {
            Host.call('closeWidget').catch(() => {});
        });
    }

    function reflectPin() {
        const btn = document.getElementById('widget-pin');
        if (!btn) return;
        btn.dataset.on = pinned ? 'true' : 'false';
        btn.setAttribute('aria-pressed', pinned ? 'true' : 'false');
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
        function tick() {
            const now = new Date();
            timeEl.textContent = new Intl.DateTimeFormat(locale, { hour: '2-digit', minute: '2-digit' }).format(now);
            if (dateEl) dateEl.textContent = new Intl.DateTimeFormat(locale, { weekday: 'long', day: '2-digit', month: 'long' }).format(now);
        }
        tick();
        setInterval(tick, 1000);
    }

    function startCalendar() {
        if (size === 'mini') {
            shell('<div class="calendar-mini"><div class="widget-muted" id="calendar-mini-weekday">--</div><div class="calendar-mini-day" id="calendar-mini-day">--</div><div class="widget-muted" id="calendar-mini-month">--</div></div>');
            renderCalendarMini();
            return;
        }
        shell('<div class="widget-muted" id="calendar-title" style="margin-bottom:10px"></div><div class="calendar-head" id="calendar-head"></div><div class="calendar-grid" id="calendar-grid"></div>');
        renderCalendar();
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
            '<div class="power-row"><span class="widget-muted" data-i18n="widget_battery">Battery</span><strong id="power-battery">--</strong></div>' +
            (size === 'mini' ? '' :
                '<div class="power-row"><span class="widget-muted" data-i18n="widget_plan">Plan</span><strong id="power-plan">--</strong></div>' +
                '<div class="power-row"><span class="widget-muted" data-i18n="widget_cpu_auto">CPU avg</span><strong id="power-auto-cpu">--</strong></div>' +
                '<div class="power-row"><span class="widget-muted" data-i18n="widget_sample_interval">Sample</span><strong id="power-auto-sample">--</strong></div>'));
        if (window.I18n && I18n.apply) I18n.apply();
        pollPower();
        if (size !== 'mini') {
            pollPlan();
            pollCpuAutomation();
        }
        setInterval(pollPower, 5000);
        Host.on('activePlanChanged', (data) => renderPlan(data && data.plan));
        Host.on('cpuAutomationStateChanged', renderCpuAutomation);
    }

    async function pollPower() {
        try {
            const state = await Host.call('getBatteryPower');
            renderPower(state);
        } catch { }
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
        if (!state || !state.available) {
            watts.textContent = '--';
            battery.textContent = 'AC';
            return;
        }
        watts.textContent = state.powerWatts == null ? '--' : (state.powerWatts > 0 ? '+' : '') + state.powerWatts.toFixed(1) + ' W';
        battery.textContent = state.batteryPercent == null ? '--' : state.batteryPercent + '%';
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

    function applySettings(res) {
        if (!res || !res.settings) return;
        locale = (window.I18n && I18n.getLang && I18n.getLang()) || locale;
        if (res.resolvedTheme && window.VoltTheme) {
            window.__voltResolvedTheme = res.resolvedTheme;
            VoltTheme.apply(res.settings.theme || 'dark', res.resolvedTheme);
        }
        const item = res.settings.widgets && Array.isArray(res.settings.widgets.items)
            ? res.settings.widgets.items.find(i => i.type === type)
            : null;
        pinned = !!(item && item.pinned);
        reflectPin();
    }

    Host.on('themeChanged', (data) => {
        if (!data || !data.resolvedTheme || !window.VoltTheme) return;
        window.__voltResolvedTheme = data.resolvedTheme;
        VoltTheme.apply('auto', data.resolvedTheme);
    });
    Host.on('widgetTopmostChanged', (data) => {
        pinned = !!(data && data.topmost);
        reflectPin();
    });

    ({ clock: startClock, calendar: startCalendar, usage: startUsage, temps: startTemps, power: startPower }[type] || startClock)();

    if (Host.available) {
        Host.call('getSettings').then(applySettings).catch(() => {});
    }
})();
