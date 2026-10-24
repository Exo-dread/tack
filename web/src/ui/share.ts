import { keptShare, readShare, type Shared } from '../model/share';
import { wellFormedValues } from '../model/text';

/** This tab's share until it's added (sessionStorage: this tab only, gone when it closes). */
const KEY = 'tack.share';

/**
 * A share from another app (Android's share sheet opens /?share&title=…&text=…), taken off the
 * address bar before anything else, so a reload or a bookmark never adds it again. Kept in this
 * tab until `shareHandled`, so a start that goes no further (the daily renewal visit to Microsoft
 * first, or storage that won't open yet) adds it when the page comes back. Returns null if there's
 * none.
 */
export function takeShare(): Shared | null {
  const shared = readShare(location.search);
  if (shared) {
    history.replaceState(null, '', location.pathname + location.hash);
    try {
      sessionStorage.setItem(KEY, JSON.stringify(shared));
    } catch {
      /* too big for storage, or none: this start adds it, as long as it gets that far */
    }
    return shared;
  }
  let kept: Shared | null = null;
  try {
    const raw = sessionStorage.getItem(KEY);
    if (raw === null) return null;
    kept = keptShare(JSON.parse(raw, wellFormedValues));
  } catch {
    /* unreadable: dropped below */
  }
  if (!kept) shareHandled();
  return kept;
}

/** The share has been added (or turned away with a message): it's not for the next start. */
export function shareHandled(): void {
  try {
    sessionStorage.removeItem(KEY);
  } catch {
    /* nothing kept */
  }
}
