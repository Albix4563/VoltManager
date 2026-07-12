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

    // step.view: app view to switch to before showing (home|power|settings).
    // step.el: CSS selector of the element to spotlight (null = centered card).
    // step.placement: 'auto' (below/above) or 'right' (beside, for the sidebar).
    const steps = [
        { el: null,                       title: 'tour_intro_title',    body: 'tour_intro_body' },
        { el: '#nav-list',                title: 'tour_nav_title',      body: 'tour_nav_body',      placement: 'right' },
        { view: 'home', el: '#plan-control',       title: 'tour_plan_title',     body: 'tour_plan_body' },
        { view: 'home', el: '#dash-taskmanager',   title: 'tour_metrics_title',  body: 'tour_metrics_body' },
        { el: '#btn-monitoring-toggle',   title: 'tour_monitor_title',  body: 'tour_monitor_body',  placement: 'right' },
        { view: 'power', el: '#pm-subnav',         title: 'tour_power_title',    body: 'tour_power_body' },
        { view: 'widgets', el: '#widgets-card',   title: 'tour_widgets_title',  body: 'tour_widgets_body' },
        { view: 'settings', el: '#pref-show-tour', title: 'tour_settings_title', body: 'tour_settings_body' },
        { el: null,                       title: 'tour_done_title',     body: 'tour_done_body' },
    ];

    const PAD = 8;           // spotlight padding around the target
    const GAP = 14;          // distance between target and tooltip
    const MIN_LEFT = 296;    // keep tooltips clear of the 280px sidebar
    const MARGIN = 16;

    let root, blocker, hole, pop, popTitle, popBody, popCounter, btnBack, btnNext, btnSkip;
    let step = 0;
    let curView = null;
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

    function ensureStyles() {
        if (document.getElementById('vm-tour-styles')) return;
        const style = document.createElement('style');
        style.id = 'vm-tour-styles';
        style.textContent = `
#vm-tour-root{position:fixed;inset:0;z-index:130;pointer-events:none;}
#vm-tour-root[hidden]{display:none;}
.vm-tour-blocker{position:fixed;inset:0;pointer-events:auto;background:transparent;}
.vm-tour-hole{position:fixed;top:0;left:0;width:0;height:0;border-radius:14px;
  box-shadow:0 0 0 9999px rgba(5,9,20,.66);border:2px solid rgba(0,241,254,.9);
  pointer-events:none;transition:top .32s cubic-bezier(.2,.8,.2,1),left .32s cubic-bezier(.2,.8,.2,1),width .32s cubic-bezier(.2,.8,.2,1),height .32s cubic-bezier(.2,.8,.2,1),box-shadow .32s ease;}
.vm-tour-hole[data-glow="true"]{box-shadow:0 0 0 9999px rgba(5,9,20,.66),0 0 22px 2px rgba(0,241,254,.45),inset 0 0 14px rgba(0,241,254,.25);}
.vm-tour-pop{position:fixed;top:0;left:0;width:320px;max-width:calc(100vw - 32px);pointer-events:auto;
  background:linear-gradient(135deg,rgba(18,33,49,.96),rgba(10,17,40,.96));
  border:1px solid rgba(0,241,254,.28);border-radius:18px;padding:18px 18px 14px;
  box-shadow:0 24px 60px rgba(0,0,0,.5),0 0 0 1px rgba(255,255,255,.04),0 0 30px rgba(0,241,254,.12);
  backdrop-filter:blur(20px);color:#d3deef;opacity:0;transform:translateY(6px) scale(.98);
  transition:top .28s cubic-bezier(.2,.8,.2,1),left .28s cubic-bezier(.2,.8,.2,1),opacity .22s ease,transform .22s ease;}
.vm-tour-pop[data-show="true"]{opacity:1;transform:translateY(0) scale(1);}
html[data-theme=light] .vm-tour-pop{background:linear-gradient(135deg,rgba(255,255,255,.97),rgba(238,246,250,.97));color:#0f2234;border-color:rgba(0,174,187,.32);}
.vm-tour-pop__title{font-size:16px;font-weight:800;letter-spacing:.01em;margin:0 0 6px;color:#eaf6ff;display:flex;align-items:center;gap:8px;}
html[data-theme=light] .vm-tour-pop__title{color:#06262c;}
.vm-tour-pop__title .material-symbols-outlined{font-size:20px;color:#00f1fe;}
.vm-tour-pop__body{font-size:13px;line-height:1.5;margin:0;color:rgba(211,222,239,.82);}
html[data-theme=light] .vm-tour-pop__body{color:rgba(15,34,52,.78);}
.vm-tour-pop__foot{display:flex;align-items:center;justify-content:space-between;gap:10px;margin-top:16px;}
.vm-tour-counter{font-size:11px;font-weight:700;letter-spacing:.08em;color:rgba(0,241,254,.85);font-variant-numeric:tabular-nums;}
.vm-tour-actions{display:flex;align-items:center;gap:8px;}
.vm-tour-btn{border:0;cursor:pointer;font-size:13px;font-weight:700;border-radius:10px;padding:8px 14px;transition:transform .15s ease,background .2s ease,color .2s ease,border-color .2s ease;line-height:1;}
.vm-tour-btn:active{transform:scale(.96);}
.vm-tour-btn--ghost{background:transparent;color:rgba(211,222,239,.7);border:1px solid rgba(255,255,255,.12);}
.vm-tour-btn--ghost:hover{color:#d3deef;border-color:rgba(255,255,255,.24);background:rgba(255,255,255,.05);}
html[data-theme=light] .vm-tour-btn--ghost{color:rgba(15,34,52,.7);border-color:rgba(15,34,52,.14);}
.vm-tour-btn--cyan{background:linear-gradient(135deg,#00f1fe,#00a8b5);color:#06262c;box-shadow:0 6px 18px rgba(0,241,254,.28);}
.vm-tour-btn--cyan:hover{box-shadow:0 8px 22px rgba(0,241,254,.4);}
.vm-tour-skip{background:transparent;border:0;cursor:pointer;font-size:12px;color:rgba(211,222,239,.55);padding:4px 6px;transition:color .2s ease;}
.vm-tour-skip:hover{color:rgba(211,222,239,.85);}
html[data-theme=light] .vm-tour-skip{color:rgba(15,34,52,.5);}
#vm-tour-root[data-reduce="true"] .vm-tour-hole,#vm-tour-root[data-reduce="true"] .vm-tour-pop{transition:none;}
        `.trim();
        document.head.appendChild(style);
    }

    function build() {
        if (root) return;
        ensureStyles();

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
            '    <button class="vm-tour-btn vm-tour-btn--cyan" type="button" data-act="next"></button>' +
            '  </div>' +
            '</div>';

        root.appendChild(blocker);
        root.appendChild(hole);
        root.appendChild(pop);
        document.body.appendChild(root);

        popTitle = pop.querySelector('.vm-tour-pop__title-text');
        popBody = pop.querySelector('.vm-tour-pop__body');
        popCounter = pop.querySelector('.vm-tour-counter');
        btnBack = pop.querySelector('[data-act="back"]');
        btnNext = pop.querySelector('[data-act="next"]');
        btnSkip = pop.querySelector('.vm-tour-skip');

        btnBack.addEventListener('click', () => { if (step > 0) showStep(step - 1); });
        btnNext.addEventListener('click', () => {
            if (step >= steps.length - 1) finish();
            else showStep(step + 1);
        });
        btnSkip.addEventListener('click', finish);
        // Block stray clicks reaching the app, but ignore drag noise.
        blocker.addEventListener('click', (e) => e.stopPropagation());
    }

    function switchView(view) {
        const link = document.querySelector('#nav-list a[data-view="' + view + '"]');
        if (link) link.click();
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
        const minLeft = Math.min(MIN_LEFT, maxLeft);
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
        popTitle.textContent = t(steps[step].title);
        popBody.textContent = t(steps[step].body);
        popCounter.textContent = (step + 1) + ' / ' + steps.length;
        btnBack.textContent = t('tour_back');
        btnBack.style.visibility = step === 0 ? 'hidden' : 'visible';
        btnNext.textContent = step >= steps.length - 1 ? t('tour_finish') : t('tour_next');
        btnSkip.textContent = t('tour_skip');
        btnSkip.style.visibility = step >= steps.length - 1 ? 'hidden' : 'visible';
    }

    function measureAndPlace(s) {
        const el = s.el ? document.querySelector(s.el) : null;
        if (el && document.getElementById('main-content')?.contains(el)) {
            try { el.scrollIntoView({ block: 'center', inline: 'nearest' }); } catch {}
        }
        requestAnimationFrame(() => {
            place(el, s.placement);
            pop.setAttribute('data-show', 'true');
        });
    }

    function showStep(i) {
        step = Math.max(0, Math.min(steps.length - 1, i));
        const s = steps[step];
        renderControls();
        pop.setAttribute('data-show', 'false');

        if (s.view && s.view !== curView) {
            curView = s.view;
            switchView(s.view);
            // Let the view-swap animation (~250ms) settle before measuring.
            setTimeout(() => measureAndPlace(s), 330);
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
        root.setAttribute('data-reduce', reduceMotion() ? 'true' : 'false');
        root.hidden = false;
        window.addEventListener('resize', onViewportChange);
        document.addEventListener('keydown', onKeydown, true);
        showStep(0);
    }

    function teardown() {
        if (!root) return;
        active = false;
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
            if (step >= steps.length - 1) finish(); else showStep(step + 1);
        } else if (e.key === 'ArrowLeft') {
            e.preventDefault();
            if (step > 0) showStep(step - 1);
        }
    }

    function blocked() {
        // Don't auto-launch over the setup / update / welcome overlays.
        const ids = ['setup-overlay', 'update-modal-overlay', 'welcome-overlay'];
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

    window.__tour = { open, close: finish, isOpen: () => active };
})();
