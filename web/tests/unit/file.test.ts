import { describe, expect, it } from 'vitest';
import { Hlc } from '../../src/model/hlc';
import { exportFile, importFile, readFile, stripJsonComments, FileError } from '../../src/model/file';
import { Workspace } from '../../src/model/workspace';

const now = Date.UTC(2026, 9, 17, 12, 0, 0);
const makeWs = (node = 'web') => new Workspace(new Hlc(node, () => now), () => new Date(now));

/** A file as Tack for Windows 1.1 writes it. */
const desktopFile = `﻿{
  "version": 1,
  "tasks": [
    { "id": "aaaaaaaaaaaa", "text": "Finish the drainage report draft", "created": "2026-10-16T09:00:00.1234567+01:00", "now": true },
    { "id": "bbbbbbbbbbbb", "text": "Reply to Mia about the site photos", "created": "2026-10-16T09:01:00+01:00", "now": false },
    { "id": "cccccccccccc", "text": "Book the crane", "created": "2026-10-16T09:02:00+01:00", "now": false },
    { "id": "dddddddddddd", "text": "Send the revised fee proposal", "created": "2026-10-15T09:02:00+01:00", "completed": "2026-10-17T08:00:00+01:00", "now": false },
  ],
  // hand-edited comment
  "sheets": [ { "id": "s1s1s1s1s1s1", "text": "Site visit\\n- rebar", "updated": "2026-10-16T10:00:00+01:00" } ],
  "settings": { "theme": "dark", "orbX": 100 }
}`;

describe('tack.json', () => {
  it('imports a Tack for Windows file', () => {
    const ws = makeWs();
    ws.merge(readFile(desktopFile, ws), 'local');
    expect(ws.now?.text).toBe('Finish the drainage report draft');
    expect(ws.open.map((t) => t.text)).toEqual(['Reply to Mia about the site photos', 'Book the crane']);
    expect(ws.done.map((t) => t.text)).toEqual(['Send the revised fee proposal']);
    expect(ws.sheetList.map((s) => s.text)).toEqual(['Site visit\n- rebar']);
  });

  it('re-importing the same file changes nothing, and never undoes local edits', () => {
    const ws = makeWs();
    ws.merge(readFile(desktopFile, ws), 'local');
    ws.complete('bbbbbbbbbbbb');
    ws.editTask('cccccccccccc', 'Book the crane for Thursday');
    const before = JSON.stringify(ws.toData());
    ws.merge(readFile(desktopFile, ws), 'local');
    expect(JSON.stringify(ws.toData())).toBe(before);
  });

  it('puts imported tasks above the current list', () => {
    const ws = makeWs();
    ws.addTask('mine');
    ws.merge(readFile(desktopFile, ws), 'local');
    expect(ws.open.map((t) => t.text)).toEqual(['Reply to Mia about the site photos', 'Book the crane', 'mine']);
  });

  it('exports a file the desktop app can read, and that round-trips here losslessly', () => {
    const a = makeWs('a');
    a.merge(readFile(desktopFile, a), 'local');
    const gone = a.addTask('secret deleted thing')!;
    a.deleteTask(gone.id);
    const file = exportFile(a);
    // Desktop view: focus first, then open in order, then done; no deleted tasks.
    expect(file.version).toBe(1);
    expect(file.tasks.map((t) => [t.text, t.now, 'completed' in t])).toEqual([
      ['Finish the drainage report draft', true, false],
      ['Reply to Mia about the site photos', false, false],
      ['Book the crane', false, false],
      ['Send the revised fee proposal', false, true],
    ]);
    const json = JSON.stringify(file);
    expect(json).not.toContain('secret deleted thing');

    const b = makeWs('b');
    b.merge(readFile(json, b), 'local');
    expect(b.now?.id).toBe(a.now?.id);
    expect(b.open.map((t) => t.id)).toEqual(a.open.map((t) => t.id));
    expect(b.tasks.get(gone.id)?.deleted).toBe(true);
  });

  it('takes changes made on the desktop since the last import', () => {
    const ws = makeWs();
    ws.merge(readFile(desktopFile, ws), 'local');
    // On the desktop: rename one task, reopen the done one, rewrite the note.
    const edited = desktopFile
      .replace('Book the crane', 'Book the crane for Thursday')
      .replace(', "completed": "2026-10-17T08:00:00+01:00"', '')
      .replace('Site visit\\n- rebar', 'Site visit\\n- rebar\\n- formwork');
    importFile(ws, edited, { lastModified: now });
    expect(ws.open.map((t) => t.text)).toContain('Book the crane for Thursday');
    expect(ws.done).toEqual([]);
    expect(ws.sheetList.map((s) => s.text)).toEqual(['Site visit\n- rebar\n- formwork']);
  });

  it('when both sides edited, the later edit wins, and notes are kept twice', () => {
    let wall = now;
    const ws = new Workspace(new Hlc('web', () => wall), () => new Date(wall));
    ws.merge(readFile(desktopFile, ws), 'local');
    wall += 60_000;
    ws.editTask('cccccccccccc', 'Book the crane (phone)');
    ws.editSheet('s1s1s1s1s1s1', 'Site visit\n- rebar\n- phone note');
    const edited = desktopFile.replace('Book the crane', 'Book the crane (desktop)').replace('- rebar', '- rebar\\n- desktop note');
    // The desktop saved before the phone's edit: the phone's text stays.
    importFile(ws, edited, { lastModified: now + 30_000 });
    expect(ws.tasks.get('cccccccccccc')?.text).toBe('Book the crane (phone)');
    // …and the desktop's note is kept as a copy beside the phone's.
    expect(ws.sheetList.map((s) => s.text)).toEqual(['Site visit\n- rebar\n- phone note', 'Site visit\n- rebar\n- desktop note']);
    // Saved after the phone's edit: the desktop's text wins. Importing again adds no more copies.
    importFile(ws, edited, { lastModified: now + 120_000 });
    expect(ws.tasks.get('cccccccccccc')?.text).toBe('Book the crane (desktop)');
    expect(ws.sheetList).toHaveLength(2);
  });

  it('keeps both versions of a note when importing an export with a different text', () => {
    let wall = now;
    const a = new Workspace(new Hlc('a', () => wall), () => new Date(wall));
    const s = a.addSheet('Plan\n- one');
    const exported = JSON.stringify(exportFile(a));
    const b = new Workspace(new Hlc('b', () => wall), () => new Date(wall));
    importFile(b, exported);
    wall += 1000;
    b.editSheet(s.id, 'Plan\n- one\n- two (b)');
    importFile(b, exported); // the older export comes back
    expect(b.sheetList.map((x) => x.text)).toEqual(['Plan\n- one\n- two (b)', 'Plan\n- one']);
    importFile(b, exported);
    expect(b.sheetList).toHaveLength(2);
  });

  it('takes a note written on Windows ("\\r\\n") there and back without a second copy', () => {
    let wall = now;
    // What Tack for Windows saved before notes were kept with "\n": the TextBox's "\r\n" everywhere.
    const desk = new Workspace(new Hlc('desk', () => wall), () => new Date(wall));
    const s = desk.addSheet('Site visit\r\n- rebar\r\n- formwork');
    expect(s.text).toBe('Site visit\n- rebar\n- formwork');
    const saved = JSON.stringify(exportFile(desk)).replaceAll('\\n', '\\r\\n');
    expect(saved).toContain('Site visit\\r\\n- rebar');

    const phone = new Workspace(new Hlc('phone', () => wall), () => new Date(wall));
    importFile(phone, saved);
    expect(phone.sheets.get(s.id)?.text).toBe('Site visit\n- rebar\n- formwork');
    wall += 1000;
    let changes = 0;
    phone.subscribe(() => changes++);
    phone.editSheet(s.id, 'Site visit\n- rebar\n- formwork'); // what the textarea hands over
    importFile(phone, saved);
    expect(changes).toBe(0);

    const before = JSON.stringify(desk.toData());
    importFile(desk, JSON.stringify(exportFile(phone)));
    expect(JSON.stringify(desk.toData())).toBe(before);
    expect(desk.sheetList).toHaveLength(1);
  });

  it('ignores records with odd ids or empty text', () => {
    const ws = makeWs();
    const t = ws.addTask('real')!;
    const file = exportFile(ws);
    const bad = { ...file.tackFocus.tasks[0], id: 'x"]', text: 'evil' };
    const blank = { ...file.tackFocus.tasks[0], id: 'blankblank12', text: '   ' };
    const doc = { ...file, tackFocus: { ...file.tackFocus, tasks: [...file.tackFocus.tasks, bad, blank] } };
    const data = readFile(JSON.stringify(doc), makeWs('other'));
    expect(data.tasks.map((x) => x.id)).toEqual([t.id]);
  });

  it('rejects files that are not Tack files', () => {
    const ws = makeWs();
    expect(() => readFile('not json', ws)).toThrow(FileError);
    expect(() => readFile('{"hello": 1}', ws)).toThrow(FileError);
    expect(() => readFile('[1,2]', ws)).toThrow(FileError);
  });

  it('strips comments and trailing commas but not inside strings', () => {
    expect(JSON.parse(stripJsonComments('{"a": "http://x//y", /* c */ "b": [1,2,],}'))).toEqual({
      a: 'http://x//y',
      b: [1, 2],
    });
    expect(JSON.parse(stripJsonComments('{"q": "say \\"hi\\", // not a comment",}'))).toEqual({
      q: 'say "hi", // not a comment',
    });
  });
});
