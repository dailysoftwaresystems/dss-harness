using RepoHarness.Core.Output;

namespace RepoHarness.Core.Execution;

/// <summary>
/// The inputs of a span of work could not be watched, so the span was not begun: no phase of it ran. Work a verdict
/// hangs on ends unmeasured, saying this - never run first and called unmeasured after, which spends the whole of the
/// work to learn what was known before it began.
/// </summary>
/// <remarks>
/// Every reason a watch cannot start is a member here, with its sentence, and no caller spells one. A watch that
/// started and was lost while the work ran is another matter: the work has run by then, and the span says so as it ends.
/// </remarks>
public sealed class InputsUnwatchedException : Exception
{
    /// <summary>What every such message opens with, before its reason.</summary>
    public const string Opening = "the inputs could not be watched, so none of this work was run: ";

    private InputsUnwatchedException(string why, Exception? innerException = null)
        : base(Opening + why, innerException)
    {
        Why = why;
    }

    /// <summary>The reason alone, as the message ends with it.</summary>
    public string Why { get; }

    /// <summary>The operating system would not watch, and said why.</summary>
    /// <param name="refusal">What it raised; its message is the reason, in the system's own words.</param>
    public static InputsUnwatchedException BySystem(Exception refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);

        return new InputsUnwatchedException(refusal.Message, refusal);
    }

    /// <summary>The tree the inputs are in is not there, or is no directory.</summary>
    /// <param name="root">The tree.</param>
    public static InputsUnwatchedException NoTree(string root)
        => new($"'{ReportText.Printable(root)}' is not a directory");

    /// <summary>None of the directories holding an input is there, so there is nothing a watch could be put on.</summary>
    /// <param name="inputs">How many inputs were to be watched.</param>
    /// <param name="root">The tree they are in.</param>
    public static InputsUnwatchedException NoDirectory(int inputs, string root)
        => new($"no directory holding any of the {ReportText.Counted(inputs, "input")} is present under '{ReportText.Printable(root)}'");
}
