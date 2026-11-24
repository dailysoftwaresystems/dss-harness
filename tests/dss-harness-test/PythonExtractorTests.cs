using System.Globalization;
using System.Text.Json;
using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;
using DssHarness.Core.Codebase.Languages.Scanning;
using DssHarness.Core.FileSystem;

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

    /// <summary>
    /// A character's name in each form of a string: decoded in a plain one and in an f-string's, by an alias in lower case too; kept as
    /// written in a raw one, as Python keeps it; and an unknown name, which Python refuses.
    /// </summary>
    internal const string Names = """
        DASH = "\N{EM DASH}"
        BOM = f"\N{bom}{DASH}"
        RAW = r"\N{EM DASH}"
        BAD = "\N{NO SUCH NAME}"
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

    internal const string Forms = """"
        """Forms the reader keeps apart."""
        import functools
        import shop.models as models
        import shop.errors as errors
        from .accounts import User as Account
        from .states import Status, Refund
        from .fmt import format
        from typing import Annotated, Literal

        HOST, PORT = "localhost", 8080
        (LEFT, RIGHT) = "left", "right"
        type Pair[K] = tuple[K, models.Price]
        match = lambda text: text.strip()
        LIMIT = 5
        check(LIMIT)


        def open(path):
            return path


        def retry(times):
            return functools.partial(times)


        @app.route("/orders", methods=METHODS)
        @retry(LIMIT)
        @functools.wraps(open)
        def orders(order: models.Order, owner: "Account") -> models.Receipt:
            return open("orders")


        class Basket[T: models.Item](models.Base[T]):
            RATE = 2
            stamp(RATE)

            def __init__(self, items, cap=RATE):
                self.items = items
                self.weight = 0
                self.MAX = cap

            @staticmethod
            def read(text, strict=False):
                return format(text)

            @classmethod
            def empty(cls, size):
                return cls.read(str(size))

            def put(self, item: Literal["tea", "cake"], grams: Annotated[int, Positive(LIMIT)]):
                self.items.append(item)
                self.weight += item.grams
                del self.items[0]
                return weigh()

            def kind(self, status):
                match status:
                    case Status.PAID:
                        return "paid"
                    case Refund(amount=amount):
                        return f"-{amount}"
                    case [first, *rest]:
                        return first
                try:
                    return isinstance(self.items, models.Order)
                except errors.OrderError as error:
                    raise error


        async def crawl(urls):
            async with session() as client:
                async def fetch(url):
                    return await client.get(url)
            async for row in rows():
                print(row)
            return [fetch(url) for url in urls if url], (lambda each=LIMIT: each.size)


        def weigh():
            first, *rest = pairs()
            return first
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
                new CodeEdge("python:tests.test_cart.test_parse_sample", "python:tests.conftest.sample", CodeEdgeKinds.References, 35, CodeConfidences.Exact),
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
        new("pkg/forms.py", Forms)
        {
            Nodes =
            [
                new("python:pkg.forms", CodeNodeKinds.Namespace, (1, 81)),
                new("python:pkg.forms.HOST", CodeNodeKinds.Member, (10, 10)),
                new("python:pkg.forms.PORT", CodeNodeKinds.Member, (10, 10)),
                new("python:pkg.forms.LEFT", CodeNodeKinds.Member, (11, 11)),
                new("python:pkg.forms.RIGHT", CodeNodeKinds.Member, (11, 11)),
                new("python:pkg.forms.Pair", CodeNodeKinds.Type, (12, 12)),
                new("python:pkg.forms.match", CodeNodeKinds.Member, (13, 13)),
                new("python:pkg.forms.LIMIT", CodeNodeKinds.Member, (14, 14)),
                new("python:pkg.forms.open", CodeNodeKinds.Member, (18, 19)),
                new("python:pkg.forms.retry", CodeNodeKinds.Member, (22, 23)),
                new("python:pkg.forms.orders", CodeNodeKinds.Member, (26, 30)),
                new("python:pkg.forms.Basket", CodeNodeKinds.Type, (33, 67)),
                new("python:pkg.forms.Basket.RATE", CodeNodeKinds.Member, (34, 34)),
                new("python:pkg.forms.Basket.__init__", CodeNodeKinds.Member, (37, 40)),
                new("python:pkg.forms.Basket.items", CodeNodeKinds.Member, (38, 38)),
                new("python:pkg.forms.Basket.weight", CodeNodeKinds.Member, (39, 39)),
                new("python:pkg.forms.Basket.MAX", CodeNodeKinds.Member, (40, 40)),
                new("python:pkg.forms.Basket.read", CodeNodeKinds.Member, (42, 44)),
                new("python:pkg.forms.Basket.empty", CodeNodeKinds.Member, (46, 48)),
                new("python:pkg.forms.Basket.put", CodeNodeKinds.Member, (50, 54)),
                new("python:pkg.forms.Basket.kind", CodeNodeKinds.Member, (56, 67)),
                new("python:pkg.forms.crawl", CodeNodeKinds.Member, (70, 76)),
                new("python:pkg.forms.crawl.fetch", CodeNodeKinds.Member, (72, 73)),
                new("python:pkg.forms.weigh", CodeNodeKinds.Member, (79, 81)),
            ],
            Edges =
            [
                new CodeEdge("python:pkg.forms", "python:pkg.forms.LIMIT", CodeEdgeKinds.Uses, 15, CodeConfidences.Exact),
                new CodeEdge("python:pkg.forms.orders", "python:pkg.forms.retry", CodeEdgeKinds.Calls, 27, CodeConfidences.Exact),
                new CodeEdge("python:pkg.forms.orders", "python:pkg.forms.open", CodeEdgeKinds.Uses, 28, CodeConfidences.Exact),
                new CodeEdge("python:pkg.forms.orders", "python:pkg.forms.open", CodeEdgeKinds.Calls, 30, CodeConfidences.Exact),
                new CodeEdge("python:pkg.forms.Basket", "python:pkg.forms.Basket.RATE", CodeEdgeKinds.Uses, 35, CodeConfidences.Exact),
                new CodeEdge("python:pkg.forms.Basket.__init__", "python:pkg.forms.Basket.RATE", CodeEdgeKinds.Uses, 37, CodeConfidences.Exact),
                new CodeEdge("python:pkg.forms.Basket.empty", "python:pkg.forms.Basket.read", CodeEdgeKinds.Calls, 48, CodeConfidences.Exact),
                new CodeEdge("python:pkg.forms.Basket.put", "python:pkg.forms.Basket.items", CodeEdgeKinds.Uses, 51, CodeConfidences.Exact),
                new CodeEdge("python:pkg.forms.Basket.put", "python:pkg.forms.Basket.weight", CodeEdgeKinds.Uses, 52, CodeConfidences.Exact),
                new CodeEdge("python:pkg.forms.Basket.put", "python:pkg.forms.weigh", CodeEdgeKinds.Calls, 54, CodeConfidences.Exact),
                new CodeEdge("python:pkg.forms.Basket.kind", "python:pkg.forms.Basket.items", CodeEdgeKinds.Uses, 65, CodeConfidences.Exact),
                new CodeEdge("python:pkg.forms.crawl", "python:pkg.forms.crawl.fetch", CodeEdgeKinds.Calls, 76, CodeConfidences.Exact),
            ],
        },
        new("pkg/names.py", Names)
        {
            Nodes =
            [
                new("python:pkg.names", CodeNodeKinds.Namespace, (1, 4)),
            ],
        },
    ];

    /// <summary>
    /// The module is a namespace named by the file's path in dots - an <c>__init__.py</c> its directory's package, a stub's and a window
    /// script's its module's - one open scope, holding the file's lines, and each function's body a closed scope of its own whose parent is
    /// the module, a method's never its class; a function at its top a function, a method in a class a method and <c>__init__</c> a
    /// constructor, a static method's first parameter counted as any other's; a name assigned at the top a constant where written in
    /// capitals and a field otherwise; each with its signature and the sentence its docstring gives.
    /// </summary>
    [Fact]
    public void AModule_IsANamespaceNamedByItsPath_AndItsDeclarationsAreItsMembers()
    {
        var cart = Read("pkg/cart.py", Cart);

        var module = Node(cart, "python:pkg.cart");
        Assert.Equal((CodeNodeKinds.Namespace, "module", "cart", "pkg.cart", 1, 60), (module.Kind, module.SubKind, module.Name, module.Qualified.Whole(), module.Start, module.End));
        Assert.Equal("A cart of lines.", module.Summary);
        Assert.Equal(
            [
                new ExtractScope(QualifiedName.Of("pkg.cart"), true),
                .. new[] { "Cart.__init__", "Cart.count", "Cart.add", "Cart.flush", "Cart.parse", "total", "describe" }
                    .Select(name => new ExtractScope(QualifiedName.Of($"pkg.cart.{name}"), false) { Parent = QualifiedName.Of("pkg.cart") }),
            ],
            cart.Scopes);

        var init = Node(cart, "python:pkg.cart.Cart.__init__");
        Assert.Equal(("constructor", 1, null, "def __init__(self, owner: str, limit: int = MAX_LINES, *args, **kwargs) -> None"), (init.SubKind, init.MinArity, init.MaxArity, init.Signature));
        Assert.Equal(QualifiedName.Of("pkg.cart.Cart"), init.Scope);
        Assert.Equal((1, 1), (Node(cart, "python:pkg.cart.Cart.parse").MinArity, Node(cart, "python:pkg.cart.Cart.parse").MaxArity));

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

        // Of the rest, the one probe is the call of the decorator an import of a package binds, @dataclass, made around the class.
        Assert.Equal(
            [("python:pkg.cart.Cart", "dataclass", 17, true)],
            cart.References.Where(reference => reference.Kind != CodeEdgeKinds.Overrides && reference.Probe).Select(reference => (reference.From.Whole(), reference.Name, reference.Line, reference.Around)));

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
    /// the base's, a module's name an import's prefix, any longer chain or other receiver a value, each member a chain passes on its way
    /// read as its head says; a class a call makes where the code writes a capitalized name; an attribute <c>__init__</c> assigns through
    /// <c>self</c> a field of its class, no use; a name a keyword argument gives, a local the body binds and a builtin none of them a use.
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

        // __init__: self.owner and self.limit are fields of Cart it declares, no uses of them.
        Assert.Equal(
            [
                ("__init__", CodeEdgeKinds.Overrides, 22, CodeQualifierKinds.Base),
                ("MAX_LINES", CodeEdgeKinds.Uses, 22, (string?)null),
                ("__init__", CodeEdgeKinds.Calls, 25, CodeQualifierKinds.Base),
            ],
            References(cart, init));

        // add: its own override reference first - a method is bound by its name alone - then its parameters' annotations, then its
        // body: self's members, a class it makes, the member self.lines it passes and the member it reaches through it.
        Assert.Equal(
            [
                ("add", CodeEdgeKinds.Overrides, 31, CodeQualifierKinds.Base),
                ("CartError", CodeEdgeKinds.References, 31, (string?)null), ("Line", CodeEdgeKinds.References, 31, (string?)null),
                ("count", CodeEdgeKinds.Uses, 32, CodeQualifierKinds.This), ("limit", CodeEdgeKinds.Uses, 32, CodeQualifierKinds.This),
                ("CartError", CodeEdgeKinds.Instantiates, 33, (string?)null),
                ("lines", CodeEdgeKinds.Uses, 34, CodeQualifierKinds.This), ("append", CodeEdgeKinds.Calls, 34, CodeQualifierKinds.Value),
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
    /// make_line</c> shows both; <c>from ..shared import money as formatted</c> binds <c>formatted</c> to <c>money</c> by a path that
    /// climbs; each name shown is looked for at each place as a module of its own, and else as the name the module binds, which its
    /// prefix then stands for; what an import at the module's top binds is the file's importers' too; and <c>from m import *</c> takes
    /// the names the module publishes alone. A module by a bare name is tried in the file's own directory and in each above it to the
    /// tree's root, the nearest first, every place one of a layout the linker passes over inside a package, the file's own directory's
    /// one of two places a name may stand at, and then under the roots of the layout; under each, a package's <c>__init__.py</c> stands
    /// first and the module's <c>.py</c> in for it, each place standing in for the one before it.
    /// </summary>
    [Fact]
    public void EachImport_BindsItsNames_AndIsLookedForWhereThePackageLayoutHoldsIt()
    {
        var cart = Read("pkg/cart.py", Cart);

        Assert.Equal([CodeImportForms.Relative, CodeImportForms.Rooted], cart.Imports.Select(import => import.Form).Distinct());
        Assert.Equal(
            [
                (6, "__future__/__init__.py", "__future__.py", (string?)null, "annotations", true, false),
                (12, "__future__/annotations/__init__.py", "__future__.py", "annotations", (string?)null, true, false),
                (6, "os/path/__init__.py", "os/path.py", "osp", (string?)null, true, false),
                (6, "dataclasses/__init__.py", "dataclasses.py", (string?)null, "dataclass,field", true, false),
                (12, "dataclasses/dataclass/__init__.py", "dataclasses.py", "dataclass", (string?)null, true, false),
                (12, "dataclasses/field/__init__.py", "dataclasses.py", "field", (string?)null, true, false),
                (2, "base/__init__.py", "base.py", (string?)null, "BaseCart,Ordered", true, false),
                (4, "base/BaseCart/__init__.py", "base.py", "BaseCart", (string?)null, true, false),
                (4, "base/Ordered/__init__.py", "base.py", "Ordered", (string?)null, true, false),
                (2, "lines/__init__.py", "lines.py", (string?)null, "Line,make_line", true, false),
                (4, "lines/Line/__init__.py", "lines.py", "Line", (string?)null, true, false),
                (4, "lines/make_line/__init__.py", "lines.py", "make_line", (string?)null, true, false),
                (2, "../shared/__init__.py", "../shared.py", "formatted", "money", true, false),
                (4, "../shared/money/__init__.py", "../shared.py", "formatted", (string?)null, true, false),
            ],
            Runs(cart));

        // The first six runs are of bare names: each place in a directory one of a layout, the file's own directory's one of two places a
        // name may stand at, and the last two of each run under the roots of the layout; the rest are relative, and none of them is.
        Assert.All(cart.Imports.Take(54), import => Assert.Equal((import.Form == CodeImportForms.Relative, import.Form == CodeImportForms.Relative && !import.Target.StartsWith("../", StringComparison.Ordinal)), (import.Layout, import.Either)));
        Assert.Equal(CodeImportForms.Rooted, cart.Imports[52].Form);
        Assert.All(cart.Imports.Skip(54), import => Assert.Equal((CodeImportForms.Relative, false, false), (import.Form, import.Layout, import.Either)));

        // What from ..shared import money as formatted binds: the module shared.money, else the name money shared binds, by formatted.
        Assert.Equal(
            [("../shared/money/__init__.py", false, (string?)null), ("../shared/money.py", false, null), ("../shared/__init__.py", true, "money"), ("../shared.py", true, "money")],
            cart.Imports.TakeLast(4).Select(import => (import.Target, import.Binds, import.Show is { } show ? string.Join(',', show) : null)));

        // A file at the tree's root has the one directory to look in, then the roots of the layout.
        Assert.Equal(
            [(4, "pkg/cart/__init__.py", "pkg/cart.py", (string?)null, "Cart", true, false), (8, "pkg/cart/Cart/__init__.py", "pkg/cart.py", "Cart", (string?)null, true, false)],
            Runs(Read("app.pyw", Window)));
        Assert.DoesNotContain(Read("app.pyw", Window).Imports, import => import.Either);

        // from m import *: the module's published names alone, at the module's top its importers' too.
        var star = Read("pkg/stars.py", "from .cart import *\n");
        Assert.Equal([(2, "cart/__init__.py", "cart.py", (string?)null, (string?)null, true, true)], Runs(star));
    }

    /// <summary>
    /// A bare import is looked for from above the file's package, as Python searches a path: a module of the same name inside the
    /// package is passed over, and the directory above the package's top is tried first, then each above it - a package's
    /// <c>__init__.py</c> saying its directory is one whatever it holds, nothing or comments alone among it. A file in a directory that
    /// is no package is its directory's script and a namespace package's module alike: where its own directory holds the module and a
    /// directory above holds one too, the two are candidates; where its own alone does, that one.
    /// </summary>
    [Fact]
    public void ABareImport_IsLookedForFromAboveThePackagesAroundTheFile()
    {
        var linked = Link(
            ("pkg/__init__.py", Read("pkg/__init__.py", string.Empty)),
            ("pkg/sub/__init__.py", Read("pkg/sub/__init__.py", "# The package in it.\n")),
            ("pkg/sub/helper.py", Read("pkg/sub/helper.py", "def pack(): ...\n")),
            ("pkg/sub/mod.py", Read("pkg/sub/mod.py", "import helper\n")),
            ("helper.py", Read("helper.py", "def pack(): ...\n")),
            ("pkg/top.py", Read("pkg/top.py", "from helper import pack\n")),
            ("tools/run.py", Read("tools/run.py", "import helper\n")),
            ("tools/helper.py", Read("tools/helper.py", "def pack(): ...\n")),
            ("scripts/go.py", Read("scripts/go.py", "import tool\n")),
            ("scripts/tool.py", Read("scripts/tool.py", "def run(): ...\n")));

        // Each package's __init__.py says its directory is a unit of the layout - an empty one, and one of comments alone, too.
        Assert.All(new[] { string.Empty, "# Only a comment.\n", "\"\"\"A package.\"\"\"\n" }, text => Assert.True(Read("pkg/__init__.py", text).FolderUnit));
        Assert.False(Read("pkg/sub/mod.py", "import helper\n").FolderUnit);

        // In a package two deep: the package's own directories are passed over, the tree's root is the first tried.
        Assert.Contains(new CodeEdge("file:pkg/sub/mod.py", "file:helper.py", CodeEdgeKinds.Imports, 1, CodeConfidences.Exact), linked.Edges);
        Assert.DoesNotContain(linked.Edges, edge => edge.Kind == CodeEdgeKinds.Imports && edge.From == "file:pkg/sub/mod.py" && edge.To != "file:helper.py");

        // In the package's top: likewise.
        Assert.Contains(new CodeEdge("file:pkg/top.py", "file:helper.py", CodeEdgeKinds.Imports, 1, CodeConfidences.Exact), linked.Edges);

        // In no package: the file's own directory's module and the root's, candidates both; its own directory's alone, that one.
        Assert.Equal(
            [new CodeEdge("file:tools/run.py", "file:helper.py", CodeEdgeKinds.Imports, 1, CodeConfidences.Candidates), new CodeEdge("file:tools/run.py", "file:tools/helper.py", CodeEdgeKinds.Imports, 1, CodeConfidences.Candidates)],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Imports && edge.From == "file:tools/run.py").OrderBy(edge => edge.To, StringComparer.Ordinal));
        Assert.Equal(
            [new CodeEdge("file:scripts/go.py", "file:scripts/tool.py", CodeEdgeKinds.Imports, 1, CodeConfidences.Exact)],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Imports && edge.From == "file:scripts/go.py"));
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
        Assert.Equal([("Cart", CodeEdgeKinds.Uses, 11, (string?)null)], References(extract, "python:pkg.dynamic.reach").Where(reference => reference.Name == "Cart"));
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

        // A decorator is a modifier by its dotted name and a call of what it names, made around the function: pytest's fixture, which an
        // import of the package binds, a probe of whether the repository declares it. A fixture is a function of its own kind within its
        // kind, and never a test though a test file declares it.
        var own = Node(extract, "python:tests.test_cart.empty_cart");

        Assert.Equal(["@pytest.fixture"], own.Modifiers);
        Assert.Equal(CodeSubKinds.Fixture, own.SubKind);
        Assert.False(own.Test);
        Assert.Equal(
            [new ExtractReference(QualifiedName.Of("python:tests.test_cart.empty_cart"), CodeEdgeKinds.Calls, "fixture", 7) { Arity = 1, Qualifier = new ExtractQualifier(CodeQualifierKinds.Prefix, QualifiedName.Of("pytest")), Probe = true, Around = true }],
            extract.References.Where(reference => reference.Name is "pytest" or "fixture"));

        // A fixture the test file imports from nowhere: linked to the conftest.py's, through the conftest's directory import.
        var conftest = Read("tests/conftest.py", Conftest);

        Assert.Equal((CodeSubKinds.Fixture, false), (Node(conftest, "python:tests.conftest.sample").SubKind, Node(conftest, "python:tests.conftest.sample").Test));
        Assert.Contains(new CodeEdge("python:tests.test_cart.test_parse_sample", "python:tests.conftest.sample", CodeEdgeKinds.References, 35, CodeConfidences.Exact), Link(("tests/test_cart.py", extract), ("tests/conftest.py", conftest)).Edges);

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
        // A nested declaration is held by the node it is in and declared in that node's scope: a function's body is a scope of its own,
        // so a function nested in a method is in the method's, as a class nested in a class is in its class's own scope.
        Assert.Equal((QualifiedName.Of("pkg.nested.Outer.make"), QualifiedName.Of("python:pkg.nested.Outer.make")), (Node(extract, "python:pkg.nested.Outer.make.inner").Scope, Node(extract, "python:pkg.nested.Outer.make.inner").Parent));
        Assert.Equal((QualifiedName.Of("pkg.nested.Outer"), QualifiedName.Of("python:pkg.nested.Outer")), (Node(extract, "python:pkg.nested.Outer.Inner").Scope, Node(extract, "python:pkg.nested.Outer.Inner").Parent));
        Assert.Equal(("field", 2, 2), (Node(extract, "python:pkg.nested.Outer.scale").SubKind, Node(extract, "python:pkg.nested.Outer.scale").Start, Node(extract, "python:pkg.nested.Outer.scale").End));
        Assert.Equal([("helper", CodeEdgeKinds.Calls, 6, (string?)null)], References(extract, "python:pkg.nested.Outer.make.inner").Where(reference => reference.Kind == CodeEdgeKinds.Calls));
        Assert.Equal([("go", CodeEdgeKinds.Overrides, 10, CodeQualifierKinds.Base), ("Outer", CodeEdgeKinds.Instantiates, 11, (string?)null), ("make", CodeEdgeKinds.Calls, 11, CodeQualifierKinds.Value)], References(extract, "python:pkg.nested.Outer.Inner.go"));
        Assert.DoesNotContain(References(extract, "python:pkg.nested.Outer.make"), reference => reference.Kind == CodeEdgeKinds.Calls);

        // A name a body nested any depth reads is looked for from its own scope outward - the functions around it, the module, never a
        // class around it - and never in a declaration of another module the file does not import.
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
    /// own fields are code; a backslash before a field's brace, and <c>\N{...}</c> no field; Python's escapes - a character's name its
    /// character, by Unicode's names and aliases as CPython 3.15 reads them, a lone surrogate kept, a line break a backslash joins dropped,
    /// one Python does not know kept with its backslash, a bytes string's its own - and a raw string's escaped quote, which closes
    /// nothing. No row holds an escape Python refuses, so no line is marked; a lone surrogate the decoder gives is the
    /// replacement character in the graph, as every one a literal's value holds is (<see cref="ExtractText.Whole"/>).
    /// </summary>
    [Theory]
    [MemberData(nameof(LexedStrings))]
    public void EachLexicalRuleOfAString_IsReadAsPythonReadsIt(string source, string[] values, string[] references)
    {
        var extract = Read("pkg/lex.py", source);

        Assert.Equal(values, (extract.Literals ?? []).Select(literal => literal.Value));
        Assert.Equal(references, References(extract, "python:pkg.lex.X").Select(reference => $"{reference.Kind} {reference.Name}"));

        // Every string of a row closes and holds no refused escape, so no line is marked.
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
        { """X = f"{V:{W}}}}" """, ["}"], ["uses V", "uses W"] },
        { """X = f"{V:>3} {{x}} {W} end" """, [" {x} ", " end"], ["uses V", "uses W"] },
        { """X = f"{RETRIES=}" """, ["RETRIES="], ["uses RETRIES"] },
        { """X = f"{TOTAL = }" """, ["TOTAL = "], ["uses TOTAL"] },
        { """X = f"{A + B=!r:>{W}}" """, ["A + B="], ["uses A", "uses B", "uses W"] },
        { """X = t"Hello {USER}" """, ["Hello "], ["uses USER"] },
        { """X = Rt"\d{USER}" """, ["\\d"], ["uses USER"] },
        { """X = f"\N{EM DASH} {TITLE}" """, ["\u2014 "], ["uses TITLE"] },
        { """X = f"\{NAME}" """, ["\\"], ["uses NAME"] },
        { """X = "\x41\u0042\U00000043\101\N{EM DASH}" """, ["ABCA\u2014"], [] },
        { """X = u"\N{latin small letter e with acute}" """, ["\u00E9"], [] },
        { """X = t"\N{EM DASH}{USER}" """, ["\u2014"], ["uses USER"] },
        { """X = r"\N{EM DASH}" """, ["\\N{EM DASH}"], [] },
        { """X = "\U0000D800" """, ["\uFFFD"], [] },
        { """X = "\uD800" """, ["\uFFFD"], [] },
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
    /// A character's name in braces is the character it names, in each form of a string Python decodes one: a plain string, a
    /// <c>u</c> one, an f-string's and a t-string's constant parts, by the names and aliases of Unicode 17.0.0 as CPython 3.15 reads them - an
    /// alias in lower case, a computed name of a CJK unified ideograph and of a Hangul syllable among them. A bytes string and a raw
    /// one keep it as written, as Python does, and no line is marked.
    /// </summary>
    [Fact]
    public void ACharactersName_IsDecodedAsCPythonDecodesIt_InEachFormOfString()
    {
        var extract = Read("pkg/named.py", """
            A = "\N{EM DASH}\N{BOM}\N{CJK UNIFIED IDEOGRAPH-4E00}\N{HANGUL SYLLABLE GA}"
            B = f"{A}\N{LF}"
            C = b"\N{EM DASH}"
            D = rb"\N{EM DASH}"
            """);

        // A value is kept once with every place it is written: the bytes string's and the raw one's are alike.
        Assert.Equal(["\u2014\uFEFF\u4E00\uAC00", "\n", "\\N{EM DASH}"], (extract.Literals ?? []).Select(literal => literal.Value));
        Assert.Equal(
            [("python:pkg.named.A", 1), ("python:pkg.named.B", 2), ("python:pkg.named.C", 3), ("python:pkg.named.D", 4)],
            (extract.Literals ?? []).SelectMany(literal => literal.Places, (literal, place) => (place.Node?.Whole(), place.Line)));
        Assert.Equal([("A", CodeEdgeKinds.Uses, 2)], References(extract, "python:pkg.named.B").Select(reference => (reference.Name, reference.Kind, reference.Line)));
        Assert.Empty(extract.Dynamic);
    }

    /// <summary>
    /// A name CPython computes is the character it names in a literal in any case, as 3.15 reads it and 3.14 did not: an ideograph's
    /// from its prefix and the code in lower-case digits, a Tangut ideograph's, a Nushu character's, a Hangul syllable's from lower-case
    /// jamo - in a plain string and in an f-string's constant parts. The same prefix and a leading zero is one Python refuses: kept as
    /// written, its line marked, the file being one Python does not compile.
    /// </summary>
    [Fact]
    public void ANameCPythonComputes_IsTheCharacterInAnyCase_InALiteral()
    {
        var extract = Read("pkg/computed.py", """
            A = "\N{cjk unified ideograph-4e00}\N{TANGUT IDEOGRAPH-17000}\N{hangul syllable ga}"
            B = f"\N{nushu character-1b170}{A}\N{tangut ideograph-18d1e}"
            C = "\N{CJK UNIFIED IDEOGRAPH-04E00}"
            """);

        // An f-string's constant parts are a literal each: the field between them is code.
        Assert.Equal(["\u4E00\U00017000\uAC00", "\U0001B170", "\U00018D1E", "\\N{CJK UNIFIED IDEOGRAPH-04E00}"], (extract.Literals ?? []).Select(literal => literal.Value));
        Assert.Equal([("A", CodeEdgeKinds.Uses, 2)], References(extract, "python:pkg.computed.B").Select(reference => (reference.Name, reference.Kind, reference.Line)));
        Assert.Equal([new ExtractDynamic(3, Refused, PythonReading.RefusedEscape)], extract.Dynamic);
    }

    /// <summary>
    /// Each refused escape of a part is said once, by the line it stands on: two on one line give that line twice, and it is the reader
    /// that marks a line once (<see cref="AnEscapePythonRefuses_IsKeptAsWritten_AndMarksItsLine"/>). Read on the lexer's literal, where
    /// the decoder's answer is seen whole.
    /// </summary>
    [Fact]
    public void EachRefusedEscapeOfAPart_IsSaidByItsLine_OneAnEscape()
    {
        var text = PythonLexer.Scan(CodeText.Of("X = \"\\x4\\u12\"\nY = \"\\N{BAD}\\U1234\""));
        var first = Assert.Single(text.Scan.LiteralValues(text.Scan[2]));
        var second = Assert.Single(text.Scan.LiteralValues(text.Scan[5]));

        Assert.Equal(("\\x4\\u12", 1), (first.Value, first.Line));
        Assert.Equal([1, 1], first.RefusedLines);
        Assert.Equal(("\\N{BAD}\\U1234", 2), (second.Value, second.Line));
        Assert.Equal([2, 2], second.RefusedLines);
    }

    /// <summary>
    /// A <c>\U</c> naming a lone surrogate is no escape Python refuses: the decoder gives the surrogate as a <c>\u</c>'s does, and no
    /// line is marked - where one naming a code past every character is kept as written and refused. Read on the lexer's literal, where
    /// the value is the decoder's: the graph keeps a lone surrogate as the replacement character, as it keeps every one a value holds.
    /// </summary>
    [Fact]
    public void AUniversalEscapeNamingALoneSurrogate_IsTheSurrogate_NotRefused()
    {
        static (string Value, int Line, IReadOnlyList<int> RefusedLines) Decoded(string source)
        {
            var text = PythonLexer.Scan(CodeText.Of(source));
            var literal = text.Scan[2];

            Assert.Equal(ScanTokenKind.Text, literal.Kind);
            var value = Assert.Single(text.Scan.LiteralValues(literal));
            return (value.Value, value.Line, value.RefusedLines);
        }

        foreach (var source in new[] { "X = \"\\U0000D800\"", "X = \"\\uD800\"" })
        {
            var (value, line, refused) = Decoded(source);
            Assert.Equal("\uD800", value);
            Assert.Equal(1, line);
            Assert.Empty(refused);
        }

        var (kept, keptLine, refusedLines) = Decoded("X = \"\\U00110000\"");
        Assert.Equal("\\U00110000", kept);
        Assert.Equal(1, keptLine);
        Assert.Equal([1], refusedLines);
    }

    /// <summary>The sentence of every mark of a refused escape, as <see cref="PythonReading"/> writes it.</summary>
    private const string Refused = "an escape Python refuses - a character's name Unicode has not, a \\N with no name in braces, a \\x, \\u or \\U short of its digits, a \\U past every character: kept as written";

    /// <summary>
    /// An escape Python refuses is kept as written in its literal's value, and the line it stands on is marked dynamic with the sentence
    /// of its limit: an unknown name; a computed name with a leading zero; a <c>\N</c> with no name in braces, empty or never closed - one read over a line break to its
    /// brace is still one unknown name; a <c>\x</c>, <c>\u</c> or <c>\U</c> short of its digits, in bytes too; a <c>\U</c> past every
    /// character; in an f-string's constant text and in a string its field holds. Two refusals on one line make one mark, and on two
    /// lines two; in a string over lines the line of the escape is marked, not the one the string begins on.
    /// </summary>
    /// <param name="source">A module's source.</param>
    /// <param name="values">Its literals' values, as kept.</param>
    /// <param name="lines">The lines marked, in their order.</param>
    [Theory]
    [MemberData(nameof(RefusedEscapes))]
    public void AnEscapePythonRefuses_IsKeptAsWritten_AndMarksItsLine(string source, string[] values, int[] lines)
    {
        var extract = Read("pkg/refused.py", source);

        Assert.Equal(values, (extract.Literals ?? []).Select(literal => literal.Value));
        Assert.Equal(lines.Select(line => new ExtractDynamic(line, Refused, PythonReading.RefusedEscape)), extract.Dynamic);
    }

    /// <summary>The rows of <see cref="AnEscapePythonRefuses_IsKeptAsWritten_AndMarksItsLine"/>: a module's source, its literals' values as kept, and the lines marked.</summary>
    public static TheoryData<string, string[], int[]> RefusedEscapes() => new()
    {
        { """X = "\N{NO SUCH NAME}" """, ["\\N{NO SUCH NAME}"], [1] },
        { """X = "\N{em-dash}" """, ["\\N{em-dash}"], [1] },
        { """X = "\N{}" """, ["\\N{}"], [1] },
        { """X = "\N{EM DASH" """, ["\\N{EM DASH"], [1] },
        { """X = "\N" """, ["\\N"], [1] },
        { """X = "\x4" """, ["\\x4"], [1] },
        { """X = "\u12" """, ["\\u12"], [1] },
        { """X = "\U1234" """, ["\\U1234"], [1] },
        { """X = "\U00110000" """, ["\\U00110000"], [1] },
        { """X = b"\x4" """, ["\\x4"], [1] },
        { """X = f"\N{BAD NAME}" """, ["\\N{BAD NAME}"], [1] },
        { """X = f"{'\N{BAD NAME}'}" """, ["\\N{BAD NAME}"], [1] },

        // A name CPython computes, written with a leading zero: 3.14 read it a character, 3.15 refuses it.
        { """X = "\N{CJK UNIFIED IDEOGRAPH-04E00}" """, ["\\N{CJK UNIFIED IDEOGRAPH-04E00}"], [1] },

        // Two on one line make one mark, and two lines make two.
        { """X = "\N{ONE}\N{TWO}" """, ["\\N{ONE}\\N{TWO}"], [1] },
        { "X = \"\\N{ONE}\"\nY = \"\\x4\"\n", ["\\N{ONE}", "\\x4"], [1, 2] },

        // A string over lines: the line the escape stands on is marked, not the one the string begins on.
        { "X = \"\"\"\nok\n\\N{BAD}\n\"\"\"\n", ["\nok\n\\N{BAD}\n"], [3] },
        { "X = \"\"\"\nok\n\\N{BAD}\n\nok\n\\u12\n\"\"\"\n", ["\nok\n\\N{BAD}\n\nok\n\\u12\n"], [3, 6] },

        // A name read to the first closing brace over a line break is still one unknown name, on the line it begins.
        { "X = \"\"\"\\N{EM\nDASH}\"\"\"\n", ["\\N{EM\nDASH}"], [1] },
    };

    /// <summary>
    /// Every literal the reading decodes marks the line its refused escape stands on, wherever the literal is read: at the module's top,
    /// in an <c>__all__</c>, in a decorator's argument, in a docstring, in a function's body and in a string its f-string's field holds;
    /// and in a string annotation, which is read as a type expression and kept as no literal of the graph, the line is marked just the
    /// same - in the annotation's own text, and in a literal that text holds: on the file line of the escape, where the annotation is
    /// written over lines and the escape is on the second. The kind is the sign of the one limit of the help that says it.
    /// </summary>
    [Fact]
    public void ARefusedEscape_MarksItsLine_WhereverTheLiteralIsRead()
    {
        var extract = Read("pkg/places.py", """"
            __all__ = ["\N{BAD ONE}"]
            MARKED: "\N{BAD TWO}" = 1


            @mark.parametrize("x", ["\N{BAD THREE}"])
            def decorated(x):
                """A docstring.


                Its refusal is two lines down: \N{BAD FOUR}.
                """
                return f"{'\N{BAD FIVE}'}"


            def annotated(value: "\N{BAD SIX}") -> "\N{BAD SEVEN}":
                return value


            ALIAS: r'Literal["\N{BAD EIGHT}"]' = None


            OVER: r"""Literal[
                "\N{BAD NINE}",
                ]""" = None
            """");

        Assert.Equal([1, 2, 5, 10, 12, 15, 19, 23], extract.Dynamic.Select(mark => mark.Line).Order());
        Assert.All(extract.Dynamic, mark => Assert.Equal(new ExtractDynamic(mark.Line, Refused, PythonReading.RefusedEscape), mark));
        Assert.Equal(["refused-escapes"], new PythonExtractor().Help.DoesNotSee.Where(limit => limit.Signs.Contains(PythonReading.RefusedEscape)).Select(limit => limit.Name));
    }

    /// <summary>
    /// A debugging field's text, to its <c>=</c> and the blanks after it, is a constant part of the string's value as written - never
    /// decoded - going on from the text before the field, as Python's value holds it, and its code is read as any field's; a field a
    /// format holds stands in no value's text, so its text is none of the value's.
    /// </summary>
    [Fact]
    public void ADebuggingFieldsText_IsAConstantPartOfTheValue_AsWritten()
    {
        var extract = Read("pkg/debug.py", """
            A = f"{RETRIES=}"
            B = f"tries: {RETRIES = !r}"
            C = f"{'\n'=}"
            D = f"{V:{W=}}"
            """);

        // The last is the string the field of C holds, decoded as any string is: its own, not C's value.
        Assert.Equal(["RETRIES=", "tries: RETRIES = ", "'\\n'=", "\n"], (extract.Literals ?? []).Select(literal => literal.Value));
        Assert.Equal([("RETRIES", CodeEdgeKinds.Uses, 1, (string?)null)], References(extract, "python:pkg.debug.A"));
        Assert.Equal([("RETRIES", CodeEdgeKinds.Uses, 2, (string?)null)], References(extract, "python:pkg.debug.B"));
        Assert.Equal([("V", CodeEdgeKinds.Uses, 4, (string?)null), ("W", CodeEdgeKinds.Uses, 4, (string?)null)], References(extract, "python:pkg.debug.D"));
    }

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
    /// A backslash continues a line where it ends the line's code alone, and into the next line alone: one a trailing comment ends
    /// with is the comment's; a line a backslash joins to a comment ends with that comment; and a line a backslash joins to a blank
    /// one ends there - so the class after each is a statement of its own.
    /// </summary>
    [Fact]
    public void ABackslash_ContinuesALineWhereItEndsItsCode_AndIntoTheNextLineAlone()
    {
        var extract = Read("pkg/config.py", """
            TIMEOUT = 30  # see C:\
            class Client:
                pass


            RETRIES = 3 \
            # the backslash above joins this comment to its line
            class Server:
                pass


            LIMIT = 1 \

            class Store:
                pass
            """);

        Assert.Equal(
            [
                ("python:pkg.config", 1, 15), ("python:pkg.config.TIMEOUT", 1, 1), ("python:pkg.config.Client", 2, 3), ("python:pkg.config.RETRIES", 6, 6),
                ("python:pkg.config.Server", 8, 9), ("python:pkg.config.LIMIT", 12, 12), ("python:pkg.config.Store", 14, 15),
            ],
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
        Assert.Contains(new CodeEdge("python:app.second", "python:pkg.lines.Line", CodeEdgeKinds.Uses, 4, CodeConfidences.Unique), linked.Edges);
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
        Assert.Contains(new CodeEdge("python:tests.test_cart.test_parse_sample", "python:tests.conftest.sample", CodeEdgeKinds.References, 35, CodeConfidences.Exact), linked.Edges);
    }

    /// <summary>
    /// An import and a statement a semicolon parts on one line are each read as written: the import binds its module alone, and the
    /// statement after it declares what it assigns and holds the code it calls.
    /// </summary>
    [Fact]
    public void AnImportAndAStatementOnOneLine_AreEachReadAsWritten()
    {
        var extract = Read("pkg/m.py", "import os; LIMIT = 3\nfrom .lines import Line; Line()\n");

        Assert.Contains(extract.Nodes, node => node.Id == "python:pkg.m.LIMIT");
        Assert.Equal(["os", "Line"], Runs(extract).Select(run => run.Prefix).OfType<string>());
        Assert.Contains(("Line", CodeEdgeKinds.Instantiates, 2, (string?)null), References(extract, "python:pkg.m"));
    }

    /// <summary>
    /// A chain whose head a <c>from</c> import binds is reached through the import, one rule for every name an import binds: the
    /// module <c>from . import views</c> binds is its own package's, never another package's module of that name; the module
    /// <c>from pkg import utils</c> binds is the package's, never a module of the tree's root of that name; and a name bound to a
    /// module outside the tree - <c>from os import path</c> - reaches nothing of the tree's, though a module of the tree ends with
    /// its name.
    /// </summary>
    [Fact]
    public void AChainThroughANameAFromImportBinds_IsReachedThroughTheImport()
    {
        var linked = Link(
            ("blog/__init__.py", Read("blog/__init__.py", string.Empty)),
            ("blog/views.py", Read("blog/views.py", "def index(request): ...\n")),
            ("shop/__init__.py", Read("shop/__init__.py", string.Empty)),
            ("shop/views.py", Read("shop/views.py", "def index(request): ...\n")),
            ("blog/urls.py", Read("blog/urls.py", "from . import views\n\nroutes = [views.index]\nviews.index(None)\n")),
            ("utils.py", Read("utils.py", "def parse(t): ...\n")),
            ("pkg/__init__.py", Read("pkg/__init__.py", string.Empty)),
            ("pkg/utils.py", Read("pkg/utils.py", "def parse(t): ...\n")),
            ("pkg/main.py", Read("pkg/main.py", "from pkg import utils\n\n\ndef run(t):\n    return utils.parse(t)\n")),
            ("config/path.py", Read("config/path.py", "def join(a, b): ...\n")),
            ("app.py", Read("app.py", "from os import path\n\n\ndef where(a, b):\n    return path.join(a, b)\n")));

        Assert.Contains(new CodeEdge("python:blog.urls.routes", "python:blog.views.index", CodeEdgeKinds.Uses, 3, CodeConfidences.Exact), linked.Edges);
        Assert.Contains(new CodeEdge("python:blog.urls", "python:blog.views.index", CodeEdgeKinds.Calls, 4, CodeConfidences.Exact), linked.Edges);
        Assert.Contains(new CodeEdge("python:pkg.main.run", "python:pkg.utils.parse", CodeEdgeKinds.Calls, 5, CodeConfidences.Exact), linked.Edges);
        Assert.DoesNotContain(linked.Edges, edge => edge.Kind != CodeEdgeKinds.Contains && edge.To is "python:shop.views.index" or "python:utils.parse" or "python:config.path.join");
    }

    /// <summary>
    /// <c>import a.b</c> binds <c>a</c>, the package, and reaches the module <c>a.b</c> by the names to it: a member of the package is the
    /// package's - its <c>__init__.py</c>'s, never the module's of the same name - a member reached by the whole path is the module's,
    /// and a chain through a module outside the tree reaches nothing of the tree's, though a function of its name is declared there. A
    /// step between the head and the module the import names is made an import of its own the first time the code reaches through
    /// it, and once.
    /// </summary>
    [Fact]
    public void ADottedImport_BindsItsHead_AndReachesEachModuleByTheNamesToIt()
    {
        const string app = """
            import os
            import shop.geometry


            def run(xs, root, name):
                os.path.join(root, name)
                shop.version()
                return shop.geometry.largest(xs)
            """;
        var linked = Link(
            ("shop/__init__.py", Read("shop/__init__.py", "def version(): ...\n")),
            ("shop/geometry.py", Read("shop/geometry.py", "def largest(xs): ...\n\n\ndef version(): ...\n")),
            ("tools/paths.py", Read("tools/paths.py", "def join(base, leaf): ...\n")),
            ("app.py", Read("app.py", app)));

        Assert.Equal(
            [
                (4, "os/__init__.py", "os.py", "os", (string?)null, true, false),
                (4, "shop/__init__.py", "shop.py", "shop", (string?)null, true, false),
                (4, "shop/geometry/__init__.py", "shop/geometry.py", "shop.geometry", (string?)null, false, false),
            ],
            Runs(Read("app.py", app)));
        Assert.Equal(
            [new CodeEdge("python:app.run", "python:shop.version", CodeEdgeKinds.Calls, 7, CodeConfidences.Exact), new CodeEdge("python:app.run", "python:shop.geometry.largest", CodeEdgeKinds.Calls, 8, CodeConfidences.Exact)],
            linked.Edges.Where(edge => edge.From == "python:app.run" && edge.Kind == CodeEdgeKinds.Calls).OrderBy(edge => edge.Line));

        // The step a.b of import a.b.c, reached through, becomes an import of its own once; one the code never reaches is none.
        Assert.Equal(["a", "a.b.c"], Runs(Read("app.py", "import a.b.c\n")).Select(run => run.Prefix));
        Assert.Equal(["a", "a.b.c", "a.b"], Runs(Read("app.py", "import a.b.c\n\na.b.f()\na.b.g()\n")).Select(run => run.Prefix));
    }

    /// <summary>
    /// A package whose <c>__init__.py</c> star-imports its modules gives their names to its importers, and a star import of a module
    /// that star-imports another takes that one's names too, however the star imports come round: each file is walked once.
    /// </summary>
    [Fact]
    public void APackageThatStarImportsItsModules_GivesTheirNames_AStarOfAStarAmongThem()
    {
        var linked = Link(
            ("pkg/__init__.py", Read("pkg/__init__.py", "from .models import *\n")),
            ("pkg/models.py", Read("pkg/models.py", "class Model:\n    pass\n")),
            ("pkg/a.py", Read("pkg/a.py", "from .b import *\n\nfound = foo()\n")),
            ("pkg/b.py", Read("pkg/b.py", "from .c import *\n")),
            ("pkg/c.py", Read("pkg/c.py", "from .a import *\n\n\ndef foo(): ...\n")),
            ("app.py", Read("app.py", "from pkg import Model\n\nmade = Model()\n")));

        Assert.Contains(new CodeEdge("python:app.made", "python:pkg.models.Model", CodeEdgeKinds.Instantiates, 3, CodeConfidences.Unique), linked.Edges);
        Assert.Equal(
            [new CodeEdge("python:pkg.a.found", "python:pkg.c.foo", CodeEdgeKinds.Calls, 3, CodeConfidences.Unique)],
            linked.Edges.Where(edge => edge.From == "python:pkg.a.found"));

        // What a module with no literal __all__ publishes is its own names and what its own * imports take.
        var starred = Read("pkg/b.py", "from .c import *\n");

        Assert.True(starred.PublishesTaken);
        Assert.Empty(starred.Published!);
        Assert.False(Read("pkg/b.py", "from .c import *\n__all__ = [\"foo\"]\n").PublishesTaken);
    }

    /// <summary>
    /// A star import takes a name the module publishes under another name than the declaration's - one it imports renamed - by the name
    /// it publishes, with no <c>__all__</c> and with one that names it.
    /// </summary>
    [Fact]
    public void AStarImport_TakesANameAModuleImportsUnderAnother_ByTheNameItPublishes()
    {
        var linked = Link(
            ("pkg/__init__.py", Read("pkg/__init__.py", string.Empty)),
            ("pkg/lines.py", Read("pkg/lines.py", "class Line:\n    pass\n")),
            ("pkg/cart.py", Read("pkg/cart.py", "from .lines import Line as CartLine\n")),
            ("pkg/listed.py", Read("pkg/listed.py", "from .lines import Line as ListedLine\n\n__all__ = [\"ListedLine\"]\n")),
            ("app.py", Read("app.py", "from pkg.cart import *\nfrom pkg.listed import *\n\nfirst = CartLine()\nsecond = ListedLine()\n")));

        Assert.Contains(new CodeEdge("python:app.first", "python:pkg.lines.Line", CodeEdgeKinds.Instantiates, 4, CodeConfidences.Unique), linked.Edges);
        Assert.Contains(new CodeEdge("python:app.second", "python:pkg.lines.Line", CodeEdgeKinds.Instantiates, 5, CodeConfidences.Unique), linked.Edges);
    }

    /// <summary>
    /// A fixture is found as pytest finds one: the test's own module's, else the nearest <c>conftest.py</c>'s of its directory and of
    /// each above it that declares it - an override in a deeper one shadowing the one above - and never a sibling directory's; a
    /// fixture's own parameters name fixtures too, and one that names the fixture's own name is given the one a level out, which the
    /// fixture overrides - a conftest.py's the one above it, a test module's its conftest.py's - and never the fixture itself.
    /// </summary>
    [Fact]
    public void AFixture_IsTheNearestConftestsOfTheTestsDirectoryOrOneAbove_AndNoSiblings()
    {
        const string client = "import pytest\n\n\n@pytest.fixture\ndef client():\n    return 1\n";
        var linked = Link(
            ("tests/conftest.py", Read("tests/conftest.py", "import pytest\n\n\n@pytest.fixture\ndef username():\n    return \"outer\"\n")),
            ("tests/sub/conftest.py", Read("tests/sub/conftest.py", "import pytest\n\n\n@pytest.fixture\ndef username(username):\n    return \"inner-\" + username\n\n\n@pytest.fixture\ndef user(username):\n    return username\n")),
            ("tests/sub/test_names.py", Read("tests/sub/test_names.py", "import pytest\n\n\ndef test_name(username):\n    assert username\n\n\n@pytest.fixture\ndef user(user):\n    return user\n\n\ndef test_user(user):\n    assert user\n")),
            ("tests/test_top.py", Read("tests/test_top.py", "def test_name(username):\n    assert username\n")),
            ("tests/api/conftest.py", Read("tests/api/conftest.py", client)),
            ("tests/web/conftest.py", Read("tests/web/conftest.py", client)),
            ("tests/api/test_api.py", Read("tests/api/test_api.py", "def test_get(client):\n    assert client\n")));

        IEnumerable<CodeEdge> Fixtures(string from) => linked.Edges.Where(edge => edge.From == from && edge.Kind == CodeEdgeKinds.References);

        Assert.Equal([new CodeEdge("python:tests.sub.test_names.test_name", "python:tests.sub.conftest.username", CodeEdgeKinds.References, 4, CodeConfidences.Exact)], Fixtures("python:tests.sub.test_names.test_name"));
        Assert.Equal([new CodeEdge("python:tests.sub.test_names.test_user", "python:tests.sub.test_names.user", CodeEdgeKinds.References, 13, CodeConfidences.Exact)], Fixtures("python:tests.sub.test_names.test_user"));
        Assert.Equal([new CodeEdge("python:tests.sub.test_names.user", "python:tests.sub.conftest.user", CodeEdgeKinds.References, 9, CodeConfidences.Exact)], Fixtures("python:tests.sub.test_names.user"));
        Assert.Equal([new CodeEdge("python:tests.sub.conftest.username", "python:tests.conftest.username", CodeEdgeKinds.References, 5, CodeConfidences.Exact)], Fixtures("python:tests.sub.conftest.username"));
        Assert.Equal([new CodeEdge("python:tests.sub.conftest.user", "python:tests.sub.conftest.username", CodeEdgeKinds.References, 10, CodeConfidences.Exact)], Fixtures("python:tests.sub.conftest.user"));
        Assert.Equal([new CodeEdge("python:tests.test_top.test_name", "python:tests.conftest.username", CodeEdgeKinds.References, 1, CodeConfidences.Exact)], Fixtures("python:tests.test_top.test_name"));
        Assert.Equal([new CodeEdge("python:tests.api.test_api.test_get", "python:tests.api.conftest.client", CodeEdgeKinds.References, 1, CodeConfidences.Exact)], Fixtures("python:tests.api.test_api.test_get"));
    }

    /// <summary>
    /// A bare import is looked for under the roots of the layout too, after the directories around the file: the directory an
    /// outermost package stands in - <c>src/</c> of a project's packages, which no test's directory is above - and each a
    /// <c>pyproject.toml</c> names, as pytest's <c>pythonpath</c> does; one root that holds the module is it, several are each a
    /// candidate.
    /// </summary>
    [Fact]
    public void ABareImport_IsLookedForUnderTheRootsOfTheLayout_AnOutermostPackagesAndAProjectFilesAlike()
    {
        var project = new PyprojectExtractor().Extract(new CodeFileInput("pyproject.toml", "[tool.pytest.ini_options]\npythonpath = [\"lib\"]\n"));
        var linked = CodeGraphLinker.Link(
        [
            Py("src/shop/__init__.py", string.Empty),
            Py("src/shop/geometry.py", "def largest(xs): ...\n"),
            Py("tests/test_geometry.py", "from shop.geometry import largest\n\n\ndef test_largest():\n    assert largest([1])\n"),
            Py("lib/helpers.py", "def pack(): ...\n"),
            Py("tests/test_helpers.py", "import helpers\n\n\ndef test_pack():\n    helpers.pack()\n"),
            Py("src/util/__init__.py", string.Empty),
            Py("lib/util/__init__.py", string.Empty),
            Py("tests/test_util.py", "import util\n"),
            new CodeGraphFile("pyproject.toml", "pyproject", project),
        ]);

        Assert.Equal(["lib"], project.LayoutRoots!);
        Assert.Contains(new CodeEdge("file:tests/test_geometry.py", "file:src/shop/geometry.py", CodeEdgeKinds.Imports, 1, CodeConfidences.Unique), linked.Edges);
        Assert.Contains(new CodeEdge("python:tests.test_geometry.test_largest", "python:src.shop.geometry.largest", CodeEdgeKinds.Tests, 5, CodeConfidences.Unique), linked.Edges);
        Assert.Contains(new CodeEdge("file:tests/test_helpers.py", "file:lib/helpers.py", CodeEdgeKinds.Imports, 1, CodeConfidences.Unique), linked.Edges);
        Assert.Contains(new CodeEdge("python:tests.test_helpers.test_pack", "python:lib.helpers.pack", CodeEdgeKinds.Tests, 5, CodeConfidences.Exact), linked.Edges);
        Assert.Equal(
            [new CodeEdge("file:tests/test_util.py", "file:lib/util/__init__.py", CodeEdgeKinds.Imports, 1, CodeConfidences.Candidates), new CodeEdge("file:tests/test_util.py", "file:src/util/__init__.py", CodeEdgeKinds.Imports, 1, CodeConfidences.Candidates)],
            linked.Edges.Where(edge => edge.From == "file:tests/test_util.py" && edge.Kind == CodeEdgeKinds.Imports).OrderBy(edge => edge.To, StringComparer.Ordinal));

        // A module under several roots is an import of several places, the sign of the one limit that says so.
        Assert.Equal([CodeGraphLimit.CandidateImports], linked.Signs["file:tests/test_util.py"]);
        Assert.Equal(["ambiguous-imports"], new PythonExtractor().Help.DoesNotSee.Where(limit => limit.Signs.Contains(CodeGraphLimit.CandidateImports)).Select(limit => limit.Name));
    }

    /// <summary>
    /// An <c>__all__</c> of string literals is a list or a tuple - in parentheses or bare - and nothing else: a string in parentheses is
    /// a string, and an <c>__all__</c> added to or taken away - by an f-string's field's code as by any - is one the code changes, marked,
    /// the module's public names published for it.
    /// </summary>
    [Fact]
    public void AnAllOfLiterals_IsAListOrATuple_AndOneTheCodeChangesIsMarked()
    {
        const string declared = "\n\nclass Cart: ...\nclass Line: ...\n";

        Assert.Equal(["Cart"], Read("pkg/m.py", "__all__ = (\"Cart\",)" + declared).Published!);
        Assert.Equal(["Cart", "Line"], Read("pkg/m.py", "__all__ = \"Cart\", \"Line\"" + declared).Published!);
        Assert.Empty(Read("pkg/m.py", "__all__ = ()" + declared).Published!);
        Assert.All(new[] { "__all__ = (\"Cart\",)", "__all__ = \"Cart\", \"Line\"", "__all__ = ()" }, all => Assert.Empty(Read("pkg/m.py", all + declared).Dynamic));

        foreach (var (all, line) in new[] { ("__all__ = (\"Line\")", 1), ("__all__ = [\"Cart\"]\n__all__ += [\"Line\"]", 2), ("__all__ = [\"Cart\"]\ndel __all__", 2), ("__all__ = [\"Cart\"]\n_said = f\"{__all__.append('Line')}\"", 2) })
        {
            var changed = Read("pkg/m.py", all + declared);

            Assert.Equal(["Cart", "Line"], changed.Published!);
            Assert.Equal([(line, PythonReading.ComputedExports)], changed.Dynamic.Select(mark => (mark.Line, mark.Kind)));
        }
    }

    /// <summary>
    /// An import in a function binds for that function's code alone, and one in a class for the class's own - its body's and its
    /// fields' values, never a method's: neither is a name of the module, published or reached through it by an importer.
    /// </summary>
    [Fact]
    public void AnImportInAFunctionOrAClass_BindsForItsOwnCodeAlone()
    {
        var service = Read("pkg/service.py", "def run():\n    from .heavy import Engine\n    return Engine()\n\n\nclass Garage:\n    from .heavy import Engine\n    kept = Engine()\n\n    def start(self):\n        return Engine()\n");
        var linked = Link(
            ("pkg/__init__.py", Read("pkg/__init__.py", string.Empty)),
            ("pkg/heavy.py", Read("pkg/heavy.py", "class Engine:\n    pass\n")),
            ("pkg/service.py", service),
            ("app.py", Read("app.py", "from pkg.service import Engine\nfrom pkg.service import *\n\nmade = Engine()\n")));

        Assert.Equal(["run", "Garage"], service.Published!);
        Assert.DoesNotContain(service.Imports, import => import.Reexports);
        Assert.Contains(new CodeEdge("python:pkg.service.run", "python:pkg.heavy.Engine", CodeEdgeKinds.Instantiates, 3, CodeConfidences.Exact), linked.Edges);
        Assert.Contains(new CodeEdge("python:pkg.service.Garage.kept", "python:pkg.heavy.Engine", CodeEdgeKinds.Instantiates, 8, CodeConfidences.Exact), linked.Edges);
        Assert.DoesNotContain(linked.Edges, edge => edge.From is "python:app.made" or "python:pkg.service.Garage.start" && edge.To == "python:pkg.heavy.Engine");
    }

    /// <summary>
    /// A link made again over Python files is the whole link: packages come around a module, whose bare import is then looked for
    /// from above them.
    /// </summary>
    [Fact]
    public void ALinkMadeAgain_FindsAgainAnImportWhosePackagesCame()
    {
        CodeGraphFile[] before = [Py("pkg/sub/mod.py", "import helper\n\nhelper.pack()\n"), Py("pkg/sub/helper.py", "def pack(): ...\n"), Py("helper.py", "def pack(): ...\n")];
        CodeGraphFile[] after = [.. before, Py("pkg/__init__.py", string.Empty), Py("pkg/sub/__init__.py", string.Empty)];
        var again = CodeGraphLinker.Relink(CodeGraphLinker.Link(before), after);

        Assert.Equal(CodeGraphLinker.Link(after).Edges, again.Edges);
        Assert.Equal(
            [new CodeEdge("python:pkg.sub.mod", "python:helper.pack", CodeEdgeKinds.Calls, 3, CodeConfidences.Exact)],
            again.Edges.Where(edge => edge.From == "python:pkg.sub.mod" && edge.Kind == CodeEdgeKinds.Calls));
    }

    /// <summary>
    /// Each name a target list writes at the top is a declaration of its own - <c>a, b = ...</c>, <c>(a, b) = ...</c> - a constant in
    /// capitals, each given its element of the value where the two are as long, and one the module publishes; <c>type X[K] = ...</c> is a
    /// type alias whose type parameter is its own and names no type; and <c>match</c>, a soft keyword, is a name where it heads no
    /// statement of its own - its lambda's parameter the lambda's.
    /// </summary>
    [Fact]
    public void ATargetList_ATypeAlias_AndASoftKeyword_DeclareTheirNames()
    {
        var forms = Read("pkg/forms.py", Forms);

        Assert.Equal(
            [("HOST", "constant", 10), ("PORT", "constant", 10), ("LEFT", "constant", 11), ("RIGHT", "constant", 11), ("match", "field", 13), ("LIMIT", "constant", 14)],
            forms.Nodes.Where(node => node.Parent?.Whole() == "python:pkg.forms" && node.Kind == CodeNodeKinds.Member && node.SubKind is "constant" or "field").Select(node => (node.Name, node.SubKind!, node.Start)));
        Assert.Equal(
            ["HOST, PORT =", "HOST, PORT =", "(LEFT, RIGHT) =", "(LEFT, RIGHT) ="],
            forms.Nodes.Where(node => node.Name is "HOST" or "PORT" or "LEFT" or "RIGHT").Select(node => node.Signature));
        Assert.Equal(
            [("localhost", "python:pkg.forms.HOST", 10), ("left", "python:pkg.forms.LEFT", 11), ("right", "python:pkg.forms.RIGHT", 11)],
            (forms.Literals ?? []).Where(literal => literal.Value is "localhost" or "left" or "right").Select(literal => (literal.Value, Assert.Single(literal.Places).Node!.Whole(), literal.Places[0].Line)));
        Assert.Equal(["HOST", "PORT", "LEFT", "RIGHT", "Pair", "match", "LIMIT"], forms.Published!.Where(name => name is "HOST" or "PORT" or "LEFT" or "RIGHT" or "Pair" or "match" or "LIMIT"));

        var pair = Node(forms, "python:pkg.forms.Pair");
        Assert.Equal((CodeNodeKinds.Type, "alias", "type Pair[K] ="), (pair.Kind, pair.SubKind, pair.Signature));
        Assert.Equal([("Price", CodeEdgeKinds.References, 12, CodeQualifierKinds.Prefix)], References(forms, "python:pkg.forms.Pair"));
        Assert.DoesNotContain(forms.References, reference => reference.Name is "K" or "T");
        Assert.Equal([("strip", CodeEdgeKinds.Calls, 13, CodeQualifierKinds.Value)], References(forms, "python:pkg.forms.match"));

        // A function's type parameters are its own; a head the reader cannot read is marked so, its lines the code around it.
        var generic = Read("pkg/generic.py", "def first[T](items: list[T]) -> T:\n    return items[0]\n");
        Assert.Equal(("function", 1, 1), (Node(generic, "python:pkg.generic.first").SubKind!, Node(generic, "python:pkg.generic.first").MinArity!.Value, Node(generic, "python:pkg.generic.first").MaxArity!.Value));
        Assert.Empty(generic.References);
        var broken = Read("pkg/broken.py", "def (x):\n    pass\nclass :\n    pass\n");
        Assert.Equal(
            [(1, "a def whose head the reader cannot read: its lines read as the code around it", PythonReading.UnreadHead), (3, "a class whose head the reader cannot read: its lines read as the code around it", PythonReading.UnreadHead)],
            broken.Dynamic.Select(mark => (mark.Line, mark.Why, mark.Kind)));
        CodeGraphContract.Signed(new PythonExtractor(), broken, "pkg/broken.py");

        // A soft keyword is a name wherever it heads no statement of its own: a function called match, and a field named case.
        var soft = Read("pkg/soft.py", "def match(rule, path):\n    return rule\n\n\ndef route(path):\n    return match(RULES, path)\n\n\ncase: int = 2\nmatch(RULES, \"x\")\n");
        Assert.Equal([("match", CodeEdgeKinds.Calls, 6, (string?)null), ("RULES", CodeEdgeKinds.Uses, 6, (string?)null)], References(soft, "python:pkg.soft.route"));
        Assert.Equal([2], soft.References.Where(reference => reference.From == "python:pkg.soft.route" && reference.Name == "match").Select(reference => reference.Arity));
        Assert.Equal(("field", 9), (Node(soft, "python:pkg.soft.case").SubKind, Node(soft, "python:pkg.soft.case").Start));
        Assert.Equal([("match", CodeEdgeKinds.Calls, 10, (string?)null), ("RULES", CodeEdgeKinds.Uses, 10, (string?)null)], References(soft, "python:pkg.soft"));
    }

    /// <summary>
    /// A decorator is read whole, where the function is defined: a modifier by its dotted name, and code - what it reads and is given,
    /// a route's path and a parameter set's name among its literals - the name it calls with the function a call, of one argument where
    /// it is written alone; one an import of a package binds a probe (<see cref="ExtractReference.Probe"/>), one the repository's own
    /// code binds a call of it, one the language answers to nothing; each made around the function (<see cref="ExtractReference.Around"/>),
    /// as its annotations are, and its body's code none of them. No decorator is told by the name it is written with.
    /// </summary>
    [Fact]
    public void ADecorator_IsReadWhole_WhatItIsGivenTheFunctionsCode()
    {
        var forms = Read("pkg/forms.py", Forms);
        var orders = "python:pkg.forms.orders";

        Assert.Equal(["@app.route", "@retry", "@functools.wraps"], Node(forms, orders).Modifiers);
        Assert.Equal(
            [
                ("app", CodeEdgeKinds.Uses, 26, (string?)null), ("route", CodeEdgeKinds.Calls, 26, CodeQualifierKinds.Value), ("METHODS", CodeEdgeKinds.Uses, 26, (string?)null),
                ("retry", CodeEdgeKinds.Calls, 27, (string?)null), ("LIMIT", CodeEdgeKinds.Uses, 27, (string?)null),
                ("wraps", CodeEdgeKinds.Calls, 28, CodeQualifierKinds.Prefix), ("open", CodeEdgeKinds.Uses, 28, (string?)null),
                ("Receipt", CodeEdgeKinds.References, 29, CodeQualifierKinds.Prefix), ("Order", CodeEdgeKinds.References, 29, CodeQualifierKinds.Prefix),
                ("User", CodeEdgeKinds.References, 29, CodeQualifierKinds.Prefix),
                ("open", CodeEdgeKinds.Calls, 30, (string?)null),
            ],
            References(forms, orders));
        Assert.Equal(
            [(true, false), (true, false), (true, false), (true, false), (true, false), (true, true), (true, false), (true, false), (true, false), (true, false), (false, false)],
            forms.References.Where(reference => reference.From == orders).Select(reference => (reference.Around, reference.Probe)));
        Assert.Equal([2, 1, 1], forms.References.Where(reference => reference.From == orders && reference.Kind == CodeEdgeKinds.Calls && reference.Line < 30).Select(reference => reference.Arity!.Value));
        Assert.Equal([(orders, 26)], Places(forms, "/orders"));
        Assert.Equal([(orders, 30)], Places(forms, "orders"));

        // pytest's own: a call of what the import of the package reaches, a probe; a fixture's parameters and a parameter set's name and
        // values, each the decorated function's code; a fixture's own parameter a fixture it requires; a name the parameter set gives no
        // fixture.
        var sizes = Read("tests/test_sizes.py", """
            import pytest

            SIZES = [1, 2]
            CASES = ["a"]


            @pytest.fixture(params=CASES)
            def case(request):
                return request.param


            @pytest.mark.parametrize("size", SIZES)
            def test_size(size, case):
                assert size
            """);

        Assert.Equal(CodeSubKinds.Fixture, Node(sizes, "python:tests.test_sizes.case").SubKind);
        Assert.Equal(["@pytest.fixture"], Node(sizes, "python:tests.test_sizes.case").Modifiers);
        Assert.Equal(
            [
                ("fixture", CodeEdgeKinds.Calls, 7, CodeQualifierKinds.Prefix), ("CASES", CodeEdgeKinds.Uses, 7, (string?)null),
                ("request", CodeEdgeKinds.References, 8, CodeQualifierKinds.Fixture), ("param", CodeEdgeKinds.Uses, 9, CodeQualifierKinds.Value),
            ],
            References(sizes, "python:tests.test_sizes.case"));
        Assert.Equal(["@pytest.mark.parametrize"], Node(sizes, "python:tests.test_sizes.test_size").Modifiers);
        Assert.Equal(
            [
                ("mark", CodeEdgeKinds.Uses, 12, CodeQualifierKinds.Prefix), ("parametrize", CodeEdgeKinds.Calls, 12, CodeQualifierKinds.Type),
                ("SIZES", CodeEdgeKinds.Uses, 12, (string?)null), ("case", CodeEdgeKinds.References, 13, CodeQualifierKinds.Fixture),
            ],
            References(sizes, "python:tests.test_sizes.test_size"));
        Assert.Equal(["fixture", "parametrize"], sizes.References.Where(reference => reference.Probe).Select(reference => reference.Name));
        Assert.Equal([("python:tests.test_sizes.test_size", 12)], Places(sizes, "size"));

        // A decorator the repository declares, named like a framework's, is a call of it where the file imports it: an edge from what
        // it decorates. A builtin decorator names nothing; one a local of a function around it binds is a call through its value.
        var email = Read("jobs/email.py", "from jobs.registry import task\nfrom .registry import task as job\n\n\n@task\ndef send_receipt():\n    pass\n\n\n@job\ndef send_reminder():\n    pass\n\n\ndef make(wrap):\n    @wrap\n    @property\n    def inner():\n        pass\n    return inner\n");
        var linked = Link(
            ("jobs/__init__.py", Read("jobs/__init__.py", string.Empty)),
            ("jobs/registry.py", Read("jobs/registry.py", "REGISTRY = []\n\n\ndef task(fn):\n    REGISTRY.append(fn)\n    return fn\n")),
            ("jobs/email.py", email));

        Assert.Equal(
            [("python:jobs.email.send_receipt", "python:jobs.registry.task", 5, CodeConfidences.Unique), ("python:jobs.email.send_reminder", "python:jobs.registry.task", 10, CodeConfidences.Exact)],
            linked.Edges.Where(edge => edge.From.StartsWith("python:jobs.email.", StringComparison.Ordinal) && edge.Kind == CodeEdgeKinds.Calls).Select(edge => (edge.From, edge.To, edge.Line, edge.Confidence)));
        Assert.Equal([(true, true), (true, false)], email.References.Where(reference => reference.Kind == CodeEdgeKinds.Calls).Select(reference => (reference.Around, reference.Probe)));
        Assert.Equal([(16, PythonReading.FunctionValue)], email.Dynamic.Select(mark => (mark.Line, mark.Kind)));
    }

    /// <summary>
    /// A static method takes no receiver: every parameter it declares is one a call gives; a class method's <c>cls</c>, as a method's
    /// <c>self</c>, is taken off.
    /// </summary>
    [Fact]
    public void AStaticMethod_CountsEachParameter_AndAClassMethodTakesItsReceiverOff()
    {
        var forms = Read("pkg/forms.py", Forms);

        Assert.Equal(
            [("__init__", 1, 2), ("read", 1, 2), ("empty", 1, 1), ("put", 2, 2), ("kind", 1, 1)],
            forms.Nodes.Where(node => node.Parent?.Whole() == "python:pkg.forms.Basket" && node.SubKind is "method" or "constructor").Select(node => (node.Name, node.MinArity!.Value, node.MaxArity!.Value)));
    }

    /// <summary>
    /// An attribute a method first assigns through <c>self</c> is a field of its class - a constant where written in capitals - over the
    /// statement's line, held by the class and declared in its scope; one the class declares already, or a later assignment of it, is a
    /// use of that one, and so is each member a chain passes on its way, read through the receiver at its head.
    /// </summary>
    [Fact]
    public void AnAttributeAMethodAssigns_IsAFieldOfItsClass()
    {
        var forms = Read("pkg/forms.py", Forms);

        Assert.Equal(
            [("RATE", "constant", 34), ("items", "field", 38), ("weight", "field", 39), ("MAX", "constant", 40)],
            forms.Nodes.Where(node => node.Parent?.Whole() == "python:pkg.forms.Basket" && node.SubKind is "constant" or "field").Select(node => (node.Name, node.SubKind!, node.Start)));
        Assert.All(
            forms.Nodes.Where(node => node.Name is "items" or "weight" or "MAX"),
            node => Assert.Equal((QualifiedName.Of("pkg.forms.Basket"), "self." + node.Name + " ="), (node.Scope, node.Signature)));
        Assert.DoesNotContain(References(forms, "python:pkg.forms.Basket.__init__"), reference => reference.Kind == CodeEdgeKinds.Uses && reference.Name is "items" or "weight" or "MAX");

        // put: a member self.items passes on its way to append, a field it adds to, the member del takes an item of.
        Assert.Equal(
            [
                ("items", CodeEdgeKinds.Uses, 51, CodeQualifierKinds.This), ("append", CodeEdgeKinds.Calls, 51, CodeQualifierKinds.Value),
                ("weight", CodeEdgeKinds.Uses, 52, CodeQualifierKinds.This), ("grams", CodeEdgeKinds.Uses, 52, CodeQualifierKinds.Value),
                ("items", CodeEdgeKinds.Uses, 53, CodeQualifierKinds.This), ("weigh", CodeEdgeKinds.Calls, 54, (string?)null),
            ],
            References(forms, "python:pkg.forms.Basket.put").Where(reference => reference.Line > 50));
    }

    /// <summary>
    /// The head and each member of a chain are read on the way to its last name: a name the file declares or imports at its head as a
    /// name alone is - an imported one a use, whatever it names - and a member through what the head is: one rule for every chain whose
    /// head an import binds, the first member through the prefix of the import, each after it through the type the names before it stand
    /// for in the module; a member of <c>self</c> through the type's own, each after it a value by no name. A local, a receiver and a
    /// module's name at its head are no use. A chain a call's result reaches is read whole from that call, the first through the base
    /// where <c>super()</c> reached it.
    /// </summary>
    [Fact]
    public void AChainsHeadAndMembers_AreReadOnTheWayToItsLast()
    {
        var extract = Read("pkg/chains.py", """
            from .config import CONFIG
            import shop.store as store


            class Order:
                def total(self):
                    return CONFIG.prices.get(self.customer.name)

                def save(self):
                    store.session.commit()
                    return super().save().done.value
            """);

        Assert.Equal(
            [
                ("CONFIG", CodeEdgeKinds.Uses, 7, (string?)null), ("prices", CodeEdgeKinds.Uses, 7, CodeQualifierKinds.Prefix), ("get", CodeEdgeKinds.Calls, 7, CodeQualifierKinds.Type),
                ("customer", CodeEdgeKinds.Uses, 7, CodeQualifierKinds.This), ("name", CodeEdgeKinds.Uses, 7, CodeQualifierKinds.Value),
            ],
            References(extract, "python:pkg.chains.Order.total").Where(reference => reference.Kind != CodeEdgeKinds.Overrides));
        Assert.Equal(
            ["CONFIG", "CONFIG.prices", "store", "store.session"],
            extract.References.Where(reference => reference.Name is "prices" or "get" or "session" or "commit").Select(reference => extract.Written(reference.Qualifier)));
        Assert.Equal(
            [
                ("session", CodeEdgeKinds.Uses, 10, CodeQualifierKinds.Prefix), ("commit", CodeEdgeKinds.Calls, 10, CodeQualifierKinds.Type),
                ("save", CodeEdgeKinds.Calls, 11, CodeQualifierKinds.Base), ("done", CodeEdgeKinds.Uses, 11, CodeQualifierKinds.Value),
                ("value", CodeEdgeKinds.Uses, 11, CodeQualifierKinds.Value),
            ],
            References(extract, "python:pkg.chains.Order.save").Where(reference => reference.Kind != CodeEdgeKinds.Overrides));
    }

    /// <summary>
    /// A <c>case</c>'s pattern is read as Python reads one: a class pattern's class a reference, never a call; a dotted value the body's
    /// code, a use of what it names, through the import its head is; a literal the body's; a keyword pattern's attribute name nothing;
    /// and each capture - a name alone, one after <c>*</c>, a keyword pattern's value - bound by the body, no use. What a pattern and an
    /// <c>except</c> name is read where the body's run reaches it, in the order the text writes it.
    /// </summary>
    [Fact]
    public void ACasesPattern_NamesItsClass_UsesItsValues_AndBindsItsCaptures()
    {
        var forms = Read("pkg/forms.py", Forms);

        Assert.Equal(
            [
                ("kind", CodeEdgeKinds.Overrides, 56, CodeQualifierKinds.Base),
                ("Status", CodeEdgeKinds.Uses, 58, (string?)null), ("PAID", CodeEdgeKinds.Uses, 58, CodeQualifierKinds.Prefix),
                ("Refund", CodeEdgeKinds.References, 60, (string?)null),
                ("Order", CodeEdgeKinds.References, 65, CodeQualifierKinds.Prefix), ("items", CodeEdgeKinds.Uses, 65, CodeQualifierKinds.This),
                ("OrderError", CodeEdgeKinds.References, 66, CodeQualifierKinds.Prefix),
            ],
            References(forms, "python:pkg.forms.Basket.kind"));
        Assert.Equal([("python:pkg.forms.Basket.kind", 59)], Places(forms, "paid"));
        Assert.Equal([("python:pkg.forms.Basket.kind", 61)], Places(forms, "-"));
        Assert.DoesNotContain(forms.References, reference => reference.Name is "amount" or "first" or "rest" or "error" or "status");
    }

    /// <summary>
    /// A type the code names is read by one reader whatever names it - an annotation, a return annotation, a string annotation, an
    /// <c>isinstance</c>'s, an <c>except</c>'s, a class pattern's, a base's subscript, a type parameter's bound: a dotted name through
    /// the import its head is, a string through the file's imports - one renamed by its own name - and <c>Literal</c>'s values and
    /// <c>Annotated</c>'s metadata the code they are, the literals among them the declaring node's. What an <c>isinstance</c> tests is
    /// code; what it tests against, where written as anything but types, is code too.
    /// </summary>
    [Fact]
    public void ATypeTheCodeNames_IsReadByOneReader_DottedQuotedOrGivenValues()
    {
        var forms = Read("pkg/forms.py", Forms);

        Assert.Equal(
            [("Item", CodeEdgeKinds.References, 33, CodeQualifierKinds.Prefix), ("Base", CodeEdgeKinds.Inherits, 33, CodeQualifierKinds.Prefix)],
            References(forms, "python:pkg.forms.Basket").Where(reference => reference.Line == 33));
        Assert.Equal(
            [
                ("Literal", CodeEdgeKinds.References, 50, (string?)null), ("Annotated", CodeEdgeKinds.References, 50, (string?)null),
                ("Positive", CodeEdgeKinds.Instantiates, 50, (string?)null), ("LIMIT", CodeEdgeKinds.Uses, 50, (string?)null),
            ],
            References(forms, "python:pkg.forms.Basket.put").Where(reference => reference.Line == 50 && reference.Kind != CodeEdgeKinds.Overrides));
        Assert.Equal([("python:pkg.forms.Basket.put", 50)], Places(forms, "tea"));
        Assert.Equal([("python:pkg.forms.Basket.put", 50)], Places(forms, "cake"));

        // isinstance: the value tested read as code, the type it is tested against named; written as a call, the call is code.
        var tested = Read("pkg/tested.py", """
            import shop.models as models


            def check(value, kinds):
                return isinstance(value.inner, (models.Cart, Line)) or isinstance(value, kinds())
            """);

        Assert.Equal(
            [
                ("Cart", CodeEdgeKinds.References, 5, CodeQualifierKinds.Prefix), ("Line", CodeEdgeKinds.References, 5, (string?)null),
                ("inner", CodeEdgeKinds.Uses, 5, CodeQualifierKinds.Value),
            ],
            References(tested, "python:pkg.tested.check"));
        Assert.Equal(["a call through the function value kinds"], tested.Dynamic.Select(mark => mark.Why));
    }

    /// <summary>
    /// <c>async for</c> and <c>async with</c> head compound statements whose suites are read as statements - a function declared in one
    /// a declaration, its name no call of the code around it - and a function's body is a scope of its own: a closure sees the names the
    /// function around it binds, a lambda's parameters and a comprehension's targets are their own, and a lambda's default is the code
    /// around it.
    /// </summary>
    [Fact]
    public void AsyncForAndWith_HeadTheirSuites_AndAClosureSeesTheNamesAroundIt()
    {
        var forms = Read("pkg/forms.py", Forms);
        var fetch = Node(forms, "python:pkg.forms.crawl.fetch");

        Assert.Equal((QualifiedName.Of("pkg.forms.crawl"), "python:pkg.forms.crawl"), (fetch.Scope, fetch.Parent!.Whole()));
        Assert.Equal(["async"], fetch.Modifiers);
        Assert.Equal(
            [
                ("session", CodeEdgeKinds.Calls, 71, (string?)null), ("rows", CodeEdgeKinds.Calls, 74, (string?)null), ("fetch", CodeEdgeKinds.Calls, 76, (string?)null),
                ("LIMIT", CodeEdgeKinds.Uses, 76, (string?)null), ("size", CodeEdgeKinds.Uses, 76, CodeQualifierKinds.Value),
            ],
            References(forms, "python:pkg.forms.crawl"));
        Assert.Equal([("get", CodeEdgeKinds.Calls, 73, CodeQualifierKinds.Value)], References(forms, "python:pkg.forms.crawl.fetch"));
        Assert.Equal([("pairs", CodeEdgeKinds.Calls, 80, (string?)null)], References(forms, "python:pkg.forms.weigh"));
        Assert.Equal(
            [
                new ExtractScope(QualifiedName.Of("pkg.forms.crawl"), false) { Parent = QualifiedName.Of("pkg.forms") },
                new ExtractScope(QualifiedName.Of("pkg.forms.crawl.fetch"), false) { Parent = QualifiedName.Of("pkg.forms.crawl") },
            ],
            forms.Scopes.Where(scope => scope.Name.Whole().StartsWith("pkg.forms.crawl", StringComparison.Ordinal)));

        // The review's own case: a def inside an async for is a declaration, its parameter no use of the function around it.
        var jobs = Read("pkg/jobs.py", "async def run(jobs):\n    async for job in jobs:\n        def handle(item):\n            return item\n        handle(job)\n");
        Assert.Equal(["python:pkg.jobs", "python:pkg.jobs.run", "python:pkg.jobs.run.handle"], jobs.Nodes.Select(node => node.Id.Whole()));
        Assert.Equal([("handle", CodeEdgeKinds.Calls, 5, (string?)null)], References(jobs, "python:pkg.jobs.run"));
    }

    /// <summary>
    /// The module's own code and a class's are what they declare's readers: a name the module assigns read at its top is a use of it, and
    /// a name the class declares read by the class's own code is the class's own, never a base's - a default of a method, read where the
    /// method is defined, sees it as the class body does.
    /// </summary>
    [Fact]
    public void TheModulesCodeAndAClasssCode_ReadTheNamesTheyDeclare()
    {
        var forms = Read("pkg/forms.py", Forms);

        Assert.Equal([("check", CodeEdgeKinds.Calls, 15, (string?)null), ("LIMIT", CodeEdgeKinds.Uses, 15, (string?)null)], References(forms, "python:pkg.forms"));
        Assert.Equal([("stamp", CodeEdgeKinds.Calls, 35, (string?)null), ("RATE", CodeEdgeKinds.Uses, 35, CodeQualifierKinds.This)], References(forms, "python:pkg.forms.Basket").Where(reference => reference.Line > 33));
        Assert.Equal([("__init__", CodeEdgeKinds.Overrides, 37, CodeQualifierKinds.Base), ("RATE", CodeEdgeKinds.Uses, 37, (string?)null)], References(forms, "python:pkg.forms.Basket.__init__"));

        // A settings module: the module reads what it assigns, at any depth of its own code.
        var settings = Read("pkg/settings.py", "DEBUG = env(\"DEBUG\")\nAPPS = [\"core\"]\nif DEBUG:\n    APPS += [\"debug\"]\n");
        Assert.Equal(
            [("env", CodeEdgeKinds.Calls, 1, (string?)null), ("DEBUG", CodeEdgeKinds.Uses, 3, (string?)null), ("APPS", CodeEdgeKinds.Uses, 4, (string?)null)],
            settings.References.Select(reference => (reference.Name, reference.Kind, reference.Line, reference.Qualifier?.Kind)));
    }

    /// <summary>
    /// A builtin's name the file declares or imports is the file's: a call of it a call of what the file binds; one the file binds
    /// nowhere names nothing.
    /// </summary>
    [Fact]
    public void ABuiltinsName_TheFileDeclaresOrImports_IsTheFiles()
    {
        var forms = Read("pkg/forms.py", Forms);

        Assert.Equal([("read", CodeEdgeKinds.Overrides, 43, CodeQualifierKinds.Base), ("format", CodeEdgeKinds.Calls, 44, (string?)null)], References(forms, "python:pkg.forms.Basket.read"));
        Assert.Equal([("read", CodeEdgeKinds.Calls, 48, CodeQualifierKinds.This)], References(forms, "python:pkg.forms.Basket.empty").Where(reference => reference.Kind == CodeEdgeKinds.Calls));
        Assert.DoesNotContain(forms.References, reference => reference.Name is "str" or "print" or "tuple" or "int" or "isinstance");
    }

    /// <summary>
    /// A name in an f-string's field is read as the file reads it anywhere: through the import that renames it, the import a module's
    /// name is, the class the file declares.
    /// </summary>
    [Fact]
    public void ANameInAnFStringsField_IsBoundAsAnywhereInTheFile()
    {
        var extract = Read("pkg/labels.py", """
            from .money import cents_to_text as money
            import shop.units as units


            class Cart:
                LIMIT = 3


            def label(p):
                return f"{money(p.cents)} {units.fmt(p.size)} {Cart.LIMIT}"
            """);

        Assert.Equal(
            [
                ("cents_to_text", CodeEdgeKinds.Calls, 10, CodeQualifierKinds.Prefix), ("cents", CodeEdgeKinds.Uses, 10, CodeQualifierKinds.Value),
                ("fmt", CodeEdgeKinds.Calls, 10, CodeQualifierKinds.Prefix), ("size", CodeEdgeKinds.Uses, 10, CodeQualifierKinds.Value),
                ("Cart", CodeEdgeKinds.References, 10, (string?)null), ("LIMIT", CodeEdgeKinds.Uses, 10, CodeQualifierKinds.Type),
            ],
            References(extract, "python:pkg.labels.label"));
    }

    /// <summary>
    /// A body is read once the module's walk has bound every name: an import written below a function is the function's to read
    /// through, as Python binds a name when the body runs.
    /// </summary>
    [Fact]
    public void ABody_IsReadOnceEveryNameOfTheModuleIsBound()
    {
        var extract = Read("pkg/late.py", "def run():\n    return helper.go()\n\n\nimport pkg.helper as helper\n");

        Assert.Equal([("go", CodeEdgeKinds.Calls, 2, CodeQualifierKinds.Prefix)], References(extract, "python:pkg.late.run"));
    }

    /// <summary>
    /// <c>cls(...)</c> in a class method and <c>type(self)(...)</c> in a method make the class the method's receiver is - an
    /// instantiates of that class's own node, as <c>self.f()</c> is a call through <c>this</c> - and are never a call through a
    /// function value, as a call of any other parameter is; what <c>type(self)</c> is given to make is the method's code.
    /// </summary>
    [Fact]
    public void ACallOfTheReceiversClass_MakesTheClassItsMethodIsOf()
    {
        var extract = Read("pkg/shop.py", "class Cart:\n    @classmethod\n    def empty(cls):\n        return cls()\n\n    def copy(self):\n        return type(self)(self.items)\n\n    def other(self, factory):\n        return factory()\n");
        var linked = Link(("pkg/shop.py", extract));

        Assert.Equal([("empty", CodeEdgeKinds.Overrides, 3, CodeQualifierKinds.Base), ("Cart", CodeEdgeKinds.Instantiates, 4, CodeQualifierKinds.Own)], References(extract, "python:pkg.shop.Cart.empty"));
        Assert.Equal(
            [("copy", CodeEdgeKinds.Overrides, 6, CodeQualifierKinds.Base), ("Cart", CodeEdgeKinds.Instantiates, 7, CodeQualifierKinds.Own), ("items", CodeEdgeKinds.Uses, 7, CodeQualifierKinds.This)],
            References(extract, "python:pkg.shop.Cart.copy"));
        Assert.All(extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Instantiates), reference => Assert.Equal("python:pkg.shop.Cart", reference.Qualifier!.Name!.Whole()));
        Assert.Equal([(10, PythonReading.FunctionValue)], extract.Dynamic.Select(mark => (mark.Line, mark.Kind)));
        Assert.Equal(
            [("python:pkg.shop.Cart.empty", 4, CodeConfidences.Exact), ("python:pkg.shop.Cart.copy", 7, CodeConfidences.Exact)],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Instantiates && edge.To == "python:pkg.shop.Cart").OrderBy(edge => edge.Line).Select(edge => (edge.From, edge.Line, edge.Confidence)));
    }

    /// <summary>
    /// A function's body is a scope of its own whose parent is the module, never its class: a bare name a method reads is the module's,
    /// though its class declares one of that name, and reaches the class's own through <c>self</c> alone; what the method's head runs -
    /// a default - is read where the method is defined, in the class. An attribute <c>__init__</c> assigns through <c>self</c> is the
    /// field another method's <c>self.items</c> reaches.
    /// </summary>
    [Fact]
    public void ABareNameAMethodReads_IsLookedForAroundTheClass_NeverInIt()
    {
        var linked = Link(("pkg/scopes.py", Read("pkg/scopes.py", """
            LIMIT = 9


            class Cart:
                LIMIT = 5

                def __init__(self):
                    self.items = []

                def add(self, item, cap=LIMIT):
                    self.items.append(item)
                    return LIMIT + helper()

                def helper(self):
                    return 0


            def helper():
                return 1
            """)));

        var add = "python:pkg.scopes.Cart.add";

        Assert.Equal(
            [
                (add, "python:pkg.scopes.Cart.LIMIT", CodeEdgeKinds.Uses, 10), (add, "python:pkg.scopes.Cart.items", CodeEdgeKinds.Uses, 11),
                (add, "python:pkg.scopes.helper", CodeEdgeKinds.Calls, 12), (add, "python:pkg.scopes.LIMIT", CodeEdgeKinds.Uses, 12),
            ],
            linked.Edges.Where(edge => edge.From == add && edge.Kind is CodeEdgeKinds.Uses or CodeEdgeKinds.Calls).OrderBy(edge => edge.Line).ThenBy(edge => edge.Kind, StringComparer.Ordinal)
                .Select(edge => (edge.From, edge.To, edge.Kind, edge.Line)));
        Assert.All(linked.Edges.Where(edge => edge.From == add && edge.Kind is CodeEdgeKinds.Uses or CodeEdgeKinds.Calls), edge => Assert.Equal(CodeConfidences.Exact, edge.Confidence));
    }

    /// <summary>
    /// A class of a test file built on a base another file declares may be a test class by it, and says so with each of its <c>test*</c>
    /// methods: the link makes them tests where a <c>TestCase</c> subclass is among its bases any number of levels up - one declared in a
    /// file no runner collects by its name, through a class of another such file that is no test class itself - <c>tests</c> edges from
    /// them, and a method not named <c>test*</c> none. A <c>TestCase</c> subclass is a test class wherever it is declared, and says that
    /// tests are built on it. The marks are kept as an extract is stored, and what a class's head names is made around it.
    /// </summary>
    [Fact]
    public void ATestClassByABaseAnotherFileDeclares_IsATestOnceTheLinkFindsThatBase()
    {
        var suite = Read("tests/test_orders.py", """
            from .support import ShopCase
            from pkg.orders import place


            class OrdersSuite(ShopCase):
                def test_place(self):
                    place()

                def helper(self):
                    place()


            class Deeper(OrdersSuite):
                def test_again(self):
                    place()
            """);

        Assert.Equal(
            [
                ("python:tests.test_orders.OrdersSuite", false, true), ("python:tests.test_orders.OrdersSuite.test_place", false, true),
                ("python:tests.test_orders.OrdersSuite.helper", false, false), ("python:tests.test_orders.Deeper", false, true),
                ("python:tests.test_orders.Deeper.test_again", false, true),
            ],
            suite.Nodes.Skip(1).Select(node => (node.Id.Whole(), node.Test, node.TestByType)));

        var cases = Read("tests/base.py", "import unittest\n\n\nclass BaseCase(unittest.TestCase):\n    def test_ready(self):\n        place()\n");
        var support = Read("tests/support.py", "from .base import BaseCase\n\n\nclass ShopCase(BaseCase):\n    def setUp(self):\n        self.ready = True\n");
        var linked = Link(
            ("tests/base.py", cases),
            ("tests/support.py", support),
            ("tests/test_orders.py", suite),
            ("pkg/orders.py", Read("pkg/orders.py", "def place():\n    return 1\n")));

        Assert.Equal(
            [("python:tests.base.BaseCase", true, true, false), ("python:tests.base.BaseCase.test_ready", true, false, false)],
            cases.Nodes.Skip(1).Select(node => (node.Id.Whole(), node.Test, node.TestBase, node.TestByType)));
        Assert.DoesNotContain(support.Nodes, node => node.Test || node.TestBase || node.TestByType);
        Assert.Equal(
            [("python:tests.test_orders.OrdersSuite.test_place", 7), ("python:tests.test_orders.Deeper.test_again", 15)],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Tests && edge.To == "python:pkg.orders.place").OrderBy(edge => edge.Line).Select(edge => (edge.From, edge.Line)));

        var stored = JsonSerializer.Deserialize<FileExtract>(JsonSerializer.Serialize(suite, JsonStateFile.LineOptions), JsonStateFile.LineOptions)!;
        var storedCases = JsonSerializer.Deserialize<FileExtract>(JsonSerializer.Serialize(cases, JsonStateFile.LineOptions), JsonStateFile.LineOptions)!;
        Assert.Equal(suite.Nodes.Select(node => node.TestByType), stored.Nodes.Select(node => node.TestByType));
        Assert.Equal(cases.Nodes.Select(node => node.TestBase), storedCases.Nodes.Select(node => node.TestBase));
        Assert.Equal(suite.References.Select(reference => reference.Around), stored.References.Select(reference => reference.Around));
        Assert.Equal([true, true], stored.References.Where(reference => reference.Kind == CodeEdgeKinds.Inherits).Select(reference => reference.Around));
    }

    /// <summary>
    /// A test is what a runner collects. A <c>Test*</c> class and a <c>test*</c> function are tests of a pytest file alone - pytest's
    /// default <c>python_files</c>, <c>test_*.py</c> and <c>*_test.py</c> - never of a file unittest's or Django's <c>test*.py</c> names
    /// alone, whose runners collect <c>TestCase</c> subclasses and nothing else: Django's <c>tests.py</c>, a <c>testing.py</c>, a
    /// <c>testclient.py</c> holding a production module's <c>TestClient</c>. A <c>TestCase</c> subclass is a test class wherever it is
    /// declared, as unittest runs one by its type, with its <c>test*</c> methods.
    /// </summary>
    [Fact]
    public void ATestNamedOutsideAPytestFile_IsNoTest()
    {
        const string text = """
            import unittest


            class TestHelper:
                def test_shape(self):
                    pass


            class Case(unittest.TestCase):
                def test_x(self):
                    pass


            def test_free():
                pass
            """;

        foreach (var path in new[] { "shop/client.py", "polls/tests.py", "pkg/testing.py", "pkg/testclient.py", "pkg/test.py" })
        {
            var module = path[..^3].Replace('/', '.');
            Assert.Equal([$"python:{module}.Case", $"python:{module}.Case.test_x"], Read(path, text).Nodes.Where(node => node.Test).Select(node => node.Id.Whole()));
        }

        foreach (var path in new[] { "pkg/test_cart.py", "pkg/cart_test.py" })
        {
            var module = path[..^3].Replace('/', '.');
            Assert.Equal(
                [$"python:{module}.TestHelper", $"python:{module}.TestHelper.test_shape", $"python:{module}.Case", $"python:{module}.Case.test_x", $"python:{module}.test_free"],
                Read(path, text).Nodes.Where(node => node.Test).Select(node => node.Id.Whole()));
        }

        Assert.DoesNotContain(Read("tests/helpers.py", "def test_like():\n    pass\n\n\nclass TestLike:\n    pass\n").Nodes, node => node.Test);
    }

    /// <summary>
    /// A class of a test file built on a base a <c>*</c> import may bind - a name no import of the file binds by itself - may be a test
    /// class by that base, as one built on a base an import binds may: it says so with its <c>test*</c> methods, and the link makes them
    /// tests where it finds a <c>TestCase</c> subclass among its bases; one built on a name the language answers to says nothing.
    /// </summary>
    [Fact]
    public void AClassBuiltOnABaseAStarImportBinds_IsATestOnceTheLinkFindsThatBase()
    {
        var orders = Read("tests/test_orders.py", """
            from .base import *
            from pkg.orders import create


            class OrderTests(APITestBase):
                def test_create(self):
                    create()


            class Plain(object):
                def test_not(self):
                    create()
            """);
        var linked = Link(
            ("tests/base.py", Read("tests/base.py", "import unittest\n\n\nclass APITestBase(unittest.TestCase):\n    pass\n")),
            ("tests/test_orders.py", orders),
            ("pkg/orders.py", Read("pkg/orders.py", "def create():\n    return 1\n")));

        Assert.Equal(
            ["python:tests.test_orders.OrderTests", "python:tests.test_orders.OrderTests.test_create"],
            orders.Nodes.Where(node => node.TestByType).Select(node => node.Id.Whole()));
        Assert.DoesNotContain(orders.Nodes, node => node.Test);
        Assert.Equal(
            [("python:tests.test_orders.OrderTests.test_create", 7)],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Tests).Select(edge => (edge.From, edge.Line)));
    }

    /// <summary>
    /// A name a <c>parametrize</c> mark gives a test is given values in place of the fixture of its name - the test's own mark, its
    /// class's decorator and <c>pytestmark</c>, and the module's <c>pytestmark</c> among a list of marks alike, as pytest reads the marks
    /// of a test and of each node it is collected under - but one the mark's <c>indirect=</c> routes to that fixture, as its
    /// <c>request.param</c>, is still the fixture's: every name where it is <c>True</c>, those a list names, none where it is
    /// <c>False</c>.
    /// </summary>
    [Fact]
    public void AParametrizedName_IsNoFixture_ButOneTheMarkRoutesToIt_AndAClasssAndTheModulesMarksGiveItToo()
    {
        var conftest = Read("tests/conftest.py", """
            import pytest


            @pytest.fixture
            def user(request):
                return request.param


            @pytest.fixture
            def role():
                return 1


            @pytest.fixture
            def db():
                return 2
            """);
        var test = Read("tests/test_roles.py", """
            import pytest

            pytestmark = [pytest.mark.slow, pytest.mark.parametrize("db", [1])]


            @pytest.mark.parametrize("user", ["admin"], indirect=True)
            def test_admin(user, role, db):
                pass


            @pytest.mark.parametrize("user, role", [("a", "b")], indirect=["user"])
            def test_listed(user, role):
                pass


            @pytest.mark.parametrize("role", [1], indirect=False)
            class TestRoles:
                pytestmark = pytest.mark.parametrize("user", ["a"])

                def test_login(self, user, role, db):
                    pass
            """);
        var linked = Link(("tests/conftest.py", conftest), ("tests/test_roles.py", test));

        Assert.Equal(
            [
                ("python:tests.test_roles.test_admin", "user", 7), ("python:tests.test_roles.test_admin", "role", 7),
                ("python:tests.test_roles.test_listed", "user", 12),
            ],
            test.References.Where(reference => reference.Qualifier?.Kind == CodeQualifierKinds.Fixture).Select(reference => (reference.From.Whole(), reference.Name, reference.Line)));
        Assert.Equal(
            [
                ("python:tests.test_roles.test_admin", "python:tests.conftest.user"), ("python:tests.test_roles.test_admin", "python:tests.conftest.role"),
                ("python:tests.test_roles.test_listed", "python:tests.conftest.user"),
            ],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.References && edge.To.StartsWith("python:tests.conftest.", StringComparison.Ordinal)).Select(edge => (edge.From, edge.To)));
    }

    /// <summary>
    /// A class's own <c>parametrize</c> marks - its decorator's and its <c>pytestmark</c>'s, which pytest applies to every class built on
    /// it - give each test of a class another file builds on it, any number of levels up, a value in place of the fixture of that name:
    /// the class says the names its marks give, but one a mark routes to the fixture, and the link finds no fixture of such a name for
    /// a test of a class built on it, nor counts it external or signs it; a test of a class built on no such base requires its fixture
    /// still.
    /// </summary>
    [Fact]
    public void AParametrizeMarkOfABaseClass_GivesTheTestsOfEachClassBuiltOnIt_AValueInPlaceOfTheFixture()
    {
        var conftest = Read("tests/conftest.py", """
            import pytest


            @pytest.fixture
            def db():
                return 2
            """);
        var bases = Read("tests/bases.py", """
            import pytest


            @pytest.mark.parametrize("user", ["admin"])
            class Users:
                pytestmark = pytest.mark.parametrize("db", [1], indirect=True)


            class Between(Users):
                pass
            """);
        var test = Read("tests/test_users.py", """
            from .bases import Between


            class TestLogin(Between):
                def test_login(self, user, db):
                    pass


            class TestOther:
                def test_other(self, user):
                    pass
            """);
        var linked = Link(("tests/conftest.py", conftest), ("tests/bases.py", bases), ("tests/test_users.py", test));

        Assert.Equal([("python:tests.bases.Users", "user")], bases.Nodes.Where(node => node.Parametrizes is not null).Select(node => (node.Id.Whole(), string.Join(',', node.Parametrizes!))));
        Assert.Equal(
            [("python:tests.test_users.TestLogin.test_login", "python:tests.conftest.db")],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.References && edge.To.StartsWith("python:tests.conftest.", StringComparison.Ordinal)).Select(edge => (edge.From, edge.To)));

        // The one test whose user no base gives finds no fixture of it: counted external, and signed so.
        Assert.Equal(
            [("python:tests.test_users.TestOther.test_other", 1)],
            linked.External.Where(pair => pair.Key.StartsWith("python:tests.test_users.", StringComparison.Ordinal)).Select(pair => (pair.Key, pair.Value)));
        Assert.Equal(
            [("python:tests.test_users.TestOther.test_other", CodeGraphLimit.UnprovidedFixture)],
            linked.Signs.Where(pair => pair.Key.StartsWith("python:tests.test_users.", StringComparison.Ordinal)).Select(pair => (pair.Key, string.Join(',', pair.Value))));
    }

    /// <summary>
    /// A statement a text ends inside - an annotation with no type after its colon, an assignment with no value after its <c>=</c>, as a
    /// file being edited holds - declares what it writes and reads nothing past the text's end: a function's local and a class's field
    /// alike, the field's signature the text as written.
    /// </summary>
    [Fact]
    public void AnAnnotationOrAValueTheTextEndsBefore_IsReadAsNone()
    {
        Assert.Equal(
            [("python:pkg.cut", (string?)null), ("python:pkg.cut.f", "def f()")],
            Read("pkg/cut.py", "def f():\n    x:").Nodes.Select(node => (node.Id.Whole(), node.Kind == CodeNodeKinds.Member ? node.Signature : null)));
        Assert.Equal(
            [("python:pkg.cut", (string?)null), ("python:pkg.cut.A", (string?)null), ("python:pkg.cut.A.x", "x =")],
            Read("pkg/cut.py", "class A:\n    x =").Nodes.Select(node => (node.Id.Whole(), node.Kind == CodeNodeKinds.Member ? node.Signature : null)));
        Assert.Equal(
            [("python:pkg.cut", (string?)null), ("python:pkg.cut.A", (string?)null), ("python:pkg.cut.A.x", "x: int =")],
            Read("pkg/cut.py", "class A:\n    x: int =").Nodes.Select(node => (node.Id.Whole(), node.Kind == CodeNodeKinds.Member ? node.Signature : null)));
    }

    /// <summary>
    /// A name an import binds is read as what it reaches, whatever it names: an imported constant read alone or as the head of a chain
    /// is a use of it - <c>references</c> reaches types alone - and is joined to the constant; a type mention of the file's own class
    /// alone a reference.
    /// </summary>
    [Fact]
    public void AnImportedNameReadAlone_IsAUseOfIt_WhateverItNames()
    {
        var client = Read("pkg/client.py", "from .config import MAX_RETRIES, CONFIG\n\n\ndef fetch():\n    for _ in range(MAX_RETRIES):\n        CONFIG.get(\"x\")\n    return Local\n\n\nclass Local:\n    pass\n");
        var linked = Link(
            ("pkg/__init__.py", Read("pkg/__init__.py", string.Empty)),
            ("pkg/config.py", Read("pkg/config.py", "MAX_RETRIES = 3\nCONFIG = {}\n")),
            ("pkg/client.py", client));

        Assert.Equal(
            [("MAX_RETRIES", CodeEdgeKinds.Uses, 5, (string?)null), ("CONFIG", CodeEdgeKinds.Uses, 6, (string?)null), ("get", CodeEdgeKinds.Calls, 6, CodeQualifierKinds.Prefix), ("Local", CodeEdgeKinds.References, 7, (string?)null)],
            References(client, "python:pkg.client.fetch"));
        Assert.Equal(
            [
                new CodeEdge("python:pkg.client.fetch", "python:pkg.config.MAX_RETRIES", CodeEdgeKinds.Uses, 5, CodeConfidences.Unique),
                new CodeEdge("python:pkg.client.fetch", "python:pkg.config.CONFIG", CodeEdgeKinds.Uses, 6, CodeConfidences.Unique),
                new CodeEdge("python:pkg.client.fetch", "python:pkg.client.Local", CodeEdgeKinds.References, 7, CodeConfidences.Exact),
            ],
            linked.Edges.Where(edge => edge.From == "python:pkg.client.fetch"));
    }

    /// <summary>
    /// A name an import of a declaration around the code binds is found as Python finds it: a closure's through the function around
    /// it; a head's - a decorator's, an annotation's, a default's - where the declaration is defined, a method's in its class; an
    /// annotation of an attribute a method assigns in the method, the type it names no name the method binds, so the method's own call
    /// of it is one; and none of them through another declaration's import.
    /// </summary>
    [Fact]
    public void AnImportAroundTheCode_IsFoundAsPythonFindsIt()
    {
        var service = Read("pkg/service.py", """
            def outer():
                from .heavy import Engine
                def inner():
                    return Engine()
                return inner


            def make_app():
                from .views import cached
                @cached
                def handler():
                    pass
                return handler


            class Shop:
                from .heavy import Engine

                def build(self, kind: Engine = None) -> Engine:
                    return kind


            class Depot:
                def __init__(self):
                    from .heavy import Engine
                    self.engine: Engine = Engine()

                def other(self) -> Engine:
                    return cached
            """);
        var linked = Link(
            ("pkg/__init__.py", Read("pkg/__init__.py", string.Empty)),
            ("pkg/heavy.py", Read("pkg/heavy.py", "class Engine:\n    pass\n")),
            ("pkg/views.py", Read("pkg/views.py", "def cached(fn):\n    return fn\n")),
            ("pkg/service.py", service));

        Assert.Equal(
            [
                ("python:pkg.service.outer.inner", "python:pkg.heavy.Engine", CodeEdgeKinds.Instantiates, 4),
                ("python:pkg.service.make_app.handler", "python:pkg.views.cached", CodeEdgeKinds.Calls, 10),
                ("python:pkg.service.Shop.build", "python:pkg.heavy.Engine", CodeEdgeKinds.References, 19),
                ("python:pkg.service.Depot.__init__", "python:pkg.heavy.Engine", CodeEdgeKinds.Instantiates, 26),
                ("python:pkg.service.Depot.engine", "python:pkg.heavy.Engine", CodeEdgeKinds.References, 26),
            ],
            linked.Edges.Where(edge => edge.Kind != CodeEdgeKinds.Contains && edge.To is "python:pkg.heavy.Engine" or "python:pkg.views.cached").OrderBy(edge => edge.Line).ThenBy(edge => edge.From, StringComparer.Ordinal).Select(edge => (edge.From, edge.To, edge.Kind, edge.Line)));
        Assert.All(linked.Edges.Where(edge => edge.Kind != CodeEdgeKinds.Contains && edge.To is "python:pkg.heavy.Engine" or "python:pkg.views.cached"), edge => Assert.Equal(CodeConfidences.Exact, edge.Confidence));
        Assert.Empty(service.Dynamic);
    }

    /// <summary>
    /// What a function's statements name that the walk meets before the file's names are bound - an <c>except</c>'s type, a
    /// <c>case</c>'s class pattern, a local's annotation - is read where the function's run reaches it, once every name of the file is
    /// bound: a name the module imports below the function is the import's there.
    /// </summary>
    [Fact]
    public void WhatAStatementNames_IsReadOnceEveryNameOfTheFileIsBound()
    {
        var late = Read("pkg/late.py", """
            def load(value):
                try:
                    pass
                except DbError:
                    pass
                match value:
                    case Point(x=0):
                        pass
                total: Price = 1
                return total


            from .db import Error as DbError
            from .geo import Point
            from .money import Price
            """);

        Assert.Equal(
            [("Error", CodeEdgeKinds.References, 4, CodeQualifierKinds.Prefix), ("Point", CodeEdgeKinds.References, 7, (string?)null), ("Price", CodeEdgeKinds.References, 9, (string?)null)],
            References(late, "python:pkg.late.load"));
    }

    /// <summary>
    /// A class's type parameters are the code's of every declaration it holds: a method's annotations, its body, its decorators and the
    /// class's base name its <c>Item</c> as their own, never a type of the repository's of that name the file imports; a function's
    /// decorator runs outside the function's own type parameters, and names what the module binds by their name.
    /// </summary>
    [Fact]
    public void AClasssTypeParameters_AreItsMembersOwn()
    {
        var box = Read("pkg/box.py", """
            from .models import Item, register, T


            class Box[Item](Base[Item]):
                def get(self) -> Item:
                    value: Item = self.held
                    return Item

                @register(Item)
                def put[Item](self, item: Item) -> None:
                    pass


            @register(T)
            def first[T](items: list[T]) -> T:
                return items[0]
            """);

        Assert.Equal(
            [
                ("Base", CodeEdgeKinds.Inherits, 4, (string?)null), ("held", CodeEdgeKinds.Uses, 6, CodeQualifierKinds.This), ("register", CodeEdgeKinds.Calls, 9, (string?)null),
                ("register", CodeEdgeKinds.Calls, 14, (string?)null), ("T", CodeEdgeKinds.Uses, 14, (string?)null),
            ],
            box.References.Where(reference => reference.Kind != CodeEdgeKinds.Overrides).Select(reference => (reference.Name, reference.Kind, reference.Line, reference.Qualifier?.Kind)));
    }

    /// <summary>
    /// A fixture is what pytest's or pytest-asyncio's fixture decorator makes, told by what binds the decorator's head, and is named as
    /// pytest names it - by its <c>name=</c> where one is given; a name a <c>parametrize</c> mark gives a test is no fixture of it.
    /// </summary>
    [Fact]
    public void AFixture_IsNamedAsPytestNamesIt_AndAParametrizedNameIsNone()
    {
        var conftest = Read("tests/conftest.py", """
            import pytest
            import pytest_asyncio
            from pytest import fixture


            @pytest.fixture(name="client")
            def client_fixture():
                return 1


            @pytest_asyncio.fixture
            async def session():
                return 2


            @fixture
            def user():
                return 3
            """);
        var test = Read("tests/test_api.py", """
            import pytest


            @pytest.mark.parametrize(argnames="user, role", argvalues=[(1, 2)])
            def test_x(client, session, user, role):
                assert client
            """);
        var linked = Link(("tests/conftest.py", conftest), ("tests/test_api.py", test));

        Assert.Equal(
            [("python:tests.conftest.client", "client"), ("python:tests.conftest.session", "session"), ("python:tests.conftest.user", "user")],
            conftest.Nodes.Where(node => node.SubKind == CodeSubKinds.Fixture).Select(node => (node.Id.Whole(), node.Name)));
        Assert.Equal(
            [("client", CodeEdgeKinds.References, 5, CodeQualifierKinds.Fixture), ("session", CodeEdgeKinds.References, 5, CodeQualifierKinds.Fixture)],
            References(test, "python:tests.test_api.test_x").Where(reference => reference.Qualifier == CodeQualifierKinds.Fixture));
        Assert.Equal(
            ["python:tests.conftest.client", "python:tests.conftest.session"],
            linked.Edges.Where(edge => edge.From == "python:tests.test_api.test_x" && edge.Kind == CodeEdgeKinds.References).Select(edge => edge.To));
    }

    /// <summary>
    /// What a body binds is its own: a walrus's name at any depth - in a comprehension's condition too, which binds it in the function -
    /// is no use; a name a <c>global</c> declares is the module's, read and written as a use of it; and a name a <c>nonlocal</c> declares
    /// is the function's around it, the code's own.
    /// </summary>
    [Fact]
    public void AWalrus_AGlobal_AndANonlocal_BindAsPythonBinds()
    {
        var binds = Read("pkg/binds.py", """
            COUNT = 0


            def bump():
                global COUNT
                COUNT += 1


            def find(rx, texts):
                if (found := rx.match(texts[0])):
                    return found.group(1)
                return [size for text in texts if (size := len(text))]


            def counter():
                total = 0
                def add():
                    nonlocal total
                    total += 1
                    return total
                return add
            """);

        Assert.Equal([("COUNT", CodeEdgeKinds.Uses, 6, (string?)null)], References(binds, "python:pkg.binds.bump"));
        Assert.Equal([("match", CodeEdgeKinds.Calls, 10, CodeQualifierKinds.Value), ("group", CodeEdgeKinds.Calls, 11, CodeQualifierKinds.Value)], References(binds, "python:pkg.binds.find"));
        Assert.Equal([("add", CodeEdgeKinds.Uses, 21, (string?)null)], References(binds, "python:pkg.binds.counter"));
        Assert.Empty(References(binds, "python:pkg.binds.counter.add"));
        Assert.Contains(new CodeEdge("python:pkg.binds.bump", "python:pkg.binds.COUNT", CodeEdgeKinds.Uses, 6, CodeConfidences.Exact), Link(("pkg/binds.py", binds)).Edges);
    }

    /// <summary>
    /// An import in a conditional block at the module's top - a <c>try</c>'s and its <c>except</c>'s, an <c>if TYPE_CHECKING:</c>'s -
    /// binds names of the module, published and its importers' too, as one at the top does.
    /// </summary>
    [Fact]
    public void AnImportInAConditionalBlockAtTheTop_BindsNamesOfTheModule()
    {
        var fast = Read("pkg/fast.py", """
            try:
                import ujson as json
            except ImportError:
                import json
            from typing import TYPE_CHECKING
            if TYPE_CHECKING:
                from .cart import Cart
            """);
        var linked = Link(
            ("pkg/__init__.py", Read("pkg/__init__.py", string.Empty)),
            ("pkg/cart.py", Read("pkg/cart.py", "class Cart:\n    pass\n")),
            ("pkg/fast.py", fast),
            ("pkg/app.py", Read("pkg/app.py", "from .fast import Cart\n\nmade = Cart()\n")));

        Assert.Equal(["json", "TYPE_CHECKING", "Cart"], fast.Published!);
        Assert.All(fast.Imports, import => Assert.True(import.Reexports));
        Assert.Contains(new CodeEdge("python:pkg.app.made", "python:pkg.cart.Cart", CodeEdgeKinds.Instantiates, 3, CodeConfidences.Unique), linked.Edges);
    }

    /// <summary>
    /// What <c>from helper import pack</c> and <c>import helper</c> reach, where the file's directory is no package and both places a
    /// bare import may stand at hold the module, is each place's: a call of a function a candidate of both, as the file's imports edges
    /// are, and never the first alone; and one place alone declaring it leaves it a candidate all the same - a function, a class, a
    /// member of the class - since the file cannot tell which module it imports. Each such reference's sign is the import's, never
    /// a duck-typed receiver's.
    /// </summary>
    [Fact]
    public void WhatEitherPlaceGives_IsACandidateOfEach_ThoughOnePlaceAloneDeclaresIt()
    {
        var linked = Link(
            ("tools/helper.py", Read("tools/helper.py", "def pack(): ...\n\n\ndef solo(): ...\n\n\nclass Box:\n    def create(self): ...\n")),
            ("helper.py", Read("helper.py", "def pack(): ...\n")),
            ("tools/run.py", Read("tools/run.py", "from helper import pack, solo\nimport helper\n\npack()\nhelper.pack()\nsolo()\nhelper.solo()\nhelper.Box.create()\n")));

        Assert.Equal(
            [
                new CodeEdge("python:tools.run", "python:helper.pack", CodeEdgeKinds.Calls, 4, CodeConfidences.Candidates),
                new CodeEdge("python:tools.run", "python:tools.helper.pack", CodeEdgeKinds.Calls, 4, CodeConfidences.Candidates),
                new CodeEdge("python:tools.run", "python:helper.pack", CodeEdgeKinds.Calls, 5, CodeConfidences.Candidates),
                new CodeEdge("python:tools.run", "python:tools.helper.pack", CodeEdgeKinds.Calls, 5, CodeConfidences.Candidates),
                new CodeEdge("python:tools.run", "python:tools.helper.solo", CodeEdgeKinds.Calls, 6, CodeConfidences.Candidates),
                new CodeEdge("python:tools.run", "python:tools.helper.solo", CodeEdgeKinds.Calls, 7, CodeConfidences.Candidates),
                new CodeEdge("python:tools.run", "python:tools.helper.Box.create", CodeEdgeKinds.Calls, 8, CodeConfidences.Candidates),
            ],
            linked.Edges.Where(edge => edge.From == "python:tools.run" && edge.Kind == CodeEdgeKinds.Calls).OrderBy(edge => edge.Line).ThenBy(edge => edge.To, StringComparer.Ordinal));
        Assert.Equal(
            [new CodeEdge("python:tools.run", "python:tools.helper.Box", CodeEdgeKinds.Uses, 8, CodeConfidences.Candidates)],
            linked.Edges.Where(edge => edge.From == "python:tools.run" && edge.Kind == CodeEdgeKinds.Uses));
        Assert.Equal([CodeGraphLimit.CandidateImports], linked.Signs["python:tools.run"]);
    }

    /// <summary>
    /// A chain of types through an import - <c>pkg.mod.A.B.C.go()</c>, <c>A.B.C.go()</c> of <c>from pkg.mod import A</c> - writes each
    /// name once: the member past the module through the import's prefix, each after it a type linked to the use of the member before,
    /// so the names the extract's qualifiers write are the prefix's alone however long the chain is, where each step written whole would
    /// grow with its length squared; and the linker walks the links to the type each step names, the call exact on the type that
    /// declares it. A name the language keeps to itself on the way - <c>np.__version__.split()</c> - is a probe of what the module
    /// declares, the step after it linked to it, so a library's call is bound where its head is: an import naming no file of the tree.
    /// </summary>
    [Fact]
    public void AChainOfTypesThroughAnImport_WritesEachNameOnce_AndIsLinkedThroughItsLinks()
    {
        static int Written(int length)
            => Read("pkg/use.py", "import pkg.mod\n\n\ndef run():\n    return pkg.mod." + string.Join('.', Enumerable.Range(0, length).Select(at => $"T{at}")) + ".go()\n")
                .References.Sum(reference => reference.Qualifier?.Name?.Whole().Length ?? 0);

        Assert.Equal((7, 7, 7), (Written(10), Written(100), Written(1_000)));

        var use = Read("pkg/use.py", "import pkg.mod\nfrom pkg.mod import A\n\n\ndef run():\n    pkg.mod.A.B.C.go()\n    A.B.C.go()\n");
        var chained = use.References.Where(reference => reference.Qualifier is { Kind: CodeQualifierKinds.Prefix or CodeQualifierKinds.Type }).ToList();

        // Each step past the first names its own name alone and links the use of the one before, whose names it writes on.
        Assert.Equal(
            [("A", null, "pkg.mod"), ("B", "A", "pkg.mod.A"), ("C", "B", "pkg.mod.A.B"), ("go", "C", "pkg.mod.A.B.C"), ("B", null, "A"), ("C", "B", "A.B"), ("go", "C", "A.B.C")],
            chained.Select(reference => (reference.Name, reference.Qualifier!.Link is { } link ? use.References[link].Name : null, use.Written(reference.Qualifier))));

        var linked = Link(
            ("pkg/__init__.py", Read("pkg/__init__.py", string.Empty)),
            ("pkg/mod.py", Read("pkg/mod.py", "class A:\n    class B:\n        class C:\n            def go(self):\n                pass\n")),
            ("pkg/use.py", use));

        Assert.Equal(
            [("python:pkg.mod.A.B.C.go", 6, CodeConfidences.Exact), ("python:pkg.mod.A.B.C.go", 7, CodeConfidences.Exact)],
            linked.Edges.Where(edge => edge.From == "python:pkg.use.run" && edge.Kind == CodeEdgeKinds.Calls).Select(edge => (edge.To, edge.Line, edge.Confidence)));

        var version = Read("pkg/ver.py", "import numpy as np\n\n\ndef ver():\n    return np.__version__.split()\n");

        Assert.Equal(
            [("__version__", CodeQualifierKinds.Prefix, true, (string?)null), ("split", CodeQualifierKinds.Type, false, "__version__")],
            version.References.Where(reference => reference.Name is "__version__" or "split")
                .Select(reference => (reference.Name, reference.Qualifier!.Kind, reference.Probe, reference.Qualifier.Link is { } link ? version.References[link].Name : null)));
        Assert.Equal([CodeGraphLimit.OutsideImport], Link(("pkg/ver.py", version)).Signs["python:pkg.ver.ver"]);
    }

    /// <summary>
    /// The reading's work over the file's structure (<see cref="PythonReading.Work"/>) grows with its text, never with a depth times a
    /// length: classes nested in blocks a thousand deep around a long suite - each class's names found by its own statements alone, each
    /// statement's end found from the file's logical lines - and a chain thousands of names long, each member's qualifier found from the
    /// one before it. Doubling every size doubles the work, less what a file costs whatever its size.
    /// </summary>
    [Fact]
    public void TheReadingsWork_GrowsWithItsText_NeverWithADepthTimesALength()
    {
        var small = Work(500, 1_000, 2_000);
        var large = Work(1_000, 2_000, 4_000);

        Assert.Equal((26_764L, -14L), (small, large - 2 * small));
    }

    /// <summary>
    /// The work of reading a file of classes and <c>if</c> blocks nested <paramref name="depth"/> deep, the innermost suite
    /// <paramref name="width"/> assignments and a method that reads a chain <paramref name="chain"/> names long through <c>self</c>.
    /// </summary>
    private static long Work(int depth, int width, int chain)
    {
        var lines = Enumerable.Range(0, depth).Select(level => new string(' ', level) + (level % 2 == 0 ? $"class C{level}:" : "if x:"))
            .Concat(Enumerable.Range(0, width).Select(at => new string(' ', depth) + $"v{at} = {at}"))
            .Append(new string(' ', depth) + "def m(self):")
            .Append(new string(' ', depth + 1) + "return self." + string.Join('.', Enumerable.Range(0, chain).Select(at => $"a{at}")));
        var reading = new PythonReading("python", "pkg/wide.py", new CodeFileInput("pkg/wide.py", string.Join('\n', lines) + "\n").Text);

        reading.Read();
        return reading.Work;
    }

    /// <summary>
    /// Blocks nested five thousand deep are read whole, each statement's end found from the file's logical lines once and every walk on
    /// the reader's stack: the call the deepest makes and the one after the last closes, nothing marked dynamic - in a child given a
    /// small stack, which a reading by recursion would overflow.
    /// </summary>
    [Fact]
    public async Task ABlockFiveThousandDeep_IsReadWhole()
        => Assert.Equal(
            new ChildRunAnswer(0, "calls probe 5004, calls helper 5005; 0 dynamic", ""),
            await ChildRun.MethodAsync(typeof(PythonExtractorTests), nameof(BlocksFiveThousandDeep), TestContext.Current.CancellationToken));

    /// <summary>The child of <see cref="ABlockFiveThousandDeep_IsReadWhole"/>: what the deep body calls, and how much is marked dynamic.</summary>
    internal static string BlocksFiveThousandDeep() => SmallStack.Run(() =>
    {
        var lines = Enumerable.Range(0, 5_000).Select(depth => new string(' ', 4 + depth) + "if x:");
        var extract = Read("pkg/deep.py", $"def helper():\n    pass\ndef deep(x):\n{string.Join('\n', lines)}\n{new string(' ', 5_004)}probe(x)\n    helper()\n");

        return Said(extract, "python:pkg.deep.deep");
    });

    /// <summary>
    /// F-strings nested five thousand deep in one another's fields are read whole, each field's code a run of its own on the reader's
    /// stack of runs and none a call into the reading - the call the deepest makes among what the body calls, nothing marked dynamic -
    /// in a child given a small stack.
    /// </summary>
    [Fact]
    public async Task AnFStringFiveThousandDeep_IsReadWhole()
        => Assert.Equal(
            new ChildRunAnswer(0, "calls deep 2; 0 dynamic", ""),
            await ChildRun.MethodAsync(typeof(PythonExtractorTests), nameof(FStringsFiveThousandDeep), TestContext.Current.CancellationToken));

    /// <summary>The child of <see cref="AnFStringFiveThousandDeep_IsReadWhole"/>: what the body calls, and how much is marked dynamic.</summary>
    internal static string FStringsFiveThousandDeep() => SmallStack.Run(() => Said(
        Read("pkg/nest.py", $"def go():\n    return {string.Concat(Enumerable.Repeat("f\"{", 5_000))}deep(){string.Concat(Enumerable.Repeat("}\"", 5_000))}\n"),
        "python:pkg.nest.go"));

    /// <summary>
    /// <c>Test*</c> classes nested five thousand deep in a test file are read whole, each a test class held by the one around it and the
    /// method of the deepest a test - in a child given a small stack.
    /// </summary>
    [Fact]
    public async Task ATestClassFiveThousandDeep_IsReadWhole()
        => Assert.Equal(
            new ChildRunAnswer(0, "5000 test classes, a test 5001 deep calls probe; 0 dynamic", ""),
            await ChildRun.MethodAsync(typeof(PythonExtractorTests), nameof(TestClassesFiveThousandDeep), TestContext.Current.CancellationToken));

    /// <summary>The child of <see cref="ATestClassFiveThousandDeep_IsReadWhole"/>: how many test classes were read, how deep the test is, what it calls, and how much is marked dynamic.</summary>
    internal static string TestClassesFiveThousandDeep() => SmallStack.Run(() =>
    {
        var classes = Enumerable.Range(0, 5_000).Select(depth => new string(' ', depth) + "class TestA:");
        var extract = Read("tests/test_deep.py", $"{string.Join('\n', classes)}\n{new string(' ', 5_000)}def test_deep(self):\n{new string(' ', 5_001)}probe()\n");
        var nodes = extract.Nodes.ToDictionary(node => node.Id);
        var test = Assert.Single(extract.Nodes, node => node.Kind == CodeNodeKinds.Member && node.Test);
        var depth = 0;

        for (var id = test.Parent; id is not null; id = nodes[id].Parent)
        {
            depth++;
        }

        var calls = string.Join(", ", extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Calls && reference.From == test.Id).Select(reference => reference.Name));

        return string.Create(CultureInfo.InvariantCulture, $"{extract.Nodes.Count(node => node.Kind == CodeNodeKinds.Type && node.Test)} test classes, a test {depth} deep calls {calls}; {extract.Dynamic.Count} dynamic");
    });

    /// <summary>What the node of <paramref name="id"/> calls, by name and line, and how much of the file is marked dynamic.</summary>
    private static string Said(FileExtract extract, string id)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{string.Join(", ", extract.References.Where(reference => reference.From == id && reference.Kind == CodeEdgeKinds.Calls).Select(reference => $"calls {reference.Name} {reference.Line}"))}; {extract.Dynamic.Count} dynamic");

    /// <summary>Where the literal <paramref name="value"/> is written: each place's node and line.</summary>
    private static List<(string Node, int Line)> Places(FileExtract extract, string value)
        => [.. Assert.Single(extract.Literals ?? [], literal => literal.Value == value).Places.Select(place => (place.Node!.Whole(), place.Line))];

    private static FileExtract Read(string path, string text) => new PythonExtractor().Extract(new CodeFileInput(path, text));

    private static CodeGraphFile Py(string path, string text) => new(path, "python", Read(path, text));

    private static CodeGraphLinked Link(params (string Path, FileExtract Extract)[] files)
        => CodeGraphLinker.Link([.. files.Select(file => new CodeGraphFile(file.Path, "python", file.Extract))]);

    private static ExtractNode Node(FileExtract extract, string id) => Assert.Single(extract.Nodes, node => node.Id == id);

    private static List<(string Name, string Kind, int Line, string? Qualifier)> References(FileExtract extract, string from)
        => [.. extract.References.Where(reference => reference.From == from).Select(reference => (reference.Name, reference.Kind, reference.Line, reference.Qualifier?.Kind))];
}
