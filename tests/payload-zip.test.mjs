import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, writeFileSync, rmSync, existsSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';

const repoRoot = resolve(import.meta.dirname, '..');
const zipScript = resolve(repoRoot, 'scripts/New-PayloadZip.ps1');

// Legge lo ZIP con .NET: FullName restituisce il nome entry grezzo, senza normalizzare i separatori.
const readZip = String.raw`
param($Zip)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($Zip)
try {
  $archive.Entries | ForEach-Object {
    $memory = New-Object IO.MemoryStream
    $entryStream = $_.Open()
    try { $entryStream.CopyTo($memory) } finally { $entryStream.Dispose() }
    [pscustomobject]@{ name = $_.FullName; data = [Convert]::ToBase64String($memory.ToArray()) }
    $memory.Dispose()
  } | ConvertTo-Json -Compress
} finally { $archive.Dispose() }
`;

const files = {
  'VoltManager.exe': Buffer.from([0x4d, 0x5a, 0x00, 0xff, 0x10]),
  'empty.txt': Buffer.alloc(0),
  'wwwroot/index.html': Buffer.from('<p>già</p>', 'utf8'),
  'wwwroot/js/app.js': Buffer.from('export {};\n', 'utf8'),
  'wwwroot/js/empty.js': Buffer.alloc(0),
};
const emptyDirs = ['logs', 'wwwroot/css/vuota'];

function run(shell, args) {
  return spawnSync(shell, ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', ...args], {
    encoding: 'utf8', timeout: 30000,
  });
}

for (const shell of ['powershell.exe', 'pwsh']) {
  const probe = process.platform === 'win32' ? run(shell, ['-Command', 'exit 0']) : null;
  const unavailable = !probe || probe.error?.code === 'ENOENT';
  test(`payload zip uses canonical '/' entries on ${shell}`, { skip: unavailable && `${shell} non disponibile` }, () => {
    assert.equal(probe.status, 0, probe.error?.message || probe.stderr || probe.stdout);
    const tmp = mkdtempSync(join(tmpdir(), 'voltmanager-payload-zip-'));
    try {
      const source = join(tmp, 'publish');
      for (const [name, data] of Object.entries(files)) {
        mkdirSync(join(source, name, '..'), { recursive: true });
        writeFileSync(join(source, name), data);
      }
      for (const dir of emptyDirs) mkdirSync(join(source, dir), { recursive: true });
      const zip = join(tmp, 'payload.zip');
      const readerPath = join(tmp, 'read-zip.ps1');
      writeFileSync(readerPath, readZip, 'utf8');

      const created = run(shell, ['-File', zipScript, '-Source', source, '-Destination', zip]);
      assert.equal(created.status, 0, created.stderr || created.stdout);

      const read = run(shell, ['-File', readerPath, '-Zip', zip]);
      assert.equal(read.status, 0, read.stderr || read.stdout);
      const entries = [JSON.parse(read.stdout)].flat();

      const expected = [...Object.keys(files), ...emptyDirs.map(d => `${d}/`)].sort();
      assert.deepEqual(entries.map(e => e.name).sort(), expected);
      for (const entry of entries) {
        assert.ok(!entry.name.includes('\\'), `entry con backslash: ${entry.name}`);
        const data = Buffer.from(entry.data, 'base64');
        assert.deepEqual(data, files[entry.name] ?? Buffer.alloc(0), `contenuto diverso: ${entry.name}`);
      }

      // Un file di destinazione già esistente non viene sovrascritto.
      const again = run(shell, ['-File', zipScript, '-Source', source, '-Destination', zip]);
      assert.notEqual(again.status, 0);
      assert.ok(existsSync(zip));
    } finally {
      rmSync(tmp, { recursive: true, force: true });
    }
  });
}
