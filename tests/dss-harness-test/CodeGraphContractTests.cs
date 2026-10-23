using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;
using Xunit.Sdk;

namespace DssHarness.Tests;

/// <summary>
/// The contract's checks themselves: each passes a sound implementation, and fails one that breaks its rule - so the kit
/// every implementation derives from holds it to something.
/// </summary>
public sealed class CodeGraphContractTests
{
    private static readonly CodeGraphFixture A = new("src/a.toy", "class A\ncall B\n")
    {
        Nodes = [new("toy:A", CodeNodeKinds.Type, (1, 1))],
        Edges = [new("toy:A", "toy:B", CodeEdgeKinds.Instantiates, 2, CodeConfidences.Unique)],
    };

    private static readonly CodeGraphFixture B = new("src/b.toy", "class B\n") { Nodes = [new("toy:B", CodeNodeKinds.Type, (1, 1))] };

    private static readonly IReadOnlyList<CodeGraphFixture> Fixtures = [A, B];

    /// <summary>A sound implementation passes every check, and its extracts, once stored, read back as stored.</summary>
    [Fact]
    public void ASoundImplementation_PassesEveryCheck()
    {
        var toy = new Toy();
        using var stored = new TempDirectory();

        CodeGraphContract.Covered(toy, Fixtures);
        CodeGraphContract.Deterministic(toy, Fixtures);
        CodeGraphContract.Concurrent(toy, Fixtures);
        CodeGraphContract.Sound(toy, Fixtures);
        CodeGraphContract.Tolerant(toy, Fixtures);
        CodeGraphContract.Claimed(toy, Fixtures, Registry(toy), new Dictionary<string, IReadOnlyList<CodeGraphFixture>>());
        CodeGraphContract.Stored(toy, Fixtures, stored.Path, store: true);
        CodeGraphContract.Stored(toy, Fixtures, stored.Path, store: false);
        CodeGraphContract.Linked(toy, Fixtures);
    }

    /// <summary>
    /// No fixture at all, a claimed extension no fixture has and a claimed name no fixture bears each fail; a name borne
    /// in another case passes.
    /// </summary>
    [Fact]
    public void Covered_Fails_WithNoFixture_OrAClaimNoFixtureBears()
    {
        CodeGraphContract.Covered(new Toy { Claims = new([new("TOYFILE")], [new(".toy")]) }, [.. Fixtures, new("dir/Toyfile", "class T\n")]);
        Red(() => CodeGraphContract.Covered(new Toy(), []), "gives no fixture");
        Red(() => CodeGraphContract.Covered(new Toy { Claims = CodeGraphClaims.OfExtensions(".toy", ".other") }, Fixtures), "claims the extension '.other'");
        Red(() => CodeGraphContract.Covered(new Toy { Claims = new([new("Toyfile")], [new(".toy")]) }, Fixtures), "claims the file name 'Toyfile'");
    }

    /// <summary>An answer that changes from one call to the next, or with the file's line endings, fails.</summary>
    [Fact]
    public void Deterministic_Fails_OnAnAnswerThatChangesBetweenCalls_OrWithTheLineEndings()
    {
        var calls = 0;

        Red(() => CodeGraphContract.Deterministic(new Toy { Answer = input => Toy.Read(input) with { Generated = (++calls % 2).ToString(System.Globalization.CultureInfo.InvariantCulture) } }, Fixtures));
        Red(() => CodeGraphContract.Deterministic(new Toy { Answer = input => new([new("toy:A", CodeNodeKinds.Type, "A", "A", 1, 1, input.Text.Split('\n')[0])]) }, [A with { Text = "class A\r\ncall B\r\n" }]));

        // Fixtures written with line feeds, read otherwise only once their endings are carriage return and line feed.
        Red(() => CodeGraphContract.Deterministic(new Toy { Answer = input => Toy.Read(input) with { Generated = input.Text.Contains('\r', StringComparison.Ordinal) ? "crlf" : null } }, Fixtures));
    }

    /// <summary>An answer that state shared between calls spoils when many threads extract at once fails.</summary>
    [Fact]
    public void Concurrent_Fails_OnAnAnswerSharedStateSpoils()
    {
        var inFlight = 0;

        Red(() => CodeGraphContract.Concurrent(
            new Toy
            {
                Answer = input =>
                {
                    var crowded = Interlocked.Increment(ref inFlight) > 1;
                    Thread.Sleep(5);
                    crowded |= Volatile.Read(ref inFlight) > 1;
                    Interlocked.Decrement(ref inFlight);
                    return crowded ? FileExtract.Empty : Toy.Read(input);
                },
            },
            Fixtures));
    }

    /// <summary>A digest set by the implementation, a span outside the file and a node the fixture names that is missing each fail.</summary>
    [Fact]
    public void Sound_Fails_OnADigestItSets_ASpanOutsideTheFile_OrANamedNodeMissing()
    {
        Red(() => CodeGraphContract.Sound(new Toy { Answer = input => new([.. Toy.Read(input).Nodes.Select(node => node with { Digest = new string('0', 64) })]) }, Fixtures), "which is the core's to set");
        Red(() => CodeGraphContract.Sound(new Toy { Answer = input => new([.. Toy.Read(input).Nodes.Select(node => node with { End = 9 })]) }, Fixtures));
        Red(() => CodeGraphContract.Sound(new Toy(), [B with { Nodes = [new("toy:C", CodeNodeKinds.Type)] }]));
        Red(() => CodeGraphContract.Sound(new Toy(), [B with { Nodes = [new("toy:B", CodeNodeKinds.Member)] }]));
        Red(() => CodeGraphContract.Sound(new Toy(), [B with { Nodes = [new("toy:B", CodeNodeKinds.Type, (1, 2))] }]));
    }

    /// <summary>An exception on an empty file, and an unsound extract of a cut of a fixture, each fail.</summary>
    [Fact]
    public void Tolerant_Fails_OnAnExceptionOrAnUnsoundExtract_OfACutOrAnEmptyFile()
    {
        Assert.IsType<InvalidOperationException>(Record.Exception(() => CodeGraphContract.Tolerant(new Toy { Answer = input => input.Text.Length == 0 ? throw new InvalidOperationException("empty") : Toy.Read(input) }, Fixtures)));
        Red(() => CodeGraphContract.Tolerant(new Toy { Answer = input => input.Text.EndsWith('\n') ? Toy.Read(input) : new([new("toy:X", CodeNodeKinds.Type, "X", "X", 1, 2, "x")]) }, Fixtures));
    }

    /// <summary>
    /// A fixture the registry gives another implementation, a content test that answers twice unalike - though only its
    /// first answer differs - or throws on some text, and two content tests of one shared claim that both say yes on a
    /// fixture of either each fail; a name and an extension alike in their letters are no shared claim.
    /// </summary>
    [Fact]
    public void Claimed_Fails_WhereAFixtureIsAnothers_ATestIsImpure_OrTwoTestsBothSayYes()
    {
        var toy = new Toy();
        var named = new Toy { Language = "named", Claims = new([new("a.toy")], []) };

        Red(() => CodeGraphContract.Claimed(toy, Fixtures, Registry(toy, named), new Dictionary<string, IReadOnlyList<CodeGraphFixture>>()));

        var flips = 0;
        var flipping = new Toy { Claims = new([], [new(".toy", _ => ++flips % 2 == 1)]) };

        Red(() => CodeGraphContract.Claimed(flipping, [B], Registry(flipping), new Dictionary<string, IReadOnlyList<CodeGraphFixture>>()));

        var asked = 0;
        var firstOnly = new Toy { Claims = new([], [new(".toy", _ => ++asked == 1)]) };

        Red(() => CodeGraphContract.Claimed(firstOnly, [B], Registry(firstOnly), new Dictionary<string, IReadOnlyList<CodeGraphFixture>>()), "then otherwise of the same text");

        var throwing = new Toy { Claims = new([], [new(".toy", probe => probe.Length == 0 ? throw new InvalidOperationException("no text") : probe.Contains("class", StringComparison.Ordinal))]) };

        Red(() => CodeGraphContract.Claimed(throwing, [B], Registry(throwing), new Dictionary<string, IReadOnlyList<CodeGraphFixture>>()), "threw on a text: no text");

        var dotted = new Toy { Language = "dotted", Claims = new([new(".toy")], []) };
        CodeGraphContract.Claimed(toy, Fixtures, Registry(toy, dotted), new Dictionary<string, IReadOnlyList<CodeGraphFixture>> { ["dotted"] = [new(".toy", "class D\n")] });

        var mine = new Toy { Claims = new([], [new(".toy", probe => probe.Contains("class", StringComparison.Ordinal))]) };
        var theirs = new Toy { Language = "other", Claims = new([], [new(".toy", probe => probe.Contains("other", StringComparison.Ordinal))]) };
        var registry = Registry(mine, theirs);

        CodeGraphContract.Claimed(mine, Fixtures, registry, new Dictionary<string, IReadOnlyList<CodeGraphFixture>> { ["other"] = [new("c.toy", "other")] });
        Red(
            () => CodeGraphContract.Claimed(mine, Fixtures, registry, new Dictionary<string, IReadOnlyList<CodeGraphFixture>> { ["other"] = [new("c.toy", "other class")] }),
            "'toy' and 'other' both say c.toy is theirs");
    }

    /// <summary>
    /// An extract not stored for the version fails unless storing is asked, naming the file; one stored unlike the answer
    /// fails, one checked out with other line endings does not; and one stored for no fixture fails.
    /// </summary>
    [Fact]
    public void Stored_Fails_WhereNoneIsStored_OrTheStoredDiffers()
    {
        using var stored = new TempDirectory();
        var toy = new Toy();
        var file = Path.Combine(stored.Path, "toy", "v1", "src_b.toy.json");

        Red(() => CodeGraphContract.Stored(toy, [B], stored.Path, store: false), file);
        Assert.False(File.Exists(file));

        CodeGraphContract.Stored(toy, [B], stored.Path, store: true);
        Assert.True(File.Exists(file));

        Red(() => CodeGraphContract.Stored(new Toy { Answer = input => Toy.Read(input) with { Generated = "now" } }, [B], stored.Path, store: true), "what it answers changed");
        Red(() => CodeGraphContract.Stored(new Toy { Version = 2 }, [B], stored.Path, store: false), Path.Combine(stored.Path, "toy", "v2", "src_b.toy.json"));

        File.WriteAllText(file, File.ReadAllText(file).Replace("\n", "\r\n", StringComparison.Ordinal));
        CodeGraphContract.Stored(toy, [B], stored.Path, store: false);

        File.WriteAllText(Path.Combine(stored.Path, "toy", "v1", "src_gone.toy.json"), "{}\n");
        Red(() => CodeGraphContract.Stored(toy, [B], stored.Path, store: false), "src_gone.toy.json");
    }

    /// <summary>An edge a fixture names that linking does not give - another target, line or confidence - fails.</summary>
    [Fact]
    public void Linked_Fails_OnAnEdgeLinkingDoesNotGive()
    {
        var toy = new Toy();

        Red(() => CodeGraphContract.Linked(toy, [A with { Edges = [A.Edges[0] with { To = "toy:C" }] }, B]));
        Red(() => CodeGraphContract.Linked(toy, [A with { Edges = [A.Edges[0] with { Line = 1 }] }, B]));
        Red(() => CodeGraphContract.Linked(toy, [A with { Edges = [A.Edges[0] with { Confidence = CodeConfidences.Exact }] }, B]));
    }

    private static CodeGraphExtractors Registry(params ICodeGraphExtractor[] extractors) => new([.. extractors, new ListedExtractor()]);

    /// <summary>Asserts <paramref name="check"/> fails as an assertion, naming <paramref name="why"/> where given.</summary>
    private static void Red(Action check, string? why = null)
    {
        var failure = Assert.IsAssignableFrom<XunitException>(Record.Exception(check));

        if (why is not null)
        {
            Assert.Contains(why, failure.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>A small implementation: <c>class X</c> declares a type, <c>call Y</c> under it calls one; each answer can be replaced.</summary>
    private sealed class Toy : ICodeGraphExtractor
    {
        public string Language { get; init; } = "toy";

        public int Version { get; init; } = 1;

        public CodeGraphClaims Claims { get; init; } = CodeGraphClaims.OfExtensions(".toy");

        public bool Outlines => false;

        public CodeGraphExtractorHelp Help { get; } = new("toy files", []);

        public Func<CodeFileInput, FileExtract>? Answer { get; init; }

        public static FileExtract Read(CodeFileInput input)
        {
            var lines = input.Lines;
            var nodes = new List<ExtractNode>();
            var references = new List<ExtractReference>();

            for (var at = 0; at < lines.Count; at++)
            {
                if (Named(lines[at], "class ") is { } type)
                {
                    nodes.Add(new ExtractNode($"toy:{type}", CodeNodeKinds.Type, type, type, at + 1, at + 1, lines[at]));
                }
                else if (Named(lines[at], "call ") is { } called && nodes.Count > 0)
                {
                    references.Add(new ExtractReference(nodes[^1].Id, CodeEdgeKinds.Calls, called, at + 1));
                }
            }

            return new FileExtract(nodes) { References = references };
        }

        public FileExtract Extract(CodeFileInput input) => (Answer ?? Read)(input);

        private static string? Named(string line, string keyword)
            => line.StartsWith(keyword, StringComparison.Ordinal) && line[keyword.Length..] is { Length: > 0 } name && name.All(char.IsAsciiLetter) ? name : null;
    }
}
