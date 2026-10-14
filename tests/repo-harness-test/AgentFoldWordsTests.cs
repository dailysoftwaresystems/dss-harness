using RepoHarness.Core.Git;
using RepoHarness.Core.Orchestration;

namespace RepoHarness.Tests;

/// <summary>
/// The words a fold's refusal says of how the main tree came to hold a path since the agent's base, for every way it can:
/// what the base holds there - nothing, a file or a directory - by what its HEAD holds - what the base holds, nothing, a
/// file or a directory - by what the main tree holds against its HEAD - the same, otherwise, something git ignores,
/// something git lists nothing of and does not ignore, as an empty directory, what lies in a submodule HEAD holds or only
/// the index does, or the same under a spelling that differs in case alone, as a commit's rename of case alone leaves
/// it - each said whole, as a
/// path the main tree made, changed, made a directory of or removed is refused, and short, as a file in the way of the
/// agent's is listed. A way the main tree cannot hold a path - holding what its HEAD holds of another kind, say - is none.
/// </summary>
public sealed class AgentFoldWordsTests
{
    private static readonly GitEntry BaseFile = new("100644", "blob", "b0");
    private static readonly GitEntry HeadFile = new("100644", "blob", "h1");
    private static readonly GitEntry BaseDirectory = new("040000", "tree", "t0");
    private static readonly GitEntry HeadDirectory = new("040000", "tree", "t1");

    /// <summary>Each way, and what is said of it: whole for a path refused, short for a file in the way.</summary>
    /// <param name="family">What the main tree did since the base, and whether the path is refused or in the way.</param>
    /// <param name="atBase">What the base holds at the path.</param>
    /// <param name="atHead">What the main tree's HEAD holds there.</param>
    /// <param name="main">What the main tree holds there against its HEAD.</param>
    /// <param name="said">What is said.</param>
    [Theory]
    [InlineData("made", "nothing", "same as base", "differs", "'p' is not at the agent's base X, and the main tree made it since, by an uncommitted edit, so writing the agent's copy would lose that change")]
    [InlineData("made", "nothing", "same as base", "ignored", "'p' is not at the agent's base X, and the main tree holds a file there that git ignores, so writing the agent's copy would lose that file")]
    [InlineData("made", "nothing", "same as base", "unlisted, not ignored", "'p' is not at the agent's base X, and the main tree made it since, by an uncommitted edit, so writing the agent's copy would lose that change")]
    [InlineData("made", "nothing", "same as base", "below a committed submodule", "'p' is not at the agent's base X, and the main tree made it since, by a commit, so writing the agent's copy would lose that change")]
    [InlineData("made", "nothing", "same as base", "below a committed submodule, differs", "'p' is not at the agent's base X, and the main tree made it since, by a commit, then changed by an uncommitted edit, so writing the agent's copy would lose that change")]
    [InlineData("made", "nothing", "same as base", "below a staged submodule, differs", "'p' is not at the agent's base X, and the main tree made it since, by an uncommitted edit, so writing the agent's copy would lose that change")]
    [InlineData("made", "nothing", "file", "matches HEAD", "'p' is not at the agent's base X, and the main tree made it since, by a commit, so writing the agent's copy would lose that change")]
    [InlineData("made", "nothing", "file", "differs", "'p' is not at the agent's base X, and the main tree made it since, by a commit, then changed by an uncommitted edit, so writing the agent's copy would lose that change")]
    [InlineData("made", "nothing", "directory", "differs", "'p' is not at the agent's base X, and the main tree made it since, by an uncommitted edit, after a commit made a directory there, so writing the agent's copy would lose that change")]
    [InlineData("made", "directory", "same as base", "differs", "'p' is not at the agent's base X, and the main tree made it since, by an uncommitted edit, so writing the agent's copy would lose that change")]
    [InlineData("made", "directory", "same as base", "case-only rename, differs", "'p' is not at the agent's base X, and the main tree made it since, by an uncommitted edit, after a commit renamed the directory there, so writing the agent's copy would lose that change")]
    [InlineData("made", "directory", "nothing", "differs", "'p' is not at the agent's base X, and the main tree made it since, by an uncommitted edit, after a commit removed the directory there, so writing the agent's copy would lose that change")]
    [InlineData("made", "directory", "nothing", "ignored", "'p' is not at the agent's base X, and the main tree made it since, by an uncommitted edit git ignores, after a commit removed the directory there, so writing the agent's copy would lose that change")]
    [InlineData("made", "directory", "file", "matches HEAD", "'p' is not at the agent's base X, and the main tree made it since, by a commit, so writing the agent's copy would lose that change")]
    [InlineData("made", "directory", "file", "differs", "'p' is not at the agent's base X, and the main tree made it since, by a commit, then changed by an uncommitted edit, so writing the agent's copy would lose that change")]
    [InlineData("made", "directory", "directory", "differs", "'p' is not at the agent's base X, and the main tree made it since, by an uncommitted edit, after a commit changed the directory there, so writing the agent's copy would lose that change")]
    [InlineData("changed", "file", "same as base", "differs", "'p': the main tree changed it after the agent's base X, by an uncommitted edit, so writing the agent's copy would lose that change")]
    [InlineData("changed", "file", "same as base", "case-only rename", "'p': the main tree changed it after the agent's base X, by a commit, so writing the agent's copy would lose that change")]
    [InlineData("changed", "file", "same as base", "case-only rename, differs", "'p': the main tree changed it after the agent's base X, by a commit, then changed by an uncommitted edit, so writing the agent's copy would lose that change")]
    [InlineData("changed", "file", "nothing", "differs", "'p': the main tree changed it after the agent's base X, by an uncommitted edit, after a commit removed it, so writing the agent's copy would lose that change")]
    [InlineData("changed", "file", "nothing", "ignored", "'p': the main tree changed it after the agent's base X, by an uncommitted edit git ignores, after a commit removed it, so writing the agent's copy would lose that change")]
    [InlineData("changed", "file", "file", "matches HEAD", "'p': the main tree changed it after the agent's base X, by a commit, so writing the agent's copy would lose that change")]
    [InlineData("changed", "file", "file", "differs", "'p': the main tree changed it after the agent's base X, by a commit, then changed by an uncommitted edit, so writing the agent's copy would lose that change")]
    [InlineData("changed", "file", "directory", "differs", "'p': the main tree changed it after the agent's base X, by an uncommitted edit, after a commit made a directory of it, so writing the agent's copy would lose that change")]
    [InlineData("directory", "file", "same as base", "differs", "'p' is at the agent's base X, and the main tree holds a directory there now, made by an uncommitted edit, so writing the agent's copy would lose that change")]
    [InlineData("directory", "file", "same as base", "case-only rename, differs", "'p' is at the agent's base X, and the main tree holds a directory there now, made by an uncommitted edit, after a commit renamed it, so writing the agent's copy would lose that change")]
    [InlineData("directory", "file", "nothing", "differs", "'p' is at the agent's base X, and the main tree holds a directory there now, made by an uncommitted edit, after a commit removed it, so writing the agent's copy would lose that change")]
    [InlineData("directory", "file", "nothing", "ignored", "'p' is at the agent's base X, and the main tree holds a directory there now, made by an uncommitted edit git ignores, after a commit removed it, so writing the agent's copy would lose that change")]
    [InlineData("directory", "file", "nothing", "unlisted, not ignored", "'p' is at the agent's base X, and the main tree holds a directory there now, made by an uncommitted edit, after a commit removed it, so writing the agent's copy would lose that change")]
    [InlineData("directory", "file", "file", "differs", "'p' is at the agent's base X, and the main tree holds a directory there now, made by an uncommitted edit, after a commit changed it, so writing the agent's copy would lose that change")]
    [InlineData("directory", "file", "directory", "matches HEAD", "'p' is at the agent's base X, and the main tree holds a directory there now, made by a commit, so writing the agent's copy would lose that change")]
    [InlineData("directory", "file", "directory", "differs", "'p' is at the agent's base X, and the main tree holds a directory there now, made by a commit, then changed by an uncommitted edit, so writing the agent's copy would lose that change")]
    [InlineData("removed", "file", "same as base", "differs", "'p' is at the agent's base X, and missing from the main tree, removed by an uncommitted edit, so writing the agent's copy would lose that change")]
    [InlineData("removed", "file", "same as base", "case-only rename, differs", "'p' is at the agent's base X, and missing from the main tree, removed by an uncommitted edit, after a commit renamed it, so writing the agent's copy would lose that change")]
    [InlineData("removed", "file", "nothing", "matches HEAD", "'p' is at the agent's base X, and missing from the main tree, removed by a commit, so writing the agent's copy would lose that change")]
    [InlineData("removed", "file", "nothing", "differs", "'p' is at the agent's base X, and missing from the main tree, removed by a commit, so writing the agent's copy would lose that change")]
    [InlineData("removed", "file", "file", "differs", "'p' is at the agent's base X, and missing from the main tree, removed by an uncommitted edit, after a commit changed it, so writing the agent's copy would lose that change")]
    [InlineData("removed", "file", "directory", "differs", "'p' is at the agent's base X, and missing from the main tree, removed by an uncommitted edit, after a commit made a directory of it, so writing the agent's copy would lose that change")]
    [InlineData("way-made", "nothing", "same as base", "differs", "made since the agent's base X by an uncommitted edit")]
    [InlineData("way-made", "nothing", "same as base", "ignored", "ignored by git")]
    [InlineData("way-made", "nothing", "same as base", "unlisted, not ignored", "made since the agent's base X by an uncommitted edit")]
    [InlineData("way-made", "nothing", "same as base", "below a committed submodule", "made since the agent's base X by a commit")]
    [InlineData("way-made", "nothing", "same as base", "below a committed submodule, differs", "made since the agent's base X by a commit, then changed by an uncommitted edit")]
    [InlineData("way-made", "nothing", "same as base", "below a staged submodule, differs", "made since the agent's base X by an uncommitted edit")]
    [InlineData("way-made", "nothing", "file", "matches HEAD", "made since the agent's base X by a commit")]
    [InlineData("way-made", "nothing", "file", "differs", "made since the agent's base X by a commit, then changed by an uncommitted edit")]
    [InlineData("way-made", "nothing", "directory", "differs", "made since the agent's base X by an uncommitted edit, after a commit made a directory there")]
    [InlineData("way-made", "directory", "same as base", "differs", "made since the agent's base X by an uncommitted edit")]
    [InlineData("way-made", "directory", "same as base", "case-only rename, differs", "made since the agent's base X by an uncommitted edit, after a commit renamed the directory there")]
    [InlineData("way-made", "directory", "nothing", "differs", "made since the agent's base X by an uncommitted edit, after a commit removed the directory there")]
    [InlineData("way-made", "directory", "nothing", "ignored", "made since the agent's base X by an uncommitted edit git ignores, after a commit removed the directory there")]
    [InlineData("way-made", "directory", "file", "matches HEAD", "made since the agent's base X by a commit")]
    [InlineData("way-made", "directory", "file", "differs", "made since the agent's base X by a commit, then changed by an uncommitted edit")]
    [InlineData("way-made", "directory", "directory", "differs", "made since the agent's base X by an uncommitted edit, after a commit changed the directory there")]
    [InlineData("way-changed", "file", "same as base", "differs", "changed since the agent's base X by an uncommitted edit")]
    [InlineData("way-changed", "file", "same as base", "case-only rename", "changed since the agent's base X by a commit")]
    [InlineData("way-changed", "file", "same as base", "case-only rename, differs", "changed since the agent's base X by a commit, then changed by an uncommitted edit")]
    [InlineData("way-changed", "file", "nothing", "differs", "changed since the agent's base X by an uncommitted edit, after a commit removed it")]
    [InlineData("way-changed", "file", "nothing", "ignored", "changed since the agent's base X by an uncommitted edit git ignores, after a commit removed it")]
    [InlineData("way-changed", "file", "file", "matches HEAD", "changed since the agent's base X by a commit")]
    [InlineData("way-changed", "file", "file", "differs", "changed since the agent's base X by a commit, then changed by an uncommitted edit")]
    [InlineData("way-changed", "file", "directory", "differs", "changed since the agent's base X by an uncommitted edit, after a commit made a directory of it")]
    public void EveryWayTheMainTreeCameToHoldAPath_IsSaidTrue(string family, string atBase, string atHead, string main, string said)
    {
        var based = atBase switch
        {
            "nothing" => null,
            "file" => BaseFile,
            _ => BaseDirectory,
        };
        var head = atHead switch
        {
            "same as base" => based,
            "nothing" => null,
            "file" => HeadFile,
            _ => HeadDirectory,
        };
        AgentFold.Since since = family switch
        {
            "made" or "way-made" => new AgentFold.Since.Made(),
            "changed" or "way-changed" => new AgentFold.Since.Changed(BaseFile.ObjectId),
            "directory" => new AgentFold.Since.MadeADirectory(BaseFile.ObjectId),
            _ => new AgentFold.Since.Removed(BaseFile.ObjectId),
        };
        var came = new AgentFold.Came(
            since.Now,
            based,
            head,
            HeadSpelledOtherwise: main.StartsWith("case-only rename", StringComparison.Ordinal),
            Listed: main.EndsWith("differs", StringComparison.Ordinal),
            GitIgnores: main == "ignored",
            InSubmodule: main.Contains("committed submodule", StringComparison.Ordinal) ? AgentFold.Submodule.Committed
                : main.Contains("staged submodule", StringComparison.Ordinal) ? AgentFold.Submodule.Staged
                : AgentFold.Submodule.None);

        Assert.Equal(
            said,
            family.StartsWith("way-", StringComparison.Ordinal)
                ? since.Short("since the agent's base X", came)
                : since.Said("p", "X", came, agentRemoved: false));
    }

    /// <summary>
    /// What the main tree did at a path since the agent's base is one rule, read by every weighing of such a path: a file
    /// where the base holds none is made, the base's file gone is removed, a directory in its place made of it, and a file
    /// git says the main tree moved from changed; nothing where the main tree holds what the base holds.
    /// </summary>
    [Fact]
    public void WhatTheMainTreeDidSinceTheBase_IsOneRule()
    {
        Assert.IsType<AgentFold.Since.Made>(AgentFold.Since.Of(null, AgentFold.Holds.File, moved: false));
        Assert.Null(AgentFold.Since.Of(null, AgentFold.Holds.Nothing, moved: true));
        Assert.Null(AgentFold.Since.Of(null, AgentFold.Holds.Directory, moved: true));
        Assert.Equal(new AgentFold.Since.Removed("b0"), AgentFold.Since.Of("b0", AgentFold.Holds.Nothing, moved: true));
        Assert.Equal(new AgentFold.Since.MadeADirectory("b0"), AgentFold.Since.Of("b0", AgentFold.Holds.Directory, moved: true));
        Assert.Equal(new AgentFold.Since.Changed("b0"), AgentFold.Since.Of("b0", AgentFold.Holds.File, moved: true));
        Assert.Null(AgentFold.Since.Of("b0", AgentFold.Holds.File, moved: false));
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentFold.Since.Of("b0", (AgentFold.Holds)99, moved: true));
    }

    /// <summary>
    /// Nothing says how a file git ignores came to be where neither HEAD nor the base holds one, so asking is refused rather
    /// than answered with a claim of when it came; one git lists nothing of is ignored only where git says it ignores it.
    /// </summary>
    [Fact]
    public void HowAFileGitIgnoresCame_WhereNothingRecordedIt_IsNotSaid()
    {
        var came = new AgentFold.Came(AgentFold.Holds.File, Based: null, Head: null, HeadSpelledOtherwise: false, Listed: false, GitIgnores: true, AgentFold.Submodule.None);
        var unlisted = came with { GitIgnores = false };

        Assert.True(came.Ignored);
        Assert.Throws<InvalidOperationException>(() => came.How);
        Assert.False(unlisted.Ignored);
        Assert.Equal("by an uncommitted edit", unlisted.How);
    }
}
