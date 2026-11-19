namespace DssHarness.Core.Codebase.Languages.Scanning;

/// <summary>
/// Where a scanned language's reader stands as it walks a text's declarations, scope by scope: the scope what it reads
/// is put in, the name before each name it declares, the node that holds them, and the type they are members of. Each
/// block's frame is held by the walk of that block on the reader's own stack (<see cref="ScanStack"/>), so a block nested
/// any depth is read.
/// </summary>
/// <param name="Prefix">The qualified name each declaration's name is joined to; <see langword="null"/> at the top.</param>
/// <param name="Scope">The scope declarations are put in, by its qualified name; <see langword="null"/> where it is the repository's own.</param>
/// <param name="Parent">The key of the node that holds them (<see cref="ScanExtract.Add"/>); <see langword="null"/> where it is the file.</param>
/// <param name="Owner">The simple name of the type they are members of; <see langword="null"/> outside one.</param>
internal sealed record ScanFrame(string? Prefix, string? Scope, string? Parent, string? Owner)
{
    /// <summary>The frame of a file's top: no scope, no prefix, no type.</summary>
    public static ScanFrame Top { get; } = new(null, null, null, null);

    /// <summary>How many blocks the frame's declarations are nested in: none at the file's top, which a reader's rules for the top tell by it.</summary>
    public int Depth { get; init; }

    /// <summary><paramref name="name"/> joined to <see cref="Prefix"/> by the model's separator, a dot.</summary>
    public string Qualify(string name) => ExtractText.Qualify(Prefix, name);

    /// <summary>The frame of a block this one holds that declares in a scope of its own.</summary>
    public ScanFrame Inside(string? prefix, string? scope, string? parent, string? owner) => new(prefix, scope, parent, owner) { Depth = Depth + 1 };

    /// <summary>This frame, for a block it holds that declares in the same scope: an anonymous type's, a linkage block's.</summary>
    public ScanFrame Deeper() => this with { Depth = Depth + 1 };
}
