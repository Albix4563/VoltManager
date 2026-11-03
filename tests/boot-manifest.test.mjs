import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const wwwroot = path.join(root, 'src/VoltManager/wwwroot');
const read = relative => readFileSync(path.join(root, relative), 'utf8');

const pageSources = Object.fromEntries(
  ['index.html', 'widgets.html'].map(file => [file, readFileSync(path.join(wwwroot, file), 'utf8')])
);

function references(html, tag, attribute) {
  return [...html.matchAll(new RegExp(`<${tag}\\b[^>]*\\b${attribute}="([^"]+)"`, 'g'))]
    .map(match => match[1]);
}

function assetPath(reference) {
  return reference.split('?')[0];
}

function scriptsOf(html) {
  return references(html, 'script', 'src');
}

function stylesOf(html) {
  return [...html.matchAll(/<link\b[^>]*\bhref="([^"]+\.css[^"]*)"/g)].map(match => match[1]);
}

function readSourceTree(directory, extension) {
  return readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const full = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      if (entry.name === 'bin' || entry.name === 'obj') return [];
      return readSourceTree(full, extension);
    }
    return entry.name.endsWith(extension) ? [readFileSync(full, 'utf8')] : [];
  });
}

test('every local script and stylesheet reference resolves to a shipped asset', () => {
  let total = 0;
  for (const [page, html] of Object.entries(pageSources)) {
    const local = [...scriptsOf(html), ...stylesOf(html)]
      .map(assetPath)
      .filter(reference => /^(?:js|css)\//.test(reference));
    total += local.length;
    const missing = local.filter(reference => !existsSync(path.join(wwwroot, reference)));
    assert.deepEqual(missing, [], `${page}: missing local assets`);
  }
  assert.ok(total > 40, `expected the pages to load a broad asset manifest, got ${total}`);
});

test('each page loads every local asset at most once', () => {
  for (const [page, html] of Object.entries(pageSources)) {
    const scripts = scriptsOf(html).map(assetPath);
    const styles = stylesOf(html).map(assetPath);
    assert.equal(new Set(scripts).size, scripts.length, `${page}: duplicate script`);
    assert.equal(new Set(styles).size, styles.length, `${page}: duplicate stylesheet`);
  }
});

test('bootstrap dependencies load before their consumers', () => {
  const scripts = scriptsOf(pageSources['index.html']).map(assetPath);
  const position = file => {
    const index = scripts.indexOf(file);
    assert.notEqual(index, -1, `index.html must load ${file}`);
    return index;
  };

  assert.equal(scripts[0], 'js/asset-version.js', 'VM_ASSET_URL must exist before any module loads');
  assert.ok(position('js/theme.js') < position('js/bridge.js'));
  assert.ok(position('js/i18n.catalogs.js') < position('js/i18n.js'));
  assert.ok(position('js/i18n.js') < position('js/app.js'));
  assert.ok(position('js/view-lifecycle.js') < position('js/app.js'));
  assert.ok(position('js/animation-level.js') < position('js/perf-guard.js'));
  assert.ok(position('js/bridge.js') < position('js/bootstrap-loader.js'));
});

test('pages remain free of inline scripts and inline event handlers', () => {
  for (const [page, html] of Object.entries(pageSources)) {
    assert.doesNotMatch(html, /<script\b(?![^>]*\bsrc=)[^>]*>/i, `${page}: inline script violates the CSP`);
    assert.doesNotMatch(html, /\s+on[a-z]+\s*=/i, `${page}: inline event handler attribute`);
  }
});

test('every JavaScript module is reachable from a page or a native/dynamic loader', () => {
  const jsDirectory = path.join(wwwroot, 'js');
  const modules = readdirSync(jsDirectory).filter(file => file.endsWith('.js'));
  const reachable = [
    ...Object.values(pageSources),
    ...modules.map(file => readFileSync(path.join(jsDirectory, file), 'utf8')),
    ...readSourceTree(path.join(root, 'src/VoltManager'), '.cs'),
  ].join('\n');

  const unreferenced = modules.filter(module => !reachable.includes(module));
  assert.deepEqual(unreferenced, [], 'orphaned JavaScript modules');
});

test('every dynamic js/... reference points to a shipped module', () => {
  const jsDirectory = path.join(wwwroot, 'js');
  const modules = readdirSync(jsDirectory).filter(file => file.endsWith('.js'));
  const missing = [];
  for (const module of modules) {
    const source = readFileSync(path.join(jsDirectory, module), 'utf8');
    for (const match of source.matchAll(/['"]js\/([A-Za-z0-9_.-]+\.js)['"]/g)) {
      if (!existsSync(path.join(jsDirectory, match[1]))) missing.push(`${module} -> ${match[1]}`);
    }
  }
  assert.deepEqual(missing, [], 'dynamic asset references must resolve');
});

test('the reorganization bootstrap only references shipped lazy modules', () => {
  const source = read('src/VoltManager/wwwroot/js/bootstrap-loader.js');
  const block = source.match(/REORGANIZATION_SCRIPTS\s*=\s*\[([\s\S]*?)\];/)?.[1];
  assert.ok(block, 'REORGANIZATION_SCRIPTS inventory must exist');
  const modules = [...block.matchAll(/'((?:js|css)\/[^']+)'/g)].map(match => match[1]);
  assert.ok(modules.length >= 4);
  const missing = modules.filter(module => !existsSync(path.join(wwwroot, module)));
  assert.deepEqual(missing, []);
});

test('deferred power modules referenced by the shell exist on disk', () => {
  const source = read('src/VoltManager/wwwroot/js/app.js');
  const block = source.match(/deferredPowerScripts\s*=\s*\[([\s\S]*?)\];/)?.[1];
  assert.ok(block, 'deferredPowerScripts inventory must exist');
  const modules = [...block.matchAll(/'([^']+\.js)'/g)].map(match => match[1]);
  assert.ok(modules.length > 0);
  const missing = modules.filter(module => {
    const normalized = module.startsWith('js/') ? module : `js/${module}`;
    return !existsSync(path.join(wwwroot, normalized));
  });
  assert.deepEqual(missing, []);
});

test('the widget page loads only widget-safe modules', () => {
  const modules = scriptsOf(pageSources['widgets.html']).map(assetPath);
  assert.deepEqual(modules, [
    'js/asset-version.js',
    'js/theme.js',
    'js/bridge.js',
    'js/font.js',
    'js/i18n.catalogs.js',
    'js/i18n.js',
    'js/animation-level.js',
    'js/widget-plan-override.js',
    'js/widgets.js',
  ]);
  for (const desktopOnly of [
    'js/app.js', 'js/dashboard.js', 'js/settings.js', 'js/power.js',
    'js/lan-remote-control.js', 'js/ui-reorganization.js',
  ]) {
    assert.equal(modules.includes(desktopOnly), false, `widget page must not load ${desktopOnly}`);
  }
});

test('DOM ids stay unique within each page', () => {
  for (const [page, html] of Object.entries(pageSources)) {
    const ids = [...html.matchAll(/\sid="([^"]+)"/g)].map(match => match[1]);
    const duplicates = [...new Set(ids.filter((id, index) => ids.indexOf(id) !== index))];
    assert.deepEqual(duplicates, [], `${page}: duplicate ids break getElementById lookups`);
  }
});

test('script and stylesheet sources stay relative to the shipped web root', () => {
  for (const [page, html] of Object.entries(pageSources)) {
    for (const reference of scriptsOf(html)) {
      assert.match(reference, /^js\//, `${page}: script must be a local js/ reference: ${reference}`);
    }
    for (const reference of stylesOf(html)) {
      assert.match(reference, /^css\//, `${page}: stylesheet must be a local css/ reference: ${reference}`);
    }
  }
});
