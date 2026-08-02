# Game Detection Evidence Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an explainable confidence engine and process-tree correlation to VoltManager game detection in shadow mode.

**Architecture:** Keep `HeavyAppDetectionService` as the compatibility facade. Add pure, focused types for process-tree traversal and evidence scoring, then enrich each existing detection with all matching signals while preserving its current primary reason and active-state behavior.

**Tech Stack:** C# 12, .NET 8 Windows, WPF host, System.Text.Json, xUnit 2.5.3

## Global Constraints

- Work on branch `Dev` and push completed working batches to `origin/Dev`.
- Do not commit `AGENTS.md`, local agent configuration, secrets, or unplanned binaries.
- Preserve the existing five-second shared process-snapshot architecture.
- Do not change automatic power-plan activation semantics in this increment.
- Do not inject into, hook, or read memory from game processes.
- Follow test-driven development: each production behavior must first be observed failing in an automated test.

---

### Task 1: Confidence evidence model and scorer

**Files:**
- Create: `src/VoltManager/Services/GameDetection/GameDetectionEvidence.cs`
- Create: `src/VoltManager/Services/GameDetection/GameConfidenceScorer.cs`
- Create: `tests/VoltManager.Tests/GameDetection/GameConfidenceScorerTests.cs`

**Interfaces:**
- Consumes: `IEnumerable<GameDetectionEvidence>`
- Produces: `GameDetectionEvidence`, `GameDetectionAssessment`, `GameConfidenceScorer.Score(IEnumerable<GameDetectionEvidence>, string?)`

- [x] **Step 1: Write failing scorer tests**

```csharp
[Fact]
public void Score_caps_correlated_groups_and_applies_penalties()
{
    var assessment = GameConfidenceScorer.Score(new[]
    {
        new GameDetectionEvidence("manifest", "provenance", 30, "Manifest executable"),
        new GameDetectionEvidence("installRoot", "provenance", 20, "Install root"),
        new GameDetectionEvidence("foreground", "runtime", 20, "Foreground"),
        new GameDetectionEvidence("deny", "penalty", -10, "Known helper"),
    }, "manifest");

    Assert.Equal(45, assessment.Score);
    Assert.Equal("unknown", assessment.Level);
}

[Theory]
[InlineData(39, "ignored")]
[InlineData(40, "unknown")]
[InlineData(60, "probable")]
[InlineData(75, "confirmed")]
public void Level_uses_documented_thresholds(int score, string expected)
    => Assert.Equal(expected, GameConfidenceScorer.LevelFor(score));
```

- [x] **Step 2: Run the focused tests and verify RED**

Run: `dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj --configuration Debug --no-restore --filter FullyQualifiedName~GameConfidenceScorerTests`

Expected: compilation fails because the evidence and scorer types do not exist.

- [x] **Step 3: Implement the minimal evidence records and scorer**

Implement immutable JSON-serializable records. Sum positive weights by group with caps `identity=50`, `provenance=35`, `runtime=25`, `history=25`; sum every negative weight; clamp to 0–100; map score thresholds to the four documented level strings.

- [x] **Step 4: Run focused and full tests**

Run the focused command from Step 2, then `dotnet test VoltManager.sln --configuration Debug --no-restore`.

Expected: all tests pass with zero failures.

### Task 2: Bounded process graph

**Files:**
- Create: `src/VoltManager/Services/GameDetection/ProcessGraph.cs`
- Create: `tests/VoltManager.Tests/GameDetection/ProcessGraphTests.cs`

**Interfaces:**
- Consumes: `IEnumerable<ProcessSample>`
- Produces: `ProcessGraph.GetAncestors(int processId, int maxDepth)` and `ProcessGraph.TryFindAncestor(int processId, Func<ProcessSample,bool>, int maxDepth, out ProcessSample)`

- [x] **Step 1: Write failing graph tests**

```csharp
[Fact]
public void GetAncestors_returns_nearest_first_and_honors_depth()
{
    var graph = new ProcessGraph(new[]
    {
        Sample(10, 0, "steam"), Sample(20, 10, "bootstrap"), Sample(30, 20, "game")
    });

    Assert.Equal(new[] { 20, 10 }, graph.GetAncestors(30, 2).Select(p => p.Pid));
    Assert.Single(graph.GetAncestors(30, 1));
}

[Fact]
public void GetAncestors_stops_on_cycles()
{
    var graph = new ProcessGraph(new[] { Sample(10, 20, "a"), Sample(20, 10, "b") });
    Assert.Equal(new[] { 20 }, graph.GetAncestors(10, 8).Select(p => p.Pid));
}
```

- [x] **Step 2: Run focused tests and verify RED**

Run: `dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj --configuration Debug --no-restore --filter FullyQualifiedName~ProcessGraphTests`

Expected: compilation fails because `ProcessGraph` does not exist.

- [x] **Step 3: Implement bounded, cycle-safe traversal**

Index samples by PID, reject non-positive depth, stop on missing/zero/self parent, and track visited PIDs so malformed snapshots cannot loop.

- [x] **Step 4: Run focused and full tests**

Run the focused command from Step 2, then the full solution tests.

Expected: all tests pass with zero failures.

### Task 3: Integrate evidence into heavy-app detection in shadow mode

**Files:**
- Modify: `src/VoltManager/Services/HeavyAppDetectionService.cs`
- Create: `tests/VoltManager.Tests/GameDetection/HeavyAppEvidenceTests.cs`

**Interfaces:**
- Consumes: existing path, process name, working set, GPU preference set, settings, start time, and optional launcher ancestry
- Produces: `HeavyAppDetectionService.AssessProcess(...)`; additional JSON fields `confidenceScore`, `confidenceLevel`, and `evidence` on `DetectedHeavyApp`

- [x] **Step 1: Write failing assessment tests**

```csharp
[Fact]
public void AssessProcess_keeps_primary_reason_and_collects_all_matching_signals()
{
    var cfg = new HeavyAppDetectionSettings();
    var path = HeavyAppDetectionService.NormalizePath(
        @"D:\Steam\steamapps\common\Title\Binaries\Win64\Title-Win64-Shipping.exe");

    var result = HeavyAppDetectionService.AssessProcess(
        path, "Title-Win64-Shipping", 2L * 1024 * 1024 * 1024,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase), cfg,
        DateTime.UtcNow.AddMinutes(-3), DateTime.UtcNow, hasLauncherAncestor: true);

    Assert.Equal("gameInstallPath", result.PrimaryReason);
    Assert.Contains(result.Evidence, e => e.Code == "gameBinaryLayout");
    Assert.Contains(result.Evidence, e => e.Code == "launcherAncestry");
    Assert.Contains(result.Evidence, e => e.Code == "duration2m");
}
```

- [x] **Step 2: Run focused tests and verify RED**

Run: `dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj --configuration Debug --no-restore --filter FullyQualifiedName~HeavyAppEvidenceTests`

Expected: compilation fails because `AssessProcess` and the evidence fields do not exist.

- [x] **Step 3: Implement multi-signal assessment**

Share an allocation-light normalized classifier between `ClassifyProcess` and `AssessProcess`. Collect every enabled applicable signal after the existing shell/helper guard, preserve reason priority, add duration evidence, score it, and return no primary reason for excluded processes.

- [x] **Step 4: Integrate process graph into the scan**

Build one `ProcessGraph` per shared snapshot. For each detected process, search up to three ancestors using the existing known-storefront predicate and add `launcherAncestry` without making it a standalone detection reason.

- [x] **Step 5: Publish evidence without changing activation behavior**

Populate `DetectedHeavyApp.ConfidenceScore`, `.ConfidenceLevel`, and `.Evidence`. Continue adding a process to `detected` whenever the legacy primary reason is non-null, irrespective of confidence.

- [x] **Step 6: Run focused and full tests**

Run the focused command from Step 2 and `dotnet test VoltManager.sln --configuration Debug --no-restore`.

Expected: all tests pass; legacy self-check invariants remain valid.

### Task 4: Verification, documentation check, and publication

**Files:**
- Verify: all files above

**Interfaces:**
- Consumes: completed Tasks 1–3
- Produces: verified Debug tests, Release build, clean intended diff, commit on `Dev`, push to `origin/Dev`

- [x] **Step 1: Run final verification**

Run:

```powershell
dotnet test VoltManager.sln --configuration Debug --no-restore
dotnet build VoltManager.sln --configuration Release --no-restore
```

Expected: both commands exit 0 with no failed tests and no build errors.

- [x] **Step 2: Review the implementation diff and repository status**

Run: `git diff --check`, `git diff --stat`, `git status --short --branch`, and confirm only planned source, test, spec, and plan files are included.

- [x] **Step 3: Commit and push the working batch**

```powershell
git add docs/superpowers/specs/2026-08-02-game-detection-evidence-design.md docs/superpowers/plans/2026-08-02-game-detection-evidence-engine.md src/VoltManager/Services/GameDetection src/VoltManager/Services/HeavyAppDetectionService.cs tests/VoltManager.Tests/GameDetection
git commit -m "Add explainable game detection confidence engine"
git push origin Dev
```

Expected: commit succeeds on `Dev` and `origin/Dev` advances to the new commit.
