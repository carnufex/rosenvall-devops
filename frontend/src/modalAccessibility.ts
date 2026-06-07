export const modalFocusableSelector = [
  'a[href]',
  'area[href]',
  'button:not([disabled])',
  'input:not([disabled])',
  'select:not([disabled])',
  'textarea:not([disabled])',
  'iframe',
  'object',
  'embed',
  '[contenteditable="true"]',
  '[tabindex]:not([tabindex="-1"])'
].join(',');

export function nextModalFocusIndex(currentIndex: number, focusableCount: number, shiftKey: boolean) {
  if (focusableCount <= 0) return -1;
  if (shiftKey) return currentIndex <= 0 ? focusableCount - 1 : currentIndex - 1;
  return currentIndex >= focusableCount - 1 ? 0 : currentIndex + 1;
}
