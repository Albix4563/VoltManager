/** Loads the UI reorganization after the existing feature modules are mounted. */
(function () {
    'use strict';

    const MAX_ATTEMPTS = 3;
    const RETRY_DELAYS_MS = [250, 1000];
    const REORGANIZATION_SCRIPTS = [
        'js/ui-reorganization.layout.js',
        'js/ui-reorganization.js',
        'js/ui-reorganization.status.js',
        'js/global-search.js',
    ];

    function assetUrl(path) {
        return window.VM_ASSET_URL(path);
    }

    function removeNode(node) {
        if (!node) return;
        if (typeof node.remove === 'function') node.remove();
        else if (node.parentNode) node.parentNode.removeChild(node);
    }

    function reportError(error) {
        const message = 'VoltManager UI reorganization failed to load: ' +
            ((error && error.message) || String(error || 'Unknown error'));
        try {
            if (window.Host && typeof window.Host.call === 'function') {
                const result = window.Host.call('logError', {
                    message,
                    stack: error && error.stack ? String(error.stack) : null,
                }, { timeoutMs: 5000 });
                if (result && typeof result.catch === 'function') result.catch(() => {});
            }
        } catch (_) {}
        try { console.error(message, error); } catch (_) {}
    }

    function loadScriptWithRetry(path, options) {
        const settings = options || {};
        const maxAttempts = settings.maxAttempts || MAX_ATTEMPTS;
        const delays = settings.retryDelaysMs || RETRY_DELAYS_MS;
        const schedule = settings.setTimeout || window.setTimeout.bind(window);
        const src = assetUrl(path);

        return new Promise((resolve, reject) => {
            let attempt = 0;

            function tryLoad() {
                const existing = document.querySelector('script[data-vm-reorg-src="' + src + '"]');
                if (existing) {
                    resolve(existing);
                    return;
                }

                attempt += 1;
                const script = document.createElement('script');
                script.src = src;
                script.async = false;
                script.dataset.vmReorgSrc = src;
                script.onload = () => resolve(script);
                script.onerror = () => {
                    removeNode(script);
                    if (attempt >= maxAttempts) {
                        reject(new Error('Unable to load ' + src + ' after ' + attempt + ' attempts'));
                        return;
                    }
                    const delay = delays[Math.min(attempt - 1, delays.length - 1)] || 0;
                    schedule(tryLoad, delay);
                };
                document.body.appendChild(script);
            }

            tryLoad();
        });
    }

    async function loadReorganization(options) {
        if (window.__voltUiReorganizationLoading) return false;
        window.__voltUiReorganizationLoading = true;
        try {
            for (const path of REORGANIZATION_SCRIPTS)
                await loadScriptWithRetry(path, options);
            window.__voltUiReorganizationLoaded = true;
            return true;
        } catch (error) {
            reportError(error);
            return false;
        } finally {
            window.__voltUiReorganizationLoading = false;
        }
    }

    window.VoltBootstrapLoader = {
        loadScriptWithRetry,
        loadReorganization,
        reportError,
    };

    if (document.readyState === 'complete') loadReorganization();
    else window.addEventListener('load', loadReorganization, { once: true });
})();
