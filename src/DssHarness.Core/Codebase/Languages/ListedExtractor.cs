namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// The fallback: every file no other implementation claims is a node of the graph, with its size and its lines, so an
/// agent finds it by its name and knows how large it is before it reads it - and nothing in it is read.
/// </summary>
public sealed class ListedExtractor : ICodeGraphExtractor
{
    /// <inheritdoc/>
    public string Language => "listed";

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public CodeGraphClaims Claims => CodeGraphClaims.OfFallback;

    /// <inheritdoc/>
    public bool Outlines => false;

    /// <inheritdoc/>
    public CodeGraphExtractorHelp Help { get; } = new(
        "every file no other implementation claims: a node with its size and its lines, nothing in it read",
        ["anything inside such a file: an agent reads it whole, or searches it"]);

    /// <inheritdoc/>
    public FileExtract Extract(CodeFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return FileExtract.Empty;
    }
}
