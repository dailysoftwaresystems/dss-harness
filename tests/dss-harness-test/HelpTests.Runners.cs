using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DssHarness.Core.Anchors;
using DssHarness.Core.Ci;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Help;
using DssHarness.Core.Hosts;
using DssHarness.Core.Legs;
using DssHarness.Core.Mutations;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Runners;
using DssHarness.Core.Runs;
using DssHarness.Core.Sync;

namespace DssHarness.Tests;

public sealed partial class HelpTests
{
    /// <summary>
    /// The runners topic says what a writesTree runner does: its legs work in place whatever localCopy says, a leg a host
    /// would run is refused before any host is measured, and so is --local-copy.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_SaysWhatARunnerThatWritesTheTreeDoes()
    {
        var result = HelpText.Of("runners");

        Assert.Contains(
            "A runner whose steps change files of the tree says writesTree: its legs work in their trees themselves, "
                + "whatever the leg's own localCopy or defaults.localCopy says ('help legs'), for what it wrote in a copy "
                + "the next sync would make the tree again. A leg a host would run is refused before any host is measured, "
                + "and so is --local-copy on the command.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
        Assert.Contains(
            "A run check's runner runs within the leg's own work, so it is held to the same: one that writes the tree is "
                + "refused wherever the leg it checks would work in a copy, --local-copy among them.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The runners topic says that a run check whose own runner's inputs cannot be watched was not run, and confirms
    /// nothing: the failure it was to excuse stays genuine.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_SaysARunCheckWhoseWatchCannotStart_ConfirmsNothing()
    {
        var result = HelpText.Of("runners");

        Assert.Contains(
            "A check whose own runner's watch cannot start was not run, and confirms nothing: the failure stays genuine, and the run fails.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The runners topic names a step's successPattern, which line it is matched against, and which of
    /// that line's streams.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_SaysWhatAStepsSuccessPatternIsMatchedAgainst()
    {
        var result = HelpText.Of("runners");
        var words = Prose.Words(result.StandardOutput);

        foreach (var text in new[]
        {
            "successPattern: <regular expression> what its last run line must print, besides exiting 0",
            "matched against what that line printed on standard output and standard error, a line of output at a time - "
                + "^ and $ its start and end, whether it ends in CRLF or LF, as its log keeps it - after secrets are redacted.",
        })
        {
            Assert.Contains(text, words, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The runners topic lists every key a runner and an action file take, each indented to the depth
    /// it is nested at and with what it does, read from what the files are read with: a key cannot be
    /// taken without being listed here.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_ListsEveryKeyARunnerAndAnActionFileTake()
    {
        var result = HelpText.Of("runners");
        var lines = result.StandardOutput.ReplaceLineEndings("\n").Split('\n');

        foreach (var (key, depth) in Listed(ConfigKeys.Of<RunnerConfig>(), 0).Concat(Listed(ActionFileKeys.File, 0)))
        {
            var pattern = $"^ {{{2 + (2 * depth)}}}{Regex.Escape(key.Name)} +{Regex.Escape(key.Meaning)}{(key.Required ? "; required" : string.Empty)}$";

            Assert.True(lines.Any(line => Regex.IsMatch(line, pattern)), $"'{key.Name}' is not listed as: {pattern}");
        }
    }

    /// <summary>
    /// The runners topic says which stall bound a phase or step runs under, and where the order its
    /// environment is built in is given.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_SaysWhichStallBoundApplies()
    {
        var result = HelpText.Of("runners");
        var words = Prose.Words(result.StandardOutput);

        foreach (var text in new[]
        {
            "The stall bound a phase or step runs under is its own stallSeconds, else its runner's, else "
                + "defaults.stallSeconds: the first of them set, where 0 means none, as does setting none.",
            "'help config' gives the order its environment is built in.",
        })
        {
            Assert.Contains(text, words, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Wherever the runners topic shows how a step key is written, what it does is what 'Every key'
    /// says of it: one meaning, from one place, however many times the key is shown.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_SaysWhatAStepKeyDoes_InTheWordsItsListUses()
    {
        var result = HelpText.Of("runners");
        var meanings = ActionFileKeys.Step.ToDictionary(key => key.Name, key => key.Meaning, StringComparer.Ordinal);
        var shown = result.StandardOutput.ReplaceLineEndings("\n").Split('\n')
            .Select(line => SpeltKeyLine().Match(line))
            .Where(line => line.Success && meanings.ContainsKey(line.Groups["key"].Value))
            .ToList();

        Assert.NotEmpty(shown);

        foreach (var line in shown)
        {
            Assert.Equal(meanings[line.Groups["key"].Value], line.Groups["meaning"].Value);
        }
    }

    /// <summary>
    /// The runners topic says a step that performs a predefined action is refused with each key only a
    /// run block reads, naming every one of them from the list the parser refuses them by.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_SaysAStepThatUsesAPredefinedAction_TakesNoKeyOnlyARunBlockReads()
    {
        var result = HelpText.Of("runners");
        var words = Prose.Words(result.StandardOutput);

        Assert.Contains(
            $"A step that uses {string.Join(" or ", PredefinedActions.All)} runs no program, so it takes none of the keys "
                + $"only a run block reads, and is refused with any of them: '{string.Join("', '", KeyDescription.Names(ActionFileKeys.RunBlock))}'.",
            words,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The runners topic says what a step's outputs are relative to, and what its name names, by the
    /// name a run line uses for that directory, {stepBuild}: 'its own directory' read as the action's,
    /// which workingDirectoryRoot names.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_SaysAStepsOutputsAreRelativeToItsBuildDirectory()
    {
        var result = HelpText.Of("runners");
        var words = Prose.Words(result.StandardOutput);

        Assert.Contains("outputs: [<path>, ...] what it writes, relative to its build directory, {stepBuild}", words, StringComparison.Ordinal);
        Assert.Contains("name names its log file and its build directory, {stepBuild}; required", words, StringComparison.Ordinal);
    }

    /// <summary>
    /// The runners topic says what runOn takes - the systems quoted from the code - where a leg of
    /// another system says it left a step out, and that a leg left with no step is refused.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_SaysWhatRunOnTakes_AndWhatALegLeftWithoutAStepGets()
    {
        var result = HelpText.Of("runners");

        foreach (var text in new[] { "runOn: [<system>, ...]", $"of {string.Join(", ", PlatformNames.OperatingSystems)}; absent, all", "skippedSteps", "refused before anything starts" })
        {
            Assert.Contains(text, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The runners topic says where an input's value comes from, in which order, that a secret never
    /// gives it one, what becomes of one with none, what --input refuses, and that a secret does not
    /// go there.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_SaysWhereAnInputsValueComesFrom()
    {
        var result = HelpText.Of("runners");
        var words = Prose.Words(result.StandardOutput);

        foreach (var text in new[]
        {
            "resolved from 'run --input <name>=<value>' first, .harness-config/runner/.env second and each "
                + "input's own 'default' last; .secrets never gives one a value.",
            "A required input with none of them is refused, and so is a step naming one that is not required and has "
                + "none, naming the input and how it can have one - both before any leg is admitted (below).",
            "default its value when neither --input nor the runner's .env gives one",
            "--input takes one name=value each time it is given",
            "an empty value and a name given twice",
            "a secret stays in",
        })
        {
            Assert.Contains(text, words, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The runners topic says that everything a leg's runner refuses before its first step that does not wait on the
    /// leg's build is refused before a heavy leg waits for its slot, where each leg is checked and what its check's line
    /// leaves out, that a leg whose work would never start is never checked, what stays at the start because it waits on
    /// the build, and that a run check's runner is warned of rather than refusing the run.
    /// </summary>
    [Fact]
    public void RunnersTopic_SaysWhatARunnerRefusesBeforeItsBuild_IsRefusedBeforeAHeavyLegWaitsForItsSlot()
    {
        var words = Prose.Words(HelpText.Of("runners").StandardOutput);

        foreach (var text in new[]
        {
            "Everything else a leg's runner would refuse before its first step that does not wait on the leg's build is "
                + "refused before a heavy leg waits for its slot as well, by the checks the runner makes as it starts and in "
                + "their order: the steps the run selects and the leg's system runs, every input and every name a run line "
                + "holds, each program a line starts held to the tool policy, a line that starts nothing or would put a "
                + "secret in its arguments, and an action's working space git does not ignore.",
            "A leg this machine runs is checked here once its tree is synced and locked and its isolation and developer "
                + "environment are decided, right before its slot is asked for; a WSL distribution's leg by the distribution, "
                + $"sent the leg's run line without --json, {DssHarness.Core.SetUps.LegSetUps.Option} or {DssHarness.Core.Build.KeepGoing.Option} "
                + $"and with {DssHarness.Core.Application.Legs.RunnerLegs.CheckBeforeAdmissionOption}, which checks and runs nothing; an ssh "
                + "host's by the host, before it admits the leg.",
            "A leg whose work would never start - its isolation refused, or its developer environment not set up - ends "
                + "with its own line and is never checked.",
            "A step reading the build of a leg this run did not build is refused as the runner starts, after the slot, "
                + "for the build it waits on is made then; a leg that waits for no slot is checked as its runner starts.",
            "A run check's runner is checked alike, and what would refuse it is warned of rather than refusing the run: a "
                + "check runs only where a failure matches the exception it confirms, and one that starts it is refused by "
                + "it then, leaving that failure unexcused.",
        })
        {
            Assert.Contains(text, words, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The runners topic says what an input may declare its value must be - its pattern, and for a list its separator and
    /// bounds - how items are counted, which declarations are refused when the file is read, and that a required input
    /// with no value, a value its input refuses and a step naming an input with no value are refused before any leg is
    /// admitted, by whose .env: a WSL distribution's checked by it before this machine asks for the leg's slot.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_SaysWhatAnInputMayDeclareItsValueMustBe_AndThatItIsHeldBeforeAnyLegIsAdmitted()
    {
        var result = HelpText.Of("runners");
        var words = Prose.Words(result.StandardOutput);

        foreach (var text in new[]
        {
            "An input may declare what its value must be: 'pattern', a .NET regular expression the whole value must "
                + "match, case and all - [0-9]+ takes 12 and not 12a; and for a list, 'separator', the text between its "
                + "items, matched as written, with 'minItems', 'maxItems' or both, each at least 1.",
            "An item is the text between separators with white space trimmed from either end, and one left empty is "
                + "not counted, so a separator of one space parts a run of spaces as one does and a trailing separator "
                + "adds no item.",
            "an empty or unreadable pattern, a bound with no separator, a separator with no bound and a minItems over "
                + "maxItems are refused, and so is a default its own input refuses.",
            "A required input with no value, and a value its input refuses, are refused before any leg is placed, "
                + "synced, locked or admitted - a heavy run never waits for a slot to be told - naming every such input "
                + "at once, as a usage error; then a step naming an input with no value, as a configuration error; and "
                + "all of it again as each leg's runner starts.",
            "A leg this machine runs is held to this machine's .env; for a leg another machine runs, only what --input "
                + "gave is held to its input here, since that machine's .env is its own, and the rest is that machine's "
                + "to hold by its own values: a WSL distribution, whose leg this machine admits, is asked to check it "
                + "once the leg's copy is synced and before this machine asks for the leg's slot, its refusal the run's; "
                + "an ssh host checks it before it admits the leg itself.",
            "pattern a .NET regular expression its whole value must match",
            "separator the text between the items of its value, which minItems and maxItems count",
        })
        {
            Assert.Contains(text, words, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The runners topic says what a name in braces is made of, that the runner's .env values are
    /// names too, that one this leg has nothing for is refused before the first program starts, and
    /// which names an input or a value may not have.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_SaysWhatANameIs_AndWhichNamesAreRefused()
    {
        var result = HelpText.Of("runners");
        var words = Prose.Words(result.StandardOutput);

        foreach (var text in new[]
        {
            "refused over the whole file before the first program starts, as is one this leg has nothing for.",
            $"A name {LegPathNames.NameRule}.",
            "<value name> any value the runner's .env holds, by its name",
            "An input or a .env value named like a name above is refused, since a run line naming it would get "
                + "this tool's value while the environment held its own; so is an input whose name a run line "
                + "cannot write.",
        })
        {
            Assert.Contains(text, words, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The runners topic names the two compiler names with what each is filled in with; says a line may start with one,
    /// that a step naming one builds its leg first, when one is refused - before anything starts, and once the leg is
    /// built - and that a test invocation may name one too. The tools and admission topics count a step naming one among
    /// those that build their leg.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_SaysWhatACompilersNameIs_WhereItMayStand_AndWhenItIsRefused()
    {
        var token = TestContext.Current.CancellationToken;
        var written = (HelpText.Of("runners")).StandardOutput;
        var runners = Prose.Words(written);

        foreach (var text in new[]
        {
            $"{{{LegPathNames.CompilerC}}} {{{LegPathNames.CompilerCxx}}} the C and the C++ compiler the leg's build identified: the whole path of the program CMake built with, as this machine spells it",
            "The first token must be a program declared under tools, a path in the repository, or the name of one of the leg's compilers alone ({compiler_C}, below); anything else is refused before a single step runs.",
            "starts it, a step naming {product}, {buildDir} or a compiler has its leg built first (below).",
            "A step whose run line or workingDirectory names {buildDir}, {product}, {compiler_C} or {compiler_CXX} reads what the build made or identified, so every leg of a run that runs it is built first",
            "{compiler_C} and {compiler_CXX} name what built the leg, never what a toolchain declares: the program CMake's own record in the leg's build directory names for that language - gcc on one leg and cl on another, from one run line.",
            "A line may start with one, written alone: the leg's own compiler may run though nothing declares it under tools.",
            "A leg whose project CMake does not build is refused before anything starts, naming each leg and step, since no build of it identifies a compiler;",
            "a name the leg's build has nothing for is refused once the leg is built, before its first step, saying which case it is - no compiler identified for that language, a record of one that could not be read or that names no program, or one the build runs with words after it, a launcher such as ccache given its compiler or a compiler given options: the name is a program alone, so the program and the words are named and neither is filled in.",
            "A test invocation's args, coresArgs and workingDirectory may name one too: the check made before the build lets the name stand, and it is filled in, or refused, once the leg is built - under test --no-build, from what the build already in its directory identified.",
        })
        {
            Assert.Contains(text, runners, StringComparison.Ordinal);
        }

        // The rows of its tables of names are headed by the names this tool fills in, each of them and no other: one
        // added to the code cannot be left out, and one the code dropped cannot stay.
        var rows = written
            .Split('\n')
            .Select(line => Regex.Match(line, @"^  ((?:\{[A-Za-z_]+\} ?)+) +\S"))
            .Where(row => row.Success)
            .SelectMany(row => Regex.Matches(row.Groups[1].Value, @"\{([A-Za-z_]+)\}").Select(name => name.Groups[1].Value));

        Assert.Equal(LegPathNames.All.Order(StringComparer.Ordinal), rows.Order(StringComparer.Ordinal));

        Assert.Contains(
            "runs a step or phase naming {product}, {buildDir} or a compiler on that leg's system",
            Prose.Words((HelpText.Of("tools")).StandardOutput),
            StringComparison.Ordinal);
        Assert.Contains(
            "requires the build or runs a step or phase naming {product}, {buildDir} or a compiler, and is heavy, too,",
            Prose.Words((HelpText.Of("admission")).StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The runner topic says a leg's line names what its steps kept, as the path sync --pull takes.
    /// </summary>
    [Fact]
    public async Task RunnerTopic_SaysALegsLineNamesWhatItKept_AsSyncPullTakesIt()
    {
        var result = HelpText.Of("runner");
        var text = Prose.Words(result.StandardOutput);

        Assert.Contains("names each file its steps kept as keptOutputs, relative to the tree", text, StringComparison.Ordinal);
        Assert.Contains(
            "A leg that did not pass carries, in --json, the last lines its last failing phase printed as logTail and that phase's "
                + "name as tailPhase - a host's leg among them, whose log stays there.",
            text,
            StringComparison.Ordinal);
        Assert.Contains("sync --pull' takes to bring it back", text, StringComparison.Ordinal);
        Assert.Contains(
            "A path given to --pull that ends with '/' names a directory: every file below it is brought back, each checked as a file named is and "
            + "each named in the report, as the path a later command takes. The host that holds it lists it first, and a directory that is not "
            + "there or holds no file fails the pull before any file crosses. Every file below it is brought back, however many and however much "
            + "they hold together, each in a request of its own; what the files listed hold is weighed against the room free where they land, and "
            + "a pull with no room for them fails before any crosses, naming both.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "A link along the path named, file or directory, is followed as it is spelt - a build directory pointed at "
                + "another volume is ordinary - but never into a place the isolation hides from every command: one that "
                + "leads there fails the pull, nothing read. A link below a directory named is never followed: it is named, "
                + "and nothing is brought back for it.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "A pull that stops part way - a file gone, a host that stopped answering - fails naming each file it had brought back, which stays "
            + "where it was written, and each it had not.",
            text,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The runner topic names the largest file that can cross to a host or back, the one the refusal names: a file crosses
    /// whole, inside one request, and that request is one line of text a string must hold.
    /// </summary>
    [Fact]
    public async Task RunnerTopic_NamesTheLargestFileThatCanCross_AsTheRefusalDoes()
    {
        var result = HelpText.Of("runner");
        var text = Prose.Words(result.StandardOutput);

        Assert.Contains(
            $"A file crosses whole, whichever way it goes - to a host inside one request, back in one answer: one larger than {SyncServe.LargestFile.ToString(CultureInfo.InvariantCulture)} bytes is refused by name before anything is sent.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(SyncServe.LargestFile.ToString(CultureInfo.InvariantCulture), SyncServe.TooLargeToCarry(SyncServe.LargestFile + 1, "a.bin", "ssh vps"), StringComparison.Ordinal);
    }

    private static IEnumerable<(KeyDescription Key, int Depth)> Listed(IReadOnlyList<KeyDescription> keys, int depth)
        => keys.SelectMany(key => Listed(key.Keys, depth + 1).Prepend((key, depth)));

    /// <summary>A step key shown as it is written, then what it does: '  outputs: [&lt;path&gt;, ...]  what it writes'.</summary>
    [GeneratedRegex(@"^  (?<key>[A-Za-z]+): \S.*?  +(?<meaning>\S.*)$")]
    private static partial Regex SpeltKeyLine();
}
