namespace DssHarness.Core.Output;

/// <summary>
/// A line the harness itself wrote, through one of the five members of <see cref="IHarnessOutput"/> that write a line
/// of its own - <see cref="IHarnessOutput.Ok"/>, <see cref="IHarnessOutput.Fail"/>, <see cref="IHarnessOutput.Warn"/>,
/// <see cref="IHarnessOutput.Info"/> and <see cref="IHarnessOutput.Detail"/>: the adaptation of a legacy call into an
/// event, and the one definition of the line.
/// </summary>
/// <param name="Kind">Which member's line this is.</param>
/// <param name="Command">The command the line names; <see langword="null"/> only where the member takes none, which none does.</param>
/// <param name="Message">The message as it was given, before the home shorthand and the quoted-line exemption are applied at the rendering.</param>
public sealed record TextLine(LineKind Kind, string Command, string Message) : RunEvent
{
    /// <inheritdoc/>
    public override IEnumerable<TextForm> TextForms()
        => [new(Kind, Command, Message, VerboseOnly: Kind == LineKind.Detail)];
}

/// <summary>
/// A line of a child process's own output, passed through as it came, on the stream of its own - and the stream a
/// host's notes ride on, read back by the machine that asked and never shortened by the home shorthand.
/// </summary>
/// <param name="IsError">Whether the line is the child's error output.</param>
/// <param name="Line">The line as the child wrote it.</param>
public sealed record PassthroughLine(bool IsError, string Line) : RunEvent
{
    /// <inheritdoc/>
    public override IEnumerable<TextForm> TextForms()
        => [new(IsError ? LineKind.RawError : LineKind.Raw, null, Line)];
}

/// <summary>A piece of a command's answer, written unprefixed to standard output: a listing, a detail block, a document.</summary>
/// <param name="Text">The text as the command gave it.</param>
public sealed record DataWritten(string Text) : RunEvent
{
    /// <inheritdoc/>
    public override IEnumerable<TextForm> TextForms()
        => [new(LineKind.Data, null, Text)];
}

/// <summary>
/// A record written to disk: a run's directory made, an orchestrator's or a step's record kept. Nothing the terminal
/// says - the path is already in the lines that name what was done - but the fact, with its path, for a front end.
/// </summary>
/// <param name="Kind">What the record is, in the words the details of the command that wrote it say.</param>
/// <param name="Path">Where the record is, absolute, as the writer that knows it gave it.</param>
public sealed record RecordWritten(string Kind, string Path) : RunEvent;

/// <summary>
/// Events a queued sink was not given, said to it where they fell: after the last event it was given before them and
/// before the first after them - or last, where none came after - stamped with the operation and with the sequence and
/// time of the last of them. Where its queue was full an event was dropped and counted, so a front end knows it missed
/// some and reads what it needs from the run's files, which hold it whole.
/// </summary>
/// <param name="Count">How many events were dropped.</param>
public sealed record EventsDropped(int Count) : RunEvent;
