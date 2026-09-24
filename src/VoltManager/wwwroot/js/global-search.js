/**
 * Global navigation search for VoltManager.
 * Indexes stable application routes and controls without executing actions.
 */
(function () {
    'use strict';

    const api = window.VoltGlobalSearch = window.VoltGlobalSearch || {};
    const MAX_RESULTS = 8;
    const HIGHLIGHT_MS = 1800;
    const TARGET_WAIT_MS = 3000;

    const catalog = [
        { id: 'overview', labelKey: 'overview_title', descriptionKey: 'search_desc_overview', keywords: ['search_kw_home', 'search_kw_dashboard'], icon: 'dashboard', category: 'overview', view: 'overview', subview: null, targetId: null },
        { id: 'monitoring', labelKey: 'monitoring_title', descriptionKey: 'search_desc_monitoring', keywords: ['search_kw_monitor', 'search_kw_hardware'], icon: 'monitoring', category: 'monitoring', view: 'monitoring', subview: 'hardware', targetId: 'vm-monitoring-hardware' },
        { id: 'power-plans', labelKey: 'power_title', descriptionKey: 'search_desc_power', keywords: ['search_kw_power', 'search_kw_energy'], icon: 'bolt', category: 'power', view: 'power-plans', subview: 'active', targetId: 'vm-power-active' },
        { id: 'automations', labelKey: 'automations_title', descriptionKey: 'search_desc_automations', keywords: ['search_kw_automation', 'search_kw_rules'], icon: 'automation', category: 'automations', view: 'automations', subview: 'rules', targetId: 'vm-automation-rules' },
        { id: 'system-tools', labelKey: 'system_title', descriptionKey: 'search_desc_system', keywords: ['search_kw_system', 'search_kw_tools'], icon: 'construction', category: 'system', view: 'system-tools', subview: 'scheduled', targetId: 'vm-system-scheduled' },
        { id: 'remote-control', labelKey: 'remote_title', descriptionKey: 'search_desc_remote', keywords: ['search_kw_remote', 'search_kw_lan', 'search_kw_system'], icon: 'devices', category: 'system', view: 'remote-control', subview: null, targetId: null },
        { id: 'widgets', labelKey: 'widgets_title', descriptionKey: 'search_desc_widgets', keywords: ['search_kw_widgets', 'search_kw_desktop'], icon: 'widgets', category: 'widgets', view: 'widgets', subview: null, targetId: 'vm-widgets-content' },
        { id: 'settings', labelKey: 'settings_title', descriptionKey: 'search_desc_settings', keywords: ['search_kw_settings', 'search_kw_preferences'], icon: 'settings', category: 'settings', view: 'settings', subview: 'general', targetId: 'vm-settings-general' },

        { id: 'monitor-hardware', labelKey: 'tab_hardware', descriptionKey: 'search_desc_hardware', keywords: ['search_kw_cpu', 'search_kw_gpu', 'search_kw_ram', 'search_kw_disk'], icon: 'memory', category: 'monitoring', view: 'monitoring', subview: 'hardware', targetId: 'vm-monitoring-hardware' },
        { id: 'monitor-processes', labelKey: 'tab_processes', descriptionKey: 'search_desc_processes', keywords: ['search_kw_processes', 'search_kw_tasks'], icon: 'process_chart', category: 'monitoring', view: 'monitoring', subview: 'processes', targetId: 'vm-monitoring-processes' },
        { id: 'monitor-temperatures', labelKey: 'tab_temperatures', descriptionKey: 'search_desc_temperatures', keywords: ['search_kw_temperature', 'search_kw_thermal'], icon: 'device_thermostat', category: 'monitoring', view: 'monitoring', subview: 'temperatures', targetId: 'vm-monitoring-temperatures' },
        { id: 'monitor-battery', labelKey: 'tab_battery', descriptionKey: 'search_desc_battery', keywords: ['search_kw_battery', 'search_kw_health'], icon: 'battery_horiz_075', category: 'monitoring', view: 'monitoring', subview: 'battery', targetId: 'vm-monitoring-battery' },

        { id: 'power-active', labelKey: 'tab_active_plan', descriptionKey: 'search_desc_active_plan', keywords: ['search_kw_plan', 'search_kw_power'], icon: 'bolt', category: 'power', view: 'power-plans', subview: 'active', targetId: 'vm-power-active' },
        { id: 'power-source', labelKey: 'tab_power_source', descriptionKey: 'search_desc_power_source', keywords: ['search_kw_ac', 'search_kw_battery', 'search_kw_source'], icon: 'power', category: 'power', view: 'power-plans', subview: 'source', targetId: 'vm-power-source' },
        { id: 'power-timeouts', labelKey: 'power_timeout_title', descriptionKey: 'search_desc_timeouts', keywords: ['search_kw_sleep', 'search_kw_screen', 'search_kw_timeout'], icon: 'bedtime', category: 'power', view: 'power-plans', subview: 'source', targetId: 'power-timeouts-mount' },
        { id: 'power-keep-awake', labelKey: 'tab_keep_awake', descriptionKey: 'search_desc_keep_awake', keywords: ['search_kw_awake', 'search_kw_sleep'], icon: 'bedtime_off', category: 'power', view: 'power-plans', subview: 'keep-awake', targetId: 'vm-keep-awake' },
        { id: 'power-history', labelKey: 'tab_plan_history', descriptionKey: 'search_desc_history', keywords: ['search_kw_history', 'search_kw_plan'], icon: 'history', category: 'power', view: 'power-plans', subview: 'history', targetId: 'vm-panel-power-plans-history' },
        { id: 'power-advanced', labelKey: 'tab_advanced', descriptionKey: 'search_desc_advanced', keywords: ['search_kw_advanced', 'search_kw_cpu', 'search_kw_pcie'], icon: 'tune', category: 'power', view: 'power-plans', subview: 'advanced', targetId: 'vm-panel-power-plans-advanced' },

        { id: 'automation-rules', labelKey: 'tab_cpu_rules', descriptionKey: 'search_desc_rules', keywords: ['search_kw_cpu', 'search_kw_rules', 'search_kw_threshold'], icon: 'tune', category: 'automations', view: 'automations', subview: 'rules', targetId: 'vm-automation-rules' },
        { id: 'automation-master', labelKey: 'power_master_title', descriptionKey: 'search_desc_master_automation', keywords: ['search_kw_automation', 'search_kw_background'], icon: 'autorenew', category: 'automations', view: 'automations', subview: 'rules', targetId: 'master-toggle' },
        { id: 'automation-profiles', labelKey: 'tab_app_profiles', descriptionKey: 'search_desc_profiles', keywords: ['search_kw_apps', 'search_kw_profiles', 'search_kw_plan'], icon: 'app_shortcut', category: 'automations', view: 'automations', subview: 'profiles', targetId: 'vm-automation-profiles' },
        { id: 'automation-gaming', labelKey: 'tab_gaming', descriptionKey: 'search_desc_gaming', keywords: ['search_kw_gaming', 'search_kw_heavy'], icon: 'sports_esports', category: 'automations', view: 'automations', subview: 'gaming', targetId: 'vm-automation-gaming' },
        { id: 'automation-gaming-mode', labelKey: 'dash_gaming_title', descriptionKey: 'search_desc_gaming_mode', keywords: ['search_kw_gaming', 'search_kw_performance'], icon: 'sports_esports', category: 'automations', view: 'automations', subview: 'gaming', targetId: 'pref-gaming-mode-home' },

        { id: 'system-scheduled', labelKey: 'tab_scheduled', descriptionKey: 'search_desc_scheduled', keywords: ['search_kw_schedule', 'search_kw_shutdown', 'search_kw_restart'], icon: 'schedule', category: 'system', view: 'system-tools', subview: 'scheduled', targetId: 'vm-system-scheduled' },
        { id: 'system-startup', labelKey: 'tab_startup', descriptionKey: 'search_desc_startup', keywords: ['search_kw_startup', 'search_kw_apps'], icon: 'rocket_launch', category: 'system', view: 'system-tools', subview: 'startup', targetId: 'vm-system-startup' },
        { id: 'system-memory', labelKey: 'tab_memory', descriptionKey: 'search_desc_memory', keywords: ['search_kw_ram', 'search_kw_memory', 'search_kw_cleaner'], icon: 'memory', category: 'system', view: 'system-tools', subview: 'memory', targetId: 'vm-system-memory' },

        { id: 'settings-general', labelKey: 'tab_general', descriptionKey: 'search_desc_general', keywords: ['search_kw_settings', 'search_kw_preferences'], icon: 'settings', category: 'settings', view: 'settings', subview: 'general', targetId: 'vm-settings-general' },
        { id: 'settings-autostart', labelKey: 'set_pref_autostart', descriptionKey: 'search_desc_autostart_setting', keywords: ['search_kw_startup', 'search_kw_windows'], icon: 'login', category: 'settings', view: 'settings', subview: 'general', targetId: 'pref-autostart' },
        { id: 'settings-appearance', labelKey: 'tab_appearance', descriptionKey: 'search_desc_appearance', keywords: ['search_kw_theme', 'search_kw_language', 'search_kw_font'], icon: 'palette', category: 'settings', view: 'settings', subview: 'appearance', targetId: 'vm-settings-appearance' },
        { id: 'settings-language', labelKey: 'set_pref_lang', descriptionKey: 'search_desc_language', keywords: ['search_kw_language', 'search_kw_locale'], icon: 'language', category: 'settings', view: 'settings', subview: 'appearance', targetId: 'pref-lang' },
        { id: 'settings-theme', labelKey: 'set_pref_theme', descriptionKey: 'search_desc_theme', keywords: ['search_kw_theme', 'search_kw_color'], icon: 'palette', category: 'settings', view: 'settings', subview: 'appearance', targetId: 'pref-theme' },
        { id: 'settings-font', labelKey: 'set_pref_font', descriptionKey: 'search_desc_font', keywords: ['search_kw_font', 'search_kw_text'], icon: 'text_fields', category: 'settings', view: 'settings', subview: 'appearance', targetId: 'pref-font' },
        { id: 'settings-maintenance', labelKey: 'tab_maintenance', descriptionKey: 'search_desc_maintenance', keywords: ['search_kw_backup', 'search_kw_diagnostics'], icon: 'build', category: 'settings', view: 'settings', subview: 'maintenance', targetId: 'vm-settings-maintenance' },
        { id: 'settings-updates', labelKey: 'tab_updates', descriptionKey: 'search_desc_updates', keywords: ['search_kw_update', 'search_kw_channel'], icon: 'system_update', category: 'settings', view: 'settings', subview: 'updates', targetId: 'vm-settings-updates' },
        { id: 'settings-info', labelKey: 'tab_info', descriptionKey: 'search_desc_info', keywords: ['search_kw_info', 'search_kw_version'], icon: 'info', category: 'settings', view: 'settings', subview: 'info', targetId: 'vm-settings-info' },
    ];

    let dialog = null;
    let input = null;
    let resultList = null;
    let selectedIndex = 0;
    let visibleResults = [];
    let previousFocus = null;
    let highlightTimer = 0;

    function normalize(value) {
        return String(value == null ? '' : value)
            .normalize('NFD')
            .replace(/[\u0300-\u036f]/g, '')
            .toLocaleLowerCase()
            .trim()
            .replace(/\s+/g, ' ');
    }

    function rankEntries(query, entries) {
        const q = normalize(query);
        const source = Array.isArray(entries) ? entries : [];
        if (!q) return source.slice(0, MAX_RESULTS);

        return source
            .map((entry, index) => {
                const label = normalize(entry.label);
                const description = normalize(entry.description);
                const keywords = (entry.keywords || []).map(normalize);
                const words = label.split(/\s+/);
                let score = Number.POSITIVE_INFINITY;

                if (label.startsWith(q)) score = 0;
                else if (words.some(word => word.startsWith(q))) score = 1;
                else if (label.includes(q)) score = 2;
                else if (keywords.some(keyword => keyword.includes(q))) score = 3;
                else if (description.includes(q)) score = 4;

                return { entry, index, score };
            })
            .filter(item => Number.isFinite(item.score))
            .sort((a, b) => a.score - b.score || a.index - b.index)
            .slice(0, MAX_RESULTS)
            .map(item => item.entry);
    }

    function translate(key) {
        if (!key) return '';
        const reorg = window.VoltUiReorg;
        if (reorg && typeof reorg.t === 'function') {
            const value = reorg.t(key);
            if (value && value !== key) return value;
        }
        if (window.I18n && typeof window.I18n.t === 'function') {
            const value = window.I18n.t(key);
            if (value && value !== key) return value;
        }
        return key;
    }

    function localizedCatalog() {
        return catalog.map(entry => ({
            ...entry,
            label: translate(entry.labelKey),
            description: translate(entry.descriptionKey),
            keywords: entry.keywords.map(translate),
        }));
    }

    function categoryLabel(category) {
        return translate('search_category_' + category);
    }

    function escapeHtml(value) {
        return String(value == null ? '' : value)
            .replaceAll('&', '&amp;')
            .replaceAll('<', '&lt;')
            .replaceAll('>', '&gt;')
            .replaceAll('"', '&quot;')
            .replaceAll("'", '&#39;');
    }

    function setSelected(index) {
        if (!visibleResults.length) {
            selectedIndex = 0;
            input?.removeAttribute('aria-activedescendant');
            return;
        }
        selectedIndex = (index + visibleResults.length) % visibleResults.length;
        resultList?.querySelectorAll('.vm-search-option').forEach((node, optionIndex) => {
            const active = optionIndex === selectedIndex;
            node.classList.toggle('is-active', active);
            node.setAttribute('aria-selected', active ? 'true' : 'false');
            if (active) {
                input?.setAttribute('aria-activedescendant', node.id);
                node.scrollIntoView({ block: 'nearest' });
            }
        });
    }

    function resultMarkup(entry, index) {
        const description = entry.description
            ? '<span class="vm-search-option__description">' + escapeHtml(entry.description) + '</span>'
            : '';
        return `<button type="button" role="option" id="vm-search-option-${escapeHtml(entry.id)}"
            class="vm-search-option${index === selectedIndex ? ' is-active' : ''}"
            aria-selected="${index === selectedIndex ? 'true' : 'false'}" data-search-index="${index}">
            <span class="vm-search-option__icon material-symbols-outlined" aria-hidden="true">${escapeHtml(entry.icon)}</span>
            <span class="vm-search-option__content">
                <span class="vm-search-option__label">${escapeHtml(entry.label)}</span>
                <span class="vm-search-option__path">${escapeHtml(categoryLabel(entry.category))}</span>
                ${description}
            </span>
        </button>`;
    }

    function renderResults() {
        if (!resultList || !input) return;
        visibleResults = rankEntries(input.value, localizedCatalog());
        selectedIndex = Math.min(selectedIndex, Math.max(0, visibleResults.length - 1));

        if (!visibleResults.length) {
            resultList.innerHTML = `<div class="vm-search-empty" role="status">${escapeHtml(translate('search_empty'))}</div>`;
            input.removeAttribute('aria-activedescendant');
            return;
        }

        resultList.innerHTML = visibleResults.map(resultMarkup).join('');
        setSelected(selectedIndex);
    }

    function ensureDialog() {
        if (dialog) return dialog;
        dialog = document.createElement('div');
        dialog.id = 'vm-global-search';
        dialog.className = 'vm-search-overlay hidden';
        dialog.setAttribute('aria-hidden', 'true');
        dialog.innerHTML = `
            <section class="vm-search-dialog" role="dialog" aria-modal="true" aria-labelledby="vm-search-title">
                <h2 class="sr-only" id="vm-search-title">${escapeHtml(translate('search_title'))}</h2>
                <div class="vm-search-input-row">
                    <span class="material-symbols-outlined" aria-hidden="true">search</span>
                    <input id="vm-global-search-input" class="vm-search-input" type="search"
                        autocomplete="off" spellcheck="false" role="combobox"
                        aria-autocomplete="list" aria-expanded="true"
                        aria-controls="vm-global-search-results"
                        placeholder="${escapeHtml(translate('search_placeholder'))}">
                    <kbd class="vm-search-kbd">Ctrl K</kbd>
                </div>
                <div id="vm-global-search-results" class="vm-search-results" role="listbox"
                    aria-label="${escapeHtml(translate('search_title'))}"></div>
                <footer class="vm-search-footer">
                    <span>${escapeHtml(translate('search_hint'))}</span>
                </footer>
            </section>`;
        document.body.appendChild(dialog);
        input = dialog.querySelector('#vm-global-search-input');
        resultList = dialog.querySelector('#vm-global-search-results');

        input.addEventListener('input', () => {
            selectedIndex = 0;
            renderResults();
        });
        input.addEventListener('keydown', onInputKeydown);
        resultList.addEventListener('mousemove', event => {
            const option = event.target.closest?.('[data-search-index]');
            if (option) setSelected(Number(option.dataset.searchIndex));
        });
        resultList.addEventListener('click', event => {
            const option = event.target.closest?.('[data-search-index]');
            if (!option) return;
            const entry = visibleResults[Number(option.dataset.searchIndex)];
            if (entry) openResult(entry);
        });
        dialog.addEventListener('mousedown', event => {
            if (event.target === dialog) close();
        });
        return dialog;
    }

    function updateLocalizedUi() {
        updateSearchButton();
        if (!dialog) return;
        const title = dialog.querySelector('#vm-search-title');
        const footer = dialog.querySelector('.vm-search-footer span');
        if (title) title.textContent = translate('search_title');
        if (input) input.placeholder = translate('search_placeholder');
        if (resultList) resultList.setAttribute('aria-label', translate('search_title'));
        if (footer) footer.textContent = translate('search_hint');
        renderResults();
    }

    function updateSearchButton() {
        const button = document.getElementById('vm-global-search-button');
        if (!button) return;
        const label = translate('search_button');
        button.setAttribute('aria-label', label);
        button.title = label + ' (Ctrl+K)';
    }

    function open() {
        ensureDialog();
        if (!dialog.classList.contains('hidden')) return;
        previousFocus = typeof HTMLElement !== 'undefined' && document.activeElement instanceof HTMLElement
            ? document.activeElement : null;
        selectedIndex = 0;
        input.value = '';
        updateLocalizedUi();
        dialog.classList.remove('hidden');
        dialog.setAttribute('aria-hidden', 'false');
        document.documentElement.classList.add('vm-search-open');
        const focusInput = () => input.focus({ preventScroll: true });
        if (typeof requestAnimationFrame === 'function') requestAnimationFrame(focusInput);
        else setTimeout(focusInput, 0);
    }

    function close(options) {
        if (!dialog || dialog.classList.contains('hidden')) return;
        dialog.classList.add('hidden');
        dialog.setAttribute('aria-hidden', 'true');
        document.documentElement.classList.remove('vm-search-open');
        input?.removeAttribute('aria-activedescendant');
        if (options?.restoreFocus !== false && previousFocus && document.contains(previousFocus)) {
            previousFocus.focus({ preventScroll: true });
        }
        previousFocus = null;
    }

    function onInputKeydown(event) {
        if (event.key === 'ArrowDown') {
            event.preventDefault();
            setSelected(selectedIndex + 1);
        } else if (event.key === 'ArrowUp') {
            event.preventDefault();
            setSelected(selectedIndex - 1);
        } else if (event.key === 'Enter') {
            event.preventDefault();
            const entry = visibleResults[selectedIndex];
            if (entry) openResult(entry);
        } else if (event.key === 'Escape') {
            event.preventDefault();
            close();
        }
    }

    function waitFrame() {
        return new Promise(resolve => {
            if (typeof requestAnimationFrame === 'function') requestAnimationFrame(resolve);
            else setTimeout(resolve, 16);
        });
    }

    async function waitForTarget(targetId) {
        if (!targetId) return null;
        const started = Date.now();
        while (Date.now() - started < TARGET_WAIT_MS) {
            const target = document.getElementById(targetId);
            if (target && target.isConnected) return target;
            await waitFrame();
        }
        return document.getElementById(targetId);
    }

    function canFocus(element) {
        if (!element || element.disabled) return false;
        return element.matches('button, a[href], input, select, textarea, [tabindex]:not([tabindex="-1"])');
    }

    function findFocusable(target) {
        if (!target) return null;
        if (canFocus(target)) return target;
        return target.querySelector?.(
            'button:not([disabled]), a[href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'
        ) || null;
    }

    function prefersReducedMotion() {
        return typeof matchMedia === 'function' &&
            matchMedia('(prefers-reduced-motion: reduce)').matches;
    }

    function highlightTarget(target) {
        if (!target) return;
        clearTimeout(highlightTimer);
        document.querySelectorAll('.vm-search-target-highlight')
            .forEach(node => node.classList.remove('vm-search-target-highlight'));
        target.classList.add('vm-search-target-highlight');
        highlightTimer = setTimeout(() => {
            target.classList.remove('vm-search-target-highlight');
        }, HIGHLIGHT_MS);
    }

    async function navigate(entry) {
        const router = window.VoltUiReorg;
        if (!router || typeof router.activateView !== 'function') {
            return { navigated: false, focused: false };
        }

        router.activateView(entry.view, true);
        if (entry.subview && typeof router.activateSubview === 'function') {
            router.activateSubview(entry.view, entry.subview);
        }

        await waitFrame();
        const target = await waitForTarget(entry.targetId);
        let focused = false;
        if (target) {
            target.scrollIntoView({
                behavior: prefersReducedMotion() ? 'auto' : 'smooth',
                block: 'center',
                inline: 'nearest'
            });
            const focusable = findFocusable(target);
            if (focusable) {
                try { focusable.focus({ preventScroll: true }); }
                catch (_) { focusable.focus(); }
                focused = true;
            }
            highlightTarget(target);
        }
        return { navigated: true, focused };
    }

    async function openResult(entry) {
        const origin = previousFocus;
        close({ restoreFocus: false });
        const result = await navigate(entry);
        if (!result.focused && origin && document.contains(origin)) {
            origin.focus({ preventScroll: true });
        }
    }

    function onDocumentKeydown(event) {
        if (event.ctrlKey && !event.altKey && !event.shiftKey && event.key.toLowerCase() === 'k') {
            event.preventDefault();
            open();
            return;
        }
        if (event.key === 'Escape' && dialog && !dialog.classList.contains('hidden')) {
            event.preventDefault();
            close();
        }
    }

    function wireSearchButton() {
        const button = document.getElementById('vm-global-search-button');
        if (!button || button.dataset.vmSearchWired === 'true') return;
        button.dataset.vmSearchWired = 'true';
        button.addEventListener('click', open);
        updateSearchButton();
    }

    function boot() {
        wireSearchButton();
        document.addEventListener('voltuiready', wireSearchButton);
        document.addEventListener('langchanged', updateLocalizedUi);
        document.addEventListener('keydown', onDocumentKeydown);
    }

    api.catalog = catalog;
    api.normalize = normalize;
    api.rankEntries = rankEntries;
    api.localizedCatalog = localizedCatalog;
    api.open = open;
    api.close = close;
    api.navigate = navigate;

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', boot, { once: true });
    } else {
        boot();
    }
})();

