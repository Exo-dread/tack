import { h } from './dom';
import { afterAnimation, animate, EASE_IN, SPRING } from './motion';

/**
 * One toast at a time, above the dock: what just happened, and Undo. A new toast replaces the
 * old one (whose undo then lapses), exactly like the desktop app. Everything a toast says is
 * also announced to screen readers.
 */
export class Toasts {
  readonly el: HTMLElement;
  private readonly live: HTMLElement;
  private current: { node: HTMLElement; timer: ReturnType<typeof setTimeout>; undo?: () => void } | null = null;

  constructor() {
    this.el = h('div.toasts');
    this.live = h('div.sr-only', { 'aria-live': 'polite', 'aria-atomic': 'true' });
    document.body.append(this.live);
  }

  /** Says something to screen readers only. */
  announce(message: string): void {
    this.live.textContent = '';
    // A fresh node each time so repeated messages are still read.
    requestAnimationFrame(() => (this.live.textContent = message));
  }

  show(message: string, undo?: () => void, options: { duration?: number; action?: string } = {}): void {
    this.dismiss(false);
    const node = h(
      'div.toast',
      {},
      h('span.toast-text', { text: message }),
      undo
        ? h('button.toast-action', {
            type: 'button',
            text: options.action ?? 'Undo',
            onclick: () => {
              const u = this.current?.undo;
              this.dismiss();
              u?.();
              if (!options.action) this.announce('Undone');
            },
          })
        : null,
    );
    // Swipe down to dismiss.
    let startY = 0;
    node.addEventListener('pointerdown', (e) => (startY = e.clientY));
    node.addEventListener('pointerup', (e) => {
      if (e.clientY - startY > 24) this.dismiss();
    });
    this.el.append(node);
    animate(node, [{ opacity: 0, transform: 'translateY(16px) scale(.96)' }, { opacity: 1, transform: 'none' }], {
      duration: SPRING.duration,
      easing: SPRING.easing,
    });
    // The countdown pauses while you're touching, hovering or tabbed onto the toast, so it never
    // vanishes under your finger (and anyone who needs longer can take it).
    let remaining = options.duration ?? (undo ? 5000 : 2600);
    let startedAt = performance.now();
    let paused = false;
    const pause = () => {
      if (paused || this.current?.node !== node) return;
      paused = true;
      clearTimeout(this.current.timer);
      remaining = Math.max(2000, remaining - (performance.now() - startedAt));
    };
    const resume = () => {
      if (!paused || this.current?.node !== node || node.contains(document.activeElement)) return;
      paused = false;
      startedAt = performance.now();
      this.current.timer = setTimeout(() => this.dismiss(), remaining);
    };
    node.addEventListener('pointerenter', pause);
    node.addEventListener('focusin', pause);
    node.addEventListener('pointerleave', resume);
    node.addEventListener('focusout', () => requestAnimationFrame(resume));
    this.current = { node, undo, timer: setTimeout(() => this.dismiss(), remaining) };
    this.announce(undo ? `${message}. Undo available.` : message);
  }

  /** Ctrl/Cmd+Z: runs the current toast's undo, if there is one. */
  undo(): boolean {
    const u = this.current?.undo;
    if (!u) return false;
    this.dismiss();
    u();
    this.announce('Undone');
    return true;
  }

  dismiss(animated = true): void {
    const c = this.current;
    if (!c) return;
    this.current = null;
    clearTimeout(c.timer);
    const a = animated
      ? animate(c.node, [{ opacity: 1, transform: 'none' }, { opacity: 0, transform: 'translateY(10px) scale(.97)' }], {
          duration: 160,
          easing: EASE_IN,
          fill: 'forwards',
        })
      : null;
    afterAnimation(a, () => c.node.remove(), 300);
  }
}
