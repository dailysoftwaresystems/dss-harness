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
    /// prefixes and filters, global imports, address roots, partial and nested types and their bases, values and their
    /// types, extensions, every kind of qualifier - and one small change of one file at a time made at random - a value's
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
                Assert.Equal([.. whole.Unresolved], [.. again.Unresolved]);

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

    /// <summary>What the references of the declaration <paramref name="reader"/> found in <paramref name="linked"/>: its edges, and its counts of names found nowhere and of several.</summary>
    private static string Found(CodeGraphLinked linked, string reader)
        => string.Join(
            '\n',
            linked.Edges.Where(edge => edge.From == reader)
                .Select(edge => $"{edge.To} {edge.Kind} {edge.Confidence}")
                .Append($"external {linked.External.GetValueOrDefault(reader)}")
                .Append($"unresolved {linked.Unresolved.GetValueOrDefault(reader)}"));

    /// <summary>Every thing <see cref="Reading"/> changes alone.</summary>
    public static TheoryData<string> ThingsAFindingReads() => [.. Reading().Keys];

    /// <summary>
    /// For each thing a finding reads of another file, the declaration whose reference reads it, a tree, and the same tree
    /// with that one thing of that one file changed - every other file the very same object.
    /// </summary>
    private static Dictionary<string, (string Reader, CodeGraphFile[] Before, CodeGraphFile[] After)> Reading()
    {
        static ExtractQualifier TypeQualifier(string name) => new(CodeQualifierKinds.Type, name);
        static ExtractQualifier Prefix(string name) => new(CodeQualifierKinds.Prefix, name);
        static ExtractNode Node(string id, string kind, string qualified, string? scope) => new(id, kind, Last(qualified), qualified, 1, 1, qualified) { Scope = scope };

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
        Case(
            "the language a file's global imports come by",
            [.. shapes, File("usings.t", [], imports: [geo with { Global = true }])],
            File("ext.t", extension),
            new CodeGraphFile("ext.t", "u", File("ext.t", extension).Extract));

        return cases;
    }

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
        Assert.Equal([.. whole.Unresolved], [.. again.Unresolved]);
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

        var declared = RandomScopes.Where(_ => Chance(random, 2)).Select(scope => new ExtractScope(scope, Open: !Chance(random, 6)) { Parent = Chance(random, 3) ? Pick(random, RandomScopes) : null }).ToList();
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

        var references = new List<ExtractReference>();

        foreach (var type in nodes.Where(node => node.Kind == CodeNodeKinds.Type && Chance(random, 2)))
        {
            references.Add(RandomBase(random, type.Id));
        }

        for (var made = nodes.Count == 0 ? 0 : random.Next(0, 9); made > 0; made--)
        {
            references.Add(RandomReference(random, Pick(random, nodes).Id, made));
        }

        return new CodeGraphFile(path, Chance(random, 12) ? "u" : "t", new FileExtract(nodes)
        {
            References = references,
            Scopes = declared,
            Imports = [.. Enumerable.Range(0, random.Next(0, 3)).Select(_ => RandomImport(random))],
            AddressRoots = Chance(random, 5) ? [RandomRoot(random)] : [],
        });
    }

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
        var dot = scope.Name.LastIndexOf('.');

        if (nodes.Any(node => node.Id == $"n:{scope.Name}"))
        {
            return;
        }

        nodes.Add(new ExtractNode($"n:{scope.Name}", CodeNodeKinds.Namespace, scope.Name[(dot + 1)..], scope.Name, 1, 1, scope.Name) { Scope = dot < 0 ? null : scope.Name[..dot] });
    }

    private static void AddMember(Random random, List<ExtractNode> nodes, string type)
    {
        var member = Pick(random, RandomNames);
        var value = Chance(random, 2);
        var arity = random.Next(0, 2);
        var id = value ? $"t:{type}.{member}" : $"t:{type}.{member}({arity})";

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

    private static ExtractReference RandomReference(Random random, string from, int line)
        => new(from, Pick(random, [CodeEdgeKinds.Calls, CodeEdgeKinds.Calls, CodeEdgeKinds.References]), Pick(random, RandomNames), line)
        {
            Arity = random.Next(0, 2),
            Qualifier = random.Next(9) switch
            {
                0 or 1 => null,
                2 => new ExtractQualifier(CodeQualifierKinds.Named, Dotted(random)),
                3 => new ExtractQualifier(CodeQualifierKinds.Type, Dotted(random)),
                4 or 5 => new ExtractQualifier(CodeQualifierKinds.Value, Dotted(random)),
                6 => new ExtractQualifier(CodeQualifierKinds.This, null),
                7 => new ExtractQualifier(CodeQualifierKinds.Base, null),
                _ => new ExtractQualifier(CodeQualifierKinds.Prefix, "p"),
            },
        };

    private static ExtractImport RandomImport(Random random)
        => random.Next(4) switch
        {
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
            },
            _ => Import(CodeImportForms.Address, Pick(random, ["pkg:x.t", "pkg:in/w.t", "q:m.t", "q:ui/v.t"])),
        };

    private static ExtractAddressRoot RandomRoot(Random random) => new(Pick(random, ["pkg:", "q:"]), Pick(random, ["../lib", "../app", "lib", ".", "../../out"]));

    /// <summary>
    /// <paramref name="tree"/> with one small change of one file made at random: a value's type, what a member extends, a
    /// scope's parent or whether it is closed, an import, an address root, a base, a declaration come or gone, what holds
    /// one, lines moved, a language - or a file come, gone or made anew.
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

        switch (random.Next(20))
        {
            case 0:
                Each(node => node.Kind == CodeNodeKinds.Member && node.MinArity is null && Chance(random, 2), node => node with { ValueType = Chance(random, 4) ? null : Dotted(random) });
                break;

            case 13:
                Each(node => node.Kind == CodeNodeKinds.Member && node.MinArity is not null && Chance(random, 2), node => node with { MinArity = 1 - node.MinArity, MaxArity = 1 - node.MinArity });
                break;

            case 14 when imports.Count > 0:
                var changing = random.Next(imports.Count);
                imports[changing] = random.Next(4) switch
                {
                    0 => imports[changing] with { Prefix = imports[changing].Prefix is null ? Pick(random, ["p", "A"]) : null },
                    1 => imports[changing] with { Show = imports[changing].Show is null ? [Pick(random, RandomNames)] : null },
                    2 => imports[changing] with { Hide = imports[changing].Hide is null ? [Pick(random, RandomNames)] : null },
                    _ => imports[changing] with { Global = !imports[changing].Global },
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
                scopes[parented] = scopes[parented] with { Parent = Chance(random, 3) ? null : Pick(random, RandomScopes) };
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
                roots = roots.Count > 0 && Chance(random, 2) ? [] : [RandomRoot(random)];
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
                    references.Add(RandomBase(random, type.Id));
                }

                break;

            case 7:
                if (nodes.FirstOrDefault(node => node.Kind == CodeNodeKinds.Type && Chance(random, 2)) is { } holder)
                {
                    AddMember(random, nodes, holder.Qualified);
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
                references.Add(RandomReference(random, Pick(random, nodes).Id, references.Count + 1));
                break;

            default:
                Each(node => node.Kind == CodeNodeKinds.Type && Chance(random, 2), node => node with { Scope = Chance(random, 3) ? null : Pick(random, RandomScopes) });
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
        yield return ("app/tools.t", [File("app/tools.t", [Member("Tool", 0), Member("Tool2", 0)])]);
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
