using DssHarness.Core.Platform;

namespace DssHarness.Core.Agents.Isolation;

/// <summary>
/// The tool caches an isolated child writes, declared here alone and said in help: each a variable that tells one tool
/// where its cache is, pointed inside the isolation's own cache directory (<see cref="Directory"/>) - never at the user's
/// own cache. A package or build cache is code a later build runs: a package the global NuGet folder holds is restored
/// without a check and its build files run, a crate's or a module's extracted sources are compiled as they lie, a wheel or
/// an object file is used as it is. Written by an agent's commands, the user's own cache would carry what they planted
/// into every build outside, so the user's caches stay read-only and isolated children share one of their own, which
/// nothing outside the isolation reads. Cargo, Gradle and Dart's pub keep their caches in a home of theirs, which the
/// variable moves whole, so what the user keeps there - a registry, a property, a tool pub activated - is not read
/// inside; Maven's local repository has no variable of its own, so a Maven build inside finds the
/// user's read-only and names another with <c>-Dmaven.repo.local</c>. Moving <c>XDG_CACHE_HOME</c> moves what the user
/// installed under it too: Playwright's browsers are read where they are (<see cref="Installed"/>), and everything else -
/// Hugging Face's models, pre-commit's environments - is fetched again into the isolation's own.
/// </summary>
public static class IsolationCaches
{
    /// <summary>The isolation's own directory under the user's cache directory, which holds the caches and every launch's temporary directory.</summary>
    public const string RootName = "dssharness-isolation";

    /// <summary>The directory under the isolation's own that holds the tool caches.</summary>
    public const string Directory = "caches";

    /// <summary>The directory under the isolation's own that holds each launch's temporary directory.</summary>
    public const string TempDirectory = "tmp";

    /// <summary>The variable a child's temporary directory is named by.</summary>
    public const string TempVariable = "TMPDIR";

    /// <summary>Every declared cache, in the order help names them: the tool, its variable, and its directory under <see cref="Directory"/>.</summary>
    public static IReadOnlyList<IsolationCache> Declared { get; } =
    [
        new("NuGet's packages", "NUGET_PACKAGES", "nuget-packages"),
        new("NuGet's HTTP cache", "NUGET_HTTP_CACHE_PATH", "nuget-http"),
        new("NuGet's plugins cache", "NUGET_PLUGINS_CACHE_PATH", "nuget-plugins"),
        new("npm", "npm_config_cache", "npm"),
        new("Yarn", "YARN_CACHE_FOLDER", "yarn"),
        new("pip", "PIP_CACHE_DIR", "pip"),
        new("uv", "UV_CACHE_DIR", "uv"),
        new("Cargo", "CARGO_HOME", "cargo"),
        new("Go's modules", "GOMODCACHE", "go-modules"),
        new("Go's builds", "GOCACHE", "go-build"),
        new("Gradle", "GRADLE_USER_HOME", "gradle"),
        new("ccache", "CCACHE_DIR", "ccache"),
        new("pnpm's store", "npm_config_store_dir", "pnpm-store"),
        new("Yarn 2 and later", "YARN_GLOBAL_FOLDER", "yarn-berry"),
        new("Bun", "BUN_INSTALL_CACHE_DIR", "bun"),
        new("Dart's and Flutter's pub", "PUB_CACHE", "pub"),
        new("every other cache the XDG directories place", "XDG_CACHE_HOME", "xdg"),
    ];

    /// <summary>The variable Playwright finds its browsers by.</summary>
    public const string PlaywrightBrowsersVariable = "PLAYWRIGHT_BROWSERS_PATH";

    /// <summary>
    /// What the user installed under the cache directory the isolation moves that a build reads as it lies, each read where
    /// it is: on Linux, where <c>XDG_CACHE_HOME</c> places Playwright's browsers, <see cref="PlaywrightBrowsersVariable"/>
    /// at the user's own <c>ms-playwright</c>, where it is there and the variable names none already - read alone, so a
    /// browser installed inside fails. Elsewhere none: macOS's and Windows' Playwright keeps its browsers where the
    /// isolation moves nothing.
    /// </summary>
    /// <param name="home">The user's home.</param>
    /// <param name="platform">This machine's system.</param>
    /// <param name="environment">Reads this process's environment.</param>
    /// <param name="exists">Whether a directory is there.</param>
    public static IReadOnlyList<KeyValuePair<string, string>> Installed(string home, PlatformId platform, Func<string, string?> environment, Func<string, bool> exists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(exists);

        if (platform != PlatformId.Linux || environment(PlaywrightBrowsersVariable) is { Length: > 0 })
        {
            return [];
        }

        var cache = environment("XDG_CACHE_HOME") is { Length: > 0 } named && Path.IsPathFullyQualified(named) ? named : Path.Combine(home, ".cache");
        var browsers = Path.Combine(cache, "ms-playwright");

        return exists(browsers) ? [new(PlaywrightBrowsersVariable, browsers)] : [];
    }

    /// <summary>
    /// The isolation's own directory on this machine: on Windows under <c>LOCALAPPDATA</c> where it names one whole, else
    /// <c>AppData\Local</c> in the home; elsewhere under <c>XDG_CACHE_HOME</c> where it names one whole, else the home's
    /// own cache directory - macOS's the Library's.
    /// </summary>
    /// <param name="home">The user's home.</param>
    /// <param name="platform">This machine's system.</param>
    /// <param name="environment">Reads this process's environment.</param>
    public static string Root(string home, PlatformId platform, Func<string, string?> environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        ArgumentNullException.ThrowIfNull(environment);

        string? Named(string variable) => environment(variable) is { Length: > 0 } named && Path.IsPathFullyQualified(named) ? named : null;

        var cache = platform switch
        {
            PlatformId.Windows => Named("LOCALAPPDATA") ?? Path.Combine(home, "AppData", "Local"),
            PlatformId.MacOs => Named("XDG_CACHE_HOME") ?? Path.Combine(home, "Library", "Caches"),
            _ => Named("XDG_CACHE_HOME") ?? Path.Combine(home, ".cache"),
        };

        return Path.Combine(cache, RootName);
    }

    /// <summary>Each declared cache's variable, pointed at its directory under <paramref name="caches"/>.</summary>
    /// <param name="caches">The isolation's cache directory.</param>
    public static IReadOnlyList<KeyValuePair<string, string>> Environment(string caches)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caches);

        return [.. Declared.Select(cache => new KeyValuePair<string, string>(cache.Variable, Path.Combine(caches, cache.Directory)))];
    }
}

/// <summary>One declared cache of an isolated child's (<see cref="IsolationCaches.Declared"/>).</summary>
/// <param name="Tool">What it caches, as help names it.</param>
/// <param name="Variable">The variable that tells the tool where it is.</param>
/// <param name="Directory">Its directory under the isolation's cache directory.</param>
public sealed record IsolationCache(string Tool, string Variable, string Directory);
