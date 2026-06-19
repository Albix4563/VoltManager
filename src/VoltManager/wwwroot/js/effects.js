/**
 * VoltFx — additive motion/rendering layer.
 * Exposes window.VoltFx tween helpers (consumed by dashboard.js) and wires
 * ambient aurora, pointer-tracking spotlight, hover sheen, gradient titles,
 * button ripple and load-reactive rings. All continuous motion is disabled
 * under prefers-reduced-motion.
 */
(function () {
  const reduce = () => !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
  const CIRC = 251.2; // 2*PI*r(40), matches dashboard ring geometry
  const easeOutCubic = (t) => 1 - Math.pow(1 - t, 3);

  function isLight() { return document.documentElement.dataset.theme === 'light'; }

  // Cyan -> amber -> red as utilization climbs. Mirrors effects.css halo tiers.
  function ringColor(pct) {
    const accent = isLight() ? '#00aebb' : '#00f1fe';
    if (pct >= 90) return { stroke: '#ff5a6a', load: 'high' };
    if (pct >= 72) return { stroke: '#ffc14a', load: 'mid' };
    return { stroke: accent, load: 'low' };
  }

  // ---- Generic rAF tween, one in-flight animation per element ----
  const running = new WeakMap();
  function tween(el, from, to, dur, onStep) {
    const prev = running.get(el);
    if (prev) cancelAnimationFrame(prev);
    if (reduce() || dur <= 0) { onStep(to); running.delete(el); return; }
    const start = performance.now();
    const step = (now) => {
      const t = Math.min(1, (now - start) / dur);
      onStep(from + (to - from) * easeOutCubic(t));
      if (t < 1) running.set(el, requestAnimationFrame(step));
      else running.delete(el);
    };
    running.set(el, requestAnimationFrame(step));
  }

  const lastNum = new WeakMap();

  const VoltFx = {
    /** Count-up tween for a text node. */
    animateNumber(el, target, { suffix = '', decimals = 0, dur = 850 } = {}) {
      if (!el) return;
      const from = lastNum.has(el) ? lastNum.get(el) : target;
      lastNum.set(el, target);
      tween(el, from, target, dur, (v) => { el.textContent = v.toFixed(decimals) + suffix; });
    },

    /** Smooth ring fill + count-up + load-reactive colour. */
    animateRing(circle, label, pct) {
      if (!circle) return;
      const clamped = Math.max(0, Math.min(100, pct));
      // rAF is the sole driver — strip the CSS transition so it can't re-ease
      // every per-frame write (that layering is what made the fill stutter).
      if (circle.dataset.fxRing !== '1') {
        circle.style.transition = 'stroke .4s ease, filter .4s ease';
        circle.style.willChange = 'stroke-dashoffset';
        circle.dataset.fxRing = '1';
      }
      const fromOff = lastNum.has(circle) ? lastNum.get(circle) : (CIRC * (1 - clamped / 100));
      const toOff = CIRC * (1 - clamped / 100);
      lastNum.set(circle, toOff);
      tween(circle, fromOff, toOff, 900, (v) => { circle.style.strokeDashoffset = v.toFixed(1); });
      if (label) this.animateNumber(label, clamped, { suffix: '%' });
      const { stroke, load } = ringColor(clamped);
      circle.style.stroke = stroke;
      circle.style.filter = 'drop-shadow(0 0 6px ' + stroke + 'aa)';
      const card = circle.closest('.glass-card');
      if (card) card.dataset.load = load;
    },

    /** Fill tween for a linear bar — compositor-only via scaleX (no per-frame
     *  layout). Bar is pinned to full width once, then scaled from the left. */
    animateBar(bar, pct, dur = 900) {
      if (!bar) return;
      const clamped = Math.max(0, Math.min(100, pct));
      if (bar.dataset.fxBar !== '1') {
        bar.style.width = '100%';
        bar.style.transformOrigin = 'left center';
        bar.style.transition = 'none';
        bar.style.willChange = 'transform';
        bar.dataset.fxBar = '1';
      }
      const from = lastNum.has(bar) ? lastNum.get(bar) : clamped;
      lastNum.set(bar, clamped);
      tween(bar, from, clamped, dur, (v) => { bar.style.transform = 'scaleX(' + (v / 100).toFixed(4) + ')'; });
    },
  };
  window.VoltFx = VoltFx;

  // ---- Aurora background ----
  function mountAurora() {
    const main = document.querySelector('main.flex-1') || document.querySelector('main');
    if (!main || main.querySelector('.vm-aurora')) return;
    const aurora = document.createElement('div');
    aurora.className = 'vm-aurora';
    aurora.setAttribute('aria-hidden', 'true');
    aurora.innerHTML =
      '<span class="vm-aurora__orb vm-aurora__orb--1"></span>' +
      '<span class="vm-aurora__orb vm-aurora__orb--2"></span>' +
      '<span class="vm-aurora__orb vm-aurora__orb--3"></span>';
    main.insertBefore(aurora, main.firstChild);
    // The legacy static blobs are now redundant under the aurora — drop them.
    main.querySelectorAll(':scope > .absolute.rounded-full.blur-3xl, :scope > .absolute.blur-\\[100px\\]')
      .forEach((el) => el.remove());
  }

  // ---- Pointer-tracking spotlight (delegated + rAF throttled) ----
  let spotEl = null, spotX = 0, spotY = 0, spotQueued = false;
  function flushSpot() {
    spotQueued = false;
    if (!spotEl) return;
    const r = spotEl.getBoundingClientRect();
    spotEl.style.setProperty('--mx', ((spotX - r.left) / r.width * 100).toFixed(1) + '%');
    spotEl.style.setProperty('--my', ((spotY - r.top) / r.height * 100).toFixed(1) + '%');
  }
  function onPointerMove(e) {
    const card = e.target.closest && e.target.closest('.glass-card, .glass-panel');
    if (card !== spotEl) {
      if (spotEl) spotEl.classList.remove('fx-spot');
      spotEl = card;
      if (spotEl) {
        spotEl.classList.add('fx-spot');
        if (spotEl.classList.contains('glass-card')) {
          spotEl.classList.remove('fx-sheen');
          void spotEl.offsetWidth;
          spotEl.classList.add('fx-sheen');
        }
      }
    }
    if (!spotEl) return;
    spotX = e.clientX; spotY = e.clientY;
    if (!spotQueued) { spotQueued = true; requestAnimationFrame(flushSpot); }
  }

  // ---- Button ripple ----
  const RIPPLE_SEL = '.btn-glow, .btn-cyan, .btn-ghost, .nav-item, .pm-seg';
  function onRipple(e) {
    const btn = e.target.closest && e.target.closest(RIPPLE_SEL);
    if (!btn || reduce()) return;
    const r = btn.getBoundingClientRect();
    const size = Math.max(r.width, r.height) * 1.6;
    const span = document.createElement('span');
    span.className = 'vm-ripple';
    span.style.width = span.style.height = size + 'px';
    span.style.left = (e.clientX - r.left) + 'px';
    span.style.top = (e.clientY - r.top) + 'px';
    const cs = getComputedStyle(btn);
    if (cs.position === 'static') btn.style.position = 'relative';
    if (cs.overflow !== 'hidden') btn.style.overflow = 'hidden';
    btn.appendChild(span);
    span.addEventListener('animationend', () => span.remove());
  }

  // ---- Gradient-shimmer headings ----
  function decorateTitles() {
    document.querySelectorAll('.text-headline-lg').forEach((el) => el.classList.add('fx-title'));
  }

  function init() {
    mountAurora();
    decorateTitles();
    document.addEventListener('pointermove', onPointerMove, { passive: true });
    document.addEventListener('pointerdown', onRipple, { passive: true });
    // Re-decorate dynamically-mounted views (e.g. System tab) and re-attach
    // the aurora if the main element is ever rebuilt.
    document.addEventListener('viewchange', decorateTitles);
    document.addEventListener('navmounted', decorateTitles);
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
