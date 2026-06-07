import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { modalFocusableSelector, nextModalFocusIndex } from './modalAccessibility.ts';

test('modal focus selector includes normal interactive controls but excludes disabled buttons', () => {
  assert.match(modalFocusableSelector, /button:not\(\[disabled\]\)/);
  assert.match(modalFocusableSelector, /a\[href\]/);
  assert.match(modalFocusableSelector, /\[tabindex\]:not\(\[tabindex="-1"\]\)/);
});

test('modal focus index wraps forward and backward inside the dialog', () => {
  assert.equal(nextModalFocusIndex(0, 3, false), 1);
  assert.equal(nextModalFocusIndex(2, 3, false), 0);
  assert.equal(nextModalFocusIndex(2, 3, true), 1);
  assert.equal(nextModalFocusIndex(0, 3, true), 2);
  assert.equal(nextModalFocusIndex(0, 0, false), -1);
});

test('ModalFrame wires escape close focus restore and tab containment', () => {
  const appSource = readFileSync(new URL('./App.tsx', import.meta.url), 'utf8');

  assert.match(appSource, /modalFocusableSelector/);
  assert.match(appSource, /nextModalFocusIndex/);
  assert.match(appSource, /previouslyFocusedRef/);
  assert.match(appSource, /event\.key === 'Escape'/);
  assert.match(appSource, /event\.key !== 'Tab'/);
});
