using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// Checks the workflows a repository declares: that each definition file reads, holds, and names
/// files that are there - the one command that reads a workflow's whole definition and its files
/// together.
/// </summary>
/// <remarks>
/// The pure rules live in <see cref="WorkflowValidator"/>, the file's shape in
/// <see cref="WorkflowFileParser"/>; this adds what only the repository can answer: that the file
/// is where the configuration says it is, that the files the definition names are in the workflow's
/// directory, and that the legs and runners it names are declared.
/// </remarks>
public interface IWorkflowValidationService
{
    /// <summary>
    /// Validates the declared workflow <paramref name="named"/>, or every one the configuration
    /// declares where it names none.
    /// </summary>
    /// <param name="startDirectory">A directory inside the repository.</param>
    /// <param name="named">A workflow's name, as <c>workflows</c> declares it; <see langword="null"/> for every one.</param>
    /// <param name="cancellationToken">Stops the check.</param>
    /// <exception cref="HarnessException">
    /// The configuration does not load, or the tree cannot be resolved. A workflow with problems is
    /// reported, not thrown.
    /// </exception>
    Task<WorkflowValidationReport> CheckAsync(string startDirectory, string? named, CancellationToken cancellationToken = default);
}

/// <summary>What checking a repository's workflows found, one line per workflow.</summary>
/// <param name="Root">The tree whose workflows were checked.</param>
/// <param name="Lines">One line per workflow checked: what it is, or its problems.</param>
public sealed record WorkflowValidationReport(string Root, IReadOnlyList<string> Lines)
{
    /// <summary>Whether every workflow checked holds.</summary>
    public bool Passed => Lines.Count > 0 && Lines.All(Holds);

    /// <summary>Whether one line says its workflow held.</summary>
    public static bool Holds(string line) => line.StartsWith("ok ", StringComparison.Ordinal);
}

/// <inheritdoc cref="IWorkflowValidationService"/>
public sealed class WorkflowValidationService(
    IHarnessContextLoader contextLoader,
    IFileSystem fileSystem,
    IHostPlatform platform) : IWorkflowValidationService
{
    /// <summary>The command this service reports under.</summary>
    public const string CommandName = "validate-workflow";

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHostPlatform _platform = platform;

    /// <inheritdoc/>
    public async Task<WorkflowValidationReport> CheckAsync(
        string startDirectory,
        string? named,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);

        var context = await _contextLoader.LoadAsync(startDirectory, cancellationToken).ConfigureAwait(false);
        var config = context.Config;

        if (named is not null && !(config.Workflows ?? []).Contains(named, StringComparer.Ordinal))
        {
            throw new HarnessException(
                HarnessExit.UsageError,
                DeclaredName.NotDeclared(named, "workflows", config.Workflows ?? [])
                    + $" A workflow is defined by '{WorkflowName.Expected(named)}'.");
        }

        var lines = (named is null ? config.Workflows ?? [] : [named])
            .SelectMany(name => Check(context, name))
            .ToList();

        return new WorkflowValidationReport(context.Layout.RepositoryRoot, lines);
    }

    private IEnumerable<string> Check(HarnessContext context, string name)
    {
        var file = context.Layout.WorkflowFile(name);

        if (!_fileSystem.FileExists(file))
        {
            yield return $"{Shown(name)} is not there; a workflow is defined by that file, written by 'dssharness init' where it is the default one";
            yield break;
        }

        // The parser's refusal already names the file and every problem it found in it.
        var refusal = TryParse(file, out var definition);

        if (refusal is not null)
        {
            yield return refusal;
            yield break;
        }

        if (!string.Equals(definition!.Name, name, StringComparison.Ordinal))
        {
            yield return $"{Shown(name)} names itself '{definition.Name}'; a workflow's name is its directory's and its file's, said again in the file so a copied directory cannot pass as the workflow it was copied from";
            yield break;
        }

        var problems = new List<string>(WorkflowValidator.Validate(definition, context.Config.Commit.Template is not null));
        problems.AddRange(Files(definition, context.Layout.WorkflowDirectory(name), name, context.Config));

        if (problems.Count > 0)
        {
            foreach (var problem in problems)
            {
                yield return $"{Shown(name)}: {problem}";
            }

            yield break;
        }

        var steps = Count(definition.Steps);
        var reasons = Count(definition.Steps, "reason");
        yield return $"ok {Shown(name)} ({steps} step{(steps == 1 ? string.Empty : "s")}, {reasons} reasoning)";
    }

    /// <summary>Reads and parses the definition at <paramref name="file"/>, answering its refusal instead of throwing it.</summary>
    private string? TryParse(string file, out WorkflowDefinition? definition)
    {
        try
        {
            definition = WorkflowFileParser.Parse(file, _fileSystem.ReadAllText(file));
            return null;
        }
        catch (HarnessException exception)
        {
            definition = null;
            return exception.Message;
        }
    }

    /// <summary>
    /// What only the repository can answer: that the files the definition names are in the
    /// workflow's own directory, and that the legs and runners it names are declared.
    /// </summary>
    private IReadOnlyList<string> Files(WorkflowDefinition definition, string directory, string name, HarnessConfig config)
    {
        var problems = new List<string>();

        if (definition.Recommendations is not null)
        {
            Add(problems, NamedFile(definition.Recommendations, directory, name, "the bar for recommendations"));
        }

        if (definition.Advisor is { } advisor)
        {
            problems.AddRange(AgentAndPrompt(advisor.Agent, advisor.Prompt, directory, name, "the advisor"));
        }

        if (definition.Reporter is { } reporter)
        {
            problems.AddRange(AgentAndPrompt(reporter.Agent, reporter.StatusPrompt, directory, name, "the reporter's status prompt"));
            problems.AddRange(AgentAndPrompt(reporter.Agent, reporter.AskPrompt, directory, name, "the reporter's ask prompt"));
        }

        foreach (var (path, step) in Walk(definition.Steps))
        {
            var where = $"step '{path}'";

            if (step.Agent is not null)
            {
                Add(problems, NamedFile($"{HarnessLayout.WorkflowAgentsDirectoryName}/{step.Agent}.md", directory, name, $"{where}'s agent"));
            }

            if (step.Prompt is not null)
            {
                Add(problems, NamedFile(step.Prompt, directory, name, $"{where}'s prompt"));
            }

            if (step.ResumePrompt is not null)
            {
                Add(problems, NamedFile(step.ResumePrompt, directory, name, $"{where}'s resume prompt"));
            }

            if (step.Outputs is not null)
            {
                problems.AddRange(Schema(step.Outputs, directory, name, $"{where}'s outputs"));
            }

            if (step.Body is not null)
            {
                Add(problems, NamedFile(step.Body, directory, name, $"{where}'s body"));
            }

            if (step.Runner is not null && !config.PredefinedRunners.ContainsKey(step.Runner))
            {
                problems.Add($"{where} runs '{step.Runner}', which " + DeclaredName.NotDeclared(step.Runner, "predefinedRunners", config.PredefinedRunners.Keys));
            }

            if (step.Runner is not null
                && step.In is null
                && config.PredefinedRunners.TryGetValue(step.Runner, out var runner)
                && runner.WritesTree)
            {
                problems.Add($"{where} runs '{step.Runner}', which writes the tree, and names no 'in'; a runner that changes files works in an agent's worktree, never in the main tree");
            }

            foreach (var leg in step.Legs.Where(leg => !config.Legs.ContainsKey(leg)))
            {
                problems.Add($"{where} names the leg '{leg}', which " + DeclaredName.NotDeclared(leg, "legs", config.Legs.Keys));
            }
        }

        return problems;
    }

    private static void Add(List<string> problems, string? problem)
    {
        if (problem is not null)
        {
            problems.Add(problem);
        }
    }

    private IReadOnlyList<string> AgentAndPrompt(string agent, string prompt, string directory, string name, string what)
    {
        var problems = new List<string>();
        Add(problems, NamedFile($"{HarnessLayout.WorkflowAgentsDirectoryName}/{agent}.md", directory, name, $"the agent {what} runs"));
        Add(problems, NamedFile(prompt, directory, name, $"{what}'s prompt"));
        return problems;
    }

    /// <summary>
    /// The one problem with one file the definition names: that it stays inside the workflow's own
    /// directory, and that it is there. What a reviewer reads there is what the workflow runs, as
    /// an action's files are to its own directory.
    /// </summary>
    private string? NamedFile(string named, string directory, string name, string what)
    {
        if (Path.IsPathRooted(named) || named.Split('/', '\\').Any(segment => segment is ".." or "."))
        {
            return $"'{named}', {what}, leaves the workflow's directory; every file a workflow names is relative to '{HarnessLayout.WorkflowsDirectoryRelative}/{name}/', as an action's are to its own directory";
        }

        string root;
        string path;

        try
        {
            // The directory is resolved the same way as the file, so a repository reached through a
            // link of its own compares against the same real path, as an action's directory is.
            root = Path.TrimEndingDirectorySeparator(_fileSystem.ResolveLinks(Path.GetFullPath(directory)));
            path = _fileSystem.ResolveLinks(Path.GetFullPath(Path.Combine(directory, named)));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return $"'{named}', {what}, could not be resolved inside '{directory}': {exception.Message}";
        }

        if (!PathContainment.IsStrictlyInside(root, path, _platform.PathComparison))
        {
            return $"'{named}', {what}, leads to '{path}', which is outside '{root}'; every file a workflow names is relative to '{HarnessLayout.WorkflowsDirectoryRelative}/{name}/', as an action's are to its own directory";
        }

        return _fileSystem.FileExists(path)
            ? null
            : $"'{named}', {what}, is not at '{HarnessLayout.WorkflowsDirectoryRelative}/{name}/{named.Replace('\\', '/')}'; write it there, beside the definition that names it";
    }

    /// <summary>That a schema file is there, and holds a JSON object.</summary>
    private IReadOnlyList<string> Schema(string named, string directory, string name, string what)
    {
        if (NamedFile(named, directory, name, what) is { } problem)
        {
            return [problem];
        }

        try
        {
            using var document = JsonDocument.Parse(_fileSystem.ReadAllText(Path.Combine(directory, named)));

            return document.RootElement.ValueKind == JsonValueKind.Object
                ? []
                : [$"'{named}', {what}, holds no JSON object; a schema is a mapping of properties"];
        }
        catch (JsonException exception)
        {
            return [$"'{named}', {what}, is not valid JSON ({exception.Message})"];
        }
    }

    private static string Shown(string name)
        => $"{HarnessLayout.WorkflowsDirectoryRelative}/{name}/{name}{HarnessLayout.WorkflowFileExtension}";

    private static IEnumerable<(string Path, WorkflowStep Step)> Walk(IReadOnlyList<WorkflowStep> steps, string? parent = null)
    {
        foreach (var step in steps)
        {
            var path = parent is null ? step.Name : $"{parent}.{step.Name}";

            yield return (path, step);

            foreach (var nested in Walk(step.Steps, path))
            {
                yield return nested;
            }
        }
    }

    private static int Count(IReadOnlyList<WorkflowStep> steps, string? type = null)
        => steps.Count(step => type is null || step.Type == type)
            + steps.Where(step => step.Steps.Count > 0).Sum(step => Count(step.Steps, type));
}

/// <summary>Turns a workflow validation report into what validate-workflow prints.</summary>
public static class WorkflowValidationReports
{
    /// <summary>What validate-workflow reports.</summary>
    /// <param name="report">What was checked.</param>
    /// <param name="json">Whether to write the result as JSON instead of as lines.</param>
    public static CommandOutcome Render(WorkflowValidationReport report, bool json)
    {
        ArgumentNullException.ThrowIfNull(report);

        var data = json ? [Document(report).ToJsonString(ReportJson.Options)] : Array.Empty<string>();

        if (report.Passed)
        {
            return CommandOutcome.Ok($"{ReportText.Counted(report.Lines.Count, "workflow")} checked, all valid") with
            {
                Data = data,
                Quiet = json,
                Details = json ? null : report.Lines,
            };
        }

        return CommandOutcome.Refused(
            $"{ReportText.Counted(report.Lines.Count(WorkflowValidationReport.Holds), "workflow")} valid, "
            + $"{report.Lines.Count(line => !WorkflowValidationReport.Holds(line))} with problems. "
            + "Nothing was run.") with
        {
            Data = data,
            Details = json ? null : report.Lines,
        };
    }

    /// <summary>
    /// The document validate-workflow answers with where it stops before it reads any definition - a
    /// <c>--directory</c> that is not there, a configuration that does not load, a workflow nobody
    /// declared: its own, no workflows and no problems, after the code it exits with and the line it
    /// ends on.
    /// </summary>
    /// <param name="exitCode">The code the command exits with.</param>
    /// <param name="summary">The line it ends on.</param>
    public static string Stopped(int exitCode, string summary)
        => ReportJson.Stopped(exitCode, summary, Document(report: null));

    private static JsonObject Document(WorkflowValidationReport? report)
        => new()
        {
            ["workflows"] = report is null
                ? null
                : new JsonArray([.. report.Lines.Select(line => (JsonNode)line!)]),
            ["passed"] = report?.Passed,
        };
}
