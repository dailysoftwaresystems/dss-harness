namespace DssHarness.Core.Codebase;

/// <summary>
/// A qualified name as the graph holds it in memory: the name before its own part, and that part - a segment, a key's
/// place, a member's name with the parameters its id spells - never the whole name written out, which is made again
/// only where a text is asked for (<see cref="Whole"/>). A name nested any depth therefore costs what its own part
/// adds, so a file nested thousands deep holds names that grow with its depth and never with the square of it - in
/// memory the shape its record keeps in bytes (<see cref="StoredName"/>), which reads back into it. The one owner of
/// joining a name to the one before it, of a text a name begins with, and of the part past a name's last dot.
/// </summary>
/// <remarks>
/// <para>
/// Two names are equal by the text they write, however each was made: a whole name held as one part equals the same
/// name held as the one before it and its own part. Its hash is kept with it, made of the one before's, so a table of
/// names never walks a name's parts to place it.
/// </para>
/// <para>
/// A text may stand before the whole name - an implementation's name before an id's - shared by every name joined to
/// it, never copied into each.
/// </para>
/// </remarks>
public sealed class QualifiedName
{
    /// <summary>The multiplier of a name's hash, over its text's characters in order.</summary>
    private const ulong Basis = 0x100000001b3;

    /// <summary>The name before this one's own part; <see langword="null"/> where this name is one part whole.</summary>
    private readonly QualifiedName? _before;

    /// <summary>
    /// This name's own part, the separator that joins it to the one before it written before it - a dot, a bracket, a
    /// slash - or, where no name is before it, the whole name's text.
    /// </summary>
    private readonly string _own;

    /// <summary>
    /// Text the whole name begins with, before the name's first part - an implementation's name before an id's - the
    /// same text every name joined to it holds; empty where the name begins with its own first part.
    /// </summary>
    private readonly string _prefix;

    /// <summary>How long the whole name's text is.</summary>
    private readonly int _length;

    /// <summary>The hash of the whole name's text, made of the one before's own.</summary>
    private readonly ulong _hash;

    private QualifiedName(QualifiedName? before, string own, string prefix)
    {
        _before = before;
        _own = own;
        _prefix = prefix;
        _length = prefix.Length + (before?._length ?? 0) + own.Length;

        // The hash of the text the parts write in order - the text before them, then the name before, then this own -
        // composed of the one before's own hash, so equal texts hash equally however each was made.
        _hash = before is null
            ? Hash(prefix) * Pow(own.Length) + Hash(own)
            : Hash(prefix) * Pow(before._length + own.Length) + before._hash * Pow(own.Length) + Hash(own);
    }

    /// <summary><paramref name="text"/> as one whole name of a single part.</summary>
    /// <param name="text">The name's whole text.</param>
    public static QualifiedName Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new(before: null, own: text, prefix: "");
    }

    /// <summary>
    /// This name with <paramref name="piece"/> joined to it by <paramref name="separator"/> - the model's dot, an
    /// opening bracket where brackets join a key's place, a slash where a path does - which holds this name as the one
    /// before that part and never writes it out again.
    /// </summary>
    /// <param name="piece">The name's own part, without the separator.</param>
    /// <param name="separator">What joins the part to the name before it; the model's dot.</param>
    public QualifiedName Under(string piece, string separator = ".")
    {
        ArgumentNullException.ThrowIfNull(piece);
        ArgumentNullException.ThrowIfNull(separator);
        return new(this, separator + piece, "");
    }

    /// <summary>
    /// This name with <paramref name="text"/> standing before its whole name - an implementation's name before an
    /// id's - the text shared, never copied into the name or any part joined to it.
    /// </summary>
    /// <param name="text">The text the name begins with; nothing, where it keeps what it begins with.</param>
    public QualifiedName Prepend(string text) => text.Length == 0 ? this : new(_before, _own, text + _prefix);

    /// <summary>How long the whole name's text is.</summary>
    public int Length => _length;

    /// <summary>
    /// How many characters of its name this name itself holds: its own part, and the text before the whole name only
    /// where no name before it holds it too - so what a table of names holds grows with the names' own parts and never
    /// with the square of how deep they nest.
    /// </summary>
    internal int Held => _before is null ? _prefix.Length + _own.Length : _own.Length;

    /// <summary>
    /// Whether the name's whole text is empty or holds nothing but whitespace - asked of a name wherever a whole text was
    /// asked before, read from its own parts without the whole name made: a name with a part before its own holds that
    /// part's separator, which is not whitespace.
    /// </summary>
    internal bool WhitespaceOnly()
        => _length == 0 || _before is null && string.IsNullOrWhiteSpace(_prefix + _own);

    /// <summary>The name's whole text, made again here and never kept by the name: its parts read from its first.</summary>
    public string Whole()
    {
        var names = new List<QualifiedName> { this };

        for (var at = _before; at is not null; at = at._before)
        {
            names.Add(at);
        }

        // The text each name begins with comes before every part, the nearest name's first; then each name's own part,
        // from the head of the chain to its nearest.
        var parts = new List<string>(names.Count * 2);

        foreach (var name in names)
        {
            if (name._prefix.Length > 0)
            {
                parts.Add(name._prefix);
            }
        }

        for (var at = names.Count - 1; at >= 0; at--)
        {
            if (names[at]._own.Length > 0)
            {
                parts.Add(names[at]._own);
            }
        }

        return string.Concat(parts);
    }

    /// <summary>The name's whole text; the whole name made again where a text is asked for.</summary>
    public override string ToString() => Whole();

    /// <summary>Whether the name's whole text is <paramref name="text"/>, read against it without the whole name made.</summary>
    /// <param name="text">A text, as a whole name.</param>
    public bool Is(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return _length == text.Length && text.AsSpan().SequenceEqual(Whole());
    }

    /// <summary>Whether the name's whole text begins with <paramref name="text"/>, read against its parts from its first.</summary>
    /// <param name="text">A text the name may begin with.</param>
    public bool StartsWith(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return _length >= text.Length && Whole().StartsWith(text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The name's part past its last dot - a type's own name, as an extension names the type it extends - read from its
    /// own parts, the whole name never made; the whole name where no dot parts it.
    /// </summary>
    public string PastLastDot()
    {
        var parts = new List<string>();

        for (var at = this; at is not null; at = at._before)
        {
            var dot = at._own.LastIndexOf('.');

            if (dot >= 0)
            {
                parts.Add(at._own[(dot + 1)..]);
                parts.Reverse();
                return string.Concat(parts);
            }

            parts.Add(at._own);
        }

        return Whole();
    }

    /// <summary>
    /// The name before its last dot - a declaration's scope, as its own qualified name holds it - held as the parts up to
    /// that dot, each part the name already holds, so a scope nested any depth is the name before it and its own part;
    /// <see langword="null"/> where no dot parts the name, or none of it stands before the dot.
    /// </summary>
    public QualifiedName? BeforeLastDot()
    {
        for (var at = this; at is not null; at = at._before)
        {
            if (at._own.LastIndexOf('.') is not (>= 0 and var dot))
            {
                continue;
            }

            // The cut part's own before its dot, joined to the name before that part by the separator its own already
            // holds; the whole part where the dot is the first thing it holds. The parts past the cut hold no dot, so
            // none of them stands before it.
            return dot == 0 ? at._before
                : at._before is null ? QualifiedName.Of(at._prefix + at._own[..dot])
                : at._before.Under(at._own[..dot], "");
        }

        return null;
    }

    /// <summary>Whether <paramref name="obj"/> is a name whose whole text this one's is.</summary>
    /// <param name="obj">Another name.</param>
    public override bool Equals(object? obj) => obj is QualifiedName other && Equals(other);

    /// <summary>Whether <paramref name="other"/>'s whole text is this one's, however each was made.</summary>
    /// <param name="other">Another name.</param>
    public bool Equals(QualifiedName? other)
        => ReferenceEquals(this, other)
            || other is not null && other._length == _length && other._hash == _hash && other.Whole().Equals(Whole(), StringComparison.Ordinal);

    /// <summary>The hash of the name's whole text, kept with it as it is made.</summary>
    public override int GetHashCode() => (int)(_hash ^ (_hash >> 32));

    /// <summary>Whether two names write the same text, or are both no name.</summary>
    /// <param name="one">A name, or none.</param>
    /// <param name="other">Another, or none.</param>
    public static bool operator ==(QualifiedName? one, QualifiedName? other) => one is null ? other is null : one.Equals(other);

    /// <summary>Whether two names write different texts.</summary>
    /// <param name="one">A name, or none.</param>
    /// <param name="other">Another, or none.</param>
    public static bool operator !=(QualifiedName? one, QualifiedName? other) => !(one == other);

    /// <summary>A whole text as one name of a single part.</summary>
    /// <param name="text">The name's whole text.</param>
    public static implicit operator QualifiedName(string text) => Of(text);

    /// <summary><paramref name="text"/> as its characters raise the hash, empty at none.</summary>
    private static ulong Hash(string text)
    {
        var hash = 0ul;

        foreach (var character in text)
        {
            hash = hash * Basis + character;
        }

        return hash;
    }

    /// <summary>The basis raised to <paramref name="power"/>.</summary>
    private static ulong Pow(int power)
    {
        var result = 1ul;
        var basis = Basis;

        for (var left = power; left > 0; left >>= 1)
        {
            if ((left & 1) == 1)
            {
                result *= basis;
            }

            basis *= basis;
        }

        return result;
    }
}
