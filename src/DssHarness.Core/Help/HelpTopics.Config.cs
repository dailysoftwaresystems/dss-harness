using System.Globalization;
using System.Text;
using DssHarness.Core.Ci;
using DssHarness.Core.Anchors;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Codebase;
using DssHarness.Core.Build;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
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
using DssHarness.Core.Secrets;
using DssHarness.Core.SetUps;
using DssHarness.Core.Sync;
using DssHarness.Core.Tools;
using DssHarness.Core.Worktrees;

namespace DssHarness.Core.Help;

public static partial class HelpTopics
{
    private static string RenderConfig()
    {
        var builder = new StringBuilder();

        builder.AppendLine("config.json");
        builder.AppendLine();
        builder.AppendLine($"  defaults       buildCores and testCores ({HarnessDefaults.DefaultCores} each), maxParallelLegs (per");
        builder.AppendLine("                 machine) and maxParallelLegsTotal (the whole fleet), default project,");
        builder.AppendLine("                 localCopy, where this machine's own legs work for every leg at once");
        builder.AppendLine("                 ('help legs'), stallSeconds, the stall bound nothing more specific replaces");
        builder.AppendLine("                 ('help runners'), and admission, how heavy legs share a machine");
        builder.AppendLine("                 ('help admission')");
        builder.AppendLine("  toolchains     compilers, as environment and cache variables (msvc, gcc, clang)");
        builder.AppendLine("  sanitizers     instrumentation overlays composed onto a build");
        builder.AppendLine("  buildConfigs   named configurations (debug, release, o1, o2)");
        builder.AppendLine("  projects       what to build, and with which adapter (cmake, dotnet, dart)");
        builder.AppendLine("  hosts          this machine (local), WSL distributions (wsl) and ssh hosts (ssh),");
        builder.AppendLine("                 each with its own core counts and environment when they differ,");
        builder.AppendLine("                 and admission for this machine and each ssh host");
        builder.AppendLine("  emulators      ways to run programs for another processor on a host: qemu,");
        builder.AppendLine("                 Rosetta, Prism");
        builder.AppendLine("  developerEnvironments  what a toolchain's legs start in, set up on the host");
        builder.AppendLine("                 that runs them: visualStudio runs that instance's vcvarsall.bat,");
        builder.AppendLine("                 for the MSVC toolset an optional toolsVersion names ('help legs')");
        builder.AppendLine("  legs           units of work: os + processor (+ emulator) + project + toolchain");
        builder.AppendLine("                 + config (+ sanitizer), and localCopy, where a leg this machine");
        builder.AppendLine("                 runs itself works ('help legs'); buildSpaceGiB, the room a leg's");
        builder.AppendLine("                 build comes to, is 'help space'");
        builder.AppendLine("  legSets        named groups of legs, selected with --legs like a leg");
        builder.AppendLine("  tools          external tools to verify and install");
        builder.AppendLine("  predefinedRunners  multi-phase procedures such as a corpus test or a benchmark;");
        builder.AppendLine("                     one whose steps change the tree says writesTree ('help legs')");
        builder.AppendLine("  workflows      the workflows this repository runs, each defined by a yml file of its");
        builder.AppendLine("                 own under .harness-config/workflows/<name>/ and declared here by name");
        builder.AppendLine("                 alone; validate-workflow checks them ('help workflows')");
        builder.AppendLine("  workflowSources  where a remote use may come from beyond the repository: allow, the");
        builder.AppendLine("                 owners and repositories it may name (empty, and a remote use is refused);");
        builder.AppendLine("                 hosts, GitHub Enterprise hosts, each with the one credential its calls");
        builder.AppendLine("                 are given, \"credential\": {\"from\": \"env\", \"name\": \"<VARIABLE>\"}");
        builder.AppendLine("                 (required); pin, the refs it takes (tag-or-commit, the default, commit,");
        builder.AppendLine("                 or any, which allows a moving branch); offline, what stands in where the");
        builder.AppendLine("                 network keeps a ref from being resolved (refuse, the default, or");
        builder.AppendLine("                 last-resolved) ('help workflows')");
        builder.AppendLine("  setUp          what a run brings up around its work and down after, by name: an up");
        builder.AppendLine($"                 and a down command line for '{ToolPackage.Command} up' and '{ToolPackage.Command} down', which");
        builder.AppendLine("                 take inputs; a test invocation's and a runner's setUp name them ('help setups')");
        builder.AppendLine("  commit         commit template and sign-off policy");
        builder.AppendLine("  sync           what the tree mirror carries, and what it must never carry; history,");
        builder.AppendLine("                 how much git history a copy is given: 'head' (the commit HEAD names,");
        builder.AppendLine("                 the default) or 'full' (every commit behind it too)");
        builder.AppendLine("  contention     tools that, running against a leg's build directory, void its result");
        builder.AppendLine("  worktrees      naming, path budget and path limit");
        builder.AppendLine("  anchors        the pending and done anchor registries, and how new ids are spelled");
        builder.AppendLine("  mutations      the mutation arms registry, its texts, the workers a leg sweeps");
        builder.AppendLine("                 with, its test binaries' report, and what each runner reads at");
        builder.AppendLine("                 run time, part of each arm's key ('help mutations')");
        builder.AppendLine("  providers      the companies whose models the tool reaches, each with its models: a");
        builder.AppendLine("                 model's API address, its keys' variable names (apiKeyNames, in the");
        builder.AppendLine("                 order they are tried), its client, its efforts, its sizes and its");
        builder.AppendLine("                 prices, with the date they were read - nothing of a model is in the");
        builder.AppendLine("                 tool's code ('help agent-clients')");
        builder.AppendLine("  tierConfig     each tier's models in the order they are tried: the first serves a");
        builder.AppendLine("                 step on the tier, the rest are its fallback; defaults.agentTier");
        builder.AppendLine("                 (balanced, as init writes it) is the tier of a step naming neither");
        builder.AppendLine("  balanceKeyUsage  off unless turned on: a provider's keys balanced by what each");
        builder.AppendLine("                 spent in the last seven days, a step starting on the one that spent");
        builder.AppendLine("                 least");
        builder.AppendLine($"  agentClients   the clients the tool builds in ({string.Join(", ", Agents.AgentProviders.ClientKinds.Select(kind => kind.Name))}), each");
        builder.AppendLine("                 by its name: update, claude-code's alone, says whether the tool");
        builder.AppendLine("                 installs and updates Claude Code with npm; auth, claude-code's");
        builder.AppendLine($"                 alone, how it signs in ({string.Join(", ", Agents.AgentAuth.All)}, {Agents.AgentAuth.Login}");
        builder.AppendLine("                 where nothing is said), and token the variable a token is held under;");
        builder.AppendLine($"                 {Agents.AgentClientSetting.ConfigDirKey} and {Agents.AgentClientSetting.ExecutableKey}, claude-code's alone, the variables");
        builder.AppendLine("                 that hold a specific login's Claude Code directory and the program");
        builder.AppendLine($"                 it starts ({Orchestration.ClaudeTranscripts.ConfigDirectoryVariable}, {Agents.ClaudeCode.ClaudeCodeUpdateService.ExecutableVariable} where none is named);");
        builder.AppendLine("                 maxRetries, maxRetryWaitSeconds and requestExtras, the API");
        builder.AppendLine($"                 clients' ({string.Join(", ", Agents.AgentProviders.ApiClients)}), their tries, longest wait and the");
        builder.AppendLine("                 fields merged into each request; requestsPerMinute,");
        builder.AppendLine("                 inputTokensPerMinute and outputTokensPerMinute, the API clients'");
        builder.AppendLine("                 too, the buckets that pace a lane's minute; cacheTtl and betas,");
        builder.AppendLine("                 claude-api's");
        builder.AppendLine("                 alone, how long its requests are cached and the betas sent beside");
        builder.AppendLine("                 the tool's own; preserveThinking, glm's alone, whether Z.AI keeps");
        builder.AppendLine("                 the model's reasoning across turns; maxParallel, every client's,");
        builder.AppendLine("                 the requests of one provider and key under way at once - a");
        builder.AppendLine($"                 claude-code launch counting as one ({string.Join(", ", Agents.ClientGateSettings.BuiltIn.Select(bound => $"{bound.Key} {bound.Value}"))}");
        builder.AppendLine("                 where nothing is said) - the gate of 'help agent-clients';");
        builder.AppendLine("                 a client holds no model, size or price - contextTokens and");
        builder.AppendLine("                 maxOutputTokens are a model's ('help agent-clients')");
        builder.AppendLine("  agentPolicy    what an agent may do beyond the rules built into the tool:");
        builder.AppendLine("                 restrictions, each one rule string, that only ever refuse -");
        builder.AppendLine("                 Run(git push *), Write(.github/**), Read(secrets/**),");
        builder.AppendLine("                 Web(pastebin.com) - applied to every agent of every run under this");
        builder.AppendLine("                 configuration, beside a workflow's definitions and steps;");
        builder.AppendLine("                 webDomains, the allow list of every page an agent reads and every");
        builder.AppendLine("                 result a search gives - a search source's own requests are held to");
        builder.AppendLine("                 the Web(...) rules alone (left out, any address); worktreeRules (true");
        builder.AppendLine("                 where nothing is said) and");
        builder.AppendLine("                 protectedPaths (true too), the switches of the rule sets 'help");
        builder.AppendLine("                 agent-clients' names; keepEnvironment, the secret-shaped names a");
        builder.AppendLine($"                 command an agent runs keeps; commandTimeoutSeconds ({Agents.AgentCommandSettings.DefaultTimeoutSeconds}) and");
        builder.AppendLine($"                 maxCommandTimeoutSeconds ({Agents.AgentCommandSettings.DefaultMaxTimeoutSeconds}), how long one runs where its call");
        builder.AppendLine($"                 names no time, and the most it may name, {Agents.AgentCommandSettings.MostTimeoutSeconds} at most;");
        builder.AppendLine($"                 isolation, {string.Join(" or ", Agents.Isolation.AgentIsolationMode.Modes)} ({Agents.Isolation.AgentIsolationMode.Required} where nothing is");
        builder.AppendLine("                 said), whether an agent that may run a command starts where this");
        builder.AppendLine("                 machine cannot isolate its commands; hiddenPlaces, more places the");
        builder.AppendLine("                 user keeps credentials in, each a whole path or one under ~/,");
        builder.AppendLine("                 hidden beside the declared ones, which it never takes away ('help");
        builder.AppendLine("                 agent-clients')");
        AppendWrapped(
            builder,
            $"the harness's own web search: sources, each by its name - its {string.Join(", ", Web.Search.WebSearchSource.OwnKeys.Select(key => key.Name))} "
            + "and its kind's own keys, laid over the built-in sources key by key, which init writes; defaults, the sources or "
            + "tags a step that names none searches, those turned on; "
            + $"maxPerStep ({Web.Search.WebSearchSources.DefaultMaxPerStep}) and resultsPerSource ({Web.Search.WebSearchSources.DefaultResultsPerSource}) ('help web-search')",
            "  agentWebSearch ");
        AppendWrapped(
            builder,
            "what an agent is given of the work it goes on with, each bound a positive number at its default where it is "
            + $"left out: continuation, the continuation record's - recordChars ({Agents.AgentContinuationBounds.DefaultRecordChars}), "
            + $"stepChars ({Agents.AgentContinuationBounds.DefaultStepChars}), paths ({Agents.AgentContinuationBounds.DefaultPaths}), "
            + $"lastIterations ({Agents.AgentContinuationBounds.DefaultLastIterations}) and iterationChars ({Agents.AgentContinuationBounds.DefaultIterationChars}); "
            + $"compaction, the engine's compaction's - reducedChars ({Agents.AgentCompactionBounds.DefaultReducedChars}) and "
            + $"callInputChars ({Agents.AgentCompactionBounds.DefaultCallInputChars}) ('help agent-clients')",
            "  agentContext   ");
        builder.AppendLine();
        builder.AppendLine("A list whose absence means every one of what it names, or a set the tool chooses, is");
        builder.AppendLine("refused given empty, as --legs given no name is: a runner's legs and steps; a tool's");
        builder.AppendLine("platforms, toolchains, legs, processors and emulators; a toolchain's platforms; an");
        builder.AppendLine("emulator's phases; ci.workflows; test.inputs; a project's targets and");
        builder.AppendLine("rebuildableFormats; a platform's toolSearchDirectories; agentWebSearch.defaults.");
        builder.AppendLine("Leave the key out instead. A list whose empty form means none - such as");
        builder.AppendLine("sync.neverTransfer, sync.exclude or");
        builder.AppendLine("contention.buildTools - takes [] as that. workflows, which declares no workflow left");
        builder.AppendLine("out, agentPolicy.keepEnvironment, which gives no name back left out,");
        builder.AppendLine("agentPolicy.hiddenPlaces, which adds no place left out, and a test");
        builder.AppendLine("invocation's keepOnFailure, which keeps no evidence left out, are refused");
        builder.AppendLine("given empty too, so none is spelt one way: leave the key out for none. A runner's list");
        builder.AppendLine("under mutations.runReads, which would say nothing given empty, is refused so: leave the");
        builder.AppendLine("runner out instead.");
        builder.AppendLine();
        builder.AppendLine("A toolchain, a build config and a sanitizer overlay compose: each contributes");
        builder.AppendLine("environment and cache variables, so clang x debug x asan needs no entry of its");
        builder.AppendLine("own. Each combination builds in its own directory, keyed by that combination,");
        builder.AppendLine("so two toolchains never share one build tree.");
        builder.AppendLine();
        builder.AppendLine("A combination's directory is kept between builds, and its build system decides");
        builder.AppendLine("what to rebuild. It starts from clean only where one of these holds, and the");
        builder.AppendLine("leg's line says which, as 'rebuilt from clean:':");
        builder.AppendLine();
        builder.AppendLine("  - another variant built there last, as its record says: a mutation worker builds");
        builder.AppendLine($"    in {VariantKey.BuildRootName}/<key>, named by its variant's key, which two variants' names can");
        builder.AppendLine("    come to alike ('help mutations')");
        builder.AppendLine("  - the build before it cannot be trusted: a phase spanned a clock step, the inputs");
        builder.AppendLine("    moved while it ran, something else used the directory, or an input could not");
        builder.AppendLine("    be read");
        builder.AppendLine("  - a compiler CMake identified is not what is at its path now: CMake identifies a");
        builder.AppendLine("    compiler once, so one updated in place is never identified again");
        builder.AppendLine("  - an input that changed since that build began is dated no later than the newest");
        builder.AppendLine("    file it left, which a build system ordering dates would miss; the line names");
        builder.AppendLine("    the input, that file and both dates");
        builder.AppendLine("  - nothing can say: no record of what it was built from, the files git tracks");
        builder.AppendLine("    could not be listed, or an input, its date or the directory could not be read");
        builder.AppendLine();
        builder.AppendLine("A change dated after that build is left to the build system, which rebuilds what");
        builder.AppendLine("it knows reads it: an output a custom command makes from a file it names in no");
        builder.AppendLine("DEPENDS is not remade. A project's rebuildableFormats says which files are inputs,");
        builder.AppendLine("by extension or whole name such as '.cpp' or 'CMakeLists.txt', in place of what");
        builder.AppendLine("its type reads, and a file with no extension always is one; an edit to any other");
        builder.AppendLine("file never discards a directory.");
        builder.AppendLine();
        builder.AppendLine("A host's env reaches every process a leg starts there - each build phase, the");
        builder.AppendLine("ninja that reads the build's dependency records, the test runner, each step of");
        builder.AppendLine("a runner - as the lowest layer, so everything more specific still says");
        builder.AppendLine("otherwise. The developer environment the leg's toolchain names is set up over");
        builder.AppendLine("it, on that host. From lowest to highest:");
        builder.AppendLine();
        builder.AppendLine("  build    host env, developer environment, then the variant's (toolchain, build");
        builder.AppendLine("           config, sanitizer, project)");
        builder.AppendLine("  test     host env, developer environment, then the test invocation's env");
        builder.AppendLine("  run      host env, developer environment, then the runner's values and secrets,");
        builder.AppendLine($"           its env, the action's inputs as INPUT_<NAME> where a {PredefinedActions.ReadInputs}");
        builder.AppendLine("           step read them, the phase's or step's");
        builder.AppendLine();
        builder.AppendLine("Names compare ignoring case, as on Windows. A PATH set in a host's env is where");
        builder.AppendLine("that host finds every one of those programs, which no survey can see: none of");
        builder.AppendLine("them is required of the host before a leg starts, and each is the run's to find -");
        builder.AppendLine("but in a developer environment, whose PATH, built over the host's, is looked in");
        builder.AppendLine("before the leg starts.");
        builder.AppendLine("A host running a leg another machine sent it reads the section that machine");
        builder.AppendLine("names it by, never 'local', which in their shared file is the machine that sent it.");
        builder.AppendLine();
        builder.AppendLine("An ssh host's holdAwakeSeconds holds it awake between commands, until a command's");
        builder.AppendLine("own keepAwake takes over: as each command finishes with it, the host starts a");
        builder.AppendLine("DssHarness of its own that runs its keepAwake - {pid} filled in with that process -");
        builder.AppendLine("for that many seconds, and goes on once the connection has ended. The next command's");
        builder.AppendLine("own keepAwake ends it there, as does an update of DssHarness there, and a newer");
        builder.AppendLine("hold replaces an older one. It needs keepAwake, which it runs; a hold that cannot be");
        builder.AppendLine("left is said and fails nothing. On a Windows host, OpenSSH may end that process with");
        builder.AppendLine("the connection.");
        builder.AppendLine();
        builder.AppendLine("A host's keepAwake - [\"caffeinate\", \"-dimsu\", \"-w\", \"{pid}\"] on macOS - runs on that");
        builder.AppendLine("host while a leg's own work does, {pid} filled in with the DssHarness process");
        builder.AppendLine("running the leg, and is stopped when the work ends. One that cannot start, or");
        builder.AppendLine("ends early, is said and fails nothing: a sleep it did not prevent still marks the");
        builder.AppendLine("phase it interrupted suspect, as on a host that declares none. A host's compiler");
        builder.AppendLine("cache is its own variable in env, CCACHE_DIR for ccache; compilerCacheDirectory");
        builder.AppendLine("is retired, and refused where it is read.");
        builder.AppendLine();
        builder.AppendLine("Selected legs run at the same time, and a command waits for all of them. Within");
        builder.AppendLine("a leg the order is fixed: sync when its copy needs it - a host's, or its own in the");
        builder.AppendLine("tree's .legs - then build on buildCores cores, then test on testCores cores. Where a");
        builder.AppendLine("leg runs is measured before anything starts; see");
        builder.AppendLine($"'{ToolPackage.Command} help legs'.");
        builder.AppendLine();
        builder.AppendLine("A verdict must describe the code, not the moment it ran in. Every test invocation");
        builder.AppendLine("declares a successPattern, since exiting 0 is not proof anything ran. A leg whose");
        builder.AppendLine("test inputs change while it runs, whose copy - a host's, or its own - could not be");
        builder.AppendLine("made the tree the run began with because a file in it changed first, or whose build");
        builder.AppendLine("directory another process uses, gets no pass or fail at all.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "Its inputs are watched only as deep as they lie - each directory holding one, for its own files, and each "
            + "directory above one, the root among them, for the names of the directories in it alone - so a build in another "
            + "tree below the worktrees root, which init keeps in git by a placeholder, is never heard. On Linux every such "
            + "directory is watched, however many there are, through one inotify instance for the whole process. On Windows "
            + $"and macOS a watch takes at most {FileSystemWatcherKind.MostDirectoriesWatched} directories besides the root's own, "
            + "those holding inputs and those above them alike, so past them the top-level directories watching the most are "
            + "watched all the way down, until the rest fit, and a tree whose inputs lie in more top-level directories than that "
            + "is watched whole. Where inotify cannot be called directly "
            + "on Linux, .NET's watcher stands in, as on Windows and macOS, each directory taking an inotify instance of its "
            + "own, and the process says so once. A directory that holds inputs, or lies above some, going, arriving or "
            + "changing its name counts every input in or below it, under both names of a rename, though it be back when the "
            + "work ends; one merely written to counts none. A leg whose inputs cannot be watched at all runs none of its "
            + "work, and ends unmeasured at once, saying why ('help verdicts'). Durations that diverge between legs, or that span a clock step or a host "
            + "sleep, are marked suspect, and never change a verdict.");
        builder.AppendLine();
        builder.AppendLine("A test count countPattern reads that differs from the other legs of the same project");
        builder.AppendLine("and test set is marked on its own - 'test count differs', and testCountDiffers and");
        builder.AppendLine("testCountNote in --json - never as a timing, and never changes a verdict either. A");
        builder.AppendLine("test invocation's testSet declares a set that differs on purpose, such as a");
        builder.AppendLine("platform's own tests:");
        builder.AppendLine();
        builder.AppendLine("  \"test\": {");
        builder.AppendLine("    \"all\": { \"runner\": \"ctest\", \"successPattern\": \"...\", \"countPattern\": \"...\" },");
        builder.AppendLine("    \"windows\": { \"testSet\": \"windows\" }");
        builder.AppendLine("  }");
        builder.AppendLine();
        builder.AppendLine("Windows legs are then compared with each other, and every other leg with the rest.");
        builder.AppendLine("A leg's os names its section however it is spelled - Windows as windows - as every");
        builder.AppendLine("per-system setting is read.");
        builder.AppendLine();
        builder.AppendLine("'test --filter', '--exclude' and '--label' reach the runner through the invocation's");
        builder.AppendLine("filterArg, excludeArg and labelArg, so one set of options serves every runner. For");
        builder.AppendLine("ctest, '-R' chooses tests by name, '-L' chooses them by label and '-LE' leaves a label");
        builder.AppendLine("out; a label could otherwise be left out but never chosen. The filter chooses the tests");
        builder.AppendLine("to run, each --exclude leaves its tests out, and each --label names one more the tests");
        builder.AppendLine("to run must carry. A runner that does not leave out each of several exclusions given");
        builder.AppendLine("apart - ctest leaves out only what every -LE matches - declares excludeJoin, and several");
        builder.AppendLine("reach it as one value joined by it: '|' for ctest, which init seeds. Where ctest's args");
        builder.AppendLine("give its excludeArg themselves, the exclusions are added to those values instead,");
        builder.AppendLine("where they stand: to each -LE's, and to the last -E's. An empty excludeJoin gives each");
        builder.AppendLine("its own excludeArg over a join the shared section declares.");
        builder.AppendLine();
        builder.AppendLine("'test --filter' may be given more than once, each value a unit that names tests, and");
        builder.AppendLine("the tests any unit names run. None of ctest, dotnet test and dart test runs them given");
        builder.AppendLine("its filter option apart - ctest keeps only the last -R, dotnet test refuses a second");
        builder.AppendLine("--filter, and dart test runs only a test every --name matches - so the units reach");
        builder.AppendLine("each as one value joined by '|', the join the harness knows each of the three reads");
        builder.AppendLine("as alternatives, where its invocation declares no filterJoin; init writes none.");
        builder.AppendLine("Another runner is given each unit after a filterArg of its own where it declares no");
        builder.AppendLine("filterJoin, and an empty filterJoin does that over a join the shared section");
        builder.AppendLine("declares. A workflow's test step gives its filter's units the same way.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "A test invocation may name what a failed run keeps as evidence: keepOnFailure, paths relative to the "
            + "leg's build directory - files, directories, or patterns, {buildDir} among the names they may use. When "
            + "the invocation does not pass, whatever the verdict, what they name is copied into kept-on-failure/ beside "
            + "the leg's log, each file's tree as it stood under the build directory, within keepOnFailureCapBytes kept "
            + $"in all ({DiskSpace.Size(Testing.KeptOnFailure.DefaultCapBytes)} where it is left out). A pass keeps "
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
            + "transport: 'sync --pull' takes the kept-on-failure directory within that host's run.");
        builder.AppendLine();
        builder.AppendLine("A leg on a host reached through WSL or ssh runs in that host's copy of its tree:");
        builder.AppendLine("the files the sync writes there from this tree, in a git repository of the host's own -");
        builder.AppendLine("one the sync made, or one it took over. Each sync makes its HEAD name the commit this");
        builder.AppendLine("tree is at, so what a step asks git about HEAD - which commit, what a file held there -");
        builder.AppendLine("is answered there as it is here. What changed since is answered for the files the copy");
        builder.AppendLine("holds: one this tree tracks and the sync withholds reads as deleted, one it carries");
        builder.AppendLine("untracked as added, and one git stores converted here - line endings, a clean filter -");
        builder.AppendLine("as changed, since the copy's index holds each file's bytes. The commit crosses as a");
        builder.AppendLine("pack of git's own, less what the copy holds, and nothing crosses while its HEAD names");
        builder.AppendLine("the commit and it holds what is asked. By default the commit alone crosses: a question");
        builder.AppendLine("about a commit behind it that the copy lacks fails there, in git's own words.");
        builder.AppendLine("\"sync\": { \"history\": \"full\" } gives the copy every commit behind it too, once, and");
        builder.AppendLine("each later one as it is made; a partial clone then has git fetch what it left out. A");
        builder.AppendLine("copy the sync took over has its HEAD moved where it names another commit - detached,");
        builder.AppendLine("and said the once it leaves a branch - and no branch, tag or commit of it changes: it");
        builder.AppendLine("gains the objects sent, and where it lacks the commit before the one it is given, git");
        builder.AppendLine("reads it as a shallow repository from then on, said too, which 'git fetch --unshallow'");
        builder.AppendLine("there undoes. The copy's branches, remotes and stash are still its own, and a");
        builder.AppendLine("submodule's history is not carried. Each");
        builder.AppendLine("sync makes its index hold the files it carried, so a build there fingerprints its");
        builder.AppendLine("inputs and keeps its directory as a build here does. A test that checks this");
        builder.AppendLine("checkout's state has nothing to say about that copy, so an invocation's");
        builder.AppendLine("remoteExcludes are given to every leg that works in a copy - a host's, or its own in");
        builder.AppendLine("the tree's .legs - beside --exclude's, and to none that works in its tree itself:");
        builder.AppendLine();
        builder.AppendLine("  \"all\": {");
        builder.AppendLine("    \"runner\": \"ctest\", \"successPattern\": \"...\",");
        builder.AppendLine("    \"excludeArg\": \"-LE\", \"excludeJoin\": \"|\", \"remoteExcludes\": [\"git-state\"]");
        builder.AppendLine("  }");
        builder.AppendLine();
        builder.AppendLine("They are held to every rule --exclude's are, when the file is read, where the");
        builder.AppendLine("invocation alone decides it: ctest needs the excludeJoin, since --exclude's and its");
        builder.AppendLine("args' own reach it beside them. A test preset's filters are read as the leg starts.");
        builder.AppendLine();
        builder.AppendLine("ctest is refused an option it would read otherwise: an exclusion beside another given");
        builder.AppendLine("apart with no join; a filter beside a -R its args give; a filter or an exclusion beside");
        builder.AppendLine("a test preset that sets the same filter itself; and any of the three beside");
        builder.AppendLine("--rerun-failed, beside --union in the args - but an exclusion by -E, which ctest still");
        builder.AppendLine("reads there unless the args or a preset choose tests by name too - or beside a test");
        builder.AppendLine("preset that takes a union or cannot be read. So is a selection ctest could only find");
        builder.AppendLine("empty, before anything is built for it: a value one option chooses tests by that");
        builder.AppendLine("another leaves out by the same part of a test - its name, or a label - where either is");
        builder.AppendLine("given - of several filter units, where it is so of every one. With excludeArg '-LE',");
        builder.AppendLine("'--label x' is refused beside an exclusion 'x', given by");
        builder.AppendLine("--exclude or the remoteExcludes, and beside the args' '-LE x' where it is their");
        builder.AppendLine("only -LE and no test preset they name adds one. Values are judged as written: spelled");
        builder.AppendLine("the same, or plain text where the chosen one holds the other, since ctest finds a");
        builder.AppendLine("pattern anywhere in a name or a label. ctest says it found no test only once the leg");
        builder.AppendLine("has been built; where it still finds none, the detail of a leg that did not pass says");
        builder.AppendLine("so, unless --no-tests=ignore keeps ctest quiet. A label beside labels the args or a");
        builder.AppendLine("preset choose runs: ctest reads it as one more a test must carry. An option given");
        builder.AppendLine("where its arg is not declared, or given empty or as spaces alone, is refused, and a");
        builder.AppendLine("host running one of the legs is given all three. A -I in the args, or a preset's");
        builder.AppendLine("index, picks by position among the tests the others leave, so a filter, an exclusion");
        builder.AppendLine("or a label moves a fixed window onto other tests.");
        builder.AppendLine();
        builder.AppendLine("When the file is read, ctest is also refused an invocation that would pass an option");
        builder.AppendLine("on as its opposite: a filterArg or a labelArg that leaves tests out, a labelArg that");
        builder.AppendLine("chooses them by name, an excludeArg that chooses them, and an excludeJoin other than");
        builder.AppendLine("'|', which ctest would read as part of one pattern that leaves out nothing. And ctest,");
        builder.AppendLine("dotnet test and dart test - dotnet or dart run with test as its command - are");
        builder.AppendLine("refused an empty filterJoin, which would give them several units apart, and a");
        builder.AppendLine("filterJoin other than '|', which each would read as part of one pattern or filter");
        builder.AppendLine("expression; dart test's --plain-name, which reads plain text, takes several units");
        builder.AppendLine("neither way, and is refused for --name. Each is refused a filter beside the same");
        builder.AppendLine("option its args give, as a run starts.");
        builder.AppendLine();
        builder.AppendLine("The file is checked when it is read: unknown keys, a key written twice, and");
        builder.AppendLine("references to undeclared names are rejected, with every problem listed at once.");
        builder.AppendLine("Comments and trailing commas are accepted, since the file is meant to be edited.");
        builder.AppendLine("A section whose command is not implemented yet is still checked, but has no effect");
        builder.AppendLine("until that command arrives.");

        return builder.ToString();
    }
}
