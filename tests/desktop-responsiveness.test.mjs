import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';

const resolveFile = (rel) => fileURLToPath(new URL(rel, import.meta.url));
const read = (rel) => readFileSync(resolveFile(rel), 'utf8');
const runFile = (rel, ctx) => vm.runInContext(read(rel), ctx, { filename: resolveFile(rel) });

class FakeClassList {
  constructor(initial = []) { this.set = new Set(initial); }
  add(...names) { names.forEach(n => this.set.add(n)); }
  remove(...names) { names.forEach(n => this.set.delete(n)); }
  contains(name) { return this.set.has(name); }
  toggle(name, force) {
    const on = force === undefined ? !this.set.has(name) : !!force;
    if (on) this.set.add(name); else this.set.delete(name);
    return on;
  }
}

function makeEl(tag, { id = '', classes = [], dataset = {}, rect = { left: 0, top: 0, width: 120, height: 40 } } = {}) {
  const attrs = new Map();
  const listeners = new Map();
  const el = {
    tagName: tag.toUpperCase(),
    id,
    classList: new FakeClassList(classes),
    dataset: { ...dataset },
    style: {
      props: {},
      setProperty(k, v) { this.props[k] = v; },
      getPropertyValue(k) { return this.props[k] || ''; },
    },
    children: [],
    textContent: '',
    scrollTop: 120,
    setAttribute(k, v) { attrs.set(k, String(v)); },
    getAttribute(k) { return attrs.has(k) ? attrs.get(k) : null; },
    appendChild(child) { this.children.push(child); return child; },
    insertBefore(child) { this.children.unshift(child); return child; },
    remove() { this.removed = true; },
    querySelector() { return null; },
    querySelectorAll() { return []; },
    closest(sel) {
      if (sel.split(',').some(s => this.classList.contains(s.trim().replace(/^\./, '')))) return this;
      return null;
    },
    getBoundingClientRect() { return rect; },
    addEventListener(type, fn) { (listeners.get(type) || listeners.set(type, []).get(type)).push(fn); },
    removeEventListener(type, fn) {
      const list = listeners.get(type);
      if (list) listeners.set(type, list.filter(f => f !== fn));
    },
    dispatch(type, ev = {}) { (listeners.get(type) || []).forEach(fn => fn({ currentTarget: this, ...ev })); },
  };
  return el;
}

test('effects.js throttles spotlight rAF, clears hover classes on scroll, and caches ripple styles', () => {
  const docListeners = new Map();
  const rafQueue = [];
  const timers = [];
  let getComputedStyleCalls = 0;
  const docEl = {
    dataset: { perfTier: 'full', fx: 'rich', anim: 'high' },
    addEventListener() {},
    removeEventListener() {},
  };
  const card = makeEl('div', { classes: ['glass-card'], rect: { left: 0, top: 0, width: 200, height: 100 } });
  const navBtn = makeEl('a', { classes: ['nav-item'], rect: { left: 0, top: 0, width: 180, height: 40 } });

  const ctx = vm.createContext({
    window: {
      matchMedia: () => ({ matches: false, addEventListener() {} }),
      addEventListener() {},
      removeEventListener() {},
    },
    document: {
      readyState: 'complete',
      hidden: false,
      documentElement: docEl,
      querySelector: () => null,
      querySelectorAll: () => [],
      createElement: (tag) => makeEl(tag),
      addEventListener(type, fn) { (docListeners.get(type) || docListeners.set(type, []).get(type)).push(fn); },
      removeEventListener(type, fn) {
        const list = docListeners.get(type);
        if (list) docListeners.set(type, list.filter(f => f !== fn));
      },
    },
    getComputedStyle: () => {
      getComputedStyleCalls += 1;
      return { position: 'static', overflow: 'visible', getPropertyValue: () => '#3b82f6' };
    },
    requestAnimationFrame: (cb) => { rafQueue.push(cb); return rafQueue.length; },
    cancelAnimationFrame: () => {},
    setTimeout: (fn, delay) => { timers.push({ fn, delay }); return timers.length; },
    clearTimeout: () => {},
  });
  ctx.window.document = ctx.document;
  runFile('../src/VoltManager/wwwroot/js/effects.js', ctx);

  // Enter card -> snaps immediately and adds fx-spot + fx-sheen
  const move = docListeners.get('pointermove')[0];
  move({ target: card, clientX: 20, clientY: 50 });
  assert.equal(card.classList.contains('fx-spot'), true);
  assert.equal(card.classList.contains('fx-sheen'), true);
  assert.equal(card.style.props['--mx'], '10.0%');

  // High-refresh pointer moves (8ms apart) are throttled until >= 32ms
  move({ target: card, clientX: 180, clientY: 50 });
  const firstFrame = rafQueue.shift();
  firstFrame(100);
  const mxAfter100 = card.style.props['--mx'];
  const fastFrame = rafQueue.shift();
  fastFrame(108); // < 32ms since 100 -> must skip paint
  assert.equal(card.style.props['--mx'], mxAfter100);
  const nextFrame = rafQueue.shift();
  nextFrame(136); // >= 32ms -> advances toward target
  assert.notEqual(card.style.props['--mx'], mxAfter100);

  // Scroll immediately drops fx-spot and fx-sheen
  docListeners.get('wheel')[0]();
  assert.equal(timers.at(-1).delay, 220);
  assert.equal(docEl.dataset.scrolling, 'true');
  move({ target: card, clientX: 20, clientY: 50 });
  assert.equal(card.classList.contains('fx-sheen'), false);
  timers.at(-1).fn();
  assert.equal(docEl.dataset.scrolling, undefined);
  docListeners.get('scroll')[0]();
  assert.equal(card.classList.contains('fx-spot'), false);
  assert.equal(card.classList.contains('fx-sheen'), false);

  // Repeated ripples read getComputedStyle only once per element
  const ripple = docListeners.get('pointerdown')[0];
  ripple({ target: navBtn, clientX: 20, clientY: 20 });
  ripple({ target: navBtn, clientX: 40, clientY: 20 });
  assert.equal(getComputedStyleCalls, 1);
  assert.equal(navBtn.dataset.fxRippleReady, '1');
});

test('activateView positions sidebar indicator before toggling views, never blocks on advanced.js, and ignores repeat clicks', async () => {
  const order = [];
  let powerLoadCalls = 0;
  let resolvePowerLoad;
  const overview = makeEl('section', { classes: ['vm-reorg-view', 'flex'], dataset: { vmView: 'overview' } });
  const plans = makeEl('section', { classes: ['vm-reorg-view', 'hidden'], dataset: { vmView: 'power-plans' } });
  const origToggle = plans.classList.toggle.bind(plans.classList);
  plans.classList.toggle = (name, force) => {
    if (name === 'hidden') order.push('toggle-view');
    return origToggle(name, force);
  };
  const overviewLink = makeEl('a', { classes: ['nav-item'], dataset: { view: 'overview' } });
  const plansLink = makeEl('a', { classes: ['nav-item'], dataset: { view: 'power-plans' } });
  const legacyView = makeEl('section', { classes: ['vm-legacy-view', 'flex'] });
  const main = makeEl('main', { id: 'main-content' });
  const emitted = [];
  let lifecycleCalls = 0;

  const ctx = vm.createContext({
    window: {
      VoltUiReorg: {
        positionIndicator() { order.push('position-indicator'); },
      },
      VoltViewLifecycle: { transition() { lifecycleCalls += 1; } },
      __voltEnsurePowerScripts() {
        powerLoadCalls += 1;
        return new Promise(resolve => { resolvePowerLoad = resolve; });
      },
      addEventListener() {},
      removeEventListener() {},
      dispatchEvent() {},
    },
    document: {
      readyState: 'loading',
      documentElement: { dataset: {} },
      getElementById: (id) => (id === 'main-content' ? main : null),
      querySelector: (sel) => {
        if (sel === '.vm-reorg-view[data-vm-view="power-plans"]') return plans;
        if (sel === '.vm-reorg-view[data-vm-view="overview"]') return overview;
        return null;
      },
      querySelectorAll: (sel) => {
        if (sel === '.vm-reorg-view') return [overview, plans];
        if (sel === '.vm-legacy-view') return [legacyView];
        if (sel === '#nav-list .nav-item[data-view]') return [overviewLink, plansLink];
        return [];
      },
      addEventListener() {},
      dispatchEvent(ev) { emitted.push(ev); },
    },
    CustomEvent: class { constructor(type, init = {}) { this.type = type; this.detail = init.detail; } },
    history: { replaceState() {} },
    location: { hash: '' },
    setTimeout: () => 1,
    clearTimeout: () => {},
  });
  ctx.window.document = ctx.document;
  runFile('../src/VoltManager/wwwroot/js/ui-reorganization.js', ctx);

  ctx.window.VoltUiReorg.activateView('power-plans', false);
  // Indicator positioned BEFORE view visibility toggle, and switch completed synchronously
  assert.deepEqual(order, ['position-indicator', 'toggle-view']);
  assert.equal(plans.classList.contains('hidden'), false);
  assert.equal(plans.classList.contains('vm-view-switching'), true);
  assert.equal(legacyView.classList.contains('hidden'), true);
  assert.equal(powerLoadCalls, 1);
  assert.equal(lifecycleCalls, 1);

  // Clicking the already-active section is a no-op (still updates hash when requested)
  const eventsBefore = emitted.length;
  ctx.window.VoltUiReorg.activateView('power-plans', true);
  assert.equal(emitted.length, eventsBefore);
  assert.equal(powerLoadCalls, 1);

  // Subview activation updates state and emits voltuisubviewchanged
  ctx.window.VoltUiReorg.activateSubview('power-plans', 'advanced');
  assert.equal(plans.classList.contains('vm-view-switching'), false);
  assert.equal(ctx.window.VoltUiReorg.state.subviews['power-plans'], 'advanced');
  assert.equal(lifecycleCalls, 2);

  // When deferred advanced.js finishes loading, lifecycle syncs once
  resolvePowerLoad();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(lifecycleCalls, 3);
});

test('ui-reorganization.status skips overview DOM writes off-overview and uses scaleX meters without getComputedStyle', () => {
  let getComputedStyleCalls = 0;
  const nodes = new Map();
  for (const id of [
    'cpu-pct', 'gpu-pct', 'ram-pct', 'disk-pct',
    'ov-metric-cpu', 'ov-meter-cpu', 'ov-metric-gpu', 'ov-meter-gpu',
    'ov-metric-ram', 'ov-meter-ram', 'ov-metric-disk', 'ov-meter-disk',
    'vm-top-plan', 'vm-top-automation', 'manual-override-chip', 'master-toggle',
  ]) {
    nodes.set(id, makeEl('div', { id }));
  }
  nodes.get('cpu-pct').textContent = '45%';
  nodes.get('manual-override-chip').classList.add('hidden');
  nodes.get('master-toggle').checked = true;

  const hostSubs = new Map();
  const api = { ready: true, state: { view: 'overview' }, t: k => k, activateView() {}, activateSubview() {} };

  const ctx = vm.createContext({
    window: {
      VoltUiReorg: api,
      Host: { available: false, on: (name, fn) => hostSubs.set(name, fn) },
    },
    Host: { available: false, on: (name, fn) => hostSubs.set(name, fn) },
    document: {
      hidden: false,
      body: { id: 'body' },
      getElementById: id => nodes.get(id) || null,
      querySelector: () => null,
      querySelectorAll: () => [],
      addEventListener() {},
    },
    MutationObserver: class { observe() {} disconnect() {} },
    requestAnimationFrame: cb => cb(),
    setInterval: () => 1,
    clearInterval: () => {},
    getComputedStyle: () => { getComputedStyleCalls += 1; return { display: 'block' }; },
  });
  runFile('../src/VoltManager/wwwroot/js/ui-reorganization.status.js', ctx);

  assert.equal(nodes.get('ov-meter-cpu').style.transform, 'scaleX(0.4500)');
  assert.equal(nodes.get('ov-meter-cpu').style.width, undefined);
  assert.equal(getComputedStyleCalls, 0);

  // Switching away from overview skips hidden overview metric writes on metrics ticks
  api.state.view = 'automations';
  nodes.get('cpu-pct').textContent = '88%';
  hostSubs.get('metrics')({ cpu: 88 });
  assert.equal(nodes.get('ov-metric-cpu').textContent, '45%');
  assert.equal(nodes.get('ov-meter-cpu').style.transform, 'scaleX(0.4500)');

  api.state.view = 'settings';
  hostSubs.get('metrics')({ cpu: 90 });
  assert.equal(nodes.get('ov-metric-cpu').textContent, '45%');
});

test('energy-suggestions avoids redundant viewchange and desktop metrics work after mounting', () => {
  const docListeners = new Map();
  const hostSubs = new Map();
  const overview = makeEl('section', { id: 'view-overview' });
  const grid = makeEl('div', { classes: ['vm-overview-grid'] });
  const btn = makeEl('button', { id: 'btn-energy-tips' });
  const badge = makeEl('span', { id: 'vm-tips-badge' });
  let selectorQueries = 0;

  const ctx = vm.createContext({
    window: {
      Host: {
        available: false,
        on: (name, fn) => hostSubs.set(name, fn),
      },
      I18n: { t: k => k, getLang: () => 'en' },
    },
    document: {
      readyState: 'complete',
      hidden: false,
      documentElement: { dataset: { vmHasBattery: 'false' } },
      getElementById: (id) => {
        if (id === 'view-overview') return overview;
        if (id === 'btn-energy-tips') return btn;
        if (id === 'vm-tips-badge') return badge;
        return null;
      },
      querySelector: (sel) => {
        selectorQueries += 1;
        return sel === '#view-overview .vm-overview-grid' ? grid : null;
      },
      querySelectorAll: () => [],
      createElement: (tag) => makeEl(tag),
      addEventListener(type, fn) { (docListeners.get(type) || docListeners.set(type, []).get(type)).push(fn); },
    },
    console,
    Date,
    setTimeout: () => 1,
    clearTimeout: () => {},
  });
  ctx.Host = ctx.window.Host;
  ctx.I18n = ctx.window.I18n;
  ctx.window.document = ctx.document;
  runFile('../src/VoltManager/wwwroot/js/energy-suggestions.js', ctx);

  // Already mounted after boot -> subsequent viewchange/voltuiviewchanged do not re-query or re-mount
  docListeners.get('voltuiready')[0]();
  const queriesAfterMount = selectorQueries;
  docListeners.get('viewchange')[0]();
  docListeners.get('voltuiviewchanged')[0]();
  assert.equal(selectorQueries, queriesAfterMount);

  // Desktop metrics tick with vmHasBattery === 'false' skips render
  let badgeWrites = 0;
  const origDesc = Object.getOwnPropertyDescriptor(badge, 'textContent');
  Object.defineProperty(badge, 'textContent', {
    get() { return origDesc ? origDesc.value : ''; },
    set(v) { badgeWrites += 1; if (origDesc) origDesc.value = v; },
    configurable: true,
  });
  hostSubs.get('metrics')({ cpu: 15 });
  assert.equal(badgeWrites, 0);
});

test('app.js only pulls deferred advanced.js for power/system views and marks loaded flag', async () => {
  const appPath = resolveFile('../src/VoltManager/wwwroot/js/app.js');
  const appSrc = readFileSync(appPath, 'utf8');
  const start = appSrc.indexOf('    const deferredPowerScripts');
  const end = appSrc.indexOf('    let viewTransitionEpoch');
  const prefixLines = appSrc.slice(0, start).split('\n').length - 1;
  const scripts = [];
  const docListeners = new Map();
  const ctx = vm.createContext({
    window: { VM_ASSET_URL: p => p },
    document: {
      createElement: () => ({ dataset: {}, remove() {} }),
      body: { appendChild: s => scripts.push(s) },
      addEventListener(type, fn) { (docListeners.get(type) || docListeners.set(type, []).get(type)).push(fn); },
    },
  });
  vm.runInContext('\n'.repeat(prefixLines) + appSrc.slice(start, end), ctx, { filename: appPath });
  assert.equal(ctx.needsPowerScripts('settings'), false);
  assert.equal(ctx.needsPowerScripts('automations'), false);
  assert.equal(ctx.needsPowerScripts('power-plans'), true);
  assert.equal(ctx.needsPowerScripts('system-tools'), true);
  const p = ctx.window.__voltEnsurePowerScripts();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(scripts.length, 1);
  scripts[0].onload();
  await p;
  assert.equal(ctx.window.__voltPowerScriptsLoaded, true);
});

test('CSS responsiveness rules keep main-content contained, disable scroll hover churn, and avoid ripple blend layers', () => {
  const effectsCss = read('../src/VoltManager/wwwroot/css/effects.css');
  const polishCss = read('../src/VoltManager/wwwroot/css/polish.css');
  const reorgCss = read('../src/VoltManager/wwwroot/css/ui-reorganization.css');

  assert.match(polishCss, /#main-content\s*\{[^}]*contain:\s*layout\s+style;/);
  assert.doesNotMatch(effectsCss, /\.vm-ripple\s*\{[^}]*mix-blend-mode/);
  assert.match(effectsCss, /html\[data-scrolling="true"\]\s+\.glass-card::before\s*\{\s*animation:\s*none\s*!important;\s*\}/);
  assert.match(effectsCss, /html\[data-scrolling="true"\]\s+\.glass-card:not\(\.fx-metric\):hover/);
  assert.match(reorgCss, /\.vm-meter\s*>\s*span\s*\{[^}]*transform:\s*scaleX\(0\)/);
  assert.doesNotMatch(effectsCss, /\.vm-(?:reorg-view|subview)\.hidden/);
  assert.match(effectsCss, /#main-content \*\s*\{\s*backdrop-filter:\s*none\s*!important/);
  assert.match(reorgCss, /\.vm-reorg-view\.vm-view-switching \.vm-subview\.active > \*\s*\{\s*animation-duration:\s*0s/);
  assert.doesNotMatch(read('../src/VoltManager/wwwroot/index.html'), /backdrop-blur-md/);
});

test('startup waits for DOMContentLoaded instead of window load', () => {
  for (const file of ['bootstrap-loader.js', 'ui-reorganization.js']) {
    const source = read('../src/VoltManager/wwwroot/js/' + file);
    assert.match(source, /document\.readyState !== 'loading'/);
    assert.match(source, /document\.addEventListener\('DOMContentLoaded'/);
    assert.match(source, /typeof document\.addEventListener === 'function'/);
  }
});

test('metrics query the active view once and limit monitoring rendering to hardware panels', () => {
  const source = read('../src/VoltManager/wwwroot/js/dashboard.js');
  const metrics = source.slice(source.indexOf("Host.on('metrics', (m) => {"), source.indexOf('// ----- Top Processes -----'));
  assert.equal((metrics.match(/querySelector\('\.vm-reorg-view:not\(\.hidden\)'\)/g) || []).length, 1);
  assert.match(metrics, /data-vm-panel="hardware"/);
  assert.match(metrics, /data-vm-panel="temperatures"/);
  assert.match(metrics, /if \(full\) \{[\s\S]*renderSensors\(m\)/);
  assert.match(metrics, /setText\(cpuPct, Math\.round\(m\.cpu\)/);
});
