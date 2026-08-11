import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import vm from 'node:vm';

const layoutScript = readFileSync(
    new URL('../src/VoltManager/wwwroot/js/ui-reorganization.layout.js', import.meta.url),
    'utf8');

test('positions the sidebar indicator relative to its offset parent', () => {
    const parent = {
        getBoundingClientRect: () => ({ top: 100 })
    };
    const indicator = {
        offsetParent: parent,
        parentElement: parent,
        style: {}
    };
    const list = {
        getBoundingClientRect: () => ({ top: 124 })
    };
    const link = {
        getBoundingClientRect: () => ({ top: 188, height: 48 })
    };
    const elements = {
        'nav-indicator': indicator,
        'nav-list': list
    };
    const context = {
        document: {
            getElementById: id => elements[id] || null
        },
        window: {}
    };

    vm.runInNewContext(layoutScript, context);
    context.window.VoltUiReorg.positionIndicator(link);

    assert.equal(indicator.style.top, '88px');
    assert.equal(indicator.style.height, '48px');
});
