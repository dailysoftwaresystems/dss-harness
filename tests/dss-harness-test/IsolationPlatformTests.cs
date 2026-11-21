using DssHarness.Core.Platform;
using DssHarness.Core.Processes;

namespace DssHarness.Tests;

/// <summary>What the isolation's platform layer says of this machine, in the parts that need no mechanism to hold.</summary>
public sealed class IsolationPlatformTests
{
    /// <summary>
    /// Where Ubuntu restricts unprivileged user namespaces, the remedy names the commands this machine needs: the profile
    /// loaded in place where the apparmor package ships it there, copied first from the extra profiles where they hold
    /// it, and the package that carries those installed first where neither is.
    /// </summary>
    [Theory]
    [InlineData(true, true, "sudo apparmor_parser -r /etc/apparmor.d/bwrap-userns-restrict")]
    [InlineData(true, false, "sudo apparmor_parser -r /etc/apparmor.d/bwrap-userns-restrict")]
    [InlineData(false, true, "sudo cp /usr/share/apparmor/extra-profiles/bwrap-userns-restrict /etc/apparmor.d/ and sudo apparmor_parser -r /etc/apparmor.d/bwrap-userns-restrict")]
    [InlineData(false, false, "sudo apt-get install apparmor-profiles, sudo cp /usr/share/apparmor/extra-profiles/bwrap-userns-restrict /etc/apparmor.d/ and sudo apparmor_parser -r /etc/apparmor.d/bwrap-userns-restrict")]
    public void TheRestrictedRemedy_NamesTheCommandsThisMachineNeeds(bool inPlace, bool extra, string commands)
    {
        var remedy = IsolationPlatform.RestrictedRemedy(
            "/usr/bin/bwrap",
            path => path switch
            {
                IsolationPlatform.RestrictProfile => inPlace,
                IsolationPlatform.ExtraRestrictProfile => extra,
                _ => throw new InvalidOperationException($"'{path}' was asked."),
            });

        Assert.Equal(
            $"Ubuntu restricts unprivileged user namespaces here (/proc/sys/kernel/apparmor_restrict_unprivileged_userns = 1): load the bwrap-userns-restrict AppArmor profile for '/usr/bin/bwrap' ({commands})",
            remedy);
    }

    /// <summary>
    /// The probe's trial runs to its end however long it takes - asked with no time bound of the tool's own - told every
    /// five minutes while it runs, naming the program, and the mechanism it then proves is the machine's: a trial of twelve
    /// minutes is waited out where thirty seconds once ended it unproved.
    /// </summary>
    [Fact]
    public async Task TheIsolationProbe_IsWaitedAndTold()
    {
        using var tree = new TempDirectory();
        var clock = new TimerClock();
        var told = new List<string>();
        var runner = new SlowTrialRunner(clock, TimeSpan.FromMinutes(12));
        var platform = new IsolationPlatform(
            new SaidPlatform(PlatformId.MacOs),
            runner,
            mac: new MacSandboxHost(path => path == SandboxExec.ProgramPath, () => false, () => "15.3", path => path),
            told: wait => told.Add(wait.Line()),
            clock: clock);

        var proved = await platform.ProbeAsync(tree.Path, TestContext.Current.CancellationToken);

        Assert.IsType<SandboxExec>(proved.Mechanism);
        Assert.Null(Assert.Single(runner.Asked).Timeout);
        Assert.Equal(
            ["has waited 5m00s for the isolation probe's '/bin/sh' to end", "has waited 10m00s for the isolation probe's '/bin/sh' to end"],
            told);
    }

    /// <summary>
    /// A process runner whose trial lasts <paramref name="lasts"/> on <paramref name="clock"/>, which it moves itself a
    /// minute at a time once the probe's wait stands on it, and which then holds as a sandbox that holds does: the open place
    /// written, the others left as they were.
    /// </summary>
    private sealed class SlowTrialRunner(TimerClock clock, TimeSpan lasts) : IProcessRunner
    {
        public List<ProcessRequest> Asked { get; } = [];

        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Asked.Add(request);

            for (var minutes = TimeSpan.Zero; minutes < lasts; minutes += TimeSpan.FromMinutes(1))
            {
                await clock.WaitingAsync().WaitAsync(cancellationToken);
                clock.Advance(TimeSpan.FromMinutes(1));
                await Task.Yield();
            }

            File.WriteAllText(Path.Combine(request.Arguments[^2], "w"), string.Empty);

            return new ProcessResult(0, string.Empty, string.Empty, lasts, TimedOut: false);
        }

        public string? FindExecutable(string command) => null;
    }
}
