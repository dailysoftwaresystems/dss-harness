using System.Globalization;
using System.Text.Json.Serialization;

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
/// <param name="Symbol">The frame's function as printed - <c>Namespace.Class.Method</c>, <c>ns::f&lt;T&gt;</c>; null where it names none.</param>
/// <param name="Kind">Which kind of frame it is: <c>call</c>, <c>include</c>, <c>instantiation</c>, <c>macro</c> or <c>async</c>.</param>
/// <param name="InTree">Whether the frame's file is inside the tree; false where it names no file.</param>
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
/// exceptions or cause chain, each an <see cref="ExceptionRecord"/> of its own. One shape is read by a test failure,
/// by a run check's failure and by a step attempt of the engine's that ended in an error it did not expect.
/// </summary>
/// <param name="Type">The exception's or error's type as printed (<c>InvalidOperationException</c>, <c>panic</c>); never empty.</param>
/// <param name="Message">The message as printed, masked; never empty.</param>
/// <param name="Stack">The stack the tool printed, innermost first, as the tool printed it; empty where none was.</param>
/// <param name="CausedBy">The causes it names, outermost first, each followed at most <see cref="CauseBound"/> deep; empty where it names none.</param>
public sealed record ExceptionRecord(
    string Type,
    string Message,
    IReadOnlyList<StackFrame> Stack,
    IReadOnlyList<ExceptionRecord> CausedBy)
{
    /// <summary>How deep a cause chain is followed.</summary>
    public const int CauseBound = 8;

    /// <summary>What is wrong with this record; null when nothing is.</summary>
    public string? Problem()
        => string.IsNullOrWhiteSpace(Type) ? "an exception names no type"
            : string.IsNullOrWhiteSpace(Message) ? "an exception says no message"
            : Stack.Count > StackTrace.FrameBound ? $"its stack holds {Stack.Count.ToString(CultureInfo.InvariantCulture)} frames, over the bound of {StackTrace.FrameBound.ToString(CultureInfo.InvariantCulture)}"
            : Stack.Any(frame => frame.Problem() is { } wrong) ? Stack.First(frame => frame.Problem() is not null).Problem()
            : CausedBy.Count > CauseBound ? $"its cause chain holds {CausedBy.Count.ToString(CultureInfo.InvariantCulture)}, over the bound of {CauseBound.ToString(CultureInfo.InvariantCulture)}"
            : CausedBy.Any(cause => cause.Problem() is { } wrong) ? CausedBy.First(cause => cause.Problem() is not null).Problem()
            : null;
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
    /// and the last <see cref="LastKept"/>, the middle cut - which the log still holds.
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
