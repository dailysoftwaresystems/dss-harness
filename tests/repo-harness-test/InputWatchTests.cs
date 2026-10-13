using RepoHarness.Core.Execution;

namespace RepoHarness.Tests;

/// <summary>
/// What a watch makes of what it is told, whatever tells it: these tell it themselves, as a platform would, through a
/// kind that watches nothing. A watch that cannot start is never made, and holds nothing; one that is lost while the
/// work runs leaves the span unanswered for; and a directory that goes, arrives or changes its name takes every input
/// in or below it along.
/// </summary>
public sealed class InputWatchTests
{
    private static readonly string[] Inputs = ["README.md", "src/a/x.cpp", "src/a/deep/y.cpp", "src/b/z.cpp"];

    /// <summary>
    /// A watch names the directories its inputs are in to the kind that watches for it, once, and stops that watching
    /// when it is disposed of - once too.
    /// </summary>
    [Fact]
    public void AWatch_HandsItsKindTheDirectoriesItsInputsAreIn_AndStopsItWhenDisposed()
    {
        using var temp = Tree();
        var kind = new ScriptedWatchKind();

        using (var watch = new InputWatch(temp.Path, Inputs, StringComparer.Ordinal, kind))
        {
            Assert.Equal(1, kind.Starts);
            Assert.Equal(
                [(temp.Path, false), (temp.Combine("src", "a"), false), (temp.Combine("src", "a", "deep"), false), (temp.Combine("src", "b"), false)],
                kind.Targets!.OrderBy(target => target.Directory, StringComparer.Ordinal));
            Assert.Equal(kind.Targets!.OrderBy(target => target.Directory, StringComparer.Ordinal), watch.Watched);
            Assert.Same(watch, kind.Sink);
            Assert.Equal(0, kind.Stops);
        }

        Assert.Equal(1, kind.Stops);
    }

    /// <summary>
    /// A directory that holds inputs, or lies above some, arriving, going or changing its name counts every input in or
    /// below it: each went or arrived with it, whether or not it is back when the work ends.
    /// </summary>
    [Theory]
    [InlineData("src/a", new[] { "src/a/deep/y.cpp", "src/a/x.cpp" })]
    [InlineData("src/a/deep", new[] { "src/a/deep/y.cpp" })]
    [InlineData("src", new[] { "src/a/deep/y.cpp", "src/a/x.cpp", "src/b/z.cpp" })]
    public void ADirectoryThatHoldsInputsOrLiesAboveSome_Named_CountsEveryInputInOrBelowIt(string directory, string[] counted)
    {
        using var temp = Tree();
        var kind = new ScriptedWatchKind();
        using var watch = new InputWatch(temp.Path, Inputs, StringComparer.Ordinal, kind);

        kind.Sink!.Named(temp.Combine(directory.Split('/')));

        Assert.Equal(counted, watch.Changed);
        Assert.Null(watch.Failure);
    }

    /// <summary>The tree itself going takes every input with it.</summary>
    [Fact]
    public void TheTreeItself_Named_CountsEveryInput()
    {
        using var temp = Tree();
        var kind = new ScriptedWatchKind();
        using var watch = new InputWatch(temp.Path, Inputs, StringComparer.Ordinal, kind);

        kind.Sink!.Named(temp.Path);

        Assert.Equal(Inputs.Order(StringComparer.Ordinal), watch.Changed);
    }

    /// <summary>
    /// A directory above no input counts nothing, however it is named: a build directory made beside the sources, an
    /// object directory made below a directory of inputs. It is heard as any path no input is.
    /// </summary>
    [Theory]
    [InlineData("build")]
    [InlineData("src/a/objects")]
    [InlineData("src/ab")]
    [InlineData("sr")]
    public void ADirectoryAboveNoInput_Named_CountsNothing(string directory)
    {
        using var temp = Tree();
        var kind = new ScriptedWatchKind();
        var elsewhere = new List<string>();
        using var watch = new InputWatch(temp.Path, Inputs, StringComparer.Ordinal, kind, heardElsewhere: elsewhere.Add);

        kind.Sink!.Named(temp.Combine(directory.Split('/')));

        Assert.Empty(watch.Changed);
        Assert.Equal([directory], elsewhere);
    }

    /// <summary>
    /// A directory merely written to - as one is each time a file is made in it - took none of its inputs anywhere, and
    /// counts none: a build writing beside sources would otherwise count every source in the directory above it.
    /// </summary>
    [Theory]
    [InlineData("src/a")]
    [InlineData("src")]
    public void ADirectoryAboveInputs_MerelyWrittenTo_CountsNothing(string directory)
    {
        using var temp = Tree();
        var kind = new ScriptedWatchKind();
        using var watch = new InputWatch(temp.Path, Inputs, StringComparer.Ordinal, kind);

        kind.Sink!.Written(temp.Combine(directory.Split('/')));

        Assert.Empty(watch.Changed);
    }

    /// <summary>An input written to, and one named, each count as that input, as word of any change to one always did.</summary>
    [Fact]
    public void AnInputWrittenTo_AndOneNamed_EachCountAsThatInput()
    {
        using var temp = Tree();
        var kind = new ScriptedWatchKind();
        using var watch = new InputWatch(temp.Path, Inputs, StringComparer.Ordinal, kind);

        kind.Sink!.Written(temp.Combine("src", "b", "z.cpp"));
        kind.Sink!.Named(temp.Combine("README.md"));

        Assert.Equal(["README.md", "src/b/z.cpp"], watch.Changed);
    }

    /// <summary>
    /// A directory is found above its inputs as this platform compares paths: where two spellings are one directory, word
    /// of either counts the inputs below it, in the spelling the configuration declared them in.
    /// </summary>
    [Theory]
    [InlineData(true, new[] { "src/a/deep/y.cpp", "src/a/x.cpp" })]
    [InlineData(false, new string[0])]
    public void ADirectoryNamedInAnotherCase_CountsItsInputs_OnlyWherePathsCompareSo(bool ignoringCase, string[] counted)
    {
        using var temp = Tree();
        var kind = new ScriptedWatchKind();
        using var watch = new InputWatch(temp.Path, Inputs, ignoringCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal, kind);

        kind.Sink!.Named(temp.Combine("SRC", "A"));

        Assert.Equal(counted, watch.Changed);
    }

    /// <summary>
    /// Where the watch can be told of something done before it began, as one on macOS can, a directory it is told of
    /// counts only the inputs below it that no longer stand as the work found them: told late of the checkout that made
    /// the directory, it counts none - and told of one moved aside, every input that went with it.
    /// </summary>
    [Theory]
    [InlineData(false, new[] { "src/a/deep/y.cpp", "src/a/x.cpp" })]
    [InlineData(true, new string[0])]
    public async Task ADirectoryToldOfLate_CountsOnlyTheInputsThatNoLongerStandAsFound_WhereTheWatchCanBeToldOfEarlierWrites(
        bool reportsEarlierWrites,
        string[] counted)
    {
        using var temp = Tree();
        var factory = new HarnessFactory();
        var found = await new InputFingerprint(factory.FileSystem, factory.Platform, factory.Output).TakeAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);
        var kind = new ScriptedWatchKind();
        using var watch = new InputWatch(temp.Path, Inputs, StringComparer.Ordinal, kind, reportsEarlierWrites, found.Files);

        kind.Sink!.Named(temp.Combine("src", "a"));

        Assert.Equal(counted, watch.Changed);

        // Moved aside, its inputs are not there to stand as they were found, whatever the watch can be told of.
        Directory.Move(temp.Combine("src", "a"), temp.Combine("src", "a.aside"));
        kind.Sink!.Named(temp.Combine("src", "a"));

        Assert.Equal(["src/a/deep/y.cpp", "src/a/x.cpp"], watch.Changed);
    }

    /// <summary>
    /// A watch told it was lost - events dropped, or the watch itself gone - leaves the span unanswered for: unmeasured
    /// as it ends, in the words it was told, and never unchanged. The first loss told is the one said.
    /// </summary>
    [Fact]
    public void AWatchLostWhileTheWorkRan_IsUnmeasuredAsTheSpanEnds_InTheWordsItWasTold()
    {
        using var temp = Tree();
        var kind = new ScriptedWatchKind();
        using var watch = new InputWatch(temp.Path, Inputs, StringComparer.Ordinal, kind);
        var snapshot = new InputSnapshot([new FileFingerprint("README.md", 6, "abcdef")], []);

        Assert.Null(watch.Failure);
        Assert.Equal(InputChange.Unchanged, InputFingerprint.Compare(snapshot, snapshot, watch).Change);

        kind.Sink!.Lost("the queue of events overflowed");
        kind.Sink!.Lost("and again");

        Assert.Equal("the queue of events overflowed", watch.Failure);

        var comparison = InputFingerprint.Compare(snapshot, snapshot, watch);

        Assert.Equal(
            (InputChange.Unmeasured, "the inputs could not be watched while the phase ran: the queue of events overflowed"),
            (comparison.Change, comparison.Detail));
        Assert.Equal(LegVerdict.Unmeasured, comparison.Verdict()!.Verdict);
    }

    /// <summary>
    /// A watch its kind cannot start is never made: the kind's refusal leaves as it was raised, for the work to be
    /// stopped before it begins - never kept as a watch that failed, which the work would run under to its end.
    /// </summary>
    [Fact]
    public void AWatchItsKindCannotStart_IsNeverMade_AndTheRefusalLeaves()
    {
        using var temp = Tree();
        var kind = ScriptedWatchKind.Refusing();

        var refused = Assert.Throws<InputsUnwatchedException>(() => new InputWatch(temp.Path, Inputs, StringComparer.Ordinal, kind));

        Assert.Equal(ScriptedWatchKind.Refused, refused.Message);
        Assert.Equal(ScriptedWatchKind.SystemsWords, refused.Why);
        Assert.IsType<IOException>(refused.InnerException);
        Assert.Equal(1, kind.Starts);
    }

    /// <summary>A tree that is not there cannot be watched, and its watch is never made: no kind is asked.</summary>
    [Fact]
    public void AWatchOfATreeThatIsNotThere_IsNeverMade_AndSaysSo()
    {
        using var temp = Tree();
        var kind = new ScriptedWatchKind();
        var gone = temp.Combine("gone");

        var refused = Assert.Throws<InputsUnwatchedException>(() => new InputWatch(gone, Inputs, StringComparer.Ordinal, kind));

        Assert.Equal($"the inputs could not be watched, so none of this work was run: '{gone}' is not a directory", refused.Message);
        Assert.Equal(0, kind.Starts);
    }

    /// <summary>
    /// A watch none of whose inputs' directories is there has nothing to be put on, and is never made: said with how many
    /// inputs it was for, and where they were looked for.
    /// </summary>
    [Fact]
    public void AWatchWithNoDirectoryOfAnInputThere_IsNeverMade_AndSaysSo()
    {
        using var temp = new TempDirectory();
        var kind = new ScriptedWatchKind();

        var refused = Assert.Throws<InputsUnwatchedException>(
            () => new InputWatch(temp.Path, ["gen/a.json", "gen/deep/b.json", "out/c.json"], StringComparer.Ordinal, kind));

        Assert.Equal(
            $"the inputs could not be watched, so none of this work was run: no directory holding any of the 3 input(s) is present under '{temp.Path}'",
            refused.Message);
        Assert.Equal(0, kind.Starts);
    }

    /// <summary>
    /// Through a kind with no bound - inotify's hub on Linux - every directory that holds an input is watched for its own
    /// entries, the root among them, and none all the way down: past the 64 .NET's watcher takes, and however far past,
    /// spread over one top-level directory or over many, the tree's other directories stay unheard.
    /// </summary>
    [Theory]
    [InlineData(65, 1)]
    [InlineData(65, 65)]
    [InlineData(5000, 100)]
    public void ThroughAKindWithNoBound_EveryDirectoryOfInputsIsWatchedForItself_NoneAllTheWayDown(int directories, int tops)
    {
        using var temp = new TempDirectory();
        string[] inputs =
        [
            "README.md",
            .. Enumerable.Range(0, directories).Select(index => $"t{index % tops:000}/d{index:0000}/f.cpp"),
        ];

        foreach (var input in inputs)
        {
            temp.WriteFile(input, "f");
        }

        var kind = new ScriptedWatchKind(mostDirectories: null);

        using var watch = new InputWatch(temp.Path, inputs, StringComparer.Ordinal, kind);

        Assert.Equal(
            inputs.Skip(1).Select(input => temp.Combine(input[..input.LastIndexOf('/')].Split('/'))).Append(temp.Path).Order(StringComparer.Ordinal),
            watch.Watched.Select(target => target.Directory));
        Assert.All(watch.Watched, target => Assert.False(target.Recursive));
        Assert.Equal(directories + 1, kind.Targets!.Count);
    }

    /// <summary>A tree holding every one of <see cref="Inputs"/>.</summary>
    private static TempDirectory Tree()
    {
        var temp = new TempDirectory();

        foreach (var input in Inputs)
        {
            temp.WriteFile(input, input);
        }

        return temp;
    }
}
