/**
 * Central lifecycle for route-owned frontend modules.
 * Modules own their listeners/timers and expose init/activate/deactivate/dispose.
 */
(function () {
    if (window.VoltViewLifecycle) return;

    const modules = new Map();
    let currentRoute = { view: null, subviews: {} };
    let disposed = false;

    function normalizeRoute(route) {
        return {
            view: route && route.view ? String(route.view) : null,
            subviews: { ...((route && route.subviews) || {}) },
        };
    }

    function isMatch(record, route) {
        return typeof record.module.matches === 'function'
            ? !!record.module.matches(route)
            : false;
    }

    function initialize(record) {
        if (record.initialized) return;
        record.module.init?.();
        record.initialized = true;
    }

    function register(id, module) {
        if (disposed) throw new Error('VoltViewLifecycle is disposed');
        const key = String(id || '').trim();
        if (!key) throw new Error('Lifecycle module id is required');
        if (!module || typeof module !== 'object') throw new Error('Lifecycle module is required');
        if (modules.has(key)) throw new Error('Lifecycle module already registered: ' + key);

        const record = { module, initialized: false, active: false, disposed: false };
        modules.set(key, record);

        if (isMatch(record, currentRoute)) {
            initialize(record);
            record.module.activate?.(currentRoute);
            record.active = true;
        }
        return () => unregister(key);
    }

    function unregister(id) {
        const record = modules.get(id);
        if (!record) return false;
        if (record.active) {
            record.module.deactivate?.(currentRoute);
            record.active = false;
        }
        if (record.initialized && !record.disposed) {
            record.module.dispose?.();
            record.disposed = true;
        }
        modules.delete(id);
        return true;
    }

    function transition(route) {
        if (disposed) return;
        const nextRoute = normalizeRoute(route);

        for (const record of modules.values()) {
            if (record.active && !isMatch(record, nextRoute)) {
                record.module.deactivate?.(nextRoute);
                record.active = false;
            }
        }

        currentRoute = nextRoute;
        for (const record of modules.values()) {
            if (!record.active && isMatch(record, currentRoute)) {
                initialize(record);
                record.module.activate?.(currentRoute);
                record.active = true;
            }
        }
    }

    function dispose() {
        if (disposed) return;
        disposed = true;
        for (const record of modules.values()) {
            if (record.active) {
                record.module.deactivate?.(currentRoute);
                record.active = false;
            }
            if (record.initialized && !record.disposed) {
                record.module.dispose?.();
                record.disposed = true;
            }
        }
        modules.clear();
    }

    window.VoltViewLifecycle = {
        register,
        unregister,
        transition,
        dispose,
        route: () => normalizeRoute(currentRoute),
    };

    window.addEventListener?.('unload', dispose, { once: true });
})();
