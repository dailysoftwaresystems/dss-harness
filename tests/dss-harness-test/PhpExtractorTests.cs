using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;
using DssHarness.Core.Codebase.Languages.Scanning;

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


    /// <inheritdoc/>
    public override IReadOnlyList<CodeGraphFixture> Fixtures { get; } =
    [
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
    ];

    /// <summary>
    /// Text outside PHP's tags, comments, strings, heredocs, nowdocs and attributes are never read as code, and a string's
    /// or a heredoc's interpolation is - a nowdoc's not.
    /// </summary>
    [Fact]
    public void TextCommentsStringsAndAttributes_AreNeverReadAsCode_ButAnInterpolationIs()
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

        Assert.Equal(["seventh", "tenth", "fourteenth", "sixteenth"], Calls(extract, "php:go"));
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
            extract.Nodes.Select(node => (node.Qualified, node.SubKind!)));

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
                .Select(reference => (reference.From, reference.Kind, reference.Name, reference.Qualifier?.Kind, reference.Qualifier?.Name)));
        Assert.Equal(["strtoupper", "name", "apply"], Calls(extract, "php:Shop.Cart.$label"));
        Assert.Equal(["Cart"], Calls(extract, "php:Shop.Cart.make"));
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

        Assert.Equal([("php:A", 2, 5), ("php:A.f", 4, 4), ("php:C", 6, 8), ("php:C.g", 7, 7), ("php:h", 10, 10)], braced.Nodes.Select(node => (node.Id, node.Start, node.End)));
        Assert.Equal([("make", "B.Thing"), ("make", "C.Thing")], braced.References.Select(reference => (reference.Name, reference.Qualifier?.Name)));
        Assert.Equal([("php:A", 2, 3), ("php:A.X", 3, 3), ("php:B.Deep", 4, 5), ("php:B.Deep.Y", 5, 5)], ended.Nodes.Select(node => (node.Id, node.Start, node.End)));
        Assert.Equal([("A", null), ("A.X", "A"), ("B.Deep", null), ("B.Deep.Y", "B.Deep")], ended.Scopes.Select(scope => (scope.Name, scope.Parent)));
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
            extract.References.Select(reference => (reference.Name, reference.Qualifier?.Name)));
        Assert.All(extract.References, reference => Assert.Equal(CodeQualifierKinds.Type, reference.Qualifier?.Kind));
    }

    /// <summary>
    /// A name is written out as PHP resolves it: after a leading <c>\</c> the name itself, <c>namespace\</c> the file's
    /// namespace's, a class's in its namespace alone; an unqualified function's in its namespace and then the global one,
    /// as the linker finds it - and one of the global namespace among its declarations alone.
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
            extract.References.Select(reference => (reference.Name, reference.Qualifier?.Kind, reference.Qualifier?.Name)));

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
                ("Mailer", "type", "Lib"), ("fresh", "type", "Lib.Mailer"), ("again", "value", "$instance"), ("stat", "this", null), ("late", "this", null),
                ("up", "base", null), ("make", "type", "Lib.Mailer"), ("full", "type", "Lib.Mailer"), ("mine", "this", null), ("report", "type", "App.Problem"),
                ("closure", "type", "Lib.Mailer"), ("b", "value", null), ("make", "scope", "App"), ("chained", "value", null),
            ],
            extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Calls).Select(reference => (reference.Name, reference.Qualifier?.Kind, reference.Qualifier?.Name)));
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
            extract.Nodes.Where(node => node.Test).Select(node => node.Id));

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
            Assert.Equal(["beforeEach", "setup", "skip"], Calls(extract, extract.Nodes[0].Id));
            Assert.Equal(["run"], Calls(extract, extract.Nodes[1].Id));
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

        Assert.Equal([("php:f", 1, 1), ("php:views/a.php.(script)", 1, 4), ("php:g", 4, 4)], extract.Nodes.Select(node => (node.Id, node.Start, node.End)));
        Assert.Equal([2, 3, 3], extract.References.Where(reference => reference.Name == "f").Select(reference => reference.Line));
    }

    /// <summary>
    /// Code nested past the scanner's depth - interpolations, test groups, conditional declarations - is marked dynamic where
    /// it stops, and what is read before it is kept: no text nests the reader past its stack.
    /// </summary>
    [Fact]
    public void CodeNestedTooDeep_IsMarkedDynamic_AndNeverOverflows()
    {
        var strings = Read("src/s.php", $"<?php function go($a) {{ return {string.Concat(Enumerable.Repeat("\"{$a->b(", 200))}1{string.Concat(Enumerable.Repeat(")}\"", 200))}; }}");
        var groups = Read("tests/g.php", "<?php " + string.Concat(Enumerable.Repeat("describe('g', function () {", 200)) + "deep();" + string.Concat(Enumerable.Repeat("});", 200)));
        var conditionals = Read("src/c.php", "<?php " + string.Concat(Enumerable.Repeat("if (true) {", 400)) + "function f() {}" + string.Concat(Enumerable.Repeat("}", 400)));
        var braces = Read("src/b.php", "<?php function go() { " + new string('{', 400) + "deep();" + new string('}', 400) + " }");

        Assert.Single(strings.Dynamic, dynamic => dynamic.Why.Contains("nested more than", StringComparison.Ordinal));
        Assert.Single(groups.Dynamic, dynamic => dynamic.Why.Contains("nested more than", StringComparison.Ordinal));
        Assert.Contains(conditionals.Dynamic, dynamic => dynamic.Why == ScanFrame.TooDeepWhy);
        Assert.Single(strings.Nodes);
        Assert.DoesNotContain(conditionals.Nodes, node => node.Id == "php:f");
        Assert.Equal(["deep"], Calls(braces, "php:go"));
    }

    /// <summary>
    /// A word after an arrow or colons is a member's name, whatever word it is - <c>new</c>, <c>catch</c>,
    /// <c>include</c>, <c>instanceof</c> - and a call of it, never the keyword.
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
            [("new", "type", "Factory"), ("make", "value", null), ("catch", "value", null), ("handle", "scope", null), ("include", "type", "Blade"), ("include", "value", null), ("instanceof", "value", null), ("build", "value", null), ("Foo", "scope", null)],
            extract.References.Select(reference => (reference.Name, reference.Qualifier?.Kind, reference.Qualifier?.Name)));
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
            extract.References.Where(reference => reference.Kind == CodeEdgeKinds.References).Select(reference => (reference.From, reference.Name, reference.Line)));
        Assert.Equal(
            [("m", "value", null), ("close", "type", "App.Handle")],
            extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Calls).Select(reference => (reference.Name, reference.Qualifier?.Kind, reference.Qualifier?.Name)));
        Assert.Equal(["php:App.T.NAME", "php:App.T.ONE", "php:App.T.TWO"], extract.Nodes.Where(node => node.SubKind == "constant").Select(node => node.Id));
        Assert.Equal((4, (int?)5), Arity(extract, "php:App.T.f"));
    }

    /// <summary>
    /// A conditional's blocks - <c>if</c>, <c>elseif</c>, <c>else if</c> and <c>else</c> - declare what they hold, as PHP
    /// declares a function where it is not yet; an anonymous class's calls and an arrow function's typed parameter are the
    /// body's they are written in.
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

        Assert.Equal(["php:src/c.php.(script)", "php:f", "php:g", "php:h", "php:i", "php:go"], extract.Nodes.Select(node => node.Id));
        Assert.Equal(["a", "b", "c"], Calls(extract, "php:src/c.php.(script)"));
        Assert.Equal(
            [("send", "type", "Lib.Mailer"), ("inner", "scope", null)],
            extract.References.Where(reference => reference.From == "php:go").Select(reference => (reference.Name, reference.Qualifier?.Kind, reference.Qualifier?.Name)));
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

        Assert.Equal(["php:Checks.marked", "php:OrderTest.orders"], extract.Nodes.Where(node => node.Test).Select(node => node.Id));
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

        Assert.Equal([("make", "Lib.Mailer"), ("helper", "Lib")], extract.References.Select(reference => (reference.Name, reference.Qualifier?.Name)));
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
        Assert.Equal([("m", "value", null), ("n", "value", null)], extract.References.Where(reference => reference.From == "php:H.go").Select(reference => (reference.Name, reference.Qualifier?.Kind, reference.Qualifier?.Name)));
        Assert.Empty(extract.Dynamic);
    }

    /// <summary>
    /// An inclusion's path the code computes is read as the code it is; <c>dirname(__DIR__, n)</c> climbs from 1 to 31
    /// levels, and none else; and a text with an escape is computed, one with a <c>$</c> in single quotes not.
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

        Assert.Equal(["base_path", "dirname", "dirname"], Calls(extract, "php:src/i.php.(script)"));
        Assert.Equal([2, 3, 4, 5], extract.Dynamic.Select(dynamic => dynamic.Line));
        Assert.Equal(["e$f.php"], extract.Imports.Select(import => import.Target));
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
        Assert.Equal(CodeQualifierKinds.Value, Assert.Single(chain.References).Qualifier?.Kind);
    }

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
