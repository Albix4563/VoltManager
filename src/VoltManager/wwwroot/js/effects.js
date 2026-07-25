/**
 * VoltFx — additive motion/rendering layer.
 * Exposes window.VoltFx tween helpers (consumed by dashboard.js) and wires
 * ambient aurora, pointer-tracking spotlight, hover sheen, gradient titles,
 * button ripple and load-reactive rings. All continuous motion is disabled
 * under prefers-reduced-motion.
 */
(function () {
  const motionQuery = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)');
  // Aurora/pointer effects off by default: continuous blur layers pushed the
  // WebView GPU process toward ~100MB. Rings/bars still chase via VoltFx
  // unless lite/hidden. Opt back in with <html data-fx="rich">.
  const reduce = () => !!(
    (motionQuery && motionQuery.matches) ||
    document.hidden ||
    document.documentElement.dataset.perf === 'lite' ||
    document.documentElement.dataset.perfTier !== 'full' ||
    document.documentElement.dataset.fx !== 'rich'
  );
  const CIRC = 251.2; // 2*PI*r(40), matches dashboard ring geometry

  function currentAccent() {
    return getComputedStyle(document.documentElement).getPropertyValue('--vm-accent').trim() || '#3b82f6';
  }

  // Theme accent -> amber -> red as utilization climbs. Mirrors effects.css halo tiers.
  function ringColor(pct) {
    const accent = currentAccent();
    if (pct >= 90) return { stroke: '#ff5a6a', load: 'high' };
    if (pct >= 72) return { stroke: '#ffc14a', load: 'mid' };
    return { stroke: accent, load: 'low' };
  }

  // ---- Continuous damped-chase engine -------------------------------------
  // One shared rAF loop eases every registered channel toward its *moving*
  // target. Live metrics arrive ~1/s; the old code restarted a fixed 900ms
  // easeOut tween each tick, so the fill decelerated to a near-stop, paused,
  // then snapped into a fresh tween — read as stutter. Here a value is never
  // restarted: each tick only retargets, and an exponential (frame-rate-
  // independent) approach keeps it gliding so it's still in motion when the
  // next sample lands. HALF_LIFE is tuned so a 1s-cadence metric closes ~85%
  // of the gap per second — smooth, still responsive to real spikes.
  const HALF_LIFE = 350; // ms to halve the remaining gap
  const channels = new Map(); // el -> { value, target, render, eps }
  let rafId = 0;
  let lastTs = 0;

  function frame(ts) {
    const dt = lastTs ? Math.min(100, ts - lastTs) : 16; // clamp post-throttle jumps
    lastTs = ts;
    const alpha = 1 - Math.pow(2, -dt / HALF_LIFE);
    let alive = false;
    channels.forEach((ch) => {
      const gap = ch.target - ch.value;
      if (Math.abs(gap) <= ch.eps) {
        if (ch.value !== ch.target) { ch.value = ch.target; ch.render(ch.value); }
        return; // settled — kept idle in the map so the next retarget eases from here
      }
      ch.value += gap * alpha;
      ch.render(ch.value);
      alive = true;
    });
    if (alive) { rafId = requestAnimationFrame(frame); }
    else { rafId = 0; lastTs = 0; } // all idle — park the loop until next retarget
  }

  // Register/retarget a channel. First sighting snaps (no animate-from-zero);
  // reduced-motion always snaps and never starts the loop.
  function chase(el, target, render, eps) {
    if (reduce()) { channels.delete(el); render(target); return; }
    const ch = channels.get(el);
    if (!ch) { channels.set(el, { value: target, target, render, eps }); render(target); return; }
    ch.target = target;
    ch.render = render;
    ch.eps = eps;
    if (!rafId) { lastTs = 0; rafId = requestAnimationFrame(frame); }
  }

  const VoltFx = {
    /** Count-up toward a moving target. `signed` keeps a leading + for >0. */
    animateNumber(el, target, { suffix = '', decimals = 0, signed = false } = {}) {
      if (!el) return;
      const eps = decimals > 0 ? 0.5 * Math.pow(10, -decimals) : 0.4;
      chase(el, target, (v) => {
        el.textContent = (signed && v > 0 ? '+' : '') + v.toFixed(decimals) + suffix;
      }, eps);
    },

    /** Smooth ring fill + count-up + load-reactive colour. */
    animateRing(circle, label, pct) {
      if (!circle) return;
      const clamped = Math.max(0, Math.min(100, pct));
      // rAF is the sole driver of the fill — strip the CSS transition so it
      // can't re-ease every per-frame write (that layering smeared the chase).
      if (circle.dataset.fxRing !== '1') {
        circle.style.transition = 'stroke .4s ease, filter .4s ease';
        circle.dataset.fxRing = '1';
      }
      // Promote only while the chase runs; drop will-change when settled.
      circle.style.willChange = reduce() ? 'auto' : 'stroke-dashoffset';
      const toOff = CIRC * (1 - clamped / 100);
      chase(circle, toOff, (v) => {
        circle.style.strokeDashoffset = v.toFixed(1);
        if (Math.abs(toOff - v) <= 0.3) circle.style.willChange = 'auto';
      }, 0.3);
      if (label) this.animateNumber(label, clamped, { suffix: '%' });
      const { stroke, load } = ringColor(clamped);
      circle.style.stroke = stroke;
      circle.style.filter = 'drop-shadow(0 0 6px ' + stroke + 'aa)';
      const card = circle.closest('.glass-card');
      if (card) card.dataset.load = load;
    },

    /** Fill a linear bar — compositor-only via scaleX (no per-frame layout).
     *  Bar is pinned to full width once, then scaled from the left. */
    animateBar(bar, pct) {
      if (!bar) return;
      const clamped = Math.max(0, Math.min(100, pct));
      if (bar.dataset.fxBar !== '1') {
        bar.style.width = '100%';
        bar.style.transformOrigin = 'left center';
        bar.style.transition = 'none';
        bar.dataset.fxBar = '1';
      }
      bar.style.willChange = reduce() ? 'auto' : 'transform';
      chase(bar, clamped, (v) => {
        bar.style.transform = 'scaleX(' + (v / 100).toFixed(4) + ')';
        if (Math.abs(clamped - v) <= 0.15) bar.style.willChange = 'auto';
      }, 0.15);
    },

    /** Freeze + drop every running tween at once. perf-guard.js calls this when
     *  RAM-pressure lite mode kicks in so the rAF loop parks immediately instead
     *  of waiting for each channel's next retarget. */
    stopMotion() {
      channels.clear();
      if (rafId) { cancelAnimationFrame(rafId); rafId = 0; lastTs = 0; }
    },
  };
  window.VoltFx = VoltFx;

  // ---- Aurora background ----
  function mountAurora() {
    const main = document.querySelector('main.flex-1') || document.querySelector('main');
    if (!main) return;
    const mounted = main.querySelector('.vm-aurora');
    if (mounted) { mounted.classList.remove('hidden'); return; }
    const aurora = document.createElement('div');
    aurora.className = 'vm-aurora';
    aurora.setAttribute('aria-hidden', 'true');
    // Two orbs are enough for ambient wash; a third only inflated GPU layers.
    aurora.innerHTML =
      '<span class="vm-aurora__orb vm-aurora__orb--1"></span>' +
      '<span class="vm-aurora__orb vm-aurora__orb--2"></span>';
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
  const RIPPLE_SEL = '.btn-glow, .btn-primary, .btn-ghost, .nav-item, .pm-seg';
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

  let richEffectsMounted = false;

  function mountRichEffects() {
    if (richEffectsMounted) return;
    richEffectsMounted = true;
    mountAurora();
    document.addEventListener('pointermove', onPointerMove, { passive: true });
    document.addEventListener('pointerdown', onRipple, { passive: true });
  }

  function unmountRichEffects() {
    if (!richEffectsMounted) return;
    richEffectsMounted = false;
    document.removeEventListener('pointermove', onPointerMove);
    document.removeEventListener('pointerdown', onRipple);
    if (spotEl) spotEl.classList.remove('fx-spot');
    spotEl = null;
    document.querySelector('.vm-aurora')?.classList.add('hidden');
    VoltFx.stopMotion();
  }

  function syncRichEffects() {
    if (reduce()) unmountRichEffects();
    else mountRichEffects();
  }

  function init() {
    decorateTitles();
    syncRichEffects();
    document.addEventListener('viewchange', decorateTitles);
    document.addEventListener('navmounted', decorateTitles);
    document.addEventListener('perftierchange', syncRichEffects);
    document.addEventListener('perfmodechange', syncRichEffects);
    document.addEventListener('visibilitychange', syncRichEffects);
    if (motionQuery) motionQuery.addEventListener('change', syncRichEffects);
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
