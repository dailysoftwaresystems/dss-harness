using DssHarness.Core.Application;
using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;
using DssHarness.Core.Composition;
using DssHarness.Core.Output;
using DssHarness.Core.Results;
using Microsoft.Extensions.DependencyInjection;

namespace DssHarness.Tests;

/// <summary>
/// The registry of the codebase graph's implementations: what it refuses when it is built, which implementation reads a
/// file, and that the one the container builds holds together - so a clash between two languages is a red test in the
/// change that brings it.
/// </summary>
public sealed class CodeGraphExtractorsTests
{
    /// <summary>The registry the container builds holds every implementation of the core, each once, and is the one help reads.</summary>
    [Fact]
    public void TheContainersRegistry_HoldsEveryImplementationOfTheCore()
    {
        using var provider = new ServiceCollection()
            .AddDssHarness(new SessionOptions { Command = "test" }, OperationId.New())
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        var registry = provider.GetRequiredService<CodeGraphExtractors>();

        Assert.Equal(CodeGraphExtractors.Implementations, registry.All.Select(extractor => extractor.GetType()).OrderBy(type => type.FullName, StringComparer.Ordinal));
        Assert.Contains(typeof(MarkdownExtractor), CodeGraphExtractors.Implementations);
        Assert.Contains(typeof(ListedExtractor), CodeGraphExtractors.Implementations);
        Assert.Equal(registry.All.Select(extractor => extractor.Language), CodeGraphExtractors.OfThisBuild.All.Select(extractor => extractor.Language));
        Assert.Equal("listed", registry.Fallback.Language);
        Assert.DoesNotContain("listed", registry.Languages);
        Assert.Contains("markdown", registry.Languages);
    }

    /// <summary>Every implementation of the core has a test class of the contract's kit: a language is one class and its tests.</summary>
    [Fact]
    public void EveryImplementation_HasATestClassOfTheKit()
    {
        var kits = typeof(CodeGraphExtractorsTests).Assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, BaseType.IsGenericType: true } && type.BaseType.GetGenericTypeDefinition() == typeof(CodeGraphExtractorContractTests<>))
            .Select(type => type.BaseType!.GetGenericArguments()[0])
            .ToHashSet();

        Assert.All(CodeGraphExtractors.Implementations, implementation => Assert.Contains(implementation, kits));
    }

    /// <summary>Two implementations of one name are refused: a name is an id's prefix and an extract's name.</summary>
    [Fact]
    public void TwoImplementationsOfOneName_AreRefused()
    {
        var refused = Refused(Fake("toy", ".toy"), Fake("toy", ".other"), Fallback());

        Assert.Contains("are named 'toy'", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A name no id and no file name can carry is refused, and so is the core's own name for a file.</summary>
    /// <param name="language">The name.</param>
    [Theory]
    [InlineData("")]
    [InlineData("Toy")]
    [InlineData("1toy")]
    [InlineData("c#")]
    [InlineData("to-y")]
    [InlineData("file")]
    public void ANameNoIdCanCarry_IsRefused(string language)
    {
        var refused = Refused(Fake(language, ".toy"), Fallback());

        Assert.Contains("is not a language's name", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A version below 1 is refused: it is part of every extract's name.</summary>
    /// <param name="version">The version.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AVersionBelowOne_IsRefused(int version)
    {
        var refused = Refused(Fake("toy", ".toy") with { Version = version }, Fallback());

        Assert.Contains("a version is a positive integer", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A registry with no fallback, or two, is refused: one implementation lists every file no other reads.</summary>
    [Fact]
    public void NoFallback_OrTwo_AreRefused()
    {
        Assert.Contains("no implementation of the codebase graph's contract is the fallback", Refused(Fake("toy", ".toy")).Message, StringComparison.Ordinal);
        Assert.Contains("are each the fallback", Refused(Fallback(), Fallback("other")).Message, StringComparison.Ordinal);
        Assert.Contains("'listed' is the fallback and claims names or extensions too", Refused(new FakeExtractor("listed", new CodeGraphClaims([], [new(".txt")], Fallback: true))).Message, StringComparison.Ordinal);
        Assert.Contains("'listed' is the fallback and claims names or extensions too", Refused(new FakeExtractor("listed", new CodeGraphClaims([new("LICENSE")], [], Fallback: true))).Message, StringComparison.Ordinal);
    }

    /// <summary>A claimed name no file can bear, and an extension no file has, are refused: a claim that could never match is a defect.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("dir/Makefile")]
    [InlineData("dir\\Makefile")]
    public void AClaimedNameNoFileBears_IsRefused(string name)
        => Assert.Contains($"'toy' claims the file name '{name}', which no file is named", Refused(Fake("toy", names: [name]), Fallback()).Message, StringComparison.Ordinal);

    /// <summary>An extension is a dot and what follows a name's last dot: one without its dot, a bare dot, two dots or a separator is refused.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("toy")]
    [InlineData(".")]
    [InlineData(".tar.gz")]
    [InlineData("..x")]
    [InlineData("./x")]
    [InlineData(".a\\b")]
    public void AClaimedExtensionNoFileHas_IsRefused(string extension)
        => Assert.Contains($"'toy' claims the extension '{extension}', which no file has", Refused(Fake("toy", extension), Fallback()).Message, StringComparison.Ordinal);

    /// <summary>
    /// Two that claim one extension or one exact name, in any case, are refused where either gives it no content test -
    /// never decided by an order - and accepted where both do.
    /// </summary>
    [Fact]
    public void ASharedClaim_IsRefusedWithoutAContentTestOnEach_AndAcceptedWithOne()
    {
        static bool Yes(CodeText probe) => probe.StartsWith("toy");
        static bool No(CodeText probe) => !Yes(probe);

        var extension = Refused(Fake("toy", ".x", Yes), Fake("other", ".X"), Fallback());
        Assert.Contains("'other', 'toy' each claim the extension '.x'", extension.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'other' gives it no content test", extension.Message, StringComparison.Ordinal);

        var name = Refused(Fake("toy", names: ["Makefile"]), Fake("other", names: ["makefile"]), Fallback());
        Assert.Contains("each claim the file name", name.Message, StringComparison.Ordinal);
        Assert.Contains("give it no content test", name.Message, StringComparison.Ordinal);

        var registry = new CodeGraphExtractors([Fake("toy", ".x", Yes), Fake("other", ".x", No), Fallback()]);
        Assert.Equal("toy", registry.Route("a/b.x", "toy file").Extractor?.Language);
        Assert.Equal("other", registry.Route("a/b.x", "some file").Extractor?.Language);
    }

    /// <summary>
    /// At a file, the claimants of its exact name read it before those of its extension, whatever order they are in; a file
    /// no name or extension claims - a name with no extension, a name that begins with its only dot - is the fallback's.
    /// </summary>
    [Fact]
    public void AnExactName_IsNarrowerThanAnExtension_AndAFileNoneClaims_IsTheFallbacks()
    {
        var registry = new CodeGraphExtractors([Fake("byext", ".json"), Fake("byname", names: ["package.json"]), Fallback()]);

        Assert.Equal("byname", registry.Route("web/Package.JSON", string.Empty).Extractor?.Language);
        Assert.Equal("byext", registry.Route("web/other.json", string.Empty).Extractor?.Language);
        Assert.Equal("listed", registry.Route("web/Makefile", string.Empty).Extractor?.Language);
        Assert.Equal("listed", registry.Route(".json", string.Empty).Extractor?.Language);
        Assert.Equal("listed", registry.Route("dir.json/file", string.Empty).Extractor?.Language);
        Assert.Empty(registry.ClaimantsOf("web/Makefile"));
        Assert.Equal(["byname"], registry.ClaimantsOf("package.json").Select(claimed => claimed.Extractor.Language));
    }

    /// <summary>
    /// Where a repository maps file name endings to implementations, the longest ending a file's name ends with is
    /// claimed between the registry's exact names and its extensions: an exact name still wins, and an ending wins over
    /// an extension, the longest ending over a shorter one.
    /// </summary>
    [Fact]
    public void AMappedEndingIsClaimedBetweenExactNamesAndExtensions_TheLongestFirst()
    {
        var registry = new CodeGraphExtractors([Fake("byext", ".json"), Fake("byname", names: ["package.json"]), Fake("mapper", ".map"), Fallback()]);

        Assert.Equal("byname", registry.Route("web/Package.JSON", string.Empty).Extractor?.Language);

        var claims = new Dictionary<string, IReadOnlyList<string>> { ["byext"] = [".target.json"], ["mapper"] = [".values.map", ".map"] };

        Assert.Equal("byext", registry.Route("app/settings.target.json", string.Empty, claims).Extractor?.Language);
        Assert.Equal("mapper", registry.Route("app/values.map", string.Empty, claims).Extractor?.Language);
        Assert.Equal("byext", registry.Route("app/settings.json", string.Empty, claims).Extractor?.Language);
        Assert.Equal("listed", registry.Route("app/other.yml", string.Empty, claims).Extractor?.Language);
        Assert.Equal([("mapper", ".map")], registry.ClaimantsOf("app/values.map", claims).Select(claimed => (claimed.Extractor.Language, claimed.Claim.Value)));

        // An implementation the build holds none of is left out, as its workflow's check refuses one before any run.
        Assert.Equal("byext", registry.Route("app/settings.target.json", string.Empty, new Dictionary<string, IReadOnlyList<string>> { ["gone"] = [".target.json"] }).Extractor?.Language);
    }

    /// <summary>Where no claimant's test says yes, or several do, no implementation reads the file, and the route says why.</summary>
    [Fact]
    public void WhereNoneOrSeveralContentTestsSayYes_TheFileIsAmbiguous_WithWhy()
    {
        var registry = new CodeGraphExtractors([Fake("toy", ".x", _ => true), Fake("other", ".x", probe => probe.Length > 3), Fallback()]);

        var several = registry.Route("a.x", "long text");
        Assert.Null(several.Extractor);
        Assert.Equal("'other', 'toy' each say by their content tests that it is theirs", several.Ambiguous);

        var none = new CodeGraphExtractors([Fake("toy", ".x", _ => false), Fake("other", ".x", _ => false), Fallback()]).Route("a.x", "text");
        Assert.Null(none.Extractor);
        Assert.Equal("'other', 'toy' claim it, and none of their content tests says it is theirs", none.Ambiguous);

        Assert.Equal("toy", registry.Route("a.x", "ab").Extractor?.Language);
    }

    /// <summary>The registry finds an implementation by its name, and lists every one but the fallback as a language.</summary>
    [Fact]
    public void Find_AndLanguages_ReadTheNames()
    {
        var registry = new CodeGraphExtractors([Fake("zeta", ".z"), Fake("alpha", ".a"), Fallback()]);

        Assert.Equal(["alpha", "listed", "zeta"], registry.All.Select(extractor => extractor.Language));
        Assert.Equal(["alpha", "zeta"], registry.Languages);
        Assert.Equal("zeta", registry.Find("zeta")?.Language);
        Assert.Null(registry.Find("Zeta"));
        Assert.Null(registry.Find("cobol"));
    }

    private static HarnessException Refused(params ICodeGraphExtractor[] extractors)
    {
        var refused = Assert.Throws<HarnessException>(() => new CodeGraphExtractors(extractors));
        Assert.Equal(HarnessExit.InternalError, refused.ExitCode);
        Assert.StartsWith("The codebase graph's implementations do not hold together: ", refused.Message, StringComparison.Ordinal);
        return refused;
    }

    /// <summary>
    /// What a content test is given is the decoded text itself, read by position however long - its surrogate pairs whole
    /// with it - so a marker anywhere in a file decides its claim. Red with the text the test is given cut at its head.
    /// </summary>
    [Fact]
    public void AContentTest_IsGivenTheWholeText_HoweverLong()
    {
        var text = CodeText.Of(new string('a', 100_000) + "\U0001F600" + "tail");
        var registry = new CodeGraphExtractors([Fake("toy", ".x", probe => probe.EndsWith("\U0001F600tail")), Fake("other", ".x", probe => !probe.EndsWith("\U0001F600tail")), Fallback()]);

        Assert.Equal("toy", registry.Route("a.x", text).Extractor?.Language);
        Assert.Equal("other", registry.Route("a.x", "a").Extractor?.Language);
    }

    private static FakeExtractor Fake(string language, string? extension = null, Func<CodeText, bool>? test = null, string[]? names = null)
        => new(language, new CodeGraphClaims(
            [.. (names ?? []).Select(name => new CodeGraphClaim(name, test))],
            extension is null ? [] : [new CodeGraphClaim(extension, test)]));

    private static FakeExtractor Fallback(string language = "listed") => new(language, CodeGraphClaims.OfFallback);

    /// <summary>An implementation that claims what it is told and declares nothing.</summary>
    private sealed record FakeExtractor(string Language, CodeGraphClaims Claims) : ICodeGraphExtractor
    {
        public int Version { get; init; } = 1;

        public bool Outlines => false;

        public CodeGraphExtractorHelp Help { get; } = new("nothing", []);

        public FileExtract Extract(CodeFileInput input) => FileExtract.Empty;
    }

    /// <summary>
    /// The architecture and the help say what the code holds of how a file is claimed and how keys are joined: a
    /// workflow's claimed endings after an exact name and before an extension, the keys' join of candidates and its bound,
    /// and the default naming policies the shape holds.
    /// </summary>
    [Fact]
    public void TheDocs_SayTheClaimOrderTheKeysJoinAndItsDefault_AsTheCodeHoldsThem()
    {
        var architecture = Prose.Document("docs", "architecture.md");
        var help = Prose.Words(DssHarness.Core.Help.HelpTopics.Render("codebase-graph"));
        var defaults = string.Join(" and ", CodebaseGraphShape.DefaultKeyNaming.Select(naming => $"`{naming}`"));
        var policies = string.Join(", ", CodebaseGraphShape.NamingValues.SkipLast(1).Select(naming => $"`{naming}`")) + $" or `{CodebaseGraphShape.NamingValues[^1]}`";

        Assert.Contains($"(`codebaseGraph.keyNaming`: {policies}, {defaults} the default)", architecture, StringComparison.Ordinal);
        Assert.Contains("every key a literal or a name names is joined, however many keys share it", architecture, StringComparison.Ordinal);
        Assert.Contains("A file goes to the claimants of its exact name, else of an ending `codebaseGraph.claims` maps, else of its extension, else to the fallback", architecture, StringComparison.Ordinal);
        Assert.Contains("by its exact name, else by an ending codebaseGraph.claims maps, else by its extension, else by the fallback", help, StringComparison.Ordinal);
        Assert.Contains("every key a literal or a name names is joined, however many keys share it", help, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing of the tool outside the implementations' own folder names one of them or their namespace: the store, the
    /// sync, the linker, the step, help and the validator know no language by name, so a new one is one class and its tests.
    /// A type nested in one is its own alone, which no code outside it can name.
    /// </summary>
    [Fact]
    public void NothingOutsideTheImplementationsFolder_NamesOneOfThem()
    {
        var languages = typeof(ICodeGraphExtractor).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(MarkdownExtractor).Namespace && !type.IsNested && !type.Name.StartsWith('<'))
            .Select(type => type.Name)
            .Append(typeof(MarkdownExtractor).Namespace!)
            .ToList();
        var root = Path.Combine(Prose.RepositoryRoot(), "src");
        var own = Path.Combine(root, "DssHarness.Core", "Codebase", "Languages") + Path.DirectorySeparatorChar;

        var naming = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.StartsWith(own, StringComparison.OrdinalIgnoreCase) && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(file => languages.Where(name => File.ReadAllText(file).Contains(name, StringComparison.Ordinal)).Select(name => $"{Path.GetRelativePath(root, file)} names {name}"))
            .ToList();

        Assert.Contains("MarkdownExtractor", languages);
        Assert.Empty(naming);
    }
}
