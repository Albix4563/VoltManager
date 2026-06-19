/**
 * JSON-RPC bridge over WebView2 postMessage.
 * Host.call(method, payload) -> Promise
 * Host.on(eventName, handler) for C#-pushed events.
 */
(function () {
    const pending = new Map();
    const listeners = new Map();
    let nextId = 1;

    const hasWebView = !!(window.chrome && window.chrome.webview);

    if (hasWebView) {
        window.chrome.webview.addEventListener('message', (e) => {
            const msg = e.data;
            if (!msg || typeof msg !== 'object') return;
            if (msg.event) {
                const handlers = listeners.get(msg.event) || [];
                handlers.forEach(h => { try { h(msg.data); } catch (err) { console.error(err); } });
                return;
            }
            if (msg.id && pending.has(msg.id)) {
                const { resolve, reject } = pending.get(msg.id);
                pending.delete(msg.id);
                if (msg.ok) resolve(msg.result);
                else reject(new Error(msg.error || 'Errore bridge'));
            }
        });
    }

    window.Host = {
        available: hasWebView,
        call(method, payload) {
            if (!hasWebView) {
                return Promise.reject(new Error('Bridge non disponibile (anteprima browser)'));
            }
            return new Promise((resolve, reject) => {
                const id = 'rpc-' + (nextId++);
                pending.set(id, { resolve, reject });
                window.chrome.webview.postMessage({ id, method, payload: payload || {} });
                setTimeout(() => {
                    if (pending.has(id)) {
                        pending.delete(id);
                        reject(new Error('Timeout: ' + method));
                    }
                }, 120000);
            });
        },
        on(eventName, handler) {
            if (!listeners.has(eventName)) listeners.set(eventName, []);
            listeners.get(eventName).push(handler);
        },
    };

    // Forward uncaught JS errors and rejected promises to the host log so UI
    // failures are diagnosable from the same file as backend errors. Best-effort:
    // never throw from a handler, never recurse if logError itself fails.
    function reportToHost(message, stack) {
        if (!hasWebView) return;
        try {
            Host.call('logError', { message: String(message || ''), stack: stack ? String(stack) : null })
                .catch(() => {});
        } catch (_) { /* swallow */ }
    }

    window.addEventListener('error', (e) => {
        const msg = e.message || (e.error && e.error.message) || 'Errore script';
        reportToHost(msg, e.error && e.error.stack);
    });
    window.addEventListener('unhandledrejection', (e) => {
        const reason = e.reason;
        const msg = (reason && reason.message) || String(reason) || 'Promise non gestita';
        reportToHost('Unhandled rejection: ' + msg, reason && reason.stack);
    });
})();
