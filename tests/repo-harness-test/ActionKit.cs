using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Runners;

namespace RepoHarness.Tests;

/// <summary>Reads the action files a test writes: the real file system, this platform, and output nobody reads.</summary>
internal static class ActionKit
{
    /// <summary>The parser a test reads an action file with.</summary>
    public static ActionFileParser Parser()
        => new(
            new PhysicalFileSystem(FilePermissionsFactory.Create()),
            new ConsoleHarnessOutput(new StringWriter(), new StringWriter(), verbose: false),
            new HostPlatform());

    /// <summary><paramref name="text"/>, read as the action file at <paramref name="path"/>.</summary>
    public static ActionFile Parse(string path, string text) => Parser().Parse(path, text);
}
