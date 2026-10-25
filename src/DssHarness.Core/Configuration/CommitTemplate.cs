using System.Text;
using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Core.Configuration;

/// <summary>
/// <c>commit.template</c>'s placeholders, its filling and its refusals, in one place: read by the configuration's one
/// brace grammar (<see cref="FlowTemplate.Pieces(string)"/>) - <c>{{</c> and <c>}}</c> braces, <c>${...}</c> left as written -
/// each placeholder a name of <c>commit.variables</c> matched whatever its case. The configuration's check reads the
/// placeholders and the refusals here, the definition's check its refusals, and a <c>commit</c> step fills the template
/// here.
/// </summary>
public static class CommitTemplate
{
    /// <summary>Why a commit step has no message where the configuration declares no template, said of the step.</summary>
    public const string NoMessage = "names no message and the configuration declares no commit.template; one of the two writes the commit's message";

    /// <summary>
    /// Why a commit step that gives no value for <paramref name="name"/>, which the template requires, has no message, said
    /// of the step - with what the variable means, where the configuration says.
    /// </summary>
    /// <param name="name">The variable's name, as the configuration declares it.</param>
    /// <param name="variable">The variable.</param>
    public static string NoValue(string name, CommitVariable variable)
    {
        ArgumentNullException.ThrowIfNull(variable);

        return $"gives no value for '{name}', which commit.template requires{(variable.Description is { } means ? $" ({means})" : string.Empty)}: add it under the step's variables";
    }

    /// <summary>Why a template that uses <paramref name="placeholder"/>, which <c>commit.variables</c> does not declare, is refused.</summary>
    /// <param name="placeholder">The name the placeholder names.</param>
    public static string Undeclared(string placeholder)
        => $"commit.template uses {{{placeholder}}}, which is not declared in commit.variables; declare it there, or take it out of the template";

    /// <summary>Why <paramref name="template"/> cannot be read as a template: a placeholder never closed, or one that names nothing; null where it can.</summary>
    /// <param name="template">The template, as <c>commit.template</c> holds it.</param>
    public static string? GrammarProblem(string template)
        => WorkflowExpressions.TemplateSyntaxProblem(template) is { } broken ? $"commit.template is not written whole: {broken}" : null;

    /// <summary>The names the placeholders of <paramref name="template"/> name, each once whatever its case, in the order first named.</summary>
    /// <param name="template">The template, as <c>commit.template</c> holds it.</param>
    public static IReadOnlyList<string> Placeholders(string template)
    {
        ArgumentNullException.ThrowIfNull(template);

        return [.. FlowTemplate.Pieces(template).Where(piece => piece.Kind == FlowTemplate.PieceKind.Placeholder).Select(piece => piece.Text).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The message a <c>commit</c> step commits with: where the configuration declares no template, the step's own
    /// <paramref name="message"/>; where it declares one, the template with each placeholder replaced, in one pass - so a
    /// value is never filled again - by the step's value for that variable, else its declared default, else nothing.
    /// Null, with why - said of the step - where the configuration declares no template and the step names no message,
    /// where the template cannot be read or a placeholder names a variable <c>commit.variables</c> does not declare,
    /// where a variable the template requires has no value, and where the message is blank once filled, which
    /// <c>git commit</c> refuses.
    /// </summary>
    /// <param name="commit">The configuration's <c>commit</c> section.</param>
    /// <param name="message">The step's own message, filled; null where it names none.</param>
    /// <param name="variables">The step's values for the template's variables, each filled, by name whatever its case.</param>
    /// <param name="problem">Why there is no message; null where there is one.</param>
    public static string? Message(CommitConfig commit, string? message, IReadOnlyDictionary<string, string> variables, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(variables);

        problem = null;

        if (commit.Template is not { } template)
        {
            if (message is null)
            {
                problem = NoMessage;
                return null;
            }

            return Unblank(message, out problem);
        }

        if (GrammarProblem(template) is { } broken)
        {
            problem = $"cannot fill its message: {broken}";
            return null;
        }

        var filled = new StringBuilder();

        foreach (var piece in FlowTemplate.Pieces(template))
        {
            if (piece.Kind != FlowTemplate.PieceKind.Placeholder)
            {
                filled.Append(piece.Text);
                continue;
            }

            if (DeclaredName.In(commit.Variables.Keys, piece.Text) is not { } declared)
            {
                problem = $"cannot fill its message: {Undeclared(piece.Text)}";
                return null;
            }

            if (DeclaredName.In(variables.Keys, piece.Text) is { } given)
            {
                filled.Append(variables[given]);
                continue;
            }

            var variable = commit.Variables[declared];

            if (variable.Required)
            {
                problem = NoValue(declared, variable);
                return null;
            }

            filled.Append(variable.Default);
        }

        return Unblank(filled.ToString(), out problem);
    }

    /// <summary>The message as it is, or null with why where it holds nothing but blanks.</summary>
    private static string? Unblank(string message, out string? problem)
    {
        problem = string.IsNullOrWhiteSpace(message)
            ? "has a message that is blank once filled, and git refuses to commit a blank message: give the step a message, or values that fill the template"
            : null;
        return problem is null ? message : null;
    }
}
