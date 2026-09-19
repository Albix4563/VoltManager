# Issue #32 Resource Validation Runtime Crash Implementation Plan

> For agentic workers: REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Make issue #32's deterministic Windows resource-validation test reliable while turning PowerShell host crashes into durable, explicit test failures.

**Architecture:** Move support-process classification and diagnostic persistence into a focused test helper. The resource-validation integration test selects a stable Windows PowerShell host, probes its runtime once, runs the same six benchmark cases without retries, and only parses benchmark summaries after the support process is proven to have produced a valid benchmark-domain exit code.

**Tech Stack:** Node.js test runner, ESM, PowerShell, GitHub Actions on Windows.

**Spec:** docs/superpowers/specs/2026-09-19-issue-32-resource-validation-runtime-crash.md

## Global Constraints

- Branch is Dev; completed work is committed and pushed to origin/Dev.
- Do not add retries or skips for CLR crashes.
- Valid benchmark exit codes are only 0, 1, and 2.
- Failure diagnostics live under TestResults/resource-validation-process-failures/, which the existing CI upload already captures.
- AGENTS.md and local diagnostic files under artifacts/ remain uncommitted.

## Review Focus

- Native Windows exception exit 0xC0000005 must be classified as a crash, never as benchmark inconclusive; Task 1 tests it.
- A timeout with no exit code must be classified as timeout and persist stderr/stdout; Task 1 tests it.
- An unexpected ordinary exit outside 0/1/2 must be a support-process failure; Task 1 tests it.
- Diagnostic persistence must copy partial benchmark output before the temporary run directory is removed; Task 1 tests it with real files.
- Changing the Windows default host back to pwsh must break a test; Task 1 tests host selection.

---

### Task 1: Support-process classification and durable diagnostics

**Files:**
- Create: tests/helpers/resource-validation-process.mjs
- Create: tests/resource-validation-process.test.mjs

**Interfaces:**
- Produces: selectResourceValidationShell(platform, override), classifyResourceValidationProcess(result, expectedExitCode), and persistResourceValidationFailure(options).
- Consumes: Node fs and path only.

- [ ] **Step 1: Write the failing helper tests**

~~~js
import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import {
  classifyResourceValidationProcess,
  persistResourceValidationFailure,
  selectResourceValidationShell,
} from './helpers/resource-validation-process.mjs';

test('selects Windows PowerShell 5.1 by default and honors an override', () => {
  assert.equal(selectResourceValidationShell('win32'), 'powershell.exe');
  assert.equal(selectResourceValidationShell('win32', 'pwsh-preview'), 'pwsh-preview');
});

test('classifies benchmark exits separately from CLR crashes and timeouts', () => {
  assert.equal(classifyResourceValidationProcess({ status: 2, signal: null }, 2).kind, 'benchmark');
  assert.equal(classifyResourceValidationProcess({ status: 3221225477, signal: null, stderr: 'Internal CLR error.' }, 2).kind, 'crash');
  assert.equal(classifyResourceValidationProcess({ status: null, signal: 'SIGTERM', error: { code: 'ETIMEDOUT' } }, 0).kind, 'timeout');
  assert.equal(classifyResourceValidationProcess({ status: 17, signal: null }, 0).kind, 'support-process-error');
});
~~~

Add a third test that writes a real partial benchmark-summary.json, persists a synthetic CLR crash, and asserts diagnostics.json, stderr.log, and the copied partial output.

- [ ] **Step 2: Run the helper test and verify RED**

Run: node --test tests/resource-validation-process.test.mjs

Expected: FAIL because tests/helpers/resource-validation-process.mjs does not exist.

- [ ] **Step 3: Implement the minimal helper**

selectResourceValidationShell returns a non-empty override when supplied, otherwise powershell.exe on win32 and pwsh elsewhere.

classifyResourceValidationProcess returns timeout for ETIMEDOUT; crash for a signal or a Windows native exception status at or above 0xC0000000; support-process-error for missing/non-benchmark exits; and benchmark only for 0, 1, or 2 while recording whether it matches the expected exit.

persistResourceValidationFailure writes diagnostics.json, stdout.log, stderr.log, and recursively copies an existing partial output directory.

- [ ] **Step 4: Run the helper test and verify GREEN**

Run: node --test tests/resource-validation-process.test.mjs

Expected: PASS, 3 tests.

- [ ] **Step 5: Commit Task 1**

~~~powershell
git add tests/helpers/resource-validation-process.mjs tests/resource-validation-process.test.mjs docs/superpowers/specs/2026-09-19-issue-32-resource-validation-runtime-crash.md docs/superpowers/plans/2026-09-19-issue-32-resource-validation-runtime-crash.md
git commit -m "test: classify resource validation process failures"
~~~

### Task 2: Harden the six-scenario integration test

**Files:**
- Modify: tests/resource-validation.test.mjs

**Interfaces:**
- Consumes: Task 1 helper functions.
- Produces: deterministic validation of all six scenario tuples plus durable diagnostics for support-process failures.

- [ ] **Step 1: Integrate process classification before summary parsing**

Import the Task 1 helper. Select the host from process.platform and VOLT_RESOURCE_VALIDATION_SHELL. Probe it once for PowerShell version, Environment.Version, and OS description. For every scenario classify the child result before reading benchmark-summary.json. When the result is not an expected benchmark exit, persist diagnostics under TestResults/resource-validation-process-failures and fail with the diagnostic path.

- [ ] **Step 2: Verify affected runtime produces explicit retained crash evidence**

Run repeatedly with VOLT_RESOURCE_VALIDATION_SHELL=pwsh on the affected local runtime until the known runtime failure appears.

Expected: failure is classified as crash or support-process failure, diagnostics are retained, and no retry occurs.

- [ ] **Step 3: Use the stable default host**

Normal Windows execution uses the Task 1 selector without an override, which resolves to powershell.exe. Keep the six existing tuples and their exit/status/renderer assertions unchanged.

- [ ] **Step 4: Verify the integration test repeatedly**

Run: 1..5 | ForEach-Object { node --test tests/resource-validation.test.mjs; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE } }

Expected: PASS in all five runs.

- [ ] **Step 5: Commit Task 2**

~~~powershell
git add tests/resource-validation.test.mjs
git commit -m "fix: stabilize resource validation test host"
~~~

### Task 3: Full regression and issue evidence

**Files:**
- Modify only if verification reveals a requirement gap.

**Interfaces:**
- Consumes: Task 1 and Task 2 test behavior.
- Produces: local and CI evidence sufficient to close issue #32.

- [ ] **Step 1: Run the complete JavaScript suite twice**

Run: 1..2 | ForEach-Object { node --test tests/*.test.mjs; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE } }

Expected: both full-suite runs PASS.

- [ ] **Step 2: Run .NET regression tests**

Run: dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release -v:minimal

Expected: PASS.

- [ ] **Step 3: Verify repository state**

Run: git status --short --branch

Expected: only intended tracked changes are present; AGENTS.md and artifacts/ are not staged.

- [ ] **Step 4: Push Dev and verify CI**

Run: git push origin Dev, then inspect the GitHub Actions run for the pushed commit.

Expected: CI build-and-test completes successfully on Windows.

- [ ] **Step 5: Close issue #32 with evidence**

Close issue #32 with a concise comment listing the diagnosed runtime constraint, local repeated-test evidence, commit SHA, and successful CI run.

