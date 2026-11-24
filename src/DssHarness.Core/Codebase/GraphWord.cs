namespace DssHarness.Core.Codebase;

/// <summary>
/// A word a search of the graph looks for in a line or a value, by its characters alone: no pattern is compiled of it, so no
/// word is refused for its length, and every place it stands at is found in one pass over the text however its characters
/// repeat (Knuth, Morris and Pratt) - a text read in time linear in it and the word, never timed. Each place is held where
/// the characters beside it are the boundaries the word asks for: the text's start or end, or a character the edge takes
/// (<see cref="Whole"/>, the one whole-word rule every search of the graph reads - the text fallback's, and a <c>sites</c>
/// answer's read of the literals and of the files' text alike (<see cref="GraphSitesSearch"/>)).
/// </summary>
public sealed class GraphWord
{
    private readonly Func<char, bool>? _before;
    private readonly Func<char, bool>? _after;

    /// <summary>Of each length the word begins with, how long the longest of its proper prefixes is that it also ends with.</summary>
    private readonly int[] _borders;

    /// <summary>
    /// The word <paramref name="word"/>, held where the character before it is the text's start or one <paramref name="before"/>
    /// takes, and the one after it the text's end or one <paramref name="after"/> takes - either side free where its edge takes
    /// none.
    /// </summary>
    /// <param name="word">The word, as it is looked for: its characters, in order.</param>
    /// <param name="before">What a character before the word must be, where one is there; null where any may be.</param>
    /// <param name="after">What a character after the word must be, where one is there; null where any may be.</param>
    public GraphWord(string word, Func<char, bool>? before, Func<char, bool>? after)
    {
        ArgumentException.ThrowIfNullOrEmpty(word);

        Word = word;
        _before = before;
        _after = after;
        _borders = new int[word.Length];

        for (int index = 1, matched = 0; index < word.Length; index++)
        {
            while (matched > 0 && word[index] != word[matched])
            {
                matched = _borders[matched - 1];
            }

            if (word[index] == word[matched])
            {
                matched++;
            }

            _borders[index] = matched;
        }
    }

    /// <summary>The word, as it is looked for.</summary>
    public string Word { get; }

    /// <summary>
    /// <paramref name="word"/> as a whole word: a boundary beside each of its two edges that is a word character - a letter, a
    /// number or an underscore (<see cref="CodebaseGraphQueries.WordCharacter"/>) - the text's start or end, or a character
    /// that is none of them; an edge that is no word character is its own boundary, so <c>.btn-primary</c> is held by
    /// <c>button.btn-primary</c>.
    /// </summary>
    /// <param name="word">The word, as it is asked.</param>
    public static GraphWord Whole(string word)
    {
        ArgumentException.ThrowIfNullOrEmpty(word);

        static bool NoWordCharacter(char character) => !CodebaseGraphQueries.WordCharacter(character);

        return new GraphWord(
            word,
            CodebaseGraphQueries.WordCharacter(word[0]) ? NoWordCharacter : null,
            CodebaseGraphQueries.WordCharacter(word[^1]) ? NoWordCharacter : null);
    }

    /// <summary>
    /// Whether <paramref name="text"/> holds the word at a place its boundaries hold: each place it stands at found in one pass,
    /// a run of the text that cannot begin it passed over at once, and a place whose boundaries fail never read again.
    /// </summary>
    /// <param name="text">A line, or a value.</param>
    public bool In(ReadOnlySpan<char> text)
    {
        var word = Word.AsSpan();
        var matched = 0;

        for (var index = 0; index < text.Length; index++)
        {
            // Nothing begun: the next character the word begins with is where it may stand next.
            if (matched == 0)
            {
                var next = text[index..].IndexOf(word[0]);

                if (next < 0)
                {
                    return false;
                }

                index += next;
            }

            while (matched > 0 && text[index] != word[matched])
            {
                matched = _borders[matched - 1];
            }

            if (text[index] == word[matched] && ++matched == word.Length)
            {
                var start = index + 1 - word.Length;

                if ((_before is null || start == 0 || _before(text[start - 1])) && (_after is null || index + 1 == text.Length || _after(text[index + 1])))
                {
                    return true;
                }

                matched = _borders[matched - 1];
            }
        }

        return false;
    }
}
