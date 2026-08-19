import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const widgetWindow = readFileSync(
  new URL('../src/VoltManager/WidgetWindow.xaml.cs', import.meta.url),
  'utf8'
);

test('widget native placement is verified and has a corrective fallback', () => {
  assert.match(
    widgetWindow,
    /SetWindowPos\(\s*hwnd,[\s\S]*?if\s*\(![\s\S]*?GetWindowRect\(hwnd,[\s\S]*?MoveWindow\(hwnd,/,
    'ApplyPlacement must verify the native bounds and retry with MoveWindow when SetWindowPos does not apply them'
  );

  assert.match(
    widgetWindow,
    /DllImport\("user32\.dll",\s*SetLastError\s*=\s*true\)[\s\S]*?SetWindowPos/,
    'SetWindowPos failures must preserve the Win32 error code for diagnostics'
  );

  assert.match(
    widgetWindow,
    /DllImport\("user32\.dll",\s*SetLastError\s*=\s*true\)[\s\S]*?MoveWindow/,
    'MoveWindow failures must preserve the Win32 error code for diagnostics'
  );
});
