import { h } from './dom';

/**
 * What shows while Tack is at Microsoft renewing the OneDrive sign-in: a quick trip, once a day
 * where the browser won't let a hidden frame do it, made before the app starts. Microsoft's
 * redirects leave this page up until Tack's own comes back, so without this it's a blank screen
 * for as long as the trip takes. Slow or stuck there, you can open Tack anyway, and after a while
 * it opens by itself; the renewal is tried again later.
 */

/** Quicker than this, nothing shows (a message that flashes by is worse than none). */
const SHOW_AFTER = 400;
/** Then: taking longer than usual, and a way out. */
const OFFER_AFTER = 8000;
/** Then: Tack opens by itself. */
const GIVE_UP_AFTER = 25_000;

/** Tack is being opened afresh (Open Tack now, or the wait ran out): this page is on its way out. */
let reloading = false;

const open = () => {
  reloading = true;
  location.reload();
};

/**
 * Has this page set off to open Tack afresh? Then it goes no further: some browsers let it run a
 * moment longer (and the sign-in it was waiting for may fail meanwhile, as the reload cuts it off).
 */
export const isReloading = () => reloading;

/**
 * Says `text` after a moment, and `slow` with a way to open Tack after a while. The live region
 * goes in empty at once and the words follow, so screen readers announce them. Returns what takes
 * it away.
 */
function notice(text: string, slow: string): () => void {
  const region = h('div.renewing', { role: 'status' });
  const p = h('p.boot-message');
  document.body.append(region);
  const timers = [
    setTimeout(() => {
      p.textContent = text;
      region.append(p);
    }, SHOW_AFTER),
    setTimeout(() => {
      p.textContent = `${slow} `;
      p.append(h('button.button.primary', { type: 'button', text: 'Open Tack now', onclick: open }));
      region.append(p);
    }, OFFER_AFTER),
  ];
  return () => {
    // On the way out, the words stay up until the new page takes over.
    if (reloading) return;
    timers.forEach(clearTimeout);
    region.remove();
  };
}

/**
 * On the way to Microsoft, before the app starts: says so after a moment. Opening Tack instead
 * (the button, after a while by itself, or Back from Microsoft's page) boots without the visit,
 * which isn't due again for an hour. Returns what calls it off (Tack stayed after all).
 */
export function showLeaving(): () => void {
  const hide = notice('Renewing your OneDrive sign-in…', 'Renewing your OneDrive sign-in is taking longer than usual.');
  const giveUp = setTimeout(open, GIVE_UP_AFTER);
  const back = (e: PageTransitionEvent) => e.persisted && open();
  addEventListener('pageshow', back);
  return () => {
    hide();
    clearTimeout(giveUp);
    removeEventListener('pageshow', back);
  };
}

/**
 * Back from Microsoft while Tack finishes the sign-in: the same. It opens by itself once that
 * request runs out of time (msauth.ts), with the answer handled as it should be; meanwhile, after
 * a while, you can open Tack anyway (the sign-in is then tried again another time). Returns what
 * takes the message away.
 */
export function showReturning(): () => void {
  return notice('Finishing your OneDrive sign-in…', 'Finishing your OneDrive sign-in is taking longer than usual.');
}
