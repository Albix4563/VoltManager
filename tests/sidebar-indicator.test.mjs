import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import vm from 'node:vm';

const layoutScript = readFileSync(
    new URL('../src/VoltManager/wwwroot/js/ui-reorganization.layout.js', import.meta.url),
    'utf8');

const sidebarCss = readFileSync(
    new URL('../src/VoltManager/wwwroot/css/redesign.css', import.meta.url), 'utf8');
const reorgCss = readFileSync(
    new URL('../src/VoltManager/wwwroot/css/ui-reorganization.css', import.meta.url), 'utf8');

const rule = (css, selector) => {
    const start = css.indexOf(`${selector}{`);
    assert.notEqual(start, -1, `Regola mancante: ${selector}`);
    return css.slice(start, css.indexOf('}', start) + 1);
};

test('la barra ridotta lascia più spazio senza bordo, ombra o pannello sfocato', () => {
    assert.match(rule(sidebarCss, ':root'), /--side-nav-w:\s*280px;/);
    assert.match(rule(sidebarCss, ':root'), /--side-nav-w-collapsed:\s*64px;/);
    const collapsed = rule(sidebarCss, 'body.sidebar-collapsed .side-nav');
    assert.match(collapsed, /width:\s*var\(--side-nav-w-collapsed\);/);
    assert.match(rule(sidebarCss, 'body.sidebar-collapsed .app-main'),
        /margin-left:\s*var\(--side-nav-w-collapsed\);/);
    for (const declaration of ['border-right: 0;', 'box-shadow: none;',
        'background: transparent;', 'backdrop-filter: none;']) {
        assert.ok(collapsed.includes(declaration), declaration);
        assert.ok(rule(sidebarCss.slice(sidebarCss.indexOf('@media (max-width: 900px)')),
            'body .side-nav').includes(declaration), `Responsive: ${declaration}`);
    }
    assert.match(sidebarCss, /@media \(max-width: 900px\)\{\s*:root\{ --side-nav-w: var\(--side-nav-w-collapsed\); \}/);
});

test('i controlli restano accessibili e rientrano nella barra da 64 px', () => {
    const navItem = rule(sidebarCss, 'body.sidebar-collapsed .side-nav .nav-item');
    assert.match(navItem, /width:\s*52px;/);
    assert.match(navItem, /min-height:\s*48px;/);
    for (const selector of ['body.sidebar-collapsed .side-nav__header',
        'body.sidebar-collapsed .side-nav #nav-list',
        'body.sidebar-collapsed .side-nav .mt-auto']) {
        const block = rule(sidebarCss, selector);
        assert.match(block, /padding-left:\s*6px;/);
        assert.match(block, /padding-right:\s*6px;/);
    }
    assert.doesNotMatch(reorgCss, /#side-nav\[data-collapsed="true"\] #nav-list\s*\{[^}]*padding/);
});

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
