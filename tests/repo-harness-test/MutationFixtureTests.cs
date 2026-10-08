using System.Text;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Mutations;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// The fixture a self-test sweeps, as this assembly carries it: whole, its registry sweepable and every arm in it designed,
/// written where it is kept only where it differs, read as a tree a sync reads, and each arm held to its design.
/// </summary>
public sealed class MutationFixtureTests
{
    /// <summary>
    /// Every file of the fixture is embedded, under its path in the fixture spelt with '/', as the UTF-8 text without a
    /// byte order mark its every write puts back byte for byte.
    /// </summary>
    [Fact]
    public void EveryFileOfTheFixture_IsEmbedded_UnderItsPathInIt()
    {
        var files = MutationFixture.Files();

        // Each file of tests/mutation-fixture, and nothing else this assembly carries.
        Assert.Equal(
            [
                "CMakeLists.txt",
                "arms.txt",
                "src/budget.hpp",
                "src/fixture.cpp",
                "src/fixture.hpp",
                "tests/fixture_tests.cpp",
                "texts/charge.after",
                "texts/charge.before",
                "texts/charge.diag",
                "texts/coupled-use.after",
                "texts/coupled-use.before",
                "texts/coupled.after",
                "texts/coupled.diag",
                "texts/depth.after",
                "texts/depth.before",
                "texts/depth.control-after",
                "texts/depth.control-before",
                "texts/floor.after",
                "texts/floor.before",
                "texts/floor.diag",
                "texts/sanity.after",
                "texts/sanity.before",
                "texts/spare.after",
                "texts/spare.before",
                "texts/unlinked.after",
                "texts/unlinked.before",
            ],
            files.Keys);
        Assert.All(files, pair =>
        {
            Assert.NotEmpty(pair.Value);
            Assert.False(pair.Value.AsSpan().StartsWith(Encoding.UTF8.Preamble), $"'{pair.Key}' starts with a byte order mark");
            Assert.Equal(pair.Value, new UTF8Encoding(false).GetBytes(new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(pair.Value)));
            Assert.DoesNotContain((byte)'\r', pair.Value);
        });
    }

    /// <summary>
    /// Its registry reads whole, every text in its text directory cited; it designs a verdict for every arm it declares and
    /// for no other; and each text an arm replaces occurs exactly once where it is replaced, and is replaced by another -
    /// the arm's own, and a paired control's in the site as it was - so each arm's verdict is its design's, never its
    /// pre-flight's.
    /// </summary>
    [Fact]
    public void ItsRegistry_ReadsWhole_AndEveryArmIsDesigned_AndEachTextOccursOnce()
    {
        var files = MutationFixture.Files();
        var registry = MutationFixture.Registry();

        Assert.Equal(MutationFixture.Designed.Keys.Order(StringComparer.Ordinal), registry.Arms.Select(arm => arm.Id).Order(StringComparer.Ordinal));
        Assert.All(registry.Arms, arm => Assert.Null(arm.Scope));

        foreach (var arm in registry.Arms)
        {
            foreach (var site in arm.Sites)
            {
                var edit = SiteEdit.Apply(files[site.Site], SiteEdit.Text(files[site.Before]), SiteEdit.Text(files[site.After]));

                Assert.Equal(1, edit.Occurrences);
                Assert.False(edit.ChangesNothing, $"arm '{arm.Id}' replaces a text of '{site.Site}' with itself");
            }

            if (arm.Control is { } control)
            {
                var edit = SiteEdit.Apply(files[arm.Own.Site], SiteEdit.Text(files[control.Before]), SiteEdit.Text(files[control.After]));

                Assert.Equal(1, edit.Occurrences);
                Assert.False(edit.ChangesNothing, $"the control of arm '{arm.Id}' replaces a text of '{arm.Own.Site}' with itself");
            }
        }

        // Swept with its own registry, texts and report argument, and with the workers and the bound the repository gives.
        var settings = MutationFixture.SettingsFor(new MutationSettings { Workers = 3, RunTimeFactor = 4 });

        Assert.Equal(("arms.txt", "texts", 3, 4.0), (settings.Registry, settings.TextDirectory, settings.Workers, settings.RunTimeFactor));
        Assert.Equal(["--report={report}"], settings.ReportArgs);
    }

    /// <summary>
    /// Written where it is kept, the fixture is there whole; written again, nothing of it is rewritten; a file of it that
    /// changed is written back, and a file that is no part of it removed.
    /// </summary>
    [Fact]
    public void TheFixture_IsWrittenWhereItIsKept_OnlyWhereItDiffers()
    {
        using var temp = new TempDirectory();
        var directory = temp.Combine("fixture");
        var store = new MutationFixtureStore(new HarnessFactory().FileSystem, directory);
        var files = MutationFixture.Files();

        Assert.Equal(directory, store.Directory);

        store.Write();

        foreach (var (path, bytes) in files)
        {
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(directory, path)));
        }

        var registry = Path.Combine(directory, "arms.txt");
        var site = Path.Combine(directory, "src", "fixture.cpp");
        var longAgo = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        File.SetLastWriteTimeUtc(registry, longAgo);
        File.WriteAllText(site, "// changed\n");
        File.WriteAllText(Path.Combine(directory, "texts", "stale.before"), "an earlier build's\n");

        store.Write();

        Assert.Equal(longAgo, File.GetLastWriteTimeUtc(registry));
        Assert.Equal(files["src/fixture.cpp"], File.ReadAllBytes(site));
        Assert.False(File.Exists(Path.Combine(directory, "texts", "stale.before")));
        Assert.Equal(files.Count, Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Count());
    }

    /// <summary>
    /// The fixture is kept among this user's own data for this tool, named only once a self-test needs it; a process that
    /// can name no directory of its user's own keeps it nowhere else, and refuses the self-test, saying why, writing nothing.
    /// </summary>
    [Fact]
    public void TheFixture_IsKeptAmongThisUsersOwnData_OrNowhere()
    {
        Assert.EndsWith(Path.Combine(ToolPackage.Command, MutationFixture.DirectoryName), MutationFixtureStore.DefaultDirectory(), StringComparison.Ordinal);

        var asked = 0;
        var nowhere = new MutationFixtureStore(
            new HarnessFactory().FileSystem,
            () =>
            {
                asked++;
                throw new DirectoryNotFoundException("the process was given no home");
            });

        Assert.Equal(0, asked);

        var refusal = Assert.Throws<HarnessException>(() => nowhere.Directory);

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Equal("The self-test's fixture has nowhere to be kept: the process was given no home. Nothing was swept.", refusal.Message);
        Assert.Equal(refusal.Message, Assert.Throws<HarnessException>(nowhere.Write).Message);
        Assert.Equal(1, asked);
    }

    /// <summary>
    /// A fixture that cannot be written where it is kept refuses the self-test, naming where and why: no self-test runs on
    /// the machine until it can be.
    /// </summary>
    [Fact]
    public void AFixtureThatCannotBeWritten_RefusesTheSelfTest_NamingWhere()
    {
        using var temp = new TempDirectory();

        // A file where its directory would be, which nothing can write a file below.
        var directory = temp.WriteFile("fixture", "not a directory\n");

        var refusal = Assert.Throws<HarnessException>(new MutationFixtureStore(new HarnessFactory().FileSystem, directory).Write);

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.StartsWith($"The self-test's fixture '{directory}' could not be written: ", refusal.Message, StringComparison.Ordinal);
        Assert.EndsWith("Until it can be, no self-test runs on this machine.", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The fixture is a CMake project built from its root, whose whole build is witnessed by the test binary it links -
    /// named as each system names a program - and each time it is asked for it is a project of its own.
    /// </summary>
    [Fact]
    public void TheFixturesProject_IsWitnessedByItsTestBinary_AndIsNeverShared()
    {
        var project = MutationFixture.Project;

        Assert.Equal(("mutation-fixture", "cmake", "."), (project.Name, project.Type, project.Path));
        Assert.Equal(
            [("windows", "fixture_tests.exe"), ("linux", "fixture_tests"), ("macos", "fixture_tests")],
            new[] { "windows", "linux", "macos" }.Select(os => (os, Assert.Single(project.BuildOutputs).For(os))));
        Assert.NotSame(project, MutationFixture.Project);
    }

    /// <summary>Read as a sync reads a tree, the fixture is each of its files by its size and hash, rooted where it is kept.</summary>
    [Fact]
    public void TheFixture_IsReadAsATree_EachFileByItsSizeAndHash()
    {
        var reading = MutationFixture.Reading("/data/mutation-fixture");
        var files = MutationFixture.Files();

        Assert.Equal("/data/mutation-fixture", reading.Files.Root);
        Assert.Equal(files.Keys, reading.Files.Entries.Keys.Order(StringComparer.Ordinal));
        Assert.All(files, pair => Assert.Equal((pair.Value.LongLength, FileContentHash.Of(pair.Value)), (reading.Files.Entries[pair.Key].Size, reading.Files.Entries[pair.Key].ContentHash)));
        Assert.Equal("/data/mutation-fixture", reading.Context.Layout.RepositoryRoot);
    }

    /// <summary>
    /// An arm that reached the verdict it is designed to reach passed, saying so; one that reached another verdict an arm's
    /// design decides is violated, naming both; and one that reached no verdict of the judge's keeps it, which says nothing
    /// of how this tool judged it.
    /// </summary>
    [Theory]
    [InlineData(LegVerdict.Survived, LegVerdict.Survived, LegVerdict.Passed, "survived, as designed: ran 3 case(s), and none failed")]
    [InlineData(LegVerdict.Failed, LegVerdict.Failed, LegVerdict.Passed, "failed, as designed: ran 3 case(s), and none failed")]
    [InlineData(LegVerdict.Passed, LegVerdict.Survived, LegVerdict.Violated, "designed to reach passed, and reached survived: ran 3 case(s), and none failed")]
    [InlineData(LegVerdict.Violated, LegVerdict.Unwitnessed, LegVerdict.Violated, "designed to reach violated, and reached unwitnessed: ran 3 case(s), and none failed")]
    [InlineData(LegVerdict.Survived, LegVerdict.Unattributed, LegVerdict.Violated, "designed to reach survived, and reached unattributed: ran 3 case(s), and none failed")]
    [InlineData(LegVerdict.Passed, LegVerdict.Stopped, LegVerdict.Stopped, "ran 3 case(s), and none failed")]
    [InlineData(LegVerdict.Passed, LegVerdict.Poisoned, LegVerdict.Poisoned, "ran 3 case(s), and none failed")]
    [InlineData(LegVerdict.Passed, LegVerdict.NotAdmitted, LegVerdict.NotAdmitted, "ran 3 case(s), and none failed")]
    public void AnArm_IsHeldToItsDesign(LegVerdict designed, LegVerdict reached, LegVerdict verdict, string detail)
        => Assert.Equal(
            ReachedVerdict.Of(verdict, detail),
            MutationFixture.Judge(designed, ReachedVerdict.Of(reached, "ran 3 case(s), and none failed")));

    /// <summary>
    /// Each arm of the fixture is held to the verdict the fixture designs for it, by its id; an arm it designs nothing for
    /// is violated, saying what it reached.
    /// </summary>
    [Fact]
    public void EachArm_IsHeldToTheVerdictTheFixtureDesignsForIt()
    {
        var survived = ReachedVerdict.Of(LegVerdict.Survived, "ran 3 case(s), and none failed");

        Assert.Equal(ReachedVerdict.Of(LegVerdict.Passed, "survived, as designed: ran 3 case(s), and none failed"), MutationFixture.Hold("spare-unseen", survived));
        Assert.Equal(
            ReachedVerdict.Of(LegVerdict.Violated, "designed to reach unattributed, and reached survived: ran 3 case(s), and none failed"),
            MutationFixture.Hold("sanity-lost", survived));
        Assert.Equal(
            ReachedVerdict.Of(LegVerdict.Violated, "the fixture designs no verdict for this arm, which reached survived: ran 3 case(s), and none failed"),
            MutationFixture.Hold("nobody-designed-this", survived));
    }
}
