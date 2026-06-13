/**
 * Tab router + nav indicator animation + shared boot.
 */
(function () {
    const navList = document.getElementById('nav-list');
    const navIndicator = document.getElementById('nav-indicator');
    const mainContent = document.getElementById('main-content');

    function getNavLinks() {
        return Array.from(document.querySelectorAll('#nav-list a[data-view]'));
    }

    function getViews() {
        const views = {};
        document.querySelectorAll('#main-content .view[id^="view-"]').forEach(el => {
            views[el.id.replace(/^view-/, '')] = el;
        });
        return views;
    }

    function positionIndicator(link) {
        if (!link || !navIndicator) return;
        const li = link.parentElement;
        navIndicator.style.top = li.offsetTop + 'px';
        navIndicator.style.height = li.offsetHeight + 'px';
    }

    function activate(link) {
        getNavLinks().forEach(l => {
            l.classList.remove('text-secondary-container', 'font-bold', 'bg-surface-container-high/50');
            l.classList.add('text-on-surface-variant', 'font-medium', 'opacity-80');
            l.querySelector('.material-symbols-outlined')?.classList.remove('icon-fill');
        });
        link.classList.add('text-secondary-container', 'font-bold', 'bg-surface-container-high/50');
        link.classList.remove('text-on-surface-variant', 'font-medium', 'opacity-80');
        link.querySelector('.material-symbols-outlined')?.classList.add('icon-fill');
        positionIndicator(link);
    }

    function showView(name) {
        const views = getViews();
        Object.entries(views).forEach(([key, el]) => {
            el.classList.toggle('hidden', key !== name);
        });
        // Re-trigger entrance animation.
        mainContent.classList.remove('animate-in');
        void mainContent.offsetWidth;
        mainContent.classList.add('animate-in');
        document.dispatchEvent(new CustomEvent('viewchange', { detail: { view: name } }));
    }

    navList.addEventListener('click', (e) => {
        const link = e.target.closest('a[data-view]');
        if (!link || !navList.contains(link)) return;
        e.preventDefault();
        activate(link);
        showView(link.dataset.view);
    });

    // Initial indicator position.
    const initialLink = getNavLinks()[0];
    if (initialLink) positionIndicator(initialLink);

    document.addEventListener('navmounted', () => {
        const activeLink = document.querySelector('#nav-list a.text-secondary-container[data-view]') || getNavLinks()[0];
        if (activeLink) positionIndicator(activeLink);
    });

    window.addEventListener('resize', () => {
        const activeLink = document.querySelector('#nav-list a.text-secondary-container[data-view]');
        if (activeLink) positionIndicator(activeLink);
    });

    // Top bar: minimize to tray.
    document.getElementById('btn-minimize-tray').addEventListener('click', () => {
        Host.call('minimizeToTray').catch(() => {});
    });

    // Shared boot: system info into dashboard + settings cards.
    async function boot() {
        if (!Host.available) return;
        try {
            const info = await Host.call('getSystemInfo');
            document.getElementById('cpu-name').textContent = info.cpuName;
            document.getElementById('gpu-name').textContent = info.gpuName;
            document.getElementById('info-cpu').textContent = info.cpuName;
            document.getElementById('info-gpu').textContent = info.gpuName;
            document.getElementById('info-ram').textContent = info.ramTotalGb + ' GB';
            document.getElementById('info-os').textContent = info.osVersion;
            document.getElementById('info-version').textContent = 'v' + info.appVersion;
            document.getElementById('sidebar-version').textContent = 'VoltManager v' + info.appVersion;
            document.getElementById('version-badge').textContent = I18n.t('set_updates_curr') + 'v' + info.appVersion;
        } catch (err) {
            console.error('getSystemInfo failed', err);
        }
    }
    boot();
})();