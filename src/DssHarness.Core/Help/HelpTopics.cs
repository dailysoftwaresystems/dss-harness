using System.Globalization;
using System.Text;
using DssHarness.Core.Ci;
using DssHarness.Core.Anchors;
using DssHarness.Core.Build;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.Git;
using DssHarness.Core.Hosts;
using DssHarness.Core.Legs;
using DssHarness.Core.Mutations;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
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

/// <summary>
/// The tool's reference material, one topic at a time: what does not fit in option descriptions. <c>--help</c> answers
/// "what can I type"; this answers "what do the answers mean", which is what a caller reaching for documentation actually
/// wants. The core owns the text, so a front end of its own answers the same questions the command line does.
/// </summary>
public static class HelpTopics
{
    /// <summary>Opening words of the unrecognised-topic message.</summary>
    public const string UnknownTopicPrefix = "Unknown topic";

    /// <summary>
    /// What a command asked for --json answers with where it stops before it has anything of its own to say, and the one
    /// exit where it answers with none: said in the overview for every command and wherever a listing's document is
    /// described, since most documents carry exitCode and summary only then.
    /// </summary>
    private const string StoppedDocument = "Asked for --json, a command answers with one document on every exit but two: --help, "
        + "-h or -? prints its usage instead, exit 0, whatever else its command line holds, even what the parser would otherwise "
        + "refuse; and --json given twice, a value each, asks for no document, so the parser's refusal is said alone, exit 10. Stopped before it has anything of its own to say - a command line the parser refuses, a --directory that is not "
        + "there, a repository not initialised, another usage error, or a refusal met before anything is weighed - it answers "
        + "with its own document holding nothing, exitCode and summary first, which the leg ledger, the agent document and the "
        + "check-mutations --list listing carry on every exit and every other document only then, or, where its document is an "
        + "array, with that array empty.";

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
        new("admission", ["heavy"], "How heavy legs share a machine: its slots, its memory and its room", RenderAdmission),
        new("worktrees", ["worktree"], "Naming rules, the path budget, and when deleting refuses", RenderWorktrees),
        new("orchestrators", ["orchestrator", "agents", "agent"], "Orchestrators, their agents, and folding an agent's work", RenderOrchestrators),
        new("workflows", ["workflow"], "Workflow definitions: what config.json declares and the yml file defines", RenderWorkflows),
        new("agent-clients", ["clients", "models"], "Providers, models, tiers and keys in config.json", RenderAgentClients),
        new("anchors", ["anchor"], "Anchor registries and the commands that change them", RenderAnchors),
        new("layout", [], "What init creates, and what git tracks", RenderLayout),
        new("secrets", ["hosts-secrets"], "Where each host's connection data lives", RenderSecrets),
        new("tools", [], "What install-missing-tools installs, and where", RenderTools),
        new("runners", ["runner", "actions"], "Predefined runners, action files and excused failures", RenderRunners),
        new("setups", ["setup", "set-up", "set-ups", "up", "down"], "Set-ups a run brings up around its work, and down after", RenderSetUps),
        new("verdicts", ["verdict"], "What each leg verdict means, and what to do about it", RenderVerdicts),
        new("mutations", ["mutation", "check-mutations", "arms", "self-test"], "Mutation testing: the registry, its workers, and the self-test", RenderMutations),
        new("ci", ["check-ci-legs"], "How check-ci-legs finds a workflow's legs and budgets", RenderCi),
    ];

    /// <summary>
    /// Every topic's name and one-line summary, in the order the overview lists them: what the command's own description
    /// says of them. The one list the overview, the rendering and the answer to an unknown topic read, so no topic is ever
    /// advertised by one and missing from another.
    /// </summary>
    public static IReadOnlyList<HelpTopicInfo> Described
        => [.. Topics.Select(topic => new HelpTopicInfo(topic.Name, topic.Summary))];

    /// <summary>What to do about each verdict that has a remedy worth saying, in the order help lists them, one line apiece.</summary>
    private static readonly (LegVerdict[] Verdicts, string[] Remedy)[] Remedies =
    [
        ([LegVerdict.Violated], ["Fix the arm's declaration, or the code it guards"]),
        ([LegVerdict.Survived], ["Strengthen the test that should have failed"]),
        (
            [LegVerdict.InputsMoved, LegVerdict.Unmeasured],
            [
                "Let the tree settle, then run again; where its",
                "inputs could not be watched, free or raise what",
                "the machine's reason names, or restore the tree",
            ]),
        ([LegVerdict.Contended], ["Wait for the other run"]),
        ([LegVerdict.Unwitnessed], ["Find out what actually ran"]),
        ([LegVerdict.LogHeld], ["Find out which run still owns the logs"]),
        (
            [LegVerdict.NotAdmitted],
            [
                "Wait for the heavy legs it names, free memory",
                "or room on the filesystem it names, or raise the",
                "machine's limits ('help admission')",
            ]),
        (
            [LegVerdict.Unattributed],
            [
                "Contain the crash or hang in the case, or make",
                "the runner write its report",
            ]),
        ([LegVerdict.Stopped], ["Find out what stopped its work, then run again"]),
        ([LegVerdict.SkippedUnavailable], ["Make what its line names available, then run again"]),
        ([LegVerdict.SkippedToolMissing], ["Install or update the tool its line names,", "then run again"]),
    ];

    /// <summary>Renders one topic, reporting whether it was recognised.</summary>
    public static bool TryRender(string? topic, out string text)
    {
        text = Render(topic);
        return !text.StartsWith(UnknownTopicPrefix, StringComparison.Ordinal);
    }

    /// <summary>Renders one topic, or the overview where none is asked. Separated from any command so it is directly callable.</summary>
    public static string Render(string? topic)
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
        builder.AppendLine("--dry-run reaches and asks every host as a run does, and installs no tool: each tool");
        builder.AppendLine("it would install or update says 'would install' or 'would update' with the command");
        builder.AppendLine("that would run, sudo and all, and nobody is asked for a password or whether one is");
        builder.AppendLine("needed. It exits 1 while anything is missing, as a run that could not install it does.");
        builder.AppendLine();
        builder.AppendLine("Each host is asked about its tools by its own survey, the one 'legs' runs there,");
        builder.AppendLine("so a tool is installed exactly where a leg would be turned away over it, and never");
        builder.AppendLine("a second time where a leg there finds it. The survey runs the DssHarness on the host,");
        builder.AppendLine("put there at this machine's version as 'legs' puts it, and needs its .NET SDK: a host");
        builder.AppendLine("whose survey cannot run - one a dry run finds with no SDK yet - has every tool");
        builder.AppendLine("unknown, saying why, and nothing is installed there.");
        builder.AppendLine();
        builder.AppendLine("Every WSL distribution and ssh host gets the .NET 10 SDK when it has none, under");
        builder.AppendLine("the home directory, where no login-free PATH names it - which is why every command");
        builder.AppendLine("the harness runs there spells the resolved absolute path rather than a bare name.");
        builder.AppendLine("The package manager an install runs is started the same way, where the search found");
        builder.AppendLine("it - brew in /opt/homebrew/bin, which no login-free PATH on macOS names - save one run");
        builder.AppendLine("through sudo, which sudo looks up on a PATH of its own.");
        builder.AppendLine();
        builder.AppendLine("Each entry under tools that carries an install is probed with probe.args and");
        builder.AppendLine("probe.regex, compared against minVersion, and installed or updated through it. An");
        builder.AppendLine("entry with no install is an allowlist entry: probed if it declares a probe,");
        builder.AppendLine("reported when missing, never installed. That is how a program shipping with the");
        builder.AppendLine("platform, or with the repository, is allowed to appear in a runner's steps.");
        builder.AppendLine();
        builder.AppendLine("A minVersion holds every copy of the tool a leg could start: the first one - on the");
        builder.AppendLine("PATH, then in the searched directories - whose version meets it is the one a leg");
        builder.AppendLine("there starts (see 'Where a program is found'). The tool is current where such a copy");
        builder.AppendLine("exists, named with the copy it is started ahead of, and installed or updated where");
        builder.AppendLine("every copy said an older version. Where the copy that meets it is behind an older one");
        builder.AppendLine("a directory raised for another tool holds, the tool is outdated, naming both, and");
        builder.AppendLine("nothing is installed: an install would not change which copy a leg starts. Where none");
        builder.AppendLine("met it and a copy's version could not be read, or a place a copy could be could not");
        builder.AppendLine("be looked in, the tool is unknown and nothing is installed: nothing says it is behind.");
        builder.AppendLine("A minVersion is written major.minor.patch and needs a probe to read versions with;");
        builder.AppendLine("brew installs and updates with 'brew install', which upgrades a formula that is there");
        builder.AppendLine("and older, since 'brew upgrade' touches only what brew put there - never the system's");
        builder.AppendLine("own copy that fell below.");
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
        builder.AppendLine("set up on the leg's host for the look, by the DssHarness there, so cl is found where");
        builder.AppendLine("Visual Studio keeps it - and any other leg on its host's own PATH. An install runs");
        builder.AppendLine("once on a host, whichever leg asked first, and each is then told what it finds. Where");
        builder.AppendLine("the environment could not be looked at or set up, a tool its legs need is unknown,");
        builder.AppendLine("saying why, and nothing is installed for it. A tool no selected leg on a host needs");
        builder.AppendLine("is not looked for there at all. A scope naming nothing declared, or one");
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
        builder.AppendLine("was. Appended: a name the PATH already answers for keeps that answer - save a");
        builder.AppendLine("declared tool whose first copy is older than its minVersion. That tool is found where");
        builder.AppendLine("its first copy that meets it is, and that copy's directory goes ahead of the PATH of");
        builder.AppendLine("every process every leg on that host starts - one whose environment, or developer");
        builder.AppendLine("environment, sets PATH included - every other program in it with it, as a shell");
        builder.AppendLine("Homebrew set up puts /opt/homebrew/bin first. A leg the tool is declared for is turned");
        builder.AppendLine("away where every copy is older, naming each copy, what it said, and - where the tool");
        builder.AppendLine("declares an install there - the install-missing-tools command that would install one;");
        builder.AppendLine("where the copy that meets it is behind an older one in a directory raised for another");
        builder.AppendLine("tool, naming both - only where no order of the raised directories, nor a further copy");
        builder.AppendLine("of a tool in the way, keeps both on copies that meet them; and where no copy could be");
        builder.AppendLine("shown to meet it, as unknown,");
        builder.AppendLine("'skipped-unavailable'. A leg in a developer environment holds the copy that");
        builder.AppendLine("environment's PATH names to it, once set up. install-missing-tools asks the same");
        builder.AppendLine("survey, on the host's own PATH and on the PATH each developer environment its legs");
        builder.AppendLine("start in sets up there; a tool the survey could not establish is unknown rather than");
        builder.AppendLine("missing, and nothing is installed for it.");
        builder.AppendLine();
        builder.AppendLine("A leg goes to the first of its hosts that can take it - the right machine, reached -");
        builder.AppendLine("whatever the command, which is where a sync puts its tree and where --use-staged");
        builder.AppendLine("finds it. It is turned away there when that host lacks a program its command will");
        builder.AppendLine("start:");
        builder.AppendLine();
        builder.AppendLine("  build   the project's build program, ninja for a Ninja generator, and the");
        builder.AppendLine("          compilers CC and CXX name");
        builder.AppendLine("  test    those, and the test runner; with --no-build, the runner alone");
        builder.AppendLine("  run     what the runner's steps start, and the build's too on a leg it builds:");
        builder.AppendLine("          where the runner, or a runner its run checks name, requires the build or");
        builder.AppendLine("          runs a step or phase naming {product}, {buildDir} or a compiler on that");
        builder.AppendLine("          leg's system");
        builder.AppendLine("  sync    nothing: a copy starts no program");
        builder.AppendLine("  clean   nothing: removing a directory starts no program");
        builder.AppendLine("  legs    what build and test start");
        builder.AppendLine("  check-mutations");
        builder.AppendLine("          what build starts: each worker, and each arm, is built as the leg builds");
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
        builder.AppendLine("PATH, and never turns a leg away: that PATH is where it is found when it starts, a");
        builder.AppendLine("directory raised for a minVersion ahead of it. A PATH among a runner's values is not");
        builder.AppendLine("seen before the run; name its directory under toolSearchDirectories instead.");
        builder.AppendLine();
        builder.AppendLine("A leg turned away for a missing program, or for one below its minVersion, is");
        builder.AppendLine($"'skipped-tool-missing', and the run is incomplete, exit {HarnessExit.Incomplete} - or exit {LegsExit.Unavailable} when");
        builder.AppendLine($"no selected leg can run at all, save of check-mutations, which stays {MutationService.NothingRuns}: its {LegsExit.Unavailable}");
        builder.AppendLine("is an arm violated. A leg that is already running when a program will not start");
        builder.AppendLine("has 'failed', naming the program and the reason the system gave: no survey could");
        builder.AppendLine("have required it - a file the build was to make, a binary for another processor.");
        builder.AppendLine("Neither is 'poisoned', which is kept for a defect in this tool.");

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
        builder.AppendLine("                                     ADDRESS=, USER=, PORT= (optional), and the");
        builder.AppendLine($"                                     superuser credential as {HostSecretsStore.SuperuserKey}= (optional)");
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
        builder.AppendLine("read is refused because ssh ignores it, and so is an .env other users can read");
        builder.AppendLine($"that holds a {HostSecretsStore.SuperuserKey}, a credential already shared; one written blank holds");
        builder.AppendLine("none, and declares none. Each refusal names the command that fixes it. A");
        builder.AppendLine("credential is passed to a privileged command on standard input, and reaches no");
        builder.AppendLine("log, no argument list and no error message.");
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
        builder.AppendLine("The first token must be a program declared under tools, a path in the");
        builder.AppendLine("repository, or the name of one of the leg's compilers alone ({compiler_C},");
        builder.AppendLine("below); anything else is refused before a single step runs. It is judged as");
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
        builder.AppendLine("ranSteps on its line, and the manual ones among them as manualSteps. Whichever runner");
        builder.AppendLine("starts it, a step naming {product}, {buildDir} or a compiler has its leg built first");
        builder.AppendLine("(below).");
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
        builder.AppendLine("matched against what that line printed on standard output and standard error, a line");
        builder.AppendLine("of output at a time - ^ and $ its start and end, whether it ends in CRLF or LF, as");
        builder.AppendLine("its log keeps it - after secrets are redacted. One that does not compile, or that");
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
        builder.AppendLine($"  {{{LegPathNames.CompilerC}}} {{{LegPathNames.CompilerCxx}}}         the C and the C++ compiler the leg's build");
        builder.AppendLine("                                      identified: the whole path of the program");
        builder.AppendLine("                                      CMake built with, as this machine spells it");
        builder.AppendLine("  <input name>                        any input the action declares, or the step");
        builder.AppendLine("                                      declares for itself, by its name");
        builder.AppendLine($"  <value name>                        any value the runner's {HarnessLayout.RunnerEnvDirectoryName} holds, by its name");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"A step whose run line or workingDirectory names {{{LegPathNames.BuildDirectory}}}, {{{LegPathNames.Product}}}, "
            + $"{{{LegPathNames.CompilerC}}} or {{{LegPathNames.CompilerCxx}}} reads what the build made or identified, so "
            + "every leg of a run that runs it is built first - however the run comes to run it: by default, named "
            + $"with {StepSelection.Option} or under a runner's steps, or needed by one that is - whichever runner "
            + "starts it, requireBuild or not; one limited by runOn builds the legs of those systems alone, and a "
            + "runner's own phase naming one builds as a step does. So does a runner whose run checks name a "
            + "runner that needs the build: a check runs on the leg as the runner carrying it left it. Left unbuilt, "
            + "such a step would read whatever the last build left: nothing, or what an older commit built. A leg a "
            + "run would build that cannot be built - it names no project, or no toolchain for its system - is "
            + "refused before anything starts, naming each leg and what builds it; so is a {product} the runner's "
            + "own steps or phases name where the leg's project declares no one file for its system. A run check's "
            + "{product} or compiler is the check's to refuse, when it runs.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"{{{LegPathNames.CompilerC}}} and {{{LegPathNames.CompilerCxx}}} name what built the leg, never what a toolchain "
            + "declares: the program CMake's own record in the leg's build directory names for that language - gcc on one "
            + "leg and cl on another, from one run line. A line may start with one, written alone: the leg's own compiler "
            + "may run though nothing declares it under tools. A leg whose project CMake does not build is refused before "
            + "anything starts, naming each leg and step, since no build of it identifies a compiler; and a name the "
            + "leg's build has nothing for is refused once the leg is built, before its first step, saying which case it "
            + "is - no compiler identified for that language, a record of one that could not be read or that names no "
            + "program, or one the build runs with words after it, a launcher such "
            + "as ccache given its compiler or a compiler given options: the name is a program alone, so the program and "
            + "the words are named and neither is filled in. A test invocation's args, coresArgs and workingDirectory may "
            + "name one too: the check made before the build lets the name stand, and it is filled in, or refused, once "
            + "the leg is built - under test --no-build, from what the build already in its directory identified.");
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
        builder.AppendLine("kept it. A leg that works in a copy of its tree keeps them in the copy, on this");
        builder.AppendLine("machine: its 'tree' names the copy, and the path joined with it opens the file.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "A path given to --pull that ends with '/' names a directory: every file below it is brought back, each checked as "
            + "a file named is and each named in the report, as the path a later command takes. The host that holds it lists "
            + "it first, and a directory that is not there, holds no file, holds more than "
            + $"{SyncServe.MostFilesPulledFromADirectory.ToString(CultureInfo.InvariantCulture)} files or more than "
            + $"{(SyncServe.LargestDirectoryPulled / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)} MiB together fails the pull before "
            + "any file crosses: each file comes in a request of its own, so a directory named is a step's kept outputs, never "
            + "a build tree - keep that as one archive. A link below it is never followed: it is named, and nothing is brought "
            + "back for it. With --dry-run the host is asked what the directory holds, and nothing is brought. A pull that "
            + "stops part way - a file gone, a host that stopped answering - fails naming each file it had brought back, which "
            + "stays where it was written, and each it had not.");
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
        AppendWrapped(
            builder,
            "A file crosses whole, whichever way it goes - to a host inside one request, back in one answer: one larger than "
            + $"{SyncServe.LargestFile.ToString(CultureInfo.InvariantCulture)} bytes is refused by name before anything is "
            + "sent. Keep the smaller thing a later step reads - a packaged build rather than a build tree.");

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
        builder.AppendLine("quiet one, the same day. A check whose own runner's watch cannot start was not");
        builder.AppendLine("run, and confirms nothing: the failure stays genuine, and the run fails.");
        builder.AppendLine();
        builder.AppendLine("A runner whose steps change files of the tree says writesTree: its legs work in");
        builder.AppendLine("their trees themselves, whatever the leg's own localCopy or defaults.localCopy");
        builder.AppendLine("says ('help legs'), for what it wrote in a copy the next sync would make the tree");
        builder.AppendLine("again. A leg a host would run is refused before any host is measured, and so is");
        builder.AppendLine("--local-copy on the command. A run check's runner runs within the leg's own work,");
        builder.AppendLine("so it is held to the same: one that writes the tree is refused wherever the leg it");
        builder.AppendLine("checks would work in a copy, --local-copy among them.");
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

    private static string RenderSetUps()
    {
        var builder = new StringBuilder();

        builder.AppendLine("Set-ups: what a repository brings up for its work, and down after");
        builder.AppendLine();
        builder.AppendLine("config.json names each set-up under setUp, with an up and a down command line:");
        builder.AppendLine();
        builder.AppendLine("  \"setUp\": {");
        builder.AppendLine("    \"e2e\": {");
        builder.AppendLine("      \"up\": \"docker compose -f e2e/compose.yml up -d --wait\",");
        builder.AppendLine("      \"down\": { \"windows\": \"scripts/e2e-down.cmd\", \"all\": \"./scripts/e2e-down.sh\" },");
        builder.AppendLine("      \"shared\": \"machine\"");
        builder.AppendLine("    }");
        builder.AppendLine("  }");
        builder.AppendLine();
        builder.AppendLine("A line is a program and its arguments, split as an action file's run line is: at");
        builder.AppendLine("spaces, a double-quoted word kept whole, and no shell between - no pipes, no");
        builder.AppendLine("redirection, no variables, no wildcards. What needs a shell is a script the line");
        builder.AppendLine("names. Its program is a tool declared under tools, or a path inside the repository");
        builder.AppendLine("('help tools'), and a relative path is read against the root of the tree the");
        builder.AppendLine("line runs in. Either line may be a map of windows, linux, macos and all, where all");
        builder.AppendLine("serves every platform the map does not name; a platform it serves none of has no");
        builder.AppendLine("line, and the set-up is refused there, nothing run.");
        builder.AppendLine();
        builder.AppendLine("A set-up may declare inputs its lines name in braces, each with an optional default:");
        builder.AppendLine();
        builder.AppendLine("  \"db\": {");
        builder.AppendLine("    \"up\": \"docker compose --profile {profile} up -d --wait\",");
        builder.AppendLine("    \"down\": \"docker compose --profile {profile} down\",");
        builder.AppendLine("    \"inputs\": { \"profile\": { \"default\": \"dev\", \"description\": \"which services\" } }");
        builder.AppendLine("  }");
        builder.AppendLine();
        builder.AppendLine("Each {name} in an argument is filled in with that input's value, the argument staying");
        builder.AppendLine("one argument whatever the value holds; {{ and }} are braces. An input with no default");
        builder.AppendLine("must be given a value by whatever runs the set-up; a brace no input fills is refused");
        builder.AppendLine("when the file is read.");
        builder.AppendLine();
        builder.AppendLine($"  {ToolPackage.Command} {SetUpService.UpCommand} <name>      run its up line at the root of the tree, on this machine");
        builder.AppendLine($"  {ToolPackage.Command} {SetUpService.DownCommand} <name>    run its down line there");
        builder.AppendLine();
        builder.AppendLine($"{CommandLineInputs.Option} name=value gives an input its value, once per input, over its default;");
        builder.AppendLine("an input is named ignoring case, so two values for one name in two cases are refused.");
        builder.AppendLine("A set-up is brought down with the values it was brought up with. Each command streams");
        builder.AppendLine("the line's output and exits 0 where it exits 0, and 20 where it exits anything else;");
        builder.AppendLine("10 for a name setUp does not declare or a value that cannot be given, 13 where this");
        builder.AppendLine("platform has no line or the program is not allowed, 14 where the program cannot start.");
        builder.AppendLine("--json answers with the line that ran, the values it was given, its exit code and how");
        builder.AppendLine("long it took. down is refused, 13, while a leg of a run on this machine still uses the");
        builder.AppendLine("set-up in that tree, or in any of the repository's where the machine shares it: the last");
        builder.AppendLine("of them brings it down as it ends.");
        builder.AppendLine();
        builder.AppendLine("Around a run");
        builder.AppendLine();
        builder.AppendLine("A test invocation and a predefined runner name the set-ups their work needs, each by");
        builder.AppendLine("name, or with the values it gives the set-up's inputs:");
        builder.AppendLine();
        builder.AppendLine("  \"projects\": [ { \"name\": \"core\", \"test\": { \"all\": { \"setUp\": [\"e2e\"] } } } ]");
        builder.AppendLine("  \"predefinedRunners\": { \"corpus\": { \"setUp\": [\"e2e\", { \"name\": \"db\", \"with\": { \"profile\": \"ci\" } }], ... } }");
        builder.AppendLine();
        builder.AppendLine($"test and run bring them up only when given {LegSetUps.Option}; without it a run runs as it");
        builder.AppendLine("did. With it, each leg brings up what its work names - for test its test invocation's,");
        builder.AppendLine("for run its runner's and its run checks' runners' - on the machine that runs the leg,");
        builder.AppendLine("in the tree it works in there: the tree, its copy, or a host's copy. They come up in");
        builder.AppendLine("the order named, before the leg's build, and go down in the reverse order after its");
        builder.AppendLine("work, however that ended. Interrupted, what came up comes down until shortly before");
        builder.AppendLine("the command line stops waiting, as long as for a deletion, counted from the interruption;");
        builder.AppendLine("one still coming down then is stopped, and said as not brought down.");
        builder.AppendLine("Legs sharing a set-up share it: those working in one tree of a machine, or, where it");
        builder.AppendLine("says \"shared\": \"machine\", every leg of the repository on the machine whatever tree it");
        builder.AppendLine("works in - for what a machine holds once, as a container's name or a port, which a tree");
        builder.AppendLine("of its own would bring down under the others; another repository's set-up of the name");
        builder.AppendLine("is another. The first brings it up, the others wait until it is up and use it, and the");
        builder.AppendLine("last of them to end, in whichever tree, brings it down. A leg sharing it with legs that");
        builder.AppendLine("each brought it up apart, as two trees' legs sharing it by tree, waits until it meets");
        builder.AppendLine("one. A leg");
        builder.AppendLine("giving its inputs other values waits until the legs using it let it go, then brings it");
        builder.AppendLine("up with its own. A run killed before bringing one down leaves it up, and the next leg");
        builder.AppendLine("brings it up again, so a line must be safe to run twice. A leg holds every set-up it");
        builder.AppendLine("names before any comes up, in the order of their names, so two legs naming two in");
        builder.AppendLine("opposite orders never each wait for the other. A set-up named twice with the values its");
        builder.AppendLine("inputs take, however each writes them, is brought up once.");
        builder.AppendLine();
        builder.AppendLine("A line around a run runs as a phase of the leg, in the leg's environment and under");
        builder.AppendLine("defaults.stallSeconds, its log 'set-up <name> up' or 'set-up <name> down' among the");
        builder.AppendLine("leg's records. One that does not come up fails the leg before its work starts, and is");
        builder.AppendLine("brought down unless its line never started. One that does not come down is said on");
        builder.AppendLine("the leg's line, or beneath a refusal that ended the run, and in the run's last line,");
        builder.AppendLine("and as setUpsLeftRunning in --json, with the command that brings it down, and");
        builder.AppendLine($"the run exits 21 where nothing failed. {LegSetUps.Option} is refused, 10, where nothing the");
        builder.AppendLine("selected legs run names a set-up, and 13 where a line a leg would run could not run on");
        builder.AppendLine("its system - no line for its platform, a program not allowed, a value its inputs");
        builder.AppendLine("cannot be given, one set-up named twice with other values - every one named before");
        builder.AppendLine("anything runs.");

        return builder.ToString();
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
        builder.AppendLine("A run where nothing failed but some leg reached no verdict, or left a set-up it brought up");
        builder.AppendLine($"running ('help setups'), exits {HarnessExit.Incomplete}, not 0, and names the legs that did not report. A leg that never ran proves nothing about the");
        builder.AppendLine("code, and nor does a build something stopped from outside before it finished, so");
        builder.AppendLine("counting either among the legs that passed reports evidence nobody gathered. A");
        builder.AppendLine("CMake build under ninja that fails with ninja saying nothing of why - which it says");
        builder.AppendLine("whenever it ends a build itself - or saying it was interrupted is stopped, not");
        builder.AppendLine("failed: run again, it finishes, where a failed one fails again. Read under the name");
        builder.AppendLine("it says it under, which for samurai, run by CMake for ninja where it is installed,");
        builder.AppendLine("is its file's. A build the harness stopped for hanging stays failed, saying it hung,");
        builder.AppendLine("and so does one under any other build tool, since only ninja's lines are read. A");
        builder.AppendLine("heavy leg's build stopped for leaving less room than its machine's minFreeGiB is");
        builder.AppendLine("stopped too ('help admission').");
        builder.AppendLine("A run killed, or stopped with its machine, before it finished says nothing more:");
        builder.AppendLine("the next build, test, run or check-mutations in its tree on that machine to own its");
        builder.AppendLine("run directory says it was abandoned - its run id, process and start, and where its");
        builder.AppendLine("records are - and releases its claim on them.");
        builder.AppendLine();
        builder.AppendLine("check-mutations judges each arm of a repository's mutation registry, and a leg's");
        builder.AppendLine("verdict there is the worst of its own and its arms': violated where an arm's");
        builder.AppendLine("declaration did not hold, survived where its mutation built and ran and no case");
        builder.AppendLine("failed, and unattributed where its run failed and nothing ties that to a case. An");
        builder.AppendLine("arm not driven to a verdict - its sweep stopped, or ended by a refusal of the run,");
        builder.AppendLine("while it was driven or before; its own build, or its paired control's, stopped from");
        builder.AppendLine("outside; no worker left to run it; or the unmutated run of its test binary not");
        builder.AppendLine("passing - is stopped, saying why ('help mutations').");
        builder.AppendLine();
        builder.AppendLine("When several apply the more fundamental one is reported, in the order above.");
        builder.AppendLine("A leg whose inputs moved is not reported as failed even when its tests failed,");
        builder.AppendLine("because what failed was a tree that never existed.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "A leg on a copy of its tree - a host's, or one of its own in the tree's .legs - is inputs-moved with nothing of it "
            + "run where the copy cannot be the tree the run read: a file written, appearing or going while the tree was read, "
            + "named; a file to carry changed after it was read; another command synced the copy again between this run's sync "
            + "and the leg taking it, naming that run, or by then the copy is not there, is part made, or holds no copy this "
            + "tool finished; or, under --use-staged, the copy is part made, is not there, or holds no copy this tool made. Each "
            + "leg on a copy says which tree it tested with its verdict - 'tree read at <commit>', with the uncommitted changes "
            + "the reading carried, or under --use-staged 'tree staged at <commit> by run <id>' as the copy records it - and "
            + "--json's 'tree' holds its path, the tree it is a copy of, and the commit, the count and the run. A leg that "
            + "worked in its tree itself and is inputs-moved names the remedy after its detail, where its command could have run "
            + "it on a copy: the leg's localCopy, defaults.localCopy, or --local-copy ('help legs'). A leg another machine sent "
            + "here, or one typed in a copy a sync made, is named none: it works in a copy already, and never in one of its own.");
        builder.AppendLine();

        // The words such a leg's line opens with, read from where the line is made.
        AppendWrapped(
            builder,
            "A leg whose inputs cannot be watched - the machine refusing the watch as its work was to begin - is unmeasured "
            + $"with no phase of it run: '{InputsUnwatchedException.Opening.TrimEnd()}' and the machine's reason. Run first and "
            + "called unmeasured after, the whole of the work was spent to learn what was known before it began. A watch that "
            + "started and was lost while the work ran still leaves it unmeasured, as the work ends. On Linux the machine's "
            + "reason can be this user's inotify instances all taken (fs.inotify.max_user_instances) - a process of this tool "
            + "holds one, however many legs it runs - or its inotify watches run out (fs.inotify.max_user_watches), one for each "
            + "directory the work watches - those its inputs lie in and those above them; each is said with the limit and the "
            + "processes /proc shows holding it, the most "
            + "first. The leg does not wait for either: what holds them is another program, whose end nothing here can read. "
            + "Its remedy is the machine's, not the tree's: free or raise what the reason names - end a program holding this "
            + "user's inotify instances or watches, or raise the limit - or put back the tree the watch was to be put on, then "
            + "run again.");
        builder.AppendLine();
        builder.AppendLine("A run's closing line names its verdict with the count of legs that reached it, and");
        builder.AppendLine("then the rest, worst first - 'poisoned: 2 of 8 leg(s); 3 failed, 3 passed' - never");
        builder.AppendLine("the verdict beside the total, which reads as every leg having it.");
        builder.AppendLine();
        builder.AppendLine("A tool contention.sharedResourceTools lists, found running beside a leg, is a");
        builder.AppendLine("warning: one line per tool and per whose it was, with a count and the range of");
        builder.AppendLine("process ids. A process working in another declared leg's build directory is named");
        builder.AppendLine("as that leg's - two legs on one host share its load, which is maxParallelLegs at");
        builder.AppendLine("work, and yours to decide about - and one working in a build directory of another");
        builder.AppendLine("tree of the repository on that machine, a worktree's, an agent's or the main");
        builder.AppendLine("checkout's, or a copy one of them keeps in its .legs, as that tree's leg's:");
        builder.AppendLine("'worktree o1/xa's leg 'linux-debug'', or 'worktree o1/xa's leg copy's leg");
        builder.AppendLine("'linux-debug''. Only what no tree there accounts for is nobody's known, and where the");
        builder.AppendLine("other trees could not be listed the line says so. contention.sharedState says what");
        builder.AppendLine("each tool shares, in the words the warning uses:");
        builder.AppendLine();
        builder.AppendLine("  \"sharedState\": { \"ccache\": \"the per-user compiler cache\" }");
        builder.AppendLine();
        builder.AppendLine("Remedies");

        // Each verdict's name and exit code read from the verdict table, as the list above them is, so neither can
        // drift from what a run reports.
        foreach (var (verdicts, remedy) in Remedies)
        {
            var code = verdicts.Select(Verdicts.ExitCodeFor).Distinct().Single();
            var names = string.Join(", ", verdicts.Select(Verdicts.Display));

            builder.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{code,3}  {names,-27}{remedy[0]}"));

            foreach (var more in remedy.Skip(1))
            {
                builder.AppendLine(new string(' ', 32) + more);
            }
        }

        return builder.ToString();
    }


    private static string RenderMutations()
    {
        var builder = new StringBuilder();

        builder.AppendLine("Mutation testing");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "check-mutations proves a repository's tests can fail. For each selected leg and each arm its registry "
            + "declares, it mutates exactly the text the arm names, in a worker copy of the leg's tree and never in the tree "
            + "itself; builds the arm's target; proves every object that depends on the site was rebuilt; runs the arm's test "
            + "binary whole; judges what failed against what the arm declares, as an exact set; and puts the site back, "
            + "checked by its hash. A BUILD-RED arm's mutation must instead stop the build at an object that depends on its "
            + "site, and its paired positive control must build.");
        builder.AppendLine();
        builder.AppendLine("  \"mutations\": {");
        builder.AppendLine("    \"registry\": \"tests/mutations/arms.registry\",");
        builder.AppendLine("    \"textDirectory\": \"tests/mutations/texts\",");
        builder.AppendLine($"    \"reportArgs\": [\"--gtest_output=xml:{{{MutationReport.Placeholder}}}\"]");
        builder.AppendLine("  }");
        builder.AppendLine();
        AppendKeys(builder, ConfigKeys.Of<MutationSettings>());
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"workers is {MutationSettings.DefaultWorkers} and runTimeFactor {MutationSettings.DefaultRunTimeFactor.ToString(CultureInfo.InvariantCulture)} "
            + "where left out. The registry and textDirectory are relative to the repository root, and carried by sync like "
            + "the rest of the tree: one inside the harness's own directory, which sync never carries but for its runner "
            + "actions, is refused, and so is one sync.neverTransfer, sync.exclude or worktrees.root keeps a sync from "
            + $"carrying - a worker is a copy a sync makes, and a host sweeps its own. reportArgs makes a test binary write a JUnit XML report to {{{MutationReport.Placeholder}}}, "
            + "a new file for every run: [\"--reporter\", \"JUnit::out={report}\"] for Catch2, [\"--reporters=junit\", "
            + "\"--out={report}\"] for doctest, [\"--logger=JUNIT,all,{report}\"] for Boost.Test. It is required where an arm runs "
            + "its binary, and only {report} may stand in braces. "
            + "runReads names, for each runner as the A rows name it, the files of the tree its runs read at run time - a data "
            + "file, a golden output, a plugin it opens - each a path, everything below a directory, or **/name at any depth: what "
            + "the sweep's reading of the tree holds of each is part of the key of every arm that runner runs. A runner given no "
            + "file, an entry outside the tree, one holding a '*' anywhere but a leading '**/', and one naming the whole tree "
            + "('.'), are refused; so is a runner no TEST-RED arm's A row names, when the registry is read.");
        builder.AppendLine();
        builder.AppendLine("The registry");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "The repository's own file: one row to a line, its fields separated by '|' and trimmed, a line whose first "
            + "character that is not blank is '#' a comment, nothing escaped, and the last field taking the rest of the line, "
            + "so a why may hold a '|'.");
        builder.AppendLine();
        builder.AppendLine("  A | arm | site | before | after | red kind | target | runner | cases | diag | why");
        builder.AppendLine("  C | arm | case | why                          a case the mutation must redden");
        builder.AppendLine("  G | arm | case | why                          a neighbour that must run and stay green");
        builder.AppendLine("  B | arm | control before | control after | why");
        builder.AppendLine("                                                a BUILD-RED arm's paired control");
        builder.AppendLine("  M | arm | site | before | after | why         another site, mutated, put back and");
        builder.AppendLine("                                                checked with the arm's own: another");
        builder.AppendLine("                                                file, or a further text of a file");
        builder.AppendLine("                                                the arm already mutates");
        builder.AppendLine("  S | arm | legs | why                          the legs it runs on, named as --legs");
        builder.AppendLine("                                                names them; without one, every leg");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"site is the file mutated. before, after, diag and a control's texts name files holding the text, so a mutation "
            + "may span lines: each read as its file holds it, less a UTF-8 byte order mark at its start and one line ending "
            + "at its end, and where the site ends its lines otherwise, given the site's line endings, so one registry serves "
            + "a checkout with either. red kind is "
            + $"{MutationRegistryParser.TestRed} or {MutationRegistryParser.BuildRed}, declared and never inferred. target is the "
            + "build target the mutation is built as, and runner the test target whose binary runs, found through the build's "
            + "own manifest; cases is how many cases it runs, skipped ones included. A case is named as its report names it, "
            + "classname.name: Suite.Case for GoogleTest. A BUILD-RED arm runs nothing: its runner is "
            + $"'{MutationRegistryParser.NoRunner}', its cases 0 and its diag {MutationRegistryParser.PairedControlToken}, and "
            + "it has exactly one B row, no C or G row, and no M row but for its own file, which its paired control rewrites "
            + "whole. A TEST-RED arm has at least one C row and no B row.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "A mutant that is several places of one file, apart from each other, is one arm: an M row naming a file its arm "
            + "already mutates - its own, or another M row's - adds a text to that file, as many as the mutant has, spelt as "
            + "the arm's first row for the file spells it. Every text of a file is replaced together, as one edit of it: each "
            + "must stand exactly once in the file as the tree holds it - never as another of them left it - and differ from "
            + "what replaces it, the file is written once and put back whole, checked once against the tree's, and what "
            + "depends on it is witnessed rebuilt as for one text. Declared as one text spanning every site, such a mutant "
            + "would break at any edit of a line between them. A further site in another file stays an M row naming that file.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"The whole registry is read before any host is touched, and every problem is listed with its line ({HarnessExit.ConfigInvalid}): "
            + "a row of no kind it reads; an arm id outside [A-Za-z0-9_-], or one another reads as ignoring case; a path outside "
            + "[A-Za-z0-9_./-], absolute, climbing out with '..', ending in '/', or with an empty or '.' segment; a target or "
            + "runner outside [A-Za-z0-9_.+-]; a row naming an arm no A row above it declares; a case both red and green, or "
            + "declared twice; an M row naming a file its arm already mutates under another spelling, as the tree's own file "
            + "system compares their names; two texts of one arm that overlap in one file as the tree holds it - one "
            + "lying across or within the other, or the same text cited twice; an S row naming neither a leg nor a leg set; "
            + "a text no row cites in textDirectory, a mutation nobody drives - but for a file there a sync withholds, which "
            + "no copy of the tree holds; a cited text that is not there, or that a "
            + "sync withholds from every copy of the tree - one sync.neverTransfer, sync.exclude or worktrees.root "
            + "covers, or git ignores - as the registry itself is refused where git ignores it; and a "
            + "before-text, a control's before-text or a diagnostic holding nothing, which would match everywhere, or be said "
            + "by every run. Rows R, X, I, F and T are refused, each naming what "
            + "took its place: the leg's own tree, project and variant (R); sync's exclusions (X); the variant's configure "
            + "(I); the dependency sources the leg's own build fetched, read from its CMake cache (F); and ninja's records of "
            + "every object that depends on a site (T).");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"--arms names the arms to drive, as --legs names legs, and an unknown one is refused ({HarnessExit.UsageError}) - "
            + "once the registry itself can be swept: its own problems are listed first, every one of them, whatever --arms "
            + "names. "
            + "On each leg the arms its S row does not name, and those --arms does not, are skipped-not-selected; an arm "
            + "selected whose S row names no selected leg is named in a warning before the sweep starts, and a sweep whose "
            + $"every selected arm is such a one is refused ({HarnessExit.UsageError}), naming each and where it runs: it would drive "
            + "no arm, and pass having proved nothing. Only a leg built by CMake with the Ninja generator can be swept: "
            + "ninja's own records say which objects a mutation rebuilt. "
            + "--list sweeps nothing (see 'What holds, and what is pending'), so it is refused with --pending, --self-test, "
            + $"--force-lock, --use-staged, --here and the hidden --held ({HarnessExit.UsageError}), each in one sentence naming both; and --pending "
            + "is refused with --self-test, of which nothing is ever recorded.");
        builder.AppendLine();
        builder.AppendLine("Workers");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"Each leg is swept in worker copies of its tree, kept in it as <tree>/{HarnessLayout.MutationsDirectoryName}/<key>{WorkerFamily.OwnMark}-<n>, numbered "
            + "from 1: as many as workers, never more than the leg has arms to drive, and one in a WSL distribution. The key "
            + $"is the first {VariantKey.CopyKeyLength} hexadecimal digits of the SHA-256 of the leg's variant, as its build "
            + $"directory is named: its first worker adds {ReportText.Counted(WorkerFamily.AddedToItsTreesPath, "character")} to its tree's path "
            + "whatever the variant is called, since a worker is built as deep as the tree and a machine whose paths are bounded "
            + "has no more to spare, and the lines of a sweep and of a clean say which leg a worker is. Each is a repository "
            + $"of its own, as every copy a sync makes is, and init keeps {HarnessLayout.MutationsDirectoryName} in git and "
            + "everything made in it out: a sweep in a tree where git does not ignore what it would make there is refused "
            + $"({HarnessExit.Refused}), naming init, with nothing made - asked on the machine that makes the workers, so a "
            + "host answers for its own copy. Before every "
            + "sweep each is synced again from one reading of the tree, by content - so its build stays warm, and a site a "
            + "killed sweep left mutated is put back - and built whole before it drives an arm: that sync withholds the "
            + "worker's own build directory itself, whatever sync.neverTransfer says, and sync.maxDeleteFraction judges "
            + "only what mirrors the tree the sweep read. Of the tree itself a sweep "
            + $"reads every file and writes none: its workers, and the claims on them, are in {HarnessLayout.MutationsDirectoryName}, "
            + $"and its run's records in {HarnessLayout.DirectoryName}, as every run's are. On this machine a sweep makes no copy "
            + "of its own even where the leg's localCopy "
            + $"or the repository's default ({HarnessLayout.LegsDirectoryName}) says one - its workers are already copies of "
            + "one reading - and reads the dependency sources the leg's build fetched from where the configuration places "
            + "that build: the copy's build directory, where the leg works in one. On another machine it works in a copy of "
            + $"its own there, <host's copy>/{HarnessLayout.LegsDirectoryName}/<key>, with its workers in it (see the locks below).");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "A worker builds the dependency sources the leg's own build fetched, read from its CMake cache "
            + $"({BuildDirectoryGuard.FetchContentSource}<NAME>), and never fetches where every one was found. They are read "
            + "once, as the tree is, and each worker is given its own copy by content, kept in its "
            + $"{HarnessLayout.DependencySourcesDirectoryRelative}/<name> - their files, never a clone's own .git, nor a link - "
            + "and configured with it: so a clean of the leg, or a build of it that fetches again, while a sweep runs "
            + "changes nothing a worker builds. Sources the tree itself holds are the worker's own copy of them, and what "
            + "the project's cacheVars point at is the project's to say. A dependency not found is fetched by the worker "
            + "as the leg would fetch it.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "A sweep runs the workers whose copy and build fit the room left - a copy being the tree's files and what git "
            + "keeps there of its history, and a build of the variant coming to the leg's "
            + "buildSpaceGiB where it declares one, else to what the leg's own build last recorded, else to the most any "
            + "other tree of the repository on its machine recorded of it - and whose build stays within the path limit, "
            + "reckoned as a worktree's is; where not even the first does, the leg is skipped-unavailable, saying why, and "
            + "one that runs fewer says how many of those it wanted, and what keeps each of the rest out. A worker is claimed while a sweep uses it, in <worker>.claim.json beside it in "
            + $"{HarnessLayout.MutationsDirectoryName}, and "
            + "a claim whose sweep died is released and said. Workers an earlier sweep left beyond the count workers allows "
            + "are removed before the sweep plans, so lowering it frees their room, and a directory under a worker's name "
            + "that no sync made is said and left.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "The workers go with their tree. clean removes a leg's with its build directory - its self-test's too, and "
            + "those of a variant no leg of that host and tree builds any more. delete-worktree and delete-agent "
            + "remove the workers kept in the worktree first, here and in each host's copy of it; a worker a "
            + "sweep still running holds keeps its worktree, or its host's copy, until the sweep has ended, and one that "
            + "cannot be removed keeps it as a failure until that is put right - or --force "
            + "deletes the worktree with that worker in it. A directory there under a worker's name that no sync made goes "
            + "with the worktree, or the host's copy, as everything else in it does, and is said. No copy of a tree carries "
            + "its workers, and a sync into a host's "
            + "copy never deletes that copy's own. Workers an earlier version of this tool kept beside a tree, as "
            + "<tree>.mutation-<key><mark>-<n>, are no workers to a sweep: nothing counts one, and clean --former-workers "
            + "alone removes them - beside each tree of the repository on each machine a selected leg runs on, here and on "
            + "hosts, and beside each one deleted since, a dry run listing each with its size - keeping one a sweep of that "
            + "version still claims by the claim "
            + "file beside it. Each holds a .git of its own that git records no worktree at, so until then one below the "
            + "worktrees root is said as any such directory: list-worktree warns of it, and delete-orchestrator, and "
            + $"delete-worktree of an orchestrator's directory with or without --force, refuse ({HarnessExit.Refused}) while one "
            + "is below it.");
        builder.AppendLine();
        builder.AppendLine("An arm's turn");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "Each test binary's arms wait for its unmutated control, built and run once on the leg: one that does not pass "
            + "- a build that does not pass, a red case, no report, a failing exit, a hang - decides the leg's own verdict "
            + "and stops each of its arms. "
            + "Its run bounds theirs: a mutated run may take runTimeFactor times as long, never less than it and a minute, "
            + "and is stopped as unattributed past that, naming which of the two set its bound. Each arm is admitted as a unit of its own where its machine "
            + "declares admission ('help admission'), pre-flighted, mutated - its sites dated past the worker's last build "
            + "- built with its runner beside its target, witnessed rebuilt from ninja's log, then run whole or paired with "
            + "its control. Every site is put back and checked by its hash; a site that cannot be put back poisons the arm "
            + "and retires its worker, and the other workers go on. What the sweep timed across a clock step or a host sleep "
            + "is said on the leg's line, among why its timings are suspect, and changes no verdict: an arm's run and the "
            + "unmutated run that bounds it, however each ended, and what each of its builds says of its own - a worker's "
            + "rebuilt from clean with it.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "Whatever fails is kept to what it failed in. A worker that cannot be made is retired alone, saying why, and "
            + "the other workers drive every arm. An arm whose driving ends in a failure is given the verdict that "
            + "failure comes to - poisoned where nobody named it - and a binary whose control cannot be built and run stops "
            + "its own arms and no other. Once its machine does not admit one arm, the sweep asks it for no other: every "
            + "arm left is not-admitted at once, naming the arm refused, rather than each waiting as long again.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "A sweep stopped part way puts every site back and still reports the leg: each arm judged by then, and the "
            + $"arms it was driving and those no worker reached stopped. The run ends interrupted ({HarnessExit.Cancelled}). A refusal "
            + "of the run raised inside a sweep ends the run with its own exit code, after the leg's line.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"A sweep none of whose selected legs can run is incomplete, exit {MutationService.NothingRuns}, saying why each cannot - "
            + $"never the {LegsExit.Unavailable} a build, a test and a run give there, which of a sweep is an arm violated.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "A sweep of a leg takes a lock of its own, keyed by its workers: it refuses another sweep of the leg's variant, "
            + "refused-locked, and never holds off a build, test or sync of the leg. --force-lock takes it, and a worker "
            + "another live sweep claims.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "On another machine a sweep never shares the host's copy of the tree, which every command whose leg lands there "
            + "syncs, and a test holds for as long as it runs: it works in a copy of its own, kept in that copy's "
            + $"{HarnessLayout.LegsDirectoryName} as <host's copy>/{HarnessLayout.LegsDirectoryName}/<key>, synced from this machine's reading of "
            + "the tree as the host's copy is, and holds it whole. So a test or build of the host's copy never holds a sweep "
            + "off, nor a sweep them, in either direction; a second sweep of the variant there is refused-locked before its "
            + "sync rewrites the copy; and where the host's copy is not there yet, or a sync of it did not finish, the tree is synced to it first, as any "
            + "command would, so every command finds the tree there - once a run, refused-locked while another run syncs it. "
            + "Its first sweep builds the variant there from clean. clean of the leg "
            + "removes the copy with its workers, and is refused-locked here while a sweep of this machine's holds it; "
            + "delete-worktree and delete-agent remove it with the host's copy, a worker a live sweep claims keeping that copy.");
        builder.AppendLine();
        builder.AppendLine("How a runner is started");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "A runner is started by the sweep itself, never by ctest. It is the program the runner's target builds, in the "
            + "worker's build directory, given mutations.reportArgs and nothing else. It starts in the directory the leg's "
            + "tests start in - test.workingDirectory, its names filled in for the worker, or the worker's root where the "
            + "settings name none - with the environment they start with: what the leg's host declares, the developer "
            + "environment its toolchain names, and test.env over both, the host's program directories added to its PATH.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "Nothing else of the test settings is applied: not the runner, its args, a filter, an exclusion, a label, the "
            + "core count or the success pattern. And nothing of a test's ctest entry is: not the add_test command line, "
            + "its launcher or its arguments; not ENVIRONMENT or ENVIRONMENT_MODIFICATION; not WORKING_DIRECTORY; not a "
            + "fixture (FIXTURES_REQUIRED, FIXTURES_SETUP, FIXTURES_CLEANUP), DEPENDS, REQUIRED_FILES, RESOURCE_LOCK or "
            + "RUN_SERIAL; not TIMEOUT - a mutated run is bounded by runTimeFactor, an unmutated one by "
            + "defaults.stallSeconds alone; and not what ctest reads a result by - WILL_FAIL, "
            + "PASS_REGULAR_EXPRESSION, FAIL_REGULAR_EXPRESSION, SKIP_REGULAR_EXPRESSION, SKIP_RETURN_CODE, DISABLED. A "
            + "case is red where the binary's own report says so.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "So a test that is green under ctest for what ctest sets up around it can be red in its binary's unmutated "
            + "run, which stops every arm of that binary, each arm's line naming the run's log. Where the leg's tests run "
            + "through ctest, the binary's line then names what ctest registers for the red cases that a sweep does not "
            + "apply, read from ctest's own description (ctest --show-only=json-v1), bounded by two minutes; what ctest "
            + "could not say is said as that and changes no verdict, and where the leg's tests run through no ctest "
            + "nothing is asked. A sweep starts the binary itself, never through ctest, and runs no fixture.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "A mutated build is a build of its own. Each mutation is configured and built again in its worker, so "
            + "whatever a project derives from its sources as it builds - a stamp that digests them, a version header, a "
            + "cache named for either - is another in every mutant than in the unmutated build. A case that depends on "
            + "such state is red under every arm of its runner, and no arm declares it.");
        builder.AppendLine();
        builder.AppendLine("What holds, and what is pending");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "A sweep records each arm's verdict on each leg with what it was judged on. A recorded passed holds where what "
            + "the arm would be judged on now, computed in full on the leg's machine, is what the record says; every other "
            + "arm is pending.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "An arm's key is the SHA-256 of what it was judged on, read from the first worker made before it drove an arm: "
            + "the judge's revision; the rows the sweep and the judge read of it - never a why, a comment, a line's number, "
            + "the letter case of its id or its S row; what the sweep's reading of the tree holds of each text it cites "
            + "and each site it mutates, found as the tree's file system compares names; what each target its build builds "
            + "is built from - its build lines, every file of "
            + "the tree they read, the dependency sources the worker was given, what the worker's configure made and what "
            + "they read outside the tree, the worker's own path told wherever it stands; the compilers CMake identified; "
            + "the leg's developer environment; what of the configuration the build, the run and the judge are given; "
            + "what its runner reads at run time, as runReads names it, each file by the path the reading spells, so one "
            + "renamed by case alone moves the key; and the host. No part is a commit, a date, a run, a "
            + "worker or a tree: the same content judged again, in another worker or another tree, comes to the same key. An "
            + "arm a part of whose key could not be read has none. A file a test binary reads at run time that runReads does "
            + "not name is read by no part of a key: editing it moves none, and a recorded passed it would change still holds. "
            + "Every TEST-RED arm is judged on what its test binary is built from, so after most changes under a large test "
            + "binary's sources every arm it runs is pending. What a target is built from is what ninja's own records name, "
            + "so two changes move no key until the object that reads them is compiled again: a file that stands earlier on "
            + "a search path than the one an unchanged #include found, and a file a command reads without declaring it. "
            + "ninja rebuilds nothing for either, and a sweep's workers are built by the same records, so a sweep judges the "
            + "stale object as build and test would; a worker built from clean - after a clean of the leg - sees both.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "Each verdict is recorded as its arm ends - one reached by observing the arm, and an arm stopped because its "
            + "runner's unmutated control did not pass - so a sweep stopped part way keeps what it had judged. Never an arm "
            + "its machine did not admit, one the sweep's own stop or a refusal of the run ended, one no worker reached, a "
            + "skipped one, or anything of a self-test: nothing was observed of those, and the record keeps what it held. "
            + $"The record is {HarnessLayout.MutationRecordDirectoryRelative} in the main checkout, whichever tree swept: "
            + $"<leg>.json for each leg, and the tables its entries name in {ArmVerdictRecord.TablesDirectoryName}/<leg>/, each "
            + "written once under its digest and removed with the last entry naming it. It ignores itself, and no sync "
            + "carries it: a host's copy keeps none, so a leg swept there tells this machine instead - each verdict as a "
            + "line on the host's standard error that this machine reads as the arm ends there and never relays, a sweep "
            + "stopped part way or cut off keeping what it had judged - and a sweep typed by hand in a copy the harness "
            + "synced records nothing, saying so once as a warning: under --pending it then holds nothing, and sweeps every "
            + "selected arm. A later verdict on a "
            + "key replaces the arm's entry on that key, whatever either was, and a verdict with no key the arm's entry with "
            + $"none; an arm keeps {ArmVerdictRecord.KeptPerArm} keys on a leg, the oldest by its place in the file going first, "
            + "never by its date. Two sweeps recording at once lose neither's verdicts. clean, a deleted worktree and a "
            + "removed host copy leave it as it was; deleting it is the one way to forget it, and every arm is then pending. "
            + $"A record that cannot be read, or that a newer {ToolPackage.Id} wrote, is never written: a sweep of that leg "
            + "says so once, as a warning naming the file and on the leg's line, and records none. A sweep this machine "
            + "records names the record once, in its lines and in --json, as a listing does.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "check-mutations --list says, without sweeping, each selected arm's last verdict on each selected leg its S row "
            + "names, and whether anything it was judged on that this machine reads has moved since. It reads and verifies "
            + $"the registry as a sweep does, every problem listed ({HarnessExit.ConfigInvalid}), resolves --legs and --arms as a "
            + $"sweep does ({HarnessExit.UsageError}), and reads each leg's tree once and each leg's record; it asks no host, takes "
            + "no lock, begins no run and writes nothing, and needs nothing only a sweep needs - report arguments, a leg "
            + "built with Ninja, a selection that drives an arm. A row gives the verdict shown - the newest entry nothing read "
            + "here moved for, else the newest - its run, when, and what the arm is now. It is pending where its record "
            + $"cannot be read or a newer {ToolPackage.Id} wrote it, its leg cannot be swept, it was never swept on the leg, the "
            + "verdict shown is no passed, it was swept with no key, or the judge's revision, its rows, a text it cites, a site "
            + "it mutates, a file of the tree its targets are built from, a file its runner reads at run time, what of the "
            + "configuration its build, its run or its "
            + "judge is given, or the host its leg names changed: the row says the first that applies, in that order, and "
            + "--json every one. A leg that names no host is read as though swept where its verdict was judged - which host "
            + "a sweep would place it on is not read here - so the environment its build is given is the one that host "
            + "declares. Every other arm is unmoved here, which is never to say it holds: a listing reads the rows, "
            + "the texts, the tree's files and the settings, never how a target is built, a file its build makes, the "
            + "dependency sources it fetched, a file outside the tree it reads, the compilers or the developer environment - "
            + "only a sweep on the leg's machine reads those. An arm unmoved here whose build is generated from a file that "
            + "moved since, or whose configure the configuration now gives otherwise, says so on its row. A leg that cannot "
            + "be swept is said once, above the rows, and never refused; a record that "
            + $"cannot be read is said as a warning. A listing exits {HarnessExit.Success} whatever is pending - it is no gate - "
            + $"and otherwise as a sweep would: {HarnessExit.CommandFailed} where git could not say what a tree ignores, and "
            + $"{LegExit.InputsMoved} where a tree changed while it was read. --json prints the listing as one JSON document. "
            + StoppedDocument);
        builder.AppendLine();
        AppendWrapped(
            builder,
            "check-mutations --pending drives, on each leg, only the arms with no recorded passed that holds; --arms narrows "
            + "the arms it weighs, and without --pending every selected arm is driven, as ever, and recorded. On the leg's "
            + "machine the first worker is made alone - claimed, synced, built whole - and the key of every arm is read from "
            + "its build before any arm is mutated. An arm whose key is a recorded passed's holds: it is not driven, and is "
            + "skipped-not-selected on that leg, its detail naming the run whose passed holds and when, and the record notes "
            + "that this run verified it. Every other arm is pending, and is swept as ever, in as many workers beyond the "
            + "first as the pending arms need; its progress and --json say the first reason it was pending - a listing's, or "
            + "one only a sweep reads: how a target is built, a file joined or left what it is built from, a file its build "
            + "makes, the dependency sources it fetched or a file outside the tree it reads moved, its compiler or its "
            + "developer environment changed, or what it would be judged on cannot be read now - and, where nothing else "
            + "says how its key moved, that it moved. A host sweeping one leg "
            + "of a run under --pending is given what the record holds of that leg's own arms - each arm's newest entry "
            + "and every recorded passed, as bytes in a hidden option of that leg alone - and decides held and pending "
            + "there, from its own reading of its copy and its own worker; given --pending and no record it holds nothing "
            + "and sweeps every selected arm. The leg's line counts the "
            + "arms swept by verdict, those that held, and those not selected. With nothing pending no other worker is made "
            + "and the leg passes on its own verdict, saying nothing is pending: each passed was verified there, and the tree "
            + $"asks nothing more of it. A sweep under --pending exits as a sweep does - {HarnessExit.Success} where nothing is "
            + "pending - and a first worker that cannot be made is the leg's failure, as any worker's is where none is made, "
            + "and verifies nothing.");
        builder.AppendLine();
        builder.AppendLine("Records");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"Each arm's records are in <run>/<leg>/{MutationRecords.ArmsDirectory}/<arm>/: {MutationRecords.ArmRecordFileName}, its verdict as "
            + "the ledger carries it, beside the logs of its build and its run, and of a paired control's build in "
            + $"{MutationRecords.PairedControlDirectory}/. Each control's are in <run>/<leg>/{MutationRecords.ControlsDirectory}/<runner>/, and "
            + $"each worker's whole build in <run>/<leg>/{MutationRecords.WorkersDirectory}/<n>/. A leg's line counts its arms by verdict; "
            + "ARMS, below the table, names each arm selected that did not pass, and why; --json carries every arm beneath "
            + "its leg: "
            + "arm, verdict, failure, detail, durationSeconds, worker, cases, declaredCases, reds, declaredReds, records and key; "
            + "under --pending an arm it left out has held - the run and the time of the passed that holds - and an arm it swept "
            + "has pending - the first reason, its code and the words it is said in - and each leg has pendingSweep: how many "
            + "of its arms held, and how many were swept; and each control that did not pass is beneath the leg as controls: "
            + "its runner, verdict, detail, reds, records, and what ctest registered for its red cases - how they were "
            + "matched, and what of the tests it names a sweep does not apply.");
        builder.AppendLine();
        builder.AppendLine("Self-test");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "check-mutations --self-test sweeps the fixture this tool carries, in place of the repository's registry, "
            + "which it does not need: a CMake library, a test binary that writes its own JUnit report, and eight arms, one "
            + "to each verdict an arm's design can reach on any machine - passed, violated, survived, unattributed and "
            + "failed - with a second that passes as the other red kind, a third whose mutation is coupled across two "
            + "files and a fourth whose mutation is two sites of one file. It is built as each selected leg builds, with the leg's toolchain, "
            + "configuration and sanitizer, and each arm is held to the verdict it is designed to reach: one that reaches "
            + "it passed, saying so, and one that reaches another is violated, naming both - a defect of this tool's with "
            + "that compiler, never the fixture's. --arms names the fixture's arms.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "Every worker a self-test makes reads each arm's key from its own build, and the self-test compares them arm by "
            + "arm, so it proves on the leg's own toolchain that two workers come to one key: a key two workers read "
            + "otherwise, or one a worker could not read, fails the leg as violated, naming the arm, the workers and the "
            + "part that differs; a self-test with one worker says it compared its keys with no other's. A self-test's "
            + "keys are on no arm's line.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"The fixture is written where this tool keeps its own data, <user data>/{ToolPackage.Command}/{MutationFixture.DirectoryName}, "
            + "only where it differs. Its workers are kept in the leg's own tree, as a sweep's are, in a family of "
            + $"their own - <tree>/{HarnessLayout.MutationsDirectoryName}/<key>{WorkerFamily.SelfTestMark}-<n>, a few megabytes each - so they are "
            + "that repository's: counted by its room, built by its toolchain, and removed by a clean of the leg, and "
            + "with its worktree or its host's copy. A self-test is placed by no room a build of the leg's tree needs, "
            + "its workers' paths are reckoned by the fixture's own longest, and a leg on a host is self-tested there "
            + "with the fixture that host's DssHarness carries.");
        builder.AppendLine();
        builder.AppendLine("Verdicts");
        builder.AppendLine();
        builder.AppendLine("Each arm reaches one, and a leg's is the worst of its own and its arms':");
        builder.AppendLine();
        builder.AppendLine("  passed        the arm held as declared");
        builder.AppendLine("  failed        its build failed at a step that is no object depending on its site - a");
        builder.AppendLine("                link, another object - or named no step that failed");
        builder.AppendLine("  violated      its declaration did not hold: a site or a cited text that is not there, or");
        builder.AppendLine("                a site spelt otherwise than the tree spells it, or that is no file the");
        builder.AppendLine("                sweep's reading of the tree holds; its before-text not in its");
        builder.AppendLine("                site exactly once, or replaced by itself; its target or its runner not");
        builder.AppendLine("                built, or no object they build depending on a site; other cases red than");
        builder.AppendLine("                its C rows, another number of cases run, a G row's case not run, or its");
        builder.AppendLine("                diagnostic not said; a TEST-RED mutation that does not compile; or a");
        builder.AppendLine("                BUILD-RED one that does, or whose control does not");
        builder.AppendLine("  survived      the mutation built and ran, and no case failed");
        builder.AppendLine("  unattributed  the run failed and nothing ties that to a case: no report, one that is no");
        builder.AppendLine("                JUnit report - its line says why - a failing exit whose report names no");
        builder.AppendLine("                failing case, or a run past its bound, or silent for");
        builder.AppendLine("                defaults.stallSeconds, stopped as hung");
        builder.AppendLine("  unwitnessed   the build passed and an object that depends on a site was not rebuilt");
        builder.AppendLine("  stopped       not driven to a verdict: the sweep was stopped, or ended by a refusal of the");
        builder.AppendLine("                run, while it was driven or before; its own build, or its paired control's,");
        builder.AppendLine("                was stopped from outside; no worker was left to run it; or the unmutated");
        builder.AppendLine("                run of its binary did not pass");
        builder.AppendLine("  not-admitted  its machine did not admit it, or an arm of the sweep before it");
        builder.AppendLine("  poisoned      a site could not be put back as it was, or a failure nobody named ended");
        builder.AppendLine("                its driving");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "An arm reaches others where nothing of its own decides: skipped-not-selected where --arms or its S row leaves "
            + "it out of a leg, or --pending leaves it out, its passed holding; skipped-unavailable, with every arm of its leg, where no worker fits the room left or the "
            + "path limit; unmeasured where ninja's log could not be read around its build, or the report its run wrote "
            + "could not be read from its file, which is no finding about the binary; whatever a guard of its "
            + "build reaches, as a leg's build does - inputs-moved, contended; and whatever a failure that ends its driving "
            + "comes to, as a leg's does - skipped-tool-missing for a tool that is not there, failed for a program that "
            + "will not start.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "A repository moving its sweep from a mutation harness of its own, which exited with the codes on the left, "
            + "reads these instead:");
        builder.AppendLine();
        builder.AppendLine($"  0  as declared                                {HarnessExit.Success,3}  passed");
        builder.AppendLine($"  1  a violation, a missing diagnostic included {LegExit.Violated,3}  violated");
        builder.AppendLine($"  5  nothing reddened                           {LegExit.Survived,3}  survived");
        builder.AppendLine($"  6  a red no case name could be read for       {LegExit.Unattributed,3}  unattributed");
        builder.AppendLine($"  7  a build that passed missed a declared unit {LegExit.Unwitnessed,3}  unwitnessed");
        builder.AppendLine($"  8  the build failed upstream of the unit      {HarnessExit.CommandFailed,3}  failed");
        builder.AppendLine($"  4  the harness broke                          {HarnessExit.InternalError,3}  poisoned");
        builder.AppendLine($"  4  an arm not driven                               stopped: the run exits {HarnessExit.Incomplete}");
        builder.AppendLine("                                                      unless something failed");
        builder.AppendLine($"  2  usage, or the registry not valid           {HarnessExit.UsageError,3}  usage, or {HarnessExit.ConfigInvalid} not valid");
        builder.AppendLine($"     a live owner                               {HarnessExit.Refused,3}  refused-locked");

        return builder.ToString();
    }

    private static string RenderAdmission()
    {
        var builder = new StringBuilder();

        builder.AppendLine("Heavy legs on one machine");
        builder.AppendLine();
        builder.AppendLine("A leg that builds or tests is heavy. A run's leg builds where its runner, or a runner");
        builder.AppendLine("its expected exceptions' run checks name, requires the build or runs a step or phase");
        builder.AppendLine("naming {product}, {buildDir} or a compiler, and is heavy, too, where one of them says");
        builder.AppendLine("\"heavy\": true or runs a step whose action says heavy: true. Either kind of step counts");
        builder.AppendLine("however the run comes to run it - by default, named with --manual-step or under a");
        builder.AppendLine("runner's steps, or needed by one that is - whichever runner starts it, and one");
        builder.AppendLine("limited by runOn builds, or makes heavy, the legs of those systems alone. A runner");
        builder.AppendLine("that only reads the tree is light, and starts at once.");
        builder.AppendLine("Where a machine declares admission, each heavy leg -");
        builder.AppendLine("its tree synced and its lock taken - waits for the machine to take it: first one of");
        builder.AppendLine("its slots, shared by every command this user runs there - worktrees each running a");
        builder.AppendLine("gate of their own, which maxParallelLegs, counted by one command, cannot see - given");
        builder.AppendLine("in the order legs asked; then the memory in use below the limit; then, where its");
        builder.AppendLine("build's need is known (buildSpaceGiB, or what a build recorded; 'help space'), room");
        builder.AppendLine("for it on the filesystem it fills beside what every other admitted leg there claims,");
        builder.AppendLine("each claim counted whole, so a leg may wait for another's whole work, its tests");
        builder.AppendLine("included. A leg holds its slot, and the room it claimed, until its work ends, and");
        builder.AppendLine("one whose command has ended - crashed, killed - is reclaimed by the next leg that");
        builder.AppendLine("looks, and said to be. A waiting leg keeps its lock: another run of its variant is");
        builder.AppendLine("refused-locked meanwhile, as it would be while the leg ran.");
        builder.AppendLine();
        builder.AppendLine("While a heavy leg builds, each filesystem its build fills is read again every");
        builder.AppendLine($"{RoomFloorWatch.Every.TotalSeconds:0} seconds, and the build is stopped once one has less free than minFreeGiB:");
        builder.AppendLine($"stopped, exit {HarnessExit.Incomplete}, naming what was free and where, and what it built left for");
        builder.AppendLine("clean - and so is a build that failed with one under it, read again as it failed,");
        builder.AppendLine("since a full disk says nothing of its code, and one whose own write failed then -");
        builder.AppendLine("a phase's log, its record. A build under it as it starts writes nothing, its");
        builder.AppendLine("record included, and a record it has no room left for is said, the one it wrote");
        builder.AppendLine("as it began standing. The room a leg claims is only what is said of its build; this");
        builder.AppendLine("holds every build, its need said or not - a sweep's workers' too. A WSL leg holds");
        builder.AppendLine("the drive its disk grows on as well, which this machine names as it sends the leg");
        builder.AppendLine("and the distribution reaches through its mount there: /mnt/c, as /proc/mounts");
        builder.AppendLine("lists it by the drive, or by the path its options name where an older WSL lists");
        builder.AppendLine("what it mounts as drvfs. Where it is mounted nowhere, the build says only the");
        builder.AppendLine("distribution's own room is held. A room that cannot be read stops nothing, and is");
        builder.AppendLine("said once.");
        builder.AppendLine();
        builder.AppendLine("A check-mutations leg, whose sweep can last hours, is admitted unit by unit instead:");
        builder.AppendLine("each worker as it is made, claiming the room its copy and build still need, and each");
        builder.AppendLine("arm as it starts, each unit holding a slot only while it runs. Only the sweep's first");
        builder.AppendLine("unit settles; the rest are read once. A sweep run in a WSL distribution is taken");
        builder.AppendLine("whole by the command that sends it there, and runs one worker.");
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
        builder.AppendLine("  minFreeGiB        the room a heavy leg's build leaves free on each filesystem it");
        builder.AppendLine($"                    fills, from 0 to {AdmissionSettings.MostMinFreeGiB}; 0 stops no build ({AdmissionSettings.DefaultMinFreeGiB})");
        builder.AppendLine();
        builder.AppendLine($"Seconds are at most {AdmissionSettings.MostSeconds}, and the wait at most {AdmissionSettings.MostWaitMinutes} minutes.");
        builder.AppendLine();
        builder.AppendLine("Declared under defaults, or under hosts.local or an ssh host, whose fields replace");
        builder.AppendLine("the defaults' one by one; a machine where neither declares one takes every leg at");
        builder.AppendLine("once. What a command's own configuration declares is what its legs are admitted by:");
        builder.AppendLine("a repository that declares none never joins the line, and where repositories that");
        builder.AppendLine("declare different counts share a machine, a leg starts only while it is fewer legs");
        builder.AppendLine("from the front than every count up to it allows. A WSL distribution runs on this");
        builder.AppendLine("machine: its heavy legs take this machine's slots, against its memory, and claim");
        builder.AppendLine("room on the drive where WSL keeps the distribution's disk, before they are sent");
        builder.AppendLine("there, and admission under hosts.wsl is refused - though a command typed");
        builder.AppendLine("inside a distribution keeps a record of the distribution's own, apart from those of");
        builder.AppendLine("the commands typed on Windows. An ssh host is a machine of its own, and takes the");
        builder.AppendLine("legs sent to it by its own section.");
        builder.AppendLine();
        builder.AppendLine("The memory in use is each system's own count of what it can no longer give: on");
        builder.AppendLine("Windows the commit charge against the commit limit, which grows with the page file;");
        builder.AppendLine("on Linux MemTotal less MemAvailable; on macOS 100 less the free share memory_pressure");
        builder.AppendLine("reports. A machine whose count could not be read in a leg's wait takes the leg");
        builder.AppendLine("without it - on its slot, and its room where its build's need is known - and its");
        builder.AppendLine("line says so; one that stops giving a reading it gave is read again, never taken on");
        builder.AppendLine("the reading it last gave. The room is read the same way: one never read in the wait");
        builder.AppendLine("lets the leg start, claimed against every filesystem of the machine, and one read");
        builder.AppendLine("before and not now decides nothing.");
        builder.AppendLine();
        builder.AppendLine("On Windows the commit counts what WSL's virtual machine keeps as page cache - what");
        builder.AppendLine("every distribution's builds read and wrote - which it gives back only once it has");
        builder.AppendLine("idled for minutes. So a leg about to wait on the memory has that cache dropped first,");
        builder.AppendLine("as root (sync, then drop_caches) in a running distribution a WSL host of its");
        builder.AppendLine("repository reaches - at most once a minute, whichever leg asks - and reads the");
        builder.AppendLine("memory again once what was dropped has come back, a minute later at the most, its");
        builder.AppendLine("line saying how much was dropped and the memory before and after, and goes on from");
        builder.AppendLine("that reading as from any other. A WSL leg sent from a machine that admits drops it");
        builder.AppendLine("as it ends, without waiting, its line saying so - or that WSL listed none of its");
        builder.AppendLine("distributions running, though the leg had just run in one. A distribution that is");
        builder.AppendLine("not running is never started for it, and a drop that could not be made is said");
        builder.AppendLine("once - a WSL host whose item cannot be read, where no other reaches one, or a list");
        builder.AppendLine("of the distributions running that cannot be read, among the reasons; that list is");
        builder.AppendLine("read in UTF-16 too, which an older wsl.exe writes whatever WSL_UTF8 says.");
        builder.AppendLine();
        builder.AppendLine("While it waits, a leg says who holds each slot - tree, variant, host, leg, command,");
        builder.AppendLine("process, run, and since when it asked, never the machine's name, which only the");
        builder.AppendLine("record keeps - and, once it holds one, what the memory stands at, or the room free");
        builder.AppendLine("and who claims it. It says so again, as it reads then and with how long it has");
        builder.AppendLine($"waited, at least every {LegAdmission.SaidAgainEvery.TotalMinutes:0} minutes - a poll longer than that is cut at it, and a");
        builder.AppendLine("settle longer than that waited whole in pieces of it - so a long wait never goes");
        builder.AppendLine("silent. Its line, and admission in --json, name how long it waited, the memory it");
        builder.AppendLine("started at and the room it claimed; --json also names the record it asked in. One");
        builder.AppendLine($"that waited maxWaitMinutes is not-admitted, exit {LegExit.NotAdmitted}, naming what held the slots and");
        builder.AppendLine("where they are recorded, the memory it waited on, or the room, who claimed it and");
        builder.AppendLine("where the claims are recorded: nothing of it ran, and nothing about the code is");
        builder.AppendLine("claimed. A wait with only legs of its own run ahead of it - holding slots, or in");
        builder.AppendLine("line first: a command's WSL leg behind its Windows legs, asked for at once - is");
        builder.AppendLine("certain to end and does not count: its line says they are its own, and a refusal");
        builder.AppendLine("after it says how much of the wait did not count. Another command's leg in line");
        builder.AppendLine("ahead makes it count, whoever holds the slots. On an ssh host, the legs one command");
        builder.AppendLine("sends there ask under that command's run.");
        builder.AppendLine();
        builder.AppendLine("The slots are kept in <user data>/dssharness/admission-<machine id>.json, and the");
        builder.AppendLine("room the legs claim beside them in admission-<machine id>.room.json - the user");
        builder.AppendLine("data being LOCALAPPDATA on Windows, ~/Library/Application Support on macOS, and");
        builder.AppendLine("$XDG_DATA_HOME or ~/.local/share on Linux - beside the hold that keeps the machine");
        builder.AppendLine("awake: one of each per machine and per user, whichever repository asks, named by the");
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
        builder.AppendLine($"  {ToolPackage.Command} {ToolUpdateService.CommandName}             Update this machine's {ToolPackage.Id}, while none runs here");
        builder.AppendLine($"  {ToolPackage.Command} sync                    Put each host's copy in step with this tree");
        builder.AppendLine($"  {ToolPackage.Command} build                   Build every selected leg");
        builder.AppendLine($"  {ToolPackage.Command} test                    Build and test every selected leg");
        builder.AppendLine($"  {ToolPackage.Command} run <runner>            Run a predefined runner across its legs");
        builder.AppendLine($"  {ToolPackage.Command} check-mutations         Prove each selected leg's tests can fail, arm by arm");
        builder.AppendLine($"  {ToolPackage.Command} clean                   Remove legs' builds, mutation workers and copies");
        builder.AppendLine($"  {ToolPackage.Command} up <name>               Bring a set-up up; '{ToolPackage.Command} down <name>' brings it down");
        builder.AppendLine($"  {ToolPackage.Command} list-worktree           Show worktrees and the copies hosts keep of them");
        builder.AppendLine($"  {ToolPackage.Command} list-orchestrator       Show orchestrators and where each agent stands");
        builder.AppendLine($"  {ToolPackage.Command} read-anchors            List the deferred work recorded as anchors");
        builder.AppendLine();
        builder.AppendLine("Every command accepts");
        builder.AppendLine("  -C, --directory <dir>              Operate on this directory (default: current)");
        builder.AppendLine("  -v, --verbose                      Show per-phase detail and child process output");
        builder.AppendLine();
        AppendWrapped(builder, StoppedDocument);
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
        builder.AppendLine("  legs, sync");
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
        builder.AppendLine("  build, test, run, check-mutations");
        builder.AppendLine($"    {LegExit.InputsMoved,3}  inputs-moved or unmeasured: let the tree settle, then run again; where the inputs");
        builder.AppendLine("         could not be watched, free or raise what the machine's reason names, or restore the tree");
        builder.AppendLine($"    {LegExit.Contended,3}  contended: wait for the other run");
        builder.AppendLine($"    {LegExit.Unwitnessed,3}  unwitnessed: find out what actually ran");
        builder.AppendLine($"    {LegExit.LogHeld,3}  log-held: find out which run still owns this leg's logs");
        builder.AppendLine($"    {LegExit.NotAdmitted,3}  not-admitted: wait for the heavy legs it names, free memory or room on the");
        builder.AppendLine("         filesystem it names, or raise the machine's limits ('help admission')");
        builder.AppendLine();
        builder.AppendLine("  build, test, run, clean");
        builder.AppendLine($"    {LegsExit.Unavailable,3}  no selected leg can run, and no failure turned one away");
        builder.AppendLine();
        builder.AppendLine("  check-mutations");
        builder.AppendLine($"    {LegExit.Violated,3}  violated: fix the arm's declaration, or the code it guards");
        builder.AppendLine($"    {LegExit.Survived,3}  survived: strengthen the test that should have failed");
        builder.AppendLine($"    {LegExit.Unattributed,3}  unattributed: contain the crash or hang in the case, or make the runner write");
        builder.AppendLine("         its report");
        builder.AppendLine($"    {MutationService.NothingRuns,3}  no selected leg can run, and no failure turned one away: incomplete, since");
        builder.AppendLine($"         {LegsExit.Unavailable} is an arm violated here");
        builder.AppendLine();
        builder.AppendLine($"  {FlowService.RunCommand}, {FlowService.ResumeCommand}, {FlowService.AnswerCommand}");
        builder.AppendLine($"    {FlowExit.Paused,3}  paused: a question waits - a step's, or the budget's at its reserve - for {FlowService.AnswerCommand}");
        builder.AppendLine($"         A session otherwise ends {HarnessExit.Success} done, {HarnessExit.CommandFailed} failed, {HarnessExit.Incomplete} failed where every failure that ended it");
        builder.AppendLine($"         was incomplete after its retries, and {HarnessExit.Cancelled} at a safe pause or a stop");
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
        builder.AppendLine("the compiler the answer names and no configure has failed since that answer - which");
        builder.AppendLine("the error index CMake 4 writes in an answer's place says - since a configure that");
        builder.AppendLine("identified the compiler again and then failed leaves a record of one nothing built with.");
        builder.AppendLine("Never by when either was written, which a clock that steps gets wrong; before CMake 4 a");
        builder.AppendLine("failed configure leaves nothing of itself, and the record is read as it names it.");
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
        builder.AppendLine("amd64)', and developerEnvironment in --json. A sync starts nothing there, and needs none.");
        builder.AppendLine();
        builder.AppendLine("Selected legs run at once and the command waits for all of them, reporting each");
        builder.AppendLine("live. Legs are chunked by the PHYSICAL machine they run on: this machine and every");
        builder.AppendLine("WSL distribution are one machine, because a distribution runs on it, and each ssh");
        builder.AppendLine("host is its own. defaults.maxParallelLegs caps how many run at once on any one");
        builder.AppendLine("machine, so a busy laptop is not asked for more than it has while the remote hosts");
        builder.AppendLine("sit idle; defaults.maxParallelLegsTotal caps the whole fleet on top of that, for");
        builder.AppendLine("what it shares even when its machines do not - a license server, a network share.");
        builder.AppendLine("Both are counted by one command; where a machine declares admission, its heavy legs");
        builder.AppendLine("take its slots, and claim its room, across every command this user runs there");
        builder.AppendLine("('help admission').");
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
        builder.AppendLine("Where a leg works");
        builder.AppendLine();
        builder.AppendLine("A leg a host runs works in that host's copy of its tree, whatever anything else");
        builder.AppendLine("says. A leg this machine runs itself works where the first of these says anything:");
        builder.AppendLine("--local-copy or --in-place on the command, for that run (build, test, run); a");
        builder.AppendLine("runner's writesTree, which is in place ('help runners'); the leg's own localCopy,");
        builder.AppendLine("true or false; defaults.localCopy, a default for every leg of the repository; and,");
        builder.AppendLine("where none says, its tree itself. A runner that writes the tree refuses");
        builder.AppendLine("--local-copy, and a run check whose runner writes it is refused wherever the leg it");
        builder.AppendLine("checks would work in a copy. A copy is kept in the tree's own");
        builder.AppendLine($"{HarnessLayout.LegsDirectoryName} slot, <tree>/.legs/<key>, one per tree and variant, made");
        builder.AppendLine("from the run's one reading of the tree and kept warm between runs: a write the tree");
        builder.AppendLine("receives while the leg runs - a fold, a commit, an edit - ends nothing of the leg's,");
        builder.AppendLine("and the leg ends on the tree its run read. The first build in a copy starts cold, and");
        builder.AppendLine("files git ignores are not in it. A build made in the tree is not the copy's:");
        builder.AppendLine("--no-build tests what is built in the copy, and --use-staged runs it on its copy as");
        builder.AppendLine("the last sync left it. A tree where git does not ignore what a copy would make in");
        builder.AppendLine($".legs refuses the run ({HarnessExit.Refused}), naming init, with nothing made.");
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
        builder.AppendLine("ssh looks the name up itself. For a host whose name is looked up here, no reason,");
        builder.AppendLine("relayed line or document names an address the name resolved to: wherever ssh, or a");
        builder.AppendLine("program there, names one as a word, it reads as the address declared. The name is");
        builder.AppendLine("looked up again, afresh, before every call that lets ssh look it up itself - one");
        builder.AppendLine("never pinned, though not through a ProxyJump or a ProxyCommand, or once its pin is");
        builder.AppendLine("dropped - so an address it has moved to is withheld too, wherever this machine's own");
        builder.AppendLine("lookup finds it as well.");
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
        builder.AppendLine("from source reports the same version as the published package. An install or update");
        builder.AppendLine("is said with whether the host then answered as this build ('updated DssHarness A to B,");
        builder.AppendLine("and it answers as B'), and what stopped it answering after one is said as coming after");
        builder.AppendLine("it ('updated DssHarness A to B, then the host could not be reached: ...').");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"This machine's own {ToolPackage.Id} is updated through '{ToolPackage.Command} {ToolUpdateService.CommandName}', never by a "
            + $"'dotnet tool update' typed beside running commands: an update beside a running {ToolPackage.Id} fails on Windows after "
            + "taking the tool away for ten seconds, and takes its files from under it elsewhere, and whatever anybody types "
            + $"meanwhile finds no tool (127, from the shell). {ToolUpdateService.CommandName} moves the installation up to the "
            + $"newest release nuget.org lists, from nuget.org alone, only while no other {ToolPackage.Id} process runs on the machine: "
            + $"with one running it changes nothing and names each by its process and command ({HarnessExit.Refused}), and with "
            + $"{ToolUpdateService.WaitOption} it waits for them to end, saying who it waits for as it starts and every "
            + $"{ToolUpdateService.SaidAgainEvery.TotalMinutes.ToString(CultureInfo.InvariantCulture)} minutes - but for a wait asked from inside "
            + $"a running {ToolPackage.Id}, which would never end and is refused, as is one where the process table names no "
            + "process's parent. Two updates of one place never change it together: the change holds a machine-wide lock on the "
            + "directory the tool is kept in, and an update that finds it held - two run beside the installed tool are each the program "
            + $"dotnet, and neither counts the other - changes nothing and is refused ({HarnessExit.Refused}), with "
            + $"{ToolUpdateService.WaitOption} or without, since it read the machine before the other changed it. "
            + "Once the update has begun this command does not stop it, interrupted or not, and reads back what it left: "
            + "it says the update done only once the tool is listed as that release, and a failure with what dotnet said and what "
            + $"is listed since, or why that is not known ({HarnessExit.CommandFailed}; {HarnessExit.Incomplete} where dotnet did not finish within "
            + $"{ToolPackage.ChangeBudget.TotalMinutes.ToString(CultureInfo.InvariantCulture)} minutes, an update begun and not finished, which running "
            + "it again can finish, as every program's update this tool makes answers it). Where nuget.org does not say which release "
            + "is the newest it says why - no route to it, no answer in time, an answer it cannot read - and changes nothing, "
            + "unless the copy asking is itself a release newer than the installed one, which it then moves to, saying so. A tool "
            + $"installed with dotnet's --tool-path is named with {ToolUpdateService.ToolPathOption}.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"Windows replaces no program while it runs, and '{ToolPackage.Command} {ToolUpdateService.CommandName}' is itself a "
            + $"{ToolPackage.Id} running: there it changes nothing and says the command that does, with the options it was typed "
            + "with - the door on every system, which is the newest release run beside the installed one, without installing it, "
            + $"its exit code the update's. With {ToolUpdateService.WaitOption}:");
        builder.AppendLine();
        builder.AppendLine($"  {ToolUpdateService.FromBeside(new ToolUpdateRequest(Wait: true))}");
        builder.AppendLine();
        builder.AppendLine("The DssHarness on a host is reached through a hidden host-agent command, with the");
        builder.AppendLine("request on standard input, held open while the host works: interrupting host-exec");
        builder.AppendLine("ends it, and the host cancels the command. The command line ssh hands a remote");
        builder.AppendLine("shell holds only fixed words, after a mark the shell writes first so that what its");
        builder.AppendLine("startup files print is dropped, and no argument is ever reinterpreted by sh, cmd or");
        builder.AppendLine("PowerShell. host-exec runs in the host's copy of the tree it is typed in, which");
        builder.AppendLine($"'{ToolPackage.Command} sync' creates and keeps in step with that tree - its files, and the");
        builder.AppendLine("commit its git HEAD names ('help config').");
        builder.AppendLine();
        // From the numbers both ends count by, so no sentence here can come to say another time than the host keeps.
        var patience = new HostAgentPatience();
        var beat = TimeSpan.FromSeconds(HostAgentProtocol.BeatSeconds);
        var silence = beat * HostAgentProtocol.BeatsMissed;

        static string Seconds(TimeSpan span) => span.TotalSeconds.ToString(CultureInfo.InvariantCulture);

        AppendWrapped(
            builder,
            "No leg, host-exec or sync's operation a host runs for this machine outlives the command that asked for it, "
            + "whatever carries it there; only what holds a host awake between commands (holdAwakeSeconds) is started to "
            + "outlast one. The host stops it when the input held open ends, which is how a command "
            + "interrupted or stopped here ends it at once; and, since an input that never ends says nothing, when the beat "
            + $"this machine writes on it every {Seconds(beat)} seconds has gone unheard for {Seconds(silence)}, which the host "
            + $"looks for every {Seconds(silence / HostAgentProtocol.SilenceLooks)}: a dispatcher killed where nothing closes "
            + "its end of the connection leaves a leg that is cancelled within "
            + $"{Seconds(silence + (silence / HostAgentProtocol.SilenceLooks))} seconds of its last beat, on an ssh host as in a "
            + "WSL distribution, and the host says why where this machine would read it. On Linux and macOS a session hung up "
            + $"cancels it the same way. What the host started that has not stopped {Seconds(patience.Unwind)} seconds after it "
            + "was cancelled - a command reading the output of something its child left running - is ended with its "
            + "processes, and the host's DssHarness with it, so the host is never held by a run nobody reads; a command that "
            + $"finishes what it began - a deletion, a fold, a hand-over - is given {Seconds(patience.Finishing)}, as it is "
            + "where it is typed and interrupted. Where this machine can no longer write to the connection while a host works, "
            + "it says so here, with why: the host hears no more of it, and stops what it was asked. To end a run on its hosts, "
            + "stop the command that dispatched it.");
        builder.AppendLine();
        builder.AppendLine("A host answering this machine writes its home as ~: in every line of DssHarness's");
        builder.AppendLine("own - a leg's reason, a lock or free-space message, a failure quoting git or the");
        builder.AppendLine("system - and in the paths its ledger holds, runDirectory and space among them, so");
        builder.AppendLine("this machine's 'logs of <leg> on <host>:' line names no account either. On a Windows");
        builder.AppendLine("host the separator after it stays the host's own, as in ~\\src\\app. A program's own");
        builder.AppendLine("lines, a phase's last lines among them, keep the home as it printed it; a command");
        builder.AppendLine("typed on the host names its paths in full; and under host-exec a document other than");
        builder.AppendLine("a ledger is written as the host prints it - but for an address the host's name");
        builder.AppendLine("resolved to, which reads as the address declared. A copy's path this machine builds");
        builder.AppendLine("from repositoryPath is said as that is written: '~/src/app' keeps the account out of");
        builder.AppendLine("it.");
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
        builder.AppendLine("typed in. The mutation workers a sweep of the leg keeps in that tree go with");
        builder.AppendLine("it - its self-test's too, and those of a variant no leg there builds any more -");
        builder.AppendLine("under the lock a sweep takes: one a live sweep claims is kept, one that cannot be");
        builder.AppendLine("removed fails the leg, which still says what went, and a directory under a worker's");
        builder.AppendLine("name that no sync made is said and left ('help mutations'). The copies of the tree");
        builder.AppendLine($"its legs work in, in the tree's {HarnessLayout.LegsDirectoryName} slot, go the same way - the leg's own variant's and");
        builder.AppendLine("those of a variant no leg builds any more, whatever the configuration says now -");
        builder.AppendLine("each under the lock a run takes on it: one a run is building in, or replacing, is");
        builder.AppendLine("kept and said - on a host, the copy a sweep works in there among them, with its");
        builder.AppendLine("workers. A host holding no copy of the tree holds nothing of it to remove.");
        builder.AppendLine();
        builder.AppendLine($"clean {CleanService.FormerWorkersOption} removes only the workers an earlier version of this tool");
        builder.AppendLine("kept beside the repository's trees, as <tree>.mutation-<key><mark>-<n>, once a machine:");
        builder.AppendLine("here beside the main checkout, every worktree, and each worktree deleted since in the");
        builder.AppendLine("worktrees root; on each host a selected leg runs on, asked of it from its home, beside");
        builder.AppendLine("its copy of the main checkout and each worktree's copy beside it, there still or removed");
        builder.AppendLine("since, whatever copy the host holds. With --dry-run it lists each with its size. One a");
        builder.AppendLine("sweep of that version still claims, by the claim file beside it, is kept,");
        builder.AppendLine("refused-locked; one no sync made is said and left. Where the worktrees, or those");
        builder.AppendLine("deleted since, cannot be listed, the leg fails, saying so, once the workers beside the");
        builder.AppendLine("trees it knows are dealt with.");
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
        builder.AppendLine("what each holds and removes nothing, and ends as the clean would: a leg a run is");
        builder.AppendLine("building, or whose workers a sweep holds, or whose copy a run holds, is");
        builder.AppendLine($"refused-locked in the dry run too ({HarnessExit.Refused}), with what each holds still said. --json carries");
        builder.AppendLine("all of it as each leg's 'space', with its mutation workers' as 'workerBytes' and its");
        builder.AppendLine("copies' as 'copyBytes': what was removed of them - 0 where every one there was kept -");
        builder.AppendLine("or, in a dry run, what is there. A leg whose build directory was not measured - a");
        builder.AppendLine("link, locked or not removable - carries a space saying its workers and copies alone,");
        builder.AppendLine("where any is there.");
        builder.AppendLine();
        builder.AppendLine("A host whose DssHarness is older than this machine's is updated first, as for any");
        builder.AppendLine("command, and the update needs room: a host that is both full and behind has to be");
        builder.AppendLine("freed by hand once.");
        builder.AppendLine();
        builder.AppendLine("A leg is placed only where its host has the room its build still needs, as it is");
        builder.AppendLine("only where the programs it starts are: a build that fills a disk dies half way, and");
        builder.AppendLine("takes any other leg building there with it. What a build needs is what its");
        builder.AppendLine("directory comes to once built - the leg's buildSpaceGiB, or, left out, what a build");
        builder.AppendLine("of its variant recorded as it finished: in this tree's copy, or else the most any");
        builder.AppendLine("other copy of the repository on that host recorded - the main checkout's, a");
        builder.AppendLine("worktree's, an agent's or a plain one, or a leg copy of any of them, named in the");
        builder.AppendLine("line - less what the directory already holds. Legs building on one filesystem of a");
        builder.AppendLine("host are counted together, in the order they were selected, since every build");
        builder.AppendLine("directory stays once built; one that does not fit beside those before it is");
        builder.AppendLine("skipped-unavailable:");
        builder.AppendLine();
        builder.AppendLine("  ssh vps: this leg needs ~11.4 GiB, what worktree o1/xa's copy of the same");
        builder.AppendLine("  variant came to there; 3.2 GiB free on '/'");
        builder.AppendLine();
        builder.AppendLine("A leg nothing has measured that declares no buildSpaceGiB is placed as it always");
        builder.AppendLine("was, and so is one whose directory no build of this version recorded. Nothing is");
        builder.AppendLine("walked to decide: the room is the filesystem's own count, and what a directory holds");
        builder.AppendLine("is what its build recorded. Commands that build nothing - sync, clean - need no room.");
        builder.AppendLine("A sweep of a leg's mutation arms builds in its first worker, never in the leg's own");
        builder.AppendLine("directory, and is placed by the room that worker's build still needs; a self-test");
        builder.AppendLine("builds no tree of the leg's, and is placed by none.");
        builder.AppendLine();
        builder.AppendLine("One command counts only its own legs. Where a machine declares admission ('help");
        builder.AppendLine("admission'), each heavy leg whose need is known also claims the room its build");
        builder.AppendLine("needs as it is let start, and any later heavy leg - of this command or another -");
        builder.AppendLine("waits until its own need fits beside every claim on that filesystem.");
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
            + "worktrees are below it, or a directory holding a .git of its own that git records no worktree at - what a "
            + "removal left, a person's to look in and delete, or a worker an earlier version of this tool kept beside an "
            + $"agent's worktree, which clean --former-workers removes - --force or not ({HarnessExit.Refused}), nor any directory with no .git of its own while git "
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
            "The copies kept in a worktree go before it, of its two families: the mutation workers its sweeps made "
            + $"('help mutations'), in {HarnessLayout.MutationsDirectoryName}, and the copies of it its legs work in "
            + $"('help legs'), in {HarnessLayout.LegsDirectoryName} - never sent to any host, so only this machine holds "
            + "them. They are asked about before anything "
            + $"of it is removed or looked through: one a sweep still running holds - or a run holds a leg copy - keeps the "
            + $"worktree ({HarnessExit.Refused}), "
            + $"and one that cannot be removed, or told for the harness's, keeps it as a failure ({HarnessExit.CommandFailed}), as "
            + "workers that cannot be looked for do, with nothing removed. A run holds a leg copy however either spells it: "
            + "the run lock tells a tree on this machine by where it leads, every link followed, whichever way the run holding "
            + "it spelled it - a build before this one's included - so a run in a worktree kept on another disk through a "
            + $"linked worktrees root, or in a copy kept through a {HarnessLayout.LegsDirectoryName} that is a link, keeps it "
            + "from a deletion that reaches it the other way. A sweep "
            + "that takes one meanwhile keeps the worktree as "
            + "well, and so does one whose removal then fails, the others gone by then, which is said. An interruption as they go "
            + $"says which went, and that nothing else of the worktree was removed ({HarnessExit.Cancelled}). --force deletes the "
            + "worktree with every copy in it, one held or one that could not be removed among them, saying which went with "
            + "it. A worktree whose own removal then fails, or is stopped, says which copies went before it. In a host's copy "
            + "the workers are asked about first as well, and a copy kept once some had gone says which went.");
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
        builder.AppendLine("a leg this machine runs there takes, taken with everything kept inside the copy, so");
        builder.AppendLine("a sweep this machine runs there keeps it as a run does, none starts there while it");
        builder.AppendLine("goes, and its marker goes last, so a removal that stops part way leaves the rest");
        builder.AppendLine("still the harness's to remove. A host is reached through the worktree's own");
        builder.AppendLine("configuration, read before it goes, or else through the one the command runs in; one");
        builder.AppendLine("neither declares is not asked, and its copy is forgotten, and named.");
        builder.AppendLine("A copy that cannot be removed now stays recorded, and the worktree is deleted all the");
        builder.AppendLine("same, but the command fails, naming each, with the highest code among them:");
        builder.AppendLine($"{HarnessExit.Refused} where a run or a sweep holds one or it was refused, {HarnessExit.HostUnavailable} where its host cannot");
        builder.AppendLine($"be reached, {HarnessExit.CommandFailed} where removing it failed there; or {HarnessExit.Cancelled} when interrupted. Running");
        builder.AppendLine("delete-worktree again removes what it left. For a name whose worktree is gone, it");
        builder.AppendLine("removes what any worktree of that name left, and never the copies of one that still");
        builder.AppendLine("exists.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"{WorktreeService.ListCommand} lists each worktree with the copies this machine records hosts keeping of it, and the "
            + "copies left by worktrees that are gone - removed by hand, or by another tool - each with the "
            + $"{WorktreeService.DeleteCommand} that deals with them. With --hosts it also asks each declared host which worktree "
            + "copies it keeps beside its repositoryPath, and how large each is, and sets them against that record: "
            + "a copy of a worktree here, one left by a worktree that is gone, one the record does not hold - made "
            + "from another checkout or machine, or forgotten here, which deleting a worktree never reaches - and one "
            + "recorded that is not there. A host that cannot be asked is named, and the command fails with the "
            + $"highest code among them, {HarnessExit.HostUnavailable} where a host cannot be reached. --json prints the "
            + $"listing as one JSON document. {StoppedDocument}");

        return builder.ToString();
    }

    /// <summary>
    /// The agent sections of config.json: the companies whose models the tool reaches, their keys in the git-ignored
    /// slot, the tiers a step runs on and the effort forms - said where a person reads them, before the clients that
    /// reach the companies are built (nothing here runs a step).
    /// </summary>
    private static string RenderAgentClients()
    {
        var builder = new StringBuilder();

        builder.AppendLine("Agent clients");
        builder.AppendLine();
        builder.AppendLine("The companies whose models this tool reaches are declared in config.json, under");
        builder.AppendLine("providers, and nothing of a model is in the tool's code: a model's entry says what it");
        builder.AppendLine("takes (its efforts), what it holds (its context and its output) and what it costs (its");
        builder.AppendLine("prices, with the date they were read); a model's code is listed once across every");
        builder.AppendLine("provider, so naming a model is enough - its code says its provider, its API's address,");
        builder.AppendLine("its keys' names and its client.");
        builder.AppendLine();
        builder.AppendLine("A provider's keys are a list, apiKeyNames: the names of the variables that hold its");
        builder.AppendLine($"keys in the git-ignored slot {HarnessLayout.WorkflowsDirectoryRelative}/.env/, at least one, in the");
        builder.AppendLine("order they are tried - a name and never a key, and a second key of one company is a second");
        builder.AppendLine("name in the list. A client that signs in with the machine's login - claude-code - reads no");
        builder.AppendLine("key at all. Choosing a key is the engine's, when it runs a run's steps, and no command of");
        builder.AppendLine("this build runs a step: a step is to start on the first key the slot holds, unless");
        builder.AppendLine("balanceKeyUsage is on (off unless turned on), where it is to start on the one that has spent");
        builder.AppendLine("least in the last seven days, counted back from the moment it starts, its conversation");
        builder.AppendLine("staying with one account. What a key spent is to be what the engine counts for each request");
        builder.AppendLine("served with it, at the configuration's prices, recorded by the key's name in the");
        builder.AppendLine("repository's records; what the same key spends elsewhere - another repository, another");
        builder.AppendLine("machine, another tool - is not seen.");
        builder.AppendLine();
        builder.AppendLine("The slot itself is a directory of dotenv files, " + HarnessLayout.WorkflowsEnvSlotRelative + "/:");
        builder.AppendLine("every file in it is read and merged, a name declared in two of them is refused, a line whose");
        builder.AppendLine($"name is no variable's name ({Secrets.AgentKeyStore.VariableNameShape}) is refused by its line number alone, for it");
        builder.AppendLine("may be part of a key, and no key is ever read from the process environment - a CI machine");
        builder.AppendLine("writes the file. A sample for the providers init declares, none filled in:");
        builder.AppendLine();

        foreach (var line in SlotSample())
        {
            builder.AppendLine(line);
        }

        builder.AppendLine();
        builder.AppendLine($"'{ToolPackage.Command} list-agent-providers' says what your configuration appoints and");
        builder.AppendLine("whether the slot holds it, present or missing, never a value; 'list-agent-providers");
        builder.AppendLine("--template' prints the agent sections as init writes them into a new config, to paste into");
        builder.AppendLine("an existing one.");
        builder.AppendLine();
        builder.AppendLine("A provider's name and each tier's are held to the orchestration name rule - lower-case");
        builder.AppendLine("letters and digits, single hyphens - and a tier is one name in one case everywhere; a");
        builder.AppendLine("model's code, what is sent to the API, is one word, and its name is the one a person reads.");
        builder.AppendLine();
        builder.AppendLine("The tiers a step runs on are tierConfig's: each tier its models in the order they are");
        builder.AppendLine("tried, the first serving a step on the tier and the rest its fallback. A step's effort is");
        builder.AppendLine("its own - one level for every model of its tier, or a map giving each its own: all for the");
        builder.AppendLine("rest, a model's code for one. Each model's level is checked against what that model takes");
        builder.AppendLine("when the definition is checked, a level it does not take refused naming the level to give");
        builder.AppendLine("it instead; validate-workflow says, under the line that says a definition holds, every");
        builder.AppendLine("step's models and levels, each provider's prices for the models its steps may run on and");
        builder.AppendLine("the date they were read, and each key a step's model would read that the slot lacks, with");
        builder.AppendLine("the steps that would read it.");
        builder.AppendLine();
        builder.AppendLine("This build checks configurations and definitions against these sections and writes them with");
        builder.AppendLine($"init (and '{ToolPackage.Command} init' writes the prices it holds, as read on their date).");
        builder.AppendLine();
        builder.AppendLine("The Claude Code this tool uses is the one npm installs, and no other: npm's is the one");
        builder.AppendLine("that is installed, brought up to date and driven from a command line, and a native");
        builder.AppendLine("launcher, a Homebrew or WinGet package and any other claude on the PATH is never started,");
        builder.AppendLine("updated or fallen back to. The tool asks npm where its global packages are and starts the");
        builder.AppendLine("program the package names, by its full path and never a launcher; on Windows npm.cmd is a");
        builder.AppendLine("batch file, so the tool does what that file does and starts npm's own npm-cli.js with node.");
        builder.AppendLine("A run that needs a Claude model through the claude-code client needs npm, and no other run");
        builder.AppendLine("does: where npm has no Claude Code, or one older than the tool needs, a run that needs it");
        builder.AppendLine("will install or update it first, with");
        builder.AppendLine($"'npm install -g @anthropic-ai/claude-code@{Agents.ClaudeCode.ClaudeCodeUpdateService.Needed}' -");
        builder.AppendLine("never with sudo, never 'npm update -g', never past the version needed, never beside a");
        builder.AppendLine("session of that installation that is running (its program, node running its script, a");
        builder.AppendLine("claude node runs, or a claude whose program the machine will not say, such as another");
        builder.AppendLine("user's on Linux; a claude installed any other way whose program it names does not count,");
        builder.AppendLine("and a process table read by names alone refuses the change), and never a program the slot");
        builder.AppendLine("names (CLAUDE_EXECUTABLE: a person's own choice, started as named; a slot that cannot give");
        builder.AppendLine("it is refused, never passed over for npm's).");
        builder.AppendLine("After a change, whether it ran or failed, what npm holds since is said. A client's update");
        builder.AppendLine("key (agentClients.<client>.update, true where nothing is said) and DISABLE_UPDATES in the");
        builder.AppendLine("tool's own environment (a setting of the person's, never a key) turn installing and");
        builder.AppendLine("updating off, and a run that then needs one that is missing or too old will be refused with");
        builder.AppendLine("the command to run by hand. The clients that reach the companies, and the step runner that");
        builder.AppendLine("readies Claude Code before a run's first step, are what it builds next, and nothing here");
        builder.AppendLine("runs a step.");

        return builder.ToString();
    }

    /// <summary>
    /// The sample file for the slot, from the names the providers the configuration init writes appoint: one comment
    /// naming the file, then each appointed name with nothing after its <c>=</c> but a comment saying what its value
    /// is - the shape a person copies, holding no value.
    /// </summary>
    private static IReadOnlyList<string> SlotSample()
    {
        return
        [
            "  # " + HarnessLayout.WorkflowsEnvSlotRelative + "/keys.env",
            .. Agents.AgentProviderTemplates.Read().Providers.SelectMany(provider =>
                provider.ApiKeyNames.Select(name => $"  {name}= # {DssHarness.Core.Secrets.AgentKeyStore.WhatHolds(provider)}")),
        ];
    }

    private static string RenderAnchors()
    {
        var defaults = new AnchorSettings();
        var rules = AnchorIdRules.From(defaults);
        var bookkeeping = AnchorStatus.ClosedMark + AnchorStatus.BookkeepingMark;
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
        builder.AppendLine("No verdict is read from the prose beside the Status, unless anchors.triggerCarriesVerdict");
        builder.AppendLine("is true: then a closed row's Trigger opens with the closed mark and no other row's");
        builder.AppendLine("does, write-anchor and set-anchor refuse a row whose two cells disagree, and");
        builder.AppendLine("read-anchors --lint and check-anchor-balance report one. A closed row's Trigger may");
        builder.AppendLine($"open with {bookkeeping} instead, the closed mark and then the bookkeeping mark, when its");
        builder.AppendLine("closure only repairs the mark of work done before the change that closes it.");
        builder.AppendLine("Whatever the setting, a closed row whose Trigger opens with the two marks where they");
        builder.AppendLine("are not read as the pair - the setting is off, or emphasis stands between them - is");
        builder.AppendLine("noted, failing nothing: by read-anchors --lint wherever it is, and by");
        builder.AppendLine("check-anchor-balance where it is a closure the change made.");
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
        builder.AppendLine("read-anchors --lint shows what to repair. A directory where a registry's file");
        builder.AppendLine("belongs, or a file that cannot be read, is refused too, by init as well.");
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
        builder.AppendLine("across both registries, so moving a row counts as nothing. A base that has moved on");
        builder.AppendLine("since HEAD left it is measured from where HEAD left it, as check-anchor-citations");
        builder.AppendLine("--current-pr measures a branch, so its own later changes are never counted as the");
        builder.AppendLine("change's; during an unfinished merge, HEAD is taken merged with what the merge brings");
        builder.AppendLine("in, as the commit finishing it would be. A shallow clone needs its history back to");
        builder.AppendLine("where the two part, unless either commit names the other as a parent, as a pull");
        builder.AppendLine("request's merge commit names the tip it merges into. It fails when the change creates");
        builder.AppendLine("more anchors than its work closes. An anchor newly disclosed records debt that already");
        builder.AppendLine("existed, so it is not counted; where anchors.triggerCarriesVerdict holds, a closure");
        builder.AppendLine($"whose Trigger opens with {bookkeeping} records work that already existed, so the anchor leaves");
        builder.AppendLine("the open count and the change is credited with nothing for it. Nor is it for taking");
        builder.AppendLine("away the open row of an anchor that had a closed row already where the change began,");
        builder.AppendLine("which is noted. It also fails when a closed anchor is in the pending registry, a live");
        builder.AppendLine("one is in the done registry, a Status is none of the four spellings, an id has more than");
        builder.AppendLine("one row, an anchor open or closed where the change began is in neither registry now (a");
        builder.AppendLine("row moves between them, and is never deleted, so a lost one that was open is not");
        builder.AppendLine("credited either; one whose Anchor cell named no id, or more than one, is noted instead,");
        builder.AppendLine("since repairing that cell changes the id it reads as), a row states two verdicts where");
        builder.AppendLine("anchors.triggerCarriesVerdict holds, or a registry is malformed. A registry that did");
        builder.AppendLine("not exist where the change began counts as empty there, and one malformed there is");
        builder.AppendLine("noted, since a row it hid was not counted there; one git ignores has no history, and");
        builder.AppendLine("is refused.");
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
        builder.AppendLine($"take it within {NamedMutexAnchorRegistryLock.DefaultTimeout.TotalSeconds:0} seconds writes nothing and exits {HarnessExit.Refused}. A read takes it");
        builder.AppendLine("too, while it reads both files, so a row being moved between them is never read in");
        builder.AppendLine($"neither or both; one that cannot take it in time reads nothing and exits {HarnessExit.Refused} too. A lock");
        builder.AppendLine("that belongs to another user refuses the same way, and one the system will not open");
        builder.AppendLine($"exits {HarnessExit.HostUnavailable}.");
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
        builder.AppendLine("       the registry is ignored by git, the lock is held or belongs to another user,");
        builder.AppendLine("       anchors.citationRoots declares no root, a file in a root is not named in UTF-8,");
        builder.AppendLine("       or HEAD names no commit for check-anchor-balance or check-anchor-citations");
        builder.AppendLine("       --current-commit or --current-pr");
        builder.AppendLine($"  {HarnessExit.HostUnavailable,3}  the machine-wide lock on the registries could not be opened");
        builder.AppendLine($"  {HarnessExit.CommandFailed,3}  a registry is malformed, is a directory or cannot be read, the base");
        builder.AppendLine("       names no commit or shares no history with HEAD, a shallow clone's history may stop");
        builder.AppendLine("       before where the two part, git lists a file it cannot read, or the directories at");
        builder.AppendLine("       the top of the tree cannot be listed");
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
                ($"  {OrchestratorLayout.AgentsDirectoryName}/<agent>/{OrchestratorLayout.SharedDirectoryName}/<sha256>", "the bytes of each file it shares, kept to merge from"),
                ($"  {OrchestratorLayout.AgentsDirectoryName}/<agent>/{OrchestratorLayout.RowsDirectoryName}/<ID>/", "an anchor row it files, a file for each cell"),
                ($"  {OrchestratorLayout.AgentsDirectoryName}/<agent>/{OrchestratorLayout.AppliedRowsFileName}", "the rows its folds applied, each as declared then"),
                ($"  {OrchestratorLayout.AgentsDirectoryName}/<agent>/{OrchestratorLayout.EvidenceDirectoryName}/", "its evidence roots' files, kept when it is deleted"),
                ($"  {OrchestratorLayout.AgentsDirectoryName}/<agent>/{OrchestratorLayout.SessionsDirectoryName}/<id>/", "API clients only: one conversation with the provider, in the provider's own form"),
                ($"  {OrchestratorLayout.AgentsDirectoryName}/<agent>/{OrchestratorLayout.StepsDirectoryName}/<instance>/", "one step the agent runs, or that runs in or for its worktree: everything of the step, its handoff.md where one stopped it"),
                ($"  {OrchestratorLayout.StepsDirectoryName}/<instance>/", "one step of the run that no agent owns, the create-agent that makes an agent included"),
                ($"  <step>/{OrchestratorLayout.ChildrenDirectoryName}/<session>{OrchestratorLayout.ChildExtension}", "in either kind of step, on Linux and macOS: a session a contained child of the step leads while it runs"),
                ($"  {OrchestratorLayout.FlowFileName}", "a run's state in one file - the truth a resume goes on from"),
                ($"  {OrchestratorLayout.WorkflowStatusFileName}", "a run's status for a person: each step of its definition, its instances, progress and what it waits on"),
                ($"  {OrchestratorLayout.DefinitionFileName}", "the workflow's definition as the run began with it"),
                ($"  {OrchestratorLayout.QuestionsFileName}", "every question the run raised, its answers, and who gave each"),
                ($"  {OrchestratorLayout.PreExistingIssuesFileName}", "each pre-existing issue the run listed, proven"),
                ($"  {OrchestratorLayout.EventsFileName}", "the run's history, one event to a line, written after the state it tells of"),
                ($"  {OrchestratorLayout.ReportFileName}, {OrchestratorLayout.ReportJsonFileName}", "the report of the run's last session"),
                ($"  {OrchestratorLayout.InboxFileName}, {OrchestratorLayout.StopFileName}", "an answer, and a pause or a stop, handed to the engine running the run"),
                ($"  ../<run>{OrchestratorLayout.OwnerFileName}", "beside the run's directory: the process whose engine holds the run"),
            ]);
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"A row's cells are {string.Join(", ", AgentRows.RequiredCells.Select(cell => cell + AgentRows.CellExtension))}, and "
            + $"{AnchorCellNames.Priority}{AgentRows.CellExtension} where it declares one, each read as write-anchor reads a cell file. "
            + "An agent's worktree is <worktrees.root>/<orchestrator>/<agent>, which list-worktree and delete-worktree name "
            + "orchestrator/agent; the copies hosts keep of it are named orchestrator--agent. An agent can also be made with no "
            + $"worktree ({AgentService.CreateCommand} --no-worktree), reading the main tree and writing only its scratch: the engine "
            + "makes one for a read-only reason step, and a person has no use for one by hand. It records no session - --session "
            + "beside --no-worktree is a usage error - and delete-agent marks it deleted in one write, folding nothing, removing no "
            + "worktree and looking for no transcript; --discard-uncommitted and the allowances have nothing to act on there, and "
            + "it says so. Agents write their handoffs, pause "
            + "and stop points as .md files in their own directory. Every command that writes an agent's records names where they are "
            + "on each way out once it has: its record, its seed, the directory of the bytes it shares where it keeps any, and the rows "
            + "its folds applied - each that is there.");
        builder.AppendLine();
        builder.AppendLine("Commands");
        builder.AppendLine($"  {command} {OrchestratorService.CreateCommand} <o> --model <id> [--parallel <n>] [--session <id>]");
        builder.AppendLine($"  {command} {AgentService.CreateCommand} <o> <a> --model <id> [--empty | --no-worktree] [--session <id>]");
        builder.AppendLine($"  {command} {AgentService.SeedCommand} <o> <a> [--empty] [--force]");
        builder.AppendLine($"  {command} {AgentService.RefreshCommand} <o> <a> [<path>... | {RefreshRequest.AllOption}] [{RefreshRequest.ExceptOption} <path>]...");
        builder.AppendLine($"      [{RefreshRequest.StaleOption} <path>]... [{FoldAllowances.MergeOption} <path>]... [--apply] [--json]");
        var paths = $"[{FoldAllowances.MergeOption} <path>]... [{FoldAllowances.SettledOption} <path>]...";
        builder.AppendLine($"  {command} {AgentService.RebaseCommand} <o> <a> [--apply] [--json] {paths}");
        var rows = $"[{AnchorBatchRequest.NewOption} <ID>]... [{AnchorBatchRequest.AcceptLostOption} {AnchorRowCell.Form}]...";
        var over = $"[{AnchorBatchRequest.AcceptOverOption} {AnchorRowCell.Form}]...";
        builder.AppendLine($"  {command} {AgentService.FoldCommand} <o> <a> [--apply] [--json] {paths}");
        builder.AppendLine($"      {rows} {over}");
        builder.AppendLine($"  {command} {AgentService.DeleteCommand} <o> <a> [--apply] [--json] {paths}");
        builder.AppendLine($"      {rows} {over}");
        builder.AppendLine("      [--discard-uncommitted]");
        builder.AppendLine($"  {command} {OrchestratorService.ListCommand} [<o>] [--steps] [--questions] [--json]");
        builder.AppendLine($"  {command} {OrchestratorService.DeleteCommand} <o> [--delete-evidence]");
        builder.AppendLine($"  {command} {FlowService.RunCommand} <workflow> [--run <name>] [--input <k=v>]... [--json]");
        builder.AppendLine($"  {command} {FlowService.ResumeCommand} <run> [--json]");
        builder.AppendLine($"  {command} {FlowService.AnswerCommand} <run> <answer> [--question <id>] [--text <text> | --text-file <file>]");
        builder.AppendLine("      [--again] [--json]");
        builder.AppendLine($"  {command} {FlowService.PauseCommand} <run> [--all] [--json]");
        builder.AppendLine($"  {command} {FlowService.StopCommand} <run> [--now] [--json]");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"{OrchestratorService.ListCommand} lists the orchestrators, or the one named, their agents and where each stands, "
            + "and the worktrees below an orchestrator's directory that no agent records; for a run, its own lines from flow.json "
            + "and what its engine is doing - none, running, pausing while its handoffs are written, recording the machine steps a "
            + "complete pause left running, or gone, an owner recorded whose process has ended - with each step instance (--steps: "
            + "its state, visit, attempt, agent and directory) and each question and what became of it (--questions). --json prints "
            + $"the listing as one JSON document. {StoppedDocument}");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"A run is an orchestrator a workflow's engine drives. {FlowService.RunCommand} validates the workflow and makes the run - "
            + "r<N>, one more than the highest there, or the name --run gives, held to worktrees.maxNameLength and refused where it is "
            + "taken - with its record, its state (flow.json), its status (workflow.json), its questions (questions.json), its "
            + "pre-existing issues and its events, and claims it for its process (<run>.owner.json beside the run's directory). "
            + "Only one run of a repository is unfinished with work in the tree at once: a start, or a session of another run, is "
            + $"refused ({HarnessExit.Refused}) while one is, and so it is where an agent's record of another run cannot be read. Each "
            + $"session drives the run until nothing more can run: done ({HarnessExit.Success}), failed ({HarnessExit.CommandFailed}, or "
            + $"{HarnessExit.Incomplete} where every failure that ended it was incomplete after its retries), paused on a question "
            + $"({FlowExit.Paused}), or paused or stopped on request ({HarnessExit.Cancelled}); it writes the run's report (report.md and "
            + "report.json) and names each record it wrote, in text and in --json, whichever way it ends. "
            + $"{FlowService.ResumeCommand} goes on with a run from its records alone, where it stood, under the definition it began "
            + "with - refused where the repository's entry for it changed since - reclaiming the owner of an engine that has gone "
            + "and saying so: what a stop or a crash cut off, and a failure that ended the run, run again as new attempts, save a "
            + "failure an answer fail ended; what that failure kept from running is drawn again; what the run owed - a failure or "
            + "an ending a pause recorded, the triage of an issue, a redirect - is made first; and every question and answer the run "
            + "has is kept. It goes on beside a question still open, and is refused, having written nothing, only where nothing "
            + "can run until one is answered - a session an answer began, or one whose budget question is open, pauses on it "
            + "instead. A record the run is made with that is missing is refused by name. "
            + $"{FlowService.AnswerCommand} answers a question: handed to the engine running the "
            + $"run through inbox.json, which it reads every {FlowTimings.Default.PollEvery.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s, "
            + "or else a session of its own. "
            + $"{FlowService.PauseCommand} asks that engine for the safe pause through stop.json - its agents hand off, the machine "
            + "steps finish unless --all, then the run is paused - and waits until it is complete; "
            + $"{FlowService.StopCommand} asks it to stop where it stands and waits {FlowTimings.Default.StopWait.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s, "
            + "and --now ends its process: only on this machine, and only the process its owner names by its start. "
            + $"{OrchestratorService.DeleteCommand} refuses a run a live engine owns, and removes a gone engine's owner with the run. In the terminal "
            + "a session runs in, the first Ctrl+C - and off Windows SIGTERM or a hang-up - asks the safe pause, which starts no "
            + "clock: an agent's handoff and a build may take as long as they take. The second stops the run where it stands, and "
            + $"the command ends within {PointOfNoReturn.SafePauseDrain.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s whatever is left; "
            + $"what is left then is what a crash leaves, and {FlowService.ResumeCommand} goes on from the records. No step runner is "
            + "built yet: each step of a run fails, saying no runner serves its type.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "What a killed process left running. On Linux and macOS a contained child of a step leads a session of its own, "
            + $"which nothing ends when the process that started it is killed, so it is recorded in its step's {OrchestratorLayout.ChildrenDirectoryName}/ "
            + "while it runs and the record taken back once that process ends it. Every command over an orchestrator - the three "
            + "orchestrator commands, each agent command, and each run verb over a run already made - first ends each recorded "
            + "session whose starter has gone, as a stop "
            + "ends one (SIGTERM, then SIGKILL five seconds later), removes its record, and says so in a line, and in the document's "
            + "ended where it answers with --json. A session whose starter still runs is left; one whose id a later process "
            + "carries ends nothing, and its record goes; a record that does not read as one is said and left; a starter on another "
            + "machine is never taken to have gone. Windows records nothing: a job ends a contained child with the process that "
            + "started it.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"--parallel, {OrchestratorRecord.DefaultParallel} unless given, is the most open agents at once - every agent with a "
            + "worktree not yet deleted, and every worktree git lists below the orchestrator's directory: create-agent refuses the "
            + "next, naming them, and list-orchestrator counts them. An agent with no worktree is never open. Run again, "
            + "create-orchestrator changes only the limit and the session, and create-agent records only the session, and nothing "
            + "of an agent with no worktree; for an agent whose making stopped part way, it says so and names what finishes it. "
            + "An agent's name is never used twice: a deleted agent's directory stays as its history.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "Seeding. create-agent hands the agent the main tree's uncommitted state - every path git status lists, off the "
            + "worktrees root, .git, .orchestrators, .mutations and .legs: each changed file copied into its worktree, each deletion made there - and "
            + "records what it was handed, each file with its digest. An untracked directory git will not look into, a repository "
            + "of its own, is named and not handed; --empty hands it nothing. A symbolic link among it is refused before the "
            + "worktree is made, so nothing is left behind. seed-agent does it again, refused over changes of the agent's own - a "
            + "copy it was handed and left alone is not one - unless --force; --empty hands it nothing more, keeping what it was "
            + "handed before. Seeding again hands besides every path the main tree holds otherwise than the agent shares it, "
            + "committed or not. refresh-agent hands a live agent every such path under the paths given - the anchor registries' "
            + $"directory where none are, and anywhere in the tree with {RefreshRequest.AllOption}, which takes no path beside it: one "
            + "whose main-tree copy is not what the agent was last handed or folded, where it was, "
            + "put back as the main tree's HEAD holds it included, and otherwise one the main tree holds otherwise than the agent's "
            + "base, committed since or not. It is a dry run until --apply, refused, copying nothing, where the agent changed or "
            + "deleted one, and records them as handed to it, so its fold leaves them out; one the agent already holds as the main "
            + "tree does is recorded as shared - listed, and written only with --apply - so its record says what it holds; a "
            + "symbolic link the main tree committed "
            + "is named and never handed, and so is a submodule's entry. A file the main tree turned into a directory, or a "
            + "directory it turned into a file, is handed as git holds it: what goes is removed first, then what comes is copied. "
            + "A path in a submodule the agent's worktree records - where the main tree turned one into files of its own - refuses "
            + "create-agent before its worktree is made, and seed-agent and refresh-agent, writing nothing: a hand-over never "
            + "writes into another repository, and rebase-agent brings in what the main tree committed there. "
            + "A refresh refused over the agent's changes names every path it changed, and the arguments that leave them: "
            + $"{RefreshRequest.ExceptOption} <path>, once for each, hands it every other path and leaves that one as the agent "
            + "changed it, its own work for its fold to weigh against what the main tree holds - said in the report, never "
            + "silently; it is not a --force, nothing is left out that nobody named, and one naming a path the refresh would not "
            + $"have refused is refused, nothing copied. {RefreshRequest.StaleOption} <path>, once for each, says its copy is not the agent's "
            + "work and hands it the main tree's - taken only where the agent's copy, as git would store it, is a blob the main "
            + "tree's history held at that path, among the last 512 commits that changed it, and refused "
            + "otherwise, nothing copied, as is one naming a path the agent holds no file at. The refusal over the agent's changes "
            + "names each such copy with the commit whose bytes it holds, and the arguments that hand them; it says too where the "
            + "search stopped short of the main tree's history. It reaches a path the main tree did not move as well - a copy put "
            + "back by hand to an older version - and one naming a path the agent holds as the main tree does is let stand. A path "
            + "the agent changed that the main tree moved too, each holding a file there, is weighed in full as a fold weighs one: "
            + "the refusal says beside it what a three-way merge of the two gives - from what the agent last shared, or its base - "
            + "shows the conflicts, and names the arguments that take the clean ones. Two such paths that differ in case alone, one "
            + "file in the agent's worktree, are weighed once: under the name the main tree's git gives the file, from the base's "
            + "file found in any case, whatever core.ignorecase says. "
            + $"{FoldAllowances.MergeOption} <path>, once for each, takes a clean merge: written into the agent's worktree as its "
            + "change, for its fold to write, and the path recorded as handed the main tree's copy, which the merge descends from. "
            + "It is never taken unasked, and a clean textual merge proves nothing about the program; one naming no path under the "
            + "paths refreshed is refused as a misspelling, and one naming a path with nothing left to merge is let stand. The arm "
            + "registry both changed is merged by arm, unasked, and taken where the result reads as a registry of the worktree as "
            + "the refresh leaves it; one that does not, or that conflicts, is among the agent's changes, and a --merge naming it "
            + "or an anchor registry is refused. A path named by two of "
            + $"{RefreshRequest.ExceptOption}, {RefreshRequest.StaleOption} and {FoldAllowances.MergeOption} is a usage error, exit "
            + $"{HarnessExit.UsageError}. "
            + "What the agent holds of its own where what it is handed needs room - a file or a link where a directory goes, or "
            + "files of its own in a directory a file replaces - refuses the hand-over, forced or not, naming each: copied, it "
            + "would write over them, or through the link out of its worktree. A file of the agent's written after it was weighed "
            + $"is never written over or removed by either, forced or not: the hand-over stops there, exit {HarnessExit.Incomplete}, "
            + "naming it, and run again weighs it anew. Neither hands anything while a move of the agent's "
            + "base stands stopped part way, or its HEAD is off its base. Both say when the agent's base is not the main tree's "
            + "HEAD. A path named otherwise than in UTF-8, committed or not, refuses any hand-over, naming it as git quotes it: no "
            + "file opens here by such a name.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "Rebasing. rebase-agent moves a live agent's base to the main tree's HEAD, a dry run until --apply. Each path the "
            + "two commits hold differently that the agent holds as its base does comes into its worktree as git holds it - a "
            + "change, a new file, a deletion, a file renamed in case alone, a link, a submodule's entry, a file turned into a "
            + "directory, or a directory into a file or a submodule's entry - and its own changes, and what it was handed, stay: its seed still says what it shares, "
            + "and refresh-agent hands it any of those the main tree moved since. A path it changed that the main tree committed a "
            + "change to since - an edit, a deletion, or anything of its own where the main tree committed a file, ignored or "
            + "staged included - refuses the move, nothing changed, unless --settled <path> names it: you reconciled its copy by "
            + "hand, and it stays as its own change on the new base. Where a three-way merge of the two is clean - git's, from what "
            + $"the old base held, of the new base's version and the agent's - {FoldAllowances.MergeOption} <path>, once for each, "
            + "writes it into the agent's worktree as its own change on the new base, whether it can be run merged with its "
            + "content: it is never taken unasked, and a clean textual merge proves nothing about the program. Nothing is recorded "
            + "for it: its fold weighs it against the new base. The refusal says what a merge of each such path gives, and names "
            + $"the command that takes the clean ones. A {FoldAllowances.MergeOption} naming a path the two commits hold alike is "
            + "refused as the misspelling it most likely is, and one naming any other path they changed is let stand, so the "
            + $"command that finishes a stopped move runs again; a path named with both {FoldAllowances.MergeOption} and "
            + $"{FoldAllowances.SettledOption} is a usage error, exit {HarnessExit.UsageError}; and a path one of whose versions is "
            + "a link, or that the agent reaches through one, is offered no merge. The arm registry is merged by arm, unasked, as a fold merges it (below), "
            + "from the old base's version, of the new base's as the worktree checks it out and the agent's, and judged by the "
            + "worktree as the move leaves it. A file it made that is the very file the main tree "
            + "committed there - the same content as git compares it, and the same mode where git trusts one - is no such "
            + "change: it is listed as held as the new base holds it already, and nothing is written. Whatever of the agent's "
            + "what comes in would go over refuses the move too: a file or a link where the new base holds a directory, and what it keeps - its own, settled "
            + "or shared - in a directory where the new base holds a file, or a submodule's entry that would hide it from git; each "
            + "is named, with what to do. So does a file the new base renamed in case alone, where the agent's file system finds a "
            + "name in any case, unless its old name comes in with it: where the agent keeps that - its own change, settled or "
            + "shared - writing the new name would write over it, and both are named, a shared one it changed to be folded first, "
            + "since no refresh hands it, and one it did not to be refreshed under the new name; where the agent's worktree no "
            + "longer holds it, the two are named as a rename and a deletion of one file - its old name to be put back and the "
            + "move run again, the new name deleted after only where the agent's session deleted the old - unless a move that "
            + "stopped part way removed it, as its record says, which running it again finishes. A file of the agent's written "
            + "after it was "
            + $"weighed is never written over or removed: the move stops there, exit {HarnessExit.Incomplete}, before git writes "
            + "anything, naming it. Its record names where the move goes before anything is written, then the new base's "
            + "paths are written, HEAD and the index move, and its record names the new base: a move that stopped part way, exit "
            + $"{HarnessExit.Incomplete}, is finished where it was going by running it again - the command its last words name, "
            + "with every option the move was given - what it wrote held as the new base holds it, and every other command refuses "
            + "the agent until then, naming that same command. Only such a move is ever finished, and "
            + "only from its base or where it goes: a HEAD moved by hand - back, forward or beside its base - or naming no commit "
            + "is never taken for one, and each command refuses it, saying how to put it back; a commit made inside the agent is "
            + "said to be one, and where git cannot say which a HEAD is, each says so and fails, never guessing.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "Dry runs. A dry run of refresh-agent, rebase-agent, fold-agent or delete-agent exits with what its --apply, typed "
            + "with the same arguments, would exit with at that moment: 0 only where that --apply would go through. Where a path, "
            + $"a row, or a consent nobody gave would refuse it, the dry run exits {HarnessExit.Refused} - {HarnessExit.UsageError} "
            + "where its arguments are a usage error, a path never handed to an agent among them - and where it would fail "
            + "before writing anything, with that failure's code - and either way it still prints all it weighed on standard "
            + "output, its summary last, on standard error. A run holding what the --apply takes refuses the dry run too: every "
            + "holder is named on a 'held now:' line, said with where its run's leg stands where it asked for a heavy-leg slot, "
            + $"and it exits {HarnessExit.Refused} - save where the dry "
            + "run already failed before writing anything, or met a usage error its --apply answers before it takes anything, "
            + "which keeps its code and names the holder still; "
            + $"delete-agent, whose --apply would go as far as the removal and leave it unfinished, exits {HarnessExit.Incomplete} "
            + "instead where only a copy of the worktree is held, by a run or a sweep; and where nothing refuses its --apply "
            + "before the removal - a tree held, a lock file that cannot be read, a path, a row or a consent - it exits "
            + $"{HarnessExit.Incomplete} too where the copies kept in the worktree cannot be removed as they stand, one that "
            + "cannot be told or removed or none that can be looked for, its request then refused; a directory there that no "
            + $"sync made goes with the worktree, and is only said. A record of the copies hosts keep that cannot be read refuses "
            + $"delete-agent ({HarnessExit.Refused}), its dry run and its --apply alike, before anything is written. What an "
            + "--apply can only find by "
            + "writing - a file changed after it was weighed, a write "
            + "that fails - is not in a dry run, so 0 never promises that the --apply will finish. --json answers with one "
            + "document, the whole of standard output, whose request member says what is wanted of you - 'sound' (nothing; wait, "
            + "where heldNow names a holder), 'needs-consent' (give what the dry run names: cells, rows or paths) or 'refused' (put right what the "
            + "last line names) - and whose heldNow holds each holder that decides its code: each a 'held now:' line names, with "
            + "where its leg stands - null for a leg of this machine where this machine's record of heavy legs cannot be read - "
            + "and each holding a copy kept in the "
            + "worktree, said as its own line says it, a leg copy's holder with where its leg stands, a mutation worker's with "
            + "the process, the run and since when its sweep's claim records. Beside them, needs names each consent the "
            + $"request lacks - '{OrchestrationReports.Named(ConsentKind.FoldNewRows)}' ({AnchorBatchRequest.NewOption}), "
            + $"'{OrchestrationReports.Named(ConsentKind.FoldLostRows)}' ({AnchorBatchRequest.AcceptLostOption}), "
            + $"'{OrchestrationReports.Named(ConsentKind.FoldOverCells)}' ({AnchorBatchRequest.AcceptOverOption}) "
            + $"or '{OrchestrationReports.Named(ConsentKind.FoldTwoChanges)}' ({FoldAllowances.MergeOption}) - and refusals each "
            + $"reason it is refused for, '{OrchestrationReports.Named(RefusalKind.Conflict)}' where a path both trees changed has "
            + $"a merge that conflicts and '{OrchestrationReports.Named(RefusalKind.Other)}' for anything else, so a request "
            + $"refused for conflicting merges alone says '{OrchestrationReports.Named(RefusalKind.Conflict)}' and nothing else; "
            + "folded says what the --apply of a fold and of a deletion did, gone through or stopped part way after writing some "
            + "of it - what it wrote, deleted, merged and handed, the arm registry merged by arm, the registries and rows it "
            + "applied, and the cells --accept-over took - and is null for every other answer and where it did nothing at all; "
            + "planned says what the plan of a refresh or a rebase takes, keeps, hands and merges, and is null for a fold and a "
            + "deletion. An --apply refused at its hold answers the same "
            + "document, applied true: request 'sound', and the run it met in heldNow. Stopped before "
            + "anything is weighed - a command line the parser refuses, a --directory that is not there, a repository not "
            + "initialised, another usage error, or a refusal met before then, an agent that is not there among them - it "
            + "answers with the same document, empty: applied false, request and heldNow null, no lines, no needs, no "
            + "refusals, and folded and planned null.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "Folding. fold-agent writes an agent's own work into the main tree, a dry run until --apply, and never removes its "
            + "worktree. Every path its status lists and every path it shares with the main tree - handed to it, or written or "
            + "removed by an earlier fold - goes in one list: its own, written; deleted, removed first, with the directories that "
            + "leaves empty, so a file it turned into a directory, or a directory into a file, makes room for what replaced it; "
            + $"inherited, shared and unchanged; already in the main tree; merged, where both trees changed it and {FoldAllowances.MergeOption} "
            + "names it, or it is the arm registry, merged by arm unasked; or settled. A shared path is compared with what "
            + "both trees held, any other with the blob at the agent's own base as git status compares, so a line-ending "
            + "conversion is no change, a changed mode is one, and a sibling's committed fold is never written over. What a fold "
            + "writes is recorded as shared: when a review sends the agent back, its later change of such a path - putting it back "
            + "as it was included - is its work to fold like any other. The whole fold is refused, and nothing written, where the "
            + "main tree changed a path the agent shares after the agent was last handed it or folded, or any other of its paths "
            + "since the agent's base - made, changed, made a directory of or removed by a commit, by an uncommitted edit or by "
            + "both, which the message says - where the "
            + "agent's HEAD is off its base, a commit made inside it among the ways, or a move of its base stands stopped part way, "
            + "for a link, a repository of its own, a path in a submodule of the main tree's - which the message says it made by a "
            + "commit or staged, and which --settled leaves out - or a path leading out of the main tree, "
            + "where what the main tree holds is in "
            + "the way of what it writes - a file where a directory goes, or files the fold does not remove in a directory a file "
            + "replaces, each said made or changed since the agent's base where the main tree did either, as any other path is, "
            + "or ignored by git - "
            + "and for an anchor registry the agent changed as a file: its rows go in through its rows directory only. "
            + "A rename of case alone the agent made, where both trees find a name in any case and the main tree's git index names "
            + "the file as before, is refused by name: a fold writes files, and the main tree's git would go on naming the file as "
            + "before - git mv -f there makes the rename, of a file in a directory renamed in case too, and the fold run again "
            + "finds the old name absent from both. The renamed file is weighed against the base's under its old name, found in "
            + "any case whatever core.ignorecase says: an edit made with the rename merges with the main tree's, and the main "
            + "tree's removal of the file refuses it, never undone. A file a tree spells on disk otherwise than its git index, "
            + "where the tree finds a name in any case, is one file whatever core.ignorecase says, weighed under the index's name - "
            + "save where the part spelled otherwise holds a character outside ASCII and core.ignorecase is false: such a name "
            + "may be composed otherwise in Unicode on disk, so it is listed under both spellings, and a fold of it stops at the "
            + "second. "
            + "A path both trees changed, each holding a file there that is not what both held, is still weighed in full: its "
            + "refusal says what a three-way merge of the two gives - git's merge, of the forms git stores, from what both held, "
            + "which for a shared path are the bytes kept beside the agent's seed, or found by their digest in the main tree's "
            + "history where none were kept, and for any other its base's - clean, with the hunks each side brought; conflicting, "
            + "with the conflicts shown; or none offered, with why - and names the command that takes the clean ones. "
            + $"{FoldAllowances.MergeOption} <path>, once for each, takes a clean merge: written into both trees, the main tree's "
            + "first, and recorded as shared once both hold it, so a fold that stopped between the two merges again to the same "
            + "bytes. It is never taken unasked, a conflict never, and a clean textual merge proves nothing about the program: "
            + "build and test the tree before you commit it, and have the agent's session read the file again before it writes "
            + $"it. A {FoldAllowances.MergeOption} naming no path the fold weighs is refused as the misspelling it most likely is, and one "
            + "naming a path with nothing left to merge is let stand, so the command line of a fold that stopped part way runs again; "
            + $"a path named with both {FoldAllowances.MergeOption} and {FoldAllowances.SettledOption} is a usage error, exit "
            + $"{HarnessExit.UsageError}, and a {FoldAllowances.MergeOption} naming an anchor registry or the arm registry is refused. "
            + "Never merged, whatever is named: a deletion against a change, a path both trees made, a version that is a link "
            + "or is reached through one, a file git reads as "
            + "binary or will not merge, one whose merge attribute asks for another merge, a file above 16 MiB, and an anchor "
            + "registry; nor is the arm registry ever merged by line. The arm registry (mutations.registry) both trees changed is "
            + "merged by arm, unasked - each arm from the side that alone added, removed or changed it, each version as its tree "
            + "holds it, from nothing where neither held a registry before - and taken only where the merged text reads as a "
            + "registry of the tree as the command leaves it, as check-mutations reads one ('help mutations'); an arm the two "
            + "changed differently, a version that cannot be read as a registry, or a merged text that does not read as one "
            + "refuses the path, each shown, and so do shared bytes that cannot be found, since an arm one side removed would come "
            + "back. Whether a file can be run is merged with its content: the "
            + "merged file takes the bit of the side that "
            + "changed it, and the bit alone never conflicts - but a registry both trees made, one so that it can be run and one "
            + "not, is offered no merge, since nothing either held before says which is meant; Windows keeps no such bit, and "
            + "none is read or set there. "
            + "--settled <path> names a path you reconciled in the main tree by hand: once the rest is written, the main tree's copy - "
            + "a file, or its absence - is handed to the agent, over only what was weighed there, and recorded as shared, so its next "
            + "fold starts from the reconciliation. A path both trees already hold alike is recorded, one both trees and the seed "
            + "agree on is let stand, so the line that ran once runs again, and one reached through a link, or a directory git will "
            + "not look into, is left out and said to be; one only the agent changed is refused, since nothing was reconciled there "
            + "and handing the main tree's copy would undo its change. It is not a --force. A path this process "
            + "cannot look at is never read as absent: the fold fails, nothing written. A file of either tree that changed after "
            + $"it was weighed is never written over: the fold stops there, exit {HarnessExit.Incomplete}, and run again weighs it "
            + "anew. Then the rows it declares anew are applied, all or nothing, each held to what write-anchor and set-anchor "
            + "refuse - a cell they would store cut, a citation of a row neither registry nor the fold's own rows hold - and to two "
            + "rules of its own. A row no registry holds is made only where --new <ID> names it, since a typo in an existing row's "
            + "id would make it a second row; and a cell of an existing row whose stored text the agent's does not keep word for "
            + "word - neither respaced, nor kept whole in an addendum, nor filling an empty cell - is written only where "
            + "--accept-lost <ID>:<cell> names it: the dry run shows what each such cell loses, word by word, and the command that "
            + "writes it, on its line 'to write them:', and --apply refuses the fold, writing nothing, until each is named - and so "
            + $"the dry run that shows it exits {HarnessExit.Refused} itself. Every "
            + "refusal of every row is named in one run. A --new or an --accept-lost naming a row the agent did not file is "
            + "refused, and so is an --accept-lost naming a cell that keeps its stored text, and a --new naming a row a registry "
            + "holds other than as declared; one naming a row an earlier fold of the agent applied is let stand, so the command "
            + "line of that fold runs again. An existing row is changed in the cells that differ, and one already as declared left "
            + "alone; a write that fails puts both registries back byte for byte. A row an earlier fold applied, declared as it was "
            + "then, is never applied again, so a change the registries took since stands. One declared anew over it is weighed "
            + "cell by cell - priority, status, trigger, closing, cross-refs - from what that fold recorded as declared, against "
            + "the registries as the write finds them: a cell only the agent changed is written; one only the registries changed "
            + "is kept, and said, and stays kept at each fold until the agent changes it; one both changed alike is left; and one "
            + "both changed otherwise is written over the registries' only where --accept-over <ID>:<cell> names it, the dry run "
            + "showing the three texts - what --accept-over consents to includes what the registries' text loses. The fold never "
            + "writes the agent's own row files: before it changes a kept cell, the agent writes the registries' text into it, or "
            + "its change is two changes of one cell. An --accept-over naming a cell not both changed is refused, as is one naming "
            + "a row the agent did not file, and one naming a row declared as its earlier fold recorded it is let stand; a row an "
            + "earlier fold applied that the registries no longer hold is refused, to be set by hand.");
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
            + "own is never forced: its evidence is kept again, and the delete-worktree --force that removes it is named for you; "
            + $"since every --apply of it ends so, its dry run exits {HarnessExit.Incomplete} too, naming the same command and "
            + "keeping nothing. A worktree made at its path since is not its to remove.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "Every command that writes the main tree or an agent's worktree holds it, as a sync holds a copy - fold-agent, "
            + "seed-agent, refresh-agent and delete-agent hold the agent's worktree as well as the main tree, and rebase-agent "
            + "holds its worktree - so no leg builds in either meanwhile, and once one has written, an interruption waits for it "
            + "to finish. A dry run of the four asks with the very hold its --apply takes, so a run holding what it needs is "
            + $"named and the dry run refused itself, exit {HarnessExit.Refused}, saying that nothing in the request needs "
            + "correcting; the refusal an --apply gives names the holder in the words of the dry run's 'held now:' line, where "
            + "its run's leg stands included. A holder is a fact about one moment, so 0 "
            + "from a dry run never promises the tree will still be free when the --apply is typed. A leg that works in a copy "
            + "of the tree holds only the copy ('help legs'): a fold and such a leg never "
            + "refuse each other, and a fold that lands while the run is still reading the tree ends its legs inputs-moved with "
            + "nothing run - seconds, at the run's start - while the same run started again reads the folded tree. "
            + "delete-agent alone names a copy a run holds, since it removes every copy with the worktree: a host's, a leg copy "
            + "kept in it, and a mutation worker, each in its own words. delete-agent refuses to run from "
            + "inside the worktree it removes, and so does its dry run, "
            + "and an agent made under a worktrees root the configuration no longer names is refused with nothing touched. "
            + "delete-orchestrator deletes the orchestrator once every agent of it with a worktree is deleted and no worktree is "
            + "left below its directory, nor any directory holding a .git of its own that git records no worktree at - until then "
            + $"it refuses ({HarnessExit.Refused}), naming each - and the evidence, transcripts, steps and sessions its agents "
            + "kept, whatever their kind or state, only with --delete-evidence; an agent with no worktree holds nothing but "
            + "records, and goes with it, deleted or not. It "
            + "removes the record last, so one that stops part way finishes when run again.");

        return builder.ToString();
    }

    private static string RenderWorkflows()
    {
        var builder = new StringBuilder();

        builder.AppendLine("Workflows");
        builder.AppendLine();
        builder.AppendLine($"A workflow is a flow of typed steps that depend on one another by name, declared by");
        builder.AppendLine($"name under workflows in config.json and defined by a yml file of its own:");
        builder.AppendLine($"{HarnessLayout.WorkflowsDirectoryRelative}/<name>/<name>{HarnessLayout.WorkflowFileExtension}. This build checks a");
        builder.AppendLine($"workflow's definition, with '{ToolPackage.Command} validate-workflow <name>', and starts and drives a run");
        builder.AppendLine($"of one with '{ToolPackage.Command} {FlowService.RunCommand} <name>' (see 'help orchestrators'). No step runner");
        builder.AppendLine("is built yet, so each step of a run fails, saying no runner serves its type.");
        builder.AppendLine();
        builder.AppendLine("The workflow's directory is the workflow's own - its agent files (agents/<name>.md),");
        builder.AppendLine("its prompts, its schemas, the bar it recommends by (recommendations.md) - and every path");
        builder.AppendLine("the yml names is relative to that directory and stays inside it, as an action's files");
        builder.AppendLine("are relative to its own directory (see 'help runners'). So two workflows never read");
        builder.AppendLine("each other's files, and a repository can hold as many as it has ways of working. init");
        builder.AppendLine("writes one - the implementation workflow, from a request to a pull request whose checks are");
        builder.AppendLine($"green - and declares it in the config it creates. Where config.json declares {WorkflowTemplates.ShippedWorkflowName},");
        builder.AppendLine("init writes each of its files that is missing - the whole set where the repository has no");
        builder.AppendLine("definition of it yet, and where it has one only the shipped files that definition names,");
        builder.AppendLine("so a file taken out with its step stays out - and keeps each one that is there,");
        builder.AppendLine("whatever it holds, so an init that stopped part way is finished by the next - where a");
        builder.AppendLine("file is missing and the definition there does not read, init refuses (12; 13 where it");
        builder.AppendLine("cannot be read), since which files it names cannot be told; nothing that exists is ever");
        builder.AppendLine("overwritten, and a config that does not declare it gets none of it.");
        builder.AppendLine("The name is the folder's, the file's and the name: key's alike, one word, and a");
        builder.AppendLine("disagreement among the three is refused when the file is read.");
        builder.AppendLine();
        builder.AppendLine($"'{ToolPackage.Command} {WorkflowExportService.CommandName} <name> --to <dir>' copies a workflow - its yml and every");
        builder.AppendLine("file it names, and no other - into <dir>/<name>/, a folder of its own that must not be");
        builder.AppendLine("there (13 where it is): a declared one once it holds (12 where it does not), byte for");
        builder.AppendLine($"byte, or, with --shipped, the {WorkflowTemplates.ShippedWorkflowName} workflow as this tool ships it, to make a");
        builder.AppendLine("version of your own. A file that cannot be written takes back what was written before it, or");
        builder.AppendLine("says why it could not.");
        builder.AppendLine();
        builder.AppendLine("A workflow declares inputs - the values a run is started with, each with a kind, a");
        builder.AppendLine("default and the options it takes; params - its own knobs, defaulted in the file and read");
        builder.AppendLine("as param.<name>; and outputs - what the whole workflow produces, each from a step of");
        builder.AppendLine("the flow. Its steps are of the types below, or groups holding steps of their own: a");
        builder.AppendLine("group runs its steps once for each work item a forEach yields, again and again until a");
        builder.AppendLine("repeat's bound is met, or once as a block that gathers its members' findings. Every");
        builder.AppendLine("step's name is a lower-case word with dashes within, unique among its siblings; steps");
        builder.AppendLine($"nest at most {WorkflowFileParser.MaxDepth} deep. A step raised by another step's ending says on - by the outcome");
        builder.AppendLine("(failure, success or end, a failure by exit code) - and sends the run back with then;");
        builder.AppendLine("the schedule never starts it. A step gives a failure nothing handles to the workflow's");
        builder.AppendLine("recover by saying onFailure: recover, itself or through a group around it - never a");
        builder.AppendLine("reasoning step, whose agent is tried again (retries, waiting backoffSeconds, twice as long");
        builder.AppendLine("each time) and on its fallback, and which asks where it still fails. The recovery's steps,");
        builder.AppendLine("written once, read the step that failed as source and see every step of the flow, find");
        builder.AppendLine("what went wrong and put it right; then the run goes where its onSuccess says - failed-step,");
        builder.AppendLine("the step that gave it its failure, which runs again from where it stopped - and a failure");
        builder.AppendLine("of its own work, past its steps' retries, does what its onFailure says: pause or stop; the");
        builder.AppendLine("recovery never calls itself. A step that fails again goes to the recovery again by its own");
        builder.AppendLine("onFailure, until it");
        builder.AppendLine($"passes, a person is asked, or maxVisits is met (absent, the engine's bound of {FlowRecover.MostVisits}");
        builder.AppendLine("visits); past it the run asks (recoveryBound), with the advisor's recommendation of a fix:");
        builder.AppendLine("more, one more recovery; direct, with the fix in words, for the next one; skip, where the");
        builder.AppendLine("step is skippable; or abort.");
        builder.AppendLine();
        builder.AppendLine("An issue a reasoning step meets that was there before the run, it reports in its answer");
        builder.AppendLine($"(preExisting) and never fixes itself; onPreExisting names the step of the flow's top level");
        builder.AppendLine($"that triages each one. That step says raised: {WorkflowStep.RaisedByPreExisting} - it is raised once per issue and never");
        builder.AppendLine("scheduled, as the recovery is not - and onAddress: recover: it reproduces the issue on the");
        builder.AppendLine("run's base commit, gives the recovery one that is in a step's way, related to the work or");
        builder.AppendLine("critical, to fix at its root (a step it was in the way of runs again from where it stopped),");
        builder.AppendLine("and lists the rest, proven, with a title and a description, in the run's");
        builder.AppendLine("pre-existing-issues.json, shown at the end. Where a workflow has such a step, every other");
        builder.AppendLine("reasoning step names it, so no issue is lost.");
        builder.AppendLine();
        builder.AppendLine("A reasoning step names its tier or its model and its effort: one level for every model it");
        builder.AppendLine("may run on, or a map giving each its own - all for the rest, a model's code for one, so a");
        builder.AppendLine("step can run its Claude model at xhigh and GLM 5.3 at max, the highest it takes. Each");
        builder.AppendLine("model's level is checked against what that model takes when the definition is checked");
        builder.AppendLine("against the configuration's providers ('help agent-clients'), and validate-workflow says");
        builder.AppendLine("every step's models and levels under the line that says it holds. A test step's filterWhen");
        builder.AppendLine("is an expression that switches its filter: where it reads false the step gives no filter and");
        builder.AppendLine("every test of its legs runs, as the shipped verify steps do where the run's input");
        builder.AppendLine("affectedTests is off (on with --input affectedTests=on, each item tested on its own tests).");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "validate-workflow holds a definition to the config.json it is checked under too: a run step whose runner, or a runner "
            + "its run checks name, writes the tree (writesTree) names with in the create-agent step whose worktree it works in, since "
            + "the run reads its definition in the main tree and folds into it; the agents a run makes, named "
            + $"'{OrchestrationRules.GeneratedAgentPrefix}' and a number that grows to the workflow's maxInstances, fit "
            + "worktrees.maxNameLength; and a commit step gives a value for every variable commit.template requires and none for one "
            + "it does not declare - or, where none is declared, a message and no variables.");
        builder.AppendLine();
        builder.AppendLine("The keys of the file and of every step are listed below, generated from the tables the");
        builder.AppendLine("reader itself reads, so what is listed is what is read.");
        builder.AppendLine();
        builder.AppendLine("The file's own keys:");
        builder.AppendLine();
        AppendKeys(builder, WorkflowKeys.File);
        builder.AppendLine();
        builder.AppendLine("What one declared input takes, and one declared param, and one declared output:");
        builder.AppendLine();
        AppendKeys(builder, WorkflowKeys.Input);
        AppendKeys(builder, WorkflowKeys.Param);
        AppendKeys(builder, WorkflowKeys.Output);
        builder.AppendLine();
        builder.AppendLine("What every step takes, group or typed step alike:");
        builder.AppendLine();
        AppendKeys(builder, WorkflowStepTypes.Common);
        builder.AppendLine();
        builder.AppendLine("What a group takes, beyond those:");
        builder.AppendLine();
        AppendKeys(builder, WorkflowStepTypes.Group);
        builder.AppendLine();
        builder.AppendLine("The step types, and what each takes beyond the common keys (a check's failOn says what");
        builder.AppendLine("a red verdict does to the step: red fails it, never records it; each type's default is");
        builder.AppendLine("in its list):");
        builder.AppendLine();

        foreach (var type in WorkflowStepTypes.All)
        {
            // orient takes nothing beyond the common keys: its line stands alone.
            builder.AppendLine($"  {type.Type}{(type.IsCheck ? $" (a check; failOn {type.FailOnDefault} by default)" : string.Empty)}");
            builder.AppendLine();

            if (type.Keys.Count > 0)
            {
                AppendKeys(builder, type.Keys);
                builder.AppendLine();
            }
        }

        AppendWrapped(
            builder,
            $"An expression reads names joined by &&, ||, !, ==, != and ?? with ( ) to group, 'empty(x)' for whether a read holds nothing "
            + "and 'any(list, test)' for whether any member meets a test written with 'it' for the member: every step, input and param it "
            + "names must be declared before it, every path it reads is typed by what holds it - a reason step's schema, the output a "
            + "built-in type answers, the engine's facts of a step, the run's own names - and every operator takes what it says: !, && and "
            + "|| a boolean, == and != no list and no object, any a list and a test that yields a boolean. A when, an until and a blocking "
            + "yield a boolean, and a forEach and a key that takes a list a list; how the run stands without its always steps "
            + $"({string.Join(", ", WorkflowExpressions.RunStatuses.Select(status => $"run.{status}"))}) is read by an always step alone. A placeholder in braces - "
            + "'{step.member}' - in a template key reads the same names, typed the same way, as one in a prompt does; one in any other key "
            + "is refused, and a pull request's base reads nothing but the run's inputs and run.name, the same for every pull request. "
            + "What only a prompt reads stands where its agent is asked it: {recommendations} in a reason step's prompt and the "
            + "advisor's, {question.<name>} in the advisor's alone, {status.<name>} in the reporter's two - {status.question} in its "
            + "askPrompt alone - {update.<name>} in the prompts of the step that plans an update and of the steps of its scope, and "
            + "{piece.<name>} in a step of the work-item group; the advisor's and the reporter's prompts read no step by name, and a pull "
            + "request's body, which the engine fills and no agent reads, reads what a template key reads and no skill. "
            + $"A question gives only the texts the engine names ({Under("question", WorkflowExpressions.QuestionTexts)}), and so do the "
            + $"status ({Under("status", WorkflowExpressions.StatusTexts)}), a plan update ({Under("update", WorkflowExpressions.UpdateTexts)}) "
            + $"and a piece ({Under("piece", WorkflowExpressions.PieceTexts)}); the bar is one text, and so is each run fact "
            + $"({string.Join(", ", WorkflowExpressions.RunFacts)}), which a template reads and an expression does not. "
            + $"'{ToolPackage.Command} validate-workflow' checks the definition - the shape of every key, every name an expression, a "
            + "template key or a prompt reads, every file the definition names - and "
            + $"refuses ({HarnessExit.ConfigInvalid}) naming each problem, without running anything, as a "
            + "config.json that does not hold is refused. Where config.json declares no workflow there is nothing to check, and it is "
            + $"refused ({HarnessExit.Refused}), saying that \"workflows\": [\"{WorkflowTemplates.ShippedWorkflowName}\"] declares the one init writes.");

        return builder.ToString();

        static string Under(string root, IReadOnlyList<string> texts) => string.Join(", ", texts.Select(text => $"{root}.{text}"));
    }

    private static string RenderLayout()
    {
        var builder = new StringBuilder();

        builder.AppendLine("Layout created by init");
        builder.AppendLine();
        builder.AppendLine("  .harness-config/config.json        tracked by git; the whole contract");
        builder.AppendLine($"  {HarnessLayout.WorkflowsDirectoryRelative}/<name>/<name>{HarnessLayout.WorkflowFileExtension}");
        builder.AppendLine("                                     tracked; one directory per workflow, holding its");
        builder.AppendLine("                                     definition and every file that definition names -");
        builder.AppendLine($"                                     its agents, prompts and schemas ('{ToolPackage.Command} help workflows');");
        builder.AppendLine("                                     each missing file written by init where");
        builder.AppendLine("                                     config.json declares the implementation one");
        builder.AppendLine($"  {HarnessLayout.WorkflowsEnvSlotRelative}/    contents ignored; the agent clients' keys, NAME=value");
        builder.AppendLine("                                     files, every file merged, no key read from");
        builder.AppendLine("                                     anywhere else; no workflow may be named .env");
        builder.AppendLine($"  {HarnessLayout.RunnerActionsDirectoryRelative}/<name>/<name>.yml");
        builder.AppendLine("                                     tracked; one directory per action, holding its");
        builder.AppendLine("                                     steps and whatever those steps run");
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
        var agentDirectory = $"{HarnessLayout.OrchestratorsDirectoryName}/<o>/{OrchestratorLayout.AgentsDirectoryName}/<agent>";
        builder.AppendLine($"  {HarnessLayout.OrchestratorsDirectoryName}/<o>/{OrchestratorLayout.StepsDirectoryName}/<instance>/");
        builder.AppendLine("                                     ignored; one step of a run that no agent owns,");
        builder.AppendLine("                                     the create-agent that makes an agent included");
        builder.AppendLine($"  {agentDirectory}/{OrchestratorLayout.StepsDirectoryName}/<instance>/");
        builder.AppendLine("                                     ignored; one step the agent runs, or that");
        builder.AppendLine("                                     runs in or for its worktree: everything of the");
        builder.AppendLine($"                                     step, its {OrchestratorLayout.HandoffFileName} where one stopped it");
        builder.AppendLine($"  {agentDirectory}/{OrchestratorLayout.SessionsDirectoryName}/<id>/");
        builder.AppendLine("                                     ignored; API clients only: one conversation with");
        builder.AppendLine("                                     the provider, in the provider's own form");
        builder.AppendLine($"  <step>/{OrchestratorLayout.ChildrenDirectoryName}/<session>{OrchestratorLayout.ChildExtension}");
        builder.AppendLine("                                     ignored; in either kind of step, on Linux and");
        builder.AppendLine("                                     macOS: a session a contained child of the step");
        builder.AppendLine("                                     leads while it runs, ended by the next command");
        builder.AppendLine("                                     over the orchestrator where its starter was killed");
        builder.AppendLine($"  {HarnessLayout.MutationsDirectoryName}/                        contents ignored, .gitkeep tracked; the tree's");
        builder.AppendLine("                                     mutation workers, in every tree, never sent to");
        builder.AppendLine($"                                     a host by sync ('{ToolPackage.Command} help mutations')");
        builder.AppendLine($"  {HarnessLayout.LegsDirectoryName}/                             contents ignored, .gitkeep tracked; the copies");
        builder.AppendLine("                                     of the tree its legs work in, in every tree,");
        builder.AppendLine("                                     never sent to a host by sync");
        builder.AppendLine($"  {HarnessLayout.RunsDirectoryRelative}/              ignored; one directory of records per run, in");
        builder.AppendLine("                                     the tree that ran it");
        builder.AppendLine("  .harness-config/lock.json          ignored; records in-progress runs");
        builder.AppendLine($"  {HarnessLayout.HostCopiesDirectoryRelative}/       ignores itself; which hosts hold a copy of");
        builder.AppendLine("                                     which worktree, in the main checkout");
        builder.AppendLine($"  {HarnessLayout.MutationRecordDirectoryRelative}/   ignores itself; each leg's verdicts of its");
        builder.AppendLine("                                     mutation arms, with what each was judged on,");
        builder.AppendLine("                                     in the main checkout ('help mutations')");
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
        builder.AppendLine("and workflows/.env directories, and git re-includes no placeholder from an");
        builder.AppendLine("excluded one. A rule re-including a directory the block ignores, such as each");
        builder.AppendLine("host's under sshItems, is named too, though git itself names none for the files");
        builder.AppendLine("below it. A path git will not answer about - one beyond a symbolic link - is named");
        builder.AppendLine("with git's reason, and the rest are still asked. runs is ignored by name, with no");
        builder.AppendLine("trailing slash, so one kept on another disk through a link is ignored as the link");
        builder.AppendLine($"it is. The worktrees root, the orchestrators directory, {HarnessLayout.MutationsDirectoryName} and {HarnessLayout.LegsDirectoryName} are");
        builder.AppendLine("kept in git by their placeholders, so each reads as not ignored itself while");
        builder.AppendLine("everything made in it is, and none can be a link: init names one it finds, and");
        builder.AppendLine("writes nothing through it. Sync withholds each by name. Each copy in them is a");
        builder.AppendLine("whole copy of the tree, so a program of the repository's own that walks the tree");
        builder.AppendLine("from its root - a census, a guard over every file - must leave out what the block");
        builder.AppendLine("ignores, as git does, or it reads every file once more for each copy kept there.");
        builder.AppendLine();
        builder.AppendLine("A block an earlier build wrote under the markers of the tool's former name");
        builder.AppendLine($"('{GitIgnoreManager.RetiredBeginMarker}') is the managed block too: init");
        builder.AppendLine("rewrites it in place under the current markers and says so, and none of its lines");
        builder.AppendLine("is read as a rule of the repository's own.");
        builder.AppendLine();
        builder.AppendLine("init writes the tree it runs in, a worktree's own included: its configuration - a");
        builder.AppendLine("copy of the main checkout's, where the worktree was running on that one - its");
        builder.AppendLine(".gitignore and its placeholders. The main checkout is left as it is, but for an");
        builder.AppendLine("anchor registry git ignores, created there where it is missing.");
        builder.AppendLine();
        builder.AppendLine("Ignored state lives in the main checkout. A worktree receives the tracked part of");
        builder.AppendLine(".harness-config through git but never the ignored part, so connection data and the");
        builder.AppendLine("run lock resolve back to the originating checkout. A run's records are an");
        builder.AppendLine("exception, and so are a tree's .mutations and .legs: each belongs to the tree that");
        builder.AppendLine("ran or made it, so a run started inside a worktree writes its records there, and");
        builder.AppendLine("build, test, run and check-mutations name the directory in");
        builder.AppendLine("their output and as runDirectory in --json. Each leg that kept records has a line of");
        builder.AppendLine("its own naming its directory and the logs in it - 'logs of <leg>[ on <host>]' - as its");
        builder.AppendLine("verdict is said and in the run's closing lines, and as records and the phase logs as");
        builder.AppendLine("logs in --json, a host's paths with its home written as ~ (see");
        builder.AppendLine($"'{ToolPackage.Command} help legs'). Each names its run");
        builder.AppendLine("too, in its first line, 'run <id>', and as runId in --json, however it ended: a run");
        builder.AppendLine("refused before it had a directory keeps no records, and is cited by that id alone.");
        builder.AppendLine("check-mutations --list begins no run, and names neither.");
        builder.AppendLine("A host runs a leg under a run of its own: the dispatcher names it once, '<leg>: run");
        builder.AppendLine("<id> on <host>', never relaying the host's own line, and the leg's --json holds it as");
        builder.AppendLine("hostRunId.");
        builder.AppendLine("Action files are tracked, so a worktree has its own and a runner acts on the tree it");
        builder.AppendLine("was asked about.");

        return builder.ToString();
    }

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
        builder.AppendLine("                 that runs them: visualStudio runs that instance's vcvarsall.bat");
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
        builder.AppendLine("  agentClients   the clients the tool builds in (claude-code, claude-api, glm), each");
        builder.AppendLine("                 by its name: update, claude-code's alone, says whether the tool");
        builder.AppendLine("                 installs and updates Claude Code with npm; a client holds no model,");
        builder.AppendLine("                 size or price - contextTokens and maxOutputTokens are a model's");
        builder.AppendLine("                 ('help agent-clients')");
        builder.AppendLine();
        builder.AppendLine("A list whose absence means every one of what it names, or a set the tool chooses, is");
        builder.AppendLine("refused given empty, as --legs given no name is: a runner's legs and steps; a tool's");
        builder.AppendLine("platforms, toolchains, legs, processors and emulators; a toolchain's platforms; an");
        builder.AppendLine("emulator's phases; ci.workflows; test.inputs; a project's targets and");
        builder.AppendLine("rebuildableFormats; a platform's toolSearchDirectories. Leave the key out instead. A");
        builder.AppendLine("list whose empty form means none - such as sync.neverTransfer, sync.exclude or");
        builder.AppendLine("contention.buildTools - takes [] as that. workflows, which declares no workflow left");
        builder.AppendLine("out, is refused given empty too, so none is spelt one way: leave the key out for no");
        builder.AppendLine("workflow. A runner's list under mutations.runReads, which would say nothing given");
        builder.AppendLine("empty, is refused so: leave the runner out instead.");
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
        builder.AppendLine("given. With excludeArg '-LE', '--label x' is refused beside an exclusion 'x', given by");
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
        builder.AppendLine("'|', which ctest would read as part of one pattern that leaves out nothing.");
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

/// <summary>What a front end needs to know of one reference topic: its name and what the overview says of it.</summary>
/// <param name="Name">The name it is asked for by, and advertised under.</param>
/// <param name="Summary">What the overview says of it.</param>
public sealed record HelpTopicInfo(string Name, string Summary);
