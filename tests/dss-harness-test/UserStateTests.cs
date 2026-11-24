using DssHarness.Core.Platform;

namespace DssHarness.Tests;

/// <summary>
/// Where this tool keeps its per-user state (<see cref="UserState"/>): in the directory its own variable names, on every
/// system, before the system's default - the isolation's own directory below it - and a value that names no directory
/// refused rather than read as the default; and the test process's own, never the machine user's.
/// </summary>
public sealed class UserStateTests
{
    /// <summary>Every system, for what does not depend on the one the test runs on.</summary>
    private static readonly PlatformId[] Systems = [PlatformId.Windows, PlatformId.Linux, PlatformId.MacOs];

    /// <summary>
    /// The variable of the tool's own names the directory its per-user state is kept in, on every system and before the
    /// system's own places: the state is that directory, and the isolation's own directory is below it, whatever the
    /// user's cache variables name - so moving the tool's state moves the isolation's with it, and nothing of anyone
    /// else's.
    /// </summary>
    [Fact]
    public void TheToolsOwnVariable_NamesWhereItsStateIs_OnEverySystem_BeforeTheSystemsOwnPlaces()
    {
        using var temp = new TempDirectory();
        var state = temp.Combine("state");
        var home = temp.Combine("home");

        string? Environment(string name) => name switch
        {
            UserState.Variable => state,
            "LOCALAPPDATA" => temp.Combine("local"),
            "XDG_CACHE_HOME" => temp.Combine("cache"),
            _ => null,
        };

        var isolation = Path.Combine(state, UserState.IsolationDirectoryName);

        Assert.Equal(state, UserState.DirectoryOf(Environment));
        Assert.Equal([isolation, isolation, isolation], Systems.Select(system => UserState.IsolationDirectory(home, system, Environment)));
    }

    /// <summary>
    /// Where the variable names nothing - not set, or set to nothing - the isolation's own directory is in the user's cache
    /// directory as each system places it: LOCALAPPDATA on Windows, else AppData\Local in the home; XDG_CACHE_HOME
    /// elsewhere, else the home's cache directory, macOS's the Library's.
    /// </summary>
    [Fact]
    public void WhereTheVariableNamesNothing_TheIsolationsDirectory_IsInTheUsersCacheDirectory()
    {
        using var temp = new TempDirectory();
        var home = temp.Combine("home");
        var local = temp.Combine("local");
        var cache = temp.Combine("cache");

        string? Named(string name) => name switch
        {
            UserState.Variable => string.Empty,
            "LOCALAPPDATA" => local,
            "XDG_CACHE_HOME" => cache,
            _ => null,
        };

        Assert.Equal(
            [
                Path.Combine(home, "AppData", "Local", UserState.IsolationDirectoryName),
                Path.Combine(home, ".cache", UserState.IsolationDirectoryName),
                Path.Combine(home, "Library", "Caches", UserState.IsolationDirectoryName),
                Path.Combine(local, UserState.IsolationDirectoryName),
                Path.Combine(cache, UserState.IsolationDirectoryName),
                Path.Combine(cache, UserState.IsolationDirectoryName),
            ],
            Systems.Select(system => UserState.IsolationDirectory(home, system, _ => null))
                .Concat(Systems.Select(system => UserState.IsolationDirectory(home, system, Named))));
    }

    /// <summary>
    /// A variable naming no fully qualified path names no directory, and is refused, naming what it holds - never read as
    /// the system's default, the very place it was set to leave - by the state's directory and the isolation's alike.
    /// </summary>
    [Theory]
    [InlineData("state")]
    [InlineData("~/state")]
    public void AVariableNamingNoFullyQualifiedPath_IsRefused_NeverReadAsTheDefault(string given)
    {
        using var temp = new TempDirectory();

        string? Environment(string name) => name == UserState.Variable ? given : null;

        var state = Assert.Throws<DirectoryNotFoundException>(() => UserState.DirectoryOf(Environment));
        var isolation = Systems.Select(system => Assert.Throws<DirectoryNotFoundException>(() => UserState.IsolationDirectory(temp.Path, system, Environment)).Message);
        var said = $"{UserState.Variable} names '{given}', which is no fully qualified path";

        Assert.Equal(said, state.Message);
        Assert.Equal([said, said, said], isolation);
    }

    /// <summary>
    /// The test process keeps this tool's per-user state in its own, below the suite's temporary root, never in the
    /// machine user's: the state's directory, and the isolation's own directory as the registrations name it from this
    /// process's environment and the machine's own home - so no test reads or writes the settings file, the records or
    /// the caches the machine's user keeps.
    /// </summary>
    [Fact]
    public void TheTestProcess_KeepsTheToolsStateInItsOwn_NeverTheMachineUsers()
    {
        var platform = new HostPlatform();
        var isolation = UserState.IsolationDirectory(platform.HomeDirectory, platform.Current, System.Environment.GetEnvironmentVariable);

        Assert.Equal(
            (TestHost.UserStateDirectory, Path.Combine(TestHost.UserStateDirectory, UserState.IsolationDirectoryName)),
            (UserState.Directory, isolation));
        TestHost.IsKeptInTheProcesssOwnState(UserState.Directory);
        TestHost.IsKeptInTheProcesssOwnState(isolation);
    }
}
