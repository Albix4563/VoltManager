import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const read = (path) => readFileSync(new URL(path, import.meta.url), 'utf8');

const motion = read('../src/VoltManager/wwwroot/css/motion.css');
const redesign = read('../src/VoltManager/wwwroot/css/redesign.css');
const polish = read('../src/VoltManager/wwwroot/css/polish.css');
const effects = read('../src/VoltManager/wwwroot/css/effects.css');
const reorg = read('../src/VoltManager/wwwroot/css/ui-reorganization.css');
const app = read('../src/VoltManager/wwwroot/js/app.js');
const power = read('../src/VoltManager/wwwroot/js/power.js');
const powerFeatures = read('../src/VoltManager/wwwroot/css/power-features.css');
const advanced = read('../src/VoltManager/wwwroot/js/advanced.js');
const tour = read('../src/VoltManager/wwwroot/js/tour.feature.js');
const settings = read('../src/VoltManager/wwwroot/js/settings.js');
const widgets = read('../src/VoltManager/wwwroot/css/widgets.css');
const indexHtml = read('../src/VoltManager/wwwroot/index.html');
const widgetsHtml = read('../src/VoltManager/wwwroot/widgets.html');

test('shared motion tokens are loaded by the app and widget surfaces', () => {
  for (const html of [indexHtml, widgetsHtml]) {
    assert.match(html, /css\/motion\.css/);
  }

  const tokens = {
    '--vm-motion-fast': '280ms',
    '--vm-motion-base': '420ms',
    '--vm-motion-sidebar': '520ms',
    '--vm-motion-slow': '600ms',
    '--vm-motion-enter': '560ms',
    '--vm-motion-exit': '360ms',
    '--vm-motion-stagger': '80ms',
    '--vm-ease-standard': 'cubic-bezier(.2,.8,.2,1)',
    '--vm-ease-emphasized': 'cubic-bezier(.16,1,.3,1)',
    '--vm-ease-exit': 'cubic-bezier(.4,0,.2,1)',
  };

  for (const [name, value] of Object.entries(tokens)) {
    assert.ok(motion.includes(`${name}: ${value}`), `${name} should be ${value}`);
  }
});

test('view changes use the relaxed motion tokens and restrained transforms', () => {
  assert.match(redesign, /animation:\s*vmLeave\s+var\(--vm-motion-exit\)\s+var\(--vm-ease-exit\)/);
  assert.match(redesign, /scale\(\.985\)\s+translateY\(-3px\)/);
  assert.match(redesign, /blur\(2px\)/);
  assert.match(redesign, /animation:\s*vmEnter\s+var\(--vm-motion-enter\)\s+var\(--vm-ease-emphasized\)/);
  assert.match(redesign, /scale\(1\.008\)/);
  assert.match(redesign, /animation:\s*vmRise\s+var\(--vm-motion-slow\)\s+var\(--vm-ease-emphasized\)/);
  assert.match(redesign, /animation-delay:\s*calc\(var\(--vm-i,\s*0\)\s*\*\s*var\(--vm-motion-stagger\)\)/);
  assert.match(redesign, /translateY\(10px\)/);
});

test('view swapping waits for vmLeave animationend with a safety fallback', () => {
  assert.doesNotMatch(app, /setTimeout\([\s\S]{0,180}?250\)/);
  assert.match(app, /addEventListener\(['"]animationend['"]/);
  assert.match(app, /event\.target\s*!==\s*current/);
  assert.match(app, /event\.animationName\s*!==\s*['"]vmLeave['"]/);
  assert.match(app, /removeEventListener\(['"]animationend['"]/);
  assert.match(app, /setTimeout\([\s\S]{0,220}?vm-motion-exit|setTimeout\([\s\S]{0,220}?450/);
});

test('structural motion is relaxed while click feedback remains faster', () => {
  assert.match(redesign, /transition:\s*width\s+var\(--vm-motion-sidebar\)\s+var\(--vm-ease-emphasized\)/);
  assert.match(redesign, /transition:\s*margin-left\s+var\(--vm-motion-sidebar\)\s+var\(--vm-ease-emphasized\)/);
  assert.match(powerFeatures, /grid-template-rows\s+var\(--vm-motion-base\)/);
  assert.match(powerFeatures, /padding\s+var\(--vm-motion-base\)/);
  assert.match(app, /transform\s+480ms/);
  assert.match(reorg, /animation:\s*vmReorgViewIn\s+600ms/);
  assert.match(reorg, /animation:\s*vmReorgPanelIn\s+560ms/);
  assert.match(reorg, /animation-delay:\s*80ms/);
});

test('decorative motion is slower and reduced-motion/performance tiers remain intact', () => {
  assert.match(effects, /vmSheen\s+1\.25s/);
  assert.match(effects, /vmRipple\s+\.8s/);
  assert.doesNotMatch(effects, /vmNavPulse/);
  assert.match(effects, /\.nav-indicator\s*\{[\s\S]*animation:\s*none/s);
  assert.match(effects, /vmPing\s+2\.6s/);
  assert.match(effects, /vmTitleSheen\s+10s/);
  assert.match(effects, /vmOrb1\s+36s/);
  assert.match(effects, /vmOrb2\s+44s/);
  assert.match(effects, /prefers-reduced-motion:\s*reduce/);
  assert.match(effects, /data-perf-tier="balanced"/);
  assert.match(effects, /data-perf-tier="lite"/);
});

test('welcome-adjacent toast and tour motion use relaxed timings', () => {
  assert.match(settings, /slideInRight\s+0?\.45s/);
  assert.match(tour, /\.vm-tour-hole[\s\S]*top\s+\.42s[\s\S]*left\s+\.42s/);
  assert.match(tour, /\.vm-tour-pop[\s\S]*opacity\s+\.42s[\s\S]*transform\s+\.42s/);
  assert.match(tour, /TOUR_TRANSITION_SETTLE_MS\s*=\s*520/);
  assert.match(tour, /setTimeout\(\(\) => measureAndPlace\(s\),\s*TOUR_TRANSITION_SETTLE_MS\)/);
});

test('reduced motion disables the newly relaxed onboarding and advanced motion', () => {
  assert.match(tour, /#vm-tour-root\[data-reduce="true"\][\s\S]*transition:none/);
  assert.match(motion, /prefers-reduced-motion:\s*reduce[\s\S]*\.welcome-step[\s\S]*animation:\s*none\s*!important/);
  assert.match(motion, /prefers-reduced-motion:\s*reduce[\s\S]*\.adv-panel\s*>\s*\*[\s\S]*animation:\s*none\s*!important/);
  assert.match(motion, /prefers-reduced-motion:\s*reduce[\s\S]*\.vm-acc-body[\s\S]*transition:\s*none\s*!important/);
  assert.match(motion, /prefers-reduced-motion:\s*reduce[\s\S]*\.startup-switch__knob[\s\S]*transition:\s*none\s*!important/);
  assert.match(motion, /prefers-reduced-motion:\s*reduce[\s\S]*\.startup-card[\s\S]*animation:\s*none\s*!important/);
  assert.match(motion, /prefers-reduced-motion:\s*reduce[\s\S]*\.heavy-app-badge[\s\S]*animation:\s*none\s*!important/);
});

test('remaining micro interactions use shared relaxed timing instead of legacy 150-250ms values', () => {
  assert.doesNotMatch(reorg, /transition:[^;\n]*(?:\.18s|\.22s)/);
  assert.doesNotMatch(advanced, /transition:[^;\n]*(?:\.15s|\.2s|\.22s|\.25s)/);
  assert.match(advanced, /\.adv-panel>\*\{animation:advSlideIn var\(--vm-motion-enter\) var\(--vm-ease-emphasized\) both;\}/);
  assert.match(app, /\.startup-card__accent[^\n]*var\(--vm-motion-fast\)/);
  assert.match(app, /\.startup-remove-btn[^\n]*var\(--vm-motion-fast\)/);
  assert.match(powerFeatures, /\.app-profile-icon-btn[^\n]*var\(--vm-motion-fast\)/);
  assert.match(widgets, /\.widget-bar span[\s\S]*transition:\s*width var\(--vm-motion-fast\) var\(--vm-ease-standard\)/);
});

test('sidebar transition uses explicit expanding and collapsing phases without timers', () => {
  assert.match(app, /sidebar-expanding/);
  assert.match(app, /sidebar-collapsing/);
  assert.match(app, /transitionend/);
  assert.match(app, /style\.willChange\s*=\s*['"]width['"]/);
  assert.match(app, /style\.willChange\s*=\s*['"]['"]/);
  const sidebarBlock = app.match(/\(function wireSidebarCollapse\(\)[\s\S]*?\}\)\(\);/s)?.[0] || '';
  assert.doesNotMatch(sidebarBlock, /setTimeout|setInterval/);
});

test('navigation and card polish use compositor-friendly restrained feedback', () => {
  assert.match(polish, /\.nav-item\s*\{[\s\S]*transition:\s*transform[^;]*background-color[^;]*color[^;]*border-color/s);
  assert.match(polish, /\.nav-item:hover\s*\{[^}]*translateX\(2px\)/s);
  assert.match(polish, /\.nav-item\.text-secondary-container\s*\{[^}]*border-color/s);
  assert.match(polish, /\.glass-card[^\{]*\{[^}]*linear-gradient/s);
  assert.match(polish, /\.glass-card:not\(\.fx-metric\):hover\s*\{[^}]*translateY\(-1px\)/s);
  assert.match(polish, /\.btn-primary:active[\s\S]*scale\(\.98\)/s);
  assert.match(reorg, /\.vm-divider\s*\{[^}]*linear-gradient/s);
});

test('card spotlight dissolves at card edges instead of clipping flat', () => {
  const spot = effects.match(/\.glass-card::after, \.glass-panel::after\{[\s\S]*?\n\}/)?.[0] || '';
  assert.ok(spot, 'spotlight ::after rule must exist');
  assert.match(spot, /radial-gradient\(130px circle at var\(--mx,50%\)/);
  assert.match(spot, /radial-gradient\(280px circle at var\(--mx,50%\)/);
  assert.match(spot, /--vm-accent-rgb/);
  assert.match(spot, /transition:\s*opacity\s+var\(--vm-motion-slow/);
  assert.match(spot, /mask-image:[\s\S]*to right[\s\S]*to bottom/);
  assert.match(spot, /mask-composite:\s*intersect/);
  assert.match(effects, /\.glass-card\.fx-spot::after,\s*\.glass-panel\.fx-spot::after\s*\{[\s\S]*transition:\s*opacity\s+var\(--vm-motion-enter/);
});

test('cursor halo uses damped micro-glide, initial snap and pointerleave cleanup', () => {
  const effectsJs = read('../src/VoltManager/wwwroot/js/effects.js');
  assert.match(effectsJs, /SPOT_HALF_LIFE\s*=\s*28/);
  assert.match(effectsJs, /SPOT_EPS\s*=\s*0\.08/);
  assert.match(effectsJs, /flushSpot\(0\);\s*\n\s*spotEl\.classList\.add\('fx-spot'\)/);
  assert.match(effectsJs, /addEventListener\('pointerleave',\s*clearSpot/);
  assert.match(effectsJs, /removeEventListener\('pointerleave',\s*clearSpot/);

  const docListeners = new Map();
  const rootListeners = new Map();
  let rafQueue = [];
  let nextRafId = 1;
  const classes = new Set(['glass-card']);
  const styles = new Map();
  const card = {
    classList: {
      add: (c) => classes.add(c),
      remove: (c) => classes.delete(c),
      contains: (c) => classes.has(c),
    },
    style: { setProperty: (k, v) => styles.set(k, v) },
    getBoundingClientRect: () => ({ left: 0, top: 0, width: 200, height: 100 }),
    addEventListener() {},
    removeEventListener() {},
  };
  const documentElement = {
    dataset: { perfTier: 'full', fx: 'rich' },
    addEventListener: (type, fn) => rootListeners.set(type, fn),
    removeEventListener: (type) => rootListeners.delete(type),
  };
  const document = {
    readyState: 'complete',
    hidden: false,
    documentElement,
    querySelector: () => null,
    querySelectorAll: () => [],
    addEventListener: (type, fn) => docListeners.set(type, fn),
    removeEventListener: (type) => docListeners.delete(type),
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
    cancelAnimationFrame: (id) => { rafQueue = rafQueue.filter((item) => item.id !== id); },
    setTimeout,
    clearTimeout,
  });

  const onPointerMove = docListeners.get('pointermove');
  assert.ok(onPointerMove, 'pointermove listener should be mounted in rich mode');

  // First entry snaps immediately to cursor coordinates before fade-in
  onPointerMove({ clientX: 50, clientY: 25, target: { closest: () => card } });
  assert.equal(styles.get('--mx'), '25.0%');
  assert.equal(styles.get('--my'), '25.0%');
  assert.equal(classes.has('fx-spot'), true);
  assert.equal(rafQueue.length, 0, 'settled entry snap should not queue extra frames');

  // Subsequent move smoothly glides toward target and parks once settled
  onPointerMove({ clientX: 150, clientY: 75, target: { closest: () => card } });
  assert.equal(rafQueue.length, 1);
  let ts = 16;
  let ticks = 0;
  while (rafQueue.length > 0 && ticks < 30) {
    const next = rafQueue.shift();
    next.cb(ts);
    ts += 16;
    ticks++;
  }
  assert.ok(ticks > 1, 'micro-glide should ease across multiple frames');
  assert.equal(rafQueue.length, 0, 'loop must park once settled at target');
  assert.equal(styles.get('--mx'), '75.0%');
  assert.equal(styles.get('--my'), '75.0%');

  // Leaving window clears active spotlight
  rootListeners.get('pointerleave')();
  assert.equal(classes.has('fx-spot'), false);
});

test('slower decorative loops keep the ambient glow restrained', () => {
  assert.match(effects, /\.vm-aurora__orb\s*\{[\s\S]*opacity:\s*\.34/s);
  assert.match(effects, /\.nav-indicator\s*\{[\s\S]*0 0 18px[^;]*\/ \.28\)/s);
});

test('scroll containers stabilize layout gutter and suppress spotlight churn while scrolling', () => {
  const effectsJs = read('../src/VoltManager/wwwroot/js/effects.js');
  assert.match(polish, /#main-content\s*\{[^}]*overscroll-behavior-y:\s*contain[^}]*scrollbar-gutter:\s*stable/s);
  assert.match(effects, /html\[data-scrolling="true"\]\s+\.glass-card\.fx-spot::after[\s\S]*opacity:\s*0\s*!important/);
  assert.match(effectsJs, /document\.addEventListener\('scroll',\s*onScroll,\s*\{\s*capture:\s*true,\s*passive:\s*true\s*\}\)/);
  assert.match(effectsJs, /if\s*\(!spotRect\)\s*spotRect\s*=\s*spotEl\.getBoundingClientRect\(\)/);
  assert.doesNotMatch(effectsJs, /void\s+spotEl\.offsetWidth/);
});

test('entrance keyframes release transform and filter layers once settled', () => {
  const effectsJs = read('../src/VoltManager/wwwroot/js/effects.js');
  assert.match(redesign, /@keyframes\s+vmRiseHigh\s*\{[\s\S]*to\s*\{[^}]*transform:\s*none;[^}]*filter:\s*none/);
  assert.match(reorg, /@keyframes\s+vmReorgViewIn\s*\{[\s\S]*to\s*\{[^}]*transform:\s*none/);
  assert.match(reorg, /@keyframes\s+vmReorgPanelIn\s*\{[\s\S]*to\s*\{[^}]*transform:\s*none/);
  assert.match(reorg, /@keyframes\s+vmReorgGroupIn\s*\{[\s\S]*to\s*\{[^}]*transform:\s*none/);
  assert.match(app, /@keyframes\s+startupCardIn\{from\{[^}]*\}to\{opacity:1;transform:none\}\}/);
  assert.match(advanced, /@keyframes\s+advSlideIn\{from\{[^}]*\}to\{opacity:1;transform:none\}\}/);
  assert.match(effectsJs, /if\s*\(el\.textContent\s*!==\s*nextText\)\s*el\.textContent\s*=\s*nextText/);
});
