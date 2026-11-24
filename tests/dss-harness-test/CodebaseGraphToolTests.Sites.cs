using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;
using DssHarness.Core.Help;

namespace DssHarness.Tests;

public sealed partial class CodebaseGraphToolTests
{
    /// <summary>What a <c>sites</c> call's data is, as the tool's schema and every usage line about it say it.</summary>
    internal const string DataDescription = "the data whose deciding places are asked: a symbol as symbol accepts one - a node's handle, its id, a path:line, or a qualified or simple name - a key path, dotted - a key whose own name holds a dot or a bracket quoted in brackets, in double or single quotes, Logging.LogLevel[\"Microsoft.AspNetCore\"] or Logging.LogLevel['Microsoft.AspNetCore'], or with its names' own dots as they are, Logging.LogLevel.Microsoft.AspNetCore, which asks of every key it may mean - or as .NET's configuration writes one (Payments:TimeoutSeconds, Payments__TimeoutSeconds, Logging:LogLevel:Microsoft.AspNetCore), or a diagnostic id, a rule's selector as a rule writes it (.btn, #main), asked by its name as a class list writes it too, or a quoted literal, asked by its value alone.";

    /// <summary>The coverage line every answer that searched a file the graph lists only ends its coverage with.</summary>
    private const string NoExtractor = "coverage: nothing is assumed of a language with no extractor; those files are read by text alone";

    /// <summary>The coverage line of the one markdown file every tree of these tests holds, its README, which the graph outlines.</summary>
    private const string OutlinedReadme = "coverage: 1 file(s) the graph outlines (.md), their values and their text held by no literal of it, searched by text too - 0 hit(s)";

    /// <summary>The coverage line of the one file every tree of these tests holds that the graph lists only, its zoo.txt, where it holds no hit.</summary>
    private const string ListedZoo = "coverage: 1 file(s) the graph lists only (.txt) searched by text - 0 hit(s)";

    /// <summary>The coverage line of an answer that searched <paramref name="word"/> and <paramref name="paths"/> key paths as an environment variable writes each.</summary>
    private static string KeyPathsSearched(string word, int paths)
        => $"coverage: '{word}' searched as a whole word, and {paths.ToString(CultureInfo.InvariantCulture)} key path(s) it names as an environment variable writes each, its places joined by '__'";

    /// <summary>
    /// <c>sites</c> answers every place that decides a name once - the card of the node at it, saying every way it decides,
    /// an edge's other than exact with how sure it is, as <c>node</c> marks a candidates edge: its definers, what calls them,
    /// what answers an interface's member for its type, and the lines the text search found in the files the graph is unsure
    /// of beneath the card of the node at each - the cards grouped by file kind and tier; then the coverage lines - the files
    /// the graph lists only, those it is unsure of, those it outlines - every limit of each language whose files it read, and
    /// no file the graph answers of said to be one the reader may not read.
    /// </summary>
    [Fact]
    public async Task Sites_OfAName_AnswerEachPlaceOnceWithEveryWayItDecides_GroupedByKindAndTier_WithTheCoverageLines()
    {
        using var tree = await TreeAsync();
        var tool = Tool(tree, await QueriesAsync(tree));

        var answer = await AskAsync(tool, """{"op":"sites","data":"Area"}""");

        // Only the files no step reads are left out, as many as the text fallback leaves out for the same tree: the files the
        // graph answers of - passed over by the search's own will - are never said to be ones the reader may not read.
        var fallback = (await AskAsync(tool, """{"op":"find","query":"Okapi"}""")).Text.Split('\n')[^1];

        Assert.Equal(GraphToolOutcome.Answered, answer.Outcome);
        Assert.Matches(@"^\[\d+ file\(s\) you may not read were left out\]$", fallback);

        // The answer of one page: no header, and its whole text as the graph holds the tree. Total is reached by three edges
        // of candidates, so its card says so; Circle.Area defines and answers IShape.Area for Circle, one card saying both.
        Assert.Equal(
            [
                "every place that decides 'Area':",
                "csharp in directory src/ (4):",
                "  bc11b2 member public static double Total(IShape[] shapes) | src/Geometry.cs:5-14 #5b03176a | 2 caller(s), 3 callee(s) | calls (candidates), a line holds it",
                "      src/Geometry.cs:10 sum += shape.Area();",
                "  dff23b member double Area() | src/Shapes.cs:6-6 #704ac59f | 1 caller(s), 0 callee(s) | defines",
                "  7bfbc3 member public double Area() | src/Shapes.cs:12-15 #2a3dc106 | 1 caller(s), 0 callee(s) | defines, implements",
                "  df67b4 member public double Area() | src/Shapes.g.cs:6-9 #f393c58e | 1 caller(s), 0 callee(s), generated | defines, a line holds it",
                "      src/Shapes.g.cs:6 public double Area()",
                ListedZoo,
                "coverage: 2 file(s) the graph is unsure of, searched by text too - 2 hit(s)",
                OutlinedReadme,
                NoExtractor,
                .. LimitsOf("csharp", [CodeGraphLimit.Candidates]),
                .. LimitsOf("markdown"),
                fallback,
            ],
            answer.Text.Split('\n'));
        Assert.Equal(1, answer.Paging!.Pages);
        Assert.Equal(4, answer.Paging.Total);
        Assert.Equal(["csharp:Demo.Circle.Area()", "csharp:Demo.Generated.Square.Area()", "csharp:Demo.IShape.Area()"], answer.Facts.Asked);
        Assert.Equal(2, answer.Facts.Hits);
    }

    /// <summary>
    /// A data that names a node by itself - its handle, its id, a <c>path:line</c> - asks of that node alone, and a name
    /// written with its scopes - qualified, with C++'s <c>::</c>, or with some of its scopes - asks of the nodes it names:
    /// each is searched for by the name code writes, so every form answers the same places.
    /// </summary>
    [Fact]
    public async Task Sites_ByAHandleAnIdAPathLineOrANameWithItsScopes_AskOfTheNodeItNames_AndSearchTheNameCodeWrites()
    {
        using var tree = await TreeAsync();
        var tool = Tool(tree, await QueriesAsync(tree));

        var byHandle = await AskAsync(tool, """{"op":"sites","data":"7bfbc3"}""");
        var lines = byHandle.Text.Split('\n');

        Assert.Equal(
            [
                "every place that decides '7bfbc3':",
                "csharp in directory src/ (3):",
                "  bc11b2 member public static double Total(IShape[] shapes) | src/Geometry.cs:5-14 #5b03176a | 2 caller(s), 3 callee(s) | calls (candidates), a line holds it",
                "      src/Geometry.cs:10 sum += shape.Area();",
                "  7bfbc3 member public double Area() | src/Shapes.cs:12-15 #2a3dc106 | 1 caller(s), 0 callee(s) | defines",
                "  df67b4 member public double Area() | src/Shapes.g.cs:6-9 #f393c58e | 1 caller(s), 0 callee(s), generated | a line holds it",
                "      src/Shapes.g.cs:6 public double Area()",
                ListedZoo,
                "coverage: 2 file(s) the graph is unsure of, searched by text too - 2 hit(s)",
                OutlinedReadme,
                NoExtractor,
            ],
            lines[..11]);
        Assert.Equal(["csharp:Demo.Circle.Area()"], byHandle.Facts.Asked);

        foreach (var data in new[] { "csharp:Demo.Circle.Area()", "src/Shapes.cs:14", "Demo.Circle.Area", "Demo::Circle::Area", "Circle.Area" })
        {
            var other = await AskAsync(tool, $$"""{"op":"sites","data":"{{data}}"}""");
            var said = other.Text.Split('\n');

            Assert.Equal($"every place that decides '{data}':", said[0]);
            Assert.Equal(lines[1..], said[1..]);
            Assert.Equal(["csharp:Demo.Circle.Area()"], other.Facts.Asked);
        }
    }

    /// <summary>
    /// A key path asks of the key it names, held and never defined, and is searched for by its last key, as data writes it:
    /// the key's file, outlined, searched by text, its line beneath the key's card.
    /// </summary>
    [Fact]
    public async Task Sites_OfAKeyPath_AnswerTheKeyItNames_AndSearchItsLastKey()
    {
        using var tree = await TreeAsync(("config/app.json", "{\n  \"Payments\": {\n    \"TimeoutSeconds\": 30\n  }\n}\n"));
        var queries = await QueriesAsync(tree);
        var tool = Tool(tree, queries);

        var answer = await AskAsync(tool, """{"op":"sites","data":"Payments.TimeoutSeconds"}""");
        var key = queries.Card("json:config/app.json#Payments.TimeoutSeconds");
        var lines = answer.Text.Split('\n');

        Assert.Equal(
            [
                "every place that decides 'Payments.TimeoutSeconds':",
                "json in directory config/ (1):",
                $"  {GraphLines.Of(key)} | holds the key, a line holds it",
                "      config/app.json:3 \"TimeoutSeconds\": 30",
                ListedZoo,
                "coverage: 2 file(s) the graph is unsure of, searched by text too - 0 hit(s)",
                "coverage: 2 file(s) the graph outlines (.json, .md), their values and their text held by no literal of it, searched by text too - 1 hit(s)",
                KeyPathsSearched("TimeoutSeconds", 1),
                NoExtractor,
                .. LimitsOf("csharp", [CodeGraphLimit.Candidates]),
                .. LimitsOf("json"),
                .. LimitsOf("markdown"),
            ],
            lines[..^1]);
        Assert.Equal([key.Id], answer.Facts.Asked);
    }

    /// <summary>
    /// A step whose reads do not reach a file is shown no place in it - a definer's caller, a literal's node, a hit - and the
    /// places left out are counted; where every place the graph holds of the data is in such a file, the answer says so. A
    /// file the graph answers of is never taken by the text search, so it is never counted among the files the reader may not
    /// read, as a file the search would have read is.
    /// </summary>
    [Fact]
    public async Task Sites_ShowNoPlaceInAFileTheStepsReadsDoNotReach_AndCountThem()
    {
        using var tree = await TreeAsync(
            ("src/Keys.cs", "namespace Demo;\n\npublic static class Keys\n{\n    public static string Of()\n    {\n        return \"Area\";\n    }\n}\n"),
            ("src/Hidden.cs", "namespace Demo;\n\npublic static class Hidden\n{\n    public static string Of()\n    {\n        return \"Hidden value\";\n    }\n}\n"));
        var tool = Tool(tree, await QueriesAsync(tree), mayRead: path => path is not ("src/Keys.cs" or "src/Geometry.cs" or "src/Hidden.cs"));

        var named = await AskAsync(tool, """{"op":"sites","data":"Area"}""");
        var quoted = (await AskAsync(tool, """{"op":"sites","data":"\"Area\""}""")).Text.Split('\n');
        var hidden = (await AskAsync(tool, """{"op":"sites","data":"'Hidden value'"}""")).Text.Split('\n');
        var fallback = (await AskAsync(tool, """{"op":"find","query":"Okapi"}""")).Text.Split('\n')[^1];

        // The text fallback takes every file, Keys.cs and Hidden.cs among them; a sites answer of a name never takes those two,
        // which the graph is sure of, and counts the rest the fallback counts - Geometry.cs, which it is unsure of, among them.
        var counted = int.Parse(fallback.Split(' ')[0][1..], CultureInfo.InvariantCulture);

        // Total, which calls the definers, stands in Geometry.cs and Keys.Of's literal in Keys.cs: both left out and counted,
        // and Geometry.cs is never searched by text.
        Assert.Equal(
            [
                "every place that decides 'Area':",
                "csharp in directory src/ (3):",
                "  dff23b member double Area() | src/Shapes.cs:6-6 #704ac59f | 1 caller(s), 0 callee(s) | defines",
                "  7bfbc3 member public double Area() | src/Shapes.cs:12-15 #2a3dc106 | 1 caller(s), 0 callee(s) | defines, implements",
                "  df67b4 member public double Area() | src/Shapes.g.cs:6-9 #f393c58e | 1 caller(s), 0 callee(s), generated | defines, a line holds it",
                "      src/Shapes.g.cs:6 public double Area()",
                ListedZoo,
                "coverage: 1 file(s) the graph is unsure of, searched by text too - 1 hit(s)",
                OutlinedReadme,
                NoExtractor,
                .. LimitsOf("csharp"),
                .. LimitsOf("markdown"),
                $"[{(counted - 2).ToString(CultureInfo.InvariantCulture)} file(s) you may not read were left out]",
                "2 place(s) left out: in files your reads do not reach",
            ],
            named.Text.Split('\n'));
        Assert.Equal(["csharp:Demo.Circle.Area()", "csharp:Demo.Generated.Square.Area()", "csharp:Demo.IShape.Area()"], named.Facts.Asked);

        // A literal a file the reads do not reach holds is no place shown, and is counted; a quoted value takes every file the
        // graph read, so the files it may not read are the fallback's.
        Assert.DoesNotContain(quoted, line => line.Contains("src/Keys.cs", StringComparison.Ordinal));
        Assert.Equal([fallback, "1 place(s) left out: in files your reads do not reach"], quoted[^2..]);

        // Where every place is in such a file, the answer says so, and what it searched by text holds none.
        Assert.Equal(
            [
                "every place that decides the literal 'Hidden value':",
                "every place the graph holds of it is in a file your reads do not reach, and no line of the files searched by text holds it",
            ],
            hidden[..2]);
        Assert.Equal("1 place(s) left out: in files your reads do not reach", hidden[^1]);
    }

    /// <summary>
    /// Every file the graph does not answer of the data is searched by text and said by why: a file it lists only, an
    /// extensionless one by its name; a file it does not hold, written since it was synced; and a file it outlines, its hit
    /// beneath the card of its section - each counted once the search read it, so a binary file the search passed over is
    /// counted nowhere. The cards are grouped by file kind and tier, a file at the tree's root in the directory '.'.
    /// </summary>
    [Fact]
    public async Task Sites_SearchEveryFileTheGraphDoesNotAnswerOf_AndSayEachByWhy()
    {
        using var tree = await TreeAsync(
            ("Makefile", "all:\n\techo Okapi\n"),
            ("docs/guide.md", "# Guide\n\nAn Okapi lives here.\n"),
            ("docs/blob.bin", "\0\0\0Okapi\n"));
        var queries = await QueriesAsync(tree);
        tree.Write("docs/new.txt", "A new Okapi arrived.\n");
        var tool = Tool(tree, queries, sync: false);

        var answer = (await AskAsync(tool, """{"op":"sites","data":"Okapi"}""")).Text.Split('\n');
        var guide = queries.AtLine("docs/guide.md", 3)!;

        // The text fallback's notes of the same tree, from a find no node's name, summary or path matches: the guide's section
        // holds Okapi in its summary, so a find of Okapi is answered by the graph and searches nothing.
        var fallback = (await AskAsync(tool, """{"op":"find","query":"Quagga"}""")).Text.Split('\n');

        Assert.Equal(["no node matched", "text: no line holds 'Quagga'"], fallback[..2]);
        Assert.Matches(@"^\[\d+ file\(s\) you may not read were left out\]$", fallback[^2]);

        Assert.Equal(
            [
                "every place that decides 'Okapi':",
                ".txt in directory docs/ (2):",
                "  docs/new.txt:1 A new Okapi arrived.",
                "  docs/zoo.txt:1 An Okapi grazes here.",
                "Makefile in directory . (1):",
                "  Makefile:2 echo Okapi",
                "markdown in directory docs/ (1):",
                $"  {GraphLines.Of(guide)} | a line holds it",
                "      docs/guide.md:3 An Okapi lives here.",
                "coverage: 2 file(s) the graph lists only (.txt, Makefile) searched by text - 2 hit(s)",
                "coverage: 1 file(s) the graph does not hold, written since its last sync or kept out of it (.txt), searched by text - 1 hit(s)",
                "coverage: 2 file(s) the graph is unsure of, searched by text too - 0 hit(s)",
                "coverage: 2 file(s) the graph outlines (.md), their values and their text held by no literal of it, searched by text too - 1 hit(s)",
                NoExtractor,
                .. LimitsOf("csharp", [CodeGraphLimit.Candidates]),
                .. LimitsOf("markdown"),
                .. fallback[^2..],
            ],
            answer);
        Assert.Equal("[1 file(s) passed over: binary, or no text this tool reads]", answer[^1]);
    }

    /// <summary>
    /// A file a node of which makes a reference of the data's name that the linker bound to nothing of that name - a member
    /// of a type the repository does not declare - is searched by text and said: the graph may be silent of a place in it.
    /// </summary>
    [Fact]
    public async Task Sites_SearchAFileWhoseReferenceOfTheNameTheGraphBoundToNothing_AndSayIt()
    {
        using var tree = await TreeAsync(
            ("src/Pinger.cs", "namespace Demo.Net;\n\npublic static class Pinger\n{\n    public static void Ping()\n    {\n    }\n}\n"),
            ("src/Caller.cs", "namespace Demo.Other;\n\npublic static class Caller\n{\n    public static void Run(System.Net.Http.HttpClient client)\n    {\n        client.Ping();\n    }\n}\n"));
        var queries = await QueriesAsync(tree);
        var tool = Tool(tree, queries);

        var answer = (await AskAsync(tool, """{"op":"sites","data":"Ping"}""")).Text.Split('\n');

        Assert.Equal(
            [
                "every place that decides 'Ping':",
                "csharp in directory src/ (2):",
                $"  {GraphLines.Of(CardOf(queries, "Caller.Run"))} | a line holds it",
                "      src/Caller.cs:7 client.Ping();",
                $"  {GraphLines.Of(CardOf(queries, "Pinger.Ping"))} | defines",
                ListedZoo,
                "coverage: 2 file(s) the graph is unsure of, searched by text too - 0 hit(s)",
                OutlinedReadme,
                "coverage: 1 file(s) holding a reference named 'Ping' that the graph bound to nothing of that name, searched by text too - 1 hit(s)",
                NoExtractor,
            ],
            answer[..10]);
    }

    /// <summary>
    /// A rule's selector is asked as a rule writes it and by its name as a class list writes it: the literals that hold it so
    /// - a class added by its name, a selector that ends with it - are its places, and a longer class's name is none; its
    /// leading dot is its own, so <c>.header</c> asks of the class rule alone, never the element rule or the key of that name.
    /// </summary>
    [Fact]
    public async Task ASelector_IsAskedAsARuleWritesItAndByItsName_ItsDotItsOwn()
    {
        using var tree = await TreeAsync(
            ("web/site.css", ".btn-primary {\n  color: red;\n}\n\n.btn-primary-outline {\n  color: blue;\n}\n\nheader {\n  margin: 0;\n}\n\n.header {\n  padding: 0;\n}\n"),
            ("web/app.ts", "export function mark(button: HTMLElement): void {\n  button.classList.add('btn-primary');\n}\n\nexport function find(): Element | null {\n  return document.querySelector('button.btn-primary');\n}\n\nexport function outline(): Element | null {\n  return document.querySelector('.btn-primary-outline');\n}\n"),
            ("config/app.json", "{\n  \"header\": \"Welcome\"\n}\n"));
        var queries = await QueriesAsync(tree);
        var tool = Tool(tree, queries);

        var selector = await AskAsync(tool, """{"op":"sites","data":".btn-primary"}""");
        var lines = selector.Text.Split('\n');
        var rule = queries.Card("css:web/site.css#.btn-primary");

        Assert.Equal(["css:web/site.css#.btn-primary"], selector.Facts.Asked);
        Assert.Contains($"  {GraphLines.Of(rule)} | defines, a line holds it", lines);
        Assert.Contains("      web/site.css:1 .btn-primary {", lines);
        Assert.Contains($"  {GraphLines.Of(CardOf(queries, "mark"))} | holds the literal", lines);
        Assert.Contains("      web/app.ts:2 holds the literal 'btn-primary'", lines);
        Assert.Contains($"  {GraphLines.Of(CardOf(queries, "find"))} | holds the literal", lines);
        Assert.Contains("      web/app.ts:6 holds the literal 'button.btn-primary'", lines);
        Assert.Contains("coverage: '.btn-primary' searched as a rule writes it and as the name 'btn-primary', a whole word of a class list", lines);
        Assert.DoesNotContain(lines, line => line.Contains("btn-primary-outline", StringComparison.Ordinal));

        // The search itself: the selector with no character of a class's name after it, and its name a whole word of a class list.
        var search = GraphSitesSearch.Of(".btn-primary");
        string[] held = ["button.btn-primary", ".btn-primary {", "class=\"btn btn-primary\"", "addClass('btn-primary')", "btn-primary"];
        string[] other = [".btn-primary-outline", "btn-primary-outline", "my-btn-primary", "class=\"btn-primary2\""];

        Assert.Equal([.. held.Select(_ => true), .. other.Select(_ => false)], held.Concat(other).Select(search.Holds));
        Assert.Equal((".btn-primary", "btn-primary"), (search.Word, search.Selector));
        Assert.Empty(search.Spellings);

        // The dot is the selector's own: the class rule alone, and the name without it the element rule and the key.
        Assert.Equal(["css:web/site.css#.header"], (await AskAsync(tool, """{"op":"sites","data":".header"}""")).Facts.Asked);
        Assert.Equal(["css:web/site.css#header", "json:config/app.json#header"], (await AskAsync(tool, """{"op":"sites","data":"header"}""")).Facts.Asked);
    }

    /// <summary>
    /// What overrides a definer and what it overrides are its places both ways - C#'s override of a virtual member and
    /// Python's method a base declares, which the reader asks as a probe - and what implements a definer is reached by each
    /// edge that says so: a type's implements of an interface, and an interface member's dispatch to the member that answers
    /// it for a type.
    /// </summary>
    [Fact]
    public async Task Sites_ReachWhatOverridesAndIsOverridden_AndWhatImplements_ByEachEdgeThatSaysSo()
    {
        using var tree = await TreeAsync(
            ("src/Animals.cs", "namespace Demo.Animals;\n\npublic class Animal\n{\n    public virtual string Sound()\n    {\n        return \"...\";\n    }\n}\n\npublic sealed class Dog : Animal\n{\n    public override string Sound()\n    {\n        return \"woof\";\n    }\n}\n"),
            ("zoo/animals.py", "class Animal:\n    def sound(self):\n        return \"...\"\n\n\nclass Dog(Animal):\n    def sound(self):\n        return \"woof\"\n"));
        var queries = await QueriesAsync(tree);
        var tool = Tool(tree, queries);

        async Task<string[]> Sections(string data, int count) => (await AskAsync(tool, $$"""{"op":"sites","data":"{{data}}"}""")).Text.Split('\n')[..count];

        var animal = GraphLines.Of(CardOf(queries, "Animal.Sound"));
        var dog = GraphLines.Of(CardOf(queries, "Dog.Sound"));
        var animalPy = GraphLines.Of(CardOf(queries, "Animal.sound"));
        var dogPy = GraphLines.Of(CardOf(queries, "Dog.sound"));

        Assert.Equal(["every place that decides 'Animal.Sound':", "csharp in directory src/ (2):", $"  {animal} | defines", $"  {dog} | overrides", ListedZoo], await Sections("Animal.Sound", 5));
        Assert.Equal(["every place that decides 'Dog.Sound':", "csharp in directory src/ (2):", $"  {animal} | overrides", $"  {dog} | defines", ListedZoo], await Sections("Dog.Sound", 5));
        Assert.Equal(["every place that decides 'Animal.sound':", "python in directory zoo/ (2):", $"  {animalPy} | defines", $"  {dogPy} | overrides", ListedZoo], await Sections("Animal.sound", 5));
        Assert.Equal(["every place that decides 'Dog.sound':", "python in directory zoo/ (2):", $"  {animalPy} | overrides", $"  {dogPy} | defines", ListedZoo], await Sections("Dog.sound", 5));

        // The type's implements, and the interface member's dispatch: each reaches its implementation alone.
        var circle = (await AskAsync(tool, """{"op":"sites","data":"IShape"}""")).Text.Split('\n');
        var area = (await AskAsync(tool, """{"op":"sites","data":"IShape.Area"}""")).Text.Split('\n');

        Assert.Contains($"  {GraphLines.Of(CardOf(queries, "Demo.Circle"))} | implements", circle);
        Assert.Contains("  7bfbc3 member public double Area() | src/Shapes.cs:12-15 #2a3dc106 | 1 caller(s), 0 callee(s) | implements", area);
    }

    /// <summary>
    /// Every limit of each language whose files a <c>sites</c> answer read is said where the answer ends, one rule for every
    /// language: a limit whose sign the files read show - the kind of a dynamic mark one holds, or what the linker found of its
    /// references and its imports, each sign one thing it knows - in full, as its help says it, with how many show it; and every
    /// other by its short name, on one line for the language that points to <c>help codebase-graph-languages</c>, which says each
    /// in full under that name - nothing left unsaid, the answer small, and no limit said in full of a file that does not show it.
    /// Read before what the answer says of their files is trusted.
    /// </summary>
    [Fact]
    public async Task Sites_EndWithEveryLimitOfEachLanguageRead_TheOnesItsFilesShowMarkedWithHowMany()
    {
        // A file holding a mark of one kind; a file the linker left candidates in and holds no mark.
        using var marked = await TreeAsync(("shop/total.py", "def total(cart):\n    \"\"\"The cart's total.\"\"\"\n    return sum(getattr(line, \"cents\") for line in cart.lines)\n"));
        using var typed = await TreeAsync(("shop/total.py", "class Tea:\n    def cents(self):\n        return 350\n\n\nclass Cake:\n    def cents(self):\n        return 500\n\n\ndef total(cart):\n    return cart.cents()\n"));

        var computed = (await AskAsync(Tool(marked, await QueriesAsync(marked)), """{"op":"sites","data":"total"}""")).Text.Split('\n');
        var duck = (await AskAsync(Tool(typed, await QueriesAsync(typed)), """{"op":"sites","data":"total"}""")).Text.Split('\n');

        // After the coverage lines, the limits, then the files left out: the notes in their order. Geometry.cs is left candidates
        // in every tree of these tests, and C#'s limits are said by the one rule too: those that say a call is bound by its name
        // alone in full, as Python's duck-typing is where a file of it is left candidates.
        string[] Tail(string[] answer)
        {
            var last = Array.FindLastIndex(answer, line => line.StartsWith("coverage: ", StringComparison.Ordinal));

            Assert.True(last >= 0, string.Join('\n', answer));
            Assert.Matches(@"^\[\d+ file\(s\) you may not read were left out\]$", answer[^1]);

            return answer[(last + 1)..^1];
        }

        Assert.Equal([.. LimitsOf("csharp", [CodeGraphLimit.Candidates]), .. LimitsOf("markdown"), .. LimitsOf("python", [PythonReading.ComputedAttribute])], Tail(computed));
        Assert.Equal([.. LimitsOf("csharp", [CodeGraphLimit.Candidates]), .. LimitsOf("markdown"), .. LimitsOf("python", [CodeGraphLimit.Candidates])], Tail(duck));
        Assert.Equal("coverage: 3 file(s) the graph is unsure of, searched by text too - 2 hit(s)", computed.Single(line => line.Contains("unsure of", StringComparison.Ordinal)));

        // The marks themselves, as the answer writes them: a limit shown by its words in full, never by the sign, so a limit that
        // loses its sign is seen to lose its mark; every other by its name alone, on one line a language that points to where each is
        // said in full - a limit no file shows is never said in full, and none is left unsaid.
        Assert.Contains($"python does not see {Says("computed attribute access")} - shown by 1 file(s) read.", Tail(computed));
        Assert.Contains($"python does not see {Says("a duck-typed receiver")} - shown by 1 file(s) read.", Tail(duck));
        Assert.DoesNotContain(Tail(computed), line => line.Contains(Says("a duck-typed receiver"), StringComparison.Ordinal));
        Assert.Equal(
            [
                $"csharp does not see {Says("a var given a call's result", "csharp")} - shown by 1 file(s) read.",
                $"csharp does not see {Says("a generic extension method", "csharp")} - shown by 1 file(s) read.",
                "csharp also does not see ('help codebase-graph-languages' says each): preprocessor-branches, local-functions, dynamic-calls, untyped-reads, generated-sources, same-named-types, assembly-attributes, deep-nesting, huge-files.",
                "markdown also does not see ('help codebase-graph-languages' says each): fenced-headings, html-headings.",
                $"python does not see {Says("computed attribute access")} - shown by 1 file(s) read.",
                "python also does not see ('help codebase-graph-languages' says each): dynamic-imports, unseen-roots, ambiguous-imports, monkey-patching, exec-eval, duck-typing, capital-functions, computed-all, unclosed, refused-escapes, unreadable-heads, module-getattr, unbound-names, outside-fixtures, lambdas, test-helpers, lowercase-annotations, outside-attributes, manifests.",
            ],
            Tail(computed));
        Assert.All(
            CodeGraphExtractors.OfThisBuild.Find("python")!.Help.DoesNotSee.Where(limit => !limit.Says.StartsWith("computed attribute access", StringComparison.Ordinal)),
            limit => Assert.Contains($"    {limit.Name}: does not see", HelpTopics.Render(HelpTopics.CodebaseGraphLanguages), StringComparison.Ordinal));

        // What makes each shown: the signs the limits name - the kind of the mark the one file holds, candidates the other is left.
        var limits = CodeGraphExtractors.OfThisBuild.Find("python")!.Help.DoesNotSee;

        Assert.Equal([PythonReading.ComputedAttribute], Assert.Single(limits, limit => limit.Says.StartsWith("computed attribute access", StringComparison.Ordinal)).Signs);
        Assert.Equal([CodeGraphLimit.Candidates, PythonReading.FunctionValue], Assert.Single(limits, limit => limit.Says.StartsWith("a duck-typed receiver", StringComparison.Ordinal)).Signs);

        // Every limit whose sign the code can give: the two above, and the others the reader marks or the linker finds.
        Assert.Equal(12, limits.Count(limit => limit.Signs.Count > 0));

        // A file whose bracket never closes; one whose call reaches through an import that names no file of the tree; one whose
        // call names what nothing the file says binds; a test given a fixture no conftest.py provides; a script whose bare import
        // stands at two places, its call through it a candidate of each though one place alone declares it; and a page another
        // template engine writes. Each shows the limits its own sign names, of its own language, and no other: a library's call shows
        // no name nothing binds and no fixture, and a call through an import of two places no duck-typed receiver.
        using var signed = await TreeAsync(
            ("shop/total.py", "def total(cart):\n    return sum(\n"),
            ("shop/rates.py", "from vendor.rates import convert\n\n\ndef rate():\n    return convert(1)\n"),
            ("shop/tally.py", "def tally():\n    return undeclared_total()\n"),
            ("tests/test_rates.py", "def test_rate(tmp_path):\n    assert tmp_path\n"),
            ("tools/run.py", "import helper\n\n\ndef run():\n    return helper.pack()\n"),
            ("tools/helper.py", "def pack():\n    return 1\n"),
            ("helper.py", "def other():\n    return 2\n"),
            ("web/page.html", "<main>\n  <p>{% if user %}Hello{% endif %}</p>\n</main>\n"));
        var signedQueries = await QueriesAsync(signed);
        var shown = Tail((await AskAsync(Tool(signed, signedQueries), """{"op":"sites","data":"total"}""")).Text.Split('\n'));

        // Each file's own signs, as the view reads them: the kind of its mark, and what the linker found of its references and imports.
        Assert.Equal([PythonReading.Unclosed], signedQueries.SignsOf("shop/total.py"));
        Assert.Equal([CodeGraphLimit.OutsideImport], signedQueries.SignsOf("shop/rates.py"));
        Assert.Equal([CodeGraphLimit.Unbound], signedQueries.SignsOf("shop/tally.py"));
        Assert.Equal([CodeGraphLimit.UnprovidedFixture], signedQueries.SignsOf("tests/test_rates.py"));
        Assert.Equal([CodeGraphLimit.CandidateImports], signedQueries.SignsOf("tools/run.py"));
        Assert.Empty(signedQueries.SignsOf("tools/helper.py"));
        Assert.Empty(signedQueries.SignsOf("helper.py"));
        Assert.Equal([HtmlExtractor.OtherEngine], signedQueries.SignsOf("web/page.html"));

        Assert.Equal(
            [
                .. LimitsOf("csharp", [CodeGraphLimit.Candidates]),
                .. LimitsOf("html", [HtmlExtractor.OtherEngine]),
                .. LimitsOf("markdown"),
                $"python does not see {Says("a root of the path")} - shown by 1 file(s) read.",
                $"python does not see {Says("two places or more")} - shown by 1 file(s) read.",
                $"python does not see {Says("never closes")} - shown by 1 file(s) read.",
                $"python does not see {Says("neither declares nor imports")} - shown by 1 file(s) read.",
                $"python does not see {Says("no conftest.py")} - shown by 1 file(s) read.",
                "python also does not see ('help codebase-graph-languages' says each): computed-attributes, dynamic-imports, monkey-patching, exec-eval, duck-typing, capital-functions, computed-all, refused-escapes, unreadable-heads, module-getattr, lambdas, test-helpers, lowercase-annotations, outside-attributes, manifests.",
            ],
            shown);
        Assert.Contains($"html does not see {Assert.Single(CodeGraphExtractors.OfThisBuild.Find("html")!.Help.DoesNotSee, limit => limit.Signs.Contains(HtmlExtractor.OtherEngine)).Says} - shown by 1 file(s) read.", shown);

        // Each sign the linker gives is one thing it knows, the sign of one limit of Python's alone.
        string[] Named(string sign) => [.. limits.Where(limit => limit.Signs.Contains(sign)).Select(limit => limit.Name)];

        Assert.Equal(["unseen-roots"], Named(CodeGraphLimit.OutsideImport));
        Assert.Equal(["unbound-names"], Named(CodeGraphLimit.Unbound));
        Assert.Equal(["outside-fixtures"], Named(CodeGraphLimit.UnprovidedFixture));
        Assert.Equal(["ambiguous-imports"], Named(CodeGraphLimit.CandidateImports));
        Assert.Equal(["duck-typing"], Named(CodeGraphLimit.Candidates));
        Assert.Equal(["unclosed"], Named(PythonReading.Unclosed));
        Assert.Equal(["refused-escapes"], Named(PythonReading.RefusedEscape));
    }

    /// <summary>The words of the one limit of <paramref name="language"/> - Python's where none is named - whose clause holds <paramref name="names"/>, as its help says them.</summary>
    private static string Says(string names, string language = "python")
        => Assert.Single(CodeGraphExtractors.OfThisBuild.Find(language)!.Help.DoesNotSee, limit => limit.Says.Contains(names, StringComparison.Ordinal)).Says;

    /// <summary>
    /// The lines a <c>sites</c> answer ends with for <paramref name="language"/>: each limit of its implementation's help that some
    /// of the files read - each by the signs it gives, <paramref name="files"/> - show a sign of, in its order and in full, with how
    /// many show it; then every other by its short name, in its order, on one line that points to the help topic saying each.
    /// </summary>
    private static IEnumerable<string> LimitsOf(string language, params IReadOnlyList<string>[] files)
    {
        var extractor = CodeGraphExtractors.OfThisBuild.Find(language)!;
        var named = new List<string>();

        foreach (var limit in extractor.Help.DoesNotSee)
        {
            var shown = files.Count(signs => signs.Any(sign => limit.Signs.Contains(sign, StringComparer.Ordinal)));

            if (shown == 0)
            {
                named.Add(limit.Name);
                continue;
            }

            yield return $"{extractor.Language} does not see {limit.Says} - shown by {shown.ToString(CultureInfo.InvariantCulture)} file(s) read.";
        }

        if (named.Count > 0)
        {
            yield return $"{extractor.Language} also does not see ('help codebase-graph-languages' says each): {string.Join(", ", named)}.";
        }
    }

    /// <summary>The card of the one node <paramref name="symbol"/> names in <paramref name="queries"/>.</summary>
    private static GraphCard CardOf(CodebaseGraphQueries queries, string symbol) => Assert.IsType<GraphResolve.Found>(queries.Resolve(symbol)).Card;

    /// <summary>
    /// A quoted data asks by its value alone, never the symbols: a name the graph holds declarations of answers no definer
    /// quoted, but every literal that holds it beneath its node's card, and every line of every file the graph read - for it
    /// holds a value only where code writes it as a string literal - each line a literal already says passed over; a value
    /// only a listed file holds is a raw line.
    /// </summary>
    [Fact]
    public async Task AQuotedSitesData_AsksByItsValueAlone_NeverTheSymbols()
    {
        using var tree = await TreeAsync(
            ("src/Keys.cs", "namespace Demo;\n\npublic static class Keys\n{\n    public static string Of()\n    {\n        return \"Area\";\n    }\n}\n"));
        var tool = Tool(tree, await QueriesAsync(tree));

        var quoted = (await AskAsync(tool, """{"op":"sites","data":"\"Area\""}""")).Text.Split('\n');
        var okapi = (await AskAsync(tool, """{"op":"sites","data":"'Okapi'"}""")).Text.Split('\n');

        Assert.Equal(
            [
                "every place that decides the literal 'Area':",
                "csharp in directory src/ (5):",
                "  bc11b2 member public static double Total(IShape[] shapes) | src/Geometry.cs:5-14 #5b03176a | 2 caller(s), 3 callee(s) | a line holds it",
                "      src/Geometry.cs:10 sum += shape.Area();",
                "  f8d51c member public static string Of() | src/Keys.cs:5-8 #22f96ba7 | holds the literal",
                "      src/Keys.cs:7 holds the literal 'Area'",
                "  dff23b member double Area() | src/Shapes.cs:6-6 #704ac59f | 1 caller(s), 0 callee(s) | a line holds it",
                "      src/Shapes.cs:6 double Area();",
                "  7bfbc3 member public double Area() | src/Shapes.cs:12-15 #2a3dc106 | 1 caller(s), 0 callee(s) | a line holds it",
                "      src/Shapes.cs:12 public double Area()",
                "  df67b4 member public double Area() | src/Shapes.g.cs:6-9 #f393c58e | 1 caller(s), 0 callee(s), generated | a line holds it",
                "      src/Shapes.g.cs:6 public double Area()",
                ListedZoo,
                "coverage: 2 file(s) the graph is unsure of, searched by text too - 2 hit(s)",
                OutlinedReadme,
                "coverage: 3 file(s) the graph read, for it holds a value only where code writes it as a string literal, searched by text too - 3 hit(s)",
                NoExtractor,
            ],
            quoted[..17]);

        // Single quotes ask by the value as double ones do, and a value only a listed file holds is a raw line.
        Assert.Equal(
            [
                "every place that decides the literal 'Okapi':",
                ".txt in directory docs/ (1):",
                "  docs/zoo.txt:1 An Okapi grazes here.",
                "coverage: 1 file(s) the graph lists only (.txt) searched by text - 1 hit(s)",
            ],
            okapi[..4]);
    }

    /// <summary>
    /// <c>sites</c> answers in pages of whole entries like every read: the header naming the pages and the exact
    /// total, the last page saying it is the last, every site on exactly one page, none cut, none repeated.
    /// </summary>
    [Fact]
    public async Task Sites_AnswersInPagesOfWholeEntries_EverySiteOnExactlyOnePage()
    {
        var overloads = new StringBuilder("namespace Demo;\n\npublic static class Decider\n{");

        for (var at = 0; at < 61; at++)
        {
            overloads.Append(CultureInfo.InvariantCulture, $"\n    public static void Decides(int value{at})\n    {{\n    }}\n");
        }

        using var tree = await TreeAsync(("src/Decider.cs", overloads.Append("}\n").ToString()));
        var tool = Tool(tree, await QueriesAsync(tree));

        var first = (await AskAsync(tool, """{"op":"sites","data":"Decides"}""")).Text.Split('\n');
        var pages = int.Parse(first[0].Split(' ')[3], CultureInfo.InvariantCulture);

        Assert.Equal($"page 1 of {pages} · 61 results · ask again with page: 2 for the next", first[0]);
        Assert.Equal("every place that decides 'Decides':", first[1]);
        Assert.Equal("csharp in directory src/ (61):", first[2]);

        // Every page read to the last: the union is every site once, and the coverage lines end the last.
        var all = first[3..].ToList();

        for (var page = 2; page <= pages; page++)
        {
            var later = (await AskAsync(tool, $$"""{"op":"sites","data":"Decides","page":{{page}}}""")).Text.Split('\n');
            all.AddRange(later[1..]);

            if (page == pages)
            {
                Assert.Equal($"page {pages} of {pages} · the last", later[0]);
                Assert.Contains(NoExtractor, later);
            }
        }

        var sites = all.Where(line => line.StartsWith("  ", StringComparison.Ordinal)).ToList();

        Assert.Equal(61, sites.Count);
        Assert.Distinct(sites);
        Assert.All(sites, site => Assert.EndsWith(" | defines", site, StringComparison.Ordinal));

        var past = await AskAsync(tool, $$"""{"op":"sites","data":"Decides","page":{{pages + 1}}}""");

        Assert.Equal((GraphToolOutcome.Usage, $"page {pages + 1} of 'sites Decides': it has {pages} pages (1 to {pages})"), (past.Outcome, past.Text));
    }

    /// <summary>
    /// A <c>sites</c> call that names no data, one that is empty, one quoted whose value is empty or white space alone, or one
    /// with a member it does not read, is a misuse answered with a usage line - never thrown at the lookups.
    /// </summary>
    [Fact]
    public async Task ASitesCall_NeedsItsData_AndReadsNoOtherMember()
    {
        using var tree = await TreeAsync();
        var tool = Tool(tree, await QueriesAsync(tree));

        var missing = await AskAsync(tool, """{"op":"sites"}""");
        var blank = await AskAsync(tool, """{"op":"sites","data":" "}""");
        var quotedBlank = await AskAsync(tool, """{"op":"sites","data":"\" \""}""");
        var quotedEmpty = await AskAsync(tool, """{"op":"sites","data":"''"}""");
        var stray = await AskAsync(tool, """{"op":"sites","data":"Area","kind":"type"}""");

        Assert.Equal((GraphToolOutcome.Usage, $"op 'sites' needs data: {DataDescription}"), (missing.Outcome, missing.Text));
        Assert.Equal((GraphToolOutcome.Usage, $"data is empty: {DataDescription}"), (blank.Outcome, blank.Text));
        Assert.Equal((GraphToolOutcome.Usage, $"data is empty: {DataDescription}"), (quotedBlank.Outcome, quotedBlank.Text));
        Assert.Equal((GraphToolOutcome.Usage, $"data is empty: {DataDescription}"), (quotedEmpty.Outcome, quotedEmpty.Text));
        Assert.Equal((GraphToolOutcome.Usage, "op 'sites' does not read kind: it reads data, page"), (stray.Outcome, stray.Text));
    }

    /// <summary>
    /// Where nothing decides the data - no declaration, no literal, no line of the files searched by text - the answer says so
    /// of what was searched alone, never of every line, and still ends with its coverage lines.
    /// </summary>
    [Fact]
    public async Task Sites_OfDataNothingDecides_SaySoOfWhatWasSearched_AndStillEndWithTheCoverageLines()
    {
        using var tree = await TreeAsync();
        var tool = Tool(tree, await QueriesAsync(tree));

        var answer = await AskAsync(tool, """{"op":"sites","data":"NothingAnywhere"}""");
        var quoted = (await AskAsync(tool, """{"op":"sites","data":"'NothingAnywhere'"}""")).Text.Split('\n');

        Assert.Equal(
            [
                "every place that decides 'NothingAnywhere':",
                "the graph holds no declaration of it and no literal that holds it, and no line of the files searched by text holds it",
                ListedZoo,
                "coverage: 2 file(s) the graph is unsure of, searched by text too - 0 hit(s)",
                OutlinedReadme,
                NoExtractor,
            ],
            answer.Text.Split('\n')[..6]);
        Assert.Equal(0, answer.Paging!.Total);
        Assert.Equal(
            [
                "every place that decides the literal 'NothingAnywhere':",
                "the graph holds no literal that holds it, and no line of the files searched by text holds it",
                ListedZoo,
                "coverage: 2 file(s) the graph is unsure of, searched by text too - 0 hit(s)",
                OutlinedReadme,
                "coverage: 2 file(s) the graph read, for it holds a value only where code writes it as a string literal, searched by text too - 0 hit(s)",
                NoExtractor,
            ],
            quoted[..7]);

        // Where the step reads no file the graph lists only and none it is unsure of, the coverage lines say none was searched,
        // and nothing is said of a language with no extractor, whose files were not searched.
        var unread = (await AskAsync(Tool(tree, await QueriesAsync(tree), mayRead: path => !path.StartsWith("docs/", StringComparison.Ordinal) && path is not ("src/Geometry.cs" or "src/Shapes.g.cs")), """{"op":"sites","data":"NothingAnywhere"}""")).Text.Split('\n');

        Assert.Equal(
            [
                "every place that decides 'NothingAnywhere':",
                "the graph holds no declaration of it and no literal that holds it, and no line of the files searched by text holds it",
                "coverage: no file the graph lists only was searched by text",
                "coverage: no file the graph is unsure of was searched by text",
                OutlinedReadme,
                .. LimitsOf("csharp"),
                .. LimitsOf("markdown"),
            ],
            unread[..^1]);
    }

    /// <summary>
    /// The node at a line - a text hit's card, a <c>path:line</c>'s node - is, at a line nodes begin on, the outermost of them -
    /// of those that end last, the one no other of them is declared inside - whatever their count: a one-line type, not its
    /// one member; a positional record, not its parameter, one or several; a one-line enum, not a member; a one-line object of
    /// a JSON list, not its key, one or several; a method that begins on a field's line, not the field. Where several are
    /// outermost, the one that makes a reference at the line, else the nearest node they are all declared inside: two fields
    /// on one line, their type. At a line inside a node that began before it, the innermost node that holds it, never one by
    /// the order of their ids: a page's landmark before the body of the same lines, a Vue component that spans its whole file
    /// before the file's own node, and a Razor page's markup before the class it compiles to.
    /// </summary>
    [Fact]
    public async Task TheNodeAtALine_IsTheOutermostTheLineBegins_OrTheInnermostThatHoldsIt_NeverOneByTheOrderOfIds()
    {
        using var tree = await TreeAsync(
            ("web/page.html", "<main>\n  <p>Okapi</p>\n</main>\n"),
            ("src/One.cs", "namespace Demo;\n\npublic sealed class One { public int Two() => 2; }\n"),
            ("src/Cart.vue", "<template>\n  <p>{{ total() }}</p>\n</template>\n<script setup>\nfunction total() { return 1; }\n</script>\n"),
            ("src/CartService.cs", "namespace Shop;\n\npublic sealed class CartService\n{\n    public int Total() => 0;\n}\n"),
            ("src/Pages/_Total.cshtml", "@model Shop.CartService\n\n<p>Total: @Model.Total()</p>\n"),
            ("src/Events.cs", "namespace Demo;\n\npublic interface IEvent\n{\n}\n\npublic sealed class Product\n{\n}\n\npublic sealed record OrderPlaced(System.Guid Id, decimal Total) : IEvent;\n\npublic sealed record Line(Product Item, int Count);\n"),
            ("src/Ids.cs", "namespace Demo;\n\npublic interface IId\n{\n}\n\npublic readonly record struct OrderId(System.Guid Value) : IId;\n"),
            ("src/Enums.cs", "namespace Demo;\n\npublic enum Shipping { Standard, Express }\n\npublic enum Color { Red, Green, Blue }\n"),
            ("src/Pair.cs", "namespace Demo;\n\npublic sealed class Pair\n{\n    public int Left; public int Right;\n}\n"),
            ("src/Mixed.cs", "namespace Demo;\n\npublic sealed class Mixed\n{\n    public int Count; public void Reset()\n    {\n        Count = 0;\n    }\n}\n"),
            ("config/limits.json", "{\n  \"limits\": [\n    { \"kind\": \"depth\", \"value\": 8 },\n    { \"kind\": \"size\", \"value\": 9 }\n  ]\n}\n"),
            ("config/urls.json", "[\n  { \"url\": \"x\" }\n]\n"));
        var queries = await QueriesAsync(tree);

        string Qualified(string path, int line) => queries.AtLine(path, line)!.Qualified;

        Assert.Equal("landmark", queries.AtLine("web/page.html", 2)!.SubKind);
        Assert.Equal(("vue:src/Cart.vue", CodeNodeKinds.Type), (queries.AtLine("src/Cart.vue", 2)!.Id, queries.AtLine("src/Cart.vue", 2)!.Kind));
        Assert.Equal("razor:src/Pages/_Total.cshtml#view", queries.AtLine("src/Pages/_Total.cshtml", 3)!.Id);

        // Each line here begins several nodes, the others declared inside one: the type, the record, the enum, the object -
        // of one member as of several.
        Assert.Equal(
            ["Demo.One", "Demo.OrderPlaced", "Demo.Line", "Demo.OrderId", "Demo.Shipping", "Demo.Color", "config/limits.json#limits[0]", "config/limits.json#limits[1]", "config/urls.json#[0]"],
            [Qualified("src/One.cs", 3), Qualified("src/Events.cs", 11), Qualified("src/Events.cs", 13), Qualified("src/Ids.cs", 7), Qualified("src/Enums.cs", 3), Qualified("src/Enums.cs", 5), Qualified("config/limits.json", 3), Qualified("config/limits.json", 4), Qualified("config/urls.json", 2)]);

        // A method that begins on a field's line ends after it: the method's lines hold the field's.
        Assert.Equal("Demo.Mixed.Reset", Qualified("src/Mixed.cs", 5));

        // Two fields on a line of their type: neither of them, but the type they are declared inside.
        Assert.Equal("Demo.Pair", Qualified("src/Pair.cs", 5));

        // A line no node but the file's own holds is the file's; one past its end is none.
        Assert.Equal(CodeNodeKinds.File, queries.AtLine("config/limits.json", 1)!.Kind);
        Assert.Null(queries.AtLine("src/Enums.cs", 400));
    }

    /// <summary>
    /// The node at a line is read from the file's line index, made once a file: asking it of every line of a file of thousands
    /// of nodes makes one card a line answered, never a card of every node of the file a line - which made a search of a
    /// file's every line cost its lines times its nodes.
    /// </summary>
    [Fact]
    public async Task TheNodeAtEveryLineOfAFile_MakesOneCardALine()
    {
        const int Keys = 500;
        var json = new StringBuilder("{\n");

        for (var at = 0; at < Keys; at++)
        {
            json.Append(CultureInfo.InvariantCulture, $"  \"key{at}\": {{ \"version\": \"1.{at}\", \"name\": \"package{at}\" }}{(at < Keys - 1 ? "," : string.Empty)}\n");
        }

        using var tree = await TreeAsync(("data/lock.json", json.Append("}\n").ToString()));
        var queries = await QueriesAsync(tree);
        var before = queries.CardsMade;
        var answered = Enumerable.Range(1, Keys + 2).Count(line => queries.AtLine("data/lock.json", line) is not null);

        Assert.Equal(Keys + 2, answered);
        Assert.Equal(Keys + 2, queries.CardsMade - before);
        Assert.Equal("data/lock.json#key7", queries.AtLine("data/lock.json", 9)!.Qualified);
    }

    /// <summary>
    /// A search's caller takes each file by the path its listing names it by, before the search asks whether the reader may
    /// read it: a file not taken is passed over by the caller's own will and counted nowhere - never among the files the
    /// reader may not read - and each hit and each file read is named by that same path.
    /// </summary>
    [Fact]
    public async Task ASearch_TakesEachFileByItsListedPath_BeforeItsReadGate_AndCountsNoFileItDidNotTake()
    {
        using var tree = await TreeAsync(("src/Keys.cs", "namespace Demo;\n\npublic static class Keys\n{\n    public static string Of() => \"grazes\";\n}\n"));
        var files = new AgentFiles(tree.Harness.GitClient, tree.Harness.FileSystem);
        var taken = new List<string>();
        var hits = new List<string>();
        var read = new List<string>();

        var search = await files.SearchAsync(
            tree.Tree,
            new Regex("grazes|Geometry").IsMatch,
            path =>
            {
                taken.Add(path);
                return path is "docs/zoo.txt" or "src/Geometry.cs";
            },
            0,
            full => !full.EndsWith("Geometry.cs", StringComparison.Ordinal) && !full.EndsWith("Keys.cs", StringComparison.Ordinal),
            (hit, _) =>
            {
                hits.Add($"{hit.Path}:{hit.Line.ToString(CultureInfo.InvariantCulture)}");
                return ValueTask.CompletedTask;
            },
            read.Add,
            TestContext.Current.CancellationToken);

        // Keys.cs, which the reader may not read either, was never taken: Geometry.cs alone is counted as left out.
        Assert.Contains("src/Keys.cs", taken);
        Assert.Equal(1, search.Refused);
        Assert.Equal(["[1 file(s) you may not read were left out]"], search.LeftOut(path => path));
        Assert.Equal(["docs/zoo.txt:1"], hits);
        Assert.Equal(["docs/zoo.txt"], read);
    }

    /// <summary>
    /// What decides of the data is walked from every definer, one the step's reads do not reach among them, each place it
    /// reaches held to the reads where it stands: a readable caller of a definer in a file the reads do not reach is listed -
    /// it tells nothing the step cannot read itself - and the definer counted as left out; the answer never says every place
    /// is in a file the reads do not reach where one is not.
    /// </summary>
    [Fact]
    public async Task Sites_WalkFromEveryDefiner_ListingAReadableCallerOfOneTheReadsDoNotReach()
    {
        using var tree = await TreeAsync(
            ("lib/Util.cs", "namespace Demo.Lib;\n\npublic static class Util\n{\n    public static int Helper() => 1;\n}\n"),
            ("src/App.cs", "namespace Demo.App;\n\npublic static class App\n{\n    public static int Run() => Demo.Lib.Util.Helper();\n}\n"));
        var queries = await QueriesAsync(tree);
        var tool = Tool(tree, queries, mayRead: path => path != "lib/Util.cs");

        var answer = await AskAsync(tool, """{"op":"sites","data":"Helper"}""");
        var lines = answer.Text.Split('\n');

        Assert.Equal(["every place that decides 'Helper':", "csharp in directory src/ (1):", $"  {GraphLines.Of(CardOf(queries, "App.Run"))} | calls", ListedZoo], lines[..4]);
        Assert.Equal("1 place(s) left out: in files your reads do not reach", lines[^1]);
        Assert.Empty(answer.Facts.Asked);
    }

    /// <summary>
    /// A key path is read in every spelling the one owner of a key path's spellings reads - dotted, a colon between its places
    /// as .NET's configuration writes one in code, a double underscore as an environment variable writes one - each asking of
    /// the same keys: the key at that path, and a key whose own name spells it, an environment variable's under a compose
    /// file's <c>environment</c>; and it is searched for by its last key, as data and code write it, and by its places as an
    /// environment variable writes them, which a whole word of its last key is never held by - the line that writes the path
    /// so said under its key's card, and how many key paths were searched so said in the coverage lines. One spelt so that
    /// nothing names is read as a key path too. A name spelt so of a place that holds a separator of its own is no spelling of
    /// the path, for it is never read back as it.
    /// </summary>
    [Fact]
    public async Task Sites_OfAKeyPathInAnySpelling_AnswerTheSameKeys_AndSearchItAsCodeDataAndTheEnvironmentWriteIt()
    {
        using var tree = await TreeAsync(
            ("config/app.json", "{\n  \"Payments\": {\n    \"TimeoutSeconds\": 30\n  }\n}\n"),
            ("src/Settings.cs", "namespace Demo;\n\npublic static class Settings\n{\n    public static string Timeout(System.Collections.Generic.IDictionary<string, string> config)\n    {\n        return config[\"Payments:TimeoutSeconds\"];\n    }\n}\n"),
            ("deploy/compose.yml", "environment:\n  Payments__TimeoutSeconds: 60\n"));
        var queries = await QueriesAsync(tree);
        var tool = Tool(tree, queries);
        var key = queries.Card("json:config/app.json#Payments.TimeoutSeconds");
        var variable = queries.AtLine("deploy/compose.yml", 2)!;

        Assert.Equal(("Payments__TimeoutSeconds", CodeSubKinds.Key), (variable.Name, variable.SubKind));

        foreach (var data in new[] { "Payments:TimeoutSeconds", "Payments__TimeoutSeconds", "Payments.TimeoutSeconds" })
        {
            var answer = await AskAsync(tool, $$"""{"op":"sites","data":"{{data}}"}""");
            var lines = answer.Text.Split('\n');

            Assert.Equal([key.Id, variable.Id], answer.Facts.Asked);
            Assert.Contains($"  {GraphLines.Of(key)} | holds the key, a line holds it", lines);
            Assert.Contains("      config/app.json:3 \"TimeoutSeconds\": 30", lines);
            Assert.Contains("      src/Settings.cs:7 holds the literal 'Payments:TimeoutSeconds'", lines);
            Assert.Contains($"  {GraphLines.Of(variable)} | holds the key, a line holds it", lines);
            Assert.Contains("      deploy/compose.yml:2 Payments__TimeoutSeconds: 60", lines);
            Assert.Contains(KeyPathsSearched("TimeoutSeconds", 1), lines);
        }

        // The word each is searched for by - the last key of a path in each spelling, a key path nothing names read as one, a name
        // with its scopes by its last part, a selector whole - and the key path it spells as an environment variable writes it.
        string[] written = ["Payments:TimeoutSeconds", "Payments__TimeoutSeconds", "Limits:1:Value", "Order::Total", ".btn", "MAX_SIZE"];
        var searches = written.Select(data => queries.SitesSearch(data, queries.DefinersOf(data))).ToList();

        Assert.Equal(["TimeoutSeconds", "TimeoutSeconds", "Value", "Total", ".btn", "MAX_SIZE"], searches.Select(search => search.Word));
        Assert.Equal([["Payments__TimeoutSeconds"], ["Payments__TimeoutSeconds"], ["Limits__1__Value"], [], [], []], searches.Select(search => search.Spellings.Order(StringComparer.Ordinal).ToArray()));

        // The last name of a path, a quoted name's dot no separator; each path as an environment variable writes it, and each name a
        // key may be written with that names it, none of a place that holds a separator of its own.
        Assert.Equal(
            ["TimeoutSeconds", "Value", "limits", "value", "Total", "MAX_SIZE"],
            new[] { "Payments.TimeoutSeconds", "Limits[1].Value", "limits[1]", "packages[\"a.b\"].value", "Order.Total", "MAX_SIZE" }.Select(CodeKeyPaths.LastName));
        Assert.Equal(
            ["Payments__TimeoutSeconds", "Limits__1__Value", null, null, null],
            new[] { "Payments.TimeoutSeconds", "Limits[1].Value", "packages[\"\"].name", "Payments", "environment.Payments__TimeoutSeconds" }.Select(CodeKeyPaths.Environment));
        Assert.Equal(["Limits:1:Value", "Limits__1__Value"], CodeKeyPaths.Spellings("Limits[1].Value"));
        Assert.Equal(["environment:Payments__TimeoutSeconds"], CodeKeyPaths.Spellings("environment.Payments__TimeoutSeconds"));
    }

    /// <summary>
    /// A key whose own name holds a dot - <c>Microsoft.AspNetCore</c> under <c>Logging.LogLevel</c>, as the ASP.NET Core
    /// template's appsettings writes it - is answered in every spelling: by its path with the name quoted in brackets, by
    /// .NET's spellings of it, by the name with some of its scopes, and by the name alone; and it is searched by its whole
    /// name, where a path read as four places searched for <c>LogLevel</c>. The nested keys of the same dots are another
    /// key's answer.
    /// </summary>
    [Fact]
    public async Task Sites_OfAKeyWhoseNameHoldsADot_AnswerThatKeyInEverySpelling_AndSearchItsWholeName()
    {
        using var tree = await DottedTreeAsync();
        var queries = await QueriesAsync(tree);
        var tool = Tool(tree, queries);
        var key = queries.Card("json:config/appsettings.json#Logging.LogLevel[\"Microsoft.AspNetCore\"]");
        var nested = queries.Card("json:config/appsettings.json#Logging.LogLevel.Microsoft.AspNetCore");
        var variable = queries.AtLine("deploy/compose.yml", 2)!;

        Assert.Equal(("Microsoft.AspNetCore", "Logging.LogLevel[\"Microsoft.AspNetCore\"]"), (key.Name, key.Qualified));
        Assert.Equal(("Logging__LogLevel__Microsoft.AspNetCore", "environment[\"Logging__LogLevel__Microsoft.AspNetCore\"]"), (variable.Name, variable.Qualified));

        Assert.Equal([key.Id, variable.Id], queries.DefinersOf("Logging:LogLevel:Microsoft.AspNetCore").Select(card => card.Id));
        Assert.Equal([key.Id, variable.Id], queries.DefinersOf("Logging__LogLevel__Microsoft.AspNetCore").Select(card => card.Id));
        Assert.Equal([key.Id, variable.Id], queries.DefinersOf("Logging.LogLevel[\"Microsoft.AspNetCore\"]").Select(card => card.Id));
        Assert.Equal([key.Id], queries.DefinersOf("LogLevel[\"Microsoft.AspNetCore\"]").Select(card => card.Id));
        Assert.Equal([key.Id], queries.DefinersOf("[\"Microsoft.AspNetCore\"]").Select(card => card.Id));
        Assert.Equal([nested.Id, key.Id], queries.DefinersOf("Microsoft.AspNetCore").Select(card => card.Id));
        Assert.Equal([nested.Id], queries.DefinersOf("Logging:LogLevel:Microsoft:AspNetCore").Select(card => card.Id));
        Assert.Equal([nested.Id, key.Id], queries.DefinersOf("Logging.LogLevel.Microsoft.AspNetCore").Select(card => card.Id));

        var search = queries.SitesSearch("Logging:LogLevel:Microsoft.AspNetCore", queries.DefinersOf("Logging:LogLevel:Microsoft.AspNetCore"));

        Assert.Equal("Microsoft.AspNetCore", search.Word);
        Assert.Equal(["Logging__LogLevel__Microsoft.AspNetCore"], search.Spellings.Order(StringComparer.Ordinal).ToArray());

        var answer = await AskAsync(tool, """{"op":"sites","data":"Logging:LogLevel:Microsoft.AspNetCore"}""");
        var lines = answer.Text.Split('\n');

        Assert.Contains($"  {GraphLines.Of(key)} | holds the key, a line holds it", lines);
        Assert.Contains("      config/appsettings.json:4 \"Microsoft.AspNetCore\": \"Warning\",", lines);
        Assert.Contains("      src/Logs.cs:7 holds the literal 'Logging:LogLevel:Microsoft.AspNetCore'", lines);
        Assert.Contains($"  {GraphLines.Of(variable)} | holds the key, a line holds it", lines);
        Assert.Contains("      deploy/compose.yml:2 Logging__LogLevel__Microsoft.AspNetCore: Debug", lines);
        Assert.Contains(KeyPathsSearched("Microsoft.AspNetCore", 1), lines);
        Assert.DoesNotContain(lines, line => line.StartsWith($"  {GraphLines.Of(nested)}", StringComparison.Ordinal));
    }

    /// <summary>
    /// A key path with a place in single quotes answers the keys the double-quoted one answers - JavaScript's and Python's
    /// habit - in every rule: by its path, with some of its scopes, with both marks in one path and with its own mark
    /// escaped; the answers, ids and records show the double form alone.
    /// </summary>
    [Fact]
    public async Task Sites_OfAKeyPathWithASingleQuotedPlace_AnswerTheKeysTheDoubleQuotedOneAnswers()
    {
        using var tree = await DottedTreeAsync(
            (".vscode/settings.json", "{\n  \"[typescript]\": {\n    \"editor.defaultFormatter\": \"x\"\n  }\n}\n"),
            ("config/flags.json", "{\n  \"Flags\": {\n    \"it's.on\": true\n  }\n}\n"));
        var queries = await QueriesAsync(tree);
        var tool = Tool(tree, queries);
        var key = queries.Card("json:config/appsettings.json#Logging.LogLevel[\"Microsoft.AspNetCore\"]");
        var variable = queries.AtLine("deploy/compose.yml", 2)!;
        const string formatter = "json:.vscode/settings.json#[\"[typescript]\"][\"editor.defaultFormatter\"]";

        Assert.Equal([key.Id, variable.Id], queries.DefinersOf("Logging.LogLevel['Microsoft.AspNetCore']").Select(card => card.Id));
        Assert.Equal([key.Id], queries.DefinersOf("LogLevel['Microsoft.AspNetCore']").Select(card => card.Id));
        Assert.Equal([key.Id], queries.DefinersOf("['Microsoft.AspNetCore']").Select(card => card.Id));
        Assert.Equal([formatter], queries.DefinersOf("[\"[typescript]\"]['editor.defaultFormatter']").Select(card => card.Id));
        Assert.Equal([formatter], queries.DefinersOf("['[typescript]'][\"editor.defaultFormatter\"]").Select(card => card.Id));
        Assert.Equal("Flags[\"it's.on\"]", Assert.Single(queries.DefinersOf("Flags['it\\'s.on']")).Qualified);

        var search = queries.SitesSearch("Logging.LogLevel['Microsoft.AspNetCore']", queries.DefinersOf("Logging.LogLevel['Microsoft.AspNetCore']"));

        Assert.Equal("Microsoft.AspNetCore", search.Word);
        Assert.Equal(["Logging__LogLevel__Microsoft.AspNetCore"], search.Spellings.Order(StringComparer.Ordinal).ToArray());

        Assert.StartsWith("'Logging.LogLevel['Nope.Missing']' is not a node the view holds\ntext: no line holds 'Nope.Missing'", (await AskAsync(tool, """{"op":"node","symbol":"Logging.LogLevel['Nope.Missing']"}""")).Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A key path written with its names' own dots as they are - as an agent composes it from the file it read, which is
    /// how it found the key before the path was written exactly - asks of every key it may mean: the nested keys of the
    /// same dots and the key whose name holds them. It resolves as the name the earlier rules find, the nested key, and a
    /// path that names no nested key resolves as the key. The queries alone read it, in their last rule.
    /// </summary>
    [Fact]
    public async Task Sites_OfAKeyPathWrittenWithItsNamesDotsAsTheyAre_AnswerEveryKeyItMayMean()
    {
        using var tree = await DottedTreeAsync(
            ("config/serilog.json", "{\n  \"Serilog\": {\n    \"MinimumLevel.Default\": \"Information\"\n  }\n}\n"),
            (".gitlab-ci.yml", ".defaults:\n  image: dotnet\n"));
        var queries = await QueriesAsync(tree);
        var key = queries.Card("json:config/appsettings.json#Logging.LogLevel[\"Microsoft.AspNetCore\"]");
        var nested = queries.Card("json:config/appsettings.json#Logging.LogLevel.Microsoft.AspNetCore");
        var level = queries.Card("json:config/serilog.json#Serilog[\"MinimumLevel.Default\"]");

        Assert.Equal([nested.Id, key.Id], queries.DefinersOf("Logging.LogLevel.Microsoft.AspNetCore").Select(card => card.Id));
        Assert.Equal(nested.Hash, Assert.IsType<GraphResolve.Found>(queries.Resolve("Logging.LogLevel.Microsoft.AspNetCore")).Card.Hash);
        Assert.Equal(level.Hash, Assert.IsType<GraphResolve.Found>(queries.Resolve("Serilog.MinimumLevel.Default")).Card.Hash);
        Assert.Equal(["yaml:.gitlab-ci.yml#[\".defaults\"].image"], queries.DefinersOf(".defaults.image").Select(card => card.Id));
        Assert.DoesNotContain(nested.Id, queries.DefinersOf("Logging.LogLevel['Microsoft.AspNetCore']").Select(card => card.Id));
    }

    /// <summary>
    /// A symbol the view does not hold is searched for by its last name, a quoted one's own text whole: the separator a
    /// path is cut at cuts nothing inside a quoted name, in either quote mark.
    /// </summary>
    [Fact]
    public async Task ASymbolTheViewDoesNotHold_IsSearchedByItsLastName_AQuotedNameWhole()
    {
        using var tree = await TreeAsync();
        var tool = Tool(tree, await QueriesAsync(tree));

        Assert.StartsWith("'Logging.LogLevel[\"Nope.Missing\"]' is not a node the view holds\ntext: no line holds 'Nope.Missing'", (await AskAsync(tool, """{"op":"node","symbol":"Logging.LogLevel[\"Nope.Missing\"]"}""")).Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A tree holding a key whose own name holds a dot beside the nested keys of the same dots - the ASP.NET Core template's
    /// logging levels - a literal spelling the key as .NET's configuration does, and an environment variable naming it; with
    /// <paramref name="more"/> beside them.
    /// </summary>
    private static async Task<CodebaseGraphTree> DottedTreeAsync(params (string Path, string Text)[] more)
        => await TreeAsync(
        [
            ("config/appsettings.json", "{\n  \"Logging\": {\n    \"LogLevel\": {\n      \"Microsoft.AspNetCore\": \"Warning\",\n      \"Microsoft\": {\n        \"AspNetCore\": \"Error\"\n      }\n    }\n  }\n}\n"),
            ("src/Logs.cs", "namespace Demo;\n\npublic static class Logs\n{\n    public static string Level(System.Collections.Generic.IDictionary<string, string> config)\n    {\n        return config[\"Logging:LogLevel:Microsoft.AspNetCore\"];\n    }\n}\n"),
            ("deploy/compose.yml", "environment:\n  Logging__LogLevel__Microsoft.AspNetCore: Debug\n"),
            .. more,
        ]);

    /// <summary>
    /// A file whose extract the graph no longer holds - its declarations in no answer - is searched by text and said by why,
    /// each of its lines that holds the data a raw line: the graph's silence of it is never read as the data's absence.
    /// </summary>
    [Fact]
    public async Task Sites_SearchAFileWhoseExtractTheGraphNoLongerHolds_AndSayIt()
    {
        using var tree = await TreeAsync();
        File.Delete(tree.Run.GraphExtractFile(tree.Manifest().Files["src/Geometry.cs"].Extract!));
        var queries = new CodebaseGraphQueries((await tree.ReadOnlyAsync())!);

        var lines = (await AskAsync(Tool(tree, queries, sync: false), """{"op":"sites","data":"Total"}""")).Text.Split('\n');

        Assert.Equal("every place that decides 'Total':", lines[0]);
        Assert.Contains("  src/Geometry.cs:5 public static double Total(IShape[] shapes)", lines);
        Assert.Contains("  src/Geometry.cs:18 return Total([shape, shape]);", lines);
        Assert.Contains("coverage: 1 file(s) whose extract the graph no longer holds, their declarations in no answer (.cs), searched by text - 2 hit(s)", lines);
    }

    /// <summary>
    /// What names a definer by its kind - a parameter's type, an <c>is</c> test, Python's <c>isinstance</c> - and what is built
    /// on it - a type inheriting it - are its places, each saying so, as the <c>node</c> operation lists both edges.
    /// </summary>
    [Fact]
    public async Task Sites_ReachWhatNamesTheDefinerByItsKind_AndWhatInheritsIt()
    {
        using var tree = await TreeAsync(
            ("src/Shipments.cs", "namespace Demo.Shipping;\n\npublic class Shipment\n{\n}\n\npublic sealed class ExpressShipment : Shipment\n{\n}\n\npublic static class Days\n{\n    public static int Of(Shipment shipment) => shipment is ExpressShipment ? 1 : 5;\n}\n"),
            ("shop/__init__.py", ""),
            ("shop/orders.py", "class Order:\n    pass\n\n\nclass ExpressOrder(Order):\n    pass\n"),
            ("shop/ship.py", "from shop.orders import ExpressOrder\n\n\ndef days(o):\n    return 1 if isinstance(o, ExpressOrder) else 5\n"));
        var queries = await QueriesAsync(tree);
        var tool = Tool(tree, queries);

        async Task<string[]> Lines(string data) => (await AskAsync(tool, $$"""{"op":"sites","data":"{{data}}"}""")).Text.Split('\n');

        var shipment = GraphLines.Of(CardOf(queries, "Demo.Shipping.Shipment"));
        var express = GraphLines.Of(CardOf(queries, "ExpressShipment"));
        var days = GraphLines.Of(CardOf(queries, "Days.Of"));

        Assert.Equal(["every place that decides 'Shipment':", "csharp in directory src/ (3):", $"  {shipment} | defines", $"  {express} | inherits", $"  {days} | references", ListedZoo], (await Lines("Shipment"))[..6]);
        Assert.Equal(["every place that decides 'ExpressShipment':", "csharp in directory src/ (2):", $"  {express} | defines", $"  {days} | references", ListedZoo], (await Lines("ExpressShipment"))[..5]);

        // Python's: a test of a value's kind, and a class built on one.
        Assert.Contains(await Lines("ExpressOrder"), line => line.StartsWith($"  {GraphLines.Of(CardOf(queries, "days"))} | references", StringComparison.Ordinal));
        Assert.Contains(await Lines("Order"), line => line.StartsWith($"  {GraphLines.Of(CardOf(queries, "ExpressOrder"))} | inherits", StringComparison.Ordinal));
    }

    /// <summary>
    /// A place's lines never stand on a page without its card: a page that gives anything before a place it cannot hold whole
    /// ends before its card, and the next page gives it whole; a place no page holds is given in parts, the page that ends
    /// inside it saying it goes on and each page after beginning with its card again, said as going on and counted nothing -
    /// every place counted once, every line given once.
    /// </summary>
    [Fact]
    public async Task Sites_GiveAPlacesLinesOnlyBesideItsCard_WholeOnAPageThatHoldsIt_ItsCardAgainOnEachPageAfter()
    {
        const int Long = 900;
        const int Short = 200;
        var code = new StringBuilder("// <auto-generated/>\nnamespace Demo.Big;\n\npublic static class Tally\n{\n    public static int Long(int zebu)\n    {\n        var total = 0;\n");

        code.Append(string.Concat(Enumerable.Repeat("        total += zebu;\n", Long)));
        code.Append("        return total;\n    }\n\n    public static int Short(int zebu)\n    {\n        var total = 0;\n");
        code.Append(string.Concat(Enumerable.Repeat("        total += zebu;\n", Short)));
        code.Append("        return total;\n    }\n}\n");

        using var tree = await TreeAsync(("src/Big.g.cs", code.ToString()));
        var queries = await QueriesAsync(tree);
        var tool = Tool(tree, queries);
        var longCard = $"  {GraphLines.Of(CardOf(queries, "Tally.Long"))} | a line holds it";
        var shortCard = $"  {GraphLines.Of(CardOf(queries, "Tally.Short"))} | a line holds it";
        var shortStart = CardOf(queries, "Tally.Short").Start;

        var first = await AskAsync(tool, """{"op":"sites","data":"zebu"}""");
        var pages = new List<string[]> { first.Text.Split('\n') };

        for (var page = 2; page <= first.Paging!.Pages; page++)
        {
            pages.Add((await AskAsync(tool, $$"""{"op":"sites","data":"zebu","page":{{page}}}""")).Text.Split('\n'));
        }

        Assert.Equal(3, pages.Count);
        Assert.Equal(2, first.Paging.Total);

        // Page 1 ends inside the long place, saying so; page 2 begins with its card again, then goes on with its lines.
        Assert.Equal("… Demo.Big.Tally.Long goes on", pages[0][^1]);
        Assert.Equal(["every place that decides 'zebu':", "csharp in directory src/ (2):", $"{longCard} … goes on"], pages[1][1..4]);

        // The short place, which page 2 has no room left for whole, begins page 3 and is given whole on it.
        Assert.DoesNotContain(pages[1], line => line.StartsWith(shortCard, StringComparison.Ordinal) || line.Contains($"src/Big.g.cs:{shortStart.ToString(CultureInfo.InvariantCulture)} ", StringComparison.Ordinal));
        Assert.Equal(shortCard, pages[2][3]);

        // Every line of a place stands beneath its own card on its page, and every line is given once.
        var given = new List<int>();

        foreach (var lines in pages)
        {
            string? card = null;

            foreach (var line in lines)
            {
                if (line.StartsWith("  ", StringComparison.Ordinal) && !line.StartsWith("      ", StringComparison.Ordinal))
                {
                    card = line;
                }
                else if (line.StartsWith("      src/Big.g.cs:", StringComparison.Ordinal))
                {
                    var at = int.Parse(line["      src/Big.g.cs:".Length..line.IndexOf(' ', "      src/Big.g.cs:".Length)], CultureInfo.InvariantCulture);
                    Assert.NotNull(card);
                    Assert.StartsWith(at < shortStart ? longCard : shortCard, card, StringComparison.Ordinal);
                    given.Add(at);
                }
            }
        }

        Assert.Equal(Long + Short + 2, given.Count);
        Assert.Distinct(given);
    }

    /// <summary>
    /// A file whose name holds a backslash - an ordinary character of a name off Windows - is told by the one path its listing
    /// names it by, where the search takes it, where its hits and its read name it: searched, its hit given, nothing thrown.
    /// </summary>
    [Fact]
    public async Task Sites_ReadAFileWhoseNameHoldsABackslash_ByThePathItsListingNamesItBy()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows cannot hold a backslash in a file name.");

        using var tree = await TreeAsync(("docs/a\\b.txt", "A zebu grazes here.\n"));
        var tool = Tool(tree, await QueriesAsync(tree));

        var answer = await AskAsync(tool, """{"op":"sites","data":"zebu"}""");

        Assert.Equal(GraphToolOutcome.Answered, answer.Outcome);
        Assert.Equal(["every place that decides 'zebu':", ".txt in directory docs/ (1):", "  docs/a\\b.txt:1 A zebu grazes here."], answer.Text.Split('\n')[..3]);
    }

    /// <summary>
    /// A name hundreds of key paths end with - a workflow's every step's <c>name</c> - is searched for by every one of them as an
    /// environment variable writes it, by no pattern however many there are: each line and literal is held to the paths whole,
    /// all of them looked for in one pass of it, so a line that writes one of them is a place and one that writes another name
    /// ending alike is none. No key path is left out, the answer is given, and the coverage line says how many were searched so.
    /// </summary>
    [Fact]
    public async Task Sites_OfANameHundredsOfKeyPathsEndWith_SearchEachAsAnEnvironmentVariableWritesIt_InOnePassOfALine()
    {
        const int Jobs = 300;
        var jobs = new StringBuilder("{\n  \"jobs\": {\n");

        for (var at = 0; at < Jobs; at++)
        {
            jobs.Append(CultureInfo.InvariantCulture, $"    \"job{at}\": {{ \"steps\": [ {{ \"name\": \"step\" }} ] }}{(at < Jobs - 1 ? "," : string.Empty)}\n");
        }

        using var tree = await TreeAsync(
            ("config/jobs.json", jobs.Append("  }\n}\n").ToString()),
            ("deploy/jobs.txt", "jobs__job150__steps__0__name=build\nother__name=nothing\njobs__job150__steps__0__names=nothing\n"),
            ("src/Env.cs", "namespace Demo;\n\npublic static class Env\n{\n    public static string? Step() => System.Environment.GetEnvironmentVariable(\"jobs__job7__steps__0__name\");\n\n    public static string? Other() => System.Environment.GetEnvironmentVariable(\"nobody__name\");\n}\n"));
        var queries = await QueriesAsync(tree);
        var tool = Tool(tree, queries);

        var first = await AskAsync(tool, """{"op":"sites","data":"name"}""");
        var lines = new List<string>(first.Text.Split('\n'));

        for (var page = 2; page <= first.Paging!.Pages; page++)
        {
            var later = await AskAsync(tool, $$"""{"op":"sites","data":"name","page":{{page}}}""");

            Assert.Equal(GraphToolOutcome.Answered, later.Outcome);
            lines.AddRange(later.Text.Split('\n'));
        }

        Assert.Equal(GraphToolOutcome.Answered, first.Outcome);
        Assert.Equal(Jobs, queries.DefinersOf("name").Count(card => card.SubKind == CodeSubKinds.Key));
        Assert.Contains("  deploy/jobs.txt:1 jobs__job150__steps__0__name=build", lines);
        Assert.Contains("      src/Env.cs:5 holds the literal 'jobs__job7__steps__0__name'", lines);
        Assert.Contains(KeyPathsSearched("name", Jobs), lines);

        // Another name ending alike, and a longer word that begins with a key path, are no places.
        Assert.DoesNotContain(lines, line => line.Contains("other__name", StringComparison.Ordinal) || line.Contains("__names", StringComparison.Ordinal) || line.Contains("nobody__name", StringComparison.Ordinal));

        // Every key path is searched, none left out.
        Assert.Equal(Jobs, queries.SitesSearch("name", queries.DefinersOf("name")).Spellings.Count);

        // A key path is held whole whatever characters its places hold, each edge that is a word character at a word's edge.
        var spelt = GraphSitesSearch.Of("timeout", ["my-service__timeout", "Payments__timeout"]);
        string[] held = ["export my-service__timeout=1", "Payments__timeout: 30", "a-Payments__timeout"];
        string[] other = ["xmy-service__timeout", "my-service__timeouts", "MY__timeout", "__timeout"];

        Assert.Equal([.. held.Select(_ => true), .. other.Select(_ => false)], held.Concat(other).Select(spelt.Holds));
        Assert.Throws<ArgumentException>(() => GraphSitesSearch.Of("timeout", ["timeout_seconds"]));
    }

    /// <summary>
    /// A quoted value however long is searched for by its characters alone - no pattern is compiled of it, so none is refused
    /// for its length: a value of twenty thousand characters is answered, a line that holds it whole a place and one that holds
    /// it with a word character after it none. The whole-word rule itself holds a word where its boundaries hold, though an
    /// earlier place of its characters, one that place overlaps, is none.
    /// </summary>
    [Fact]
    public async Task Sites_OfAQuotedValueOfTwentyThousandCharacters_IsSearchedByItsCharacters_AndAnswered()
    {
        var value = string.Concat(Enumerable.Repeat("okapi", 4000));
        using var tree = await TreeAsync(("docs/long.txt", $"{value}\n{value}s\n"));
        var tool = Tool(tree, await QueriesAsync(tree));
        var asked = $$"""{"op":"sites","data":"\"{{value}}\""}""";

        var first = await AskAsync(tool, asked);
        var lines = new List<string>(first.Text.Split('\n'));

        for (var page = 2; page <= (first.Paging?.Pages ?? 1); page++)
        {
            var later = await AskAsync(tool, $$"""{{asked[..^1]}},"page":{{page}}}""");

            Assert.Equal(GraphToolOutcome.Answered, later.Outcome);
            lines.AddRange(later.Text.Split('\n'));
        }

        // One hit of the two lines, zoo.txt holding none: the line the value ends with an 's' after is no place of it.
        Assert.Equal(GraphToolOutcome.Answered, first.Outcome);
        Assert.StartsWith("  docs/long.txt:1 okapiokapi", Assert.Single(lines, line => line.StartsWith("  docs/long.txt:", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("coverage: 2 file(s) the graph lists only (.txt) searched by text - 1 hit(s)", lines);

        // A word is held where its boundaries hold, an earlier place of its characters it overlaps none.
        var word = GraphWord.Whole("abab");
        string[] held = ["ababab abab", "abab.", "(abab)", "abab"];
        string[] other = ["ababab", "xabab", "abab_", "abaabab", "aba"];

        Assert.Equal([.. held.Select(_ => true), .. other.Select(_ => false)], held.Concat(other).Select(line => word.In(line)));
    }

    /// <summary>
    /// A selector is a selector before it is a key path: a BEM element's class, <c>.card__title</c>, is asked of its rule and
    /// searched as a rule writes it and by its name as a class list writes it - a literal that adds the class and a line of
    /// markup that names it are its places - never as the key path <c>.card.title</c>, whose last key would make every word
    /// <c>title</c> a place.
    /// </summary>
    [Fact]
    public async Task ABemSelector_IsSearchedAsTheSelectorItIs_NeverAsAKeyPath()
    {
        using var tree = await TreeAsync(
            ("web/card.css", ".card__title {\n  font-weight: bold;\n}\n"),
            ("web/card.ts", "export function mark(el: HTMLElement): void {\n  el.classList.add('card__title');\n}\n\nexport function heading(): string {\n  return 'title';\n}\n"),
            ("docs/card.txt", "<h2 class=\"card__title\">Cart</h2>\n<title>Shop</title>\n"));
        var queries = await QueriesAsync(tree);
        var tool = Tool(tree, queries);

        var answer = await AskAsync(tool, """{"op":"sites","data":".card__title"}""");
        var lines = answer.Text.Split('\n');

        Assert.Equal(["css:web/card.css#.card__title"], answer.Facts.Asked);
        Assert.Contains($"  {GraphLines.Of(queries.Card("css:web/card.css#.card__title"))} | defines, a line holds it", lines);
        Assert.Contains("      web/card.ts:2 holds the literal 'card__title'", lines);
        Assert.Contains("  docs/card.txt:1 <h2 class=\"card__title\">Cart</h2>", lines);
        Assert.Contains("coverage: '.card__title' searched as a rule writes it and as the name 'card__title', a whole word of a class list", lines);
        Assert.DoesNotContain(lines, line => line.Contains("'title'", StringComparison.Ordinal) || line.Contains("docs/card.txt:2", StringComparison.Ordinal) || line.Contains("environment variable", StringComparison.Ordinal));
        Assert.Equal((".card__title", "card__title"), (queries.SitesSearch(".card__title", queries.DefinersOf(".card__title")).Word, queries.SitesSearch(".card__title", queries.DefinersOf(".card__title")).Selector));
    }

    /// <summary>
    /// A name whose last part holds brackets that are no key path's - C#'s indexer <c>this[]</c>, C++'s <c>operator[]</c> - is
    /// read as code writes it: resolved with its scopes, as main resolved it, and searched for whole, never by the word before
    /// its brackets, so a string that holds the word <c>this</c> is no place of the indexer. A key path's places in brackets are
    /// an item's digits and a quoted name alone.
    /// </summary>
    [Fact]
    public async Task ANameWhoseBracketsAreNoKeyPaths_IsResolvedAndSearchedAsCodeWritesIt()
    {
        using var tree = await TreeAsync(
            ("src/Canvas.cs", "namespace Shapes;\n\npublic sealed class Canvas\n{\n    public int this[int at] => at;\n\n    public string Label() => \"Click this button\";\n}\n"),
            ("src/vec.cpp", "struct Vec\n{\n    double operator[](int at) const\n    {\n        return at;\n    }\n};\n"));
        var queries = await QueriesAsync(tree);
        var tool = Tool(tree, queries);

        Assert.Equal(("this[]", "Shapes.Canvas.this[]"), (CardOf(queries, "Canvas.this[]").Name, CardOf(queries, "Canvas.this[]").Qualified));
        Assert.Equal(("operator[]", "Vec.operator[]"), (CardOf(queries, "Vec::operator[]").Name, CardOf(queries, "Vec::operator[]").Qualified));

        var answer = await AskAsync(tool, """{"op":"sites","data":"Canvas.this[]"}""");

        Assert.Equal([CardOf(queries, "Canvas.this[]").Id], answer.Facts.Asked);
        Assert.Equal("this[]", queries.SitesSearch("Canvas.this[]", queries.DefinersOf("Canvas.this[]")).Word);
        Assert.DoesNotContain(answer.Text.Split('\n'), line => line.Contains("Click this button", StringComparison.Ordinal));

        // The places a path or a name is written of: a bracket opens a place only as a key's path writes one.
        string[] Places(string path) => [.. CodeKeyPaths.Places(path).Select(place => path[place.Start..place.End])];

        Assert.Equal(["Shapes", "Canvas", "this[]"], Places("Shapes.Canvas.this[]"));
        Assert.Equal(["Vec", "operator[]="], Places("Vec.operator[]="));
        Assert.Equal(["limits", "[1]", "value"], Places("limits[1].value"));
        Assert.Equal(["messages", "[\"a.b\\\"]\"]", "text"], Places("messages[\"a.b\\\"]\"].text"));
        Assert.Equal(["this[]", "this[]", "[1]"], new[] { "Shapes.Canvas.this[]", "this[]", "limits[1]" }.Select(CodeKeyPaths.Last));
        Assert.Equal(["this[]", "operator[]", "limits"], new[] { "Shapes.Canvas.this[]", "Vec.operator[]", "limits[1]" }.Select(CodeKeyPaths.LastName));
    }

    /// <summary>
    /// What names a node by itself - its id, a <c>path:line</c> - is never read as a key path, whether or not the view holds
    /// what it names, and neither is a name the data's definers are declarations of, none a key - a constant code declares as
    /// <c>RETRY__LIMIT</c> is searched for whole, never as the path <c>RETRY.LIMIT</c>'s last key: none is searched as an
    /// environment variable writes a path, and the coverage lines say none was. A key path spelt so that names a key is.
    /// </summary>
    [Fact]
    public async Task Sites_ByANodeOrAFilesLine_OrANameCodeDeclares_NeverSearchItAsAKeyPath()
    {
        using var tree = await TreeAsync(
            ("config/app.json", "{\n  \"Payments\": {\n    \"TimeoutSeconds\": 30\n  }\n}\n"),
            ("src/Retry.cs", "namespace Demo;\n\npublic static class Retry\n{\n    public const int RETRY__LIMIT = 3;\n}\n"));
        var queries = await QueriesAsync(tree);
        var tool = Tool(tree, queries);

        foreach (var data in new[] { "csharp:Demo.Circle.Area()", "src/Shapes.cs:14", "csharp:Demo.Gone()", "src/Gone.cs:7", "RETRY__LIMIT" })
        {
            var lines = (await AskAsync(tool, $$"""{"op":"sites","data":"{{data}}"}""")).Text.Split('\n');

            Assert.DoesNotContain(lines, line => line.Contains("environment variable", StringComparison.Ordinal));
            Assert.Empty(queries.SitesSearch(data, queries.DefinersOf(data)).Spellings);
        }

        var limit = await AskAsync(tool, """{"op":"sites","data":"RETRY__LIMIT"}""");

        Assert.Equal("RETRY__LIMIT", queries.SitesSearch("RETRY__LIMIT", queries.DefinersOf("RETRY__LIMIT")).Word);
        Assert.Contains($"  {GraphLines.Of(CardOf(queries, "Retry.RETRY__LIMIT"))} | defines", limit.Text.Split('\n'));
        Assert.Contains(KeyPathsSearched("TimeoutSeconds", 1), (await AskAsync(tool, """{"op":"sites","data":"Payments:TimeoutSeconds"}""")).Text.Split('\n'));
    }

    /// <summary>
    /// A name a definer, none of them a key, is found by as it is written - with some of its scopes, a double underscore in its
    /// last part - is searched for as that name, never as the key path it would spell: <c>Retry.RETRY__LIMIT</c> by
    /// <c>RETRY__LIMIT</c>, so a Dockerfile's <c>ENV RETRY__LIMIT</c> line and a literal of it are its places and a query's
    /// <c>LIMIT 10</c> is none, and Python's <c>Foo.__validate</c> by <c>__validate</c>, so its call is a place and an unrelated
    /// <c>validate</c> is none; a key path whose definers only the path it spells finds is still read as that path.
    /// </summary>
    [Fact]
    public async Task ANameADefinerIsFoundByAsItIsWritten_IsSearchedAsThatName_NeverAsTheKeyPathItWouldSpell()
    {
        using var tree = await TreeAsync(
            ("src/Retry.cs", "namespace Demo;\n\npublic static class Retry\n{\n    public const int RETRY__LIMIT = 3;\n\n    public static string Name() => \"RETRY__LIMIT\";\n}\n"),
            ("src/JwtSettings.cs", "namespace Api;\n\npublic sealed class JwtSettings\n{\n    public string Secret { get; set; } = \"\";\n}\n"),
            ("app/foo.py", "class Foo:\n    def __validate(self):\n        return True\n\n    def run(self):\n        return self.__validate()\n"),
            ("app/check.py", "def validate(order):\n    return order\n"),
            ("Dockerfile", "FROM base\nENV RETRY__LIMIT=5\n"),
            ("db/report.sql", "SELECT * FROM t LIMIT 10;\n"));
        var queries = await QueriesAsync(tree);
        var tool = Tool(tree, queries);

        GraphSitesSearch SearchOf(string data) => queries.SitesSearch(data, queries.DefinersOf(data));

        Assert.Equal(("RETRY__LIMIT", 0), (SearchOf("Retry.RETRY__LIMIT").Word, SearchOf("Retry.RETRY__LIMIT").Spellings.Count));
        Assert.Equal(("__validate", 0), (SearchOf("Foo.__validate").Word, SearchOf("Foo.__validate").Spellings.Count));
        Assert.Equal("Secret", SearchOf("JwtSettings:Secret").Word);
        Assert.Equal(["JwtSettings__Secret"], SearchOf("JwtSettings:Secret").Spellings);

        // The ENV line and the literal by the name whole, the query's LIMIT none of them, and no key path searched.
        Assert.Equal(
            [
                "every place that decides 'Retry.RETRY__LIMIT':",
                "Dockerfile in directory . (1):",
                "  Dockerfile:2 ENV RETRY__LIMIT=5",
                "csharp in directory src/ (2):",
                $"  {GraphLines.Of(CardOf(queries, "Retry.RETRY__LIMIT"))} | defines",
                $"  {GraphLines.Of(CardOf(queries, "Retry.Name"))} | holds the literal",
                "      src/Retry.cs:7 holds the literal 'RETRY__LIMIT'",
                "coverage: 3 file(s) the graph lists only (.sql, .txt, Dockerfile) searched by text - 1 hit(s)",
                "coverage: 2 file(s) the graph is unsure of, searched by text too - 0 hit(s)",
                OutlinedReadme,
                NoExtractor,
            ],
            (await AskAsync(tool, """{"op":"sites","data":"Retry.RETRY__LIMIT"}""")).Text.Split('\n')[..11]);
    }

    /// <summary>
    /// A key path spelt as .NET's configuration spells one whose definers its path finds - code's members, none a key, as the
    /// options pattern binds a section to a class - is read as that key path in either spelling: searched for by its last key,
    /// as code writes it, and by its places as an environment variable writes them - a Dockerfile's <c>ENV</c> line, a literal
    /// that writes the path with a colon - and a file holding a reference of that last name the graph bound to nothing is
    /// searched for the same word, never for the data as it was typed, which no line of code holds.
    /// </summary>
    [Fact]
    public async Task AKeyPathWhoseDefinersItsPathFindsAreCodesMembers_IsSearchedAsThatKeyPath()
    {
        using var tree = await TreeAsync(
            ("src/JwtSettings.cs", "namespace Api;\n\npublic sealed class JwtSettings\n{\n    public string Secret { get; set; } = \"\";\n}\n"),
            ("src/Program.cs", "namespace Api;\n\npublic static class Program\n{\n    public static string Key(Microsoft.Extensions.Options.IOptions<JwtSettings> options)\n    {\n        return options.Value.Secret;\n    }\n\n    public static string Raw(Microsoft.Extensions.Configuration.IConfiguration config)\n    {\n        return config[\"JwtSettings:Secret\"];\n    }\n}\n"),
            ("Dockerfile", "FROM base\nENV JwtSettings__Secret=change-me\n"));
        var queries = await QueriesAsync(tree);
        var tool = Tool(tree, queries);

        foreach (var data in new[] { "JwtSettings:Secret", "JwtSettings__Secret" })
        {
            var definers = queries.DefinersOf(data);
            var search = queries.SitesSearch(data, definers);
            var answer = await AskAsync(tool, $$"""{"op":"sites","data":"{{data}}"}""");
            var lines = answer.Text.Split('\n');

            Assert.Equal(["csharp:Api.JwtSettings.Secret"], definers.Select(card => card.Id));
            Assert.Equal("Secret", search.Word);
            Assert.Equal(["JwtSettings__Secret"], search.Spellings);
            Assert.Equal(["csharp:Api.JwtSettings.Secret"], answer.Facts.Asked);

            // The ENV line by the path's spelling, the unbound reference and the literal by the last key: both of Program.cs's lines
            // are the hits of the file taken for its reference of that name.
            Assert.Equal(
                [
                    $"every place that decides '{data}':",
                    "Dockerfile in directory . (1):",
                    "  Dockerfile:2 ENV JwtSettings__Secret=change-me",
                    "csharp in directory src/ (3):",
                    $"  {GraphLines.Of(CardOf(queries, "JwtSettings.Secret"))} | defines",
                    $"  {GraphLines.Of(CardOf(queries, "Program.Key"))} | a line holds it",
                    "      src/Program.cs:7 return options.Value.Secret;",
                    $"  {GraphLines.Of(CardOf(queries, "Program.Raw"))} | holds the literal",
                    "      src/Program.cs:12 holds the literal 'JwtSettings:Secret'",
                    "coverage: 2 file(s) the graph lists only (.txt, Dockerfile) searched by text - 1 hit(s)",
                    "coverage: 2 file(s) the graph is unsure of, searched by text too - 0 hit(s)",
                    OutlinedReadme,
                    "coverage: 1 file(s) holding a reference named 'Secret' that the graph bound to nothing of that name, searched by text too - 2 hit(s)",
                    KeyPathsSearched("Secret", 1),
                    NoExtractor,
                ],
                lines[..15]);
        }
    }

    /// <summary>
    /// A node declared in many files - a namespace every file opens - is looked up once in each file's line index and once for
    /// each card of it as a file declares it, from an index of every declaration made once: a file's index costs its own
    /// nodes, never every other file the node is declared in, so the indexes of every file cost the files' nodes, never their
    /// square.
    /// </summary>
    [Fact]
    public async Task EachFilesLineIndex_LooksUpEachNodeItSharesOnce_HoweverManyFilesDeclareIt()
    {
        const int Files = 40;
        using var tree = await TreeAsync([.. Enumerable.Range(0, Files).Select(at => ($"wide/W{at}.cs", $"namespace Wide;\n\npublic sealed class C{at}\n{{\n}}\n"))]);
        var queries = await QueriesAsync(tree);

        Assert.Equal(Files, queries.Parts(CardOf(queries, "Wide").Id).Count);

        var before = queries.DeclarationsRead;

        for (var at = 0; at < Files; at++)
        {
            Assert.Equal($"Wide.C{at}", queries.AtLine($"wide/W{at}.cs", 3)!.Qualified);
        }

        // Each file's index looks up each of its three nodes - its own, the namespace, its class - and its card one more.
        Assert.Equal(Files * 4, queries.DeclarationsRead - before);
    }
}
