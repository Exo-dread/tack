/// <reference types="node" />
/**
 * Markdown in Notes (docs/NOTES-MARKDOWN.md). The shared cases in tests/vectors/markdown.json are
 * written from CASES by src/model/markdown.ts (`npm run vectors` rewrites them; `npm test` fails if
 * they're out of date), and Tack for Windows' port (src/Tack/Model/Markdown.cs) must read every
 * one the same. The rules below are also checked by hand, so the cases can't quietly drift.
 */
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { lineStarts, parse, setTask, type Block, type Inline } from '../../src/model/markdown';

const VECTORS = '../../../tests/vectors/markdown.json';

/** [name, text]. Each rule of the spec, its edges, and what stays plain. */
export const CASES: [string, string][] = [
  // ---- paragraphs and lines
  ['empty note', ''],
  ['only blank lines', '\n\n  \n\t\n'],
  ['one line', 'Hello there'],
  ['line breaks are kept', 'one\ntwo\nthree'],
  ['blank lines separate paragraphs; several show as one gap', 'one\n\n\n\ntwo'],
  ['leading and trailing spaces of a line go', '   padded   \n\tTabbed\t'],
  ['CRLF and lone CR are line ends', 'a\r\nb\rc\n\r\nd'],
  ['an indented line is not code', '    four spaces\n\tand a tab'],
  // ---- headings
  ['headings 1 to 3', '# One\n## Two\n### Three'],
  ['levels 4 to 6 are level 3', '#### Four\n##### Five\n###### Six'],
  ['seven # is plain text', '####### Seven'],
  ['#tag is plain text', '#tag and #another'],
  ['a lone # is plain text', '#\n##'],
  ['a trailing run of # is dropped', '# Title ##\n## Also #####   '],
  ['a # stuck to a word stays', '# C#\n## F# and C##'],
  ['a heading of only #', '# ###'],
  ['an escaped closing #', '# Keep \\#'],
  ['a heading with inline formatting', '## A **bold** _move_'],
  ['a heading interrupts a paragraph', 'text\n# Heading\nmore'],
  ['a heading can be indented', '   # Indented'],
  ['a tab after # makes a heading', '#\tTabbed'],
  // ---- rules
  ['rules of -, * and _', '---\n***\n___'],
  ['rules with spaces between and more characters', '- - -\n *  *  * *\n_____'],
  ['a --- under a paragraph is a rule (no setext headings)', 'Title\n---\nafter'],
  ['=== is plain', 'Title\n==='],
  ['two dashes or mixed characters are plain', '--\n-*-\n--- x'],
  // ---- bullet lists
  ['bullets with -, * and +', '- one\n* two\n+ three'],
  ['a marker needs a space after it', '-one\n*two*\n+three'],
  ['an empty item', '- \n- b'],
  ['nested lists by 2 spaces', '- a\n  - b\n    - c\n  - d\n- e'],
  ['one space more is the same level', '- a\n - b\n- c'],
  ['a tab counts as 4 for nesting', '- a\n\t- b\n\t\t- c'],
  ['lists nest at most 8 deep; deeper is the 8th', Array.from({ length: 11 }, (_, i) => `${'  '.repeat(i)}- L${i + 1}`).join('\n')],
  ['a line indented to the item text continues it', '- first\n  still first\n- second'],
  ['a blank line ends nothing by itself', '- a\n\n- b\n\n  more b'],
  ['a non-indented line after a blank ends the list', '- a\n\nparagraph'],
  ['a non-indented line right after an item ends the list', '- a\nparagraph'],
  ['a line indented less than the item text ends the list', '- a\n x'],
  ['a line under a nested item, indented to the outer item, continues the outer item', '- a\n  - b\n  about a\n  and more\n- c'],
  ['other blocks inside an item are its text', '- a\n  # not a heading\n  > not a quote\n  ```'],
  ['a heading, quote or rule at the margin ends a list', '- a\n# H\n- b\n> q\n- c\n---'],
  ['a list right after a paragraph line', 'Shopping:\n- milk\n- eggs'],
  ['extra spaces after the marker move the item text', '-   wide\n    continued'],
  // ---- numbered lists
  ['numbered with . and )', '1. one\n2) two\n3. three'],
  ['the list starts at the first number', '7. seven\n8. eight\n1. nine'],
  ['up to 9 digits', '123456789. big\n1234567890. too big'],
  ['a number needs . or ) and a space', '1.one\n2 two\n3:three'],
  ['bullets and numbers are different lists', '- a\n- b\n1. one\n2. two\n- c'],
  ['a numbered list nested in a bullet', '- a\n  1. one\n  2. two\n- b'],
  ['changing kind in a nested list starts another list in the item', '- a\n  - b\n  1. c\n  2. d'],
  ['0 starts a list', '0. zero\n1. one'],
  // ---- tasks
  ['tasks: [ ], [x] and [X]', '- [ ] open\n- [x] done\n- [X] also done'],
  ['tasks with any bullet, and numbered', '* [ ] star\n+ [x] plus\n1. [ ] numbered\n2) [x] paren'],
  ['task offsets with CRLF', 'Todo\r\n- [ ] one\r\n- [x] two\r\n\r\n  - [ ] nested'],
  ['task offsets after a lone CR', 'a\r- [ ] b'],
  ['a box alone on the line is a task', '- [ ]\n- [x]'],
  ['not tasks: no space, other letters, missing bracket', '- [ ]x\n- [y] no\n- [ x\n- [] empty\n-[ ] no space'],
  ['a task box later in the text is plain', '- see [ ] here'],
  ['nested tasks', '- [ ] parent\n  - [x] child\n    - [ ] grandchild'],
  ['a task with formatting', '- [x] **done** and `code`'],
  ['indented tasks with tabs', '\t- [ ] tabbed'],
  // ---- quotes
  ['a quote', '> one\n> two'],
  ['the space after > is optional', '>tight\n>  two spaces'],
  ['consecutive > lines are one quote; a blank line ends it', '> a\n> b\n\n> c'],
  ['a quote with a list and a task inside', '> Notes:\n> - one\n> - [ ] two\n>   - nested'],
  ['a quote inside a quote', '> outer\n> > inner\n> back'],
  ['a quote with a heading and code', '> # Title\n> ```\n> code\n> ```'],
  ['an unclosed fence in a quote ends with the quote', '> ```\n> code\nafter'],
  ['a quote interrupts a paragraph and a line without > ends it', 'para\n> quoted\nnot quoted'],
  ['quotes nest at most 8 deep; deeper > is text', '>>>>>>>>>> ten'],
  ['an empty quote line keeps the quote going', '> a\n>\n> b'],
  // ---- code blocks
  ['a code block with a language word', '```ts\nconst a = *b*;\n  indented\n```'],
  ['a tilde fence', '~~~\n# not a heading\n~~~'],
  ['a longer fence closes only on as many', '````\n```\nstill code\n````'],
  ['the other fence character does not close', '```\n~~~\n```'],
  ['an unclosed fence runs to the end', '```\nopen\n\nstill'],
  ['an empty code block', '```\n```'],
  ['blank lines in code are kept', '```\na\n\n\nb\n```'],
  ['a fence closer may have trailing spaces, not text', '```\nx\n``` y\n```  '],
  ['a backtick fence whose word has a backtick is inline code', '``` a ``` b'],
  ['code keeps CRLF lines as plain lines', '```\r\nline1\r\nline2\r\n```'],
  ['code interrupts a paragraph', 'text\n```\ncode\n```'],
  // ---- inline: emphasis
  ['bold, italic, both and struck', '**bold** __bold__ *italic* _italic_ ***both*** ~~struck~~'],
  ['nested emphasis', '*a **b** c* and **a *b* c**'],
  ['delimiters need a non-space inside', 'a * not * and ** not ** and a * b'],
  ['snake_case stays plain', 'snake_case_name and __init__ and a_b_c'],
  ['_ at word edges works', '_ok_ (_ok_) x_no_ _no_x'],
  ['* works inside words', 'un*frigging*believable and 2*3*4'],
  ['unmatched delimiters are plain', '**open and *also'],
  ['runs split to match what they can', '**open and *also and close** later*'],
  ['uneven runs leave the rest as text', '**a* and *a** and ***a*'],
  ['a single ~ or three are plain', '~a~ and ~~~b~~~'],
  ['struck with bold inside', '~~**both**~~'],
  ['emphasis does not cross lines', '*one\ntwo*'],
  ['inline formatting nests at most 8 deep', '*1 _2 ~~3 **4 *5 _6 ~~7 **8 *9* 8** 7~~ 6_ 5* 4** 3~~ 2_ 1*'],
  ['a long run of * is plain', `a${'*'.repeat(40)}b`],
  // ---- inline: code
  ['code spans', '`code` and ``with ` inside`` and ` padded `'],
  ['no formatting inside code', '`**not bold**` and `\\*`'],
  ['an unmatched backtick run is plain', '``no close` here'],
  ['code of only spaces keeps them', '`  `'],
  ['code takes the first closer of the same length', '`a``b` c`'],
  // ---- inline: links
  ['a link shows its text', 'see [the docs](https://example.com/docs) now'],
  ['link text can be formatted', '[**bold** link](u)'],
  ['a link url is trimmed and ends at the first )', '[a]( spaced url ) and [b](x(y)z)'],
  ['not links: no url, space before (, no )', '[a] and [b] (c) and [d](e'],
  ['an empty link and an empty url', '[](x) and [y]()'],
  ['no link inside a link', '[a [b](c) d](e)'],
  ['brackets inside link text', '[[a]](b)'],
  ['<https://…> and bare urls are plain text', '<https://example.com> and https://example.com/a_b_c'],
  ['code inside a link text, and ] in code', '[`a]`](b)'],
  ['emphasis around a link', '**[a](b)** and *[c*](d)'],
  // ---- inline: escapes
  ['escapes show the character', '\\*not italic\\* \\_ \\# \\[x\\](y) \\` \\\\'],
  ['a backslash before other characters stays', '\\a \\é \\ end\\'],
  ['an escaped marker is not a list or heading', '\\- not a list\n\\# not a heading\n1\\. not numbered'],
  // ---- mixed
  [
    'a note',
    '# Site visit\n\nMet **Sam** at the gate. Bring:\n- [x] keys\n- [ ] the *new* drawings\n  - A1 and A2\n\n> Call before 9\n\n---\n1. Rebar\n2. Ties\n\n```\nlength = 12.5\n```\n[plan](https://example.com/plan) `v2`',
  ],
  ['non-ASCII text and _ beside it', 'café_au_lait and _résumé_ and 日本_語'],
];

function format(cases: { name: string; text: string; blocks: Block[] }[]): string {
  const one = (c: (typeof cases)[number]) =>
    `  {\n    "name": ${JSON.stringify(c.name)},\n    "text": ${JSON.stringify(c.text)},\n    "blocks": ${JSON.stringify(c.blocks)}\n  }`;
  return `[\n${cases.map(one).join(',\n')}\n]\n`;
}

describe('markdown vectors', () => {
  const vectors = CASES.map(([name, text]) => ({ name, text, blocks: parse(text) }));

  it('cover every rule', () => {
    expect(vectors.length).toBeGreaterThanOrEqual(60);
    expect(new Set(vectors.map((v) => v.name)).size).toBe(vectors.length);
  });

  it("point every task's at between its [ and ]", () => {
    let tasks = 0;
    const visit = (blocks: Block[], text: string) => {
      for (const b of blocks) {
        const items = 'ul' in b ? b.ul : 'ol' in b ? b.ol : [];
        if ('quote' in b) visit(b.quote, text);
        for (const it of items) {
          if (it.task !== null) {
            tasks++;
            expect(text[it.at! - 1]).toBe('[');
            expect(text[it.at! + 1]).toBe(']');
            expect(it.task ? ['x', 'X'] : [' ']).toContain(text[it.at!]);
          } else {
            expect(it.at).toBeNull();
          }
          visit(it.sub, text);
        }
      }
    };
    for (const v of vectors) visit(v.blocks, v.text);
    expect(tasks).toBeGreaterThan(20);
  });

  it('are up to date with src/model/markdown.ts', async () => {
    await expect(format(vectors)).toMatchFileSnapshot(VECTORS);
  });
});

const p = (...c: Inline[]): Block => ({ p: c });
const item = (c: Inline[], sub: Block[] = [], task: boolean | null = null, at: number | null = null) => ({ c, task, at, sub });
const one = (text: string) => parse(text)[0];

describe('markdown rules', () => {
  it('headings', () => {
    expect(parse('# A\n## B\n### C\n###### D')).toEqual([
      { h: 1, c: ['A'] },
      { h: 2, c: ['B'] },
      { h: 3, c: ['C'] },
      { h: 3, c: ['D'] },
    ]);
    expect(one('#tag')).toEqual(p('#tag'));
    expect(one('# Title ##')).toEqual({ h: 1, c: ['Title'] });
    expect(one('# C#')).toEqual({ h: 1, c: ['C#'] });
  });

  it('paragraphs keep their line breaks', () => {
    expect(parse('a\nb\n\n\nc')).toEqual([p('a', { br: 1 }, 'b'), p('c')]);
  });

  it('lists, nesting and continuation', () => {
    expect(parse('- a\n  - b\n- c')).toEqual([{ ul: [item(['a'], [{ ul: [item(['b'])] }]), item(['c'])] }]);
    expect(parse('3. a\n4) b')).toEqual([{ ol: [item(['a']), item(['b'])], start: 3 }]);
    expect(parse('- a\n  more')).toEqual([{ ul: [item(['a', { br: 1 }, 'more'])] }]);
    expect(parse('- a\nnot')).toEqual([{ ul: [item(['a'])] }, p('not')]);
    expect(parse('- a\n\n- b')).toEqual([{ ul: [item(['a']), item(['b'])] }]);
    expect(parse('- a\n1. b')).toEqual([{ ul: [item(['a'])] }, { ol: [item(['b'])], start: 1 }]);
  });

  it('lists nest 8 deep at most', () => {
    const text = Array.from({ length: 12 }, (_, i) => `${'  '.repeat(i)}- ${i}`).join('\n');
    let depth = 0;
    let blocks = parse(text);
    while (blocks.length) {
      depth++;
      const list = blocks[0] as { ul: { sub: Block[] }[] };
      blocks = list.ul[list.ul.length - 1]!.sub;
    }
    expect(depth).toBe(8);
  });

  it('tasks, with offsets into the original text', () => {
    const text = 'x\r\n- [ ] a\r\n  - [X] b';
    const blocks = parse(text);
    expect(blocks).toEqual([p('x'), { ul: [item(['a'], [{ ul: [item(['b'], [], true, 17)] }], false, 6)] }]);
    expect(text[6]).toBe(' ');
    expect(text[17]).toBe('X');
    expect(text[16]).toBe('[');
    // In a quote too.
    const quoted = '> - [x] q';
    expect(parse(quoted)).toEqual([{ quote: [{ ul: [item(['q'], [], true, 5)] }] }]);
    expect(quoted[5]).toBe('x');
  });

  it('quotes hold blocks', () => {
    expect(parse('> a\n> - b')).toEqual([{ quote: [p('a'), { ul: [item(['b'])] }] }]);
    expect(parse('> a\n\n> b')).toEqual([{ quote: [p('a')] }, { quote: [p('b')] }]);
  });

  it('code blocks', () => {
    expect(parse('```js\n*a*\n\n b\n```')).toEqual([{ code: '*a*\n\n b' }]);
    expect(parse('~~~\nx')).toEqual([{ code: 'x' }]);
    expect(parse('```\n```')).toEqual([{ code: '' }]);
  });

  it('rules', () => {
    expect(parse('a\n---\n* * *')).toEqual([p('a'), { hr: 1 }, { hr: 1 }]);
  });

  it('emphasis', () => {
    expect(one('**a** *b* ***c*** ~~d~~ __e__ _f_')).toEqual(
      p({ b: ['a'] }, ' ', { i: ['b'] }, ' ', { i: [{ b: ['c'] }] }, ' ', { s: ['d'] }, ' ', { b: ['e'] }, ' ', { i: ['f'] }),
    );
    expect(one('snake_case_name')).toEqual(p('snake_case_name'));
    expect(one('a * b *')).toEqual(p('a * b *'));
    expect(one('**a')).toEqual(p('**a'));
    expect(one('**a*')).toEqual(p('*', { i: ['a'] }));
  });

  it('inline nesting stops at 8', () => {
    const open = ['*', '_', '~~', '**'];
    let text = 'x';
    for (let i = 0; i < 12; i++) {
      const d = open[i % 4]!;
      text = `${d}a ${text} a${d}`;
    }
    let depth = 0;
    const walk = (xs: Inline[], d: number) => {
      depth = Math.max(depth, d);
      for (const x of xs) {
        if (typeof x === 'string') continue;
        for (const v of Object.values(x)) if (Array.isArray(v)) walk(v as Inline[], d + 1);
      }
    };
    walk((one(text) as { p: Inline[] }).p, 0);
    expect(depth).toBe(8);
  });

  it('code spans and escapes', () => {
    expect(one('`**a**` \\*b\\*')).toEqual(p({ code: '**a**' }, ' *b*'));
    expect(one('``a`b``')).toEqual(p({ code: 'a`b' }));
    expect(one('`a')).toEqual(p('`a'));
    expect(one('\\q')).toEqual(p('\\q'));
  });

  it('links show text, keep the url as text', () => {
    expect(one('[a *b*](http://x)')).toEqual(p({ link: ['a ', { i: ['b'] }], url: 'http://x' }));
    expect(one('[a] (b)')).toEqual(p('[a] (b)'));
    expect(one('[a [b](c)](d)')).toEqual(p('[a ', { link: ['b'], url: 'c' }, '](d)'));
    expect(one('<https://x.y>')).toEqual(p('<https://x.y>'));
  });

  it('says which line each shown line came from, when asked', () => {
    const text = '# H\n\npara\nline 2\n- a\n  more\n> q\n```\nc\n```\n---';
    expect(parse(text, { lines: true })).toEqual([
      { h: 1, c: ['H'], ln: 0 },
      { p: ['para', { br: 1 }, 'line 2'], lns: [2, 3] },
      { ul: [{ ...item(['a', { br: 1 }, 'more']), lns: [4, 5] }] },
      { quote: [{ p: ['q'], lns: [6] }] },
      { code: 'c', ln: 7 },
      { hr: 1, ln: 10 },
    ]);
    expect(lineStarts('a\r\nb\rc\nd')).toEqual([0, 3, 5, 7]);
  });
});

describe('ticking a checklist box', () => {
  it('changes exactly that one character', () => {
    const text = 'a\n- [ ] one\n- [X] two';
    expect(setTask(text, 5, true)).toBe('a\n- [x] one\n- [X] two');
    expect(setTask(text, 15, false)).toBe('a\n- [ ] one\n- [ ] two');
    // Already so: unchanged.
    expect(setTask(text, 5, false)).toBe(text);
    expect(setTask(text, 15, true)).toBe(text);
  });

  it('refuses an offset that is no longer a box', () => {
    expect(setTask('- [ ] a', 2, true)).toBeNull();
    expect(setTask('- [y] a', 3, true)).toBeNull();
    expect(setTask('[ ]', 1.5, true)).toBeNull();
    expect(setTask('[ ]', -1, true)).toBeNull();
    expect(setTask('[ ', 1, true)).toBeNull();
    expect(setTask('', 0, true)).toBeNull();
  });

  it('every task in the shared cases ticks and unticks in place', () => {
    for (const [, text] of CASES) {
      const visit = (blocks: Block[]) => {
        for (const b of blocks) {
          if ('quote' in b) visit(b.quote);
          for (const it of 'ul' in b ? b.ul : 'ol' in b ? b.ol : []) {
            if (it.at !== null) {
              const flipped = setTask(text, it.at, !it.task)!;
              expect(flipped.length).toBe(text.length);
              expect(parse(flipped).length).toBe(parse(text).length);
            }
            visit(it.sub);
          }
        }
      };
      visit(parse(text));
    }
  });
});

describe('the formatted view builds no markup from text', () => {
  it('uses no innerHTML, insertAdjacentHTML, outerHTML, document.write, eval or <a>', () => {
    for (const file of ['../../src/ui/markdownview.ts', '../../src/ui/notes.ts', '../../src/model/markdown.ts']) {
      const source = readFileSync(fileURLToPath(new URL(file, import.meta.url)), 'utf8');
      expect(source, file).not.toMatch(/innerHTML|outerHTML|insertAdjacentHTML|document\.write|\beval\(|new Function|createElement\('a'\)|\bhref\b/);
    }
  });
});
