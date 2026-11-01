using DssHarness.Core.Agents;
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

    /// <summary>This machine's isolation, its probe made through a real process runner as every isolated child is started.</summary>
    public static IsolationPlatform Platform()
        => new(new HostPlatform(), Runner());

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
    /// A step's launch in its worktree over a real repository: a canary in a credential slot of the main checkout, the
    /// launch's own directory, and the isolation its commands run inside, built by the one owner of a profile with the
    /// mechanism <paramref name="platform"/> proved.
    /// </summary>
    /// <param name="temp">Where the repository is made.</param>
    /// <param name="home">The home the user's git configuration is read under.</param>
    /// <param name="platform">This machine's isolation, proved.</param>
    public static async Task<StepLaunch> StepLaunchAsync(TempDirectory temp, TempDirectory home, IsolationPlatform platform)
    {
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("a1");
        var slot = HarnessLayout.SecretSlotDirectoriesUnder(kit.Main)[0];
        Directory.CreateDirectory(slot);
        var canary = Path.Combine(slot, "canary");
        await File.WriteAllTextAsync(canary, "secret", Token);
        var launch = Path.Combine(kit.Layout.Directory, "steps", "think");
        var config = new HarnessConfig { Worktrees = OrchestrationKit.Settings };
        var ground = AgentStepGround.Of(
            config,
            definition: null,
            new WorkflowStep { Name = "think", Type = "reason" },
            new AgentWorkspace(worktree, kit.Layout.WorkDirectory("a1"), kit.Main, [], worktree, kit.Layout.Directory),
            webSearchGiven: false,
            []);
        var isolated = await new AgentIsolation(platform, kit.Harness.GitClient, kit.Harness.FileSystem, new HomedPlatform(home.Path))
            .ForLaunchAsync(config, ground, kit.Main, launch, Token);

        return new StepLaunch(kit, worktree, canary, launch, Assert.IsType<ProcessIsolation>(isolated.ForCommands));
    }

    /// <summary>Runs <paramref name="program"/> contained and inside <paramref name="isolation"/>, in <paramref name="directory"/>.</summary>
    public static Task<ProcessResult> InsideAsync(ProcessIsolation isolation, string directory, string program, params string[] arguments)
        => Runner().RunAsync(
            new ProcessRequest
            {
                FileName = program,
                Arguments = arguments,
                WorkingDirectory = directory,
                Timeout = TimeSpan.FromSeconds(60),
                Contained = true,
                Isolation = isolation,
            },
            Token);

    /// <summary>A step's launch over a real repository, made by <see cref="StepLaunchAsync"/>.</summary>
    /// <param name="Kit">The repository.</param>
    /// <param name="Worktree">The step's worktree.</param>
    /// <param name="Canary">The canary in a credential slot of the main checkout.</param>
    /// <param name="Directory">The launch's own directory.</param>
    /// <param name="Commands">The isolation its commands run inside.</param>
    public sealed record StepLaunch(OrchestrationKit Kit, string Worktree, string Canary, string Directory, ProcessIsolation Commands);
}
