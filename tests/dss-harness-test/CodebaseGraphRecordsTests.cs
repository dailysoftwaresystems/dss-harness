using System.Reflection;
using System.Text.Json;
using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// The codebase graph's model and records hold themselves to their shape: each thing an extract, a manifest, a stored
/// extract and a link result can get wrong is refused by name, and the ids, handles and digests are computed one way.
/// </summary>
public sealed class CodebaseGraphRecordsTests
{
    private static readonly string Blob = new('a', 40);

    private static readonly ExtractNode Node = new("toy:A", CodeNodeKinds.Type, "A", "A", 1, 3, "class A") { BodyStart = 2, Summary = "An A.", MinArity = 0, MaxArity = 1 };

    /// <summary>
    /// Each vocabulary's list holds every name the vocabulary declares, each once and nothing else - the edge kinds' list
    /// with their meanings among them - so a name added to one is never refused by the records, or left out of help, for
    /// want of its line in a list.
    /// </summary>
    [Fact]
    public void EachVocabularysList_HoldsEveryNameItDeclares_EachOnce()
    {
        static string[] Declared(Type vocabulary) =>
        [
            .. vocabulary.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(field => field.IsLiteral && field.FieldType == typeof(string))
                .Select(field => (string)field.GetRawConstantValue()!)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(Declared(typeof(CodeEdgeKinds)), CodeEdgeKinds.Described.Select(described => described.Kind).Order(StringComparer.Ordinal));
        Assert.Equal(Declared(typeof(CodeEdgeKinds)), CodeEdgeKinds.All.Order(StringComparer.Ordinal));
        Assert.Equal(Declared(typeof(CodeConfidences)), CodeConfidences.All.Order(StringComparer.Ordinal));
        Assert.Equal(Declared(typeof(CodeFileStates)), CodeFileStates.All.Order(StringComparer.Ordinal));
        Assert.Equal(Declared(typeof(CodeImportForms)), CodeImportForms.All.Order(StringComparer.Ordinal));
        Assert.Equal(Declared(typeof(CodeQualifierKinds)), CodeQualifierKinds.All.Order(StringComparer.Ordinal));
        Assert.Equal(Declared(typeof(CodeNodeKinds)).Where(kind => kind != CodeNodeKinds.File), CodeNodeKinds.Extracted.Order(StringComparer.Ordinal));
    }

    /// <summary>A sound extract has no problem; each way an extract can be wrong is refused, saying what is.</summary>
    [Fact]
    public void AnExtract_IsRefusedForEachThingItGetsWrong()
    {
        var sound = new FileExtract([Node, Node with { Id = "toy:A.b", Kind = CodeNodeKinds.Member, Name = "b", Qualified = "A.b", Start = 2, End = 3, Parent = "toy:A" }])
        {
            Scopes = [new ExtractScope("app", Open: true)],
            Imports = [new ExtractImport(CodeImportForms.Scope, "lib", 1) { Prefix = "l" }],
            AddressRoots = [new ExtractAddressRoot("package:x/", "lib")],
            References = [new ExtractReference("toy:A", CodeEdgeKinds.Calls, "Go", 2) { Arity = 1, Qualifier = new ExtractQualifier(CodeQualifierKinds.Named, "B") }],
            Generated = "a generator wrote it",
            Dynamic = [new ExtractDynamic(3, "a call through a pointer", "pointer-call")],
        };

        Assert.Null(sound.Problem("toy", 3));
        Assert.Null((sound with { Nodes = [Node with { Summary = "An \U0001F600 A." }] }).Problem("toy", 3));
        Assert.Null((sound with { Nodes = [Node with { Exports = ["default"] }], AddressRoots = [new ExtractAddressRoot("pkg", "src/index.ts") { Whole = true }] }).Problem("toy", 3));
        Assert.Null((sound with { Literals = LiteralRow.Table(new LiteralRow("held by a node", 2, "toy:A.b"), new LiteralRow("held by the file", 3)) }).Problem("toy", 3));
        Assert.Null((sound with { Nodes = [Node with { Selectors = ["app-cart", "x-1", "|money", "|$x_2"] }] }).Problem("toy", 3));

        // A chain of a type is written as links as a chain of a value or of a name is: the type the names its links write.
        Assert.Null((sound with { References = [.. sound.References, new ExtractReference("toy:A", CodeEdgeKinds.Calls, "Run", 2) { Qualifier = new ExtractQualifier(CodeQualifierKinds.Type) { Link = 0 } }] }).Problem("toy", 3));

        var wrong = new (FileExtract Extract, string Problem)[]
        {
            (sound with { Nodes = [Node, Node] }, "two of its nodes are 'toy:A'"),
            (sound with { Nodes = [Node with { Id = "" }] }, "it has no id"),
            (sound with { Nodes = [Node with { Id = "other:A" }] }, "its id does not begin 'toy:'"),
            (sound with { Nodes = [Node with { Kind = CodeNodeKinds.File }] }, "its kind is 'file'"),
            (sound with { Nodes = [Node with { Name = " " }] }, "it has no name"),
            (sound with { Nodes = [Node with { Name = "Sure?\nIt cannot be undone." }] }, "its name is not one line"),
            (sound with { Nodes = [Node with { Qualified = "messages.Sure?\rIt cannot be undone." }] }, "its name is not one line"),
            (sound with { Nodes = [Node with { End = 4 }] }, "its lines 1-4 are not lines of the file's 3"),
            (sound with { Nodes = [Node with { Start = 0 }] }, "its lines 0-3 are not lines"),
            (sound with { Nodes = [Node with { Start = 3, End = 2 }] }, "its lines 3-2 are not lines"),
            (sound with { Nodes = [Node with { BodyStart = 4 }] }, "its body begins on line 4"),
            (sound with { Nodes = [Node with { Signature = "class\nA" }] }, "its signature is not one line"),
            (sound with { Nodes = [Node with { Summary = "one\rtwo" }] }, "its summary is not one line"),
            (sound with { Nodes = [Node with { MinArity = -1 }] }, "its arity is no range"),
            (sound with { Nodes = [Node with { MinArity = 2, MaxArity = 1 }] }, "its arity is no range"),
            (sound with { Nodes = [Node with { MinArity = null, MaxArity = 1 }] }, "its arity is no range"),
            (sound with { Nodes = [Node with { Digest = "ABC" }] }, "its digest is no SHA-256"),
            (sound with { Nodes = [Node with { ValueType = " " }] }, "its value's type is empty"),
            (sound with { Nodes = [Node with { Extends = "" }] }, "the type it extends is empty"),
            (sound with { Nodes = [Node with { SeenIn = " " }] }, "the scope it is seen in is empty"),
            (sound with { Nodes = [Node with { SeenIn = "lib\uD83D" }] }, "a surrogate cut from its pair"),
            (sound with { Nodes = [Node with { Exports = [] }] }, "a name it is exported by is empty, or it lists none"),
            (sound with { Nodes = [Node with { Exports = ["default", " "] }] }, "a name it is exported by is empty, or it lists none"),
            (sound with { Nodes = [Node with { Exports = ["x\uD83D"] }] }, "a surrogate cut from its pair"),
            (sound with { Nodes = [Node with { Selectors = ["cart"] }] }, "a selector it declares names no element with a dash and no pipe after a bar, or it declares none"),
            (sound with { Nodes = [Node with { Selectors = ["app-cart", ".cart"] }] }, "a selector it declares names no element with a dash and no pipe after a bar, or it declares none"),
            (sound with { Nodes = [Node with { Selectors = ["|"] }] }, "a selector it declares names no element with a dash and no pipe after a bar, or it declares none"),
            (sound with { Nodes = [Node with { Selectors = ["|1x"] }] }, "a selector it declares names no element with a dash and no pipe after a bar, or it declares none"),
            (sound with { Nodes = [Node with { Selectors = [] }] }, "a selector it declares names no element with a dash and no pipe after a bar, or it declares none"),
            (sound with { Nodes = [Node with { ValueType = "x\uD83D" }] }, "a surrogate cut from its pair"),
            (sound with { Nodes = [Node, Node with { Id = "toy:B", Parent = "toy:Z" }] }, "is declared inside 'toy:Z', which it holds no node of"),
            (sound with { Nodes = [Node with { End = 2 }, Node with { Id = "toy:B", Start = 2, End = 3, Parent = "toy:A" }] }, "whose lines do not hold it"),
            (sound with { Nodes = [Node with { Parent = "toy:A" }] }, "its node 'toy:A' is declared inside itself, through 'toy:A'"),
            (sound with { Nodes = [Node with { Parent = "toy:B" }, Node with { Id = "toy:B", Parent = "toy:A" }] }, "is declared inside itself, through"),
            (sound with { Nodes = [Node with { Summary = "cut \uD83D" }] }, "it holds text that is no whole UTF-16, a surrogate cut from its pair: 'cut \uD83D'"),
            (sound with { Nodes = [Node with { Name = "\uDE00x" }] }, "a surrogate cut from its pair"),
            (sound with { References = [new ExtractReference("toy:A", CodeEdgeKinds.Calls, "Go\uD83D", 1)] }, "a surrogate cut from its pair"),
            (sound with { Scopes = [new ExtractScope(" ", Open: true)] }, "it holds a scope with no name"),
            (sound with { Scopes = [new ExtractScope("app", true), new ExtractScope("app", false)] }, "it declares the scope 'app' twice"),
            (sound with { Imports = [new ExtractImport("wildcard", "lib", 1)] }, "its form is 'wildcard'"),
            (sound with { Imports = [new ExtractImport(CodeImportForms.Relative, " ", 1)] }, "it names nothing"),
            (sound with { Imports = [new ExtractImport(CodeImportForms.Scope, "lib", 4)] }, "it is on line 4, outside the file's 3"),
            (sound with { Imports = [new ExtractImport(CodeImportForms.Scope, "lib", 1) { Prefix = "" }] }, "its prefix is empty"),
            (sound with { Imports = [new ExtractImport(CodeImportForms.Ending, "x.h", 1) { Fallback = true }] }, "its import of 'x.h' stands in for the import before it, and no import of a file is before it"),
            (sound with { Imports = [new ExtractImport(CodeImportForms.Scope, "lib", 1), new ExtractImport(CodeImportForms.Ending, "x.h", 1) { Fallback = true }] }, "no import of a file is before it"),
            (sound with { Imports = [new ExtractImport(CodeImportForms.Relative, "x.h", 1), new ExtractImport(CodeImportForms.Scope, "lib", 1) { Fallback = true }] }, "it is of a scope, and stands in for another import: only an import of a file does"),
            (sound with { Imports = [new ExtractImport(CodeImportForms.Scope, "lib", 1) { Export = true }] }, "it is of a scope, and is an export: only an import of a file is exported"),
            (sound with { Imports = [new ExtractImport(CodeImportForms.Relative, "x.dart", 1) { Export = true, Prefix = "p" }] }, "it is an export with a prefix"),
            (sound with { Imports = [new ExtractImport(CodeImportForms.Scope, "lib", 1) { Shares = true }] }, "it is of a scope, and shares its imports: only a file's are shared"),
            (sound with { Imports = [new ExtractImport(CodeImportForms.Relative, "x.dart", 1) { Shares = true, Export = true }] }, "it shares its imports, and is an export or has a prefix or a filter"),
            (sound with { Imports = [new ExtractImport(CodeImportForms.Relative, "x.dart", 1) { Shares = true, Prefix = "p" }] }, "it shares its imports, and is an export or has a prefix or a filter"),
            (sound with { Imports = [new ExtractImport(CodeImportForms.Relative, "x.dart", 1) { Shares = true, Show = ["A"] }] }, "it shares its imports, and is an export or has a prefix or a filter"),
            (sound with { Imports = [new ExtractImport(CodeImportForms.Relative, "x.dart", 1) { Shares = true, Hide = ["A"] }] }, "it shares its imports, and is an export or has a prefix or a filter"),
            (sound with { AddressRoots = [new ExtractAddressRoot("package:x/", "")] }, "an address root with no prefix or no directory"),
            (sound with { References = [new ExtractReference("toy:Z", CodeEdgeKinds.Calls, "Go", 1)] }, "is made from 'toy:Z', which it holds no node of"),
            (sound with { References = [new ExtractReference("toy:A", CodeEdgeKinds.Dispatches, "Go", 1)] }, "its kind is 'dispatches'"),
            (sound with { References = [new ExtractReference("toy:A", CodeEdgeKinds.Calls, "", 1)] }, "it names nothing"),
            (sound with { References = [new ExtractReference("toy:A", CodeEdgeKinds.Calls, "Go", 0)] }, "it is on line 0"),
            (sound with { References = [new ExtractReference("toy:A", CodeEdgeKinds.Calls, "Go", 1) { Arity = -1 }] }, "its arity is below zero"),
            (sound with { References = [new ExtractReference("toy:A", CodeEdgeKinds.Calls, "Go", 1) { Qualifier = new ExtractQualifier("super") }] }, "its qualifier's kind is 'super'"),
            (sound with { References = [new ExtractReference("toy:A", CodeEdgeKinds.Calls, "Go", 1) { Qualifier = new ExtractQualifier(CodeQualifierKinds.Prefix) }] }, "its qualifier names nothing"),
            (sound with { References = [new ExtractReference("toy:A", CodeEdgeKinds.Calls, "Go", 1) { Qualifier = new ExtractQualifier(CodeQualifierKinds.Named, " ") }] }, "its qualifier names nothing"),
            (sound with { References = [new ExtractReference("toy:A", CodeEdgeKinds.Calls, "Go", 1) { Qualifier = new ExtractQualifier(CodeQualifierKinds.Type) }] }, "its qualifier names nothing"),
            (sound with { References = [new ExtractReference("toy:A", CodeEdgeKinds.Calls, "Go", 1) { Qualifier = new ExtractQualifier(CodeQualifierKinds.Value, "") }] }, "its qualifier's name is empty"),
            (sound with { References = [new ExtractReference("toy:A", CodeEdgeKinds.Calls, "Go", 1) { Qualifier = new ExtractQualifier(CodeQualifierKinds.Scope, " ") }] }, "its qualifier's name is empty"),
            (sound with { References = [new ExtractReference("toy:A", CodeEdgeKinds.Calls, "Go", 1) { Qualifier = new ExtractQualifier(CodeQualifierKinds.This, " ") }] }, "its qualifier's name is empty"),
            (sound with { References = [.. sound.References, new ExtractReference("toy:A", CodeEdgeKinds.Calls, "Run", 2) { Qualifier = new ExtractQualifier(CodeQualifierKinds.This) { Link = 0 } }] }, "a reference to 'Run' links its qualifier of kind 'this', and only a chain of a value, of a name or of a type is written as links"),
            (sound with { References = [.. sound.References, new ExtractReference("toy:A", CodeEdgeKinds.Calls, "Run", 2) { Qualifier = new ExtractQualifier(CodeQualifierKinds.Type, "B") { Link = 0 } }] }, "a reference to 'Run' links its qualifier and names 'B' too: a linked chain's names are its links'"),
            (sound with { Generated = " " }, "it says it is generated and not why"),
            (sound with { Dynamic = [new ExtractDynamic(1, "", "macro")] }, "it marks a line dynamic and not why"),
            (sound with { Dynamic = [new ExtractDynamic(9, "macro", "macro")] }, "it marks line 9 dynamic, outside its 3 lines"),
            (sound with { Literals = LiteralRow.Table(new LiteralRow("value", 2, "toy:Z")) }, "its literal 'value' is held by 'toy:Z', which it holds no node of"),
            (sound with { Nodes = [Node with { Selectors = ["app-cart", " "] }] }, "a selector it declares names no element with a dash and no pipe after a bar, or it declares none"),
            (sound with { Nodes = [Node with { Kind = CodeNodeKinds.Namespace, TestByType = true }] }, "it is a test by its type, and a node of kind 'namespace' is neither a type nor a member a type holds"),
            (sound with { Nodes = [Node with { Id = "toy:b", Kind = CodeNodeKinds.Member, Name = "b", Qualified = "b", TestByType = true }] }, "its node 'toy:b': it is a member that is a test by its type, and no type holds it"),
            (sound with { Nodes = [Node with { Kind = CodeNodeKinds.Namespace }, Node with { Id = "toy:A.b", Kind = CodeNodeKinds.Member, Name = "b", Qualified = "A.b", Start = 2, End = 3, Parent = "toy:A", TestByType = true }] }, "its node 'toy:A.b' is a test by its type, and 'toy:A', which holds it, is no type"),
            (sound with { Nodes = [Node with { Kind = CodeNodeKinds.Namespace, TestBase = true }] }, "it is a type tests are built on, and a node of kind 'namespace' is no type"),
            (sound with { Nodes = [Node, Node with { Id = "toy:A.b", Kind = CodeNodeKinds.Member, Name = "b", Qualified = "A.b", Start = 2, End = 3, Parent = "toy:A", TestBase = true }] }, "it is a type tests are built on, and a node of kind 'member' is no type"),
            (sound with { Nodes = [Node with { Joined = true }] }, "it is the own member of the type it extends, and it extends none"),
            (sound with { Nodes = [Node with { Parametrizes = [] }] }, "a name it parametrizes tests by is empty, or it names none"),
            (sound with { Nodes = [Node with { Parametrizes = ["user", " "] }] }, "a name it parametrizes tests by is empty, or it names none"),
            (sound with { Nodes = [Node with { Kind = CodeNodeKinds.Namespace, Parametrizes = ["user"] }] }, "it parametrizes the tests of the types built on it, and a node of kind 'namespace' is no type"),
            (sound with { Nodes = [Node with { Kind = CodeNodeKinds.Namespace, MembersByReceiver = true }] }, "its members are reached by a receiver alone, and a node of kind 'namespace' is no type, nor a member that extends one"),
            (sound with { Nodes = [Node, Node with { Id = "toy:A.b", Kind = CodeNodeKinds.Member, Name = "b", Qualified = "A.b", Start = 2, End = 3, Parent = "toy:A", MembersByReceiver = true }] }, "its members are reached by a receiver alone, and a node of kind 'member' is no type, nor a member that extends one"),
            (sound with { Nodes = [Node with { Templates = [] }] }, "a template it names is empty, or it names none"),
            (sound with { Nodes = [Node with { Templates = [""] }] }, "a template it names is empty, or it names none"),
            (sound with { Imports = [new ExtractImport(CodeImportForms.Relative, "m.py", 1) { Published = true, Prefix = "m" }] }, "it takes the names a file publishes, and so names no prefix, filters none and stands for nothing else"),
            (sound with { Imports = [new ExtractImport(CodeImportForms.Relative, "m.py", 1) { Published = true, Show = ["A"] }] }, "it takes the names a file publishes, and so names no prefix, filters none and stands for nothing else"),
            (sound with { Imports = [new ExtractImport(CodeImportForms.Relative, "m.py", 1) { Published = true, Export = true }] }, "it takes the names a file publishes, and so names no prefix, filters none and stands for nothing else"),
            (sound with { Imports = [new ExtractImport(CodeImportForms.Scope, "lib", 1) { Published = true }] }, "it is of a scope, and takes the names a file publishes: only an import of a file does"),
            (sound with { Imports = [new ExtractImport(CodeImportForms.Scope, "lib", 1) { Layout = true }] }, "it names a place of a layout, which only a path relative to the file does"),
            (sound with { Imports = [new ExtractImport(CodeImportForms.Rooted, "m.py", 1) { Layout = true }] }, "it names a place of a layout, which only a path relative to the file does"),
            (sound with { Imports = [new ExtractImport(CodeImportForms.Relative, "m.py", 1) { Layout = true, Export = true }] }, "it names a place a name is looked for, which is no export: an export names a file"),
            (sound with { Imports = [new ExtractImport(CodeImportForms.Relative, "m.py", 1) { Binds = true, Show = ["A"] }] }, "its prefix stands for the one name it shows, and so it has a prefix and shows one name, hiding none"),
            (sound with { Imports = [new ExtractImport(CodeImportForms.Relative, "m.py", 1) { Binds = true, Prefix = "A", Show = ["A", "B"] }] }, "its prefix stands for the one name it shows, and so it has a prefix and shows one name, hiding none"),
            (sound with { Imports = [new ExtractImport(CodeImportForms.Relative, "m.py", 1) { Binds = true, Prefix = "A", Show = ["A"], Hide = ["B"] }] }, "its prefix stands for the one name it shows, and so it has a prefix and shows one name, hiding none"),
            (sound with { Imports = [new ExtractImport(CodeImportForms.Scope, "lib", 1) { Binds = true, Prefix = "A", Show = ["A"] }] }, "its prefix stands for a name a file shows, which only an import of a file that is no export does"),
            (sound with { Imports = [new ExtractImport(CodeImportForms.Scope, "lib", 1) { Either = true }] }, "it is one of two places a name may stand at, which only an import of a file that is no export is"),
            (sound with { Imports = [new ExtractImport(CodeImportForms.Relative, "m.py", 1) { Either = true, Export = true }] }, "it is one of two places a name may stand at, which only an import of a file that is no export is"),
            (sound with { Imports = [new ExtractImport(CodeImportForms.Directory, "tests", 1) { Serves = "fixtures" }] }, "it serves the references of the qualifier 'fixtures', which is none of"),
            (sound with { Imports = [new ExtractImport(CodeImportForms.Relative, "m.py", 1) { Serves = CodeQualifierKinds.Fixture }] }, "it serves one qualifier's references, which only an import of a directory's every file, made for that directory's files alone, does"),
            (sound with { Imports = [new ExtractImport(CodeImportForms.Relative, "m.py", 1) { Serves = CodeQualifierKinds.Fixture, Export = true }] }, "it serves one qualifier's references, which only an import of a directory's every file, made for that directory's files alone, does"),
            (sound with { Imports = [new ExtractImport(CodeImportForms.Directory, "tests", 1) { Serves = CodeQualifierKinds.Fixture, Global = true }] }, "it serves one qualifier's references, which only an import of a directory's every file, made for that directory's files alone, does"),
            (sound with { Published = ["A", " "] }, "a name it publishes is empty"),
            (sound with { Published = ["A\uD83D"] }, "a surrogate cut from its pair"),
            (sound with { PublishesTaken = true }, "it publishes what its imports take, and publishes nothing of its own"),
            (sound with { LayoutRoots = [] }, "a root of its layout it names is empty, or it names none"),
            (sound with { LayoutRoots = ["src", ""] }, "a root of its layout it names is empty, or it names none"),
            (sound with { LayoutRoots = ["src\uD83D"] }, "a surrogate cut from its pair"),
            (sound with { Dynamic = [new ExtractDynamic(1, "a computed __all__", null!)] }, "it marks line 1 dynamic and names no kind of what it saw"),
            (sound with { Dynamic = [new ExtractDynamic(1, "a computed __all__", " ")] }, "it marks line 1 dynamic with a kind that is empty"),
            (sound with { Dynamic = [new ExtractDynamic(1, "a computed __all__", "computed\uD83D")] }, "a surrogate cut from its pair"),
        };

        Assert.All(wrong, pair => Assert.Contains(pair.Problem, pair.Extract.Problem("toy", 3), StringComparison.Ordinal));
    }

    /// <summary>
    /// Every field an extract may set is written by the store and read back as it was: a node's element selectors, its templates, the
    /// type it extends, whether its members are reached by a receiver alone, whether it is a test by its type and whether tests are built on it,
    /// the names it parametrizes the tests of the types built on it by, whether it is the own member of the type it extends; an import's prefix
    /// standing for the name it shows, whether its names are its importers' too, whether it takes a file's published names, whether it
    /// names a place of a layout, whether it is one of two places, whether it is under a root of the layout and the qualifier it
    /// serves; a reference that is a probe, and one made around its node; the names a file publishes, whether what its imports take
    /// is among them, whether its names are bound within it, whether it keeps its names' kinds apart, whether it writes its type names whole,
    /// whether its directory is a unit of its layout and the roots it declares; and a dynamic mark's kind. Written again, the extract read back
    /// is the very text it was read from.
    /// </summary>
    [Fact]
    public void EveryFieldOfAnExtract_IsWrittenByTheStore_AndReadBackAsItWas()
    {
        var extract = new FileExtract(
        [
            Node with { Selectors = ["app-cart", "cart-view"], Templates = ["cart.html"], MembersByReceiver = true, TestByType = true, TestBase = true, Parametrizes = ["db", "user"] },
            Node with { Id = "toy:A.b", Kind = CodeNodeKinds.Member, Name = "b", Qualified = "A.b", Start = 2, End = 3, Parent = "toy:A", Extends = "Shape", Joined = true, MembersByReceiver = true },
        ])
        {
            Imports =
            [
                new ExtractImport(CodeImportForms.Relative, "lines.py", 1) { Prefix = "Line", Show = ["Line"], Binds = true, Reexports = true, Layout = true, Either = true },
                new ExtractImport(CodeImportForms.Relative, "stars.py", 1) { Published = true, Reexports = true },
                new ExtractImport(CodeImportForms.Rooted, "pkg/cart.py", 2) { Fallback = true },
                new ExtractImport(CodeImportForms.Directory, "tests", 1) { Serves = CodeQualifierKinds.Fixture },
            ],
            References =
            [
                new ExtractReference("toy:A.b", CodeEdgeKinds.Overrides, "b", 2) { Qualifier = new ExtractQualifier(CodeQualifierKinds.Base), Probe = true },
                new ExtractReference("toy:A.b", CodeEdgeKinds.References, "Shape", 2) { Around = true },
            ],
            Published = ["A", "b"],
            PublishesTaken = true,
            BoundWithin = true,
            KindsApart = true,
            WrittenWhole = true,
            FolderUnit = true,
            LayoutRoots = ["src", "."],
            Dynamic = [new ExtractDynamic(3, "a module's __all__ the code computes", "computed-exports")],
        };

        Assert.Null(extract.Problem("toy", 3));

        var text = Json(extract);
        var back = JsonSerializer.Deserialize<FileExtract>(text, JsonStateFile.LineOptions)!;

        Assert.Equal(text, Json(back));
        Assert.Equal(["app-cart", "cart-view"], back.Nodes[0].Selectors!);
        Assert.Equal(["cart.html"], back.Nodes[0].Templates!);
        Assert.Equal(("Shape", true), (back.Nodes[1].Extends, back.Nodes[1].Joined));
        Assert.Equal([(true, true, true), (true, false, false)], back.Nodes.Select(node => (node.MembersByReceiver, node.TestByType, node.TestBase)));
        Assert.Equal(["db", "user"], back.Nodes[0].Parametrizes!);
        Assert.Null(back.Nodes[1].Parametrizes);
        Assert.Equal(
            [
                (CodeImportForms.Relative, "Line", "Line", true, true, false, true, true, false, (string?)null),
                (CodeImportForms.Relative, null, null, false, true, true, false, false, false, null),
                (CodeImportForms.Rooted, null, null, false, false, false, false, false, true, null),
                (CodeImportForms.Directory, null, null, false, false, false, false, false, false, CodeQualifierKinds.Fixture),
            ],
            back.Imports.Select(import => (import.Form, import.Prefix, import.Show is { } show ? string.Join(',', show) : null, import.Binds, import.Reexports, import.Published, import.Layout, import.Either, import.Fallback, import.Serves)));
        Assert.Equal([(true, false), (false, true)], back.References.Select(reference => (reference.Probe, reference.Around)));
        Assert.Equal(["A", "b"], back.Published!);
        Assert.Equal((true, true, true), (back.PublishesTaken, back.BoundWithin, back.FolderUnit));
        Assert.Equal((true, true), (back.KindsApart, back.WrittenWhole));
        Assert.Equal(["src", "."], back.LayoutRoots!);
        Assert.Equal("computed-exports", Assert.Single(back.Dynamic).Kind);

        // A field left at its default is not written, so an extract that sets none of them is written as it was before they were.
        var plain = Json(new FileExtract([Node]) { References = [new ExtractReference("toy:A", CodeEdgeKinds.Calls, "Go", 2)] });

        Assert.DoesNotContain("membersByReceiver", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("testByType", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("testBase", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("around", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("publishesTaken", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("layoutRoots", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("folderUnit", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("boundWithin", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("kindsApart", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("writtenWhole", plain, StringComparison.Ordinal);
    }

    /// <summary>
    /// An extract whose nodes nest a hundred thousand deep is checked in time to its nodes: each node's holders are walked
    /// up once, a node found to end at the root never walked up again.
    /// </summary>
    [Fact]
    public void AnExtractNestedAHundredThousandDeep_IsCheckedInTimeToItsNodes()
    {
        const int Depth = 100_000;
        var nodes = Enumerable.Range(0, Depth).Select(at => new ExtractNode($"toy:N{at}", CodeNodeKinds.Section, $"N{at}", $"N{at}", 1, 1, $"N{at}") { Parent = at == 0 ? null : QualifiedName.Of($"toy:N{at - 1}") }).ToList();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.Null(new FileExtract(nodes).Problem("toy", 1));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"the check took {watch.Elapsed}");

        // One that comes round to itself through its holders is still refused.
        nodes[0] = nodes[0] with { Parent = $"toy:N{Depth - 1}" };

        Assert.Equal("its node 'toy:N0' is declared inside itself, through 'toy:N0'", new FileExtract(nodes).Problem("toy", 1));
    }

    /// <summary>
    /// A path a project file writes relative to its own directory is made one clean path - its <c>.</c> and <c>..</c> out,
    /// a <c>..</c> kept where it climbs above that directory - and a line a reader names is the file's nearest one.
    /// </summary>
    [Fact]
    public void APathRelativeToADirectory_IsMadeClean_AndALineIsTheFilesNearest()
    {
        Assert.Equal(
            ["src", "src/x", "../shared", "../../a", ".", ".", "a/c"],
            new[] { "./src/", "src\\.\\x", "../shared", "a/../../../a", "./", "a/..", "a/b/../c" }.Select(CodeGraphIds.Clean));
        Assert.Equal([1, 1, 3, 3, 1], new[] { (0, 3), (1, 3), (3, 3), (9, 3), (5, 0) }.Select(pair => FileExtract.LineWithin(pair.Item1, pair.Item2)));
    }

    /// <summary>The digests the core sets are each node's lines', and a node whose lines are not the file's is left without one.</summary>
    [Fact]
    public void TheCoresDigests_AreEachNodesLines_AndNoneWhereItsLinesAreNotTheFiles()
    {
        const string Text = "class A\r\n  go\r\nend\r\n";
        var lines = CodeFileText.Lines(Text);
        var digested = new FileExtract([Node, Node with { Id = "toy:B", End = 9 }]).WithDigests(Text);

        Assert.Equal(CodeGraphIds.Sha256("class A\n  go\nend"), digested.Nodes[0].Digest);

        Assert.Equal(CodeGraphIds.Digest(CodeFileText.Lines("class A\n  go\nend"), 1, 3), digested.Nodes[0].Digest);
        Assert.Equal(CodeGraphIds.Digest(lines, 2, 3), CodeGraphIds.Digest(CodeText.Of(Text), 2, 3));
        Assert.Null(digested.Nodes[1].Digest);
        Assert.Throws<ArgumentOutOfRangeException>(() => CodeGraphIds.Digest(lines, 2, 4));

        // A digest the implementation set is never kept: the core's replaces it, and none is left where the lines are not the file's.
        var set = new FileExtract([Node with { Digest = new string('0', 64) }, Node with { Id = "toy:B", End = 9, Digest = new string('0', 64) }]).WithDigests(Text);

        Assert.Equal(digested.Nodes[0].Digest, set.Nodes[0].Digest);
        Assert.Null(set.Nodes[1].Digest);
    }

    /// <summary>A sync line is refused for each thing it gets wrong, and the store appends none it would not read back.</summary>
    [Fact]
    public void ASyncLine_IsRefusedForEachThingItGetsWrong_AndTheStoreAppendsNone()
    {
        var line = new CodeGraphSyncLine(DateTimeOffset.UnixEpoch, "base", "map#1", 3, 3, 2, 1, 0, 0.5) { Rebuilt = "no graph was read", SeededFrom = "earlier" };

        Assert.Null(line.Problem());
        Assert.Equal("it names no view", (line with { View = " " }).Problem());
        Assert.Equal("it says nothing of why it synced", (line with { Why = "" }).Problem());
        Assert.Equal("it counts files below zero", (line with { Listed = -1 }).Problem());
        Assert.Equal("it counts files below zero", (line with { Hashed = -1 }).Problem());
        Assert.Equal("it counts files below zero", (line with { Extracted = -1 }).Problem());
        Assert.Equal("it counts files below zero", (line with { Reused = -1 }).Problem());
        Assert.Equal("it counts files below zero", (line with { Removed = -1 }).Problem());
        Assert.Equal("it counts files below zero", (line with { Unreadable = -1 }).Problem());
        Assert.Equal("its time is no time a clock gives", (line with { Seconds = double.NaN }).Problem());
        Assert.Equal("its time is no time a clock gives", (line with { Seconds = -0.1 }).Problem());
        Assert.Equal("its time is no time a clock gives", (line with { Seconds = double.PositiveInfinity }).Problem());
        Assert.Equal("it says the graph was built again and not why", (line with { Rebuilt = " " }).Problem());
        Assert.Equal("it says it was seeded and not from which run", (line with { SeededFrom = "" }).Problem());

        using var directory = new TempDirectory();
        var (store, layout) = Store(directory);

        var refused = Assert.Throws<InvalidOperationException>(() => store.AppendGraphSync(layout, line with { Seconds = double.NaN }));
        Assert.Contains("its time is no time a clock gives", refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(layout.GraphSyncFile));

        store.AppendGraphSync(layout, line);
        Assert.Single(File.ReadAllLines(layout.GraphSyncFile));
    }

    /// <summary>
    /// Every record of the graph reads back from the strict store as it was written - an extract with every field, a
    /// manifest whose entry names no blob, a link result - and one the store would not read back is never written.
    /// </summary>
    [Fact]
    public void EachRecord_ReadsBackFromTheStore_AsItWasWritten()
    {
        using var directory = new TempDirectory();
        var (store, layout) = Store(directory);
        const string Text = "class A\n  go\nend\n";
        var lines = CodeFileText.Lines(Text);
        var extract = new FileExtract([Node with { SubKind = "class", Scope = "app", Modifiers = ["public", "static"], Prototype = true, Test = true, Summary = "An \U0001F600 A." }, Node with { Id = "toy:A.b", Kind = CodeNodeKinds.Member, Name = "b", Qualified = "A.b", Start = 2, End = 3, Parent = "toy:A", BodyStart = null, MinArity = null, MaxArity = null, ValueType = "Repo", Extends = "A" }])
        {
            Scopes = [new ExtractScope("app", Open: true) { Parent = "root" }, new ExtractScope("root", Open: false)],
            Imports = [new ExtractImport(CodeImportForms.Scope, "lib", 1) { Prefix = "l", Show = ["X"], Hide = ["Y"], Global = true }],
            AddressRoots = [new ExtractAddressRoot("package:x/", "lib")],
            References =
            [
                new ExtractReference("toy:A", CodeEdgeKinds.Calls, "Go", 2) { Arity = 1, Qualifier = new ExtractQualifier(CodeQualifierKinds.Named, "B") },
                new ExtractReference("toy:A", CodeEdgeKinds.Calls, "Save", 3) { Arity = 0, Qualifier = new ExtractQualifier(CodeQualifierKinds.Value, "repo.Store") },
            ],
            Generated = "a generator wrote it",
            Dynamic = [new ExtractDynamic(3, "a call through a pointer", "pointer-call")],
        }.WithDigests(Text);
        var stored = new CodeGraphStoredExtract(CodeGraphManifest.CurrentSchema, "toy", 1, Blob, "src/a.toy", lines.Count, extract);
        var file = layout.GraphExtractFile(CodeGraphIds.ExtractName(Blob, "src/a.toy", "toy", 1));

        store.WriteGraphExtract(file, stored);
        Assert.Equal(Json(stored), Json(store.ReadGraphExtract(file)!));

        var files = new Dictionary<string, CodeGraphFileEntry>(StringComparer.Ordinal)
        {
            ["src/a.toy"] = new(Blob, CodeFileStates.Parsed, 10) { Language = "toy", Extract = CodeGraphIds.ExtractName(Blob, "src/a.toy", "toy", 1) },
            ["link"] = new(null, CodeFileStates.Link, 0),
        };
        var extractors = new[] { new CodeGraphExtractorVersion("toy", 1) };
        var manifest = new CodeGraphManifest(CodeGraphManifest.CurrentSchema, "1.0.0", extractors, directory.Path, null, true, DateTimeOffset.UnixEpoch, files, CodeGraphManifest.DigestOf(extractors, files));

        store.WriteGraphManifest(layout, manifest);
        var manifestRead = store.ReadGraphManifest(layout)!;
        Assert.Null(manifestRead.Files["link"].Blob);
        Assert.Null(manifestRead.Head);
        Assert.Equal(Json(manifest), Json(manifestRead));

        var symbols = new CodeGraphSymbolsFile(
            CodeGraphManifest.CurrentSchema,
            manifest.Digest,
            CodeGraphLinker.Revision,
            [new("file:src/a.toy", "abcdef", CodeNodeKinds.File, ["src/a.toy"]), new("toy:A", "123456", CodeNodeKinds.Type, ["src/a.toy"])],
            [new CodeEdge("file:src/a.toy", "toy:A", CodeEdgeKinds.Contains, 1, CodeConfidences.Exact)],
            new Dictionary<string, int> { ["toy:A"] = 1 });

        store.WriteGraphSymbols(layout, symbols);
        Assert.Equal(Json(symbols), Json(store.ReadGraphSymbols(layout)!));

        // A record the store would not read back is refused before a byte is written.
        var other = layout.GraphExtractFile(CodeGraphIds.ExtractName(Blob, "src/b.toy", "toy", 1));
        var refused = Assert.ThrowsAny<Exception>(() => store.WriteGraphExtract(other, stored with { Extract = extract with { Nodes = [Node with { Summary = "\uD83D" }] } }));
        Assert.Contains("a surrogate cut from its pair", refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(other));
    }

    /// <summary>A handle is the first six characters of an id's digest, lengthened for every id that shares them until none does.</summary>
    [Fact]
    public void AHandle_IsSixCharacters_LengthenedOnlyWhereTwoShareThem()
    {
        var ids = Enumerable.Range(0, 20_000).Select(at => $"toy:N{at}").ToList();
        var hashes = ids.ToDictionary(id => id, CodeGraphIds.Sha256, StringComparer.Ordinal);
        var groups = ids.GroupBy(id => hashes[id][..CodeGraphIds.HandleLength], StringComparer.Ordinal).ToList();
        var clash = groups.First(group => group.Count() > 1).ToList();
        var alone = groups.First(group => group.Count() == 1).Single();

        var handles = CodeGraphIds.Handles(ids);

        Assert.Equal(ids.Count, handles.Values.Distinct(StringComparer.Ordinal).Count());
        // Each of a clash is lengthened exactly past what it shares with another, and no further.
        static int Shared(string one, string other)
        {
            var at = 0;

            while (at < one.Length && one[at] == other[at])
            {
                at++;
            }

            return at;
        }

        Assert.All(clash, id => Assert.Equal(1 + clash.Where(other => other != id).Max(other => Shared(hashes[id], hashes[other])), handles[id].Length));
        Assert.Equal(hashes[alone][..CodeGraphIds.HandleLength], handles[alone]);
        Assert.All(handles, pair => Assert.StartsWith(pair.Value, hashes[pair.Key], StringComparison.Ordinal));
    }

    /// <summary>A file entry is refused for each thing it gets wrong: its state, its blob, its size, and an extract named where it was not read.</summary>
    [Fact]
    public void AFileEntry_IsRefusedForEachThingItGetsWrong()
    {
        var read = new CodeGraphFileEntry(Blob, CodeFileStates.Parsed, 10) { Language = "toy", Extract = "aa/x.toy-1.json" };

        Assert.Null(read.Problem());
        Assert.Null(new CodeGraphFileEntry(null, CodeFileStates.Link, 0).Problem());
        Assert.Null(new CodeGraphFileEntry(new string('b', 64), CodeFileStates.Binary, 3).Problem());

        Assert.Contains("its state is 'lost'", (read with { State = "lost" }).Problem(), StringComparison.Ordinal);
        Assert.Equal("it names no blob", (read with { Blob = null }).Problem());
        Assert.Contains("is no object id", (read with { Blob = Blob.ToUpperInvariant() }).Problem(), StringComparison.Ordinal);
        Assert.Contains("is no object id", (read with { Blob = Blob[..39] }).Problem(), StringComparison.Ordinal);
        Assert.Equal("its size is below zero", (read with { Bytes = -1 }).Problem());
        Assert.Equal("it is parsed and names no extract", (read with { Extract = null }).Problem());
        Assert.Equal("it is binary and names an extract", (read with { State = CodeFileStates.Binary }).Problem());
        Assert.Equal("it is parsed and names no implementation", (read with { Language = null }).Problem());
        Assert.NotNull((read with { Extract = "../x.json" }).Problem());
    }

    /// <summary>
    /// A manifest is refused for another schema, no tool or tree, an implementation unnamed or named twice, a path that is
    /// none, a bad entry, a file read by an implementation it names no version of or naming another extract than that
    /// version makes of its blob, and a digest that is not its own.
    /// </summary>
    [Fact]
    public void AManifest_IsRefusedForEachThingItGetsWrong()
    {
        var files = new Dictionary<string, CodeGraphFileEntry>(StringComparer.Ordinal) { ["src/a.toy"] = new(Blob, CodeFileStates.Parsed, 10) { Language = "toy", Extract = CodeGraphIds.ExtractName(Blob, "src/a.toy", "toy", 1) } };
        var extractors = new[] { new CodeGraphExtractorVersion("toy", 1) };
        var manifest = new CodeGraphManifest(CodeGraphManifest.CurrentSchema, "1.0.0", extractors, "/tree", null, false, DateTimeOffset.UnixEpoch, files, CodeGraphManifest.DigestOf(extractors, files));

        Assert.Null(manifest.Problem());
        Assert.Equal("its schema is 2, and this build reads 1", (manifest with { Schema = 2 }).Problem());
        Assert.Equal("it names no tool", (manifest with { Tool = "" }).Problem());
        Assert.Equal("it names no tree", (manifest with { Tree = " " }).Problem());
        Assert.Contains("it names an implementation as 'toy' at version 0", (manifest with { Extractors = [new("toy", 0)] }).Problem(), StringComparison.Ordinal);
        Assert.NotNull((manifest with { Files = new Dictionary<string, CodeGraphFileEntry> { ["../a.toy"] = files["src/a.toy"] } }).Problem());
        Assert.Equal("its file 'src/a.toy': its size is below zero", (manifest with { Files = new Dictionary<string, CodeGraphFileEntry> { ["src/a.toy"] = files["src/a.toy"] with { Bytes = -1 } } }).Problem());
        Assert.Equal("it names the implementation 'toy' twice", (manifest with { Extractors = [new("toy", 1), new("toy", 2)] }).Problem());
        Assert.Equal("its file 'src/a.toy' was read by 'toy', which it names no version of", (manifest with { Extractors = [new("other", 1)] }).Problem());
        Assert.Equal(
            $"its file 'src/a.toy' names the extract '{files["src/a.toy"].Extract}', which is not the one 'toy' at version 2 makes of its blob",
            (manifest with { Extractors = [new("toy", 2)] }).Problem());
        Assert.Equal(
            "its file 'src/a.toy' names the extract 'aa/x.json', which is not the one 'toy' at version 1 makes of its blob",
            (manifest with { Files = new Dictionary<string, CodeGraphFileEntry> { ["src/a.toy"] = files["src/a.toy"] with { Extract = "aa/x.json" } } }).Problem());
        Assert.Equal("its digest is not the digest of what it says", (manifest with { Digest = "0" }).Problem());

        // Its key naming is held to the shape's one rule of a sound keyNaming - the one the definition's check words for its
        // block - a policy named twice among it.
        Assert.Equal("its keyNaming names no policy", (manifest with { KeyNaming = [] }).Problem());
        Assert.Equal("its keyNaming names 'camelCase' more than once", (manifest with { KeyNaming = ["camelCase", "PascalCase", "camelCase"] }).Problem());
        Assert.Equal("its keyNaming names 'PascalCase', which is none of 'camelCase', 'snake_case', 'kebab-case', 'exact'", (manifest with { KeyNaming = ["PascalCase"] }).Problem());
        Assert.Null((manifest with { KeyNaming = ["snake_case", "exact"] }).Problem());
        Assert.Equal(
            [(KeyNamingFault.Twice, "camelCase"), (KeyNamingFault.Twice, "PascalCase"), (KeyNamingFault.Unknown, "PascalCase")],
            CodebaseGraphShape.NamingProblems(["camelCase", "PascalCase", "camelCase", "PascalCase"]));
        Assert.Throws<ArgumentException>(() => CodebaseGraphShape.NamingOf(["exact", "exact"]));

        // When it was synced and the tool that synced it are not what the link result is made from.
        Assert.Equal(manifest.Digest, CodeGraphManifest.DigestOf(extractors, new Dictionary<string, CodeGraphFileEntry>(files)));
        Assert.Null((manifest with { Tool = "2.0.0", SyncedAt = DateTimeOffset.UtcNow }).Problem());
    }

    /// <summary>A stored extract is refused for another schema, no implementation, no blob, a path that is none, no line, no extract, a node with no digest, and an extract that is not sound.</summary>
    [Fact]
    public void AStoredExtract_IsRefusedForEachThingItGetsWrong()
    {
        var extract = new FileExtract([Node]).WithDigests("class A\ngo\nend");
        var stored = new CodeGraphStoredExtract(CodeGraphManifest.CurrentSchema, "toy", 1, Blob, "src/a.toy", 3, extract);

        Assert.Null(stored.Problem());
        Assert.Equal("its schema is 0, and this build reads 1", (stored with { Schema = 0 }).Problem());
        Assert.Equal("it names no implementation", (stored with { Version = 0 }).Problem());
        Assert.Equal("it names no blob", (stored with { Blob = "x" }).Problem());
        Assert.NotNull((stored with { Path = "/abs" }).Problem());
        Assert.Equal("its file has no line", (stored with { Lines = 0 }).Problem());
        Assert.Equal("it holds no extract", (stored with { Extract = null! }).Problem());
        Assert.Equal("its node 'toy:A' has no digest", (stored with { Extract = new FileExtract([Node]) }).Problem());
        Assert.Contains("its lines 1-3 are not lines of the file's 2", (stored with { Lines = 2 }).Problem(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A manifest's digest is the hash of the text its implementations and files serialize to, in ordinal order of path -
    /// hashed as it is serialized, never held as that text.
    /// </summary>
    [Fact]
    public void AManifestsDigest_IsTheHashOfTheTextItsFilesSerializeTo()
    {
        var files = new Dictionary<string, CodeGraphFileEntry>(StringComparer.Ordinal)
        {
            ["src/b.toy"] = new(Blob, CodeFileStates.Parsed, 10) { Language = "toy", Extract = "aa/b.json" },
            ["src/a.toy"] = new(Blob, CodeFileStates.Parsed, 20) { Language = "toy", Extract = "aa/a.json" },
            ["link"] = new(null, CodeFileStates.Link, 0),
        };
        var extractors = new[] { new CodeGraphExtractorVersion("toy", 1) };
        var text = JsonSerializer.Serialize(new { extractors, files = files.OrderBy(file => file.Key, StringComparer.Ordinal).ToList() }, JsonStateFile.LineOptions);

        Assert.Equal(CodeGraphIds.Sha256(text), CodeGraphManifest.DigestOf(extractors, files));
    }

    /// <summary>
    /// A link result is refused for another schema, no manifest, a node with no handle or twice, a handle two nodes share, a
    /// node of no kind a graph holds, an edge that is not one between its nodes, and a count of what references named that is
    /// below zero or of no node it holds.
    /// </summary>
    [Fact]
    public void ALinkResult_IsRefusedForEachThingItGetsWrong()
    {
        var symbols = new CodeGraphSymbolsFile(
            CodeGraphManifest.CurrentSchema,
            "digest",
            CodeGraphLinker.Revision,
            [new("file:a.toy", "abcdef", CodeNodeKinds.File, ["a.toy"]), new("toy:A", "123456", CodeNodeKinds.Type, ["a.toy"])],
            [new CodeEdge("file:a.toy", "toy:A", CodeEdgeKinds.Contains, 1, CodeConfidences.Exact)],
            new Dictionary<string, int>());

        Assert.Null(symbols.Problem());
        Assert.Equal("its schema is 3, and this build reads 1", (symbols with { Schema = 3 }).Problem());
        Assert.Equal("it names no manifest", (symbols with { Graph = "" }).Problem());
        Assert.Equal("it names no linker's revision", (symbols with { Linker = 0 }).Problem());
        Assert.Equal("it holds a node with no id, handle or file", (symbols with { Nodes = [new("toy:A", "", CodeNodeKinds.Type, ["a.toy"])] }).Problem());
        Assert.Equal("it holds a node with no id, handle or file", (symbols with { Nodes = [new("toy:A", "123456", CodeNodeKinds.Type, [])] }).Problem());
        Assert.Equal("it holds the node 'toy:A' twice", (symbols with { Nodes = [.. symbols.Nodes, symbols.Nodes[1]] }).Problem());
        Assert.StartsWith("its edge 'file:a.toy' -> 'toy:Z'", (symbols with { Edges = [new CodeEdge("file:a.toy", "toy:Z", CodeEdgeKinds.Contains, 1, CodeConfidences.Exact)] }).Problem(), StringComparison.Ordinal);
        Assert.NotNull((symbols with { Edges = [new CodeEdge("file:a.toy", "toy:A", "owns", 1, CodeConfidences.Exact)] }).Problem());
        Assert.NotNull((symbols with { Edges = [new CodeEdge("file:a.toy", "toy:A", CodeEdgeKinds.Contains, 1, "probably")] }).Problem());
        Assert.Equal(
            "its node 'toy:A' has the handle 'abcdef', which another node has: a handle names one node of its view",
            (symbols with { Nodes = [symbols.Nodes[0], symbols.Nodes[1] with { Handle = "abcdef" }] }).Problem());
        Assert.Equal("its node 'toy:A' is of the kind 'gadget', which no node of a graph is", (symbols with { Nodes = [symbols.Nodes[0], symbols.Nodes[1] with { Kind = "gadget" }] }).Problem());
        Assert.Equal(
            "it counts 1 of what the references of 'toy:Z' named outside the repository, and that is no count of one of its nodes",
            (symbols with { External = new Dictionary<string, int> { ["toy:Z"] = 1 } }).Problem());
        Assert.Equal(
            "it counts -1 of what the references of 'toy:A' named outside the repository, and that is no count of one of its nodes",
            (symbols with { External = new Dictionary<string, int> { ["toy:A"] = -1 } }).Problem());
        Assert.Null((symbols with { External = new Dictionary<string, int> { ["toy:A"] = 0, ["file:a.toy"] = 2 } }).Problem());
    }

    /// <summary>
    /// A link result is kept as lines: its head, counting what follows, then each node, each edge and each count of what a
    /// node's references named outside the repository, on a line of its own - a node's id written against the shorter id of
    /// the node that contains it where that is shorter, an edge and a count naming each node by its place - and reads back
    /// as it was written, each id the whole of it.
    /// </summary>
    [Fact]
    public void ALinkResult_IsKeptAsLines_ItsHeadCountingWhatFollows()
    {
        using var directory = new TempDirectory();
        var (store, layout) = Store(directory);
        var symbols = Symbols();

        store.WriteGraphSymbols(layout, symbols);

        Assert.Equal(
            [
                Json(new CodeGraphSymbolsHead(CodeGraphManifest.CurrentSchema, "digest", CodeGraphLinker.Revision, 2, 1, 1)),
                Json(new CodeGraphSymbolLine(new StoredName("file:a.toy"), "abcdef", CodeNodeKinds.File, ["a.toy"])),
                Json(new CodeGraphSymbolLine(new StoredName("toy:A"), "123456", CodeNodeKinds.Type, ["a.toy"])),
                Json(new CodeGraphEdgeLine(0, 1, CodeEdgeKinds.Contains, 1, CodeConfidences.Exact)),
                Json(new CodeGraphCountLine(1, 1)),
                "",
            ],
            File.ReadAllText(layout.GraphSymbolsFile).Split('\n'));
        Assert.Equal(Json(symbols), Json(store.ReadGraphSymbols(layout)!));

        // A node whose id goes on from its container's is written as what it keeps of that one and the rest - where that
        // is shorter than the id whole, which it is not for a container's id of five characters.
        var nested = symbols with
        {
            Nodes = [.. symbols.Nodes, new("toy:A.Inner", "654321", CodeNodeKinds.Type, ["a.toy"]), new("toy:A.Inner.Deepest", "fedcba", CodeNodeKinds.Type, ["a.toy"])],
            Edges =
            [
                .. symbols.Edges,
                new CodeEdge("toy:A", "toy:A.Inner", CodeEdgeKinds.Contains, 2, CodeConfidences.Exact),
                new CodeEdge("toy:A.Inner", "toy:A.Inner.Deepest", CodeEdgeKinds.Contains, 3, CodeConfidences.Exact),
            ],
        };

        store.WriteGraphSymbols(layout, nested);

        var written = File.ReadAllLines(layout.GraphSymbolsFile);
        Assert.Equal(Json(new CodeGraphSymbolLine(new StoredName("toy:A.Inner"), "654321", CodeNodeKinds.Type, ["a.toy"])), written[3]);
        Assert.Equal(Json(new CodeGraphSymbolLine(new StoredName(".Deepest", Kept: 11, At: 2), "fedcba", CodeNodeKinds.Type, ["a.toy"])), written[4]);
        Assert.Equal(Json(nested), Json(store.ReadGraphSymbols(layout)!));
    }

    /// <summary>
    /// A link result keeps each node's signs of what the graph does not see on a line of its own after the counts - the node by its
    /// place, its signs in order - and reads them back as written; signs of no node it holds, a node given no sign or an empty one,
    /// and a node's signs said twice are refused.
    /// </summary>
    [Fact]
    public void ALinkResult_KeepsEachNodesSigns_AfterItsCounts_AndRefusesThoseOfNoNodeOrNoSign()
    {
        using var directory = new TempDirectory();
        var (store, layout) = Store(directory);
        var symbols = Symbols() with
        {
            Signs = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["file:a.toy"] = [CodeGraphLimit.CandidateImports], ["toy:A"] = [CodeGraphLimit.OutsideImport, CodeGraphLimit.Unbound] },
        };

        Assert.Null(symbols.Problem());
        store.WriteGraphSymbols(layout, symbols);

        Assert.Equal(
            [
                Json(new CodeGraphSymbolsHead(CodeGraphManifest.CurrentSchema, "digest", CodeGraphLinker.Revision, 2, 1, 1) { Signs = 2 }),
                Json(new CodeGraphSymbolLine(new StoredName("file:a.toy"), "abcdef", CodeNodeKinds.File, ["a.toy"])),
                Json(new CodeGraphSymbolLine(new StoredName("toy:A"), "123456", CodeNodeKinds.Type, ["a.toy"])),
                Json(new CodeGraphEdgeLine(0, 1, CodeEdgeKinds.Contains, 1, CodeConfidences.Exact)),
                Json(new CodeGraphCountLine(1, 1)),
                Json(new CodeGraphSignLine(0, [CodeGraphLimit.CandidateImports])),
                Json(new CodeGraphSignLine(1, [CodeGraphLimit.OutsideImport, CodeGraphLimit.Unbound])),
                "",
            ],
            File.ReadAllText(layout.GraphSymbolsFile).Split('\n'));
        Assert.Equal(Json(symbols), Json(store.ReadGraphSymbols(layout)!));

        const string NoSign = "that is no node of it or no sign of one";

        Assert.Equal($"it says signs of what the graph does not see of 'toy:Z', and {NoSign}", (symbols with { Signs = new Dictionary<string, IReadOnlyList<string>> { ["toy:Z"] = [CodeGraphLimit.Unbound] } }).Problem());
        Assert.Equal($"it says signs of what the graph does not see of 'toy:A', and {NoSign}", (symbols with { Signs = new Dictionary<string, IReadOnlyList<string>> { ["toy:A"] = [] } }).Problem());
        Assert.Equal($"it says signs of what the graph does not see of 'toy:A', and {NoSign}", (symbols with { Signs = new Dictionary<string, IReadOnlyList<string>> { ["toy:A"] = [CodeGraphLimit.Unbound, " "] } }).Problem());

        var lines = File.ReadAllLines(layout.GraphSymbolsFile);
        string[] twice = [.. lines[..5], lines[6], lines[6]];
        File.WriteAllText(layout.GraphSymbolsFile, string.Concat(twice.Select(line => line + "\n")));
        Assert.Contains("it says the signs of the node 'toy:A' twice", Assert.Throws<HarnessException>(() => store.ReadGraphSymbols(layout)).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A link result's file of fewer or more lines than its head counts, a line that does not read as what it is there, a
    /// head that counts fewer than none of a kind, a node counted twice, an edge naming a node by no place of one and ids
    /// written against each other or against no node are each refused, naming the line where there is one.
    /// </summary>
    [Fact]
    public void ALinkResultsFile_OfOtherLinesThanItsHeadCounts_IsRefused_NamingTheLine()
    {
        using var directory = new TempDirectory();
        var (store, layout) = Store(directory);
        store.WriteGraphSymbols(layout, Symbols());
        var lines = File.ReadAllLines(layout.GraphSymbolsFile);
        var head = new CodeGraphSymbolsHead(CodeGraphManifest.CurrentSchema, "digest", CodeGraphLinker.Revision, 2, 1, 1);

        string Refused(params string[] written)
        {
            File.WriteAllText(layout.GraphSymbolsFile, string.Concat(written.Select(line => line + "\n")));
            return Assert.Throws<HarnessException>(() => store.ReadGraphSymbols(layout)).Message;
        }

        Assert.Equal(Json(head), lines[0]);
        Assert.Contains("it ends after line 4, where a count of what a node's references named outside the repository was to be", Refused(lines[..^1]), StringComparison.Ordinal);
        Assert.Contains("it goes on past line 5, where it was to end", Refused([.. lines, lines[^1]]), StringComparison.Ordinal);
        Assert.Contains("line 4, an edge, does not read: it holds null", Refused([.. lines[..3], "null", .. lines[4..]]), StringComparison.Ordinal);
        Assert.Contains("line 2, a node, does not read: ", Refused([lines[0], "{\"id\":\"toy:B\"}", .. lines[2..]]), StringComparison.Ordinal);
        Assert.Contains("its head counts fewer than no lines of a kind", Refused([Json(head with { Edges = -1 }), .. lines[1..]]), StringComparison.Ordinal);
        Assert.Contains("it counts the node 'toy:A' twice", Refused([Json(head with { External = 2 }), .. lines[1..], lines[^1]]), StringComparison.Ordinal);
        Assert.Contains(
            "an edge names node 7, and it holds 2",
            Refused([.. lines[..3], Json(new CodeGraphEdgeLine(0, 7, CodeEdgeKinds.Contains, 1, CodeConfidences.Exact)), .. lines[4..]]),
            StringComparison.Ordinal);
        Assert.Contains(
            "node 0 is written, through what it is written against, against itself",
            Refused(
                lines[0],
                Json(new CodeGraphSymbolLine(new StoredName("ile:a.toy", Kept: 1, At: 1), "abcdef", CodeNodeKinds.File, ["a.toy"])),
                Json(new CodeGraphSymbolLine(new StoredName("oy:A", Kept: 1, At: 0), "123456", CodeNodeKinds.Type, ["a.toy"])),
                lines[3],
                lines[4]),
            StringComparison.Ordinal);
        Assert.Contains(
            "node 1 is written against node 9, and there are 2",
            Refused(lines[0], lines[1], Json(new CodeGraphSymbolLine(new StoredName("oy:A", Kept: 1, At: 9), "123456", CodeNodeKinds.Type, ["a.toy"])), lines[3], lines[4]),
            StringComparison.Ordinal);

        // One an older linker wrote - its head counting what references named ambiguously, and those counts after the
        // others - is no link result this build reads, and is refused: the next sync links the graph again.
        var older = Json(head).Replace("\"external\":1", "\"external\":1,\"unresolved\":1", StringComparison.Ordinal);
        Assert.NotEqual(Json(head), older);
        Assert.Contains("unresolved", Refused([older, .. lines[1..], Json(new CodeGraphCountLine(0, 2))]), StringComparison.Ordinal);
    }

    /// <summary>
    /// An extract's names are written against its nodes' parents' where that is shorter - an id and a qualified name as
    /// what they keep of the parent's and the rest, a name that would keep too little whole - and a scope's against its
    /// parent's where the two can never be written against each other, so scopes that are each other's parents - a Dart
    /// library and its part - are kept by name; never between the two halves of a surrogate pair, and read back as they
    /// were. One that keeps more than its base holds, or keeps some of a parent its node has none of, and nodes declared
    /// inside each other, are refused.
    /// </summary>
    [Fact]
    public void AnExtractsNames_AreWrittenAgainstTheirParents_AndOnesThatDoNotRead_AreRefused()
    {
        var extract = new FileExtract(
        [
            new ExtractNode("toy:Alpha", CodeNodeKinds.Type, "Alpha", "Alpha", 1, 3, "class Alpha"),
            new ExtractNode("toy:Alpha.beta", CodeNodeKinds.Member, "beta", "Alpha.beta", 2, 2, "Alpha.beta") { Parent = "toy:Alpha" },
        ])
        {
            Scopes =
            [
                new ExtractScope("Alpha", Open: true),
                new ExtractScope("Alpha.Inner", Open: true) { Parent = "Alpha" },
                new ExtractScope("lib/shapes.dart", Open: true) { Parent = "shapes" },
                new ExtractScope("shapes", Open: true) { Parent = "lib/shapes.dart" },
            ],
        };
        var text = Json(extract);

        Assert.Contains("\"id\":[9,\".beta\"]", text, StringComparison.Ordinal);
        Assert.Contains("\"qualified\":[5,\".beta\"]", text, StringComparison.Ordinal);
        Assert.Contains("\"signature\":\"Alpha.beta\"", text, StringComparison.Ordinal);
        Assert.Contains("{\"name\":[5,\".Inner\"],\"open\":true,\"parentAt\":0}", text, StringComparison.Ordinal);
        Assert.Contains("{\"name\":\"lib/shapes.dart\",\"open\":true,\"parentAt\":3}", text, StringComparison.Ordinal);
        Assert.Contains("{\"name\":\"shapes\",\"open\":true,\"parent\":\"lib/shapes.dart\"}", text, StringComparison.Ordinal);
        Assert.Equal(
            [("Alpha", null), ("Alpha.Inner", "Alpha"), ("lib/shapes.dart", "shapes"), ("shapes", "lib/shapes.dart")],
            JsonSerializer.Deserialize<FileExtract>(text, JsonStateFile.LineOptions)!.Scopes.Select(scope => (scope.Name.Whole(), scope.Parent?.Whole())));
        Assert.Equal(text, Json(JsonSerializer.Deserialize<FileExtract>(text, JsonStateFile.LineOptions)));

        static string Refused(string written) => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<FileExtract>(written, JsonStateFile.LineOptions)).Message;

        Assert.Contains("a name keeps 99 characters of a name of 9", Refused(text.Replace("[9,\".beta\"]", "[99,\".beta\"]", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("node 0's id keeps characters of a parent it has none of", Refused(text.Replace("\"id\":\"toy:Alpha\"", "\"id\":[3,\":Alpha\"]", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains(
            "node 0 is written, through what it is written against, against itself",
            Refused(text.Replace("\"signature\":\"class Alpha\"", "\"signature\":\"class Alpha\",\"parent\":1", StringComparison.Ordinal)),
            StringComparison.Ordinal);

        var kept = new string('p', 20);
        Assert.Equal(new StoredName("\U0001F600x", 20), StoredName.Against($"{kept}\U0001F600x", [("id", $"{kept}\U0001F601")]));
    }

    /// <summary>
    /// A file nested thousands deep - each node's id and qualified name its whole path - is kept in an extract and a link
    /// result that grow with its depth and not with the square of it, each name written against its parent's and each id
    /// against its container's: twice as deep is about twice the bytes, a few hundred a level; and each reads back to the
    /// very ids, names and handles the reader and the link gave. Names written whole come to four times the bytes for twice
    /// the depth, and thousands a level.
    /// </summary>
    [Fact]
    public void ADeepFilesExtractAndLinkResult_GrowWithItsDepth_AndReadBackTheSameIds()
    {
        using var directory = new TempDirectory();
        var (store, layout) = Store(directory);

        (long Extract, long Symbols) Kept(int depth)
        {
            var text = string.Concat(Enumerable.Repeat("{\"a\":", depth)) + "1" + new string('}', depth);
            var lines = CodeFileText.Lines(text);
            var extract = new JsonExtractor().Extract(new CodeFileInput("deep.json", text)).WithDigests(text);
            var file = layout.GraphExtractFile(CodeGraphIds.ExtractName(Blob, "deep.json", "json", 1));

            store.WriteGraphExtract(file, new CodeGraphStoredExtract(CodeGraphManifest.CurrentSchema, "json", 1, Blob, "deep.json", lines.Count, extract));
            var read = store.ReadGraphExtract(file)!.Extract;

            Assert.Equal(depth, read.Nodes.Count);
            Assert.Equal(
                extract.Nodes.Select(node => (node.Id, node.Qualified, node.Signature, node.Scope, node.Parent)),
                read.Nodes.Select(node => (node.Id, node.Qualified, node.Signature, node.Scope, node.Parent)));

            var linked = CodeGraphLinker.Link([new CodeGraphFile("deep.json", "json", extract)]);
            var handles = CodeGraphIds.Handles(linked.Symbols.Select(symbol => symbol.Id));
            var symbols = new CodeGraphSymbolsFile(
                CodeGraphManifest.CurrentSchema,
                "digest",
                CodeGraphLinker.Revision,
                [.. linked.Symbols.Select(symbol => new CodeGraphSymbolEntry(symbol.Id, handles[symbol.Id], symbol.Kind, symbol.Paths))],
                linked.Edges,
                linked.External);

            store.WriteGraphSymbols(layout, symbols);
            var back = store.ReadGraphSymbols(layout)!;

            Assert.Equal(symbols.Nodes.Select(node => (node.Id, node.Handle)), back.Nodes.Select(node => (node.Id, node.Handle)));
            Assert.Equal(symbols.Edges, back.Edges);
            return (new FileInfo(file).Length, new FileInfo(layout.GraphSymbolsFile).Length);
        }

        var half = Kept(2_500);
        var whole = Kept(5_000);

        Assert.InRange((double)whole.Extract / half.Extract, 1.8, 2.3);
        Assert.InRange((double)whole.Symbols / half.Symbols, 1.8, 2.3);
        Assert.InRange(whole.Extract, 1, 5_000L * 600);
        Assert.InRange(whole.Symbols, 1, 5_000L * 300);
    }

    /// <summary>
    /// A file nested thousands deep, read by its reader and linked again, holds link keys that grow with its depth and
    /// not with the square of it: each node's id the name before it and its own part, as its extract holds it, and what
    /// the link keeps of the keys it may read again - each node's id, each scope's name - counts each name's own part
    /// alone. Twice the depth is about twice the characters; a link whose keys were each name written out whole holds
    /// every node's whole path, which for twice the depth is four times the characters.
    /// </summary>
    [Fact]
    public void ADeepFilesLinkKeys_GrowWithItsDepth_NotWithTheSquareOfIt()
    {
        long Kept(int depth)
        {
            var text = string.Concat(Enumerable.Repeat("{\"a\":", depth)) + "1" + new string('}', depth);
            var extract = new JsonExtractor().Extract(new CodeFileInput("deep.json", text)).WithDigests(text);
            var linked = CodeGraphLinker.Link([new CodeGraphFile("deep.json", "json", extract)]);
            var relinked = CodeGraphLinker.Relink(linked, [new CodeGraphFile("deep.json", "json", extract)]);

            Assert.Equal([.. linked.Symbols.Select(symbol => symbol.Id)], [.. relinked.Symbols.Select(symbol => symbol.Id)]);
            Assert.Equal(linked.Edges, relinked.Edges);
            Assert.True(relinked.Memory!.Keys.Characters > 0, "the link kept no key of the file");
            return relinked.Memory.Keys.Characters;
        }

        var half = Kept(2_500);
        var whole = Kept(5_000);

        Assert.InRange((double)whole / Math.Max(half, 1), 1.8, 2.3);
        Assert.True(whole < 5_000L * 100, $"linking a file 5,000 deep kept {whole} key characters");
    }

    /// <summary>
    /// A large link result is written and read a line at a time: no write to its file and no read of it is larger than a
    /// few lines, where a list written or read whole is held whole while it is.
    /// </summary>
    [Fact]
    public void ALargeLinkResult_IsWrittenAndReadALineAtATime()
    {
        using var directory = new TempDirectory();
        var harness = new HarnessFactory();
        var disk = new Transfers(harness.FileSystem);
        var store = new OrchestrationStore(disk, harness.Output);
        var layout = OrchestratorLayout.Of(new HarnessLayout(directory.Path, directory.Path), "run");
        var nodes = Enumerable.Range(0, 20_000).Select(at => new CodeGraphSymbolEntry($"toy:N{at}", $"{at:x6}", CodeNodeKinds.Type, ["a.toy"])).ToList();
        var symbols = Symbols() with
        {
            Nodes = [.. Symbols().Nodes, .. nodes],
            Edges = [.. Enumerable.Range(0, 100_000).Select(at => new CodeEdge("file:a.toy", nodes[at % nodes.Count].Id, CodeEdgeKinds.Contains, at, CodeConfidences.Exact))],
        };

        store.WriteGraphSymbols(layout, symbols);
        var read = store.ReadGraphSymbols(layout)!;

        Assert.True(new FileInfo(layout.GraphSymbolsFile).Length > 4 << 20);
        Assert.Equal(symbols.Edges.Count, read.Edges.Count);
        Assert.InRange(disk.LargestWrite, 1, 64 << 10);
        Assert.InRange(disk.LargestRead, 1, 64 << 10);
    }

    /// <summary>
    /// An extract's name holds its blob, the whole digest of its path, its implementation and version: a renamed file is
    /// read again, a raised version too; the name an earlier build gave it held eight characters of the digest alone.
    /// </summary>
    [Fact]
    public void AnExtractsName_HoldsItsBlobPathImplementationAndVersion()
    {
        var name = CodeGraphIds.ExtractName(Blob, "src/a.toy", "toy", 2);

        Assert.Equal($"aa/{Blob}-{CodeGraphIds.Sha256("src/a.toy")}.toy-2.json", name);
        Assert.Equal($"aa/{Blob}-{CodeGraphIds.Sha256("src/a.toy")[..8]}.toy-2.json", CodeGraphIds.EarlierExtractName(Blob, "src/a.toy", "toy", 2));
        Assert.NotEqual(name, CodeGraphIds.ExtractName(Blob, "src/b.toy", "toy", 2));
        Assert.NotEqual(name, CodeGraphIds.ExtractName(Blob, "src/a.toy", "toy", 3));
    }

    /// <summary>A store over a real directory, and a run's layout in it.</summary>
    private static (OrchestrationStore Store, OrchestratorLayout Layout) Store(TempDirectory directory)
    {
        var harness = new HarnessFactory();
        return (new OrchestrationStore(harness.FileSystem, harness.Output), OrchestratorLayout.Of(new HarnessLayout(directory.Path, directory.Path), "run"));
    }

    /// <summary>A record as the store writes it, on one line.</summary>
    private static string Json<T>(T record) => JsonSerializer.Serialize(record, JsonStateFile.LineOptions);

    /// <summary>A link result of two nodes, an edge between them and a count of what one's references named outside the repository.</summary>
    private static CodeGraphSymbolsFile Symbols() => new(
        CodeGraphManifest.CurrentSchema,
        "digest",
        CodeGraphLinker.Revision,
        [new("file:a.toy", "abcdef", CodeNodeKinds.File, ["a.toy"]), new("toy:A", "123456", CodeNodeKinds.Type, ["a.toy"])],
        [new CodeEdge("file:a.toy", "toy:A", CodeEdgeKinds.Contains, 1, CodeConfidences.Exact)],
        new Dictionary<string, int> { ["toy:A"] = 1 });

    /// <summary>The real file system, noting the most bytes any one read of a file asked for, and any one write of one gave.</summary>
    private sealed class Transfers(IFileSystem inner) : PassThroughFileSystem(inner)
    {
        /// <summary>The most bytes one read asked for.</summary>
        public int LargestRead { get; private set; }

        /// <summary>The most bytes one write gave.</summary>
        public int LargestWrite { get; private set; }

        public override Stream OpenRead(string path) => new Noted(base.OpenRead(path), this);

        public override void WriteAtomic(string path, Action<Stream> write) => base.WriteAtomic(path, stream => write(new Noted(stream, this)));

        /// <summary>A stream that notes each read's and each write's size.</summary>
        private sealed class Noted(Stream inner, Transfers noted) : Stream
        {
            public override bool CanRead => inner.CanRead;

            public override bool CanSeek => false;

            public override bool CanWrite => inner.CanWrite;

            public override long Length => throw new NotSupportedException();

            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                noted.LargestRead = Math.Max(noted.LargestRead, buffer.Length);
                return inner.Read(buffer);
            }

            public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                noted.LargestWrite = Math.Max(noted.LargestWrite, buffer.Length);
                inner.Write(buffer);
            }

            public override void Flush() => inner.Flush();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    inner.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }
}
