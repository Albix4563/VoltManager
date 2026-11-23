import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import vm from 'node:vm';
import { createDocumentHarness, event, FakeNode } from './helpers/dom-harness.mjs';

const layoutScript = readFileSync(
    new URL('../src/VoltManager/wwwroot/js/ui-reorganization.layout.js', import.meta.url),
    'utf8');
const appScript = readFileSync(
    new URL('../src/VoltManager/wwwroot/js/app.js', import.meta.url), 'utf8');
const indexHtml = readFileSync(
    new URL('../src/VoltManager/wwwroot/index.html', import.meta.url), 'utf8');
const sidebarCss = readFileSync(
    new URL('../src/VoltManager/wwwroot/css/redesign.css', import.meta.url), 'utf8');
const reorgCss = readFileSync(
    new URL('../src/VoltManager/wwwroot/css/ui-reorganization.css', import.meta.url), 'utf8');

const rule = (css, selector) => {
    const start = css.indexOf(`${selector}{`);
    assert.notEqual(start, -1, `Regola mancante: ${selector}`);
    return css.slice(start, css.indexOf('}', start) + 1);
};

function mountWireSidebarCollapse({ savedValue = null, isNarrow = false, throwStorage = false } = {}) {
    const sidebarBlock = appScript.match(/\(function wireSidebarCollapse\(\)[\s\S]*?\}\)\(\);/s)?.[0];
    assert.ok(sidebarBlock, 'wireSidebarCollapse IIFE mancante in app.js');

    const document = createDocumentHarness({ readyState: 'complete' });
    const nav = new FakeNode({ id: 'side-nav', tagName: 'NAV', dataset: { collapsed: 'false' } });
    const toggleBtn = new FakeNode({ id: 'btn-sidebar-toggle', tagName: 'BUTTON' });
    toggleBtn.setAttribute('aria-expanded', 'true');
    toggleBtn.setAttribute('aria-controls', 'side-nav');
    document.registerId(nav);
    document.registerId(toggleBtn);

    const store = new Map();
    if (savedValue !== null) store.set('volt.sidebarCollapsed', savedValue);
    let setCalls = 0;
    const localStorage = {
        getItem(key) {
            if (throwStorage) throw new Error('storage disabled');
            return store.has(key) ? store.get(key) : null;
        },
        setItem(key, value) {
            setCalls += 1;
            if (throwStorage) throw new Error('storage disabled');
            store.set(key, String(value));
        },
    };

    let narrow = isNarrow;
    let repositionCalls = 0;
    const window = {
        matchMedia: (query) => ({ matches: query === '(max-width: 900px)' ? narrow : false }),
    };

    vm.runInNewContext(sidebarBlock, {
        document,
        localStorage,
        window,
        _sidebarReposition: () => { repositionCalls += 1; },
    });

    return {
        document,
        nav,
        toggleBtn,
        store,
        getSetCalls: () => setCalls,
        getRepositionCalls: () => repositionCalls,
        setNarrow: (value) => { narrow = !!value; },
    };
}

test('la barra ridotta lascia più spazio senza bordo, ombra o pannello sfocato', () => {
    assert.match(rule(sidebarCss, ':root'), /--side-nav-w:\s*280px;/);
    assert.match(rule(sidebarCss, ':root'), /--side-nav-w-collapsed:\s*64px;/);
    const collapsed = rule(sidebarCss, 'body.sidebar-collapsed .side-nav');
    assert.match(collapsed, /width:\s*var\(--side-nav-w-collapsed\);/);
    assert.doesNotMatch(collapsed, /align-items:\s*center;/);
    assert.match(rule(sidebarCss, 'body.sidebar-collapsed .app-main'),
        /margin-left:\s*var\(--side-nav-w-collapsed\);/);
    for (const declaration of ['border-right: 0;', 'box-shadow: none;',
        'background: transparent;', 'backdrop-filter: none;', '-webkit-backdrop-filter: none;']) {
        assert.ok(collapsed.includes(declaration), declaration);
        assert.ok(rule(sidebarCss.slice(sidebarCss.indexOf('@media (max-width: 900px)')),
            'body .side-nav').includes(declaration), `Responsive: ${declaration}`);
    }
    assert.match(sidebarCss, /@media \(max-width: 900px\)\{\s*:root\{ --side-nav-w: var\(--side-nav-w-collapsed\); \}/);
});

test('header, toggle 52x48 a x=6 e colonna icone restano geometricamente stabili', () => {
    const headerHtml = indexHtml.match(/<div class="side-nav__header[^"]*">[\s\S]*?<\/div>\s*<!-- Navigation Links -->/)?.[0] || '';
    assert.ok(headerHtml, '.side-nav__header mancante in index.html');
    assert.match(headerHtml, /^<div class="side-nav__header[^"]*">\s*<button[^>]*id="btn-sidebar-toggle"/);
    assert.doesNotMatch(indexHtml, /id="btn-sidebar-expand"/);

    for (const selector of ['.side-nav__header', '.side-nav #nav-list', '.side-nav .mt-auto']) {
        const block = rule(sidebarCss, selector);
        assert.match(block, /padding-left:\s*6px;/);
        assert.match(block, /padding-right:\s*6px;/);
    }

    const toggle = rule(sidebarCss, '.side-nav__toggle');
    assert.match(toggle, /width:\s*52px;/);
    assert.match(toggle, /height:\s*48px;/);
    assert.match(toggle, /flex:\s*0 0 52px;/);

    const navItemExpanded = rule(sidebarCss, '.side-nav .nav-item');
    assert.match(navItemExpanded, /justify-content:\s*flex-start;/);
    assert.match(navItemExpanded, /padding-left:\s*13px;/);
    assert.match(navItemExpanded, /padding-right:\s*13px;/);
    assert.match(navItemExpanded, /min-height:\s*48px;/);

    const navItemCollapsed = rule(sidebarCss, 'body.sidebar-collapsed .side-nav .nav-item');
    assert.match(navItemCollapsed, /width:\s*52px;/);
    assert.match(navItemCollapsed, /min-height:\s*48px;/);
    assert.match(navItemCollapsed, /gap:\s*0;/);
    assert.doesNotMatch(navItemCollapsed, /justify-content:\s*center/);

    const sectionCollapsed = rule(sidebarCss, 'body.sidebar-collapsed .side-nav .nav-section-label');
    assert.match(sectionCollapsed, /opacity:\s*0;/);
    assert.doesNotMatch(sectionCollapsed, /max-height:\s*0|padding:\s*0/);

    const responsive = sidebarCss.slice(sidebarCss.indexOf('@media (max-width: 900px)'));
    const responsiveNavItem = rule(responsive, '.side-nav .nav-item');
    assert.match(responsiveNavItem, /width:\s*52px;/);
    assert.match(responsiveNavItem, /min-height:\s*48px;/);
    assert.doesNotMatch(responsiveNavItem, /justify-content:\s*center/);

    assert.doesNotMatch(reorgCss, /#side-nav\[data-collapsed="true"\] #nav-list\s*\{[^}]*padding/);
    assert.doesNotMatch(reorgCss, /#side-nav\[data-collapsed="true"\] #nav-list \.nav-item\s*\{[^}]*justify-content:\s*center/);
});

test('wireSidebarCollapse ripristina stato salvato e alterna subito anche su click rapidi senza transitionend', () => {
    const restored = mountWireSidebarCollapse({ savedValue: '1' });
    assert.equal(restored.document.body.classList.contains('sidebar-collapsed'), true);
    assert.equal(restored.nav.dataset.collapsed, 'true');
    assert.equal(restored.toggleBtn.getAttribute('aria-expanded'), 'false');
    assert.equal(restored.toggleBtn.title, 'Espandi barra laterale');
    assert.equal(restored.toggleBtn.getAttribute('aria-label'), 'Espandi barra laterale');
    assert.equal(restored.getSetCalls(), 0, 'boot non deve riscrivere localStorage');
    assert.equal(restored.getRepositionCalls(), 1);
    assert.equal(restored.nav.listenerCount('transitionend'), 0);

    const fresh = mountWireSidebarCollapse();
    assert.equal(fresh.document.body.classList.contains('sidebar-collapsed'), false);
    assert.equal(fresh.nav.dataset.collapsed, 'false');
    assert.equal(fresh.toggleBtn.getAttribute('aria-expanded'), 'true');
    assert.equal(fresh.toggleBtn.title, 'Comprimi barra laterale');
    assert.equal(fresh.toggleBtn.getAttribute('aria-label'), 'Comprimi barra laterale');
    assert.equal(fresh.nav.listenerCount('transitionend'), 0);

    // Rapid consecutive clicks with zero transitionend events must reverse immediately.
    const expectedStates = [true, false, true, false, true];
    for (let i = 0; i < expectedStates.length; i += 1) {
        const expected = expectedStates[i];
        fresh.toggleBtn.dispatchEvent(event('click'));
        assert.equal(fresh.document.body.classList.contains('sidebar-collapsed'), expected);
        assert.equal(fresh.document.body.classList.contains('sidebar-expanding'), false);
        assert.equal(fresh.document.body.classList.contains('sidebar-collapsing'), false);
        assert.equal(fresh.nav.dataset.collapsed, expected ? 'true' : 'false');
        assert.equal(fresh.toggleBtn.getAttribute('aria-expanded'), expected ? 'false' : 'true');
        assert.equal(fresh.toggleBtn.title, expected ? 'Espandi barra laterale' : 'Comprimi barra laterale');
        assert.equal(fresh.toggleBtn.getAttribute('aria-label'), expected ? 'Espandi barra laterale' : 'Comprimi barra laterale');
        assert.equal(fresh.store.get('volt.sidebarCollapsed'), expected ? '1' : '0');
    }
    assert.equal(fresh.getSetCalls(), expectedStates.length);
    assert.equal(fresh.getRepositionCalls(), 1 + expectedStates.length);
});

test('wireSidebarCollapse preserva la preferenza salvata sotto <=900px e tollera errori localStorage', () => {
    const narrow = mountWireSidebarCollapse({ savedValue: '0', isNarrow: true });
    assert.equal(narrow.document.body.classList.contains('sidebar-collapsed'), false);
    assert.equal(narrow.store.get('volt.sidebarCollapsed'), '0');
    assert.equal(narrow.getSetCalls(), 0);

    narrow.toggleBtn.dispatchEvent(event('click'));
    assert.equal(narrow.store.get('volt.sidebarCollapsed'), '0');
    assert.equal(narrow.getSetCalls(), 0);
    assert.equal(narrow.document.body.classList.contains('sidebar-collapsed'), false);

    narrow.setNarrow(false);
    narrow.toggleBtn.dispatchEvent(event('click'));
    assert.equal(narrow.store.get('volt.sidebarCollapsed'), '1');
    assert.equal(narrow.document.body.classList.contains('sidebar-collapsed'), true);

    const noStorage = mountWireSidebarCollapse({ throwStorage: true });
    assert.equal(noStorage.document.body.classList.contains('sidebar-collapsed'), false);
    noStorage.toggleBtn.dispatchEvent(event('click'));
    assert.equal(noStorage.document.body.classList.contains('sidebar-collapsed'), true);
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
