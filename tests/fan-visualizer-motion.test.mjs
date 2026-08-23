import assert from 'node:assert/strict';
import test from 'node:test';
import vm from 'node:vm';
import { readFile } from 'node:fs/promises';

const source = await readFile(
  new URL('../src/VoltManager/wwwroot/js/fan-visualizer.feature.js', import.meta.url),
  'utf8',
);

function loadVisualizer() {
  const frames = [];
  const firstMatrices = [];
  const media = { matches: false };
  let matchMediaCalls = 0;
  let currentFrame = -1;
  const gl = new Proxy({
    COMPILE_STATUS: 1,
    LINK_STATUS: 2,
    VERTEX_SHADER: 3,
    FRAGMENT_SHADER: 4,
    ARRAY_BUFFER: 5,
    STATIC_DRAW: 6,
    FLOAT: 7,
    DEPTH_TEST: 8,
    CULL_FACE: 9,
    BACK: 10,
    BLEND: 11,
    SRC_ALPHA: 12,
    ONE_MINUS_SRC_ALPHA: 13,
    TRIANGLES: 14,
    COLOR_BUFFER_BIT: 16,
    DEPTH_BUFFER_BIT: 32,
    createShader: () => ({}),
    getShaderParameter: () => true,
    getShaderInfoLog: () => '',
    createProgram: () => ({}),
    getProgramParameter: () => true,
    getProgramInfoLog: () => '',
    getAttribLocation: () => 0,
    getUniformLocation: () => ({}),
    createBuffer: () => ({}),
    uniformMatrix4fv(_location, _transpose, matrix) {
      if (!firstMatrices[currentFrame]) firstMatrices[currentFrame] = [...matrix];
    },
  }, {
    get(target, property) {
      if (property in target) return target[property];
      return () => {};
    },
  });
  const canvas = {
    clientWidth: 320,
    clientHeight: 240,
    width: 0,
    height: 0,
    isConnected: true,
    dataset: {},
    getContext: () => gl,
  };
  const window = {
    devicePixelRatio: 1,
    matchMedia() {
      matchMediaCalls++;
      return media;
    },
  };
  vm.runInContext(source, vm.createContext({
    window,
    console,
    Float32Array,
    Math,
    Map,
    WeakMap,
    Error,
    requestAnimationFrame(callback) {
      frames.push(callback);
      return frames.length;
    },
    cancelAnimationFrame() {},
  }));
  window.FanHardwareVisualizer.mount(canvas, { type: 'fan', rpm: 1200 });
  return {
    media,
    get matchMediaCalls() { return matchMediaCalls; },
    frame(now) {
      currentFrame++;
      frames.shift()(now);
      return firstMatrices[currentFrame];
    },
  };
}

test('fan visualizer reuses one reduced-motion query across animation frames', () => {
  const visualizer = loadVisualizer();

  visualizer.frame(0);
  visualizer.frame(16);
  visualizer.media.matches = true;
  const reducedFirst = visualizer.frame(32);
  const reducedSecond = visualizer.frame(48);

  assert.equal(visualizer.matchMediaCalls, 1);
  assert.deepEqual(reducedSecond, reducedFirst);
});
