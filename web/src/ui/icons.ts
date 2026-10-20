/** Icons: 24px grid, 1.75px strokes, round ends. Static markup, rendered with svg(). */

const icon = (body: string, extra = '') =>
  `<svg class="icon" viewBox="0 0 24 24" width="24" height="24" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false"${extra}>${body}</svg>`;

export const icons = {
  tasks: icon('<circle cx="12" cy="12" r="8.25"/><path d="m8.6 12.2 2.3 2.3 4.6-4.9"/>'),
  notes: icon('<path d="M7 3.75h7.5l3.75 3.75V19a1.25 1.25 0 0 1-1.25 1.25H7A1.25 1.25 0 0 1 5.75 19V5A1.25 1.25 0 0 1 7 3.75Z"/><path d="M14.25 3.9V7.75h3.85M8.75 12h6.5M8.75 15.5h4.5"/>'),
  plus: icon('<path d="M12 5.5v13M5.5 12h13"/>'),
  send: icon('<path d="M12 18.5V6M6.5 11.25 12 5.75l5.5 5.5"/>', ' stroke-width="2.1"'),
  gear: icon('<circle cx="12" cy="12" r="2.6"/><path d="M19.2 13.6a7.4 7.4 0 0 0 0-3.2l1.9-1.5-1.9-3.3-2.3.9a7.3 7.3 0 0 0-2.7-1.6L13.8 2.5h-3.6l-.4 2.4a7.3 7.3 0 0 0-2.7 1.6l-2.3-.9-1.9 3.3 1.9 1.5a7.4 7.4 0 0 0 0 3.2l-1.9 1.5 1.9 3.3 2.3-.9a7.3 7.3 0 0 0 2.7 1.6l.4 2.4h3.6l.4-2.4a7.3 7.3 0 0 0 2.7-1.6l2.3.9 1.9-3.3Z"/>'),
  focus: icon('<circle cx="12" cy="12" r="7.75"/><circle cx="12" cy="12" r="2.75" fill="currentColor" stroke="none"/>'),
  unfocus: icon('<path d="m7 7 10 10M17 7 7 17"/>'),
  trash: icon('<path d="M5 7h14M10 4.75h4M7 7l.8 11.2A1.75 1.75 0 0 0 9.55 19.8h4.9a1.75 1.75 0 0 0 1.75-1.6L17 7M10.25 10.5v5.5M13.75 10.5v5.5"/>'),
  up: icon('<path d="M12 18.5V5.75M6.75 11 12 5.75 17.25 11"/>'),
  down: icon('<path d="M12 5.5v12.75M6.75 13 12 18.25 17.25 13"/>'),
  chevron: icon('<path d="m8 10 4 4 4-4"/>'),
  close: icon('<path d="m7 7 10 10M17 7 7 17"/>'),
  toTask: icon('<path d="M4.5 7.5h9M4.5 12h6M4.5 16.5h4.5"/><circle cx="16.75" cy="15.25" r="4"/><path d="m14.9 15.3 1.3 1.3 2.4-2.6"/>'),
  clock: icon('<circle cx="12" cy="12" r="8.25"/><path d="M12 7.5V12l3 2"/>'),
  keyboardDown: icon('<rect x="3.5" y="4.5" width="17" height="10" rx="2"/><path d="M7 8h.01M10 8h.01M13 8h.01M16.5 8h.01M8 11.25h8M9.5 18.25 12 20.5l2.5-2.25"/>'),
  share: icon('<path d="M12 14.5V3.75M8.25 7.25 12 3.5l3.75 3.75"/><path d="M8 10.5H6.75A1.75 1.75 0 0 0 5 12.25v6.5a1.75 1.75 0 0 0 1.75 1.75h10.5A1.75 1.75 0 0 0 19 18.75v-6.5a1.75 1.75 0 0 0-1.75-1.75H16"/>'),
  addHome: icon('<rect x="4.25" y="4.25" width="15.5" height="15.5" rx="3.5"/><path d="M12 8.5v7M8.5 12h7"/>'),
  download: icon('<path d="M12 4v11M7.75 11 12 15.25 16.25 11M5 19.75h14"/>'),
  upload: icon('<path d="M12 15.5v-11M7.75 8.75 12 4.5l4.25 4.25M5 19.75h14"/>'),
  restore: icon('<path d="M4.75 12a7.25 7.25 0 1 0 2.2-5.2"/><path d="M4.5 4.75v3.5H8"/><path d="M12 8.5V12l2.5 1.5"/>'),
  grip: icon('<circle cx="9" cy="7" r=".9" fill="currentColor"/><circle cx="15" cy="7" r=".9" fill="currentColor"/><circle cx="9" cy="12" r=".9" fill="currentColor"/><circle cx="15" cy="12" r=".9" fill="currentColor"/><circle cx="9" cy="17" r=".9" fill="currentColor"/><circle cx="15" cy="17" r=".9" fill="currentColor"/>'),
  cloud: icon('<path d="M7.25 18.25h9.5a3.75 3.75 0 0 0 .6-7.45 5.25 5.25 0 0 0-10.2-.9A4.2 4.2 0 0 0 7.25 18.25Z"/>'),
  reopen: icon('<path d="M5 12a7 7 0 1 0 2.05-4.95"/><path d="M4.75 4.5v3.75H8.5"/>'),
};

/** The checkbox: a ring that fills, and a tick that draws itself. */
export const checkMark = `<svg class="check-art" viewBox="0 0 28 28" aria-hidden="true" focusable="false">
  <circle class="check-ring" cx="14" cy="14" r="11.25"/>
  <circle class="check-fill" cx="14" cy="14" r="12"/>
  <path class="check-tick" d="M8.6 14.4l3.6 3.6 7.2-7.6" pathLength="1"/>
</svg>`;
