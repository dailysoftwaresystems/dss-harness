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
    /// The config topic names every agent section of the file's top level, agentClients among them - its one key, and
    /// that a client holds no size, a model's sizes being its own - and the agent-clients topic the name rules.
    /// </summary>
    [Fact]
    public async Task ConfigTopic_NamesTheAgentClientsSection_AndTheAgentClientsTopicTheNameRules()
    {        var config = Prose.Words(HelpText.Of("config").StandardOutput);

        Assert.Contains(
            "agentClients the clients the tool builds in (claude-code, claude-api, glm), each by its name: update, claude-code's alone, says "
                + "whether the tool installs and updates Claude Code with npm; auth, claude-code's alone, how it signs in (login, apiKey, "
                + "oauthToken, authToken, login where nothing is said), and token the variable a token is held under; configDir and "
                + "executable, claude-code's alone, the variables that hold a specific login's Claude Code directory and the program it "
                + "starts (CLAUDE_CONFIG_DIR, CLAUDE_EXECUTABLE where none is named); maxRetries, maxRetryWaitSeconds and requestExtras, "
                + "the API clients' (claude-api, glm), their tries, longest wait and the fields merged into each request; "
                + "requestsPerMinute, inputTokensPerMinute and outputTokensPerMinute, the API clients' too, the buckets that pace a "
                + "lane's minute; cacheTtl and betas, claude-api's alone, how long its requests are cached and the betas sent beside "
                + "the tool's own; preserveThinking, glm's alone, whether Z.AI keeps the model's reasoning across turns; maxParallel, "
                + "every client's, the requests of one provider and key under way at once - a claude-code launch counting as one "
                + "(claude-code 3, claude-api 4, glm 2 where "
                + "nothing is said) - the gate of 'help agent-clients'; a client holds no model, size or price - contextTokens and "
                + "maxOutputTokens are a model's ('help agent-clients')",
            config,
            StringComparison.Ordinal);

        // The agent's context is a top-level section of its own, every default the one constant the code holds.
        Assert.Contains(
            "agentContext what an agent is given of the work it goes on with: compaction, the engine's compaction's - "
                + $"reducedChars ({DssHarness.Core.Agents.AgentCompactionBounds.DefaultReducedChars}) and callInputChars ({DssHarness.Core.Agents.AgentCompactionBounds.DefaultCallInputChars}), "
                + "each a positive number at its default where it is left out ('help agent-clients'); continuation, which once bounded "
                + "the continuation record, is retired and refused - the record holds every step, every changed path and every "
                + "iteration, nothing of it cut, its pages read by read_result",
            config,
            StringComparison.Ordinal);

        Assert.Contains(
            "agentPolicy what an agent may do beyond the rules built into the tool: restrictions, each one rule string, that only ever refuse - "
                + "Run(git push *), Write(.github/**), Read(secrets/**), Web(pastebin.com) - applied to every agent of every run under this "
                + "configuration, beside a workflow's definitions and steps; webDomains, the allow list of every page an agent reads and every "
                + "result a search gives - a search source's own requests are held to the Web(...) rules alone (left out, any "
                + "address); worktreeRules (true where nothing is said) and protectedPaths (true too), the switches of the rule sets "
                + "'help agent-clients' names; keepEnvironment, the secret-shaped names a command an agent runs keeps; "
                + "commandTimeoutSeconds, how long one runs where its call names no time - left out, until it ends, stopped where it "
                + "prints nothing for the step's stallSeconds and by the step's own bounds - and a call names any time to 2147483 s; "
                + "maxCommandTimeoutSeconds is retired, and refused; isolation, required or preferred (required where nothing is said), whether "
                + "an agent that may run a command starts where this machine cannot isolate its commands; hiddenPlaces, more places "
                + "the user keeps credentials in, each a whole path or one under ~/, hidden beside the declared ones, which it never "
                + "takes away ('help agent-clients')",
            config,
            StringComparison.Ordinal);

        Assert.Contains(
            "A provider's name and each tier's are held to the orchestration name rule - lower-case letters and digits, single hyphens - "
                + "and a tier is one name in one case everywhere; a model's code, what is sent to the API, is one word, and its name is the "
                + "one a person reads.",
            Prose.Words(HelpText.Of("agent-clients").StandardOutput),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigTopic_DocumentsCoresAndParallelLegs_WithTheRealDefault()
    {
        var result = HelpText.Of("config");

        foreach (var setting in new[] { "buildCores", "testCores", "maxParallelLegs", "sanitizer", "hosts", "emulators", "developerEnvironments" })
        {
            Assert.Contains(setting, result.StandardOutput, StringComparison.Ordinal);
        }

        Assert.Contains(
            $"({HarnessDefaults.DefaultCores.ToString(CultureInfo.InvariantCulture)} each)",
            result.StandardOutput,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The config topic says what a failed test invocation keeps as evidence - the paths under the build directory, the
    /// cap with its real default read from the code, kept-on-failure beside the leg's log, a pass keeping nothing, a
    /// later step reading nothing from it, a path naming nothing said rather than refused, and a host's copy crossing
    /// back over its transport.
    /// </summary>
    [Fact]
    public async Task ConfigTopic_SaysWhatAFailedTestInvocationKeeps()
    {
        var said = Prose.Words((HelpText.Of("config")).StandardOutput);

        Assert.Contains(
            "A test invocation may name what a failed run keeps as evidence: keepOnFailure, paths relative to the leg's "
                + "build directory - files, directories, or patterns, {buildDir} among the names they may use. When the "
                + "invocation does not pass, whatever the verdict, what they name is copied into kept-on-failure/ beside "
                + "the leg's log, each file's tree as it stood under the build directory, within keepOnFailureCapBytes kept "
                + $"in all ({DiskSpace.Size(DssHarness.Core.Testing.KeptOnFailure.DefaultCapBytes)} where it is left out). A pass keeps "
                + "nothing, and a later step reads nothing from it: it is evidence beside the verdict, never an output, and "
                + "a failed step still keeps nothing of what one reads. Only a file of its own is copied: a link is never "
                + "kept through, wherever on the way from the build directory it stands - an entry that is one, a file "
                + "beneath a linked directory, and no walk of a directory or a pattern follows one; what it leads to is kept "
                + "where an entry names that - and a pipe or a device is no file to read. Each is measured by what it holds "
                + "as it is read, and no more of it is copied than the cap has left, so a file that grows past the cap as it "
                + "is read is refused over it. A directory that cannot be read is passed by and named where an entry could "
                + "have named what it holds - beneath a directory an entry names, or one a pattern could reach - the files "
                + "beside it kept, and a stop while the evidence crosses keeps what is already kept and names what it left. "
                + "The leg's line names the directory, how many files were kept and their size, and how many were refused "
                + "over the cap, could not be read, were left as links, named nothing or were left uncopied by a stop, and "
                + "how many directories could not be read - a path naming nothing is said, not refused, for "
                + "the failure may have been before it was written. A keepOnFailureCapBytes given where no keepOnFailure it "
                + "applies to names a path caps nothing, and is refused. On a "
                + "leg a WSL or ssh host runs, the copy is kept beside that host's own log and crosses back over the host's "
                + "transport: 'sync --pull' takes the kept-on-failure directory within that host's run.",
            said,
            StringComparison.Ordinal);

        // The number is the decision, not a constant nobody reads: said as the words that decided it.
        Assert.Contains("within keepOnFailureCapBytes kept in all (64 MiB where it is left out)", said, StringComparison.Ordinal);
    }

    /// <summary>
    /// The config topic says a leg's inputs are watched, on Linux, only as deep as they lie, and the directories above them
    /// for the names in them alone, so a build in another tree below the worktrees root is never heard, however many
    /// directories they lie in, through one inotify instance for the process; on Windows and macOS - and where inotify
    /// cannot be called directly, .NET's watcher standing in, said once - through one watch of the root, counting only what
    /// names an input or a directory above one; and that an overflow has every input looked at again.
    /// </summary>
    [Fact]
    public async Task ConfigTopic_SaysInputsAreWatchedOnlyAsDeepAsTheyLie()
    {
        var result = HelpText.Of("config");

        Assert.Contains(
            "On Linux its inputs are watched only as deep as they lie - each directory holding one, for its own files, and each "
                + "directory above one, the root among them, for the names of the directories in it alone - so a build in another tree "
                + "below the worktrees root, which init keeps in git by a placeholder, is never heard; every such directory is watched, "
                + "however many there are, through one inotify instance for the whole process, and this user's inotify watches running "
                + "out is said as the limit it is (fs.inotify.max_user_watches). On Windows and macOS, and on Linux where inotify cannot "
                + "be called directly - where .NET's watcher stands in, and the process says so once - a tree is one watch of its root, "
                + "all the way down, and what it hears counts only where it names an input or a directory above one. Where events come "
                + "faster than the system's buffer or queue holds them, every input is looked at again against how the work found it, "
                + "and one that no longer stands so counts.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The config topic says what a directory that holds inputs or lies above some going, arriving or changing its name
    /// counts - every input in or below it, though it be back when the work ends - and that a leg whose inputs cannot be
    /// watched runs none of its work.
    /// </summary>
    [Fact]
    public async Task ConfigTopic_SaysWhatADirectoryThatWentCounts_AndThatAnUnwatchedLegRunsNothing()
    {
        var said = Prose.Words((HelpText.Of("config")).StandardOutput);

        Assert.Contains(
            "A directory that holds inputs, or lies above some, going, arriving or changing its name "
                + "counts every input in or below it, under both names of a rename, though it be back when the work ends; one merely "
                + "written to counts none. "
                + "A leg whose inputs cannot be watched at all runs none of its work, and ends unmeasured at once, saying why ('help "
                + "verdicts').",
            said,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The config topic says a test count that differs is marked on its own, never as a timing, and
    /// how a set that differs on purpose is declared.
    /// </summary>
    [Fact]
    public async Task ConfigTopic_SaysATestCountThatDiffers_IsNoTimingMark_AndHowATestSetIsNamed()
    {
        var result = HelpText.Of("config");

        foreach (var text in new[]
        {
            "and test set is marked on its own - 'test count differs', and testCountDiffers and",
            "testCountNote in --json - never as a timing, and never changes a verdict either. A",
            "\"windows\": { \"testSet\": \"windows\" }",
            "A leg's os names its section however it is spelled - Windows as windows - as every",
            "per-system setting is read.",
        })
        {
            Assert.Contains(text, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The config topic says how test --filter, --exclude and --label reach a runner, what each is
    /// for ctest, that a label can now be chosen as well as left out, how several exclusions reach
    /// a runner that would not leave out each of them given apart, what a leg on a host's copy
    /// leaves out beside them, that a selection ctest could only find empty is refused before the
    /// build and how its values are judged, and which invocations are refused when the file is read.
    /// </summary>
    [Fact]
    public async Task ConfigTopic_SaysHowTheTestSelectionReachesTheRunner()
    {
        var result = HelpText.Of("config");

        foreach (var text in new[]
        {
            "filterArg, excludeArg and labelArg, so one set of options serves every runner. For",
            "ctest, '-R' chooses tests by name, '-L' chooses them by label and '-LE' leaves a label",
            "apart - ctest leaves out only what every -LE matches - declares excludeJoin, and several",
            "one the sync made, or one it took over. Each sync makes its HEAD name the commit this",
            "tree is at, so what a step asks git about HEAD - which commit, what a file held there -",
            "untracked as added, and one git stores converted here - line endings, a clean filter -",
            "pack of git's own, less what the copy holds, and nothing crosses while its HEAD names",
            "about a commit behind it that the copy lacks fails there, in git's own words.",
            "\"sync\": { \"history\": \"full\" } gives the copy every commit behind it too, once, and",
            "each later one as it is made; a partial clone then has git fetch what it left out. A",
            "copy the sync took over has its HEAD moved where it names another commit - detached,",
            "reads it as a shallow repository from then on, said too, which 'git fetch --unshallow'",
            "sync makes its index hold the files it carried, so a build there fingerprints its",
            "                 how much git history a copy is given: 'head' (the commit HEAD names,",
            "remoteExcludes are given to every leg that works in a copy - a host's, or its own in",
            "reads there unless the args or a preset choose tests by name too - or beside a test",
            "preset that takes a union or cannot be read. So is a selection ctest could only find",
            "another leaves out by the same part of a test - its name, or a label - where either is",
            "only -LE and no test preset they name adds one. Values are judged as written: spelled",
            "has been built; where it still finds none, the detail of a leg that did not pass says",
            "so, unless --no-tests=ignore keeps ctest quiet. A label beside labels the args or a",
            "When the file is read, ctest is also refused an invocation that would pass an option",
            "'|', which ctest would read as part of one pattern that leaves out nothing. And ctest,",
            "refused an empty filterJoin, which would give them several units apart, and a",
            "neither way, and is refused for --name. Each is refused a filter beside the same",
            "'test --filter' may be given more than once, each value a unit that names tests, and",
            "its filter option apart - ctest keeps only the last -R, dotnet test refuses a second",
            "--filter, and dart test runs only a test every --name matches - so the units reach",
            "each as one value joined by '|', the join the harness knows each of the three reads",
            "as alternatives, where its invocation declares no filterJoin; init writes none.",
            "declares. A workflow's test step gives its filter's units the same way.",
            "given - of several filter units, where it is so of every one. With excludeArg '-LE',",
        })
        {
            Assert.Contains(text, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The config topic says when a combination's build directory is kept and when it starts from
    /// clean, and what rebuildableFormats decides: a consumer found both only by reading the source.
    /// </summary>
    [Fact]
    public async Task ConfigTopic_SaysWhenABuildDirectoryIsKept_AndWhatCountsAsAnInput()
    {
        var result = HelpText.Of("config");

        foreach (var text in new[]
        {
            "A combination's directory is kept between builds, and its build system decides",
            "  - a compiler CMake identified is not what is at its path now: CMake identifies a",
            "  - an input that changed since that build began is dated no later than the newest",
            "  - nothing can say: no record of what it was built from, the files git tracks",
            "DEPENDS is not remade. A project's rebuildableFormats says which files are inputs,",
        })
        {
            Assert.Contains(text, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The config topic names the repository's own stall bound, and every layer a runner's phase or
    /// step is given in order, its action's inputs among them.
    /// </summary>
    [Fact]
    public async Task ConfigTopic_NamesTheDefaultStallBound_AndEveryLayerOfARunnersEnvironment()
    {
        var result = HelpText.Of("config");
        var words = Prose.Words(result.StandardOutput);

        foreach (var text in new[]
        {
            "stallSeconds, the stall bound nothing more specific replaces ('help runners')",
            "run host env, developer environment, then the runner's values and secrets, its env, the action's "
                + $"inputs as INPUT_<NAME> where a {PredefinedActions.ReadInputs} step read them, the phase's or step's",
        })
        {
            Assert.Contains(text, words, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The config topic says a hold between commands runs the host's keepAwake, ends when a command's own starts,
    /// and needs keepAwake.
    /// </summary>
    [Fact]
    public async Task ConfigTopic_SaysAHoldBetweenCommandsEndsWhenACommandsOwnKeepAwakeStarts()
    {
        var result = HelpText.Of("config");
        var text = Prose.Words(result.StandardOutput);

        Assert.Contains("holdAwakeSeconds holds it awake between commands, until a command's own keepAwake takes over", text, StringComparison.Ordinal);
        Assert.Contains("The next command's own keepAwake ends it there", text, StringComparison.Ordinal);
        Assert.Contains("It needs keepAwake, which it runs", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The config topic names the workflows key and the layout topic the workflows directory, each saying where the
    /// definition lives: never an entry in the one, always a file of its own under the other.
    /// </summary>
    [Fact]
    public async Task TheConfigAndLayoutTopics_NameTheWorkflowsKeyAndItsDirectory()
    {
        var config = HelpText.Of("config");
        var layout = HelpText.Of("layout");


        Assert.Contains(
            "  workflows      the workflows this repository runs, each defined by a yml file of its",
            config.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains(
            "  .harness-config/workflows/<name>/<name>.yml",
            layout.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains(
            "each missing file written by init where config.json declares the implementation one",
            Prose.Words(layout.StandardOutput),
            StringComparison.Ordinal);

        // workflows, keepEnvironment, hiddenPlaces and keepOnFailure are refused given empty though leaving each out means
        // none, which neither of the other two kinds of list the topic names is: said apart from both, with the spelling
        // the refusal itself asks for.
        Assert.Contains(
            "workflows, which declares no workflow left out, agentPolicy.keepEnvironment, which gives no name back left "
                + "out, agentPolicy.hiddenPlaces, which adds no place left out, and a test invocation's keepOnFailure, which keeps no evidence left out, are refused given empty "
                + "too, so none is spelt one way: leave the key out for none.",
            Prose.Words(config.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The config topic names every member of a problem matcher - its name, pattern, severity and how a severity group is
    /// read, the groups of its place, its end and its stack's frame and until - and which phases each reads: those whose
    /// runner or invocation names it in matchers, or every one where nothing names it, a CI job's log by all.
    /// </summary>
    [Fact]
    public void ConfigTopic_NamesEveryMemberOfAMatcher_AndWhichPhasesItReads()
    {
        var config = Prose.Words(HelpText.Of("config").StandardOutput);

        Assert.Contains(
            "results matchers: a problem matcher for any other tool, in the shape editors and CI take, so its output is digested as "
                + "every tool's is - name, unique, the tool its diagnostics name; pattern, a regex; severity, error, warning or note, or "
                + "a group of the pattern whose text is read as editors read it (warn a warning; info, hint and notice a note; anything "
                + "else an error), error where it is left out; file, line, column, code and message, the groups holding each, the whole "
                + "match the message where none is named; end, the pattern of the last line of a diagnostic that runs on past its "
                + "match; and stack, where the tool prints a chain under it - frame, a pattern naming a frame's file, line, column, "
                + "symbol or module, and until, the line it ends at, else the first line no frame matches. A predefined runner's or a "
                + "test invocation's matchers names the matchers that read its output; one nothing names reads every build, test and "
                + "runner phase, and a CI job's log is read by all. Validated at load, a name no matcher answers to refused, each "
                + "pattern matched in time linear in what it reads, so one holding a backreference, a lookaround or an atomic group is "
                + "refused, naming it",
            config,
            StringComparison.Ordinal);
    }
}
