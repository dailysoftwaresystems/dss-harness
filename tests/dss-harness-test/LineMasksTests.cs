using DssHarness.Core.Execution;
using DssHarness.Core.Runners;

namespace DssHarness.Tests;

/// <summary>What a mask says of a text beside masking it: a secret behind an address's escapes among it.</summary>
public sealed class LineMasksTests
{
    /// <summary>
    /// A secret escaped once more for each address that carries it - or written as HTML's character references, as a page's
    /// reader reads them: a name of HTML's own, a number with no ';' - is held,
    /// and the text masked at the reading that undoes every escape of a secret it holds, one escaped less deep beside it
    /// masked too; a text whose secrets are plain keeps its form, and one that holds none is itself.
    /// </summary>
    [Fact]
    public void ASecretBehindEscapes_IsHeld_AndMaskedAtItsFullestReading()
    {
        var mask = new Secrets("pl/ain1", "deep/2");
        var text = "https://a.test/?k=" + Uri.EscapeDataString("pl/ain1") + "&to=" + Uri.EscapeDataString("https://b.test/?k=" + Uri.EscapeDataString("deep/2"));

        Assert.True(mask.HoldsUnescaped(text));
        Assert.Equal("https://a.test/?k=***&to=https://b.test/?k=***", mask.RedactUnescaped(text));
        Assert.True(mask.HoldsUnescaped("<p>key=deep&#x2F;2</p>"));
        Assert.Equal("<p>key=***</p>", mask.RedactUnescaped("<p>key=deep&#x2F;2</p>"));
        Assert.Equal("""{ "q": "a%22b", "key": "***" }""", mask.RedactUnescaped("""{ "q": "a%22b", "key": "deep/2" }"""));
        Assert.True(mask.HoldsUnescaped("deep&sol;2"));
        Assert.True(mask.HoldsUnescaped("deep&#47;2"));
        Assert.True(mask.HoldsUnescaped("pl&#47ain1"));
        Assert.True(mask.HoldsUnescaped("pl&sol;ain&#x31"));
        Assert.Equal("a &notit; b", mask.RedactUnescaped("a &notit; b"));
        Assert.True(new Secrets("x<y").HoldsUnescaped("x&lty"));
        Assert.True(new Secrets("x<y").HoldsUnescaped("x&#x10000003C;y"));
        Assert.True(new Secrets("x<y").HoldsUnescaped("x&#4294967356;y"));
        Assert.False(mask.HoldsUnescaped("https://a.test/?q=a%252Fb"));
        Assert.Equal("https://a.test/?q=a%252Fb", mask.RedactUnescaped("https://a.test/?q=a%252Fb"));
    }

    /// <summary>
    /// A text's escapes are undone at most so many times: a secret escaped that deep is held, and a text that unescapes into
    /// itself a hundred thousand times costs no more passes.
    /// </summary>
    [Fact]
    public void ATextsEscapes_AreUndoneSoDeepAndNoDeeper()
    {
        var mask = new Secrets("deep/2");
        var deep = Enumerable.Range(0, LineMasks.DeepestReading).Aggregate("deep/2", (escaped, _) => Uri.EscapeDataString(escaped));
        var endless = "%" + string.Concat(Enumerable.Repeat("25", 100_000)) + "41";

        Assert.True(mask.HoldsUnescaped(deep));
        Assert.False(mask.HoldsUnescaped(Uri.EscapeDataString(deep)));
        Assert.False(mask.HoldsUnescaped(endless));
        Assert.Equal(endless, mask.RedactUnescaped(endless));
    }

    /// <summary>
    /// The start of a text cut short tells no part of a secret its end could begin, read as it stands or behind escapes the
    /// cut parted - a reference cut after its <c>&amp;#x</c>, a percent-escape after its <c>%2</c>, escaped twice - and
    /// masks one it holds whole; a start whose end begins none is masked as any text is, an unfinished escape kept.
    /// </summary>
    [Fact]
    public void TheStartOfATextCutShort_TellsNoPartOfASecret()
    {
        var mask = new ActionValues(new Dictionary<string, string>(StringComparer.Ordinal), new Dictionary<string, string>(StringComparer.Ordinal) { ["K"] = "abcdef+ghijkl" });

        Assert.Equal("said ", mask.RedactHead("said abcdef"));
        Assert.Equal("said ", mask.RedactHead("said abcdef&#x"));
        Assert.Equal("said ", mask.RedactHead("said abcdef&amp;#x2"));
        Assert.Equal("said ", mask.RedactHead("said abcdef%252"));
        Assert.Equal("said *** and ", mask.RedactHead("said abcdef&#x2B;ghijkl and abc"));
        Assert.Equal("said R&D", mask.RedactHead("said R&D"));
        Assert.Equal("said 100%", mask.RedactHead("said 100%"));
        Assert.Equal("said & ", mask.RedactHead("said & abc"));
        Assert.Equal("said ", mask.RedactHead("said abcdef&#x2B;ghi&#x6"));
        Assert.Equal("said ", mask.RedactHead("said abcdef&#x" + new string('0', 40)));
        Assert.Equal("said x&", mask.RedactHead("said x&abcdef"));
        Assert.Equal("said ", mask.RedactHead("said abcdef&plu"));
        Assert.Equal("said ", mask.RedactHead("said abcdef%26%23x2"));

        // A secret that holds an escape of its own is cut as it stands, before a reading undoes it.
        var escaped = new ActionValues(new Dictionary<string, string>(StringComparer.Ordinal), new Dictionary<string, string>(StringComparer.Ordinal) { ["K"] = "ab%41cd" });
        Assert.Equal("said ", escaped.RedactHead("said ab%41c&#x"));

        // Read deepest first: its own start may hide behind an escape where another secret's shows plain.
        var two = new ActionValues(new Dictionary<string, string>(StringComparer.Ordinal), new Dictionary<string, string>(StringComparer.Ordinal) { ["K"] = "abcdef+ghijkl", ["L"] = "bzzz" });
        Assert.Equal("said ", two.RedactHead("said %61b"));
    }

    /// <summary>A mask of the secrets it is given.</summary>
    private sealed class Secrets(params string[] secrets) : ILineMask
    {
        public string Redact(string? text) => secrets.Aggregate(text ?? string.Empty, (masked, secret) => masked.Replace(secret, "***", StringComparison.Ordinal));

        public int Cut(string text) => text.Length;
    }
}
