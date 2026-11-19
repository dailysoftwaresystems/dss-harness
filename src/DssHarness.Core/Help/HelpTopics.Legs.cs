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
using DssHarness.Core.Waiting;
using DssHarness.Core.Worktrees;

namespace DssHarness.Core.Help;

public static partial class HelpTopics
{
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
        builder.AppendLine("toolsVersion, optional, holds an environment's legs to one MSVC toolset, named as");
        builder.AppendLine(@"VC\Tools\MSVC names its toolsets: by two numbers, for the newest of that line, or");
        builder.AppendLine("by all three, for that one - for a compiler such as a CUDA toolkit's nvcc, which");
        builder.AppendLine("refuses an MSVC newer than it knows. Left out, vcvarsall.bat sets up the instance's");
        builder.AppendLine("default toolset. Only its form is checked when the file is read: no toolset is built");
        builder.AppendLine("into this tool. The survey then takes the newest instance holding a toolset it names,");
        builder.AppendLine("with the newest of those, and a host with none turns the leg away as a tool missing,");
        builder.AppendLine("naming what each instance holds. vcvarsall.bat is run with -vcvars_ver= and that");
        builder.AppendLine("toolset, and one that sets up another fails the leg; the leg's line names the MSVC");
        builder.AppendLine("set up. Two environments of one kind keep two toolsets apart on one machine:");
        builder.AppendLine();
        builder.AppendLine("  \"developerEnvironments\": { \"visualStudio\": { \"kind\": \"visualStudio\" },");
        builder.AppendLine("    \"cuda\": { \"kind\": \"visualStudio\", \"toolsVersion\": \"14.44\" } }");
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
        AppendFailedUnits(builder);
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
            $"A leg whose host's {ToolPackage.Id} fails as a defect (exit {HarnessExit.InternalError}) with no line for it is poisoned - the "
            + "harness failing, never a host that could not be reached - its line naming the host, the code and what the host "
            + "said. Where it failed so before anything ran there - no run named, no note written, and no ledger, or one with no "
            + "line, no run and no set-up left running - the leg is started there again, "
            + $"{(RemoteLegRunner.MostRestarts == 1 ? "once" : $"{RemoteLegRunner.MostRestarts} times")} at most, which is safe since "
            + "nothing ran: a runtime or a tool replaced there while it started fails that way once. "
            + "A start that then answers is the leg's line, which says what the failed start said ('retried once: ...', and "
            + "retried in --json); one that fails so again is poisoned, saying it was retried; and one that ends with no line "
            + "for the leg says it in what ends it. Once anything ran there, the leg is never started again. A WSL distribution "
            + "asked to check a heavy leg before this machine takes its slot is asked once more alike - a check runs nothing - "
            + "and one that fails so again is the leg's poisoned line, with no slot waited for; what its failed start said is "
            + "said on whatever line the leg then reaches, before what its run's own starts said.");
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
            + $"is listed since, or why that is not known ({HarnessExit.CommandFailed}). dotnet is waited for to its end, however long "
            + $"it takes, and said every {Waits.SaidEvery.TotalMinutes.ToString(CultureInfo.InvariantCulture)} minutes while it lasts. Where "
            + "nuget.org does not say which release is the newest it says why - no route to it, a refusal, an answer it cannot read - "
            + "and changes nothing, "
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
            + "cancels it the same way. What the host started is then waited for while anything it started still runs, however "
            + $"long that takes to stop, and said every {Waits.SaidEvery.TotalMinutes.ToString(CultureInfo.InvariantCulture)} minutes "
            + "where this machine would read it; once nothing it started runs and it still has not ended - a command reading "
            + "the output of something its child left running, or writing a line nobody reads - it is ended, and the host's "
            + "DssHarness with it, so the host is never held by a run nobody reads, as it is where the host's DssHarness is "
            + "itself stopped meanwhile. A command that finishes what it began - a deletion, a fold, a hand-over - is waited "
            + "for to its end, as it is where it is typed and interrupted. Where this machine can no longer write to the connection while a host works, "
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
        builder.AppendLine($"from repositoryPath, or an item's {HostSecretsStore.RepositoryPathKey}, is said as that is written:");
        builder.AppendLine("'~/src/app' keeps the account out of it.");
        builder.AppendLine();
        builder.AppendLine("A host keeps a copy of each tree whose legs it runs: the main checkout's at its");
        builder.AppendLine($"repositoryPath - or the {HostSecretsStore.RepositoryPathKey} its item's .env sets, which wins ('help");
        builder.AppendLine($"secrets') - and each worktree's beside it, at <repositoryPath>{HostCopies.WorktreeSuffix}<name>,");
        builder.AppendLine("named for the worktree's directory. 'legs' names, for each host, where it keeps the");
        builder.AppendLine("copy of the tree it was typed in, and what set that; a host running legs finds its");
        builder.AppendLine("main copy beside the copy it was sent, never from config.json. So worktrees do not");
        builder.AppendLine("share a copy on a host, or the lock on it, and their legs there run side by side;");
        builder.AppendLine("each worktree's first sync to a host carries its whole tree. A worktree made");
        builder.AppendLine("elsewhere, under the name of one that has a copy, is refused that copy while the");
        builder.AppendLine("other exists. This machine records which hosts hold a copy of which worktree, in");
        builder.AppendLine($"{HarnessLayout.HostCopiesDirectoryRelative} in the main checkout, and delete-worktree asks each of");
        builder.AppendLine("them to remove it.");
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

    /// <summary>
    /// What a build that failed says of the units it failed to build, and how <see cref="KeepGoing.Option"/> has each build
    /// tool go on past them: every way, from the adapters' own (<see cref="IBuildAdapter.KeepGoingWays"/>), so help names no
    /// way the code does not go by.
    /// </summary>
    private static void AppendFailedUnits(StringBuilder builder)
    {
        builder.AppendLine("Units a build failed");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "A leg whose build its own tool failed names each unit it failed to build: by its source, as ninja's manifest "
            + "names the source a failed step compiles - a step that links or runs a command by its output - or by the "
            + "target make says failed, which is the object it was building. Its line says how many, they are listed "
            + "beneath the table, and --json carries them as failedUnits: each unit's source and step, the count, the tool, "
            + "whether its tool went on past failing units, how many failing lines could not be told its own, and how far it "
            + "got. Only the build's own steps are units: ninja's FAILED line directly after a status line of its own - one "
            + "printing what a build line of its manifest prints, at a count no lower than the build stands at and no more "
            + "than one higher, two after a console step's FAILED line, whose end has no status, and any higher while a "
            + "console step runs, for ninja holds back every other step's lines until it ends and then writes only some of "
            + "their statuses - and its last such status for how far it got, one more where a console step's FAILED line or "
            + "start came after it, and not said where a console step that did not fail was followed by statuses of its own, "
            + "which it may have ended before or after; samurai's failed jobs whose commands a build line runs, and its last "
            + "status of its own - written as each step starts, none for one it starts while a console step runs - for how far "
            + "it got, never one more for a console step's end, and not said where that status is a console step's start before "
            + "the last step it planned; make's failing step of a target's own makefile, and a failure of CMake's driving "
            + "makefiles that no deeper failure of its target explains, read by its structure in any language - the makefile "
            + "and line parted from the target as each catalog writes the colon, French's ' : ' and Simplified Chinese's "
            + "full-width one among them - and whatever signal ended it. A build nested in a step - an external project's - is "
            + "that step's unit, never its own lines: a failing line ninja wrote is a nested build's only within a failed step's "
            + "output, before the nested build's own end, and a failure make wrote only where a shallower step known to be the "
            + "build's own failed after it - its makefile read and none of the driving ones, or, where no failure names a "
            + "makefile, as make before 4 writes them, its target no driving makefile's goal. One that cannot be told the "
            + "build's own or a nested build's - after a status line not its own, "
            + "as every one is where NINJA_STATUS spells the status otherwise - is counted on the line as perhaps a unit of its "
            + "own, never read as clean; and check-mutations judges an arm's build by the same reading: its own failing steps "
            + "and every one that cannot be told apart, a nested build's alone left out. Where which units failed is not read "
            + "- MSBuild's, dotnet's and dart's output is not, nor a log that could not be read back, nor one in which no line of the "
            + "build's own named a failed unit - the line says so and why, never that none failed. ninja and make stop at "
            + "their first failures, so the line of a build of theirs not kept going says that every unit it never reached "
            + "was not built and says nothing of the code, and - where ninja's own [run/planned] status says it - how many "
            + "of the steps it had to run it ran: 'ran 22 of the 1093 steps it had to run'.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"{KeepGoing.Option}, on build, test and run - and keepGoing on a workflow's build, test and run steps - has each "
            + "leg's build go on past its failing units, so its line names every one. Each build tool goes on its own way, "
            + "by the generator the leg's toolchain names or its project's type:");
        builder.AppendLine();

        foreach (var way in BuildAdapters.All.SelectMany(adapter => adapter.KeepGoingWays))
        {
            var how = way.How switch
            {
                KeepGoingHow.WhenTold => $"given {string.Join(' ', way.Arguments)}",
                KeepGoingHow.ByItself => "given nothing",
                _ => $"refused ({HarnessExit.Refused})",
            };

            builder.AppendLine($"  {way.Builds}");
            AppendWrapped(builder, $"{how}: {way.Said}.", lead: "    ");
        }

        builder.AppendLine();
        AppendWrapped(
            builder,
            $"A toolchain that names no generator - CMake would pick its build tool as it configures - and a generator no way "
            + $"is known for, Xcode's among them, refuse {KeepGoing.Option} for each leg they build before anything runs "
            + $"({HarnessExit.Refused}), naming every one; and a run that builds none of its legs refuses it too ({HarnessExit.UsageError}). "
            + "A leg that builds in a copy of its tree builds by the copy's configuration, so where the copy's gives its "
            + $"tool no way, its build is refused before it starts ({HarnessExit.Refused}): no tool is run without its way "
            + "while the build was asked for it. Whether a tool went on is read from what its build ran with, and a ninja "
            + "told to go on ended its build only where its last word says so - 'cannot make progress due to previously "
            + "encountered errors', or an error of its own - a failed step, ninja's FAILED line or samurai's failed job, never "
            + "being its last word: killed after a step failed, it says nothing, and the build is stopped, never one whose "
            + "unrun steps need one that failed. check-mutations takes none: an arm's build is judged by whether, and where, "
            + "it fails.");
    }
}
