using DssHarness.Core.Tools;

namespace DssHarness.Tests;

/// <summary>
/// One reading of a tool's version for every place that asks: a host's survey of the copies a leg would start, and
/// <c>install-missing-tools</c>.
/// </summary>
public sealed class ToolVersionTests
{
    /// <summary>
    /// The regex's first group, or its whole match where it has none; with no regex, the last word of the first line;
    /// standard error where nothing came on standard output; and a probe that failed, a regex that matches nothing, or
    /// one that cannot be used, says why.
    /// </summary>
    [Fact]
    public void Of_TakesTheVersionAsTheProbeSaysTo_AndSaysWhyWhereItCannot()
    {
        Assert.Equal(("3.12.3", null), ToolVersion.Of("python3", @"(\d+\.\d+\.\d+)", HostResults.Ok("Python 3.12.3\n")));
        Assert.Equal(("3.12.3", null), ToolVersion.Of("python3", @"\d+\.\d+\.\d+", HostResults.Ok("Python 3.12.3\n")));
        Assert.Equal(("1.12.1", null), ToolVersion.Of("ninja", null, HostResults.Ok("ninja version 1.12.1\nmore\n")));
        Assert.Equal(("2.7.18", null), ToolVersion.Of("python", @"(\d+\.\d+\.\d+)", HostResults.Failed(0, "Python 2.7.18\n")));

        Assert.Equal(
            (null, "'python3' would not report its version there (exit 1): no such interpreter"),
            ToolVersion.Of("python3", @"(\d+\.\d+\.\d+)", HostResults.Failed(1, "no such interpreter\n")));
        Assert.Equal(
            (null, "the probe regex of 'python3' matched nothing in what it printed: Python 3.12"),
            ToolVersion.Of("python3", @"(\d+\.\d+\.\d+)", HostResults.Ok("Python 3.12\n")));

        var (none, broken) = ToolVersion.Of("python3", "(", HostResults.Ok("Python 3.12.3\n"));

        Assert.Null(none);
        Assert.StartsWith("the probe regex of 'python3' could not be used:", broken, StringComparison.Ordinal);
    }

    /// <summary>
    /// A version meets a minimum it is at least, and is held to nothing where either is not major.minor.patch - saying
    /// which, since the two have unrelated remedies.
    /// </summary>
    [Fact]
    public void Meets_ComparesMajorMinorPatch_AndUncomparable_SaysWhichCouldNotBeRead()
    {
        Assert.True(ToolVersion.Meets("3.12.0", "3.12.0"));
        Assert.True(ToolVersion.Meets("3.13.1", "3.12.0"));
        Assert.False(ToolVersion.Meets("3.9.6", "3.12.0"));
        Assert.Null(ToolVersion.Meets("2.47.0.windows.1", "2.40.0"));
        Assert.Null(ToolVersion.Meets("3.12.0", "3.12"));

        Assert.Equal(
            "'2.47.0.windows.1' is not a version this can compare with the minVersion '2.40.0'; give the tool's probe a regex whose first "
            + "group is the version",
            ToolVersion.Uncomparable("2.47.0.windows.1", "2.40.0"));
        Assert.Equal(
            "the minVersion '3.12' is not a version this can compare; write it as major.minor.patch, such as '1.2.0'",
            ToolVersion.Uncomparable("3.12.0", "3.12"));
        Assert.Equal(
            "neither '' nor the minVersion '3.12' is a version this can compare; the minVersion is written as major.minor.patch, and the "
            + "probe's regex captures the version in its first group",
            ToolVersion.Uncomparable(null, "3.12"));
    }

    /// <summary>
    /// A floor is what a declared tool says when it declares a minVersion and a probe to read it with; nothing
    /// otherwise, and the platforms it names, or none for every one.
    /// </summary>
    [Fact]
    public void AFloor_IsATool_WithAMinVersionAndAProbe()
    {
        static DssHarness.Core.Configuration.ToolConfig Tool(string? minVersion = "3.12.0", bool probe = true, List<string>? platforms = null)
            => new()
            {
                Name = "python3",
                MinVersion = minVersion,
                Probe = probe ? new DssHarness.Core.Configuration.ToolProbe { Args = ["--version"], Regex = @"(\d+\.\d+\.\d+)" } : null,
                Platforms = platforms,
            };

        Assert.Equal(
            new ToolFloor { Name = "python3", MinVersion = "3.12.0", Args = ["--version"], Regex = @"(\d+\.\d+\.\d+)", Platforms = ["macos"] },
            ToolFloor.For(Tool(platforms: ["macos"])),
            new FloorComparer());
        Assert.Null(ToolFloor.For(Tool(minVersion: null)));
        Assert.Null(ToolFloor.For(Tool(minVersion: string.Empty)));
        Assert.Null(ToolFloor.For(Tool(probe: false)));
        Assert.Null(ToolFloor.For(Tool(platforms: []))!.Platforms);
        Assert.Equal(["python3"], ToolFloor.Of([Tool(), Tool(minVersion: null)]).Select(floor => floor.Name));
    }

    /// <summary>Compares two floors by what they hold, their lists by their items.</summary>
    private sealed class FloorComparer : IEqualityComparer<ToolFloor?>
    {
        public bool Equals(ToolFloor? x, ToolFloor? y)
            => x is not null && y is not null
                && (x.Name, x.MinVersion, x.Regex) == (y.Name, y.MinVersion, y.Regex)
                && x.Args.SequenceEqual(y.Args)
                && (x.Platforms ?? []).SequenceEqual(y.Platforms ?? []);

        public int GetHashCode(ToolFloor? obj) => obj?.Name.GetHashCode(StringComparison.Ordinal) ?? 0;
    }
}
