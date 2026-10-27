using DssHarness.Core.Git;

namespace DssHarness.Tests;

/// <summary>One change git's status names: whether the index holds it - what a commit would take - read from its staged column alone.</summary>
public sealed class GitStatusEntryTests
{
    /// <summary>
    /// A change is staged where its staged column names one - added, modified, deleted, renamed, copied, or either side of
    /// a conflict - and not where that column is blank, untracked or ignored.
    /// </summary>
    [Theory]
    [InlineData('A', ' ', true)]
    [InlineData('M', 'M', true)]
    [InlineData('D', ' ', true)]
    [InlineData('R', ' ', true)]
    [InlineData('C', ' ', true)]
    [InlineData('U', 'U', true)]
    [InlineData('A', 'A', true)]
    [InlineData('D', 'D', true)]
    [InlineData(' ', 'M', false)]
    [InlineData(' ', 'D', false)]
    [InlineData('?', '?', false)]
    [InlineData('!', '!', false)]
    public void AChangeIsStaged_WhereItsStagedColumnNamesOne(char index, char workTree, bool staged)
        => Assert.Equal(staged, new GitStatusEntry(index, workTree, new GitName("a.txt", IsUtf8: true), null).IsStaged);
}
