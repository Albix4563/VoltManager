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
            channelWarnPreviewTitle: 'Canale Preview (Beta)',
            channelWarnPreviewBody: "Attenzione: il canale Preview può contenere funzionalità non stabili. C'è un rischio di crash o malfunzionamento dell'app.",
            channelWarnDevTitle: 'Canale Dev (Alpha)',
            channelWarnDevBody: "Attenzione: il canale Dev è la versione in cui lo sviluppatore testa tutte le modifiche. C'è un rischio reale che l'app si rompa e che sia necessaria una reinstallazione.",
            channelWarnConfirm: 'Continua',
            channelWarnCancel: 'Annulla',
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
            deferredGame: "C'è un gioco in esecuzione. L'aggiornamento verrà installato alla chiusura del gioco.",
            snoozed: 'Aggiornamento rimandato.',
            skipped: 'Questa versione verrà saltata.',
            min15: '15 minuti', min30: '30 minuti', hour1: '1 ora', hours2: '2 ore',
            updatedToastTitle: 'VoltManager si è aggiornato',
            updatedToastBody: 'Ci sono novità: leggi il changelog per scoprire cosa è cambiato.',
            updatedToastCta: 'Leggi changelog',
            hotkeysTitle: 'Scorciatoie globali',
            hotkeysSub: 'Cambia piano o Mantieni PC attivo senza aprire VoltManager.',
            hotkeysEnabled: 'Attiva scorciatoie',
            hotkeysEnabledSub: 'Le combinazioni funzionano anche quando VoltManager è nell’area di notifica.',
            hotkeySaver: 'Risparmio energetico',
            hotkeyBalanced: 'Bilanciato',
            hotkeyPerformance: 'Prestazioni',
            hotkeyAuto: 'Automatico',
            hotkeyKeepAwake: 'Attiva/disattiva Mantieni PC attivo',
            hotkeyHint: 'Seleziona un campo e premi una combinazione con Ctrl, Alt, Shift o Win.',
            hotkeyInvalid: 'Usa almeno un modificatore più lettera, numero o F1–F12.',
            hotkeyRegistered: 'scorciatoie registrate'
        },
        es: {
            autoUpdates: 'Búsqueda automática de actualizaciones',
            autoUpdatesSub: 'Busca nuevas versiones automáticamente cada 30 minutos',
            silentAutoUpdates: 'Actualizaciones automáticas silenciosas',
            silentAutoUpdatesSub: 'Descarga e instala nuevas versiones sin pedir confirmación',
            channelStable: 'Estable',
            channelPreview: 'Vista previa (Beta)',
            channelDev: 'Desarrollo (Alpha)',
            channelBadgeStable: 'Canal: Estable',
            channelBadgePreview: 'Canal: Vista previa (Beta)',
            channelBadgeDev: 'Canal: Desarrollo (Alpha)',
            channelWarnPreviewTitle: 'Canal Vista previa (Beta)',
            channelWarnPreviewBody: 'Atención: el canal Preview puede incluir funciones no estables. Existe riesgo de bloqueos o mal funcionamiento de la app.',
            channelWarnDevTitle: 'Canal Desarrollo (Alpha)',
            channelWarnDevBody: 'Atención: el canal Dev es la versión donde el desarrollador prueba todos los cambios. Existe un riesgo real de que la app se rompa y sea necesaria una reinstalación.',
            channelWarnConfirm: 'Continuar',
            channelWarnCancel: 'Cancelar',
            snoozeFor: 'Posponer',
            snooze: 'Posponer',
            skip: 'Omitir versión',
            later: 'Más tarde',
            install: 'Descargar e instalar',
            noInfo: 'No hay información disponible.',
            check: 'Buscando actualizaciones…',
            err: 'Error: ',
            checkErr: 'No se pudieron buscar actualizaciones.',
            dlInstall: 'Descargar e instalar ',
            dlProg: 'Descargando… ',
            dlFail: 'Descarga fallida: ',
            installing: 'Instalando, la aplicación se reiniciará…',
            deferredGame: 'Hay un juego en ejecución. La actualización se instalará al cerrar el juego.',
            snoozed: 'Actualización pospuesta.',
            skipped: 'Esta versión se omitirá.',
            min15: '15 minutos', min30: '30 minutos', hour1: '1 hora', hours2: '2 horas',
            updatedToastTitle: 'VoltManager se ha actualizado',
            updatedToastBody: 'Hay novedades. Lee el registro de cambios para ver qué ha cambiado.',
            updatedToastCta: 'Leer registro de cambios',
            hotkeysTitle: 'Atajos globales',
            hotkeysSub: 'Cambia el plan o Mantener PC activo sin abrir VoltManager.',
            hotkeysEnabled: 'Activar atajos',
            hotkeysEnabledSub: 'Funcionan incluso cuando VoltManager está en el área de notificación.',
            hotkeySaver: 'Ahorro de energía',
            hotkeyBalanced: 'Equilibrado',
            hotkeyPerformance: 'Alto rendimiento',
            hotkeyAuto: 'Automático',
            hotkeyKeepAwake: 'Alternar Mantener PC activo',
            hotkeyHint: 'Selecciona un campo y pulsa una combinación con Ctrl, Alt, Shift o Win.',
            hotkeyInvalid: 'Usa un modificador y una letra, número o F1–F12.',
            hotkeyRegistered: 'atajos registrados'
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
            channelWarnPreviewTitle: 'Preview channel (Beta)',
            channelWarnPreviewBody: 'Warning: the Preview channel may include unstable features. There is a risk of crashes or app malfunctions.',
            channelWarnDevTitle: 'Dev channel (Alpha)',
            channelWarnDevBody: 'Warning: the Dev channel is where the developer tests all changes. There is a real risk the app may break and a reinstall may be required.',
            channelWarnConfirm: 'Continue',
            channelWarnCancel: 'Cancel',
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
            deferredGame: 'A game is running. The update will install after you close the game.',
            snoozed: 'Update postponed.',
            skipped: 'This version will be skipped.',
            min15: '15 minutes', min30: '30 minutes', hour1: '1 hour', hours2: '2 hours',
            updatedToastTitle: 'VoltManager has updated',
            updatedToastBody: 'There are new changes. Read the changelog to see what changed.',
            updatedToastCta: 'Read changelog',
            hotkeysTitle: 'Global shortcuts',
            hotkeysSub: 'Change power plan or Keep Awake without opening VoltManager.',
            hotkeysEnabled: 'Enable shortcuts',
            hotkeysEnabledSub: 'Shortcuts work even while VoltManager is in the notification area.',
            hotkeySaver: 'Power Saver',
            hotkeyBalanced: 'Balanced',
            hotkeyPerformance: 'Performance',
            hotkeyAuto: 'Automatic',
            hotkeyKeepAwake: 'Toggle Keep Awake',
            hotkeyHint: 'Select a field and press a combination with Ctrl, Alt, Shift, or Win.',
            hotkeyInvalid: 'Use at least one modifier plus a letter, number, or F1–F12.',
            hotkeyRegistered: 'shortcuts registered'
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
            channelWarnPreviewTitle: '预览通道（Beta）',
            channelWarnPreviewBody: '警告：预览通道可能包含不稳定功能，应用存在崩溃或异常风险。',
            channelWarnDevTitle: '开发通道（Alpha）',
            channelWarnDevBody: '警告：开发通道是开发者测试所有修改的版本，存在应用损坏且可能需要重新安装的真实风险。',
            channelWarnConfirm: '继续',
            channelWarnCancel: '取消',
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
            deferredGame: '检测到游戏正在运行。关闭游戏后将自动安装更新。',
            snoozed: '更新已推迟。',
            skipped: '将跳过此版本。',
            min15: '15 分钟', min30: '30 分钟', hour1: '1 小时', hours2: '2 小时',
            updatedToast: 'VoltManager 已成功更新',
            hotkeysTitle: '全局快捷键',
            hotkeysSub: '无需打开 VoltManager 即可切换电源计划或保持电脑唤醒。',
            hotkeysEnabled: '启用快捷键',
            hotkeysEnabledSub: 'VoltManager 位于通知区域时快捷键仍然有效。',
            hotkeySaver: '节能',
            hotkeyBalanced: '平衡',
            hotkeyPerformance: '高性能',
            hotkeyAuto: '自动',
            hotkeyKeepAwake: '切换保持唤醒',
            hotkeyHint: '选择一个字段，然后按下包含 Ctrl、Alt、Shift 或 Win 的组合键。',
            hotkeyInvalid: '请使用修饰键加字母、数字或 F1–F12。',
            hotkeyRegistered: '个快捷键已注册'
        }
    };

    function lang() {
        return window.I18n && I18n.getLang ? I18n.getLang() : 'it';
    }

    function lt(key) {
        const l = lang();
        return (localText[l] && localText[l][key]) || (localText.en && localText.en[key]) || key;
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
            const result = await Host.call('downloadUpdate', { url: downloadUrl });
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
            'border-radius:12px;padding:14px 18px;' +
            'font-size:13px;display:flex;align-items:flex-start;gap:12px;max-width:min(420px,calc(100vw - 48px));' +
            'overflow-wrap:anywhere;animation:slideInRight 0.3s ease;';
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

    const toggleAutostart = document.getElementById('toggle-autostart');
    const toggleTray = document.getElementById('toggle-tray');
    const toggleWidgetsMaster = document.getElementById('toggle-widgets-master');
    const widgetsCard = document.getElementById('widgets-card');
    const widgetsList = document.getElementById('widgets-list');
    const widgetsEnabledList = document.getElementById('widgets-enabled-list');
    const widgetsDisabledList = document.getElementById('widgets-disabled-list');
    const WIDGET_TYPES = ['clock', 'calendar', 'usage', 'temps', 'power', 'plans'];
    const WIDGET_PRESETS = ['mini', 'medium', 'large'];

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
            item.size = normalizeWidgetSize(item.size);
            item.anchor = WIDGET_ANCHORS.includes(item.anchor) ? item.anchor : 'topRight';
            item.offsetX = Number.isFinite(item.offsetX) ? item.offsetX : 0;
            item.offsetY = Number.isFinite(item.offsetY) ? item.offsetY : 0;
            item.width = Number.isFinite(item.width) ? item.width : 260;
            item.height = Number.isFinite(item.height) ? item.height : 150;
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
                    size: normalizeWidgetSize(item.size),
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
        }[type] || 'widgets';
    }

    function normalizeWidgetSize(size) {
        return WIDGET_PRESETS.includes(size) ? size : 'medium';
    }

    function widgetSizeLabel(size) {
        size = normalizeWidgetSize(size);
        return tr('widget_size_' + size, size);
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
            var sizeKey = normalizeWidgetSize(item.size);
            var sizeButtons = WIDGET_PRESETS.map(function (preset) {
                var selected = preset === sizeKey;
                return '<button class="widget-size-option" type="button" data-widget-size data-widget-type="' + esc(item.type) + '" data-size="' + preset + '" aria-pressed="' + (selected ? 'true' : 'false') + '">' +
                    '<span data-i18n="widget_size_' + preset + '">' + esc(widgetSizeLabel(preset)) + '</span>' +
                    '</button>';
            }).join('');

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

            return '<article class="startup-card" data-state="' + stateAttr + '" data-widget-row data-widget-type="' + esc(item.type) + '">' +
                '<div class="startup-card__accent"></div>' +
                '<div class="startup-card__header"><div class="startup-card__title-wrap"><div class="startup-card__app-icon"><span class="material-symbols-outlined">' + widgetIcon(item.type) + '</span></div><div class="startup-card__meta"><p class="startup-card__name" data-i18n="widget_' + item.type + '">' + esc(item.type) + '</p><div class="startup-card__badges">' + chip + badgePin + '</div></div></div>' +
                '<div class="startup-actions">' + toggleBtn + pinBtn + resetBtn + '</div></div>' +
                '<div class="startup-card__details">' +
                '<div class="widget-size-row"><div><span class="startup-detail-label" data-i18n="widget_detail_size">Dimensione</span><span class="startup-detail-value">' + width + '\u00d7' + height + '</span></div><div class="widget-size-control" role="group" aria-label="' + esc(tr('widget_size_selector', 'Widget size')) + '">' + sizeButtons + '</div></div>' +
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

            const sizeBtn = e.target.closest('[data-widget-size]');
            if (sizeBtn && sizeBtn.dataset.widgetType === type) {
                const size = normalizeWidgetSize(sizeBtn.dataset.size);
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

    function showChannelRiskConfirm(channel) {
        return new Promise((resolve) => {
            const overlay = document.getElementById('channel-warn-overlay');
            const titleEl = document.getElementById('channel-warn-title');
            const bodyEl = document.getElementById('channel-warn-body');
            const btnConfirm = document.getElementById('channel-warn-confirm');
            const btnCancel = document.getElementById('channel-warn-cancel');
            if (!overlay || !btnConfirm || !btnCancel) {
                resolve(window.confirm(
                    channel === 'dev' ? lt('channelWarnDevBody') : lt('channelWarnPreviewBody')
                ));
                return;
            }

            if (titleEl) {
                titleEl.textContent = channel === 'dev'
                    ? lt('channelWarnDevTitle')
                    : lt('channelWarnPreviewTitle');
            }
            if (bodyEl) {
                bodyEl.textContent = channel === 'dev'
                    ? lt('channelWarnDevBody')
                    : lt('channelWarnPreviewBody');
            }
            btnConfirm.textContent = lt('channelWarnConfirm');
            btnCancel.textContent = lt('channelWarnCancel');

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
        var normalized = 'inter';
        var stack = 'Inter, system-ui, -apple-system, sans-serif';
        if (window.VoltFont && VoltFont.apply) {
            normalized = VoltFont.apply(font);
            stack = (VoltFont.stackFor && VoltFont.stackFor(normalized))
                || document.documentElement.style.getPropertyValue('--vm-font-family')
                || stack;
        } else {
            const map = {
                'inter': 'Inter, system-ui, -apple-system, sans-serif',
                'segoe-ui': '"Segoe UI", system-ui, -apple-system, sans-serif',
                'arial': 'Arial, Helvetica, sans-serif',
                'calibri': 'Calibri, Candara, "Segoe UI", sans-serif',
                'verdana': 'Verdana, Geneva, sans-serif',
                'tahoma': 'Tahoma, Verdana, sans-serif',
                'trebuchet-ms': '"Trebuchet MS", Arial, sans-serif',
                'candara': 'Candara, Calibri, "Segoe UI", sans-serif',
                'corbel': 'Corbel, "Segoe UI", sans-serif',
                'century-gothic': '"Century Gothic", Arial, sans-serif',
                'franklin-gothic': '"Franklin Gothic Medium", "Arial Narrow", Arial, sans-serif',
                'georgia': 'Georgia, serif',
                'cambria': 'Cambria, Georgia, serif',
                'palatino-linotype': '"Palatino Linotype", Palatino, Georgia, serif',
                'times-new-roman': '"Times New Roman", Times, serif',
                'consolas': 'Consolas, "Courier New", monospace',
                'courier-new': '"Courier New", Courier, monospace',
                'lucida-console': '"Lucida Console", Monaco, monospace'
            };
            var key = (font && typeof font === 'string') ? font.trim().toLowerCase() : 'inter';
            normalized = map[key] ? key : 'inter';
            stack = map[normalized];
            document.documentElement.style.setProperty('--vm-font-family', stack);
            document.documentElement.style.fontFamily = stack;
            if (document.body) document.body.style.fontFamily = stack;
        }

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

    function applyBatteryPresence() { /* power-source plan lives on Home only */ }

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
        setToggle(toggleAutostart, s.startWithWindows);
        setToggle(toggleTray, settings.closeToTray);
        mountGlobalHotkeysUi(settings);

        checkBatteryPresence();

        mountAutoUpdateUi();
        const autoUpdates = normalizeAutoUpdates(settings);
        syncAutoUpdateToggles(autoUpdates);
        setChannelUi(autoUpdates.updateChannel);
        wireAutoUpdateUi();

        mountAutoShutdownUi();
        setAutoShutdownUi(normalizeAutoShutdownSettings(settings));
        wireAutoShutdownUi();

        mountWidgetsUi();
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
            const channel = normalizeAutoUpdates(settings).updateChannel;
            if (channel) setChannelUi(channel);
        }
    });

    document.getElementById('pref-autostart')?.addEventListener('click', async () => {
        const enable = toggleAutostart?.dataset.on !== 'true';
        setToggle(toggleAutostart, enable);
        try {
            const res = await Host.call('setStartWithWindows', { enabled: enable });
            if (res && res.success === false) {
                setToggle(toggleAutostart, !enable);
                setStatus(tr('msg_err', lt('err')) + (res.message || ''), true);
            }
        } catch (err) {
            setToggle(toggleAutostart, !enable);
            Host.fail(err, (msg) => setStatus(tr('msg_err', lt('err')) + msg, true));
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
        } catch (err) {
            setToggle(toggleTray, !enable);
            Host.fail(err, (msg) => setStatus(tr('msg_err', lt('err')) + msg, true));
        }
    });

    document.getElementById('btn-export-settings')?.addEventListener('click', async () => {
        try {
            await Host.call('exportSettings');
        } catch (e) {
            Host.fail(e, (msg) => setStatus(tr('msg_err', lt('err')) + msg, true));
        }
    });

    document.getElementById('btn-import-settings')?.addEventListener('click', async () => {
        try {
            const res = await Host.call('importSettings');
            // ponytail: full reload instead of re-hydrating every panel from the new settings
            if (res && res.success) location.reload();
        } catch (e) {
            Host.fail(e, (msg) => setStatus(tr('msg_err', lt('err')) + msg, true));
        }
    });

    function diagMsg(key, fallback) {
        return (window.I18n && I18n.t) ? I18n.t(key) : fallback;
    }

    document.getElementById('btn-export-diagnostics')?.addEventListener('click', async () => {
        const statusEl = document.getElementById('update-status');
        try {
            const res = await Host.call('exportDiagnostics');
            if (!res || res.cancelled) {
                if (statusEl) statusEl.textContent = diagMsg('set_diagnostics_cancelled', 'Export cancelled.');
                return;
            }
            if (res.success) {
                if (statusEl) statusEl.textContent = diagMsg('set_diagnostics_ok', 'Diagnostics exported.') + (res.path ? ' ' + res.path : '');
            } else if (statusEl) {
                statusEl.textContent = diagMsg('set_diagnostics_fail', 'Could not export diagnostics.');
            }
        } catch (e) {
            console.error('exportDiagnostics failed', e);
            if (statusEl) statusEl.textContent = diagMsg('set_diagnostics_fail', 'Could not export diagnostics.');
        }
    });

    document.getElementById('btn-open-logs')?.addEventListener('click', async () => {
        const statusEl = document.getElementById('update-status');
        try {
            const res = await Host.call('openLogFolder');
            if (statusEl) {
                statusEl.textContent = res && res.success
                    ? diagMsg('set_logs_ok', 'Log folder opened.')
                    : diagMsg('set_logs_fail', 'Could not open log folder.') + (res && res.error ? ' ' + res.error : '');
            }
        } catch (e) {
            console.error('openLogFolder failed', e);
            if (statusEl) statusEl.textContent = diagMsg('set_logs_fail', 'Could not open log folder.');
        }
    });

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
