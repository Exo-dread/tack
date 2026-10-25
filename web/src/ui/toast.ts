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
  private current: {
    node: HTMLElement;
    timer?: ReturnType<typeof setTimeout>;
    undo?: () => void;
    /** A question's (see `ask`): what happens if it goes unanswered. */
    dropped?: () => void;
    token?: object;
  } | null = null;

  constructor() {
    this.el = h('div.toasts');
    this.live = h('div.sr-only', { 'aria-live': 'polite', 'aria-atomic': 'true' });
    document.body.append(this.live);
  }

  /**
   * Shows toasts inside `host` (an open dialog, which would otherwise cover them) until it's
   * called with null, which puts them back where they were.
   */
  mount(host: HTMLElement | null): void {
    if (host) {
      if (this.el.parentElement === host) return;
      this.home ??= this.el.parentElement;
      host.append(this.el);
    } else if (this.home) {
      this.home.append(this.el);
      this.home = null;
    }
  }

  private home: HTMLElement | null = null;

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

  /**
   * A question in place of a toast: a small `label` over `message` (two lines of it at most), the
   * action and Dismiss. It stays until it's answered: the action runs `act`; Dismiss, a swipe down,
   * or a newer toast taking its place runs `dropped`. Returns a token for `asking`.
   */
  ask(label: string, message: string, action: string, act: () => void, dropped: () => void): object {
    this.dismiss(false);
    const token = {};
    let answered = false;
    const answer = (run: () => void) => {
      if (answered) return;
      answered = true;
      if (this.current?.node === node) this.dismiss();
      run();
    };
    const node = h(
      'div.toast.ask',
      {},
      h('span.toast-ask', {}, h('span.toast-label', { text: label }), h('span.toast-text', { text: message })),
      h('button.toast-action', { type: 'button', text: action, onclick: () => answer(act) }),
      h('button.toast-action.quiet', { type: 'button', text: 'Dismiss', onclick: () => answer(dropped) }),
    );
    let startY = 0;
    node.addEventListener('pointerdown', (e) => (startY = e.clientY));
    node.addEventListener('pointerup', (e) => {
      if (e.clientY - startY > 24) answer(dropped);
    });
    this.el.append(node);
    animate(node, [{ opacity: 0, transform: 'translateY(16px) scale(.96)' }, { opacity: 1, transform: 'none' }], {
      duration: SPRING.duration,
      easing: SPRING.easing,
    });
    // No countdown: it waits for an answer. A newer toast ends it (see dismiss).
    this.current = { node, dropped: () => answer(dropped), token };
    this.announce(`${label}: ${message}. ${action} or Dismiss.`);
    return token;
  }

  /** Is the question `ask` returned this token for still on show? */
  asking(token: object): boolean {
    return this.current?.token === token;
  }

  /** Is this undo still on offer (its toast on show)? */
  offers(undo: () => void): boolean {
    return this.current?.undo === undo;
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
    // A question nobody answered (a newer toast took its place): it's dropped.
    c.dropped?.();
    // While it fades, its Undo would run the next toast's undo: take it out of reach at once.
    c.node.inert = true;
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
