/**
 * Tack's motion vocabulary. Springs settle quickly (well under half a second), nothing loops,
 * and with "reduce motion" everything becomes a short fade or an instant change.
 */

const reduceQuery = matchMedia('(prefers-reduced-motion: reduce)');
export const reducedMotion = (): boolean => reduceQuery.matches;

/** Samples a damped spring into a CSS linear() easing (supported by every current engine). */
function springEasing(stiffness: number, damping: number, mass = 1, samples = 40): { easing: string; duration: number } {
  const w0 = Math.sqrt(stiffness / mass);
  const zeta = damping / (2 * Math.sqrt(stiffness * mass));
  const wd = w0 * Math.sqrt(Math.max(0, 1 - zeta * zeta));
  const at = (t: number) =>
    zeta < 1
      ? 1 - Math.exp(-zeta * w0 * t) * (Math.cos(wd * t) + ((zeta * w0) / wd) * Math.sin(wd * t))
      : 1 - Math.exp(-w0 * t) * (1 + w0 * t);
  // Settle when within 0.1% and slow.
  let end = 0.1;
  while (end < 3 && (Math.abs(1 - at(end)) > 0.001 || Math.abs(at(end) - at(end - 0.016)) > 0.0005)) end += 0.016;
  const points: string[] = [];
  for (let i = 0; i <= samples; i++) points.push(at((end * i) / samples).toFixed(4));
  points[points.length - 1] = '1';
  return { easing: `linear(${points.join(', ')})`, duration: Math.round(end * 1000) };
}

const supportsLinear = typeof CSS !== 'undefined' && CSS.supports?.('transition-timing-function', 'linear(0, 1)');

/** Gentle: lists settling, cards moving. A hint of overshoot. */
export const SPRING = supportsLinear ? springEasing(260, 26) : { easing: 'cubic-bezier(.2,.9,.25,1.05)', duration: 420 };
/** Bouncy: small things popping in (ticks, counts, the orb). */
export const POP = supportsLinear ? springEasing(420, 20) : { easing: 'cubic-bezier(.3,1.4,.5,1)', duration: 380 };
export const EASE_OUT = 'cubic-bezier(.2,.8,.2,1)';
export const EASE_IN = 'cubic-bezier(.4,0,1,1)';

// Expose the springs to CSS, so transitions use the same curves.
document.documentElement.style.setProperty('--spring', SPRING.easing);
document.documentElement.style.setProperty('--spring-ms', `${SPRING.duration}ms`);
document.documentElement.style.setProperty('--pop', POP.easing);
document.documentElement.style.setProperty('--pop-ms', `${POP.duration}ms`);

export function animate(el: Element, keyframes: Keyframe[], options: KeyframeAnimationOptions): Animation | null {
  if (typeof el.animate !== 'function') return null;
  if (reducedMotion()) {
    // Keep fades (they carry meaning), drop movement.
    const fades = keyframes.map((k) => ('opacity' in k ? { opacity: k.opacity } : {}));
    if (!fades.some((k) => 'opacity' in k)) return null;
    return el.animate(fades, { ...options, duration: Math.min(Number(options.duration) || 150, 150), easing: 'linear' });
  }
  return el.animate(keyframes, options);
}

const flips = new WeakMap<Element, Animation>();

/**
 * FLIP: measure keyed elements, change the DOM, then animate every element from where it was
 * to where it is. Elements share keys across containers (a task row and the Now card are the
 * same "t:id"), so a task glides from the list into focus, or down into Done.
 */
export function measure(root: ParentNode): Map<string, DOMRect> {
  const rects = new Map<string, DOMRect>();
  root.querySelectorAll<HTMLElement>('[data-flip]').forEach((el) => {
    if (el.classList.contains('exiting')) return;
    const r = el.getBoundingClientRect();
    // Hidden elements report an empty box at 0,0; treat them as new when they appear.
    if (r.width || r.height) rects.set(el.dataset.flip!, r);
  });
  return rects;
}

export function playFlip(root: ParentNode, before: Map<string, DOMRect>, options: { enter?: boolean } = {}): void {
  const els = [...root.querySelectorAll<HTMLElement>('[data-flip]')].filter((el) => !el.classList.contains('exiting'));
  els.forEach((el) => flips.get(el)?.cancel());
  const viewH = window.innerHeight;
  for (const el of els) {
    const after = el.getBoundingClientRect();
    if (after.bottom < -50 || after.top > viewH + 50) continue;
    const prev = before.get(el.dataset.flip!);
    if (prev) {
      const dx = prev.left - after.left;
      const dy = prev.top - after.top;
      if (Math.abs(dx) < 0.5 && Math.abs(dy) < 0.5) continue;
      if (reducedMotion()) continue;
      const a = el.animate([{ transform: `translate(${dx}px, ${dy}px)` }, { transform: 'none' }], {
        duration: SPRING.duration,
        easing: SPRING.easing,
      });
      flips.set(el, a);
    } else if (options.enter !== false && before.size > 0) {
      const a = animate(el, [{ opacity: 0, transform: 'translateY(10px) scale(.98)' }, { opacity: 1, transform: 'none' }], {
        duration: SPRING.duration,
        easing: SPRING.easing,
      });
      if (a) flips.set(el, a);
    }
  }
}

/**
 * Takes an element out of the flow where it stands and fades it away, so its neighbours can
 * close the gap with FLIP. `offsetTop` must be read before the DOM changed.
 */
export function exitInPlace(el: HTMLElement, offsetTop: number, how: 'fade' | 'away' = 'fade'): void {
  el.classList.add('exiting');
  el.removeAttribute('data-flip');
  el.style.position = 'absolute';
  el.style.top = `${offsetTop}px`;
  el.style.left = '0';
  el.style.right = '0';
  el.inert = true;
  const frames =
    how === 'away'
      ? [{ opacity: 1 }, { opacity: 0 }]
      : [{ opacity: 1, transform: 'scale(1)' }, { opacity: 0, transform: 'scale(.97)' }];
  const a = animate(el, frames, { duration: 200, easing: EASE_OUT, fill: 'forwards' });
  if (a) a.finished.then(() => el.remove(), () => el.remove());
  else el.remove();
}
