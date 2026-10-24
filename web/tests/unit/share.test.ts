import { describe, expect, it } from 'vitest';
import { Hlc } from '../../src/model/hlc';
import { MAX_PASTE_LINES, MAX_TASK_TEXT } from '../../src/model/limits';
import { addShared, keptShare, readShare, sharedLines, type Shared } from '../../src/model/share';
import { MAIN } from '../../src/model/spaces';
import { Workspace } from '../../src/model/workspace';

const NOW = Date.UTC(2026, 9, 24, 9, 0, 0);
const ws = () => new Workspace(new Hlc('aaaa1111', () => NOW), () => new Date(NOW));
const share = (s: Partial<Shared>): Shared => ({ title: '', text: '', url: '', ...s });
const q = (fields: Record<string, string>, marker = true) => `?${marker ? 'share&' : ''}${new URLSearchParams(fields)}`;

describe('reading a share from the address', () => {
  it('is only a share when the address says so', () => {
    expect(readShare('')).toBeNull();
    expect(readShare('?new')).toBeNull();
    expect(readShare('?notes')).toBeNull();
    expect(readShare('?share')).toEqual(share({}));
  });

  it('reads the three fields, as the manifest names them', () => {
    expect(readShare(q({ title: 'Clash report', text: 'Level 3 ducts', url: 'https://example.com/r/1' }))).toEqual({
      title: 'Clash report',
      text: 'Level 3 ducts',
      url: 'https://example.com/r/1',
    });
  });

  it('is a share when the browser put the fields in place of "?share" (Android does)', () => {
    expect(readShare(q({ title: 'Page', text: 'https://example.com/' }, false))).toEqual(share({ title: 'Page', text: 'https://example.com/' }));
    expect(readShare('?text=hello')).toEqual(share({ text: 'hello' }));
  });

  it('decodes as a form does: + is a space, and whole characters survive', () => {
    expect(readShare('?share&text=one+two%20three')!.text).toBe('one two three');
    expect(readShare(`?share&text=${encodeURIComponent('Café 📌 日本')}`)!.text).toBe('Café 📌 日本');
  });

  it('reads broken encodings as U+FFFD, never half a character', () => {
    expect(readShare('?share&text=a%E0%A4')!.text).toBe('a�');
    expect(readShare('?share&text=50%')!.text).toBe('50%');
    expect(readShare('?share&text=%ED%A0%BD')!.text).not.toMatch(/[\uD800-\uDFFF]/);
  });

  it('takes the first of a repeated field and ignores unknown ones', () => {
    expect(readShare('?share&text=first&text=second&files=x&__proto__=y')).toEqual(share({ text: 'first' }));
  });
});

describe('a share kept for the next start', () => {
  it('reads back only three strings', () => {
    expect(keptShare({ title: 'a', text: 'b', url: 'c', extra: 1 })).toEqual({ title: 'a', text: 'b', url: 'c' });
    for (const bad of [null, 'text', 3, [], {}, { title: 'a', text: 'b' }, { title: 'a', text: 2, url: 'c' }]) expect(keptShare(bad)).toBeNull();
  });

  it('never keeps half a character', () => {
    expect(keptShare({ title: '', text: 'half \uD83D', url: '' })!.text).toBe('half �');
  });
});

describe('what a share becomes', () => {
  it('a page: its title with its address', () => {
    expect(sharedLines(share({ title: 'Revit 2024 release notes', text: 'https://help.autodesk.com/view/RVT/2024/ENU/' }))).toEqual([
      'Revit 2024 release notes https://help.autodesk.com/view/RVT/2024/ENU/',
    ]);
    expect(sharedLines(share({ title: 'Revit 2024 release notes', url: 'https://help.autodesk.com/' }))).toEqual(['Revit 2024 release notes https://help.autodesk.com/']);
    expect(sharedLines(share({ text: 'https://example.com/a' }))).toEqual(['https://example.com/a']);
  });

  it('the address only if it adds anything', () => {
    expect(sharedLines(share({ text: 'Read https://example.com/a', url: 'https://example.com/a' }))).toEqual(['Read https://example.com/a']);
    expect(sharedLines(share({ text: 'Read https://example.com/a/', url: 'https://example.com/a' }))).toEqual(['Read https://example.com/a/']);
    expect(sharedLines(share({ text: 'Read https://example.com/a', url: 'https://example.com/a/' }))).toEqual(['Read https://example.com/a']);
    expect(sharedLines(share({ text: 'Read https://example.com/ab', url: 'https://example.com/a' }))).toEqual(['Read https://example.com/ab https://example.com/a']);
    expect(sharedLines(share({ title: 'See https://example.com', text: 'https://example.com' }))).toEqual(['See https://example.com']);
  });

  it('a title and a line of text: both, unless one says the other', () => {
    expect(sharedLines(share({ title: 'Clash review', text: 'Check level 3 ducts' }))).toEqual(['Clash review: Check level 3 ducts']);
    expect(sharedLines(share({ title: 'Clash review?', text: 'Check level 3 ducts' }))).toEqual(['Clash review? Check level 3 ducts']);
    expect(sharedLines(share({ title: 'clash review', text: 'Clash review on Friday' }))).toEqual(['Clash review on Friday']);
    expect(sharedLines(share({ title: 'Clash review on Friday', text: 'clash review' }))).toEqual(['Clash review on Friday']);
    expect(sharedLines(share({ title: 'Only a title' }))).toEqual(['Only a title']);
    expect(sharedLines(share({ text: 'Only text' }))).toEqual(['Only text']);
  });

  it('is cleaned as typed text is: one line, single spaces', () => {
    expect(sharedLines(share({ title: '  Clash\treview  ', text: '  ' }))).toEqual(['Clash review']);
    expect(sharedLines(share({ text: '\n\n  Ducts   on level 3 \n\n' }))).toEqual(['Ducts on level 3']);
  });

  it('nothing at all, when nothing was shared', () => {
    expect(sharedLines(share({}))).toEqual([]);
    expect(sharedLines(share({ title: ' ', text: '\n\t\n', url: ' ' }))).toEqual([]);
  });

  it('several lines of text: a task per line, as a paste', () => {
    expect(sharedLines(share({ text: '- milk\r\n2. eggs\n\n[ ] bread\n' }))).toEqual(['milk', 'eggs', 'bread']);
  });

  it('several lines: the title first, unless the text says it', () => {
    expect(sharedLines(share({ title: 'Snags', text: 'Door 1.04\nDoor 1.05' }))).toEqual(['Snags', 'Door 1.04', 'Door 1.05']);
    expect(sharedLines(share({ title: 'Snags', text: 'Snags\nDoor 1.04\nDoor 1.05' }))).toEqual(['Snags', 'Door 1.04', 'Door 1.05']);
  });

  it('several lines: an address on its own joins the line above, unless that is an address too', () => {
    expect(sharedLines(share({ text: '"the selected words"\nhttps://example.com/#:~:text=the' }))).toEqual(['"the selected words" https://example.com/#:~:text=the']);
    expect(sharedLines(share({ text: 'Site office\nhttps://maps.example/x\nhttps://maps.example/y' }))).toEqual(['Site office https://maps.example/x', 'https://maps.example/y']);
    expect(sharedLines(share({ text: 'https://a.example\nhttps://b.example' }))).toEqual(['https://a.example', 'https://b.example']);
    expect(sharedLines(share({ text: 'One\nTwo', url: 'https://example.com/' }))).toEqual(['One', 'Two https://example.com/']);
    expect(sharedLines(share({ text: 'One https://example.com\nTwo', url: 'https://example.com/' }))).toEqual(['One https://example.com', 'Two']);
  });

  it('only an address that is the whole line counts as one', () => {
    expect(sharedLines(share({ text: 'One\njavascript:alert(1)' }))).toEqual(['One', 'javascript:alert(1)']);
    expect(sharedLines(share({ text: 'One\nftp://example.com' }))).toEqual(['One', 'ftp://example.com']);
  });
});

describe('adding what was shared', () => {
  it('adds the tasks at the top, in order, in the space given, with one Undo', () => {
    const w = ws();
    w.addTask('already here');
    const work = w.addSpace('Work');
    const result = addShared(w, share({ text: 'one\ntwo\nthree' }), work.id);
    expect(result.kind).toBe('added');
    if (result.kind !== 'added') return;
    expect(w.openIn(work.id).map((t) => t.text)).toEqual(['one', 'two', 'three']);
    expect(w.open.map((t) => t.text)).toEqual(['one', 'two', 'three', 'already here']);
    result.undo();
    expect(w.open.map((t) => t.text)).toEqual(['already here']);
  });

  it('into Main when no space is given', () => {
    const w = ws();
    w.addSpace('Work');
    const result = addShared(w, share({ text: 'shared' }));
    expect(result.kind === 'added' && w.spaceOf(result.added[0]!)).toBe(MAIN);
  });

  it('says why when nothing was added', () => {
    const w = ws();
    expect(addShared(w, share({ text: '   ' })).kind).toBe('empty');
    expect(addShared(w, share({ text: 'x'.repeat(MAX_TASK_TEXT + 1) })).kind).toBe('too-long');
    expect(addShared(w, share({ text: Array.from({ length: MAX_PASTE_LINES + 1 }, (_, i) => `t${i}`).join('\n') })).kind).toBe('too-many');
    expect(w.tasks.size).toBe(0);
  });
});
