/**
 * Long-lived automatic-update suspension controls.
 *
 * This intentionally stays separate from the short 15/30/60/120 minute
 * "snooze" action in the update prompt. Both use the host-owned
 * snoozedUntilUtc deadline, while manual checks remain available.
 */
(function () {
    if (!window.Host || !Host.available) return;

    const DAY_MINUTES = 24 * 60;
    const SUPPORTED_DAYS = [1, 5, 7, 12];
    let expiryTimer = null;

    const text = {
        it: {
            automaticSub: 'Controlla automaticamente nuove versioni ogni 15 minuti',
            title: 'Sospendi aggiornamenti',
            sub: 'Ferma temporaneamente i controlli automatici. Il controllo manuale resta disponibile.',
            forLabel: 'Sospendi per',
            day1: '1 giorno',
            days5: '5 giorni',
            days7: '7 giorni',
            days12: '12 giorni',
            suspend: 'Sospendi',
            resume: 'Riprendi ora',
            active: 'Aggiornamenti automatici attivi.',
            disabled: 'La ricerca automatica degli aggiornamenti è disattivata.',
            until: 'Aggiornamenti sospesi fino al {date}.',
            failed: 'Impossibile modificare la sospensione degli aggiornamenti.'
        },
        es: {
            automaticSub: 'Busca nuevas versiones automáticamente cada 15 minutos',
            title: 'Pausar actualizaciones',
            sub: 'Detiene temporalmente las comprobaciones automáticas. La comprobación manual sigue disponible.',
            forLabel: 'Pausar durante',
            day1: '1 día',
            days5: '5 días',
            days7: '7 días',
            days12: '12 días',
            suspend: 'Pausar',
            resume: 'Reanudar ahora',
            active: 'Las actualizaciones automáticas están activas.',
            disabled: 'La búsqueda automática de actualizaciones está desactivada.',
            until: 'Actualizaciones pausadas hasta {date}.',
            failed: 'No se pudo cambiar la pausa de actualizaciones.'
        },
        en: {
            automaticSub: 'Automatically checks for new versions every 15 minutes',
            title: 'Pause updates',
            sub: 'Temporarily stops automatic checks. Manual update checks remain available.',
            forLabel: 'Pause for',
            day1: '1 day',
            days5: '5 days',
            days7: '7 days',
            days12: '12 days',
            suspend: 'Pause',
            resume: 'Resume now',
            active: 'Automatic updates are active.',
            disabled: 'Automatic update checks are disabled.',
            until: 'Updates paused until {date}.',
            failed: 'Unable to change the update pause.'
        },
        zh: {
            automaticSub: '每 15 分钟自动检查新版本',
            title: '暂停更新',
            sub: '暂时停止自动检查。仍可手动检查更新。',
            forLabel: '暂停时长',
            day1: '1 天',
            days5: '5 天',
            days7: '7 天',
            days12: '12 天',
            suspend: '暂停',
            resume: '立即恢复',
            active: '自动更新已启用。',
            disabled: '自动更新检查已关闭。',
            until: '更新已暂停至 {date}。',
            failed: '无法更改更新暂停状态。'
        }
    };

    function lang() {
        const value = window.I18n && I18n.getLang ? I18n.getLang() : 'it';
        return text[value] ? value : 'en';
    }

    function t(key) {
        const current = lang();
        return text[current][key] || text.en[key] || key;
    }

    function getSettings() {
        const source = window.__voltSettings;
        if (!source) return null;
        return source.get ? source.get() : source;
    }

    function getAutoUpdates() {
        const settings = getSettings();
        if (!settings) return null;
        settings.autoUpdates = settings.autoUpdates || {
            enabled: true,
            silentInstallEnabled: true,
            updateChannel: 'stable',
            intervalMinutes: 15,
            snoozedUntilUtc: null,
            skippedVersion: null
        };
        return settings.autoUpdates;
    }

    function parseDeadline(value) {
        if (!value) return 0;
        const milliseconds = Date.parse(value);
        return Number.isFinite(milliseconds) ? milliseconds : 0;
    }

    function formatDeadline(milliseconds) {
        const locale = { it: 'it-IT', es: 'es-ES', en: 'en-US', zh: 'zh-CN' }[lang()] || 'en-US';
        try {
            return new Intl.DateTimeFormat(locale, {
                dateStyle: 'medium',
                timeStyle: 'short'
            }).format(new Date(milliseconds));
        } catch {
            return new Date(milliseconds).toLocaleString();
        }
    }

    function setDisabledState(disabled) {
        const select = document.getElementById('update-suspend-days');
        const suspend = document.getElementById('btn-suspend-updates');
        if (select) select.disabled = disabled;
        if (suspend) suspend.disabled = disabled;

        const controls = document.getElementById('update-suspension-controls');
        if (controls) controls.classList.toggle('opacity-50', disabled);
    }

    function scheduleExpiryRefresh(deadlineMs) {
        if (expiryTimer) clearTimeout(expiryTimer);
        expiryTimer = null;
        const remaining = deadlineMs - Date.now();
        if (remaining <= 0) return;
        expiryTimer = setTimeout(sync, Math.min(remaining + 250, 0x7fffffff));
    }

    function syncAutomaticCheckCopy() {
        const sub = document.getElementById('pref-auto-updates-sub');
        if (sub) sub.textContent = t('automaticSub');
    }

    function sync() {
        syncAutomaticCheckCopy();
        const autoUpdates = getAutoUpdates();
        if (!autoUpdates) return;

        const enabled = autoUpdates.enabled !== false;
        const deadlineMs = parseDeadline(autoUpdates.snoozedUntilUtc);
        const suspended = enabled && deadlineMs > Date.now();
        const status = document.getElementById('update-suspension-status');
        const resume = document.getElementById('btn-resume-updates');

        setDisabledState(!enabled);
        if (resume) resume.classList.toggle('hidden', !suspended);

        if (status) {
            if (!enabled) status.textContent = t('disabled');
            else if (suspended) status.textContent = t('until').replace('{date}', formatDeadline(deadlineMs));
            else status.textContent = t('active');
        }

        scheduleExpiryRefresh(suspended ? deadlineMs : 0);
    }

    function refreshLabels() {
        const labels = {
            'update-suspension-title': t('title'),
            'update-suspension-sub': t('sub'),
            'update-suspend-label': t('forLabel'),
            'update-suspend-day-1': t('day1'),
            'update-suspend-day-5': t('days5'),
            'update-suspend-day-7': t('days7'),
            'update-suspend-day-12': t('days12'),
            'btn-suspend-updates': t('suspend'),
            'btn-resume-updates': t('resume')
        };
        Object.entries(labels).forEach(([id, value]) => {
            const element = document.getElementById(id);
            if (element) element.textContent = value;
        });
        sync();
    }

    function mount() {
        if (document.getElementById('pref-update-suspension')) {
            refreshLabels();
            return;
        }

        const silentPreference = document.getElementById('pref-silent-auto-updates');
        if (!silentPreference) return;

        silentPreference.insertAdjacentHTML('afterend',
            '<div class="mt-md pt-md border-t border-white/10" id="pref-update-suspension">' +
            '  <div class="flex items-start justify-between gap-md flex-wrap">' +
            '    <div class="min-w-0 flex-1">' +
            '      <p class="text-body-md text-on-surface" id="update-suspension-title"></p>' +
            '      <p class="text-label-sm text-on-surface-variant mt-1" id="update-suspension-sub"></p>' +
            '      <p class="text-label-sm text-on-surface-variant mt-2" id="update-suspension-status" role="status" aria-live="polite"></p>' +
            '    </div>' +
            '    <div class="flex items-end gap-sm flex-wrap" id="update-suspension-controls">' +
            '      <label class="flex flex-col gap-1" for="update-suspend-days">' +
            '        <span class="text-label-sm text-on-surface-variant" id="update-suspend-label"></span>' +
            '        <select id="update-suspend-days" class="bg-surface-container-low/50 text-secondary-container font-medium border border-white/10 rounded-lg py-2 px-3 text-body-md focus:outline-none focus:border-secondary-container transition-all duration-300 cursor-pointer">' +
            '          <option value="1" id="update-suspend-day-1"></option>' +
            '          <option value="5" id="update-suspend-day-5"></option>' +
            '          <option value="7" id="update-suspend-day-7"></option>' +
            '          <option value="12" id="update-suspend-day-12"></option>' +
            '        </select>' +
            '      </label>' +
            '      <button type="button" id="btn-suspend-updates" class="btn-ghost rounded-lg py-2.5 px-4 text-label-md"></button>' +
            '      <button type="button" id="btn-resume-updates" class="btn-ghost rounded-lg py-2.5 px-4 text-label-md hidden"></button>' +
            '    </div>' +
            '  </div>' +
            '</div>');

        document.getElementById('btn-suspend-updates')?.addEventListener('click', suspendUpdates);
        document.getElementById('btn-resume-updates')?.addEventListener('click', resumeUpdates);

        const autoToggle = document.getElementById('toggle-auto-updates');
        if (autoToggle && window.MutationObserver) {
            new MutationObserver(sync).observe(autoToggle, {
                attributes: true,
                attributeFilter: ['data-on']
            });
        }

        refreshLabels();
    }

    async function suspendUpdates() {
        const autoUpdates = getAutoUpdates();
        if (!autoUpdates || autoUpdates.enabled === false) return;

        const select = document.getElementById('update-suspend-days');
        const days = Number.parseInt(select?.value || '1', 10);
        if (!SUPPORTED_DAYS.includes(days)) return;

        const suspend = document.getElementById('btn-suspend-updates');
        const resume = document.getElementById('btn-resume-updates');
        if (suspend) suspend.disabled = true;
        if (resume) resume.disabled = true;

        try {
            const result = await Host.call('snoozeUpdate', { minutes: days * DAY_MINUTES });
            autoUpdates.snoozedUntilUtc = result?.snoozedUntilUtc || new Date(Date.now() + days * 86400000).toISOString();
            sync();
        } catch (error) {
            const status = document.getElementById('update-suspension-status');
            if (status) status.textContent = t('failed');
            if (window.console) console.error('snoozeUpdate failed', error);
        } finally {
            if (suspend) suspend.disabled = autoUpdates.enabled === false;
            if (resume) resume.disabled = false;
        }
    }

    async function resumeUpdates() {
        const autoUpdates = getAutoUpdates();
        if (!autoUpdates) return;

        const resume = document.getElementById('btn-resume-updates');
        if (resume) resume.disabled = true;
        try {
            const result = await Host.call('setAutoUpdateChecks', { enabled: true });
            autoUpdates.enabled = true;
            autoUpdates.snoozedUntilUtc = result?.autoUpdates?.snoozedUntilUtc || null;

            const toggle = document.getElementById('toggle-auto-updates');
            if (toggle) toggle.dataset.on = 'true';
            const silentPref = document.getElementById('pref-silent-auto-updates');
            if (silentPref) {
                silentPref.classList.remove('opacity-50');
                silentPref.classList.remove('pointer-events-none');
            }
            sync();
        } catch (error) {
            const status = document.getElementById('update-suspension-status');
            if (status) status.textContent = t('failed');
            if (window.console) console.error('setAutoUpdateChecks failed', error);
        } finally {
            if (resume) resume.disabled = false;
        }
    }

    document.addEventListener('settingsloaded', mount);
    document.addEventListener('langchanged', refreshLabels);
    document.addEventListener('DOMContentLoaded', () => {
        syncAutomaticCheckCopy();
        if (window.__voltSettings) mount();
    });
})();
