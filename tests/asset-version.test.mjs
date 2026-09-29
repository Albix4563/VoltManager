import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const wwwroot = path.join(root, 'src/VoltManager/wwwroot');
const read = relative => fs.readFileSync(path.join(root, relative), 'utf8');

test('all local script and stylesheet references use the centralized asset version', () => {
  const versionSource = read('src/VoltManager/wwwroot/js/asset-version.js');
  const match = versionSource.match(/VM_ASSET_VERSION\s*=\s*['"]([^'"]+)['"]/);
  assert.ok(match, 'VM_ASSET_VERSION declaration missing');
  const version = match[1];
  const htmlSources = ['index.html', 'widgets.html']
    .map(file => fs.readFileSync(path.join(wwwroot, file), 'utf8'))
    .join('\n');
  const refs = [...htmlSources.matchAll(/<(?:script|link)\b[^>]*(?:src|href)="((?:js|css)\/[^"?#]+(?:\?[^"#]*)?)"/g)]
    .map(item => item[1]);

  assert.ok(refs.length > 0, 'no local scripts/styles found');
  for (const ref of refs) {
    const query = ref.split('?')[1] || '';
    const params = new URLSearchParams(query);
    assert.equal(params.get('v'), version, `${ref}: asset version mismatch or missing`);
  }

  const loaderFiles = ['bootstrap-loader.js', 'app.js', 'tips.js', 'tour.js'];
  for (const file of loaderFiles) {
    const source = fs.readFileSync(path.join(wwwroot, 'js', file), 'utf8');
    assert.match(source, /VM_ASSET_URL\(/, `${file}: dynamic assets must use VM_ASSET_URL`);
    assert.doesNotMatch(source, /(?:js|css)\/[^'"`\s]+\?v=[^'"`\s]+/, `${file}: hard-coded asset version`);
  }
});
