import { coarsePointer, isEditable } from './dom';

/**
 * Keeps the app inside the part of the screen you can actually see.
 *
 * Android Chrome shrinks the layout when the keyboard opens (the viewport meta asks it to).
 * iOS Safari doesn't: the keyboard covers the page and the visual viewport scrolls instead.
 * So the app root is sized and positioned from the visual viewport, which makes both behave
 * the same: the dock and the text you're typing sit just above the keyboard.
 */
export function trackViewport(onChange?: () => void): void {
  const root = document.documentElement;
  const vv = window.visualViewport;

  const update = () => {
    const height = vv ? vv.height : window.innerHeight;
    const top = vv ? Math.max(0, vv.offsetTop) : 0;
    root.style.setProperty('--vv-h', `${Math.round(height)}px`);
    root.style.setProperty('--vv-top', `${Math.round(top)}px`);
    // A touch device with a text field focused has its keyboard up (Android reports no
    // covered area because it resizes the page instead, so focus is the reliable signal).
    const typing = isEditable(document.activeElement);
    root.classList.toggle('typing', typing);
    root.classList.toggle('kb-open', typing && coarsePointer());
    onChange?.();
  };

  vv?.addEventListener('resize', update);
  vv?.addEventListener('scroll', update);
  window.addEventListener('resize', update);
  document.addEventListener('focusin', update);
  document.addEventListener('focusout', () => {
    // Focus moves between fields in one go; wait a frame before deciding the keyboard closed.
    requestAnimationFrame(() => {
      update();
      // iOS sometimes leaves the page scrolled after the keyboard closes.
      if (!isEditable(document.activeElement) && window.scrollY !== 0) window.scrollTo(0, 0);
    });
  });
  update();
}
