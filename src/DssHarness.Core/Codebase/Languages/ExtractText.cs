using System.Text;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// How every implementation gives a node its text: a signature on one line, whole however long the header runs, and a
/// summary - the first sentence of what the code says of itself, whole, which its meaning alone bounds. One place, so
/// every language says them alike; what serves them gives one wider than a page in pieces.
/// </summary>
internal static class ExtractText
{
    /// <summary><paramref name="text"/> on one line: each run of white space, line breaks among it, one space.</summary>
    /// <param name="text">Text written over one line or more.</param>
    public static string Flat(string text)
    {
        var flat = new StringBuilder(text.Length);
        var space = false;

        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                space = flat.Length > 0;
                continue;
            }

            if (space)
            {
                flat.Append(' ');
                space = false;
            }

            flat.Append(character);
        }

        return flat.ToString();
    }

    /// <summary><paramref name="header"/>, a declaration's header on one line, as a node's signature: whole.</summary>
    /// <param name="header">The header.</param>
    public static string Signature(string header) => Whole(header);

    /// <summary>
    /// The first sentence of <paramref name="text"/>, what the code says of itself on one line - up to the first <c>.</c>,
    /// <c>!</c> or <c>?</c> that a space or the end follows, the whole text where none does - as a node's summary: whole,
    /// however many lines it ran over.
    /// </summary>
    /// <param name="text">What the code says, on one line.</param>
    public static string Summary(string text)
    {
        for (var at = 0; at < text.Length; at++)
        {
            if (text[at] is '.' or '!' or '?' && (at + 1 == text.Length || char.IsWhiteSpace(text[at + 1])))
            {
                return Whole(text[..(at + 1)]);
            }
        }

        return Whole(text);
    }

    /// <summary>
    /// <paramref name="text"/> as whole UTF-16, as a record holds it: each surrogate apart from its pair - a file cut inside
    /// a character - the replacement character.
    /// </summary>
    /// <param name="text">Text read from a file.</param>
    public static string Whole(string text)
    {
        var apart = FileExtract.Apart(text, 0);

        if (apart < 0)
        {
            return text;
        }

        var whole = text.ToCharArray();

        for (; apart >= 0; apart = FileExtract.Apart(text, apart + 1))
        {
            whole[apart] = '�';
        }

        return new string(whole);
    }

    /// <summary><paramref name="name"/> joined to <paramref name="scope"/>, the qualified name it is declared in, by the model's separator, a dot; itself at the top.</summary>
    /// <param name="scope">The qualified name of the scope it is declared in; <see langword="null"/> at the top.</param>
    /// <param name="name">Its name.</param>
    public static string Qualify(string? scope, string name) => scope is null ? name : $"{scope}.{name}";
}
