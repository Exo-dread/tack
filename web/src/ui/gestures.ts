import { h, svg } from './dom';
import { haptics } from './haptics';
import { afterAnimation, animate, EASE_IN, SPRING } from './motion';

export interface SwipeAction {
  label: string;
  icon: string;
  tone: 'accent' | 'danger' | 'neutral';
}

export interface RowGestureOptions {
  /** The scrolling element the rows live in. */
  scroller: HTMLElement;
  rowSelector: string;
  /** Actions revealed by swiping right ("right") or left ("left"), if any. */
  swipeActions(row: HTMLElement): { right?: SwipeAction; left?: SwipeAction } | null;
  onSwipe(row: HTMLElement, direction: 'left' | 'right'): void;
  canDrag(row: HTMLElement): boolean;
  /** `index` is the row's new position among its draggable siblings. */
  onDrop(row: HTMLElement, index: number): void;
  /** Height covered at the bottom of the scroller (the dock), for auto-scroll. */
  bottomInset(): number;
  isBusy(): boolean;
  /** A gesture has ended (the view may have held off re-rendering meanwhile). */
  onIdle(): void;
}

type Phase = 'pending' | 'swipe' | 'drag';

interface Slot {
  el: HTMLElement;
  mid: number;
}

interface State {
  phase: Phase;
  pointerId: number;
  touch: boolean;
  row: HTMLElement;
  body: HTMLElement;
  x0: number;
  y0: number;
  lastX: number;
  lastY: number;
  lastT: number;
  vx: number;
  timer?: ReturnType<typeof setTimeout>;
  // swipe
  actions?: { right?: SwipeAction; left?: SwipeAction };
  under?: HTMLElement;
  armed?: 'left' | 'right' | null;
  dx?: number;
  width?: number;
  // drag
  slots?: Slot[];
  index0?: number;
  index?: number;
  rowMid0?: number;
  shift?: number;
  scroll0?: number;
  raf?: number;
}

const LONG_PRESS_MS = 380;

/**
 * Touch-first row gestures: swipe right or left to reveal and commit an action, long-press
 * (or mouse-drag) to lift a row and reorder it. Vertical scrolling stays native: rows use
 * `touch-action: pan-y`, and only once a gesture has started do we hold the page still.
 */
export class RowGestures {
  private s: State | null = null;
  private suppressClickUntil = 0;

  constructor(
    private readonly root: HTMLElement,
    private readonly o: RowGestureOptions,
  ) {
    root.addEventListener('pointerdown', this.onDown);
    root.addEventListener('click', this.onClickCapture, true);
    root.addEventListener('touchmove', this.onTouchMove, { passive: false });
    root.addEventListener('contextmenu', (e) => {
      if ((e.target as Element).closest(o.rowSelector) && (this.s || matchMedia('(pointer: coarse)').matches)) {
        e.preventDefault();
      }
    });
  }

  /** True while a swipe or drag is in progress (the view holds off re-rendering). */
  get active(): boolean {
    return this.s !== null && this.s.phase !== 'pending';
  }

  /**
   * Clears drag styling from every row. The view calls this after measuring rows for FLIP and
   * before reordering them, so they glide from where they appeared to where they belong.
   */
  release(): void {
    this.root.querySelectorAll<HTMLElement>('.lifted, .shifted').forEach((el) => {
      el.classList.remove('lifted', 'shifted');
      el.style.transform = '';
    });
    this.root.querySelectorAll('.reordering').forEach((el) => el.classList.remove('reordering'));
  }

  // ------------------------------------------------------------------ pointer plumbing

  private onDown = (e: PointerEvent) => {
    if (e.button !== 0 || !e.isPrimary || this.s) return;
    const target = e.target as HTMLElement;
    if (target.closest('textarea, input, .row-tools, .hover-actions')) return;
    const row = target.closest<HTMLElement>(this.o.rowSelector);
    if (!row || !this.root.contains(row) || row.classList.contains('editing') || row.classList.contains('exiting')) return;
    if (this.o.isBusy()) return;
    const body = row.querySelector<HTMLElement>('.row-body');
    if (!body) return;

    const touch = e.pointerType !== 'mouse';
    this.s = {
      phase: 'pending',
      pointerId: e.pointerId,
      touch,
      row,
      body,
      x0: e.clientX,
      y0: e.clientY,
      lastX: e.clientX,
      lastY: e.clientY,
      lastT: e.timeStamp,
      vx: 0,
    };
    if (touch && this.o.canDrag(row)) {
      this.s.timer = setTimeout(() => this.startDrag(), LONG_PRESS_MS);
    }
    window.addEventListener('pointermove', this.onMove);
    window.addEventListener('pointerup', this.onUp);
    window.addEventListener('pointercancel', this.onCancel);
  };

  private onMove = (e: PointerEvent) => {
    const s = this.s;
    if (!s || e.pointerId !== s.pointerId) return;
    const dx = e.clientX - s.x0;
    const dy = e.clientY - s.y0;
    const dt = Math.max(1, e.timeStamp - s.lastT);
    s.vx = 0.7 * ((e.clientX - s.lastX) / dt) + 0.3 * s.vx;
    s.lastX = e.clientX;
    s.lastY = e.clientY;
    s.lastT = e.timeStamp;

    if (s.phase === 'pending') {
      if (Math.hypot(dx, dy) > 8 && s.timer) {
        clearTimeout(s.timer);
        s.timer = undefined;
      }
      if (Math.abs(dx) > 10 && Math.abs(dx) > Math.abs(dy) * 1.4) {
        const actions = this.o.swipeActions(s.row);
        if (actions && (actions.left || actions.right)) this.startSwipe(actions);
        else this.end();
      } else if (!s.touch && Math.abs(dy) > 6 && this.o.canDrag(s.row)) {
        this.startDrag();
      } else if (s.touch && Math.abs(dy) > 10) {
        this.end(); // a scroll: let the browser have it
      }
      return;
    }
    if (s.phase === 'swipe') this.updateSwipe(dx);
    else if (s.phase === 'drag') this.updateDrag();
  };

  private onUp = (e: PointerEvent) => {
    const s = this.s;
    if (!s || e.pointerId !== s.pointerId) return;
    if (s.phase === 'swipe') this.finishSwipe(false);
    else if (s.phase === 'drag') this.finishDrag(false);
    else this.end();
  };

  private onCancel = (e: PointerEvent) => {
    const s = this.s;
    if (!s || e.pointerId !== s.pointerId) return;
    if (s.phase === 'swipe') this.finishSwipe(true);
    else if (s.phase === 'drag') this.finishDrag(true);
    else this.end();
  };

  /** Once a row is moving under the finger, the page must not scroll too. */
  private onTouchMove = (e: TouchEvent) => {
    if (this.s && this.s.phase !== 'pending' && e.cancelable) e.preventDefault();
  };

  /** A swipe or drag ends with a pointerup over the row, which would also "click" it. */
  private onClickCapture = (e: MouseEvent) => {
    if (performance.now() < this.suppressClickUntil) {
      e.stopPropagation();
      e.preventDefault();
    }
  };

  private end(): void {
    const s = this.s;
    if (s?.timer) clearTimeout(s.timer);
    if (s?.raf) cancelAnimationFrame(s.raf);
    this.s = null;
    window.removeEventListener('pointermove', this.onMove);
    window.removeEventListener('pointerup', this.onUp);
    window.removeEventListener('pointercancel', this.onCancel);
    if (s && s.phase !== 'pending') requestAnimationFrame(() => this.o.onIdle());
  }

  // ------------------------------------------------------------------ swipe

  private startSwipe(actions: { right?: SwipeAction; left?: SwipeAction }): void {
    const s = this.s!;
    s.phase = 'swipe';
    s.actions = actions;
    s.width = s.row.offsetWidth;
    s.armed = null;
    const side = (a: SwipeAction | undefined, cls: string) =>
      a ? h(`div.under.${cls}.tone-${a.tone}`, {}, svg(a.icon), h('span', { text: a.label })) : null;
    s.under = h('div.swipe-under', { 'aria-hidden': 'true' }, side(actions.right, 'under-right'), side(actions.left, 'under-left'));
    s.row.prepend(s.under);
    s.row.classList.add('swiping');
  }

  private updateSwipe(rawDx: number): void {
    const s = this.s!;
    const dir = rawDx > 0 ? 'right' : 'left';
    const action = s.actions?.[dir];
    const width = s.width!;
    const limit = width * 0.62;
    let dx = action ? rawDx : rawDx * 0.12;
    if (Math.abs(dx) > limit) dx = Math.sign(dx) * (limit + (Math.abs(dx) - limit) * 0.25);
    s.dx = dx;
    s.body.style.transform = `translateX(${dx}px)`;
    s.under!.dataset.dir = dir;
    const threshold = Math.min(150, Math.max(76, width * 0.3));
    const armed = action && Math.abs(dx) > threshold ? dir : null;
    if (armed !== s.armed) {
      s.armed = armed;
      s.under!.classList.toggle('armed', armed !== null);
      if (armed) haptics.tick();
    }
    s.under!.style.setProperty('--reveal', String(Math.min(1, Math.abs(dx) / threshold)));
  }

  private finishSwipe(cancelled: boolean): void {
    const s = this.s!;
    const dx = s.dx ?? 0;
    const dir = dx > 0 ? 'right' : 'left';
    const flick = Math.abs(s.vx) > 0.65 && Math.sign(s.vx) === Math.sign(dx) && Math.abs(dx) > 36;
    const commit = !cancelled && !!s.actions?.[dir] && (s.armed === dir || flick);
    const { row, body, under } = s;
    this.suppressClickUntil = performance.now() + 400;
    this.end();

    const cleanup = () => {
      body.style.transform = '';
      under?.remove();
      row.classList.remove('swiping');
    };

    if (commit && dir === 'left') {
      haptics.warn();
      const a = animate(body, [{ transform: `translateX(${dx}px)` }, { transform: `translateX(${-row.offsetWidth - 24}px)` }], {
        duration: 170,
        easing: EASE_IN,
        fill: 'forwards',
      });
      const go = () => {
        this.o.onSwipe(row, 'left');
        // If the row survives (e.g. the action was refused), bring it back.
        requestAnimationFrame(() => {
          if (row.isConnected && !row.classList.contains('exiting')) {
            a?.cancel();
            cleanup();
          }
        });
      };
      afterAnimation(a, go, 350);
      return;
    }

    body.style.transform = '';
    const back = animate(body, [{ transform: `translateX(${dx}px)` }, { transform: 'none' }], {
      duration: SPRING.duration,
      easing: SPRING.easing,
    });
    afterAnimation(back, cleanup, SPRING.duration + 150);
    if (commit) this.o.onSwipe(row, dir);
  }

  // ------------------------------------------------------------------ drag to reorder

  private startDrag(): void {
    const s = this.s;
    if (!s || s.phase !== 'pending') return;
    s.timer = undefined;
    const list = s.row.parentElement!;
    const rows = [...list.children].filter(
      (el): el is HTMLElement => el instanceof HTMLElement && el.matches(this.o.rowSelector) && !el.classList.contains('exiting'),
    );
    const scroll = this.o.scroller.scrollTop;
    s.slots = rows.map((el) => {
      const r = el.getBoundingClientRect();
      return { el, mid: r.top + scroll + r.height / 2 };
    });
    s.index0 = s.index = rows.indexOf(s.row);
    const rect = s.row.getBoundingClientRect();
    s.rowMid0 = rect.top + scroll + rect.height / 2;
    s.shift = rect.height;
    s.scroll0 = scroll;
    s.phase = 'drag';
    haptics.tick();
    s.row.classList.add('lifted');
    list.classList.add('reordering');
    this.updateDrag();
    const loop = () => {
      if (!this.s || this.s.phase !== 'drag') return;
      this.autoScroll();
      this.s.raf = requestAnimationFrame(loop);
    };
    s.raf = requestAnimationFrame(loop);
  }

  private updateDrag(): void {
    const s = this.s!;
    const scroller = this.o.scroller;
    const dy = s.lastY - s.y0 + (scroller.scrollTop - s.scroll0!);
    s.row.style.transform = `translateY(${dy}px) scale(1.025)`;
    const mid = s.rowMid0! + dy;
    const slots = s.slots!;
    const i0 = s.index0!;
    let target = i0;
    for (let i = i0 + 1; i < slots.length; i++) if (mid > slots[i].mid) target = i;
    for (let i = i0 - 1; i >= 0; i--) if (mid < slots[i].mid) target = i;
    if (target !== s.index) {
      s.index = target;
      haptics.tick();
    }
    slots.forEach((slot, i) => {
      if (i === i0) return;
      const offset = i0 < i && i <= target ? -s.shift! : target <= i && i < i0 ? s.shift! : 0;
      slot.el.classList.toggle('shifted', offset !== 0);
      slot.el.style.transform = offset ? `translateY(${offset}px)` : '';
    });
  }

  private autoScroll(): void {
    const s = this.s!;
    const scroller = this.o.scroller;
    const rect = scroller.getBoundingClientRect();
    const edge = 72;
    const top = rect.top + edge;
    const bottom = rect.bottom - this.o.bottomInset() - edge;
    let speed = 0;
    if (s.lastY < top) speed = -Math.min(18, (top - s.lastY) * 0.25);
    else if (s.lastY > bottom) speed = Math.min(18, (s.lastY - bottom) * 0.25);
    if (speed) {
      const before = scroller.scrollTop;
      scroller.scrollTop += speed;
      if (scroller.scrollTop !== before) this.updateDrag();
    }
  }

  private finishDrag(cancelled: boolean): void {
    const s = this.s!;
    const { row } = s;
    const from = s.index0!;
    const to = cancelled ? from : s.index!;
    this.suppressClickUntil = performance.now() + 400;
    this.end();

    if (to !== from) {
      haptics.tick();
      this.o.onDrop(row, to);
    }
    // Whatever wasn't re-rendered glides home.
    if (row.classList.contains('lifted')) {
      const current = row.style.transform;
      this.release();
      animate(row, [{ transform: current }, { transform: 'none' }], { duration: SPRING.duration, easing: SPRING.easing });
    }
  }
}
