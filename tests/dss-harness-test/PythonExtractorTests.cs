using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;

namespace DssHarness.Tests;

/// <summary>Python's implementation over the structural scanner, held to the contract's kit, and the rules of its own the kit does not name.</summary>
public sealed class PythonExtractorTests : CodeGraphExtractorContractTests<PythonExtractor>
{
    internal const string Cart = """"
        """A cart of lines."""
        from __future__ import annotations
        import os.path as osp
        from dataclasses import dataclass, field
        from .base import BaseCart, Ordered
        from .lines import Line, make_line
        from ..shared import money as formatted

        MAX_LINES: int = 50
        default_tag = "cart"


        class CartError(Exception):
            """Raised when the cart cannot take a line."""


        @dataclass
        class Cart(BaseCart, Ordered):
            lines: list[Line] = field(default_factory=list)
            tag: str = default_tag

            def __init__(self, owner: str, limit: int = MAX_LINES, *args, **kwargs) -> None:
                self.owner = owner
                self.limit = limit
                super().__init__(*args)

            @property
            def count(self) -> int:
                return len(self.lines)

            def add(self, line: Line) -> CartError | None:
                if self.count >= self.limit:
                    raise CartError("full")
                self.lines.append(line)
                return None

            async def flush(self) -> None:
                await self.save()

            @staticmethod
            def parse(text: str) -> "Cart":
                if isinstance(text, str):
                    return Cart(owner=text)
                return None


        def total(cart: Cart) -> int:
            result = 0
            for line in cart.lines:
                result += line.cents * line.count
            return result


        def describe(cart: Cart, fmt=formatted) -> str:
            name = osp.basename(fmt)
            return f"{name} has {cart.count} line(s), total {total(cart)}"


        if __name__ == "__main__":
            print(describe(Cart(owner="main")))
        """";

    internal const string Base = """"
        """What every cart is built on."""


        class BaseCart:
            def __init__(self, *args):
                self.args = args

            def add(self, line):
                pass


        class Ordered:
            pass
        """";

    internal const string Lines = """"
        """A line of a cart."""
        from dataclasses import dataclass


        @dataclass
        class Line:
            name: str
            cents: int = 0
            count: int = 1


        def make_line(name: str, cents: int = 0) -> Line:
            return Line(name=name, cents=cents)
        """";

    internal const string Shared = """"
        """Money, as a receipt writes it."""
        def money(cents: int) -> str:
            return f"{cents // 100}.{cents % 100:02d}"
        """";

    internal const string Package = """"
        """The shop's cart package."""
        from .cart import Cart
        from .lines import Line as CartLine

        __all__ = ["Cart", "CartLine"]
        """";

    internal const string Stub = """
        from .cart import Cart

        def load(path: str) -> Cart: ...

        class Store:
            def __init__(self, cart: Cart) -> None: ...
        """;

    internal const string Window = """
        from pkg.cart import Cart

        cart = Cart(owner="window")
        print(cart.count)
        """;

    internal const string Tests = """
        import unittest
        import pytest
        from pkg.cart import Cart, total
        from pkg.lines import make_line


        @pytest.fixture
        def empty_cart() -> Cart:
            return Cart(owner="test")


        def test_total_counts_every_line(empty_cart):
            empty_cart.add(make_line("tea", 350))
            assert total(empty_cart) == 350


        def helper(x):
            return x * 2


        class TestAdding:
            def test_add_appends(self, empty_cart):
                empty_cart.add(make_line("tea"))
                assert empty_cart.count == 1


        class CartCase(unittest.TestCase):
            def test_parse(self):
                self.assertEqual(Cart.parse("x").count, 0)

            def helper(self):
                pass


        def test_parse_sample(sample):
            assert Cart.parse(sample).count == 0


        def test_sees_no_helper(helper):
            assert total
        """;

    internal const string Conftest = """"
        import pytest


        @pytest.fixture
        def sample() -> str:
            """A name a cart is parsed from."""
            return "sample"
        """";

    /// <inheritdoc/>
    public override IReadOnlyList<CodeGraphFixture> Fixtures { get; } =
    [
        new("pkg/cart.py", Cart)
        {
            Nodes =
            [
                new("python:pkg.cart", CodeNodeKinds.Namespace, (1, 60)),
                new("python:pkg.cart.MAX_LINES", CodeNodeKinds.Member, (9, 9)),
                new("python:pkg.cart.default_tag", CodeNodeKinds.Member, (10, 10)),
                new("python:pkg.cart.CartError", CodeNodeKinds.Type, (13, 14)),
                new("python:pkg.cart.Cart", CodeNodeKinds.Type, (17, 44)),
                new("python:pkg.cart.Cart.lines", CodeNodeKinds.Member, (19, 19)),
                new("python:pkg.cart.Cart.tag", CodeNodeKinds.Member, (20, 20)),
                new("python:pkg.cart.Cart.__init__", CodeNodeKinds.Member, (22, 25)),
                new("python:pkg.cart.Cart.count", CodeNodeKinds.Member, (27, 29)),
                new("python:pkg.cart.Cart.add", CodeNodeKinds.Member, (31, 35)),
                new("python:pkg.cart.Cart.flush", CodeNodeKinds.Member, (37, 38)),
                new("python:pkg.cart.Cart.parse", CodeNodeKinds.Member, (40, 44)),
                new("python:pkg.cart.total", CodeNodeKinds.Member, (47, 51)),
                new("python:pkg.cart.describe", CodeNodeKinds.Member, (54, 56)),
            ],
            Edges =
            [
                new CodeEdge("file:pkg/cart.py", "file:pkg/lines.py", CodeEdgeKinds.Imports, 6, CodeConfidences.Exact),
                new CodeEdge("file:pkg/cart.py", "file:shared.py", CodeEdgeKinds.Imports, 7, CodeConfidences.Exact),
                new CodeEdge("file:pkg/cart.py", "file:pkg/base.py", CodeEdgeKinds.Imports, 5, CodeConfidences.Exact),
                new CodeEdge("python:pkg.cart.Cart", "python:pkg.base.BaseCart", CodeEdgeKinds.Inherits, 18, CodeConfidences.Unique),
                new CodeEdge("python:pkg.cart.Cart", "python:pkg.base.Ordered", CodeEdgeKinds.MixesIn, 18, CodeConfidences.Unique),
                new CodeEdge("python:pkg.cart.Cart.lines", "python:pkg.lines.Line", CodeEdgeKinds.References, 19, CodeConfidences.Unique),
                new CodeEdge("python:pkg.cart.Cart.tag", "python:pkg.cart.default_tag", CodeEdgeKinds.Uses, 20, CodeConfidences.Exact),
                new CodeEdge("python:pkg.cart.Cart.__init__", "python:pkg.base.BaseCart.__init__", CodeEdgeKinds.Overrides, 22, CodeConfidences.Exact),
                new CodeEdge("python:pkg.cart.Cart.__init__", "python:pkg.cart.MAX_LINES", CodeEdgeKinds.Uses, 22, CodeConfidences.Exact),
                new CodeEdge("python:pkg.cart.Cart.__init__", "python:pkg.base.BaseCart.__init__", CodeEdgeKinds.Calls, 25, CodeConfidences.Exact),
                new CodeEdge("python:pkg.cart.Cart.add", "python:pkg.base.BaseCart.add", CodeEdgeKinds.Overrides, 31, CodeConfidences.Exact),
                new CodeEdge("python:pkg.cart.Cart.add", "python:pkg.cart.Cart.count", CodeEdgeKinds.Uses, 32, CodeConfidences.Exact),
                new CodeEdge("python:pkg.cart.Cart.add", "python:pkg.cart.CartError", CodeEdgeKinds.Instantiates, 33, CodeConfidences.Exact),
                new CodeEdge("python:pkg.cart.Cart.parse", "python:pkg.cart.Cart", CodeEdgeKinds.Instantiates, 43, CodeConfidences.Exact),
                new CodeEdge("python:pkg.cart.describe", "python:shared.money", CodeEdgeKinds.Uses, 54, CodeConfidences.Exact),
                new CodeEdge("python:pkg.cart.describe", "python:pkg.cart.total", CodeEdgeKinds.Calls, 56, CodeConfidences.Exact),
                new CodeEdge("python:pkg.cart", "python:pkg.cart.describe", CodeEdgeKinds.Calls, 60, CodeConfidences.Exact),
                new CodeEdge("python:pkg.cart", "python:pkg.cart.Cart", CodeEdgeKinds.Instantiates, 60, CodeConfidences.Exact),
            ],
        },
        new("pkg/base.py", Base)
        {
            Nodes =
            [
                new("python:pkg.base", CodeNodeKinds.Namespace, (1, 13)),
                new("python:pkg.base.BaseCart", CodeNodeKinds.Type, (4, 9)),
                new("python:pkg.base.BaseCart.__init__", CodeNodeKinds.Member, (5, 6)),
                new("python:pkg.base.BaseCart.add", CodeNodeKinds.Member, (8, 9)),
                new("python:pkg.base.Ordered", CodeNodeKinds.Type, (12, 13)),
            ],
        },
        new("pkg/lines.py", Lines)
        {
            Nodes =
            [
                new("python:pkg.lines", CodeNodeKinds.Namespace, (1, 13)),
                new("python:pkg.lines.Line", CodeNodeKinds.Type, (5, 9)),
                new("python:pkg.lines.Line.name", CodeNodeKinds.Member, (7, 7)),
                new("python:pkg.lines.Line.cents", CodeNodeKinds.Member, (8, 8)),
                new("python:pkg.lines.Line.count", CodeNodeKinds.Member, (9, 9)),
                new("python:pkg.lines.make_line", CodeNodeKinds.Member, (12, 13)),
            ],
            Edges =
            [
                new CodeEdge("python:pkg.lines.make_line", "python:pkg.lines.Line", CodeEdgeKinds.References, 12, CodeConfidences.Exact),
                new CodeEdge("python:pkg.lines.make_line", "python:pkg.lines.Line", CodeEdgeKinds.Instantiates, 13, CodeConfidences.Exact),
            ],
        },
        new("shared.py", Shared)
        {
            Nodes =
            [
                new("python:shared", CodeNodeKinds.Namespace, (1, 3)),
                new("python:shared.money", CodeNodeKinds.Member, (2, 3)),
            ],
        },
        new("pkg/__init__.py", Package)
        {
            Nodes =
            [
                new("python:pkg", CodeNodeKinds.Namespace, (1, 5)),
                new("python:pkg.__all__", CodeNodeKinds.Member, (5, 5)),
            ],
            Edges =
            [
                new CodeEdge("file:pkg/__init__.py", "file:pkg/cart.py", CodeEdgeKinds.Imports, 2, CodeConfidences.Exact),
                new CodeEdge("file:pkg/__init__.py", "file:pkg/lines.py", CodeEdgeKinds.Imports, 3, CodeConfidences.Exact),
            ],
        },
        new("pkg/util.pyi", Stub)
        {
            Nodes =
            [
                new("python:pkg.util", CodeNodeKinds.Namespace, (1, 6)),
                new("python:pkg.util.load", CodeNodeKinds.Member, (3, 3)),
                new("python:pkg.util.Store", CodeNodeKinds.Type, (5, 6)),
                new("python:pkg.util.Store.__init__", CodeNodeKinds.Member, (6, 6)),
            ],
            Edges =
            [
                new CodeEdge("file:pkg/util.pyi", "file:pkg/cart.py", CodeEdgeKinds.Imports, 1, CodeConfidences.Exact),
                new CodeEdge("python:pkg.util.load", "python:pkg.cart.Cart", CodeEdgeKinds.References, 3, CodeConfidences.Unique),
                new CodeEdge("python:pkg.util.Store.__init__", "python:pkg.cart.Cart", CodeEdgeKinds.References, 6, CodeConfidences.Unique),
            ],
        },
        new("app.pyw", Window)
        {
            Nodes =
            [
                new("python:app", CodeNodeKinds.Namespace, (1, 4)),
                new("python:app.cart", CodeNodeKinds.Member, (3, 3)),
            ],
            Edges =
            [
                new CodeEdge("file:app.pyw", "file:pkg/cart.py", CodeEdgeKinds.Imports, 1, CodeConfidences.Exact),
                new CodeEdge("python:app.cart", "python:pkg.cart.Cart", CodeEdgeKinds.Instantiates, 3, CodeConfidences.Unique),
            ],
        },
        new("tests/test_cart.py", Tests)
        {
            Nodes =
            [
                new("python:tests.test_cart", CodeNodeKinds.Namespace, (1, 40)),
                new("python:tests.test_cart.empty_cart", CodeNodeKinds.Member, (7, 9)),
                new("python:tests.test_cart.test_total_counts_every_line", CodeNodeKinds.Member, (12, 14)),
                new("python:tests.test_cart.helper", CodeNodeKinds.Member, (17, 18)),
                new("python:tests.test_cart.TestAdding", CodeNodeKinds.Type, (21, 24)),
                new("python:tests.test_cart.TestAdding.test_add_appends", CodeNodeKinds.Member, (22, 24)),
                new("python:tests.test_cart.CartCase", CodeNodeKinds.Type, (27, 32)),
                new("python:tests.test_cart.CartCase.test_parse", CodeNodeKinds.Member, (28, 29)),
                new("python:tests.test_cart.CartCase.helper", CodeNodeKinds.Member, (31, 32)),
                new("python:tests.test_cart.test_parse_sample", CodeNodeKinds.Member, (35, 36)),
                new("python:tests.test_cart.test_sees_no_helper", CodeNodeKinds.Member, (39, 40)),
            ],
            Edges =
            [
                new CodeEdge("file:tests/test_cart.py", "file:pkg/cart.py", CodeEdgeKinds.Imports, 3, CodeConfidences.Exact),
                new CodeEdge("file:tests/test_cart.py", "file:pkg/lines.py", CodeEdgeKinds.Imports, 4, CodeConfidences.Exact),
                new CodeEdge("python:tests.test_cart.test_total_counts_every_line", "python:pkg.lines.make_line", CodeEdgeKinds.Tests, 13, CodeConfidences.Unique),
                new CodeEdge("python:tests.test_cart.test_total_counts_every_line", "python:pkg.cart.total", CodeEdgeKinds.Tests, 14, CodeConfidences.Unique),
                new CodeEdge("python:tests.test_cart.empty_cart", "python:pkg.cart.Cart", CodeEdgeKinds.Instantiates, 9, CodeConfidences.Unique),
                new CodeEdge("python:tests.test_cart.TestAdding.test_add_appends", "python:pkg.lines.make_line", CodeEdgeKinds.Tests, 23, CodeConfidences.Unique),
                new CodeEdge("python:tests.test_cart.test_total_counts_every_line", "python:tests.test_cart.empty_cart", CodeEdgeKinds.References, 12, CodeConfidences.Exact),
                new CodeEdge("python:tests.test_cart.test_parse_sample", "python:tests.conftest.sample", CodeEdgeKinds.References, 35, CodeConfidences.Unique),
            ],
        },
        new("tests/conftest.py", Conftest)
        {
            Nodes =
            [
                new("python:tests.conftest", CodeNodeKinds.Namespace, (1, 7)),
                new("python:tests.conftest.sample", CodeNodeKinds.Member, (4, 7)),
            ],
        },
    ];

    /// <summary>
    /// The module is a namespace named by the file's path in dots - an <c>__init__.py</c> its directory's package, a stub's and a window
    /// script's its module's - one open scope, holding the file's lines; a function at its top a function, a method in a class a method and
    /// <c>__init__</c> a constructor; a name assigned at the top a constant where written in capitals and a field otherwise; each with its
    /// signature and the sentence its docstring gives.
    /// </summary>
    [Fact]
    public void AModule_IsANamespaceNamedByItsPath_AndItsDeclarationsAreItsMembers()
    {
        var cart = Read("pkg/cart.py", Cart);

        var module = Node(cart, "python:pkg.cart");
        Assert.Equal((CodeNodeKinds.Namespace, "module", "cart", "pkg.cart", 1, 60), (module.Kind, module.SubKind, module.Name, module.Qualified.Whole(), module.Start, module.End));
        Assert.Equal("A cart of lines.", module.Summary);
        Assert.Equal([new ExtractScope(QualifiedName.Of("pkg.cart"), true)], cart.Scopes);

        var init = Node(cart, "python:pkg.cart.Cart.__init__");
        Assert.Equal(("constructor", 1, null, "def __init__(self, owner: str, limit: int = MAX_LINES, *args, **kwargs) -> None"), (init.SubKind, init.MinArity, init.MaxArity, init.Signature));
        Assert.Equal(QualifiedName.Of("pkg.cart.Cart"), init.Scope);

        var count = Node(cart, "python:pkg.cart.Cart.count");
        Assert.Equal(("method", 0, 0), (count.SubKind, count.MinArity, count.MaxArity));
        Assert.Equal(["@property"], count.Modifiers);
        Assert.Equal(29, count.BodyStart);

        Assert.Equal(["async"], Node(cart, "python:pkg.cart.Cart.flush").Modifiers);
        Assert.Equal(["@staticmethod"], Node(cart, "python:pkg.cart.Cart.parse").Modifiers);

        var klass = Node(cart, "python:pkg.cart.Cart");
        Assert.Equal((17, 19), (klass.Start, klass.BodyStart));
        Assert.Equal(["@dataclass"], klass.Modifiers);

        // Each method asks of its type's bases by its name - an edge where one declares it, no doubt where none does.
        Assert.Equal(
            [("python:pkg.cart.Cart.__init__", "__init__", 22), ("python:pkg.cart.Cart.count", "count", 28), ("python:pkg.cart.Cart.add", "add", 31), ("python:pkg.cart.Cart.flush", "flush", 37), ("python:pkg.cart.Cart.parse", "parse", 41)],
            cart.References.Where(reference => reference.Kind == CodeEdgeKinds.Overrides).Select(reference => (reference.From, reference.Name, reference.Line)));
        Assert.All(cart.References.Where(reference => reference.Kind == CodeEdgeKinds.Overrides), reference => Assert.True(reference.Probe));
        Assert.All(cart.References.Where(reference => reference.Kind != CodeEdgeKinds.Overrides), reference => Assert.False(reference.Probe));

        Assert.Equal(("constant", "MAX_LINES: int ="), (Node(cart, "python:pkg.cart.MAX_LINES").SubKind, Node(cart, "python:pkg.cart.MAX_LINES").Signature));
        Assert.Equal(("field", 10), (Node(cart, "python:pkg.cart.default_tag").SubKind, Node(cart, "python:pkg.cart.default_tag").Start));

        // __init__.py is its directory's package; a stub and a window script are modules as a .py is.
        var package = Read("pkg/__init__.py", Package);
        Assert.Equal(("pkg", "pkg"), (Node(package, "python:pkg").Name, Node(package, "python:pkg").Qualified.Whole()));
        Assert.Equal("pkg.util", Node(Read("pkg/util.pyi", Stub), "python:pkg.util").Qualified.Whole());
        Assert.Equal("app", Node(Read("app.pyw", Window), "python:app").Qualified.Whole());
    }

    /// <summary>
    /// Each call a body makes is read by its name and arity with what its receiver is: <c>self.x</c> the type's own, <c>super().__init__()</c>
    /// the base's, a module's name an import's prefix, any longer chain or other receiver a value; a class a call makes where the code
    /// writes a capitalized name; a name a keyword argument gives, a local the body binds and a builtin none of them a use.
    /// </summary>
    [Fact]
    public void EachCall_IsReadByItsNameAndArity_WithItsReceiver()
    {
        var cart = Read("pkg/cart.py", Cart);
        var init = "python:pkg.cart.Cart.__init__";
        var add = "python:pkg.cart.Cart.add";
        var flush = "python:pkg.cart.Cart.flush";
        var describe = "python:pkg.cart.describe";
        var module = "python:pkg.cart";

        Assert.Equal(
            [
                ("__init__", CodeEdgeKinds.Overrides, 22, CodeQualifierKinds.Base),
                ("MAX_LINES", CodeEdgeKinds.Uses, 22, (string?)null), ("owner", CodeEdgeKinds.Uses, 23, CodeQualifierKinds.This), ("limit", CodeEdgeKinds.Uses, 24, CodeQualifierKinds.This),
                ("__init__", CodeEdgeKinds.Calls, 25, CodeQualifierKinds.Base),
            ],
            References(cart, init));

        // add: its own override reference first - a method is bound by its name alone - then its parameters' annotations, then its
        // body: self's members, a class it makes, a member it reaches through self.lines.
        Assert.Equal(
            [
                ("add", CodeEdgeKinds.Overrides, 31, CodeQualifierKinds.Base),
                ("CartError", CodeEdgeKinds.References, 31, (string?)null), ("Line", CodeEdgeKinds.References, 31, (string?)null),
                ("count", CodeEdgeKinds.Uses, 32, CodeQualifierKinds.This), ("limit", CodeEdgeKinds.Uses, 32, CodeQualifierKinds.This),
                ("CartError", CodeEdgeKinds.Instantiates, 33, (string?)null), ("append", CodeEdgeKinds.Calls, 34, CodeQualifierKinds.Value),
            ],
            References(cart, add));
        Assert.Equal([1], cart.References.Where(reference => reference.From == add && reference.Name == "append").Select(reference => reference.Arity));

        // Each method an override reference of its own name among its type's bases, on its line, before its body's code.
        Assert.Equal([("flush", CodeEdgeKinds.Overrides, 37, CodeQualifierKinds.Base), ("save", CodeEdgeKinds.Calls, 38, CodeQualifierKinds.This)], References(cart, flush));

        // describe: a module's name is the import's prefix; an f-string's fields are code; a parameter's default is the function's, a
        // function an import renames reached by the name the code gives it; a local the body binds (name) and a keyword argument's
        // name (owner) are no use.
        Assert.Equal(
            [
                ("Cart", CodeEdgeKinds.References, 54, (string?)null), ("money", CodeEdgeKinds.Uses, 54, CodeQualifierKinds.Prefix),
                ("basename", CodeEdgeKinds.Calls, 55, CodeQualifierKinds.Prefix),
                ("count", CodeEdgeKinds.Uses, 56, CodeQualifierKinds.Value), ("total", CodeEdgeKinds.Calls, 56, (string?)null),
            ],
            References(cart, describe));
        Assert.Equal([1], cart.References.Where(reference => reference.From == describe && reference.Name == "total").Select(reference => reference.Arity));

        // The top's own code: the module's.
        Assert.Equal(
            [("describe", CodeEdgeKinds.Calls, 60, (string?)null), ("Cart", CodeEdgeKinds.Instantiates, 60, (string?)null)],
            References(cart, module));

        // A type an annotation names - list[Line], a return annotation, one written as a string - in the order the file writes them.
        Assert.Equal(
            [("Line", 19), ("CartError", 31), ("Line", 31), ("Cart", 41), ("Cart", 47), ("Cart", 54)],
            cart.References.Where(reference => reference.Kind == CodeEdgeKinds.References && reference.Name is "Line" or "CartError" or "Cart").Select(reference => (reference.Name, reference.Line)));

        // A builtin an isinstance names, a local the body binds, and the name of a keyword argument: none is read.
        Assert.DoesNotContain(cart.References, reference => reference.Name is "str" or "name");
        Assert.DoesNotContain(References(cart, module), reference => reference.Name == "owner");

        // A constant a field's value reads and a constant a default reads: uses of the module's names, in the order the file writes them.
        Assert.Equal([("default_tag", 20, "python:pkg.cart.Cart.tag"), ("MAX_LINES", 22, "python:pkg.cart.Cart.__init__")], cart.References.Where(reference => reference.Kind == CodeEdgeKinds.Uses && reference.Name is "MAX_LINES" or "default_tag").Select(reference => (reference.Name, reference.Line, reference.From)));
    }

    /// <summary>
    /// Each import binds the names the code reaches things by: <c>import os.path as osp</c> binds <c>osp</c>; <c>from .lines import Line,
    /// make_line</c> shows both, and what a <c>from</c> binds is the file's importers's too; <c>from ..shared import money as formatted</c>
    /// binds <c>formatted</c> to <c>money</c> by a path that climbs; each name shown is looked for as a module of its own as well; and
    /// <c>from m import *</c> takes the names the module publishes alone. A module by a bare name is tried in the file's own directory and
    /// in each above it to the tree's root, the nearest first, every place one of a layout the linker passes over inside a package; under
    /// each, a package's <c>__init__.py</c> stands first and the module's <c>.py</c> in for it, each place standing in for the one before it.
    /// </summary>
    [Fact]
    public void EachImport_BindsItsNames_AndIsLookedForWhereThePackageLayoutHoldsIt()
    {
        var cart = Read("pkg/cart.py", Cart);

        Assert.All(cart.Imports, import => Assert.Equal(CodeImportForms.Relative, import.Form));
        Assert.Equal(
            [
                (4, "__future__/__init__.py", "../__future__.py", (string?)null, "annotations", true, false),
                (4, "__future__/annotations/__init__.py", "../__future__/annotations.py", "annotations", (string?)null, false, false),
                (4, "os/path/__init__.py", "../os/path.py", "osp", (string?)null, false, false),
                (4, "dataclasses/__init__.py", "../dataclasses.py", (string?)null, "dataclass,field", true, false),
                (4, "dataclasses/dataclass/__init__.py", "../dataclasses/dataclass.py", "dataclass", (string?)null, false, false),
                (4, "dataclasses/field/__init__.py", "../dataclasses/field.py", "field", (string?)null, false, false),
                (2, "base/__init__.py", "base.py", (string?)null, "BaseCart,Ordered", true, false),
                (2, "base/BaseCart/__init__.py", "base/BaseCart.py", "BaseCart", (string?)null, false, false),
                (2, "base/Ordered/__init__.py", "base/Ordered.py", "Ordered", (string?)null, false, false),
                (2, "lines/__init__.py", "lines.py", (string?)null, "Line,make_line", true, false),
                (2, "lines/Line/__init__.py", "lines/Line.py", "Line", (string?)null, false, false),
                (2, "lines/make_line/__init__.py", "lines/make_line.py", "make_line", (string?)null, false, false),
                (2, "../shared/__init__.py", "../shared.py", "formatted", "money", true, false),
                (2, "../shared/money/__init__.py", "../shared/money.py", "formatted", (string?)null, false, false),
            ],
            Runs(cart));

        // The first six runs are of bare names, each place one of a layout; the rest are relative, and none of them is.
        Assert.All(cart.Imports.Take(24), import => Assert.True(import.Layout));
        Assert.All(cart.Imports.Skip(24), import => Assert.False(import.Layout));

        // A file at the tree's root has the one directory to look in.
        Assert.Equal([(2, "pkg/cart/__init__.py", "pkg/cart.py", (string?)null, "Cart", true, false), (2, "pkg/cart/Cart/__init__.py", "pkg/cart/Cart.py", "Cart", (string?)null, false, false)], Runs(Read("app.pyw", Window)));

        // from m import *: the module's published names alone.
        var star = Read("pkg/stars.py", "from .cart import *\n");
        Assert.Equal([(2, "cart/__init__.py", "cart.py", (string?)null, (string?)null, false, true)], Runs(star));
    }

    /// <summary>
    /// A bare import is looked for from above the file's package, as Python searches a path: a module of the same name inside the
    /// package is passed over, and the directory above the package's top is tried first, then each above it; a file in no package
    /// looks in its own directory first, as a script's path does.
    /// </summary>
    [Fact]
    public void ABareImport_IsLookedForFromAboveThePackagesAroundTheFile()
    {
        var linked = Link(
            ("pkg/__init__.py", Read("pkg/__init__.py", "\"\"\"A package.\"\"\"\n")),
            ("pkg/sub/__init__.py", Read("pkg/sub/__init__.py", "\"\"\"A package in it.\"\"\"\n")),
            ("pkg/sub/helper.py", Read("pkg/sub/helper.py", "def pack(): ...\n")),
            ("pkg/sub/mod.py", Read("pkg/sub/mod.py", "import helper\n")),
            ("helper.py", Read("helper.py", "def pack(): ...\n")),
            ("pkg/top.py", Read("pkg/top.py", "from helper import pack\n")),
            ("tools/run.py", Read("tools/run.py", "import helper\n")),
            ("tools/helper.py", Read("tools/helper.py", "def pack(): ...\n")));

        // Each package's __init__.py says its directory is a unit of the layout.
        Assert.All(new[] { "pkg/__init__.py", "pkg/sub/__init__.py" }, path => Assert.True(Read(path, "\"\"\"A package.\"\"\"\n").FolderUnit));
        Assert.False(Read("pkg/sub/mod.py", "import helper\n").FolderUnit);

        // In a package two deep: the package's own directories are passed over, the tree's root is the first tried.
        Assert.Contains(new CodeEdge("file:pkg/sub/mod.py", "file:helper.py", CodeEdgeKinds.Imports, 1, CodeConfidences.Exact), linked.Edges);
        Assert.DoesNotContain(linked.Edges, edge => edge.Kind == CodeEdgeKinds.Imports && edge.From == "file:pkg/sub/mod.py" && edge.To != "file:helper.py");

        // In the package's top: likewise.
        Assert.Contains(new CodeEdge("file:pkg/top.py", "file:helper.py", CodeEdgeKinds.Imports, 1, CodeConfidences.Exact), linked.Edges);

        // In no package: the file's own directory is the first tried, as a script's is.
        Assert.Contains(new CodeEdge("file:tools/run.py", "file:tools/helper.py", CodeEdgeKinds.Imports, 1, CodeConfidences.Exact), linked.Edges);
    }

    /// <summary>
    /// The file's imports as runs - an import and those standing in for it, in the order they are written: how many places are tried,
    /// the first and the last, the name the run binds its module by, the names it shows, whether what it binds is the file's
    /// importers's too, and whether it takes the published names alone.
    /// </summary>
    private static List<(int Places, string First, string Last, string? Prefix, string? Show, bool Reexports, bool Published)> Runs(FileExtract extract)
    {
        var runs = new List<(int Places, string First, string Last, string? Prefix, string? Show, bool Reexports, bool Published)>();

        foreach (var import in extract.Imports)
        {
            var shown = import.Show is null ? null : string.Join(',', import.Show);

            if (!import.Fallback || runs.Count == 0)
            {
                runs.Add((1, import.Target, import.Target, import.Prefix, shown, import.Reexports, import.Published));
            }
            else
            {
                var last = runs[^1];
                runs[^1] = (last.Places + 1, last.First, import.Target, last.Prefix, last.Show, last.Reexports, last.Published);
            }
        }

        return runs;
    }
    /// <summary>
    /// What the reader cannot bind is marked dynamic with why: an attribute reached by <c>getattr</c>, a module named by
    /// <c>importlib.import_module</c>, code a string holds in <c>exec</c>, and a call through a value a body binds; a call on a parameter
    /// is bound by its name alone, never marked.
    /// </summary>
    [Fact]
    public void WhatTheReaderCannotBind_IsMarkedDynamicWithWhy()
    {
        var extract = Read("pkg/dynamic.py", """
            import importlib
            from pkg.cart import Cart


            def reach(obj, name, make):
                getattr(obj, name)
                importlib.import_module("pkg.lines")
                exec("x = 1")
                make(name)
                obj.add(name)
                return Cart


            def make(x):
                return x


            def use():
                return make(1)
            """);

        Assert.Equal(
            [
                (6, "an attribute reached by getattr: a name the code computes"),
                (7, "a dynamic import: a module the code names by a computed name"),
                (8, "a call of eval or exec: code a string holds"),
                (9, "a call through the function value make"),
            ],
            extract.Dynamic.Select(mark => (mark.Line, mark.Why)));

        // Each mark is of the kind that tells the limit of the help it shows, so an answer says of the file that limit and no other.
        Assert.Equal(
            [PythonReading.ComputedAttribute, PythonReading.DynamicImport, PythonReading.ComputedCode, PythonReading.FunctionValue],
            extract.Dynamic.Select(mark => mark.Kind));
        Assert.Equal([PythonReading.ComputedExports], Read("pkg/all.py", "__all__ = [\"Cart\"]\n__all__.append(\"Line\")\n").Dynamic.Select(mark => mark.Kind));
        Assert.Empty(Read("pkg/all.py", "__all__ = [\"Cart\", \"Line\"]\n").Dynamic);
        Assert.Equal([("add", CodeEdgeKinds.Calls, 10, CodeQualifierKinds.Value)], References(extract, "python:pkg.dynamic.reach").Where(reference => reference.Kind == CodeEdgeKinds.Calls));
        Assert.Equal([("Cart", CodeEdgeKinds.References, 11, (string?)null)], References(extract, "python:pkg.dynamic.reach").Where(reference => reference.Name == "Cart"));
        Assert.Equal([("make", CodeEdgeKinds.Calls, 19, (string?)null)], References(extract, "python:pkg.dynamic.use"));
    }

    /// <summary>
    /// A test file's <c>test*</c> functions, its <c>Test*</c> classes and its <c>TestCase</c> subclasses with their <c>test*</c> methods are tests,
    /// each parameter a test requires a reference of the test naming the fixture that provides it - the test file's own, or one a
    /// <c>conftest.py</c> beside it declares, which no test imports - and a helper, or a method of a test class not named <c>test*</c>, is
    /// none; a function pytest's <c>@fixture</c> decorates is a fixture, never a test.
    /// </summary>
    [Fact]
    public void ATestFilesTests_AreItsTestFunctions_TestClassesAndTestCases_AndTheirFixturesAreReferences()
    {
        var extract = Read("tests/test_cart.py", Tests);
        var test = "python:tests.test_cart.test_total_counts_every_line";

        Assert.Equal(
            [
                test, "python:tests.test_cart.TestAdding", "python:tests.test_cart.TestAdding.test_add_appends",
                "python:tests.test_cart.CartCase", "python:tests.test_cart.CartCase.test_parse", "python:tests.test_cart.test_parse_sample",
                "python:tests.test_cart.test_sees_no_helper",
            ],
            extract.Nodes.Where(node => node.Test).Select(node => node.Id));
        Assert.Equal([("empty_cart", CodeEdgeKinds.References, 12, CodeQualifierKinds.Fixture)], References(extract, test).Where(reference => reference.Kind == CodeEdgeKinds.References));
        Assert.Equal([("add", CodeEdgeKinds.Calls, 13, CodeQualifierKinds.Value), ("make_line", CodeEdgeKinds.Calls, 13, (string?)null), ("total", CodeEdgeKinds.Calls, 14, (string?)null)], References(extract, test).Where(reference => reference.Kind == CodeEdgeKinds.Calls));

        // The language's own decorators are modifiers by their dotted name, their arguments the node's code alone; a fixture is a function
        // of its own kind within its kind, and never a test though a test file declares it.
        var own = Node(extract, "python:tests.test_cart.empty_cart");

        Assert.Equal(["@pytest.fixture"], own.Modifiers);
        Assert.Equal(CodeSubKinds.Fixture, own.SubKind);
        Assert.False(own.Test);
        Assert.DoesNotContain(extract.References, reference => reference.Name is "pytest" or "fixture");

        // A fixture the test file imports from nowhere: linked to the conftest.py's, by its name alone.
        var conftest = Read("tests/conftest.py", Conftest);

        Assert.Equal((CodeSubKinds.Fixture, false), (Node(conftest, "python:tests.conftest.sample").SubKind, Node(conftest, "python:tests.conftest.sample").Test));
        Assert.Contains(new CodeEdge("python:tests.test_cart.test_parse_sample", "python:tests.conftest.sample", CodeEdgeKinds.References, 35, CodeConfidences.Unique), Link(("tests/test_cart.py", extract), ("tests/conftest.py", conftest)).Edges);

        // Not a test file: nothing in it is a test.
        Assert.DoesNotContain(Read("pkg/cart.py", Cart).Nodes, node => node.Test);
    }

    /// <summary>
    /// Nested classes and functions are kept with the node that holds them, named under it; a class attribute's value is the attribute's
    /// code; a method's body is the method's own, read by no run of the class around it.
    /// </summary>
    [Fact]
    public void NestedDeclarations_AreKeptWithTheNodeThatHoldsThem()
    {
        var extract = Read("pkg/nested.py", """
            class Outer:
                scale = 2

                def make(self):
                    def inner(x):
                        return helper(x)
                    return inner

                class Inner:
                    def go(self):
                        return Outer().make()


            def helper(x):
                return x
            """);

        Assert.Equal(
            [
                "python:pkg.nested", "python:pkg.nested.Outer", "python:pkg.nested.Outer.scale", "python:pkg.nested.Outer.make", "python:pkg.nested.Outer.make.inner",
                "python:pkg.nested.Outer.Inner", "python:pkg.nested.Outer.Inner.go", "python:pkg.nested.helper",
            ],
            extract.Nodes.Select(node => node.Id));
        // A nested declaration is held by the node it is in and declared in the scope that node is declared in itself - a function is
        // no scope, so the function's own class here, as a class nested in a class is in its class's own scope.
        Assert.Equal((QualifiedName.Of("pkg.nested.Outer"), QualifiedName.Of("python:pkg.nested.Outer.make")), (Node(extract, "python:pkg.nested.Outer.make.inner").Scope, Node(extract, "python:pkg.nested.Outer.make.inner").Parent));
        Assert.Equal((QualifiedName.Of("pkg.nested.Outer"), QualifiedName.Of("python:pkg.nested.Outer")), (Node(extract, "python:pkg.nested.Outer.Inner").Scope, Node(extract, "python:pkg.nested.Outer.Inner").Parent));
        Assert.Equal(("field", 2, 2), (Node(extract, "python:pkg.nested.Outer.scale").SubKind, Node(extract, "python:pkg.nested.Outer.scale").Start, Node(extract, "python:pkg.nested.Outer.scale").End));
        Assert.Equal([("helper", CodeEdgeKinds.Calls, 6, (string?)null)], References(extract, "python:pkg.nested.Outer.make.inner").Where(reference => reference.Kind == CodeEdgeKinds.Calls));
        Assert.Equal([("go", CodeEdgeKinds.Overrides, 10, CodeQualifierKinds.Base), ("Outer", CodeEdgeKinds.Instantiates, 11, (string?)null), ("make", CodeEdgeKinds.Calls, 11, CodeQualifierKinds.Value)], References(extract, "python:pkg.nested.Outer.Inner.go"));
        Assert.DoesNotContain(References(extract, "python:pkg.nested.Outer.make"), reference => reference.Kind == CodeEdgeKinds.Calls);

        // A name a declaration nested any depth reads is looked for from its scope outward - the class, its bases, the module - never
        // in a declaration of another module the file does not import.
        var linked = Link(("pkg/nested.py", extract));
        Assert.Contains(new CodeEdge("python:pkg.nested.Outer.make.inner", "python:pkg.nested.helper", CodeEdgeKinds.Calls, 6, CodeConfidences.Exact), linked.Edges);
        Assert.Contains(new CodeEdge("python:pkg.nested.Outer.Inner.go", "python:pkg.nested.Outer", CodeEdgeKinds.Instantiates, 11, CodeConfidences.Exact), linked.Edges);
        Assert.Contains(new CodeEdge("python:pkg.nested.Outer.Inner.go", "python:pkg.nested.Outer.make", CodeEdgeKinds.Calls, 11, CodeConfidences.Unique), linked.Edges);
    }

    /// <summary>
    /// Blocks are made by indentation and read whole however deep: a loop's body, a one-line suite, a backslash continuation and lines
    /// inside brackets that begin at a block's column each end and begin what Python says they do; the names a block binds - a loop's
    /// target, one an <c>as</c> gives - are the body's own, read as no use.
    /// </summary>
    [Fact]
    public void BlocksAreMadeByIndentation_AndReadWholeHoweverDeep()
    {
        var extract = Read("pkg/blocks.py", """
            def outer(items):
                total = 0
                for item in \
                        items:
                    total += item
                if total: return total
                with closing(open("x")) as handle:
                    pass
                try:
                    total = int(handle.read())
                except GateError as error:
                    raise error
                return total


            class Gate:
                def hold(self, lines=[
                    1,
                    2,
                ]):
                    return lines
            """);

        var outer = Node(extract, "python:pkg.blocks.outer");
        var id = outer.Id.Whole();
        Assert.Equal((1, 13), (outer.Start, outer.End));

        // A with's header is the body's code: its call is the function's; a builtin - open, int - names nothing.
        Assert.Equal([("closing", CodeEdgeKinds.Calls, 7, (string?)null), ("read", CodeEdgeKinds.Calls, 10, CodeQualifierKinds.Value)], References(extract, id).Where(reference => reference.Kind == CodeEdgeKinds.Calls));
        Assert.Equal([("GateError", CodeEdgeKinds.References, 11, (string?)null)], References(extract, id).Where(reference => reference.Kind == CodeEdgeKinds.References));

        // A backslash continuation: the for's header runs to the items on the next line, its targets the body's own; a builtin, a
        // parameter, a local, a loop's target and a name an as gives are none of them a use.
        Assert.DoesNotContain(References(extract, id), reference => reference.Kind == CodeEdgeKinds.Uses);
        Assert.DoesNotContain(extract.References, reference => reference.Name is "items" or "item" or "total" or "handle" or "error" or "int" or "open" or "pass");

        // A parameter with a default may be left out: no argument is required, one may be given.
        var hold = Node(extract, "python:pkg.blocks.Gate.hold");
        Assert.Equal((17, 21), (hold.Start, hold.End));
        Assert.Equal((0, 1), (hold.MinArity, hold.MaxArity));
    }

    /// <summary>
    /// Each string literal the file holds is kept once by its value as Python reads its escapes, with its line and the node whose code holds
    /// it: a triple-quoted one's, a raw one's, a bytes one's, an f-string's constant parts each one of its own, its fields read as code
    /// where they stand, a conversion and a format its own.
    /// </summary>
    [Fact]
    public void EachStringLiteral_IsKeptByItsValue_AnFStringsPartsEachItsOwn_AndItsFieldsReadAsCode()
    {
        var extract = Read("pkg/strings.py", """"
            TITLE = "The\tcart"
            RAW = r"C:\tmp\cart"
            BYTES = b"cart"
            WIDE = """a cart
            over lines"""
            FIELDS = f"total {money(cents)!r:>10} and {NAME}"


            def money(cents):
                return cents
            """");

        Assert.Equal(["The\tcart", "C:\\tmp\\cart", "cart", "a cart\nover lines", "total ", " and "], (extract.Literals ?? []).Select(literal => literal.Value));
        Assert.Equal(
            [
                ("python:pkg.strings.TITLE", 1), ("python:pkg.strings.RAW", 2), ("python:pkg.strings.BYTES", 3), ("python:pkg.strings.WIDE", 4),
                ("python:pkg.strings.FIELDS", 6), ("python:pkg.strings.FIELDS", 6),
            ],
            (extract.Literals ?? []).Select(literal => (literal.Places[0].Node?.Whole(), literal.Places[0].Line)));
        Assert.Equal([("money", CodeEdgeKinds.Calls, 6, (string?)null)], References(extract, "python:pkg.strings.FIELDS").Where(reference => reference.Kind == CodeEdgeKinds.Calls));
        Assert.Equal([("cents", CodeEdgeKinds.Uses, 6, (string?)null), ("NAME", CodeEdgeKinds.Uses, 6, (string?)null)], References(extract, "python:pkg.strings.FIELDS").Where(reference => reference.Kind == CodeEdgeKinds.Uses));
    }

    /// <summary>
    /// Each lexical rule of a string is read as Python reads it, one row each: what the string's value is - the constant parts kept,
    /// each decoded by its prefix's escapes - and the names its fields read as code. A prefix in any case and of two letters, <c>u</c>
    /// and <c>t</c> among them; three single quotes; doubled braces a brace; a field's code to its closer through <c>!=</c>, through a
    /// colon a group or a dictionary holds and through a nested string, and to a debugging <c>=</c>, a conversion or a format, whose
    /// own fields are code; a backslash before a field's brace, and <c>\N{...}</c> no field; Python's escapes - a character's name kept
    /// as written, a code past every character's the replacement character, a line break a backslash joins dropped, one Python does not
    /// know kept with its backslash, a bytes string's its own - and a raw string's escaped quote, which closes nothing.
    /// </summary>
    [Theory]
    [MemberData(nameof(LexedStrings))]
    public void EachLexicalRuleOfAString_IsReadAsPythonReadsIt(string source, string[] values, string[] references)
    {
        var extract = Read("pkg/lex.py", source);

        Assert.Equal(values, (extract.Literals ?? []).Select(literal => literal.Value));
        Assert.Equal(references, References(extract, "python:pkg.lex.X").Select(reference => $"{reference.Kind} {reference.Name}"));

        // Every string of a row closes, so no line is marked as one that never does.
        Assert.Empty(extract.Dynamic);
    }

    /// <summary>The rows of <see cref="EachLexicalRuleOfAString_IsReadAsPythonReadsIt"/>: a module's source, its literals' values, and what its constant's value reads.</summary>
    public static TheoryData<string, string[], string[]> LexedStrings() => new()
    {
        { """X = U"cart" """, ["cart"], [] },
        { """X = rb"\n" """, ["\\n"], [] },
        { """X = Rb'\t' """, ["\\t"], [] },
        { """X = BR"\d" """, ["\\d"], [] },
        { """X = Fr"{NAME}\d" """, ["\\d"], ["uses NAME"] },
        { """X = '''a cart's''' """, ["a cart's"], [] },
        { """X = f"{{id}}: {NAME}" """, ["{id}: "], ["uses NAME"] },
        { """X = f"{A != B}" """, [], ["uses A", "uses B"] },
        { """X = f"{ITEMS[1:N]}" """, [], ["uses ITEMS", "uses N"] },
        { """X = f"{ {'k': V}['k'] }" """, ["k"], ["uses V"] },
        { """X = f"{D["key"]}" """, ["key"], ["uses D"] },
        { """X = f"{NAME + rb'x\n'}" """, ["x\\n"], ["uses NAME"] },
        { """X = f"{V:.{DIGITS}f}" """, [], ["uses V", "uses DIGITS"] },
        { """X = f"{V:{W}.{P}}" """, [], ["uses V", "uses W", "uses P"] },
        { """X = f"{RETRIES=}" """, [], ["uses RETRIES"] },
        { """X = f"{TOTAL = }" """, [], ["uses TOTAL"] },
        { """X = f"{A + B=!r:>{W}}" """, [], ["uses A", "uses B", "uses W"] },
        { """X = t"Hello {USER}" """, ["Hello "], ["uses USER"] },
        { """X = Rt"\d{USER}" """, ["\\d"], ["uses USER"] },
        { """X = f"\N{EM DASH} {TITLE}" """, ["\\N{EM DASH} "], ["uses TITLE"] },
        { """X = f"\{NAME}" """, ["\\"], ["uses NAME"] },
        { """X = "\x41\u0042\U00000043\101\N{EM DASH}" """, ["ABCA\\N{EM DASH}"], [] },
        { """X = "\U00110000" """, ["\uFFFD"], [] },
        { "X = \"ab\\\ncd\"\n", ["abcd"], [] },
        { """X = "\d+\." """, ["\\d+\\."], [] },
        { """X = "\\ \' \"" """, ["\\ ' \""], [] },
        { """X = b"\u0041\N{DASH}" """, ["\\u0041\\N{DASH}"], [] },
        { """X = b"\xc3\xa9" """, ["\u00e9"], [] },
        { """X = r'\'' """, ["\\'"], [] },
        { """X = r"\"" """, ["\\\""], [] },
        { """X = r'[\'"]' """, ["[\\'\"]"], [] },
        { """X = rf"\"{NAME}" """, ["\\\""], ["uses NAME"] },
    };

    /// <summary>
    /// A raw string's escaped quote closes nothing, as Python's tokenizer reads a raw string - the backslash kept in its value - so the
    /// brackets around it close where the code closes them, and every statement after it is read as its own: the functions and the
    /// class past a pattern that holds a quote are declared, nothing is marked.
    /// </summary>
    [Fact]
    public void ARawStringsEscapedQuote_ClosesNothing_AndTheStatementsAfterItAreRead()
    {
        var extract = Read("pkg/raw.py", """
            import re
            QUOTE = re.compile(r'[\'"]')
            APOSTROPHE = r'\''
            DOUBLE = r"\""


            def strip(text):
                return QUOTE.sub("", text)


            class Parser:
                pass
            """);

        Assert.Equal(
            [("python:pkg.raw", 1, 12), ("python:pkg.raw.QUOTE", 2, 2), ("python:pkg.raw.APOSTROPHE", 3, 3), ("python:pkg.raw.DOUBLE", 4, 4), ("python:pkg.raw.strip", 7, 8), ("python:pkg.raw.Parser", 11, 12)],
            extract.Nodes.Select(node => (node.Id.Whole(), node.Start, node.End)));
        Assert.Equal(["[\\'\"]", "\\'", "\\\""], (extract.Literals ?? []).Select(literal => literal.Value));
        Assert.Empty(extract.Dynamic);
    }

    /// <summary>
    /// A bracket or a string that never closes - text Python refuses, which a file being edited may hold - marks the line it opens on
    /// dynamic, of the kind that tells its limit, since what follows it is read as held in it: a call left open, a string of one line
    /// left open at its end and a triple-quoted one left open to the file's; a file whose brackets and strings all close is marked
    /// nowhere.
    /// </summary>
    [Fact]
    public void ABracketOrAStringThatNeverCloses_MarksTheLineItOpensOn()
    {
        var bracket = Read("pkg/edited.py", """
            import re


            def broken(text):
                return re.sub("x", "", text


            class After:
                pass
            """);
        var strings = Read("pkg/strings.py", """"
            NAME = 'cart
            LIMIT = """open


            def after():
                pass
            """");

        Assert.Equal([(5, "a bracket that never closes: what follows it is read as held in it")], bracket.Dynamic.Select(mark => (mark.Line, mark.Why)));
        Assert.Equal(
            [(1, "a string that never closes: what follows its quote is read as its text"), (2, "a string that never closes: what follows its quote is read as its text")],
            strings.Dynamic.Select(mark => (mark.Line, mark.Why)));
        Assert.All(bracket.Dynamic.Concat(strings.Dynamic), mark => Assert.Equal(PythonReading.Unclosed, mark.Kind));
        Assert.Empty(Read("pkg/whole.py", "def f(x):\n    return (x, [1, {2: 3}], f\"{x}\", '''a\nb''')\n").Dynamic);

        // The kind is the sign of the one limit of the help that says it, so an answer that searched such a file says that limit.
        Assert.StartsWith("a bracket or a string that never closes", Assert.Single(new PythonExtractor().Help.DoesNotSee, limit => limit.Signs.Contains(PythonReading.Unclosed)).Says, StringComparison.Ordinal);
    }

    /// <summary>
    /// A line is continued by a backslash that ends its code alone: one a trailing comment ends with is the comment's, and a line a
    /// backslash joins to a comment ends with that comment, so the class on the next line is a statement of its own in each.
    /// </summary>
    [Fact]
    public void ABackslashAtACommentsEnd_ContinuesNoLine()
    {
        var extract = Read("pkg/config.py", """
            TIMEOUT = 30  # see C:\
            class Client:
                pass


            RETRIES = 3 \
            # the backslash above joins this comment to its line
            class Server:
                pass
            """);

        Assert.Equal(
            [("python:pkg.config", 1, 9), ("python:pkg.config.TIMEOUT", 1, 1), ("python:pkg.config.Client", 2, 3), ("python:pkg.config.RETRIES", 6, 6), ("python:pkg.config.Server", 8, 9)],
            extract.Nodes.Select(node => (node.Id.Whole(), node.Start, node.End)));
    }

    /// <summary>
    /// A form feed among the blanks a line begins with begins its column again, as Python's tokenizer counts it - a page break written
    /// before a declaration - so the declaration stands at the module's column and the declarations after it are read.
    /// </summary>
    [Fact]
    public void AFormFeedALineBeginsWith_BeginsItsColumnAgain()
    {
        var extract = Read("pkg/paged.py", "\fdef helper():\n    return 1\n\n\ndef after():\n    return 2\n");

        Assert.Equal(
            [("python:pkg.paged", 1, 6), ("python:pkg.paged.helper", 1, 2), ("python:pkg.paged.after", 5, 6)],
            extract.Nodes.Select(node => (node.Id.Whole(), node.Start, node.End)));
    }

    /// <summary>
    /// A Python module's own imported names are its importers's: <c>from pkg import Cart</c> reaches what a package's <c>__init__.py</c>
    /// imports, <c>from pkg import CartLine</c> what it imports under that name (<c>from .lines import Line as CartLine</c>), each the
    /// one declaration, and the imports edges go to the package's file. A name no file declares is external, counted and no edge.
    /// </summary>
    [Fact]
    public void APackageReexportsWhatItImports_UnderTheNameItGivesIt()
    {
        var linked = Link(
            ("pkg/__init__.py", Read("pkg/__init__.py", """
                from .cart import Cart
                from .lines import Line as CartLine
                """)),
            ("pkg/cart.py", Read("pkg/cart.py", """
                from .lines import Line


                class Cart:
                    def __init__(self, lines: list[Line]) -> None:
                        self.lines = lines
                """)),
            ("pkg/lines.py", Read("pkg/lines.py", """
                class Line:
                    name: str
                """)),
            ("app.py", Read("app.py", """
                from pkg import Cart, CartLine

                first = Cart([CartLine()])
                second = CartLine
                """)));

        Assert.Contains(new CodeEdge("python:app.first", "python:pkg.cart.Cart", CodeEdgeKinds.Instantiates, 3, CodeConfidences.Unique), linked.Edges);
        Assert.Contains(new CodeEdge("python:app.first", "python:pkg.lines.Line", CodeEdgeKinds.Instantiates, 3, CodeConfidences.Unique), linked.Edges);
        Assert.Contains(new CodeEdge("python:app.second", "python:pkg.lines.Line", CodeEdgeKinds.References, 4, CodeConfidences.Unique), linked.Edges);
        Assert.Contains(new CodeEdge("file:app.py", "file:pkg/__init__.py", CodeEdgeKinds.Imports, 1, CodeConfidences.Exact), linked.Edges);
        Assert.DoesNotContain(linked.Edges, edge => edge.Kind == CodeEdgeKinds.Imports && edge.From == "file:app.py" && edge.To != "file:pkg/__init__.py");
    }

    /// <summary>
    /// <c>from m import *</c> binds the names <c>m</c>'s <c>__all__</c> names, where it is a list of string literals alone - a name
    /// it only imports among them, a public one it leaves out not; where the module declares no <c>__all__</c>, or computes it, the
    /// names not written with a leading underscore. A name nothing binds is bound by nothing: the repository's declarations are not
    /// looked through for it, as a name a Python file does not declare or import is a name it has not.
    /// </summary>
    [Fact]
    public void AStarImportBindsTheNamesAllNames_ElseThePublicOnes()
    {
        const string lines = "class Line:\n    name: str\n";
        const string body = """

            class Cart:
                def __init__(self, lines: list[Line]) -> None:
                    self.lines = lines

            def _pack(cart: Cart) -> Cart: ...

            def unpacked(cart: Cart) -> Cart: ...
            """;
        const string imports = "from .lines import Line\n";
        const string app = """
            from pkg.cart import *

            packed = _pack(Cart([Line()]))
            loose = unpacked(Cart([Line()]))
            made = Cart([Line()])
            """;

        IReadOnlyList<CodeEdge> Edges(params (string Path, FileExtract Extract)[] files)
            => [.. CodeGraphLinker.Link([.. files.Select(file => new CodeGraphFile(file.Path, "python", file.Extract))]).Edges
                .Where(edge => edge.From.StartsWith("python:app.", StringComparison.Ordinal) && edge.Kind is CodeEdgeKinds.Instantiates or CodeEdgeKinds.Calls)];

        // __all__ names Cart and Line: unpacked is public and left out, _pack private; Line is only imported, and reached; the two
        // left out bind nothing.
        var named = Edges(
            ("pkg/cart.py", Read("pkg/cart.py", imports + "\n__all__ = [\"Cart\", \"Line\"]\n" + body)),
            ("pkg/lines.py", Read("pkg/lines.py", lines)),
            ("app.py", Read("app.py", app)));

        Assert.Equal(
            [
                ("python:app.packed", "instantiates", "Cart"), ("python:app.packed", "instantiates", "Line"),
                ("python:app.loose", "instantiates", "Cart"), ("python:app.loose", "instantiates", "Line"),
                ("python:app.made", "instantiates", "Cart"), ("python:app.made", "instantiates", "Line"),
            ],
            named.Select(edge => (edge.From, edge.Kind, edge.To.Split('.')[^1])));

        // No __all__: every public name, and no private one.
        var publicNames = Edges(
            ("pkg/cart.py", Read("pkg/cart.py", imports + body)),
            ("pkg/lines.py", Read("pkg/lines.py", lines)),
            ("app.py", Read("app.py", app)));

        Assert.Equal(
            [
                ("python:app.packed", "instantiates", "Cart"), ("python:app.packed", "instantiates", "Line"),
                ("python:app.loose", "calls", "unpacked"), ("python:app.loose", "instantiates", "Cart"), ("python:app.loose", "instantiates", "Line"),
                ("python:app.made", "instantiates", "Cart"), ("python:app.made", "instantiates", "Line"),
            ],
            publicNames.Select(edge => (edge.From, edge.Kind, edge.To.Split('.')[^1])));

        // An __all__ the code computes names nothing the graph can read: the public names, as with none.
        var computed = Edges(
            ("pkg/cart.py", Read("pkg/cart.py", imports + "\n__all__ = [\"Cart\"]\n__all__.append(\"Line\")\n" + body)),
            ("pkg/lines.py", Read("pkg/lines.py", lines)),
            ("app.py", Read("app.py", app)));

        Assert.Equal(
            [
                ("python:app.packed", "instantiates", "Cart"), ("python:app.packed", "instantiates", "Line"),
                ("python:app.loose", "calls", "unpacked"), ("python:app.loose", "instantiates", "Cart"), ("python:app.loose", "instantiates", "Line"),
                ("python:app.made", "instantiates", "Cart"), ("python:app.made", "instantiates", "Line"),
            ],
            computed.Select(edge => (edge.From, edge.Kind, edge.To.Split('.')[^1])));
    }

    /// <summary>
    /// A name a file neither declares nor imports binds nothing, though another module of the repository declares it: a Python name is
    /// looked up in the module's own names and its imports, and in no other module's, so the repository's declarations are not looked
    /// through for it - as they are for a language whose files share one scope.
    /// </summary>
    [Fact]
    public void ANameAFileNeitherDeclaresNorImports_BindsNothing()
    {
        var linked = Link(
            ("pkg/cart.py", Read("pkg/cart.py", "class CartError(Exception):\n    pass\n\n\ndef total(cart):\n    return CartError\n")),
            ("app.py", Read("app.py", "def load(path):\n    return CartError\n")));

        // Declared in the module: bound. Declared in another module and not imported: no edge, and no candidates.
        Assert.Contains(new CodeEdge("python:pkg.cart.total", "python:pkg.cart.CartError", CodeEdgeKinds.References, 6, CodeConfidences.Exact), linked.Edges);
        Assert.DoesNotContain(linked.Edges, edge => edge.From.StartsWith("python:app.", StringComparison.Ordinal) && edge.To == "python:pkg.cart.CartError");
    }

    /// <summary>
    /// A test's parameter names a fixture, and reaches a fixture alone: a function of the test file that is no fixture, though it
    /// carries the name the parameter writes, is not reached by it.
    /// </summary>
    [Fact]
    public void ATestParameter_NamesAFixture_AndReachesAFixtureAlone()
    {
        var extract = Read("tests/test_cart.py", Tests);
        var linked = Link(("tests/test_cart.py", extract), ("tests/conftest.py", Read("tests/conftest.py", Conftest)));
        var test = "python:tests.test_cart.test_sees_no_helper";

        Assert.Equal([("helper", CodeEdgeKinds.References, 39, CodeQualifierKinds.Fixture)], References(extract, test).Where(reference => reference.Kind == CodeEdgeKinds.References));
        Assert.DoesNotContain(linked.Edges, edge => edge.From == test && edge.Kind == CodeEdgeKinds.References);
        Assert.Contains(new CodeEdge("python:tests.test_cart.test_parse_sample", "python:tests.conftest.sample", CodeEdgeKinds.References, 35, CodeConfidences.Unique), linked.Edges);
    }

    private static FileExtract Read(string path, string text) => new PythonExtractor().Extract(new CodeFileInput(path, text));

    private static CodeGraphLinked Link(params (string Path, FileExtract Extract)[] files)
        => CodeGraphLinker.Link([.. files.Select(file => new CodeGraphFile(file.Path, "python", file.Extract))]);

    private static ExtractNode Node(FileExtract extract, string id) => Assert.Single(extract.Nodes, node => node.Id == id);

    private static List<(string Name, string Kind, int Line, string? Qualifier)> References(FileExtract extract, string from)
        => [.. extract.References.Where(reference => reference.From == from).Select(reference => (reference.Name, reference.Kind, reference.Line, reference.Qualifier?.Kind))];
}
