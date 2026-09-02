window.VoltFont = (function() {
    const fonts = {
        'inter': 'Inter, system-ui, -apple-system, sans-serif',
        'segoe-ui': '"Segoe UI", system-ui, -apple-system, sans-serif',
        'arial': 'Arial, Helvetica, sans-serif',
        'calibri': 'Calibri, Candara, "Segoe UI", sans-serif',
        'verdana': 'Verdana, Geneva, sans-serif',
        'tahoma': 'Tahoma, Verdana, sans-serif',
        'trebuchet-ms': '"Trebuchet MS", Arial, sans-serif',
        'candara': 'Candara, Calibri, "Segoe UI", sans-serif',
        'corbel': 'Corbel, "Segoe UI", sans-serif',
        'century-gothic': '"Century Gothic", Arial, sans-serif',
        'franklin-gothic': '"Franklin Gothic Medium", "Arial Narrow", Arial, sans-serif',
        'georgia': 'Georgia, serif',
        'cambria': 'Cambria, Georgia, serif',
        'palatino-linotype': '"Palatino Linotype", Palatino, Georgia, serif',
        'times-new-roman': '"Times New Roman", Times, serif',
        'consolas': 'Consolas, "Courier New", monospace',
        'courier-new': '"Courier New", Courier, monospace',
        'lucida-console': '"Lucida Console", Monaco, monospace'
    };

    function normalize(key) {
        if (!key || typeof key !== 'string') return 'inter';
        const k = key.trim().toLowerCase();
        return Object.prototype.hasOwnProperty.call(fonts, k) ? k : 'inter';
    }

    function stackFor(key) {
        return fonts[normalize(key)];
    }

    function apply(key) {
        const norm = normalize(key);
        const stack = fonts[norm];
        // Set on both html and body so Tailwind's html{font-family:...} never
        // wins over our runtime choice for elements that inherit from body.
        document.documentElement.style.setProperty('--vm-font-family', stack);
        if (document.body) {
            document.body.style.fontFamily = stack;
        }
        document.documentElement.style.fontFamily = stack;
        return norm;
    }

    return {
        normalize: normalize,
        apply: apply,
        stackFor: stackFor,
        fonts: fonts,
        keys: Object.keys(fonts)
    };
})();
