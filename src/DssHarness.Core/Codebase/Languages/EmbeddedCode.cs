using System.Globalization;
using System.Text;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// Code another language's file holds - an HTML page's scripts and style elements, a Vue component's script and style
/// blocks, a Razor file's C# - read by that language's own reader and made part of the host's extract: the one way every
/// markup reader embeds code. The reader is given the host's text with only the code kept, so what it declares carries
/// the host's lines; and every field of what it answers - nodes, scopes, imports, address roots, references, dynamic
/// marks, literals and why the file is generated - is merged into the host's, so nothing the
/// embedded reader saw is lost on the way.
/// </summary>
internal static class EmbeddedCode
{
    /// <summary>
    /// The text a reader of embedded code is given: the ranges of <paramref name="text"/> kept, each on the line it stands
    /// on in the host, and nothing else of it - the line breaks between them kept and every other character left out, two
    /// ranges on one line apart by a space, so no token of one runs into the next (<see cref="EmbeddedText"/>): a text made
    /// of the host's own runs (<see cref="CodeText.Composed"/>), never a copy of them.
    /// </summary>
    /// <param name="text">The host's text, its line endings each one line feed.</param>
    /// <param name="lines">Where the host's lines begin.</param>
    /// <param name="kept">The ranges of code, each from its first character to past its last, in order and apart.</param>
    public static CodeText Kept(CodeText text, CodeLineIndex lines, IReadOnlyList<(long Start, long End)> kept)
    {
        var written = new EmbeddedText(text, lines);
        var at = 0L;

        foreach (var (start, end) in kept)
        {
            var from = Math.Clamp(start, at, text.Length);
            var to = Math.Clamp(end, from, text.Length);

            written.Write(from, to);
            at = to;
        }

        return written.Text();
    }

    /// <summary>
    /// <paramref name="text"/> with each of <paramref name="blanked"/> blanked - every character of it a space but a line
    /// feed - so what is left stands where it stood, at its own offset and line: markup read with the code it holds taken
    /// out. A text made of the host's own runs, each blanked one read blank (<see cref="CodeTextPiece.Blanked"/>).
    /// </summary>
    /// <param name="text">The host's text, its line endings each one line feed.</param>
    /// <param name="blanked">The ranges taken out, each from its first character to past its last.</param>
    public static CodeText Blanked(CodeText text, IEnumerable<(long Start, long End)> blanked)
    {
        var pieces = new CodeTextPieces();
        var at = 0L;

        // Blanking twice is blanking once: the ranges in order, each overlap read as one.
        foreach (var (start, end) in blanked.Select(range => (Start: Math.Clamp(range.Start, 0, text.Length), End: Math.Clamp(range.End, 0, text.Length))).Where(range => range.End > range.Start).OrderBy(range => range.Start))
        {
            if (end <= at)
            {
                continue;
            }

            var from = Math.Max(start, at);
            pieces.Run(at, from).Blank(from, end);
            at = end;
        }

        return text.Composed(pieces.Run(at, text.Length).Pieces);
    }

    /// <summary>
    /// <paramref name="host"/> with every field of each of <paramref name="embedded"/> merged into it, in order: each
    /// node's id made unique among those already held - with its place among those alike, as <see cref="ExtractIds"/>
    /// gives it - and every field that names it following it (<see cref="FileExtract.Renamed"/>): the nodes it holds or is
    /// written onto, the references it makes and the literals it holds; each scope once, by its name, the imports, address
    /// roots, references, dynamic marks and literals after the host's, every one of each - each chain's links following
    /// the references they name (<see cref="ExtractQualifier.Link"/>), the literals by the one rule every reader keeps them
    /// by (<see cref="ExtractLiterals"/>) - and why the file is generated the host's own, else
    /// the first embedded reader's that says.
    /// </summary>
    /// <param name="host">The host's own extract: what its reader of markup made.</param>
    /// <param name="embedded">What each reader of the code it embeds made of it.</param>
    public static FileExtract Merged(FileExtract host, IEnumerable<FileExtract> embedded)
    {
        var ids = new ExtractIds();
        var nodes = new List<ExtractNode>();

        foreach (var node in host.Nodes)
        {
            nodes.Add(node with { Id = ids.Next(node.Id) });
        }

        var scopes = new Dictionary<string, ExtractScope>(StringComparer.Ordinal);

        foreach (var scope in host.Scopes)
        {
            scopes.TryAdd(scope.Name, scope);
        }

        var imports = new List<ExtractImport>(host.Imports);
        var roots = new List<ExtractAddressRoot>(host.AddressRoots);
        var references = new List<ExtractReference>(host.References);
        var dynamic = new List<ExtractDynamic>(host.Dynamic);
        var literals = new ExtractLiterals();
        var generated = host.Generated;

        literals.AddAll(host.Literals);

        foreach (var extract in embedded)
        {
            // An id the host or an earlier reader gave is told apart; whatever names the node follows it.
            var given = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var node in extract.Nodes)
            {
                given[node.Id] = ids.Next(node.Id);
            }

            var renamed = extract.Renamed(id => given.GetValueOrDefault(id, id));
            var before = references.Count;

            nodes.AddRange(renamed.Nodes);

            // A chain's link names a reference by its place among its own extract's, which follows it here.
            references.AddRange(renamed.References.Select(reference => reference.Qualifier is { Link: { } link } linked
                ? reference with { Qualifier = linked with { Link = link + before } }
                : reference));

            foreach (var scope in extract.Scopes)
            {
                scopes.TryAdd(scope.Name, scope);
            }

            imports.AddRange(extract.Imports);
            roots.AddRange(extract.AddressRoots);
            dynamic.AddRange(extract.Dynamic);
            generated ??= extract.Generated;
            literals.AddAll(renamed.Literals);
        }

        return new FileExtract(nodes)
        {
            Scopes = [.. scopes.Values],
            Imports = imports,
            AddressRoots = roots,
            References = references,
            Generated = generated,
            Dynamic = dynamic,
            Literals = literals.Kept(),
        };
    }

    /// <summary>
    /// <paramref name="read"/> with each node <paramref name="into"/> names a node of the host for folded into that node:
    /// the node gone, and every field that named it - what it holds, the references it makes, the literals it holds -
    /// naming the host's node (<see cref="FileExtract.Renamed"/>). What a reader declares to read the markup's code by - a
    /// component's options object, the function a template's or a handler's code is read in - is the markup's node it
    /// stands for.
    /// </summary>
    /// <param name="read">What the embedded code's reader made.</param>
    /// <param name="into">The id of the host's node a node is folded into; <see langword="null"/> for one kept.</param>
    public static FileExtract Folded(FileExtract read, Func<ExtractNode, string?> into)
    {
        var gone = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var node in read.Nodes)
        {
            if (into(node) is { } host)
            {
                gone[node.Id] = host;
            }
        }

        return gone.Count == 0 ? read : (read with { Nodes = [.. read.Nodes.Where(node => !gone.ContainsKey(node.Id))] }).Renamed(id => gone.GetValueOrDefault(id, id));
    }

    /// <summary>
    /// The JavaScript a template's or a page's expressions and handlers are read as, by the JavaScript reader, through
    /// <paramref name="regions"/>: each a function of its own - named by its place, its parameters the names the markup
    /// declares where its code stands that the code names - holding that code on the host's line it stands on, so the
    /// reader's own rules say what it calls and uses, what it declares and what it holds, and no name one region's code
    /// declares is another's; what the reader makes of it, each function folded into the host's node its region stands for.
    /// </summary>
    /// <param name="language">The host's implementation, whose name prefixes every id.</param>
    /// <param name="path">The host's path.</param>
    /// <param name="text">The host's text, its line endings each one line feed.</param>
    /// <param name="lines">Where the host's lines begin.</param>
    /// <param name="regions">The regions, in the order their code is written.</param>
    /// <param name="typescript">Whether the code is TypeScript, as the host's script is.</param>
    public static FileExtract Script(string language, string path, CodeText text, CodeLineIndex lines, IReadOnlyList<CodeRegion> regions, bool typescript)
    {
        var written = new EmbeddedText(text, lines);
        var hosts = Framed(written, regions, name => ($"function {name}(", ") {", "}"));

        return Folded(new JsReading(language, path, written.Text(), typescript, markup: false, module: false).Read(), node => node.Parent is null && hosts.TryGetValue(node.Name, out var host) ? host : null);
    }

    /// <summary>
    /// The C# a Razor file holds read as Razor compiles it into one class, by the C# reader: after
    /// <paramref name="head"/> - the class's opening - <paramref name="members"/> as written, and each of
    /// <paramref name="regions"/> - the code its markup holds - a field of its own whose value is a function, its
    /// parameters the names the markup's code declares where the region stands that it names, typed as each is declared,
    /// holding the region's code. A region's code may stand inside another's - an expression in the markup a statement
    /// switches to - so each piece is written on lines of its own and every line the reader names is given back as the
    /// host's line it stands for (<see cref="FileExtract.Relined"/>); the reader's own rules say what the code calls, makes
    /// and uses, each region sees the class's members, and no name one region's code declares is another's. What the
    /// reader makes of it, each field folded into the host's node its region stands for.
    /// </summary>
    /// <param name="language">The host's implementation, whose name prefixes every id.</param>
    /// <param name="path">The host's path.</param>
    /// <param name="lines">Where the host's lines begin.</param>
    /// <param name="lineCount">How many lines the host has (<see cref="CodeFileText.Lines"/>): no line past them is named.</param>
    /// <param name="head">The class's opening.</param>
    /// <param name="members">The class's members as written - its code blocks', a directive's - each where it stands in the host.</param>
    /// <param name="regions">The regions, each one's code a block's statements.</param>
    /// <param name="alone">
    /// What a name the regions' code reads or calls by itself - none of its own, no local's - is qualified by: the
    /// instance's, where the code is an instance's method; <see langword="null"/> to leave it as the reader says.
    /// </param>
    public static FileExtract CSharp(string language, string path, CodeLineIndex lines, int lineCount, string head, IReadOnlyList<(long Offset, string Code)> members, IReadOnlyList<CodeRegion> regions, ExtractQualifier? alone)
    {
        var written = new StringBuilder(head);
        var host = new List<int> { 1 };
        var hosts = new Dictionary<string, string>(StringComparer.Ordinal);
        var called = new HashSet<string>(StringComparer.Ordinal);

        // Each piece on a line of its own, the host's line it begins on said, and each line it breaks the host's next.
        void Write(long offset, string code)
        {
            var line = lines.LineAt(Math.Max(offset, 0));

            written.Append('\n').Append(code);
            host.Add(line);

            foreach (var character in code)
            {
                if (character == '\n')
                {
                    host.Add(++line);
                }
            }
        }

        var member = 0;

        for (var at = 0; at <= regions.Count; at++)
        {
            for (; member < members.Count && (at == regions.Count || members[member].Offset < regions[at].Offset); member++)
            {
                Write(members[member].Offset, members[member].Code);
            }

            if (at == regions.Count)
            {
                break;
            }

            var region = regions[at];
            var name = string.Create(CultureInfo.InvariantCulture, $"__region{at + 1}");

            hosts[name] = region.Node;

            if (region.Called)
            {
                called.Add(name);
            }

            Write(region.Offset, $"object {name} = async ({region.Parameters}) => {{ {region.Code} }};");
        }

        written.Append(" }");

        var read = CSharpExtractor.Read(language, new CodeFileInput(path, written.ToString())).Relined(line => FileExtract.LineWithin(host[Math.Clamp(line, 1, host.Count) - 1], lineCount));
        var frames = read.Nodes.Where(node => node.Parent is not null && hosts.ContainsKey(node.Name)).ToDictionary(node => node.Id, node => node.Name, StringComparer.Ordinal);

        // A region's own names are what its host's code reads by itself; a handler's call is given what the event gives.
        read = read with
        {
            References = [.. read.References.Select(reference => !frames.TryGetValue(reference.From, out var frame) ? reference
                : reference with
                {
                    Arity = called.Contains(frame) && reference.Kind == CodeEdgeKinds.Calls ? null : reference.Arity,
                    Qualifier = reference is { Qualifier: null, Kind: CodeEdgeKinds.Uses or CodeEdgeKinds.Calls } ? alone : reference.Qualifier,
                })],
        };

        return Folded(read, node => frames.ContainsKey(node.Id) ? hosts[node.Name] : null);
    }

    /// <summary>
    /// Writes each of <paramref name="regions"/> framed by <paramref name="frame"/> - what stands before its parameters,
    /// between them and its code and after it - on the host's lines, in the order they stand in the host; answers the
    /// name each frame is given, by its place, with the host's node it stands for.
    /// </summary>
    private static Dictionary<string, string> Framed(EmbeddedText written, IReadOnlyList<CodeRegion> regions, Func<string, (string Open, string Body, string Close)> frame)
    {
        var hosts = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var at = 0; at < regions.Count; at++)
        {
            var region = regions[at];
            var name = string.Create(CultureInfo.InvariantCulture, $"__region{at + 1}");
            var (open, body, close) = frame(name);

            hosts[name] = region.Node;
            written.Write(region.Offset, $"{open}{region.Parameters}{body}");
            written.Write(region.Offset, region.Code);
            written.Write(region.Offset, close);
        }

        return hosts;
    }
}

/// <summary>Code a markup holds that is read as one function's (<see cref="EmbeddedCode.Script"/>, <see cref="EmbeddedCode.CSharp"/>).</summary>
/// <param name="Node">The id of the host's node the code is the code of: an element's section, the component.</param>
/// <param name="Parameters">The parameters the function is given: the names the markup declares where the code stands that it names.</param>
/// <param name="Offset">Where the code stands in the host.</param>
/// <param name="Code">The code, its line breaks the host's own.</param>
internal sealed record CodeRegion(string Node, string Parameters, long Offset, string Code)
{
    /// <summary>
    /// Whether the code is the call of a handler a markup names, which its framework makes with whatever the event gives:
    /// its call says no count of arguments.
    /// </summary>
    public bool Called { get; init; }
}

/// <summary>
/// The names a markup's code declares for what stands after it, each over the stretch of the host it is declared for -
/// a loop's aliases over its element, a C# block's locals to its close - read in the order the host writes them: each
/// declared where it is reached and gone once its stretch is passed, so each name is held once however many regions name
/// it, and a region is given those it names alone (<see cref="Given"/>). Stretches nest, as scopes do, so the one
/// declared last is the first to end.
/// </summary>
internal sealed class DeclaredNames
{
    /// <summary>Each name declared where the walk is, with how each of its declarations writes a parameter of it, the innermost last.</summary>
    private readonly Dictionary<string, List<string>> _held = new(StringComparer.Ordinal);

    /// <summary>The stretches open, the innermost last: where each ends, and the names it declares.</summary>
    private readonly Stack<(long End, IReadOnlyList<(string Name, string Written)> Names)> _open = new();

    /// <summary>Declares <paramref name="names"/> - each by its name and how a parameter of it is written - until <paramref name="end"/>.</summary>
    public void Declare(IReadOnlyList<(string Name, string Written)> names, long end)
    {
        if (names.Count == 0)
        {
            return;
        }

        foreach (var (name, written) in names)
        {
            if (!_held.TryGetValue(name, out var declarations))
            {
                _held[name] = declarations = [];
            }

            declarations.Add(written);
        }

        _open.Push((end, names));
    }

    /// <summary>Declares <paramref name="names"/> - each written as its name alone - until <paramref name="end"/>.</summary>
    public void Declare(IEnumerable<string> names, long end) => Declare([.. names.Select(name => (name, name))], end);

    /// <summary>Leaves every stretch that ends at or before <paramref name="offset"/>: what stands there is declared in none of them.</summary>
    public void Leave(long offset)
    {
        while (_open.Count > 0 && _open.Peek().End <= offset)
        {
            foreach (var (name, _) in _open.Pop().Names)
            {
                var declarations = _held[name];
                declarations.RemoveAt(declarations.Count - 1);

                if (declarations.Count == 0)
                {
                    _held.Remove(name);
                }
            }
        }
    }

    /// <summary>
    /// The parameters a region whose code is <paramref name="code"/> is given: each name declared where the walk stands
    /// that the code writes - a name in a string or a comment among them, which a parameter it is not read by does not
    /// change - written as its innermost declaration writes it, in the order the code first writes each, apart by commas.
    /// </summary>
    public string Given(string code)
    {
        if (_held.Count == 0)
        {
            return string.Empty;
        }

        var given = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var at = 0; at < code.Length;)
        {
            if (!IsName(code[at]) || char.IsAsciiDigit(code[at]))
            {
                at++;
                continue;
            }

            var start = at;

            while (at < code.Length && IsName(code[at]))
            {
                at++;
            }

            var name = code[start..at];

            if (_held.TryGetValue(name, out var declarations) && seen.Add(name))
            {
                given.Add(declarations[^1]);
            }
        }

        return string.Join(", ", given);
    }

    /// <summary>Whether <paramref name="character"/> may be part of a name in the code a markup holds: JavaScript's and C#'s alike.</summary>
    private static bool IsName(char character) => char.IsLetterOrDigit(character) || character is '_' or '$';
}

/// <summary>
/// Code a reader of embedded code is given, written piece by piece on the host's lines: each piece on the line its
/// offset stands on in the host - or after what is written already, where that ends on a later line - the line breaks
/// between pieces kept and nothing else of the host, two pieces on one line apart by a space so no token of one runs into
/// the next. A piece is a run of the host's own code, or code that frames it - a function's head and its close - alike, so
/// what the reader declares and references carries the host's lines; what is written is a text made of the host's runs
/// and the frames between them (<see cref="CodeText.Composed"/>), never a copy of the host.
/// </summary>
/// <param name="host">The host's text.</param>
/// <param name="lines">Where the host's lines begin.</param>
internal sealed class EmbeddedText(CodeText host, CodeLineIndex lines)
{
    private readonly CodeTextPieces _pieces = new();

    /// <summary>How many characters are written.</summary>
    private long _written;

    /// <summary>The host's line what is written last ends on.</summary>
    private int _line = 1;

    /// <summary>Writes <paramref name="code"/> on the host's line <paramref name="offset"/> stands on, or after what is written already.</summary>
    /// <param name="offset">Where the code stands in the host.</param>
    /// <param name="code">The code, its line breaks the host's own.</param>
    public void Write(long offset, string code)
    {
        Place(offset);
        _pieces.Write(code);
        _written += code.Length;
        _line += code.AsSpan().Count('\n');
    }

    /// <summary>Writes the host's code from <paramref name="start"/> to before <paramref name="end"/> on the line it stands on, or after what is written already.</summary>
    /// <param name="start">Where the code begins in the host.</param>
    /// <param name="end">Where it ends.</param>
    public void Write(long start, long end)
    {
        Place(start);
        _pieces.Run(start, end);
        _written += end - start;

        for (var at = start < end ? host.Find('\n', start, end) : -1; at >= 0; at = at + 1 < end ? host.Find('\n', at + 1, end) : -1)
        {
            _line++;
        }
    }

    /// <summary>What is written.</summary>
    public CodeText Text() => host.Composed(_pieces.Pieces);

    /// <summary>Goes on to the host's line <paramref name="offset"/> stands on, or puts a space after what is written already on it.</summary>
    private void Place(long offset)
    {
        var line = lines.LineAt(Math.Max(offset, 0));

        if (line > _line)
        {
            _pieces.Write('\n', line - _line);
            _written += line - _line;
            _line = line;
        }
        else if (_written > 0)
        {
            _pieces.Write(" ");
            _written++;
        }
    }
}
