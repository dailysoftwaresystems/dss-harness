using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
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
/// is where the configuration says it is, that the workflow's directory, its definition and the
/// files the definition names are the repository's own, inside the workflow's directory, and that
/// what the definition asks of the configuration holds: the legs and runners it names are
/// declared, a runner that writes the tree works in an agent's worktree, a commit step fills the
/// commit template, and the agents a run makes are named within the worktrees' bound.
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
    /// <paramref name="named"/> is a workflow the configuration does not declare
    /// (<see cref="HarnessExit.UsageError"/>); or the configuration does not load, or the tree cannot
    /// be resolved. A workflow with problems is reported, not thrown, and so is a configuration that
    /// declares none.
    /// </exception>
    Task<WorkflowValidationReport> CheckAsync(string startDirectory, string? named, CancellationToken cancellationToken = default);
}

/// <summary>What checking one declared workflow found.</summary>
/// <param name="Name">The workflow, as <c>workflows</c> declares it.</param>
/// <param name="Lines">One line saying what it is, where it holds, and one line for each of its agents' models and levels; one line for each problem, where it does not.</param>
public sealed record WorkflowCheck(string Name, IReadOnlyList<string> Lines)
{
    /// <summary>Whether it holds: its first line says so, whatever its agents' models and levels add under it.</summary>
    public bool Holds => Lines is [var line, ..] && WorkflowValidationReport.Holds(line);

    /// <summary>
    /// The definition that held, as it was read and checked; <see langword="null"/> where it does not hold. What a command
    /// that goes on from a check reads, so that what it acts on is what was checked, never the file read a second time.
    /// </summary>
    public WorkflowDefinition? Definition { get; init; }

    /// <summary>
    /// The workflow's directory, every link along it followed, which every file the definition names was found inside;
    /// <see langword="null"/> where it does not hold.
    /// </summary>
    public string? Directory { get; init; }
}

/// <summary>What checking a repository's workflows found, workflow by workflow.</summary>
/// <param name="Root">The tree whose workflows were checked.</param>
/// <param name="Workflows">Each workflow checked, in the order it was asked for; none where the configuration declares none.</param>
public sealed record WorkflowValidationReport(string Root, IReadOnlyList<WorkflowCheck> Workflows)
{
    /// <summary>Every line of every workflow checked, in order: one for a workflow that holds, one per problem of one that does not.</summary>
    public IReadOnlyList<string> Lines => [.. Workflows.SelectMany(workflow => workflow.Lines)];

    /// <summary>Whether a workflow was checked and every one holds: none checked is no pass.</summary>
    public bool Passed => Workflows.Count > 0 && Workflows.All(workflow => workflow.Holds);

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

        var workflows = (named is null ? config.Workflows ?? [] : [named])
            .Select(name => Check(context, name))
            .ToList();

        return new WorkflowValidationReport(context.Layout.RepositoryRoot, workflows);
    }

    private WorkflowCheck Check(HarnessContext context, string name)
    {
        var file = context.Layout.WorkflowFile(name);

        if (!IsFile(file, out var unlooked))
        {
            return new WorkflowCheck(name, [unlooked is null
                ? $"{Shown(name)} is not there; a workflow is defined by that file, written by 'dssharness init' where it is the implementation one"
                : $"{Shown(name)} could not be looked at: {unlooked}"]);
        }

        // Where the workflow's directory and its definition lead, every link along them followed, before a word of it is
        // read: the files a definition names are held to that directory, so it must itself be the repository's own.
        if (Contained(context.Layout, name, out var directory) is { } outside)
        {
            return new WorkflowCheck(name, [outside]);
        }

        // The parser's refusal already names the file and every problem it found in it.
        var refusal = TryParse(name, file, out var definition);

        if (refusal is not null)
        {
            return new WorkflowCheck(name, [refusal]);
        }

        if (!string.Equals(definition!.Name, name, StringComparison.Ordinal))
        {
            return new WorkflowCheck(name, [$"{Shown(name)} names itself '{definition.Name}'; a workflow's name is its directory's and its file's, said again in the file so a copied directory cannot pass as the workflow it was copied from"]);
        }

        // The configuration's providers, resolved once: the same resolution checks the definition and says each
        // agent's models and levels where it holds.
        var resolved = DssHarness.Core.Agents.AgentProviders.Resolve(context.Config);

        var problems = new List<string>(WorkflowValidator.Validate(
            definition,
            context.Config.Commit.Template is not null,
            named => Text(named, directory, name),
            resolved.Providers));
        problems.AddRange(Files(definition, directory, name, context.Config));

        if (problems.Count > 0)
        {
            return new WorkflowCheck(name, [.. problems.Select(problem => $"{Shown(name)}: {problem}")]);
        }

        // The recovery is counted apart: its steps run only where something failed. Under the one line that says the
        // definition holds, each agent's models and the level each runs at, resolved the same check resolved them.
        var steps = Count(definition.Steps);
        var recovering = definition.Recover is { } recover ? Count(recover.Steps) : 0;
        var reasons = Count([.. definition.Steps, .. definition.Recover?.Steps ?? []], "reason");
        var recovery = definition.Recover is null ? string.Empty : $", {recovering} recovering";
        return new WorkflowCheck(
            name,
            [
                $"ok {Shown(name)} ({steps} step{(steps == 1 ? string.Empty : "s")}{recovery}, {reasons} reasoning)",
                .. WorkflowValidator.ModelLevels(definition, resolved.Providers!),
            ])
        {
            Definition = definition,
            Directory = directory,
        };
    }

    /// <summary>
    /// Why the workflow <paramref name="name"/> is not the repository's own, or <see langword="null"/> where it is: its
    /// directory strictly inside the workflows directory, and its definition strictly inside its directory, each once every
    /// link along it is followed - so that what a reviewer reads there is what the workflow runs, as an action's file is
    /// held to the actions directory (<see cref="Runners.ActionPath.Resolve"/>).
    /// </summary>
    /// <param name="layout">The tree's layout.</param>
    /// <param name="name">The workflow.</param>
    /// <param name="directory">The workflow's directory with every link along it followed, which the files it names are held to.</param>
    private string? Contained(HarnessLayout layout, string name, out string directory)
    {
        string workflows;
        string file;

        try
        {
            // The workflows directory is resolved the same way as the rest, so a repository reached through a link of its
            // own compares against the same real path, as the actions directory is.
            workflows = Path.TrimEndingDirectorySeparator(_fileSystem.ResolveLinks(Path.GetFullPath(layout.WorkflowsDirectory)));
            directory = Path.TrimEndingDirectorySeparator(_fileSystem.ResolveLinks(Path.GetFullPath(layout.WorkflowDirectory(name))));
            file = _fileSystem.ResolveLinks(Path.GetFullPath(layout.WorkflowFile(name)));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            directory = string.Empty;
            return $"{Shown(name)} could not be resolved inside '{layout.WorkflowsDirectory}': {exception.Message}";
        }

        if (!PathContainment.IsStrictlyInside(workflows, directory, _platform.PathComparison))
        {
            return $"{Shown(name)}: its directory leads to '{directory}', which is outside '{workflows}'; a workflow's directory is the repository's own, so that what a reviewer reads there is what the workflow runs";
        }

        return PathContainment.IsStrictlyInside(directory, file, _platform.PathComparison)
            ? null
            : $"{Shown(name)} leads to '{file}', which is outside '{directory}'; a workflow's definition is a file of its own directory, so that what a reviewer reads there is what the workflow runs";
    }

    /// <summary>
    /// Reads and parses the definition at <paramref name="file"/>, answering why it could not be instead of throwing: the
    /// parser's refusal, which names the file and every problem in it, or the disk's, naming the file - one workflow that
    /// cannot be read is that workflow's problem, never the end of every other's check.
    /// </summary>
    private string? TryParse(string name, string file, out WorkflowDefinition? definition)
    {
        definition = null;
        string text;

        try
        {
            text = _fileSystem.ReadAllText(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"{Shown(name)} could not be read: {exception.Message}";
        }

        try
        {
            definition = WorkflowFileParser.Parse(file, text);
            return null;
        }
        catch (HarnessException exception)
        {
            return exception.Message;
        }
    }

    /// <summary>
    /// What only the repository can answer: that the files the definition names are in the
    /// workflow's own directory, that the legs and runners it names are declared, that a runner
    /// that writes the tree works in an agent's worktree, that its commit steps fill the
    /// configuration's commit template, and that the agents it makes are named within
    /// <c>worktrees.maxNameLength</c>.
    /// </summary>
    private IReadOnlyList<string> Files(WorkflowDefinition definition, string directory, string name, HarnessConfig config)
    {
        var problems = new List<string>();

        foreach (var named in WorkflowFiles.OfDefinition(definition))
        {
            problems.AddRange(Check(named, directory, name));
        }

        // The recovery's steps name files as the flow's do, and are checked the same way, under its own path.
        foreach (var (path, step) in WorkflowFiles.Steps(definition))
        {
            var where = $"step '{path}'";

            foreach (var named in WorkflowFiles.OfStep(step, where))
            {
                problems.AddRange(Check(named, directory, name));
            }

            if (step.Runner is not null && !config.PredefinedRunners.ContainsKey(step.Runner))
            {
                problems.Add($"{where} runs '{step.Runner}': " + DeclaredName.NotDeclared(step.Runner, "predefinedRunners", config.PredefinedRunners.Keys));
            }

            problems.AddRange(WritesTheMainTree(step, where, config));
            problems.AddRange(CommitVariables(step, where, config.Commit));

            // A leg set is named where a leg is, as --legs takes one.
            foreach (var leg in step.Legs.Where(leg => !config.Legs.ContainsKey(leg) && !config.LegSets.ContainsKey(leg)))
            {
                problems.Add(
                    $"{where} names '{leg}', which is neither a leg nor a leg set: "
                        + DeclaredName.NotDeclared(leg, "legs or legSets", [.. config.Legs.Keys, .. config.LegSets.Keys]));
            }
        }

        Add(problems, GeneratedAgentNames(definition, config.Worktrees));
        return problems;
    }

    /// <summary>
    /// V24: a run step whose runner writes the tree (<c>writesTree</c>) - the step's own, or one its run checks name,
    /// which runs within the step's legs - works in an agent's worktree, named with <c>in</c>, and never on the main
    /// tree: the run reads its definition there and folds into it, so what such a step wrote under the other steps would
    /// change what they read. A runner nobody declared is refused beside this, by name.
    /// </summary>
    private static IEnumerable<string> WritesTheMainTree(WorkflowStep step, string where, HarnessConfig config)
    {
        if (step.Runner is null || step.In is not null || !config.PredefinedRunners.TryGetValue(step.Runner, out var runner))
        {
            yield break;
        }

        const string OnTheMainTree = "on the main tree: the run reads its definition there and folds into it, so a runner "
            + "that writes works in an agent's worktree. Name the create-agent step with 'in'";

        if (runner.WritesTree)
        {
            yield return $"{where} runs '{step.Runner}', which writes the tree (writesTree), {OnTheMainTree}";
        }

        foreach (var check in runner.RunCheckRunners())
        {
            if (config.PredefinedRunners.TryGetValue(check, out var checkRunner) && checkRunner.WritesTree)
            {
                yield return $"{where} runs '{step.Runner}', whose run check '{check}' writes the tree (writesTree), {OnTheMainTree}";
            }
        }
    }

    /// <summary>
    /// V30, where config.json declares a <c>commit.template</c>: a commit step gives a value for every variable the
    /// template requires, and none for a variable <c>commit.variables</c> does not declare, which nothing would fill in.
    /// A name is matched as the configuration matches the template's placeholders, whatever its case. Where no template
    /// is declared the validator holds the step to its message and no variables, from what the configuration says.
    /// </summary>
    private static IEnumerable<string> CommitVariables(WorkflowStep step, string where, CommitConfig commit)
    {
        if (step.Type != "commit" || commit.Template is null)
        {
            yield break;
        }

        foreach (var (name, _) in commit.Variables.Where(variable => variable.Value.Required))
        {
            if (DeclaredName.In(step.Variables.Keys, name) is null)
            {
                yield return $"{where} is a commit step and gives no value for '{name}', which commit.template requires: add it under the step's variables";
            }
        }

        foreach (var given in step.Variables.Keys.Where(given => DeclaredName.In(commit.Variables.Keys, given) is null))
        {
            yield return $"{where} is a commit step and gives a value for '{given}', which commit.variables does not declare: the template is filled with the variables it declares, and this one would be read by nothing";
        }
    }

    /// <summary>
    /// V27: the names a run gives the agents it makes fit <c>worktrees.maxNameLength</c>, as every agent's name must when
    /// it is made - the letter and a number that grows to the workflow's most step instances, one agent made by each
    /// at the most. A workflow none of whose steps makes an agent names none.
    /// </summary>
    private static string? GeneratedAgentNames(WorkflowDefinition definition, WorktreeSettings worktrees)
    {
        if (!WorkflowFiles.Steps(definition).Any(entry => WorkflowStepTypes.MakesAgent(entry.Step)))
        {
            return null;
        }

        var longest = OrchestrationRules.LongestGeneratedAgentName(definition.MaxInstances ?? WorkflowDefinition.DefaultMaxInstances);
        var digits = longest - OrchestrationRules.GeneratedAgentPrefix.Length;

        return longest > worktrees.MaxNameLength
            ? $"the workflow creates agents, whose generated names ('{OrchestrationRules.GeneratedAgentPrefix}' and up to {digits} digit{(digits == 1 ? string.Empty : "s")}) "
                + $"are {longest} characters, and worktrees.maxNameLength is {worktrees.MaxNameLength}"
            : null;
    }

    private static void Add(List<string> problems, string? problem)
    {
        if (problem is not null)
        {
            problems.Add(problem);
        }
    }

    /// <summary>The problems of one file the definition names: a schema read as a JSON object, any other found.</summary>
    private IReadOnlyList<string> Check(WorkflowFiles.Named named, string directory, string name)
    {
        if (named.IsSchema)
        {
            return Schema(named.File, directory, name, named.What);
        }

        var problems = new List<string>();
        Add(problems, NamedFile(named.File, directory, name, named.What, out _));
        return problems;
    }

    /// <summary>
    /// The one problem with one file the definition names: that it is spelt as a path inside the
    /// workflow's own directory, that it stays there once every link along it is followed, and that
    /// it is there. What a reviewer reads there is what the workflow runs, as an action's files are
    /// to its own directory.
    /// </summary>
    /// <param name="named">The file, as the definition names it.</param>
    /// <param name="directory">The workflow's directory, every link along it followed.</param>
    /// <param name="name">The workflow.</param>
    /// <param name="what">What names the file, as a problem says it.</param>
    /// <param name="path">The file with every link along it followed, where it could be resolved.</param>
    private string? NamedFile(string named, string directory, string name, string what, out string path)
    {
        path = string.Empty;
        var relative = $"{HarnessLayout.WorkflowDirectoryRelative(name)}/";

        // Rooted by every platform's rules, as an action's path is: a drive written on Linux would otherwise read as a
        // directory's name, and a definition would mean two things on two machines.
        if (PlatformPaths.IsRootedOnAnyPlatform(named))
        {
            return $"'{named}', {what}, is an absolute path; every file a workflow names is relative to '{relative}', as an action's are to its own directory";
        }

        if (named.Split('/', '\\').Any(segment => segment is ".." or "." or ""))
        {
            return $"'{named}', {what}, does not name a file inside the workflow's directory; every file a workflow names is relative to '{relative}', without '.', '..' or an empty part, as an action's are to its own directory";
        }

        try
        {
            path = _fileSystem.ResolveLinks(Path.GetFullPath(Path.Combine(directory, named)));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return $"'{named}', {what}, could not be resolved inside '{directory}': {exception.Message}";
        }

        if (!PathContainment.IsStrictlyInside(directory, path, _platform.PathComparison))
        {
            return $"'{named}', {what}, leads to '{path}', which is outside '{directory}'; every file a workflow names is relative to '{relative}', as an action's are to its own directory";
        }

        if (IsFile(path, out var unlooked))
        {
            return null;
        }

        return unlooked is null
            ? $"'{named}', {what}, is not at '{relative}{named.Replace('\\', '/')}'; write it there, beside the definition that names it"
            : $"'{named}', {what}, could not be looked at: {unlooked}";
    }

    /// <summary>
    /// Whether a file is at <paramref name="path"/>; where none is, <paramref name="unlooked"/> says why the file system
    /// would not look, or is <see langword="null"/> where it looked and found nothing - a file that could not be looked
    /// at is never reported as missing, and the person is not sent to write one that may be there.
    /// </summary>
    private bool IsFile(string path, out string? unlooked)
    {
        unlooked = null;

        if (_fileSystem.FileExists(path))
        {
            return true;
        }

        // FileExists answers false where it could not look; the kind of what is there raises instead.
        try
        {
            _ = _fileSystem.KindOf(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            unlooked = exception.Message.TrimEnd('.');
        }

        return false;
    }

    /// <summary>
    /// The text of a file the definition names, for the validator to type a schema's paths and check a prompt's
    /// placeholders by; <see langword="null"/> where it is not a file of the workflow's own directory there to read,
    /// which <see cref="Check(WorkflowFiles.Named, string, string)"/> says.
    /// </summary>
    private string? Text(string named, string directory, string name)
    {
        if (NamedFile(named, directory, name, string.Empty, out var path) is not null)
        {
            return null;
        }

        try
        {
            return _fileSystem.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>That a schema file is there, can be read, and holds a JSON object.</summary>
    private IReadOnlyList<string> Schema(string named, string directory, string name, string what)
    {
        if (NamedFile(named, directory, name, what, out var path) is { } problem)
        {
            return [problem];
        }

        try
        {
            using var document = JsonDocument.Parse(_fileSystem.ReadAllText(path));

            return document.RootElement.ValueKind == JsonValueKind.Object
                ? []
                : [$"'{named}', {what}, holds no JSON object; a schema is a mapping of properties"];
        }
        catch (JsonException exception)
        {
            return [$"'{named}', {what}, is not valid JSON ({exception.Message})"];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [$"'{named}', {what}, could not be read: {exception.Message}"];
        }
    }

    private static string Shown(string name)
        => $"{HarnessLayout.WorkflowsDirectoryRelative}/{name}/{name}{HarnessLayout.WorkflowFileExtension}";

    private static int Count(IReadOnlyList<WorkflowStep> steps, string? type = null)
        => steps.Count(step => type is null || step.Type == type)
            + steps.Where(step => step.Steps.Count > 0).Sum(step => Count(step.Steps, type));
}

/// <summary>Turns a workflow validation report into what validate-workflow prints.</summary>
public static class WorkflowValidationReports
{
    /// <summary>
    /// What validate-workflow says where the configuration declares no workflow: there is nothing
    /// to check, and what declares the one init writes.
    /// </summary>
    public static string NoneDeclared { get; } =
        $"config.json declares no workflow, so there is none to check; \"workflows\": [\"{WorkflowTemplates.ShippedWorkflowName}\"] "
        + $"declares the one '{ToolPackage.Command} init' writes, and init then writes its files. Nothing was run.";

    /// <summary>What validate-workflow reports.</summary>
    /// <param name="report">What was checked.</param>
    /// <param name="json">Whether to write the result as JSON instead of as lines.</param>
    /// <remarks>
    /// A workflow that does not hold is configuration that does not, as config.json, an action file
    /// or the mutation registry is - the parser itself refuses a malformed definition as that - so it
    /// is refused <see cref="HarnessExit.ConfigInvalid"/>, the code a run of that workflow would meet.
    /// A configuration that declares none has nothing to fix in a definition and nothing that passed:
    /// the request has no subject, a precondition not met, refused <see cref="HarnessExit.Refused"/>.
    /// </remarks>
    public static CommandOutcome Render(WorkflowValidationReport report, bool json)
    {
        ArgumentNullException.ThrowIfNull(report);

        var data = json ? [Document(report).ToJsonString(ReportJson.Options)] : Array.Empty<string>();

        if (report.Workflows.Count == 0)
        {
            return CommandOutcome.Refused(NoneDeclared) with { Data = data };
        }

        if (report.Passed)
        {
            return CommandOutcome.Ok($"{ReportText.Counted(report.Workflows.Count, "workflow")} checked, all valid") with
            {
                Data = data,
                Quiet = json,
                Details = json ? null : report.Lines,
            };
        }

        var valid = report.Workflows.Count(workflow => workflow.Holds);

        return CommandOutcome.Failed(
            HarnessExit.ConfigInvalid,
            $"{ReportText.Counted(valid, "workflow")} valid, {report.Workflows.Count - valid} with problems. Nothing was run.") with
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
