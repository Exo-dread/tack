import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { Hlc } from '../../src/model/hlc';
import { findIn, fold, foldQuery, originalRange, SearchIndex } from '../../src/model/search';
import { Workspace } from '../../src/model/workspace';

/**
 * Search's matching rule. The cases in tests/shared/search.json are Tack for Windows' too
 * (tests/Tack.Tests/SearchTests.cs): the two apps find exactly the same things.
 */

interface Shared {
  fold: [string, string][];
  match: { text: string; query: string; range: [number, number] | null }[];
  search: {
    spaces: string[];
    tasks: { text: string; space?: string; now?: boolean; done?: boolean }[];
    sheets: { text: string; space?: string }[];
    queries: { query: string; tasks: string[]; done: string[]; notes: string[] }[];
  };
}

const shared = JSON.parse(readFileSync(new URL('../../../tests/shared/search.json', import.meta.url), 'utf8')) as Shared;

const START = Date.UTC(2026, 9, 24, 9, 0, 0);

/** The shared workspace, made as the cases say (the same steps as the desktop's test). */
function sharedWorkspace(): Workspace {
  let wall = START;
  const ws = new Workspace(new Hlc('search01', () => wall), () => new Date(wall));
  const ids = new Map<string, string>();
  for (const name of shared.search.spaces) ids.set(name, ws.addSpace(name).id);
  const made: string[] = [];
  const tasks = shared.search.tasks;
  for (let i = tasks.length - 1; i >= 0; i--) {
    const t = tasks[i]!;
    made[i] = ws.addTask(t.text, { asNow: t.now, space: t.space ? ids.get(t.space) : undefined })!.id;
  }
  for (let i = tasks.length - 1; i >= 0; i--) {
    if (!tasks[i]!.done) continue;
    wall += 60_000;
    ws.complete(made[i]!);
  }
  for (const s of shared.search.sheets) ws.addSheet(s.text, s.space ? ids.get(s.space) : undefined);
  return ws;
}

describe('search: the shared rule (tests/shared/search.json)', () => {
  it.each(shared.fold)('folds %j', (text, folded) => {
    expect(fold(text)).toBe(folded);
  });

  it.each(shared.match.map((m) => [m.text, m.query, m.range] as const))('finds in %j: %j', (text, query, range) => {
    expect(findIn(text, query)).toEqual(range);
  });

  it('builds the shared workspace as the cases say', () => {
    const ws = sharedWorkspace();
    expect(ws.now?.text).toBe(shared.search.tasks.find((t) => t.now)?.text);
    expect([ws.now!, ...ws.open].map((t) => t.text)).toEqual(shared.search.tasks.filter((t) => !t.done).map((t) => t.text));
    expect(ws.done.map((t) => t.text)).toEqual(shared.search.tasks.filter((t) => t.done).map((t) => t.text));
  });

  it.each(shared.search.queries.map((q) => [q.query, q] as const))('searches every space for %j', (query, expected) => {
    const ws = sharedWorkspace();
    const r = new SearchIndex().find(ws, query);
    expect(r.tasks.map((h) => ws.tasks.get(h.id)!.text)).toEqual(expected.tasks);
    expect(r.done.map((h) => ws.tasks.get(h.id)!.text)).toEqual(expected.done);
    expect(r.notes.map((h) => ws.sheets.get(h.id)!.text)).toEqual(expected.notes);
    expect(r.total).toBe(expected.tasks.length + expected.done.length + expected.notes.length);
  });
});

describe('search: the rest', () => {
  it('leaves a lone surrogate as it is, and matches it', () => {
    expect(fold('a\ud83dB')).toBe('a\ud83db');
    expect(fold('\ude00')).toBe('\ude00');
    expect(findIn('x\ud83d y', '\ud83d')).toEqual([1, 2]);
  });

  it('trims only the spaces at either end of a query', () => {
    expect(foldQuery('  Clash  Report \n')).toBe('clash  report');
  });

  it('maps a match deep in a long note back to where it was typed', () => {
    const text = `${'Notes about the façade.\n'.repeat(20_000)}Ünique line`;
    const q = foldQuery('unique');
    const at = fold(text).indexOf(q);
    const [s, e] = originalRange(text, at, at + q.length);
    expect(text.slice(s, e)).toBe('Ünique');
  });

  it('finds what changed since the last query, and forgets what was deleted', () => {
    let wall = START;
    const ws = new Workspace(new Hlc('search02', () => wall), () => new Date(wall));
    const index = new SearchIndex();
    const t = ws.addTask('Draft the report')!;
    expect(index.find(ws, 'report').tasks.map((h) => h.id)).toEqual([t.id]);
    ws.editTask(t.id, 'Draft the memo');
    expect(index.find(ws, 'report').total).toBe(0);
    expect(index.find(ws, 'memo').tasks.map((h) => h.id)).toEqual([t.id]);
    wall += 1000;
    ws.deleteTask(t.id);
    expect(index.find(ws, 'memo').total).toBe(0);
  });

  it('stays quick on 10,000 tasks and 500 long notes', () => {
    let wall = START;
    const ws = new Workspace(new Hlc('search03', () => wall), () => new Date(wall));
    const words = ['clash', 'report', 'façade', 'Ducts', 'beams', 'model', 'Navisworks', 'level', 'review', 'Revit'];
    ws.batch(() => {
      for (let i = 0; i < 10_000; i++) ws.addTask(`${words[i % 10]} ${words[(i * 7) % 10]} task number ${i}`);
      for (let i = 0; i < 500; i++) {
        const lines = Array.from({ length: 200 }, (_, j) => `- ${words[(i + j) % 10]} item ${j} on sheet ${i}, with some more words to make it long`);
        ws.addSheet(`Sheet ${i}\n${lines.join('\n')}`);
      }
    });
    const index = new SearchIndex();
    const first = performance.now();
    index.find(ws, 'zzz');
    const firstMs = performance.now() - first;
    const t0 = performance.now();
    let found = 0;
    for (const q of ['c', 'cl', 'cla', 'clas', 'clash', 'facade', 'sheet 49', 'nothing like this']) found += index.find(ws, q).total;
    const typingMs = (performance.now() - t0) / 8;
    expect(found).toBeGreaterThan(10_000);
    // Generous budgets, to catch something accidentally slow (folding again on every keystroke).
    expect(firstMs).toBeLessThan(3000);
    expect(typingMs).toBeLessThan(150);
  });
});
