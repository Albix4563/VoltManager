/* VoltManager Fan Center lazy loader.
 * The full cooling UI and WebGL visualizer are feature-scoped and are not parsed
 * during normal dashboard/settings sessions. Existing file names stay in index.html,
 * so this optimization does not alter startup ordering for other modules.
 */
(function () {
    'use strict';

    let loading = null;
    let loaded = false;

    function loadScript(src, marker) {
        return new Promise((resolve, reject) => {
            const existing = document.querySelector('script[data-vm-lazy-feature="' + marker + '"]');
            if (existing) {
                if (existing.dataset.loaded === 'true') resolve();
                else existing.addEventListener('load', resolve, { once: true });
                return;
            }
            const script = document.createElement('script');
            script.src = src;
            script.async = false;
            script.dataset.vmLazyFeature = marker;
            script.addEventListener('load', () => {
                script.dataset.loaded = 'true';
                resolve();
            }, { once: true });
            script.addEventListener('error', () => reject(new Error('Unable to load ' + src)), { once: true });
            document.body.appendChild(script);
        });
    }

    function ensureFanCenter() {
        if (loaded) return Promise.resolve();
        if (loading) return loading;
        loading = loadScript('js/fan-visualizer.feature.js?v=lazy1', 'fan-visualizer')
            .then(() => loadScript('js/fans.feature.js?v=lazy1', 'fan-center'))
            .then(() => { loaded = true; })
            .catch(error => {
                loading = null;
                console.error('Fan Center lazy load failed', error);
                throw error;
            });
        return loading;
    }

    function coolingVisible() {
        const view = document.getElementById('view-cooling');
        return !!view && !view.classList.contains('hidden');
    }

    document.addEventListener('voltuiviewchanged', event => {
        if (event.detail && event.detail.view === 'cooling') ensureFanCenter().catch(() => {});
    });

    document.addEventListener('voltuiready', () => {
        if (coolingVisible()) ensureFanCenter().catch(() => {});
    });

    if (document.readyState === 'complete' && coolingVisible())
        ensureFanCenter().catch(() => {});

    window.__loadFanCenterFeature = ensureFanCenter;
})();
