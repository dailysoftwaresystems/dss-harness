using DssHarness.Core.Platform;

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
}
