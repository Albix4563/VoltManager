import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const read = (path) => readFileSync(new URL(path, import.meta.url), 'utf8');

const motion = read('../src/VoltManager/wwwroot/css/motion.css');
const redesign = read('../src/VoltManager/wwwroot/css/redesign.css');
const effects = read('../src/VoltManager/wwwroot/css/effects.css');
const reorg = read('../src/VoltManager/wwwroot/css/ui-reorganization.css');
const app = read('../src/VoltManager/wwwroot/js/app.js');
const power = read('../src/VoltManager/wwwroot/js/power.js');
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
  assert.match(redesign, /transition:\s*width\s+var\(--vm-motion-base\)/);
  assert.match(redesign, /transition:\s*margin-left\s+var\(--vm-motion-base\)/);
  assert.match(power, /grid-template-rows\s+var\(--vm-motion-base\)/);
  assert.match(power, /padding\s+var\(--vm-motion-base\)/);
  assert.match(app, /transform\s+480ms/);
  assert.match(reorg, /animation:\s*vmReorgViewIn\s+600ms/);
  assert.match(reorg, /animation:\s*vmReorgPanelIn\s+560ms/);
  assert.match(reorg, /animation-delay:\s*80ms/);
});

test('decorative motion is slower and reduced-motion/performance tiers remain intact', () => {
  assert.match(effects, /vmSheen\s+1\.25s/);
  assert.match(effects, /vmRipple\s+\.8s/);
  assert.match(effects, /vmNavPulse\s+3\.8s/);
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
  assert.match(power, /\.app-profile-icon-btn[^\n]*var\(--vm-motion-fast\)/);
  assert.match(widgets, /\.widget-bar span[\s\S]*transition:\s*width var\(--vm-motion-fast\) var\(--vm-ease-standard\)/);
});

test('slower decorative loops also reduce persistent glow intensity', () => {
  assert.match(effects, /\.vm-aurora__orb\s*\{[\s\S]*opacity:\s*\.34/s);
  assert.match(effects, /@keyframes vmNavPulse\s*\{[\s\S]*\/ \.35\)[\s\S]*\/ \.55\)/s);
});
