import { onPrefs, prefs } from '../store/prefs';

/**
 * Light and dark, and the accent. Like the desktop app, each accent has two shades: a bright
 * one for dark glass and a deeper one for light glass, so ticks and text keep their contrast.
 * The colours themselves live in CSS (keyed by data-theme and data-accent on <html>), so the
 * boot script can set them before the first paint.
 */
export const ACCENTS: readonly { id: string; name: string }[] = [
  { id: 'teal', name: 'Teal' },
  { id: 'sky', name: 'Sky' },
  { id: 'mint', name: 'Mint' },
  { id: 'amber', name: 'Amber' },
  { id: 'coral', name: 'Coral' },
  { id: 'lilac', name: 'Lilac' },
];

/** Page colours, also used for the browser chrome (theme-color). Keep in step with styles. */
const SURFACE = { dark: '#0d0f14', light: '#eef0f4' };

const systemDark = matchMedia('(prefers-color-scheme: dark)');

export function resolvedTheme(): 'light' | 'dark' {
  const t = prefs().theme;
  if (t === 'light' || t === 'dark') return t;
  return systemDark.matches ? 'dark' : 'light';
}

export function applyTheme(): void {
  const theme = resolvedTheme();
  const root = document.documentElement;
  root.dataset.theme = theme;
  root.dataset.accent = ACCENTS.some((a) => a.id === prefs().accent) ? prefs().accent : 'teal';
  document.querySelectorAll<HTMLMetaElement>('meta[name="theme-color"]').forEach((m) => {
    m.content = SURFACE[theme];
    m.removeAttribute('media');
  });
}

export function watchTheme(): void {
  applyTheme();
  systemDark.addEventListener('change', applyTheme);
  onPrefs(applyTheme);
}
