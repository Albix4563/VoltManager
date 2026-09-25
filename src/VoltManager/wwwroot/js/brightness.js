(function () {
    'use strict';

    let revision = 0;
    let saveTimer = 0;
    let currentPercent = null;
    let pendingSetRevision = 0;

    const $ = id => document.getElementById(id);
    const clamp = value => Math.max(0, Math.min(100, Math.round(Number(value))));
    const translate = key => window.I18n?.feature?.('uiReorganization', key) || key;

    function setSupport(value) {
        if (window.VoltUiReorg?.setBrightnessSupport) {
            window.VoltUiReorg.setBrightnessSupport(value);
            return;
        }
        document.documentElement.dataset.vmBrightness = value === true ? 'supported' :
            value === false ? 'unsupported' : 'unknown';
    }

    function showPercent(value) {
        const slider = $('vm-brightness-range');
        const output = $('vm-brightness-value');
        currentPercent = Number.isFinite(Number(value)) && value != null ? clamp(value) : null;
        if (slider && currentPercent != null) slider.value = String(currentPercent);
        if (output) output.textContent = currentPercent == null ? '--%' : currentPercent + '%';
    }

    async function refresh() {
        if (!window.Host?.available || pendingSetRevision) return;
        const request = ++revision;
        try {
            const result = await Host.call('getDisplayBrightness');
            if (request !== revision) return;
            setSupport(result?.supported === true);
            showPercent(result?.percent);
        } catch (error) {
            if (request !== revision) return;
            console.error('getDisplayBrightness failed', error);
            setSupport(null);
        }
    }

    function queueSet(percent) {
        if (!window.Host?.available) return;
        const request = ++revision;
        pendingSetRevision = request;
        showPercent(percent);
        clearTimeout(saveTimer);
        saveTimer = setTimeout(async () => {
            try {
                const result = await Host.call('setDisplayBrightness', { percent });
                if (request !== revision) return;
                setSupport(result?.supported === true);
                showPercent(result?.percent);
                if (pendingSetRevision === request) pendingSetRevision = 0;
            } catch (error) {
                if (request !== revision) return;
                console.error('setDisplayBrightness failed', error);
                if (pendingSetRevision === request) pendingSetRevision = 0;
                refresh();
            }
        }, 150);
    }

    function wire() {
        const slider = $('vm-brightness-range');
        if (!slider || slider.dataset.vmWired) return;
        slider.dataset.vmWired = 'true';
        slider.addEventListener('input', () => queueSet(clamp(slider.value)));
        $('vm-brightness-decrease')?.addEventListener('click', () => queueSet(clamp((currentPercent ?? Number(slider.value)) - 10)));
        $('vm-brightness-increase')?.addEventListener('click', () => queueSet(clamp((currentPercent ?? Number(slider.value)) + 10)));
        translateLabels();
        showPercent(currentPercent);
        refresh();
    }

    function translateLabels() {
        [['vm-brightness-decrease', 'brightness_decrease'],
            ['vm-brightness-increase', 'brightness_increase'],
            ['vm-brightness-range', 'brightness_title']].forEach(([id, key]) =>
            $(id)?.setAttribute('aria-label', translate(key)));
    }

    setSupport(null);
    document.addEventListener('voltuiready', wire);
    document.addEventListener('langchanged', translateLabels);
    window.addEventListener('focus', refresh);
    document.addEventListener('visibilitychange', () => { if (!document.hidden) refresh(); });
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', refresh);
    else refresh();
})();
