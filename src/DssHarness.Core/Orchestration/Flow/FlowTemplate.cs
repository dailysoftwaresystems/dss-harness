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
    public static IEnumerable<Piece> Pieces(string template)
    {
        ArgumentNullException.ThrowIfNull(template);

        var text = new StringBuilder();
        var textAt = 0;
        var at = 0;

        while (at < template.Length)
        {
            if (text.Length == 0)
            {
                textAt = at;
            }

            if (Holds(template, at, "{{") || Holds(template, at, "}}"))
            {
                text.Append(template[at]);
                at += 2;
                continue;
            }

            if (Holds(template, at, "${") && template.IndexOf('}', at + 2) is var expanded and >= 0)
            {
                text.Append(template, at, expanded + 1 - at);
                at = expanded + 1;
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
            var close = template.IndexOfAny(['}', '{', '\n'], at + 1);

            if (close < 0 || template[close] != '}')
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
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(read);

        var filled = new StringBuilder(template.Length);

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

            try
            {
                var value = FlowExpression.Parse(name).Evaluate(read);

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

        problem = null;
        return filled.ToString();
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

    /// <summary>Whether <paramref name="template"/> holds <paramref name="pair"/> at <paramref name="at"/>.</summary>
    private static bool Holds(string template, int at, string pair)
        => string.CompareOrdinal(template, at, pair, 0, pair.Length) == 0;
}
