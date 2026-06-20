/**
 * Settings & Info: GitHub updates + changelog + preferences toggles.
 */
(function () {
    if (!window.Host || !Host.available) return;

    const btnCheck = document.getElementById('btn-check-updates');
    const btnDownload = document.getElementById('btn-download-update');
    const btnDownloadLabel = document.getElementById('btn-download-label');
    const statusEl = document.getElementById('update-status');

    let downloadUrl = null;
    let _updateInfo = null;
    let modalActionsMounted = false;
    let autoUpdatesWired = false;
    let pendingWelcomeUpdateInfo = null;

    const localText = {
        it: {
            autoUpdates: 'Autoricerca aggiornamenti',
            autoUpdatesSub: 'Controlla automaticamente nuove versioni ogni 30 minuti',
            silentAutoUpdates: 'Aggiornamenti automatici silenziosi',
            silentAutoUpdatesSub: 'Scarica e installa le nuove versioni senza chiedere conferma',
            channelStable: 'Stabile',
            channelPreview: 'Preview (Beta)',
            channelDev: 'Dev (Alpha)',
            channelBadgeStable: 'Canale: Stabile',
            channelBadgePreview: 'Canale: Preview (Beta)',
            channelBadgeDev: 'Canale: Dev (Alpha)',
            snoozeFor: 'Rimanda di',
            snooze: 'Rimanda',
            skip: 'Salta versione',
            later: 'Più tardi',
            install: 'Scarica e installa',
            noInfo: 'Nessuna informazione disponibile.',
            check: 'Controllo aggiornamenti…',
            err: 'Errore: ',
            checkErr: 'Impossibile controllare gli aggiornamenti.',
            dlInstall: 'Scarica e installa ',
            dlProg: 'Download… ',
            dlFail: 'Download non riuscito: ',
            installing: "Installazione in corso, l'app si riavvierà…",
            snoozed: 'Aggiornamento rimandato.',
            skipped: 'Questa versione verrà saltata.',
            min15: '15 minuti', min30: '30 minuti', hour1: '1 ora', hours2: '2 ore',
            updatedToastTitle: 'VoltManager si e aggiornato',
            updatedToastBody: 'Ci sono novita: leggi il changelog per scoprire cosa e cambiato.',
            updatedToastCta: 'Leggi changelog'
        },
        en: {
            autoUpdates: 'Automatic update checks',
            autoUpdatesSub: 'Automatically checks for new versions every 30 minutes',
            silentAutoUpdates: 'Silent automatic updates',
            silentAutoUpdatesSub: 'Downloads and installs new versions without asking first',
            channelStable: 'Stable',
            channelPreview: 'Preview (Beta)',
            channelDev: 'Dev (Alpha)',
            channelBadgeStable: 'Channel: Stable',
            channelBadgePreview: 'Channel: Preview (Beta)',
            channelBadgeDev: 'Channel: Dev (Alpha)',
            snoozeFor: 'Snooze for',
            snooze: 'Snooze',
            skip: 'Skip version',
            later: 'Later',
            install: 'Download and install',
            noInfo: 'No information available.',
            check: 'Checking for updates…',
            err: 'Error: ',
            checkErr: 'Unable to check for updates.',
            dlInstall: 'Download and install ',
            dlProg: 'Download… ',
            dlFail: 'Download failed: ',
            installing: 'Installing, the app will restart…',
            snoozed: 'Update postponed.',
            skipped: 'This version will be skipped.',
            min15: '15 minutes', min30: '30 minutes', hour1: '1 hour', hours2: '2 hours',
            updatedToastTitle: 'VoltManager has updated',
            updatedToastBody: 'There are new changes. Read the changelog to see what changed.',
            updatedToastCta: 'Read changelog'
        },
        zh: {
            autoUpdates: '自动检查更新',
            autoUpdatesSub: '每 30 分钟自动检查新版本',
            channelStable: '稳定版',
            channelPreview: '预览版（Beta）',
            channelDev: '开发版（Alpha）',
            channelBadgeStable: '通道：稳定版',
            channelBadgePreview: '通道：预览版（Beta）',
            channelBadgeDev: '通道：开发版（Alpha）',
            snoozeFor: '推迟',
            snooze: '推迟',
            skip: '跳过版本',
            later: '稍后',
            install: '下载并安装',
            noInfo: '没有可用信息。',
            check: '正在检查更新…',
            err: '错误：',
            checkErr: '无法检查更新。',
            dlInstall: '下载并安装 ',
            dlProg: '正在下载… ',
            dlFail: '下载失败：',
            installing: '正在安装，应用将重启…',
            snoozed: '更新已推迟。',
            skipped: '将跳过此版本。',
            min15: '15 分钟', min30: '30 分钟', hour1: '1 小时', hours2: '2 小时',
            updatedToast: 'VoltManager 已成功更新'
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
        if (!btnDownload) return;
        btnDownload.classList.toggle('hidden', !visible);
        btnDownload.classList.toggle('flex', visible);
    }

    function setStatus(text, isError) {
        if (!statusEl) return;
        statusEl.textContent = text || '';
        statusEl.classList.remove('hidden', 'ok', 'err');
        statusEl.classList.add(isError ? 'err' : 'ok');
    }

    function injectUpdateModalLayoutStyles() {
        if (document.getElementById('update-modal-layout-fix')) return;
        const style = document.createElement('style');
        style.id = 'update-modal-layout-fix';
        style.textContent = `
#update-modal-overlay{overflow:hidden;padding:16px;box-sizing:border-box;}
#update-modal{width:min(640px,calc(100vw - 32px));max-width:min(640px,calc(100vw - 32px));max-height:calc(100vh - 32px);display:flex;flex-direction:column;overflow:hidden;box-sizing:border-box;}
#update-modal,#update-modal *{min-width:0;box-sizing:border-box;}
#update-modal .update-modal-header{flex:0 0 auto;}
#update-modal .update-modal-versions{display:flex;align-items:center;gap:16px;flex-wrap:wrap;flex:0 0 auto;}
#update-modal .update-modal-version-card{flex:1 1 150px;max-width:210px;}
#upd-modal-notes{flex:1 1 auto;max-height:min(42vh,260px);overflow-y:auto;overflow-x:hidden;scrollbar-gutter:stable;}
#upd-modal-notes,#upd-modal-notes *{max-width:100%;overflow-wrap:anywhere;word-break:break-word;}
#upd-modal-progress-wrap,#upd-modal-state-msg{flex:0 0 auto;}
#update-modal .update-modal-footer{flex:0 0 auto;display:grid;grid-template-columns:minmax(0,1fr) auto;align-items:end;gap:12px;overflow:hidden;}
#upd-modal-snooze-wrap{display:flex;align-items:end;gap:8px;flex-wrap:wrap;min-width:0;}
#upd-modal-snooze-label{width:100%;}
#upd-modal-snooze-minutes{width:128px;max-width:100%;}
#update-modal .update-modal-actions{display:flex;justify-content:flex-end;gap:12px;flex-wrap:wrap;min-width:0;}
#update-modal .update-modal-actions button,#upd-modal-btn-snooze{min-height:42px;white-space:normal;text-align:center;}
#upd-modal-btn-install{min-width:130px;max-width:160px;justify-content:center;}
@media (max-width:680px){
  #update-modal{width:calc(100vw - 24px);max-width:calc(100vw - 24px);}
  #update-modal .update-modal-footer{grid-template-columns:1fr;align-items:stretch;}
  #update-modal .update-modal-actions{justify-content:stretch;}
  #update-modal .update-modal-actions button,#upd-modal-btn-snooze,#upd-modal-btn-install,#upd-modal-btn-dismiss,#upd-modal-btn-skip{flex:1 1 140px;max-width:none;}
}`;
        document.head.appendChild(style);
    }

    function applyUpdateModalLayout() {
        injectUpdateModalLayoutStyles();

        const modal = document.getElementById('update-modal');
        const notesEl = document.getElementById('upd-modal-notes');
        const footer = document.getElementById('upd-modal-btn-dismiss')?.parentElement;
        const versionsRow = document.getElementById('upd-modal-cur-ver')?.closest('.px-6');
        const header = document.getElementById('upd-modal-btn-dismiss')?.closest('#update-modal')?.firstElementChild;

        if (modal) modal.classList.add('update-modal-shell');
        if (header) header.classList.add('update-modal-header');
        if (versionsRow) {
            versionsRow.classList.add('update-modal-versions');
            Array.from(versionsRow.children).forEach(child => {
                if (child.id !== 'upd-modal-cur-ver' && child.id !== 'upd-modal-new-ver' && child.tagName !== 'SPAN') {
                    child.classList.add('update-modal-version-card');
                }
            });
        }
        if (notesEl) notesEl.classList.add('update-modal-notes');
        if (footer) {
            footer.classList.add('update-modal-footer');
            let actions = document.getElementById('upd-modal-actions');
            if (!actions) {
                actions = document.createElement('div');
                actions.id = 'upd-modal-actions';
                actions.className = 'update-modal-actions';
                ['upd-modal-btn-skip', 'upd-modal-btn-dismiss', 'upd-modal-btn-install'].forEach(id => {
                    const button = document.getElementById(id);
                    if (button) actions.appendChild(button);
                });
                footer.appendChild(actions);
            }
        }
    }

    function mountUpdateModalActions() {
        if (modalActionsMounted) {
            applyUpdateModalLayout();
            return;
        }
        const dismiss = document.getElementById('upd-modal-btn-dismiss');
        const footer = dismiss?.parentElement;
        if (!footer) return;

        footer.insertAdjacentHTML('afterbegin',
            '<div id="upd-modal-snooze-wrap">' +
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
        applyUpdateModalLayout();
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
        applyUpdateModalLayout();
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
        if (newBadge) {
            newBadge.textContent = formatVersion(info.latestVersion);
            const isBeta = /-?beta/i.test(String(info.latestVersion || ''));
            const isAlpha = /-?alpha/i.test(String(info.latestVersion || ''));
            let betaTag = document.getElementById('upd-modal-beta-tag');
            if (isBeta || isAlpha) {
                if (!betaTag) {
                    betaTag = document.createElement('span');
                    betaTag.id = 'upd-modal-beta-tag';
                    newBadge.parentElement?.appendChild(betaTag);
                }
                betaTag.textContent = isAlpha ? 'ALPHA' : 'BETA';
                betaTag.style.cssText = isAlpha 
                    ? 'margin-top:4px;padding:2px 8px;border-radius:9999px;font-size:11px;font-weight:700;background:rgba(239,68,68,0.18);color:#f87171;border:1px solid rgba(239,68,68,0.45);'
                    : 'margin-top:4px;padding:2px 8px;border-radius:9999px;font-size:11px;font-weight:700;background:rgba(251,191,36,0.18);color:#fcd34d;border:1px solid rgba(251,191,36,0.45);';
            } else if (betaTag) {
                betaTag.remove();
            }
        }
        if (progWrap)  progWrap.classList.add('hidden');
        if (progBar)   progBar.style.width = '0%';
        if (progLabel) progLabel.textContent = '0%';
        if (stateMsg)  stateMsg.classList.add('hidden');
        if (btnInstall) {
            btnInstall.disabled = false;
            btnInstall.innerHTML = '<span class="material-symbols-outlined text-[16px]">download</span>' + esc(tr('upd_modal_btn_install', lt('install')));
        }
        if (btnDismiss) btnDismiss.textContent = tr('upd_modal_btn_later', lt('later'));
        setModalActionsDisabled(false);

        if (notesEl) {
            let html = '';
            if (info && info.releaseNotes) {
                html += '<div class="update-modal-release-notes text-body-sm text-on-surface-variant whitespace-pre-line leading-relaxed">' + esc(info.releaseNotes) + '</div>';
            }
            if (info && info.commits && info.commits.length) {
                html += '<ul class="mt-3 space-y-1 text-label-md text-on-surface-variant list-disc pl-4">';
                info.commits.slice(0, 8).forEach(c => {
                    html += '<li><span class="text-secondary-fixed-dim font-mono">' + esc(c.sha) + '</span> ' + esc(c.message) + '</li>';
                });
                html += '</ul>';
            }
            notesEl.innerHTML = html || '<p class="text-label-md text-on-surface-variant opacity-60">' + esc(tr('msg_no_info', lt('noInfo'))) + '</p>';
        }

        overlay.classList.remove('hidden');
        overlay.classList.add('flex');
    }

    function isWelcomeOpen() {
        if (window.__welcome && typeof window.__welcome.isOpen === 'function')
            return window.__welcome.isOpen();
        const overlay = document.getElementById('welcome-overlay');
        return !!overlay && !overlay.classList.contains('hidden');
    }

    function openUpdateModalAfterWelcome(info) {
        if (isWelcomeOpen()) {
            pendingWelcomeUpdateInfo = normalizeUpdateInfo(info);
            return;
        }
        openUpdateModal(info);
    }

    function flushQueuedWelcomeUpdate() {
        if (!pendingWelcomeUpdateInfo || isWelcomeOpen()) return;
        const info = pendingWelcomeUpdateInfo;
        pendingWelcomeUpdateInfo = null;
        openUpdateModal(info);
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
            setStatus(tr('msg_err', lt('err')) + err.message, true);
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
            setStatus(tr('msg_err', lt('err')) + err.message, true);
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

        if (progWrap) progWrap.classList.remove('hidden');
        setModalActionsDisabled(true);
        if (progLabel) progLabel.textContent = tr('msg_dl_prog', lt('dlProg')) + '0%';

        try {
            await Host.call('downloadUpdate', { url: downloadUrl });
            if (stateMsg) {
                stateMsg.textContent = tr('upd_modal_installing', lt('installing'));
                stateMsg.classList.remove('hidden');
            }
            if (progBar) progBar.style.width = '100%';
        } catch (err) {
            setModalActionsDisabled(false);
            setStatus(tr('msg_dl_fail', lt('dlFail')) + err.message, true);
        }
    }

    btnCheck?.addEventListener('click', async () => {
        btnCheck.disabled = true;
        const icon = btnCheck.querySelector('.material-symbols-outlined');
        if (icon) {
            icon.classList.add('spinning');
            icon.textContent = 'progress_activity';
        }
        setStatus(tr('msg_check_update', lt('check')), false);
        try {
            const info = normalizeUpdateInfo(await Host.call('checkForUpdates'));
            _updateInfo = info;
            if (info.status === 'ok') {
                setStatus(info.message, false);
                if (info.updateAvailable && info.downloadUrl) {
                    downloadUrl = info.downloadUrl;
                    setDownloadButtonVisible(true);
                    if (btnDownloadLabel) btnDownloadLabel.textContent = tr('msg_dl_install', lt('dlInstall')) + formatVersion(info.latestVersion);
                } else {
                    downloadUrl = null;
                    setDownloadButtonVisible(false);
                }
            } else {
                setStatus(info.message || tr('msg_check_err', lt('checkErr')), true);
                downloadUrl = null;
                setDownloadButtonVisible(false);
            }
        } catch (err) {
            setStatus(tr('msg_err', lt('err')) + err.message, true);
        } finally {
            btnCheck.disabled = false;
            if (icon) {
                icon.classList.remove('spinning');
                icon.textContent = 'download';
            }
        }
    });

    Host.on('updateDownloadProgress', (data) => {
        const pct = Math.max(0, Math.min(100, Number(data && data.pct) || 0));
        const progBar   = document.getElementById('upd-modal-bar');
        const progLabel = document.getElementById('upd-modal-prog-label');
        if (progBar)   progBar.style.width = pct + '%';
        if (progLabel) progLabel.textContent = tr('msg_dl_prog', lt('dlProg')) + pct + '%';
        if (btnDownloadLabel) btnDownloadLabel.textContent = tr('msg_dl_prog', lt('dlProg')) + pct + '%';
    });

    Host.on('updateAvailable', (info) => {
        info = normalizeUpdateInfo(info);
        if (!info || !info.downloadUrl) return;
        _updateInfo = info;
        downloadUrl = info.downloadUrl;
        openUpdateModalAfterWelcome(info);
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
            'font-size:13px;display:flex;align-items:flex-start;gap:12px;max-width:min(420px,calc(100vw - 48px));' +
            'overflow-wrap:anywhere;animation:slideInRight 0.3s ease;';
        toast.innerHTML =
            '<span style="color:#00f1fe;font-size:18px;">✓</span>' +
            '<span>' + esc(tr('upd_toast_msg', lt('updatedToast'))) + (ver ? ' v' + esc(ver) : '') + '</span>' +
            '<button onclick="this.parentElement.remove()" style="background:none;border:none;color:#94a3b8;cursor:pointer;font-size:16px;margin-left:4px;">×</button>';
        toast.innerHTML =
            '<span class="material-symbols-outlined" style="color:#00f1fe;font-size:20px;margin-top:1px;">new_releases</span>' +
            '<span style="display:flex;flex-direction:column;gap:6px;min-width:0;">' +
            '  <strong style="color:#f8fbff;font-size:13px;">' + esc(lt('updatedToastTitle')) + (ver ? ' v' + esc(ver) : '') + '</strong>' +
            '  <span style="color:#cbd5e1;line-height:1.35;">' + esc(lt('updatedToastBody')) + '</span>' +
            '  <button id="updated-toast-changelog" type="button" style="align-self:flex-start;background:rgba(0,241,254,.12);border:1px solid rgba(0,241,254,.32);color:#9ffbff;border-radius:8px;padding:6px 10px;cursor:pointer;font-weight:700;font-size:12px;">' + esc(lt('updatedToastCta')) + '</button>' +
            '</span>' +
            '<button id="updated-toast-close" type="button" style="background:none;border:none;color:#94a3b8;cursor:pointer;font-size:18px;margin-left:4px;line-height:1;">x</button>';
        document.body.appendChild(toast);
        document.getElementById('updated-toast-close')?.addEventListener('click', () => toast.remove());
        document.getElementById('updated-toast-changelog')?.addEventListener('click', () => {
            document.querySelector('#nav-list a[data-view="changelog"]')?.click();
            toast.remove();
        });
        setTimeout(() => { if (toast.parentElement) toast.remove(); }, 12000);
    }

    btnDownload?.addEventListener('click', () => {
        if (!downloadUrl) return;
        openUpdateModalAfterWelcome(_updateInfo || { downloadUrl });
    });

    document.addEventListener('welcomeclosed', flushQueuedWelcomeUpdate);

    const toggleAutostart = document.getElementById('toggle-autostart');
    const toggleTray = document.getElementById('toggle-tray');
    const togglePowerSourcePlan = document.getElementById('toggle-power-source-plan');

    function setToggle(el, on) {
        if (el) el.dataset.on = on ? 'true' : 'false';
    }

    function normalizeAutoUpdates(settings) {
        if (!settings.autoUpdates) {
            settings.autoUpdates = { enabled: true, silentInstallEnabled: true, updateChannel: 'stable', intervalMinutes: 30, snoozedUntilUtc: null, skippedVersion: null };
        }
        if (typeof settings.autoUpdates.silentInstallEnabled !== 'boolean') {
            settings.autoUpdates.silentInstallEnabled = true;
        }
        if (!Number.isFinite(settings.autoUpdates.intervalMinutes) || settings.autoUpdates.intervalMinutes < 5) {
            settings.autoUpdates.intervalMinutes = 30;
        }
        if (!['stable', 'preview', 'dev'].includes(settings.autoUpdates.updateChannel)) {
            settings.autoUpdates.updateChannel = settings.autoUpdates.previewChannel ? 'preview' : 'stable';
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
            '</div>' +
            '<div class="flex items-center justify-between group cursor-pointer mt-md" id="pref-silent-auto-updates">' +
            '  <div>' +
            '    <p class="text-body-md text-on-surface group-hover:text-secondary-fixed transition-colors" id="pref-silent-auto-updates-title"></p>' +
            '    <p class="text-label-sm text-on-surface-variant" id="pref-silent-auto-updates-sub"></p>' +
            '  </div>' +
            '  <div class="mini-toggle" data-on="true" id="toggle-silent-auto-updates"><div class="mini-toggle-knob"></div></div>' +
            '</div>');
        refreshAutoUpdateLabels();
    }

    function refreshAutoUpdateLabels() {
        const title = document.getElementById('pref-auto-updates-title');
        const sub = document.getElementById('pref-auto-updates-sub');
        const silentTitle = document.getElementById('pref-silent-auto-updates-title');
        const silentSub = document.getElementById('pref-silent-auto-updates-sub');
        if (title) title.textContent = lt('autoUpdates');
        if (sub) sub.textContent = lt('autoUpdatesSub');
        if (silentTitle) silentTitle.textContent = lt('silentAutoUpdates');
        if (silentSub) silentSub.textContent = lt('silentAutoUpdatesSub');
    }

    function syncAutoUpdateToggles(autoUpdates) {
        autoUpdates = autoUpdates || { enabled: true, silentInstallEnabled: true };
        setToggle(document.getElementById('toggle-auto-updates'), autoUpdates.enabled !== false);
        setToggle(document.getElementById('toggle-silent-auto-updates'), autoUpdates.silentInstallEnabled !== false);
        const silentPref = document.getElementById('pref-silent-auto-updates');
        if (silentPref) {
            const enabled = autoUpdates.enabled !== false;
            silentPref.classList.toggle('opacity-50', !enabled);
            silentPref.classList.toggle('pointer-events-none', !enabled);
        }
    }

    // Reflects the selected channel in the dropdown + the card badge.
    function setChannelUi(channel) {
        const select = document.getElementById('update-channel-select');
        if (select) select.value = channel;
        const badgeText = document.getElementById('update-channel-badge-text');
        if (badgeText) {
            if (channel === 'dev') badgeText.textContent = lt('channelBadgeDev');
            else if (channel === 'preview') badgeText.textContent = lt('channelBadgePreview');
            else badgeText.textContent = lt('channelBadgeStable');
        }
    }

    function wireAutoUpdateUi() {
        if (autoUpdatesWired) return;
        document.addEventListener('click', async (e) => {
            let pref = e.target.closest('#pref-auto-updates');
            if (pref && window.__voltSettings) {
                const settings = window.__voltSettings.get ? window.__voltSettings.get() : window.__voltSettings;
                const toggle = document.getElementById('toggle-auto-updates');
                const enable = toggle?.dataset.on !== 'true';
                setToggle(toggle, enable);
                normalizeAutoUpdates(settings).enabled = enable;
                try {
                    await Host.call('setAutoUpdateChecks', { enabled: enable });
                } catch {
                    setToggle(toggle, !enable);
                    normalizeAutoUpdates(settings).enabled = !enable;
                }
                syncAutoUpdateToggles(normalizeAutoUpdates(settings));
                return;
            }

            pref = e.target.closest('#pref-silent-auto-updates');
            if (pref && window.__voltSettings) {
                const settings = window.__voltSettings.get ? window.__voltSettings.get() : window.__voltSettings;
                const autoUpdates = normalizeAutoUpdates(settings);
                if (autoUpdates.enabled === false) return;
                const toggle = document.getElementById('toggle-silent-auto-updates');
                const enable = toggle?.dataset.on !== 'true';
                setToggle(toggle, enable);
                autoUpdates.silentInstallEnabled = enable;
                try {
                    await Host.call('setSilentAutoUpdates', { enabled: enable });
                } catch {
                    autoUpdates.silentInstallEnabled = !enable;
                    setToggle(toggle, !enable);
                }
                syncAutoUpdateToggles(autoUpdates);
                return;
            }
        });

        const channelSelect = document.getElementById('update-channel-select');
        if (channelSelect) {
            channelSelect.addEventListener('change', async () => {
                if (!window.__voltSettings) return;
                const settings = window.__voltSettings.get ? window.__voltSettings.get() : window.__voltSettings;
                const channel = channelSelect.value;
                const prev = normalizeAutoUpdates(settings).updateChannel;
                setChannelUi(channel);
                normalizeAutoUpdates(settings).updateChannel = channel;
                try {
                    await Host.call('setUpdateChannel', { channel });
                } catch {
                    setChannelUi(prev);
                    normalizeAutoUpdates(settings).updateChannel = prev;
                }
            });
        }
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

    function normalizePowerSourcePlan(settings) {
        if (!settings.powerSourcePlan) {
            settings.powerSourcePlan = { enabled: true, pluggedPlan: 'performance', unpluggedMode: 'previous' };
        }
        settings.powerSourcePlan.enabled = settings.powerSourcePlan.enabled !== false;
        if (!['performance', 'balanced', 'powerSaver'].includes(settings.powerSourcePlan.pluggedPlan)) {
            settings.powerSourcePlan.pluggedPlan = 'performance';
        }
        settings.powerSourcePlan.unpluggedMode = 'previous';
        return settings.powerSourcePlan;
    }

    function normalizeTheme(settings) {
        settings.theme = settings.theme === 'light' ? 'light'
            : (settings.theme === 'black' ? 'black'
            : (settings.theme === 'auto' ? 'auto' : 'dark'));
        return settings.theme;
    }

    function setThemeUi(theme, resolvedTheme) {
        // When theme is "auto", use the resolved theme from the host (C# ThemeService)
        // to apply the actual dark/light variant to the DOM.
        var effective = theme;
        if (theme === 'auto') {
            effective = resolvedTheme || (window.__voltResolvedTheme || 'dark');
        }
        effective = effective === 'light' ? 'light' : (effective === 'black' ? 'black' : 'dark');
        if (window.VoltTheme && VoltTheme.apply) VoltTheme.apply(effective);
        else {
            document.documentElement.dataset.theme = effective;
            document.documentElement.classList.toggle('dark', effective !== 'light');
            document.documentElement.classList.toggle('light', effective === 'light');
        }
        const select = document.getElementById('theme-select');
        if (select) select.value = theme;
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
        if (window.I18n && I18n.apply) I18n.apply();
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
            const settings = window.__voltSettings.get ? window.__voltSettings.get() : window.__voltSettings;
            const autoShutdown = normalizeAutoShutdownSettings(settings);
            autoShutdown.enabled = toggle.dataset.on !== 'true';
            setAutoShutdownUi(autoShutdown);
            if (window.__voltSettings.save) window.__voltSettings.save();
        });

        timeInput.addEventListener('change', (e) => {
            const value = e.target.value;
            if (!/^[0-9]{2}:[0-9]{2}$/.test(value)) return;
            const settings = window.__voltSettings.get ? window.__voltSettings.get() : window.__voltSettings;
            const autoShutdown = normalizeAutoShutdownSettings(settings);
            autoShutdown.time = value;
            setAutoShutdownUi(autoShutdown);
            if (window.__voltSettings.save) window.__voltSettings.save();
        });
    }

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
        const prefPowerSourcePlan = document.getElementById('pref-power-source-plan');
        if (prefPowerSourcePlan) {
            prefPowerSourcePlan.classList.toggle('hidden', hasBattery === false);
        }
    }

    document.addEventListener('settingsloaded', () => {
        const s = window.__voltSettings;
        if (!s) return;
        const settings = s.get ? s.get() : s;
        setToggle(toggleAutostart, s.startWithWindows);
        setToggle(toggleTray, settings.closeToTray);
        setToggle(togglePowerSourcePlan, normalizePowerSourcePlan(settings).enabled);

        checkBatteryPresence();

        mountAutoUpdateUi();
        const autoUpdates = normalizeAutoUpdates(settings);
        syncAutoUpdateToggles(autoUpdates);
        setChannelUi(autoUpdates.updateChannel);
        wireAutoUpdateUi();

        mountAutoShutdownUi();
        setAutoShutdownUi(normalizeAutoShutdownSettings(settings));
        wireAutoShutdownUi();

        const langSelect = document.getElementById('lang-select');
        if (langSelect && window.I18n && I18n.getLang) {
            langSelect.value = I18n.getLang();
            if (langSelect.dataset.wired !== 'true') {
                langSelect.dataset.wired = 'true';
                langSelect.addEventListener('change', (e) => I18n.setLang(e.target.value));
            }
        }

        const themeSelect = document.getElementById('theme-select');
        if (themeSelect) {
            setThemeUi(normalizeTheme(settings), settings.resolvedTheme);
            if (themeSelect.dataset.wired !== 'true') {
                themeSelect.dataset.wired = 'true';
                themeSelect.addEventListener('change', (e) => {
                    const v = e.target.value;
                    const next = v === 'light' ? 'light' : (v === 'black' ? 'black' : (v === 'auto' ? 'auto' : 'dark'));
                    settings.theme = next;
                    setThemeUi(next);
                    if (window.__voltSettings.save) window.__voltSettings.save();
                });
            }
        }

        // Listen for system theme changes pushed by the host (C# ThemeService)
        // when the user is in "auto" mode. The host resolves the actual theme
        // and notifies the frontend so the DOM stays in sync with Windows.
        if (!window.__voltThemeListenerWired) {
            window.__voltThemeListenerWired = true;
            if (window.Host && Host.on) {
                Host.on('themeChanged', (data) => {
                    if (data && data.resolvedTheme) {
                        window.__voltResolvedTheme = data.resolvedTheme;
                        if (window.__voltSettings) {
                            const s = window.__voltSettings.get ? window.__voltSettings.get() : window.__voltSettings;
                            if (s.theme === 'auto') setThemeUi('auto', data.resolvedTheme);
                        }
                    }
                });
            }
        }
    });

    document.addEventListener('langchanged', () => {
        refreshUpdateModalLabels();
        refreshAutoUpdateLabels();
    });

    document.getElementById('pref-autostart')?.addEventListener('click', async () => {
        const enable = toggleAutostart?.dataset.on !== 'true';
        setToggle(toggleAutostart, enable);
        try {
            const res = await Host.call('setStartWithWindows', { enabled: enable });
            if (res && res.success === false) setToggle(toggleAutostart, !enable);
        } catch {
            setToggle(toggleAutostart, !enable);
        }
    });

    document.getElementById('pref-tray')?.addEventListener('click', async () => {
        const enable = toggleTray?.dataset.on !== 'true';
        setToggle(toggleTray, enable);
        try {
            await Host.call('setCloseToTray', { enabled: enable });
            if (window.__voltSettings) {
                const settings = window.__voltSettings.get ? window.__voltSettings.get() : window.__voltSettings;
                settings.closeToTray = enable;
            }
        } catch {
            setToggle(toggleTray, !enable);
        }
    });

    document.getElementById('pref-power-source-plan')?.addEventListener('click', async () => {
        if (!window.__voltSettings) return;
        const settings = window.__voltSettings.get ? window.__voltSettings.get() : window.__voltSettings;
        const cfg = normalizePowerSourcePlan(settings);
        const enable = togglePowerSourcePlan?.dataset.on !== 'true';
        setToggle(togglePowerSourcePlan, enable);
        cfg.enabled = enable;
        try {
            const state = await Host.call('setPowerSourcePlanSwitch', { enabled: enable });
            cfg.enabled = !!state.enabled;
            setToggle(togglePowerSourcePlan, cfg.enabled);
        } catch {
            cfg.enabled = !enable;
            setToggle(togglePowerSourcePlan, !enable);
        }
    });

    Host.on('powerSourcePlanChanged', (state) => {
        if (!state) return;
        setToggle(togglePowerSourcePlan, !!state.enabled);
        if (window.__voltSettings) {
            const settings = window.__voltSettings.get ? window.__voltSettings.get() : window.__voltSettings;
            normalizePowerSourcePlan(settings).enabled = !!state.enabled;
        }
    });
})();
