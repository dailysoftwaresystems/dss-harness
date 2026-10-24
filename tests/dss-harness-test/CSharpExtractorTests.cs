using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;

namespace DssHarness.Tests;

/// <summary>C#'s implementation, held to the contract's kit, and the rules of its own the kit does not name.</summary>
public sealed class CSharpExtractorTests : CodeGraphExtractorContractTests<CSharpExtractor>
{
    private const string Shape = """
        // A sample.
        using System;
        using System.Collections.Generic;
        using static Shapes.Geometry;
        using Measures = Shapes.Measure;

        namespace Shapes;

        /// <summary>A figure on a plane. It has an area.</summary>
        public interface IShape
        {
            /// <summary>How large it is.</summary>
            double Area();

            string Name { get; }
        }

        /// <summary>What every <see cref="IShape"/> shares.</summary>
        public abstract class Shape : IShape
        {
            private readonly List<Point> _points = new();

            protected Shape(string name) => Name = name;

            public string Name { get; }

            public abstract double Area();

            public virtual void Draw(Canvas canvas, int times = 1)
            {
                canvas.Plot(_points.Count);
                Log(Name);
            }

            protected static void Log(string text) => Console.WriteLine(text);
        }

        public sealed class Circle : Shape
        {
            private readonly double _radius;

            public Circle(double radius) : base("circle") => _radius = radius;

            public Circle() : this(1) { }

            public override double Area() => Square(_radius) * Math.PI;

            public override void Draw(Canvas canvas, int times = 1)
            {
                base.Draw(canvas, times);
                var copy = new Circle(_radius);
                copy.Draw(canvas);
                Measures.Of(this);
            }
        }
        """;

    private const string Geometry = """
        namespace Shapes
        {
            /// <summary>
            /// Sums every shape uses.
            /// More of them later.
            /// </summary>
            public static class Geometry
            {
                public static double Square(double value) => value * value;

                public static double Twice(this double value, params double[] more) => value * 2;
            }

            public static class Measure
            {
                public static double Of(IShape shape) => shape.Area();
            }

            public struct Point : IEquatable<Point>
            {
                public int X, Y;

                public bool Equals(Point other) => X == other.X;
            }

            public class Canvas
            {
                public void Plot(int count)
                {
                }

                public event EventHandler? Drawn;

                public int this[int at] => at;

                public static Canvas operator +(Canvas left, Canvas right) => left;

                public static implicit operator int(Canvas canvas) => 0;

                static Canvas()
                {
                }

                ~Canvas()
                {
                }
            }

            public delegate void Painted(Canvas canvas);

            public enum Color
            {
                Red = 1,
                Green,
            }
        }
        """;

    private const string Usings = """
        global using Shapes;
        global using Figure = Shapes.Shape;
        """;

    private const string Tests = """
        using Xunit;

        namespace Shapes.Tests;

        public class CircleTests
        {
            [Fact]
            public void AreaGrows()
            {
                var circle = new Circle(2);
                Assert.True(circle.Area() > 3);
            }

            [Theory]
            [InlineData(1)]
            public void Draws(int times)
            {
                Circle circle = new();
                circle.Draw(new Canvas(), times);
            }

            private static void Check(Figure figure)
            {
            }
        }
        """;

    private const string Program = """
        using System.Reflection;

        var shape = new Circle(3);
        Report(shape);
        dynamic loose = shape;
        loose.Spin();
        Func<int, int> twice = n => n * 2;
        twice(4);
        typeof(Circle).GetMethod("Area");
        Activator.CreateInstance(typeof(Circle));

        static void Report(IShape figure) => System.Console.WriteLine(figure.Area());

        namespace App
        {
            partial class Holder<T> where T : IShape
            {
                public T? Value { get; set; }

                partial void Changed();

                public Holder(T value)
                {
                    Value = value;
                    value.Area();
                }
            }

            partial class Holder<T>
            {
                partial void Changed() { }
            }

            static class Areas
            {
                public static double Square(double side) => side;
            }
        }
        """;

    private const string Extras = """
        using Kinds = Shapes;

        namespace Shapes;

        /// <summary>Ways to say what a shape is.</summary>
        public static class Describing
        {
            public static string Describe(this IShape shape, string? prefix = null) => prefix + shape.Name;

            public static T Echo<T>(this T value) where T : IShape => value;
        }

        public record Label(string Text, int Size = 1) : IComparable<Label>
        {
            public int CompareTo(Label? other) => Size.CompareTo(other?.Size ?? 0);
        }

        public sealed partial class Board(Canvas canvas) : IDisposable
        {
            public static Board Instance { get; } = new(new Canvas());

            private Circle Pin { get; } = new();

            public Canvas Surface => canvas;

            public event EventHandler? Changed { add { } remove { } }

            void IDisposable.Dispose() => Surface.Plot(0);

            public static Board operator checked -(Board board) => board;

            public string Show(Circle circle)
            {
                var text = circle.Describe();
                Pin.Draw(Surface);
                Board.Instance.Surface.Plot(1);
                Board.Instance.Count(2);
                Kinds.Measure.Of(circle);
                foreach (Label label in Labels()) { _ = label.Text; }
                try { Plot(); } catch (InvalidOperationException failure) when (failure.Data is { }) { }
                var found = from item in Labels() where item.Size > 0 select item.Text;
                return circle is Circle { Name: { } named } ? named : text;
            }

            private static IEnumerable<Label> Labels() => [];

            private void Plot() => Surface.Plot(2);
        }

        public static class Ext
        {
            extension(Board board)
            {
                public int Count(int scale) => board.Surface.GetHashCode() + scale;

                public bool Empty => true;
            }
        }
        """;

    /// <inheritdoc/>
    public override IReadOnlyList<CodeGraphFixture> Fixtures { get; } =
    [
        new("src/Shapes/Extras.cs", Extras)
        {
            Nodes =
            [
                new("csharp:Shapes.Describing.Describe(IShape,string?)", CodeNodeKinds.Member, (8, 8)),
                new("csharp:Shapes.Describing.Echo`1(T)", CodeNodeKinds.Member, (10, 10)),
                new("csharp:Shapes.Label", CodeNodeKinds.Type, (13, 16)),
                new("csharp:Shapes.Board", CodeNodeKinds.Type, (18, 48)),
                new("csharp:Shapes.Board.Changed", CodeNodeKinds.Member, (26, 26)),
                new("csharp:Shapes.Board.IDisposable.Dispose()", CodeNodeKinds.Member, (28, 28)),
                new("csharp:Shapes.Board.operator checked -(Board)", CodeNodeKinds.Member, (30, 30)),
                new("csharp:Shapes.Board.Show(Circle)", CodeNodeKinds.Member, (32, 43)),
                new("csharp:Shapes.Ext", CodeNodeKinds.Type, (50, 58)),
                new("csharp:Shapes.Ext.extension(Board).Count(int)", CodeNodeKinds.Member, (54, 54)),
                new("csharp:Shapes.Ext.extension(Board).Empty", CodeNodeKinds.Member, (56, 56)),
            ],
            Edges =
            [
                // An extension of a type, called on a value of a type built on it.
                new("csharp:Shapes.Board.Show(Circle)", "csharp:Shapes.Describing.Describe(IShape,string?)", CodeEdgeKinds.Calls, 34, CodeConfidences.Unique),
                new("csharp:Shapes.Board.Show(Circle)", "csharp:Shapes.Circle.Draw(Canvas,int)", CodeEdgeKinds.Calls, 35, CodeConfidences.Exact),

                // A chain of values from a type, each through the type the one before is declared with.
                new("csharp:Shapes.Board.Show(Circle)", "csharp:Shapes.Canvas.Plot(int)", CodeEdgeKinds.Calls, 36, CodeConfidences.Exact),
                new("csharp:Shapes.Board.Show(Circle)", "csharp:Shapes.Ext.extension(Board).Count(int)", CodeEdgeKinds.Calls, 37, CodeConfidences.Unique),

                // An alias at the head of a dotted name.
                new("csharp:Shapes.Board.Show(Circle)", "csharp:Shapes.Measure.Of(IShape)", CodeEdgeKinds.Calls, 38, CodeConfidences.Exact),
                new("csharp:Shapes.Board.Show(Circle)", "csharp:Shapes.Label", CodeEdgeKinds.References, 39, CodeConfidences.Exact),
                new("csharp:Shapes.Board.Show(Circle)", "csharp:Shapes.Board.Plot()", CodeEdgeKinds.Calls, 40, CodeConfidences.Exact),
                new("csharp:Shapes.Board.Pin", "csharp:Shapes.Circle", CodeEdgeKinds.Instantiates, 22, CodeConfidences.Exact),
                new("csharp:Shapes.Board.Instance", "csharp:Shapes.Board", CodeEdgeKinds.Instantiates, 20, CodeConfidences.Exact),
                new("csharp:Shapes.Board.IDisposable.Dispose()", "csharp:Shapes.Canvas.Plot(int)", CodeEdgeKinds.Calls, 28, CodeConfidences.Exact),
                new("csharp:Shapes.Ext.extension(Board).Count(int)", "csharp:Shapes.Board", CodeEdgeKinds.References, 52, CodeConfidences.Exact),
                new("file:src/Shapes/Extras.cs", "csharp:Shapes", CodeEdgeKinds.Imports, 1, CodeConfidences.Exact),
            ],
        },
        new("src/Shapes/Shape.cs", Shape)
        {
            Nodes =
            [
                new("csharp:Shapes", CodeNodeKinds.Namespace, (7, 55)),
                new("csharp:Shapes.IShape", CodeNodeKinds.Type, (10, 16)),
                new("csharp:Shapes.IShape.Area()", CodeNodeKinds.Member, (13, 13)),
                new("csharp:Shapes.Shape", CodeNodeKinds.Type, (19, 36)),
                new("csharp:Shapes.Shape._points", CodeNodeKinds.Member, (21, 21)),
                new("csharp:Shapes.Shape.Shape(string)", CodeNodeKinds.Member, (23, 23)),
                new("csharp:Shapes.Shape.Draw(Canvas,int)", CodeNodeKinds.Member, (29, 33)),
                new("csharp:Shapes.Circle", CodeNodeKinds.Type, (38, 55)),
                new("csharp:Shapes.Circle.Circle()", CodeNodeKinds.Member, (44, 44)),
                new("csharp:Shapes.Circle.Draw(Canvas,int)", CodeNodeKinds.Member, (48, 54)),
            ],
            Edges =
            [
                new("csharp:Shapes", "csharp:Shapes.Circle", CodeEdgeKinds.Contains, 38, CodeConfidences.Exact),
                new("csharp:Shapes.Shape", "csharp:Shapes.IShape", CodeEdgeKinds.Implements, 19, CodeConfidences.Exact),
                new("csharp:Shapes.Circle", "csharp:Shapes.Shape", CodeEdgeKinds.Inherits, 38, CodeConfidences.Exact),
                new("csharp:Shapes.Shape._points", "csharp:Shapes.Point", CodeEdgeKinds.References, 21, CodeConfidences.Exact),
                new("csharp:Shapes.Shape.Draw(Canvas,int)", "csharp:Shapes.Canvas.Plot(int)", CodeEdgeKinds.Calls, 31, CodeConfidences.Exact),
                new("csharp:Shapes.Shape.Draw(Canvas,int)", "csharp:Shapes.Shape.Log(string)", CodeEdgeKinds.Calls, 32, CodeConfidences.Exact),
                new("csharp:Shapes.Circle.Circle(double)", "csharp:Shapes.Shape.Shape(string)", CodeEdgeKinds.Calls, 42, CodeConfidences.Exact),
                new("csharp:Shapes.Circle.Circle()", "csharp:Shapes.Circle.Circle(double)", CodeEdgeKinds.Calls, 44, CodeConfidences.Exact),
                new("csharp:Shapes.Circle.Area()", "csharp:Shapes.Shape.Area()", CodeEdgeKinds.Overrides, 46, CodeConfidences.Exact),
                new("csharp:Shapes.Circle.Area()", "csharp:Shapes.Geometry.Square(double)", CodeEdgeKinds.Calls, 46, CodeConfidences.Unique),
                new("csharp:Shapes.Circle.Draw(Canvas,int)", "csharp:Shapes.Shape.Draw(Canvas,int)", CodeEdgeKinds.Calls, 50, CodeConfidences.Exact),
                new("csharp:Shapes.Circle.Draw(Canvas,int)", "csharp:Shapes.Circle", CodeEdgeKinds.Instantiates, 51, CodeConfidences.Exact),
                new("csharp:Shapes.Circle.Draw(Canvas,int)", "csharp:Shapes.Circle.Draw(Canvas,int)", CodeEdgeKinds.Calls, 52, CodeConfidences.Exact),
                new("csharp:Shapes.Circle.Draw(Canvas,int)", "csharp:Shapes.Measure.Of(IShape)", CodeEdgeKinds.Calls, 53, CodeConfidences.Exact),
                new("csharp:Shapes.IShape.Area()", "csharp:Shapes.Circle.Area()", CodeEdgeKinds.Dispatches, 46, CodeConfidences.Exact),
                new("csharp:Shapes.IShape.Name", "csharp:Shapes.Shape.Name", CodeEdgeKinds.Dispatches, 25, CodeConfidences.Exact),
                new("file:src/Shapes/Shape.cs", "csharp:Shapes.Geometry", CodeEdgeKinds.Imports, 4, CodeConfidences.Exact),
                new("file:src/Shapes/Shape.cs", "csharp:Shapes.Measure", CodeEdgeKinds.Imports, 5, CodeConfidences.Exact),
            ],
        },
        new("src/Shapes/Geometry.cs", Geometry)
        {
            Nodes =
            [
                new("csharp:Shapes", CodeNodeKinds.Namespace, (1, 56)),
                new("csharp:Shapes.Geometry.Twice(double,double[])", CodeNodeKinds.Member, (11, 11)),
                new("csharp:Shapes.Point.X", CodeNodeKinds.Member, (21, 21)),
                new("csharp:Shapes.Point.Y", CodeNodeKinds.Member, (21, 21)),
                new("csharp:Shapes.Canvas", CodeNodeKinds.Type, (26, 47)),
                new("csharp:Shapes.Canvas.Drawn", CodeNodeKinds.Member, (32, 32)),
                new("csharp:Shapes.Canvas.this[int]", CodeNodeKinds.Member, (34, 34)),
                new("csharp:Shapes.Canvas.operator +(Canvas,Canvas)", CodeNodeKinds.Member, (36, 36)),
                new("csharp:Shapes.Canvas.implicit operator int(Canvas)", CodeNodeKinds.Member, (38, 38)),
                new("csharp:Shapes.Canvas.static Canvas()", CodeNodeKinds.Member, (40, 42)),
                new("csharp:Shapes.Canvas.~Canvas()", CodeNodeKinds.Member, (44, 46)),
                new("csharp:Shapes.Painted", CodeNodeKinds.Type, (49, 49)),
                new("csharp:Shapes.Color", CodeNodeKinds.Type, (51, 55)),
                new("csharp:Shapes.Color.Green", CodeNodeKinds.Member, (54, 54)),
            ],
            Edges =
            [
                new("csharp:Shapes.Measure.Of(IShape)", "csharp:Shapes.IShape.Area()", CodeEdgeKinds.Calls, 16, CodeConfidences.Exact),
                new("csharp:Shapes.Painted", "csharp:Shapes.Canvas", CodeEdgeKinds.References, 49, CodeConfidences.Exact),
            ],
        },
        new("src/Shapes/Usings.cs", Usings)
        {
            Edges =
            [
                new("file:src/Shapes/Usings.cs", "csharp:Shapes", CodeEdgeKinds.Imports, 1, CodeConfidences.Exact),
                new("file:src/Shapes/Usings.cs", "csharp:Shapes.Shape", CodeEdgeKinds.Imports, 2, CodeConfidences.Exact),
            ],
        },
        new("tests/Shapes.Tests/CircleTests.cs", Tests)
        {
            Nodes =
            [
                new("csharp:Shapes.Tests", CodeNodeKinds.Namespace, (3, 25)),
                new("csharp:Shapes.Tests.CircleTests.AreaGrows()", CodeNodeKinds.Member, (7, 12)),
                new("csharp:Shapes.Tests.CircleTests.Draws(int)", CodeNodeKinds.Member, (14, 20)),
            ],
            Edges =
            [
                new("csharp:Shapes.Tests.CircleTests.AreaGrows()", "csharp:Shapes.Circle", CodeEdgeKinds.Instantiates, 10, CodeConfidences.Exact),
                new("csharp:Shapes.Tests.CircleTests.AreaGrows()", "csharp:Shapes.Circle.Area()", CodeEdgeKinds.Calls, 11, CodeConfidences.Exact),
                new("csharp:Shapes.Tests.CircleTests.AreaGrows()", "csharp:Shapes.Circle.Area()", CodeEdgeKinds.Tests, 11, CodeConfidences.Exact),
                new("csharp:Shapes.Tests.CircleTests.Draws(int)", "csharp:Shapes.Circle.Draw(Canvas,int)", CodeEdgeKinds.Tests, 19, CodeConfidences.Exact),
                new("csharp:Shapes.Tests.CircleTests.Draws(int)", "csharp:Shapes.Canvas", CodeEdgeKinds.Instantiates, 19, CodeConfidences.Exact),
                new("csharp:Shapes.Tests.CircleTests.Check(Figure)", "csharp:Shapes.Shape", CodeEdgeKinds.References, 22, CodeConfidences.Exact),
            ],
        },
        new("src/App/Program.cs", Program)
        {
            Nodes =
            [
                new("csharp:src/App/Program.cs:Program.<Main>$(string[])", CodeNodeKinds.Member, (3, 12)),
                new("csharp:App.Holder`1", CodeNodeKinds.Type, (16, 32)),
                new("csharp:App.Holder`1.Changed()", CodeNodeKinds.Member, (20, 20)),
                new("csharp:App.Holder`1.Changed()~2", CodeNodeKinds.Member, (31, 31)),
            ],
            Edges =
            [
                new("csharp:src/App/Program.cs:Program.<Main>$(string[])", "csharp:Shapes.Circle", CodeEdgeKinds.Instantiates, 3, CodeConfidences.Unique),
                new("csharp:src/App/Program.cs:Program.<Main>$(string[])", "csharp:Shapes.IShape.Area()", CodeEdgeKinds.Calls, 12, CodeConfidences.Exact),
                new("csharp:App.Holder`1", "csharp:Shapes.IShape", CodeEdgeKinds.References, 16, CodeConfidences.Unique),
                new("csharp:App.Holder`1", "csharp:App.Holder`1.Changed()~2", CodeEdgeKinds.Contains, 31, CodeConfidences.Exact),
                new("csharp:App.Holder`1.Changed()", "csharp:App.Holder`1.Changed()~2", CodeEdgeKinds.Declares, 20, CodeConfidences.Unique),
            ],
        },
    ];

    /// <summary>
    /// A declaration's signature - its header on one line, its attributes left out - its summary, the line its body opens
    /// on, its modifiers, the arguments a call of it takes and its marks are what an agent is served before its body.
    /// </summary>
    [Fact]
    public void ADeclaration_KnowsItsSignature_ItsSummary_ItsBody_ItsArity_AndItsMarks()
    {
        var nodes = Fixtures.SelectMany(fixture => Extractor.Extract(new CodeFileInput(fixture.Path, fixture.Text)).Nodes).GroupBy(node => node.Id).ToDictionary(group => group.Key, group => group.First());

        var shape = nodes["csharp:Shapes.IShape"];
        Assert.Equal(("interface", "A figure on a plane.", 11, "public interface IShape", "Shapes", "csharp:Shapes"), (shape.SubKind, shape.Summary, shape.BodyStart, shape.Signature, shape.Scope, shape.Parent));

        // A reference in the documentation reads as what it names; a summary over lines is one line, its first sentence.
        Assert.Equal("What every IShape shares.", nodes["csharp:Shapes.Shape"].Summary);
        Assert.Equal("Sums every shape uses.", nodes["csharp:Shapes.Geometry"].Summary);

        var draw = nodes["csharp:Shapes.Circle.Draw(Canvas,int)"];
        Assert.Equal(("method", "public override void Draw(Canvas canvas, int times = 1)", 49, 1, 2, "Draw", "Shapes.Circle.Draw"), (draw.SubKind, draw.Signature, draw.BodyStart, draw.MinArity, draw.MaxArity, draw.Name, draw.Qualified));
        Assert.Equal(["public", "override"], draw.Modifiers);

        // An extension method is called on its first argument too; a params list has no most.
        Assert.Equal((0, null), (nodes["csharp:Shapes.Geometry.Twice(double,double[])"].MinArity, nodes["csharp:Shapes.Geometry.Twice(double,double[])"].MaxArity));

        // A member with no body that is abstract, an interface's or a partial method's declaring half is a prototype; an
        // automatic property, whose body the compiler writes, is not.
        Assert.True(nodes["csharp:Shapes.IShape.Area()"].Prototype);
        Assert.True(nodes["csharp:Shapes.IShape.Name"].Prototype);
        Assert.True(nodes["csharp:Shapes.Shape.Area()"].Prototype);
        Assert.False(nodes["csharp:Shapes.Shape.Name"].Prototype);
        Assert.True(nodes["csharp:App.Holder`1.Changed()"].Prototype);
        Assert.False(nodes["csharp:App.Holder`1.Changed()~2"].Prototype);

        Assert.True(nodes["csharp:Shapes.Tests.CircleTests.AreaGrows()"].Test);
        Assert.True(nodes["csharp:Shapes.Tests.CircleTests.Draws(int)"].Test);
        Assert.False(nodes["csharp:Shapes.Tests.CircleTests.Check(Figure)"].Test);

        // Each variable of one declaration is a node over the declaration's lines.
        Assert.Equal(("field", "public int Y"), (nodes["csharp:Shapes.Point.Y"].SubKind, nodes["csharp:Shapes.Point.Y"].Signature));
        Assert.Equal(("constant", "Red = 1"), (nodes["csharp:Shapes.Color.Red"].SubKind, nodes["csharp:Shapes.Color.Red"].Signature));
        Assert.Equal(("constructor", "static Canvas"), (nodes["csharp:Shapes.Canvas.static Canvas()"].SubKind, nodes["csharp:Shapes.Canvas.static Canvas()"].Name));

        // A field, a property, an event and a constant say the type their value is declared with - none for a type
        // parameter's - and an extension the type it extends: a constrained type parameter's constraint.
        Assert.Equal(["Circle", "Canvas", "EventHandler", "Color"], new[] { "csharp:Shapes.Board.Pin", "csharp:Shapes.Board.Surface", "csharp:Shapes.Board.Changed", "csharp:Shapes.Color.Red" }.Select(id => nodes[id].ValueType));
        Assert.Null(nodes["csharp:App.Holder`1.Value"].ValueType);
        Assert.Equal(["IShape", "IShape", "Board", "Board", null], new[] { "csharp:Shapes.Describing.Describe(IShape,string?)", "csharp:Shapes.Describing.Echo`1(T)", "csharp:Shapes.Ext.extension(Board).Count(int)", "csharp:Shapes.Ext.extension(Board).Empty", "csharp:Shapes.Geometry.Square(double)" }.Select(id => nodes[id].Extends));
        Assert.Equal((1, 2), (nodes["csharp:Shapes.Ext.extension(Board).Count(int)"].MinArity, nodes["csharp:Shapes.Ext.extension(Board).Count(int)"].MaxArity));
        Assert.Equal(("delegate", null), (nodes["csharp:Shapes.Painted"].SubKind, nodes["csharp:Shapes.Painted"].BodyStart));

        // Statements at the file's top are the method the compiler makes of them.
        Assert.Equal(("<Main>$", "Program.<Main>$", "Program", null), (nodes["csharp:src/App/Program.cs:Program.<Main>$(string[])"].Name, nodes["csharp:src/App/Program.cs:Program.<Main>$(string[])"].Qualified, nodes["csharp:src/App/Program.cs:Program.<Main>$(string[])"].Scope, nodes["csharp:src/App/Program.cs:Program.<Main>$(string[])"].Parent));
    }

    /// <summary>
    /// A using is an import of a scope - a namespace, the type of a <c>using static</c>, an alias's target reached through
    /// its prefix - and a <c>global</c> one an import every C# file has; each namespace a declaration opens is a scope
    /// inside the one before it.
    /// </summary>
    [Fact]
    public void Usings_AreImportsOfScopes_AnAliasThroughItsPrefix_AndAGlobalOneEveryFiles()
    {
        var shape = Extractor.Extract(new CodeFileInput("src/Shapes/Shape.cs", Shape));
        var usings = Extractor.Extract(new CodeFileInput("src/Shapes/Usings.cs", Usings));
        var tests = Extractor.Extract(new CodeFileInput("tests/Shapes.Tests/CircleTests.cs", Tests));

        Assert.Equal(
            [("System", null, false), ("System.Collections.Generic", null, false), ("Shapes.Geometry", null, false), ("Shapes.Measure", "Measures", false)],
            shape.Imports.Select(import => (import.Target, import.Prefix, import.Global)));
        Assert.All(shape.Imports, import => Assert.Equal(CodeImportForms.Scope, import.Form));
        Assert.Equal([("Shapes", null, true), ("Shapes.Shape", "Figure", true)], usings.Imports.Select(import => (import.Target, import.Prefix, import.Global)));
        Assert.Empty(usings.Nodes);

        Assert.Equal([("Shapes", null), ("Shapes.Tests", "Shapes"), ("Shapes.Tests.CircleTests", "Shapes.Tests")], tests.Scopes.Select(scope => (scope.Name, scope.Parent)));
        Assert.All(tests.Scopes, scope => Assert.True(scope.Open));
    }

    /// <summary>
    /// A call is its name, its arguments and what its receiver is: <c>this</c>, <c>base</c>, the type a variable,
    /// parameter, field or property is declared with, a name written as a type's is; else a value of no said type - by
    /// its name where it is one, a field's chain of members by their names - and never an unqualified call, which is looked
    /// for in the scopes it is written in. A local function, <c>nameof</c> and a value of a type parameter's type are no
    /// call of a declared member.
    /// </summary>
    [Fact]
    public void ACall_IsQualifiedByWhatItsReceiverIs_WhereTheFileSays()
    {
        const string Text = """
            class Box<T>
            {
                private readonly Store _store = new();
                public Shelf Top { get; set; }

                void Run(Crate crate, T item)
                {
                    this.Mine();
                    base.Theirs();
                    crate.Open(1, 2);
                    _store.Save();
                    Top.Lift();
                    this._store.Load();
                    var made = new Truck();
                    made.Drive();
                    var found = Find();
                    found.Unknown();
                    other.Something();
                    Helpers.Static.Call();
                    string.Join(",", "a");
                    List<int>.Empty();
                    crate?.Close();
                    ((Shelf)found).Turn();
                    new Truck().Honk();
                    item.Shake();
                    Local();
                    nameof(Run);
                    Generic<int>();
                    var apply = (Crate c) => c.Seal();
                    this.Inherited.Go();
                    _store.Inner.Go();
                    crate.Lid.Open();
                    Cache.Instance.Get();
                    Find().Then();
                    void Local() { }
                }
            }
            """;

        var calls = Extractor.Extract(new CodeFileInput("Box.cs", Text)).References.Where(reference => reference.Kind == CodeEdgeKinds.Calls)
            .Select(reference => (reference.Name, reference.Arity, reference.Qualifier?.Kind, reference.Qualifier?.Name)).ToList();

        Assert.Equal(
            [
                ("Mine", 0, CodeQualifierKinds.This, null),
                ("Theirs", 0, CodeQualifierKinds.Base, null),
                ("Open", 2, CodeQualifierKinds.Type, "Crate"),
                ("Save", 0, CodeQualifierKinds.Type, "Store"),
                ("Lift", 0, CodeQualifierKinds.Type, "Shelf"),
                ("Load", 0, CodeQualifierKinds.Type, "Store"),
                ("Drive", 0, CodeQualifierKinds.Type, "Truck"),
                ("Find", 0, null, null),
                ("Unknown", 0, CodeQualifierKinds.Value, null),
                ("Something", 0, CodeQualifierKinds.Value, "other"),
                ("Call", 0, CodeQualifierKinds.Named, "Helpers.Static"),
                ("Join", 2, CodeQualifierKinds.Type, "string"),
                ("Empty", 0, CodeQualifierKinds.Type, "List`1"),
                ("Close", 0, CodeQualifierKinds.Type, "Crate"),
                ("Turn", 0, CodeQualifierKinds.Type, "Shelf"),
                ("Honk", 0, CodeQualifierKinds.Type, "Truck"),
                ("Shake", 0, CodeQualifierKinds.Value, null),
                ("Generic", 0, null, null),
                ("Seal", 0, CodeQualifierKinds.Type, "Crate"),
                ("Go", 0, CodeQualifierKinds.Value, "Inherited"),
                ("Go", 0, CodeQualifierKinds.Value, "_store.Inner"),
                ("Open", 0, CodeQualifierKinds.Value, null),
                ("Get", 0, CodeQualifierKinds.Named, "Cache.Instance"),
                ("Then", 0, CodeQualifierKinds.Value, null),
                ("Find", 0, null, null),
            ],
            calls);
    }

    /// <summary>
    /// A variable is of the type its declaration says - a parameter's, a local's or a new one's given to a <c>var</c>, a
    /// loop's, a catch's, a pattern's, an <c>out</c> one's, a query's, a primary constructor's parameter - a local hides a
    /// field of its name, and one name declared with two types says neither.
    /// </summary>
    [Fact]
    public void AVariable_IsOfTheTypeItsDeclarationSays_AndALocalHidesAField()
    {
        const string Text = """
            class Yard(Gate gate)
            {
                private Shed shed;

                void Walk(IEnumerable<Crate> crates, object thing)
                {
                    gate.Open();
                    shed.Paint();
                    foreach (Crate each in crates) { each.Lift(); }
                    try { } catch (Fault fault) { fault.Log(); }
                    if (thing is Barrow barrow) { barrow.Push(); }
                    Take(out Rake rake);
                    rake.Sweep();
                    var lifted = from Crate lift in crates select lift.Weigh();
                    if (thing is Hoe twice) { twice.Dig(); }
                    if (thing is Spade twice) { twice.Dig(); }
                    var shed = new Barn();
                    shed.Paint();
                }
            }
            """;

        var calls = Extractor.Extract(new CodeFileInput("Yard.cs", Text)).References.Where(reference => reference.Kind == CodeEdgeKinds.Calls && reference.Qualifier is not null)
            .Select(reference => (reference.Name, reference.Qualifier!.Kind, reference.Qualifier.Name)).ToList();

        Assert.Equal(
            [
                ("Open", CodeQualifierKinds.Type, "Gate"),
                ("Paint", CodeQualifierKinds.Type, "Barn"),
                ("Lift", CodeQualifierKinds.Type, "Crate"),
                ("Log", CodeQualifierKinds.Type, "Fault"),
                ("Push", CodeQualifierKinds.Type, "Barrow"),
                ("Sweep", CodeQualifierKinds.Type, "Rake"),
                ("Weigh", CodeQualifierKinds.Type, "Crate"),
                ("Dig", CodeQualifierKinds.Value, null),
                ("Dig", CodeQualifierKinds.Value, null),
                ("Paint", CodeQualifierKinds.Type, "Barn"),
            ],
            calls);
    }

    /// <summary>
    /// The types a declaration's signature, attributes and constraints name, and those a body names in a <c>new</c>, a
    /// <c>typeof</c>, a cast, an <c>as</c>, an <c>is</c>, a pattern or a local variable's declaration, are references, and
    /// a target-typed <c>new</c> makes the type it is given to - a generic one with its count of
    /// type arguments, an attribute with <c>Attribute</c> after its name; a type parameter, a built-in type, <c>var</c> and
    /// <c>dynamic</c> are none.
    /// </summary>
    [Fact]
    public void TheTypesADeclarationNames_AreReferences()
    {
        const string Text = """
            [Serializable, Tools.Marked]
            class Holder<T> : Base<Item>, IList<T> where T : IItem
            {
                [Obsolete] Map<Key, (Value, Other)>? Pick(T[] items, ref Ticket ticket, dynamic loose, var bad) => null;

                void Look(object thing)
                {
                    var a = typeof(Seen);
                    var b = (Cast)thing;
                    var c = thing as Soft;
                    var d = thing is Checked;
                    var e = thing is Patterned p;
                    var f = new Made<Inner>();
                    var g = new Nested.Deep();
                    Local local = default;
                    foreach (Each each in new Each[0]) { }
                    try { } catch (Caught caught) { }
                    Parse(out Outed outed);
                    Target target = new();
                }
            }
            """;

        var references = Extractor.Extract(new CodeFileInput("Holder.cs", Text)).References.Where(reference => reference.Kind != CodeEdgeKinds.Calls)
            .Select(reference => (reference.Kind, reference.Name, reference.Qualifier?.Name)).ToList();

        // A type a declaration names is a type qualifier's, never a value's.
        Assert.All(
            Extractor.Extract(new CodeFileInput("Holder.cs", Text)).References.Where(reference => reference.Kind != CodeEdgeKinds.Calls && reference.Qualifier is not null),
            reference => Assert.Equal(CodeQualifierKinds.Type, reference.Qualifier!.Kind));

        Assert.Equal(
            [
                (CodeEdgeKinds.References, "SerializableAttribute", null),
                (CodeEdgeKinds.References, "MarkedAttribute", "Tools"),
                (CodeEdgeKinds.Inherits, "Base`1", null),
                (CodeEdgeKinds.References, "Item", null),
                (CodeEdgeKinds.Implements, "IList`1", null),
                (CodeEdgeKinds.References, "IItem", null),
                (CodeEdgeKinds.References, "ObsoleteAttribute", null),
                (CodeEdgeKinds.References, "Ticket", null),
                (CodeEdgeKinds.References, "Map`2", null),
                (CodeEdgeKinds.References, "Key", null),
                (CodeEdgeKinds.References, "Value", null),
                (CodeEdgeKinds.References, "Other", null),
                (CodeEdgeKinds.References, "Seen", null),
                (CodeEdgeKinds.References, "Cast", null),
                (CodeEdgeKinds.References, "Soft", null),
                (CodeEdgeKinds.References, "Checked", null),
                (CodeEdgeKinds.References, "Patterned", null),
                (CodeEdgeKinds.Instantiates, "Made`1", null),
                (CodeEdgeKinds.References, "Inner", null),
                (CodeEdgeKinds.Instantiates, "Deep", "Nested"),
                (CodeEdgeKinds.References, "Local", null),
                (CodeEdgeKinds.References, "Each", null),
                (CodeEdgeKinds.References, "Caught", null),
                (CodeEdgeKinds.References, "Outed", null),
                (CodeEdgeKinds.References, "Target", null),
                (CodeEdgeKinds.Instantiates, "Target", null),
            ],
            references);
    }

    /// <summary>
    /// A base list is read by what declares it: an interface extends its bases, a struct implements each, and a class
    /// inherits its first unless it is named as an interface is, and implements the rest; an <c>override</c> names the
    /// member of a base it overrides.
    /// </summary>
    [Fact]
    public void ABaseList_IsInheritedOrImplemented_ByWhatDeclaresIt()
    {
        const string Text = """
            interface IOne : ITwo, Three { }
            struct Pair : IFirst, Second { }
            record struct Point : IThird;
            class Plain : IFace, Other { }
            class Derived : Parent, IFace
            {
                public override string ToString() => string.Empty;
                public override int Count { get; }
            }
            record Rec(int X) : Parent(X);
            """;

        var extract = Extractor.Extract(new CodeFileInput("Bases.cs", Text));

        Assert.Equal(
            [
                ("csharp:IOne", CodeEdgeKinds.Inherits, "ITwo"),
                ("csharp:IOne", CodeEdgeKinds.Inherits, "Three"),
                ("csharp:Pair", CodeEdgeKinds.Implements, "IFirst"),
                ("csharp:Pair", CodeEdgeKinds.Implements, "Second"),
                ("csharp:Point", CodeEdgeKinds.Implements, "IThird"),
                ("csharp:Plain", CodeEdgeKinds.Implements, "IFace"),
                ("csharp:Plain", CodeEdgeKinds.Implements, "Other"),
                ("csharp:Derived", CodeEdgeKinds.Inherits, "Parent"),
                ("csharp:Derived", CodeEdgeKinds.Implements, "IFace"),
                ("csharp:Derived.ToString()", CodeEdgeKinds.Overrides, "ToString"),
                ("csharp:Derived.Count", CodeEdgeKinds.Overrides, "Count"),
                ("csharp:Rec", CodeEdgeKinds.Inherits, "Parent"),
                ("csharp:Rec", CodeEdgeKinds.Calls, "Parent"),
            ],
            extract.References.Select(reference => (reference.From, reference.Kind, reference.Name)));

        Assert.Equal(("record", "record struct Point : IThird"), (extract.Nodes.Single(node => node.Id == "csharp:Point").SubKind, extract.Nodes.Single(node => node.Id == "csharp:Point").Signature));
        Assert.All(extract.References.Where(reference => reference.Kind is CodeEdgeKinds.Overrides), reference => Assert.Equal(CodeQualifierKinds.Base, reference.Qualifier?.Kind));
    }

    /// <summary>
    /// A member's id is its type's qualified name, its own - an explicit interface implementation's with the interface - and
    /// its parameter types with <c>ref</c>, <c>out</c> and <c>in</c>; a generic one's with its count of type parameters;
    /// two alike in one file are told apart by their order.
    /// </summary>
    [Fact]
    public void AMembersId_IsItsTypes_ItsName_AndItsParameterTypes()
    {
        const string Text = """
            namespace N.M
            {
                class Outer<K, V>
                {
                    class Inner { }
                    void Take<T>(ref T one, out int two, in Dictionary<string, (int a, int b)> three) { two = 0; }
                    void IDisposable.Dispose() { }
                    void Same() { }
                    void Same() { }
                    int IList<int>.this[int at] => at;
                }
            }
            """;

        Assert.Equal(
            [
                "csharp:N.M",
                "csharp:N.M.Outer`2",
                "csharp:N.M.Outer`2.Inner",
                "csharp:N.M.Outer`2.Take`1(ref T,out int,in Dictionary<string,(int,int)>)",
                "csharp:N.M.Outer`2.IDisposable.Dispose()",
                "csharp:N.M.Outer`2.Same()",
                "csharp:N.M.Outer`2.Same()~2",
                "csharp:N.M.Outer`2.IList`1.this[int]",
            ],
            Extractor.Extract(new CodeFileInput("Outer.cs", Text)).Nodes.Select(node => node.Id));
    }

    /// <summary>
    /// What a call cannot be bound to is marked dynamic with its line, and linked to nothing: a call on a <c>dynamic</c>
    /// value, a delegate's invocation, a member named by a string to reflection and <c>Activator.CreateInstance</c>.
    /// </summary>
    [Fact]
    public void WhatACallCannotBeBoundTo_IsMarkedDynamic()
    {
        var extract = Extractor.Extract(new CodeFileInput("src/App/Program.cs", Program));

        Assert.Equal(
            [
                (6, "a call on 'loose', which is dynamic"),
                (8, "a delegate invoked through 'twice'"),
                (9, "a member named by a string to reflection (GetMethod)"),
                (10, "a type made by Activator.CreateInstance"),
            ],
            extract.Dynamic.Select(mark => (mark.Line, mark.Why)));
        Assert.DoesNotContain(extract.References, reference => reference.Name is "Spin" or "twice" or "Report");

        const string Text = """
            class C
            {
                event Action Changed;
                dynamic _loose;
                void Run(Func<int> make, Handler handle, dynamic loose, object thing, Type type)
                {
                    make.Invoke();
                    handle(1);
                    Changed?.Invoke();
                    typeof(C).GetMethod(nameof(Run));
                    Activator.CreateInstance<C>();
                    loose();
                    loose.Inner.Go();
                    ((dynamic)thing).Go();
                    this._loose.Go();
                    System.Activator.CreateInstance(type);
                    type.GetProperty("Name");
                    type.InvokeMember("Run", default, null, null, null);
                }
            }
            delegate void Handler(int at);
            """;

        var marked = Extractor.Extract(new CodeFileInput("C.cs", Text));

        Assert.Equal(
            [
                (7, "a delegate invoked through 'make'"),
                (8, "a delegate invoked through 'handle'"),
                (9, "a delegate invoked through 'Changed'"),
                (12, "a call on 'loose', which is dynamic"),
                (13, "a call on 'loose', which is dynamic"),
                (14, "a call on '(dynamic)', which is dynamic"),
                (15, "a call on '_loose', which is dynamic"),
                (16, "a type made by Activator.CreateInstance"),
                (17, "a member named by a string to reflection (GetProperty)"),
                (18, "a member named by a string to reflection (InvokeMember)"),
            ],
            marked.Dynamic.Select(mark => (mark.Line, mark.Why)));
        Assert.DoesNotContain(marked.References, reference => reference.Name is "Go" or "Invoke" or "loose" or "handle");

        // A dynamic value says no type its calls are found through.
        Assert.Null(marked.Nodes.Single(node => node.Name == "_loose").ValueType);
    }

    /// <summary>A file is generated where its name ends as a generator's do, or its first comment says <c>&lt;auto-generated&gt;</c>.</summary>
    [Theory]
    [InlineData("src/Resources.Designer.cs", "class R { }", "its name ends with .Designer.cs, as a generator's files are")]
    [InlineData("src/Api.GENERATED.CS", "class R { }", "its name ends with .GENERATED.CS, as a generator's files are")]
    [InlineData("obj/TemporaryGeneratedFile_0a1b.cs", "class R { }", "its name begins with TemporaryGeneratedFile_, as a build's passing files are")]
    [InlineData("src/Old.cs", "// <autogenerated />\nclass R { }", "it begins with a comment marking it <auto-generated>")]
    [InlineData("src/Block.cs", "/* <auto-generated/> */ class R { }", "it begins with a comment marking it <auto-generated>")]
    [InlineData("obj/Api.g.cs", "class R { }", "its name ends with .g.cs, as a generator's files are")]
    [InlineData("obj/View.g.i.cs", "class R { }", "its name ends with .g.i.cs, as a generator's files are")]
    [InlineData("src/Made.cs", "// <auto-generated>\n//   By a tool.\n// </auto-generated>\nclass R { }", "it begins with a comment marking it <auto-generated>")]
    [InlineData("src/Hand.cs", "// written by hand\nclass R { } // <auto-generated>", null)]
    public void AFile_IsGenerated_ByItsNameOrItsMarker(string path, string text, string? why)
        => Assert.Equal(why, Extractor.Extract(new CodeFileInput(path, text)).Generated);

    /// <summary>
    /// Lines are the core's: a line separator, a paragraph separator, a next-line character and a form feed - which the
    /// parser's own lines break at too, but an editor and git do not - leave a node on the line the core counts.
    /// </summary>
    [Fact]
    public void Lines_AreCountedAsTheCoreCountsThem()
    {
        var extract = Extractor.Extract(new CodeFileInput("A.cs", "class A\r{\n    string s = @\"one\u2028two\u2029three\u0085four\f\";\n    void M() { }\r\n}\n"));

        Assert.Equal([("csharp:A", 1, 5), ("csharp:A.s", 3, 3), ("csharp:A.M()", 4, 4)], extract.Nodes.Select(node => (node.Id, node.Start, node.End)));
    }

    /// <summary>Code under a preprocessor branch the parse does not take is not seen: no symbol is defined.</summary>
    [Fact]
    public void CodeUnderABranchNotTaken_IsNotSeen()
    {
        const string Text = """
            class A
            {
            #if DEBUG
                void Hidden() { }
            #else
                void Shown() { }
            #endif
            }
            """;

        Assert.Equal(["csharp:A", "csharp:A.Shown()"], Extractor.Extract(new CodeFileInput("A.cs", Text)).Nodes.Select(node => node.Id));
    }

    /// <summary>
    /// An extension block's members are its static class's, each naming the block's receiver; an instance method's static
    /// form takes the receiver first, one argument more.
    /// </summary>
    [Fact]
    public void AnExtensionBlocksMembers_AreItsClasss_CalledOnItsReceiver()
    {
        const string Text = """
            static class Ext
            {
                extension(Item item)
                {
                    public int Weigh(int scale) => scale;
                    public static int Count() => 0;
                    public bool Heavy => true;
                }

                extension<T>(List<T> items)
                {
                    public T Weigh(int scale) { items.Clear(); return default; }
                }
            }
            """;

        var extract = Extractor.Extract(new CodeFileInput("Ext.cs", Text));

        // Each member's id holds its block's receiver, so two blocks' members of one name are two whatever their order.
        Assert.Equal(
            [
                ("csharp:Ext", null, null, null),
                ("csharp:Ext.extension(Item).Weigh(int)", 1, 2, "Item"),
                ("csharp:Ext.extension(Item).Count()", 0, 0, null),
                ("csharp:Ext.extension(Item).Heavy", null, null, "Item"),
                ("csharp:Ext.extension(List<T>).Weigh(int)", 1, 2, "List`1"),
            ],
            extract.Nodes.Select(node => (node.Id, node.MinArity, node.MaxArity, node.Extends)));
        Assert.Equal(
            ["csharp:Ext.extension(Item).Weigh(int)", "csharp:Ext.extension(Item).Count()", "csharp:Ext.extension(Item).Heavy"],
            extract.References.Where(reference => reference.Name == "Item").Select(reference => reference.From));

        // The block's type parameter is no type, and its receiver is read by its name as a field is.
        Assert.DoesNotContain(extract.References, reference => reference.Name == "T");
        Assert.Contains(extract.References, reference => reference is { Name: "Clear", Qualifier: { Kind: CodeQualifierKinds.Type, Name: "List`1" } });
    }

    /// <summary>
    /// A record's positional parameters are its properties, each over its own lines with the record's documentation of
    /// it, but for one its body declares itself - an interface's implemented explicitly is none; a file-local type's id,
    /// and those of what it declares, hold its file's path, and its scope is closed.
    /// </summary>
    [Fact]
    public void ARecordsParameters_AreItsProperties_AndAFileLocalType_IsItsFilesAlone()
    {
        const string Text = """
            namespace App;

            /// <summary>A pair.</summary>
            /// <param name="Left">The left one. Read first.</param>
            record Pair(int Left, string Right, Item Items) : IPair
            {
                public string Right { get; } = Right;
                IList IPair.Items => null;
            }

            file static class Helpers
            {
                public static void Run() { }

                class Inner { }
            }

            file delegate void Done();

            class Holder : IHold
            {
                Crate IHold.Box => null;
                Item Box { get; }
                void Go() => Box.Open();
            }
            """;

        var extract = Extractor.Extract(new CodeFileInput("src/Gen.cs", Text));
        var left = extract.Nodes.Single(node => node.Id == "csharp:App.Pair.Left");

        Assert.Equal(("property", 5, 5, "int Left", "The left one.", "int", "csharp:App.Pair", "App.Pair"), (left.SubKind, left.Start, left.End, left.Signature, left.Summary, left.ValueType, left.Parent, left.Scope));
        Assert.Equal(7, Assert.Single(extract.Nodes, node => node.Name == "Right").Start);

        // An interface's member implemented explicitly hides no positional property, nor the type of a member of its name.
        Assert.Equal(["csharp:App.Pair.Items", "csharp:App.Pair.IPair.Items"], extract.Nodes.Where(node => node.Name == "Items").Select(node => node.Id));
        Assert.Contains(extract.References, reference => reference is { Name: "Open", Qualifier: { Kind: CodeQualifierKinds.Type, Name: "Item" } });
        Assert.Equal(
            ["csharp:src/Gen.cs:App.Helpers", "csharp:src/Gen.cs:App.Helpers.Run()", "csharp:src/Gen.cs:App.Helpers.Inner", "csharp:src/Gen.cs:App.Done"],
            extract.Nodes.Where(node => node.Qualified.StartsWith("App.Helpers", StringComparison.Ordinal) || node.Name == "Done").Select(node => node.Id));
        Assert.Equal(
            [("App", true), ("App.Pair", true), ("App.Helpers", false), ("App.Helpers.Inner", false), ("App.Done", false), ("App.Holder", true)],
            extract.Scopes.Select(scope => (scope.Name, scope.Open)));
    }

    /// <summary>
    /// A receiver written with <c>!</c> or in parentheses is the value it wraps; an operator names the type it returns; a
    /// generic local function's call is its member's own.
    /// </summary>
    [Fact]
    public void ASuppressedOrParenthesizedReceiver_IsItsValue_AnOperatorNamesItsReturn_AndALocalGenericIsNoCall()
    {
        const string Text = """
            class Vec
            {
                Crate? crate;

                public static Pt operator +(Vec a, Vec b) => default;

                void Go()
                {
                    crate!.Open();
                    (crate!).Close();
                    Pick<int>(1);
                    void Pick<T>(T value) { }
                }
            }
            """;

        var extract = Extractor.Extract(new CodeFileInput("Vec.cs", Text));

        Assert.Contains(extract.References, reference => reference is { Name: "Open", Qualifier: { Kind: CodeQualifierKinds.Type, Name: "Crate" } });
        Assert.Contains(extract.References, reference => reference is { Name: "Close", Qualifier: { Kind: CodeQualifierKinds.Type, Name: "Crate" } });
        Assert.Contains(extract.References, reference => reference is { From: "csharp:Vec.operator +(Vec,Vec)", Kind: CodeEdgeKinds.References, Name: "Pt" });
        Assert.DoesNotContain(extract.References, reference => reference.Name == "Pick");
    }

    /// <summary>A test framework's attribute marks a test, written alone or qualified, with <c>Attribute</c> after it or not; another attribute does not.</summary>
    [Theory]
    [InlineData("[Fact]", true)]
    [InlineData("[Xunit.FactAttribute]", true)]
    [InlineData("[Theory, InlineData(1)]", true)]
    [InlineData("[NUnit.Framework.Test]", true)]
    [InlineData("[TestCase(1)]", true)]
    [InlineData("[TestCaseSource(nameof(Cases))]", true)]
    [InlineData("[TestMethod]", true)]
    [InlineData("[DataTestMethod]", true)]
    [InlineData("[TestMethodAttribute]", true)]
    [InlineData("[Benchmark]", false)]
    [InlineData("[Obsolete]", false)]
    public void ATestFrameworksAttribute_MarksATest(string attributes, bool test)
        => Assert.Equal(test, Extractor.Extract(new CodeFileInput("T.cs", $"class T {{ {attributes} void Run(int at) {{ }} }}")).Nodes.Single(node => node.Id == "csharp:T.Run(int)").Test);

    /// <summary>
    /// Two parts of one partial type, or two blocks of one namespace, in one file are one node over the lines from the
    /// first to the last, its summary the first said, holding what each part declares - nested types among it; a type of
    /// the name in another place is another.
    /// </summary>
    [Fact]
    public void PartsOfOneTypeOrNamespace_InOneFile_AreOneNode()
    {
        const string Text = """
            namespace A
            {
                partial class X
                {
                    void One() { }
                }
            }

            namespace B
            {
                partial class X { }
            }

            namespace A
            {
                /// <summary>Said here.</summary>
                partial class X
                {
                    void Two() { }

                    class Inner { }
                }
            }
            """;

        var extract = Extractor.Extract(new CodeFileInput("X.cs", Text));
        var nodes = extract.Nodes.ToDictionary(node => node.Id);

        Assert.Equal(["csharp:A", "csharp:A.X", "csharp:A.X.One()", "csharp:B", "csharp:B.X", "csharp:A.X.Two()", "csharp:A.X.Inner"], extract.Nodes.Select(node => node.Id));
        Assert.Equal((1, 23, 2), (nodes["csharp:A"].Start, nodes["csharp:A"].End, nodes["csharp:A"].BodyStart));
        Assert.Equal((3, 22, "Said here."), (nodes["csharp:A.X"].Start, nodes["csharp:A.X"].End, nodes["csharp:A.X"].Summary));
        Assert.Equal(["csharp:A.X", "csharp:A.X"], new[] { "csharp:A.X.Two()", "csharp:A.X.Inner" }.Select(id => nodes[id].Parent));
        Assert.Null(extract.WithDigests(new CodeFileInput("X.cs", Text).Lines).Problem("csharp", new CodeFileInput("X.cs", Text).Lines.Count));
    }

    /// <summary>The parser enters the tool with its English resources alone: no satellite of another language is beside the assemblies a build of it holds.</summary>
    [Fact]
    public void TheParser_EntersTheToolWithItsEnglishResourcesAlone()
    {
        var parser = Path.GetDirectoryName(typeof(Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree).Assembly.Location)!;

        Assert.Empty(Directory.EnumerateFiles(parser, "*.resources.dll", SearchOption.AllDirectories));
    }
}
