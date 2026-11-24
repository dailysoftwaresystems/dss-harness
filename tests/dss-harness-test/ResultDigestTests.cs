using System.Globalization;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Platform;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// Results digested: the digest folds the same diagnostic printed again and holds to its bounds, marks every frame by
/// whether it is the tree's, says itself whole as one document, and a step attempt the engine did not expect keeps the
/// exception's record. The parsers' own reading is held by ResultParsersTests and the fixtures.
/// </summary>
public sealed class ResultDigestTests
{

    /// <summary>MSBuild folds the same error printed once per target framework into one entry with its count.</summary>
    [Fact]
    public void TheSameDiagnosticPrintedAgain_IsFoldedIntoOneWithItsCount()
    {
        var digest = ResultDigest.Of(
        [
            new Diagnostic("msbuild", "error", "CS0103", "The name 'x' does not exist in the current context", "src/Program.cs", 12, 35, Project: "src/App.csproj"),
            new Diagnostic("msbuild", "error", "CS0103", "The name 'x' does not exist in the current context", "src/Program.cs", 12, 35, Project: "src/App.iOS.csproj"),
            new Diagnostic("msbuild", "warning", "CS0219", "The variable 'y' is assigned but its value is never used", "src/Program.cs", 20, 13, Project: "src/App.csproj"),
        ],
            [],
            []);

        Assert.Equal(2, digest.Diagnostics.Count);
        Assert.Equal(2, digest.Diagnostics[0].Count);
        Assert.Equal(1, digest.Diagnostics[1].Count);
        Assert.Equal(1, digest.Errors);
        Assert.Equal(1, digest.Warnings);
        Assert.Equal(digest.Diagnostics[0], digest.First);
    }





    /// <summary>A stack longer than the bound keeps its first frames and its last, with the cut said.</summary>
    [Fact]
    public void AStackOverTheBound_KeepsItsFirstAndLastFrames()
    {
        var frames = Enumerable.Range(0, 100).Select(at => new DssHarness.Core.Results.StackFrame($"at Frame{at}()")).ToList();

        var held = StackTrace.Held(frames, out var cut);

        Assert.Equal(StackTrace.FrameBound, held.Count);
        Assert.Equal(36, cut);
        Assert.Equal("at Frame0()", held[0].Text);
        Assert.Equal("at Frame99()", held[^1].Text);
        Assert.Equal("at Frame47()", held[StackTrace.FirstKept - 1].Text);
        Assert.Equal("at Frame84()", held[StackTrace.FirstKept].Text);
    }



    /// <summary>The digest's bounds: at most 50 diagnostics and 50 failures, the rest folded and counted.</summary>
    [Fact]
    public void TheDigest_HoldsToItsBounds()
    {
        var diagnostics = Enumerable.Range(0, 80).Select(at => new Diagnostic("msbuild", "error", $"CS{at:0000}", $"message {at}", "src/x.cs", at + 1)).ToList();
        var digest = ResultDigest.Of(diagnostics, [], []);

        Assert.Equal(ResultDigest.Bound, digest.Diagnostics.Count);
        Assert.Equal(80, digest.Errors);
    }

    /// <summary>A step attempt the engine did not expect keeps the exception's record - type, message, stack, cause.</summary>
    [Fact]
    public void AnUnexpectedException_IsKeptOnTheAttempt()
    {
        var attempt = new FlowStepAttempt(1, Exception: new DssHarness.Core.Results.ExceptionRecord(
            "System.InvalidOperationException",
            "The operation is invalid.",
            [new DssHarness.Core.Results.StackFrame("   at N.C.M() in src/x.cs:line 12", "src/x.cs", 12, Symbol: "N.C.M")],
            [new DssHarness.Core.Results.ExceptionRecord("System.ArgumentException", "The argument was wrong.", [], [])]));

        Assert.Null(attempt.Problem());
        Assert.Equal("System.InvalidOperationException", attempt.Exception!.Type);
        Assert.Equal("src/x.cs", attempt.Exception.Stack[0].File);
        Assert.Equal("System.ArgumentException", attempt.Exception.CausedBy[0].Type);
    }

    /// <summary>
    /// Every frame of every stack is marked by whether the tree holds its file, on the machine's own file system: a rooted
    /// file under the tree and a relative one read from where the tool ran are in it, named relative to its root; a file
    /// the tree does not hold under it, and a rooted file elsewhere, are not - the one place a frame is marked, so every
    /// reader reads one.
    /// </summary>
    [Fact]
    public void EveryFrameOfEveryStack_IsMarkedByWhetherTheTreeHoldsItsFile()
    {
        using var temp = new TempDirectory();
        var main = temp.WriteFile(Path.Combine("src", "main.c"), "int main;");
        var elsewhere = Path.Combine(Path.GetPathRoot(temp.Path) ?? "/", "elsewhere", "out.c");
        var digest = ResultDigest.Of(
            [new Diagnostic("gcc", "error", null, "jammed", "src/main.c", 3, 9, Stack:
            [
                new StackFrame("in main.c", main, 3),
                new StackFrame("from ../src/main.c", "../src/main.c", 1),
                new StackFrame("in src/gone.c", Path.Combine(temp.Path, "src", "gone.c"), 12),
                new StackFrame("elsewhere", elsewhere, 1),
            ])],
            [],
            []);

        var marked = digest.WithInTree(new DigestTree(temp.Path, temp.Combine("build"), new PhysicalFileSystem(FilePermissionsFactory.Create())));

        Assert.Equal(
            [("src/main.c", true), ("src/main.c", true), (Path.Combine(temp.Path, "src", "gone.c"), false), (elsewhere, false)],
            marked.Diagnostics[0].Stack.Select(frame => (frame.File, frame.InTree)));
    }

    /// <summary>
    /// A Go test's place is read from its package's directory, for <c>go test</c> prints it relative to that directory and
    /// names the package by its import path: the import path less the module path of the <c>go.mod</c> nearest the tree's
    /// root that owns it - a module at the root, one in a directory of its own below it, its path quoted, in a block or
    /// beside a comment as Go reads them; the root's module over a copy of the tree inside it that holds the same; of two
    /// equally near, the longer module path, as Go gives a package to the most specific module - and a module path owns an
    /// import path only at a part's end. Where no <c>go.mod</c> owns the package, or no package was named, the place stays
    /// as printed: never read from where the tool ran, though the file is there.
    /// </summary>
    [Fact]
    public void AGoTestsPlace_IsReadFromItsPackagesDirectory_ThroughTheModuleNearestTheRootThatOwnsIt()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("go.mod", "// The application.\nmodule example.com/app // its path\n\ngo 1.22\n");
        temp.WriteFile("store/store_test.go");
        temp.WriteFile(".legs/copy/go.mod", "module example.com/app\n");
        temp.WriteFile(".legs/copy/store/store_test.go");
        temp.WriteFile("tools/go.mod", "module \"example.com/tools\"\n");
        temp.WriteFile("tools/gen/gen_test.go");
        temp.WriteFile("lib/go.mod", "module (\n\texample.com/lib\n)\n");
        temp.WriteFile("lib/lib_test.go");
        temp.WriteFile("mono/go.mod", "module example.com/mono\n");
        temp.WriteFile("mono/b/z/z_test.go");
        temp.WriteFile("nested/go.mod", "module example.com/mono/b\n");
        temp.WriteFile("nested/z/z_test.go");
        temp.WriteFile("lication/x/x_test.go");
        temp.WriteFile("cmd/parse_test.go");
        temp.WriteFile("cmd/cut_test.go");

        // The first asks of a package no module owns, so the whole tree is read before the others are asked.
        var marked = ResultDigest.Of(
            [],
            [
                new TestFailure("go", "TestX", Class: "example.com/application/x", File: "x_test.go", Line: 3),
                new TestFailure("go", "TestStoreLoads", Class: "example.com/app/store", File: "store_test.go", Line: 14),
                new TestFailure("go", "TestGen", Class: "example.com/tools/gen", File: "gen_test.go", Line: 5),
                new TestFailure("go", "TestLib", Class: "example.com/lib", File: "lib_test.go", Line: 2),
                new TestFailure("go", "TestZ", Class: "example.com/mono/b/z", File: "z_test.go", Line: 7),
                new TestFailure("go", "TestParse", Class: "example.org/parse", File: "parse_test.go", Line: 9),
                new TestFailure("go", "TestCut", File: "cut_test.go", Line: 4),
            ],
            [])
            .WithInTree(new DigestTree(temp.Path, temp.Combine("cmd"), new PhysicalFileSystem(FilePermissionsFactory.Create())));

        Assert.Equal(
            [
                ("TestX", "x_test.go", (int?)3),
                ("TestStoreLoads", "store/store_test.go", 14),
                ("TestGen", "tools/gen/gen_test.go", 5),
                ("TestLib", "lib/lib_test.go", 2),
                ("TestZ", "nested/z/z_test.go", 7),
                ("TestParse", "parse_test.go", 9),
                ("TestCut", "cut_test.go", 4),
            ],
            marked.Failures.Select(failure => (failure.Id, failure.File!, failure.Line)));
    }

    /// <summary>
    /// A Dart frame that names a package's file - <c>package:demo/store.dart</c> - is read as Dart reads it: through the
    /// package configuration of the package the tool ran in, the <c>.dart_tool/package_config.json</c> nearest above where
    /// it ran - a workspace's further up is not read where a nearer one is - each package's root read against the
    /// configuration's own directory, and its file below the package's directory of its URIs; the failure's place is that
    /// frame, the throw site, never its caller. A package the configuration places outside the tree - the pub cache's - a
    /// package it does not name, a file that is not there and the runtime's own library are in none and stay as written,
    /// and nothing of the tree is listed for any of them.
    /// </summary>
    [Fact]
    public void ADartFrameNamingAPackage_IsReadThroughThePackageConfigurationOfWhereTheToolRan()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("app/pubspec.yaml", "name: demo\n");
        temp.WriteFile("app/lib/store.dart");
        temp.WriteFile("app/test/store_test.dart");
        temp.WriteFile(
            "app/.dart_tool/package_config.json",
            """
            {
              "configVersion": 2,
              "packages": [
                { "name": "test_api", "rootUri": "file:///home/runner/.pub-cache/hosted/pub.dev/test_api-0.7.3", "packageUri": "lib/", "languageVersion": "3.4" },
                { "name": "demo_tools", "rootUri": "../../tools", "packageUri": "lib/", "languageVersion": "3.5" },
                { "name": "demo", "rootUri": "../", "packageUri": "lib/", "languageVersion": "3.5" }
              ],
              "generator": "pub",
              "generatorVersion": "3.5.0"
            }
            """);
        temp.WriteFile("tools/pubspec.yaml", "name: demo_tools\n");
        temp.WriteFile("tools/lib/gen.dart");

        // A workspace's configuration above the package's own, which a nearer one leaves unread: were it read, demo's files
        // would be the tools package's, where no store.dart is.
        temp.WriteFile(".dart_tool/package_config.json", """{ "configVersion": 2, "packages": [{ "name": "demo", "rootUri": "../tools/", "packageUri": "lib/" }] }""");
        temp.WriteFile(".legs/copy/app/lib/store.dart");
        var files = new CountedListings();

        var stack = new List<StackFrame>
        {
            new("#0 expect (package:test_api/expect.dart:5:3)", "package:test_api/expect.dart", 5),
            new("#1 Store.load (package:demo/store.dart:14:7)", "package:demo/store.dart", 14),
            new("#2 gen (package:demo_tools/gen.dart:3:1)", "package:demo_tools/gen.dart", 3),
            new("#3 gone (package:demo/gone.dart:2:1)", "package:demo/gone.dart", 2),
            new("#4 other (package:other/other.dart:1:1)", "package:other/other.dart", 1),
            new("#5 _throw (dart:core/errors.dart:9:1)", "dart:core/errors.dart", 9),
        };
        var marked = ResultDigest.Of([], [new TestFailure("dart", "Bad state: no record", Exception: new ExceptionRecord("StateError", "no record", stack, []))], [])
            .WithInTree(new DigestTree(temp.Path, temp.Combine("app", "test"), files));

        Assert.Equal(
            [
                ("package:test_api/expect.dart", false),
                ("app/lib/store.dart", true),
                ("tools/lib/gen.dart", true),
                ("package:demo/gone.dart", false),
                ("package:other/other.dart", false),
                ("dart:core/errors.dart", false),
            ],
            marked.Failures[0].Exception!.Stack.Select(frame => (frame.File!, frame.InTree)));
        Assert.Equal(("app/lib/store.dart", (int?)14), (marked.Failures[0].File, marked.Failures[0].Line));
        Assert.Empty(files.Listed);
    }

    /// <summary>
    /// The declarations of the tree's graph are joined where the graph is, at the digest's places in the tree alone: a
    /// diagnostic's symbol is the declaration enclosing its line - its kind, its qualified name and its handle, as the graph
    /// names a node - and each frame the placing marked the tree's, a diagnostic's chain and a failure's stack down its
    /// causes, holds it beside the function its line printed, the record saying both. A frame outside the tree and a
    /// diagnostic's file written outside the root are never asked, each place is asked once however many findings name it,
    /// and a place the graph holds no declaration at says none.
    /// </summary>
    [Fact]
    public void TheGraphsDeclarations_AreJoinedAtTheDigestsPlacesInTheTree_EachAskedOnce()
    {
        var asked = new List<string>();
        var digest = ResultDigest.Of(
            [
                new Diagnostic("msbuild", "error", "CS0103", "The name 'x' does not exist in the current context", "src/Program.cs", 12, 35, Log: (10, 11)),
                new Diagnostic("gcc", "error", null, "jammed", "/usr/include/stdio.h", 3, Stack: [new StackFrame("In file included from src/main.c:2:", "src/main.c", 2, Kind: "include", InTree: true)]),
            ],
            [
                new TestFailure(
                    "xunit",
                    "App.Tests.Holds",
                    Exception: new ExceptionRecord(
                        "System.InvalidOperationException",
                        "no record",
                        [
                            new StackFrame("at App.Tests.Holds() in src/Program.cs:12", "src/Program.cs", 12, Symbol: "App.Tests.Holds", InTree: true),
                            new StackFrame("at System.Text.StringBuilder.Append(char)", "mscorlib.cs", 123, Symbol: "System.Text.StringBuilder.Append"),
                            new StackFrame("in src/Other.cs:3", "src/Other.cs", 3, InTree: true),
                        ],
                        [new ExceptionRecord("System.ArgumentException", "inner", [new StackFrame("at Inner() in src/Other.cs:9", "src/Other.cs", 9, Symbol: "Inner", InTree: true)], [])])),
            ],
            []);

        var declared = digest.Declared((file, line) =>
        {
            asked.Add($"{file}:{line.ToString(CultureInfo.InvariantCulture)}");

            return (file, line) switch
            {
                ("src/Program.cs", 12) => new EnclosingDeclaration("member", "App.Tests.Holds", "4f2a9b"),
                ("src/Other.cs", 9) => new EnclosingDeclaration("type", "App.Other", "9b7c3d"),
                ("src/main.c", 2) => new EnclosingDeclaration("member", "main", "1a2b3c"),
                _ => null,
            };
        });

        Assert.Equal(new string?[] { "member App.Tests.Holds 4f2a9b", null }, declared.Diagnostics.Select(diagnostic => diagnostic.Symbol));
        Assert.Equal(new EnclosingDeclaration("member", "main", "1a2b3c"), Assert.Single(declared.Diagnostics[1].Stack).Declaration);

        var thrown = declared.Failures[0].Exception!;

        // The function as printed stays the frame's own; the record says the declaration beside it.
        Assert.Equal(new string?[] { "App.Tests.Holds", "System.Text.StringBuilder.Append", null }, thrown.Stack.Select(frame => frame.Symbol));
        Assert.Equal(new string?[] { "App.Tests.Holds [member App.Tests.Holds 4f2a9b]", "System.Text.StringBuilder.Append", null }, thrown.Stack.Select(frame => frame.SymbolSaid));
        Assert.Equal(
            "Inner [type App.Other 9b7c3d]",
            declared.Document()["failures"]![0]!["exception"]!["causedBy"]![0]!["stack"]![0]!["symbol"]!.GetValue<string>());

        // Only the places in the tree, each once: the diagnostic and the frame name the same place.
        Assert.Equal(["src/Program.cs:12", "src/main.c:2", "src/Other.cs:3", "src/Other.cs:9"], asked);
        Assert.Equal(["src/Program.cs", "src/main.c", "src/Other.cs"], digest.FilesInTree());
    }

    /// <summary>
    /// What a model is handed of a digest says the declarations joined to it: the first error's symbol, and each frame of a
    /// rendered stack inside the tree with its declaration in brackets after its line, so a model reads file, declaration
    /// and line together; a frame outside the tree is rendered as printed.
    /// </summary>
    [Fact]
    public void WhatAModelIsHanded_SaysTheDeclarationsJoinedToTheDigest()
    {
        var digest = ResultDigest.Of(
            [new Diagnostic("msbuild", "error", "CS0103", "The name 'x' does not exist in the current context", "src/Store.cs", 14, 9)],
            [
                new TestFailure(
                    "xunit",
                    "App.Tests.Loads",
                    Exception: new ExceptionRecord(
                        "System.InvalidOperationException",
                        "no record",
                        [
                            new StackFrame("at App.Store.Load() in src/Store.cs:line 14", "src/Store.cs", 14, Symbol: "App.Store.Load", InTree: true),
                            new StackFrame("at System.Linq.Enumerable.First()", Symbol: "System.Linq.Enumerable.First"),
                        ],
                        [])),
            ],
            []).Declared((file, line) => (file, line) == ("src/Store.cs", 14) ? new EnclosingDeclaration("member", "App.Store.Load", "4f2a9b") : null);

        var handed = ResultDigest.Handed(digest, [], PhaseParsers.AsRead).Digest!;

        Assert.Equal("member App.Store.Load 4f2a9b", handed["first"]!["symbol"]!.GetValue<string>());
        Assert.Equal(
            ["at App.Store.Load() in src/Store.cs:line 14 [member App.Store.Load 4f2a9b]", "at System.Linq.Enumerable.First()"],
            handed["failures"]![0]!["stack"]!.AsArray().Select(line => line!.GetValue<string>()));
    }

    /// <summary>
    /// The tree's declarations are read once for a digest, however many of its places ask, and never from what is not the
    /// tree's own: several Go tests of packages no <c>go.mod</c> owns, each needing the whole tree read, list each directory
    /// of it exactly once - the placing stays linear in the tree and the places - while git's own directory, at any depth,
    /// and the tool's own state the tree holds - the copies its legs work in, its mutation workers and a former version's,
    /// its agents' worktrees where the configuration keeps them, a worker's dependency sources - are never listed; a Dart
    /// frame of a package no configuration above where the tool ran names lists nothing; and every place stays as written.
    /// </summary>
    [Fact]
    public void TheTreesDeclarations_AreReadOnceForADigest_NeverFromWhatIsNotTheTreesOwn()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("a/b/c/c_test.go");
        temp.WriteFile("d/d_test.go");
        temp.WriteFile("e/f/f.dart");
        temp.WriteFile(".git/objects/info");
        temp.WriteFile("a/.git/hooks/pre-commit");
        temp.WriteFile(".legs/copy/go.mod", "module example.org\n");
        temp.WriteFile(".m/w-1/go.mod", "module example.org\n");
        temp.WriteFile(".mutations/w-0/go.mod", "module example.org\n");
        temp.WriteFile(".worktrees/agent/go.mod", "module example.org\n");
        temp.WriteFile(".harness-config/deps/module/go.mod", "module example.org\n");
        var files = new CountedListings();

        var marked = ResultDigest.Of(
            [],
            [
                new TestFailure("go", "TestC", Class: "example.org/c", File: "c_test.go", Line: 1),
                new TestFailure("go", "TestD", Class: "example.org/d", File: "d_test.go", Line: 2),
                new TestFailure("go", "TestE", Class: "example.org/e", File: "e_test.go", Line: 3),
                new TestFailure("dart", "Bad state", Exception: new ExceptionRecord("StateError", "bad", [new StackFrame("#0 f (package:other/f.dart:4:1)", "package:other/f.dart", 4)], [])),
            ],
            [])
            .WithInTree(new DigestTree(temp.Path, null, files, ".worktrees"));

        string[] directories = [temp.Path, temp.Combine("a"), temp.Combine("a", "b"), temp.Combine("a", "b", "c"), temp.Combine("d"), temp.Combine("e"), temp.Combine("e", "f"), temp.Combine(".harness-config")];

        Assert.Equal(directories.Order(StringComparer.Ordinal).Select(directory => (directory, 1)), files.Listed.OrderBy(listed => listed.Key, StringComparer.Ordinal).Select(listed => (listed.Key, listed.Value)));
        Assert.Equal(["c_test.go", "d_test.go", "e_test.go", null], marked.Failures.Select(failure => failure.File));
        Assert.Equal(("package:other/f.dart", false), (marked.Failures[3].Exception!.Stack[0].File, marked.Failures[3].Exception!.Stack[0].InTree));
    }

    /// <summary>The real file system, counting how often each directory is listed.</summary>
    private sealed class CountedListings() : PassThroughFileSystem(new PhysicalFileSystem(FilePermissionsFactory.Create()))
    {
        /// <summary>Each directory listed, with how many times.</summary>
        public Dictionary<string, int> Listed { get; } = new(StringComparer.Ordinal);

        public override IEnumerable<string> EnumerateDirectories(string path)
        {
            Listed[path] = Listed.GetValueOrDefault(path) + 1;
            return base.EnumerateDirectories(path);
        }
    }

    /// <summary>The digest as one JSON document keeps everything: every diagnostic and failure whole, its counts, its unparsed lines.</summary>
    [Fact]
    public void TheDocument_KeepsEverythingWhole()
    {
        var digest = ResultDigest.Of(
            [new Diagnostic("msbuild", "error", "CS0103", "The name 'x' does not exist in the current context", "src/Program.cs", 12, 35, Count: 2, Log: (10, 11))],
            [],
            ["a stray line"]);

        var document = digest.Document();

        Assert.Equal(1, document["errors"]!.GetValue<int>());
        Assert.Equal("CS0103", document["diagnostics"]![0]!["code"]!.GetValue<string>());
        Assert.Equal(2, document["diagnostics"]![0]!["count"]!.GetValue<int>());
        Assert.Equal([10, 11], document["diagnostics"]![0]!["log"]!.AsArray().Select(line => line!.GetValue<int>()));
        Assert.Equal(["a stray line"], document["unparsed"]!.AsArray().Select(line => line!.GetValue<string>()));
    }

    /// <summary>
    /// The README says what the ledger's <c>--json</c> keeps and counts of a red leg's digest - the bounds the code
    /// applies, each cut counted - so a reader expects the bounded digest the ledger answers, not every diagnostic of a
    /// build that printed hundreds.
    /// </summary>
    [Fact]
    public void TheReadme_SaysWhatTheLedgersJsonKeeps_OfARedLegsDigest()
    {
        Assert.Contains(
            "`--json` carries the digest typed as `digest`, its bounds said - the first "
                + $"{ResultDigest.Bound.ToString(CultureInfo.InvariantCulture)} diagnostics of each severity, the first "
                + $"{ResultDigest.Bound.ToString(CultureInfo.InvariantCulture)} failures and the last {ResultDigest.Bound.ToString(CultureInfo.InvariantCulture)} lines no parser held kept, the rest counted by "
                + $"code and by file over the whole output, each stack held to its first {StackTrace.FirstKept.ToString(CultureInfo.InvariantCulture)} and last "
                + $"{StackTrace.LastKept.ToString(CultureInfo.InvariantCulture)} frames, each cause chain followed "
                + $"{ExceptionRecord.CauseBound.ToString(CultureInfo.InvariantCulture)} deep, every cut counted",
            Prose.Words(Prose.Document("README.md")),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Every type and member the architecture's section on results digested names in code - backticked, a type and the
    /// member or nested type after it - is one the code holds, so a member renamed or taken out is never left named there as
    /// the owner of the rule the prose says.
    /// </summary>
    [Fact]
    public void TheArchitecturesDigestSection_NamesOnlyMembersTheCodeHolds()
    {
        var document = File.ReadAllText(Path.Combine(Prose.RepositoryRoot(), "docs", "architecture.md")).Replace("\r\n", "\n", StringComparison.Ordinal);
        var start = document.IndexOf("\n## Results digested\n", StringComparison.Ordinal);
        var end = document.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        var spans = document[start..end].Split('`');
        var types = typeof(ResultDigest).Assembly.GetTypes();

        // Each code span's name, up to a call's parenthesis: a type and what is named in it, every part a type's or a
        // member's name - a file's name (Thread.java) and a path are not.
        var named = spans
            .Where((_, at) => at % 2 == 1)
            .Select(span => span.Split('(')[0])
            .Where(span => span.Split('.') is { Length: > 1 } parts && parts.All(part => part.Length > 0 && char.IsAsciiLetterUpper(part[0]) && part.All(char.IsAsciiLetterOrDigit)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(1, spans.Length % 2);
        Assert.Contains("ResultDigest.Gathering.Through", named);
        Assert.DoesNotContain(named, name => !Holds(name));

        bool Holds(string name)
        {
            const System.Reflection.BindingFlags Every = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.FlattenHierarchy;
            var parts = name.Split('.');
            IEnumerable<Type> holders = types.Where(type => !type.IsNested && type.Name.Split('`')[0] == parts[0]);

            foreach (var part in parts[1..^1])
            {
                holders = [.. holders.SelectMany(type => type.GetNestedTypes(Every)).Where(type => type.Name.Split('`')[0] == part)];
            }

            return holders.Any(type => type.GetMember(parts[^1], Every).Length > 0);
        }
    }

    /// <summary>
    /// A digest's document reads back as the digest it was written from - every diagnostic, failure, frame, cause and
    /// count, each stack's and each cause chain's cut, the counts by code and by file and what the bounds left out, its
    /// first error the first of its errors - so another machine's ledger, which carries a leg's digest as that machine's
    /// --json wrote it, is read by the names it was written under; every text it holds read through what each line
    /// another machine printed becomes on this one.
    /// </summary>
    [Fact]
    public void ADigestsDocument_ReadsBackAsTheDigest_EachTextAsThisMachineSaysIt()
    {
        IReadOnlyList<StackFrame> deep = [.. Enumerable.Range(1, StackTrace.FrameBound + 6).Select(at => new StackFrame($"in src/Program.cs:{at.ToString(CultureInfo.InvariantCulture)}", "src/Program.cs", at, Kind: "include", InTree: true))];
        var chain = new ExceptionRecord("System.IO.IOException", "inner at 192.0.2.10", [], []);

        for (var depth = 0; depth < ExceptionRecord.CauseBound + 2; depth++)
        {
            chain = new ExceptionRecord("System.InvalidOperationException", depth == ExceptionRecord.CauseBound + 1 ? "outer" : "wrapping at 192.0.2.10", [new StackFrame("at A()", "tests/A.cs", 45, InTree: true)], [chain]);
        }

        var digest = ResultDigest.Of(
            [
                new Diagnostic("gcc", "warning", "-Wunused", "unused at 192.0.2.10", "src/a.c", 3, 9, 3, 12, "main", "app", Log: (4, 4)),
                new Diagnostic("msbuild", "error", "CS0103", "no x at 192.0.2.10", "src/Program.cs", 12, 35, Count: 2, Stack: deep, Log: (10, 11)),
            ],
            [
                new TestFailure(
                    "xunit",
                    "App.Tests.Holds",
                    "Holds",
                    "App.Tests",
                    "Holds",
                    "expected 1",
                    "1",
                    "2",
                    "tests/A.cs",
                    45,
                    chain,
                    null,
                    0.25,
                    "errored",
                    (2, 3)),
            ],
            [.. Enumerable.Range(1, ResultDigest.Bound + 3).Select(at => $"a stray line {at.ToString(CultureInfo.InvariantCulture)} from 192.0.2.10")]);

        var read = ResultDigest.Read(digest.Document());

        Assert.Equal(digest.Document().ToJsonString(), read!.Document().ToJsonString());
        Assert.Same(read.Diagnostics[1], read.First);
        var cut = read.Failures[0].Exception!;

        for (var depth = 1; depth <= ExceptionRecord.CauseBound; depth++)
        {
            cut = cut.CausedBy[0];
        }

        // The cuts the bounds made are read back as they were said: six frames of seventy, the two records below the
        // eighth cause, and the three unparsed lines before the last fifty held.
        Assert.Equal((6, 2, ResultDigest.Bound + 3), (read.Diagnostics[1].StackCut, cut.CausesCut, read.UnparsedCount));
        Assert.Null(ResultDigest.Read(null));

        var said = ResultDigest.Read(digest.Document(), text => text.Replace("192.0.2.10", "mac", StringComparison.Ordinal))!;

        Assert.Equal(
            ["unused at mac", "no x at mac", "wrapping at mac", "a stray line 4 from mac"],
            [said.Diagnostics[0].Message, said.Diagnostics[1].Message, said.Failures[0].Exception!.CausedBy[0].Message, said.Unparsed[0]]);
    }

    /// <summary>A document of no digest's shape, or holding what no digest does, is refused, naming what is wrong - never read as less than it holds.</summary>
    [Theory]
    [InlineData("""{"diagnostics":{}}""", "'diagnostics' is no list")]
    [InlineData("""{"diagnostics":[{"tool":"gcc","severity":"error"}]}""", "'message' is missing")]
    [InlineData("""{"diagnostics":[{"tool":"gcc","severity":"error","message":"m","log":[1]}]}""", "'log' is no range of two lines")]
    [InlineData("""{"diagnostics":[{"tool":"gcc","severity":"fatal","message":"m"}]}""", "'fatal' is not a severity a diagnostic has")]
    [InlineData("""{"failures":[{"framework":"xunit","id":"t","outcome":"failed","duration":"long"}]}""", "'duration' is no number")]
    [InlineData("""{"failures":[{"framework":"xunit","id":"t","outcome":"failed","stack":[{"text":"at A()","inTree":"yes"}]}]}""", "'inTree' is no flag")]
    [InlineData("""[]""", "a digest is no object")]
    [InlineData("""{"errors":1,"leftOut":{"diagnostics":2}}""", "it says it left out other than its counts and what it holds make")]
    [InlineData("""{"failures":[{"framework":"xunit","id":"t","outcome":"failed","exception":{"type":"T","message":"m","causedBy":[{"type":"T","message":"m","causedBy":[{"type":"T","message":"m","causedBy":[{"type":"T","message":"m","causedBy":[{"type":"T","message":"m","causedBy":[{"type":"T","message":"m","causedBy":[{"type":"T","message":"m","causedBy":[{"type":"T","message":"m","causedBy":[{"type":"T","message":"m","causedBy":[{"type":"T","message":"m"}]}]}]}]}]}]}]}]}]}}],"failed":1}""", "a cause chain is deeper than its bound of 8")]
    public void ADocumentOfNoDigestsShape_IsRefused_NamingWhatIsWrong(string document, string problem)
        => Assert.Equal(problem, Assert.Throws<System.Text.Json.JsonException>(() => ResultDigest.Read(System.Text.Json.Nodes.JsonNode.Parse(document))).Message);
}
