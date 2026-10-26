using System.Globalization;
using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;
using DssHarness.Core.Codebase.Languages.Scanning;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;

namespace DssHarness.Tests;

/// <summary>
/// The structural scanner's core, which knows no language: brackets matched over a conditional's branches, the comments
/// above a declaration, a header spelled on one line, a call's arity and type arguments, the extract's ids and parts,
/// and the walk's progress. Read through C's lexer, whose tokens it is given.
/// </summary>
public sealed class StructuralScannerTests
{
    /// <summary>
    /// A bracket each branch of a conditional opens, left open after it, is one bracket: the one after the conditional
    /// closes every branch's, and its own match is the first branch's; what a branch opens and closes in it is its own.
    /// What the joined group holds begins past the last of its openers, so the head another branch writes is none of it;
    /// what any other group holds, past its own opener.
    /// </summary>
    [Fact]
    public void ABracketEachBranchLeavesOpen_IsClosedByTheOneAfterTheConditional()
    {
        var scan = CFamilyLexer.Scan("""
            #if WIDE
            void f(int a, int b) {
            #else
            void f(int a) {
            #endif
                x();
            }
            """, cpp: false);

        var (first, second, close) = (At(scan, "{", 1), At(scan, "{", 2), At(scan, "}", 1));

        Assert.Equal(close, scan.Match(first));
        Assert.Equal(close, scan.Match(second));
        Assert.Equal(first, scan.Match(close));
        Assert.Equal(At(scan, ")", 1), scan.Match(At(scan, "(", 1)));
        Assert.Equal(At(scan, ")", 2), scan.Match(At(scan, "(", 2)));
        Assert.Equal((second + 1, second + 1), (scan.Within(first), scan.Within(second)));
        Assert.Equal(At(scan, "(", 3) + 1, scan.Within(At(scan, "(", 3)));
    }

    /// <summary>
    /// A run of closers nothing opened is matched in time linear in its length - each passed over at once, where nothing
    /// it could close is open, with no walk down what is open - and what follows still matches.
    /// </summary>
    [Fact]
    public void ARunOfClosersNothingOpened_IsMatchedInLinearTime()
    {
        const int Run = 50_000;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var scan = CFamilyLexer.Scan(new string('[', Run) + new string(')', Run) + new string('(', Run) + new string('}', Run) + "{}", cpp: false);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
        Assert.Equal([-1, -1], new[] { Run, 3 * Run }.Select(scan.Match));
        Assert.Equal(4 * Run + 1, scan.Match(4 * Run));
    }

    /// <summary>
    /// A run of non-null assertions before a list of type arguments is stepped back over in a loop, so no hostile file's
    /// run of them reaches past the reader's stack - an overflow no catch stops. It is read in a child on a small stack:
    /// a reading that recursed once per <c>!</c> would end that child, never this run of the suite.
    /// </summary>
    [Fact]
    public async Task ARunOfNonNullAssertions_IsSteppedOverInALoop()
    {
        var result = await new ProcessRunner(new HostPlatform(), FilePermissionsFactory.Create())
            .RunAsync(TestHost.ChildRequest(AssertionsChild, "100000"), TestContext.Current.CancellationToken);

        Assert.Equal((0, "1\n"), (result.ExitCode, result.StandardOutput.ReplaceLineEndings("\n")));
    }

    /// <summary>The child mode of <see cref="AssertionsOnASmallStack"/>.</summary>
    internal const string AssertionsChild = "read-assertions-on-a-small-stack";

    /// <summary>
    /// The child that reads the call <c>f(</c>, as many <c>!</c> as its argument says, then <c>&lt;A&gt;(0));</c> on a
    /// thread of a 256 KB stack, and says the call's arity.
    /// </summary>
    internal static int AssertionsOnASmallStack(TextWriter output, string[] arguments)
    {
        var scan = CFamilyLexer.Scan($"f({new string('!', int.Parse(arguments[0], CultureInfo.InvariantCulture))}<A>(0));", cpp: true);
        int? arity = null;
        var reader = new Thread(() => arity = ScanCalls.At(scan, 0, new ScanCallRules(new HashSet<string>(StringComparer.Ordinal), CppLike))?.Arity, maxStackSize: 256 * 1024);

        reader.Start();
        reader.Join();
        output.Write($"{arity?.ToString(CultureInfo.InvariantCulture)}\n");
        return 0;
    }

    /// <summary>Branches that leave different brackets open at a depth are not joined: the bracket after closes the first branch's alone.</summary>
    [Fact]
    public void BracketsOfDifferentKinds_AreNotJoined()
    {
        var scan = CFamilyLexer.Scan("""
            #if A
            int x[] = {
            #else
            int x[] = (
            #endif
            1 };
            """, cpp: false);

        Assert.Equal(At(scan, "}", 1), scan.Match(At(scan, "{", 1)));
        Assert.Equal(-1, scan.Match(At(scan, "(", 1)));
    }

    /// <summary>
    /// A parenthesis a branch leaves open is kept only where every branch leaves one at that depth - a conditional with no
    /// alternative has an empty one - and a brace the first branch leaves open is kept though another leaves none; every
    /// other bracket left open is stranded: it matches nothing and holds nothing.
    /// </summary>
    [Fact]
    public void ABracketOnlySomeBranchesLeaveOpen_IsStranded_ButTheFirstBranchsBrace()
    {
        var scan = CFamilyLexer.Scan("""
            #if A
            f(1,
            #endif
            #if B
            g(1,
            #else
            g(2,
            #endif
            3);
            #ifdef __cplusplus
            extern "C" {
            #endif
            #if C
            #else
            h(
            #endif
            }
            """, cpp: false);

        Assert.Equal([At(scan, "(", 1), At(scan, "(", 4)], scan.Stranded);
        Assert.Equal(-1, scan.Match(At(scan, "(", 1)));
        Assert.Equal(At(scan, "(", 1) + 1, scan.Past(At(scan, "(", 1)));
        Assert.Equal((At(scan, "(", 1) + 1, At(scan, "(", 1) + 1), scan.Bounds(At(scan, "(", 1), scan.Count));
        Assert.Equal(At(scan, ")", 1), scan.Match(At(scan, "(", 2)));
        Assert.Equal(At(scan, ")", 1), scan.Match(At(scan, "(", 3)));
        Assert.Equal(At(scan, "}", 1), scan.Match(At(scan, "{", 1)));
    }

    /// <summary>A statement ends past its semicolon, at the brace that ends its block, past a body it ends with - and, at a stranded bracket, at the end of the branch it lies in, the rest of that branch with it.</summary>
    [Fact]
    public void AStatement_EndsPastItsSemicolon_OrWithTheBranchAStrandedBracketLiesIn()
    {
        var scan = CFamilyLexer.Scan("a (b; c); { d } e } f;\n#if 0\nnot ( code\n#else\nalso\n#endif\ng;\n", cpp: false);
        var stranded = Assert.Single(scan.Stranded);

        Assert.Equal(At(scan, ";", 2) + 1, scan.StatementEnd(0, scan.Count));
        Assert.Equal(At(scan, "}", 1) + 1, scan.StatementEnd(At(scan, "{", 1), scan.Count));
        Assert.Equal(At(scan, "}", 2), scan.StatementEnd(At(scan, "e", 1), scan.Count));
        Assert.Equal(scan.BranchEnd(stranded), scan.StatementEnd(At(scan, "not", 1), scan.Count));
        Assert.True(scan[scan.BranchEnd(stranded)] is { Kind: ScanTokenKind.Directive, Branch: ScanBranch.Alternative });
        Assert.Equal(scan.Count, scan.BranchEnd(0));
    }

    /// <summary>
    /// A token at the top of a span is the first so written outside its groups and a type's arguments - where the
    /// language writes them - and none another's: a default's <c>=</c> past <c>T&lt;N = 2&gt;</c>, and the next past one passed.
    /// </summary>
    [Fact]
    public void ATokenAtTheTop_IsOutsideGroupsAndATypesArguments_AndNoOthers()
    {
        var scan = CFamilyLexer.Scan("T<N = 2> a = (b = c), d = e;\n", cpp: true);
        var types = new ScanTypeArguments(new HashSet<string>(StringComparer.Ordinal) { ",", "=" }, new HashSet<string>(StringComparer.Ordinal) { "(", "::", "{" });

        Assert.Equal(At(scan, "=", 2), ScanCalls.TopLevel(scan, 0, scan.Count, "=", types));
        Assert.Equal(At(scan, "=", 1), ScanCalls.TopLevel(scan, 0, scan.Count, "=", null));
        Assert.Equal(At(scan, "=", 4), ScanCalls.TopLevel(scan, 0, scan.Count, "=", types, at => at == At(scan, "=", 2)));
        Assert.Equal(At(scan, ";", 1), ScanCalls.TopLevel(scan, 0, scan.Count, ";", types));
        Assert.Equal(scan.Count, ScanCalls.TopLevel(scan, 0, scan.Count, "f", types));
    }

    /// <summary>A stray parenthesis never ends a body: a closing one matches nothing past a brace, and an opening one left open is passed over by the brace that closes the body.</summary>
    [Fact]
    public void AStrayParenthesis_NeverEndsABody()
    {
        var scan = CFamilyLexer.Scan("void f() { ) g(; }\n", cpp: false);

        Assert.Equal(At(scan, "}", 1), scan.Match(At(scan, "{", 1)));
        Assert.Equal(-1, scan.Match(At(scan, ")", 2)));
        Assert.Equal(-1, scan.Match(At(scan, "(", 2)));
        Assert.Equal(scan.Count, scan.Past(At(scan, "(", 2)));

        // A closing parenthesis in a block never closes one opened outside it, which would leave the block unclosed.
        var outside = CFamilyLexer.Scan("m( { ) } )", cpp: false);

        Assert.Equal(At(outside, "}", 1), outside.Match(At(outside, "{", 1)));
        Assert.Equal(-1, outside.Match(At(outside, ")", 1)));
        Assert.Equal(At(outside, ")", 2), outside.Match(At(outside, "(", 1)));
    }

    /// <summary>The comments above a declaration are the run ending on its line or the one before, no blank line inside it, none trailing the code above.</summary>
    [Fact]
    public void TheCommentsAboveADeclaration_AreTheRunNextToIt()
    {
        var scan = CFamilyLexer.Scan("""
            int a; // a's
            // one
            /* two */

            // three
            /* four
               five */
            int b;
            int c; // c's
            int d;
            """, cpp: false);

        Assert.Equal(
            ["// three", "/* four\n   five */"],
            scan.CommentsAbove(At(scan, "int", 2)).Select(comment => scan.Source.Substring(comment.Start, comment.Length).ReplaceLineEndings("\n")));
        Assert.Empty(scan.CommentsAbove(At(scan, "int", 4)));
        Assert.Empty(scan.CommentsAbove(0));
    }

    /// <summary>A header is spelled on one line: each token as written, one space where the text has anything between two, directives left out.</summary>
    [Fact]
    public void AHeader_IsSpelledOnOneLine()
    {
        var scan = CFamilyLexer.Scan("void\n    f( int  a /* c */ ,\n#if A\n#endif\n char*b)\n", cpp: false);

        Assert.Equal("void f( int a , char*b)", scan.Spelled(0, scan.Count));
        Assert.Equal(string.Empty, scan.Spelled(3, 3));
    }

    /// <summary>A call's arity is one more than its commas at its own depth, none for nothing but directives between its parentheses; a keyword makes no call.</summary>
    [Theory]
    [InlineData("f();", 0)]
    [InlineData("f(/* none */);", 0)]
    [InlineData("f(\n#if A\n#endif\n);", 0)]
    [InlineData("f(\n#if A\n1\n#endif\n);", 1)]
    [InlineData("f((a, b), c[1, 2], {d, e});", 3)]
    [InlineData("f(g<int, long>(1), h<a, b>::c, k < l, m > n);", 4)]
    public void ACallsArity_IsItsOwnCommas(string text, int arity)
    {
        var scan = CFamilyLexer.Scan(text, cpp: true);

        Assert.Equal(arity, ScanCalls.At(scan, 0, new ScanCallRules(new HashSet<string>(StringComparer.Ordinal), CppLike))?.Arity);
        Assert.Null(ScanCalls.At(scan, 0, new ScanCallRules(new HashSet<string>(StringComparer.Ordinal) { "f" }, CppLike)));
    }

    /// <summary>A call's name may be followed by type arguments where the language writes them; where it does not, a <c>&lt;</c> after it is no call's.</summary>
    [Fact]
    public void ACallsTypeArguments_AreReadOnlyWhereTheLanguageWritesThem()
    {
        var scan = CFamilyLexer.Scan("make<std::vector<int>, 3>(x);", cpp: true);

        Assert.Equal(1, ScanCalls.At(scan, 0, new ScanCallRules(new HashSet<string>(), CppLike))?.Arity);
        Assert.Null(ScanCalls.At(scan, 0, new ScanCallRules(new HashSet<string>(), null)));
    }

    /// <summary>What may follow a type-argument list is the language's: a list a named constructor follows is a call's in a language that writes one so, and a comparison's in one that does not.</summary>
    [Fact]
    public void WhatFollowsATypeArgumentList_IsTheLanguages()
    {
        var scan = CFamilyLexer.Scan("f(Map<String, int>.from(m));", cpp: true);
        var dartLike = new ScanTypeArguments(new HashSet<string>(StringComparer.Ordinal) { ",", ".", "?" }, new HashSet<string>(StringComparer.Ordinal) { "(", "." });

        Assert.Equal(1, ScanCalls.At(scan, 0, new ScanCallRules(new HashSet<string>(), dartLike))?.Arity);
        Assert.Equal(2, ScanCalls.At(scan, 0, new ScanCallRules(new HashSet<string>(), CppLike))?.Arity);
    }

    /// <summary>A type-argument list ends past its <c>&gt;</c>; where what follows a <c>&lt;</c> is an expression's, or runs too long, there is none.</summary>
    [Theory]
    [InlineData("a<int, std::map<int, long>> b;", 13)]
    [InlineData("a<T*, const U&, 3, V...> b;", 14)]
    [InlineData("a < b; c > d;", -1)]
    [InlineData("a < (b; c > d;", -1)]
    [InlineData("a < b == c > d;", -1)]
    public void ATypeArgumentList_EndsPastItsCloser(string text, int past)
        => Assert.Equal(past, ScanCalls.TypeArgumentsEnd(CFamilyLexer.Scan(text, cpp: true), 1, CppLike));

    /// <summary>
    /// A list's start is found back from its closer where the list it closes is a type's arguments, and none where it is a
    /// comparison's or a statement or a brace comes first; an angle bracket's end is past its closer, groups passed over,
    /// or at the brace or the semicolon that cuts it short.
    /// </summary>
    [Fact]
    public void ATypeArgumentListsStart_IsFoundFromItsCloser_AndAnAnglesEndPastIt()
    {
        var scan = CFamilyLexer.Scan("a<b<c>, d(e, f)> x; g < h; i > j; k<l {m> n;", cpp: true);

        Assert.Equal(1, ScanCalls.TypeArgumentsStart(scan, At(scan, ">", 2), CppLike));
        Assert.Equal(3, ScanCalls.TypeArgumentsStart(scan, At(scan, ">", 1), CppLike));
        Assert.Equal(-1, ScanCalls.TypeArgumentsStart(scan, At(scan, ">", 3), CppLike));
        Assert.Equal(-1, ScanCalls.TypeArgumentsStart(scan, At(scan, ">", 4), CppLike));
        Assert.Equal(At(scan, ">", 2) + 1, scan.AngleEnd(1, scan.Count));
        Assert.Equal(At(scan, ";", 2), scan.AngleEnd(At(scan, "<", 3), scan.Count));
        Assert.Equal(At(scan, "{", 1), scan.AngleEnd(At(scan, "<", 4), scan.Count));
    }

    /// <summary>
    /// Dart's lexer keeps whole the punctuators Dart writes whole and every other one alone - two closers of type
    /// arguments two tokens - reads a number with its separators, its exponent's sign and a fraction that begins at its
    /// point, and a name with a dollar in it.
    /// </summary>
    [Fact]
    public void DartsLexer_CutsDartsTokens()
    {
        var scan = DartLexer.Scan("a?..b ...?c ??= d?.e .. f => g ?? h; List<List<int>> $x = 1_000 + 0xFF + 1e-5 + .5;").Scan;

        Assert.Equal(
            [
                "a", "?..", "b", "...?", "c", "??=", "d", "?.", "e", "..", "f", "=>", "g", "??", "h", ";", "List", "<", "List", "<", "int", ">", ">", "$x", "=",
                "1_000", "+", "0xFF", "+", "1e-5", "+", ".5", ";",
            ],
            Enumerable.Range(0, scan.Count).Select(at => scan.Text(at)));
    }

    /// <summary>
    /// Dart's lexer reads a number's point only where a digit follows it, a hexadecimal number with no exponent, and a
    /// string or a block comment the text ends in to its end.
    /// </summary>
    [Fact]
    public void DartsLexer_ReadsNumbersAndUnendedTexts()
    {
        var numbers = DartLexer.Scan("1.toString(); .5; 1e-3; 0xE-1; 2.5e+3;").Scan;
        var unended = DartLexer.Scan("a = 'abc").Scan;
        var comment = DartLexer.Scan("a /* b /* c */ d").Scan;

        Assert.Equal(
            ["1", ".", "toString", "(", ")", ";", ".5", ";", "1e-3", ";", "0xE", "-", "1", ";", "2.5e+3", ";"],
            Enumerable.Range(0, numbers.Count).Select(at => numbers.Text(at)));
        Assert.Equal(["a", "=", "'abc"], Enumerable.Range(0, unended.Count).Select(at => unended.Text(at)));
        Assert.Equal(["a"], Enumerable.Range(0, comment.Count).Select(at => comment.Text(at)));
        Assert.Equal(1, Assert.Single(comment.Comments).Line);
    }

    /// <summary>
    /// JavaScript's lexer keeps whole the punctuators JavaScript writes whole and every other one alone - two closers of
    /// type arguments two tokens, <c>?.</c> but before a digit, where it is a condition's - reads a private name with its
    /// <c>#</c>, a number with its separators and suffix, and a first line's <c>#!</c> as a comment.
    /// </summary>
    [Fact]
    public void JavaScriptsLexer_CutsJavaScriptsTokens()
    {
        var scan = JsLexer.Scan("#!/usr/bin/env node\na?.b ?? c ??= d === e !== f ... g => h ** i; Map<Set<T>> #x = 1_000n + 0xFFn + 1e-5 + .5; k ?.5 : 6", markup: false).Scan;

        Assert.Equal(
            [
                "a", "?.", "b", "??", "c", "??=", "d", "===", "e", "!==", "f", "...", "g", "=>", "h", "**", "i", ";", "Map", "<", "Set", "<", "T", ">", ">",
                "#x", "=", "1_000n", "+", "0xFFn", "+", "1e-5", "+", ".5", ";", "k", "?", ".5", ":", "6",
            ],
            Enumerable.Range(0, scan.Count).Select(at => scan.Text(at)));
        Assert.Equal(1, Assert.Single(scan.Comments).Line);
    }

    /// <summary>
    /// A slash after what ends an operand divides, and after anything else - an operator, a keyword an expression begins
    /// after, a statement's start - opens a regular expression, which runs past a slash in a class and an escaped one to
    /// its flags; a template keeps its code's braces, strings and templates and says where each interpolation is code.
    /// </summary>
    [Fact]
    public void JavaScriptsLexer_TellsARegularExpressionFromADivision_AndKeepsATemplatesCode()
    {
        var scan = JsLexer.Scan("a = b / c / d; e = /[/]+\\/x/gi.test(f); return /y/; g(`h ${i({ j: '}' })} ${`k ${l}`} m`)", markup: false);
        var texts = Enumerable.Range(0, scan.Scan.Count).Where(at => scan.Scan[at].Kind == ScanTokenKind.Text).Select(at => scan.Scan.Text(at)).ToList();
        var template = Enumerable.Range(0, scan.Scan.Count).Single(at => scan.Scan.Text(at).StartsWith('`'));

        Assert.Equal(["/[/]+\\/x/gi", "/y/", "`h ${i({ j: '}' })} ${`k ${l}`} m`"], texts);
        Assert.Equal(2, scan.Scan.Code(0, scan.Scan.Count).Count(at => scan.Scan.Is(at, "/")));
        Assert.Equal(
            ["i({ j: '}' })", "`k ${l}`"],
            scan.Interpolations[scan.Scan[template].Start].Select(span => scan.Scan.Source[span.Start..span.End]));
    }

    /// <summary>
    /// In markup an element - its tags, attributes, text and the elements it holds - is one token: each <c>{...}</c> it
    /// holds is code, and the name of each tag it opens is said; a <c>&lt;</c> after an operand compares, and a list of a
    /// generic arrow's type parameters (<c>&lt;T,&gt;</c>, <c>&lt;T extends U&gt;</c>) opens none. Out of markup, none is read.
    /// </summary>
    [Fact]
    public void JavaScriptsLexer_ReadsAnElementAsOneToken_ItsCodeAndTagsSaid()
    {
        const string Text = "x = a < b; y = <Card title={`t${n}`} on={() => go('>')}>Don't {list.map(i => <Item key={i} />)}<br/></Card>; z = <T,>(v: T) => v; w = <></>;";
        var scan = JsLexer.Scan(Text, markup: true);
        var element = Enumerable.Range(0, scan.Scan.Count).Single(at => scan.Scan.Text(at).StartsWith("<Card", StringComparison.Ordinal));
        var start = scan.Scan[element].Start;

        Assert.Equal("<Card title={`t${n}`} on={() => go('>')}>Don't {list.map(i => <Item key={i} />)}<br/></Card>", scan.Scan.Text(element));
        Assert.Equal(["`t${n}`", "() => go('>')", "list.map(i => <Item key={i} />)"], scan.Interpolations[start].Select(span => Text[span.Start..span.End]));
        Assert.Equal(["Card", "br"], scan.Elements![start].Select(span => Text[span.Start..span.End]));
        Assert.Contains(Enumerable.Range(0, scan.Scan.Count), at => scan.Scan.Text(at) == "<>" + "</>");
        Assert.Equal(2, Enumerable.Range(0, scan.Scan.Count).Count(at => scan.Scan.Is(at, "<")));
        Assert.Empty(JsLexer.Scan(Text, markup: false).Elements!);
    }

    /// <summary>
    /// A regular expression in a template's code is read whole, its quotes and braces none of the code's; a member's name
    /// and TypeScript's non-null assertion end an operand, so a slash after either divides; and in markup a generic
    /// function type's list - an arrow or a return type after its parameters - opens no element, an element's text in
    /// parentheses still one.
    /// </summary>
    [Fact]
    public void JavaScriptsLexer_ReadsATemplatesRegularExpression_AndTellsWhatEndsAnOperand()
    {
        var template = JsLexer.Scan("h = `<a title=\"${t.replace(/\"/g, '&quot;')}\">${s.replace(/}/g, '')}`; f()", markup: false).Scan;

        Assert.Equal(["h", "=", "`<a title=\"${t.replace(/\"/g, '&quot;')}\">${s.replace(/}/g, '')}`", ";", "f", "(", ")"], Enumerable.Range(0, template.Count).Select(at => template.Text(at)));

        var tick = JsLexer.Scan("h = `${a(/`/)} ${b()}`; f()", markup: false).Scan;

        Assert.Equal(["h", "=", "`${a(/`/)} ${b()}`", ";", "f", "(", ")"], Enumerable.Range(0, tick.Count).Select(at => tick.Text(at)));

        var divisions = JsLexer.Scan("r = total! / count; q = cfg.default / 2 / 3; z = a.in < b", markup: true).Scan;

        Assert.Equal(3, Enumerable.Range(0, divisions.Count).Count(at => divisions.Is(at, "/")));
        Assert.Single(Enumerable.Range(0, divisions.Count), at => divisions.Is(at, "<"));

        var generics = JsLexer.Scan("type R = <T>(item: T) => string; const id = <const T,>(x: T) => x; const e = <b>(note)</b>; const f = <small>(optional): </small>; type G = <T>(x: T) => void; const t = <Table>x</Table>;", markup: true);

        Assert.Equal(["<b>(note)</b>", "<small>(optional): </small>", "<Table>x</Table>"], Enumerable.Range(0, generics.Scan.Count).Where(at => generics.Scan.Text(at).StartsWith('<') && generics.Scan[at].Kind == ScanTokenKind.Text).Select(at => generics.Scan.Text(at)));
    }

    /// <summary>
    /// A string, a template, a regular expression and an element the text ends in are read to its end, and elements
    /// nested past the scanner's depth stop being read: never an exception, and every token where the text has it.
    /// </summary>
    [Fact]
    public void JavaScriptsLexer_ReadsUnendedTexts_AndElementsTooDeep_ToTheEnd()
    {
        foreach (var text in (string[])["a = 'abc", "a = `x ${b", "a = /re", "a = <div {b", "a = <div>", "a = <a>{" + string.Concat(Enumerable.Repeat("<a>{", 500))])
        {
            var scan = JsLexer.Scan(text, markup: true).Scan;

            Assert.Equal(["a", "="], Enumerable.Range(0, 2).Select(at => scan.Text(at)));
            Assert.Equal(text.Length, scan[scan.Count - 1].End);
        }
    }

    /// <summary>
    /// PHP's lexer reads code only between its open tags - <c>&lt;?php</c>, <c>&lt;?=</c> - and <c>?&gt;</c>, kept as a
    /// token, a line comment ending before it; a variable is one name, a name with namespaces' separators one, but the
    /// separator before a group's brace; and its punctuators of more than one character are whole.
    /// </summary>
    [Fact]
    public void PhpsLexer_CutsPhpsTokens_BetweenItsTags()
    {
        var scan = PhpLexer.Scan("<p>a()</p><?xml version=\"1.0\"?><?phpx b() <?php $a?->b <=> $c ?? $d ??= \\App\\Models\\User::class . $$e ... f => g ** 1_000 + 0x1F; # c\n// d ?>x()<?= h ?><? use App\\{A, B};").Scan;

        Assert.Equal(
            [
                "$a", "?->", "b", "<=>", "$c", "??", "$d", "??=", "\\App\\Models\\User", "::", "class", ".", "$$e", "...", "f", "=>", "g", "**", "1_000", "+",
                "0x1F", ";", "?>", "h", "?>", "use", "App", "\\", "{", "A", ",", "B", "}", ";",
            ],
            Enumerable.Range(0, scan.Count).Select(at => scan.Text(at)));
        Assert.Equal(2, scan.Comments.Count);
    }

    /// <summary>
    /// A string in single quotes, double quotes or backticks, a heredoc and a nowdoc are each one token; a double-quoted
    /// string's and a heredoc's interpolations - <c>{$...}</c> from its <c>$</c>, <c>${...}</c> after its brace, holding
    /// strings and braces of their own - are said as code, a single-quoted string's and a nowdoc's never; a string or a
    /// heredoc the text ends in is read to its end.
    /// </summary>
    [Fact]
    public void PhpsLexer_ReadsStringsHeredocsAndNowdocs_AndSaysTheirInterpolations()
    {
        const string Text = "<?php $a = 'x {$no}' . \"y {$b->c(\"}\")} ${d} {$o->{'p'}->q()} {$a['{$']} \\\" e\" . `f` . <<<EOT\n  g {$h->i()}\n  EOT . <<<'N'\n  {$j}\n  N;";
        var scan = PhpLexer.Scan(Text);
        var texts = Enumerable.Range(0, scan.Scan.Count).Where(at => scan.Scan[at].Kind == ScanTokenKind.Text).ToList();

        Assert.Equal(
            ["'x {$no}'", "\"y {$b->c(\"}\")} ${d} {$o->{'p'}->q()} {$a['{$']} \\\" e\"", "`f`", "<<<EOT\n  g {$h->i()}\n  EOT", "<<<'N'\n  {$j}\n  N"],
            texts.Select(at => scan.Scan.Text(at)));
        Assert.Equal(["$b->c(\"}\")", "d", "$o->{'p'}->q()", "$a['{$']"], scan.Interpolations[scan.Scan[texts[1]].Start].Select(span => Text[span.Start..span.End]));
        Assert.Equal(["$h->i()"], scan.Interpolations[scan.Scan[texts[3]].Start].Select(span => Text[span.Start..span.End]));
        Assert.False(scan.Interpolations.ContainsKey(scan.Scan[texts[0]].Start));
        Assert.False(scan.Interpolations.ContainsKey(scan.Scan[texts[4]].Start));
        Assert.Equal(";", scan.Scan.Text(scan.Scan.Count - 1));

        foreach (var unended in (string[])["<?php a = 'abc", "<?php a = \"x {$b", "<?php a = <<<EOT\nabc {$d"])
        {
            var cut = PhpLexer.Scan(unended).Scan;

            Assert.Equal(["a", "="], Enumerable.Range(0, 2).Select(at => cut.Text(at)));
            Assert.Equal(unended.Length, cut[cut.Count - 1].End);
        }

        var open = PhpLexer.Scan("<?php a = \"x {$b->c()");
        Assert.Equal(["$b->c()"], open.Interpolations.Values.Single().Select(span => "<?php a = \"x {$b->c()"[span.Start..span.End]));
    }

    /// <summary>
    /// A comment in an interpolation's code is read by its language's own rules and is none of the code's - a quote in
    /// <c>// don't</c> opens no string, a brace in one closes nothing - in Dart's, JavaScript's and PHP's alike, PHP's
    /// <c>#</c> among them: the literal ends where it does, its code is said whole, and what follows it is read.
    /// </summary>
    [Fact]
    public void ACommentInAnInterpolation_IsNoneOfItsCode_InEveryLanguage()
    {
        const string Dart = "xs.map((x) {\n  // don't } here\n  /* nor ' here */ return x;\n})";
        const string Js = "xs.map((x) => {\n  // don't } here\n  /* nor ' here */ return x;\n})";
        const string Php = "$xs->map(function ($x) {\n  // don't } here\n  # nor ' here\n  /* nor } here */ return $x;\n})";

        foreach (var (literal, code, scanned) in (IEnumerable<(string, string, Func<string, InterpolatedText>)>)
        [
            ($"'''${{{Dart}}}'''", Dart, text => DartLexer.Scan(text)),
            ($"`${{{Js}}}`", Js, text => JsLexer.Scan(text, markup: false)),
            ($"\"{{{Php}}}\"", Php, text => PhpLexer.Scan("<?php " + text)),
        ])
        {
            var text = $"a = {literal}; f();";
            var scan = scanned(text);
            var source = scan.Scan.Source;

            Assert.Equal(["a", "=", literal, ";", "f", "(", ")", ";"], Enumerable.Range(0, scan.Scan.Count).Select(at => scan.Scan.Text(at)));
            Assert.Equal([code], scan.Interpolations[scan.Scan[2].Start].Select(span => source[span.Start..span.End]));
        }
    }

    /// <summary>
    /// A call's arity counts the commas of a type's arguments a literal's list or a type test's holds nothing, and a
    /// comma that ends the arguments nothing where the language lets one; C's macro arguments count an empty one there.
    /// </summary>
    [Fact]
    public void ACallsArity_PassesOverATypesArguments_AndAnEndingCommaWhereTheLanguageLetsOne()
    {
        var scan = CFamilyLexer.Scan("f(a, b,) g(x < y, z > w) h(<K, V>{}) i(m as Map<K, V>) j(std::pair<int, int>(1, 2)) k(i++ < n, j > m) l(x << 2, y >> 2)", cpp: true);
        var trailing = new ScanCallRules(new HashSet<string>(), CppLike, TrailingComma: true);
        var plain = new ScanCallRules(new HashSet<string>(), CppLike);

        Assert.Equal(
            [2, 2, 1, 1, 1, 2, 2],
            new[] { "f", "g", "h", "i", "j", "k", "l" }.Select(name => ScanCalls.At(scan, At(scan, name, 1), trailing)!.Value.Arity));
        Assert.Equal(3, ScanCalls.At(scan, At(scan, "f", 1), plain)!.Value.Arity);
    }

    /// <summary>A list that runs past the most tokens one is read over is taken for none.</summary>
    [Fact]
    public void ATypeArgumentListTooLong_IsNone()
    {
        var scan = CFamilyLexer.Scan($"a<{string.Join(", ", Enumerable.Repeat("int", 200))}> b;", cpp: true);

        Assert.Equal(-1, ScanCalls.TypeArgumentsEnd(scan, 1, CppLike));
    }

    /// <summary>Ids are given once the file is read, declarations with a body first: a prototype before its definition takes the twin's id; parents and references keep their nodes.</summary>
    [Fact]
    public void AnExtractsIds_GoToDeclarationsWithABodyFirst()
    {
        var extract = new ScanExtract();
        var type = extract.Add(new ExtractNode("c:T", CodeNodeKinds.Type, "T", "T", 1, 9, "struct T"));
        var prototype = extract.Add(new ExtractNode("c:T.f()", CodeNodeKinds.Member, "f", "T.f", 2, 2, "void f();") { Parent = type, Prototype = true });
        var body = extract.Add(new ExtractNode("c:T.f()", CodeNodeKinds.Member, "f", "T.f", 3, 5, "void f()") { Parent = type });
        extract.Reference(body, CodeEdgeKinds.Calls, "g", 4, arity: 0);
        extract.Reference(prototype, CodeEdgeKinds.References, "U", 2);
        extract.Reference(body, CodeEdgeKinds.Calls, string.Empty, 4);

        var built = extract.Build();

        Assert.Equal(
            [("c:T", null), ("c:T.f()~2", "c:T"), ("c:T.f()", "c:T")],
            built.Nodes.Select(node => (node.Id, node.Parent)));
        Assert.Equal([("c:T.f()", "g"), ("c:T.f()~2", "U")], built.References.Select(reference => (reference.From, reference.Name)));
    }

    /// <summary>A part of a node of the same id, kind and holder is that node, running to the part's last line; another holder's is its own.</summary>
    [Fact]
    public void APart_IsTheNodeOfItsIdKindAndHolder()
    {
        var extract = new ScanExtract();
        var first = extract.Part(new ExtractNode("cpp:app", CodeNodeKinds.Type, "app", "app", 1, 4, "namespace app") { SubKind = "namespace" });
        var again = extract.Part(new ExtractNode("cpp:app", CodeNodeKinds.Type, "app", "app", 8, 12, "namespace app") { SubKind = "namespace" });
        var inner = extract.Part(new ExtractNode("cpp:app", CodeNodeKinds.Type, "app", "app", 9, 10, "namespace app") { Parent = first });

        Assert.Equal(first, again);
        Assert.NotEqual(first, inner);
        Assert.Equal((1, 12), (extract.Node(first).Start, extract.Node(first).End));
        Assert.Equal(["cpp:app", "cpp:app~2"], extract.Build().Nodes.Select(node => node.Id));
    }

    /// <summary>A scope is declared once, its first declaration's openness and holder kept.</summary>
    [Fact]
    public void AScope_IsDeclaredOnce()
    {
        var extract = new ScanExtract();
        extract.Scope("app", open: true, parent: null);
        extract.Scope("app", open: false, parent: "other");

        Assert.Equal([new ExtractScope("app", true)], extract.Build().Scopes);
    }

    /// <summary>The walk always moves on: a declaration that reads nothing, or answers where it began or before, is passed over a token at a time.</summary>
    [Fact]
    public void TheWalk_AlwaysMovesOn()
    {
        var read = new List<int>();

        ScanFrame.Walk(0, 6, at =>
        {
            read.Add(at);
            return at switch { 0 => 3, 3 => 3, 4 => 1, _ => at + 1 };
        });

        Assert.Equal([0, 3, 4, 5], read);
        Assert.Equal("a.b", (ScanFrame.Top with { Prefix = "a" }).Qualify("b"));
        Assert.Equal("b", ScanFrame.Top.Qualify("b"));
    }

    /// <summary>Type arguments as C++ writes them.</summary>
    private static ScanTypeArguments CppLike { get; } = new(
        new HashSet<string>(StringComparer.Ordinal) { ",", "::", "*", "&", ".", "...", "?", "-", "+" },
        new HashSet<string>(StringComparer.Ordinal) { "(", "::", "{" });

    /// <summary>The index of the <paramref name="nth"/> token written <paramref name="text"/>, from one.</summary>
    private static int At(ScannedText scan, string text, int nth)
        => Enumerable.Range(0, scan.Count).Where(at => scan.Is(at, text)).ElementAt(nth - 1);
}
