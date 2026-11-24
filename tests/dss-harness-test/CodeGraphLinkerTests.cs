using System.Diagnostics;
using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;

namespace DssHarness.Tests;

/// <summary>
/// The one linker every language uses, over hand-made extracts: each step a reference is looked for in, how sure each makes
/// an edge, what is counted external or unresolved, the forms of import, closed scopes, and the edges derived after.
/// </summary>
public sealed class CodeGraphLinkerTests
{
    /// <summary>A qualifier that names a type finds the member there, exact - by its simple name or its qualified one.</summary>
    [Fact]
    public void ANamedQualifier_FindsTheMemberInThatType_Exact()
    {
        var linked = Link(
            File("a.t", [Type("app.A"), Member("app.A.Run", 0)], scopes: [Open("app")]),
            File("b.t", [Type("app.B"), Member("app.B.Go", 0)], [Call("app.B.Go", "Run", 0, Named("A")), Call("app.B.Go", "Run", 0, Named("app.A"))], [Open("app")]));

        Assert.Contains(Edge("t:app.B.Go", "t:app.A.Run", CodeEdgeKinds.Calls, CodeConfidences.Exact), linked.Edges);
        Assert.Empty(linked.External);
    }

    /// <summary>
    /// A qualifier is where the code says and nowhere else: a member the qualifier's type does not hold is external,
    /// though the repository declares that name elsewhere.
    /// </summary>
    [Fact]
    public void AQualifier_LooksNowhereElse_AndAMemberItsTypeLacksIsExternal()
    {
        var linked = Link(
            File("a.t", [Type("app.A"), Type("app.C"), Member("app.C.Run", 0)], scopes: [Open("app")]),
            File("b.t", [Type("app.B"), Member("app.B.Go", 0)], [Call("app.B.Go", "Run", 0, Named("A"))], [Open("app")]));

        Assert.DoesNotContain(linked.Edges, edge => edge.Kind == CodeEdgeKinds.Calls);
        Assert.Equal(1, linked.External["t:app.B.Go"]);
    }

    /// <summary>
    /// <c>this</c> looks in the type and then its bases, nearest first; <c>base</c> skips the type itself; and a name the
    /// type does not declare is found in its bases unqualified too, exact - each base found before any member is looked for.
    /// </summary>
    [Fact]
    public void This_LooksInTheTypeThenItsBases_AndBase_SkipsTheTypeItself()
    {
        var linked = Link(
            File("a.t", [Type("app.A"), Member("app.A.Run", 0), Member("app.A.Only", 0)], scopes: [Open("app")]),
            File("b.t", [Type("app.B"), Member("app.B.Run", 0), Member("app.B.Go", 0) with { Parent = "t:app.B" }],
                [
                    Call("app.B.Go", "Run", 0, new ExtractQualifier(CodeQualifierKinds.This)),
                    Call("app.B.Go", "Run", 0, new ExtractQualifier(CodeQualifierKinds.Base)) with { Line = 2 },
                    Call("app.B.Go", "Only", 0, new ExtractQualifier(CodeQualifierKinds.This)) with { Line = 3 },
                    Call("app.B.Go", "Only", 0) with { Line = 4 },
                    Ref("app.B", CodeEdgeKinds.Inherits, "A"),
                ],
                [Open("app")]));

        Assert.Contains(Edge("t:app.B", "t:app.A", CodeEdgeKinds.Inherits, CodeConfidences.Exact), linked.Edges);
        Assert.Contains(new CodeEdge("t:app.B.Go", "t:app.B.Run", CodeEdgeKinds.Calls, 1, CodeConfidences.Exact), linked.Edges);
        Assert.Contains(new CodeEdge("t:app.B.Go", "t:app.A.Run", CodeEdgeKinds.Calls, 2, CodeConfidences.Exact), linked.Edges);
        Assert.Contains(new CodeEdge("t:app.B.Go", "t:app.A.Only", CodeEdgeKinds.Calls, 3, CodeConfidences.Exact), linked.Edges);
        Assert.Contains(new CodeEdge("t:app.B.Go", "t:app.A.Only", CodeEdgeKinds.Calls, 4, CodeConfidences.Exact), linked.Edges);
    }

    /// <summary>
    /// Unqualified, a name is looked for in the scopes it is written in from the inside out - the type, its bases, the
    /// namespace - and found there exact, before any import or the repository.
    /// </summary>
    [Fact]
    public void AnUnqualifiedName_IsFoundInTheEnclosingScopes_FromTheInsideOut_Exact()
    {
        var linked = Link(
            File("a.t", [Type("app.Helper"), Type("app.A"), Member("app.A.Go", 0), Member("app.A.Near", 0)], [
                Call("app.A.Go", "Helper", 0),
                Call("app.A.Go", "Near", 0) with { Line = 2 },
            ], [Open("app")]),
            File("b.t", [Type("lib.Helper"), Type("lib.X"), Member("lib.X.Near", 0)], scopes: [Open("lib")]));

        Assert.Contains(Edge("t:app.A.Go", "t:app.Helper", CodeEdgeKinds.Instantiates, CodeConfidences.Exact), linked.Edges);
        Assert.Contains(new CodeEdge("t:app.A.Go", "t:app.A.Near", CodeEdgeKinds.Calls, 2, CodeConfidences.Exact), linked.Edges);
        Assert.DoesNotContain(linked.Edges, edge => edge.To is "t:lib.Helper" or "t:lib.X.Near" && edge.Kind != CodeEdgeKinds.Contains);
    }

    /// <summary>
    /// Next, what the file imports, in order: the first import that holds the name decides, unique; an import's filters
    /// are applied, and an import reached by a prefix is not looked in unqualified.
    /// </summary>
    [Fact]
    public void Imports_AreLookedInInOrder_WithTheirFilters_AndAPrefixedImportOnlyThroughItsPrefix()
    {
        var linked = Link(
            File("one.t", [Type("one.Log"), Type("one.Hidden")], scopes: [Open("one")]),
            File("two.t", [Type("two.Log"), Type("two.Hidden"), Type("two.Shown")], scopes: [Open("two")]),
            File("three.t", [Type("three.Hidden"), Type("three.Shown")], scopes: [Open("three")]),
            File("use.t", [Member("Go", 0)],
                [
                    Ref("Go", CodeEdgeKinds.References, "Log"),
                    Ref("Go", CodeEdgeKinds.References, "Hidden") with { Line = 2 },
                    Ref("Go", CodeEdgeKinds.References, "Shown") with { Line = 3 },
                    Ref("Go", CodeEdgeKinds.References, "Shown") with { Line = 4, Qualifier = new ExtractQualifier(CodeQualifierKinds.Prefix, "p") },
                ],
                imports:
                [
                    Import(CodeImportForms.Scope, "one") with { Hide = ["Hidden"] },
                    Import(CodeImportForms.Scope, "three") with { Prefix = "p" },
                    Import(CodeImportForms.Scope, "two") with { Show = ["Log", "Hidden"] },
                ]));

        Assert.Contains(new CodeEdge("t:Go", "t:one.Log", CodeEdgeKinds.References, 1, CodeConfidences.Unique), linked.Edges);
        Assert.Contains(new CodeEdge("t:Go", "t:two.Hidden", CodeEdgeKinds.References, 2, CodeConfidences.Unique), linked.Edges);
        Assert.DoesNotContain(linked.Edges, edge => edge.From == "t:Go" && edge.To is "t:two.Log" or "t:one.Hidden" or "t:three.Hidden");

        // Shown is let through by no import unprefixed, so the repository's two are candidates.
        Assert.Contains(new CodeEdge("t:Go", "t:two.Shown", CodeEdgeKinds.References, 3, CodeConfidences.Candidates), linked.Edges);
        Assert.Contains(new CodeEdge("t:Go", "t:three.Shown", CodeEdgeKinds.References, 3, CodeConfidences.Candidates), linked.Edges);
        Assert.Contains(new CodeEdge("t:Go", "t:three.Shown", CodeEdgeKinds.References, 4, CodeConfidences.Exact), linked.Edges);
        Assert.DoesNotContain(new CodeEdge("t:Go", "t:two.Shown", CodeEdgeKinds.References, 4, CodeConfidences.Exact), linked.Edges);
    }

    /// <summary>
    /// A file imported by a relative path, an address or a path's ending makes its top-level declarations visible, and is
    /// an <c>imports</c> edge - exact by path or address, unique or candidates by an ending; a path that climbs out of the
    /// tree names nothing.
    /// </summary>
    [Fact]
    public void FilesImportedByPath_AddressOrEnding_AreVisible_AndImportedEdges()
    {
        var pubspec = File("pubspec.t", [new ExtractNode("t:pkg", CodeNodeKinds.Project, "pkg", "pkg", 1, 1, "pkg")]);
        var linked = Link(
            pubspec with { Extract = pubspec.Extract! with { AddressRoots = [new("package:pkg/", "lib")] } },
            File("lib/a.t", [Type("Near")]),
            File("lib/src/b.t", [Type("Far")]),
            File("x/util.h", [Type("UtilX")]),
            File("y/util.h", [Type("UtilY")]),
            File("z/only.h", [Type("Only")]),
            File("out.t", [Type("Out")]),
            File("lib/use.t", [Member("Go", 0)],
                [
                    Ref("Go", CodeEdgeKinds.References, "Near"),
                    Ref("Go", CodeEdgeKinds.References, "Far") with { Line = 2 },
                    Ref("Go", CodeEdgeKinds.References, "UtilX") with { Line = 3 },
                ],
                imports:
                [
                    Import(CodeImportForms.Relative, "./a.t"),
                    Import(CodeImportForms.Address, "package:pkg/src/b.t") with { Line = 2 },
                    Import(CodeImportForms.Ending, "util.h") with { Line = 3 },
                    Import(CodeImportForms.Ending, "only.h") with { Line = 4 },
                    Import(CodeImportForms.Relative, "../../out.t") with { Line = 5 },
                    Import(CodeImportForms.Address, "package:other/c.t") with { Line = 6 },
                ]));

        Assert.Contains(new CodeEdge("file:lib/use.t", "file:lib/a.t", CodeEdgeKinds.Imports, 1, CodeConfidences.Exact), linked.Edges);
        Assert.Contains(new CodeEdge("file:lib/use.t", "file:lib/src/b.t", CodeEdgeKinds.Imports, 2, CodeConfidences.Exact), linked.Edges);
        Assert.Contains(new CodeEdge("file:lib/use.t", "file:x/util.h", CodeEdgeKinds.Imports, 3, CodeConfidences.Candidates), linked.Edges);
        Assert.Contains(new CodeEdge("file:lib/use.t", "file:y/util.h", CodeEdgeKinds.Imports, 3, CodeConfidences.Candidates), linked.Edges);
        Assert.Contains(new CodeEdge("file:lib/use.t", "file:z/only.h", CodeEdgeKinds.Imports, 4, CodeConfidences.Unique), linked.Edges);
        Assert.DoesNotContain(linked.Edges, edge => edge.From == "file:lib/use.t" && edge.Line is 5 or 6);

        Assert.Contains(new CodeEdge("t:Go", "t:Near", CodeEdgeKinds.References, 1, CodeConfidences.Unique), linked.Edges);
        Assert.Contains(new CodeEdge("t:Go", "t:Far", CodeEdgeKinds.References, 2, CodeConfidences.Unique), linked.Edges);
        Assert.Contains(new CodeEdge("t:Go", "t:UtilX", CodeEdgeKinds.References, 3, CodeConfidences.Unique), linked.Edges);
    }

    /// <summary>
    /// An import of a file that stands in for the one before it is taken only where that one names no file: beside the
    /// importing file first, then by the path's ending - its edge, and what a name is found through, the one taken alone.
    /// </summary>
    [Fact]
    public void AnImportStandingInForAnother_IsTakenOnlyWhereThatOneNamesNoFile()
    {
        var linked = Link(
            File("src/util.h", [Type("Near")]),
            File("lib/util.h", [Type("Far"), Type("Near") with { Id = "t:Near@lib" }]),
            File("src/a.c", [Member("Go", 0)],
                [Ref("Go", CodeEdgeKinds.References, "Near")],
                imports: [Import(CodeImportForms.Relative, "util.h"), Import(CodeImportForms.Ending, "util.h") with { Fallback = true }]),
            File("app/b.c", [Member("Stop", 0)],
                [Ref("Stop", CodeEdgeKinds.References, "Far")],
                imports: [Import(CodeImportForms.Relative, "util.h"), Import(CodeImportForms.Ending, "util.h") with { Fallback = true }]));

        // Beside the file: that one alone, and no edge to the other file of that name.
        Assert.Contains(new CodeEdge("file:src/a.c", "file:src/util.h", CodeEdgeKinds.Imports, 1, CodeConfidences.Exact), linked.Edges);
        Assert.DoesNotContain(linked.Edges, edge => edge.From == "file:src/a.c" && edge.To == "file:lib/util.h");
        Assert.Contains(Edge("t:Go", "t:Near", CodeEdgeKinds.References, CodeConfidences.Unique), linked.Edges);

        // None beside it: every file the ending names.
        Assert.Contains(new CodeEdge("file:app/b.c", "file:src/util.h", CodeEdgeKinds.Imports, 1, CodeConfidences.Candidates), linked.Edges);
        Assert.Contains(new CodeEdge("file:app/b.c", "file:lib/util.h", CodeEdgeKinds.Imports, 1, CodeConfidences.Candidates), linked.Edges);
        Assert.Contains(Edge("t:Stop", "t:Far", CodeEdgeKinds.References, CodeConfidences.Unique), linked.Edges);
    }

    /// <summary>
    /// A declaration with no body found beside the one with a body it declares - of its language, qualified name and
    /// parameters - is that one: a call is its edge alone, where both were candidates; one of another name is no such pair.
    /// </summary>
    [Fact]
    public void APrototypeFoundBesideItsBody_IsThatBody()
    {
        var linked = Link(
            File("a.c", [Member("Run", 0) with { Prototype = true }, Member("Run", 0) with { Id = "t:Run~2" }, Member("Two", 0) with { Prototype = true }]),
            File("b.c", [Member("Go", 0), Member("Two", 0) with { Id = "t:Two@b" }, Member("ns.Two", 0) with { Id = "t:ns.Two", Name = "Two" }], [Call("Go", "Run", 0), Call("Go", "Two", 0) with { Line = 2 }]));

        Assert.Contains(Edge("t:Go", "t:Run~2", CodeEdgeKinds.Calls, CodeConfidences.Unique), linked.Edges);
        Assert.DoesNotContain(linked.Edges, edge => edge.Kind == CodeEdgeKinds.Calls && edge.To == "t:Run");

        // Two's prototype and body are one; ns.Two is another declaration of that name, so two candidates are left.
        Assert.Contains(new CodeEdge("t:Go", "t:Two@b", CodeEdgeKinds.Calls, 2, CodeConfidences.Candidates), linked.Edges);
        Assert.Contains(new CodeEdge("t:Go", "t:ns.Two", CodeEdgeKinds.Calls, 2, CodeConfidences.Candidates), linked.Edges);
        Assert.DoesNotContain(linked.Edges, edge => edge.Kind == CodeEdgeKinds.Calls && edge.To == "t:Two");

        // A prototype that takes otherwise than a body of its name declares none, and is no part of it.
        var unlike = Link(
            File("a.c", [Member("Three", 1) with { MaxArity = 2, Prototype = true }, Member("Three", 1) with { Id = "t:Three~2" }]),
            File("b.c", [Member("Go", 0)], [Call("Go", "Three", 1)]));

        Assert.DoesNotContain(unlike.Edges, edge => edge.Kind == CodeEdgeKinds.Declares);
        Assert.Equal(2, unlike.Edges.Count(edge => edge.Kind == CodeEdgeKinds.Calls));
    }

    /// <summary>A prototype given a body - or a body made a prototype, or renamed - changes what a call beside it finds, so linking again finds that call again.</summary>
    [Theory]
    [InlineData(false, "Run")]
    [InlineData(true, "Other")]
    public void APrototypeMadeABody_IsFoundAgainByACallInAnotherFile(bool prototype, string qualified)
    {
        var go = File("b.c", [Member("Go", 0)], [Call("Go", "Run", 0)]);
        var before = File("a.c", [Member("Run", 0) with { Prototype = true }, Member("Run", 0) with { Id = "t:Run~2" }]);
        var after = File("a.c", [Member("Run", 0) with { Prototype = prototype, Qualified = qualified }, Member("Run", 0) with { Id = "t:Run~2" }]);

        Assert.Single(Link(before, go).Edges, edge => edge.Kind == CodeEdgeKinds.Calls);
        Assert.Equal(2, Again([before, go], [after, go]).Edges.Count(edge => edge.Kind == CodeEdgeKinds.Calls));
    }

    /// <summary>
    /// A member defined outside its type's body - held by no node of that type in its file - is in that type by the scope
    /// it is declared in: <c>this</c> and <c>base</c> look there, as they do for a member its type's body holds.
    /// </summary>
    [Fact]
    public void AMemberDefinedOutsideItsTypesBody_IsInThatTypeByItsScope()
    {
        var linked = Link(
            File("a.h", [Type("app.A"), Member("app.A.Run", 0), Type("app.B"), Member("app.B.Log", 0)], [Ref("app.B", CodeEdgeKinds.Inherits, "A")], [Open("app")]),
            File("b.cc", [Member("app.B.Go", 0)], [Call("app.B.Go", "Log", 0, new ExtractQualifier(CodeQualifierKinds.This)), Call("app.B.Go", "Run", 0, new ExtractQualifier(CodeQualifierKinds.Base)) with { Line = 2 }], [Open("app")]));

        Assert.Contains(new CodeEdge("t:app.B.Go", "t:app.B.Log", CodeEdgeKinds.Calls, 1, CodeConfidences.Exact), linked.Edges);
        Assert.Contains(new CodeEdge("t:app.B.Go", "t:app.A.Run", CodeEdgeKinds.Calls, 2, CodeConfidences.Exact), linked.Edges);
    }

    /// <summary>
    /// Last, the repository by name and arity: one is unique; two or more are an edge to each, candidates, however many -
    /// a hundred among them, none dropped; none is counted external.
    /// </summary>
    [Fact]
    public void TheRepository_FindsByNameAndArity_UniqueCandidatesOrExternal()
    {
        var many = Enumerable.Range(1, 100).Select(at => File($"m{at}.t", [Member($"m{at}.Many", 0) with { Scope = $"m{at}" }], scopes: [Open($"m{at}")]));
        var linked = Link([
            File("one.t", [Member("one.Single", 1) with { Scope = "one" }, Member("one.Single2", 2) with { Scope = "one", Name = "Single" }], scopes: [Open("one")]),
            File("two.t", [Member("two.Pair", 0) with { Scope = "two" }], scopes: [Open("two")]),
            File("three.t", [Member("three.Pair", 0) with { Scope = "three" }], scopes: [Open("three")]),
            .. many,
            File("use.t", [Member("Go", 0)],
                [
                    Call("Go", "Single", 2),
                    Call("Go", "Pair", 0) with { Line = 2 },
                    Call("Go", "Many", 0) with { Line = 3 },
                    Call("Go", "Nothing", 0) with { Line = 4 },
                    Call("Go", "Single", 5) with { Line = 5 },
                ]),
        ]);

        Assert.Contains(new CodeEdge("t:Go", "t:one.Single2", CodeEdgeKinds.Calls, 1, CodeConfidences.Unique), linked.Edges);
        Assert.Contains(new CodeEdge("t:Go", "t:two.Pair", CodeEdgeKinds.Calls, 2, CodeConfidences.Candidates), linked.Edges);
        Assert.Contains(new CodeEdge("t:Go", "t:three.Pair", CodeEdgeKinds.Calls, 2, CodeConfidences.Candidates), linked.Edges);
        Assert.Equal(
            [.. Enumerable.Range(1, 100).Select(at => $"t:m{at}.Many").Order(StringComparer.Ordinal)],
            linked.Edges.Where(edge => edge is { Line: 3, Kind: CodeEdgeKinds.Calls, Confidence: CodeConfidences.Candidates }).Select(edge => edge.To).Order(StringComparer.Ordinal));
        Assert.Equal(2, linked.External["t:Go"]);
        Assert.Equal(5, linked.References);
    }

    /// <summary>A member that takes any number of arguments is called by any; one with a range by a count within it.</summary>
    [Fact]
    public void Arity_IsARange_AndOneWithNoLeastTakesAny()
    {
        var any = new ExtractNode("t:Any", CodeNodeKinds.Member, "Any", "Any", 1, 1, "Any");
        var ranged = Member("Ranged", 1) with { MaxArity = 3 };
        var open = Member("Open", 1) with { MaxArity = null };

        Assert.True(any.TakesArity(9));
        Assert.True(ranged.TakesArity(3) && ranged.TakesArity(1) && ranged.TakesArity(null));
        Assert.False(ranged.TakesArity(4) || ranged.TakesArity(0));
        Assert.True(open.TakesArity(40));
    }

    /// <summary>
    /// What a closed scope declares is its file's alone: found from that file, by the repository's step too, and external
    /// from any other.
    /// </summary>
    [Fact]
    public void AClosedScopesDeclarations_AreSeenFromItsFileAlone()
    {
        var linked = Link(
            File("a.t", [Member("Go", 0), Type("hidden"), Member("hidden.Secret", 0) with { Scope = "hidden" }], [Call("Go", "Secret", 0)], [new ExtractScope("hidden", Open: false)]),
            File("b.t", [Member("Other", 0)], [Call("Other", "Secret", 0)], [new ExtractScope("hidden", Open: false)]));

        Assert.Contains(Edge("t:Go", "t:hidden.Secret", CodeEdgeKinds.Calls, CodeConfidences.Unique), linked.Edges);
        Assert.Equal(1, linked.External["t:Other"]);
    }

    /// <summary>
    /// A closed scope at a file's top - no scope holds it, and it is no type's or namespace's own, as a C file's for its
    /// <c>static</c> names - is one every declaration of the file is written in: what it declares is found exact from
    /// anywhere in the file, after the declaration's own scopes; a closed type's members are not, and another file finds
    /// none of it.
    /// </summary>
    [Fact]
    public void AFilesTopClosedScope_IsOneEveryDeclarationOfItIsWrittenIn()
    {
        var linked = Link(
            File(
                "a.t",
                [Member("Go", 0), Member("Helper", 0) with { Id = "t:a.t:Helper", Scope = "file:a.t" }, Type("Local"), Member("Local.Run", 0) with { Scope = "Local" }],
                [Call("Go", "Helper", 0), Call("Go", "Run", 0)],
                [new ExtractScope("file:a.t", Open: false), new ExtractScope("Local", Open: false)]),
            File("b.t", [Member("Other", 0)], [Call("Other", "Helper", 0)]));

        Assert.Contains(Edge("t:Go", "t:a.t:Helper", CodeEdgeKinds.Calls, CodeConfidences.Exact), linked.Edges);
        Assert.Contains(Edge("t:Go", "t:Local.Run", CodeEdgeKinds.Calls, CodeConfidences.Unique), linked.Edges);
        Assert.Equal(1, linked.External["t:Other"]);
    }

    /// <summary>
    /// A chain written as links past an import's prefix - of a name or of a type, the prefix one name or several - is found a step a
    /// link however long: its first name past the prefix what the import makes visible by it, each next a member of what the one
    /// before came to, the call exact on the type the last names.
    /// </summary>
    [Fact]
    public void AChainOfLinksPastAnImportsPrefix_IsFoundAStepALink()
    {
        static IEnumerable<ExtractReference> Chain(string from, string prefix, string kind) =>
        [
            Ref(from, CodeEdgeKinds.Uses, "Outer") with { Qualifier = Prefix(prefix) },
            Ref(from, CodeEdgeKinds.Uses, "Inner") with { Qualifier = new ExtractQualifier(kind) { Link = 0 } },
            Ref(from, CodeEdgeKinds.Uses, "Deep") with { Qualifier = new ExtractQualifier(kind) { Link = 1 } },
            Call(from, "Run", 0, new ExtractQualifier(kind) { Link = 2 }),
        ];

        var linked = Link(
            File("lib/shapes.t", [Type("Outer"), Type("Outer.Inner"), Type("Outer.Inner.Deep"), Member("Outer.Inner.Deep.Run", 0)]),
            File("named.t", [Member("Named", 0)], [.. Chain("Named", "ns", CodeQualifierKinds.Named)], imports: [Import(CodeImportForms.Relative, "lib/shapes.t") with { Prefix = "ns" }]),
            File("typed.t", [Member("Typed", 0)], [.. Chain("Typed", "lib.shapes", CodeQualifierKinds.Type)], imports: [Import(CodeImportForms.Relative, "lib/shapes.t") with { Prefix = "lib.shapes" }]));

        Assert.Equal(
            [Edge("t:Named", "t:Outer.Inner.Deep.Run", CodeEdgeKinds.Calls, CodeConfidences.Exact), Edge("t:Typed", "t:Outer.Inner.Deep.Run", CodeEdgeKinds.Calls, CodeConfidences.Exact)],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Calls).OrderBy(edge => edge.From, StringComparer.Ordinal));
        Assert.Empty(linked.External);
    }

    /// <summary>
    /// What a member's body declares - a closed scope its file declares under the member's qualified name, as a Python function's
    /// body and a JavaScript wrapper's are - is seen from that body and the bodies inside it alone: found exact from the member's
    /// own code and from a member declared inside it, by its name and by a type's; and from nowhere else - not by a name the rest
    /// of its file writes alone nor by the name of a type it ends, and not from another file - nor by the repository's step from
    /// anywhere, the body's own code's call on a value whose type nothing says among it: each counted external, a name nothing
    /// that code sees declares.
    /// </summary>
    [Fact]
    public void AMembersBodysDeclarations_AreSeenFromThatBodyAlone()
    {
        var linked = Link(
            File(
                "a.t",
                [Member("Outer", 0), Member("Outer.Helper", 0), Type("Outer.Local"), Member("Outer.Local.Make", 0), Member("Outer.Helper.Inner", 0), Member("Sibling", 0)],
                [
                    Call("Outer", "Helper", 0),
                    Call("Outer", "Helper", 0, new ExtractQualifier(CodeQualifierKinds.Value)),
                    Call("Outer.Helper.Inner", "Helper", 0),
                    Ref("Outer.Helper.Inner", CodeEdgeKinds.References, "Local"),
                    Call("Outer.Helper.Inner", "Make", 0, new ExtractQualifier(CodeQualifierKinds.Type, "Local")),
                    Call("Sibling", "Helper", 0),
                    Call("Sibling", "Helper", 0, new ExtractQualifier(CodeQualifierKinds.Value)),
                    Ref("Sibling", CodeEdgeKinds.References, "Local"),
                    Call("Sibling", "Make", 0, new ExtractQualifier(CodeQualifierKinds.Type, "Local")),
                ],
                [new ExtractScope("Outer", Open: false), new ExtractScope("Outer.Helper", Open: false) { Parent = "Outer" }]),
            File("b.t", [Member("Far", 0)], [Call("Far", "Helper", 0)]));

        Assert.Equal(
            [
                Edge("t:Outer", "t:Outer.Helper", CodeEdgeKinds.Calls, CodeConfidences.Exact),
                Edge("t:Outer.Helper.Inner", "t:Outer.Helper", CodeEdgeKinds.Calls, CodeConfidences.Exact),
                Edge("t:Outer.Helper.Inner", "t:Outer.Local.Make", CodeEdgeKinds.Calls, CodeConfidences.Exact),
                Edge("t:Outer.Helper.Inner", "t:Outer.Local", CodeEdgeKinds.References, CodeConfidences.Exact),
            ],
            linked.Edges.Where(edge => edge.Kind is CodeEdgeKinds.Calls or CodeEdgeKinds.References).OrderBy(edge => edge.From, StringComparer.Ordinal).ThenBy(edge => edge.Kind, StringComparer.Ordinal).ThenBy(edge => edge.To, StringComparer.Ordinal));
        Assert.Equal([("t:Far", 1), ("t:Outer", 1), ("t:Sibling", 4)], linked.External.OrderBy(count => count.Key, StringComparer.Ordinal).Select(count => (count.Key, count.Value)));
    }

    /// <summary>
    /// A global import is made by every file of its language: a name it makes visible is found unique through it from a file
    /// that imports nothing, where the repository alone would give candidates.
    /// </summary>
    [Fact]
    public void AGlobalImport_IsMadeByEveryFileOfItsLanguage()
    {
        var linked = Link(
            File("usings.t", [], imports: [Import(CodeImportForms.Scope, "lib") with { Global = true }]),
            File("lib.t", [new ExtractNode("t:lib", CodeNodeKinds.Namespace, "lib", "lib", 1, 1, "lib"), Type("lib.Tool")], scopes: [Open("lib")]),
            File("other.t", [Type("other.Tool")], scopes: [Open("other")]),
            File("use.t", [Member("Go", 0)], [Ref("Go", CodeEdgeKinds.References, "Tool")]),
            new CodeGraphFile("elsewhere.u", "u", Extract([new ExtractNode("u:Go", CodeNodeKinds.Member, "Go", "Go", 1, 1, "Go")]) with
            {
                References = [new ExtractReference("u:Go", CodeEdgeKinds.References, "Tool", 1)],
            }));

        Assert.Contains(Edge("t:Go", "t:lib.Tool", CodeEdgeKinds.References, CodeConfidences.Unique), linked.Edges);
        Assert.DoesNotContain(linked.Edges, edge => edge.From == "t:Go" && edge.To == "t:other.Tool");
        Assert.Contains(Edge("u:Go", "t:other.Tool", CodeEdgeKinds.References, CodeConfidences.Candidates), linked.Edges);
        Assert.Contains(Edge("file:usings.t", "t:lib", CodeEdgeKinds.Imports, CodeConfidences.Exact), linked.Edges);
    }

    /// <summary>
    /// A scope import's prefix - an alias - stands for what it imports wherever a type's or a scope's name is written:
    /// alone, and as a qualifier no type or scope has as its whole name; exact, from its own file and, made global, from
    /// every file of its language, and from no other.
    /// </summary>
    [Fact]
    public void AScopeImportsPrefix_StandsForWhatItImports_AloneOrAsAQualifier()
    {
        var linked = Link(
            File("lib.t", [Type("lib.Tool"), Member("lib.Tool.Run", 0)], scopes: [Open("lib")]),
            File("usings.t", [], imports: [Import(CodeImportForms.Scope, "lib.Tool") with { Prefix = "Gadget", Global = true }]),
            File("use.t", [Type("app.A"), Member("app.A.Go", 0)],
                [
                    Ref("app.A.Go", CodeEdgeKinds.References, "Kit"),
                    Call("app.A.Go", "Run", 0, Named("Kit")) with { Line = 2 },
                    Ref("app.A.Go", CodeEdgeKinds.References, "Gadget") with { Line = 3 },
                    Call("app.A.Go", "Run", 0, Named("Gadget")) with { Line = 4 },
                ],
                [Open("app")],
                [Import(CodeImportForms.Scope, "lib.Tool") with { Prefix = "Kit" }]),
            new CodeGraphFile("other.u", "u", Extract([new ExtractNode("u:Go", CodeNodeKinds.Member, "Go", "Go", 1, 1, "Go")]) with
            {
                References = [new ExtractReference("u:Go", CodeEdgeKinds.References, "Gadget", 1), new ExtractReference("u:Go", CodeEdgeKinds.References, "Kit", 2)],
            }));

        Assert.Contains(new CodeEdge("t:app.A.Go", "t:lib.Tool", CodeEdgeKinds.References, 1, CodeConfidences.Exact), linked.Edges);
        Assert.Contains(new CodeEdge("t:app.A.Go", "t:lib.Tool.Run", CodeEdgeKinds.Calls, 2, CodeConfidences.Exact), linked.Edges);
        Assert.Contains(new CodeEdge("t:app.A.Go", "t:lib.Tool", CodeEdgeKinds.References, 3, CodeConfidences.Exact), linked.Edges);
        Assert.Contains(new CodeEdge("t:app.A.Go", "t:lib.Tool.Run", CodeEdgeKinds.Calls, 4, CodeConfidences.Exact), linked.Edges);
        Assert.DoesNotContain(linked.Edges, edge => edge.From == "u:Go");
        Assert.Equal(2, linked.External["u:Go"]);
    }

    /// <summary>
    /// A dotted name that begins with an alias is that alias's target and the rest, from its own file; a type or a scope
    /// of a written name is found before an alias of it, as a name written alone is in the scopes before the aliases.
    /// </summary>
    [Fact]
    public void ADottedNameBeginningWithAnAlias_IsItsTarget_AndAWholeNameOrAScopesTypeComesFirst()
    {
        var linked = Link(
            File("lib.t", [Type("lib.Tool"), Member("lib.Tool.Run", 0), Type("lib.Kit"), Member("lib.Kit.Run", 0)], scopes: [Open("lib")]),
            File("use.t", [Type("app.Kit"), Member("app.Kit.Run", 0), Type("app.A"), Member("app.A.Go", 0)],
                [
                    Call("app.A.Go", "Run", 0, Named("L.Tool")),
                    Call("app.A.Go", "Run", 0, Named("Kit")) with { Line = 2 },
                    Call("app.A.Go", "Run", 0, Named("lib.Kit")) with { Line = 3 },
                ],
                [Open("app")],
                [Import(CodeImportForms.Scope, "lib") with { Prefix = "L" }, Import(CodeImportForms.Scope, "lib.Kit") with { Prefix = "Kit" }]));

        Assert.Contains(new CodeEdge("t:app.A.Go", "t:lib.Tool.Run", CodeEdgeKinds.Calls, 1, CodeConfidences.Exact), linked.Edges);
        Assert.Contains(new CodeEdge("t:app.A.Go", "t:app.Kit.Run", CodeEdgeKinds.Calls, 2, CodeConfidences.Exact), linked.Edges);
        Assert.Contains(new CodeEdge("t:app.A.Go", "t:lib.Kit.Run", CodeEdgeKinds.Calls, 3, CodeConfidences.Exact), linked.Edges);
        Assert.DoesNotContain(linked.Edges, edge => edge is { From: "t:app.A.Go", To: "t:lib.Kit.Run", Line: 2 });
    }

    /// <summary>
    /// A value of a type the code does not say is found through the member it is - its type's member, exact - and else by
    /// its name alone in the repository, never in the scopes the call is written in as though it were a call of theirs.
    /// </summary>
    [Fact]
    public void AValueOfNoSaidType_IsFoundThroughTheMemberItIs_ElseByItsNameAlone()
    {
        var linked = Link(
            File("a.t",
                [Type("app.A"), Member("app.A.Save", 0), Value("app.A.repo", "Repo"), Value("app.A.loose", null), Member("app.A.Go", 0), Type("app.Repo"), Member("app.Repo.Save", 0)],
                [
                    Call("app.A.Go", "Save", 0, new ExtractQualifier(CodeQualifierKinds.Value)),
                    Call("app.A.Go", "Save", 0, new ExtractQualifier(CodeQualifierKinds.Value, "repo")) with { Line = 2 },
                    Call("app.A.Go", "Save", 0, new ExtractQualifier(CodeQualifierKinds.Value, "loose")) with { Line = 3 },
                    Call("app.A.Go", "Save", 0) with { Line = 4 },
                ],
                [Open("app")]));

        Assert.Contains(new CodeEdge("t:app.A.Go", "t:app.A.Save", CodeEdgeKinds.Calls, 1, CodeConfidences.Candidates), linked.Edges);
        Assert.Contains(new CodeEdge("t:app.A.Go", "t:app.Repo.Save", CodeEdgeKinds.Calls, 1, CodeConfidences.Candidates), linked.Edges);
        Assert.Contains(new CodeEdge("t:app.A.Go", "t:app.Repo.Save", CodeEdgeKinds.Calls, 2, CodeConfidences.Exact), linked.Edges);
        Assert.DoesNotContain(linked.Edges, edge => edge is { From: "t:app.A.Go", To: "t:app.A.Save", Line: 2 });
        Assert.Contains(new CodeEdge("t:app.A.Go", "t:app.A.Save", CodeEdgeKinds.Calls, 3, CodeConfidences.Candidates), linked.Edges);
        Assert.Contains(new CodeEdge("t:app.A.Go", "t:app.A.Save", CodeEdgeKinds.Calls, 4, CodeConfidences.Exact), linked.Edges);
    }

    /// <summary>
    /// A named qualifier no type or scope has is a value where a member the scopes hold - a base's among them - has it, and
    /// the call goes through that member's type, as its own file names it; a chain of names, from a value or a type, goes
    /// through each one's type in turn; one that is neither stays external.
    /// </summary>
    [Fact]
    public void ANamedQualifierNamingNoType_IsAValue_AndAChainGoesThroughEachType()
    {
        var linked = Link(
            File("base.t", [Type("app.Base"), Value("app.Base.Repository", "Repo"), Type("app.Repo"), Member("app.Repo.Save", 0), Type("app.Cache"), Value("app.Cache.Instance", "Cache"), Value("app.Cache.Store", "Repo")], scopes: [Open("app")]),
            File("derived.t", [Type("app.Derived"), Member("app.Derived.Go", 0), Value("app.Derived.cache", "Cache")],
                [
                    Ref("app.Derived", CodeEdgeKinds.Inherits, "Base"),
                    Call("app.Derived.Go", "Save", 0, Named("Repository")),
                    Call("app.Derived.Go", "Save", 0, Named("Cache.Instance.Store")) with { Line = 2 },
                    Call("app.Derived.Go", "Save", 0, new ExtractQualifier(CodeQualifierKinds.Value, "cache.Store")) with { Line = 3 },
                    Call("app.Derived.Go", "Save", 0, Named("Console")) with { Line = 4 },
                ],
                [Open("app")]));

        Assert.Contains(new CodeEdge("t:app.Derived.Go", "t:app.Repo.Save", CodeEdgeKinds.Calls, 1, CodeConfidences.Exact), linked.Edges);
        Assert.Contains(new CodeEdge("t:app.Derived.Go", "t:app.Repo.Save", CodeEdgeKinds.Calls, 2, CodeConfidences.Exact), linked.Edges);
        Assert.Contains(new CodeEdge("t:app.Derived.Go", "t:app.Repo.Save", CodeEdgeKinds.Calls, 3, CodeConfidences.Exact), linked.Edges);
        Assert.DoesNotContain(linked.Edges, edge => edge is { From: "t:app.Derived.Go", Line: 4 });
        Assert.Equal(1, linked.External["t:app.Derived.Go"]);
    }

    /// <summary>
    /// A value the scopes hold is the qualifier where no nearer scope declares a type or a scope of its name, before a type
    /// an import has of it; a type a nearer scope declares is the qualifier before the type at the top of that name; a
    /// chain that comes to a type the repository does not declare holds nothing it declares, though the repository has a
    /// member of the call's name, and one through a value of no said type is looked for by the call's name alone.
    /// </summary>
    [Fact]
    public void TheNearestOfAValueOrAType_IsTheQualifier_AndAChainThroughATypeTheRepositoryLacks_FindsNothing()
    {
        var linked = Link(
            File("lib.t", [Type("lib.Log"), Member("lib.Log.Info", 0)], scopes: [Open("lib")]),
            File("top.t", [Type("Inner"), Member("Inner.Make", 0)]),
            File("app.t",
                [
                    Type("app.ILog"), Member("app.ILog.Info", 0),
                    Type("app.Base"), Value("app.Base.Log", "ILog"), Value("app.Base.map", "Dictionary`2"), Value("app.Base.loose", null),
                    Type("app.D"), Member("app.D.Go", 0), Type("app.D.Inner"), Member("app.D.Inner.Make", 0),
                    Type("app.Keys"), Member("app.Keys.Contains", 1),
                    Type("app.D.Cfg"), Member("app.D.Cfg.Load", 0), Value("app.Base.Cfg", "Settings"), Type("app.Settings"), Member("app.Settings.Load", 0),
                ],
                [
                    Ref("app.D", CodeEdgeKinds.Inherits, "Base"),
                    Call("app.D.Go", "Info", 0, Named("Log")),
                    Call("app.D.Go", "Make", 0, Named("Inner")) with { Line = 2 },
                    Call("app.D.Go", "Contains", 1, new ExtractQualifier(CodeQualifierKinds.Value, "map.Keys")) with { Line = 3 },
                    Call("app.D.Go", "Contains", 1, Named("map.Keys")) with { Line = 4 },
                    Call("app.D.Go", "Contains", 1, new ExtractQualifier(CodeQualifierKinds.Value, "loose.Keys")) with { Line = 5 },
                    Call("app.D.Go", "Load", 0, Named("Cfg")) with { Line = 6 },
                ],
                [Open("app")],
                [Import(CodeImportForms.Scope, "lib")]));

        Assert.Equal(
            [
                ("t:app.ILog.Info", 1, CodeConfidences.Exact),
                ("t:app.D.Inner.Make", 2, CodeConfidences.Exact),
                ("t:app.Keys.Contains", 5, CodeConfidences.Unique),
                ("t:app.D.Cfg.Load", 6, CodeConfidences.Exact),
            ],
            linked.Edges.Where(edge => edge is { From: "t:app.D.Go", Kind: CodeEdgeKinds.Calls }).Select(edge => (edge.To, edge.Line, edge.Confidence)));
        Assert.Equal(2, linked.External["t:app.D.Go"]);
    }

    /// <summary>
    /// A type qualifier is a type or a scope alone, though a value of its first name is nearer, which a named one is; a
    /// named chain through a value of no said type is looked for by the call's name alone, as a value qualifier's is, and
    /// a call on a value found by its name alone never makes a type.
    /// </summary>
    [Fact]
    public void ATypeQualifier_IsNeverAValue_AndACallOnAValueOfNoSaidType_NeverMakesAType()
    {
        var linked = Link(
            File("orders.t",
                [new ExtractNode("t:App.Orders", CodeNodeKinds.Namespace, "Orders", "App.Orders", 1, 1, "namespace App.Orders") { Scope = "App" }, Type("App.Orders.OrderLine"), Member("App.Orders.OrderLine.Make", 0)],
                scopes: [Open("App"), Open("App.Orders")]),
            File("shop.t",
                [Type("App.Shop"), Value("App.Shop.Orders", "List`1"), Value("App.Shop.Loose", null), Member("App.Shop.Add", 0), Type("App.Order"), Member("App.Stock.Order", 0)],
                [
                    Call("App.Shop.Add", "Make", 0, new ExtractQualifier(CodeQualifierKinds.Type, "Orders.OrderLine")),
                    Ref("App.Shop.Add", CodeEdgeKinds.References, "OrderLine") with { Line = 2, Qualifier = new ExtractQualifier(CodeQualifierKinds.Type, "Orders") },
                    Call("App.Shop.Add", "Make", 0, Named("Orders.OrderLine")) with { Line = 3 },
                    Call("App.Shop.Add", "Order", 0, Named("Loose.Inner")) with { Line = 4 },
                ],
                [Open("App")]));

        Assert.Equal(
            [
                ("t:App.Orders.OrderLine.Make", 1, CodeEdgeKinds.Calls, CodeConfidences.Exact),
                ("t:App.Orders.OrderLine", 2, CodeEdgeKinds.References, CodeConfidences.Exact),
                ("t:App.Stock.Order", 4, CodeEdgeKinds.Calls, CodeConfidences.Unique),
            ],
            linked.Edges.Where(edge => edge.From == "t:App.Shop.Add").Select(edge => (edge.To, edge.Line, edge.Kind, edge.Confidence)));
        Assert.Equal(1, linked.External["t:App.Shop.Add"]);
    }

    /// <summary>
    /// A scope qualifier finds the name as that scope declares it, else among the declarations in no scope, exact - and with
    /// no name among those alone - never in a scope the reference is written in, nor in another language's files; what
    /// neither holds is external.
    /// </summary>
    [Fact]
    public void AScopeQualifier_FindsTheNameInThatScope_ElseAtTheTop_OfItsLanguageAlone()
    {
        var linked = Link(
            File("app.t", [Member("App.Go", 0), Member("App.Local", 0), Member("App.Sub.Inner", 0)],
                [
                    Call("App.Go", "Local", 0, new ExtractQualifier(CodeQualifierKinds.Scope, "App")),
                    Call("App.Go", "Helper", 0, new ExtractQualifier(CodeQualifierKinds.Scope, "App")) with { Line = 2 },
                    Call("App.Go", "Local", 0, new ExtractQualifier(CodeQualifierKinds.Scope)) with { Line = 3 },
                    Call("App.Go", "Inner", 0, new ExtractQualifier(CodeQualifierKinds.Scope, "App")) with { Line = 4 },
                    Call("App.Go", "Native", 0, new ExtractQualifier(CodeQualifierKinds.Scope, "App")) with { Line = 5 },
                    Call("App.Go", "Local", 0, new ExtractQualifier(CodeQualifierKinds.Scope, "Lib")) with { Line = 6 },
                    Call("App.Go", "Other", 0, new ExtractQualifier(CodeQualifierKinds.Scope, "App")) with { Line = 7 },
                ],
                [Open("App"), Open("App.Sub")]),
            File("top.t", [Member("Helper", 0), Member("Local", 0)]),
            File("native.c", [Member("Native", 0), Member("App.Other", 0)], scopes: [Open("App")]) with { Language = "c" });

        Assert.Equal(
            [
                ("t:App.Local", 1, CodeConfidences.Exact),
                ("t:Helper", 2, CodeConfidences.Exact),
                ("t:Local", 3, CodeConfidences.Exact),
                ("t:Local", 6, CodeConfidences.Exact),
            ],
            linked.Edges.Where(edge => edge.From == "t:App.Go" && edge.Kind == CodeEdgeKinds.Calls).Select(edge => (edge.To, edge.Line, edge.Confidence)));
        Assert.Equal(3, linked.External["t:App.Go"]);
    }

    /// <summary>
    /// A member declared to extend a type is found, <c>unique</c>, for a call on a value of that type - <c>this</c> among
    /// them - or of a type built on it, or of a type the repository does not declare named alike, where the call's file
    /// reaches it - through an import of its scope or its file, from its own file where it is closed, or from the top -
    /// never as what an override overrides; the type it extends the one its own file names, so a
    /// type of that name elsewhere finds nothing, and a type that holds the name itself comes first; <c>base</c> reaches
    /// one of a base alone, and a value's type is as sure as the extension found through it.
    /// </summary>
    [Fact]
    public void AnExtension_IsFoundWhereReached_ForItsOwnType_ATypeBuiltOnIt_AndATypeTheRepositoryLacks()
    {
        var linked = Link(
            File("ext.t",
                [
                    Type("lib.Ext"),
                    Member("lib.Ext.Describe", 0) with { Extends = "IShape" },
                    Member("lib.Ext.AddAll", 1) with { Extends = "lib.IServices" },
                    Member("lib.Ext.Paint", 0) with { Extends = "app.Shape" },
                    Member("lib.Ext.Spin", 0) with { Extends = "app.Circle" },
                ],
                scopes: [Open("lib")]),
            File("far.t", [Type("far.Hidden"), Member("far.Hidden.Describe", 0) with { Extends = "IShape" }], scopes: [Open("far")]),
            File("other.t", [Type("other.Shape")], scopes: [Open("other")]),
            File("top.t", [Type("Top"), Member("Top.Wrap", 0) with { Extends = "app.Shape" }]),
            File("deep.t", [Type("deep.PathExt"), Member("deep.PathExt.Shine", 0) with { Extends = "app.Shape" }], scopes: [Open("deep")]),
            File("app.t",
                [
                    Type("app.IShape"), Type("app.Shape"), Type("app.Circle"), Member("app.Circle.Go", 0) with { Parent = "t:app.Circle" }, Type("app.Other"),
                    Type("app.Own"), Member("app.Own.Describe", 0), Type("app.Loop"), Type("app.Back"), Value("app.Circle.next", "Circle"),
                    Type("app.LocalExt"), Member("app.LocalExt.Glow", 0) with { Extends = "Shape" },
                ],
                [
                    Ref("app.Shape", CodeEdgeKinds.Implements, "IShape"),
                    Ref("app.Circle", CodeEdgeKinds.Inherits, "Shape"),
                    Ref("app.Own", CodeEdgeKinds.Implements, "IShape"),
                    Ref("app.Loop", CodeEdgeKinds.Inherits, "Back"),
                    Ref("app.Back", CodeEdgeKinds.Inherits, "Loop"),
                    Call("app.Circle.Go", "Describe", 0, Named("Circle")),
                    Call("app.Circle.Go", "AddAll", 1, Named("IServices")) with { Line = 2 },
                    Call("app.Circle.Go", "Describe", 0, Named("Other")) with { Line = 3 },
                    Call("app.Circle.Go", "Paint", 0, Named("other.Shape")) with { Line = 4 },
                    Call("app.Circle.Go", "Paint", 0, new ExtractQualifier(CodeQualifierKinds.This)) with { Line = 5 },
                    Call("app.Circle.Go", "Describe", 0, Named("Own")) with { Line = 6 },
                    Call("app.Circle.Go", "Describe", 0, Named("Loop")) with { Line = 7 },
                    Call("app.Circle.Go", "Spin", 0, new ExtractQualifier(CodeQualifierKinds.Base)) with { Line = 8 },
                    Call("app.Circle.Go", "Paint", 0, new ExtractQualifier(CodeQualifierKinds.Base)) with { Line = 9 },
                    Call("app.Circle.Go", "Paint", 0, new ExtractQualifier(CodeQualifierKinds.Value, "next")) with { Line = 10 },
                    Call("app.Circle.Go", "Wrap", 0, Named("Circle")) with { Line = 11 },
                    Call("app.Circle.Go", "Shine", 0, Named("Circle")) with { Line = 12 },
                    Call("app.Circle.Go", "Glow", 0, Named("Circle")) with { Line = 13 },
                    new ExtractReference("t:app.Circle.Go", CodeEdgeKinds.Overrides, "Paint", 14) { Arity = 0, Qualifier = new ExtractQualifier(CodeQualifierKinds.Base) },
                ],
                [Open("app"), new ExtractScope("app.LocalExt", Open: false) { Parent = "app" }],
                [Import(CodeImportForms.Scope, "lib"), Import(CodeImportForms.Relative, "deep.t")]));

        Assert.Equal(
            [
                ("t:lib.Ext.Describe", 1, CodeConfidences.Unique),
                ("t:lib.Ext.AddAll", 2, CodeConfidences.Unique),
                ("t:lib.Ext.Paint", 5, CodeConfidences.Unique),
                ("t:app.Own.Describe", 6, CodeConfidences.Exact),
                ("t:lib.Ext.Paint", 9, CodeConfidences.Unique),
                ("t:lib.Ext.Paint", 10, CodeConfidences.Unique),
                ("t:Top.Wrap", 11, CodeConfidences.Unique),
                ("t:deep.PathExt.Shine", 12, CodeConfidences.Unique),
                ("t:app.LocalExt.Glow", 13, CodeConfidences.Unique),
            ],
            linked.Edges.Where(edge => edge is { From: "t:app.Circle.Go", Kind: CodeEdgeKinds.Calls }).Select(edge => (edge.To, edge.Line, edge.Confidence)));
        Assert.DoesNotContain(linked.Edges, edge => edge.Kind == CodeEdgeKinds.Overrides);
    }

    /// <summary>
    /// An import of a file makes visible what each file it exports does - each export's filter applied, exports of exports
    /// followed, each file once though they come round again - and an export makes nothing visible to the file that makes
    /// it, which finds the name as though it imported nothing.
    /// </summary>
    [Fact]
    public void AnImportOfAFile_SeesWhatItExports_EachFilterApplied_AndExportsThatComeRoundEnd()
    {
        static ExtractImport Export(string target) => Import(CodeImportForms.Relative, target) with { Export = true };

        var linked = Link(
            File("lib/barrel.t", [Member("Own", 0)], [Call("Own", "Run", 0)], imports: [Export("src/a.t"), Export("src/b.t") with { Hide = ["Hidden"] }]),
            File("lib/src/a.t", [Member("Run", 0)], imports: [Export("../barrel.t")]),
            File("lib/src/b.t", [Member("Stop", 0), Member("Hidden", 0)], imports: [Export("c.t") with { Show = ["Far"] }]),
            File("lib/src/c.t", [Member("Far", 0), Member("Near", 0)]),
            File("z.t", [Member("z.Run", 0)]),
            File("use.t", [Member("Go", 0)],
                [
                    Call("Go", "Run", 0, Prefix("p")),
                    Call("Go", "Stop", 0, Prefix("p")) with { Line = 2 },
                    Call("Go", "Hidden", 0, Prefix("p")) with { Line = 3 },
                    Call("Go", "Far", 0, Prefix("p")) with { Line = 4 },
                    Call("Go", "Near", 0, Prefix("p")) with { Line = 5 },
                ],
                imports: [Import(CodeImportForms.Relative, "lib/barrel.t") with { Prefix = "p" }]));

        Assert.Equal(
            [("t:Run", 1), ("t:Stop", 2), ("t:Far", 4)],
            linked.Edges.Where(edge => edge is { From: "t:Go", Kind: CodeEdgeKinds.Calls, Confidence: CodeConfidences.Exact }).Select(edge => (edge.To, edge.Line)));
        Assert.Equal(2, linked.External["t:Go"]);
        Assert.Equal(
            ["t:Run", "t:z.Run"],
            linked.Edges.Where(edge => edge is { From: "t:Own", Kind: CodeEdgeKinds.Calls }).Select(edge => edge.To).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A declaration kept to a scope is seen from the declarations written in that scope, or in one inside it, alone: its
    /// file and a file that declares into the scope find it by its name; a file that imports it finds none - by its name,
    /// through a prefix, as a type or in the repository - and an extension kept so is reached from that scope alone.
    /// </summary>
    [Fact]
    public void ADeclarationKeptToAScope_IsSeenFromThatScopeAlone()
    {
        var linked = Link(
            File("lib/a.t",
                [
                    Member("lib.a._run", 0) with { SeenIn = "lib.a" },
                    Type("lib.a.T"), Member("lib.a.T.Go", 0) with { Parent = "t:lib.a.T" },
                    Type("lib.a._Hidden") with { SeenIn = "lib.a" }, Member("lib.a._Hidden.Make", 0),
                    Type("lib.a._Ext") with { SeenIn = "lib.a" }, Member("lib.a._Ext.Shine", 0) with { Extends = "Shape", SeenIn = "lib.a" },
                ],
                [Call("lib.a.T.Go", "_run", 0), Call("lib.a.T.Go", "Shine", 0, Named("Shape")) with { Line = 2 }],
                [Open("lib.a")]),
            File("lib/part.t", [Member("lib.a.Use", 0)], [Call("lib.a.Use", "_run", 0) with { Line = 3 }, Call("lib.a.Use", "Make", 0, Named("_Hidden")) with { Line = 4 }]),
            File("shape.t", [Type("Shape")]),
            File("use.t", [Member("Go", 0)],
                [
                    Call("Go", "_run", 0) with { Line = 5 },
                    Call("Go", "_run", 0, Prefix("p")) with { Line = 6 },
                    Call("Go", "Make", 0, Named("_Hidden")) with { Line = 7 },
                    Call("Go", "Shine", 0, Named("Shape")) with { Line = 8 },
                ],
                imports: [Import(CodeImportForms.Relative, "lib/a.t"), Import(CodeImportForms.Relative, "lib/a.t") with { Prefix = "p" }]));

        Assert.Equal(
            [
                ("t:lib.a.T.Go", "t:lib.a._run", 1, CodeConfidences.Exact),
                ("t:lib.a.T.Go", "t:lib.a._Ext.Shine", 2, CodeConfidences.Unique),
                ("t:lib.a.Use", "t:lib.a._run", 3, CodeConfidences.Exact),
                ("t:lib.a.Use", "t:lib.a._Hidden.Make", 4, CodeConfidences.Exact),
            ],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Calls).Select(edge => (edge.From, edge.To, edge.Line, edge.Confidence)));
        Assert.Equal(4, linked.External["t:Go"]);
    }

    /// <summary>
    /// A file that shares another's imports imports what that one does, after its own - each import's prefix and filters
    /// with it, a relative path read from the file that made it - so a prefix that file writes, written here as a value no
    /// scope holds, is that import; what the shared file shares in turn is not followed, nor is the file it shares
    /// imported by that: an extension that file reaches is not reached from here.
    /// </summary>
    [Fact]
    public void AFileThatSharesAnothersImports_ImportsWhatThatOneDoes_AndNoFurther()
    {
        var linked = Link(
            File("ext/lib.t", [], scopes: [Open("ext")]),
            File("ext/part.t", [Type("ext.StrExt"), Member("ext.StrExt.Shout", 0) with { Extends = "String", Parent = "t:ext.StrExt" }]),
            File("lib/far.t", [], imports: [Import(CodeImportForms.Relative, "../ext/lib.t") with { Shares = true }]),
            File("lib/near.t", [Member("Near", 0)], [Call("Near", "Shout", 0, new ExtractQualifier(CodeQualifierKinds.Type, "String"))], imports: [Import(CodeImportForms.Relative, "far.t") with { Shares = true }]),
            File("lib/lib.t", [], imports: [Import(CodeImportForms.Relative, "src/run.t") with { Prefix = "p" }, Import(CodeImportForms.Relative, "src/stop.t") with { Hide = ["Stop"] }]),
            File("lib/src/run.t", [Member("Run", 0)]),
            File("lib/src/stop.t", [Member("Stop", 0), Member("Halt", 0)]),
            File("other.t", [Member("other.Stop", 0), Member("other.Halt", 0)]),
            File("lib/part.t", [Member("Go", 0)],
                [
                    Call("Go", "Run", 0, Prefix("p")),
                    Call("Go", "Run", 0, new ExtractQualifier(CodeQualifierKinds.Value, "p")) with { Line = 2 },
                    Call("Go", "Halt", 0) with { Line = 3 },
                    Call("Go", "Stop", 0) with { Line = 4 },
                ],
                imports: [Import(CodeImportForms.Relative, "lib.t") with { Shares = true }]),
            File("lib/sub.t", [Member("Deeper", 0)], [Call("Deeper", "Run", 0, Prefix("p"))], imports: [Import(CodeImportForms.Relative, "part.t") with { Shares = true }]));

        Assert.Equal(
            [("t:Run", 1, CodeConfidences.Exact), ("t:Run", 2, CodeConfidences.Exact), ("t:Halt", 3, CodeConfidences.Unique)],
            linked.Edges.Where(edge => edge is { From: "t:Go", Kind: CodeEdgeKinds.Calls } && edge.Line < 4).Select(edge => (edge.To, edge.Line, edge.Confidence)));
        Assert.Equal(["t:Stop", "t:other.Stop"], linked.Edges.Where(edge => edge is { From: "t:Go", Line: 4 }).Select(edge => edge.To).Order(StringComparer.Ordinal));
        Assert.Contains(Edge("file:lib/part.t", "file:lib/lib.t", CodeEdgeKinds.Imports, CodeConfidences.Exact), linked.Edges);
        Assert.DoesNotContain(linked.Edges, edge => edge.From == "t:Deeper" && edge.Kind == CodeEdgeKinds.Calls);
        Assert.DoesNotContain(linked.Edges, edge => edge.From == "t:Near" && edge.Kind == CodeEdgeKinds.Calls);
    }

    /// <summary>
    /// A type written through the prefix a file is imported by - a value's type, a qualifier - is what that import makes
    /// visible of that name, its filter applied: a call on a value of it goes through it.
    /// </summary>
    [Fact]
    public void ATypeWrittenThroughAFilesPrefix_IsWhatThatImportMakesVisibleOfIt()
    {
        var linked = Link(
            File("shape.t", [Type("Shape"), Member("Shape.Draw", 0), Type("Other"), Member("Other.Draw", 0)]),
            File("lib.t",
                [Value("lib.cur", "p.Shape"), Value("lib.hid", "q.Shape"), Member("lib.Go", 0)],
                [
                    Call("lib.Go", "Draw", 0, new ExtractQualifier(CodeQualifierKinds.Value, "cur")),
                    Call("lib.Go", "Draw", 0, new ExtractQualifier(CodeQualifierKinds.Value, "hid")) with { Line = 2 },
                    Call("lib.Go", "Draw", 0, Named("p.Other")) with { Line = 3 },
                ],
                [Open("lib")],
                [Import(CodeImportForms.Relative, "shape.t") with { Prefix = "p" }, Import(CodeImportForms.Relative, "shape.t") with { Prefix = "q", Show = ["Other"] }]));

        Assert.Equal(
            [("t:Shape.Draw", 1), ("t:Other.Draw", 3)],
            linked.Edges.Where(edge => edge is { From: "t:lib.Go", Kind: CodeEdgeKinds.Calls, Confidence: CodeConfidences.Exact }).Select(edge => (edge.To, edge.Line)));
    }

    /// <summary>
    /// A value written with the name of a prefix its file's shared imports make stands for that import alone: where the
    /// import is of no file of the tree, what it names is outside the tree, never a declaration of the name found elsewhere.
    /// </summary>
    [Fact]
    public void AValueNamedAsASharedImportsPrefix_IsThatImportsAlone_OutsideTheTreeWhereItIs()
    {
        var linked = Link(
            File("lib.t", [], imports: [Import(CodeImportForms.Address, "package:far/far.t") with { Prefix = "p" }]),
            File("use.t", [Member("Go", 0)], [Call("Go", "Run", 0, new ExtractQualifier(CodeQualifierKinds.Value, "p"))], imports: [Import(CodeImportForms.Relative, "lib.t") with { Shares = true }]),
            File("run.t", [Member("Run", 0)]));

        Assert.DoesNotContain(linked.Edges, edge => edge is { From: "t:Go", Kind: CodeEdgeKinds.Calls });
        Assert.Equal(1, linked.External["t:Go"]);
    }

    /// <summary>
    /// A type is looked in before its bases and its bases nearest first: the mixins it names, the last first, before what
    /// it inherits and implements, whatever order it names them in - for a call, an override and a dispatch alike - and a
    /// base found among candidates answers with every one of them that holds the name.
    /// </summary>
    [Fact]
    public void ATypesBases_AreLookedInNearestFirst_AMixinBeforeWhatItIsAppliedOver_ACandidateBaseWhole()
    {
        var linked = Link(
            File("i.t", [Type("I"), Member("I.Run", 0) with { Parent = "t:I" }]),
            File("s.t", [Type("S"), Member("S.Run", 0) with { Parent = "t:S" }, Member("S.Stop", 0) with { Parent = "t:S" }]),
            File("m1.t", [Type("M1"), Member("M1.Run", 0) with { Parent = "t:M1" }, Member("M1.Stop", 0) with { Parent = "t:M1" }]),
            File("m2.t", [Type("M2"), Member("M2.Run", 0) with { Parent = "t:M2" }]),
            File("c.t",
                [Type("C"), Member("C.Go", 0) with { Parent = "t:C" }, Member("C.Run", 0) with { Parent = "t:C" }],
                [
                    Ref("C", CodeEdgeKinds.MixesIn, "M1"),
                    Ref("C", CodeEdgeKinds.Inherits, "S") with { Line = 2 },
                    Ref("C", CodeEdgeKinds.MixesIn, "M2") with { Line = 3 },
                    Ref("C", CodeEdgeKinds.Implements, "I") with { Line = 4 },
                    Call("C.Go", "Run", 0, new ExtractQualifier(CodeQualifierKinds.Base)) with { Line = 5 },
                    Call("C.Go", "Stop", 0, new ExtractQualifier(CodeQualifierKinds.This)) with { Line = 6 },
                    new ExtractReference("t:C.Run", CodeEdgeKinds.Overrides, "Run", 7) { Qualifier = new ExtractQualifier(CodeQualifierKinds.Base) },
                ]),
            File("a/base.t", [Type("a.Base"), Member("a.Base.Run", 0) with { Parent = "t:a.Base" }]),
            File("b/base.t", [Type("b.Base"), Member("b.Base.Run", 0) with { Parent = "t:b.Base" }]),
            File("d.t",
                [Type("D"), Member("D.Go", 0) with { Parent = "t:D" }],
                [Ref("D", CodeEdgeKinds.Inherits, "Base"), Call("D.Go", "Run", 0, new ExtractQualifier(CodeQualifierKinds.This)) with { Line = 2 }]),
            File("e.t", [Type("E")], [Ref("E", CodeEdgeKinds.Inherits, "S"), Ref("E", CodeEdgeKinds.MixesIn, "M1") with { Line = 2 }, Ref("E", CodeEdgeKinds.Implements, "I") with { Line = 3 }]));

        Assert.Contains(Edge("t:C.Go", "t:M2.Run", CodeEdgeKinds.Calls, CodeConfidences.Exact) with { Line = 5 }, linked.Edges);
        Assert.Contains(Edge("t:C.Go", "t:M1.Stop", CodeEdgeKinds.Calls, CodeConfidences.Exact) with { Line = 6 }, linked.Edges);
        Assert.Equal(["t:M2.Run"], linked.Edges.Where(edge => edge is { From: "t:C.Run", Kind: CodeEdgeKinds.Overrides }).Select(edge => edge.To));
        Assert.Equal(["t:M1.Run"], linked.Edges.Where(edge => edge is { From: "t:I.Run", Kind: CodeEdgeKinds.Dispatches } && edge.To != "t:C.Run").Select(edge => edge.To));
        Assert.Equal(
            [("t:a.Base.Run", CodeConfidences.Candidates), ("t:b.Base.Run", CodeConfidences.Candidates)],
            linked.Edges.Where(edge => edge is { From: "t:D.Go", Kind: CodeEdgeKinds.Calls }).Select(edge => (edge.To, edge.Confidence)).Order());
    }

    /// <summary>
    /// Past the first level, what a level's types inherit is still one rank, whichever of them names it - two
    /// grandparents that declare the name both answer - and what a mixin is declared on is not looked in where the mixin is
    /// applied: what it is applied over, the type's own base, answers - for a call and a dispatch alike; what a mixin
    /// implements is.
    /// </summary>
    [Fact]
    public void PastTheFirstLevel_WhatALevelInheritsIsOneRank_AndAMixinsOwnBasesAreNotLookedIn()
    {
        static ExtractNode Method(string qualified) => Member(qualified, 0) with { Parent = $"t:{qualified[..qualified.LastIndexOf('.')]}" };

        var linked = Link(
            File("pa.t", [Type("PA"), Method("PA.F")]),
            File("pb.t", [Type("PB"), Method("PB.F")]),
            File("xy.t", [Type("X"), Type("Y")], [Ref("X", CodeEdgeKinds.Inherits, "PA"), Ref("Y", CodeEdgeKinds.Inherits, "PB") with { Line = 2 }]),
            File("z.t", [Type("Z"), Method("Z.G")],
                [Ref("Z", CodeEdgeKinds.Inherits, "X"), Ref("Z", CodeEdgeKinds.Inherits, "Y") with { Line = 2 }, Call("Z.G", "F", 0, new ExtractQualifier(CodeQualifierKinds.This)) with { Line = 3 }]),
            File("i.t", [Type("I"), Method("I.F")]),
            File("a.t", [Type("A"), Method("A.F")]),
            File("b.t", [Type("B"), Method("B.F")], [Ref("B", CodeEdgeKinds.Inherits, "A")]),
            File("sm.t", [Type("S"), Type("M")], [Ref("S", CodeEdgeKinds.Inherits, "B"), Ref("M", CodeEdgeKinds.Inherits, "A") with { Line = 2 }]),
            File("cl.t", [Type("Closeable"), Method("Closeable.Close"), Type("AutoClose"), Type("Resource"), Method("Resource.Go")],
                [
                    Ref("AutoClose", CodeEdgeKinds.Implements, "Closeable"),
                    Ref("Resource", CodeEdgeKinds.MixesIn, "AutoClose") with { Line = 2 },
                    Call("Resource.Go", "Close", 0, new ExtractQualifier(CodeQualifierKinds.This)) with { Line = 3 },
                ]),
            File("c.t", [Type("C"), Type("D"), Method("D.G")],
                [
                    Ref("C", CodeEdgeKinds.Inherits, "S"),
                    Ref("C", CodeEdgeKinds.MixesIn, "M") with { Line = 2 },
                    Ref("C", CodeEdgeKinds.Implements, "I") with { Line = 3 },
                    Ref("D", CodeEdgeKinds.Inherits, "S") with { Line = 4 },
                    Ref("D", CodeEdgeKinds.MixesIn, "M") with { Line = 5 },
                    Call("D.G", "F", 0, new ExtractQualifier(CodeQualifierKinds.This)) with { Line = 6 },
                ]));

        Assert.Equal(
            [("t:PA.F", CodeConfidences.Candidates), ("t:PB.F", CodeConfidences.Candidates)],
            linked.Edges.Where(edge => edge is { From: "t:Z.G", Kind: CodeEdgeKinds.Calls }).Select(edge => (edge.To, edge.Confidence)).Order());
        Assert.Equal([("t:B.F", CodeConfidences.Exact)], linked.Edges.Where(edge => edge is { From: "t:D.G", Kind: CodeEdgeKinds.Calls }).Select(edge => (edge.To, edge.Confidence)));
        Assert.Equal(["t:B.F"], linked.Edges.Where(edge => edge is { From: "t:I.F", Kind: CodeEdgeKinds.Dispatches }).Select(edge => edge.To));
        Assert.Contains(Edge("t:Resource.Go", "t:Closeable.Close", CodeEdgeKinds.Calls, CodeConfidences.Exact) with { Line = 3 }, linked.Edges);
    }

    /// <summary>
    /// A base that two ways up reach answers a dispatch as sure as the surer way, whichever is found first; what a mixin is
    /// declared on is no way up from where the mixin is applied.
    /// </summary>
    [Fact]
    public void ABaseTwoWaysReach_AnswersAsSureAsTheSurerWay_WhicheverComesFirst()
    {
        var linked = Link(
            File("p2.t", [Type("P2"), Type("S2")], [Ref("P2", CodeEdgeKinds.Inherits, "G"), Ref("S2", CodeEdgeKinds.Inherits, "G") with { Line = 2 }]),
            File("n.t",
                [
                    Type("n.G"), Member("n.G.Run", 0) with { Parent = "t:n.G" }, Member("n.G.Go", 0) with { Parent = "t:n.G" }, Type("n.P1"),
                    Type("n.I"), Member("n.I.Run", 0) with { Parent = "t:n.I" }, Type("n.T"),
                    Type("n.J"), Member("n.J.Go", 0) with { Parent = "t:n.J" }, Type("n.M2"), Type("n.C2"),
                ],
                [
                    Ref("n.P1", CodeEdgeKinds.Inherits, "G"),
                    Ref("n.T", CodeEdgeKinds.Implements, "I") with { Line = 2 },
                    Ref("n.T", CodeEdgeKinds.Inherits, "P2") with { Line = 3, Qualifier = Prefix("p") },
                    Ref("n.T", CodeEdgeKinds.Inherits, "P1") with { Line = 4 },
                    Ref("n.M2", CodeEdgeKinds.Inherits, "G") with { Line = 5 },
                    Ref("n.C2", CodeEdgeKinds.Implements, "J") with { Line = 6 },
                    Ref("n.C2", CodeEdgeKinds.MixesIn, "M2") with { Line = 7 },
                    Ref("n.C2", CodeEdgeKinds.Inherits, "S2") with { Line = 8, Qualifier = Prefix("p") },
                ],
                imports: [Import(CodeImportForms.Relative, "p2.t") with { Prefix = "p" }]));

        Assert.Contains(Edge("t:P2", "t:n.G", CodeEdgeKinds.Inherits, CodeConfidences.Unique), linked.Edges);
        Assert.Contains(Edge("t:n.P1", "t:n.G", CodeEdgeKinds.Inherits, CodeConfidences.Exact), linked.Edges);
        Assert.Contains(Edge("t:n.T", "t:n.I", CodeEdgeKinds.Implements, CodeConfidences.Exact) with { Line = 2 }, linked.Edges);
        Assert.Contains(Edge("t:n.T", "t:P2", CodeEdgeKinds.Inherits, CodeConfidences.Exact) with { Line = 3 }, linked.Edges);
        Assert.Contains(Edge("t:n.T", "t:n.P1", CodeEdgeKinds.Inherits, CodeConfidences.Exact) with { Line = 4 }, linked.Edges);
        Assert.Equal([("t:n.G.Run", CodeConfidences.Exact)], linked.Edges.Where(edge => edge is { From: "t:n.I.Run", Kind: CodeEdgeKinds.Dispatches }).Select(edge => (edge.To, edge.Confidence)));
        Assert.Contains(Edge("t:n.M2", "t:n.G", CodeEdgeKinds.Inherits, CodeConfidences.Exact) with { Line = 5 }, linked.Edges);
        Assert.Equal([("t:n.G.Go", CodeConfidences.Unique)], linked.Edges.Where(edge => edge is { From: "t:n.J.Go", Kind: CodeEdgeKinds.Dispatches }).Select(edge => (edge.To, edge.Confidence)));
    }

    /// <summary>
    /// An extension is reached through an import of a file that declares its scope open - a library its part declares it
    /// into - or that exports one that does, each filter on the way letting the extension's own name; a file imported by a
    /// prefix reaches it still, and a scope imported by an alias, which names the scope and imports nothing, does not.
    /// </summary>
    [Fact]
    public void AnExtension_IsReachedThroughWhatAnImportMakesVisible_ItsNameLetByEachFilter()
    {
        CodeGraphFile User(string name, params ExtractImport[] imports)
            => File($"{name}.t", [Member($"{name}.Go", 0)], [Call($"{name}.Go", "Shout", 0, new ExtractQualifier(CodeQualifierKinds.Type, "String"))], imports: imports);

        var library = Import(CodeImportForms.Relative, "ext/lib.t");
        var barrel = Import(CodeImportForms.Relative, "barrel.t");

        var linked = Link(
            File("ext/lib.t", [], scopes: [Open("ext")]),
            File("ext/part.t", [Type("ext.StrExt"), Member("ext.StrExt.Shout", 0) with { Extends = "String", Parent = "t:ext.StrExt" }]),
            File("barrel.t", [], imports: [library with { Export = true }]),
            User("a", library),
            User("b", library with { Hide = ["StrExt"] }),
            User("c", barrel),
            User("d", barrel with { Show = ["Other"] }),
            User("e", library with { Prefix = "p" }),
            User("f", Import(CodeImportForms.Scope, "ext") with { Prefix = "E" }),
            User("g", Import(CodeImportForms.Scope, "ext")));

        Assert.Equal(
            ["t:a.Go", "t:c.Go", "t:e.Go", "t:g.Go"],
            linked.Edges.Where(edge => edge is { To: "t:ext.StrExt.Shout", Kind: CodeEdgeKinds.Calls }).Select(edge => edge.From).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A type whose own scope is closed is its file's alone, as what it declares is: a call in its file finds it, and one
    /// in another file - by its name, its whole qualified name, as the ending of one or as a type it names - finds none,
    /// nor an extension of it.
    /// </summary>
    [Fact]
    public void ATypeWhoseOwnScopeIsClosed_IsItsFilesAlone()
    {
        static CodeGraphFile Local(string path, IReadOnlyList<ExtractReference>? references = null, IReadOnlyList<ExtractNode>? more = null)
            => File(path,
                [Type("app.Helpers") with { Id = $"t:{path}:app.Helpers" }, Member("app.Helpers.Run", 0) with { Id = $"t:{path}:app.Helpers.Run" }, Type($"app.{path[0]}"), Member($"app.{path[0]}.Go", 0), .. more ?? []],
                references,
                [Open("app"), new ExtractScope("app.Helpers", Open: false) { Parent = "app" }],
                [Import(CodeImportForms.Scope, "app.Helpers")]);

        var linked = Link(
            Local("a.t",
                [Call("app.a.Go", "Run", 0, Named("Helpers")), Call("app.a.Go", "Describe", 0, Named("Helpers")) with { Line = 2 }],
                [Type("app.AExt"), Member("app.AExt.Describe", 0) with { Extends = "Helpers" }]),
            Local("b.t"),
            File("c.t", [Type("app.C"), Member("app.C.Go", 0)],
                [
                    Call("app.C.Go", "Run", 0, Named("Helpers")),
                    Call("app.C.Go", "Run", 0, Named("app.Helpers")) with { Line = 2 },
                    Call("app.C.Go", "Run", 0) with { Line = 3 },
                    Ref("app.C.Go", CodeEdgeKinds.References, "Helpers") with { Line = 4 },
                    Call("app.C.Go", "Describe", 0, Named("app.Helpers")) with { Line = 5 },
                    Call("app.C.Go", "Describe", 0, Named("Helpers")) with { Line = 6 },
                ],
                [Open("app")],
                [Import(CodeImportForms.Scope, "app.Helpers")]));

        Assert.Equal(
            [("t:a.t:app.Helpers.Run", 1, CodeConfidences.Exact), ("t:app.AExt.Describe", 2, CodeConfidences.Unique)],
            linked.Edges.Where(edge => edge is { From: "t:app.a.Go", Kind: CodeEdgeKinds.Calls }).Select(edge => (edge.To, edge.Line, edge.Confidence)));
        Assert.DoesNotContain(linked.Edges, edge => edge is { From: "t:app.C.Go", Kind: CodeEdgeKinds.Calls });
        Assert.Equal(6, linked.External["t:app.C.Go"]);
        Assert.Equal(
            [("file:a.t", "t:a.t:app.Helpers"), ("file:b.t", "t:b.t:app.Helpers")],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Imports).Select(edge => (edge.From, edge.To)));
    }

    /// <summary>
    /// A member answers an interface's and a body declares its prototype where they take the same parameters, as far as
    /// their counts say: a default one gives and the other does not changes no parameter, a different most does.
    /// </summary>
    [Fact]
    public void ADefaultGivenInOneAlone_ChangesNoParameter_ForADispatchOrADeclaration()
    {
        var linked = Link(File("a.t",
            [
                Type("I"), Member("I.Run", 1) with { MinArity = 0 },
                Type("C"), Member("C.Run", 1),
                Type("D"), Member("D.Run", 2),
                Member("Work", 2) with { Prototype = true, MinArity = 1 }, Member("Work", 2) with { Id = "t:Work~2" },
            ],
            [Ref("C", CodeEdgeKinds.Implements, "I"), Ref("D", CodeEdgeKinds.Implements, "I")]));

        Assert.Contains(linked.Edges, edge => edge is { From: "t:I.Run", To: "t:C.Run", Kind: CodeEdgeKinds.Dispatches });
        Assert.DoesNotContain(linked.Edges, edge => edge is { From: "t:I.Run", To: "t:D.Run" });
        Assert.Contains(linked.Edges, edge => edge is { From: "t:Work", To: "t:Work~2", Kind: CodeEdgeKinds.Declares });
    }

    /// <summary>
    /// An interface's member dispatches to the member of the same name and arity of each type that implements it, and of
    /// each type derived from one, as sure as the implementing edge.
    /// </summary>
    [Fact]
    public void AnInterfacesMember_DispatchesToEveryImplementation_AndToDerivedTypes()
    {
        var linked = Link(File("a.t",
            [Type("I"), Member("I.Run", 0), Type("C"), Member("C.Run", 0), Member("C.Run1", 1) with { Name = "Run" }, Type("D"), Member("D.Run", 0), Type("E"), Member("E.Run", 0)],
            [Ref("C", CodeEdgeKinds.Implements, "I"), Ref("D", CodeEdgeKinds.Inherits, "C")]));

        Assert.Contains(Edge("t:I.Run", "t:C.Run", CodeEdgeKinds.Dispatches, CodeConfidences.Unique), linked.Edges);
        Assert.Contains(Edge("t:I.Run", "t:D.Run", CodeEdgeKinds.Dispatches, CodeConfidences.Unique), linked.Edges);
        Assert.DoesNotContain(linked.Edges, edge => edge.Kind == CodeEdgeKinds.Dispatches && edge.To is "t:C.Run1" or "t:E.Run");
    }

    /// <summary>
    /// A declaration with no body declares the one with a body, the same qualified name and arity, of its language - unique
    /// for one, candidates for several - and never one in another file's closed scope.
    /// </summary>
    [Fact]
    public void APrototype_DeclaresItsBody_OfItsLanguage_AndNeverAnotherFilesClosedOne()
    {
        var linked = Link(
            File("a.h", [Member("ns.Run", 0) with { Scope = "ns", Prototype = true }, Member("ns.Two", 0) with { Scope = "ns", Prototype = true }], scopes: [Open("ns")]),
            File("a.c", [Member("ns.Run", 0) with { Id = "t:ns.Run@a.c", Scope = "ns" }, Member("ns.Two", 0) with { Id = "t:ns.Two@a.c", Scope = "ns" }], scopes: [Open("ns")]),
            File("b.c", [Member("ns.Two", 0) with { Id = "t:ns.Two@b.c", Scope = "ns" }, Member("ns.Run", 0) with { Id = "t:ns.Run@b.c", Scope = "ns" }], scopes: [new ExtractScope("ns", Open: false)]),
            File("c.c", [Member("ns.Two", 0) with { Id = "t:ns.Two@c.c", Scope = "ns" }], scopes: [Open("ns")]),
            new CodeGraphFile("a.u", "u", Extract([new ExtractNode("u:ns.Run", CodeNodeKinds.Member, "Run", "ns.Run", 1, 1, "Run") { MinArity = 0, MaxArity = 0 }])));

        Assert.Contains(Edge("t:ns.Run", "t:ns.Run@a.c", CodeEdgeKinds.Declares, CodeConfidences.Unique), linked.Edges);
        Assert.Contains(Edge("t:ns.Two", "t:ns.Two@a.c", CodeEdgeKinds.Declares, CodeConfidences.Candidates), linked.Edges);
        Assert.Contains(Edge("t:ns.Two", "t:ns.Two@c.c", CodeEdgeKinds.Declares, CodeConfidences.Candidates), linked.Edges);
        Assert.DoesNotContain(linked.Edges, edge => edge.Kind == CodeEdgeKinds.Declares && edge.To is "t:ns.Run@b.c" or "t:ns.Two@b.c" or "u:ns.Run");
    }

    /// <summary>A test gives a <c>tests</c> edge to each member it calls directly, as sure as the call; a member that is no test gives none.</summary>
    [Fact]
    public void ATest_TestsEachMemberItCalls_AsSureAsTheCall()
    {
        var linked = Link(
            File("lib.t", [Member("lib.Run", 0) with { Scope = "lib" }, Member("lib.Two", 0) with { Scope = "lib" }], scopes: [Open("lib")]),
            File("lib2.t", [Member("lib2.Two", 0) with { Scope = "lib2" }], scopes: [Open("lib2")]),
            File("test.t", [Member("Checks", 0) with { Test = true }, Member("Helper", 0)],
                [Call("Checks", "Run", 0), Call("Checks", "Two", 0) with { Line = 2 }, Call("Helper", "Run", 0) with { Line = 3 }]));

        Assert.Contains(new CodeEdge("t:Checks", "t:lib.Run", CodeEdgeKinds.Tests, 1, CodeConfidences.Unique), linked.Edges);
        Assert.Contains(new CodeEdge("t:Checks", "t:lib.Two", CodeEdgeKinds.Tests, 2, CodeConfidences.Candidates), linked.Edges);
        Assert.Contains(new CodeEdge("t:Checks", "t:lib2.Two", CodeEdgeKinds.Tests, 2, CodeConfidences.Candidates), linked.Edges);
        Assert.DoesNotContain(linked.Edges, edge => edge.From == "t:Helper" && edge.Kind == CodeEdgeKinds.Tests);
    }

    /// <summary>
    /// Every declaration is contained by its parent node, or its file; a type of several parts is one symbol naming each
    /// file, and its members of either part are found through it; every file is a symbol, read or not.
    /// </summary>
    [Fact]
    public void ContainsEdges_Symbols_AndATypeOfSeveralParts()
    {
        var linked = Link(
            File("p1.t", [Type("app.P"), Member("app.P.One", 0) with { Parent = "t:app.P" }], scopes: [Open("app")]),
            File("p2.t", [Type("app.P"), Member("app.P.Two", 0) with { Parent = "t:app.P" }], [Call("app.P.Two", "One", 0, Named("P"))], [Open("app")]),
            new CodeGraphFile("logo.png", null, null));

        Assert.Contains(Edge("file:p1.t", "t:app.P", CodeEdgeKinds.Contains, CodeConfidences.Exact), linked.Edges);
        Assert.Contains(Edge("t:app.P", "t:app.P.Two", CodeEdgeKinds.Contains, CodeConfidences.Exact), linked.Edges);
        Assert.Contains(Edge("t:app.P.Two", "t:app.P.One", CodeEdgeKinds.Calls, CodeConfidences.Exact), linked.Edges);
        Assert.Equal(["p1.t", "p2.t"], Assert.Single(linked.Symbols, symbol => symbol.Id == "t:app.P").Paths);
        Assert.Contains(new CodeGraphSymbol("file:logo.png", CodeNodeKinds.File, ["logo.png"]), linked.Symbols, SymbolComparer.Instance);
        Assert.Equal(linked.Edges.Count, linked.Edges.Distinct().Count());
    }

    /// <summary>
    /// The extracts keep names and not targets: a file that changes to declare a name binds every caller of it in files
    /// that did not change, the next time the tree is linked.
    /// </summary>
    [Fact]
    public void AChangedFilesNewName_BindsAnUnchangedCaller_WhenLinkedAgain()
    {
        var caller = File("caller.t", [Member("Go", 0)], [Call("Go", "Fresh", 0)]);

        var before = Link(caller, File("callee.t", [Member("lib.Old", 0) with { Scope = "lib" }], scopes: [Open("lib")]));
        var after = Link(caller, File("callee.t", [Member("lib.Fresh", 0) with { Scope = "lib" }], scopes: [Open("lib")]));

        Assert.Equal(1, before.External["t:Go"]);
        Assert.Contains(Edge("t:Go", "t:lib.Fresh", CodeEdgeKinds.Calls, CodeConfidences.Unique), after.Edges);
        Assert.Empty(after.External);
    }

    /// <summary>
    /// A qualifier that names no container, by its qualified name or as a reference finds a name, falls back to every
    /// container whose qualified name ends with it - at a name's boundary, never inside one.
    /// </summary>
    [Fact]
    public void AQualifierNamingNoContainer_IsEveryOneEndingWithIt_AtANamesBoundary()
    {
        var linked = Link(
            File("a.t", [Type("deep.inner.Box"), Member("deep.inner.Box.Open", 0)], scopes: [Open("deep.inner")]),
            File("b.t", [Type("deep.inner.MyBox"), Member("deep.inner.MyBox.Open", 0)], scopes: [Open("deep.inner")]),
            File("use.t", [Member("Go", 0)], [Call("Go", "Open", 0, Named("inner.Box")), Call("Go", "Open", 0, Named("yBox")) with { Line = 2 }]));

        Assert.Contains(Edge("t:Go", "t:deep.inner.Box.Open", CodeEdgeKinds.Calls, CodeConfidences.Exact), linked.Edges);
        Assert.DoesNotContain(linked.Edges, edge => edge is { From: "t:Go", To: "t:deep.inner.MyBox.Open" });
        Assert.Equal(1, linked.External["t:Go"]);
    }

    /// <summary>Bases that name each other and scopes that hold each other end every look: a name is found through them, or counted external.</summary>
    [Fact]
    public void BasesAndScopesInACycle_EndEveryLook()
    {
        var linked = Link(
            File("a.t", [Type("A"), Member("A.Go", 0) with { Parent = "t:A" }, Type("B"), Member("B.OnB", 0), Type("C"), Member("C.Run", 0), Type("I"), Member("I.Run", 0)],
                [
                    Ref("A", CodeEdgeKinds.Inherits, "B"),
                    Ref("B", CodeEdgeKinds.Inherits, "A"),
                    Ref("C", CodeEdgeKinds.Implements, "I"),
                    Ref("I", CodeEdgeKinds.Inherits, "C"),
                    Call("A.Go", "OnB", 0),
                    Call("A.Go", "Missing", 0) with { Line = 2 },
                    Call("A.Go", "Missing", 0, new ExtractQualifier(CodeQualifierKinds.This)) with { Line = 3 },
                ]),
            File("s.t", [Member("x.InX", 0), Member("y.InY", 0)], [Call("x.InX", "InY", 0), Call("x.InX", "Nowhere", 0) with { Line = 2 }],
                [new ExtractScope("x", Open: true) { Parent = "y" }, new ExtractScope("y", Open: true) { Parent = "x" }]));

        Assert.Contains(Edge("t:A.Go", "t:B.OnB", CodeEdgeKinds.Calls, CodeConfidences.Exact), linked.Edges);
        Assert.Equal(2, linked.External["t:A.Go"]);
        Assert.Contains(Edge("t:x.InX", "t:y.InY", CodeEdgeKinds.Calls, CodeConfidences.Exact), linked.Edges);
        Assert.Equal(1, linked.External["t:x.InX"]);

        // A type is never an interface of its own, though its bases lead back to it.
        Assert.Contains(Edge("t:I.Run", "t:C.Run", CodeEdgeKinds.Dispatches, CodeConfidences.Unique), linked.Edges);
        Assert.DoesNotContain(linked.Edges, edge => edge.Kind == CodeEdgeKinds.Dispatches && edge.From == edge.To);
    }

    /// <summary>
    /// Every declaration of a name is an edge to each, however many; an import by a path's ending is an edge to each file
    /// it names, however many - none counted anywhere, none dropped.
    /// </summary>
    [Fact]
    public void EveryCandidate_IsAnEdge_AndAnEndingIsAnImportOfEachFileItNames()
    {
        var five = Enumerable.Range(1, 5).Select(at => File($"f{at}/five.h", [Member($"f{at}.Five", 0) with { Scope = $"f{at}" }], scopes: [Open($"f{at}")]));
        var many = Enumerable.Range(1, 40).Select(at => File($"s{at}/many.h", []));
        var linked = Link([
            .. five,
            .. many,
            File("use.t", [Member("Go", 0)], [Call("Go", "Five", 0)], imports: [Import(CodeImportForms.Ending, "five.h"), Import(CodeImportForms.Ending, "many.h") with { Line = 2 }]),
        ]);

        Assert.Equal(5, linked.Edges.Count(edge => edge is { From: "t:Go", Kind: CodeEdgeKinds.Calls, Confidence: CodeConfidences.Candidates }));
        Assert.Equal(5, linked.Edges.Count(edge => edge is { From: "file:use.t", Kind: CodeEdgeKinds.Imports, Line: 1, Confidence: CodeConfidences.Candidates }));
        Assert.Equal(
            [.. Enumerable.Range(1, 40).Select(at => $"file:s{at}/many.h").Order(StringComparer.Ordinal)],
            linked.Edges.Where(edge => edge is { From: "file:use.t", Kind: CodeEdgeKinds.Imports, Line: 2, Confidence: CodeConfidences.Candidates }).Select(edge => edge.To).Order(StringComparer.Ordinal));
        Assert.Empty(linked.External);
    }

    /// <summary>
    /// A mixin is a base, an edge of its own, whose members are found through it; an override reaches the base's member of
    /// its name and arity; a project reference reaches a project and nothing else.
    /// </summary>
    [Fact]
    public void MixinsOverridesAndProjectReferences_ReachWhatTheyName()
    {
        var linked = Link(
            File("a.t",
                [Type("M"), Member("M.FromMixin", 0), Type("C"), Member("C.Run", 0), Member("C.Go", 0) with { Parent = "t:C" }, Type("D"), Member("D.Run", 0) with { Parent = "t:D" }],
                [
                    Ref("C", CodeEdgeKinds.MixesIn, "M"),
                    Ref("D", CodeEdgeKinds.Inherits, "C"),
                    Call("C.Go", "FromMixin", 0),
                    new ExtractReference("t:D.Run", CodeEdgeKinds.Overrides, "Run", 2) { Arity = 0, Qualifier = new ExtractQualifier(CodeQualifierKinds.Base) },
                ]),
            File("app.proj", [new ExtractNode("t:app", CodeNodeKinds.Project, "app", "app", 1, 1, "app"), Type("lib")], [Ref("app", CodeEdgeKinds.ProjectReference, "lib")]),
            File("lib.proj", [new ExtractNode("t:libproject", CodeNodeKinds.Project, "lib", "lib", 1, 1, "lib")]));

        Assert.Contains(Edge("t:C", "t:M", CodeEdgeKinds.MixesIn, CodeConfidences.Unique), linked.Edges);
        Assert.Contains(Edge("t:C.Go", "t:M.FromMixin", CodeEdgeKinds.Calls, CodeConfidences.Exact), linked.Edges);
        Assert.Contains(new CodeEdge("t:D.Run", "t:C.Run", CodeEdgeKinds.Overrides, 2, CodeConfidences.Exact), linked.Edges);
        Assert.Contains(Edge("t:app", "t:libproject", CodeEdgeKinds.ProjectReference, CodeConfidences.Unique), linked.Edges);
        Assert.DoesNotContain(linked.Edges, edge => edge is { From: "t:app", To: "t:lib" });
    }

    /// <summary>
    /// An interface's member dispatches, for a type that does not declare it, to its nearest base's member - never to the
    /// interface's own - and to a derived type's own.
    /// </summary>
    [Fact]
    public void AnInterfacesMember_DispatchesThroughABasesMember()
    {
        var linked = Link(File("a.t",
            [Type("I"), Member("I.Run", 0), Type("B"), Member("B.Run", 0), Type("C"), Type("D"), Member("D.Run", 0)],
            [Ref("C", CodeEdgeKinds.Implements, "I"), Ref("C", CodeEdgeKinds.Inherits, "B"), Ref("D", CodeEdgeKinds.Inherits, "C")]));

        Assert.Contains(Edge("t:I.Run", "t:B.Run", CodeEdgeKinds.Dispatches, CodeConfidences.Unique), linked.Edges);
        Assert.Contains(Edge("t:I.Run", "t:D.Run", CodeEdgeKinds.Dispatches, CodeConfidences.Unique), linked.Edges);
        Assert.DoesNotContain(linked.Edges, edge => edge is { Kind: CodeEdgeKinds.Dispatches, To: "t:I.Run" });
    }

    /// <summary>A global import by a relative path is read from the file that makes it, whichever file it is looked in from.</summary>
    [Fact]
    public void AGlobalRelativeImport_IsReadFromTheFileThatMakesIt()
    {
        var linked = Link(
            File("cfg/usings.t", [], imports: [Import(CodeImportForms.Relative, "../lib/tool.t") with { Global = true }]),
            File("lib/tool.t", [new ExtractNode("t:lib/Tool", CodeNodeKinds.Type, "Tool", "Tool", 1, 1, "Tool")]),
            File("other/tool.t", [new ExtractNode("t:other/Tool", CodeNodeKinds.Type, "Tool", "Tool", 1, 1, "Tool")]),
            File("deep/dir/use.t", [Member("Go", 0)], [Ref("Go", CodeEdgeKinds.References, "Tool")]));

        Assert.Contains(Edge("t:Go", "t:lib/Tool", CodeEdgeKinds.References, CodeConfidences.Unique), linked.Edges);
        Assert.DoesNotContain(linked.Edges, edge => edge is { From: "t:Go", To: "t:other/Tool" });
        Assert.Contains(Edge("file:cfg/usings.t", "file:lib/tool.t", CodeEdgeKinds.Imports, CodeConfidences.Exact), linked.Edges);
    }

    /// <summary>
    /// Address roots are tried the longest prefix first, and two of one prefix in one order - their files' paths' - whatever
    /// order the files that declare them come in.
    /// </summary>
    [Fact]
    public void AddressRoots_AreTriedLongestFirst_AndTwoAlikeInOneOrder()
    {
        CodeGraphFile Root(string path, string prefix, string directory)
            => File(path, []) with { Extract = new FileExtract([]) { AddressRoots = [new(prefix, directory)] } };

        CodeGraphFile[] files =
        [
            Root("one.t", "pkg:", "one"),
            Root("two.t", "pkg:", "two"),
            Root("sub.t", "pkg:sub/", "three"),
            File("one/x.t", []),
            File("two/x.t", []),
            File("three/y.t", []),
            File("one/sub/y.t", []),
            File("use.t", [], imports: [Import(CodeImportForms.Address, "pkg:x.t"), Import(CodeImportForms.Address, "pkg:sub/y.t") with { Line = 2 }]),
        ];

        foreach (var order in new CodeGraphFile[][] { files, [.. Enumerable.Reverse(files)] })
        {
            var linked = Link(order);

            Assert.Contains(new CodeEdge("file:use.t", "file:one/x.t", CodeEdgeKinds.Imports, 1, CodeConfidences.Exact), linked.Edges);
            Assert.Contains(new CodeEdge("file:use.t", "file:three/y.t", CodeEdgeKinds.Imports, 2, CodeConfidences.Exact), linked.Edges);
            Assert.Equal(2, linked.Edges.Count(edge => edge is { From: "file:use.t", Kind: CodeEdgeKinds.Imports }));
        }
    }

    /// <summary>
    /// A whole address root stands for its address alone - none longer, none it begins - and is a file's; roots of one
    /// prefix are tried in the order their file declares them until one names a file the tree holds, a root that names a
    /// directory or a file it does not hold passed over.
    /// </summary>
    [Fact]
    public void AWholeAddressRoot_StandsForItsAddressAlone_AndRootsAreTriedUntilOneNamesAFile()
    {
        var linked = Link(
            File("pkg.t", []) with
            {
                Extract = new FileExtract([])
                {
                    AddressRoots = [new("pkg", "src/missing.t") { Whole = true }, new("pkg", "src") { Whole = true }, new("pkg", "src/main.t") { Whole = true }, new("pkg", "dist/main.t") { Whole = true }],
                },
            },
            File("src/main.t", []),
            File("dist/main.t", []),
            File("use.t", [], imports: [Import(CodeImportForms.Address, "pkg"), Import(CodeImportForms.Address, "pkg/main.t") with { Line = 2 }, Import(CodeImportForms.Address, "pkgs") with { Line = 3 }]));

        Assert.Equal(
            [new CodeEdge("file:use.t", "file:src/main.t", CodeEdgeKinds.Imports, 1, CodeConfidences.Exact)],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Imports));
    }

    /// <summary>
    /// A name a declaration is exported by besides its own is what an import of its file by that name finds - by a prefix
    /// or by what it shows, as exact or as sure as its own name would be - and nothing else: its own file, a file that
    /// imports it by no such name, and a reference of that name nowhere imported find nothing by it.
    /// </summary>
    [Fact]
    public void ANameADeclarationIsExportedBy_IsWhatAnImportOfItsFileFindsByIt()
    {
        // Its own name kept to its file's scope: an importer by an export's name still finds it.
        var shape = File("shape.t", [Member("Make", 0) with { Exports = ["default", "build"], SeenIn = "shape" }, Member("Self", 0)], [Call("Self", "default", 0) with { From = "t:Self" }], [Open("shape")]);
        var linked = Link(
            shape,
            File("prefixed.t", [Member("Go", 0)], [Call("Go", "default", 0, Prefix("S"))], imports: [Import(CodeImportForms.Relative, "shape.t") with { Prefix = "S", Show = ["default"] }]),
            File("shown.t", [Member("Go2", 0)], [Call("Go2", "build", 0)], imports: [Import(CodeImportForms.Relative, "shape.t") with { Show = ["build"] }]),
            File("none.t", [Member("Go3", 0)], [Call("Go3", "build", 0)]));

        Assert.Contains(Edge("t:Go", "t:Make", CodeEdgeKinds.Calls, CodeConfidences.Exact), linked.Edges);
        Assert.Contains(Edge("t:Go2", "t:Make", CodeEdgeKinds.Calls, CodeConfidences.Unique), linked.Edges);
        Assert.DoesNotContain(linked.Edges, edge => edge.From is "t:Self" or "t:Go3" && edge.Kind == CodeEdgeKinds.Calls);

        // By its own name, kept to its scope, no importer finds it.
        var own = Link(shape, File("own.t", [Member("Go4", 0)], [Call("Go4", "Make", 0)], imports: [Import(CodeImportForms.Relative, "shape.t") with { Show = ["Make"] }]));

        Assert.DoesNotContain(own.Edges, edge => edge is { From: "t:Go4", Kind: CodeEdgeKinds.Calls });
    }

    /// <summary>
    /// A local address root gives its meaning to the addresses its own file's directory's files import alone, before any
    /// root that is every file's, the nearest file's first - whatever prefix a farther one's is - and a file in no such
    /// directory is given none by it.
    /// </summary>
    [Fact]
    public void ALocalAddressRoot_IsItsDirectorysFilesAlone_TheNearestFirst_BeforeEveryFilesRoot()
    {
        CodeGraphFile Roots(string path, params ExtractAddressRoot[] roots) => File(path, []) with { Extract = new FileExtract([]) { AddressRoots = roots } };

        var linked = Link(
            Roots("pkg.t", new ExtractAddressRoot("@x/", "global")),
            Roots("apps/cfg.t", new ExtractAddressRoot("@x/", "outer") { Local = true }, new ExtractAddressRoot("@x/deep/", "deeper") { Local = true }),
            Roots("apps/in/cfg.t", new ExtractAddressRoot("@x/", "near") { Local = true }),
            File("global/y.t", []),
            File("apps/outer/y.t", []),
            File("apps/outer/z.t", []),
            File("apps/in/near/y.t", []),
            File("apps/deeper/w.t", []),
            File("apps/in/use.t", [], imports: [Import(CodeImportForms.Address, "@x/y.t"), Import(CodeImportForms.Address, "@x/z.t") with { Line = 2 }, Import(CodeImportForms.Address, "@x/deep/w.t") with { Line = 3 }]),
            File("apps/use.t", [], imports: [Import(CodeImportForms.Address, "@x/y.t")]),
            File("other/use.t", [], imports: [Import(CodeImportForms.Address, "@x/y.t")]));

        Assert.Equal(
            [
                ("file:apps/in/use.t", "file:apps/deeper/w.t", 3), ("file:apps/in/use.t", "file:apps/in/near/y.t", 1), ("file:apps/in/use.t", "file:apps/outer/z.t", 2),
                ("file:apps/use.t", "file:apps/outer/y.t", 1), ("file:other/use.t", "file:global/y.t", 1),
            ],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Imports).Select(edge => (edge.From, edge.To, edge.Line)).Order());
    }

    /// <summary>
    /// A concrete type another implements still answers its interface's member; the type implementing it in turn answers
    /// both, and so does a type mixing it in; and the member of an interface reaches every type implementing one that extends
    /// it - never an interface's own.
    /// </summary>
    [Fact]
    public void Dispatch_ReachesThroughImplementedTypes_AndExtendedInterfaces()
    {
        var linked = Link(File("a.t",
            [Type("Repo"), Member("Repo.Run", 0), Type("Impl"), Member("Impl.Run", 0), Type("Mock"), Member("Mock.Run", 0), Type("Mixed"), Member("Mixed.Run", 0), Type("IA"), Member("IA.Go", 0), Type("IB"), Type("C"), Member("C.Go", 0)],
            [Ref("Impl", CodeEdgeKinds.Implements, "Repo"), Ref("Mock", CodeEdgeKinds.Implements, "Impl"), Ref("Mixed", CodeEdgeKinds.MixesIn, "Impl"), Ref("IB", CodeEdgeKinds.Inherits, "IA"), Ref("C", CodeEdgeKinds.Implements, "IB")]));

        Assert.Contains(Edge("t:Repo.Run", "t:Impl.Run", CodeEdgeKinds.Dispatches, CodeConfidences.Unique), linked.Edges);
        Assert.Contains(Edge("t:Repo.Run", "t:Mock.Run", CodeEdgeKinds.Dispatches, CodeConfidences.Unique), linked.Edges);
        Assert.Contains(Edge("t:Impl.Run", "t:Mock.Run", CodeEdgeKinds.Dispatches, CodeConfidences.Unique), linked.Edges);
        Assert.Contains(Edge("t:Repo.Run", "t:Mixed.Run", CodeEdgeKinds.Dispatches, CodeConfidences.Unique), linked.Edges);
        Assert.Contains(Edge("t:IA.Go", "t:C.Go", CodeEdgeKinds.Dispatches, CodeConfidences.Unique), linked.Edges);
        Assert.DoesNotContain(linked.Edges, edge => edge.Kind == CodeEdgeKinds.Dispatches && edge.To is "t:Repo.Run" or "t:IA.Go");
    }

    /// <summary>
    /// A dispatch is as sure as the least sure edge on its way: a type that inherits one of two types of a name, one of
    /// which implements an interface exactly, answers that interface's member only as one of the candidates; a member
    /// found up a base of two of a name answers as one of them; and two bases it inherits that answer alike each answer as
    /// one.
    /// </summary>
    [Fact]
    public void ADispatch_IsAsSureAsTheLeastSureEdgeOnItsWay()
    {
        var linked = Link(File("a.t",
            [
                Type("n.Repo"), Member("n.Repo.Run", 0), Type("n.Impl"), Member("n.Impl.Run", 0), Type("x.Impl"), Type("m.Mock"), Member("m.Mock.Run", 0),
                Type("n.T"), Type("p.Base"), Member("p.Base.Run", 0), Type("r.Base"),
                Type("n.U"), Type("n.A"), Member("n.A.Run", 0), Type("n.B"), Member("n.B.Run", 0),
            ],
            [
                Ref("n.Impl", CodeEdgeKinds.Implements, "Repo"), Ref("m.Mock", CodeEdgeKinds.Inherits, "Impl"),
                Ref("n.T", CodeEdgeKinds.Implements, "Repo"), Ref("n.T", CodeEdgeKinds.Inherits, "Base"),
                Ref("n.U", CodeEdgeKinds.Implements, "Repo"), Ref("n.U", CodeEdgeKinds.Inherits, "A"), Ref("n.U", CodeEdgeKinds.Inherits, "B"),
            ]));

        Assert.Contains(Edge("t:n.Impl", "t:n.Repo", CodeEdgeKinds.Implements, CodeConfidences.Exact), linked.Edges);
        Assert.Contains(Edge("t:m.Mock", "t:n.Impl", CodeEdgeKinds.Inherits, CodeConfidences.Candidates), linked.Edges);
        Assert.Contains(Edge("t:n.Repo.Run", "t:n.Impl.Run", CodeEdgeKinds.Dispatches, CodeConfidences.Exact), linked.Edges);
        Assert.Equal(CodeConfidences.Candidates, Assert.Single(linked.Edges, edge => edge is { Kind: CodeEdgeKinds.Dispatches, To: "t:m.Mock.Run" }).Confidence);

        Assert.Contains(Edge("t:n.T", "t:p.Base", CodeEdgeKinds.Inherits, CodeConfidences.Candidates), linked.Edges);
        Assert.Equal(CodeConfidences.Candidates, Assert.Single(linked.Edges, edge => edge is { Kind: CodeEdgeKinds.Dispatches, To: "t:p.Base.Run" }).Confidence);

        Assert.Contains(Edge("t:n.U", "t:n.A", CodeEdgeKinds.Inherits, CodeConfidences.Exact), linked.Edges);
        Assert.Equal(CodeConfidences.Candidates, Assert.Single(linked.Edges, edge => edge is { Kind: CodeEdgeKinds.Dispatches, To: "t:n.A.Run" }).Confidence);
        Assert.Equal(CodeConfidences.Candidates, Assert.Single(linked.Edges, edge => edge is { Kind: CodeEdgeKinds.Dispatches, To: "t:n.B.Run" }).Confidence);
    }

    /// <summary>
    /// Of two ways up to an interface, the surer decides, though the less sure one is found first: a nearer base that
    /// implements one of two interfaces of a name, and a farther one that implements it in its own scope.
    /// </summary>
    [Fact]
    public void ADispatch_TakesTheSurestWayUp_ThoughALessSureOneIsFoundFirst()
    {
        var linked = Link(File("a.t",
            [Type("b.T"), Member("b.T.Run", 0), Type("b.B1"), Type("b.B2"), Type("s.I"), Member("s.I.Run", 0), Type("s.B3"), Type("u.I")],
            [
                Ref("b.T", CodeEdgeKinds.Inherits, "B1"), Ref("b.T", CodeEdgeKinds.Inherits, "B2"),
                Ref("b.B1", CodeEdgeKinds.Implements, "I"), Ref("b.B2", CodeEdgeKinds.Inherits, "B3"), Ref("s.B3", CodeEdgeKinds.Implements, "I"),
            ]));

        Assert.Contains(Edge("t:b.B1", "t:s.I", CodeEdgeKinds.Implements, CodeConfidences.Candidates), linked.Edges);
        Assert.Contains(Edge("t:b.B2", "t:s.B3", CodeEdgeKinds.Inherits, CodeConfidences.Unique), linked.Edges);
        Assert.Equal(CodeConfidences.Unique, Assert.Single(linked.Edges, edge => edge is { Kind: CodeEdgeKinds.Dispatches, From: "t:s.I.Run" }).Confidence);
    }

    /// <summary>
    /// An interface that extends another by an implements edge is no type that answers it: its member, which has no body,
    /// answers nothing; and the type implementing it answers both - the other's member once, as sure as the less sure of
    /// the two edges up to it.
    /// </summary>
    [Fact]
    public void AnInterfaceExtendedByImplements_AnswersNothing_AndItsImplementerAnswersBothOnce()
    {
        var linked = Link(File("a.t",
            [Type("p.IA"), Member("p.IA.Go", 0), Type("q.IB"), Member("q.IB.Go", 0) with { Prototype = true }, Type("q.C"), Member("q.C.Go", 0)],
            [Ref("q.IB", CodeEdgeKinds.Implements, "IA"), Ref("q.C", CodeEdgeKinds.Implements, "IB")]));

        Assert.Contains(Edge("t:q.IB", "t:p.IA", CodeEdgeKinds.Implements, CodeConfidences.Unique), linked.Edges);
        Assert.Contains(Edge("t:q.C", "t:q.IB", CodeEdgeKinds.Implements, CodeConfidences.Exact), linked.Edges);
        Assert.Equal(CodeConfidences.Unique, Assert.Single(linked.Edges, edge => edge is { Kind: CodeEdgeKinds.Dispatches, From: "t:p.IA.Go" }).Confidence);
        Assert.Contains(Edge("t:p.IA.Go", "t:q.C.Go", CodeEdgeKinds.Dispatches, CodeConfidences.Unique), linked.Edges);
        Assert.Contains(Edge("t:q.IB.Go", "t:q.C.Go", CodeEdgeKinds.Dispatches, CodeConfidences.Exact), linked.Edges);
        Assert.DoesNotContain(linked.Edges, edge => edge is { Kind: CodeEdgeKinds.Dispatches, To: "t:q.IB.Go" });
    }

    /// <summary>An edge found two ways - a call through an import's prefix and the same call through the import itself - is listed once, as sure as its surest finding.</summary>
    [Fact]
    public void AnEdgeFoundTwoWays_IsListedOnce_AsSureAsItsSurestFinding()
    {
        var linked = Link(
            File("lib.t", [Member("f", 0)]),
            File("main.t", [Member("Go", 0)], [Call("Go", "f", 0), Call("Go", "f", 0, new ExtractQualifier(CodeQualifierKinds.Prefix, "l"))], imports: [Import(CodeImportForms.Relative, "lib.t"), Import(CodeImportForms.Relative, "lib.t") with { Prefix = "l" }]));

        Assert.Equal(CodeConfidences.Exact, Assert.Single(linked.Edges, edge => edge is { Kind: CodeEdgeKinds.Calls, From: "t:Go", To: "t:f" }).Confidence);
    }

    /// <summary>
    /// A container a base reference looked for before every base was found is looked for again after: a qualifier its type
    /// holds through a base is found there, exact, and not as every container of that name the repository declares.
    /// </summary>
    [Fact]
    public void AContainerLookedForBeforeEveryBaseWasFound_IsLookedForAgainAfter()
    {
        var linked = Link(File("a.t",
            [Type("Base"), Type("Base.Inner"), Member("Base.Inner.Run", 0), Type("Other"), Type("Other.Inner"), Member("Other.Inner.Run", 0), Type("X"), Type("X.Y"), Member("X.Go", 0)],
            [
                Ref("X.Y", CodeEdgeKinds.Inherits, "Marker") with { Qualifier = Named("Inner") },
                Ref("X", CodeEdgeKinds.Inherits, "Base"),
                Call("X.Go", "Run", 0, Named("Inner")),
            ]));

        Assert.Contains(Edge("t:X.Go", "t:Base.Inner.Run", CodeEdgeKinds.Calls, CodeConfidences.Exact), linked.Edges);
        Assert.DoesNotContain(linked.Edges, edge => edge is { From: "t:X.Go", To: "t:Other.Inner.Run" });
    }

    /// <summary>
    /// A link made again after any one file of a tree changed, came or went is the whole link of the tree as it stands - in
    /// every edge, node and count, in order - whatever the change touched: a name, a base, a value's type, an extension, a
    /// scope's parent or whether it is closed, an import by scope, path or address, what every file imports, an address root;
    /// and so is a link made again from one made again.
    /// </summary>
    [Fact]
    public void ALinkMadeAgain_IsTheWholeLinkOfTheTreeAsItStands_WhateverOneFileChanged()
    {
        var world = World();

        foreach (var (path, versions) in Versions())
        {
            foreach (var version in versions.Append(null))
            {
                var after = Replaced(world, path, version);

                var forth = Again(world, after);
                var back = Again(after, world);

                Assert.Equal(CodeGraphLinker.Link(world).Edges, CodeGraphLinker.Relink(forth, world).Edges);
                Assert.True(back.References <= CodeGraphLinker.Link(world).References);
            }
        }
    }

    /// <summary>
    /// Over trees made at random - scopes open and closed with their parents, namespaces, imports of every form with
    /// prefixes and filters, global and directory imports, address roots, partial and nested types and their bases, values
    /// and their types, extensions, every kind of qualifier and of reference, uses among them, data of every kind - keys,
    /// elements, rules - parts and literals - and one small change of one file at a time made at random - a value's
    /// type, what a member extends, what it takes, a scope's parent or whether it is closed, an import or one of its fields,
    /// an address root, a base, a type's qualified name or id, a declaration or a namespace come or gone, lines moved, a
    /// language, a file come, gone or made anew - each link made again from the one before is the whole link of the tree as
    /// it then stands.
    /// </summary>
    [Fact]
    public void ALinkMadeAgain_IsTheWholeLink_OverTreesAndChangesMadeAtRandom()
    {
        var random = new Random(20_23);

        for (var round = 0; round < 80; round++)
        {
            var tree = Enumerable.Range(0, 9).Select(_ => RandomFile(random)).DistinctBy(file => file.Path).ToArray();
            var linked = CodeGraphLinker.Link(tree);

            for (var change = 0; change < 30; change++)
            {
                var after = Changed(random, tree);
                var again = CodeGraphLinker.Relink(linked, after);
                var whole = CodeGraphLinker.Link(after);

                Assert.Equal(whole.Edges, again.Edges);
                Assert.Equal(whole.Symbols, again.Symbols, SymbolComparer.Instance);
                Assert.Equal([.. whole.External], [.. again.External]);
                Assert.Equal(SignsOf(whole), SignsOf(again));

                (tree, linked) = (after, again);
            }
        }
    }

    /// <summary>
    /// A link made again finds again the references of the file that changed and those that read a name its change added,
    /// and keeps every other: a file whose declarations stand as they stood, its lines moved, has its own found again alone.
    /// </summary>
    [Fact]
    public void ALinkMadeAgain_FindsOnlyWhatTheChangeCouldHaveChanged()
    {
        var caller = File("a.t", [Member("Go", 0)], [Call("Go", "Fresh", 0), Call("Go", "Kept", 0) with { Line = 2 }]);
        var other = File("b.t", [Member("Other", 0)], [Call("Other", "Kept", 0)]);
        var kept = File("k.t", [Member("Kept", 0)]);
        var lib = File("lib.t", [Member("Old", 0)], [Call("Old", "Kept", 0)]);
        CodeGraphFile[] before = [caller, other, kept, lib];

        var added = Again(before, [caller, other, kept, File("lib.t", [Member("Old", 0), Member("Fresh", 0) with { Start = 2, End = 2 }], [Call("Old", "Kept", 0)])]);
        var moved = Again(before, [caller, other, kept, File("lib.t", [Member("Old", 0) with { Start = 3, End = 4 }], [Call("Old", "Kept", 0) with { Line = 4 }])]);
        var whole = CodeGraphLinker.Link(before);

        Assert.Equal(4, whole.References);
        Assert.Equal(2, added.References);
        Assert.Contains(Edge("t:Go", "t:Fresh", CodeEdgeKinds.Calls, CodeConfidences.Unique), added.Edges);
        Assert.Equal(1, moved.References);
        Assert.Contains(new CodeEdge("t:Old", "t:Kept", CodeEdgeKinds.Calls, 4, CodeConfidences.Unique), moved.Edges);
    }

    /// <summary>A base that moved in one file finds again a call through that type made in another, and nothing else.</summary>
    [Fact]
    public void ALinkMadeAgain_FollowsABaseThatMoved_IntoAFileThatDidNot()
    {
        var shape = File("shape.t", [Type("Shape"), Member("Shape.Area", 0)]);
        var round = File("round.t", [Type("Round"), Member("Round.Area", 0)]);
        var use = File("use.t", [Type("Use"), Value("Use.circle", "Circle"), Member("Use.Go", 0)], [Call("Use.Go", "Area", 0, Named("circle")), Call("Use.Go", "Use", 0)]);
        var circle = File("circle.t", [Type("Circle")], [Ref("Circle", CodeEdgeKinds.Inherits, "Shape")]);

        var again = Again([shape, round, use, circle], [shape, round, use, File("circle.t", [Type("Circle")], [Ref("Circle", CodeEdgeKinds.Inherits, "Round")])]);

        Assert.Contains(Edge("t:Use.Go", "t:Round.Area", CodeEdgeKinds.Calls, CodeConfidences.Exact), again.Edges);
        Assert.Equal(2, again.References);
    }

    /// <summary>
    /// Each kind of thing a finding reads of a file it is not made in, changed alone in that file while the finding's own
    /// file stays as it was: a link made again finds that reference again, and is the whole link - the change altering
    /// what the reading declaration's references find, so a finding that did not keep what it read would keep what it
    /// found before.
    /// </summary>
    /// <param name="read">The thing read, as <see cref="ThingsAFindingReads"/> names it.</param>
    [Theory]
    [MemberData(nameof(ThingsAFindingReads))]
    public void ALinkMadeAgain_FindsAgainAReferenceThatReadWhatAChangeAltered(string read)
    {
        var (reader, before, after) = Reading()[read];

        Assert.NotEqual(Found(CodeGraphLinker.Link(before), reader), Found(CodeGraphLinker.Link(after), reader));
        Again(before, after);
    }

    /// <summary>What the references of the declaration <paramref name="reader"/> found in <paramref name="linked"/>: its edges, its count of names found nowhere, and the signs they gave.</summary>
    private static string Found(CodeGraphLinked linked, string reader)
        => string.Join(
            '\n',
            linked.Edges.Where(edge => edge.From == reader)
                .Select(edge => $"{edge.To} {edge.Kind} {edge.Confidence}")
                .Append($"external {linked.External.GetValueOrDefault(reader)}")
                .Append($"signs {string.Join(", ", linked.Signs.GetValueOrDefault(reader) ?? [])}"));

    /// <summary>Every thing <see cref="Reading"/> changes alone.</summary>
    public static TheoryData<string> ThingsAFindingReads() => [.. Reading().Keys];

    /// <summary>
    /// For each thing a finding reads of another file, the declaration whose reference reads it, a tree, and the same tree
    /// with that one thing of that one file changed - every other file the very same object.
    /// </summary>
    private static Dictionary<string, (string Reader, CodeGraphFile[] Before, CodeGraphFile[] After)> Reading()
    {
        static ExtractQualifier TypeQualifier(string name) => new(CodeQualifierKinds.Type, name);
        static ExtractNode Node(string id, string kind, string qualified, string? scope) => new(id, kind, Last(qualified), qualified, 1, 1, qualified) { Scope = scope is null ? null : QualifiedName.Of(scope) };

        var cases = new Dictionary<string, (string Reader, CodeGraphFile[] Before, CodeGraphFile[] After)>(StringComparer.Ordinal);
        var reader = "t:Go";

        void Case(string read, CodeGraphFile[] kept, CodeGraphFile? was, CodeGraphFile? now)
            => cases[read] = (reader, [.. kept, .. was is null ? [] : new[] { was }], [.. kept, .. now is null ? [] : new[] { now }]);

        var go = Member("Go", 0);

        // The repository by a name: a declaration of it come.
        Case("a name, in the repository", [File("use.t", [go], [Call("Go", "Run", 0)])], null, File("run.t", [Member("Run", 0)]));

        // An extension by its name: a member extending a type nothing declares, which the call's qualifier names.
        Case(
            "a name, among the extensions",
            [File("use.t", [go], [Call("Go", "Run", 0, TypeQualifier("Ghost"))])],
            null,
            File("ext.t", [Type("Ext"), Member("Ext.Run", 0) with { Extends = "Ghost" }]));

        // What a file imported by its path declares at its top.
        var importsLib = File("use.t", [go], [Call("Go", "Run", 0, Prefix("p"))], imports: [Import(CodeImportForms.Relative, "lib.t") with { Prefix = "p" }]);
        Case("a name, at the top of a file imported by its path", [importsLib], File("lib.t", [Member("Stop", 0)]), File("lib.t", [Member("Stop", 0), Member("Run", 0)]));

        // Whether the file an import binding a name names is in the tree: come, so the name is no longer one only an import naming no
        // file of it binds - counted external alike, but its sign gone.
        Case(
            "whether an import binding a name names a file of the tree",
            [Within(File("use.t", [go], [Call("Go", "Run", 0)], imports: [Import(CodeImportForms.Relative, "far.t") with { Show = ["Run"] }]))],
            null,
            File("far.t", [Member("Stop", 0)]));

        // Whether a declaration of a file imported by its path is at its top: one held by another is not.
        Case(
            "whether a declaration is at its file's top",
            [importsLib],
            File("lib.t", [Member("Stop", 0), Member("Run", 0)]),
            File("lib.t", [Member("Stop", 0), Member("Run", 0) with { Parent = "t:Stop" }]));

        // The open scopes of a file imported by its path: one it declares open now, which another file declares into.
        Case(
            "the open scopes of a file imported by its path",
            [importsLib, File("s.t", [Member("lib.Run", 0)])],
            File("lib.t", [Member("Stop", 0)]),
            File("lib.t", [Member("Stop", 0)], scopes: [Open("lib")]));

        // Whether a path is a file of the tree.
        Case("a path", [importsLib], null, File("lib.t", [Member("Run", 0)]));

        // The files of a name an import by a path's ending looks for.
        Case(
            "a file name",
            [File("use.t", [go], [Call("Go", "Run", 0, Prefix("p"))], imports: [Import(CodeImportForms.Ending, "x.t") with { Prefix = "p" }])],
            null,
            File("deep/x.t", [Member("Run", 0)]));

        // The address roots every file declares, which an import by an address is found through: one moved to the file's.
        Case(
            "the address roots",
            [File("use.t", [go], [Call("Go", "Run", 0, Prefix("p"))], imports: [Import(CodeImportForms.Address, "pkg:x.t") with { Prefix = "p" }]), File("lib/x.t", [Member("Run", 0)])],
            new CodeGraphFile("r.t", "t", Extract([]) with { AddressRoots = [new ExtractAddressRoot("pkg:", "elsewhere")] }),
            new CodeGraphFile("r.t", "t", Extract([]) with { AddressRoots = [new ExtractAddressRoot("pkg:", "lib")] }));

        // Whether an address root is whole: one become an address's alone, which no longer stands for the files under it.
        Case(
            "whether an address root is whole",
            [File("use.t", [go], [Call("Go", "Run", 0, Prefix("p"))], imports: [Import(CodeImportForms.Address, "pkg:x.t") with { Prefix = "p" }]), File("lib/x.t", [Member("Run", 0)])],
            new CodeGraphFile("r.t", "t", Extract([]) with { AddressRoots = [new ExtractAddressRoot("pkg:", "lib")] }),
            new CodeGraphFile("r.t", "t", Extract([]) with { AddressRoots = [new ExtractAddressRoot("pkg:", "lib") { Whole = true }] }));

        // Whether an address root is local: one become its directory's files' alone, which no longer gives the call's file its meaning.
        Case(
            "whether an address root is local",
            [File("use.t", [go], [Call("Go", "Run", 0, Prefix("p"))], imports: [Import(CodeImportForms.Address, "pkg:x.t") with { Prefix = "p" }]), File("lib/x.t", [Member("Run", 0)])],
            new CodeGraphFile("cfg/r.t", "t", Extract([]) with { AddressRoots = [new ExtractAddressRoot("pkg:", "../lib")] }),
            new CodeGraphFile("cfg/r.t", "t", Extract([]) with { AddressRoots = [new ExtractAddressRoot("pkg:", "../lib") { Local = true }] }));

        // The names a declaration of a file imported by its path is exported by besides its own.
        Case(
            "the names a declaration is exported by",
            [File("use.t", [go], [Call("Go", "default", 0, Prefix("p"))], imports: [Import(CodeImportForms.Relative, "lib.t") with { Prefix = "p" }])],
            File("lib.t", [Member("Run", 0)]),
            File("lib.t", [Member("Run", 0) with { Exports = ["default"] }]));

        // The containers of a qualified name: a type come where a namespace of that name was, whose base holds the call.
        var stop = File("b.t", [Type("B"), Member("B.Stop", 0)]);
        var callsStop = File("use.t", [go], [Call("Go", "Stop", 0, TypeQualifier("lib.A"))]);
        Case(
            "a qualified name",
            [callsStop, stop, File("s.t", [Node("n:lib.A", CodeNodeKinds.Namespace, "lib.A", "lib")])],
            null,
            File("a.t", [Type("lib.A")], [Ref("lib.A", CodeEdgeKinds.Inherits, "B")]));

        // The qualified names a written name's last run ends: a type come whose name ends with the qualifier.
        Case(
            "a name's last run",
            [File("use.t", [go], [Call("Go", "Run", 0, TypeQualifier("in.B"))]), File("m.t", [Member("lib.in.B.Run", 0)])],
            null,
            File("t.t", [Type("lib.in.B")]));

        // The parts of an id: a part come of another qualified name, whose scope holds the call.
        Case(
            "an id",
            [File("use.t", [go], [Call("Go", "Run", 0, TypeQualifier("lib.A"))]), File("a.t", [Type("lib.A")]), File("f.t", [Member("far.A.Run", 0)])],
            null,
            File("b.t", [Node("t:lib.A", CodeNodeKinds.Type, "far.A", "far")]));

        // The scope a type's own scope is declared in, as the type's file places it: the type that holds the call's moved to
        // the scope an extension's type is declared in, so the call's file now reaches the extension.
        reader = "t:lib.T.N.Go";
        Case(
            "the scope a type is",
            [
                File("b.t", [Type("lib.T.N"), Member("lib.T.N.Go", 0) with { Parent = "t:lib.T.N" }], [Call("lib.T.N.Go", "Run", 0, new ExtractQualifier(CodeQualifierKinds.This))]),
                File("x.t", [Type("app.X"), Member("app.X.Run", 0) with { Extends = "lib.T.N" }]),
            ],
            File("a.t", [Type("lib.T")]),
            File("a.t", [Type("lib.T") with { Scope = "app" }]));

        // A type's bases as the first pass found them, which a base named through that type's bases is found again by.
        reader = "t:C";
        Case(
            "a type's first bases",
            [
                File("b1.t", [Type("B1"), Type("B1.Inner")]),
                File("b2.t", [Type("B2"), Type("B2.Inner")]),
                File("c.t", [Type("C")], [Ref("C", CodeEdgeKinds.Inherits, "Inner") with { Qualifier = TypeQualifier("T") }]),
            ],
            File("t.t", [Type("T")], [Ref("T", CodeEdgeKinds.Inherits, "B1")]),
            File("t.t", [Type("T")], [Ref("T", CodeEdgeKinds.Inherits, "B2")]));

        // A type's bases as the second pass found them - through another type's first - which a call on the type looks in.
        reader = "t:Go";
        Case(
            "a type's final bases",
            [
                File("b1.t", [Type("B1"), Type("B1.Inner"), Member("B1.Inner.Run", 0)]),
                File("b2.t", [Type("B2"), Type("B2.Inner"), Member("B2.Inner.Run", 0)]),
                File("t.t", [Type("T")], [Ref("T", CodeEdgeKinds.Inherits, "Inner") with { Qualifier = TypeQualifier("Q") }]),
                File("use.t", [go], [Call("Go", "Run", 0, TypeQualifier("T"))]),
            ],
            File("q.t", [Type("Q")], [Ref("Q", CodeEdgeKinds.Inherits, "B1")]),
            File("q.t", [Type("Q")], [Ref("Q", CodeEdgeKinds.Inherits, "B2")]));

        // A type's bases in their ranks: a mixin become a base it inherits beside another, so a call on the type finds the
        // member each of them declares, where the mixin's alone was nearer.
        Case(
            "a type's bases in their ranks",
            [
                File("m.t", [Type("M"), Member("M.Run", 0) with { Parent = "t:M" }]),
                File("s.t", [Type("S"), Member("S.Run", 0) with { Parent = "t:S" }]),
                File("use.t", [go], [Call("Go", "Run", 0, TypeQualifier("C"))]),
            ],
            File("c.t", [Type("C")], [Ref("C", CodeEdgeKinds.Inherits, "S"), Ref("C", CodeEdgeKinds.MixesIn, "M") with { Line = 2 }]),
            File("c.t", [Type("C")], [Ref("C", CodeEdgeKinds.Inherits, "M"), Ref("C", CodeEdgeKinds.Inherits, "S") with { Line = 2 }]));

        // The scope a container is: closed in its own file, it is that file's alone, and not seen from the call's.
        Case(
            "the scope a container is",
            [File("use.t", [go], [Call("Go", "Run", 0, TypeQualifier("lib.A"))]), File("m.t", [Member("lib.A.Run", 0)])],
            File("a.t", [Type("lib.A")]),
            File("a.t", [Type("lib.A")], scopes: [new ExtractScope("lib.A", Open: false)]));

        // What a declaration is: a type become a namespace of the same id, which has no bases to look in.
        Case(
            "what a declaration is",
            [callsStop, stop],
            File("a.t", [Type("lib.A")], [Ref("lib.A", CodeEdgeKinds.Inherits, "B")]),
            File("a.t", [Node("t:lib.A", CodeNodeKinds.Namespace, "lib.A", "lib")], [Ref("lib.A", CodeEdgeKinds.Inherits, "B")]));

        // What a member takes: the fewest arguments, and the most.
        Case("the fewest arguments a member takes", [File("use.t", [go], [Call("Go", "Run", 0)])], File("run.t", [Member("Run", 1)]), File("run.t", [Member("Run", 1) with { MinArity = 0 }]));
        Case("the most arguments a member takes", [File("use.t", [go], [Call("Go", "Run", 1)])], File("run.t", [Member("Run", 0)]), File("run.t", [Member("Run", 0) with { MaxArity = 1 }]));

        // A value's type, a call on which goes through it.
        reader = "t:lib.U.Go";
        Case(
            "a value's type",
            [
                File("u.t", [Type("lib.U"), Member("lib.U.Go", 0)], [Call("lib.U.Go", "Stop", 0, new ExtractQualifier(CodeQualifierKinds.Value, "cur"))]),
                File("bc.t", [Type("lib.B"), Member("lib.B.Stop", 0), Type("lib.C"), Member("lib.C.Stop", 0)]),
            ],
            File("v.t", [Value("lib.U.cur", "B")]),
            File("v.t", [Value("lib.U.cur", "C")]));

        // What another file imports, which what its extension extends is found through: each of an import's fields, and the
        // language its global imports come by.
        reader = "t:Go";
        var shapes = new[] { File("g.t", [Type("geo.Shape")]), File("f.t", [Type("far.Shape")]), File("use.t", [go], [Call("Go", "Run", 0, TypeQualifier("far.Shape"))]) };
        var extension = new[] { Type("Ext"), Member("Ext.Run", 0) with { Extends = "Shape" } };
        var geo = Import(CodeImportForms.Scope, "geo");
        var importing = File("ext.t", extension, imports: [geo]);

        Case("an import's form", shapes, importing, File("ext.t", extension, imports: [geo with { Form = CodeImportForms.Relative }]));
        Case("an import's target", shapes, importing, File("ext.t", extension, imports: [geo with { Target = "far" }]));
        Case("an import's prefix", shapes, importing, File("ext.t", extension, imports: [geo with { Prefix = "G" }]));
        Case("what an import shows", shapes, importing, File("ext.t", extension, imports: [geo with { Show = ["Other"] }]));
        Case("what an import hides", shapes, importing, File("ext.t", extension, imports: [geo with { Hide = ["Shape"] }]));
        var shares = File("use.t", [go], [Call("Go", "Run", 0, Prefix("p"))], imports: [Import(CodeImportForms.Relative, "lib.t") with { Shares = true }]);

        // What a file imported by its path exports, which a name is found through.
        var importsBarrel = File("use.t", [go], [Call("Go", "Run", 0, Prefix("p"))], imports: [Import(CodeImportForms.Relative, "lib.t") with { Prefix = "p" }]);
        Case(
            "what a file imported by its path exports",
            [importsBarrel, File("run.t", [Member("Run", 0)])],
            File("lib.t", [Member("Stop", 0)]),
            File("lib.t", [Member("Stop", 0)], imports: [Import(CodeImportForms.Relative, "run.t") with { Export = true }]));

        // The scope a declaration is kept to, which the finding's declaration is not written in.
        Case(
            "the scope a declaration is kept to",
            [File("use.t", [go], [Call("Go", "Run", 0)])],
            File("run.t", [Member("lib.Run", 0)]),
            File("run.t", [Member("lib.Run", 0) with { SeenIn = "lib" }]));

        // Whether a file imported by its path has an extract: one read now, which exports what is called.
        Case(
            "whether a file imported by its path has an extract",
            [File("use.t", [go], [Call("Go", "Run", 0, Prefix("p"))], imports: [Import(CodeImportForms.Relative, "lib.t") with { Prefix = "p" }]), File("run.t", [Member("Run", 0)])],
            new CodeGraphFile("lib.t", "t", null),
            File("lib.t", [], imports: [Import(CodeImportForms.Relative, "run.t") with { Export = true }]));

        // Whether a file an extension is reached through has an extract: one read now, which exports the library of the
        // extension's scope.
        Case(
            "whether a file an extension is reached through has an extract",
            [
                File("use.t", [go], [Call("Go", "Shout", 0, TypeQualifier("String"))], imports: [Import(CodeImportForms.Relative, "barrel.t")]),
                File("ext/lib.t", [], scopes: [Open("ext")]),
                File("ext/part.t", [Type("ext.StrExt"), Member("ext.StrExt.Shout", 0) with { Extends = "String", Parent = "t:ext.StrExt" }]),
            ],
            new CodeGraphFile("barrel.t", "t", null),
            File("barrel.t", [], imports: [Import(CodeImportForms.Relative, "ext/lib.t") with { Export = true }]));

        // Whether a file whose imports are shared has an extract: one read now, which imports what is called.
        Case(
            "whether a file whose imports are shared has an extract",
            [shares, File("run.t", [Member("Run", 0)])],
            new CodeGraphFile("lib.t", "t", null),
            File("lib.t", [], imports: [Import(CodeImportForms.Relative, "run.t") with { Prefix = "p" }]));

        // What a file whose imports are shared imports.
        Case(
            "what a file whose imports are shared imports",
            [shares, File("run.t", [Member("Run", 0)])],
            File("lib.t", []),
            File("lib.t", [], imports: [Import(CodeImportForms.Relative, "run.t") with { Prefix = "p" }]));

        Case(
            "the language a file's global imports come by",
            [.. shapes, File("usings.t", [], imports: [geo with { Global = true }])],
            File("ext.t", extension),
            new CodeGraphFile("ext.t", "u", File("ext.t", extension).Extract));

        // A directory's scope import come - a _ViewImports.cshtml written where none was - which a view under it now finds
        // its one declaration through, where it had two.
        reader = "t:Cart";
        Case(
            "a directory's scope import",
            [
                File("Views/Cart.cshtml", [Member("Cart", 0)], [Ref("Cart", CodeEdgeKinds.References, "Helper")]) with { Language = "razor" },
                File("shop.t", [Type("Shop.Web.Helper")], scopes: [Open("Shop.Web")]),
                File("other.t", [Type("Other.Helper")], scopes: [Open("Other")]),
            ],
            null,
            File("Views/_ViewImports.cshtml", [], imports: [Import(CodeImportForms.Directory, "Shop.Web")]) with { Language = "razor" });

        // A declaration's selectors, names it is found by as its own: one come that the reference names.
        reader = "t:Go";
        Case(
            "a declaration's selectors",
            [File("use.t", [go], [Ref("Go", CodeEdgeKinds.Instantiates, "x-cart")])],
            File("cart.t", [Type("Cart")]),
            File("cart.t", [Type("Cart") with { Selectors = ["x-cart"] }]));

        // The templates a declaration names: a template's own name, read on the declaration that names its file its template.
        reader = "t:Body";
        Case(
            "the templates a declaration names",
            [File("cart.t", [Member("Body", 0)], [Call("Body", "Save", 0, new ExtractQualifier(CodeQualifierKinds.This))])],
            File("comp.t", [Type("Comp"), Member("Comp.Save", 0) with { Parent = "t:Comp" }]),
            File("comp.t", [Type("Comp") with { Templates = ["./cart.t"] }, Member("Comp.Save", 0) with { Parent = "t:Comp" }]));

        // Whether a type's members are reached by a receiver alone: its bases passed over by a name written alone in its scope - a
        // namespace's of its name merged with it, which sees its own names and never the type's bases' - and that name found past it.
        reader = "t:lib.Cart.Go";
        Case(
            "whether a type's members are reached by a receiver alone",
            [
                File("b.t", [Member("lib.Cart.Go", 0)], [Call("lib.Cart.Go", "Run", 0)]),
                File("c.t", [Member("lib.Run", 0)]),
                File("d.t", [Type("lib.Base"), Member("lib.Base.Run", 0)]),
            ],
            File("a.t", [Type("lib.Cart")], [Ref("lib.Cart", CodeEdgeKinds.Inherits, "Base")]),
            File("a.t", [Type("lib.Cart") with { MembersByReceiver = true }], [Ref("lib.Cart", CodeEdgeKinds.Inherits, "Base")]));

        // Whether a member added to a type from outside its body is reached by a receiver alone: the scope it is declared in passed over.
        reader = "t:lib.X.Go";
        var outside = new ExtractScope("lib.X", Open: true) { Parent = QualifiedName.Of("lib") };
        Case(
            "whether a member added from outside its type is reached by a receiver alone",
            [File("b.t", [Member("lib.X.Go", 0)], [Call("lib.X.Go", "Run", 0)], scopes: [outside]), File("c.t", [Member("lib.Run", 0)])],
            File("a.t", [Member("lib.X.Run", 0) with { Extends = "X" }]),
            File("a.t", [Member("lib.X.Run", 0) with { Extends = "X", MembersByReceiver = true }]));

        // The members declared to extend a type of a name: one come that extends the function a this is read in, which makes the
        // function a constructor function, whose own this is read on it.
        reader = "t:Go";
        Case(
            "the members declared to extend a type of a name",
            [File("use.t", [go], [Call("Go", "Run", 0, new ExtractQualifier(CodeQualifierKinds.This))])],
            File("ext.t", [Member("Go.Run", 0) with { Extends = "Other" }]),
            File("ext.t", [Member("Go.Run", 0) with { Extends = "Go" }]));

        // What a file of a language that binds its names within the file reaches through another file's imports and layout: each
        // a declaration named Run of an id of its own, so which one a call finds is seen.
        reader = "t:Go";
        static ExtractNode RunAt(string id) => Member("Run", 0) with { Id = id };
        static CodeGraphFile Within(CodeGraphFile file) => file with { Extract = file.Extract! with { BoundWithin = true } };
        var throughMid = Within(File("use.t", [go], [Call("Go", "Run", 0)], imports: [Import(CodeImportForms.Relative, "mid.t")]));
        var runs = new[] { File("a.t", [RunAt("t:a.Run")]), File("b.t", [RunAt("t:b.Run")]) };

        // Whether what another file's import binds is its importers' too: a re-export become one.
        Case(
            "whether an import's names are its importers' too",
            [throughMid, .. runs],
            File("mid.t", [], imports: [Import(CodeImportForms.Relative, "a.t") with { Show = ["Run"] }]),
            File("mid.t", [], imports: [Import(CodeImportForms.Relative, "a.t") with { Show = ["Run"], Reexports = true }]));

        // Whether another file's place is one of two a name may stand at: the place above become a candidate beside it.
        Case(
            "whether an import is one of two places",
            [throughMid, .. runs],
            File("mid.t", [], imports: [Import(CodeImportForms.Relative, "a.t") with { Reexports = true }, Import(CodeImportForms.Relative, "b.t") with { Reexports = true, Fallback = true }]),
            File("mid.t", [], imports: [Import(CodeImportForms.Relative, "a.t") with { Reexports = true, Either = true }, Import(CodeImportForms.Relative, "b.t") with { Reexports = true, Fallback = true }]));

        // The names a file publishes, which an import taking its published names makes visible alone.
        var takesPublished = Within(File("use.t", [go], [Call("Go", "Run", 0)], imports: [Import(CodeImportForms.Relative, "lib.t") with { Published = true }]));
        Case(
            "the names a file publishes",
            [takesPublished],
            new CodeGraphFile("lib.t", "t", Extract([RunAt("t:lib.Run")]) with { Published = ["Stop"] }),
            new CodeGraphFile("lib.t", "t", Extract([RunAt("t:lib.Run")]) with { Published = ["Run"] }));

        // Whether what a file's own such imports take is among what it publishes: its * import's names become its own.
        Case(
            "whether a file publishes what its imports take",
            [takesPublished, new CodeGraphFile("a.t", "t", Extract([RunAt("t:a.Run")]) with { Published = ["Run"] })],
            new CodeGraphFile("lib.t", "t", Extract([]) with { Published = [], Imports = [Import(CodeImportForms.Relative, "a.t") with { Published = true, Reexports = true }] }),
            new CodeGraphFile("lib.t", "t", Extract([]) with { Published = [], PublishesTaken = true, Imports = [Import(CodeImportForms.Relative, "a.t") with { Published = true, Reexports = true }] }));

        // Whether a directory is a unit of its language's layout: a package's file come, so a place of the layout in it is passed
        // over and the one above taken.
        var layout = Within(File(
            "pkg/sub/mod.t",
            [go],
            [Call("Go", "Run", 0, Prefix("h"))],
            imports: [Import(CodeImportForms.Relative, "helper.t") with { Prefix = "h", Layout = true }, Import(CodeImportForms.Relative, "../../helper.t") with { Prefix = "h", Layout = true, Fallback = true }]));
        Case(
            "a unit of the layout",
            [layout, File("pkg/sub/helper.t", [RunAt("t:near.Run")]), File("helper.t", [RunAt("t:far.Run")])],
            null,
            new CodeGraphFile("pkg/sub/init.t", "t", Extract([]) with { FolderUnit = true }));

        // The roots of the layout an import under one is found through: a root a project file declares moved, and a unit come
        // that makes the directory it stands in one.
        var rooted = Within(File("use.t", [go], [Call("Go", "Run", 0, Prefix("x"))], imports: [Import(CodeImportForms.Rooted, "x/run.t") with { Prefix = "x" }]));
        var underRoots = new[] { rooted, File("src/x/run.t", [RunAt("t:src.Run")]), File("lib/x/run.t", [RunAt("t:lib.Run")]) };
        Case(
            "a root of the layout a file declares",
            underRoots,
            new CodeGraphFile("project.t", "t", Extract([]) with { LayoutRoots = ["src"] }),
            new CodeGraphFile("project.t", "t", Extract([]) with { LayoutRoots = ["lib"] }));
        Case("a unit that makes a root of the layout", underRoots, null, new CodeGraphFile("src/x/init.t", "t", Extract([]) with { FolderUnit = true }));

        // What a directory's import serves: become the fixture references' alone, which a test's parameter now reaches a fixture through.
        Case(
            "what an import serves",
            [
                Within(File("tests/t.t", [go], [Ref("Go", CodeEdgeKinds.References, "sample") with { Qualifier = new ExtractQualifier(CodeQualifierKinds.Fixture) }])),
            ],
            File("tests/conf.t", [Member("conf.sample", 0) with { SubKind = CodeSubKinds.Fixture }], scopes: [Open("conf")], imports: [Import(CodeImportForms.Directory, "conf")]),
            File("tests/conf.t", [Member("conf.sample", 0) with { SubKind = CodeSubKinds.Fixture }], scopes: [Open("conf")], imports: [Import(CodeImportForms.Directory, "conf") with { Serves = CodeQualifierKinds.Fixture }]));

        // The keys' join: a key come that a member's name and a literal name, each now a use of it.
        reader = "t:Reader";
        Case(
            "a configuration key",
            [new CodeGraphFile("reader.t", "t", Extract([Type("Reader"), Value("Reader.Timeout", null) with { Parent = "t:Reader" }]) with { Literals = LiteralRow.Table(new LiteralRow("timeout", 1, "t:Reader")) })],
            null,
            new CodeGraphFile("app.json", "json", Extract([new ExtractNode("json:app.json#timeout", CodeNodeKinds.Member, "timeout", "timeout", 1, 1, "timeout") { SubKind = CodeSubKinds.Key }])));

        return cases;
    }

    /// <summary>
    /// A member extending a type of a name several types are - none an import or the member's scopes name - reads its own
    /// <c>this</c> on each of them, never on the first alone: a member one of them declares is found unique, never exact.
    /// </summary>
    [Fact]
    public void AMemberExtendingATypeSeveralAreNamed_ReadsItsThisOnEachOfThem()
    {
        var linked = Link(
            File("lib/w.t", [Type("lib.Widget"), Member("lib.Widget.Draw", 0) with { Parent = "t:lib.Widget" }]),
            File("app/w.t", [Type("app.Widget"), Member("app.Widget.Size", 0) with { Parent = "t:app.Widget" }]),
            File("ext.t", [Member("Ext.Paint", 0) with { Extends = "Widget" }], [Call("Ext.Paint", "Draw", 0, new ExtractQualifier(CodeQualifierKinds.This)), Call("Ext.Paint", "Size", 0, new ExtractQualifier(CodeQualifierKinds.This))]));

        Assert.Equal(
            [Edge("t:Ext.Paint", "t:lib.Widget.Draw", CodeEdgeKinds.Calls, CodeConfidences.Unique), Edge("t:Ext.Paint", "t:app.Widget.Size", CodeEdgeKinds.Calls, CodeConfidences.Unique)],
            linked.Edges.Where(edge => edge.From == "t:Ext.Paint" && edge.Kind == CodeEdgeKinds.Calls));
    }

    /// <summary>
    /// A name written alone in a member's code passes over the scope of members reached by a receiver alone - a type's own that
    /// says so, and the one a member added from outside its type is declared in - and finds what the scope around holds: never
    /// the member itself, nor its sibling.
    /// </summary>
    [Fact]
    public void ANameWrittenAlone_PassesOverTheScopeOfMembersReachedByAReceiverAlone()
    {
        var linked = Link(
            File("lib.t", [Type("lib.Cart") with { MembersByReceiver = true }, Member("lib.Cart.render", 1) with { Parent = "t:lib.Cart" }, Member("lib.render", 1)], [Call("lib.Cart.render", "render", 1)]),
            File("ext.t", [Member("lib.Box.paint", 0) with { Extends = "Box", MembersByReceiver = true }, Member("lib.Box.draw", 0) with { Extends = "Box", MembersByReceiver = true }, Member("lib.draw", 0)], [Call("lib.Box.paint", "draw", 0)], scopes: [new ExtractScope("lib.Box", Open: true) { Parent = QualifiedName.Of("lib") }]));

        Assert.Equal([Edge("t:lib.Cart.render", "t:lib.render", CodeEdgeKinds.Calls, CodeConfidences.Exact)], linked.Edges.Where(edge => edge.From == "t:lib.Cart.render"));
        Assert.Equal([Edge("t:lib.Box.paint", "t:lib.draw", CodeEdgeKinds.Calls, CodeConfidences.Exact)], linked.Edges.Where(edge => edge.From == "t:lib.Box.paint"));
    }

    /// <summary>
    /// A fixture qualifier reaches a fixture alone - a member of that kind within a kind - as every qualifier that names the kind it
    /// reaches does: through the scope import that serves fixtures, exact; a type of that kind, and a member of none, are not reached.
    /// </summary>
    [Fact]
    public void AFixtureQualifier_ReachesAFixtureMemberAlone()
    {
        var test = File("tests/t.t", [Member("Go", 0)], [Ref("Go", CodeEdgeKinds.References, "sample") with { Qualifier = new ExtractQualifier(CodeQualifierKinds.Fixture) }]);
        IReadOnlyList<CodeEdge> Reached(ExtractNode sample)
            => [.. Link(test, File("tests/conf.t", [sample], scopes: [Open("c")], imports: [Import(CodeImportForms.Directory, "c") with { Serves = CodeQualifierKinds.Fixture }])).Edges.Where(edge => edge.From == "t:Go")];

        Assert.Equal([Edge("t:Go", "t:c.sample", CodeEdgeKinds.References, CodeConfidences.Exact)], Reached(Member("c.sample", 0) with { SubKind = CodeSubKinds.Fixture }));
        Assert.Empty(Reached(Type("c.sample") with { SubKind = CodeSubKinds.Fixture }));
        Assert.Empty(Reached(Member("c.sample", 0)));
    }

    /// <summary>
    /// What a reference found - nothing, or several - gives its node the sign of why, as the way it is written tells it, no language
    /// named, each one thing the linker knows: a fixture no scope and no serving import provides; a name, or a prefix, only an import
    /// that names no file of the tree binds; a name written alone nothing binds, in a file that binds its names within it - none in a
    /// file that does not, and none where an import naming a file of the tree binds the name, though each is counted external
    /// alike - a directory's import, which every file of its language is given, naming a scope and no file, binds none of them;
    /// candidates through an import of two places, both places declaring the name or one alone, as that import's file's own node is
    /// signed beside its edges; and any other candidates. A use, which is recall, and a probe that found nothing, which misses nothing,
    /// give none.
    /// </summary>
    [Fact]
    public void AFinding_GivesItsNodeTheSignOfWhyItFoundNothingOrSeveral()
    {
        static CodeGraphFile Within(CodeGraphFile file) => file with { Extract = file.Extract! with { BoundWithin = true } };

        var linked = Link(
            Within(File("t/test.t", [Member("UsesFixture", 0)], [Ref("UsesFixture", CodeEdgeKinds.References, "sample") with { Qualifier = new ExtractQualifier(CodeQualifierKinds.Fixture) }])),
            Within(File(
                "outside.t",
                [Member("Outside", 0), Member("Prefixed", 0)],
                [Call("Outside", "convert", 1), Call("Prefixed", "get", 0, Prefix("requests"))],
                imports: [Import(CodeImportForms.Relative, "vendor/rates.t") with { Show = ["convert"] }, Import(CodeImportForms.Relative, "requests.t") with { Prefix = "requests" }])),
            Within(File(
                "unbound.t",
                [Member("Unbound", 0), Member("UsesOnly", 0), Member("Probing", 0)],
                [Call("Unbound", "nowhere", 0), Ref("UsesOnly", CodeEdgeKinds.Uses, "absent"), Ref("Probing", CodeEdgeKinds.Calls, "absent") with { Probe = true }])),
            File("open.t", [Member("Open", 0)], [Call("Open", "nowhere", 0)]),
            Within(File("present.t", [Member("Present", 0)], [Call("Present", "missing", 0)], imports: [Import(CodeImportForms.Relative, "lib.t") with { Show = ["missing"] }])),
            File("lib.t", [Member("Other", 0)]),
            Within(File(
                "tools/run.t",
                [Member("Run", 0), Member("Solo", 0)],
                [Call("Run", "pack", 0, Prefix("h")), Call("Solo", "solo", 0, Prefix("h"))],
                imports: [Import(CodeImportForms.Relative, "helper.t") with { Prefix = "h", Either = true }, Import(CodeImportForms.Relative, "../helper.t") with { Prefix = "h", Fallback = true }])),
            File("tools/helper.t", [Member("pack", 0) with { Id = "t:near.pack" }, Member("solo", 0) with { Id = "t:near.solo" }]),
            File("helper.t", [Member("pack", 0) with { Id = "t:far.pack" }]),
            File("duck.t", [Member("Duck", 0)], [Call("Duck", "quack", 0, new ExtractQualifier(CodeQualifierKinds.Value))]),
            File("ducks/a.t", [Member("quack", 0) with { Id = "t:a.quack" }]),
            File("ducks/b.t", [Member("quack", 0) with { Id = "t:b.quack" }]),
            File("top.t", [], imports: [Import(CodeImportForms.Directory, "far") with { Global = true }]));

        Assert.Equal(
            [
                "file:tools/run.t: candidate-imports",
                "t:Duck: candidates",
                "t:Outside: outside-import",
                "t:Prefixed: outside-import",
                "t:Run: candidate-imports",
                "t:Solo: candidate-imports",
                "t:Unbound: unbound",
                "t:UsesFixture: unprovided-fixture",
            ],
            SignsOf(linked));

        // Counted external all the same - a name the repository declares nowhere, one an import of a file of the tree binds - but no
        // sign of either: nothing the linker knows tells why. A directory's import, given to every file of the language, names a
        // scope and no file: it makes no name an outside import's.
        Assert.Equal(1, linked.External["t:Open"]);
        Assert.Equal(1, linked.External["t:Present"]);
        Assert.Equal(
            [Edge("t:Run", "t:far.pack", CodeEdgeKinds.Calls, CodeConfidences.Candidates), Edge("t:Run", "t:near.pack", CodeEdgeKinds.Calls, CodeConfidences.Candidates)],
            linked.Edges.Where(edge => edge.From == "t:Run").OrderBy(edge => edge.To, StringComparer.Ordinal));
        Assert.Equal([Edge("t:Solo", "t:near.solo", CodeEdgeKinds.Calls, CodeConfidences.Candidates)], linked.Edges.Where(edge => edge.From == "t:Solo"));
    }

    /// <summary>
    /// A chain written as links is bound where its head is: a link that found nothing past a head only an import naming no file of
    /// the tree binds gives outside-import, though its own qualifier writes no name - here past a probe that found nothing, which
    /// gives none - and one left candidates through an import taken as several gives candidate-imports, as its head does, both places
    /// declaring the type or one alone: a member of a type an import of two places reaches is a candidate as the type is.
    /// </summary>
    [Fact]
    public void AChainWrittenAsLinks_GivesTheSignItsHeadsImportGives()
    {
        static CodeGraphFile Within(CodeGraphFile file) => file with { Extract = file.Extract! with { BoundWithin = true } };

        var linked = Link(
            Within(File(
                "outside.t",
                [Member("Outside", 0)],
                [Ref("Outside", CodeEdgeKinds.Uses, "version") with { Qualifier = Prefix("np"), Probe = true }, Call("Outside", "split", 0, new ExtractQualifier(CodeQualifierKinds.Type) { Link = 0 })],
                imports: [Import(CodeImportForms.Relative, "vendor/numpy.t") with { Prefix = "np" }])),
            Within(File(
                "tools/run.t",
                [Member("Run", 0), Member("Solo", 0)],
                [
                    Ref("Run", CodeEdgeKinds.Uses, "Box") with { Qualifier = Prefix("h") }, Call("Run", "pack", 0, new ExtractQualifier(CodeQualifierKinds.Type) { Link = 0 }),
                    Ref("Solo", CodeEdgeKinds.Uses, "Crate") with { Qualifier = Prefix("h") }, Call("Solo", "pack", 0, new ExtractQualifier(CodeQualifierKinds.Type) { Link = 2 }),
                ],
                imports: [Import(CodeImportForms.Relative, "helper.t") with { Prefix = "h", Either = true }, Import(CodeImportForms.Relative, "../helper.t") with { Prefix = "h", Fallback = true }])),
            File(
                "tools/helper.t",
                [
                    Type("Box") with { Id = "t:near.Box" }, Member("Box.pack", 0) with { Id = "t:near.Box.pack", Parent = "t:near.Box" },
                    Type("Crate") with { Id = "t:near.Crate" }, Member("Crate.pack", 0) with { Id = "t:near.Crate.pack", Parent = "t:near.Crate" },
                ]),
            File("helper.t", [Type("Box") with { Id = "t:far.Box" }, Member("Box.pack", 0) with { Id = "t:far.Box.pack", Parent = "t:far.Box" }]));

        Assert.Equal(["file:tools/run.t: candidate-imports", "t:Outside: outside-import", "t:Run: candidate-imports", "t:Solo: candidate-imports"], SignsOf(linked));
        Assert.Equal(
            [Edge("t:Run", "t:far.Box.pack", CodeEdgeKinds.Calls, CodeConfidences.Candidates), Edge("t:Run", "t:near.Box.pack", CodeEdgeKinds.Calls, CodeConfidences.Candidates)],
            linked.Edges.Where(edge => edge is { From: "t:Run", Kind: CodeEdgeKinds.Calls }).OrderBy(edge => edge.To, StringComparer.Ordinal));
        Assert.Equal(
            [Edge("t:Solo", "t:near.Crate", CodeEdgeKinds.Uses, CodeConfidences.Candidates), Edge("t:Solo", "t:near.Crate.pack", CodeEdgeKinds.Calls, CodeConfidences.Candidates)],
            linked.Edges.Where(edge => edge.From == "t:Solo"));
    }

    /// <summary>
    /// What an import of two places reaches is a candidate of each, though one place alone declares it, by every way a reference
    /// reaches through one: a name written alone it lets, a member of a type a qualifier names through its prefix, and a member of the
    /// value a chain of names reaches from a type it lets - the type's doubt carried along the chain to what is found on its value. Each
    /// gives the sign of the import, never of a name bound alone.
    /// </summary>
    [Fact]
    public void WhatAnImportOfTwoPlacesReaches_IsACandidate_ANameATypesMemberAndAValuesMemberAlike()
    {
        static CodeGraphFile Within(CodeGraphFile file) => file with { Extract = file.Extract! with { BoundWithin = true } };

        var linked = Link(
            Within(File(
                "tools/run.t",
                [Member("Bare", 0), Member("Named", 0), Member("Valued", 0)],
                [Call("Bare", "solo", 0), Call("Named", "make", 0, new ExtractQualifier(CodeQualifierKinds.Type, "h.Box")), Call("Valued", "go", 0, new ExtractQualifier(CodeQualifierKinds.Value, "Box.inner"))],
                imports:
                [
                    Import(CodeImportForms.Relative, "helper.t") with { Either = true }, Import(CodeImportForms.Relative, "../helper.t") with { Fallback = true },
                    Import(CodeImportForms.Relative, "helper.t") with { Prefix = "h", Either = true }, Import(CodeImportForms.Relative, "../helper.t") with { Prefix = "h", Fallback = true },
                ])),
            File(
                "tools/helper.t",
                [
                    Member("solo", 0) with { Id = "t:near.solo" },
                    Type("Box") with { Id = "t:near.Box" }, Member("Box.make", 0) with { Id = "t:near.Box.make", Parent = "t:near.Box" },
                    Value("Box.inner", "Inner") with { Id = "t:near.Box.inner", Parent = "t:near.Box" },
                    Type("Inner") with { Id = "t:near.Inner" }, Member("Inner.go", 0) with { Id = "t:near.Inner.go", Parent = "t:near.Inner" },
                ]),
            File("helper.t", [Member("other", 0) with { Id = "t:far.other" }]));

        Assert.Equal(
            [
                Edge("t:Bare", "t:near.solo", CodeEdgeKinds.Calls, CodeConfidences.Candidates),
                Edge("t:Named", "t:near.Box.make", CodeEdgeKinds.Calls, CodeConfidences.Candidates),
                Edge("t:Valued", "t:near.Inner.go", CodeEdgeKinds.Calls, CodeConfidences.Candidates),
            ],
            linked.Edges.Where(edge => edge.From is "t:Bare" or "t:Named" or "t:Valued"));
        Assert.Equal(
            ["file:tools/run.t: candidate-imports", "t:Bare: candidate-imports", "t:Named: candidate-imports", "t:Valued: candidate-imports"],
            SignsOf(linked));
    }

    /// <summary>The signs each node of <paramref name="linked"/> gave, a line a node, in the order of their ids.</summary>
    private static string[] SignsOf(CodeGraphLinked linked)
        => [.. linked.Signs.OrderBy(signs => signs.Key, StringComparer.Ordinal).Select(signs => $"{signs.Key}: {string.Join(", ", signs.Value)}")];

    /// <summary>Two files of one path are no tree: the linker refuses them.</summary>
    [Fact]
    public void TwoFilesOfOnePath_AreRefused()
        => Assert.Throws<ArgumentException>(() => Link(File("a.t", [Member("Go", 0)]), File("a.t", [Member("Stop", 0)])));

    /// <summary>
    /// The link made again from <paramref name="before"/>'s, each file of <paramref name="after"/> that is one of
    /// <paramref name="before"/>'s given as that very object - held to be the whole link of <paramref name="after"/>.
    /// </summary>
    private static CodeGraphLinked Again(IReadOnlyList<CodeGraphFile> before, IReadOnlyList<CodeGraphFile> after)
    {
        var again = CodeGraphLinker.Relink(CodeGraphLinker.Link(before), after);
        var whole = CodeGraphLinker.Link(after);

        Assert.Equal(whole.Edges, again.Edges);
        Assert.Equal(whole.Symbols, again.Symbols, SymbolComparer.Instance);
        Assert.Equal([.. whole.External], [.. again.External]);
        Assert.Equal(SignsOf(whole), SignsOf(again));
        Assert.True(again.References <= whole.References);
        return again;
    }

    /// <summary>The paths a tree made at random holds its files at.</summary>
    private static readonly string[] RandomPaths = ["lib/x.t", "lib/y.t", "lib/in/w.t", "app/m.t", "app/n.t", "app/ui/v.t", "far/z.t", "top.t", "q/r.t", "s.t"];

    /// <summary>The scopes a tree made at random declares.</summary>
    private static readonly string[] RandomScopes = ["lib", "app", "far", "lib.in", "app.ui"];

    /// <summary>The names a tree made at random declares and names: few enough to meet, enough that a change touches some alone.</summary>
    private static readonly string[] RandomNames = ["A", "B", "C", "D", "E", "F", "G", "H", "Run", "Stop", "Make", "Next"];

    private static T Pick<T>(Random random, IReadOnlyList<T> from) => from[random.Next(from.Count)];

    private static bool Chance(Random random, int one) => random.Next(one) == 0;

    /// <summary>The first parts a dotted name made at random may begin with: a scope, a scope's last part, a name.</summary>
    private static readonly string[] RandomHeads = [.. RandomScopes, "in", "ui", .. RandomNames];

    /// <summary>A name, or a dotted name of two or three parts: a scope, a scope's last part or a name, then names.</summary>
    private static string Dotted(Random random)
        => random.Next(5) switch
        {
            0 or 1 => Pick(random, RandomNames),
            2 => $"{Pick(random, RandomHeads)}.{Pick(random, RandomNames)}.{Pick(random, RandomNames)}",
            _ => $"{Pick(random, RandomHeads)}.{Pick(random, RandomNames)}",
        };

    /// <summary>A file of a tree made at random, at a path of <see cref="RandomPaths"/>.</summary>
    private static CodeGraphFile RandomFile(Random random, string? at = null)
    {
        var path = at ?? Pick(random, RandomPaths);

        if (Chance(random, 15))
        {
            return new CodeGraphFile(path, "t", null);
        }

        var declared = RandomScopes.Where(_ => Chance(random, 2)).Select(scope => new ExtractScope(scope, Open: !Chance(random, 6)) { Parent = Chance(random, 3) ? QualifiedName.Of(Pick(random, RandomScopes)) : null }).ToList();
        var nodes = new List<ExtractNode>();

        for (var types = random.Next(0, 4); types > 0; types--)
        {
            AddType(random, nodes, declared);
        }

        if (Chance(random, 4) && declared.Count > 0)
        {
            AddNamespace(random, nodes, declared);
        }

        if (Chance(random, 3))
        {
            nodes.Add(new ExtractNode($"t:{path}.Go", CodeNodeKinds.Member, "Go", "Go", 1, 1, "Go") { MinArity = 0, MaxArity = 0 });
        }

        // Now and then data - a configuration key, an element, a style rule, a variable, a mixin, keyframes - of a name code
        // names too, and a type that says it is a part of a declaration other files hold parts of.
        for (var data = Chance(random, 2) ? random.Next(1, 3) : 0; data > 0; data--)
        {
            var name = Pick(random, RandomData);

            if (nodes.All(node => node.Id != $"t:{path}#{name}"))
            {
                nodes.Add(new ExtractNode($"t:{path}#{name}", CodeNodeKinds.Member, name, name, 1, 1, name) { SubKind = Pick(random, RandomDataKinds) });
            }
        }

        if (Chance(random, 4) && nodes.FindIndex(node => node.Kind == CodeNodeKinds.Type) is var part and >= 0)
        {
            nodes[part] = nodes[part] with { Part = true };
        }

        var references = new List<ExtractReference>();

        foreach (var type in nodes.Where(node => node.Kind == CodeNodeKinds.Type && Chance(random, 2)))
        {
            references.Add(RandomBase(random, type.Id.Whole()));
        }

        for (var made = nodes.Count == 0 ? 0 : random.Next(0, 9); made > 0; made--)
        {
            references.Add(RandomReference(random, Pick(random, nodes).Id.Whole(), made, Pick(random, nodes).Id.Whole()));
        }

        // Now and then a file of a language that binds its names within it, one that says its directory is a unit of its layout or
        // declares a root of it, and one that publishes names - and what its own imports that take published names take.
        IReadOnlyList<string>? published = Chance(random, 3) ? [.. RandomNames.Where(_ => Chance(random, 4))] : null;

        return new CodeGraphFile(path, Chance(random, 12) ? "u" : "t", new FileExtract(nodes)
        {
            References = references,
            Scopes = declared,
            Imports = RandomImports(random),
            AddressRoots = Chance(random, 5) ? [RandomRoot(random)] : [],
            Literals = nodes.Count > 0 && Chance(random, 3) ? LiteralRow.Table(new LiteralRow(Pick(random, RandomData), 1)) : [],
            BoundWithin = Chance(random, 4),
            FolderUnit = Chance(random, 4),
            LayoutRoots = Chance(random, 6) ? [Pick(random, RandomLayoutRoots)] : null,
            Published = published,
            PublishesTaken = published is not null && Chance(random, 2),
        });
    }

    /// <summary>The roots of the layout a file made at random declares, each beside it: some a directory of the tree's paths, one the file's own, one out of the tree.</summary>
    private static readonly string[] RandomLayoutRoots = ["lib", "app", ".", "..", "../../out"];

    /// <summary>The names a tree made at random gives its data and its literals: some a member's name is, as a naming policy writes it.</summary>
    private static readonly string[] RandomData = ["run", "stop", "Run", "Make", "next_one", "timeout"];

    /// <summary>The kinds of data a tree made at random declares, a key the likeliest: each a kind a reference may reach only as <c>Takes</c> lets it.</summary>
    private static readonly string[] RandomDataKinds = [CodeSubKinds.Key, CodeSubKinds.Key, CodeSubKinds.Element, CodeSubKinds.Rule, CodeSubKinds.StyleVariable, CodeSubKinds.StyleMixin, CodeSubKinds.Keyframes];

    /// <summary>
    /// Adds a type of a scope <paramref name="declared"/> holds, of a type <paramref name="nodes"/> holds - a nested one -
    /// or of none, with members: values of a type or of none, members that take one or no argument, some extending a type.
    /// </summary>
    private static void AddType(Random random, List<ExtractNode> nodes, IReadOnlyList<ExtractScope> declared)
    {
        var holders = nodes.Where(node => node.Kind == CodeNodeKinds.Type).Select(node => node.Qualified).ToList();
        var scope = holders.Count > 0 && Chance(random, 4) ? Pick(random, holders)
            : Chance(random, 4) || declared.Count == 0 ? null
            : Pick(random, declared).Name;
        var name = Pick(random, RandomNames);
        var qualified = scope is null ? name : $"{scope}.{name}";

        if (nodes.Any(node => node.Id == $"t:{qualified}"))
        {
            return;
        }

        nodes.Add(new ExtractNode($"t:{qualified}", CodeNodeKinds.Type, name, qualified, 1, 1, qualified) { Scope = scope });

        for (var members = random.Next(0, 5); members > 0; members--)
        {
            AddMember(random, nodes, qualified);
        }
    }

    /// <summary>Adds a namespace node for a scope <paramref name="declared"/> holds, placed in its parent where it names one.</summary>
    private static void AddNamespace(Random random, List<ExtractNode> nodes, IReadOnlyList<ExtractScope> declared)
    {
        var scope = Pick(random, declared);
        var dot = scope.Name.Whole().LastIndexOf('.');

        if (nodes.Any(node => node.Id == $"n:{scope.Name}"))
        {
            return;
        }

        var name = scope.Name.Whole();
        nodes.Add(new ExtractNode($"n:{name}", CodeNodeKinds.Namespace, name[(dot + 1)..], name, 1, 1, name) { Scope = dot < 0 ? null : QualifiedName.Of(name[..dot]) });
    }

    private static void AddMember(Random random, List<ExtractNode> nodes, string type)
    {
        var member = Pick(random, RandomNames);
        var value = Chance(random, 2);
        var arity = random.Next(0, 2);
        var id = value ? $"t:{type}.{member}" : $"t:{type}.{member}({arity})";

        // Now and then a second declaration of one name and parameters in a file: a prototype and its body, each its own id.
        id = !value && Chance(random, 6) && nodes.Any(node => node.Id == id) ? $"{id}~2" : id;

        if (nodes.Any(node => node.Id == id))
        {
            return;
        }

        nodes.Add(new ExtractNode(id, CodeNodeKinds.Member, member, $"{type}.{member}", 1, 1, member)
        {
            Scope = type,
            Parent = $"t:{type}",
            MinArity = value ? null : arity,
            MaxArity = value ? null : arity,
            ValueType = value && !Chance(random, 3) ? Dotted(random) : null,
            Extends = !value && Chance(random, 3) ? Dotted(random) : null,
            Prototype = !value && Chance(random, 8),
            Test = !value && Chance(random, 8),
            SeenIn = Chance(random, 8) ? QualifiedName.Of(Pick(random, RandomScopes)) : null,
            Exports = Chance(random, 8) ? [Pick(random, RandomNames)] : null,
        });
    }

    private static ExtractReference RandomBase(Random random, string type)
        => new(type, Pick(random, [CodeEdgeKinds.Inherits, CodeEdgeKinds.Implements, CodeEdgeKinds.MixesIn]), Pick(random, RandomNames), 1)
        {
            Qualifier = random.Next(4) switch
            {
                0 => new ExtractQualifier(CodeQualifierKinds.Type, Pick(random, RandomScopes)),
                1 => new ExtractQualifier(CodeQualifierKinds.Type, Dotted(random)),
                _ => null,
            },
        };

    /// <summary>A reference made at random from <paramref name="from"/>, now and then of a style sheet's kind or to <paramref name="own"/>, a node of its own file.</summary>
    private static ExtractReference RandomReference(Random random, string from, int line, string own)
        => new(from, Pick(random, [CodeEdgeKinds.Calls, CodeEdgeKinds.Calls, CodeEdgeKinds.References, CodeEdgeKinds.Uses]), Pick(random, [.. RandomNames, .. RandomData]), line)
        {
            Arity = random.Next(0, 2),
            Probe = Chance(random, 10),
            Qualifier = random.Next(17) switch
            {
                12 => new ExtractQualifier(CodeQualifierKinds.StyleVariable),
                13 => new ExtractQualifier(CodeQualifierKinds.StyleMixin),
                14 => new ExtractQualifier(CodeQualifierKinds.Keyframes),
                15 => new ExtractQualifier(CodeQualifierKinds.Own, own),
                16 => new ExtractQualifier(CodeQualifierKinds.Fixture),
                0 or 1 => null,
                2 => new ExtractQualifier(CodeQualifierKinds.Named, Dotted(random)),
                3 => new ExtractQualifier(CodeQualifierKinds.Type, Dotted(random)),
                4 or 5 => new ExtractQualifier(CodeQualifierKinds.Value, Dotted(random)),
                6 => new ExtractQualifier(CodeQualifierKinds.This, null),
                7 => new ExtractQualifier(CodeQualifierKinds.Base, null),
                8 => new ExtractQualifier(CodeQualifierKinds.Scope, random.Next(2) == 0 ? null : QualifiedName.Of(Pick(random, RandomScopes))),
                9 => new ExtractQualifier(CodeQualifierKinds.Selector),
                10 => new ExtractQualifier(CodeQualifierKinds.Key, random.Next(2) == 0 ? null : QualifiedName.Of(Pick(random, ["t", "u"]))),
                _ => new ExtractQualifier(CodeQualifierKinds.Prefix, "p"),
            },
        };

    /// <summary>Up to three imports made at random, one of a file standing in for the one of a file before it now and then.</summary>
    private static List<ExtractImport> RandomImports(Random random)
    {
        var imports = new List<ExtractImport>();

        for (var made = random.Next(0, 4); made > 0; made--)
        {
            var import = RandomImport(random);
            imports.Add(import.Form is not (CodeImportForms.Scope or CodeImportForms.Directory) && imports.Count > 0 && imports[^1].Form is not (CodeImportForms.Scope or CodeImportForms.Directory) && Chance(random, 2) ? import with { Fallback = true } : import);
        }

        return imports;
    }

    private static ExtractImport RandomImport(Random random)
        => random.Next(6) switch
        {
            3 => Import(CodeImportForms.Directory, Pick(random, [.. RandomScopes, "lib.A", "app.B"])) with { Serves = Chance(random, 3) ? CodeQualifierKinds.Fixture : null },
            5 => Import(CodeImportForms.Rooted, Pick(random, ["x.t", "in/w.t", "m.t", "ui/v.t", "y.t"])) with
            {
                Prefix = Chance(random, 2) ? "p" : null,
                Reexports = Chance(random, 3),
            } is var rooted && rooted.Prefix is null && Chance(random, 4) ? rooted with { Published = true } : rooted,
            0 or 1 => Import(CodeImportForms.Scope, Pick(random, [.. RandomScopes, "lib.A", "app.B", "far.C"])) with
            {
                Prefix = Chance(random, 3) ? Pick(random, ["p", "A"]) : null,
                Global = Chance(random, 6),
                Show = Chance(random, 6) ? [Pick(random, RandomNames)] : null,
                Hide = Chance(random, 6) ? [Pick(random, RandomNames)] : null,
            },
            2 => Import(Pick(random, [CodeImportForms.Relative, CodeImportForms.Ending]), Pick(random, ["../lib/x.t", "x.t", "../top.t", "y.t", "../app/m.t", "in/w.t", "lib/y.t", "z.t"])) with
            {
                Prefix = Chance(random, 3) ? "p" : null,
            } is var file && Chance(random, 4) ? file with { Prefix = null, Export = true, Hide = Chance(random, 3) ? [Pick(random, RandomNames)] : null }
                : file.Prefix is null && Chance(random, 5) ? file with { Shares = true }
                : RandomFileImport(random, file),
            _ => Import(CodeImportForms.Address, Pick(random, ["pkg:x.t", "pkg:in/w.t", "q:m.t", "q:ui/v.t"])),
        };

    /// <summary>
    /// <paramref name="file"/>, an import of a file that is no export, now and then made what a language that binds its names within
    /// the file writes: its names its importers' too, a place of its layout, one of two places a name may stand at, one whose prefix
    /// stands for the one name it shows, or one that takes the names a file publishes.
    /// </summary>
    private static ExtractImport RandomFileImport(Random random, ExtractImport file)
    {
        file = file with
        {
            Reexports = Chance(random, 3),
            Layout = file.Form == CodeImportForms.Relative && Chance(random, 3),
            Either = Chance(random, 6),
        };

        return file.Prefix is not null && Chance(random, 3) ? file with { Binds = true, Show = [Pick(random, RandomNames)] }
            : file.Prefix is null && Chance(random, 5) ? file with { Published = true }
            : file;
    }

    private static ExtractAddressRoot RandomRoot(Random random)
        => (Chance(random, 3)
            ? new ExtractAddressRoot(Pick(random, ["pkg:x.t", "q:m.t"]), Pick(random, ["../lib/x.t", "../app/m.t", "lib", "x.t"])) { Whole = true }
            : new ExtractAddressRoot(Pick(random, ["pkg:", "q:"]), Pick(random, ["../lib", "../app", "lib", ".", "../../out"]))) with { Local = Chance(random, 3) };

    /// <summary>
    /// <paramref name="tree"/> with one small change of one file made at random: a value's type, what a member extends, a
    /// scope's parent or whether it is closed, an import, an address root, a base, a declaration come or gone, what holds
    /// one, the kind of data one is, lines moved, a language - or a file come, gone or made anew.
    /// </summary>
    private static CodeGraphFile[] Changed(Random random, CodeGraphFile[] tree)
    {
        var file = Pick(random, tree);

        switch (random.Next(14))
        {
            case 0:
                var made = RandomFile(random);
                return Replaced(tree, made.Path, made);

            case 1 when tree.Length > 2:
                return [.. tree.Where(other => other.Path != file.Path)];
        }

        if (file.Extract is not { } extract)
        {
            return Replaced(tree, file.Path, RandomFile(random, file.Path));
        }

        var nodes = extract.Nodes.ToList();
        var references = extract.References.ToList();
        var scopes = extract.Scopes.ToList();
        var imports = extract.Imports.ToList();
        var roots = extract.AddressRoots.ToList();
        var language = file.Language;

        void Each(Func<ExtractNode, bool> which, Func<ExtractNode, ExtractNode> change)
        {
            var at = nodes.FindIndex(node => which(node));

            if (at >= 0)
            {
                nodes[at] = change(nodes[at]);
            }
        }

        switch (random.Next(21))
        {
            case 0:
                Each(node => node.Kind == CodeNodeKinds.Member && node.MinArity is null && Chance(random, 2), node => node with { ValueType = Chance(random, 4) ? null : Dotted(random) });
                Each(node => Chance(random, 3), node => node with { SeenIn = node.SeenIn is null ? QualifiedName.Of(Pick(random, RandomScopes)) : null });
                Each(node => Chance(random, 4), node => node with { Exports = node.Exports is null ? [Pick(random, RandomNames)] : null });
                break;

            case 13:
                Each(node => node.Kind == CodeNodeKinds.Member && node.MinArity is not null && Chance(random, 2), node => node with { MinArity = 1 - node.MinArity, MaxArity = 1 - node.MinArity });
                break;

            case 14 when imports.Count > 0:
                var changing = random.Next(imports.Count);
                imports[changing] = random.Next(9) switch
                {
                    4 => imports[changing] with { Reexports = !imports[changing].Reexports },
                    5 when imports[changing].Form == CodeImportForms.Relative && !imports[changing].Export => imports[changing] with { Layout = !imports[changing].Layout },
                    6 when imports[changing].Form is not (CodeImportForms.Scope or CodeImportForms.Directory) && !imports[changing].Export => imports[changing] with { Either = !imports[changing].Either },
                    7 when imports[changing] is { Form: not (CodeImportForms.Scope or CodeImportForms.Directory), Prefix: null, Show: null, Hide: null, Export: false, Binds: false } => imports[changing] with { Published = !imports[changing].Published },
                    8 when imports[changing] is { Form: CodeImportForms.Directory, Global: false } => imports[changing] with { Serves = imports[changing].Serves is null ? CodeQualifierKinds.Fixture : null },
                    0 when imports[changing] is { Binds: false, Published: false } => imports[changing] with { Prefix = imports[changing].Prefix is null ? Pick(random, ["p", "A"]) : null },
                    1 when imports[changing] is { Binds: false, Published: false } => imports[changing] with { Show = imports[changing].Show is null ? [Pick(random, RandomNames)] : null },
                    2 when imports[changing] is { Binds: false, Published: false } => imports[changing] with { Hide = imports[changing].Hide is null ? [Pick(random, RandomNames)] : null },
                    3 when imports[changing] is { Form: not (CodeImportForms.Scope or CodeImportForms.Directory), Prefix: null } => imports[changing] with { Export = !imports[changing].Export, Shares = false },
                    // An import made for the language's every file serves no qualifier's references: one that serves loses it.
                    _ => imports[changing] with { Global = !imports[changing].Global, Serves = null },
                };
                break;

            case 15:
                // A type named otherwise, its id kept: its qualified name and its run's end move, and what its members are in stays.
                Each(node => node.Kind == CodeNodeKinds.Type && Chance(random, 2), node => node with { Qualified = Pick(random, [.. RandomHeads, "lib.in"]) + "." + node.Name });
                break;

            case 16 when nodes.FirstOrDefault(node => node.Kind == CodeNodeKinds.Type) is { } renamed:
                // A type whose id is another's: one type of two parts, or two of one - what is in it, and its references, with it.
                var id = $"t:{Pick(random, RandomScopes)}.{renamed.Name}";

                if (nodes.All(node => node.Id != id))
                {
                    nodes = [.. nodes.Select(node => node.Id == renamed.Id ? node with { Id = id } : node.Parent == renamed.Id ? node with { Parent = id } : node)];
                    references = [.. references.Select(reference => reference.From == renamed.Id ? reference with { From = id } : reference)];
                }

                break;

            case 18 when nodes.Count > 1:
                // What holds a declaration: none, which sets it at its file's top where it is in no scope, or another.
                var held = random.Next(nodes.Count);
                nodes[held] = nodes[held] with { Parent = nodes[held].Parent is null ? Pick(random, nodes).Id : null };
                break;

            case 17:
                if (nodes.FirstOrDefault(node => node.Kind == CodeNodeKinds.Namespace) is { } space && Chance(random, 2))
                {
                    nodes.Remove(space);
                    references.RemoveAll(reference => reference.From == space.Id);
                }
                else if (scopes.Count > 0)
                {
                    AddNamespace(random, nodes, scopes);
                }

                break;

            case 1:
                Each(node => node.Kind == CodeNodeKinds.Member && node.MinArity is not null && Chance(random, 2), node => node with { Extends = Chance(random, 3) ? null : Dotted(random) });
                break;

            case 2 when scopes.Count > 0:
                var parented = random.Next(scopes.Count);
                scopes[parented] = scopes[parented] with { Parent = Chance(random, 3) ? null : QualifiedName.Of(Pick(random, RandomScopes)) };
                break;

            case 3 when scopes.Count > 0:
                var toggled = random.Next(scopes.Count);
                scopes[toggled] = scopes[toggled] with { Open = !scopes[toggled].Open };
                break;

            case 4:
                if (imports.Count > 0 && Chance(random, 2))
                {
                    imports.RemoveAt(random.Next(imports.Count));
                }
                else
                {
                    imports.Insert(random.Next(imports.Count + 1), RandomImport(random));
                }

                break;

            case 5:
                roots = roots.Count > 0 && Chance(random, 3) ? [] : roots.Count > 0 && Chance(random, 2) ? [Chance(random, 2) ? roots[0] with { Whole = !roots[0].Whole } : roots[0] with { Local = !roots[0].Local }] : [RandomRoot(random)];
                break;

            case 6:
                var bases = references.FindIndex(reference => reference.Kind is CodeEdgeKinds.Inherits or CodeEdgeKinds.Implements or CodeEdgeKinds.MixesIn);
                var type = nodes.FirstOrDefault(node => node.Kind == CodeNodeKinds.Type);

                if (bases >= 0 && Chance(random, 3))
                {
                    references.RemoveAt(bases);
                }
                else if (bases >= 0)
                {
                    references[bases] = references[bases] with { Name = Pick(random, RandomNames) };
                }
                else if (type is not null)
                {
                    references.Add(RandomBase(random, type.Id.Whole()));
                }

                break;

            case 7:
                if (nodes.FirstOrDefault(node => node.Kind == CodeNodeKinds.Type && Chance(random, 2)) is { } holder)
                {
                    AddMember(random, nodes, holder.Qualified.Whole());
                }
                else
                {
                    AddType(random, nodes, scopes);
                }

                break;

            case 8 when nodes.Count > 0:
                var gone = Pick(random, nodes).Id;
                nodes.RemoveAll(node => node.Id == gone || node.Parent == gone);
                references.RemoveAll(reference => nodes.All(node => node.Id != reference.From));
                break;

            case 9:
                nodes = [.. nodes.Select(node => node with { Start = node.Start + 1, End = node.End + 2 })];
                references = [.. references.Select(reference => reference with { Line = reference.Line + 3 })];
                break;

            case 10:
                language = language == "t" ? "u" : "t";
                break;

            case 11 when nodes.Count > 0:
                references.Add(RandomReference(random, Pick(random, nodes).Id.Whole(), references.Count + 1, Pick(random, nodes).Id.Whole()));
                break;

            case 19:
                // Data made another kind under its id - a mixin become a function, a key an element - which what may reach
                // it reads.
                Each(node => CodeSubKinds.IsData(node.SubKind) && Chance(random, 2), node => node with { SubKind = Pick(random, RandomDataKinds) });
                break;

            case 20:
                // What a file says of itself that another file's finding reads: whether its names are bound within it, whether its
                // directory is a unit of its layout, the roots it declares, the names it publishes and whether what its imports take
                // is among them.
                extract = random.Next(5) switch
                {
                    0 => extract with { BoundWithin = !extract.BoundWithin },
                    1 => extract with { FolderUnit = !extract.FolderUnit },
                    2 => extract with { LayoutRoots = extract.LayoutRoots is null ? [Pick(random, RandomLayoutRoots)] : null },
                    3 => extract with { Published = extract.Published is null ? [Pick(random, RandomNames)] : null, PublishesTaken = false },
                    _ => extract.Published is null ? extract : extract with { PublishesTaken = !extract.PublishesTaken },
                };
                break;

            default:
                Each(node => node.Kind == CodeNodeKinds.Type && Chance(random, 2), node => node with { Scope = Chance(random, 3) ? null : QualifiedName.Of(Pick(random, RandomScopes)) });
                break;
        }

        return Replaced(tree, file.Path, file with
        {
            Language = language,
            Extract = extract with { Nodes = nodes, References = references, Scopes = scopes, Imports = imports, AddressRoots = roots },
        });
    }

    /// <summary><paramref name="world"/> with the file at <paramref name="path"/> made <paramref name="version"/>, or gone where it is null - and added where it was not there.</summary>
    private static CodeGraphFile[] Replaced(CodeGraphFile[] world, string path, CodeGraphFile? version)
        => [.. world.Where(file => file.Path != path), .. version is null ? [] : new[] { version }];

    /// <summary>A tree that reads every kind of thing a finding may read.</summary>
    private static CodeGraphFile[] World()
        =>
        [
            File("lib/base.t", [Type("lib.Base"), Member("lib.Base.Run", 0), Member("lib.Base.Stop", 0)], scopes: [Open("lib")]),
            File("lib/shape.t", [Type("lib.Shape"), Member("lib.Shape.Area", 0), Value("lib.Shape.Next", "Shape")], [Ref("lib.Shape", CodeEdgeKinds.Inherits, "Base")], [Open("lib")]),
            File("lib/ext.t", [Type("lib.Ext"), Member("lib.Ext.Scale", 1) with { Extends = "Shape" }], scopes: [Open("lib")]),
            File(
                "app/main.t",
                [Type("app.Main"), Member("app.Main.Go", 0), Value("app.Main.shape", "Shape")],
                [
                    Call("app.Main.Go", "Run", 0, Named("shape")),
                    Call("app.Main.Go", "Scale", 1, Named("shape")) with { Line = 2 },
                    Call("app.Main.Go", "Area", 0, new ExtractQualifier(CodeQualifierKinds.Value, "shape.Next")) with { Line = 3 },
                    Call("app.Main.Go", "Helper", 0) with { Line = 4 },
                    Ref("app.Main", CodeEdgeKinds.References, "Shape") with { Line = 5 },
                    Call("app.Main.Go", "Secret", 0) with { Line = 6 },
                    Call("app.Main.Go", "Tool", 0, new ExtractQualifier(CodeQualifierKinds.Prefix, "g")) with { Line = 7 },
                ],
                [Open("app")],
                [Import(CodeImportForms.Scope, "lib")]),
            File("app/helper.t", [Member("app.Helper", 0)], scopes: [new ExtractScope("app", Open: true) { Parent = "lib" }]),
            File("app/rel.t", [Member("Rel", 0)], [Call("Rel", "Base", 0)], imports: [Import(CodeImportForms.Relative, "../lib/base.t"), Import(CodeImportForms.Relative, "tools.t") with { Prefix = "g" }]),
            File("app/tools.t", [Member("Tool", 0)]),
            File("cfg/roots.t", []) with { Extract = new FileExtract([]) { AddressRoots = [new("pkg:", "lib")] } },
            File("app/addr.t", [Member("Addr", 0)], [Call("Addr", "Shape", 0)], imports: [Import(CodeImportForms.Address, "pkg:shape.t")]),
            File("cfg/entry.t", []) with { Extract = new FileExtract([]) { AddressRoots = [new("entry", "../app/tools.t") { Whole = true }] } },
            File("app/entry.t", [Member("Entry", 0)], [Call("Entry", "default", 0, Prefix("e"))], imports: [Import(CodeImportForms.Address, "entry") with { Prefix = "e" }]),
            File("glob.t", [], imports: [Import(CodeImportForms.Scope, "lib") with { Global = true }]),
            File("closed.t", [Member("Go2", 0), Member("hidden.Secret", 0) with { Scope = "hidden" }], [Call("Go2", "Secret", 0)], [new ExtractScope("hidden", Open: false)]),
            File("notes.t", []) with { Extract = null },
        ];

    /// <summary>For each file of <see cref="World"/>, the other versions of it a change may make.</summary>
    private static IEnumerable<(string Path, CodeGraphFile[] Versions)> Versions()
    {
        yield return ("lib/base.t",
        [
            File("lib/base.t", [Type("lib.Base"), Member("lib.Base.Run", 0) with { Start = 4, End = 9 }, Member("lib.Base.Stop", 0)], scopes: [Open("lib")]),
            File("lib/base.t", [Type("lib.Base"), Member("lib.Base.Walk", 0), Member("lib.Base.Stop", 0)], scopes: [Open("lib")]),
            File("lib/base.t", [Type("lib.Base"), Member("lib.Base.Run", 0), Member("lib.Base.Run", 1) with { Id = "t:lib.Base.Run(1)" }], scopes: [Open("lib")]),
            File("lib/base.t", [Type("lib.Base"), Member("lib.Base.Run", 0)], scopes: [new ExtractScope("lib", Open: false)]),
        ]);
        yield return ("lib/shape.t",
        [
            File("lib/shape.t", [Type("lib.Shape"), Member("lib.Shape.Area", 0), Value("lib.Shape.Next", "Shape")], [Ref("lib.Shape", CodeEdgeKinds.Inherits, "Ext")], [Open("lib")]),
            File("lib/shape.t", [Type("lib.Shape"), Member("lib.Shape.Area", 0), Value("lib.Shape.Next", "Base")], scopes: [Open("lib")]),
            File("lib/shape.t", [Type("lib.Shape"), Member("lib.Shape.Run", 0), Value("lib.Shape.Next", null)], [Ref("lib.Shape", CodeEdgeKinds.Inherits, "Base")], [Open("lib")]),
        ]);
        yield return ("lib/ext.t",
        [
            File("lib/ext.t", [Type("lib.Ext"), Member("lib.Ext.Scale", 1) with { Extends = "Base" }], scopes: [Open("lib")]),
            File("lib/ext.t", [Type("lib.Ext"), Member("lib.Ext.Scale", 1)], scopes: [Open("lib")]),
            File("lib/ext.t", [Type("far.Ext"), Member("far.Ext.Scale", 1) with { Extends = "Shape" }], scopes: [Open("far")]),
        ]);
        yield return ("app/main.t",
        [
            File("app/main.t", [Type("app.Main"), Member("app.Main.Go", 0), Value("app.Main.shape", "Base")], [Call("app.Main.Go", "Run", 0, Named("shape"))], [Open("app")], [Import(CodeImportForms.Scope, "lib")]),
            File("app/main.t", [Type("app.Main"), Member("app.Main.Go", 0)], [Call("app.Main.Go", "Rel", 0)], [Open("app")]),
        ]);
        yield return ("app/helper.t",
        [
            File("app/helper.t", [Member("app.Helper", 0)], scopes: [Open("app")]),
            File("app/helper.t", [Member("app.Helper", 0), Member("app.Run", 0)], scopes: [new ExtractScope("app", Open: true) { Parent = "lib" }]),
        ]);
        yield return ("app/rel.t",
        [
            File("app/rel.t", [Member("Rel", 0)], [Call("Rel", "Base", 0)], imports: [Import(CodeImportForms.Relative, "../lib/shape.t")]),
            File("app/rel.t", [Member("Rel", 0)], [Call("Rel", "Base", 0)], imports: [Import(CodeImportForms.Ending, "base.t")]),
        ]);
        yield return ("app/tools.t",
        [
            File("app/tools.t", [Member("Tool", 0), Member("Tool2", 0)]),
            File("app/tools.t", [Member("Tool", 0) with { Exports = ["default"] }]),
        ]);
        yield return ("cfg/entry.t",
        [
            File("cfg/entry.t", []) with { Extract = new FileExtract([]) { AddressRoots = [new("entry", "../app") { Whole = true }] } },
            File("cfg/entry.t", []) with { Extract = new FileExtract([]) { AddressRoots = [new("entry", "../lib/base.t") { Whole = true }, new("entry", "../app/tools.t") { Whole = true }] } },
        ]);
        yield return ("cfg/roots.t",
        [
            File("cfg/roots.t", []) with { Extract = new FileExtract([]) { AddressRoots = [new("pkg:", "app")] } },
            File("cfg/roots.t", []) with { Extract = new FileExtract([]) { AddressRoots = [new("lib:", "lib")] } },
        ]);
        yield return ("app/addr.t", [File("app/addr.t", [Member("Addr", 0)], [Call("Addr", "Shape", 0)], imports: [Import(CodeImportForms.Address, "pkg:base.t")])]);
        yield return ("glob.t",
        [
            File("glob.t", [], imports: [Import(CodeImportForms.Scope, "app") with { Global = true }]),
            File("glob.t", []) with { Language = "u" },
        ]);
        yield return ("closed.t",
        [
            File("closed.t", [Member("Go2", 0), Member("hidden.Secret", 0) with { Scope = "hidden" }], [Call("Go2", "Secret", 0)], [new ExtractScope("hidden", Open: true)]),
        ]);
        yield return ("notes.t", [File("notes.t", [Member("Helper", 0)])]);
        yield return ("app/new.t", [File("app/new.t", [Member("app.Helper", 0), Type("lib.Shape")], scopes: [Open("app"), Open("lib")])]);
    }

    /// <summary>
    /// A key of a configuration file gains a <c>uses</c> edge of <c>candidates</c> from the declaration holding a string
    /// literal equal to its last segment or its whole path, and from each member whose name is the segment as a declared
    /// naming policy writes it - the default camelCase joining <c>CallingConvention</c> to <c>callingConvention</c>.
    /// None of it is counted: a use is recall, never a doubt.
    /// </summary>
    [Fact]
    public void AKeyIsJoinedToTheLiteralsAndMembersThatNameIt_AtCandidates()
    {
        var key = new ExtractNode("json:app.json#targets.x64.callingConvention", CodeNodeKinds.Member, "callingConvention", "targets.x64.callingConvention", 2, 2, "targets.x64.callingConvention") { SubKind = "key" };
        var reader = new ExtractNode("c:Reader", CodeNodeKinds.Type, "Reader", "Reader", 1, 9, "Reader");
        var bySegment = new ExtractNode("c:Reader.BySegment", CodeNodeKinds.Member, "BySegment", "Reader.BySegment", 2, 4, "BySegment") { Parent = "c:Reader" };
        var byPath = new ExtractNode("c:Reader.ByPath", CodeNodeKinds.Member, "ByPath", "Reader.ByPath", 5, 8, "ByPath") { Parent = "c:Reader" };
        var property = new ExtractNode("c:Reader.CallingConvention", CodeNodeKinds.Member, "CallingConvention", "Reader.CallingConvention", 6, 7, "CallingConvention") { Parent = "c:Reader" };

        var linked = CodeGraphLinker.Link(
        [
            new CodeGraphFile("app.json", "json", new FileExtract([key])),
            new CodeGraphFile("reader.c", "c", new FileExtract([reader, bySegment, byPath, property])
            {
                Literals = LiteralRow.Table(new LiteralRow("callingConvention", 3, bySegment.Id.Whole()), new LiteralRow("targets.x64.callingConvention", 5, byPath.Id.Whole())),
            }),
        ]);

        Assert.Contains(new CodeEdge("c:Reader.BySegment", "json:app.json#targets.x64.callingConvention", CodeEdgeKinds.Uses, 3, CodeConfidences.Candidates), linked.Edges);
        Assert.Contains(new CodeEdge("c:Reader.ByPath", "json:app.json#targets.x64.callingConvention", CodeEdgeKinds.Uses, 5, CodeConfidences.Candidates), linked.Edges);
        Assert.Contains(new CodeEdge("c:Reader.CallingConvention", "json:app.json#targets.x64.callingConvention", CodeEdgeKinds.Uses, 6, CodeConfidences.Candidates), linked.Edges);
        Assert.Empty(linked.External);
    }

    /// <summary>
    /// The naming policies the link is given say what a key's last segment joins: under <c>snake_case</c> alone,
    /// <c>calling_convention</c> reaches <c>CallingConvention</c> and <c>callingConvention</c> does not.
    /// </summary>
    [Fact]
    public void TheNamingPoliciesSayWhatAKeyJoins()
    {
        var camel = new ExtractNode("json:a.json#callingConvention", CodeNodeKinds.Member, "callingConvention", "callingConvention", 1, 1, "callingConvention") { SubKind = "key" };
        var snake = new ExtractNode("json:a.json#calling_convention", CodeNodeKinds.Member, "calling_convention", "calling_convention", 2, 2, "calling_convention") { SubKind = "key" };
        var property = new ExtractNode("c:Reader.CallingConvention", CodeNodeKinds.Member, "CallingConvention", "Reader.CallingConvention", 1, 1, "CallingConvention");

        foreach (var (naming, camelJoined, snakeJoined) in new[] { ((string?)null, true, false), ("snake_case", false, true), ("kebab-case", false, false) })
        {
            var linked = CodeGraphLinker.Link(
            [
                new CodeGraphFile("a.json", "json", new FileExtract([camel, snake])),
                new CodeGraphFile("reader.c", "c", new FileExtract([property])),
            ], naming is null ? null : [naming]);

            Assert.Equal(camelJoined, linked.Edges.Any(edge => edge.To == camel.Id && edge.From == property.Id));
            Assert.Equal(snakeJoined, linked.Edges.Any(edge => edge.To == snake.Id && edge.From == property.Id));
        }
    }

    /// <summary>
    /// Two declarations of one qualified name that say they are parts - a XAML view and the C# type its <c>x:Class</c>
    /// names - are joined by a <c>partOf</c> edge, exact, from the later id to the first; a declaration that says it is
    /// a part alone, or one of another name, is not.
    /// </summary>
    [Fact]
    public void TwoPartsOfOneDeclarationAreJoinedByPartOf_Exact()
    {
        var view = new ExtractNode("xml:Shop.Cart", CodeNodeKinds.Type, "Cart", "Shop.Cart", 1, 6, "Shop.Cart") { SubKind = "view", Part = true };
        var code = new ExtractNode("c:Shop.Cart", CodeNodeKinds.Type, "Cart", "Shop.Cart", 1, 9, "Shop.Cart") { Part = true };
        var other = new ExtractNode("c:Shop.Cart2", CodeNodeKinds.Type, "Cart2", "Shop.Cart2", 1, 2, "Shop.Cart2") { Part = true };
        var whole = new ExtractNode("c:Shop.Order", CodeNodeKinds.Type, "Order", "Shop.Order", 1, 3, "Shop.Order");

        var linked = CodeGraphLinker.Link(
        [
            new CodeGraphFile("Views/Cart.xaml", "xml", new FileExtract([view])),
            new CodeGraphFile("Cart.xaml.cs", "c", new FileExtract([code, other, whole])),
        ]);

        Assert.Contains(new CodeEdge("xml:Shop.Cart", "c:Shop.Cart", CodeEdgeKinds.PartOf, 1, CodeConfidences.Exact), linked.Edges);
        Assert.DoesNotContain(linked.Edges, edge => edge.Kind == CodeEdgeKinds.PartOf && edge.To != "c:Shop.Cart");
    }

    /// <summary>
    /// A <c>$(Name)</c> an MSBuild project writes, a use of a key of the xml extractor's (the <c>key</c> qualifier), reaches
    /// the key of that name a <c>.props</c> file's <c>PropertyGroup</c> declares - held inside the file's section, as a
    /// real extract holds it - and neither the C# property nor the JSON key of that name, which an unqualified use reached.
    /// </summary>
    [Fact]
    public void APropertyAMSBuildProjectUses_ReachesTheXmlKeyOfItsName_AndNoMemberOrOtherKey()
    {
        var project = new CodeGraphFile("app.csproj", "msbuild", new FileExtract([new ExtractNode("msbuild:app.csproj", CodeNodeKinds.Project, "app.csproj", "app.csproj", 1, 3, "app.csproj")])
        {
            References = [new ExtractReference("msbuild:app.csproj", CodeEdgeKinds.Uses, "OutputPath", 2) { Qualifier = new ExtractQualifier(CodeQualifierKinds.Key, "xml") }],
            Imports = [new ExtractImport(CodeImportForms.Relative, "Directory.Build.props", 1)],
        });
        var props = new CodeGraphFile("Directory.Build.props", "xml", new FileExtract(
        [
            new ExtractNode("xml:Directory.Build.props#Project", CodeNodeKinds.Section, "Project", "Directory.Build.props#Project", 1, 3, "Project"),
            new ExtractNode("xml:Directory.Build.props#OutputPath", CodeNodeKinds.Member, "OutputPath", "OutputPath", 2, 2, "OutputPath") { SubKind = CodeSubKinds.Key, Parent = "xml:Directory.Build.props#Project" },
        ]));
        var code = new CodeGraphFile("Build.cs", "c", new FileExtract([Type("App"), Value("App.OutputPath", "string") with { Parent = "t:App" }]));
        var json = new CodeGraphFile("app.json", "json", new FileExtract([new ExtractNode("json:app.json#OutputPath", CodeNodeKinds.Member, "OutputPath", "OutputPath", 1, 1, "OutputPath") { SubKind = CodeSubKinds.Key }]));

        var linked = CodeGraphLinker.Link([project, props, code, json]);

        Assert.Equal(
            [new CodeEdge("msbuild:app.csproj", "xml:Directory.Build.props#OutputPath", CodeEdgeKinds.Uses, 2, CodeConfidences.Exact)],
            linked.Edges.Where(edge => edge.From == "msbuild:app.csproj" && edge.Kind == CodeEdgeKinds.Uses));
    }

    /// <summary>
    /// A node of data - a configuration key, an element, a style rule - is reached by no reference that names it by its
    /// name alone: a call of <c>build()</c> and a use of <c>body</c> written as code names code find the function and the
    /// field of those names, unique, and none of the key, the element or the rule of each name beside them; a use through
    /// an import's prefix - <c>cfg.timeout</c> of a JSON file imported as <c>cfg</c> - reaches the key it names, and a
    /// name an import makes visible by a name the file it imports exports the node by - a CSS module's <c>btn</c>, as
    /// <c>import { btn }</c> binds it - the rule.
    /// </summary>
    [Fact]
    public void DataIsReachedByNoNameAlone_ButAUseThroughAnImportsPrefixReachesTheKey()
    {
        static ExtractNode Data(string id, string name, string subKind) => new(id, CodeNodeKinds.Member, name, name, 1, 1, name) { SubKind = subKind };

        var linked = CodeGraphLinker.Link(
        [
            File("main.t", [Member("Go", 0)],
            [
                Call("Go", "build", 0),
                Ref("Go", CodeEdgeKinds.Uses, "body") with { Line = 2 },
                Ref("Go", CodeEdgeKinds.Uses, "timeout") with { Line = 3, Qualifier = Prefix("cfg") },
                Ref("Go", CodeEdgeKinds.Uses, "btn") with { Line = 4 },
            ], imports: [Import(CodeImportForms.Relative, "config.json") with { Prefix = "cfg" }, Import(CodeImportForms.Relative, "x.module.css")]),
            File("lib.t", [Member("build", 0), Value("body", null)]),
            new CodeGraphFile("compose.yml", "yaml", Extract([Data("yaml:compose.yml#services.web.build", "build", CodeSubKinds.Key)])),
            new CodeGraphFile("site.css", "css", Extract([Data("css:site.css#body", "body", CodeSubKinds.Rule)])),
            new CodeGraphFile("page.xml", "xml", Extract([Data("xml:page.xml#page/body", "body", CodeSubKinds.Element)])),
            new CodeGraphFile("config.json", "json", Extract([Data("json:config.json#timeout", "timeout", CodeSubKinds.Key)])),
            new CodeGraphFile("x.module.css", "css", Extract([Data("css:x.module.css#.btn", ".btn", CodeSubKinds.Rule) with { Exports = ["btn"] }, Data("css:x.module.css#body", "body", CodeSubKinds.Rule)])),
        ]);

        Assert.Equal(
            [
                new CodeEdge("t:Go", "t:build", CodeEdgeKinds.Calls, 1, CodeConfidences.Unique),
                new CodeEdge("t:Go", "t:body", CodeEdgeKinds.Uses, 2, CodeConfidences.Unique),
                new CodeEdge("t:Go", "json:config.json#timeout", CodeEdgeKinds.Uses, 3, CodeConfidences.Exact),
                new CodeEdge("t:Go", "css:x.module.css#.btn", CodeEdgeKinds.Uses, 4, CodeConfidences.Unique),
            ],
            linked.Edges.Where(edge => edge.From == "t:Go").OrderBy(edge => edge.Line));
    }

    /// <summary>
    /// No node a style sheet declares - a variable, a mixin, a function, keyframes, a font face, a rule - is reached by a
    /// name code writes: a PHP <c>$radius</c> and a C# <c>Button()</c> find the code's own of those names, unique, and a
    /// name the code declares nowhere - <c>spin</c>, <c>darken()</c>, <c>Brand</c>, <c>--gap</c>, <c>.btn</c> - finds
    /// nothing, the sheet's nodes of each name beside them.
    /// </summary>
    [Fact]
    public void AStyleSheetsNodes_AreReachedByNoNameCodeWrites()
    {
        static ExtractNode Styled(string name, string subKind) => new($"css:site.scss#{name}", CodeNodeKinds.Member, name, name, 1, 1, name) { SubKind = subKind };

        var linked = CodeGraphLinker.Link(
        [
            File("app.t", [Member("Go", 0), Member("Button", 0), Value("$radius", null)],
            [
                Call("Go", "Button", 0),
                Ref("Go", CodeEdgeKinds.Uses, "$radius") with { Line = 2 },
                Ref("Go", CodeEdgeKinds.Uses, "spin") with { Line = 3 },
                Call("Go", "darken", 2) with { Line = 4 },
                Ref("Go", CodeEdgeKinds.Uses, "Brand") with { Line = 5 },
                Ref("Go", CodeEdgeKinds.Uses, "--gap") with { Line = 6 },
                Ref("Go", CodeEdgeKinds.Uses, ".btn") with { Line = 7 },
            ]),
            new CodeGraphFile("site.scss", "css", Extract(
            [
                Styled("$radius", CodeSubKinds.StyleVariable),
                Styled("--gap", CodeSubKinds.StyleVariable),
                Styled("Button", CodeSubKinds.StyleMixin),
                Styled("darken", CodeSubKinds.StyleFunction) with { MinArity = 2, MaxArity = 2 },
                Styled("spin", CodeSubKinds.Keyframes),
                Styled("Brand", CodeSubKinds.FontFace),
                Styled(".btn", CodeSubKinds.Rule),
            ])),
        ]);

        Assert.Equal(
            [
                new CodeEdge("t:Go", "t:Button", CodeEdgeKinds.Calls, 1, CodeConfidences.Unique),
                new CodeEdge("t:Go", "t:$radius", CodeEdgeKinds.Uses, 2, CodeConfidences.Unique),
            ],
            linked.Edges.Where(edge => edge.From == "t:Go").OrderBy(edge => edge.Line));
    }

    /// <summary>
    /// A style sheet's own references reach its nodes through the qualifiers that say what each names, each among its
    /// kind alone and its own sheet's first, exact: <c>$radius</c> the variable, an <c>@include button</c> the mixin, an
    /// animation's <c>spin</c> the keyframes and an <c>@extend .btn</c> the rule - none the code's of those names - and a
    /// qualifier that names another kind - a mixin <c>spin</c> - none of them.
    /// </summary>
    [Fact]
    public void AStyleSheetsReferences_ReachItsNodes_ThroughQualifiersThatSayWhatTheyName()
    {
        static ExtractNode Styled(string name, string subKind) => new($"css:site.scss#{name}", CodeNodeKinds.Member, name, name, 1, 9, name) { SubKind = subKind };
        static ExtractReference Styling(string kind, string name, int line, string? qualifier)
            => new("css:site.scss#.card", kind, name, line) { Qualifier = qualifier is null ? null : new ExtractQualifier(qualifier) };

        var linked = CodeGraphLinker.Link(
        [
            new CodeGraphFile("site.scss", "css", Extract(
            [
                Styled(".card", CodeSubKinds.Rule),
                Styled("$radius", CodeSubKinds.StyleVariable),
                Styled("button", CodeSubKinds.StyleMixin),
                Styled("spin", CodeSubKinds.Keyframes),
                Styled(".btn", CodeSubKinds.Rule),
            ]) with
            {
                References =
                [
                    Styling(CodeEdgeKinds.Uses, "$radius", 2, CodeQualifierKinds.StyleVariable),
                    Styling(CodeEdgeKinds.Uses, "button", 3, CodeQualifierKinds.StyleMixin),
                    Styling(CodeEdgeKinds.Uses, "spin", 4, CodeQualifierKinds.Keyframes),
                    Styling(CodeEdgeKinds.Inherits, ".btn", 5, CodeQualifierKinds.Selector),
                    Styling(CodeEdgeKinds.Uses, "spin", 6, CodeQualifierKinds.StyleMixin),
                ],
            }),
            File("app.t", [Member("button", 0), Value("$radius", null), Value("spin", null)]),
        ]);

        Assert.Equal(
            [
                new CodeEdge("css:site.scss#.card", "css:site.scss#$radius", CodeEdgeKinds.Uses, 2, CodeConfidences.Exact),
                new CodeEdge("css:site.scss#.card", "css:site.scss#button", CodeEdgeKinds.Uses, 3, CodeConfidences.Exact),
                new CodeEdge("css:site.scss#.card", "css:site.scss#spin", CodeEdgeKinds.Uses, 4, CodeConfidences.Exact),
                new CodeEdge("css:site.scss#.card", "css:site.scss#.btn", CodeEdgeKinds.Inherits, 5, CodeConfidences.Exact),
            ],
            linked.Edges.Where(edge => edge.From == "css:site.scss#.card").OrderBy(edge => edge.Line));
    }

    /// <summary>
    /// The keys' join joins every key a literal or a value's name names, however many: a <c>name</c> a hundred files of
    /// data hold is joined to each of them, of <c>candidates</c>, and so are the copies of one path - one key in each
    /// locale's file, in each environment's - none dropped.
    /// </summary>
    [Fact]
    public void AKeysJoin_JoinsEveryKeyANameNames_HoweverManyHoldIt()
    {
        static CodeGraphFile[] Tree(int keys, string path)
            =>
            [
                .. Enumerable.Range(0, keys).Select(at => new CodeGraphFile($"k{at}.json", "json", new FileExtract([new ExtractNode($"json:k{at}.json#{path}", CodeNodeKinds.Member, path.Split('.')[^1], path, 1, 1, path) { SubKind = CodeSubKinds.Key }]))),
                new CodeGraphFile("reader.c", "c", new FileExtract([Type("Reader") with { End = 3 }, Value("Reader.Name", "string") with { Parent = "t:Reader" }]) { Literals = LiteralRow.Table(new LiteralRow(path, 3)) }),
            ];

        var named = CodeGraphLinker.Link(Tree(100, "name")).Edges.Where(edge => edge.Kind == CodeEdgeKinds.Uses).ToList();

        Assert.Equal(
            [.. Enumerable.Range(0, 100).SelectMany(at => new[] { $"file:reader.c json:k{at}.json#name", $"t:Reader.Name json:k{at}.json#name" }).Order(StringComparer.Ordinal)],
            named.Select(edge => $"{edge.From} {edge.To}").Order(StringComparer.Ordinal));
        Assert.All(named, edge => Assert.Equal(CodeConfidences.Candidates, edge.Confidence));

        var copies = CodeGraphLinker.Link(Tree(6, "checkout.title")).Edges.Where(edge => edge.Kind == CodeEdgeKinds.Uses).ToList();

        Assert.Equal(
            [.. Enumerable.Range(0, 6).Select(at => $"file:reader.c json:k{at}.json#checkout.title").Order(StringComparer.Ordinal)],
            copies.Select(edge => $"{edge.From} {edge.To}").Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A literal names a key by the path .NET's configuration spells it by too - a colon between its places, or a double
    /// underscore as an environment variable writes one, a place of digits an item - and a string that is no such spelling,
    /// one with an empty place among them, names none by it.
    /// </summary>
    [Fact]
    public void ALiteral_NamesAKeyByThePathDotNetsConfigurationSpellsItBy()
    {
        static ExtractNode Key(string path) => new($"json:appsettings.json#{path}", CodeNodeKinds.Member, path.Split('.')[^1], path, 1, 1, path) { SubKind = CodeSubKinds.Key };

        var linked = CodeGraphLinker.Link(
        [
            new CodeGraphFile("appsettings.json", "json", new FileExtract([Key("Payments.TimeoutSeconds"), Key("Limits[1].Value")])),
            new CodeGraphFile("reader.c", "c", new FileExtract([Type("Reader") with { End = 5 }])
            {
                Literals = LiteralRow.Table(
                    new LiteralRow("Payments:TimeoutSeconds", 1),
                    new LiteralRow("Payments__TimeoutSeconds", 2),
                    new LiteralRow("Limits:1:Value", 3),
                    new LiteralRow("Payments::TimeoutSeconds", 4),
                    new LiteralRow("Limits:01:Value", 5)),
            }),
        ]);

        Assert.Equal(
            [("json:appsettings.json#Payments.TimeoutSeconds", 1), ("json:appsettings.json#Payments.TimeoutSeconds", 2), ("json:appsettings.json#Limits[1].Value", 3)],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Uses).Select(edge => (edge.To, edge.Line)));
        Assert.Equal(
            ["Payments.TimeoutSeconds", "Payments.TimeoutSeconds", "Limits[1].Value", null, "Limits.01.Value", null, null],
            new[] { "Payments:TimeoutSeconds", "Payments__TimeoutSeconds", "Limits:1:Value", "Payments::TimeoutSeconds", "Limits:01:Value", "Payments.TimeoutSeconds", ":x" }.Select(CodeKeyPaths.Dotted));
    }

    /// <summary>
    /// A literal names a key whose own name holds a dot by its name and by each spelling .NET's configuration reads it by -
    /// a colon or a double underscore between the places, a dot inside a name staying a dot - and never the nested keys of
    /// the same dots, which only their own spelling names: <c>Logging:LogLevel:Microsoft.AspNetCore</c> is the key
    /// <c>Microsoft.AspNetCore</c> under <c>LogLevel</c>, as the ASP.NET Core template writes it, and
    /// <c>Logging:LogLevel:Microsoft:AspNetCore</c> the nested one.
    /// </summary>
    [Fact]
    public void ALiteral_NamesAKeyWhoseNameHoldsADot_ByItsNameAndEachSpellingDotNetReadsIt_NeverTheNestedKeyOfTheSameDots()
    {
        var json = new DssHarness.Core.Codebase.Languages.JsonExtractor().Extract(
            new CodeFileInput("appsettings.json", """{"Logging":{"LogLevel":{"Microsoft.AspNetCore":"Warning","Microsoft":{"AspNetCore":"Error"}}}}"""));
        var linked = CodeGraphLinker.Link(
        [
            new CodeGraphFile("appsettings.json", "json", json),
            new CodeGraphFile("reader.c", "c", new FileExtract([Type("Reader") with { End = 4 }])
            {
                Literals = LiteralRow.Table(
                    new LiteralRow("Logging:LogLevel:Microsoft.AspNetCore", 1),
                    new LiteralRow("Logging__LogLevel__Microsoft.AspNetCore", 2),
                    new LiteralRow("Logging:LogLevel:Microsoft:AspNetCore", 3),
                    new LiteralRow("Microsoft.AspNetCore", 4)),
            }),
        ]);

        Assert.Equal(
            [("json:appsettings.json#Logging.LogLevel[\"Microsoft.AspNetCore\"]", 1), ("json:appsettings.json#Logging.LogLevel[\"Microsoft.AspNetCore\"]", 2), ("json:appsettings.json#Logging.LogLevel.Microsoft.AspNetCore", 3), ("json:appsettings.json#Logging.LogLevel[\"Microsoft.AspNetCore\"]", 4)],
            linked.Edges.Where(edge => edge is { Kind: CodeEdgeKinds.Uses, Confidence: CodeConfidences.Candidates }).Select(edge => (edge.To, edge.Line)));
    }

    /// <summary>
    /// A literal's path spelled with a colon names a key whose own name holds a dot through a YAML merge as directly as it
    /// names a nested one: <c>job:a.b</c> reaches the merged mapping's key <c>a.b</c>, <c>job.a.b</c> the nested
    /// <c>a</c> then <c>b</c> - where both spelled the same path, and one literal named both.
    /// </summary>
    [Fact]
    public void ALiteralsPath_ThroughAYamlMerge_ReachesAKeyWhoseNameHoldsADot_AndNotTheNestedKeyOfItsDots()
    {
        const string Config = """
            base: &base
              "a.b": 1
              a:
                b: 2
            job:
              <<: *base
            """;
        var yaml = new DssHarness.Core.Codebase.Languages.YamlExtractor().Extract(new CodeFileInput("config.yml", Config));

        string[] Reached(params string[] literals)
            => [.. CodeGraphLinker.Link(
                [
                    new CodeGraphFile("config.yml", "yaml", yaml),
                    new CodeGraphFile("reader.c", "c", new FileExtract([Type("Reader") with { End = literals.Length }]) { Literals = LiteralRow.Table([.. literals.Select((literal, at) => new LiteralRow(literal, at + 1))]) }),
                ]).Edges.Where(edge => edge is { Kind: CodeEdgeKinds.Uses, Confidence: CodeConfidences.Candidates }).Select(edge => $"{edge.Line} {edge.To}")];

        Assert.Equal(["1 yaml:config.yml#base[\"a.b\"]"], Reached("job:a.b"));
        Assert.Equal(["1 yaml:config.yml#base.a.b"], Reached("job.a.b"));
    }

    /// <summary>
    /// A literal writing a path in brackets - as lodash and JSONPath write one - names the key of that path in either quote
    /// mark, and a path with each place quoted needlessly the keys of its places. The loose form, a path's quoted names
    /// written with their own dots as they are, names none: code splits a dotted literal into its places, as .NET's
    /// configuration and lodash do.
    /// </summary>
    [Fact]
    public void ALiteralWritingAPathInBrackets_EitherQuoteMark_NamesTheKeyOfItsPath_AndTheLooseFormNone()
    {
        var json = new DssHarness.Core.Codebase.Languages.JsonExtractor().Extract(
            new CodeFileInput("appsettings.json", """{"Logging":{"LogLevel":{"Default":"Information","Microsoft.AspNetCore":"Warning"}}}"""));
        var linked = CodeGraphLinker.Link(
        [
            new CodeGraphFile("appsettings.json", "json", json),
            new CodeGraphFile("reader.c", "c", new FileExtract([Type("Reader") with { End = 4 }])
            {
                Literals = LiteralRow.Table(
                    new LiteralRow("Logging.LogLevel[\"Microsoft.AspNetCore\"]", 1),
                    new LiteralRow("Logging.LogLevel['Microsoft.AspNetCore']", 2),
                    new LiteralRow("""["Logging"]["LogLevel"]["Default"]""", 3),
                    new LiteralRow("Logging.LogLevel.Microsoft.AspNetCore", 4)),
            }),
        ]);

        Assert.Equal(
            [("json:appsettings.json#Logging.LogLevel[\"Microsoft.AspNetCore\"]", 1), ("json:appsettings.json#Logging.LogLevel[\"Microsoft.AspNetCore\"]", 2), ("json:appsettings.json#Logging.LogLevel.Default", 3)],
            linked.Edges.Where(edge => edge is { Kind: CodeEdgeKinds.Uses, Confidence: CodeConfidences.Candidates }).Select(edge => (edge.To, edge.Line)));
    }

    /// <summary>
    /// A literal's path that runs through what a YAML file repeats by reference reaches the key it runs on into: through a
    /// mapping's merge to the merged mapping's key, and through an alias to its anchor's - the longest part of the path the
    /// file holds deciding, so a key the merging mapping writes itself shadows the merged one's; and references that come
    /// round to themselves end there, naming nothing more.
    /// </summary>
    [Fact]
    public void ALiteralsPath_RunsThroughAYamlAliasOrMerge_ToTheKeyItReaches()
    {
        const string Config = """
            defaults: &defaults
              timeout: 30
              retry:
                count: 3
            job:
              <<: *defaults
              timeout: 60
            copy: *defaults
            """;
        var yaml = new DssHarness.Core.Codebase.Languages.YamlExtractor().Extract(new CodeFileInput("config.yml", Config));

        string[] Reached(params string[] literals)
            => [.. CodeGraphLinker.Link(
                [
                    new CodeGraphFile("config.yml", "yaml", yaml),
                    new CodeGraphFile("reader.c", "c", new FileExtract([Type("Reader") with { End = literals.Length }]) { Literals = LiteralRow.Table([.. literals.Select((literal, at) => new LiteralRow(literal, at + 1))]) }),
                ]).Edges.Where(edge => edge is { Kind: CodeEdgeKinds.Uses, Confidence: CodeConfidences.Candidates }).Select(edge => $"{edge.Line} {edge.To}")];

        Assert.Equal(["1 yaml:config.yml#defaults.retry.count"], Reached("job.retry.count"));
        Assert.Equal(["1 yaml:config.yml#job.timeout"], Reached("job.timeout"));
        Assert.Equal(["1 yaml:config.yml#defaults.timeout"], Reached("copy.timeout"));
        Assert.Equal(["1 yaml:config.yml#defaults.retry.count"], Reached("copy.retry.count"));
        Assert.Equal(["1 yaml:config.yml#defaults.retry.count"], Reached("job:retry:count"));
        Assert.Empty(Reached("job.retry.none"));

        // Two sections of a file of data that each refer to the other: a path through them ends where it comes round.
        static ExtractNode Section(string path) => new($"d:ring.d#{path}", CodeNodeKinds.Section, path, $"ring.d#{path}", 1, 1, path);

        var ring = new CodeGraphFile("ring.d", "d", new FileExtract([Section("x"), Section("y"), new ExtractNode("d:ring.d#y.z", CodeNodeKinds.Member, "z", "y.z", 1, 1, "y.z") { SubKind = CodeSubKinds.Key, Parent = "d:ring.d#y" }])
        {
            References =
            [
                new ExtractReference("d:ring.d#x", CodeEdgeKinds.Uses, "y", 1) { Qualifier = new ExtractQualifier(CodeQualifierKinds.Own, "d:ring.d#y") },
                new ExtractReference("d:ring.d#y", CodeEdgeKinds.Uses, "x", 1) { Qualifier = new ExtractQualifier(CodeQualifierKinds.Own, "d:ring.d#x") },
            ],
        });
        var around = CodeGraphLinker.Link([ring, new CodeGraphFile("reader.c", "c", new FileExtract([Type("Reader") with { End = 2 }]) { Literals = LiteralRow.Table(new LiteralRow("x.w", 1), new LiteralRow("x.z", 2)) })]);

        Assert.Equal([("d:ring.d#y.z", 2)], around.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Uses && edge.From == "file:reader.c").Select(edge => (edge.To, edge.Line)));
    }

    /// <summary>
    /// What a file imports is found through the files it exports however long their chain, each file once: a name declared
    /// at the end of five thousand files that each export the next is found from the first - on a thread whose stack is a
    /// fraction of what following the chain by recursion would take.
    /// </summary>
    [Fact]
    public void ANameAtTheEndOfALongChainOfExports_IsFound_OnAStackOfItsOwn()
    {
        const int Chain = 5000;
        var files = Enumerable.Range(0, Chain)
            .Select(at => File($"e/{at}.t", at == Chain - 1 ? [Member("Far", 0)] : [], imports: [new ExtractImport(CodeImportForms.Relative, $"{at + 1}.t", 1) { Export = true }]))
            .Append(File("use.t", [Member("Go", 0)], [Call("Go", "Far", 0)], imports: [Import(CodeImportForms.Relative, "e/0.t")]))
            .ToArray();
        CodeGraphLinked? linked = null;
        var thread = new Thread(() => linked = CodeGraphLinker.Link(files), 256 * 1024);

        thread.Start();
        thread.Join();

        Assert.Contains(new CodeEdge("t:Go", "t:Far", CodeEdgeKinds.Calls, 1, CodeConfidences.Unique), linked!.Edges);
    }

    /// <summary>
    /// A key is joined to the name of a value - a field, a property - alone, never to a member that is called; a policy
    /// left out joins nothing, the key's own spelling under <c>exact</c> among it; and <c>snake_case</c> and
    /// <c>kebab-case</c> take one word alone, and a word with digits, as they take several.
    /// </summary>
    [Fact]
    public void AKeyJoinsAValuesNameAlone_AsTheDeclaredPoliciesWriteIt()
    {
        static ExtractNode Key(string name) => new($"json:a.json#{name}", CodeNodeKinds.Member, name, name, 1, 1, name) { SubKind = CodeSubKinds.Key };

        var keys = new CodeGraphFile("a.json", "json", new FileExtract([Key("timeout"), Key("s3_bucket"), Key("retry-count"), Key("retries")]));
        var code = new CodeGraphFile("reader.c", "c", new FileExtract(
        [
            Type("Reader"),
            Value("Reader.timeout", "int") with { Parent = "t:Reader" },
            Value("Reader.Timeout", "int") with { Parent = "t:Reader" },
            Value("Reader.S3Bucket", "string") with { Parent = "t:Reader" },
            Value("Reader.RetryCount", "int") with { Parent = "t:Reader" },
            Member("Reader.Retries", 0) with { Parent = "t:Reader" },
        ]));

        string[] Joined(params string[] naming)
            => [.. CodeGraphLinker.Link([keys, code], naming.Length == 0 ? null : naming).Edges
                .Where(edge => edge.Kind == CodeEdgeKinds.Uses)
                .Select(edge => $"{edge.From} {edge.To}")
                .Order(StringComparer.Ordinal)];

        Assert.Equal(["t:Reader.Timeout json:a.json#timeout", "t:Reader.timeout json:a.json#timeout"], Joined());
        Assert.Equal(["t:Reader.S3Bucket json:a.json#s3_bucket", "t:Reader.Timeout json:a.json#timeout"], Joined(CodebaseGraphShape.SnakeCase));
        Assert.Equal(["t:Reader.RetryCount json:a.json#retry-count", "t:Reader.Timeout json:a.json#timeout"], Joined(CodebaseGraphShape.KebabCase));
        Assert.Equal(["t:Reader.timeout json:a.json#timeout"], Joined(CodebaseGraphShape.Exact));
    }

    /// <summary>
    /// A class a document writes is a use of the rule of that name, looked for among rules alone: the style sheet's
    /// <c>.btn</c> reaches the page that writes <c>class="btn"</c> - the repository's, unique, as the page neither holds
    /// nor links it - and nothing else of that name is its target.
    /// </summary>
    [Fact]
    public void AClassADocumentWrites_IsAUseOfTheRuleOfThatName_AmongRulesAlone()
    {
        var rule = new ExtractNode("css:site.css#.btn", CodeNodeKinds.Member, ".btn", ".btn", 8, 11, ".btn") { SubKind = CodeSubKinds.Rule };
        var member = new ExtractNode("c:Helper", CodeNodeKinds.Member, ".btn", "Helper..btn", 1, 1, ".btn");
        var page = new ExtractNode("html:index.html#body", CodeNodeKinds.Section, "body", "index.html#body", 1, 9, "body");

        var linked = CodeGraphLinker.Link(
        [
            new CodeGraphFile("site.css", "css", new FileExtract([rule])),
            new CodeGraphFile("index.html", "html", new FileExtract([page])
            {
                References = [new ExtractReference(page.Id, CodeEdgeKinds.Uses, ".btn", 5) { Qualifier = new ExtractQualifier(CodeQualifierKinds.Selector) }],
            }),
            new CodeGraphFile("helper.c", "c", new FileExtract([member])),
        ]);

        Assert.Contains(new CodeEdge("html:index.html#body", "css:site.css#.btn", CodeEdgeKinds.Uses, 5, CodeConfidences.Unique), linked.Edges);
        Assert.DoesNotContain(linked.Edges, edge => edge.To == "c:Helper" && edge.Kind == CodeEdgeKinds.Uses);
        Assert.DoesNotContain(linked.External, pair => pair.Key == page.Id);
    }

    /// <summary>
    /// A node standing for a suffix written onto others' (<see cref="ExtractNode.SuffixOf"/>) is matched to a name each
    /// node at each place in the name once, however many others reach it: forty levels of two nodes, each written onto
    /// both below it - a trillion ways down - match a name they write at once, and one they do not write to nothing.
    /// </summary>
    [Fact]
    public void ANodeWrittenOntoOthers_IsMatchedOnceAtEachPlaceOfTheName_HoweverManyReachIt()
    {
        const int Levels = 40;
        static ExtractNode Rule(string id, string name) => new($"css:a.css#{id}", CodeNodeKinds.Member, name, name, 1, 1, name) { SubKind = CodeSubKinds.Rule };

        var nodes = new List<ExtractNode> { Rule(".a", ".a"), Rule(".b", ".a") with { Id = "css:a.css#.a~2" } };

        for (var level = 0; level < Levels; level++)
        {
            QualifiedName[] below = [.. (level == 0 ? (string[])["css:a.css#.a", "css:a.css#.a~2"] : [$"css:a.css#l{level - 1}a", $"css:a.css#l{level - 1}b"]).Select(QualifiedName.Of)];
            nodes.Add(Rule($"l{level}a", "&-s") with { SuffixOf = below });
            nodes.Add(Rule($"l{level}b", "&-s") with { SuffixOf = below });
        }

        var page = new ExtractNode("html:p.html#body", CodeNodeKinds.Section, "body", "p.html#body", 1, 2, "body");
        var written = ".a" + string.Concat(Enumerable.Repeat("-s", Levels));
        var watch = Stopwatch.StartNew();
        var linked = Link(
            new CodeGraphFile("a.css", "css", Extract(nodes)),
            new CodeGraphFile("p.html", "html", Extract([page]) with
            {
                References =
                [
                    new ExtractReference(page.Id, CodeEdgeKinds.Uses, written, 1) { Qualifier = new ExtractQualifier(CodeQualifierKinds.Selector) },
                    new ExtractReference(page.Id, CodeEdgeKinds.Uses, written + "-t", 2) { Qualifier = new ExtractQualifier(CodeQualifierKinds.Selector) },
                ],
            }));
        watch.Stop();

        Assert.Equal(
            [new CodeEdge(page.Id.Whole(), $"css:a.css#l{Levels - 1}a", CodeEdgeKinds.Uses, 1, CodeConfidences.Candidates), new CodeEdge(page.Id.Whole(), $"css:a.css#l{Levels - 1}b", CodeEdgeKinds.Uses, 1, CodeConfidences.Candidates)],
            linked.Edges.Where(edge => edge.From == page.Id));
        // The slowest runner of the matrix, under the suite's own load, matches these levels in six seconds; a wrong
        // matching - each level matched through every one below it - is the square of them, far past any bound here.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"matching {Levels} levels took {watch.Elapsed}");
    }

    /// <summary>
    /// A style sheet's selector is looked for through each of its file's imports in turn as one walk: a file a walk before
    /// reached - here the one hub every import forwards, and each file the hub forwards - is not walked again for the next
    /// import, so a sheet of many imports that share what they forward is linked in time that grows with the files, not
    /// with the imports times what they share.
    /// </summary>
    [Fact]
    public void AStyleSheetsImports_AreWalkedAsOne_AFileAWalkReachedNeverWalkedAgain()
    {
        const int Imports = 3_000;
        const int Shared = 3_000;
        static ExtractNode Rule(string path, string name) => new($"css:{path}#{name}", CodeNodeKinds.Member, name, name, 1, 1, name) { SubKind = CodeSubKinds.Rule };
        static ExtractImport Forward(string target) => new(CodeImportForms.Relative, target, 1) { Export = true };

        var main = Extract([Rule("main.css", ".m")]) with
        {
            Imports = [.. Enumerable.Range(0, Imports).Select(at => new ExtractImport(CodeImportForms.Relative, $"i{at}.css", 1))],
            References = [new ExtractReference("css:main.css#.m", CodeEdgeKinds.Uses, ".found", 2) { Qualifier = new ExtractQualifier(CodeQualifierKinds.Selector) }],
        };

        var files = new List<CodeGraphFile> { new("main.css", "css", main) };
        files.AddRange(Enumerable.Range(0, Imports).Select(at => new CodeGraphFile($"i{at}.css", "css", Extract([Rule($"i{at}.css", $".i{at}")]) with { Imports = [Forward("hub.css")] })));
        files.Add(new CodeGraphFile("hub.css", "css", Extract([Rule("hub.css", ".hub")]) with { Imports = [.. Enumerable.Range(0, Shared).Select(at => Forward($"s{at}.css"))] }));
        files.AddRange(Enumerable.Range(0, Shared).Select(at => new CodeGraphFile($"s{at}.css", "css", Extract([Rule($"s{at}.css", $".s{at}")]))));
        files.Add(new CodeGraphFile("elsewhere.css", "css", Extract([Rule("elsewhere.css", ".found")])));

        var watch = Stopwatch.StartNew();
        var linked = Link([.. files]);
        watch.Stop();

        Assert.Equal(
            [new CodeEdge("css:main.css#.m", "css:elsewhere.css#.found", CodeEdgeKinds.Uses, 2, CodeConfidences.Unique)],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Uses));
        // As above: the slowest runner of the matrix links these in six seconds; a walk made for each import is the
        // imports times what they share, far past any bound here.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"linking {Imports} imports of one hub forwarding {Shared} files took {watch.Elapsed}");
    }

    /// <summary>
    /// A scope import a file declares for its directory - a Razor <c>_ViewImports.cshtml</c>'s <c>@using</c> - every
    /// file of the language under that directory imports: a view's unqualified name is found through it among the two
    /// the repository declares, and a file of another language, or outside the subtree, is left with both.
    /// </summary>
    [Fact]
    public void ADirectorysScopeImport_ReachesEveryFileOfItsLanguageUnderIt_AndNoOther()
    {
        var linked = CodeGraphLinker.Link(
        [
            File("Views/_ViewImports.cshtml", [Member("Imported", 0)], imports: [Import(CodeImportForms.Directory, "Shop.Web")]) with { Language = "razor" },
            File("Views/Cart.cshtml", [Member("Cart", 0)], [Ref("Cart", CodeEdgeKinds.References, "Helper")]) with { Language = "razor" },
            File("Views/Deep/Other.cshtml", [Member("Other", 0)], [Ref("Other", CodeEdgeKinds.References, "Helper") with { Line = 2 }]) with { Language = "razor" },
            File("Elsewhere/None.cshtml", [Member("None", 0)], [Ref("None", CodeEdgeKinds.References, "Helper") with { Line = 3 }]) with { Language = "razor" },
            File("Views/Plain.c", [Member("Plain", 0)], [Ref("Plain", CodeEdgeKinds.References, "Helper") with { Line = 4 }]) with { Language = "c" },
            File("shop.t", [Type("Shop.Web.Helper")], scopes: [Open("Shop.Web")]),
            File("other.t", [Type("Other.Helper")], scopes: [Open("Other")]),
        ]);

        Assert.Contains(new CodeEdge("t:Cart", "t:Shop.Web.Helper", CodeEdgeKinds.References, 1, CodeConfidences.Unique), linked.Edges);
        Assert.Contains(new CodeEdge("t:Other", "t:Shop.Web.Helper", CodeEdgeKinds.References, 2, CodeConfidences.Unique), linked.Edges);

        // With no scope to say which Helper, both are the reference's, and neither alone: the file outside the subtree
        // and the one of another language keep the two candidates.
        foreach (var from in new[] { "t:None", "t:Plain" })
        {
            var found = linked.Edges.Where(edge => edge.From == from && edge.Kind == CodeEdgeKinds.References).ToList();

            Assert.Equal(2, found.Count);
            Assert.All(found, edge => Assert.Equal(CodeConfidences.Candidates, edge.Confidence));
        }
    }

    /// <summary>
    /// A chain of sixty names after its first and a call on its end, as the C# reader writes it - each name's use a link to
    /// the use of the name before it - resolves every link exact, though every type holds a value and a method of the same
    /// names: each use to the value of the type the name before it is, the call to the method of the last type. A chain cut
    /// short past MostReceiverNames names was a value of no type past them, each name after found by its name alone among
    /// the sixty-one that declare it.
    /// </summary>
    [Fact]
    public void ASixtyCallChain_ResolvesEveryLink()
    {
        var types = string.Concat(Enumerable.Range(0, 61).Select(at => $"class T{at} {{ public T{at + 1} next; public void Run() {{ }} }}\n"));
        var text = $"namespace app;\n{types}class Use {{ T0 head; void Go() {{ head{string.Concat(Enumerable.Repeat(".next", 60))}.Run(); }} }}\n";
        var linked = Link(new CodeGraphFile("src/Chain.cs", "csharp", new CSharpExtractor().Extract(new CodeFileInput("src/Chain.cs", text))));

        static IEnumerable<(string To, string Kind, string Confidence)> Ordered(IEnumerable<(string To, string Kind, string Confidence)> edges)
            => edges.OrderBy(edge => edge.To, StringComparer.Ordinal).ThenBy(edge => edge.Kind, StringComparer.Ordinal);

        Assert.Equal(
            Ordered(
            [
                .. Enumerable.Range(0, 60).Select(at => ($"csharp:app.T{at}.next", CodeEdgeKinds.Uses, CodeConfidences.Exact)),
                ("csharp:app.T60.Run()", CodeEdgeKinds.Calls, CodeConfidences.Exact),
                ("csharp:app.Use.head", CodeEdgeKinds.Uses, CodeConfidences.Exact),
            ]),
            Ordered(linked.Edges.Where(edge => edge is { From: "csharp:app.Use.Go()", Kind: CodeEdgeKinds.Uses or CodeEdgeKinds.Calls }).Select(edge => (edge.To, edge.Kind, edge.Confidence))));
    }

    /// <summary>
    /// A chain on a variable of a declared type - a local's, a parameter's - is read through that type however many names
    /// it holds, as the C# reader writes it: each name's use the value of the type of the name before it, the call on its
    /// end the method of the last type, each exact, though another type holds a method of the call's name. A chain past
    /// one name read as a value of no type uses no name, and finds the call by its name alone, among both.
    /// </summary>
    [Fact]
    public void AChainOnATypedVariable_IsReadThroughItsType_HoweverManyNamesItHolds()
    {
        const string Text = """
            namespace app;
            class Foo { public Mid A; }
            class Mid { public Low B; }
            class Low { public void Bar() { } }
            class Other { public void Bar() { } }
            class Use
            {
                void Go(Foo given)
                {
                    Foo f = given;
                    f.A.B.Bar();
                    given.A.B.Bar();
                }
            }
            """;
        var linked = Link(new CodeGraphFile("src/Use.cs", "csharp", new CSharpExtractor().Extract(new CodeFileInput("src/Use.cs", Text))));

        static IEnumerable<(string To, string Kind, int Line, string Confidence)> Ordered(IEnumerable<(string To, string Kind, int Line, string Confidence)> edges)
            => edges.OrderBy(edge => edge.Line).ThenBy(edge => edge.To, StringComparer.Ordinal);

        Assert.Equal(
            Ordered(
            [
                .. new[] { 11, 12 }.SelectMany(line => new[]
                {
                    ("csharp:app.Foo.A", CodeEdgeKinds.Uses, line, CodeConfidences.Exact),
                    ("csharp:app.Mid.B", CodeEdgeKinds.Uses, line, CodeConfidences.Exact),
                    ("csharp:app.Low.Bar()", CodeEdgeKinds.Calls, line, CodeConfidences.Exact),
                }),
            ]),
            Ordered(linked.Edges.Where(edge => edge is { From: "csharp:app.Use.Go(Foo)", Kind: CodeEdgeKinds.Uses or CodeEdgeKinds.Calls }).Select(edge => (edge.To, edge.Kind, edge.Line, edge.Confidence))));
    }

    /// <summary>
    /// A chain is stepped a link at a time, each once: the use of each name and the call on its end read the links before
    /// them once, a second statement that writes the same chain from the same declaration steps none of it again, and
    /// another declaration that writes it steps it for itself - three links twice, counted. A chain read again from its
    /// first name for each reference made on it, or kept for each reference rather than for what it says, steps more.
    /// </summary>
    [Fact]
    public void ASharedPrefix_IsResolvedOnce()
    {
        var linked = Link(File("use.t",
            [
                Type("app.H"), Value("app.H.a", "A"), Type("app.A"), Value("app.A.b", "B"), Type("app.B"), Value("app.B.c", "C"),
                Type("app.C"), Member("app.C.Run", 0), Member("app.C.Stop", 0),
                Type("app.Use"), Value("app.Use.head", "H"), Member("app.Use.Go", 0), Member("app.Use.Other", 0),
            ],
            [.. ChainStatement("app.Use.Go", 1, 0, "Run"), .. ChainStatement("app.Use.Go", 2, 4, "Stop"), .. ChainStatement("app.Use.Other", 3, 8, "Run")],
            [Open("app")]));

        Assert.Equal(6, linked.ChainLinks);
        Assert.Equal(
            [
                ("t:app.Use.Go", "t:app.H.a", CodeEdgeKinds.Uses, 1), ("t:app.Use.Go", "t:app.A.b", CodeEdgeKinds.Uses, 1), ("t:app.Use.Go", "t:app.B.c", CodeEdgeKinds.Uses, 1),
                ("t:app.Use.Go", "t:app.C.Run", CodeEdgeKinds.Calls, 1),
                ("t:app.Use.Go", "t:app.H.a", CodeEdgeKinds.Uses, 2), ("t:app.Use.Go", "t:app.A.b", CodeEdgeKinds.Uses, 2), ("t:app.Use.Go", "t:app.B.c", CodeEdgeKinds.Uses, 2),
                ("t:app.Use.Go", "t:app.C.Stop", CodeEdgeKinds.Calls, 2),
                ("t:app.Use.Other", "t:app.H.a", CodeEdgeKinds.Uses, 3), ("t:app.Use.Other", "t:app.A.b", CodeEdgeKinds.Uses, 3), ("t:app.Use.Other", "t:app.B.c", CodeEdgeKinds.Uses, 3),
                ("t:app.Use.Other", "t:app.C.Run", CodeEdgeKinds.Calls, 3),
            ],
            linked.Edges.Where(edge => edge.Kind is CodeEdgeKinds.Uses or CodeEdgeKinds.Calls && edge.Confidence == CodeConfidences.Exact).Select(edge => (edge.From, edge.To, edge.Kind, edge.Line)));
        Assert.Empty(linked.External);
    }

    /// <summary>
    /// A chain of ten thousand names written as links, none a value - a type's whole qualified name, a name before each -
    /// is looked for a link at a time, the chain before each name carried as that one's and never written out: the call on
    /// its end is the type's method, exact, and what the link keeps of the keys it read grows with the names, not with the
    /// square of them, in bounded time. A link whose chain was looked for by its names written out keeps each of the ten
    /// thousand chains' text.
    /// </summary>
    [Fact]
    public void ANamedChainOfTenThousandLinks_IsLookedForALinkAtATime_ItsNamesNeverWrittenOut()
    {
        const int Links = 10_000;
        var names = Enumerable.Range(0, Links + 1).Select(at => $"N{at}").ToList();
        var deepest = string.Join('.', names);
        var references = new List<ExtractReference> { new("t:Go", CodeEdgeKinds.Uses, names[1], 1) { Qualifier = Named(names[0]) } };

        for (var at = 2; at <= Links; at++)
        {
            references.Add(new ExtractReference("t:Go", CodeEdgeKinds.Uses, names[at], 1) { Qualifier = new ExtractQualifier(CodeQualifierKinds.Named) { Link = at - 2 } });
        }

        references.Add(Call("Go", "Run", 0, new ExtractQualifier(CodeQualifierKinds.Named) { Link = Links - 1 }));

        var watch = Stopwatch.StartNew();
        var linked = Link(File("deep.t", [Type(deepest), Member($"{deepest}.Run", 0)]), File("use.t", [Member("Go", 0)], references));
        watch.Stop();

        Assert.Equal([new CodeEdge("t:Go", $"t:{deepest}.Run", CodeEdgeKinds.Calls, 1, CodeConfidences.Exact)], linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Calls));
        Assert.InRange(linked.Memory!.Keys.Characters, deepest.Length, 20L * deepest.Length);
        // As above: the slowest runner of the matrix links a chain of this many names in eight seconds; the chains'
        // names written out is the square of them, far past any bound here.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"linking a chain of {Links} names took {watch.Elapsed}");
    }

    /// <summary>
    /// What a file imported makes visible through the files it exports is walked once for the name it is asked for, and
    /// kept: a thousand calls of an extension two thousand exports away, each from a declaration of its own, walk the two
    /// thousand files once, and each finds the extension. A walk made for each call visits them a thousand times.
    /// </summary>
    [Fact]
    public void WhatAnImportExposes_IsWalkedOnceForAName_HoweverManyCallsAskIt()
    {
        const int Depth = 2_000;
        const int Calls = 1_000;

        var linked = CodeGraphLinker.Link(
        [
            .. Enumerable.Range(0, Depth).Select(at => File($"f{at}.t", [], imports: [Import(CodeImportForms.Relative, $"f{at + 1}.t") with { Export = true }])),
            File($"f{Depth}.t", [Type("lib.Ext"), Member("lib.Ext.Shout", 0) with { Extends = "app.Thing" }], scopes: [Open("lib")]),
            File("app.t",
                [Type("app.Thing"), .. Enumerable.Range(0, Calls).Select(at => Member($"app.Thing.Go{at}", 0) with { Parent = "t:app.Thing" })],
                [.. Enumerable.Range(0, Calls).Select(at => Call($"app.Thing.Go{at}", "Shout", 0, new ExtractQualifier(CodeQualifierKinds.This)))],
                [Open("app")],
                [Import(CodeImportForms.Relative, "f0.t")]),
        ]);

        Assert.Equal(
            Enumerable.Range(0, Calls).Select(at => ($"t:app.Thing.Go{at}", CodeConfidences.Unique)).OrderBy(edge => edge.Item1, StringComparer.Ordinal),
            linked.Edges.Where(edge => edge is { Kind: CodeEdgeKinds.Calls, To: "t:lib.Ext.Shout" }).Select(edge => (edge.From, edge.Confidence)).OrderBy(edge => edge.From, StringComparer.Ordinal));
        Assert.Equal(Depth + 1, linked.ExposedFiles);
    }

    /// <summary>
    /// A link made again finds again each reference made through a chain's link that a change moved - the use of each name
    /// after it and the call on its end, which read it through the links before them - and none before it: the value whose
    /// type changed is the second name's, so the use of the first name keeps what it found. A reference that kept only the
    /// keys it read itself would keep the call on the type it named before.
    /// </summary>
    [Fact]
    public void ALinkMadeAgain_FindsAgainWhatReadAMovedLinkOfAChain()
    {
        static CodeGraphFile Types(string typeOfB) => File("types.t",
            [
                Type("app.A"), Value("app.A.b", typeOfB), Type("app.B"), Value("app.B.c", "C"), Type("app.D"), Value("app.D.c", "E"),
                Type("app.C"), Member("app.C.Run", 0), Type("app.E"), Member("app.E.Run", 0),
            ],
            scopes: [Open("app")]);

        var use = File("use.t",
            [Type("app.H"), Value("app.H.a", "A"), Type("app.Use"), Value("app.Use.head", "H"), Member("app.Use.Go", 0)],
            [.. ChainStatement("app.Use.Go", 1, 0, "Run")],
            [Open("app")]);

        var again = Again([use, Types("B")], [use, Types("D")]);

        Assert.Contains(Edge("t:app.Use.Go", "t:app.D.c", CodeEdgeKinds.Uses, CodeConfidences.Exact), again.Edges);
        Assert.Contains(Edge("t:app.Use.Go", "t:app.E.Run", CodeEdgeKinds.Calls, CodeConfidences.Exact), again.Edges);
        Assert.Equal(3, again.References);
    }

    /// <summary>
    /// A base named through the bases of the type that names it, forty levels up: each type inherits the type nested in
    /// the one the type before it inherits - T2 inherits T1.N, which T1's base T0 declares - so each level's base is known
    /// only once the level before it is, and the forty-first type's base is found in the forty-first round of the bases.
    /// Two passes of the bases found the second level's and none past it.
    /// </summary>
    [Fact]
    public void ABaseFortyLevelsUp_IsResolved()
    {
        static string Nested(int depth) => "app.T0" + string.Concat(Enumerable.Repeat(".N", depth));

        var linked = Link(File("deep.t",
            [.. Enumerable.Range(0, 41).Select(depth => Type(Nested(depth))), .. Enumerable.Range(1, 41).Select(level => Type($"app.T{level}"))],
            [
                Ref("app.T1", CodeEdgeKinds.Inherits, "T0"),
                .. Enumerable.Range(2, 40).Select(level => Ref($"app.T{level}", CodeEdgeKinds.Inherits, "N") with { Line = level, Qualifier = Named($"T{level - 1}") }),
            ],
            [Open("app")]));

        Assert.Equal(
            [.. Enumerable.Range(1, 41).Select(level => ($"t:app.T{level}", $"t:{Nested(level - 1)}", CodeConfidences.Exact)).OrderBy(edge => edge.Item1, StringComparer.Ordinal)],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Inherits).Select(edge => (edge.From, edge.To, edge.Confidence)).OrderBy(edge => edge.From, StringComparer.Ordinal));
        Assert.Empty(linked.External);
    }

    /// <summary>
    /// Rounds of the bases that come round end at the revisit: a nested type's base is found through the bases of the type
    /// it is nested in, and that type's base through the nested type's, so each round undoes what the one before found,
    /// until a round finds the bases a round before it found, which no further round leaves - and the link ends there, as
    /// that round found them. Two types that inherit each other end alike, a call through them to a name neither holds
    /// counted external. A loop of rounds that ended only where a round found what the one before did would not end.
    /// </summary>
    [Fact]
    public void ACyclicHierarchy_EndsAtTheRevisit()
    {
        var linked = Link(File("ring.t",
            [
                Type("app.N"), Type("app.N.K"), Type("app.N.K.N"), Type("app.Outer"), Type("app.Outer.C"),
                Type("app.A"), Member("app.A.Go", 0) with { Parent = "t:app.A" }, Type("app.B"),
            ],
            [
                Ref("app.Outer.C", CodeEdgeKinds.Inherits, "N"),
                Ref("app.Outer", CodeEdgeKinds.Inherits, "K") with { Line = 2, Qualifier = Named("app.Outer.C") },
                Ref("app.A", CodeEdgeKinds.Inherits, "B") with { Line = 3 },
                Ref("app.B", CodeEdgeKinds.Inherits, "A") with { Line = 4 },
                Call("app.A.Go", "Missing", 0, new ExtractQualifier(CodeQualifierKinds.This)) with { Line = 5 },
            ],
            [Open("app")]));

        // The rounds: C : N, then Outer : N.K through it, then C : N.K.N through Outer's, then Outer : none through C's,
        // then C : N again - the first round's bases, where the link ends.
        Assert.Equal(
            [("t:app.A", "t:app.B"), ("t:app.B", "t:app.A"), ("t:app.Outer.C", "t:app.N")],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Inherits).Select(edge => (edge.From, edge.To)).OrderBy(edge => edge.From, StringComparer.Ordinal));
        Assert.Equal(1, linked.External["t:app.Outer"]);
        Assert.Equal(1, linked.External["t:app.A.Go"]);
    }

    /// <summary>
    /// Exports twenty thousand files deep are followed on the walk's own stack, in a child given a small stack: an import
    /// of the first file sees what the last one declares, through its prefix. A walk that recursed once an export would
    /// overflow it, and end the child.
    /// </summary>
    [Fact]
    public async Task ExportsTwentyThousandFilesDeep_AreFollowedOnTheWalksOwnStack()
        => Assert.Equal(new ChildRunAnswer(0, $"t:Run {CodeConfidences.Exact}", ""), await ChildRun.MethodAsync(typeof(CodeGraphLinkerTests), nameof(TwentyThousandExports), TestContext.Current.CancellationToken));

    /// <summary>The child of <see cref="ExportsTwentyThousandFilesDeep_AreFollowedOnTheWalksOwnStack"/>: what the call through the first file's prefix found, and how sure.</summary>
    internal static string TwentyThousandExports() => SmallStack.Run(() =>
    {
        var linked = CodeGraphLinker.Link(
        [
            .. Enumerable.Range(0, 20_000).Select(at => File($"f{at}.t", [], imports: [Import(CodeImportForms.Relative, $"f{at + 1}.t") with { Export = true }])),
            File("f20000.t", [Member("Run", 0)]),
            File("use.t", [Member("Go", 0)], [Call("Go", "Run", 0, Prefix("p"))], imports: [Import(CodeImportForms.Relative, "f0.t") with { Prefix = "p" }]),
        ]);

        return string.Join(' ', linked.Edges.Where(edge => edge is { From: "t:Go", Kind: CodeEdgeKinds.Calls }).Select(edge => $"{edge.To} {edge.Confidence}"));
    });

    /// <summary>
    /// The references a reader writes for <c>head.a.b.c.call()</c> in <paramref name="from"/> on <paramref name="line"/>,
    /// its first at <paramref name="first"/> among its file's: a use of each name after the first - the first qualified by
    /// <c>head</c>, each next a link to the use before it - and the call, a link to the last.
    /// </summary>
    private static IEnumerable<ExtractReference> ChainStatement(string from, int line, int first, string call) =>
    [
        new ExtractReference($"t:{from}", CodeEdgeKinds.Uses, "a", line) { Qualifier = new ExtractQualifier(CodeQualifierKinds.Value, "head") },
        new ExtractReference($"t:{from}", CodeEdgeKinds.Uses, "b", line) { Qualifier = new ExtractQualifier(CodeQualifierKinds.Value) { Link = first } },
        new ExtractReference($"t:{from}", CodeEdgeKinds.Uses, "c", line) { Qualifier = new ExtractQualifier(CodeQualifierKinds.Value) { Link = first + 1 } },
        Call(from, call, 0, new ExtractQualifier(CodeQualifierKinds.Value) { Link = first + 2 }) with { Line = line },
    ];

    private static CodeGraphLinked Link(params CodeGraphFile[] files) => CodeGraphLinker.Link(files);

    private static CodeGraphFile File(string path, IReadOnlyList<ExtractNode> nodes, IReadOnlyList<ExtractReference>? references = null, IReadOnlyList<ExtractScope>? scopes = null, IReadOnlyList<ExtractImport>? imports = null)
        => new(path, "t", Extract(nodes) with { References = references ?? [], Scopes = scopes ?? [], Imports = imports ?? [] });

    private static FileExtract Extract(IReadOnlyList<ExtractNode> nodes) => new(nodes);

    private static ExtractScope Open(string name) => new(name, Open: true);

    /// <summary>A type of <paramref name="qualified"/> name, declared in the scope its name holds before its last part.</summary>
    private static ExtractNode Type(string qualified)
        => new($"t:{qualified}", CodeNodeKinds.Type, Last(qualified), qualified, 1, 1, qualified) { Scope = ScopeOf(qualified) };

    /// <summary>A member of <paramref name="qualified"/> name taking exactly <paramref name="arity"/> arguments, in the scope its name holds before its last part.</summary>
    private static ExtractNode Member(string qualified, int arity)
        => new($"t:{qualified}", CodeNodeKinds.Member, Last(qualified), qualified, 1, 1, qualified) { Scope = ScopeOf(qualified), MinArity = arity, MaxArity = arity };

    /// <summary>A value of <paramref name="qualified"/> name - a member no call takes arguments of - declared as <paramref name="type"/> where one is said.</summary>
    private static ExtractNode Value(string qualified, string? type)
        => new($"t:{qualified}", CodeNodeKinds.Member, Last(qualified), qualified, 1, 1, qualified) { Scope = ScopeOf(qualified), ValueType = type };

    private static ExtractReference Call(string from, string name, int arity, ExtractQualifier? qualifier = null)
        => new($"t:{from}", CodeEdgeKinds.Calls, name, 1) { Arity = arity, Qualifier = qualifier };

    private static ExtractReference Ref(string from, string kind, string name) => new($"t:{from}", kind, name, 1);

    private static ExtractQualifier Named(string name) => new(CodeQualifierKinds.Named, name);

    private static ExtractQualifier Prefix(string name) => new(CodeQualifierKinds.Prefix, name);

    private static ExtractImport Import(string form, string target) => new(form, target, 1);

    private static CodeEdge Edge(string from, string to, string kind, string confidence) => new(from, to, kind, 1, confidence);

    private static string Last(string qualified) => qualified[(qualified.LastIndexOf('.') + 1)..];

    private static QualifiedName? ScopeOf(string qualified) => qualified.LastIndexOf('.') is var dot and > 0 ? QualifiedName.Of(qualified[..dot]) : null;

    /// <summary>Symbols compared by their id, kind and paths.</summary>
    private sealed class SymbolComparer : IEqualityComparer<CodeGraphSymbol>
    {
        public static SymbolComparer Instance { get; } = new();

        public bool Equals(CodeGraphSymbol? x, CodeGraphSymbol? y)
            => x is not null && y is not null && x.Id == y.Id && x.Kind == y.Kind && x.Paths.SequenceEqual(y.Paths);

        public int GetHashCode(CodeGraphSymbol obj) => obj.Id.GetHashCode(StringComparison.Ordinal);
    }
}
