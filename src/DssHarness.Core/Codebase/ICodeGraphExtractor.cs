namespace DssHarness.Core.Codebase;

/// <summary>
/// The one contract everything that reads a file's contents for the codebase graph implements: a language, an outline of
/// a kind of file, or the listing of a file no other reads. The rest of the graph - the store, the sync, the linker, the
/// step, help and the validator - knows no language by name, so a new language is one class and its tests.
/// </summary>
/// <remarks>
/// <para>
/// An implementation is registered by being written: <see cref="CodeGraphExtractors.Implementations"/> finds every
/// concrete type of the core that implements this, in one scan, and the registry refuses two that would claim one file
/// without a test to tell them apart (<see cref="CodeGraphExtractors"/>). It is made with no argument, since it reads
/// nothing but what it is given.
/// </para>
/// <para>
/// Its test class derives from the contract's test kit, which holds it to purity and determinism, a sound model,
/// tolerance, its claims, its stored extracts under its version, and the edges its fixtures name through the one linker.
/// </para>
/// </remarks>
public interface ICodeGraphExtractor
{
    /// <summary>
    /// The implementation's name: a node id's prefix, a value <c>codebaseGraph.languages</c> may hold, and the name of its
    /// extracts on disk. Lower-case letters and digits, beginning with a letter; unique in the registry.
    /// </summary>
    string Language { get; }

    /// <summary>
    /// A positive integer, raised whenever what the implementation answers for some file changes: part of every extract's
    /// name, so raising it extracts again the files it reads - and reads again every file a content test decides, and every
    /// one its claimants left ambiguous, since a new version may decide them otherwise - and no other.
    /// </summary>
    int Version { get; }

    /// <summary>What files it reads.</summary>
    CodeGraphClaims Claims { get; }

    /// <summary>
    /// Whether it outlines its files - their parts, for an agent to be served one - rather than reading a language's
    /// declarations: a file it reads is <c>outlined</c>, not <c>parsed</c>.
    /// </summary>
    bool Outlines { get; }

    /// <summary>What it reads, and what it does not see, as <c>help codebase-graph</c> says them for it.</summary>
    CodeGraphExtractorHelp Help { get; }

    /// <summary>
    /// The extract of one file. Pure: handed no file system, no process and no clock, it reads nothing but
    /// <paramref name="input"/> and answers alike for the same input every time; a file it cannot make sense of it
    /// answers as far as it can, never with an exception.
    /// </summary>
    /// <param name="input">The file's path and its decoded text.</param>
    FileExtract Extract(CodeFileInput input);
}

/// <summary>One file given to an implementation to extract.</summary>
/// <param name="Path">The file's path, relative to the tree's root with forward separators.</param>
/// <param name="Text">The file's text, decoded by the core.</param>
public sealed record CodeFileInput(string Path, string Text)
{
    /// <summary>The file's lines, as every span of its extract counts them (<see cref="CodeFileText.Lines"/>).</summary>
    public IReadOnlyList<string> Lines => CodeFileText.Lines(Text);
}

/// <summary>What files an implementation reads.</summary>
/// <param name="Names">Exact file names it claims, each with an optional content test.</param>
/// <param name="Extensions">Extensions it claims, each with its dot, with an optional content test.</param>
/// <param name="Fallback">Whether it reads every file no other implementation claims: one implementation of the registry, the listing.</param>
public sealed record CodeGraphClaims(IReadOnlyList<CodeGraphClaim> Names, IReadOnlyList<CodeGraphClaim> Extensions, bool Fallback = false)
{
    /// <summary>The claims of the fallback: no name, no extension.</summary>
    public static CodeGraphClaims OfFallback { get; } = new([], [], Fallback: true);

    /// <summary>Claims of the extensions <paramref name="extensions"/>, none with a content test.</summary>
    /// <param name="extensions">The extensions, each with its dot.</param>
    public static CodeGraphClaims OfExtensions(params string[] extensions)
        => new([], [.. extensions.Select(extension => new CodeGraphClaim(extension))]);
}

/// <summary>One file name or extension an implementation claims.</summary>
/// <param name="Value">The exact file name, or the extension with its dot; compared without regard to case.</param>
/// <param name="ContentTest">
/// A pure test of the file's first <see cref="CodeFileText.ProbeCharacters"/> characters, decoded, which says whether it is this
/// implementation's; <see langword="null"/> where the claim needs none. Where two implementations claim one name or one
/// extension, each must give one, and the registry refuses them otherwise.
/// </param>
public sealed record CodeGraphClaim(string Value, Func<string, bool>? ContentTest = null);

/// <summary>What <c>help codebase-graph</c> says of one implementation.</summary>
/// <param name="Reads">One line: what it reads, and through what.</param>
/// <param name="DoesNotSee">What it does not see, each a line a person reads before trusting an answer of the graph.</param>
public sealed record CodeGraphExtractorHelp(string Reads, IReadOnlyList<string> DoesNotSee);
