using NSubstitute;
using DssHarness.Core.Build;
using DssHarness.Core.Configuration;
using DssHarness.Core.Legs;
using DssHarness.Core.Mutations;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;

namespace DssHarness.Tests;

/// <summary>
/// Whether the build in a copy of a tree kept in it fits this machine's path limit is reckoned in one place, as a
/// worktree's is: the copy, its build directory below it - its variant's own in a leg's copy, build/&lt;key&gt; in a
/// mutation worker - the longest path a build makes below that, and the margin, under the limit declared.
/// </summary>
public sealed class CopyPathBudgetTests
{
    private const int Reserve = 40;

    private static readonly VariantKey Variant = new("x86_64", "gcc", "debug", null);

    private static readonly string Copy = Path.Combine(Path.GetTempPath(), "repo", HarnessLayout.LegsDirectoryName, Variant.CopyKey);

    /// <summary>Where a leg's copy builds: its variant's own directory, as the tree it copies does.</summary>
    private static readonly string Build = Variant.DirectoryUnder(Copy);

    /// <summary>What a build makes below its copy: a separator, the build root, a separator, the variant's directory, and the reserve.</summary>
    private static readonly int Below = 1 + VariantKey.BuildRootName.Length + 1 + Variant.DirectoryName.Length + Reserve;

    /// <summary>
    /// The longest path a copy's build makes below it is its build directory, the separator before it counted, and the
    /// reserve below that - however the copy's own path ends.
    /// </summary>
    [Fact]
    public void TheLongestPathBelowACopy_IsItsBuildDirectoryAndTheReserveBelowIt()
    {
        Assert.Equal(Below, CopyPathBudget.BuildBelow(Copy, Build, Reserve));
        Assert.Equal(Below, CopyPathBudget.BuildBelow(Copy + Path.DirectorySeparatorChar, Build, Reserve));
        Assert.Equal(Below - Reserve, CopyPathBudget.BuildBelow(Copy, Build, 0));
    }

    /// <summary>
    /// A mutation worker is reckoned by the directory it builds in, build/&lt;key&gt;: the variant's key below it, never
    /// the variant's name - so whatever the variant is called, its build adds the same few characters below the worker.
    /// </summary>
    [Fact]
    public void AWorker_IsReckonedByItsBuildDirectoryNamedByTheKey()
    {
        var longer = Variant with { Toolchain = "gcc-with-a-much-longer-toolchain-name", Sanitizer = "address" };

        foreach (var variant in new[] { Variant, longer })
        {
            var workers = MutationWorkers.Of(Path.Combine(Path.GetTempPath(), "repo"), variant);

            Assert.Equal(
                1 + VariantKey.BuildRootName.Length + 1 + VariantKey.CopyKeyLength + Reserve,
                CopyPathBudget.BuildBelow(workers.PathOf(1), workers.BuildDirectoryOf(1), Reserve));
        }
    }

    /// <summary>
    /// A copy is asked about by what its build makes below it, with the margin and the limit the repository's worktree
    /// settings declare: the limit's own where it declares none.
    /// </summary>
    [Theory]
    [InlineData(200)]
    [InlineData(null)]
    public void ACopy_IsReckonedByItsBuild_WithTheMarginAndTheLimitDeclared(int? limit)
    {
        var budget = Substitute.For<IPathBudget>();
        var answer = PathBudgetResult.WithinBudget(1, 2, 3);

        budget.Check(Copy, Below, 9, limit).Returns(answer);

        Assert.Same(answer, CopyPathBudget.Check(budget, new WorktreeSettings { PathBudgetMargin = 9, PathLimit = limit }, Copy, Build, Reserve));
    }

    /// <summary>
    /// What a copy is given beside its tree - the dependency sources a sweep gives a worker - reckons it where its
    /// longest path is longer than the build's, and changes nothing where it is not.
    /// </summary>
    [Theory]
    [InlineData(5, 5)]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    public void WhatACopyIsGiven_ReckonsIt_OnlyWhereItIsTheLonger(int givenPastTheBuild, int reckonedPastTheBuild)
    {
        var budget = Substitute.For<IPathBudget>();

        CopyPathBudget.Check(budget, new WorktreeSettings { PathBudgetMargin = 9, PathLimit = 200 }, Copy, Build, Reserve, given: Below + givenPastTheBuild);

        budget.Received(1).Check(Copy, Below + reckonedPastTheBuild, 9, 200);
    }

    /// <summary>
    /// Reckoned by the machine's own check, a copy fits while every path of its build stays under the limit, and does
    /// not from the first character at it: the copy, what its build makes below it, the separator before the longest
    /// path below its build directory, which that check counts, and the margin.
    /// </summary>
    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void ACopyFits_WhileItsBuildsLongestPathStaysUnderTheLimit(int underTheLimitBy, bool fits)
    {
        const int Margin = 9;
        var longest = Copy.Length + Below + 1 + Margin;
        var settings = new WorktreeSettings { PathBudgetMargin = Margin, PathLimit = longest + underTheLimitBy };

        var result = CopyPathBudget.Check(new PathBudget(new HostPlatform()), settings, Copy, Build, Reserve);

        Assert.Equal((fits, longest), (result.IsWithinBudget, result.RequiredLength));
    }
}
