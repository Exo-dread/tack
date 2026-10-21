import { h } from './dom';
import { afterAnimation, animate, EASE_IN, EASE_OUT, SPRING } from './motion';

export interface SheetHandle {
  readonly dialog: HTMLDialogElement;
  /** Closes the sheet: sliding away, or at once (when focus must go straight back to a field). */
  close(instant?: boolean): void;
}

let sheets = 0;

/**
 * A small sheet that slides up from the bottom, within thumb reach (centred on wide screens),
 * as Settings does: a modal dialog with a title and Done, closed by Escape, a tap outside or a
 * swipe down. `onEscape` may keep it open (return false), e.g. to cancel a name being typed.
 * `onClosed` runs once it has gone, when the page can take the focus back.
 */
export function presentSheet(
  title: string,
  body: Node[],
  options: { onClose?: () => void; onEscape?: () => boolean; onClosed?: () => void } = {},
): SheetHandle {
  const titleId = `sheet-title-${++sheets}`;
  const head = h('div.sheet-head', {}, h('h2', { id: titleId, text: title }), h('button.text-button', { type: 'button', text: 'Done', onclick: () => handle.close() }));
  const panel = h('div.sheet-panel', {}, h('div.sheet-grip', { 'aria-hidden': 'true' }), head, h('div.sheet-scroll', {}, ...body));
  const dialog = h('dialog.sheet.picker', { 'aria-labelledby': titleId }, panel);
  let open = true;
  const handle: SheetHandle = {
    dialog,
    close(instant = false) {
      if (!open) return;
      open = false;
      options.onClose?.();
      const gone = () => {
        dialog.close();
        dialog.remove();
        options.onClosed?.();
      };
      if (instant) {
        gone();
        return;
      }
      dialog.classList.add('closing');
      const from = getComputedStyle(panel).transform;
      const a = animate(panel, [{ transform: from === 'none' ? 'none' : from }, { transform: 'translateY(100%)' }], { duration: 220, easing: EASE_IN, fill: 'forwards' });
      afterAnimation(a, gone, 400);
    },
  };
  document.body.append(dialog);
  dialog.showModal();
  dialog.addEventListener('cancel', (e) => {
    e.preventDefault();
    if (options.onEscape && options.onEscape() === false) return;
    handle.close();
  });
  dialog.addEventListener('click', (e) => {
    if (e.target === dialog) handle.close();
  });
  dragToDismiss(panel, () => handle.close());
  animate(panel, [{ transform: 'translateY(100%)' }, { transform: 'none' }], { duration: SPRING.duration, easing: SPRING.easing });
  return handle;
}

/** Swiping the grip or the title down closes the sheet; a short swipe springs back. */
function dragToDismiss(panel: HTMLElement, close: () => void): void {
  let start: { y: number; t: number; id: number } | null = null;
  let dy = 0;
  const begin = (e: PointerEvent) => {
    if ((e.target as HTMLElement).closest('button')) return;
    start = { y: e.clientY, t: e.timeStamp, id: e.pointerId };
    dy = 0;
    (e.currentTarget as HTMLElement).setPointerCapture(e.pointerId);
  };
  const move = (e: PointerEvent) => {
    if (!start || e.pointerId !== start.id) return;
    dy = Math.max(0, e.clientY - start.y);
    panel.style.transform = `translateY(${dy}px)`;
  };
  const end = (e: PointerEvent) => {
    if (!start || e.pointerId !== start.id) return;
    const v = dy / Math.max(1, e.timeStamp - start.t);
    start = null;
    if (dy > 110 || (v > 0.6 && dy > 30)) {
      close();
    } else {
      const from = panel.style.transform;
      panel.style.transform = '';
      animate(panel, [{ transform: from || 'none' }, { transform: 'none' }], { duration: SPRING.duration, easing: SPRING.easing });
    }
  };
  for (const el of [panel.querySelector<HTMLElement>('.sheet-grip')!, panel.querySelector<HTMLElement>('.sheet-head')!]) {
    el.addEventListener('pointerdown', begin);
    el.addEventListener('pointermove', move);
    el.addEventListener('pointerup', end);
    el.addEventListener('pointercancel', end);
  }
}

export interface MenuItem {
  label: string;
  icon?: SVGSVGElement;
  disabled?: boolean;
  /** Destructive (Delete): its icon in the danger colour, as Delete is everywhere else. */
  danger?: boolean;
  run(): void;
}

/**
 * A small menu dropping from a button (the ⋯ on notes): arrow keys move through it, Escape or a
 * tap anywhere else closes it, and focus goes back to the button.
 */
export function openMenu(anchor: HTMLElement, items: MenuItem[]): void {
  // A tap on the button while its menu is open closes it (on pointerdown): don't open it again.
  if (anchor.dataset.menuClosed) {
    delete anchor.dataset.menuClosed;
    return;
  }
  // Focusable itself, for a menu with nothing to choose right now: Escape still closes it.
  const menu = h('div.menu', { role: 'menu', tabindex: '-1', 'aria-label': anchor.getAttribute('aria-label') ?? 'Menu' });
  const buttons = items.map((item) => {
    const b = h('button.menu-item', { class: item.danger ? 'danger' : null, type: 'button', role: 'menuitem', tabindex: '-1', disabled: item.disabled ?? false }, item.icon ?? null, h('span', { text: item.label }));
    b.addEventListener('click', () => {
      close(false);
      item.run();
    });
    return b;
  });
  menu.append(...buttons);
  const r = anchor.getBoundingClientRect();
  document.body.append(menu);
  const width = menu.offsetWidth;
  const left = Math.max(8, Math.min(r.right - width, window.innerWidth - width - 8));
  const below = r.bottom + 6 + menu.offsetHeight < window.innerHeight;
  menu.style.left = `${left}px`;
  menu.style.top = `${below ? r.bottom + 6 : r.top - 6 - menu.offsetHeight}px`;
  anchor.setAttribute('aria-expanded', 'true');
  animate(menu, [{ opacity: 0, transform: `translateY(${below ? -6 : 6}px) scale(.97)` }, { opacity: 1, transform: 'none' }], { duration: 180, easing: EASE_OUT });

  const enabled = () => buttons.filter((b) => !b.disabled);
  const close = (refocus: boolean) => {
    if (!menu.isConnected) return;
    menu.remove();
    anchor.setAttribute('aria-expanded', 'false');
    document.removeEventListener('pointerdown', outside, true);
    if (refocus) anchor.focus();
  };
  const outside = (e: PointerEvent) => {
    if (menu.contains(e.target as Node)) return;
    if (anchor.contains(e.target as Node)) {
      anchor.dataset.menuClosed = 'true';
      setTimeout(() => delete anchor.dataset.menuClosed, 600);
    }
    close(false);
  };
  document.addEventListener('pointerdown', outside, true);
  menu.addEventListener('keydown', (e) => {
    const list = enabled();
    const i = list.indexOf(document.activeElement as HTMLButtonElement);
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      list[(i + (e.key === 'ArrowDown' ? 1 : -1) + list.length) % list.length]?.focus();
    } else if (e.key === 'Home' || e.key === 'End') {
      e.preventDefault();
      (e.key === 'Home' ? list[0] : list.at(-1))?.focus();
    } else if (e.key === 'Escape') {
      e.preventDefault();
      e.stopPropagation();
      close(true);
    } else if (e.key === 'Tab') {
      close(false);
    }
  });
  menu.addEventListener('focusout', (e) => {
    if (!menu.contains(e.relatedTarget as Node | null) && e.relatedTarget) close(false);
  });
  (enabled()[0] ?? menu).focus();
}
