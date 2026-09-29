using RepoHarness.Core.Worktrees;

namespace RepoHarness.Tests;

/// <summary>How a worktree is named, and found, whether plain or an orchestrator's agent's.</summary>
public sealed class WorktreeAddressTests
{
    /// <summary>A plain name reads as itself; an orchestrator's and its agent's read as the two joined by a slash.</summary>
    [Theory]
    [InlineData("fix-auth", new[] { "fix-auth" }, "fix-auth", false)]
    [InlineData("o1/api", new[] { "o1", "api" }, "o1--api", true)]
    [InlineData("cycle-3/io-2", new[] { "cycle-3", "io-2" }, "cycle-3--io-2", true)]
    public void TryParse_ReadsAPlainNameAndAnAgentsAddress(string text, string[] segments, string copyName, bool nested)
    {
        Assert.True(WorktreeAddress.TryParse(text, out var address, out var error), error);
        Assert.Equal(segments, address!.Segments);
        Assert.Equal(text, address.Name);
        Assert.Equal(copyName, address.CopyName);
        Assert.Equal(nested, address.IsNested);
        Assert.Equal(text, address.ToString());
    }

    /// <summary>What no worktree is called is refused with why: a name's own rule for each part, and at most two parts.</summary>
    [Theory]
    [InlineData(null, "A worktree name is required.")]
    [InlineData("", "A worktree name is required.")]
    [InlineData("Fix", "'Fix' is not a valid name.")]
    [InlineData("o1/api/x", "'o1/api/x' names more than an orchestrator and one of its agents")]
    [InlineData("o1/", "'o1/' is not a valid address: A worktree name is required.")]
    [InlineData("/api", "'/api' is not a valid address: A worktree name is required.")]
    [InlineData("o1/Api", "'o1/Api' is not a valid address: 'Api' is not a valid name.")]
    [InlineData("o1--api", "'o1--api' is not a valid name.")]
    [InlineData("../x", "'../x' is not a valid address: '..' is not a valid name.")]
    public void TryParse_RefusesWhatNoWorktreeIsCalled_SayingWhy(string? text, string starts)
    {
        Assert.False(WorktreeAddress.TryParse(text, out var address, out var error));
        Assert.Null(address);
        Assert.StartsWith(starts, error, StringComparison.Ordinal);
    }

    /// <summary>
    /// An address is the directories it names under the root, and a tree under the root is read back as the address
    /// it sits at; one outside the root, the root itself, or a directory no worktree can be named for, as none.
    /// </summary>
    [Fact]
    public void AnAddress_IsItsDirectoriesUnderTheRoot_AndATreeReadsBackAsOne()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine(".worktrees");
        var agent = WorktreeAddress.Nested("o1", "api");

        Assert.Equal(Path.Combine(root, "o1", "api"), agent.PathUnder(root));
        Assert.Equal(agent, WorktreeAddress.OfTree(root, Path.Combine(root, "o1", "api"), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(WorktreeAddress.Plain("wt"), WorktreeAddress.OfTree(root, Path.Combine(root, "wt") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.Null(WorktreeAddress.OfTree(root, root, StringComparison.OrdinalIgnoreCase));
        Assert.Null(WorktreeAddress.OfTree(root, temp.Combine("elsewhere", "wt"), StringComparison.OrdinalIgnoreCase));
        Assert.Null(WorktreeAddress.OfTree(root, Path.Combine(root, "o1", "api", "deeper"), StringComparison.OrdinalIgnoreCase));
        Assert.Null(WorktreeAddress.OfTree(root, Path.Combine(root, "My Tree"), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A copy's name is read back as the address a person types, and a plain worktree's as itself.</summary>
    [Theory]
    [InlineData("o1--api", "o1/api")]
    [InlineData("fix-auth", "fix-auth")]
    public void OfCopyName_IsTheAddressAPersonTypes(string copyName, string address)
        => Assert.Equal(address, WorktreeAddress.OfCopyName(copyName));

    /// <summary>Two addresses naming one worktree are equal, however each was made.</summary>
    [Fact]
    public void AddressesNamingOneWorktree_AreEqual()
    {
        Assert.True(WorktreeAddress.TryParse("o1/api", out var parsed, out _));
        Assert.Equal(WorktreeAddress.Nested("o1", "api"), parsed);
        Assert.Equal(WorktreeAddress.Nested("o1", "api").GetHashCode(), parsed!.GetHashCode());
        Assert.NotEqual(WorktreeAddress.Nested("o1", "api"), WorktreeAddress.Nested("o2", "api"));
    }
}
