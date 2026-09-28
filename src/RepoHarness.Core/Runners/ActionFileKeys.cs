using RepoHarness.Core.Configuration;
using RepoHarness.Core.Platform;

namespace RepoHarness.Core.Runners;

/// <summary>
/// Every key an action file takes, with what each does: the parser reads exactly these, and
/// <c>help runners</c> lists them.
/// </summary>
/// <remarks>
/// One list for both, so a key cannot be read without being listed, nor listed without being read:
/// the parser refuses a key missing from here before it looks at it, and a key here that it has no
/// reader for is a defect it reports as one. A key a step shares with a runner's phase says what the
/// phase's says, read from the phase's own key.
/// </remarks>
public static class ActionFileKeys
{
    /// <summary>What one declared input takes, an action's or a step's.</summary>
    public static IReadOnlyList<KeyDescription> Input { get; } =
    [
        new("default", "its value when neither --input nor the runner's values give one"),
        new("required", "refuse a run that gives it no value"),
        new("description", "what the value is for"),
    ];

    /// <summary>What one step takes.</summary>
    public static IReadOnlyList<KeyDescription> Step { get; } =
    [
        new("name", "names its log file and its own directory") { Required = true },
        new("uses", $"{string.Join(" or ", PredefinedActions.All)}, in place of run"),
        new("ref", $"the commit or branch {PredefinedActions.Checkout} confirms the tree is at"),
        new("run", "its program lines, one per line; no shell"),
        new("workingDirectory", "where it runs, under workingDirectoryRoot; absent, that root"),
        new("workingDirectoryRoot", $"what workingDirectory starts from: {string.Join(", ", WorkingDirectoryRoots.All)}"),
        new("runOn", $"the systems it runs on, of {string.Join(", ", PlatformNames.OperatingSystems)}; absent, all"),
        Phase("env"),
        new("successPattern", "what its last run line's output must match, besides exiting 0"),
        Phase("stallSeconds"),
        Phase("continueOnError"),
        Phase("watchContention"),
        Phase("requireInputsUnmoved"),
        new("outputs", "files it writes, relative to its own directory"),
        new("persist", "keep its outputs when the action finishes"),
        new("manual", "run only where a run names it"),
        new("needs", "steps declared before it that run first"),
        new("inputs", "values this step alone reads, each by name") { Keys = Input },
    ];

    /// <summary>What the file itself takes.</summary>
    public static IReadOnlyList<KeyDescription> File { get; } =
    [
        new("name", "what the action is called"),
        new("description", "what it does"),
        new("inputs", "values its steps read, each by name") { Keys = Input },
        new("steps", "what it runs, in order") { Required = true, Keys = Step },
    ];

    private static KeyDescription Phase(string name) => ConfigKeys.Of<RunnerPhase>().Single(key => key.Name == name);
}
