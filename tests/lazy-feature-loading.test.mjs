import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, statSync } from 'node:fs';

function text(path) {
  return readFileSync(new URL('../' + path, import.meta.url), 'utf8');
}

const fansLoader = text('src/VoltManager/wwwroot/js/fans.js');
const fansFeature = text('src/VoltManager/wwwroot/js/fans.feature.js');
const visualizerLoader = text('src/VoltManager/wwwroot/js/fan-visualizer.js');
const visualizerFeature = text('src/VoltManager/wwwroot/js/fan-visualizer.feature.js');
const tipsLoader = text('src/VoltManager/wwwroot/js/tips.js');
const tipsFeature = text('src/VoltManager/wwwroot/js/tips.feature.js');
const tourLoader = text('src/VoltManager/wwwroot/js/tour.js');
const tourFeature = text('src/VoltManager/wwwroot/js/tour.feature.js');

test('Fan Center and WebGL implementation are not parsed by the eager entry points', () => {
  assert.match(fansLoader, /voltuiviewchanged/);
  assert.match(fansLoader, /view === 'cooling'/);
  assert.match(fansLoader, /fans\.feature\.js/);
  assert.match(fansLoader, /fan-visualizer\.feature\.js/);
  assert.doesNotMatch(fansLoader, /getFanTopology/);
  assert.match(fansFeature, /getFanTopology/);
  assert.match(fansFeature, /fanControlChanged/);
  assert.ok(visualizerLoader.length < visualizerFeature.length);
});

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
  assert.ok(fansLoader.length < fansFeature.length / 10);
  assert.ok(tipsLoader.length < tipsFeature.length / 3);
  assert.ok(tourLoader.length < tourFeature.length / 5);
});
