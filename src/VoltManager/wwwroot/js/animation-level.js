(function (root, factory) {
  const api = factory();
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  if (root) root.VoltAnimationLevel = Object.assign(root.VoltAnimationLevel || {}, api);
})(typeof window !== 'undefined' ? window : null, function () {
  function recommendedLevel(tier) {
    if (tier === 'lite') return 'low';
    if (tier === 'full') return 'high';
    return 'medium';
  }

  function resolveLevel(setting, tier) {
    return ['low', 'medium', 'high'].includes(setting)
      ? setting
      : recommendedLevel(tier);
  }

  function levelRank(level) {
    if (level === 'low') return 0;
    if (level === 'medium') return 1;
    if (level === 'high') return 2;
    return -1;
  }

  function exceedsRecommended(level, tier) {
    if (level === 'auto') return false;
    const rank = levelRank(level);
    return rank >= 0 && rank > levelRank(recommendedLevel(tier));
  }

  return { recommendedLevel, resolveLevel, levelRank, exceedsRecommended };
});
