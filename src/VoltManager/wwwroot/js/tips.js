/* Energy Tips lazy entry point. */
(function () {
    'use strict';
    let loading = null;

    function ensureTips() {
        if (window.__tipsFeatureLoaded) return Promise.resolve(window.__tips);
        if (loading) return loading;
        loading = new Promise((resolve, reject) => {
            const script = document.createElement('script');
            script.src = 'js/tips.feature.js?v=lazy1';
            script.async = false;
            script.dataset.vmLazyFeature = 'energy-tips';
            script.addEventListener('load', () => {
                window.__tipsFeatureLoaded = true;
                resolve(window.__tips);
            }, { once: true });
            script.addEventListener('error', () => {
                loading = null;
                reject(new Error('Unable to load Energy Tips'));
            }, { once: true });
            document.body.appendChild(script);
        });
        return loading;
    }

    const button = document.getElementById('btn-energy-tips');
    async function openOnFirstClick(event) {
        event.preventDefault();
        button?.removeEventListener('click', openOnFirstClick);
        try {
            const api = await ensureTips();
            if (api && api.open) api.open();
        } catch (error) {
            button?.addEventListener('click', openOnFirstClick);
            console.error('Energy Tips lazy load failed', error);
        }
    }
    button?.addEventListener('click', openOnFirstClick);

    window.__loadEnergyTipsFeature = ensureTips;
})();
