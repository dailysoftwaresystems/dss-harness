using System.Collections.Concurrent;
using System.Text.Json;
using DssHarness.Core.Codebase;
using DssHarness.Core.FileSystem;

namespace DssHarness.Tests;

/// <summary>
/// The codebase graph contract's test kit: every implementation of <see cref="ICodeGraphExtractor"/> is held to the same
/// rules by deriving from it and giving its fixtures, and inherits every test - fixtures for each of its claims, purity
/// and determinism, answers alike from many threads at once, a sound model, tolerance, its claims, its stored extracts
/// under its version, and the edges its fixtures name through the one linker. Each test is one of
/// <see cref="CodeGraphContract"/>'s checks, which <see cref="CodeGraphContractTests"/> shows going red on an
/// implementation that breaks it.
/// </summary>
/// <typeparam name="TExtractor">The implementation, made with no argument as the container makes it.</typeparam>
/// <remarks>
/// The stored extracts are kept with the tests, one file for each fixture under its implementation's name and version.
/// An answer that differs from the one stored under the same version fails: what an implementation answers changed, and
/// its version must be raised. With no file stored for a version yet - the version was just raised - the test fails
/// naming the file; set <see cref="CodeGraphContract.StoreVariable"/> to 1 and run it again to write them, read them, and
/// commit them.
/// </remarks>
public abstract class CodeGraphExtractorContractTests<TExtractor>
    where TExtractor : ICodeGraphExtractor, new()
{
    /// <summary>The implementation under test.</summary>
    protected TExtractor Extractor { get; } = new();

    /// <summary>The implementation's fixtures: source files, and for each what its extract and linking must hold.</summary>
    public abstract IReadOnlyList<CodeGraphFixture> Fixtures { get; }

    /// <summary>It gives fixtures, one at least for each name and extension it claims.</summary>
    [Fact]
    public void ItGivesAFixture_ForEachThingItClaims() => CodeGraphContract.Covered(Extractor, Fixtures);

    /// <summary>Each fixture extracted twice, and once with its line endings changed each way, gives the same extract: spans count lines.</summary>
    [Fact]
    public void EachFixture_IsExtractedAlike_EveryTime_WhateverItsLineEndings() => CodeGraphContract.Deterministic(Extractor, Fixtures);

    /// <summary>Each fixture extracted from many threads at once gives the extract it gives alone: a sync reads files in parallel through one instance.</summary>
    [Fact]
    public void EachFixture_IsExtractedAlike_FromManyThreadsAtOnce() => CodeGraphContract.Concurrent(Extractor, Fixtures);

    /// <summary>Each fixture's extract is a sound model, digests left to the core, holding the nodes the fixture names.</summary>
    [Fact]
    public void EachFixturesExtract_IsASoundModel_HoldingTheNodesItNames() => CodeGraphContract.Sound(Extractor, Fixtures);

    /// <summary>
    /// Every cut of each fixture's first <see cref="CodeGraphContract.EveryCut"/> characters, an empty file and text that
    /// is no code at all each give a sound extract, and never an exception.
    /// </summary>
    [Fact]
    public void EveryCutOfEachFixture_AnEmptyFile_AndTextThatIsNoCode_GiveASoundExtract() => CodeGraphContract.Tolerant(Extractor, Fixtures);

    /// <summary>
    /// Every content test it gives answers alike every time and never throws, of any text; it claims each of its fixtures
    /// in the registry this build holds; and where it shares a claim with another implementation, the two tests never
    /// both say yes on a fixture of either.
    /// </summary>
    [Fact]
    public void ItClaimsEachFixture_ItsContentTestsArePure_AndNeverSayYesWithAnothersOnEithersFixtures()
        => CodeGraphContract.Claimed(Extractor, Fixtures, CodeGraphExtractors.OfThisBuild, FixturesOfEveryImplementation());

    /// <summary>
    /// Each fixture's extract is the one stored with the tests for this version, and nothing is stored for a fixture it no
    /// longer gives: an answer that changed under the same version fails.
    /// </summary>
    [Fact]
    public void EachFixturesExtract_IsTheOneStoredForItsVersion()
        => CodeGraphContract.Stored(
            Extractor,
            Fixtures,
            Path.Combine(Prose.RepositoryRoot(), "tests", "dss-harness-test", "fixtures", "codebase-graph"),
            Environment.GetEnvironmentVariable(CodeGraphContract.StoreVariable) == "1");

    /// <summary>The fixtures linked together by the one linker every language uses give the edges they name, as sure as they name.</summary>
    [Fact]
    public void TheFixturesLinkedTogether_GiveTheEdgesTheyName() => CodeGraphContract.Linked(Extractor, Fixtures);

    /// <summary>The fixtures of every implementation the test assembly holds a kit for, by its name.</summary>
    private static Dictionary<string, IReadOnlyList<CodeGraphFixture>> FixturesOfEveryImplementation()
    {
        var fixtures = new Dictionary<string, IReadOnlyList<CodeGraphFixture>>(StringComparer.Ordinal);

        foreach (var kit in typeof(CodeGraphExtractorContractTests<>).Assembly.GetTypes().Where(type => type is { IsAbstract: false, BaseType.IsGenericType: true }
            && type.BaseType.GetGenericTypeDefinition() == typeof(CodeGraphExtractorContractTests<>)))
        {
            var language = ((ICodeGraphExtractor)Activator.CreateInstance(kit.BaseType!.GetGenericArguments()[0])!).Language;
            fixtures[language] = (IReadOnlyList<CodeGraphFixture>)kit.GetProperty(nameof(Fixtures))!.GetValue(Activator.CreateInstance(kit))!;
        }

        return fixtures;
    }
}

/// <summary>
/// The contract's checks, each failing the test that asks it where an implementation breaks the contract: what the kit
/// runs for every implementation, and what its own tests run on implementations that break each rule.
/// </summary>
public static class CodeGraphContract
{
    /// <summary>The variable that, set to 1, writes each fixture's extract where none is stored for the version.</summary>
    public const string StoreVariable = "DSSHARNESS_STORE_EXTRACTS";

    /// <summary>How many of a fixture's first characters every cut of is tried; beyond them, one cut in <see cref="CutStride"/>.</summary>
    public const int EveryCut = 4_000;

    /// <summary>How far apart the cuts past a fixture's first <see cref="EveryCut"/> characters are.</summary>
    public const int CutStride = 64;

    /// <summary>The implementation gives fixtures, and one at least for each name and extension it claims - the fallback one at least.</summary>
    public static void Covered(ICodeGraphExtractor extractor, IReadOnlyList<CodeGraphFixture> fixtures)
    {
        Assert.True(fixtures.Count > 0, $"'{extractor.Language}' gives no fixture, and the kit holds an implementation to what its fixtures hold");

        foreach (var claim in extractor.Claims.Names)
        {
            Assert.True(fixtures.Any(fixture => string.Equals(FileName(fixture.Path), claim.Value, StringComparison.OrdinalIgnoreCase)), $"'{extractor.Language}' claims the file name '{claim.Value}', and no fixture is named so");
        }

        foreach (var claim in extractor.Claims.Extensions)
        {
            Assert.True(fixtures.Any(fixture => FileName(fixture.Path) is var name && name.LastIndexOf('.') is > 0 and var dot && string.Equals(name[dot..], claim.Value, StringComparison.OrdinalIgnoreCase)), $"'{extractor.Language}' claims the extension '{claim.Value}', and no fixture has it");
        }
    }

    /// <summary>Each fixture extracted twice, and with its line endings changed each way, gives the same extract.</summary>
    public static void Deterministic(ICodeGraphExtractor extractor, IReadOnlyList<CodeGraphFixture> fixtures)
    {
        foreach (var fixture in fixtures)
        {
            var once = Json(extractor.Extract(new CodeFileInput(fixture.Path, fixture.Text)));
            var lf = fixture.Text.Replace("\r\n", "\n", StringComparison.Ordinal);

            Assert.Equal(once, Json(extractor.Extract(new CodeFileInput(fixture.Path, fixture.Text))));
            Assert.Equal(once, Json(extractor.Extract(new CodeFileInput(fixture.Path, lf))));
            Assert.Equal(once, Json(extractor.Extract(new CodeFileInput(fixture.Path, lf.Replace("\n", "\r\n", StringComparison.Ordinal)))));
        }
    }

    /// <summary>
    /// Every fixture extracted again and again by threads of their own, released together through one instance, gives the
    /// extract it gives alone: a sync reads files in parallel through one instance.
    /// </summary>
    public static void Concurrent(ICodeGraphExtractor extractor, IReadOnlyList<CodeGraphFixture> fixtures)
    {
        var alone = fixtures.Select(fixture => Json(extractor.Extract(new CodeFileInput(fixture.Path, fixture.Text)))).ToList();
        var unlike = new ConcurrentQueue<string>();
        using var go = new ManualResetEventSlim();

        var threads = Enumerable.Range(0, Math.Clamp(Environment.ProcessorCount, 4, 16)).Select(index => new Thread(() =>
        {
            go.Wait();

            for (var round = 0; round < 8; round++)
            {
                for (var step = 0; step < fixtures.Count; step++)
                {
                    var at = (step + index) % fixtures.Count;

                    try
                    {
                        if (Json(extractor.Extract(new CodeFileInput(fixtures[at].Path, fixtures[at].Text))) != alone[at])
                        {
                            unlike.Enqueue($"{fixtures[at].Path} was extracted otherwise than alone");
                        }
                    }
                    catch (Exception ex)
                    {
                        unlike.Enqueue($"{fixtures[at].Path} threw: {ex.Message}");
                    }
                }
            }
        })
        { IsBackground = true }).ToList();

        threads.ForEach(thread => thread.Start());
        go.Set();

        Assert.All(threads, thread => Assert.True(thread.Join(TimeSpan.FromMinutes(2)), "a thread extracting the fixtures did not end in two minutes"));
        Assert.True(unlike.IsEmpty, $"'{extractor.Language}' answers otherwise from many threads at once: {string.Join("; ", unlike.Distinct().Take(5))}");
    }

    /// <summary>
    /// Each fixture's extract leaves every digest to the core and, digested, is a sound model: every span inside the file,
    /// every parent present and holding its child, ids unique and of its implementation, kinds of the model's vocabulary,
    /// every reference made from a node of it, text whole - and it holds the nodes the fixture names.
    /// </summary>
    public static void Sound(ICodeGraphExtractor extractor, IReadOnlyList<CodeGraphFixture> fixtures)
    {
        foreach (var fixture in fixtures)
        {
            var input = new CodeFileInput(fixture.Path, fixture.Text);
            var lines = input.Lines;
            var answered = extractor.Extract(input);

            Assert.All(answered.Nodes, node => Assert.True(node.Digest is null, $"'{extractor.Language}' set the digest of '{node.Id}', which is the core's to set"));

            var extract = answered.WithDigests(lines);
            Assert.Null(extract.Problem(extractor.Language, lines.Count));

            foreach (var expected in fixture.Nodes)
            {
                var node = Assert.Single(extract.Nodes, node => node.Id == expected.Id);
                Assert.Equal(expected.Kind, node.Kind);

                if (expected.Lines is { } span)
                {
                    Assert.Equal(span, (node.Start, node.End));
                }
            }
        }
    }

    /// <summary>
    /// Every cut of each fixture's first <see cref="EveryCut"/> characters - and one in <see cref="CutStride"/> past them -
    /// an empty file and text that is no code at all each give a sound extract, and never an exception.
    /// </summary>
    public static void Tolerant(ICodeGraphExtractor extractor, IReadOnlyList<CodeGraphFixture> fixtures)
    {
        foreach (var (path, text) in Texts(fixtures))
        {
            var input = new CodeFileInput(path, text);
            var extract = extractor.Extract(input);

            Assert.NotNull(extract);
            Assert.Null(extract.WithDigests(input.Lines).Problem(extractor.Language, input.Lines.Count));
        }
    }

    /// <summary>
    /// Every content test the implementation gives answers alike each of five times it is asked, and never throws, of any
    /// text - each fixture, every cut of it, an empty one and text that is no code - asked before anything else asks it;
    /// <paramref name="registry"/> gives the implementation each of its fixtures; and where it shares a name or an
    /// extension with another, the two tests never both say yes on a fixture of either.
    /// </summary>
    /// <param name="extractor">The implementation.</param>
    /// <param name="fixtures">Its fixtures.</param>
    /// <param name="registry">The registry it is one of.</param>
    /// <param name="others">The fixtures of the registry's other implementations, by name.</param>
    public static void Claimed(ICodeGraphExtractor extractor, IReadOnlyList<CodeGraphFixture> fixtures, CodeGraphExtractors registry, IReadOnlyDictionary<string, IReadOnlyList<CodeGraphFixture>> others)
    {
        foreach (var claim in extractor.Claims.Names.Concat(extractor.Claims.Extensions).Where(claim => claim.ContentTest is not null))
        {
            foreach (var probe in Texts(fixtures).Select(text => CodeFileText.Probe(text.Text)).Distinct(StringComparer.Ordinal))
            {
                var first = Asked(extractor, claim, probe);

                for (var again = 0; again < 4; again++)
                {
                    Assert.True(Asked(extractor, claim, probe) == first, $"'{extractor.Language}''s content test of its claim '{claim.Value}' says {first} of a text, then otherwise of the same text");
                }
            }
        }

        foreach (var fixture in fixtures)
        {
            Assert.Equal(extractor.Language, registry.Route(fixture.Path, CodeFileText.Probe(fixture.Text)).Extractor?.Language);
        }

        foreach (var (what, claimsOf) in new (string, Func<ICodeGraphExtractor, IReadOnlyList<CodeGraphClaim>>)[] { ("file name", one => one.Claims.Names), ("extension", one => one.Claims.Extensions) })
        {
            foreach (var claim in claimsOf(extractor))
            {
                var sharers = registry.All
                    .Where(other => other.Language != extractor.Language)
                    .SelectMany(other => claimsOf(other)
                        .Where(theirs => string.Equals(theirs.Value, claim.Value, StringComparison.OrdinalIgnoreCase))
                        .Select(theirs => (Other: other, Claim: theirs)));

                foreach (var (other, theirs) in sharers)
                {
                    // A shared claim is only ever accepted with a test on each (the registry refuses it otherwise).
                    Assert.True(claim.ContentTest is not null && theirs.ContentTest is not null, $"'{extractor.Language}' and '{other.Language}' each claim the {what} '{claim.Value}', and a test does not tell them apart");

                    foreach (var fixture in fixtures.Concat(others.GetValueOrDefault(other.Language) ?? []))
                    {
                        var probe = CodeFileText.Probe(fixture.Text);
                        Assert.False(claim.ContentTest!(probe) && theirs.ContentTest!(probe), $"'{extractor.Language}' and '{other.Language}' both say {fixture.Path} is theirs");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Each fixture's extract is the one stored under <paramref name="directory"/> for its implementation and version,
    /// whatever line endings the stored file was checked out with; one is written where none is and <paramref name="store"/>
    /// says so; and the version's folder holds no extract of a fixture the implementation no longer gives.
    /// </summary>
    /// <param name="extractor">The implementation.</param>
    /// <param name="fixtures">Its fixtures.</param>
    /// <param name="directory">Where the stored extracts are kept, a folder for each implementation and version under it.</param>
    /// <param name="store">Whether an extract not stored yet is written.</param>
    public static void Stored(ICodeGraphExtractor extractor, IReadOnlyList<CodeGraphFixture> fixtures, string directory, bool store)
    {
        var version = Path.Combine(directory, extractor.Language, $"v{extractor.Version}");
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var fixture in fixtures)
        {
            var file = Path.Combine(version, fixture.Path.Replace('/', '_') + ".json");
            var answer = Json(extractor.Extract(new CodeFileInput(fixture.Path, fixture.Text))) + "\n";
            named.Add(Path.GetFileName(file));

            if (!File.Exists(file))
            {
                if (!store)
                {
                    Assert.Fail($"no extract of {fixture.Path} is stored for '{extractor.Language}' at version {extractor.Version}, at {file}: set {StoreVariable}=1 and run the tests again to write it, read it, and commit it");
                }

                Directory.CreateDirectory(version);
                File.WriteAllText(file, answer);
            }

            Assert.True(
                File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal) == answer,
                $"'{extractor.Language}' answers for {fixture.Path} what is not stored for version {extractor.Version} at {file}: what it answers changed, so its version is raised and its extracts stored again");
        }

        var orphans = (Directory.Exists(version) ? Directory.EnumerateFiles(version, "*.json") : []).Select(Path.GetFileName).Where(name => !named.Contains(name!)).ToList();
        Assert.True(orphans.Count == 0, $"'{extractor.Language}' version {extractor.Version} has extracts stored for no fixture it gives, in {version}: {string.Join(", ", orphans)}; remove them");
    }

    /// <summary>The fixtures linked together by the one linker give the edges they name, from the line they name, as sure as they name.</summary>
    public static void Linked(ICodeGraphExtractor extractor, IReadOnlyList<CodeGraphFixture> fixtures)
    {
        var linked = CodeGraphLinker.Link([.. fixtures.Select(fixture => new CodeGraphFile(fixture.Path, extractor.Language, extractor.Extract(new CodeFileInput(fixture.Path, fixture.Text))))]);

        foreach (var expected in fixtures.SelectMany(fixture => fixture.Edges))
        {
            Assert.Contains(expected, linked.Edges);
        }
    }

    /// <summary>The extract as the store writes it, on one line.</summary>
    private static string Json(FileExtract extract) => JsonSerializer.Serialize(extract, JsonStateFile.LineOptions);

    private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];

    /// <summary>What a claim's content test says of <paramref name="probe"/>, failing where it throws.</summary>
    private static bool Asked(ICodeGraphExtractor extractor, CodeGraphClaim claim, string probe)
    {
        try
        {
            return claim.ContentTest!(probe);
        }
        catch (Exception ex)
        {
            Assert.Fail($"'{extractor.Language}''s content test of its claim '{claim.Value}' threw on a text: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Every text an implementation must answer for: each fixture and every cut of it, its line endings changed each way, an
    /// empty file of its path, and text that is no code at all.
    /// </summary>
    private static IEnumerable<(string Path, string Text)> Texts(IReadOnlyList<CodeGraphFixture> fixtures)
        => fixtures.SelectMany(fixture => Cuts(fixture.Text)
                .Concat([fixture.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal), string.Empty, "été }}}{{ ((( \"\"\" /* -- # \t\r\n\r\n\u0000 ]]] <<< \U0001F600\U0001F600 \r"])
                .Select(text => (fixture.Path, text)));

    /// <summary><paramref name="text"/> cut after each of its first <see cref="EveryCut"/> characters, and after one in <see cref="CutStride"/> past them: every pair of a surrogate split among them.</summary>
    private static IEnumerable<string> Cuts(string text)
    {
        for (var length = 0; length <= text.Length; length += length < EveryCut ? 1 : CutStride)
        {
            yield return text[..length];
        }
    }
}

/// <summary>One source file of an implementation's fixtures, and what its extract and linking must hold.</summary>
/// <param name="Path">Its path, relative to a tree.</param>
/// <param name="Text">Its text.</param>
public sealed record CodeGraphFixture(string Path, string Text)
{
    /// <summary>Nodes its extract must hold, each once, of the kind - and, where given, the lines - named.</summary>
    public IReadOnlyList<CodeGraphExpectedNode> Nodes { get; init; } = [];

    /// <summary>Edges linking the fixtures together must give.</summary>
    public IReadOnlyList<CodeEdge> Edges { get; init; } = [];
}

/// <summary>A node an extract must hold.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Kind">Its kind.</param>
/// <param name="Lines">Its first and last lines, where they are pinned.</param>
public sealed record CodeGraphExpectedNode(string Id, string Kind, (int Start, int End)? Lines = null);
