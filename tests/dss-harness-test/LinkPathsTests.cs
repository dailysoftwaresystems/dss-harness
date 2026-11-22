using DssHarness.Core.FileSystem;

namespace DssHarness.Tests;

/// <summary>The one walk of links a path's resolution takes: every link followed, each once, ended only by a cycle.</summary>
public sealed class LinkPathsTests
{
    /// <summary>How many links the long chain is made of: more than Linux's forty, which once bounded the walk.</summary>
    private const int Chain = 60;

    /// <summary>
    /// A chain of sixty links - each leading to the next, the last to a directory - is followed to its end, and a file
    /// below its first link resolves to the file in that directory. Links that lead back to one still being followed - a
    /// loop of three - end the walk where the loop closes, saying the path asked about and the link it came back to.
    /// </summary>
    [Fact]
    public void ASixtyLinkChain_Resolves_ALoopEndsAtTheRevisit()
    {
        using var temp = new TempDirectory();
        var real = LinkPaths.Resolve(temp.Path);
        temp.WriteFile(Path.Combine("target", "f.txt"), "f");

        for (var index = Chain - 1; index >= 0; index--)
        {
            TestLinks.DirectoryLink(temp.Combine($"link{index}"), index == Chain - 1 ? temp.Combine("target") : temp.Combine($"link{index + 1}"));
        }

        Assert.Equal(Path.Combine(real, "target", "f.txt"), LinkPaths.Resolve(temp.Combine("link0", "f.txt")));

        // The loop's first link is made a directory until the last can lead to it, then a link to the second.
        Directory.CreateDirectory(temp.Combine("loop0"));
        TestLinks.DirectoryLink(temp.Combine("loop2"), temp.Combine("loop0"));
        TestLinks.DirectoryLink(temp.Combine("loop1"), temp.Combine("loop2"));
        Directory.Delete(temp.Combine("loop0"));
        TestLinks.DirectoryLink(temp.Combine("loop0"), temp.Combine("loop1"));

        var asked = temp.Combine("loop0", "f.txt");

        Assert.Equal(
            $"The links along '{asked}' lead back to '{Path.Combine(real, "loop0")}', which is still being followed: they form a cycle.",
            Assert.Throws<IOException>(() => LinkPaths.Resolve(asked)).Message);
    }
}
