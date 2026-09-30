/**
 * Phase-1 compatibility facade. Performance/motion/effects policy lives in
 * window.Volt.style; this file keeps the legacy VoltAnimationLevel queries.
 */
(function () {
  'use strict';

  const style = window.Volt && window.Volt.style;
  if (!style) return;

  style.init();
  style.bindHost(window.Host);

  const compat = window.VoltAnimationLevel = window.VoltAnimationLevel || {};
  compat.classifyHardwareTier = compat.classifyHardwareTier || style.classifyHardwareTier;
  compat.recommendedLevel = compat.recommendedLevel || style.recommendedLevel;
  compat.resolveLevel = compat.resolveLevel || style.resolveLevel;
  compat.hardwareTier = () => style.getState().hardwareTier || 'full';
  compat.recommended = () => style.recommendedLevel(compat.hardwareTier());
  compat.effective = setting => style.resolveLevel(setting || 'auto', compat.hardwareTier());
})();
