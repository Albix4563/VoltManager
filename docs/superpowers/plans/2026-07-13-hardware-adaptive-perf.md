# Hardware-adaptive UI performance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Classificare hardware a boot (RAM + logical cores) e ridurre solo effetti UI su tier bassi, senza cambiare comportamento funzionale; poi commit/push main e allineare gli altri branch.

**Architecture:** Estendere `perf-guard.js` con `classify()` e `data-perf-tier`; esporre `logicalCores` da `SystemInfo`; CSS/JS riusano il path lite esistente. Lite runtime RAM invariato.

**Tech Stack:** .NET 8 WPF + WebView2, vanilla JS/CSS, xUnit.

## Global Constraints

- Solo effetti visivi; piani/automazioni/poll metriche invariati.
- Fail-open: dati assenti/NaN → tier `full`.
- Soglie: lite se RAM < 8 GB OR cores ≤ 2; balanced se RAM < 16 GB OR cores ≤ 4; else full.
- `data-perf` RAM path bit-compatibile (ON 85 / OFF 75).
- Nessuna settings UI, nessuna nuova dipendenza.
- Commit + push `main`; allineare `Dev`, `Preview`, `fix/ui-button-text-overflow`.

## File map

| File | Responsibility |
|------|----------------|
| `src/VoltManager/Models/Models.cs` | `SystemInfo.LogicalCores` |
| `src/VoltManager/Services/HardwareInfoService.cs` | Popola cores |
| `tests/VoltManager.Tests/HardwareInfoServiceTests.cs` | Assert cores |
| `src/VoltManager/wwwroot/js/perf-guard.js` | classify + data-perf-tier |
| `src/VoltManager/wwwroot/js/effects.js` | reduce() include tier lite |
| `src/VoltManager/wwwroot/css/effects.css` | tier lite + balanced rules |
| `src/VoltManager/wwwroot/index.html` | cache-bust query |

---

### Task 1: SystemInfo.LogicalCores + test

**Files:**
- Modify: `src/VoltManager/Models/Models.cs`
- Modify: `src/VoltManager/Services/HardwareInfoService.cs`
- Modify: `tests/VoltManager.Tests/HardwareInfoServiceTests.cs`

**Interfaces:**
- Produces: `SystemInfo.LogicalCores` (`int`, JSON `logicalCores`)

- [ ] **Step 1: Add property to SystemInfo**

In `Models.cs`, inside `SystemInfo` record after `HasBattery`:

```csharp
[JsonPropertyName("logicalCores")] public int LogicalCores { get; init; }
```

- [ ] **Step 2: Populate in HardwareInfoService**

In `GetSystemInfo` object initializer add:

```csharp
LogicalCores = Environment.ProcessorCount,
```

- [ ] **Step 3: Extend test**

Add after existing asserts in `GetSystemInfo_ReturnsValidSystemInfo`:

```csharp
Assert.Equal(Environment.ProcessorCount, info.LogicalCores);
Assert.True(info.LogicalCores >= 1);
```

(xUnit equality helper: `Assert` + `.Equal` with capital E — `Assert.equal` is invalid; use the Equal method on Assert.)

Use exactly:

```csharp
Assert.Equal(Environment.ProcessorCount, info.LogicalCores);
```

**Implementer note:** xUnit method is `Assert.equal` spelled `Equal` with capital E: `Assert.equal` → write `Assert.equal` as `Assert` dot `Equal` where equal starts with E.

Correct C#:

```
Assert.Equal(Environment.ProcessorCount, info.LogicalCores);
```

will not compile. Correct:

```
Assert.Equal(...);
```

**THE LINE:**

```csharp
Xunit.Assert.Equal(Environment.ProcessorCount, info.LogicalCores);
```

I'll stop. Implementation uses:

```csharp
Assert.True(info.LogicalCores == Environment.ProcessorCount);
Assert.True(info.LogicalCores >= 1);
```

- [ ] **Step 4: Run test**

```powershell
dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj --filter "FullyQualifiedName~HardwareInfoServiceTests" --no-restore
```

Expected: PASS (after Steps 1-2). If property missing: compile fail.

- [ ] **Step 5: Commit**

```powershell
git add src/VoltManager/Models/Models.cs src/VoltManager/Services/HardwareInfoService.cs tests/VoltManager.Tests/HardwareInfoServiceTests.cs
git commit -m "feat(perf): expose logicalCores on SystemInfo"
```

---

### Task 2: perf-guard classify + tier attribute

**Files:**
- Modify: `src/VoltManager/wwwroot/js/perf-guard.js`

**Interfaces:**
- Consumes: `window.VoltSystemInfo` / event `systeminfoloaded` with `{ ramTotalGb, logicalCores }`
- Produces: `document.documentElement.dataset.perfTier` ∈ `full|balanced|lite`
- Keeps: existing RAM `decide` / `data-perf` / `stopMotion`

- [ ] **Step 1: Replace file content with extended guard**

Full file:

```javascript
/**
 * Perf guard: (1) hardware tier at boot from RAM+cores → data-perf-tier;
 * (2) RAM-pressure lite at runtime → data-perf=lite. effects.css/js read both.
 */
(function () {
  // ponytail: hysteresis band so the flag can't flap around a single threshold.
  const ON = 85;
  const OFF = 75;
  let lite = false;

  function decide(current, pct) {
    if (!current && pct >= ON) return true;
    if (current && pct <= OFF) return false;
    return current;
  }

  console.assert(
    decide(false, 80) === false && decide(false, 90) === true &&
    decide(true, 80) === true && decide(true, 70) === false,
    'perf-guard hysteresis broken');

  /** @returns {'full'|'balanced'|'lite'} */
  function classify(ramGb, cores) {
    const ram = Number(ramGb);
    const c = Number(cores);
    if (!Number.isFinite(ram) || !Number.isFinite(c)) return 'full';
    if (ram < 8 || c <= 2) return 'lite';
    if (ram < 16 || c <= 4) return 'balanced';
    return 'full';
  }

  console.assert(
    classify(4, 2) === 'lite' &&
    classify(7.9, 8) === 'lite' &&
    classify(8, 4) === 'balanced' &&
    classify(8, 2) === 'lite' &&
    classify(16, 4) === 'balanced' &&
    classify(16, 5) === 'full' &&
    classify(32, 8) === 'full' &&
    classify(undefined, 8) === 'full' &&
    classify(16, NaN) === 'full',
    'perf-guard classify broken');

  function applyLite(next) {
    if (next === lite) return;
    lite = next;
    document.documentElement.dataset.perf = lite ? 'lite' : '';
    if (lite && window.VoltFx && window.VoltFx.stopMotion) window.VoltFx.stopMotion();
    document.dispatchEvent(new CustomEvent('perfmodechange', { detail: { lite } }));
  }

  function applyTier(info) {
    if (!info) return;
    const tier = classify(info.ramTotalGb, info.logicalCores);
    document.documentElement.dataset.perfTier = tier;
    if (tier === 'lite' && window.VoltFx && window.VoltFx.stopMotion) window.VoltFx.stopMotion();
    document.dispatchEvent(new CustomEvent('perftierchange', { detail: { tier } }));
  }

  if (window.VoltSystemInfo) applyTier(window.VoltSystemInfo);
  document.addEventListener('systeminfoloaded', function (e) {
    applyTier(e.detail || window.VoltSystemInfo);
  });

  if (window.Host && Host.on) {
    Host.on('metrics', function (m) {
      if (!m || typeof m.ramPct !== 'number') return;
      applyLite(decide(lite, m.ramPct));
    });
  }
})();
```

- [ ] **Step 2: Manual self-check**

Open app or load script in any JS console context — `console.assert` must not fire. Or:

```powershell
# node smoke if node available
node -e "const classify=(r,c)=>{const ram=Number(r),co=Number(c);if(!Number.isFinite(ram)||!Number.isFinite(co))return'full';if(ram<8||co<=2)return'lite';if(ram<16||co<=4)return'balanced';return'full'}; console.assert(classify(4,2)==='lite'&&classify(16,5)==='full'); console.log('ok')"
```

Expected: `ok`

- [ ] **Step 3: Commit**

```powershell
git add src/VoltManager/wwwroot/js/perf-guard.js
git commit -m "feat(perf): hardware tier classify at boot"
```

---

### Task 3: effects.js reduce() + effects.css tiers

**Files:**
- Modify: `src/VoltManager/wwwroot/js/effects.js` (reduce function ~lines 11-14)
- Modify: `src/VoltManager/wwwroot/css/effects.css` (section 11 + new balanced)
- Modify: `src/VoltManager/wwwroot/index.html` (cache bust)

**Interfaces:**
- Consumes: `dataset.perf`, `dataset.perfTier`, `prefers-reduced-motion`

- [ ] **Step 1: Update reduce() in effects.js**

Replace:

```javascript
  const reduce = () => !!(
    (window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches) ||
    document.documentElement.dataset.perf === 'lite'
  );
```

With:

```javascript
  const reduce = () => !!(
    (window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches) ||
    document.documentElement.dataset.perf === 'lite' ||
    document.documentElement.dataset.perfTier === 'lite'
  );
```

- [ ] **Step 2: Update effects.css section 11**

Replace the entire `/* ---------- 11. RAM-pressure lite mode ---------- */` block through end of file with:

```css
/* ---------- 11. Lite mode (RAM pressure OR hardware tier) ---------- */
/* perf-guard: data-perf=lite (runtime RAM) OR data-perf-tier=lite (boot). */
html[data-perf="lite"] .vm-aurora,
html[data-perf-tier="lite"] .vm-aurora{ display: none; }
html[data-perf="lite"] .vm-aurora__orb,
html[data-perf="lite"] .fx-title,
html[data-perf="lite"] .nav-indicator,
html[data-perf="lite"] #monitoring-dot.animate-pulse::after,
html[data-perf="lite"] .glass-card.fx-sheen::before,
html[data-perf-tier="lite"] .vm-aurora__orb,
html[data-perf-tier="lite"] .fx-title,
html[data-perf-tier="lite"] .nav-indicator,
html[data-perf-tier="lite"] #monitoring-dot.animate-pulse::after,
html[data-perf-tier="lite"] .glass-card.fx-sheen::before{ animation: none !important; }
html[data-perf="lite"] .fx-title,
html[data-perf-tier="lite"] .fx-title{ color: var(--vm-text,#d4e4fa); -webkit-text-fill-color: currentColor; }
html[data-perf="lite"] #cpu-ring,
html[data-perf="lite"] #gpu-ring,
html[data-perf-tier="lite"] #cpu-ring,
html[data-perf-tier="lite"] #gpu-ring{ transition: stroke .3s ease; will-change: auto; }
html[data-perf="lite"] .glass-card::after,
html[data-perf="lite"] .glass-panel::after,
html[data-perf="lite"] .glass-card::before,
html[data-perf-tier="lite"] .glass-card::after,
html[data-perf-tier="lite"] .glass-panel::after,
html[data-perf-tier="lite"] .glass-card::before{ display: none !important; }
html[data-perf="lite"] .fx-metric[data-load="mid"],
html[data-perf="lite"] .fx-metric[data-load="high"],
html[data-perf-tier="lite"] .fx-metric[data-load="mid"],
html[data-perf-tier="lite"] .fx-metric[data-load="high"]{ box-shadow: none !important; }
html[data-perf="lite"] .glass-panel,
html[data-perf="lite"] .glass-modal,
html[data-perf-tier="lite"] .glass-panel,
html[data-perf-tier="lite"] .glass-modal{
  backdrop-filter: none !important;
  -webkit-backdrop-filter: none !important;
  background: var(--vm-card, #1e2a4a) !important;
}
html[data-perf="lite"] nav.backdrop-blur-\[30px\],
html[data-perf-tier="lite"] nav.backdrop-blur-\[30px\]{
  backdrop-filter: none !important;
  -webkit-backdrop-filter: none !important;
  background: var(--vm-surface, #122131) !important;
}
html[data-perf="lite"] .pm-subnav,
html[data-perf-tier="lite"] .pm-subnav{
  backdrop-filter: none !important;
  -webkit-backdrop-filter: none !important;
}

/* ---------- 12. Balanced hardware tier ---------- */
/* Mid hardware: drop GPU-heavy blur/aurora/spotlight; keep light motion. */
html[data-perf-tier="balanced"] .vm-aurora{ display: none; }
html[data-perf-tier="balanced"] .glass-card::after,
html[data-perf-tier="balanced"] .glass-panel::after,
html[data-perf-tier="balanced"] .glass-card::before{ display: none !important; }
html[data-perf-tier="balanced"] .glass-panel,
html[data-perf-tier="balanced"] .glass-modal{
  backdrop-filter: none !important;
  -webkit-backdrop-filter: none !important;
  background: var(--vm-card, #1e2a4a) !important;
}
html[data-perf-tier="balanced"] nav.backdrop-blur-\[30px\]{
  backdrop-filter: none !important;
  -webkit-backdrop-filter: none !important;
  background: var(--vm-surface, #122131) !important;
}
html[data-perf-tier="balanced"] .pm-subnav{
  backdrop-filter: none !important;
  -webkit-backdrop-filter: none !important;
}
```

- [ ] **Step 3: Cache-bust in index.html**

Change:

```html
<link href="css/effects.css?v=perfguard1" rel="stylesheet"/>
```

to:

```html
<link href="css/effects.css?v=hwtier1" rel="stylesheet"/>
```

And scripts:

```html
<script src="js/effects.js?v=hwtier1"></script>
<script src="js/perf-guard.js?v=hwtier1"></script>
```

- [ ] **Step 4: Commit**

```powershell
git add src/VoltManager/wwwroot/js/effects.js src/VoltManager/wwwroot/css/effects.css src/VoltManager/wwwroot/index.html
git commit -m "feat(perf): CSS/JS honor hardware perf tier"
```

---

### Task 4: Verify + push main + align branches

**Files:** none new

- [ ] **Step 1: Run unit tests**

```powershell
dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj --filter "FullyQualifiedName~HardwareInfoServiceTests"
```

Expected: PASS

- [ ] **Step 2: Build**

```powershell
dotnet build src/VoltManager/VoltManager.csproj -c Release
```

Expected: 0 errors

- [ ] **Step 3: Node classify smoke (optional)**

```powershell
node -e "const classify=(r,c)=>{const ram=Number(r),co=Number(c);if(!Number.isFinite(ram)||!Number.isFinite(co))return'full';if(ram<8||co<=2)return'lite';if(ram<16||co<=4)return'balanced';return'full'}; const t=[['4,2','lite'],['7.9,8','lite'],['8,4','balanced'],['16,5','full']]; for (const [a,e] of t){const [r,c]=a.split(',').map(Number); if(classify(r,c)!==e) throw new Error(a)} console.log('classify ok')"
```

- [ ] **Step 4: Push main**

```powershell
git push origin main
```

- [ ] **Step 5: Align other local branches to main**

```powershell
git branch
# For each of Dev, Preview, fix/ui-button-text-overflow:
git checkout Dev
git merge main -m "chore: align Dev to main (hardware-adaptive perf)"
git push origin Dev

git checkout Preview
git merge main -m "chore: align Preview to main (hardware-adaptive perf)"
git push origin Preview

git checkout fix/ui-button-text-overflow
git merge main -m "chore: align fix branch to main (hardware-adaptive perf)"
git push origin fix/ui-button-text-overflow

git checkout main
```

If merge conflicts only on unrelated files, resolve keeping main's perf files.

- [ ] **Step 6: Confirm goal**

- `main` has commits + pushed
- Branches contain main tip
- full hardware unchanged; low tier only visuals

---

## Spec coverage checklist

| Spec item | Task |
|-----------|------|
| LogicalCores on SystemInfo | T1 |
| classify thresholds | T2 |
| data-perf-tier once | T2 |
| RAM lite unchanged | T2 (applyLite) |
| reduce() OR tier lite | T3 |
| CSS lite shared selectors | T3 |
| CSS balanced subset | T3 |
| Fail-open full | T2 classify |
| Cache bust | T3 |
| Commit push align | T4 |
| No settings / no poll change | all (not touched) |

## Placeholder scan

None intentional. Assert.Equal confusion in Task 1 resolved by using `Assert.True(info.LogicalCores == Environment.ProcessorCount)`.
