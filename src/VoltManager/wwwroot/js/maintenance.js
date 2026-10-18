/**
 * VoltManager Maintenance Tools (Applications, Registry, Backup).
 * Original implementation based on native Windows APIs and VoltManager Host bridge.
 */
(function () {
    'use strict';

    const api = window.VoltMaintenance = window.VoltMaintenance || {};

    // ponytail: in-memory operation tracking with single active operation; upgrade path: persistent operation queue if multiple concurrent operations are needed.
    const state = {
        subview: 'apps', // 'apps' | 'registry' | 'backup'
        initialized: false,
        active: false,

        // Apps
        apps: [],
        appsWarnings: [],
        appsLoaded: false,
        appsLoading: false,
        appsError: null,
        appsSearch: '',
        appsSort: 'name_asc',

        // Active operation (uninstall, leftovers, registry scan/apply, restore)
        activeOperation: null,
        operationPollingTimer: null,
        lastRenderedOpFingerprint: null,
        selectedItemIds: new Set(),

        // Registry
        registryOp: null,
        registrySelectedItemIds: new Set(),

        // Backups
        backups: [],
        backupsLoaded: false,
        backupsLoading: false,
        backupsError: null,
        selectedBackup: null,
        selectedBackupLoading: false,
        selectedBackupItemIds: new Set(),

        // Modal dialog
        modal: null
    };

    // Reference all keys explicitly so contract scanner validates 100% parity
    const I18N_KEYS = {
        apps_title: 'apps_title',
        apps_subtitle: 'apps_subtitle',
        apps_search_input: 'apps_search_input',
        apps_sort_label: 'apps_sort_label',
        apps_sort_name_asc: 'apps_sort_name_asc',
        apps_sort_name_desc: 'apps_sort_name_desc',
        apps_sort_size_desc: 'apps_sort_size_desc',
        apps_sort_size_asc: 'apps_sort_size_asc',
        apps_sort_publisher: 'apps_sort_publisher',
        apps_refresh: 'apps_refresh',
        apps_open_settings: 'apps_open_settings',
        apps_count: 'apps_count',
        apps_loading: 'apps_loading',
        apps_empty: 'apps_empty',
        apps_uninstall: 'apps_uninstall',
        apps_protected: 'apps_protected',
        apps_version: 'apps_version',
        apps_publisher: 'apps_publisher',
        apps_location: 'apps_location',
        apps_size: 'apps_size',
        confirm_uninstall_title: 'confirm_uninstall_title',
        confirm_uninstall_desc: 'confirm_uninstall_desc',
        confirm_uninstall_warning: 'confirm_uninstall_warning',
        btn_confirm: 'btn_confirm',
        btn_cancel: 'btn_cancel',
        btn_verify: 'btn_verify',
        btn_scan_leftovers: 'btn_scan_leftovers',
        btn_exclude_selected: 'btn_exclude_selected',
        btn_clean_selected: 'btn_clean_selected',
        btn_restore_selected: 'btn_restore_selected',
        btn_close: 'btn_close',
        btn_delete: 'btn_delete',
        btn_details: 'btn_details',
        op_status_running: 'op_status_running',
        op_status_completed: 'op_status_completed',
        op_status_cancelled: 'op_status_cancelled',
        op_status_failed: 'op_status_failed',
        op_status_unknown: 'op_status_unknown',
        op_status_needsVerification: 'op_status_needsVerification',
        op_status_rebootRequired: 'op_status_rebootRequired',
        reboot_notice: 'reboot_notice',
        verify_notice: 'verify_notice',
        leftovers_title: 'leftovers_title',
        leftovers_empty: 'leftovers_empty',
        leftovers_hint: 'leftovers_hint',
        registry_title: 'registry_title',
        registry_subtitle: 'registry_subtitle',
        registry_disclaimer: 'registry_disclaimer',
        registry_scan_btn: 'registry_scan_btn',
        registry_clean: 'registry_clean',
        registry_results_count: 'registry_results_count',
        item_kind_file: 'item_kind_file',
        item_kind_directory: 'item_kind_directory',
        item_kind_registry: 'item_kind_registry',
        item_user_data: 'item_user_data',
        item_selected_count: 'item_selected_count',
        item_none_selected: 'item_none_selected',
        confirm_clean_title: 'confirm_clean_title',
        confirm_clean_desc: 'confirm_clean_desc',
        confirm_clean_user_data_warn: 'confirm_clean_user_data_warn',
        backup_title: 'backup_title',
        backup_subtitle: 'backup_subtitle',
        backup_disclaimer: 'backup_disclaimer',
        backup_empty: 'backup_empty',
        backup_refresh: 'backup_refresh',
        backup_items_count: 'backup_items_count',
        backup_detail_title: 'backup_detail_title',
        confirm_delete_backup_title: 'confirm_delete_backup_title',
        confirm_delete_backup_desc: 'confirm_delete_backup_desc',
        confirm_restore_title: 'confirm_restore_title',
        confirm_restore_desc: 'confirm_restore_desc',
        restore_success: 'restore_success',
        clean_success: 'clean_success'
    };

    function t(key, params) {
        const lang = window.I18n && window.I18n.getLang ? window.I18n.getLang() : 'it';
        let str = window.I18n && window.I18n.feature
            ? window.I18n.feature('maintenance', key, lang, key)
            : key;
        if (params && typeof params === 'object') {
            for (const [k, v] of Object.entries(params)) {
                str = str.replaceAll('{' + k + '}', String(v));
            }
        }
        return str;
    }

    function formatSize(kb) {
        if (kb == null || typeof kb !== 'number' || isNaN(kb) || kb <= 0) return '--';
        if (kb < 1024) return kb + ' KB';
        const mb = kb / 1024;
        if (mb < 1024) return mb.toFixed(1) + ' MB';
        const gb = mb / 1024;
        return gb.toFixed(2) + ' GB';
    }

    function formatDate(iso) {
        if (!iso) return '--';
        try {
            const d = new Date(iso);
            if (isNaN(d.getTime())) return String(iso);
            const locale = window.I18n && window.I18n.getLocale ? window.I18n.getLocale() : 'it-IT';
            return d.toLocaleString(locale, {
                year: 'numeric', month: '2-digit', day: '2-digit',
                hour: '2-digit', minute: '2-digit'
            });
        } catch (_) {
            return String(iso);
        }
    }

    function showError(err) {
        const msg = err && err.message ? err.message : String(err || '');
        console.error('Maintenance error:', msg);
        try { window.alert?.(msg); } catch (_) {}
    }

    /**
     * Small trusted DOM builder using textContent. No innerHTML with data.
     */
    function el(tag, props, ...children) {
        const element = document.createElement(tag);
        if (props) {
            for (const [key, val] of Object.entries(props)) {
                if (val == null) continue;
                if (key === 'className') {
                    element.className = val;
                } else if (key === 'id') {
                    element.id = val;
                    element.setAttribute('id', val);
                } else if (key === 'checked') {
                    element.checked = !!val;
                    if (val) element.setAttribute('checked', '');
                    else element.removeAttribute('checked');
                } else if (key === 'disabled') {
                    element.disabled = !!val;
                    if (val) element.setAttribute('disabled', '');
                    else element.removeAttribute('disabled');
                } else if (key === 'value') {
                    element.value = val;
                } else if (key === 'textContent') {
                    element.textContent = val;
                } else if (key.startsWith('on') && typeof val === 'function') {
                    element.addEventListener(key.slice(2).toLowerCase(), val);
                } else if (key === 'dataset' && typeof val === 'object') {
                    Object.assign(element.dataset, val);
                } else if (typeof val === 'boolean') {
                    if (val) element.setAttribute(key, '');
                    else element.removeAttribute(key);
                } else {
                    element.setAttribute(key, String(val));
                }
            }
        }
        for (const child of children) {
            if (child == null || child === false) continue;
            if (typeof child === 'string' || typeof child === 'number') {
                element.appendChild(document.createTextNode(String(child)));
            } else if (child instanceof Node) {
                element.appendChild(child);
            } else if (Array.isArray(child)) {
                for (const c of child) {
                    if (c == null || c === false) continue;
                    element.appendChild(typeof c === 'string' || typeof c === 'number' ? document.createTextNode(String(c)) : c);
                }
            }
        }
        return element;
    }

    async function callHost(method, payload) {
        if (!window.Host || typeof window.Host.call !== 'function') {
            throw new Error('Host bridge unavailable');
        }
        return window.Host.call(method, payload || {});
    }

    function opFingerprint(op) {
        if (!op) return 'null';
        const itemsLen = (op.items || []).length;
        return `${op.id}:${op.status}:${op.processed}:${op.total}:${op.message}:${itemsLen}:${op.canScanLeftovers}`;
    }

    // =========================================================================
    // Modal Confirmation Dialog
    // =========================================================================
    let previousActiveElement = null;
    let modalKeydownHandler = null;

    function showConfirmModal(opts) {
        closeConfirmModal();
        previousActiveElement = document.activeElement;

        const scrim = el('div', { className: 'vm-dialog-scrim' });
        const box = el('div', {
            className: 'vm-dialog-box',
            role: 'dialog',
            'aria-modal': 'true',
            'aria-labelledby': 'vm-dialog-title'
        });

        const titleText = el('span', {
            id: 'vm-dialog-title',
            textContent: opts.title || t(I18N_KEYS.confirm_clean_title)
        });

        const titleRow = el('div', { className: 'vm-dialog-title-row' },
            el('span', { className: 'material-symbols-outlined' }, opts.icon || 'warning'),
            titleText
        );

        const desc = el('p', { className: 'vm-dialog-desc', textContent: opts.message || '' });
        box.appendChild(titleRow);
        box.appendChild(desc);

        if (opts.warning) {
            const warnBox = el('div', { className: 'vm-maintenance-notice vm-maintenance-notice--warning' },
                el('span', { className: 'material-symbols-outlined' }, 'warning'),
                el('span', { textContent: opts.warning })
            );
            box.appendChild(warnBox);
        }

        const footer = el('div', { className: 'vm-dialog-footer' });
        const cancelBtn = el('button', {
            type: 'button',
            id: 'vm-dialog-cancel-btn',
            className: 'vm-maintenance-btn',
            textContent: opts.cancelLabel || t(I18N_KEYS.btn_cancel),
            onClick: () => {
                closeConfirmModal();
                if (typeof opts.onCancel === 'function') opts.onCancel();
            }
        });

        const confirmBtn = el('button', {
            type: 'button',
            id: 'vm-dialog-confirm-btn',
            className: opts.danger ? 'vm-maintenance-btn vm-maintenance-btn--danger' : 'vm-maintenance-btn vm-maintenance-btn--primary',
            textContent: opts.confirmLabel || t(I18N_KEYS.btn_confirm),
            onClick: () => {
                closeConfirmModal();
                if (typeof opts.onConfirm === 'function') opts.onConfirm();
            }
        });

        footer.appendChild(cancelBtn);
        footer.appendChild(confirmBtn);
        box.appendChild(footer);
        scrim.appendChild(box);

        scrim.addEventListener('click', e => {
            if (e.target === scrim) {
                closeConfirmModal();
                if (typeof opts.onCancel === 'function') opts.onCancel();
            }
        });

        modalKeydownHandler = function (e) {
            if (e.key === 'Escape') {
                e.preventDefault();
                e.stopPropagation();
                closeConfirmModal();
                if (typeof opts.onCancel === 'function') opts.onCancel();
                return;
            }

            if (e.key === 'Tab') {
                const focusable = box.querySelectorAll('button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])');
                const focusableArr = Array.from(focusable || []).filter(n => !n.disabled && !n.hidden);
                if (focusableArr.length === 0) {
                    e.preventDefault();
                    return;
                }
                const first = focusableArr[0];
                const last = focusableArr[focusableArr.length - 1];

                if (e.shiftKey) {
                    if (document.activeElement === first || (box.contains && !box.contains(document.activeElement))) {
                        e.preventDefault();
                        last.focus();
                    }
                } else {
                    if (document.activeElement === last || (box.contains && !box.contains(document.activeElement))) {
                        e.preventDefault();
                        first.focus();
                    }
                }
            }
        };

        document.addEventListener('keydown', modalKeydownHandler, true);

        document.body.appendChild(scrim);
        state.modal = scrim;
        confirmBtn.focus();
    }

    function closeConfirmModal() {
        if (modalKeydownHandler) {
            document.removeEventListener('keydown', modalKeydownHandler, true);
            modalKeydownHandler = null;
        }
        if (state.modal) {
            const parent = state.modal.parentElement || state.modal.parentNode;
            if (parent && typeof parent.removeChild === 'function') {
                parent.removeChild(state.modal);
            }
        }
        state.modal = null;

        if (previousActiveElement && typeof previousActiveElement.focus === 'function') {
            const elToRestore = previousActiveElement;
            previousActiveElement = null;
            try {
                elToRestore.focus();
            } catch (_) {}
        } else {
            previousActiveElement = null;
        }
    }

    // =========================================================================
    // Polling & Operation Coordination
    // =========================================================================
    let currentPollingOpId = null;
    let pollingGeneration = 0;

    function startPolling(opId) {
        stopPolling();
        if (!opId) return;
        currentPollingOpId = opId;
        const generation = ++pollingGeneration;

        function scheduleNext(delay = 1000) {
            if (generation !== pollingGeneration) return;
            state.operationPollingTimer = setTimeout(pollTick, delay);
        }

        async function pollTick() {
            state.operationPollingTimer = null;
            if (generation !== pollingGeneration) return;
            if (!state.active || (typeof document !== 'undefined' && document.hidden)) {
                stopPolling();
                return;
            }
            if (!state.activeOperation || state.activeOperation.id !== opId || state.activeOperation.status !== 'running') {
                stopPolling();
                return;
            }

            try {
                const op = await callHost('maintenance.status', { operationId: opId });
                if (generation !== pollingGeneration || !state.active || (typeof document !== 'undefined' && document.hidden)) {
                    return;
                }
                handleOperationUpdate(op);
                if (op && op.status === 'running') {
                    scheduleNext(1000);
                } else {
                    stopPolling();
                }
            } catch (err) {
                console.error('maintenance.status failed', err);
                if (generation === pollingGeneration && state.active && !(typeof document !== 'undefined' && document.hidden)) {
                    scheduleNext(2000);
                }
            }
        }

        scheduleNext(1000);
    }

    function stopPolling() {
        pollingGeneration++;
        if (state.operationPollingTimer) {
            clearTimeout(state.operationPollingTimer);
            state.operationPollingTimer = null;
        }
        currentPollingOpId = null;
    }

    function handleOperationUpdate(op) {
        if (!op) return;
        const previous = state.activeOperation;
        state.activeOperation = op;

        if (op.kind === 'registry') {
            state.registryOp = op;
        }

        if (op.status !== 'running') {
            stopPolling();
        }

        // Avoid DOM update if state fingerprint hasn't changed
        const currentFp = opFingerprint(op);
        if (currentFp === state.lastRenderedOpFingerprint) {
            return;
        }
        state.lastRenderedOpFingerprint = currentFp;

        if (state.subview === 'apps') {
            renderAppsView();
        } else if (state.subview === 'registry') {
            renderRegistryView();
        } else if (state.subview === 'backup') {
            renderBackupView();
        }

        // If an uninstall operation finished, refresh the apps list
        if (previous && previous.status === 'running' && op.status === 'completed' && op.kind === 'uninstall') {
            loadApps(true);
        }
        // If a restore operation finished, refresh the backups list
        if (previous && previous.status === 'running' && op.status === 'completed' && op.kind === 'restore') {
            loadBackups(true);
        }
    }

    // =========================================================================
    // Subtab: Apps
    // =========================================================================
    async function loadApps(force) {
        if (state.appsLoading) return;
        if (state.appsLoaded && !force) return;

        state.appsLoading = true;
        state.appsError = null;
        renderAppsView();

        try {
            const data = await callHost('maintenance.getApps', {});
            state.apps = Array.isArray(data?.apps) ? data.apps : [];
            state.appsWarnings = Array.isArray(data?.warnings) ? data.warnings : [];
            state.appsLoaded = true;
        } catch (err) {
            state.appsError = err.message || String(err);
        } finally {
            state.appsLoading = false;
            renderAppsView();
        }
    }

    function getFilteredAndSortedApps() {
        const query = (state.appsSearch || '').trim().toLowerCase();
        let list = state.apps.filter(app => {
            if (!query) return true;
            const name = (app.name || '').toLowerCase();
            const publisher = (app.publisher || '').toLowerCase();
            const loc = (app.installLocation || '').toLowerCase();
            return name.includes(query) || publisher.includes(query) || loc.includes(query);
        });

        const sortMode = state.appsSort;
        list.sort((a, b) => {
            if (sortMode === 'name_asc') {
                return (a.name || '').localeCompare(b.name || '');
            } else if (sortMode === 'name_desc') {
                return (b.name || '').localeCompare(a.name || '');
            } else if (sortMode === 'size_desc') {
                return (b.estimatedSizeKb || 0) - (a.estimatedSizeKb || 0);
            } else if (sortMode === 'size_asc') {
                return (a.estimatedSizeKb || 0) - (b.estimatedSizeKb || 0);
            } else if (sortMode === 'publisher_asc') {
                return (a.publisher || '').localeCompare(b.publisher || '');
            }
            return 0;
        });

        return list;
    }

    function updateAppsListOnly() {
        const countEl = document.getElementById('vm-apps-count');
        const listContainer = document.getElementById('vm-apps-list-container');
        if (!listContainer) return;

        const filteredApps = getFilteredAndSortedApps();
        if (countEl) {
            countEl.textContent = state.appsLoading
                ? t(I18N_KEYS.apps_loading)
                : t(I18N_KEYS.apps_count, { count: filteredApps.length });
        }

        listContainer.replaceChildren();

        if (state.appsLoading && !state.apps.length) {
            listContainer.appendChild(el('div', {
                className: 'py-6 text-center text-on-surface-variant',
                textContent: t(I18N_KEYS.apps_loading)
            }));
        } else if (state.appsError) {
            listContainer.appendChild(el('div', {
                className: 'vm-maintenance-notice vm-maintenance-notice--warning',
                textContent: state.appsError
            }));
        } else if (!filteredApps.length) {
            listContainer.appendChild(el('div', {
                className: 'py-6 text-center text-on-surface-variant opacity-75',
                textContent: t(I18N_KEYS.apps_empty)
            }));
        } else {
            const listWrap = el('div', { className: 'vm-maintenance-list', role: 'list' });
            for (const app of filteredApps) {
                const card = renderAppCard(app);
                listWrap.appendChild(card);
            }
            listContainer.appendChild(listWrap);
        }
    }

    function renderAppsView() {
        const container = document.getElementById('vm-system-apps');
        if (!container) return;

        let shell = container.querySelector('.vm-maintenance-shell');
        if (!shell) {
            container.replaceChildren();
            shell = el('div', { className: 'vm-maintenance-shell' });

            // Toolbar
            const toolbar = el('div', { className: 'vm-maintenance-toolbar' });

            // Search input
            const searchWrap = el('div', { className: 'vm-maintenance-search-wrap' },
                el('span', { className: 'material-symbols-outlined' }, 'search'),
                el('input', {
                    type: 'text',
                    id: 'vm-apps-search',
                    className: 'vm-maintenance-search-input',
                    placeholder: t(I18N_KEYS.apps_search_input),
                    'aria-label': t(I18N_KEYS.apps_search_input),
                    value: state.appsSearch,
                    onInput: e => {
                        state.appsSearch = e.target.value;
                        updateAppsListOnly();
                    }
                })
            );

            // Actions: Sort, Refresh, Windows Settings
            const actions = el('div', { className: 'vm-maintenance-actions' });

            const sortSelect = el('select', {
                id: 'vm-apps-sort',
                className: 'vm-maintenance-select',
                'aria-label': t(I18N_KEYS.apps_sort_label),
                onChange: e => {
                    state.appsSort = e.target.value;
                    updateAppsListOnly();
                }
            },
                el('option', { value: 'name_asc', selected: state.appsSort === 'name_asc', textContent: t(I18N_KEYS.apps_sort_name_asc) }),
                el('option', { value: 'name_desc', selected: state.appsSort === 'name_desc', textContent: t(I18N_KEYS.apps_sort_name_desc) }),
                el('option', { value: 'size_desc', selected: state.appsSort === 'size_desc', textContent: t(I18N_KEYS.apps_sort_size_desc) }),
                el('option', { value: 'size_asc', selected: state.appsSort === 'size_asc', textContent: t(I18N_KEYS.apps_sort_size_asc) }),
                el('option', { value: 'publisher_asc', selected: state.appsSort === 'publisher_asc', textContent: t(I18N_KEYS.apps_sort_publisher) })
            );

            const refreshBtn = el('button', {
                type: 'button',
                id: 'vm-apps-refresh',
                className: 'vm-maintenance-btn',
                onClick: () => loadApps(true)
            },
                el('span', { className: 'material-symbols-outlined' }, 'refresh'),
                el('span', { textContent: t(I18N_KEYS.apps_refresh) })
            );

            const settingsBtn = el('button', {
                type: 'button',
                id: 'vm-apps-open-settings',
                className: 'vm-maintenance-btn',
                onClick: async () => {
                    try {
                        await callHost('maintenance.openAppsSettings', {});
                    } catch (err) {
                        console.error('openAppsSettings failed', err);
                    }
                }
            },
                el('span', { className: 'material-symbols-outlined' }, 'settings'),
                el('span', { textContent: t(I18N_KEYS.apps_open_settings) })
            );

            actions.appendChild(sortSelect);
            actions.appendChild(refreshBtn);
            actions.appendChild(settingsBtn);
            toolbar.appendChild(searchWrap);
            toolbar.appendChild(actions);
            shell.appendChild(toolbar);

            const warningsWrap = el('div', { id: 'vm-apps-warnings' });
            shell.appendChild(warningsWrap);

            const opContainer = el('div', { id: 'vm-apps-op-container' });
            shell.appendChild(opContainer);

            const countRow = el('div', { className: 'vm-apps-meta-row' },
                el('span', {
                    id: 'vm-apps-count',
                    className: 'text-label-sm text-on-surface-variant font-medium'
                })
            );
            shell.appendChild(countRow);

            const listContainer = el('div', { id: 'vm-apps-list-container' });
            shell.appendChild(listContainer);

            container.appendChild(shell);
        } else {
            const searchInput = document.getElementById('vm-apps-search');
            if (searchInput && document.activeElement !== searchInput && searchInput.value !== state.appsSearch) {
                searchInput.value = state.appsSearch;
            }
            const sortSelect = document.getElementById('vm-apps-sort');
            if (sortSelect && document.activeElement !== sortSelect && sortSelect.value !== state.appsSort) {
                sortSelect.value = state.appsSort;
            }
        }

        // Host warnings if any
        const warningsWrap = document.getElementById('vm-apps-warnings');
        if (warningsWrap) {
            warningsWrap.replaceChildren();
            if (state.appsWarnings && state.appsWarnings.length) {
                warningsWrap.appendChild(el('div', { className: 'vm-maintenance-notice vm-maintenance-notice--warning' },
                    el('span', { className: 'material-symbols-outlined' }, 'warning'),
                    el('div', {}, ...state.appsWarnings.map(w => el('p', { textContent: String(w) })))
                ));
            }
        }

        // Active operation card (for uninstall or leftovers)
        const opContainer = document.getElementById('vm-apps-op-container');
        if (opContainer) {
            opContainer.replaceChildren();
            if (state.activeOperation && (state.activeOperation.kind === 'uninstall' || state.activeOperation.kind === 'leftovers')) {
                const opCard = renderOperationCard(state.activeOperation, () => renderAppsView());
                opContainer.appendChild(opCard);
            }
        }

        updateAppsListOnly();
    }

    function renderAppCard(app) {
        const card = el('article', { className: 'vm-app-card', role: 'listitem' });

        const icon = el('div', { className: 'vm-app-icon' },
            el('span', { className: 'material-symbols-outlined' }, 'apps')
        );

        const info = el('div', { className: 'vm-app-info' });
        const nameRow = el('div', { className: 'vm-app-name-row' },
            el('span', { className: 'vm-app-name', textContent: app.name || '--' })
        );

        if (!app.canUninstall) {
            nameRow.appendChild(el('span', {
                className: 'vm-badge vm-badge--status',
                textContent: app.reason || t(I18N_KEYS.apps_protected)
            }));
        }

        const metaRow = el('div', { className: 'vm-app-meta' },
            el('span', { textContent: (app.publisher || '') }),
            el('span', { textContent: app.version ? 'v' + app.version : '' }),
            el('span', { textContent: formatSize(app.estimatedSizeKb) })
        );

        info.appendChild(nameRow);
        info.appendChild(metaRow);

        if (app.installLocation) {
            info.appendChild(el('div', {
                className: 'vm-app-location',
                title: app.installLocation,
                textContent: app.installLocation
            }));
        }

        const actions = el('div', { className: 'vm-app-actions' });
        if (app.canUninstall) {
            const isThisOpRunning = state.activeOperation && state.activeOperation.appId === app.id && state.activeOperation.status === 'running';
            const uninstallBtn = el('button', {
                type: 'button',
                className: 'vm-maintenance-btn vm-maintenance-btn--danger',
                disabled: isThisOpRunning,
                onClick: () => promptUninstall(app)
            },
                el('span', { className: 'material-symbols-outlined' }, 'delete'),
                el('span', { textContent: t(I18N_KEYS.apps_uninstall) })
            );
            actions.appendChild(uninstallBtn);
        } else {
            const settingsLink = el('button', {
                type: 'button',
                className: 'vm-maintenance-btn',
                onClick: () => callHost('maintenance.openAppsSettings', {}).catch(() => {})
            },
                el('span', { className: 'material-symbols-outlined' }, 'open_in_new'),
                el('span', { textContent: t(I18N_KEYS.apps_open_settings) })
            );
            actions.appendChild(settingsLink);
        }

        card.appendChild(icon);
        card.appendChild(info);
        card.appendChild(actions);
        return card;
    }

    function promptUninstall(app) {
        showConfirmModal({
            title: t(I18N_KEYS.confirm_uninstall_title),
            message: t(I18N_KEYS.confirm_uninstall_desc, { name: app.name || '' }),
            warning: t(I18N_KEYS.confirm_uninstall_warning),
            confirmLabel: t(I18N_KEYS.apps_uninstall),
            danger: true,
            icon: 'delete_sweep',
            onConfirm: async () => {
                try {
                    const op = await callHost('maintenance.start', {
                        kind: 'uninstall',
                        appId: app.id
                    });
                    state.selectedItemIds.clear();
                    handleOperationUpdate(op);
                    if (op && op.status === 'running') {
                        startPolling(op.id);
                    }
                } catch (err) {
                    console.error('start uninstall failed', err);
                    showError(err);
                }
            }
        });
    }

    // =========================================================================
    // Generic Operation Card Renderer (for active uninstall, leftovers, registry)
    // =========================================================================
    function renderOperationCard(op, onRefresh) {
        const card = el('div', { className: 'vm-maintenance-op-card' });

        const statusKey = 'op_status_' + (op.status || 'unknown');
        const statusLabel = t(I18N_KEYS[statusKey] || I18N_KEYS.op_status_unknown);

        const header = el('div', { className: 'vm-maintenance-op-header' },
            el('div', { className: 'vm-maintenance-op-title' },
                el('span', { className: 'material-symbols-outlined' }, op.status === 'running' ? 'sync' : 'info'),
                el('span', { textContent: statusLabel })
            ),
            el('span', {
                className: 'text-label-sm text-on-surface-variant font-mono',
                textContent: (op.processed != null && op.total != null && op.total > 0)
                    ? `${op.processed} / ${op.total}`
                    : ''
            })
        );
        card.appendChild(header);

        if (op.message) {
            card.appendChild(el('p', {
                className: 'text-body-sm text-on-surface-variant',
                textContent: op.message
            }));
        }

        // Progress bar
        if (op.status === 'running' && op.total != null && op.total > 0) {
            const pct = Math.max(0, Math.min(100, Math.round((op.processed / op.total) * 100)));
            const track = el('div', { className: 'vm-maintenance-op-progress-track' },
                el('div', {
                    className: 'vm-maintenance-op-progress-bar',
                    style: `width: ${pct}%`
                })
            );
            card.appendChild(track);
        }

        // Action buttons based on status
        const actions = el('div', { className: 'vm-maintenance-op-actions' });

        if (op.status === 'running') {
            const cancelBtn = el('button', {
                type: 'button',
                className: 'vm-maintenance-btn',
                onClick: async () => {
                    try {
                        const updated = await callHost('maintenance.cancel', { operationId: op.id });
                        handleOperationUpdate(updated);
                    } catch (err) {
                        console.error('cancel failed', err);
                    }
                }
            },
                el('span', { className: 'material-symbols-outlined' }, 'cancel'),
                el('span', { textContent: t(I18N_KEYS.btn_cancel) })
            );
            actions.appendChild(cancelBtn);
        } else if (op.status === 'needsVerification' || op.status === 'unknown') {
            card.appendChild(el('div', {
                className: 'vm-maintenance-notice',
                textContent: t(I18N_KEYS.verify_notice)
            }));
            const verifyBtn = el('button', {
                type: 'button',
                className: 'vm-maintenance-btn vm-maintenance-btn--primary',
                onClick: async () => {
                    try {
                        const updated = await callHost('maintenance.verifyUninstall', { operationId: op.id });
                        handleOperationUpdate(updated);
                    } catch (err) {
                        console.error('verifyUninstall failed', err);
                    }
                }
            },
                el('span', { className: 'material-symbols-outlined' }, 'verified'),
                el('span', { textContent: t(I18N_KEYS.btn_verify) })
            );
            actions.appendChild(verifyBtn);
        } else if (op.status === 'rebootRequired') {
            card.appendChild(el('div', {
                className: 'vm-maintenance-notice vm-maintenance-notice--warning',
                textContent: t(I18N_KEYS.reboot_notice)
            }));
        }

        // If completed uninstall and leftovers can be scanned
        if (op.status === 'completed' && op.kind === 'uninstall' && op.canScanLeftovers) {
            const leftoversBtn = el('button', {
                type: 'button',
                className: 'vm-maintenance-btn vm-maintenance-btn--primary',
                onClick: async () => {
                    try {
                        const leftOp = await callHost('maintenance.start', {
                            kind: 'leftovers',
                            appId: op.appId
                        });
                        state.selectedItemIds.clear();
                        handleOperationUpdate(leftOp);
                        if (leftOp && leftOp.status === 'running') {
                            startPolling(leftOp.id);
                        }
                    } catch (err) {
                        console.error('start leftovers failed', err);
                    }
                }
            },
                el('span', { className: 'material-symbols-outlined' }, 'search'),
                el('span', { textContent: t(I18N_KEYS.btn_scan_leftovers) })
            );
            actions.appendChild(leftoversBtn);
        }

        if (actions.children.length) {
            card.appendChild(actions);
        }

        // Leftover items list (when kind === 'leftovers')
        if (op.kind === 'leftovers' && op.items && op.items.length) {
            const itemsPanel = renderItemsList(op, state.selectedItemIds, onRefresh);
            card.appendChild(itemsPanel);
        } else if (op.kind === 'leftovers' && op.status === 'completed' && (!op.items || !op.items.length)) {
            card.appendChild(el('p', {
                className: 'text-body-sm text-on-surface-variant opacity-75 py-2',
                textContent: t(I18N_KEYS.leftovers_empty)
            }));
        }

        return card;
    }

    // =========================================================================
    // Generic Items List (Leftovers & Registry)
    // =========================================================================
    function renderItemsList(op, selectedSet, onRefresh) {
        const wrap = el('div', { className: 'vm-maintenance-shell mt-2' });

        const items = Array.isArray(op.items) ? op.items : [];
        const total = items.length;
        const selectedCount = selectedSet.size;

        const hint = el('p', {
            className: 'text-body-sm text-on-surface-variant',
            textContent: t(I18N_KEYS.leftovers_hint)
        });
        wrap.appendChild(hint);

        // Results toolbar: selection counter, exclude, clean
        const toolbar = el('div', { className: 'vm-maintenance-toolbar' });
        const counter = el('span', {
            className: 'text-label-sm text-on-surface-variant font-medium',
            textContent: t(I18N_KEYS.item_selected_count, { selected: selectedCount, total })
        });
        toolbar.appendChild(counter);

        const actions = el('div', { className: 'vm-maintenance-actions' });

        const excludeBtn = el('button', {
            type: 'button',
            className: 'vm-maintenance-btn',
            disabled: selectedCount === 0,
            onClick: async () => {
                if (selectedCount === 0) return;
                const itemIds = Array.from(selectedSet);
                try {
                    await callHost('maintenance.exclude', { operationId: op.id, itemIds });
                    selectedSet.clear();
                    const updated = await callHost('maintenance.status', { operationId: op.id });
                    handleOperationUpdate(updated);
                } catch (err) {
                    console.error('exclude failed', err);
                }
            }
        },
            el('span', { className: 'material-symbols-outlined' }, 'block'),
            el('span', { textContent: t(I18N_KEYS.btn_exclude_selected) })
        );

        const cleanBtn = el('button', {
            type: 'button',
            className: 'vm-maintenance-btn vm-maintenance-btn--danger',
            disabled: selectedCount === 0,
            onClick: () => {
                if (selectedCount === 0) return;
                const itemIds = Array.from(selectedSet);
                const hasUserData = items.some(it => selectedSet.has(it.id) && it.containsUserData);
                showConfirmModal({
                    title: t(I18N_KEYS.confirm_clean_title),
                    message: t(I18N_KEYS.confirm_clean_desc, { count: selectedCount }),
                    warning: hasUserData ? t(I18N_KEYS.confirm_clean_user_data_warn) : null,
                    confirmLabel: t(I18N_KEYS.btn_confirm),
                    danger: true,
                    icon: 'cleaning_services',
                    onConfirm: async () => {
                        try {
                            const applyOp = await callHost('maintenance.apply', {
                                operationId: op.id,
                                itemIds
                            });
                            selectedSet.clear();
                            handleOperationUpdate(applyOp);
                            if (applyOp && applyOp.status === 'running') {
                                startPolling(applyOp.id);
                            }
                        } catch (err) {
                            console.error('apply failed', err);
                            showError(err);
                        }
                    }
                });
            }
        },
            el('span', { className: 'material-symbols-outlined' }, 'delete_sweep'),
            el('span', { textContent: t(I18N_KEYS.btn_clean_selected, { count: selectedCount }) })
        );

        actions.appendChild(excludeBtn);
        actions.appendChild(cleanBtn);
        toolbar.appendChild(actions);
        wrap.appendChild(toolbar);

        // List of items
        const list = el('div', { className: 'vm-maintenance-list' });
        for (const item of items) {
            const isChecked = selectedSet.has(item.id);
            const isSelectable = item.selectable !== false;

            const row = el('div', { className: 'vm-item-row' });

            const checkbox = el('input', {
                type: 'checkbox',
                className: 'vm-item-checkbox',
                disabled: !isSelectable,
                checked: isChecked,
                onChange: e => {
                    if (e.target.checked) selectedSet.add(item.id);
                    else selectedSet.delete(item.id);
                    if (typeof onRefresh === 'function') onRefresh();
                }
            });

            // Kind badge
            const kindKey = 'item_kind_' + (item.kind || 'file');
            const kindLabel = t(I18N_KEYS[kindKey] || I18N_KEYS.item_kind_file);
            const kindBadge = el('span', { className: 'vm-badge vm-badge--kind', textContent: kindLabel });

            // Content
            const content = el('div', { className: 'vm-item-content' });
            content.appendChild(el('div', { className: 'vm-item-path', textContent: item.path || '--' }));

            if (item.reason) {
                content.appendChild(el('div', { className: 'vm-item-reason', textContent: item.reason }));
            }

            if (item.containsUserData) {
                content.appendChild(el('span', { className: 'vm-badge vm-badge--warning' },
                    el('span', { className: 'material-symbols-outlined' }, 'warning'),
                    el('span', { textContent: t(I18N_KEYS.item_user_data) })
                ));
            }

            row.appendChild(checkbox);
            row.appendChild(kindBadge);
            row.appendChild(content);
            list.appendChild(row);
        }
        wrap.appendChild(list);

        return wrap;
    }

    // =========================================================================
    // Subtab: Registry
    // =========================================================================
    function renderRegistryView() {
        const container = document.getElementById('vm-system-registry');
        if (!container) return;

        container.replaceChildren();

        const shell = el('div', { className: 'vm-maintenance-shell' });

        // Disclaimer / intro banner
        const disclaimerBox = el('div', { className: 'vm-maintenance-notice' },
            el('span', { className: 'material-symbols-outlined' }, 'shield'),
            el('div', {},
                el('strong', { className: 'block font-semibold mb-1', textContent: t(I18N_KEYS.registry_title) }),
                el('p', { textContent: t(I18N_KEYS.registry_disclaimer) })
            )
        );
        shell.appendChild(disclaimerBox);

        // Action toolbar
        const toolbar = el('div', { className: 'vm-maintenance-toolbar' });
        const scanBtn = el('button', {
            type: 'button',
            id: 'vm-registry-scan-btn',
            className: 'vm-maintenance-btn vm-maintenance-btn--primary',
            disabled: state.registryOp && state.registryOp.status === 'running',
            onClick: async () => {
                try {
                    const op = await callHost('maintenance.start', { kind: 'registry' });
                    state.registrySelectedItemIds.clear();
                    handleOperationUpdate(op);
                    if (op && op.status === 'running') {
                        startPolling(op.id);
                    }
                } catch (err) {
                    console.error('start registry scan failed', err);
                    showError(err);
                }
            }
        },
            el('span', { className: 'material-symbols-outlined' }, 'search'),
            el('span', { textContent: t(I18N_KEYS.registry_scan_btn) })
        );
        toolbar.appendChild(scanBtn);
        shell.appendChild(toolbar);

        // If an operation is running or completed
        if (state.registryOp) {
            const opCard = renderOperationCard(state.registryOp, () => renderRegistryView());
            shell.appendChild(opCard);

            if (state.registryOp.status === 'completed') {
                if (state.registryOp.items && state.registryOp.items.length) {
                    const itemsPanel = renderItemsList(state.registryOp, state.registrySelectedItemIds, () => renderRegistryView());
                    shell.appendChild(itemsPanel);
                } else {
                    shell.appendChild(el('div', {
                        className: 'py-6 text-center text-on-surface-variant opacity-75',
                        textContent: t(I18N_KEYS.registry_clean)
                    }));
                }
            }
        }

        container.appendChild(shell);
    }

    // =========================================================================
    // Subtab: Backup & Restore
    // =========================================================================
    async function loadBackups(force) {
        if (state.backupsLoading) return;
        if (state.backupsLoaded && !force) return;

        state.backupsLoading = true;
        state.backupsError = null;
        renderBackupView();

        try {
            const data = await callHost('maintenance.getBackups', {});
            state.backups = Array.isArray(data?.backups) ? data.backups : [];
            state.backupsLoaded = true;
        } catch (err) {
            state.backupsError = err.message || String(err);
        } finally {
            state.backupsLoading = false;
            renderBackupView();
        }
    }

    async function openBackupDetail(backupId) {
        state.selectedBackupLoading = true;
        state.selectedBackupItemIds.clear();
        renderBackupView();
        try {
            const detail = await callHost('maintenance.getBackup', { backupId });
            state.selectedBackup = detail;
        } catch (err) {
            console.error('getBackup failed', err);
            showError(err);
        } finally {
            state.selectedBackupLoading = false;
            renderBackupView();
        }
    }

    function renderBackupView() {
        const container = document.getElementById('vm-system-backup');
        if (!container) return;

        container.replaceChildren();

        const shell = el('div', { className: 'vm-maintenance-shell' });

        // Disclaimer banner: backup does NOT reinstall apps
        const disclaimerBox = el('div', { className: 'vm-maintenance-notice' },
            el('span', { className: 'material-symbols-outlined' }, 'info'),
            el('p', { textContent: t(I18N_KEYS.backup_disclaimer) })
        );
        shell.appendChild(disclaimerBox);

        // Active restore operation card in main backup list view
        if (state.activeOperation && state.activeOperation.kind === 'restore') {
            const opCard = renderOperationCard(state.activeOperation, () => renderBackupView());
            shell.appendChild(opCard);
        }

        // If a backup detail view is currently open
        if (state.selectedBackup) {
            shell.appendChild(renderBackupDetailView());
            container.appendChild(shell);
            return;
        }

        // Toolbar
        const toolbar = el('div', { className: 'vm-maintenance-toolbar' });
        const refreshBtn = el('button', {
            type: 'button',
            className: 'vm-maintenance-btn',
            onClick: () => loadBackups(true)
        },
            el('span', { className: 'material-symbols-outlined' }, 'refresh'),
            el('span', { textContent: t(I18N_KEYS.backup_refresh) })
        );
        toolbar.appendChild(refreshBtn);
        shell.appendChild(toolbar);

        if (state.backupsLoading && !state.backups.length) {
            shell.appendChild(el('div', {
                className: 'py-6 text-center text-on-surface-variant',
                textContent: t(I18N_KEYS.apps_loading)
            }));
        } else if (state.backupsError) {
            shell.appendChild(el('div', {
                className: 'vm-maintenance-notice vm-maintenance-notice--warning',
                textContent: state.backupsError
            }));
        } else if (!state.backups.length) {
            shell.appendChild(el('div', {
                className: 'py-6 text-center text-on-surface-variant opacity-75',
                textContent: t(I18N_KEYS.backup_empty)
            }));
        } else {
            const list = el('div', { className: 'vm-maintenance-list', role: 'list' });
            for (const backup of state.backups) {
                const card = el('article', { className: 'vm-app-card', role: 'listitem' });

                const icon = el('div', { className: 'vm-app-icon' },
                    el('span', { className: 'material-symbols-outlined' }, 'settings_backup_restore')
                );

                const info = el('div', { className: 'vm-app-info' },
                    el('div', { className: 'vm-app-name', textContent: backup.description || '--' }),
                    el('div', { className: 'vm-app-meta' },
                        el('span', { textContent: formatDate(backup.createdAt) }),
                        el('span', { textContent: t(I18N_KEYS.backup_items_count, { count: backup.itemCount || 0 }) }),
                        el('span', { className: 'vm-badge vm-badge--status', textContent: backup.status || '' })
                    )
                );

                const actions = el('div', { className: 'vm-app-actions' },
                    el('button', {
                        type: 'button',
                        className: 'vm-maintenance-btn vm-maintenance-btn--primary',
                        onClick: () => openBackupDetail(backup.id)
                    },
                        el('span', { className: 'material-symbols-outlined' }, 'visibility'),
                        el('span', { textContent: t(I18N_KEYS.btn_details) })
                    ),
                    el('button', {
                        type: 'button',
                        className: 'vm-maintenance-btn vm-maintenance-btn--danger',
                        onClick: () => {
                            showConfirmModal({
                                title: t(I18N_KEYS.confirm_delete_backup_title),
                                message: t(I18N_KEYS.confirm_delete_backup_desc),
                                confirmLabel: t(I18N_KEYS.btn_delete),
                                danger: true,
                                icon: 'delete_forever',
                                onConfirm: async () => {
                                    try {
                                        await callHost('maintenance.deleteBackup', { backupId: backup.id });
                                        loadBackups(true);
                                    } catch (err) {
                                        console.error('deleteBackup failed', err);
                                        showError(err);
                                    }
                                }
                            });
                        }
                    },
                        el('span', { className: 'material-symbols-outlined' }, 'delete'),
                        el('span', { textContent: t(I18N_KEYS.btn_delete) })
                    )
                );

                card.appendChild(icon);
                card.appendChild(info);
                card.appendChild(actions);
                list.appendChild(card);
            }
            shell.appendChild(list);
        }

        container.appendChild(shell);
    }

    function renderBackupDetailView() {
        const detail = state.selectedBackup;
        const wrap = el('div', { className: 'vm-maintenance-shell' });

        const headerToolbar = el('div', { className: 'vm-maintenance-toolbar' });
        const backBtn = el('button', {
            type: 'button',
            className: 'vm-maintenance-btn',
            onClick: () => {
                state.selectedBackup = null;
                renderBackupView();
            }
        },
            el('span', { className: 'material-symbols-outlined' }, 'arrow_back'),
            el('span', { textContent: t(I18N_KEYS.btn_close) })
        );

        const title = el('h3', {
            className: 'text-title-md font-semibold text-on-surface',
            textContent: t(I18N_KEYS.backup_detail_title, { desc: detail.description || '' })
        });

        headerToolbar.appendChild(backBtn);
        headerToolbar.appendChild(title);
        wrap.appendChild(headerToolbar);

        // Active restore operation card in detail view
        if (state.activeOperation && state.activeOperation.kind === 'restore') {
            const opCard = renderOperationCard(state.activeOperation, () => renderBackupView());
            wrap.appendChild(opCard);
        }

        const items = Array.isArray(detail.items) ? detail.items : [];
        const selectedCount = state.selectedBackupItemIds.size;

        const actionToolbar = el('div', { className: 'vm-maintenance-toolbar' },
            el('span', {
                className: 'text-label-sm text-on-surface-variant font-medium',
                textContent: t(I18N_KEYS.item_selected_count, { selected: selectedCount, total: items.length })
            }),
            el('button', {
                type: 'button',
                className: 'vm-maintenance-btn vm-maintenance-btn--primary',
                disabled: selectedCount === 0,
                onClick: () => {
                    if (selectedCount === 0) return;
                    const itemIds = Array.from(state.selectedBackupItemIds);
                    showConfirmModal({
                        title: t(I18N_KEYS.confirm_restore_title),
                        message: t(I18N_KEYS.confirm_restore_desc, { count: selectedCount }),
                        confirmLabel: t(I18N_KEYS.btn_confirm),
                        icon: 'settings_backup_restore',
                        onConfirm: async () => {
                            try {
                                const restoreOp = await callHost('maintenance.restore', {
                                    backupId: detail.id,
                                    itemIds
                                });
                                state.selectedBackupItemIds.clear();
                                handleOperationUpdate(restoreOp);
                                if (restoreOp && restoreOp.status === 'running') {
                                    startPolling(restoreOp.id);
                                }
                            } catch (err) {
                                console.error('restore failed', err);
                                showError(err);
                            }
                        }
                    });
                }
            },
                el('span', { className: 'material-symbols-outlined' }, 'settings_backup_restore'),
                el('span', { textContent: t(I18N_KEYS.btn_restore_selected, { count: selectedCount }) })
            )
        );
        wrap.appendChild(actionToolbar);

        const list = el('div', { className: 'vm-maintenance-list' });
        for (const item of items) {
            const row = el('div', { className: 'vm-item-row' });
            const isChecked = state.selectedBackupItemIds.has(item.id);

            const checkbox = el('input', {
                type: 'checkbox',
                className: 'vm-item-checkbox',
                checked: isChecked,
                onChange: e => {
                    if (e.target.checked) state.selectedBackupItemIds.add(item.id);
                    else state.selectedBackupItemIds.delete(item.id);
                    renderBackupView();
                }
            });

            const kindKey = 'item_kind_' + (item.kind || 'file');
            const kindBadge = el('span', { className: 'vm-badge vm-badge--kind', textContent: t(I18N_KEYS[kindKey] || I18N_KEYS.item_kind_file) });

            const content = el('div', { className: 'vm-item-content' },
                el('div', { className: 'vm-item-path', textContent: item.path || '--' }),
                item.reason ? el('div', { className: 'vm-item-reason', textContent: item.reason }) : null,
                item.status ? el('span', { className: 'vm-badge vm-badge--status', textContent: item.status }) : null
            );

            row.appendChild(checkbox);
            row.appendChild(kindBadge);
            row.appendChild(content);
            list.appendChild(row);
        }
        wrap.appendChild(list);

        return wrap;
    }

    function onVisibilityChange() {
        if (typeof document !== 'undefined' && document.hidden) {
            stopPolling();
        } else if (state.active && state.activeOperation && state.activeOperation.status === 'running') {
            callHost('maintenance.status', { operationId: state.activeOperation.id })
                .then(op => {
                    if (state.active && !(typeof document !== 'undefined' && document.hidden)) {
                        handleOperationUpdate(op);
                        if (op && op.status === 'running') {
                            startPolling(op.id);
                        }
                    }
                })
                .catch(err => console.error('visibility refresh failed', err));
        }
    }

    // =========================================================================
    // Public API & Lifecycle
    // =========================================================================
    api.init = function () {
        if (state.initialized) return;
        state.initialized = true;
        if (typeof document !== 'undefined' && document.addEventListener) {
            document.addEventListener('visibilitychange', onVisibilityChange);
        }
    };

    api.activate = function (route) {
        state.active = true;
        const sub = route?.subviews?.['system-tools'] || state.subview || 'apps';
        api.activateSubview(sub);

        // If an operation was active when tab was hidden, refresh its status now
        if (state.activeOperation && state.activeOperation.id) {
            callHost('maintenance.status', { operationId: state.activeOperation.id })
                .then(op => {
                    if (state.active && !(typeof document !== 'undefined' && document.hidden)) {
                        handleOperationUpdate(op);
                        if (op && op.status === 'running') {
                            startPolling(op.id);
                        }
                    }
                })
                .catch(err => console.error('maintenance.status refresh failed on activate', err));
        }
    };

    api.deactivate = function () {
        state.active = false;
        stopPolling();
        closeConfirmModal();
    };

    api.dispose = function () {
        api.deactivate();
        if (typeof document !== 'undefined' && document.removeEventListener) {
            document.removeEventListener('visibilitychange', onVisibilityChange);
        }
        state.initialized = false;
    };

    api.activateSubview = function (subview) {
        if (!['apps', 'registry', 'backup'].includes(subview)) return;
        state.subview = subview;

        if (subview === 'apps') {
            loadApps(false);
            renderAppsView();
        } else if (subview === 'registry') {
            renderRegistryView();
        } else if (subview === 'backup') {
            loadBackups(false);
            renderBackupView();
        }
    };

    api.onSubviewChanged = function (subview) {
        if (['apps', 'registry', 'backup'].includes(subview)) {
            api.activateSubview(subview);
        } else {
            // Left maintenance subtabs (e.g. switched to scheduled/startup/memory)
            stopPolling();
            closeConfirmModal();
        }
    };

    // Subview change listener inside system-tools
    document.addEventListener('voltuisubviewchanged', event => {
        if (event.detail && event.detail.group === 'system-tools') {
            api.onSubviewChanged(event.detail.view);
        }
    });

    // Register with central lifecycle
    if (window.VoltViewLifecycle) {
        window.VoltViewLifecycle.register('maintenance', {
            matches: route => route.view === 'system-tools' && ['apps', 'registry', 'backup'].includes(route.subviews?.['system-tools']),
            init: () => api.init(),
            activate: route => api.activate(route),
            deactivate: route => api.deactivate(route),
            dispose: () => api.dispose()
        });
    }

    // If already active upon script load, initialize and activate immediately
    const currentView = window.VoltUiReorg?.state?.view;
    const currentSub = window.VoltUiReorg?.state?.subviews?.['system-tools'];
    if (currentView === 'system-tools' && ['apps', 'registry', 'backup'].includes(currentSub)) {
        api.init();
        api.activate({ view: 'system-tools', subviews: { 'system-tools': currentSub } });
    }
})();
