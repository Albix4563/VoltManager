(function () {
    const allowedThemeColors = ['blue', 'red', 'green', 'orange', 'purple', 'pink', 'gray'];

    function normalize(themeColor) {
        const value = typeof themeColor === 'string' ? themeColor.trim().toLowerCase() : '';
        return allowedThemeColors.includes(value) ? value : 'blue';
    }

    function toRgbChannels(hex) {
        const value = String(hex || '').replace('#', '');
        if (!/^[0-9a-f]{6}$/i.test(value)) return '59 130 246';
        return [0, 2, 4]
            .map(index => parseInt(value.slice(index, index + 2), 16))
            .join(' ');
    }

    function applyPalette(palette) {
        if (!palette) return;

        const root = document.documentElement.style;
        root.setProperty('--vm-bg', palette.background);
        root.setProperty('--vm-bg-deep', palette.background);
        root.setProperty('--vm-surface', palette.surface);
        root.setProperty('--vm-surface-low', palette.surface);
        root.setProperty('--vm-surface-high', palette.surfaceElevated);
        root.setProperty('--vm-panel', palette.surface);
        root.setProperty('--vm-card', palette.surfaceElevated);
        root.setProperty('--vm-border', palette.border);
        root.setProperty('--vm-border-strong', palette.secondary);
        root.setProperty('--vm-text', palette.text);
        root.setProperty('--vm-muted', palette.mutedText);
        root.setProperty('--vm-muted-soft', palette.mutedText);
        root.setProperty('--vm-accent', palette.primary);
        root.setProperty('--vm-accent-dim', palette.secondary);
        root.setProperty('--vm-accent-hover', palette.hover);
        root.setProperty('--vm-accent-text', palette.onPrimary);
        root.setProperty('--vm-on-accent', palette.onPrimary);
        root.setProperty('--vm-accent-rgb', toRgbChannels(palette.primary));
        root.setProperty('--md-sys-color-secondary-container', palette.primary);
        root.setProperty('--md-sys-color-surface-container-high', palette.surfaceElevated);
    }

    function apply(themeColor, palette) {
        const normalized = normalize(themeColor);
        const catalog = window.__voltThemeCatalog || {};
        const resolvedPalette = palette || catalog[normalized];

        document.documentElement.dataset.themeColor = normalized;
        applyPalette(resolvedPalette);
        return normalized;
    }

    window.VoltTheme = {
        colors: Object.freeze(allowedThemeColors.slice()),
        normalize,
        apply,
    };

    apply(document.documentElement.dataset.themeColor);
})();
