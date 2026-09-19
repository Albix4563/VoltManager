import {
  cpSync,
  existsSync,
  mkdirSync,
  mkdtempSync,
  writeFileSync,
} from 'node:fs';
import { join } from 'node:path';

const VALID_BENCHMARK_EXIT_CODES = new Set([0, 1, 2]);
const WINDOWS_NATIVE_EXCEPTION_MIN = 0xC0000000;

export function selectResourceValidationShell(platform, override) {
  const requested = typeof override === 'string' ? override.trim() : '';
  if (requested) return requested;
  return platform === 'win32' ? 'powershell.exe' : 'pwsh';
}

export function classifyResourceValidationProcess(result, expectedExitCode) {
  const status = Number.isInteger(result?.status) ? result.status : null;
  const signal = result?.signal ?? null;

  if (result?.error?.code === 'ETIMEDOUT') {
    return { kind: 'timeout', status, signal };
  }
  if (signal || (status !== null && status >= WINDOWS_NATIVE_EXCEPTION_MIN)) {
    return { kind: 'crash', status, signal };
  }
  if (status === null || !VALID_BENCHMARK_EXIT_CODES.has(status)) {
    return { kind: 'support-process-error', status, signal };
  }
  return {
    kind: 'benchmark',
    status,
    signal,
    matchesExpectedExit: status === expectedExitCode,
  };
}

export function persistResourceValidationFailure({
  artifactRoot,
  scenario,
  short,
  expectedExitCode,
  classification,
  result,
  shellInfo,
  output,
}) {
  mkdirSync(artifactRoot, { recursive: true });
  const safeScenario = String(scenario).replace(/[^a-z0-9_.-]+/gi, '-');
  const diagnosticDir = mkdtempSync(join(artifactRoot, safeScenario + '-'));

  const metadata = {
    generatedAtUtc: new Date().toISOString(),
    scenario,
    short: Boolean(short),
    expectedExitCode,
    actualExitCode: Number.isInteger(result?.status) ? result.status : null,
    signal: result?.signal ?? null,
    spawnError: result?.error
      ? {
          name: result.error.name ?? null,
          message: result.error.message ?? String(result.error),
          code: result.error.code ?? null,
        }
      : null,
    classification,
    shell: shellInfo ?? null,
    sourceOutput: output,
  };

  writeFileSync(join(diagnosticDir, 'diagnostics.json'), JSON.stringify(metadata, null, 2) + '\n');
  writeFileSync(join(diagnosticDir, 'stdout.log'), result?.stdout ?? '');
  writeFileSync(join(diagnosticDir, 'stderr.log'), result?.stderr ?? '');

  if (output && existsSync(output)) {
    cpSync(output, join(diagnosticDir, 'output'), { recursive: true });
  }

  return diagnosticDir;
}
