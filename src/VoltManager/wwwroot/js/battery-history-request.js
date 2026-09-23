(function (root, factory) {
    const api = factory();
    if (typeof module === 'object' && module.exports) {
        module.exports = api;
    } else {
        root.VoltManagerBatteryHistory = api;
    }
}(typeof globalThis !== 'undefined' ? globalThis : this, function () {
    const allowedHours = new Set([6, 24, 48]);

    function normalizeHours(value, fallback) {
        const hours = Number(value);
        return allowedHours.has(hours) ? hours : fallback;
    }

    function formatSelectedWindow(value) {
        return normalizeHours(value, 24) + 'h';
    }

    function createBatteryHistoryRequestCoordinator({ request, render, initialHours = 24 }) {
        if (typeof request !== 'function' || typeof render !== 'function') {
            throw new TypeError('Battery history coordinator requires request and render functions.');
        }

        let selectedHours = normalizeHours(initialHours, 24);
        let requestSequence = 0;

        async function run(hours) {
            selectedHours = normalizeHours(hours, selectedHours);
            const requestHours = selectedHours;
            const requestId = ++requestSequence;
            const payload = await request(requestHours);

            if (requestId !== requestSequence || requestHours !== selectedHours) {
                return false;
            }

            render(payload, requestHours);
            return true;
        }

        function setHours(hours) {
            selectedHours = normalizeHours(hours, selectedHours);
            return selectedHours;
        }

        return {
            getHours: () => selectedHours,
            setHours,
            refresh: () => run(selectedHours),
            select: hours => run(hours),
        };
    }

    return {
        createBatteryHistoryRequestCoordinator,
        formatSelectedWindow,
    };
}));
