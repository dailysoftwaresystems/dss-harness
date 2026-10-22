using System.Diagnostics;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// Fills one template of a step, by the one grammar and the names the step reads; <see langword="null"/>, saying why in
/// <paramref name="problem"/>, where a name it reads gives nothing.
/// </summary>
/// <param name="template">The template, as the definition writes it.</param>
/// <param name="problem">Why it could not be filled; null where it was.</param>
public delegate string? FlowTemplateFiller(string template, out string? problem);

/// <summary>
/// The template keys of a step - the text it hands on, which its type's table marks
/// (<see cref="WorkflowStepTypes.StepType.TemplateKeys"/>) - read and filled in one place: the definition's check reads
/// each text, and the engine fills each where the step starts, so the two never take different texts for one key.
/// </summary>
/// <remarks>
/// A scalar key is one text, a list key one text for each member, a map key one for each value. A pause's
/// <c>question</c> is filled as the pause asks, not where a step starts, and is left as written here.
/// </remarks>
public static class FlowStepTemplates
{
    /// <summary>The texts of the template key <paramref name="key"/> of <paramref name="step"/>, each as written.</summary>
    /// <param name="step">The step.</param>
    /// <param name="key">A key its type marks a template.</param>
    /// <exception cref="UnreachableException">The key is no template key any type marks.</exception>
    public static IReadOnlyList<string> Texts(WorkflowStep step, string key)
    {
        ArgumentNullException.ThrowIfNull(step);

        return key switch
        {
            "filter" => step.Filter is null ? [] : [step.Filter],
            "exclude" => step.Exclude,
            "labels" => step.Labels,
            "inputs" => [.. step.Inputs.Values],
            "base" => step.Base is null ? [] : [step.Base],
            "branch" => step.Branch is null ? [] : [step.Branch],
            "commit" => step.Commit is null ? [] : [step.Commit],
            "message" => step.Message is null ? [] : [step.Message],
            "variables" => [.. step.Variables.Values],
            "title" => step.Title is null ? [] : [step.Title],
            "question" => step.Question is null ? [] : [step.Question],
            _ => throw new UnreachableException($"'{key}' is marked a template key of '{step.Type}', and nothing reads it."),
        };
    }

    /// <summary>
    /// <paramref name="step"/> with every template key of its type filled by <paramref name="fill"/>, but a pause's
    /// <c>question</c>. A test's <c>filter</c>, and a member of its <c>exclude</c> or <c>labels</c>, that fills to an
    /// empty text is not given: no filter is passed, and so every test of the step's legs runs (part 04, "Templates in
    /// step keys"). <see langword="null"/> where one cannot be filled, naming the key in <paramref name="key"/> and why
    /// in <paramref name="problem"/>.
    /// </summary>
    /// <param name="step">The step, as it starts.</param>
    /// <param name="fill">Fills one text.</param>
    /// <param name="key">The key that could not be filled; null where every one was.</param>
    /// <param name="problem">Why; null where every one was filled.</param>
    public static WorkflowStep? Fill(WorkflowStep step, FlowTemplateFiller fill, out string? key, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(fill);

        key = null;
        problem = null;

        var filled = step;

        foreach (var templateKey in (step.Type is null ? null : WorkflowStepTypes.Find(step.Type))?.TemplateKeys ?? [])
        {
            if (templateKey == "question")
            {
                continue;
            }

            var texts = new List<string>();

            foreach (var text in Texts(step, templateKey))
            {
                if (fill(text, out var unreadable) is not { } value)
                {
                    key = templateKey;
                    problem = unreadable;
                    return null;
                }

                texts.Add(value);
            }

            filled = templateKey switch
            {
                "filter" => filled with { Filter = texts is [{ Length: > 0 } given] ? given : null },
                "exclude" => filled with { Exclude = [.. texts.Where(text => text.Length > 0)] },
                "labels" => filled with { Labels = [.. texts.Where(text => text.Length > 0)] },
                "inputs" => filled with { Inputs = Mapped(step.Inputs, texts) },
                "base" => filled with { Base = texts.SingleOrDefault() },
                "branch" => filled with { Branch = texts.SingleOrDefault() },
                "commit" => filled with { Commit = texts.SingleOrDefault() },
                "message" => filled with { Message = texts.SingleOrDefault() },
                "variables" => filled with { Variables = Mapped(step.Variables, texts) },
                "title" => filled with { Title = texts.SingleOrDefault() },
                _ => throw new UnreachableException($"'{templateKey}' is marked a template key of '{step.Type}', and nothing fills it."),
            };
        }

        return filled;
    }

    /// <summary>A map's keys, each with its value filled, in the order the map gives them.</summary>
    private static Dictionary<string, string> Mapped(IReadOnlyDictionary<string, string> map, List<string> filled)
    {
        var mapped = new Dictionary<string, string>(map.Count, StringComparer.Ordinal);
        var at = 0;

        foreach (var name in map.Keys)
        {
            mapped[name] = filled[at++];
        }

        return mapped;
    }
}
