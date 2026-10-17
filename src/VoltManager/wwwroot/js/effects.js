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

  let cachedAccent = '';
  function invalidateAccent() { cachedAccent = ''; }
  function currentAccent() {
    if (!cachedAccent) {
      cachedAccent = getComputedStyle(document.documentElement).getPropertyValue('--vm-accent').trim() || '#3b82f6';
    }
    return cachedAccent;
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
  function chase(el, target, render, eps, empty = 0) {
    if (reduce()) { channels.delete(el); render(target); return; }
    const ch = channels.get(el);
    if (!ch) {
      channels.set(el, { value: target, target, render, eps, empty });
      render(target);
      return;
    }
    ch.target = target;
    ch.render = render;
    ch.eps = eps;
    ch.empty = empty;
    if (!rafId) { lastTs = 0; rafId = requestAnimationFrame(frame); }
  }

  const VoltFx = {
    /** Count-up toward a moving target. `signed` keeps a leading + for >0. */
    animateNumber(el, target, { suffix = '', decimals = 0, signed = false } = {}) {
      if (!el) return;
      const eps = decimals > 0 ? 0.5 * Math.pow(10, -decimals) : 0.4;
      chase(el, target, (v) => {
        const nextText = (signed && v > 0 ? '+' : '') + v.toFixed(decimals) + suffix;
        if (el.textContent !== nextText) el.textContent = nextText;
      }, eps, 0);
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
      const nextWill = reduce() ? 'auto' : 'stroke-dashoffset';
      if (circle.style.willChange !== nextWill) circle.style.willChange = nextWill;
      const toOff = CIRC * (1 - clamped / 100);
      chase(circle, toOff, (v) => {
        const nextOff = v.toFixed(1);
        if (circle.style.strokeDashoffset !== nextOff) circle.style.strokeDashoffset = nextOff;
        if (Math.abs(toOff - v) <= 0.3 && circle.style.willChange !== 'auto') {
          circle.style.willChange = 'auto';
        }
      }, 0.3, CIRC);
      if (label) this.animateNumber(label, clamped, { suffix: '%' });
      const { stroke, load } = ringColor(clamped);
      if (circle.dataset.fxStroke !== stroke) {
        circle.dataset.fxStroke = stroke;
        circle.style.stroke = stroke;
        circle.style.filter = 'drop-shadow(0 0 6px ' + stroke + 'aa)';
      }
      const card = circle.closest('.glass-card');
      if (card && card.dataset.load !== load) card.dataset.load = load;
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
      const nextWill = reduce() ? 'auto' : 'transform';
      if (bar.style.willChange !== nextWill) bar.style.willChange = nextWill;
      chase(bar, clamped, (v) => {
        const nextTransform = 'scaleX(' + (v / 100).toFixed(4) + ')';
        if (bar.style.transform !== nextTransform) bar.style.transform = nextTransform;
        if (Math.abs(clamped - v) <= 0.15 && bar.style.willChange !== 'auto') {
          bar.style.willChange = 'auto';
        }
      }, 0.15, 0);
    },

    /** Replay registered dashboard metrics from their empty state to the latest target. */
    replayIntro(root) {
      if (!root || document.documentElement.dataset.anim !== 'high' || reduce()) return;
      let replayed = false;
      channels.forEach((ch, el) => {
        if (!root.contains(el)) return;
        ch.value = ch.empty;
        ch.render(ch.value);
        replayed = true;
      });
      if (replayed && !rafId) {
        lastTs = 0;
        rafId = requestAnimationFrame(frame);
      }
    },

    /** Freeze + drop every running tween at once. perf-guard.js calls this when
     *  RAM-pressure lite mode kicks in so the rAF loop parks immediately instead
     *  of waiting for each channel's next retarget. */
    stopMotion() {
      channels.clear();
      if (rafId) { cancelAnimationFrame(rafId); rafId = 0; lastTs = 0; }
      cancelSpotRaf();
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
  }

  // ---- Pointer-tracking spotlight (delegated + rAF damped micro-glide) ----
  const SPOT_HALF_LIFE = 28; // ms — tight cursor synchronization with silky micro-smoothing
  const SPOT_EPS = 0.08;     // % threshold to park the rAF loop once settled
  let spotEl = null, spotRect = null, spotX = 0, spotY = 0, spotQueued = false;
  let spotRaf = 0, spotLastTs = 0, spotCurX = 50, spotCurY = 50, spotSnap = true;
  let lastMx = '', lastMy = '';
  let isScrolling = false, scrollIdleTimer = 0;

  function cancelSpotRaf() {
    if (spotRaf) { cancelAnimationFrame(spotRaf); spotRaf = 0; }
    spotQueued = false;
    spotLastTs = 0;
  }

  function clearSpot() {
    cancelSpotRaf();
    if (spotEl) spotEl.classList.remove('fx-spot');
    spotEl = null;
    spotRect = null;
    lastMx = '';
    lastMy = '';
  }

  function invalidateSpotRect() {
    spotRect = null;
  }

  function onScroll() {
    spotRect = null;
    cancelSpotRaf();
    if (!isScrolling) {
      isScrolling = true;
      document.documentElement.dataset.scrolling = 'true';
    }
    if (scrollIdleTimer) clearTimeout(scrollIdleTimer);
    scrollIdleTimer = setTimeout(() => {
      scrollIdleTimer = 0;
      isScrolling = false;
      delete document.documentElement.dataset.scrolling;
    }, 120);
  }

  function flushSpot(ts) {
    spotQueued = false;
    spotRaf = 0;
    if (!spotEl || isScrolling) { spotLastTs = 0; return; }
    if (!spotRect) spotRect = spotEl.getBoundingClientRect();
    if (!spotRect.width || !spotRect.height) { spotLastTs = 0; return; }
    const targetX = Math.max(0, Math.min(100, (spotX - spotRect.left) / spotRect.width * 100));
    const targetY = Math.max(0, Math.min(100, (spotY - spotRect.top) / spotRect.height * 100));
    if (spotSnap) {
      spotCurX = targetX;
      spotCurY = targetY;
      spotSnap = false;
      spotLastTs = ts || 0;
    } else {
      const dt = spotLastTs && ts ? Math.min(64, Math.max(1, ts - spotLastTs)) : 16;
      spotLastTs = ts || 0;
      const alpha = 1 - Math.pow(2, -dt / SPOT_HALF_LIFE);
      spotCurX += (targetX - spotCurX) * alpha;
      spotCurY += (targetY - spotCurY) * alpha;
    }
    const settled = Math.abs(targetX - spotCurX) <= SPOT_EPS && Math.abs(targetY - spotCurY) <= SPOT_EPS;
    if (settled) {
      spotCurX = targetX;
      spotCurY = targetY;
    }
    const mx = spotCurX.toFixed(1) + '%';
    const my = spotCurY.toFixed(1) + '%';
    if (mx !== lastMx) { spotEl.style.setProperty('--mx', mx); lastMx = mx; }
    if (my !== lastMy) { spotEl.style.setProperty('--my', my); lastMy = my; }
    if (!settled) {
      spotQueued = true;
      spotRaf = requestAnimationFrame(flushSpot);
    } else {
      spotLastTs = 0;
    }
  }
  function onPointerMove(e) {
    if (isScrolling) return;
    let card = e.target.closest && e.target.closest('.glass-card, .glass-panel');
    // Processes is a large live surface: its rows and meters are refreshed while
    // the pointer moves. Rewriting spotlight coordinates on that same surface
    // invalidates a large raster area every frame in WebView2, which can expose
    // black compositor tiles when the window is maximized. Keep the static
    // theme/hover treatment, but opt this one high-churn card out of pointer FX.
    if (card && card.classList.contains('processes-card')) card = null;
    spotX = e.clientX; spotY = e.clientY;
    if (card !== spotEl) {
      if (spotEl) spotEl.classList.remove('fx-spot');
      spotEl = card;
      spotRect = null;
      spotSnap = true;
      lastMx = '';
      lastMy = '';
      if (!spotEl) {
        cancelSpotRaf();
        return;
      }
      flushSpot(0);
      spotEl.classList.add('fx-spot');
      if (spotEl.classList.contains('glass-card') && !spotEl.classList.contains('fx-sheen')) {
        spotEl.classList.add('fx-sheen');
        spotEl.addEventListener('animationend', function clearSheen(ev) {
          if (ev.animationName === 'vmSheen') {
            ev.currentTarget.classList.remove('fx-sheen');
            ev.currentTarget.removeEventListener('animationend', clearSheen);
          }
        });
      }
      return;
    }
    if (!spotEl) return;
    if (!spotQueued) { spotQueued = true; spotRaf = requestAnimationFrame(flushSpot); }
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
    document.documentElement.addEventListener('pointerleave', clearSpot, { passive: true });
    window.addEventListener('blur', clearSpot, { passive: true });
  }

  function unmountRichEffects() {
    if (!richEffectsMounted) return;
    richEffectsMounted = false;
    document.removeEventListener('pointermove', onPointerMove);
    document.removeEventListener('pointerdown', onRipple);
    document.documentElement.removeEventListener('pointerleave', clearSpot);
    window.removeEventListener('blur', clearSpot);
    clearSpot();
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
    document.addEventListener('scroll', onScroll, { capture: true, passive: true });
    window.addEventListener('resize', invalidateSpotRect, { passive: true });
    document.addEventListener('settingsloaded', invalidateAccent);
    document.addEventListener('themeChanged', invalidateAccent);
    if (typeof MutationObserver === 'function' && document.documentElement) {
      new MutationObserver(invalidateAccent).observe(document.documentElement, {
        attributes: true,
        attributeFilter: ['data-theme-color', 'style']
      });
    }
    document.addEventListener('viewchange', (event) => {
      spotRect = null;
      decorateTitles();
      if (event.detail && event.detail.view === 'monitoring' && event.detail.reorganized) {
        VoltFx.replayIntro(document.querySelector('.vm-reorg-view[data-vm-view="monitoring"]'));
      } else if (event.detail && event.detail.view === 'home' && !event.detail.reorganized) {
        VoltFx.replayIntro(document.getElementById('view-home'));
      }
    });
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
