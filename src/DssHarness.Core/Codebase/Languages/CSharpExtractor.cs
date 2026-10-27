using System.Globalization;
using System.Text;
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
/// is one: the linker finds the member it is, and the call through that member's type, or by its name alone. A type a
/// declaration names is a type qualifier's. A field, a property and an event say the type their value is declared with, and an extension method the
/// type it extends. What it cannot bind - a call on a <c>dynamic</c> value, a member named by a string to reflection,
/// <c>Activator.CreateInstance</c>, a delegate's invocation - is marked dynamic.
/// </para>
/// </remarks>
public sealed class CSharpExtractor : ICodeGraphExtractor
{
    /// <summary>The newest syntax the parser reads, its documentation comments read, and no preprocessor symbol defined.</summary>
    private static readonly CSharpParseOptions Options = new(LanguageVersion.Preview, DocumentationMode.Parse);

    /// <inheritdoc/>
    public string Language => "csharp";

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public CodeGraphClaims Claims { get; } = CodeGraphClaims.OfExtensions(".cs");

    /// <inheritdoc/>
    public bool Outlines => false;

    /// <inheritdoc/>
    public CodeGraphExtractorHelp Help { get; } = new(
        "C# (.cs), through the compiler's own parser and its syntax alone, nothing built: every namespace, type and member with its signature, summary and body; usings, base lists, overrides and test attributes; each call with what its receiver is where the file says",
        [
            "code under a preprocessor branch the parse does not take: it defines no symbol, so code under #if DEBUG is out",
            "the type of a value no declaration says - a var given a call's result, a lambda's parameter: a call on it is linked by its name alone",
            "local functions and lambdas as nodes of their own: what they call, the member they are written in calls",
            "a call on a dynamic value, a member named by a string to reflection, Activator.CreateInstance and a delegate's invocation: marked dynamic, and not linked",
            "a generic extension method on a type parameter with no constraint, found by its name alone; a constructor's base(...) in a part of a partial class whose base list is in another file",
            "code a source generator writes while the project builds, which is in no file of the tree",
            "two types of one name in two projects - a Program in each - which are one node, as a partial type's parts are; a record's positional property as an override of its base's",
        ]);

    /// <inheritdoc/>
    public FileExtract Extract(CodeFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var root = CSharpSyntaxTree.ParseText(input.Text, Options).GetCompilationUnitRoot();
        return new Reading($"{Language}:", input.Path, CodeFileText.LineIndex(input.Text), root).Read();
    }

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
    private sealed record TypeFrame(ExtractNode Node, string IdName, bool Local, string Identifier, string? Inherited, bool Interface, IReadOnlyDictionary<string, string?> Fields, IReadOnlySet<string> TypeParameters)
    {
        /// <summary>The receiver's type of the extension block its members are declared in; <see langword="null"/> outside one.</summary>
        public TypeSyntax? Receiver { get; init; }
    }

    /// <summary>The values a member's code reads by a name alone - its own variables, then its type's fields - each with the type it is declared with where the file says.</summary>
    /// <param name="Locals">Its parameters and variables: a local hides a field.</param>
    /// <param name="Fields">Its type's fields, properties and events.</param>
    private sealed record Known(IReadOnlyDictionary<string, string?> Locals, IReadOnlyDictionary<string, string?> Fields)
    {
        /// <summary>Whether <paramref name="name"/> is a value the member reads, and the type it is declared with where the file says.</summary>
        public bool TryGet(string name, out string? declared) => Locals.TryGetValue(name, out declared) || Fields.TryGetValue(name, out declared);
    }

    /// <summary>One file's reading: what it declares, imports, references and cannot bind, gathered as its tree is walked.</summary>
    private sealed class Reading(string prefix, string path, CodeLineIndex lines, CompilationUnitSyntax root)
    {
        private readonly List<ExtractNode> _nodes = [];
        private readonly Dictionary<string, ExtractScope> _scopes = new(StringComparer.Ordinal);
        private readonly List<ExtractImport> _imports = [];
        private readonly List<ExtractReference> _references = [];
        private readonly List<ExtractDynamic> _dynamic = [];
        private readonly ExtractIds _ids = new();

        /// <summary>The index in the nodes of each namespace's and type's first node, by its id, kind and parent: where a later part of it runs on.</summary>
        private readonly Dictionary<(string Id, string Kind, string? Parent), int> _parts = [];

        /// <summary>The class each type of the file inherits, by the type's id, as its constructors are named: a partial type's base list may be on another part.</summary>
        private readonly Dictionary<string, string> _inherited = new(StringComparer.Ordinal);

        /// <summary>The delegate types the file declares, by name: a variable of one is called only through the delegate.</summary>
        private readonly HashSet<string> _delegates = root.DescendantNodes(node => node is not BlockSyntax).OfType<DelegateDeclarationSyntax>()
            .Select(declared => declared.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);

        public FileExtract Read()
        {
            foreach (var directive in root.Usings)
            {
                Import(directive);
            }

            if (root.Members.OfType<GlobalStatementSyntax>().ToList() is { Count: > 0 } statements)
            {
                TopLevel(statements);
            }

            foreach (var member in root.Members.Where(member => member is not GlobalStatementSyntax))
            {
                Declare(member, scope: null, parent: null, type: null);
            }

            return new FileExtract(_nodes)
            {
                Scopes = [.. _scopes.Values],
                Imports = _imports,
                References = _references,
                Generated = Generated(),
                Dynamic = _dynamic,
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

        /// <summary>A declaration at a file's or a namespace's level, or one inside <paramref name="type"/>.</summary>
        private void Declare(MemberDeclarationSyntax member, string? scope, ExtractNode? parent, TypeFrame? type)
        {
            switch (member)
            {
                case BaseNamespaceDeclarationSyntax declared:
                    Namespace(declared, scope, parent);
                    break;

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

                    foreach (var inner in block.Members)
                    {
                        Declare(inner, scope, parent, type with { Receiver = receiver?.Type, Fields = fields, TypeParameters = typeParameters });
                    }

                    break;
                }

                case BaseTypeDeclarationSyntax declared:
                    Type(declared, scope, parent, type);
                    break;

                case DelegateDeclarationSyntax declared:
                    Delegate(declared, scope, parent, type);
                    break;

                // A member at a namespace's level is no C#: a file cut short, or not yet written.
                default:
                    if (type is not null)
                    {
                        Member(member, type);
                    }

                    break;
            }
        }

        private void Namespace(BaseNamespaceDeclarationSyntax declared, string? scope, ExtractNode? parent)
        {
            if (Written(declared.Name) is not { } written)
            {
                foreach (var member in declared.Members)
                {
                    Declare(member, scope, parent, type: null);
                }

                return;
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

            var node = Part(qualified, CodeNodeKinds.Namespace, parent, declared, () => new ExtractNode(string.Empty, CodeNodeKinds.Namespace, qualified[(qualified.LastIndexOf('.') + 1)..], qualified, Line(declared.SpanStart), EndLine(declared), ExtractText.Signature($"namespace {written}"))
            {
                Scope = qualified.LastIndexOf('.') is var dot and > 0 ? qualified[..dot] : null,
                Parent = parent?.Id,
                BodyStart = declared is NamespaceDeclarationSyntax block ? LineOf(block.OpenBraceToken) : null,
            });

            foreach (var directive in declared.Usings)
            {
                Import(directive);
            }

            foreach (var member in declared.Members)
            {
                Declare(member, qualified, node, type: null);
            }
        }

        private void Type(BaseTypeDeclarationSyntax declared, string? scope, ExtractNode? parent, TypeFrame? outer)
        {
            var identifier = declared.Identifier.ValueText;

            if (identifier.Length == 0)
            {
                return;
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

            var frame = new TypeFrame(node, idName, local, identifier, inherited, isInterface, FieldTypes(declared, typeParameters), typeParameters);

            // A record's positional parameters are its properties, but for one its body declares itself.
            if (declared is RecordDeclarationSyntax { ParameterList: { } positional } declaredRecord)
            {
                var own = FieldTypes(declaredRecord.WithParameterList(null), typeParameters);

                foreach (var parameter in positional.Parameters.Where(parameter => parameter.Identifier.ValueText is { Length: > 0 } named && !own.ContainsKey(named)))
                {
                    Positional(parameter, declaredRecord, frame);
                }
            }

            switch (declared)
            {
                case TypeDeclarationSyntax withMembers:
                    foreach (var member in withMembers.Members)
                    {
                        Declare(member, qualified, node, frame);
                    }

                    break;

                case EnumDeclarationSyntax enumeration:
                    foreach (var member in enumeration.Members)
                    {
                        Member(member, frame);
                    }

                    break;
            }
        }

        private void Delegate(DelegateDeclarationSyntax declared, string? scope, ExtractNode? parent, TypeFrame? outer)
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
                Walk(node, type, [variable], type.TypeParameters);
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
        private ExtractNode Part(string idName, string kind, ExtractNode? parent, SyntaxNode declared, Func<ExtractNode> made)
        {
            var key = ($"{prefix}{idName}", kind, parent?.Id);

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
        }

        /// <summary>
        /// What <paramref name="code"/> calls, makes and names, from <paramref name="from"/>: each invocation with its
        /// receiver's kind, each <c>new</c> - a target-typed one by the type it is given to - and each type a local
        /// variable, a <c>typeof</c>, a cast, an <c>as</c>, an <c>is</c> or a pattern names.
        /// </summary>
        private void Walk(ExtractNode from, TypeFrame? type, IEnumerable<SyntaxNode> code, IReadOnlySet<string> typeParameters)
        {
            var nodes = code.SelectMany(part => part.DescendantNodesAndSelf()).ToList();
            var known = new Known(Locals(nodes, typeParameters), type?.Fields ?? new Dictionary<string, string?>(StringComparer.Ordinal));
            var functions = nodes.OfType<LocalFunctionStatementSyntax>().Select(function => function.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);

            foreach (var node in nodes)
            {
                switch (node)
                {
                    case InvocationExpressionSyntax invocation:
                        Invocation(from, invocation, known, functions);
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
                }
            }
        }

        /// <summary>One invocation: a call by its name, its argument count and its receiver, or a mark where it cannot be bound.</summary>
        private void Invocation(ExtractNode from, InvocationExpressionSyntax invocation, Known known, IReadOnlySet<string> functions)
        {
            var arity = invocation.ArgumentList.Arguments.Count;

            switch (invocation.Expression)
            {
                case IdentifierNameSyntax called:
                {
                    var name = called.Identifier.ValueText;

                    // nameof names and calls nothing; a local function's calls are its member's own.
                    if (name == "nameof" || functions.Contains(name))
                    {
                        return;
                    }

                    if (known.TryGet(name, out var declared))
                    {
                        Dynamic(called, declared == DynamicType ? $"a call on '{name}', which is dynamic" : $"a delegate invoked through '{name}'");
                        return;
                    }

                    Call(from, name, arity, Line(called.SpanStart), qualifier: null);
                    break;
                }

                case GenericNameSyntax called when !functions.Contains(called.Identifier.ValueText):
                    Call(from, called.Identifier.ValueText, arity, Line(called.SpanStart), qualifier: null);
                    break;

                case MemberAccessExpressionSyntax access when access.IsKind(SyntaxKind.SimpleMemberAccessExpression):
                    Accessed(from, invocation, access.Expression, access.Name, known);
                    break;

                // x?.Call(): the receiver is what the conditional access reads.
                case MemberBindingExpressionSyntax binding:
                    Accessed(from, invocation, invocation.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault(access => access.WhenNotNull.Span.Contains(invocation.Span))?.Expression, binding.Name, known);
                    break;
            }
        }

        /// <summary>A call of <paramref name="name"/> on <paramref name="receiver"/>, qualified by what the receiver is where the file says, or marked where it cannot be bound.</summary>
        private void Accessed(ExtractNode from, InvocationExpressionSyntax invocation, ExpressionSyntax? receiver, SimpleNameSyntax name, Known known)
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

            switch (receiver)
            {
                case ThisExpressionSyntax:
                    Call(from, member, arity, line, new ExtractQualifier(CodeQualifierKinds.This));
                    return;

                case BaseExpressionSyntax:
                    Call(from, member, arity, line, new ExtractQualifier(CodeQualifierKinds.Base));
                    return;
            }

            if (DynamicRoot(receiver, known) is { } loose)
            {
                Dynamic(name, $"a call on '{loose}', which is dynamic");
                return;
            }

            if (member == "Invoke" && Path(receiver, out _) is [var value] && known.TryGet(value, out var declared) && declared is not null && IsDelegate(declared))
            {
                Dynamic(name, $"a delegate invoked through '{value}'");
                return;
            }

            Call(from, member, arity, line, Receiver(receiver, known));
        }

        /// <summary>
        /// What a call's <paramref name="receiver"/> is: a type it is made or cast as; a variable, parameter or field the
        /// member reads, by the type it is declared with - each a type qualifier - a field's chain of members by its names,
        /// for the linker to follow, or a value of no said type; a name no variable has, as a named qualifier - a type's, a
        /// namespace's or a value's - where it begins with a capital letter, else as a value of another file by its name;
        /// anything else a value of no said type.
        /// </summary>
        private static ExtractQualifier Receiver(ExpressionSyntax? receiver, Known known)
        {
            switch (receiver)
            {
                case ObjectCreationExpressionSyntax created when NameOf(created.Type) is { } made:
                    return new ExtractQualifier(CodeQualifierKinds.Type, ExtractText.Qualify(made.Qualifier, made.Name));

                case ParenthesizedExpressionSyntax { Expression: CastExpressionSyntax cast } when Key(cast.Type) is { } to:
                    return new ExtractQualifier(CodeQualifierKinds.Type, to);

                case PredefinedTypeSyntax predefined:
                    return new ExtractQualifier(CodeQualifierKinds.Type, predefined.Keyword.ValueText);
            }

            if (Path(receiver, out var throughThis) is not { } path)
            {
                return new ExtractQualifier(CodeQualifierKinds.Value);
            }

            var head = path[0];
            var chain = string.Join('.', path);

            // A field named through this is the field, whatever variable the member names alike.
            if (!throughThis && known.Locals.TryGetValue(head, out var local))
            {
                return local is not null && path.Count == 1 ? new ExtractQualifier(CodeQualifierKinds.Type, local) : new ExtractQualifier(CodeQualifierKinds.Value);
            }

            if (known.Fields.TryGetValue(head, out var field))
            {
                return field is not null && path.Count == 1 ? new ExtractQualifier(CodeQualifierKinds.Type, field) : new ExtractQualifier(CodeQualifierKinds.Value, chain);
            }

            // A name given type arguments is a type's, and no value's.
            return char.IsUpper(head[0]) && !throughThis
                ? new ExtractQualifier(path.Any(part => part.Contains('`', StringComparison.Ordinal)) ? CodeQualifierKinds.Type : CodeQualifierKinds.Named, chain)
                : new ExtractQualifier(CodeQualifierKinds.Value, chain);
        }

        /// <summary>
        /// The variable or cast a receiver's chain begins at, where it is <c>dynamic</c>: what the call is on, written as the
        /// code names it; <see langword="null"/> where it is not.
        /// </summary>
        private static string? DynamicRoot(ExpressionSyntax? receiver, Known known)
        {
            var at = receiver;

            while (true)
            {
                switch (at)
                {
                    case MemberAccessExpressionSyntax access when access.Expression is not ThisExpressionSyntax:
                        at = access.Expression;
                        continue;

                    case MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } access:
                        return known.Fields.TryGetValue(access.Name.Identifier.ValueText, out var field) && field == DynamicType ? access.Name.Identifier.ValueText : null;

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
                        return $"({DynamicType})";

                    case ParenthesizedExpressionSyntax parenthesized:
                        at = parenthesized.Expression;
                        continue;

                    case IdentifierNameSyntax identifier:
                        return known.TryGet(identifier.Identifier.ValueText, out var declared) && declared == DynamicType ? identifier.Identifier.ValueText : null;

                    default:
                        return null;
                }
            }
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

        /// <summary>
        /// The variables <paramref name="nodes"/> declare - parameters, locals, pattern, loop and query variables, a lambda's
        /// - by name, each with the type it is declared with where the file says: a name declared twice with two types is
        /// said by neither.
        /// </summary>
        private static Dictionary<string, string?> Locals(IReadOnlyList<SyntaxNode> nodes, IReadOnlySet<string> typeParameters)
        {
            var locals = new Dictionary<string, string?>(StringComparer.Ordinal);

            void Declared(SyntaxToken identifier, TypeSyntax? written)
            {
                if (identifier.ValueText is { Length: > 0 } name)
                {
                    var declared = written is null ? null : ValueKey(written, typeParameters);
                    locals[name] = locals.TryGetValue(name, out var known) && known != declared ? null : declared;
                }
            }

            foreach (var node in nodes)
            {
                switch (node)
                {
                    case ParameterSyntax parameter:
                        Declared(parameter.Identifier, parameter.Type);
                        break;

                    case VariableDeclarationSyntax declaration:
                        foreach (var variable in declaration.Variables)
                        {
                            Declared(variable.Identifier, declaration.Type.IsVar ? Inferred(variable.Initializer?.Value) : declaration.Type);
                        }

                        break;

                    case SingleVariableDesignationSyntax designation:
                        Declared(designation.Identifier, designation.Parent switch
                        {
                            DeclarationExpressionSyntax declared => declared.Type,
                            DeclarationPatternSyntax declared => declared.Type,
                            RecursivePatternSyntax declared => declared.Type,
                            _ => null,
                        });
                        break;

                    case ForEachStatementSyntax loop:
                        Declared(loop.Identifier, loop.Type);
                        break;

                    case CatchDeclarationSyntax caught:
                        Declared(caught.Identifier, caught.Type);
                        break;

                    case FromClauseSyntax clause:
                        Declared(clause.Identifier, clause.Type);
                        break;

                    case LetClauseSyntax clause:
                        Declared(clause.Identifier, null);
                        break;

                    case JoinClauseSyntax clause:
                        Declared(clause.Identifier, clause.Type);
                        break;

                    case JoinIntoClauseSyntax clause:
                        Declared(clause.Identifier, null);
                        break;

                    case QueryContinuationSyntax clause:
                        Declared(clause.Identifier, null);
                        break;
                }
            }

            return locals;
        }

        /// <summary>The type a <c>var</c> is given where its initializer says: a <c>new</c> of a named type, or a cast.</summary>
        private static TypeSyntax? Inferred(ExpressionSyntax? value) => value switch
        {
            ObjectCreationExpressionSyntax created => created.Type,
            CastExpressionSyntax cast => cast.Type,
            _ => null,
        };

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

        /// <summary>
        /// The types <paramref name="type"/> names, each a <c>references</c> from <paramref name="from"/>: itself where it
        /// is named, each type argument, an array's or a nullable's element, a tuple's parts. A type parameter, a built-in
        /// type, <c>var</c> and <c>dynamic</c> are none.
        /// </summary>
        private void Mention(ExtractNode from, TypeSyntax? type, IReadOnlySet<string> typeParameters)
        {
            switch (type)
            {
                case NameSyntax named when NameOf(named) is { } target && !typeParameters.Contains(target.Name) && !IsContextual(named):
                    Reference(from, CodeEdgeKinds.References, target, named);
                    Arguments(from, named, typeParameters);
                    break;

                case NullableTypeSyntax nullable:
                    Mention(from, nullable.ElementType, typeParameters);
                    break;

                case ArrayTypeSyntax array:
                    Mention(from, array.ElementType, typeParameters);
                    break;

                case PointerTypeSyntax pointer:
                    Mention(from, pointer.ElementType, typeParameters);
                    break;

                case RefTypeSyntax reference:
                    Mention(from, reference.Type, typeParameters);
                    break;

                case ScopedTypeSyntax scoped:
                    Mention(from, scoped.Type, typeParameters);
                    break;

                case TupleTypeSyntax tuple:
                    foreach (var element in tuple.Elements)
                    {
                        Mention(from, element.Type, typeParameters);
                    }

                    break;
            }
        }

        /// <summary>The type arguments a named type is written with, each mentioned.</summary>
        private void Arguments(ExtractNode from, TypeSyntax type, IReadOnlySet<string> typeParameters)
        {
            var generic = type switch
            {
                GenericNameSyntax name => name,
                QualifiedNameSyntax { Right: GenericNameSyntax name } => name,
                AliasQualifiedNameSyntax { Name: GenericNameSyntax name } => name,
                NullableTypeSyntax nullable => nullable.ElementType as GenericNameSyntax,
                _ => null,
            };

            foreach (var argument in generic?.TypeArgumentList.Arguments ?? [])
            {
                Mention(from, argument, typeParameters);
            }
        }

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

                var tick = named.Name.IndexOf('`', StringComparison.Ordinal);
                var plain = tick < 0 ? named.Name : named.Name[..tick];
                var arity = tick < 0 ? string.Empty : named.Name[tick..];

                Reference(from, CodeEdgeKinds.References, (plain.EndsWith("Attribute", StringComparison.Ordinal) ? plain : $"{plain}Attribute") + arity, named.Qualifier, Line(attribute.SpanStart));
                Arguments(from, attribute.Name, typeParameters);
            }
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
            => Reference(from, kind, target.Name, target.Qualifier, Line(at.SpanStart));

        private void Reference(ExtractNode from, string kind, string name, string? qualifier, int line)
            => _references.Add(new ExtractReference(from.Id, kind, name, line)
            {
                Qualifier = qualifier is null ? null : new ExtractQualifier(CodeQualifierKinds.Type, qualifier),
            });

        private void Dynamic(SyntaxNode at, string why) => _dynamic.Add(new ExtractDynamic(Line(at.SpanStart), why));

        private void Scope(string name, string? parent, bool open = true) => _scopes.TryAdd(name, new ExtractScope(name, open) { Parent = parent });

        private ExtractNode Add(ExtractNode node)
        {
            _nodes.Add(node);
            return node;
        }

        /// <summary>The id of a declaration named so: the language's prefix and the name, told apart from an earlier one alike by its place.</summary>
        private string Id(string name) => _ids.Next($"{prefix}{name}");

        /// <summary>The id of a member of <paramref name="type"/> identified by <paramref name="tail"/>: an extension block's with the block's receiver.</summary>
        private string MemberId(TypeFrame type, string tail)
            => Id($"{type.IdName}.{(type.Receiver is { } receiver ? $"extension({TypeText(receiver)})." : string.Empty)}{tail}");

        /// <summary>
        /// The name a type's id is made of, and whether it is its file's alone: a file-local type's - and one declared in
        /// it - holds its file's path, as the statements at a file's top do, since many files may each have one alike.
        /// </summary>
        private (string IdName, bool Local) Named(TypeFrame? outer, SyntaxTokenList modifiers, string name, string qualified)
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

    /// <summary>The text of a documentation comment's content as it reads: a <c>&lt;see cref&gt;</c> as what it names, an element as its text.</summary>
    private static string Prose(SyntaxList<XmlNodeSyntax> content)
        => string.Concat(content.Select(node => node switch
        {
            XmlTextSyntax text => string.Concat(text.TextTokens.Select(token => token.IsKind(SyntaxKind.XmlTextLiteralNewLineToken) ? " " : token.ValueText)),
            XmlElementSyntax element => Prose(element.Content),
            XmlEmptyElementSyntax empty => empty.Attributes.FirstOrDefault() switch
            {
                XmlCrefAttributeSyntax cref => cref.Cref.ToString(),
                XmlNameAttributeSyntax name => name.Identifier.Identifier.ValueText,
                XmlTextAttributeSyntax text => string.Concat(text.TextTokens.Select(token => token.ValueText)),
                _ => string.Empty,
            },
            XmlCDataSectionSyntax data => string.Concat(data.TextTokens.Select(token => token.ValueText)),
            _ => string.Empty,
        }));

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
    private static (string Name, string? Qualifier)? NameOf(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax name when name.Identifier.ValueText.Length > 0 => (name.Identifier.ValueText, null),
        GenericNameSyntax name when name.Identifier.ValueText.Length > 0 => (Generic(name.Identifier.ValueText, name.TypeArgumentList.Arguments.Count), null),
        QualifiedNameSyntax qualified when NameOf(qualified.Right) is { } right && Written(qualified.Left) is { } left => (right.Name, left),
        AliasQualifiedNameSyntax aliased => NameOf(aliased.Name),
        NullableTypeSyntax nullable => NameOf(nullable.ElementType),
        _ => null,
    };

    /// <summary>
    /// A type as a value of it is qualified by: its name as written, a generic one with its count of type arguments;
    /// <c>dynamic</c> as itself; <see langword="null"/> where the file does not say one - <c>var</c>, an array, a tuple, a
    /// pointer.
    /// </summary>
    private static string? Key(TypeSyntax type) => type switch
    {
        NullableTypeSyntax nullable => Key(nullable.ElementType),
        IdentifierNameSyntax { IsVar: true } => null,
        IdentifierNameSyntax { Identifier.ValueText: DynamicType } => DynamicType,
        RefTypeSyntax reference => Key(reference.Type),
        ScopedTypeSyntax scoped => Key(scoped.Type),
        NameSyntax or PredefinedTypeSyntax => Written(type),
        _ => null,
    };

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

    /// <summary>
    /// The names <paramref name="receiver"/> writes, where it is a chain of them - an identifier, a type's name, members
    /// of it - with <c>this.</c> before it left out and said in <paramref name="throughThis"/>; <see langword="null"/>
    /// where it is anything else.
    /// </summary>
    private static List<string>? Path(ExpressionSyntax? receiver, out bool throughThis)
    {
        throughThis = false;

        switch (receiver)
        {
            case IdentifierNameSyntax identifier when identifier.Identifier.ValueText.Length > 0:
                return [identifier.Identifier.ValueText];

            case GenericNameSyntax generic when generic.Identifier.ValueText.Length > 0:
                return [Generic(generic.Identifier.ValueText, generic.TypeArgumentList.Arguments.Count)];

            case AliasQualifiedNameSyntax aliased:
                return Path(aliased.Name, out throughThis);

            // `x!` and `(x)` are x.
            case PostfixUnaryExpressionSyntax suppressed when suppressed.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                return Path(suppressed.Operand, out throughThis);

            case ParenthesizedExpressionSyntax parenthesized:
                return Path(parenthesized.Expression, out throughThis);

            case MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } access when access.IsKind(SyntaxKind.SimpleMemberAccessExpression) && access.Name.Identifier.ValueText.Length > 0:
                throughThis = true;
                return [access.Name.Identifier.ValueText];

            case MemberAccessExpressionSyntax access when access.IsKind(SyntaxKind.SimpleMemberAccessExpression) && Path(access.Expression, out throughThis) is { } left && Path(access.Name, out _) is [var right]:
                left.Add(right);
                return left;

            default:
                return null;
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
    /// with its count of type arguments, <c>global::</c> left out; <see langword="null"/> where it is anything else.
    /// </summary>
    private static string? Written(ExpressionSyntax? name) => name switch
    {
        IdentifierNameSyntax identifier when identifier.Identifier.ValueText.Length > 0 => identifier.Identifier.ValueText,
        GenericNameSyntax generic when generic.Identifier.ValueText.Length > 0 => Generic(generic.Identifier.ValueText, generic.TypeArgumentList.Arguments.Count),
        QualifiedNameSyntax qualified when Written(qualified.Left) is { } left && Written(qualified.Right) is { } right => ExtractText.Qualify(left, right),
        AliasQualifiedNameSyntax aliased => Written(aliased.Name),
        MemberAccessExpressionSyntax access when access.IsKind(SyntaxKind.SimpleMemberAccessExpression) && Written(access.Expression) is { } left && Written(access.Name) is { } right => ExtractText.Qualify(left, right),
        PredefinedTypeSyntax predefined => predefined.Keyword.ValueText,
        _ => null,
    };

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
}
