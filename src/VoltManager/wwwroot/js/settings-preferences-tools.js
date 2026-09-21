/** Settings preferences and maintenance tools owned by the Settings route. */
(function () {
    if (!window.Host || !Host.available || !window.VoltViewLifecycle || !window.VoltSettingsCore) return;

    const core = window.VoltSettingsCore;
    const disposers = [];
    let initialized = false;

    function listen(target, type, handler, options) {
        if (!target) return;
        target.addEventListener(type, handler, options);
        disposers.push(() => target.removeEventListener(type, handler, options));
    }

    function currentSettings() {
        const api = window.__voltSettings;
        if (!api) return null;
        return api.get ? api.get() : api;
    }

    function syncPreferences() {
        const api = window.__voltSettings;
        const settings = currentSettings();
        if (!api || !settings) return;
        core.setToggle(document.getElementById('toggle-autostart'), !!api.startWithWindows);
        core.setToggle(document.getElementById('toggle-tray'), !!settings.closeToTray);
    }

    async function onAutostart() {
        const toggle = document.getElementById('toggle-autostart');
        const enable = toggle?.dataset.on !== 'true';
        core.setToggle(toggle, enable);
        try {
            const res = await Host.call('setStartWithWindows', { enabled: enable });
            if (res && res.success === false) {
                core.setToggle(toggle, !enable);
                core.setStatus(core.tr('msg_err', core.lt('err')) + (res.message || ''), true);
            }
        } catch (err) {
            core.setToggle(toggle, !enable);
            Host.fail(err, msg => core.setStatus(core.tr('msg_err', core.lt('err')) + msg, true));
        }
    }

    async function onTray() {
        const toggle = document.getElementById('toggle-tray');
        const enable = toggle?.dataset.on !== 'true';
        core.setToggle(toggle, enable);
        try {
            await Host.call('setCloseToTray', { enabled: enable });
            const settings = currentSettings();
            if (settings) settings.closeToTray = enable;
        } catch (err) {
            core.setToggle(toggle, !enable);
            Host.fail(err, msg => core.setStatus(core.tr('msg_err', core.lt('err')) + msg, true));
        }
    }

    async function onExportSettings() {
        try { await Host.call('exportSettings'); }
        catch (err) { Host.fail(err, msg => core.setStatus(core.tr('msg_err', core.lt('err')) + msg, true)); }
    }

    async function onImportSettings() {
        try {
            const res = await Host.call('importSettings');
            if (res && res.success) location.reload();
        } catch (err) {
            Host.fail(err, msg => core.setStatus(core.tr('msg_err', core.lt('err')) + msg, true));
        }
    }

    function diagMsg(key, fallback) {
        return window.I18n && I18n.t ? I18n.t(key) : fallback;
    }

    async function onExportDiagnostics() {
        const status = document.getElementById('update-status');
        try {
            const res = await Host.call('exportDiagnostics');
            if (!res || res.cancelled) {
                if (status) status.textContent = diagMsg('set_diagnostics_cancelled', 'Export cancelled.');
                return;
            }
            if (res.success) {
                if (status) status.textContent = diagMsg('set_diagnostics_ok', 'Diagnostics exported.') + (res.path ? ' ' + res.path : '');
            } else if (status) {
                status.textContent = diagMsg('set_diagnostics_fail', 'Could not export diagnostics.');
            }
        } catch (err) {
            console.error('exportDiagnostics failed', err);
            if (status) status.textContent = diagMsg('set_diagnostics_fail', 'Could not export diagnostics.');
        }
    }

    async function onOpenLogs() {
        const status = document.getElementById('update-status');
        try {
            const res = await Host.call('openLogFolder');
            if (status) {
                status.textContent = res && res.success
                    ? diagMsg('set_logs_ok', 'Log folder opened.')
                    : diagMsg('set_logs_fail', 'Could not open log folder.') + (res && res.error ? ' ' + res.error : '');
            }
        } catch (err) {
            console.error('openLogFolder failed', err);
            if (status) status.textContent = diagMsg('set_logs_fail', 'Could not open log folder.');
        }
    }

    function init() {
        if (initialized) return;
        initialized = true;
        listen(document, 'settingsloaded', syncPreferences);
        listen(document.getElementById('pref-autostart'), 'click', onAutostart);
        listen(document.getElementById('pref-tray'), 'click', onTray);
        listen(document.getElementById('btn-export-settings'), 'click', onExportSettings);
        listen(document.getElementById('btn-import-settings'), 'click', onImportSettings);
        listen(document.getElementById('btn-export-diagnostics'), 'click', onExportDiagnostics);
        listen(document.getElementById('btn-open-logs'), 'click', onOpenLogs);
        syncPreferences();
    }

    function dispose() {
        while (disposers.length) {
            try { disposers.pop()(); } catch (_) {}
        }
        initialized = false;
    }

    VoltViewLifecycle.register('settings-preferences-tools', {
        matches: route => route.view === 'settings',
        init,
        activate: syncPreferences,
        deactivate() {},
        dispose,
    });
})();