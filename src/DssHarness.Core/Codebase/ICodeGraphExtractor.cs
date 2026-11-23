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
/// <param name="Text">
/// The file's text, decoded by the core and read by position (<see cref="CodeText"/>): never one string of the whole file,
/// so what an implementation holds of a file is what it keeps of it, whatever the file's size.
/// </param>
public sealed record CodeFileInput(string Path, CodeText Text)
{
    /// <summary>
    /// The file's lines, as every span of its extract counts them (<see cref="CodeFileText.Lines"/>): each read from the
    /// text when it is asked for (<see cref="CodeText.Lines"/>), so only the lines an implementation keeps are held.
    /// </summary>
    public IReadOnlyList<string> Lines => Text.Lines;
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
/// A pure test of the file's decoded text, given whole and read by position (<see cref="CodeText"/>) only as far as the
/// test reads - a test defined by a format's start (a shebang, an XML prolog) stops there by its own grammar, and one that
/// looks for a mark finds it wherever the file holds it - which says whether it is this implementation's;
/// <see langword="null"/> where the claim needs none. Where two implementations claim one name or one extension, each must
/// give one, and the registry refuses them otherwise.
/// </param>
public sealed record CodeGraphClaim(string Value, Func<CodeText, bool>? ContentTest = null);

/// <summary>
/// What an implementation does not see: a clause the help prints after <c>does not see</c>, a line a person reads before trusting
/// an answer of the graph, and the signs by which a file shows the limit applies to it (<see cref="Signs"/>): a <c>sites</c> answer
/// says every limit of each language whose files it read, and marks a limit with how many of those files show its sign.
/// </summary>
/// <param name="Says">What it does not see: a clause with no final stop.</param>
public sealed record CodeGraphLimit(string Says)
{
    /// <summary>The sign of a file some of whose references the linker left candidates, however few (<see cref="CodebaseGraphQueries.SignsOf"/>).</summary>
    public const string Candidates = "candidates";

    /// <summary>
    /// The sign of a file some of whose references the linker bound to nothing the repository declares, each counted external
    /// (<see cref="CodeGraphLinked.External"/>): where a language binds its names within the file (<see cref="FileExtract.BoundWithin"/>),
    /// a callee the tree does declare that the file reaches by a way the graph does not read - a root of a layout it does not
    /// know, a name bound by nothing the file says - is counted so, and the limit that names that way says why.
    /// </summary>
    public const string External = "external";

    /// <summary>
    /// The signs a file gives that the limit applies to it: <see cref="Candidates"/>, <see cref="External"/>, or the kind of a
    /// dynamic mark its reading made (<see cref="ExtractDynamic.Kind"/>) - a sign names one limit as another may name it, where
    /// both tell of one thing. None where no sign of it is named: the limit is said of every file of its language all the same,
    /// and marked of none.
    /// </summary>
    public IReadOnlyList<string> Signs { get; init; } = [];

    /// <summary>A limit no sign is named for: its clause alone.</summary>
    public static implicit operator CodeGraphLimit(string says) => new(says);
}

/// <summary>What <c>help codebase-graph</c> says of one implementation.</summary>
/// <param name="Reads">One line: what it reads, and through what.</param>
/// <param name="DoesNotSee">What it does not see, each a line a person reads before trusting an answer of the graph, with the signs that tell where it applies.</param>
public sealed record CodeGraphExtractorHelp(string Reads, IReadOnlyList<CodeGraphLimit> DoesNotSee)
{
    /// <summary>
    /// What reading a file holds beyond what the implementation keeps of it, where a library it reads through keeps more -
    /// which library, what it keeps and why, as its documentation says; <see langword="null"/> where reading a file holds
    /// what the implementation keeps and no more, as every reader of the core's text by position does.
    /// </summary>
    public string? Holds { get; init; }
}
