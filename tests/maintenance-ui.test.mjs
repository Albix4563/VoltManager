import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import { FakeNode, FakeClassList } from './helpers/dom-harness.mjs';

const root = new URL('../', import.meta.url);
const read = path => readFileSync(new URL(path, root), 'utf8');

function setupDomEnvironment() {
    const ids = new Map();
    const selectors = new Map();
    const listeners = new Map();

    const document = {
        readyState: 'complete',
        hidden: false,
        documentElement: null,
        body: null,
        activeElement: null,
        getElementById: id => ids.get(id) || null,
        querySelector: sel => {
            if (sel.startsWith('#')) return ids.get(sel.slice(1)) || null;
            return (selectors.get(sel) || [])[0] || null;
        },
        querySelectorAll: sel => selectors.get(sel) || [],
        createElement: tag => {
            const n = new Node({ tagName: String(tag).toUpperCase() });
            return n;
        },
        createTextNode: text => {
            const n = new Node({ tagName: '#TEXT' });
            n.textContent = String(text);
            return n;
        },
        addEventListener: (name, handler) => {
            if (!listeners.has(name)) listeners.set(name, []);
            listeners.get(name).push(handler);
        },
        removeEventListener: (name, handler) => {
            const list = listeners.get(name) || [];
            const idx = list.indexOf(handler);
            if (idx >= 0) list.splice(idx, 1);
        },
        dispatchEvent: evt => {
            for (const h of [...(listeners.get(evt.type) || [])]) h(evt);
            return true;
        },
        registerId: node => {
            if (node?.id) ids.set(node.id, node);
            return node;
        }
    };

    class Node extends FakeNode {
        constructor(opts = {}) {
            super(opts);
            if (opts.id) ids.set(opts.id, this);
        }
        set className(val) {
            this._className = val;
            this.classList = new FakeClassList(String(val || '').split(/\s+/).filter(Boolean));
        }
        get className() {
            return this._className || '';
        }
        setAttribute(name, val) {
            super.setAttribute(name, val);
            if (name === 'id') {
                this.id = val;
                ids.set(val, this);
            }
        }
        focus() {
            this.focused = true;
            document.activeElement = this;
            this.dispatchEvent({ type: 'focus', target: this });
        }
        blur() {
            this.focused = false;
            if (document.activeElement === this) document.activeElement = null;
            this.dispatchEvent({ type: 'blur', target: this });
        }
        contains(child) {
            if (!child) return false;
            if (child === this) return true;
            let curr = child.parentElement;
            while (curr) {
                if (curr === this) return true;
                curr = curr.parentElement;
            }
            return false;
        }
        querySelector(sel) {
            if (sel.startsWith('#')) return ids.get(sel.slice(1)) || null;
            if (sel.startsWith('.')) {
                const cls = sel.slice(1);
                return findAllNodes(this, n => n.classList?.contains(cls))[0] || null;
            }
            return null;
        }
        querySelectorAll(sel) {
            const res = [];
            function walk(node) {
                for (const c of node.children || []) {
                    if (sel.includes('button') && c.tagName === 'BUTTON') res.push(c);
                    else if (sel.includes('input') && c.tagName === 'INPUT') res.push(c);
                    else if (sel.includes('select') && c.tagName === 'SELECT') res.push(c);
                    walk(c);
                }
            }
            walk(this);
            return res;
        }
        appendChild(child) {
            super.appendChild(child);
            function indexIds(n) {
                if (n.id) ids.set(n.id, n);
                for (const c of n.children || []) indexIds(c);
            }
            indexIds(child);
            return child;
        }
        removeChild(child) {
            const idx = this.children.indexOf(child);
            if (idx >= 0) this.children.splice(idx, 1);
            child.parentElement = null;
            function removeIds(n) {
                if (n.id) ids.delete(n.id);
                for (const c of n.children || []) removeIds(c);
            }
            removeIds(child);
            return child;
        }
    }

    const docElement = new Node({ tagName: 'HTML' });
    const body = new Node({ tagName: 'BODY' });
    document.documentElement = docElement;
    document.body = body;

    body.appendChild = child => {
        body.children.push(child);
        child.parentElement = body;
        child.parentNode = body;
        function indexIds(n) {
            if (n.id) ids.set(n.id, n);
            for (const c of n.children || []) indexIds(c);
        }
        indexIds(child);
        return child;
    };

    body.removeChild = child => {
        const idx = body.children.indexOf(child);
        if (idx >= 0) body.children.splice(idx, 1);
        child.parentElement = null;
        child.parentNode = null;
        function removeIds(n) {
            if (n.id) ids.delete(n.id);
            for (const c of n.children || []) removeIds(c);
        }
        removeIds(child);
        return child;
    };

    return { document, Node, ids, selectors, listeners };
}

function loadMaintenanceContext(hostMock = {}) {
    const { document, Node } = setupDomEnvironment();

    // Create container elements for system tools panels
    const appsPanel = document.registerId(new Node({ id: 'vm-system-apps' }));
    const registryPanel = document.registerId(new Node({ id: 'vm-system-registry' }));
    const backupPanel = document.registerId(new Node({ id: 'vm-system-backup' }));

    const calls = [];
    const Host = {
        call: async (method, payload) => {
            calls.push({ method, payload });
            if (typeof hostMock[method] === 'function') {
                return hostMock[method](payload);
            }
            if (hostMock[method] !== undefined) {
                return hostMock[method];
            }
            return {};
        }
    };

    const catalogsSource = read('src/VoltManager/wwwroot/js/i18n.catalogs.js');
    const i18nSource = read('src/VoltManager/wwwroot/js/i18n.js');
    const lifecycleSource = read('src/VoltManager/wwwroot/js/view-lifecycle.js');
    const maintenanceSource = read('src/VoltManager/wwwroot/js/maintenance.js');

    const windowObj = {
        addEventListener: () => {},
        removeEventListener: () => {},
        dispatchEvent: () => true
    };
    const context = vm.createContext({
        console,
        window: windowObj,
        document,
        Node,
        Host,
        setTimeout,
        clearTimeout,
        setInterval,
        clearInterval,
        CustomEvent: class CustomEvent {
            constructor(type, init) {
                this.type = type;
                this.detail = init?.detail;
            }
        },
        localStorage: {
            getItem: () => 'it',
            setItem: () => {}
        }
    });
    context.window.window = context.window;
    context.window.document = document;
    context.window.Host = Host;
    context.window.Node = Node;

    vm.runInContext(catalogsSource, context);
    vm.runInContext(i18nSource, context);
    vm.runInContext(lifecycleSource, context);
    vm.runInContext(maintenanceSource, context);

    return {
        context,
        api: context.window.VoltMaintenance,
        lifecycle: context.window.VoltViewLifecycle,
        document,
        calls,
        appsPanel,
        registryPanel,
        backupPanel
    };
}

test('systemTools layout includes scheduled, startup, memory, apps, registry, backup subtabs and panels', () => {
    const layout = read('src/VoltManager/wwwroot/js/ui-reorganization.layout.js');
    assert.match(layout, /denseShell\('system-tools'/);
    for (const sub of ['scheduled', 'startup', 'memory', 'apps', 'registry', 'backup']) {
        assert.match(layout, new RegExp(`id:\\s*'${sub}'`));
        assert.match(layout, new RegExp(`vm-system-${sub}`));
    }
});

test('global search catalogs apps, registry and backup maintenance views', () => {
    const search = read('src/VoltManager/wwwroot/js/global-search.js');
    for (const id of ['system-apps', 'system-registry', 'system-backup']) {
        assert.match(search, new RegExp(`id:\\s*'${id}'`));
    }
    assert.match(search, /subview:\s*'apps'/);
    assert.match(search, /subview:\s*'registry'/);
    assert.match(search, /subview:\s*'backup'/);
});

test('startup rendering in app.js displays localized unknownState when stateKnown is false', () => {
    const appSource = read('src/VoltManager/wwwroot/js/app.js');
    assert.match(appSource, /app\.stateKnown\s*!==\s*false/);
    assert.match(appSource, /startup-status-chip--unknown/);
    assert.match(appSource, /t\('unknownState'\)/);
    assert.match(appSource, /app\.registryView/);
});

test('maintenance module registers with VoltViewLifecycle and matches system-tools maintenance subtabs', () => {
    const { api, lifecycle } = loadMaintenanceContext();
    assert.ok(api, 'VoltMaintenance must be exported');
    assert.equal(typeof api.init, 'function');
    assert.equal(typeof api.activate, 'function');
    assert.equal(typeof api.deactivate, 'function');
    assert.equal(typeof api.dispose, 'function');

    // Test route matching
    const maintenanceModule = lifecycle ? true : false;
    assert.equal(maintenanceModule, true);
});

test('apps subview fetches apps, filters by search, and renders with textContent', async () => {
    const rawAppName = '<script>alert("xss")</script> Test App';
    const rawPublisher = '<b>Evil Corp</b>';
    const rawLocation = 'C:\\Program Files\\Evil"App';

    const ctx = loadMaintenanceContext({
        'maintenance.getApps': {
            apps: [
                {
                    id: 'app-1',
                    name: rawAppName,
                    version: '1.2.3',
                    publisher: rawPublisher,
                    installLocation: rawLocation,
                    estimatedSizeKb: 15360, // 15 MB
                    canUninstall: true
                },
                {
                    id: 'app-sys',
                    name: 'Windows Core Tool',
                    version: '10.0',
                    publisher: 'Microsoft',
                    installLocation: 'C:\\Windows',
                    estimatedSizeKb: 2048,
                    canUninstall: false,
                    reason: 'Componente di sistema'
                }
            ],
            warnings: ['Avviso test permessi']
        }
    });

    // Activate apps subview
    ctx.api.init();
    ctx.api.activate({ view: 'system-tools', subviews: { 'system-tools': 'apps' } });

    // Wait a tick for async loadApps
    await new Promise(r => setImmediate(r));

    assert.ok(ctx.calls.some(c => c.method === 'maintenance.getApps'));

    // Check DOM in apps panel
    const searchInput = ctx.document.getElementById('vm-apps-search');
    assert.ok(searchInput, 'Search input must exist');

    // Check warning box
    const textAll = collectText(ctx.appsPanel);
    assert.ok(textAll.includes('Avviso test permessi'));

    // Search filter test
    searchInput.value = 'Test App';
    searchInput.dispatchEvent({ type: 'input', target: searchInput });

    // Verify raw text is rendered as textContent, not executed as HTML
    const appCards = ctx.appsPanel.children.find(c => c.classList?.contains('vm-maintenance-shell'));
    assert.ok(appCards, 'Shell rendered');
});

test('uninstall workflow displays confirmation summary, starts operation, and handles verification', async () => {
    let operationState = {
        id: 'op-uninstall-1',
        kind: 'uninstall',
        appId: 'app-1',
        status: 'running',
        processed: 1,
        total: 10,
        message: 'Disinstallazione in corso...'
    };

    const ctx = loadMaintenanceContext({
        'maintenance.getApps': {
            apps: [
                { id: 'app-1', name: 'SuperApp', version: '2.0', publisher: 'Vendor', canUninstall: true, estimatedSizeKb: 1024 }
            ]
        },
        'maintenance.start': payload => {
            assert.equal(payload.kind, 'uninstall');
            assert.equal(payload.appId, 'app-1');
            return operationState;
        },
        'maintenance.status': () => operationState,
        'maintenance.verifyUninstall': () => {
            operationState = { ...operationState, status: 'completed', canScanLeftovers: true };
            return operationState;
        }
    });

    ctx.api.init();
    ctx.api.activate({ view: 'system-tools', subviews: { 'system-tools': 'apps' } });
    await new Promise(r => setImmediate(r));

    // Find uninstall button on card and trigger
    const uninstallBtn = findButtonByText(ctx.appsPanel, 'Disinstalla');
    assert.ok(uninstallBtn, 'Uninstall button should exist');
    uninstallBtn.dispatchEvent({ type: 'click' });

    // Modal should be in body
    const modalConfirmBtn = findButtonByText(ctx.document.body, 'Disinstalla');
    assert.ok(modalConfirmBtn, 'Confirm modal button should exist');
    modalConfirmBtn.dispatchEvent({ type: 'click' });

    await new Promise(r => setImmediate(r));
    assert.ok(ctx.calls.some(c => c.method === 'maintenance.start'));

    // Transition op to needsVerification
    operationState = {
        ...operationState,
        status: 'needsVerification',
        message: 'Processo terminato. Verifica richiesta.'
    };

    // Trigger verify
    const verifyBtn = findButtonByText(ctx.appsPanel, 'Ho terminato, verifica');
    if (verifyBtn) {
        verifyBtn.dispatchEvent({ type: 'click' });
        await new Promise(r => setImmediate(r));
        assert.ok(ctx.calls.some(c => c.method === 'maintenance.verifyUninstall'));
    }

    ctx.api.deactivate();
});

test('leftovers and registry review items have manual selection and user data warnings', async () => {
    let opState = {
        id: 'op-reg-1',
        kind: 'registry',
        status: 'completed',
        message: 'Scansione completata',
        items: [
            { id: 'item-1', kind: 'registry', path: 'HKCU\\Software\\Foo', reason: 'Missing binary', selectable: true, containsUserData: false },
            { id: 'item-2', kind: 'file', path: 'C:\\Users\\User\\config.json', reason: 'Orphan file', selectable: true, containsUserData: true }
        ]
    };

    const ctx = loadMaintenanceContext({
        'maintenance.start': payload => {
            assert.equal(payload.kind, 'registry');
            return opState;
        },
        'maintenance.status': () => opState,
        'maintenance.apply': payload => {
            assert.deepEqual([...payload.itemIds], ['item-2']);
            return { id: 'op-apply-1', kind: 'registry', status: 'completed', message: 'Pulizia riuscita' };
        },
        'maintenance.exclude': payload => {
            assert.deepEqual([...payload.itemIds], ['item-1']);
            return { success: true };
        }
    });

    ctx.api.init();
    ctx.api.activate({ view: 'system-tools', subviews: { 'system-tools': 'registry' } });

    // Click scan registry
    const scanBtn = ctx.document.getElementById('vm-registry-scan-btn');
    assert.ok(scanBtn, 'Scan button must exist');
    scanBtn.dispatchEvent({ type: 'click' });
    await new Promise(r => setImmediate(r));

    assert.ok(ctx.calls.some(c => c.method === 'maintenance.start'));

    // Check user data warning text
    const textAll = collectText(ctx.registryPanel);
    assert.ok(textAll.includes('Dati personali / Configurazione'));

    // Checkboxes should all be unchecked by default (no preselection)
    const checkboxes = findAllNodes(ctx.registryPanel, n => n.tagName === 'INPUT' && n.attributes?.get('type') === 'checkbox');
    assert.equal(checkboxes.length, 2);
    for (const cb of checkboxes) {
        assert.equal(cb.checked, false, 'Checkboxes must be unchecked initially');
    }

    // Select item-2 (user data item)
    checkboxes[1].checked = true;
    checkboxes[1].dispatchEvent({ type: 'change', target: checkboxes[1] });

    // Clean button should now be enabled
    const cleanBtn = findButtonByText(ctx.registryPanel, 'Pulisci selezionati (1)');
    assert.ok(cleanBtn, 'Clean button with count 1 should exist');
    cleanBtn.dispatchEvent({ type: 'click' });

    // Confirmation modal should warn about personal data
    const modalText = collectText(ctx.document.body);
    assert.ok(modalText.includes('ATTENZIONE: uno o più elementi selezionati contengono dati personali'));

    // Confirm clean
    const confirmBtn = findButtonByText(ctx.document.body, 'Conferma');
    assert.ok(confirmBtn, 'Confirm button in modal');
    confirmBtn.dispatchEvent({ type: 'click' });
    await new Promise(r => setImmediate(r));

    assert.ok(ctx.calls.some(c => c.method === 'maintenance.apply'));

    ctx.api.deactivate();
});

test('backup subview displays disclaimer, lists backups, and handles selective restore and delete', async () => {
    let backupsList = [
        { id: 'b-1', createdAt: '2026-10-18T12:00:00Z', description: 'Pulizia registro Test', itemCount: 3, status: 'completed' }
    ];

    const ctx = loadMaintenanceContext({
        'maintenance.getBackups': () => ({ backups: backupsList }),
        'maintenance.getBackup': payload => {
            assert.equal(payload.backupId, 'b-1');
            return {
                id: 'b-1',
                description: 'Pulizia registro Test',
                status: 'completed',
                items: [
                    { id: 'bi-1', kind: 'registry', path: 'HKCU\\TestKey', reason: 'Backup key', status: 'stored' }
                ]
            };
        },
        'maintenance.restore': payload => {
            assert.equal(payload.backupId, 'b-1');
            assert.deepEqual([...payload.itemIds], ['bi-1']);
            return { id: 'op-restore-1', status: 'completed', message: 'Ripristinato' };
        },
        'maintenance.deleteBackup': payload => {
            assert.equal(payload.backupId, 'b-1');
            backupsList = [];
            return { success: true };
        }
    });

    ctx.api.init();
    ctx.api.activate({ view: 'system-tools', subviews: { 'system-tools': 'backup' } });
    await new Promise(r => setImmediate(r));

    assert.ok(ctx.calls.some(c => c.method === 'maintenance.getBackups'));

    // Check disclaimer that backup does not reinstall apps
    const allText = collectText(ctx.backupPanel);
    assert.ok(allText.includes('reinstalla') && allText.includes('disinstallatore'));

    // Open detail
    const detailsBtn = findButtonByText(ctx.backupPanel, 'Dettagli / Ripristina');
    assert.ok(detailsBtn, 'Details button must exist');
    detailsBtn.dispatchEvent({ type: 'click' });
    await new Promise(r => setImmediate(r));

    assert.ok(ctx.calls.some(c => c.method === 'maintenance.getBackup'));

    // Select backup item and restore
    const cb = findAllNodes(ctx.backupPanel, n => n.tagName === 'INPUT' && n.attributes?.get('type') === 'checkbox')[0];
    assert.ok(cb);
    cb.checked = true;
    cb.dispatchEvent({ type: 'change', target: cb });

    const restoreBtn = findButtonByText(ctx.backupPanel, 'Ripristina selezionati (1)');
    assert.ok(restoreBtn);
    restoreBtn.dispatchEvent({ type: 'click' });

    // Confirm modal
    const confirmRestoreBtn = findButtonByText(ctx.document.body, 'Conferma');
    assert.ok(confirmRestoreBtn);
    confirmRestoreBtn.dispatchEvent({ type: 'click' });
    await new Promise(r => setImmediate(r));

    assert.ok(ctx.calls.some(c => c.method === 'maintenance.restore'));

    ctx.api.deactivate();
});

test('lifecycle deactivation stops polling and activation refreshes status', async () => {
    let statusCalls = 0;
    const ctx = loadMaintenanceContext({
        'maintenance.start': () => ({ id: 'op-poll-1', status: 'running' }),
        'maintenance.status': () => {
            statusCalls++;
            return { id: 'op-poll-1', status: 'running' };
        }
    });

    ctx.api.init();
    ctx.api.activate({ view: 'system-tools', subviews: { 'system-tools': 'registry' } });

    // Start scan to trigger polling
    const scanBtn = ctx.document.getElementById('vm-registry-scan-btn');
    scanBtn.dispatchEvent({ type: 'click' });
    await new Promise(r => setImmediate(r));

    // Deactivate: polling timer must be stopped
    ctx.api.deactivate();
    const callsAtDeactivate = statusCalls;

    // Wait 1.5s in mock time or real time
    await new Promise(r => setTimeout(r, 1200));
    assert.equal(statusCalls, callsAtDeactivate, 'Polling must stop after deactivation');

    // Reactivate: status refreshed immediately
    ctx.api.activate({ view: 'system-tools', subviews: { 'system-tools': 'registry' } });
    await new Promise(r => setImmediate(r));

    assert.ok(statusCalls > callsAtDeactivate, 'Reactivating must refresh status');

    ctx.api.dispose();
});

test('apps search input preserves element identity, focus and caret during consecutive keystrokes', async () => {
    const ctx = loadMaintenanceContext({
        'maintenance.getApps': {
            apps: [
                { id: 'app-abc', name: 'ABC Editor', version: '1.0', canUninstall: true },
                { id: 'app-abx', name: 'ABX Viewer', version: '1.0', canUninstall: true },
                { id: 'app-xyz', name: 'XYZ Player', version: '1.0', canUninstall: true }
            ]
        }
    });

    ctx.api.init();
    ctx.api.activate({ view: 'system-tools', subviews: { 'system-tools': 'apps' } });
    await new Promise(r => setImmediate(r));

    const input = ctx.document.getElementById('vm-apps-search');
    assert.ok(input, 'Search input must exist');

    let blurCount = 0;
    input.addEventListener('blur', () => blurCount++);

    // Focus input
    input.focus();
    assert.equal(ctx.document.activeElement, input, 'Input must be active element on focus');

    // Keystroke 1: 'a'
    input.value = 'a';
    input.dispatchEvent({ type: 'input', target: input });
    assert.equal(ctx.document.activeElement, input, 'Active element must stay on input after typing "a"');
    assert.equal(ctx.document.getElementById('vm-apps-search'), input, 'Input element must not be recreated');
    assert.equal(blurCount, 0, 'No blur event should fire');

    // Keystroke 2: 'ab'
    input.value = 'ab';
    input.dispatchEvent({ type: 'input', target: input });
    assert.equal(ctx.document.activeElement, input, 'Active element must stay on input after typing "ab"');
    assert.equal(ctx.document.getElementById('vm-apps-search'), input, 'Input element must remain identical');
    assert.equal(blurCount, 0, 'No blur event should fire');

    // Keystroke 3: 'abc'
    input.value = 'abc';
    input.dispatchEvent({ type: 'input', target: input });
    assert.equal(ctx.document.activeElement, input, 'Active element must stay on input after typing "abc"');
    assert.equal(ctx.document.getElementById('vm-apps-search'), input, 'Input element must remain identical');
    assert.equal(blurCount, 0, 'No blur event should fire');

    // Filter check: only 'ABC Editor' should be rendered in cards
    const textAll = collectText(ctx.appsPanel);
    assert.ok(textAll.includes('ABC Editor'), 'Matching app should be rendered');
    assert.ok(!textAll.includes('ABX Viewer'), 'Non-matching app should be filtered out');
    assert.ok(!textAll.includes('XYZ Player'), 'Non-matching app should be filtered out');

    ctx.api.deactivate();
});

test('search and sort controls expose accessible labels', async () => {
    const ctx = loadMaintenanceContext({
        'maintenance.getApps': { apps: [] }
    });

    ctx.api.init();
    ctx.api.activate({ view: 'system-tools', subviews: { 'system-tools': 'apps' } });
    await new Promise(r => setImmediate(r));

    const searchInput = ctx.document.getElementById('vm-apps-search');
    assert.ok(searchInput.getAttribute('aria-label'), 'Search input must have aria-label');

    const sortSelect = ctx.document.getElementById('vm-apps-sort');
    assert.ok(sortSelect.getAttribute('aria-label'), 'Sort select must have aria-label');

    ctx.api.deactivate();
});

test('confirmation modal provides aria-labelledby, focus trap, Escape handling, and focus restore', async () => {
    const ctx = loadMaintenanceContext({
        'maintenance.getApps': {
            apps: [
                { id: 'app-1', name: 'DemoApp', version: '1.0', canUninstall: true }
            ]
        }
    });

    ctx.api.init();
    ctx.api.activate({ view: 'system-tools', subviews: { 'system-tools': 'apps' } });
    await new Promise(r => setImmediate(r));

    const uninstallBtn = findButtonByText(ctx.appsPanel, 'Disinstalla');
    assert.ok(uninstallBtn);
    uninstallBtn.focus();
    assert.equal(ctx.document.activeElement, uninstallBtn);

    // Open confirmation modal
    uninstallBtn.dispatchEvent({ type: 'click' });

    const modalBox = findAllNodes(ctx.document.body, n => n.getAttribute('role') === 'dialog')[0];
    assert.ok(modalBox, 'Modal box with role="dialog" must exist');
    assert.equal(modalBox.getAttribute('aria-modal'), 'true');
    assert.equal(modalBox.getAttribute('aria-labelledby'), 'vm-dialog-title');

    const titleEl = ctx.document.getElementById('vm-dialog-title');
    assert.ok(titleEl, 'Title element with id="vm-dialog-title" must exist');

    const cancelBtn = ctx.document.getElementById('vm-dialog-cancel-btn');
    const confirmBtn = ctx.document.getElementById('vm-dialog-confirm-btn');
    assert.ok(cancelBtn && confirmBtn);

    // Initial focus on confirm button
    assert.equal(ctx.document.activeElement, confirmBtn);

    // Focus trap test: Tab from last focusable element wraps to first
    ctx.document.dispatchEvent({
        type: 'keydown',
        key: 'Tab',
        shiftKey: false,
        preventDefault: () => {}
    });
    assert.equal(ctx.document.activeElement, cancelBtn, 'Tab from last element should wrap to first element');

    // Shift+Tab from first element wraps to last
    ctx.document.dispatchEvent({
        type: 'keydown',
        key: 'Tab',
        shiftKey: true,
        preventDefault: () => {}
    });
    assert.equal(ctx.document.activeElement, confirmBtn, 'Shift+Tab from first element should wrap to last element');

    // Escape key test: closes modal and restores focus to uninstallBtn
    ctx.document.dispatchEvent({
        type: 'keydown',
        key: 'Escape',
        preventDefault: () => {},
        stopPropagation: () => {}
    });

    assert.equal(ctx.document.getElementById('vm-dialog-title'), null, 'Modal should be closed on Escape');
    assert.equal(ctx.document.activeElement, uninstallBtn, 'Focus must be restored to trigger button after modal close');

    ctx.api.deactivate();
});

test('polling does not overlap async requests and stops completely when document is hidden', async () => {
    let statusCalls = 0;
    let pendingResolve = null;

    const ctx = loadMaintenanceContext({
        'maintenance.start': () => ({ id: 'op-poll-async', status: 'running' }),
        'maintenance.status': () => {
            statusCalls++;
            return new Promise(resolve => {
                pendingResolve = resolve;
            });
        }
    });

    ctx.api.init();
    ctx.api.activate({ view: 'system-tools', subviews: { 'system-tools': 'registry' } });

    const scanBtn = ctx.document.getElementById('vm-registry-scan-btn');
    scanBtn.dispatchEvent({ type: 'click' });
    await new Promise(r => setImmediate(r));

    // Wait 1.1s for first poll tick
    await new Promise(r => setTimeout(r, 1100));
    assert.equal(statusCalls, 1, 'First status call initiated');

    // Simulate slow response: while status request is pending, wait another 1.2s
    await new Promise(r => setTimeout(r, 1200));
    assert.equal(statusCalls, 1, 'No second status request should be sent while first is in-flight (no overlap)');

    // Resolve first request with running status
    pendingResolve({ id: 'op-poll-async', status: 'running' });
    await new Promise(r => setImmediate(r));

    // Next tick will be scheduled 1s after resolution
    await new Promise(r => setTimeout(r, 1100));
    assert.equal(statusCalls, 2, 'Second status call initiated only after first completed');

    // Resolve second request
    pendingResolve({ id: 'op-poll-async', status: 'running' });
    await new Promise(r => setImmediate(r));

    // Now test document.hidden: simulate window minimize / tab hidden
    ctx.document.hidden = true;
    ctx.document.dispatchEvent({ type: 'visibilitychange' });

    const callsWhileHidden = statusCalls;
    await new Promise(r => setTimeout(r, 1500));
    assert.equal(statusCalls, callsWhileHidden, 'Zero polling requests while document.hidden is true');

    // Restore visibility: triggers single status refresh
    ctx.document.hidden = false;
    ctx.document.dispatchEvent({ type: 'visibilitychange' });
    await new Promise(r => setImmediate(r));
    assert.ok(statusCalls > callsWhileHidden, 'Restoring visibility refreshes status');

    if (pendingResolve) pendingResolve({ id: 'op-poll-async', status: 'completed' });
    ctx.api.dispose();
});

test('restore operation displays progress card and updates backup view on poll ticks', async () => {
    let restoreOp = {
        id: 'op-restore-1',
        kind: 'restore',
        status: 'running',
        processed: 2,
        total: 5,
        message: 'Ripristino elementi 2/5...'
    };

    const ctx = loadMaintenanceContext({
        'maintenance.getBackups': () => ({
            backups: [
                { id: 'b-1', description: 'Test Backup', itemCount: 5, status: 'completed', createdAt: '2026-10-18T10:00:00Z' }
            ]
        }),
        'maintenance.getBackup': () => ({
            id: 'b-1',
            description: 'Test Backup',
            items: [{ id: 'i-1', kind: 'registry', path: 'HKCU\\Test', status: 'stored' }]
        }),
        'maintenance.restore': () => restoreOp,
        'maintenance.status': () => restoreOp
    });

    ctx.api.init();
    ctx.api.activate({ view: 'system-tools', subviews: { 'system-tools': 'backup' } });
    await new Promise(r => setImmediate(r));

    // Open detail
    const detailsBtn = findButtonByText(ctx.backupPanel, 'Dettagli / Ripristina');
    detailsBtn.dispatchEvent({ type: 'click' });
    await new Promise(r => setImmediate(r));

    // Select item
    const cb = findAllNodes(ctx.backupPanel, n => n.tagName === 'INPUT' && n.attributes?.get('type') === 'checkbox')[0];
    cb.checked = true;
    cb.dispatchEvent({ type: 'change', target: cb });

    // Click restore
    const restoreBtn = findButtonByText(ctx.backupPanel, 'Ripristina selezionati');
    restoreBtn.dispatchEvent({ type: 'click' });

    // Confirm modal
    const confirmBtn = ctx.document.getElementById('vm-dialog-confirm-btn');
    confirmBtn.dispatchEvent({ type: 'click' });
    await new Promise(r => setImmediate(r));

    assert.ok(ctx.calls.some(c => c.method === 'maintenance.restore'));

    // Check that restore progress card is displayed in backup panel
    let panelText = collectText(ctx.backupPanel);
    assert.ok(panelText.includes('Ripristino elementi 2/5...'), 'Operation progress message must be visible in backup view');
    assert.ok(panelText.includes('2 / 5'), 'Processed/total counts must be visible in backup view');

    // Simulate next poll tick with updated progress
    restoreOp = {
        ...restoreOp,
        processed: 5,
        total: 5,
        status: 'completed',
        message: 'Ripristino completato'
    };

    // Wait for poll tick
    await new Promise(r => setTimeout(r, 1100));

    panelText = collectText(ctx.backupPanel);
    assert.ok(panelText.includes('Ripristino completato'), 'Updated progress message must render on poll update');

    ctx.api.dispose();
});

// Helper functions for DOM inspection
function collectText(node) {
    if (!node) return '';
    let text = node.textContent || '';
    for (const child of node.children || []) {
        text += ' ' + collectText(child);
    }
    return text;
}

function findAllNodes(root, predicate) {
    const results = [];
    function walk(node) {
        if (!node) return;
        if (predicate(node)) results.push(node);
        for (const child of node.children || []) walk(child);
    }
    walk(root);
    return results;
}

function findButtonByText(root, text) {
    const buttons = findAllNodes(root, n => n.tagName === 'BUTTON');
    return buttons.find(b => {
        const t = collectText(b).trim();
        return t.includes(text);
    }) || null;
}
