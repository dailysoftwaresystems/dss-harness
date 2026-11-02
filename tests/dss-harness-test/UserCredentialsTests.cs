using DssHarness.Core.Agents.Isolation;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;

namespace DssHarness.Tests;

/// <summary>
/// The places a user keeps credentials in outside the tool's slots: the declared list, pinned, and the places it leaves
/// out with why; each place whole on this machine - in the home, where a variable moves it, the Windows ones on Windows
/// alone - and those <c>agentPolicy.hiddenPlaces</c> adds after them; and the key's refusals, said where the configuration
/// is read.
/// </summary>
public sealed class UserCredentialsTests
{
    /// <summary>
    /// The declared list is pinned whole - each place, what it holds, whether it is a directory, the files it leaves read,
    /// the one system it is on and whether its variable names a list - so a change in it is a change someone made; each
    /// place it leaves out says what a build reads there.
    /// </summary>
    [Fact]
    public void TheDeclaredList_IsPinned_AndEachPlaceLeftOutSaysWhatABuildReadsThere()
    {
        Assert.Equal(
            [
                "~/.ssh|ssh's keys|dir|known_hosts,config||",
                "~/.aws|AWS's credentials and sign-in caches|dir|||",
                "$AWS_SHARED_CREDENTIALS_FILE|AWS's credentials and sign-in caches|file|||",
                "$AWS_CONFIG_FILE|AWS's credentials and sign-in caches|file|||",
                "~/.azure|Azure's sign-in tokens|dir|||",
                "$AZURE_CONFIG_DIR|Azure's sign-in tokens|dir|||",
                "$XDG_CONFIG_HOME/gh|gh's login|dir|||",
                "$GH_CONFIG_DIR|gh's login|dir|||",
                "%APPDATA%/GitHub CLI|gh's login|dir||Windows|",
                "~/.kube|Kubernetes' cluster credentials|dir|||",
                "$KUBECONFIG|Kubernetes' cluster credentials|file|||listed",
                "~/.docker/config.json|Docker's registry logins|file|||",
                "$DOCKER_CONFIG/config.json|Docker's registry logins|file|||",
                "~/.netrc|the logins curl and git read|file|||",
                "~/_netrc|the logins curl and git read|file||Windows|",
                "~/.git-credentials|git's credential store|file|||",
                "$XDG_CONFIG_HOME/git/credentials|git's credential store|file|||",
                "~/.gnupg/private-keys-v1.d|GnuPG's private keys|dir|||",
                "~/.gnupg/secring.gpg|GnuPG's private keys|file|||",
                "$GNUPGHOME/private-keys-v1.d|GnuPG's private keys|dir|||",
                "$GNUPGHOME/secring.gpg|GnuPG's private keys|file|||",
                "%APPDATA%/gnupg/private-keys-v1.d|GnuPG's private keys|dir||Windows|",
                "%APPDATA%/gnupg/secring.gpg|GnuPG's private keys|file||Windows|",
                "~/.config/gcloud|Google Cloud's credentials|dir|||",
                "$CLOUDSDK_CONFIG|Google Cloud's credentials|dir|||",
                "%APPDATA%/gcloud|Google Cloud's credentials|dir||Windows|",
                "~/.npmrc|npm's tokens|file|||",
                "$NPM_CONFIG_USERCONFIG|npm's tokens|file|||",
                "$npm_config_userconfig|npm's tokens|file|||",
                "~/.pypirc|PyPI's upload tokens|file|||",
                "~/.cargo/credentials.toml|Cargo's registry tokens|file|||",
                "~/.cargo/credentials|Cargo's registry tokens|file|||",
                "$CARGO_HOME/credentials.toml|Cargo's registry tokens|file|||",
                "$CARGO_HOME/credentials|Cargo's registry tokens|file|||",
                "~/.m2/settings-security.xml|Maven's master password|file|||",
                "~/.gradle/gradle.properties|Gradle's properties with their publishing and signing credentials|file|||",
                "$GRADLE_USER_HOME/gradle.properties|Gradle's properties with their publishing and signing credentials|file|||",
            ],
            UserCredentials.Declared.Select(place => string.Join('|', place.Spelled, place.Holds, place.Directory ? "dir" : "file", string.Join(',', place.Readable), place.Only?.ToString() ?? string.Empty, place.Listed ? "listed" : string.Empty)));
        Assert.Equal(
            [
                new LeftOutPlace("~/.nuget/NuGet/NuGet.Config", "every restore reads its package sources there and refuses one it cannot read"),
                new LeftOutPlace("%APPDATA%/NuGet/NuGet.Config", "every restore reads its package sources there and refuses one it cannot read"),
                new LeftOutPlace("~/.m2/settings.xml", "every Maven build reads its mirrors and repositories there and refuses one it cannot read"),
            ],
            UserCredentials.LeftOut);
    }

    /// <summary>
    /// Off Windows each declared place is whole in the home - ssh's directory with its known_hosts and config left read -
    /// the configuration directory where XDG_CONFIG_HOME names it, and each place a tool's own variable moves its own to
    /// beside the home's, each entry of a list; a variable that names nothing whole moves nothing; none of Windows' own
    /// places is among them.
    /// </summary>
    [Fact]
    public void OffWindows_EachDeclaredPlace_IsWholeInTheHome_AndWhereAToolsVariableMovesIt()
    {
        using var home = new TempDirectory();
        using var elsewhere = new TempDirectory();
        string Home(params string[] parts) => Path.Combine([home.Path, .. parts]);
        string Elsewhere(params string[] parts) => Path.Combine([elsewhere.Path, .. parts]);
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["XDG_CONFIG_HOME"] = Elsewhere("config"),
            ["GNUPGHOME"] = Elsewhere("gnupg"),
            ["KUBECONFIG"] = string.Join(Path.PathSeparator, Elsewhere("k1"), "relative", Elsewhere("k2")),
            ["DOCKER_CONFIG"] = "not-whole",
        };

        var places = UserCredentials.Of(new HarnessConfig(), home.Path, PlatformId.Linux, name => variables.GetValueOrDefault(name));

        Assert.Equal(
            [
                (Home(".ssh"), true, string.Join('|', Home(".ssh", "known_hosts"), Home(".ssh", "config"))),
                (Home(".aws"), true, string.Empty),
                (Home(".azure"), true, string.Empty),
                (Elsewhere("config", "gh"), true, string.Empty),
                (Home(".kube"), true, string.Empty),
                (Elsewhere("k1"), false, string.Empty),
                (Elsewhere("k2"), false, string.Empty),
                (Home(".docker", "config.json"), false, string.Empty),
                (Home(".netrc"), false, string.Empty),
                (Home(".git-credentials"), false, string.Empty),
                (Elsewhere("config", "git", "credentials"), false, string.Empty),
                (Home(".gnupg", "private-keys-v1.d"), true, string.Empty),
                (Home(".gnupg", "secring.gpg"), false, string.Empty),
                (Elsewhere("gnupg", "private-keys-v1.d"), true, string.Empty),
                (Elsewhere("gnupg", "secring.gpg"), false, string.Empty),
                (Home(".config", "gcloud"), true, string.Empty),
                (Home(".npmrc"), false, string.Empty),
                (Home(".pypirc"), false, string.Empty),
                (Home(".cargo", "credentials.toml"), false, string.Empty),
                (Home(".cargo", "credentials"), false, string.Empty),
                (Home(".m2", "settings-security.xml"), false, string.Empty),
                (Home(".gradle", "gradle.properties"), false, string.Empty),
            ],
            places.Select(place => (place.Path, place.Directory, string.Join('|', place.Readable))));
    }

    /// <summary>
    /// On Windows its own places are added - gh's, GnuPG's and Google Cloud's under APPDATA, or AppData\Roaming in the home
    /// where it names none, and _netrc beside .netrc - and the configuration directory is the home's own where
    /// XDG_CONFIG_HOME names none.
    /// </summary>
    [Fact]
    public void OnWindows_ItsOwnPlacesAreAdded_UnderAppDataOrTheHomesRoamingDirectory()
    {
        using var home = new TempDirectory();
        using var appData = new TempDirectory();
        var roaming = Path.Combine(home.Path, "AppData", "Roaming");

        var named = UserCredentials.Of(new HarnessConfig(), home.Path, PlatformId.Windows, name => name == "APPDATA" ? appData.Path : null).Select(place => place.Path).ToList();
        var unnamed = UserCredentials.Of(new HarnessConfig(), home.Path, PlatformId.Windows, _ => null).Select(place => place.Path).ToList();

        foreach (var (places, root) in new[] { (named, appData.Path), (unnamed, roaming) })
        {
            Assert.Equal(
                [
                    Path.Combine(root, "GitHub CLI"),
                    Path.Combine(home.Path, "_netrc"),
                    Path.Combine(root, "gnupg", "private-keys-v1.d"),
                    Path.Combine(root, "gnupg", "secring.gpg"),
                    Path.Combine(root, "gcloud"),
                ],
                places.Where(place => place.StartsWith(root, StringComparison.Ordinal) || place == Path.Combine(home.Path, "_netrc")));
            Assert.Contains(Path.Combine(home.Path, ".config", "gh"), places);
        }

        Assert.DoesNotContain(UserCredentials.Of(new HarnessConfig(), home.Path, PlatformId.MacOs, _ => null), place => place.Path.StartsWith(roaming, StringComparison.Ordinal) || place.Path == Path.Combine(home.Path, "_netrc"));
    }

    /// <summary>
    /// The places agentPolicy.hiddenPlaces names are added after the declared ones, never taking one away: under ~/ in the
    /// home, a whole path as it is, a directory where it ends with a separator, else a file; one whole on another system
    /// alone is none of this machine's.
    /// </summary>
    [Fact]
    public void TheConfiguredPlaces_AreAddedAfterTheDeclaredOnes_NeverTakingOneAway()
    {
        using var home = new TempDirectory();
        using var elsewhere = new TempDirectory();
        var other = OperatingSystem.IsWindows() ? "/etc/vault/token" : @"C:\vault\token";
        var config = new HarnessConfig { AgentPolicy = new AgentPolicyConfig { HiddenPlaces = ["~/.vault-token", "~/.config/sops/", elsewhere.Path + Path.DirectorySeparatorChar, Path.Combine(elsewhere.Path, "token"), other] } };

        var places = UserCredentials.Of(config, home.Path, PlatformId.Linux, _ => null);
        var declared = UserCredentials.Of(new HarnessConfig(), home.Path, PlatformId.Linux, _ => null);

        Assert.Equal(declared.Select(place => (place.Path, place.Directory)), places.Take(declared.Count).Select(place => (place.Path, place.Directory)));
        Assert.Equal(
            [
                (Path.Combine(home.Path, ".vault-token"), false),
                (Path.Combine(home.Path, ".config", "sops"), true),
                (elsewhere.Path, true),
                (Path.Combine(elsewhere.Path, "token"), false),
            ],
            places.Skip(declared.Count).Select(place => (place.Path, place.Directory)));
    }

    /// <summary>
    /// agentPolicy.hiddenPlaces is refused given empty - leaving it out adds none - and each entry that names no one place
    /// from the root, a drive or the home, climbs with '..', holds a pattern, or is a root or the home itself, by its whole
    /// sentence; each whole place, under ~/ or from a root or a drive, reads.
    /// </summary>
    [Fact]
    public void TheKey_IsRefusedGivenEmpty_AndEachEntryThatNamesNoOnePlace()
    {
        Assert.Empty(UserCredentials.Problems(null));
        Assert.Empty(UserCredentials.Problems(new AgentPolicyConfig()));
        Assert.Empty(UserCredentials.Problems(new AgentPolicyConfig { HiddenPlaces = ["~/.vault-token", "/etc/secrets/", @"C:\secrets\token"] }));
        Assert.Equal(
            ["agentPolicy.hiddenPlaces is given empty: leave the key out to hide no place but the declared ones"],
            UserCredentials.Problems(new AgentPolicyConfig { HiddenPlaces = [] }));
        Assert.Equal(
            [
                "agentPolicy.hiddenPlaces[0] 'secrets/token' is no whole path: name a place from the root, a drive or ~/",
                "agentPolicy.hiddenPlaces[1] '' is no whole path: name a place from the root, a drive or ~/",
                "agentPolicy.hiddenPlaces[2] '~vault' is no whole path: name a place from the root, a drive or ~/",
                "agentPolicy.hiddenPlaces[3] '~/../other/.netrc' climbs with '..': name the place itself",
                "agentPolicy.hiddenPlaces[4] '~/.config/*/token' holds a pattern, which names no one place: name each place whole",
                "agentPolicy.hiddenPlaces[5] '~/' is the home itself, which holds the trees an agent works in: name the places inside it that hold credentials",
                "agentPolicy.hiddenPlaces[6] '/' is a root itself, which holds the trees an agent works in: name the places inside it that hold credentials",
                @"agentPolicy.hiddenPlaces[7] 'C:\' is a root itself, which holds the trees an agent works in: name the places inside it that hold credentials",
            ],
            UserCredentials.Problems(new AgentPolicyConfig { HiddenPlaces = ["secrets/token", string.Empty, "~vault", "~/../other/.netrc", "~/.config/*/token", "~/", "/", @"C:\"] }));
    }

    /// <summary>
    /// The configuration's reader takes agentPolicy.hiddenPlaces by that name, and refuses a malformed one where the file
    /// is read, by its whole sentence, beside the policy's other problems.
    /// </summary>
    [Fact]
    public void TheConfigurationsReader_TakesTheKey_AndRefusesAMalformedHiddenPlace()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, ".harness-config", "config.json");
        var store = new JsonConfigStore(new PassThroughFileSystem(new PhysicalFileSystem(FilePermissionsFactory.Create())));
        temp.WriteFile(".harness-config/config.json", "{ \"agentPolicy\": { \"hiddenPlaces\": [\"~/.vault-token\", \"/etc/secrets/\"] } }");

        Assert.Equal(["~/.vault-token", "/etc/secrets/"], store.Load(path).AgentPolicy!.HiddenPlaces);

        temp.WriteFile(".harness-config/config.json", "{ \"agentPolicy\": { \"hiddenPlaces\": [\"secrets/token\"] } }");

        var failure = Assert.Throws<ConfigException>(() => store.Load(path));

        Assert.Equal(
            $"'{path}' could not be read: agentPolicy.hiddenPlaces[0] 'secrets/token' is no whole path: name a place from the root, a drive or ~/",
            failure.Message);
        Assert.Equal(
            ["agentPolicy.hiddenPlaces[0] 'secrets/token' is no whole path: name a place from the root, a drive or ~/"],
            AgentPolicy.RuleProblems(new HarnessConfig { AgentPolicy = new AgentPolicyConfig { HiddenPlaces = ["secrets/token"] } }));
    }
}
