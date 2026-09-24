(function () {
    'use strict';
    if (!window.Host || !Host.available) return;

    const $ = id => document.getElementById(id);
    let state = null;
    let wired = false;

    function t(key) {
        return window.VoltUiReorg?.t ? VoltUiReorg.t(key) : key;
    }

    function feedback(key, error) {
        const node = $('lan-remote-feedback');
        if (!node) return;
        node.textContent = key ? t(key) : '';
        node.dataset.error = error ? 'true' : 'false';
    }

    function render(next) {
        if (!next) return;
        state = next;
        const enabled = $('lan-remote-enabled');
        if (enabled) enabled.checked = !!next.enabled;
        const status = $('lan-remote-status');
        if (status) status.textContent = t(next.running ? 'remote_status_running' : (next.enabled ? 'remote_status_waiting' : 'remote_status_disabled'));
        $('lan-remote-status-dot')?.classList.toggle('is-running', !!next.running);
        const url = next.urls?.[0] || '--';
        if ($('lan-remote-url')) $('lan-remote-url').textContent = url;
        if ($('lan-remote-port')) $('lan-remote-port').textContent = String(next.port || 51737);
        if ($('lan-remote-fingerprint')) $('lan-remote-fingerprint').textContent = next.tlsFingerprintSha256 || '--';
        if ($('lan-remote-pin-status')) $('lan-remote-pin-status').textContent = next.hasPin ? '••••' : t('remote_pin_missing');
        if ($('lan-remote-perm-plan')) $('lan-remote-perm-plan').checked = !!next.allowPlanChange;
        if ($('lan-remote-perm-shutdown')) $('lan-remote-perm-shutdown').checked = !!next.allowShutdown;
        if ($('lan-remote-perm-restart')) $('lan-remote-perm-restart').checked = !!next.allowRestart;
    }

    async function refresh() {
        try {
            render(await Host.call('getLanRemoteControlState'));
            feedback('');
        } catch (error) {
            feedback('remote_error', true);
            console.error('getLanRemoteControlState failed', error);
        }
    }

    function showGenerated(pin) {
        if (!pin) return;
        const panel = $('lan-remote-generated-panel');
        const code = $('lan-remote-generated-pin');
        if (code) code.textContent = pin;
        panel?.classList.remove('hidden');
    }

    async function savePermissions() {
        try {
            const next = await Host.call('setLanRemoteControlPermissions', {
                allowPlanChange: !!$('lan-remote-perm-plan')?.checked,
                allowShutdown: !!$('lan-remote-perm-shutdown')?.checked,
                allowRestart: !!$('lan-remote-perm-restart')?.checked
            });
            render(next);
            feedback('remote_saved');
        } catch (error) {
            render(state);
            feedback('remote_error', true);
        }
    }

    async function setEnabled() {
        const desired = !!$('lan-remote-enabled')?.checked;
        try {
            const result = await Host.call('setLanRemoteControlEnabled', { enabled: desired });
            render(result?.state || result);
            if (result?.generatedPin) showGenerated(result.generatedPin);
            feedback(desired ? 'remote_started' : 'remote_stopped');
        } catch (error) {
            render(state);
            feedback('remote_error', true);
        }
    }

    async function generatePin() {
        try {
            const result = await Host.call('generateLanRemoteControlPin');
            render(result?.state);
            showGenerated(result?.pin);
            feedback('remote_pin_replaced');
        } catch (error) {
            feedback('remote_error', true);
        }
    }

    async function setPin() {
        const input = $('lan-remote-pin-input');
        const pin = input?.value || '';
        if (!/^[0-9]{4}$/.test(pin)) {
            feedback('remote_pin_invalid', true);
            input?.focus();
            return;
        }
        try {
            render(await Host.call('setLanRemoteControlPin', { pin }));
            input.value = '';
            $('lan-remote-generated-panel')?.classList.add('hidden');
            feedback('remote_pin_replaced');
        } catch (error) {
            feedback('remote_error', true);
        }
    }

    async function copyValue(value) {
        if (!value || value === '--') return;
        try {
            await navigator.clipboard.writeText(value);
            feedback('remote_copied');
        } catch { feedback('remote_copy_failed', true); }
    }

    function wire() {
        if (wired || !$('lan-remote-enabled')) return;
        wired = true;
        $('lan-remote-enabled')?.addEventListener('change', setEnabled);
        ['lan-remote-perm-plan', 'lan-remote-perm-shutdown', 'lan-remote-perm-restart'].forEach(id => $(id)?.addEventListener('change', savePermissions));
        $('lan-remote-generate-pin')?.addEventListener('click', generatePin);
        $('lan-remote-set-pin')?.addEventListener('click', setPin);
        $('lan-remote-copy-url')?.addEventListener('click', () => copyValue($('lan-remote-url')?.textContent));
        $('lan-remote-copy-fingerprint')?.addEventListener('click', () => copyValue($('lan-remote-fingerprint')?.textContent));
        $('lan-remote-copy-pin')?.addEventListener('click', () => copyValue($('lan-remote-generated-pin')?.textContent));
        $('lan-remote-pin-input')?.addEventListener('input', event => { event.target.value = event.target.value.replace(/[^0-9]/g, '').slice(0, 4); });
    }

    document.addEventListener('voltuiviewchanged', event => {
        if (event.detail?.view !== 'remote-control') return;
        wire();
        refresh();
    });
    document.addEventListener('langchanged', () => { if (state) render(state); });
})();
