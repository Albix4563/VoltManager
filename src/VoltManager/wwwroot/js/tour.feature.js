/**
 * Guided feature tour (spotlight / coachmarks).
 *
 * Runs once, right after the welcome onboarding overlay is completed on first
 * launch (chained via the `welcomecompleted` event), walking the user through
 * the real UI: navigation, active plan, live metrics, automation status, Power
 * Management and Settings. Each step dims the screen, highlights a real element
 * and shows a tooltip card.
 *
 * Gated on settings.tourCompleted so it never repeats automatically. Re-runnable
 * from Settings → "Tour guidato" (#btn-show-tour) and via window.__tour.open().
 *
 * Self-contained: injects its own CSS (no Tailwind recompile needed), measures
 * elements with getBoundingClientRect, and switches app views by clicking the
 * sidebar nav links so spotlighted targets are actually on screen.
 */
(function () {
    if (!window.Host || !Host.available) return;
    const TOUR_TRANSITION_SETTLE_MS = 680;

    // step.view: app view to switch to before showing (home|power|settings).
    // step.el: CSS selector of the element to spotlight (null = centered card).
    // step.placement: 'auto' (below/above) or 'right' (beside, for the sidebar).
    const steps = [
        { el: null,                                               icon: 'waving_hand',       title: 'tour_intro_title',        body: 'tour_intro_body' },
        { el: '#nav-list',                                        icon: 'menu',              title: 'tour_nav_title',          body: 'tour_nav_body',          placement: 'right' },
        { el: '#vm-top-status',                                   icon: 'bolt',              title: 'tour_topbar_title',       body: 'tour_topbar_body' },
        { el: '#btn-energy-tips',                                 icon: 'tips_and_updates',  title: 'tour_tips_title',         body: 'tour_tips_body' },
        { view: 'overview',                                       icon: 'dashboard',         el: '.vm-overview-grid',        title: 'tour_overview_title',     body: 'tour_overview_body' },
        { view: 'overview',                                       icon: 'brightness_6',      el: '#vm-brightness-card',      title: 'tour_brightness_title',   body: 'tour_brightness_body',   laptopOnly: true },
        { el: '#btn-monitoring-toggle',                           icon: 'monitor_heart',     title: 'tour_monitor_title',      body: 'tour_monitor_body',      placement: 'right' },
        { view: 'monitoring',     subview: 'hardware',            icon: 'memory',            el: '#dash-taskmanager',        title: 'tour_metrics_title',      body: 'tour_metrics_body' },
        { view: 'monitoring',     subview: 'processes',           icon: 'process_chart',     el: '#processes-section',       title: 'tour_processes_title',    body: 'tour_processes_body' },
        { view: 'monitoring',     subview: 'temperatures',        icon: 'device_thermostat', el: '#vm-panel-monitoring-temperatures', title: 'tour_temperatures_title', body: 'tour_temperatures_body' },
        { view: 'monitoring',     subview: 'storage',             icon: 'hard_drive',        el: '#vm-panel-monitoring-storage',      title: 'tour_storage_title',      body: 'tour_storage_body' },
        { view: 'monitoring',     subview: 'battery',             icon: 'battery_horiz_075', el: '#vm-panel-monitoring-battery',      title: 'tour_battery_title',      body: 'tour_battery_body',      laptopOnly: true },
        { view: 'power-plans',    subview: 'active',              icon: 'bolt',              el: '[data-vm-view="power-plans"] .vm-subnav',  title: 'tour_plan_title',         body: 'tour_plan_body' },
        { view: 'automations',    subview: 'rules',               icon: 'automation',        el: '[data-vm-view="automations"] .vm-subnav',  title: 'tour_power_title',        body: 'tour_power_body' },
        { view: 'system-tools',   subview: 'scheduled',           icon: 'construction',      el: '[data-vm-view="system-tools"] .vm-subnav', title: 'tour_system_tools_title', body: 'tour_system_tools_body' },
        { view: 'remote-control',                                 icon: 'devices',           el: '#lan-remote-hero',         title: 'tour_remote_title',       body: 'tour_remote_body' },
        { view: 'widgets',        subview: 'widget',              icon: 'widgets',           el: '#widgets-card',            title: 'tour_widgets_title',      body: 'tour_widgets_body' },
        { view: 'settings',       subview: 'general',             icon: 'settings',          el: '#pref-show-tour',          title: 'tour_settings_title',     body: 'tour_settings_body' },
        { el: null,                                               icon: 'check_circle',      title: 'tour_done_title',         body: 'tour_done_body' },
    ];

    const PAD = 8;           // spotlight padding around the target
    const GAP = 14;          // distance between target and tooltip
    const MIN_LEFT = 296;    // keep tooltips clear of the 280px sidebar
    const MARGIN = 16;

    let root, blocker, hole, pop, popIcon, popTitle, popBody, popCounter, btnBack, btnNext, btnSkip;
    let step = 0;
    let curView = null;
    let curSubview = null;
    let settleTimer = 0;
    let active = false;
    let autoStarted = false;

    function t(key) {
        return (window.I18n && I18n.t) ? I18n.t(key) : key;
    }

    function reduceMotion() {
        return !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
    }

    function getSettings() {
        const s = window.__voltSettings;
        if (!s) return null;
        return s.get ? s.get() : s;
    }

    function persistCompleted() {
        const s = getSettings();
        if (!s) return;
        s.tourCompleted = true;
        const api = window.__voltSettings;
        if (api && api.saveNow) api.saveNow().catch(err => console.error('saveSettings failed', err));
        else if (api && api.save) api.save();
    }

    function build() {
        if (root) return;

        root = document.createElement('div');
        root.id = 'vm-tour-root';
        root.hidden = true;
        root.setAttribute('data-reduce', reduceMotion() ? 'true' : 'false');

        blocker = document.createElement('div');
        blocker.className = 'vm-tour-blocker';

        hole = document.createElement('div');
        hole.className = 'vm-tour-hole';

        pop = document.createElement('div');
        pop.className = 'vm-tour-pop';
        pop.setAttribute('role', 'dialog');
        pop.setAttribute('aria-live', 'polite');
        pop.innerHTML =
            '<h2 class="vm-tour-pop__title"><span class="material-symbols-outlined">tips_and_updates</span><span class="vm-tour-pop__title-text"></span></h2>' +
            '<p class="vm-tour-pop__body"></p>' +
            '<div class="vm-tour-pop__foot">' +
            '  <button class="vm-tour-skip" type="button"></button>' +
            '  <div class="vm-tour-actions">' +
            '    <span class="vm-tour-counter"></span>' +
            '    <button class="vm-tour-btn vm-tour-btn--ghost" type="button" data-act="back"></button>' +
            '    <button class="vm-tour-btn vm-tour-btn--primary" type="button" data-act="next"></button>' +
            '  </div>' +
            '</div>';

        root.appendChild(blocker);
        root.appendChild(hole);
        root.appendChild(pop);
        document.body.appendChild(root);

        popIcon = pop.querySelector('.vm-tour-pop__title .material-symbols-outlined');
        popTitle = pop.querySelector('.vm-tour-pop__title-text');
        popBody = pop.querySelector('.vm-tour-pop__body');
        popCounter = pop.querySelector('.vm-tour-counter');
        btnBack = pop.querySelector('[data-act="back"]');
        btnNext = pop.querySelector('[data-act="next"]');
        btnSkip = pop.querySelector('.vm-tour-skip');

        btnBack.addEventListener('click', () => navigate(-1));
        btnNext.addEventListener('click', () => {
            navigate(1);
        });
        btnSkip.addEventListener('click', finish);
        // Block stray clicks reaching the app, but ignore drag noise.
        blocker.addEventListener('click', (e) => e.stopPropagation());
    }

    function switchView(view, subview, subviewGroup) {
        if (window.VoltUiReorg?.ready) {
            const mappedView = { home: 'overview', power: 'power-plans' }[view] || view;
            window.VoltUiReorg.activateView(mappedView, true);
            if (subview) window.VoltUiReorg.activateSubview?.(subviewGroup || mappedView, subview);
            return;
        }
        const legacyView = { overview: 'home', monitoring: 'home', 'power-plans': 'power', automations: 'power', 'system-tools': 'system' }[view] || view;
        const link = document.querySelector('#nav-list a[data-view="' + legacyView + '"]');
        if (link) link.click();
    }

    function visibleStep(index) {
        const item = steps[index];
        if (item.laptopOnly && document.documentElement.dataset.vmHasBattery !== 'true') return false;
        if (!item.el) return true;
        let node = document.querySelector(item.el);
        if (!node) return false;
        while (node && !node.classList?.contains('vm-reorg-view')) {
            const isTargetSubview = !!(item.subview && node.classList?.contains('vm-subview') &&
                node.dataset?.vmPanel === item.subview);
            if (!isTargetSubview && (node.hidden || node.classList?.contains('hidden') ||
                node.getAttribute?.('aria-hidden') === 'true' || node.style?.display === 'none' ||
                window.getComputedStyle?.(node)?.display === 'none')) return false;
            node = node.parentElement;
        }
        return true;
    }

    function availableSteps() {
        return steps.map((_, index) => index).filter(visibleStep);
    }

    function navigate(direction) {
        const available = availableSteps();
        const index = available.indexOf(step);
        const next = index < 0
            ? (direction > 0 ? available.find(item => item > step) : available.slice().reverse().find(item => item < step))
            : available[index + direction];
        if (next == null) {
            if (direction > 0) finish();
            return;
        }
        showStep(next);
    }

    function place(el, placement) {
        const popRect = pop.getBoundingClientRect();
        const pw = popRect.width || 320;
        const ph = popRect.height || 160;
        const vw = window.innerWidth;
        const vh = window.innerHeight;

        if (!el) {
            // Centered card; collapse the spotlight to nothing (full dim).
            hole.dataset.glow = 'false';
            hole.style.width = '0px';
            hole.style.height = '0px';
            hole.style.left = (vw / 2) + 'px';
            hole.style.top = (vh / 2) + 'px';
            hole.style.borderColor = 'transparent';
            pop.style.left = Math.round((vw - pw) / 2) + 'px';
            pop.style.top = Math.round((vh - ph) / 2) + 'px';
            return;
        }

        const r = el.getBoundingClientRect();
        const hx = r.left - PAD, hy = r.top - PAD;
        const hw = r.width + PAD * 2, hh = r.height + PAD * 2;
        hole.dataset.glow = 'true';
        hole.style.borderColor = '';
        hole.style.left = hx + 'px';
        hole.style.top = hy + 'px';
        hole.style.width = hw + 'px';
        hole.style.height = hh + 'px';

        let left, top;
        if (placement === 'right') {
            left = r.right + GAP;
            top = r.top + r.height / 2 - ph / 2;
        } else {
            const below = r.bottom + GAP + ph <= vh - MARGIN;
            top = below ? r.bottom + GAP : r.top - GAP - ph;
            left = r.left + r.width / 2 - pw / 2;
        }
        // Clamp inside the viewport, clear of the sidebar.
        const maxLeft = vw - pw - MARGIN;
        const sidebarEdge = document.body?.classList?.contains('sidebar-collapsed') ? 80 : MIN_LEFT;
        const minLeft = Math.min(sidebarEdge, maxLeft);
        left = Math.max(minLeft, Math.min(left, maxLeft));
        top = Math.max(MARGIN, Math.min(top, vh - ph - MARGIN));
        pop.style.left = Math.round(left) + 'px';
        pop.style.top = Math.round(top) + 'px';
    }

    let repositionRaf = 0;
    function reposition() {
        if (!active) return;
        const s = steps[step];
        const el = s.el ? document.querySelector(s.el) : null;
        place(el, s.placement);
    }

    function onViewportChange() {
        if (!active) return;
        cancelAnimationFrame(repositionRaf);
        repositionRaf = requestAnimationFrame(reposition);
    }

    function renderControls() {
        const available = availableSteps();
        const position = available.indexOf(step);
        if (popIcon) popIcon.textContent = steps[step].icon || 'tips_and_updates';
        popTitle.textContent = t(steps[step].title);
        popBody.textContent = t(steps[step].body);
        popCounter.textContent = (position + 1) + ' / ' + available.length;
        btnBack.textContent = t('tour_back');
        btnBack.style.visibility = position <= 0 ? 'hidden' : 'visible';
        btnNext.textContent = position >= available.length - 1 ? t('tour_finish') : t('tour_next');
        btnSkip.textContent = t('tour_skip');
        btnSkip.style.visibility = position >= available.length - 1 ? 'hidden' : 'visible';
    }

    function measureAndPlace(s) {
        if (!active) return;
        const el = s.el ? document.querySelector(s.el) : null;
        if (el && document.getElementById('main-content')?.contains(el)) {
            try { el.scrollIntoView({ block: 'center', inline: 'nearest' }); } catch {}
        }
        requestAnimationFrame(() => {
            if (!active) return;
            place(el, s.placement);
            pop.setAttribute('data-show', 'true');
        });
    }

    function showStep(i) {
        const available = availableSteps();
        step = available.find(index => index >= i) ?? available[available.length - 1];
        if (step == null) return;
        const s = steps[step];
        renderControls();
        pop.setAttribute('data-show', 'false');
        clearTimeout(settleTimer);

        const targetView = s.view || curView;
        const targetSubview = s.subview || null;
        const viewChanged = !!(s.view && s.view !== curView);
        const subviewChanged = !!(targetSubview && targetSubview !== curSubview);
        if (viewChanged || subviewChanged) {
            curView = targetView;
            curSubview = targetSubview;
            switchView(targetView, targetSubview, s.subviewGroup);
            // Let the relaxed view-swap animation settle before measuring.
            settleTimer = setTimeout(() => measureAndPlace(s), TOUR_TRANSITION_SETTLE_MS);
        } else {
            requestAnimationFrame(() => measureAndPlace(s));
        }
    }

    function open() {
        build();
        if (active) return;
        active = true;
        autoStarted = true;
        curView = null;
        curSubview = null;
        root.setAttribute('data-reduce', reduceMotion() ? 'true' : 'false');
        root.hidden = false;
        window.addEventListener('resize', onViewportChange);
        document.addEventListener('keydown', onKeydown, true);
        showStep(0);
    }

    function teardown() {
        if (!root) return;
        active = false;
        clearTimeout(settleTimer);
        cancelAnimationFrame(repositionRaf);
        root.hidden = true;
        pop.setAttribute('data-show', 'false');
        window.removeEventListener('resize', onViewportChange);
        document.removeEventListener('keydown', onKeydown, true);
    }

    function finish() {
        teardown();
        persistCompleted();
    }

    function onKeydown(e) {
        if (!active) return;
        if (e.key === 'Escape') { e.preventDefault(); finish(); }
        else if (e.key === 'ArrowRight') {
            e.preventDefault();
            navigate(1);
        } else if (e.key === 'ArrowLeft') {
            e.preventDefault();
            navigate(-1);
        }
    }

    function blocked() {
        // Don't auto-launch over the setup / update / welcome overlays.
        const ids = ['setup-overlay', 'extra-plans-overlay', 'update-modal-overlay', 'welcome-overlay'];
        return ids.some(id => {
            const o = document.getElementById(id);
            return o && !o.classList.contains('hidden');
        });
    }

    function maybeAutoStart() {
        if (autoStarted) return;
        const s = getSettings();
        if (!s || s.tourCompleted === true) return;
        if (blocked()) return;
        open();
    }

    // First launch: welcome overlay just finished -> chain into the tour.
    document.addEventListener('welcomecompleted', () => {
        const s = getSettings();
        if (s && s.tourCompleted !== true && !blocked()) open();
    });

    // Upgrade path: existing users who already passed the welcome but never saw
    // the tour. welcome.js only opens its overlay when welcomeCompleted!==true,
    // so when it's already true we are free to start once settings are loaded.
    document.addEventListener('settingsloaded', () => {
        const s = getSettings();
        if (s && s.welcomeCompleted === true && s.tourCompleted !== true) {
            // Defer a tick so dashboards/labels finish their first paint.
            setTimeout(maybeAutoStart, 400);
        } else {
            autoStarted = false; // brand-new users: wait for welcomecompleted
        }
    });

    // Settings -> "Tour guidato" replay button (always runs, force).
    document.getElementById('btn-show-tour')?.addEventListener('click', () => {
        autoStarted = true;
        open();
    });

    // Keep labels in sync if the language changes mid-tour.
    document.addEventListener('langchanged', () => { if (active) renderControls(); });
    document.addEventListener('voltbatteryavailabilitychanged', () => {
        if (active && !visibleStep(step)) navigate(1);
        else if (active) renderControls();
    });

    window.__tour = { open, close: finish, isOpen: () => active };
})();
