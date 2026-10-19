namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The names an expression or a template reads: extracted here, checked by
/// <see cref="WorkflowValidator"/> against the flow's own names.
/// </summary>
/// <remarks>
/// <para>
/// An expression is boolean work over <em>reads</em> - dotted names whose first segment says where
/// the rest of the name lives - joined by <c>&amp;&amp;</c>, <c>||</c>, <c>!</c>, <c>==</c>,
/// <c>!=</c> and <c>??</c>, with <c>(</c> and <c>)</c> to group and single- or double-quoted strings
/// as literals. Two functions take a read of their own: <c>empty(x)</c>, whether a read holds
/// nothing, and <c>any(list, test)</c>, whether any member of a list meets a test written with
/// <c>it</c> for the member.
/// </para>
/// <para>
/// What the segments after the first <em>mean</em> - that a member named after a step is one the
/// step's schema holds - is the engine's to check when it runs; what this grammar decides is where
/// a read starts, so that a definition can be refused for naming a step, an input or a param that
/// does not exist.
/// </para>
/// </remarks>
public static class WorkflowExpressions
{
    /// <summary>
    /// The words a read can start with that name no step: the run's own names - what it was given, where in its loops
    /// and handlers it is, what it knows of itself - the functions an expression calls, and the run facts a template
    /// reads. A step may be named none of them (<see cref="ReservedNames"/>), so that no read is ever between two answers.
    /// </summary>
    public static readonly IReadOnlyList<string> ReservedRoots =
    [
        "input", "param", "run", "carried", "previous", "source", "issue", "item", "it", "index", "iteration", "visit",
        "recover", "skill", "recommendations", "update", "piece", "status", "question", "empty", "any",
        "worktree", "work", "plans", "rows", "orchestrator", "agent", "step", "os",
    ];

    /// <summary>
    /// The engine's own facts of a step, read after its name - <c>review.code</c>, <c>plan.latest.items</c> - beside the
    /// members of its output: how it ended, its exit code, its message, the question its failure was answered with, and
    /// of a reasoning step its newest output and what its agent assumed.
    /// </summary>
    public static readonly IReadOnlyList<string> StepFacts = ["outcome", "code", "error", "question", "latest", "assumptions"];

    /// <summary>The literals an identifier can be without naming anything.</summary>
    private static readonly IReadOnlyList<string> Literals = ["true", "false", "null"];

    /// <summary>
    /// The names no step may have: every word a read starts with that names no step, every fact of the engine's that
    /// follows a step's name, and the literals. A step named for a root would never be read, its reads taken for the
    /// run's own; one named for a fact would make one path say two things - inside a block <c>review</c>, a member named
    /// <c>code</c> would make <c>review.code</c> the block's exit code and that member's output at once.
    /// </summary>
    public static IReadOnlyList<string> ReservedNames { get; } = [.. ReservedRoots.Concat(StepFacts).Concat(Literals).Distinct(StringComparer.Ordinal)];

    /// <summary>One name an expression reads: its first segment, whole, and how it was read.</summary>
    /// <param name="Root">The read's first name, without an index: where the rest of the name lives.</param>
    /// <param name="Whole">The whole read, as written, its indexes included: <c>plan.items[0].ok</c>.</param>
    /// <param name="Template">Whether the read came out of a <c>{...}</c> template.</param>
    /// <param name="InsideAny">Whether the read sits in the test of an <c>any(...)</c>, where <c>it</c> names the member.</param>
    public sealed record Read(string Root, string Whole, bool Template, bool InsideAny)
    {
        /// <summary>
        /// The read's names in order, its indexes left out - <c>plan</c>, <c>items</c>, <c>ok</c> of
        /// <c>plan.items[0].ok</c> - as a name is looked up: an index is a place in a list, and names nothing.
        /// </summary>
        public IReadOnlyList<string> Names => [.. Whole.Split('.').Select(segment => segment.IndexOf('[') is var open and >= 0 ? segment[..open] : segment)];
    }

    /// <summary>
    /// <paramref name="written"/> as a read, the one way the scanner here and <see cref="FlowExpression"/> alike spell
    /// one: its dotted segments trimmed of space and joined again, its root the first name without an index.
    /// </summary>
    /// <param name="written">The read, as the expression or the placeholder wrote it.</param>
    /// <param name="template">Whether it came out of a <c>{...}</c> placeholder.</param>
    /// <param name="insideAny">Whether it sits in an <c>any(...)</c>.</param>
    internal static Read ReadOf(string written, bool template, bool insideAny)
    {
        // The read's own grammar - a dotted name - is walked here, so that 'a.b.c' is one read and not three.
        // Anything after the first segment is the engine's to mean.
        var segments = written.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var whole = segments.Length > 0 ? string.Join('.', segments) : written;
        var first = segments.Length > 0 ? segments[0] : written;

        return new Read(first.IndexOf('[') is var open and >= 0 ? first[..open] : first, whole, template, insideAny);
    }

    /// <summary>
    /// The whole number <paramref name="written"/> says, as a path's index and an instance id's marks are written:
    /// digits alone - no sign, no space - with no leading zero but in <c>0</c> itself; <see langword="null"/> where it
    /// is not written so, or is past what an index can be. One spelling, so that one place in a list is never written
    /// two ways.
    /// </summary>
    /// <param name="written">The digits, as written.</param>
    public static int? WholeNumber(string written)
    {
        ArgumentNullException.ThrowIfNull(written);

        return written.Length > 0
            && written.All(character => character is >= '0' and <= '9')
            && (written.Length == 1 || written[0] != '0')
            && int.TryParse(written, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
    }

    /// <summary>
    /// Every name <paramref name="expression"/> reads, in the order it reads them, once each.
    /// </summary>
    /// <param name="expression">An expression, or a template's text.</param>
    /// <remarks>An empty expression reads nothing; that it is empty is the validator's to refuse.</remarks>
    public static IReadOnlyList<Read> ReadsOf(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);

        var reads = new List<Read>();
        var index = 0;

        // One entry per open '(': whether it opened an any(...) call, whose test may read 'it'.
        var calls = new Stack<bool>();

        while (index < expression.Length)
        {
            var character = expression[index];

            if (char.IsWhiteSpace(character))
            {
                index++;
                continue;
            }

            // A quoted string is a literal, whatever it holds: 'approve' names nothing.
            if (character is '\'' or '"')
            {
                index = SkipQuoted(expression, index);
                continue;
            }

            // A template inside an expression: its content is a read of its own.
            if (character == '{')
            {
                var end = expression.IndexOf('}', index + 1);

                if (end < 0)
                {
                    break;
                }

                Add(reads, expression[(index + 1)..end], Template: true, calls.Any(call => call));
                index = end + 1;
                continue;
            }

            if (character == '(')
            {
                calls.Push(false);
                index++;
                continue;
            }

            if (character == ')' && calls.Count > 0)
            {
                calls.Pop();
                index++;
                continue;
            }

            if (StartsName(character))
            {
                var (name, next) = ReadName(expression, index);
                index = next;

                // any( opens a context in which 'it' names the member; its '(' is consumed here, so
                // that the call's own parenthesis is the one marked.
                if (string.Equals(name, "any", StringComparison.Ordinal) && NextIsOpenParenthesis(expression, index))
                {
                    calls.Push(true);
                    index = AfterOpenParenthesis(expression, index);
                    continue;
                }

                if (name.Length == 0 || char.IsDigit(name[0]) || Literals.Contains(name, StringComparer.Ordinal))
                {
                    continue;
                }

                Add(reads, name, Template: false, calls.Any(call => call));
                continue;
            }

            index++;
        }

        return reads;
    }

    /// <summary>
    /// Every name <paramref name="text"/> reads through a <c>{...}</c> template, in the order the
    /// templates sit in the text, once each.
    /// </summary>
    /// <param name="text">A template's text: any run of it may hold one or more <c>{...}</c> placeholders.</param>
    /// <remarks>An empty text reads nothing; that it is empty is the validator's to refuse.</remarks>
    public static IReadOnlyList<Read> TemplateReads(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var reads = new List<Read>();

        for (var index = text.IndexOf('{'); index >= 0; index = text.IndexOf('{', index + 1))
        {
            var end = text.IndexOf('}', index + 1);

            if (end < 0)
            {
                break;
            }

            var content = text[(index + 1)..end].Trim();
            index = end;

            if (content.Length == 0)
            {
                continue;
            }

            // A placeholder may say how its value is written, after '|': what it reads is before it.
            var separator = content.IndexOf('|');

            if (separator >= 0)
            {
                content = content[..separator].Trim();
            }

            if (content.Length > 0)
            {
                Add(reads, content, Template: true, insideAny: false);
            }
        }

        return reads;
    }

    /// <summary>
    /// What is wrong with how <paramref name="expression"/> is written, where something is: a quoted string or a
    /// placeholder left open, or parentheses that do not pair. A read past such a place is never reached, so a name
    /// misspelt there would pass unseen; the expression is refused instead, with where it went wrong.
    /// </summary>
    /// <param name="expression">An expression.</param>
    /// <returns>The problem, in words, or <see langword="null"/> where the expression is written whole.</returns>
    public static string? SyntaxProblem(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);

        var depth = 0;

        for (var index = 0; index < expression.Length; index++)
        {
            switch (expression[index])
            {
                case '\'' or '"':
                    var end = expression.IndexOf(expression[index], index + 1);

                    if (end < 0)
                    {
                        return $"the quote at column {index + 1} is never closed";
                    }

                    index = end;
                    break;

                case '{':
                    var close = expression.IndexOfAny(['}', '{'], index + 1);

                    if (close < 0 || expression[close] != '}')
                    {
                        return $"the placeholder at column {index + 1} is never closed";
                    }

                    index = close;
                    break;

                case '(':
                    depth++;
                    break;

                case ')':
                    if (--depth < 0)
                    {
                        return $"the parenthesis at column {index + 1} closes nothing that was opened";
                    }

                    break;
            }
        }

        return depth > 0 ? $"{depth} parenthes{(depth == 1 ? "is is" : "es are")} opened and never closed" : null;
    }

    /// <summary>
    /// What is wrong with how a template's <paramref name="text"/> is written, where something is: a placeholder left
    /// open, after which nothing the text reads would be seen.
    /// </summary>
    /// <param name="text">A template's text.</param>
    /// <returns>The problem, in words, or <see langword="null"/> where every placeholder is closed.</returns>
    public static string? TemplateSyntaxProblem(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        for (var index = text.IndexOf('{'); index >= 0; index = text.IndexOf('{', index + 1))
        {
            // A placeholder is closed on its own line, before the next one opens: a '}' further on closes another.
            var end = text.IndexOfAny(['}', '{', '\n'], index + 1);

            if (end < 0 || text[end] != '}')
            {
                return $"the placeholder at column {ColumnOf(text, index)} of line {LineOf(text, index)} is never closed";
            }

            index = end;
        }

        return null;
    }

    /// <summary>Whether <paramref name="name"/> is one a step may not have (<see cref="ReservedNames"/>).</summary>
    /// <param name="name">A step's name, or a declared input's, param's or output's.</param>
    public static bool IsReserved(string name)
        => ReservedNames.Contains(name, StringComparer.Ordinal);

    /// <summary>Whether a read starting with <paramref name="root"/> reads the run's own names, and no step (<see cref="ReservedRoots"/>).</summary>
    /// <param name="root">A read's first segment.</param>
    public static bool IsRoot(string root)
        => ReservedRoots.Contains(root, StringComparer.Ordinal);

    /// <summary>The line, counted from 1, that <paramref name="index"/> sits on in <paramref name="text"/>.</summary>
    private static int LineOf(string text, int index) => text[..index].Count(character => character == '\n') + 1;

    /// <summary>The column, counted from 1, that <paramref name="index"/> sits at on its own line.</summary>
    private static int ColumnOf(string text, int index) => index - text[..index].LastIndexOf('\n');

    private static void Add(List<Read> reads, string name, bool Template, bool insideAny)
    {
        var read = ReadOf(name, Template, insideAny);

        if (reads.All(earlier => !string.Equals(earlier.Whole, read.Whole, StringComparison.Ordinal)))
        {
            reads.Add(read);
        }
    }

    /// <summary>Whether the first non-space character at <paramref name="index"/> opens a call.</summary>
    private static bool NextIsOpenParenthesis(string expression, int index)
        => SkipSpaces(expression, index) < expression.Length && expression[SkipSpaces(expression, index)] == '(';

    /// <summary>The position after the first non-space character at <paramref name="index"/>.</summary>
    private static int AfterOpenParenthesis(string expression, int index)
        => SkipSpaces(expression, index) + 1;

    private static int SkipSpaces(string expression, int index)
    {
        while (index < expression.Length && char.IsWhiteSpace(expression[index]))
        {
            index++;
        }

        return index;
    }

    /// <summary>A name starts with a letter or an underscore; digits may follow, and dashes within.</summary>
    private static bool StartsName(char character) => char.IsLetter(character) || character == '_';

    /// <summary>
    /// Reads one path as it is written whole, with no space inside it: segments of name characters
    /// joined by dots, each followed by any indexes into a list (<c>items[0]</c>). A dash is a name
    /// character, because a step's name may hold one; a dot is not, because it separates the
    /// segments of a read. Read in one pass, however many segments the path has.
    /// </summary>
    private static (string Name, int Next) ReadName(string expression, int index)
    {
        var end = index;

        while (true)
        {
            while (end < expression.Length && (char.IsLetterOrDigit(expression[end]) || expression[end] is '_' or '-'))
            {
                end++;
            }

            // An index is part of the path where it is written as one: '[', a whole number, ']'.
            while (end < expression.Length && expression[end] == '['
                && expression.IndexOf(']', end + 1) is var close and > 0
                && WholeNumber(expression[(end + 1)..close]) is not null)
            {
                end = close + 1;
            }

            // A dotted read: a.b, a.b.c. A trailing dot (a member the engine fills) ends the name here.
            if (end < expression.Length && expression[end] == '.' && end + 1 < expression.Length && StartsName(expression[end + 1]))
            {
                end++;
                continue;
            }

            return (expression[index..end], end);
        }
    }

    /// <summary>The position after the quoted string that starts at <paramref name="index"/>.</summary>
    private static int SkipQuoted(string expression, int index)
    {
        var quote = expression[index];
        var end = index + 1;

        while (end < expression.Length && expression[end] != quote)
        {
            end++;
        }

        return end < expression.Length ? end + 1 : end;
    }
}
