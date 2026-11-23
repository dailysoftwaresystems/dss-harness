using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using DssHarness.Core.Walks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// C#, through the compiler's own parser: Roslyn's syntax tree of a file - its syntax alone, with no compilation, no
/// references and no restore, so nothing of a repository is built to graph it - gives every namespace, type and member
/// with its lines, signature and summary; the usings, as imports; base lists, overrides and test attributes; and each
/// call, creation and type a member names, with what its receiver is where the file says. A file that does not compile
/// is read as far as it parses.
/// </summary>
/// <remarks>
/// <para>
/// A node's id is its qualified name - a generic type's or method's with a backtick and its count of type parameters -
/// and a callable's parameter types: <c>csharp:A.B.Box`1.Put(T,int)</c>; an extension block's member with the block's
/// receiver, and the statements at a file's top with the file's path, since several programs of one repository have
/// them, as a file-local type and what it declares do, since several files may each have one alike. A partial type's
/// parts in several files share one id, so the graph holds it once, and two parts of one type, or two blocks of one
/// namespace, in one file are one node over the lines from the first to the last. Namespaces and types are open scopes,
/// since a namespace and a partial type are declared in many files; a file-local type's is closed, its file's alone. A
/// record's positional parameters are its properties.
/// </para>
/// <para>
/// A call is a reference by its name and number of arguments, qualified by its receiver: <c>this</c>, <c>base</c>, the
/// type a variable, parameter, field or property of the file is declared with, or a type made or cast, as a type
/// qualifier; a name the code writes that begins with a capital letter as a type's or a namespace's does, as a named one,
/// which a value nearer may be - else a value whose type the file does not say, by the name it is written as where it
/// is one: the linker finds the member it is, and the call through that member's type, or by its name alone. A chain of
/// two names or more, of any length, is written as a link to the use made for its last name (<see cref="ExtractQualifier.Link"/>),
/// each name's use linked to the one before it, so no reference writes the names before it again - past a variable of a
/// declared type, its first name's use qualified by that type, so the chain is read through each name's type as one past a
/// field is. A variable is seen
/// where C# scopes it - a block's, a statement's, a lambda's, a clause's - and hides a field of its name there alone. A
/// type a declaration names is a type qualifier's. A field, a property and an event say the type their value is declared
/// with, and an extension method the type it extends. What it cannot bind - a call on a <c>dynamic</c> value, a member
/// named by a string to reflection, <c>Activator.CreateInstance</c>, a delegate's invocation - is marked dynamic.
/// </para>
/// <para>
/// No depth is refused. The parser recurses once for each level a file nests, and a stack overflow cannot be caught, so
/// the depth is measured first, by a pass over the text of this reader's own (<see cref="Nesting"/>), and the parse is
/// run on a thread whose stack holds it (<see cref="DeepCalls.RunSized"/>); what is read from the tree is read on stacks
/// of this reader's own, every walk of it a loop. A file whose depth no thread's stack can hold is marked dynamic at the
/// line it nests deepest at, with why: an extract is never empty without its reason.
/// </para>
/// </remarks>
public sealed class CSharpExtractor : ICodeGraphExtractor
{
    /// <summary>The newest syntax the parser reads, its documentation comments read, and no preprocessor symbol defined.</summary>
    private static readonly CSharpParseOptions Options = new(LanguageVersion.Preview, DocumentationMode.Parse);

    /// <summary>
    /// The syntax <see cref="Closing"/> lexes by: <see cref="Options"/>' with documentation comments as comments, which
    /// hold no bracket of the code either way and so are not read as XML.
    /// </summary>
    private static readonly CSharpParseOptions Tokens = Options.WithDocumentationMode(DocumentationMode.None);

    /// <summary>
    /// The stack one level the parser or its lexer nests to takes, at most, as <see cref="Nesting"/> counts levels: what
    /// <see cref="DeepCalls.RunSized"/> gives a parse of each.
    /// </summary>
    /// <remarks>
    /// Measured on Roslyn 5.9 under .NET 10 on Windows x64 - whose calls take the most stack of the systems this tool runs
    /// on - by a probe outside the repository that searched, each try in a process of its own, for the least stack texts of
    /// 2,000 to 8,000 levels of one shape each parse on with no overflow and no short-stack error, as the runtime first
    /// compiles the parser. As this measure counts levels, a lambda in a lambda's body took up to 1,777 bytes a level, a
    /// generic's type argument 1,188, a call in a call's arguments 1,155 for each of the two levels it counts, an array in
    /// an array's initializer 1,105 for each of two, a class in a class 909, an interpolated string in another's hole 614
    /// for each of two, a documentation comment's element 598, a parenthesis 565, a block 483, a unary operator 450 and an
    /// if in an if 352 for each of two. 4096 bytes is more than twice the most measured: the margin for a level the
    /// measure reads as fewer than the parser takes, other processors, other compilations of the runtime's and later
    /// versions of the parser.
    /// </remarks>
    internal const int FrameBytes = 4096;

    /// <summary>The compiler's code for a parse its own guard ended short of stack: the whole text a skipped token, no member read.</summary>
    private const string ShortOfStack = "CS8078";

    /// <summary>
    /// The kinds of the marks the reading makes, each the sign of a limit of C#'s help and of every implementation's whose C# it reads
    /// (<see cref="CodeGraphLimit.Signs"/>): a call it cannot bind, a file of more characters than the compiler counts, and one that
    /// nests deeper than any thread's stack can be given to parse.
    /// </summary>
    internal const string
        DynamicCall = "dynamic-call",
        HugeFile = "huge-file",
        TooDeep = "too-deep";

    /// <inheritdoc/>
    public string Language => "csharp";

    /// <inheritdoc/>
    public int Version => 4;

    /// <inheritdoc/>
    public CodeGraphClaims Claims { get; } = CodeGraphClaims.OfExtensions(".cs");

    /// <inheritdoc/>
    public bool Outlines => false;

    /// <summary>
    /// What the reading of C# does not see in the code it reads, wherever that code stands: a .cs file's, and that of every
    /// implementation whose C# this reader reads (<see cref="Read"/>) - a Razor file's - whose help says them too.
    /// </summary>
    internal static IReadOnlyList<CodeGraphLimit> CodeUnseen { get; } =
    [
        ("preprocessor-branches", "code under a preprocessor branch the parse does not take: it defines no symbol, so code under #if DEBUG is out"),
        ("untyped-values", "the type of a value no declaration says - a var given a call's result, a lambda's parameter: a call on it is linked by its name alone"),
        ("local-functions", "local functions and lambdas as nodes of their own: what they call, the member they are written in calls"),
        new CodeGraphLimit("dynamic-calls", "a call on a dynamic value, a member named by a string to reflection, Activator.CreateInstance and a delegate's invocation: marked dynamic, and not linked") { Signs = [DynamicCall] },
        ("generic-extensions", "a generic extension method on a type parameter with no constraint, found by its name alone; a constructor's base(...) in a part of a partial class whose base list is in another file"),
        ("untyped-reads", "a read of a member through a value no declaration types, linked by the member's name alone; a read of another file's field or property whose name its type's members also carry, linked to each of them"),
        ("generated-sources", "code a source generator writes while the project builds, which is in no file of the tree"),
        ("same-named-types", "two types of one name in two projects - a Program in each - which are one node, as a partial type's parts are; a record's positional property as an override of its base's"),
        ("assembly-attributes", "what an assembly's or a module's attribute names or reads - [assembly: InternalsVisibleTo(...)] - which no node of the file declares: only its strings are kept, held by the file"),
        new CodeGraphLimit("deep-nesting", "a file that nests so deep - some hundreds of thousands of levels - that no thread can be given the stack the compiler's parser needs for it, a .NET thread's stack being counted in an Int32: nothing in it parsed, the line it nests deepest on marked dynamic, with why") { Signs = [TooDeep] },
    ];

    /// <inheritdoc/>
    public CodeGraphExtractorHelp Help { get; } = new(
        "C# (.cs), through the compiler's own parser and its syntax alone, nothing built: every namespace, type and member with its signature, summary and body; usings, base lists, overrides and test attributes; each call with what its receiver is where the file says; each member a member reads or writes without calling it - a field, a property, a constant, an enum member, and any name a nameof names - and each string literal the file holds, an interpolated string's constant parts each one of its own, by its value as the compiler reads it, its line and the node it is written in",
        [
            .. CodeUnseen,
            new CodeGraphLimit("huge-files", "a file of more than 2,147,483,647 characters, past the Int32 the compiler counts a text's characters in (SourceText.Length): nothing in it parsed, the line its count ends on marked dynamic, with why") { Signs = [HugeFile] },
        ])
    {
        Holds = "the compiler's syntax tree of the file, whole by the compiler's design - as Roslyn's documentation has it, a syntax tree holds every bit of the text it was parsed from: every token and every run of trivia is an entry of it, its text kept where it is 42 characters or more and a shorter one shared - so reading a file holds as much as its code writes, though the text itself is given the compiler by position (SourceText over the core's text), never as one string",
    };

    /// <inheritdoc/>
    public FileExtract Extract(CodeFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return Read(Language, input);
    }

    /// <summary>
    /// Reads C# text as another implementation's extract - a Razor file's code blocks among them - every id prefixed by
    /// <paramref name="language"/>: the one walker every implementation's C# is read by.
    /// </summary>
    /// <param name="language">The implementation the extract is part of: the prefix of every id.</param>
    /// <param name="input">The C# text, at the lines it stands on in its own file.</param>
    internal static FileExtract Read(string language, CodeFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var lines = input.Text.LineIndex;
        var (root, unread) = Parse(input.Text);

        return root is not null
            ? new Reading($"{language}:", input.Path, lines, root).Read()
            : new FileExtract([]) { Dynamic = [new ExtractDynamic(lines.LineAt(unread!.Value.At), unread.Value.Why, unread.Value.Kind)] };
    }

    /// <summary>
    /// <paramref name="text"/>'s syntax tree, parsed on a thread whose stack holds the depth it nests to
    /// (<see cref="Nesting"/>) - and again on twice the stack where the parser's own guard still ends it short of stack,
    /// as a nesting the measure reads shallower than the parser recurses into would - or, where the stack its depth needs
    /// is more than one thread can be given, where it nests deepest and why it is not read: the one way a file goes
    /// unread, and said.
    /// </summary>
    private static (CompilationUnitSyntax? Root, (long At, string Why, string Kind)? Unread) Parse(CodeText text)
    {
        // Roslyn counts a text's characters in an Int32 (SourceText.Length): a file of more is no text it can be given.
        if (text.Length > int.MaxValue)
        {
            return (null, (int.MaxValue, string.Create(CultureInfo.InvariantCulture, $"the file holds {text.Length:N0} characters, past the {int.MaxValue:N0} the C# compiler's text counts in an Int32 (SourceText.Length), so it is not parsed"), HugeFile));
        }

        var (depth, deepest) = Nesting.Of(text);
        var source = new CodeSourceText(text);

        for (var levels = Math.Max(depth, 1); ; levels *= 2)
        {
            CompilationUnitSyntax root;

            try
            {
                root = DeepCalls.RunSized(levels, FrameBytes, () => CSharpSyntaxTree.ParseText(source, Options).GetCompilationUnitRoot());
            }
            catch (InsufficientExecutionStackException exception)
            {
                var deeper = levels > Math.Max(depth, 1) ? $" - deeper as the parser reads it, which ran short of stack with room for {levels / 2} -" : string.Empty;
                return (null, (deepest, $"the file nests {depth} levels deep here{deeper}, which no thread can be given the stack to parse ({exception.Message.TrimEnd('.')})", TooDeep));
            }

            if (!root.ContainsDiagnostics || !root.GetDiagnostics().Any(diagnostic => diagnostic.Id == ShortOfStack))
            {
                return (root, null);
            }
        }
    }

    /// <summary>
    /// Where the bracket at <paramref name="open"/> of <paramref name="text"/> - a <c>{</c>, a <c>(</c> or a <c>[</c> - is closed, as
    /// the compiler's own lexer reads what it holds: a string, an interpolated or raw one among them, a character and a
    /// comment each one token, so no bracket inside one counts; the text's length where nothing closes it. How another
    /// implementation finds the C# its markup holds - a Razor file's code blocks and expressions - by C#'s rules. The lexer
    /// recurses into an interpolated string's holes and a directive's expression, so it lexes on a stack that holds what
    /// the text could nest to (<see cref="Nesting.Lexed"/>).
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="open">Where the bracket stands.</param>
    internal static long Closing(CodeText text, long open)
    {
        ArgumentNullException.ThrowIfNull(text);

        var (opens, closes) = text[open] switch
        {
            '(' => (SyntaxKind.OpenParenToken, SyntaxKind.CloseParenToken),
            '[' => (SyntaxKind.OpenBracketToken, SyntaxKind.CloseBracketToken),
            _ => (SyntaxKind.OpenBraceToken, SyntaxKind.CloseBraceToken),
        };

        return DeepCalls.RunSized(Nesting.Lexed(text), FrameBytes, () =>
        {
            var depth = 0;

            foreach (var (kind, start, _) in TokensFrom(text, open, Tokens))
            {
                if (kind == SyntaxKind.EndOfFileToken)
                {
                    break;
                }

                if (kind == opens)
                {
                    depth++;
                }
                else if (kind == closes && --depth == 0)
                {
                    return start;
                }
            }

            return text.Length;
        });
    }

    /// <summary>
    /// The tokens of <paramref name="text"/> from <paramref name="at"/> on as the compiler's own lexer reads them - a string,
    /// an interpolated or raw one among them, a character and a comment each one token - each with where it begins and
    /// ends, the text's end last: what reading C# a markup file holds goes by. The text is lexed from
    /// <paramref name="at"/> a piece at a time, each twice the one before, as far as the tokens are asked for - a token the
    /// piece's end may have cut short, one that runs to it, is given from the next piece - so what the lexer is given is
    /// never the whole text, only as far as the reader reads, and each piece is lexed from <paramref name="at"/> as the
    /// lexer reads from there.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="at">Where the lexing begins.</param>
    /// <param name="options">What the lexer reads by; its own defaults where none is given.</param>
    internal static IEnumerable<(SyntaxKind Kind, long Start, long End)> TokensFrom(CodeText text, long at, CSharpParseOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        var given = at;

        for (var size = (long)PieceCharacters; ; size *= 2)
        {
            var end = Math.Min(text.Length, at + size);
            var whole = end == text.Length;
            var piece = text.Slice(at, end);

            foreach (var token in SyntaxFactory.ParseTokens(piece, options: options))
            {
                var (start, past) = (at + token.SpanStart, at + token.Span.End);

                if (!whole && (token.IsKind(SyntaxKind.EndOfFileToken) || token.Span.End >= piece.Length))
                {
                    break;
                }

                if (start < given)
                {
                    continue;
                }

                yield return (token.Kind(), start, past);

                if (token.IsKind(SyntaxKind.EndOfFileToken))
                {
                    yield break;
                }

                given = past;
            }
        }
    }

    /// <summary>How many characters of a text <see cref="TokensFrom"/> lexes first, twice as many each time after.</summary>
    private const int PieceCharacters = 4096;

    /// <summary>
    /// The type a <c>var</c> is given where its initializer says: a <c>new</c> of a named type, or a cast - the one rule a
    /// local's type is read by, a Razor file's markup's locals among them.
    /// </summary>
    internal static TypeSyntax? Inferred(ExpressionSyntax? value) => value switch
    {
        ObjectCreationExpressionSyntax created => created.Type,
        CastExpressionSyntax cast => cast.Type,
        _ => null,
    };

    /// <summary>The attributes a test framework marks a test with, by their names as written: xUnit's, NUnit's and MSTest's.</summary>
    private static readonly HashSet<string> TestAttributes = new(StringComparer.Ordinal) { "Fact", "Theory", "Test", "TestCase", "TestCaseSource", "TestMethod", "DataTestMethod" };

    /// <summary>The members of reflection that take a member's name as a string.</summary>
    private static readonly HashSet<string> Reflection = new(StringComparer.Ordinal) { "GetMethod", "GetProperty", "GetField", "GetMember", "GetEvent", "InvokeMember" };

    /// <summary>The delegate types of the base library, by name: a variable of one is called only through the delegate.</summary>
    private static readonly HashSet<string> Delegates = new(StringComparer.Ordinal) { "Func", "Action", "Predicate", "Comparison", "Converter", "EventHandler" };

    /// <summary>How the name of a file a generator wrote ends, as the compiler's own check reads it.</summary>
    private static readonly string[] GeneratedEndings = [".g.cs", ".g.i.cs", ".generated.cs", ".designer.cs"];

    /// <summary>How the name of a file a build writes for a moment begins.</summary>
    private const string GeneratedStart = "TemporaryGeneratedFile_";

    /// <summary>What <c>dynamic</c> is to the values a member reads: a type no call on it is bound by.</summary>
    private const string DynamicType = "dynamic";

    /// <summary>The type a member is declared in, as its code sees it.</summary>
    /// <param name="Node">The type's node.</param>
    /// <param name="IdName">The name its id is made of, and its members' ids after it: its qualified name, a file-local type's with its file's path before it.</param>
    /// <param name="Local">Whether it is a file-local type, or declared in one: its file's alone.</param>
    /// <param name="Identifier">Its name as written, without its type parameters: what its constructors are named.</param>
    /// <param name="Inherited">The name of the class it inherits, as its constructors are named; <see langword="null"/> where it inherits none.</param>
    /// <param name="Interface">Whether it is an interface, whose members without a body are prototypes.</param>
    /// <param name="Fields">Its fields, properties, events and primary constructor's parameters, by name, each with its declared type where the file says.</param>
    /// <param name="TypeParameters">The names of its type parameters, and those of every type it is declared in.</param>
    private sealed record TypeFrame(ExtractNode Node, QualifiedName IdName, bool Local, string Identifier, string? Inherited, bool Interface, IReadOnlyDictionary<string, string?> Fields, IReadOnlySet<string> TypeParameters)
    {
        /// <summary>The receiver's type of the extension block its members are declared in; <see langword="null"/> outside one.</summary>
        public TypeSyntax? Receiver { get; init; }

        /// <summary>
        /// The parameters among <see cref="Fields"/> - a class's or a struct's primary constructor's, an extension block's
        /// receiver - which its members read by their names as fields, though no member is declared of them.
        /// </summary>
        public IReadOnlySet<string> Captured { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>A declaration still to read, with what it is declared in (<see cref="Reading.Declare"/>).</summary>
    /// <param name="Member">The declaration.</param>
    /// <param name="Scope">The qualified name of the namespace or type it is declared in; <see langword="null"/> at a file's level.</param>
    /// <param name="Parent">The node it is declared inside; <see langword="null"/> at a file's level.</param>
    /// <param name="Type">The type it is a member of; <see langword="null"/> outside one.</param>
    private readonly record struct Declaration(MemberDeclarationSyntax Member, QualifiedName? Scope, ExtractNode? Parent, TypeFrame? Type);

    /// <summary>
    /// The values a member's code reads by a name alone where its walk stands - its parameters, variables and local
    /// functions, each seen where C# scopes it, then its type's fields - each value with the type it is declared with where
    /// the file says. The walk tells it where it stands (<see cref="At"/>), in the order the file writes its code.
    /// </summary>
    /// <remarks>
    /// Each declaration's scope is a span of the text (<see cref="Scopes"/>), and scopes nest as the syntax does: a scope
    /// is entered once the walk reaches its start and left at its end, on a stack, the innermost of a name the one seen.
    /// The walk stands where each node's full span begins, which the tree keeps, as a scope's ends are full spans' too. A
    /// name declared twice in one scope with two types says neither.
    /// </remarks>
    private sealed class Known
    {
        /// <summary>The declarations, by where their scope begins - the outer of two that begin alike first.</summary>
        private readonly List<Local> _declared;

        /// <summary>The scopes the walk stands in, the innermost on top.</summary>
        private readonly Stack<Local> _open = new();

        /// <summary>The declarations seen now, by name, the innermost last.</summary>
        private readonly Dictionary<string, List<Local>> _seen = new(StringComparer.Ordinal);

        /// <summary>How many of <see cref="_declared"/> the walk has reached.</summary>
        private int _reached;

        /// <param name="nodes">The code walked, in the order the file writes it.</param>
        /// <param name="outer">Parameters the code sees but is not part of, seen throughout it: a primary constructor's, to its base's arguments.</param>
        /// <param name="typeParameters">The type parameters in scope, which a declared type of a value may be.</param>
        /// <param name="fields">Its type's fields, properties and events.</param>
        /// <param name="captured">The parameters among <paramref name="fields"/> no member is declared of.</param>
        public Known(IReadOnlyList<SyntaxNode> nodes, IEnumerable<ParameterSyntax>? outer, IReadOnlySet<string> typeParameters, IReadOnlyDictionary<string, string?> fields, IReadOnlySet<string> captured)
        {
            Fields = fields;
            Captured = captured;

            var declared = new List<Local>();

            foreach (var parameter in outer ?? [])
            {
                Add(declared, parameter.Identifier, parameter.Type, (int.MinValue, int.MaxValue), typeParameters);
            }

            foreach (var node in nodes)
            {
                Scopes.Declared(node, declared, typeParameters);
            }

            _declared = [.. declared.OrderBy(local => local.Start).ThenByDescending(local => local.End)];
        }

        /// <summary>Its type's fields, properties and events, each with the type it is declared with where the file says.</summary>
        public IReadOnlyDictionary<string, string?> Fields { get; }

        /// <summary>The parameters among <see cref="Fields"/> no member is declared of (<see cref="TypeFrame.Captured"/>).</summary>
        public IReadOnlySet<string> Captured { get; }

        /// <summary>Stands the walk at <paramref name="position"/>: the scopes it has left are closed, and those it has reached opened.</summary>
        public void At(int position)
        {
            while (_open.TryPeek(out var top) && top.End <= position)
            {
                _open.Pop();
                var seen = _seen[top.Name];
                seen.RemoveAt(seen.LastIndexOf(top));
            }

            while (_reached < _declared.Count && _declared[_reached].Start <= position)
            {
                var entering = _declared[_reached++];

                if (entering.End <= position)
                {
                    continue;
                }

                if (!_seen.TryGetValue(entering.Name, out var seen))
                {
                    _seen[entering.Name] = seen = [];
                }

                // One name declared twice in one scope is one local, of no type where its two say two.
                if (seen.Count > 0 && seen[^1] is { } same && same.Start == entering.Start && same.End == entering.End)
                {
                    if (same.Type != entering.Type)
                    {
                        same.Type = null;
                    }

                    continue;
                }

                seen.Add(entering);
                _open.Push(entering);
            }
        }

        /// <summary>Whether <paramref name="name"/> is a parameter or a variable seen where the walk stands, and the type it is declared with where the file says.</summary>
        public bool Local(string name, out string? declared)
        {
            declared = null;

            if (!_seen.TryGetValue(name, out var seen) || seen.Count == 0 || seen[^1].Function)
            {
                return false;
            }

            declared = seen[^1].Type;
            return true;
        }

        /// <summary>Whether <paramref name="name"/> is a local function seen where the walk stands: what it calls, its member calls.</summary>
        public bool Function(string name) => _seen.TryGetValue(name, out var seen) && seen.Count > 0 && seen[^1].Function;

        /// <summary>Whether <paramref name="name"/> is a value the member reads, and the type it is declared with where the file says.</summary>
        public bool TryGet(string name, out string? declared) => Local(name, out declared) || Fields.TryGetValue(name, out declared);

        /// <summary>A declaration of <paramref name="identifier"/>, of <paramref name="written"/>'s type where the file says one, seen in <paramref name="scope"/>.</summary>
        public static void Add(List<Local> declared, SyntaxToken identifier, TypeSyntax? written, (int Start, int End) scope, IReadOnlySet<string> typeParameters, bool function = false)
        {
            if (identifier.ValueText is { Length: > 0 } name)
            {
                declared.Add(new Local(name, scope.Start, scope.End, written is null ? null : ValueKey(written, typeParameters), function));
            }
        }
    }

    /// <summary>A parameter, a variable or a local function, and the span of the text it is seen in.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="start">Where its scope begins.</param>
    /// <param name="end">Where its scope ends: past its last character.</param>
    /// <param name="type">The type it is declared with where the file says.</param>
    /// <param name="function">Whether it is a local function.</param>
    private sealed class Local(string name, int start, int end, string? type, bool function)
    {
        public string Name { get; } = name;

        public int Start { get; } = start;

        public int End { get; } = end;

        public string? Type { get; set; } = type;

        public bool Function { get; } = function;
    }

    /// <summary>
    /// One walk of a member's code as it is read: the node its references are made from, what its names are where it
    /// stands, and what it has read of the chains its receivers write - each node's chain, the use made for each member a
    /// chain reads, each chain's top and dynamic root - kept, so a chain of any length is read once whatever reads it.
    /// </summary>
    private sealed class Walked(ExtractNode from, IReadOnlyList<SyntaxNode> nodes, Known known)
    {
        /// <summary>The node the walk's references are made from.</summary>
        public ExtractNode From { get; } = from;

        public Known Known { get; } = known;

        /// <summary>The nodes the walk reads: a use is made of a member it reads alone.</summary>
        public HashSet<SyntaxNode> Nodes { get; } = new(nodes, ReferenceEqualityComparer.Instance);

        /// <summary>Each node's chain of names (<see cref="Reading.ChainOf"/>), none where it writes none.</summary>
        public Dictionary<ExpressionSyntax, Chain?> Chains { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>The use made for each member access, and whether its qualifier writes its receiver's chain; none where none is made.</summary>
        public Dictionary<MemberAccessExpressionSyntax, (int Index, bool Writes)?> Uses { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>The dynamic value each receiver's chain begins at, none where it begins at none (<see cref="Reading.DynamicRoot"/>).</summary>
        public Dictionary<ExpressionSyntax, string?> Roots { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>The member access each node's chain ends at (<see cref="Reading.Top"/>).</summary>
        public Dictionary<SyntaxNode, SyntaxNode> Tops { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>The nodes a climb to a chain's top passes, kept for one climb at a time.</summary>
        public List<SyntaxNode> Climbed { get; } = [];

        /// <summary>The nodes a read down a chain passes, kept for one read at a time.</summary>
        public List<ExpressionSyntax> Down { get; } = [];

        /// <summary>The nodes a read down to a dynamic root passes, kept for one read at a time.</summary>
        public List<ExpressionSyntax> Rooted { get; } = [];
    }

    /// <summary>The chain of names a receiver writes - <c>a.b.c</c> - as read from its end (<see cref="Reading.ChainOf"/>).</summary>
    /// <param name="Head">Its first name.</param>
    /// <param name="Count">How many names it writes.</param>
    /// <param name="ThroughThis">Whether <c>this.</c> is written before it.</param>
    /// <param name="Generic">Whether a name of it is given type arguments, and so is a type's.</param>
    /// <param name="Last">Its last name.</param>
    /// <param name="Before">What writes the names before its last; <see langword="null"/> where it writes one.</param>
    private sealed record Chain(string Head, int Count, bool ThroughThis, bool Generic, string Last, ExpressionSyntax? Before)
    {
        /// <summary>The member access that writes its last name, whose use a chain written as links links to; none for a qualified name's.</summary>
        public MemberAccessExpressionSyntax? Access { get; init; }
    }

    /// <summary>
    /// Where C# scopes what a declaration declares, as a span of the text: a parameter its member's, lambda's or local
    /// function's; a local, a local function and a variable an expression declares - a pattern's, an <c>out</c> one -
    /// the statements they stand among, a switch's sections one span for them all, or, where a statement declares it as a
    /// loop, a <c>using</c>, a <c>fixed</c> or a <c>while</c>'s condition does, that statement; a variable a switch arm, a
    /// case label, a catch's filter, a lambda's or a member's expression, a query clause, an initializer or an attribute
    /// declares, that; a loop's, a catch's and a query's range variables theirs, a range variable from the end of the
    /// clause that declares it.
    /// </summary>
    private static class Scopes
    {
        /// <summary>What <paramref name="node"/> declares, each added to <paramref name="declared"/> with its scope; nothing where it declares none.</summary>
        public static void Declared(SyntaxNode node, List<Local> declared, IReadOnlySet<string> typeParameters)
        {
            switch (node)
            {
                case ParameterSyntax parameter:
                    Known.Add(declared, parameter.Identifier, parameter.Type, Span(parameter.Parent is BaseParameterListSyntax list ? list.Parent ?? list : parameter.Parent ?? parameter), typeParameters);
                    break;

                case VariableDeclarationSyntax declaration:
                {
                    var scope = declaration.Parent switch
                    {
                        LocalDeclarationStatementSyntax statement => Statements(statement),
                        { } holder => Span(holder),
                        null => Span(declaration),
                    };

                    foreach (var variable in declaration.Variables)
                    {
                        Known.Add(declared, variable.Identifier, declaration.Type.IsVar ? Inferred(variable.Initializer?.Value) : declaration.Type, scope, typeParameters);
                    }

                    break;
                }

                case SingleVariableDesignationSyntax designation:
                    Known.Add(declared, designation.Identifier, designation.Parent switch
                    {
                        DeclarationExpressionSyntax written => written.Type,
                        DeclarationPatternSyntax written => written.Type,
                        RecursivePatternSyntax written => written.Type,
                        _ => null,
                    }, Expressed(designation), typeParameters);
                    break;

                case ForEachStatementSyntax loop:
                    Known.Add(declared, loop.Identifier, loop.Type, Span(loop), typeParameters);
                    break;

                case CatchDeclarationSyntax caught:
                    Known.Add(declared, caught.Identifier, caught.Type, Span(caught.Parent ?? caught), typeParameters);
                    break;

                case LocalFunctionStatementSyntax function:
                    Known.Add(declared, function.Identifier, null, Statements(function), typeParameters, function: true);
                    break;

                case FromClauseSyntax clause:
                    Known.Add(declared, clause.Identifier, clause.Type, (clause.Expression.FullSpan.End, QueryEnd(clause)), typeParameters);
                    break;

                case LetClauseSyntax clause:
                    Known.Add(declared, clause.Identifier, null, (clause.FullSpan.End, QueryEnd(clause)), typeParameters);
                    break;

                // A join's variable is seen by what it is joined on, and on through the query where no into renames it.
                case JoinClauseSyntax clause:
                    Known.Add(declared, clause.Identifier, clause.Type, (clause.InExpression.FullSpan.End, clause.Into is { } into ? into.FullSpan.Start : QueryEnd(clause)), typeParameters);
                    break;

                case JoinIntoClauseSyntax clause:
                    Known.Add(declared, clause.Identifier, null, (clause.FullSpan.End, clause.Parent is JoinClauseSyntax join ? QueryEnd(join) : clause.FullSpan.End), typeParameters);
                    break;

                case QueryContinuationSyntax clause:
                    Known.Add(declared, clause.Identifier, null, (clause.Identifier.FullSpan.End, clause.FullSpan.End), typeParameters);
                    break;
            }
        }

        /// <summary>
        /// The scope of a variable an expression declares - a pattern's, an <c>out</c> one, a deconstruction's: the first
        /// of what holds it that scopes one, climbed to on a loop.
        /// </summary>
        private static (int Start, int End) Expressed(SyntaxNode designation)
        {
            for (var at = designation.Parent; at is not null; at = at.Parent)
            {
                switch (at)
                {
                    // A loop's, a using's and a fixed's head, and a while's condition, scope theirs to the statement; any
                    // other statement's - an if's condition among them - to the statements it stands among.
                    case WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax or CommonForEachStatementSyntax or UsingStatementSyntax or FixedStatementSyntax:
                        return Span(at);

                    case StatementSyntax statement:
                        return Statements(statement);

                    case SwitchExpressionArmSyntax or CatchClauseSyntax or AnonymousFunctionExpressionSyntax or QueryClauseSyntax or SelectOrGroupClauseSyntax or AttributeSyntax or PrimaryConstructorBaseTypeSyntax or MemberDeclarationSyntax:
                        return Span(at);

                    // A case label's variables are its section's.
                    case SwitchLabelSyntax label:
                        return Span(label.Parent ?? label);

                    // An expression body's are its member's, a constructor's initializer's its constructor's.
                    case ArrowExpressionClauseSyntax or ConstructorInitializerSyntax:
                        return Span(at.Parent ?? at);

                    // A field's, a property's, a parameter's and an enum member's initializer scopes its own; a local's is
                    // its statement's.
                    case EqualsValueClauseSyntax value when value.Parent is not VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: not BaseFieldDeclarationSyntax } }:
                        return Span(value);
                }
            }

            return (int.MinValue, int.MaxValue);
        }

        /// <summary>
        /// The span of the statements <paramref name="statement"/> stands among - past any labels it has: a block's, a
        /// switch's sections' together, the file's top for a top-level one; an embedded one - a loop's or an if's statement
        /// that is no block - its own.
        /// </summary>
        private static (int Start, int End) Statements(SyntaxNode statement)
        {
            var at = statement;

            while (at.Parent is LabeledStatementSyntax labeled)
            {
                at = labeled;
            }

            return at.Parent switch
            {
                BlockSyntax block => Span(block),
                SwitchSectionSyntax { Parent: SwitchStatementSyntax switched } => (switched.OpenBraceToken.FullSpan.Start, switched.FullSpan.End),
                GlobalStatementSyntax => (int.MinValue, int.MaxValue),
                _ => Span(at),
            };
        }

        /// <summary>Where the query body a clause is part of selects, past which its range variables are seen no more: a continuation's into begins anew.</summary>
        private static int QueryEnd(SyntaxNode clause) => clause.Parent switch
        {
            QueryBodySyntax body => body.SelectOrGroup.FullSpan.End,
            QueryExpressionSyntax query => query.Body.SelectOrGroup.FullSpan.End,
            _ => clause.FullSpan.End,
        };

        /// <summary>
        /// <paramref name="node"/>'s full span, the trivia about it with it: the tree keeps where a node begins and how wide
        /// it is, while its span's ends are found down its first and its last token, as deep as it nests.
        /// </summary>
        private static (int Start, int End) Span(SyntaxNode node) => (node.FullSpan.Start, node.FullSpan.End);
    }

    /// <summary>One file's reading: what it declares, imports, references and cannot bind, gathered as its tree is walked.</summary>
    private sealed class Reading(string prefix, string path, CodeLineIndex lines, CompilationUnitSyntax root)
    {
        private readonly List<ExtractNode> _nodes = [];
        private readonly Dictionary<QualifiedName, ExtractScope> _scopes = new();
        private readonly List<ExtractImport> _imports = [];
        private readonly List<ExtractReference> _references = [];
        private readonly List<ExtractDynamic> _dynamic = [];
        private readonly ExtractLiterals _literals = new();
        private readonly ExtractIds _ids = new();

        /// <summary>The index in the nodes of each namespace's and type's first node, by its id, kind and parent: where a later part of it runs on.</summary>
        private readonly Dictionary<(QualifiedName Id, string Kind, QualifiedName? Parent), int> _parts = [];

        /// <summary>The class each type of the file inherits, by the type's id, as its constructors are named: a partial type's base list may be on another part.</summary>
        private readonly Dictionary<QualifiedName, string> _inherited = new();

        /// <summary>The delegate types the file declares, by name: a variable of one is called only through the delegate.</summary>
        private readonly HashSet<string> _delegates = root.DescendantNodes(node => node is not BlockSyntax).OfType<DelegateDeclarationSyntax>()
            .Select(declared => declared.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);

        public FileExtract Read()
        {
            foreach (var directive in root.Usings)
            {
                Import(directive);
            }

            // The assembly's and the module's attributes: their strings, which no declaration holds - the file's own does.
            foreach (var node in root.AttributeLists.SelectMany(list => list.DescendantNodes()))
            {
                Literal(node, holder: null);
            }

            if (root.Members.OfType<GlobalStatementSyntax>().ToList() is { Count: > 0 } statements)
            {
                TopLevel(statements);
            }

            Declare(root.Members.Where(member => member is not GlobalStatementSyntax).Select(member => new Declaration(member, Scope: null, Parent: null, Type: null)));

            return new FileExtract(_nodes)
            {
                Scopes = [.. _scopes.Values],
                Imports = _imports,
                References = _references,
                Generated = Generated(),
                Dynamic = _dynamic,
                Literals = _literals.Kept(),
            };
        }

        /// <summary>
        /// A using directive, as an import of a scope: a namespace, the type <c>using static</c> names, or what an alias
        /// stands for, reached through the alias as its prefix; every C# file of the repository makes a <c>global</c> one.
        /// </summary>
        private void Import(UsingDirectiveSyntax directive)
        {
            if (directive.NamespaceOrType is not NameSyntax named || Written(named) is not { } target)
            {
                return;
            }

            _imports.Add(new ExtractImport(CodeImportForms.Scope, target, Line(directive.SpanStart))
            {
                Prefix = directive.Alias is { } alias && alias.Name.Identifier.ValueText is { Length: > 0 } name ? name : null,
                Global = directive.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword),
            });
        }

        /// <summary>
        /// Declarations at a file's or a namespace's level, or inside a type, and every one they hold, read in the order the
        /// file writes them: on a stack of their own, a namespace's or a type's members stacked once it is read, its first
        /// on top - so a declaration nested any depth is read as one nested once is, and no nesting is a call deeper.
        /// </summary>
        private void Declare(IEnumerable<Declaration> declarations)
        {
            var pending = new Stack<Declaration>();
            Stacked(pending, declarations);

            while (pending.TryPop(out var next))
            {
                Stacked(pending, Declared(next));
            }
        }

        /// <summary>
        /// One declaration at a file's or a namespace's level, or one inside its type: what it declares, and the
        /// declarations it holds, each with the scope, the node and the type it is declared in, for <see cref="Declare"/>.
        /// </summary>
        private IEnumerable<Declaration> Declared(Declaration declaration)
        {
            var (member, scope, parent, type) = declaration;

            switch (member)
            {
                case BaseNamespaceDeclarationSyntax declared:
                    return Namespace(declared, scope, parent);

                // An extension block's members are its static class's, each called on the block's receiver, which they
                // read by its name as a member reads a field.
                case ExtensionBlockDeclarationSyntax block when type is not null:
                {
                    var receiver = block.ParameterList?.Parameters.FirstOrDefault();
                    var typeParameters = With(type.TypeParameters, block.TypeParameterList);
                    var fields = new Dictionary<string, string?>(type.Fields, StringComparer.Ordinal);

                    if (receiver is { Identifier.ValueText.Length: > 0 })
                    {
                        fields[receiver.Identifier.ValueText] = receiver.Type is { } written ? ValueKey(written, typeParameters) : null;
                    }

                    var frame = type with
                    {
                        Receiver = receiver?.Type,
                        Fields = fields,
                        TypeParameters = typeParameters,
                        Captured = receiver is { Identifier.ValueText.Length: > 0 } ? new HashSet<string>([.. type.Captured, receiver.Identifier.ValueText], StringComparer.Ordinal) : type.Captured,
                    };

                    return [.. block.Members.Select(inner => new Declaration(inner, scope, parent, frame))];
                }

                case BaseTypeDeclarationSyntax declared:
                    return Type(declared, scope, parent, type);

                case DelegateDeclarationSyntax declared:
                    Delegate(declared, scope, parent, type);
                    return [];

                // A member at a namespace's level is no C#: a file cut short, or not yet written.
                default:
                    if (type is not null)
                    {
                        Member(member, type);
                    }

                    return [];
            }
        }

        /// <summary>A namespace: its node, its scopes and imports; answers the declarations it holds.</summary>
        private IEnumerable<Declaration> Namespace(BaseNamespaceDeclarationSyntax declared, QualifiedName? scope, ExtractNode? parent)
        {
            if (Written(declared.Name) is not { } written)
            {
                return [.. declared.Members.Select(member => new Declaration(member, scope, parent, Type: null))];
            }

            var qualified = ExtractText.Qualify(scope, written);

            // Each name a declaration such as `namespace A.B` opens is a scope inside the one before it.
            var at = scope;

            foreach (var part in written.Split('.'))
            {
                var opened = ExtractText.Qualify(at, part);
                Scope(opened, at);
                at = opened;
            }

            var node = Part(qualified, CodeNodeKinds.Namespace, parent, declared, () => new ExtractNode(string.Empty, CodeNodeKinds.Namespace, qualified.PastLastDot(), qualified, Line(declared.SpanStart), EndLine(declared), ExtractText.Signature($"namespace {written}"))
            {
                Scope = qualified.BeforeLastDot(),
                Parent = parent?.Id,
                BodyStart = declared is NamespaceDeclarationSyntax block ? LineOf(block.OpenBraceToken) : null,
            });

            foreach (var directive in declared.Usings)
            {
                Import(directive);
            }

            return [.. declared.Members.Select(member => new Declaration(member, qualified, node, Type: null))];
        }

        /// <summary>A type: its node, its scope, bases and head, an enum's members; answers the declarations it holds.</summary>
        private IEnumerable<Declaration> Type(BaseTypeDeclarationSyntax declared, QualifiedName? scope, ExtractNode? parent, TypeFrame? outer)
        {
            var identifier = declared.Identifier.ValueText;

            if (identifier.Length == 0)
            {
                return [];
            }

            var list = (declared as TypeDeclarationSyntax)?.TypeParameterList;
            var name = Generic(identifier, list?.Parameters.Count ?? 0);
            var qualified = ExtractText.Qualify(scope, name);
            var typeParameters = With(outer?.TypeParameters, list);
            var (idName, local) = Named(outer, declared.Modifiers, name, qualified);

            // A type with no body - a record's one line - ends its header at its semicolon.
            var opens = LineOf(declared.OpenBraceToken) is null ? declared.SemicolonToken : declared.OpenBraceToken;

            var node = Part(idName, CodeNodeKinds.Type, parent, declared, () => new ExtractNode(string.Empty, CodeNodeKinds.Type, name, qualified, Line(declared.SpanStart), EndLine(declared), Header(declared, opens))
            {
                SubKind = declared switch
                {
                    RecordDeclarationSyntax => "record",
                    ClassDeclarationSyntax => "class",
                    StructDeclarationSyntax => "struct",
                    InterfaceDeclarationSyntax => "interface",
                    EnumDeclarationSyntax => "enum",
                    TypeDeclarationSyntax other => other.Keyword.ValueText,
                    _ => null,
                },
                Scope = scope,
                Parent = parent?.Id,
                BodyStart = LineOf(declared.OpenBraceToken),
                Summary = Summary(declared),
                Modifiers = Modifiers(declared.Modifiers),
                Part = declared.Modifiers.Any(SyntaxKind.PartialKeyword),
            });

            Scope(qualified, scope, open: !local);
            Attributes(node, declared.AttributeLists, typeParameters);

            var isInterface = declared is InterfaceDeclarationSyntax;
            var isStruct = declared is StructDeclarationSyntax || (declared is RecordDeclarationSyntax record && record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword));
            var inherited = _inherited.GetValueOrDefault(node.Id);

            if (declared is not EnumDeclarationSyntax && declared.BaseList is { } bases)
            {
                for (var at = 0; at < bases.Types.Count; at++)
                {
                    var written = bases.Types[at].Type;

                    if (NameOf(written) is not { } target || typeParameters.Contains(target.Name))
                    {
                        continue;
                    }

                    // An interface extends its bases; a struct implements every one; a class inherits its first unless it
                    // is named as an interface is, and implements the rest.
                    var kind = isInterface ? CodeEdgeKinds.Inherits
                        : isStruct || at > 0 || LooksLikeInterface(target.Name) ? CodeEdgeKinds.Implements
                        : CodeEdgeKinds.Inherits;

                    Reference(node, kind, target, written);
                    Arguments(node, written, typeParameters);

                    if (kind == CodeEdgeKinds.Inherits && !isInterface)
                    {
                        inherited = _inherited[node.Id] = Plain(target.Name);

                        // A primary constructor passes its base's constructor its arguments.
                        if (bases.Types[at] is PrimaryConstructorBaseTypeSyntax primary)
                        {
                            Call(node, inherited, primary.ArgumentList.Arguments.Count, Line(primary.ArgumentList.SpanStart), new ExtractQualifier(CodeQualifierKinds.Base));
                        }
                    }
                }
            }

            if (declared is TypeDeclarationSyntax typed)
            {
                MentionParameters(node, typed.ParameterList, typeParameters);
                Constraints(node, typed.ConstraintClauses, typeParameters);
            }

            var frame = new TypeFrame(node, idName, local, identifier, inherited, isInterface, FieldTypes(declared, typeParameters), typeParameters)
            {
                Captured = Captured(declared, typeParameters),
            };

            // What the type's own head reads and holds - its attributes' arguments and what it passes its base's
            // constructor, where a primary constructor's parameter is the parameter - is the type's.
            Walk(node, frame, [.. declared.AttributeLists, .. declared.BaseList?.Types.OfType<PrimaryConstructorBaseTypeSyntax>().Select(primary => primary.ArgumentList) ?? []], typeParameters,
                scope: (declared as TypeDeclarationSyntax)?.ParameterList?.Parameters);

            // A record's positional parameters are its properties, but for one its body declares itself; what another
            // primary constructor's parameter holds - its attributes, its default - is the type's.
            if (declared is TypeDeclarationSyntax { ParameterList: { } primaryParameters })
            {
                var positional = declared as RecordDeclarationSyntax;
                var own = positional is not null ? FieldTypes(positional.WithParameterList(null), typeParameters) : null;

                foreach (var parameter in primaryParameters.Parameters)
                {
                    if (positional is not null && own is not null && parameter.Identifier.ValueText is { Length: > 0 } named && !own.ContainsKey(named))
                    {
                        Positional(parameter, positional, frame);
                    }
                    else
                    {
                        Walk(node, frame, [parameter], typeParameters);
                    }
                }
            }

            if (declared is EnumDeclarationSyntax enumeration)
            {
                foreach (var member in enumeration.Members)
                {
                    Member(member, frame);
                }
            }

            return declared is TypeDeclarationSyntax withMembers ? [.. withMembers.Members.Select(member => new Declaration(member, qualified, node, frame))] : [];
        }

        private void Delegate(DelegateDeclarationSyntax declared, QualifiedName? scope, ExtractNode? parent, TypeFrame? outer)
        {
            var identifier = declared.Identifier.ValueText;

            if (identifier.Length == 0)
            {
                return;
            }

            var name = Generic(identifier, declared.TypeParameterList?.Parameters.Count ?? 0);
            var qualified = ExtractText.Qualify(scope, name);
            var typeParameters = With(outer?.TypeParameters, declared.TypeParameterList);
            var (idName, local) = Named(outer, declared.Modifiers, name, qualified);

            var node = Add(new ExtractNode(Id(idName), CodeNodeKinds.Type, name, qualified, Line(declared.SpanStart), EndLine(declared), Header(declared, declared.SemicolonToken))
            {
                SubKind = "delegate",
                Scope = scope,
                Parent = parent?.Id,
                Summary = Summary(declared),
                Modifiers = Modifiers(declared.Modifiers),
            });

            // A file-local delegate's own scope, though nothing is declared in it, says it is its file's alone.
            if (local)
            {
                Scope(qualified, scope, open: false);
            }

            Attributes(node, declared.AttributeLists, typeParameters);
            Mention(node, declared.ReturnType, typeParameters);
            MentionParameters(node, declared.ParameterList, typeParameters);
            Constraints(node, declared.ConstraintClauses, typeParameters);
            Walk(node, outer, [declared], typeParameters);
        }

        /// <summary>A member of <paramref name="type"/>: a method, a constructor, a property, a field and the rest.</summary>
        private void Member(MemberDeclarationSyntax member, TypeFrame type)
        {
            switch (member)
            {
                case MethodDeclarationSyntax method:
                {
                    var identifier = method.Identifier.ValueText;
                    var typeParameters = With(type.TypeParameters, method.TypeParameterList);

                    // A classic extension method is called on its first parameter; an extension block's instance method is
                    // called on the block's receiver, which its static form takes first.
                    var first = method.ParameterList.Parameters.FirstOrDefault();
                    var extension = first?.Modifiers.Any(SyntaxKind.ThisKeyword) == true;
                    var received = type.Receiver is not null && !method.Modifiers.Any(SyntaxKind.StaticKeyword);

                    var node = Callable(method, type, identifier, $"{Explicit(method.ExplicitInterfaceSpecifier)}{Generic(identifier, method.TypeParameterList?.Parameters.Count ?? 0)}({ParameterTypes(method.ParameterList)})", "method", method.Body, method.ExpressionBody, Arity(method.ParameterList, extension ? 1 : 0, received ? 1 : 0), typeParameters, node => node with
                    {
                        Extends = extension ? Extended(first!.Type, method.ConstraintClauses, typeParameters) : received ? ValueKey(type.Receiver!, typeParameters) : null,
                    });

                    if (node is not null)
                    {
                        Mention(node, method.ReturnType, typeParameters);
                        Constraints(node, method.ConstraintClauses, typeParameters);
                    }

                    break;
                }

                case ConstructorDeclarationSyntax constructor:
                {
                    // A static constructor is run by the runtime alone: named so no call is linked to it.
                    var isStatic = constructor.Modifiers.Any(SyntaxKind.StaticKeyword);
                    var identifier = isStatic ? $"static {type.Identifier}" : type.Identifier;
                    var node = Callable(constructor, type, identifier, $"{identifier}({ParameterTypes(constructor.ParameterList)})", "constructor", constructor.Body, constructor.ExpressionBody, Arity(constructor.ParameterList, 0, 0), type.TypeParameters);

                    if (node is not null && constructor.Initializer is { } initializer)
                    {
                        var arity = initializer.ArgumentList.Arguments.Count;
                        var line = Line(initializer.ThisOrBaseKeyword.SpanStart);

                        if (initializer.IsKind(SyntaxKind.ThisConstructorInitializer))
                        {
                            Call(node, type.Identifier, arity, line, new ExtractQualifier(CodeQualifierKinds.This));
                        }
                        else if (type.Inherited is { } inherited)
                        {
                            Call(node, inherited, arity, line, new ExtractQualifier(CodeQualifierKinds.Base));
                        }
                    }

                    break;
                }

                case DestructorDeclarationSyntax destructor:
                    Callable(destructor, type, $"~{type.Identifier}", $"~{type.Identifier}()", "destructor", destructor.Body, destructor.ExpressionBody, (0, 0), type.TypeParameters);
                    break;

                case OperatorDeclarationSyntax operation:
                {
                    var name = $"operator {(operation.CheckedKeyword.IsKind(SyntaxKind.CheckedKeyword) ? "checked " : string.Empty)}{operation.OperatorToken.ValueText}";
                    var node = Callable(operation, type, name, $"{Explicit(operation.ExplicitInterfaceSpecifier)}{name}({ParameterTypes(operation.ParameterList)})", "operator", operation.Body, operation.ExpressionBody, Arity(operation.ParameterList, 0, 0), type.TypeParameters);

                    if (node is not null)
                    {
                        Mention(node, operation.ReturnType, type.TypeParameters);
                    }

                    break;
                }

                case ConversionOperatorDeclarationSyntax conversion:
                {
                    var name = $"{conversion.ImplicitOrExplicitKeyword.ValueText} operator {TypeText(conversion.Type)}";
                    var node = Callable(conversion, type, name, $"{Explicit(conversion.ExplicitInterfaceSpecifier)}{name}({ParameterTypes(conversion.ParameterList)})", "operator", conversion.Body, conversion.ExpressionBody, Arity(conversion.ParameterList, 0, 0), type.TypeParameters);

                    if (node is not null)
                    {
                        Mention(node, conversion.Type, type.TypeParameters);
                    }

                    break;
                }

                case PropertyDeclarationSyntax property:
                    Property(property, type, property.Identifier.ValueText, $"{Explicit(property.ExplicitInterfaceSpecifier)}{property.Identifier.ValueText}", "property", property.ExpressionBody, null);
                    break;

                case IndexerDeclarationSyntax indexer:
                    Property(indexer, type, "this[]", $"{Explicit(indexer.ExplicitInterfaceSpecifier)}this[{ParameterTypes(indexer.ParameterList)}]", "indexer", indexer.ExpressionBody, indexer.ParameterList);
                    break;

                case EventDeclarationSyntax declared:
                    Property(declared, type, declared.Identifier.ValueText, $"{Explicit(declared.ExplicitInterfaceSpecifier)}{declared.Identifier.ValueText}", "event", null, null);
                    break;

                case BaseFieldDeclarationSyntax field:
                    Fields(field, type);
                    break;

                case EnumMemberDeclarationSyntax constant when constant.Identifier.ValueText is { Length: > 0 } name:
                {
                    var node = Add(new ExtractNode(Id(ExtractText.Qualify(type.IdName, name)), CodeNodeKinds.Member, name, ExtractText.Qualify(type.Node.Qualified, name), Line(constant.SpanStart), EndLine(constant), Header(constant, default))
                    {
                        SubKind = "constant",
                        Scope = type.Node.Qualified,
                        Parent = type.Node.Id,
                        Summary = Summary(constant),
                        ValueType = type.Node.Name,
                    });

                    Attributes(node, constant.AttributeLists, type.TypeParameters);
                    Walk(node, type, [constant], type.TypeParameters);
                    break;
                }
            }
        }

        /// <summary>
        /// A member with parameters and a body - a method, a constructor, a destructor, an operator - named
        /// <paramref name="name"/> and identified by <paramref name="tail"/> after its type's qualified name: a prototype
        /// where it has no body (abstract, an interface's, extern, the declaring half of a partial method).
        /// </summary>
        private ExtractNode? Callable(BaseMethodDeclarationSyntax member, TypeFrame type, string name, string tail, string subKind, BlockSyntax? body, ArrowExpressionClauseSyntax? arrow, (int? Min, int? Max) arity, IReadOnlySet<string> typeParameters, Func<ExtractNode, ExtractNode>? more = null)
        {
            if (name.Length == 0 || name.EndsWith(' '))
            {
                return null;
            }

            var opens = body is not null ? body.OpenBraceToken : arrow is not null ? arrow.ArrowToken : member.SemicolonToken;
            var made = new ExtractNode(MemberId(type, tail), CodeNodeKinds.Member, name, ExtractText.Qualify(type.Node.Qualified, name), Line(member.SpanStart), EndLine(member), Header(member, opens))
            {
                SubKind = subKind,
                Scope = type.Node.Qualified,
                Parent = type.Node.Id,
                BodyStart = body is not null ? LineOf(body.OpenBraceToken) : arrow is not null ? LineOf(arrow.ArrowToken) : null,
                Summary = Summary(member),
                Modifiers = Modifiers(member.Modifiers),
                MinArity = arity.Min,
                MaxArity = arity.Max,
                Prototype = body is null && arrow is null,
                Test = IsTest(member.AttributeLists),
            };

            var node = Add(more is null ? made : more(made));

            Attributes(node, member.AttributeLists, typeParameters);
            MentionParameters(node, member.ParameterList, typeParameters);
            Mention(node, type.Receiver, typeParameters);
            Overrides(node, member.Modifiers, member.ParameterList.Parameters.Count, Line(member is MethodDeclarationSyntax method ? method.Identifier.SpanStart : member.SpanStart));
            Walk(node, type, [member], typeParameters);
            return node;
        }

        /// <summary>
        /// A property, an indexer or an event with accessors: a prototype where neither it nor an accessor has a body and it
        /// is abstract, extern, an interface's or the declaring half of a partial one - an automatic property has a body
        /// the compiler writes.
        /// </summary>
        private void Property(BasePropertyDeclarationSyntax member, TypeFrame type, string name, string tail, string subKind, ArrowExpressionClauseSyntax? arrow, BracketedParameterListSyntax? parameters)
        {
            if (name.Length == 0 || tail.EndsWith('.'))
            {
                return;
            }

            var accessors = member.AccessorList;
            var bodiless = arrow is null && (accessors?.Accessors.All(accessor => accessor.Body is null && accessor.ExpressionBody is null) ?? true);
            var declaredOnly = member.Modifiers.Any(SyntaxKind.AbstractKeyword) || member.Modifiers.Any(SyntaxKind.ExternKeyword) || member.Modifiers.Any(SyntaxKind.PartialKeyword) || (type.Interface && !member.Modifiers.Any(SyntaxKind.StaticKeyword));
            var opens = accessors is not null ? accessors.OpenBraceToken : arrow?.ArrowToken ?? default;
            var arity = parameters is null ? (null, null) : Arity(parameters, 0, 0);

            var node = Add(new ExtractNode(MemberId(type, tail), CodeNodeKinds.Member, name, ExtractText.Qualify(type.Node.Qualified, name), Line(member.SpanStart), EndLine(member), Header(member, opens))
            {
                SubKind = subKind,
                Scope = type.Node.Qualified,
                Parent = type.Node.Id,
                BodyStart = accessors is not null ? LineOf(accessors.OpenBraceToken) : arrow is not null ? LineOf(arrow.ArrowToken) : null,
                Summary = Summary(member),
                Modifiers = Modifiers(member.Modifiers),
                MinArity = arity.Min,
                MaxArity = arity.Max,
                Prototype = bodiless && declaredOnly,
                ValueType = parameters is null ? NodeValueType(member.Type, type.TypeParameters) : null,
                Extends = type.Receiver is { } receiver && !member.Modifiers.Any(SyntaxKind.StaticKeyword) ? ValueKey(receiver, type.TypeParameters) : null,
            });

            Attributes(node, member.AttributeLists, type.TypeParameters);
            Mention(node, member.Type, type.TypeParameters);
            Mention(node, type.Receiver, type.TypeParameters);
            MentionParameters(node, parameters, type.TypeParameters);
            Overrides(node, member.Modifiers, null, Line(member.SpanStart));
            Walk(node, type, [member], type.TypeParameters);
        }

        /// <summary>Each variable of a field, constant or event field declaration, a node of its own over the declaration's lines.</summary>
        private void Fields(BaseFieldDeclarationSyntax field, TypeFrame type)
        {
            var subKind = field is EventFieldDeclarationSyntax ? "event" : field.Modifiers.Any(SyntaxKind.ConstKeyword) ? "constant" : "field";

            // The declaration's attributes are read once, by its first variable's node.
            IEnumerable<SyntaxNode> attributes = field.AttributeLists;

            foreach (var variable in field.Declaration.Variables)
            {
                var name = variable.Identifier.ValueText;

                if (name.Length == 0)
                {
                    continue;
                }

                var node = Add(new ExtractNode(Id(ExtractText.Qualify(type.IdName, name)), CodeNodeKinds.Member, name, ExtractText.Qualify(type.Node.Qualified, name), Line(field.SpanStart), EndLine(field), ExtractText.Signature(ExtractText.Flat($"{Header(field, field.Declaration.Variables[0].Identifier)} {name}")))
                {
                    SubKind = subKind,
                    Scope = type.Node.Qualified,
                    Parent = type.Node.Id,
                    Summary = Summary(field),
                    Modifiers = Modifiers(field.Modifiers),
                    Prototype = subKind == "event" && type.Interface && !field.Modifiers.Any(SyntaxKind.StaticKeyword),
                    ValueType = NodeValueType(field.Declaration.Type, type.TypeParameters),
                });

                Attributes(node, field.AttributeLists, type.TypeParameters);
                Mention(node, field.Declaration.Type, type.TypeParameters);
                Walk(node, type, [.. attributes, variable], type.TypeParameters);
                attributes = [];
            }
        }

        /// <summary>
        /// The statements at a file's top - a program with no <c>Main</c> - as the method the compiler makes of them,
        /// <c>Program.&lt;Main&gt;$</c>, so what they call is linked from it; its id holds the file's path, since every
        /// program of a repository has one.
        /// </summary>
        private void TopLevel(IReadOnlyList<GlobalStatementSyntax> statements)
        {
            const string Program = "Program";
            const string Main = "<Main>$";

            var node = Add(new ExtractNode(Id($"{path}:{Program}.{Main}(string[])"), CodeNodeKinds.Member, Main, $"{Program}.{Main}", Line(statements[0].SpanStart), EndLine(statements[^1]), "top-level statements")
            {
                SubKind = "method",
                Scope = Program,
            });

            Walk(node, type: null, statements, new HashSet<string>(StringComparer.Ordinal));
        }

        /// <summary>
        /// The node of a namespace or a type whose id <paramref name="idName"/> makes, declared by <paramref name="declared"/>: a
        /// new one where none of this file is yet, else - for a later part of a partial type, or a later block of one
        /// namespace, in the same place - the first one, its lines running on to this part's last and its summary the
        /// first said: two nodes of one would make every reference to it name both.
        /// </summary>
        private ExtractNode Part(QualifiedName idName, string kind, ExtractNode? parent, SyntaxNode declared, Func<ExtractNode> made)
        {
            var key = (idName.Prepend(prefix), kind, parent?.Id);

            if (!_parts.TryGetValue(key, out var at))
            {
                var node = Add(made() with { Id = Id(idName) });

                // One told apart from an earlier one by its place is no part of it, nor a later one's.
                if (node.Id == key.Item1)
                {
                    _parts[key] = _nodes.Count - 1;
                }

                return node;
            }

            var first = _nodes[at];
            return _nodes[at] = first with { End = Math.Max(first.End, EndLine(declared)), Summary = first.Summary ?? Summary(declared) };
        }

        /// <summary>
        /// A positional parameter of <paramref name="record"/>, the property the compiler makes of it: over the parameter's
        /// own lines, its summary the record's documentation of it.
        /// </summary>
        private void Positional(ParameterSyntax parameter, RecordDeclarationSyntax record, TypeFrame type)
        {
            var name = parameter.Identifier.ValueText;
            var node = Add(new ExtractNode(MemberId(type, name), CodeNodeKinds.Member, name, ExtractText.Qualify(type.Node.Qualified, name), Line(parameter.SpanStart), EndLine(parameter), ExtractText.Signature(ExtractText.Flat(parameter.ToString())))
            {
                SubKind = "property",
                Scope = type.Node.Qualified,
                Parent = type.Node.Id,
                Summary = Summary(record, parameter: name),
                ValueType = parameter.Type is { } written ? NodeValueType(written, type.TypeParameters) : null,
            });

            Mention(node, parameter.Type, type.TypeParameters);
            Walk(node, type, [parameter], type.TypeParameters);
        }

        /// <summary>
        /// What <paramref name="code"/> calls, makes and names, from <paramref name="from"/>: each invocation with its
        /// receiver's kind, each <c>new</c> - a target-typed one by the type it is given to - and each type a local
        /// variable, a <c>typeof</c>, a cast, an <c>as</c>, an <c>is</c> or a pattern names. <paramref name="scope"/> are
        /// parameters the code sees but is not part of: a primary constructor's, to its base's arguments.
        /// </summary>
        private void Walk(ExtractNode from, TypeFrame? type, IEnumerable<SyntaxNode> code, IReadOnlySet<string> typeParameters, IEnumerable<ParameterSyntax>? scope = null)
        {
            var nodes = code.SelectMany(part => part.DescendantNodesAndSelf()).ToList();
            var walk = new Walked(from, nodes, new Known(nodes, scope, typeParameters, type?.Fields ?? new Dictionary<string, string?>(StringComparer.Ordinal), type?.Captured ?? new HashSet<string>(StringComparer.Ordinal)));

            foreach (var node in nodes)
            {
                walk.Known.At(node.FullSpan.Start);

                switch (node)
                {
                    case InvocationExpressionSyntax invocation:
                        Invocation(invocation, walk);
                        break;

                    case ObjectCreationExpressionSyntax created when NameOf(created.Type) is { } made && !typeParameters.Contains(made.Name):
                        Reference(from, CodeEdgeKinds.Instantiates, made, created.Type);
                        Arguments(from, created.Type, typeParameters);
                        break;

                    case ImplicitObjectCreationExpressionSyntax created when TargetType(created) is { } target && NameOf(target) is { } made && !typeParameters.Contains(made.Name):
                        Reference(from, CodeEdgeKinds.Instantiates, made, created);
                        break;

                    case VariableDeclarationSyntax declaration when declaration.Parent is not BaseFieldDeclarationSyntax:
                        Mention(from, declaration.Type, typeParameters);
                        break;

                    case ForEachStatementSyntax loop:
                        Mention(from, loop.Type, typeParameters);
                        break;

                    case CatchDeclarationSyntax caught:
                        Mention(from, caught.Type, typeParameters);
                        break;

                    case DeclarationExpressionSyntax declared:
                        Mention(from, declared.Type, typeParameters);
                        break;

                    case TypeOfExpressionSyntax typeOf:
                        Mention(from, typeOf.Type, typeParameters);
                        break;

                    case CastExpressionSyntax cast:
                        Mention(from, cast.Type, typeParameters);
                        break;

                    case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AsExpression) || binary.IsKind(SyntaxKind.IsExpression):
                        Mention(from, binary.Right as TypeSyntax, typeParameters);

                        // x is State.Done tests a constant as often as it does a nested type, which the syntax cannot
                        // tell apart: a use of its last name too, qualified by the rest, as the pattern x is State.Done
                        // or State.Failed reads each.
                        if (binary.IsKind(SyntaxKind.IsExpression) && binary.Right is QualifiedNameSyntax constant)
                        {
                            Used(from, constant.Right, Receiver(constant.Left, walk));
                        }

                        break;

                    case DeclarationPatternSyntax pattern:
                        Mention(from, pattern.Type, typeParameters);
                        break;

                    case TypePatternSyntax pattern:
                        Mention(from, pattern.Type, typeParameters);
                        break;

                    case RecursivePatternSyntax pattern:
                        Mention(from, pattern.Type, typeParameters);
                        break;

                    // A member read or written without a call of it - x.Y - and x?.Y: a use of the member, qualified by
                    // what its receiver is where the file says. A member of an invoked access is the call's, not a use; one
                    // a property pattern's path names past its first, a member of a value of no type the file says.
                    case MemberAccessExpressionSyntax access when access.IsKind(SyntaxKind.SimpleMemberAccessExpression) && access.Parent is not InvocationExpressionSyntax:
                        Use(access, walk);
                        break;

                    case MemberBindingExpressionSyntax binding when binding.Parent is not InvocationExpressionSyntax
                        && binding.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault(holder => holder.WhenNotNull.Span.Contains(binding.Span)) is { } conditional:
                        Used(from, binding.Name, Receiver(conditional.Expression, walk));
                        break;

                    // A member an object or a with initializer sets: a use of the made type's member, not the enclosing one's.
                    case AssignmentExpressionSyntax { Left: IdentifierNameSyntax member, Parent: InitializerExpressionSyntax initializer }
                        when initializer.IsKind(SyntaxKind.ObjectInitializerExpression) || initializer.IsKind(SyntaxKind.WithInitializerExpression):
                        Used(from, member, Initialized(initializer, walk));
                        break;

                    // A member a property pattern reads - { Name: ... } - of the type the pattern tests.
                    case SubpatternSyntax { NameColon.Name: IdentifierNameSyntax member, Parent: PropertyPatternClauseSyntax { Parent: RecursivePatternSyntax pattern } }:
                        Used(from, member, Tested(pattern, walk));
                        break;

                    // A member an attribute's named argument sets - [Display(Name = "x")] - of the attribute's class.
                    case AttributeArgumentSyntax { NameEquals.Name: IdentifierNameSyntax member, Parent.Parent: AttributeSyntax attribute }:
                        Used(from, member, NameOf(attribute.Name) is { } named ? new ExtractQualifier(CodeQualifierKinds.Type, ExtractText.Qualify(named.Qualifier is null ? null : QualifiedName.Of(named.Qualifier), AttributeClass(named.Name))) : new ExtractQualifier(CodeQualifierKinds.Value));
                        break;

                    case IdentifierNameSyntax read when Readable(read, walk, typeParameters):
                        Used(from, read, PatternOfPath(read, walk) is { } tested ? Tested(tested, walk) : null);
                        break;

                    case LiteralExpressionSyntax or InterpolatedStringExpressionSyntax:
                        Literal(node, from.Id);
                        break;
                }
            }
        }

        /// <summary>
        /// The string literal <paramref name="node"/> is, its value decoded as the compiler reads it - a raw one's as written -
        /// or an interpolated string's constant parts, each a literal of its own, its holes code read as code: kept as held by
        /// the node of <paramref name="holder"/>, none where the file's own holds it.
        /// </summary>
        private void Literal(SyntaxNode node, QualifiedName? holder)
        {
            switch (node)
            {
                case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) || literal.IsKind(SyntaxKind.Utf8StringLiteralExpression):
                    // A raw string over lines begins its value on the line after its quotes.
                    _literals.Add(literal.Token.ValueText, Line(literal.SpanStart) + (literal.Token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken) || literal.Token.IsKind(SyntaxKind.Utf8MultiLineRawStringLiteralToken) ? 1 : 0), holder);
                    break;

                case InterpolatedStringExpressionSyntax interpolated:
                    foreach (var part in interpolated.Contents.OfType<InterpolatedStringTextSyntax>())
                    {
                        _literals.Add(part.TextToken.ValueText, Line(part.SpanStart), holder);
                    }

                    break;
            }
        }

        /// <summary>
        /// A member read or written without a call of it: a use of the member by its name, qualified by
        /// <paramref name="qualifier"/> - what its receiver is where the file says; none where it is read by its name alone.
        /// Answers where among the references it is; none where the name is empty and no use is made.
        /// </summary>
        private int? Used(ExtractNode from, SimpleNameSyntax name, ExtractQualifier? qualifier)
        {
            var member = name.Identifier.ValueText;

            if (member.Length == 0)
            {
                return null;
            }

            _references.Add(new ExtractReference(from.Id, CodeEdgeKinds.Uses, member, Line(name.SpanStart)) { Qualifier = qualifier });
            return _references.Count - 1;
        }

        /// <summary>
        /// The use made for the member <paramref name="access"/> reads - <c>x.Y</c> - and whether its qualifier writes the
        /// names of its receiver's chain, by a name or a link; none where the walk makes none of it: an invoked access's
        /// member is the call's. Each is made once, as the walk first needs it: the uses of a chain's members before it are
        /// made first, the innermost first, so the use of each name is made before - and links to - the one before it, and a
        /// chain of any length is walked once.
        /// </summary>
        private (int Index, bool Writes)? Use(MemberAccessExpressionSyntax access, Walked walk)
        {
            var pending = new Stack<MemberAccessExpressionSyntax>();

            for (MemberAccessExpressionSyntax? at = access; at is not null && !walk.Uses.ContainsKey(at); at = Inner(at))
            {
                pending.Push(at);
            }

            while (pending.TryPop(out var next))
            {
                walk.Uses[next] = Made(next);
            }

            return walk.Uses[access];

            // The member access a chain's receiver is, past its parentheses and its null-forgiving marks.
            static MemberAccessExpressionSyntax? Inner(MemberAccessExpressionSyntax outer)
            {
                var receiver = outer.Expression;

                while (receiver is ParenthesizedExpressionSyntax or PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression })
                {
                    receiver = receiver is ParenthesizedExpressionSyntax parenthesized ? parenthesized.Expression : ((PostfixUnaryExpressionSyntax)receiver).Operand;
                }

                return receiver as MemberAccessExpressionSyntax;
            }

            (int Index, bool Writes)? Made(MemberAccessExpressionSyntax member)
            {
                if (!walk.Nodes.Contains(member) || !member.IsKind(SyntaxKind.SimpleMemberAccessExpression) || member.Parent is InvocationExpressionSyntax)
                {
                    return null;
                }

                var writes = false;
                var qualifier = PatternOfPath(member, walk) is null ? Receiver(member.Expression, walk, out writes) : new ExtractQualifier(CodeQualifierKinds.Value);

                return Used(walk.From, member.Name, qualifier) is { } index ? (index, writes) : null;
            }
        }

        /// <summary>
        /// Whether <paramref name="read"/> names a value the member reads by the name alone - the first of a receiver's chain
        /// among them, where it is a field of its type's, a name no capital begins, the member a property pattern's path
        /// begins with, or what a member assigned is reached through, a capital else beginning the name of a type or a
        /// namespace a member is reached through: not a call's callee, not a member of a receiver, nothing the compiler reads
        /// as a type or a namespace, no label a <c>goto</c> names, no name a named argument, an initializer, an anonymous
        /// type, a tuple or a pattern declares or sets - those of an initializer and a pattern are read as their type's - no
        /// discard, and none of its own locals, parameters, local functions or type parameters where it stands, nor a primary
        /// constructor's parameter or an extension block's receiver, of which no member is declared. The syntax alone cannot
        /// tell a field of another type from a value the language itself holds, and a use of either is what the graph
        /// wants, resolved or not.
        /// </summary>
        private static bool Readable(IdentifierNameSyntax read, Walked walk, IReadOnlySet<string> typeParameters)
        {
            var name = read.Identifier.ValueText;
            var known = walk.Known;

            if (name.Length == 0 || name == "value" || typeParameters.Contains(name) || known.Function(name) || known.Local(name, out _) || known.Captured.Contains(name)
                || (name == "_" && !known.Fields.ContainsKey(name))
                || SyntaxFacts.IsInNamespaceOrTypeContext(read))
            {
                return false;
            }

            return read.Parent switch
            {
                InvocationExpressionSyntax or MemberBindingExpressionSyntax or NameColonSyntax or NameEqualsSyntax or QualifiedNameSyntax or AliasQualifiedNameSyntax => false,

                // A label a goto names is no value; a goto case's constant is one.
                GotoStatementSyntax jump => !jump.IsKind(SyntaxKind.GotoStatement),

                // A chain's first name is read where it is a value: a field of its type's, a name no type's is, the member
                // of the tested type a property pattern's path begins with - { Owner.Name: ... } - or what a member is
                // assigned through - Checkout.IsEnabled = true, a control a markup file declares - a value's as often as
                // a type's, and the use is what reaches the field.
                MemberAccessExpressionSyntax access => ReferenceEquals(access.Expression, read)
                    && (known.Fields.ContainsKey(name) || !char.IsUpper(name[0]) || PatternOfPath(read, walk) is not null
                        || (Top(read, walk) is var top && top.Parent is AssignmentExpressionSyntax assignment && ReferenceEquals(assignment.Left, top))),
                AssignmentExpressionSyntax { Parent: InitializerExpressionSyntax initializer } assignment => !ReferenceEquals(assignment.Left, read)
                    || !(initializer.IsKind(SyntaxKind.ObjectInitializerExpression) || initializer.IsKind(SyntaxKind.WithInitializerExpression)),
                _ => true,
            };
        }

        /// <summary>
        /// What the members <paramref name="initializer"/> sets are qualified by: the type a <c>new</c> makes - a
        /// target-typed one's where the code says it - the value a <c>with</c> copies; a value of no type the file says for
        /// an initializer of a member's value.
        /// </summary>
        private ExtractQualifier Initialized(InitializerExpressionSyntax initializer, Walked walk) => initializer.Parent switch
        {
            ObjectCreationExpressionSyntax created => Receiver(created, walk),
            ImplicitObjectCreationExpressionSyntax created when TargetType(created) is { } target && Key(target) is { } made => new ExtractQualifier(CodeQualifierKinds.Type, made),
            WithExpressionSyntax copied => Receiver(copied.Expression, walk),
            _ => new ExtractQualifier(CodeQualifierKinds.Value),
        };

        /// <summary>
        /// What the members <paramref name="pattern"/> reads are qualified by: the type it names, else what it tests - an
        /// <c>is</c>'s operand, a switch's governing value - and a value of no type the file says for a pattern nested in
        /// another's.
        /// </summary>
        private ExtractQualifier Tested(RecursivePatternSyntax pattern, Walked walk)
        {
            if (pattern.Type is { } type)
            {
                return Key(type) is { } named ? new ExtractQualifier(CodeQualifierKinds.Type, named) : new ExtractQualifier(CodeQualifierKinds.Value);
            }

            return pattern.Parent switch
            {
                IsPatternExpressionSyntax tested => Receiver(tested.Expression, walk),
                SwitchExpressionArmSyntax { Parent: SwitchExpressionSyntax switched } => Receiver(switched.GoverningExpression, walk),
                CasePatternSwitchLabelSyntax { Parent.Parent: SwitchStatementSyntax switched } => Receiver(switched.Expression, walk),
                _ => new ExtractQualifier(CodeQualifierKinds.Value),
            };
        }

        /// <summary>
        /// The property pattern whose path - what an extended pattern writes before its colon, <c>{ A.B: 1 }</c> - is
        /// <paramref name="node"/> or holds it as the receiver of a member it names, however long the path; <see langword="null"/>
        /// where none does. The path's first name is a member of what the pattern tests.
        /// </summary>
        private static RecursivePatternSyntax? PatternOfPath(ExpressionSyntax node, Walked walk)
            => Top(node, walk) is var top && top.Parent is ExpressionColonSyntax { Parent: SubpatternSyntax { Parent: PropertyPatternClauseSyntax { Parent: RecursivePatternSyntax pattern } } } colon && ReferenceEquals(colon.Expression, top)
                ? pattern
                : null;

        /// <summary>
        /// The member access whose chain <paramref name="node"/> begins or is part of - the last of those it is the receiver
        /// of, in turn - or the node itself where it is the receiver of none: climbed once for each node, and kept, so a chain
        /// of any length is climbed once.
        /// </summary>
        private static SyntaxNode Top(ExpressionSyntax node, Walked walk)
        {
            var climbed = walk.Climbed;
            SyntaxNode at = node;

            climbed.Clear();

            while (!walk.Tops.ContainsKey(at))
            {
                if (at.Parent is not MemberAccessExpressionSyntax access || !ReferenceEquals(access.Expression, at))
                {
                    break;
                }

                climbed.Add(at);
                at = access;
            }

            var top = walk.Tops.GetValueOrDefault(at, at);

            foreach (var each in climbed)
            {
                walk.Tops[each] = top;
            }

            return top;
        }

        /// <summary>One invocation: a call by its name, its argument count and its receiver, or a mark where it cannot be bound.</summary>
        private void Invocation(InvocationExpressionSyntax invocation, Walked walk)
        {
            var arity = invocation.ArgumentList.Arguments.Count;

            switch (invocation.Expression)
            {
                case IdentifierNameSyntax called:
                {
                    var name = called.Identifier.ValueText;

                    // nameof names and calls nothing; a local function's calls are its member's own.
                    if (name == "nameof" || walk.Known.Function(name))
                    {
                        return;
                    }

                    if (walk.Known.TryGet(name, out var declared))
                    {
                        Dynamic(called, declared == DynamicType ? $"a call on '{name}', which is dynamic" : $"a delegate invoked through '{name}'");
                        return;
                    }

                    Call(walk.From, name, arity, Line(called.SpanStart), qualifier: null);
                    break;
                }

                case GenericNameSyntax called when !walk.Known.Function(called.Identifier.ValueText):
                    Call(walk.From, called.Identifier.ValueText, arity, Line(called.SpanStart), qualifier: null);
                    break;

                case MemberAccessExpressionSyntax access when access.IsKind(SyntaxKind.SimpleMemberAccessExpression):
                    Accessed(invocation, access.Expression, access.Name, walk);
                    break;

                // x?.Call(): the receiver is what the conditional access reads.
                case MemberBindingExpressionSyntax binding:
                    Accessed(invocation, invocation.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault(access => access.WhenNotNull.Span.Contains(invocation.Span))?.Expression, binding.Name, walk);
                    break;
            }
        }

        /// <summary>A call of <paramref name="name"/> on <paramref name="receiver"/>, qualified by what the receiver is where the file says, or marked where it cannot be bound.</summary>
        private void Accessed(InvocationExpressionSyntax invocation, ExpressionSyntax? receiver, SimpleNameSyntax name, Walked walk)
        {
            var member = name.Identifier.ValueText;

            if (member.Length == 0)
            {
                return;
            }

            var arity = invocation.ArgumentList.Arguments.Count;
            var line = Line(name.SpanStart);

            if (Reflection.Contains(member) && invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression is { } named && !IsNameOf(named))
            {
                Dynamic(name, $"a member named by a string to reflection ({member})");
            }

            if (member == "CreateInstance" && name is not GenericNameSyntax && Written(receiver) is "Activator" or "System.Activator")
            {
                Dynamic(name, "a type made by Activator.CreateInstance");
            }

            if (DynamicRoot(receiver, walk) is { } loose)
            {
                Dynamic(name, $"a call on '{loose}', which is dynamic");
                return;
            }

            if (member == "Invoke" && ChainOf(receiver, walk) is { Count: 1 } value && walk.Known.TryGet(value.Head, out var declared) && declared is not null && IsDelegate(declared))
            {
                Dynamic(name, $"a delegate invoked through '{value.Head}'");
                return;
            }

            Call(walk.From, member, arity, line, Receiver(receiver, walk));
        }

        /// <summary>What a call's or a use's <paramref name="receiver"/> is: <see cref="Receiver(ExpressionSyntax?, Walked, out bool)"/>.</summary>
        private ExtractQualifier Receiver(ExpressionSyntax? receiver, Walked walk) => Receiver(receiver, walk, out _);

        /// <summary>
        /// What a call's or a use's <paramref name="receiver"/> is - the one owner of it for both: <c>this</c> and
        /// <c>base</c>; a type it is made or cast as; a variable, parameter or field the member reads, by the type it is
        /// declared with - each a type qualifier - a chain of members past a variable or a field, of any length, for the
        /// linker to follow through the type of each, or a value of no said type; a name no variable has, as a named
        /// qualifier - a type's, a namespace's or a value's - where it begins with a capital letter, else as a value of
        /// another file by its name; anything else a value of no said type. A chain of two names or more is written as a link
        /// to the use of its last (<see cref="Chained"/>).
        /// <paramref name="writes"/> says whether the qualifier writes the receiver's chain - its names, by a name or a link.
        /// </summary>
        private ExtractQualifier Receiver(ExpressionSyntax? receiver, Walked walk, out bool writes)
        {
            writes = false;

            switch (receiver)
            {
                case ThisExpressionSyntax:
                    return new ExtractQualifier(CodeQualifierKinds.This);

                case BaseExpressionSyntax:
                    return new ExtractQualifier(CodeQualifierKinds.Base);

                case ObjectCreationExpressionSyntax created when NameOf(created.Type) is { } made:
                    return new ExtractQualifier(CodeQualifierKinds.Type, ExtractText.Qualify(made.Qualifier is null ? null : QualifiedName.Of(made.Qualifier), made.Name));

                case ParenthesizedExpressionSyntax { Expression: CastExpressionSyntax cast } when Key(cast.Type) is { } to:
                    return new ExtractQualifier(CodeQualifierKinds.Type, to);

                case PredefinedTypeSyntax predefined:
                    return new ExtractQualifier(CodeQualifierKinds.Type, predefined.Keyword.ValueText);
            }

            if (ChainOf(receiver, walk) is not { } chain)
            {
                return new ExtractQualifier(CodeQualifierKinds.Value);
            }

            // A field named through this is the field, whatever variable the member names alike. A variable of a declared type
            // is a value of that type: its one name's qualifier the type, and a chain of any length past it a link to the use
            // of its last name, the use of its first qualified by the type - so the linker reads each name through the type of
            // the one before, as it reads a field's.
            if (!chain.ThroughThis && walk.Known.Local(chain.Head, out var local))
            {
                if (local is null)
                {
                    return new ExtractQualifier(CodeQualifierKinds.Value);
                }

                writes = true;
                return chain.Count == 1 ? new ExtractQualifier(CodeQualifierKinds.Type, local) : Chained(CodeQualifierKinds.Value, receiver!, chain, walk);
            }

            if (walk.Known.Fields.TryGetValue(chain.Head, out var field) && field is not null && chain.Count == 1)
            {
                return new ExtractQualifier(CodeQualifierKinds.Type, field);
            }

            writes = true;

            // A name given type arguments is a type's, and no value's: a type's chain is written out, as only a value's and
            // a name's are linked.
            return walk.Known.Fields.ContainsKey(chain.Head) || !char.IsUpper(chain.Head[0]) || chain.ThroughThis ? Chained(CodeQualifierKinds.Value, receiver!, chain, walk)
                : chain.Generic ? new ExtractQualifier(CodeQualifierKinds.Type, ChainText(receiver!, walk))
                : Chained(CodeQualifierKinds.Named, receiver!, chain, walk);
        }

        /// <summary>
        /// A chain's qualifier of <paramref name="kind"/>: a link to the use made for its last name, where it writes two
        /// names or more and that use's own qualifier writes the names before it (<see cref="ExtractQualifier.Link"/>); else
        /// its names written out - a single name, or a chain whose last name's use does not write the ones before it: a
        /// field's of a declared type, one generic name's, a qualified name's an <c>is</c> tests.
        /// </summary>
        private ExtractQualifier Chained(string kind, ExpressionSyntax receiver, Chain chain, Walked walk)
            => chain is { Count: > 1, Access: { Name: IdentifierNameSyntax } access } && Use(access, walk) is { Writes: true } use
                ? new ExtractQualifier(kind) { Link = use.Index }
                : new ExtractQualifier(kind, ChainText(receiver, walk));

        /// <summary>
        /// The variable or cast a receiver's chain begins at, where it is <c>dynamic</c>: what the call is on, written as the
        /// code names it; <see langword="null"/> where it is not. Each node's answer is kept, so a chain of any length is read
        /// once, as <see cref="Receiver(ExpressionSyntax?, Walked, out bool)"/> reads it.
        /// </summary>
        private static string? DynamicRoot(ExpressionSyntax? receiver, Walked walk)
        {
            var down = walk.Rooted;
            string? root = null;

            down.Clear();

            for (var at = receiver; at is not null;)
            {
                if (walk.Roots.TryGetValue(at, out var known))
                {
                    root = known;
                    break;
                }

                down.Add(at);

                switch (at)
                {
                    case MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } access:
                        root = walk.Known.Fields.TryGetValue(access.Name.Identifier.ValueText, out var field) && field == DynamicType ? access.Name.Identifier.ValueText : null;
                        break;

                    case MemberAccessExpressionSyntax access:
                        at = access.Expression;
                        continue;

                    case ElementAccessExpressionSyntax element:
                        at = element.Expression;
                        continue;

                    case InvocationExpressionSyntax invocation:
                        at = invocation.Expression;
                        continue;

                    case ConditionalAccessExpressionSyntax conditional:
                        at = conditional.Expression;
                        continue;

                    case PostfixUnaryExpressionSyntax postfix:
                        at = postfix.Operand;
                        continue;

                    case ParenthesizedExpressionSyntax { Expression: CastExpressionSyntax cast } when Key(cast.Type) == DynamicType:
                        root = $"({DynamicType})";
                        break;

                    case ParenthesizedExpressionSyntax parenthesized:
                        at = parenthesized.Expression;
                        continue;

                    case IdentifierNameSyntax identifier:
                        root = walk.Known.TryGet(identifier.Identifier.ValueText, out var declared) && declared == DynamicType ? identifier.Identifier.ValueText : null;
                        break;
                }

                break;
            }

            foreach (var each in down)
            {
                walk.Roots[each] = root;
            }

            return root;
        }

        /// <summary>
        /// The chain of names <paramref name="receiver"/> writes - an identifier, a type's name, members of it, with
        /// <c>this.</c> before it left out and said - or <see langword="null"/> where it is anything else: read from its
        /// end, each node's chain its receiver's with a name more, and kept, so a chain of any length is read once.
        /// </summary>
        private static Chain? ChainOf(ExpressionSyntax? receiver, Walked walk)
        {
            if (receiver is null)
            {
                return null;
            }

            // Down to the chain's first name, or to a node read before.
            var down = walk.Down;
            Chain? below = null;
            var ended = false;
            var at = receiver;

            down.Clear();

            while (!walk.Chains.TryGetValue(at, out below))
            {
                down.Add(at);

                if (Through(at) is not { } next)
                {
                    ended = true;
                    break;
                }

                at = next;
            }

            // Up from it, each node's chain made of the one below.
            for (var index = down.Count - 1; index >= 0; index--)
            {
                var node = down[index];
                var inner = ended && index == down.Count - 1 ? null : index + 1 < down.Count ? walk.Chains[down[index + 1]] : below;

                walk.Chains[node] = node switch
                {
                    IdentifierNameSyntax identifier when identifier.Identifier.ValueText.Length > 0 => new Chain(identifier.Identifier.ValueText, 1, false, false, identifier.Identifier.ValueText, null),
                    GenericNameSyntax named when named.Identifier.ValueText.Length > 0 => new Chain(Generic(named.Identifier.ValueText, named.TypeArgumentList.Arguments.Count), 1, false, true, Generic(named.Identifier.ValueText, named.TypeArgumentList.Arguments.Count), null),
                    MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } access when access.IsKind(SyntaxKind.SimpleMemberAccessExpression) && access.Name.Identifier.ValueText.Length > 0
                        => new Chain(access.Name.Identifier.ValueText, 1, true, false, access.Name.Identifier.ValueText, null),
                    AliasQualifiedNameSyntax or PostfixUnaryExpressionSyntax or ParenthesizedExpressionSyntax => inner,
                    QualifiedNameSyntax qualified when inner is not null => new Chain(inner.Head, inner.Count + 1, inner.ThroughThis, inner.Generic || qualified.Right is GenericNameSyntax, SimpleName(qualified.Right), qualified.Left),
                    MemberAccessExpressionSyntax access when inner is not null => new Chain(inner.Head, inner.Count + 1, inner.ThroughThis, inner.Generic || access.Name is GenericNameSyntax, SimpleName(access.Name), access.Expression) { Access = access },
                    _ => null,
                };
            }

            return walk.Chains[receiver];

            // The node a chain's node is read on from: what `x!`, `(x)` and `global::x` are, the receiver of a name a qualified
            // name or a member access writes after it; none where the node is a chain's first name, or none.
            static ExpressionSyntax? Through(ExpressionSyntax node) => node switch
            {
                AliasQualifiedNameSyntax aliased => aliased.Name,
                PostfixUnaryExpressionSyntax suppressed when suppressed.IsKind(SyntaxKind.SuppressNullableWarningExpression) => suppressed.Operand,
                ParenthesizedExpressionSyntax parenthesized => parenthesized.Expression,
                QualifiedNameSyntax qualified when qualified.Right.Identifier.ValueText.Length > 0 => qualified.Left,
                MemberAccessExpressionSyntax { Expression: not ThisExpressionSyntax } access when access.IsKind(SyntaxKind.SimpleMemberAccessExpression) && access.Name.Identifier.ValueText.Length > 0 => access.Expression,
                _ => null,
            };
        }

        /// <summary>The names the chain <paramref name="receiver"/> writes, joined by dots: read from its end, its first name last.</summary>
        private static string ChainText(ExpressionSyntax receiver, Walked walk)
        {
            var names = new List<string>();
            var at = receiver;

            while (walk.Chains.GetValueOrDefault(at) is { } chain)
            {
                if (chain.Before is not { } before)
                {
                    names.Add(chain.Head);
                    break;
                }

                names.Add(chain.Last);
                at = before;
            }

            names.Reverse();
            return string.Join('.', names);
        }

        /// <summary>
        /// The parameters of a class's or a struct's primary constructor no member of <paramref name="declared"/> is declared
        /// of: what its members read by their names as fields, though no node is of them. A record's are its properties.
        /// </summary>
        private static HashSet<string> Captured(BaseTypeDeclarationSyntax declared, IReadOnlySet<string> typeParameters)
        {
            if (declared is not TypeDeclarationSyntax { ParameterList: { } primary } typed || declared is RecordDeclarationSyntax)
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }

            var members = FieldTypes(typed.WithParameterList(null), typeParameters);
            return primary.Parameters.Select(parameter => parameter.Identifier.ValueText).Where(name => name.Length > 0 && !members.ContainsKey(name)).ToHashSet(StringComparer.Ordinal);
        }

        /// <summary>
        /// <paramref name="declared"/>'s fields, properties, events and primary constructor's parameters, by name, each
        /// with the type it is declared with: what a member of it reads by a name alone.
        /// </summary>
        private static Dictionary<string, string?> FieldTypes(BaseTypeDeclarationSyntax declared, IReadOnlySet<string> typeParameters)
        {
            var fields = new Dictionary<string, string?>(StringComparer.Ordinal);

            if (declared is not TypeDeclarationSyntax typed)
            {
                return fields;
            }

            foreach (var parameter in typed.ParameterList?.Parameters ?? [])
            {
                fields.TryAdd(parameter.Identifier.ValueText, parameter.Type is { } written ? ValueKey(written, typeParameters) : null);
            }

            foreach (var member in typed.Members)
            {
                switch (member)
                {
                    case BaseFieldDeclarationSyntax field:
                        foreach (var variable in field.Declaration.Variables)
                        {
                            fields.TryAdd(variable.Identifier.ValueText, ValueKey(field.Declaration.Type, typeParameters));
                        }

                        break;

                    // An interface's member implemented explicitly is no member read by its name.
                    case PropertyDeclarationSyntax { ExplicitInterfaceSpecifier: null } property:
                        fields.TryAdd(property.Identifier.ValueText, ValueKey(property.Type, typeParameters));
                        break;

                    case EventDeclarationSyntax { ExplicitInterfaceSpecifier: null } declaredEvent:
                        fields.TryAdd(declaredEvent.Identifier.ValueText, ValueKey(declaredEvent.Type, typeParameters));
                        break;
                }
            }

            fields.Remove(string.Empty);
            return fields;
        }

        /// <summary>The type a target-typed <c>new()</c> makes: the one written where it is given - a variable's, a field's, a property's, a parameter's default.</summary>
        private static TypeSyntax? TargetType(ImplicitObjectCreationExpressionSyntax created) => created.Parent switch
        {
            EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } } when !declaration.Type.IsVar => declaration.Type,
            EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax property } => property.Type,
            EqualsValueClauseSyntax { Parent: ParameterSyntax parameter } => parameter.Type,
            _ => null,
        };

        /// <summary>
        /// The type an extension method's <c>this</c> parameter is - for a type parameter, the type it is constrained to -
        /// as a named qualifier names it; <see langword="null"/> where the file says none.
        /// </summary>
        private static string? Extended(TypeSyntax? receiver, SyntaxList<TypeParameterConstraintClauseSyntax> clauses, IReadOnlySet<string> typeParameters)
        {
            if (receiver is null || Key(receiver) is not { } key || key == DynamicType)
            {
                return null;
            }

            return !typeParameters.Contains(key) ? key
                : clauses.FirstOrDefault(clause => clause.Name.Identifier.ValueText == key)?.Constraints.OfType<TypeConstraintSyntax>().FirstOrDefault() is { } constraint ? ValueKey(constraint.Type, typeParameters)
                : null;
        }

        /// <summary>Whether <paramref name="declared"/> is a delegate type: one of the base library's, or one the file declares.</summary>
        private bool IsDelegate(string declared)
        {
            var name = Plain(declared[(declared.LastIndexOf('.') + 1)..]);
            return Delegates.Contains(name) || _delegates.Contains(name);
        }

        /// <summary>The types <paramref name="type"/> names (<see cref="Mention(ExtractNode, IEnumerable{TypeSyntax}, IReadOnlySet{string})"/>); none where it is none.</summary>
        private void Mention(ExtractNode from, TypeSyntax? type, IReadOnlySet<string> typeParameters) => Mention(from, type is null ? [] : [type], typeParameters);

        /// <summary>
        /// The types <paramref name="types"/> name, each a <c>references</c> from <paramref name="from"/>: itself where it
        /// is named, each type argument, an array's or a nullable's element, a tuple's parts, in the order they are written -
        /// on a stack of their own, so a type nested in another any depth is no call deeper. A type parameter, a built-in
        /// type, <c>var</c> and <c>dynamic</c> are none.
        /// </summary>
        private void Mention(ExtractNode from, IEnumerable<TypeSyntax> types, IReadOnlySet<string> typeParameters)
        {
            var pending = new Stack<TypeSyntax>();
            Stacked(pending, types);

            while (pending.TryPop(out var type))
            {
                switch (type)
                {
                    case NameSyntax named when NameOf(named) is { } target && !typeParameters.Contains(target.Name) && !IsContextual(named):
                        Reference(from, CodeEdgeKinds.References, target, named);
                        Stacked(pending, TypeArguments(named));
                        break;

                    case NullableTypeSyntax nullable:
                        pending.Push(nullable.ElementType);
                        break;

                    case ArrayTypeSyntax array:
                        pending.Push(array.ElementType);
                        break;

                    case PointerTypeSyntax pointer:
                        pending.Push(pointer.ElementType);
                        break;

                    case RefTypeSyntax reference:
                        pending.Push(reference.Type);
                        break;

                    case ScopedTypeSyntax scoped:
                        pending.Push(scoped.Type);
                        break;

                    case TupleTypeSyntax tuple:
                        Stacked(pending, tuple.Elements.Select(element => element.Type));
                        break;
                }
            }
        }

        /// <summary>The type arguments a named type is written with, each mentioned.</summary>
        private void Arguments(ExtractNode from, TypeSyntax type, IReadOnlySet<string> typeParameters) => Mention(from, TypeArguments(type), typeParameters);

        /// <summary>The type arguments <paramref name="type"/> is written with, where it is a generic name: its last part's.</summary>
        private static IEnumerable<TypeSyntax> TypeArguments(TypeSyntax type) => type switch
        {
            GenericNameSyntax name => name.TypeArgumentList.Arguments,
            QualifiedNameSyntax { Right: GenericNameSyntax name } => name.TypeArgumentList.Arguments,
            AliasQualifiedNameSyntax { Name: GenericNameSyntax name } => name.TypeArgumentList.Arguments,
            NullableTypeSyntax { ElementType: GenericNameSyntax name } => name.TypeArgumentList.Arguments,
            _ => [],
        };

        private void MentionParameters(ExtractNode from, BaseParameterListSyntax? parameters, IReadOnlySet<string> typeParameters)
        {
            foreach (var parameter in parameters?.Parameters ?? [])
            {
                Mention(from, parameter.Type, typeParameters);
            }
        }

        private void Constraints(ExtractNode from, SyntaxList<TypeParameterConstraintClauseSyntax> clauses, IReadOnlySet<string> typeParameters)
        {
            foreach (var constraint in clauses.SelectMany(clause => clause.Constraints).OfType<TypeConstraintSyntax>())
            {
                Mention(from, constraint.Type, typeParameters);
            }
        }

        /// <summary>Each attribute <paramref name="lists"/> put on <paramref name="from"/>, a reference to its class: its name as written, with <c>Attribute</c> after it where it is left out.</summary>
        private void Attributes(ExtractNode from, SyntaxList<AttributeListSyntax> lists, IReadOnlySet<string> typeParameters)
        {
            foreach (var attribute in lists.SelectMany(list => list.Attributes))
            {
                if (NameOf(attribute.Name) is not { } named)
                {
                    continue;
                }

                Reference(from, CodeEdgeKinds.References, AttributeClass(named.Name), named.Qualifier is null ? null : QualifiedName.Of(named.Qualifier), Line(attribute.SpanStart));
                Arguments(from, attribute.Name, typeParameters);
            }
        }

        /// <summary>The class an attribute written <paramref name="name"/> is: its name with <c>Attribute</c> after it where it is left out, a generic one's count kept.</summary>
        private static string AttributeClass(string name)
        {
            var tick = name.IndexOf('`', StringComparison.Ordinal);
            var plain = tick < 0 ? name : name[..tick];
            var arity = tick < 0 ? string.Empty : name[tick..];

            return (plain.EndsWith("Attribute", StringComparison.Ordinal) ? plain : $"{plain}Attribute") + arity;
        }

        /// <summary>An <c>override</c>'s reference to the member of a base type it overrides, by its name and parameters.</summary>
        private void Overrides(ExtractNode node, SyntaxTokenList modifiers, int? parameters, int line)
        {
            if (modifiers.Any(SyntaxKind.OverrideKeyword))
            {
                _references.Add(new ExtractReference(node.Id, CodeEdgeKinds.Overrides, node.Name, line)
                {
                    Arity = parameters,
                    Qualifier = new ExtractQualifier(CodeQualifierKinds.Base),
                });
            }
        }

        private void Call(ExtractNode from, string name, int arity, int line, ExtractQualifier? qualifier)
        {
            if (name.Length > 0)
            {
                _references.Add(new ExtractReference(from.Id, CodeEdgeKinds.Calls, name, line) { Arity = arity, Qualifier = qualifier });
            }
        }

        private void Reference(ExtractNode from, string kind, (string Name, string? Qualifier) target, SyntaxNode at)
            => Reference(from, kind, target.Name, target.Qualifier is null ? null : QualifiedName.Of(target.Qualifier), Line(at.SpanStart));

        private void Reference(ExtractNode from, string kind, string name, QualifiedName? qualifier, int line)
            => _references.Add(new ExtractReference(from.Id, kind, name, line)
            {
                Qualifier = qualifier is null ? null : new ExtractQualifier(CodeQualifierKinds.Type, qualifier),
            });

        private void Dynamic(SyntaxNode at, string why) => _dynamic.Add(new ExtractDynamic(Line(at.SpanStart), why, DynamicCall));

        private void Scope(QualifiedName name, QualifiedName? parent, bool open = true) => _scopes.TryAdd(name, new ExtractScope(name, open) { Parent = parent });

        private ExtractNode Add(ExtractNode node)
        {
            _nodes.Add(node);
            return node;
        }

        /// <summary>The id of a declaration named so: the language's prefix and the name, told apart from an earlier one alike by its place.</summary>
        private QualifiedName Id(QualifiedName name) => _ids.Next(name.Prepend(prefix));

        /// <summary>The id of a member of <paramref name="type"/> identified by <paramref name="tail"/>: an extension block's with the block's receiver.</summary>
        private QualifiedName MemberId(TypeFrame type, string tail)
            => Id(type.IdName.Under($"{(type.Receiver is { } receiver ? $"extension({TypeText(receiver)})." : string.Empty)}{tail}"));

        /// <summary>
        /// The name a type's id is made of, and whether it is its file's alone: a file-local type's - and one declared in
        /// it - holds its file's path, as the statements at a file's top do, since many files may each have one alike.
        /// </summary>
        private (QualifiedName IdName, bool Local) Named(TypeFrame? outer, SyntaxTokenList modifiers, string name, QualifiedName qualified)
            => outer is not null ? (ExtractText.Qualify(outer.IdName, name), outer.Local)
                : modifiers.Any(SyntaxKind.FileKeyword) ? ($"{path}:{qualified}", true)
                : (qualified, false);

        /// <summary>Why the file is generated code: its name, or the marker a generator writes in the comments it begins with.</summary>
        private string? Generated()
        {
            var name = CodeGraphIds.FileName(path);

            if (GeneratedEndings.FirstOrDefault(ending => name.EndsWith(ending, StringComparison.OrdinalIgnoreCase)) is { } ending)
            {
                return $"its name ends with {name[^ending.Length..]}, as a generator's files are";
            }

            if (name.StartsWith(GeneratedStart, StringComparison.OrdinalIgnoreCase))
            {
                return $"its name begins with {GeneratedStart}, as a build's passing files are";
            }

            return root.GetLeadingTrivia().Any(trivia => (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
                && (trivia.ToString().Contains("<auto-generated", StringComparison.OrdinalIgnoreCase) || trivia.ToString().Contains("<autogenerated", StringComparison.OrdinalIgnoreCase)))
                ? "it begins with a comment marking it <auto-generated>"
                : null;
        }

        private int Line(int position) => lines.LineAt(position);

        /// <summary>The line <paramref name="node"/>'s last character is on.</summary>
        private int EndLine(SyntaxNode node) => Line(Math.Max(node.SpanStart, node.Span.End - 1));

        /// <summary>The line <paramref name="token"/> is on, where the file holds it; <see langword="null"/> where it is missing.</summary>
        private int? LineOf(SyntaxToken token) => token.RawKind == 0 || token.IsMissing ? null : Line(token.SpanStart);
    }

    /// <summary>
    /// <paramref name="declaration"/>'s header on one line, as a signature: its tokens from the first after its attributes
    /// up to <paramref name="stop"/> - the token its body or its end begins with - each run of trivia between them one
    /// space.
    /// </summary>
    private static string Header(MemberDeclarationSyntax declaration, SyntaxToken stop)
    {
        var header = new StringBuilder();
        var token = declaration.AttributeLists.Count > 0 ? declaration.AttributeLists[^1].GetLastToken().GetNextToken() : declaration.GetFirstToken();
        var previous = default(SyntaxToken);

        while (token.RawKind != 0 && token.SpanStart < declaration.Span.End && token != stop)
        {
            if (header.Length > 0 && (previous.HasTrailingTrivia || token.HasLeadingTrivia))
            {
                header.Append(' ');
            }

            header.Append(token.Text);
            previous = token;
            token = token.GetNextToken();
        }

        return ExtractText.Signature(ExtractText.Flat(header.ToString()));
    }

    /// <summary>
    /// The first sentence of the <c>&lt;summary&gt;</c> of <paramref name="declaration"/>'s documentation comment - or, of
    /// its <c>&lt;param&gt;</c> that names <paramref name="parameter"/>; <see langword="null"/> where it has none.
    /// </summary>
    private static string? Summary(SyntaxNode declaration, string? parameter = null)
    {
        var summary = declaration.GetLeadingTrivia()
            .Select(trivia => trivia.GetStructure())
            .OfType<DocumentationCommentTriviaSyntax>()
            .LastOrDefault()?
            .Content.OfType<XmlElementSyntax>()
            .FirstOrDefault(element => parameter is null
                ? element.StartTag.Name.LocalName.ValueText == "summary"
                : element.StartTag.Name.LocalName.ValueText == "param" && element.StartTag.Attributes.OfType<XmlNameAttributeSyntax>().Any(named => named.Identifier.Identifier.ValueText == parameter));

        return summary is not null && ExtractText.Flat(Prose(summary.Content)) is { Length: > 0 } text ? ExtractText.Summary(text) : null;
    }

    /// <summary>
    /// The text of a documentation comment's content as it reads: a <c>&lt;see cref&gt;</c> as what it names, an element as
    /// its text - read on a stack of its own, so an element nested any depth is no call deeper.
    /// </summary>
    private static string Prose(SyntaxList<XmlNodeSyntax> content)
    {
        var prose = new StringBuilder();

        // The contents still to read, each from where it stands: an element's is read before the rest of what holds it.
        var pending = new Stack<(SyntaxList<XmlNodeSyntax> Nodes, int At)>();
        pending.Push((content, 0));

        while (pending.TryPop(out var next))
        {
            if (next.At >= next.Nodes.Count)
            {
                continue;
            }

            pending.Push((next.Nodes, next.At + 1));

            switch (next.Nodes[next.At])
            {
                case XmlTextSyntax text:
                    foreach (var token in text.TextTokens)
                    {
                        prose.Append(token.IsKind(SyntaxKind.XmlTextLiteralNewLineToken) ? " " : token.ValueText);
                    }

                    break;

                case XmlElementSyntax element:
                    pending.Push((element.Content, 0));
                    break;

                case XmlEmptyElementSyntax empty:
                    prose.Append(empty.Attributes.FirstOrDefault() switch
                    {
                        XmlCrefAttributeSyntax cref => cref.Cref.ToString(),
                        XmlNameAttributeSyntax name => name.Identifier.Identifier.ValueText,
                        XmlTextAttributeSyntax text => string.Concat(text.TextTokens.Select(token => token.ValueText)),
                        _ => string.Empty,
                    });
                    break;

                case XmlCDataSectionSyntax data:
                    foreach (var token in data.TextTokens)
                    {
                        prose.Append(token.ValueText);
                    }

                    break;
            }
        }

        return prose.ToString();
    }

    /// <summary>Whether a framework's attribute among <paramref name="lists"/> marks the member a test.</summary>
    private static bool IsTest(SyntaxList<AttributeListSyntax> lists)
        => lists.SelectMany(list => list.Attributes).Any(attribute => NameOf(attribute.Name) is { } named
            && Plain(named.Name) is var plain
            && TestAttributes.Contains(plain.EndsWith("Attribute", StringComparison.Ordinal) ? plain[..^"Attribute".Length] : plain));

    private static IReadOnlyList<string> Modifiers(SyntaxTokenList modifiers)
        => [.. modifiers.Select(modifier => modifier.ValueText).Where(text => text.Length > 0)];

    /// <summary>
    /// The fewest and the most arguments a call of a member with <paramref name="parameters"/> takes: no most where it
    /// takes a <c>params</c> list; <paramref name="received"/> fewer at least where a call on its receiver gives one of
    /// them - an extension method's; <paramref name="extra"/> more at most where its static form takes its receiver first.
    /// </summary>
    private static (int? Min, int? Max) Arity(BaseParameterListSyntax parameters, int received, int extra)
    {
        var list = parameters.Parameters;
        var required = list.Count(parameter => parameter.Default is null && !parameter.Modifiers.Any(SyntaxKind.ParamsKeyword));
        var most = list.Any(parameter => parameter.Modifiers.Any(SyntaxKind.ParamsKeyword)) ? (int?)null : list.Count + extra;

        return (Math.Max(0, required - received), most);
    }

    /// <summary>A member's parameter types, as its id holds them: each as written, with <c>ref</c>, <c>out</c> or <c>in</c> where it says one.</summary>
    private static string ParameterTypes(BaseParameterListSyntax parameters)
        => string.Join(',', parameters.Parameters.Select(parameter =>
            string.Concat(parameter.Modifiers.Where(modifier => modifier.IsKind(SyntaxKind.RefKeyword) || modifier.IsKind(SyntaxKind.OutKeyword) || modifier.IsKind(SyntaxKind.InKeyword) || modifier.IsKind(SyntaxKind.ReadOnlyKeyword))
                .Select(modifier => $"{modifier.ValueText} ")) + (parameter.Type is { } type ? TypeText(type) : "?")));

    /// <summary>The interface an explicit implementation names, as the member's id begins with it.</summary>
    private static string Explicit(ExplicitInterfaceSpecifierSyntax? specifier)
        => specifier is not null && Written(specifier.Name) is { } written ? $"{written}." : string.Empty;

    /// <summary>
    /// <paramref name="type"/> as written with no trivia and no tuple element's name, which changes no type: a space only
    /// between two words, so <c>(int a, int b)</c> is <c>(int,int)</c>.
    /// </summary>
    private static string TypeText(SyntaxNode type)
    {
        var text = new StringBuilder();

        foreach (var token in type.DescendantTokens().Where(token => token.Text.Length > 0 && !(token.Parent is TupleElementSyntax element && token == element.Identifier)))
        {
            if (text.Length > 0 && IsWordy(text[^1]) && IsWordy(token.Text[0]))
            {
                text.Append(' ');
            }

            text.Append(token.Text);
        }

        return text.ToString();

        static bool IsWordy(char at) => char.IsLetterOrDigit(at) || at is '_' or '@';
    }

    /// <summary>
    /// The type a type's syntax names, as a reference names it: its name - a generic one's with its count of type
    /// arguments - and the qualifier written before it; <see langword="null"/> where it names none, or the file is cut
    /// inside it.
    /// </summary>
    private static (string Name, string? Qualifier)? NameOf(TypeSyntax type)
    {
        // `A?` and `global::A` name A: unwrapped on a loop, however many the parser nests.
        while (true)
        {
            switch (type)
            {
                case AliasQualifiedNameSyntax aliased:
                    type = aliased.Name;
                    continue;

                case NullableTypeSyntax nullable:
                    type = nullable.ElementType;
                    continue;

                case QualifiedNameSyntax qualified:
                    return Named(qualified.Right) is { } right && Written(qualified.Left) is { } left ? (right, left) : null;

                case SimpleNameSyntax simple:
                    return Named(simple) is { } name ? (name, null) : null;

                default:
                    return null;
            }
        }

        static string? Named(SimpleNameSyntax name) => name.Identifier.ValueText.Length > 0 ? SimpleName(name) : null;
    }

    /// <summary>
    /// A type as a value of it is qualified by: its name as written, a generic one with its count of type arguments;
    /// <c>dynamic</c> as itself; <see langword="null"/> where the file does not say one - <c>var</c>, an array, a tuple, a
    /// pointer.
    /// </summary>
    private static string? Key(TypeSyntax type)
    {
        // `A?`, `ref A` and `scoped A` are A: unwrapped on a loop, however many the parser nests.
        while (true)
        {
            switch (type)
            {
                case NullableTypeSyntax nullable:
                    type = nullable.ElementType;
                    continue;

                case RefTypeSyntax reference:
                    type = reference.Type;
                    continue;

                case ScopedTypeSyntax scoped:
                    type = scoped.Type;
                    continue;

                case IdentifierNameSyntax { IsVar: true }:
                    return null;

                case IdentifierNameSyntax { Identifier.ValueText: DynamicType }:
                    return DynamicType;

                case NameSyntax or PredefinedTypeSyntax:
                    return Written(type);

                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// The type a value declared as <paramref name="type"/> is, as a named qualifier names it: its <see cref="Key"/>,
    /// <c>dynamic</c> kept; <see langword="null"/> for one of <paramref name="typeParameters"/>, which is whatever it is
    /// given.
    /// </summary>
    private static string? ValueKey(TypeSyntax type, IReadOnlySet<string> typeParameters)
        => Key(type) is { } key && !typeParameters.Contains(key) ? key : null;

    /// <summary>The type a member's value is declared with, as its node says it: a <c>dynamic</c> one says none, since no call on it binds.</summary>
    private static string? NodeValueType(TypeSyntax type, IReadOnlySet<string> typeParameters)
        => ValueKey(type, typeParameters) is { } key && key != DynamicType ? key : null;

    /// <summary>Stacks <paramref name="items"/> on <paramref name="pending"/>, the first on top: read in the order given, before what was stacked before them.</summary>
    private static void Stacked<T>(Stack<T> pending, IEnumerable<T> items)
    {
        var listed = items as IReadOnlyList<T> ?? [.. items];

        for (var at = listed.Count - 1; at >= 0; at--)
        {
            pending.Push(listed[at]);
        }
    }

    /// <summary><paramref name="outer"/>'s type parameters and those <paramref name="list"/> declares: the names a type of it may be.</summary>
    private static IReadOnlySet<string> With(IReadOnlySet<string>? outer, TypeParameterListSyntax? list)
        => (outer ?? new HashSet<string>(StringComparer.Ordinal)).Concat(list?.Parameters.Select(parameter => parameter.Identifier.ValueText) ?? []).ToHashSet(StringComparer.Ordinal);

    /// <summary>Whether <paramref name="type"/> is a word C# reads as a type of its own where it is written alone: <c>var</c>, <c>dynamic</c>, <c>nint</c> and their kin.</summary>
    private static bool IsContextual(TypeSyntax type)
        => type.IsVar || type.IsNint || type.IsNuint || type.IsUnmanaged || type.IsNotNull || type is IdentifierNameSyntax { Identifier.ValueText: DynamicType };

    /// <summary>
    /// A name the code writes - a namespace, a type, a chain of member access - as one dotted name, each generic part
    /// with its count of type arguments, <c>global::</c> left out; <see langword="null"/> where it is anything else. Read
    /// from its end on a loop, so a name of any length is no call deeper.
    /// </summary>
    private static string? Written(ExpressionSyntax? name)
    {
        // Its names, last first.
        var names = new List<string>();

        for (var at = name; ;)
        {
            switch (at)
            {
                case SimpleNameSyntax simple when simple.Identifier.ValueText.Length > 0:
                    names.Add(SimpleName(simple));
                    break;

                case PredefinedTypeSyntax predefined:
                    names.Add(predefined.Keyword.ValueText);
                    break;

                case AliasQualifiedNameSyntax aliased:
                    at = aliased.Name;
                    continue;

                case QualifiedNameSyntax { Right.Identifier.ValueText.Length: > 0 } qualified:
                    names.Add(SimpleName(qualified.Right));
                    at = qualified.Left;
                    continue;

                case MemberAccessExpressionSyntax { Name.Identifier.ValueText.Length: > 0 } access when access.IsKind(SyntaxKind.SimpleMemberAccessExpression):
                    names.Add(SimpleName(access.Name));
                    at = access.Expression;
                    continue;

                default:
                    return null;
            }

            names.Reverse();
            return string.Join('.', names);
        }
    }

    /// <summary>The name <paramref name="name"/> writes: a generic one's with its count of type arguments.</summary>
    private static string SimpleName(SimpleNameSyntax name)
        => name is GenericNameSyntax generic ? Generic(generic.Identifier.ValueText, generic.TypeArgumentList.Arguments.Count) : name.Identifier.ValueText;

    /// <summary>Whether <paramref name="expression"/> is <c>nameof(...)</c>, which names a member by the code and not by a string.</summary>
    private static bool IsNameOf(ExpressionSyntax expression)
        => expression is InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } };

    /// <summary>A name with its count of type parameters after a backtick, where it has some.</summary>
    private static string Generic(string name, int count) => count > 0 ? $"{name}`{count.ToString(CultureInfo.InvariantCulture)}" : name;

    /// <summary>A name without its count of type parameters.</summary>
    private static string Plain(string name) => name.IndexOf('`', StringComparison.Ordinal) is var tick and >= 0 ? name[..tick] : name;

    /// <summary>
    /// Whether a base a class names first is named as an interface is in C#: an I, a capital letter and then a small one -
    /// <c>IShape</c>, not <c>IOException</c>.
    /// </summary>
    private static bool LooksLikeInterface(string name) => name.Length > 2 && name[0] == 'I' && char.IsUpper(name[1]) && char.IsLower(name[2]);

    /// <summary>
    /// How deep a C# text nests as the compiler reads it: a measure taken before the parse, by one pass over its characters
    /// on a loop of this reader's own, so the parse is given a stack that holds it (<see cref="Parse"/>). It reads the text
    /// as the lexer does - a string, a character and a comment one token each, an interpolated string's holes code, what a
    /// directive's branch leaves out no code, the parse defining no symbol but those the file defines - and counts a level
    /// for each bracket and brace open - two for a call's argument list - each interpolated string and each of its holes, each angle bracket a generic may
    /// open, each element of a documentation comment, each parenthesis and negation of a directive's condition, and each
    /// thing that holds the next with no bracket - a lambda's arrow, an assignment, a conditional, a unary operator, a cast,
    /// a statement that holds a statement, a label - until the statement or the item of the list it is in ends.
    /// </summary>
    /// <remarks>
    /// The parser's own guard ends a parse short of stack where its recursion nears the stack's end, but its speculative
    /// scans of types, its lexer's interpolated strings and its documentation comments recurse unguarded, and those nest
    /// by brackets, holes and elements, each counted. What nests with no bracket is counted once a level, however the
    /// parser takes it, and where it reads a nesting as ended that the parser still holds - a statement's head before a
    /// block that is no statement's - the parser's guard ends the parse, and it is parsed again on more
    /// (<see cref="Parse"/>). A level counted that the parser does not take costs a thread a little more stack, reserved
    /// and taken only as it is reached.
    /// </remarks>
    private sealed class Nesting
    {
        /// <summary>How much the lexer could recurse for each text <see cref="Lexed"/> was asked of, kept while the text is.</summary>
        private static readonly ConditionalWeakTable<CodeText, StrongBox<long>> LexedDepths = new();

        private readonly CodeText _text;

        /// <summary>The brackets, braces, interpolated strings and holes open, the innermost on top.</summary>
        private readonly Stack<Frame> _frames = new();

        /// <summary>The directives' branches open, the innermost on top.</summary>
        private readonly Stack<Branch> _branches = new();

        /// <summary>The symbols the file defines where its code is read.</summary>
        private readonly HashSet<string> _defined = new(StringComparer.Ordinal);

        private long _at;

        /// <summary>The levels the open frames are themselves.</summary>
        private long _levels;

        /// <summary>The levels the open frames hold below them, beyond themselves: what nested with no bracket where each opened.</summary>
        private long _held;

        /// <summary>The levels an expression nests to with no bracket in the item of the list or the statement the read is in.</summary>
        private long _expression;

        /// <summary>The levels statements that hold the next nest to, in the statement the read is in.</summary>
        private long _statement;

        /// <summary>The angle brackets a generic may have open since the statement or the block began.</summary>
        private long _angles;

        /// <summary>How many interpolated strings' holes are open, in which no directive stands.</summary>
        private int _holes;

        private long _most;
        private long _mostAt;

        /// <summary>Whether the token before is an operand, after which an operator is a binary one.</summary>
        private bool _operand;

        /// <summary>Whether the token before is a <c>)</c>: an operand after it is a cast's.</summary>
        private bool _closedParen;

        /// <summary>Whether a statement begins here: a name read here and a colon after it are a label.</summary>
        private bool _statementStart = true;

        /// <summary>Whether the token before is a name a statement begins with, which a colon makes a label.</summary>
        private bool _labelCandidate;

        /// <summary>Whether a brace opened now begins an expression's - an initializer, a lambda's body, a switch's arms - and not a statement's or a declaration's.</summary>
        private bool _expressionBrace;

        /// <summary>Whether an initializer may follow - after <c>new</c>, <c>delegate</c>, <c>stackalloc</c>, <c>is</c> or <c>case</c> - so a brace begins an expression's.</summary>
        private bool _initializes;

        /// <summary>Whether only whitespace stands before the read on its line, where a directive may begin.</summary>
        private bool _lineStart = true;

        /// <summary>The elements of the documentation comment read open, and what the read of its XML is in.</summary>
        private long _elements;
        private XmlPart _xml;
        private char _xmlQuote;

        private Nesting(CodeText text) => _text = text;

        private enum XmlPart
        {
            Content,
            Tag,
            Quoted,
            EndTag,
            Comment,
            Data,
            Instruction,
        }

        /// <summary>How deep <paramref name="text"/> nests, in levels the parser takes, and where it nests deepest.</summary>
        public static (long Depth, long At) Of(CodeText text)
        {
            var nesting = new Nesting(text);
            nesting.Read();
            return (nesting._most, nesting._mostAt);
        }

        /// <summary>
        /// How deep the lexer alone could recurse into <paramref name="text"/> lexed from any place in it - into an
        /// interpolated string's holes and what they hold, no more than every dollar sign and every bracket the text holds
        /// where it holds a dollar sign, and into a directive's condition, no more than one line's parentheses and
        /// negations - counted once for each text, so many lexes of one text cost one count.
        /// </summary>
        public static long Lexed(CodeText text) => LexedDepths.GetValue(text, static counted =>
        {
            long dollars = 0, brackets = 0, directive = 0, line = 0;
            var (lineStart, inDirective) = (true, false);

            for (long at = 0; at < counted.Length; at++)
            {
                var character = counted[at];

                if (character is '\r' or '\n')
                {
                    (directive, line, lineStart, inDirective) = (Math.Max(directive, line), 0, true, false);
                    continue;
                }

                inDirective |= lineStart && character == '#';
                lineStart &= char.IsWhiteSpace(character);
                dollars += character == '$' ? 1 : 0;
                brackets += character is '(' or '[' or '{' ? 1 : 0;
                line += inDirective && character is '(' or '!' ? 1 : 0;
            }

            return new StrongBox<long>((dollars > 0 ? dollars + brackets : 0) + Math.Max(directive, line));
        }).Value;

        private long Depth => _levels + _held + _expression + _statement + _angles;

        private bool Active => !_branches.TryPeek(out var branch) || branch.Active;

        private void Read()
        {
            while (_at < _text.Length)
            {
                if (!Active)
                {
                    Disabled();
                }
                else if (_frames.TryPeek(out var top) && top.Kind == Frame.Interpolated)
                {
                    StringPart(top);
                }
                else
                {
                    Token();
                }
            }
        }

        /// <summary>Records the depth now, and <paramref name="beyond"/> more levels, where it is the deepest yet, at <paramref name="at"/>.</summary>
        private void Note(long beyond = 0, long at = -1)
        {
            if (Depth + beyond > _most)
            {
                _most = Depth + beyond;
                _mostAt = Math.Clamp(at < 0 ? _at : at, 0, Math.Max(0, _text.Length - 1));
            }
        }

        private char Peek(int ahead) => _at + ahead < _text.Length ? _text[_at + ahead] : '\0';

        /// <summary>How long the longest keyword the read tells a word apart by is: <c>stackalloc</c>'s ten, and room to spare.</summary>
        private const int LongestKeyword = 16;

        /// <summary>How many of <paramref name="character"/> stand in a run from the read on.</summary>
        private long Run(char character)
        {
            var at = _at;

            while (at < _text.Length && _text[at] == character)
            {
                at++;
            }

            return at - _at;
        }

        /// <summary>Where the line <paramref name="at"/> is on ends: at its line break, or the text's end.</summary>
        private long LineEnd(long at)
        {
            var found = at < _text.Length ? _text.FindAny("\r\n", at, _text.Length) : -1;
            return found < 0 ? _text.Length : found;
        }

        /// <summary>One line a directive's branch leaves out: no code, read for a directive alone.</summary>
        private void Disabled()
        {
            var at = _at;

            while (at < _text.Length && _text[at] is ' ' or '\t' or '\f' or '\v')
            {
                at++;
            }

            if (at < _text.Length && _text[at] == '#')
            {
                _at = at;
                Directive();
            }
            else
            {
                _at = LineEnd(at);
            }

            _at += _at < _text.Length && _text[_at] == '\r' ? 1 : 0;
            _at += _at < _text.Length && _text[_at] == '\n' ? 1 : 0;
            _lineStart = true;
        }

        /// <summary>One token of code, or the whitespace, the comment or the directive before the next.</summary>
        private void Token()
        {
            var character = _text[_at];

            if (character is '\r' or '\n')
            {
                _at++;
                _lineStart = true;
                return;
            }

            if (char.IsWhiteSpace(character))
            {
                _at++;
                return;
            }

            if (character == '#' && _lineStart && _holes == 0)
            {
                Directive();
                return;
            }

            _lineStart = false;
            var next = Peek(1);

            if (character == '/' && next == '/')
            {
                if (Peek(2) == '/' && Peek(3) != '/')
                {
                    DocumentationLines();
                }
                else
                {
                    _at = LineEnd(_at);
                }

                return;
            }

            if (character == '/' && next == '*')
            {
                var end = _at + 2 <= _text.Length ? _text.IndexOf("*/", _at + 2) : -1;
                var close = end < 0 ? _text.Length : end + 2;

                if (Peek(2) == '*' && Peek(3) != '/')
                {
                    Documentation(_at + 3, end < 0 ? _text.Length : end);
                }

                _at = close;
                return;
            }

            var label = _labelCandidate;
            var cast = _closedParen;
            var statementStart = _statementStart;
            (_labelCandidate, _closedParen, _statementStart) = (false, false, false);

            if (IsNameStart(character) || (character == '@' && IsNameStart(next)) || character == '\\')
            {
                Word(cast, statementStart);
                return;
            }

            if (char.IsDigit(character) || (character == '.' && char.IsDigit(next)))
            {
                Operand(cast);
                Number();
                return;
            }

            if (character is '"' or '$' || (character == '@' && next is '"' or '$'))
            {
                Operand(cast);
                OpenString();
                return;
            }

            if (character == '\'')
            {
                Operand(cast);
                Character();
                return;
            }

            Punctuation(character, next, cast, label);
        }

        /// <summary>A name or a keyword: what nests after it counted, an operand else.</summary>
        private void Word(bool cast, bool statementStart)
        {
            var start = _at;
            _at++;

            while (_at < _text.Length && (IsNamePart(_text[_at]) || _text[_at] == '\\'))
            {
                _at++;
            }

            if (cast)
            {
                _expression++;
            }

            // No word the read tells apart is longer than a keyword: a longer one is none of them, and is never made a string.
            switch (_at - start <= LongestKeyword ? _text.Slice(start, _at) : string.Empty)
            {
                // A statement that holds the next, its block a statement's.
                case "if" or "else" or "while" or "for" or "foreach" or "do" or "using" or "lock" or "fixed" or "try" or "catch" or "finally" or "checked" or "unchecked" or "unsafe":
                    _statement++;
                    (_operand, _expressionBrace) = (false, false);
                    break;

                // What may be followed by an initializer, its braces an expression's.
                case "new" or "delegate" or "stackalloc" or "is" or "case":
                    _expression++;
                    (_operand, _expressionBrace, _initializes) = (false, true, true);
                    break;

                // What holds the expression after it.
                case "await" or "throw" or "return" or "yield" or "ref" or "out" or "in" or "not" or "and" or "or" or "with" or "switch" or "from" or "when" or "typeof" or "sizeof":
                    _expression++;
                    (_operand, _expressionBrace) = (false, true);
                    break;

                default:
                    (_operand, _expressionBrace, _labelCandidate) = (true, false, statementStart);
                    break;
            }

            Note();
        }

        /// <summary>A number, a string or a character: an operand, a cast's where a <c>)</c> is before it.</summary>
        private void Operand(bool cast)
        {
            if (cast)
            {
                _expression++;
                Note();
            }

            (_operand, _expressionBrace) = (true, false);
        }

        private void Number()
        {
            while (_at < _text.Length && (IsNamePart(_text[_at]) || (_text[_at] == '.' && char.IsDigit(Peek(1)))))
            {
                _at++;
            }
        }

        /// <summary>A character literal: to its closing quote, or its line's end.</summary>
        private void Character()
        {
            _at += Peek(1) == '\\' ? 3 : 2;

            while (_at < _text.Length && _text[_at] is not ('\'' or '\r' or '\n'))
            {
                _at++;
            }

            _at = Math.Min(_at + (_at < _text.Length && _text[_at] == '\'' ? 1 : 0), _text.Length);
        }

        /// <summary>An operator or a bracket.</summary>
        private void Punctuation(char character, char next, bool cast, bool label)
        {
            switch (character)
            {
                case '(':
                    if (cast)
                    {
                        _expression++;
                    }

                    // A call's argument list holds its arguments a level below the call: two levels for one parenthesis.
                    Open(Frame.Paren, expression: true, levels: _operand ? 2 : 1);
                    return;

                case '[':
                    Open(Frame.Bracket, expression: true);
                    return;

                case '{':
                    Open(Frame.Brace, expression: _expressionBrace || _initializes);
                    return;

                case ')':
                    Close(Frame.Paren);
                    _closedParen = true;
                    return;

                case ']':
                    Close(Frame.Bracket);
                    return;

                case '}':
                    CloseBrace();
                    return;

                case ';':
                    _at++;
                    (_expression, _statement, _angles) = (0, 0, 0);
                    (_initializes, _statementStart, _operand, _expressionBrace) = (false, true, false, false);
                    return;

                case ',':
                    _at++;
                    _expression = 0;
                    (_initializes, _operand, _expressionBrace) = (false, false, true);
                    return;

                case '=' when next == '=':
                    Binary(2);
                    return;

                // An assignment and a lambda's arrow hold what is after them.
                case '=':
                    Nest(next == '>' ? 2 : 1);
                    return;

                case '?' when next == '.' && !char.IsDigit(Peek(2)):
                    _at += 2;
                    (_operand, _expressionBrace) = (false, false);
                    return;

                case '?' when next == '[':
                    _at++;
                    (_operand, _expressionBrace) = (false, false);
                    return;

                // A conditional, a null-coalescing and a nullable type's mark.
                case '?':
                    Nest(next == '?' ? Peek(2) == '=' ? 3 : 2 : 1);
                    return;

                case ':' when next == ':':
                    _at += 2;
                    (_operand, _expressionBrace) = (false, false);
                    return;

                // A colon in a hole begins its format, which is no code.
                case ':' when _frames.TryPeek(out var top) && top.Kind == Frame.Hole:
                    Format(top);
                    return;

                case ':':
                    _at++;

                    if (label)
                    {
                        _statement++;
                        Note();
                    }

                    (_operand, _expressionBrace, _initializes, _statementStart) = (false, false, false, true);
                    return;

                case '!' when next == '=':
                    Binary(2);
                    return;

                // A postfix ! forgives a null, and holds nothing.
                case '!' when _operand:
                    _at++;
                    return;

                case '!' or '~':
                    Nest(1);
                    return;

                case '+' or '-' when next == character:
                    if (_operand)
                    {
                        _at += 2;
                    }
                    else
                    {
                        Nest(2);
                    }

                    return;

                case '-' when next == '>':
                    _at += 2;
                    (_operand, _expressionBrace) = (false, false);
                    return;

                case '+' or '-' or '*' or '&' or '^' or '|' or '/' or '%' when next == '=':
                    Nest(2);
                    return;

                case '&' when next == '&':
                case '|' when next == '|':
                    Binary(2);
                    return;

                // A unary operator holds its operand; a binary one, read on a loop, nothing.
                case '+' or '-' or '*' or '&' or '^':
                    if (_operand)
                    {
                        Binary(1);
                    }
                    else
                    {
                        Nest(1);
                    }

                    return;

                case '|' or '/' or '%':
                    Binary(1);
                    return;

                // A generic's type arguments nest as its angle brackets do; a shift's and a comparison's are counted alike,
                // and a scan of a type ends where the next statement or block begins.
                case '<':
                {
                    var run = Run('<');
                    _at += run;
                    _angles += run;
                    Note();
                    Shifted(run);
                    return;
                }

                case '>':
                {
                    var run = Run('>');
                    _at += run;
                    _angles = Math.Max(0, _angles - run);
                    Shifted(run);
                    return;
                }

                case '.' when next == '.':
                    Nest(2);
                    return;

                case '.':
                    _at++;
                    (_operand, _expressionBrace) = (false, false);
                    return;

                default:
                    _at++;
                    return;
            }
        }

        /// <summary>An operator that holds what is after it, <paramref name="width"/> characters wide: a level more.</summary>
        private void Nest(int width)
        {
            _at += width;
            _expression++;
            (_operand, _expressionBrace) = (false, true);
            Note();
        }

        /// <summary>
        /// What a run of <paramref name="run"/> angle brackets read is with an <c>=</c> after it: a shift's assignment, which
        /// holds what is after it, or a comparison, read on a loop.
        /// </summary>
        private void Shifted(long run)
        {
            if (Peek(0) == '=')
            {
                if (run > 1)
                {
                    Nest(1);
                }
                else
                {
                    Binary(1);
                }

                return;
            }

            (_operand, _expressionBrace) = (false, true);
        }

        /// <summary>A binary operator <paramref name="width"/> characters wide, read on a loop: no level.</summary>
        private void Binary(int width)
        {
            _at += width;
            (_operand, _expressionBrace) = (false, true);
        }

        /// <summary>A bracket, a brace or a hole opened: a level, what nested with no bracket before it held below it.</summary>
        private void Open(char kind, bool expression, int levels = 1)
        {
            _at++;
            Push(new Frame(kind, _expression, _statement, _initializes, expression) { Levels = levels });

            if (kind == Frame.Brace)
            {
                _angles = 0;
            }

            (_operand, _expressionBrace, _statementStart) = (false, kind != Frame.Brace, kind == Frame.Brace && !expression);
        }

        private void Push(Frame frame)
        {
            _frames.Push(frame);
            _levels += frame.Levels;
            _held += frame.Expression + frame.Statement;
            (_expression, _statement, _initializes) = (0, 0, false);
            Note();
        }

        /// <summary>
        /// The frame on top closed: what nested with no bracket where it opened goes on - but past a statement's or a
        /// declaration's block, whose heads it ends.
        /// </summary>
        private void Pop(Frame frame)
        {
            _frames.Pop();
            _levels -= frame.Levels;
            _held -= frame.Expression + frame.Statement;

            (_expression, _statement, _initializes) = frame.Kind != Frame.Brace || frame.ExpressionBrace
                ? (frame.Expression, frame.Statement, frame.Kind != Frame.Brace && frame.Initializes)
                : (0, 0, false);
        }

        /// <summary>A <c>)</c> or a <c>]</c>: it closes the frame on top where that is its own, and nothing else.</summary>
        private void Close(char kind)
        {
            _at++;

            if (_frames.TryPeek(out var top) && top.Kind == kind)
            {
                Pop(top);
            }

            (_operand, _expressionBrace) = (true, false);
        }

        /// <summary>A <c>}</c>: it closes a hole, as many as the string's dollar signs, or a brace.</summary>
        private void CloseBrace()
        {
            if (_frames.TryPeek(out var top) && top.Kind == Frame.Hole)
            {
                var run = Math.Min(Run('}'), top.Dollars);

                _at += Math.Max(run, 1);

                if (run >= top.Dollars)
                {
                    Pop(top);
                    _holes--;
                }

                return;
            }

            _at++;

            if (top.Kind == Frame.Brace && _frames.Count > 0)
            {
                Pop(top);
                (_operand, _statementStart) = (top.ExpressionBrace, !top.ExpressionBrace);
            }

            (_angles, _expressionBrace) = (0, false);
        }

        /// <summary>A hole's format, after its colon: no code, to the hole's end - or the string's, where a line ends a regular one.</summary>
        private void Format(Frame hole)
        {
            _at++;

            while (_at < _text.Length)
            {
                var character = _text[_at];

                if (character == '}')
                {
                    var run = Run('}');

                    if (run >= hole.Dollars)
                    {
                        _at += hole.Dollars;
                        Pop(hole);
                        _holes--;
                        return;
                    }

                    _at += run;
                    continue;
                }

                if ((character is '\r' or '\n' || character == '"') && hole.Quotes == 1 && !hole.Verbatim)
                {
                    Pop(hole);
                    _holes--;
                    return;
                }

                _at++;
            }
        }

        /// <summary>
        /// A string opened: <c>$</c>, <c>@</c> and the quotes it begins with read. A plain, a verbatim or a raw one is read
        /// to its end at once; an interpolated one is a frame, read on in <see cref="StringPart"/>, each of its holes a
        /// frame above it.
        /// </summary>
        private void OpenString()
        {
            var verbatim = Peek(0) == '@';
            _at += verbatim ? 1 : 0;

            var dollars = Run('$');
            _at += dollars;

            if (!verbatim && Peek(0) == '@')
            {
                verbatim = true;
                _at++;
            }

            if (Peek(0) != '"')
            {
                return;
            }

            var quotes = verbatim ? 1 : Run('"');

            if (quotes == 2)
            {
                _at += 2;
                return;
            }

            quotes = quotes >= 3 ? quotes : 1;
            _at += quotes;

            if (dollars == 0)
            {
                Unholed(quotes, verbatim);
                return;
            }

            Push(new Frame(Frame.Interpolated, _expression, _statement, _initializes, false) { Quotes = quotes, Dollars = quotes >= 3 ? dollars : 1, Verbatim = verbatim });
        }

        /// <summary>A string with no holes, to its end: a raw one at as many quotes as it opened with, a regular one at its line's end too.</summary>
        private void Unholed(long quotes, bool verbatim)
        {
            while (_at < _text.Length)
            {
                var character = _text[_at];

                if (quotes >= 3)
                {
                    var run = character == '"' ? Run('"') : 1;
                    _at += run;

                    if (character == '"' && run >= quotes)
                    {
                        return;
                    }

                    continue;
                }

                if (character == '"')
                {
                    if (verbatim && Peek(1) == '"')
                    {
                        _at += 2;
                        continue;
                    }

                    _at++;
                    return;
                }

                if (!verbatim && character is '\r' or '\n')
                {
                    return;
                }

                _at = Math.Min(_at + (!verbatim && character == '\\' ? 2 : 1), _text.Length);
            }
        }

        /// <summary>An interpolated string's text, to its end or its next hole.</summary>
        private void StringPart(Frame text)
        {
            while (_at < _text.Length)
            {
                var character = _text[_at];

                if (text.Quotes >= 3)
                {
                    var run = character is '"' or '{' ? Run(character) : 1;
                    _at += run;

                    if (character == '"' && run >= text.Quotes)
                    {
                        Pop(text);
                        _operand = true;
                        return;
                    }

                    if (character == '{' && run >= text.Dollars)
                    {
                        Hole(text);
                        return;
                    }

                    continue;
                }

                switch (character)
                {
                    case '"' when text.Verbatim && Peek(1) == '"':
                        _at += 2;
                        continue;

                    case '"':
                        _at++;
                        Pop(text);
                        _operand = true;
                        return;

                    case '\\' when !text.Verbatim:
                        _at = Math.Min(_at + 2, _text.Length);
                        continue;

                    // A regular string ends at its line's end.
                    case '\r' or '\n' when !text.Verbatim:
                        Pop(text);
                        return;

                    case '{' when Peek(1) == '{':
                    case '}' when Peek(1) == '}':
                        _at += 2;
                        continue;

                    case '{':
                        _at++;
                        Hole(text);
                        return;

                    default:
                        _at++;
                        continue;
                }
            }
        }

        /// <summary>A hole of <paramref name="text"/> opened: a frame, its code read as code.</summary>
        private void Hole(Frame text)
        {
            Push(new Frame(Frame.Hole, _expression, _statement, _initializes, false) { Quotes = text.Quotes, Dollars = text.Dollars, Verbatim = text.Verbatim });
            _holes++;
            (_operand, _expressionBrace) = (false, true);
        }

        /// <summary>
        /// A line's documentation comment and those on the lines right after it, one comment: its XML read on from line to
        /// line, the <c>///</c> each begins with left out.
        /// </summary>
        private void DocumentationLines()
        {
            (_elements, _xml) = (0, XmlPart.Content);

            while (true)
            {
                var end = LineEnd(_at);
                Xml(_at + 3, end);
                _at = end;

                var next = end + (end < _text.Length && _text[end] == '\r' ? 1 : 0);
                next += next < _text.Length && _text[next] == '\n' ? 1 : 0;

                while (next < _text.Length && _text[next] is ' ' or '\t')
                {
                    next++;
                }

                if (next == end || !_text.At(next, "///") || (next + 3 < _text.Length && _text[next + 3] == '/'))
                {
                    return;
                }

                _at = next;
            }
        }

        /// <summary>A block's documentation comment from <paramref name="from"/> to <paramref name="to"/>: its XML.</summary>
        private void Documentation(long from, long to)
        {
            (_elements, _xml) = (0, XmlPart.Content);
            Xml(from, to);
        }

        /// <summary>The XML of a documentation comment from <paramref name="from"/> to <paramref name="to"/>, read on from where the read of it stands: each element open a level.</summary>
        private void Xml(long from, long to)
        {
            for (var at = from; at < to; at++)
            {
                var character = _text[at];
                var next = at + 1 < _text.Length ? _text[at + 1] : '\0';

                switch (_xml)
                {
                    case XmlPart.Content when character == '<':
                        if (next == '/')
                        {
                            (_elements, _xml) = (Math.Max(0, _elements - 1), XmlPart.EndTag);
                        }
                        else if (_text.At(at, "<!--"))
                        {
                            _xml = XmlPart.Comment;
                        }
                        else if (_text.At(at, "<![CDATA["))
                        {
                            _xml = XmlPart.Data;
                        }
                        else if (next is '?' or '!')
                        {
                            _xml = next == '?' ? XmlPart.Instruction : XmlPart.EndTag;
                        }
                        else if (char.IsLetter(next) || next is '_' or ':')
                        {
                            (_elements, _xml) = (_elements + 1, XmlPart.Tag);
                            Note(_elements, at);
                        }

                        break;

                    case XmlPart.Tag when character is '"' or '\'':
                        (_xmlQuote, _xml) = (character, XmlPart.Quoted);
                        break;

                    case XmlPart.Tag when character == '/' && next == '>':
                        (_elements, _xml) = (Math.Max(0, _elements - 1), XmlPart.Content);
                        at++;
                        break;

                    case XmlPart.Tag or XmlPart.EndTag when character == '>':
                        _xml = XmlPart.Content;
                        break;

                    case XmlPart.Quoted when character == _xmlQuote:
                        _xml = XmlPart.Tag;
                        break;

                    case XmlPart.Comment when _text.At(at, "-->"):
                    case XmlPart.Data when _text.At(at, "]]>"):
                    case XmlPart.Instruction when _text.At(at, "?>"):
                        _xml = XmlPart.Content;
                        break;
                }
            }
        }

        /// <summary>
        /// A directive, to its line's end: its condition's parentheses and negations each a level the directive parser takes;
        /// an <c>#if</c>'s, an <c>#elif</c>'s and an <c>#else</c>'s branch taken or left out as the compiler takes it, with no
        /// symbol defined but those the file defines; a <c>#define</c> and an <c>#undef</c> read where their code is.
        /// </summary>
        private void Directive()
        {
            var end = LineEnd(_at);
            var line = _text.Slice(_at + 1, end).AsSpan().TrimStart();
            var named = 0;

            while (named < line.Length && char.IsLetter(line[named]))
            {
                named++;
            }

            var name = line[..named];
            var rest = line[named..];

            if (rest.IndexOf("//", StringComparison.Ordinal) is var comment and >= 0)
            {
                rest = rest[..comment];
            }

            Note(rest.Count('(') + rest.Count('!'), _at);

            switch (name)
            {
                case "if":
                {
                    var taken = Active && Evaluate(rest);
                    _branches.Push(new Branch(Active, taken, taken));
                    break;
                }

                case "elif" when _branches.TryPop(out var branch):
                {
                    var taken = branch.Outer && !branch.Taken && Evaluate(rest);
                    _branches.Push(new Branch(branch.Outer, branch.Taken || taken, taken));
                    break;
                }

                case "else" when _branches.TryPop(out var branch):
                    _branches.Push(new Branch(branch.Outer, true, branch.Outer && !branch.Taken));
                    break;

                case "endif":
                    _branches.TryPop(out _);
                    break;

                case "define" or "undef" when Active && rest.Trim() is { Length: > 0 } symbol:
                    if (name is "define")
                    {
                        _defined.Add(symbol.ToString());
                    }
                    else
                    {
                        _defined.Remove(symbol.ToString());
                    }

                    break;
            }

            _at = end;
        }

        /// <summary>
        /// What a directive's <paramref name="condition"/> is, as the compiler takes it - a symbol true where the file
        /// defines it, <c>!</c>, <c>==</c>, <c>!=</c>, <c>&amp;&amp;</c> and <c>||</c> by their precedence, parentheses
        /// - on stacks of its own, however deep it nests; what is missing is false.
        /// </summary>
        private bool Evaluate(ReadOnlySpan<char> condition)
        {
            var values = new Stack<bool>();
            var operators = new Stack<char>();

            for (var at = 0; at < condition.Length;)
            {
                var character = condition[at];
                var next = at + 1 < condition.Length ? condition[at + 1] : '\0';

                if (char.IsWhiteSpace(character))
                {
                    at++;
                }
                else if (IsNameStart(character))
                {
                    var start = at;

                    while (at < condition.Length && IsNamePart(condition[at]))
                    {
                        at++;
                    }

                    var symbol = condition[start..at];
                    values.Push(symbol is "true" || (symbol is not "false" && _defined.Contains(symbol.ToString())));
                }
                else if (character == '(' || (character == '!' && next != '='))
                {
                    operators.Push(character);
                    at++;
                }
                else if (character == ')')
                {
                    while (operators.TryPop(out var open) && open != '(')
                    {
                        Apply(open);
                    }

                    at++;
                }
                else if ((character, next) is ('=', '=') or ('!', '=') or ('&', '&') or ('|', '|'))
                {
                    var binary = character == '!' ? 'n' : character;

                    while (operators.TryPeek(out var before) && before != '(' && Precedence(before) >= Precedence(binary))
                    {
                        Apply(operators.Pop());
                    }

                    operators.Push(binary);
                    at += 2;
                }
                else
                {
                    break;
                }
            }

            while (operators.TryPop(out var pending))
            {
                if (pending != '(')
                {
                    Apply(pending);
                }
            }

            return values.TryPeek(out var value) && value;

            void Apply(char operation)
            {
                if (operation == '!')
                {
                    values.Push(!(values.TryPop(out var operand) && operand));
                    return;
                }

                var right = values.TryPop(out var second) && second;
                var left = values.TryPop(out var first) && first;

                values.Push(operation switch
                {
                    '=' => left == right,
                    'n' => left != right,
                    '&' => left && right,
                    _ => left || right,
                });
            }

            static int Precedence(char operation) => operation switch
            {
                '!' => 4,
                '=' or 'n' => 3,
                '&' => 2,
                _ => 1,
            };
        }

        private static bool IsNameStart(char character) => char.IsLetter(character) || character == '_';

        private static bool IsNamePart(char character) => char.IsLetterOrDigit(character) || character == '_';

        /// <summary>
        /// A bracket, a brace, an interpolated string or one of its holes open, and what nested with no bracket where it
        /// opened, which it holds below it.
        /// </summary>
        /// <param name="Kind">What opened it.</param>
        /// <param name="Expression">The levels an expression nested to with no bracket where it opened.</param>
        /// <param name="Statement">The levels statements that hold the next nested to where it opened.</param>
        /// <param name="Initializes">Whether an initializer could follow where it opened.</param>
        /// <param name="ExpressionBrace">Whether it is a brace an expression opened, after which the expression goes on.</param>
        private readonly record struct Frame(char Kind, long Expression, long Statement, bool Initializes, bool ExpressionBrace)
        {
            public const char Paren = '(';
            public const char Bracket = '[';
            public const char Brace = '{';
            public const char Interpolated = '"';
            public const char Hole = 'h';

            /// <summary>A string's: how many quotes it opened with, one for a regular or a verbatim one.</summary>
            public long Quotes { get; init; }

            /// <summary>A string's: how many braces open and close a hole - its dollar signs for a raw one, else one.</summary>
            public long Dollars { get; init; }

            /// <summary>A string's: whether it is a verbatim one, which a line's end does not end.</summary>
            public bool Verbatim { get; init; }

            /// <summary>How many levels it is: two for a call's argument list, else one.</summary>
            public int Levels { get; init; } = 1;
        }

        /// <summary>A directive's branch: whether what holds it is code, whether a branch of it was taken, and whether this one is.</summary>
        private readonly record struct Branch(bool Outer, bool Taken, bool Active);
    }
}
