using System.Globalization;
using DssHarness.Core.Configuration;
using DssHarness.Core.Platform;

namespace DssHarness.Core.Agents.Isolation;

/// <summary>
/// The places a user keeps credentials in outside this tool's slots, declared here alone and said in help: each hidden from
/// every isolated child - an agent's commands and the tool's executions of the agents' work alike - as the slots are, and
/// named by no command of an agent's, beside the places <c>agentPolicy.hiddenPlaces</c> adds. A build that reads one fails
/// inside, so each place is one a build has no need of, or one whose secret a build can be handed as a value instead:
/// the runner's environment carries it, read by the tool, never as a file the child reads.
/// </summary>
/// <remarks>
/// <para>
/// Each place is decided by what reads it. ssh's directory is hidden but its <c>known_hosts</c> and <c>config</c>, which
/// ssh reads by itself to reach a host - a push over ssh signs through the user's SSH agent, whose socket it is handed
/// (<see cref="AgentIsolation.SshAgentSocketVariable"/>), never through a key file. AWS's, Azure's, gh's, Kubernetes',
/// Google Cloud's and Docker's logins, the logins curl and git read (<c>.netrc</c>), git's credential store, GnuPG's
/// private keys, PyPI's, Cargo's and npm's tokens, Maven's master password and Gradle's properties are hidden whole:
/// what a build reads of them - a private registry's token, a cloud's key - it is handed as a value, and inside the
/// isolation Cargo's and Gradle's homes are moved into its own caches (<see cref="IsolationCaches"/>), so neither reads
/// the user's there. Each tool's own variable that moves its place - <c>GNUPGHOME</c>, <c>DOCKER_CONFIG</c>,
/// <c>GH_CONFIG_DIR</c> and their kind - moves what is hidden with it, read from this process's environment, which every
/// child inherits.
/// </para>
/// <para>
/// Left out because builds read them (<see cref="LeftOut"/>): NuGet's user configuration, which every restore reads for
/// its package sources and refuses to read empty, and Maven's <c>settings.xml</c>, which every Maven build reads for its
/// mirrors and repositories - a password either holds is better given as a value (NuGet's
/// <c>NuGetPackageSourceCredentials_&lt;source&gt;</c>, Maven's <c>${env.NAME}</c>). The system's own credential stores -
/// macOS's keychain, Windows' Credential Manager - are services, not places: what a child may ask of them is the
/// mechanism's, and on Linux the keyring is reached through the user's message bus, in the runtime directory the
/// isolation hides.
/// </para>
/// </remarks>
public static class UserCredentials
{
    /// <summary>The variable that names Windows' roaming application data.</summary>
    public const string AppDataVariable = "APPDATA";

    /// <summary>The variable that names the user's configuration directory, <c>~/.config</c> where it names none.</summary>
    public const string ConfigHomeVariable = "XDG_CONFIG_HOME";

    /// <summary>
    /// Every declared place, in the order help names them: where it is - <c>~/</c> the user's home, <c>$NAME</c> the place
    /// this process's variable NAME names whole (<c>$XDG_CONFIG_HOME</c> <c>~/.config</c> where it names none, any other
    /// none), <c>%APPDATA%</c> Windows' roaming application data (<c>AppData\Roaming</c> in the home where it names none) -
    /// what it holds, and whether it is a directory.
    /// </summary>
    public static IReadOnlyList<UserCredential> Declared { get; } =
    [
        new("~/.ssh", "ssh's keys", Directory: true) { Readable = ["known_hosts", "config"] },
        new("~/.aws", "AWS's credentials and sign-in caches", Directory: true),
        new("$AWS_SHARED_CREDENTIALS_FILE", "AWS's credentials and sign-in caches", Directory: false),
        new("$AWS_CONFIG_FILE", "AWS's credentials and sign-in caches", Directory: false),
        new("~/.azure", "Azure's sign-in tokens", Directory: true),
        new("$AZURE_CONFIG_DIR", "Azure's sign-in tokens", Directory: true),
        new("$XDG_CONFIG_HOME/gh", "gh's login", Directory: true),
        new("$GH_CONFIG_DIR", "gh's login", Directory: true),
        new("%APPDATA%/GitHub CLI", "gh's login", Directory: true) { Only = PlatformId.Windows },
        new("~/.kube", "Kubernetes' cluster credentials", Directory: true),
        new("$KUBECONFIG", "Kubernetes' cluster credentials", Directory: false) { Listed = true },
        new("~/.docker/config.json", "Docker's registry logins", Directory: false),
        new("$DOCKER_CONFIG/config.json", "Docker's registry logins", Directory: false),
        new("~/.netrc", "the logins curl and git read", Directory: false),
        new("~/_netrc", "the logins curl and git read", Directory: false) { Only = PlatformId.Windows },
        new("~/.git-credentials", "git's credential store", Directory: false),
        new("$XDG_CONFIG_HOME/git/credentials", "git's credential store", Directory: false),
        new("~/.gnupg/private-keys-v1.d", "GnuPG's private keys", Directory: true),
        new("~/.gnupg/secring.gpg", "GnuPG's private keys", Directory: false),
        new("$GNUPGHOME/private-keys-v1.d", "GnuPG's private keys", Directory: true),
        new("$GNUPGHOME/secring.gpg", "GnuPG's private keys", Directory: false),
        new("%APPDATA%/gnupg/private-keys-v1.d", "GnuPG's private keys", Directory: true) { Only = PlatformId.Windows },
        new("%APPDATA%/gnupg/secring.gpg", "GnuPG's private keys", Directory: false) { Only = PlatformId.Windows },
        new("~/.config/gcloud", "Google Cloud's credentials", Directory: true),
        new("$CLOUDSDK_CONFIG", "Google Cloud's credentials", Directory: true),
        new("%APPDATA%/gcloud", "Google Cloud's credentials", Directory: true) { Only = PlatformId.Windows },
        new("~/.npmrc", "npm's tokens", Directory: false),
        new("$NPM_CONFIG_USERCONFIG", "npm's tokens", Directory: false),
        new("$npm_config_userconfig", "npm's tokens", Directory: false),
        new("~/.pypirc", "PyPI's upload tokens", Directory: false),
        new("~/.cargo/credentials.toml", "Cargo's registry tokens", Directory: false),
        new("~/.cargo/credentials", "Cargo's registry tokens", Directory: false),
        new("$CARGO_HOME/credentials.toml", "Cargo's registry tokens", Directory: false),
        new("$CARGO_HOME/credentials", "Cargo's registry tokens", Directory: false),
        new("~/.m2/settings-security.xml", "Maven's master password", Directory: false),
        new("~/.gradle/gradle.properties", "Gradle's properties with their publishing and signing credentials", Directory: false),
        new("$GRADLE_USER_HOME/gradle.properties", "Gradle's properties with their publishing and signing credentials", Directory: false),
    ];

    /// <summary>The places a user may keep credentials in that the list leaves out, each with why: builds read them.</summary>
    public static IReadOnlyList<LeftOutPlace> LeftOut { get; } =
    [
        new("~/.nuget/NuGet/NuGet.Config", "every restore reads its package sources there and refuses one it cannot read"),
        new("%APPDATA%/NuGet/NuGet.Config", "every restore reads its package sources there and refuses one it cannot read"),
        new("~/.m2/settings.xml", "every Maven build reads its mirrors and repositories there and refuses one it cannot read"),
    ];

    /// <summary>
    /// Every place hidden on this machine, each once: each declared one of this system's - where its variable names one, the
    /// place it names - then each <c>agentPolicy.hiddenPlaces</c> names: <c>~/</c> in the home, a whole path as it is, one
    /// spelt ending with a separator a directory, any other a file; one whole on another system alone is none of this one's.
    /// Whether each is there is the isolation's to see as it builds a profile.
    /// </summary>
    /// <param name="config">The configuration, whose <c>agentPolicy.hiddenPlaces</c> adds places.</param>
    /// <param name="home">The user's home.</param>
    /// <param name="platform">This machine's system.</param>
    /// <param name="environment">Reads this process's environment.</param>
    public static IReadOnlyList<HiddenPlace> Of(HarnessConfig config, string home, PlatformId platform, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        ArgumentNullException.ThrowIfNull(environment);

        var places = new List<HiddenPlace>();

        foreach (var credential in Declared.Where(credential => credential.Only is null || credential.Only == platform))
        {
            foreach (var path in Paths(credential, home, environment))
            {
                places.Add(new HiddenPlace(path, credential.Directory) { Readable = [.. credential.Readable.Select(name => Path.Combine(path, name))] });
            }
        }

        foreach (var spelled in config.AgentPolicy?.HiddenPlaces ?? [])
        {
            var directory = spelled.EndsWith('/') || spelled.EndsWith('\\');
            var path = PlatformPaths.IsHomeRelative(spelled)
                ? Path.Combine([home, .. spelled[2..].Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)])
                : Path.IsPathFullyQualified(spelled) ? spelled : null;

            if (path is not null)
            {
                places.Add(new HiddenPlace(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), directory));
            }
        }

        return [.. places.DistinctBy(place => place.Path, platform == PlatformId.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)];
    }

    /// <summary>
    /// Why <paramref name="policy"/>'s <c>hiddenPlaces</c> does not read, each by its entry: the list given empty - leaving
    /// it out adds none - or an entry that names no one place from the root, a drive or the home, climbs with <c>..</c>,
    /// holds a pattern, or names a root or the home itself, which hold the trees an agent works in.
    /// </summary>
    /// <param name="policy">The configuration's <c>agentPolicy</c>; null where it has none.</param>
    public static IReadOnlyList<string> Problems(AgentPolicyConfig? policy)
    {
        if (policy?.HiddenPlaces is not { } places)
        {
            return [];
        }

        if (places.Count == 0)
        {
            return ["agentPolicy.hiddenPlaces is given empty: leave the key out to hide no place but the declared ones"];
        }

        var problems = new List<string>();

        foreach (var (place, at) in places.Select((place, at) => (place, at.ToString(CultureInfo.InvariantCulture))))
        {
            var named = $"agentPolicy.hiddenPlaces[{at}] '{place}'";
            var home = PlatformPaths.IsHomeRelative(place);

            if (!home && !PlatformPaths.IsAbsoluteOnAnyPlatform(place))
            {
                problems.Add($"{named} is no whole path: name a place from the root, a drive or ~/");
            }
            else if (PlatformPaths.ClimbsOut(place))
            {
                problems.Add($"{named} climbs with '..': name the place itself");
            }
            else if (place.IndexOfAny(['*', '?', '[']) >= 0)
            {
                problems.Add($"{named} holds a pattern, which names no one place: name each place whole");
            }
            else if ((home ? place[2..] : place).TrimEnd('/', '\\') is var rest && (rest.Length == 0 || (!home && PlatformPaths.NamesADrive(rest) && rest.Length == 2)))
            {
                problems.Add($"{named} is {(home ? "the home" : "a root")} itself, which holds the trees an agent works in: name the places inside it that hold credentials");
            }
        }

        return problems;
    }

    /// <summary>
    /// Each place <paramref name="credential"/> names on this machine, whole: none where its variable names none, and of a
    /// list each whole entry, split as this machine splits its PATH.
    /// </summary>
    private static IEnumerable<string> Paths(UserCredential credential, string home, Func<string, string?> environment)
    {
        var cut = credential.Spelled.IndexOf('/', StringComparison.Ordinal);
        var head = cut < 0 ? credential.Spelled : credential.Spelled[..cut];
        string[] rest = cut < 0 ? [] : credential.Spelled[(cut + 1)..].Split('/');

        IEnumerable<string> bases = head switch
        {
            "~" => [home],
            "%APPDATA%" => [Whole(environment(AppDataVariable)) ?? Path.Combine(home, "AppData", "Roaming")],
            "$" + ConfigHomeVariable => [Whole(environment(ConfigHomeVariable)) ?? Path.Combine(home, ".config")],
            _ when head.StartsWith('$') && credential.Listed => (environment(head[1..]) ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(Whole)
                .OfType<string>(),
            _ when head.StartsWith('$') => Whole(environment(head[1..])) is { } named ? [named] : [],
            _ => throw new InvalidOperationException($"'{credential.Spelled}' begins with none of ~/, $NAME or %APPDATA%"),
        };

        return bases.Select(at => Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine([at, .. rest]))));
    }

    /// <summary><paramref name="value"/> where it is a whole path; null where it is none.</summary>
    private static string? Whole(string? value) => value is { Length: > 0 } && Path.IsPathFullyQualified(value) ? value : null;
}

/// <summary>One place a user keeps credentials in, as <see cref="UserCredentials.Declared"/> spells it.</summary>
/// <param name="Spelled">Where it is: <c>~/</c>, <c>$NAME</c> or <c>%APPDATA%</c>, then the path under it.</param>
/// <param name="Holds">What it holds, as help names it.</param>
/// <param name="Directory">Whether it is a directory; a file where false - what is made, empty, where it is not there and a command could make it.</param>
public sealed record UserCredential(string Spelled, string Holds, bool Directory)
{
    /// <summary>The files inside it a program reads by itself, which stay read: ssh's <c>known_hosts</c> and <c>config</c>.</summary>
    public IReadOnlyList<string> Readable { get; init; } = [];

    /// <summary>The one system it is a place on; every system where null.</summary>
    public PlatformId? Only { get; init; }

    /// <summary>Whether the variable it begins with names a list of places, split as this machine splits its PATH: <c>KUBECONFIG</c>.</summary>
    public bool Listed { get; init; }
}

/// <summary>A place a user may keep credentials in that <see cref="UserCredentials.Declared"/> leaves out, and why.</summary>
/// <param name="Spelled">Where it is, as the declared list spells a place.</param>
/// <param name="Why">What a build reads there, which would fail were it hidden.</param>
public sealed record LeftOutPlace(string Spelled, string Why);

/// <summary>
/// A place hidden from every isolated child and named by no command of an agent's - one a user keeps credentials in -
/// whole on this machine, with the files inside it that stay read.
/// </summary>
/// <param name="Path">The place, whole.</param>
/// <param name="Directory">Whether it is a directory; a file where false - what is made, empty, where it is not there and a command could make it.</param>
public sealed record HiddenPlace(string Path, bool Directory)
{
    /// <summary>The files inside it that stay read, each whole: what a program reads there by itself.</summary>
    public IReadOnlyList<string> Readable { get; init; } = [];
}
