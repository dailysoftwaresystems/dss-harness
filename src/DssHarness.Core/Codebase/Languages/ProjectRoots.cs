namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// The address roots a project file's mappings of addresses to paths declare, alike for a Node package's
/// <c>exports</c> and a TypeScript project's <c>paths</c>: an address whose one <c>*</c> ends it, mapped to a place whose
/// one <c>*</c> ends it after a <c>/</c>, a directory's; an address with no <c>*</c>, mapped to a place with none, a file's -
/// one whole root for each path TypeScript and Node try it as - and any other mapping none (<see cref="Unread"/>), each
/// root declared once.
/// </summary>
internal static class ProjectRoots
{
    /// <summary>The mappings no root is declared for, as an extractor's help says them.</summary>
    public const string Unread = "a pattern whose * is not its last character, a place whose * does not follow a / and end it (./*: ./dist/*.js, @x/*: src/*/index), and a pattern with a * mapped to a place with none, or one with none mapped to a place with one";

    /// <summary>
    /// Adds the roots a mapping of <paramref name="address"/> to <paramref name="place"/>, relative to the directory
    /// <paramref name="from"/>, declares: a directory's where both end in their <c>*</c>, a file's where neither has one,
    /// none for any other or for an empty address - a TypeScript project's catch-all <c>*</c>.
    /// </summary>
    /// <param name="roots">The roots the file declares.</param>
    /// <param name="address">The address, or the pattern of addresses, mapped.</param>
    /// <param name="place">The path it maps to, with either separator.</param>
    /// <param name="from">The directory <paramref name="place"/> is relative to, relative to the file's own.</param>
    /// <param name="local">Whether it is the file's directory's files' alone (<see cref="ExtractAddressRoot.Local"/>).</param>
    public static void AddMapping(List<ExtractAddressRoot> roots, string address, string place, string from, bool local)
    {
        // A place written with backslashes is the path it names, as both resolvers take it: src\* is src/*.
        place = place.Replace('\\', '/');

        var star = address.IndexOf('*', StringComparison.Ordinal);
        var placed = place.IndexOf('*', StringComparison.Ordinal);

        if (star < 0)
        {
            if (address.Length > 0 && placed < 0)
            {
                AddFile(roots, address, $"{from}/{place}", local);
            }
        }
        else if (star > 0 && star == address.Length - 1 && placed == place.Length - 1 && (placed == 0 || place[placed - 1] == '/'))
        {
            AddDirectory(roots, address[..^1], $"{from}/{place[..^1]}", local);
        }
    }

    /// <summary>Adds the root that makes every address <paramref name="prefix"/> begins a path under <paramref name="directory"/>, once.</summary>
    /// <param name="roots">The roots the file declares.</param>
    /// <param name="prefix">The addresses' prefix.</param>
    /// <param name="directory">The directory, relative to the file's own.</param>
    /// <param name="local">Whether it is the file's directory's files' alone (<see cref="ExtractAddressRoot.Local"/>).</param>
    public static void AddDirectory(List<ExtractAddressRoot> roots, string prefix, string directory, bool local)
        => Add(roots, new ExtractAddressRoot(prefix, CodeGraphIds.Clean(directory)) { Local = local });

    /// <summary>
    /// Adds a whole root of <paramref name="address"/> for each path TypeScript and Node try <paramref name="file"/> as
    /// (<see cref="JsReading.Tried"/>) - as written, with each extension, as a directory's <c>index</c> - each once.
    /// </summary>
    /// <param name="roots">The roots the file declares.</param>
    /// <param name="address">The whole address.</param>
    /// <param name="file">The file it stands for, relative to the file's own directory.</param>
    /// <param name="local">Whether it is the file's directory's files' alone (<see cref="ExtractAddressRoot.Local"/>).</param>
    public static void AddFile(List<ExtractAddressRoot> roots, string address, string file, bool local)
    {
        foreach (var tried in JsReading.Tried(CodeGraphIds.Clean(file), typescript: true, node: true))
        {
            Add(roots, new ExtractAddressRoot(address, CodeGraphIds.Clean(tried)) { Whole = true, Local = local });
        }
    }

    private static void Add(List<ExtractAddressRoot> roots, ExtractAddressRoot root)
    {
        if (!roots.Contains(root))
        {
            roots.Add(root);
        }
    }
}
