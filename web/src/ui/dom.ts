type Child = Node | string | null | undefined | false;
type Attrs = Record<string, string | number | boolean | null | undefined | EventListener>;

/**
 * Tiny element builder: h('button.check', { 'aria-label': 'Done', onclick }, child…).
 * Attributes starting with "on" become listeners; false/null/undefined attributes are skipped.
 * Never sets inline style attributes (the CSP forbids them); use element.style instead.
 */
export function h<K extends keyof HTMLElementTagNameMap>(
  spec: K | `${K}.${string}` | `${K}#${string}`,
  attrs: Attrs = {},
  ...children: Child[]
): HTMLElementTagNameMap[K] {
  const tag = /^[a-z0-9]+/i.exec(spec)![0];
  const el = document.createElement(tag) as HTMLElementTagNameMap[K];
  const classes: string[] = [];
  for (const [, kind, name] of spec.slice(tag.length).matchAll(/([.#])([^.#]+)/g)) {
    if (kind === '#') el.id = name;
    else classes.push(name);
  }
  if (classes.length) el.className = classes.join(' ');
  for (const [key, value] of Object.entries(attrs)) {
    if (value === false || value === null || value === undefined) continue;
    if (key.startsWith('on') && typeof value === 'function') {
      el.addEventListener(key.slice(2), value as EventListener);
    } else if (key === 'class') {
      el.className = [el.className, String(value)].filter(Boolean).join(' ');
    } else if (key === 'text') {
      el.textContent = String(value);
    } else {
      el.setAttribute(key, value === true ? '' : String(value));
    }
  }
  for (const c of children) if (c !== null && c !== undefined && c !== false) el.append(c);
  return el;
}

const parsed = new Map<string, SVGSVGElement>();

/** Trusted, static SVG markup (our icons) as an element. Each icon is parsed once, then cloned. */
export function svg(markup: string): SVGSVGElement {
  let el = parsed.get(markup);
  if (!el) {
    const t = document.createElement('template');
    t.innerHTML = markup.trim();
    el = t.content.firstElementChild as SVGSVGElement;
    parsed.set(markup, el);
  }
  return el.cloneNode(true) as SVGSVGElement;
}

export function qs<T extends Element = HTMLElement>(root: ParentNode, selector: string): T {
  const el = root.querySelector<T & Element>(selector);
  if (!el) throw new Error(`Missing element: ${selector}`);
  return el;
}

export const isEditable = (el: Element | null): boolean =>
  !!el && (el instanceof HTMLTextAreaElement || (el instanceof HTMLInputElement && !['checkbox', 'radio', 'button', 'file'].includes(el.type)) || (el as HTMLElement).isContentEditable);

export const coarsePointer = (): boolean => matchMedia('(pointer: coarse)').matches;

export const isIOS = (): boolean =>
  /iP(hone|ad|od)/.test(navigator.userAgent) || (navigator.userAgent.includes('Mac') && navigator.maxTouchPoints > 1);

export const isStandalone = (): boolean =>
  matchMedia('(display-mode: standalone)').matches || (navigator as Navigator & { standalone?: boolean }).standalone === true;
