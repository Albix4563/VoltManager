/**
 * Context-aware energy suggestions.
 *
 * Turns the live power state (battery level, AC/battery, active plan, auto
 * boost, keep-awake, gaming mode, brightness, manual override) into a short,
 * prioritized list of actionable recommendations.
 *
 * Results are projected on the Home overview, on the top-bar tips badge and,
 * through getPreferredTipIds(), at the front of the energy tips carousel so
 * the advice that matches the current situation is shown first.
 *
 * The rule engine is pure and exposed as __energySuggestions.evaluate(snapshot)
 * so other surfaces (widgets, remote control) can reuse it later.
 */
(function () {
    'use strict';

    const CATALOG = 'energySuggestions';
    const MAX_VISIBLE = 2;

    let powerState = null;          // last PowerSourcePlanState from the host
    let keepAwake = null;           // runtime keep-awake state; null = not resolved yet
    let lastPowerRefresh = 0;
    let mounted = false;
    let wired = false;
    let card = null;
    let listEl = null;
    let headingEl = null;
    let openBtn = null;
    let footEl = null;
    let badgeEl = null;
    let buttonEl = null;
    let rendered = [];
    let renderedSignature = '';
    let contextCount = 0;

    function t(key, params) {
        const lang = window.I18n && I18n.getLang ? I18n.getLang() : 'it';
        let value = window.I18n && I18n.feature ? I18n.feature(CATALOG, key, lang) : key;
        Object.keys(params || {}).forEach(name => {
            value = value.split('{' + name + '}').join(String(params[name]));
        });
        return value;
    }

    function percentValue(value) {
        const match = String(value == null ? '' : value).replace(',', '.').match(/-?\d+(?:\.\d+)?/);
        const number = match ? Number(match[0]) : NaN;
        return Number.isFinite(number) ? Math.max(0, Math.min(100, Math.round(number))) : null;
    }

    function readSettings() {
        const store = window.__voltSettings;
        if (!store) return null;
        try {
            return typeof store.get === 'function' ? store.get() : store;
        } catch (_) {
            return null;
        }
    }

    function readPlan() {
        const buttons = document.querySelectorAll('#plan-control [data-plan]');
        for (let index = 0; index < buttons.length; index += 1) {
            const button = buttons[index];
            if (button.classList.contains('text-secondary-container') ||
                button.getAttribute('aria-pressed') === 'true' ||
                button.dataset.active === 'true') {
                return button.dataset.plan || null;
            }
        }
        return null;
    }

    function readBatteryPercentFromDom() {
        return percentValue(document.getElementById('power-flow-percent')?.textContent);
    }

    function readBrightness() {
        const fromOutput = percentValue(document.getElementById('vm-brightness-value')?.textContent);
        if (fromOutput != null) return fromOutput;
        const range = document.getElementById('vm-brightness-range');
        if (range && String(range.value || '') !== '') return percentValue(range.value);
        return null;
    }

    function readAutomation(settings) {
        if (settings && typeof settings.masterAutomationEnabled === 'boolean') {
            return settings.masterAutomationEnabled;
        }
        const master = document.getElementById('master-toggle');
        return master ? master.checked !== false : true;
    }

    function readOverride(settings) {
        let active = false;
        let plan = null;
        const override = settings && settings.override;
        if (override && typeof override === 'object') {
            plan = typeof override.plan === 'string' ? override.plan : null;
            const expires = override.expiresAtUtc ? Date.parse(override.expiresAtUtc) : NaN;
            active = !override.expiresAtUtc || Number.isNaN(expires) || expires > Date.now();
        }
        const chip = document.getElementById('manual-override-chip');
        if (chip && !chip.classList.contains('hidden')) active = true;
        return { active, plan: plan || readPlan() };
    }

    function snapshot() {
        const settings = readSettings();
        const hasBattery = document.documentElement.dataset.vmHasBattery === 'true';
        const sourceKnown = !!(powerState && powerState.powerSourceKnown);
        const override = readOverride(settings);
        const hostPercent = powerState && Number.isFinite(Number(powerState.batteryPercent))
            ? percentValue(powerState.batteryPercent)
            : null;

        let keepAwakeActive;
        if (keepAwake != null) keepAwakeActive = !!keepAwake;
        else keepAwakeActive = !!(settings && settings.keepAwake && settings.keepAwake.enabled);

        return {
            hasBattery,
            onAc: sourceKnown ? !!powerState.pluggedIn : null,
            batteryPercent: hostPercent != null ? hostPercent : readBatteryPercentFromDom(),
            plan: readPlan(),
            automation: readAutomation(settings),
            powerSourcePlan: powerState
                ? !!powerState.enabled
                : !(settings && settings.powerSourcePlan && settings.powerSourcePlan.enabled === false),
            override: override.active,
            overridePlan: override.plan,
            keepAwake: keepAwakeActive,
            gaming: !!(window.__voltGamingMode && window.__voltGamingMode.isActive &&
                window.__voltGamingMode.isActive()),
            brightness: readBrightness(),
        };
    }

    const RULES = [
        {
            id: 'low-battery',
            priority: 100,
            tone: 'critical',
            icon: 'battery_alert',
            tipId: 'tip1',
            titleKey: 'lowBattery_title',
            bodyKey: 'lowBattery_body',
            ctaKey: 'lowBattery_cta',
            action: { kind: 'switch-plan', plan: 'powerSaver' },
            match: s => s.hasBattery && s.onAc === false && s.batteryPercent != null &&
                s.batteryPercent <= 20 && s.plan !== 'powerSaver',
        },
        {
            id: 'performance-on-battery',
            priority: 90,
            tone: 'recommended',
            icon: 'speed',
            tipId: 'tip1',
            titleKey: 'performance_title',
            bodyKey: 'performance_body',
            ctaKey: 'performance_cta',
            action: { kind: 'switch-plan', plan: 'balanced' },
            match: s => s.hasBattery && s.onAc === false && s.plan === 'performance' && !s.override,
        },
        {
            id: 'keep-awake-on-battery',
            priority: 80,
            tone: 'recommended',
            icon: 'bedtime_off',
            titleKey: 'keepAwake_title',
            bodyKey: 'keepAwake_body',
            ctaKey: 'keepAwake_cta',
            action: { kind: 'disable-keep-awake' },
            match: s => s.hasBattery && s.onAc === false && s.keepAwake,
        },
        {
            id: 'gaming-on-battery',
            priority: 75,
            tone: 'recommended',
            icon: 'sports_esports',
            titleKey: 'gaming_title',
            bodyKey: 'gaming_body',
            ctaKey: 'gaming_cta',
            action: { kind: 'disable-gaming' },
            match: s => s.hasBattery && s.onAc === false && s.gaming,
        },
        {
            id: 'override-on-battery',
            priority: 70,
            tone: 'recommended',
            icon: 'lock',
            tipId: 'tip8',
            titleKey: 'override_title',
            bodyKey: 'override_body',
            ctaKey: 'override_cta',
            action: { kind: 'clear-override' },
            match: s => s.hasBattery && s.onAc === false && s.override &&
                (s.overridePlan === 'performance' || s.plan === 'performance'),
        },
        {
            id: 'brightness-high',
            priority: 60,
            tone: 'tip',
            icon: 'brightness_6',
            tipId: 'tip3',
            titleKey: 'brightness_title',
            bodyKey: 'brightness_body',
            ctaKey: 'brightness_cta',
            action: { kind: 'dim-brightness' },
            match: s => s.hasBattery && s.onAc === false && s.brightness != null &&
                s.brightness >= 70 && (s.batteryPercent == null || s.batteryPercent < 60),
        },
        {
            id: 'auto-boost-off',
            priority: 50,
            tone: 'recommended',
            icon: 'power',
            tipId: 'tip2',
            titleKey: 'autoBoost_title',
            bodyKey: 'autoBoost_body',
            ctaKey: 'autoBoost_cta',
            action: { kind: 'enable-auto-boost' },
            match: s => s.hasBattery && s.automation && !s.powerSourcePlan && !s.override,
        },
        {
            id: 'automation-off',
            priority: 40,
            tone: 'recommended',
            icon: 'automation',
            tipId: 'tip8',
            titleKey: 'automation_title',
            bodyKey: 'automation_body',
            ctaKey: 'automation_cta',
            action: { kind: 'open-automations' },
            match: s => !s.automation,
        },
    ];

    function suggestionFor(rule, state) {
        const values = { percent: state.batteryPercent == null ? '--' : state.batteryPercent };
        return {
            id: rule.id,
            priority: rule.priority,
            tone: rule.tone,
            icon: rule.icon,
            context: true,
            tipId: rule.tipId || null,
            title: t(rule.titleKey, values),
            body: t(rule.bodyKey, values),
            cta: t(rule.ctaKey, values),
            action: rule.action,
        };
    }

    function generalSuggestion() {
        return {
            id: 'general-tip',
            priority: 10,
            tone: 'general',
            icon: 'tips_and_updates',
            context: false,
            tipId: null,
            title: t('general_title'),
            body: t('general_body'),
            cta: t('general_cta'),
            action: { kind: 'open-tips' },
        };
    }

    function evaluate(state) {
        const current = state || {};
        const matched = [];
        RULES.forEach(rule => {
            try {
                if (rule.match(current)) matched.push(suggestionFor(rule, current));
            } catch (_) {
                // A malformed snapshot must never break the Home card.
            }
        });
        matched.sort((a, b) => b.priority - a.priority);
        return matched.length ? matched : [generalSuggestion()];
    }

    function getPreferredTipIds(state) {
        const ids = evaluate(state || snapshot()).map(item => item.tipId).filter(Boolean);
        return Array.from(new Set(ids));
    }

    function click(node) {
        if (!node) return false;
        if (typeof node.click === 'function') {
            node.click();
            return true;
        }
        if (typeof MouseEvent === 'function') {
            node.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
            return true;
        }
        node.dispatchEvent({ type: 'click', bubbles: true, cancelable: true });
        return true;
    }

    function navigate(view, subview) {
        const router = window.VoltUiReorg;
        if (router && typeof router.activateView === 'function') {
            router.activateView(view, true);
            if (subview && typeof router.activateSubview === 'function') {
                router.activateSubview(view, subview);
            }
            return true;
        }
        return click(document.querySelector('#nav-list a[data-view="power"]'));
    }

    function openTips() {
        if (window.__tips && typeof window.__tips.open === 'function') {
            window.__tips.open();
            return;
        }
        click(document.getElementById('btn-energy-tips'));
    }

    async function runAction(action, trigger) {
        if (!action) return;
        if (trigger) trigger.disabled = true;
        try {
            switch (action.kind) {
                case 'switch-plan': {
                    const target = document.querySelector('#plan-control [data-plan="' + action.plan + '"]');
                    if (!click(target)) navigate('power-plans', 'active');
                    break;
                }
                case 'enable-auto-boost':
                    if (window.Host && Host.available) {
                        await Host.call('setPowerSourcePlanSwitch', { enabled: true });
                    }
                    break;
                case 'disable-keep-awake':
                    if (window.Host && Host.available) {
                        await Host.call('setKeepAwake', { enabled: false });
                    }
                    keepAwake = false;
                    break;
                case 'disable-gaming':
                    if (window.__voltGamingMode && window.__voltGamingMode.setEnabled) {
                        await window.__voltGamingMode.setEnabled(false);
                    }
                    break;
                case 'dim-brightness':
                    click(document.getElementById('vm-brightness-decrease'));
                    break;
                case 'clear-override':
                    if (!click(document.getElementById('btn-clear-manual-override')) &&
                        window.Host && Host.available) {
                        await Host.call('clearManualOverride');
                    }
                    break;
                case 'open-automations':
                    navigate('automations', 'rules');
                    break;
                case 'open-tips':
                    openTips();
                    break;
                default:
                    break;
            }
        } catch (error) {
            console.error('energy suggestion action failed', error);
            if (window.Host && typeof Host.fail === 'function') Host.fail(error);
        } finally {
            if (trigger) trigger.disabled = false;
            renderedSignature = '';
            render();
        }
    }

    function renderItem(item) {
        const article = document.createElement('article');
        article.className = 'vm-suggestion';
        article.setAttribute('data-suggestion-id', item.id);
        article.setAttribute('data-tone', item.tone);

        const icon = document.createElement('div');
        icon.className = 'vm-suggestion__icon';
        const glyph = document.createElement('span');
        glyph.className = 'material-symbols-outlined';
        glyph.textContent = item.icon;
        glyph.setAttribute('aria-hidden', 'true');
        icon.appendChild(glyph);

        const copy = document.createElement('div');
        copy.className = 'vm-suggestion__copy';
        const meta = document.createElement('span');
        meta.className = 'vm-suggestion__meta';
        meta.textContent = t(item.tone === 'critical' ? 'priority_now'
            : item.context ? 'priority_recommended' : 'priority_general');
        const title = document.createElement('h4');
        title.className = 'vm-suggestion__title';
        title.textContent = item.title;
        const body = document.createElement('p');
        body.className = 'vm-suggestion__body';
        body.textContent = item.body;
        copy.appendChild(meta);
        copy.appendChild(title);
        copy.appendChild(body);

        const action = document.createElement('button');
        action.type = 'button';
        action.className = 'vm-suggestion__action';
        action.setAttribute('data-suggestion-action', item.id);
        action.textContent = item.cta;

        article.appendChild(icon);
        article.appendChild(copy);
        article.appendChild(action);
        return article;
    }

    function mount() {
        if (mounted && card) return true;
        const grid = document.querySelector('#view-overview .vm-overview-grid');
        const view = document.getElementById('view-overview');
        if (!view || !grid) return false;

        card = document.createElement('section');
        card.id = 'vm-energy-suggestions';
        card.className = 'vm-ui-panel vm-section-card vm-suggestions-card';
        card.setAttribute('aria-labelledby', 'vm-suggestions-title');

        const header = document.createElement('div');
        header.className = 'vm-section-title-row';
        const title = document.createElement('h3');
        title.className = 'vm-section-title';
        title.id = 'vm-suggestions-title';
        const icon = document.createElement('span');
        icon.className = 'material-symbols-outlined';
        icon.textContent = 'tips_and_updates';
        icon.setAttribute('aria-hidden', 'true');
        headingEl = document.createElement('span');
        headingEl.textContent = t('card_title');
        title.appendChild(icon);
        title.appendChild(headingEl);

        openBtn = document.createElement('button');
        openBtn.type = 'button';
        openBtn.className = 'btn-ghost vm-ui-control vm-section-action';
        openBtn.id = 'vm-suggestions-open';
        openBtn.setAttribute('data-vm-suggestions-open', '');
        openBtn.textContent = t('view_all');
        header.appendChild(title);
        header.appendChild(openBtn);

        listEl = document.createElement('div');
        listEl.className = 'vm-suggestions-list';
        listEl.id = 'vm-suggestions-list';

        footEl = document.createElement('p');
        footEl.className = 'vm-suggestions-foot';
        footEl.textContent = t('card_subtitle');

        card.appendChild(header);
        card.appendChild(listEl);
        card.appendChild(footEl);
        card.addEventListener('click', onCardClick);
        grid.appendChild(card);
        mounted = true;
        return true;
    }

    function render() {
        if (!mount()) return;
        const state = snapshot();
        const suggestions = evaluate(state);
        const visible = suggestions.slice(0, MAX_VISIBLE);
        const nextContextCount = suggestions.filter(item => item.context).length;
        const signature = JSON.stringify({
            lang: window.I18n && I18n.getLang ? I18n.getLang() : '',
            entries: visible.map(item => [item.id, item.title, item.body, item.cta]),
            contextCount: nextContextCount,
        });
        if (signature === renderedSignature) return;
        renderedSignature = signature;
        rendered = visible;
        contextCount = nextContextCount;

        headingEl.textContent = t('card_title');
        openBtn.textContent = t('view_all');
        footEl.textContent = t('card_subtitle');
        if (typeof listEl.replaceChildren === 'function') listEl.replaceChildren();
        else listEl.textContent = '';
        visible.forEach(item => listEl.appendChild(renderItem(item)));
        card.setAttribute('data-count', String(visible.length));
        updateBadge(contextCount);
    }

    function labelText() {
        if (contextCount > 0) return t('badge_title', { count: contextCount });
        return window.I18n && I18n.t ? I18n.t('tips_btn_title') : 'Energy tips';
    }

    function refreshLabel() {
        if (!buttonEl) return;
        const label = labelText();
        buttonEl.setAttribute('title', label);
        buttonEl.setAttribute('aria-label', label);
    }

    function updateBadge(count) {
        contextCount = count;
        if (badgeEl) {
            badgeEl.textContent = String(count);
            badgeEl.classList.toggle('hidden', count <= 0);
            badgeEl.setAttribute('aria-hidden', count > 0 ? 'false' : 'true');
        }
        refreshLabel();
    }

    function onCardClick(event) {
        const target = event && event.target;
        if (!target || typeof target.closest !== 'function') return;
        if (target.closest('[data-vm-suggestions-open]')) {
            event.preventDefault();
            openTips();
            return;
        }
        const trigger = target.closest('[data-suggestion-action]');
        if (!trigger) return;
        event.preventDefault();
        const item = rendered.find(entry => entry.id === trigger.getAttribute('data-suggestion-action'));
        if (item) runAction(item.action, trigger);
    }

    function refreshPowerState() {
        if (!window.Host || !Host.available) return;
        lastPowerRefresh = Date.now();
        Host.call('getPowerSourcePlanState').then(state => {
            if (!state) return;
            powerState = state;
            renderedSignature = '';
            render();
        }).catch(error => console.error('getPowerSourcePlanState failed for suggestions', error));
    }

    function maybeRefreshPowerState() {
        if (Date.now() - lastPowerRefresh < 60000) return;
        refreshPowerState();
    }

    function refreshKeepAwake() {
        if (!window.Host || !Host.available) return;
        Host.call('getKeepAwakeState').then(state => {
            keepAwake = !!(state && state.enabled);
            renderedSignature = '';
            render();
        }).catch(error => console.error('getKeepAwakeState failed for suggestions', error));
    }

    function wire() {
        if (wired) return;
        wired = true;
        buttonEl = document.getElementById('btn-energy-tips');
        badgeEl = document.getElementById('vm-tips-badge');
        refreshLabel();

        document.addEventListener('voltuiready', () => { if (mount()) render(); });
        document.addEventListener('viewchange', () => { if (mount()) render(); });
        document.addEventListener('voltuiviewchanged', () => { if (mount()) render(); });
        document.addEventListener('settingsloaded', () => { renderedSignature = ''; render(); });
        document.addEventListener('voltbatteryavailabilitychanged', () => {
            renderedSignature = '';
            refreshPowerState();
            render();
        });
        document.addEventListener('gamingmodechanged', () => { renderedSignature = ''; render(); });
        document.addEventListener('langchanged', () => { renderedSignature = ''; render(); });
        document.addEventListener('visibilitychange', () => { if (!document.hidden) refreshPowerState(); });

        if (window.Host && typeof Host.on === 'function') {
            Host.on('powerSourcePlanChanged', state => {
                if (state) powerState = state;
                renderedSignature = '';
                render();
            });
            Host.on('keepAwakeChanged', state => {
                keepAwake = !!(state && state.enabled);
                renderedSignature = '';
                render();
            });
            Host.on('metrics', () => {
                maybeRefreshPowerState();
                render();
            });
        }
    }

    function boot() {
        wire();
        refreshKeepAwake();
        refreshPowerState();
        render();
    }

    window.__energySuggestions = {
        evaluate,
        snapshot,
        refresh: () => { renderedSignature = ''; render(); },
        isMounted: () => mounted,
        getRenderedSuggestions: () => rendered.map(item => ({ ...item })),
        getPreferredTipIds,
        refreshLabel,
        openAll: openTips,
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', boot);
    } else {
        boot();
    }
})();
