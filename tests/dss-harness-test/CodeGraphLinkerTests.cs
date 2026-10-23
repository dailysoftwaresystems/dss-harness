using DssHarness.Core.Codebase;

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
    /// Last, the repository by name and arity: one is unique; two to five are an edge to each, candidates; more are counted
    /// unresolved; none is counted external.
    /// </summary>
    [Fact]
    public void TheRepository_FindsByNameAndArity_UniqueCandidatesUnresolvedOrExternal()
    {
        var many = Enumerable.Range(1, CodeConfidences.MostCandidates + 1).Select(at => File($"m{at}.t", [Member($"m{at}.Many", 0) with { Scope = $"m{at}" }], scopes: [Open($"m{at}")]));
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
        Assert.DoesNotContain(linked.Edges, edge => edge.Line == 3 && edge.Kind == CodeEdgeKinds.Calls);
        Assert.Equal(1, linked.Unresolved["t:Go"]);
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
            File("a.t", [Member("Go", 0), Member("hidden.Secret", 0) with { Scope = "hidden" }], [Call("Go", "Secret", 0)], [new ExtractScope("hidden", Open: false)]),
            File("b.t", [Member("Other", 0)], [Call("Other", "Secret", 0)], [new ExtractScope("hidden", Open: false)]));

        Assert.Contains(Edge("t:Go", "t:hidden.Secret", CodeEdgeKinds.Calls, CodeConfidences.Unique), linked.Edges);
        Assert.Equal(1, linked.External["t:Other"]);
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
    /// Exactly <see cref="CodeConfidences.MostCandidates"/> declarations of a name are an edge to each; an import by a
    /// path's ending that names that many files is an edge to each, and one that names more is counted unresolved.
    /// </summary>
    [Fact]
    public void TheMostCandidates_AreEachAnEdge_AndAnEndingNamingMoreIsUnresolved()
    {
        var most = CodeConfidences.MostCandidates;
        var five = Enumerable.Range(1, most).Select(at => File($"f{at}/five.h", [Member($"f{at}.Five", 0) with { Scope = $"f{at}" }], scopes: [Open($"f{at}")]));
        var six = Enumerable.Range(1, most + 1).Select(at => File($"s{at}/six.h", []));
        var linked = Link([
            .. five,
            .. six,
            File("use.t", [Member("Go", 0)], [Call("Go", "Five", 0)], imports: [Import(CodeImportForms.Ending, "five.h"), Import(CodeImportForms.Ending, "six.h") with { Line = 2 }]),
        ]);

        Assert.Equal(most, linked.Edges.Count(edge => edge is { From: "t:Go", Kind: CodeEdgeKinds.Calls, Confidence: CodeConfidences.Candidates }));
        Assert.Equal(most, linked.Edges.Count(edge => edge is { From: "file:use.t", Kind: CodeEdgeKinds.Imports, Line: 1, Confidence: CodeConfidences.Candidates }));
        Assert.DoesNotContain(linked.Edges, edge => edge is { From: "file:use.t", Line: 2 });
        Assert.Equal(1, linked.Unresolved["file:use.t"]);
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
    /// Address roots are tried the longest prefix first, and two of one prefix in one order - by directory - whatever order
    /// the files that declare them come in.
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
    /// found up a base of two of a name answers as one of them; and two bases that answer alike each answer as one.
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
                Ref("n.U", CodeEdgeKinds.Implements, "Repo"), Ref("n.U", CodeEdgeKinds.MixesIn, "A"), Ref("n.U", CodeEdgeKinds.MixesIn, "B"),
            ]));

        Assert.Contains(Edge("t:n.Impl", "t:n.Repo", CodeEdgeKinds.Implements, CodeConfidences.Exact), linked.Edges);
        Assert.Contains(Edge("t:m.Mock", "t:n.Impl", CodeEdgeKinds.Inherits, CodeConfidences.Candidates), linked.Edges);
        Assert.Contains(Edge("t:n.Repo.Run", "t:n.Impl.Run", CodeEdgeKinds.Dispatches, CodeConfidences.Exact), linked.Edges);
        Assert.Equal(CodeConfidences.Candidates, Assert.Single(linked.Edges, edge => edge is { Kind: CodeEdgeKinds.Dispatches, To: "t:m.Mock.Run" }).Confidence);

        Assert.Contains(Edge("t:n.T", "t:p.Base", CodeEdgeKinds.Inherits, CodeConfidences.Candidates), linked.Edges);
        Assert.Equal(CodeConfidences.Candidates, Assert.Single(linked.Edges, edge => edge is { Kind: CodeEdgeKinds.Dispatches, To: "t:p.Base.Run" }).Confidence);

        Assert.Contains(Edge("t:n.U", "t:n.A", CodeEdgeKinds.MixesIn, CodeConfidences.Exact), linked.Edges);
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

    private static ExtractReference Call(string from, string name, int arity, ExtractQualifier? qualifier = null)
        => new($"t:{from}", CodeEdgeKinds.Calls, name, 1) { Arity = arity, Qualifier = qualifier };

    private static ExtractReference Ref(string from, string kind, string name) => new($"t:{from}", kind, name, 1);

    private static ExtractQualifier Named(string name) => new(CodeQualifierKinds.Named, name);

    private static ExtractImport Import(string form, string target) => new(form, target, 1);

    private static CodeEdge Edge(string from, string to, string kind, string confidence) => new(from, to, kind, 1, confidence);

    private static string Last(string qualified) => qualified[(qualified.LastIndexOf('.') + 1)..];

    private static string? ScopeOf(string qualified) => qualified.LastIndexOf('.') is var dot and > 0 ? qualified[..dot] : null;

    /// <summary>Symbols compared by their id, kind and paths.</summary>
    private sealed class SymbolComparer : IEqualityComparer<CodeGraphSymbol>
    {
        public static SymbolComparer Instance { get; } = new();

        public bool Equals(CodeGraphSymbol? x, CodeGraphSymbol? y)
            => x is not null && y is not null && x.Id == y.Id && x.Kind == y.Kind && x.Paths.SequenceEqual(y.Paths);

        public int GetHashCode(CodeGraphSymbol obj) => obj.Id.GetHashCode(StringComparison.Ordinal);
    }
}
