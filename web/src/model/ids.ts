/** 12 hex characters, the same shape as Tack for Windows' ids. */
export function newId(): string {
  const bytes = new Uint8Array(6);
  crypto.getRandomValues(bytes);
  return Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
}

/** A short random device id, used to break ties between clocks. */
export function newDeviceId(): string {
  const alphabet = '0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ';
  const bytes = new Uint8Array(8);
  crypto.getRandomValues(bytes);
  return Array.from(bytes, (b) => alphabet[b % alphabet.length]).join('');
}

/**
 * A 12-hex id derived from a string, for things every device must create identically
 * (e.g. the copy kept when two devices edited the same note at once).
 */
export function derivedId(source: string): string {
  // Two independent FNV-1a passes give 64 bits; 48 of them are used.
  let h1 = 0x811c9dc5;
  let h2 = 0x01000193 ^ 0x5bd1e995;
  for (let i = 0; i < source.length; i++) {
    const c = source.charCodeAt(i);
    h1 = Math.imul(h1 ^ c, 0x01000193);
    h2 = Math.imul(h2 ^ c, 0x5bd1e995) ^ (h2 >>> 15);
  }
  const hex = (n: number) => (n >>> 0).toString(16).padStart(8, '0');
  return (hex(h1) + hex(h2)).slice(0, 12);
}
