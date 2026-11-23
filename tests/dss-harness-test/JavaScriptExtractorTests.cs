using System.Globalization;
using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;

namespace DssHarness.Tests;

/// <summary>JavaScript's implementation over the structural scanner, held to the contract's kit, and the rules of its own the kit does not name.</summary>
public sealed class JavaScriptExtractorTests : CodeGraphExtractorContractTests<JavaScriptExtractor>
{
    internal const string Shapes = """
        import { Base, Printer } from './base.js';
        import * as geometry from './geometry';
        import Logger, { format as fmt } from './log';

        /** A shape with an area. */
        export class Shape extends Base {
          #cache = new Map();

          constructor(name) {
            super(name);
            this.name = name;
          }

          /** The area. */
          get area() {
            return 0;
          }

          describe() {
            const printer = new Printer();
            printer.write(`${this.name}: ${fmt(this.area)}`);
            geometry.round(this.area);
            Logger.info('described');
            this.#log();
          }

          #log() {}
        }

        export class Square extends Shape {
          constructor(side) {
            super('square');
            this.side = side;
          }

          get area() {
            return this.side * this.side;
          }

          scale = (by) => new Square(this.side * by);

          static unit() {
            return new Square(1);
          }
        }

        export function totalArea(shapes, visit) {
          let sum = 0;
          for (const shape of shapes) {
            visit(shape);
            sum += shape.area;
            shape.describe();
          }
          return sum + Square.unit().area;
        }

        export const double = (n) => n * 2;

        function helper() {
          return double(2);
        }

        export { helper as assist };
        export default Square;
        """;

    internal const string Base = """
        // What every shape is built on.
        export class Base {
          constructor(label) {
            this.label = label;
          }

          hello() {}
        }

        export class Printer {
          write(text) {
            console.log(text);
          }
        }
        """;

    internal const string Geometry = """
        export function round(value) {
          return Math.round(value)
        }

        const PI = 3.14
        export const TAU = PI * 2
        """;

    internal const string Log = """
        export default class Log {
          static info(text) {}
        }

        export function format(value) {
          return value.toFixed(2)
        }
        """;

    internal const string Legacy = """
        'use strict';

        const { join, resolve: absolute } = require('./paths');
        const util = require('./util');

        /**
         * Builds a thing.
         * @param {string} name the name
         */
        function build(name) {
          return util.make(join(name), absolute(name));
        }

        class Builder {
          run() {
            return build('x');
          }
        }

        module.exports = { build, Builder };
        module.exports.extra = function () {
          return build('extra');
        };
        exports.more = (a, b) => a + b;
        """;

    internal const string Util = """
        function make(a, b) {
          return [a, b];
        }

        module.exports = { make };
        """;

    internal const string Paths = """
        exports.join = (...parts) => parts.filter(Boolean);
        exports.resolve = function (part) {
          return '/' + part;
        };
        """;

    internal const string View = """
        import { Card } from './card.jsx';

        export default function View({ items }) {
          return (
            <section>
              {items.map((item) => (
                <Card key={item.id} title={`#${item.id}`} onPick={() => pick(item)}>
                  <Card.Body>{label(item)}</Card.Body>
                </Card>
              ))}
              <my-element data-x="1" />
            </section>
          );
        }

        function pick(item) {
          return item;
        }

        function label(item) {
          return String(item.id);
        }
        """;

    internal const string Card = """
        export function Card({ title, children }) {
          return <div className="card"><h2>{title}</h2>{children}</div>;
        }
        """;

    internal const string ShapesTest = """
        import Square, { totalArea } from '../src/shapes.mjs';

        describe('Square', () => {
          it('has an area', () => {
            const square = new Square(2);
            expect(square.area).toBe(4);
            square.describe();
          });

          it.each([1, 2])('scales by %i', (by) => {
            Square.unit().scale(by);
          });
        });

        test('totals', () => {
          totalArea([], () => {});
        });
        """;

    internal const string Script = """
        // A page's script: its names are every script's.
        var counter = 0;

        function increment() {
          counter++
          render()
        }

        function render() {
          document.getElementById('out').textContent = `${counter}`
        }

        document.addEventListener('click', increment)
        """;

    internal const string Minified = "function a(){b()}function b(){}";

    private const string Reads = """
        export const GREETING = 'hello world';

        const KIND = 'one';

        export class Reader {
            constructor() {
                this.own = 0;
            }

            take(table) {
                const local = 4;
                this.own += local + table.count;
                return GREETING + ' padding ' + `${KIND} part`;
            }
        }

        export const Settings = { retries: 3 };

        export function retry() {
            return Settings.retries;
        }
        """;

    /// <inheritdoc/>
    public override IReadOnlyList<CodeGraphFixture> Fixtures { get; } =
    [
        new("src/reads.js", Reads)
        {
            Nodes =
            [
                new("javascript:src/reads.js.GREETING", CodeNodeKinds.Member, (1, 1)),
                new("javascript:src/reads.js.KIND", CodeNodeKinds.Member, (3, 3)),
                new("javascript:src/reads.js.Reader", CodeNodeKinds.Type, (5, 15)),
                new("javascript:src/reads.js.Reader.constructor", CodeNodeKinds.Member, (6, 8)),
                new("javascript:src/reads.js.Reader.take", CodeNodeKinds.Member, (10, 14)),
                new("javascript:src/reads.js.Settings", CodeNodeKinds.Member, (17, 17)),
                new("javascript:src/reads.js.retry", CodeNodeKinds.Member, (19, 21)),
            ],
            Edges =
            [
                // A module's own constant, read bare, and one read inside a template's hole.
                new("javascript:src/reads.js.Reader.take", "javascript:src/reads.js.GREETING", CodeEdgeKinds.Uses, 13, CodeConfidences.Exact),
                new("javascript:src/reads.js.Reader.take", "javascript:src/reads.js.KIND", CodeEdgeKinds.Uses, 13, CodeConfidences.Exact),

                // A capitalized constant a member is read through is the value it is.
                new("javascript:src/reads.js.retry", "javascript:src/reads.js.Settings", CodeEdgeKinds.Uses, 20, CodeConfidences.Exact),
            ],
        },
        new("src/shapes.mjs", Shapes)
        {
            Nodes =
            [
                new("javascript:src/shapes.mjs.Shape", CodeNodeKinds.Type, (6, 28)),
                new("javascript:src/shapes.mjs.Shape.#cache", CodeNodeKinds.Member, (7, 7)),
                new("javascript:src/shapes.mjs.Shape.constructor", CodeNodeKinds.Member, (9, 12)),
                new("javascript:src/shapes.mjs.Shape.area", CodeNodeKinds.Member, (15, 17)),
                new("javascript:src/shapes.mjs.Shape.#log", CodeNodeKinds.Member, (27, 27)),
                new("javascript:src/shapes.mjs.Square", CodeNodeKinds.Type, (30, 45)),
                new("javascript:src/shapes.mjs.Square.scale", CodeNodeKinds.Member, (40, 40)),
                new("javascript:src/shapes.mjs.Square.unit", CodeNodeKinds.Member, (42, 44)),
                new("javascript:src/shapes.mjs.totalArea", CodeNodeKinds.Member, (47, 55)),
                new("javascript:src/shapes.mjs.double", CodeNodeKinds.Member, (57, 57)),
                new("javascript:src/shapes.mjs.helper", CodeNodeKinds.Member, (59, 61)),
            ],
            Edges =
            [
                new("file:src/shapes.mjs", "file:src/base.js", CodeEdgeKinds.Imports, 1, CodeConfidences.Exact),
                new("file:src/shapes.mjs", "file:src/geometry.js", CodeEdgeKinds.Imports, 2, CodeConfidences.Exact),
                new("file:src/shapes.mjs", "file:src/log.js", CodeEdgeKinds.Imports, 3, CodeConfidences.Exact),
                new("javascript:src/shapes.mjs.Shape", "javascript:src/base.js.Base", CodeEdgeKinds.Inherits, 6, CodeConfidences.Unique),
                new("javascript:src/shapes.mjs.Square", "javascript:src/shapes.mjs.Shape", CodeEdgeKinds.Inherits, 30, CodeConfidences.Exact),
                new("javascript:src/shapes.mjs.Shape.constructor", "javascript:src/base.js.Base.constructor", CodeEdgeKinds.Calls, 10, CodeConfidences.Exact),
                new("javascript:src/shapes.mjs.Shape.describe", "javascript:src/base.js.Printer", CodeEdgeKinds.Instantiates, 20, CodeConfidences.Unique),
                new("javascript:src/shapes.mjs.Shape.describe", "javascript:src/base.js.Printer.write", CodeEdgeKinds.Calls, 21, CodeConfidences.Exact),
                new("javascript:src/shapes.mjs.Shape.describe", "javascript:src/log.js.format", CodeEdgeKinds.Calls, 21, CodeConfidences.Exact),
                new("javascript:src/shapes.mjs.Shape.describe", "javascript:src/geometry.js.round", CodeEdgeKinds.Calls, 22, CodeConfidences.Exact),
                new("javascript:src/shapes.mjs.Shape.describe", "javascript:src/log.js.Log.info", CodeEdgeKinds.Calls, 23, CodeConfidences.Exact),
                new("javascript:src/shapes.mjs.Shape.describe", "javascript:src/shapes.mjs.Shape.#log", CodeEdgeKinds.Calls, 24, CodeConfidences.Exact),
                new("javascript:src/shapes.mjs.Square.constructor", "javascript:src/shapes.mjs.Shape.constructor", CodeEdgeKinds.Calls, 32, CodeConfidences.Exact),
                new("javascript:src/shapes.mjs.Square.scale", "javascript:src/shapes.mjs.Square", CodeEdgeKinds.Instantiates, 40, CodeConfidences.Exact),
                new("javascript:src/shapes.mjs.totalArea", "javascript:src/shapes.mjs.Shape.describe", CodeEdgeKinds.Calls, 52, CodeConfidences.Unique),
                new("javascript:src/shapes.mjs.totalArea", "javascript:src/shapes.mjs.Square.unit", CodeEdgeKinds.Calls, 54, CodeConfidences.Exact),
                new("javascript:src/shapes.mjs.helper", "javascript:src/shapes.mjs.double", CodeEdgeKinds.Calls, 60, CodeConfidences.Exact),
            ],
        },
        new("src/base.js", Base)
        {
            Nodes = [new("javascript:src/base.js.Base", CodeNodeKinds.Type, (2, 8)), new("javascript:src/base.js.Printer.write", CodeNodeKinds.Member, (11, 13))],
        },
        new("src/geometry.js", Geometry)
        {
            Nodes = [new("javascript:src/geometry.js.round", CodeNodeKinds.Member, (1, 3)), new("javascript:src/geometry.js.PI", CodeNodeKinds.Member, (5, 5))],
        },
        new("src/log.js", Log)
        {
            Nodes = [new("javascript:src/log.js.Log", CodeNodeKinds.Type, (1, 3)), new("javascript:src/log.js.format", CodeNodeKinds.Member, (5, 7))],
        },
        new("lib/legacy.cjs", Legacy)
        {
            Nodes =
            [
                new("javascript:lib/legacy.cjs.build", CodeNodeKinds.Member, (10, 12)),
                new("javascript:lib/legacy.cjs.Builder.run", CodeNodeKinds.Member, (15, 17)),
                new("javascript:lib/legacy.cjs.extra", CodeNodeKinds.Member, (21, 23)),
                new("javascript:lib/legacy.cjs.more", CodeNodeKinds.Member, (24, 24)),
            ],
            Edges =
            [
                new("file:lib/legacy.cjs", "file:lib/paths.js", CodeEdgeKinds.Imports, 3, CodeConfidences.Exact),
                new("file:lib/legacy.cjs", "file:lib/util.cjs", CodeEdgeKinds.Imports, 4, CodeConfidences.Exact),
                new("javascript:lib/legacy.cjs.build", "javascript:lib/util.cjs.make", CodeEdgeKinds.Calls, 11, CodeConfidences.Exact),
                new("javascript:lib/legacy.cjs.build", "javascript:lib/paths.js.join", CodeEdgeKinds.Calls, 11, CodeConfidences.Unique),
                new("javascript:lib/legacy.cjs.build", "javascript:lib/paths.js.resolve", CodeEdgeKinds.Calls, 11, CodeConfidences.Exact),
                new("javascript:lib/legacy.cjs.Builder.run", "javascript:lib/legacy.cjs.build", CodeEdgeKinds.Calls, 16, CodeConfidences.Exact),
                new("javascript:lib/legacy.cjs.extra", "javascript:lib/legacy.cjs.build", CodeEdgeKinds.Calls, 22, CodeConfidences.Exact),
            ],
        },
        new("lib/util.cjs", Util)
        {
            Nodes = [new("javascript:lib/util.cjs.make", CodeNodeKinds.Member, (1, 3))],
        },
        new("lib/paths.js", Paths)
        {
            Nodes = [new("javascript:lib/paths.js.join", CodeNodeKinds.Member, (1, 1)), new("javascript:lib/paths.js.resolve", CodeNodeKinds.Member, (2, 4))],
        },
        new("src/view.jsx", View)
        {
            Nodes = [new("javascript:src/view.jsx.View", CodeNodeKinds.Member, (3, 14)), new("javascript:src/view.jsx.pick", CodeNodeKinds.Member, (16, 18))],
            Edges =
            [
                new("file:src/view.jsx", "file:src/card.jsx", CodeEdgeKinds.Imports, 1, CodeConfidences.Exact),
                new("javascript:src/view.jsx.View", "javascript:src/card.jsx.Card", CodeEdgeKinds.Calls, 7, CodeConfidences.Unique),
                new("javascript:src/view.jsx.View", "javascript:src/view.jsx.pick", CodeEdgeKinds.Calls, 7, CodeConfidences.Exact),
                new("javascript:src/view.jsx.View", "javascript:src/view.jsx.label", CodeEdgeKinds.Calls, 8, CodeConfidences.Exact),
            ],
        },
        new("src/card.jsx", Card)
        {
            Nodes = [new("javascript:src/card.jsx.Card", CodeNodeKinds.Member, (1, 3))],
        },
        new("test/shapes.test.js", ShapesTest)
        {
            Nodes =
            [
                new("javascript:test/shapes.test.js.'Square'", CodeNodeKinds.Member, (3, 13)),
                new("javascript:test/shapes.test.js.'Square'.'has an area'", CodeNodeKinds.Member, (4, 8)),
                new("javascript:test/shapes.test.js.'Square'.'scales by %i'", CodeNodeKinds.Member, (10, 12)),
                new("javascript:test/shapes.test.js.'totals'", CodeNodeKinds.Member, (15, 17)),
            ],
            Edges =
            [
                new("file:test/shapes.test.js", "file:src/shapes.mjs", CodeEdgeKinds.Imports, 1, CodeConfidences.Exact),
                new("javascript:test/shapes.test.js.'Square'.'has an area'", "javascript:src/shapes.mjs.Square", CodeEdgeKinds.Instantiates, 5, CodeConfidences.Exact),
                new("javascript:test/shapes.test.js.'Square'.'has an area'", "javascript:src/shapes.mjs.Shape.describe", CodeEdgeKinds.Tests, 7, CodeConfidences.Exact),
                new("javascript:test/shapes.test.js.'Square'.'scales by %i'", "javascript:src/shapes.mjs.Square.unit", CodeEdgeKinds.Tests, 11, CodeConfidences.Exact),
                new("javascript:test/shapes.test.js.'totals'", "javascript:src/shapes.mjs.totalArea", CodeEdgeKinds.Tests, 16, CodeConfidences.Unique),
            ],
        },
        new("public/script.js", Script)
        {
            Nodes =
            [
                new("javascript:counter", CodeNodeKinds.Member, (2, 2)),
                new("javascript:increment", CodeNodeKinds.Member, (4, 7)),
                new("javascript:public/script.js.(module)", CodeNodeKinds.Member, (1, 13)),
            ],
            Edges = [new("javascript:increment", "javascript:render", CodeEdgeKinds.Calls, 6, CodeConfidences.Unique)],
        },
        new("public/app.min.js", Minified)
        {
            Nodes = [new("javascript:a", CodeNodeKinds.Member, (1, 1))],
        },
    ];

    /// <summary>
    /// Comments, strings, regular expressions and markup's text are never read as code - a slash after an operand
    /// divides - and a template's code is, its own templates and strings with it; a name may hold a dollar.
    /// </summary>
    [Fact]
    public void CommentsStringsAndRegularExpressions_AreNeverReadAsCode_ButATemplatesCodeIs()
    {
        var extract = Read("src/a.mjs", """
            export function go() {
              /* first(1); */
              // second(2);
              const quoted = 'third(3)' + "fourth(4)";
              const re = /fifth\(5\)/g;
              const ratio = total / sixth(6) / 2;
              const it = `it's ${seventh(eighth(`${ninth({ a: '}' })}`))} done`;
              $tenth(10);
            }
            """);

        Assert.Equal(["sixth", "seventh", "eighth", "ninth", "$tenth"], Calls(extract, "javascript:src/a.mjs.go"));
        Assert.Equal((1, 9), Lines(extract, "javascript:src/a.mjs.go"));
    }

    /// <summary>
    /// Every kind of declaration is a node of its sub-kind and name: classes, constructors, methods, getters, setters by
    /// their name and =, static members, fields - one an arrow or a function is given a method, and one a member assigns
    /// through this, made once the file is read - private ones by their #, functions, generators, async ones, a const, let or
    /// var each - a function, an arrow or a class bound to one read as one, the class by its binding's name.
    /// </summary>
    [Fact]
    public void EveryKindOfDeclaration_IsANodeOfItsSubKind_NamedAsJavaScriptCallsIt()
    {
        var extract = Read("src/k.mjs", """
            export class Box extends Base {
              static zero = 0;
              #items = [];
              handle = (event) => this.open(event);
              constructor(value) { this.value = value; }
              get size() { return 1; }
              set size(next) {}
              static of(value) { return new Box(value); }
              async *each() {}
              #hidden() {}
            }
            function plain(a, b) {}
            function* counter() {}
            async function load() {}
            const arrow = async (x) => x, single = y => y, made = function named() {};
            let count = 0;
            var Shape = class Inner {};
            """);

        Assert.Equal(
            [
                ("Box", "class"), ("zero", "field"), ("#items", "field"), ("handle", "method"), ("constructor", "constructor"), ("size", "getter"),
                ("size=", "setter"), ("of", "method"), ("each", "method"), ("#hidden", "method"), ("plain", "function"), ("counter", "function"),
                ("load", "function"), ("arrow", "function"), ("single", "function"), ("made", "function"), ("count", "variable"), ("Shape", "class"),
                ("value", "field"),
            ],
            extract.Nodes.Select(node => (node.Name, node.SubKind!)));
        Assert.Equal(["static"], Node(extract, "javascript:src/k.mjs.Box.of").Modifiers);
        Assert.Equal(["async", "*"], Node(extract, "javascript:src/k.mjs.Box.each").Modifiers);
        Assert.Equal("src/k.mjs.Box", Node(extract, "javascript:src/k.mjs.Box.#hidden").SeenIn?.Whole());
        Assert.Equal(["Base"], extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Inherits).Select(reference => reference.Name));
    }

    /// <summary>A JavaScript callable takes any number of arguments, as JavaScript calls it; a getter takes none at all, and is a value.</summary>
    [Fact]
    public void AJavaScriptCallable_TakesAnyNumberOfArguments_AndAGetterIsAValue()
    {
        var extract = Read("src/p.js", """
            class P {
              constructor(a, b = 1, ...rest) {}
              get all() { return []; }
            }
            function f(x, y) {}
            """);

        Assert.Equal((0, (int?)null), Arity(extract, "javascript:P.constructor"));
        Assert.Equal((0, (int?)null), Arity(extract, "javascript:f"));
        Assert.Equal(((int?)null, (int?)null), Arity(extract, "javascript:P.all"));
    }

    /// <summary>
    /// Each binding an import makes is an import of the file it names: those by their own names one import showing them; a
    /// default, a renamed and a namespace one each by a prefix, the name it binds, showing the export it names - and a
    /// reference written by such a name written as that export, by the prefix; a side effect's import shows nothing.
    /// </summary>
    [Fact]
    public void EachBinding_IsAnImportOfTheFileItNames_ADefaultRenamedOrNamespaceOneByItsPrefix()
    {
        var extract = Read("src/i.mjs", """
            import Main, { one, two as second, default as fallback } from './lib.js';
            import * as all from './all.js';
            import './side.js';

            export function go() {
              one();
              second();
              Main();
              new Main();
              all.three();
              Main.make();
              fallback();
            }
            """);

        Assert.Equal(
            [
                ("./lib.js", "Main", "default"), ("./lib.js", "second", "two"), ("./lib.js", "fallback", "default"), ("./lib.js", null, "one"),
                ("./all.js", "all", null), ("./side.js", null, ""),
            ],
            extract.Imports.Select(import => (import.Target, import.Prefix, import.Show is null ? null : string.Join(',', import.Show))));
        Assert.Equal(
            [
                ("one", null, null), ("two", "prefix", "second"), ("default", "prefix", "Main"), ("default", "prefix", "Main"), ("three", "prefix", "all"),
                ("make", "named", "Main.default"), ("default", "prefix", "fallback"),
            ],
            extract.References.Where(reference => reference.Kind is CodeEdgeKinds.Calls or CodeEdgeKinds.Instantiates).Select(reference => (reference.Name, reference.Qualifier?.Kind, extract.Written(reference.Qualifier))));
    }

    /// <summary>
    /// A default import of a single-file component binds the component it exports by default, as one of a script does -
    /// a member through it is that export's - one of a CSS module or of data, which export nothing by a name, binds its
    /// prefix alone, a member through it what the file declares and exports, and one of any other style sheet binds
    /// nothing: its prefix lets no name through.
    /// </summary>
    [Fact]
    public void ADefaultImportOfAComponent_BindsItsDefaultExport_AndOneOfAStyleSheetNone()
    {
        var extract = Read("src/main.js", """
            import Cart from './Cart.vue';
            import styles from './site.css';
            import classes from './Cart.module.scss';
            import config from './config.yaml';

            export function go() {
              Cart.open();
              styles.paint();
              classes.wide();
              config.load();
            }
            """);

        Assert.Equal(
            [("./Cart.vue", "Cart", "default"), ("./site.css", "styles", ""), ("./Cart.module.scss", "classes", null), ("./config.yaml", "config", null)],
            extract.Imports.Select(import => (import.Target, import.Prefix, import.Show is null ? null : string.Join(',', import.Show))));
        Assert.Equal(
            [("open", "named", "Cart.default"), ("paint", "prefix", "styles"), ("wide", "prefix", "classes"), ("load", "prefix", "config")],
            extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Calls).Select(reference => (reference.Name, reference.Qualifier?.Kind, extract.Written(reference.Qualifier))));
    }

    /// <summary>
    /// A specifier with no extension is tried as Node tries it - as written, with each of JavaScript's extensions, then
    /// the directory's index with each - each standing in for the one before, the first the tree holds taken; one with an
    /// extension, or naming no script, as written; an absolute one or a URL is no import of the tree.
    /// </summary>
    [Fact]
    public void ASpecifier_IsTriedAsNodeTriesIt()
    {
        var extract = Read("src/s.js", """
            import { a } from './a';
            import { b } from './b.js';
            import styles from './s.css';
            import { c } from 'pkg/sub';
            import { d } from 'https://example.com/d.js';
            import { e } from '/abs/e.js';
            """);

        Assert.Equal(
            [
                "./a", "./a.js", "./a.jsx", "./a.mjs", "./a.cjs", "./a.ts", "./a.tsx", "./a.json", "./a/index.js", "./a/index.jsx", "./a/index.mjs",
                "./a/index.cjs", "./a/index.ts", "./a/index.tsx", "./a/index.json",
            ],
            extract.Imports.Where(import => import.Show?.Contains("a") == true).Select(import => import.Target));
        Assert.Equal([false, .. Enumerable.Repeat(true, 14)], extract.Imports.Where(import => import.Show?.Contains("a") == true).Select(import => import.Fallback));
        Assert.Equal(["./b.js"], extract.Imports.Where(import => import.Show?.Contains("b") == true).Select(import => import.Target));
        Assert.Equal(["./s.css"], extract.Imports.Where(import => import.Prefix == "styles").Select(import => import.Target));
        Assert.All(extract.Imports.Where(import => import.Show?.Contains("c") == true), import => Assert.Equal(CodeImportForms.Address, import.Form));
        Assert.DoesNotContain(extract.Imports, import => import.Show?.Contains("d") == true || import.Show?.Contains("e") == true);

        var linked = Link(("src/s.js", "import { a } from './a';\nexport function go() { a(); }"), ("src/a/index.js", "export function a() {}"));

        Assert.Contains(new CodeEdge("file:src/s.js", "file:src/a/index.js", CodeEdgeKinds.Imports, 1, CodeConfidences.Exact), linked.Edges);
        Assert.Contains(new CodeEdge("javascript:src/s.js.go", "javascript:src/a/index.js.a", CodeEdgeKinds.Calls, 2, CodeConfidences.Unique), linked.Edges);
    }

    /// <summary>
    /// CommonJS: a require bound to a name is a namespace's import, one bound to a pattern each name it takes - by itself or
    /// renamed - and one alone an import of nothing; module.exports and exports export what they are given - a name, an
    /// object's names and functions, a function, a class - and module.exports = require(...) re-exports a file.
    /// </summary>
    [Fact]
    public void CommonJs_RequiresAreImports_AndModuleExportsExports()
    {
        var extract = Read("lib/c.js", """
            const all = require('./all');
            const { one, two: second } = require('./parts');
            require('./side');

            function hidden() {}
            function shown() { all.go(); one(); second(); }
            class Kept {}

            module.exports = { shown, alias: Kept, inline() {} };
            exports.later = function () {};
            """);

        Assert.Equal(
            [("./all", "all", null, false), ("./parts", "second", "two", false), ("./parts", null, "one", false), ("./side", null, "", false)],
            extract.Imports.Where(import => !import.Fallback).Select(import => (import.Target, import.Prefix, import.Show is null ? null : string.Join(',', import.Show), import.Export)));
        Assert.Equal("lib/c.js", Node(extract, "javascript:lib/c.js.hidden").SeenIn?.Whole());
        Assert.Null(Node(extract, "javascript:lib/c.js.shown").SeenIn);
        Assert.Equal(["alias"], Node(extract, "javascript:lib/c.js.Kept").Exports);
        Assert.Null(Node(extract, "javascript:lib/c.js.inline").SeenIn);
        Assert.Null(Node(extract, "javascript:lib/c.js.later").SeenIn);
        Assert.Equal([("go", "all"), ("one", null), ("two", "second")], extract.References.Select(reference => (reference.Name, extract.Written(reference.Qualifier))));

        var again = Read("lib/r.js", "module.exports = require('./impl');");
        Assert.True(Assert.Single(again.Imports, import => !import.Fallback).Export);
    }

    /// <summary>
    /// CommonJS's other forms: each declarator a require binds, a require's member bound by its name, an object
    /// module.exports names - a require's file imported, a spread of one re-exported but its default, any other value's
    /// calls the module's - an export a block holds, as a module that exports only where it can, and a class exported by a
    /// name not its own exported by that name, its own the module's, a function expression named by its export.
    /// </summary>
    [Fact]
    public void CommonJs_EveryRequireADeclaratorBinds_AndWhatAnExportedObjectHolds()
    {
        var extract = Read("lib/u.js", """
            var a = require('./a'), b = require('./b');
            const run = require('./run').default;
            function helper() {}
            if (typeof module !== 'undefined') {
              module.exports = { helper, nested: require('./nested'), ...require('./spread'), made: make() };
            }
            run(1);
            exports.make = class Maker {};
            exports.build = function builder() {};
            """);

        Assert.Equal(
            [
                ("./a", "a", null, false, null), ("./b", "b", null, false, null), ("./run", "run", "default", false, null), ("./nested", null, "", false, null),
                ("./spread", null, null, true, "default"),
            ],
            extract.Imports.Where(import => !import.Fallback)
                .Select(import => (import.Target, import.Prefix, import.Show is null ? null : string.Join(',', import.Show), import.Export, import.Hide is null ? null : string.Join(',', import.Hide))));
        Assert.Null(Node(extract, "javascript:lib/u.js.helper").SeenIn);
        Assert.Equal(
            [("Maker", "lib/u.js", "make"), ("build", null, "")],
            extract.Nodes.Where(node => node.Name is "Maker" or "build").Select(node => (node.Name, node.SeenIn, string.Join(',', node.Exports ?? []))));
        Assert.Equal([("make", null), ("module", null), ("default", "run")], extract.References.Select(reference => (reference.Name, extract.Written(reference.Qualifier))));
        Assert.Empty(extract.Dynamic);
    }

    /// <summary>
    /// A require is bound in whichever declarator of a statement it stands, its type written or not, and one whose value
    /// is more - a call of what it answers, a pattern of a member of it - imports its file with no name bound; exports.k =
    /// require(...) exports that file's namespace by k, not its names; an import or a require whose specifier the code
    /// computes is marked dynamic and imports nothing.
    /// </summary>
    [Fact]
    public void ARequireInAnyDeclarator_IsBound_AndOneTheCodeComputesIsDynamic()
    {
        var extract = Read("lib/w.js", """
            var path = require('path'), root = path.resolve('.'), util = require('./util');
            const { router } = require('./routes')(app);
            const { a } = require('./a').b;
            const app = require('express')
              .Router();
            exports.utils = require('./utils');
            const { ...rest } = require('./config'), {} = require('./polyfill');
            function load(lang, name) {
              import(`./locale/${lang}.js`);
              require('./' + name);
              import('./locale/' + lang);
            }
            """);

        Assert.Equal(
            [("path", "path", null, false), ("./util", "util", null, false), ("./config", null, "", false), ("./polyfill", null, "", false), ("./utils", null, "", false), ("./routes", null, "", false), ("./a", null, "", false), ("express", null, "", false)],
            extract.Imports.Where(import => !import.Fallback).Select(import => (import.Target, import.Prefix, import.Show is null ? null : string.Join(',', import.Show), import.Export)));
        Assert.Equal(3, extract.Dynamic.Count(dynamic => dynamic.Why.EndsWith("of a module the code computes", StringComparison.Ordinal)));

        var typed = new TypeScriptExtractor().Extract(new CodeFileInput("src/p.ts", "const pkg: PackageJson = require('../package.json');\npkg.version();"));

        Assert.Equal([("../package.json", "pkg")], typed.Imports.Select(import => (import.Target, import.Prefix)));
    }

    /// <summary>A JSX tag or a new of a function the body declares is the body's own, and no call of the repository's; an exported object's async, generator and accessor methods are its functions.</summary>
    [Fact]
    public void ALocalFunctionsElementOrNew_IsTheBodysOwn_AndAnExportedObjectsMethodsAreItsFunctions()
    {
        var page = Read("src/page.jsx", "export function Page() {\n  function Row() {}\n  new Row();\n  return <Row />;\n}");

        Assert.Empty(page.References);

        var exported = Read("lib/o.js", "module.exports = { async f() {}, *g() {}, get x() { return 1; }, [k]() { run(); } };");

        Assert.Equal(["f", "g", "x"], exported.Nodes.Where(node => node.Kind == CodeNodeKinds.Member && node.Name != "(module)").Select(node => node.Name));

        // The computed key k is read to name the method: a use, beside the call the method's body makes.
        Assert.Equal(["k", "run"], exported.References.Select(reference => reference.Name));
    }

    /// <summary>
    /// An export * re-exports every name of the file it names but its default, an export * as ns imports the file and
    /// makes none of its names this one's: a default imported from a file of several export *s is that file's own.
    /// </summary>
    [Fact]
    public void AnExportStar_ReExportsAllButTheDefault_AndAnExportStarAsANamespaceImportsTheFile()
    {
        var extract = Read("ui/index.js", "export * from './Button';\nexport * as icons from './icons';");

        Assert.Equal(
            [("./Button", true, "default", null), ("./icons", false, null, "")],
            extract.Imports.Where(import => !import.Fallback)
                .Select(import => (import.Target, import.Export, import.Hide is null ? null : string.Join(',', import.Hide), import.Show is null ? null : string.Join(',', import.Show))));

        var linked = Link(
            ("ui/index.js", "export * from './Button';\nexport * from './Card';\nexport default function Library() {}"),
            ("ui/Button.js", "export default function Button() {}"),
            ("ui/Card.js", "export default function Card() {}"),
            ("app.js", "import Lib from './ui';\nexport function go() { Lib(); }"));

        Assert.Equal(
            [new CodeEdge("javascript:app.js.go", "javascript:ui/index.js.Library", CodeEdgeKinds.Calls, 2, CodeConfidences.Exact)],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Calls));
    }

    /// <summary>
    /// A function a body declares is its own wherever the body calls it, JavaScript hoisting it, and a function
    /// expression's own name its body's to call itself by: neither is a call of the repository's.
    /// </summary>
    [Fact]
    public void AHoistedFunction_AndAFunctionExpressionsName_AreTheBodysOwn()
    {
        var extract = Read("src/h.js", "function main() {\n  setup();\n  function setup() {}\n}\nconst f = function g() { g(); };");

        Assert.Empty(extract.References);
    }

    /// <summary>
    /// A declarator after a statement's first begins where it is written, its header its own; a call that spreads its
    /// arguments gives no count of them; an import of a module the code computes is marked dynamic.
    /// </summary>
    [Fact]
    public void ADeclaratorBeginsWhereItIsWritten_ASpreadGivesNoCount_AndAComputedImportIsDynamic()
    {
        var extract = Read("src/v.js", "const a = () => 1,\n  b = () => 2;\nfunction go(name, args) { f(...args); import(name); require(name); }");

        Assert.Equal([("a", 1, "const a = ()"), ("b", 2, "b = ()")], extract.Nodes.Where(node => node.Name is "a" or "b").Select(node => (node.Name, node.Start, node.Signature)));
        Assert.Null(Assert.Single(extract.References, reference => reference.Name == "f").Arity);
        Assert.Equal(["an import of a module the code computes", "an import by require of a module the code computes"], extract.Dynamic.Select(dynamic => dynamic.Why));
        Assert.Equal([JsReading.ComputedImport, JsReading.ComputedImport], extract.Dynamic.Select(dynamic => dynamic.Kind));
        CodeGraphContract.Signed(new JavaScriptExtractor(), extract, "src/v.js");
    }

    /// <summary>
    /// A module keeps what it does not export by its own name to itself - an export statement or a list naming it exports
    /// it - and a declaration another file imports by a name besides its own - a list's alias, a default - carries that
    /// name, its own still the module's: an importer finds it by that name, and no file finds it by its own; a script,
    /// which neither imports nor exports, keeps nothing, its declarations named as no module's.
    /// </summary>
    [Fact]
    public void AModuleKeepsWhatItDoesNotExport_AndAScriptNothing()
    {
        var module = Read("src/m.js", """
            export function a() {}
            function b() {}
            function c() {}
            function d() {}
            export { b, c as see };
            export default d;
            export default function handler() {}
            """);
        var script = Read("src/s.js", "function a() {}\nvar b = 1;");
        var listed = Read("src/l.js", "function a() { b(); }\nclass K { m() {} }\nexport { a, K as Kind };");

        Assert.Equal(
            [("a", null, null), ("b", null, null), ("c", "src/m.js", "see"), ("d", "src/m.js", "default"), ("handler", "src/m.js", "default")],
            module.Nodes.Select(node => (node.Name, node.SeenIn, node.Exports is null ? null : string.Join(',', node.Exports))));
        Assert.Equal(["src/m.js"], module.Scopes.Select(scope => scope.Name.Whole()));
        Assert.Equal([("javascript:a", null), ("javascript:b", null)], script.Nodes.Select(node => (node.Id.Whole(), node.SeenIn?.Whole())));
        Assert.Empty(script.Scopes);
        Assert.Equal(listed.Nodes.Single(node => node.Name == "a").Id, Assert.Single(listed.References, reference => reference.Name == "b").From);
        Assert.Equal(listed.Nodes.Single(node => node.Name == "K").Id, listed.Nodes.Single(node => node.Name == "m").Parent);

        foreach (var path in (string[])["src/plain.mjs", "src/plain.cjs"])
        {
            Assert.Equal(path, Assert.Single(Read(path, "function f() {}").Nodes).SeenIn?.Whole());
        }

        var linked = Link(
            ("src/m.js", "function c() {}\nfunction d() {}\nfunction hidden() {}\nexport { c as see };\nexport default d;"),
            ("src/use.js", "import main, { see } from './m.js';\nexport function go() { main(); see(); hidden(); }"),
            ("src/own.js", "import { c, d } from './m.js';\nexport function mine() { c(); d(); }"),
            ("public/page.js", "function page() { c(); d(); }"));

        Assert.Contains(new CodeEdge("javascript:src/use.js.go", "javascript:src/m.js.d", CodeEdgeKinds.Calls, 2, CodeConfidences.Exact), linked.Edges);
        Assert.Contains(new CodeEdge("javascript:src/use.js.go", "javascript:src/m.js.c", CodeEdgeKinds.Calls, 2, CodeConfidences.Unique), linked.Edges);
        Assert.DoesNotContain(linked.Edges, edge => edge is { Kind: CodeEdgeKinds.Calls, To: "javascript:src/m.js.hidden" });
        Assert.DoesNotContain(linked.Edges, edge => edge.Kind == CodeEdgeKinds.Calls && edge.From is "javascript:src/own.js.mine" or "javascript:page");
    }

    /// <summary>
    /// A call's receiver is what the file says it is: this, super, an import's prefix, a local a new made, a type's name, a
    /// chain of values, a new's result, an optional call; a tagged template is a call of its tag.
    /// </summary>
    [Fact]
    public void AReceiver_IsWhatTheFileSaysItIs()
    {
        var extract = Read("src/r.mjs", """
            import * as p from './p.js';

            class R extends Q {
              go() {
                this.one();
                super.two();
                p.three();
                const made = new Thing();
                made.four();
                Thing.five();
                this.store.items.six();
                new Thing().seven();
                made?.eight?.();
                css`nine`;
                styled.div`ten`;
              }
            }
            """);

        Assert.Equal(
            [
                ("one", "this", null), ("two", "base", null), ("three", "prefix", "p"), ("four", "type", "Thing"), ("five", "named", "Thing"),
                ("six", "value", "store.items"), ("seven", "type", "Thing"), ("eight", "type", "Thing"), ("css", null, null), ("div", "value", "styled"),
            ],
            extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Calls).Select(reference => (reference.Name, reference.Qualifier?.Kind, extract.Written(reference.Qualifier))));
    }

    /// <summary>
    /// What a reader cannot bind is marked dynamic, and not linked: a computed member's call, eval, new Function,
    /// Reflect.apply and Reflect.construct, a function value's call and apply, a call through a parameter, a local or a
    /// variable no function is bound to, and a component a value holds.
    /// </summary>
    [Fact]
    public void WhatCannotBeBound_IsMarkedDynamic()
    {
        var extract = Read("src/d.jsx", """
            const handler = make();
            export function go(callback, name) {
              api[name]();
              eval('x');
              new Function('return 1');
              Reflect.apply(target, null, []);
              Reflect.construct(Target, []);
              fn.call(null);
              fn.apply(null, []);
              callback();
              handler();
              const Icon = pick(name);
              return <Icon />;
            }
            """);

        Assert.Equal(
            [
                "a call through a computed member", "a call of eval", "a function made by new Function", "a call through Reflect.apply", "a call through Reflect.construct",
                "a call through a function value's call", "a call through a function value's apply", "a call through the function value callback",
                "a call through the function value handler", "a component held by the value Icon",
            ],
            extract.Dynamic.Select(dynamic => dynamic.Why));
        Assert.Equal(new[] { JsReading.FunctionValue, JsReading.ComputedCode, JsReading.ComputedCode }.Concat(Enumerable.Repeat(JsReading.FunctionValue, 7)), extract.Dynamic.Select(dynamic => dynamic.Kind));
        CodeGraphContract.Signed(new JavaScriptExtractor(), extract, "src/d.jsx");
        Assert.Equal(["make", "pick"], extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Calls).Select(reference => reference.Name));
    }

    /// <summary>
    /// A JSX element's component - a capitalized tag, a member's chain - is a call of it, an element of markup's own none;
    /// the code its attributes and children hold is read, a comment in it not; and each JSX attribute's string - a
    /// <c>className</c>, a <c>class</c>, an <c>id</c> - a use of each rule it names, as markup's own are, read where the
    /// attribute's name stands before its string.
    /// </summary>
    [Fact]
    public void AnElementsComponent_IsACallOfIt_AndItsCodeIsRead_AndItsClassAndIdUseTheirRules()
    {
        var extract = Read("src/e.jsx", """
            import Layout from './layout.jsx';
            export function Page() {
              return (
                <Layout title={heading()}>
                  <Layout.Side open />
                  <div className="x" id="main">{body()}{/* hidden() */}</div>
                  <p class="a b" />
                  <custom-element />
                </Layout>
              );
            }
            """);

        Assert.Equal(
            [
                (".x", CodeQualifierKinds.Selector, null), ("#main", CodeQualifierKinds.Selector, null), (".a", CodeQualifierKinds.Selector, null), (".b", CodeQualifierKinds.Selector, null),
                ("heading", null, null), ("body", null, null), ("default", "prefix", "Layout"), ("Side", "named", "Layout.default"),
            ],
            extract.References.Select(reference => (reference.Name, reference.Qualifier?.Kind, extract.Written(reference.Qualifier))));
        Assert.Equal([6, 6, 7, 7], extract.References.Where(reference => reference.Qualifier?.Kind == CodeQualifierKinds.Selector).Select(reference => reference.Line));
    }

    /// <summary>
    /// In a test file, each describe, context and suite call is a test group and each it, test and specify call a test -
    /// with .only, .skip and .each - named by its description as written, nested in the group that holds it; elsewhere
    /// they are calls.
    /// </summary>
    [Fact]
    public void ATestFilesTestCalls_AreTests_NamedByTheirDescriptions()
    {
        const string Text = """
            describe('outer', () => {
              context(`inner`, () => {
                it.only('one', () => { run(); });
              });
              test.skip('two', () => {});
              it.each([1])('three %i', (n) => {});
            });
            """;

        foreach (var path in (string[])["test/a.js", "tests/a.js", "src/__tests__/a.js", "src/a.test.js", "src/a.spec.mjs"])
        {
            var extract = Read(path, Text);

            Assert.Equal(
                [("'outer'", "testGroup"), ("`inner`", "testGroup"), ("'one'", "test"), ("'two'", "test"), ("'three %i'", "test")],
                extract.Nodes.Select(node => (node.Name, node.SubKind!)));
            Assert.All(extract.Nodes, node => Assert.True(node.Test));
            Assert.Equal(extract.Nodes[1].Id, extract.Nodes[2].Parent);
        }

        Assert.DoesNotContain(Read("src/a.js", Text).Nodes, node => node.Test);
    }

    /// <summary>A file a bundler or a generator wrote says so: by its name, or by what its first lines say.</summary>
    [Theory]
    [InlineData("dist/app.min.js", "function a() {}", "its name ends .min.js, as a bundler names what it writes")]
    [InlineData("dist/app.bundle.js", "function a() {}", "its name ends .bundle.js, as a bundler names what it writes")]
    [InlineData("src/api.generated.js", "function a() {}", "its name holds .generated., as a generator names what it writes")]
    [InlineData("src/api.js", "/* @generated by a tool */\nfunction a() {}", "its first lines say @generated, as a generator writes")]
    [InlineData("src/api.js", "// Code generated. DO NOT EDIT.\nfunction a() {}", "its first lines say DO NOT EDIT, as a generator writes")]
    [InlineData("src/api.js", "function a() {}", null)]
    public void AGeneratedFile_SaysSo(string path, string text, string? why) => Assert.Equal(why, Read(path, text).Generated);

    /// <summary>
    /// A line break ends a statement where JavaScript's would end it: after what may end one, before what does not go on
    /// with it - an operator, a member's dot, an argument list - so each declaration of a file written without semicolons
    /// runs to its own last line.
    /// </summary>
    [Fact]
    public void ALineBreak_EndsAStatement_WhereJavaScriptsWould()
    {
        var extract = Read("src/n.mjs", """
            const a = 1
            const b = a
              + 2
            const c = [1, 2]
              .map(n => n * 2)
            let d = c
            export const e = () => {
              go()
            }
            const f = 3
            """);

        Assert.Equal(
            [("a", 1, 1), ("b", 2, 3), ("c", 4, 5), ("d", 6, 6), ("e", 7, 9), ("f", 10, 10)],
            extract.Nodes.Select(node => (node.Name, node.Start, node.End)));
    }

    /// <summary>
    /// Blocks nested five thousand deep in a body are read whole, each a scope of its own on the reader's stack of them:
    /// the const each declares hides the module's function of its name inside it, and the call after the last one closes
    /// is the module's function's - in a child given a small stack, which a reading by recursion would overflow.
    /// </summary>
    [Fact]
    public async Task ABlockFiveThousandDeep_IsReadWhole()
        => Assert.Equal(
            new ChildRunAnswer(0, "calls probe 3, calls helper 4; 0 dynamic", ""),
            await ChildRun.MethodAsync(typeof(JavaScriptExtractorTests), nameof(BlocksFiveThousandDeep), TestContext.Current.CancellationToken));

    /// <summary>The child of <see cref="ABlockFiveThousandDeep_IsReadWhole"/>: what the deep body references, and how much is marked dynamic.</summary>
    internal static string BlocksFiveThousandDeep() => SmallStack.Run(() => Said(Read(
        "src/deep.js",
        $"export function helper() {{}}\nexport function deep() {{\n{string.Concat(Enumerable.Repeat("{ const helper = 1; ", 5_000))}probe(helper);{new string('}', 5_000)}\nhelper();\n}}\n")));

    /// <summary>
    /// Templates nested five thousand deep are read whole, each hole's code a run of its own on the reader's stack of
    /// runs and none a call into the reading - the call the deepest makes among what the body calls, nothing marked
    /// dynamic - in a child given a small stack.
    /// </summary>
    [Fact]
    public async Task InterpolationsNestedFiveThousandDeep_AreReadWhole()
        => Assert.Equal(
            new ChildRunAnswer(0, "calls deep 1; 0 dynamic", ""),
            await ChildRun.MethodAsync(typeof(JavaScriptExtractorTests), nameof(InterpolationsFiveThousandDeep), TestContext.Current.CancellationToken));

    /// <summary>The child of <see cref="InterpolationsNestedFiveThousandDeep_AreReadWhole"/>: what the body references, and how much is marked dynamic.</summary>
    internal static string InterpolationsFiveThousandDeep() => SmallStack.Run(() => Said(Read(
        "src/t.js",
        $"export function go() {{ return {string.Concat(Enumerable.Repeat("`${", 5_000))}deep(){string.Concat(Enumerable.Repeat("}`", 5_000))}; }}")));

    /// <summary>
    /// Test groups nested five thousand deep are read whole, each a node in the group that holds it and what its
    /// arguments hold a run of code on the reader's stack of runs - the deepest group's call its own - in a child given a
    /// small stack.
    /// </summary>
    [Fact]
    public async Task TestGroupsNestedFiveThousandDeep_AreReadWhole()
        => Assert.Equal(
            new ChildRunAnswer(0, "5000 groups, deep called from one 5000 deep; 0 dynamic", ""),
            await ChildRun.MethodAsync(typeof(JavaScriptExtractorTests), nameof(TestGroupsFiveThousandDeep), TestContext.Current.CancellationToken));

    /// <summary>The child of <see cref="TestGroupsNestedFiveThousandDeep_AreReadWhole"/>: how many groups were read, how deep the one that calls is, and how much is marked dynamic.</summary>
    internal static string TestGroupsFiveThousandDeep() => SmallStack.Run(() =>
    {
        var extract = Read("test/g.js", string.Concat(Enumerable.Repeat("describe(g, () => {", 5_000)) + "deep();" + string.Concat(Enumerable.Repeat("});", 5_000)));
        var nodes = extract.Nodes.ToDictionary(node => node.Id);
        var depth = 0;

        for (var id = Assert.Single(extract.References, reference => reference.Kind == CodeEdgeKinds.Calls).From; id is not null; id = nodes[id].Parent)
        {
            depth++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{extract.Nodes.Count(node => node.SubKind == "testGroup")} groups, deep called from one {depth} deep; {extract.Dynamic.Count} dynamic");
    });

    /// <summary>What <paramref name="extract"/> references, each by its kind, name and line, and how many lines it marks dynamic: a depth test child's answer.</summary>
    private static string Said(FileExtract extract)
        => string.Create(CultureInfo.InvariantCulture, $"{string.Join(", ", extract.References.Select(reference => $"{reference.Kind} {reference.Name} {reference.Line}"))}; {extract.Dynamic.Count} dynamic");

    /// <summary>
    /// A local is its scope's, as JavaScript scopes it: a let, a const, a class and a function a block declares - the
    /// function from the block's first line - are the block's, a loop head's the loop's, a catch's error its block's, an
    /// arrow's parameter the arrow's, and a var its function's. Each hides the same name of the module's or an import's
    /// inside its scope alone: past it, a call of the name is a call of the module's.
    /// </summary>
    [Fact]
    public void ALocal_IsItsScopes_AndHidesANameInsideItAlone()
    {
        var extract = Read("src/scope.js", """
            import { format } from './fmt';
            export function item() {}
            export function render(list) {
              if (list.length === 0) {
                const format = 'none';
                return format;
              }
              list.map(item => item.id);
              { inner(); function inner() {} }
              { class Shape {} new Shape(); }
              { var later = make(); }
              for (let step = 0; step < 1; step++) {}
              try { go(); } catch (error) { log(error); }
              inner();
              item();
              later();
              error();
              step();
              new Shape();
              return format(list);
            }
            """);

        Assert.Equal(
            [
                ("calls", "map", 8), ("calls", "make", 11), ("calls", "go", 13), ("calls", "log", 13), ("calls", "inner", 14), ("calls", "item", 15),
                ("calls", "error", 17), ("calls", "step", 18), ("instantiates", "Shape", 19), ("calls", "format", 20),
            ],
            extract.References.Where(reference => reference.Kind is CodeEdgeKinds.Calls or CodeEdgeKinds.Instantiates).Select(reference => (reference.Kind, reference.Name, reference.Line)));
        Assert.Equal(["a call through the function value later"], extract.Dynamic.Select(dynamic => dynamic.Why));
    }

    /// <summary>A shebang says how the file is run, and is no summary of the declaration below it.</summary>
    [Fact]
    public void AShebang_IsNoSummary()
        => Assert.Null(Assert.Single(Read("bin/cli.js", "#!/usr/bin/env node\nexport function main() {}").Nodes).Summary);

    /// <summary>
    /// A string's value is what JavaScript reads of it, its escapes decoded; a template's constant parts are each a literal
    /// of their own, a <c>$</c> before a hole's <c>${</c> one of them; a regular expression is none, nor is a JSX element's
    /// markup, whose attributes' quoted texts are, their character references decoded; and a parameter's default is its
    /// function's code. A use is of a value the code reads: a receiver's first name where it is a value - a namespace an
    /// import binds and a type the language declares not - each branch of a conditional, and no key of an object literal.
    /// </summary>
    [Fact]
    public void AStringsValue_IsWhatJavaScriptReadsOfIt_AndAUseIsOfAValueTheCodeReads()
    {
        var extract = Read("src/price.jsx", """
            import * as ns from './ns';
            import { config } from './config';

            export function Price({ amount }, label = 'parameter default') {
              const re = /a\/b"c/g, plain = /plain text/;
              const tab = 'a\tb \u{41}\x42C';
              const total = `USD $${amount} due`;
              const view = <Badge title="Tom &amp; Jerry &#33;" kind='second'>{label} text</Badge>;
              const port = config.port;
              ns.helper();
              Math.max(1, 2);
              const big = isBig ? LIMIT : SMALL;
              return { key: value, other: 2 };
            }
            """);

        const string Price = "javascript:src/price.jsx.Price";

        Assert.Equal(
            [
                new LiteralRow("parameter default", 4, Price),
                new LiteralRow("a\tb ABC", 6, Price),
                new LiteralRow("USD $", 7, Price),
                new LiteralRow(" due", 7, Price),
                new LiteralRow("Tom & Jerry !", 8, Price),
                new LiteralRow("second", 8, Price),
            ],
            LiteralRow.Of(extract));
        Assert.Equal(
            [("config", 9, ""), ("port", 9, "value config"), ("isBig", 12, ""), ("LIMIT", 12, ""), ("SMALL", 12, ""), ("value", 13, "")],
            extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Uses).Select(reference => (reference.Name, reference.Line, $"{reference.Qualifier?.Kind} {extract.Written(reference.Qualifier)}".Trim())));
    }

    /// <summary>
    /// Every literal that holds a character is kept, however short - a one-letter one too, an empty one none - and each
    /// value once, however many times the file writes it: ten thousand of one value are one literal of ten thousand places,
    /// each its own line. A floor of three characters dropped the one letter; a literal kept a place at a time held the
    /// value ten thousand times.
    /// </summary>
    [Fact]
    public void EveryNonEmptyLiteral_IsKeptOnce()
    {
        var text = "const one = 'a';\nconst none = '';\n" + string.Concat(Enumerable.Repeat("use('same');\n", 10_000));
        var extract = Read("src/many.js", text);

        Assert.Equal(["a", "same"], (extract.Literals ?? []).Select(literal => literal.Value));
        Assert.Equal([1], extract.Literals![0].Places.Select(place => place.Line));
        Assert.Equal(Enumerable.Range(3, 10_000), extract.Literals[1].Places.Select(place => place.Line));
        Assert.Null(extract.Problem("javascript", CodeFileText.Lines(text).Count));
    }

    /// <summary>
    /// A call's receiver of any count of names is read by them all; and an import whose string its line leaves open
    /// names nothing.
    /// </summary>
    [Fact]
    public void AReceiverOfAnyCountOfNames_IsReadByThemAll_AndAnImportLeftOpenNamesNothing()
    {
        var extract = Read("src/run.js", """
            import open from './open
            import closed from './closed';

            export function run() {
              a.b.c.d.e.f.g.h.go();
              a.b.c.d.e.f.g.h.i.go();
            }
            """);

        Assert.Equal(
            [(5, "value a.b.c.d.e.f.g.h"), (6, "value a.b.c.d.e.f.g.h.i")],
            extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Calls).Select(reference => (reference.Line, $"{reference.Qualifier?.Kind} {extract.Written(reference.Qualifier)}".Trim())));
        Assert.Equal([2], extract.Imports.Select(import => import.Line).Distinct());
    }

    /// <summary>
    /// A chain of sixty names is written as sixty references of one name each: the second qualified by the first's name,
    /// each after by a link to the reference the name before it made - one link a name, so the chain costs its length -
    /// and the call at its end by a link to the last; its names read whole from the links (<see cref="FileExtract.Written"/>).
    /// </summary>
    [Fact]
    public void ASixtyNameChain_IsWrittenAsSixtyLinks()
    {
        var names = Enumerable.Range(1, 60).Select(index => string.Create(CultureInfo.InvariantCulture, $"n{index}")).ToList();
        var text = $"export function run() {{\n  {string.Join('.', names)}.go();\n}}\n";
        var extract = Read("src/chain.js", text);
        var uses = extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Uses).ToList();
        var call = Assert.Single(extract.References, reference => reference.Kind == CodeEdgeKinds.Calls);

        Assert.Equal(names, uses.Select(reference => reference.Name));
        Assert.Null(uses[0].Qualifier);
        Assert.Equal(new ExtractQualifier(CodeQualifierKinds.Value, "n1"), uses[1].Qualifier);
        Assert.All(Enumerable.Range(2, 58), at => Assert.Equal(new ExtractQualifier(CodeQualifierKinds.Value) { Link = at - 1 }, uses[at].Qualifier));
        Assert.Equal(new ExtractQualifier(CodeQualifierKinds.Value) { Link = 59 }, call.Qualifier);
        Assert.Equal(string.Join('.', names), extract.Written(call.Qualifier));
        Assert.Null(extract.Problem("javascript", CodeFileText.Lines(text).Count));
    }

    /// <summary>
    /// A member a string names in brackets - <c>styles['btn-big']</c>, a name no dot can write - is a use of it as the
    /// member a dot names is, qualified by its receiver alike: by the prefix a style sheet's import binds; a member the
    /// code computes is none.
    /// </summary>
    [Fact]
    public void AMemberAStringNames_IsAUse_QualifiedAsADotsIs()
    {
        var extract = Read("src/view.js", """
            import styles from './site.css';
            import classes from './Cart.module.css';
            export function view(key) {
              return [styles['btn-big'], styles.wide, classes['is-open'], classes.closed, styles[key]];
            }
            """);

        Assert.Equal(
            [("btn-big", "prefix", "styles"), ("wide", "prefix", "styles"), ("is-open", "prefix", "classes"), ("closed", "prefix", "classes")],
            extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Uses).Select(reference => (reference.Name, reference.Qualifier?.Kind, extract.Written(reference.Qualifier))));
    }

    /// <summary>
    /// Every declarator of a declaration declares a local - <c>const a = 1, b = 2</c>, one on the next line after a comma,
    /// a pattern and a <c>for</c>'s among them - and none is a use, while a line the declaration does not go on to with a
    /// comma is code of its own.
    /// </summary>
    [Fact]
    public void EachDeclaratorOfADeclaration_IsALocal_AndNoUse()
    {
        var extract = Read("src/sum.js", """
            export function sum() {
              const a = 1, b = total;
              let c = 0, d = first
              seen = q, rest = 1
              const e = 2,
                { f, g } = second;
              for (let i = 0, j = limit; i < j; i++) {}
              return a + b + c + d + e + f + g;
            }
            """);

        Assert.Equal(
            [("total", 2), ("first", 3), ("seen", 4), ("q", 4), ("rest", 4), ("second", 6), ("limit", 7)],
            extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Uses).Select(reference => (reference.Name, reference.Line)));
    }

    /// <summary>
    /// The first name of a receiver's chain - a member's or an element's - is a use where it is a value, by what the file
    /// says of it whatever its case: a constant it declares, a name an import binds, a default import by its export, a
    /// name it says nothing of and writes as a value's; a type it declares and a namespace an import binds are none.
    /// </summary>
    [Fact]
    public void AReceiversFirstName_IsAUse_WhereTheFileSaysItIsAValue()
    {
        var extract = Read("src/h.js", """
            import Router from 'next/router';
            import { Config, routes } from './config';
            import * as tools from './tools';
            export const Settings = { retries: 3 };
            export class Store { static size = 1; }
            export function go(type) {
              Router.push('/home');
              const t = Config.timeout + Settings.retries + tools.limit + Store.size;
              return routes[0] + handlers[type] + t;
            }
            """);

        Assert.Equal(
            [
                ("default", "prefix", "Router", 7), ("Config", null, null, 8), ("timeout", "named", "Config", 8), ("Settings", null, null, 8), ("retries", "named", "Settings", 8),
                ("limit", "prefix", "tools", 8), ("size", "named", "Store", 8), ("routes", null, null, 9), ("handlers", null, null, 9),
            ],
            extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Uses).Select(reference => (reference.Name, reference.Qualifier?.Kind, extract.Written(reference.Qualifier), reference.Line)));
    }

    /// <summary>A label a break or a continue names is no use, where a case's name is one.</summary>
    [Fact]
    public void ALabelABreakOrAContinueNames_IsNoUse()
    {
        var extract = Read("src/c.js", """
            export function walk(items, limit) {
              outer: for (const item of items) {
                for (const other of items) {
                  if (other === item) continue outer;
                  if (!other) break outer;
                }
              }
              switch (items.length) { case LIMIT: return limit; }
            }
            """);

        Assert.Equal([("length", 8), ("LIMIT", 8)], extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Uses).Select(reference => (reference.Name, reference.Line)));
    }

    /// <summary>
    /// A JSX attribute's character reference is read as HTML reads one, of any length: a number written with any count of
    /// digits its character, one past every character's the replacement character, and a name no entity has its text.
    /// </summary>
    [Fact]
    public void AnAttributesCharacterReference_IsReadAsHtmlReadsOne_OfAnyLength()
    {
        var digits = new string('0', 40);
        var extract = Read("src/a.jsx", $$"""
            export const view = <a title="x&#{{digits}}65;y &notanentityofanyname; &#99999999999999; &amp" />;
            """);

        Assert.Equal(["xAy &notanentityofanyname; � &amp"], LiteralRow.Of(extract).Select(literal => literal.Value));
    }

    /// <summary>A String.raw template's value is its text as written, no escape read in it, where another tag's template is read by JavaScript's escapes.</summary>
    [Fact]
    public void AStringRawTemplate_KeepsItsTextAsWritten()
    {
        var extract = Read("src/n.js", """
            export function f() {
              const raw = String.raw`^\d+$ raw pattern`;
              const tagged = html`tab\tseparated text`;
              return raw + tagged;
            }
            """);

        Assert.Equal(["^\\d+$ raw pattern", "tab\tseparated text"], LiteralRow.Of(extract).Select(literal => literal.Value));
    }

    /// <summary>
    /// A type Backbone builds by a call of extend - <c>var View = Backbone.View.extend({...})</c> - is a type that inherits what it
    /// extends, the object it is given its members: each function a method and each value a field; its <c>events</c> hash's keys
    /// uses of the rules their selectors name and its handlers calls of the methods they name, a method the events bind reached
    /// from the type; and a member a prototype assignment adds - <c>View.prototype.x = function () {}</c> - a member of the type,
    /// in its scope as the file declares it, reached by a call on the type and reading its own <c>this</c> as the type's.
    /// </summary>
    [Fact]
    public void ABackboneType_ExtendCallIsATypeInheritingItsBase_WithTheObjectsMembers_AndAPrototypeMemberIsItsMember()
    {
        const string Text = """
            var Backbone = require('backbone');

            var InvoiceView = Backbone.View.extend({
              tagName: 'li',
              initialize: function () { this.listenTo(this.model, 'change', this.render); },
              render: function () { this.$el.html(this.template()); },
              events: { 'click .save': 'save', 'dblclick #delete': 'remove' },
              save: function () { this.model.save(); }
            });

            InvoiceView.prototype.template = function () { return this.render(); };

            new InvoiceView().template();
            """;
        var extract = Read("lib/invoice.js", Text);
        var type = "javascript:lib/invoice.js.InvoiceView";

        Assert.Equal((CodeNodeKinds.Type, "class", 3, 9), (Node(extract, type).Kind, Node(extract, type).SubKind, Node(extract, type).Start, Node(extract, type).End));

        // The object's functions are methods and its values fields, each a member of the type over its own lines.
        Assert.Equal(
            [("field", 4, 4), ("method", 5, 5), ("method", 6, 6), ("method", 8, 8)],
            [.. new[] { "tagName", "initialize", "render", "save" }.Select(name => (Node(extract, $"{type}.{name}").SubKind, Node(extract, $"{type}.{name}").Start, Node(extract, $"{type}.{name}").End))]);
        Assert.Equal(type, Node(extract, $"{type}.save").Parent);

        // It inherits what it extends, read as the chain names it: through the import Backbone is bound by.
        Assert.Equal([("View", 3, CodeQualifierKinds.Prefix, "Backbone")], [.. extract.References.Where(reference => reference.From == type && reference.Kind == CodeEdgeKinds.Inherits).Select(reference => (reference.Name, reference.Line, reference.Qualifier?.Kind, extract.Written(reference.Qualifier)))]);

        // The events hash: each key a use of the rules its selector names, each handler a call of the method it names.
        Assert.Equal([(".save", 7), ("#delete", 7)], [.. extract.References.Where(reference => reference.From == type && reference.Qualifier?.Kind == CodeQualifierKinds.Selector).Select(reference => (reference.Name, reference.Line))]);
        Assert.Equal([("save", 7), ("remove", 7)], [.. extract.References.Where(reference => reference.From == type && reference.Kind == CodeEdgeKinds.Calls && reference.Qualifier?.Kind == CodeQualifierKinds.This).Select(reference => (reference.Name, reference.Line))]);

        // A prototype assignment's member is a member of the type the file declares, in its scope, and no name of the file's.
        var template = Node(extract, $"{type}.template");

        Assert.Equal(("method", (string?)null, 11, 11), (template.SubKind, template.Extends, template.Start, template.End));
        Assert.Equal(type, template.Parent);
        Assert.DoesNotContain(extract.Nodes, node => node.Id == "javascript:lib/invoice.js.template");

        // Linked: the events reach the methods they bind, the prototype member's own this reads the type's members, and a call on
        // the type reaches the prototype member, its member.
        var linked = Link(("lib/invoice.js", Text));

        Assert.Contains(new CodeEdge(type, $"{type}.save", CodeEdgeKinds.Calls, 7, CodeConfidences.Exact), linked.Edges);
        Assert.Contains(new CodeEdge($"{type}.template", $"{type}.render", CodeEdgeKinds.Calls, 11, CodeConfidences.Exact), linked.Edges);
        Assert.Contains(new CodeEdge("javascript:lib/invoice.js.(module)", $"{type}.template", CodeEdgeKinds.Calls, 13, CodeConfidences.Exact), linked.Edges);
    }

    /// <summary>A Backbone router's routes: each handler a string names a call of the method it names, and the functions it is given its methods.</summary>
    [Fact]
    public void ABackboneRouter_RoutesAreCallsOfTheirHandlers()
    {
        var extract = Read("lib/router.js", """
            var Router = Backbone.Router.extend({
              routes: { 'orders': 'list', 'orders/:id': 'show' },
              list: function () {},
              show: function (id) {}
            });
            """);
        var type = "javascript:Router";

        Assert.Equal([("list", 2), ("show", 2)], [.. extract.References.Where(reference => reference.From == type && reference.Kind == CodeEdgeKinds.Calls).Select(reference => (reference.Name, reference.Line))]);
        Assert.Equal([("method", 3, 3), ("method", 4, 4)], [.. new[] { "list", "show" }.Select(name => (Node(extract, $"{type}.{name}").SubKind, Node(extract, $"{type}.{name}").Start, Node(extract, $"{type}.{name}").End))]);
    }

    /// <summary>
    /// A call of <c>extend</c> is a type Backbone builds only where the link before <c>extend</c> names a type and the call is given
    /// what Backbone's is: a merge of objects - Underscore's <c>_.extend</c>, jQuery's <c>$.extend</c> - is a variable whose value's
    /// code is read, and Backbone's second object holds the type's static members.
    /// </summary>
    [Fact]
    public void AMergeOfObjects_IsNoType_AndBackbonesSecondObjectHoldsItsStaticMembers()
    {
        var extract = Read("lib/app.js", """
            var vent = _.extend({}, Backbone.Events);
            var settings = $.extend({ debug: false }, window.APP_CONFIG);
            var Invoice = Backbone.Model.extend({ total: function () { return 0; } }, { create: function () { return build(); } });
            var merged = _.extend({}, { a: 1 });
            var Mixed = Backbone.Model.extend(mixin);
            """);

        // A merge of objects alone, and Backbone's given no object: each gate holds its own.
        Assert.Equal(
            ["variable", "variable", "variable", "variable"],
            new[] { "vent", "settings", "merged", "Mixed" }.Select(name => Node(extract, $"javascript:{name}").SubKind));
        Assert.Contains(extract.References, reference => reference.From == "javascript:vent" && reference.Name == "Events");
        Assert.Contains(extract.References, reference => reference.From == "javascript:settings" && reference.Name == "APP_CONFIG");

        // Backbone's own: the prototype's object its members, the second its static ones, each read whole.
        Assert.Equal((CodeNodeKinds.Type, "class"), (Node(extract, "javascript:Invoice").Kind, Node(extract, "javascript:Invoice").SubKind));
        Assert.Equal(
            [("total", false), ("create", true)],
            extract.Nodes.Where(node => node.Parent == "javascript:Invoice").Select(node => (node.Name, node.Modifiers.Contains("static"))));
        Assert.Equal(["build"], Calls(extract, "javascript:Invoice.create"));
    }

    /// <summary>
    /// A member a prototype assignment adds binds no name of its file: in a module it is its type's member, so the type's own call
    /// of the module's function of the same name reaches the function, never the member itself; and none of the file's names is it.
    /// </summary>
    [Fact]
    public void APrototypeMember_IsItsTypesMember_AndNoNameOfItsFile()
    {
        const string Text = """
            var Backbone = require('backbone');
            function render(view) { return view.el; }
            var View = Backbone.View.extend({});
            View.prototype.render = function (x) { return render(this); };
            module.exports = View;
            """;
        var extract = Read("lib/view.js", Text);
        var view = "javascript:lib/view.js.View";

        Assert.Equal(
            [("javascript:lib/view.js.render", (string?)null), ($"{view}.render", view)],
            extract.Nodes.Where(node => node.Name == "render").Select(node => (node.Id.Whole(), node.Parent?.Whole())));
        Assert.Equal(["default"], Node(extract, view).Exports);
        Assert.Null(Node(extract, $"{view}.render").Exports);

        // Linked: the member's call of render reaches the module's function alone - no edge back to itself.
        var linked = Link(("lib/view.js", Text));

        Assert.Equal(
            [new CodeEdge($"{view}.render", "javascript:lib/view.js.render", CodeEdgeKinds.Calls, 4, CodeConfidences.Exact)],
            linked.Edges.Where(edge => edge.From == $"{view}.render" && edge.Kind == CodeEdgeKinds.Calls));
    }

    /// <summary>
    /// A function whose prototype the file writes is a constructor function: a type whose constructor is its body and whose
    /// prototype's members are its members - each assignment's and each key of an object given the whole prototype - so a member's
    /// <c>this.total()</c> reaches its sibling; and a member added to a type the file does not declare extends it, in a scope of the
    /// type's name whose own names its code does not see by themselves.
    /// </summary>
    [Fact]
    public void AConstructorFunction_IsAType_WhosePrototypesMembersAreItsMembers()
    {
        const string Text = """
            function Cart() { this.lines = []; }
            Cart.prototype.add = function (line) { this.lines.push(line); this.total(); };
            Cart.prototype.total = function () { return 0; };
            Cart.prototype = { clear() { this.total(); } };
            Widget.prototype.paint = function () { draw(); };
            Widget.prototype.draw = function () {};
            export function draw() {}
            """;
        var extract = Read("lib/cart.js", Text);
        var cart = "javascript:lib/cart.js.Cart";

        Assert.Equal((CodeNodeKinds.Type, "class"), (Node(extract, cart).Kind, Node(extract, cart).SubKind));
        Assert.Equal(
            [("constructor", "constructor"), ("add", "method"), ("total", "method"), ("clear", "method"), ("lines", "field")],
            extract.Nodes.Where(node => node.Parent == cart).Select(node => (node.Name, node.SubKind!)));

        // A type the file does not declare: its members extend it, in the scope of its name, no name of the file's.
        Assert.Equal(
            [("javascript:lib/cart.js.Widget.paint", "Widget"), ("javascript:lib/cart.js.Widget.draw", "Widget")],
            extract.Nodes.Where(node => node.Extends is not null).Select(node => (node.Id.Whole(), node.Extends!)));

        var linked = Link(("lib/cart.js", Text));

        Assert.Contains(new CodeEdge($"{cart}.add", $"{cart}.total", CodeEdgeKinds.Calls, 2, CodeConfidences.Exact), linked.Edges);
        Assert.Contains(new CodeEdge($"{cart}.clear", $"{cart}.total", CodeEdgeKinds.Calls, 4, CodeConfidences.Exact), linked.Edges);
        Assert.Equal(
            [new CodeEdge("javascript:lib/cart.js.Widget.paint", "javascript:lib/cart.js.draw", CodeEdgeKinds.Calls, 5, CodeConfidences.Exact)],
            linked.Edges.Where(edge => edge.From == "javascript:lib/cart.js.Widget.paint" && edge.Kind == CodeEdgeKinds.Calls));

        // A member extending a type from outside its body binds no name of its file: the file's export of paint is none of it.
        var exported = Read("lib/cart.js", Text + "\nexport { paint as paintIt };");

        Assert.Null(Node(exported, "javascript:lib/cart.js.Widget.paint").Exports);
    }

    /// <summary>
    /// A constructor function is one wherever it is declared: a chain the file assigns a function whose prototype it writes is the
    /// constructor type of that chain - Closure's <c>App.Cart = function</c> - which another script's <c>new App.Cart()</c> makes,
    /// though no declaration is the scope <c>App</c> it is declared in; and a function another file declares is the type its
    /// prototype's members extend, so their <c>this</c> is read on it and on them, and its <c>new</c> makes it.
    /// </summary>
    [Fact]
    public void AConstructorFunction_ByAChainOrInAnotherFile_IsTheTypeItsPrototypesMembersAreOf()
    {
        const string Chained = """
            var App = App || {};
            App.Cart = function () { this.lines = []; };
            App.Cart.prototype.add = function (line) { this.lines.push(line); this.total(); };
            App.Cart.prototype.total = function () { return 0; };
            """;
        var extract = Read("lib/app-cart.js", Chained);
        var cart = "javascript:App.Cart";

        Assert.Equal((CodeNodeKinds.Type, "class", "App"), (Node(extract, cart).Kind, Node(extract, cart).SubKind, Node(extract, cart).Scope?.Whole()));
        Assert.Equal(["constructor", "add", "total", "lines"], extract.Nodes.Where(node => node.Parent == cart).Select(node => node.Name));
        Assert.Equal(
            [new CodeEdge($"{cart}.add", $"{cart}.total", CodeEdgeKinds.Calls, 3, CodeConfidences.Exact)],
            Link(("lib/app-cart.js", Chained)).Edges.Where(edge => edge.From == $"{cart}.add" && edge.Kind == CodeEdgeKinds.Calls));

        // Another script's new of the chain: the type the chain's names are declared under, the scope App no container.
        Assert.Equal(
            [new CodeEdge("javascript:make", cart, CodeEdgeKinds.Instantiates, 1, CodeConfidences.Exact)],
            Link(("lib/app-cart.js", Chained), ("lib/main.js", "function make() { return new App.Cart(); }")).Edges.Where(edge => edge.From == "javascript:make"));

        // The function in one script, its prototype's members in another: each this reaches the members, and new makes the type.
        var linked = Link(
            ("lib/cart.js", "function Cart() { this.total(); }\nfunction make() { return new Cart(); }"),
            ("lib/cart-total.js", "Cart.prototype.add = function (line) { this.total(); };\nCart.prototype.total = function () { return 0; };"));

        Assert.Equal(
            [
                new CodeEdge("javascript:Cart", "javascript:Cart.total", CodeEdgeKinds.Calls, 1, CodeConfidences.Exact),
                new CodeEdge("javascript:Cart.add", "javascript:Cart.total", CodeEdgeKinds.Calls, 1, CodeConfidences.Exact),
                new CodeEdge("javascript:make", "javascript:Cart", CodeEdgeKinds.Instantiates, 2, CodeConfidences.Unique),
            ],
            linked.Edges.Where(edge => edge.Kind is CodeEdgeKinds.Calls or CodeEdgeKinds.Instantiates).OrderBy(edge => edge.From, StringComparer.Ordinal));
    }

    /// <summary>
    /// A type the file declares holds the members its prototype is given wherever they are written - after code of the file's
    /// own, or after its body closes - so its lines hold theirs and the extract is a sound one, which a graph keeps: a type whose
    /// lines did not hold a member it holds is no extract, and its file would be read as one no implementation could read.
    /// </summary>
    [Fact]
    public void APrototypesMembersWrittenAfterTheirType_AreHeldByItsLines_SoItsExtractIsSound()
    {
        const string Text = """
            function Cart() {}
            var shared = setup();
            Cart.prototype.add = function () {};
            Cart.prototype = { clear() {} };
            class Box {}
            Box.prototype.open = function () {};
            """;
        var extract = Read("lib/cart.mjs", Text);

        Assert.Null(extract.Problem("javascript", 6));
        Assert.Equal(((1, 4), (5, 6)), (Lines(extract, "javascript:lib/cart.mjs.Cart"), Lines(extract, "javascript:lib/cart.mjs.Box")));
        Assert.Equal(
            ["javascript:lib/cart.mjs.Cart.constructor", "javascript:lib/cart.mjs.Cart.add", "javascript:lib/cart.mjs.Cart.clear", "javascript:lib/cart.mjs.Box.open"],
            extract.Nodes.Where(node => node.Parent is not null).Select(node => node.Id.Whole()));
    }

    /// <summary>
    /// A constructor function another module declares is the type its prototype's members extend wherever the extending file binds
    /// it - by a <c>require</c> of its module or a default import, as a call of the name finds it: the module's default - so their
    /// <c>this</c> reaches each other, and a <c>new</c> of it through either binding makes it.
    /// </summary>
    [Theory]
    [InlineData("function Cart() { this.lines = []; }\nmodule.exports = Cart;", "var Cart = require('{0}');")]
    [InlineData("export default function Cart() { this.lines = []; }", "import Cart from '{0}';")]
    public void AConstructorAnotherModuleDeclares_IsFoundThroughARequireOrADefaultImport(string declared, string binding)
    {
        string Bound(string specifier) => binding.Replace("{0}", specifier, StringComparison.Ordinal);

        var linked = Link(
            ("lib/cart.js", declared),
            ("lib/cart-total.js", Bound("./cart") + "\nCart.prototype.total = function () { return this.count(); };\nCart.prototype.count = function () { return 0; };"),
            ("app/make.js", Bound("../lib/cart") + "\nexport function make() { return new Cart(); }"));

        Assert.Equal(
            [
                new CodeEdge("javascript:app/make.js.make", "javascript:lib/cart.js.Cart", CodeEdgeKinds.Instantiates, 2, CodeConfidences.Exact),
                new CodeEdge("javascript:lib/cart-total.js.Cart.total", "javascript:lib/cart-total.js.Cart.count", CodeEdgeKinds.Calls, 2, CodeConfidences.Unique),
            ],
            linked.Edges.Where(edge => edge.Kind is CodeEdgeKinds.Calls or CodeEdgeKinds.Instantiates).OrderBy(edge => edge.From, StringComparer.Ordinal));
    }

    /// <summary>
    /// Members the repository adds to the prototypes of two modules' defaults, which no constructor function the repository
    /// declares is - what a call returns - extend each the one its file names by the whole chain, <c>Cart.default</c> and
    /// <c>Box.default</c>, so a value a <c>new</c> of one makes reaches its own members and never the other's, though both chains
    /// end in the one name <c>default</c>.
    /// </summary>
    [Fact]
    public void MembersAddedToTwoModulesDefaults_ExtendEachTheOneItsChainNames_ThoughBothEndInDefault()
    {
        var linked = Link(
            ("lib/cart.js", "module.exports = make();"),
            ("lib/box.js", "module.exports = make();"),
            ("app/main.js", """
                var Cart = require('../lib/cart');
                var Box = require('../lib/box');
                Cart.prototype.add = function () {};
                Box.prototype.add = function () {};
                function go() { var cart = new Cart(); cart.add(); }
                """));

        Assert.Equal(
            [new CodeEdge("javascript:app/main.js.go", "javascript:app/main.js.Cart.add", CodeEdgeKinds.Calls, 5, CodeConfidences.Unique)],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Calls));
    }

    /// <summary>
    /// What a constructor, a method or an accessor assigns through <c>this</c> is a field of its type where the type declares nothing
    /// of that name - a field, a method, an accessor - made from its first assignment, over that statement's lines: so a chain
    /// through it, <c>this.cart.total()</c>, is read on that field, whose type nothing says, and bound by its name alone; what a
    /// compound assignment writes declares nothing, and a member a file adds to a type another file declares gives that type none.
    /// </summary>
    [Fact]
    public void AMemberABodyAssignsThroughThis_IsAFieldOfItsType_WhereTheTypeDeclaresNoneOfItsName()
    {
        const string Text = """
            export class CartView {
              constructor(cart) {
                this.cart = cart;
                this.size = 0; this.label = 'x';
              }
              get size() { return 1; } set label(text) {}
              render() { this.shown = true; this.cart = null; this.ready += 1; return this.cart.total(); }
            }
            Widget.prototype.paint = function () { this.color = 'red'; };
            """;
        var extract = Read("lib/view.js", Text);
        var view = "javascript:lib/view.js.CartView";

        Assert.Equal(
            [($"{view}.cart", 3, 3, (string?)view, "this.cart"), ($"{view}.shown", 7, 7, view, "this.shown")],
            extract.Nodes.Where(node => node.SubKind == "field").Select(node => (node.Id.Whole(), node.Start, node.End, node.Parent?.Whole(), node.Signature)));

        var linked = Link(("lib/view.js", Text), ("lib/cart.js", "export class Cart { total() { return 0; } }"));

        Assert.Equal(
            [
                new CodeEdge($"{view}.render", "javascript:lib/cart.js.Cart.total", CodeEdgeKinds.Calls, 7, CodeConfidences.Unique),
                new CodeEdge($"{view}.render", $"{view}.cart", CodeEdgeKinds.Uses, 7, CodeConfidences.Exact),
                new CodeEdge($"{view}.render", $"{view}.shown", CodeEdgeKinds.Uses, 7, CodeConfidences.Exact),
            ],
            linked.Edges.Where(edge => edge.From == $"{view}.render").Distinct().OrderBy(edge => edge.Kind, StringComparer.Ordinal).ThenBy(edge => edge.To, StringComparer.Ordinal));
    }

    /// <summary>
    /// A field a body's assignment through <c>this</c> declares is of the type the assignment's <c>new</c> makes, as a field the
    /// type's body declares so is: a chain through it is read on that type, exact, and not by its name alone among every type's.
    /// </summary>
    [Fact]
    public void AFieldAssignedThroughThis_IsOfTheTypeItsNewMakes()
    {
        var linked = Link(("lib/cart.js", """
            export class Cart {
              constructor() { this.lines = new Lines(); }
              sum() { return this.lines.sum(); }
            }
            export class Lines { sum() { return 0; } }
            """));

        Assert.Equal(
            [new CodeEdge("javascript:lib/cart.js.Cart.sum", "javascript:lib/cart.js.Lines.sum", CodeEdgeKinds.Calls, 3, CodeConfidences.Exact)],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Calls));
    }

    /// <summary>
    /// A chain on <c>this</c> through a member its type declares nowhere - one a base the repository does not hold gives, as React's
    /// <c>Component</c> gives <c>props</c>, or one the code adds at run time - says no type of what it reaches, so what is called on
    /// it is bound by its name alone, never by nothing.
    /// </summary>
    [Fact]
    public void AChainOnThis_ThroughAMemberItsTypeDeclaresNowhere_IsBoundByItsNameAlone()
    {
        var linked = Link(
            ("src/panel.js", "export class Panel extends Component {\n  save() { return this.props.onSave(); }\n}"),
            ("src/actions.js", "export function onSave() { return 1; }"));

        Assert.Equal(
            [new CodeEdge("javascript:src/panel.js.Panel.save", "javascript:src/actions.js.onSave", CodeEdgeKinds.Calls, 2, CodeConfidences.Unique)],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Calls));
    }

    /// <summary>
    /// Every form CommonJS exports by, and an AMD factory's return, written in a wrapper's body - which keeps what it declares to
    /// its own code - is its importers' by the name it exports: what it declares where it is written - a function, a class, a value,
    /// an object's function - as what it exports by a name of its own.
    /// </summary>
    [Theory]
    [InlineData("define(['require', 'exports'], function (require, exports) {\n  exports.total = function () { return 1; };\n});", "const { total } = require('../lib/m');\ntotal();", "(module)", "total", CodeEdgeKinds.Calls, CodeConfidences.Unique)]
    [InlineData("(function () {\n  module.exports.total = function () { return 1; };\n}).call(this);", "const { total } = require('../lib/m');\ntotal();", "(module)", "total", CodeEdgeKinds.Calls, CodeConfidences.Unique)]
    [InlineData("(function () {\n  exports.Cart = class Cart {};\n})();", "const { Cart } = require('../lib/m');\nnew Cart();", "(module)", "Cart", CodeEdgeKinds.Instantiates, CodeConfidences.Unique)]
    [InlineData("define(function () {\n  module.exports = class {};\n});", "const Cart = require('../lib/m');\nnew Cart();", "(module)", "default", CodeEdgeKinds.Instantiates, CodeConfidences.Exact)]
    [InlineData("(function () {\n  module.exports = function () { return 1; };\n})();", "const make = require('../lib/m');\nmake();", "(module)", "default", CodeEdgeKinds.Calls, CodeConfidences.Exact)]
    [InlineData("define(['exports'], function (exports) {\n  exports.rate = 0.2;\n});", "const { rate } = require('../lib/m');\nfunction go() { return rate; }", "go", "rate", CodeEdgeKinds.Uses, CodeConfidences.Unique)]
    [InlineData("define(function () {\n  return { total: function () { return 1; } };\n});", "const { total } = require('../lib/m');\ntotal();", "(module)", "total", CodeEdgeKinds.Calls, CodeConfidences.Unique)]
    [InlineData("define(function () {\n  return function () { return 1; };\n});", "const make = require('../lib/m');\nmake();", "(module)", "default", CodeEdgeKinds.Calls, CodeConfidences.Exact)]
    public void EveryExportAWrappersBodyWrites_IsItsImportersByTheNameItExports(string module, string importer, string from, string declared, string kind, string confidence)
    {
        var linked = Link(("lib/m.js", module), ("app/main.js", importer));

        Assert.Equal(
            [new CodeEdge($"javascript:app/main.js.{from}", $"javascript:lib/m.js.{declared}", kind, 2, confidence)],
            linked.Edges.Where(edge => edge.From.StartsWith("javascript:app/", StringComparison.Ordinal) && edge.Kind is CodeEdgeKinds.Calls or CodeEdgeKinds.Instantiates or CodeEdgeKinds.Uses));
    }

    /// <summary>
    /// AMD's own dependencies - <c>require</c>, <c>exports</c> and <c>module</c> - are the loader's objects, no module: given by
    /// the factory's dependencies or by its parameters' names alone, none is an import, and the body's <c>require('./x')</c> is
    /// an import as CommonJS's is.
    /// </summary>
    [Fact]
    public void AnAmdFactorysOwnDependencies_AreNoImports_AndItsRequireImports()
    {
        var extract = Read("app/total.js", """
            define(['require', 'exports', './cart'], function (require, exports, cart) {
              exports.total = function () { return cart.sum() + require('./tax').rate; };
            });
            """);

        Assert.Equal(
            [("./cart", (string?)"cart"), ("./tax", null)],
            extract.Imports.Where(import => import.Target is "require" or "exports" or "module" or "./cart" or "./tax").Select(import => (import.Target, import.Prefix)));
    }

    /// <summary>
    /// A wrapper's body is a closed scope of its own, under the file's, and its code outside its declarations the calls of a node
    /// of its own, named by where its function begins: its code sees what it declares and what the file does, and the file's
    /// other code - a script's top among it - does not see what it declares, so a script's global of one name is what the top's
    /// call reaches; nor does the reader name it from outside the wrapper - an export, a prototype's member written there.
    /// </summary>
    [Fact]
    public void AWrappersDeclarations_AreSeenFromItsOwnCodeAlone()
    {
        const string Wrapped = """
            (function () {
              function format(x) { return x; }
              window.show = function () { return format(1); };
              format(3);
              function Cart() {}
              Cart.prototype.add = function () { return format(4); };
            })();
            format(2);
            """;
        var linked = Link(("lib/a.js", "function format(x) { return x; }"), ("lib/b.js", Wrapped));
        var wrapper = "javascript:lib/b.js.(wrapper 1:2)";

        // The top's call is a script's global's - found by its name, as any global of another script is - and the wrapper's own a local's.
        Assert.Equal(
            [
                new CodeEdge("javascript:lib/b.js.(module)", "javascript:format", CodeEdgeKinds.Calls, 8, CodeConfidences.Unique),
                new CodeEdge(wrapper, "javascript:lib/b.js.format", CodeEdgeKinds.Calls, 3, CodeConfidences.Exact),
                new CodeEdge(wrapper, "javascript:lib/b.js.format", CodeEdgeKinds.Calls, 4, CodeConfidences.Exact),
                new CodeEdge("javascript:lib/b.js.Cart.add", "javascript:lib/b.js.format", CodeEdgeKinds.Calls, 6, CodeConfidences.Exact),
            ],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Calls).OrderBy(edge => edge.From, StringComparer.Ordinal));
        // What the wrapper declares is in its body's scope, which alone keeps it to the wrapper's code, as a member's body does: no
        // scope of its own is it kept to.
        Assert.Equal(
            ("module", (1, 7), (string?)wrapper["javascript:".Length..], (string?)null),
            (Node(Read("lib/b.js", Wrapped), wrapper).SubKind, Lines(Read("lib/b.js", Wrapped), wrapper), Node(Read("lib/b.js", Wrapped), "javascript:lib/b.js.format").Scope?.Whole(), Node(Read("lib/b.js", Wrapped), "javascript:lib/b.js.format").SeenIn?.Whole()));

        // A module's names outside the wrapper: no export of its Cart, and a prototype's member of a Cart the file's top does not declare.
        var module = Read("lib/c.js", """
            (function () { function Cart() {} Cart.prototype.add = function () {}; })();
            Cart.prototype.total = function () {};
            export { Cart };
            """);

        Assert.Equal(
            [("javascript:lib/c.js.Cart.add", "javascript:lib/c.js.Cart", (string?)null), ("javascript:lib/c.js.Cart.total", null, "Cart")],
            module.Nodes.Where(node => node.Name is "add" or "total").Select(node => (node.Id.Whole(), node.Parent?.Whole(), node.Extends)));
        Assert.Null(Node(module, "javascript:lib/c.js.Cart").Exports);
    }

    /// <summary>
    /// What a wrapper's code exports by its own name - <c>module.exports = { helper }</c> written in its body - is its importers' by
    /// that name, though the body keeps it to its own code: an importer's call reaches it, unique as a name an import shows is.
    /// </summary>
    [Fact]
    public void WhatAWrapperExportsByItsOwnName_IsItsImportersByThatName()
    {
        var linked = Link(
            ("lib/u.js", """
                (function () {
                  function helper() { return 1; }
                  function kept() { return 2; }
                  module.exports = { helper };
                })();
                """),
            ("app/use.js", """
                const { helper, kept } = require('../lib/u');
                helper();
                kept();
                """));

        Assert.Equal(
            [new CodeEdge("javascript:app/use.js.(module)", "javascript:lib/u.js.helper", CodeEdgeKinds.Calls, 2, CodeConfidences.Unique)],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Calls && edge.From.StartsWith("javascript:app/", StringComparison.Ordinal)));
    }

    /// <summary>
    /// An AMD module's factory and a function called where it is written at the file's top are read as the file's top is: what
    /// they declare is the file's, by its path, in a closed scope of the body's own under a node of its own - a module's, a script's
    /// alike - an AMD dependency an import by its parameter's name, a define given a factory a module, and the factory's return
    /// the module's export; the code around the body the file's code.
    /// </summary>
    [Fact]
    public void AWrappersBody_IsReadAsTheFilesTop()
    {
        var amd = Read("app/view.js", """
            define(['backbone', './base'], function (Backbone, Base) {
              var View = Base.extend({ render: function () { return helper(); } });
              function helper() { return 1; }
              return View;
            });
            """);

        Assert.Equal(
            [("(wrapper 1:32)", CodeNodeKinds.Member), ("View", CodeNodeKinds.Type), ("render", CodeNodeKinds.Member), ("helper", CodeNodeKinds.Member)],
            amd.Nodes.Where(node => node.Id.Whole().StartsWith("javascript:app/view.js.", StringComparison.Ordinal) && node.Name != "(module)").Select(node => (node.Name, node.Kind)));
        Assert.Equal(["default"], Node(amd, "javascript:app/view.js.View").Exports);
        Assert.Equal(["Base"], amd.Imports.Where(import => import.Target == "./base").Select(import => import.Prefix));
        Assert.Equal(["helper"], Calls(amd, "javascript:app/view.js.View.render"));

        // A define given a factory makes the file a module, its factory's body a closed scope under the module's.
        Assert.Equal(
            [new ExtractScope("app/view.js", Open: true), new ExtractScope(QualifiedName.Of("app/view.js").Under("(wrapper 1:32)"), Open: false) { Parent = QualifiedName.Of("app/view.js") }],
            amd.Scopes);

        var script = Read("lib/shop.js", """
            ;(function ($) {
              function Cart() {}
              Cart.prototype.add = function () { return $.ajax(); };
            })(jQuery);
            """);

        Assert.Equal(
            ["javascript:lib/shop.js.Cart", "javascript:lib/shop.js.Cart.constructor", "javascript:lib/shop.js.Cart.add"],
            script.Nodes.Where(node => node.Kind != CodeNodeKinds.Member || node.Parent is not null).Select(node => node.Id.Whole()));
        Assert.Equal(
            [new ExtractScope("lib/shop.js", Open: false), new ExtractScope(QualifiedName.Of("lib/shop.js").Under("(wrapper 1:3)"), Open: false) { Parent = QualifiedName.Of("lib/shop.js") }],
            script.Scopes);
        Assert.Contains(script.References, reference => reference.Name == "jQuery");
    }

    /// <summary>
    /// A JSX attribute names the rules of a class and an id by its own name alone, as the lexer keeps it beside its string: a
    /// <c>data-id</c> or a <c>data-class</c> names none, and an attribute an expression sets leaves the next one's name its own.
    /// </summary>
    [Fact]
    public void AJsxAttribute_NamesARule_ByItsOwnNameAlone()
    {
        var extract = Read("src/row.jsx", """
            export const Row = () => <div data-id="row" data-class="wide" title={tip} className="ok" id={key} class='b' aria-labelledby="main" />;
            """);

        Assert.Equal([".ok", ".b"], extract.References.Where(reference => reference.Qualifier?.Kind == CodeQualifierKinds.Selector).Select(reference => reference.Name));
    }

    private static FileExtract Read(string path, string text) => new JavaScriptExtractor().Extract(new CodeFileInput(path, text));

    private static ExtractNode Node(FileExtract extract, string id) => Assert.Single(extract.Nodes, node => node.Id == id);

    private static (int, int) Lines(FileExtract extract, string id) => (Node(extract, id).Start, Node(extract, id).End);

    private static (int?, int?) Arity(FileExtract extract, string id) => (Node(extract, id).MinArity, Node(extract, id).MaxArity);

    private static IEnumerable<string> Calls(FileExtract extract, string from)
        => extract.References.Where(reference => reference.From == from && reference.Kind == CodeEdgeKinds.Calls).Select(reference => reference.Name);

    /// <summary>The files linked together, each read by JavaScript's implementation.</summary>
    private static CodeGraphLinked Link(params (string Path, string Text)[] files)
        => CodeGraphLinker.Link([.. files.Select(file => new CodeGraphFile(file.Path, "javascript", Read(file.Path, file.Text)))]);
}
