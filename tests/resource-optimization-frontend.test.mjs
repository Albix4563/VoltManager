import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';

const resolvePath = (rel) => fileURLToPath(new URL(rel, import.meta.url));
const readText = (rel) => readFileSync(new URL(rel, import.meta.url), 'utf8');
const readBytes = (rel) => readFileSync(new URL(rel, import.meta.url));

const effectsJsPath = resolvePath('../src/VoltManager/wwwroot/js/effects.js');
const perfGuardJsPath = resolvePath('../src/VoltManager/wwwroot/js/perf-guard.js');
const statusJsPath = resolvePath('../src/VoltManager/wwwroot/js/ui-reorganization.status.js');
const i18nJsPath = resolvePath('../src/VoltManager/wwwroot/js/i18n.js');

const effectsCss = readText('../src/VoltManager/wwwroot/css/effects.css');
const redesignCss = readText('../src/VoltManager/wwwroot/css/redesign.css');
const themeColorsCss = readText('../src/VoltManager/wwwroot/css/theme-colors.css');
const indexHtml = readText('../src/VoltManager/wwwroot/index.html');
const effectsJs = readText('../src/VoltManager/wwwroot/js/effects.js');
const perfGuardJs = readText('../src/VoltManager/wwwroot/js/perf-guard.js');
const statusJs = readText('../src/VoltManager/wwwroot/js/ui-reorganization.status.js');
const appJs = readText('../src/VoltManager/wwwroot/js/app.js');
const i18nCatalogsJs = readText('../src/VoltManager/wwwroot/js/i18n.catalogs.js');
const i18nJs = readText('../src/VoltManager/wwwroot/js/i18n.js');

test('GPU-free aurora, static decorative elements, blur-free view transitions and background CSS pause', () => {
  const orbBlock = effectsCss.match(/\.vm-aurora__orb\s*\{[^}]*\}/)?.[0] || '';
  assert.ok(orbBlock);
  assert.doesNotMatch(orbBlock, /filter:\s*blur/);
  assert.match(effectsCss, /\.vm-aurora__orb--1\s*\{[^}]*min\(34vw,\s*420px\)/);
  assert.match(effectsCss, /\.vm-aurora__orb--2\s*\{[^}]*min\(30vw,\s*420px\)/);
  assert.doesNotMatch(effectsCss, /@keyframes\s+(?:vmOrb1|vmOrb2|vmTitleSheen|vmPing)\b/);
  assert.doesNotMatch(redesignCss, /filter:\s*blur\(/);

  // Backdrop-filter removed from nav#side-nav and gated on overlays/popovers
  const sideNavTag = indexHtml.match(/<nav\s+id="side-nav"[^>]*>/)?.[0] || '';
  assert.ok(sideNavTag);
  assert.doesNotMatch(sideNavTag, /backdrop-blur-/);
  assert.match(sideNavTag, /\bbg-surface-container\b/);

  for (const ctxPattern of [
    /@media\s*\(prefers-reduced-transparency:\s*reduce\)[\s\S]*?\.vm-search-overlay[\s\S]*?backdrop-filter:\s*none\s*!important/,
    /html\[data-perf="lite"\]\s+\.theme-chooser-overlay[\s\S]*?\.vm-tour-pop[\s\S]*?backdrop-filter:\s*none\s*!important/,
    /html\[data-perf-tier="lite"\]\s+\.theme-chooser-overlay[\s\S]*?\.vm-tour-pop[\s\S]*?backdrop-filter:\s*none\s*!important/,
    /html\[data-perf-tier="balanced"\]\s+\.theme-chooser-overlay[\s\S]*?\.vm-tour-pop[\s\S]*?backdrop-filter:\s*none\s*!important/,
  ]) {
    assert.match(effectsCss, ctxPattern);
  }

  // Ring drop-shadow disabled
  assert.match(effectsCss, /#cpu-ring,\s*#gpu-ring\s*\{[^}]*filter:\s*none\s*!important/);
  assert.doesNotMatch(effectsCss, /#cpu-ring,\s*#gpu-ring\s*\{[^}]*filter\s+\.8s/);
  assert.match(themeColorsCss, /#cpu-ring,\s*\n#gpu-ring\s*\{[^}]*filter:\s*none\s*!important/);

  // Background hidden rules
  assert.match(effectsCss, /html\[data-hidden="true"\]\s*\*[\s\S]*animation-play-state:\s*paused\s*!important[\s\S]*transition:\s*none\s*!important/);
  assert.match(effectsCss, /html\[data-hidden="true"\]\s+\.vm-aurora\s*\{[^}]*display:\s*none\s*!important/);
});

test('effects.js throttles rAF to ~30fps, skips .hidden views, removes ring drop-shadow, and stops on document.hidden', () => {
  let rafQueue = [];
  let nextRafId = 1;
  const cancelledIds = [];
  const docListeners = new Map();
  const addDocListener = (t, fn) => {
    const list = docListeners.get(t) || [];
    list.push(fn);
    docListeners.set(t, list);
  };
  const removeDocListener = (t, fn) => {
    const list = docListeners.get(t);
    if (!list) return;
    const idx = list.indexOf(fn);
    if (idx >= 0) list.splice(idx, 1);
  };
  const fireDoc = (t, ev) => {
    for (const fn of [...(docListeners.get(t) || [])]) fn(ev);
  };

  const documentElement = { dataset: { perfTier: 'full', fx: 'rich' }, addEventListener() {}, removeEventListener() {} };
  const document = {
    readyState: 'complete',
    hidden: false,
    documentElement,
    querySelector: () => null,
    querySelectorAll: () => [],
    addEventListener: addDocListener,
    removeEventListener: removeDocListener,
  };
  const window = {
    matchMedia: () => ({ matches: false, addEventListener() {} }),
    addEventListener() {},
    removeEventListener() {},
    getComputedStyle: () => ({ getPropertyValue: () => '#3b82f6' }),
  };

  vm.runInNewContext(effectsJs, {
    window,
    document,
    getComputedStyle: window.getComputedStyle,
    requestAnimationFrame: (cb) => { const id = nextRafId++; rafQueue.push({ id, cb }); return id; },
    cancelAnimationFrame: (id) => { cancelledIds.push(id); rafQueue = rafQueue.filter((item) => item.id !== id); },
    setTimeout,
    clearTimeout,
  }, { filename: effectsJsPath });

  const makeRing = (inHiddenView = false) => ({
    dataset: {},
    style: {},
    closest: (sel) => (sel === '.hidden' && inHiddenView ? {} : null),
  });
  const makeLabel = (inHiddenView = false) => ({
    textContent: '',
    closest: (sel) => (sel === '.hidden' && inHiddenView ? {} : null),
  });

  // Hidden view rings snap without scheduling rAF or setting filter/willChange
  const hiddenRing = makeRing(true);
  const hiddenLabel = makeLabel(true);
  window.VoltFx.animateRing(hiddenRing, hiddenLabel, 40);
  window.VoltFx.animateRing(hiddenRing, hiddenLabel, 80);
  assert.equal(rafQueue.length, 0);
  assert.equal(hiddenRing.style.willChange, 'auto');
  assert.equal(hiddenRing.style.filter, undefined);
  assert.equal(hiddenLabel.textContent, '80%');

  // Visible ring throttles rAF updates to >=33ms and sets no drop-shadow
  const visibleRing = makeRing(false);
  const visibleLabel = makeLabel(false);
  window.VoltFx.animateRing(visibleRing, visibleLabel, 10);
  window.VoltFx.animateRing(visibleRing, visibleLabel, 90);
  assert.equal(visibleRing.style.filter, undefined);
  assert.equal(rafQueue.length, 1);

  rafQueue.shift().cb(0);
  const afterFrame0 = visibleLabel.textContent;
  assert.notEqual(afterFrame0, '10%');
  assert.equal(rafQueue.length, 1);

  // Frame at 16ms (<33ms) is skipped and does not advance value
  rafQueue.shift().cb(16);
  assert.equal(visibleLabel.textContent, afterFrame0);
  assert.equal(rafQueue.length, 1);

  // Frame at 34ms (>=33ms) advances value
  rafQueue.shift().cb(34);
  assert.notEqual(visibleLabel.textContent, afterFrame0);
  assert.equal(rafQueue.length, 1);

  // Going hidden stops rAF immediately via visibilitychange
  document.hidden = true;
  fireDoc('visibilitychange');
  assert.equal(rafQueue.length, 0);
  assert.ok(cancelledIds.length >= 1);

  // Additional paths: animateBar, replayIntro, mid-flight hidden view, hidden frame, hidden flushSpot
  document.hidden = false;
  fireDoc('visibilitychange');
  documentElement.dataset.anim = 'high';
  let barHidden = false;
  const bar = {
    dataset: {},
    style: {},
    closest: (sel) => (sel === '.hidden' && barHidden ? {} : null),
  };
  const hiddenBar = {
    dataset: {},
    style: {},
    closest: (sel) => (sel === '.hidden' ? {} : null),
  };
  window.VoltFx.animateBar(hiddenBar, 60);
  assert.equal(hiddenBar.style.willChange, 'auto');

  window.VoltFx.animateBar(bar, 10);
  window.VoltFx.animateBar(bar, 85);
  assert.equal(rafQueue.length, 1);
  // Element moves into a .hidden view before next frame: snaps to target on frame tick
  barHidden = true;
  rafQueue.shift().cb(100);
  assert.equal(bar.style.transform, 'scaleX(0.8500)');

  // replayIntro replays visible channels and skips hidden ones
  barHidden = false;
  const root = { contains: (el) => el === bar || el === hiddenBar };
  window.VoltFx.replayIntro(root);
  assert.equal(rafQueue.length, 1);

  // If document becomes hidden before rAF fires, frame() exits cleanly
  document.hidden = true;
  rafQueue.shift().cb(200);
  assert.equal(rafQueue.length, 0);

  // Pointer spotlight skips flushSpot when document is hidden
  document.hidden = false;
  fireDoc('visibilitychange');
  const card = {
    classList: { add() {}, remove() {}, contains: () => false },
    style: { setProperty() {} },
    addEventListener() {},
    removeEventListener() {},
    closest: (sel) => (sel === '.glass-card, .glass-panel' ? card : null),
    getBoundingClientRect: () => ({ left: 0, top: 0, width: 200, height: 100 }),
  };
  fireDoc('pointermove', { pointerType: 'mouse', clientX: 50, clientY: 50, target: card });
  fireDoc('pointermove', { pointerType: 'mouse', clientX: 140, clientY: 60, target: card });
  assert.equal(rafQueue.length, 1);
  document.hidden = true;
  rafQueue.shift().cb(300);
  assert.equal(rafQueue.length, 0);
});

test('perf-guard.js syncs data-hidden and calls VoltFx.stopMotion when hidden', () => {
  const docListeners = new Map();
  let stopCalls = 0;
  const document = {
    hidden: true,
    documentElement: { dataset: {} },
    addEventListener: (t, fn) => docListeners.set(t, fn),
    dispatchEvent() {},
  };
  const window = { VoltFx: { stopMotion: () => { stopCalls++; } } };

  vm.runInNewContext(perfGuardJs, { window, document, console, CustomEvent: class {} }, { filename: perfGuardJsPath });
  assert.equal(document.documentElement.dataset.hidden, 'true');
  assert.equal(stopCalls, 1);

  document.hidden = false;
  docListeners.get('visibilitychange')();
  assert.equal(document.documentElement.dataset.hidden, undefined);

  document.hidden = true;
  docListeners.get('visibilitychange')();
  assert.equal(document.documentElement.dataset.hidden, 'true');
  assert.equal(stopCalls, 2);
});

test('background intervals stop on document.hidden and restart when visible', () => {
  const docListeners = new Map();
  const cleared = [];
  let nextId = 10;
  const created = [];
  const context = {
    VoltUiReorg: { ready: true },
    document: {
      hidden: false,
      getElementById: () => null,
      addEventListener: (t, fn) => docListeners.set(t, fn),
    },
    MutationObserver: class { observe() {} },
    requestAnimationFrame: () => 1,
    setInterval: (fn, ms) => { const id = nextId++; created.push({ id, fn, ms }); return id; },
    clearInterval: (id) => { cleared.push(id); },
  };
  context.window = context;

  vm.runInNewContext(statusJs, context, { filename: statusJsPath });
  assert.equal(created.length, 1);
  assert.equal(created[0].ms, 5000);

  context.document.hidden = true;
  docListeners.get('visibilitychange')();
  assert.deepEqual(cleared, [10]);

  context.document.hidden = false;
  docListeners.get('visibilitychange')();
  assert.equal(created.length, 2);

  // app.js scheduleCountdownTimer pause/resume wiring
  assert.match(appJs, /function stopScheduleCountdownTimer\(\)[\s\S]*clearInterval\(scheduleCountdownTimer\)/);
  assert.match(appJs, /function startScheduleCountdownTimer\(\)[\s\S]*setInterval\(scheduleCountdownTick,\s*1000\)/);
  assert.match(appJs, /addEventListener\('visibilitychange'[\s\S]*stopScheduleCountdownTimer\(\)[\s\S]*startScheduleCountdownTimer\(\)/);
});

test('i18n.js prunes non-core namespaces in widget mode without affecting full app mode', () => {
  const runI18n = (hasWidgetRoot) => {
    const window = { addEventListener() {} };
    const document = {
      documentElement: { lang: '' },
      getElementById: (id) => (hasWidgetRoot && id === 'widget-root' ? {} : null),
      querySelectorAll: () => [],
      addEventListener() {},
      dispatchEvent() {},
    };
    const ctx = vm.createContext({
      window,
      document,
      localStorage: { getItem: () => 'en', setItem() {} },
      CustomEvent: class {},
      Intl,
      console,
    });
    vm.runInNewContext(i18nCatalogsJs, ctx);
    vm.runInNewContext(i18nJs, ctx, { filename: i18nJsPath });
    return {
      namespaces: Object.keys(ctx.window.VoltI18nCatalogs.namespaces),
      title: ctx.window.I18n.t('app_title'),
    };
  };

  const widgetRes = runI18n(true);
  assert.deepEqual(widgetRes.namespaces, ['core']);
  assert.ok(widgetRes.title && widgetRes.title !== 'app_title');

  const appRes = runI18n(false);
  assert.ok(appRes.namespaces.length > 1);
  assert.ok(appRes.namespaces.includes('power'));
});

test('PNG logos are downscaled to 96x96 RGBA and WOFF2 font is subsetted', () => {
  for (const name of ['project_logo.png', 'sponsor_logo.png']) {
    const buf = readBytes(`../src/VoltManager/wwwroot/images/${name}`);
    assert.equal(buf.subarray(0, 8).toString('hex'), '89504e470d0a1a0a', `${name} PNG header`);
    assert.equal(buf.subarray(12, 16).toString('ascii'), 'IHDR', `${name} IHDR chunk`);
    assert.equal(buf.readUInt32BE(16), 96, `${name} width`);
    assert.equal(buf.readUInt32BE(20), 96, `${name} height`);
    assert.equal(buf[25], 6, `${name} RGBA colorType`);
    assert.ok(buf.length < 15 * 1024, `${name} should be compact (<15KB), got ${buf.length}`);
  }

  const woff2 = readBytes('../src/VoltManager/wwwroot/vendor/fonts/material-symbols-outlined.woff2');
  assert.equal(woff2.subarray(0, 4).toString('ascii'), 'wOF2');
  assert.ok(woff2.length < 60 * 1024, `subsetted woff2 should be <60KB, got ${woff2.length}`);
});
