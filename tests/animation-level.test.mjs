import test from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { readFileSync } from 'node:fs';

const require = createRequire(import.meta.url);
const animationLevel = require('../src/VoltManager/wwwroot/js/animation-level.js');
const indexHtml = readFileSync(
  new URL('../src/VoltManager/wwwroot/index.html', import.meta.url),
  'utf8');

test('recommendedLevel maps hardware tiers to animation levels', () => {
  assert.equal(animationLevel.recommendedLevel('lite'), 'low');
  assert.equal(animationLevel.recommendedLevel('balanced'), 'medium');
  assert.equal(animationLevel.recommendedLevel('full'), 'high');
});

test('classifyHardwareTier matches the main UI hardware thresholds', () => {
  assert.equal(animationLevel.classifyHardwareTier(4, 8), 'lite');
  assert.equal(animationLevel.classifyHardwareTier(16, 2), 'lite');
  assert.equal(animationLevel.classifyHardwareTier(8, 4), 'balanced');
  assert.equal(animationLevel.classifyHardwareTier(16, 5), 'full');
  assert.equal(animationLevel.classifyHardwareTier(undefined, 8), 'full');
});

test('resolveLevel keeps explicit levels and treats every other setting as auto', () => {
  for (const level of ['low', 'medium', 'high']) {
    assert.equal(animationLevel.resolveLevel(level, 'balanced'), level);
  }

  assert.equal(animationLevel.resolveLevel('auto', 'lite'), 'low');
  assert.equal(animationLevel.resolveLevel(undefined, 'balanced'), 'medium');
  assert.equal(animationLevel.resolveLevel('garbage', 'full'), 'high');
});

test('levelRank and exceedsRecommended compare explicit levels to hardware recommendation', () => {
  assert.equal(animationLevel.levelRank('low'), 0);
  assert.equal(animationLevel.levelRank('medium'), 1);
  assert.equal(animationLevel.levelRank('high'), 2);
  assert.equal(animationLevel.levelRank('garbage'), -1);

  assert.equal(animationLevel.exceedsRecommended('high', 'lite'), true);
  assert.equal(animationLevel.exceedsRecommended('medium', 'lite'), true);
  assert.equal(animationLevel.exceedsRecommended('low', 'lite'), false);
  assert.equal(animationLevel.exceedsRecommended('medium', 'balanced'), false);
  assert.equal(animationLevel.exceedsRecommended('auto', 'lite'), false);
});

test('index loads the animation-level helper before perf-guard and exposes its select', () => {
  const helperIndex = indexHtml.indexOf('js/animation-level.js');
  const perfGuardIndex = indexHtml.indexOf('js/perf-guard.js');

  assert.ok(helperIndex >= 0, 'animation-level.js should be loaded');
  assert.ok(perfGuardIndex >= 0, 'perf-guard.js should be loaded');
  assert.ok(helperIndex < perfGuardIndex, 'animation-level.js should load before perf-guard.js');
  assert.match(indexHtml, /id=["']animation-level-select["']/);
});
