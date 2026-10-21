import { svg } from './dom';
import { animate, POP } from './motion';

let instances = 0;

/**
 * The orb from Tack for Windows: a glass bead whose ring fills as today's tasks get done,
 * with the number of open tasks at its centre, a tick when everything is done, and a quiet
 * dot (the tack) when there's nothing at all.
 */
export class Orb {
  readonly el: SVGSVGElement;
  private readonly arc: SVGCircleElement;
  private readonly count: SVGTextElement;
  private shown = { open: -1, progress: -1, allDone: false };

  constructor(className = '') {
    const id = `orb${++instances}`;
    this.el = svg(`
      <svg class="orb ${className}" viewBox="0 0 48 48" role="img">
        <defs>
          <radialGradient id="${id}-body" cx="50%" cy="30%" r="80%" fx="50%" fy="14%">
            <stop class="orb-stop-top" offset="0"/>
            <stop class="orb-stop-bottom" offset="1"/>
          </radialGradient>
          <linearGradient id="${id}-sheen" x1="0" y1="0" x2="0" y2="0.6">
            <stop class="orb-sheen-top" offset="0"/>
            <stop class="orb-sheen-bottom" offset="1"/>
          </linearGradient>
        </defs>
        <circle class="orb-halo" cx="24" cy="24" r="22"/>
        <circle class="orb-body" cx="24" cy="24" r="22" fill="url(#${id}-body)"/>
        <circle class="orb-sheen" cx="24" cy="24" r="21.4" fill="url(#${id}-sheen)"/>
        <circle class="orb-rim" cx="24" cy="24" r="21.5"/>
        <circle class="orb-track" cx="24" cy="24" r="15.5"/>
        <circle class="orb-arc" cx="24" cy="24" r="15.5" pathLength="100" transform="rotate(-90 24 24)"/>
        <circle class="orb-dot" cx="24" cy="24" r="3.4"/>
        <path class="orb-tick" d="M18.6 24.4l3.6 3.6 7.4-7.6" pathLength="1"/>
        <text class="orb-count" x="24" y="24" text-anchor="middle" dominant-baseline="central"></text>
      </svg>`);
    this.arc = this.el.querySelector('.orb-arc')!;
    this.count = this.el.querySelector('.orb-count')!;
  }

  /** `scope` names what's counted (the space on show), once there are spaces. */
  update(open: number, doneToday: number, progress: number, scope = ''): void {
    const allDone = open === 0 && doneToday > 0;
    const p = allDone ? 1 : Math.max(0, Math.min(1, progress));
    this.el.classList.toggle('is-empty', open === 0 && doneToday === 0);
    this.el.classList.toggle('is-done', allDone);
    this.el.classList.toggle('has-progress', p > 0.001);
    this.el.classList.toggle('has-open', open > 0);
    this.arc.style.strokeDashoffset = String(100 - p * 100);

    if (open !== this.shown.open) {
      this.count.textContent = open > 99 ? '99+' : open > 0 ? String(open) : '';
      this.count.classList.toggle('small', open > 99);
      if (this.shown.open >= 0 && open > 0) {
        animate(this.count, [{ transform: 'scale(.55)', opacity: 0.2 }, { transform: 'scale(1)', opacity: 1 }], {
          duration: POP.duration,
          easing: POP.easing,
        });
      }
    }
    const label =
      open === 0 && doneToday === 0
        ? 'No tasks'
        : `${open} open${doneToday ? `, ${doneToday} done today` : ''}`;
    this.el.setAttribute('aria-label', scope ? `${scope}: ${label}` : label);
    this.shown = { open, progress: p, allDone };
  }

  /** A single soft bloom of light: everything for today is done. */
  celebrate(): void {
    this.el.classList.remove('bloom');
    // Restart the CSS animation.
    void this.el.getBoundingClientRect();
    this.el.classList.add('bloom');
    setTimeout(() => this.el.classList.remove('bloom'), 1400);
  }
}
