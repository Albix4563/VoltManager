/**
 * Perf guard: (1) hardware tier at boot from RAM+cores → data-perf-tier;
 * (2) RAM-pressure lite at runtime → data-perf=lite. effects.css/js read both.
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

  console.assert(
    decide(false, 80) === false && decide(false, 90) === true &&
    decide(true, 80) === true && decide(true, 70) === false,
    'perf-guard hysteresis broken');

  /** @returns {'full'|'balanced'|'lite'} */
  function classify(ramGb, cores) {
    const ram = Number(ramGb);
    const c = Number(cores);
    if (!Number.isFinite(ram) || !Number.isFinite(c)) return 'full';
    if (ram < 8 || c <= 2) return 'lite';
    if (ram < 16 || c <= 4) return 'balanced';
    return 'full';
  }

  console.assert(
    classify(4, 2) === 'lite' &&
    classify(7.9, 8) === 'lite' &&
    classify(8, 4) === 'balanced' &&
    classify(8, 2) === 'lite' &&
    classify(16, 4) === 'balanced' &&
    classify(16, 5) === 'full' &&
    classify(32, 8) === 'full' &&
    classify(undefined, 8) === 'full' &&
    classify(16, NaN) === 'full',
    'perf-guard classify broken');

  function applyLite(next) {
    if (next === lite) return;
    lite = next;
    document.documentElement.dataset.perf = lite ? 'lite' : '';
    if (lite && window.VoltFx && window.VoltFx.stopMotion) window.VoltFx.stopMotion();
    document.dispatchEvent(new CustomEvent('perfmodechange', { detail: { lite } }));
  }

  function applyTier(info) {
    if (!info) return;
    const tier = classify(info.ramTotalGb, info.logicalCores);
    document.documentElement.dataset.perfTier = tier;
    if (tier === 'lite' && window.VoltFx && window.VoltFx.stopMotion) window.VoltFx.stopMotion();
    document.dispatchEvent(new CustomEvent('perftierchange', { detail: { tier } }));
  }

  if (window.VoltSystemInfo) applyTier(window.VoltSystemInfo);
  document.addEventListener('systeminfoloaded', function (e) {
    applyTier(e.detail || window.VoltSystemInfo);
  });

  if (window.Host && Host.on) {
    Host.on('metrics', function (m) {
      if (!m || typeof m.ramPct !== 'number') return;
      applyLite(decide(lite, m.ramPct));
    });
  }
})();
