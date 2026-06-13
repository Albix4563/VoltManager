/**
 * Settings & Info: GitHub updates + changelog + preferences toggles.
 */
(function () {
    if (!Host.available) return;

    const btnCheck = document.getElementById('btn-check-updates');
    const btnDownload = document.getElementById('btn-download-update');
    const btnDownloadLabel = document.getElementById('btn-download-label');
    const statusEl = document.getElementById('update-status');
    const changelog = document.getElementById('changelog');

    let downloadUrl = null;

    // ── Update modal state machine ──────────────────────────────────
    // States: idle | checking | update-available | downloading | installing
    let _updateInfo = null;

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

    function setDownloadButtonVisible(visible) {
        btnDownload.classList.toggle('hidden', !visible);
        btnDownload.classList.toggle('flex', visible);
    }

    function openUpdateModal(info) {
        info = normalizeUpdateInfo(info);
        _updateInfo = info;
        downloadUrl = info && info.downloadUrl ? info.downloadUrl : downloadUrl;
        const overlay = document.getElementById('update-modal-overlay');
        if (!overlay) return;
        // Populate modal
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
        if (stateMsg)  stateMsg.classList.add('hidden');
        if (btnInstall) { btnInstall.disabled = false; btnInstall.textContent = I18n.t('upd_modal_btn_install'); }
        if (btnDismiss) btnDismiss.textContent = I18n.t('upd_modal_btn_later');

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

    // Wire modal buttons once DOM is ready
    document.addEventListener('DOMContentLoaded', () => {
        const btnInstall = document.getElementById('upd-modal-btn-install');
        const btnDismiss = document.getElementById('upd-modal-btn-dismiss');
        if (btnInstall) btnInstall.addEventListener('click', doDownloadAndInstall);
        if (btnDismiss) btnDismiss.addEventListener('click', closeUpdateModal);
    });

    function esc(s) {
        const div = document.createElement('div');
        div.textContent = s == null ? '' : String(s);
        return div.innerHTML;
    }

    function setStatus(text, isError) {
        statusEl.textContent = text;
        statusEl.classList.remove('hidden', 'ok', 'err');
        statusEl.classList.add(isError ? 'err' : 'ok');
    }

    function fmtDate(iso) {
        try {
            return new Date(iso).toLocaleDateString('it-IT', { day: 'numeric', month: 'short', year: 'numeric' });
        } catch { return ''; }
    }

    function renderChangelog(info) {
        info = normalizeUpdateInfo(info);
        let html = '';
        if (info.releaseNotes) {
            html += '<div class="relative pl-6 pb-6 border-l border-surface-variant/50">' +
                '<div class="absolute left-0 top-1 w-3 h-3 rounded-full bg-secondary-container -translate-x-[6.5px] shadow-[0_0_10px_rgba(0,241,254,0.4)]"></div>' +
                '<div class="flex items-center gap-sm mb-xs">' +
                '<h4 class="text-title-lg text-on-surface">' + esc(formatVersion(info.latestVersion)) + (info.updateAvailable ? '' : ' (Current)') + '</h4>' +
                '<span class="text-label-sm text-on-surface-variant opacity-70">Release</span></div>' +
                '<div class="text-body-md text-on-surface-variant mt-sm whitespace-pre-line">' + esc(info.releaseNotes) + '</div>' +
                '</div>';
        }
        if (info.commits && info.commits.length) {
            html += '<div class="relative pl-6 border-l border-surface-variant/50">' +
                '<div class="absolute left-0 top-1 w-3 h-3 rounded-full bg-surface-variant -translate-x-[6.5px] border-2 border-background"></div>' +
                '<div class="flex items-center gap-sm mb-xs">' +
                '<h4 class="text-title-lg text-on-surface opacity-80">' + esc(I18n.t('msg_latest_commits')) + '</h4></div>' +
                '<ul class="text-body-md text-on-surface-variant space-y-2 mt-sm list-disc pl-4 marker:text-secondary-container/50">' +
                info.commits.map(c =>
                    '<li><span class="text-secondary-fixed-dim font-mono text-label-md">' + esc(c.sha) + '</span> ' +
                    esc(c.message) +
                    ' <span class="opacity-60 text-label-sm">— ' + esc(c.author) + ', ' + esc(fmtDate(c.date)) + '</span></li>'
                ).join('') +
                '</ul></div>';
        }
        if (!html) {
            html = '<p class="text-body-md text-on-surface-variant opacity-70">' + esc(I18n.t('msg_no_info')) + '</p>';
        }
        changelog.innerHTML = html;
    }

    async function doDownloadAndInstall() {
        if (!downloadUrl) return;
        const progWrap  = document.getElementById('upd-modal-progress-wrap');
        const progBar   = document.getElementById('upd-modal-bar');
        const progLabel = document.getElementById('upd-modal-prog-label');
        const stateMsg  = document.getElementById('upd-modal-state-msg');
        const btnInstall= document.getElementById('upd-modal-btn-install');
        const btnDismiss= document.getElementById('upd-modal-btn-dismiss');

        if (progWrap)  progWrap.classList.remove('hidden');
        if (btnInstall) btnInstall.disabled = true;
        if (btnDismiss) btnDismiss.disabled = true;
        if (progLabel) progLabel.textContent = I18n.t('msg_dl_prog') + '0%';

        try {
            await Host.call('downloadUpdate', { url: downloadUrl });
            // App exits and restarts — show transitional state
            if (stateMsg) {
                stateMsg.textContent = I18n.t('upd_modal_installing');
                stateMsg.classList.remove('hidden');
            }
            if (progBar) progBar.style.width = '100%';
        } catch (err) {
            if (btnInstall) btnInstall.disabled = false;
            if (btnDismiss) btnDismiss.disabled = false;
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
                renderChangelog(info);
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
                if (info.commits && info.commits.length) renderChangelog(info);
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
        // Legacy settings-page label
        btnDownloadLabel.textContent = I18n.t('msg_dl_prog') + data.pct + '%';
    });

    // Startup banner: update available → open branded modal
    Host.on('updateAvailable', (info) => {
        info = normalizeUpdateInfo(info);
        if (!info || !info.downloadUrl) return;
        _updateInfo = info;
        downloadUrl = info.downloadUrl;
        renderChangelog(info);
        openUpdateModal(info);
    });

    // Post-update toast: app restarted with --updated flag
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

    // ----- Preferences toggles -----
    const toggleAutostart = document.getElementById('toggle-autostart');
    const toggleTray = document.getElementById('toggle-tray');

    function setToggle(el, on) {
        el.dataset.on = on ? 'true' : 'false';
    }

    function normalizeAutoShutdownSettings(settings) {
        if (!settings.autoShutdown) {
            settings.autoShutdown = { enabled: false, time: '23:00', lastTriggeredLocalDate: null };
        }
        if (!/^\d{2}:\d{2}$/.test(settings.autoShutdown.time || '')) {
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
            '    <div class="mini-toggle cursor-pointer" data-on="false" id="toggle-auto-shutdown">' +
            '      <div class="mini-toggle-knob"></div>' +
            '    </div>' +
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
        if (!toggle || !timeInput || !window.__voltSettings) return;

        toggle.addEventListener('click', () => {
            const settings = window.__voltSettings.get();
            const autoShutdown = normalizeAutoShutdownSettings(settings);
            autoShutdown.enabled = toggle.dataset.on !== 'true';
            setAutoShutdownUi(autoShutdown);
            window.__voltSettings.save();
        });

        timeInput.addEventListener('change', (e) => {
            const value = e.target.value;
            if (!/^\d{2}:\d{2}$/.test(value)) return;
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

        mountAutoShutdownUi();
        setAutoShutdownUi(normalizeAutoShutdownSettings(s.get()));
        wireAutoShutdownUi();

        const langSelect = document.getElementById('lang-select');
        langSelect.value = I18n.getLang();
        langSelect.addEventListener('change', (e) => {
            I18n.setLang(e.target.value);
            // Optionally dispatch a global event so others can re-render if needed
        });
    });

    document.getElementById('pref-autostart').addEventListener('click', async () => {
        const enable = toggleAutostart.dataset.on !== 'true';
        setToggle(toggleAutostart, enable); // optimistic
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
