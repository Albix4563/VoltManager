import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';

const root = path.resolve(path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1')), '..');
const wwwroot = path.join(root, 'src', 'VoltManager', 'wwwroot');
const read = (...parts) => fs.readFileSync(path.join(wwwroot, ...parts), 'utf8');

test('HTML and JS assets enforce style-src self without inline styles or style tags', () => {
  for (const page of ['index.html', 'widgets.html']) {
    const html = read(page);
    const cspMatch = html.match(/<meta\b[^>]*http-equiv="Content-Security-Policy"[^>]*\bcontent="([^"]*)"/i);
    assert.ok(cspMatch, `${page} must declare a CSP meta tag`);
    const policy = cspMatch[1];
    assert.match(policy, /(?:^|;)\s*style-src\s+'self'(?:\s*;|\s*$)/i, `${page} style-src must be 'self' only`);
    assert.doesNotMatch(policy, /unsafe-inline|unsafe-eval/i, `${page} CSP must not allow unsafe-inline/eval`);
    assert.doesNotMatch(html, /<style\b/i, `${page} must not embed <style> tags`);
    assert.doesNotMatch(html, /\sstyle\s*=\s*['"]/i, `${page} must not use inline style attributes`);
  }

  const jsDir = path.join(wwwroot, 'js');
  for (const file of fs.readdirSync(jsDir).filter(name => name.endsWith('.js'))) {
    const src = read('js', file);
    assert.doesNotMatch(src, /createElement\s*\(\s*['"]style['"]\s*\)/, `${file}: must not inject <style> elements`);
    assert.doesNotMatch(src, /setAttribute\s*\(\s*['"]style['"]/i, `${file}: must not call setAttribute('style', ...)`);
    assert.doesNotMatch(src, /\.style\.cssText\b/, `${file}: must not assign style.cssText`);
    assert.doesNotMatch(src, /<style\b|\sstyle\s*=\s*['"`]/i, `${file}: must not build HTML markup with style attributes or tags`);
  }
});

test('widgets renderProcesses applies bar width via CSSOM without inline style attributes', () => {
  const widgetsSrc = read('js', 'widgets.js');
  const fnStart = widgetsSrc.indexOf('    function renderProcesses(list)');
  const fnEnd = widgetsSrc.indexOf('    // ---- Memory', fnStart);
  assert.ok(fnStart > 0 && fnEnd > fnStart);

  const bars = [{ style: {} }, { style: {} }];
  const host = {
    innerHTML: '',
    querySelectorAll: selector => (selector === '.proc-bar > span' ? bars : []),
  };
  const ctx = vm.createContext({
    document: { getElementById: id => (id === 'proc-list' ? host : null) },
    size: 'medium',
    pct: value => Math.max(0, Math.min(100, Number(value) || 0)),
    mb: value => `${value} MB`,
    esc: value => String(value),
  });
  vm.runInContext(widgetsSrc.slice(fnStart, fnEnd), ctx);
  ctx.renderProcesses([
    { name: 'Code', instances: 2, ramMb: 512, cpuPercent: 42.3 },
    { name: 'Render', instances: 1, ramMb: 256, cpuPercent: 140 },
  ]);

  assert.doesNotMatch(host.innerHTML, /\sstyle\s*=/i);
  assert.equal(bars[0].style.width, '42.3%');
  assert.equal(bars[1].style.width, '100%');
});

test('advanced sliders initialize --pct via CSSOM and RAM template uses CSS classes', () => {
  const advSrc = read('js', 'advanced.js');
  const buildFnStart = advSrc.indexOf('    function mountAdvancedUi()');
  const buildFnEnd = advSrc.indexOf('    function refreshAdvLabels()', buildFnStart);
  assert.ok(buildFnStart > 0 && buildFnEnd > buildFnStart);

  const sliders = [
    { min: '0', max: '100', value: '25', props: new Map(), style: { setProperty(k, v) { this._owner.props.set(k, v); } } },
    { min: '0', max: '100', value: '100', props: new Map(), style: { setProperty(k, v) { this._owner.props.set(k, v); } } },
  ];
  sliders.forEach(s => { s.style._owner = s; });

  const mount = {
    innerHTML: '',
    querySelectorAll: selector => (selector === '.adv-slider' ? sliders : []),
  };
  const ctx = vm.createContext({
    document: {
      getElementById: id => (id === 'vm-power-advanced' ? mount : null),
    },
    advMounted: false,
    refreshAdvLabels() {},
    checkBatteryPresence() {},
    window: {},
  });
  vm.runInContext(advSrc.slice(buildFnStart, buildFnEnd) + '\nmountAdvancedUi();', ctx);

  assert.doesNotMatch(mount.innerHTML, /\sstyle\s*=/i);
  assert.equal(sliders[0].props.get('--pct'), '25%');
  assert.equal(sliders[1].props.get('--pct'), '100%');
});

test('dashboard source strip and toasts rely on CSS classes rather than inline styles', () => {
  const dashSrc = read('js', 'dashboard.js');
  assert.match(dashSrc, /class="battery-source-seg ' \+ \(s\.ac \? 'is-ac' : 'is-dc'\)/);

  const detectSrc = read('js', 'power-detection.js');
  assert.match(detectSrc, /plan-conflict-toast__icon/);
  assert.match(detectSrc, /plan-conflict-toast__close/);

  const settingsSrc = read('js', 'settings.js');
  assert.match(settingsSrc, /upd-channel-tag--alpha/);
  assert.match(settingsSrc, /updated-toast-content/);
});
