/**
 * Power-plan history view module. Owns its bridge subscription and route lifecycle.
 */
(function () {
    if (!window.Host || !Host.available || !window.VoltViewLifecycle) return;

    let active = false;
    let planHistoryWired = false;
    let planHistoryUnsubscribe = null;
    let visibilityWired = false;
    let historyMountNode = null;
    const planHistoryState = {
        entries: [],
        revision: -1,
        notifiedRevision: -1,
        filter: 'all',
        visibleCount: 50,
        dirty: true,
        loading: false,
        error: false,
    };

    function lang() {
        return window.I18n && I18n.getLang ? I18n.getLang() : 'it';
    }

    function ht(key) {
        return window.I18n && I18n.feature ? I18n.feature('planHistory', key, lang()) : key;
    }

    function getHistoryMount() {
        return document.getElementById('vm-power-history') || document.getElementById('plan-history-mount');
    }

    function historyLocale() {
        return { it: 'it-IT', en: 'en-US', es: 'es-ES', zh: 'zh-CN' }[lang()] || 'en-US';
    }

    function historyFormat(template, values) {
        if (window.I18n && I18n.format) return I18n.format(template, values);
        return Object.entries(values || {}).reduce(
            (result, [key, value]) => result.replaceAll('{' + key + '}', () => value == null || value === '' ? '—' : String(value)),
            template);
    }

    function historyDate(value) {
        const date = new Date(value);
        if (Number.isNaN(date.getTime())) return '—';
        const options = {
            year: 'numeric', month: '2-digit', day: '2-digit',
            hour: '2-digit', minute: '2-digit', second: '2-digit'
        };
        if (window.I18n && I18n.date) return I18n.date(date, lang(), options);
        return new Intl.DateTimeFormat(historyLocale(), options).format(date);
    }

    function historyNumber(value) {
        if (value == null || value === '') return '—';
        const number = Number(value);
        if (!Number.isFinite(number)) return '—';
        if (window.I18n && I18n.number) return I18n.number(number, lang(), { maximumFractionDigits: 2 });
        return new Intl.NumberFormat(historyLocale(), { maximumFractionDigits: 2 }).format(number);
    }

    function historyPlanName(plan) {
        if (!plan) return ht('unavailable');
        const key = {
            powerSaver: 'dash_plan_saver',
            balanced: 'dash_plan_balanced',
            performance: 'dash_plan_performance'
        }[plan.planId];
        if (key) return I18n.t(key);
        return String(plan.name || plan.guid || ht('customPlan'));
    }

    function historyExplanation(entry) {
        const d = entry.details || {};
        const app = String(d.appName || 'App');
        switch (entry.reasonCode) {
            case 'manual_selection': return ht('manualSelection');
            case 'manual_override': return ht('manualOverride');
            case 'gaming_manual': return ht('gamingManual');
            case 'external_change_detected': return ht('externalChange');
            case 'expected_plan_restored': return ht('guardRestore');
            case 'profile_applied': return historyFormat(ht('appProfileApply'), { app });
            case 'profile_session_ended': return historyFormat(ht('appProfileEnd'), { app });
            case 'game_load_detected': return historyFormat(ht('gameLoad'), { app });
            case 'heavy_app_load_detected': return historyFormat(ht('heavyLoad'), { app });
            case 'heavy_app_session_ended': return historyFormat(ht('heavyEnd'), { app });
            case 'cpu_rule_triggered':
                return historyFormat(ht('cpuRule'), {
                    comparison: d.comparison === 'lt' ? '<' : '>',
                    threshold: historyNumber(d.thresholdPct),
                    duration: historyNumber(d.durationMinutes),
                    average: historyNumber(d.averageCpu)
                });
            case 'tripped': return entry.source === 'idle' ? ht('idleTrip') : ht('thermalTrip');
            case 'active_no_input': return ht('idleKeep');
            case 'active_no_sensors': return ht('thermalKeep');
            case 'active_switch': return entry.source === 'idle' ? ht('idleKeep') : ht('thermalKeep');
            case 'cooled': return ht('thermalRestore');
            case 'disabled': return ht(entry.source === 'idle' ? 'idleDisabled' : 'thermalDisabled');
            case 'resumed': return ht('idleRestore');
            case 'battery_skip': return ht('idleBatterySkip');
            case 'plugged_switch': return ht('plugged');
            case 'unplugged_restore': return ht('unplugged');
            case 'disabled_restore': return ht('powerSourceDisabled');
            case 'low_battery_switch': return ht('lowBattery');
            case 'low_battery_restore': return ht('lowBatteryRestore');
            case 'parameters_reapply': return ht('parameters');
            default: return ht('generic');
        }
    }

    function historyFilteredEntries() {
        const filter = planHistoryState.filter;
        if (filter === 'all') return planHistoryState.entries;
        if (filter === 'problems')
            return planHistoryState.entries.filter(entry => entry.outcome === 'failed' || entry.outcome === 'unverifiable');
        if (filter === 'automatic') return planHistoryState.entries.filter(entry => entry.category === 'automatic');
        if (filter === 'manual') return planHistoryState.entries.filter(entry => entry.category === 'manual');
        if (filter === 'external') return planHistoryState.entries.filter(entry => entry.category === 'external');
        return planHistoryState.entries;
    }

    function historyElement(tag, className, textValue) {
        const node = document.createElement(tag);
        if (className) node.className = className;
        if (textValue != null) node.textContent = String(textValue);
        return node;
    }

    function historyPlanField(container, label, plan) {
        const field = historyElement('div', 'plan-history-plan');
        field.appendChild(historyElement('span', 'plan-history-plan-label', label));
        field.appendChild(historyElement('span', 'plan-history-plan-value', historyPlanName(plan)));
        container.appendChild(field);
    }

    function renderPlanHistory() {
        const mount = getHistoryMount();
        const list = document.getElementById('plan-history-list');
        const state = document.getElementById('plan-history-state');
        const more = document.getElementById('plan-history-more');
        const retry = document.getElementById('plan-history-retry');
        const clear = document.getElementById('plan-history-clear');
        if (!mount || !list || !state || !more || !retry || !clear) return;

        document.querySelectorAll('.plan-history-filter').forEach(button => {
            const active = button.dataset.filter === planHistoryState.filter;
            button.setAttribute('aria-pressed', active ? 'true' : 'false');
            button.textContent = ht(button.dataset.filter);
        });
        clear.textContent = ht('clear');
        retry.textContent = ht('retry');
        more.textContent = ht('showMore');
        const note = document.getElementById('plan-history-note');
        if (note) note.textContent = ht('note');

        list.replaceChildren();
        retry.hidden = true;
        more.hidden = true;

        if (planHistoryState.error) {
            state.textContent = ht('loadError');
            retry.hidden = false;
            return;
        }

        const filtered = historyFilteredEntries();
        if (!planHistoryState.entries.length) {
            state.textContent = ht('empty');
            return;
        }
        if (!filtered.length) {
            state.textContent = ht('noResults');
            return;
        }

        state.textContent = '';
        filtered.slice(0, planHistoryState.visibleCount).forEach(entry => {
            const card = historyElement('article', 'plan-history-entry');
            const head = historyElement('div', 'plan-history-entry-head');
            let timeText = historyDate(entry.lastTimestampUtc);
            const attempts = Number(entry.attempts) || 1;
            if (attempts > 1) {
                const attemptsText = window.I18n && I18n.number
                    ? I18n.number(attempts, lang())
                    : new Intl.NumberFormat(historyLocale()).format(attempts);
                timeText = historyDate(entry.firstTimestampUtc) + ' → ' + historyDate(entry.lastTimestampUtc) +
                    ' · ' + attemptsText + ' ' + ht('attempts');
            }
            head.appendChild(historyElement('span', 'plan-history-time', timeText));
            const outcome = historyElement('span', 'plan-history-outcome', ht(entry.outcome));
            outcome.dataset.problem = entry.outcome === 'failed' || entry.outcome === 'unverifiable' ? 'true' : 'false';
            head.appendChild(outcome);
            card.appendChild(head);
            card.appendChild(historyElement('p', 'plan-history-explanation', historyExplanation(entry)));

            const plans = historyElement('div', 'plan-history-plans');
            if (entry.outcome === 'applied') {
                historyPlanField(plans, ht('previous'), entry.previousPlan);
                historyPlanField(plans, ht('appliedPlan'), entry.observedPlan || entry.requestedPlan);
            } else if (entry.outcome === 'externalDetected') {
                historyPlanField(plans, ht('previous'), entry.previousPlan);
                historyPlanField(plans, ht('observed'), entry.observedPlan);
            } else {
                historyPlanField(plans, ht('requested'), entry.requestedPlan);
                historyPlanField(plans, ht('observed'), entry.observedPlan);
            }
            card.appendChild(plans);
            list.appendChild(card);
        });

        more.hidden = filtered.length <= planHistoryState.visibleCount;
    }

    function mountPlanHistoryUi() {
        const mount = getHistoryMount();
        if (!mount) return;
        const existing = document.getElementById('plan-history-shell');
        if (existing) {
            if (existing.parentElement !== mount) mount.appendChild(existing);
            mount.dataset.mounted = 'true';
            return;
        }
        if (mount.dataset.mounted === 'true') return;
        mount.dataset.mounted = 'true';

        const shell = historyElement('div', 'plan-history-shell');
        shell.id = 'plan-history-shell';
        const toolbar = historyElement('div', 'plan-history-toolbar');
        const filters = historyElement('div', 'plan-history-filters');
        filters.setAttribute('role', 'group');
        ['all', 'automatic', 'manual', 'external', 'problems'].forEach(filter => {
            const button = historyElement('button', 'plan-history-filter', ht(filter));
            button.type = 'button';
            button.dataset.filter = filter;
            button.setAttribute('aria-pressed', filter === 'all' ? 'true' : 'false');
            filters.appendChild(button);
        });
        toolbar.appendChild(filters);
        const clear = historyElement('button', 'plan-history-action', ht('clear'));
        clear.type = 'button';
        clear.id = 'plan-history-clear';
        toolbar.appendChild(clear);
        shell.appendChild(toolbar);

        const state = historyElement('p', 'plan-history-state');
        state.id = 'plan-history-state';
        state.setAttribute('aria-live', 'polite');
        shell.appendChild(state);
        const retry = historyElement('button', 'plan-history-action', ht('retry'));
        retry.type = 'button';
        retry.id = 'plan-history-retry';
        retry.hidden = true;
        shell.appendChild(retry);
        const list = historyElement('div', 'plan-history-list');
        list.id = 'plan-history-list';
        shell.appendChild(list);
        const more = historyElement('button', 'plan-history-action', ht('showMore'));
        more.type = 'button';
        more.id = 'plan-history-more';
        more.hidden = true;
        shell.appendChild(more);
        const note = historyElement('p', 'plan-history-note', ht('note'));
        note.id = 'plan-history-note';
        shell.appendChild(note);
        mount.appendChild(shell);
    }

    function planHistoryVisible() {
        return active && !document.hidden;
    }

    async function loadPlanHistory() {
        if (!Host.available || planHistoryState.loading) return;
        mountPlanHistoryUi();
        planHistoryState.loading = true;
        planHistoryState.error = false;
        try {
            const snapshot = await Host.call('getPlanHistory');
            const revision = snapshot?.revision;
            if (!Number.isSafeInteger(revision) || revision < 0 || !Array.isArray(snapshot.entries))
                throw new Error('Invalid plan history snapshot');
            if (revision >= Math.max(planHistoryState.revision, planHistoryState.notifiedRevision)) {
                planHistoryState.revision = revision;
                planHistoryState.entries = snapshot.entries;
                planHistoryState.dirty = false;
                planHistoryState.error = false;
            }
        } catch (err) {
            planHistoryState.error = true;
            console.error('getPlanHistory failed', err);
        } finally {
            planHistoryState.loading = false;
            if (planHistoryVisible()) {
                renderPlanHistory();
                if (planHistoryState.dirty && !planHistoryState.error) loadPlanHistory();
            }
        }
    }

    async function onHistoryClick(event) {
            const filter = event.target.closest('.plan-history-filter');
            if (filter) {
                planHistoryState.filter = filter.dataset.filter || 'all';
                planHistoryState.visibleCount = 50;
                renderPlanHistory();
                return;
            }
            if (event.target.closest('#plan-history-more')) {
                planHistoryState.visibleCount += 50;
                renderPlanHistory();
                return;
            }
            if (event.target.closest('#plan-history-retry')) {
                planHistoryState.error = false;
                await loadPlanHistory();
                return;
            }
            if (event.target.closest('#plan-history-clear')) {
                try {
                    const result = await Host.call('clearPlanHistory');
                    const revision = result?.revision;
                    if (!Number.isSafeInteger(revision) || revision < 0)
                        throw new Error('Invalid plan history revision');
                    if (revision >= Math.max(planHistoryState.revision, planHistoryState.notifiedRevision)) {
                        planHistoryState.revision = revision;
                        planHistoryState.entries = [];
                        planHistoryState.dirty = false;
                        planHistoryState.error = false;
                        planHistoryState.visibleCount = 50;
                        renderPlanHistory();
                    } else {
                        planHistoryState.dirty = true;
                        if (planHistoryVisible()) loadPlanHistory();
                    }
                } catch (err) {
                    planHistoryState.error = true;
                    renderPlanHistory();
                }
            }
    }

    function onVisibilityChange() {
        if (planHistoryVisible()) loadPlanHistory();
    }

    function onLanguageChange() {
        renderPlanHistory();
    }

    function wirePlanHistoryUi() {
        if (planHistoryWired) return;
        mountPlanHistoryUi();
        historyMountNode = document.getElementById('plan-history-shell') || getHistoryMount();
        if (!historyMountNode) return;

        historyMountNode.addEventListener('click', onHistoryClick);

        planHistoryUnsubscribe = Host.on('planHistoryChanged', data => {
            const revision = data?.revision;
            if (!Number.isSafeInteger(revision) || revision < 0 || revision <= planHistoryState.revision) return;
            planHistoryState.notifiedRevision = Math.max(planHistoryState.notifiedRevision, revision);
            planHistoryState.dirty = true;
            if (planHistoryVisible()) loadPlanHistory();
        });
        document.addEventListener('langchanged', onLanguageChange);
        planHistoryWired = true;
        renderPlanHistory();
    }

    function activate() {
        active = true;
        mountPlanHistoryUi();
        wirePlanHistoryUi();
        if (!visibilityWired) {
            document.addEventListener('visibilitychange', onVisibilityChange);
            visibilityWired = true;
        }
        loadPlanHistory();
    }

    function deactivate() {
        active = false;
        if (visibilityWired) {
            document.removeEventListener('visibilitychange', onVisibilityChange);
            visibilityWired = false;
        }
    }

    function dispose() {
        deactivate();
        if (historyMountNode) historyMountNode.removeEventListener('click', onHistoryClick);
        historyMountNode = null;
        document.removeEventListener('langchanged', onLanguageChange);
        if (planHistoryUnsubscribe) planHistoryUnsubscribe();
        planHistoryUnsubscribe = null;
        planHistoryWired = false;
    }

    const descriptor = {
        matches(route) {
            return (route.view === 'power' && route.subviews.power === 'history') ||
                (route.view === 'power-plans' && route.subviews['power-plans'] === 'history');
        },
        init: wirePlanHistoryUi,
        activate,
        deactivate,
        dispose,
    };
    window.VoltViewLifecycle.register('plan-history', descriptor);

    window.VoltPlanHistory = {
        state: planHistoryState,
        load: loadPlanHistory,
        render: renderPlanHistory,
        explanation: historyExplanation,
        number: historyNumber,
        descriptor,
    };
})();
