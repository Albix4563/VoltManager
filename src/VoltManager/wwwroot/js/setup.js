/**
 * Setup overlay: owns default-plan readiness for the whole frontend.
 */
(function () {
    const hostAvailable = !!(window.Host && Host.available);
    if (!hostAvailable) {
        window.VoltSetupReady = Promise.resolve();
        return;
    }

    const overlay = document.getElementById('setup-overlay');
    const modal = document.getElementById('setup-modal');
    const status = document.getElementById('setup-status');
    const association = document.getElementById('setup-association');
    const associationList = document.getElementById('setup-association-list');
    const btnAssociate = document.getElementById('btn-setup-associate');
    const btnInstall = document.getElementById('btn-setup-install');
    const btnRetry = document.getElementById('btn-setup-retry');
    const btnExit = document.getElementById('btn-setup-exit');
    if (!overlay || !modal || !status || !association || !associationList ||
        !btnAssociate || !btnInstall || !btnRetry || !btnExit) return;

    const t = key => window.I18n ? I18n.t(key) : key;
    let currentState = null;
    let busy = false;
    let readyResolved = false;
    let previousFocus = null;
    let resolveReady;
    window.VoltSetupReady = new Promise(resolve => { resolveReady = resolve; });

    function hasSelectedAssociations() {
        return !!associationList.querySelector('select[data-plan-id] option:checked:not([value=""])');
    }

    function setBusy(value) {
        busy = value;
        btnInstall.disabled = value || hasSelectedAssociations();
        btnRetry.disabled = value;
        btnAssociate.disabled = value || !hasSelectedAssociations();
        overlay.setAttribute('aria-busy', value ? 'true' : 'false');
    }

    function show() {
        if (overlay.classList.contains('hidden')) previousFocus = document.activeElement;
        overlay.classList.remove('hidden');
        overlay.classList.add('flex');
        requestAnimationFrame(() => {
            const preferred = !btnRetry.classList.contains('hidden') && !btnRetry.disabled
                ? btnRetry : overlay.querySelector('select:not([disabled]), button:not([disabled])');
            (preferred || modal).focus();
        });
    }

    function hide() {
        overlay.classList.add('hidden');
        overlay.classList.remove('flex');
        if (previousFocus && typeof previousFocus.focus === 'function') previousFocus.focus();
        previousFocus = null;
    }

    function setStatus(message, isError) {
        status.textContent = message || '';
        status.classList.toggle('hidden', !message);
        status.classList.toggle('is-error', !!isError);
    }

    function planLabel(planId) {
        const key = {
            PowerSaver: 'setup_plan_power_saver',
            Balanced: 'setup_plan_balanced',
            Performance: 'setup_plan_performance',
        }[planId];
        return key ? t(key) : planId;
    }

    function updateAssociationButton() {
        btnAssociate.disabled = busy || !hasSelectedAssociations();
        btnInstall.disabled = busy || hasSelectedAssociations();
    }

    function renderAssociations(state) {
        associationList.replaceChildren();
        const missing = Array.isArray(state?.missing) ? state.missing : [];
        const candidates = (Array.isArray(state?.installed) ? state.installed : [])
            .filter(plan => !plan.planId && plan.guid);

        for (const planId of missing) {
            if (candidates.length === 0) break;
            const field = document.createElement('label');
            field.className = 'vm-setup-field';

            const label = document.createElement('span');
            label.className = 'text-label-md text-on-surface';
            label.textContent = planLabel(planId);

            const select = document.createElement('select');
            select.className = 'vm-native-select';
            select.dataset.planId = planId;
            select.setAttribute('aria-label', planLabel(planId));

            const placeholder = document.createElement('option');
            placeholder.value = '';
            placeholder.textContent = t('setup_choose_existing');
            select.appendChild(placeholder);
            for (const plan of candidates) {
                const option = document.createElement('option');
                option.value = plan.guid;
                option.textContent = (plan.name || '').trim() || plan.guid;
                select.appendChild(option);
            }
            select.addEventListener('change', updateAssociationButton);
            field.append(label, select);
            associationList.appendChild(field);
        }

        const hasChoices = associationList.children.length > 0;
        association.classList.toggle('hidden', !hasChoices);
        updateAssociationButton();
    }

    function markReady(message) {
        if (message) setStatus(message, false);
        hide();
        if (readyResolved) return;
        readyResolved = true;
        resolveReady();
    }

    function renderState(state, message) {
        currentState = state;
        btnRetry.classList.add('hidden');
        if (state?.allPresent) {
            renderAssociations({ missing: [], installed: [] });
            markReady(message || '');
            return;
        }

        show();
        btnInstall.classList.remove('hidden');
        renderAssociations(state);
        setStatus(message || '', false);
    }

    function renderCheckError() {
        currentState = null;
        show();
        association.classList.add('hidden');
        associationList.replaceChildren();
        btnInstall.classList.add('hidden');
        btnRetry.classList.remove('hidden');
        setStatus(t('setup_check_error'), true);
    }

    async function checkDefaults({ startup = false, message = '' } = {}) {
        setBusy(true);
        const attempts = startup ? 2 : 1;
        try {
            let lastError;
            for (let attempt = 0; attempt < attempts; attempt++) {
                try {
                    const state = await Host.call('checkDefaultPlans');
                    renderState(state, message);
                    return state;
                } catch (err) {
                    lastError = err;
                    if (attempt + 1 < attempts)
                        await new Promise(resolve => window.setTimeout(resolve, 250));
                }
            }
            console.error('default power plan check failed', lastError);
            renderCheckError();
            return null;
        } finally {
            setBusy(false);
        }
    }

    btnExit.addEventListener('click', () => {
        Host.call('exitApp').catch(() => {});
    });

    btnRetry.addEventListener('click', () => { void checkDefaults(); });

    btnAssociate.addEventListener('click', async () => {
        if (busy) return;
        const associations = Array.from(associationList.querySelectorAll('select[data-plan-id]'))
            .filter(select => select.value)
            .map(select => ({ planId: select.dataset.planId, guid: select.value }));
        if (associations.length === 0) return;

        setBusy(true);
        setStatus(t('setup_associating'), false);
        try {
            await Host.call('associateDefaultPlans', { associations });
            await checkDefaults({ message: t('setup_associate_ok') });
        } catch (err) {
            console.error('power plan association failed', err);
            setStatus(t('setup_associate_error'), true);
        } finally {
            setBusy(false);
        }
    });

    btnInstall.addEventListener('click', async () => {
        if (busy || !currentState || currentState.allPresent || hasSelectedAssociations()) return;
        setBusy(true);
        setStatus(t('msg_installing'), false);
        try {
            await Host.call('restoreDefaultPlans');
            const state = await checkDefaults();
            if (state && !state.allPresent) setStatus(t('msg_install_part'), false);
        } catch (err) {
            console.error('power plan restore failed', err);
            setStatus(t('setup_restore_error'), true);
        } finally {
            setBusy(false);
        }
    });

    document.addEventListener('keydown', event => {
        if (overlay.classList.contains('hidden')) return;
        if (event.key === 'Escape' && !busy) {
            event.preventDefault();
            btnExit.click();
            return;
        }
        if (event.key !== 'Tab') return;
        const focusable = Array.from(modal.querySelectorAll(
            'button:not([disabled]):not(.hidden), select:not([disabled]), [tabindex]:not([tabindex="-1"])'
        )).filter(element => element.offsetParent !== null);
        if (focusable.length === 0) return;
        const first = focusable[0];
        const last = focusable[focusable.length - 1];
        if (event.shiftKey && document.activeElement === first) {
            event.preventDefault();
            last.focus();
        } else if (!event.shiftKey && document.activeElement === last) {
            event.preventDefault();
            first.focus();
        }
    });

    document.addEventListener('langchanged', () => {
        if (currentState && !currentState.allPresent) renderAssociations(currentState);
    });

    window.VoltSetup = { ready: window.VoltSetupReady, recheck: checkDefaults };
    void checkDefaults({ startup: true });
})();
