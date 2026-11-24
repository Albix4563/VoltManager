import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { runInNewContext } from 'node:vm';

const read = file => readFileSync(new URL(`../src/VoltManager/wwwroot/${file}`, import.meta.url), 'utf8');

test('le impostazioni CPU, animazioni e lingua hanno etichette localizzate', () => {
    const html = read('index.html');
    const context = { window: {} };
    runInNewContext(read('js/i18n.catalogs.js'), context);
    const catalogs = Object.values(context.window.VoltI18nCatalogs.namespaces.core);
    const localized = key => {
        assert.ok(key);
        assert.ok(catalogs.length > 0 && catalogs.every(catalog => catalog[key]?.trim()), key);
    };

    for (const id of ['cpu-sample-interval', 'animation-level-select', 'lang-select', 'welcome-lang-select']) {
        const label = html.match(new RegExp(`<label\\b[^>]*\\bfor="${id}"[^>]*>`))?.[0];
        localized(label?.match(/data-i18n="([^"]+)"/)?.[1]);
    }

    for (const plan of ['saver', 'balanced', 'performance']) {
        for (const [field, suffix] of [['threshold', 'if'], ['minutes', 'min']]) {
            const input = html.match(new RegExp(`<input\\b[^>]*\\bid="rule-${plan}-${field}"[^>]*>`))?.[0];
            const labels = [`rule-${plan}-target`, `rule-${plan}-${suffix}`];
            assert.ok(input?.includes(`aria-labelledby="${labels.join(' ')}"`), `${plan}-${field}`);
            for (const id of labels) {
                const span = html.match(new RegExp(`<span\\b[^>]*\\bid="${id}"[^>]*>`))?.[0];
                localized(span?.match(/data-i18n="([^"]+)"/)?.[1]);
            }
        }
    }
});

test('le icone del controllo remoto hanno un fallback generico', () => {
    assert.match(read('remote/remote.css'), /font-family:\s*'Material Symbols Outlined',\s*sans-serif\s*;/);
});
