namespace DssHarness.Tests;

/// <summary>
/// Text read as a person reads it, its words one space apart wherever a line was wrapped: what the CLI says, and what
/// the repository's own documents say, so a test finds a sentence whole and holds it to the words the code owns.
/// </summary>
internal static class Prose
{
    /// <summary>
    /// <paramref name="text"/> with every run of whitespace, line breaks among it, read as one space, so a sentence
    /// wrapped across lines is found whole.
    /// </summary>
    public static string Words(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The words of one of the repository's own documents, as <see cref="Words"/> reads them.</summary>
    /// <param name="path">The document's path below the repository's root, a segment each: <c>"docs", "architecture.md"</c>.</param>
    public static string Document(params string[] path) => Words(File.ReadAllText(Path.Combine([RepositoryRoot(), .. path])));

    /// <summary>The repository this test was built from: the first directory above it holding the solution.</summary>
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DssHarness.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"No directory above '{AppContext.BaseDirectory}' holds DssHarness.slnx.");
    }
}
