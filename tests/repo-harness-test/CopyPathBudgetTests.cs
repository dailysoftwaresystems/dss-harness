using NSubstitute;
using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Platform;

namespace RepoHarness.Tests;

/// <summary>
/// Whether the build in a copy of a tree kept beside it fits this machine's path limit is reckoned in one place, as a
/// worktree's is: the copy, its variant's build directory below it, the longest path a build makes below that, and
/// the margin, under the limit declared.
/// </summary>
public sealed class CopyPathBudgetTests
{
    private const int Reserve = 40;

    private static readonly VariantKey Variant = new("x86_64", "gcc", "debug", null);

    private static readonly string Copy = Path.Combine(Path.GetTempPath(), "repo.mutation-357e24cw-1");

    /// <summary>What a build makes below its copy: a separator, the build root, a separator, the variant's directory, and the reserve.</summary>
    private static readonly int Below = 1 + VariantKey.BuildRootName.Length + 1 + Variant.DirectoryName.Length + Reserve;

    /// <summary>
    /// The longest path a copy's build makes below it is its variant's build directory, the separator before it
    /// counted, and the reserve below that - however the copy's own path ends.
    /// </summary>
    [Fact]
    public void TheLongestPathBelowACopy_IsItsBuildDirectoryAndTheReserveBelowIt()
    {
        Assert.Equal(Below, CopyPathBudget.BuildBelow(Copy, Variant, Reserve));
        Assert.Equal(Below, CopyPathBudget.BuildBelow(Copy + Path.DirectorySeparatorChar, Variant, Reserve));
        Assert.Equal(Below - Reserve, CopyPathBudget.BuildBelow(Copy, Variant, 0));
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

        Assert.Same(answer, CopyPathBudget.Check(budget, new WorktreeSettings { PathBudgetMargin = 9, PathLimit = limit }, Copy, Variant, Reserve));
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

        CopyPathBudget.Check(budget, new WorktreeSettings { PathBudgetMargin = 9, PathLimit = 200 }, Copy, Variant, Reserve, given: Below + givenPastTheBuild);

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

        var result = CopyPathBudget.Check(new PathBudget(new HostPlatform()), settings, Copy, Variant, Reserve);

        Assert.Equal((fits, longest), (result.IsWithinBudget, result.RequiredLength));
    }
}
