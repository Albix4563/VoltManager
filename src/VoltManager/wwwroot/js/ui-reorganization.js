/**
 * Router, view relocation and interaction wiring for the VoltManager UI.
 */
(function () {
    'use strict';

    const api = window.VoltUiReorg = window.VoltUiReorg || {};
    const $ = id => document.getElementById(id);
    const state = api.state = {
        view: 'overview',
        subviews: {
            monitoring: 'hardware',
            'power-plans': 'active',
            automations: 'rules',
            'system-tools': 'scheduled',
            settings: 'general'
        },
        widgetFilter: 'all',
        hasBattery: null
    };
    const legacy = {
        overview: 'home',
        monitoring: 'home',
        'power-plans': 'power',
        automations: 'power',
        'system-tools': 'system',
        widgets: 'widgets',
        settings: 'settings'
    };

    function hidden(node) {
        return !node || node.classList.contains('hidden') ||
            node.getAttribute('aria-hidden') === 'true' ||
            getComputedStyle(node).display === 'none';
    }

    function move(node, target, preserveVisibility) {
        if (!node || !target) return false;
        const wasHidden = node.classList.contains('hidden');
        const aria = node.getAttribute('aria-hidden');
        target.appendChild(node);
        if (!preserveVisibility) {
            node.classList.remove('hidden');
            node.removeAttribute('aria-hidden');
        } else {
            node.classList.toggle('hidden', wasHidden);
            if (aria == null) node.removeAttribute('aria-hidden');
            else node.setAttribute('aria-hidden', aria);
        }
        return true;
    }

    function click(node) {
        if (!node) return;
        node.dispatchEvent(new MouseEvent('click', {
            bubbles: true,
            cancelable: true,
            view: window
        }));
    }

    function setBatteryOnlyVisible(node, visible) {
        if (!node) return;
        node.classList.toggle('hidden', !visible);
        node.style.display = visible ? '' : 'none';
        node.setAttribute('aria-hidden', visible ? 'false' : 'true');
        if (node.matches('button, a, input, select, textarea, [role="tab"], [tabindex]')) {
            node.tabIndex = visible ? 0 : -1;
        }
    }

    function hasReadableBatteryPercent() {
        const value = $('power-flow-percent')?.textContent.trim() || '';
        return value !== '' && value !== '--' && value !== '--%';
    }

    function syncBatteryCapabilityUi() {
        const hasBattery = state.hasBattery === true;
        const batteryTab = document.querySelector(
            '[data-vm-subnav-group="monitoring"][data-vm-subnav-target="battery"]');
        setBatteryOnlyVisible(batteryTab, hasBattery);
        setBatteryOnlyVisible($('pref-power-source-plan-home'), hasBattery);

        if (!hasBattery && state.subviews.monitoring === 'battery') {
            api.activateSubview('monitoring', 'hardware');
        }

        const batteryChip = $('vm-top-battery-chip');
        const showBatteryChip = hasBattery && hasReadableBatteryPercent();
        setBatteryOnlyVisible(batteryChip, showBatteryChip);
    }

    function applySystemInfo(info) {
        if (!info || typeof info.hasBattery !== 'boolean') return;
        state.hasBattery = info.hasBattery;
        syncBatteryCapabilityUi();
        document.dispatchEvent(new CustomEvent('voltbatteryavailabilitychanged', {
            detail: { hasBattery: state.hasBattery }
        }));
    }

    function installBatteryCapabilityDetection() {
        // Battery-only controls default to hidden until the hardware capability
        // is explicitly confirmed. This prevents desktop-only systems from
        // briefly exposing stale battery UI during startup.
        syncBatteryCapabilityUi();
        if (window.VoltSystemInfo) applySystemInfo(window.VoltSystemInfo);

        document.addEventListener('systeminfoloaded', event => {
            applySystemInfo(event.detail);
        });

        const percent = $('power-flow-percent');
        if (percent) {
            new MutationObserver(syncBatteryCapabilityUi).observe(percent, {
                childList: true,
                subtree: true,
                characterData: true
            });
        }

        if (state.hasBattery == null && window.Host?.available) {
            Host.call('getSystemInfo').then(info => {
                if (!window.VoltSystemInfo) window.VoltSystemInfo = info;
                applySystemInfo(info);
            }).catch(error =>
                console.error('getSystemInfo failed while resolving battery capability', error));
        } else {
            syncBatteryCapabilityUi();
        }
    }

    function activateLegacyPowerPanel(name) {
        const segment = document.querySelector(`#view-power .pm-seg[data-pm="${name}"]`);
        if (segment) setTimeout(() => click(segment), 0);
    }

    function relocate() {
        move($('dash-taskmanager'), $('vm-monitoring-hardware'));
        move($('processes-section'), $('vm-monitoring-processes'));
        move($('temp-section'), $('vm-monitoring-temperatures'), true);
        ['battery-health-section', 'power-flow-section', 'battery-history-section']
            .forEach(id => move($(id), $('vm-monitoring-battery'), true));

        const plan = $('plan-control');
        const oldPlanCard = plan && plan.closest('section');
        move(plan, $('vm-power-active'));
        move($('manual-override-chip'), $('vm-power-active'), true);
        move($('active-plan-reason'), $('vm-power-active'), true);
        move($('pref-power-source-plan-home'), $('vm-power-source'), true);
        move($('pref-low-battery-threshold-home'), $('vm-power-source'), true);
        move($('keep-awake-mount'), $('vm-keep-awake'));
        move($('advanced-params-mount'), $('vm-power-advanced'));

        const ruleItem = document.querySelector('.vm-acc-item[data-pm="rules"]');
        const ruleBody = ruleItem && ruleItem.querySelector('.vm-acc-body-inner');
        move(ruleBody || ruleItem, $('vm-automation-rules'));
        move($('app-power-profile-mount'), $('vm-automation-profiles'));
        move($('heavy-app-mount'), $('vm-automation-gaming'));
        move($('pref-gaming-mode-home'), $('vm-automation-gaming'));

        move($('schedule-panel'), $('vm-system-scheduled'));
        const startupButton = $('btn-refresh-startup-apps');
        move(startupButton && startupButton.closest('.glass-panel'), $('vm-system-startup'));
        move($('ram-cleaner-mount'), $('vm-system-memory'));

        move($('widgets-card'), $('vm-widgets-content'));

        const updateButton = $('btn-check-updates');
        move(updateButton && updateButton.closest('.glass-panel'), $('vm-settings-updates'));
        ['pref-autostart', 'pref-tray', 'pref-show-welcome', 'pref-show-tour', 'pref-backup', 'pref-global-hotkeys']
            .forEach(id => move($(id), $('vm-settings-general')));
        ['pref-theme', 'pref-lang', 'pref-font', 'font-specimen-preview']
            .forEach(id => move($(id), $('vm-settings-appearance')));
        const info = $('info-version');
        move(info && info.closest('.glass-panel'), $('vm-settings-info'));

        move($('btn-refresh-changelog'), $('vm-changelog-actions'));
        move($('changelog-status'), $('vm-settings-changelog'), true);
        move($('changelog-list'), $('vm-settings-changelog'));

        if (oldPlanCard && !oldPlanCard.children.length) oldPlanCard.remove();
        // Drop empty decorative leftovers from legacy shells (headings/glows
        // left behind after relocate). Keep any node that still hosts an id
        // used by power/advanced lazy mounts or activateLegacyPowerPanel.
        document.querySelectorAll('.vm-legacy-view').forEach(view => {
            view.querySelectorAll(':scope > :not([id]):not([data-pm])').forEach(child => {
                if (!child.querySelector('[id], [data-pm], .pm-seg, .vm-acc-item')) child.remove();
            });
        });
        cleanup();
        configureWidgets();
    }

    function cleanup() {
        ['dash-taskmanager', 'processes-section', 'temp-section',
            'battery-health-section', 'power-flow-section', 'battery-history-section']
            .forEach(id => $(id)?.classList.remove('mt-8'));

        document.querySelectorAll('#vm-automation-rules .vm-acc-body, #vm-automation-rules .vm-acc-body-inner')
            .forEach(node => {
                node.style.maxHeight = 'none';
                node.style.opacity = '1';
                node.style.overflow = 'visible';
            });

        document.querySelectorAll('#vm-settings-general > *, #vm-settings-appearance > *')
            .forEach(node => {
                node.classList.remove('mt-md');
                node.classList.add('vm-settings-row');
            });
    }

    function dispatchView(name) {
        document.dispatchEvent(new CustomEvent('viewchange', {
            detail: { view: name, reorganized: true }
        }));
    }

    api.activateView = function (name, updateHash) {
        const target = $('view-' + name);
        if (!target) return;
        state.view = name;

        const finish = () => {
            document.querySelectorAll('.vm-reorg-view').forEach(view => {
                const active = view === target;
                view.classList.toggle('hidden', !active);
                view.classList.toggle('flex', active);
                view.setAttribute('aria-hidden', active ? 'false' : 'true');
            });
            document.querySelectorAll('.vm-legacy-view').forEach(view => {
                view.classList.add('hidden');
                view.classList.remove('flex');
                view.setAttribute('aria-hidden', 'true');
            });

            document.querySelectorAll('#nav-list .nav-item[data-view]').forEach(link => {
                const active = link.dataset.view === name;
                link.classList.toggle('text-secondary-container', active);
                link.classList.toggle('font-bold', active);
                link.classList.toggle('bg-surface-container-high/50', active);
                link.classList.toggle('text-on-surface-variant', !active);
                link.classList.toggle('font-medium', !active);
                link.classList.toggle('opacity-80', !active);
                link.querySelector('.material-symbols-outlined')?.classList.toggle('icon-fill', active);
                if (active) api.positionIndicator?.(link);
            });

            $('main-content').scrollTop = 0;
            if (updateHash) {
                try { history.replaceState(null, '', '#' + name); }
                catch (_) { location.hash = name; }
            }
            dispatchView(name);
            if (legacy[name]) setTimeout(() => dispatchView(legacy[name]), 0);
            if (name === 'settings' && state.subviews.settings === 'updates') loadChangelog();
            if (name === 'power-plans' && state.subviews['power-plans'] === 'advanced') {
                activateLegacyPowerPanel('advanced');
            }
            if (name === 'system-tools' && state.subviews['system-tools'] === 'memory') {
                activateLegacyPowerPanel('ram');
            }
            document.dispatchEvent(new CustomEvent('voltuiviewchanged', { detail: { view: name } }));
        };

        // power.js/advanced.js are deferred from cold start — pull them before
        // painting power-related reorg views so panels wire correctly.
        const needPower = name === 'power-plans' || name === 'automations'
            || name === 'system-tools' || name === 'settings';
        if (needPower && typeof window.__voltEnsurePowerScripts === 'function') {
            window.__voltEnsurePowerScripts().then(finish).catch(finish);
            return;
        }
        finish();
    };

    api.activateSubview = function (group, name) {
        const previous = state.subviews[group];
        state.subviews[group] = name;
        document.querySelectorAll(`[data-vm-subnav-group="${group}"]`).forEach(button => {
            const active = button.dataset.vmSubnavTarget === name;
            button.classList.toggle('active', active);
            button.setAttribute('aria-selected', active ? 'true' : 'false');
        });
        document.querySelectorAll(`[data-vm-panel-group="${group}"]`).forEach(panel => {
            const active = panel.dataset.vmPanel === name;
            panel.classList.toggle('hidden', !active);
            panel.classList.toggle('active', active);
        });
        if (group === 'settings' && name === 'updates') loadChangelog();
        if (group === 'system-tools' && name === 'startup') dispatchView('system');

        if (group === 'power-plans' && name === 'advanced') {
            activateLegacyPowerPanel('advanced');
        }
        if (group === 'system-tools') {
            if (name === 'memory') activateLegacyPowerPanel('ram');
            else if (previous === 'memory') activateLegacyPowerPanel('rules');
        }

        document.dispatchEvent(new CustomEvent('voltuisubviewchanged', {
            detail: { group, view: name }
        }));
    };

    function loadChangelog() {
        dispatchView('changelog');
    }

    function quickAction(action) {
        if (['saver', 'balanced', 'performance'].includes(action)) {
            const planName = action === 'saver' ? 'powerSaver' : action;
            click(document.querySelector(`#plan-control [data-plan="${planName}"]`));
            return;
        }

        if (action === 'automatic') {
            const chip = $('manual-override-chip');
            if ($('btn-clear-manual-override') && !hidden(chip)) {
                click($('btn-clear-manual-override'));
            } else {
                const master = $('master-toggle');
                if (master && !master.checked) {
                    master.checked = true;
                    master.dispatchEvent(new Event('change', { bubbles: true }));
                }
                if (window.Host && Host.available) {
                    Host.call('clearManualOverride').catch(error =>
                        console.error('clearManualOverride failed', error));
                }
            }
            return;
        }

        if (action === 'gaming') {
            const active = !!(window.__voltGamingMode?.isActive?.());
            if (window.__voltGamingMode?.setEnabled) {
                window.__voltGamingMode.setEnabled(!active).catch(error =>
                    console.error('setGamingMode failed', error));
            } else {
                click($('pref-gaming-mode-home'));
            }
            return;
        }

        if (action === 'keep-awake') {
            const mount = $('keep-awake-mount');
            click(mount && (mount.querySelector('input[type="checkbox"]') ||
                mount.querySelector('[role="switch"]') ||
                mount.querySelector('button') ||
                mount.querySelector('.mini-toggle')));
        }
    }

    function configureWidgets() {
        const groups = $('widgets-groups');
        if (!groups) return;
        groups.classList.add('vm-widget-groups');
        Array.from(groups.children).forEach((section, index) => {
            section.dataset.widgetState = index ? 'disabled' : 'active';
        });
        filterWidgets('all');
    }

    function filterWidgets(filter) {
        state.widgetFilter = filter;
        document.querySelectorAll('[data-widget-filter]').forEach(button => {
            const active = button.dataset.widgetFilter === filter;
            button.classList.toggle('active', active);
            button.setAttribute('aria-selected', active ? 'true' : 'false');
        });
        document.querySelectorAll('#widgets-groups > section').forEach(section => {
            section.classList.toggle('hidden',
                filter !== 'all' && section.dataset.widgetState !== filter);
        });
    }

    function wireInteractions() {
        const nav = $('nav-list');
        nav?.addEventListener('click', event => {
            const link = event.target.closest('a[data-view]');
            if (!link || link.dataset.view === 'system') return;
            event.preventDefault();
            event.stopImmediatePropagation();
            api.activateView(link.dataset.view, true);
        }, true);

        document.addEventListener('click', event => {
            const sub = event.target.closest('[data-vm-subnav-target]');
            if (sub) {
                event.preventDefault();
                api.activateSubview(sub.dataset.vmSubnavGroup, sub.dataset.vmSubnavTarget);
                return;
            }
            const go = event.target.closest('[data-vm-go]');
            if (go) {
                event.preventDefault();
                api.activateView(go.dataset.vmGo, true);
                return;
            }
            const action = event.target.closest('[data-vm-action]');
            if (action) {
                event.preventDefault();
                quickAction(action.dataset.vmAction);
                return;
            }
            const filter = event.target.closest('[data-widget-filter]');
            if (filter) {
                event.preventDefault();
                filterWidgets(filter.dataset.widgetFilter);
            }
        });
    }

    function suppressLegacySystemItem() {
        const nav = $('nav-list');
        if (!nav) return;
        new MutationObserver(() => {
            nav.querySelectorAll('a[data-view="system"]').forEach(link => {
                const item = link.closest('li');
                if (item && !item.classList.contains('hidden')) item.remove();
            });
        }).observe(nav, { childList: true, subtree: true });
    }

    function applyLanguage() {
        api.applyTranslations?.();
        document.dispatchEvent(new CustomEvent('voltuistranslated'));
    }

    function initialView() {
        const hash = location.hash.replace(/^#/, '');
        return $('view-' + hash) ? hash : 'overview';
    }

    function boot() {
        if (api.ready) return;
        api.ready = true;
        api.installViews?.();
        relocate();
        api.installSidebar?.();
        api.installTopStatus?.();
        wireInteractions();
        suppressLegacySystemItem();
        applyLanguage();
        installBatteryCapabilityDetection();

        document.addEventListener('langchanged', applyLanguage);
        window.addEventListener('resize', () => {
            api.positionIndicator?.(
                document.querySelector(`#nav-list .nav-item[data-view="${state.view}"]`)
            );
        });

        api.activateView(initialView(), false);
        Object.entries(state.subviews).forEach(([group, name]) =>
            api.activateSubview(group, name));
        document.dispatchEvent(new CustomEvent('voltuiready'));
    }

    if (document.readyState === 'complete') boot();
    else window.addEventListener('load', boot, { once: true });
})();
