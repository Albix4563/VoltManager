/**
 * Settings & Info: GitHub updates + changelog + preferences toggles.
 */
(function () {
    if (!Host.available) return;

    const btnCheck = document.getElementById('btn-check-updates');
    const btnDownload = document.getElementById('btn-download-update');
    const btnDownloadLabel = document.getElementById('btn-download-label');
    const statusEl = document.getElementById('update-status');

    let downloadUrl = null;
    let _updateInfo = null;
    let modalActionsMounted = false;
    let autoUpdatesWired = false;

    const localText = {
        it: {
            autoUpdates: 'Autoricerca aggiornamenti',
            autoUpdatesSub: 'Controlla automaticamente nuove versioni ogni 30 minuti',
            snoozeFor: 'Rimanda di',
            snooze: 'Rimanda',
            skip: 'Salta versione',
            snoozed: 'Aggiornamento rimandato.',
            skipped: 'Questa versione verrà saltata.',
            min15: '15 minuti', min30: '30 minuti', hour1: '1 ora', hours2: '2 ore'
        },
        en: {
            autoUpdates: 'Automatic update checks',
            autoUpdatesSub: 'Automatically checks for new versions every 30 minutes',
            snoozeFor: 'Snooze for',
            snooze: 'Snooze',
            skip: 'Skip version',
            snoozed: 'Update postponed.',
            skipped: 'This version will be skipped.',
            min15: '15 minutes', min30: '30 minutes', hour1: '1 hour', hours2: '2 hours'
        }
    };

    function lang() {
        return window.I18n && I18n.getLang ? I18n.getLang() : 'it';
    }

    function lt(key) {
        const l = lang();
        return (localText[l] && localText[l][key]) || localText.it[key] || key;
    }

    function tr(key, fallback) {
        if (!window.I18n || !I18n.t) return fallback;
        const value = I18n.t(key);
        return value === key ? fallback : value;
    }

    function normalizeUpdateInfo(info) {
        const normalized = Object.assign({}, info || {});
        normalized.currentVersion = normalized.currentVersion || normalized.version || '';
        normalized.latestVersion = normalized.latestVersion || normalized.newVersion || normalized.targetVersion || '';
        return normalized;
    }

    function formatVersion(ver) {
        const value = ver == null ? '' : String(ver).trim();
        if (!value || value === '?') return 'N/D';
        return value.toLowerCase().startsWith('v') ? value : 'v' + value;
    }

    function normalizeVersion(ver) {
        return ver == null ? '' : String(ver).trim().replace(/^[vV]/, '');
    }

    function esc(s) {
        const div = document.createElement('div');
        div.textContent = s == null ? '' : String(s);
        return div.innerHTML;
    }

    function setDownloadButtonVisible(visible) {
        btnDownload.classList.toggle('hidden', !visible);
        btnDownload.classList.toggle('flex', visible);
    }

    function setStatus(text, isError) {
        statusEl.textContent = text;
        statusEl.classList.remove('hidden', 'ok', 'err');
        statusEl.classList.add(isError ? 'err' : 'ok');
    }

    function mountUpdateModalActions() {
        if (modalActionsMounted) return;
        const dismiss = document.getElementById('upd-modal-btn-dismiss');
        const footer = dismiss?.parentElement;
        if (!footer) return;

        footer.insertAdjacentHTML('afterbegin',
            '<div id="upd-modal-snooze-wrap" class="mr-auto flex flex-wrap items-center gap-2">' +
            '  <span class="text-label-md text-on-surface-variant" id="upd-modal-snooze-label"></span>' +
            '  <select id="upd-modal-snooze-minutes" class="bg-surface-container-low/50 text-secondary-container font-medium border border-white/10 rounded-lg py-2 px-3 text-label-md focus:outline-none focus:border-secondary-container">' +
            '    <option value="15" id="upd-modal-snooze-15"></option>' +
            '    <option value="30" selected id="upd-modal-snooze-30"></option>' +
            '    <option value="60" id="upd-modal-snooze-60"></option>' +
            '    <option value="120" id="upd-modal-snooze-120"></option>' +
            '  </select>' +
            '  <button id="upd-modal-btn-snooze" class="btn-ghost rounded-lg py-2.5 px-4 text-label-md" type="button"></button>' +
            '</div>');

        dismiss.insertAdjacentHTML('beforebegin',
            '<button id="upd-modal-btn-skip" class="btn-ghost rounded-lg py-2.5 px-4 text-label-md" type="button"></button>');

        document.getElementById('upd-modal-btn-snooze')?.addEventListener('click', snoozeUpdateFromModal);
        document.getElementById('upd-modal-btn-skip')?.addEventListener('click', skipUpdateFromModal);
        modalActionsMounted = true;
        refreshUpdateModalLabels();
    }

    function refreshUpdateModalLabels() {
        const map = {
            'upd-modal-snooze-label': lt('snoozeFor'),
            'upd-modal-btn-snooze': tr('upd_modal_snooze', lt('snooze')),
            'upd-modal-btn-skip': tr('upd_modal_skip', lt('skip')),
            'upd-modal-snooze-15': lt('min15'),
            'upd-modal-snooze-30': lt('min30'),
            'upd-modal-snooze-60': lt('hour1'),
            'upd-modal-snooze-120': lt('hours2')
        };
        Object.entries(map).forEach(([id, text]) => {
            const el = document.getElementById(id);
            if (el) el.textContent = text;
        });
    }

    function setModalActionsDisabled(disabled) {
        ['upd-modal-btn-install', 'upd-modal-btn-dismiss', 'upd-modal-btn-snooze', 'upd-modal-btn-skip', 'upd-modal-snooze-minutes']
            .forEach(id => {
                const el = document.getElementById(id);
                if (el) el.disabled = disabled;
            });
    }

    function openUpdateModal(info) {
        info = normalizeUpdateInfo(info);
        _updateInfo = info;
        downloadUrl = info && info.downloadUrl ? info.downloadUrl : downloadUrl;
        const overlay = document.getElementById('update-modal-overlay');
        if (!overlay) return;
        mountUpdateModalActions();
        refreshUpdateModalLabels();

        const curBadge  = document.getElementById('upd-modal-cur-ver');
        const newBadge  = document.getElementById('upd-modal-new-ver');
        const notesEl   = document.getElementById('upd-modal-notes');
        const progWrap  = document.getElementById('upd-modal-progress-wrap');
        const progBar   = document.getElementById('upd-modal-bar');
        const progLabel = document.getElementById('upd-modal-prog-label');
        const stateMsg  = document.getElementById('upd-modal-state-msg');
        const btnInstall= document.getElementById('upd-modal-btn-install');
        const btnDismiss= document.getElementById('upd-modal-btn-dismiss');

        if (curBadge)  curBadge.textContent  = formatVersion(info.currentVersion);
        if (newBadge)  newBadge.textContent  = formatVersion(info.latestVersion);
        if (progWrap)  progWrap.classList.add('hidden');
        if (progBar)   progBar.style.width = '0%';
        if (progLabel) progLabel.textContent = '0%';
        if (stateMsg)  stateMsg.classList.add('hidden');
        if (btnInstall) {
            btnInstall.disabled = false;
            btnInstall.innerHTML = '<span class="material-symbols-outlined text-[16px]">download</span>' + esc(I18n.t('upd_modal_btn_install'));
        }
        if (btnDismiss) btnDismiss.textContent = I18n.t('upd_modal_btn_later');
        setModalActionsDisabled(false);

        if (notesEl) {
            let html = '';
            if (info && info.releaseNotes)
                html += '<div class="text-body-sm text-on-surface-variant whitespace-pre-line leading-relaxed">' + esc(info.releaseNotes) + '</div>';
            if (info && info.commits && info.commits.length) {
                html += '<ul class="mt-3 space-y-1 text-label-md text-on-surface-variant list-disc pl-4">';
                info.commits.slice(0, 8).forEach(c => {
                    html += '<li><span class="text-secondary-fixed-dim font-mono">' + esc(c.sha) + '</span> ' + esc(c.message) + '</li>';
                });
                html += '</ul>';
            }
            notesEl.innerHTML = html || '<p class="text-label-md text-on-surface-variant opacity-60">' + esc(I18n.t('msg_no_info')) + '</p>';
        }

        overlay.classList.remove('hidden');
        overlay.classList.add('flex');
    }

    function closeUpdateModal() {
        const overlay = document.getElementById('update-modal-overlay');
        if (!overlay) return;
        overlay.classList.add('hidden');
        overlay.classList.remove('flex');
    }

    async function snoozeUpdateFromModal() {
        const select = document.getElementById('upd-modal-snooze-minutes');
        const minutes = parseInt(select?.value || '30', 10) || 30;
        setModalActionsDisabled(true);
        try {
            await Host.call('snoozeUpdate', { minutes });
            closeUpdateModal();
            setStatus(tr('msg_update_snoozed', lt('snoozed')), false);
        } catch (err) {
            setStatus(I18n.t('msg_err') + err.message, true);
            setModalActionsDisabled(false);
        }
    }

    async function skipUpdateFromModal() {
        const version = normalizeVersion(_updateInfo && _updateInfo.latestVersion);
        if (!version) return;
        setModalActionsDisabled(true);
        try {
            await Host.call('skipUpdateVersion', { version });
            downloadUrl = null;
            setDownloadButtonVisible(false);
            closeUpdateModal();
            setStatus(tr('msg_update_skipped', lt('skipped')), false);
        } catch (err) {
            setStatus(I18n.t('msg_err') + err.message, true);
            setModalActionsDisabled(false);
        }
    }

    document.addEventListener('DOMContentLoaded', () => {
        const btnInstall = document.getElementById('upd-modal-btn-install');
        const btnDismiss = document.getElementById('upd-modal-btn-dismiss');
        if (btnInstall) btnInstall.addEventListener('click', doDownloadAndInstall);
        if (btnDismiss) btnDismiss.addEventListener('click', closeUpdateModal);
        mountUpdateModalActions();
    });

    async function doDownloadAndInstall() {
        if (!downloadUrl) return;
        const progWrap  = document.getElementById('upd-modal-progress-wrap');
        const progBar   = document.getElementById('upd-modal-bar');
        const progLabel = document.getElementById('upd-modal-prog-label');
        const stateMsg  = document.getElementById('upd-modal-state-msg');

        if (progWrap)  progWrap.classList.remove('hidden');
        setModalActionsDisabled(true);
        if (progLabel) progLabel.textContent = I18n.t('msg_dl_prog') + '0%';

        try {
            await Host.call('downloadUpdate', { url: downloadUrl });
            if (stateMsg) {
                stateMsg.textContent = I18n.t('upd_modal_installing');
                stateMsg.classList.remove('hidden');
            }
            if (progBar) progBar.style.width = '100%';
        } catch (err) {
            setModalActionsDisabled(false);
            setStatus(I18n.t('msg_dl_fail') + err.message, true);
        }
    }

    btnCheck.addEventListener('click', async () => {
        btnCheck.disabled = true;
        const icon = btnCheck.querySelector('.material-symbols-outlined');
        icon.classList.add('spinning');
        icon.textContent = 'progress_activity';
        setStatus(I18n.t('msg_check_update'), false);
        try {
            const info = normalizeUpdateInfo(await Host.call('checkForUpdates'));
            _updateInfo = info;
            if (info.status === 'ok') {
                setStatus(info.message, false);
                if (info.updateAvailable && info.downloadUrl) {
                    downloadUrl = info.downloadUrl;
                    setDownloadButtonVisible(true);
                    btnDownloadLabel.textContent = I18n.t('msg_dl_install') + formatVersion(info.latestVersion);
                } else {
                    downloadUrl = null;
                    setDownloadButtonVisible(false);
                }
            } else {
                setStatus(info.message || I18n.t('msg_check_err'), true);
                downloadUrl = null;
                setDownloadButtonVisible(false);
            }
        } catch (err) {
            setStatus(I18n.t('msg_err') + err.message, true);
        } finally {
            btnCheck.disabled = false;
            icon.classList.remove('spinning');
            icon.textContent = 'download';
        }
    });

    Host.on('updateDownloadProgress', (data) => {
        const progBar   = document.getElementById('upd-modal-bar');
        const progLabel = document.getElementById('upd-modal-prog-label');
        if (progBar)   progBar.style.width = data.pct + '%';
        if (progLabel) progLabel.textContent = I18n.t('msg_dl_prog') + data.pct + '%';
        btnDownloadLabel.textContent = I18n.t('msg_dl_prog') + data.pct + '%';
    });

    Host.on('updateAvailable', (info) => {
        info = normalizeUpdateInfo(info);
        if (!info || !info.downloadUrl) return;
        _updateInfo = info;
        downloadUrl = info.downloadUrl;
        openUpdateModal(info);
    });

    Host.on('appUpdated', (data) => {
        const ver = (data && data.version) ? data.version : '';
        showUpdatedToast(ver);
    });

    function showUpdatedToast(ver) {
        if (document.getElementById('updated-toast')) return;
        const toast = document.createElement('div');
        toast.id = 'updated-toast';
        toast.style.cssText =
            'position:fixed;bottom:24px;right:24px;z-index:2000;' +
            'background:#1E2A4A;border:1px solid rgba(0,241,254,0.4);border-radius:12px;' +
            'padding:14px 18px;box-shadow:0 8px 32px rgba(0,0,0,0.6);color:#e2e8f0;' +
            'font-size:13px;display:flex;align-items:center;gap:12px;' +
            'animation:slideInRight 0.3s ease;';
        toast.innerHTML =
            '<span style="color:#00f1fe;font-size:18px;">✓</span>' +
            '<span>' + esc(I18n.t('upd_toast_msg')) + (ver ? ' v' + esc(ver) : '') + '</span>' +
            '<button onclick="this.parentElement.remove()" style="background:none;border:none;color:#94a3b8;cursor:pointer;font-size:16px;margin-left:4px;">×</button>';
        document.body.appendChild(toast);
        setTimeout(() => { if (toast.parentElement) toast.remove(); }, 6000);
    }

    btnDownload.addEventListener('click', () => {
        if (!downloadUrl) return;
        openUpdateModal(_updateInfo || { downloadUrl });
    });

    const toggleAutostart = document.getElementById('toggle-autostart');
    const toggleTray = document.getElementById('toggle-tray');

    function setToggle(el, on) {
        if (el) el.dataset.on = on ? 'true' : 'false';
    }

    function normalizeAutoUpdates(settings) {
        if (!settings.autoUpdates) {
            settings.autoUpdates = { enabled: true, intervalMinutes: 30, snoozedUntilUtc: null, skippedVersion: null };
        }
        if (!Number.isFinite(settings.autoUpdates.intervalMinutes) || settings.autoUpdates.intervalMinutes < 5) {
            settings.autoUpdates.intervalMinutes = 30;
        }
        return settings.autoUpdates;
    }

    function mountAutoUpdateUi() {
        if (document.getElementById('pref-auto-updates')) return;
        const tray = document.getElementById('pref-tray');
        if (!tray) return;
        tray.insertAdjacentHTML('afterend',
            '<div class="flex items-center justify-between group cursor-pointer" id="pref-auto-updates">' +
            '  <div>' +
            '    <p class="text-body-md text-on-surface group-hover:text-secondary-fixed transition-colors" id="pref-auto-updates-title"></p>' +
            '    <p class="text-label-sm text-on-surface-variant" id="pref-auto-updates-sub"></p>' +
            '  </div>' +
            '  <div class="mini-toggle" data-on="true" id="toggle-auto-updates"><div class="mini-toggle-knob"></div></div>' +
            '</div>');
        refreshAutoUpdateLabels();
    }

    function refreshAutoUpdateLabels() {
        const title = document.getElementById('pref-auto-updates-title');
        const sub = document.getElementById('pref-auto-updates-sub');
        if (title) title.textContent = lt('autoUpdates');
        if (sub) sub.textContent = lt('autoUpdatesSub');
    }

    function wireAutoUpdateUi() {
        if (autoUpdatesWired) return;
        document.addEventListener('click', async (e) => {
            const pref = e.target.closest('#pref-auto-updates');
            if (!pref || !window.__voltSettings) return;

            const toggle = document.getElementById('toggle-auto-updates');
            const enable = toggle?.dataset.on !== 'true';
            setToggle(toggle, enable);
            normalizeAutoUpdates(window.__voltSettings.get()).enabled = enable;
            try {
                await Host.call('setAutoUpdateChecks', { enabled: enable });
            } catch {
                setToggle(toggle, !enable);
                normalizeAutoUpdates(window.__voltSettings.get()).enabled = !enable;
            }
        });
        autoUpdatesWired = true;
    }

    function normalizeAutoShutdownSettings(settings) {
        if (!settings.autoShutdown) {
            settings.autoShutdown = { enabled: false, action: 'shutdown', time: '23:00', lastTriggeredLocalDate: null };
        }
        if (!/^[0-9]{2}:[0-9]{2}$/.test(settings.autoShutdown.time || '')) {
            settings.autoShutdown.time = '23:00';
        }
        return settings.autoShutdown;
    }

    function mountAutoShutdownUi() {
        if (document.getElementById('auto-shutdown-panel')) return;
        const prefs = document.getElementById('pref-tray')?.parentElement;
        if (!prefs) return;

        prefs.insertAdjacentHTML('beforeend',
            '<div class="space-y-sm pt-md border-t border-white/10" id="auto-shutdown-panel">' +
            '  <div class="flex items-center justify-between group">' +
            '    <div>' +
            '      <p class="text-body-md text-on-surface group-hover:text-secondary-fixed transition-colors" data-i18n="set_pref_autoshutdown">Autospegnimento</p>' +
            '      <p class="text-label-sm text-on-surface-variant" data-i18n="set_pref_autoshutdown_sub">Spegne il PC all\'orario indicato, se è acceso</p>' +
            '    </div>' +
            '    <div class="mini-toggle cursor-pointer" data-on="false" id="toggle-auto-shutdown"><div class="mini-toggle-knob"></div></div>' +
            '  </div>' +
            '  <label class="flex items-center justify-between gap-md" for="auto-shutdown-time">' +
            '    <span class="text-label-sm text-on-surface-variant" data-i18n="set_pref_autoshutdown_time">Orario</span>' +
            '    <input id="auto-shutdown-time" type="time" class="bg-surface-container-low/50 text-secondary-container font-medium border border-white/10 rounded-lg py-2 px-3 text-body-md focus:outline-none focus:border-secondary-container transition-all duration-300" />' +
            '  </label>' +
            '  <p class="text-label-sm text-on-surface-variant opacity-70" data-i18n="set_pref_autoshutdown_note">Non forza la chiusura delle app con lavoro non salvato.</p>' +
            '</div>');
        I18n.apply();
    }

    function setAutoShutdownUi(autoShutdown) {
        const toggle = document.getElementById('toggle-auto-shutdown');
        const timeInput = document.getElementById('auto-shutdown-time');
        if (!toggle || !timeInput) return;

        setToggle(toggle, autoShutdown.enabled);
        timeInput.value = autoShutdown.time;
        timeInput.disabled = !autoShutdown.enabled;
        timeInput.classList.toggle('opacity-50', !autoShutdown.enabled);
    }

    function wireAutoShutdownUi() {
        const toggle = document.getElementById('toggle-auto-shutdown');
        const timeInput = document.getElementById('auto-shutdown-time');
        if (!toggle || !timeInput || !window.__voltSettings || toggle.dataset.wired === 'true') return;
        toggle.dataset.wired = 'true';
        timeInput.dataset.wired = 'true';

        toggle.addEventListener('click', () => {
            const settings = window.__voltSettings.get();
            const autoShutdown = normalizeAutoShutdownSettings(settings);
            autoShutdown.enabled = toggle.dataset.on !== 'true';
            setAutoShutdownUi(autoShutdown);
            window.__voltSettings.save();
        });

        timeInput.addEventListener('change', (e) => {
            const value = e.target.value;
            if (!/^[0-9]{2}:[0-9]{2}$/.test(value)) return;
            const settings = window.__voltSettings.get();
            const autoShutdown = normalizeAutoShutdownSettings(settings);
            autoShutdown.time = value;
            setAutoShutdownUi(autoShutdown);
            window.__voltSettings.save();
        });
    }

    document.addEventListener('settingsloaded', () => {
        const s = window.__voltSettings;
        setToggle(toggleAutostart, s.startWithWindows);
        setToggle(toggleTray, s.get().closeToTray);

        mountAutoUpdateUi();
        setToggle(document.getElementById('toggle-auto-updates'), normalizeAutoUpdates(s.get()).enabled);
        wireAutoUpdateUi();

        mountAutoShutdownUi();
        setAutoShutdownUi(normalizeAutoShutdownSettings(s.get()));
        wireAutoShutdownUi();

        const langSelect = document.getElementById('lang-select');
        langSelect.value = I18n.getLang();
        if (langSelect.dataset.wired !== 'true') {
            langSelect.dataset.wired = 'true';
            langSelect.addEventListener('change', (e) => I18n.setLang(e.target.value));
        }
    });

    document.addEventListener('langchanged', () => {
        refreshUpdateModalLabels();
        refreshAutoUpdateLabels();
    });

    document.getElementById('pref-autostart').addEventListener('click', async () => {
        const enable = toggleAutostart.dataset.on !== 'true';
        setToggle(toggleAutostart, enable);
        try {
            const res = await Host.call('setStartWithWindows', { enabled: enable });
            if (!res.success) setToggle(toggleAutostart, !enable);
        } catch {
            setToggle(toggleAutostart, !enable);
        }
    });

    document.getElementById('pref-tray').addEventListener('click', async () => {
        const enable = toggleTray.dataset.on !== 'true';
        setToggle(toggleTray, enable);
        try {
            await Host.call('setCloseToTray', { enabled: enable });
            if (window.__voltSettings) window.__voltSettings.get().closeToTray = enable;
        } catch {
            setToggle(toggleTray, !enable);
        }
    });
})();