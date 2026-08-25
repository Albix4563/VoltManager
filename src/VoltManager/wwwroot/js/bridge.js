/**
 * JSON-RPC bridge over WebView2 postMessage.
 * Host.call(method, payload) -> Promise
 * Host.on(eventName, handler) for C#-pushed events.
 */
(function () {
    const pending = new Map();
    const listeners = new Map();
    let nextId = 1;

    // getTopProcesses is display-only elastic work. Keep its latest result so a
    // dashboard timer cannot force native process enumeration more often than the
    // host resource policy permits. Safety and thermal RPCs never pass this gate.
    let topProcessesCache = [];
    let topProcessesLastCallAt = 0;
    let topProcessesInFlight = null;

    const hasWebView = !!(window.chrome && window.chrome.webview);

    if (hasWebView) {
        window.chrome.webview.addEventListener('message', (e) => {
            const msg = e.data;
            if (!msg || typeof msg !== 'object') return;
            if (msg.event) {
                const handlers = listeners.get(msg.event) || [];
                handlers.forEach(h => { try { h(msg.data); } catch (err) { console.error(err); } });
                // Also dispatch as DOM event for i18n/theme cross-cutting concerns.
                try {
                    document.dispatchEvent(new CustomEvent(msg.event, { detail: msg.data }));
                } catch (_) {}
                return;
            }
            if (msg.id && pending.has(msg.id)) {
                const { resolve, reject, timeout } = pending.get(msg.id);
                pending.delete(msg.id);
                clearTimeout(timeout);
                if (msg.ok) resolve(msg.result);
                else {
                    var err = new Error(msg.error || 'Bridge error');
                    err.code = msg.code || 'unknown';
                    err.detail = msg.error || '';
                    reject(err);
                }
            }
        });
    }

    function rawCall(method, payload) {
        if (!hasWebView) {
            return Promise.reject(new Error('Bridge non disponibile (anteprima browser)'));
        }
        return new Promise((resolve, reject) => {
            const id = 'rpc-' + (nextId++);
            const timeout = setTimeout(() => {
                if (pending.has(id)) {
                    pending.delete(id);
                    reject(new Error('Timeout: ' + method));
                }
            }, 120000);
            pending.set(id, { resolve, reject, timeout });
            window.chrome.webview.postMessage({ id, method, payload: payload || {} });
        });
    }

    function topProcessPolicy() {
        const state = window.VoltResourceProfile;
        if (!state) return { allowed: true, intervalMs: 0 };
        if (state.allowProcessPolling === false) return { allowed: false, intervalMs: 0 };
        const intervalMs = Number(state.processPollingIntervalMs);
        return {
            allowed: true,
            intervalMs: Number.isFinite(intervalMs) && intervalMs > 0 ? intervalMs : 0,
        };
    }

    function callTopProcesses(payload) {
        const policy = topProcessPolicy();
        if (!policy.allowed) return Promise.resolve(topProcessesCache);

        const now = Date.now();
        if (topProcessesInFlight) return topProcessesInFlight;
        if (policy.intervalMs > 0 && topProcessesLastCallAt > 0 &&
            now - topProcessesLastCallAt < policy.intervalMs) {
            return Promise.resolve(topProcessesCache);
        }

        topProcessesLastCallAt = now;
        topProcessesInFlight = rawCall('getTopProcesses', payload)
            .then(result => {
                if (Array.isArray(result)) topProcessesCache = result;
                return result;
            })
            .finally(() => { topProcessesInFlight = null; });
        return topProcessesInFlight;
    }

    window.Host = {
        available: hasWebView,
        call(method, payload) {
            if (method === 'getTopProcesses') return callTopProcesses(payload);
            return rawCall(method, payload);
        },
        on(eventName, handler) {
            if (!listeners.has(eventName)) listeners.set(eventName, []);
            listeners.get(eventName).push(handler);
        },
        /**
         * Surfaces a user-initiated host failure on an existing status hook.
         * show(msg, isError) may be a function, or omitted (console only).
         * Returns the message string for callers that also need it.
         */
        fail(err, show) {
            var msg = (err && err.message) ? err.message : String(err || 'Error');
            try {
                if (typeof show === 'function') show(msg, true);
            } catch (_) { /* status UI must not mask the original error */ }
            try { console.error(msg, err); } catch (_) {}
            return msg;
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
