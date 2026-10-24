using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;
using DssHarness.Core.Codebase.Languages.Scanning;

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
