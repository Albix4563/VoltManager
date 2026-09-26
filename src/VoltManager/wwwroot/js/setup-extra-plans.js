/**
 * Detects and optionally removes power plans beyond VoltManager's three primary plans.
 * Deletion only happens after an explicit user click.
 */
(function () {
    if (!window.Host || !Host.available) return;

    const overlay = document.getElementById('extra-plans-overlay');
    const list = document.getElementById('extra-plans-list');
    const description = document.getElementById('extra-plans-description');
    const status = document.getElementById('extra-plans-status');
    const btnDelete = document.getElementById('btn-extra-plans-delete');
    const btnLater = document.getElementById('btn-extra-plans-later');
    const btnDismiss = document.getElementById('btn-extra-plans-dismiss');
    const setupOverlay = document.getElementById('setup-overlay');
    const welcomeOverlay = document.getElementById('welcome-overlay');
    if (!overlay || !list || !description || !status || !btnDelete || !btnLater || !btnDismiss) return;

    let currentReport = null;
    let busy = false;
    let closing = false;
    let closeTimer = null;
    let previousFocus = null;

    const t = key => window.I18n ? I18n.t(key) : key;
    const format = (key, values) => window.I18n && I18n.format
        ? I18n.format(t(key), values)
        : t(key);

    function isHidden(element) {
        return !element || element.classList.contains('hidden');
    }

    function waitUntilHidden(element) {
        if (isHidden(element)) return Promise.resolve();
        return new Promise(resolve => {
            const observer = new MutationObserver(() => {
                if (!isHidden(element)) return;
                observer.disconnect();
                resolve();
            });
            observer.observe(element, { attributes: true, attributeFilter: ['class'] });
        });
    }

    function waitForSetupToFinish() {
        if (!setupOverlay) return Promise.resolve();
        if (!isHidden(setupOverlay)) return waitUntilHidden(setupOverlay);
        return new Promise(resolve => {
            let appeared = false;
            const observer = new MutationObserver(() => {
                if (!isHidden(setupOverlay)) {
                    appeared = true;
                    return;
                }
                if (!appeared) return;
                observer.disconnect();
                resolve();
            });
            observer.observe(setupOverlay, { attributes: true, attributeFilter: ['class'] });
        });
    }

    function show() {
        clearTimeout(closeTimer);
        closing = false;
        previousFocus = document.activeElement;
        overlay.classList.remove('hidden');
        overlay.classList.add('flex');
        requestAnimationFrame(() => {
            const firstCheckbox = list.querySelector('input[type="checkbox"]');
            (firstCheckbox || btnDelete || btnLater).focus();
        });
    }

    function close() {
        clearTimeout(closeTimer);
        closing = false;
        overlay.classList.add('hidden');
        overlay.classList.remove('flex');
        busy = false;
        if (previousFocus && typeof previousFocus.focus === 'function') previousFocus.focus();
        previousFocus = null;
    }

    function setStatus(message, isError) {
        status.textContent = message || '';
        status.classList.toggle('hidden', !message);
        status.style.color = isError ? '#ff8a80' : '';
    }

    function updateDeleteButton() {
        const anyChecked = !!list.querySelector('input[type="checkbox"]:checked');
        btnDelete.disabled = busy || closing || !anyChecked;
    }

    function badge(text, extraClass) {
        const el = document.createElement('span');
        el.className = 'text-label-sm rounded-full px-2 py-0.5 border ' + extraClass;
        el.textContent = text;
        return el;
    }

    function renderList(extras) {
        list.replaceChildren();
        for (const plan of extras) {
            const row = document.createElement('label');
            row.className = 'flex items-start gap-sm rounded-xl border border-white/10 bg-white/[0.03] p-sm cursor-pointer';

            const checkbox = document.createElement('input');
            checkbox.type = 'checkbox';
            checkbox.checked = true;
            checkbox.value = plan.guid;
            checkbox.className = 'mt-1 accent-[var(--vm-accent)] focus-visible:outline focus-visible:outline-2 focus-visible:outline-secondary-container';
            checkbox.addEventListener('change', updateDeleteButton);

            const body = document.createElement('span');
            body.className = 'min-w-0 flex-1';

            const heading = document.createElement('span');
            heading.className = 'flex items-center gap-xs flex-wrap';

            const name = document.createElement('span');
            name.className = 'text-body-md text-on-surface font-semibold break-all';
            name.textContent = (plan.name || '').trim() || plan.guid;
            heading.appendChild(name);

            const original = (currentReport?.keep || []).find(kept => kept.planId === plan.duplicateOf);
            if (plan.isDuplicate)
                heading.appendChild(badge(format('extra_plans_duplicate', { plan: original?.name || plan.duplicateOf || '' }), 'border-secondary-container/25 text-secondary-container bg-secondary-container/10'));
            if (plan.isActive)
                heading.appendChild(badge(t('extra_plans_active'), 'border-white/15 text-on-surface-variant bg-white/5'));

            const guid = document.createElement('span');
            guid.className = 'block text-label-sm text-on-surface-variant mt-1 break-all';
            guid.textContent = plan.guid;

            body.appendChild(heading);
            body.appendChild(guid);
            row.appendChild(checkbox);
            row.appendChild(body);
            list.appendChild(row);
        }
        updateDeleteButton();
    }

    function renderReport(report) {
        currentReport = report || { extras: [] };
        const extras = Array.isArray(currentReport.extras) ? currentReport.extras : [];
        if (extras.length === 0) {
            description.textContent = t('extra_plans_none');
        } else {
            description.textContent = format('extra_plans_description', { count: extras.length });
        }
        renderList(extras);
        btnDismiss.classList.toggle('hidden', extras.length === 0);
    }

    async function refreshAdvancedPlans() {
        try {
            await window.VoltAdvanced?.reloadPowerPlans?.();
        } catch (err) {
            console.error('advanced power plan refresh failed', err);
        }
    }

    async function deleteSelected() {
        if (busy) return;
        const guids = Array.from(list.querySelectorAll('input[type="checkbox"]:checked'), input => input.value);
        if (guids.length === 0) return;

        busy = true;
        updateDeleteButton();
        try {
            const result = await Host.call('deleteExtraPlans', { guids });
            const deletedCount = Array.isArray(result?.deleted) ? result.deleted.length : 0;
            const failedCount = Array.isArray(result?.failed) ? result.failed.length : 0;
            setStatus(format('extra_plans_result', { deleted: deletedCount, failed: failedCount }), failedCount > 0);
            await refreshAdvancedPlans();

            if (failedCount === 0) {
                closing = true;
                updateDeleteButton();
                closeTimer = setTimeout(close, 900);
            } else {
                const fresh = await Host.call('findExtraPlans');
                renderReport(fresh);
            }
        } catch (err) {
            setStatus(t('msg_err') + (err?.message || err), true);
        } finally {
            busy = false;
            updateDeleteButton();
        }
    }

    async function dismissCurrent() {
        if (busy || !currentReport) return;
        const guids = (currentReport.extras || []).map(plan => plan.guid);
        if (guids.length === 0) {
            close();
            return;
        }
        busy = true;
        updateDeleteButton();
        try {
            await Host.call('dismissExtraPlans', { guids });
            close();
        } catch (err) {
            setStatus(t('msg_err') + (err?.message || err), true);
        } finally {
            busy = false;
            updateDeleteButton();
        }
    }

    async function openManual() {
        setStatus('', false);
        try {
            const report = await Host.call('findExtraPlans');
            renderReport(report);
            show();
        } catch (err) {
            renderReport({ extras: [] });
            setStatus(t('msg_err') + (err?.message || err), true);
            show();
        }
    }

    async function autoCheck() {
        try {
            const defaults = await Host.call('checkDefaultPlans');
            if (!defaults?.allPresent) await waitForSetupToFinish();
            await waitUntilHidden(welcomeOverlay);

            const report = await Host.call('findExtraPlans');
            if (!report?.shouldPrompt) return;
            setStatus('', false);
            renderReport(report);
            show();
        } catch (err) {
            console.error('extra power plan check failed', err);
        }
    }

    btnDelete.addEventListener('click', deleteSelected);
    btnLater.addEventListener('click', close);
    btnDismiss.addEventListener('click', dismissCurrent);
    document.addEventListener('keydown', event => {
        if (event.key !== 'Escape' || isHidden(overlay) || busy) return;
        event.preventDefault();
        close();
    });
    document.addEventListener('langchanged', () => {
        if (!currentReport || isHidden(overlay)) return;
        renderReport(currentReport);
    });

    window.ExtraPlansCleanup = { open: openManual };
    document.addEventListener('settingsloaded', () => { void autoCheck(); }, { once: true });
})();
