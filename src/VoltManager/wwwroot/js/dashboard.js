/**
 * Home dashboard: live metrics rings/bars + power plan segmented control.
 */
(function () {
    const CIRC = 251.2; // 2 * PI * r(40)

    const cpuRing = document.getElementById('cpu-ring');
    const cpuPct = document.getElementById('cpu-pct');
    const gpuRing = document.getElementById('gpu-ring');
    const gpuPct = document.getElementById('gpu-pct');
    const gpuName = document.getElementById('gpu-name');
    const ramPct = document.getElementById('ram-pct');
    const ramBar = document.getElementById('ram-bar');
    const ramDetail = document.getElementById('ram-detail');
    const diskPct = document.getElementById('disk-pct');
    const diskBar = document.getElementById('disk-bar');
    const cpuTemp = document.getElementById('cpu-temp');
    const cpuTempBadge = document.getElementById('cpu-temp-badge');
    const cpuClock = document.getElementById('cpu-clock');
    const gpuTemp = document.getElementById('gpu-temp');
    const gpuTempBadge = document.getElementById('gpu-temp-badge');
    const ramClock = document.getElementById('ram-clock');
    const tempSection = document.getElementById('temp-section');
    const sensorList = document.getElementById('sensor-list');
    const batteryHealthSection = document.getElementById('battery-health-section');
    const batteryHealthRating = document.getElementById('battery-health-rating');
    const batteryHealthPct = document.getElementById('battery-health-pct');
    const batteryHealthDetail = document.getElementById('battery-health-detail');
    const batteryHealthBar = document.getElementById('battery-health-bar');
    const powerFlowSection = document.getElementById('power-flow-section');
    const powerFlowIcon = document.getElementById('power-flow-icon');
    const powerFlowStatusIcon = document.getElementById('power-flow-status-icon');
    const powerFlowStatus = document.getElementById('power-flow-status');
    const powerFlowWatts = document.getElementById('power-flow-watts');
    const powerFlowPercent = document.getElementById('power-flow-percent');
    const powerFlowTimeLabel = document.getElementById('power-flow-time-label');
    const powerFlowTime = document.getElementById('power-flow-time');
    const powerFlowVoltage = document.getElementById('power-flow-voltage');
    const powerFlowDetail = document.getElementById('power-flow-detail');

    function setRing(circle, label, pct) {
        if (window.VoltFx) { window.VoltFx.animateRing(circle, label, pct); return; }
        circle.style.strokeDashoffset = (CIRC * (1 - pct / 100)).toFixed(1);
        label.textContent = Math.round(pct) + '%';
    }

    let gpuUnavailableShown = false;

    // ----- Temperatures & fans -----
    const CATEGORY_ORDER = ['cpu', 'gpu', 'storage', 'motherboard'];

    // No reading -> hide the badge entirely instead of showing a useless N/D.
    function setTempBadge(badge, label, value) {
        badge.classList.toggle('hidden', value == null);
        if (value != null) label.textContent = Math.round(value) + '°C';
    }

    function setClockText(element, value) {
        element.classList.toggle('hidden', value == null);
        if (value != null) element.textContent = Math.round(value) + ' MHz';
    }

    function formatSensor(s) {
        if (s.type === 'clock') return Math.round(s.value) + ' MHz';
        return s.type === 'fan' ? Math.round(s.value) + ' RPM' : Math.round(s.value) + '°C';
    }

    // DOM is rebuilt only when the sensor set changes (cached key); per-tick we
    // just rewrite the cached value spans to avoid innerHTML churn every second.
    let sensorKey = '';
    let sensorValueEls = [];

    function sortSensors(sensors) {
        return sensors.slice().sort((a, b) =>
            (CATEGORY_ORDER.indexOf(a.category) - CATEGORY_ORDER.indexOf(b.category)) ||
            a.hardware.localeCompare(b.hardware) ||
            (a.type === b.type ? 0 : a.type === 'temp' ? -1 : 1));
    }

    function buildSensorList(sorted) {
        sensorList.innerHTML = '';
        sensorValueEls = [];
        let group = null;
        let lastGroup = '';
        sorted.forEach((s) => {
            const groupKey = s.category + '|' + s.hardware;
            if (groupKey !== lastGroup) {
                lastGroup = groupKey;
                group = document.createElement('div');
                const header = document.createElement('p');
                header.className = 'text-label-md text-secondary-container uppercase mb-2';
                header.textContent = I18n.t('dash_cat_' + s.category) + ' · ' + s.hardware;
                group.appendChild(header);
                sensorList.appendChild(group);
            }
            const row = document.createElement('div');
            row.className = 'flex items-center justify-between py-1 border-b border-white/5 last:border-0';
            const name = document.createElement('span');
            name.className = 'text-body-md text-on-surface-variant truncate pr-4';
            name.textContent = s.name;
            const value = document.createElement('span');
            value.className = 'text-body-md font-semibold text-on-surface whitespace-nowrap';
            row.appendChild(name);
            row.appendChild(value);
            group.appendChild(row);
            sensorValueEls.push(value);
        });
    }

    function renderSensors(m) {
        const sensors = m.sensorsAvailable && m.sensors ? m.sensors : [];
        // Nothing live to show -> whole section disappears.
        tempSection.classList.toggle('hidden', sensors.length === 0);
        if (sensors.length === 0) {
            sensorKey = '';
            return;
        }
        const sorted = sortSensors(sensors);
        const key = sorted.map(s => s.category + '|' + s.hardware + '|' + s.name + '|' + s.type).join(';');
        if (key !== sensorKey) {
            sensorKey = key;
            buildSensorList(sorted);
        }
        sorted.forEach((s, i) => { sensorValueEls[i].textContent = formatSensor(s); });
    }

    document.addEventListener('langchanged', () => {
        sensorKey = ''; // force rebuild on next tick so group headers translate
    });

    // ----- Battery health (wear level) -----
    let lastBatteryHealth = null;

    function renderBatteryHealth(state) {
        lastBatteryHealth = state;
        const ok = state && state.available && state.healthPercent != null;
        batteryHealthSection.classList.toggle('hidden', !ok);
        if (!ok) return;

        const health = state.healthPercent;
        batteryHealthRating.textContent = I18n.t('dash_battery_health_rating_' + state.rating);
        const wear = state.wearPercent != null ? state.wearPercent : (100 - health);
        const designWh = state.designedCapacityMwh ? (state.designedCapacityMwh / 1000).toFixed(1) : '--';
        const fullWh = state.fullChargedCapacityMwh != null ? (state.fullChargedCapacityMwh / 1000).toFixed(1) : '--';
        batteryHealthDetail.textContent =
            fullWh + ' Wh / ' + designWh + ' Wh · ' + wear + '% ' + I18n.t('dash_battery_health_wear');
        if (window.VoltFx) {
            window.VoltFx.animateNumber(batteryHealthPct, health, { suffix: '%' });
            window.VoltFx.animateBar(batteryHealthBar, health);
        } else {
            batteryHealthPct.textContent = Math.round(health) + '%';
            batteryHealthBar.style.width = health + '%';
        }
    }

    document.addEventListener('langchanged', () => {
        if (lastBatteryHealth) renderBatteryHealth(lastBatteryHealth);
    });

    // ----- Power flow (live battery charge/discharge wattage) -----
    let lastPowerFlow = null;
    let powerFlowTimer = null;

    const POWER_FLOW_ICON = {
        charging: 'battery_charging_full',
        discharging: 'battery_5_bar',
        full: 'battery_full',
        idle: 'power',
        unknown: 'battery_unknown',
    };

    function formatDuration(minutes) {
        if (minutes == null || minutes < 0) return '--';
        const h = Math.floor(minutes / 60);
        const m = minutes % 60;
        if (h > 0) return h + 'h ' + m + 'm';
        return m + 'm';
    }

    function renderPowerFlow(state) {
        lastPowerFlow = state;
        const ok = state && state.available;
        powerFlowSection.classList.toggle('hidden', !ok);
        if (!ok) return;

        const status = state.status || 'unknown';
        powerFlowStatus.textContent = I18n.t('power_flow_status_' + status);
        const iconName = POWER_FLOW_ICON[status] || POWER_FLOW_ICON.unknown;
        powerFlowStatusIcon.textContent = iconName;
        powerFlowIcon.textContent = status === 'discharging' ? 'battery_horiz_050' : 'bolt';

        const watts = state.powerWatts;
        if (watts == null) {
            powerFlowWatts.textContent = '--';
        } else if (window.VoltFx) {
            // signed:true keeps the +/- cue through every chase frame.
            window.VoltFx.animateNumber(powerFlowWatts, watts, { suffix: ' W', decimals: 1, signed: true });
        } else {
            powerFlowWatts.textContent = (watts > 0 ? '+' : '') + watts.toFixed(1) + ' W';
        }

        powerFlowPercent.textContent = state.batteryPercent != null ? state.batteryPercent + '%' : '--';

        if (state.timeKind === 'toEmpty' || state.timeKind === 'toFull') {
            powerFlowTimeLabel.textContent = I18n.t(
                state.timeKind === 'toFull' ? 'power_flow_to_full' : 'power_flow_to_empty');
            powerFlowTime.textContent = formatDuration(state.minutesRemaining);
        } else {
            powerFlowTimeLabel.textContent = I18n.t('power_flow_to_empty');
            powerFlowTime.textContent = '--';
        }

        powerFlowVoltage.textContent = state.voltageVolts != null ? state.voltageVolts.toFixed(2) + ' V' : '--';

        const acText = state.onAc ? I18n.t('power_flow_plugged') : I18n.t('power_flow_on_battery');
        const capText = (state.remainingCapacityMwh != null && state.fullChargedCapacityMwh != null)
            ? ' · ' + (state.remainingCapacityMwh / 1000).toFixed(1) + ' / '
              + (state.fullChargedCapacityMwh / 1000).toFixed(1) + ' Wh'
            : '';
        powerFlowDetail.textContent = acText + capText;
    }

    async function pollPowerFlow() {
        if (!Host.available) return;
        try {
            const state = await Host.call('getBatteryPower');
            renderPowerFlow(state);
        } catch (err) {
            console.error('getBatteryPower failed', err);
        }
    }

    function startPowerFlowPolling() {
        if (powerFlowTimer) return;
        pollPowerFlow();
        powerFlowTimer = setInterval(pollPowerFlow, 5000);
    }

    document.addEventListener('langchanged', () => {
        if (lastPowerFlow) renderPowerFlow(lastPowerFlow);
    });

    // ----- Battery history (charge % sparkline over time) -----
    const batteryHistorySection = document.getElementById('battery-history-section');
    const batteryHistoryRange = document.getElementById('battery-history-range');
    const batteryHistoryCurrent = document.getElementById('battery-history-current');
    const batteryHistoryStats = document.getElementById('battery-history-stats');
    const batteryHistoryLine = document.getElementById('battery-history-line');
    const batteryHistoryArea = document.getElementById('battery-history-area');
    let lastBatteryHistory = null;
    let batteryHistoryTimer = null;

    function formatHistorySpan(seconds) {
        const h = Math.floor(seconds / 3600);
        const m = Math.floor((seconds % 3600) / 60);
        if (h > 0) return h + 'h' + (m > 0 ? ' ' + m + 'm' : '');
        return Math.max(1, m) + 'm';
    }

    function renderBatteryHistory(payload) {
        lastBatteryHistory = payload;
        const samples = (payload && payload.samples) || [];
        const pts = samples.filter(s => s.pct != null);
        // Need at least two points to draw a trend; otherwise hide the whole card.
        batteryHistorySection.classList.toggle('hidden', pts.length < 2);
        if (pts.length < 2) return;

        const W = 100, H = 32, padY = 2;
        const t0 = pts[0].t;
        const t1 = pts[pts.length - 1].t;
        const span = Math.max(1, t1 - t0);
        const x = (t) => (t - t0) / span * W;
        const y = (pct) => H - padY - (pct / 100) * (H - padY * 2);

        let d = '';
        pts.forEach((s, i) => {
            d += (i === 0 ? 'M' : 'L') + x(s.t).toFixed(2) + ' ' + y(s.pct).toFixed(2) + ' ';
        });
        d = d.trim();
        batteryHistoryLine.setAttribute('d', d);
        batteryHistoryArea.setAttribute('d',
            d + ' L' + W.toFixed(2) + ' ' + H + ' L0 ' + H + ' Z');

        const cur = pts[pts.length - 1].pct;
        batteryHistoryCurrent.textContent = cur + '%';
        batteryHistoryRange.textContent =
            I18n.t('battery_history_window').replace('{span}', formatHistorySpan(t1 - t0));

        let min = pts[0].pct, max = pts[0].pct;
        for (const s of pts) { if (s.pct < min) min = s.pct; if (s.pct > max) max = s.pct; }
        batteryHistoryStats.textContent =
            I18n.t('battery_history_min') + ' ' + min + '% · ' +
            I18n.t('battery_history_max') + ' ' + max + '% · ' +
            pts.length + ' ' + I18n.t('battery_history_samples');
    }

    async function pollBatteryHistory() {
        if (!Host.available) return;
        try {
            const payload = await Host.call('getBatteryHistory');
            renderBatteryHistory(payload);
        } catch (err) {
            console.error('getBatteryHistory failed', err);
        }
    }

    function startBatteryHistoryPolling() {
        if (batteryHistoryTimer) return;
        pollBatteryHistory();
        batteryHistoryTimer = setInterval(pollBatteryHistory, 60000);
    }

    document.addEventListener('langchanged', () => {
        if (lastBatteryHistory) renderBatteryHistory(lastBatteryHistory);
    });

    Host.on('metrics', (m) => {
        setTempBadge(cpuTempBadge, cpuTemp, m.cpuTemp);
        setTempBadge(gpuTempBadge, gpuTemp, m.gpuTemp);
        setClockText(cpuClock, m.cpuClock);
        setClockText(ramClock, m.ramClock);
        renderSensors(m);
        setRing(cpuRing, cpuPct, m.cpu);
        if (m.gpuAvailable) {
            setRing(gpuRing, gpuPct, m.gpu);
        } else {
            gpuRing.style.strokeDashoffset = CIRC;
            gpuPct.textContent = 'N/D';
            if (!gpuUnavailableShown) {
                gpuName.textContent = I18n.t('dash_gpu_unavailable');
                gpuUnavailableShown = true;
            }
        }
        if (window.VoltFx) {
            window.VoltFx.animateNumber(ramPct, m.ramPct, { suffix: '%' });
            window.VoltFx.animateBar(ramBar, m.ramPct);
            window.VoltFx.animateNumber(diskPct, m.disk, { suffix: '%' });
            window.VoltFx.animateBar(diskBar, m.disk);
        } else {
            ramPct.textContent = Math.round(m.ramPct) + '%';
            ramBar.style.width = m.ramPct + '%';
            diskPct.textContent = Math.round(m.disk) + '%';
            diskBar.style.width = m.disk + '%';
        }
        ramDetail.textContent = m.ramUsedGb.toFixed(1) + ' GB / ' + m.ramTotalGb.toFixed(1) + ' GB In Use';
    });

    // ----- Power plan segmented control -----
    const planButtons = Array.from(document.querySelectorAll('#plan-control button'));
    const pill = document.getElementById('plan-pill');
    const overrideChip = document.getElementById('manual-override-chip');
    const overrideLabel = document.getElementById('manual-override-label');
    const clearOverrideBtn = document.getElementById('btn-clear-manual-override');
    const powerSourcePlanHome = document.getElementById('pref-power-source-plan-home');
    const powerSourcePlanHomeToggle = document.getElementById('toggle-power-source-plan-home');
    const gamingModeHome = document.getElementById('pref-gaming-mode-home');
    const gamingModeHomeToggle = document.getElementById('toggle-gaming-mode-home');
    const overrideOverlay = document.getElementById('manual-override-overlay');
    const overridePlanLabel = document.getElementById('manual-override-plan');
    const overrideWarning = document.getElementById('manual-override-warning');
    const overrideConfirm = document.getElementById('btn-manual-override-confirm');
    const overrideCancel = document.getElementById('btn-manual-override-cancel');
    const overrideOptions = Array.from(document.querySelectorAll('.manual-override-option'));
    const planOrder = ['powerSaver', 'balanced', 'performance'];
    let switching = false;
    let pendingPlan = null;
    let pendingForever = false;
    let pendingHours = null;
    let activeOverride = null;
    let overrideTimer = null;

    function reflectPlan(plan) {
        const index = planOrder.indexOf(plan);
        planButtons.forEach((b) => {
            b.classList.remove('text-secondary-container', 'font-semibold');
            b.classList.add('text-on-surface-variant');
        });
        if (index < 0) {
            // Unknown/custom plan active: park the pill out of view.
            pill.style.opacity = '0';
            return;
        }
        pill.style.opacity = '1';
        pill.style.transform = 'translateX(' + (index * 102) + '%)';
        const btn = planButtons[index];
        btn.classList.add('text-secondary-container', 'font-semibold');
        btn.classList.remove('text-on-surface-variant');
    }

    function planName(plan) {
        const key = {
            powerSaver: 'dash_plan_saver',
            balanced: 'dash_plan_balanced',
            performance: 'dash_plan_performance',
        }[plan];
        return key ? I18n.t(key) : plan;
    }

    function formatRemaining(expiresAtUtc) {
        const expires = new Date(expiresAtUtc);
        const ms = Math.max(0, expires.getTime() - Date.now());
        const totalMinutes = Math.ceil(ms / 60000);
        const hours = Math.floor(totalMinutes / 60);
        const minutes = totalMinutes % 60;
        if (hours > 0) return hours + 'h ' + minutes + 'm';
        return minutes + 'm';
    }

    function renderOverrideStatus(override) {
        activeOverride = override || null;
        if (overrideTimer) {
            clearInterval(overrideTimer);
            overrideTimer = null;
        }

        if (!activeOverride) {
            overrideChip.classList.add('hidden');
            overrideChip.classList.remove('flex');
            return;
        }

        const update = () => {
            overrideChip.classList.remove('hidden');
            overrideChip.classList.add('flex');
            if (!activeOverride.expiresAtUtc) {
                overrideLabel.textContent = I18n.t('override_locked_forever');
                return;
            }
            overrideLabel.textContent = I18n.t('override_locked_until') + formatRemaining(activeOverride.expiresAtUtc);
        };

        update();
        if (activeOverride.expiresAtUtc) overrideTimer = setInterval(update, 30000);
    }

    function setMiniToggle(el, on) {
        if (!el) return;
        el.dataset.on = on ? 'true' : 'false';
        el.dataset.state = on ? 'on' : 'off';
        el.setAttribute('aria-pressed', on ? 'true' : 'false');
    }

    function normalizePowerSourcePlan(settings) {
        if (!settings.powerSourcePlan) {
            settings.powerSourcePlan = { enabled: true, pluggedPlan: 'performance', unpluggedMode: 'previous' };
        }
        settings.powerSourcePlan.enabled = settings.powerSourcePlan.enabled !== false;
        return settings.powerSourcePlan;
    }

    function renderPowerSourcePlanState(state) {
        const enabled = state ? !!state.enabled : true;
        setMiniToggle(powerSourcePlanHomeToggle, enabled);
        if (window.__voltSettings) {
            const settings = window.__voltSettings.get ? window.__voltSettings.get() : window.__voltSettings;
            normalizePowerSourcePlan(settings).enabled = enabled;
        }
    }

    function coerceGamingModeState(data) {
        if (data && data.state) return data.state;
        return data || { active: false };
    }

    function renderGamingModeState(data) {
        const state = coerceGamingModeState(data);
        setMiniToggle(gamingModeHomeToggle, !!state.active);
    }

    async function setGamingModeFromHome(enabled) {
        if (!Host.available) return;
        renderGamingModeState({ active: enabled });
        try {
            const res = window.__voltGamingMode && window.__voltGamingMode.setEnabled
                ? await window.__voltGamingMode.setEnabled(enabled)
                : await Host.call('setGamingMode', { enabled });
            renderGamingModeState(res);
        } catch (err) {
            console.error('setGamingMode failed', err);
            renderGamingModeState({ active: !enabled });
        }
    }

    function resetOverrideModal() {
        pendingForever = false;
        pendingHours = null;
        overrideWarning.classList.add('hidden');
        overrideConfirm.classList.add('hidden');
        overrideOptions.forEach((option) => {
            option.classList.remove('bg-white/10', 'text-secondary-container');
        });
    }

    function closeOverrideModal() {
        overrideOverlay.style.display = 'none';
        overrideOverlay.classList.add('hidden');
        overrideOverlay.classList.remove('flex');
        pendingPlan = null;
        resetOverrideModal();
    }

    function openOverrideModal(plan) {
        pendingPlan = plan;
        resetOverrideModal();
        overridePlanLabel.textContent = planName(plan);
        overrideOverlay.style.display = 'flex';
        overrideOverlay.classList.remove('hidden');
        overrideOverlay.classList.add('flex');
    }
    window.openOverrideModal = openOverrideModal;

    async function applyOverride() {
        if (!pendingPlan || switching) return;
        switching = true;
        const previous = planButtons.find(b => b.classList.contains('text-secondary-container'));
        reflectPlan(pendingPlan);
        try {
            const payload = { plan: pendingPlan };
            if (!pendingForever) payload.hours = pendingHours;
            const res = await Host.call('setManualOverride', payload);
            if (!res.success && previous) reflectPlan(previous.dataset.plan);
            renderOverrideStatus(res.override);
            closeOverrideModal();
        } catch (err) {
            console.error('setManualOverride failed', err);
            if (previous) reflectPlan(previous.dataset.plan);
        } finally {
            switching = false;
        }
    }

    planButtons.forEach((btn) => {
        btn.addEventListener('click', () => {
            if (switching) return;
            openOverrideModal(btn.dataset.plan);
        });
    });

    overrideOptions.forEach((option) => {
        option.addEventListener('click', async () => {
            overrideOptions.forEach((o) => o.classList.remove('bg-white/10', 'text-secondary-container'));
            option.classList.add('bg-white/10', 'text-secondary-container');

            pendingForever = option.dataset.forever === 'true';
            pendingHours = pendingForever ? null : Number(option.dataset.hours);
            overrideWarning.classList.toggle('hidden', !pendingForever);
            overrideConfirm.classList.toggle('hidden', !pendingForever);
            if (!pendingForever) await applyOverride();
        });
    });

    overrideConfirm.addEventListener('click', applyOverride);
    overrideCancel.addEventListener('click', closeOverrideModal);
    overrideOverlay.addEventListener('click', (event) => {
        if (event.target === overrideOverlay) closeOverrideModal();
    });

    clearOverrideBtn.addEventListener('click', async () => {
        try {
            const res = await Host.call('clearManualOverride');
            renderOverrideStatus(res.override);
        } catch (err) {
            console.error('clearManualOverride failed', err);
        }
    });

    powerSourcePlanHome?.addEventListener('click', async () => {
        if (!Host.available) return;
        const enable = powerSourcePlanHomeToggle?.dataset.on !== 'true';
        setMiniToggle(powerSourcePlanHomeToggle, enable);
        try {
            const state = await Host.call('setPowerSourcePlanSwitch', { enabled: enable });
            renderPowerSourcePlanState(state);
        } catch (err) {
            console.error('setPowerSourcePlanSwitch failed', err);
            setMiniToggle(powerSourcePlanHomeToggle, !enable);
        }
    });

    gamingModeHome?.addEventListener('click', () => {
        const enable = gamingModeHomeToggle?.dataset.on !== 'true';
        setGamingModeFromHome(enable);
    });

    gamingModeHome?.addEventListener('keydown', (event) => {
        if (event.key !== 'Enter' && event.key !== ' ') return;
        event.preventDefault();
        const enable = gamingModeHomeToggle?.dataset.on !== 'true';
        setGamingModeFromHome(enable);
    });

    Host.on('activePlanChanged', (data) => {
        reflectPlan(data.plan ? data.plan : null);
    });

    Host.on('automationStateChanged', (data) => {
        renderOverrideStatus(data.override);
    });

    Host.on('manualOverrideChanged', (data) => {
        renderOverrideStatus(data.override);
    });

    Host.on('powerSourcePlanChanged', renderPowerSourcePlanState);
    Host.on('gamingModeChanged', renderGamingModeState);
    document.addEventListener('gamingmodechanged', (event) => renderGamingModeState(event.detail));

    document.addEventListener('langchanged', () => {
        renderOverrideStatus(activeOverride);
    });

    function checkBatteryPresence() {
        const info = window.VoltSystemInfo;
        if (info) {
            applyBatteryPresence(info.hasBattery);
        } else {
            document.addEventListener('systeminfoloaded', (e) => {
                applyBatteryPresence(e.detail.hasBattery);
            });
        }
    }

    function applyBatteryPresence(hasBattery) {
        if (powerSourcePlanHome) {
            powerSourcePlanHome.classList.toggle('hidden', hasBattery === false);
        }
        // No battery -> never poll the firmware power flow (section stays hidden).
        if (hasBattery !== false) {
            startPowerFlowPolling();
            startBatteryHistoryPolling();
        } else {
            powerFlowSection.classList.add('hidden');
            batteryHistorySection.classList.add('hidden');
        }
    }

    // Initial active plan.
    if (Host.available) {
        checkBatteryPresence();
        Host.call('getActivePlan').then(p => {
            if (p && p.planId) reflectPlan(p.planId);
        }).catch(() => {});
        Host.call('getSettings').then(res => {
            if (res && res.settings) {
                renderOverrideStatus(res.settings.override);
                renderPowerSourcePlanState(normalizePowerSourcePlan(res.settings));
            }
        }).catch(() => {});
        Host.call('getPowerSourcePlanState').then(renderPowerSourcePlanState).catch(() => {});
        Host.call('getGamingMode').then(renderGamingModeState).catch(() => {});
        Host.call('getBatteryHealth').then(renderBatteryHealth).catch(() => {});
    }
})();
