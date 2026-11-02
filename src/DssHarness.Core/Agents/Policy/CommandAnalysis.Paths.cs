namespace DssHarness.Core.Agents.Policy;

/// <summary>
/// One path a word of a command may name to the program it is handed to (<see cref="CommandAnalysis.PathsNamed"/>).
/// </summary>
/// <param name="Path">The path, as the word spells it.</param>
/// <param name="Whole">
/// Whether the word gives it as a path of its own - the word itself, a value after an <c>=</c>, an <c>@</c> or a
/// <c>&lt;</c>, a PowerShell parameter's after its <c>:</c>, a word its braces spell, a file URL's path - which a program
/// may walk where it is a directory; false for a value glued to a short option and a word of a script's text, which name
/// a file where they name one and are as often a separator, a field or a word of a message (<c>cut -d/</c>,
/// <c>git commit -m "a / b"</c>).
/// </param>
public readonly record struct NamedPath(string Path, bool Whole);

public static partial class CommandAnalysis
{
    /// <summary>
    /// Every path <paramref name="word"/> may name to the program it is handed to, each once: the word itself; the value
    /// after its first <c>=</c> (<c>--file=x</c>, <c>of=x</c>, an assignment's) and a PowerShell parameter's after its
    /// <c>:</c> (<c>-Path:x</c>); a value glued to a short option (<c>-i./key</c>, <c>-t.git/hooks</c>); each of these with
    /// the <c>@</c> or <c>&lt;</c> that makes curl, httpie and gh read a file's contents taken off (<c>@x</c>,
    /// <c>f=@x</c>, <c>f=@x;type=text/plain</c>, <c>f=&lt;x</c>); each word of a script's text inside it; each word its
    /// braces spell where <paramref name="braces"/> says the shell expands them; and the path a file URL names. The one
    /// reading of which paths a word names, for the places no command reaches and for the places a command writes alike.
    /// </summary>
    /// <param name="word">The word, as the command's reading spells it.</param>
    /// <param name="braces">Whether the shell expands the word's braces: false for a here-document's or a here-string's text.</param>
    /// <param name="lists">
    /// Whether each part of every path read that a <c>:</c> or a <c>;</c> separates is a path too, as it is to a container
    /// engine's <c>source:target</c> and to a search path's list of directories - a drive's own colon kept with its path
    /// (<c>C:\x</c>): false for a command an agent runs, whose rules read the word whole.
    /// </param>
    /// <exception cref="NotSupportedException">The word's braces spell more words than <see cref="BraceBound"/>.</exception>
    public static IReadOnlyList<NamedPath> PathsNamed(string word, bool braces = true, bool lists = false)
    {
        ArgumentNullException.ThrowIfNull(word);

        var named = new List<NamedPath>();

        void Add(string path, bool whole)
        {
            if (path.Length == 0)
            {
                return;
            }

            named.Add(new NamedPath(path, whole));

            // The file curl's, httpie's and gh's '@' and '<' read, and the part of curl's -F value before its ';' fields.
            if (path[0] is '@' or '<' && path.Length > 1)
            {
                var read = path[1..];
                named.Add(new NamedPath(read, whole));

                if (read.IndexOf(';', StringComparison.Ordinal) is var fields and > 0)
                {
                    named.Add(new NamedPath(read[..fields], whole));
                }
            }
        }

        Add(word, whole: true);

        if (word.IndexOf('=', StringComparison.Ordinal) is var equals and >= 0 && equals + 1 < word.Length)
        {
            Add(word[(equals + 1)..], whole: true);
        }

        if (word.Length > 2 && word[0] == '-')
        {
            // A PowerShell parameter's value glued after its ':' (-Path:~/x), as an option's after its '='.
            if (word.IndexOf(':', StringComparison.Ordinal) is var colon and > 1 && colon + 1 < word.Length)
            {
                Add(word[(colon + 1)..], whole: true);
            }

            // A short option's value glued to it, as getopt reads one (-i./key, -t.git/hooks).
            if (word[1] != '-')
            {
                Add(word[2..], whole: false);
            }
        }

        if (word.Any(char.IsWhiteSpace))
        {
            foreach (var piece in word.Split([' ', '\t', '\n', '\r', ';', '|', '&', '(', ')', '`', '\'', '"'], StringSplitOptions.RemoveEmptyEntries))
            {
                Add(piece, whole: false);
            }
        }

        if (braces && word.Contains('{', StringComparison.Ordinal))
        {
            foreach (var spelled in BraceExpansions(word))
            {
                Add(spelled, whole: true);
            }
        }

        foreach (var url in named.Where(path => path.Path.StartsWith("file:", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            if (Uri.TryCreate(url.Path, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                Add(uri.LocalPath, url.Whole);
            }
        }

        if (lists)
        {
            foreach (var listed in named.Where(path => path.Path.IndexOfAny([':', ';']) >= 0).ToList())
            {
                foreach (var part in ListParts(listed.Path))
                {
                    Add(part, listed.Whole);
                }
            }
        }

        // Each path once, given whole where any reading gives it whole.
        return [.. named
            .GroupBy(path => path.Path, StringComparer.Ordinal)
            .Select(group => new NamedPath(group.Key, group.Any(path => path.Whole)))];
    }

    /// <summary>
    /// The parts of <paramref name="path"/> a <c>:</c> or a <c>;</c> separates, each not empty: a drive's letter and its
    /// colon kept with the part after it, where that part begins with a separator or there is none (<c>C:\x</c>, <c>C:</c>).
    /// </summary>
    private static IEnumerable<string> ListParts(string path)
    {
        var start = 0;

        for (var at = 0; at <= path.Length; at++)
        {
            if (at < path.Length && path[at] is not (':' or ';'))
            {
                continue;
            }

            // A drive's colon: one letter before it, at a part's start, and a separator or nothing after it.
            if (at < path.Length && path[at] == ':' && at == start + 1 && char.IsAsciiLetter(path[start])
                && (at + 1 == path.Length || path[at + 1] is '\\' or '/' or ';'))
            {
                continue;
            }

            if (at > start)
            {
                yield return path[start..at];
            }

            start = at + 1;
        }
    }
}
