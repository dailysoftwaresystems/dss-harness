using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;

namespace DssHarness.Tests;

/// <summary>Dart's implementation over the structural scanner, held to the contract's kit, and the rules of its own the kit does not name.</summary>
public sealed class DartExtractorTests : CodeGraphExtractorContractTests<DartExtractor>
{
    internal const string Shapes = """
        /// Geometry of shapes.
        library shapes;

        import 'dart:math' as math;
        import 'src/base.dart';
        export 'src/circle.dart' show Circle;

        part 'src/shapes_part.dart';

        /// A shape with an area.
        abstract class Shape extends Base with Describe implements Comparable<Shape> {
          const Shape(this.name);

          final String name;

          /// The area.
          double get area;

          @override
          int compareTo(Shape other) => area.compareTo(other.area);

          void describe() {
            final printer = Printer();
            printer.write('$name: ${_format(area)}');
            _log(name);
          }
        }

        class Square extends Shape {
          Square(this.side) : super('square');

          Square.unit() : this(1);

          factory Square.fromJson(Map<String, dynamic> json) => Square(json['side'] as double);

          final double side;

          @override
          double get area => side * side;

          Square operator +(Square other) => Square(side + other.side);

          set width(double value) {}
        }

        mixin Describe {
          String label() => 'shape';
        }

        enum Kind implements Comparable<Kind> {
          round('r'),
          angular.named();

          const Kind(this.code);

          const Kind.named() : code = 'a';

          final String code;

          @override
          int compareTo(Kind other) => code.compareTo(other.code);
        }

        extension ShapeArea on Shape {
          bool get isLarge => area > 10;

          double doubled() => area * 2;
        }

        extension type const Meters(double value) implements double {
          Meters plus(Meters other) => Meters(value + other.value);
        }

        typedef ShapeVisitor = void Function(Shape shape);

        double totalArea(List<Shape> shapes, {required ShapeVisitor visit, int limit = 3}) {
          var sum = 0.0;
          for (final shape in shapes) {
            visit(shape);
            sum += shape.area;
            shape.describe();
          }
          return math.max(sum, 0) + shapes.first.doubled();
        }
        """;

    internal const string Base = """
        /// What every shape is built on.
        class Base {
          void hello() {}
        }

        class Printer {
          void write(String text) {
            print(text);
          }
        }
        """;

    internal const string ShapesPart = """
        part of '../shapes.dart';

        void _log(String text) {
          Printer().write(text);
        }

        String _format(double value) => value.toStringAsFixed(2);
        """;

    internal const string Circle = """
        import 'base.dart';

        class Circle extends Base {
          Circle(this.radius);

          final double radius;

          double get area => 3.14 * radius * radius;
        }
        """;

    internal const string Model = """
        import 'package:json_annotation/json_annotation.dart';

        part 'model.g.dart';

        @JsonSerializable()
        class Model {
          Model(this.id);

          factory Model.fromJson(Map<String, dynamic> json) => _$ModelFromJson(json);

          final String id;
        }
        """;

    internal const string ModelGenerated = """
        // GENERATED CODE - DO NOT MODIFY BY HAND

        part of 'model.dart';

        Model _$ModelFromJson(Map<String, dynamic> json) => Model(json['id'] as String);
        """;

    internal const string ShapesTest = """
        import 'package:test/test.dart';
        import '../lib/shapes.dart';

        void main() {
          group('Square', () {
            test('has an area', () {
              final square = Square(2);
              expect(square.area, 4);
              square.describe();
            });
          });

          test('circles are exported', () {
            final circle = Circle(1);
            expect(circle.area, greaterThan(3));
          });
        }
        """;

    /// <inheritdoc/>
    public override IReadOnlyList<CodeGraphFixture> Fixtures { get; } =
    [
        new("lib/shapes.dart", Shapes)
        {
            Nodes =
            [
                new("dart:lib/shapes.dart.Shape", CodeNodeKinds.Type, (11, 27)),
                new("dart:lib/shapes.dart.Shape.new", CodeNodeKinds.Member, (12, 12)),
                new("dart:lib/shapes.dart.Shape.area", CodeNodeKinds.Member, (17, 17)),
                new("dart:lib/shapes.dart.Shape.compareTo", CodeNodeKinds.Member, (19, 20)),
                new("dart:lib/shapes.dart.Square.unit", CodeNodeKinds.Member, (32, 32)),
                new("dart:lib/shapes.dart.Square.operator+", CodeNodeKinds.Member, (41, 41)),
                new("dart:lib/shapes.dart.Square.width=", CodeNodeKinds.Member, (43, 43)),
                new("dart:lib/shapes.dart.Describe", CodeNodeKinds.Type, (46, 48)),
                new("dart:lib/shapes.dart.Kind.angular", CodeNodeKinds.Member, (52, 52)),
                new("dart:lib/shapes.dart.ShapeArea.doubled", CodeNodeKinds.Member, (67, 67)),
                new("dart:lib/shapes.dart.Meters.value", CodeNodeKinds.Member, (70, 70)),
                new("dart:lib/shapes.dart.ShapeVisitor", CodeNodeKinds.Type, (74, 74)),
                new("dart:lib/shapes.dart.totalArea", CodeNodeKinds.Member, (76, 84)),
            ],
            Edges =
            [
                new("file:lib/shapes.dart", "file:lib/src/base.dart", CodeEdgeKinds.Imports, 5, CodeConfidences.Exact),
                new("file:lib/shapes.dart", "file:lib/src/circle.dart", CodeEdgeKinds.Imports, 6, CodeConfidences.Exact),
                new("file:lib/shapes.dart", "file:lib/src/shapes_part.dart", CodeEdgeKinds.Imports, 8, CodeConfidences.Exact),
                new("dart:lib/shapes.dart.Shape", "dart:lib/src/base.dart.Base", CodeEdgeKinds.Inherits, 11, CodeConfidences.Unique),
                new("dart:lib/shapes.dart.Shape", "dart:lib/shapes.dart.Describe", CodeEdgeKinds.MixesIn, 11, CodeConfidences.Exact),
                new("dart:lib/shapes.dart.Shape.describe", "dart:lib/src/base.dart.Printer", CodeEdgeKinds.Instantiates, 23, CodeConfidences.Unique),
                new("dart:lib/shapes.dart.Shape.describe", "dart:lib/src/base.dart.Printer.write", CodeEdgeKinds.Calls, 24, CodeConfidences.Exact),
                new("dart:lib/shapes.dart.Shape.describe", "dart:lib/shapes.dart._format", CodeEdgeKinds.Calls, 24, CodeConfidences.Exact),
                new("dart:lib/shapes.dart.Shape.describe", "dart:lib/shapes.dart._log", CodeEdgeKinds.Calls, 25, CodeConfidences.Exact),
                new("dart:lib/shapes.dart.Square.new", "dart:lib/shapes.dart.Shape.new", CodeEdgeKinds.Calls, 30, CodeConfidences.Exact),
                new("dart:lib/shapes.dart.Square.unit", "dart:lib/shapes.dart.Square.new", CodeEdgeKinds.Calls, 32, CodeConfidences.Exact),
                new("dart:lib/shapes.dart.Square.fromJson", "dart:lib/shapes.dart.Square", CodeEdgeKinds.Instantiates, 34, CodeConfidences.Exact),
                new("dart:lib/shapes.dart.Square.area", "dart:lib/shapes.dart.Shape.area", CodeEdgeKinds.Overrides, 39, CodeConfidences.Exact),
                new("dart:lib/shapes.dart.Kind.round", "dart:lib/shapes.dart.Kind.new", CodeEdgeKinds.Calls, 51, CodeConfidences.Exact),
                new("dart:lib/shapes.dart.Kind.angular", "dart:lib/shapes.dart.Kind.named", CodeEdgeKinds.Calls, 52, CodeConfidences.Exact),
                new("dart:lib/shapes.dart.Meters.plus", "dart:lib/shapes.dart.Meters", CodeEdgeKinds.Instantiates, 71, CodeConfidences.Exact),
                new("dart:lib/shapes.dart.totalArea", "dart:lib/shapes.dart.ShapeVisitor", CodeEdgeKinds.References, 76, CodeConfidences.Exact),
                new("dart:lib/shapes.dart.totalArea", "dart:lib/shapes.dart.Shape.describe", CodeEdgeKinds.Calls, 81, CodeConfidences.Unique),
                new("dart:lib/shapes.dart.totalArea", "dart:lib/shapes.dart.ShapeArea.doubled", CodeEdgeKinds.Calls, 83, CodeConfidences.Unique),
            ],
        },
        new("lib/src/base.dart", Base)
        {
            Nodes = [new("dart:lib/src/base.dart.Base", CodeNodeKinds.Type, (2, 4)), new("dart:lib/src/base.dart.Printer.write", CodeNodeKinds.Member, (7, 9))],
        },
        new("lib/src/shapes_part.dart", ShapesPart)
        {
            Nodes = [new("dart:lib/shapes.dart._log", CodeNodeKinds.Member, (3, 5)), new("dart:lib/shapes.dart._format", CodeNodeKinds.Member, (7, 7))],
            Edges =
            [
                new("file:lib/src/shapes_part.dart", "file:lib/shapes.dart", CodeEdgeKinds.Imports, 1, CodeConfidences.Exact),
                new("dart:lib/shapes.dart._log", "dart:lib/src/base.dart.Printer", CodeEdgeKinds.Instantiates, 4, CodeConfidences.Unique),
                new("dart:lib/shapes.dart._log", "dart:lib/src/base.dart.Printer.write", CodeEdgeKinds.Calls, 4, CodeConfidences.Exact),
            ],
        },
        new("lib/src/circle.dart", Circle)
        {
            Nodes = [new("dart:lib/src/circle.dart.Circle", CodeNodeKinds.Type, (3, 9))],
            Edges = [new("dart:lib/src/circle.dart.Circle", "dart:lib/src/base.dart.Base", CodeEdgeKinds.Inherits, 3, CodeConfidences.Unique)],
        },
        new("lib/model.dart", Model)
        {
            Nodes = [new("dart:lib/model.dart.Model", CodeNodeKinds.Type, (5, 12)), new("dart:lib/model.dart.Model.fromJson", CodeNodeKinds.Member, (9, 9))],
            Edges = [new("dart:lib/model.dart.Model.fromJson", "dart:lib/model.dart._$ModelFromJson", CodeEdgeKinds.Calls, 9, CodeConfidences.Exact)],
        },
        new("lib/model.g.dart", ModelGenerated)
        {
            Nodes = [new("dart:lib/model.dart._$ModelFromJson", CodeNodeKinds.Member, (5, 5))],
            Edges = [new("dart:lib/model.dart._$ModelFromJson", "dart:lib/model.dart.Model", CodeEdgeKinds.Instantiates, 5, CodeConfidences.Exact)],
        },
        new("test/shapes_test.dart", ShapesTest)
        {
            Nodes =
            [
                new("dart:test/shapes_test.dart.main.'Square'", CodeNodeKinds.Member, (5, 11)),
                new("dart:test/shapes_test.dart.main.'Square'.'has an area'", CodeNodeKinds.Member, (6, 10)),
                new("dart:test/shapes_test.dart.main.'circles are exported'", CodeNodeKinds.Member, (13, 16)),
            ],
            Edges =
            [
                new("dart:test/shapes_test.dart.main.'Square'.'has an area'", "dart:lib/shapes.dart.Shape.describe", CodeEdgeKinds.Tests, 9, CodeConfidences.Exact),
                new("dart:test/shapes_test.dart.main.'circles are exported'", "dart:lib/src/circle.dart.Circle", CodeEdgeKinds.Instantiates, 14, CodeConfidences.Unique),
            ],
        },
    ];

    /// <summary>
    /// Comments - nested block ones among them - and strings - raw, triple-quoted over lines, holding what reads as code or
    /// a comment - are never read as code; an interpolation's code is, its own strings and braces with it, and a name may
    /// hold a dollar.
    /// </summary>
    [Fact]
    public void CommentsAndStrings_AreNeverReadAsCode_ButAnInterpolationsCodeIs()
    {
        var extract = Read("lib/a.dart", """
            void go() {
              /* first(1); /* nested(2); */ still(3); */
              // fourth(4);
              final raw = r'${fifth(5)}';
              final long = '''
                sixth(6) // not a comment
              ''';
              final it = "it's ${seventh(eighth('${ninth({1: 2})}'))} done";
              _$tenth(10);
            }
            """);

        Assert.Equal(["seventh", "eighth", "ninth", "_$tenth"], Calls(extract, "dart:lib/a.dart.go"));
        Assert.Equal((1, 10), Lines(extract, "dart:lib/a.dart.go"));
    }

    /// <summary>
    /// Every kind of declaration is a node of its sub-kind and name: classes with their modifiers, mixins, enums and their
    /// values, extensions - one with no name named extension - extension types, typedefs of both forms, functions, getters,
    /// setters by their name and =, operators by operator and their symbol, fields and variables each, constants, and
    /// constructors - new, named, factory, const, external, redirecting.
    /// </summary>
    [Fact]
    public void EveryKindOfDeclaration_IsANodeOfItsSubKind_NamedAsDartCallsIt()
    {
        var extract = Read("lib/k.dart", """
            sealed class A {}
            abstract base class B {}
            interface class C {}
            final class D {}
            mixin class E {}
            base mixin M on A {}
            enum Color { red, green }
            extension on String { int get twice => length * 2; }
            extension type Id._(int value) {}
            typedef Pair = (int, int);
            typedef int Compare(int a, int b);
            const limit = 3;
            var count = 0, total = 1;

            class Box<T> {
              Box(this.value);
              Box.empty() : this(null);
              factory Box.of(T value) = Box<T>;
              external Box.native();
              static const zero = 0;
              late final T value;
              T get current => value;
              set current(T next) => value = next;
              bool operator ==(Object other) => false;
              void operator []=(int at, T to) {}
              Box<T> operator -() => this;
            }
            """);

        Assert.Equal(
            [
                ("A", "class", "sealed"), ("B", "class", "abstract base"), ("C", "class", "interface"), ("D", "class", "final"), ("E", "class", "mixin"),
                ("M", "mixin", "base"), ("Color", "enum", ""), ("red", "constant", ""), ("green", "constant", ""), ("extension", "extension", ""),
                ("twice", "getter", ""), ("Id", "extensionType", ""), ("value", "field", "final"), ("_", "constructor", ""), ("Pair", "typedef", ""),
                ("Compare", "typedef", ""), ("limit", "constant", "const"), ("count", "variable", ""), ("total", "variable", ""), ("Box", "class", ""),
                ("new", "constructor", ""), ("empty", "constructor", "redirecting"), ("of", "constructor", "factory redirecting"),
                ("native", "constructor", "external"), ("zero", "constant", "static const"), ("value", "field", "late final"), ("current", "getter", ""),
                ("current=", "setter", ""), ("operator==", "operator", ""), ("operator[]=", "operator", ""), ("operator-", "operator", ""),
            ],
            extract.Nodes.Select(node => (node.Name, node.SubKind!, string.Join(' ', node.Modifiers))));
        Assert.Equal("dart:lib/k.dart.Box.operator[]=", Assert.Single(extract.Nodes, node => node.Name == "operator[]=").Id);
        Assert.Equal("lib/k.dart", Assert.Single(extract.Nodes, node => node.Name == "extension").SeenIn);
        Assert.Equal("String", Assert.Single(extract.Nodes, node => node.Name == "twice").Extends);
        Assert.Equal("lib/k.dart", Assert.Single(extract.Nodes, node => node.Name == "twice").SeenIn);
        Assert.Equal(["A"], extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Inherits).Select(reference => reference.Name));
        Assert.True(Assert.Single(extract.Nodes, node => node.Name == "native").Prototype);
    }

    /// <summary>
    /// A callable takes from its required positional and required named parameters to all of them, an initializing one
    /// among them; a getter takes none at all, and is a value of the type it answers.
    /// </summary>
    [Fact]
    public void ACallablesArity_IsItsRequiredParametersToAllOfThem_AndAGetterIsAValue()
    {
        var extract = Read("lib/p.dart", """
            class P {
              P(this.a, [int b = 1, int? c]);
              P.named({required int a, String? b, int c = 2});
              int a = 0;
              void f(int x, {required int y, int z = 0}) {}
              void g([int? x]) {}
              List<int> get all => [];
            }
            """);

        Assert.Equal((1, 3), Arity(extract, "dart:lib/p.dart.P.new"));
        Assert.Equal((1, 3), Arity(extract, "dart:lib/p.dart.P.named"));
        Assert.Equal((2, 3), Arity(extract, "dart:lib/p.dart.P.f"));
        Assert.Equal((0, 1), Arity(extract, "dart:lib/p.dart.P.g"));
        Assert.Equal(((int?)null, (int?)null, "List"), (Node(extract, "dart:lib/p.dart.P.all").MinArity, Node(extract, "dart:lib/p.dart.P.all").MaxArity, Node(extract, "dart:lib/p.dart.P.all").ValueType));
    }

    /// <summary>
    /// A call's receiver is what the file says it is: this, super, an import's prefix, a local of a type written or made, a
    /// type's name with or without its arguments, a constructor's call, a chain of values, through ?. and !; a cascade's
    /// sections are called on its target, assignments among them.
    /// </summary>
    [Fact]
    public void AReceiver_IsWhatTheFileSaysItIs_ACascadesItsTarget()
    {
        var extract = Read("lib/r.dart", """
            import 'other.dart' as p;

            class R {
              void go(Shape given) {
                this.one();
                super.two();
                p.three();
                given.four();
                final made = Shape.unit();
                made?.five();
                List<int>.filled(1, 0);
                Shape().six();
                widget.controller!.seven();
                Paint()
                  ..color = red
                  ..eight()
                  ?..nine();
                given..ten()..eleven(1);
              }
            }
            """);

        Assert.Equal(
            [
                ("one", "this", null), ("two", "base", null), ("three", "prefix", "p"), ("four", "type", "Shape"), ("unit", "named", "Shape"),
                ("five", "type", "Shape"), ("filled", "named", "List"), ("Shape", null, null), ("six", "type", "Shape"), ("seven", "value", "widget.controller"),
                ("Paint", null, null), ("eight", "type", "Paint"), ("nine", "type", "Paint"), ("ten", "type", "Shape"), ("eleven", "type", "Shape"),
            ],
            extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Calls).Select(reference => (reference.Name, reference.Qualifier?.Kind, reference.Qualifier?.Name)));
    }

    /// <summary>A constructor named after a type's arguments makes that type: a local given what it makes, and a call on its result, are of it.</summary>
    [Fact]
    public void ANamedConstructorAfterATypesArguments_MakesThatType()
    {
        var extract = Read("lib/c.dart", """
            void go() {
              final cache = Cache<int>.create();
              cache.get();
              Cache<int>.create().put();
            }
            """);

        Assert.Equal(
            [("create", "named", "Cache"), ("get", "type", "Cache"), ("create", "named", "Cache"), ("put", "type", "Cache")],
            extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Calls).Select(reference => (reference.Name, reference.Qualifier?.Kind, reference.Qualifier?.Name)));
    }

    /// <summary>
    /// new and const instantiate the type they name - through a prefix, with a constructor's name after it, which is a call
    /// of that constructor on the type - and a const declaration or collection is no instantiation, nor a call of a named
    /// constructor alone, which a static method's call is written as.
    /// </summary>
    [Fact]
    public void NewAndConst_InstantiateTheirType_AndANamedConstructorIsCalledOnIt()
    {
        var extract = Read("lib/n.dart", """
            import 'shapes.dart' as p;

            void go() {
              final a = new Square(1);
              final b = const Square.unit();
              final c = const p.Square.unit();
              Square.unit();
              const d = <int>[1];
              const limit = 3;
              final e = const {1: 2};
            }
            """);

        Assert.Equal(
            [
                (CodeEdgeKinds.Instantiates, "Square", null, null), (CodeEdgeKinds.Instantiates, "Square", null, null), (CodeEdgeKinds.Calls, "unit", "named", "Square"),
                (CodeEdgeKinds.Instantiates, "Square", "prefix", "p"), (CodeEdgeKinds.Calls, "unit", "named", "p.Square"), (CodeEdgeKinds.Calls, "unit", "named", "Square"),
            ],
            extract.References.Select(reference => (reference.Kind, reference.Name, reference.Qualifier?.Kind, reference.Qualifier?.Name)));
    }

    /// <summary>
    /// A call through a function value - a parameter, a local, a field of a function type a typedef or Flutter's callbacks
    /// name, a value's call, Function.apply - and one on a value declared dynamic are marked dynamic, the first not linked.
    /// </summary>
    [Fact]
    public void ACallThroughAFunctionValue_OrOnADynamicOne_IsMarkedDynamic()
    {
        var extract = Read("lib/f.dart", """
            class W {
              W(this.onTap, this.build);
              final VoidCallback onTap;
              final Builder build;

              void go(void Function() done, int compare(int a, int b), dynamic loose) {
                done();
                compare(1, 2);
                onTap();
                this.build();
                final local = () {};
                local();
                done.call();
                Function.apply(done, []);
                loose.anything();
              }
            }

            typedef Builder = Widget Function();
            """);

        Assert.Equal(
            [
                "a call through the function value done", "a call through the function value compare", "a call through the function value onTap",
                "a call through the function value build", "a call through the function value local", "a call through a function value's call",
                "a call through Function.apply", "a call on the dynamic value loose",
            ],
            extract.Dynamic.Select(dynamic => dynamic.Why));
        Assert.Equal(["anything"], Calls(extract, "dart:lib/f.dart.W.go"));
    }

    /// <summary>
    /// A library's file and its parts are one open scope: a part named by a URI beside it, or under the lib/ directory its
    /// path is under for a package: one, puts its declarations in the library's; one named by the library's name in that
    /// name, which the library declares beside its own; a private name is kept to the library.
    /// </summary>
    [Fact]
    public void ALibrarysParts_PutTheirDeclarationsInItsScope_HoweverTheyNameIt()
    {
        var byUri = Read("lib/src/a_part.dart", "part of '../a.dart';\nvoid _hidden() {}\nvoid shown() {}\n");
        var byPackage = Read("packages/shapes/lib/src/b.g.dart", "part of 'package:shapes/src/b.dart';\nvoid shown() {}\n");
        var byName = Read("lib/c_part.dart", "part of shapes.c;\nvoid shown() {}\n");
        var library = Read("lib/c.dart", "library shapes.c;\npart 'c_part.dart';\nvoid go() {}\n");

        Assert.Equal(["dart:lib/a.dart._hidden", "dart:lib/a.dart.shown"], byUri.Nodes.Select(node => node.Id));
        Assert.Equal(["lib/a.dart", null], byUri.Nodes.Select(node => node.SeenIn));
        Assert.Equal(new ExtractImport(CodeImportForms.Relative, "../a.dart", 1) { Shares = true }, Assert.Single(byUri.Imports));
        Assert.Equal("dart:packages/shapes/lib/src/b.dart.shown", Assert.Single(byPackage.Nodes).Id);
        Assert.Equal(CodeImportForms.Address, Assert.Single(byPackage.Imports).Form);
        Assert.Equal("dart:shapes.c.shown", Assert.Single(byName.Nodes).Id);
        Assert.Empty(byName.Imports);
        Assert.Equal(
            [new ExtractScope("lib/c.dart", Open: true) { Parent = "shapes.c" }, new ExtractScope("shapes.c", Open: true) { Parent = "lib/c.dart" }],
            library.Scopes);
        Assert.Empty(byUri.Scopes);
    }

    /// <summary>
    /// Linked, a part sees its library's names and private ones and its imports' prefixes, the library sees its parts', a
    /// part named by the library's name sees the library's - and an importer of the library sees no private name.
    /// </summary>
    [Fact]
    public void Linked_APartAndItsLibrarySeeEachOther_AndAnImporterSeesNoPrivateName()
    {
        var linked = Link(
            ("lib/a.dart", "import 'src/tools.dart' as t;\npart 'src/a_part.dart';\nvoid go() { _helper(); }\nvoid _secret() {}\n"),
            ("lib/src/a_part.dart", "part of '../a.dart';\nvoid _helper() { _secret(); t.tool(); }\n"),
            ("lib/src/tools.dart", "void tool() {}\n"),
            ("lib/c.dart", "library shapes.c;\npart 'c_part.dart';\nvoid go() { fromPart(); }\nvoid _inC() {}\n"),
            ("lib/c_part.dart", "part of shapes.c;\nvoid fromPart() { _inC(); }\n"),
            ("bin/main.dart", "import '../lib/a.dart';\nvoid main() { _secret(); _helper(); go(); }\n"));

        Assert.Equal(
            new[]
            {
                ("dart:lib/a.dart.go", "dart:lib/a.dart._helper", CodeConfidences.Exact),
                ("dart:lib/a.dart._helper", "dart:lib/a.dart._secret", CodeConfidences.Exact),
                ("dart:lib/a.dart._helper", "dart:lib/src/tools.dart.tool", CodeConfidences.Exact),
                ("dart:lib/c.dart.go", "dart:shapes.c.fromPart", CodeConfidences.Exact),
                ("dart:shapes.c.fromPart", "dart:lib/c.dart._inC", CodeConfidences.Exact),
                ("dart:bin/main.dart.main", "dart:lib/a.dart.go", CodeConfidences.Unique),
            }.Order(),
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Calls).Select(edge => (edge.From, edge.To, edge.Confidence)).Order());
    }

    /// <summary>
    /// An import or an export carries its prefix, its combinators and whether it is deferred as Dart writes them; each of
    /// its configurations' URIs stands in for it, with them; a dart: library and a package are imports by an address.
    /// </summary>
    [Fact]
    public void AnImport_CarriesItsPrefix_ItsCombinators_AndItsConfigurations()
    {
        var extract = Read("lib/i.dart", """
            import 'dart:async';
            import 'package:http/http.dart' deferred as http show get, post hide put;
            import 'stub.dart' if (dart.library.io) 'io.dart' if (dart.library.js_interop) 'web.dart' as platform;
            export 'src/api.dart' hide Internal;
            """);

        Assert.Equal(
            [
                new ExtractImport(CodeImportForms.Address, "dart:async", 1),
                new ExtractImport(CodeImportForms.Address, "package:http/http.dart", 2) { Prefix = "http" },
                new ExtractImport(CodeImportForms.Relative, "stub.dart", 3) { Prefix = "platform" },
                new ExtractImport(CodeImportForms.Relative, "io.dart", 3) { Prefix = "platform", Fallback = true },
                new ExtractImport(CodeImportForms.Relative, "web.dart", 3) { Prefix = "platform", Fallback = true },
                new ExtractImport(CodeImportForms.Relative, "src/api.dart", 4) { Export = true },
            ],
            extract.Imports.Select(import => import with { Show = null, Hide = null }));
        Assert.Equal(["get", "post"], extract.Imports[1].Show!);
        Assert.Equal(["put"], extract.Imports[1].Hide!);
        Assert.Equal(["Internal"], extract.Imports[5].Hide!);
    }

    /// <summary>
    /// In a test file - under test/, or ending _test.dart - each test, testWidgets and group call is a test node named by
    /// its description, held by what it is written in, what it calls its own; elsewhere such a call is a call.
    /// </summary>
    [Fact]
    public void ATestFilesTestCalls_AreTestNodes_AndElsewhereCalls()
    {
        const string Text = """
            void main() {
              group('a $name', () {
                testWidgets('renders', (tester) async {
                  render();
                });
              });
              test("counts", () => count());
            }
            """;

        var tests = Read("app/widget_test.dart", Text);
        var plain = Read("lib/main.dart", Text);

        Assert.Equal(
            [("main", null, false), ("'a $name'", "testGroup", true), ("'renders'", "test", true), ("\"counts\"", "test", true)],
            tests.Nodes.Select(node => (node.Name, node.SubKind == "function" ? null : node.SubKind, node.Test)));
        Assert.Equal(["render"], Calls(tests, "dart:app/widget_test.dart.main.'a $name'.'renders'"));
        Assert.Equal(["count"], Calls(tests, "dart:app/widget_test.dart.main.\"counts\""));
        Assert.True(Read("test/helpers.dart", Text).Nodes.Count(node => node.Test) == 3);
        Assert.Equal(["group", "testWidgets", "render", "test", "count"], Calls(plain, "dart:lib/main.dart.main"));
    }

    /// <summary>A file a generator writes is answered generated: by the ending its generator names it with, or by what its first lines say.</summary>
    [Fact]
    public void GeneratedDart_IsAnsweredGenerated_ByItsNameOrItsFirstLines()
    {
        Assert.Equal("its name ends .freezed.dart, as a generator names what it writes", Read("lib/a.freezed.dart", "void x() {}").Generated);
        Assert.Equal("its name ends .pb.dart, as a generator names what it writes", Read("lib/a.pb.dart", "void x() {}").Generated);
        Assert.Equal("its first lines say DO NOT MODIFY BY HAND, as a generator writes", Read("lib/a.dart", "// GENERATED CODE - DO NOT MODIFY BY HAND\nvoid x() {}").Generated);
        Assert.Null(Read("lib/a.dart", "void x() {}").Generated);
    }

    /// <summary>
    /// A pattern names types, which it references, and binds locals: a case's object patterns, an arm of a switch
    /// expression, a declaration's destructuring; a constant pattern's constructor instantiates; what follows is code.
    /// </summary>
    [Fact]
    public void APatternsTypes_AreReferenced_AndWhatFollowsItIsCode()
    {
        var extract = Read("lib/m.dart", """
            String go(Object shape) {
              switch (shape) {
                case Circle(radius: var r) when r > 0:
                  return area(r);
                case const Square(1):
                  return 'unit';
              }
              final Point(:x, :y) = origin();
              if (shape case Line(start: Point p)) {
                p.move();
              }
              return switch (shape) {
                Circle(radius: var r) => round(r),
                Triangle() => 'three',
                _ => 'none',
              };
            }
            """);

        Assert.Equal(
            [
                (CodeEdgeKinds.References, "Circle"), (CodeEdgeKinds.Calls, "area"), (CodeEdgeKinds.Instantiates, "Square"), (CodeEdgeKinds.References, "Point"),
                (CodeEdgeKinds.Calls, "origin"), (CodeEdgeKinds.References, "Line"), (CodeEdgeKinds.Calls, "move"), (CodeEdgeKinds.References, "Circle"),
                (CodeEdgeKinds.Calls, "round"), (CodeEdgeKinds.References, "Triangle"),
            ],
            extract.References.Select(reference => (reference.Kind, reference.Name)));
        Assert.Equal(new ExtractQualifier(CodeQualifierKinds.Type, "Point"), Assert.Single(extract.References, reference => reference.Name == "move").Qualifier);
    }

    /// <summary>A local function is no node and no call of another's name: what it calls is its holder's, and a call of it is none.</summary>
    [Fact]
    public void ALocalFunction_IsNoCallOfAnotherName_ItsCallsItsHolders()
    {
        var extract = Read("lib/l.dart", """
            void go() {
              int twice(int x) => x * 2;
              helper() {
                inner();
              }
              twice(1);
              helper();
              outer();
            }
            """);

        Assert.Equal(["inner", "outer"], Calls(extract, "dart:lib/l.dart.go"));
        Assert.Single(extract.Nodes);
    }

    /// <summary>
    /// Linked: @override links to the member of that name its base, a mixin or an interface declares - a mixin's on type its
    /// base, which its super calls reach - and an extension member is found through the type a receiver is declared with.
    /// </summary>
    [Fact]
    public void Linked_AnOverrideFindsItsBasesMember_AndAnExtensionIsFoundThroughItsType()
    {
        var linked = Link(
            ("lib/a.dart", """
                import 'ext.dart';

                abstract class Animal { void speak(); }

                mixin Loud on Animal {
                  @override
                  void speak() { super.speak(); }
                }

                class Dog extends Animal with Loud {
                  @override
                  void speak() {}

                  void go(String name) { name.shout(); }
                }
                """),
            ("lib/ext.dart", "extension Shouting on String {\n  String shout() => toUpperCase();\n}\nextension _Quiet on String {\n  String shout() => this;\n}\n"));

        Assert.Equal(
            [
                ("dart:lib/a.dart.Loud.speak", "dart:lib/a.dart.Animal.speak", CodeEdgeKinds.Overrides),
                ("dart:lib/a.dart.Loud.speak", "dart:lib/a.dart.Animal.speak", CodeEdgeKinds.Calls),
                ("dart:lib/a.dart.Dog.speak", "dart:lib/a.dart.Loud.speak", CodeEdgeKinds.Overrides),
                ("dart:lib/a.dart.Dog.go", "dart:lib/ext.dart.Shouting.shout", CodeEdgeKinds.Calls),
            ],
            linked.Edges.Where(edge => edge.Kind is CodeEdgeKinds.Overrides or CodeEdgeKinds.Calls).Select(edge => (edge.From, edge.To, edge.Kind)));
    }

    /// <summary>
    /// A package's import by its address is found through the root its pubspec declares - its lib/ - from a file of
    /// another package, as the pubspec's dependency names the package.
    /// </summary>
    [Fact]
    public void APackagesImport_IsFoundThroughTheRootItsPubspecDeclares()
    {
        var linked = Link(
            ("packages/shapes/pubspec.yaml", "name: shapes\n"),
            ("packages/shapes/lib/shapes.dart", "export 'src/square.dart';\n"),
            ("packages/shapes/lib/src/square.dart", "class Square {}\n"),
            ("app/pubspec.yaml", "name: app\ndependencies:\n  shapes:\n    path: ../packages/shapes\n"),
            ("app/lib/main.dart", "import 'package:shapes/shapes.dart';\nvoid main() { Square(); }\n"));

        Assert.Contains(new CodeEdge("file:app/lib/main.dart", "file:packages/shapes/lib/shapes.dart", CodeEdgeKinds.Imports, 1, CodeConfidences.Exact), linked.Edges);
        Assert.Contains(new CodeEdge("dart:app/lib/main.dart.main", "dart:packages/shapes/lib/src/square.dart.Square", CodeEdgeKinds.Instantiates, 2, CodeConfidences.Unique), linked.Edges);
        Assert.Contains(new CodeEdge("pubspec:app/pubspec.yaml", "pubspec:packages/shapes/pubspec.yaml", CodeEdgeKinds.ProjectReference, 3, CodeConfidences.Unique), linked.Edges);
    }

    /// <summary>
    /// Declarations, interpolations and test groups nested past the reader's stack are read no deeper, never overflow it,
    /// and are marked dynamic where they stop being read.
    /// </summary>
    [Fact]
    public void NestingPastTheReadersStack_IsReadNoDeeper_AndMarked()
    {
        var depth = 400;
        var interpolated = Read("lib/a.dart", "void go() { print(" + string.Concat(Enumerable.Repeat("'${f(", depth)) + string.Concat(Enumerable.Repeat(")}'", depth)) + "); }");
        var grouped = Read("test/a_test.dart", "void main() { " + string.Concat(Enumerable.Repeat("group('g', () { ", depth)) + "test('t', () {});" + string.Concat(Enumerable.Repeat("}); ", depth)) + "}");
        var declared = Read("lib/b.dart", string.Concat(Enumerable.Repeat("class A { ", depth)) + new string('}', depth) + "\nvoid after() {}\n");

        Assert.InRange(interpolated.References.Count, 2, ScanDepth + 2);
        Assert.Equal("code nested more than 64 interpolations and test groups deep, which is not read", Assert.Single(interpolated.Dynamic).Why);
        Assert.InRange(grouped.Nodes.Count(node => node.Test), 1, ScanDepth + 1);
        Assert.Equal("code nested more than 64 interpolations and test groups deep, which is not read", Assert.Single(grouped.Dynamic).Why);
        Assert.InRange(declared.Nodes.Count(node => node.Name == "A"), 1, ScanDepth + 1);
        Assert.Equal("declarations nested more than 64 blocks deep, which are not read", Assert.Single(declared.Dynamic).Why);
        Assert.Single(declared.Nodes, node => node.Id == "dart:lib/b.dart.after");
    }

    /// <summary>
    /// Any name but a reserved word's names a function, a method or a local - of, show, get, on among them - and a
    /// member's call is a call whatever its name spells after its receiver's dot: when, new; an augmentation is passed
    /// over, its body with it.
    /// </summary>
    [Fact]
    public void AnyNameButAReservedWord_NamesAMember_AndAMembersCallIsACall()
    {
        var extract = Read("lib/t.dart", """
            class Theme2 {
              static Theme2 of(BuildContext context) => lookup(context);
              void show() { render(); }
              int get(int i) => i;
              void on(String event) {}
              void go(AsyncValue<int> value) {
                final show = Theme2();
                show.on('x');
                value.when(data: (d) => d, error: (e, s) => 0);
                Theme2.new();
              }
            }

            augment class Theme2 {
              void added() { hidden(); }
            }

            augment void topLevel() { hidden(); }
            """);

        Assert.Equal(["of", "show", "get", "on", "go"], extract.Nodes.Where(node => node.Kind == CodeNodeKinds.Member).Select(node => node.Name));
        Assert.Single(extract.Nodes, node => node.Name == "Theme2");
        Assert.Equal(["lookup"], Calls(extract, "dart:lib/t.dart.Theme2.of"));
        Assert.Equal(["render"], Calls(extract, "dart:lib/t.dart.Theme2.show"));
        Assert.Equal(
            [("Theme2", null, null), ("on", "type", "Theme2"), ("when", "type", "AsyncValue"), ("new", "named", "Theme2")],
            extract.References.Where(reference => reference is { From: "dart:lib/t.dart.Theme2.go", Kind: CodeEdgeKinds.Calls }).Select(reference => (reference.Name, reference.Qualifier?.Kind, reference.Qualifier?.Name)));
        Assert.DoesNotContain(extract.References, reference => reference.Name == "hidden");
    }

    /// <summary>
    /// A call's arity counts no comma that ends its arguments, nor the commas of a type's arguments - after as or is, or
    /// a literal's - and an expression's comparisons are no type's arguments; an enum value's arguments are counted alike.
    /// </summary>
    [Fact]
    public void ACallsArity_CountsNoCommaThatEndsIt_NorATypesArguments()
    {
        var text = """
            int add(int a, int b) => a + b;
            class Address { Address.fromJson(Map<String, dynamic> json); }
            enum Size { small(1, 2,), large(3, 4); const Size(this.w, this.h); final int w; final int h; }
            void main() {
              add(
                1,
                2,
              );
              Address.fromJson(json['a'] as Map<String, dynamic>);
              add(<String, int>{'a': 1}.length, x is Map<String, int> ? 1 : 0);
              add(a < b, c > d);
              add(const <String, int>{}, 1);
              add(x << 1, y >> 1);
              add(<int>[1, 2].first, 1);
              add(!<String, int>{}.isEmpty ? 1 : 0, a! < b);
            }
            """;
        var extract = Read("lib/a.dart", text);

        Assert.Equal(
            [("add", 2), ("fromJson", 1), ("add", 2), ("add", 2), ("add", 2), ("add", 2), ("add", 2), ("add", 2)],
            extract.References.Where(reference => reference is { From: "dart:lib/a.dart.main", Kind: CodeEdgeKinds.Calls }).Select(reference => (reference.Name, reference.Arity ?? -1)));
        Assert.Equal([2, 2], extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Calls && reference.From.StartsWith("dart:lib/a.dart.Size.", StringComparison.Ordinal)).Select(reference => reference.Arity ?? -1));
        Assert.Equal(7, Link(("lib/a.dart", text)).Edges.Count(edge => edge is { From: "dart:lib/a.dart.main", To: "dart:lib/a.dart.add", Kind: CodeEdgeKinds.Calls }));
    }

    /// <summary>
    /// Linked: a field marked @override overrides the getter of its name its base declares; an annotation with type
    /// arguments before a parameter is passed over, the parameter's type its own.
    /// </summary>
    [Fact]
    public void Linked_AFieldMarkedOverride_OverridesItsBasesGetter_AndAParametersAnnotationIsPassedOver()
    {
        var linked = Link(("lib/a.dart", """
            abstract class A { String get name; double area(); }
            class B extends A {
              B(this.name);
              @override
              final String name;
              @override
              double area() => 0;
              void use(@Foo<int>() A given) { given.area(); }
            }
            """));

        Assert.Contains(new CodeEdge("dart:lib/a.dart.B.name", "dart:lib/a.dart.A.name", CodeEdgeKinds.Overrides, 5, CodeConfidences.Exact), linked.Edges);
        Assert.Contains(linked.Edges, edge => edge is { From: "dart:lib/a.dart.B.use", To: "dart:lib/a.dart.A.area", Kind: CodeEdgeKinds.Calls, Confidence: CodeConfidences.Exact });
    }

    /// <summary>
    /// Linked: a configurable import stands for the first of its URIs the repository holds, and an export's filter lets
    /// through only the names it shows.
    /// </summary>
    [Fact]
    public void Linked_AConfigurableImportTakesTheFirstURIHeld_AndAnExportShowsWhatItsFilterLets()
    {
        var main = ("lib/main.dart", "import 'stub.dart' if (dart.library.io) 'io.dart' as platform;\nimport 'all.dart' as all;\nvoid main() { platform.make(); all.Shown(); all.Hidden(); }\n");
        var io = ("lib/io.dart", "void make() {}\n");
        var all = ("lib/all.dart", "export 'src/types.dart' show Shown;\n");
        var types = ("lib/src/types.dart", "class Shown {}\nclass Hidden {}\n");

        var noStub = Link(main, io, all, types);
        var withStub = Link(main, io, ("lib/stub.dart", "void make() {}\n"), all, types);

        Assert.Contains(new CodeEdge("dart:lib/main.dart.main", "dart:lib/io.dart.make", CodeEdgeKinds.Calls, 3, CodeConfidences.Exact), noStub.Edges);
        Assert.Contains(new CodeEdge("dart:lib/main.dart.main", "dart:lib/stub.dart.make", CodeEdgeKinds.Calls, 3, CodeConfidences.Exact), withStub.Edges);
        Assert.DoesNotContain(withStub.Edges, edge => edge is { From: "dart:lib/main.dart.main", To: "dart:lib/io.dart.make" });
        Assert.Contains(new CodeEdge("dart:lib/main.dart.main", "dart:lib/src/types.dart.Shown", CodeEdgeKinds.Instantiates, 3, CodeConfidences.Exact), noStub.Edges);
        Assert.DoesNotContain(noStub.Edges, edge => edge is { From: "dart:lib/main.dart.main", To: "dart:lib/src/types.dart.Hidden" });
    }

    private const int ScanDepth = 64;

    private static FileExtract Read(string path, string text) => new DartExtractor().Extract(new CodeFileInput(path, text));

    private static ExtractNode Node(FileExtract extract, string id) => Assert.Single(extract.Nodes, node => node.Id == id);

    private static (int, int) Lines(FileExtract extract, string id) => (Node(extract, id).Start, Node(extract, id).End);

    private static (int?, int?) Arity(FileExtract extract, string id) => (Node(extract, id).MinArity, Node(extract, id).MaxArity);

    private static IEnumerable<string> Calls(FileExtract extract, string from)
        => extract.References.Where(reference => reference.From == from && reference.Kind == CodeEdgeKinds.Calls).Select(reference => reference.Name);

    /// <summary>The files linked together, each read by the implementation that claims it - Dart's or the pubspec's.</summary>
    private static CodeGraphLinked Link(params (string Path, string Text)[] files)
        => CodeGraphLinker.Link([.. files.Select(file => file.Path.EndsWith("pubspec.yaml", StringComparison.Ordinal)
            ? new CodeGraphFile(file.Path, "pubspec", new PubspecExtractor().Extract(new CodeFileInput(file.Path, file.Text)))
            : new CodeGraphFile(file.Path, "dart", new DartExtractor().Extract(new CodeFileInput(file.Path, file.Text))))]);
}
