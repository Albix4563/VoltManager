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
                gpuName.textContent = 'Contatori GPU non disponibili';
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
    const planOrder = ['powerSaver', 'balanced', 'performance'];
    let switching = false;

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

    planButtons.forEach((btn) => {
        btn.addEventListener('click', async () => {
            if (switching) return;
            switching = true;
            const previous = planButtons.find(b => b.classList.contains('text-secondary-container'));
            reflectPlan(btn.dataset.plan); // optimistic
            try {
                const res = await Host.call('setActivePlan', { plan: btn.dataset.plan });
                if (!res.success && previous) reflectPlan(previous.dataset.plan);
            } catch (err) {
                console.error('setActivePlan failed', err);
                if (previous) reflectPlan(previous.dataset.plan);
            } finally {
                switching = false;
            }
        });
    });

    Host.on('activePlanChanged', (data) => {
        reflectPlan(data.plan ? data.plan : null);
    });

    // Initial active plan.
    if (Host.available) {
        Host.call('getActivePlan').then(p => {
            if (p && p.planId) reflectPlan(p.planId);
        }).catch(() => {});
    }
})();
