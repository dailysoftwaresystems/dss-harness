using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;

namespace DssHarness.Tests;

/// <summary>TypeScript's implementation over the reader JavaScript shares, held to the contract's kit, and the rules of its own the kit does not name.</summary>
public sealed class TypeScriptExtractorTests : CodeGraphExtractorContractTests<TypeScriptExtractor>
{
    internal const string Shapes = """
        import { Base, Printer } from './base';
        import * as geometry from './geometry.mjs';
        import Logger, { format as fmt } from './log.cjs';
        import type { Options } from './types';

        /** A shape with an area. */
        export abstract class Shape extends Base implements Comparable<Shape> {
          private readonly cache = new Map<string, number>();

          constructor(public readonly name: string, private printer: Printer) {
            super(name);
          }

          /** The area. */
          abstract get area(): number;

          compareTo(other: Shape): number {
            return this.area - other.area;
          }

          describe(options?: Options): void {
            const printer = new Printer();
            printer.write(`${this.name}: ${fmt(this.area)}`);
            this.printer.write(this.name);
            geometry.round(this.area);
            Logger.info('described');
          }
        }

        export class Square extends Shape {
          constructor(private side: number) {
            super('square', new Printer());
          }

          override get area(): number {
            return this.side * this.side;
          }

          scale = (by: number): Square => new Square(this.side * by);

          static unit(): Square {
            return new Square(1);
          }
        }

        export interface Comparable<T> {
          compareTo(other: T): number;
        }

        export enum Kind {
          Round = 'r',
          Angular,
        }

        export type Visitor = (shape: Shape) => void;

        export function totalArea(shapes: Shape[], visit: Visitor, limit = 3): number {
          let sum = 0;
          for (const shape of shapes) {
            visit(shape);
            sum += shape.area;
            shape.describe();
          }
          const unit: Square = Square.unit();
          unit.scale(limit);
          return sum;
        }

        export namespace Shapes {
          export function make(): Shape {
            return Square.unit();
          }
        }

        export default Square;
        """;

    internal const string Base = """
        // What every shape is built on.
        export class Base {
          constructor(protected readonly label: string) {}

          hello(): void {}
        }

        export class Printer {
          write(text: string): void {
            console.log(text);
          }
        }
        """;

    internal const string Geometry = """
        export function round(value: number) {
          return Math.round(value)
        }
        """;

    internal const string Log = """
        export default class Log {
          static info(text: string) {}
        }

        export function format(value: number): string {
          return value.toFixed(2);
        }
        """;

    internal const string Types = """
        export interface Options {
          verbose?: boolean;
          (input: string): void;
          [key: string]: unknown;
          run(): Promise<void>
        }

        declare global {
          interface Window { shapes: number }
        }
        """;

    internal const string Ambient = """
        declare function greet(name: string): string;
        declare const VERSION: string;
        declare class Widget {
          constructor(id: number);
          render(): void;
        }
        declare namespace Lib {
          function make(): Widget;
        }
        """;

    internal const string App = """
        import React, { useState } from 'react';
        import { Square } from './shapes';
        import Panel from './panel';

        type Props = { title: string; onClose: () => void };

        export function App({ title, onClose }: Props) {
          const [open, setOpen] = useState(false);
          const square = new Square(2);
          return (
            <Panel title={title} onClick={() => setOpen(!open)}>
              <h1>{title.toUpperCase()}</h1>
              {open && <button onClick={onClose}>Close {square.area}</button>}
            </Panel>
          );
        }

        export const identity = <T,>(value: T): T => value;
        """;

    internal const string Panel = """
        export default function Panel(props: { title: string }) {
          return <div className="panel">{props.title}</div>;
        }
        """;

    internal const string ShapesTest = """
        import { describe, it, expect } from 'vitest';
        import Square, { totalArea } from '../src/shapes';

        describe('Square', () => {
          it('has an area', () => {
            const square = new Square(2);
            expect(square.area).toBe(4);
            square.describe();
          });
        });

        test('totals', () => {
          totalArea([], () => {});
        });
        """;

    /// <inheritdoc/>
    public override IReadOnlyList<CodeGraphFixture> Fixtures { get; } =
    [
        new("src/shapes.ts", Shapes)
        {
            Nodes =
            [
                new("typescript:src/shapes.ts.Shape", CodeNodeKinds.Type, (7, 28)),
                new("typescript:src/shapes.ts.Shape.cache", CodeNodeKinds.Member, (8, 8)),
                new("typescript:src/shapes.ts.Shape.constructor", CodeNodeKinds.Member, (10, 12)),
                new("typescript:src/shapes.ts.Shape.printer", CodeNodeKinds.Member, (10, 10)),
                new("typescript:src/shapes.ts.Shape.area", CodeNodeKinds.Member, (15, 15)),
                new("typescript:src/shapes.ts.Square.area", CodeNodeKinds.Member, (35, 37)),
                new("typescript:src/shapes.ts.Square.scale", CodeNodeKinds.Member, (39, 39)),
                new("typescript:src/shapes.ts.Comparable.compareTo", CodeNodeKinds.Member, (47, 47)),
                new("typescript:src/shapes.ts.Kind.Angular", CodeNodeKinds.Member, (52, 52)),
                new("typescript:src/shapes.ts.Visitor", CodeNodeKinds.Type, (55, 55)),
                new("typescript:src/shapes.ts.totalArea", CodeNodeKinds.Member, (57, 67)),
                new("typescript:src/shapes.ts.Shapes", CodeNodeKinds.Namespace, (69, 73)),
                new("typescript:src/shapes.ts.Shapes.make", CodeNodeKinds.Member, (70, 72)),
            ],
            Edges =
            [
                new("file:src/shapes.ts", "file:src/base.ts", CodeEdgeKinds.Imports, 1, CodeConfidences.Exact),
                new("file:src/shapes.ts", "file:src/geometry.mts", CodeEdgeKinds.Imports, 2, CodeConfidences.Exact),
                new("file:src/shapes.ts", "file:src/log.cts", CodeEdgeKinds.Imports, 3, CodeConfidences.Exact),
                new("file:src/shapes.ts", "file:src/types.ts", CodeEdgeKinds.Imports, 4, CodeConfidences.Exact),
                new("typescript:src/shapes.ts.Shape", "typescript:src/base.ts.Base", CodeEdgeKinds.Inherits, 7, CodeConfidences.Unique),
                new("typescript:src/shapes.ts.Shape", "typescript:src/shapes.ts.Comparable", CodeEdgeKinds.Implements, 7, CodeConfidences.Exact),
                new("typescript:src/shapes.ts.Square", "typescript:src/shapes.ts.Shape", CodeEdgeKinds.Inherits, 30, CodeConfidences.Exact),
                new("typescript:src/shapes.ts.Shape.printer", "typescript:src/base.ts.Printer", CodeEdgeKinds.References, 10, CodeConfidences.Unique),
                new("typescript:src/shapes.ts.Shape.describe", "typescript:src/types.ts.Options", CodeEdgeKinds.References, 21, CodeConfidences.Unique),
                new("typescript:src/shapes.ts.Shape.constructor", "typescript:src/base.ts.Base.constructor", CodeEdgeKinds.Calls, 11, CodeConfidences.Exact),
                new("typescript:src/shapes.ts.Shape.describe", "typescript:src/base.ts.Printer.write", CodeEdgeKinds.Calls, 23, CodeConfidences.Exact),
                new("typescript:src/shapes.ts.Shape.describe", "typescript:src/log.cts.format", CodeEdgeKinds.Calls, 23, CodeConfidences.Exact),
                new("typescript:src/shapes.ts.Shape.describe", "typescript:src/base.ts.Printer.write", CodeEdgeKinds.Calls, 24, CodeConfidences.Exact),
                new("typescript:src/shapes.ts.Shape.describe", "typescript:src/geometry.mts.round", CodeEdgeKinds.Calls, 25, CodeConfidences.Exact),
                new("typescript:src/shapes.ts.Shape.describe", "typescript:src/log.cts.Log.info", CodeEdgeKinds.Calls, 26, CodeConfidences.Exact),
                new("typescript:src/shapes.ts.Square.area", "typescript:src/shapes.ts.Shape.area", CodeEdgeKinds.Overrides, 35, CodeConfidences.Exact),
                new("typescript:src/shapes.ts.Square.constructor", "typescript:src/shapes.ts.Shape.constructor", CodeEdgeKinds.Calls, 32, CodeConfidences.Exact),
                new("typescript:src/shapes.ts.totalArea", "typescript:src/shapes.ts.Visitor", CodeEdgeKinds.References, 57, CodeConfidences.Exact),
                new("typescript:src/shapes.ts.totalArea", "typescript:src/shapes.ts.Square.unit", CodeEdgeKinds.Calls, 64, CodeConfidences.Exact),
                new("typescript:src/shapes.ts.totalArea", "typescript:src/shapes.ts.Square.scale", CodeEdgeKinds.Calls, 65, CodeConfidences.Exact),
                new("typescript:src/shapes.ts.Comparable.compareTo", "typescript:src/shapes.ts.Shape.compareTo", CodeEdgeKinds.Dispatches, 17, CodeConfidences.Exact),
            ],
        },
        new("src/base.ts", Base)
        {
            Nodes = [new("typescript:src/base.ts.Base", CodeNodeKinds.Type, (2, 6)), new("typescript:src/base.ts.Base.label", CodeNodeKinds.Member, (3, 3))],
        },
        new("src/geometry.mts", Geometry)
        {
            Nodes = [new("typescript:src/geometry.mts.round", CodeNodeKinds.Member, (1, 3))],
        },
        new("src/log.cts", Log)
        {
            Nodes = [new("typescript:src/log.cts.Log", CodeNodeKinds.Type, (1, 3)), new("typescript:src/log.cts.format", CodeNodeKinds.Member, (5, 7))],
        },
        new("src/types.ts", Types)
        {
            Nodes =
            [
                new("typescript:src/types.ts.Options", CodeNodeKinds.Type, (1, 6)),
                new("typescript:src/types.ts.Options.run", CodeNodeKinds.Member, (5, 5)),
                new("typescript:Window", CodeNodeKinds.Type, (9, 9)),
            ],
        },
        new("src/ambient.d.ts", Ambient)
        {
            Nodes =
            [
                new("typescript:greet", CodeNodeKinds.Member, (1, 1)),
                new("typescript:Widget.render", CodeNodeKinds.Member, (5, 5)),
                new("typescript:Lib.make", CodeNodeKinds.Member, (8, 8)),
            ],
            Edges = [new("typescript:Lib.make", "typescript:Widget", CodeEdgeKinds.References, 8, CodeConfidences.Unique)],
        },
        new("src/app.tsx", App)
        {
            Nodes =
            [
                new("typescript:src/app.tsx.Props", CodeNodeKinds.Type, (5, 5)),
                new("typescript:src/app.tsx.App", CodeNodeKinds.Member, (7, 16)),
                new("typescript:src/app.tsx.identity", CodeNodeKinds.Member, (18, 18)),
            ],
            Edges =
            [
                new("file:src/app.tsx", "file:src/shapes.ts", CodeEdgeKinds.Imports, 2, CodeConfidences.Exact),
                new("typescript:src/app.tsx.App", "typescript:src/app.tsx.Props", CodeEdgeKinds.References, 7, CodeConfidences.Exact),
                new("typescript:src/app.tsx.App", "typescript:src/shapes.ts.Square", CodeEdgeKinds.Instantiates, 9, CodeConfidences.Unique),
                new("typescript:src/app.tsx.App", "typescript:src/panel.tsx.Panel", CodeEdgeKinds.Calls, 11, CodeConfidences.Exact),
            ],
        },
        new("src/panel.tsx", Panel)
        {
            Nodes = [new("typescript:src/panel.tsx.Panel", CodeNodeKinds.Member, (1, 3))],
        },
        new("test/shapes.test.ts", ShapesTest)
        {
            Nodes =
            [
                new("typescript:test/shapes.test.ts.'Square'", CodeNodeKinds.Member, (4, 10)),
                new("typescript:test/shapes.test.ts.'Square'.'has an area'", CodeNodeKinds.Member, (5, 9)),
                new("typescript:test/shapes.test.ts.'totals'", CodeNodeKinds.Member, (12, 14)),
            ],
            Edges =
            [
                new("typescript:test/shapes.test.ts.'Square'.'has an area'", "typescript:src/shapes.ts.Square", CodeEdgeKinds.Instantiates, 6, CodeConfidences.Exact),
                new("typescript:test/shapes.test.ts.'Square'.'has an area'", "typescript:src/shapes.ts.Shape.describe", CodeEdgeKinds.Tests, 8, CodeConfidences.Exact),
                new("typescript:test/shapes.test.ts.'totals'", "typescript:src/shapes.ts.totalArea", CodeEdgeKinds.Tests, 13, CodeConfidences.Unique),
            ],
        },
    ];

    /// <summary>
    /// Types never throw the reading off: a type parameter's default object, a conditional type, a function type, an
    /// object type a function returns, a predicate, a mapped type, a type an import names - each declaration after them
    /// read where it is, to its own last line.
    /// </summary>
    [Fact]
    public void Types_NeverThrowTheReadingOff()
    {
        var extract = Read("src/t.ts", """
            export class Box<T extends object = {}> {
              open(): { a: string } {
                return { a: '' };
              }
            }
            export function pick<K extends keyof T, T>(value: T, key: K): T[K] extends string ? `${T[K]}` : never {
              return value[key] as never;
            }
            export function guard(value: unknown): asserts value is string {}
            export type Mapped<T> = { readonly [P in keyof T]?: T[P] };
            export type Lazy = typeof import('./lazy');
            export const handle: (event: Event) => void = (event) => {
              done(event);
            };
            export function last() {}
            """);

        Assert.Equal(
            [("Box", 1, 5), ("open", 2, 4), ("pick", 6, 8), ("guard", 9, 9), ("Mapped", 10, 10), ("Lazy", 11, 11), ("handle", 12, 14), ("last", 15, 15)],
            extract.Nodes.Select(node => (node.Name, node.Start, node.End)));
        Assert.Equal(["done"], extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Calls).Select(reference => reference.Name));
    }

    /// <summary>
    /// Every kind of TypeScript's declaration is a node of its sub-kind and name: an interface - its parts in a file one -
    /// an enum and a const one, a type alias, a namespace a node for each name of it, an ambient module by its string,
    /// what declare global declares at no scope, an abstract class and its abstract members, each overload a prototype
    /// beside its body's twin, a parameter property a field, a decorated member marked; a call, a construct and an index
    /// signature none.
    /// </summary>
    [Fact]
    public void EveryKindOfDeclaration_IsANodeOfItsSubKind_NamedAsTypeScriptCallsIt()
    {
        var extract = Read("src/k.ts", """
            export interface Shape { area(): number }
            export interface Shape { name: string; (x: number): void; new (x: number): Shape; [key: string]: unknown }
            export enum Color { Red, Green = 'g' }
            export const enum Flag { A = 1 << 0 }
            export type Pair = [number, number];
            export namespace A.B { export const deep = 1; }
            declare module 'lib' { export function libFn(): void; }
            declare global { interface Window { app: number } }
            export abstract class Animal {
              abstract speak(): void;
              protected abstract readonly legs: number;
              constructor(private readonly id: string) {}
              static { register(); }
              @Watch('x') changed(): void {}
            }
            export function over(a: string): void;
            export function over(a: number): void;
            export function over(a: any) {}
            """);

        Assert.Equal(
            [
                ("typescript:src/k.ts.Shape", "interface"), ("typescript:src/k.ts.Shape.area", "method"), ("typescript:src/k.ts.Shape.name", "property"),
                ("typescript:src/k.ts.Color", "enum"), ("typescript:src/k.ts.Color.Red", "constant"), ("typescript:src/k.ts.Color.Green", "constant"),
                ("typescript:src/k.ts.Flag", "enum"), ("typescript:src/k.ts.Flag.A", "constant"), ("typescript:src/k.ts.Pair", "type"),
                ("typescript:src/k.ts.A", "namespace"), ("typescript:src/k.ts.A.B", "namespace"), ("typescript:src/k.ts.A.B.deep", "constant"),
                ("typescript:src/k.ts.'lib'", "module"), ("typescript:src/k.ts.'lib'.libFn", "function"), ("typescript:Window", "interface"),
                ("typescript:Window.app", "property"), ("typescript:src/k.ts.Animal", "class"), ("typescript:src/k.ts.Animal.speak", "method"),
                ("typescript:src/k.ts.Animal.legs", "field"), ("typescript:src/k.ts.Animal.constructor", "constructor"), ("typescript:src/k.ts.Animal.id", "field"),
                ("typescript:src/k.ts.Animal.changed", "method"), ("typescript:src/k.ts.over~2", "function"), ("typescript:src/k.ts.over~3", "function"),
                ("typescript:src/k.ts.over", "function"),
            ],
            extract.Nodes.Select(node => (node.Id, node.SubKind!)));
        Assert.Equal((1, 2), (Node(extract, "typescript:src/k.ts.Shape").Start, Node(extract, "typescript:src/k.ts.Shape").End));
        Assert.Equal([true, true, false], extract.Nodes.Where(node => node.Name == "over").Select(node => node.Prototype));
        Assert.True(Node(extract, "typescript:src/k.ts.Animal.speak").Prototype);
        Assert.Equal("src/k.ts.Animal", Node(extract, "typescript:src/k.ts.Animal.id").SeenIn);
        Assert.Null(Node(extract, "typescript:src/k.ts.'lib'").SeenIn);
        Assert.Equal(["const"], Node(extract, "typescript:src/k.ts.Flag").Modifiers.Except(["export"]));
        Assert.Equal(["@Watch"], Node(extract, "typescript:src/k.ts.Animal.changed").Modifiers);
        Assert.Equal(
            [("typescript:src/k.ts.Animal.changed", "Watch"), ("typescript:src/k.ts.Animal", "register")],
            extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Calls).Select(reference => (reference.From, reference.Name)));
    }

    /// <summary>A TypeScript callable takes from its required parameters to all of them - none most where a rest one takes any - and its this parameter is none.</summary>
    [Fact]
    public void ATypeScriptCallable_TakesItsRequiredParameters_ToAllOfThem()
    {
        var extract = Read("src/p.ts", """
            export class P {
              constructor(a: number, b = 1, c?: string) {}
              many(this: P, first: string, ...rest: string[]) {}
            }
            export const arrow = (x: number, y?: number) => x;
            """);

        Assert.Equal((1, (int?)3), Arity(extract, "typescript:src/p.ts.P.constructor"));
        Assert.Equal((1, (int?)null), Arity(extract, "typescript:src/p.ts.P.many"));
        Assert.Equal((1, (int?)2), Arity(extract, "typescript:src/p.ts.arrow"));
    }

    /// <summary>
    /// What a declaration's types name are its references: an annotation's, a return type's, a type argument's, an as's and
    /// a satisfies' - but the language's own types, the type parameters in force, an object type's keys, a value typeof
    /// takes and a type infer binds; an interface's extends is an inherits edge, a class's implements an implements one.
    /// </summary>
    [Fact]
    public void WhatATypeNames_IsAReference_ButTheLanguagesOwnAndWhatItBinds()
    {
        var extract = Read("src/r.ts", """
            export interface Named extends Base, Other<Item> {}
            export class Store<T> implements Named {
              items: Map<string, Item> = new Map();
              find(key: string, fallback: T): Item | undefined {
                return lookup(key) as Item satisfies Shape;
              }
            }
            export type Unwrap<T> = T extends Promise<infer U> ? U : { value: T; other: typeof config };
            """);

        Assert.Equal(
            [
                (CodeEdgeKinds.Inherits, "Base"), (CodeEdgeKinds.Inherits, "Other"), (CodeEdgeKinds.References, "Item"), (CodeEdgeKinds.Implements, "Named"),
                (CodeEdgeKinds.References, "Item"), (CodeEdgeKinds.References, "Item"), (CodeEdgeKinds.References, "Item"), (CodeEdgeKinds.References, "Shape"),
            ],
            extract.References.Where(reference => reference.Kind != CodeEdgeKinds.Calls).Select(reference => (reference.Kind, reference.Name)));
    }

    /// <summary>
    /// A receiver whose type the file states is the call's qualifier - a parameter's annotation, a local's, a field's
    /// type through the linker - one typed any marks its call dynamic, and a call through a field or a parameter of a
    /// function type is dynamic too.
    /// </summary>
    [Fact]
    public void AReceiverWhoseTypeTheFileStates_QualifiesItsCall()
    {
        var extract = Read("src/q.ts", """
            export class Q {
              onChange: (value: string) => void;
              constructor(private service: Service) {}
              go(given: Shape, loose: any, run: () => void) {
                given.area();
                const local: Shape = make();
                local.scale();
                this.service.read();
                loose.whatever();
                this.onChange('x');
                run();
              }
            }
            """);

        Assert.Equal(
            [("area", "type", "Shape"), ("make", null, null), ("scale", "type", "Shape"), ("read", "value", "service"), ("whatever", "value", null)],
            extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Calls).Select(reference => (reference.Name, reference.Qualifier?.Kind, reference.Qualifier?.Name)));
        Assert.Equal(
            ["a call on the any value loose", "a call through the function value onChange", "a call through the function value run"],
            extract.Dynamic.Select(dynamic => dynamic.Why));

        var linked = Link(("src/q.ts", "export class Service { read() {} }\nexport class Q {\n  constructor(private service: Service) {}\n  go() { this.service.read(); }\n}"));

        Assert.Contains(new CodeEdge("typescript:src/q.ts.Q.go", "typescript:src/q.ts.Service.read", CodeEdgeKinds.Calls, 4, CodeConfidences.Exact), linked.Edges);
    }

    /// <summary>A declaration file's every member is a prototype, its declarations the repository's where it imports and exports nothing; it is no generated file by that alone.</summary>
    [Fact]
    public void ADeclarationFilesMembers_AreEachAPrototype()
    {
        var extract = Read("types/lib.d.ts", "declare function f(): void;\ndeclare class C { m(): void; field: number; }\ndeclare const v: string;\nfunction g() { return 1; }\nconst h = () => 1;");

        Assert.All(extract.Nodes.Where(node => node.Kind == CodeNodeKinds.Member), node => Assert.True(node.Prototype));
        Assert.Equal(["typescript:f", "typescript:C", "typescript:C.m", "typescript:C.field", "typescript:v", "typescript:g", "typescript:h"], extract.Nodes.Select(node => node.Id));
        Assert.Null(extract.Generated);
    }

    /// <summary>
    /// TypeScript's own imports: import type as any import, import x = require(...) a namespace's, import x = A.B an alias
    /// of a scope, and a specifier written with .js tried as its source's .ts first, then .tsx and .d.ts, then as written.
    /// </summary>
    [Fact]
    public void TypeScriptsImports_AreImportsAsJavaScriptsAre()
    {
        var extract = Read("src/i.ts", """
            import type { Shape } from './shape.js';
            import fs = require('fs');
            import Inner = Outer.Inner;
            export type { Shape as Kept } from './kept';
            """);

        Assert.Equal(
            [
                (CodeImportForms.Relative, "./shape.ts", null, "Shape", false), (CodeImportForms.Relative, "./shape.tsx", null, "Shape", false),
                (CodeImportForms.Relative, "./shape.d.ts", null, "Shape", false), (CodeImportForms.Relative, "./shape.js", null, "Shape", false),
                (CodeImportForms.Address, "fs", "fs", null, false), (CodeImportForms.Scope, "Outer.Inner", "Inner", null, false),
            ],
            extract.Imports.Where(import => !import.Export && import.Form != CodeImportForms.Address || import.Target == "fs")
                .Select(import => (import.Form, import.Target, import.Prefix, import.Show is null ? null : string.Join(',', import.Show), import.Export)));
        Assert.All(extract.Imports.Where(import => import.Target.StartsWith("./kept", StringComparison.Ordinal)), import => Assert.True(import.Export));
        Assert.Equal(
            ["./q", "./q.ts", "./q.tsx", "./q.d.ts", "./q.js", "./q.jsx", "./q/index.ts", "./q/index.tsx", "./q/index.d.ts", "./q/index.js", "./q/index.jsx"],
            Read("src/j.ts", "import { q } from './q';").Imports.Select(import => import.Target));
    }

    /// <summary>In a .tsx file a generic arrow's type parameters open no element, and in a .ts file a type assertion opens none: the reading goes on.</summary>
    [Fact]
    public void AnAngleThatIsNoElement_LeavesTheReadingWhole()
    {
        var tsx = Read("src/g.tsx", "export const id = <T,>(value: T) => value;\nexport const wide = <T extends object>(value: T) => <div>{show(value)}</div>;\nexport function after() {}");
        var ts = Read("src/g.ts", "export const cast = <Shape>value;\nexport function after() { go(); }");

        Assert.Equal([("id", "function"), ("wide", "function"), ("after", "function")], tsx.Nodes.Select(node => (node.Name, node.SubKind!)));
        Assert.Equal(["show"], tsx.References.Select(reference => reference.Name));
        Assert.Equal([("cast", "constant"), ("after", "function")], ts.Nodes.Select(node => (node.Name, node.SubKind!)));
        Assert.Equal(["go"], ts.References.Select(reference => reference.Name));
    }

    /// <summary>
    /// In a .tsx file the type arguments a component's tag writes after its name - <c>&lt;Table&lt;User&gt;</c> - are the
    /// tag's own, none of its <c>&gt;</c> the tag's end: the element ends where its tag does, self-closing or closed by its
    /// own tag, and the reading goes on past it.
    /// </summary>
    [Fact]
    public void AComponentsTypeArguments_AreItsTagsOwn()
    {
        foreach (var element in (string[])["<Table<User> rows={rows} />", "<Table<Map<string, (x: User) => void>>>{rows.map(row => draw(row))}</Table>"])
        {
            var extract = Read("src/t.tsx", $"export function List() {{ return {element}; }}\nexport function Other() {{ helper(); }}\n");

            Assert.Equal([("List", 1, 1), ("Other", 2, 2)], extract.Nodes.Select(node => (node.Name, node.Start, node.End)));
            Assert.Equal(
                element.Contains("draw", StringComparison.Ordinal) ? ["List:map", "List:draw", "List:Table", "Other:helper"] : ["List:Table", "Other:helper"],
                extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Calls).Select(reference => $"{reference.From[(reference.From.LastIndexOf('.') + 1)..]}:{reference.Name}"));
        }
    }

    /// <summary>
    /// A line break ends a member or a statement where TypeScript's would, whatever word ends the line - a type's void,
    /// as const, a member's name a keyword spells - and a type's member before an index or a call signature on the next
    /// line, a list of type arguments one member however many commas it holds.
    /// </summary>
    [Fact]
    public void ALineBreak_EndsAMemberOrAStatement_WhereTypeScriptsWould()
    {
        var extract = Read("src/b.ts", """
            export interface P {
              onClose: () => void
              disabled?: boolean
              names: Map<string, number>
              [key: string]: unknown
              (x: number): void
            }
            export type Cb = () => void
            export function register(cb: Cb) {}
            export const A = ['a'] as const
            export class C {
              cb: () => void
              kind = Base.type
              run() {}
              private ok: boolean = this.n < 3
              stop() {}
              [KEY] = new Map<string, number>()
              last = 1
            }
            """);

        Assert.Equal(
            [
                ("P", 1, 7), ("onClose", 2, 2), ("disabled", 3, 3), ("names", 4, 4), ("Cb", 8, 8), ("register", 9, 9), ("A", 10, 10), ("C", 11, 19), ("cb", 12, 12),
                ("kind", 13, 13), ("run", 14, 14), ("ok", 15, 15), ("stop", 16, 16), ("last", 18, 18),
            ],
            extract.Nodes.Select(node => (node.Name, node.Start, node.End)));
    }

    /// <summary>A default export of every kind of TypeScript's declaration is exported by the name default, its own name the module's.</summary>
    [Fact]
    public void ADefaultExport_IsExportedByDefault_ItsOwnNameTheModules()
    {
        var extract = Read("src/o.ts", "export default interface Options { a: string }");

        Assert.Equal(("src/o.ts", "default"), (Node(extract, "typescript:src/o.ts.Options").SeenIn, string.Join(',', Node(extract, "typescript:src/o.ts.Options").Exports ?? [])));
    }

    /// <summary>
    /// A call's type arguments may hold an object type; a method named call or apply of a type the file says is that
    /// type's; a receiver cast to any marks its call dynamic; a generic function type's own type parameters are no
    /// references; and a call that spreads its arguments reaches a callable of any count.
    /// </summary>
    [Fact]
    public void ACallsTypeArguments_ACastToAny_AndAMethodNamedApply_AreReadAsTypeScriptMeansThem()
    {
        var extract = Read("src/c.ts", """
            export class Patch { apply(doc: Doc) {} }
            export type Render = <T>(item: T) => T;
            export type Mapper = { map<U>(f: (x: Item) => U): U[]; make: new <V>(x: V) => V };
            export function go(p: Patch, pair: [number, number]) {
              api.post<{ id: number }>('/x', body);
              p.apply(doc);
              (x as any).m();
              take(...pair);
            }
            export function take(a: number, b: number) {}
            """);

        Assert.Equal(
            [("post", "value", "api"), ("apply", "type", "Patch"), ("m", "value", null), ("take", null, null)],
            extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Calls).Select(reference => (reference.Name, reference.Qualifier?.Kind, reference.Qualifier?.Name)));
        Assert.Equal(["a call on the any value x as any"], extract.Dynamic.Select(dynamic => dynamic.Why));
        Assert.DoesNotContain(extract.References, reference => reference.Name is "T" or "U" or "V");
        Assert.Contains(extract.References, reference => reference is { Name: "Item", Kind: CodeEdgeKinds.References });

        var linked = Link(("src/c.ts", "export function take(a: number, b: number) {}\nexport function go(pair: [number, number]) { take(...pair); }"));

        Assert.Contains(new CodeEdge("typescript:src/c.ts.go", "typescript:src/c.ts.take", CodeEdgeKinds.Calls, 2, CodeConfidences.Exact), linked.Edges);
    }

    private static FileExtract Read(string path, string text) => new TypeScriptExtractor().Extract(new CodeFileInput(path, text));

    private static ExtractNode Node(FileExtract extract, string id) => Assert.Single(extract.Nodes, node => node.Id == id);

    private static (int?, int?) Arity(FileExtract extract, string id) => (Node(extract, id).MinArity, Node(extract, id).MaxArity);

    /// <summary>The files linked together, each read by TypeScript's implementation.</summary>
    private static CodeGraphLinked Link(params (string Path, string Text)[] files)
        => CodeGraphLinker.Link([.. files.Select(file => new CodeGraphFile(file.Path, "typescript", Read(file.Path, file.Text)))]);
}
