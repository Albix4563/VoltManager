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

    function setRing(circle, label, pct) {
        circle.style.strokeDashoffset = (CIRC * (1 - pct / 100)).toFixed(1);
        label.textContent = Math.round(pct) + '%';
    }

    let gpuUnavailableShown = false;

    Host.on('metrics', (m) => {
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
        ramPct.textContent = Math.round(m.ramPct) + '%';
        ramBar.style.width = m.ramPct + '%';
        ramDetail.textContent = m.ramUsedGb.toFixed(1) + ' GB / ' + m.ramTotalGb.toFixed(1) + ' GB In Use';
        diskPct.textContent = Math.round(m.disk) + '%';
        diskBar.style.width = m.disk + '%';
    });

    // ----- Power plan segmented control -----
    const planButtons = Array.from(document.querySelectorAll('#plan-control button'));
    const pill = document.getElementById('plan-pill');
    const overrideChip = document.getElementById('manual-override-chip');
    const overrideLabel = document.getElementById('manual-override-label');
    const clearOverrideBtn = document.getElementById('btn-clear-manual-override');
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

    Host.on('activePlanChanged', (data) => {
        reflectPlan(data.plan ? data.plan : null);
    });

    Host.on('automationStateChanged', (data) => {
        renderOverrideStatus(data.override);
    });

    Host.on('manualOverrideChanged', (data) => {
        renderOverrideStatus(data.override);
    });

    document.addEventListener('langchanged', () => {
        renderOverrideStatus(activeOverride);
    });

    // Initial active plan.
    if (Host.available) {
        Host.call('getActivePlan').then(p => {
            if (p && p.planId) reflectPlan(p.planId);
        }).catch(() => {});
        Host.call('getSettings').then(res => {
            if (res && res.settings) renderOverrideStatus(res.settings.override);
        }).catch(() => {});
    }
})();
