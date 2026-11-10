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
        status.textContent = api.t(key, params);
    }

    function bytes(value) {
        if (!Number.isFinite(value) || value < 0) return '—';
        const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB'];
        const index = Math.min(4, Math.floor(Math.log2(Math.max(1, value)) / 10));
        return numberFormat.format(value / (1024 ** index)) + ' ' + units[index];
    }

    function controls() {
        select.disabled = loading || scanning;
        refresh.disabled = loading || scanning;
        analyze.disabled = loading || scanning || !select.value;
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

    async function loadDrives() {
        if (loading || scanning || !window.Host?.available) return;
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
        if (loading || scanning || !select.value || !window.Host?.available) return;
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
    document.addEventListener('langchanged', () => { updateFormats(); summary(); message(statusKey, statusParams); });
    activate();
})();
