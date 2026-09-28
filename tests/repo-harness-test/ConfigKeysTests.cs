using RepoHarness.Core.Configuration;
using RepoHarness.Core.Runners;

namespace RepoHarness.Tests;

/// <summary>
/// The keys help lists are the keys the files take: a runner's read from the contract config.json is
/// read with, an action file's from the list its parser reads, each saying what it does.
/// </summary>
public sealed class ConfigKeysTests
{
    /// <summary>Every key a runner takes, and every key an action file takes, says what it does.</summary>
    [Fact]
    public void EveryKey_SaysWhatItDoes()
    {
        var unexplained = Flattened(ConfigKeys.Of<RunnerConfig>(), "predefinedRunners.<name>")
            .Concat(Flattened(ActionFileKeys.File, "action file"))
            .Where(key => string.IsNullOrWhiteSpace(key.Key.Meaning))
            .Select(key => key.Path)
            .ToList();

        Assert.Empty(unexplained);
    }

    /// <summary>
    /// A runner's keys are the ones config.json reads it with, spelled as the file spells them, each
    /// nested section's with its own, and required exactly where the file is refused without them.
    /// </summary>
    [Fact]
    public void ARunnersKeys_AreTheOnesTheFileIsReadWith()
    {
        var runner = ConfigKeys.Of<RunnerConfig>();

        Assert.Equal(
            ["description", "legs", "phases", "action", "steps", "requireBuild", "stallSeconds", "expectedExceptions", "cleanDirectories", "env"],
            KeyDescription.Names(runner));
        Assert.DoesNotContain(runner, key => key.Required);

        var expected = Section(runner, "expectedExceptions");
        Assert.Equal(
            ["exceptionType", "message", "earnedOn", "earnedAt", "mechanism", "anchor"],
            expected.Where(key => key.Required).Select(key => key.Name));

        var checks = Section(expected, "runChecks");
        Assert.Equal(["predefinedRunner", "expects", "minStepsInFailureWindow", "minStepSeconds"], KeyDescription.Names(checks));
        Assert.Equal(["sameException", "success", "resultCode", "message"], KeyDescription.Names(Section(checks, "expects")));
    }

    /// <summary>
    /// A phase takes stepName, and neither outputs nor persist: those are checked and kept in an
    /// action's own directories, which a runner of phases does not have.
    /// </summary>
    [Fact]
    public void APhasesKeys_HoldStepName_AndNeitherOutputsNorPersist()
    {
        var phase = Section(ConfigKeys.Of<RunnerConfig>(), "phases");

        Assert.Equal(
            ["name", "command", "workingDirectory", "env", "successPattern", "stallSeconds", "continueOnError", "watchContention", "requireInputsUnmoved", "stepName"],
            KeyDescription.Names(phase));
        Assert.Equal(["name", "command"], phase.Where(key => key.Required).Select(key => key.Name));
    }

    /// <summary>
    /// A key a step shares with a phase says what the phase's says: one sentence for one rule, read
    /// from the phase's own key.
    /// </summary>
    [Fact]
    public void AKeyAStepSharesWithAPhase_SaysWhatThePhasesSays()
    {
        var phase = ConfigKeys.Of<RunnerPhase>();

        foreach (var name in new[] { "env", "stallSeconds", "continueOnError", "watchContention", "requireInputsUnmoved" })
        {
            Assert.Equal(
                Assert.Single(phase, key => key.Name == name).Meaning,
                Assert.Single(ActionFileKeys.Step, key => key.Name == name).Meaning);
        }
    }

    /// <summary>
    /// An action file's inputs, and a step's own, take the keys one declared input takes; its steps
    /// take a step's keys; and a step's name and the file's steps are the ones it is refused without.
    /// </summary>
    [Fact]
    public void AnActionFilesSections_TakeTheirOwnKeys()
    {
        Assert.Same(ActionFileKeys.Input, Section(ActionFileKeys.File, "inputs"));
        Assert.Same(ActionFileKeys.Step, Section(ActionFileKeys.File, "steps"));
        Assert.Same(ActionFileKeys.Input, Section(ActionFileKeys.Step, "inputs"));
        Assert.Equal(["steps"], ActionFileKeys.File.Where(key => key.Required).Select(key => key.Name));
        Assert.Equal(["name"], ActionFileKeys.Step.Where(key => key.Required).Select(key => key.Name));
    }

    private static IReadOnlyList<KeyDescription> Section(IReadOnlyList<KeyDescription> keys, string name)
        => Assert.Single(keys, key => key.Name == name).Keys;

    private static IEnumerable<(KeyDescription Key, string Path)> Flattened(IReadOnlyList<KeyDescription> keys, string path)
        => keys.SelectMany(key => Flattened(key.Keys, $"{path}.{key.Name}").Prepend((key, $"{path}.{key.Name}")));
}
