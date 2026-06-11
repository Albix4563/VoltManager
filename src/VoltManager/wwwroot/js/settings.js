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
        let html = '';
        if (info.releaseNotes) {
            html += '<div class="relative pl-6 pb-6 border-l border-surface-variant/50">' +
                '<div class="absolute left-0 top-1 w-3 h-3 rounded-full bg-secondary-container -translate-x-[6.5px] shadow-[0_0_10px_rgba(0,241,254,0.4)]"></div>' +
                '<div class="flex items-center gap-sm mb-xs">' +
                '<h4 class="text-title-lg text-on-surface">v' + esc(info.latestVersion) + (info.updateAvailable ? '' : ' (Current)') + '</h4>' +
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

    btnCheck.addEventListener('click', async () => {
        btnCheck.disabled = true;
        const icon = btnCheck.querySelector('.material-symbols-outlined');
        icon.classList.add('spinning');
        icon.textContent = 'progress_activity';
        setStatus(I18n.t('msg_check_update'), false);
        try {
            const info = await Host.call('checkForUpdates');
            if (info.status === 'ok') {
                setStatus(info.message, false);
                renderChangelog(info);
                if (info.updateAvailable && info.downloadUrl) {
                    downloadUrl = info.downloadUrl;
                    btnDownload.classList.remove('hidden');
                    btnDownload.classList.add('flex');
                    btnDownloadLabel.textContent = I18n.t('msg_dl_install') + info.latestVersion;
                }
            } else {
                setStatus(info.message || I18n.t('msg_check_err'), true);
                if (info.commits && info.commits.length) renderChangelog(info);
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
        btnDownloadLabel.textContent = I18n.t('msg_dl_prog') + data.pct + '%';
        const bannerBtn = document.getElementById('upd-banner-install');
        if (bannerBtn) bannerBtn.textContent = I18n.t('msg_dl_prog') + data.pct + '%';
    });

    // ----- Startup update banner (pushed by host after auto-check) -----
    Host.on('updateAvailable', (info) => {
        if (!info || !info.downloadUrl) return;
        // Sync Settings page state so the manual flow shows the update too.
        downloadUrl = info.downloadUrl;
        btnDownload.classList.remove('hidden');
        btnDownload.classList.add('flex');
        btnDownloadLabel.textContent = I18n.t('msg_dl_install') + info.latestVersion;
        renderChangelog(info);

        if (document.getElementById('upd-banner')) return;
        const banner = document.createElement('div');
        banner.id = 'upd-banner';
        banner.style.cssText =
            'position:fixed;bottom:24px;right:24px;z-index:1000;max-width:380px;' +
            'background:#1b2330;border:1px solid rgba(0,241,254,0.35);border-radius:14px;' +
            'padding:16px 18px;box-shadow:0 8px 32px rgba(0,0,0,0.55);color:#e2e8f0;' +
            'font-size:14px;display:flex;flex-direction:column;gap:10px;';
        banner.innerHTML =
            '<div style="font-weight:700;color:#00f1fe;">' +
                esc(I18n.t('upd_banner_title')) + esc(info.latestVersion) + '</div>' +
            '<div style="opacity:0.8;">' + esc(I18n.t('upd_banner_sub')) + '</div>' +
            '<div style="display:flex;gap:10px;justify-content:flex-end;">' +
                '<button id="upd-banner-later" style="background:none;border:none;color:#94a3b8;cursor:pointer;padding:6px 10px;">' +
                    esc(I18n.t('upd_banner_later')) + '</button>' +
                '<button id="upd-banner-install" style="background:#00f1fe;border:none;color:#0b1118;font-weight:700;cursor:pointer;padding:6px 14px;border-radius:8px;">' +
                    esc(I18n.t('upd_banner_install')) + '</button>' +
            '</div>';
        document.body.appendChild(banner);

        document.getElementById('upd-banner-later').addEventListener('click', () => banner.remove());
        document.getElementById('upd-banner-install').addEventListener('click', async (e) => {
            e.target.disabled = true;
            try {
                await Host.call('downloadUpdate', { url: info.downloadUrl });
                // Host launches installer and exits the app.
            } catch (err) {
                e.target.disabled = false;
                setStatus(I18n.t('msg_dl_fail') + err.message, true);
            }
        });
    });

    btnDownload.addEventListener('click', async () => {
        if (!downloadUrl) return;
        btnDownload.disabled = true;
        try {
            await Host.call('downloadUpdate', { url: downloadUrl });
            // Host launches installer and exits the app.
        } catch (err) {
            setStatus(I18n.t('msg_dl_fail') + err.message, true);
            btnDownload.disabled = false;
            btnDownloadLabel.textContent = I18n.t('set_btn_download');
        }
    });

    // ----- Preferences toggles -----
    const toggleAutostart = document.getElementById('toggle-autostart');
    const toggleTray = document.getElementById('toggle-tray');

    function setToggle(el, on) {
        el.dataset.on = on ? 'true' : 'false';
    }

    document.addEventListener('settingsloaded', () => {
        const s = window.__voltSettings;
        setToggle(toggleAutostart, s.startWithWindows);
        setToggle(toggleTray, s.get().closeToTray);

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
