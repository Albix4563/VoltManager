(function () {
  'use strict';

  const win = window;
  const doc = document;
  const html = doc.documentElement;
  const Volt = win.Volt = win.Volt || {};
  if (Volt.style && Volt.style.__phase1Controller) {
    Volt.style.init();
    return;
  }

  const THEMES = ['blue', 'red', 'green', 'orange', 'purple', 'pink', 'gray'];
  const PALETTE_KEYS = [
    'background', 'surface', 'surfaceElevated', 'primary', 'secondary',
    'hover', 'text', 'mutedText', 'border', 'onPrimary',
  ];
  const CUSTOM_BASE = [11, 17, 32];
  const RESOURCE_PROFILES = ['full', 'balanced', 'gaming', 'workload', 'critical'];
  const ANIMATION_LEVELS = ['auto', 'low', 'medium', 'high'];
  const ON = 85;
  const OFF = 75;
  const customTokenMap = {
    '--vm-bg': 'background', '--vm-bg-deep': 'background',
    '--vm-surface': 'surface', '--vm-surface-low': 'surface',
    '--vm-surface-high': 'surfaceElevated', '--vm-panel': 'surface', '--vm-card': 'surfaceElevated',
    '--vm-border': 'border', '--vm-border-strong': 'secondary',
    '--vm-text': 'text', '--vm-muted': 'mutedText', '--vm-muted-soft': 'mutedText',
    '--vm-accent': 'primary', '--vm-accent-dim': 'secondary', '--vm-accent-hover': 'hover',
    '--vm-accent-text': 'onPrimary', '--vm-on-accent': 'onPrimary',
  };

  const state = {
    initialized: false,
    listenersBound: false,
    mediaBound: false,
    theme: 'blue',
    palette: null,
    customColor: null,
    catalog: {},
    animationSetting: 'auto',
    hardwareTier: null,
    ramLite: false,
    resourceLite: false,
    effectiveLite: false,
    resourceProfile: null,
    reducedMotion: false,
    hidden: !!doc.hidden,
    effects: null,
    motion: null,
    perfTier: null,
  };
  const boundHostChannels = new WeakMap();
  const motionQuery = win.matchMedia ? win.matchMedia('(prefers-reduced-motion: reduce)') : null;

  function createRuntimeStyleOwner() {
    const records = new WeakMap();
    const recordsById = new Map();
    const PRUNE_THRESHOLD = 128;
    let sheet = null;
    let sheetAttempted = false;
    let sheetBackend = 'none';
    let nextId = 0;

    function fallbackSheet() {
      const sheets = Array.from(doc.styleSheets || []).reverse();
      for (const candidate of sheets) {
        try {
          void candidate.cssRules;
          if (typeof candidate.insertRule === 'function' && typeof candidate.deleteRule === 'function')
            return candidate;
        } catch (_) {}
      }
      return null;
    }

    function ensureSheet() {
      if (sheet) return sheet;
      if (sheetAttempted) return null;
      sheetAttempted = true;
      if (typeof win.CSSStyleSheet === 'function') {
        try {
          const candidate = new win.CSSStyleSheet();
          if (typeof candidate.replaceSync === 'function') {
            candidate.replaceSync('');
            const adopted = Array.from(doc.adoptedStyleSheets || []);
            doc.adoptedStyleSheets = adopted.concat(candidate);
            sheet = candidate;
            sheetBackend = 'constructable';
          }
        } catch (_) {
          sheet = null;
        }
      }
      if (!sheet) {
        sheet = fallbackSheet();
        if (sheet) sheetBackend = 'existing';
      }
      return sheet;
    }

    function recordElement(record) {
      if (!record) return null;
      return record.ref && typeof record.ref.deref === 'function' ? record.ref.deref() : record.ref;
    }

    function removeRule(record) {
      if (!sheet || !record || !record.wrapper) return;
      try {
        const rules = Array.from(sheet.cssRules || []);
        const index = rules.indexOf(record.wrapper);
        if (index >= 0) sheet.deleteRule(index);
      } catch (_) {}
    }

    function releaseRecord(record, element) {
      if (!record) return false;
      removeRule(record);
      const target = element || recordElement(record);
      if (target) {
        if (target.dataset) delete target.dataset.vmStyleId;
        else if (typeof target.removeAttribute === 'function') target.removeAttribute('data-vm-style-id');
        records.delete(target);
      }
      recordsById.delete(record.id);
      return true;
    }

    function pruneDisconnected() {
      for (const record of Array.from(recordsById.values())) {
        const element = recordElement(record);
        if (!element || (record.wasConnected && element.isConnected === false)) releaseRecord(record, element);
      }
      return recordsById.size;
    }

    function ensureRecord(element) {
      if (!element) return null;
      let record = records.get(element);
      if (record) {
        if (element.isConnected !== false) record.wasConnected = true;
        return record;
      }
      pruneDisconnected();
      if (recordsById.size >= PRUNE_THRESHOLD) pruneDisconnected();
      const id = 'vm-runtime-' + (++nextId);
      if (element.dataset) element.dataset.vmStyleId = id;
      else if (typeof element.setAttribute === 'function') element.setAttribute('data-vm-style-id', id);
      const Ref = typeof win.WeakRef === 'function' ? win.WeakRef : null;
      record = {
        id,
        ref: Ref ? new Ref(element) : element,
        rule: null,
        wrapper: null,
        values: new Map(),
        wasConnected: element.isConnected !== false,
      };
      const owner = ensureSheet();
      if (owner) {
        try {
          const index = owner.insertRule('@layer overrides { [data-vm-style-id="' + id + '"] {} }', owner.cssRules.length);
          const wrapper = owner.cssRules[index];
          record.rule = wrapper && wrapper.cssRules ? wrapper.cssRules[0] : null;
          record.wrapper = wrapper || null;
        } catch (_) {}
      }
      records.set(element, record);
      recordsById.set(id, record);
      return record;
    }

    function set(element, property, value) {
      if (!element || typeof property !== 'string' || !property.trim()) return false;
      const record = ensureRecord(element);
      if (!record) return false;
      const normalized = value == null ? '' : String(value);
      record.values.set(property, normalized);
      if (record.rule && record.rule.style && typeof record.rule.style.setProperty === 'function') {
        try {
          record.rule.style.setProperty(property, normalized);
        } catch (_) { return false; }
      }
      return true;
    }

    function setMany(element, declarations) {
      if (!declarations || typeof declarations !== 'object') return false;
      let ok = true;
      for (const [property, value] of Object.entries(declarations)) ok = set(element, property, value) && ok;
      return ok;
    }

    function remove(element, property) {
      const record = element && records.get(element);
      if (!record) return false;
      record.values.delete(property);
      if (record.rule && record.rule.style && typeof record.rule.style.removeProperty === 'function') {
        try { record.rule.style.removeProperty(property); } catch (_) { return false; }
      }
      return true;
    }

    function release(element) {
      const record = element && records.get(element);
      if (!record) return false;
      return releaseRecord(record, element);
    }

    function get(element, property) {
      const record = element && records.get(element);
      return record && record.values.has(property) ? record.values.get(property) : '';
    }

    function stats() {
      pruneDisconnected();
      return {
        records: recordsById.size,
        rules: sheet ? Array.from(sheet.cssRules || []).length : 0,
        constructable: sheetBackend === 'constructable',
        backend: sheetBackend,
      };
    }

    // WebView2 uses a constructable sheet. Environments without constructable
    // sheets fall back to mutating an already-loaded same-document stylesheet
    // through CSSOM. Both paths insert only @layer overrides rules; there is
    // deliberately no inline-style or dynamically-created <style> fallback.
    return { set, setMany, remove, release, get, prune: pruneDisconnected, stats };
  }

  const runtime = createRuntimeStyleOwner();

  function normalize(themeColor) {
    const value = typeof themeColor === 'string' ? themeColor.trim().toLowerCase() : '';
    return THEMES.includes(value) ? value : 'blue';
  }

  function normalizeCustomColor(input) {
    const value = typeof input === 'string' ? input.trim() : '';
    if (!/^#(?:[0-9a-f]{3}|[0-9a-f]{4}|[0-9a-f]{6})$/i.test(value)) return null;
    const hex = value.slice(1);
    let r;
    let g;
    let b;
    let a = 255;
    if (hex.length === 3 || hex.length === 4) {
      r = parseInt(hex[0] + hex[0], 16);
      g = parseInt(hex[1] + hex[1], 16);
      b = parseInt(hex[2] + hex[2], 16);
      if (hex.length === 4) a = parseInt(hex[3] + hex[3], 16);
    } else {
      r = parseInt(hex.slice(0, 2), 16);
      g = parseInt(hex.slice(2, 4), 16);
      b = parseInt(hex.slice(4, 6), 16);
    }
    if (a < 255) {
      const weight = a / 255;
      r = Math.round(r * weight + CUSTOM_BASE[0] * (1 - weight));
      g = Math.round(g * weight + CUSTOM_BASE[1] * (1 - weight));
      b = Math.round(b * weight + CUSTOM_BASE[2] * (1 - weight));
    }
    return '#' + [r, g, b].map(channel => channel.toString(16).padStart(2, '0').toUpperCase()).join('');
  }

  function isSafePalette(palette) {
    return !!palette && PALETTE_KEYS.every(key =>
      typeof palette[key] === 'string' && /^#[0-9a-f]{6}$/i.test(palette[key]));
  }

  function toRgbChannels(hex) {
    const value = String(hex || '').replace('#', '');
    if (!/^[0-9a-f]{6}$/i.test(value)) return '59 130 246';
    return [0, 2, 4].map(index => parseInt(value.slice(index, index + 2), 16)).join(' ');
  }

  function visitRules(rules, visitor) {
    if (!rules) return null;
    for (const rule of Array.from(rules)) {
      const found = visitor(rule);
      if (found) return found;
      if (rule && rule.cssRules) {
        const nested = visitRules(rule.cssRules, visitor);
        if (nested) return nested;
      }
    }
    return null;
  }

  function customThemeRule() {
    try {
      for (const sheet of Array.from(doc.styleSheets || [])) {
        const href = String(sheet.href || '');
        if (href && !/\/css\/tokens\.css(?:[?#]|$)|\/tokens\.css(?:[?#]|$)/i.test(href)) continue;
        const found = visitRules(sheet.cssRules, rule =>
          rule && rule.selectorText === ':root[data-theme="custom"]' ? rule : null);
        if (found) return found;
      }
    } catch (_) {}
    return null;
  }

  function writeCustomPalette(palette) {
    if (!isSafePalette(palette)) return false;
    const rule = customThemeRule();
    const declaration = rule && rule.style;
    if (!declaration || typeof declaration.setProperty !== 'function') return false;
    try {
      for (const [token, key] of Object.entries(customTokenMap)) declaration.setProperty(token, palette[key]);
      declaration.setProperty('--vm-accent-rgb', toRgbChannels(palette.primary));
      return true;
    } catch (_) {
      return false;
    }
  }

  function emit(name, detail) {
    if (typeof doc.dispatchEvent !== 'function' || typeof CustomEvent !== 'function') return;
    doc.dispatchEvent(new CustomEvent(name, { detail }));
  }

  function applyTheme(themeColor, palette) {
    const requested = typeof themeColor === 'string' ? themeColor.trim().toLowerCase() : '';
    let applied = requested === 'custom' ? 'custom' : normalize(requested);
    let resolvedPalette = palette;
    if (applied === 'custom') {
      if (!isSafePalette(resolvedPalette) || !writeCustomPalette(resolvedPalette)) applied = 'blue';
    }
    html.dataset.theme = applied;
    html.dataset.themeColor = applied; // phase-1 compatibility attribute
    state.theme = applied;
    state.palette = applied === 'custom' && isSafePalette(resolvedPalette) ? Object.assign({}, resolvedPalette) : null;
    return applied;
  }

  function setThemeCatalog(catalog) {
    state.catalog = catalog && typeof catalog === 'object' ? catalog : {};
    win.__voltThemeCatalog = state.catalog;
  }

  function acceptThemeState(themeState) {
    if (!themeState || !themeState.themeColor) return state.theme;
    const applied = applyTheme(themeState.themeColor, themeState.palette);
    const accepted = {
      themeColor: applied,
      palette: isSafePalette(themeState.palette) ? Object.assign({}, themeState.palette) : null,
      customColor: themeState.customColor || null,
    };
    if (accepted.palette && applied !== 'custom') state.catalog[applied] = accepted.palette;
    state.customColor = applied === 'custom' ? accepted.customColor : null;
    win.__voltThemeState = accepted;
    win.__voltThemeCatalog = state.catalog;
    emit('themechange', accepted);
    return applied;
  }

  function recommendedLevel(tier) {
    if (tier === 'lite') return 'low';
    if (tier === 'full') return 'high';
    return 'medium';
  }

  function classifyHardwareTier(ramGb, cores) {
    const ram = Number(ramGb);
    const cpu = Number(cores);
    if (!Number.isFinite(ram) || !Number.isFinite(cpu)) return 'full';
    if (ram < 8 || cpu <= 2) return 'lite';
    if (ram < 16 || cpu <= 4) return 'balanced';
    return 'full';
  }

  function resolveLevel(setting, tier) {
    return ['low', 'medium', 'high'].includes(setting) ? setting : recommendedLevel(tier);
  }

  function stopMotion() {
    if (win.VoltFx && typeof win.VoltFx.stopMotion === 'function') win.VoltFx.stopMotion();
  }

  function syncPolicy(options) {
    const opts = options || {};
    const previousEffects = state.effects;
    const previousMotion = state.motion;
    const resolved = state.hardwareTier ? resolveLevel(state.animationSetting, state.hardwareTier) : null;
    const tier = resolved === 'low' ? 'lite' : resolved === 'high' ? 'full' : resolved === 'medium' ? 'balanced' : null;
    state.perfTier = tier;
    state.motion = state.reducedMotion ? 'reduced' : (resolved || 'auto');
    state.effects = state.hidden || state.reducedMotion || state.effectiveLite
      ? 'off'
      : resolved === 'high' ? 'full' : 'reduced';

    html.dataset.motion = state.motion;
    html.dataset.effects = state.effects;
    html.dataset.resourceProfile = state.resourceProfile ? state.resourceProfile.profile : 'full';
    html.dataset.perf = state.effectiveLite ? 'lite' : '';
    if (state.hardwareTier) html.dataset.hwTier = state.hardwareTier;
    if (tier) html.dataset.perfTier = tier;
    else delete html.dataset.perfTier;
    if (resolved) {
      html.dataset.anim = resolved;
      html.dataset.animationLevel = resolved;
    }
    if (resolved === 'high') html.dataset.fx = 'rich';
    else delete html.dataset.fx;

    if (tier === 'lite' || state.resourceLite || (state.effects !== 'full' && previousEffects !== state.effects)) stopMotion();
    if (opts.emitTier && tier) {
      emit('perftierchange', { tier, hwTier: state.hardwareTier, animationLevel: resolved });
    }
    if (previousEffects !== state.effects || previousMotion !== state.motion || opts.forceStyleEvent) {
      emit('voltstylechange', snapshot());
    }
  }

  function setAnimationLevel(level) {
    state.animationSetting = ANIMATION_LEVELS.includes(level) ? level : 'auto';
    syncPolicy({ emitTier: true });
  }

  function setHardwareInfo(info) {
    if (!info) return;
    state.hardwareTier = classifyHardwareTier(info.ramTotalGb, info.logicalCores);
    syncPolicy({ emitTier: true });
  }

  function decideRamLite(current, pct) {
    if (!current && pct >= ON) return true;
    if (current && pct <= OFF) return false;
    return current;
  }

  function setRamPercent(pct) {
    if (typeof pct !== 'number' || !Number.isFinite(pct)) return;
    const next = decideRamLite(state.ramLite, pct);
    if (next === state.ramLite) return;
    state.ramLite = next;
    syncEffectiveLite();
  }

  function setResourceProfile(profileState) {
    if (!profileState) return;
    const candidate = String(profileState.profile || 'full').toLowerCase();
    const profile = RESOURCE_PROFILES.includes(candidate) ? candidate : 'full';
    const normalized = Object.assign({}, profileState, { profile });
    const previous = state.resourceProfile;
    state.resourceProfile = normalized;
    win.VoltResourceProfile = normalized;
    state.resourceLite = !!normalized.reducedEffects || profile === 'gaming' || profile === 'workload' || profile === 'critical';
    syncEffectiveLite();
    const keys = ['profile', 'uiVisible', 'uiActive', 'reducedEffects', 'allowProcessPolling', 'processPollingIntervalMs', 'metricsIntervalMs'];
    if (!previous || keys.some(key => previous[key] !== normalized[key])) emit('resourceprofilechange', normalized);
  }

  function syncEffectiveLite() {
    const next = state.ramLite || state.resourceLite;
    if (next === state.effectiveLite) {
      syncPolicy();
      return;
    }
    state.effectiveLite = next;
    syncPolicy({ forceStyleEvent: true });
    if (state.effectiveLite) stopMotion();
    emit('perfmodechange', { lite: state.effectiveLite, ramLite: state.ramLite, resourceLite: state.resourceLite });
  }

  function currentSettings(detail) {
    const direct = detail && (detail.settings || detail);
    if (direct && typeof direct === 'object') return direct;
    const store = win.__voltSettings;
    return store && (typeof store.get === 'function' ? store.get() : store);
  }

  function hydrateSettings(payload) {
    if (!payload) return;
    if (payload.themeCatalog) setThemeCatalog(payload.themeCatalog);
    if (payload.theme) acceptThemeState(payload.theme);
    const settings = currentSettings(payload.settings || payload);
    if (settings && typeof settings.animationLevel === 'string') setAnimationLevel(settings.animationLevel);
  }

  function bindHost(host) {
    if (!host || typeof host.on !== 'function') return false;
    let channels = boundHostChannels.get(host);
    if (!channels) {
      channels = new Set();
      boundHostChannels.set(host, channels);
    }
    let bound = true;
    const safeOn = (name, handler) => {
      if (channels.has(name)) return;
      try {
        host.on(name, handler);
        channels.add(name);
      } catch (_) {
        bound = false;
      }
    };
    safeOn('metrics', metrics => { if (metrics) setRamPercent(metrics.ramPct); });
    safeOn('resourceProfileChanged', setResourceProfile);
    safeOn('themeChanged', acceptThemeState);
    safeOn('animationLevelChanged', data => setAnimationLevel(data && data.level));
    return bound;
  }

  function snapshot() {
    return {
      theme: state.theme,
      effects: state.effects,
      motion: state.motion,
      animationSetting: state.animationSetting,
      animationLevel: state.hardwareTier ? resolveLevel(state.animationSetting, state.hardwareTier) : null,
      hardwareTier: state.hardwareTier,
      resourceProfile: state.resourceProfile ? state.resourceProfile.profile : 'full',
      ramLite: state.ramLite,
      resourceLite: state.resourceLite,
      reducedMotion: state.reducedMotion,
      hidden: state.hidden,
    };
  }

  function isMotionReduced() {
    return state.reducedMotion;
  }

  function bindDocumentListeners() {
    if (state.listenersBound || typeof doc.addEventListener !== 'function') return;
    state.listenersBound = true;
    doc.addEventListener('systeminfoloaded', event => setHardwareInfo(event.detail || win.VoltSystemInfo));
    doc.addEventListener('settingsloaded', event => hydrateSettings(event.detail || currentSettings()));
    doc.addEventListener('animationlevelchange', event => setAnimationLevel(event.detail && event.detail.level));
    doc.addEventListener('visibilitychange', () => {
      state.hidden = !!doc.hidden;
      syncPolicy({ forceStyleEvent: true });
    });
  }

  function bindMotionPreference() {
    if (state.mediaBound || !motionQuery) return;
    state.mediaBound = true;
    state.reducedMotion = !!motionQuery.matches;
    const onChange = event => {
      state.reducedMotion = !!event.matches;
      syncPolicy({ forceStyleEvent: true });
    };
    if (typeof motionQuery.addEventListener === 'function') motionQuery.addEventListener('change', onChange);
    else if (typeof motionQuery.addListener === 'function') motionQuery.addListener(onChange);
  }

  function init() {
    if (!state.initialized) {
      state.initialized = true;
      bindDocumentListeners();
      bindMotionPreference();
      const bootstrap = win.__voltThemeState || null;
      if (bootstrap && bootstrap.themeColor) acceptThemeState(bootstrap);
      else applyTheme(html.dataset.theme || html.dataset.themeColor || 'blue');
      if (win.VoltSystemInfo) setHardwareInfo(win.VoltSystemInfo);
      const settings = currentSettings();
      if (settings && typeof settings.animationLevel === 'string') setAnimationLevel(settings.animationLevel);
      bindHost(win.Host);
    } else {
      bindHost(win.Host);
    }
    syncPolicy();
    return api;
  }

  const api = Volt.style = {
    __phase1Controller: true,
    init,
    bindHost,
    normalize,
    normalizeCustomColor,
    isSafePalette,
    applyTheme,
    acceptThemeState,
    setThemeCatalog,
    hydrateSettings,
    setAnimationLevel,
    setHardwareInfo,
    setResourceProfile,
    setRamPercent,
    classifyHardwareTier,
    recommendedLevel,
    resolveLevel,
    isMotionReduced,
    runtime,
    getState: snapshot,
  };

  init();
})();
