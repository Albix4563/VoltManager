/**
 * JSON-RPC bridge over WebView2 postMessage.
 * Host.call(method, payload) -> Promise
 * Host.on(eventName, handler) for C#-pushed events.
 */
(function () {
    const pending = new Map();
    const listeners = new Map();
    const DEFAULT_RPC_TIMEOUT_MS = 120000;
    const DEGRADED_TIMEOUT_THRESHOLD = 3;
    const PROBE_TIMEOUT_MS = 2000;
    const PROBE_DELAYS_MS = [1000, 2000, 5000, 10000];
    const ERROR_FORWARD_LIMIT = 20;
    const ERROR_FORWARD_WINDOW_MS = 60000;

    const hasWebView = !!(window.chrome && window.chrome.webview);
    const sessionId = createSessionId();
    let nextId = 1;
    let consecutiveTimeouts = 0;
    let degraded = false;
    let probeAttempt = 0;
    let probeTimer = null;

    let errorWindowStartedAt = 0;
    let errorForwarded = 0;
    let errorSummaryForwarded = false;

    // getTopProcesses is display-only elastic work. Keep its latest result so a
    // dashboard timer cannot force native process enumeration more often than the
    // host resource policy permits. Safety and thermal RPCs never pass this gate.
    let topProcessesCache = [];
    let topProcessesLastCallAt = 0;
    let topProcessesInFlight = null;

    function createSessionId() {
        try {
            if (window.crypto && typeof window.crypto.randomUUID === 'function')
                return window.crypto.randomUUID();
            if (window.crypto && typeof window.crypto.getRandomValues === 'function') {
                const values = new Uint32Array(4);
                window.crypto.getRandomValues(values);
                return Array.from(values, value => value.toString(16).padStart(8, '0')).join('');
            }
        } catch (_) {}
        return Date.now().toString(36) + '-' + Math.random().toString(36).slice(2);
    }

    function emit(name, data) {
        const handlers = listeners.get(name) || [];
        handlers.forEach(handler => {
            try { handler(data); } catch (err) { console.error(err); }
        });
        try { document.dispatchEvent(new CustomEvent(name, { detail: data })); } catch (_) {}
    }

    function emitReconnected() {
        const detail = { sessionId };
        emit('bridge:reconnected', detail);
        try { window.dispatchEvent(new CustomEvent('bridge:reconnected', { detail })); } catch (_) {}
        rawCall('bridge.requestFreshState', {}, 5000, { trackLiveness: false }).catch(() => {});
    }

    function markHealthy() {
        consecutiveTimeouts = 0;
        probeAttempt = 0;
        if (probeTimer !== null) {
            clearTimeout(probeTimer);
            probeTimer = null;
        }
        if (!degraded) return;
        degraded = false;
        emitReconnected();
    }

    function scheduleProbe() {
        if (!hasWebView || !degraded || probeTimer !== null) return;
        const delay = PROBE_DELAYS_MS[Math.min(probeAttempt, PROBE_DELAYS_MS.length - 1)];
        probeTimer = setTimeout(() => {
            probeTimer = null;
            if (!degraded) return;
            probeAttempt++;
            rawCall('bridge.ping', {}, PROBE_TIMEOUT_MS, { probe: true, trackLiveness: false })
                .catch(() => { if (degraded) scheduleProbe(); });
        }, delay);
    }

    function noteTimeout() {
        consecutiveTimeouts++;
        if (consecutiveTimeouts < DEGRADED_TIMEOUT_THRESHOLD || degraded) return;
        degraded = true;
        probeAttempt = 0;
        scheduleProbe();
    }

    function shouldFailFast(method, options) {
        if (!degraded || method === 'bridge.ping') return false;
        return method === 'getTopProcesses' || !!(options && options.background === true);
    }

    function degradedError(method) {
        const error = new Error('Bridge degraded: ' + method);
        error.code = 'bridge_degraded';
        return error;
    }

    if (hasWebView) {
        window.chrome.webview.addEventListener('message', (e) => {
            const msg = e.data;
            if (!msg || typeof msg !== 'object') return;
            if (msg.event) {
                emit(msg.event, msg.data);
                return;
            }
            if (!msg.id || !pending.has(msg.id)) return;

            const entry = pending.get(msg.id);
            pending.delete(msg.id);
            clearTimeout(entry.timeout);
            markHealthy();
            if (msg.ok) entry.resolve(msg.result);
            else {
                const err = new Error(msg.error || 'Bridge error');
                err.code = msg.code || 'unknown';
                err.detail = msg.error || '';
                entry.reject(err);
            }
        });
    }

    function rawCall(method, payload, timeoutMs, meta) {
        if (!hasWebView)
            return Promise.reject(new Error('Bridge non disponibile (anteprima browser)'));

        const metadata = meta || {};
        return new Promise((resolve, reject) => {
            const id = 'rpc-' + sessionId + '-' + (nextId++);
            const timeout = setTimeout(() => {
                if (!pending.has(id)) return;
                pending.delete(id);
                if (metadata.trackLiveness !== false) noteTimeout();
                reject(new Error('Timeout: ' + method));
            }, timeoutMs > 0 ? timeoutMs : DEFAULT_RPC_TIMEOUT_MS);
            pending.set(id, { resolve, reject, timeout });
            try {
                window.chrome.webview.postMessage({ id, method, payload: payload || {} });
            } catch (err) {
                pending.delete(id);
                clearTimeout(timeout);
                reject(err);
            }
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

    function callTopProcesses(payload, options) {
        const policy = topProcessPolicy();
        if (!policy.allowed) return Promise.resolve(topProcessesCache);
        if (shouldFailFast('getTopProcesses', options))
            return Promise.reject(degradedError('getTopProcesses'));

        const now = Date.now();
        if (topProcessesInFlight) return topProcessesInFlight;
        if (policy.intervalMs > 0 && topProcessesLastCallAt > 0 &&
            now - topProcessesLastCallAt < policy.intervalMs) {
            return Promise.resolve(topProcessesCache);
        }

        topProcessesLastCallAt = now;
        topProcessesInFlight = rawCall('getTopProcesses', payload, options && options.timeoutMs)
            .then(result => {
                if (Array.isArray(result)) topProcessesCache = result;
                return result;
            })
            .finally(() => { topProcessesInFlight = null; });
        return topProcessesInFlight;
    }

    window.Host = {
        available: hasWebView,
        call(method, payload, options) {
            if (method === 'getTopProcesses') return callTopProcesses(payload, options);
            if (shouldFailFast(method, options)) return Promise.reject(degradedError(method));
            return rawCall(method, payload, options && options.timeoutMs);
        },
        on(eventName, handler) {
            if (!listeners.has(eventName)) listeners.set(eventName, []);
            const handlers = listeners.get(eventName);
            handlers.push(handler);
            return () => {
                const current = listeners.get(eventName);
                if (!current) return;
                const index = current.indexOf(handler);
                if (index >= 0) current.splice(index, 1);
                if (!current.length) listeners.delete(eventName);
            };
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

    function forwardError(message, stack) {
        try {
            rawCall('logError', { message: String(message || ''), stack: stack ? String(stack) : null }, 5000,
                { trackLiveness: false }).catch(() => {});
        } catch (_) {}
    }

    // Forward uncaught JS errors and rejected promises to the host log. A broken
    // render loop must not turn into an unbounded host-message/log loop.
    function reportToHost(message, stack) {
        if (!hasWebView) return;
        const now = Date.now();
        if (errorWindowStartedAt === 0 || now - errorWindowStartedAt >= ERROR_FORWARD_WINDOW_MS) {
            errorWindowStartedAt = now;
            errorForwarded = 0;
            errorSummaryForwarded = false;
        }

        if (errorForwarded < ERROR_FORWARD_LIMIT) {
            errorForwarded++;
            forwardError(message, stack);
            return;
        }

        if (!errorSummaryForwarded) {
            errorSummaryForwarded = true;
            forwardError('JS error log rate limit reached; suppressing further errors for 60 seconds.', null);
        }
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
