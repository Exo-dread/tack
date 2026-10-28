# Notes in Markdown

A note shows formatted while you read it and is the plain editor while you write it. The text stored,
synced and exported is exactly what you typed: formatting is only how it's shown. Both apps read the same
Markdown the same way (`web/src/model/markdown.ts`, `src/Tack/Model/Markdown.cs`, shared cases in
`tests/vectors/markdown.json`).

## When it's formatted

- **Reading:** the note on show is formatted whenever its editor doesn't have the keyboard: on opening Notes
  or switching sheet, after clicking or tabbing away, after the panel folds (desktop) or the app is hidden (web).
  Opening Notes or switching sheet no longer puts the caret in the editor; Ctrl+N (new sheet) and the blank
  paper of a space with no sheets still do.
- **Writing:** a click or tap on the formatted note (one that isn't a drag to select text, a checkbox or the
  scroll bar) opens the editor with the caret at the start of the line the click landed on, scrolled so that
  line stays where it was. Anything that selects text in a note (search, Ctrl+Enter's task link, Undo)
  opens the editor with that selection.
- **Keys on the formatted note** (one rule, both apps). "The caret" is where you last wrote in this sheet (the
  end of the note if you haven't), with nothing selected: a selection left in the hidden editor (search's
  match, say) is never typed over. When a checkbox has the keyboard, it's the end of that item's line instead.
  - A printable character opens the editor at the caret and is typed there.
  - Enter or F2 opens the editor at the caret and types nothing.
  - F5 and Ctrl+Enter open the editor at the caret and do what they do there: F5 inserts the date and time
    (never the browser's reload), Ctrl+Enter turns the caret's line into a task.
  - Ctrl+V (and Shift+Insert) pastes at the caret (an image on the desktop is a snippet, as in the editor).
  - Space on a checkbox ticks it. Ctrl+Z undoes the last tick (Tack Focus) or opens the editor and undoes
    (Tack for Windows).
- An empty note is always the editor (with its hint). A note longer than 200,000 characters is never
  formatted (it stays the editor), so a huge paste can't stall the panel.
- **Format notes** in Settings (on by default, this device only, never synced or imported) turns it off: then
  notes are always the editor, exactly as in 1.8.0 / 0.5.0.
- Snippets (desktop) stay above the text, as now, in both views.
- Selecting text: in Tack Focus you can select and copy in the formatted note (a drag doesn't open the editor).
  In Tack for Windows any click opens the editor at that line, where you select as usual: its formatted view lays
  out only the lines on screen, which is what keeps a long note fast.

## What's formatted

Line-based, CommonMark-flavoured, no HTML, no images, no tables. Anything not listed is plain text.

Blocks (a line's leading spaces: a tab counts as 4):
- `#`, `##`, `###` + space + text: headings 1-3. `####` to `######` are level 3 too. A trailing run of `#` is
  dropped. `#tag` (no space) is plain text.
- `-`, `*`, `+` + space: a bullet item. `1.` / `1)` + space (1-9 digits): a numbered item; the list starts at
  the first item's number. Items continue while lines are items of the same kind at the same indent; a deeper
  indent (2+ spaces more than the item's marker) starts a nested list inside the previous item (at most 8 levels;
  deeper is the 8th). `-`, `*` and `+` are one kind, numbers the other; a change of kind starts a new list. A
  non-item line indented at least as far as an open item's text continues the deepest such item: as a line break
  in its text, or, once the item has a nested list, as a paragraph after that list. Any other non-item line ends
  the list. Blank lines inside a list are dropped. Inside an item's text, `#`, `>` and fences are plain text.
- `- [ ] ` / `- [x] ` / `- [X] ` (any bullet marker, also numbered): a checklist item with a checkbox. Ticking
  it in the formatted note changes exactly that `[ ]` to `[x]` (or back) in the text, as one edit with Undo,
  saved and synced like typing. The item's text is shown struck through and muted when ticked.
- `>` + optional space: a quote; its content is parsed as blocks (lists inside quotes work). Consecutive `>`
  lines form one quote.
- A line of 3+ `` ` `` or `~` (optionally followed by a language word, which is ignored) opens a code block,
  closed by a line of at least as many of the same character, or the end of the note. Its lines are shown as they
  are, monospace, no inline formatting.
- `---`, `***` or `___` (3+ of the same character, spaces between allowed), alone on a line: a rule. A `---`
  right under a paragraph line is a rule too (no setext headings).
- Anything else is a paragraph: consecutive non-blank lines, each line break kept (notes are line-oriented,
  unlike CommonMark). Blank lines separate blocks; several blank lines show as one gap.

Inline (in headings, paragraphs, items and quotes):
- `` `code` `` (runs of backticks match runs of the same length; no formatting inside).
- `**bold**` / `__bold__`, `*italic*` / `_italic_`, `***both***`, `~~struck~~`. A delimiter opens only if
  followed by a non-space and closes only if preceded by a non-space; `_` only at word edges (snake_case
  stays plain). Unmatched delimiters are plain text. Matching is linear: no backtracking, and a run of
  100,000 `*` costs no more than 100,000 letters.
- `[text](url)`: shown as `text`, styled as a link but **not clickable** (synced text never becomes something
  to open: CLAUDE.md). The url is in its tooltip / title as plain text. `<https://…>` and bare URLs are plain text.
- `\` before any ASCII punctuation shows that character literally.
- Nesting: inline formatting nests at most 8 deep; deeper delimiters are plain text.

## The tree (shared cases)

`parse(text)` returns blocks; each case in `tests/vectors/markdown.json` is `{ "name", "text", "blocks" }`.

```
block   := {"h": 1|2|3, "c": inline[]}
         | {"p": inline[]}                     (line breaks inside are {"br": 1})
         | {"ul": item[]} | {"ol": item[], "start": n}
         | {"quote": block[]}
         | {"code": "text"}                    (lines joined with "\n", no trailing newline)
         | {"hr": 1}
item    := {"c": inline[], "task": null|false|true, "at": n|null, "sub": block[]}
           ("at": UTF-16 index in the note's text of the character between [ and ], null if not a task)
inline  := "text" | {"b": inline[]} | {"i": inline[]} | {"s": inline[]} | {"code": "text"}
         | {"link": inline[], "url": "text"} | {"br": 1}
```

Adjacent text pieces are merged into one string.

Details both parsers share (each pinned by a case): inline formatting never crosses a line break; leading and
trailing spaces and tabs are trimmed from paragraph, heading and item lines (code lines are kept as they are);
`# C#` keeps `C#` (a closing `#` run is dropped only after a space); rules are checked before list items, so
`- - -` is a rule; a task box is `[ ]`, `[x]` or `[X]` right at the item's text, followed by a space or the end
of the line; quotes have no lazy continuation (a line without `>` ends the quote); a backtick fence whose rest
of line holds a backtick is not a fence; `_` needs a non-word character outside it (code units 0x80 and up
count as word characters, except U+00A0 and U+3000); `***x***` is italic around bold; code spans pair with the
next run of the same length and lose one space at each end when both ends are spaces; a link's url runs to the
first `)`, with no escapes, and links don't nest. Line endings: `\r\n` and `\r` are read as `\n` (offsets stay
those of the original text).

## Cost

- Typing is untouched: the editor is the same plain text box, and nothing is parsed while it has the keyboard.
- The note is parsed and laid out once each time the formatted view is shown or the note changes while shown
  (a sync from another device, a ticked box), only the note on show. Nothing runs when idle.
- Budgets (measured, in the PR): a 20 KB note shows in under 16 ms on the desktop at 150% and under 8 ms in
  Chrome; a 200 KB note in under 150 ms. Panel open, tab switch and fold timings unchanged (`--frames`).
