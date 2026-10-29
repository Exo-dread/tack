using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tack.Model;

namespace Tack.Tests;

/// <summary>
/// The Markdown a note reads as (docs/NOTES-MARKDOWN.md). The cases in tests/vectors/markdown.json are Tack Focus's
/// too (web/src/model/markdown.ts): both apps read every note the same way.
/// </summary>
internal static class MarkdownTests
{
    public static IEnumerable<(string Name, Action Run)> All()
    {
        yield return ("markdown: the spec's own cases read as the tree it gives", SpecCases);
        yield return ("markdown, shared cases: every case as Tack Focus reads it", SharedCases);
        yield return ("markdown: blocks, items and line breaks know where their line starts, CR LF or not", LineOffsets);
        yield return ("markdown: a checkbox's place is the character between its brackets, in the text as typed", TaskOffsets);
    }

    // ---- The tree as the shared cases write it ----

    public static JsonArray ToJson(IReadOnlyList<MdBlock> blocks)
    {
        var array = new JsonArray();
        foreach (var b in blocks) array.Add(Block(b));
        return array;
    }

    private static JsonNode Block(MdBlock b) => b switch
    {
        MdHeading h => new JsonObject { ["h"] = h.Level, ["c"] = Inlines(h.Content) },
        MdParagraph p => new JsonObject { ["p"] = Inlines(p.Content) },
        MdList { Ordered: false } l => new JsonObject { ["ul"] = Items(l.Items) },
        MdList l => new JsonObject { ["ol"] = Items(l.Items), ["start"] = l.Start },
        MdQuote q => new JsonObject { ["quote"] = ToJson(q.Blocks) },
        MdCodeBlock c => new JsonObject { ["code"] = c.Text },
        MdRule => new JsonObject { ["hr"] = 1 },
        _ => throw new InvalidOperationException(b.GetType().Name),
    };

    private static JsonArray Items(IReadOnlyList<MdItem> items)
    {
        var array = new JsonArray();
        foreach (var it in items)
        {
            array.Add(new JsonObject
            {
                ["c"] = Inlines(it.Content),
                ["task"] = it.Task is { } t ? JsonValue.Create(t) : null,
                ["at"] = it.At is { } at ? JsonValue.Create(at) : null,
                ["sub"] = ToJson(it.Sub),
            });
        }
        return array;
    }

    private static JsonArray Inlines(IReadOnlyList<MdInline> inlines)
    {
        var array = new JsonArray();
        foreach (var i in inlines)
        {
            array.Add(i switch
            {
                MdText t => JsonValue.Create(t.Text),
                MdStyle { Kind: MdStyleKind.Bold } s => new JsonObject { ["b"] = Inlines(s.Content) },
                MdStyle { Kind: MdStyleKind.Italic } s => new JsonObject { ["i"] = Inlines(s.Content) },
                MdStyle s => new JsonObject { ["s"] = Inlines(s.Content) },
                MdCode c => new JsonObject { ["code"] = c.Text },
                MdLink l => new JsonObject { ["link"] = Inlines(l.Content), ["url"] = l.Url },
                MdBreak => new JsonObject { ["br"] = 1 },
                _ => throw new InvalidOperationException(i.GetType().Name),
            });
        }
        return array;
    }

    private static readonly JsonSerializerOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Show(JsonNode? node) => node?.ToJsonString(Compact) ?? "null";

    private static void Expect(string name, string text, string expected)
    {
        var actual = ToJson(Markdown.Parse(text));
        var want = JsonNode.Parse(expected);
        if (!JsonNode.DeepEquals(want, actual))
            throw new TestFailure($"{name}: {JsonSerializer.Serialize(text)}\n  expected: {Show(want)}\n  actual:   {Show(actual)}");
    }

    // ---- Cases from the spec ----

    private static void SpecCases()
    {
        Expect("empty", "", "[]");
        Expect("blank lines", "\n  \n\t\n", "[]");
        Expect("paragraph", "Hello", """[{"p":["Hello"]}]""");
        Expect("line breaks kept", "one\ntwo\r\nthree\rfour", """[{"p":["one",{"br":1},"two",{"br":1},"three",{"br":1},"four"]}]""");
        Expect("blank lines separate", "one\n\n\n\ntwo", """[{"p":["one"]},{"p":["two"]}]""");

        Expect("h1", "# Title", """[{"h":1,"c":["Title"]}]""");
        Expect("h2", "## Title", """[{"h":2,"c":["Title"]}]""");
        Expect("h3", "### Title", """[{"h":3,"c":["Title"]}]""");
        Expect("h4 to h6 are 3", "#### a\n##### b\n###### c", """[{"h":3,"c":["a"]},{"h":3,"c":["b"]},{"h":3,"c":["c"]}]""");
        Expect("seven is text", "####### a", """[{"p":["####### a"]}]""");
        Expect("#tag is text", "#tag", """[{"p":["#tag"]}]""");
        Expect("closing hashes dropped", "## Title ##", """[{"h":2,"c":["Title"]}]""");
        Expect("heading inline", "# A **b**", """[{"h":1,"c":["A ",{"b":["b"]}]}]""");

        Expect("bullets", "- a\n* b\n+ c", """[{"ul":[{"c":["a"],"task":null,"at":null,"sub":[]},{"c":["b"],"task":null,"at":null,"sub":[]},{"c":["c"],"task":null,"at":null,"sub":[]}]}]""");
        Expect("numbered", "3. a\n4) b", """[{"ol":[{"c":["a"],"task":null,"at":null,"sub":[]},{"c":["b"],"task":null,"at":null,"sub":[]}],"start":3}]""");
        Expect("ten digits is text", "1234567890. a", """[{"p":["1234567890. a"]}]""");
        Expect("bullet then numbered: two lists", "- a\n1. b", """[{"ul":[{"c":["a"],"task":null,"at":null,"sub":[]}]},{"ol":[{"c":["b"],"task":null,"at":null,"sub":[]}],"start":1}]""");
        Expect("nested", "- a\n  - b\n- c", """[{"ul":[{"c":["a"],"task":null,"at":null,"sub":[{"ul":[{"c":["b"],"task":null,"at":null,"sub":[]}]}]},{"c":["c"],"task":null,"at":null,"sub":[]}]}]""");
        Expect("continuation", "- a\n  more", """[{"ul":[{"c":["a",{"br":1},"more"],"task":null,"at":null,"sub":[]}]}]""");
        Expect("blank ends nothing", "- a\n\n- b", """[{"ul":[{"c":["a"],"task":null,"at":null,"sub":[]},{"c":["b"],"task":null,"at":null,"sub":[]}]}]""");
        Expect("non-indented after blank ends the list", "- a\n\nb", """[{"ul":[{"c":["a"],"task":null,"at":null,"sub":[]}]},{"p":["b"]}]""");

        Expect("tasks", "- [ ] a\n- [x] b\n* [X] c\n1. [ ] d", """
            [{"ul":[{"c":["a"],"task":false,"at":3,"sub":[]},{"c":["b"],"task":true,"at":11,"sub":[]},{"c":["c"],"task":true,"at":19,"sub":[]}]},
             {"ol":[{"c":["d"],"task":false,"at":28,"sub":[]}],"start":1}]
            """);

        Expect("quote", "> a\n> b", """[{"quote":[{"p":["a",{"br":1},"b"]}]}]""");
        Expect("quote with list", ">- a\n> - b", """[{"quote":[{"ul":[{"c":["a"],"task":null,"at":null,"sub":[]},{"c":["b"],"task":null,"at":null,"sub":[]}]}]}]""");

        Expect("code", "```js\nlet *a* = 1;\n  b\n```", """[{"code":"let *a* = 1;\n  b"}]""");
        Expect("code to the end", "~~~\na\n```", """[{"code":"a\n```"}]""");
        Expect("code closed by more", "```\na\n`````\nb", """[{"code":"a"},{"p":["b"]}]""");

        Expect("rules", "---\n***\n_ _ _", """[{"hr":1},{"hr":1},{"hr":1}]""");
        Expect("--- under a paragraph is a rule", "text\n---", """[{"p":["text"]},{"hr":1}]""");

        Expect("code span", "a `b *c*` d", """[{"p":["a ",{"code":"b *c*"}," d"]}]""");
        Expect("code span runs", "``a`b``", """[{"p":[{"code":"a`b"}]}]""");
        Expect("bold", "**a** __b__", """[{"p":[{"b":["a"]}," ",{"b":["b"]}]}]""");
        Expect("italic", "*a* _b_", """[{"p":[{"i":["a"]}," ",{"i":["b"]}]}]""");
        Expect("both", "***a***", """[{"p":[{"i":[{"b":["a"]}]}]}]""");
        Expect("struck", "~~a~~", """[{"p":[{"s":["a"]}]}]""");
        Expect("no opening before a space", "a * b *", """[{"p":["a * b *"]}]""");
        Expect("snake_case", "snake_case_name", """[{"p":["snake_case_name"]}]""");
        Expect("unmatched", "**a", """[{"p":["**a"]}]""");
        Expect("link", "[text](https://example.com)", """[{"p":[{"link":["text"],"url":"https://example.com"}]}]""");
        Expect("autolinks are text", "<https://a.b> https://c.d", """[{"p":["<https://a.b> https://c.d"]}]""");
        Expect("escapes", @"\*a\* \# \\", """[{"p":["*a* # \\"]}]""");

        // Nesting: eight deep, the ninth (outermost: matched last) plain.
        string nine = string.Join(" ", Enumerable.Range(1, 9).Select(k => (k % 2 == 1 ? "*" : "_") + k)) + " x "
            + string.Join(" ", Enumerable.Range(1, 9).Reverse().Select(k => k + (k % 2 == 1 ? "*" : "_")));
        var tree = Markdown.Parse(nine);
        int depth = 0;
        IReadOnlyList<MdInline> level = ((MdParagraph)tree[0]).Content;
        while (level.Count > 0 && level.OfType<MdStyle>().FirstOrDefault() is { } style)
        {
            depth++;
            level = style.Content;
        }
        Check.Equal(8, depth, "inline formatting nests 8 deep: " + Show(ToJson(tree)));
    }

    private static void SharedCases()
    {
        string path = Path.Combine(Program.Root, "tests", "vectors", "markdown.json");
        if (!File.Exists(path)) throw new TestFailure("tests/vectors/markdown.json is missing");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var cases = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement : doc.RootElement.GetProperty("cases");
        int n = 0;
        var failures = new List<string>();
        foreach (var c in cases.EnumerateArray())
        {
            string name = c.GetProperty("name").GetString()!, text = c.GetProperty("text").GetString()!;
            var want = JsonNode.Parse(c.GetProperty("blocks").GetRawText());
            var actual = ToJson(Markdown.Parse(text));
            if (!JsonNode.DeepEquals(want, actual))
                failures.Add($"{name}: {JsonSerializer.Serialize(text)}\n  expected: {Show(want)}\n  actual:   {Show(actual)}");
            n++;
        }
        if (failures.Count > 0) throw new TestFailure($"{failures.Count} of {n} cases differ\n" + string.Join("\n", failures));
        Check.True(n > 20, $"the cases were read ({n})");
    }

    private static void LineOffsets()
    {
        foreach (string nl in new[] { "\n", "\r\n" })
        {
            string text = string.Join(nl, "# Head", "", "para one", "para two", "- item", "  more", "> quote", "```", "code", "```", "---");
            int Line(int k)
            {
                int at = 0;
                for (int i = 0; i < k; i++) at = text.IndexOf(nl, at, StringComparison.Ordinal) + nl.Length;
                return at;
            }
            var blocks = Markdown.Parse(text);
            Check.Equal(Line(0), blocks[0].Line, "heading");
            var para = (MdParagraph)blocks[1];
            Check.Equal(Line(2), para.Line, "paragraph");
            Check.Equal(Line(3), para.Content.OfType<MdBreak>().Single().Line, "its line break: where the next line starts");
            var list = (MdList)blocks[2];
            Check.Equal(Line(4), list.Items[0].Line, "item");
            Check.Equal(Line(5), list.Items[0].Content.OfType<MdBreak>().Single().Line, "item's continuation");
            Check.Equal(Line(6), blocks[3].Line, "quote");
            var code = (MdCodeBlock)blocks[4];
            Check.Equal(Line(7), code.Line, "code fence");
            Check.Equal(Line(8), code.Lines[0], "code line");
            Check.Equal(Line(10), blocks[5].Line, "rule");
        }
    }

    private static void TaskOffsets()
    {
        string text = "intro\r\n> - [x] done\r\n  - [ ] todo";
        var quote = (MdQuote)Markdown.Parse(text)[1];
        var item = ((MdList)quote.Blocks[0]).Items[0];
        Check.Equal(true, item.Task, "ticked");
        Check.Equal('x', text[item.At!.Value], "its place in a quote, after CR LF");
    }
}
