/**
 * Perf guard: (1) hardware tier at boot from RAM+cores → data-perf-tier;
 * (2) RAM-pressure lite at runtime; (3) host resource profile → one semantic
 * data-resource-profile signal. effects.css/js continue to consume data-perf=lite,
 * so gaming/critical reuse the same proven low-cost rendering path.
 */
(function () {
  const ON = 85;  // enter RAM-pressure lite mode at/above this system RAM %
  const OFF = 75; // leave RAM-pressure lite mode at/below this %
  let ramLite = false;
  let resourceLite = false;
  let effectiveLite = false;

  // Pure decision: rise past ON enters, fall past OFF leaves,
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

  function syncEffectiveLite() {
    const next = ramLite || resourceLite;
    if (next === effectiveLite) return;
    effectiveLite = next;
    document.documentElement.dataset.perf = effectiveLite ? 'lite' : '';
    if (effectiveLite && window.VoltFx && window.VoltFx.stopMotion) window.VoltFx.stopMotion();
    document.dispatchEvent(new CustomEvent('perfmodechange', {
      detail: { lite: effectiveLite, ramLite, resourceLite }
    }));
  }

  function applyRamLite(next) {
    if (next === ramLite) return;
    ramLite = next;
    syncEffectiveLite();
  }

  function applyResourceProfile(state) {
    if (!state) return;
    const candidate = String(state.profile || 'full').toLowerCase();
    const profile = ['full', 'balanced', 'gaming', 'critical'].includes(candidate)
      ? candidate
      : 'full';
    const previous = document.documentElement.dataset.resourceProfile || '';
    document.documentElement.dataset.resourceProfile = profile;
    window.VoltResourceProfile = Object.assign({}, state, { profile });

    resourceLite = profile === 'gaming' || profile === 'critical';
    syncEffectiveLite();
    if (resourceLite && window.VoltFx && window.VoltFx.stopMotion) window.VoltFx.stopMotion();

    if (previous !== profile) {
      document.dispatchEvent(new CustomEvent('resourceprofilechange', {
        detail: window.VoltResourceProfile
      }));
    }
  }

  function applyTier(info) {
    if (!info) return;
    const tier = classify(info.ramTotalGb, info.logicalCores);
    document.documentElement.dataset.perfTier = tier;
    if (tier === 'lite' && window.VoltFx && window.VoltFx.stopMotion) window.VoltFx.stopMotion();
    document.dispatchEvent(new CustomEvent('perftierchange', { detail: { tier } }));
  }

  document.documentElement.dataset.resourceProfile =
    document.documentElement.dataset.resourceProfile || 'full';

  if (window.VoltSystemInfo) applyTier(window.VoltSystemInfo);
  document.addEventListener('systeminfoloaded', function (e) {
    applyTier(e.detail || window.VoltSystemInfo);
  });

  if (window.Host && Host.on) {
    Host.on('metrics', function (m) {
      if (!m || typeof m.ramPct !== 'number') return;
      applyRamLite(decide(ramLite, m.ramPct));
    });
    Host.on('resourceProfileChanged', applyResourceProfile);
  }
})();
