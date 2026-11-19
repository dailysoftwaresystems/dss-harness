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
    {
        ArgumentNullException.ThrowIfNull(pattern);

        regex = null;

        if (OptionsLacked(options) is { } asked)
        {
            refusal = $"The pattern '{pattern}' is asked for {asked}, which .NET's linear-time matcher lacks.";
            return false;
        }

        try
        {
            regex = new Regex(pattern, options | Linear, Regex.InfiniteMatchTimeout);
            refusal = null;
            return true;
        }
        catch (RegexParseException ex)
        {
            refusal = ex.Message;
            return false;
        }
        catch (NotSupportedException ex)
        {
            refusal = Constructs.TryGetValue(ex.Message, out var construct)
                ? $"The pattern '{pattern}' holds {construct}, which .NET's linear-time matcher lacks."
                : $"The pattern '{pattern}' is refused by .NET's linear-time matcher: {ex.Message}";
            return false;
        }
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
