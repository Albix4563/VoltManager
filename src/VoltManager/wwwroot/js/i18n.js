window.I18n = (function() {
    const catalogRoot = window.VoltI18nCatalogs || { metadata: {}, namespaces: { core: {} } };
    const metadata = catalogRoot.metadata;

    const translations = catalogRoot.namespaces.core;

    let lang = localStorage.getItem('volt_lang') || 'it';
    let _bridgeSetLangPending = false;

    // ===== public API =====

    function getSupportedCodes() { return Object.keys(metadata); }

    function normalizeLang(code) {
        if (!code || typeof code !== 'string') return '';
        var c = code.trim().replace(/_/g, '-');
        // Direct match
        if (metadata[c]) return c;
        // Try two-letter fallback
        var two = c.substring(0, 2);
        if (metadata[two]) return two;
        // Try culture -> code mapping
        var map = { 'it-it': 'it', 'it-ch': 'it', 'en-gb': 'en', 'en-us': 'en',
                    'zh-cn': 'zh', 'zh-hans': 'zh', 'zh-hant': 'zh', 'zh-tw': 'zh',
                    'es-es': 'es', 'es-mx': 'es', 'es-ar': 'es' };
        var lower = c.toLowerCase();
        if (map[lower]) return map[lower];
        return '';
    }

    function isSupported(code) { return normalizeLang(code) !== ''; }

    function getLang() { return lang; }

    function getLocale() {
        var m = metadata[lang];
        return m ? m.locale : 'it-IT';
    }

    function getLanguages() {
        return getSupportedCodes().map(function(c) {
            return { code: c, locale: metadata[c].locale, label: metadata[c].label };
        });
    }

    function catalog(namespace) {
        return (catalogRoot.namespaces && catalogRoot.namespaces[namespace]) || {};
    }

    function feature(namespace, key, language, fallback) {
        var namespaceCatalog = catalog(namespace);
        var requested = normalizeLang(language || lang) || 'en';
        var requestedText = namespaceCatalog[requested] && namespaceCatalog[requested][key];
        if (typeof requestedText === 'string' && requestedText !== '') return requestedText;
        var englishText = namespaceCatalog.en && namespaceCatalog.en[key];
        if (typeof englishText === 'string' && englishText !== '') return englishText;
        return typeof fallback === 'string' && fallback !== '' ? fallback : key;
    }

    function t(key) {
        return feature('core', key, lang, key);
    }

    function format(template, values) {
        var source = String(template == null ? '' : template);
        var data = values || {};
        return source.replace(/\{(\d+|[A-Za-z_][A-Za-z0-9_]*)\}/g, function(match, name) {
            var value = Array.isArray(data) ? data[Number(name)] : data[name];
            return value === undefined || value === null ? match : String(value);
        });
    }

    function localeFor(language) {
        var normalized = normalizeLang(language || lang) || 'en';
        return metadata[normalized] ? metadata[normalized].locale : metadata.en.locale;
    }

    function number(value, language, options) {
        return new Intl.NumberFormat(localeFor(language), options || {}).format(value);
    }

    function date(value, language, options) {
        var parsed = value instanceof Date ? value : new Date(value);
        return new Intl.DateTimeFormat(localeFor(language), options || {}).format(parsed);
    }

    function plural(count, forms, language) {
        var rule = new Intl.PluralRules(localeFor(language)).select(count);
        var template = forms && (forms[rule] || forms.other);
        return format(template == null ? '' : template, { count: count });
    }

    /** Interpolate: tf("key {0} {1}", [a, b]) */
    function tf(key, values) {
        var text = t(key);
        if (!values || !values.length) return text;
        return text.replace(/\{(\d+)\}/g, function(_, i) {
            return values[i] !== undefined ? values[i] : '{' + i + '}';
        });
    }

    function apply() {
        document.documentElement.lang = lang;
        // [data-i18n]
        document.querySelectorAll('[data-i18n]').forEach(function(el) {
            var key = el.getAttribute('data-i18n');
            if (!key) return;
            if (el.tagName === 'INPUT' && (el.type === 'button' || el.type === 'submit')) {
                el.value = t(key);
            } else {
                el.innerHTML = t(key);
            }
        });
        // [data-i18n-title]
        document.querySelectorAll('[data-i18n-title]').forEach(function(el) {
            var key = el.getAttribute('data-i18n-title');
            if (key) el.title = t(key);
        });
        // [data-i18n-placeholder]
        document.querySelectorAll('[data-i18n-placeholder]').forEach(function(el) {
            var key = el.getAttribute('data-i18n-placeholder');
            if (key) el.placeholder = t(key);
        });
        // [data-i18n-aria-label]
        document.querySelectorAll('[data-i18n-aria-label]').forEach(function(el) {
            var key = el.getAttribute('data-i18n-aria-label');
            if (key) el.setAttribute('aria-label', t(key));
        });
        // [data-i18n-value]
        document.querySelectorAll('[data-i18n-value]').forEach(function(el) {
            var key = el.getAttribute('data-i18n-value');
            if (key) el.value = t(key);
        });
    }

    function setLang(l) {
        var normalized = normalizeLang(l);
        if (!normalized) { console.warn('[i18n] Unsupported language: ' + l); return; }
        if (normalized === lang) return;
        lang = normalized;
        localStorage.setItem('volt_lang', normalized);
        // Keep in-memory settings cache in sync so later saveSettings won't wipe language.
        try {
            if (window.__voltSettings) {
                var s = window.__voltSettings.get ? window.__voltSettings.get() : window.__voltSettings;
                if (s) s.language = normalized;
            }
        } catch (_) { /* ignore */ }
        apply();
        document.dispatchEvent(new CustomEvent('langchanged', { detail: normalized }));
        // Also persist to backend if bridge is available.
        _persistToBackend(normalized);
    }

    /** Call setLanguage on the C# backend. Guards against re-entrant loops. */
    function _persistToBackend(code) {
        if (!window.Host || !Host.available) return;
        if (_bridgeSetLangPending) return;
        _bridgeSetLangPending = true;
        Host.call('setLanguage', { language: code })
            .catch(function(err) { console.warn('[i18n] setLanguage bridge error:', err); })
            .finally(function() { _bridgeSetLangPending = false; });
    }

    /** Called by bridge when languageChanged event is received from host or widget. */
    function onHostLanguageChanged(data) {
        if (!data || !data.language) return;
        var normalized = normalizeLang(data.language);
        if (!normalized) return;
        if (normalized === lang) return;
        lang = normalized;
        localStorage.setItem('volt_lang', normalized);
        apply();
        document.dispatchEvent(new CustomEvent('langchanged', { detail: normalized }));
    }

    /**
     * Bootstrap: resolve language from settings, localStorage, OS via bridge.
     * Called once from getSettings result.
     */
    function initFromSettings(settingsResult) {
        if (!settingsResult) return;
        var settingsNorm = normalizeLang(settingsResult.settings && settingsResult.settings.language);
        var hostNorm = normalizeLang(settingsResult.resolvedLanguage);
        var storedNorm = normalizeLang(localStorage.getItem('volt_lang'));
        // Explicit settings > localStorage (migrate) > host OS fallback.
        var resolved = settingsNorm || storedNorm || hostNorm;
        if (!resolved) return;
        if (resolved !== lang) {
            lang = resolved;
            apply();
            document.dispatchEvent(new CustomEvent('langchanged', { detail: resolved }));
        }
        localStorage.setItem('volt_lang', resolved);
        // Persist localStorage choice when settings.language is empty (migration / wiped save).
        if (!settingsNorm && storedNorm && storedNorm === resolved) {
            _persistToBackend(resolved);
        }
    }

    // Run initially
    document.addEventListener('DOMContentLoaded', apply);

    // Listen for languageChanged from bridge host/widget events.
    document.addEventListener('languageChanged', function(e) {
        if (e.detail) onHostLanguageChanged(e.detail);
    });
    // storage fallback for separate WebView documents (widgets).
    window.addEventListener('storage', function(e) {
        if (e.key === 'volt_lang' && e.newValue) {
            var normalized = normalizeLang(e.newValue);
            if (normalized && normalized !== lang) {
                lang = normalized;
                apply();
                document.dispatchEvent(new CustomEvent('langchanged', { detail: normalized }));
            }
        }
    });

    return {
        metadata: metadata,
        getSupportedCodes: getSupportedCodes,
        normalizeLang: normalizeLang,
        isSupported: isSupported,
        setLang: setLang,
        getLang: getLang,
        getLocale: getLocale,
        getLanguages: getLanguages,
        catalog: catalog,
        feature: feature,
        t: t,
        tf: tf,
        format: format,
        number: number,
        date: date,
        plural: plural,
        apply: apply,
        onHostLanguageChanged: onHostLanguageChanged,
        initFromSettings: initFromSettings
    };
})();
