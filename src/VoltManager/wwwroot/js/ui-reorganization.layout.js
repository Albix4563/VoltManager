/**
 * Static view and navigation layout for the VoltManager UI reorganization.
 */
(function () {
    'use strict';

    const api = window.VoltUiReorg = window.VoltUiReorg || {};

    api.lang = function () {
        return window.I18n && I18n.getLang ? I18n.getLang() : 'it';
    };

    api.t = function (key, params) {
        const strings = window.VoltUiReorgStrings || {};
        const lang = api.lang();
        let value = (strings[lang] && strings[lang][key]) ||
            (strings.en && strings.en[key]) || key;
        Object.entries(params || {}).forEach(([name, replacement]) => {
            value = value.replaceAll('{' + name + '}', String(replacement));
        });
        return value;
    };

    api.el = id => document.getElementById(id);

    function statusCard(icon, label, id) {
        return `<div class="vm-status-card">
            <div class="vm-status-card__header">
                <span class="material-symbols-outlined">${icon}</span>
                <span class="vm-status-card__label" data-vm-i18n="${label}"></span>
            </div>
            <strong id="${id}">--</strong>
        </div>`;
    }

    function quick(icon, label, action) {
        return `<button type="button" class="vm-quick-action" data-vm-action="${action}">
            <span class="material-symbols-outlined">${icon}</span><span data-vm-i18n="${label}"></span>
        </button>`;
    }

    function metric(icon, label, id, meter) {
        return `<article class="vm-compact-metric">
            <div class="vm-compact-metric__top"><span class="material-symbols-outlined">${icon}</span><span data-vm-i18n="${label}"></span><strong id="${id}">--</strong></div>
            <div class="vm-meter"><span id="${meter}"></span></div>
        </article>`;
    }

    function summary(icon, label, id) {
        return `<div class="vm-summary-row"><span class="material-symbols-outlined">${icon}</span><span data-vm-i18n="${label}"></span><strong id="${id}">--</strong></div>`;
    }

    function subnav(group, items) {
        return `<div class="vm-subnav" role="tablist" data-vm-subnav="${group}">
            ${items.map((item, index) => `<button type="button" class="vm-subnav__item${index ? '' : ' active'}"
                data-vm-subnav-group="${group}" data-vm-subnav-target="${item.id}"
                role="tab" aria-selected="${index ? 'false' : 'true'}">
                <span class="material-symbols-outlined">${item.icon}</span><span data-vm-i18n="${item.label}"></span>
            </button>`).join('')}
        </div>`;
    }

    function panel(group, id, html, active) {
        return `<div class="vm-subview${active ? ' active' : ' hidden'}" data-vm-panel-group="${group}" data-vm-panel="${id}">${html}</div>`;
    }

    function view(id, title, subtitle, html) {
        const section = document.createElement('section');
        section.id = 'view-' + id;
        section.className = 'view vm-reorg-view flex-1 flex-col hidden';
        section.dataset.vmView = id;
        section.innerHTML = `<div class="vm-view-heading"><div>
            <h2 class="text-headline-lg text-on-surface" data-vm-i18n="${title}"></h2>
            <p class="text-body-lg text-on-surface-variant mt-xs" data-vm-i18n="${subtitle}"></p>
        </div></div>${html}`;
        return section;
    }

    function overview() {
        return view('overview', 'overview_title', 'overview_subtitle', `
            <div class="vm-overview-grid">
                <section class="glass-panel rounded-xl p-lg vm-section-card">
                    <h3 class="vm-section-title"><span class="material-symbols-outlined">info</span><span data-vm-i18n="overview_current_state"></span></h3>
                    <div class="vm-status-grid">
                        ${statusCard('electric_bolt', 'status_plan', 'ov-status-plan')}
                        ${statusCard('power', 'status_power', 'ov-status-power')}
                        ${statusCard('monitor_heart', 'status_monitoring', 'ov-status-monitoring')}
                        ${statusCard('lock_clock', 'status_override', 'ov-status-override')}
                        ${statusCard('automation', 'status_automation', 'ov-status-automation')}
                    </div>
                </section>
                <section class="glass-panel rounded-xl p-lg vm-section-card">
                    <h3 class="vm-section-title"><span class="material-symbols-outlined">bolt</span><span data-vm-i18n="overview_quick_controls"></span></h3>
                    <div class="vm-quick-grid">
                        ${quick('battery_saver', 'quick_saver', 'saver')}
                        ${quick('balance', 'quick_balanced', 'balanced')}
                        ${quick('speed', 'quick_performance', 'performance')}
                        ${quick('autorenew', 'quick_automatic', 'automatic')}
                        ${quick('sports_esports', 'quick_gaming', 'gaming')}
                        ${quick('bedtime_off', 'quick_keep_awake', 'keep-awake')}
                    </div>
                </section>
            </div>
            <section class="glass-panel rounded-xl p-lg vm-section-card">
                <div class="vm-section-title-row">
                    <h3 class="vm-section-title"><span class="material-symbols-outlined">memory</span><span data-vm-i18n="overview_metrics"></span></h3>
                    <button type="button" class="btn-ghost rounded-lg py-2 px-3 text-label-md" data-vm-go="monitoring"><span data-vm-i18n="open_monitoring"></span></button>
                </div>
                <div class="vm-compact-metrics">
                    ${metric('developer_board', 'metric_cpu', 'ov-metric-cpu', 'ov-meter-cpu')}
                    ${metric('dns', 'metric_gpu', 'ov-metric-gpu', 'ov-meter-gpu')}
                    ${metric('memory_alt', 'metric_ram', 'ov-metric-ram', 'ov-meter-ram')}
                    ${metric('hard_drive', 'metric_disk', 'ov-metric-disk', 'ov-meter-disk')}
                </div>
            </section>
            <section class="glass-panel rounded-xl p-lg vm-section-card">
                <div class="vm-section-title-row">
                    <h3 class="vm-section-title"><span class="material-symbols-outlined">automation</span><span data-vm-i18n="overview_automation"></span></h3>
                    <button type="button" class="btn-ghost rounded-lg py-2 px-3 text-label-md" data-vm-go="automations"><span data-vm-i18n="open_automations"></span></button>
                </div>
                <div class="vm-automation-summary">
                    ${summary('tune', 'auto_cpu_rules', 'ov-auto-rules')}
                    ${summary('app_shortcut', 'auto_app_profile', 'ov-auto-profile')}
                    ${summary('sports_esports', 'auto_gaming', 'ov-auto-gaming')}
                    ${summary('schedule', 'auto_scheduled', 'ov-auto-scheduled')}
                </div>
            </section>`);
    }

    function monitoring() {
        return view('monitoring', 'monitoring_title', 'monitoring_subtitle',
            subnav('monitoring', [
                { id: 'hardware', icon: 'memory', label: 'tab_hardware' },
                { id: 'processes', icon: 'process_chart', label: 'tab_processes' },
                { id: 'temperatures', icon: 'device_thermostat', label: 'tab_temperatures' },
                { id: 'battery', icon: 'battery_horiz_075', label: 'tab_battery' }
            ]) + `<div class="vm-subview-stack">
                ${panel('monitoring', 'hardware', '<div id="vm-monitoring-hardware"></div>', true)}
                ${panel('monitoring', 'processes', '<div id="vm-monitoring-processes"></div>', false)}
                ${panel('monitoring', 'temperatures', '<div id="vm-monitoring-temperatures"></div>', false)}
                ${panel('monitoring', 'battery', '<div id="vm-monitoring-battery" class="vm-stack"></div>', false)}
            </div>`);
    }

    function powerPlans() {
        return view('power-plans', 'power_title', 'power_subtitle',
            subnav('power-plans', [
                { id: 'active', icon: 'bolt', label: 'tab_active_plan' },
                { id: 'source', icon: 'power', label: 'tab_power_source' },
                { id: 'advanced', icon: 'tune', label: 'tab_advanced' }
            ]) + `<div class="vm-subview-stack">
                ${panel('power-plans', 'active', `<section class="glass-panel rounded-xl p-lg vm-section-card">
                    <p class="text-body-md text-on-surface-variant mb-md" data-vm-i18n="active_plan_hint"></p><div id="vm-power-active"></div>
                </section>`, true)}
                ${panel('power-plans', 'source', `<section class="glass-panel rounded-xl p-lg vm-section-card">
                    <p class="text-body-md text-on-surface-variant mb-md" data-vm-i18n="power_source_hint"></p>
                    <div id="power-timeouts-mount"></div><div class="vm-divider"></div>
                    <div id="vm-power-source" class="vm-stack"></div><div class="vm-divider"></div>
                    <h3 class="vm-section-title"><span class="material-symbols-outlined">bedtime_off</span><span data-vm-i18n="keep_awake_primary"></span></h3>
                    <div id="vm-keep-awake"></div>
                </section>`, false)}
                ${panel('power-plans', 'advanced', `<section class="glass-panel rounded-xl p-lg vm-section-card">
                    <div class="vm-advanced-warning"><span class="material-symbols-outlined">info</span><span data-vm-i18n="advanced_hint"></span></div>
                    <div id="vm-power-advanced"></div>
                </section>`, false)}
            </div>`);
    }

    function automations() {
        return view('automations', 'automations_title', 'automations_subtitle',
            subnav('automations', [
                { id: 'rules', icon: 'tune', label: 'tab_cpu_rules' },
                { id: 'profiles', icon: 'app_shortcut', label: 'tab_app_profiles' },
                { id: 'gaming', icon: 'sports_esports', label: 'tab_gaming' }
            ]) + `<div class="vm-subview-stack">
                ${panel('automations', 'rules', `<div class="vm-rules-summary glass-panel rounded-xl p-md mb-md">
                    <span class="material-symbols-outlined">rule</span><span data-vm-i18n="rules_summary"></span><strong id="vm-rules-count">--</strong>
                </div><div id="vm-automation-rules"></div>`, true)}
                ${panel('automations', 'profiles', '<div id="vm-automation-profiles"></div>', false)}
                ${panel('automations', 'gaming', '<div id="vm-automation-gaming" class="vm-stack"></div>', false)}
            </div>`);
    }

    function systemTools() {
        return view('system-tools', 'system_title', 'system_subtitle',
            subnav('system-tools', [
                { id: 'scheduled', icon: 'schedule', label: 'tab_scheduled' },
                { id: 'startup', icon: 'rocket_launch', label: 'tab_startup' },
                { id: 'memory', icon: 'memory', label: 'tab_memory' }
            ]) + `<div class="vm-subview-stack">
                ${panel('system-tools', 'scheduled', '<div id="vm-system-scheduled"></div>', true)}
                ${panel('system-tools', 'startup', '<div id="vm-system-startup"></div>', false)}
                ${panel('system-tools', 'memory', '<div id="vm-system-memory"></div>', false)}
            </div>`);
    }

    function widgets() {
        return view('widgets', 'widgets_title', 'widgets_subtitle', `
            <div class="vm-widget-filters" role="tablist">
                <button type="button" class="vm-widget-filter active" data-widget-filter="all" aria-selected="true"><span data-vm-i18n="filter_all"></span></button>
                <button type="button" class="vm-widget-filter" data-widget-filter="active" aria-selected="false"><span data-vm-i18n="filter_active"></span></button>
                <button type="button" class="vm-widget-filter" data-widget-filter="disabled" aria-selected="false"><span data-vm-i18n="filter_disabled"></span></button>
            </div><div id="vm-widgets-content"></div>`);
    }

    function settings() {
        return view('settings', 'settings_title', 'settings_subtitle',
            subnav('settings', [
                { id: 'general', icon: 'settings', label: 'tab_general' },
                { id: 'appearance', icon: 'palette', label: 'tab_appearance' },
                { id: 'updates', icon: 'system_update', label: 'tab_updates' },
                { id: 'info', icon: 'info', label: 'tab_info' }
            ]) + `<div class="vm-subview-stack">
                ${panel('settings', 'general', `<section class="glass-panel rounded-xl p-lg vm-section-card">
                    <p class="text-body-md text-on-surface-variant mb-md" data-vm-i18n="settings_general_hint"></p><div id="vm-settings-general" class="vm-settings-list"></div>
                </section>`, true)}
                ${panel('settings', 'appearance', `<section class="glass-panel rounded-xl p-lg vm-section-card">
                    <p class="text-body-md text-on-surface-variant mb-md" data-vm-i18n="settings_appearance_hint"></p><div id="vm-settings-appearance" class="vm-settings-list"></div>
                </section>`, false)}
                ${panel('settings', 'updates', `<div id="vm-settings-updates" class="vm-stack"></div>
                    <section class="glass-panel rounded-xl p-lg vm-section-card"><div class="vm-section-title-row">
                    <h3 class="vm-section-title"><span class="material-symbols-outlined">history</span><span data-vm-i18n="changelog_panel"></span></h3>
                    <div id="vm-changelog-actions"></div></div><div id="vm-settings-changelog" class="vm-stack"></div></section>`, false)}
                ${panel('settings', 'info', '<div id="vm-settings-info"></div>', false)}
            </div>`);
    }

    api.installViews = function () {
        const main = api.el('main-content');
        if (!main || api.el('view-overview')) return;
        const first = main.querySelector('.view');
        const fragment = document.createDocumentFragment();
        [overview(), monitoring(), powerPlans(), automations(), systemTools(), widgets(), settings()]
            .forEach(section => fragment.appendChild(section));
        if (first) main.insertBefore(fragment, first);
        else main.appendChild(fragment);
        main.querySelectorAll('.view:not(.vm-reorg-view)').forEach(section => {
            section.classList.add('hidden', 'vm-legacy-view');
            section.setAttribute('aria-hidden', 'true');
        });
    };

    api.installSidebar = function () {
        const nav = api.el('nav-list');
        if (!nav) return;
        const item = (id, icon, label) => `<li><a class="nav-item flex items-center gap-3 text-on-surface-variant font-medium px-4 py-3 opacity-80 hover:bg-white/5 hover:text-secondary-fixed transition-all duration-300 rounded-lg active:scale-[0.98]"
            data-view="${id}" href="#${id}"><span class="material-symbols-outlined">${icon}</span><span class="text-body-md" data-vm-i18n="${label}"></span></a></li>`;
        nav.innerHTML = `
            <li class="nav-section-label is-first" aria-hidden="true" data-vm-i18n="nav_main"></li>
            ${item('overview', 'dashboard', 'nav_overview')}${item('monitoring', 'monitoring', 'nav_monitoring')}
            <li class="nav-section-label" aria-hidden="true" data-vm-i18n="nav_energy"></li>
            ${item('power-plans', 'bolt', 'nav_power_plans')}${item('automations', 'automation', 'nav_automations')}
            <li class="nav-section-label" aria-hidden="true" data-vm-i18n="nav_system"></li>
            ${item('system-tools', 'construction', 'nav_system_tools')}${item('widgets', 'widgets', 'nav_widgets')}
            <li class="nav-section-label" aria-hidden="true" data-vm-i18n="nav_app"></li>
            ${item('settings', 'settings', 'nav_settings')}
            <li class="hidden" aria-hidden="true"><a data-view="system" href="#"></a></li>`;
    };

    api.installTopStatus = function () {
        const header = document.querySelector('#app-main > header');
        if (!header || api.el('vm-top-status')) return;
        header.classList.add('vm-topbar');
        const actions = header.querySelector('.ml-auto');
        const strip = document.createElement('div');
        strip.id = 'vm-top-status';
        strip.className = 'vm-top-status';
        strip.innerHTML = `
            <div class="vm-top-chip"><span class="material-symbols-outlined">bolt</span><span data-vm-i18n="top_plan"></span><strong id="vm-top-plan">--</strong></div>
            <div class="vm-top-chip" id="vm-top-battery-chip"><span class="material-symbols-outlined">battery_horiz_075</span><span data-vm-i18n="top_battery"></span><strong id="vm-top-battery">--</strong></div>
            <div class="vm-top-chip"><span class="material-symbols-outlined">automation</span><span data-vm-i18n="top_automation"></span><strong id="vm-top-automation">--</strong></div>`;
        if (actions) header.insertBefore(strip, actions);
        else header.appendChild(strip);
    };

    api.applyTranslations = function () {
        document.querySelectorAll('[data-vm-i18n]').forEach(node => {
            node.textContent = api.t(node.dataset.vmI18n);
        });
    };

    api.positionIndicator = function (link) {
        const indicator = api.el('nav-indicator');
        if (!indicator || !link) return;
        const parent = indicator.offsetParent || indicator.parentElement;
        if (!parent) return;
        const parentRect = parent.getBoundingClientRect();
        const linkRect = link.getBoundingClientRect();
        indicator.style.top = (linkRect.top - parentRect.top) + 'px';
        indicator.style.height = linkRect.height + 'px';
    };
})();
