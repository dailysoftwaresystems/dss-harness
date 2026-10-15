using System.Text.RegularExpressions;

namespace DssHarness.Tests;

/// <summary>
/// The repository's own guide for coding agents, under <c>.claude/skills/dss-harness</c>: a guide that sends its reader
/// to a file that moved, or keeps a page its map never names, is read as current all the same, so what it points at is
/// held to the tree.
/// </summary>
public sealed partial class RepositorySkillTests
{
    private static readonly string Guide = Path.Combine(Prose.RepositoryRoot(), ".claude", "skills", "dss-harness");

    /// <summary>
    /// Every relative link of every page of the guide names a file or a directory the repository holds, read from the
    /// page's own directory with any anchor after it set aside; every broken one is named at once.
    /// </summary>
    [Fact]
    public void EveryLinkOfTheGuide_NamesSomethingTheRepositoryHolds()
    {
        var links = Pages()
            .SelectMany(page => Links(page).Select(target => (page, target)))
            .Where(link => !link.target.Contains("://", StringComparison.Ordinal) && !link.target.StartsWith('#'))
            .ToList();

        Assert.NotEmpty(links);
        Assert.Empty(links
            .Select(link => (link.page, path: Path.GetFullPath(Path.Combine(Path.GetDirectoryName(link.page)!, link.target.Split('#')[0]))))
            .Where(link => !File.Exists(link.path) && !Directory.Exists(link.path))
            .Select(link => $"{Path.GetRelativePath(Guide, link.page)} -> {link.path}"));
    }

    /// <summary>
    /// The guide's front page links each of its reference pages, so none is kept that no reader is sent to, and it is
    /// named as its directory is: the name it is invoked by.
    /// </summary>
    [Fact]
    public void TheFrontPage_IsNamedAsItsDirectory_AndLinksEveryReferencePage()
    {
        var front = Path.Combine(Guide, "SKILL.md");
        var linked = Links(front).ToHashSet(StringComparer.Ordinal);
        var references = Directory.GetFiles(Path.Combine(Guide, "references"), "*.md");

        Assert.Contains("name: dss-harness", File.ReadAllLines(front).TakeWhile(line => line != "description: >"));
        Assert.NotEmpty(references);
        Assert.All(references, reference => Assert.Contains("references/" + Path.GetFileName(reference), linked));
    }

    /// <summary>The guide's pages, its front page among them.</summary>
    private static string[] Pages()
    {
        var pages = Directory.GetFiles(Guide, "*.md", SearchOption.AllDirectories);

        Assert.Contains(Path.Combine(Guide, "SKILL.md"), pages);

        return pages;
    }

    /// <summary>The target of every Markdown link on <paramref name="page"/>, as written.</summary>
    private static IEnumerable<string> Links(string page) =>
        Link().Matches(File.ReadAllText(page)).Select(match => match.Groups["target"].Value);

    [GeneratedRegex(@"\]\((?<target>[^)\s]+)\)")]
    private static partial Regex Link();
}
