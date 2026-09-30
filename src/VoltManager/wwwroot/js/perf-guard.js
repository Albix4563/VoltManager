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
  let hwTier = null;
  let animationLevel = 'auto';

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
    return window.VoltAnimationLevel && window.VoltAnimationLevel.classifyHardwareTier
      ? window.VoltAnimationLevel.classifyHardwareTier(ramGb, cores)
      : 'full';
  }

  if (window.VoltAnimationLevel && window.VoltAnimationLevel.classifyHardwareTier) {
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
  }

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
    const profile = ['full', 'balanced', 'gaming', 'workload', 'critical'].includes(candidate)
      ? candidate
      : 'full';
    const previous = window.VoltResourceProfile;
    document.documentElement.dataset.resourceProfile = profile;
    window.VoltResourceProfile = Object.assign({}, state, { profile });

    resourceLite = !!state.reducedEffects || profile === 'gaming' || profile === 'workload' || profile === 'critical';
    syncEffectiveLite();
    if (resourceLite && window.VoltFx && window.VoltFx.stopMotion) window.VoltFx.stopMotion();

    if (!previous || ['profile', 'uiVisible', 'uiActive', 'reducedEffects',
      'allowProcessPolling', 'processPollingIntervalMs', 'metricsIntervalMs']
      .some(key => previous[key] !== window.VoltResourceProfile[key])) {
      document.dispatchEvent(new CustomEvent('resourceprofilechange', {
        detail: window.VoltResourceProfile
      }));
    }
  }

  function currentAnimationSetting(eventDetail) {
    const detailSettings = eventDetail && (eventDetail.settings || eventDetail);
    if (detailSettings && typeof detailSettings.animationLevel === 'string') {
      return detailSettings.animationLevel;
    }
    const store = window.__voltSettings;
    const settings = store && (store.get ? store.get() : store);
    return settings && typeof settings.animationLevel === 'string'
      ? settings.animationLevel
      : animationLevel;
  }

  function applyAnimationLevel() {
    if (!hwTier || !window.VoltAnimationLevel) return;
    const level = window.VoltAnimationLevel.resolveLevel(animationLevel, hwTier);
    const tier = level === 'low' ? 'lite' : level === 'high' ? 'full' : 'balanced';
    const html = document.documentElement;
    html.dataset.perfTier = tier;
    html.dataset.anim = level;
    if (level === 'high') html.dataset.fx = 'rich';
    else delete html.dataset.fx;
    if (tier === 'lite' && window.VoltFx && window.VoltFx.stopMotion) window.VoltFx.stopMotion();
    document.dispatchEvent(new CustomEvent('perftierchange', {
      detail: { tier, hwTier, animationLevel: level }
    }));
  }

  function applyTier(info) {
    if (!info) return;
    hwTier = classify(info.ramTotalGb, info.logicalCores);
    document.documentElement.dataset.hwTier = hwTier;
    applyAnimationLevel();
  }

  document.documentElement.dataset.resourceProfile =
    document.documentElement.dataset.resourceProfile || 'full';

  if (window.VoltAnimationLevel) {
    window.VoltAnimationLevel.hardwareTier = () => hwTier;
    window.VoltAnimationLevel.recommended = () =>
      window.VoltAnimationLevel.recommendedLevel(hwTier);
    window.VoltAnimationLevel.effective = () =>
      hwTier ? window.VoltAnimationLevel.resolveLevel(animationLevel, hwTier) : null;
  }

  if (window.__voltSettings) animationLevel = currentAnimationSetting();
  if (window.VoltSystemInfo) applyTier(window.VoltSystemInfo);
  document.addEventListener('systeminfoloaded', function (e) {
    applyTier(e.detail || window.VoltSystemInfo);
  });
  document.addEventListener('settingsloaded', function (e) {
    animationLevel = currentAnimationSetting(e.detail);
    applyAnimationLevel();
  });
  document.addEventListener('animationlevelchange', function (e) {
    animationLevel = e.detail && e.detail.level ? e.detail.level : currentAnimationSetting();
    applyAnimationLevel();
  });

  if (window.Host && Host.on) {
    Host.on('metrics', function (m) {
      if (!m || typeof m.ramPct !== 'number') return;
      applyRamLite(decide(ramLite, m.ramPct));
    });
    Host.on('resourceProfileChanged', applyResourceProfile);
  }
})();
