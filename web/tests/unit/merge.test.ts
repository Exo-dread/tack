import { describe, expect, it } from 'vitest';
import { Hlc } from '../../src/model/hlc';
import { Workspace } from '../../src/model/workspace';
import type { WorkspaceData } from '../../src/model/types';

/** Two devices with their own clocks, editing offline and then exchanging everything. */
function devices() {
  let wall = Date.UTC(2026, 9, 17, 22, 0, 0);
  const tick = () => (wall += 1000);
  const make = (node: string) => new Workspace(new Hlc(node, () => wall), () => new Date(wall));
  return { phone: make('phone'), desk: make('desk'), tick };
}

function sync(a: Workspace, b: Workspace, base?: string) {
  const fromA = a.toData();
  const fromB = b.toData();
  a.merge(fromB, 'remote', base);
  b.merge(fromA, 'remote', base);
}

function visible(ws: Workspace) {
  return {
    now: ws.now?.text ?? null,
    open: ws.open.map((t) => t.text),
    done: ws.done.map((t) => t.text),
    sheets: ws.sheetList.map((s) => s.text),
  };
}

describe('merging two devices', () => {
  it('brings a task added on the phone to the desktop', () => {
    const { phone, desk, tick } = devices();
    desk.addTask('existing');
    sync(phone, desk);
    tick();
    phone.addTask('call the plumber tomorrow');
    sync(phone, desk);
    expect(visible(desk).open).toEqual(['call the plumber tomorrow', 'existing']);
    expect(visible(desk)).toEqual(visible(phone));
  });

  it('keeps independent edits to the same task', () => {
    const { phone, desk, tick } = devices();
    const t = desk.addTask('draft report')!;
    sync(phone, desk);
    tick();
    phone.editTask(t.id, 'draft drainage report');
    tick();
    desk.complete(t.id);
    sync(phone, desk);
    expect(visible(phone).done).toEqual(['draft drainage report']);
    expect(visible(desk)).toEqual(visible(phone));
  });

  it('lets a later delete win over an earlier edit, and undo bring it back', () => {
    const { phone, desk, tick } = devices();
    const t = desk.addTask('x')!;
    sync(phone, desk);
    tick();
    phone.editTask(t.id, 'x edited');
    tick();
    const undo = desk.deleteTask(t.id);
    sync(phone, desk);
    expect(visible(phone).open).toEqual([]);
    tick();
    undo();
    sync(phone, desk);
    expect(visible(phone).open).toEqual(['x edited']);
    expect(visible(desk)).toEqual(visible(phone));
  });

  it('agrees on one focus task', () => {
    const { phone, desk, tick } = devices();
    const a = desk.addTask('a')!;
    const b = desk.addTask('b')!;
    sync(phone, desk);
    tick();
    phone.setNow(a.id);
    tick();
    desk.setNow(b.id);
    sync(phone, desk);
    expect(phone.now?.id).toBe(b.id);
    expect(desk.now?.id).toBe(b.id);
  });

  it('keeps both versions of a note edited on two devices at once', () => {
    const { phone, desk, tick } = devices();
    const s = desk.addSheet('Shopping\n- milk');
    sync(phone, desk);
    const base = desk.sheets.get(s.id)!.clk.text;
    tick();
    phone.editSheet(s.id, 'Shopping\n- milk\n- eggs');
    tick();
    desk.editSheet(s.id, 'Shopping\n- milk\n- bread');
    sync(phone, desk, base);
    expect(visible(phone).sheets).toEqual(['Shopping\n- milk\n- bread', 'Shopping\n- milk\n- eggs']);
    expect(visible(desk)).toEqual(visible(phone));
    // Merging again changes nothing.
    sync(phone, desk, base);
    expect(desk.sheetList).toHaveLength(2);
  });

  it('converges whatever order changes arrive in (randomised)', () => {
    for (let round = 0; round < 40; round++) {
      let seed = round + 1;
      const rand = () => ((seed = (seed * 16807) % 2147483647) / 2147483647);
      const { phone, desk, tick } = devices();
      const third = new Workspace(new Hlc('tab', () => Date.now()), () => new Date());
      for (const t of ['a', 'b', 'c', 'd']) desk.addTask(t);
      sync(phone, desk);
      third.merge(desk.toData(), 'remote');

      const snapshots: WorkspaceData[] = [];
      for (let i = 0; i < 30; i++) {
        tick();
        const ws = rand() < 0.5 ? phone : desk;
        const open = ws.open;
        const pick = open[Math.floor(rand() * open.length)];
        const op = Math.floor(rand() * 7);
        if (op === 0) ws.addTask(`n${i}`);
        else if (pick && op === 1) ws.complete(pick.id);
        else if (pick && op === 2) ws.deleteTask(pick.id);
        else if (pick && op === 3) ws.moveTask(pick.id, Math.floor(rand() * open.length));
        else if (pick && op === 4) ws.setNow(pick.id);
        else if (pick && op === 5) ws.editTask(pick.id, `${pick.text}!`);
        else if (ws.done[0]) ws.reopen(ws.done[0].id);
        if (rand() < 0.3) snapshots.push(ws.toData());
      }
      // A third replica receives partial snapshots out of order, then everything.
      for (const s of snapshots.reverse()) third.merge(s, 'remote');
      sync(phone, desk);
      third.merge(phone.toData(), 'remote');
      expect(visible(phone)).toEqual(visible(desk));
      expect(visible(third)).toEqual(visible(desk));
    }
  });
});
