import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

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
