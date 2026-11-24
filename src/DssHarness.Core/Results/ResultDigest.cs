using System.Globalization;
using System.Text.Json.Nodes;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Json;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;

namespace DssHarness.Core.Results;

/// <summary>
/// One diagnostic a compiler, a build tool or a linter printed: what is wrong, where, how bad it is, and the chain
/// that led to it - typed once so a model reads the error and never searches a log for it. The path is relative to
/// the tree where the tree holds the file; the symbol is the declaration the graph finds at that line, where a graph of
/// the tree was asked.
/// </summary>
/// <param name="Tool">The tool that printed it: <c>msbuild</c>, <c>csc</c>, <c>msvc</c>, <c>gcc</c>, <c>clang</c>, <c>cmake</c>, <c>ninja</c>, <c>link</c>, <c>ld</c>, <c>tsc</c>, <c>eslint</c>, <c>dart</c>, <c>go</c>, or a configured matcher's name.</param>
/// <param name="Severity">How bad it is: <c>error</c>, <c>warning</c> or <c>note</c>.</param>
/// <param name="Code">The tool's code for it (<c>CS0103</c>, <c>C2065</c>, <c>-Wunused-variable</c>, <c>TS2304</c>, <c>LNK2019</c>); null where the tool gives none.</param>
/// <param name="Message">The message, one line, masked; never empty.</param>
/// <param name="File">The file it names, relative to the tree where the tree holds it (<see cref="ResultDigest.WithInTree(Func{string, string?}, Func{string, string, string?})"/>); null where the tool names none.</param>
/// <param name="Line">The line it names; null where it names none.</param>
/// <param name="Column">The column it names; null where it names none.</param>
/// <param name="EndLine">The last line it names, where the tool gives a range; null where it does not.</param>
/// <param name="EndColumn">The last column it names, where the tool gives a range; null where it does not.</param>
/// <param name="Symbol">The declaration enclosing that line, said as the tree's graph holds it - its kind, its qualified name, the node's handle (<see cref="EnclosingDeclaration.Said"/>) - joined where the graph is (<see cref="ResultDigest.Declared"/>); null where no graph was asked, where the place is outside the tree, or where the graph holds no declaration there.</param>
/// <param name="Project">The project or target the tool named; null where it names none.</param>
/// <param name="Count">How many times the same diagnostic was printed - once per translation unit or per target framework, folded into one; 1 where it was printed once.</param>
/// <param name="Stack">The chain the tool printed that led to it, as frames, innermost first; empty where it printed none, as MSBuild's own errors are - never null.</param>
/// <param name="Log">The line range of the log it was read from (from, to), so the raw text is one read away; null where it was read from no log.</param>
public sealed record Diagnostic(
    string Tool,
    string Severity,
    string? Code,
    string Message,
    string? File = null,
    int? Line = null,
    int? Column = null,
    int? EndLine = null,
    int? EndColumn = null,
    string? Symbol = null,
    string? Project = null,
    int Count = 1,
    IReadOnlyList<StackFrame>? Stack = null,
    (int From, int To)? Log = null)
{
    /// <summary>The severities a diagnostic has.</summary>
    public static readonly IReadOnlyList<string> Severities = ["error", "warning", "note"];

    /// <summary>The chain the tool printed that led to it, as frames, innermost first; empty where it printed none - never null.</summary>
    public IReadOnlyList<StackFrame> Stack { get; init; } = Stack ?? [];

    /// <summary>How many frames of the chain the digest left out of its middle, held to <see cref="StackTrace.FrameBound"/> - which the log still holds; 0 where none were.</summary>
    public int StackCut { get; init; }

    /// <summary>What is wrong with this diagnostic; null when nothing is.</summary>
    public string? Problem()
        => string.IsNullOrWhiteSpace(Tool) ? "a diagnostic names no tool"
            : !Severities.Contains(Severity, StringComparer.Ordinal) ? $"'{Severity}' is not a severity a diagnostic has"
            : string.IsNullOrWhiteSpace(Message) ? "a diagnostic says no message"
            : Line is < 1 ? $"a diagnostic names line {Line?.ToString(CultureInfo.InvariantCulture)}"
            : Column is < 1 ? $"a diagnostic names column {Column?.ToString(CultureInfo.InvariantCulture)}"
            : Count < 1 ? $"a diagnostic was printed {Count.ToString(CultureInfo.InvariantCulture)} times"
            : Stack.Count > StackTrace.FrameBound ? $"its stack holds {Stack.Count.ToString(CultureInfo.InvariantCulture)} frames, over the bound of {StackTrace.FrameBound.ToString(CultureInfo.InvariantCulture)}"
            : StackCut < 0 ? "it says a negative cut"
            : Stack.Select(frame => frame.Problem()).FirstOrDefault(wrong => wrong is not null);

    /// <summary>Whether this and <paramref name="other"/> are the same diagnostic printed again - the same tool, code, message and place.</summary>
    /// <param name="other">The other printing.</param>
    public bool SameAs(Diagnostic other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return Identity == other.Identity;
    }

    /// <summary>What makes two printings one diagnostic: <see cref="SameAs"/>'s rule, as a key a fold looks up in constant time.</summary>
    internal (string Tool, string? Code, string Message, string? File, int? Line, int? Column) Identity => (Tool, Code, Message, File, Line, Column);
}

/// <summary>
/// One test that failed: its identity as the runner gives it, the assertion as the framework reports it, and - where
/// the failure is a thrown error - the exception with its stack and its causes, typed once for every framework.
/// </summary>
/// <param name="Framework">The framework that ran it: <c>xunit</c>, <c>mstest</c>, <c>nunit</c>, <c>gtest</c>, <c>catch2</c>, <c>ctest</c>, <c>dart</c>, <c>jest</c>, <c>vitest</c>, <c>go</c>, <c>junit</c>, or a matcher's name.</param>
/// <param name="Id">The test's id as the runner gives it (its fully-qualified name or its filter form); never empty.</param>
/// <param name="Name">The test's own name; null where the runner gives only the id.</param>
/// <param name="Class">The test's class or suite - a Go test's package, by the import path <c>go test</c> names it by; null where the runner gives none.</param>
/// <param name="Method">The test's method; null where the runner gives none.</param>
/// <param name="Message">The assertion's message as the framework reports it, masked; null where it gives none.</param>
/// <param name="Expected">What was expected, where the framework says it apart; null where it does not.</param>
/// <param name="Actual">What there was, where the framework says it apart; null where it does not.</param>
/// <param name="File">
/// Where it failed, relative to the tree where the tree holds it: the top frame of its stack the tree holds, where one
/// is (<see cref="ResultDigest.WithInTree(Func{string, string?}, Func{string, string, string?})"/>), else the assertion's
/// place the runner named apart - a Go test's read from its package's directory, as <c>go test</c> prints it, and as
/// printed where no module of the tree owns its package; null where neither names one.
/// </param>
/// <param name="Line">The line <paramref name="File"/> names; null where none was named.</param>
/// <param name="Exception">Where the failure is a thrown error: its record, which holds the failure's stack; null for a plain assertion.</param>
/// <param name="Stack">
/// The stack trace of a plain assertion, as the runner printed it, in the order it printed it, every frame marked
/// <see cref="StackFrame.InTree"/>; empty where none was printed, and where the failure is a thrown error, whose stack is its
/// record's - one place for a failure's stack, never two. Never null.
/// </param>
/// <param name="Duration">How long the test took, in seconds; null where the runner does not say.</param>
/// <param name="Outcome">How it failed: <c>failed</c>, <c>errored</c>, <c>timedOut</c> or <c>crashed</c>.</param>
/// <param name="Shard">The shard that ran it, its index of the count of shards; null where the leg ran unsharded.</param>
/// <param name="Log">
/// The line range of the log it was read from (from, to) - a results file's, that of the log's failure of the same test,
/// or of the log's printing of the failure its case was said through (<see cref="ResultDigest.Gathering.Beside"/>); null
/// where no log said it.
/// </param>
public sealed record TestFailure(
    string Framework,
    string Id,
    string? Name = null,
    string? Class = null,
    string? Method = null,
    string? Message = null,
    string? Expected = null,
    string? Actual = null,
    string? File = null,
    int? Line = null,
    ExceptionRecord? Exception = null,
    IReadOnlyList<StackFrame>? Stack = null,
    double? Duration = null,
    string Outcome = "failed",
    (int Index, int Count)? Shard = null,
    (int From, int To)? Log = null)
{
    /// <summary>The outcomes a failure has.</summary>
    public static readonly IReadOnlyList<string> Outcomes = ["failed", "errored", "timedOut", "crashed"];

    /// <summary>The stack trace of a plain assertion; empty where none was printed and where the failure is a thrown error - never null.</summary>
    public IReadOnlyList<StackFrame> Stack { get; init; } = Stack ?? [];

    /// <summary>How many frames of <see cref="Stack"/> the digest left out of its middle - which the log still holds; 0 where none were.</summary>
    public int StackCut { get; init; }

    /// <summary>The failure's stack, wherever it is held: its exception's where it is a thrown error, else its own.</summary>
    public IReadOnlyList<StackFrame> Frames => Exception?.Stack ?? Stack;

    /// <summary>What is wrong with this failure; null when nothing is.</summary>
    public string? Problem()
        => string.IsNullOrWhiteSpace(Framework) ? "a failure names no framework"
            : string.IsNullOrWhiteSpace(Id) ? "a failure names no test"
            : Line is < 1 ? $"a failure names line {Line?.ToString(CultureInfo.InvariantCulture)}"
            : !Outcomes.Contains(Outcome, StringComparer.Ordinal) ? $"'{Outcome}' is not an outcome a failure has"
            : Shard is { } shard && (shard.Count < 1 || shard.Index < 1 || shard.Index > shard.Count) ? $"it names shard {shard.Index.ToString(CultureInfo.InvariantCulture)} of {shard.Count.ToString(CultureInfo.InvariantCulture)}"
            : Exception is not null && Stack.Count > 0 ? "it holds its stack twice, its own and its exception's"
            : Stack.Count > StackTrace.FrameBound ? $"its stack holds {Stack.Count.ToString(CultureInfo.InvariantCulture)} frames, over the bound of {StackTrace.FrameBound.ToString(CultureInfo.InvariantCulture)}"
            : StackCut < 0 ? "it says a negative cut"
            : Exception?.Problem() is { } wrong ? wrong
            : Stack.Select(frame => frame.Problem()).FirstOrDefault(frameWrong => frameWrong is not null);
}

/// <summary>What a red entry hands a model of what its phase printed (<see cref="ResultDigest.Handed"/>): every line once.</summary>
/// <param name="Digest">The digest said for a model; null where the entry carries none.</param>
/// <param name="Lines">The raw lines beside it; empty where the digest says them all.</param>
public sealed record HandedToAModel(JsonObject? Digest, IReadOnlyList<string> Lines);

/// <summary>How many printings one code or one file had, over the whole of what was digested.</summary>
/// <param name="Of">The code, or the file.</param>
/// <param name="Count">How many printings named it.</param>
public sealed record Counted(string Of, int Count);

/// <summary>
/// The tree a digest's places are read against: where the work ran, the directory the tool that printed them ran in -
/// which a relative path it printed is read from - the file system that says whether the tree holds a file, and where
/// the configuration keeps its agents' worktrees, which with the rest of the tool's own state in the tree
/// (<see cref="HarnessLayout.StatePaths"/>) no declaration of the tree's is read from.
/// </summary>
/// <param name="Root">The tree the phase ran in, as the machine that ran it writes it.</param>
/// <param name="WorkingDirectory">The directory the tool ran in, which a relative path it printed is read from; <see langword="null"/> for the root.</param>
/// <param name="FileSystem">The file system the tree is on.</param>
/// <param name="WorktreesRoot">The configured worktrees root (<see cref="WorktreeSettings.Root"/>); its default where the tree's configuration names none.</param>
public sealed record DigestTree(string Root, string? WorkingDirectory, IFileSystem FileSystem, string WorktreesRoot = WorktreeSettings.DefaultRoot);

/// <summary>
/// The declaration enclosing a place of a digest, as the tree's graph holds it: what was declared - its kind, its
/// qualified name - and the handle that names the node in the view, so a place a digest names is joined to the code it
/// stands in and a reader can ask the graph about it by its handle.
/// </summary>
/// <param name="Kind">The declaration's kind in the graph: a <c>type</c>, a <c>member</c>, a <c>section</c>.</param>
/// <param name="QualifiedName">The declaration's qualified name, as the graph names it.</param>
/// <param name="Handle">The node's handle in the tree's view of the graph.</param>
public sealed record EnclosingDeclaration(string Kind, string QualifiedName, string Handle)
{
    /// <summary>The declaration said as one line: its kind, its qualified name and its handle - the three a digest's symbol holds.</summary>
    public string Said() => $"{Kind} {QualifiedName} {Handle}";
}

/// <summary>
/// What a phase's or a job's output digested to: the diagnostics and the test failures, typed, bounded, with the
/// counts of each over the whole output, the first error in the order the tool met it, and whether output the parsers
/// did not understand remains. Nothing in a digest is a guess: what no parser held stays <see cref="Unparsed"/>'s lines.
/// Made only by <see cref="Of"/> and <see cref="Gathering"/>, the one place its bounds are applied and said.
/// </summary>
public sealed record ResultDigest
{
    /// <summary>
    /// How many diagnostics of each severity, how many failures, and how many unparsed lines a digest holds: the first
    /// this many diagnostics and failures in the order met - an error is never displaced by warnings, for each severity is
    /// held apart - and the last this many unparsed lines, the nearest the failure, as a log's tail is; the rest counted
    /// over the whole (<see cref="ByCode"/>, <see cref="ByFile"/>, the counts by severity and outcome,
    /// <see cref="UnparsedCount"/>) and said as left out, to a model as much as in the record.
    /// </summary>
    public const int Bound = 50;

    private ResultDigest()
    {
    }

    /// <summary>The diagnostics, in the order the tool met them, folded once each: the first <see cref="Bound"/> of each severity.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; private init; } = [];

    /// <summary>The test failures, in the order the runner met them, the first <see cref="Bound"/>.</summary>
    public IReadOnlyList<TestFailure> Failures { get; private init; } = [];

    /// <summary>How many diagnostics of severity <c>error</c> the whole output printed, folded ones counted once each.</summary>
    public int Errors { get; private init; }

    /// <summary>How many of severity <c>warning</c>, over the whole output.</summary>
    public int Warnings { get; private init; }

    /// <summary>How many of severity <c>note</c>, over the whole output.</summary>
    public int Notes { get; private init; }

    /// <summary>How many test failures of outcome <c>failed</c>, over the whole output.</summary>
    public int Failed { get; private init; }

    /// <summary>How many of outcome <c>errored</c>.</summary>
    public int Errored { get; private init; }

    /// <summary>How many of outcome <c>timedOut</c>.</summary>
    public int TimedOut { get; private init; }

    /// <summary>How many of outcome <c>crashed</c>.</summary>
    public int Crashed { get; private init; }

    /// <summary>The printings of every diagnostic by its code, over the whole output - the held and the rest - in the order each code was first met.</summary>
    public IReadOnlyList<Counted> ByCode { get; private init; } = [];

    /// <summary>The printings of every diagnostic by its file, over the whole output - the held and the rest - in the order each file was first met.</summary>
    public IReadOnlyList<Counted> ByFile { get; private init; } = [];

    /// <summary>The lines that look like errors and matched nothing, the last <see cref="Bound"/> in the order met, masked - what the parsers did not understand; empty where they understood everything.</summary>
    public IReadOnlyList<string> Unparsed { get; private init; } = [];

    /// <summary>How many such lines there were in all, the held among them.</summary>
    public int UnparsedCount { get; private init; }

    /// <summary>The first error in the order the tool met it - the one a reader starts from - always held; null where there was none.</summary>
    public Diagnostic? First => Diagnostics.FirstOrDefault(diagnostic => diagnostic.Severity == "error");

    /// <summary>The first test failure in the order the runner met it, whatever its outcome; null where there was none.</summary>
    public TestFailure? FirstFailure => Failures.Count > 0 ? Failures[0] : null;

    /// <summary>How many distinct diagnostics the bound left out of <see cref="Diagnostics"/>, counted all the same.</summary>
    public int DiagnosticsLeftOut => Errors + Warnings + Notes - Diagnostics.Count;

    /// <summary>How many failures the bound left out of <see cref="Failures"/>, counted all the same.</summary>
    public int FailuresLeftOut => Failed + Errored + TimedOut + Crashed - Failures.Count;

    /// <summary>How many unparsed lines the bound left out of <see cref="Unparsed"/>.</summary>
    public int UnparsedLeftOut => UnparsedCount - Unparsed.Count;

    /// <summary>Whether the digest holds something to start from: an error, or a failure.</summary>
    public bool HoldsAProblem => Errors > 0 || Failed + Errored + TimedOut + Crashed > 0;

    /// <summary>Whether the digest holds any typed finding - a diagnostic of any severity, or a failure.</summary>
    public bool HoldsAFinding => Errors + Warnings + Notes > 0 || Failed + Errored + TimedOut + Crashed > 0;

    /// <summary>Nothing, digested: the empty digest an output with nothing to parse answers.</summary>
    public static ResultDigest None { get; } = new();

    /// <summary>What is wrong with this digest; null when nothing is.</summary>
    public string? Problem()
        => Diagnostics.Select(diagnostic => diagnostic.Problem()).FirstOrDefault(wrong => wrong is not null) is { } diagnosticWrong ? diagnosticWrong
            : Failures.Select(failure => failure.Problem()).FirstOrDefault(wrong => wrong is not null) is { } failureWrong ? failureWrong
            : Diagnostic.Severities.FirstOrDefault(severity => Diagnostics.Count(diagnostic => diagnostic.Severity == severity) > Bound) is { } over
                ? $"it holds over {Bound.ToString(CultureInfo.InvariantCulture)} diagnostics of severity '{over}'"
            : Failures.Count > Bound || Unparsed.Count > Bound ? $"it holds over {Bound.ToString(CultureInfo.InvariantCulture)} failures or unparsed lines"
            : DiagnosticsLeftOut < 0 || FailuresLeftOut < 0 || UnparsedLeftOut < 0 ? "it holds more than it counts"
            : null;

    /// <summary>
    /// The digest of <paramref name="diagnostics"/>, <paramref name="failures"/> and <paramref name="unparsed"/>, each
    /// gathered in the order given (<see cref="Gathering"/>).
    /// </summary>
    /// <param name="diagnostics">The diagnostics in the order the tool met them.</param>
    /// <param name="failures">The failures in the order the runner met them.</param>
    /// <param name="unparsed">The lines no parser held.</param>
    public static ResultDigest Of(IEnumerable<Diagnostic> diagnostics, IEnumerable<TestFailure> failures, IEnumerable<string> unparsed)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(failures);
        ArgumentNullException.ThrowIfNull(unparsed);

        var gathering = new Gathering();

        foreach (var diagnostic in diagnostics)
        {
            gathering.Add(diagnostic);
        }

        foreach (var failure in failures)
        {
            gathering.Add(failure);
        }

        foreach (var line in unparsed)
        {
            gathering.Unparsed(line);
        }

        return gathering.Digest();
    }

    /// <summary>
    /// This digest with every place it names read against <paramref name="tree"/>, a tree this machine ran the phase in:
    /// a file the tree holds is in the tree, named relative to its root with <c>/</c>; any other is not, and stays as the
    /// tool wrote it. A path is read in the form the tool wrote it, never by this machine's own rules
    /// (<see cref="PlatformPaths.RelativeAsWritten"/>) - a relative one from the directory the tool ran in, a
    /// <c>file:</c> URI as the path it names - and is in the tree only where the tree holds that file: a frame that names
    /// no file, a runtime's own module (<c>dart:</c>, <c>node:</c>), a bare name the tree has no file of
    /// (<c>Thread.java</c>), a path outside the root, are in none. A runtime's own name for one of the project's files is
    /// read as the runtime reads it (<see cref="ProjectPackages"/>): a Go test's place, which <c>go test</c> prints
    /// relative to its package's directory and names the package by its import path, from that directory - the import path
    /// less the module path of the <c>go.mod</c> nearest the tree's root that owns it - and a Dart <c>package:name/path</c>
    /// below the directory the package configuration of where the tool ran gives the package
    /// (<c>.dart_tool/package_config.json</c>); where nothing of the tree declares the name, the place stays as written.
    /// Placed by the one owner of placing a digest's paths
    /// (<see cref="WithInTree(Func{string, string?}, Func{string, string, string?})"/>), and the one place a digest's paths
    /// ask the tree's file system: the tree's declarations are read once for the digest, however many of its places ask.
    /// The declaration the tree's graph holds at a place is joined where the graph is (<see cref="Declared"/>), never here.
    /// </summary>
    /// <param name="tree">The tree the phase ran in, and where its tool ran.</param>
    public ResultDigest WithInTree(DigestTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentException.ThrowIfNullOrWhiteSpace(tree.Root);

        var packages = new ProjectPackages(tree);

        return WithInTree(
            file => ProjectPackages.DartPackageOf(file) is { } named
                ? packages.DartLibraryOf(named.Package) is { } library ? Held(named.Path, library) : null
                : Held(file, tree.WorkingDirectory),
            (file, package) => packages.GoPackageDirectoryOf(package) is { } directory ? Held(file, directory) : null);

        // A path the tree holds, read from the directory a relative one was printed relative to, named as the tree's.
        string? Held(string file, string? from)
            => PlatformPaths.RelativeAsWritten(tree.Root, file, from) is { } below && tree.FileSystem.FileExists(Path.Combine(tree.Root, below))
                ? below
                : null;
    }

    /// <summary>
    /// The files of the tree this digest's places name, each once in the order first met: those <see cref="Declared"/>
    /// asks about, which an ask checks first so it answers of each file as it is.
    /// </summary>
    public IReadOnlyList<string> FilesInTree() => [.. PlacesInTree().Select(place => place.File).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// This digest with the declaration the tree's graph holds enclosing each of its places in the tree joined to it - the
    /// one owner of a digest's symbols: a diagnostic's symbol is it (<see cref="EnclosingDeclaration.Said"/>), and each
    /// frame holds it as its <see cref="StackFrame.Declaration"/> beside the function its line printed - so what and where
    /// is file, declaration and line, and the handle joins the place to the graph's node. Only a place in the tree is asked
    /// (<see cref="PlacesInTree"/>): a diagnostic's own, and each frame the placing marked the tree's - a diagnostic's
    /// chain, every failure's stack and each of its causes' - never a frame outside the tree, a runtime's own module, or a
    /// place that names no file or no line. Each place asked once, however many findings name it; one the graph holds no
    /// declaration at says none. Joined where the graph is - a leg step of a run that keeps a graph of the tree its legs
    /// ran in - for a digest made where its phase ran, against that machine's copy of the tree, names its places as the
    /// tree does.
    /// </summary>
    /// <param name="declarationAt">
    /// The tree's graph's declaration enclosing a file of the tree, named as the tree names it, at a line of it; null where it
    /// holds none, and where the file the graph read is not proven to hold what the digest's phase read
    /// (<see cref="Execution.WorkedTree.Built"/>).
    /// </param>
    public ResultDigest Declared(Func<string, int, EnclosingDeclaration?> declarationAt)
    {
        ArgumentNullException.ThrowIfNull(declarationAt);

        var asked = new Dictionary<(string File, int Line), EnclosingDeclaration?>();

        return this with
        {
            Diagnostics = [.. Diagnostics.Select(diagnostic => diagnostic with
            {
                Symbol = InTree(diagnostic) is { } place ? At(place)?.Said() : diagnostic.Symbol,
                Stack = Frames(diagnostic.Stack),
            })],
            Failures = [.. Failures.Select(failure => failure with
            {
                Exception = failure.Exception is { } thrown ? Thrown(thrown) : null,
                Stack = Frames(failure.Stack),
            })],
        };

        ExceptionRecord Thrown(ExceptionRecord thrown) => thrown with { Stack = Frames(thrown.Stack), CausedBy = [.. thrown.CausedBy.Select(Thrown)] };

        IReadOnlyList<StackFrame> Frames(IReadOnlyList<StackFrame> stack)
            => [.. stack.Select(frame => InTree(frame) is { } place ? frame with { Declaration = At(place) } : frame)];

        EnclosingDeclaration? At((string File, int Line) place)
        {
            if (!asked.TryGetValue(place, out var declared))
            {
                asked[place] = declared = declarationAt(place.File, place.Line);
            }

            return declared;
        }
    }

    /// <summary>
    /// Every place of this digest in the tree, in the order met: each diagnostic's own - its file named as the tree names
    /// one (<see cref="InTree(Diagnostic)"/>) - and its chain's frames, then each failure's frames, its own stack's and its
    /// thrown error's down its causes, each the placing marked the tree's (<see cref="InTree(StackFrame)"/>).
    /// </summary>
    private IEnumerable<(string File, int Line)> PlacesInTree()
    {
        foreach (var diagnostic in Diagnostics)
        {
            if (InTree(diagnostic) is { } place)
            {
                yield return place;
            }

            foreach (var framed in Placed(diagnostic.Stack))
            {
                yield return framed;
            }
        }

        foreach (var failure in Failures)
        {
            foreach (var framed in Placed(failure.Stack))
            {
                yield return framed;
            }

            // Each cause after the record that names it, on an explicit stack, as deep as the chain goes.
            for (var causes = new Stack<ExceptionRecord>(failure.Exception is { } thrown ? [thrown] : []); causes.TryPop(out var record);)
            {
                foreach (var framed in Placed(record.Stack))
                {
                    yield return framed;
                }

                for (var at = record.CausedBy.Count - 1; at >= 0; at--)
                {
                    causes.Push(record.CausedBy[at]);
                }
            }
        }

        static IEnumerable<(string File, int Line)> Placed(IReadOnlyList<StackFrame> stack)
            => stack.Select(InTree).OfType<(string File, int Line)>();
    }

    /// <summary>
    /// A diagnostic's own place where it is in the tree: its file named as the placing names every file the tree holds -
    /// relative to the root with <c>/</c>, never rooted, climbing out of the root or of a scheme other than a path's
    /// (<see cref="PlatformPaths.RelativeAsWritten"/> reads it as it is) - and a line; null otherwise. A tool's path the
    /// tree does not hold stays as written, and one written so names the tree's file of that path to every reader of the
    /// digest, so its declaration is that file's.
    /// </summary>
    private static (string File, int Line)? InTree(Diagnostic diagnostic)
        => diagnostic is { File: { } file, Line: { } line } && PlatformPaths.RelativeAsWritten(null, file) is { } relative && string.Equals(relative, file, StringComparison.Ordinal)
            ? (file, line)
            : null;

    /// <summary>A frame's place where the placing marked it the tree's (<see cref="StackFrame.InTree"/>) and it names a line; null otherwise.</summary>
    private static (string File, int Line)? InTree(StackFrame frame)
        => frame is { InTree: true, File: { } file, Line: { } line } ? (file, line) : null;

    /// <summary>
    /// This digest with every place it names placed by <paramref name="inTree"/> - the one place a frame is marked and a
    /// path made the tree's, for a tree this machine ran the phase in (<see cref="WithInTree(DigestTree)"/>) as for one
    /// another machine ran a job in, which only that machine's convention places: each frame of every stack, every cause's
    /// among them, marked by whether its file is in the tree, and every file in it - a frame's, a diagnostic's and its
    /// project's, a failure's, a count's by file - named as the tree names it; a file outside it named as the tool wrote it,
    /// and a frame that names no file in no tree. A failure's place is then the top frame of its stack in the tree, where
    /// one is - the assertion's line, not the framework's - else the place its runner named apart: a Go test's, which
    /// <c>go test</c> prints relative to its package's directory, placed by <paramref name="inPackage"/> alone and as
    /// printed where that places it nowhere, never read from where the tool ran.
    /// </summary>
    /// <param name="inTree">A file as the tool wrote it, named as the tree names it; null where it is not in the tree.</param>
    /// <param name="inPackage">
    /// A file a Go test printed and the import path of its package - the failure's class - named as the tree names it; null
    /// where it is not in the tree or the tree's module that owns the package is not known. Null where the places are read by
    /// their text alone - a job another machine ran - so such a file stays as printed.
    /// </param>
    public ResultDigest WithInTree(Func<string, string?> inTree, Func<string, string, string?>? inPackage = null)
    {
        ArgumentNullException.ThrowIfNull(inTree);

        // Each path asked once, however many frames name it; a Go test's place once for its package.
        var asked = new Dictionary<string, string?>(StringComparer.Ordinal);
        var askedInPackage = new Dictionary<(string File, string Package), string?>();

        return this with
        {
            Diagnostics = [.. Diagnostics.Select(diagnostic => diagnostic with { File = Placed(diagnostic.File), Project = Placed(diagnostic.Project), Stack = Marked(diagnostic.Stack) })],
            Failures = [.. Failures.Select(Failure)],
            ByFile = [.. ByFile
                .Select(counted => counted with { Of = Placed(counted.Of)! })
                .GroupBy(counted => counted.Of, StringComparer.Ordinal)
                .Select(same => new Counted(same.Key, same.Sum(counted => counted.Count)))],
        };

        TestFailure Failure(TestFailure failure)
        {
            var exception = failure.Exception is { } thrown ? Thrown(thrown) : null;
            var stack = Marked(failure.Stack);
            var top = (exception?.Stack ?? stack).FirstOrDefault(frame => frame.InTree);

            return failure with
            {
                Exception = exception,
                Stack = stack,
                File = top is not null ? top.File : PlaceOf(failure),
                Line = top is not null ? top.Line : failure.Line,
            };
        }

        // The place a failure's runner named apart: a Go test's read from its package's directory alone, any other's as
        // every path is.
        string? PlaceOf(TestFailure failure)
        {
            if (failure is not { Framework: GoPanicParser.Framework, File: { } file })
            {
                return Placed(failure.File);
            }

            if (failure.Class is not { } package)
            {
                return file;
            }

            if (!askedInPackage.TryGetValue((file, package), out var relative))
            {
                relative = inPackage?.Invoke(file, package);
                askedInPackage[(file, package)] = relative;
            }

            return relative ?? file;
        }

        ExceptionRecord Thrown(ExceptionRecord thrown)
            => thrown with { Stack = Marked(thrown.Stack), CausedBy = [.. thrown.CausedBy.Select(Thrown)] };

        IReadOnlyList<StackFrame> Marked(IReadOnlyList<StackFrame> stack)
            => [.. stack.Select(frame => InTree(frame.File) is { } relative ? frame with { File = relative, InTree = true } : frame with { InTree = false })];

        // A path the tree holds, named as the tree's; any other as the tool wrote it.
        string? Placed(string? file) => InTree(file) ?? file;

        string? InTree(string? file)
        {
            if (file is null)
            {
                return null;
            }

            if (!asked.TryGetValue(file, out var relative))
            {
                relative = inTree(file);
                asked[file] = relative;
            }

            return relative;
        }
    }

    /// <summary>
    /// What a red entry hands a model of what its phase printed - a red leg's, a red job's: the digest said for a model and
    /// the raw lines beside it, the one rule of every entry that carries one, so every line reaches a model once and none
    /// that was printed is out of its reach. Where the digest holds an error or a failure to start from and the whole log
    /// is one read away (<paramref name="logHere"/>), the lines no parser held are said in the digest - the last
    /// <see cref="Bound"/>, the nearest the failure, with how many it left out - and no raw line goes beside it: a model
    /// reads the typed problems and the lines no parser held, and every other line from the log by the ranges they give,
    /// never a page that repeats them. Otherwise - the digest holds warnings and notes alone, or nothing typed, or no log
    /// of the phase is on this machine to read - <paramref name="tail"/> goes whole beside it, for the line that says why
    /// the phase failed is in no diagnostic, or in nothing a model could read, and the digest says those of its lines no
    /// parser held the tail does not: each line of the tail, read as the digest's were (<paramref name="asRead"/>), answers
    /// for one of them. Where there is no digest, the tail alone.
    /// </summary>
    /// <param name="digest">The entry's digest; null where it carries none.</param>
    /// <param name="tail">The raw lines that would go without a digest: the phase's last lines, the job's failing page.</param>
    /// <param name="asRead">A raw line as the digest's reading read it (<see cref="PhaseParsers.AsRead"/> for a phase's log).</param>
    /// <param name="logHere">Whether the whole log the tail is the end of is one read away on this machine, named beside the entry.</param>
    public static HandedToAModel Handed(ResultDigest? digest, IReadOnlyList<string> tail, Func<string, string> asRead, bool logHere)
    {
        ArgumentNullException.ThrowIfNull(tail);
        ArgumentNullException.ThrowIfNull(asRead);

        if (digest is null)
        {
            return new HandedToAModel(null, tail);
        }

        if (digest.HoldsAProblem && logHere)
        {
            return new HandedToAModel(digest.ForAModel(digest.Unparsed), []);
        }

        var told = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var line in tail)
        {
            var read = asRead(line);
            told[read] = told.GetValueOrDefault(read) + 1;
        }

        var said = new List<string>();

        foreach (var line in digest.Unparsed)
        {
            if (told.TryGetValue(line, out var left) && left > 0)
            {
                told[line] = left - 1;
            }
            else
            {
                said.Add(line);
            }
        }

        return new HandedToAModel(digest.ForAModel(said), tail);
    }

    /// <summary>
    /// The first problem said as one line - the ledger's, a red check's and a continuation record's: the first error,
    /// what printed it, its code, its place and its message's first line, the count of its printings where it printed
    /// more than once; else the first failure, whatever its outcome. Null where the digest holds neither.
    /// </summary>
    public string? FirstSaid()
        => First is { } first ? Said(first)
            : FirstFailure is { } failure ? Said(failure)
            : null;

    /// <summary>
    /// The digest as a model reads it: the counts, the first problem said whole with its stack rendered to its frames in
    /// the tree and its causes under it, the next distinct problems to four in all - an error one line each, a test
    /// failure with its rendered stack - the rest counted by code and by file (every printing the four do not say, over
    /// the whole output), and of the lines no parser held <paramref name="unparsed"/> - those the raw lines beside it do
    /// not say - with how many the bound left out; each thing said once, and a member that says nothing - a count of none,
    /// an empty list, a null - left out at every depth, for a model pays for every byte it is handed. A rule of rendering,
    /// never of the record: the whole digest stays in the ledger's own <c>--json</c> (<see cref="Document"/>), its counts
    /// by code and by file over everything. A digest reaches a model only through <see cref="Handed"/>, which says what
    /// goes beside it.
    /// </summary>
    private JsonObject ForAModel(IReadOnlyList<string> unparsed)
    {
        // The four a round starts from, in the order met: the errors first - a failure is rarely reached past one -
        // then the failures; the first said whole, the next three as their kind renders.
        var four = Diagnostics
            .Where(diagnostic => diagnostic.Severity == "error")
            .Select(diagnostic => (Diagnostic: (Diagnostic?)diagnostic, Failure: (TestFailure?)null))
            .Concat(Failures.Select(failure => (Diagnostic: (Diagnostic?)null, Failure: (TestFailure?)failure)))
            .Take(4)
            .ToList();
        var next = four.Skip(1).ToList();
        IReadOnlyList<Diagnostic> said = [.. four.Where(item => item.Diagnostic is not null).Select(item => item.Diagnostic!)];

        return Pruned(new JsonObject
        {
            ["errors"] = Errors,
            ["warnings"] = Warnings,
            ["notes"] = Notes,
            ["failed"] = Failed,
            ["errored"] = Errored,
            ["timedOut"] = TimedOut,
            ["crashed"] = Crashed,
            ["first"] = four.Count == 0 ? null : four[0].Diagnostic is { } diagnostic ? FirstOf(diagnostic) : FirstOf(four[0].Failure!),
            ["diagnostics"] = new JsonArray([.. next.Where(item => item.Diagnostic is not null).Select(item => (JsonNode)new JsonObject
            {
                ["said"] = Said(item.Diagnostic!),
                ["count"] = item.Diagnostic!.Count,
                ["log"] = LogOf(item.Diagnostic.Log),
            })]),
            ["failures"] = new JsonArray([.. next.Where(item => item.Failure is not null).Select(item => (JsonNode)new JsonObject
            {
                ["framework"] = item.Failure!.Framework,
                ["id"] = item.Failure.Id,
                ["message"] = MessageOf(item.Failure),
                ["file"] = item.Failure.File,
                ["line"] = item.Failure.Line,
                ["outcome"] = item.Failure.Outcome,
                ["stack"] = Rendered(item.Failure),
            })]),
            ["byCode"] = Rest(ByCode, said, diagnostic => diagnostic.Code),
            ["byFile"] = Rest(ByFile, said, diagnostic => diagnostic.File),
            ["unparsed"] = new JsonArray([.. unparsed.Select(line => (JsonNode?)line)]),
            ["unparsedLeftOut"] = UnparsedLeftOut,
        });
    }

    /// <summary>
    /// The rest of <paramref name="counts"/> once <paramref name="said"/> is said: each code's or file's printings less
    /// those of the diagnostics a model is handed whole or as a line, in the order each was first met - none where nothing
    /// is left - so a model counts nothing twice.
    /// </summary>
    /// <param name="counts">The printings by code or by file, over the whole output.</param>
    /// <param name="said">The diagnostics a model is handed.</param>
    /// <param name="of">The code or the file a diagnostic is counted under.</param>
    private static JsonArray Rest(IReadOnlyList<Counted> counts, IReadOnlyList<Diagnostic> said, Func<Diagnostic, string?> of)
    {
        var told = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var diagnostic in said)
        {
            if (of(diagnostic) is { } key)
            {
                told[key] = told.GetValueOrDefault(key) + diagnostic.Count;
            }
        }

        return new JsonArray([.. counts
            .Select(counted => new Counted(counted.Of, counted.Count - told.GetValueOrDefault(counted.Of)))
            .Where(rest => rest.Count > 0)
            .Select(rest => (JsonNode)new JsonObject { ["of"] = rest.Of, ["count"] = rest.Count })]);
    }

    /// <summary>
    /// <paramref name="said"/> with every member that says nothing left out, at every depth of its objects and of the
    /// objects its lists hold: a whole number of none, an empty list, a null. What a reader of it finds missing is none.
    /// </summary>
    /// <param name="said">What a model is handed.</param>
    private static JsonObject Pruned(JsonObject said)
    {
        foreach (var (name, value) in said.ToList())
        {
            switch (value)
            {
                case null:
                case JsonArray { Count: 0 }:
                case JsonValue number when number.TryGetValue<int>(out var whole) && whole == 0:
                    said.Remove(name);
                    break;
                case JsonObject inner:
                    Pruned(inner);
                    break;
                case JsonArray items:
                    foreach (var item in items.OfType<JsonObject>())
                    {
                        Pruned(item);
                    }

                    break;
            }
        }

        return said;
    }

    /// <summary>
    /// The first problem said whole, where it is a diagnostic: its tool, code, severity, place, the declaration enclosing it
    /// where one was joined (<see cref="Declared"/>), count, log range, rendered stack and saying - its message's first line
    /// said once, in its saying.
    /// </summary>
    private static JsonObject FirstOf(Diagnostic diagnostic)
        => new()
        {
            ["kind"] = "diagnostic",
            ["tool"] = diagnostic.Tool,
            ["code"] = diagnostic.Code,
            ["severity"] = diagnostic.Severity,
            ["file"] = diagnostic.File,
            ["line"] = diagnostic.Line,
            ["column"] = diagnostic.Column,
            ["symbol"] = diagnostic.Symbol,
            ["count"] = diagnostic.Count,
            ["log"] = LogOf(diagnostic.Log),
            ["stack"] = Rendered(diagnostic.Stack, diagnostic.StackCut),
            ["said"] = Said(diagnostic),
        };

    /// <summary>
    /// The first problem said whole, where it is a test failure: its framework, test, outcome, place, log range, rendered
    /// stack and its causes, and saying - its message said once, in its saying.
    /// </summary>
    private static JsonObject FirstOf(TestFailure failure)
        => new()
        {
            ["kind"] = "failure",
            ["framework"] = failure.Framework,
            ["id"] = failure.Id,
            ["outcome"] = failure.Outcome,
            ["file"] = failure.File,
            ["line"] = failure.Line,
            ["count"] = 1,
            ["log"] = LogOf(failure.Log),
            ["stack"] = Rendered(failure),
            ["said"] = Said(failure),
        };

    /// <summary>
    /// The digest as one JSON document, whole - the ledger's <c>--json</c> and the GUI's: every diagnostic and failure
    /// it holds, fully, each cause chain to its end, each cut it made said, the counts over the whole output, the first
    /// problem, the rest by code and by file, and how many the bounds left out.
    /// </summary>
    public JsonObject Document()
        => new()
        {
            ["errors"] = Errors,
            ["warnings"] = Warnings,
            ["notes"] = Notes,
            ["failed"] = Failed,
            ["errored"] = Errored,
            ["timedOut"] = TimedOut,
            ["crashed"] = Crashed,
            ["first"] = FirstSaid(),
            ["diagnostics"] = new JsonArray([.. Diagnostics.Select(diagnostic => (JsonNode)new JsonObject
            {
                ["tool"] = diagnostic.Tool,
                ["severity"] = diagnostic.Severity,
                ["code"] = diagnostic.Code,
                ["message"] = diagnostic.Message,
                ["file"] = diagnostic.File,
                ["line"] = diagnostic.Line,
                ["column"] = diagnostic.Column,
                ["endLine"] = diagnostic.EndLine,
                ["endColumn"] = diagnostic.EndColumn,
                ["symbol"] = diagnostic.Symbol,
                ["project"] = diagnostic.Project,
                ["count"] = diagnostic.Count,
                ["stack"] = Frames(diagnostic.Stack),
                ["stackCut"] = diagnostic.StackCut,
                ["log"] = LogOf(diagnostic.Log),
            })]),
            ["failures"] = new JsonArray([.. Failures.Select(failure => (JsonNode)new JsonObject
            {
                ["framework"] = failure.Framework,
                ["id"] = failure.Id,
                ["name"] = failure.Name,
                ["class"] = failure.Class,
                ["method"] = failure.Method,
                ["message"] = failure.Message,
                ["expected"] = failure.Expected,
                ["actual"] = failure.Actual,
                ["file"] = failure.File,
                ["line"] = failure.Line,
                ["outcome"] = failure.Outcome,
                ["duration"] = failure.Duration,
                ["shard"] = failure.Shard is { } shard ? new JsonObject { ["index"] = shard.Index, ["count"] = shard.Count } : null,
                ["stack"] = Frames(failure.Stack),
                ["stackCut"] = failure.StackCut,
                ["exception"] = failure.Exception is { } thrown ? Thrown(thrown) : null,
                ["log"] = LogOf(failure.Log),
            })]),
            ["byCode"] = new JsonArray([.. ByCode.Select(counted => (JsonNode)new JsonObject { ["of"] = counted.Of, ["count"] = counted.Count })]),
            ["byFile"] = new JsonArray([.. ByFile.Select(counted => (JsonNode)new JsonObject { ["of"] = counted.Of, ["count"] = counted.Count })]),
            ["unparsed"] = new JsonArray([.. Unparsed.Select(line => (JsonNode?)line)]),
            ["leftOut"] = new JsonObject
            {
                ["diagnostics"] = DiagnosticsLeftOut,
                ["failures"] = FailuresLeftOut,
                ["unparsed"] = UnparsedLeftOut,
            },
        };

    /// <summary>A thrown error as one JSON document holds it: its type, its message, its stack and its cut, and each cause whole beneath it, with how many the cause bound left below.</summary>
    private static JsonObject Thrown(ExceptionRecord thrown)
        => new()
        {
            ["type"] = thrown.Type,
            ["message"] = thrown.Message,
            ["stack"] = Frames(thrown.Stack),
            ["stackCut"] = thrown.StackCut,
            ["causedBy"] = new JsonArray([.. thrown.CausedBy.Select(cause => (JsonNode)Thrown(cause))]),
            ["causesCut"] = thrown.CausesCut,
        };

    /// <summary>A stack as one JSON document holds it: every frame whole - its text, its place, its symbol with the declaration joined to it (<see cref="StackFrame.SymbolSaid"/>), its module, its kind and whether its file is in the tree; empty where there is none.</summary>
    private static JsonArray Frames(IReadOnlyList<StackFrame> stack)
        => new([.. stack.Select(frame => (JsonNode)new JsonObject
        {
            ["text"] = frame.Text,
            ["file"] = frame.File,
            ["line"] = frame.Line,
            ["column"] = frame.Column,
            ["module"] = frame.Module,
            ["symbol"] = frame.SymbolSaid,
            ["kind"] = frame.Kind,
            ["inTree"] = frame.InTree,
        })]);

    /// <summary>
    /// The digest <paramref name="document"/> holds, read back as <see cref="Document"/> writes it - the one owner of the
    /// shape writing it and reading it, so another machine's ledger, which carries a leg's digest as that machine's
    /// <c>--json</c> wrote it, is read by the names it was written under: every diagnostic and failure with its stack and
    /// its cut, each cause chain with its cut, the counts over the whole output, the counts by code and by file, the
    /// unparsed lines and what the bounds left out; its first error the first of its errors, as every digest keeps it.
    /// Every text it holds is read through <paramref name="said"/>, as each line another machine's programs printed is read
    /// on this one. A member this build does not write is not read.
    /// </summary>
    /// <param name="document">A digest's document; null, or JSON's null, where the ledger carries none.</param>
    /// <param name="said">What each text read becomes on this machine; as it stands where none is given.</param>
    /// <returns>The digest; null where the document carries none.</returns>
    /// <exception cref="System.Text.Json.JsonException">
    /// The document is no digest's shape - a member of the wrong kind, or a diagnostic, failure, frame or exception
    /// without what each must hold - or holds what no digest does (<see cref="Problem"/>), or says it left out other than
    /// its counts and what it holds make.
    /// </exception>
    public static ResultDigest? Read(JsonNode? document, Func<string, string>? said = null)
    {
        if (document is null)
        {
            return null;
        }

        var text = said ?? (value => value);
        var read = Object(document, "a digest");
        var unparsed = Items(read, "unparsed").Select(line => text(Text(line, "an unparsed line"))).ToList();
        var leftOut = read["leftOut"] is null ? null : Object(read["leftOut"], "'leftOut'");

        var digest = new ResultDigest
        {
            Diagnostics = [.. Items(read, "diagnostics").Select(item => Object(item, "a diagnostic")).Select(diagnostic => new Diagnostic(
                Required(diagnostic, "tool"),
                Required(diagnostic, "severity"),
                Optional(diagnostic, "code"),
                text(Required(diagnostic, "message")),
                Optional(diagnostic, "file") is { } file ? text(file) : null,
                Number(diagnostic, "line"),
                Number(diagnostic, "column"),
                Number(diagnostic, "endLine"),
                Number(diagnostic, "endColumn"),
                Optional(diagnostic, "symbol"),
                Optional(diagnostic, "project") is { } project ? text(project) : null,
                Number(diagnostic, "count") ?? 1,
                StackOf(diagnostic),
                LogRange(diagnostic))
            {
                StackCut = Number(diagnostic, "stackCut") ?? 0,
            })],
            Failures = [.. Items(read, "failures").Select(item => Object(item, "a failure")).Select(failure => new TestFailure(
                Required(failure, "framework"),
                Required(failure, "id"),
                Optional(failure, "name"),
                Optional(failure, "class"),
                Optional(failure, "method"),
                Optional(failure, "message") is { } message ? text(message) : null,
                Optional(failure, "expected") is { } expected ? text(expected) : null,
                Optional(failure, "actual") is { } actual ? text(actual) : null,
                Optional(failure, "file") is { } file ? text(file) : null,
                Number(failure, "line"),
                failure["exception"] is { } thrown ? ExceptionOf(thrown, 0) : null,
                StackOf(failure),
                failure["duration"] is null ? null : failure["duration"] is JsonValue duration && duration.TryGetValue<double>(out var seconds) ? seconds : throw new System.Text.Json.JsonException("'duration' is no number"),
                Required(failure, "outcome"),
                ShardOf(failure),
                LogRange(failure))
            {
                StackCut = Number(failure, "stackCut") ?? 0,
            })],
            Errors = Number(read, "errors") ?? 0,
            Warnings = Number(read, "warnings") ?? 0,
            Notes = Number(read, "notes") ?? 0,
            Failed = Number(read, "failed") ?? 0,
            Errored = Number(read, "errored") ?? 0,
            TimedOut = Number(read, "timedOut") ?? 0,
            Crashed = Number(read, "crashed") ?? 0,
            ByCode = CountsOf(read, "byCode", of => of),
            ByFile = CountsOf(read, "byFile", text),
            Unparsed = unparsed,
            UnparsedCount = unparsed.Count + (leftOut is null ? 0 : Number(leftOut, "unparsed") ?? 0),
        };

        // Held to what every digest holds to, as one this machine made is: a severity, an outcome, a place or a bound no
        // digest has is no digest read, and neither is one whose left-out counts are not what its counts and its held make.
        return digest.Problem() is { } wrong ? throw new System.Text.Json.JsonException(wrong)
            : leftOut is not null && (Number(leftOut, "diagnostics") is { } diagnostics && diagnostics != digest.DiagnosticsLeftOut
                || Number(leftOut, "failures") is { } failures && failures != digest.FailuresLeftOut)
                ? throw new System.Text.Json.JsonException("it says it left out other than its counts and what it holds make")
            : digest;

        ExceptionRecord ExceptionOf(JsonNode node, int depth)
        {
            // Each cause is read where its holder is, its own causes after it: a chain a digest holds is never deeper than
            // its bound, and one that is was written by nothing this build reads.
            if (depth > ExceptionRecord.CauseBound)
            {
                throw new System.Text.Json.JsonException($"a cause chain is deeper than its bound of {ExceptionRecord.CauseBound.ToString(CultureInfo.InvariantCulture)}");
            }

            var thrown = Object(node, "an exception");

            return new ExceptionRecord(
                Required(thrown, "type"),
                text(Required(thrown, "message")),
                StackOf(thrown),
                [.. Items(thrown, "causedBy").Select(cause => ExceptionOf(cause!, depth + 1))])
            {
                StackCut = Number(thrown, "stackCut") ?? 0,
                CausesCut = Number(thrown, "causesCut") ?? 0,
            };
        }

        IReadOnlyList<StackFrame> StackOf(JsonObject holder)
            => [.. Items(holder, "stack").Select(item => Object(item, "a frame")).Select(frame => new StackFrame(
                text(Required(frame, "text")),
                Optional(frame, "file") is { } file ? text(file) : null,
                Number(frame, "line"),
                Number(frame, "column"),
                Optional(frame, "module"),
                Optional(frame, "symbol"),
                Optional(frame, "kind") ?? "call",
                frame["inTree"] is null ? false : frame["inTree"] is JsonValue inTree && inTree.TryGetValue<bool>(out var inside) ? inside : throw new System.Text.Json.JsonException("'inTree' is no flag")))];

        static IReadOnlyList<Counted> CountsOf(JsonObject holder, string name, Func<string, string> of)
            => [.. Items(holder, name).Select(item => Object(item, $"a count of '{name}'")).Select(counted => new Counted(
                of(Required(counted, "of")),
                Number(counted, "count") ?? throw new System.Text.Json.JsonException("'count' is missing")))];

        static (int Index, int Count)? ShardOf(JsonObject failure)
        {
            if (failure["shard"] is null)
            {
                return null;
            }

            var shard = Object(failure["shard"], "'shard'");

            return (Number(shard, "index") ?? throw new System.Text.Json.JsonException("'index' is missing"),
                Number(shard, "count") ?? throw new System.Text.Json.JsonException("'count' is missing"));
        }

        static (int From, int To)? LogRange(JsonObject holder)
            => holder["log"] switch
            {
                null => null,
                JsonArray { Count: 2 } range => (Whole(range[0], "a log range"), Whole(range[1], "a log range")),
                _ => throw new System.Text.Json.JsonException("'log' is no range of two lines"),
            };

        static JsonObject Object(JsonNode? node, string what)
            => node as JsonObject ?? throw new System.Text.Json.JsonException($"{what} is no object");

        static IEnumerable<JsonNode?> Items(JsonObject holder, string name)
            => holder[name] switch
            {
                null => [],
                JsonArray items => items,
                _ => throw new System.Text.Json.JsonException($"'{name}' is no list"),
            };

        static string Required(JsonObject holder, string name)
            => Optional(holder, name) ?? throw new System.Text.Json.JsonException($"'{name}' is missing");

        static string? Optional(JsonObject holder, string name)
            => holder[name] is null ? null : Text(holder[name], $"'{name}'");

        static string Text(JsonNode? node, string what)
            => node is JsonValue value && value.TryGetValue<string>(out var read) ? read : throw new System.Text.Json.JsonException($"{what} is no text");

        static int? Number(JsonObject holder, string name)
            => holder[name] is null ? null : Whole(holder[name], $"'{name}'");

        static int Whole(JsonNode? node, string what)
            => node is JsonValue value && value.TryGetValue<int>(out var read) ? read : throw new System.Text.Json.JsonException($"{what} is no whole number");
    }

    /// <summary>A diagnostic said as one line: its tool, its code, its place and its message's first line.</summary>
    private static string Said(Diagnostic diagnostic)
        => $"{diagnostic.Tool}{(diagnostic.Code is { } code ? $" {code}" : string.Empty)}"
            + (diagnostic.File is { } file
                ? $" {file}{(diagnostic.Line is { } line ? $"({line.ToString(CultureInfo.InvariantCulture)}{(diagnostic.Column is { } column ? $",{column.ToString(CultureInfo.InvariantCulture)}" : string.Empty)})" : string.Empty)}"
                : string.Empty)
            + $": {FirstLine(diagnostic.Message)}"
            + (diagnostic.Count > 1 ? $" (printed {diagnostic.Count.ToString(CultureInfo.InvariantCulture)} times)" : string.Empty);

    /// <summary>
    /// A failure said as one line: its framework, its test, how it failed where it did not merely fail, its place and its
    /// message's first line - where the test's id is not already that message, as a stack parser names a thrown error.
    /// </summary>
    private static string Said(TestFailure failure)
        => $"{failure.Framework} {failure.Id}"
            + (failure.Outcome == "failed" ? string.Empty : $" {failure.Outcome}")
            + (failure.File is { } file ? $" ({file}{(failure.Line is { } line ? $":{line.ToString(CultureInfo.InvariantCulture)}" : string.Empty)})" : string.Empty)
            + (MessageOf(failure) is { } message && !string.Equals(message, failure.Id, StringComparison.Ordinal) ? $": {message}" : string.Empty);

    /// <summary>A failure's message as one line: the framework's own, else - for a thrown error that says none apart - its exception's type and message.</summary>
    private static string? MessageOf(TestFailure failure)
        => FirstLine(failure.Message) ?? (failure.Exception is { } thrown ? $"{thrown.Type}: {FirstLine(thrown.Message)}" : null);

    /// <summary>The first line of a message, trimmed: the line a one-line saying says.</summary>
    private static string? FirstLine(string? message)
        => message?.Split('\n').Select(part => part.Trim()).FirstOrDefault(part => part.Length > 0);

    /// <summary>A log range, as the schema's list of whole numbers says it.</summary>
    private static JsonArray? LogOf((int From, int To)? log)
        => log is { } range ? new JsonArray(range.From, range.To) : null;

    /// <summary>
    /// A failure's stack as a model reads it: its exception's - each cause said under it, <c>caused by</c> its type and
    /// message, with its own stack rendered - else its own.
    /// </summary>
    private static JsonArray Rendered(TestFailure failure)
    {
        if (failure.Exception is not { } thrown)
        {
            return Rendered(failure.Stack, failure.StackCut);
        }

        var lines = new List<string>();

        Said(thrown);
        return new JsonArray([.. lines.Select(line => (JsonNode?)line)]);

        void Said(ExceptionRecord record)
        {
            lines.AddRange(RenderedLines(record.Stack, record.StackCut));

            foreach (var cause in record.CausedBy)
            {
                lines.Add($"caused by {cause.Type}: {FirstLine(cause.Message)}");
                Said(cause);
            }

            if (record.CausesCut > 0)
            {
                lines.Add($"... {record.CausesCut.ToString(CultureInfo.InvariantCulture)} causes deeper, left out of the record");
            }
        }
    }

    /// <summary>A stack as a model reads it (<see cref="RenderedLines"/>), as the schema's list of lines.</summary>
    private static JsonArray Rendered(IReadOnlyList<StackFrame> stack, int cut)
        => new([.. RenderedLines(stack, cut).Select(line => (JsonNode?)line)]);

    /// <summary>
    /// A stack as a model reads it: every frame inside the tree, with the declaration the tree's graph holds at its line in
    /// brackets after it where one was joined (<see cref="Declared"/>), the one frame on each side where it crosses out of
    /// it, a line for each run of frames left out - which module or package held them, where the frames name one - and,
    /// where the record's bound cut the middle, a line saying how many the log holds there.
    /// </summary>
    private static List<string> RenderedLines(IReadOnlyList<StackFrame> stack, int cut)
    {
        var lines = new List<string>();
        var run = new List<StackFrame>();

        for (var at = 0; at < stack.Count; at++)
        {
            if (cut > 0 && at == StackTrace.FirstKept)
            {
                Flush();
                lines.Add($"... {cut.ToString(CultureInfo.InvariantCulture)} frames left out of the record; the log holds them");
            }

            var frame = stack[at];

            if (frame.InTree)
            {
                Flush();
                lines.Add(frame.Declaration is { } declared ? $"{frame.Text.Trim()} [{declared.Said()}]" : frame.Text.Trim());
            }
            else
            {
                run.Add(frame);
            }
        }

        Flush();
        return lines;

        void Flush()
        {
            if (run.Count == 0)
            {
                return;
            }

            if (run.Count <= 2)
            {
                foreach (var frame in run)
                {
                    lines.Add(frame.Text.Trim());
                }
            }
            else
            {
                var left = (run.Count - 2).ToString(CultureInfo.InvariantCulture);

                lines.Add(run[0].Text.Trim());
                lines.Add(run.FirstOrDefault(frame => frame.Module is not null)?.Module is { } module
                    ? $"... {left} frames in {module}"
                    : $"... {left} frames outside the tree");
                lines.Add(run[^1].Text.Trim());
            }

            run.Clear();
        }
    }

    /// <summary>
    /// What a runtime names a project's own files by, read as the runtime reads it. A Go package by the import path of the
    /// <c>go.mod</c> that owns it - its directory below that file's, the import path less the module's path - found where
    /// the project declares it in the tree: a tree may hold several, so the declaration that answers is the one nearest the
    /// tree's root; of two equally near, the longer module path, as Go gives a package to the most specific module; of
    /// those, the first by its directory's path. The tree is read a level at a time from its root - each level's
    /// declarations read before the level below it is listed - and no deeper than a question needs, every level read at most
    /// once for the digest and each answer kept: however many places ask, of whatever names, the tree is walked once at
    /// most, so the whole placing stays linear in the tree and the places. The walk never goes down into git's own
    /// directory, at any depth, nor into the tool's own state the tree holds (<see cref="HarnessLayout.StatePaths"/>) - the
    /// copies its legs work in, its mutation workers, its agents' worktrees, its orchestrators' records - whose files are
    /// no declaration of the tree's. A Dart package by the package configuration Dart itself resolves a <c>package:</c>
    /// URI through: the <c>.dart_tool/package_config.json</c> of the package the tool ran in - beside the nearest
    /// <c>pubspec.yaml</c> above where it ran, or its workspace's - read once for the digest, each package's files below the
    /// root and the package directory it names; a package it places outside the tree - one of the pub cache - and every
    /// name where no configuration is above where the tool ran, are in no tree, and nothing of the tree is walked for them.
    /// A directory reached through a link is never walked, and one that cannot be read, or a declaration that cannot,
    /// declares nothing: the places it would have given stay as written.
    /// </summary>
    private sealed class ProjectPackages(DigestTree tree)
    {
        /// <summary>The file a Go module is declared in, at its directory's top.</summary>
        private const string GoModuleFile = "go.mod";

        /// <summary>The directive of a <c>go.mod</c> that declares its module's path.</summary>
        private const string ModuleDirective = "module";

        /// <summary>The directory, beside a Dart package's <c>pubspec.yaml</c>, that holds what its tools made of it.</summary>
        private const string DartToolDirectory = ".dart_tool";

        /// <summary>The file in <see cref="DartToolDirectory"/> that names the root of every package the package resolves.</summary>
        private const string DartPackageConfig = "package_config.json";

        /// <summary>The scheme a Dart URI names a package's file with: <c>package:name/path</c>.</summary>
        private const string DartScheme = "package:";

        /// <summary>The directory git keeps a repository's own records in, never a declaration of the tree's.</summary>
        private const string GitDirectory = ".git";

        private readonly Dictionary<string, string?> _goAsked = new(StringComparer.Ordinal);
        private readonly List<GoModule> _met = [];
        private readonly HashSet<string> _toolState = [.. HarnessLayout.StatePaths(tree.WorktreesRoot)];
        private Dictionary<string, string>? _dartLibraries;
        private List<TreeDirectory> _level = [new(tree.Root, string.Empty)];
        private bool _levelRead;
        private bool _whole;
        private int _depth;

        /// <summary>
        /// The package a Dart URI names and the file's path below the package's own directory of its URIs -
        /// <c>package:demo/store.dart</c> is <c>demo</c>'s <c>store.dart</c> - or null where it is no such URI.
        /// </summary>
        /// <param name="file">The file as the runtime wrote it.</param>
        public static (string Package, string Path)? DartPackageOf(string file)
        {
            if (!file.StartsWith(DartScheme, StringComparison.Ordinal) || file.IndexOf('/', DartScheme.Length) is not (var slash and > 0))
            {
                return null;
            }

            var package = file[DartScheme.Length..slash];
            var path = file[(slash + 1)..];

            return package.Length > 0 && path.Length > 0 ? (package, path) : null;
        }

        /// <summary>
        /// The directory of the Go package <paramref name="importPath"/> names, written as the tree's root is - the root, a
        /// <c>/</c> and its path below it - or null where no <c>go.mod</c> of the tree owns it.
        /// </summary>
        /// <param name="importPath">The package's import path, as <c>go test</c> printed it.</param>
        public string? GoPackageDirectoryOf(string importPath)
        {
            if (!_goAsked.TryGetValue(importPath, out var directory))
            {
                directory = Answered(module => importPath.StartsWith(module.Name, StringComparison.Ordinal)
                        && (importPath.Length == module.Name.Length || importPath[module.Name.Length] == '/'))
                    is { } owner
                        ? Written(owner.Relative, importPath[owner.Name.Length..].TrimStart('/'))
                        : null;
                _goAsked[importPath] = directory;
            }

            return directory;
        }

        /// <summary>
        /// The directory the Dart package named <paramref name="package"/> keeps the files its <c>package:</c> URIs name,
        /// as the package configuration of the package the tool ran in places it - written so a path below it is read
        /// against the tree's root - or null where that configuration names no such package, or there is none.
        /// </summary>
        /// <param name="package">The package's name, as a <c>package:</c> URI gives it.</param>
        public string? DartLibraryOf(string package)
        {
            _dartLibraries ??= DartLibraries();

            return _dartLibraries.GetValueOrDefault(package);
        }

        /// <summary>
        /// Each package the Dart package configuration of where the tool ran names, by its name, with the directory of its
        /// URIs: the configuration Dart reads, in the <see cref="DartToolDirectory"/> of the directory the tool ran in or of
        /// the nearest one above it, up to the tree's root - its packages' roots read against its own directory, as Dart
        /// reads them, a <c>file:</c> one as the path it names. None where no such configuration is, or it does not read.
        /// </summary>
        private Dictionary<string, string> DartLibraries()
        {
            foreach (var directory in Enclosing())
            {
                var tools = Path.Combine(directory, DartToolDirectory);
                var config = Path.Combine(tools, DartPackageConfig);

                if (!tree.FileSystem.FileExists(config))
                {
                    continue;
                }

                try
                {
                    using var read = tree.FileSystem.OpenRead(config);

                    return PackagesOf(JsonDepth.ParseStream(read), tools);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    return [];
                }
            }

            return [];
        }

        /// <summary>
        /// The directories a package configuration names, by package: each package's <c>rootUri</c> - a reference read
        /// against <paramref name="tools"/>, the configuration's own directory, its escapes decoded, or a <c>file:</c> URI as
        /// the path it names - and below it its <c>packageUri</c>, the root itself where it names none; a package of another
        /// scheme, or one that does not say its name and its root as text, is none.
        /// </summary>
        /// <param name="config">The configuration as read.</param>
        /// <param name="tools">The directory the configuration is in, as the tree's root is written.</param>
        private static Dictionary<string, string> PackagesOf(JsonNode? config, string tools)
        {
            var packages = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var package in (config?["packages"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (Text(package["name"]) is not { Length: > 0 } name || Text(package["rootUri"]) is not { Length: > 0 } rootUri)
                {
                    continue;
                }

                var root = PlatformPaths.AsAPath(rootUri) is not { } path ? null
                    : string.Equals(path, rootUri, StringComparison.Ordinal) ? $"{tools.TrimEnd('/', '\\')}/{Uri.UnescapeDataString(rootUri)}"
                    : path;

                if (root is not null)
                {
                    packages.TryAdd(name, $"{root.TrimEnd('/', '\\')}/{Uri.UnescapeDataString(Text(package["packageUri"]) ?? string.Empty)}");
                }
            }

            return packages;

            static string? Text(JsonNode? node)
                => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        }

        /// <summary>
        /// The directory the tool ran in, and each above it up to the tree's root, nearest first: only the root where it
        /// ran in the root, or anywhere the root does not hold.
        /// </summary>
        private IEnumerable<string> Enclosing()
        {
            var below = tree.WorkingDirectory is { } working ? PlatformPaths.RelativeAsWritten(tree.Root, working) : null;
            var parts = below?.Split('/') ?? [];

            for (var count = parts.Length; count > 0; count--)
            {
                yield return Path.Combine([tree.Root, .. parts[..count]]);
            }

            yield return tree.Root;
        }

        /// <summary>
        /// The <c>go.mod</c> nearest the root that <paramref name="answers"/>, the tree read further only while none of the
        /// levels read so far holds one; null where the whole tree holds none.
        /// </summary>
        private GoModule? Answered(Func<GoModule, bool> answers)
        {
            var found = Nearest(answers);

            while (found is null && !_whole)
            {
                ReadOn();
                found = Nearest(answers);
            }

            return found;
        }

        /// <summary>The declaration nearest the root among those read that <paramref name="answers"/>; null where none does.</summary>
        private GoModule? Nearest(Func<GoModule, bool> answers)
            => _met
                .Where(answers)
                .OrderBy(declared => declared.Depth)
                .ThenByDescending(declared => declared.Name.Length)
                .ThenBy(declared => declared.Relative, StringComparer.Ordinal)
                .FirstOrDefault();

        /// <summary>
        /// Reads the tree one step further: the declarations of the level listed last, or else the level below it - the
        /// whole tree read once a level below holds no directory.
        /// </summary>
        private void ReadOn()
        {
            if (!_levelRead)
            {
                foreach (var directory in _level)
                {
                    if (GoModulePathOf(Path.Combine(directory.Path, GoModuleFile)) is { } module)
                    {
                        _met.Add(new GoModule(_depth, directory.Relative, module));
                    }
                }

                _levelRead = true;
                return;
            }

            var below = new List<TreeDirectory>();

            foreach (var directory in _level)
            {
                Beneath(directory, below);
            }

            _level = below;
            _levelRead = false;
            _depth++;
            _whole = below.Count == 0;
        }

        /// <summary>
        /// Adds to <paramref name="below"/> each directory directly beneath <paramref name="directory"/> the walk goes down
        /// into: no link, no git directory and none of the tool's own state; none where it cannot be read.
        /// </summary>
        private void Beneath(TreeDirectory directory, List<TreeDirectory> below)
        {
            List<TreeDirectory> found = [];

            try
            {
                foreach (var path in tree.FileSystem.EnumerateDirectories(directory.Path))
                {
                    if (Path.GetFileName(path) is not { Length: > 0 } name || name == GitDirectory)
                    {
                        continue;
                    }

                    var relative = directory.Relative.Length == 0 ? name : $"{directory.Relative}/{name}";

                    if (!_toolState.Contains(relative) && !tree.FileSystem.IsLink(path))
                    {
                        found.Add(new TreeDirectory(path, relative));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            below.AddRange(found);
        }

        /// <summary>The module path the <c>go.mod</c> at <paramref name="file"/> declares; null where there is none, it cannot be read, or it declares none.</summary>
        private string? GoModulePathOf(string file)
        {
            if (!tree.FileSystem.FileExists(file))
            {
                return null;
            }

            try
            {
                return ModulePathOf(FileLines.Read(tree.FileSystem, file).Select(line => line.Text));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>
        /// The path a <c>go.mod</c>'s <c>module</c> directive declares, read as Go reads the file's lines: a <c>//</c>
        /// comment taken off each, the path bare or quoted (<c>"..."</c>, <c>`...`</c>), on the directive's line or alone in
        /// its <c>module ( ... )</c> block; null where the file declares none. The lines after the directive are never read.
        /// </summary>
        /// <param name="lines">The file's lines, in order.</param>
        private static string? ModulePathOf(IEnumerable<string> lines)
        {
            var block = false;

            foreach (var text in lines)
            {
                var comment = text.IndexOf("//", StringComparison.Ordinal);
                var line = (comment >= 0 ? text[..comment] : text).Trim();

                if (block)
                {
                    if (line == ")")
                    {
                        return null;
                    }

                    if (line.Length > 0)
                    {
                        return Unquoted(line);
                    }

                    continue;
                }

                if (!line.StartsWith(ModuleDirective, StringComparison.Ordinal))
                {
                    continue;
                }

                var rest = line[ModuleDirective.Length..];

                if (rest.Length > 0 && !char.IsWhiteSpace(rest[0]) && rest[0] is not ('(' or '"' or '`'))
                {
                    // A word that only begins with the directive's.
                    continue;
                }

                rest = rest.Trim();

                if (rest == "(")
                {
                    block = true;
                    continue;
                }

                return rest.Length > 0 ? Unquoted(rest) : null;
            }

            return null;
        }

        /// <summary>A module path as written, its quotes taken off; null where nothing is inside them.</summary>
        private static string? Unquoted(string said)
            => said.Length >= 2 && ((said[0] == '"' && said[^1] == '"') || (said[0] == '`' && said[^1] == '`'))
                ? said[1..^1].Trim() is { Length: > 0 } inner ? inner : null
                : said;

        /// <summary>The directory <paramref name="declared"/> and <paramref name="below"/> name below the root, written as the root is.</summary>
        private string Written(string declared, string below)
        {
            var path = string.Join('/', new[] { declared, below }.Where(part => part.Length > 0));

            return path.Length == 0 ? tree.Root : $"{tree.Root.TrimEnd('/', '\\')}/{path}";
        }

        /// <summary>A directory of the tree: as the file system names it, and its path below the root with <c>/</c>.</summary>
        private sealed record TreeDirectory(string Path, string Relative);

        /// <summary>
        /// A <c>go.mod</c> met: how many levels below the root, its directory's path below the root, and the module path it
        /// declares.
        /// </summary>
        private sealed record GoModule(int Depth, string Relative, string Name);
    }

    /// <summary>
    /// A digest made a finding at a time, in the order the output met them: the one place a digest is made, its bounds
    /// applied and every cut counted. A diagnostic printed again is folded into the one held, where it stands, its count
    /// grown by the printings it carries - so <see cref="First"/> is the first the tool met, and the fold is linear - and
    /// one past its severity's bound is counted by severity, by code and by file all the same. The unparsed lines held are
    /// the last <see cref="Bound"/> met, every one counted. Every stack is held to
    /// <see cref="StackTrace.FrameBound"/> and every cause chain to <see cref="ExceptionRecord.CauseBound"/>, the cuts said;
    /// a finding that is not one a digest holds - a severity, an outcome or a line no tool means - is said among the
    /// unparsed lines with why, never held and never dropped unsaid. A phase's two accounts of one run - the results files
    /// its runner wrote and the log it printed - are gathered into one digest, each finding said once (<see cref="Beside"/>).
    /// </summary>
    public sealed class Gathering
    {
        private readonly List<Diagnostic> _held = [];
        private readonly Dictionary<(string Tool, string? Code, string Message, string? File, int? Line, int? Column), int> _heldAt = [];
        private readonly HashSet<(string Tool, string? Code, string Message, string? File, int? Line, int? Column)> _counted = [];
        private readonly Dictionary<string, int> _bySeverity = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _heldBySeverity = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _byOutcome = new(StringComparer.Ordinal);
        private readonly List<Counted> _byCode = [];
        private readonly Dictionary<string, int> _codeAt = new(StringComparer.Ordinal);
        private readonly List<Counted> _byFile = [];
        private readonly Dictionary<string, int> _fileAt = new(StringComparer.Ordinal);
        private readonly List<TestFailure> _failures = [];
        private readonly HashSet<string> _named = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Framework, string Id, string Outcome), Dictionary<string, Queue<int?>>> _printed = [];
        private readonly Queue<string> _unparsed = new();
        private readonly Gathering? _into;
        private readonly ILineMask? _mask;
        private int _unparsedCount;

        /// <summary>An empty gathering.</summary>
        public Gathering()
        {
        }

        /// <summary>A view of <paramref name="into"/> that masks what it is given through <paramref name="mask"/>.</summary>
        private Gathering(Gathering into, ILineMask mask)
        {
            _into = into;
            _mask = mask;
        }

        /// <summary>
        /// This gathering as it gathers text no log's mask saw - a results file's: every finding and line given to it masked
        /// through <paramref name="mask"/> as it is gathered - each message, expected and actual, each test's id and name,
        /// each frame's line, each exception's message down its causes, each unparsed line - by the one rule, whichever
        /// reader made it, and gathered here; this gathering itself where there is no mask.
        /// </summary>
        /// <param name="mask">The mask the phase's log is written through; null where it is written through none.</param>
        public Gathering Through(ILineMask? mask) => mask is null ? this : new Gathering(this, mask);

        /// <summary>Gathers <paramref name="diagnostic"/>, the next the output met.</summary>
        /// <param name="diagnostic">The diagnostic, as its parser read it.</param>
        public void Add(Diagnostic diagnostic)
        {
            ArgumentNullException.ThrowIfNull(diagnostic);

            if (_into is { } into)
            {
                into.Add(diagnostic with { Message = _mask!.Redact(diagnostic.Message), Stack = Masked(diagnostic.Stack, _mask) });
                return;
            }

            var held = diagnostic with { Stack = StackTrace.Held(diagnostic.Stack, out var cut), StackCut = diagnostic.StackCut + cut };

            if (held.Problem() is { } wrong)
            {
                Unparsed($"{diagnostic.Tool}: {FirstLine(diagnostic.Message) ?? "(no message)"} - not held, for {wrong}");
                return;
            }

            Count(_byCode, _codeAt, held.Code, held.Count);
            Count(_byFile, _fileAt, held.File, held.Count);

            var identity = held.Identity;

            if (_heldAt.TryGetValue(identity, out var at))
            {
                _held[at] = _held[at] with { Count = _held[at].Count + held.Count };
                return;
            }

            if (!_counted.Add(identity))
            {
                return;
            }

            _bySeverity[held.Severity] = _bySeverity.GetValueOrDefault(held.Severity) + 1;

            if (_heldBySeverity.GetValueOrDefault(held.Severity) < Bound)
            {
                _heldBySeverity[held.Severity] = _heldBySeverity.GetValueOrDefault(held.Severity) + 1;
                _heldAt[identity] = _held.Count;
                _held.Add(held);
            }
        }

        /// <summary>Gathers <paramref name="failure"/>, the next the runner met.</summary>
        /// <param name="failure">The failure, as its reader read it.</param>
        public void Add(TestFailure failure) => Add(failure, saidThrough: null);

        /// <summary>
        /// Gathers <paramref name="failure"/>, the next the runner met - a results file's case - said through
        /// <paramref name="saidThrough"/>: the failure its test's own output printed first, whose message and place it took
        /// - so a log's printing of that same failure (<see cref="Printing"/>) under that test's output is this one's, no
        /// second failure, and gives it what the log holds of it and it lacks (<see cref="Beside"/>). The test a failure
        /// names is its own id alone; the failure it was said through names a test only by being that printing, for its id
        /// may be no test's - a thrown error's heading, where a stack form read it - and a process another printing of that
        /// error says died is another failure.
        /// </summary>
        /// <param name="failure">The failure, as its reader read it.</param>
        /// <param name="saidThrough">The failure it was said through; null where it was said through none.</param>
        public void Add(TestFailure failure, TestFailure? saidThrough)
        {
            ArgumentNullException.ThrowIfNull(failure);

            if (_into is { } into)
            {
                into.Add(Masked(failure, _mask!), saidThrough is null ? null : Masked(saidThrough, _mask!));
                return;
            }

            if (Kept(failure, out var at) && saidThrough is not null)
            {
                Printed(saidThrough, failure.Id, at);
            }
        }

        /// <summary>
        /// Gathers <paramref name="failure"/>, one more the own output of the test <paramref name="test"/> printed - a
        /// results file's case's, beside the failure the case was said through (<see cref="Add(TestFailure, TestFailure?)"/>):
        /// a failure of its own, named by its own id, whose printing in a log (<see cref="Printing"/>) under that test's
        /// output is this one's, so two tests whose output printed one failure alike each keep their own
        /// (<see cref="Beside"/>).
        /// </summary>
        /// <param name="failure">The failure, as its reader read it.</param>
        /// <param name="test">The test whose own output printed it, as the case names it.</param>
        public void AddPrintedBy(TestFailure failure, string test)
        {
            ArgumentNullException.ThrowIfNull(failure);
            ArgumentNullException.ThrowIfNull(test);

            if (_into is { } into)
            {
                into.AddPrintedBy(Masked(failure, _mask!), _mask!.Redact(test));
                return;
            }

            if (Kept(failure, out var at))
            {
                Printed(failure, test, at);
            }
        }

        /// <summary>
        /// Holds <paramref name="failure"/> - counted by its outcome and named, and kept where the bound has room - and
        /// answers whether it is one a digest holds; one that is not is said among the unparsed lines.
        /// </summary>
        /// <param name="failure">The failure, as its reader read it.</param>
        /// <param name="at">Its place in what this holds; null past the bound, where it is counted all the same.</param>
        private bool Kept(TestFailure failure, out int? at)
        {
            at = null;

            if (Held(failure) is not { } held)
            {
                return false;
            }

            _named.Add(held.Id);
            _byOutcome[held.Outcome] = _byOutcome.GetValueOrDefault(held.Outcome) + 1;

            if (_failures.Count < Bound)
            {
                at = _failures.Count;
                _failures.Add(held);
            }

            return true;
        }

        /// <summary>
        /// Records that <paramref name="test"/>'s own output printed <paramref name="printing"/>, read into the failure at
        /// <paramref name="at"/> - none past the bound, whose printing is still no second failure: the next printing of it a
        /// log holds under that test's output gives it what the log holds of it (<see cref="Beside"/>), in the order that
        /// output printed them.
        /// </summary>
        private void Printed(TestFailure printing, string test, int? at)
        {
            if (!_printed.TryGetValue(Printing(printing), out var byTest))
            {
                _printed[Printing(printing)] = byTest = new Dictionary<string, Queue<int?>>(StringComparer.Ordinal);
            }

            if (!byTest.TryGetValue(test, out var printed))
            {
                byTest[test] = printed = new Queue<int?>();
            }

            printed.Enqueue(at);
        }

        /// <summary>
        /// What makes a log's failure a printing of a failure a results file's case was said through: the same reader's
        /// failure of the same id that ended the same way - the same text read by the same parsers, as a test's own output
        /// and the log that printed it are. An error a test threw and the same error a process died of are two printings
        /// that did not end the same way.
        /// </summary>
        private static (string Framework, string Id, string Outcome) Printing(TestFailure failure) => (failure.Framework, failure.Id, failure.Outcome);

        /// <summary>
        /// Gathers <paramref name="line"/>, the next line no parser held: every one counted, the last <see cref="Bound"/>
        /// held - the nearest the failure, as a log's tail is.
        /// </summary>
        /// <param name="line">The line, masked as the log kept it.</param>
        public void Unparsed(string line)
        {
            ArgumentNullException.ThrowIfNull(line);

            if (_into is { } into)
            {
                into.Unparsed(_mask!.Redact(line));
                return;
            }

            _unparsedCount++;
            _unparsed.Enqueue(line);

            if (_unparsed.Count > Bound)
            {
                _unparsed.Dequeue();
            }
        }

        /// <summary>
        /// Gathers what a phase's log found beside what its results files gave, gathered here first - the files the runner's
        /// own typed record of its run, the log the phase's account of all it printed - so each finding is said once, by the
        /// account that holds it typed, and nothing the log said that the files lack is lost: a diagnostic the build or the
        /// runner printed, a crash, a host that died, a test that hung, a run whose files name no failure. Every diagnostic of
        /// the log that no file holds - none at the same place, as <paramref name="place"/> reads both, of the same code, or
        /// of the same message where it names no code - is gathered, in the order the log met them, and every failure of a
        /// test no file names that did not merely fail - it crashed, errored or timed out, which no results file is written
        /// for - or, where the files name no failure at all, every one. A test that merely failed is one the files hold
        /// where they hold any: its console names it in its own words (jest's <c>›</c> path against jest-junit's joined
        /// title), and it is never said twice. A failure of a test a file names, and a printing of a failure a file's case's
        /// own test printed - the one the case was said through (<see cref="Add(TestFailure, TestFailure?)"/>), the gtest test
        /// a CTest case's own output named, printed again on the console, and every other its output printed
        /// (<see cref="AddPrintedBy"/>) - is no second failure: it gives the file's failure what the file's lacks - a message
        /// where it says none, a place where it names none, its stack and its thrown error where it holds neither, and its
        /// range in the log, so the lines the log printed of it are one read away. Such a printing is the failure of the test
        /// whose own output the log printed it under (<see cref="PhaseFindings.Tests"/>: CTest's under that test's status
        /// line), never the next by the order the files met them - one gtest test two CTest tests ran keeps each its own
        /// range, whichever ended first; where the log says no test's output it lies in, it is the one test's that printed
        /// it, and where more than one did, no case is given its range, for nothing says whose it is. A thrown error a case
        /// was said through is matched by its printing alone, never by its heading: the same error a process died of, which
        /// no results file is written for, is the log's own crash, said.
        /// </summary>
        /// <param name="log">What the log's parsers found in it, in the order of its lines.</param>
        /// <param name="place">
        /// A path as a tool wrote it, read as the tree reads it - the same file named the same way whichever form wrote it, a
        /// SARIF log's URI and a console's path alike.
        /// </param>
        /// <exception cref="InvalidOperationException">This is a masked view (<see cref="Through"/>), which gathers a file's findings alone.</exception>
        public void Beside(PhaseFindings log, Func<string, string> place)
        {
            ArgumentNullException.ThrowIfNull(log);
            ArgumentNullException.ThrowIfNull(place);

            if (_into is not null)
            {
                throw new InvalidOperationException("A masked view gathers a results file's findings; a log's are gathered beside them by the gathering itself.");
            }

            var filed = _counted.Select(identity => Same(identity.Code, identity.File, identity.Line, identity.Column, identity.Message)).ToHashSet();
            var anyFiled = _byOutcome.Count > 0;
            var unnamed = new List<TestFailure>();
            var heldAt = new Dictionary<string, int>(StringComparer.Ordinal);
            var tests = log.Tests.OrderBy(test => test.From).ToList();

            for (var at = 0; at < _failures.Count; at++)
            {
                heldAt.TryAdd(_failures[at].Id, at);
            }

            // A printing of a failure a file's case's own test printed gives the one its test's output printed - the test
            // whose output the log printed it under, else the one test that printed it; a failure of a test a file names
            // gives the file's failure of that test what it lacks; any other is the log's own.
            foreach (var failure in log.Failures)
            {
                if (_printed.TryGetValue(Printing(failure), out var byTest))
                {
                    var test = Under(failure) ?? (byTest.Count == 1 ? byTest.Keys.Single() : null);

                    Give(test is not null && byTest.TryGetValue(test, out var printed) && printed.TryDequeue(out var said) ? said : null, failure);
                }
                else if (_named.Contains(failure.Id))
                {
                    Give(heldAt.TryGetValue(failure.Id, out var test) ? test : null, failure);
                }
                else if (!anyFiled || failure.Outcome != "failed")
                {
                    unnamed.Add(failure);
                }
            }

            foreach (var diagnostic in log.Diagnostics)
            {
                if (!filed.Contains(Same(diagnostic.Code, diagnostic.File, diagnostic.Line, diagnostic.Column, diagnostic.Message)))
                {
                    Add(diagnostic);
                }
            }

            foreach (var failure in unnamed)
            {
                Add(failure);
            }

            // One diagnostic, however each form writes it: its code and place, its message where it names no code.
            (string? Code, string? File, int? Line, int? Column, string? Message) Same(string? code, string? file, int? line, int? column, string message)
                => (code, file is null ? null : place(file), line, column, code is null ? message : null);

            // The test whose own output the log printed the failure's first line under, where the log says one.
            string? Under(TestFailure printed)
            {
                if (printed.Log is not { } range)
                {
                    return null;
                }

                for (int low = 0, high = tests.Count - 1; low <= high;)
                {
                    var middle = low + ((high - low) / 2);

                    if (range.From < tests[middle].From)
                    {
                        high = middle - 1;
                    }
                    else if (range.From > tests[middle].Through)
                    {
                        low = middle + 1;
                    }
                    else
                    {
                        return tests[middle].Test;
                    }
                }

                return null;
            }

            // The held failure at the place given, given what the log's printing of it holds and it lacks; nothing where it is
            // past the bound, counted already.
            void Give(int? at, TestFailure logged)
            {
                if (at is { } held && Held(logged, say: false) is { } said)
                {
                    _failures[held] = Given(_failures[held], said);
                }
            }

            // A file's failure given what the log's of the same test holds and it lacks: its stack and thrown error as one.
            static TestFailure Given(TestFailure filed, TestFailure logged)
            {
                var bare = filed.Stack.Count == 0 && filed.Exception is null;

                return filed with
                {
                    Message = filed.Message ?? logged.Message,
                    File = filed.File ?? logged.File,
                    Line = filed.File is null ? logged.Line : filed.Line,
                    Stack = bare ? logged.Stack : filed.Stack,
                    StackCut = bare ? logged.StackCut : filed.StackCut,
                    Exception = bare ? logged.Exception : filed.Exception,
                    Log = filed.Log ?? logged.Log,
                };
            }
        }

        /// <summary>
        /// <paramref name="failure"/> as a digest holds it - its stack and its cause chain held to their bounds - or null
        /// where it is no failure a digest holds, said among the unparsed lines with why where <paramref name="say"/>.
        /// </summary>
        private TestFailure? Held(TestFailure failure, bool say = true)
        {
            var held = failure with
            {
                Stack = StackTrace.Held(failure.Stack, out var cut),
                StackCut = failure.StackCut + cut,
                Exception = failure.Exception?.Held(),
            };

            if (held.Problem() is not { } wrong)
            {
                return held;
            }

            if (say)
            {
                Unparsed($"{failure.Framework} {failure.Id}: not held, for {wrong}");
            }

            return null;
        }

        /// <summary><paramref name="failure"/> with every text it holds masked through <paramref name="mask"/>.</summary>
        private static TestFailure Masked(TestFailure failure, ILineMask mask)
            => failure with
            {
                Id = mask.Redact(failure.Id),
                Name = Text(failure.Name, mask),
                Message = Text(failure.Message, mask),
                Expected = Text(failure.Expected, mask),
                Actual = Text(failure.Actual, mask),
                Stack = Masked(failure.Stack, mask),
                Exception = failure.Exception is { } thrown ? Masked(thrown, mask) : null,
            };

        /// <summary><paramref name="thrown"/> with its message and its frames masked, down its causes.</summary>
        private static ExceptionRecord Masked(ExceptionRecord thrown, ILineMask mask)
            => thrown with { Message = mask.Redact(thrown.Message), Stack = Masked(thrown.Stack, mask), CausedBy = [.. thrown.CausedBy.Select(cause => Masked(cause, mask))] };

        /// <summary><paramref name="stack"/> with each frame's line masked.</summary>
        private static IReadOnlyList<StackFrame> Masked(IReadOnlyList<StackFrame> stack, ILineMask mask)
            => [.. stack.Select(frame => frame with { Text = mask.Redact(frame.Text) })];

        /// <summary><paramref name="text"/> masked; null where there is none.</summary>
        private static string? Text(string? text, ILineMask mask) => text is null ? null : mask.Redact(text);

        /// <summary>The digest of everything gathered so far - a masked view's, of the gathering it gathers into.</summary>
        public ResultDigest Digest()
            => _into is { } into ? into.Digest() : new()
            {
                Diagnostics = [.. _held],
                Failures = [.. _failures],
                Errors = _bySeverity.GetValueOrDefault("error"),
                Warnings = _bySeverity.GetValueOrDefault("warning"),
                Notes = _bySeverity.GetValueOrDefault("note"),
                Failed = _byOutcome.GetValueOrDefault("failed"),
                Errored = _byOutcome.GetValueOrDefault("errored"),
                TimedOut = _byOutcome.GetValueOrDefault("timedOut"),
                Crashed = _byOutcome.GetValueOrDefault("crashed"),
                ByCode = [.. _byCode],
                ByFile = [.. _byFile],
                Unparsed = [.. _unparsed],
                UnparsedCount = _unparsedCount,
            };

        /// <summary>Adds <paramref name="printings"/> to what <paramref name="of"/> counts, in the order each was first met.</summary>
        private static void Count(List<Counted> counts, Dictionary<string, int> at, string? of, int printings)
        {
            if (of is null)
            {
                return;
            }

            if (at.TryGetValue(of, out var index))
            {
                counts[index] = counts[index] with { Count = counts[index].Count + printings };
            }
            else
            {
                at[of] = counts.Count;
                counts.Add(new Counted(of, printings));
            }
        }
    }
}
