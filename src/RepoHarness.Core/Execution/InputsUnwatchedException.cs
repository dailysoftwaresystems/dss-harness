using System.Globalization;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;

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

    /// <summary>The operating system would not watch, and said why in <paramref name="words"/>.</summary>
    /// <param name="words">The system's own words for what it refused, and of what.</param>
    internal static InputsUnwatchedException BySystem(string words) => new(words);

    /// <summary>
    /// This user's inotify instances are all taken, so none could be made for the watch: said with the limit, and with
    /// who holds them where <c>/proc</c> shows it.
    /// </summary>
    /// <param name="use">What <c>/proc</c> showed as the instance was refused.</param>
    internal static InputsUnwatchedException InstancesTaken(InotifyUse use)
    {
        ArgumentNullException.ThrowIfNull(use);

        return new(
            "this user's inotify instances are all taken"
            + Limit("fs.inotify.max_user_instances", use.MaxInstances)
            + Shown(use.Held(holder => holder.Instances))
            + ". A leg needs one.");
    }

    /// <summary>
    /// This user's inotify watches ran out before every directory of the work's inputs was watched: said with the
    /// limit, how many the work needs, and who holds them where <c>/proc</c> shows it.
    /// </summary>
    /// <param name="use">What <c>/proc</c> showed as the watch was refused.</param>
    /// <param name="directories">How many directories the work watches - those its inputs lie in and those above them: a watch each.</param>
    internal static InputsUnwatchedException WatchesRanOut(InotifyUse use, int directories)
    {
        ArgumentNullException.ThrowIfNull(use);

        return new(
            "this user's inotify watches ran out"
            + Limit("fs.inotify.max_user_watches", use.MaxWatches)
            + $"; this work watches {ReportText.Counted(directories, "directory")} - those its inputs lie in and those above them - one watch each"
            + Shown(use.Held(holder => holder.Watches))
            + ".");
    }

    /// <summary>The tree the inputs are in is not there, or is no directory.</summary>
    /// <param name="root">The tree.</param>
    public static InputsUnwatchedException NoTree(string root)
        => new($"'{ReportText.Printable(root)}' is not a directory");

    /// <summary><c> - key is N</c>, or nothing where the limit could not be read.</summary>
    private static string Limit(string key, long? limit) => limit is { } value ? $" - {key} is {value.ToString(CultureInfo.InvariantCulture)}" : string.Empty;

    /// <summary><c>, and /proc shows ...</c>, or nothing where it could not be read or shows none held.</summary>
    private static string Shown(string? held) => held is null ? string.Empty : $", and /proc shows {held}";
}
