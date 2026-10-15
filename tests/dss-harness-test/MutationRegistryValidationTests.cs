using System.Reflection;
using System.Text;
using NSubstitute;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Mutations;
using DssHarness.Core.Repository;
using DssHarness.Core.Sync;

namespace DssHarness.Tests;

/// <summary>
/// A registry's text validated against a view of a tree's files: the tree as it stands, or the tree as a command would
/// leave it - some paths written, some removed - so a text nobody has written yet is judged by the files it will stand
/// among, and nothing is read that the validation does not ask for.
/// </summary>
public sealed class MutationRegistryValidationTests
{
    private const string Registry = "mutations/arms.txt";

    private const string Charge = "A | charge | src/fixture.cpp | mutations/texts/charge.before | mutations/texts/charge.after | TEST-RED | fixture | fixture_tests | 3 | mutations/texts/charge.diag | the charge is pinned";

    private const string Floor = "A | floor | src/fixture.cpp | mutations/texts/floor.before | mutations/texts/charge.after | TEST-RED | fixture | fixture_tests | 3 | mutations/texts/floor.diag | the floor is pinned";

    /// <summary>The registry the tree holds: one arm, with the row it needs.</summary>
    private static readonly string[] Held = [Charge, "C | charge | Fixture.Charge | red"];

    /// <summary>A candidate: the tree's arm, and one more citing two texts the tree does not hold.</summary>
    private static readonly string[] Candidate = [.. Held, Floor, "C | floor | Fixture.Floor | red"];

    /// <summary>
    /// A candidate is judged by the files the tree will hold. A text it cites that only the command's writes hold is no
    /// problem, and is read as the command writes it, whatever the tree holds under that path today; one the command
    /// removes is not a file in the tree; and one the command writes into the text directory that no row cites is a
    /// text nobody drives.
    /// </summary>
    [Fact]
    public async Task ACandidateText_IsValidatedAgainstTheFilesTheTreeWillHold()
    {
        using var temp = new TempDirectory();
        var config = Config();
        var tree = await TreeAsync(temp, config);
        var token = TestContext.Current.CancellationToken;

        var asItStands = await MutationRegistryValidation.ValidateAsync(Text(Candidate), tree, config.Mutations, config, token);

        Assert.Equal(
            [
                "line 3: text 'mutations/texts/floor.before' is not a file in the tree",
                "line 3: text 'mutations/texts/floor.diag' is not a file in the tree",
            ],
            asItStands.Problems);

        var written = new OverlaidRegistryFiles(tree, new Dictionary<string, Func<CancellationToken, Task<byte[]>>?>
        {
            ["mutations/texts/floor.before"] = Holding("c > 0"),
            ["mutations/texts/floor.diag"] = Holding("floor exceeded"),
        });

        Assert.Empty((await MutationRegistryValidation.ValidateAsync(Text(Candidate), written, config.Mutations, config, token)).Problems);

        // What the command writes is what is read: a text the tree holds whole and the command empties holds nothing,
        // and one the tree holds empty and the command fills holds something.
        temp.WriteFile("mutations/texts/charge.diag", string.Empty);

        var rewritten = new OverlaidRegistryFiles(tree, new Dictionary<string, Func<CancellationToken, Task<byte[]>>?>
        {
            ["mutations/texts/charge.before"] = Holding("\n"),
            ["mutations/texts/charge.diag"] = Holding("charge exceeded"),
        });

        Assert.Equal(
            ["line 1: the text in 'mutations/texts/charge.before' holds nothing, which occurs everywhere and which every run says"],
            (await MutationRegistryValidation.ValidateAsync(Text(Held), rewritten, config.Mutations, config, token)).Problems);

        temp.WriteFile("mutations/texts/charge.diag", "charge exceeded");

        var removed = new OverlaidRegistryFiles(tree, new Dictionary<string, Func<CancellationToken, Task<byte[]>>?> { ["mutations/texts/charge.diag"] = null });

        Assert.Equal(
            ["line 1: text 'mutations/texts/charge.diag' is not a file in the tree"],
            (await MutationRegistryValidation.ValidateAsync(Text(Held), removed, config.Mutations, config, token)).Problems);

        var uncited = new OverlaidRegistryFiles(tree, new Dictionary<string, Func<CancellationToken, Task<byte[]>>?> { ["mutations/texts/stray.diag"] = Holding("nobody's") });

        Assert.Equal(
            ["'mutations/texts/stray.diag' is in the text directory and no row cites it: a mutation text nobody drives"],
            (await MutationRegistryValidation.ValidateAsync(Text(Held), uncited, config.Mutations, config, token)).Problems);
    }

    /// <summary>
    /// A path the command writes is not withheld, whatever a sync says of the path in the tree as it stands, and is
    /// listed in the text directory as a file a copy would hold; every other path is withheld or not as the tree says.
    /// </summary>
    [Fact]
    public async Task AWrittenPath_IsNotWithheld_AndAnyOtherIsAsTheTreeSays()
    {
        using var temp = new TempDirectory();
        var config = Config();
        var tree = await TreeAsync(temp, config, Withholding("mutations/texts/charge.diag", "mutations/texts/charge.before"));
        var token = TestContext.Current.CancellationToken;
        var written = new OverlaidRegistryFiles(tree, new Dictionary<string, Func<CancellationToken, Task<byte[]>>?>
        {
            ["mutations/texts/charge.diag"] = Holding("charge exceeded"),
        });

        Assert.True(tree.IsWithheld("mutations/texts/charge.diag"));
        Assert.False(written.IsWithheld("mutations/texts/charge.diag"));
        Assert.True(written.IsWithheld("mutations/texts/charge.before"));
        Assert.False(written.IsWithheld("mutations/texts/charge.after"));

        Assert.Equal(["charge.after"], tree.List("mutations/texts")!.Files);
        Assert.Equal(2, tree.List("mutations/texts")!.Withheld);
        Assert.Equal(["charge.after", "charge.diag"], written.List("mutations/texts")!.Files!.Order(StringComparer.Ordinal));
        Assert.Equal(1, written.List("mutations/texts")!.Withheld);

        Assert.Equal(
            [
                "line 1: text 'mutations/texts/charge.before' is withheld from every copy of the tree by a sync - git ignores it, or "
                + "sync.neverTransfer, sync.exclude or worktrees.root covers it - so no worker would hold it",
            ],
            (await MutationRegistryValidation.ValidateAsync(Text(Held), written, config.Mutations, config, token)).Problems);
    }

    /// <summary>
    /// Nothing of a written path is read until the validation asks for that path: of a hundred paths a command writes,
    /// the two a candidate's rows need read are read, once each, and none as the view is made.
    /// </summary>
    [Fact]
    public async Task AWrittenPath_IsReadOnlyWhenTheValidationAsksForIt()
    {
        using var temp = new TempDirectory();
        var config = Config();
        var tree = await TreeAsync(temp, config);
        var read = new List<string>();
        var writes = new Dictionary<string, Func<CancellationToken, Task<byte[]>>?>();

        Func<CancellationToken, Task<byte[]>> Counted(string path, string holds) => _ =>
        {
            read.Add(path);
            return Task.FromResult(Encoding.UTF8.GetBytes(holds));
        };

        for (var index = 0; index < 98; index++)
        {
            writes[$"src/generated/file-{index}.cpp"] = Counted($"src/generated/file-{index}.cpp", "int x;");
        }

        writes["mutations/texts/floor.before"] = Counted("mutations/texts/floor.before", "c > 0");
        writes["mutations/texts/floor.diag"] = Counted("mutations/texts/floor.diag", "floor exceeded");

        var written = new OverlaidRegistryFiles(tree, writes);

        Assert.Equal(100, writes.Count);
        Assert.Empty(read);

        var validation = await MutationRegistryValidation.ValidateAsync(Text(Candidate), written, config.Mutations, config, TestContext.Current.CancellationToken);

        Assert.Empty(validation.Problems);
        Assert.Equal(["mutations/texts/floor.before", "mutations/texts/floor.diag"], read);
    }

    /// <summary>
    /// A tree's view asks what a sync withholds once, as it is made, however much is asked of it afterwards; and it is
    /// made one way - nothing else constructs one - so a sweep and a fold ask the same question of the tree the registry
    /// is read in or written into.
    /// </summary>
    [Fact]
    public async Task ATreesView_AsksWhatASyncWithholdsOnce_AndTheSweepAndTheFoldMakeItOneWay()
    {
        using var temp = new TempDirectory();
        var config = Config();
        var sync = Withholding("mutations/texts/charge.diag");
        var context = new HarnessContext(new HarnessLayout(temp.Path, temp.Path), config);
        var token = TestContext.Current.CancellationToken;

        WriteHeld(temp);

        var tree = await TreeRegistryFiles.OfAsync(sync, new HarnessFactory().FileSystem, context, token);

        Assert.True(tree.IsWithheld("mutations/texts/charge.diag"));
        Assert.False(tree.IsWithheld("mutations/texts/charge.before"));
        Assert.Equal(1, tree.List("mutations/texts")!.Withheld);
        Assert.Single((await MutationRegistryValidation.ValidateAsync(Text(Held), tree, config.Mutations, config, token)).Problems);
        Assert.Equal(3, (await MutationRegistryValidation.ValidateAsync(Text(Candidate), tree, config.Mutations, config, token)).Problems.Count);

        await sync.Received(1).ExclusionsAsync(context, Arg.Any<CancellationToken>());
        Assert.All(
            typeof(TreeRegistryFiles).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            constructor => Assert.True(constructor.IsPrivate, $"{constructor} is not private"));
    }

    /// <summary>
    /// A file of the tree is read as every file a command reads is, with the token the command was given: asked with one
    /// that is cancelled, the reading stops.
    /// </summary>
    [Fact]
    public async Task ATreesFile_IsReadWithTheTokenItIsAskedWith()
    {
        using var temp = new TempDirectory();
        var tree = await TreeAsync(temp, Config());

        Assert.Equal("c <= b"u8.ToArray(), await tree.ReadAsync("mutations/texts/charge.before", TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tree.ReadAsync("mutations/texts/charge.before", new CancellationToken(canceled: true)));
    }

    /// <summary>
    /// A written path is read once for every caller that asks while it is read or after - each waiting with its own token -
    /// and a read that failed fails each of them, however it failed; but a read the caller that began it stopped is
    /// forgotten, and the next caller reads it again with its own token.
    /// </summary>
    [Fact]
    public async Task AWrittenPath_IsReadOnceForEveryCaller_UnlessTheCallerThatBeganItStoppedIt()
    {
        using var temp = new TempDirectory();
        var tree = await TreeAsync(temp, Config());
        var token = TestContext.Current.CancellationToken;
        var begun = 0;
        var thrown = 0;
        var answer = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var written = new OverlaidRegistryFiles(tree, new Dictionary<string, Func<CancellationToken, Task<byte[]>>?>
        {
            ["mutations/texts/slow.diag"] = async stop =>
            {
                Interlocked.Increment(ref begun);
                return await answer.Task.WaitAsync(stop);
            },
            ["mutations/texts/broken.diag"] = _ =>
            {
                Interlocked.Increment(ref thrown);
                throw new IOException("the disk is gone");
            },
        });

        using (var stopping = new CancellationTokenSource())
        {
            var stopped = written.ReadAsync("mutations/texts/slow.diag", stopping.Token);

            await stopping.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopped);
        }

        var first = written.ReadAsync("mutations/texts/slow.diag", token);
        var second = written.ReadAsync("mutations/texts/slow.diag", token);

        answer.SetResult("charge exceeded"u8.ToArray());

        Assert.Equal(["charge exceeded"u8.ToArray(), "charge exceeded"u8.ToArray()], await Task.WhenAll(first, second));
        Assert.Equal(2, begun);

        await Assert.ThrowsAsync<IOException>(() => written.ReadAsync("mutations/texts/broken.diag", token));
        await Assert.ThrowsAsync<IOException>(() => written.ReadAsync("mutations/texts/broken.diag", token));
        Assert.Equal(1, thrown);
    }

    /// <summary>
    /// A written path is read once however often the validation asks for it - a before-text is asked for to tell whether
    /// it holds nothing, and again where its file holds a further text of its arm - since reading one may cost a process.
    /// </summary>
    [Fact]
    public async Task AWrittenPath_IsReadOnce_HoweverOftenTheValidationAsksForIt()
    {
        using var temp = new TempDirectory();
        var config = Config();
        var tree = await TreeAsync(temp, config);
        var read = new List<string>();

        Func<CancellationToken, Task<byte[]>> Counted(string path, string holds) => _ =>
        {
            read.Add(path);
            return Task.FromResult(Encoding.UTF8.GetBytes(holds));
        };

        temp.WriteFile("src/fixture.cpp", "if (c <= b) { charge(c); } else { spare(c); }");

        var written = new OverlaidRegistryFiles(tree, new Dictionary<string, Func<CancellationToken, Task<byte[]>>?>
        {
            ["mutations/texts/spare.before"] = Counted("mutations/texts/spare.before", "spare(c)"),
            ["mutations/texts/spare.after"] = Counted("mutations/texts/spare.after", "spare(b)"),
        });
        var validation = await MutationRegistryValidation.ValidateAsync(
            Text([.. Held, "M | charge | src/fixture.cpp | mutations/texts/spare.before | mutations/texts/spare.after | a second place"]),
            written,
            config.Mutations,
            config,
            TestContext.Current.CancellationToken);

        Assert.Empty(validation.Problems);
        Assert.Equal(["mutations/texts/spare.before", "mutations/texts/spare.after"], read);
    }

    /// <summary>
    /// The texts are read only of a registry whose rows read, and every text of a row is read whatever another of its
    /// texts is: a row with both texts missing has both said. A scope naming no leg is said after the texts.
    /// </summary>
    [Fact]
    public async Task TheTexts_AreReadOfRowsThatRead_EachOfThem_AndAScopeIsSaidAfterThem()
    {
        using var temp = new TempDirectory();
        var config = Config();
        var tree = await TreeAsync(temp, config);
        var token = TestContext.Current.CancellationToken;
        var diagGone = new OverlaidRegistryFiles(tree, new Dictionary<string, Func<CancellationToken, Task<byte[]>>?> { ["mutations/texts/charge.diag"] = null });
        var bothGone = new OverlaidRegistryFiles(tree, new Dictionary<string, Func<CancellationToken, Task<byte[]>>?>
        {
            ["mutations/texts/charge.before"] = null,
            ["mutations/texts/charge.after"] = null,
        });

        Assert.Equal(
            ["line 3: 'Z' is no row this registry reads, which are A, C, G, B, M and S"],
            (await MutationRegistryValidation.ValidateAsync(Text([.. Held, "Z | charge | x"]), diagGone, config.Mutations, config, token)).Problems);
        Assert.Equal(
            [
                "line 1: text 'mutations/texts/charge.diag' is not a file in the tree",
                "line 3: the S row of arm 'charge' names 'nowhere', which is neither a leg nor a leg set; declared legs: native",
            ],
            (await MutationRegistryValidation.ValidateAsync(Text([.. Held, "S | charge | nowhere | where it is built"]), diagGone, config.Mutations, config, token)).Problems);
        Assert.Equal(
            [
                "line 1: text 'mutations/texts/charge.before' is not a file in the tree",
                "line 1: text 'mutations/texts/charge.after' is not a file in the tree",
            ],
            (await MutationRegistryValidation.ValidateAsync(Text(Held), bothGone, config.Mutations, config, token)).Problems);
    }

    /// <summary>
    /// A file the command writes into the text directory where a sync would withhold it is listed - it is what the command
    /// puts there - and leaves the count of withheld files as the tree has it: it was not there to be withheld.
    /// </summary>
    [Fact]
    public async Task AFileWrittenWhereASyncWouldWithholdIt_IsListed_AndLeavesTheWithheldAsTheTreeHasThem()
    {
        using var temp = new TempDirectory();
        var config = Config();

        config.Sync.Exclude.AddRange(["mutations/texts/old.tmp", "mutations/texts/new.tmp"]);
        temp.WriteFile("mutations/texts/old.tmp", "an editor's leftover");

        var tree = await TreeAsync(temp, config);
        var written = new OverlaidRegistryFiles(tree, new Dictionary<string, Func<CancellationToken, Task<byte[]>>?> { ["mutations/texts/new.tmp"] = Holding("another") });

        Assert.Equal(1, tree.List("mutations/texts")!.Withheld);
        Assert.Equal(["charge.after", "charge.before", "charge.diag", "new.tmp"], written.List("mutations/texts")!.Files!.Order(StringComparer.Ordinal));
        Assert.Equal(1, written.List("mutations/texts")!.Withheld);
    }

    /// <summary>
    /// A refusal names the registry and lists every problem, each to fix, as check-mutations words it; a candidate's says
    /// besides whose text it is, in the caller's words; and a registry with no problem has no refusal.
    /// </summary>
    [Fact]
    public async Task ACandidatesRefusal_SaysItIsTheTextTheFoldWouldWrite()
    {
        using var temp = new TempDirectory();
        var config = Config();
        var tree = await TreeAsync(temp, config);
        var token = TestContext.Current.CancellationToken;

        var refused = await MutationRegistryValidation.ValidateAsync(Text(Candidate), tree, config.Mutations, config, token);
        var sound = await MutationRegistryValidation.ValidateAsync(Text(Held), tree, config.Mutations, config, token);

        Assert.False(refused.Valid);
        Assert.Equal(
            [
                "The arms registry 'mutations/arms.txt' cannot be swept: 2 problem(s), each to fix:",
                "  - line 3: text 'mutations/texts/floor.before' is not a file in the tree",
                "  - line 3: text 'mutations/texts/floor.diag' is not a file in the tree",
            ],
            refused.Refusal(Registry));
        Assert.Equal(
            [
                "The arms registry 'mutations/arms.txt', as the fold would write it, cannot be swept: 2 problem(s), each to fix:",
                "  - line 3: text 'mutations/texts/floor.before' is not a file in the tree",
                "  - line 3: text 'mutations/texts/floor.diag' is not a file in the tree",
            ],
            refused.Refusal(Registry, "as the fold would write it"));

        Assert.True(sound.Valid);
        Assert.Empty(sound.Refusal(Registry));
        Assert.Empty(sound.Refusal(Registry, "as the fold would write it"));
    }

    /// <summary>
    /// A validation gives the registry it read, each scoped arm's legs as the configuration resolves them, and every
    /// problem in one list: a scope naming no leg stands beside a row the registry does not read.
    /// </summary>
    [Fact]
    public async Task AValidation_GivesTheRegistryAndItsScopes_AndListsAScopeNamingNoLegWithTheOtherProblems()
    {
        using var temp = new TempDirectory();
        var config = Config();
        var tree = await TreeAsync(temp, config);
        var token = TestContext.Current.CancellationToken;

        var scoped = await MutationRegistryValidation.ValidateAsync(Text([.. Held, "S | charge | native | where it is built"]), tree, config.Mutations, config, token);

        Assert.Empty(scoped.Problems);
        Assert.Equal(["charge"], scoped.Registry.Arms.Select(arm => arm.Id));
        Assert.Equal(["native"], Assert.Single(scoped.Scopes, scope => scope.Key == "charge").Value);

        var wrong = await MutationRegistryValidation.ValidateAsync(Text([.. Held, "S | charge | nowhere | where it is built", "Z | charge | x"]), tree, config.Mutations, config, token);

        Assert.Equal(
            [
                "line 4: 'Z' is no row this registry reads, which are A, C, G, B, M and S",
                "line 3: the S row of arm 'charge' names 'nowhere', which is neither a leg nor a leg set; declared legs: native",
            ],
            wrong.Problems);
        Assert.Empty(wrong.Scopes);
    }

    /// <summary>
    /// A path a command removes is not there, cannot be read and is no longer listed; one it writes is there and read
    /// as written; and every other path is the tree's.
    /// </summary>
    [Fact]
    public async Task ARemovedPath_IsNotThere_AWrittenOneIs_AndAnyOtherIsTheTrees()
    {
        using var temp = new TempDirectory();
        var tree = await TreeAsync(temp, Config());
        var token = TestContext.Current.CancellationToken;
        var view = new OverlaidRegistryFiles(tree, new Dictionary<string, Func<CancellationToken, Task<byte[]>>?>
        {
            ["mutations/texts/charge.diag"] = null,
            ["mutations/texts/floor.diag"] = Holding("floor exceeded"),
            ["src/new.cpp"] = Holding("int y;"),
        });

        Assert.False(view.Exists("mutations/texts/charge.diag"));
        Assert.True(view.Exists("mutations/texts/floor.diag"));
        Assert.True(view.Exists("src/new.cpp"));
        Assert.True(view.Exists("mutations/texts/charge.before"));
        Assert.False(view.Exists("mutations/texts/nowhere.before"));

        Assert.Equal("floor exceeded", Encoding.UTF8.GetString(await view.ReadAsync("mutations/texts/floor.diag", token)));
        Assert.Equal("c <= b", Encoding.UTF8.GetString(await view.ReadAsync("mutations/texts/charge.before", token)));
        await Assert.ThrowsAsync<FileNotFoundException>(() => view.ReadAsync("mutations/texts/charge.diag", token));

        Assert.Equal(["charge.after", "charge.before", "floor.diag"], view.List("mutations/texts")!.Files!.Order(StringComparer.Ordinal));
        Assert.Null(view.List(null));
        Assert.Same(tree.Names, view.Names);
    }

    /// <summary>
    /// A written or removed path is found as the tree's own file system compares names: under one that folds case, a
    /// row spelling it in another case reads what the command writes and the listing holds the file once; under one
    /// that does not, the row names another file.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AWrittenPath_IsFoundAsTheTreesFileSystemComparesNames(bool folds)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = Config();
        var token = TestContext.Current.CancellationToken;

        WriteHeld(temp);

        var tree = await TreeRegistryFiles.OfAsync(
            Withholding(),
            new CaseOf(harness.FileSystem, temp.Path, folds),
            new HarnessContext(new HarnessLayout(temp.Path, temp.Path), config),
            token);
        var view = new OverlaidRegistryFiles(tree, new Dictionary<string, Func<CancellationToken, Task<byte[]>>?>
        {
            ["mutations/texts/Floor.Diag"] = Holding("as the command spells it"),
            ["mutations/texts/CHARGE.after"] = null,
        });

        Assert.Equal(folds ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal, view.Names);

        if (!folds)
        {
            // Neither is the command's: the first is no file of the tree either, and the second is the tree's own.
            Assert.False(view.Exists("mutations/texts/floor.diag"));
            Assert.True(view.Exists("mutations/texts/charge.after"));
            Assert.Equal(["Floor.Diag", "charge.after", "charge.before", "charge.diag"], view.List("mutations/texts")!.Files!.Order(StringComparer.Ordinal));
            return;
        }

        Assert.True(view.Exists("mutations/texts/floor.diag"));
        Assert.Equal("as the command spells it", Encoding.UTF8.GetString(await view.ReadAsync("mutations/texts/floor.diag", token)));
        Assert.False(view.Exists("mutations/texts/charge.after"));
        Assert.Equal(["Floor.Diag", "charge.before", "charge.diag"], view.List("mutations/texts")!.Files!.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A text directory the tree does not hold is one once a command writes a file directly into it - and only directly:
    /// what lies below it is not listed, as in a tree. One that is there and could not be listed stays so, whatever is
    /// written, since what else it holds cannot be told. And a file a sync withholds that the command removes is
    /// withheld no longer: it is not there to be.
    /// </summary>
    [Fact]
    public async Task ATextDirectory_IsListedAsTheCommandLeavesIt()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = Config();
        var context = new HarnessContext(new HarnessLayout(temp.Path, temp.Path), config);
        var token = TestContext.Current.CancellationToken;

        WriteHeld(temp);

        var tree = await TreeRegistryFiles.OfAsync(Withholding("mutations/texts/charge.diag"), harness.FileSystem, context, token);

        Assert.Null(tree.List("mutations/later")!.Files);
        Assert.Null(new OverlaidRegistryFiles(tree, new Dictionary<string, Func<CancellationToken, Task<byte[]>>?> { ["mutations/later/deep/a.before"] = Holding("a") }).List("mutations/later")!.Files);
        Assert.Null(new OverlaidRegistryFiles(tree, new Dictionary<string, Func<CancellationToken, Task<byte[]>>?> { ["mutations/later/a.before"] = null }).List("mutations/later")!.Files);

        var made = new OverlaidRegistryFiles(tree, new Dictionary<string, Func<CancellationToken, Task<byte[]>>?>
        {
            ["mutations/later/a.before"] = Holding("a"),
            ["mutations/later/deep/b.before"] = Holding("b"),
            ["mutations/laterstill.before"] = Holding("c"),
        }).List("mutations/later/")!;

        Assert.Equal("mutations/later", made.Directory);
        Assert.Equal(["a.before"], made.Files);
        Assert.Equal((0, null), (made.Withheld, made.Unlisted));

        Assert.Equal(1, tree.List("mutations/texts")!.Withheld);

        var emptied = new OverlaidRegistryFiles(tree, new Dictionary<string, Func<CancellationToken, Task<byte[]>>?>
        {
            ["mutations/texts/charge.diag"] = null,
            ["mutations/texts/charge.before"] = null,
            ["mutations/texts/charge.after"] = null,
        }).List("mutations/texts")!;

        Assert.Empty(emptied.Files!);
        Assert.Equal(0, emptied.Withheld);

        var unlisted = await TreeRegistryFiles.OfAsync(Withholding(), new CannotListFiles(harness.FileSystem, temp.Combine("mutations", "texts")), context, token);
        var over = new OverlaidRegistryFiles(unlisted, new Dictionary<string, Func<CancellationToken, Task<byte[]>>?> { ["mutations/texts/floor.diag"] = Holding("f") }).List("mutations/texts")!;

        Assert.Null(over.Files);
        Assert.Equal("the disk would not say.", over.Unlisted);
    }

    /// <summary>
    /// A configuration a registry is validated by: one leg, and the registry and its texts where the tests write them.
    /// </summary>
    private static HarnessConfig Config() => new()
    {
        BuildConfigs = { ["debug"] = new BuildConfiguration() },
        Toolchains = { ["gcc"] = new ToolchainConfig { Generator = "Ninja" } },
        Projects = [new ProjectConfig { Name = "app", Type = "cmake" }],
        Legs = { ["native"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", Toolchain = "gcc", Project = "app" } },
        Mutations = new MutationSettings { Registry = Registry, TextDirectory = "mutations/texts", ReportArgs = ["--gtest_output=xml:{report}"] },
    };

    /// <summary>
    /// The view of the tree in <paramref name="temp"/> as it stands, holding the registry's one arm and the three texts
    /// it cites, a sync withholding what <paramref name="sync"/> says - nothing, where it is left out.
    /// </summary>
    private static async Task<TreeRegistryFiles> TreeAsync(TempDirectory temp, HarnessConfig config, ISyncService? sync = null)
    {
        WriteHeld(temp);

        return await TreeRegistryFiles.OfAsync(
            sync ?? Withholding(),
            new HarnessFactory().FileSystem,
            new HarnessContext(new HarnessLayout(temp.Path, temp.Path), config),
            TestContext.Current.CancellationToken);
    }

    private static void WriteHeld(TempDirectory temp)
    {
        temp.WriteFile(Registry, Text(Held));
        temp.WriteFile("mutations/texts/charge.before", "c <= b");
        temp.WriteFile("mutations/texts/charge.after", "c < b");
        temp.WriteFile("mutations/texts/charge.diag", "charge exceeded");
    }

    /// <summary>A sync service that says git ignores <paramref name="ignored"/> in any tree, and withholds what its configuration does.</summary>
    private static ISyncService Withholding(params string[] ignored)
    {
        var sync = Substitute.For<ISyncService>();

        sync.ExclusionsAsync(default!, TestContext.Current.CancellationToken).ReturnsForAnyArgs(call =>
        {
            var config = call.Arg<HarnessContext>().Config;

            return Task.FromResult(new SyncExclusions(config.Sync, config.Worktrees.Root, ignored));
        });

        return sync;
    }

    private static string Text(string[] rows) => string.Join("\n", rows) + "\n";

    private static Func<CancellationToken, Task<byte[]>> Holding(string text) => _ => Task.FromResult(Encoding.UTF8.GetBytes(text));

    /// <summary>The real file system, save that the files of <paramref name="directory"/> cannot be listed.</summary>
    private sealed class CannotListFiles(IFileSystem inner, string directory) : PassThroughFileSystem(inner)
    {
        public override IEnumerable<string> EnumerateFiles(string path, bool recursive)
            => path == directory ? throw new IOException("the disk would not say.") : base.EnumerateFiles(path, recursive);
    }
}
