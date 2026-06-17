/**
 * Changelog: full GitHub release history (live, no cache).
 * Lazy-loads on first view; refresh button re-fetches.
 */
(function () {
    if (!Host.available) return;

    const listEl = document.getElementById('changelog-list');
    const statusEl = document.getElementById('changelog-status');
    const btnRefresh = document.getElementById('btn-refresh-changelog');

    let loaded = false;
    let loading = false;
    let lastData = null;

    function esc(s) {
        const div = document.createElement('div');
        div.textContent = s == null ? '' : String(s);
        return div.innerHTML;
    }

    function escAttr(s) {
        return esc(s).replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    function fmtDate(iso) {
        try {
            const lang = window.I18n && I18n.getLang ? I18n.getLang() : 'it';
            const locale = lang === 'zh' ? 'zh-CN' : (lang === 'en' ? 'en-GB' : 'it-IT');
            return new Date(iso).toLocaleDateString(locale,
                { day: 'numeric', month: 'short', year: 'numeric' });
        } catch { return ''; }
    }

    function formatVersion(ver) {
        const value = ver == null ? '' : String(ver).trim();
        if (!value) return 'N/D';
        return value.toLowerCase().startsWith('v') ? value : 'v' + value;
    }

    function setStatus(text, isError) {
        if (!statusEl) return;
        if (!text) {
            statusEl.classList.add('hidden');
            return;
        }
        statusEl.textContent = text;
        statusEl.classList.remove('hidden', 'ok', 'err');
        statusEl.classList.add(isError ? 'err' : 'ok');
    }

    function errorMessage(data) {
        const map = {
            offline: 'changelog_offline',
            ratelimited: 'changelog_ratelimited',
            error: 'changelog_error',
        };
        const key = map[data.status];
        const fallback = data.message || I18n.t('changelog_error');
        if (!key) return fallback;
        const translated = I18n.t(key);
        return translated === key ? fallback : translated;
    }

    function releaseHtml(rel) {
        const badges =
            (rel.isCurrent ? '<span class="text-label-sm px-2 py-1 rounded bg-secondary-container/10 text-secondary-container border border-secondary-container/20">' + esc(I18n.t('changelog_current')) + '</span>' : '') +
            (rel.prerelease ? '<span class="text-label-sm px-2 py-1 rounded bg-white/5 text-on-surface-variant border border-white/10">' + esc(I18n.t('changelog_prerelease')) + '</span>' : '');
        const dotClass = rel.isCurrent
            ? 'bg-secondary-container shadow-[0_0_10px_rgba(0,241,254,0.4)]'
            : 'bg-surface-variant border-2 border-background';
        const title = rel.name && rel.name.trim() && rel.name.trim() !== formatVersion(rel.version)
            ? esc(rel.name) : '';
        const link = rel.htmlUrl
            ? '<a href="#" class="text-label-sm text-secondary-fixed-dim hover:text-secondary-container inline-flex items-center gap-1 mt-sm" data-changelog-url="' + escAttr(rel.htmlUrl) + '"><span class="material-symbols-outlined text-[14px]">open_in_new</span>' + esc(I18n.t('changelog_view_github')) + '</a>'
            : '';
        const notes = rel.notes && rel.notes.trim()
            ? '<div class="text-body-md text-on-surface-variant mt-sm whitespace-pre-line">' + esc(rel.notes) + '</div>'
            : '<p class="text-body-md text-on-surface-variant opacity-60 mt-sm">' + esc(I18n.t('changelog_empty')) + '</p>';
        return '<div class="relative pl-6 pb-lg border-l border-surface-variant/50 last:border-l-transparent">' +
            '<div class="absolute left-0 top-1 w-3 h-3 rounded-full -translate-x-[6.5px] ' + dotClass + '"></div>' +
            '<div class="flex items-center gap-sm flex-wrap mb-xs">' +
            '<h4 class="text-title-lg text-on-surface">' + esc(formatVersion(rel.version)) + '</h4>' +
            badges +
            (rel.date ? '<span class="text-label-sm text-on-surface-variant opacity-70 ml-auto">' + esc(fmtDate(rel.date)) + '</span>' : '') +
            '</div>' +
            (title ? '<p class="text-body-md text-on-surface mb-xs">' + title + '</p>' : '') +
            notes +
            link +
            '</div>';
    }

    function commitsHtml(commits) {
        return '<div class="relative pl-6 border-l border-surface-variant/50">' +
            '<div class="absolute left-0 top-1 w-3 h-3 rounded-full bg-surface-variant -translate-x-[6.5px] border-2 border-background"></div>' +
            '<h4 class="text-title-lg text-on-surface opacity-80 mb-xs">' + esc(I18n.t('msg_latest_commits')) + '</h4>' +
            '<ul class="text-body-md text-on-surface-variant space-y-2 mt-sm list-disc pl-4 marker:text-secondary-container/50">' +
            commits.map(c =>
                '<li><span class="text-secondary-fixed-dim font-mono text-label-md">' + esc(c.sha) + '</span> ' +
                esc(c.message) +
                ' <span class="opacity-60 text-label-sm">— ' + esc(c.author) + ', ' + esc(fmtDate(c.date)) + '</span></li>'
            ).join('') +
            '</ul></div>';
    }

    function render(data) {
        if (!listEl) return;
        if (data.status === 'ok' && data.releases && data.releases.length) {
            setStatus('', false);
            listEl.innerHTML = data.releases.map(releaseHtml).join('');
            return;
        }
        if (data.status === 'norelease') {
            setStatus(data.message || '', false);
            listEl.innerHTML = (data.commits && data.commits.length)
                ? commitsHtml(data.commits)
                : '<p class="text-body-md text-on-surface-variant opacity-70">' + esc(I18n.t('changelog_empty')) + '</p>';
            return;
        }
        // offline | ratelimited | error
        const msg = errorMessage(data);
        setStatus(msg, true);
        listEl.innerHTML = '<p class="text-body-md text-on-surface-variant opacity-70">' + esc(msg) + '</p>';
    }

    async function load(force) {
        if (!Host.available || !listEl) return;
        if (loading) return;
        if (loaded && !force) return;
        loading = true;
        listEl.innerHTML = '<p class="text-body-md text-on-surface-variant opacity-70">' + esc(I18n.t('changelog_loading')) + '</p>';
        setStatus('', false);
        try {
            const data = await Host.call('getReleaseHistory');
            lastData = data;
            render(data);
            loaded = true;
        } catch (err) {
            setStatus(I18n.t('changelog_error') + ' ' + err.message, true);
            listEl.innerHTML = '<p class="text-body-md text-on-surface-variant opacity-70">' + esc(I18n.t('changelog_error')) + '</p>';
        } finally {
            loading = false;
        }
    }

    document.addEventListener('viewchange', (e) => {
        if (e.detail && e.detail.view === 'changelog') load(false);
    });

    if (btnRefresh) btnRefresh.addEventListener('click', () => load(true));

    if (listEl) {
        listEl.addEventListener('click', (e) => {
            const link = e.target.closest('[data-changelog-url]');
            if (!link) return;
            e.preventDefault();
            Host.call('openExternal', { url: link.dataset.changelogUrl }).catch(() => {});
        });
    }

    document.addEventListener('langchanged', () => {
        if (loaded && lastData) render(lastData);
    });
})();
