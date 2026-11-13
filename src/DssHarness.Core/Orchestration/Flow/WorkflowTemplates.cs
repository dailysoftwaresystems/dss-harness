using System.Reflection;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Output;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>What making the implementation workflow whole in a repository did.</summary>
/// <param name="Created">
/// Each file written, relative to the repository's root with forward separators, in the order it was written.
/// </param>
/// <param name="Kept">How many of the workflow's files were there already, each left as it was.</param>
public sealed record ShippedWorkflowFiles(IReadOnlyList<string> Created, int Kept);

/// <summary>
/// The implementation workflow this tool ships: the definition <c>init</c> writes where a repository's
/// configuration declares it, and every file that definition names, embedded whole under the
/// workflow's own layout.
/// </summary>
/// <remarks>
/// <para>
/// Embedded so the files travel inside the assembly and can never be missing or out of step with
/// the code that reads them. The set is held to the rules by this build's own tests:
/// <see cref="ShippedDefinition"/> reads it back, parses it and holds it to
/// <see cref="WorkflowValidator"/>'s rules, and the suite every release passes asks it, so a build
/// whose own workflow does not hold fails before it ships, rather than writing a definition it
/// refuses. What <see cref="EnsureShipped"/> asks of the build as it writes is that every file of
/// the set is embedded, before the first is written.
/// </para>
/// <para>
/// Nothing that already exists is ever written over: each file of the workflow
/// that is there is kept, whatever it holds, and only one that is missing is written - so an init
/// that stopped part way is finished by the next, and a file a repository edited stays its own.
/// </para>
/// </remarks>
public static class WorkflowTemplates
{
    /// <summary>The name of the workflow this tool ships, and that <c>init</c> declares in a config it creates.</summary>
    public const string ShippedWorkflowName = "implementation";

    /// <summary>
    /// The prefix every embedded file of the implementation workflow carries, exactly as the build embeds
    /// it: the workflow's own directory tree under one root, kept with the separators of the machine
    /// that built it.
    /// </summary>
    private const string ResourcePrefix = "DssHarness.Core.WorkflowTemplates/";

    /// <summary>
    /// The embedded set, read once: each file's text, by its path relative to the workflow's own
    /// directory, spelt with '/' whatever separator the machine that built this assembly used.
    /// </summary>
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Embedded = new(ReadEmbedded);

    /// <summary>The agent files the implementation workflow names at its own levels.</summary>
    private static IReadOnlyList<string> Agents =>
    [
        "prioritizer", "triage", "planner", "plan-auditor",
        "scribe", "auditor", "diagnostician", "pre-existing-triage", "advisor", "reporter",
    ];

    /// <summary>The prompt files the implementation workflow names at its own levels.</summary>
    private static IReadOnlyList<string> Prompts =>
    [
        "priority", "triage", "plan", "replan", "plan-update", "plan-audit",
        "anchors", "deferrals", "handoff",
        "self-audit", "pr-body",
        "diagnose", "rediagnose", "redirect", "pre-existing",
        "recommend", "status", "ask",
    ];

    /// <summary>The schema files the implementation workflow names at its own levels.</summary>
    private static IReadOnlyList<string> Schemas =>
    [
        "priority", "triage", "plan", "plan-audit",
        "self-audit", "summary", "diagnosis", "pre-existing",
    ];

    /// <summary>The agent files the item pipeline names: the workflow its rounds and its recovery both use.</summary>
    private static IReadOnlyList<string> PipelineAgents =>
    [
        "implementer", "review-scope", "reviewer-correctness", "reviewer-errors", "reviewer-tests",
        "reviewer-types", "reviewer-comments", "reviewer-simplicity",
    ];

    /// <summary>The prompt files the item pipeline names, the two its uses hand it among them.</summary>
    private static IReadOnlyList<string> PipelinePrompts =>
    [
        "implement", "remedy", "redo", "undo", "unblock", "mend", "reconcile", "anchors",
        "review-scope", "review", "rework",
    ];

    /// <summary>The schema files the item pipeline names.</summary>
    private static IReadOnlyList<string> PipelineSchemas =>
    [
        "implement", "review-scope", "review",
    ];

    /// <summary>
    /// The item pipeline's own files, under its directory in the implementation workflow's, in the order they are
    /// written: the workflow the two uses name, its agents, its prompts - the two a use hands it among them - and its
    /// schemas.
    /// </summary>
    private static IReadOnlyList<string> PipelineFiles =>
    [
        $"item-pipeline/{HarnessLayout.WorkflowAgentsDirectoryName}/implementer.md",
        $"item-pipeline/{HarnessLayout.WorkflowAgentsDirectoryName}/review-scope.md",
        $"item-pipeline/{HarnessLayout.WorkflowAgentsDirectoryName}/reviewer-correctness.md",
        $"item-pipeline/{HarnessLayout.WorkflowAgentsDirectoryName}/reviewer-errors.md",
        $"item-pipeline/{HarnessLayout.WorkflowAgentsDirectoryName}/reviewer-tests.md",
        $"item-pipeline/{HarnessLayout.WorkflowAgentsDirectoryName}/reviewer-types.md",
        $"item-pipeline/{HarnessLayout.WorkflowAgentsDirectoryName}/reviewer-comments.md",
        $"item-pipeline/{HarnessLayout.WorkflowAgentsDirectoryName}/reviewer-simplicity.md",
        .. PipelinePrompts.Select(prompt => $"item-pipeline/prompts/{prompt}.md"),
        .. PipelineSchemas.Select(schema => $"item-pipeline/schemas/{schema}.schema.json"),
        $"item-pipeline/item-pipeline{HarnessLayout.WorkflowFileExtension}",
    ];

    /// <summary>
    /// Every file of the implementation workflow, relative to its directory, in the order they are
    /// written.
    /// </summary>
    public static IReadOnlyList<string> Files =>
    [
        .. Agents.Select(agent => $"{HarnessLayout.WorkflowAgentsDirectoryName}/{agent}.md"),
        .. Prompts.Select(prompt => $"prompts/{prompt}.md"),
        .. Schemas.Select(schema => $"schemas/{schema}.schema.json"),
        "recommendations.md",
        .. PipelineFiles,
        $"{ShippedWorkflowName}{HarnessLayout.WorkflowFileExtension}",
    ];

    /// <summary>
    /// The implementation workflow's definition, read back from this build's own embedded set, parsed and held to
    /// <see cref="WorkflowValidator"/>'s rules as for a configuration with no <c>commit.template</c>, each embedded schema
    /// held to the subset the engine reads, its paths typed by those schemas and its prompts' placeholders checked. What only a repository can answer - that its files
    /// are there, its legs and runners - is <c>validate-workflow</c>'s to check there.
    /// </summary>
    /// <exception cref="HarnessException">
    /// This build's own embedded set is broken: a file is missing, or the definition does not hold.
    /// A defect of the build, not of any repository; exit 70, the code of the tool failing itself.
    /// </exception>
    public static WorkflowDefinition ShippedDefinition()
    {
        var file = $"{ShippedWorkflowName}{HarnessLayout.WorkflowFileExtension}";

        if (TextOf(file) is not { } definition)
        {
            throw new HarnessException(HarnessExit.InternalError, $"this build embeds no '{file}'; the implementation workflow is incomplete in it");
        }

        WorkflowDefinition parsed;

        try
        {
            parsed = WorkflowFileParser.Parse(file, definition);
        }
        catch (HarnessException exception)
        {
            throw new HarnessException(
                HarnessExit.InternalError,
                $"this build's own implementation workflow does not read:{Environment.NewLine}{exception.Message}",
                exception);
        }

        if (!string.Equals(parsed.Name, ShippedWorkflowName, StringComparison.Ordinal))
        {
            throw new HarnessException(
                HarnessExit.InternalError,
                $"this build's implementation workflow names itself '{parsed.Name}'; it is '{ShippedWorkflowName}'");
        }

        // The definition as a run reads it: every use resolved - the item pipeline placed at each of the two places
        // that name it - so that what is held to the rules is the tree the engine runs.
        var (resolved, resolution) = WorkflowResolver.Resolve(parsed, ShippedWorkflowName, path => TextOf(Below(path)));

        // As for a config with no commit.template: each commit step writes its message. The files are the build's own,
        // so every schema is held to the subset, every path is typed by the schema that holds it and every prompt's
        // placeholder is checked - each in the directory of the workflow that names it, and the file a use fills a
        // file-kind input with is one of the used workflow's directory, found where its use is checked.
        IReadOnlyList<string> problems =
        [
            .. resolution,
            .. WorkflowFiles.Steps(resolved)
                .SelectMany(entry => WorkflowFiles.OfStep(entry.Step, $"step '{entry.Path}'")
                    .Select(named => (entry.Step.WorkflowOf, Named: named)))
                .Where(schema => schema.Named.IsSchema && EmbeddedText(schema.WorkflowOf, schema.Named.File) is not null)
                .SelectMany(schema => WorkflowFiles.SchemaProblems(schema.Named, EmbeddedText(schema.WorkflowOf, schema.Named.File)!)),
            .. WithFilesOf(resolved),
            .. WorkflowValidator.Validate(resolved, hasCommitTemplate: false, (named, owner) => EmbeddedText(owner, named)),
        ];

        if (problems.Count > 0)
        {
            var detail = string.Join(Environment.NewLine, problems.Select(problem => "  - " + problem));

            throw new HarnessException(
                HarnessExit.InternalError,
                $"this build's own implementation workflow does not hold:{Environment.NewLine}{detail}");
        }

        return resolved;
    }

    /// <summary>
    /// Why a file a use of the resolved definition fills a file-kind input with is not one of this build's embedded
    /// set: each such file is a file of the used workflow's directory, as every file a step of it names is, and one
    /// that is missing makes the build's own set incomplete. An input filled with nothing, or with a template, is the
    /// definition's own check to refuse; none is said here.
    /// </summary>
    private static IReadOnlyList<string> WithFilesOf(WorkflowDefinition resolved)
    {
        var problems = new List<string>();

        foreach (var (path, step) in WorkflowFiles.Steps(resolved).Where(entry => entry.Step.Use is not null))
        {
            var use = step.Use!;

            foreach (var input in use.Inputs.Where(input => input.Kind == FlowInput.FileKind))
            {
                var filled = step.With.TryGetValue(input.Name, out var with) ? with : input.Default;

                if (filled is not { Length: > 0 } || filled.Contains('{'))
                {
                    continue;
                }

                if (EmbeddedText(use.Name, filled) is null)
                {
                    problems.Add($"step '{path}' fills the input '{input.Name}' of the workflow '{use.Name}' it uses with '{filled}', and no file is at that path in this build's embedded set");
                }
            }
        }

        return problems;
    }

    /// <summary>
    /// The text of one embedded file of this workflow's set, named by the workflow that owns it: a file of the
    /// workflow itself is named as it names it, and a file of a workflow it uses - the item pipeline's prompts and
    /// schemas - carries that workflow's own place under this one.
    /// </summary>
    /// <param name="owner">The workflow the file belongs to: its path under the workflows root, or null for this workflow's own.</param>
    /// <param name="named">The file, as the definition names it, relative to its owner's directory.</param>
    private static string? EmbeddedText(string? owner, string named)
        => owner is null || owner == ShippedWorkflowName
            ? TextOf(named)
            : TextOf($"{owner[(ShippedWorkflowName.Length + 1)..]}/{named}");

    /// <summary>A workflow file's path under this workflow's own directory, as the resolver names it under the workflows root.</summary>
    private static string Below(string path) => path[(ShippedWorkflowName.Length + 1)..];

    /// <summary>
    /// Writes each file of the implementation workflow that is missing from its directory under
    /// <paramref name="workflowsDirectory"/>, and keeps each one that is there, whatever it holds. Where the
    /// repository already has its own definition there, only the missing shipped files that definition names are
    /// written: a file taken out with its step stays out.
    /// </summary>
    /// <param name="workflowsDirectory">The repository's workflows directory, under its harness directory.</param>
    /// <param name="fileSystem">Writes the files.</param>
    /// <returns>The files written, in the order they were, and how many were kept.</returns>
    /// <exception cref="HarnessException">
    /// The repository's own definition does not read (<see cref="HarnessExit.ConfigInvalid"/>) or cannot be read
    /// (<see cref="HarnessExit.Refused"/>), so which shipped files it names cannot be told, and nothing is written. A
    /// file could not be written (<see cref="HarnessExit.Refused"/>): the refusal names it and each file written before
    /// it, which stay, and init run again writes what is still missing. Or this build embeds a file of the set no more
    /// (<see cref="HarnessExit.InternalError"/>), and nothing is written.
    /// </exception>
    public static ShippedWorkflowFiles EnsureShipped(string workflowsDirectory, IFileSystem fileSystem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowsDirectory);
        ArgumentNullException.ThrowIfNull(fileSystem);

        // The workflow's own directory, under the workflows directory: everything the definition
        // names lives beside it.
        var directory = Path.Combine(workflowsDirectory, ShippedWorkflowName);
        var kept = Files.Count(file => fileSystem.FileExists(PathOf(directory, file)));

        // Every shipped file there leaves nothing to write, whatever the definition names; only where one is missing is
        // the definition asked which of them it wants back.
        if (kept == Files.Count)
        {
            return new ShippedWorkflowFiles([], kept);
        }

        var missing = Wanted(directory, fileSystem).Where(file => !fileSystem.FileExists(PathOf(directory, file))).ToList();

        if (missing.Count == 0)
        {
            return new ShippedWorkflowFiles([], kept);
        }

        // Read whole before anything is written, so a build with a broken set writes nothing rather
        // than part of a workflow.
        var contents = Files.Select(file => (File: file, Text: TextOf(file))).ToList();

        if (contents.Any(entry => entry.Text is null))
        {
            var absent = string.Join(", ", contents.Where(entry => entry.Text is null).Select(entry => $"'{entry.File}'"));

            throw new HarnessException(
                HarnessExit.InternalError,
                $"this build embeds no {absent}; the implementation workflow is incomplete in it");
        }

        var created = new List<string>();

        foreach (var (file, text) in contents.Where(entry => missing.Contains(entry.File, StringComparer.Ordinal)))
        {
            var path = PathOf(directory, file);

            try
            {
                fileSystem.CreateDirectory(Path.GetDirectoryName(path)!);
                fileSystem.WriteAllTextAtomic(path, text!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A refusal, never the tool's own failure: the disk said no, and what was written before it
                // stays, a workflow the next init finishes.
                throw new HarnessException(HarnessExit.Refused, NotWritten(Shown(file), ex.Message, created, missing.Count), ex);
            }

            created.Add(Shown(file));
        }

        return new ShippedWorkflowFiles(created, kept);
    }

    /// <summary>
    /// The shipped files <c>init</c> may write into <paramref name="directory"/>: the whole set where the repository has
    /// no definition of the workflow yet; where it has one, the shipped files that definition names, and no other - a
    /// file a repository took out with its step is not written back.
    /// </summary>
    /// <exception cref="HarnessException">
    /// The definition there does not read (<see cref="HarnessExit.ConfigInvalid"/>), or could not be read
    /// (<see cref="HarnessExit.Refused"/>): which files it names cannot be told, and none is written in its place.
    /// </exception>
    private static IReadOnlyList<string> Wanted(string directory, IFileSystem fileSystem)
    {
        var definition = PathOf(directory, $"{ShippedWorkflowName}{HarnessLayout.WorkflowFileExtension}");

        if (!fileSystem.FileExists(definition))
        {
            return Files;
        }

        var unread = $"{Shown($"{ShippedWorkflowName}{HarnessLayout.WorkflowFileExtension}")} is the repository's own definition of the "
            + $"{ShippedWorkflowName} workflow, and";
        var after = $"so which shipped files it names cannot be told, and none was written; run '{ToolPackage.Command} init' again once it reads";
        string text;

        try
        {
            text = fileSystem.ReadAllText(definition);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(HarnessExit.Refused, $"{unread} could not be read ({ex.Message.TrimEnd('.')}), {after}.", ex);
        }

        try
        {
            // The definition resolved as a run reads it, its uses included: what a repository's own definition names
            // is the whole tree it runs, a used workflow's files among them - so a pipeline file the repository took
            // out is written back, read from the repository's own workflows directory as a run resolves them.
            var parsed = WorkflowFileParser.Parse(definition, text);
            var workflows = Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar))!;
            string? ReadUnder(string relative)
            {
                var path = Path.Combine([workflows, .. relative.Split('/')]);

                if (!fileSystem.FileExists(path))
                {
                    return null;
                }

                try
                {
                    return fileSystem.ReadAllText(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return null;
                }
            }

            var (resolved, _) = WorkflowResolver.Resolve(parsed, ShippedWorkflowName, ReadUnder);
            var named = WorkflowFiles.AllUnder(resolved);

            return [.. Files.Where(file => named.Contains(file, StringComparer.Ordinal))];
        }
        catch (HarnessException ex)
        {
            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"{unread} does not read, {after} ('{ToolPackage.Command} {WorkflowValidationService.CommandName} {ShippedWorkflowName}' says the same):{Environment.NewLine}{ex.Message}",
                ex);
        }
    }

    /// <summary>Where <paramref name="file"/> of the implementation workflow is, under its <paramref name="directory"/>.</summary>
    private static string PathOf(string directory, string file)
        => Path.Combine(directory, file.Replace('/', Path.DirectorySeparatorChar));

    /// <summary><paramref name="file"/> of the implementation workflow, relative to the repository's root with forward separators.</summary>
    private static string Shown(string file)
        => $"{HarnessLayout.WorkflowsDirectoryRelative}/{ShippedWorkflowName}/{file}";

    /// <summary>
    /// Why the implementation workflow was left part written: the file that could not be, and what was written
    /// before it, which stays.
    /// </summary>
    private static string NotWritten(string file, string why, IReadOnlyList<string> created, int missing)
    {
        var before = created.Count == 0
            ? $"None of the implementation workflow's {ReportText.Counted(missing, "missing file")} was written"
            : $"{created.Count} of the implementation workflow's {ReportText.Counted(missing, "missing file")} were written "
                + $"before it, and stay: {ReportText.Listed(created)}";

        return $"'{file}' could not be written: {why.TrimEnd('.')}. {before}; run '{ToolPackage.Command} init' again "
            + "once it can be, which writes what is still missing and keeps every file there.";
    }

    /// <summary>The text of one embedded file of the implementation workflow, or <see langword="null"/> where this build holds none.</summary>
    /// <param name="relative">The file's path, relative to the workflow's directory, with forward separators.</param>
    public static string? TextOf(string relative)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relative);

        return Embedded.Value.TryGetValue(relative, out var text) ? text : null;
    }

    /// <summary>
    /// Reads the embedded set whole: every resource under this workflow's root, its path taken
    /// relative to the workflow's own directory, as the fixture this tool sweeps is read.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ReadEmbedded()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var files = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"The resource '{name}' this assembly lists is not in it.");
            using var reader = new StreamReader(stream);

            // Embedded under the workflow's own name, with the machine's separator: the name the
            // set knows a file by is its path from inside that directory, with '/'.
            var relative = name[ResourcePrefix.Length..].Replace('\\', '/');
            files[relative[(relative.IndexOf('/') + 1)..]] = reader.ReadToEnd();
        }

        return files;
    }
}
