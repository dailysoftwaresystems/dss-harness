using System.Text;
using System.Text.Json.Nodes;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The one grammar every template of a run is read and filled by - a pause step's question, a step key that hands text
/// on, a pull request's base, a prompt - the configuration's own (<see cref="Execution.LegPathNames.Fill"/>): a brace
/// doubled stands for one, <c>{{</c> and <c>}}</c> alike; another expander's <c>${...}</c> is left as written; and
/// any other text in braces is a placeholder, closed on its own line before the next one opens, whose name is an
/// expression read by the roots the caller's reader reads - with how its value is written after a <c>|</c>, which the
/// filler leaves to the writer - its value written as a template writes it.
/// </summary>
public static class FlowTemplate
{
    /// <summary>What one piece of a template is.</summary>
    public enum PieceKind
    {
        /// <summary>Text, standing for itself: a brace doubled written once, another expander's group as written.</summary>
        Text,

        /// <summary>A placeholder: <see cref="Piece.Text"/> is what its braces hold.</summary>
        Placeholder,

        /// <summary>A placeholder never closed - nothing past it is read - and <see cref="Piece.Text"/> the rest of the template from it.</summary>
        Unclosed,
    }

    /// <summary>One piece of a template, in the order the template holds them.</summary>
    /// <param name="At">Where it starts in the template.</param>
    /// <param name="Text">What it holds, as its <paramref name="Kind"/> says.</param>
    /// <param name="Kind">What it is.</param>
    public readonly record struct Piece(int At, string Text, PieceKind Kind)
    {
        /// <summary>The name a placeholder reads: what its braces hold before a <c>|</c>, trimmed; empty where they hold none.</summary>
        public string Name => Text.Split('|', 2)[0].Trim();
    }

    /// <summary>
    /// <paramref name="template"/> in its pieces, by the one grammar: what is text, and what each placeholder holds,
    /// up to one never closed, which ends them.
    /// </summary>
    /// <param name="template">The template, as it is written.</param>
    public static IEnumerable<Piece> Pieces(string template) => Pieces(template, quoted: false);

    /// <summary>
    /// <paramref name="template"/> in its pieces, by the one grammar - where <paramref name="quoted"/>, a placeholder's
    /// quoted text read as its own: a brace between quotes, as a JSONPath query's quoted name may hold, neither closes
    /// the placeholder nor opens another, and a backslash between them escapes the character after it.
    /// </summary>
    /// <param name="template">The template, as it is written.</param>
    /// <param name="quoted">Whether a placeholder's text in quotes is read as its own.</param>
    public static IEnumerable<Piece> Pieces(string template, bool quoted)
    {
        ArgumentNullException.ThrowIfNull(template);

        return Read(template, quoted);
    }

    /// <summary>
    /// Where the placeholder whose <c>{</c> is at <paramref name="at"/> ends: the first <c>}</c> on its line, before
    /// the next <c>{</c> opens - neither counted between quotes where <paramref name="quoted"/>; -1 where there is none.
    /// </summary>
    private static int End(string template, int at, bool quoted)
    {
        char? quote = null;
        var escaped = false;

        for (var next = at + 1; next < template.Length; next++)
        {
            var character = template[next];

            if (character == '\n')
            {
                return -1;
            }

            if (escaped)
            {
                escaped = false;
            }
            else if (quote is { } open)
            {
                escaped = character == '\\';
                quote = character == open ? null : quote;
            }
            else if (character == '}')
            {
                return next;
            }
            else if (character == '{')
            {
                return -1;
            }
            else if (quoted && character is '\'' or '"')
            {
                quote = character;
            }
        }

        return -1;
    }

    private static IEnumerable<Piece> Read(string template, bool quoted)
    {
        var text = new StringBuilder();
        var textAt = 0;
        var at = 0;

        while (at < template.Length)
        {
            if (text.Length == 0)
            {
                textAt = at;
            }

            // A doubled brace and another expander's group are text, by the configuration's own rule of them.
            if (Execution.LegPathNames.LiteralAt(template, at) is { } literal)
            {
                text.Append(literal.Text);
                at += literal.Length;
                continue;
            }

            if (template[at] != '{')
            {
                text.Append(template[at]);
                at++;
                continue;
            }

            if (text.Length > 0)
            {
                yield return new(textAt, text.ToString(), PieceKind.Text);
                text.Clear();
            }

            // A placeholder is closed on its own line, before the next one opens: a '}' further on closes another.
            var close = End(template, at, quoted);

            if (close < 0)
            {
                yield return new(at, template[at..], PieceKind.Unclosed);
                yield break;
            }

            yield return new(at, template[(at + 1)..close], PieceKind.Placeholder);
            at = close + 1;
        }

        if (text.Length > 0)
        {
            yield return new(textAt, text.ToString(), PieceKind.Text);
        }
    }

    /// <summary>
    /// Fills <paramref name="template"/>, each name in braces read by <paramref name="read"/>. A name that reads nothing
    /// says why, in <paramref name="problem"/>, and fills nothing.
    /// </summary>
    /// <param name="template">The template, as the definition spells it.</param>
    /// <param name="read">Reads one path of a name, as the run's expressions read it.</param>
    /// <param name="problem">Why it could not be filled; null where it was.</param>
    /// <returns>The text filled; null where it could not be.</returns>
    public static string? Fill(string template, Func<FlowExpression.Path, FlowExpression.PathRead> read, out string? problem)
        => FillAroundSkills(template, read, out problem) is { } filled ? Joined(filled) : null;

    /// <summary>
    /// Fills <paramref name="template"/> as <see cref="Fill"/> does, but each placeholder that names a skill
    /// (<see cref="AgentSkills.Names"/>) kept in its place as a piece of its own: a reason step's prompt, whose skills
    /// its runner hands over. What was filled is never read as a template again - a brace a value holds stays a brace,
    /// and a skill a value names, a person's words among them, stays text. A name that reads nothing says why, in
    /// <paramref name="problem"/>, and fills nothing.
    /// </summary>
    /// <param name="template">The template, as the definition spells it.</param>
    /// <param name="read">Reads one path of a name, as the run's expressions read it.</param>
    /// <param name="problem">Why it could not be filled; null where it was.</param>
    /// <returns>
    /// The text filled, in order: <see cref="PieceKind.Text"/> pieces holding what was filled, and a
    /// <see cref="PieceKind.Placeholder"/> piece for each skill, holding what its braces hold; null where it could not be.
    /// </returns>
    public static IReadOnlyList<Piece>? FillAroundSkills(string template, Func<FlowExpression.Path, FlowExpression.PathRead> read, out string? problem)
        => FillAroundSkills(template, read, spell: null, out problem);

    /// <summary>
    /// Fills <paramref name="template"/> as the overload beside it does, with one case spelled rather than refused or
    /// filled as null: a placeholder that reads nothing, or null, where <paramref name="spell"/> says what it names - a
    /// step that left no output in this run, as a prompt's placeholder is filled for its agent, so the agent is told what
    /// it does not have - fills with the sentence <paramref name="spell"/> gives it. A name that reads nothing and is
    /// spelled nothing says why, in <paramref name="problem"/>, and fills nothing.
    /// </summary>
    /// <param name="template">The template, as the definition spells it.</param>
    /// <param name="read">Reads one path of a name, as the run's expressions read it.</param>
    /// <param name="spell">What a placeholder that reads nothing is filled with, where what it names left no output; null where nothing is.</param>
    /// <param name="problem">Why it could not be filled; null where it was.</param>
    /// <returns>
    /// The text filled, in order: each <see cref="PieceKind.Text"/> piece holding what was filled, and a
    /// <see cref="PieceKind.Placeholder"/> piece for each skill, holding what its braces hold; null where it could not be.
    /// </returns>
    public static IReadOnlyList<Piece>? FillAroundSkills(string template, Func<FlowExpression.Path, FlowExpression.PathRead> read, Func<string, string?>? spell, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(read);

        var pieces = new List<Piece>();
        var filled = new StringBuilder(template.Length);
        var filledAt = 0;

        void Close(int at)
        {
            if (filled.Length > 0)
            {
                pieces.Add(new(filledAt, filled.ToString(), PieceKind.Text));
                filled.Clear();
            }

            filledAt = at;
        }

        foreach (var piece in Pieces(template))
        {
            if (piece.Kind == PieceKind.Text)
            {
                filled.Append(piece.Text);
                continue;
            }

            if (piece.Kind == PieceKind.Unclosed || piece.Name is not { Length: > 0 } name)
            {
                problem = $"'{(piece.Kind == PieceKind.Unclosed ? piece.Text : $"{{{piece.Text}}}")}' is no name in braces";
                return null;
            }

            // A skill's name is a reason step's prompt's alone, and is not read here: the folder is handed over where
            // the step starts, by its runner, which puts the skill's block where the placeholder stands. The
            // definition's check refuses the name in every other key a template fills.
            if (AgentSkills.Names(name))
            {
                Close(piece.At);
                pieces.Add(piece);
                filledAt = piece.At + piece.Text.Length + 2;
                continue;
            }

            try
            {
                var value = FlowExpression.Parse(name).Evaluate(read);

                // A step that left no output reads nothing through a member of it, and null whole - a skipped step's
                // output is none: either way it is spelled where spell says what it names, and only a read of nothing it
                // does not spell is refused; a null it does not spell - a member an output holds as null - is filled as one.
                if ((value.IsMissing || value.IsNull) && spell?.Invoke(name) is { } sentence)
                {
                    filled.Append(sentence);
                    continue;
                }

                if (value.IsMissing)
                {
                    problem = $"'{{{name}}}' reads nothing";
                    return null;
                }

                filled.Append(value.AsTemplateText());
            }
            catch (UnresolvedReadException unreadable)
            {
                problem = $"'{{{name}}}': {unreadable.Message}";
                return null;
            }
            catch (ArgumentException notWhole)
            {
                problem = $"'{{{name}}}': {notWhole.Message}";
                return null;
            }
        }

        Close(template.Length);
        problem = null;
        return pieces;
    }

    /// <summary>
    /// Filled pieces (<see cref="FillAroundSkills(string, Func{FlowExpression.Path, FlowExpression.PathRead}, Func{string, string?}, out string?)"/>) as one text: each filled text as it is, and each skill's
    /// placeholder written back in its braces, where nothing hands its skill over.
    /// </summary>
    /// <param name="pieces">The pieces, in order.</param>
    public static string Joined(IReadOnlyList<Piece> pieces)
    {
        ArgumentNullException.ThrowIfNull(pieces);

        return string.Concat(pieces.Select(piece => piece.Kind == PieceKind.Placeholder ? $"{{{piece.Text}}}" : piece.Text));
    }

    /// <summary>
    /// The branch the flow's pull request is opened against, where its <c>pull-request</c> step names one with
    /// <c>base</c>: filled from the run's inputs and its name, which is all a base may read (V16-V19), so the run's start
    /// and its <c>orient</c> step fill it alike. Null where no pull-request step names a base; the definition's check
    /// holds every one that does to the same text.
    /// </summary>
    /// <param name="definition">The workflow.</param>
    /// <param name="inputs">The run's inputs, resolved.</param>
    /// <param name="run">The run's name.</param>
    /// <param name="problem">Why the base could not be filled; null where it was, or where none is named.</param>
    public static string? PullRequestBase(WorkflowDefinition definition, IReadOnlyDictionary<string, string> inputs, string run, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(run);

        problem = null;

        if (WorkflowFiles.Steps(definition).Select(entry => entry.Step).FirstOrDefault(step => step.Type == "pull-request" && step.Base is not null) is not { Base: { } template })
        {
            return null;
        }

        return Fill(
            template,
            path => path.Root switch
            {
                "input" => new(FlowValue.Of(FlowInputs.AsObject(inputs)), 0),
                "run" => new(FlowValue.Of(new JsonObject { ["name"] = run }), 0),
                _ => new(FlowValue.Missing, 0),
            },
            out problem);
    }
}
