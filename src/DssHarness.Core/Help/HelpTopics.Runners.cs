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
        builder.AppendLine($"  {{{LegPathNames.Distribution}}}                      the distribution the leg's own WSL item");
        builder.AppendLine("                                      names, as wsl.exe lists it: what 'wsl.exe -d'");
        builder.AppendLine("                                      takes. Refused on a leg that runs in no WSL");
        builder.AppendLine("                                      distribution");
        builder.AppendLine($"  {{{LegPathNames.ItemDistributionPrefix}<item>}}               the distribution WSL item <item> names, on");
        builder.AppendLine("                                      any leg. Refused where wslDistros declares no");
        builder.AppendLine("                                      such item");
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
            $"{{{LegPathNames.Distribution}}} and {{{LegPathNames.ItemDistributionPrefix}<item>}} are read from the WSL item's "
            + $".env - its {Secrets.HostSecretsStore.DistributionKey}, never its credential - where the leg is placed, and a "
            + "host running a leg another machine sent it is told them beside the leg: config.json names a WSL item alone, "
            + "and an item named otherwise than its distribution, given to 'wsl.exe -d', reaches none. Where the leg was "
            + "placed, an item whose .env cannot be read has no distribution, and a line naming it is refused before the "
            + $"first step runs, saying so; '{Hosts.ToolPackage.Command} legs' names each WSL host's distribution.");
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
            + "with none of them is refused, and so is a step naming one that is not required and has none, naming "
            + "the input and how it can have one - both before any leg is admitted (below). Where the action has a "
            + $"{PredefinedActions.ReadInputs} step, the same values reach every step as INPUT_<NAME> in the "
            + "environment. The environment is how a secret is handed over, under its own name: a value spliced "
            + "into a command line reaches the process table, where anything on the machine can read it. A step's "
            + "own inputs are resolved the same way and reach only its own run lines, by name; the environment "
            + "holds the action's alone.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "An input may declare what its value must be: 'pattern', a .NET regular expression the whole value must "
            + "match, case and all - [0-9]+ takes 12 and not 12a; and for a list, 'separator', the text between its "
            + "items, matched as written, with 'minItems', 'maxItems' or both, each at least 1. An item is the text "
            + "between separators with white space trimmed from either end, and one left empty is not counted, so a "
            + "separator of one space parts a run of spaces as one does and a trailing separator adds no item. Each "
            + "declaration is held to sense when the file is read: an empty or unreadable pattern, a bound with no "
            + "separator, a separator with no bound and a minItems over maxItems are refused, and so is a default its "
            + "own input refuses.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "A required input with no value, and a value its input refuses, are refused before any leg is placed, "
            + "synced, locked or admitted - a heavy run never waits for a slot to be told - naming every such input "
            + "at once, as a usage error; then a step naming an input with no value, as a configuration error; and "
            + "all of it again as each leg's runner starts. A leg this machine runs is held to this machine's "
            + $"{HarnessLayout.RunnerEnvDirectoryName}; for a leg another machine runs, only what "
            + $"{CommandLineInputs.Option} gave is held to its input here, since that machine's "
            + $"{HarnessLayout.RunnerEnvDirectoryName} is its own, and the rest is that machine's to hold by its own "
            + "values: a WSL distribution, whose leg this machine admits, is asked to check it once the leg's copy is "
            + "synced and before this machine asks for the leg's slot, its refusal the run's; an ssh host checks it "
            + "before it admits the leg itself.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "Everything else a leg's runner would refuse before its first step that does not wait on the leg's build is "
            + "refused before a heavy leg waits for its slot as well, by the checks the runner makes as it starts and in "
            + "their order: the steps the run selects and the leg's system runs, every input and every name a run line "
            + "holds, each program a line starts held to the tool policy, a line that starts nothing or would put a "
            + "secret in its arguments, and an action's working space git does not ignore. A leg this machine runs is "
            + "checked here once its tree is synced and locked and its isolation and developer environment are decided, "
            + "right before its slot is asked for; a WSL distribution's leg by the distribution, sent the leg's run line "
            + $"without --json, {SetUps.LegSetUps.Option} or {Build.KeepGoing.Option} and with {Application.Legs.RunnerLegs.CheckBeforeAdmissionOption}, "
            + "which checks and runs nothing; an ssh host's by the host, before it admits the leg. A leg whose work would "
            + "never start - its isolation refused, or its developer environment not set up - ends with its own line and "
            + "is never checked. A step reading the build of a leg this run did not build is refused as the runner "
            + "starts, after the slot, for the build it waits on is made then; a leg that waits for no slot is checked "
            + "as its runner starts. A run check's runner is checked alike, and what would refuse it is warned of rather "
            + "than refusing the run: a check runs only where a failure matches the exception it confirms, and one that "
            + "starts it is refused by it then, leaving that failure unexcused.");
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
        builder.AppendLine("A leg that did not pass carries, in --json, the last lines its last failing phase printed as");
        builder.AppendLine("logTail and that phase's name as tailPhase - a host's leg among them, whose log stays there.");
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
            + "a build tree - keep that as one archive. A link along the path named, file or directory, is followed as it is "
            + "spelt - a build directory pointed at another volume is ordinary - but never into a place the isolation hides "
            + "from every command: one that leads there fails the pull, nothing read. A link below a directory named is never "
            + "followed: it is named, and nothing is brought back for it. With --dry-run the host is asked what the directory holds, and nothing is brought. A pull that "
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
    /// Writes how each step key in <paramref name="spellings"/> is written, beside what it does as
    /// 'Every key' says it: one meaning, in one place, however many times help shows the key.
    /// </summary>
    private static void AppendSpelt(StringBuilder builder, params (string Key, string Value)[] spellings)
        => AppendColumns(
            builder,
            [.. spellings.Select(spelt => ($"  {spelt.Key}: {spelt.Value}", ActionFileKeys.Step.Single(key => key.Name == spelt.Key).Meaning))]);
}
