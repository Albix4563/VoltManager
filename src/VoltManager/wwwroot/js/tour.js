/* Guided Tour lazy entry point. */
(function () {
    'use strict';
    let loading = null;
    let loaded = false;

    function ensureTour() {
        if (loaded) return Promise.resolve(window.__tour);
        if (loading) return loading;
        loading = new Promise((resolve, reject) => {
            const script = document.createElement('script');
            script.src = 'js/tour.feature.js?v=lazy1';
            script.async = false;
            script.dataset.vmLazyFeature = 'guided-tour';
            script.addEventListener('load', () => {
                loaded = true;
                resolve(window.__tour);
            }, { once: true });
            script.addEventListener('error', () => {
                loading = null;
                reject(new Error('Unable to load Guided Tour'));
            }, { once: true });
            document.body.appendChild(script);
        });
        return loading;
    }

    function settings() {
        const api = window.__voltSettings;
        return api && api.get ? api.get() : api;
    }

    async function replayEvent(name) {
        try {
            await ensureTour();
            document.dispatchEvent(new CustomEvent(name, { detail: { lazyReplay: true } }));
        } catch (error) {
            console.error('Guided Tour lazy load failed', error);
        }
    }

    function onWelcomeCompleted(event) {
        if (event.detail && event.detail.lazyReplay) return;
        document.removeEventListener('welcomecompleted', onWelcomeCompleted);
        replayEvent('welcomecompleted');
    }

    function onSettingsLoaded(event) {
        if (event.detail && event.detail.lazyReplay) return;
        const state = settings();
        if (!state || state.welcomeCompleted !== true || state.tourCompleted === true) return;
        document.removeEventListener('settingsloaded', onSettingsLoaded);
        replayEvent('settingsloaded');
    }

    document.addEventListener('welcomecompleted', onWelcomeCompleted);
    document.addEventListener('settingsloaded', onSettingsLoaded);

    const replayButton = document.getElementById('btn-show-tour');
    async function onReplayClick(event) {
        event.preventDefault();
        replayButton?.removeEventListener('click', onReplayClick);
        try {
            const api = await ensureTour();
            if (api && api.open) api.open();
        } catch (error) {
            replayButton?.addEventListener('click', onReplayClick);
            console.error('Guided Tour lazy load failed', error);
        }
    }
    replayButton?.addEventListener('click', onReplayClick);

    window.__loadTourFeature = ensureTour;
})();
