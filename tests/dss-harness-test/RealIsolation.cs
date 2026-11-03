using DssHarness.Core.Agents;
using DssHarness.Core.Agents.ClaudeCode;
using DssHarness.Core.Agents.Isolation;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Configuration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;

namespace DssHarness.Tests;

/// <summary>
/// The operating system's isolation run for real, as this machine's mechanism gives it: the probe's mechanism - the test
/// skipped where the mechanism is not this system's, and where none proves itself here, saying why and the remedy, or
/// failed instead where the machine says it holds every build tool - and a step's launch over a real repository, a
/// credential slot holding a canary, its commands' isolation built by the one owner of a profile.
/// </summary>
internal static class RealIsolation
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// This machine's isolation, its probe made through a real process runner as every isolated child is started - this
    /// build's own program the one Windows' mechanism starts again.
    /// </summary>
    public static IsolationPlatform Platform()
        => new(new HostPlatform(), Runner(), self: () => (CliRunner.CliProgram, []));

    /// <summary>A real process runner, its contained step this build's own.</summary>
    public static ProcessRunner Runner()
        => new(new HostPlatform(), FilePermissionsFactory.Create(), CliRunner.CliProgram);

    /// <summary>
    /// The mechanism the probe proved: the test skipped where <paramref name="system"/> is not this one, saying
    /// <paramref name="elsewhere"/>, or where none proves itself here - failed instead where the machine says it holds
    /// every build tool.
    /// </summary>
    /// <param name="platform">This machine's isolation.</param>
    /// <param name="tree">The tree the probe is asked for.</param>
    /// <param name="system">Whether this is the system the mechanism is of.</param>
    /// <param name="elsewhere">Why the test is skipped on any other.</param>
    /// <param name="mechanism">The mechanism, as the failure names it.</param>
    public static async Task<IIsolationMechanism> ProvedAsync(IsolationPlatform platform, string tree, bool system, string elsewhere, string mechanism)
    {
        Assert.SkipUnless(system, elsewhere);

        var found = await platform.ProbeAsync(tree, Token);

        if (found.Mechanism is null)
        {
            BuildTools.Lacks($"This machine has no {mechanism} that proves itself: {found.Unavailable}; {found.Remedy}.");
        }

        return found.Mechanism;
    }

    /// <summary>
    /// A step's launch in its worktree over a real repository: a canary in a credential slot of the main checkout, a Claude
    /// Code directory in <paramref name="home"/> holding a canary of its own, the launch's own directory, and the isolation
    /// its commands run inside and the one a Claude Code launch's whole process runs inside - where the mechanism holds one -
    /// built by the one owner of a profile with the mechanism <paramref name="platform"/> proved.
    /// </summary>
    /// <param name="temp">Where the repository is made.</param>
    /// <param name="home">The home the user's git configuration is read under.</param>
    /// <param name="platform">This machine's isolation, proved.</param>
    public static async Task<StepLaunch> StepLaunchAsync(TempDirectory temp, TempDirectory home, IIsolationPlatform platform)
    {
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("a1");
        var slot = HarnessLayout.SecretSlotDirectoriesUnder(kit.Main)[0];
        Directory.CreateDirectory(slot);
        var canary = Path.Combine(slot, "canary");
        await File.WriteAllTextAsync(canary, "secret", Token);
        var launch = Directory.CreateDirectory(Path.Combine(kit.Layout.Directory, "steps", "think")).FullName;
        var claude = Directory.CreateDirectory(Path.Combine(home.Path, ".claude")).FullName;
        await File.WriteAllTextAsync(Path.Combine(claude, "canary"), "login", Token);
        var config = new HarnessConfig { Worktrees = OrchestrationKit.Settings };
        var ground = AgentStepGround.Of(
            config,
            definition: null,
            new WorkflowStep { Name = "think", Type = "reason" },
            new AgentWorkspace(worktree, kit.Layout.WorkDirectory("a1"), kit.Main, [], worktree, kit.Layout.Directory),
            webSearchGiven: false,
            [claude],
            UserCredentials.Of(config, home.Path, new HostPlatform().Current, _ => null));
        var isolated = await new AgentIsolation(platform, kit.Harness.GitClient, kit.Harness.FileSystem, new HomedPlatform(home.Path))
            .ForLaunchAsync(config, ground, kit.Main, launch, Token);

        return new StepLaunch(kit, worktree, canary, launch, Assert.IsType<ProcessIsolation>(isolated.ForCommands)) { ClaudeCode = isolated.ForClaudeCode, Claude = claude };
    }

    /// <summary>
    /// A home of a test's own where an isolated child reads it as it reads the user's: beside this build's own program,
    /// outside the system's temporary directory, which bubblewrap gives each launch its own of - its name short, so a Unix
    /// socket in it fits the system's bound on a socket's path.
    /// </summary>
    public static TempDirectory Home() => new(AppContext.BaseDirectory, "h");

    /// <summary>
    /// The places a user keeps credentials in, written in <paramref name="home"/> as a user has them: ssh's key beside its
    /// known_hosts and config, AWS's credentials and the logins curl and git read, each key and login holding
    /// <c>secret</c>.
    /// </summary>
    /// <param name="home">The home.</param>
    public static async Task<HomeCredentials> CredentialsAsync(TempDirectory home)
    {
        var credentials = new HomeCredentials(
            home.Combine(".ssh", "id_ed25519"),
            home.Combine(".ssh", "known_hosts"),
            home.Combine(".ssh", "config"),
            home.Combine(".aws", "credentials"),
            home.Combine(OperatingSystem.IsWindows() ? "_netrc" : ".netrc"));
        Directory.CreateDirectory(Path.GetDirectoryName(credentials.Key)!);
        Directory.CreateDirectory(Path.GetDirectoryName(credentials.Aws)!);

        foreach (var (file, text) in new[] { (credentials.Key, "secret"), (credentials.KnownHosts, "known"), (credentials.Config, "Host *"), (credentials.Aws, "secret"), (credentials.Netrc, "secret") })
        {
            await File.WriteAllTextAsync(file, text, Token);
        }

        return credentials;
    }

    /// <summary>Runs <paramref name="program"/> contained and inside <paramref name="isolation"/>, in <paramref name="directory"/>.</summary>
    public static Task<ProcessResult> InsideAsync(ProcessIsolation isolation, string directory, string program, params string[] arguments)
        => InsideAsync(isolation, directory, new Dictionary<string, string?>(), program, arguments);

    /// <summary>Runs <paramref name="program"/> contained and inside <paramref name="isolation"/>, in <paramref name="directory"/>, with <paramref name="environment"/> set.</summary>
    public static Task<ProcessResult> InsideAsync(ProcessIsolation isolation, string directory, IReadOnlyDictionary<string, string?> environment, string program, params string[] arguments)
        => Runner().RunAsync(
            new ProcessRequest
            {
                FileName = program,
                Arguments = arguments,
                WorkingDirectory = directory,
                Environment = environment,
                Timeout = TimeSpan.FromSeconds(60),
                Contained = true,
                Isolation = isolation,
            },
            Token);

    /// <summary>
    /// Holds a command handed over in <paramref name="launch"/> to the one file it ends by writing its working directory to,
    /// in Claude Code's own temporary directory under the launch's own: the isolated shell makes it outside and gives it that
    /// command, which writes it in place; a command removes it by no name, puts no link in its stead - by its name or by a
    /// rename onto it - and writes nothing beside it, the file left as the write left it.
    /// </summary>
    /// <param name="launch">The step's launch.</param>
    public static async Task EndFileWrittenInPlaceAsync(StepLaunch launch)
    {
        var handing = new IsolatedShellHanding(
            Path.Combine(launch.Directory, ClaudeShell.ReceiptsDirectoryName),
            Path.Combine(launch.Directory, ClaudeShell.TempDirectoryName));
        Directory.CreateDirectory(handing.Receipts);
        Directory.CreateDirectory(handing.ProgramTemp);
        var end = handing.ProgramTemp + "/claude-1a2b-cwd";
        var beside = handing.ProgramTemp + "/planted";
        var aimed = Path.Combine(launch.Worktree, "aimed.txt");
        await File.WriteAllTextAsync(aimed, "aimed", Token);

        var (prepared, problem) = IsolatedShell.Prepared(launch.Commands, handing, $"{IsolatedShell.Marked("true", IsolatedShell.NewReceipt())} && pwd -P >| {end}");

        Assert.Null(problem);

        var wrote = await InsideAsync(prepared!, launch.Worktree, "/bin/sh", "-c", "pwd -P >| \"$1\"", "sh", end);

        Assert.Equal(0, wrote.ExitCode);

        var written = await File.ReadAllTextAsync(end, Token);

        Assert.EndsWith("/" + Path.GetFileName(launch.Worktree) + "\n", written, StringComparison.Ordinal);

        foreach (var (how, script) in new[]
        {
            ("removed", "rm -f \"$1\""),
            ("linked by its name", "ln -sf \"$2\" \"$1\""),
            ("renamed onto", "ln -s \"$2\" link && mv -f link \"$1\""),
            ("written beside", ": > \"$3\""),
        })
        {
            var tried = await InsideAsync(prepared!, launch.Worktree, "/bin/sh", "-c", script, "sh", end, aimed, beside);

            Assert.True(tried.ExitCode != 0, $"the file was {how}");
            File.Delete(Path.Combine(launch.Worktree, "link"));
        }

        Assert.Equal((null, written, false), (new FileInfo(end).LinkTarget, await File.ReadAllTextAsync(end, Token), File.Exists(beside)));
    }

    /// <summary>A step's launch over a real repository, made by <see cref="StepLaunchAsync"/>.</summary>
    /// <param name="Kit">The repository.</param>
    /// <param name="Worktree">The step's worktree.</param>
    /// <param name="Canary">The canary in a credential slot of the main checkout.</param>
    /// <param name="Directory">The launch's own directory.</param>
    /// <param name="Commands">The isolation its commands run inside.</param>
    public sealed record StepLaunch(OrchestrationKit Kit, string Worktree, string Canary, string Directory, ProcessIsolation Commands)
    {
        /// <summary>The isolation a Claude Code launch's whole process runs inside; null where it runs outside every layer.</summary>
        public ProcessIsolation? ClaudeCode { get; init; }

        /// <summary>The Claude Code directory, holding a canary of its own.</summary>
        public string Claude { get; init; } = string.Empty;
    }

    /// <summary>The places a user keeps credentials in, made by <see cref="CredentialsAsync"/>.</summary>
    /// <param name="Key">ssh's private key.</param>
    /// <param name="KnownHosts">ssh's known_hosts.</param>
    /// <param name="Config">ssh's config.</param>
    /// <param name="Aws">AWS's credentials.</param>
    /// <param name="Netrc">The logins curl and git read: ~/.netrc, ~/_netrc on Windows.</param>
    public sealed record HomeCredentials(string Key, string KnownHosts, string Config, string Aws, string Netrc);
}
