using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;
using DssHarness.Core.Codebase.Languages.Scanning;

namespace DssHarness.Tests;

/// <summary>C's implementation over the structural scanner, held to the contract's kit, and the rules of its own the kit does not name.</summary>
public sealed class CExtractorTests : CodeGraphExtractorContractTests<CExtractor>
{
    internal const string LexHeader = """
        /* Lexing of expressions. */
        #ifndef LEX_H
        #define LEX_H

        #include <stddef.h>
        #include "token.h"

        #define MAX_TOKEN 64
        #define IS_DIGIT(c) ((c) >= '0' && (c) <= '9')

        /** A token's kind. Two of them. */
        typedef enum token_kind {
            TOKEN_NUMBER,
            TOKEN_NAME = 2,
        } token_kind;

        typedef struct lexer {
            const char *text;
            size_t at;
            int (*peek)(struct lexer *self);
        } lexer;

        typedef int (*visit_fn)(const struct token *token, void *state);

        /// Reads the next token. Returns zero at the end.
        int lex_next(lexer *lx, struct token *out);

        extern int lex_errors;

        #endif
        """;

    internal const string TokenHeader = """
        #pragma once

        struct token {
            int kind;
            char text[32];
        };
        """;

    internal const string Lex = """
        #include <ctype.h>
        #include "lex.h"

        /* A string with an apostrophe: "it's", and a // that is no comment. */
        static const char *banner = "it's // not a comment { (";

        int lex_errors = 0;

        static int skip_ws(lexer *lx);

        static int skip_ws(lexer *lx)
        {
            while (isspace((unsigned char)lx->text[lx->at]))
                lx->at++;
            return 0;
        }

        #define TWICE(x) \
            (2 * (x))

        int lex_next(lexer *lx, struct token *out)
        {
            int (*fallback)(struct lexer *) = lx->peek;
            skip_ws(lx);
            if (IS_DIGIT(lx->text[lx->at])) {
                out->kind = TOKEN_NUMBER;
                return TWICE(1);
            }
            lx->peek(lx);
            (*fallback)(lx);
            fallback(lx);
            return lex_name(lx, out, 0);
        }

        #ifdef _WIN32
        static int open_input(const char *path) { return _open(path); }
        #else
        static int open_input(const char *path) { return open(path, 0); }
        #endif

        REGISTER_LEXER(lex_next);
        """;

    internal const string Names = """
        #include "lex.h"

        // Reads a name.
        int lex_name(lexer *lx, struct token *out, int depth)
        {
            return lex_next(lx, out) + depth;
        }
        """;

    /// <summary>Comments, string and character literals and a directive's continued lines are never read as code: a body's calls are the calls its code makes, and a macro's text its own.</summary>
    [Fact]
    public void CommentsLiteralsAndContinuedDirectives_AreNeverReadAsCode()
    {
        var extract = Read("a.c", """
            #define NOTE "x(" \
                later(1)
            int go(void)
            {
                // first(1);
                /* second(2); { */
                const char *text = "third(3) // { (";
                char open = '(';
                return real(4);
            }
            """);

        Assert.Equal(["real"], Calls(extract, "c:go"));
        Assert.Equal(["later"], Calls(extract, "c:a.c:NOTE"));
        Assert.Equal((1, 2), Lines(extract, "c:a.c:NOTE"));
        Assert.Equal((3, 10), Lines(extract, "c:go"));
        Assert.DoesNotContain(extract.Nodes, node => node.Name is "first" or "second" or "third" or "later");
    }

    /// <summary>
    /// Every branch of a conditional is read: a function under each of two is two nodes, each marked conditional, and one
    /// whose head is written under each over one body is read by its first head, what follows it read as ever; the include
    /// guard marks nothing and is no node.
    /// </summary>
    [Fact]
    public void EveryBranchOfAConditional_IsRead_AndWhatItHoldsMarkedConditional_ButTheIncludeGuard()
    {
        var extract = Read("g.h", """
            #ifndef GUARD_H
            #define GUARD_H

            int always(void);

            #ifdef _WIN32
            int open_it(void) { return 1; }
            #else
            int open_it(void) { return 2; }
            #endif

            #if FAST
            int pick(int a) {
            #else
            int pick(int a, int b) {
            #endif
                return 0;
            }

            int after(void) { return 0; }

            #endif
            """);

        Assert.Empty(Node(extract, "c:always").Modifiers);
        Assert.Contains("conditional", Node(extract, "c:open_it").Modifiers);
        Assert.Contains("conditional", Node(extract, "c:open_it~2").Modifiers);
        Assert.Equal((7, 9), (Node(extract, "c:open_it").Start, Node(extract, "c:open_it~2").Start));
        Assert.Equal((13, 18), Lines(extract, "c:pick"));
        Assert.Equal(1, Node(extract, "c:pick").MaxArity);
        Assert.DoesNotContain(extract.Nodes, node => node.Id == "c:pick~2");
        Assert.Equal((20, 20), Lines(extract, "c:after"));
        Assert.Empty(Node(extract, "c:after").Modifiers);
        Assert.DoesNotContain(extract.Nodes, node => node.Name == "GUARD_H");
    }

    /// <summary>
    /// What it cannot bind is marked dynamic and makes no reference: a macro standing in the place of a declaration, a call
    /// of what a macro's parameter stands for, a call through a member, through a function pointer the file shows as one
    /// and through an element; a call of a function-like macro - one the file defines, or one named as a macro is - is
    /// marked and still a call of the macro. A function pointer declared is no call, and a struct's field is no name a
    /// call by itself makes through it.
    /// </summary>
    [Fact]
    public void WhatItCannotBind_IsMarkedDynamic()
    {
        var extract = Read("d.c", """
            #define CALL(f) f()
            struct ops { int (*read)(void); };
            DECLARE_MODULE(io);
            static int run(struct ops *o, int (*cb)(int), int (*table[2])(int))
            {
                CALL(go);
                o->read();
                cb(1);
                (*cb)(2);
                int (*fallback)(int) = cb;
                table[0](3);
                MAX(1, 2);
                return done() + read();
            }
            """);

        Assert.Equal(
            [
                (1, "a call of what the macro's parameter f stands for"),
                (3, "the macro DECLARE_MODULE stands here in the place of a declaration, which is not seen"),
                (6, "a call of the function-like macro CALL, which is not expanded"),
                (7, "a call through the member read, which C makes through a function pointer"),
                (8, "a call through the function pointer cb"),
                (9, "a call through the function pointer cb"),
                (11, "a call through an element, a function pointer"),
                (12, "a call of MAX, named as a macro is - one another file defines, which is not expanded"),
            ],
            extract.Dynamic.Select(dynamic => (dynamic.Line, dynamic.Why)).Order());
        Assert.Equal(["CALL", "MAX", "done", "read"], Calls(extract, "c:d.c:run"));
        Assert.Empty(Calls(extract, "c:d.c:CALL"));
    }

    /// <summary>An include in quotes is an import beside the file, then one by its path's ending standing in for it; in angle brackets one by its ending alone; one a macro names is none.</summary>
    [Fact]
    public void AnInclude_IsImportedBesideTheFile_ThenByItsEnding()
    {
        var extract = Read("src/a.c", """
            #include "net/b.h"
            #  include <sys/c.h>
            #include CONFIG_HEADER
            """);

        Assert.Equal(
            [
                new ExtractImport(CodeImportForms.Relative, "net/b.h", 1),
                new ExtractImport(CodeImportForms.Ending, "net/b.h", 1) { Fallback = true },
                new ExtractImport(CodeImportForms.Ending, "sys/c.h", 2),
            ],
            extract.Imports);
    }

    /// <summary>A header is C's where its code holds no mark of C++, C++'s where it does: a mark in a comment or a string is none, and the two tests always disagree.</summary>
    [Theory]
    [InlineData("int f(void); /* class namespace */ const char *s = \"::\";", false)]
    [InlineData("struct s { int class_count; };\n// template <class T>", false)]
    [InlineData("namespace x { int f(); }", true)]
    [InlineData("template <class T> T f(T);", true)]
    [InlineData("int a = b::c;", true)]
    [InlineData("class Shape;", true)]
    [InlineData("struct s { public: int x; };", true)]
    [InlineData("#include <vector>\nint f(void);", true)]
    [InlineData("#include <vector> // the standard's\nint f(void);", true)]
    [InlineData("#include <stdio.h>\n#include <sys/types.h>\nint f(void);", false)]
    [InlineData("static inline void pause(void) { asm volatile(\"pause\" ::: \"memory\"); }", false)]
    [InlineData("[[gnu::always_inline]] inline int f(void);", false)]
    [InlineData("#ifdef __cplusplus\nextern \"C\" {\nnamespace c_api {}\n#endif\nint f(void);\n#ifdef __cplusplus\n}\n#endif", false)]
    public void AHeadersClaim_IsDecidedByAMarkOfCppInItsCode(string probe, bool cpp)
    {
        var ofC = new CExtractor().Claims.Extensions.Single(claim => claim.Value == ".h").ContentTest!;
        var ofCpp = new CppExtractor().Claims.Extensions.Single(claim => claim.Value == ".h").ContentTest!;

        Assert.Equal((!cpp, cpp), (ofC(probe), ofCpp(probe)));
        Assert.Equal(cpp ? "cpp" : "c", CodeGraphExtractors.OfThisBuild.Route("include/x.h", probe).Extractor?.Language);
    }

    /// <summary>
    /// A name with internal linkage is its file's alone, its path in its id: a call from anywhere in its file finds it
    /// exact, the file's closed scope being one every declaration of it is written in, and a call from another file never
    /// reaches it - but a header's, which is each includer's own copy, is the repository's as its macros are. A prototype
    /// and its definition are one id - one node of two parts across files - and in one file the definition takes the id,
    /// the prototype the next, which declares it.
    /// </summary>
    [Fact]
    public void AStaticName_IsItsFilesAlone_AndAPrototype_IsItsDefinitionsNode()
    {
        var linked = CodeGraphLinker.Link(
        [
            File("src/a.h", "int shared(int);\nstatic inline int clampi(int x) { return x; }\n"),
            File("src/a.c", "#include \"a.h\"\nstatic int own(void);\nstatic int own(void) { return 0; }\nint shared(int x) { return own() + clampi(x); }\n"),
            File("src/b.c", "static int own(void) { return 1; }\nint main(void) { return shared(own()); }\n"),
        ]);

        Assert.Equal(["src/a.c", "src/a.h"], linked.Symbols.Single(symbol => symbol.Id == "c:shared").Paths.Order());
        Assert.Contains(new CodeEdge("c:main", "c:shared", CodeEdgeKinds.Calls, 2, CodeConfidences.Unique), linked.Edges);
        Assert.Contains(new CodeEdge("c:main", "c:src/b.c:own", CodeEdgeKinds.Calls, 2, CodeConfidences.Exact), linked.Edges);
        Assert.Contains(new CodeEdge("c:shared", "c:src/a.c:own", CodeEdgeKinds.Calls, 4, CodeConfidences.Exact), linked.Edges);
        Assert.Contains(new CodeEdge("c:shared", "c:clampi", CodeEdgeKinds.Calls, 4, CodeConfidences.Unique), linked.Edges);
        Assert.Contains(new CodeEdge("c:src/a.c:own~2", "c:src/a.c:own", CodeEdgeKinds.Declares, 2, CodeConfidences.Unique), linked.Edges);
        Assert.DoesNotContain(linked.Edges, edge => edge.From == "c:main" && edge.To.StartsWith("c:src/a.c:", StringComparison.Ordinal));
    }

    /// <summary>A declaration's summary is the first sentence of the comments directly above it - a Doxygen brief's, the lines of a run joined - and none past a blank line or from a comment trailing the line above.</summary>
    [Fact]
    public void ASummary_IsTheFirstSentenceOfTheCommentsDirectlyAbove()
    {
        var extract = Read("s.h", """
            /**
             * @brief Opens a file. More later.
             * @param path where
             */
            int open_file(const char *path);

            // First line
            // second line.
            int two(void);

            // Apart.

            int apart(void);
            int x; // trailing note
            int y;
            """);

        Assert.Equal("Opens a file.", Node(extract, "c:open_file").Summary);
        Assert.Equal("First line second line.", Node(extract, "c:two").Summary);
        Assert.Null(Node(extract, "c:apart").Summary);
        Assert.Null(Node(extract, "c:y").Summary);
    }

    /// <summary>A function's signature is its header on one line and its arity what its parameters take: none for <c>(void)</c>, any for C's empty list, at least the fixed ones of a variadic one, as for a macro.</summary>
    [Fact]
    public void ASignature_IsItsHeaderOnOneLine_AndItsArityWhatItsParametersTake()
    {
        var extract = Read("f.c", """
            int f(void);
            int g();
            int h(int a, ...);
            #define V(x, ...) printf(x, __VA_ARGS__)
            static inline int
            f2(int a,
               int b)
            {
                return a + b;
            }
            """);

        Assert.Equal((0, 0), Arity(extract, "c:f"));
        Assert.Equal((0, null), Arity(extract, "c:g"));
        Assert.Equal((1, null), Arity(extract, "c:h"));
        Assert.Equal((1, null), Arity(extract, "c:f.c:V"));
        Assert.Equal("static inline int f2(int a, int b)", Node(extract, "c:f.c:f2").Signature);
        Assert.Equal((5, 10), Lines(extract, "c:f.c:f2"));
        Assert.Equal(8, Node(extract, "c:f.c:f2").BodyStart);
    }

    /// <summary>Each declarator after the first is of the type the first is written after, and each after a type's body of that type: none is lost for having no type before it.</summary>
    [Fact]
    public void EveryDeclaratorOfADeclaration_IsRead()
    {
        var extract = Read("d.h", """
            struct P { int x, y; };
            int a[3], b[4];
            struct F { unsigned lo : 3, hi : 5; };
            typedef struct { int v; } point_t;
            typedef struct _Foo { int w; } Foo;
            typedef enum { RED, GREEN } Color;
            typedef unsigned char u8, byte_t;
            struct S { int z; } s, *ps;
            union U { int i; float f; } u;
            """);

        Assert.Equal(
            ((string[])
            [
                "c:Color", "c:Foo", "c:F", "c:F.hi", "c:F.lo", "c:GREEN", "c:P", "c:P.x", "c:P.y", "c:RED", "c:S", "c:S.z", "c:_Foo", "c:_Foo.w",
                "c:a", "c:b", "c:byte_t", "c:point_t", "c:point_t.v", "c:ps", "c:s", "c:u8", "c:U", "c:U.f", "c:U.i", "c:u",
            ]).Order(StringComparer.Ordinal),
            extract.Nodes.Select(node => node.Id).Order(StringComparer.Ordinal));
        Assert.Equal("struct", Node(extract, "c:point_t").SubKind);
        Assert.Equal("union", Node(extract, "c:U").SubKind);
        Assert.Equal("typedef", Node(extract, "c:Foo").SubKind);
        Assert.Contains(extract.References, reference => reference is { From: "c:Foo", Kind: CodeEdgeKinds.References, Name: "_Foo" });
    }

    /// <summary>A bracket one branch of a conditional leaves open and no other does is read as holding nothing, marked dynamic: what follows the conditional is read as it stands.</summary>
    [Fact]
    public void ABracketOneBranchLeavesOpen_NeverRunsOverWhatFollows()
    {
        var extract = Read("u.c", """
            #if 0
            this is not ( code
            #endif
            int ok(void) { return done(); }
            """);

        Assert.Equal(["c:ok"], extract.Nodes.Select(node => node.Id));
        Assert.Equal((4, 4), Lines(extract, "c:ok"));
        Assert.Equal(["done"], Calls(extract, "c:ok"));
        Assert.Contains(extract.Dynamic, dynamic => dynamic is { Line: 2, Why: "a bracket one branch of a conditional leaves open and no other does: read as holding nothing" });
    }

    /// <summary>An include guard is read as one written <c>#ifndef</c>, <c>#if !defined X</c> or <c>#if !defined(X)</c>, after a <c>#pragma once</c> too: its macro no node, what it holds marked no conditional.</summary>
    [Theory]
    [InlineData("#ifndef UTIL_H\n#define UTIL_H\nint util(void);\n#endif\n")]
    [InlineData("#pragma once\n#ifndef UTIL_H\n#define UTIL_H\nint util(void);\n#endif\n")]
    [InlineData("#if !defined UTIL_H\n#define UTIL_H\nint util(void);\n#endif\n")]
    [InlineData("#pragma once\n#if !defined(UTIL_H)\n#define UTIL_H\nint util(void);\n#endif\n")]
    public void AnIncludeGuard_IsReadInEachOfItsForms(string text)
    {
        var extract = Read("util.h", text);

        Assert.Equal(["c:util"], extract.Nodes.Select(node => node.Id));
        Assert.Empty(Node(extract, "c:util").Modifiers);
    }

    /// <summary>A name written as a macro is, alone on its line before a declaration that names its own type, is a macro standing there: marked, and no part of that declaration.</summary>
    [Fact]
    public void AMacroAloneOnItsLine_BeforeADeclaration_IsNoPartOfIt()
    {
        var extract = Read("g.h", """
            G_BEGIN_DECLS
            GType foo_get_type (void);
            G_END_DECLS
            """);

        Assert.Equal("GType foo_get_type (void)", Node(extract, "c:foo_get_type").Signature);
        Assert.Equal((2, 2), Lines(extract, "c:foo_get_type"));
        Assert.Equal([1, 3], extract.Dynamic.Select(dynamic => dynamic.Line));
    }

    /// <summary>
    /// A type defined after the specifiers of what is declared with it is read with its members, and its variables take
    /// those specifiers - <c>static</c> its file's alone, <c>extern</c> declared only; an unnamed struct's members at a
    /// file's scope name nothing, and its variable is read.
    /// </summary>
    [Fact]
    public void ATypeDefinedAfterSpecifiers_IsRead_ItsVariablesTakingThem()
    {
        var extract = Read("x.c", """
            static const struct op { const char *n; int (*f)(int); } ops[] = {{"a", fa}};
            static struct { int a; } state;
            extern struct ext { int e; } shared;
            """);

        Assert.Equal(
            ["c:ext", "c:ext.e", "c:op", "c:op.f", "c:op.n", "c:shared", "c:x.c:ops", "c:x.c:state"],
            extract.Nodes.Select(node => node.Id).Order(StringComparer.Ordinal));
        Assert.True(Node(extract, "c:shared").Prototype);
        Assert.False(Node(extract, "c:x.c:ops").Prototype);
        Assert.Equal("op", Node(extract, "c:x.c:ops").ValueType);
    }

    /// <summary>A macro after specifiers, where a declaration begins, stands in the place of one: marked, and no function.</summary>
    [Fact]
    public void AMacroAfterSpecifiers_StandsInThePlaceOfADeclaration()
    {
        var extract = Read("m.c", "static DEFINE_MUTEX(lock);\nstatic LIST_HEAD(queue);\nstatic inline int ok(void) { return 1; }\n");

        Assert.Equal(["c:m.c:ok"], extract.Nodes.Select(node => node.Id));
        Assert.Equal([1, 2], extract.Dynamic.Select(dynamic => dynamic.Line));
    }

    /// <summary>
    /// A macro between specifiers and the declaration it is a part of is marked and passed over, and the declaration read
    /// with those specifiers: <c>static</c> makes it its file's alone.
    /// </summary>
    [Fact]
    public void AMacroBetweenSpecifiersAndADeclaration_IsPassedOver_ItsSpecifiersTheDeclarations()
    {
        var extract = Read("m.c", "static EXPORT(1) int count;\nstatic EXPORT(2) ALIGNED(8) int twice(void) { return 2; }\nstatic EXPORT(int) third(void) { bar(); }\n");

        Assert.Equal(["c:m.c:count", "c:m.c:third", "c:m.c:twice"], extract.Nodes.Select(node => node.Id).Order(StringComparer.Ordinal));
        Assert.Equal(
            [(1, "the macro EXPORT stands in a declaration, which is not expanded"), (2, "the macro EXPORT stands in a declaration, which is not expanded"), (2, "the macro ALIGNED stands in a declaration, which is not expanded"), (3, "the macro EXPORT stands in a declaration, which is not expanded")],
            extract.Dynamic.Select(dynamic => (dynamic.Line, dynamic.Why)));
        Assert.Equal(["bar"], Calls(extract, "c:m.c:third"));
    }

    /// <summary>
    /// An unnamed struct's or union's members are the type's that holds it where no member is declared of it - C11's
    /// anonymous members - and no name of that type where one is: <c>union { ... } data;</c> holds its own.
    /// </summary>
    [Fact]
    public void AnUnnamedTypesMembers_AreItsHolders_OnlyWhereNoMemberIsDeclaredOfIt()
    {
        var extract = Read("u.c", "struct msg { int type; union { int i; float f; } data; struct { int x; }; };\n");

        Assert.Equal(["c:msg", "c:msg.data", "c:msg.type", "c:msg.x"], extract.Nodes.Select(node => node.Id).Order(StringComparer.Ordinal));
    }

    /// <summary>C has no operators to name: <c>operator</c> is a name like any other, its <c>=</c> an initializer's.</summary>
    [Fact]
    public void AnOperatorWord_IsAnyNameOfC()
    {
        var extract = Read("o.c", "int operator = compute();\n");

        Assert.Equal([("c:operator", "variable")], extract.Nodes.Select(node => (node.Id, node.SubKind)));
        Assert.Equal(["compute"], Calls(extract, "c:operator"));
    }

    /// <summary>Declarations nested deeper than a reader reads are marked and passed over, never the end of the reading.</summary>
    [Fact]
    public void DeclarationsNestedTooDeep_AreMarked_AndPassedOver()
    {
        var depth = ScanFrame.MostDepth + 10;
        var text = string.Concat(Enumerable.Range(0, depth).Select(at => $"struct s{at} {{ ")) + "int x; " + new string('}', depth) + ";\nint after(void);\n";
        var extract = Read("n.h", text);

        Assert.Contains(extract.Dynamic, dynamic => dynamic.Why == $"declarations nested more than {ScanFrame.MostDepth} blocks deep, which are not read");
        Assert.Single(extract.Nodes, node => node.Id == "c:after");
    }

    private static FileExtract Read(string path, string text) => new CExtractor().Extract(new CodeFileInput(path, text));

    private static CodeGraphFile File(string path, string text) => new(path, "c", Read(path, text));

    private static ExtractNode Node(FileExtract extract, string id) => Assert.Single(extract.Nodes, node => node.Id == id);

    private static (int, int) Lines(FileExtract extract, string id) => (Node(extract, id).Start, Node(extract, id).End);

    private static (int?, int?) Arity(FileExtract extract, string id) => (Node(extract, id).MinArity, Node(extract, id).MaxArity);

    private static IEnumerable<string> Calls(FileExtract extract, string from)
        => extract.References.Where(reference => reference.From == from && reference.Kind == CodeEdgeKinds.Calls).Select(reference => reference.Name);

    /// <inheritdoc/>
    public override IReadOnlyList<CodeGraphFixture> Fixtures { get; } =
    [
        new("src/lex.h", LexHeader)
        {
            Nodes =
            [
                new("c:MAX_TOKEN", CodeNodeKinds.Member, (8, 8)),
                new("c:IS_DIGIT", CodeNodeKinds.Member, (9, 9)),
                new("c:token_kind", CodeNodeKinds.Type, (12, 15)),
                new("c:TOKEN_NUMBER", CodeNodeKinds.Member, (13, 13)),
                new("c:TOKEN_NAME", CodeNodeKinds.Member, (14, 14)),
                new("c:lexer", CodeNodeKinds.Type, (17, 21)),
                new("c:lexer.text", CodeNodeKinds.Member, (18, 18)),
                new("c:lexer.peek", CodeNodeKinds.Member, (20, 20)),
                new("c:visit_fn", CodeNodeKinds.Type, (23, 23)),
                new("c:lex_next", CodeNodeKinds.Member, (26, 26)),
                new("c:lex_errors", CodeNodeKinds.Member, (28, 28)),
            ],
            Edges =
            [
                new("file:src/lex.h", "file:src/token.h", CodeEdgeKinds.Imports, 6, CodeConfidences.Exact),
                new("c:lex_next", "c:lexer", CodeEdgeKinds.References, 26, CodeConfidences.Unique),
                new("c:lex_next", "c:token", CodeEdgeKinds.References, 26, CodeConfidences.Unique),
            ],
        },
        new("src/token.h", TokenHeader)
        {
            Nodes =
            [
                new("c:token", CodeNodeKinds.Type, (3, 6)),
                new("c:token.kind", CodeNodeKinds.Member, (4, 4)),
                new("c:token.text", CodeNodeKinds.Member, (5, 5)),
            ],
        },
        new("src/lex.c", Lex)
        {
            Nodes =
            [
                new("c:src/lex.c:banner", CodeNodeKinds.Member, (5, 5)),
                new("c:lex_errors", CodeNodeKinds.Member, (7, 7)),
                new("c:src/lex.c:skip_ws", CodeNodeKinds.Member, (11, 16)),
                new("c:src/lex.c:skip_ws~2", CodeNodeKinds.Member, (9, 9)),
                new("c:src/lex.c:TWICE", CodeNodeKinds.Member, (18, 19)),
                new("c:lex_next", CodeNodeKinds.Member, (21, 33)),
                new("c:src/lex.c:open_input", CodeNodeKinds.Member, (36, 36)),
                new("c:src/lex.c:open_input~2", CodeNodeKinds.Member, (38, 38)),
            ],
            Edges =
            [
                new("file:src/lex.c", "file:src/lex.h", CodeEdgeKinds.Imports, 2, CodeConfidences.Exact),
                new("c:lex_next", "c:src/lex.c:skip_ws", CodeEdgeKinds.Calls, 24, CodeConfidences.Exact),
                new("c:lex_next", "c:IS_DIGIT", CodeEdgeKinds.Calls, 25, CodeConfidences.Unique),
                new("c:lex_next", "c:src/lex.c:TWICE", CodeEdgeKinds.Calls, 27, CodeConfidences.Exact),
                new("c:lex_next", "c:lex_name", CodeEdgeKinds.Calls, 32, CodeConfidences.Unique),
                new("c:src/lex.c:skip_ws~2", "c:src/lex.c:skip_ws", CodeEdgeKinds.Declares, 9, CodeConfidences.Unique),
            ],
        },
        new("src/names.c", Names)
        {
            Nodes =
            [
                new("c:lex_name", CodeNodeKinds.Member, (4, 7)),
            ],
            Edges =
            [
                new("c:lex_name", "c:lex_next", CodeEdgeKinds.Calls, 6, CodeConfidences.Unique),
            ],
        },
    ];
}
