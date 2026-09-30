using System.CommandLine;
using System.Text;
using RepoHarness.Core.Ci;
using RepoHarness.Core.Anchors;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Git;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Orchestration;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Runners;
using RepoHarness.Core.Sync;
using RepoHarness.Core.Tools;

namespace RepoHarness.Cli.Commands;

/// <summary>
/// Wires <c>DssHarness help</c>: reference material that does not fit in option
/// descriptions. <c>--help</c> answers "what can I type"; this answers "what do the
/// answers mean", which is what a caller reaching for documentation actually wants.
/// </summary>
internal static class HelpCommand
{
    internal const string Name = "help";

    /// <summary>Opening words of the unrecognised-topic message.</summary>
    internal const string UnknownTopicPrefix = "Unknown topic";

    /// <summary>
    /// Every topic, in the order the overview lists them: the one list the overview, the topic argument, the answer to an
    /// unknown topic and the rendering all read, so no topic is ever advertised by one and missing from another.
    /// </summary>
    private static readonly IReadOnlyList<HelpTopic> Topics =
    [
        new("exit-codes", ["exit"], "What each exit code means", RenderExitCodes),
        new("config", ["configuration"], "What config.json declares", RenderConfig),
        new("legs", ["hosts", "emulators"], "Hosts, emulators, and how a leg finds where it runs", RenderLegs),
        new("space", ["disk", "clean"], "Freeing a full disk, and the room a build needs", RenderSpace),
        new("admission", ["heavy"], "How heavy legs share a machine: its slots and its memory", RenderAdmission),
        new("worktrees", ["worktree"], "Naming rules, the path budget, and when deleting refuses", RenderWorktrees),
        new("orchestrators", ["orchestrator", "agents", "agent"], "Orchestrators, their agents, and folding an agent's work", RenderOrchestrators),
        new("anchors", ["anchor"], "Anchor registries and the commands that change them", RenderAnchors),
        new("layout", [], "What init creates, and what git tracks", RenderLayout),
        new("secrets", ["hosts-secrets"], "Where each host's connection data lives", RenderSecrets),
        new("tools", [], "What install-missing-tools installs, and where", RenderTools),
        new("runners", ["runner", "actions"], "Predefined runners, action files and excused failures", RenderRunners),
        new("verdicts", ["verdict"], "What each leg verdict means, and what to do about it", RenderVerdicts),
        new("ci", ["check-ci-legs"], "How check-ci-legs finds a workflow's legs and budgets", RenderCi),
    ];

    private static readonly Argument<string?> TopicArgument = new("topic")
    {
        Description = $"Topic to explain: {string.Join(", ", Topics.Select(topic => topic.Name))}. Omit for an overview.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    internal static Command Create()
    {
        var command = new Command(Name, $"Explain what the answers mean, one topic at a time: {string.Join(", ", Topics.Select(topic => topic.Name))}.");
        command.Arguments.Add(TopicArgument);
        GlobalOptions.AddTo(command);

        command.SetAction(parseResult =>
        {
            var topic = parseResult.GetValue(TopicArgument);

            if (!TryRender(topic, out var text))
            {
                // A mistyped topic is a usage error, and it goes to stderr: zero always
                // means success, and stdout stays pipeable.
                Console.Error.Write(text);
                return HarnessExit.UsageError;
            }

            Console.Out.Write(text);
            return HarnessExit.Success;
        });

        return command;
    }

    /// <summary>Renders one topic, reporting whether it was recognised.</summary>
    internal static bool TryRender(string? topic, out string text)
    {
        text = Render(topic);
        return !text.StartsWith(UnknownTopicPrefix, StringComparison.Ordinal);
    }

    /// <summary>Renders one topic. Separated from the command so it is directly testable.</summary>
    internal static string Render(string? topic)
    {
        if (string.IsNullOrEmpty(topic))
        {
            return RenderOverview();
        }

        var asked = topic.ToLowerInvariant();

        return Topics.FirstOrDefault(known => known.Name == asked || known.Aliases.Contains(asked, StringComparer.Ordinal)) is { } found
            ? found.Render()
            : $"{UnknownTopicPrefix} '{topic}'. Try: {string.Join(", ", Topics.Select(known => known.Name))}.{Environment.NewLine}";
    }

    private static string RenderTools()
    {
        var builder = new StringBuilder();

        builder.AppendLine("Installing what a host is missing");
        builder.AppendLine();
        builder.AppendLine("'install-missing-tools' runs over every declared leg, or those --legs names, on the");
        builder.AppendLine("host each leg names with wsl or ssh, and on this machine otherwise. 'init");
        builder.AppendLine("--install-tools' runs it too; plain init installs nothing and says how.");
        builder.AppendLine();
        builder.AppendLine("--dry-run reaches and asks every host as a run does, and installs nothing: each tool");
        builder.AppendLine("it would install or update says 'would install' or 'would update' with the command");
        builder.AppendLine("that would run, sudo and all, and nobody is asked for a password or whether one is");
        builder.AppendLine("needed. It exits 1 while anything is missing, as a run that could not install it does.");
        builder.AppendLine();
        builder.AppendLine("Every WSL distribution and ssh host gets the .NET 10 SDK when it has none, under");
        builder.AppendLine("the home directory, where no login-free PATH names it - which is why every command");
        builder.AppendLine("the harness runs there spells the resolved absolute path rather than a bare name.");
        builder.AppendLine();
        builder.AppendLine("Each entry under tools that carries an install is probed with probe.args and");
        builder.AppendLine("probe.regex, compared against minVersion, and installed or updated through it. An");
        builder.AppendLine("entry with no install is an allowlist entry: probed if it declares a probe,");
        builder.AppendLine("reported when missing, never installed. That is how a program shipping with the");
        builder.AppendLine("platform, or with the repository, is allowed to appear in a runner's steps.");
        builder.AppendLine();
        builder.AppendLine("An entry may name the platforms it is needed on:");
        builder.AppendLine();
        builder.AppendLine("  { \"name\": \"cl\", \"platforms\": [\"windows\"] }");
        builder.AppendLine();
        builder.AppendLine("A host whose platform an entry does not name is never asked about it, so it is");
        builder.AppendLine("neither probed there nor counted against that host's legs. Left out, a tool is");
        builder.AppendLine("needed everywhere. Without this a repository could not declare both a Windows");
        builder.AppendLine("compiler and a POSIX one: each was reported missing on the other's hosts.");
        builder.AppendLine();
        builder.AppendLine("It may narrow that to the legs that need it; every scope it names must hold:");
        builder.AppendLine();
        builder.AppendLine("  \"toolchains\": [\"msvc\"]        legs whose variant builds with one of these");
        builder.AppendLine("  \"legs\": [\"win-arm\", \"gate\"]   these legs, or the legs of these leg sets");
        builder.AppendLine("  \"processors\": [\"arm64\"]       legs built for one of these: the leg's processor,");
        builder.AppendLine("                                which under an emulator is not the host's");
        builder.AppendLine("  \"emulators\": [\"qemu-arm64\"]   legs run under one of these emulators - how to");
        builder.AppendLine("                                scope what the emulating host needs, such as the");
        builder.AppendLine("                                emulator itself, which a native arm64 host does not");
        builder.AppendLine();
        builder.AppendLine("Two legs on one host share what is installed there, and each is told only about");
        builder.AppendLine("the tools it needs: cl scoped to msvc is never missing on a MinGW leg of the same");
        builder.AppendLine("machine. Each is told about a tool as it will find it: a leg whose toolchain names");
        builder.AppendLine("a developer environment, on the PATH that environment sets up for its processor -");
        builder.AppendLine("set up on this machine for the look, so cl is found where Visual Studio keeps it -");
        builder.AppendLine("and any other leg on its host's own PATH. An install runs once on a host, whichever");
        builder.AppendLine("leg asked first, and each is then told what it finds. On another host, a tool its");
        builder.AppendLine("own PATH lacks is unknown for a leg in a developer environment, which this command");
        builder.AppendLine("sets up only here, and nothing is installed for it. A tool no selected leg on a");
        builder.AppendLine("host needs is not looked for there at all. A scope naming nothing declared, or one");
        builder.AppendLine("covering no declared leg, is refused.");
        builder.AppendLine();
        builder.AppendLine("A privileged install takes its credential from that host's own item, on standard");
        builder.AppendLine("input only: it reaches no argument list, no log and no error message. A second run");
        builder.AppendLine("reports 'already current' and changes nothing. An unreachable host is named, and");
        builder.AppendLine("the other legs still go ahead.");
        builder.AppendLine();
        builder.AppendLine("Where a program is found");
        builder.AppendLine();
        builder.AppendLine("A command run over ssh or with wsl.exe reads no login profile, so its PATH is not the");
        builder.AppendLine("one a terminal shows: on macOS it lacks /opt/homebrew/bin. A program is looked for on");
        builder.AppendLine("that PATH first, then in the searched directories - on macOS and Linux:");
        builder.AppendLine();
        builder.AppendLine("  " + string.Join(" ", ToolSearchDirectories.Posix));
        builder.AppendLine();
        builder.AppendLine("and on Windows none, whose installers put a program on the machine PATH. A login");
        builder.AppendLine("shell is not asked instead: a profile that replaces PATH was measured making");
        builder.AppendLine("'command -v' answer wrongly over ssh.");
        builder.AppendLine();
        builder.AppendLine("  \"toolSearchDirectories\": { \"macos\": [\"/opt/local/bin\", \"~/bin\"] }");
        builder.AppendLine();
        builder.AppendLine("replaces the searched directories for a platform, or for 'all', when its list names");
        builder.AppendLine("something that platform can search; naming only another platform's directories, the");
        builder.AppendLine("built-in list stays there, and given empty it is refused. Each entry is '~/' for the");
        builder.AppendLine("home directory of whoever searches, or absolute for its platform: a drive or a share");
        builder.AppendLine("on Windows, a leading '/' elsewhere. Under 'all', an entry only one kind of machine");
        builder.AppendLine("can name is searched where it can be. The harness's own .NET SDK is always looked for");
        builder.AppendLine("in the built-in list, so a repository's list cannot hide it.");
        builder.AppendLine();
        builder.AppendLine("The search runs on the machine that runs the leg, and 'legs' and the leg's own run");
        builder.AppendLine("ask it through one function there. The directory each program was found in, on the");
        builder.AppendLine("PATH or off it, is appended to the PATH of every process the leg starts - even one");
        builder.AppendLine("whose environment sets PATH itself - so the run finds what the survey found, and a");
        builder.AppendLine("build tool's own children, ninja and the compilers cmake starts, are found where it");
        builder.AppendLine("was. Appended, never prepended: a name the PATH already answers for keeps that");
        builder.AppendLine("answer. install-missing-tools, which runs before the harness is on a host, looks in");
        builder.AppendLine("the same directories through the host's shell; where that shell cannot look, it");
        builder.AppendLine("says the tool is unknown rather than missing, and installs nothing.");
        builder.AppendLine();
        builder.AppendLine("A leg goes to the first of its hosts that can take it - the right machine, reached -");
        builder.AppendLine("whatever the command, which is where a sync puts its tree and where --use-staged");
        builder.AppendLine("finds it. It is turned away there when that host lacks a program its command will");
        builder.AppendLine("start:");
        builder.AppendLine();
        builder.AppendLine("  build   the project's build program, ninja for a Ninja generator, and the");
        builder.AppendLine("          compilers CC and CXX name");
        builder.AppendLine("  test    those, and the test runner; with --no-build, the runner alone");
        builder.AppendLine("  run     what the runner's steps start, and the build's too with requireBuild");
        builder.AppendLine("  sync    nothing: a copy starts no program");
        builder.AppendLine("  clean   nothing: removing a directory starts no program");
        builder.AppendLine("  legs    what build and test start");
        builder.AppendLine();
        builder.AppendLine("A missing program never moves a leg to another host: that would measure a machine");
        builder.AppendLine("nobody chose. To run a leg elsewhere, name the host in the leg ('ssh' or 'wsl').");
        builder.AppendLine();
        builder.AppendLine("Not every declared tool - a tool one runner needs does not make every other leg on");
        builder.AppendLine("a host without it unrunnable. A program is looked for when it is named by name, or");
        builder.AppendLine("by a path absolute on the leg's platform. CC and CXX are read as CMake reads them");
        builder.AppendLine("where the value alone can say: the whole value when it holds no space, else its");
        builder.AppendLine("first word when that is a name; any other value is left to CMake. The rest is the");
        builder.AppendLine("run's to find: a relative path, read from the directory its phase starts in; and a");
        builder.AppendLine("name with a placeholder in it. A program started under an environment the");
        builder.AppendLine("configuration declares that sets PATH - the build's, the test invocation's, the");
        builder.AppendLine("runner's, a phase's or a step's - is looked for too, so its directory reaches that");
        builder.AppendLine("PATH, and never turns a leg away: that PATH is where it is found when it starts. A");
        builder.AppendLine("PATH among a runner's values is not seen before the run; name its directory under");
        builder.AppendLine("toolSearchDirectories instead.");
        builder.AppendLine();
        builder.AppendLine("A leg turned away for a missing program is 'skipped-tool-missing', and the run is");
        builder.AppendLine($"incomplete, exit {HarnessExit.Incomplete} - or exit {LegsExit.Unavailable} when no selected leg can run at all. A leg");
        builder.AppendLine("that is already running when a program will not start has 'failed', naming the");
        builder.AppendLine("program and the reason the system gave: no survey could have required it - a file");
        builder.AppendLine("the build was to make, a binary for another processor. Neither is 'poisoned', which");
        builder.AppendLine("is kept for a defect in this tool.");

        return builder.ToString();
    }

    private static string RenderSecrets()
    {
        var builder = new StringBuilder();

        builder.AppendLine("Where each host's connection data lives");
        builder.AppendLine();
        builder.AppendLine("config.json declares only NAMES, under sshItems and wslDistros. An address, a");
        builder.AppendLine("user, a key path and a credential are never in it: that file is tracked, and a");
        builder.AppendLine("config.json arriving through git must not be able to point the harness at a");
        builder.AppendLine("machine nobody set up here.");
        builder.AppendLine();
        builder.AppendLine($"  .harness-config/{HarnessLayout.SshItemsDirectoryName}/<name>/{HarnessLayout.ItemEnvFileName}");
        builder.AppendLine("                                     ADDRESS=, USER=, PORT= (optional)");
        builder.AppendLine($"  .harness-config/{HarnessLayout.SshItemsDirectoryName}/<name>/{HarnessLayout.ItemKeyFileName}");
        builder.AppendLine("                                     the private key");
        builder.AppendLine($"  .harness-config/{HarnessLayout.SshItemsDirectoryName}/<name>/{HarnessLayout.ItemKnownHostsFileName}");
        builder.AppendLine("                                     the host keys ssh may accept");
        builder.AppendLine($"  .harness-config/{HarnessLayout.WslDistrosDirectoryName}/<name>/{HarnessLayout.ItemEnvFileName}");
        builder.AppendLine("                                     the distribution, and the superuser credential");
        builder.AppendLine("                                     install-missing-tools needs");
        builder.AppendLine();
        builder.AppendLine("hosts.ssh and hosts.wsl are keyed by these names, and a host declared without an");
        builder.AppendLine("item is refused when config.json is read: it could never be reached, and failing");
        builder.AppendLine("later would blame ssh for a mistake in this file.");
        builder.AppendLine();
        builder.AppendLine("An .env other users can change is refused before anything connects, because");
        builder.AppendLine("whoever can change it can send the harness somewhere else. A key other users can");
        builder.AppendLine("read is refused because ssh ignores it. Both refusals name the command that fixes");
        builder.AppendLine("them. A credential is passed to a privileged command on standard input, and");
        builder.AppendLine("reaches no log, no argument list and no error message.");
        builder.AppendLine();
        builder.AppendLine("init writes the ignore rules that keep all of it out of git. A fresh clone plus");
        builder.AppendLine("this tree is enough for 'legs' to reach every host.");

        return builder.ToString();
    }

    private static string RenderRunners()
    {
        var builder = new StringBuilder();

        builder.AppendLine("Predefined runners");
        builder.AppendLine();
        builder.AppendLine("A procedure specific to this repository - a corpus run, a benchmark, a round trip");
        builder.AppendLine($"- is declared under predefinedRunners and started with '{ToolPackage.Command} run <name>'.");
        builder.AppendLine("It runs across the legs it declares, with the same isolation, locking, stall");
        builder.AppendLine("bounds, witnesses and reporting build and test get.");
        builder.AppendLine();
        builder.AppendLine("A runner declares phases, or an action file, never both. Every key either takes");
        builder.AppendLine("is listed at the end.");
        builder.AppendLine();
        builder.AppendLine("Each action owns one directory, and its file carries that directory's name, so a");
        builder.AppendLine("runner's 'action' is '<name>/<name>.yml'. Directories above that one group actions");
        builder.AppendLine("and are yours to arrange: 'real-examples/c/probe-nest/probe-nest.yml' is the action");
        builder.AppendLine("'probe-nest', grouped under 'real-examples/c'. What they may not be is actions");
        builder.AppendLine("themselves - a directory holding a file of its own name is an action, and an action");
        builder.AppendLine("cannot contain another, because everything beside an action's file belongs to it.");
        builder.AppendLine("A run line is a program and its arguments");
        builder.AppendLine("with no shell, so anything that is not a one-liner lives in a file; the directory is");
        builder.AppendLine("where that file goes, owned by the action it belongs to and read by the same review.");
        builder.AppendLine("A flat '<name>.yml', a path leaving the directory, and a file whose name differs from");
        builder.AppendLine("its directory's are each refused, naming the path the runner should have had.");
        builder.AppendLine();
        builder.AppendLine($"No directory along that path may be called '{HarnessLayout.ActionBuildDirectoryName}' or "
            + $"'{HarnessLayout.ActionArtifactsDirectoryName}', at any depth.");
        builder.AppendLine("Every action already owns one of each, below, and a directory that was both would be");
        builder.AppendLine("ignored by the rules that keep a run's output out of git - so the action's own file");
        builder.AppendLine("would never be committed, and what a reviewer reads would not be what runs.");
        builder.AppendLine();
        builder.AppendLine($"  {HarnessLayout.RunnerActionsDirectoryRelative}/<name>/<name>.yml");
        builder.AppendLine("                                     the steps, tracked by git");
        builder.AppendLine($"  {HarnessLayout.RunnerActionsDirectoryRelative}/<name>/...");
        builder.AppendLine("                                     whatever those steps run, beside them");
        builder.AppendLine($"  {HarnessLayout.RunnerDirectoryRelative}/{HarnessLayout.RunnerEnvDirectoryName}/");
        builder.AppendLine("                                     values the steps read, ignored");
        builder.AppendLine($"  {HarnessLayout.RunnerDirectoryRelative}/{HarnessLayout.RunnerSecretsDirectoryName}/");
        builder.AppendLine("                                     secret values, ignored and never printed");
        builder.AppendLine();
        builder.AppendLine("A sync carries the actions to every host the way it carries the rest of the tree:");
        builder.AppendLine("what git does not ignore crosses, committed or not, and an action the tree no");
        builder.AppendLine("longer has is removed from a host that has it. Nothing else in .harness-config");
        builder.AppendLine("crosses except config.json - the one this command read, which for a worktree is");
        builder.AppendLine("that worktree's. Connection data, secrets, values, locks and runs stay on their");
        builder.AppendLine("machine, and so does each action's own build and artifacts, whatever .gitignore says.");
        builder.AppendLine();
        builder.AppendLine("A run that uses an action asks git first whether that action's build and artifacts");
        builder.AppendLine("are ignored, and refuses before any step when either is not, naming the missing");
        builder.AppendLine("rule and 'init' as the fix: a run's output written where git would commit it is a");
        builder.AppendLine("measurement nobody can reproduce. Where git cannot answer in a leg's tree - a copy it");
        builder.AppendLine("will not trust, a worktree whose link is broken - that leg is unavailable, in git's");
        builder.AppendLine("own words, and the other legs still run.");
        builder.AppendLine();
        builder.AppendLine("A step either uses a predefined action or carries a run block. A run block is");
        builder.AppendLine("split on newlines and each line trimmed, so indentation and blank lines cannot");
        builder.AppendLine("change what runs. Each line is a program and its arguments, never a shell string.");
        builder.AppendLine("Quoting is double quotes only, no escapes, and a line with an unbalanced quote is");
        builder.AppendLine("refused when the file is read: the splitter silently drops everything after one,");
        builder.AppendLine("so the alternative is a command missing arguments nobody can see are missing.");
        builder.AppendLine("The first token must be a program declared under tools, or a path in the");
        builder.AppendLine("repository; anything else is refused before a single step runs. It is judged as");
        builder.AppendLine("it will start, its names filled in and read from the directory its step runs in:");
        builder.AppendLine("'{dir}/tool' is inside the repository only where {dir} keeps it there. A line, or");
        builder.AppendLine("a step's directory, that fills in to nothing is refused the same way, naming it.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"A step that uses {string.Join(" or ", PredefinedActions.All)} runs no program, so it takes none of the "
            + $"keys only a run block reads, and is refused with any of them: '{string.Join("', '", KeyDescription.Names(ActionFileKeys.RunBlock))}'.");
        builder.AppendLine();
        builder.AppendLine("A step runs at the leg's tree root unless it says otherwise, which is what a step");
        builder.AppendLine("with neither key below has always done. The new layout reads as though a step ran");
        builder.AppendLine("beside its own file; it does not, unless it asks to:");
        builder.AppendLine();
        AppendSpelt(builder, ("workingDirectoryRoot", "<root>"), ("workingDirectory", "<path>"));
        builder.AppendLine();
        builder.AppendLine($"  {WorkingDirectoryRoots.Tree,-8} the leg's worktree, or the repository. The default.");
        builder.AppendLine($"  {WorkingDirectoryRoots.Harness,-8} the tree's .harness-config directory.");
        builder.AppendLine($"  {WorkingDirectoryRoots.Action,-8} the action's own directory, beside the files it ships.");
        builder.AppendLine();
        builder.AppendLine($"So a step running a program it ships says 'workingDirectoryRoot: {WorkingDirectoryRoots.Action}' and");
        builder.AppendLine("then names it as './probe.py': a program named by a relative path is read from the");
        builder.AppendLine("directory its step runs in, by the run and by the check that allows it alike. A");
        builder.AppendLine("step that builds or tests the repository says nothing and keeps the tree root, as");
        builder.AppendLine("every step written before this did.");
        builder.AppendLine();
        AppendSpelt(builder, ("runOn", "[<system>, ...]"));
        builder.AppendLine();
        builder.AppendLine("A step without runOn runs on every leg. A leg of another operating system skips a");
        builder.AppendLine("step that names runOn, says so as it runs, and lists it as skippedSteps on its line");
        builder.AppendLine("in --json; nothing about the step is asked of that leg - not its program, which no");
        builder.AppendLine("host is turned away for, and not the names its lines use. A step that reads what a");
        builder.AppendLine("skipped step would have made finds nothing there, so give both the same runOn. A");
        builder.AppendLine("run in which some leg would run no step at all is refused before anything starts,");
        builder.AppendLine("naming the leg: it would pass having run nothing.");
        builder.AppendLine();
        AppendSpelt(builder, ("manual", "true"), ("needs", "[<step>, ...]"), ("inputs", "<name>: ..."));
        builder.AppendLine();
        builder.AppendLine("A run that names no step runs every step that is not manual, and says of each manual");
        builder.AppendLine("one that it did not run it - as it goes, and as unselectedSteps on each leg's line in");
        builder.AppendLine("--json - so a plain run is never read as having done that work. With");
        builder.AppendLine($"'{ToolPackage.Command} run <runner> {StepSelection.Option} <step>' it runs only the manual steps named,");
        builder.AppendLine("each after the steps it needs, in the order the file declares them: the steps a plain");
        builder.AppendLine("run runs do not run unless one of those needs them. A runner may name its own steps");
        builder.AppendLine("in config.json, \"steps\": [\"<step>\", ...], and runs those, manual or not, with what");
        builder.AppendLine("they need - so work that belongs with one action, a benchmark beside the build it");
        builder.AppendLine("shares modules with, can be a runner of its own with legs of its own. What");
        builder.AppendLine($"{StepSelection.Option} names wins over a runner's steps. A leg lists the steps it ran as");
        builder.AppendLine("ranSteps on its line, and the manual ones among them as manualSteps.");
        builder.AppendLine();
        builder.AppendLine("A manual step declares successPattern: it runs only when named, and a line of its own");
        builder.AppendLine("output is what says it did the work it was named for. A step can need only one");
        builder.AppendLine("declared before it, and one that runs on every system it runs on; a step every run");
        builder.AppendLine("runs cannot need a manual one; a manual step cannot share its name with one that is");
        builder.AppendLine("not; and a predefined action cannot be manual - a manual step that must have one");
        builder.AppendLine("first names it under needs. A step's own inputs are its alone: another step naming");
        builder.AppendLine($"one names nothing, and a run that does not run the step refuses {CommandLineInputs.Option} for them.");
        builder.AppendLine($"A {StepSelection.Option} naming a step the action lacks or one that is not manual, a runner");
        builder.AppendLine("naming a step its action lacks, and a leg on whose system none of the steps a run");
        builder.AppendLine("names runs - it would run only what they need, and pass - are refused before a host");
        builder.AppendLine("is measured, naming the steps there are.");
        builder.AppendLine();
        AppendSpelt(builder, ("successPattern", "<regular expression>"));
        builder.AppendLine();
        builder.AppendLine("A step passes when each of its lines exits 0; one that declares successPattern must");
        builder.AppendLine("also have its last line print something the pattern matches, since a program that");
        builder.AppendLine("exits 0 has not shown it did anything. The pattern is a .NET regular expression,");
        builder.AppendLine("matched with ^ and $ at each line - whether a line ends in CRLF or LF, as its log");
        builder.AppendLine("keeps it - against that line's standard output and standard error read together,");
        builder.AppendLine("after secrets are redacted. One that does not compile, or that");
        builder.AppendLine("is empty and so matches anything, is refused when the file is read. The earlier lines");
        builder.AppendLine("of a run block answer with their exit codes alone: the witness belongs to the step,");
        builder.AppendLine("and its last line finishing is its work being done.");
        builder.AppendLine();
        builder.AppendLine("Names a run line may use");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "A run line is written for this tool, so a name in braces it cannot fill in is refused over the whole "
            + "file before the first program starts, as is one this leg has nothing for. '${NAME}' belongs to "
            + "another expander and is never touched; a brace meant literally is doubled, so awk '{{print}}' "
            + $"reaches awk as '{{print}}'. A name {LegPathNames.NameRule}.");
        builder.AppendLine();
        builder.AppendLine("  {treeDir} {buildDir} {harnessDir}   the leg's tree, its variant-keyed build");
        builder.AppendLine("                                      directory, and the tree's .harness-config");
        builder.AppendLine("  {leg} {os} {processor}              which leg this is, derived rather than typed:");
        builder.AppendLine("  {toolchain} {config} {variant}      an instrument that records what it measured");
        builder.AppendLine("  {host} {runId}                      cannot then be labelled wrongly by hand");
        builder.AppendLine("  {product}                           the one file this build is declared to make.");
        builder.AppendLine("                                      Refused where the project declares none or");
        builder.AppendLine("                                      several, rather than guessing which");
        builder.AppendLine("  <input name>                        any input the action declares, or the step");
        builder.AppendLine("                                      declares for itself, by its name");
        builder.AppendLine($"  <value name>                        any value the runner's {HarnessLayout.RunnerEnvDirectoryName} holds, by its name");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"An input or a {HarnessLayout.RunnerEnvDirectoryName} value named like a name above is refused, since a run line "
            + "naming it would get this tool's value while the environment held its own; so is an input whose "
            + "name a run line cannot write.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"An action's 'inputs' are resolved from 'run {CommandLineInputs.Option} <name>=<value>' first, "
            + $"{HarnessLayout.RunnerDirectoryRelative}/{HarnessLayout.RunnerEnvDirectoryName} second and each input's "
            + $"own 'default' last; {HarnessLayout.RunnerSecretsDirectoryName} never gives one a value. A required input "
            + "with none of them is refused before the first step runs, and so is a step naming one that is not "
            + "required and has none, naming the input and how it can have one. Where the action has a "
            + $"{PredefinedActions.ReadInputs} step, the same values reach every step as INPUT_<NAME> in the "
            + "environment. The environment is how a secret is handed over, under its own name: a value spliced "
            + "into a command line reaches the process table, where anything on the machine can read it. A step's "
            + "own inputs are resolved the same way and reach only its own run lines, by name; the environment "
            + "holds the action's alone.");
        builder.AppendLine();
        builder.AppendLine($"{CommandLineInputs.Option} takes one name=value each time it is given, for an input the action");
        builder.AppendLine("declares or a step the run runs declares for itself, and only for the runner the");
        builder.AppendLine("command line names: a runner a run check starts reads its own values. A host running");
        builder.AppendLine("one of the run's legs is given the same values, and the same manual steps. Any other");
        builder.AppendLine("name, a runner of phases, an empty value and a name given twice are refused before a");
        builder.AppendLine("host is measured - an unset shell variable is not a request to run with nothing. The");
        builder.AppendLine("value is a plain one, on a command line; a secret stays in");
        builder.AppendLine($"{HarnessLayout.RunnerSecretsDirectoryName}.");
        builder.AppendLine();
        builder.AppendLine("What a step produces");
        builder.AppendLine();
        builder.AppendLine("A step may say what it makes, and what it makes may outlive the run:");
        builder.AppendLine();
        AppendSpelt(builder, ("outputs", "[<path>, ...]"), ("persist", "true"));
        builder.AppendLine();
        builder.AppendLine("  {stepBuild}         this step's own directory, created before it runs");
        builder.AppendLine("  {actionBuild}       this leg's directory for this run, holding one per step");
        builder.AppendLine("  {actionArtifacts}   where this leg's kept outputs go");
        builder.AppendLine("  {runArtifacts}      this run's kept outputs across every leg, one");
        builder.AppendLine("                      directory per leg. How a step reads what another");
        builder.AppendLine("                      leg produced");
        builder.AppendLine();
        builder.AppendLine("  <action>/build/<run id>/<step>/       while the run lasts");
        builder.AppendLine("  <action>/artifacts/<run id>/<step>/   what it asked to keep");
        builder.AppendLine();
        builder.AppendLine("Both are gitignored, and neither is written into directly: keyed by the run, two");
        builder.AppendLine("legs or a retry cannot write over each other. A declared output that is not there");
        builder.AppendLine("when the step exits zero makes the leg 'unwitnessed' - exiting zero having written");
        builder.AppendLine("nothing is the failure an exit code alone can never report. The build directory is");
        builder.AppendLine("emptied when the action finishes, whatever the verdict, so anything a later run");
        builder.AppendLine("needs has to say 'persist'.");
        builder.AppendLine();
        builder.AppendLine("A leg's line in --json names each file its steps kept as keptOutputs, relative to the");
        builder.AppendLine($"tree - the path '{ToolPackage.Command} sync --pull' takes to bring it back from the host that");
        builder.AppendLine("kept it.");
        builder.AppendLine();
        builder.AppendLine("Outputs are kept as soon as the step that made them passes, not at the end of the");
        builder.AppendLine("run, because a later step reads them. A step that FAILED keeps nothing, although it");
        builder.AppendLine("may have written the file: carrying evidence out of work that did not pass is what");
        builder.AppendLine("the whole verdict vocabulary exists to refuse.");
        builder.AppendLine();
        builder.AppendLine("This is also what survives a resumed run. A step already done is skipped, and the");
        builder.AppendLine("build directory of the attempt that ran it is gone - so a later step reaching an");
        builder.AppendLine("earlier one's work across a resume has to read it from the artifacts, not from");
        builder.AppendLine("'{actionBuild}'.");
        builder.AppendLine();
        builder.AppendLine("Carrying an artifact to another machine");
        builder.AppendLine();
        builder.AppendLine($"  {ToolPackage.Command} sync --artifact <run id>");
        builder.AppendLine();
        builder.AppendLine("carries exactly that run's kept artifacts to each host, at the same relative path");
        builder.AppendLine("they have here - the run and the producing leg are already in it, so a consuming");
        builder.AppendLine("step's '{runArtifacts}/<leg>/<step>/<file>' works unchanged on the far side. Each");
        builder.AppendLine("file is verified after it lands, for the reason --pull verifies: an artefact carried");
        builder.AppendLine("machine to machine is evidence that something built there runs here, and evidence");
        builder.AppendLine("nobody checked is not evidence.");
        builder.AppendLine();
        builder.AppendLine("A run that kept nothing is refused by name, whether or not any host needed a copy.");
        builder.AppendLine("An ordinary sync never carries these directories, whatever .gitignore says; this");
        builder.AppendLine("carries one run's, by name, and nothing else.");
        builder.AppendLine();
        builder.AppendLine("It writes into a copy that is already there and makes none of its own: a host with");
        builder.AppendLine("no copy, or one the harness did not create, is refused before a single file leaves");
        builder.AppendLine($"this machine. Run '{ToolPackage.Command} sync' first - with '--adopt \"<host>\"' where a");
        builder.AppendLine("directory is already at that repositoryPath. Otherwise a mistyped repositoryPath");
        builder.AppendLine("would be filled in rather than noticed.");
        builder.AppendLine();
        builder.AppendLine("A carry lands whole or not at all. Interrupted, it takes back what it had already");
        builder.AppendLine("written and says so - a directory holding two thirds of what the producer kept is");
        builder.AppendLine("the same path holding fewer files, and a step reading it would measure less than was");
        builder.AppendLine("built and pass. With '--dry-run' it carries nothing and lists what it would carry,");
        builder.AppendLine("as '--pull' does.");

        builder.AppendLine();
        builder.AppendLine("A step may ask for the guards the build and test verbs carry. Both are off unless");
        builder.AppendLine("asked for, and both watch the whole action rather than one step: an action of five");
        builder.AppendLine("steps has four gaps between them, and a file changed in a gap is the same moving");
        builder.AppendLine("tree as one changed inside a step.");
        builder.AppendLine();
        AppendSpelt(builder, ("watchContention", "true"), ("requireInputsUnmoved", "true"));
        builder.AppendLine();
        builder.AppendLine("The first samples the process table against the leg's build directory, so another");
        builder.AppendLine("run building there is reported rather than silently shared with. It needs a leg: an");
        builder.AppendLine("action run without one is refused, because a sample of no directory reports a clean");
        builder.AppendLine("one. The second fingerprints the tracked files before, during and after, so the result");
        builder.AppendLine("never describes a tree that did not exist.");
        builder.AppendLine();
        builder.AppendLine("expectedExceptions names failures a runner may produce, with the outcome to");
        builder.AppendLine("report instead. Every entry names the keys marked required below, and is refused");
        builder.AppendLine("without them: an excusal nobody can audit stops being a record of a");
        builder.AppendLine("measured confound and becomes a way to make a regression invisible. An entry");
        builder.AppendLine("naming no message, or one matching anything, is refused for the same reason.");
        builder.AppendLine();
        builder.AppendLine("runChecks gate an entry on another runner confirming the confound. Unconfirmed,");
        builder.AppendLine("the failure stays genuine. The window is the failing unit's own output up to its");
        builder.AppendLine("verdict line, never a once-per-run sample: a sample was measured charging");
        builder.AppendLine("genuine-looking failures to the tool on a loaded machine and excusing them on a");
        builder.AppendLine("quiet one, the same day.");
        builder.AppendLine();
        builder.AppendLine("Every key");
        builder.AppendLine();
        builder.AppendLine("These are all of them: any other is refused when the file is read. A runner under");
        builder.AppendLine("predefinedRunners in config.json takes:");
        builder.AppendLine();
        AppendKeys(builder, ConfigKeys.Of<RunnerConfig>());
        builder.AppendLine();
        builder.AppendLine("An action file takes:");
        builder.AppendLine();
        AppendKeys(builder, ActionFileKeys.File);
        builder.AppendLine();
        builder.AppendLine("The stall bound a phase or step runs under is its own stallSeconds, else its");
        builder.AppendLine("runner's, else defaults.stallSeconds: the first of them set, where 0 means none,");
        builder.AppendLine("as does setting none. 'help config' gives the order its environment is built in.");

        return builder.ToString();
    }

    /// <summary>
    /// Lists <paramref name="keys"/> one to a line, each nested section's keys indented beneath the key
    /// that holds them, all meanings in one column.
    /// </summary>
    private static void AppendKeys(StringBuilder builder, IReadOnlyList<KeyDescription> keys)
        => AppendColumns(builder, [.. Indented(keys, "  ").Select(line => (line.Label, line.Key.Meaning + (line.Key.Required ? "; required" : string.Empty)))]);

    /// <summary>Each key written after <paramref name="indent"/>, followed by its own section's keys one step further in.</summary>
    private static IEnumerable<(string Label, KeyDescription Key)> Indented(IReadOnlyList<KeyDescription> keys, string indent)
        => keys.SelectMany(key => Indented(key.Keys, indent + "  ").Prepend((indent + key.Name, key)));

    /// <summary>
    /// Writes how each step key in <paramref name="spellings"/> is written, beside what it does as
    /// 'Every key' says it: one meaning, in one place, however many times help shows the key.
    /// </summary>
    private static void AppendSpelt(StringBuilder builder, params (string Key, string Value)[] spellings)
        => AppendColumns(
            builder,
            [.. spellings.Select(spelt => ($"  {spelt.Key}: {spelt.Value}", ActionFileKeys.Step.Single(key => key.Name == spelt.Key).Meaning))]);

    /// <summary>Writes each label with its text beside it, every text starting in one column.</summary>
    private static void AppendColumns(StringBuilder builder, IReadOnlyList<(string Label, string Text)> lines)
    {
        var column = lines.Max(line => line.Label.Length) + 2;

        foreach (var (label, text) in lines)
        {
            builder.AppendLine(label.PadRight(column) + text);
        }
    }

    /// <summary>Writes <paramref name="text"/> as lines no wider than the rest of help, broken between words.</summary>
    private static void AppendWrapped(StringBuilder builder, string text)
    {
        const int Width = 88;
        var line = new StringBuilder();

        foreach (var word in text.Split(' '))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > Width)
            {
                builder.AppendLine(line.ToString());
                line.Clear();
            }

            line.Append(line.Length > 0 ? " " : string.Empty).Append(word);
        }

        builder.AppendLine(line.ToString());
    }

    private static string RenderVerdicts()
    {
        var builder = new StringBuilder();

        builder.AppendLine("What each leg verdict means");
        builder.AppendLine();
        builder.AppendLine("Every declared leg reaches exactly one. A leg that reaches none is a defect in");
        builder.AppendLine("the harness and fails the run, rather than vanishing from the report.");
        builder.AppendLine();

        foreach (var info in Verdicts.All)
        {
            builder.AppendLine(
                $"  {info.Display,-22} {(info.IsFailure ? "counts as failure" : "not a failure"),-18} exit {info.ExitCode}");
        }

        builder.AppendLine();
        builder.AppendLine($"A run where nothing failed but some leg reached no verdict exits {HarnessExit.Incomplete}, not 0, and");
        builder.AppendLine("names the legs that did not report. A leg that never ran proves nothing about the");
        builder.AppendLine("code, so counting it among the legs that passed reports evidence nobody gathered.");
        builder.AppendLine();
        builder.AppendLine("When several apply the more fundamental one is reported, in the order above.");
        builder.AppendLine("A leg whose inputs moved is not reported as failed even when its tests failed,");
        builder.AppendLine("because what failed was a tree that never existed.");
        builder.AppendLine();
        builder.AppendLine("A run's closing line names its verdict with the count of legs that reached it, and");
        builder.AppendLine("then the rest, worst first - 'poisoned: 2 of 8 leg(s); 3 failed, 3 passed' - never");
        builder.AppendLine("the verdict beside the total, which reads as every leg having it.");
        builder.AppendLine();
        builder.AppendLine("A tool contention.sharedResourceTools lists, found running beside a leg, is a");
        builder.AppendLine("warning: one line per tool and per whose it was, with a count and the range of");
        builder.AppendLine("process ids. A process working in another declared leg's build directory is named");
        builder.AppendLine("as that leg's - two legs on one host share its load, which is maxParallelLegs at");
        builder.AppendLine("work, and yours to decide about. contention.sharedState says what each tool shares,");
        builder.AppendLine("in the words the warning uses:");
        builder.AppendLine();
        builder.AppendLine("  \"sharedState\": { \"ccache\": \"the per-user compiler cache\" }");
        builder.AppendLine();
        builder.AppendLine("Remedies");
        builder.AppendLine($"  {LegExit.InputsMoved}  inputs-moved, unmeasured   Let the tree settle, then run again");
        builder.AppendLine($"  {LegExit.Contended}  contended                  Wait for the other run");
        builder.AppendLine($"  {LegExit.Unwitnessed}  unwitnessed                Find out what actually ran");
        builder.AppendLine($"  {LegExit.LogHeld}  log-held                   Find out which run still owns the logs");
        builder.AppendLine($"  {LegExit.NotAdmitted}  not-admitted               Wait for the heavy legs it names, free memory,");
        builder.AppendLine("                                or raise the machine's limits ('help admission')");

        return builder.ToString();
    }

    private static string RenderAdmission()
    {
        var builder = new StringBuilder();

        builder.AppendLine("Heavy legs on one machine");
        builder.AppendLine();
        builder.AppendLine("A leg that builds or tests is heavy, and so is a runner's where it, or a runner its");
        builder.AppendLine("expected exceptions' run checks name, requires the build or says \"heavy\": true; a");
        builder.AppendLine("runner that only reads the tree is light, and starts at once. Where a machine");
        builder.AppendLine("declares admission, each heavy leg - its tree synced and its lock taken - waits for");
        builder.AppendLine("the machine to take it: first one of its slots, shared by every command this user");
        builder.AppendLine("runs there - worktrees each running a gate of their own, which maxParallelLegs,");
        builder.AppendLine("counted by one command, cannot see - given in the order legs asked; then the memory");
        builder.AppendLine("in use below the limit. A leg holds its slot until its work ends, and one whose");
        builder.AppendLine("command has ended - crashed, killed - is reclaimed by the next leg that looks, and");
        builder.AppendLine("said to be. A waiting leg keeps its lock: another run of its variant is");
        builder.AppendLine("refused-locked meanwhile, as it would be while the leg ran.");
        builder.AppendLine();
        builder.AppendLine("  \"defaults\": { \"admission\": { \"heavyLegs\": 2, \"maxMemoryPercent\": 76 } },");
        builder.AppendLine("  \"hosts\": { \"local\": { \"admission\": { \"heavyLegs\": 1 } } }");
        builder.AppendLine();
        builder.AppendLine($"  heavyLegs         heavy legs the machine runs at once, at least 1 ({AdmissionSettings.DefaultHeavyLegs})");
        builder.AppendLine("  maxMemoryPercent  the memory in use a leg holding a slot starts below, above 0");
        builder.AppendLine($"                    and at most 100 ({AdmissionSettings.DefaultMaxMemoryPercent})");
        builder.AppendLine("  settleSeconds     [least, most]: where another leg holds a slot, a reading below");
        builder.AppendLine("                    the limit is read again after a time picked between them, and");
        builder.AppendLine("                    the leg starts only if it still is, so two legs taking their");
        builder.AppendLine("                    slots together do not both start on one reading; [0, 0] reads");
        builder.AppendLine($"                    once ([{string.Join(", ", AdmissionSettings.DefaultSettleSeconds)}])");
        builder.AppendLine($"  pollSeconds       the time between looks while a leg waits, at least 1 ({AdmissionSettings.DefaultPollSeconds})");
        builder.AppendLine($"  maxWaitMinutes    how long a leg waits before it is not-admitted, above 0 ({AdmissionSettings.DefaultMaxWaitMinutes})");
        builder.AppendLine();
        builder.AppendLine($"Seconds are at most {AdmissionSettings.MostSeconds}, and the wait at most {AdmissionSettings.MostWaitMinutes} minutes.");
        builder.AppendLine();
        builder.AppendLine("Declared under defaults, or under hosts.local or an ssh host, whose fields replace");
        builder.AppendLine("the defaults' one by one; a machine where neither declares one takes every leg at");
        builder.AppendLine("once. What a command's own configuration declares is what its legs are admitted by:");
        builder.AppendLine("a repository that declares none never joins the line, and where repositories that");
        builder.AppendLine("declare different counts share a machine, a leg starts only while it is fewer legs");
        builder.AppendLine("from the front than every count up to it allows. A WSL distribution runs on this");
        builder.AppendLine("machine: its heavy legs take this machine's slots, against its memory, before they");
        builder.AppendLine("are sent there, and admission under hosts.wsl is refused - though a command typed");
        builder.AppendLine("inside a distribution keeps a record of the distribution's own, apart from those of");
        builder.AppendLine("the commands typed on Windows. An ssh host is a machine of its own, and takes the");
        builder.AppendLine("legs sent to it by its own section.");
        builder.AppendLine();
        builder.AppendLine("The memory in use is each system's own count of what it can no longer give: on");
        builder.AppendLine("Windows the commit charge against the commit limit, which grows with the page file;");
        builder.AppendLine("on Linux MemTotal less MemAvailable; on macOS 100 less the free share memory_pressure");
        builder.AppendLine("reports. A machine whose count could not be read in a leg's wait takes the leg on");
        builder.AppendLine("its slot alone, and its line says so; one that stops giving a reading it gave is");
        builder.AppendLine("read again, never taken on the reading it last gave.");
        builder.AppendLine();
        builder.AppendLine("While it waits, a leg says who holds each slot - tree, variant, host, leg, command,");
        builder.AppendLine("machine, process, run, and since when it asked - and, once it holds one, what the");
        builder.AppendLine("memory stands at. Its line, and admission in --json, name how long it waited and");
        builder.AppendLine("the memory it started at; --json also names the record it asked in. One that waited");
        builder.AppendLine($"maxWaitMinutes is not-admitted, exit {LegExit.NotAdmitted}, naming what held the slots and where they");
        builder.AppendLine("are recorded, or the memory it waited on: nothing of it ran, and nothing about the");
        builder.AppendLine("code is claimed.");
        builder.AppendLine();
        builder.AppendLine("The slots are kept in <user data>/dssharness/admission-<machine id>.json - the user");
        builder.AppendLine("data being LOCALAPPDATA on Windows, ~/Library/Application Support on macOS, and");
        builder.AppendLine("$XDG_DATA_HOME or ~/.local/share on Linux - beside the hold that keeps the machine");
        builder.AppendLine("awake: one record per machine and per user, whichever repository asks, named by the");
        builder.AppendLine("identifier the system keeps for the machine rather than by its name, which a Mac");
        builder.AppendLine("takes from each network it joins. Where the system keeps none - a container its");
        builder.AppendLine("image gave no machine-id - the record is named by the machine's name, and the first");
        builder.AppendLine("heavy leg says so. Another user's legs are counted in that user's own.");

        return builder.ToString();
    }

    private static string RenderOverview()
    {
        var builder = new StringBuilder();

        builder.AppendLine("DssHarness - one cross-platform tool for a repository's build, test and");
        builder.AppendLine("cross-host work. Every behaviour is declared in .harness-config/config.json;");
        builder.AppendLine("nothing about a specific repository or toolchain is built into the tool.");
        builder.AppendLine();
        builder.AppendLine("Getting started");
        builder.AppendLine($"  {ToolPackage.Command} verify-git              Check git is present and this is a repository");
        builder.AppendLine($"  {ToolPackage.Command} init                    Create .harness-config and seed config.json");
        builder.AppendLine($"  {ToolPackage.Command} legs                    Show where each leg can run, or why it cannot");
        builder.AppendLine($"  {ToolPackage.Command} install-missing-tools   Install what each leg's host is missing");
        builder.AppendLine($"  {ToolPackage.Command} sync                    Put each host's copy in step with this tree");
        builder.AppendLine($"  {ToolPackage.Command} build                   Build every selected leg");
        builder.AppendLine($"  {ToolPackage.Command} test                    Build and test every selected leg");
        builder.AppendLine($"  {ToolPackage.Command} run <runner>            Run a predefined runner across its legs");
        builder.AppendLine($"  {ToolPackage.Command} clean                   Remove selected legs' build directories where they run");
        builder.AppendLine($"  {ToolPackage.Command} list-worktree           Show worktrees and the copies hosts keep of them");
        builder.AppendLine($"  {ToolPackage.Command} list-orchestrator       Show orchestrators and where each agent stands");
        builder.AppendLine($"  {ToolPackage.Command} read-anchors            List the deferred work recorded as anchors");
        builder.AppendLine();
        builder.AppendLine("Every command accepts");
        builder.AppendLine("  -C, --directory <dir>              Operate on this directory (default: current)");
        builder.AppendLine("  -v, --verbose                      Show per-phase detail and child process output");
        builder.AppendLine();
        builder.AppendLine("Topics");

        foreach (var topic in Topics)
        {
            builder.AppendLine($"  {ToolPackage.Command} help {topic.Name,-19}{topic.Summary}");
        }
        builder.AppendLine();
        builder.AppendLine($"Use '{ToolPackage.Command} <command> --help' for a command's own options.");

        return builder.ToString();
    }

    private static string RenderExitCodes()
    {
        var builder = new StringBuilder();

        builder.AppendLine("Exit codes");
        builder.AppendLine();
        builder.AppendLine("Zero always means success. A command that failed and a harness that could not");
        builder.AppendLine("run never share a code, because the remedies differ.");
        builder.AppendLine();
        builder.AppendLine("Shared by every command:");

        foreach (var description in HarnessExit.All)
        {
            builder.AppendLine($"  {description.Code,3}  {description.Name,-16} {description.Explanation}");
        }

        builder.AppendLine();
        builder.AppendLine("Codes 1-9 are reserved for a command's own contract:");
        builder.AppendLine();
        builder.AppendLine("  verify-git");

        foreach (var status in Enum.GetValues<VerifyGitStatus>())
        {
            builder.AppendLine($"    {(int)status,3}  {status}");
        }

        builder.AppendLine();
        builder.AppendLine("  read-anchor, read-anchors --lint, check-anchor-balance, check-anchor-citations");
        builder.AppendLine($"    {AnchorExit.Findings,3}  an id was not found, the registries have problems, or the balance did not hold");
        builder.AppendLine();
        builder.AppendLine("  legs");
        builder.AppendLine($"    {LegsExit.Unavailable,3}  a leg named with --legs cannot run, or no selected leg can");
        builder.AppendLine($"    {HarnessExit.InternalError,3}  whether a leg can run was never established, through a defect in this tool");
        builder.AppendLine();
        builder.AppendLine("  install-missing-tools");
        builder.AppendLine($"    {ToolsExit.NotProvisioned,3}  a tool is missing, out of date, or could not be installed");
        builder.AppendLine();
        builder.AppendLine("  check-ci-legs");
        builder.AppendLine($"    {CiExit.LegRed,3}  a leg is red");
        builder.AppendLine($"    {CiExit.MatrixDidNotRun,3}  the matrix did not run, which is never read as every leg passing");
        builder.AppendLine();
        builder.AppendLine("  build, test, run");
        builder.AppendLine($"    {LegExit.InputsMoved,3}  inputs-moved or unmeasured: let the tree settle, then run again");
        builder.AppendLine($"    {LegExit.Contended,3}  contended: wait for the other run");
        builder.AppendLine($"    {LegExit.Unwitnessed,3}  unwitnessed: find out what actually ran");
        builder.AppendLine($"    {LegExit.LogHeld,3}  log-held: find out which run still owns this leg's logs");
        builder.AppendLine($"    {LegExit.NotAdmitted,3}  not-admitted: wait for the heavy legs it names, free memory, or raise the");
        builder.AppendLine("         machine's limits ('help admission')");
        builder.AppendLine();
        builder.AppendLine("host-exec returns the exit code of the command it ran on the host, unchanged, or");
        builder.AppendLine($"{HarnessExit.HostUnavailable} when nothing ran there, or the command never reported how it finished.");

        return builder.ToString();
    }

    private static string RenderCi()
    {
        var builder = new StringBuilder();

        builder.AppendLine("CI legs");
        builder.AppendLine();
        builder.AppendLine("check-ci-legs reads each leg's CI verdict from the forge's job metadata - through gh,");
        builder.AppendLine("for GitHub Actions - and tells a real failure from a leg that ran out of its time");
        builder.AppendLine("budget. It knows no workflow of its own: which jobs are legs, what a leg is called,");
        builder.AppendLine("and which steps build and test it are what config.json's ci section declares, and");
        builder.AppendLine($"until they are, it refuses ({HarnessExit.Refused}) and names what to set.");
        builder.AppendLine();
        builder.AppendLine("""  "ci": {""");
        builder.AppendLine("""    "workflows": [".github/workflows/ci.yml"],""");
        builder.AppendLine("""    "legJobPattern": "^test \\((?<leg>[^,)]+)(?:, (?<budget>[0-9]+)\\))?",""");
        builder.AppendLine("""    "buildStep": "Build", "testStep": "Test",""");
        builder.AppendLine("""    "workflowBudgetPattern": "leg: {leg}, minutes: (?<budget>[0-9]+)",""");
        builder.AppendLine("""    "legBudgetMinutes": 45""");
        builder.AppendLine("  }");
        builder.AppendLine();
        builder.AppendLine("  workflows              the workflow files whose runs are read; left out, every");
        builder.AppendLine("                         .yml and .yaml file directly in .github/workflows");
        builder.AppendLine("  legJobPattern          a .NET regular expression matched against each job's name: a");
        builder.AppendLine("                         job it matches is a leg, named by its 'leg' group, and a");
        builder.AppendLine("                         'budget' group, where it has one, is the leg's budget in");
        builder.AppendLine("                         minutes. GitHub names a matrix job '<job> (<values>)', the");
        builder.AppendLine("                         values in the order the matrix declares them, and cuts a");
        builder.AppendLine("                         long name short: keep what follows the leg's name optional,");
        builder.AppendLine("                         as above, or a leg whose name was cut is not read at all");
        builder.AppendLine("  buildStep, testStep    the steps a leg builds and tests in, by their exact names");
        builder.AppendLine("  workflowBudgetPattern  optional: a .NET regular expression matched against each");
        builder.AppendLine("                         workflow's text, for a leg whose job name gave no budget -");
        builder.AppendLine("                         one the forge cut short - with {leg} standing for the leg's");
        builder.AppendLine("                         name and a 'budget' group for the minutes");
        builder.AppendLine("  legBudgetMinutes       optional: the budget of a leg nothing else gives one");
        builder.AppendLine();
        builder.AppendLine("A leg's budget comes from its job's name, then its workflow, then legBudgetMinutes. A");
        builder.AppendLine("test step that failed at or past its budget is a possible overrun, whose budget is");
        builder.AppendLine("to be re-derived; one that failed before reaching it is a real failure, to be fixed;");
        builder.AppendLine("and one with no budget to measure it against is called neither, and counted apart.");
        builder.AppendLine($"A green leg past {CiLegsService.WarningFraction * 100:0}% of its budget is warned about. A pattern that does not");
        builder.AppendLine("compile, or lacks its group, is refused when the file is read; one that runs out of");
        builder.AppendLine("time, or a budget group that captures anything but a whole number of minutes, is");
        builder.AppendLine($"refused ({HarnessExit.ConfigInvalid}) when it is matched. Neither is ever read as no match.");
        builder.AppendLine();
        builder.AppendLine($"It exits {CiExit.LegRed} when a leg is red, and {CiExit.MatrixDidNotRun} when no job is a leg - the matrix did not run,");
        builder.AppendLine("or legJobPattern matches none of its jobs - which is never read as every leg passing.");

        return builder.ToString();
    }

    private static string RenderLegs()
    {
        var builder = new StringBuilder();

        builder.AppendLine("Legs and hosts");
        builder.AppendLine();
        builder.AppendLine("A leg says what it needs, never where it runs:");
        builder.AppendLine($"  os          {string.Join(", ", PlatformNames.OperatingSystems)}");
        builder.AppendLine($"  processor   {string.Join(", ", PlatformNames.Processors)}");
        builder.AppendLine("  emulator    optional: the emulator it runs through on a host with another");
        builder.AppendLine("              processor; a leg naming one never runs natively, and a leg naming");
        builder.AppendLine("              none runs only natively");
        builder.AppendLine();
        builder.AppendLine("A toolchain declares the platforms it exists on, and a leg naming one absent from");
        builder.AppendLine("its own operating system is refused when config.json is read - not skipped later as");
        builder.AppendLine("a missing compiler. Nothing about that needs measuring: a leg says which system it");
        builder.AppendLine("needs, and it only ever runs on a host that provides it.");
        builder.AppendLine();
        builder.AppendLine("A toolchain CMake builds with also names its compiler - CC or CXX under env,");
        builder.AppendLine("CMAKE_C_COMPILER, CMAKE_CXX_COMPILER or CMAKE_TOOLCHAIN_FILE under cacheVars, or the");
        builder.AppendLine("compilerId below - and one naming none is refused the same way: CMake would take");
        builder.AppendLine("whatever compiler it found first. One only .NET or Dart builds with names none. A");
        builder.AppendLine("build directory is then held to the compiler it was configured with by the file that");
        builder.AppendLine("compiler starts on the build's own PATH, and by its name only where that PATH holds");
        builder.AppendLine("no such program, so a second gcc earlier on the PATH is refused rather than mixed");
        builder.AppendLine("into the objects of the first.");
        builder.AppendLine();
        builder.AppendLine("Every CMake configure is asked which compilers it resolved - its file API's");
        builder.AppendLine("toolchains-v1 answer, which CMake 3.20 and later write - and each leg's line names");
        builder.AppendLine("them, 'compiler: MSVC 19.51.36231 (C, CXX)', whatever its verdict, and as compilers in");
        builder.AppendLine("--json. A toolchain may hold them to what it means:");
        builder.AppendLine();
        builder.AppendLine("  \"compilerId\": { \"C\": \"MSVC\", \"CXX\": \"MSVC\" }   CMake's own ids, by language");
        builder.AppendLine();
        builder.AppendLine("A build CMake configured with another compiler fails before anything is built with");
        builder.AppendLine("it; one CMake identified no compiler for is unwitnessed, saying why. A language only a");
        builder.AppendLine("subproject enables - C, where a C++ project fetches googletest - comes with no id in");
        builder.AppendLine("CMake's answer, and is identified from CMake's own record of it, the one enabling it");
        builder.AppendLine("loaded: CMakeFiles/<version>/CMake<language>Compiler.cmake - where the record names");
        builder.AppendLine("the compiler the answer names and is no newer than the answer, since a configure that");
        builder.AppendLine("identified the compiler again and then failed leaves a record of one nothing built with.");
        builder.AppendLine("test --no-build holds the directory it tests to it the same way. A configure that");
        builder.AppendLine("fails names no compiler, never the one an earlier configure resolved.");
        builder.AppendLine();
        builder.AppendLine("A toolchain may name the developer environment its legs start in, declared once");
        builder.AppendLine("under developerEnvironments:");
        builder.AppendLine();
        builder.AppendLine("  \"developerEnvironments\": { \"visualStudio\": { \"kind\": \"visualStudio\" } }");
        builder.AppendLine("  \"toolchains\": { \"msvc\": { ..., \"developerEnvironment\": \"visualStudio\" } }");
        builder.AppendLine();
        builder.AppendLine("Every host a leg might land on is asked whether it can set it up - for Visual");
        builder.AppendLine("Studio, whether its installer's vswhere names an instance with requiresComponent,");
        builder.AppendLine("the C++ build tools by default - and a host without one turns the leg away as a");
        builder.AppendLine("tool missing, saying why; one that could not look, as unavailable. On the host that");
        builder.AppendLine("runs the leg, the instance its survey found has its vcvarsall.bat run for the leg's");
        builder.AppendLine("processor, cross-compiling where the host's differs, and what it set - cl, link and");
        builder.AppendLine("the Windows SDK on PATH, INCLUDE, LIB - is what every process of the leg starts");
        builder.AppendLine("with, so cl builds from a plain shell. One that fails, reports an [ERROR, or sets up");
        builder.AppendLine("another processor fails the leg before anything of it starts. The programs the leg");
        builder.AppendLine("starts are then looked for on the PATH it set up, and one missing there skips the");
        builder.AppendLine("leg as a tool missing, named, before anything of it starts. Each leg's line names it:");
        builder.AppendLine("'developer environment: visualStudio (Visual Studio 18.0.11205.157, MSVC 14.50.35717,");
        builder.AppendLine("amd64)', and developerEnvironment in --json. A copy starts nothing there, and needs none.");
        builder.AppendLine();
        builder.AppendLine("Selected legs run at once and the command waits for all of them, reporting each");
        builder.AppendLine("live. Legs are chunked by the PHYSICAL machine they run on: this machine and every");
        builder.AppendLine("WSL distribution are one machine, because a distribution runs on it, and each ssh");
        builder.AppendLine("host is its own. defaults.maxParallelLegs caps how many run at once on any one");
        builder.AppendLine("machine, so a busy laptop is not asked for more than it has while the remote hosts");
        builder.AppendLine("sit idle; defaults.maxParallelLegsTotal caps the whole fleet on top of that, for");
        builder.AppendLine("what it shares even when its machines do not - a license server, a network share.");
        builder.AppendLine("Both are counted by one command; where a machine declares admission, its heavy legs");
        builder.AppendLine("take its slots across every command this user runs there ('help admission').");
        builder.AppendLine("Every line says which leg it came from, and a child's own output under --verbose");
        builder.AppendLine("is tagged '<leg>/<phase>:', so several hosts building at once stay readable.");
        builder.AppendLine();
        builder.AppendLine("Hosts are this machine, the WSL distributions under hosts.wsl and the ssh hosts");
        builder.AppendLine("under hosts.ssh. Before anything runs, hosts are measured, never assumed: their");
        builder.AppendLine("operating system, their processor, and whether each emulator the legs use works");
        builder.AppendLine("there. This machine is measured first, and other hosts only for the legs it cannot");
        builder.AppendLine("take. A leg runs on the first host that can take it - its operating system, its");
        builder.AppendLine("processor, and its emulator where it names one: this machine, then the WSL");
        builder.AppendLine("distributions, for a Linux leg only, then the ssh hosts, each in the order the");
        builder.AppendLine("configuration declares them - and is turned away there when that host lacks a");
        builder.AppendLine("program its command starts. A leg that sets \"wsl\" or \"ssh\" runs on that host and");
        builder.AppendLine("nowhere else.");
        builder.AppendLine();
        builder.AppendLine($"  {ToolPackage.Command} legs                          every declared leg");
        builder.AppendLine($"  {ToolPackage.Command} legs --legs a,b gate          legs a and b, and the legs of set gate");
        builder.AppendLine($"  {ToolPackage.Command} host-exec --ssh vps -- verify-git");
        builder.AppendLine($"  {ToolPackage.Command} host-exec --wsl -- list-worktree");
        builder.AppendLine();
        builder.AppendLine("Every cmake build also has its dependency records read, with 'ninja -t deps'.");
        builder.AppendLine("An object that recorded no header dependencies is never rebuilt when a header it");
        builder.AppendLine("includes changes, so the next build links yesterday's object and reports success.");
        builder.AppendLine("Only an object built under deps = msvc can record none legitimately: cl reports");
        builder.AppendLine("headers, never the source, and ninja drops the ones under 'program files' or");
        builder.AppendLine("'microsoft visual studio' as the system's, and a unit built from a precompiled");
        builder.AppendLine("header under /Yu never reports a header the precompiled header holds and guards.");
        builder.AppendLine("Such a zero is excused only where ninja rebuilds the object all the same for every");
        builder.AppendLine("header it surely includes - what its command force-includes with /FI, and what its");
        builder.AppendLine("source and those headers include by a quoted include beside them that ninja keeps,");
        builder.AppendLine("outside every comment, in no #if block its language may not compile: through its");
        builder.AppendLine("build line's inputs, and what built them recorded, as a unit built from a");
        builder.AppendLine("precompiled header is through the object compiling it. Another object's record");
        builder.AppendLine("never excuses it: an older build, or a compiler cache, may have written that one.");
        builder.AppendLine("How each object is built is read as ninja reads it, across the files build.ninja");
        builder.AppendLine("includes - CMake keeps its rules in CMakeFiles/rules.ninja - with its escapes");
        builder.AppendLine("undone and its variables evaluated, so its command is the one cl was given.");
        builder.AppendLine("A build whose records cannot be read is 'unmeasured' rather than passed, because a");
        builder.AppendLine("check that did not run has established nothing. A build directory that is not");
        builder.AppendLine("ninja's is skipped and says so.");
        builder.AppendLine();
        builder.AppendLine("A leg no host can run is a warning that names it and says why; the other legs");
        builder.AppendLine("still go ahead. The check fails when a leg named with --legs cannot run, or when");
        builder.AppendLine("no selected leg can - and, with exit 70, when whether a leg can run was never");
        builder.AppendLine("established, through a defect in this tool, named or not. --legs given without a");
        builder.AppendLine("name is refused, not taken for every leg.");
        builder.AppendLine();
        builder.AppendLine("A WSL distribution is available when this machine runs Windows, wsl.exe exists,");
        builder.AppendLine("and a program starts in the distribution; --wsl with no name is WSL's default.");
        builder.AppendLine("An ssh host is available when .harness-config/sshItems/<name>/ holds its connection");
        builder.AppendLine("data, named under sshItems and keyed the same way by hosts.ssh, ssh connects in batch");
        builder.AppendLine("mode, so without ever waiting at a prompt, and DssHarness runs there. An .env other");
        builder.AppendLine("users can change is refused, since whoever can change it can send the harness");
        builder.AppendLine("elsewhere, and a key they can read, ssh ignores; both are checked before connecting.");
        builder.AppendLine($"Run '{ToolPackage.Command} help secrets' for the layout.");
        builder.AppendLine();
        builder.AppendLine("The ssh that runs is the first on the PATH. It is asked first what it would do, with");
        builder.AppendLine("ssh -G, and the name it would look up - the address declared, or a HostName its own");
        builder.AppendLine("configuration gives it - is looked up here, retrying; a name that does not resolve is");
        builder.AppendLine("refused before ssh starts. Every call the run makes is then given the address it");
        builder.AppendLine("resolved to as ssh's HostName, with the host's key still checked under the name, not");
        builder.AppendLine("the address. Each still goes to the address declared, so every Host block written for");
        builder.AppendLine("it applies. So a name that answers only now and then - a Mac in a dark wake - or an");
        builder.AppendLine("ssh that looks names up by other means - Git for Windows' own resolves no mDNS .local");
        builder.AppendLine("name - fails no run part way while that address keeps answering. Nothing is pinned");
        builder.AppendLine("where ssh reaches the host through a ProxyJump or a ProxyCommand, which do their own");
        builder.AppendLine("lookup, or where the address would change anything else ssh does; and an address that");
        builder.AppendLine("stops taking the connection, or shows a key the name is not known by, is dropped, and");
        builder.AppendLine("ssh looks the name up itself.");
        builder.AppendLine();
        builder.AppendLine("A host that sleeps between commands can be given hosts.ssh.<name>.wakeWaitSeconds.");
        builder.AppendLine($"Its name is then looked up again, and a connection nothing took or that timed out");
        builder.AppendLine($"tried again, every {SshWakeWindow.DefaultPollDelay.TotalSeconds:0} seconds until that many seconds have passed, before its");
        builder.AppendLine("legs are skipped; a key or a login the host refuses is never waited on. Reached,");
        builder.AppendLine("its report says how long it took to wake; not reached, its reason names the window,");
        builder.AppendLine("and for the half minute a name's answer is kept the command is refused it at once");
        builder.AppendLine("rather than waiting again. Left at 0, a host is looked up three times within a second");
        builder.AppendLine("and never waited for. Never for a host that is simply off, which would then cost the");
        builder.AppendLine("window on every command.");
        builder.AppendLine();
        builder.AppendLine("Every WSL distribution and ssh host runs DssHarness itself, installed as a global");
        builder.AppendLine($".NET tool from nuget.org, so it needs the .NET {ToolPackage.MinimumSdkMajor} SDK. It must be this machine's build:");
        builder.AppendLine("a host that is behind is installed or updated to this version, never downgraded,");
        builder.AppendLine($"and a host that is ahead stops everything ({HarnessExit.Refused}) until this machine is updated. The");
        builder.AppendLine("version and a hash of the tool's own assembly are both compared, because a build");
        builder.AppendLine("from source reports the same version as the published package.");
        builder.AppendLine();
        builder.AppendLine("The DssHarness on a host is reached through a hidden host-agent command, with the");
        builder.AppendLine("request on standard input, held open while the host works: interrupting host-exec");
        builder.AppendLine("ends it, and the host cancels the command. The command line ssh hands a remote");
        builder.AppendLine("shell holds only fixed words, so no argument is ever reinterpreted by sh, cmd or");
        builder.AppendLine("PowerShell. host-exec runs in the host's copy of the tree it is typed in, which");
        builder.AppendLine($"'{ToolPackage.Command} sync' creates and keeps in step with that tree.");
        builder.AppendLine();
        builder.AppendLine("A host answering this machine writes its home as ~: in every line of DssHarness's");
        builder.AppendLine("own - a leg's reason, a lock or free-space message, a failure quoting git or the");
        builder.AppendLine("system - and in the paths its ledger holds, runDirectory and space among them, so");
        builder.AppendLine("this machine's 'logs of <leg> on <host>:' line names no account either. On a Windows");
        builder.AppendLine("host the separator after it stays the host's own, as in ~\\src\\app. A program's own");
        builder.AppendLine("lines, a phase's last lines among them, stay as it printed them; a command typed on");
        builder.AppendLine("the host names its paths in full; and under host-exec a document other than a");
        builder.AppendLine("ledger is written as the host prints it. A copy's path this machine builds from");
        builder.AppendLine("repositoryPath is said as that is written: '~/src/app' keeps the account out of it.");
        builder.AppendLine();
        builder.AppendLine("A host keeps a copy of each tree whose legs it runs: the main checkout's at its");
        builder.AppendLine($"repositoryPath, and each worktree's beside it, at <repositoryPath>{HostCopies.WorktreeSuffix}<name>,");
        builder.AppendLine("named for the worktree's directory. So worktrees do not share a copy on a host, or");
        builder.AppendLine("the lock on it, and their legs there run side by side; each worktree's first sync to");
        builder.AppendLine("a host carries its whole tree. A worktree made elsewhere, under the name of one that");
        builder.AppendLine("has a copy, is refused that copy while the other exists. This machine records which");
        builder.AppendLine($"hosts hold a copy of which worktree, in {HarnessLayout.HostCopiesDirectoryRelative} in the main");
        builder.AppendLine("checkout, and delete-worktree asks each of them to remove it.");
        builder.AppendLine();
        builder.AppendLine("An emulator declares the hosts it runs on (hostOs, hostProcessor), the processor it");
        builder.AppendLine("runs programs for, the launcher placed in front of each program (such as");
        builder.AppendLine("qemu-aarch64 -L <sysroot>, or arch -x86_64; none for Prism and binfmt), what it");
        builder.AppendLine("requires, the phases it runs (test by default), and a witness: a program run");
        builder.AppendLine("through it whose output must match a pattern, proving it really runs programs for");
        builder.AppendLine("that processor. A launcher and a required file are each a program name, looked up");
        builder.AppendLine("on the host's PATH, or an absolute path. So is a witness with no launcher; behind a");
        builder.AppendLine("launcher a witness is an absolute path, since the launcher finds it, not the PATH.");
        builder.AppendLine();
        builder.AppendLine("legs and host-exec run what config.json declares: legs runs the witness of each");
        builder.AppendLine("emulator the selected legs use, and both run DssHarness itself on WSL distributions");
        builder.AppendLine("and ssh hosts, which they install or update there from nuget.org, and from nuget.org");
        builder.AppendLine("only. That is the trust building the repository already asks for. An ssh host is");
        builder.AppendLine("reached only when the main checkout holds its directory under sshItems, so a");
        builder.AppendLine("config.json arriving through git cannot point the harness at a machine nobody set up.");
        builder.AppendLine();
        builder.AppendLine("A host's own copy of the tree holds none of that connection data: it is gitignored, so");
        builder.AppendLine("a sync never carries it, and a host keeps no key of its own. A command typed in a copy");
        builder.AppendLine("therefore reaches no other machine, and a leg naming a host is placed by the machine");
        builder.AppendLine("that syncs to that host and dispatches the work there. Typed in a copy, a command");
        builder.AppendLine("refused any host says so, and a sync that reaches none of the hosts its legs need");
        builder.AppendLine($"fails, exit {HarnessExit.HostUnavailable}, rather than reporting that no host needed a copy.");
        builder.AppendLine();
        builder.AppendLine("Wherever it is typed, a sync fails as legs does when a leg named with --legs could not be");
        builder.AppendLine("placed on any host, or no selected leg could be: after putting in step every host it");
        builder.AppendLine("reached, it names the legs no copy was made for.");
        builder.AppendLine();
        builder.AppendLine($"A host's {ToolPackage.Id} is brought to its dispatcher's version when that machine next");
        builder.AppendLine("reaches it, and not before, so a command typed in a copy in between runs the old build.");
        builder.AppendLine("Typed there, a command asks nuget.org which listed release is newest - an unlisted one");
        builder.AppendLine("is withdrawn, and no dispatcher is brought to it - and warns when the build running is");
        builder.AppendLine("older; it waits a few seconds at most, and says nothing when nuget.org does not answer.");
        builder.AppendLine("A command the host agent runs for another machine asks nothing.");
        builder.AppendLine();
        builder.AppendLine("Exit codes");
        builder.AppendLine($"  {HarnessExit.Success,3}  legs: every named leg can run and every host answered");
        builder.AppendLine($"  {HarnessExit.Incomplete,3}  legs: the legs that answered can run, and a host did not answer");
        builder.AppendLine($"  {LegsExit.Unavailable,3}  legs, sync: a leg named with --legs cannot run, or no selected leg can");
        builder.AppendLine($"  {HarnessExit.InternalError,3}  legs, sync: whether a leg can run was never established, through a defect in this tool");
        builder.AppendLine($"  {HarnessExit.UsageError,3}  --legs names something that is neither a leg nor a leg set, or no name at all");
        builder.AppendLine($"  {HarnessExit.Refused,3}  a host runs a newer {ToolPackage.Id} than this machine");
        builder.AppendLine($"  {HarnessExit.HostUnavailable,3}  host-exec: the host cannot run {ToolPackage.Id}, has no copy of the tree it is typed in,");
        builder.AppendLine("       or the command never reported how it finished, so it may have run only in part");
        builder.AppendLine("       host-exec otherwise returns the exit code of the command it ran");

        return builder.ToString();
    }

    private static string RenderSpace()
    {
        var builder = new StringBuilder();

        builder.AppendLine("Disk space");
        builder.AppendLine();
        builder.AppendLine("clean removes each selected leg's build directory where the leg runs: in this");
        builder.AppendLine("machine's tree, or in a WSL distribution's or an ssh host's copy of the tree it is");
        builder.AppendLine("typed in.");
        builder.AppendLine();
        builder.AppendLine($"  {ToolPackage.Command} clean --legs linux-arm64-debug,linux-arm64-release");
        builder.AppendLine($"  {ToolPackage.Command} clean --legs linux-arm64-debug --dry-run");
        builder.AppendLine();
        builder.AppendLine("It writes nothing where it removes before it has removed - no sync, no lock entry,");
        builder.AppendLine("no run records - so it frees a disk a build filled. A leg a run is building is");
        builder.AppendLine("refused-locked, naming the run: the lock file is read, never written. The directory");
        builder.AppendLine("is renamed aside, then removed, so a build started meanwhile starts in a new one; what");
        builder.AppendLine("an interrupted removal left aside, the next clean of that leg removes. A build");
        builder.AppendLine("directory that is a link is left alone: what it holds is wherever it points. Each");
        builder.AppendLine("leg's line says what was removed and the room left on its filesystem; --dry-run says");
        builder.AppendLine("what each holds and removes nothing; --json carries both as each leg's 'space'.");
        builder.AppendLine();
        builder.AppendLine("A host whose DssHarness is older than this machine's is updated first, as for any");
        builder.AppendLine("command, and the update needs room: a host that is both full and behind has to be");
        builder.AppendLine("freed by hand once.");
        builder.AppendLine();
        builder.AppendLine("A leg is placed only where its host has the room its build still needs, as it is");
        builder.AppendLine("only where the programs it starts are: a build that fills a disk dies half way, and");
        builder.AppendLine("takes any other leg building there with it. What a build needs is what its");
        builder.AppendLine("directory comes to once built - the leg's buildSpaceGiB, or, left out, what a build");
        builder.AppendLine("of its variant recorded as it finished: in this tree's copy, or else in the main");
        builder.AppendLine("checkout's copy on that host - less what the directory already holds. Legs building");
        builder.AppendLine("on one filesystem of a host are counted together, in the order they were selected,");
        builder.AppendLine("since every build directory stays once built; one that does not fit beside those");
        builder.AppendLine("before it is skipped-unavailable:");
        builder.AppendLine();
        builder.AppendLine("  ssh vps: 3.2 GiB free on '/', and this leg needs ~8 GiB, what the main");
        builder.AppendLine("  checkout's copy of the same variant came to there");
        builder.AppendLine();
        builder.AppendLine("A leg nothing has measured that declares no buildSpaceGiB is placed as it always");
        builder.AppendLine("was, and so is one whose directory no build of this version recorded. Nothing is");
        builder.AppendLine("walked to decide: the room is the filesystem's own count, and what a directory holds");
        builder.AppendLine("is what its build recorded. Commands that build nothing - sync, clean - need no room.");
        builder.AppendLine();
        builder.AppendLine("A WSL distribution's disk is a file that grows on a drive of this machine, whatever");
        builder.AppendLine("room the distribution measures for itself - a terabyte, by default. A WSL leg needs");
        builder.AppendLine("its room on that drive too, which this machine's own legs on the drive fill as well.");
        builder.AppendLine();
        builder.AppendLine($"'{ToolPackage.Command} legs -v' says the room on each host it measured - where its copies are");
        builder.AppendLine("kept, and the main checkout for this machine, with the drive a WSL distribution's");
        builder.AppendLine("disk grows on - so a host that is nearly full shows before a run fills it; --json");
        builder.AppendLine("always carries it, as each host's 'space' and a WSL distribution's 'diskImageSpace'.");

        return builder.ToString();
    }

    private static string RenderWorktrees()
    {
        var builder = new StringBuilder();

        builder.AppendLine("Worktrees");
        builder.AppendLine();
        builder.AppendLine("Names are lowercase letters and digits joined by single hyphens, at most");
        builder.AppendLine($"{WorktreeSettings.DefaultMaxNameLength} characters unless worktrees.maxNameLength says otherwise.");
        builder.AppendLine("Use --random to have one generated.");
        builder.AppendLine();
        builder.AppendLine("The length limit is not cosmetic. A worktree's build tree sits below");
        builder.AppendLine("  <worktrees.root>/<name>");
        builder.AppendLine($"which is '{WorktreeSettings.SeededRoot}' in a configuration init writes, and");
        builder.AppendLine($"'{WorktreeSettings.DefaultRoot}' in one that names none. That root is configurable");
        builder.AppendLine("precisely because it is spent before a worktree's own name: a shorter one buys");
        builder.AppendLine("those characters back. Ask list-worktree where a worktree actually is rather than");
        builder.AppendLine("assuming either.");
        builder.AppendLine("On Windows that path, plus build/<variant> for the longest variant this machine");
        builder.AppendLine("builds, plus the longest path the build system generates below that, must stay");
        builder.AppendLine($"under {HostPlatform.WindowsMaxPath} characters. Exceeding it does not fail as a path error: it appears");
        builder.AppendLine("as compile errors in files the worktree never touched.");
        builder.AppendLine();
        builder.AppendLine("create-worktree refuses up front when the budget cannot be met, and says how");
        builder.AppendLine("long a name would still fit. Both sides of the budget are in config.json:");
        builder.AppendLine("  worktrees.pathBudgetReserve   the longest path your build generates below its");
        builder.AppendLine("                                own build directory, relative to it; the check adds");
        builder.AppendLine("                                build/<variant> and every separator itself, and");
        builder.AppendLine("                                every build warns when it went deeper");
        builder.AppendLine("  worktrees.pathLimit           replaces the platform limit; set it only when");
        builder.AppendLine("                                every tool in the build handles long paths");
        builder.AppendLine();
        builder.AppendLine("A Ninja build leaves out of that warning the outputs ninja says no target of it");
        builder.AppendLine("produces any more - those of a target renamed or removed - since a new worktree's");
        builder.AppendLine("build, starting from clean, never holds them: it notes them instead, where one is");
        builder.AppendLine("deeper than the reserve, naming 'ninja -t cleandead', which removes them. The");
        builder.AppendLine("harness removes nothing. Another generator, or a ninja before 1.10, is measured as");
        builder.AppendLine("before.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "An orchestrator's agents' worktrees sit below the directory named for it, <worktrees.root>/<orchestrator>/<agent>, "
            + "made by create-agent with its records, never by create-worktree. list-worktree lists each as orchestrator/agent, "
            + "and delete-worktree takes that address. The directory named for an orchestrator is never deleted as one while "
            + $"worktrees are below it, --force or not ({HarnessExit.Refused}), nor any directory with no .git of its own while git "
            + $"cannot list its worktrees ({HarnessExit.CommandFailed}); a worktree's own submodules are never taken for worktrees. "
            + "A plain worktree cannot take an orchestrator's name. An agent's copies on hosts are kept under "
            + "orchestrator--agent, so two orchestrators' agents of one name keep theirs apart.");
        builder.AppendLine();
        builder.AppendLine("Worktrees always belong to the main checkout, so running create-worktree from");
        builder.AppendLine("inside a worktree adds a sibling rather than nesting one.");
        builder.AppendLine();
        builder.AppendLine("delete-worktree removes a worktree, everything under it, and git's record of it.");
        builder.AppendLine($"Without --force it checks first, and deletes nothing and exits {HarnessExit.Refused} when the");
        builder.AppendLine("worktree has uncommitted changes: a modified, staged or untracked file git does");
        builder.AppendLine("not ignore, a changed submodule, or an edit hidden by assume-unchanged or");
        builder.AppendLine("skip-worktree. It refuses too when commits on its HEAD are on no branch, tag,");
        builder.AppendLine("remote-tracking ref, newest stash or other worktree's HEAD; when a submodule");
        builder.AppendLine("repository deleted with it, checked out or not, holds a commit no");
        builder.AppendLine("remote-tracking ref or tag contains, or a stash; when it is locked; when git's");
        builder.AppendLine("record of it names another directory or none, as after moving it by hand; and");
        builder.AppendLine("when git does not see it as a worktree of this repository. The refusal names");
        builder.AppendLine("everything it found, on one line. A tag made inside a submodule counts as kept,");
        builder.AppendLine("and is lost with the submodule's repository.");
        builder.AppendLine();
        builder.AppendLine("Without --force, when the check cannot be finished, because git cannot answer, a");
        builder.AppendLine("record cannot be read or its directory found, or, on Windows, a directory in it");
        builder.AppendLine($"cannot be looked through for what holds it, nothing is deleted and it exits {HarnessExit.CommandFailed}.");
        builder.AppendLine("Ignored files outside a declared evidenceRoots directory are deleted unchecked,");
        builder.AppendLine("even ones no build makes again, such as .env, and so are ignored directories");
        builder.AppendLine("with everything in them, the history of a repository nested inside one and the");
        builder.AppendLine("records of every run started in the worktree included.");
        builder.AppendLine("A worktree whose evidenceRoots directory holds anything is refused;");
        builder.AppendLine("--delete-evidence waives that one check and nothing else, and so does");
        builder.AppendLine("--discard-uncommitted for uncommitted changes, which it deletes with the");
        builder.AppendLine("worktree, saying how many and naming a few. --force skips every check and");
        builder.AppendLine("overrides a lock; whatever the worktree held is lost.");
        builder.AppendLine();
        builder.AppendLine($"On Windows, without --force, a worktree something holds part of is refused ({HarnessExit.Refused})");
        builder.AppendLine("whole once every check has passed, with nothing removed, naming what is held: a");
        builder.AppendLine("directory that is a process's current directory, this command's own among them,");
        builder.AppendLine("a file open without sharing its deletion, or one a program is running from. git's");
        builder.AppendLine("removal would stop there part way, with the worktree's .git file and git's record");
        builder.AppendLine("of it already gone. Close what holds it and run delete-worktree again, from outside");
        builder.AppendLine("the worktree where it is this command's own. A watcher on a directory, as an editor");
        builder.AppendLine("keeps, holds nothing; --force looks for nothing, and goes as far as it can.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "On Windows, each directory junction in the worktree is removed first, as a link, never what it leads to: "
            + "git leaves every junction, and the directories above it, while reporting the worktree removed. One that "
            + $"cannot be removed stops the deletion before git runs ({HarnessExit.CommandFailed}), and a volume mounted "
            + "on a directory is never unmounted. A directory under the root with no .git and no record of git's - "
            + $"what a removal that stopped part way leaves - is refused ({HarnessExit.Refused}), naming --force; git "
            + "worktree repair is named only while git still records a worktree there.");
        builder.AppendLine();
        builder.AppendLine("An interruption during the deletion can leave it partly done, on any platform;");
        builder.AppendLine("running delete-worktree again with --force finishes it.");
        builder.AppendLine();
        builder.AppendLine("A worktree synced to a host has a copy there of its own, beside the main checkout's,");
        builder.AppendLine("and deleting the worktree asks each host that holds one to remove it, where the");
        builder.AppendLine("harness made it: a copy it took over with --adopt, or a directory with no mark of");
        builder.AppendLine("the harness's, is left where it is, and said to be. A copy is removed under the lock");
        builder.AppendLine("a leg this machine runs there takes, and its marker goes last, so a removal that");
        builder.AppendLine("stops part way leaves the rest still the harness's to remove. A host is reached");
        builder.AppendLine("through the worktree's own configuration, read before it goes, or else through the");
        builder.AppendLine("one the command runs in; one neither declares is not asked, and its copy is");
        builder.AppendLine("forgotten, and named.");
        builder.AppendLine("A copy that cannot be removed now stays recorded, and the worktree is deleted all the");
        builder.AppendLine("same, but the command fails, naming each, with the highest code among them:");
        builder.AppendLine($"{HarnessExit.Refused} where a run holds one or it was refused, {HarnessExit.HostUnavailable} where its host cannot be reached,");
        builder.AppendLine($"{HarnessExit.CommandFailed} where removing it failed there; or {HarnessExit.Cancelled} when interrupted. Running delete-worktree");
        builder.AppendLine("again removes what it left. For a name whose worktree is gone, it removes what any");
        builder.AppendLine("worktree of that name left, and never the copies of one that still exists.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"{ListWorktreeCommand.Name} lists each worktree with the copies this machine records hosts keeping of it, and the "
            + "copies left by worktrees that are gone - removed by hand, or by another tool - each with the "
            + $"{DeleteWorktreeCommand.Name} that deals with them. With --hosts it also asks each declared host which worktree "
            + "copies it keeps beside its repositoryPath, and how large each is, and sets them against that record: "
            + "a copy of a worktree here, one left by a worktree that is gone, one the record does not hold - made "
            + "from another checkout or machine, or forgotten here, which deleting a worktree never reaches - and one "
            + "recorded that is not there. A host that cannot be asked is named, and the command fails with the "
            + $"highest code among them, {HarnessExit.HostUnavailable} where a host cannot be reached. --json prints the "
            + "listing as one JSON document.");

        return builder.ToString();
    }

    private static string RenderAnchors()
    {
        var defaults = new AnchorSettings();
        var rules = AnchorIdRules.From(defaults);
        var builder = new StringBuilder();

        builder.AppendLine("Anchors");
        builder.AppendLine();
        builder.AppendLine("An anchor is a named piece of deferred work, kept as one row of a markdown table.");
        builder.AppendLine("Two registries hold every anchor, and each anchor lives in exactly one of them:");
        builder.AppendLine();
        builder.AppendLine($"  pending   anchors.pendingAnchorsPath (default {defaults.PendingAnchorsPath})");
        builder.AppendLine("            open, gated and disclosed anchors: the work that is left");
        builder.AppendLine($"  done      anchors.doneAnchorsPath (default {defaults.DoneAnchorsPath})");
        builder.AppendLine("            closed anchors: the archive");
        builder.AppendLine();
        builder.AppendLine("init creates each registry that is missing, from a skeleton holding an");
        builder.AppendLine("introduction and an empty table, and never touches one that exists. Every");
        builder.AppendLine("command, init included, finds a registry the same way: one git tracks is in the");
        builder.AppendLine("tree the command runs in, so a change travels with that branch, and one git");
        builder.AppendLine("ignores is in the main checkout.");
        builder.AppendLine();
        builder.AppendLine("Statuses. The Status cell is the only verdict that decides whether a row is closed:");
        builder.AppendLine($"  {AnchorStatus.Render(AnchorState.Open)}       live work that can be picked up now");
        builder.AppendLine($"  {AnchorStatus.Render(AnchorState.Gated)}      live work waiting on a trigger; when it fires, set it to open");
        builder.AppendLine($"  {AnchorStatus.Render(AnchorState.Disclosed)}  live debt that existed before anyone wrote it down");
        builder.AppendLine($"  {AnchorStatus.Render(AnchorState.Closed)}     finished");
        builder.AppendLine();
        builder.AppendLine("A row is closed exactly when its Status cell starts with the closed mark. Closing");
        builder.AppendLine("an anchor moves its row to the done registry, and any other status moves it back.");
        builder.AppendLine("Nothing is read from the prose beside the Status, unless anchors.triggerCarriesVerdict");
        builder.AppendLine("is true: then a closed row's Trigger opens with the closed mark and no other row's");
        builder.AppendLine("does, write-anchor and set-anchor refuse a row whose two cells disagree, and");
        builder.AppendLine("read-anchors --lint reports one.");
        builder.AppendLine("A row is added at the end of its table: rows are never sorted.");
        builder.AppendLine();
        builder.AppendLine($"Rows: {AnchorRegistryDocument.TableHeader}");
        builder.AppendLine($"  Priority runs from {AnchorPriority.Bands[0]}, the most urgent, to {AnchorPriority.Bands[^1]}.");
        builder.AppendLine("  A new id is anchors.idPrefix, then at least anchors.minimumIdSegments");
        builder.AppendLine($"  hyphen-separated segments, the first in capitals: {rules.Example()} with the");
        builder.AppendLine($"  defaults ({defaults.IdPrefix} and {defaults.MinimumIdSegments}). Ids already in a registry are never re-checked.");
        builder.AppendLine();
        builder.AppendLine("A registry is an introduction and exactly one anchor table. A file with no anchor");
        builder.AppendLine("table, a second one, or an anchor row outside that table is malformed. Every");
        builder.AppendLine("command that reads or changes anchors refuses it rather than risk a miscount;");
        builder.AppendLine("read-anchors --lint shows what to repair.");
        builder.AppendLine();
        builder.AppendLine("Commands");
        builder.AppendLine("  write-anchor ID --priority P --trigger TEXT [--status S] [--closing TEXT]");
        builder.AppendLine("               [--cross-refs TEXT]                  add a new anchor");
        builder.AppendLine("  set-anchor ID [--priority] [--status] [--trigger] [--closing] [--cross-refs]");
        builder.AppendLine("                                                    change an existing anchor");
        builder.AppendLine("  read-anchor ID [ID ...] [--json]                  show anchors in full");
        builder.AppendLine("  read-anchors [--band P ...] [--open|--closed] [--json]");
        builder.AppendLine("                                                    list anchors");
        builder.AppendLine("  read-anchors --lint [--json]                      check both registries");
        builder.AppendLine("  check-anchor-balance [--base REF] [--json]        compare with a commit");
        builder.AppendLine("  check-anchor-citations --current-commit|--current-tree|--current-pr [--json]");
        builder.AppendLine("                                                    check every cited id resolves");
        builder.AppendLine();
        builder.AppendLine("--pending or --done limits set-anchor, read-anchor and a read-anchors listing to");
        builder.AppendLine("one registry; read-anchors --lint always checks both, and takes no filter.");
        builder.AppendLine("write-anchor and set-anchor write immediately; --anchor-dry-run shows the change");
        builder.AppendLine("and writes nothing. Pass values as you mean them: pipes are escaped for you, and a");
        builder.AppendLine("pipe you already escaped is refused. A line break collapses, with the whitespace");
        builder.AppendLine("either side of it, into one space, since a row is one line; every other character,");
        builder.AppendLine("a run of spaces or a tab among them, is kept as given, but for whitespace at the");
        builder.AppendLine("value's very start and end. A --<cell>-file is read as UTF-8: one that is not, or");
        builder.AppendLine("that opens with a byte-order mark, is refused.");
        builder.AppendLine();
        builder.AppendLine($"A value that would be stored cut is refused (exit {HarnessExit.UsageError}): an id cut where a line ends, as");
        builder.AppendLine("check-anchor-citations reads one below; an id with a space after one of its hyphens,");
        builder.AppendLine("typed or left by joined lines, across the segments a citation carries - so a prefix in");
        builder.AppendLine($"prose, such as {defaults.IdPrefix}-, is no cut; a path's directory ending a line before a file's name;");
        builder.AppendLine("and a path with a space after a '/' before a file's name, where it starts at a");
        builder.AppendLine("directory at the top of the tree. A file's name is a word holding '_', '.' or '-' and");
        builder.AppendLine("ending in a letter or a digit. A value that newly cites an id no row of either registry");
        builder.AppendLine($"holds is refused too (exit {HarnessExit.Refused}), a citation read as check-anchor-citations reads one.");
        builder.AppendLine("A cut or a citation the cell already held is history, and is not judged again.");
        builder.AppendLine();
        builder.AppendLine($"check-anchor-balance compares the working tree with --base (default {AnchorBalanceService.DefaultBase}), by id");
        builder.AppendLine("across both registries, so moving a row counts as nothing. It fails when open");
        builder.AppendLine("anchors rose, not counting anchors newly disclosed, and when a closed anchor is in");
        builder.AppendLine("the pending registry, a live one is in the done registry, or a registry is");
        builder.AppendLine("malformed. A registry that did not exist at the base counts as empty there; one git");
        builder.AppendLine("ignores has no history, and is refused.");
        builder.AppendLine();
        builder.AppendLine("check-anchor-citations reads every file under anchors.citationRoots - as HEAD holds");
        builder.AppendLine("it, as the disk holds it, or only what this branch changed - and fails when a cited");
        builder.AppendLine("id resolves to no row in either registry, by the rule read-anchor finds a row by.");
        builder.AppendLine("A citation a wrapped line cut is reported as cut, whatever rows exist, where it runs");
        builder.AppendLine("into a hyphen that ends its line; one cut before it has two segments counts when the");
        builder.AppendLine("next line carries on with what makes it a citation. One ending its line where the");
        builder.AppendLine("next opens with a hyphen is cut where the two lines joined spell a row's id: a line");
        builder.AppendLine("opening with an option such as -Wall, a figure such as (-40, or a removed diff line");
        builder.AppendLine("carries no id on. A break inside a segment, with no hyphen on either side, is cut where");
        builder.AppendLine("the two lines joined spell a row's id; otherwise it cannot be told from a line that");
        builder.AppendLine("simply ends there, and reads as the shorter id, reported unresolved unless that shorter");
        builder.AppendLine("id is a row of its own. An id followed by '*' or '{', or by a hyphen and one of those,");
        builder.AppendLine("names a family of ids and is no citation, but an id in bold is one. Keep every id");
        builder.AppendLine("whole on one line.");
        builder.AppendLine();
        builder.AppendLine("Every change holds a machine-wide lock on its two registries. A change that cannot");
        builder.AppendLine($"take it within {NamedMutexAnchorRegistryLock.DefaultTimeout.TotalSeconds:0} seconds writes nothing and exits {HarnessExit.Refused}.");
        builder.AppendLine();
        builder.AppendLine("Exit codes");
        builder.AppendLine($"  {AnchorExit.Findings,3}  an id was not found, --lint found problems, the balance did not hold,");
        builder.AppendLine("       or a citation resolves to no row or is cut");
        builder.AppendLine($"  {HarnessExit.UsageError,3}  a value is not valid (an id, a priority, a status, an empty trigger, a");
        builder.AppendLine("       cell it would store cut), options that cannot be combined, or set-anchor with nothing");
        builder.AppendLine("       to change");
        builder.AppendLine($"  {HarnessExit.NotInitialized,3}  a registry is missing; run '{ToolPackage.Command} init'");
        builder.AppendLine($"  {HarnessExit.Refused,3}  refused: the id exists, there is no such anchor, an id has two rows,");
        builder.AppendLine("       a value newly cites a row no registry holds,");
        builder.AppendLine("       the registry is ignored by git, the lock is held, anchors.citationRoots");
        builder.AppendLine("       declares no root, or a file in a root is not named in UTF-8");
        builder.AppendLine($"  {HarnessExit.CommandFailed,3}  a registry is malformed, the base commit cannot be read, git lists a");
        builder.AppendLine("       file it cannot read, or the directories at the top of the tree cannot be listed");
        builder.AppendLine();
        builder.AppendLine("read-anchors --lint and check-anchor-balance report a missing or malformed registry");
        builder.AppendLine($"as one of their findings instead, so for them it exits {AnchorExit.Findings}.");

        return builder.ToString();
    }

    private static string RenderOrchestrators()
    {
        var builder = new StringBuilder();
        var command = ToolPackage.Command;

        builder.AppendLine("Orchestrators and their agents");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "An orchestrator is a session that runs agents side by side, each in a worktree of its own. Everything it and its "
            + $"agents keep is in the main checkout, under {HarnessLayout.OrchestratorsDirectoryName}/<orchestrator>/, never in a "
            + "worktree, so another session - on another account, or after this one ends - takes the work over by reading it:");
        builder.AppendLine();
        AppendColumns(
            builder,
            [
                ($"  {OrchestratorLayout.RecordFileName}", "its record: name, model, createdAt, parallel, session"),
                ($"  {OrchestratorLayout.LogsDirectoryName}/<name>{OrchestratorLayout.LogExtension}", "a JSON line for each run that reached it or one of its agents, refusals included; a dry run writes none"),
                ($"  {OrchestratorLayout.LogsDirectoryName}/<agent>/", "the agent's Claude transcripts, kept when it is deleted"),
                ($"  {OrchestratorLayout.WorkDirectoryName}/<agent>/", "the agent's scratch and task files"),
                ($"  {OrchestratorLayout.PlansDirectoryName}/<name>/", "plans: its own, and a directory for each agent"),
                ($"  {OrchestratorLayout.AgentsDirectoryName}/<agent>/{OrchestratorLayout.RecordFileName}", "the agent's record: name, model, createdAt, base, state, closing"),
                ($"  {OrchestratorLayout.AgentsDirectoryName}/<agent>/{OrchestratorLayout.SeedFileName}", "what it shares with the main tree: each path handed or folded"),
                ($"  {OrchestratorLayout.AgentsDirectoryName}/<agent>/{OrchestratorLayout.RowsDirectoryName}/<ID>/", "an anchor row it files, a file for each cell"),
                ($"  {OrchestratorLayout.AgentsDirectoryName}/<agent>/{OrchestratorLayout.AppliedRowsFileName}", "the rows its folds applied, each as declared then"),
                ($"  {OrchestratorLayout.AgentsDirectoryName}/<agent>/{OrchestratorLayout.EvidenceDirectoryName}/", "its evidence roots' files, kept when it is deleted"),
            ]);
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"A row's cells are {string.Join(", ", AgentRows.RequiredCells.Select(cell => cell + AgentRows.CellExtension))}, and "
            + $"{AnchorCellNames.Priority}{AgentRows.CellExtension} where it declares one, each read as write-anchor reads a cell file. "
            + "An agent's worktree is <worktrees.root>/<orchestrator>/<agent>, which list-worktree and delete-worktree name "
            + "orchestrator/agent; the copies hosts keep of it are named orchestrator--agent. Agents write their handoffs, pause "
            + "and stop points as .md files in their own directory.");
        builder.AppendLine();
        builder.AppendLine("Commands");
        builder.AppendLine($"  {command} {OrchestratorService.CreateCommand} <o> --model <id> [--parallel <n>] [--session <id>]");
        builder.AppendLine($"  {command} {AgentService.CreateCommand} <o> <a> --model <id> [--empty] [--session <id>]");
        builder.AppendLine($"  {command} {AgentService.SeedCommand} <o> <a> [--empty] [--force]");
        builder.AppendLine($"  {command} {AgentService.RefreshCommand} <o> <a> [<path>...] [--apply]");
        var allowances = $"[{FoldAllowances.SettledOption} <path>]... [{AnchorBatchRequest.NewOption} <ID>]... [{AnchorBatchRequest.AcceptLostOption} {AnchorRowCell.Form}]...";
        builder.AppendLine($"  {command} {AgentService.FoldCommand} <o> <a> [--apply] {allowances}");
        builder.AppendLine($"  {command} {AgentService.DeleteCommand} <o> <a> [--apply] {allowances}");
        builder.AppendLine("      [--discard-uncommitted]");
        builder.AppendLine($"  {command} {OrchestratorService.ListCommand} [<o>] [--json]");
        builder.AppendLine($"  {command} {OrchestratorService.DeleteCommand} <o> [--delete-evidence]");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"--parallel, {OrchestratorRecord.DefaultParallel} unless given, is the most open agents at once - every agent not yet "
            + "deleted, and every worktree git lists below the orchestrator's directory: create-agent refuses the next, naming them, "
            + "and list-orchestrator counts them. Run again, create-orchestrator changes only the limit and the session, and "
            + "create-agent records only the session; for an agent whose making stopped part way, it says so and names what "
            + "finishes it. An agent's name is never used twice: a deleted agent's directory stays as its history.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "Seeding. create-agent hands the agent the main tree's uncommitted state - every path git status lists, off the "
            + "worktrees root, .git and .orchestrators: each changed file copied into its worktree, each deletion made there - and "
            + "records what it was handed, each file with its digest. An untracked directory git will not look into, a repository "
            + "of its own, is named and not handed; --empty hands it nothing. A symbolic link among it is refused before the "
            + "worktree is made, so nothing is left behind. seed-agent does it again, refused over changes of the agent's own - a "
            + "copy it was handed and left alone is not one - unless --force; --empty hands it nothing more, keeping what it was "
            + "handed before. refresh-agent hands a live agent the main tree's changes under the paths given - the anchor "
            + "registries' directory where none are - a dry run until --apply, refused, copying nothing, where the agent changed "
            + "or deleted one, and records them as handed to it, so its fold leaves them out.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "Folding. fold-agent writes an agent's own work into the main tree, a dry run until --apply, and never removes its "
            + "worktree. Every path its status lists and every path it shares with the main tree - handed to it, or written or "
            + "removed by an earlier fold - goes in one list: its own, written; deleted, removed with the directories that leaves "
            + "empty; inherited, shared and unchanged; already in the main tree; or settled. A shared path is compared with what "
            + "both trees held, any other with the blob at the agent's own base as git status compares, so a line-ending "
            + "conversion is no change, a changed mode is one, and a sibling's committed fold is never written over. What a fold "
            + "writes is recorded as shared: when a review sends the agent back, its later change of such a path - putting it back "
            + "as it was included - is its work to fold like any other. The whole fold is refused, and nothing written, where the "
            + "main tree changed one of its paths since - by a commit, or by an uncommitted edit, the message says which - where a "
            + "commit was made inside the agent, for a link, a directory, or a path leading out of the main tree, and for an anchor "
            + "registry the agent changed as a file: its rows go in through its rows directory only. --settled "
            + "<path> leaves out a path you reconciled by hand, so the rest goes in; it is not a --force. A path this process "
            + "cannot look at is never read as absent: the fold fails, nothing written. A file of either tree that changed after "
            + $"it was weighed is never written over: the fold stops there, exit {HarnessExit.Incomplete}, and run again weighs it "
            + "anew. Then the rows it declares anew are applied, all or nothing, each held to what write-anchor and set-anchor "
            + "refuse - a cell they would store cut, a citation of a row neither registry nor the fold's own rows hold - and to two "
            + "rules of its own. A row no registry holds is made only where --new <ID> names it, since a typo in an existing row's "
            + "id would make it a second row; and a cell of an existing row whose stored text the agent's does not keep word for "
            + "word - neither respaced, nor kept whole in an addendum, nor filling an empty cell - is written only where "
            + "--accept-lost <ID>:<cell> names it: the dry run shows what each such cell loses, word by word, and the command that "
            + "writes it, on its line 'to write them:', and --apply refuses the fold, writing nothing, until each is named. Every "
            + "refusal of every row is named in one run. A --new or an --accept-lost naming a row the agent did not file is "
            + "refused, and so is an --accept-lost naming a cell that keeps its stored text, and a --new naming a row a registry "
            + "holds other than as declared; one naming a row an earlier fold of the agent applied is let stand, so the command "
            + "line of that fold runs again. An existing row is changed in the cells that differ, and one already as declared left "
            + "alone; a write that fails puts both registries back byte for byte. A row an earlier fold applied, declared as it was "
            + "then, is never applied again, so a change the registries took since stands; one declared anew over it is refused "
            + "where the registries changed it since, as a file is.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "Deleting. delete-agent folds what is left and applies the rows, proves nothing is left to fold, copies the agent's "
            + $"Claude transcripts, found by session id under {ClaudeTranscripts.ConfigDirectoryVariable} or ~/.claude, into "
            + $"{OrchestratorLayout.LogsDirectoryName}/<agent>/ - one not found is said, and one found and not kept stops it before "
            + "anything is closed - and keeps what the evidence roots named before the fold and after it hold, each copy proved, in "
            + "a directory named for the run; a root that is or passes through a link, and a link under one, is neither kept nor "
            + "counted. It then records the agent closed, "
            + "deletes the evidence files still holding what was kept - one that cannot be deleted is named and left, and stops "
            + "the removal - and removes the worktree and its copies on hosts through delete-worktree - never forced, and its "
            + "evidence check kept, so a file written late stops it. It says "
            + "deleted only once the directory, git's record of it and every recorded copy are gone. --discard-uncommitted "
            + "abandons an agent: nothing folded and no rows applied, its evidence and transcripts still kept.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "A closed agent is never folded again. Run again, delete-agent compares its worktree with what closing it recorded, "
            + $"never with the main tree: a file changed or new since is work, left for you, and it exits {HarnessExit.Incomplete}; "
            + "a file gone since is the debris of a removal that stopped part way. A directory at its path with no .git of its "
            + "own is never forced: its evidence is kept again, and the delete-worktree --force that removes it is named for you. "
            + "A worktree made at its path since is not its to remove.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "Every command that writes the main tree or an agent's worktree holds it, as a sync holds a copy - fold-agent, "
            + "seed-agent, refresh-agent and delete-agent hold the agent's worktree as well as the main tree - so no leg builds in "
            + "either meanwhile, and once one has written, an interruption waits for it to finish. delete-agent refuses to run from "
            + "inside the worktree it removes, "
            + "and an agent made under a worktrees root the configuration no longer names is refused with nothing touched. "
            + "delete-orchestrator deletes the orchestrator once every agent of it is deleted and no worktree is left below its "
            + "directory, and the evidence and transcripts its agents kept only with --delete-evidence; it removes the record "
            + "last, so one that stops part way finishes when run again.");

        return builder.ToString();
    }

    private static string RenderLayout()
    {
        var builder = new StringBuilder();

        builder.AppendLine("Layout created by init");
        builder.AppendLine();
        builder.AppendLine("  .harness-config/config.json        tracked by git; the whole contract");
        builder.AppendLine($"  {HarnessLayout.RunnerActionsDirectoryRelative}/<name>/<name>.yml");
        builder.AppendLine("                                    tracked; one directory per action, holding its");
        builder.AppendLine("                                    steps and whatever those steps run");
        builder.AppendLine("  .harness-config/runner/.env/       contents ignored; values actions read");
        builder.AppendLine("  .harness-config/runner/.secrets/   contents ignored; secret values actions read");
        builder.AppendLine("  .harness-config/sshItems/<name>/   ignored, and written by you, not init:");
        builder.AppendLine("                                     .env (address, user, port), .key, known_hosts");
        builder.AppendLine("  .harness-config/wslDistros/<name>/ ignored; .env (distribution, credential)");
        builder.AppendLine($"  {WorktreeSettings.SeededRoot}/                        contents ignored, .gitkeep tracked; the worktrees,");
        builder.AppendLine("                                     at worktrees.root, which init writes as this");
        builder.AppendLine($"  {HarnessLayout.OrchestratorsDirectoryName}/                    contents ignored, .gitkeep tracked; what each");
        builder.AppendLine("                                     orchestrator and its agents keep, in the main");
        builder.AppendLine("                                     checkout, and never sent to a host by sync");
        builder.AppendLine($"                                     ('{ToolPackage.Command} help orchestrators')");
        builder.AppendLine($"  {HarnessLayout.RunsDirectoryRelative}/              ignored; one directory of records per run, in");
        builder.AppendLine("                                     the tree that ran it");
        builder.AppendLine("  .harness-config/lock.json          ignored; records in-progress runs");
        builder.AppendLine($"  {HarnessLayout.HostCopiesDirectoryRelative}/       ignores itself; which hosts hold a copy of");
        builder.AppendLine("                                     which worktree, in the main checkout");
        builder.AppendLine($"  {AnchorSettings.DefaultPendingAnchorsPath}");
        builder.AppendLine("                                     tracked; live anchors (anchors.pendingAnchorsPath)");
        builder.AppendLine($"  {AnchorSettings.DefaultDoneAnchorsPath}");
        builder.AppendLine("                                     tracked; closed anchors (anchors.doneAnchorsPath)");
        builder.AppendLine();
        builder.AppendLine("init creates each anchor registry that is missing, from a skeleton holding an");
        builder.AppendLine("introduction and an empty table, and never touches one that exists. A registry");
        builder.AppendLine("git tracks belongs to the branch, so it is created in the tree init runs in.");
        builder.AppendLine();
        builder.AppendLine("init adds these rules to .gitignore inside a marked block, replacing that");
        builder.AppendLine("block on later runs and leaving every other rule untouched. It then asks git which");
        builder.AppendLine("rule decides each path the block rules on, and names in a note any rule that turns");
        builder.AppendLine("one the other way: one git follows undoes the block there, and one the block");
        builder.AppendLine("overrules does nothing there - named only where taking it out would take from git");
        builder.AppendLine("nothing the harness keeps: no path the block rules on, none of its own files - its");
        builder.AppendLine("configuration, a placeholder, an action's files, an anchor registry - and no");
        builder.AppendLine("directory those are in, as git reads every ignore file the tree has, its");
        builder.AppendLine("info/exclude and configured excludes file among them. An allowlist's re-include of");
        builder.AppendLine("the slot a placeholder is kept in is overruled for the slot's contents and needed");
        builder.AppendLine("for the placeholder, since git never looks inside an excluded directory, so it is");
        builder.AppendLine("not named. A rule agreeing with the block is not named, however it is spelled.");
        builder.AppendLine("'.env', which many repositories ignore, is named: it takes the whole runner/.env");
        builder.AppendLine("directory, and git re-includes no placeholder from an excluded one. A rule");
        builder.AppendLine("re-including a directory the block ignores, such as each host's under sshItems, is");
        builder.AppendLine("named too, though git itself names none for the files below it. A path git will");
        builder.AppendLine("not answer about - one beyond a symbolic link - is named with git's reason, and the");
        builder.AppendLine("rest are still asked. runs is ignored by name, with no trailing slash, so one kept");
        builder.AppendLine("on another disk through a link is ignored as the link it is. The worktrees root and");
        builder.AppendLine("the orchestrators directory are kept in git by their placeholders, so each reads as");
        builder.AppendLine("not ignored itself while everything made in it is, and neither can be a link: init");
        builder.AppendLine("names one it finds, and writes nothing through it. Sync withholds both by name.");
        builder.AppendLine();
        builder.AppendLine("init writes the tree it runs in, a worktree's own included: its configuration - a");
        builder.AppendLine("copy of the main checkout's, where the worktree was running on that one - its");
        builder.AppendLine(".gitignore and its placeholders. The main checkout is left as it is, but for an");
        builder.AppendLine("anchor registry git ignores, created there where it is missing.");
        builder.AppendLine();
        builder.AppendLine("Ignored state lives in the main checkout. A worktree receives the tracked part of");
        builder.AppendLine(".harness-config through git but never the ignored part, so connection data and the");
        builder.AppendLine("run lock resolve back to the originating checkout. A run's records are the");
        builder.AppendLine("exception: they belong to the tree that ran it, so a run started inside a worktree");
        builder.AppendLine("writes them there, and build, test and run name the directory in their output and");
        builder.AppendLine("as runDirectory in --json. A leg a host ran names that host's own directory, its home");
        builder.AppendLine($"written as ~ (see '{ToolPackage.Command} help legs'). Action files are tracked, so a");
        builder.AppendLine("worktree has its own and a runner acts on the tree it was asked about.");

        return builder.ToString();
    }

    private static string RenderConfig()
    {
        var builder = new StringBuilder();

        builder.AppendLine("config.json");
        builder.AppendLine();
        builder.AppendLine($"  defaults       buildCores and testCores ({HarnessDefaults.DefaultCores} each), maxParallelLegs (per");
        builder.AppendLine("                 machine) and maxParallelLegsTotal (the whole fleet), default project,");
        builder.AppendLine("                 stallSeconds, the stall bound nothing more specific replaces");
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
        builder.AppendLine("                 that runs them: visualStudio runs that instance's vcvarsall.bat");
        builder.AppendLine("  legs           units of work: os + processor (+ emulator) + project + toolchain");
        builder.AppendLine("                 + config (+ sanitizer); buildSpaceGiB, the room a leg's build");
        builder.AppendLine("                 comes to, is 'help space'");
        builder.AppendLine("  legSets        named groups of legs, selected with --legs like a leg");
        builder.AppendLine("  tools          external tools to verify and install");
        builder.AppendLine("  predefinedRunners  multi-phase procedures such as a corpus test or a benchmark");
        builder.AppendLine($"  exec           named commands to run through '{ToolPackage.Command} exec'");
        builder.AppendLine("  commit         commit template and sign-off policy");
        builder.AppendLine("  sync           what the tree mirror carries, and what it must never carry");
        builder.AppendLine("  contention     tools that, running against a leg's build directory, void its result");
        builder.AppendLine("  worktrees      naming, path budget and path limit");
        builder.AppendLine("  anchors        the pending and done anchor registries, and how new ids are spelled");
        builder.AppendLine();
        builder.AppendLine("A list whose absence means every one of what it names, or a set the tool chooses, is");
        builder.AppendLine("refused given empty, as --legs given no name is: a runner's legs and steps; a tool's");
        builder.AppendLine("platforms, toolchains, legs, processors and emulators; a toolchain's platforms; an");
        builder.AppendLine("emulator's phases; ci.workflows; test.inputs; a project's targets and");
        builder.AppendLine("rebuildableFormats; a platform's toolSearchDirectories. Leave the key out instead. A");
        builder.AppendLine("list whose empty form means none - such as sync.neverTransfer, sync.exclude or");
        builder.AppendLine("contention.buildTools - takes [] as that.");
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
        builder.AppendLine("a leg the order is fixed: sync when the host needs it, then build on buildCores");
        builder.AppendLine("cores, then test on testCores cores. Where a leg runs is measured before anything");
        builder.AppendLine($"starts; see '{ToolPackage.Command} help legs'.");
        builder.AppendLine();
        builder.AppendLine("A verdict must describe the code, not the moment it ran in. Every test invocation");
        builder.AppendLine("declares a successPattern, since exiting 0 is not proof anything ran. A leg whose");
        builder.AppendLine("test inputs change while it runs, or whose build directory another process uses,");
        builder.AppendLine("gets no pass or fail at all. Durations that diverge between legs, or that span a");
        builder.AppendLine("clock step or a host sleep, are marked suspect, and never change a verdict.");
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
        builder.AppendLine("A leg on a host reached through WSL or ssh runs in that host's copy of its tree:");
        builder.AppendLine("the files the sync writes there from this tree, in a git repository of the host's own -");
        builder.AppendLine("one the sync made, or one it took over - whose history is not this checkout's. Each");
        builder.AppendLine("sync makes its index hold the files it carried, so a build there fingerprints its");
        builder.AppendLine("inputs and keeps its directory as a build here does. A test that checks this");
        builder.AppendLine("checkout's state has nothing to say about that copy, so an invocation's");
        builder.AppendLine("remoteExcludes are given to every leg a host runs, beside --exclude's, and to none");
        builder.AppendLine("this machine runs:");
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
        builder.AppendLine("reads there - or beside a test preset that takes a union or cannot be read. A label");
        builder.AppendLine("beside labels the args or a preset choose runs: ctest reads it as one more a test must");
        builder.AppendLine("carry. An option given where its arg is not declared, or given empty or as spaces");
        builder.AppendLine("alone, is refused, and a host running one of the legs is given all three. A -I in the");
        builder.AppendLine("args, or a preset's index, picks by position among the tests the others leave, so a");
        builder.AppendLine("filter, an exclusion or a label moves a fixed window onto other tests.");
        builder.AppendLine();
        builder.AppendLine("The file is checked when it is read: unknown keys, a key written twice, and");
        builder.AppendLine("references to undeclared names are rejected, with every problem listed at once.");
        builder.AppendLine("Comments and trailing commas are accepted, since the file is meant to be edited.");
        builder.AppendLine("A section whose command is not implemented yet is still checked, but has no effect");
        builder.AppendLine("until that command arrives.");

        return builder.ToString();
    }

    /// <summary>One reference topic.</summary>
    /// <param name="Name">The name it is asked for by, and advertised under.</param>
    /// <param name="Aliases">The other names it answers to.</param>
    /// <param name="Summary">What the overview says of it.</param>
    /// <param name="Render">What renders it.</param>
    private sealed record HelpTopic(string Name, IReadOnlyList<string> Aliases, string Summary, Func<string> Render);
}
