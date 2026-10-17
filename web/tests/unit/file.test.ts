import { describe, expect, it } from 'vitest';
import { Hlc } from '../../src/model/hlc';
import { exportFile, readFile, stripJsonComments, FileError } from '../../src/model/file';
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
