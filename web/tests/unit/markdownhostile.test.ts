import { describe, expect, it } from 'vitest';
import { canFormat, MAX_FORMATTED, MAX_LIST_DEPTH, MAX_QUOTE_DEPTH, parse, type Block, type Inline } from '../../src/model/markdown';
import { MAX_NOTE_TEXT } from '../../src/model/limits';

/**
 * Notes that would stall a naive Markdown reader. A note can come from any device in the sync
 * folder or an import, so whatever it holds, showing it must stay quick (linear in its length)
 * and shallow (nesting is capped, so neither the parser nor the page recurses without bound).
 * Tack for Windows runs the same cases (MarkdownTests.cs).
 */

/** Milliseconds for the fastest of a few runs (the first warms the JIT). */
function time(fn: () => void, runs = 3): number {
  let best = Infinity;
  for (let i = 0; i < runs; i++) {
    const t = performance.now();
    fn();
    best = Math.min(best, performance.now() - t);
  }
  return best;
}

/** Plain text of the same length: what a hostile note may cost, give or take. */
function baseline(length: number): number {
  const text = 'abcdefghij klmnopqrs '.repeat(Math.ceil(length / 21)).slice(0, length);
  return time(() => parse(text));
}

/** No worse than 10x plain text of the same length (and never over 250 ms, the floor for slow CI). */
function expectLinear(text: string): void {
  const plain = baseline(text.length);
  const took = time(() => parse(text));
  expect(took, `${took.toFixed(1)} ms against ${plain.toFixed(1)} ms for plain text`).toBeLessThan(Math.max(250, plain * 10));
}

function depthOf(blocks: Block[]): { blocks: number; inline: number } {
  let blockDepth = 0;
  let inlineDepth = 0;
  const inl = (xs: Inline[], d: number) => {
    inlineDepth = Math.max(inlineDepth, d);
    for (const x of xs) {
      if (typeof x === 'string') continue;
      if ('b' in x) inl(x.b, d + 1);
      else if ('i' in x) inl(x.i, d + 1);
      else if ('s' in x) inl(x.s, d + 1);
      else if ('link' in x) inl(x.link, d + 1);
    }
  };
  const blk = (bs: Block[], d: number) => {
    blockDepth = Math.max(blockDepth, d);
    for (const b of bs) {
      if ('quote' in b) blk(b.quote, d + 1);
      else if ('ul' in b || 'ol' in b) {
        for (const it of 'ul' in b ? b.ul : b.ol) {
          inl(it.c, 0);
          blk(it.sub, d + 1);
        }
      } else if ('p' in b) inl(b.p, 0);
      else if ('h' in b) inl(b.c, 0);
    }
  };
  blk(blocks, 0);
  return { blocks: blockDepth, inline: inlineDepth };
}

describe('Markdown: hostile notes', () => {
  it('100,000 * cost no more than 100,000 letters', () => {
    const run = `a${'*'.repeat(100_000)}b`;
    expectLinear(run);
    expect(parse(run)).toEqual([{ p: [run] }]);
    expectLinear('*a'.repeat(50_000));
    expectLinear('**a '.repeat(25_000));
    expectLinear('a* '.repeat(33_000));
    expectLinear('*a _b ~~c '.repeat(10_000));
    expectLinear('_a'.repeat(50_000));
  });

  it('100,000 [ (and brackets that never close, links that never end)', () => {
    expectLinear('['.repeat(100_000));
    expectLinear(']'.repeat(100_000));
    expectLinear('[a]('.repeat(25_000));
    expectLinear('[a](b)'.repeat(16_000));
    expectLinear(`${'['.repeat(50_000)}a${'](b)'.repeat(12_000)}`);
    expectLinear('[*'.repeat(50_000));
  });

  it('40,000 [ around formatting 8 deep: a link over the depth cap fails once, not once per bracket around it', () => {
    const n = 39_993;
    const text = `${'['.repeat(n)}${'*'.repeat(16)}x${'*'.repeat(16)}${'](x)'.repeat(n)}`;
    expect(text.length).toBe(199_998);
    expectLinear(text);
    expect(depthOf(parse(text)).inline).toBeLessThanOrEqual(8);
  });

  it('closers whose floor was matched away later still stop there (a_ ~~** _* 20,000 times)', () => {
    expectLinear('a_ ~~** _*'.repeat(20_000));
    expectLinear('a* ~~__ *_'.repeat(20_000));
  });

  it('backtick runs of every length, none closing', () => {
    let text = '';
    for (let n = 1; text.length < 100_000; n++) text += `${'`'.repeat(n)}a`;
    expectLinear(text);
    expectLinear('`a'.repeat(50_000));
    expectLinear('``a`'.repeat(25_000));
  });

  it('10,000 nested > stop at the quote depth cap', () => {
    const line = `${'>'.repeat(10_000)} deep`;
    expectLinear(line);
    expect(depthOf(parse(line)).blocks).toBe(MAX_QUOTE_DEPTH);
    const spaced = '> '.repeat(10_000) + 'deep';
    expectLinear(spaced);
    expect(depthOf(parse(spaced)).blocks).toBe(MAX_QUOTE_DEPTH);
    const lines = Array.from({ length: 10_000 }, () => '>>>>>>>>>>>>>>>> x').join('\n');
    expectLinear(lines);
  });

  it('list indents a thousand levels deep (and one 20,000 spaces in) stop at the list depth cap', () => {
    // Each line one level deeper (2 more spaces), as far as a 1 MB note allows, then a long one.
    const lines: string[] = [];
    let length = 0;
    for (let i = 0; length < MAX_NOTE_TEXT - 30_000; i++) {
      const line = `${' '.repeat(2 * i)}- ${i}`;
      lines.push(line);
      length += line.length + 1;
    }
    lines.push(`${' '.repeat(20_000)}- [ ] ten thousand in`);
    const text = lines.join('\n');
    expect(text.length).toBeLessThanOrEqual(MAX_NOTE_TEXT);
    expectLinear(text);
    expect(depthOf(parse(text)).blocks).toBe(MAX_LIST_DEPTH);
    // 1,000 items each a level deeper by a tab.
    const tabs = Array.from({ length: 1_000 }, (_, i) => `${'\t'.repeat(i)}- t`).join('\n');
    expect(depthOf(parse(tabs)).blocks).toBe(MAX_LIST_DEPTH);
  });

  it('nested inline formatting stops at 8', () => {
    const text = `${'*a _b ~~c **d '.repeat(5_000)}${' d** c~~ b_ a*'.repeat(5_000)}`;
    expectLinear(text);
    expect(depthOf(parse(text)).inline).toBeLessThanOrEqual(8);
  });

  it('a 1 MB note parses quickly, but is never shown formatted (over 200,000 characters)', () => {
    const unit = '# Head *x*\n- [ ] task **b**\n  - nested _i_\n> quote `c`\n```\ncode\n```\n[l](u) ~~s~~ snake_case\n\n';
    const text = unit.repeat(Math.floor(MAX_NOTE_TEXT / unit.length));
    expect(text.length).toBeGreaterThan(900_000);
    expectLinear(text);
    expect(canFormat(text)).toBe(false);
    expect(canFormat('x'.repeat(MAX_FORMATTED))).toBe(true);
    expect(canFormat('x'.repeat(MAX_FORMATTED + 1))).toBe(false);
    expect(canFormat('')).toBe(false);
  });

  it('a million line breaks', () => {
    expectLinear('\n'.repeat(1_000_000));
    expectLinear('a\n'.repeat(500_000));
    expectLinear('\r'.repeat(1_000_000));
    expectLinear('- a\n  b\n'.repeat(100_000));
  });

  it('lone surrogates are kept as they are, and offsets still count UTF-16 units', () => {
    const hi = String.fromCharCode(0xd800);
    const lo = String.fromCharCode(0xdc00);
    const text = `${hi}*a${lo}*\n- [ ] ${lo}${hi}\n${lo}_x_${hi}`;
    const blocks = parse(text);
    expect(blocks).toEqual([
      { p: [hi, { i: [`a${lo}`] }] },
      { ul: [{ c: [`${lo}${hi}`], task: false, at: 9, sub: [] }] },
      { p: [`${lo}_x_${hi}`] },
    ]);
    expect(text[9]).toBe(' ');
    expectLinear(`${hi}*`.repeat(50_000));
  });
});
