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
    let installHandoffTimer = 0;

    const UPDATE_DOWNLOAD_RPC_TIMEOUT_MS = 30 * 60 * 1000;
    const UPDATE_INSTALL_HANDOFF_TIMEOUT_MS = 60 * 1000;


    function lang() {
        return window.I18n && I18n.getLang ? I18n.getLang() : 'it';
    }

    function lt(key) {
        return window.I18n && I18n.feature ? I18n.feature('settings', key, lang()) : key;
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
            'upd-modal-btn-snooze': lt('snooze'),
            'upd-modal-btn-skip': lt('skip'),
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
            setStatus(lt('snoozed'), false);
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
            setStatus(lt('skipped'), false);
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
        clearTimeout(installHandoffTimer);
        const progWrap  = document.getElementById('upd-modal-progress-wrap');
        const progBar   = document.getElementById('upd-modal-bar');
        const progLabel = document.getElementById('upd-modal-prog-label');
        const stateMsg  = document.getElementById('upd-modal-state-msg');

        if (progWrap) progWrap.classList.remove('hidden');
        setModalActionsDisabled(true);
        if (progLabel) progLabel.textContent = tr('msg_dl_prog', lt('dlProg')) + '0%';
        if (stateMsg) stateMsg.classList.add('hidden');

        try {
            // The RPC spans the whole download: the host enforces its own inactivity
            // timeout, so the generic 120s bridge timeout must not cut slow links.
            const result = await Host.call('downloadUpdate', { url: downloadUrl }, { timeoutMs: UPDATE_DOWNLOAD_RPC_TIMEOUT_MS });
            if (result && result.deferred) {
                setModalActionsDisabled(false);
                if (progWrap) progWrap.classList.add('hidden');
                const msg = result.message || lt('deferredGame');
                if (stateMsg) {
                    stateMsg.textContent = msg;
                    stateMsg.classList.remove('hidden');
                }
                setStatus(msg, false);
                return;
            }
            if (stateMsg) {
                stateMsg.textContent = tr('upd_modal_installing', lt('installing'));
                stateMsg.classList.remove('hidden');
            }
            if (progBar) progBar.style.width = '100%';
            // A successful install closes this app; if we are still alive the
            // installer never took over, so surface it instead of sitting at 100%.
            installHandoffTimer = setTimeout(() => {
                setModalActionsDisabled(false);
                showUpdateModalError(stateMsg, lt('installStuck'));
            }, UPDATE_INSTALL_HANDOFF_TIMEOUT_MS);
        } catch (err) {
            setModalActionsDisabled(false);
            if (progWrap) progWrap.classList.add('hidden');
            const msg = tr('msg_dl_fail', lt('dlFail')) + err.message;
            showUpdateModalError(stateMsg, msg);
        }
    }

    function showUpdateModalError(stateMsg, msg) {
        if (stateMsg) {
            stateMsg.textContent = msg;
            stateMsg.classList.remove('hidden');
        }
        setStatus(msg, true);
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
            'border-radius:12px;padding:14px 18px;' +
            'font-size:13px;display:flex;align-items:flex-start;gap:12px;max-width:min(420px,calc(100vw - 48px));' +
            'overflow-wrap:anywhere;animation:slideInRight .45s var(--vm-ease-emphasized);';
        toast.innerHTML =
            '<span class="material-symbols-outlined toast-icon" style="font-size:20px;margin-top:1px;">new_releases</span>' +
            '<span style="display:flex;flex-direction:column;gap:6px;min-width:0;">' +
            '  <strong class="toast-title" style="font-size:13px;">' + esc(lt('updatedToastTitle')) + (ver ? ' v' + esc(ver) : '') + '</strong>' +
            '  <span class="toast-body" style="line-height:1.35;">' + esc(lt('updatedToastBody')) + '</span>' +
            '  <button id="updated-toast-changelog" type="button" style="align-self:flex-start;border-radius:8px;padding:6px 10px;cursor:pointer;font-weight:700;font-size:12px;">' + esc(lt('updatedToastCta')) + '</button>' +
            '</span>' +
            '<button id="updated-toast-close" type="button" style="background:none;border:none;cursor:pointer;font-size:18px;margin-left:4px;line-height:1;">x</button>';
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

    const toggleWidgetsMaster = document.getElementById('toggle-widgets-master');
    const widgetsCard = document.getElementById('widgets-card');
    const widgetsList = document.getElementById('widgets-list');
    const widgetsEnabledList = document.getElementById('widgets-enabled-list');
    const widgetsDisabledList = document.getElementById('widgets-disabled-list');
    const WIDGET_TYPES = ['clock', 'calendar', 'usage', 'temps', 'power', 'plans', 'launcher', 'apps', 'actions', 'brightness', 'processes', 'memory'];
    const WIDGET_PRESETS = ['mini', 'medium', 'large'];
    const WIDGET_ORIENTATIONS = ['horizontal', 'vertical'];

    function setToggle(el, on) {
        if (el) el.dataset.on = on ? 'true' : 'false';
    }

    const WIDGET_ANCHORS = [
        'topLeft', 'topCenter', 'topRight',
        'middleLeft', 'center', 'middleRight',
        'bottomLeft', 'bottomCenter', 'bottomRight',
    ];

    function normalizeWidgetsState(state) {
        state = state || { enabled: false, items: [], monitors: [] };
        if (!Array.isArray(state.items)) state.items = [];
        if (!Array.isArray(state.monitors)) state.monitors = [];
        const byType = {};
        state.items.forEach(item => {
            if (item && WIDGET_TYPES.includes(item.type)) byType[item.type] = item;
        });
        state.items = WIDGET_TYPES.map(type => {
            const item = byType[type] || { type, enabled: false, pinned: false, size: 'medium' };
            item.size = normalizeWidgetSize(item.type, item.size);
            item.orientation = normalizeWidgetOrientation(item.type, item.orientation);
            item.anchor = WIDGET_ANCHORS.includes(item.anchor) ? item.anchor : 'topRight';
            item.offsetX = Number.isFinite(item.offsetX) ? item.offsetX : 0;
            item.offsetY = Number.isFinite(item.offsetY) ? item.offsetY : 0;
            item.width = Number.isFinite(item.width) ? item.width : 260;
            item.height = Number.isFinite(item.height) ? item.height : 150;
            item.customSize = item.customSize === true;
            item.usesFallbackDisplay = item.usesFallbackDisplay === true;
            return item;
        });
        state.enabled = state.enabled === true;
        return state;
    }

    function syncLocalWidgets(state) {
        if (!window.__voltSettings) return;
        const settings = window.__voltSettings.get ? window.__voltSettings.get() : window.__voltSettings;
        settings.widgets = {
            enabled: state.enabled === true,
            items: (state.items || []).map(function (item) {
                return {
                    type: item.type,
                    enabled: item.enabled === true,
                    pinned: item.pinned === true,
                    size: normalizeWidgetSize(item.type, item.size),
                    orientation: normalizeWidgetOrientation(item.type, item.orientation),
                    width: isLauncherWidgetType(item.type) && item.customSize && item.orientation === 'horizontal' ? item.width : null,
                    height: isLauncherWidgetType(item.type) && item.customSize && item.orientation === 'vertical' ? item.height : null,
                    x: item.x,
                    y: item.y,
                    monitorId: item.monitorId || null,
                    monitorName: item.monitorName || null,
                    monitorNumber: item.monitorNumber || null,
                    anchor: item.anchor || null,
                    offsetX: Number.isFinite(item.offsetX) ? item.offsetX : 0,
                    offsetY: Number.isFinite(item.offsetY) ? item.offsetY : 0,
                };
            }),
        };
    }

    function widgetIcon(type) {
        return {
            clock: 'schedule',
            calendar: 'calendar_month',
            usage: 'monitor_heart',
            temps: 'device_thermostat',
            power: 'bolt',
            plans: 'tune',
            launcher: 'apps',
            apps: 'grid_view',
            actions: 'bolt',
            brightness: 'brightness_6',
            processes: 'list_alt',
            memory: 'memory',
        }[type] || 'widgets';
    }

    function isLauncherWidgetType(type) {
        return type === 'launcher' || type === 'apps';
    }

    function widgetPresets(type) {
        return WIDGET_PRESETS;
    }

    function normalizeWidgetSize(type, size) {
        return widgetPresets(type).includes(size) ? size : 'medium';
    }

    function normalizeWidgetOrientation(type, orientation) {
        if (!isLauncherWidgetType(type)) return 'horizontal';
        return WIDGET_ORIENTATIONS.includes(orientation) ? orientation : 'horizontal';
    }

    function widgetSizeLabel(size) {
        return tr('widget_size_' + size, size);
    }

    function widgetTitleKey(type) {
        return {
            clock: 'widget_clock', calendar: 'widget_calendar', usage: 'widget_usage', temps: 'widget_temps',
            power: 'widget_power', plans: 'widget_plans', launcher: 'widget_launcher', apps: 'widget_apps',
            actions: 'widget_actions', brightness: 'widget_brightness', processes: 'widget_processes', memory: 'widget_memory',
        }[type] || 'widget_settings_title';
    }

    function widgetAnchorLabel(anchor) {
        var key = 'widget_position_' + String(anchor || 'topRight')
            .replace(/([A-Z])/g, '_$1')
            .toLowerCase()
            .replace(/^_/, '');
        // Map camelCase anchors to i18n keys:
        // topLeft -> widget_position_top_left
        var map = {
            topLeft: 'widget_position_top_left',
            topCenter: 'widget_position_top_center',
            topRight: 'widget_position_top_right',
            middleLeft: 'widget_position_middle_left',
            center: 'widget_position_center',
            middleRight: 'widget_position_middle_right',
            bottomLeft: 'widget_position_bottom_left',
            bottomCenter: 'widget_position_bottom_center',
            bottomRight: 'widget_position_bottom_right',
        };
        return tr(map[anchor] || 'widget_position_top_right', anchor || 'topRight');
    }

    function widgetMonitorLabel(monitor) {
        if (!monitor) return tr('widget_monitor_selector', 'Monitor');
        var label = String(monitor.number) + ' — ' + (monitor.name || monitor.id);
        if (monitor.isPrimary) label += ' (' + tr('widget_monitor_primary', 'primary') + ')';
        return label;
    }

    function widgetPositionSummary(item) {
        var parts = [];
        parts.push(widgetAnchorLabel(item.anchor));
        if (item.monitorNumber != null || item.monitorName) {
            parts.push((item.monitorNumber != null ? item.monitorNumber + ' — ' : '') + (item.monitorName || item.monitorId || ''));
        }
        if (item.usesFallbackDisplay) {
            parts.push(tr('widget_monitor_fallback', 'temporary primary'));
        }
        var ox = Math.round(item.offsetX || 0);
        var oy = Math.round(item.offsetY || 0);
        if (ox !== 0 || oy !== 0) {
            parts.push(tr('widget_offset', 'Offset') + ' ' + ox + ', ' + oy);
        }
        return parts.join(' · ');
    }

    function renderMonitorOptions(item, monitors) {
        monitors = monitors || [];
        var html = '';
        var found = monitors.some(function (m) { return m.id === item.monitorId; });
        if (item.monitorId && !found) {
            var disconnected = (item.monitorNumber != null ? item.monitorNumber + ' — ' : '') +
                (item.monitorName || item.monitorId) +
                ' (' + tr('widget_monitor_disconnected', 'disconnected') + ')';
            html += '<option value="' + esc(item.monitorId) + '" selected disabled>' + esc(disconnected) + '</option>';
        }
        monitors.forEach(function (m) {
            var selected = m.id === item.monitorId || (!item.monitorId && m.isPrimary);
            html += '<option value="' + esc(m.id) + '"' + (selected ? ' selected' : '') + '>' +
                esc(widgetMonitorLabel(m)) + '</option>';
        });
        return html;
    }

    function renderAnchorGrid(item) {
        return '<div class="widget-anchor-grid" role="radiogroup" aria-label="' +
            esc(tr('widget_position_selector', 'Widget position')) + '">' +
            WIDGET_ANCHORS.map(function (anchor) {
                var selected = anchor === (item.anchor || 'topRight');
                return '<button class="widget-anchor-option" type="button" role="radio" aria-checked="' +
                    (selected ? 'true' : 'false') + '" data-widget-anchor data-widget-type="' +
                    esc(item.type) + '" data-anchor="' + anchor + '" title="' +
                    esc(widgetAnchorLabel(anchor)) + '"' + (item.enabled ? '' : ' disabled') + '></button>';
            }).join('') +
            '</div>';
    }

    function widgetEmptyState(icon, key, fallback) {
        return '<div class="widgets-empty-state"><span class="material-symbols-outlined text-[18px]">' + icon + '</span><span data-i18n="' + key + '">' + esc(tr(key, fallback)) + '</span></div>';
    }

    function renderWidgetsState(state) {
        state = normalizeWidgetsState(state);
        setToggle(toggleWidgetsMaster, state.enabled);

        const activeCount = state.items.filter(function (i) { return i.enabled; }).length;
        const totalCount = state.items.length;
        const activeEl = document.getElementById('widgets-active-count');
        const totalEl = document.getElementById('widgets-total-count');
        if (activeEl) activeEl.textContent = String(activeCount);
        if (totalEl) totalEl.textContent = String(totalCount);

        const hasGroupedLists = widgetsEnabledList && widgetsDisabledList;
        if (!widgetsList && !hasGroupedLists) return;

        function renderWidgetCard(item, monitors) {
            var stateAttr = item.enabled ? 'on' : 'off';
            var sizeKey = normalizeWidgetSize(item.type, item.size);
            var sizeButtons = widgetPresets(item.type).map(function (preset) {
                var selected = preset === sizeKey;
                return '<button class="widget-size-option" type="button" data-widget-size data-widget-type="' + esc(item.type) + '" data-size="' + preset + '" aria-pressed="' + (selected ? 'true' : 'false') + '">' +
                    '<span data-i18n="widget_size_' + preset + '">' + esc(widgetSizeLabel(preset)) + '</span>' +
                    '</button>';
            }).join('');

            var orientation = normalizeWidgetOrientation(item.type, item.orientation);
            var orientationRow = '';
            if (isLauncherWidgetType(item.type)) {
                var orientationButtons = WIDGET_ORIENTATIONS.map(function (value) {
                    var selected = value === orientation;
                    var icon = value === 'horizontal' ? 'view_week' : 'view_agenda';
                    var key = 'widget_orientation_' + value;
                    return '<button class="widget-size-option widget-orientation-option" type="button" data-widget-orientation data-widget-type="' + esc(item.type) + '" data-orientation="' + value + '" aria-pressed="' + (selected ? 'true' : 'false') + '">' +
                        '<span class="material-symbols-outlined" aria-hidden="true">' + icon + '</span>' +
                        '<span data-i18n="' + key + '">' + esc(tr(key, value)) + '</span>' +
                        '</button>';
                }).join('');
                orientationRow = '<div class="widget-size-row widget-orientation-row"><div><span class="startup-detail-label" data-i18n="widget_orientation">' + esc(tr('widget_orientation', 'Orientation')) + '</span></div>' +
                    '<div class="widget-size-control widget-orientation-control" role="group" aria-label="' + esc(tr('widget_orientation', 'Orientation')) + '">' + orientationButtons + '</div></div>';
            }

            var badgePin = item.pinned
                ? '<span class="startup-managed-badge"><span class="material-symbols-outlined text-[13px]">push_pin</span><span data-i18n="widget_pinned_badge">In primo piano</span></span>'
                : '';

            var chip = '<span class="startup-status-chip"><span data-i18n="' + (item.enabled ? 'widget_status_active' : 'widget_status_disabled') + '">' + (item.enabled ? 'Attivo' : 'Disattivo') + '</span></span>';

            var toggleBtn = '<button class="startup-switch" data-state="' + stateAttr + '" aria-pressed="' + (item.enabled ? 'true' : 'false') + '" type="button" data-widget-toggle data-widget-type="' + esc(item.type) + '">' +
                '<span class="startup-switch__track"><span class="startup-switch__label startup-switch__label-on">ON</span><span class="startup-switch__label startup-switch__label-off">OFF</span><span class="startup-switch__knob"><span class="material-symbols-outlined startup-switch__icon startup-switch__icon-on">check</span><span class="material-symbols-outlined startup-switch__icon startup-switch__icon-off">close</span></span></span>' +
                '</button>';

            var pinBtn = '<button class="startup-remove-btn' + (item.pinned ? ' startup-pin-btn--active' : '') + '" type="button" data-widget-pin data-widget-type="' + esc(item.type) + '" data-pinned="' + (item.pinned ? 'true' : 'false') + '"' + (item.enabled ? '' : ' disabled') + ' title="' + esc(item.pinned ? tr('widget_unpin_btn', 'Unpin') : tr('widget_pin_btn', 'Pin on top')) + '"><span class="material-symbols-outlined text-[18px]">push_pin</span></button>';

            var resetBtn = '<button class="startup-remove-btn" type="button" data-widget-reset data-widget-type="' + esc(item.type) + '"' + (item.enabled ? '' : ' disabled') + ' title="' + esc(tr('widget_reset_pos', 'Clear adjustment')) + '"><span class="material-symbols-outlined text-[18px]">restart_alt</span></button>';

            var width = Math.round(item.width || 0);
            var height = Math.round(item.height || 0);
            var sizeReadout = (item.customSize ? '<span data-i18n="widget_size_custom">' + esc(tr('widget_size_custom', 'Custom')) + '</span> · ' : '') + width + '\u00d7' + height;
            var titleKey = widgetTitleKey(item.type);

            var gate = item.type === 'brightness' && !item.enabled ? ' data-vm-brightness-only data-vm-laptop-only' : '';

            return '<article class="startup-card" data-state="' + stateAttr + '" data-widget-row data-widget-type="' + esc(item.type) + '"' + gate + '>' +
                '<div class="startup-card__accent"></div>' +
                '<div class="startup-card__header"><div class="startup-card__title-wrap"><div class="startup-card__app-icon"><span class="material-symbols-outlined">' + widgetIcon(item.type) + '</span></div><div class="startup-card__meta"><p class="startup-card__name" data-i18n="' + titleKey + '">' + esc(tr(titleKey, item.type)) + '</p><div class="startup-card__badges">' + chip + badgePin + '</div></div></div>' +
                '<div class="startup-actions">' + toggleBtn + pinBtn + resetBtn + '</div></div>' +
                '<div class="startup-card__details">' +
                orientationRow +
                '<div class="widget-size-row"><div><span class="startup-detail-label" data-i18n="widget_detail_size">Dimensione</span><span class="startup-detail-value">' + sizeReadout + '</span></div><div class="widget-size-control" role="group" aria-label="' + esc(tr('widget_size_selector', 'Widget size')) + '">' + sizeButtons + '</div></div>' +
                '<div class="widget-placement-row">' +
                '<label class="widget-monitor-label"><span class="startup-detail-label" data-i18n="widget_monitor_selector">Monitor</span>' +
                '<select class="widget-monitor-select" data-widget-monitor data-widget-type="' + esc(item.type) + '"' + (item.enabled ? '' : ' disabled') + '>' +
                renderMonitorOptions(item, monitors) +
                '</select></label>' +
                '<div class="widget-anchor-wrap"><span class="startup-detail-label" data-i18n="widget_position_selector">Posizione</span>' +
                renderAnchorGrid(item) +
                '</div></div>' +
                '<div class="startup-detail-line"><span class="startup-detail-label" data-i18n="widget_detail_position">Posizione</span><span class="startup-detail-value">' + esc(widgetPositionSummary(item)) + '</span></div>' +
                '</div></article>';
        }

        const monitors = state.monitors || [];
        if (hasGroupedLists) {
            const enabledItems = state.items.filter(function (item) { return item.enabled; });
            const disabledItems = state.items.filter(function (item) { return !item.enabled; });
            widgetsEnabledList.innerHTML = enabledItems.length
                ? enabledItems.map(function (item) { return renderWidgetCard(item, monitors); }).join('')
                : widgetEmptyState('visibility_off', 'widget_empty_enabled', 'Nessun widget attivo.');
            widgetsDisabledList.innerHTML = disabledItems.length
                ? disabledItems.map(function (item) { return renderWidgetCard(item, monitors); }).join('')
                : widgetEmptyState('check_circle', 'widget_empty_disabled', 'Nessun widget disattivato.');
            if (widgetsList) widgetsList.innerHTML = '';
        } else if (widgetsList) {
            widgetsList.innerHTML = state.items.map(function (item) { return renderWidgetCard(item, monitors); }).join('');
        }

        [widgetsList, widgetsEnabledList, widgetsDisabledList].filter(Boolean).forEach(function (list) {
            list.classList.toggle('opacity-60', !state.enabled);
        });
        if (window.I18n && I18n.apply) I18n.apply();
        window.VoltUiReorg?.syncLaptopOnly?.();
        syncLocalWidgets(state);
    }

    function setWidgetSwitch(card, enabled) {
        var sw = card.querySelector('.startup-switch');
        if (!sw) return;
        sw.dataset.state = enabled ? 'on' : 'off';
        sw.setAttribute('aria-pressed', enabled ? 'true' : 'false');
    }

    function mountWidgetsUi() {
        const widgetsClickRoot = widgetsCard || widgetsList;
        if (!toggleWidgetsMaster || !widgetsClickRoot || toggleWidgetsMaster.dataset.wired === 'true') return;
        toggleWidgetsMaster.dataset.wired = 'true';

        toggleWidgetsMaster.addEventListener('click', async () => {
            const previous = toggleWidgetsMaster.dataset.on === 'true';
            const enabled = !previous;
            setToggle(toggleWidgetsMaster, enabled);
            try {
                renderWidgetsState(await Host.call('setWidgetsMaster', { enabled }));
            } catch (err) {
                setToggle(toggleWidgetsMaster, previous);
                Host.fail(err, (msg) => setStatus(tr('msg_err', lt('err')) + msg, true));
            }
        });

        async function applyPlacement(card, type, monitorId, anchor) {
            try {
                renderWidgetsState(await Host.call('setWidgetPlacement', { type, monitorId, anchor }));
            } catch {
                try { renderWidgetsState(await Host.call('getWidgetsState')); } catch { /* ignore */ }
            }
        }

        widgetsClickRoot.addEventListener('click', async (e) => {
            const card = e.target.closest('[data-widget-row]');
            if (!card) return;
            const type = card.dataset.widgetType;

            const orientationBtn = e.target.closest('[data-widget-orientation]');
            if (orientationBtn && orientationBtn.dataset.widgetType === type) {
                const orientation = normalizeWidgetOrientation(type, orientationBtn.dataset.orientation);
                if (orientationBtn.getAttribute('aria-pressed') === 'true') return;
                try {
                    renderWidgetsState(await Host.call('setWidgetOrientation', { type, orientation }));
                } catch { /* re-render restores state */ }
                return;
            }

            const sizeBtn = e.target.closest('[data-widget-size]');
            if (sizeBtn && sizeBtn.dataset.widgetType === type) {
                const size = normalizeWidgetSize(type, sizeBtn.dataset.size);
                if (sizeBtn.getAttribute('aria-pressed') === 'true') return;
                try {
                    renderWidgetsState(await Host.call('setWidgetSize', { type, size }));
                } catch { /* re-render restores state */ }
                return;
            }

            const anchorBtn = e.target.closest('[data-widget-anchor]');
            if (anchorBtn && anchorBtn.dataset.widgetType === type && !anchorBtn.disabled) {
                const monitor = card.querySelector('[data-widget-monitor]');
                await applyPlacement(card, type, monitor ? monitor.value : '', anchorBtn.dataset.anchor);
                return;
            }

            const sw = e.target.closest('[data-widget-toggle]');
            if (sw && sw.dataset.widgetType === type) {
                const current = sw.dataset.state === 'on';
                const enabled = !current;
                setWidgetSwitch(card, enabled);
                try {
                    renderWidgetsState(await Host.call('setWidgetEnabled', { type, enabled }));
                } catch {
                    setWidgetSwitch(card, current);
                }
                return;
            }

            const pinBtn = e.target.closest('[data-widget-pin]');
            if (pinBtn && pinBtn.dataset.widgetType === type && !pinBtn.disabled) {
                const pinned = pinBtn.dataset.pinned === 'true';
                try {
                    renderWidgetsState(await Host.call('setWidgetPinned', { type, pinned: !pinned }));
                } catch { /* re-render restores state */ }
                return;
            }

            const resetBtn = e.target.closest('[data-widget-reset]');
            if (resetBtn && resetBtn.dataset.widgetType === type && !resetBtn.disabled) {
                try {
                    renderWidgetsState(await Host.call('resetWidgetPosition', { type }));
                } catch { /* re-render restores state */ }
                return;
            }
        });

        widgetsClickRoot.addEventListener('change', async (e) => {
            const select = e.target.closest('[data-widget-monitor]');
            if (!select) return;
            const card = select.closest('[data-widget-row]');
            if (!card) return;
            const type = card.dataset.widgetType;
            const selected = card.querySelector('[data-widget-anchor][aria-checked="true"]');
            const anchor = selected ? selected.dataset.anchor : 'topRight';
            await applyPlacement(card, type, select.value, anchor);
        });

        Host.on('widgetsStateChanged', renderWidgetsState);
    }

    const launcherDetectedList = document.getElementById('launcher-detected-list');
    const launcherCustomList = document.getElementById('launcher-custom-list');
    const launcherStatus = document.getElementById('launcher-manage-status');
    const launcherAddCategory = document.getElementById('launcher-add-category');
    let launcherEntries = null;
    let launcherBusy = false;

    function setLauncherStatus(text, isError) {
        if (!launcherStatus) return;
        launcherStatus.textContent = text || '';
        launcherStatus.classList.toggle('hidden', !text);
        launcherStatus.classList.toggle('err', !!text && isError === true);
    }

    function launcherRowIcon(item) {
        const url = String(item.iconDataUrl || '');
        if (url.startsWith('data:image/png;base64,')) {
            return '<img src="' + esc(url) + '" alt="" width="24" height="24">';
        }
        return '<span class="material-symbols-outlined" aria-hidden="true">' + (item.source === 'custom' ? 'apps' : 'sports_esports') + '</span>';
    }

    function renderLauncherRow(item) {
        const hidden = item.hidden === true;
        const missing = item.available === false;
        const category = item.category === 'apps' ? 'apps' : 'games';
        const categoryKey = category === 'apps' ? 'launcher_category_apps' : 'launcher_category_games';
        const categoryChip = item.source === 'custom'
            ? '<span class="launcher-category-chip" data-category="' + category + '" data-i18n="' + categoryKey + '">' + esc(tr(categoryKey, category === 'apps' ? 'Applications' : 'Games')) + '</span>'
            : '';
        const visibilityLabel = hidden ? tr('launcher_manage_show', 'Show in widget') : tr('launcher_manage_hide', 'Hide from widget');
        let actions = '<button class="startup-remove-btn" type="button" data-launcher-visibility data-launcher-id="' + esc(item.id) + '" data-hidden="' + (hidden ? 'true' : 'false') + '" aria-pressed="' + (hidden ? 'false' : 'true') + '" title="' + esc(visibilityLabel) + '" aria-label="' + esc(visibilityLabel) + '"><span class="material-symbols-outlined text-[18px]" aria-hidden="true">' + (hidden ? 'visibility_off' : 'visibility') + '</span></button>';
        if (item.source === 'custom') {
            const removeLabel = tr('launcher_manage_remove', 'Remove');
            actions += '<button class="startup-remove-btn" type="button" data-launcher-remove data-launcher-id="' + esc(item.id) + '" title="' + esc(removeLabel) + '" aria-label="' + esc(removeLabel) + '"><span class="material-symbols-outlined text-[18px]" aria-hidden="true">delete</span></button>';
        }
        const note = missing
            ? '<span class="launcher-manage-row__note" data-i18n="launcher_manage_missing">' + esc(tr('launcher_manage_missing', 'File not found')) + '</span>'
            : hidden ? '<span class="launcher-manage-row__note" data-i18n="launcher_manage_hidden">' + esc(tr('launcher_manage_hidden', 'Hidden from widget')) + '</span>' : '';
        return '<div class="launcher-manage-row" data-hidden="' + (hidden ? 'true' : 'false') + '" data-missing="' + (missing ? 'true' : 'false') + '">' +
            '<div class="launcher-manage-row__icon">' + launcherRowIcon(item) + '</div>' +
            '<div class="launcher-manage-row__meta"><div class="launcher-manage-row__name-line"><p class="launcher-manage-row__name">' + esc(item.name) + '</p>' + categoryChip + '</div>' +
            '<p class="launcher-manage-row__path" title="' + esc(item.path) + '">' + esc(item.path) + '</p>' + note + '</div>' +
            '<div class="launcher-manage-row__actions">' + actions + '</div></div>';
    }

    function renderLauncherManage() {
        if (!launcherDetectedList || !launcherCustomList) return;
        const refreshBtn = document.getElementById('btn-launcher-refresh');
        if (refreshBtn) refreshBtn.title = tr('launcher_manage_refresh', 'Detect again');
        if (!launcherEntries) {
            launcherDetectedList.innerHTML = widgetEmptyState('hourglass_empty', 'launcher_manage_loading', 'Searching for launchers...');
            launcherCustomList.innerHTML = '';
            return;
        }
        const detected = launcherEntries.filter(item => item.source !== 'custom');
        const custom = launcherEntries.filter(item => item.source === 'custom');
        launcherDetectedList.innerHTML = detected.length
            ? detected.map(renderLauncherRow).join('')
            : widgetEmptyState('search_off', 'launcher_manage_none_detected', 'No game launchers found on this PC.');
        launcherCustomList.innerHTML = custom.length
            ? custom.map(renderLauncherRow).join('')
            : widgetEmptyState('add_circle', 'launcher_manage_none_custom', 'No apps added yet. Use Add app to choose an .exe file.');
    }

    async function loadLauncherManage(method) {
        if (!launcherDetectedList) return;
        try {
            const list = await Host.call(method || 'getLaunchers');
            launcherEntries = Array.isArray(list) ? list : [];
            renderLauncherManage();
        } catch (err) {
            Host.fail(err, msg => setLauncherStatus(tr('launcher_manage_error', 'Could not update the launchers: ') + msg, true));
        }
    }

    async function runLauncherAction(action) {
        if (launcherBusy) return;
        launcherBusy = true;
        try {
            await action();
        } catch (err) {
            Host.fail(err, msg => setLauncherStatus(tr('launcher_manage_error', 'Could not update the launchers: ') + msg, true));
        } finally {
            launcherBusy = false;
        }
    }

    function mountLauncherManageUi() {
        const card = document.getElementById('launcher-apps-card');
        if (!card || card.dataset.wired === 'true') return;
        card.dataset.wired = 'true';

        document.getElementById('btn-launcher-refresh')?.addEventListener('click', () => runLauncherAction(async () => {
            setLauncherStatus('');
            launcherEntries = null;
            renderLauncherManage();
            await loadLauncherManage('refreshLaunchers');
            setLauncherStatus(tr('launcher_manage_refreshed', 'Launcher list updated.'));
        }));

        document.getElementById('btn-launcher-add')?.addEventListener('click', () => runLauncherAction(async () => {
            setLauncherStatus('');
            const category = launcherAddCategory && launcherAddCategory.value === 'apps' ? 'apps' : 'games';
            const res = await Host.call('addCustomLauncher', { category });
            if (!res || !res.added) return;
            const entry = res.entry;
            await loadLauncherManage();
            setLauncherStatus(tr('launcher_manage_added', 'Added {name}.').replace('{name}', entry && entry.name ? entry.name : ''));
        }));

        card.addEventListener('click', e => {
            const visibility = e.target.closest('[data-launcher-visibility]');
            if (visibility) {
                const hidden = visibility.dataset.hidden !== 'true';
                runLauncherAction(async () => {
                    setLauncherStatus('');
                    await Host.call('setLauncherHidden', { id: visibility.dataset.launcherId, hidden });
                    await loadLauncherManage();
                });
                return;
            }
            const remove = e.target.closest('[data-launcher-remove]');
            if (remove) {
                runLauncherAction(async () => {
                    setLauncherStatus('');
                    await Host.call('removeCustomLauncher', { id: remove.dataset.launcherId });
                    await loadLauncherManage();
                });
            }
        });

        Host.on('launchersChanged', () => { if (!launcherBusy) loadLauncherManage(); });
        renderLauncherManage();
        loadLauncherManage();
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
        const riskNote = document.getElementById('update-channel-risk-note');
        if (riskNote) {
            if (channel === 'dev') {
                riskNote.textContent = lt('channelWarnDevBody');
                riskNote.classList.remove('hidden');
            } else if (channel === 'preview') {
                riskNote.textContent = lt('channelWarnPreviewBody');
                riskNote.classList.remove('hidden');
            } else {
                riskNote.textContent = '';
                riskNote.classList.add('hidden');
            }
        }
    }

    function showRiskConfirm(copy) {
        return new Promise((resolve) => {
            const overlay = document.getElementById('channel-warn-overlay');
            const titleEl = document.getElementById('channel-warn-title');
            const bodyEl = document.getElementById('channel-warn-body');
            const btnConfirm = document.getElementById('channel-warn-confirm');
            const btnCancel = document.getElementById('channel-warn-cancel');
            if (!overlay || !btnConfirm || !btnCancel) {
                resolve(window.confirm(copy.body));
                return;
            }

            if (titleEl) titleEl.textContent = copy.title;
            if (bodyEl) bodyEl.textContent = copy.body;
            btnConfirm.textContent = copy.confirm;
            btnCancel.textContent = copy.cancel;

            const close = (accepted) => {
                overlay.classList.add('hidden');
                overlay.classList.remove('flex');
                btnConfirm.removeEventListener('click', onConfirm);
                btnCancel.removeEventListener('click', onCancel);
                overlay.removeEventListener('click', onOverlay);
                document.removeEventListener('keydown', onKey);
                resolve(accepted);
            };
            const onConfirm = () => close(true);
            const onCancel = () => close(false);
            const onOverlay = (e) => { if (e.target === overlay) close(false); };
            const onKey = (e) => { if (e.key === 'Escape') close(false); };

            btnConfirm.addEventListener('click', onConfirm);
            btnCancel.addEventListener('click', onCancel);
            overlay.addEventListener('click', onOverlay);
            document.addEventListener('keydown', onKey);
            overlay.classList.remove('hidden');
            overlay.classList.add('flex');
            btnConfirm.focus();
        });
    }

    function showChannelRiskConfirm(channel) {
        return showRiskConfirm({
            title: channel === 'dev' ? lt('channelWarnDevTitle') : lt('channelWarnPreviewTitle'),
            body: channel === 'dev' ? lt('channelWarnDevBody') : lt('channelWarnPreviewBody'),
            confirm: lt('channelWarnConfirm'),
            cancel: lt('channelWarnCancel'),
        });
    }

    // Animation-level strings live in the core catalog (shared with data-i18n markup).
    function animText(key) {
        return tr(key, key);
    }

    function animationLevelText(level) {
        if (level === 'low') return animText('set_animation_low');
        if (level === 'high') return animText('set_animation_high');
        return animText('set_animation_medium');
    }

    function animationHardwareTier() {
        return window.VoltAnimationLevel?.hardwareTier?.()
            || document.documentElement.dataset.hwTier
            || null;
    }

    function refreshAnimationLevelUi() {
        const select = document.getElementById('animation-level-select');
        const note = document.getElementById('animation-level-note');
        if (!select || !window.VoltAnimationLevel) return;
        const store = window.__voltSettings;
        const settings = store && (store.get ? store.get() : store);
        const setting = settings && ['auto', 'low', 'medium', 'high'].includes(settings.animationLevel)
            ? settings.animationLevel
            : 'auto';
        const recommended = window.VoltAnimationLevel.recommended();
        const autoOption = select.querySelector('option[value="auto"]');
        if (autoOption) {
            autoOption.textContent = animText('set_animation_auto')
                .replace('{level}', animationLevelText(recommended));
        }
        select.value = setting;
        if (!note) return;
        const hwTier = animationHardwareTier();
        const exceeds = !!hwTier && window.VoltAnimationLevel.exceedsRecommended(setting, hwTier);
        note.style.color = exceeds ? '#ffc857' : '';
        if (exceeds) note.textContent = animText('set_animation_over');
        else note.textContent = animText('set_animation_note_' + window.VoltAnimationLevel.resolveLevel(setting, hwTier));
    }

    function wireAnimationLevelUi() {
        const select = document.getElementById('animation-level-select');
        if (!select || select.dataset.wired === 'true' || !window.VoltAnimationLevel) return;
        select.dataset.wired = 'true';
        select.addEventListener('change', async () => {
            const store = window.__voltSettings;
            if (!store) return;
            const settings = store.get ? store.get() : store;
            const previous = ['auto', 'low', 'medium', 'high'].includes(settings.animationLevel)
                ? settings.animationLevel
                : 'auto';
            const next = select.value;
            const hwTier = animationHardwareTier();

            if (hwTier && window.VoltAnimationLevel.exceedsRecommended(next, hwTier)) {
                select.value = previous;
                const accepted = await showRiskConfirm({
                    title: animText('set_animation_warn_title'),
                    body: animText('set_animation_warn_body'),
                    confirm: animText('set_animation_warn_confirm'),
                    cancel: animText('set_animation_warn_cancel'),
                });
                if (!accepted) {
                    refreshAnimationLevelUi();
                    return;
                }
            }

            settings.animationLevel = next;
            try {
                if (store.saveNow) await store.saveNow();
                else if (store.save) store.save();
            } catch {
                settings.animationLevel = previous;
                refreshAnimationLevelUi();
                return;
            }
            refreshAnimationLevelUi();
            document.dispatchEvent(new CustomEvent('animationlevelchange', {
                detail: { level: next }
            }));
        });
    }

    async function applyUpdateChannel(channel, previousChannel) {
        if (!window.__voltSettings) return;
        const settings = window.__voltSettings.get ? window.__voltSettings.get() : window.__voltSettings;
        setChannelUi(channel);
        normalizeAutoUpdates(settings).updateChannel = channel;
        try {
            await Host.call('setUpdateChannel', { channel });
        } catch {
            setChannelUi(previousChannel);
            normalizeAutoUpdates(settings).updateChannel = previousChannel;
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

                // Revert UI immediately; apply only after warning acceptance (if needed).
                channelSelect.value = prev;

                if (channel === 'preview' || channel === 'dev') {
                    const accepted = await showChannelRiskConfirm(channel);
                    if (!accepted) {
                        setChannelUi(prev);
                        return;
                    }
                }

                await applyUpdateChannel(channel, prev);
            });
        }
        autoUpdatesWired = true;
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

    function normalizeThemeColor(settings) {
        const normalized = window.VoltTheme && VoltTheme.normalize
            ? VoltTheme.normalize(settings.themeColor)
            : 'blue';
        settings.themeColor = normalized;
        return normalized;
    }

    function setThemeUi(themeColor, palette) {
        const normalized = window.VoltTheme && VoltTheme.apply
            ? VoltTheme.apply(themeColor, palette)
            : 'blue';
        const select = document.getElementById('theme-select');
        if (select) select.value = normalized;
        return normalized;
    }

    function setFontUi(font) {
        let normalized = VoltFont.apply(font);
        const stack = VoltFont.stackFor(normalized);

        const select = document.getElementById('font-select');
        if (select) {
            // Ensure the option exists before assigning (avoids blank select).
            var hasOption = false;
            for (var i = 0; i < select.options.length; i++) {
                if (select.options[i].value === normalized) { hasOption = true; break; }
            }
            select.value = hasOption ? normalized : 'inter';
            if (!hasOption) normalized = 'inter';
        }

        const preview = document.getElementById('font-specimen-preview');
        if (preview) {
            preview.style.fontFamily = stack;
        }
        return normalized;
    }

    const hotkeyFields = [
        ['powerSaver', 'hotkeySaver'],
        ['balanced', 'hotkeyBalanced'],
        ['performance', 'hotkeyPerformance'],
        ['auto', 'hotkeyAuto'],
        ['keepAwakeToggle', 'hotkeyKeepAwake'],
    ];

    function normalizeGlobalHotkeys(settings) {
        const defaults = {
            enabled: false,
            powerSaver: 'Ctrl+Alt+1',
            balanced: 'Ctrl+Alt+2',
            performance: 'Ctrl+Alt+3',
            auto: 'Ctrl+Alt+0',
            keepAwakeToggle: 'Ctrl+Alt+K',
        };
        if (!settings.globalHotkeys) settings.globalHotkeys = Object.assign({}, defaults);
        const cfg = settings.globalHotkeys;
        cfg.enabled = cfg.enabled === true;
        hotkeyFields.forEach(([field]) => {
            if (!cfg[field] || !String(cfg[field]).trim()) cfg[field] = defaults[field];
            else cfg[field] = String(cfg[field]).trim();
        });
        return cfg;
    }

    function refreshGlobalHotkeyLabels() {
        const title = document.getElementById('global-hotkeys-title');
        const sub = document.getElementById('global-hotkeys-sub');
        const main = document.getElementById('global-hotkeys-enabled-label');
        const mainSub = document.getElementById('global-hotkeys-enabled-sub');
        const hint = document.getElementById('global-hotkeys-hint');
        if (title) title.textContent = lt('hotkeysTitle');
        if (sub) sub.textContent = lt('hotkeysSub');
        if (main) main.textContent = lt('hotkeysEnabled');
        if (mainSub) mainSub.textContent = lt('hotkeysEnabledSub');
        if (hint) hint.textContent = lt('hotkeyHint');
        hotkeyFields.forEach(([field, key]) => {
            const label = document.querySelector('[data-hotkey-label="' + field + '"]');
            if (label) label.textContent = lt(key);
        });
    }

    function renderGlobalHotkeys(settings) {
        const cfg = normalizeGlobalHotkeys(settings);
        const toggle = document.getElementById('toggle-global-hotkeys');
        setToggle(toggle, cfg.enabled);
        toggle?.setAttribute('aria-checked', cfg.enabled ? 'true' : 'false');
        hotkeyFields.forEach(([field]) => {
            const input = document.querySelector('[data-hotkey-field="' + field + '"]');
            if (input && document.activeElement !== input) input.value = cfg[field];
        });
    }

    function capturedGesture(event) {
        const key = String(event.key || '');
        if (['Control', 'Alt', 'Shift', 'Meta'].includes(key)) return null;
        const simple = /^[a-z0-9]$/i.test(key) ? key.toUpperCase() : (/^F(?:[1-9]|1[0-2])$/i.test(key) ? key.toUpperCase() : '');
        if (!simple || !(event.ctrlKey || event.altKey || event.shiftKey || event.metaKey)) return '';
        const parts = [];
        if (event.ctrlKey) parts.push('Ctrl');
        if (event.altKey) parts.push('Alt');
        if (event.shiftKey) parts.push('Shift');
        if (event.metaKey) parts.push('Win');
        parts.push(simple);
        return parts.join('+');
    }

    function mountGlobalHotkeysUi(settings) {
        if (document.getElementById('pref-global-hotkeys')) {
            renderGlobalHotkeys(settings);
            refreshGlobalHotkeyLabels();
            return;
        }

        const target = document.getElementById('vm-settings-general') || document.getElementById('pref-tray')?.parentElement;
        if (!target) return;
        const rows = hotkeyFields.map(([field]) =>
            '<label class="flex items-center justify-between gap-md rounded-xl border border-white/10 bg-surface-container-low/40 px-3 py-2">' +
            '<span class="text-label-md text-on-surface" data-hotkey-label="' + field + '"></span>' +
            '<input readonly data-hotkey-field="' + field + '" class="w-36 max-w-[45%] bg-surface-container-lowest/70 text-secondary-container font-mono border border-white/10 rounded-lg py-2 px-3 text-label-md text-center focus:outline-none focus:border-secondary-container cursor-pointer" />' +
            '</label>').join('');

        target.insertAdjacentHTML('beforeend',
            '<div class="glass-panel rounded-xl p-lg mt-md" id="pref-global-hotkeys">' +
            '<div class="flex flex-col sm:flex-row sm:items-start justify-between gap-md">' +
            '<div><h3 class="text-title-lg text-on-surface" id="global-hotkeys-title"></h3><p class="text-label-sm text-on-surface-variant mt-1" id="global-hotkeys-sub"></p></div>' +
            '<div class="mini-toggle shrink-0" id="toggle-global-hotkeys" role="switch" tabindex="0" aria-checked="false"><div class="mini-toggle-knob"></div></div>' +
            '</div>' +
            '<div class="mt-md"><p class="text-body-md text-on-surface" id="global-hotkeys-enabled-label"></p><p class="text-label-sm text-on-surface-variant" id="global-hotkeys-enabled-sub"></p></div>' +
            '<div class="grid grid-cols-1 sm:grid-cols-2 gap-sm mt-md">' + rows + '</div>' +
            '<p class="text-label-sm text-on-surface-variant mt-md" id="global-hotkeys-hint"></p>' +
            '<p class="text-label-sm text-secondary-container mt-xs" id="global-hotkeys-status"></p>' +
            '</div>');

        const toggle = document.getElementById('toggle-global-hotkeys');
        const flip = () => {
            const cfg = normalizeGlobalHotkeys(settings);
            cfg.enabled = !cfg.enabled;
            renderGlobalHotkeys(settings);
            window.__voltSettings.save?.();
        };
        toggle?.addEventListener('click', flip);
        toggle?.addEventListener('keydown', event => {
            if (event.key !== 'Enter' && event.key !== ' ') return;
            event.preventDefault();
            flip();
        });

        document.querySelectorAll('[data-hotkey-field]').forEach(input => {
            input.addEventListener('keydown', event => {
                event.preventDefault();
                event.stopPropagation();
                const gesture = capturedGesture(event);
                if (gesture == null) return;
                const status = document.getElementById('global-hotkeys-status');
                if (!gesture) {
                    if (status) status.textContent = lt('hotkeyInvalid');
                    return;
                }
                const cfg = normalizeGlobalHotkeys(settings);
                cfg[input.dataset.hotkeyField] = gesture;
                input.value = gesture;
                if (status) status.textContent = '';
                window.__voltSettings.save?.();
            });
        });

        renderGlobalHotkeys(settings);
        refreshGlobalHotkeyLabels();
    }

    document.addEventListener('settingsloaded', () => {
        const s = window.__voltSettings;
        if (!s) return;
        const settings = s.get ? s.get() : s;
        mountGlobalHotkeysUi(settings);
        wireAnimationLevelUi();
        refreshAnimationLevelUi();

        mountAutoUpdateUi();
        const autoUpdates = normalizeAutoUpdates(settings);
        syncAutoUpdateToggles(autoUpdates);
        setChannelUi(autoUpdates.updateChannel);
        wireAutoUpdateUi();

        mountWidgetsUi();
        mountLauncherManageUi();
        renderWidgetsState(normalizeWidgetsState(settings.widgets));
        Host.call('getWidgetsState').then(renderWidgetsState).catch(() => {});

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
            setThemeUi(normalizeThemeColor(settings), window.__voltThemeState && window.__voltThemeState.palette);
            if (themeSelect.dataset.wired !== 'true') {
                themeSelect.dataset.wired = 'true';
                themeSelect.addEventListener('change', (e) => {
                    const next = window.VoltTheme && VoltTheme.normalize
                        ? VoltTheme.normalize(e.target.value)
                        : 'blue';
                    settings.themeColor = next;
                    setThemeUi(next);
                    Host.call('setThemeColor', { themeColor: next })
                        .then(data => {
                            if (data && data.themeColor && data.palette) {
                                window.__voltThemeState = data;
                                setThemeUi(data.themeColor, data.palette);
                            }
                        })
                        .catch(error => console.error('setThemeColor failed', error));
                });
            }
        }

        const fontSelect = document.getElementById('font-select');
        if (fontSelect) {
            settings.font = setFontUi(settings.font || 'inter');
            if (fontSelect.dataset.wired !== 'true') {
                fontSelect.dataset.wired = 'true';
                fontSelect.addEventListener('change', (e) => {
                    const v = e.target.value;
                    settings.font = setFontUi(v);
                    if (window.__voltSettings.save) window.__voltSettings.save();
                });
            }
        }

        // Host may push font changes (import, multi-window); keep select + UI in sync.
        if (!window.__voltFontListenerWired && window.Host && Host.on) {
            window.__voltFontListenerWired = true;
            Host.on('fontChanged', (data) => {
                if (!data || !data.font) return;
                const s = window.__voltSettings && (window.__voltSettings.get ? window.__voltSettings.get() : window.__voltSettings);
                const applied = setFontUi(data.font);
                if (s) s.font = applied;
            });
        }

        if (!window.__voltThemeListenerWired) {
            window.__voltThemeListenerWired = true;
            if (window.Host && Host.on) {
                Host.on('themeChanged', (data) => {
                    if (!data || !data.themeColor || !data.palette) return;
                    window.__voltThemeState = data;
                    window.__voltThemeCatalog = window.__voltThemeCatalog || {};
                    window.__voltThemeCatalog[data.themeColor] = data.palette;
                    const current = window.__voltSettings && (window.__voltSettings.get ? window.__voltSettings.get() : window.__voltSettings);
                    if (current) current.themeColor = data.themeColor;
                    setThemeUi(data.themeColor, data.palette);
                });
            }
        }
    });

    document.addEventListener('langchanged', () => {
        refreshUpdateModalLabels();
        refreshAutoUpdateLabels();
        refreshGlobalHotkeyLabels();
        if (window.__voltSettings) {
            const settings = window.__voltSettings.get ? window.__voltSettings.get() : window.__voltSettings;
            renderWidgetsState(normalizeWidgetsState(settings.widgets));
            renderLauncherManage();
            const channel = normalizeAutoUpdates(settings).updateChannel;
            if (channel) setChannelUi(channel);
        }
        refreshAnimationLevelUi();
    });

    document.addEventListener('perftierchange', refreshAnimationLevelUi);
    document.addEventListener('systeminfoloaded', refreshAnimationLevelUi);

    window.VoltSettingsCore = { lt, tr, setStatus, setToggle };

    Host.on('powerSourcePlanChanged', (state) => {
        if (!state || !window.__voltSettings) return;
        const settings = window.__voltSettings.get ? window.__voltSettings.get() : window.__voltSettings;
        normalizePowerSourcePlan(settings).enabled = !!state.enabled;
    });

    Host.on('globalHotkeysChanged', data => {
        const status = document.getElementById('global-hotkeys-status');
        if (!status) return;
        const values = Object.values((data && data.registrations) || {});
        status.textContent = values.length ? values.filter(Boolean).length + '/' + values.length + ' ' + lt('hotkeyRegistered') : '';
    });
})();
