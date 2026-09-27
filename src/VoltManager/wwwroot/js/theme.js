(function () {
    const allowedThemeColors = ['blue', 'red', 'green', 'orange', 'purple', 'pink', 'gray'];
    const customCompositeBase = [11, 17, 32]; // #0B1120, shared with ThemeService.BaseBackground.
    const paletteKeys = [
        'background', 'surface', 'surfaceElevated', 'primary', 'secondary',
        'hover', 'text', 'mutedText', 'border', 'onPrimary',
    ];

    function normalize(themeColor) {
        const value = typeof themeColor === 'string' ? themeColor.trim().toLowerCase() : '';
        return allowedThemeColors.includes(value) ? value : 'blue';
    }

    function normalizeCustomColor(input) {
        const value = typeof input === 'string' ? input.trim() : '';
        if (!/^#(?:[0-9a-f]{3}|[0-9a-f]{4}|[0-9a-f]{6})$/i.test(value)) return null;

        const hex = value.slice(1);
        let r;
        let g;
        let b;
        let a = 255;
        if (hex.length === 3 || hex.length === 4) {
            r = parseInt(hex[0] + hex[0], 16);
            g = parseInt(hex[1] + hex[1], 16);
            b = parseInt(hex[2] + hex[2], 16);
            if (hex.length === 4) a = parseInt(hex[3] + hex[3], 16);
        } else {
            r = parseInt(hex.slice(0, 2), 16);
            g = parseInt(hex.slice(2, 4), 16);
            b = parseInt(hex.slice(4, 6), 16);
        }

        if (a < 255) {
            const weight = a / 255;
            r = Math.round(r * weight + customCompositeBase[0] * (1 - weight));
            g = Math.round(g * weight + customCompositeBase[1] * (1 - weight));
            b = Math.round(b * weight + customCompositeBase[2] * (1 - weight));
        }

        return '#' + [r, g, b]
            .map(channel => channel.toString(16).padStart(2, '0').toUpperCase())
            .join('');
    }

    function isSafePalette(palette) {
        return !!palette && paletteKeys.every(key =>
            typeof palette[key] === 'string' && /^#[0-9a-f]{6}$/i.test(palette[key]));
    }

    function toRgbChannels(hex) {
        const value = String(hex || '').replace('#', '');
        if (!/^[0-9a-f]{6}$/i.test(value)) return '59 130 246';
        return [0, 2, 4]
            .map(index => parseInt(value.slice(index, index + 2), 16))
            .join(' ');
    }

    function applyPalette(palette) {
        if (!isSafePalette(palette)) return false;

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

        // Legacy Material-style tokens are still consumed by the reorganized UI.
        // Keep them explicitly synchronized so no component can fall back to the
        // old navy/cyan prototype palette when a non-blue theme is selected.
        root.setProperty('--md-sys-color-background', palette.background);
        root.setProperty('--md-sys-color-surface', palette.surface);
        root.setProperty('--md-sys-color-surface-container-lowest', palette.background);
        root.setProperty('--md-sys-color-surface-container-low', palette.surface);
        root.setProperty('--md-sys-color-surface-container', palette.surface);
        root.setProperty('--md-sys-color-surface-container-high', palette.surfaceElevated);
        root.setProperty('--md-sys-color-surface-container-highest', palette.surfaceElevated);
        root.setProperty('--md-sys-color-on-surface', palette.text);
        root.setProperty('--md-sys-color-on-surface-variant', palette.mutedText);
        root.setProperty('--md-sys-color-outline', palette.border);
        root.setProperty('--md-sys-color-outline-variant', palette.border);
        root.setProperty('--md-sys-color-secondary', palette.secondary);
        root.setProperty('--md-sys-color-secondary-container', palette.primary);
        root.setProperty('--md-sys-color-on-secondary-container', palette.onPrimary);
        return true;
    }

    function apply(themeColor, palette) {
        const requested = typeof themeColor === 'string' ? themeColor.trim().toLowerCase() : '';
        const isCustom = requested === 'custom';
        const normalized = isCustom ? 'custom' : normalize(requested);
        const catalog = window.__voltThemeCatalog || {};
        let resolvedPalette = palette || (!isCustom ? catalog[normalized] : null);
        let appliedTheme = normalized;

        if (!isSafePalette(resolvedPalette)) {
            appliedTheme = isCustom ? 'blue' : normalized;
            resolvedPalette = catalog[appliedTheme] || catalog.blue;
        }

        if (isSafePalette(resolvedPalette)) {
            document.documentElement.dataset.themeColor = appliedTheme;
            applyPalette(resolvedPalette);
        }
        return appliedTheme;
    }

    window.VoltTheme = {
        normalize,
        normalizeCustomColor,
        isSafePalette,
        apply,
    };

    // MainWindow injects ThemeWebState before the HTML document is parsed. Use
    // that native source of truth for the first paint; the hard-coded Blue data
    // attribute remains only as a defensive fallback if bootstrap registration
    // fails or the page is opened outside the desktop host.
    const bootstrapState = window.__voltThemeState || null;
    apply(
        bootstrapState && bootstrapState.themeColor
            ? bootstrapState.themeColor
            : document.documentElement.dataset.themeColor,
        bootstrapState && bootstrapState.palette);
})();
