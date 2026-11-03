using System.Security.Cryptography;
using System.Text;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;

namespace DssHarness.Core.Agents.Isolation;

/// <summary>
/// The tool caches an isolated child writes, declared here alone and said in help: each a variable that tells one tool
/// where its cache is, pointed inside the cache directory of the child's write scope (<see cref="Directory"/>) - never at the
/// user's own cache. A package or build cache is code a later build runs: a package the global NuGet folder holds is
/// restored without a check and its build files run, a crate's or a module's extracted sources are compiled as they lie,
/// a wheel or an object file is used as it is. Written by an agent's commands, the user's own cache would carry what they
/// planted into every build outside, so the user's caches stay read-only and isolated children are given caches of their
/// own, which nothing outside the isolation reads. Cargo, Gradle and Dart's pub keep their caches in a home of theirs,
/// which the variable moves whole, so what the user keeps there - a registry, a property, a tool pub activated - is not
/// read inside; Maven's local repository has no variable of its own, so a Maven build inside finds the
/// user's read-only and names another with <c>-Dmaven.repo.local</c>. Moving <c>XDG_CACHE_HOME</c> moves what the user
/// installed under it too: Playwright's browsers are read where they are (<see cref="Installed"/>), and everything else -
/// Hugging Face's models, pre-commit's environments - is fetched again into the scope's own.
/// </summary>
/// <remarks>
/// For the same reason, children whose reach differs never share a cache: each is given the cache directory of its write
/// scope (<see cref="ScopeName"/>) - the places it writes and the sockets outside it reaches - so a package one plants is
/// run only by a child that could write everything it could, never by one that writes another tree, the main checkout's
/// work tree and git directory - a commit's or a push's hooks - or reaches the user's SSH agent, nor by another
/// repository's. A scope's cache stays for the next child of the same reach - the next launch of the agent, the next run
/// of the leg - until no place of the scope is left (<see cref="Sweep"/>).
/// </remarks>
public static class IsolationCaches
{
    /// <summary>The isolation's own directory under the user's cache directory, which holds the caches and every launch's temporary directory.</summary>
    public const string RootName = "dssharness-isolation";

    /// <summary>
    /// The directory under the isolation's own that holds the tool caches: one directory for each write scope
    /// (<see cref="ScopeName"/>), and beside it the record of the places that scope writes (<see cref="ScopeRecordExtension"/>).
    /// </summary>
    public const string Directory = "caches";

    /// <summary>The ending of the file beside a scope's cache directory that names the places its scope writes, one to a line.</summary>
    public const string ScopeRecordExtension = ".scope";

    /// <summary>The directory under the isolation's own that holds each launch's temporary directory.</summary>
    public const string TempDirectory = "tmp";

    /// <summary>
    /// The directory under the isolation's own that holds, for each leg's work held inside the isolation, the empty
    /// directory what the tool starts outside it for that work - a set-up's lines - runs from: beside the caches and the
    /// temporary directories, written by no isolated child, with no tree above it.
    /// </summary>
    public const string OutsideDirectory = "outside";

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

    /// <summary>
    /// The name of the cache directory of a child that writes <paramref name="writableRoots"/> and reaches
    /// <paramref name="sockets"/> outside: one for each write scope, the same wherever and whenever the same scope is asked.
    /// Named by the scope's outermost places - a place written inside another it writes, a runner's step's build directory
    /// in its tree or a sweep's workers, is that tree's, whose writer decides what runs there anyway - and every socket it
    /// reaches, each at its real spelling.
    /// </summary>
    /// <param name="writableRoots">The places the child writes, but its caches and its temporary directory, each at its real spelling.</param>
    /// <param name="sockets">The Unix sockets outside it reaches, each at its real spelling.</param>
    public static string ScopeName(IEnumerable<string> writableRoots, IEnumerable<string> sockets)
    {
        ArgumentNullException.ThrowIfNull(writableRoots);
        ArgumentNullException.ThrowIfNull(sockets);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', ScopeLines(writableRoots, sockets)))))[..16];
    }

    /// <summary>
    /// The cache directory of the scope that writes <paramref name="writableRoots"/> and reaches <paramref name="sockets"/>,
    /// under <paramref name="caches"/>, made where it is not there: its record written first, naming the scope's places, so
    /// no sweep finds the directory with none (<see cref="Sweep"/>).
    /// </summary>
    /// <param name="fileSystem">Writes the record and makes the directory.</param>
    /// <param name="caches">The isolation's cache directory (<see cref="Directory"/> under its own).</param>
    /// <param name="writableRoots">The places the child writes, but its caches and its temporary directory, each at its real spelling.</param>
    /// <param name="sockets">The Unix sockets outside it reaches, each at its real spelling.</param>
    public static string Prepared(IFileSystem fileSystem, string caches, IReadOnlyList<string> writableRoots, IReadOnlyList<string> sockets)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(caches);

        var name = ScopeName(writableRoots, sockets);
        var directory = Path.Combine(caches, name);

        fileSystem.CreateDirectory(caches);
        fileSystem.WriteAllTextAtomic(directory + ScopeRecordExtension, string.Join('\n', ScopeLines(writableRoots, sockets)) + "\n");
        fileSystem.CreateDirectory(directory);

        return directory;
    }

    /// <summary>
    /// Takes away, under <paramref name="caches"/>, the cache of every scope none of whose places is there any more - an
    /// agent's worktree and work directory removed with it - and every directory no record names, a scope's whose record
    /// went before it, or the one cache every child shared before each scope had its own. One that cannot be taken away
    /// is left for the next sweep.
    /// </summary>
    /// <param name="fileSystem">Reads the records and removes what is gone.</param>
    /// <param name="caches">The isolation's cache directory.</param>
    public static void Sweep(IFileSystem fileSystem, string caches)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(caches);

        if (!fileSystem.DirectoryExists(caches))
        {
            return;
        }

        try
        {
            var recorded = new HashSet<string>(StringComparer.Ordinal);

            foreach (var record in fileSystem.EnumerateFiles(caches, recursive: false).Where(file => file.EndsWith(ScopeRecordExtension, StringComparison.Ordinal)))
            {
                var directory = record[..^ScopeRecordExtension.Length];
                var places = fileSystem.ReadAllText(record).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Where(line => line.StartsWith(WrittenLine, StringComparison.Ordinal))
                    .Select(line => line[WrittenLine.Length..]);

                if (places.Any(place => fileSystem.DirectoryExists(place) || fileSystem.FileExists(place)))
                {
                    recorded.Add(Path.GetFileName(directory));
                    continue;
                }

                // The directory before its record, so a scope never stands with a directory and no record while its
                // children may still write it.
                fileSystem.DeleteDirectory(directory);
                fileSystem.DeleteFile(record);
            }

            foreach (var unnamed in fileSystem.EnumerateDirectories(caches).Where(directory => !recorded.Contains(Path.GetFileName(directory))))
            {
                if (!fileSystem.FileExists(unnamed + ScopeRecordExtension))
                {
                    fileSystem.DeleteDirectory(unnamed);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another process sweeping, or a file still held open by a child: left for the next sweep, which finds it again.
        }
    }

    /// <summary>How a record names a place its scope writes.</summary>
    private const string WrittenLine = "written ";

    /// <summary>How a record names a socket outside its scope reaches.</summary>
    private const string SocketLine = "socket ";

    /// <summary>A scope's lines, in order: each outermost place it writes, then each socket it reaches.</summary>
    private static IEnumerable<string> ScopeLines(IEnumerable<string> writableRoots, IEnumerable<string> sockets)
    {
        var roots = writableRoots.Select(Trimmed).Distinct(StringComparer.Ordinal).ToList();

        return roots
            .Where(root => !roots.Any(other => other != root && IsolationProfile.Covers(other, root)))
            .Order(StringComparer.Ordinal)
            .Select(root => WrittenLine + root)
            .Concat(sockets.Select(Trimmed).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(socket => SocketLine + socket));
    }

    /// <summary><paramref name="path"/> without a separator at its end, the root's own kept.</summary>
    private static string Trimmed(string path) => Path.TrimEndingDirectorySeparator(path ?? throw new ArgumentNullException(nameof(path)));

    /// <summary>Each declared cache's variable, pointed at its directory under <paramref name="caches"/>.</summary>
    /// <param name="caches">The cache directory of the child's write scope.</param>
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
