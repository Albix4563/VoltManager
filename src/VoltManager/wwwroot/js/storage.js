(function () {
    'use strict';

    const $ = id => document.getElementById(id);
    const api = window.VoltUiReorg;
    const select = $('storage-drive');
    if (!api || !select) return;
    const refresh = $('storage-refresh');
    const analyze = $('storage-analyze');
    const status = $('storage-status');
    const entries = $('storage-entries');
    let drives = [];
    let loading = false;
    let scanning = false;
    let loaded = false;
    let maintaining = false;
    const cleanTemp = $('storage-clean-temp');
    const cleanmgr = $('storage-cleanmgr');
    const defrag = $('storage-defrag');
    const confirmOverlay = $('storage-clean-overlay');
    const confirmTitle = $('storage-clean-title');
    const confirmDesc = $('storage-clean-description');
    const confirmScope = $('storage-clean-scope');
    const confirmWarning = $('storage-clean-warning');
    const confirmCancel = $('storage-clean-cancel');
    const confirmAccept = $('storage-clean-confirm');
    let confirmResolve = null;
    let confirmOpener = null;
    let statusKey = 'storage_ready';
    let statusParams;
    let numberFormat;
    let percentFormat;

    function updateFormats() {
        const locale = window.I18n?.getLocale?.() || 'it-IT';
        numberFormat = new Intl.NumberFormat(locale, { maximumFractionDigits: 1 });
        percentFormat = new Intl.NumberFormat(locale, { style: 'percent', maximumFractionDigits: 1 });
    }
    updateFormats();

    function message(key, params) {
        statusKey = key;
        statusParams = params;
        status.textContent = api.t(key, params && typeof params.size === 'number'
            ? { ...params, size: bytes(params.size) } : params);
    }

    function bytes(value) {
        if (!Number.isFinite(value) || value < 0) return '—';
        const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB'];
        const index = Math.min(4, Math.floor(Math.log2(Math.max(1, value)) / 10));
        return numberFormat.format(value / (1024 ** index)) + ' ' + units[index];
    }

    function controls() {
        const busy = loading || scanning || maintaining || Boolean(confirmResolve);
        select.disabled = busy;
        refresh.disabled = busy;
        analyze.disabled = busy || !select.value;
        for (const button of [cleanTemp, cleanmgr, defrag]) button.disabled = busy;
    }

    function syncConfirmModalText() {
        if (confirmTitle) confirmTitle.textContent = api.t('storage_clean_modal_title');
        if (confirmDesc) confirmDesc.textContent = api.t('storage_clean_modal_subtitle');
        if (confirmScope) confirmScope.textContent = api.t('storage_clean_modal_scope');
        if (confirmWarning) confirmWarning.textContent = api.t('storage_clean_modal_warning');
        if (confirmCancel) confirmCancel.textContent = api.t('storage_clean_modal_cancel');
        if (confirmAccept) confirmAccept.textContent = api.t('storage_clean_modal_confirm');
    }

    function closeConfirmModal(accepted, restoreFocus = true) {
        if (!confirmResolve) return;
        const resolve = confirmResolve;
        confirmResolve = null;
        confirmOverlay?.classList?.add('hidden');
        confirmOverlay?.classList?.remove('flex');
        confirmOverlay?.setAttribute?.('aria-hidden', 'true');
        confirmCancel?.removeEventListener?.('click', onConfirmCancel);
        confirmAccept?.removeEventListener?.('click', onConfirmAccept);
        confirmOverlay?.removeEventListener?.('click', onConfirmBackdrop);
        document.removeEventListener?.('keydown', onConfirmKeydown);
        const opener = confirmOpener || cleanTemp;
        confirmOpener = null;
        controls();
        if (restoreFocus && typeof opener?.focus === 'function' &&
            (!document.contains || document.contains(opener))) {
            opener.focus();
        }
        resolve(accepted);
    }

    function onConfirmCancel() { closeConfirmModal(false); }
    function onConfirmAccept() { closeConfirmModal(true, false); }
    function onConfirmBackdrop(event) { if (event.target === confirmOverlay) closeConfirmModal(false); }
    function onConfirmKeydown(event) {
        if (!confirmResolve) return;
        if (event.key === 'Escape') {
            event.preventDefault?.();
            closeConfirmModal(false);
            return;
        }
        if (event.key !== 'Tab') return;
        const active = document.activeElement;
        if (event.shiftKey) {
            if (active === confirmCancel || active !== confirmAccept) {
                event.preventDefault?.();
                confirmAccept?.focus?.();
            }
        } else if (active === confirmAccept || active !== confirmCancel) {
            event.preventDefault?.();
            confirmCancel?.focus?.();
        }
    }

    function askCleanConfirmation() {
        if (confirmResolve || !confirmOverlay || !confirmCancel || !confirmAccept) {
            return Promise.resolve(false);
        }
        syncConfirmModalText();
        confirmOpener = document.activeElement || cleanTemp;
        return new Promise(resolve => {
            confirmResolve = resolve;
            controls();
            confirmCancel.addEventListener('click', onConfirmCancel);
            confirmAccept.addEventListener('click', onConfirmAccept);
            confirmOverlay.addEventListener('click', onConfirmBackdrop);
            document.addEventListener('keydown', onConfirmKeydown);
            confirmOverlay.classList?.remove('hidden');
            confirmOverlay.classList?.add('flex');
            confirmOverlay.setAttribute?.('aria-hidden', 'false');
            confirmCancel.focus?.();
        });
    }

    function summary() {
        const container = $('storage-summary');
        container.replaceChildren();
        const drive = drives.find(item => item.root === select.value);
        if (!drive) return;
        for (const [key, value] of [
            ['storage_total', bytes(drive.totalBytes)], ['storage_used', bytes(drive.usedBytes)],
            ['storage_free', bytes(drive.freeBytes)],
            ['storage_percent', drive.totalBytes > 0 && Number.isFinite(drive.usedBytes)
                ? percentFormat.format(drive.usedBytes / drive.totalBytes) : '—'],
        ]) {
            const group = document.createElement('div');
            const label = document.createElement('dt');
            const number = document.createElement('dd');
            label.textContent = api.t(key);
            number.textContent = value;
            group.append(label, number);
            container.append(group);
        }
    }

    async function loadDrives(afterCleanup = false) {
        if (loading || scanning || Boolean(confirmResolve) || (maintaining && afterCleanup !== true) || !window.Host?.available) return;
        loading = true;
        controls();
        message('storage_loading');
        try {
            const result = await Host.call('getStorageDrives');
            drives = Array.isArray(result) ? result.filter(item => item && typeof item.root === 'string') : [];
            const previous = select.value;
            select.replaceChildren();
            for (const drive of drives) {
                const option = document.createElement('option');
                option.value = drive.root;
                option.textContent = drive.root + (drive.label ? ' (' + drive.label + ')' : '') + (drive.format ? ' · ' + drive.format : '');
                select.append(option);
            }
            if (drives.some(item => item.root === previous)) select.value = previous;
            entries.replaceChildren();
            summary();
            message(drives.length ? 'storage_ready' : 'storage_empty');
            loaded = true;
        } catch (_) {
            message('storage_error');
        } finally {
            loading = false;
            controls();
        }
    }

    async function scan() {
        if (loading || scanning || maintaining || Boolean(confirmResolve) || !select.value || !window.Host?.available) return;
        scanning = true;
        controls();
        entries.replaceChildren();
        message('storage_scanning');
        try {
            const result = await Host.call('getStorageAnalysis', { root: select.value }, { timeoutMs: 35000 });
            const rows = Array.isArray(result?.entries) ? result.entries.filter(item => item && typeof item.name === 'string' && Number.isFinite(item.bytes) && item.bytes >= 0) : [];
            rows.sort((a, b) => b.bytes - a.bytes);
            for (const item of rows) {
                const row = document.createElement('tr');
                const name = document.createElement('th');
                name.scope = 'row';
                name.textContent = item.name;
                const size = document.createElement('td');
                size.textContent = bytes(item.bytes);
                row.append(name, size);
                entries.append(row);
            }
            message(result?.partial ? 'storage_partial' : 'storage_complete', {
                size: bytes(result?.scannedBytes), count: Number.isFinite(result?.skippedCount) ? result.skippedCount : 0,
            });
        } catch (_) {
            message('storage_error');
        } finally {
            scanning = false;
            controls();
        }
    }

    async function maintain(tool) {
        if (loading || scanning || maintaining || Boolean(confirmResolve) || !window.Host?.available) return;
        if (!tool) {
            const confirmed = await askCleanConfirmation();
            if (!confirmed) {
                message('storage_clean_cancelled');
                return;
            }
        }
        maintaining = true;
        controls();
        message(tool ? 'storage_opening' : 'storage_cleaning');
        try {
            if (tool) {
                await Host.call('openStorageTool', { tool });
                message('storage_tool_opened');
            } else {
                const result = await Host.call('cleanStorageTemp', { confirmed: true }, { timeoutMs: 35000 });
                if (result?.confirmed === false) {
                    message('storage_clean_cancelled');
                } else {
                    if (!result || result.confirmed !== true || !Number.isSafeInteger(result.deletedFiles) ||
                        result.deletedFiles < 0 || !Number.isFinite(result.freedBytes) || result.freedBytes < 0 ||
                        !Number.isSafeInteger(result.skippedCount) || result.skippedCount < 0 || typeof result.partial !== 'boolean')
                        throw new Error('Risultato pulizia non valido.');
                    if (result.deletedFiles > 0) {
                        entries.replaceChildren();
                        await loadDrives(true);
                    }
                    message(result.partial ? 'storage_clean_partial' : 'storage_clean_done', {
                        count: result.deletedFiles, size: result.freedBytes, skipped: result.skippedCount,
                    });
                }
            }
        } catch (_) {
            message(tool ? 'storage_tool_error' : 'storage_clean_error');
        } finally {
            maintaining = false;
            controls();
            if (!tool && typeof cleanTemp.focus === 'function' &&
                (!document.activeElement || document.activeElement === document.body) &&
                (!document.contains || document.contains(cleanTemp))) {
                cleanTemp.focus();
            }
        }
    }
    cleanTemp.addEventListener('click', () => maintain());
    cleanmgr.addEventListener('click', () => maintain('cleanmgr'));
    defrag.addEventListener('click', () => maintain('dfrgui'));

    select.addEventListener('change', () => {
        entries.replaceChildren();
        summary();
        message('storage_ready');
        controls();
    });
    refresh.addEventListener('click', loadDrives);
    analyze.addEventListener('click', scan);
    function activate() {
        if (!loaded && api.state.view === 'monitoring' && api.state.subviews.monitoring === 'storage') loadDrives();
    }
    document.addEventListener('voltuiviewchanged', activate);
    document.addEventListener('voltuisubviewchanged', activate);
    document.addEventListener('langchanged', () => { updateFormats(); summary(); message(statusKey, statusParams); syncConfirmModalText(); });
    activate();
})();
