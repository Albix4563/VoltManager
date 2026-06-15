/**
 * Installed applications tab: Windows uninstall inventory and launcher.
 */
(function () {
    if (!window.Host || !Host.available) return;

    const labels = {
        it: {
            nav: 'Applicazioni', title: 'Applicazioni installate', sub: 'Gestisci le applicazioni installate su Windows con ricerca, dettagli e disinstallazione guidata.',
            refresh: 'Aggiorna', openSettings: 'Apri impostazioni Windows', search: 'Cerca per nome, editore, versione o percorso…',
            total: 'App rilevate', removable: 'Disinstallabili', visible: 'Risultati visibili', loading: 'Caricamento applicazioni…',
            empty: 'Nessuna applicazione trovata.', noResults: 'Nessun risultato per la ricerca corrente.',
            publisher: 'Editore', version: 'Versione', size: 'Dimensione', installed: 'Installata', source: 'Origine', path: 'Percorso',
            unknownPublisher: 'Editore sconosciuto', notAvailable: 'N/D', uninstall: 'Disinstalla', unavailable: 'Non disponibile',
            confirmTitle: 'Conferma disinstallazione', confirmBody: 'VoltManager avvierà il programma di disinstallazione ufficiale registrato da Windows. La procedura potrebbe richiedere conferma o privilegi amministratore.',
            cancel: 'Annulla', launch: 'Avvia disinstallazione', launched: 'Procedura di disinstallazione avviata. Aggiorna l’elenco dopo il completamento.',
            loadErr: 'Errore durante il caricamento delle applicazioni: ', launchErr: 'Errore durante l’avvio della disinstallazione: '
        },
        en: {
            nav: 'Applications', title: 'Installed applications', sub: 'Manage Windows installed applications with search, details, and guided uninstall.',
            refresh: 'Refresh', openSettings: 'Open Windows settings', search: 'Search by name, publisher, version, or path…',
            total: 'Detected apps', removable: 'Removable', visible: 'Visible results', loading: 'Loading applications…',
            empty: 'No applications found.', noResults: 'No results for the current search.',
            publisher: 'Publisher', version: 'Version', size: 'Size', installed: 'Installed', source: 'Source', path: 'Path',
            unknownPublisher: 'Unknown publisher', notAvailable: 'N/A', uninstall: 'Uninstall', unavailable: 'Unavailable',
            confirmTitle: 'Confirm uninstall', confirmBody: 'VoltManager will launch the official uninstall command registered by Windows. The procedure may ask for confirmation or administrator privileges.',
            cancel: 'Cancel', launch: 'Launch uninstall', launched: 'Uninstall procedure launched. Refresh the list after it completes.',
            loadErr: 'Error loading applications: ', launchErr: 'Error launching uninstall: '
        }
    };

    let applications = [];
    let loaded = false;
    let wired = false;
    let selectedApp = null;

    function t(key) {
        const lang = window.I18n && I18n.getLang ? I18n.getLang() : 'it';
        return (labels[lang] && labels[lang][key]) || labels.it[key] || key;
    }

    function esc(value) {
        const div = document.createElement('div');
        div.textContent = value == null ? '' : String(value);
        return div.innerHTML;
    }

    function escAttr(value) {
        return esc(value).replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    function ensureStyles() {
        if (document.getElementById('installed-apps-styles')) return;
        const style = document.createElement('style');
        style.id = 'installed-apps-styles';
        style.textContent = `
@keyframes installedAppIn{from{opacity:0;transform:translateY(10px) scale(.985)}to{opacity:1;transform:translateY(0) scale(1)}}
.installed-app-card{position:relative;overflow:hidden;border:1px solid rgba(255,255,255,.1);border-radius:18px;background:linear-gradient(135deg,rgba(18,33,49,.72),rgba(10,17,40,.58));padding:18px;display:flex;flex-direction:column;gap:14px;animation:installedAppIn .28s cubic-bezier(.2,.8,.2,1) both;transition:transform .22s ease,border-color .22s ease,box-shadow .22s ease,background .22s ease;}
.installed-app-card:hover{transform:translateY(-2px);border-color:rgba(0,241,254,.24);box-shadow:0 18px 36px rgba(0,0,0,.2),0 0 0 1px rgba(0,241,254,.04);background:linear-gradient(135deg,rgba(18,33,49,.86),rgba(10,17,40,.7));}
.installed-app-card:before{content:"";position:absolute;left:0;top:18px;bottom:18px;width:3px;border-radius:999px;background:rgba(0,241,254,.8);box-shadow:0 0 16px rgba(0,241,254,.48);opacity:.76;}
.installed-app-head{display:flex;align-items:flex-start;justify-content:space-between;gap:14px;min-width:0;}
.installed-app-title{display:flex;align-items:flex-start;gap:12px;min-width:0;}
.installed-app-icon{width:44px;height:44px;border-radius:15px;display:flex;align-items:center;justify-content:center;flex:0 0 auto;background:rgba(0,241,254,.1);border:1px solid rgba(0,241,254,.22);color:#00f1fe;box-shadow:0 0 18px rgba(0,241,254,.08);}
.installed-app-name{font-weight:800;color:#d3deef;line-height:1.2;word-break:break-word;}
.installed-app-publisher{font-size:12px;color:rgba(211,222,239,.62);margin-top:4px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;max-width:100%;}
.installed-app-meta{display:grid;gap:7px;margin-top:2px;}
.installed-app-line{display:flex;gap:8px;min-width:0;font-size:12px;color:rgba(211,222,239,.66);}
.installed-app-line span:first-child{color:rgba(0,241,254,.78);font-weight:800;text-transform:uppercase;letter-spacing:.04em;flex:0 0 auto;}
.installed-app-line span:last-child{min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;}
.installed-app-uninstall{border:1px solid rgba(255,120,120,.24);background:rgba(255,120,120,.08);color:#ffd0d0;border-radius:12px;padding:9px 12px;font-size:12px;font-weight:800;display:inline-flex;align-items:center;gap:7px;transition:background .2s ease,border-color .2s ease,transform .2s ease;}
.installed-app-uninstall:hover:not(:disabled){background:rgba(255,120,120,.15);border-color:rgba(255,120,120,.42);transform:translateY(-1px);}
.installed-app-uninstall:disabled{opacity:.45;cursor:not-allowed;}
.installed-summary-card{position:relative;overflow:hidden;border:1px solid rgba(255,255,255,.1);border-radius:16px;background:linear-gradient(135deg,rgba(18,33,49,.72),rgba(10,17,40,.62));padding:14px 16px;display:flex;align-items:center;gap:12px;}
.installed-summary-card:after{content:"";position:absolute;inset:0;background:radial-gradient(circle at 15% 0,rgba(0,241,254,.13),transparent 36%);pointer-events:none;}
.installed-summary-card>*{position:relative;z-index:1;}
.installed-search{background:rgba(18,33,49,.74);border:1px solid rgba(255,255,255,.1);color:#d3deef;border-radius:14px;padding:12px 14px;width:100%;outline:none;transition:border-color .2s ease,box-shadow .2s ease;}
.installed-search:focus{border-color:rgba(0,241,254,.54);box-shadow:0 0 0 3px rgba(0,241,254,.1);}
.installed-modal{background:rgba(2,8,23,.72);backdrop-filter:blur(18px);}
`;
        document.head.appendChild(style);
    }

    function viewHtml() {
        return '<div class="max-w-6xl mx-auto space-y-lg relative z-10 w-full">' +
            '<div class="mb-xl flex flex-col xl:flex-row xl:items-end justify-between gap-md"><div><h2 class="text-headline-lg text-on-surface mb-xs apps-title"></h2><p class="text-body-md text-on-surface-variant apps-sub"></p></div>' +
            '<div class="flex flex-col sm:flex-row gap-sm"><button class="btn-ghost rounded-lg py-2.5 px-4 text-label-md flex items-center justify-center gap-xs" id="btn-open-windows-apps"><span class="material-symbols-outlined text-[18px]">open_in_new</span><span class="apps-open-settings"></span></button><button class="btn-glow bg-secondary-container text-on-secondary-container text-label-md font-bold px-5 py-2.5 rounded-lg flex items-center justify-center gap-sm" id="btn-refresh-installed-apps"><span class="material-symbols-outlined text-[18px]">refresh</span><span class="apps-refresh"></span></button></div></div>' +
            '<div class="grid grid-cols-1 md:grid-cols-3 gap-sm"><div class="installed-summary-card"><span class="material-symbols-outlined text-secondary-container">inventory_2</span><div><p class="text-title-lg text-on-surface" id="installed-app-total">--</p><p class="text-label-sm text-on-surface-variant apps-total"></p></div></div><div class="installed-summary-card"><span class="material-symbols-outlined text-secondary-container">delete_sweep</span><div><p class="text-title-lg text-on-surface" id="installed-app-removable">--</p><p class="text-label-sm text-on-surface-variant apps-removable"></p></div></div><div class="installed-summary-card"><span class="material-symbols-outlined text-secondary-container">filter_alt</span><div><p class="text-title-lg text-on-surface" id="installed-app-visible">--</p><p class="text-label-sm text-on-surface-variant apps-visible"></p></div></div></div>' +
            '<div class="glass-panel rounded-xl p-lg space-y-md"><input id="installed-app-search" class="installed-search" type="search" autocomplete="off" /> <p class="text-label-md text-on-surface-variant hidden" id="installed-app-status"></p><div class="grid grid-cols-1 xl:grid-cols-2 gap-md" id="installed-app-list"></div></div>' +
            '</div>';
    }

    function modalHtml() {
        return '<div class="fixed inset-0 z-[110] hidden installed-modal items-center justify-center px-lg" id="installed-uninstall-modal"><div class="glass-modal rounded-3xl p-xl w-full max-w-lg shadow-2xl border border-white/10">' +
            '<div class="flex items-start gap-md"><div class="w-12 h-12 rounded-2xl bg-surface-container-high flex items-center justify-center border border-white/10 text-secondary-container"><span class="material-symbols-outlined">delete</span></div><div class="min-w-0"><h3 class="text-headline-md text-on-surface apps-confirm-title"></h3><p class="text-body-md text-on-surface-variant mt-sm apps-confirm-body"></p></div></div>' +
            '<div class="mt-lg rounded-xl bg-surface-container-low/60 border border-white/10 p-md"><p class="text-title-md text-on-surface truncate" id="installed-modal-name">--</p><p class="text-label-md text-on-surface-variant truncate" id="installed-modal-publisher">--</p></div>' +
            '<div class="mt-xl flex flex-col sm:flex-row gap-md justify-end"><button class="btn-ghost rounded-lg py-3 px-4 text-label-md" id="btn-cancel-installed-uninstall" type="button"><span class="apps-cancel"></span></button><button class="installed-app-uninstall justify-center" id="btn-confirm-installed-uninstall" type="button"><span class="material-symbols-outlined text-[18px]">delete_forever</span><span class="apps-launch"></span></button></div>' +
            '</div></div>';
    }

    function mountAppsTab() {
        const navList = document.getElementById('nav-list');
        const mainContent = document.getElementById('main-content');
        if (!navList || !mainContent) return;
        ensureStyles();

        if (!document.querySelector('#nav-list a[data-view="applications"]')) {
            const settingsLi = document.querySelector('#nav-list a[data-view="settings"]')?.parentElement;
            const item = document.createElement('li');
            item.innerHTML = '<a class="nav-item flex items-center gap-3 text-on-surface-variant font-medium px-4 py-3 opacity-80 hover:bg-white/5 hover:text-secondary-fixed transition-all duration-300 rounded-lg active:scale-[0.98]" data-view="applications" href="#"><span class="material-symbols-outlined">deployed_code</span><span class="text-body-md apps-nav-label"></span></a>';
            if (settingsLi) settingsLi.parentElement.insertBefore(item, settingsLi);
            else navList.appendChild(item);
        }

        if (!document.getElementById('view-applications')) {
            const settingsView = document.getElementById('view-settings');
            const section = document.createElement('section');
            section.className = 'view flex-1 flex-col hidden';
            section.id = 'view-applications';
            section.innerHTML = viewHtml();
            if (settingsView) settingsView.parentElement.insertBefore(section, settingsView);
            else mainContent.appendChild(section);
        }

        if (!document.getElementById('installed-uninstall-modal')) {
            const wrap = document.createElement('div');
            wrap.innerHTML = modalHtml();
            document.body.appendChild(wrap.firstElementChild);
        }

        refreshLabels();
        wireUi();
        document.dispatchEvent(new CustomEvent('navmounted'));
    }

    function refreshLabels() {
        const pairs = [
            ['.apps-nav-label', 'nav'], ['.apps-title', 'title'], ['.apps-sub', 'sub'], ['.apps-refresh', 'refresh'],
            ['.apps-open-settings', 'openSettings'], ['.apps-total', 'total'], ['.apps-removable', 'removable'], ['.apps-visible', 'visible'],
            ['.apps-confirm-title', 'confirmTitle'], ['.apps-confirm-body', 'confirmBody'], ['.apps-cancel', 'cancel'], ['.apps-launch', 'launch']
        ];
        pairs.forEach(([sel, key]) => document.querySelectorAll(sel).forEach(el => el.textContent = t(key)));
        const search = document.getElementById('installed-app-search');
        if (search) search.setAttribute('placeholder', t('search'));
    }

    function setStatus(text, isError) {
        const el = document.getElementById('installed-app-status');
        if (!el) return;
        el.textContent = text;
        el.classList.remove('hidden');
        el.style.color = isError ? '#ffd0d0' : '#00f1fe';
    }

    function clearStatus() {
        document.getElementById('installed-app-status')?.classList.add('hidden');
    }

    async function loadApplications(force) {
        if (loaded && !force) return;
        const list = document.getElementById('installed-app-list');
        if (!list) return;
        clearStatus();
        list.innerHTML = '<p class="text-body-md text-on-surface-variant opacity-70 py-3">' + esc(t('loading')) + '</p>';
        updateCounters(null, null, null);
        try {
            const data = await Host.call('getInstalledApplications');
            applications = data.applications || [];
            loaded = true;
            renderApplications();
        } catch (err) {
            applications = [];
            loaded = false;
            list.innerHTML = '<p class="text-body-md text-on-surface-variant opacity-70 py-3">' + esc(t('loadErr') + err.message) + '</p>';
            updateCounters(0, 0, 0);
        }
    }

    function updateCounters(total, removable, visible) {
        const totalEl = document.getElementById('installed-app-total');
        const removableEl = document.getElementById('installed-app-removable');
        const visibleEl = document.getElementById('installed-app-visible');
        if (totalEl) totalEl.textContent = total == null ? '--' : String(total);
        if (removableEl) removableEl.textContent = removable == null ? '--' : String(removable);
        if (visibleEl) visibleEl.textContent = visible == null ? '--' : String(visible);
    }

    function renderApplications() {
        const list = document.getElementById('installed-app-list');
        if (!list) return;
        const query = normalize(document.getElementById('installed-app-search')?.value || '');
        const visible = applications.filter(app => matchesQuery(app, query));
        updateCounters(applications.length, applications.filter(a => a.canUninstall).length, visible.length);

        if (!applications.length) {
            list.innerHTML = '<p class="text-body-md text-on-surface-variant opacity-70 py-3">' + esc(t('empty')) + '</p>';
            return;
        }
        if (!visible.length) {
            list.innerHTML = '<p class="text-body-md text-on-surface-variant opacity-70 py-3">' + esc(t('noResults')) + '</p>';
            return;
        }

        list.innerHTML = visible.map(renderAppCard).join('');
    }

    function matchesQuery(app, query) {
        if (!query) return true;
        return [app.name, app.publisher, app.version, app.installLocation, app.source]
            .some(value => normalize(value).includes(query));
    }

    function normalize(value) {
        return String(value || '').trim().toLowerCase();
    }

    function renderAppCard(app) {
        const publisher = app.publisher || t('unknownPublisher');
        const version = app.version || t('notAvailable');
        const size = app.estimatedSizeMb ? app.estimatedSizeMb + ' MB' : t('notAvailable');
        const installed = app.installDate || t('notAvailable');
        const path = app.installLocation || t('notAvailable');
        const disabled = app.canUninstall ? '' : ' disabled';
        const uninstallLabel = app.canUninstall ? t('uninstall') : t('unavailable');
        return '<article class="installed-app-card">' +
            '<div class="installed-app-head"><div class="installed-app-title"><div class="installed-app-icon"><span class="material-symbols-outlined">apps</span></div><div class="min-w-0"><p class="installed-app-name">' + esc(app.name) + '</p><p class="installed-app-publisher" title="' + escAttr(publisher) + '">' + esc(publisher) + '</p></div></div>' +
            '<button class="installed-app-uninstall" data-installed-app-id="' + escAttr(app.id) + '" type="button"' + disabled + '><span class="material-symbols-outlined text-[18px]">delete</span><span>' + esc(uninstallLabel) + '</span></button></div>' +
            '<div class="installed-app-meta">' +
            metaLine(t('version'), version) + metaLine(t('size'), size) + metaLine(t('installed'), installed) + metaLine(t('source'), app.source || t('notAvailable')) + metaLine(t('path'), path) +
            '</div></article>';
    }

    function metaLine(label, value) {
        return '<div class="installed-app-line"><span>' + esc(label) + '</span><span title="' + escAttr(value) + '">' + esc(value) + '</span></div>';
    }

    function openConfirm(app) {
        selectedApp = app;
        document.getElementById('installed-modal-name').textContent = app.name || t('notAvailable');
        document.getElementById('installed-modal-publisher').textContent = app.publisher || t('unknownPublisher');
        const modal = document.getElementById('installed-uninstall-modal');
        modal.classList.remove('hidden');
        modal.classList.add('flex');
    }

    function closeConfirm() {
        const modal = document.getElementById('installed-uninstall-modal');
        modal.classList.add('hidden');
        modal.classList.remove('flex');
        selectedApp = null;
    }

    function wireUi() {
        if (wired) return;
        document.addEventListener('click', async (e) => {
            const refresh = e.target.closest('#btn-refresh-installed-apps');
            if (refresh) {
                refresh.disabled = true;
                try { await loadApplications(true); }
                finally { refresh.disabled = false; }
                return;
            }

            const openSettings = e.target.closest('#btn-open-windows-apps');
            if (openSettings) {
                Host.call('openWindowsAppsSettings').catch(() => {});
                return;
            }

            const uninstall = e.target.closest('[data-installed-app-id]');
            if (uninstall) {
                const app = applications.find(a => a.id === uninstall.dataset.installedAppId);
                if (app && app.canUninstall) openConfirm(app);
                return;
            }

            if (e.target.closest('#btn-cancel-installed-uninstall') || e.target.id === 'installed-uninstall-modal') {
                closeConfirm();
                return;
            }

            const confirm = e.target.closest('#btn-confirm-installed-uninstall');
            if (confirm && selectedApp) {
                confirm.disabled = true;
                try {
                    const result = await Host.call('startInstalledAppRemoval', { id: selectedApp.id, preferQuiet: false });
                    if (!result || result.success !== true) throw new Error(result && result.message ? result.message : 'Operazione non riuscita');
                    closeConfirm();
                    setStatus(t('launched'), false);
                    setTimeout(() => loadApplications(true), 3500);
                } catch (err) {
                    setStatus(t('launchErr') + err.message, true);
                } finally {
                    confirm.disabled = false;
                }
            }
        });

        document.addEventListener('input', (e) => {
            if (e.target && e.target.id === 'installed-app-search') renderApplications();
        });

        wired = true;
    }

    mountAppsTab();

    document.addEventListener('settingsloaded', mountAppsTab);
    document.addEventListener('viewchange', (e) => {
        if (e.detail && e.detail.view === 'applications') loadApplications(false);
    });
    document.addEventListener('langchanged', () => {
        refreshLabels();
        renderApplications();
    });
})();
