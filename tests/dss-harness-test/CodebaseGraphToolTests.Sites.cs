using System.Globalization;
using System.Text;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;
using DssHarness.Core.Help;

namespace DssHarness.Tests;

public sealed partial class CodebaseGraphToolTests
{
    /// <summary>What a <c>sites</c> call's data is, as the tool's schema and every usage line about it say it.</summary>
    internal const string DataDescription = "the data whose deciding places are asked: a symbol as symbol accepts one - a node's handle, its id, a path:line, or a qualified or simple name - a key path or a diagnostic id, a rule's selector as a rule writes it (.btn, #main), asked by its name as a class list writes it too, or a quoted literal, asked by its value alone.";

    /// <summary>The coverage line every answer that searched a file the graph lists only ends its coverage with.</summary>
    private const string NoExtractor = "coverage: nothing is assumed of a language with no extractor; those files are read by text alone";

    /// <summary>The coverage line of the one markdown file every tree of these tests holds, its README, which the graph outlines.</summary>
    private const string OutlinedReadme = "coverage: 1 file(s) the graph outlines (.md), their values and their text held by no literal of it, searched by text too - 0 hit(s)";

    /// <summary>The coverage line of the one file every tree of these tests holds that the graph lists only, its zoo.txt, where it holds no hit.</summary>
    private const string ListedZoo = "coverage: 1 file(s) the graph lists only (.txt) searched by text - 0 hit(s)";

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
    /// places left out are counted; where every place the graph holds of the data is in such a file, the answer says so.
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
                fallback,
                "2 place(s) left out: in files your reads do not reach",
            ],
            named.Text.Split('\n'));
        Assert.Equal(["csharp:Demo.Circle.Area()", "csharp:Demo.Generated.Square.Area()", "csharp:Demo.IShape.Area()"], named.Facts.Asked);

        // A literal a file the reads do not reach holds is no place shown, and is counted.
        Assert.DoesNotContain(quoted, line => line.Contains("src/Keys.cs", StringComparison.Ordinal));
        Assert.Equal("1 place(s) left out: in files your reads do not reach", quoted[^1]);

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
        var fallback = (await AskAsync(tool, """{"op":"find","query":"Okapi"}""")).Text.Split('\n');
        var guide = queries.AtLine("docs/guide.md", 3)!;

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

        // The pattern itself: the selector with no character of a class's name after it, and its name a whole word of a class list.
        var pattern = CodebaseGraphQueries.SitesPattern(".btn-primary");

        Assert.All(new[] { "button.btn-primary", ".btn-primary {", "class=\"btn btn-primary\"", "addClass('btn-primary')", "btn-primary" }, held => Assert.Matches(pattern, held));
        Assert.All(new[] { ".btn-primary-outline", "btn-primary-outline", "my-btn-primary", "class=\"btn-primary2\"" }, other => Assert.DoesNotMatch(pattern, other));

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
    /// language: a limit whose sign the files read show - the kind of a dynamic mark one holds, a reference the linker left
    /// candidates, or one it counted external - in full, as its help says it, with how many show it; and every other by its short
    /// name, on one line for the language that points to <c>help codebase-graph-languages</c>, which says each in full under that
    /// name - nothing left unsaid, and the answer small. Read before what the answer says of their files is trusted.
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
        // in every tree of these tests, and C#'s limits are said by the one rule too.
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
                "csharp also does not see ('help codebase-graph-languages' says each): preprocessor-branches, untyped-values, local-functions, dynamic-calls, generic-extensions, untyped-reads, generated-sources, same-named-types, assembly-attributes, huge-files.",
                "markdown also does not see ('help codebase-graph-languages' says each): fenced-headings, html-headings.",
                $"python does not see {Says("computed attribute access")} - shown by 1 file(s) read.",
                "python also does not see ('help codebase-graph-languages' says each): dynamic-imports, unseen-roots, monkey-patching, exec-eval, duck-typing, capital-functions, computed-all, unclosed, module-getattr, unbound-names, outside-fixtures, lambdas, test-helpers, lowercase-annotations, outside-attributes, manifests.",
            ],
            Tail(computed));
        Assert.All(
            CodeGraphExtractors.OfThisBuild.Find("python")!.Help.DoesNotSee.Where(limit => !limit.Says.StartsWith("computed attribute access", StringComparison.Ordinal)),
            limit => Assert.Contains($"    {limit.Name}: does not see", HelpTopics.Render(HelpTopics.CodebaseGraphLanguages), StringComparison.Ordinal));

        // What makes each shown: the signs the limits name - the kind of the mark the one file holds, candidates the other is left.
        var limits = CodeGraphExtractors.OfThisBuild.Find("python")!.Help.DoesNotSee;

        Assert.Equal([PythonReading.ComputedAttribute], Assert.Single(limits, limit => limit.Says.StartsWith("computed attribute access", StringComparison.Ordinal)).Signs);
        Assert.Equal([CodeGraphLimit.Candidates, PythonReading.FunctionValue], Assert.Single(limits, limit => limit.Says.StartsWith("a duck-typed receiver", StringComparison.Ordinal)).Signs);

        // Every limit whose sign the code can give: the two above, and the others the reader marks or the linker counts.
        Assert.Equal(9, limits.Count(limit => limit.Signs.Count > 0));

        // A file whose bracket never closes, a file whose call the linker bound to nothing the tree declares - counted external, the
        // sign of each limit that names a way the graph does not read - and a page another template engine writes: each shows the
        // limits its sign names, of its own language.
        using var signed = await TreeAsync(
            ("shop/total.py", "def total(cart):\n    return sum(\n"),
            ("shop/rates.py", "from vendor.rates import convert\n\n\ndef rate():\n    return convert(1)\n"),
            ("web/page.html", "<main>\n  <p>{% if user %}Hello{% endif %}</p>\n</main>\n"));
        var shown = Tail((await AskAsync(Tool(signed, await QueriesAsync(signed)), """{"op":"sites","data":"total"}""")).Text.Split('\n'));
        var engine = Assert.Single(CodeGraphExtractors.OfThisBuild.Find("html")!.Help.DoesNotSee, limit => limit.Signs.Contains(HtmlExtractor.OtherEngine)).Says;

        Assert.Equal(
            [.. LimitsOf("csharp", [CodeGraphLimit.Candidates]), .. LimitsOf("html", [HtmlExtractor.OtherEngine]), .. LimitsOf("markdown"), .. LimitsOf("python", [PythonReading.Unclosed], [CodeGraphLimit.External])],
            shown);
        Assert.Contains($"html does not see {engine} - shown by 1 file(s) read.", shown);
        Assert.All(
            new[] { "never closes", "a root of the path", "neither declares nor imports", "no conftest.py" },
            names => Assert.Contains($"python does not see {Says(names)} - shown by 1 file(s) read.", shown));
        Assert.Equal([PythonReading.Unclosed], Assert.Single(limits, limit => limit.Says.Contains("never closes", StringComparison.Ordinal)).Signs);
        Assert.Equal(3, limits.Count(limit => limit.Signs.Contains(CodeGraphLimit.External)));
    }

    /// <summary>The words of the one Python limit whose clause holds <paramref name="names"/>, as its help says them.</summary>
    private static string Says(string names)
        => Assert.Single(CodeGraphExtractors.OfThisBuild.Find("python")!.Help.DoesNotSee, limit => limit.Says.Contains(names, StringComparison.Ordinal)).Says;

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
    /// The node at a line - a text hit's card, a <c>path:line</c>'s node - is the innermost: of nodes with the same lines, the
    /// one declared inside the other, never one of them by the order of their ids - a page's landmark before the body of the
    /// same lines, a type's only member on its line before the type, and a Vue component that spans its whole file before the
    /// file's own node, whose id the order would put first - and of those no other is declared inside, the one that makes a
    /// reference at the line: a Razor page's markup, not the class it compiles to, whose id the order would put first.
    /// </summary>
    [Fact]
    public async Task TheNodeAtALine_IsTheInnermost_OfNodesWithTheSameLinesTheOneDeclaredInside()
    {
        using var tree = await TreeAsync(
            ("web/page.html", "<main>\n  <p>Okapi</p>\n</main>\n"),
            ("src/One.cs", "namespace Demo;\n\npublic sealed class One { public int Two() => 2; }\n"),
            ("src/Cart.vue", "<template>\n  <p>{{ total() }}</p>\n</template>\n<script setup>\nfunction total() { return 1; }\n</script>\n"),
            ("src/CartService.cs", "namespace Shop;\n\npublic sealed class CartService\n{\n    public int Total() => 0;\n}\n"),
            ("src/Pages/_Total.cshtml", "@model Shop.CartService\n\n<p>Total: @Model.Total()</p>\n"));
        var queries = await QueriesAsync(tree);

        Assert.Equal("landmark", queries.AtLine("web/page.html", 2)!.SubKind);
        Assert.Equal("Two", queries.AtLine("src/One.cs", 3)!.Name);
        Assert.Equal(("vue:src/Cart.vue", CodeNodeKinds.Type), (queries.AtLine("src/Cart.vue", 2)!.Id, queries.AtLine("src/Cart.vue", 2)!.Kind));
        Assert.Equal("razor:src/Pages/_Total.cshtml#view", queries.AtLine("src/Pages/_Total.cshtml", 3)!.Id);
    }

    /// <summary>
    /// What a search says it left out of the files the reader may not read is the caller's count where it gives one - a
    /// search that passed over readable files by its own will says fewer, and none where none is left - and the search's own
    /// where it gives none.
    /// </summary>
    [Fact]
    public void ASearchsLeftOutFiles_AreTheCallersCount_AndNoneWhereItIsNone()
    {
        var search = new AgentSearch([], 0, Refused: 5, Passed: 0, Searched: 3, new AgentListing([], 0, 0, null), 0, null);

        Assert.Empty(search.LeftOut(path => path, refused: 0));
        Assert.Equal(["[2 file(s) you may not read were left out]"], search.LeftOut(path => path, refused: 2));
        Assert.Equal(["[5 file(s) you may not read were left out]"], search.LeftOut(path => path));
    }
}
