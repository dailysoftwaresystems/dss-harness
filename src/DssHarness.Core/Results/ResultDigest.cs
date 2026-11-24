using System.Globalization;
using System.Text.Json.Nodes;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;

namespace DssHarness.Core.Results;

/// <summary>
/// One diagnostic a compiler, a build tool or a linter printed: what is wrong, where, how bad it is, and the chain
/// that led to it - typed once so a model reads the error and never searches a log for it. The path is relative to
/// the tree where the tree holds the file; the symbol is the declaration the graph finds at that line, where it holds
/// the file.
/// </summary>
/// <param name="Tool">The tool that printed it: <c>msbuild</c>, <c>csc</c>, <c>msvc</c>, <c>gcc</c>, <c>clang</c>, <c>cmake</c>, <c>ninja</c>, <c>link</c>, <c>ld</c>, <c>tsc</c>, <c>eslint</c>, <c>dart</c>, or a configured matcher's name.</param>
/// <param name="Severity">How bad it is: <c>error</c>, <c>warning</c> or <c>note</c>.</param>
/// <param name="Code">The tool's code for it (<c>CS0103</c>, <c>C2065</c>, <c>-Wunused-variable</c>, <c>TS2304</c>, <c>LNK2019</c>); null where the tool gives none.</param>
/// <param name="Message">The message, one line, masked; never empty.</param>
/// <param name="File">The file it names, relative to the tree where the tree holds it (<see cref="ResultDigest.WithInTree(Func{string, string?}, Func{string, string, string?})"/>); null where the tool names none.</param>
/// <param name="Line">The line it names; null where it names none.</param>
/// <param name="Column">The column it names; null where it names none.</param>
/// <param name="EndLine">The last line it names, where the tool gives a range; null where it does not.</param>
/// <param name="EndColumn">The last column it names, where the tool gives a range; null where it does not.</param>
/// <param name="Symbol">The declaration enclosing that line, from the graph, where it holds the file: its class, its method, the node's handle; null where the graph does not.</param>
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
/// <param name="Log">The line range of the log it was read from (from, to); null where it was read from no log - a results file's.</param>
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

/// <summary>How many printings one code or one file had, over the whole of what was digested.</summary>
/// <param name="Of">The code, or the file.</param>
/// <param name="Count">How many printings named it.</param>
public sealed record Counted(string Of, int Count);

/// <summary>
/// The tree a digest's places are read against: where the work ran, the directory the tool that printed them ran in -
/// which a relative path it printed is read from - and the file system that says whether the tree holds a file.
/// </summary>
/// <param name="Root">The tree the phase ran in, as the machine that ran it writes it.</param>
/// <param name="WorkingDirectory">The directory the tool ran in, which a relative path it printed is read from; <see langword="null"/> for the root.</param>
/// <param name="FileSystem">The file system the tree is on.</param>
public sealed record DigestTree(string Root, string? WorkingDirectory, IFileSystem FileSystem);

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
    /// this many of each in the order met - an error is never displaced by warnings, for each severity is held apart -
    /// the rest counted over the whole (<see cref="ByCode"/>, <see cref="ByFile"/>, the counts by severity and outcome,
    /// <see cref="UnparsedCount"/>) and said as left out.
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

    /// <summary>The lines that look like errors and matched nothing, the first <see cref="Bound"/>, masked - what the parsers did not understand; empty where they understood everything.</summary>
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
    /// This digest with what <paramref name="said"/> could not give ahead of its own unparsed lines: what a reading of
    /// results files that held no finding said of them, beside what the log's own parsers made of the phase - held to the
    /// same bound, counted the same way.
    /// </summary>
    /// <param name="said">A digest that holds no finding: its unparsed lines are what it says.</param>
    /// <exception cref="ArgumentException"><paramref name="said"/> holds a finding, which is never set aside.</exception>
    public ResultDigest Beside(ResultDigest said)
    {
        ArgumentNullException.ThrowIfNull(said);

        if (said.HoldsAFinding)
        {
            throw new ArgumentException("A digest that holds a finding is the digest, never said beside another.", nameof(said));
        }

        return this with
        {
            Unparsed = [.. said.Unparsed.Concat(Unparsed).Take(Bound)],
            UnparsedCount = UnparsedCount + said.UnparsedCount,
        };
    }

    /// <summary>
    /// This digest with every place it names read against <paramref name="tree"/>, a tree this machine ran the phase in:
    /// a file the tree holds is in the tree, named relative to its root with <c>/</c>; any other is not, and stays as the
    /// tool wrote it. A path is read in the form the tool wrote it, never by this machine's own rules
    /// (<see cref="PlatformPaths.RelativeAsWritten"/>) - a relative one from the directory the tool ran in, a
    /// <c>file:</c> URI as the path it names - and is in the tree only where the tree holds that file: a frame that names
    /// no file, a runtime's own module (<c>dart:</c>, <c>node:</c>), a bare name the tree has no file of
    /// (<c>Thread.java</c>), a path outside the root, are in none. A Go test's place is read from its package's directory,
    /// for <c>go test</c> prints it relative to that directory and names the package by its import path: the import path
    /// less the module path of the <c>go.mod</c> nearest the tree's root that owns it (<see cref="GoModules"/>); where no
    /// <c>go.mod</c> of the tree owns it, the place stays as printed. Placed by the one owner of placing a digest's paths
    /// (<see cref="WithInTree(Func{string, string?}, Func{string, string, string?})"/>), and the one place a digest's paths
    /// ask the tree's file system.
    /// </summary>
    /// <param name="tree">The tree the phase ran in, and where its tool ran.</param>
    public ResultDigest WithInTree(DigestTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentException.ThrowIfNullOrWhiteSpace(tree.Root);

        var modules = new GoModules(tree);

        return WithInTree(
            file => Held(file, tree.WorkingDirectory),
            (file, package) => modules.DirectoryOf(package) is { } directory ? Held(file, directory) : null);

        // A path the tree holds, read from the directory a relative one was printed relative to, named as the tree's.
        string? Held(string file, string? from)
            => PlatformPaths.RelativeAsWritten(tree.Root, file, from) is { } below && tree.FileSystem.FileExists(Path.Combine(tree.Root, below))
                ? below
                : null;
    }

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
    /// This digest with every text it holds masked through <paramref name="mask"/> - each message, expected and actual, each
    /// test's id and name, each frame's line, each exception's message down its causes, each unparsed line: what a results
    /// file held is masked as its phase's log is, by one rule, whichever reader made it.
    /// </summary>
    /// <param name="mask">The mask the phase's log is written through.</param>
    public ResultDigest MaskedBy(ILineMask mask)
    {
        ArgumentNullException.ThrowIfNull(mask);

        return this with
        {
            Diagnostics = [.. Diagnostics.Select(diagnostic => diagnostic with { Message = mask.Redact(diagnostic.Message), Stack = Frames(diagnostic.Stack) })],
            Failures = [.. Failures.Select(failure => failure with
            {
                Id = mask.Redact(failure.Id),
                Name = Text(failure.Name),
                Message = Text(failure.Message),
                Expected = Text(failure.Expected),
                Actual = Text(failure.Actual),
                Stack = Frames(failure.Stack),
                Exception = failure.Exception is { } thrown ? Thrown(thrown) : null,
            })],
            Unparsed = [.. Unparsed.Select(line => mask.Redact(line))],
        };

        string? Text(string? text) => text is null ? null : mask.Redact(text);

        IReadOnlyList<StackFrame> Frames(IReadOnlyList<StackFrame> stack) => [.. stack.Select(frame => frame with { Text = mask.Redact(frame.Text) })];

        ExceptionRecord Thrown(ExceptionRecord thrown)
            => thrown with { Message = mask.Redact(thrown.Message), Stack = Frames(thrown.Stack), CausedBy = [.. thrown.CausedBy.Select(Thrown)] };
    }

    /// <summary>
    /// The raw lines that go beside this digest, the one rule of every red entry that carries one - a red leg's tail and a
    /// red job's failing page alike: where the digest holds an error or a failure to start from, only the lines no parser
    /// held - a model reads the typed problems or the raw lines, never both repeated - and where it holds none, warnings
    /// and notes alone among them, <paramref name="tail"/> whole, for the line that says why the phase failed is in no
    /// diagnostic.
    /// </summary>
    /// <param name="tail">The raw lines that would go without a digest: the phase's last lines, the job's failing page.</param>
    public IReadOnlyList<string> TailBeside(IReadOnlyList<string> tail)
    {
        ArgumentNullException.ThrowIfNull(tail);

        return HoldsAProblem ? Unparsed : tail;
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
    /// the whole output), and the lines no parser held; each thing said once, and a member that says nothing - a count of
    /// none, an empty list, a null - left out at every depth, for a model pays for every byte it is handed. A rule of
    /// rendering, never of the record: the whole digest stays in the ledger's own <c>--json</c> (<see cref="Document"/>),
    /// its counts by code and by file over everything.
    /// </summary>
    public JsonObject ForAModel()
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
            ["unparsed"] = new JsonArray([.. Unparsed.Select(line => (JsonNode?)line)]),
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
    /// The first problem said whole, where it is a diagnostic: its tool, code, severity, place, count, log range, rendered
    /// stack and saying - its message's first line said once, in its saying.
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

    /// <summary>A stack as one JSON document holds it: every frame whole - its text, its place, its symbol, its module, its kind and whether its file is in the tree; empty where there is none.</summary>
    private static JsonArray Frames(IReadOnlyList<StackFrame> stack)
        => new([.. stack.Select(frame => (JsonNode)new JsonObject
        {
            ["text"] = frame.Text,
            ["file"] = frame.File,
            ["line"] = frame.Line,
            ["column"] = frame.Column,
            ["module"] = frame.Module,
            ["symbol"] = frame.Symbol,
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
                failure["exception"] is { } thrown ? ExceptionOf(thrown, 1) : null,
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
    /// A stack as a model reads it: every frame inside the tree, the one frame on each side where it crosses out of
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
                lines.Add(frame.Text.Trim());
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
    /// Where a Go package's directory is in a tree: below the directory of the <c>go.mod</c> that owns the import path
    /// <c>go test</c> names the package by, the import path less that module's path. A tree may hold several modules - and
    /// the copies of the tree the harness keeps inside it hold the same ones again, deeper - so the owner is the
    /// <c>go.mod</c> nearest the tree's root whose module path is the import path or begins it at a part's end; of two
    /// equally near, the longer module path, as Go gives a package to the most specific module; of those, the first by its
    /// directory's path. The tree is read a level at a time from its root - each level's <c>go.mod</c> files read before
    /// the level below it is listed - and no deeper than the first level that holds an owner, so a tree whose root holds
    /// the module is read no further than its root; each directory is read once, whatever is asked. A directory reached
    /// through a link is never walked, and one that cannot be read, or a <c>go.mod</c> that cannot, owns nothing: the
    /// places it would have given stay as printed.
    /// </summary>
    private sealed class GoModules(DigestTree tree)
    {
        /// <summary>The file a Go module is declared in, at its directory's top.</summary>
        private const string ModuleFile = "go.mod";

        /// <summary>The directive that declares a module's path.</summary>
        private const string ModuleDirective = "module";

        private readonly Dictionary<string, string?> _asked = new(StringComparer.Ordinal);
        private readonly List<Module> _met = [];
        private List<TreeDirectory> _level = [new(tree.Root, string.Empty)];
        private bool _levelRead;
        private int _depth;

        /// <summary>
        /// The directory of the package <paramref name="importPath"/> names, written as the tree's root is - the root, a
        /// <c>/</c> and its path below it - or null where no <c>go.mod</c> of the tree owns it.
        /// </summary>
        /// <param name="importPath">The package's import path, as <c>go test</c> printed it.</param>
        public string? DirectoryOf(string importPath)
        {
            if (_asked.TryGetValue(importPath, out var known))
            {
                return known;
            }

            var owner = OwnerOf(importPath);

            while (owner is null && ReadOn())
            {
                owner = OwnerOf(importPath);
            }

            var directory = owner is null ? null : Written(owner.Relative, importPath[owner.Declared.Length..].TrimStart('/'));

            _asked[importPath] = directory;
            return directory;
        }

        /// <summary>The owner of <paramref name="importPath"/> among the modules of every level read so far; null where none is.</summary>
        private Module? OwnerOf(string importPath)
            => _met
                .Where(module => importPath.StartsWith(module.Declared, StringComparison.Ordinal)
                    && (importPath.Length == module.Declared.Length || importPath[module.Declared.Length] == '/'))
                .OrderBy(module => module.Depth)
                .ThenByDescending(module => module.Declared.Length)
                .ThenBy(module => module.Relative, StringComparer.Ordinal)
                .FirstOrDefault();

        /// <summary>
        /// Reads the tree one step further - the <c>go.mod</c> files of the level listed last, or else the level below it -
        /// and says whether there was anything left to read.
        /// </summary>
        private bool ReadOn()
        {
            if (!_levelRead)
            {
                foreach (var directory in _level)
                {
                    if (ModuleOf(Path.Combine(directory.Path, ModuleFile)) is { } path)
                    {
                        _met.Add(new Module(_depth, directory.Relative, path));
                    }
                }

                _levelRead = true;
                return true;
            }

            var below = new List<TreeDirectory>();

            foreach (var directory in _level)
            {
                Beneath(directory, below);
            }

            _level = below;
            _levelRead = false;
            _depth++;
            return below.Count > 0;
        }

        /// <summary>Adds to <paramref name="below"/> each directory directly beneath <paramref name="directory"/> that is no link; none where it cannot be read.</summary>
        private void Beneath(TreeDirectory directory, List<TreeDirectory> below)
        {
            List<TreeDirectory> found = [];

            try
            {
                foreach (var path in tree.FileSystem.EnumerateDirectories(directory.Path))
                {
                    if (Path.GetFileName(path) is { Length: > 0 } name && !tree.FileSystem.IsLink(path))
                    {
                        found.Add(new TreeDirectory(path, directory.Relative.Length == 0 ? name : $"{directory.Relative}/{name}"));
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
        private string? ModuleOf(string file)
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

        /// <summary>The directory <paramref name="module"/> and <paramref name="package"/> name below the root, written as the root is.</summary>
        private string Written(string module, string package)
        {
            var below = string.Join('/', new[] { module, package }.Where(part => part.Length > 0));

            return below.Length == 0 ? tree.Root : $"{tree.Root.TrimEnd('/', '\\')}/{below}";
        }

        /// <summary>A directory of the tree: as the file system names it, and its path below the root with <c>/</c>.</summary>
        private sealed record TreeDirectory(string Path, string Relative);

        /// <summary>A <c>go.mod</c> met: how many levels below the root, its directory's path below the root, and the module path it declares.</summary>
        private sealed record Module(int Depth, string Relative, string Declared);
    }

    /// <summary>
    /// A digest made a finding at a time, in the order the output met them: the one place a digest is made, its bounds
    /// applied and every cut counted. A diagnostic printed again is folded into the one held, where it stands, its count
    /// grown by the printings it carries - so <see cref="First"/> is the first the tool met, and the fold is linear - and
    /// one past its severity's bound is counted by severity, by code and by file all the same. Every stack is held to
    /// <see cref="StackTrace.FrameBound"/> and every cause chain to <see cref="ExceptionRecord.CauseBound"/>, the cuts said;
    /// a finding that is not one a digest holds - a severity, an outcome or a line no tool means - is said among the
    /// unparsed lines with why, never held and never dropped unsaid.
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
        private readonly List<string> _unparsed = [];
        private int _unparsedCount;

        /// <summary>Gathers <paramref name="diagnostic"/>, the next the output met.</summary>
        /// <param name="diagnostic">The diagnostic, as its parser read it.</param>
        public void Add(Diagnostic diagnostic)
        {
            ArgumentNullException.ThrowIfNull(diagnostic);

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
        public void Add(TestFailure failure)
        {
            ArgumentNullException.ThrowIfNull(failure);

            var held = failure with
            {
                Stack = StackTrace.Held(failure.Stack, out var cut),
                StackCut = failure.StackCut + cut,
                Exception = failure.Exception?.Held(),
            };

            if (held.Problem() is { } wrong)
            {
                Unparsed($"{failure.Framework} {failure.Id}: not held, for {wrong}");
                return;
            }

            _byOutcome[held.Outcome] = _byOutcome.GetValueOrDefault(held.Outcome) + 1;

            if (_failures.Count < Bound)
            {
                _failures.Add(held);
            }
        }

        /// <summary>Gathers <paramref name="line"/>, the next line no parser held.</summary>
        /// <param name="line">The line, masked as the log kept it.</param>
        public void Unparsed(string line)
        {
            ArgumentNullException.ThrowIfNull(line);

            _unparsedCount++;

            if (_unparsed.Count < Bound)
            {
                _unparsed.Add(line);
            }
        }

        /// <summary>The digest of everything gathered so far.</summary>
        public ResultDigest Digest()
            => new()
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
