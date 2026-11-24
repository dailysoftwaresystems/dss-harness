using System.Globalization;
using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;

namespace DssHarness.Tests;

/// <summary>PHP's implementation over the structural scanner, held to the contract's kit, and the rules of its own the kit does not name.</summary>
public sealed class PhpExtractorTests : CodeGraphExtractorContractTests<PhpExtractor>
{
    internal const string User = """
        <?php

        declare(strict_types=1);

        namespace App\Models;

        use App\Contracts\HasName;
        use App\Mail\Mailer;
        use App\Support\Str as Text;
        use function App\Support\helper;

        /**
         * A user of the application.
         */
        final class User extends Model implements HasName
        {
            use Notifiable;

            public const ROLE = 'user';

            private ?Repository $repo = null;

            public function __construct(private readonly Mailer $mailer, public string $name = '')
            {
                parent::__construct();
            }

            public function name(): string
            {
                return Text::upper($this->name) . helper(1);
            }

            public function notify(string $message): void
            {
                $this->mailer->send($message);
                $this->repo?->save($this);
                static::boot();
            }

            public static function boot(): void
            {
                $user = new self();
                $user->name();
                strlen("{$user->name()}");
            }
        }
        """;

    internal const string Model = """
        <?php
        namespace App\Models;

        /** What every model is. */
        abstract class Model
        {
            public function __construct()
            {
            }

            abstract public function name(): string;
        }
        """;

    internal const string Notifiable = """
        <?php
        namespace App\Models;

        trait Notifiable
        {
            public function route(): string
            {
                return 'mail';
            }
        }
        """;

    internal const string Repository = """
        <?php
        namespace App\Models;

        class Repository
        {
            public function save(Model $model): void
            {
                $model->name();
            }
        }
        """;

    internal const string HasName = """
        <?php
        namespace App\Contracts;

        interface HasName
        {
            public function name(): string;
        }
        """;

    internal const string Mailer = """
        <?php
        namespace App\Mail;

        class Mailer
        {
            public function send(string $message): bool
            {
                return true;
            }
        }
        """;

    internal const string Support = """
        <?php
        namespace App\Support;

        class Str
        {
            public static function upper(string $text): string
            {
                return strtoupper($text);
            }
        }

        function helper(int $n, string ...$rest): string
        {
            return Str::upper((string) $n);
        }
        """;

    internal const string Helpers = """
        <?php

        if (! function_exists('app_path')) {
            /** The application's directory. */
            function app_path(string $path = ''): string
            {
                return __DIR__ . '/../src/' . $path;
            }
        }

        require_once __DIR__ . '/../src/Support/Str.php';
        """;

    internal const string UserTest = """
        <?php
        namespace Tests\Unit;

        use App\Models\User;
        use App\Mail\Mailer;
        use PHPUnit\Framework\TestCase;

        class UserTest extends TestCase
        {
            public function testName(): void
            {
                $user = new User(new Mailer());
                $this->assertSame('X', $user->name());
            }

            #[Test]
            public function notifies(): void
            {
                (new User(new Mailer()))->notify('hi');
            }

            private function helper(): void
            {
            }
        }
        """;

    internal const string Pest = """
        <?php

        use App\Support\Str;

        describe('strings', function () {
            it('upper-cases', function () {
                expect(Str::upper('a'))->toBe('A');
            });
        });

        test('the path', fn () => app_path('x'));
        """;

    internal const string View = """
        <html>
        <?php foreach ($users as $user): ?>
          <li><?= htmlspecialchars($user->name()) ?></li>
        <?php endforeach; ?>
        <?php echo app_path('views'); ?>
        </html>
        """;

    internal const string Status = """
        <?php
        namespace App\Models;

        enum Status: string implements HasLabel
        {
            case Active = 'active';
            case Closed = 'closed';

            public function label(): string
            {
                return ucfirst($this->value);
            }
        }
        """;


    private const string Reads = """
        <?php
        namespace Reads;

        class Table
        {
            public const GREETING = 'hello world';

            public static int $count = 0;
        }

        enum Kind
        {
            case One;
            case String;
        }

        class Reader
        {
            private int $own = 0;

            public function take(): int
            {
                $local = 4;
                Table::$count += $local + $this->own;
                $which = Kind::One ?? Kind::String;
                return strlen(Table::GREETING . ' padding ' . "{$which->name} part");
            }
        }
        """;

    /// <summary>
    /// A function's and a class's method of one name, a namespace's constant and the class's own of one name, a constant a
    /// <c>define</c> names: a bare name in the method must reach the function and the namespace's constants, never the method
    /// nor the class's constant.
    /// </summary>
    private const string Bare = """
        <?php
        namespace Bare;

        const LIMIT = 10;

        function helper(): int
        {
            return LIMIT + STAMP;
        }

        class Box
        {
            public const LIMIT = 5;

            public function helper(): int
            {
                return helper() + LIMIT + self::LIMIT + \Bare\LIMIT;
            }
        }

        define('Bare\STAMP', 2);
        """;

    /// <summary>
    /// What a body declares: an anonymous class with a base, interfaces, traits, typed properties - one attributed, one a visibility of
    /// setting modifies, one with hooks - typed constants, and - in another function - a class with a hooked property of its own, an
    /// interface, an enum and a <c>yield from</c>.
    /// </summary>
    private const string Anonymous = """
        <?php
        namespace Made;
        use Lib\Base;
        function make() {
            return new class(LIMIT) extends Base implements \Countable, Shape {
                use Helps, Logs { Helps::run insteadof Logs; }
                public Foo|Bar $p;
                private readonly ?Baz $q;
                const int N = 1, M = SIZE;
                public function count(): int { return N; }
                #[A] public Foo $x;
                private(set) Bar $y;
                public Foo $z { #[A] get => helper(); final set(string $v) { $this->z = $v; } }
            };
        }
        function boot() {
            class Inner extends Outer {}
            class Wired { public Foo $w { get; set => helper(); } }
            interface Shaped extends Sized {}
            enum Suit: string { case Hearts = 'h'; case Spades = HEARTS; }
            yield from gen();
        }
        """;

    /// <inheritdoc/>
    public override IReadOnlyList<CodeGraphFixture> Fixtures { get; } =
    [
        new("src/reads.php", Reads)
        {
            Nodes =
            [
                new("php:Reads", CodeNodeKinds.Namespace, (2, 28)),
                new("php:Reads.Table", CodeNodeKinds.Type, (4, 9)),
                new("php:Reads.Table.GREETING", CodeNodeKinds.Member, (6, 6)),
                new("php:Reads.Table.$count", CodeNodeKinds.Member, (8, 8)),
                new("php:Reads.Kind", CodeNodeKinds.Type, (11, 15)),
                new("php:Reads.Kind.One", CodeNodeKinds.Member, (13, 13)),
                new("php:Reads.Kind.String", CodeNodeKinds.Member, (14, 14)),
                new("php:Reads.Reader", CodeNodeKinds.Type, (17, 28)),
                new("php:Reads.Reader.$own", CodeNodeKinds.Member, (19, 19)),
                new("php:Reads.Reader.take", CodeNodeKinds.Member, (21, 27)),
            ],
            Edges =
            [
                // A property read through $this - declared with its $ - a constant read through its class, and an enum case,
                // one named as a type of the language among them.
                new("php:Reads.Reader.take", "php:Reads.Reader.$own", CodeEdgeKinds.Uses, 24, CodeConfidences.Exact),
                new("php:Reads.Reader.take", "php:Reads.Table.GREETING", CodeEdgeKinds.Uses, 26, CodeConfidences.Exact),
                new("php:Reads.Reader.take", "php:Reads.Kind.One", CodeEdgeKinds.Uses, 25, CodeConfidences.Exact),
                new("php:Reads.Reader.take", "php:Reads.Kind.String", CodeEdgeKinds.Uses, 25, CodeConfidences.Exact),
            ],
        },
        new("src/Models/User.php", User)
        {
            Nodes =
            [
                new("php:App.Models", CodeNodeKinds.Namespace, (5, 46)),
                new("php:App.Models.User", CodeNodeKinds.Type, (15, 46)),
                new("php:App.Models.User.ROLE", CodeNodeKinds.Member, (19, 19)),
                new("php:App.Models.User.$repo", CodeNodeKinds.Member, (21, 21)),
                new("php:App.Models.User.__construct", CodeNodeKinds.Member, (23, 26)),
                new("php:App.Models.User.$mailer", CodeNodeKinds.Member, (23, 23)),
                new("php:App.Models.User.$name", CodeNodeKinds.Member, (23, 23)),
                new("php:App.Models.User.name", CodeNodeKinds.Member, (28, 31)),
                new("php:App.Models.User.notify", CodeNodeKinds.Member, (33, 38)),
                new("php:App.Models.User.boot", CodeNodeKinds.Member, (40, 45)),
            ],
            Edges =
            [
                new("file:src/Models/User.php", "php:App.Contracts.HasName", CodeEdgeKinds.Imports, 7, CodeConfidences.Exact),
                new("file:src/Models/User.php", "php:App.Support", CodeEdgeKinds.Imports, 10, CodeConfidences.Exact),
                new("php:App.Models.User", "php:App.Models.Model", CodeEdgeKinds.Inherits, 15, CodeConfidences.Exact),
                new("php:App.Models.User", "php:App.Contracts.HasName", CodeEdgeKinds.Implements, 15, CodeConfidences.Exact),
                new("php:App.Models.User", "php:App.Models.Notifiable", CodeEdgeKinds.MixesIn, 17, CodeConfidences.Exact),
                new("php:App.Models.User.$repo", "php:App.Models.Repository", CodeEdgeKinds.References, 21, CodeConfidences.Exact),
                new("php:App.Models.User.__construct", "php:App.Mail.Mailer", CodeEdgeKinds.References, 23, CodeConfidences.Exact),
                new("php:App.Models.User.__construct", "php:App.Models.Model.__construct", CodeEdgeKinds.Calls, 25, CodeConfidences.Exact),
                new("php:App.Models.User.name", "php:App.Support.Str.upper", CodeEdgeKinds.Calls, 30, CodeConfidences.Exact),
                new("php:App.Models.User.name", "php:App.Support.helper", CodeEdgeKinds.Calls, 30, CodeConfidences.Exact),
                new("php:App.Models.User.notify", "php:App.Mail.Mailer.send", CodeEdgeKinds.Calls, 35, CodeConfidences.Exact),
                new("php:App.Models.User.notify", "php:App.Models.Repository.save", CodeEdgeKinds.Calls, 36, CodeConfidences.Exact),
                new("php:App.Models.User.notify", "php:App.Models.User.boot", CodeEdgeKinds.Calls, 37, CodeConfidences.Exact),
                new("php:App.Models.User.boot", "php:App.Models.User", CodeEdgeKinds.Instantiates, 42, CodeConfidences.Exact),
                new("php:App.Models.User.boot", "php:App.Models.User.name", CodeEdgeKinds.Calls, 43, CodeConfidences.Exact),
                new("php:App.Models.User.boot", "php:App.Models.User.name", CodeEdgeKinds.Calls, 44, CodeConfidences.Exact),
                new("php:App.Contracts.HasName.name", "php:App.Models.User.name", CodeEdgeKinds.Dispatches, 28, CodeConfidences.Exact),
            ],
        },
        new("src/Models/Model.php", Model)
        {
            Nodes = [new("php:App.Models.Model", CodeNodeKinds.Type, (5, 12)), new("php:App.Models.Model.name", CodeNodeKinds.Member, (11, 11))],
        },
        new("src/Models/Notifiable.php", Notifiable)
        {
            Nodes = [new("php:App.Models.Notifiable", CodeNodeKinds.Type, (4, 10)), new("php:App.Models.Notifiable.route", CodeNodeKinds.Member, (6, 9))],
        },
        new("src/Models/Repository.php", Repository)
        {
            Edges =
            [
                new("php:App.Models.Repository.save", "php:App.Models.Model", CodeEdgeKinds.References, 6, CodeConfidences.Exact),
                new("php:App.Models.Repository.save", "php:App.Models.Model.name", CodeEdgeKinds.Calls, 8, CodeConfidences.Exact),
            ],
        },
        new("src/Models/Status.php", Status)
        {
            Nodes = [new("php:App.Models.Status", CodeNodeKinds.Type, (4, 13)), new("php:App.Models.Status.Active", CodeNodeKinds.Member, (6, 6))],
        },
        new("src/Contracts/HasName.php", HasName)
        {
            Nodes = [new("php:App.Contracts.HasName", CodeNodeKinds.Type, (4, 7)), new("php:App.Contracts.HasName.name", CodeNodeKinds.Member, (6, 6))],
        },
        new("src/Mail/Mailer.php", Mailer)
        {
            Nodes = [new("php:App.Mail.Mailer.send", CodeNodeKinds.Member, (6, 9))],
        },
        new("src/Support/Str.php", Support)
        {
            Nodes = [new("php:App.Support.Str.upper", CodeNodeKinds.Member, (6, 9)), new("php:App.Support.helper", CodeNodeKinds.Member, (12, 15))],
            Edges = [new("php:App.Support.helper", "php:App.Support.Str.upper", CodeEdgeKinds.Calls, 14, CodeConfidences.Exact)],
        },
        new("bootstrap/helpers.php", Helpers)
        {
            Nodes = [new("php:bootstrap/helpers.php.(script)", CodeNodeKinds.Member, (1, 11)), new("php:app_path", CodeNodeKinds.Member, (5, 8))],
            Edges = [new("file:bootstrap/helpers.php", "file:src/Support/Str.php", CodeEdgeKinds.Imports, 11, CodeConfidences.Exact)],
        },
        new("tests/Unit/UserTest.php", UserTest)
        {
            Nodes = [new("php:Tests.Unit.UserTest.testName", CodeNodeKinds.Member, (10, 14)), new("php:Tests.Unit.UserTest.notifies", CodeNodeKinds.Member, (16, 20))],
            Edges =
            [
                new("php:Tests.Unit.UserTest.testName", "php:App.Models.User", CodeEdgeKinds.Instantiates, 12, CodeConfidences.Exact),
                new("php:Tests.Unit.UserTest.testName", "php:App.Mail.Mailer", CodeEdgeKinds.Instantiates, 12, CodeConfidences.Exact),
                new("php:Tests.Unit.UserTest.testName", "php:App.Models.User.name", CodeEdgeKinds.Calls, 13, CodeConfidences.Exact),
                new("php:Tests.Unit.UserTest.testName", "php:App.Models.User.name", CodeEdgeKinds.Tests, 13, CodeConfidences.Exact),
                new("php:Tests.Unit.UserTest.notifies", "php:App.Models.User.notify", CodeEdgeKinds.Calls, 19, CodeConfidences.Unique),
                new("php:Tests.Unit.UserTest.notifies", "php:App.Models.User.notify", CodeEdgeKinds.Tests, 19, CodeConfidences.Unique),
            ],
        },
        new("tests/Feature/StrTest.php", Pest)
        {
            Nodes =
            [
                new("php:tests/Feature/StrTest.php.'strings'", CodeNodeKinds.Member, (5, 9)),
                new("php:tests/Feature/StrTest.php.'strings'.'upper-cases'", CodeNodeKinds.Member, (6, 8)),
                new("php:tests/Feature/StrTest.php.'the path'", CodeNodeKinds.Member, (11, 11)),
            ],
            Edges =
            [
                new("file:tests/Feature/StrTest.php", "php:App.Support.Str", CodeEdgeKinds.Imports, 3, CodeConfidences.Exact),
                new("php:tests/Feature/StrTest.php.'strings'.'upper-cases'", "php:App.Support.Str.upper", CodeEdgeKinds.Tests, 7, CodeConfidences.Exact),
                new("php:tests/Feature/StrTest.php.'the path'", "php:app_path", CodeEdgeKinds.Calls, 11, CodeConfidences.Exact),
                new("php:tests/Feature/StrTest.php.'the path'", "php:app_path", CodeEdgeKinds.Tests, 11, CodeConfidences.Exact),
            ],
        },
        new("resources/views/home.phtml", View)
        {
            Nodes = [new("php:resources/views/home.phtml.(script)", CodeNodeKinds.Member, (1, 6))],
            Edges =
            [
                new("php:resources/views/home.phtml.(script)", "php:App.Models.User.name", CodeEdgeKinds.Calls, 3, CodeConfidences.Candidates),
                new("php:resources/views/home.phtml.(script)", "php:app_path", CodeEdgeKinds.Calls, 5, CodeConfidences.Exact),
            ],
        },
        new("src/bare.php", Bare)
        {
            Nodes =
            [
                new("php:Bare", CodeNodeKinds.Namespace, (2, 21)),
                new("php:Bare.LIMIT", CodeNodeKinds.Member, (4, 4)),
                new("php:Bare.helper", CodeNodeKinds.Member, (6, 9)),
                new("php:Bare.Box", CodeNodeKinds.Type, (11, 19)),
                new("php:Bare.Box.LIMIT", CodeNodeKinds.Member, (13, 13)),
                new("php:Bare.Box.helper", CodeNodeKinds.Member, (15, 18)),
                new("php:Bare.STAMP", CodeNodeKinds.Member, (21, 21)),
            ],
            Edges =
            [
                // A name written alone in a method is the namespace's, never its class's: the function, and the constant - however written,
                // a const's or a define's.
                new("php:Bare.helper", "php:Bare.LIMIT", CodeEdgeKinds.Uses, 8, CodeConfidences.Exact),
                new("php:Bare.helper", "php:Bare.STAMP", CodeEdgeKinds.Uses, 8, CodeConfidences.Exact),
                new("php:Bare.Box.helper", "php:Bare.helper", CodeEdgeKinds.Calls, 17, CodeConfidences.Exact),
                new("php:Bare.Box.helper", "php:Bare.LIMIT", CodeEdgeKinds.Uses, 17, CodeConfidences.Exact),
                new("php:Bare.Box.helper", "php:Bare.Box.LIMIT", CodeEdgeKinds.Uses, 17, CodeConfidences.Exact),
            ],
        },
        new("src/anonymous.php", Anonymous)
        {
            Nodes =
            [
                new("php:Made", CodeNodeKinds.Namespace, (2, 22)),
                new("php:Made.make", CodeNodeKinds.Member, (4, 15)),
                new("php:Made.boot", CodeNodeKinds.Member, (16, 22)),
            ],
        },
    ];

    /// <summary>
    /// Text outside PHP's tags, comments, strings, heredocs and nowdocs are never read as code, and a string's or a
    /// heredoc's interpolation is - a nowdoc's not; an attribute calls nothing itself, but its arguments are read as the
    /// code they are, a closure's the code it is written in.
    /// </summary>
    [Fact]
    public void TextCommentsAndStrings_AreNeverReadAsCode_ButAnInterpolationAndAnAttributesArgumentsAre()
    {
        var extract = Read("src/a.php", """
            <p>first()</p>
            <?php
            function go($user) {
                // second();
                # third();
                /* fourth(); */
                $a = 'fifth()' . "sixth() {$user->seventh()} ${eighth}";
                $b = <<<EOT
                    ninth() {$user->tenth()}
                    EOT;
                $c = <<<'EOT'
                    eleventh() {$user->twelfth()}
                    EOT;
                ?> thirteenth() <?php
                fourteenth();
                $f = #[Pure(fifteenth())] fn () => sixteenth();
            }
            """);

        Assert.Equal(["seventh", "tenth", "fourteenth", "fifteenth", "sixteenth"], Calls(extract, "php:go"));
        Assert.Equal((3, 17), Lines(extract, "php:go"));
    }

    /// <summary>
    /// Every kind of declaration is a node of its sub-kind, named as PHP names it - a property by its variable, a
    /// constructor's promoted parameters properties of its type - with its modifiers, its value's type, and a prototype
    /// where it has no body; its base lists and its traits are its edges, and what a property's hooks call its calls.
    /// </summary>
    [Fact]
    public void EveryKindOfDeclaration_IsANodeOfItsSubKind_NamedAsPhpNamesIt()
    {
        var extract = Read("src/Shop.php", """
            <?php
            namespace Shop;

            const LIMIT = 10;

            function total(int ...$prices): int { return 0; }

            abstract class Base { abstract protected function size(): int; }

            final class Cart extends Base implements \Countable, Sized
            {
                use Loggable, Traced { Traced::log insteadof Loggable; }
                public const MAX = 5, MIN = 1;
                protected static array $items = [], $tags;
                public string $label {
                    get => strtoupper($this->name());
                    set(string $value) { $this->apply($value); }
                }
                public function __construct(private readonly Store $store, public ?Cart $parent = null) {}
                public function count(): int { return 0; }
                final public static function &make(): static { return new static(); }
            }

            interface Sized extends \Countable, Measured { public function size(): int; }

            trait Loggable { public function log(): void {} }

            enum Suit: string implements Sized
            {
                case Hearts = 'h';
                case Spades = 's';
                const Wild = self::Spades;
                public function size(): int { return 1; }
            }

            readonly class Point { public function __construct(public int $x) {} }
            """);

        Assert.Equal(
            [
                ("Shop", "namespace"), ("Shop.LIMIT", "constant"), ("Shop.total", "function"), ("Shop.Base", "class"), ("Shop.Base.size", "method"),
                ("Shop.Cart", "class"), ("Shop.Cart.MAX", "constant"), ("Shop.Cart.MIN", "constant"), ("Shop.Cart.$items", "property"),
                ("Shop.Cart.$tags", "property"), ("Shop.Cart.$label", "property"), ("Shop.Cart.__construct", "constructor"),
                ("Shop.Cart.$store", "property"), ("Shop.Cart.$parent", "property"), ("Shop.Cart.count", "method"), ("Shop.Cart.make", "method"),
                ("Shop.Sized", "interface"), ("Shop.Sized.size", "method"), ("Shop.Loggable", "trait"), ("Shop.Loggable.log", "method"),
                ("Shop.Suit", "enum"), ("Shop.Suit.Hearts", "case"), ("Shop.Suit.Spades", "case"), ("Shop.Suit.Wild", "constant"),
                ("Shop.Suit.size", "method"), ("Shop.Point", "class"), ("Shop.Point.__construct", "constructor"), ("Shop.Point.$x", "property"),
            ],
            extract.Nodes.Select(node => (node.Qualified.Whole(), node.SubKind!)));

        Assert.Equal(["final"], Node(extract, "php:Shop.Cart").Modifiers);
        Assert.Equal(["readonly"], Node(extract, "php:Shop.Point").Modifiers);
        Assert.Equal(["final", "public", "static"], Node(extract, "php:Shop.Cart.make").Modifiers);
        Assert.Equal(["protected", "static"], Node(extract, "php:Shop.Cart.$items").Modifiers);
        Assert.Equal(["private", "readonly"], Node(extract, "php:Shop.Cart.$store").Modifiers);
        Assert.Equal("Shop.Store", Node(extract, "php:Shop.Cart.$store").ValueType);
        Assert.Equal("Shop.Cart", Node(extract, "php:Shop.Cart.$parent").ValueType);
        Assert.Null(Node(extract, "php:Shop.Cart.$items").ValueType);
        Assert.Equal("Shop.Suit", Node(extract, "php:Shop.Suit.Hearts").ValueType);
        Assert.Equal([true, true, false, false], ((string[])["php:Shop.Base.size", "php:Shop.Sized.size", "php:Shop.Cart.count", "php:Shop.Suit.size"]).Select(id => Node(extract, id).Prototype));
        Assert.Equal((0, (int?)null), Arity(extract, "php:Shop.total"));
        Assert.Equal((1, (int?)2), Arity(extract, "php:Shop.Cart.__construct"));
        Assert.Equal((15, 18), Lines(extract, "php:Shop.Cart.$label"));
        Assert.Equal(["public", "abstract"], Node(extract, "php:Shop.Sized.size").Modifiers);

        Assert.Equal(
            [
                ("php:Shop.Cart", CodeEdgeKinds.Inherits, "Base", "type", "Shop"), ("php:Shop.Cart", CodeEdgeKinds.Implements, "Countable", "scope", null),
                ("php:Shop.Cart", CodeEdgeKinds.Implements, "Sized", "type", "Shop"), ("php:Shop.Cart", CodeEdgeKinds.MixesIn, "Loggable", "type", "Shop"),
                ("php:Shop.Cart", CodeEdgeKinds.MixesIn, "Traced", "type", "Shop"), ("php:Shop.Sized", CodeEdgeKinds.Inherits, "Countable", "scope", null),
                ("php:Shop.Sized", CodeEdgeKinds.Inherits, "Measured", "type", "Shop"), ("php:Shop.Suit", CodeEdgeKinds.Implements, "Sized", "type", "Shop"),
            ],
            extract.References.Where(reference => reference.Kind is CodeEdgeKinds.Inherits or CodeEdgeKinds.Implements or CodeEdgeKinds.MixesIn)
                .Select(reference => (reference.From.Whole(), reference.Kind, reference.Name, reference.Qualifier?.Kind, extract.Written(reference.Qualifier))));
        Assert.Equal(["strtoupper", "name", "apply"], Calls(extract, "php:Shop.Cart.$label"));
        Assert.Empty(Calls(extract, "php:Shop.Cart.make"));

        // Its `new static()` makes the type it is written in: an instantiation of it, never a call.
        Assert.Equal([(CodeEdgeKinds.Instantiates, "Cart", "type", "Shop")], extract.References.Where(reference => reference.From == "php:Shop.Cart.make").Select(reference => (reference.Kind, reference.Name, reference.Qualifier?.Kind, extract.Written(reference.Qualifier))));
    }

    /// <summary>
    /// A namespace in braces holds what they do, the global one's among them, and one ended by its semicolon what follows
    /// to the next: each the scope of what it declares, its <c>use</c> declarations its own.
    /// </summary>
    [Fact]
    public void ANamespace_InBracesOrToTheNext_IsTheScopeOfWhatItDeclares()
    {
        var braced = Read("src/b.php", """
            <?php
            namespace A {
                use B\Thing;
                function f() { Thing::make(); }
            }
            namespace C {
                function g() { Thing::make(); }
            }
            namespace {
                function h() {}
            }
            """);
        var ended = Read("src/e.php", """
            <?php
            namespace A;
            class X {}
            namespace B\Deep;
            class Y {}
            """);

        Assert.Equal([("php:A", 2, 5), ("php:A.f", 4, 4), ("php:C", 6, 8), ("php:C.g", 7, 7), ("php:h", 10, 10)], braced.Nodes.Select(node => (node.Id.Whole(), node.Start, node.End)));
        Assert.Equal([("make", "B.Thing"), ("make", "C.Thing")], braced.References.Select(reference => (reference.Name, braced.Written(reference.Qualifier))));
        Assert.Equal([("php:A", 2, 3), ("php:A.X", 3, 3), ("php:B.Deep", 4, 5), ("php:B.Deep.Y", 5, 5)], ended.Nodes.Select(node => (node.Id.Whole(), node.Start, node.End)));
        Assert.Equal([("A", null), ("A.X", "A"), ("B.Deep", null), ("B.Deep.Y", "B.Deep")], ended.Scopes.Select(scope => (scope.Name.Whole(), scope.Parent?.Whole())));
        Assert.Equal("Deep", Node(ended, "php:B.Deep").Name);
    }

    /// <summary>
    /// Each <c>use</c> - of a class, a function or a constant, grouped, aliased, after a leading <c>\</c> - is an import:
    /// a class's of it by the name it is reached by, a function's and a constant's of its namespace showing its name; and
    /// a name a reference writes is written out from them.
    /// </summary>
    [Fact]
    public void EachUse_IsAnImport_AndWritesOutTheNamesThatReachIt()
    {
        var extract = Read("src/u.php", """
            <?php
            namespace App;
            use Lib\{One, Two as Second, function helper, const LIMIT};
            use function Lib\Fns\run as go;
            use const Lib\MAX;
            use \Lib\Lead;

            function call() {
                One::a();
                Second::b();
                helper();
                go();
                Lead::c();
                Second\Deep::d();
                Two\Deep::e();
            }
            """);

        Assert.Equal(
            [
                ("Lib.One", "One", null), ("Lib.Two", "Second", null), ("Lib", null, "helper"), ("Lib", null, "LIMIT"), ("Lib.Fns", null, "run"),
                ("Lib", null, "MAX"), ("Lib.Lead", "Lead", null),
            ],
            extract.Imports.Select(import => (import.Target, import.Prefix, import.Show is null ? null : string.Join(',', import.Show))));
        Assert.All(extract.Imports, import => Assert.Equal(CodeImportForms.Scope, import.Form));
        Assert.Equal(
            [("a", "Lib.One"), ("b", "Lib.Two"), ("helper", "Lib"), ("run", "Lib.Fns"), ("c", "Lib.Lead"), ("d", "Lib.Two.Deep"), ("e", "App.Two.Deep")],
            extract.References.Select(reference => (reference.Name, extract.Written(reference.Qualifier))));
        Assert.All(extract.References, reference => Assert.Equal(CodeQualifierKinds.Type, reference.Qualifier?.Kind));
    }

    /// <summary>
    /// A name is written out as PHP resolves it: after a leading <c>\</c> the name itself, <c>namespace\</c> the file's
    /// namespace's, a class's in its namespace alone; an unqualified function's in its namespace and then the global one,
    /// as the linker finds it - and one of the global namespace among its declarations alone; a <c>new</c> an instantiation
    /// of the class it names.
    /// </summary>
    [Fact]
    public void AName_IsWrittenOutAsPhpResolvesIt_AFunctionsInItsNamespaceThenTheGlobalOne()
    {
        var extract = Read("src/n.php", """
            <?php
            namespace App;
            function go() {
                \Lib\full();
                \strlen('x');
                namespace\local();
                Sub\inner();
                plain();
                new \Top();
                new Near();
            }
            """);

        Assert.Equal(
            [("full", "type", "Lib"), ("strlen", "scope", null), ("local", "type", "App"), ("inner", "type", "App.Sub"), ("plain", "scope", "App"), ("Top", "scope", null), ("Near", "type", "App")],
            extract.References.Select(reference => (reference.Name, reference.Qualifier?.Kind, extract.Written(reference.Qualifier))));
        Assert.Equal([CodeEdgeKinds.Instantiates, CodeEdgeKinds.Instantiates], extract.References.Where(reference => reference.Name is "Top" or "Near").Select(reference => reference.Kind));
        Assert.All(extract.References.Where(reference => reference.Name is "Top" or "Near"), reference => Assert.Null(reference.Arity));

        var linked = Link(
            ("src/a.php", "<?php\nnamespace App;\nfunction go() {\n    helper();\n    local();\n    strlen('x');\n}\n"),
            ("src/h.php", "<?php\nfunction helper() {}\nfunction local() {}\n"),
            ("src/l.php", "<?php\nnamespace App;\nfunction local() {}\n"),
            ("src/g.php", "<?php\nfunction top() {\n    local();\n}\n"));

        Assert.Equal(
            [("php:App.go", "php:helper", 4), ("php:App.go", "php:App.local", 5), ("php:top", "php:local", 3)],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Calls).Select(edge => (edge.From, edge.To, edge.Line)));
        Assert.All(linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Calls), edge => Assert.Equal(CodeConfidences.Exact, edge.Confidence));
    }

    /// <summary>
    /// A call's receiver is what the file says it is: <c>$this</c>, a chain of its properties, a typed parameter, a local a
    /// <c>new</c> made, a caught exception, a closure's typed parameter, <c>self</c>, <c>static</c> and <c>$this</c>'s
    /// static calls, <c>parent</c>'s, a class's name; a variable of no said type, and a chain through one, a value.
    /// </summary>
    [Fact]
    public void AReceiver_IsWhatTheFileSaysItIs()
    {
        var extract = Read("src/r.php", """
            <?php
            namespace App;
            use Lib\Mailer;
            class Svc
            {
                private Mailer $mailer;
                private static ?Svc $instance = null;
                public function run(Mailer $m, $untyped): void
                {
                    $this->go();
                    $this->mailer->send();
                    $this->mailer?->queue();
                    $m->direct();
                    $untyped->loose();
                    $made = new Mailer();
                    $made->fresh();
                    self::$instance->again();
                    self::stat();
                    static::late();
                    parent::up();
                    Mailer::make();
                    \Lib\Mailer::full();
                    $this::mine();
                    try { } catch (Problem $e) { $e->report(); }
                    $fn = function (Mailer $inner) { $inner->closure(); };
                    $x->a->b();
                    make()->m->chained();
                }
            }
            """);

        Assert.Equal(
            [
                ("go", "this", null), ("send", "value", "$mailer"), ("queue", "value", "$mailer"), ("direct", "type", "Lib.Mailer"), ("loose", "value", null),
                ("fresh", "type", "Lib.Mailer"), ("again", "value", "$instance"), ("stat", "this", null), ("late", "this", null),
                ("up", "base", null), ("make", "type", "Lib.Mailer"), ("full", "type", "Lib.Mailer"), ("mine", "this", null), ("report", "type", "App.Problem"),
                ("closure", "type", "Lib.Mailer"), ("b", "value", null), ("make", "scope", "App"), ("chained", "value", null),
            ],
            extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Calls).Select(reference => (reference.Name, reference.Qualifier?.Kind, extract.Written(reference.Qualifier))));

        // The new of a class the file imports is an instantiation of it, by the name it is written out to, with no arity.
        Assert.Equal([("Mailer", "type", "Lib", 15, null)], extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Instantiates).Select(reference => (reference.Name, reference.Qualifier?.Kind, extract.Written(reference.Qualifier), reference.Line, reference.Arity)));
        Assert.Contains(extract.References, reference => reference is { Kind: CodeEdgeKinds.References, Name: "Problem", Line: 24 });
    }

    /// <summary>
    /// What a reader cannot bind is marked dynamic, and not linked: a method or a function the code names by a variable,
    /// call_user_func and its array form, eval, a <c>new</c> and a static call of a class the code computes, and an
    /// inclusion of a path it computes.
    /// </summary>
    [Fact]
    public void WhatCannotBeBound_IsMarkedDynamic()
    {
        var extract = Read("src/d.php", """
            <?php
            function go($obj, $name, $f, $class) {
                $obj->$name();
                $f();
                call_user_func('x');
                call_user_func_array([$obj, 'y'], []);
                eval('z();');
                new $class();
                new ($class . 'X')();
                $class::make();
                include $name . '.php';
            }
            """);

        Assert.Equal(
            [
                (3, "a call of a method the code names by the variable $name"), (4, "a call through the function value $f"), (5, "a call through call_user_func"),
                (6, "a call through call_user_func_array"), (7, "a call of eval"), (8, "a new of a class the code computes"), (9, "a new of a class the code computes"),
                (10, "a static call on the class the code names by $class"), (11, "an inclusion of a file whose path the code computes"),
            ],
            extract.Dynamic.Select(dynamic => (dynamic.Line, dynamic.Why)));
        Assert.Equal(Enumerable.Repeat(PhpReading.VariableCall, 8).Append(PhpReading.UnfollowedInclude), extract.Dynamic.Select(dynamic => dynamic.Kind));
        CodeGraphContract.Signed(new PhpExtractor(), extract, "src/d.php");
        Assert.Empty(extract.References);
    }

    /// <summary>
    /// <c>require</c>, <c>include</c> and their <c>_once</c> forms of a path the code writes - a text, or one joined to
    /// <c>__DIR__</c> or to <c>dirname(__DIR__)</c> - import it once, by its path from the file's directory; a URL or an
    /// absolute path is no file of the tree; one interpolated is dynamic.
    /// </summary>
    [Fact]
    public void AnInclusion_OfAPathTheCodeWrites_IsAnImportOfIt()
    {
        var extract = Read("src/boot/i.php", """
            <?php
            require 'lib/a.php';
            require_once __DIR__ . '/b.php';
            include(dirname(__DIR__) . '/c.php');
            include_once dirname(__DIR__, 2) . '/d.php';
            require 'lib/a.php';
            require 'http://example.com/x.php';
            require '/abs/y.php';
            require "e{$x}.php";
            $v = include 'f.php';
            """);

        Assert.Equal(
            [("lib/a.php", 2), ("b.php", 3), ("../c.php", 4), ("../../d.php", 5), ("f.php", 10)],
            extract.Imports.Select(import => (import.Target, import.Line)));
        Assert.All(extract.Imports, import => Assert.Equal(CodeImportForms.Relative, import.Form));
        Assert.Equal([9], extract.Dynamic.Select(dynamic => dynamic.Line));

        var linked = Link(("src/boot/start.php", "<?php\nrequire __DIR__ . '/../lib/x.php';\n"), ("src/lib/x.php", "<?php\nfunction x() {}\n"));

        Assert.Contains(new CodeEdge("file:src/boot/start.php", "file:src/lib/x.php", CodeEdgeKinds.Imports, 2, CodeConfidences.Exact), linked.Edges);
    }

    /// <summary>
    /// A class that extends a TestCase, or is named for a test and extends a class, is a test class: its public methods
    /// named test..., and any method marked <c>#[Test]</c> or <c>@test</c>, are tests; a <c>#[\Override]</c> method
    /// overrides its base's.
    /// </summary>
    [Fact]
    public void ATestClassesTestMethods_AreTests_AndAnOverrideOverrides()
    {
        var extract = Read("tests/CartTest.php", """
            <?php
            namespace Tests;
            use PHPUnit\Framework\TestCase as Base;
            class CartTest extends Base
            {
                public function testAdds(): void {}
                #[Test]
                public function removes(): void {}
                /** @test */
                public function empties(): void {}
                /** @testing nothing */
                public function helper(): void {}
                protected function testHidden(): void {}
            }
            class ShopTest extends Fixture { public function testOpen(): void {} }
            class Shop extends Fixture { public function testNot(): void {} }
            abstract class FeatureTestCase extends \Orchestra\Testbench\TestCase { public function testInherited(): void {} }
            """);

        Assert.Equal(
            ["php:Tests.CartTest.testAdds", "php:Tests.CartTest.removes", "php:Tests.CartTest.empties", "php:Tests.ShopTest.testOpen", "php:Tests.FeatureTestCase.testInherited"],
            extract.Nodes.Where(node => node.Test).Select(node => node.Id.Whole()));

        var linked = Link(
            ("src/A.php", "<?php\nclass A { public function m(): void {} }\n"),
            ("src/B.php", "<?php\nclass B extends A {\n    #[\\Override]\n    public function m(): void {}\n}\n"));

        Assert.Contains(new CodeEdge("php:B.m", "php:A.m", CodeEdgeKinds.Overrides, 4, CodeConfidences.Exact), linked.Edges);
    }

    /// <summary>
    /// In a file under tests/ or test/, each of Pest's test, it and describe calls is a node of its own, named by its
    /// description as written, held by the group it is in, what its closure calls its own; elsewhere they are calls.
    /// </summary>
    [Fact]
    public void ATestFilesPestCalls_AreTests_NamedByTheirDescriptions()
    {
        const string Text = """
            <?php
            describe('group', function () {
                beforeEach(fn () => setup());
                it('works', function () { run(); })->skip();
                test("also", fn () => run());
            });
            it('top', function () { run(); });
            """;

        foreach (var path in (string[])["tests/Feature/x.php", "test/x.php", "packages/a/tests/x.php", "packages/a/test/x.php"])
        {
            var extract = Read(path, Text);

            Assert.Equal(
                [("'group'", "testGroup", 2, 6), ("'works'", "test", 4, 4), ("\"also\"", "test", 5, 5), ("'top'", "test", 7, 7)],
                extract.Nodes.Select(node => (node.Name, node.SubKind!, node.Start, node.End)));
            Assert.All(extract.Nodes, node => Assert.True(node.Test));
            Assert.Equal(extract.Nodes[0].Id, extract.Nodes[1].Parent);
            Assert.Equal(["beforeEach", "setup", "skip"], Calls(extract, extract.Nodes[0].Id.Whole()));
            Assert.Equal(["run"], Calls(extract, extract.Nodes[1].Id.Whole()));
        }

        Assert.DoesNotContain(Read("src/x.php", Text).Nodes, node => node.Test);
    }

    /// <summary>A file a generator wrote says so by what its first lines say.</summary>
    [Theory]
    [InlineData("<?php\n// @generated by a tool\nclass A {}", "its first lines say @generated, as a generator writes")]
    [InlineData("<?php\n/* Code generated. DO NOT EDIT. */\nclass A {}", "its first lines say DO NOT EDIT, as a generator writes")]
    [InlineData("<?php\n# DO NOT EDIT\nclass A {}", "its first lines say DO NOT EDIT, as a generator writes")]
    [InlineData("<?php\nclass A {}", null)]
    public void AGeneratedFile_SaysSo(string text, string? why) => Assert.Equal(why, Read("src/a.php", text).Generated);

    /// <summary>A close tag ends a statement as its semicolon would, and the code of each open tag after - <c>&lt;?=</c> among them - is the file's script.</summary>
    [Fact]
    public void ACloseTag_EndsAStatement_AndEachOpenTagsCodeIsTheScripts()
    {
        var extract = Read("views/a.php", """
            <?php function f() {} ?>
            <b><?= f() ?></b>
            <?php if (f()): ?><i><?php f() ?></i><?php endif ?>
            <?php $x = 1 ?><?php function g() {} ?>
            """);

        Assert.Equal([("php:f", 1, 1), ("php:views/a.php.(script)", 1, 4), ("php:g", 4, 4)], extract.Nodes.Select(node => (node.Id.Whole(), node.Start, node.End)));
        Assert.Equal([2, 3, 3], extract.References.Where(reference => reference.Name == "f").Select(reference => reference.Line));
    }

    /// <summary>
    /// Blocks nested five thousand deep are read whole - a function declared in the innermost of as many conditionals a
    /// node, what it calls its own, the function after them read; a call in the innermost of as many braces a body writes
    /// its call - nothing marked, in a child given a small stack: a reading of a block's statements that recursed a frame a
    /// block would overflow it, and end the child.
    /// </summary>
    [Fact]
    public async Task ABlockFiveThousandDeep_IsReadWhole()
        => Assert.Equal(new ChildRunAnswer(0, "deep True deep 0", ""), await ChildRun.MethodAsync(typeof(PhpExtractorTests), nameof(BlocksFiveThousandDeep), TestContext.Current.CancellationToken));

    /// <summary>
    /// The child of <see cref="ABlockFiveThousandDeep_IsReadWhole"/>: what the function the conditionals hold calls, whether
    /// the function after them is read, what the function the braces are in calls, and how many places they mark dynamic.
    /// </summary>
    internal static string BlocksFiveThousandDeep() => SmallStack.Run(() =>
    {
        var conditionals = Read("src/c.php", "<?php " + string.Concat(Enumerable.Repeat("if (true) {", Deep)) + "function f() { deep(); }" + new string('}', Deep) + "\nfunction after() {}\n");
        var braces = Read("src/b.php", "<?php function go() { " + new string('{', Deep) + "deep();" + new string('}', Deep) + " }");

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{string.Join(',', Calls(conditionals, "php:f"))} {conditionals.Nodes.Any(node => node.Id == "php:after")} {string.Join(',', Calls(braces, "php:go"))} {conditionals.Dynamic.Count + braces.Dynamic.Count}");
    });

    /// <summary>
    /// Interpolations nested five thousand deep - each a call whose argument is a string of the next - are read whole, each
    /// a run of code read in its turn, nothing marked, in a child given a small stack: a reading that recursed a frame an
    /// interpolation would overflow it.
    /// </summary>
    [Fact]
    public async Task AnInterpolationFiveThousandDeep_IsReadWhole()
        => Assert.Equal(new ChildRunAnswer(0, "5000 b 0 1", ""), await ChildRun.MethodAsync(typeof(PhpExtractorTests), nameof(InterpolationsFiveThousandDeep), TestContext.Current.CancellationToken));

    /// <summary>
    /// The child of <see cref="AnInterpolationFiveThousandDeep_IsReadWhole"/>: how many calls the function makes, of what,
    /// how many places it marks dynamic, and how many nodes the file holds.
    /// </summary>
    internal static string InterpolationsFiveThousandDeep() => SmallStack.Run(() =>
    {
        var extract = Read("src/s.php", "<?php function go($a) { return " + string.Concat(Enumerable.Repeat("\"{$a->b(", Deep)) + "1" + string.Concat(Enumerable.Repeat(")}\"", Deep)) + "; }");
        var calls = Calls(extract, "php:go").ToList();

        return string.Create(CultureInfo.InvariantCulture, $"{calls.Count} {string.Join(',', calls.Distinct(StringComparer.Ordinal))} {extract.Dynamic.Count} {extract.Nodes.Count}");
    });

    /// <summary>
    /// Pest's groups nested five thousand deep are read whole - each a node held by the one it is in, the innermost test's
    /// calls its own, nothing marked - in a child given a small stack: a reading that recursed a frame a group would
    /// overflow it.
    /// </summary>
    [Fact]
    public async Task ATestGroupFiveThousandDeep_IsReadWhole()
        => Assert.Equal(new ChildRunAnswer(0, "5000 True deep 0", ""), await ChildRun.MethodAsync(typeof(PhpExtractorTests), nameof(TestGroupsFiveThousandDeep), TestContext.Current.CancellationToken));

    /// <summary>
    /// The child of <see cref="ATestGroupFiveThousandDeep_IsReadWhole"/>: how many groups it reads, whether each but the
    /// first is held by the group before it, what the innermost test calls, and how many places it marks dynamic.
    /// </summary>
    internal static string TestGroupsFiveThousandDeep() => SmallStack.Run(() =>
    {
        var extract = Read("tests/g.php", "<?php " + string.Concat(Enumerable.Repeat("describe('g', function () {", Deep)) + "it('t', function () { deep(); });" + string.Concat(Enumerable.Repeat("});", Deep)));
        var groups = extract.Nodes.Where(node => node.SubKind == "testGroup").ToList();
        var test = Assert.Single(extract.Nodes, node => node.SubKind == "test");
        var held = groups.Skip(1).Select((group, at) => group.Parent == groups[at].Id).All(holds => holds) && test.Parent == groups[^1].Id;

        return string.Create(CultureInfo.InvariantCulture, $"{groups.Count} {held} {string.Join(',', Calls(extract, test.Id.Whole()))} {extract.Dynamic.Count}");
    });

    /// <summary>
    /// A word after an arrow or colons is a member's name, whatever word it is - <c>new</c>, <c>catch</c>,
    /// <c>include</c>, <c>instanceof</c>, <c>function</c> - and a call or a use of it, never the keyword; an anonymous
    /// class's method's parameter's type is a reference, as any type the body writes is.
    /// </summary>
    [Fact]
    public void AKeywordAfterAnArrowOrColons_IsAMembersName()
    {
        var extract = Read("src/k.php", """
            <?php
            function go($q, $x) {
                Factory::new()->make();
                $q->catch(fn ($e) => handle($e));
                Blade::include('a', 'b');
                $x->include('f.php');
                $x?->instanceof();
                $x->build(include: true, require: 1, new: 2, catch: 3);
                $ok = $x->function instanceof Foo;
                return new class { public function new() {} public function include($p) {} public function catch(Foo $e) {} const require = 1; };
            }
            """);

        Assert.Equal(
            [
                ("new", "type", "Factory"), ("make", "value", null), ("catch", "value", null), ("handle", "scope", null), ("include", "type", "Blade"), ("include", "value", null),
                ("instanceof", "value", null), ("build", "value", null), ("$function", "value", null), ("Foo", "scope", null), ("Foo", "scope", null),
            ],
            extract.References.Select(reference => (reference.Name, reference.Qualifier?.Kind, extract.Written(reference.Qualifier))));
        Assert.Empty(extract.Imports);
        Assert.Empty(extract.Dynamic);
    }

    /// <summary>
    /// A type a signature, a property, <c>instanceof</c>, <c>::class</c> or a <c>catch</c> names is a reference of it, but
    /// PHP's own types; a value's type is one class alone - nullable, with null, <c>self</c> the type it is written in - a
    /// union none, and a catch of two binds its variable to neither; a typed constant is named as an untyped one is.
    /// </summary>
    [Fact]
    public void ATypeTheCodeNames_IsAReference_AndAValuesTypeIsOneClassAlone()
    {
        var extract = Read("src/t.php", """
            <?php
            namespace App;
            class T
            {
                private self $a;
                private A|B $b;
                private ?\Lib\X $c;
                private null|Resource $d;
                const string NAME = 'x';
                const int ONE = 1, TWO = 2;
                public function f(Resource $r, int $n, array &$list, Handle &$h, $rest = 1): Boolean
                {
                    $ok = $r instanceof Handle;
                    $name = Handle::class;
                    try { } catch (First|Second $e) { $e->m(); }
                    $h->close();
                    return true;
                }
            }
            """);

        Assert.Equal(["App.T", null, "Lib.X", "App.Resource"], ((string[])["php:App.T.$a", "php:App.T.$b", "php:App.T.$c", "php:App.T.$d"]).Select(id => Node(extract, id).ValueType));
        Assert.Equal(
            [
                ("php:App.T.$b", "A", 6), ("php:App.T.$b", "B", 6), ("php:App.T.$c", "X", 7), ("php:App.T.$d", "Resource", 8), ("php:App.T.f", "Boolean", 11),
                ("php:App.T.f", "Resource", 11), ("php:App.T.f", "Handle", 11), ("php:App.T.f", "Handle", 13), ("php:App.T.f", "Handle", 14), ("php:App.T.f", "First", 15), ("php:App.T.f", "Second", 15),
            ],
            extract.References.Where(reference => reference.Kind == CodeEdgeKinds.References).Select(reference => (reference.From.Whole(), reference.Name, reference.Line)));
        Assert.Equal(
            [("m", "value", null), ("close", "type", "App.Handle")],
            extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Calls).Select(reference => (reference.Name, reference.Qualifier?.Kind, extract.Written(reference.Qualifier))));
        Assert.Equal(["php:App.T.NAME", "php:App.T.ONE", "php:App.T.TWO"], extract.Nodes.Where(node => node.SubKind == "constant").Select(node => node.Id.Whole()));
        Assert.Equal((4, (int?)5), Arity(extract, "php:App.T.f"));
    }

    /// <summary>
    /// A conditional's blocks - <c>if</c>, <c>elseif</c>, <c>else if</c> and <c>else</c> - declare what they hold, as PHP
    /// declares a function where it is not yet; an anonymous class's calls and an arrow function's typed parameter - its
    /// type a reference - are the body's they are written in.
    /// </summary>
    [Fact]
    public void AConditionalsBlocksDeclare_AndAnAnonymousClassesCallsAreTheBodys()
    {
        var extract = Read("src/c.php", """
            <?php
            use Lib\Mailer;
            if (a()) { function f() {} } elseif (b()) { function g() {} } else if (c()) { function h() {} } else { function i() {} }
            function go() {
                $f = fn (Mailer $m) => $m->send();
                return new class { public function m() { inner(); } };
            }
            """);

        Assert.Equal(["php:src/c.php.(script)", "php:f", "php:g", "php:h", "php:i", "php:go"], extract.Nodes.Select(node => node.Id.Whole()));
        Assert.Equal(["a", "b", "c"], Calls(extract, "php:src/c.php.(script)"));
        Assert.Equal(
            [("Mailer", "type", "Lib"), ("send", "type", "Lib.Mailer"), ("inner", "scope", null)],
            extract.References.Where(reference => reference.From == "php:go").Select(reference => (reference.Name, reference.Qualifier?.Kind, extract.Written(reference.Qualifier))));
    }

    /// <summary>
    /// A call of a value the code computes - one an element holds, a member it names in braces, what a call or a
    /// parenthesis gives - is marked dynamic; a condition's, a cast's and a construct's parentheses, a closure called where
    /// it is written and a <c>new</c> of a computed class are no such call.
    /// </summary>
    [Fact]
    public void ACallOfAValueTheCodeComputes_IsMarkedDynamic_AndNoOtherParenthesis()
    {
        var extract = Read("src/v.php", """
            <?php
            function go($a, $o, $n, $f, $x, $c) {
                $a['k']();
                $o->{$n}();
                make()();
                ($f)();
                (function () { inside(); })();
                $i = (int) ($x);
                if ($x) ($o)->m();
                $p = #[Pure] (fn () => 3)();
                wrap(function () {})();
                return (fn () => 1)() + (static fn () => 2)();
            }
            #[Pure] function pure() {}
            """);

        Assert.Equal(
            [
                (3, "a call of a value an element holds"), (4, "a call of a member the code names in braces"), (5, "a call of the value a call or a parenthesis gives"),
                (6, "a call of the value a call or a parenthesis gives"),
                (11, "a call of the value a call or a parenthesis gives"),
            ],
            extract.Dynamic.Select(dynamic => (dynamic.Line, dynamic.Why)));
        Assert.Equal(Enumerable.Repeat(PhpReading.VariableCall, 5), extract.Dynamic.Select(dynamic => dynamic.Kind));
        CodeGraphContract.Signed(new PhpExtractor(), extract, "src/v.php");
        Assert.Equal(["make", "inside", "m", "wrap"], Calls(extract, "php:go"));
        Assert.Single(Read("src/w.php", "<?php function go($c) { return new ($c)(); }").Dynamic);
    }

    /// <summary>
    /// <c>#[Test]</c> and <c>@test</c> mark a test in a test class or a trait one uses, an <c>@test</c> past an
    /// <c>@testdox</c> among them, and in no other type.
    /// </summary>
    [Fact]
    public void ATestsMark_IsATestClasssOrATraits()
    {
        var extract = Read("tests/m.php", """
            <?php
            trait Checks
            {
                #[Test]
                public function marked(): void {}
                public function testNamed(): void {}
            }
            class Helper
            {
                #[Test]
                public function marked(): void {}
            }
            class OrderTest extends TestCase
            {
                /**
                 * @testdox It orders.
                 * @test
                 */
                public function orders(): void {}
            }
            """);

        Assert.Equal(["php:Checks.marked", "php:OrderTest.orders"], extract.Nodes.Where(node => node.Test).Select(node => node.Id.Whole()));
    }

    /// <summary>
    /// A name a <c>use</c> declaration gives is reached whatever its case, as PHP reaches it, and a <c>#</c> comment above a
    /// declaration is its summary.
    /// </summary>
    [Fact]
    public void AUsesName_IsReachedInAnyCase_AndAHashCommentIsASummary()
    {
        var extract = Read("src/u.php", """
            <?php
            use Lib\Mailer;
            use function Lib\helper;
            # Computes the tax.
            function tax() {
                mailer::make();
                HELPER();
            }
            """);

        Assert.Equal([("make", "Lib.Mailer"), ("helper", "Lib")], extract.References.Select(reference => (reference.Name, extract.Written(reference.Qualifier))));
        Assert.Equal("Computes the tax.", Node(extract, "php:tax").Summary);
    }

    /// <summary>
    /// A visibility of setting - <c>private(set)</c> - is one modifier of a property, or of a promoted parameter, its type
    /// still the value's; and what follows <c>__halt_compiler();</c> is no code.
    /// </summary>
    [Fact]
    public void ASetVisibility_IsOneModifier_AndNothingPastHaltCompilerIsCode()
    {
        var extract = Read("src/s.php", """
            <?php
            namespace Shop;
            class Order
            {
                public private(set) Customer $customer;
                protected(set) string $state = '';
                public function __construct(public private(set) Customer $buyer) {}
            }
            __halt_compiler();
            function evil() { boom(); } class Data {}
            """);

        Assert.Equal(["public", "private(set)"], Node(extract, "php:Shop.Order.$customer").Modifiers);
        Assert.Equal("Shop.Customer", Node(extract, "php:Shop.Order.$customer").ValueType);
        Assert.Equal(["protected(set)"], Node(extract, "php:Shop.Order.$state").Modifiers);
        Assert.Equal(["public", "private(set)"], Node(extract, "php:Shop.Order.$buyer").Modifiers);
        Assert.Equal("Shop.Customer", Node(extract, "php:Shop.Order.$buyer").ValueType);
        Assert.Equal(["Customer", "Customer"], extract.References.Select(reference => reference.Name));
        Assert.DoesNotContain(extract.Nodes, node => node.Name is "evil" or "Data");
        var halted = PhpLexer.Scan("<?php __halt_compiler(); x();").Scan;
        var unhalted = PhpLexer.Scan("<?php __halt_compiler(1); f();").Scan;

        Assert.Equal(["__halt_compiler", "(", ")", ";"], Enumerable.Range(0, halted.Count).Select(at => halted.Text(at)));
        Assert.Equal(["__halt_compiler", "(", "1", ")", ";", "f", "(", ")", ";"], Enumerable.Range(0, unhalted.Count).Select(at => unhalted.Text(at)));
    }

    /// <summary>
    /// A property's hooks are read past their attributes, <c>final</c> and <c>&amp;</c>; a static property's or a variable
    /// member's call is a value of no name; and <c>&lt;?</c> before white space opens code.
    /// </summary>
    [Fact]
    public void AHooksHead_IsPassedOver_AndAComputedReceiverIsAValueOfNoName()
    {
        var extract = Read("src/h.php", """
            <?
            class H
            {
                public string $a {
                    final get => first();
                    #[Pure] set { second(); }
                }
                public function go($b) {
                    Foo::$bar->m();
                    $this->$b->n();
                }
            }
            """);

        Assert.Equal(["first", "second"], Calls(extract, "php:H.$a"));

        // The call of m on Foo::$bar's value is a value's; the static property read before it is a use of $bar through Foo.
        Assert.Equal([("$bar", "type", "Foo"), ("m", "value", null), ("n", "value", null)], extract.References.Where(reference => reference.From == "php:H.go").Select(reference => (reference.Name, reference.Qualifier?.Kind, extract.Written(reference.Qualifier))));
        Assert.Empty(extract.Dynamic);
    }

    /// <summary>
    /// An inclusion's path the code computes is read as the code it is - <c>dirname(__DIR__, 0)</c>'s among them, which
    /// climbs nowhere - and one that climbs past the tree's root is of a file outside it; and a text is the path its quotes
    /// say, its escapes read as PHP reads them - <c>'c\\d.php'</c> is <c>c\d.php</c> - and a <c>$</c> in single quotes no
    /// variable.
    /// </summary>
    [Fact]
    public void AComputedInclusionsCode_IsRead_AndOnlyAPlainTextIsAPath()
    {
        var extract = Read("src/i.php", """
            <?php
            require base_path('routes/web.php');
            require dirname(__DIR__, 0) . '/a.php';
            require dirname(__DIR__, 40) . '/b.php';
            require 'c\\d.php';
            require 'e$f.php';
            """);

        Assert.Equal(["base_path", "dirname"], Calls(extract, "php:src/i.php.(script)"));
        Assert.Equal(
            [
                (2, "an inclusion of a file whose path the code computes"), (3, "an inclusion of a file whose path the code computes"),
                (4, "an inclusion of a file outside the tree: dirname climbs 40 directories, above the tree's root"),
            ],
            extract.Dynamic.Select(dynamic => (dynamic.Line, dynamic.Why)));
        Assert.Equal(Enumerable.Repeat(PhpReading.UnfollowedInclude, 3), extract.Dynamic.Select(dynamic => dynamic.Kind));
        CodeGraphContract.Signed(new PhpExtractor(), extract, "src/i.php");
        Assert.Equal(["c\\d.php", "e$f.php"], extract.Imports.Select(import => import.Target));
    }

    /// <summary>
    /// <c>dirname(__DIR__, n)</c> climbs any n directories: in a file forty directories deep, forty reach the tree's root
    /// and include the file there, and forty-one climb past it, an inclusion of a file outside the tree.
    /// </summary>
    [Fact]
    public void DirnameFortyLevels_ResolvesOrSaysOutside()
    {
        var path = string.Concat(Enumerable.Repeat("d/", 40)) + "i.php";
        var text = """
            <?php
            require dirname(__DIR__, 40) . '/root.php';
            require dirname(__DIR__, 41) . '/above.php';
            """;
        var extract = Read(path, text);

        Assert.Equal([(string.Concat(Enumerable.Repeat("../", 40)) + "root.php", 2)], extract.Imports.Select(import => (import.Target, import.Line)));
        Assert.Equal([(3, "an inclusion of a file outside the tree: dirname climbs 41 directories, above the tree's root", PhpReading.UnfollowedInclude)], extract.Dynamic.Select(dynamic => (dynamic.Line, dynamic.Why, dynamic.Kind)));
        CodeGraphContract.Signed(new PhpExtractor(), extract, path);
        Assert.Empty(extract.References);

        var linked = Link((path, text), ("root.php", "<?php\nfunction root() {}\n"));

        Assert.Contains(new CodeEdge($"file:{path}", "file:root.php", CodeEdgeKinds.Imports, 2, CodeConfidences.Exact), linked.Edges);
    }

    /// <summary>
    /// A namespace is declared at the file's top alone - one nested in another or in a block is none - so no nesting of
    /// them takes the reader past its stack; and a chain of any length is read in a time of its length.
    /// </summary>
    [Fact]
    public void NamespacesNestedAndLongChains_NeverOverflowNorCrawl()
    {
        var namespaces = Read("src/n.php", "<?php " + string.Concat(Enumerable.Repeat("namespace{", 5_000)) + "function f() {}" + new string('}', 5_000));
        var mixed = Read("src/m.php", "<?php " + string.Concat(Enumerable.Repeat("if(1){namespace{", 2_000)) + new string('}', 4_000));
        var chain = Read("src/c.php", "<?php function go() { $this" + string.Concat(Enumerable.Repeat("->a", 200_000)) + "->go(); }");

        Assert.Empty(namespaces.Nodes);
        Assert.Empty(mixed.Nodes);

        // The call is one; every member the chain reads is a use of it, in a time of the chain's length.
        Assert.Equal(CodeQualifierKinds.Value, Assert.Single(chain.References, reference => reference.Kind == CodeEdgeKinds.Calls).Qualifier?.Kind);
        Assert.Equal(200_000, chain.References.Count(reference => reference.Kind == CodeEdgeKinds.Uses && reference.Name == "$a"));
    }

    /// <summary>
    /// A string's value is what PHP reads of it: a single-quoted one's escapes <c>\\</c> and <c>\'</c> alone, a
    /// double-quoted one's every escape, a heredoc's and a nowdoc's lines past their closing indentation from the line after
    /// their label - a nowdoc's as written - and an interpolated one's constant parts each a literal of its own: a bare
    /// hole from its <c>$</c>, with one <c>-&gt;name</c> or one <c>[...]</c> after it, a <c>${name}</c> a variable read by its
    /// name and no code, a key in brackets the hole's own.
    /// </summary>
    [Fact]
    public void AStringsValue_IsWhatPhpReadsOfIt_HeredocsNowdocsAndHolesAmongThem()
    {
        var extract = Read("src/t.php", """
            <?php
            function greet($user, $items, $row)
            {
                $a = "Hello $user";
                $b = "Dear $user->name, welcome";
                $c = "$items[0] left $row[key] end";
                $d = "${name} and {$user->title}";
                $e = 'C:\temp \'quoted\'';
                $f = "Tab\there \u{48}\x49 \$x";
                $g = <<<EOT
                    first line
                      second line
                    EOT;
                $h = <<<'EOT'
                    raw $x\n
                    EOT;
            }
            """);

        Assert.Equal(
            [
                new LiteralRow("Hello ", 4, "php:greet"),
                new LiteralRow("Dear ", 5, "php:greet"),
                new LiteralRow(", welcome", 5, "php:greet"),
                new LiteralRow(" left ", 6, "php:greet"),
                new LiteralRow(" end", 6, "php:greet"),
                new LiteralRow(" and ", 7, "php:greet"),
                new LiteralRow("C:\\temp 'quoted'", 8, "php:greet"),
                new LiteralRow("Tab\there HI $x", 9, "php:greet"),
                new LiteralRow("first line\n  second line", 11, "php:greet"),
                new LiteralRow("raw $x\\n", 15, "php:greet"),
            ],
            LiteralRow.Of(extract));
        Assert.Equal(
            [("$name", 5, CodeQualifierKinds.Value), ("$title", 7, CodeQualifierKinds.Value)],
            extract.References.Select(reference => (reference.Name, reference.Line, reference.Qualifier?.Kind)));
    }

    /// <summary>
    /// A use is of a value the code reads: a nullsafe read's property by its <c>$</c>, each branch of a conditional and a
    /// short one, a named argument's value and not its name; a word or a type of the language - <c>true</c>, a cast's, a
    /// closure's return type - is none; a constant read alone looked for in its namespace and then the global one; and a
    /// constant's, a property's and an enum case's value, a parameter's default, and the arguments of the attributes each of
    /// them - a parameter among them - is written with are the code of what they are written on.
    /// </summary>
    [Fact]
    public void AUseIsOfAValueTheCodeReads_DeclaredValuesAndAttributesTheirDeclarations()
    {
        var extract = Read("src/Users.php", """
            <?php
            namespace App;

            #[Route('/users')]
            class Users
            {
                #[Deprecated('old_const')]
                public const GREETING = 'hello world';
                #[Inject('svc_prop')]
                protected $table = 'users';

                #[Get('/list')]
                public function list(#[Sensitive('param_attr')] User $user, $mode = DEFAULT_MODE): ?string
                {
                    $n = $user?->name;
                    $t = $debug ? DEBUG_LEVEL : INFO_LEVEL;
                    $u = $x ?: FALLBACK_LEVEL;
                    $v = fn ($x): ?Model => (string) $x;
                    build(named: VALUE);
                    return true;
                }
            }

            enum Status: string
            {
                #[Label('case_label')]
                case Active = 'active';
            }
            """);

        const string List = "php:App.Users.list";

        Assert.Equal(
            [
                new LiteralRow("/users", 4, "php:App.Users"),
                new LiteralRow("old_const", 7, "php:App.Users.GREETING"),
                new LiteralRow("hello world", 8, "php:App.Users.GREETING"),
                new LiteralRow("svc_prop", 9, "php:App.Users.$table"),
                new LiteralRow("users", 10, "php:App.Users.$table"),
                new LiteralRow("/list", 12, List),
                new LiteralRow("param_attr", 13, List),
                new LiteralRow("case_label", 26, "php:App.Status.Active"),
                new LiteralRow("active", 27, "php:App.Status.Active"),
            ],
            LiteralRow.Of(extract));
        Assert.Equal(
            [
                (List, "DEFAULT_MODE", 13, "scope App"), (List, "$name", 15, "type App.User"), (List, "DEBUG_LEVEL", 16, "scope App"), (List, "INFO_LEVEL", 16, "scope App"),
                (List, "FALLBACK_LEVEL", 17, "scope App"), (List, "VALUE", 19, "scope App"),
            ],
            extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Uses).Select(reference => (reference.From.Whole(), reference.Name, reference.Line, $"{reference.Qualifier?.Kind} {extract.Written(reference.Qualifier)}".Trim())));
    }

    /// <summary>A call's receiver of any number of names is read by them all - a long chain is read once, not crawled from each member.</summary>
    [Fact]
    public void AReceiverOfAnyNames_IsReadByThemAll()
    {
        var extract = Read("src/Run.php", """
            <?php
            class Run
            {
                function run()
                {
                    $this->b->c->d->e->f->g->h->go();
                    $this->b->c->d->e->f->g->h->i->go();
                }
            }
            """);

        Assert.Equal(
            [(6, "value $b.$c.$d.$e.$f.$g.$h"), (7, "value $b.$c.$d.$e.$f.$g.$h.$i")],
            extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Calls).Select(reference => (reference.Line, $"{reference.Qualifier?.Kind} {extract.Written(reference.Qualifier)}".Trim())));
    }

    /// <summary>
    /// A receiver of sixty properties is written as links: the use of its first is <c>$this</c>'s, the use of its second
    /// names the first, and the use of each from the third on and the call each link to the use of the one before - each
    /// read once, from the use before it - and its sixty names are read back whole.
    /// </summary>
    [Fact]
    public void ASixtyNameChain_IsWrittenAsLinks()
    {
        var names = Enumerable.Range(1, 60).Select(at => string.Create(CultureInfo.InvariantCulture, $"n{at}")).ToList();
        var text = $"<?php\nclass Run {{ function run() {{ $this->{string.Join("->", names)}->go(); }} }}\n";
        var extract = Read("src/Run.php", text);
        var first = extract.References.ToList().FindIndex(reference => reference.Name == "$n1");

        Assert.Equal([.. names.Select(name => "$" + name), "go"], extract.References.Skip(first).Select(reference => reference.Name));
        Assert.Equal(new ExtractQualifier(CodeQualifierKinds.This), extract.References[first].Qualifier);
        Assert.Equal(new ExtractQualifier(CodeQualifierKinds.Value, "$n1"), extract.References[first + 1].Qualifier);
        Assert.Equal(
            Enumerable.Range(first + 1, 59).Select(link => new ExtractQualifier(CodeQualifierKinds.Value) { Link = link }),
            extract.References.Skip(first + 2).Select(reference => reference.Qualifier));
        Assert.Equal(string.Join('.', names.Select(name => "$" + name)), extract.Written(extract.References[^1].Qualifier));
        Assert.Null(extract.Problem("php", CodeFileText.Lines(text).Count));
    }

    /// <summary>
    /// A parameter's type a body writes - a closure's, an arrow function's, each member of a union, a by-reference, a
    /// variadic and an intersection one's - names classes, references and no uses, as a closure's return type does; a
    /// member named as a word or a type of the language is a use, a class's <c>::class</c> none, and a label a
    /// <c>goto</c> names none.
    /// </summary>
    [Fact]
    public void AParametersTypeInABody_IsAReference_AMemberNamedAsAWordAUse_AndALabelNone()
    {
        var extract = Read("src/V.php", """
            <?php
            namespace App;

            class V
            {
                public function go($node)
                {
                    $f = function (Foo|Bar $x, ?Baz $y, Qux &$z, Many ...$more, A&B $both): ?Ret { return $x; };
                    $g = fn (Arrowed $v): int => $v;
                    $k = FieldType::String . $node->parent . $node->object . Foo::class;
                    if ($node) goto done;
                    done:
                    return $k;
                }
            }
            """);

        Assert.Equal(
            [
                (CodeEdgeKinds.References, "Foo", 8), (CodeEdgeKinds.References, "Bar", 8), (CodeEdgeKinds.References, "Baz", 8), (CodeEdgeKinds.References, "Qux", 8),
                (CodeEdgeKinds.References, "Many", 8), (CodeEdgeKinds.References, "A", 8), (CodeEdgeKinds.References, "B", 8), (CodeEdgeKinds.References, "Ret", 8),
                (CodeEdgeKinds.References, "Arrowed", 9),
                (CodeEdgeKinds.Uses, "String", 10), (CodeEdgeKinds.Uses, "$parent", 10), (CodeEdgeKinds.Uses, "$object", 10), (CodeEdgeKinds.References, "Foo", 10),
            ],
            extract.References.Where(reference => reference.From == "php:App.V.go").Select(reference => (reference.Kind, reference.Name, reference.Line)));
    }

    /// <summary>
    /// A name written alone in a class's code - a constant read in a method, a closure, an arrow function, a constant's, a property's and a
    /// parameter's default value, and a call - reaches the constant or the function PHP resolves it to, in the namespace and then the global
    /// one, never a member of the class, of its base or of its trait; <c>$this-&gt;</c> and <c>self::</c> reach those.
    /// </summary>
    [Fact]
    public void ABareName_InAClassesCode_NeverReachesAMemberOfTheClassItsBasesOrItsTraits()
    {
        var linked = Link(
            ("src/box.php", """
                <?php
                namespace App;

                const LIMIT = 10;

                function helper(): int { return 1; }

                trait Helps
                {
                    public const DEPTH = 2;
                    public function assist(): int { return 0; }
                }

                class Base
                {
                    public const ROOT = 1;
                }

                class Box extends Base
                {
                    use Helps;

                    public const LIMIT = 5;
                    public const ALIAS = LIMIT;
                    public int $size = LIMIT;

                    public function helper(int $n = LIMIT): int
                    {
                        $f = function () { return $this->assist() + LIMIT; };
                        $g = static fn () => helper();
                        return helper() + LIMIT + ROOT + DEPTH + assist() + self::LIMIT + $this->assist();
                    }
                }
                """),
            ("src/top.php", "<?php\nconst DEPTH = 7;\nfunction assist(): int { return 0; }\n"));
        var own = linked.Edges.Where(edge => edge.From.StartsWith("php:App.Box.", StringComparison.Ordinal) && edge.Kind is CodeEdgeKinds.Calls or CodeEdgeKinds.Uses).ToList();

        Assert.Equal(
            [
                (24, "php:App.Box.ALIAS", CodeEdgeKinds.Uses, "php:App.LIMIT"),
                (25, "php:App.Box.$size", CodeEdgeKinds.Uses, "php:App.LIMIT"),
                (27, "php:App.Box.helper", CodeEdgeKinds.Uses, "php:App.LIMIT"),
                (29, "php:App.Box.helper", CodeEdgeKinds.Calls, "php:App.Helps.assist"),
                (29, "php:App.Box.helper", CodeEdgeKinds.Uses, "php:App.LIMIT"),
                (30, "php:App.Box.helper", CodeEdgeKinds.Calls, "php:App.helper"),
                (31, "php:App.Box.helper", CodeEdgeKinds.Calls, "php:App.Helps.assist"),
                (31, "php:App.Box.helper", CodeEdgeKinds.Calls, "php:App.helper"),
                (31, "php:App.Box.helper", CodeEdgeKinds.Calls, "php:assist"),
                (31, "php:App.Box.helper", CodeEdgeKinds.Uses, "php:App.Box.LIMIT"),
                (31, "php:App.Box.helper", CodeEdgeKinds.Uses, "php:App.LIMIT"),
                (31, "php:App.Box.helper", CodeEdgeKinds.Uses, "php:DEPTH"),
            ],
            own.Select(edge => (edge.Line, edge.From, edge.Kind, edge.To))
                .OrderBy(edge => edge.Line).ThenBy(edge => edge.From, StringComparer.Ordinal).ThenBy(edge => edge.Kind, StringComparer.Ordinal).ThenBy(edge => edge.To, StringComparer.Ordinal));
        Assert.All(own, edge => Assert.Equal(CodeConfidences.Exact, edge.Confidence));
    }

    /// <summary>
    /// A bare constant is written out as PHP resolves it: the one a <c>use const</c> gives it - single, aliased, grouped, matched by its case -
    /// else its namespace's and then the global one; one after a leading <c>\</c> the name itself, and a qualified one by its first part as a
    /// class's is.
    /// </summary>
    [Fact]
    public void ABareConstant_IsWrittenOutAsPhpResolvesIt_AUseConstsElseItsNamespacesThenTheGlobalOne()
    {
        var extract = Read("src/n.php", """
            <?php
            namespace App;
            use Lib\Config;
            use const Lib\MAX;
            use const Lib\MIN as FLOOR;
            use Lib\{const DEPTH};
            function go() {
                return LIMIT + \LIMIT + \Lib\TOP + Sub\DEEP + namespace\OWN + MAX + FLOOR + DEPTH + Max + Config\SIZE;
            }
            """);

        Assert.Equal(
            [
                ("LIMIT", "scope", "App"), ("LIMIT", "scope", null), ("TOP", "type", "Lib"), ("DEEP", "type", "App.Sub"), ("OWN", "type", "App"),
                ("MAX", "type", "Lib"), ("MIN", "type", "Lib"), ("DEPTH", "type", "Lib"), ("Max", "scope", "App"), ("SIZE", "type", "Lib.Config"),
            ],
            extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Uses).Select(reference => (reference.Name, reference.Qualifier?.Kind, extract.Written(reference.Qualifier))));

        var global = Read("src/g.php", """
            <?php
            function go() { return LIMIT + namespace\OWN + \Top\X; }
            """);

        Assert.Equal(
            [("LIMIT", "scope", null), ("OWN", "scope", null), ("X", "type", "Top")],
            global.References.Select(reference => (reference.Name, reference.Qualifier?.Kind, global.Written(reference.Qualifier))));
    }

    /// <summary>
    /// A bare constant is its namespace's, else the global one - never another namespace's, never the repository's by its name: a global
    /// function's bare name is the global declaration's alone where a namespaced one of that name exists too.
    /// </summary>
    [Fact]
    public void ABareConstant_IsItsNamespacesElseTheGlobalOne_NeverAnotherNamespacesNorTheRepositorys()
    {
        var linked = Link(
            ("src/a.php", "<?php\nnamespace App;\nuse const Lib\\MAX;\nfunction go() {\n    return LIMIT + ONLY_GLOBAL + MAX + NOWHERE;\n}\n"),
            ("src/app.php", "<?php\nnamespace App;\nconst LIMIT = 1;\nconst ONLY_APP = 7;\n"),
            ("src/top.php", "<?php\nconst LIMIT = 2;\nconst ONLY_GLOBAL = 3;\nfunction top() {\n    return LIMIT + ONLY_APP;\n}\n"),
            ("src/lib.php", "<?php\nnamespace Lib;\nconst MAX = 9;\n"),
            ("src/other.php", "<?php\nnamespace Other;\nconst NOWHERE = 5;\n"));

        Assert.Equal(
            [
                ("php:App.go", "php:App.LIMIT", 5, CodeConfidences.Exact),
                ("php:App.go", "php:Lib.MAX", 5, CodeConfidences.Exact),
                ("php:App.go", "php:ONLY_GLOBAL", 5, CodeConfidences.Exact),
                ("php:top", "php:LIMIT", 5, CodeConfidences.Exact),
            ],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Uses).Select(edge => (edge.From, edge.To, edge.Line, edge.Confidence))
                .OrderBy(edge => edge.From, StringComparer.Ordinal).ThenBy(edge => edge.Line).ThenBy(edge => edge.To, StringComparer.Ordinal));
    }

    /// <summary>
    /// An anonymous class's head, traits and member types are references of the code it is written in - an attributed property's and one a
    /// visibility of setting modifies alike, the attribute and the <c>set</c> of its <c>private(set)</c> no use - and the names it declares
    /// - its constants, a hooked property's hooks - no use, what a hook calls and reads the code's; <c>new readonly class</c> and
    /// <c>new #[A] class</c> are anonymous classes too, their <c>readonly</c> and their attribute no class and their clauses clauses.
    /// </summary>
    [Fact]
    public void AnAnonymousClassesHeadTraitsAndMemberTypes_AreReferences_AndTheNamesItDeclaresNoUses()
    {
        var extract = Read("src/anonymous.php", Anonymous);

        Assert.Equal(
            [
                (CodeEdgeKinds.Uses, "LIMIT", "scope", "Made", 5), (CodeEdgeKinds.References, "Base", "type", "Lib", 5),
                (CodeEdgeKinds.References, "Countable", "scope", null, 5), (CodeEdgeKinds.References, "Shape", "type", "Made", 5),
                (CodeEdgeKinds.References, "Helps", "type", "Made", 6), (CodeEdgeKinds.References, "Logs", "type", "Made", 6),
                (CodeEdgeKinds.References, "Foo", "type", "Made", 7), (CodeEdgeKinds.References, "Bar", "type", "Made", 7),
                (CodeEdgeKinds.References, "Baz", "type", "Made", 8), (CodeEdgeKinds.Uses, "SIZE", "scope", "Made", 9),
                (CodeEdgeKinds.Uses, "N", "scope", "Made", 10),
                (CodeEdgeKinds.References, "Foo", "type", "Made", 11), (CodeEdgeKinds.References, "Bar", "type", "Made", 12),
                (CodeEdgeKinds.References, "Foo", "type", "Made", 13), (CodeEdgeKinds.Calls, "helper", "scope", "Made", 13),
                (CodeEdgeKinds.Uses, "$z", "this", null, 13),
            ],
            extract.References.Where(reference => reference.From == "php:Made.make")
                .Select(reference => (reference.Kind, reference.Name, reference.Qualifier?.Kind, extract.Written(reference.Qualifier), reference.Line)));

        var readonlyClass = Read("src/r.php", "<?php\nfunction f() { return new readonly class extends Base {}; }\nfunction g() { return new #[A] class extends Base {}; }\n");

        Assert.Equal(
            [(CodeEdgeKinds.References, "Base", "scope", (string?)null, 2), (CodeEdgeKinds.References, "Base", "scope", (string?)null, 3)],
            readonlyClass.References.Select(reference => (reference.Kind, reference.Name, reference.Qualifier?.Kind, readonlyClass.Written(reference.Qualifier), reference.Line)));
    }

    /// <summary>
    /// A class, an interface and an enum a function's body declares are read as declared: the types their heads name references of the body, and
    /// no name they declare - the types, an enum's cases - a use; the <c>from</c> of a <c>yield</c> is a keyword, no name.
    /// </summary>
    [Fact]
    public void AClassABodyDeclares_UsesNoneOfItsNames_AndYieldsFromIsNoName()
    {
        var extract = Read("src/anonymous.php", Anonymous);

        Assert.Equal(
            [
                (CodeEdgeKinds.References, "Outer", "type", "Made", 17),
                (CodeEdgeKinds.References, "Foo", "type", "Made", 18), (CodeEdgeKinds.Calls, "helper", "scope", "Made", 18),
                (CodeEdgeKinds.References, "Sized", "type", "Made", 19),
                (CodeEdgeKinds.Uses, "HEARTS", "scope", "Made", 20), (CodeEdgeKinds.Calls, "gen", "scope", "Made", 21),
            ],
            extract.References.Where(reference => reference.From == "php:Made.boot")
                .Select(reference => (reference.Kind, reference.Name, reference.Qualifier?.Kind, extract.Written(reference.Qualifier), reference.Line)));
    }

    /// <summary>
    /// A property with hooks that a body declares - in an anonymous class, or in a class a function's body declares - is no node: each hook's
    /// name, where the hook begins - past its attributes, its <c>final</c> and its <c>&amp;</c>, as a type's own hooks begin - is a declared name and
    /// no use, a short hook's among them, and what its arrow's expression or its body calls and reads is the call and the use of the
    /// declaration the code is written in. A call an arrow's expression begins with is a call, never taken for a hook's name.
    /// </summary>
    [Fact]
    public void AHookedPropertyABodyDeclares_DeclaresItsHooksNames_AndReadsWhatTheyCall()
    {
        const string Text = """
            <?php
            namespace App;
            function make() {
                return new class {
                    public Foo $x { get => helper(); set(string $v) { $this->x = $v; } }
                    public int $count { get => count($this->items); }
                    public Bar $y { #[A] get; final set => strtoupper('a'); }
                };
            }
            function build() {
                class Wired { public Foo $w { &get => strtoupper('b'); } }
            }
            """;
        var extract = Read("src/h.php", Text);

        Assert.Equal(
            [
                (CodeEdgeKinds.References, "Foo", 5), (CodeEdgeKinds.Calls, "helper", 5), (CodeEdgeKinds.Uses, "$x", 5),
                (CodeEdgeKinds.Calls, "count", 6), (CodeEdgeKinds.Uses, "$items", 6),
                (CodeEdgeKinds.References, "Bar", 7), (CodeEdgeKinds.Calls, "strtoupper", 7),
            ],
            extract.References.Where(reference => reference.From == "php:App.make").Select(reference => (reference.Kind, reference.Name, reference.Line)));
        Assert.Equal(
            [(CodeEdgeKinds.References, "Foo", 11), (CodeEdgeKinds.Calls, "strtoupper", 11)],
            extract.References.Where(reference => reference.From == "php:App.build").Select(reference => (reference.Kind, reference.Name, reference.Line)));
        Assert.Null(extract.Problem("php", CodeFileText.Lines(Text).Count));
    }

    /// <summary>
    /// A constructor's promoted parameter that a body declares - in an anonymous class, or in a class a function's body declares - is read
    /// past its attributes and its modifiers as a type's own is: a visibility of setting, <c>private(set)</c>, is one modifier, whose
    /// <c>set</c> is no class and no constant; the class each parameter is typed is a reference, and nothing else of its head a use.
    /// </summary>
    [Fact]
    public void APromotedParameterABodyDeclares_IsReadPastItsModifiers_AVisibilityOfSettingOneOfThem()
    {
        var extract = Read("src/p.php", """
            <?php
            namespace App;
            function make() {
                return new class {
                    public function __construct(private(set) Foo $x, #[A] protected readonly Bar $y = null) { $this->x = $y; }
                };
            }
            function build() {
                class Wired { public function __construct(public(set) Baz $z, public Qux $w = QUUX) {} }
            }
            """);

        Assert.Equal(
            [(CodeEdgeKinds.References, "Foo", 5), (CodeEdgeKinds.References, "Bar", 5), (CodeEdgeKinds.Uses, "$x", 5)],
            extract.References.Where(reference => reference.From == "php:App.make").Select(reference => (reference.Kind, reference.Name, reference.Line)));
        Assert.Equal(
            [(CodeEdgeKinds.References, "Baz", 9), (CodeEdgeKinds.References, "Qux", 9), (CodeEdgeKinds.Uses, "QUUX", 9)],
            extract.References.Where(reference => reference.From == "php:App.build").Select(reference => (reference.Kind, reference.Name, reference.Line)));
    }

    /// <summary>
    /// A constant a <c>define</c> names by a text is a node of the namespace that name writes - the file's current namespace not applied to it -
    /// held by that namespace's node, so a name written whole and a bare name alike reach it as a <c>const</c>'s is reached; a <c>define</c> of a
    /// name the code computes, or of a text that is no name, declares none.
    /// </summary>
    [Fact]
    public void ADefinedConstant_IsANodeOfTheNamespaceItsNameWrites_AndABareNameReachesIt()
    {
        const string Text = """
            <?php
            namespace App;
            define('LIMIT', 10);
            define('App\STAMP', 2);
            define('Other\FLAG', 3);
            define($computed, 3);
            define('not-a-name', 4);
            function go() {
                if (!defined('DEBUG')) { define('DEBUG', false); }
                return LIMIT + STAMP + DEBUG + \Other\FLAG;
            }
            """;
        var extract = Read("src/d.php", Text);

        Assert.Equal(
            [("php:LIMIT", (string?)null, (string?)null, 3, 3), ("php:App.STAMP", "App", "php:App", 4, 4), ("php:Other.FLAG", "Other", "php:Other", 5, 5), ("php:DEBUG", null, null, 9, 9)],
            extract.Nodes.Where(node => node.SubKind == "constant").Select(node => (node.Id.Whole(), node.Scope?.Whole(), node.Parent?.Whole(), node.Start, node.End)));

        // A namespace a define names that the file does not declare is a node of its own over the define's lines, an open scope.
        Assert.Equal((CodeNodeKinds.Namespace, 5, 5, "namespace Other"), (Node(extract, "php:Other").Kind, Node(extract, "php:Other").Start, Node(extract, "php:Other").End, Node(extract, "php:Other").Signature));
        Assert.Equal([("Other", true)], extract.Scopes.Where(scope => scope.Name == "Other").Select(scope => (scope.Name.Whole(), scope.Open)));

        var linked = Link(("src/d.php", Text));
        var uses = linked.Edges.Where(edge => edge.From == "php:App.go" && edge.Kind == CodeEdgeKinds.Uses).ToList();

        Assert.Equal(
            [("php:App.STAMP", 10), ("php:DEBUG", 10), ("php:LIMIT", 10), ("php:Other.FLAG", 10)],
            uses.Select(edge => (edge.To, edge.Line)).OrderBy(edge => edge.To, StringComparer.Ordinal));
        Assert.All(uses, edge => Assert.Equal(CodeConfidences.Exact, edge.Confidence));
    }

    /// <summary>
    /// A <c>define</c> declares its constant wherever the code runs - in a string's interpolation too, read as code of its own - held by the
    /// file where it names no namespace.
    /// </summary>
    [Fact]
    public void ADefinedConstant_IsANodeWhereverTheCodeRuns()
    {
        const string Text = """
            <?php
            function go($x) {
                return "{$x = define('HOLE', 1)}";
            }
            """;
        var extract = Read("src/i.php", Text);

        Assert.Equal([("php:HOLE", (string?)null, (string?)null, 3, 3, "define('HOLE', 1)")], extract.Nodes.Where(node => node.SubKind == "constant").Select(node => (node.Id.Whole(), node.Scope?.Whole(), node.Parent?.Whole(), node.Start, node.End, node.Signature)));
        Assert.Equal([("define", CodeEdgeKinds.Calls, 3)], extract.References.Select(reference => (reference.Name, reference.Kind, reference.Line)));
        Assert.Null(extract.Problem("php", CodeFileText.Lines(Text).Count));
    }

    /// <summary>
    /// A PHP name reaches only the kind PHP resolves it to: a call a function or a method - a member taking arguments - a constant read a
    /// constant, a <c>const</c>'s, a <c>define</c>'s, an enum case or a property, and a <c>new</c> a class; of one name, never another kind.
    /// </summary>
    [Fact]
    public void APhpName_ReachesOnlyTheKindPhpResolvesItTo_AFunctionAConstantOrAClass()
    {
        const string Text = """
            <?php
            namespace App;
            const Cart = 1;
            define('App\Total', 2);
            function Cart(): int { return 3; }
            function Total(): int { return 4; }
            class Cart
            {
                public const Size = 5;
                public static function Size(): int { return 6; }
                public function go(): array
                {
                    $made = new Cart();
                    $called = Cart();
                    $read = Cart;
                    $sized = [self::Size, self::Size(), Cart::Size, Cart::Size()];
                    $suits = [Suit::Hearts, Suit::Hearts()];
                    return [Total, Total()];
                }
            }
            enum Suit
            {
                case Hearts;
                public static function Hearts(): int { return 7; }
            }
            """;
        var extract = Read("src/k.php", Text);
        var linked = Link(("src/k.php", Text));
        var reached = linked.Edges.Where(edge => edge.From == "php:App.Cart.go").ToList();

        string Of(string qualified, string subKind) => Assert.Single(extract.Nodes, node => node.Qualified.Whole() == qualified && node.SubKind == subKind).Id.Whole();

        Assert.Equal(
            [
                (13, CodeEdgeKinds.Instantiates, Of("App.Cart", "class")), (14, CodeEdgeKinds.Calls, Of("App.Cart", "function")), (15, CodeEdgeKinds.Uses, Of("App.Cart", "constant")),
                (16, CodeEdgeKinds.Calls, Of("App.Cart.Size", "method")), (16, CodeEdgeKinds.Uses, Of("App.Cart.Size", "constant")), (17, CodeEdgeKinds.Calls, Of("App.Suit.Hearts", "method")),
                (17, CodeEdgeKinds.Uses, Of("App.Suit.Hearts", "case")), (18, CodeEdgeKinds.Calls, Of("App.Total", "function")), (18, CodeEdgeKinds.Uses, Of("App.Total", "constant")),
            ],
            reached.Select(edge => (edge.Line, edge.Kind, edge.To)).Distinct()
                .OrderBy(edge => edge.Line).ThenBy(edge => edge.Kind, StringComparer.Ordinal).ThenBy(edge => edge.To, StringComparer.Ordinal));
        Assert.All(reached, edge => Assert.Equal(CodeConfidences.Exact, edge.Confidence));
    }

    /// <summary>
    /// A class name PHP's reader writes out is the one of that whole name: found as the file's <c>use</c> declarations and its namespace write
    /// it out to, whatever the scopes the code is in declare by that name, whatever an import makes visible where its target is not in the
    /// tree, and whatever the repository declares whose name ends with it - as a property's type is too.
    /// </summary>
    [Fact]
    public void AClassNamePhpWritesOut_IsTheOneOfThatWholeName_NeverOneTheScopesOrItsEndingFind()
    {
        const string Clock = "<?php\nclass Clock { public function tick(): int { return 0; } public static function make(): int { return 0; } }\n";
        var linked = Link(
            ("src/clock.php", Clock),
            ("src/app.php", Clock.Replace("<?php\n", "<?php\nnamespace App;\n", StringComparison.Ordinal)),
            ("src/lib.php", "<?php\nnamespace App\\Lib;\nclass Mailer { public function send(): void {} }\n"),
            ("src/box.php", """
                <?php
                namespace App;
                use Lib\Mailer;
                class Box
                {
                    private \Clock $clock;
                    private Mailer $mailer;
                    public function go(): void
                    {
                        $this->clock->tick();
                        \Clock::make();
                        $this->mailer->send();
                        \Lib\Mailer::send();
                        Clock::make();
                    }
                }
                """));
        var calls = linked.Edges.Where(edge => edge is { From: "php:App.Box.go", Kind: CodeEdgeKinds.Calls }).ToList();

        Assert.Equal([(10, "php:Clock.tick"), (11, "php:Clock.make"), (14, "php:App.Clock.make")], calls.Select(edge => (edge.Line, edge.To)));
        Assert.All(calls, edge => Assert.Equal(CodeConfidences.Exact, edge.Confidence));
    }

    /// <summary>
    /// No reference PHP's reader writes is unqualified: each says where PHP looks - a namespace and the global one, a type, <c>$this</c>, a base,
    /// a value - so no PHP name is ever looked for through the scopes its code is in.
    /// </summary>
    [Fact]
    public void EveryReferenceThePhpReaderWrites_SaysWhereItIsLookedFor()
    {
        foreach (var fixture in Fixtures)
        {
            Assert.All(Read(fixture.Path, fixture.Text).References, reference => Assert.NotNull(reference.Qualifier));
        }
    }

    /// <summary>
    /// Every extract of PHP's says what its reader alone says of a file: that it keeps its functions, its constants and its classes apart, and
    /// that every class name it writes is written whole.
    /// </summary>
    [Fact]
    public void EveryExtractOfPhp_SaysItKeepsItsNamesKindsApart_AndWritesItsClassNamesWhole()
    {
        Assert.All(Fixtures, fixture =>
        {
            var extract = Read(fixture.Path, fixture.Text);

            Assert.True(extract.KindsApart, fixture.Path);
            Assert.True(extract.WrittenWhole, fixture.Path);
        });
    }

    /// <summary>How deep the depth tests nest what they read.</summary>
    private const int Deep = 5_000;

    private static FileExtract Read(string path, string text) => new PhpExtractor().Extract(new CodeFileInput(path, text));

    private static ExtractNode Node(FileExtract extract, string id) => Assert.Single(extract.Nodes, node => node.Id == id);

    private static (int, int) Lines(FileExtract extract, string id) => (Node(extract, id).Start, Node(extract, id).End);

    private static (int?, int?) Arity(FileExtract extract, string id) => (Node(extract, id).MinArity, Node(extract, id).MaxArity);

    private static IEnumerable<string> Calls(FileExtract extract, string from)
        => extract.References.Where(reference => reference.From == from && reference.Kind == CodeEdgeKinds.Calls).Select(reference => reference.Name);

    /// <summary>The files linked together, each read by PHP's implementation.</summary>
    private static CodeGraphLinked Link(params (string Path, string Text)[] files)
        => CodeGraphLinker.Link([.. files.Select(file => new CodeGraphFile(file.Path, "php", Read(file.Path, file.Text)))]);
}
