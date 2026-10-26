using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;
using DssHarness.Core.Codebase.Languages.Scanning;

namespace DssHarness.Tests;

/// <summary>C++'s implementation over the structural scanner, held to the contract's kit, and the rules of its own the kit does not name.</summary>
public sealed class CppExtractorTests : CodeGraphExtractorContractTests<CppExtractor>
{
    internal const string ShapeHeader = """
        #pragma once
        #include <string>
        #include <memory>

        namespace shapes {

        /// A figure on a plane.
        class Shape {
        public:
            explicit Shape(std::string name);
            virtual ~Shape() = default;
            virtual double area() const = 0;
            const std::string& name() const { return name_; }
            void draw(class Canvas& canvas, int times = 1);

        protected:
            static void log(const std::string& text);

        private:
            std::string name_;
        };

        class Circle final : public Shape {
        public:
            explicit Circle(double radius);
            double area() const override;

        private:
            double radius_;
        };

        }  // namespace shapes
        """;

    internal const string Shape = """
        #include "shapes/shape.hpp"
        #include "canvas.hh"

        namespace shapes {

        Shape::Shape(std::string name) : name_(std::move(name)) {}

        void Shape::draw(Canvas& canvas, int times)
        {
            canvas.plot(times);
            log(name());
            this->log("again");
        }

        void Shape::log(const std::string& text)
        {
            auto raw = R"x(not ) a "call(" here)x";
            std::printf("%s\n", text.c_str());
        }

        Circle::Circle(double radius) : Shape("circle"), radius_{radius} {}

        double Circle::area() const { return square(radius_) * 3.14; }

        }  // namespace shapes
        """;

    internal const string CanvasHeader = """
        namespace shapes {
        class Canvas {
        public:
            void plot(int count);
            template <typename T> T scaled(T value) const { return value * 2; }
        };
        }
        """;

    internal const string Canvas = """
        #include "canvas.hh"
        #include <functional>

        namespace shapes {

        void Canvas::plot(int count)
        {
            std::function<void(int)> each = [](int) {};
            each(count);
            Canvas* other = new Canvas();
            other->plot(count - 1);
            Canvas copy;
            copy.scaled(count);
            hidden(count);
        }

        }
        """;

    internal const string Math = """
        #include "canvas.hh"

        double square(double value) { return value * value; }

        namespace {
        int hidden(int x) { return x; }
        }

        static int counter = 0;
        """;

    internal const string Main = """
        #include "shapes/shape.hpp"
        #include "shapes/legacy.h"
        using namespace shapes;

        #if defined(FAST)
        static int mode() { return 1; }
        #elif defined(SLOW)
        static int mode() { return 2; }
        #endif

        int main(int argc, char** argv)
        {
            Circle circle(2.0);
            circle.draw(*new Canvas(), argc);
            std::unique_ptr<Shape> owned = std::make_unique<Circle>(1.0);
            owned->area();
            return legacy::answer() + detail::twice(1) + clamp_to(argc, mode());
        }

        TEST(Shapes, Area) { EXPECT_EQ(1, 1); }
        """;

    internal const string Detail = """
        namespace shapes::detail {
        template <class T>
        T twice(T value) { return value + value; }
        }
        """;

    internal const string Inline = """
        inline int clamp_to(int value, int most) { return value > most ? most : value; }
        """;

    internal const string Box = """
        namespace shapes {
        template <class T> struct Box { T value; enum class Fill { Empty, Full }; };
        }
        """;

    internal const string Legacy = """
        namespace legacy { int answer(); }
        """;

    /// <summary>
    /// A call is qualified by its receiver where the file says what it is: <c>this</c>; the type a parameter or a variable
    /// is declared with - through a pointer, a smart pointer, <c>new</c> or a type's name after <c>auto</c>; else the value
    /// itself, by its chain of names, or a value of no said type; a scope's or a type's name before <c>::</c> a named one.
    /// </summary>
    [Fact]
    public void ACall_IsQualifiedByWhatItsReceiverIs_WhereTheFileSays()
    {
        var extract = Read("car.cc", """
            #include <memory>
            namespace app {
            void Car::drive(Engine& engine, Engine* spare)
            {
                engine.start();
                spare->start();
                motor.start();
                this->motor.start();
                auto made = new Engine();
                made->start();
                auto copy = Engine();
                copy.start();
                std::unique_ptr<Engine> owned;
                owned->start();
                lookup().start();
                other.thing.start();
                Engine::make();
                app::helper();
                this->stop();
            }
            }
            """);

        Assert.Equal(
            [
                (5, "type", "Engine"), (6, "type", "Engine"), (7, "value", "motor"), (8, "value", "motor"), (10, "type", "Engine"), (12, "type", "Engine"),
                (14, "type", "Engine"), (15, "value", null), (16, "value", "other.thing"), (17, "named", "Engine"), (18, "named", "app"), (19, "this", null),
            ],
            extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Calls && reference.Name is "start" or "make" or "helper" or "stop")
                .Select(reference => (reference.Line, reference.Qualifier?.Kind, reference.Qualifier?.Name)));
    }

    /// <summary>A call's arity is its commas at its own depth: none inside a nested group, a lambda, or a type's arguments a call or a qualified name follows; a comparison's are commas.</summary>
    [Fact]
    public void ACallsArity_CountsOnlyItsOwnCommas()
    {
        var extract = Read("a.cc", """
            void f() {
                g(std::pair<int, int>(1, 2), x);
                h<int, long>(1);
                k(a < b, c > d);
                m([](int a, int b) { return a; }, 3);
            }
            """);

        Assert.Equal(
            [("g", 2), ("pair", 2), ("h", 1), ("k", 2), ("m", 2)],
            extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Calls).Select(reference => (reference.Name, reference.Arity ?? -1)));
        Assert.Empty(extract.Dynamic);
    }

    /// <summary>A member's id is its type's, its name and its parameters' types - a const member marked - so overloads, operators, constructors and the destructor are each their own, with their kind and arity.</summary>
    [Fact]
    public void AMembersId_IsItsTypes_ItsName_AndItsParametersTypes()
    {
        var extract = Read("v.hpp", """
            struct V {
                V();
                V(int x, int y = 0);
                ~V();
                int size() const;
                int size();
                V& operator+=(const V& other);
                V& operator=(const V&) = default;
                bool operator<(const V& other) const;
                V& operator<<=(int n) { return *this; }
                explicit operator bool() const;
                int operator()(int) const;
            };
            """);

        Assert.Equal(
            [
                ("cpp:V.V()", "constructor", 0, 0), ("cpp:V.V(int,int)", "constructor", 1, 2), ("cpp:V.~V()", "destructor", 0, 0),
                ("cpp:V.size()const", "method", 0, 0), ("cpp:V.size()", "method", 0, 0), ("cpp:V.operator+=(const V&)", "operator", 1, 1),
                ("cpp:V.operator=(const V&)", "operator", 1, 1), ("cpp:V.operator<(const V&)const", "operator", 1, 1), ("cpp:V.operator<<=(int)", "operator", 1, 1),
                ("cpp:V.operator bool()const", "operator", 0, 0), ("cpp:V.operator()(int)const", "operator", 1, 1),
            ],
            extract.Nodes.Where(node => node.Kind == CodeNodeKinds.Member).Select(node => (node.Id, node.SubKind, node.MinArity ?? -1, node.MaxArity ?? -1)));
        Assert.Equal(
            ["cpp:V.operator=(const V&)", "cpp:V.operator<<=(int)"],
            extract.Nodes.Where(node => node.Kind == CodeNodeKinds.Member && !node.Prototype).Select(node => node.Id));
    }

    /// <summary>
    /// What an anonymous namespace declares, and what a namespace's <c>static</c> does, is its file's alone - its path in
    /// its id - and a type there keeps its members in its own scope, closed; a member defined outside the type is in it.
    /// </summary>
    [Fact]
    public void WhatAnAnonymousNamespaceDeclares_IsItsFilesAlone()
    {
        var extract = Read("x.cc", """
            namespace {
            struct Local { void run(); };
            void Local::run() {}
            int helper() { return 0; }
            }
            static int count;
            namespace app { static void tick() {} }
            """);

        Assert.Equal("file:x.cc", Node(extract, "cpp:x.cc:Local").Scope);
        Assert.Equal("Local", Node(extract, "cpp:x.cc:Local.run()").Scope);
        Assert.False(Node(extract, "cpp:x.cc:Local.run()").Prototype);
        Assert.True(Node(extract, "cpp:x.cc:Local.run()~2").Prototype);
        Assert.Equal("file:x.cc", Node(extract, "cpp:x.cc:helper()").Scope);
        Assert.Equal("file:x.cc", Node(extract, "cpp:x.cc:count").Scope);
        Assert.Equal("file:x.cc", Node(extract, "cpp:x.cc:app.tick()").Scope);
        Assert.Contains(extract.Scopes, scope => scope is { Name: "Local", Open: false });
        Assert.Contains(extract.Scopes, scope => scope is { Name: "file:x.cc", Open: false });
    }

    /// <summary>A constructor's initializers call what their arguments call, and the members and bases they name are no calls.</summary>
    [Fact]
    public void AConstructorsInitializers_CallWhatTheirArgumentsCall()
    {
        var extract = Read("a.cc", "A::A(int x) : Base(x), b_(make(x)), c_{other(x)} { body(); }\n");

        Assert.Equal(["make", "other", "body"], extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Calls).Select(reference => reference.Name));
        Assert.Equal("constructor", Node(extract, "cpp:A.A(int)").SubKind);
    }

    /// <summary>A call through a <c>std::function</c>, a function pointer or a pointer to a member is marked dynamic and makes no reference.</summary>
    [Fact]
    public void ACallThroughAFunctionObjectOrAPointer_IsDynamic()
    {
        var extract = Read("r.cc", """
            #include <functional>
            void run(std::function<void()> done, void (*cb)(int), int (Obj::*pm)(int), Obj* o)
            {
                done();
                cb(1);
                (o->*pm)(2);
                real();
            }
            """);

        Assert.Equal(
            [(4, "a call through the function pointer done"), (5, "a call through the function pointer cb"), (6, "a call through a pointer to a member")],
            extract.Dynamic.Select(dynamic => (dynamic.Line, dynamic.Why)));
        Assert.Equal(["real"], extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Calls).Select(reference => reference.Name));
    }

    /// <summary>
    /// <c>using namespace</c> and <c>using enum</c> import a scope, <c>using X::y</c> the scope with its one name, a namespace
    /// alias the scope through its name, and <c>using N = T</c> is a type that references <c>T</c>.
    /// </summary>
    [Fact]
    public void Usings_AreImports_AndAnAliasIsAType()
    {
        var extract = Read("u.cc", """
            using namespace app::core;
            using app::util::helper;
            namespace fs = app::files;
            using enum app::Color;
            using Name = app::Text;
            """);

        Assert.Equal(
            [
                new ExtractImport(CodeImportForms.Scope, "app.core", 1),
                new ExtractImport(CodeImportForms.Scope, "app.util", 2) { Show = ["helper"] },
                new ExtractImport(CodeImportForms.Scope, "app.files", 3) { Prefix = "fs" },
                new ExtractImport(CodeImportForms.Scope, "app.Color", 4),
            ],
            extract.Imports,
            ImportComparer.Instance);
        Assert.Equal("alias", Node(extract, "cpp:Name").SubKind);
        Assert.Contains(extract.References, reference => reference is { From: "cpp:Name", Kind: CodeEdgeKinds.References, Name: "Text", Qualifier.Name: "app" });
    }

    /// <summary>A raw string, whatever its prefix and delimiter, is never read as code.</summary>
    [Fact]
    public void ARawString_IsNeverReadAsCode()
    {
        var extract = Read("s.cc", """
            void f() {
                auto a = R"(x(1))";
                auto b = R"delim( ) "y(2)" )delim";
                auto c = u8R"(z(3)
            w(4))";
                real();
            }
            """);

        Assert.Equal(["real"], extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Calls).Select(reference => reference.Name));
    }

    /// <summary>A class's bases are inherited by their names, the scopes before them their qualifiers, a template's arguments passed over; a template's own parameters are no types it references.</summary>
    [Fact]
    public void ABaseList_IsInherited_AndATemplatesParametersAreNoTypes()
    {
        var extract = Read("d.hpp", """
            template <class T, int N = 3>
            class D : public Base<int, std::vector<T>>, private ns::Other {
                T value;
                T get(T other) const;
            };
            """);

        Assert.Equal(
            [("Base", null), ("Other", "ns")],
            extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Inherits).Select(reference => (reference.Name, reference.Qualifier?.Name)));
        Assert.DoesNotContain(extract.References, reference => reference.Name == "T");
        Assert.Null(Node(extract, "cpp:D.value").ValueType);
        Assert.Contains("template", Node(extract, "cpp:D").Modifiers);
    }

    /// <summary>
    /// A call through a function-pointer field of a type the file defines is marked dynamic - by itself in a member of the
    /// type, through <c>this</c>, or on a value of the type, a field declared after the member among them - and a function
    /// of the field's name elsewhere is still called by it.
    /// </summary>
    [Fact]
    public void ACallThroughAFunctionPointerField_IsDynamic_AndHidesNoFunctionOfItsName()
    {
        var extract = Read("o.cc", """
            #include <functional>
            struct Obj {
                int (*read)(int);
                std::function<void()> cb_;
                void go() { cb_(); this->cb_(); read(1); later(); }
                void (*later)();
            };
            void use(Obj* o) { o->read(3); }
            int read(int);
            void top() { read(2); }
            """);

        Assert.Equal(
            [(5, "a call through the function pointer cb_"), (5, "a call through the function pointer cb_"), (5, "a call through the function pointer later"), (5, "a call through the function pointer read"), (8, "a call through the function pointer read")],
            extract.Dynamic.Select(dynamic => (dynamic.Line, dynamic.Why)).Order());
        Assert.Equal(["read"], extract.References.Where(reference => reference is { From: "cpp:top()", Kind: CodeEdgeKinds.Calls }).Select(reference => reference.Name));
        Assert.DoesNotContain(extract.References, reference => reference.Kind == CodeEdgeKinds.Calls && reference.From is "cpp:Obj.go()" or "cpp:use(Obj*)");
    }

    /// <summary>A function with a trailing return type is read whole, its body the brace after the type, and the next declaration is its own.</summary>
    [Fact]
    public void ATrailingReturnType_EndsBeforeTheBody()
    {
        var extract = Read("t.cc", "auto f() -> int { return g(); }\nint h() { return k(); }\nauto m() -> std::string { return n(); }\n");

        Assert.Equal([("cpp:f()", "g", 1), ("cpp:h()", "k", 2), ("cpp:m()", "n", 3)], extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Calls).Select(reference => (reference.From, reference.Name, reference.Line)));
        Assert.Equal((1, 1), (Node(extract, "cpp:f()").Start, Node(extract, "cpp:f()").End));
    }

    /// <summary>A parameter pack takes any number of arguments, as C's <c>...</c> does, its type's own packs inside its arguments no pack of the function's.</summary>
    [Fact]
    public void AParameterPack_TakesAnyNumber()
    {
        var extract = Read("p.hpp", """
            template <class... A> void log(const char* f, A... args);
            template <class... A> void one(std::tuple<A...> t);
            """);

        Assert.Equal((1, -1), (Node(extract, "cpp:log(const char*,A...)").MinArity ?? -1, Node(extract, "cpp:log(const char*,A...)").MaxArity ?? -1));
        Assert.Equal((1, 1), (Node(extract, "cpp:one(std::tuple<A...>)").MinArity ?? -1, Node(extract, "cpp:one(std::tuple<A...>)").MaxArity ?? -1));
    }

    /// <summary>A name and its arguments with a literal among them, at a file's or a namespace's scope, is a variable its type makes - no prototype.</summary>
    [Fact]
    public void AVariableMadeWithArguments_IsNoPrototype()
    {
        var extract = Read("v.cc", """
            static Logger logger("app");
            Foo foo(1, 2);
            int f(int a = 3);
            """);

        Assert.Equal(
            [("cpp:Foo foo", "variable"), ("cpp:f(int)", "function"), ("cpp:v.cc:logger", "variable")],
            extract.Nodes.Select(node => (node.Id == "cpp:foo" ? "cpp:Foo foo" : node.Id, node.SubKind)).Order());
        Assert.Equal("Logger", Node(extract, "cpp:v.cc:logger").ValueType);
    }

    /// <summary>
    /// A variable a template's type makes with its arguments is no call - where it begins a statement, an <c>if</c>'s
    /// first among them - and a comparison that looks a template's arguments is no type, where its name begins neither.
    /// </summary>
    [Fact]
    public void AVariableOfATemplatesType_IsNoCall()
    {
        var extract = Read("l.cc", """
            void f() {
                std::lock_guard<std::mutex> lock(m);
                std::vector<int> v(10);
                if (a < b && c > d(x)) {}
                check(i < n, j > limit(k));
                if (a < b, c > e(x)) {}
                if (std::unique_lock<std::mutex> held(m); ready) { held.unlock(); }
            }
            """);

        Assert.Equal(["check", "d", "e", "limit", "unlock"], extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Calls).Select(reference => reference.Name).Order(StringComparer.Ordinal));
        Assert.Equal(new ExtractQualifier(CodeQualifierKinds.Type, "std.unique_lock"), Assert.Single(extract.References, reference => reference.Name == "unlock").Qualifier);
    }

    /// <summary>An enumeration's enumerators are read past a directive at its start, and past a type's arguments' commas in a value.</summary>
    [Fact]
    public void Enumerators_AreReadPastDirectivesAndTypesArguments()
    {
        var extract = Read("e.hpp", """
            enum E {
            #ifdef X
                A,
            #endif
                B = Foo<int, long>::value,
                C
            };
            """);

        Assert.Equal(["cpp:A", "cpp:B", "cpp:C"], extract.Nodes.Where(node => node.SubKind == "enumerator").Select(node => node.Id));
    }

    /// <summary><c>namespace a::b</c> is a part of <c>a</c> holding a part of <c>b</c>: one node each with <c>namespace a { namespace b {} }</c>.</summary>
    [Fact]
    public void ANestedNamespaceWrittenAtOnce_IsTheSameNamespaces()
    {
        var extract = Read("n.cc", "namespace a { namespace b { void f(); } }\nnamespace a::b { void g(); }\n");

        Assert.Equal(["cpp:a", "cpp:a.b", "cpp:a.b.f()", "cpp:a.b.g()"], extract.Nodes.Select(node => node.Id).Order(StringComparer.Ordinal));
        Assert.Equal("cpp:a", Node(extract, "cpp:a.b").Parent);
        Assert.Equal((1, 2), (Node(extract, "cpp:a").Start, Node(extract, "cpp:a").End));
    }

    /// <summary>What a header's anonymous namespace declares is each includer's: the repository's, as its macros are.</summary>
    [Fact]
    public void AHeadersAnonymousNamespace_IsTheRepositorys()
    {
        var extract = Read("h.hpp", "namespace { int helper(); }\nstatic int count;\n");

        Assert.Equal(["cpp:count", "cpp:helper()"], extract.Nodes.Select(node => node.Id).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(extract.Scopes, scope => !scope.Open);
    }

    /// <summary>A macro standing in the place of a declaration - at a namespace's scope, or alone on its line in a class - is marked and declares nothing.</summary>
    [Fact]
    public void AMacroInThePlaceOfADeclaration_IsMarked_AndDeclaresNothing()
    {
        var extract = Read("m.hpp", """
            namespace app {
            DECLARE_MODULE(io);
            class Widget {
                Q_OBJECT
            public:
                void show();
            };
            }
            """);

        Assert.Equal(
            [(2, "the macro DECLARE_MODULE stands here in the place of a declaration, which is not seen"), (4, "the macro Q_OBJECT stands here in the place of a declaration, which is not seen")],
            extract.Dynamic.Select(dynamic => (dynamic.Line, dynamic.Why)));
        Assert.Equal(["cpp:app", "cpp:app.Widget", "cpp:app.Widget.show()"], extract.Nodes.Select(node => node.Id).Order(StringComparer.Ordinal));
    }

    /// <summary>A literal inside a parameter's type's arguments is the type's: such a function is no variable its type makes.</summary>
    [Fact]
    public void ALiteralInATypesArguments_MakesNoVariable()
    {
        var extract = Read("a.hpp", "class V {\n    void set(std::array<int, 4> a);\n    Vec<3> add(Vec<3> o) const;\n    void fill(char buf[16]);\n    void half(std::array<int, N / 2> a);\n    void pick(std::enable_if_t<N == 2, int> a);\n};\nstd::bitset<8> mask(std::bitset<8> m);\n");

        Assert.Equal(
            [("cpp:V.add(Vec<3>)const", "method"), ("cpp:V.fill(char[16])", "method"), ("cpp:V.half(std::array<int,N/2>)", "method"), ("cpp:V.pick(std::enable_if_t<N==2,int>)", "method"), ("cpp:V.set(std::array<int,4>)", "method"), ("cpp:mask(std::bitset<8>)", "function")],
            extract.Nodes.Where(node => node.Kind == CodeNodeKinds.Member).Select(node => (node.Id, node.SubKind)).Order());
    }

    /// <summary>A member template's body, read once its class's is, is read with the template's parameters: a value of one of them is of no type the repository declares.</summary>
    [Fact]
    public void AMemberTemplatesBody_IsReadWithItsParameters()
    {
        var extract = Read("c.hpp", "class C {\n    template <class U> void conv(U u) { U y = u; y.go(); }\n};\n");

        var go = Assert.Single(extract.References, reference => reference.Name == "go");
        Assert.Equal(new ExtractQualifier(CodeQualifierKinds.Value), go.Qualifier);
    }

    /// <summary>A lambda a statement's word comes before, and an array <c>new</c> makes, call no element.</summary>
    [Fact]
    public void ALambdaAndAnArraysNew_CallNoElement()
    {
        var extract = Read("l.cc", "auto f() { return [this](int x) { return x; }; }\nvoid g(int n) { auto p = new Foo[n](); auto q = new int[n](); }\n");

        Assert.Empty(extract.Dynamic);
        Assert.Contains(extract.References, reference => reference is { Kind: CodeEdgeKinds.Instantiates, Name: "Foo" });
    }

    /// <summary>What an array's bounds and a placement's arguments call, a <c>new</c> writes, is called.</summary>
    [Fact]
    public void WhatANewsBoundsAndPlacementCall_IsCalled()
    {
        var extract = Read("n.cc", """
            void g(Pool& pool, Dir dir, Vec v, const char* s, int n) {
                char* b = new char[strlen(s) + 1];
                auto items = new Item[count_items(dir)];
                auto t = new (pool.get(n)) T;
                auto w = new int[v.size()]();
            }
            """);

        Assert.Empty(extract.Dynamic);
        Assert.Equal(
            [("count_items", null), ("get", "Pool"), ("size", "Vec"), ("strlen", null)],
            extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Calls).Select(reference => (reference.Name, reference.Qualifier?.Name)).Order());
    }

    /// <summary>
    /// A condition's comparisons declare no variable - a variable there is given its value - and neither does what follows
    /// a conditional's colon: each name keeps the type its parameter gives it, and a function pointer stays one.
    /// </summary>
    [Fact]
    public void AConditionsComparisons_AndAConditionalsValues_DeclareNoVariable()
    {
        var extract = Read("c.cc", """
            void (*cb)(int);
            void f(Node* parent, Node* child, Node* p, Node* n, int rows, int cols, int total, int a, bool c, Engine* e) {
                if (parent && child) { child->attach(parent); }
                if (p && n == 0) n->drop();
                if (rows * cols == total) cols++;
                while (a && cb(a)) {}
                int x = c ? 1 : a * e;
                e->start();
            }
            """);

        Assert.Equal(
            [("attach", "Node"), ("drop", "Node"), ("start", "Engine")],
            extract.References.Where(reference => reference.Name is "attach" or "drop" or "start").Select(reference => (reference.Name, reference.Qualifier?.Name)).Order());
        Assert.Contains(extract.Dynamic, dynamic => dynamic.Why == "a call through the function pointer cb");
    }

    /// <summary>
    /// In code a condition's comparisons and logic are no type's arguments - <c>i &lt; 0 || i &gt; (int)n</c> and
    /// <c>i &lt; n &amp;&amp; a[i] &gt; f(x)</c> call no <c>i</c> and declare nothing - while its arithmetic, an address
    /// and a pointer's or a reference's mark may be, as a declaration's comparisons may.
    /// </summary>
    [Fact]
    public void AConditionsComparisons_AreNoTypesArguments_InCode()
    {
        var extract = Read("k.cc", """
            void check(int idx, int n, int j, Node* m, int lo, int hi, int* a) {
                if (idx < 0 || idx > (int)n) { fail(); }
                for (; idx < n || j > m; idx++) m->go();
                if (lo < hi && n > (hi - lo)) { g(); }
                while (lo < hi && (*a) > (hi - lo)) lo++;
                for (int i = 0; i < n && a[i] > f(x); ++i) { }
                for (; idx < n && j > m; idx++) m->stop();
                auto last = std::get<N - 1>(t);
                auto w = std::make_unique<Widget*>(x);
                auto v = std::forward<T&&>(t);
                process<N * 2>(x);
                Grid<2 * N> grid(arg);
                sink.connect<&Listener::receive>(listener);
                std::function<Widget*(int)> make = factory;
                make(3);
            }
            """);

        Assert.Equal(
            [("connect", "sink"), ("f", null), ("fail", null), ("forward", "std"), ("g", null), ("get", "std"), ("go", "Node"), ("make_unique", "std"), ("process", null), ("stop", "Node")],
            extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Calls).Select(reference => (reference.Name, reference.Qualifier?.Name)).Order());
        Assert.Equal(["Grid"], extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Instantiates).Select(reference => reference.Name));
        Assert.Equal(["a call through the function pointer make"], extract.Dynamic.Select(dynamic => dynamic.Why));
    }

    /// <summary>The types a type's arguments name are referenced too, a template's own parameters and the standard library's left out.</summary>
    [Fact]
    public void ATypesArguments_AreReferenced()
    {
        var extract = Read("t.hpp", "struct Tree {\n    std::vector<Widget*> kids_;\n    std::map<Key, std::vector<app::Val>> index_;\n};\n");

        Assert.Equal(
            [("Key", null), ("Val", "app"), ("Widget", null)],
            extract.References.Where(reference => reference.Kind == CodeEdgeKinds.References).Select(reference => (reference.Name, reference.Qualifier?.Name)).Order());
    }

    /// <summary>A variable a condition declares - a for's, an if's (an if constexpr's too), a catch's, a range-for's - is of the type it is declared with.</summary>
    [Fact]
    public void AVariableAConditionDeclares_IsOfItsType()
    {
        var extract = Read("c.cc", """
            void f() {
                for (Engine* e = first(); e; e = next(e)) e->start();
                if (Engine* g = find()) g->stop();
                if constexpr (Pump* q = pump()) q->prime();
                try { run(); } catch (const Error& err) { err.report(); }
                for (Wheel* w : wheels) w->spin();
            }
            """);

        Assert.Equal(
            [("prime", "type", "Pump"), ("report", "type", "Error"), ("spin", "type", "Wheel"), ("start", "type", "Engine"), ("stop", "type", "Engine")],
            extract.References.Where(reference => reference.Name is "start" or "stop" or "report" or "spin" or "prime").Select(reference => (reference.Name, reference.Qualifier?.Kind, reference.Qualifier?.Name)).Order());
    }

    /// <summary>A value an operator joins is no variable declared: <c>a * b</c> leaves what <c>b</c> is as its parameter says.</summary>
    [Fact]
    public void AProduct_DeclaresNoVariable()
    {
        var extract = Read("p.cc", "void f(Foo a, Bar b) {\n    auto z = a * b;\n    x = a && b;\n    b.run();\n}\n");

        var run = Assert.Single(extract.References, reference => reference.Name == "run");
        Assert.Equal(new ExtractQualifier(CodeQualifierKinds.Type, "Bar"), run.Qualifier);
    }

    /// <summary>Namespaces nested deeper than a reader reads are marked and passed over, never the end of the reading.</summary>
    [Fact]
    public void NamespacesNestedTooDeep_AreMarked_AndPassedOver()
    {
        var depth = 2 * ScanFrame.MostDepth;
        var text = string.Concat(Enumerable.Range(0, depth).Select(at => $"namespace n{at} {{ ")) + "void f(); " + new string('}', depth) + "\nvoid after();\n";
        var extract = Read("deep.cc", text);

        Assert.Contains(extract.Dynamic, dynamic => dynamic.Why == $"declarations nested more than {ScanFrame.MostDepth} blocks deep, which are not read");
        Assert.Single(extract.Nodes, node => node.Id == "cpp:after()");
    }

    /// <summary>
    /// Placements of a <c>new</c> nested deeper than a reader reads are marked and passed over: no nesting of them reaches
    /// past the reader's stack, and the reading goes on past them.
    /// </summary>
    [Fact]
    public void PlacementsNestedTooDeep_AreMarked_AndPassedOver()
    {
        var depth = 2 * ScanFrame.MostDepth;
        var placed = string.Concat(Enumerable.Repeat("new (", depth)) + "arena()" + string.Concat(Enumerable.Repeat(") T", depth));
        var extract = Read("p.cc", $"void f() {{\n    auto p = {placed};\n}}\nvoid after() {{ done(); }}\n");

        Assert.Equal([(2, $"code nested more than {ScanFrame.MostDepth} placements of a new deep, which is not read")], extract.Dynamic.Select(dynamic => (dynamic.Line, dynamic.Why)));
        Assert.Equal(["cpp:after()", "cpp:f()"], extract.Nodes.Select(node => node.Id).Order(StringComparer.Ordinal));
        Assert.Equal(["done"], extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Calls).Select(reference => reference.Name));
    }

    /// <summary>
    /// A head written under each branch of a conditional over one body is read by its first head, the heads the other
    /// branches write none of what the body holds: a class, an enumeration, a namespace and a linkage block each one node
    /// holding its members - never a nested one of its own name - and a function's body calls nothing another head names.
    /// </summary>
    [Fact]
    public void AHeadUnderEachBranch_IsReadByItsFirst_TheOthersNoneOfItsBody()
    {
        var extract = Read("s.hpp", """
            #ifdef _WIN32
            class Sock : public A, public B {
            #else
            class Sock : public A {
            #endif
                void send();
            };
            #if WIDE
            enum class Mode : long {
            #else
            enum class Mode {
            #endif
                On,
            };
            #ifdef V2
            namespace net {
            #else
            namespace net {
            #endif
            void open();
            }
            #ifdef C_API
            extern "C" {
            #else
            extern "C" {
            #endif
            void closed();
            }
            #ifdef FAST
            void run(int a = fast()) {
            #else
            void run(int a = slow()) {
            #endif
                go();
            }
            """);

        Assert.Equal(
            ["cpp:Mode", "cpp:Mode.On", "cpp:Sock", "cpp:Sock.send()", "cpp:closed()", "cpp:net", "cpp:net.open()", "cpp:run(int)"],
            extract.Nodes.Select(node => node.Id).Order(StringComparer.Ordinal));
        Assert.Equal(["A", "B"], extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Inherits).Select(reference => reference.Name));
        Assert.Equal(["go"], extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Calls).Select(reference => reference.Name));
    }

    /// <summary>
    /// A type's keyword before two names declares a variable of the type the first names, its brace an initializer -
    /// <c>struct sockaddr_in addr_{};</c> a field at a class's scope, a variable at a namespace's - but where each name
    /// before the last is written as a macro is, an export macro's: <c>class API_EXPORT Widget { ... };</c> defines Widget.
    /// </summary>
    [Fact]
    public void ABraceInitialisedVariableOfATaggedType_IsAVariable_ButAfterAnExportMacro()
    {
        var extract = Read("n.hpp", """
            class Outer {
                struct sockaddr_in addr_{};
                int n_;
            };
            namespace net { struct sockaddr_in any_{}; }
            class API_EXPORT Widget { int w_; };
            """);

        Assert.Equal(
            [
                ("cpp:Outer", "class"), ("cpp:Outer.addr_", "field"), ("cpp:Outer.n_", "field"), ("cpp:Widget", "class"), ("cpp:Widget.w_", "field"),
                ("cpp:net", null), ("cpp:net.any_", "variable"),
            ],
            extract.Nodes.OrderBy(node => node.Id, StringComparer.Ordinal).Select(node => (node.Id, node.SubKind)));
        Assert.Equal(("sockaddr_in", "sockaddr_in"), (Node(extract, "cpp:Outer.addr_").ValueType, Node(extract, "cpp:net.any_").ValueType));
    }

    private static FileExtract Read(string path, string text) => new CppExtractor().Extract(new CodeFileInput(path, text));

    private static ExtractNode Node(FileExtract extract, string id) => Assert.Single(extract.Nodes, node => node.Id == id);

    /// <summary>Two imports alike in every field, their filters by their names.</summary>
    private sealed class ImportComparer : IEqualityComparer<ExtractImport>
    {
        public static ImportComparer Instance { get; } = new();

        public bool Equals(ExtractImport? x, ExtractImport? y)
            => x is not null && y is not null && x with { Show = null, Hide = null } == y with { Show = null, Hide = null }
                && (x.Show ?? []).SequenceEqual(y.Show ?? []) && (x.Hide ?? []).SequenceEqual(y.Hide ?? []);

        public int GetHashCode(ExtractImport obj) => obj.Target.GetHashCode(StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public override IReadOnlyList<CodeGraphFixture> Fixtures { get; } =
    [
        new("include/shapes/shape.hpp", ShapeHeader)
        {
            Nodes =
            [
                new("cpp:shapes", CodeNodeKinds.Namespace, (5, 32)),
                new("cpp:shapes.Shape", CodeNodeKinds.Type, (8, 21)),
                new("cpp:shapes.Shape.Shape(std::string)", CodeNodeKinds.Member, (10, 10)),
                new("cpp:shapes.Shape.~Shape()", CodeNodeKinds.Member, (11, 11)),
                new("cpp:shapes.Shape.area()const", CodeNodeKinds.Member, (12, 12)),
                new("cpp:shapes.Shape.name()const", CodeNodeKinds.Member, (13, 13)),
                new("cpp:shapes.Shape.draw(Canvas&,int)", CodeNodeKinds.Member, (14, 14)),
                new("cpp:shapes.Shape.log(const std::string&)", CodeNodeKinds.Member, (17, 17)),
                new("cpp:shapes.Shape.name_", CodeNodeKinds.Member, (20, 20)),
                new("cpp:shapes.Circle", CodeNodeKinds.Type, (23, 30)),
                new("cpp:shapes.Circle.area()const", CodeNodeKinds.Member, (26, 26)),
            ],
            Edges =
            [
                new("cpp:shapes.Circle", "cpp:shapes.Shape", CodeEdgeKinds.Inherits, 23, CodeConfidences.Exact),
                new("cpp:shapes.Circle.area()const", "cpp:shapes.Shape.area()const", CodeEdgeKinds.Overrides, 26, CodeConfidences.Exact),
            ],
        },
        new("src/shape.cpp", Shape)
        {
            Nodes =
            [
                new("cpp:shapes.Shape.draw(Canvas&,int)", CodeNodeKinds.Member, (8, 13)),
                new("cpp:shapes.Circle.area()const", CodeNodeKinds.Member, (23, 23)),
            ],
            Edges =
            [
                new("file:src/shape.cpp", "file:include/shapes/shape.hpp", CodeEdgeKinds.Imports, 1, CodeConfidences.Unique),
                new("file:src/shape.cpp", "file:src/canvas.hh", CodeEdgeKinds.Imports, 2, CodeConfidences.Exact),
                new("cpp:shapes.Shape.draw(Canvas&,int)", "cpp:shapes.Canvas.plot(int)", CodeEdgeKinds.Calls, 10, CodeConfidences.Exact),
                new("cpp:shapes.Shape.draw(Canvas&,int)", "cpp:shapes.Shape.log(const std::string&)", CodeEdgeKinds.Calls, 11, CodeConfidences.Exact),
                new("cpp:shapes.Shape.draw(Canvas&,int)", "cpp:shapes.Shape.name()const", CodeEdgeKinds.Calls, 11, CodeConfidences.Exact),
                new("cpp:shapes.Shape.draw(Canvas&,int)", "cpp:shapes.Shape.log(const std::string&)", CodeEdgeKinds.Calls, 12, CodeConfidences.Exact),
                new("cpp:shapes.Circle.area()const", "cpp:square(double)", CodeEdgeKinds.Calls, 23, CodeConfidences.Unique),
            ],
        },
        new("src/canvas.hh", CanvasHeader)
        {
            Nodes =
            [
                new("cpp:shapes.Canvas", CodeNodeKinds.Type, (2, 6)),
                new("cpp:shapes.Canvas.plot(int)", CodeNodeKinds.Member, (4, 4)),
                new("cpp:shapes.Canvas.scaled(T)const", CodeNodeKinds.Member, (5, 5)),
            ],
        },
        new("src/canvas.cxx", Canvas)
        {
            Nodes =
            [
                new("cpp:shapes.Canvas.plot(int)", CodeNodeKinds.Member, (6, 15)),
            ],
            Edges =
            [
                new("cpp:shapes.Canvas.plot(int)", "cpp:shapes.Canvas", CodeEdgeKinds.Instantiates, 10, CodeConfidences.Exact),
                new("cpp:shapes.Canvas.plot(int)", "cpp:shapes.Canvas.plot(int)", CodeEdgeKinds.Calls, 11, CodeConfidences.Exact),
                new("cpp:shapes.Canvas.plot(int)", "cpp:shapes.Canvas.scaled(T)const", CodeEdgeKinds.Calls, 13, CodeConfidences.Exact),
            ],
        },
        new("src/math.cc", Math)
        {
            Nodes =
            [
                new("cpp:square(double)", CodeNodeKinds.Member, (3, 3)),
                new("cpp:src/math.cc:hidden(int)", CodeNodeKinds.Member, (6, 6)),
                new("cpp:src/math.cc:counter", CodeNodeKinds.Member, (9, 9)),
            ],
        },
        new("src/main.cpp", Main)
        {
            Nodes =
            [
                new("cpp:src/main.cpp:mode()", CodeNodeKinds.Member, (6, 6)),
                new("cpp:src/main.cpp:mode()~2", CodeNodeKinds.Member, (8, 8)),
                new("cpp:main(int,char**)", CodeNodeKinds.Member, (11, 18)),
            ],
            Edges =
            [
                new("cpp:main(int,char**)", "cpp:shapes.Circle", CodeEdgeKinds.Instantiates, 13, CodeConfidences.Unique),
                new("cpp:main(int,char**)", "cpp:shapes.Shape.draw(Canvas&,int)", CodeEdgeKinds.Calls, 14, CodeConfidences.Exact),
                new("cpp:main(int,char**)", "cpp:shapes.Canvas", CodeEdgeKinds.Instantiates, 14, CodeConfidences.Unique),
                new("cpp:main(int,char**)", "cpp:shapes.Shape.area()const", CodeEdgeKinds.Calls, 16, CodeConfidences.Exact),
                new("cpp:main(int,char**)", "cpp:legacy.answer()", CodeEdgeKinds.Calls, 17, CodeConfidences.Exact),
                new("cpp:main(int,char**)", "cpp:shapes.detail.twice(T)", CodeEdgeKinds.Calls, 17, CodeConfidences.Exact),
                new("cpp:main(int,char**)", "cpp:clamp_to(int,int)", CodeEdgeKinds.Calls, 17, CodeConfidences.Unique),
            ],
        },
        new("include/shapes/detail.ipp", Detail)
        {
            Nodes =
            [
                new("cpp:shapes.detail", CodeNodeKinds.Namespace, (1, 4)),
                new("cpp:shapes.detail.twice(T)", CodeNodeKinds.Member, (2, 3)),
            ],
        },
        new("include/shapes/inline.inl", Inline)
        {
            Nodes = [new("cpp:clamp_to(int,int)", CodeNodeKinds.Member, (1, 1))],
        },
        new("include/shapes/box.hxx", Box)
        {
            Nodes =
            [
                new("cpp:shapes.Box", CodeNodeKinds.Type, (2, 2)),
                new("cpp:shapes.Box.value", CodeNodeKinds.Member, (2, 2)),
                new("cpp:shapes.Box.Fill", CodeNodeKinds.Type, (2, 2)),
                new("cpp:shapes.Box.Fill.Empty", CodeNodeKinds.Member, (2, 2)),
            ],
        },
        new("include/shapes/legacy.h", Legacy)
        {
            Nodes = [new("cpp:legacy.answer()", CodeNodeKinds.Member, (1, 1))],
        },
    ];
}
