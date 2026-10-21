import { ZERO } from './hlc';
import type { Task } from './types';
import type { Workspace } from './workspace';

/** The welcome tasks and sheet as first written on this device (id → text). */
export interface Welcome {
  tasks: Record<string, string>;
  sheets: Record<string, string>;
}

/**
 * What a device joining a sync folder its other devices already use leaves behind: the welcome
 * tasks and sheet it made and you never touched, and a blank sheet never written on. A sheet
 * stays if it would be the last one. (Tack for Windows: Workspace.LeaveWelcomeBehind.)
 */
export function untouchedWelcome(ws: Workspace, welcome: Welcome): { tasks: string[]; sheets: string[] } {
  const me = ws.hlc.node;
  const mine = (stamp: string) => stamp.endsWith(`.${me}`);
  // Untouched: not edited, ticked or moved, in the list or to a space (every clock still the one
  // it was made with, here, and never put in a space).
  const untouched = (t: Task) =>
    mine(t.clk.text) && t.clk.completed === t.clk.text && t.clk.order === t.clk.text && t.clk.deleted === t.clk.text && t.clk.space === ZERO;
  const tasks = Object.entries(welcome.tasks)
    .filter(([id, text]) => {
      const t = ws.tasks.get(id);
      return !!t && !t.deleted && t.text === text && untouched(t);
    })
    .map(([id]) => id);
  // Only this device's own sheets (never one that just arrived from another device), never put in a space.
  const sheets = ws.sheetList
    .filter((s) => mine(s.clk.deleted) && s.clk.space === ZERO && (welcome.sheets[s.id] === s.text || (s.text === '' && s.clk.text === s.clk.deleted)))
    .map((s) => s.id);
  return { tasks, sheets: sheets.length === ws.sheetList.length ? sheets.slice(1) : sheets };
}
