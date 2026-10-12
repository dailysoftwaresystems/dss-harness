namespace RepoHarness.Core.Mutations;

/// <summary>What an arm declares its mutation does: make cases of a test binary fail, or make the build fail.</summary>
/// <remarks>
/// Declared, never inferred from what happened: inferred, an arm whose mutation should redden a test and instead broke
/// the build would read as a build-red arm behaving as declared, and a broken harness as either.
/// </remarks>
public enum RedKind
{
    /// <summary>
    /// The mutation builds, and its test binary, run whole, fails exactly the cases its C rows name, runs its G rows'
    /// cases green, runs as many cases as it declares, and says its diagnostic. Spelled <c>TEST-RED</c>.
    /// </summary>
    TestRed,

    /// <summary>
    /// The mutation stops the build at an object that depends on its site, and its paired positive control - another
    /// substitution at the same site - builds. Nothing runs. Spelled <c>BUILD-RED</c>.
    /// </summary>
    BuildRed,
}

/// <summary>One text of a site replaced: the file holding it, and the file holding what replaces it.</summary>
/// <param name="Before">The file holding the text replaced, relative to the repository root.</param>
/// <param name="After">The file holding what replaces it, relative to the repository root.</param>
/// <param name="Line">The registry line declaring it: the arm's own A row, or an M row.</param>
public sealed record SiteText(string Before, string After, int Line);

/// <summary>
/// One file an arm mutates: the text in it replaced, what replaces it, and each further text of the same file replaced
/// with it.
/// </summary>
/// <param name="Site">The file mutated, relative to the repository root.</param>
/// <param name="Before">The file holding the text replaced, relative to the repository root.</param>
/// <param name="After">The file holding what replaces it, relative to the repository root.</param>
/// <param name="Line">The registry line that first names the file: the arm's own A row, or an M row.</param>
public sealed record MutationSite(string Site, string Before, string After, int Line)
{
    /// <summary>
    /// The file's other replacements, each an M row naming the file again, in the order they are declared: a mutant that
    /// is several places of one file, apart from each other, is made as one edit of it - each text found in the file as
    /// the tree holds it, never as an earlier replacement left it - and the file put back whole.
    /// </summary>
    public IReadOnlyList<SiteText> Further { get; init; } = [];

    /// <summary>Every replacement made in the file, the row that first names it first.</summary>
    public IReadOnlyList<SiteText> Texts => [new SiteText(Before, After, Line), .. Further];

    /// <summary>Whether <paramref name="other"/> is the same file with the same replacements, in the same order.</summary>
    public bool Equals(MutationSite? other)
        => other is not null && Site == other.Site && Before == other.Before && After == other.After && Line == other.Line && Further.SequenceEqual(other.Further);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Site, Before, After, Line, Further.Count);
}

/// <summary>
/// A BUILD-RED arm's paired positive control: a substitution at the arm's own site, of the pristine site, that must
/// build. Without it, a mutation that does not compile proves nothing: the file might not compile for any reason at all.
/// </summary>
/// <param name="Before">The file holding the text the control replaces, relative to the repository root.</param>
/// <param name="After">The file holding what replaces it.</param>
/// <param name="Line">The B row declaring it.</param>
public sealed record PairedControl(string Before, string After, int Line);

/// <summary>The legs an arm runs on, as its S row names them, in the <c>--legs</c> syntax.</summary>
/// <param name="Legs">Each name as the row gives it: a leg or a leg set, separated by commas or spaces.</param>
/// <param name="Line">The S row.</param>
public sealed record ArmScope(IReadOnlyList<string> Legs, int Line);

/// <summary>One arm: a mutation, and what it must do.</summary>
public sealed record MutationArm
{
    /// <summary>Its id, unique ignoring case: the name its records are kept under on every machine.</summary>
    public required string Id { get; init; }

    /// <summary>Its A row.</summary>
    public required int Line { get; init; }

    /// <summary>The site the A row mutates.</summary>
    public required MutationSite Own { get; init; }

    /// <summary>What it declares its mutation does.</summary>
    public required RedKind Kind { get; init; }

    /// <summary>The build target the mutation is built as.</summary>
    public required string Target { get; init; }

    /// <summary>
    /// The test target whose binary runs, located from the build's own manifest; <see cref="MutationRegistryParser.NoRunner"/>
    /// for a BUILD-RED arm, which runs nothing.
    /// </summary>
    public required string Runner { get; init; }

    /// <summary>How many cases its binary runs, skipped ones included; 0 for a BUILD-RED arm.</summary>
    public required int Cases { get; init; }

    /// <summary>
    /// The file holding the text its run must say, relative to the repository root; <see cref="MutationRegistryParser.PairedControlToken"/>
    /// for a BUILD-RED arm.
    /// </summary>
    public required string Diagnostic { get; init; }

    /// <summary>Why it exists, as its A row says.</summary>
    public required string Why { get; init; }

    /// <summary>The cases its C rows say must fail, as <c>classname.name</c>, in the order they are declared.</summary>
    public IReadOnlyList<string> Reds { get; init; } = [];

    /// <summary>The neighbours its G rows say must run and stay green.</summary>
    public IReadOnlyList<string> Greens { get; init; } = [];

    /// <summary>Its B row, which a BUILD-RED arm has exactly one of.</summary>
    public PairedControl? Control { get; init; }

    /// <summary>
    /// The other files its M rows name, mutated, restored and verified together with its own, for a mechanism that lives
    /// in more than one file and is switched off only when every one of them is. An M row naming a file the arm already
    /// mutates - its own, or one of these - is no other site: it is a further text of that one
    /// (<see cref="MutationSite.Further"/>).
    /// </summary>
    public IReadOnlyList<MutationSite> Coupled { get; init; } = [];

    /// <summary>Its S row, or <see langword="null"/> where it runs on every selected leg.</summary>
    public ArmScope? Scope { get; init; }

    /// <summary>Every file it mutates, its own first, each once however many of its texts are replaced.</summary>
    public IReadOnlyList<MutationSite> Sites => [Own, .. Coupled];
}

/// <summary>The arms a registry declares, in the order it declares them.</summary>
/// <param name="Arms">The arms.</param>
public sealed record MutationRegistry(IReadOnlyList<MutationArm> Arms)
{
    /// <summary>The arm named <paramref name="id"/>, compared ignoring case as ids are kept unique, or <see langword="null"/>.</summary>
    /// <param name="id">The id, as typed.</param>
    public MutationArm? Find(string id)
        => Arms.FirstOrDefault(arm => string.Equals(arm.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A registry as read, with every problem found in it.</summary>
/// <param name="Registry">The arms that could be read.</param>
/// <param name="Problems">Every problem, each naming its line where it has one, in the order a reader meets them.</param>
public sealed record MutationRegistryReading(MutationRegistry Registry, IReadOnlyList<string> Problems)
{
    /// <summary>Whether nothing was found wrong, so the registry can be driven.</summary>
    public bool Valid => Problems.Count == 0;
}

/// <summary>The files directly in the registry's text directory, as the cover check reads them.</summary>
/// <param name="Directory">The directory, relative to the repository root, with forward separators.</param>
/// <param name="Files">
/// The names of the files directly in it that a copy of the tree holds, or <see langword="null"/> where it is not a
/// directory.
/// </param>
public sealed record TextDirectoryListing(string Directory, IReadOnlyList<string>? Files)
{
    /// <summary>Why the directory, which is there, could not be listed; <see langword="null"/> where it was, or is not there.</summary>
    public string? Unlisted { get; init; }

    /// <summary>
    /// How many files directly in it a sync withholds from every copy of the tree, which <see cref="Files"/> leaves
    /// out: no copy holds one, so it is no text anybody could drive.
    /// </summary>
    public int Withheld { get; init; }
}
