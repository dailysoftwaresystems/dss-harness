using System.Globalization;
using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;
using DssHarness.Core.Codebase.Languages.Scanning;

namespace DssHarness.Tests;

/// <summary>
/// The close-index the scans of one text share (<see cref="ScanCloses"/>): the scan of an interpolation's code, given
/// the index the scans before it filled, passes over each literal and element that code holds at once and reads what
/// was found of it - its token, parts and code - so each character is read by the text's first scan and by the scan of
/// its own interpolation's code alone, however deep they nest, and each scan reads what it would read without the index.
/// Each test scans as a reader does: the text, then each interpolation's code, the scan of each given the index of the
/// scan whose literal holds it.
/// </summary>
public sealed class ScanClosesTests
{
    /// <summary>How deep the literals of <see cref="NestedInterpolationsAThousandDeep_AreScannedOnce"/> nest.</summary>
    private const int Deep = 1_000;

    /// <summary>
    /// A literal nested a thousand deep - each level's interpolation a call of the level below it - in JavaScript, PHP
    /// and Dart: the text's first scan reads every character once, and the scan of each level's code reads only its own
    /// three or four characters, passing over the literal it holds at once, so the characters read are the text's and
    /// those alone. Each scan given the index reads what the same scan reads without it - its tokens, its literals'
    /// parts and values, and the code each holds - so the index changes nothing but what is read again. Red where a scan
    /// reads again the literals the scan before it read, a thousand levels a quarter of a million times over.
    /// </summary>
    [Fact]
    public void NestedInterpolationsAThousandDeep_AreScannedOnce()
    {
        foreach (var (name, text, own, first, inner) in Languages(Deep))
        {
            var scan = first(text);
            var (levels, alike) = Scanned(scan, inner, compared: true);

            // The first scan reads the text whole; each level's scan its own characters around the literal it holds.
            Assert.True(alike, $"a scan of {name} given the index read what it does not read without it");
            Assert.Equal((Deep, text.Length + (long)own * Deep), (levels, scan.Closes!.Read));
        }
    }

    /// <summary>
    /// JSX elements nested ten thousand deep, each in the braces of the one around it, are read whole - the outermost
    /// one token, the statement's end after it read - by a lexer whose elements and their code are frames of one stack,
    /// in a child given a small stack: a lexer that read an element's code by a call into itself would overflow it, and
    /// end the child, and one that stopped at a depth would read the outermost element to the text's end. Each level's
    /// code, scanned in its turn, passes over the element it holds at once.
    /// </summary>
    [Fact]
    public async Task JsxTenThousandElementsDeep_IsRead()
        => Assert.Equal(new ChildRunAnswer(0, "x|=|90001|; 10000 90007", ""), await ChildRun.MethodAsync(typeof(ScanClosesTests), nameof(JsxTenThousandDeep), TestContext.Current.CancellationToken));

    /// <summary>The child of <see cref="JsxTenThousandElementsDeep_IsRead"/>: the tokens, the levels scanned and the characters read.</summary>
    internal static string JsxTenThousandDeep() => SmallStack.Run(() =>
    {
        var text = $"x = {string.Concat(Enumerable.Repeat("<a>{", 10_000))}y{string.Concat(Enumerable.Repeat("}</a>", 10_000))};";
        var scan = JsLexer.Scan(text, markup: true);
        var tokens = string.Join('|', Enumerable.Range(0, scan.Scan.Count).Select(at => scan.Scan[at].Kind == ScanTokenKind.Text ? scan.Scan[at].Length.ToString(CultureInfo.InvariantCulture) : scan.Scan.Text(at)));
        var (levels, _) = Scanned(scan, (start, end, closes) => JsLexer.Scan(text, start, end, scan.Lines, markup: true, closes), compared: false);

        return string.Create(CultureInfo.InvariantCulture, $"{tokens} {levels} {scan.Closes!.Read}");
    });

    /// <summary>
    /// A literal nested ten thousand deep, in JavaScript, PHP and Dart, is read whole and each level's code scanned in its
    /// turn, in a child given a small stack: neither the lexers nor the scans of their code call into themselves, and the
    /// characters read are the text's and each level's own.
    /// </summary>
    [Fact]
    public async Task ALiteralNestedTenThousandDeep_IsLexedAndScannedOnce()
    {
        var expected = string.Join(' ', Languages(10_000).Select(language => string.Create(CultureInfo.InvariantCulture, $"{language.Name}:10000:{language.Text.Length + (long)language.Own * 10_000}")));

        Assert.Equal(new ChildRunAnswer(0, expected, ""), await ChildRun.MethodAsync(typeof(ScanClosesTests), nameof(TenThousandDeep), TestContext.Current.CancellationToken));
    }

    /// <summary>The child of <see cref="ALiteralNestedTenThousandDeep_IsLexedAndScannedOnce"/>: each language's levels scanned and characters read.</summary>
    internal static string TenThousandDeep() => SmallStack.Run(() => string.Join(' ', Languages(10_000).Select(language =>
    {
        var scan = language.First(language.Text);
        var (levels, _) = Scanned(scan, language.Inner, compared: false);

        return string.Create(CultureInfo.InvariantCulture, $"{language.Name}:{levels}:{scan.Closes!.Read}");
    })));

    /// <summary>
    /// Each language's text of a literal nested <paramref name="deep"/> levels - each level's interpolation the call
    /// <c>f(...)</c> of the level below - with how many characters of its own each level's code holds, and how the text and
    /// a part of it are scanned.
    /// </summary>
    private static IEnumerable<(string Name, string Text, int Own, Func<string, InterpolatedText> First, Func<string, InterpolatedText, Func<int, int, ScanCloses?, InterpolatedText>> Inner)> Languages(int deep)
    {
        yield return ("javascript", $"x = {Nested(deep, "`v`", "`a${f(", ")}b`")};", 3, text => JsLexer.Scan(text, markup: false), (text, first) => (start, end, closes) => JsLexer.Scan(text, start, end, first.Lines, markup: false, closes));
        yield return ("php", $"<?php $x = {Nested(deep, "\"v\"", "\"a{$f(", ")}b\"")};", 4, text => PhpLexer.Scan(text), (text, first) => (start, end, closes) => PhpLexer.Scan(text, start, end, first.Lines, closes));
        yield return ("dart", $"var x = {Nested(deep, "'v'", "'a${f(", ")}b'")};", 3, text => DartLexer.Scan(text), (text, first) => (start, end, closes) => DartLexer.Scan(text, start, end, first.Lines, closes));
    }

    /// <summary>The literal <paramref name="innermost"/> wrapped <paramref name="deep"/> times in <paramref name="open"/> and <paramref name="close"/>.</summary>
    private static string Nested(int deep, string innermost, string open, string close)
        => string.Concat(Enumerable.Repeat(open, deep)) + innermost + string.Concat(Enumerable.Repeat(close, deep));

    /// <summary>The scans a reader makes of <paramref name="first"/>'s literals' code, and of theirs in turn, each part scanned as <paramref name="inner"/> scans one of the text.</summary>
    private static (int Levels, bool Alike) Scanned(InterpolatedText first, Func<string, InterpolatedText, Func<int, int, ScanCloses?, InterpolatedText>> inner, bool compared)
        => Scanned(first, inner(first.Scan.Source, first), compared);

    /// <summary>
    /// Scans each run of code <paramref name="first"/>'s literals and elements hold, and each those scans' literals hold in
    /// turn - on a queue, as a reader reads them - each given the index of the scan before it; answers how many runs were
    /// scanned and, where <paramref name="compared"/>, whether each scan read what the same scan reads without the index.
    /// </summary>
    private static (int Levels, bool Alike) Scanned(InterpolatedText first, Func<int, int, ScanCloses?, InterpolatedText> scan, bool compared)
    {
        var pending = new Queue<InterpolatedText>([first]);
        var levels = 0;
        var alike = true;

        while (pending.TryDequeue(out var text))
        {
            foreach (var (start, end) in text.Interpolations.Values.SelectMany(spans => spans))
            {
                var inner = scan(start, end, text.Closes);
                levels++;
                alike = alike && (!compared || Read(inner) == Read(scan(start, end, null)));
                pending.Enqueue(inner);
            }
        }

        return (levels, alike);
    }

    /// <summary>What a scan read, written whole: each token, each literal's parts and value, the code each holds and the tags each element opens.</summary>
    private static string Read(InterpolatedText text)
    {
        var scan = text.Scan;
        var read = new List<string>();

        for (var at = 0; at < scan.Count; at++)
        {
            var token = scan[at];
            read.Add(string.Create(CultureInfo.InvariantCulture, $"{token} [{string.Join(',', scan.LiteralParts(token))}] {scan.Value(at) ?? "-"}"));
        }

        foreach (var (key, spans) in text.Interpolations.OrderBy(pair => pair.Key))
        {
            read.Add(string.Create(CultureInfo.InvariantCulture, $"code {key}: {string.Join(',', spans)}"));
        }

        foreach (var (key, tags) in (text.Elements ?? new Dictionary<int, List<(int Start, int End)>>()).OrderBy(pair => pair.Key))
        {
            read.Add(string.Create(CultureInfo.InvariantCulture, $"tags {key}: {string.Join(',', tags)}"));
        }

        return string.Join('\n', read);
    }
}
