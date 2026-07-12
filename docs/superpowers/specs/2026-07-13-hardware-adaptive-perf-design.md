# Hardware-adaptive UI performance — Design

**Date:** 2026-07-13  
**Status:** Approved for planning  
**Goal:** Adattare effetti/animazioni UI all’hardware del PC; comportamento funzionale invariato; commit+push su `main` e allineamento branch.

## Problem

VoltManager UI (WebView2) usa aurora blur, backdrop-filter, spotlight, rAF tweens. Su hardware debole jank/OOM. Oggi esiste solo **lite mode runtime** su pressione RAM (`perf-guard.js` → `html[data-perf="lite"]`). Manca classificazione **statica a boot** basata su capacità hardware.

## Goals

- Classificare hardware a boot (RAM totale + logical cores).
- Ridurre solo effetti visivi su tier bassi.
- Mantenere lite dinamico RAM esistente.
- PC capaci (`full`) = pixel/behaviour identico a oggi.
- Zero regressioni funzionali (piani, automazioni, poll metriche, i18n, theme).
- Commit + push `main`; allineare `Dev`, `Preview`, `fix/ui-button-text-overflow` (e altri locali) a `main`.

## Non-goals

- Toggle utente Impostazioni (Auto/Full/Lite).
- Cambiare cadenza metriche / sensor provider.
- Benchmark micro a boot.
- Classificazione GPU/VRAM o batteria (fuori scope v1).
- Nuove dipendenze.

## Approach (scelto: A — estendi perf-guard)

Estendere il path esistente invece di un nuovo servizio C#.

```
Boot → HardwareInfoService (+ LogicalCores)
     → Host.call('getSystemInfo') / VoltSystemInfo / systeminfoloaded
     → perf-guard.js classify() → html[data-perf-tier]
Metrics → ramPct hysteresis → html[data-perf=lite]  (invariato)
effects.css / effects.js leggono tier + lite + prefers-reduced-motion
```

## Architecture

### Signals

| Signal | Attribute | Lifetime | Source |
|--------|-----------|----------|--------|
| Hardware tier | `html[data-perf-tier="full\|balanced\|lite"]` | Once at boot | `ramTotalGb` + `logicalCores` |
| RAM pressure | `html[data-perf="lite"]` | Runtime hysteresis | `metrics.ramPct` (ON 85 / OFF 75) |
| OS a11y | `prefers-reduced-motion` | OS | `matchMedia` |

**Compositing rule (OR):** lite visuals se `data-perf="lite"` **OR** `data-perf-tier="lite"` **OR** reduced-motion.  
`balanced` = subset CSS; non forza `stopMotion` rAF.  
`full` = zero cambio rispetto a oggi.

### Classification (JS, pure, testable)

```
classify(ramGb, cores):
  if ram/cores missing or NaN → "full"   // fail-open high-end UX
  if ramGb < 8  OR cores ≤ 2 → "lite"
  if ramGb < 16 OR cores ≤ 4 → "balanced"
  else → "full"
```

Edges: 7.9 GB → lite; 8.0 GB + 4 cores → balanced; 16 GB + 5 cores → full; 16 GB + 4 cores → balanced (cores ≤ 4).

### What each tier cuts (visual only)

| Feature | full | balanced | lite (tier or RAM) |
|---------|------|----------|--------------------|
| Aurora orbs animated | 3 | 0–1 static / hidden | off |
| Backdrop blur glass/nav | on | off (opaque fallback) | off |
| Spotlight / sheen | on | off | off |
| Title shimmer / nav comet / ping | on | on | off |
| rAF number/ring/bar chase | on | on | snap + stopMotion |
| Load halos (box-shadow) | on | on | off |
| App logic / metrics poll | unchanged | unchanged | unchanged |

## Components

### C# (minimal)

1. **`Models.SystemInfo`** — add:
   ```csharp
   [JsonPropertyName("logicalCores")] public int LogicalCores { get; init; }
   ```
2. **`HardwareInfoService.GetSystemInfo`** — set `LogicalCores = Environment.ProcessorCount`.
3. **Bridge** — already returns `GetSystemInfo()` for `getSystemInfo`; no protocol change.

`MetricsSnapshot.ramTotalGb` already exists for live metrics but boot classification uses static `SystemInfo.ramTotalGb` (total installed), not pressure %.

### JS

1. **`perf-guard.js`**
   - Keep `decide()` RAM hysteresis + `data-perf` + `stopMotion` + `perfmodechange`.
   - Add `classify(ramGb, cores)` + `console.assert` table.
   - Apply tier: prefer `window.VoltSystemInfo` if already set; else listen `systeminfoloaded`; else optional `Host.call('getSystemInfo')` if Host available and info not yet loaded (avoid double-fetch if app.js always loads first — prefer event).
   - Default tier until info: omit attribute or `full` (same visual).
   - Export nothing unless useful for tests; keep IIFE.

2. **`effects.js`**
   - Extend `reduce()`:
     ```js
     reduced-motion OR data-perf==="lite" OR data-perf-tier==="lite"
     ```
   - No other API change. Balanced does not enter `reduce()`.

3. **`effects.css`**
   - Merge lite selectors: `html[data-perf="lite"] …, html[data-perf-tier="lite"] …` for existing lite block.
   - New `html[data-perf-tier="balanced"]` block: hide/static aurora, kill backdrop-filter on glass/nav/pm-subnav (same opaque tokens as lite), hide spotlight/sheen pseudo; leave shimmer/ping/rAF alone.
   - `full`: no rules.

4. **`index.html`** — bump cache query on `effects.css` / `perf-guard.js` if present in script tags.

### Load order

Confirm `perf-guard.js` loads after bridge and can receive `systeminfoloaded` from `app.js` refresh path. If perf-guard runs before app.js fetch, event listener is required (not only sync read).

## Data flow

1. App start → `HardwareInfoService` caches `SystemInfo` (cores + RAM GB).
2. WebView ready → `app.js` `Host.call('getSystemInfo')` → `window.VoltSystemInfo` + `systeminfoloaded`.
3. `perf-guard` classifies once → `dataset.perfTier`.
4. Metrics stream → existing RAM lite path.
5. CSS/JS react to attributes.

## Error handling

| Case | Behavior |
|------|----------|
| `getSystemInfo` fails / late | Stay `full` until data; if never arrives, remain full |
| NaN / undefined ram or cores | `full` |
| Host unavailable (dev browser) | `full`; reduced-motion still works |
| Tier lite then RAM lite | Idempotent; same CSS |

## Testing

- **JS self-check** in `perf-guard.js`: `classify` table + existing hysteresis asserts.
- **Optional C#**: if tests cover `HardwareInfoService`, assert `LogicalCores == Environment.ProcessorCount`; otherwise skip (field is one-liner).
- **Manual**: on machine ≥16 GB and >4 logical cores → `data-perf-tier="full"`, UI identical; force tier via DevTools to verify CSS.
- **Regression**: plan switch, dashboard metrics, welcome, settings still work.

## Rollout / git

1. Implement on `main` (clean working tree).
2. Verify (build/tests if cheap; self-checks).
3. Commit with message focused on hardware-adaptive perf.
4. Push `origin main`.
5. Align local branches `Dev`, `Preview`, `fix/ui-button-text-overflow` to `main` (merge or reset-to-main only if safe/fast-forward intent; prefer merge main into each or checkout + merge). Push updated remotes where they track origin.

## Invariants (must hold after change)

- Functional behaviour identical on `full` hardware.
- No settings UI, no new packages.
- RAM lite path bit-compatible (`data-perf`, thresholds 85/75).
- Metrics poll interval unchanged.
- Italian/English/etc. untouched.

## File list (expected diff)

- `src/VoltManager/Models/Models.cs`
- `src/VoltManager/Services/HardwareInfoService.cs`
- `src/VoltManager/wwwroot/js/perf-guard.js`
- `src/VoltManager/wwwroot/js/effects.js`
- `src/VoltManager/wwwroot/css/effects.css`
- `src/VoltManager/wwwroot/index.html` (cache bust only if scripts/links need it)
- This spec under `docs/superpowers/specs/`

## Open decisions (resolved)

- Trigger: hardware boot + RAM runtime.
- Signals: RAM + CPU cores only.
- Cuts: visual effects only.
- Approach: extend perf-guard (A).
