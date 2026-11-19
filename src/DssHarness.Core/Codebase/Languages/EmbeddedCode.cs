using System.Text;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// Code another language's file holds - an HTML page's scripts and style elements, a Vue component's script and style
/// blocks, a Razor file's C# - read by that language's own reader and made part of the host's extract: the one way every
/// markup reader embeds code. The reader is given the host's text with only the code kept, so what it declares carries
/// the host's lines; and every field of what it answers - nodes, scopes, imports, address roots, references, dynamic
/// marks, literals, the keys past those kept and why the file is generated - is merged into the host's, so nothing the
/// embedded reader saw is lost on the way.
/// </summary>
internal static class EmbeddedCode
{
    /// <summary>
    /// The text a reader of embedded code is given: the ranges of <paramref name="text"/> kept, each on the line it stands
    /// on in the host, and nothing else of it - the line breaks between them kept and every other character left out, two
    /// ranges on one line apart by a space, so no token of one runs into the next.
    /// </summary>
    /// <param name="text">The host's text, its line endings each one line feed.</param>
    /// <param name="kept">The ranges of code, each from its first character to past its last, in order and apart.</param>
    public static string Kept(string text, IReadOnlyList<(int Start, int End)> kept)
    {
        var built = new StringBuilder();
        var at = 0;

        foreach (var (start, end) in kept)
        {
            var from = Math.Clamp(start, at, text.Length);
            var to = Math.Clamp(end, from, text.Length);
            var breaks = text.AsSpan(at, from - at).Count('\n');

            _ = breaks > 0 ? built.Append('\n', breaks) : built.Length > 0 ? built.Append(' ') : built;
            _ = built.Append(text, from, to - from);
            at = to;
        }

        return built.ToString();
    }

    /// <summary>
    /// <paramref name="text"/> with each of <paramref name="blanked"/> blanked - every character of it a space but a line
    /// feed - so what is left stands where it stood, at its own offset and line: markup read with the code it holds taken
    /// out.
    /// </summary>
    /// <param name="text">The host's text, its line endings each one line feed.</param>
    /// <param name="blanked">The ranges taken out, each from its first character to past its last.</param>
    public static string Blanked(string text, IEnumerable<(int Start, int End)> blanked)
    {
        var characters = text.ToCharArray();

        foreach (var (start, end) in blanked)
        {
            for (var at = Math.Max(start, 0); at < Math.Min(end, characters.Length); at++)
            {
                if (characters[at] != '\n')
                {
                    characters[at] = ' ';
                }
            }
        }

        return new string(characters);
    }

    /// <summary>
    /// <paramref name="text"/> with every line ending one line feed - a CR LF and a CR alike - so the host and the code
    /// it embeds are read over one text whose offsets the markup's positions and the kept ranges share; its lines are
    /// the ones <see cref="CodeFileText.Lines"/> cuts.
    /// </summary>
    /// <param name="text">A file's text.</param>
    public static string LineFeeds(string text)
        => text.Contains('\r', StringComparison.Ordinal) ? text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n') : text;

    /// <summary>
    /// <paramref name="host"/> with every field of each of <paramref name="embedded"/> merged into it, in order: each
    /// node's id made unique among those already held - with its place among those alike, as <see cref="ExtractIds"/>
    /// gives it, the nodes it holds and the references it makes following it - each scope once, by its name, the
    /// imports, address roots, references, dynamic marks and literals after the host's, every one of each, the keys past
    /// those kept added up, and why the file is generated the host's own, else the first embedded reader's that says.
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
        var literals = new List<ExtractLiteral>(host.Literals ?? []);
        var keysBeyond = host.KeysBeyond;
        var generated = host.Generated;

        foreach (var extract in embedded)
        {
            // An id the host or an earlier reader gave is told apart; what the node holds and what it makes follow it.
            var given = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var node in extract.Nodes)
            {
                given[node.Id] = ids.Next(node.Id);
            }

            nodes.AddRange(extract.Nodes.Select(node => node with
            {
                Id = given[node.Id],
                Parent = node.Parent is { } parent && given.TryGetValue(parent, out var held) ? held : node.Parent,
                SuffixOf = node.SuffixOf?.Select(onto => given.TryGetValue(onto, out var written) ? written : onto).ToList(),
            }));
            references.AddRange(extract.References.Select(reference => given.TryGetValue(reference.From, out var from) ? reference with { From = from } : reference));

            foreach (var scope in extract.Scopes)
            {
                scopes.TryAdd(scope.Name, scope);
            }

            imports.AddRange(extract.Imports);
            roots.AddRange(extract.AddressRoots);
            dynamic.AddRange(extract.Dynamic);
            keysBeyond += extract.KeysBeyond;
            generated ??= extract.Generated;
            literals.AddRange(extract.Literals ?? []);
        }

        return new FileExtract(nodes)
        {
            Scopes = [.. scopes.Values],
            Imports = imports,
            AddressRoots = roots,
            References = references,
            Generated = generated,
            Dynamic = dynamic,
            Literals = literals.Count == 0 ? null : literals,
            KeysBeyond = keysBeyond,
        };
    }
}
