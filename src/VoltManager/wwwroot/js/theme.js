/**
 * Phase-1 compatibility facade. Theme policy/state lives in window.Volt.style.
 */
(function () {
    'use strict';

    const style = window.Volt && window.Volt.style;
    if (!style) return;

    window.VoltTheme = {
        normalize: value => style.normalize(value),
        normalizeCustomColor: value => style.normalizeCustomColor(value),
        isSafePalette: palette => style.isSafePalette(palette),
        apply(themeColor, palette) {
            if (!palette && themeColor !== 'custom') {
                return style.applyTheme(themeColor);
            }
            return style.applyTheme(themeColor, palette);
        },
    };

    style.init();
})();
