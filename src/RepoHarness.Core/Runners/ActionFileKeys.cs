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
/// reader for is a defect it reports as one. A key a step shares with a runner's phase, where it
/// means the same on both, says what the phase's says, read from the phase's own key.
/// </remarks>
public static class ActionFileKeys
{
    /// <summary>What one declared input takes, an action's or a step's.</summary>
    public static IReadOnlyList<KeyDescription> Input { get; } =
    [
        new("default", $"its value when neither {CommandLineInputs.Option} nor the runner's .env gives one"),
        new("required", "refuse a run that gives it no value"),
        new("description", "what the value is for"),
    ];

    /// <summary>
    /// What only a step's <c>run</c> block reads. A step that performs a predefined action runs no
    /// program, so nothing would read any of these, and it is refused with them.
    /// </summary>
    public static IReadOnlyList<KeyDescription> RunBlock { get; } =
    [
        new("workingDirectory", "where it runs, under workingDirectoryRoot; absent, that root"),
        new("workingDirectoryRoot", $"what workingDirectory starts from: {string.Join(", ", WorkingDirectoryRoots.All)}"),
        Phase("env"),
        new("successPattern", "what its last run line must print, besides exiting 0"),
        Phase("stallSeconds"),
        Phase("continueOnError"),
        Phase("watchContention"),
        Phase("requireInputsUnmoved"),
        new("outputs", "what it writes, relative to its build directory, {stepBuild}"),
        new("persist", "keep its outputs when the action finishes"),
        new("heavy", "a run that runs it takes a heavy-leg slot ('help admission'), whatever its runner says"),
        new("inputs", "values this step alone reads, declared as the action's are") { Keys = Input },
    ];

    /// <summary>What one step takes: what any step does, then what only a <c>run</c> block reads.</summary>
    public static IReadOnlyList<KeyDescription> Step { get; } =
    [
        new("name", "names its log file and its build directory, {stepBuild}") { Required = true },
        new("uses", $"{string.Join(" or ", PredefinedActions.All)}, in place of run"),
        new("ref", $"the commit or branch {PredefinedActions.Checkout} confirms the tree is at"),
        new("run", "its program lines, one per line; no shell"),
        new("runOn", $"the systems it runs on, of {string.Join(", ", PlatformNames.OperatingSystems)}; absent, all"),
        new("manual", "run only where a run names it"),
        new("needs", "steps declared before it that run first whenever it does"),
        .. RunBlock,
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
