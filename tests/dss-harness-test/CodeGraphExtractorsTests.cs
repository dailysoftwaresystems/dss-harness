using DssHarness.Core.Application;
using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;
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
        using var provider = TestRegistrations.Of(new SessionOptions { Command = "test" }, OperationId.New())
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

    /// <summary>
    /// A limit whose short name is none - empty, with a capital, a blank or an underscore, beginning with no letter or ending
    /// with a dash - is refused: the name is what the help lists a limit under and a sites answer names it by.
    /// </summary>
    /// <param name="name">The limit's name.</param>
    [Theory]
    [InlineData("")]
    [InlineData("Macros")]
    [InlineData("if branches")]
    [InlineData("if_branches")]
    [InlineData("1branches")]
    [InlineData("-branches")]
    [InlineData("branches-")]
    public void ALimitNamedByNoShortName_IsRefused(string name)
        => Assert.Contains($"'toy' names a limit '{name}', which is no short name", Refused(Fake("toy", ".toy") with { Help = new("nothing", [(name, "a thing")]) }, Fallback()).Message, StringComparison.Ordinal);

    /// <summary>
    /// Two limits of one implementation of one name are refused, so the name an answer says finds one limit in the help; two
    /// implementations may each name one of theirs alike, for the help lists each under its own language.
    /// </summary>
    [Fact]
    public void TwoLimitsOfOneImplementationOfOneName_AreRefused()
    {
        Assert.Contains("'toy' names 2 of its limits 'branches': each is named once", Refused(Fake("toy", ".toy") with { Help = new("nothing", [("branches", "one thing"), ("macros", "another"), ("branches", "a third")]) }, Fallback()).Message, StringComparison.Ordinal);
        Assert.Equal(["if-branches2", "if-branches2"], new CodeGraphExtractors([Fake("toy", ".toy") with { Help = new("nothing", [("if-branches2", "a thing")]) }, Fake("other", ".other") with { Help = new("nothing", [("if-branches2", "a thing")]) }, Fallback()]).All.SelectMany(extractor => extractor.Help.DoesNotSee).Select(limit => limit.Name));
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

        public CodeGraphExtractorHelp Help { get; init; } = new("nothing", []);

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
    /// The README says the forms the query command's --sites datum takes - a key path dotted, its names' own dots as they
    /// are or a name holding a dot or a bracket quoted in brackets in either quote mark, or a path as .NET's configuration
    /// writes one - as the graph's own help does, so a later wording change to it is held.
    /// </summary>
    [Fact]
    public void TheReadme_SaysTheDatumFormsTheSitesOperationTakes()
    {
        Assert.Contains(
            Prose.Words(
                "a key path (dotted - a name holding a dot or a bracket quoted in brackets, `Logging.LogLevel[\"Microsoft.AspNetCore\"]` "
                + "or `Logging.LogLevel['Microsoft.AspNetCore']`, or with its names' own dots as they are, asking of every key it "
                + "may mean - or as .NET's configuration writes one: `Payments:TimeoutSeconds`, `Payments__TimeoutSeconds`)"),
            Prose.Document("README.md"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The architecture says the spellings a key's path takes as the one owner writes and reads them back, and the paths a
    /// config's keys are named by, as the code holds them, so a later wording change to either is held.
    /// </summary>
    [Fact]
    public void TheArchitecture_SaysTheSpellingsAKeysPathTakes_AsTheCodeWritesThem()
    {
        var architecture = Prose.Document("docs", "architecture.md");

        Assert.Contains(
            Prose.Words(
                $"A key's path is written, and read back, by one owner (`{nameof(CodeKeyPaths)}`): each name joined by a dot, an item's "
                + "place and a name empty, of white space, holding a control character, or holding a dot or a bracket - the characters a "
                + $"path is parted by - in brackets, quoted as `{nameof(ReportText)}.{nameof(ReportText.Quoted)}` writes it and read back "
                + $"by `{nameof(ReportText)}.{nameof(ReportText.Unquoted)}`, so a path is one line and one sequence of places "
                + "(`targets.x64.callingConvention`, `limits[1].value`, `packages[\"\"].name`, `messages[\"Sure?\\nIt cannot be "
                + "undone.\"]`, `Logging.LogLevel[\"Microsoft.AspNetCore\"]`); a place in single quotes is read as the same place "
                + "(`['Microsoft.AspNetCore']`, its own mark and a backslash escaped as `Quoted` escapes a double quote - one escape "
                + "rule, `ReportText.QuotedBy`, with the mark its parameter) and never written; a key node is named by its own name "
                + "where it stands alone on a line (`Microsoft.AspNetCore`), every reader of data files - JSON, YAML, XML, MSBuild - "
                + "writing its keys' paths through the owner."),
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            Prose.Words(
                "keys of a `.resx`'s `data name=`, of a `.props` or `.targets`'s `PropertyGroup` children and of a `.config`'s "
                + "`appSettings` `<add key>` and `connectionStrings` `<add name>` (`appSettings.Timeout`, `connectionStrings.Main`, "
                + "`appSettings[\"Serilog.MinimumLevel\"]`, the value or connection string the summary)"),
            architecture,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The architecture says what the linker's join and the queries' reading of a name take - the path as .NET's
    /// configuration spells it, a literal writing a path in brackets in either quote mark, a path typed with single quotes
    /// or needless quotes, the keys an environment variable's spelling reaches and the loose form of a dotted path - and
    /// what Places reads and EndsWithPlaces decides, as the code holds them, so a later wording change to any of them is
    /// held.
    /// </summary>
    [Fact]
    public void TheArchitecture_SaysWhatTheJoinerAndTheQueriesRead_AsTheCodeHoldsThem()
    {
        var architecture = Prose.Document("docs", "architecture.md");

        Assert.Contains(
            Prose.Words(
                "The linker joins each key to the code that names it: a string literal equal to the key's last segment, its whole "
                + "path, the path as .NET's configuration spells it (`Payments:TimeoutSeconds`, `Payments__TimeoutSeconds`, "
                + "`Limits:1:Value`, `Logging:LogLevel:Microsoft.AspNetCore` naming `Logging.LogLevel[\"Microsoft.AspNetCore\"]`, read "
                + $"by `{nameof(CodeKeyPaths)}.{nameof(CodeKeyPaths.Dotted)}`) or a path that runs through a YAML alias or merge to it - "
                + "the longest part of the path its file holds deciding, a mapping's own key shadowing a merged one's, each place "
                + "looked for once and a path that comes round to itself through its references ending there - and a literal writing a "
                + "path in brackets, in either quote mark (lodash's `\"a['b.c']\"`), names the key of the path the writer writes "
                + $"(`{nameof(CodeKeyPaths)}.{nameof(CodeKeyPaths.Canonical)}`), never the loose form of a key's path, its names' own "
                + "dots as they are, which code splits as .NET's configuration does"),
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            Prose.Words(
                $"one spelt as the one owner of a key path's spellings reads it (`{nameof(CodeKeyPaths)}.{nameof(CodeKeyPaths.Dotted)}`) - a "
                + "path typed with single quotes, with both quote marks or with quotes a name needs not read first as the writer writes "
                + $"it (`{nameof(CodeKeyPaths)}.{nameof(CodeKeyPaths.Canonical)}`), every rule after reading that form - or with some of "
                + "its scopes, of a key path every key whose own name spells it as .NET's configuration reads a key's name - an "
                + $"environment variable's, a settings file's flat key (`{nameof(CodeKeyPaths)}.{nameof(CodeKeyPaths.Spellings)}`: "
                + "`Payments__TimeoutSeconds` under a compose file's `environment`) - so a key path in any spelling finds the same keys, "
                + "and, last of the rules, the loose form of a dotted path, its names' own dots written as they are, naming every key "
                + $"whose path so written is that text (`{nameof(CodeKeyPaths)}.{nameof(CodeKeyPaths.Unescaped)}`, one index built once a "
                + "view, in the queries alone and never the linker, for code splits a dotted literal)"),
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            Prose.Words(
                $"a path's places read by `{nameof(CodeKeyPaths)}.{nameof(CodeKeyPaths.Places)}`, which reads a place in brackets only as a "
                + "key's path writes one - an item's digits, a quoted name, whose dot parts nothing and whose own text is the last name "
                + "(`Microsoft.AspNetCore` of `Logging.LogLevel[\"Microsoft.AspNetCore\"]`) - so `this[]` of `Canvas.this[]` and "
                + "`operator[]` of `Vec::operator[]` are names; and a name written with some of its places ends a path only where one of "
                + $"the path's places begins (`{nameof(CodeKeyPaths)}.{nameof(CodeKeyPaths.EndsWithPlaces)}`)"),
            architecture,
            StringComparison.Ordinal);
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
