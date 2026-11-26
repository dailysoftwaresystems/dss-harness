using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace DssHarness.Core.Text;

/// <summary>
/// The one maker of regular expressions. Every one is matched by .NET's linear-time engine
/// (<see cref="RegexOptions.NonBacktracking"/>), culture-invariant, with no match timeout: a match costs time linear in
/// the text it reads however the pattern nests, so no timeout is needed to bound it, none is set, and no answer is ever
/// lost to one. A pattern that engine cannot match is refused, naming what in it the engine lacks.
/// </summary>
/// <remarks>
/// <para>
/// How each refusal is told. RightToLeft and ECMAScript are asked of the options before anything is compiled: .NET answers
/// either, beside the linear engine, with a bare <see cref="ArgumentOutOfRangeException"/> naming only the parameter, the
/// same it gives an options value it does not define, so its answer cannot tell which was asked.
/// </para>
/// <para>
/// A construct the engine lacks - a backreference, a lookahead or lookbehind, an atomic group, a conditional, a \G anchor,
/// a balancing group - is reported by .NET as a <see cref="NotSupportedException"/> whose message names the construct, the
/// same message for every pattern that holds it. The type learns once, of the runtime it runs on, the message an example of
/// each construct is refused with, and names a pattern's construct by the example whose message is the same. So no
/// wording is relied on: a runtime that words them otherwise, or in another language, is read the same, and a construct a
/// later runtime matches linearly is simply compiled, its example no longer refused. Where a pattern holds several, the
/// one the runtime reports is named.
/// </para>
/// <para>
/// A pattern that is no regular expression at all is refused in .NET's own words, those of its
/// <see cref="RegexParseException"/>, which name the pattern and the offset. Any other refusal of the linear engine - an
/// automaton larger than the engine builds - is told in .NET's words after the pattern. An options value .NET does not
/// define is the caller's mistake, not the pattern's, and is thrown as .NET throws it.
/// </para>
/// <para>
/// A refusal is a sentence that names the pattern in single quotes and what the engine lacks, so the sentence of whoever
/// reads the pattern - a key of a file, a tool's input - can carry it; that caller, who knows where the pattern was
/// written, adds the remedy.
/// </para>
/// <para>
/// The end of the text, <c>\z</c>, is kept by the engine for the match but not for its groups: where a match that a
/// <c>\z</c> of the pattern could end reaches the end of a text whose last character is a line feed, the engine reports
/// every group of it unmatched. A pattern that has groups is therefore compiled with each <c>\z</c> it holds as an
/// anchor written <c>(?:\Z\z)</c>: the same place - the text's end, which is an end <c>\Z</c> allows too - which the
/// engine keeps the groups at. The pattern is read as .NET reads it to find them: a backslash another escapes, a class,
/// a <c>(?#...)</c> comment and, where whitespace is ignored, a line's <c>#</c> comment hold none. A refusal is of the
/// pattern as written, which is compiled first.
/// </para>
/// </remarks>
public static class LinearRegex
{
    /// <summary>The options every regular expression is made with, beside those asked.</summary>
    private const RegexOptions Linear = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;

    /// <summary>What the linear engine says of each construct it lacks, learned of this runtime, against the phrase that names the construct.</summary>
    private static readonly Dictionary<string, string> Constructs = Learned();

    /// <summary>
    /// The regular expression <paramref name="pattern"/> is, for a pattern of the code's own, whose refusal is a bug.
    /// </summary>
    /// <param name="pattern">The pattern, in .NET's grammar.</param>
    /// <param name="options">The options asked beside the linear engine's own.</param>
    /// <exception cref="ArgumentException">The pattern is refused; the message is the refusal.</exception>
    public static Regex Compile(string pattern, RegexOptions options = RegexOptions.None) =>
        TryCompile(pattern, options, out var regex, out var refusal) ? regex : throw new ArgumentException(refusal);

    /// <summary>
    /// The regular expression <paramref name="pattern"/> is, for a pattern of a user's, a configuration's or an agent's:
    /// <see langword="true"/> with it, or <see langword="false"/> with the refusal that names what the linear engine lacks.
    /// </summary>
    /// <param name="pattern">The pattern, in .NET's grammar.</param>
    /// <param name="options">The options asked beside the linear engine's own.</param>
    /// <param name="regex">The regular expression, where it compiled.</param>
    /// <param name="refusal">Why the pattern is refused, as a sentence that names it; where it compiled, <see langword="null"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="options"/> holds a value .NET does not define.</exception>
    public static bool TryCompile(string pattern, RegexOptions options, [NotNullWhen(true)] out Regex? regex, [NotNullWhen(false)] out string? refusal)
        => TryCompile(pattern, options, null, out regex, out refusal);

    /// <summary>
    /// The regular expression <paramref name="pattern"/> is, where it is .NET's writing of a pattern written in another
    /// dialect - a schema's ECMA-262, JSONPath's I-Regexp - as <see cref="TryCompile(string, RegexOptions, out Regex?, out string?)"/>
    /// makes one, its refusal naming the pattern as <paramref name="written"/> writes it: the one its reader wrote, never
    /// the writing they did not. A pattern that is no regular expression .NET reads is refused naming it so too, since
    /// .NET's own words would name the writing and an offset into it.
    /// </summary>
    /// <param name="pattern">The pattern, in .NET's grammar.</param>
    /// <param name="options">The options asked beside the linear engine's own.</param>
    /// <param name="written">The pattern as its reader wrote it, which a refusal names; <paramref name="pattern"/> itself where null.</param>
    /// <param name="regex">The regular expression, where it compiled.</param>
    /// <param name="refusal">Why the pattern is refused, as a sentence that names it; where it compiled, <see langword="null"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="options"/> holds a value .NET does not define.</exception>
    public static bool TryCompile(string pattern, RegexOptions options, string? written, [NotNullWhen(true)] out Regex? regex, [NotNullWhen(false)] out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        regex = null;
        var named = written ?? pattern;

        if (OptionsLacked(options) is { } asked)
        {
            refusal = $"The pattern '{named}' is asked for {asked}, which .NET's linear-time matcher lacks.";
            return false;
        }

        try
        {
            regex = new Regex(pattern, options | Linear, Regex.InfiniteMatchTimeout);

            if (regex.GetGroupNumbers().Length > 1 && EndsKept(pattern, options) is { } kept)
            {
                regex = new Regex(kept, options | Linear, Regex.InfiniteMatchTimeout);
            }

            refusal = null;
            return true;
        }
        catch (RegexParseException ex)
        {
            refusal = written is null ? ex.Message : $"The pattern '{written}' is no regular expression .NET reads.";
            return false;
        }
        catch (NotSupportedException ex)
        {
            refusal = Constructs.TryGetValue(ex.Message, out var construct)
                ? $"The pattern '{named}' holds {construct}, which .NET's linear-time matcher lacks."
                : $"The pattern '{named}' is refused by .NET's linear-time matcher: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// The first match of <paramref name="regex"/> in <paramref name="input"/> read from <paramref name="at"/> to its end as
    /// the whole of it: a <c>\A</c> of the pattern stands at <paramref name="at"/>, where .NET's <c>\G</c> stands for a match
    /// sought from there - an anchor the linear engine lacks - so a pattern that begins with <c>\A</c> matches only where a
    /// reader stands, and one that does not is sought from there, as from a text's start, nothing before it read.
    /// </summary>
    /// <param name="regex">A regular expression this made.</param>
    /// <param name="input">The text.</param>
    /// <param name="at">Where in <paramref name="input"/> the match is sought from.</param>
    public static Match MatchFrom(Regex regex, string input, int at)
    {
        ArgumentNullException.ThrowIfNull(regex);
        ArgumentNullException.ThrowIfNull(input);

        return regex.Match(input, at, input.Length - at);
    }

    /// <summary>
    /// <paramref name="pattern"/> with each <c>\z</c> it holds as an anchor written <c>(?:\Z\z)</c>, which matches where
    /// <c>\z</c> does and keeps the groups the engine loses at a <c>\z</c> after a final line feed; <see langword="null"/>
    /// where it holds none. Read in one pass as .NET reads a pattern that compiled: a backslash escapes the character after
    /// it, a class runs to the bracket that closes it - a subtracted class's included - a <c>(?#</c> comment to its first
    /// <c>)</c>, and where whitespace is ignored - asked of the options, or turned on or off by <c>(?x)</c>, <c>(?-x)</c>,
    /// <c>(?x:</c> to the end of the group that holds it - a <c>#</c> comment to the line's end.
    /// </summary>
    private static string? EndsKept(string pattern, RegexOptions options)
    {
        System.Text.StringBuilder? kept = null;
        var copied = 0;
        var spaced = (options & RegexOptions.IgnorePatternWhitespace) != 0;
        var outside = new Stack<bool>();
        var at = 0;

        while (at < pattern.Length)
        {
            switch (pattern[at])
            {
                case '\\':
                    if (at + 1 < pattern.Length && pattern[at + 1] == 'z')
                    {
                        kept ??= new System.Text.StringBuilder(pattern.Length + 8);
                        kept.Append(pattern, copied, at - copied).Append(@"(?:\Z\z)");
                        copied = at + 2;
                    }

                    at += 2;
                    break;

                case '[':
                    at = ClassEnd(pattern, at);
                    break;

                case '#' when spaced:
                    at = pattern.IndexOf('\n', at) is var line and >= 0 ? line + 1 : pattern.Length;
                    break;

                case '(' when string.CompareOrdinal(pattern, at, "(?#", 0, 3) == 0:
                    at = pattern.IndexOf(')', at) is var close and >= 0 ? close + 1 : pattern.Length;
                    break;

                case '(':
                    if (InlineOptions(pattern, at, spaced) is (var set, var end, var scoped))
                    {
                        if (scoped)
                        {
                            outside.Push(spaced);
                        }

                        spaced = set;
                        at = end;
                    }
                    else
                    {
                        outside.Push(spaced);
                        at++;
                    }

                    break;

                case ')':
                    spaced = outside.Count > 0 ? outside.Pop() : spaced;
                    at++;
                    break;

                default:
                    at++;
                    break;
            }
        }

        return kept?.Append(pattern, copied, pattern.Length - copied).ToString();
    }

    /// <summary>
    /// Where the class whose <c>[</c> stands at <paramref name="at"/> ends, past its <c>]</c>: a <c>]</c> right after the
    /// <c>[</c> or a <c>[^</c> is a member, a backslash escapes the character after it, and a <c>-[</c> opens a class
    /// subtracted inside it, closed by a <c>]</c> of its own.
    /// </summary>
    private static int ClassEnd(string pattern, int at)
    {
        var depth = 0;

        while (at < pattern.Length)
        {
            // At a class's opening: its '[', a '^', and a first ']' that is a member.
            at++;
            depth++;
            at += at < pattern.Length && pattern[at] == '^' ? 1 : 0;
            at += at < pattern.Length && pattern[at] == ']' ? 1 : 0;

            while (at < pattern.Length && pattern[at] != ']' && !(pattern[at] == '-' && at + 1 < pattern.Length && pattern[at + 1] == '['))
            {
                at += pattern[at] == '\\' ? 2 : 1;
            }

            if (at < pattern.Length && pattern[at] == '-')
            {
                // A subtracted class opens at its '['.
                at++;
                continue;
            }

            // Each ']' closes the innermost class still open.
            while (at < pattern.Length && pattern[at] == ']' && depth > 0)
            {
                at++;
                depth--;
            }

            if (depth == 0)
            {
                return at;
            }
        }

        return pattern.Length;
    }

    /// <summary>
    /// The inline options group at <paramref name="at"/> - <c>(?imnsx-imnsx)</c>, or <c>(?imnsx-imnsx:</c> that opens a
    /// group - as whether whitespace is ignored after it, where the pattern goes on, and whether it opens a group the
    /// setting holds to the end of; null where the group at <paramref name="at"/> is of another kind.
    /// </summary>
    private static (bool Spaced, int End, bool Scoped)? InlineOptions(string pattern, int at, bool spaced)
    {
        if (at + 1 >= pattern.Length || pattern[at + 1] != '?')
        {
            return null;
        }

        var on = true;
        var end = at + 2;

        for (; end < pattern.Length && pattern[end] is 'i' or 'm' or 'n' or 's' or 'x' or '-'; end++)
        {
            if (pattern[end] == '-')
            {
                on = false;
            }
            else if (pattern[end] == 'x')
            {
                spaced = on;
            }
        }

        return end < pattern.Length && pattern[end] is ')' or ':' ? (spaced, end + 1, pattern[end] == ':') : null;
    }

    /// <summary>The options asked that the linear engine lacks, named and joined by "and"; <see langword="null"/> where none is.</summary>
    private static string? OptionsLacked(RegexOptions options)
    {
        List<string> lacked = [];

        if ((options & RegexOptions.RightToLeft) != 0)
        {
            lacked.Add(nameof(RegexOptions.RightToLeft));
        }

        if ((options & RegexOptions.ECMAScript) != 0)
        {
            lacked.Add(nameof(RegexOptions.ECMAScript));
        }

        return lacked.Count == 0 ? null : string.Join(" and ", lacked);
    }

    /// <summary>
    /// The message this runtime's linear engine refuses an example of each construct it lacks with, against the phrase that
    /// names the construct. An example the engine compiles is of a construct it no longer lacks, and is left out.
    /// </summary>
    private static Dictionary<string, string> Learned()
    {
        // The two lookaheads stand for the lookbehinds too, and the two conditionals for each other: .NET gives a
        // lookbehind its lookahead's message, and words a conditional on a group otherwise than one on a pattern.
        (string Example, string Construct)[] examples =
        [
            (@"(a)\1", "a backreference"),
            ("(?=a)", "a lookahead or lookbehind"),
            ("(?!a)", "a lookahead or lookbehind"),
            ("(?>a)", "an atomic group"),
            ("(?(a)a|b)", "a conditional"),
            ("(a)(?(1)a|b)", "a conditional"),
            (@"\G", @"a \G anchor"),
            ("(?<a>a)(?<b-a>b)", "a balancing group"),
        ];

        var constructs = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (example, construct) in examples)
        {
            try
            {
                _ = new Regex(example, Linear, Regex.InfiniteMatchTimeout);
            }
            catch (NotSupportedException ex)
            {
                constructs.TryAdd(ex.Message, construct);
            }
        }

        return constructs;
    }
}
