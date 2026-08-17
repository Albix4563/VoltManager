/**
 * Energy tips popup.
 *
 * A lightweight, browsable carousel of power-saving advice, opened from the
 * lightbulb button in the top bar (#btn-energy-tips). Styled with the same
 * glass-modal / btn-primary / btn-ghost classes the rest of the app already uses,
 * so it inherits the active AppThemeColor palette for free — no Tailwind
 * recompile needed.
 *
 * Content lives in i18n.js (tip1..tipN _title/_body), so tips are translated
 * alongside the rest of the UI. The tip order is reshuffled on each open so the
 * user sees a fresh suggestion first. Re-openable any time; also exposed as
 * window.__tips.open().
 */
(function () {
    // Each tip = a Material Symbols icon + the i18n keys for its title/body.
    const tips = [
        { icon: 'battery_saver',       title: 'tip1_title', body: 'tip1_body' },
        { icon: 'power',               title: 'tip2_title', body: 'tip2_body' },
        { icon: 'brightness_6',        title: 'tip3_title', body: 'tip3_body' },
        { icon: 'rocket_launch',       title: 'tip4_title', body: 'tip4_body' },
        { icon: 'speed',               title: 'tip5_title', body: 'tip5_body' },
        { icon: 'memory',              title: 'tip6_title', body: 'tip6_body' },
        { icon: 'developer_board',     title: 'tip7_title', body: 'tip7_body' },
        { icon: 'tune',                title: 'tip8_title', body: 'tip8_body' },
    ];

    let overlay, modal, iconEl, titleEl, bodyEl, counterEl, dotsEl, btnPrev, btnNext, btnClose, btnOpen;
    let order = tips.map((_, i) => i); // current (possibly shuffled) display order
    let pos = 0;                        // index into `order`
    let active = false;
    let wired = false;

    function t(key) {
        return (window.I18n && I18n.t) ? I18n.t(key) : key;
    }

    function shuffle(arr) {
        const a = arr.slice();
        for (let i = a.length - 1; i > 0; i--) {
            const j = Math.floor(Math.random() * (i + 1));
            [a[i], a[j]] = [a[j], a[i]];
        }
        return a;
    }

    function buildDots() {
        if (!dotsEl) return;
        dotsEl.innerHTML = '';
        for (let i = 0; i < order.length; i++) {
            const dot = document.createElement('span');
            dot.className = 'welcome-dot';
            dot.dataset.go = String(i);
            dotsEl.appendChild(dot);
        }
    }

    function render() {
        const tip = tips[order[pos]];
        if (!tip) return;
        if (iconEl) iconEl.textContent = tip.icon;
        if (titleEl) titleEl.textContent = t(tip.title);
        if (bodyEl) bodyEl.textContent = t(tip.body);
        if (counterEl) counterEl.textContent = (pos + 1) + ' / ' + order.length;

        const last = pos >= order.length - 1;
        if (btnPrev) btnPrev.style.visibility = pos === 0 ? 'hidden' : 'visible';
        if (btnNext) btnNext.textContent = last ? t('tips_close') : t('tips_next');
        if (btnPrev) btnPrev.textContent = t('tips_prev');

        if (dotsEl) {
            Array.from(dotsEl.children).forEach((d, i) =>
                d.setAttribute('data-active', i === pos ? 'true' : 'false'));
        }
    }

    function go(i) {
        pos = Math.max(0, Math.min(order.length - 1, i));
        render();
    }

    function open() {
        if (!overlay) return;
        order = shuffle(tips.map((_, i) => i));
        pos = 0;
        buildDots();
        render();
        overlay.classList.remove('hidden');
        overlay.classList.add('flex');
        active = true;
        if (btnNext) { try { btnNext.focus(); } catch (e) {} }
    }

    function close() {
        if (!overlay) return;
        overlay.classList.add('hidden');
        overlay.classList.remove('flex');
        active = false;
        if (btnOpen) { try { btnOpen.focus(); } catch (e) {} }
    }

    function onKeydown(e) {
        if (!active) return;
        if (e.key === 'Escape') { e.preventDefault(); close(); }
        else if (e.key === 'ArrowRight') { e.preventDefault(); if (pos >= order.length - 1) close(); else go(pos + 1); }
        else if (e.key === 'ArrowLeft') { e.preventDefault(); if (pos > 0) go(pos - 1); }
    }

    function refreshButtonTitle() {
        if (btnOpen) btnOpen.title = t('tips_btn_title');
    }

    function wire() {
        if (wired) return;
        overlay = document.getElementById('energy-tips-overlay');
        modal = document.getElementById('energy-tips-modal');
        iconEl = document.getElementById('energy-tip-icon');
        titleEl = document.getElementById('energy-tip-title');
        bodyEl = document.getElementById('energy-tip-body');
        counterEl = document.getElementById('energy-tip-counter');
        dotsEl = document.getElementById('energy-tip-dots');
        btnPrev = document.getElementById('energy-tip-prev');
        btnNext = document.getElementById('energy-tip-next');
        btnClose = document.getElementById('energy-tips-close');
        btnOpen = document.getElementById('btn-energy-tips');
        if (!overlay || !btnOpen) return;

        btnOpen.addEventListener('click', open);
        if (btnClose) btnClose.addEventListener('click', close);
        if (btnPrev) btnPrev.addEventListener('click', () => { if (pos > 0) go(pos - 1); });
        if (btnNext) btnNext.addEventListener('click', () => {
            if (pos >= order.length - 1) close(); else go(pos + 1);
        });
        if (dotsEl) dotsEl.addEventListener('click', (e) => {
            const dot = e.target.closest('[data-go]');
            if (dot) go(parseInt(dot.dataset.go, 10) || 0);
        });
        // Click on the dimmed backdrop (outside the modal) closes.
        overlay.addEventListener('click', (e) => { if (e.target === overlay) close(); });
        document.addEventListener('keydown', onKeydown, true);
        document.addEventListener('langchanged', () => { refreshButtonTitle(); if (active) render(); });

        refreshButtonTitle();
        wired = true;
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', wire);
    } else {
        wire();
    }

    window.__tips = { open, close, isOpen: () => active };
})();
