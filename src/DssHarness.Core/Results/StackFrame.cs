using System.Globalization;

namespace DssHarness.Core.Results;

/// <summary>
/// One frame of a stack a tool printed - a thrown error's call stack, a compiler's include or instantiation chain,
/// an awaited boundary a runtime marked - typed once so every digest holds one shape of it: the line as printed,
/// where it names a place, the symbol it names, what kind of frame it is, and whether its file is inside the tree.
/// </summary>
/// <param name="Text">The frame's line as the tool printed it, masked.</param>
/// <param name="File">The file the frame names, as the tool gave it, the path made relative to the tree where it is inside it; null where it names none.</param>
/// <param name="Line">The line the frame names; null where it names none.</param>
/// <param name="Column">The column the frame names; null where it names none.</param>
/// <param name="Module">The assembly, library, package or binary the frame names; null where it names none.</param>
/// <param name="Symbol">The frame's function as printed - <c>Namespace.Class.Method</c>, <c>ns::f&lt;T&gt;</c>; null where it names none. Where the tree's graph holds the frame's file (<see cref="ResultDigest.WithInTree(DigestTree)"/>), the declaration enclosing its line is said with it, in brackets - its kind, its qualified name, the node's handle (<see cref="EnclosingDeclaration.Said"/>) - or alone where the frame printed none.</param>
/// <param name="Kind">Which kind of frame it is: <c>call</c>, <c>include</c>, <c>instantiation</c>, <c>macro</c> or <c>async</c>.</param>
/// <param name="InTree">Whether the tree holds the frame's file (<see cref="ResultDigest.WithInTree(Func{string, string?}, Func{string, string, string?})"/>); false where it names no file.</param>
public sealed record StackFrame(
    string Text,
    string? File = null,
    int? Line = null,
    int? Column = null,
    string? Module = null,
    string? Symbol = null,
    string Kind = "call",
    bool InTree = false)
{
    /// <summary>The kinds a frame can be.</summary>
    public static readonly IReadOnlyList<string> Kinds = ["call", "include", "instantiation", "macro", "async"];

    /// <summary>What is wrong with this frame; null when nothing is.</summary>
    public string? Problem()
        => string.IsNullOrWhiteSpace(Text) ? "a frame holds no line"
            : !Kinds.Contains(Kind, StringComparer.Ordinal) ? $"'{Kind}' is not a kind a frame is"
            : Line is < 1 ? $"a frame names line {Line?.ToString(CultureInfo.InvariantCulture)}"
            : Column is < 1 ? $"a frame names column {Column?.ToString(CultureInfo.InvariantCulture)}"
            : null;
}

/// <summary>
/// A thrown error, typed: its type, its message, the stack the tool printed for it, and its causes - the inner
/// exceptions or cause chain, each an <see cref="ExceptionRecord"/> of its own, nested as the chain is. One shape is
/// read by a test failure, by a run check's failure and by a step attempt of the engine's that ended in an error it did
/// not expect.
/// </summary>
/// <param name="Type">The exception's or error's type as printed (<c>InvalidOperationException</c>, <c>panic</c>); never empty.</param>
/// <param name="Message">The message as printed, masked; empty where the error printed none, as a JVM's bare <c>NullPointerException</c> and a Python <c>AssertionError</c> may.</param>
/// <param name="Stack">The stack the tool printed, innermost first, as the tool printed it, held to <see cref="StackTrace.FrameBound"/> with <see cref="StackCut"/> the frames left out; empty where none was.</param>
/// <param name="CausedBy">
/// The causes it names - an inner exception, a <c>Caused by:</c>, each of an aggregate's - each a record holding its own
/// causes in turn, followed <see cref="CauseBound"/> deep with <see cref="CausesCut"/> the records left below; empty where
/// it names none.
/// </param>
public sealed record ExceptionRecord(
    string Type,
    string Message,
    IReadOnlyList<StackFrame> Stack,
    IReadOnlyList<ExceptionRecord> CausedBy)
{
    /// <summary>How many causes deep a chain is followed below its outermost record: the cause this many below it holds no causes of its own, and counts them.</summary>
    public const int CauseBound = 8;

    /// <summary>How many of the stack's frames <see cref="Held"/> left out of the middle - which the log still holds; 0 where none were.</summary>
    public int StackCut { get; init; }

    /// <summary>How many cause records below <see cref="CauseBound"/> <see cref="Held"/> left out, every one below this record counted; 0 where none were.</summary>
    public int CausesCut { get; init; }

    /// <summary>What is wrong with this record; null when nothing is.</summary>
    public string? Problem() => ProblemAt(0);

    /// <summary>
    /// This record held to the bounds every record a digest keeps is held to - its stack and each cause's to
    /// <see cref="StackTrace.FrameBound"/>, its causes followed <see cref="CauseBound"/> deep - each cut counted where it
    /// was made, never left out unsaid. The one place a record is held: a parser and the engine read a chain whole, and
    /// the bound is applied here.
    /// </summary>
    public ExceptionRecord Held() => HeldAt(0);

    /// <summary>
    /// The record of <paramref name="thrown"/>, as the engine keeps an error it did not expect: its type, its message, its
    /// stack as the runtime prints it - read by the one owner of the .NET frame form - and each inner exception nested
    /// under the one it caused, an aggregate's every one, held as every record is (<see cref="Held"/>). Its frames are the
    /// tool's own and in no tree of the run's.
    /// </summary>
    /// <param name="thrown">The exception.</param>
    public static ExceptionRecord Of(Exception thrown)
    {
        ArgumentNullException.ThrowIfNull(thrown);

        // Each exception once, however its chain loops back on itself - a cause already met is no deeper cause - and
        // read a level at a time, so a chain of any depth is read whole before it is held: each record is made once its
        // causes are, the innermost first.
        var met = new HashSet<Exception>(ReferenceEqualityComparer.Instance) { thrown };
        var open = new Stack<(Exception Exception, IReadOnlyList<Exception> Inner, List<ExceptionRecord> Made)>();
        ExceptionRecord? made = null;

        Open(thrown);

        while (open.Count > 0)
        {
            var (exception, inner, records) = open.Peek();

            if (records.Count < inner.Count)
            {
                Open(inner[records.Count]);
                continue;
            }

            open.Pop();
            made = new ExceptionRecord(
                exception.GetType().FullName ?? exception.GetType().Name,
                exception.Message,
                StackForms.DotNetFramesOf(Text.LineEnds.Lines(exception.StackTrace ?? string.Empty)),
                records);

            if (open.Count > 0)
            {
                open.Peek().Made.Add(made);
            }
        }

        return made!.Held();

        void Open(Exception exception)
        {
            IEnumerable<Exception> inner = exception is AggregateException aggregate ? aggregate.InnerExceptions : exception.InnerException is { } one ? [one] : [];

            open.Push((exception, [.. inner.Where(met.Add)], []));
        }
    }

    /// <summary>How many records are below this one, its causes and theirs, all the way down - counted a record at a time, however deep the chain.</summary>
    private int Below
    {
        get
        {
            var count = 0;
            var waiting = new Stack<ExceptionRecord>(CausedBy);

            while (waiting.TryPop(out var cause))
            {
                count++;

                foreach (var deeper in cause.CausedBy)
                {
                    waiting.Push(deeper);
                }
            }

            return count;
        }
    }

    private ExceptionRecord HeldAt(int depth)
    {
        var stack = StackTrace.Held(Stack, out var cut);

        return depth >= CauseBound
            ? this with { Stack = stack, StackCut = StackCut + cut, CausedBy = [], CausesCut = CausesCut + Below }
            : this with { Stack = stack, StackCut = StackCut + cut, CausedBy = [.. CausedBy.Select(cause => cause.HeldAt(depth + 1))] };
    }

    private string? ProblemAt(int depth)
        => string.IsNullOrWhiteSpace(Type) ? "an exception names no type"
            : Stack.Count > StackTrace.FrameBound ? $"its stack holds {Stack.Count.ToString(CultureInfo.InvariantCulture)} frames, over the bound of {StackTrace.FrameBound.ToString(CultureInfo.InvariantCulture)}"
            : StackCut < 0 || CausesCut < 0 ? "it says a negative cut"
            : Stack.Select(frame => frame.Problem()).FirstOrDefault(wrong => wrong is not null) is { } frameWrong ? frameWrong
            : depth >= CauseBound && CausedBy.Count > 0 ? $"its cause chain is over {CauseBound.ToString(CultureInfo.InvariantCulture)} deep"
            : CausedBy.Select(cause => cause.ProblemAt(depth + 1)).FirstOrDefault(wrong => wrong is not null);
}

/// <summary>The bounds every stack a digest holds is kept to.</summary>
public static class StackTrace
{
    /// <summary>The most frames a stack holds.</summary>
    public const int FrameBound = 64;

    /// <summary>How many of a stack's first frames are kept.</summary>
    public const int FirstKept = 48;

    /// <summary>How many of a stack's last frames are kept.</summary>
    public const int LastKept = 16;

    /// <summary>
    /// <paramref name="frames"/> held to the bounds, with how many were left out: the first <see cref="FirstKept"/>
    /// and the last <see cref="LastKept"/>, the middle cut - which the log still holds. The one owner of the frame bound:
    /// every record of a digest is held through it, and says its cut.
    /// </summary>
    /// <param name="frames">The frames as the tool printed them.</param>
    /// <param name="cut">How many were left out; 0 where none were.</param>
    public static IReadOnlyList<StackFrame> Held(IReadOnlyList<StackFrame> frames, out int cut)
    {
        ArgumentNullException.ThrowIfNull(frames);

        if (frames.Count <= FrameBound)
        {
            cut = 0;
            return frames;
        }

        cut = frames.Count - FirstKept - LastKept;
        return [.. frames.Take(FirstKept), .. frames.Skip(frames.Count - LastKept)];
    }
}
