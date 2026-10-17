import 'fake-indexeddb/auto';
import { describe, expect, it } from 'vitest';
import { Hlc } from '../../src/model/hlc';
import { exportFile, importFile } from '../../src/model/file';
import { Workspace } from '../../src/model/workspace';
import { IdbBackend } from '../../src/store/db';

const make = (node: string, wall: () => number) => new Workspace(new Hlc(node, wall), () => new Date(wall()));

describe('IndexedDB storage', () => {
  it('merges every write with what is stored, so a stale tab cannot undo a newer edit', async () => {
    let wall = Date.UTC(2026, 9, 17, 9);
    const db = await IdbBackend.open();
    const tabA = make('tabA', () => wall);
    const task = tabA.addTask('Draft report')!;
    await db.write({ tasks: [task], sheets: [] });

    // Tab B loaded the task before tab A renamed it.
    const tabB = make('tabB', () => wall);
    tabB.merge((await db.load()).data, 'storage');

    wall += 1000;
    tabA.editTask(task.id, 'Draft drainage report');
    await db.write({ tasks: [tabA.tasks.get(task.id)!], sheets: [] });

    // Tab B, not having heard about the rename yet, ticks the task and saves its whole record.
    wall += 1000;
    tabB.complete(task.id);
    await db.write({ tasks: [tabB.tasks.get(task.id)!], sheets: [] });

    const stored = (await db.load()).data.tasks.find((t) => t.id === task.id)!;
    expect(stored.text).toBe('Draft drainage report');
    expect(stored.completed).not.toBeNull();
  });

  it('keeps the focus register merged too', async () => {
    let wall = Date.UTC(2026, 9, 17, 10);
    const db = await IdbBackend.open();
    const a = make('a', () => wall);
    const t = a.addTask('x')!;
    a.setNow(t.id);
    const newer = { ...a.focus };
    wall -= 5000;
    const older = { id: null, clk: new Hlc('b', () => wall).now() };
    await db.write({ tasks: [t], sheets: [], focus: newer });
    await db.write({ tasks: [], sheets: [], focus: older });
    expect((await db.load()).data.focus).toEqual(newer);
  });
});

describe('exports', () => {
  it('still converge when a deleted task travels without its text', () => {
    let wall = Date.UTC(2026, 9, 17, 11);
    const a = make('a', () => wall);
    const t = a.addTask('private thing')!;
    const b = make('b', () => wall);
    b.merge(a.toData(), 'remote');
    wall += 1000;
    a.deleteTask(t.id);
    // A third device learns of the deletion only through an export (text blanked).
    const c = make('c', () => wall);
    importFile(c, JSON.stringify(exportFile(a)));
    for (const [x, y] of [
      [b, c],
      [c, a],
      [a, b],
    ] as const) {
      x.merge(y.toData(), 'remote');
      y.merge(x.toData(), 'remote');
    }
    const view = (w: Workspace) => JSON.stringify([...w.tasks.values()].map((x) => [x.id, x.deleted, x.text]));
    expect(view(a)).toBe(view(b));
    expect(view(b)).toBe(view(c));
    expect(a.tasks.get(t.id)?.deleted).toBe(true);
  });
});
