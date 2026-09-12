import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

function source(path) {
    return readFileSync(new URL('../' + path, import.meta.url), 'utf8');
}

const settings = source('src/VoltManager/wwwroot/js/settings.js');
const app = source('src/VoltManager/wwwroot/js/app.js');
const i18n = source('src/VoltManager/wwwroot/js/i18n.js');
const bridge = source('src/VoltManager/Bridge/HostBridge.cs');
const reorganization = source('src/VoltManager/wwwroot/js/ui-reorganization.js');
const reorganizationLayout = source('src/VoltManager/wwwroot/js/ui-reorganization.layout.js');

test('dynamically generated widget size keys remain translated in every language', () => {
    const expected = { en: ['Mini', 'Medium', 'Large'], it: ['Mini', 'Medio', 'Grande'],
        es: ['Mini', 'Mediano', 'Grande'], zh: ['迷你', '中等', '大型'] };
    for (const [lang, labels] of Object.entries(expected)) {
        const context = { window: { addEventListener() {} }, document: { addEventListener() {} },
            localStorage: { getItem: () => lang } };
        vm.runInNewContext(i18n, context);
        ['mini', 'medium', 'large'].forEach((size, index) =>
            assert.equal(context.window.I18n.t('widget_size_' + size), labels[index]));
    }
});

test('legacy Settings auto-shutdown UI stays removed while current scheduling remains wired', () => {
    assert.doesNotMatch(settings, /auto-shutdown-panel|normalizeAutoShutdownSettings|mountAutoShutdownUi|wireAutoShutdownUi/);
    assert.doesNotMatch(app, /removeLegacyAutoShutdownPanel/);
    assert.doesNotMatch(i18n, /set_pref_autoshutdown/);
    assert.match(app, /Host\.call\('schedulePowerAction'/);
    assert.match(app, /Host\.call\('getScheduledPowerAction'/);
});

test('frontend-orphaned bridge RPC cases stay removed', () => {
    for (const method of ['setActivePlan', 'setPreviewUpdates', 'refreshAppPowerProfiles']) {
        assert.doesNotMatch(bridge, new RegExp(`case "${method}"`));
    }
});

test('reorganized sidebar uses its hidden legacy route sentinel without a redundant mutation observer', () => {
    assert.match(reorganizationLayout, /<li class="hidden" aria-hidden="true"><a data-view="system"/);
    assert.doesNotMatch(reorganization, /suppressLegacySystemItem/);
});

test('orphaned i18n keys stay removed from every language block', () => {
    const orphanedKeys = [
        'adv_loading',
        'adv_title',
        'dash_detecting_cpu',
        'dash_detecting_gpu',
        'ram_title',
        'set_changelog_latest',
        'set_changelog_title',
        'set_pref_power_source_plan',
        'set_pref_power_source_plan_sub',
        'set_startup_add',
        'set_startup_disabled',
        'set_startup_enabled',
        'set_startup_refresh',
        'set_startup_sub',
        'set_startup_title',
        'upd_banner_install',
        'upd_banner_sub',
        'upd_banner_title',
        'upd_toast_msg',
        'welcome_theme_sub',
        'widget_calendar_sub',
        'widget_clock_sub',
        'widget_plans_sub',
        'widget_position_auto',
        'widget_power_sub',
        'widget_temps_sub',
        'widget_usage_sub',
    ];
    for (const key of orphanedKeys) {
        assert.equal(i18n.includes(`"${key}":`), false, `"${key}" must not be defined in i18n.js`);
    }
});

test('unused Tailwind utility rules stay removed from compiled app.css', () => {
    const appCss = source('src/VoltManager/wwwroot/css/app.css');
    const deadRules = [
        '.bg-gradient-to-r{background-image:linear-gradient(to right,var(--tw-gradient-stops))}',
        '.bg-primary-fixed-dim\\/5{background-color:rgba(191,197,228,.05)}',
        '.bottom-0{bottom:0}',
        '.break-all{word-break:break-all}',
        '.cursor-default{cursor:default}',
        '.h-96{height:24rem}',
        '.max-w-5xl{max-width:64rem}',
        '.mr-auto{margin-right:auto}',
        '.mt-xl{margin-top:32px}',
        '.pb-8{padding-bottom:2rem}',
        '.pt-8{padding-top:2rem}',
        '.pt-lg{padding-top:24px}',
        '.right-1\\/4{right:25%}',
        '.to-secondary-container{--tw-gradient-to:#00f1fe var(--tw-gradient-to-position)}',
        '.w-96{width:24rem}',
        '.left-1\\/4{left:25%}',
        '.h-\\[500px\\]{height:500px}',
        '.w-\\[500px\\]{width:500px}',
        '.from-\\[\\#006a70\\]',
        '.blur-\\[100px\\]',
        '.lg\\:col-span-5{',
        '.lg\\:col-span-7{',
        '.shadow-\\[0_0_10px_rgba',
        '.shadow-\\[0_0_20px_rgba',
        '.shadow-\\[0_0_8px_\\#00f1fe\\]',
        '.shadow-\\[0_0_8px_rgba',
        '.hover\\:shadow-\\[0_0_15px_rgba',
    ];
    for (const rule of deadRules) {
        assert.equal(appCss.includes(rule), false, `unused CSS rule must stay removed: ${rule}`);
    }
});
