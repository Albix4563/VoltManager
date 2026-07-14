/**
 * Overview and top-bar status projection.
 * Reads the existing live DOM only; it does not add hardware or battery polling.
 */
(function () {
    'use strict';

    const api = window.VoltUiReorg = window.VoltUiReorg || {};
    const $ = id => document.getElementById(id);
    let queued = false;

    function hidden(node) {
        return !node || node.classList.contains('hidden') ||
            node.getAttribute('aria-hidden') === 'true' ||
            getComputedStyle(node).display === 'none';
    }

    function set(id, value) {
        const node = $(id);
        if (node && node.textContent !== value) node.textContent = value;
    }

    function percentage(value) {
        const match = String(value || '').replace(',', '.').match(/-?\d+(?:\.\d+)?/);
        return match ? Math.max(0, Math.min(100, Number(match[0]))) : 0;
    }

    function planName() {
        const buttons = Array.from(document.querySelectorAll('#plan-control [data-plan]'));
        const active = buttons.find(button =>
            button.classList.contains('text-secondary-container') ||
            button.getAttribute('aria-pressed') === 'true' ||
            button.dataset.active === 'true');
        if (active) return active.textContent.trim();

        const settings = window.__voltSettings?.get?.();
        if (settings?.override?.plan) return String(settings.override.plan);
        return '--';
    }

    function powerSource() {
        const status = ($('power-flow-status')?.textContent || '').toLowerCase();
        const icon = $('power-flow-status-icon')?.textContent || '';
        if (/charg|ac|plug|corrente|cargando|充电/.test(status) || /charging|power/.test(icon)) {
            return api.t('state_ac');
        }
        if (/discharg|battery|batteria|descarg|放电/.test(status) || /battery/.test(icon)) {
            return api.t('state_battery');
        }
        return status || api.t('state_unknown');
    }

    function monitoring() {
        const label = $('monitoring-label')?.textContent.trim();
        if (label) return label;
        return $('master-toggle')?.checked ? api.t('state_active') : api.t('state_inactive');
    }

    function metric(source, target, meter) {
        const value = $(source)?.textContent.trim() || '--';
        set(target, value);
        if ($(meter)) $(meter).style.width = percentage(value) + '%';
    }

    function activeRules() {
        return ['rule-saver-toggle', 'rule-balanced-toggle', 'rule-performance-toggle']
            .map($).filter(toggle => toggle?.checked).length;
    }

    function appProfileActive() {
        const content = $('app-power-profile-mount')?.textContent.replace(/\s+/g, ' ').trim() || '';
        return /active|attiv|activo|启用|running|in esecuzione/i.test(content);
    }

    function keepAwakeActive() {
        const mount = $('keep-awake-mount');
        const input = mount?.querySelector('input[type="checkbox"]');
        const control = mount?.querySelector('[data-on], [data-state], [aria-pressed]');
        return !!(input?.checked ||
            control?.dataset.on === 'true' ||
            control?.dataset.state === 'on' ||
            control?.getAttribute('aria-pressed') === 'true');
    }

    function syncQuickButtons(plan) {
        document.querySelectorAll('[data-vm-action]').forEach(button =>
            button.classList.remove('active'));

        const value = plan.toLowerCase();
        let action = null;
        if (/saver|risparm|ahorro|节能/.test(value)) action = 'saver';
        else if (/balanc|equilibr|平衡/.test(value)) action = 'balanced';
        else if (/perform|prestaz|rendimiento|高性能/.test(value)) action = 'performance';
        document.querySelector(`[data-vm-action="${action}"]`)?.classList.add('active');

        document.querySelector('[data-vm-action="gaming"]')?.classList.toggle(
            'active', !!window.__voltGamingMode?.isActive?.());
        document.querySelector('[data-vm-action="keep-awake"]')?.classList.toggle(
            'active', keepAwakeActive());
    }

    function sync() {
        if (!api.ready) return;

        const plan = planName();
        const overrideChip = $('manual-override-chip');
        const override = overrideChip && !hidden(overrideChip)
            ? ($('manual-override-label')?.textContent.trim() || api.t('state_active'))
            : api.t('state_none');
        const automation = $('master-toggle')?.checked && hidden(overrideChip)
            ? api.t('state_active') : api.t('state_inactive');

        set('ov-status-plan', plan);
        set('ov-status-power', powerSource());
        set('ov-status-monitoring', monitoring());
        set('ov-status-override', override);
        set('ov-status-automation', automation);
        set('vm-top-plan', plan);
        set('vm-top-automation', automation);

        const battery = $('power-flow-percent')?.textContent.trim() || '';
        if (battery && battery !== '--' && battery !== '--%') {
            set('vm-top-battery', battery);
            $('vm-top-battery-chip')?.classList.remove('hidden');
        } else {
            $('vm-top-battery-chip')?.classList.add('hidden');
        }

        metric('cpu-pct', 'ov-metric-cpu', 'ov-meter-cpu');
        metric('gpu-pct', 'ov-metric-gpu', 'ov-meter-gpu');
        metric('ram-pct', 'ov-metric-ram', 'ov-meter-ram');
        metric('disk-pct', 'ov-metric-disk', 'ov-meter-disk');

        const rules = activeRules();
        const rulesLabel = rules === 1
            ? api.t('rules_count_one')
            : api.t('rules_count', { count: rules });
        set('ov-auto-rules', rulesLabel);
        set('vm-rules-count', rulesLabel);
        set('ov-auto-profile', appProfileActive()
            ? api.t('profile_detected') : api.t('no_profile'));
        set('ov-auto-gaming', window.__voltGamingMode?.isActive?.()
            ? api.t('state_active') : api.t('state_inactive'));
        set('ov-auto-scheduled', $('schedule-active') && !hidden($('schedule-active'))
            ? api.t('scheduled_yes') : api.t('scheduled_no'));

        syncQuickButtons(plan);
    }

    function queue() {
        if (queued) return;
        queued = true;
        requestAnimationFrame(() => {
            queued = false;
            sync();
        });
    }

    function observe() {
        const ids = [
            'plan-control', 'manual-override-chip', 'monitoring-label',
            'cpu-pct', 'gpu-pct', 'ram-pct', 'disk-pct',
            'power-flow-percent', 'power-flow-status', 'schedule-active',
            'master-toggle', 'app-power-profile-mount', 'keep-awake-mount'
        ];
        const observer = new MutationObserver(queue);
        ids.map($).filter(Boolean).forEach(node => observer.observe(node, {
            attributes: true,
            childList: true,
            subtree: true,
            characterData: true
        }));
        document.addEventListener('change', queue);
        document.addEventListener('gamingmodechanged', queue);
        document.addEventListener('settingsloaded', queue);
        document.addEventListener('voltuiviewchanged', queue);
        document.addEventListener('voltuisubviewchanged', queue);
        document.addEventListener('voltuistranslated', queue);
        setInterval(queue, 5000);
        queue();
    }

    if (api.ready) observe();
    else document.addEventListener('voltuiready', observe, { once: true });
})();
