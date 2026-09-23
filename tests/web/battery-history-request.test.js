const test = require('node:test');
const assert = require('node:assert/strict');

const {
    createBatteryHistoryRequestCoordinator,
    formatSelectedWindow,
} = require('../../src/VoltManager/wwwroot/js/battery-history-request.js');

function deferred() {
    let resolve;
    const promise = new Promise(r => { resolve = r; });
    return { promise, resolve };
}

test('latest battery history range wins when an older request resolves later', async () => {
    const requests = [];
    const rendered = [];
    const coordinator = createBatteryHistoryRequestCoordinator({
        initialHours: 24,
        request: hours => {
            const pending = deferred();
            requests.push({ hours, pending });
            return pending.promise;
        },
        render: (payload, hours) => rendered.push({ payload, hours }),
    });

    const first = coordinator.refresh();
    const second = coordinator.select(6);

    assert.deepEqual(requests.map(r => r.hours), [24, 6]);

    requests[1].pending.resolve({ id: 'six-hours' });
    await second;
    requests[0].pending.resolve({ id: 'stale-24-hours' });
    await first;

    assert.deepEqual(rendered, [
        { payload: { id: 'six-hours' }, hours: 6 },
    ]);
});

test('latest request wins even after selecting the same range again', async () => {
    const requests = [];
    const rendered = [];
    const coordinator = createBatteryHistoryRequestCoordinator({
        initialHours: 6,
        request: hours => {
            const pending = deferred();
            requests.push({ hours, pending });
            return pending.promise;
        },
        render: (payload, hours) => rendered.push({ payload, hours }),
    });

    const first = coordinator.refresh();
    const second = coordinator.select(6);

    requests[1].pending.resolve({ id: 'newer' });
    await second;
    requests[0].pending.resolve({ id: 'older' });
    await first;

    assert.deepEqual(rendered, [
        { payload: { id: 'newer' }, hours: 6 },
    ]);
});

test('battery history window text follows the selected range', () => {
    assert.equal(formatSelectedWindow(6), '6h');
    assert.equal(formatSelectedWindow(24), '24h');
    assert.equal(formatSelectedWindow(48), '48h');
});

test('battery history range can be stored without starting a request', () => {
    let requestCount = 0;
    const coordinator = createBatteryHistoryRequestCoordinator({
        initialHours: 24,
        request: async () => {
            requestCount++;
            return {};
        },
        render: () => {},
    });

    coordinator.setHours(48);

    assert.equal(coordinator.getHours(), 48);
    assert.equal(requestCount, 0);
});
