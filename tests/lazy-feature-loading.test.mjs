import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

function text(path) {
  return readFileSync(new URL('../' + path, import.meta.url), 'utf8');
}

const tipsLoader = text('src/VoltManager/wwwroot/js/tips.js');
const tipsFeature = text('src/VoltManager/wwwroot/js/tips.feature.js');
const tourLoader = text('src/VoltManager/wwwroot/js/tour.js');
const tourFeature = text('src/VoltManager/wwwroot/js/tour.feature.js');

test('Energy Tips implementation loads only from its first-use entry point', () => {
  assert.match(tipsLoader, /btn-energy-tips/);
  assert.match(tipsLoader, /tips\.feature\.js/);
  assert.doesNotMatch(tipsLoader, /tip1_title/);
  assert.match(tipsFeature, /tip1_title/);
});

test('Guided Tour preserves automatic first-run and manual replay triggers lazily', () => {
  assert.match(tourLoader, /welcomecompleted/);
  assert.match(tourLoader, /settingsloaded/);
  assert.match(tourLoader, /btn-show-tour/);
  assert.match(tourLoader, /tour\.feature\.js/);
  assert.doesNotMatch(tourLoader, /tour_intro_title/);
  assert.match(tourFeature, /tour_intro_title/);
});

test('lazy entry points remain materially smaller than feature implementations', () => {
  assert.ok(tipsLoader.length < tipsFeature.length / 3);
  assert.ok(tourLoader.length < tourFeature.length / 5);
});

test('shared settings bootstrap loads at startup after its consumers, while advanced stays deferred', () => {
  const html = text('src/VoltManager/wwwroot/index.html');
  const scripts = [...html.matchAll(/<script src="js\/([^"?]+)(?:\?[^\"]*)?"><\/script>/g)].map(match => match[1]);
  assert.equal(scripts.filter(name => name === 'power.js').length, 1);
  for (const consumer of ['settings.js', 'dashboard.js', 'welcome.js', 'tour.js']) {
    assert.ok(scripts.indexOf(consumer) >= 0 && scripts.indexOf(consumer) < scripts.indexOf('power.js'));
  }
  assert.equal(scripts.includes('advanced.js'), false);
});

test('concurrent lazy loads share completion and a failed script can be retried', async () => {
  const app = text('src/VoltManager/wwwroot/js/app.js');
  const scripts = [];
  const context = vm.createContext({ document: {
    createElement: () => ({ dataset: {}, remove() { this.removed = true; } }),
    body: { appendChild: script => scripts.push(script) },
  } });
  vm.runInContext(app.slice(app.indexOf('    const deferredPowerScripts'), app.indexOf('    function needsPowerScripts')), context);
  const first = context.ensurePowerScripts();
  const concurrent = context.ensurePowerScripts();
  let completed = false;
  concurrent.then(() => { completed = true; });
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(scripts.length, 1);
  assert.equal(completed, false);
  scripts[0].onload();
  await Promise.all([first, concurrent]);
  await context.ensurePowerScripts();
  assert.equal(scripts.length, 1);

  const failed = context.loadScriptOnce('retry.js');
  const rejection = assert.rejects(failed, /load failed: retry.js/);
  scripts[1].onerror();
  await rejection;
  assert.equal(scripts[1].removed, true);
  const retry = context.loadScriptOnce('retry.js');
  assert.equal(scripts.length, 3);
  scripts[2].onload();
  await retry;
});
