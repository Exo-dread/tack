import { isIOS } from './dom';

/**
 * Light touch feedback. Android uses the Vibration API. iOS Safari has no vibration API, but
 * toggling a native switch control gives the system "selection" tick (iOS 18+), so on iOS we
 * click a hidden one. Both only work inside a user gesture, which is when we call them.
 */

let enabled = true;

export function setHaptics(on: boolean): void {
  enabled = on;
}

/** Only phones and tablets can buzz (desktop Chrome has navigator.vibrate, but it does nothing). */
export function hapticsSupported(): boolean {
  return matchMedia('(pointer: coarse)').matches && (typeof navigator.vibrate === 'function' || isIOS());
}

function iosTick(): void {
  const label = document.createElement('label');
  label.ariaHidden = 'true';
  label.style.display = 'none';
  const input = document.createElement('input');
  input.type = 'checkbox';
  input.setAttribute('switch', '');
  input.tabIndex = -1;
  label.append(input);
  document.head.append(label);
  label.click();
  label.remove();
}

function buzz(pattern: number | number[], iosTicks: number): void {
  if (!enabled) return;
  try {
    if (typeof navigator.vibrate === 'function' && !isIOS()) {
      navigator.vibrate(pattern);
    } else if (isIOS()) {
      iosTick();
      if (iosTicks > 1) setTimeout(iosTick, 90);
    }
  } catch {
    /* feedback is optional */
  }
}

export const haptics = {
  /** A detent: crossing a swipe threshold, lifting a row. */
  tick: () => buzz(8, 1),
  /** Something finished. */
  success: () => buzz([10, 50, 16], 2),
  /** Something removed. */
  warn: () => buzz([14, 40, 14], 1),
};
