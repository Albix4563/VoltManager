/**
 * RAM-pressure guard. The UI's continuous compositing (aurora blur, backdrop
 * blur, halos, rAF tweens) piles up renderer memory; under high system RAM that
 * tipped WebView2 into OOM crashes / jank. When RAM crosses a high-water mark we
 * flip <html data-perf="lite">, which effects.css + effects.js read to drop all
 * motion and the GPU-heavy surfaces until memory recovers. Fully automatic.
 */
(function () {
  // ponytail: hysteresis band so the flag can't flap around a single threshold.
  const ON = 85;  // enter lite mode at/above this system RAM %
  const OFF = 75; // leave lite mode at/below this %
  let lite = false;

  // Pure decision (kept testable): rise past ON enters, fall past OFF leaves,
  // in between hold the current state.
  function decide(current, pct) {
    if (!current && pct >= ON) return true;
    if (current && pct <= OFF) return false;
    return current;
  }

  // self-check: fails loudly in console if the hysteresis band ever inverts.
  console.assert(
    decide(false, 80) === false && decide(false, 90) === true &&
    decide(true, 80) === true && decide(true, 70) === false,
    'perf-guard hysteresis broken');

  function apply(next) {
    if (next === lite) return;
    lite = next;
    document.documentElement.dataset.perf = lite ? 'lite' : '';
    if (lite && window.VoltFx && window.VoltFx.stopMotion) window.VoltFx.stopMotion();
    document.dispatchEvent(new CustomEvent('perfmodechange', { detail: { lite } }));
  }

  if (window.Host && Host.on) {
    Host.on('metrics', (m) => {
      if (!m || typeof m.ramPct !== 'number') return;
      apply(decide(lite, m.ramPct));
    });
  }
})();
