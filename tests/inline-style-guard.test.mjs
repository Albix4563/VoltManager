import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const webRoot = fileURLToPath(new URL('../src/VoltManager/wwwroot/', import.meta.url));
const appRoot = fileURLToPath(new URL('../src/VoltManager/', import.meta.url));

const checks = [
  ['style element markup', /<style\b/i],
  ['insertAdjacentHTML style attribute', /insertAdjacentHTML\s*\([\s\S]{0,1200}?\bstyle\s*=\s*\\*["'`]/i],
  ['style attribute', /\bstyle\s*=\s*\\*["'`]/i],
  ['DOM style property assignment', /\.style\s*\.\s*(?!(?:cssText|setProperty)\b)[A-Za-z_$][\w$]*\s*=/],
  ['DOM style setProperty', /\.style\s*\.\s*setProperty\s*\(/],
  ['DOM cssText assignment', /\.style\s*\.\s*cssText\s*=/],
  ['setAttribute style', /setAttribute\s*\(\s*\\*["']style\\*["']/i],
  ['style element creation', /createElement\s*\(\s*\\*["']style\\*["']\s*\)/i],
];

function walk(dir, predicate) {
  const files = [];
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) files.push(...walk(full, predicate));
    else if (predicate(full)) files.push(full);
  }
  return files;
}

function withoutRuntimeOwner(file, source) {
  if (path.basename(file) !== 'style-controller.js') return source;
  const start = source.indexOf('function createRuntimeStyleOwner()');
  const endMarker = 'const runtime = createRuntimeStyleOwner();';
  const end = source.indexOf(endMarker, start);
  assert.ok(start >= 0 && end > start, 'style-controller runtime owner allowlist markers must exist');
  return source.slice(0, start) + source.slice(end + endMarker.length);
}

function detect(source) {
  return checks.filter(([, pattern]) => pattern.test(source)).map(([name]) => name);
}

test('frontend and native injected scripts contain no inline style sources outside the runtime owner', () => {
  const offenders = [];
  const frontend = walk(webRoot, file => /\.(?:html|js)$/i.test(file));
  for (const file of frontend) {
    const source = withoutRuntimeOwner(file, readFileSync(file, 'utf8'));
    const hits = detect(source);
    if (hits.length) offenders.push(`${path.relative(webRoot, file)}: ${hits.join(', ')}`);
  }

  const nativeSources = walk(appRoot, file => file.endsWith('.cs'));
  for (const file of nativeSources) {
    const source = readFileSync(file, 'utf8');
    if (!/ExecuteScriptAsync|AddScriptToExecuteOnDocumentCreated/.test(source)) continue;
    const hits = detect(source);
    if (hits.length) offenders.push(`${path.relative(appRoot, file)}: ${hits.join(', ')}`);
  }

  assert.deepEqual(offenders, []);
});

test('inline style detector rejects every supported source form', () => {
  const fixtures = [
    ['<style>.x{display:none}</style>', 'style element markup'],
    ['<div style="display:none"></div>', 'style attribute'],
    ['node.style.width = value;', 'DOM style property assignment'],
    ['node.style.setProperty("--x", value);', 'DOM style setProperty'],
    ['node.style.cssText = "width:1px";', 'DOM cssText assignment'],
    ['node.setAttribute("style", "width:1px");', 'setAttribute style'],
    ['document.createElement("style");', 'style element creation'],
    ["node.insertAdjacentHTML('beforeend', '<span style=\"width:1px\"></span>');", 'insertAdjacentHTML style attribute'],
  ];

  for (const [source, expected] of fixtures) {
    assert.ok(detect(source).includes(expected), `expected detector to catch ${expected}`);
  }
});
