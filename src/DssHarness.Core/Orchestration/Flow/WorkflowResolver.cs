using System.Security.Cryptography;
using System.Text;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The one owner of what a step that says <c>use:</c> is: it resolves every such step of a parsed definition into the
/// workflow it names - the called workflow's file read and parsed, its steps placed in the step's own, each marked with
/// the workflow its files are read under - so that the definition the engine runs, freezes and digests holds the whole
/// resolved tree and nothing else learns what a <c>use:</c> is.
/// </summary>
/// <remarks>
/// <para>
/// A used workflow is read as a definition of its own and embedded whole: its steps become the step's members, its
/// recovery is kept beside them for the engine to reach first, its declared outputs become the step's own once it ends,
/// and its inputs are the names the step's <c>with:</c> values fill. Its run-level keys - its budget, limits, advisor,
/// amend and answers - are never applied, and are said as not applied, for the calling run's own are what hold.
/// </para>
/// <para>
/// V58, the cycle rule: a workflow that uses itself, directly or through others, is refused with the whole chain of the
/// uses that close the circle. V59, the depth rule: nesting counts across workflows as it counts within one, the
/// reader's own bound of <see cref="WorkflowFileParser.MaxDepth"/> levels in all.
/// </para>
/// </remarks>
public static class WorkflowResolver
{
    /// <summary>
    /// Resolves every <c>use:</c> step of <paramref name="definition"/>: the definition it returns holds the called
    /// workflows' steps in the places the engine runs them, and no <see cref="WorkflowStep.UseSource"/> of its own. A
    /// step it cannot resolve is a problem, said with the step's path; a definition with problems is returned beside
    /// them, its use steps left as written.
    /// </summary>
    /// <param name="definition">The workflow as it was parsed, its use steps still paths.</param>
    /// <param name="directory">
    /// The calling workflow's own directory, relative to the repository's workflows root and written with forward
    /// slashes - empty where the workflow sits in the root itself.
    /// </param>
    /// <param name="readFile">
    /// Reads a workflow file by its path under the repository's workflows root, forward slashes, <c>a/b/b.yml</c> -
    /// null where no file is there.
    /// </param>
    public static (WorkflowDefinition Definition, IReadOnlyList<string> Problems) Resolve(
        WorkflowDefinition definition,
        string directory,
        Func<string, string?> readFile)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(readFile);

        var problems = new List<string>();
        var resolved = ResolveSteps(definition.Steps, directory, [], depth: 1, readFile, problems);
        var root = definition with
        {
            Steps = [.. resolved],
            Recover = definition.Recover is null ? null : definition.Recover with
            {
                Steps = [.. ResolveSteps(definition.Recover.Steps, directory, [], depth: 1, readFile, problems)],
            },
        };

        foreach (var input in definition.Inputs.Where(input => input.Kind is FlowInput.ValueKind or FlowInput.StepKind))
        {
            problems.Add($"input '{input.Name}' is of the kind '{input.Kind}', which a run is never started with: a workflow's own inputs are '{FlowInput.TextKind}' or '{FlowInput.FileKind}', and '{FlowInput.ValueKind}' and '{FlowInput.StepKind}' are a used workflow's, filled by the with of the step that uses it");
        }

        return (root, problems);
    }

    /// <summary>
    /// Resolves the use steps among <paramref name="steps"/>, leaving every other step as it is but for the workflow
    /// mark a used workflow's members carry; a group's own steps are walked whatever the group is, so a use nested in a
    /// group of the caller's resolves as a top-level one does.
    /// </summary>
    /// <param name="steps">The steps as parsed.</param>
    /// <param name="directory">The directory the steps' workflow sits in, relative to the workflows root.</param>
    /// <param name="chain">The workflows already entered, outermost first, to refuse a use that closes a circle.</param>
    /// <param name="depth">How many workflows deep this level sits, the calling workflow's own file the first.</param>
    /// <param name="readFile">Reads a workflow file by its path under the workflows root; null where no file is there.</param>
    /// <param name="problems">Every problem found, whole, in the order the tree declares what they concern.</param>
    private static IEnumerable<WorkflowStep> ResolveSteps(
        IReadOnlyList<WorkflowStep> steps,
        string directory,
        IReadOnlyList<string> chain,
        int depth,
        Func<string, string?> readFile,
        List<string> problems)
    {
        foreach (var step in steps)
        {
            if (step.UseSource is not null)
            {
                yield return ResolveUse(step, directory, chain, depth, readFile, problems) ?? step;
                continue;
            }

            yield return step.Steps.Count == 0 ? step : step with { Steps = [.. ResolveSteps(step.Steps, directory, chain, depth, readFile, problems)] };
        }
    }

    /// <summary>Resolves one use step: its workflow read, parsed, resolved in its turn and placed in the step.</summary>
    private static WorkflowStep? ResolveUse(
        WorkflowStep step,
        string directory,
        IReadOnlyList<string> chain,
        int depth,
        Func<string, string?> readFile,
        List<string> problems)
    {
        var where = step.Name;

        if (ResolvedPath(directory, step.UseSource!) is not { } path)
        {
            problems.Add($"step '{where}' uses '{step.UseSource}', which is no path to a workflow file: a use is a path relative to the calling workflow's directory, written with forward slashes, that resolves inside the repository's workflows directory");
            return null;
        }

        if (!path.EndsWith(HarnessLayout.WorkflowFileExtension, StringComparison.Ordinal))
        {
            problems.Add($"step '{where}' uses '{step.UseSource}', which does not name a '{HarnessLayout.WorkflowFileExtension}' file");
            return null;
        }

        var name = path[..path.LastIndexOf('/')].Replace('\\', '/');

        if (chain.Contains(name, StringComparer.Ordinal))
        {
            // V58, said with the whole chain: each workflow by its name, the first said twice to close the circle.
            problems.Add($"step '{where}' uses '{step.UseSource}', the workflow '{name}', which is already in use around it: {string.Join(" -> ", [.. chain, name, chain[0]])}. A workflow that uses itself, directly or through others, would run its own steps without end");
            return null;
        }

        if (chain.Count + 1 >= WorkflowFileParser.MaxDepth)
        {
            // V59: nesting counts across workflows, the reader's own bound, so that no definition reaches the engine
            // deeper than its reader takes.
            problems.Add($"step '{where}' uses '{step.UseSource}' {chain.Count + 2} workflows deep, counting the calling one's own; workflows nest at most {WorkflowFileParser.MaxDepth} deep in all, within one file and across the uses of others");
            return null;
        }

        if (readFile(path) is not { } text)
        {
            problems.Add($"step '{where}' uses '{step.UseSource}', and no workflow file is at '{path}' under the repository's workflows directory");
            return null;
        }

        WorkflowDefinition called;

        try
        {
            called = WorkflowFileParser.Parse(path, text);
        }
        catch (HarnessException refused)
        {
            problems.Add($"step '{where}' uses '{step.UseSource}', whose file does not read: {refused.Message}");
            return null;
        }

        var inner = new List<string>(chain) { name };
        var calledSteps = ResolveSteps(called.Steps, name, inner, depth + 1, readFile, problems)
            .Select(member => member.WorkflowOf is null ? member with { WorkflowOf = name } : member)
            .ToList();
        var calledRecover = called.Recover is null ? null : called.Recover with
        {
            Steps = [.. ResolveSteps(called.Recover.Steps, name, inner, depth + 1, readFile, problems)
                .Select(member => member.WorkflowOf is null ? member with { WorkflowOf = name } : member)],
        };

        foreach (var parameter in called.Params.Where(parameter => parameter.Required && parameter.Default is null))
        {
            problems.Add($"step '{where}' uses the workflow '{name}', whose param '{parameter.Name}' is required and declares no default: a used workflow is given nothing but its inputs, so it holds the param's own value or none");
        }

        var notApplied = new List<string>();

        if (called.Budget is not null)
        {
            notApplied.Add("budget");
        }

        if (called.Advisor is not null)
        {
            notApplied.Add("advisor");
        }

        if (called.Reporter is not null)
        {
            notApplied.Add("reporter");
        }

        if (called.Amend is not null)
        {
            notApplied.Add("amend");
        }

        if (called.Answers.Count > 0)
        {
            notApplied.Add("answers");
        }

        if (new[] { called.MaxReasoningAgents, called.MaxParallelProcesses, called.MaxOpenAgents, called.MaxRetries, called.MaxInstances, called.HandoffSeconds, called.BackoffSeconds }.Any(limit => limit is not null))
        {
            notApplied.Add("limits");
        }

        if (called.CodebaseGraph is not null)
        {
            notApplied.Add("codebaseGraph");
        }

        var use = new FlowUse(
            step.UseSource!,
            name,
            FingerprintOf(text),
            called.Inputs,
            called.Params,
            called.Outputs,
            calledRecover,
            notApplied);

        return step with
        {
            UseSource = null,
            Use = use,
            Steps = calledSteps,
        };
    }

    /// <summary>
    /// The path a use names, under the repository's workflows root and written with forward slashes: the caller's
    /// directory joined to the source, <c>.</c> and <c>..</c> resolved, and null where the path leaves the root or is
    /// no relative path at all.
    /// </summary>
    private static string? ResolvedPath(string directory, string source)
    {
        if (source.Length == 0 || source.Contains(':') || source.StartsWith('/') || source.StartsWith('\\'))
        {
            return null;
        }

        var parts = new List<string>();

        foreach (var part in (directory.Length == 0 ? source : $"{directory}/{source}").Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part)
            {
                case ".":
                    break;
                case "..":
                    if (parts.Count == 0)
                    {
                        return null;
                    }

                    parts.RemoveAt(parts.Count - 1);
                    break;
                default:
                    parts.Add(part);
                    break;
            }
        }

        return parts.Count == 0 || parts.Any(part => part is "." or "..") ? null : string.Join("/", parts);
    }

    /// <summary>The SHA-256 of a workflow file's text, as the run records the resolution of the workflow it uses.</summary>
    private static string FingerprintOf(string text)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
