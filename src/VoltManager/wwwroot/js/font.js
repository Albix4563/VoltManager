window.VoltFont = (function() {
    const fonts = {
        'inter': 'Inter, system-ui, -apple-system, sans-serif',
        'segoe-ui': '"Segoe UI", system-ui, -apple-system, sans-serif',
        'arial': 'Arial, Helvetica, sans-serif',
        'calibri': 'Calibri, Candara, "Segoe UI", sans-serif',
        'verdana': 'Verdana, Geneva, sans-serif',
        'tahoma': 'Tahoma, Verdana, sans-serif',
        'trebuchet-ms': '"Trebuchet MS", Arial, sans-serif',
        'georgia': 'Georgia, serif',
        'times-new-roman': '"Times New Roman", Times, serif',
        'consolas': 'Consolas, "Courier New", monospace'
    };

    function normalize(key) {
        if (!key || typeof key !== 'string') return 'inter';
        const k = key.trim().toLowerCase();
        return fonts[k] ? k : 'inter';
    }

    function apply(key) {
        const norm = normalize(key);
        const stack = fonts[norm];
        document.documentElement.style.setProperty('--vm-font-family', stack);
        return norm;
    }

    return {
        normalize: normalize,
        apply: apply,
        fonts: fonts
    };
})();
