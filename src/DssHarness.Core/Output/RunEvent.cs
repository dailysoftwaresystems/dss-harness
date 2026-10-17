namespace DssHarness.Core.Output;

/// <summary>
/// Which member of <see cref="IHarnessOutput"/> a line of text came from, so a renderer writes it to the stream that
/// member writes to, in the shape that member gives it.
/// </summary>
public enum LineKind
{
    /// <summary>A success line: <c>&lt;command&gt;: OK - &lt;message&gt;</c>.</summary>
    Ok,

    /// <summary>A failure line: <c>&lt;command&gt;: FAIL - &lt;message&gt;</c>, the line <see cref="FailureLine"/> spells.</summary>
    Fail,

    /// <summary>A warning line: <c>&lt;command&gt;: WARN - &lt;message&gt;</c>.</summary>
    Warn,

    /// <summary>A progress line: <c>&lt;command&gt;: &lt;message&gt;</c>.</summary>
    Info,

    /// <summary>A progress line shown only when verbose: <c>&lt;command&gt;: &lt;message&gt;</c>.</summary>
    Detail,

    /// <summary>A command's answer, unprefixed.</summary>
    Data,

    /// <summary>A line of a child process's own output, unprefixed.</summary>
    Raw,

    /// <summary>A line of a child process's own error output, unprefixed - the stream a host's notes ride on.</summary>
    RawError,
}

/// <summary>
/// What the console says of an event, if anything: the line it prints, in the terms of the member of
/// <see cref="IHarnessOutput"/> that prints it.
/// </summary>
/// <param name="Kind">Which member's line this is.</param>
/// <param name="Command">The command the line names, where the line is one of the harness's own; <see langword="null"/> for a line that carries none.</param>
/// <param name="Text">The line's own text: the message for a line of the harness's own, the line as it was given for a child's.</param>
/// <param name="VerboseOnly">Whether the line is shown only when verbose.</param>
public readonly record struct TextForm(LineKind Kind, string? Command, string Text, bool VerboseOnly = false);

/// <summary>
/// What happened while this tool ran, typed, in the order it happened: what a front end subscribes to beside the lines
/// a terminal shows.
/// </summary>
/// <remarks>
/// Events are the one vocabulary and lines are their rendering: a line the harness writes exists once, as the text form
/// of the event that caused it, so a terminal and any other front end cannot be told two different things. An event
/// that carries a path carries it absolute, as the service that knows it gave it.
/// </remarks>
public abstract record RunEvent
{
    /// <summary>The operation the event happened in, where the sink knows one; <see langword="null"/> for an operation that has no id.</summary>
    public OperationId? Operation { get; init; }

    /// <summary>The operation this event's operation belongs to - a step of a run, naming the run - where there is one.</summary>
    public OperationId? Parent { get; init; }

    /// <summary>Where this event falls among the events of its output: assigned as it is published, never by its maker.</summary>
    public long Sequence { get; init; }

    /// <summary>When the event was published.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>What the console says of this event, if anything: the first is the line it prints.</summary>
    public virtual IEnumerable<TextForm> TextForms() => [];
}

/// <summary>
/// A sink of <see cref="RunEvent"/>s, called serially in <see cref="RunEvent.Sequence"/> order.
/// </summary>
/// <remarks>
/// The console's own sink is called under the publisher's lock, so lines keep their order on the terminal; any other
/// sink is fed from a bounded queue drained by a task of its own, so a slow one never holds a writer.
/// </remarks>
public interface IRunEvents
{
    /// <summary>Takes one event, in order.</summary>
    /// <param name="event">The event.</param>
    void Emit(RunEvent @event);
}
