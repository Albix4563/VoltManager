/**
 * Tab router + nav indicator animation + shared boot.
 */
(function () {
    const navLinks = document.querySelectorAll('#nav-list a');
    const navIndicator = document.getElementById('nav-indicator');
    const mainContent = document.getElementById('main-content');
    const views = {
        home: document.getElementById('view-home'),
        power: document.getElementById('view-power'),
        settings: document.getElementById('view-settings'),
    };

    function positionIndicator(link) {
        const li = link.parentElement;
        navIndicator.style.top = li.offsetTop + 'px';
        navIndicator.style.height = li.offsetHeight + 'px';
    }

    function activate(link) {
        navLinks.forEach(l => {
            l.classList.remove('text-secondary-container', 'font-bold', 'bg-surface-container-high/50');
            l.classList.add('text-on-surface-variant', 'font-medium', 'opacity-80');
            l.querySelector('.material-symbols-outlined').classList.remove('icon-fill');
        });
        link.classList.add('text-secondary-container', 'font-bold', 'bg-surface-container-high/50');
        link.classList.remove('text-on-surface-variant', 'font-medium', 'opacity-80');
        link.querySelector('.material-symbols-outlined').classList.add('icon-fill');
        positionIndicator(link);
    }

    function showView(name) {
        Object.entries(views).forEach(([key, el]) => {
            el.classList.toggle('hidden', key !== name);
        });
        // Re-trigger entrance animation.
        mainContent.classList.remove('animate-in');
        void mainContent.offsetWidth;
        mainContent.classList.add('animate-in');
        document.dispatchEvent(new CustomEvent('viewchange', { detail: { view: name } }));
    }

    navLinks.forEach(link => {
        link.addEventListener('click', (e) => {
            e.preventDefault();
            activate(link);
            showView(link.dataset.view);
        });
    });

    // Initial indicator position.
    if (navLinks.length > 0) positionIndicator(navLinks[0]);

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
            document.getElementById('version-badge').textContent = 'Current Version: v' + info.appVersion;
        } catch (err) {
            console.error('getSystemInfo failed', err);
        }
    }
    boot();
})();
