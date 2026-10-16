using System.Reflection;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The default workflow this tool ships: the definition <c>init</c> writes for a repository that
/// has none, and every file that definition names, embedded whole under the workflow's own layout.
/// </summary>
/// <remarks>
/// <para>
/// Embedded so the files travel inside the assembly and can never be missing or out of step with
/// the code that reads them. The set checks itself: <see cref="DefaultDefinition"/> reads it back,
/// parses it and holds it to <see cref="WorkflowValidator"/>'s rules, so a build whose own workflow
/// does not hold is a build that says so, not one that writes a definition it refuses.
/// </para>
/// <para>
/// Nothing that already exists is ever written over (user, 2026-10-10): where the workflows
/// directory is there already, a repository has made its own arrangement, and the default is
/// written nowhere in it.
/// </para>
/// </remarks>
public static class WorkflowTemplates
{
    /// <summary>The name of the workflow this tool ships, and that <c>init</c> declares in a config it creates.</summary>
    public const string DefaultWorkflowName = "default";

    /// <summary>
    /// The prefix every embedded file of the default workflow carries, exactly as the build embeds
    /// it: the workflow's own directory tree under one root, kept with the separators of the machine
    /// that built it.
    /// </summary>
    private const string ResourcePrefix = "DssHarness.Core.WorkflowTemplates/";

    /// <summary>
    /// The embedded set, read once: each file's text, by its path relative to the workflow's own
    /// directory, spelt with '/' whatever separator the machine that built this assembly used.
    /// </summary>
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Embedded = new(ReadEmbedded);

    /// <summary>The agent files the default workflow names, in the order its steps name them.</summary>
    private static IReadOnlyList<string> Agents =>
    [
        "prioritizer", "triage", "planner", "plan-auditor", "implementer",
        "review-scope", "reviewer-correctness", "reviewer-errors", "reviewer-tests",
        "reviewer-types", "reviewer-comments", "reviewer-simplicity",
        "scribe", "auditor", "fixer", "advisor", "reporter",
    ];

    /// <summary>The prompt files the default workflow names.</summary>
    private static IReadOnlyList<string> Prompts =>
    [
        "priority", "triage", "plan", "replan", "plan-update", "plan-audit",
        "implement", "redo", "undo", "unblock", "mend", "reconcile", "anchors",
        "review-scope", "review", "rework",
        "deferrals", "handoff",
        "fix-checks", "self-audit", "pr-body",
        "recommend", "status", "ask",
    ];

    /// <summary>The schema files the default workflow names.</summary>
    private static IReadOnlyList<string> Schemas =>
    [
        "priority", "triage", "plan", "plan-audit", "implement",
        "review-scope", "review", "self-audit", "summary", "fix",
    ];

    /// <summary>
    /// Every file of the default workflow, relative to its directory, in the order they are
    /// written.
    /// </summary>
    public static IReadOnlyList<string> Files =>
    [
        .. Agents.Select(agent => $"{HarnessLayout.WorkflowAgentsDirectoryName}/{agent}.md"),
        .. Prompts.Select(prompt => $"prompts/{prompt}.md"),
        .. Schemas.Select(schema => $"schemas/{schema}.schema.json"),
        "recommendations.md",
        $"{DefaultWorkflowName}{HarnessLayout.WorkflowFileExtension}",
    ];

    /// <summary>
    /// The default workflow's definition, read back from this build's own embedded set, parsed and
    /// held to every rule a repository's workflow is held to.
    /// </summary>
    /// <exception cref="HarnessException">
    /// This build's own embedded set is broken: a file is missing, or the definition does not hold.
    /// A defect of the build, not of any repository; exit 70, the code of the tool failing itself.
    /// </exception>
    public static WorkflowDefinition DefaultDefinition()
    {
        var file = $"{DefaultWorkflowName}{HarnessLayout.WorkflowFileExtension}";

        if (TextOf(file) is not { } definition)
        {
            throw new HarnessException(HarnessExit.InternalError, $"this build embeds no '{file}'; the default workflow is incomplete in it");
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
                $"this build's own default workflow does not read:{Environment.NewLine}{exception.Message}",
                exception);
        }

        if (!string.Equals(parsed.Name, DefaultWorkflowName, StringComparison.Ordinal))
        {
            throw new HarnessException(
                HarnessExit.InternalError,
                $"this build's default workflow names itself '{parsed.Name}'; it is '{DefaultWorkflowName}'");
        }

        if (WorkflowValidator.Validate(parsed) is [_, ..] problems)
        {
            var detail = string.Join(Environment.NewLine, problems.Select(problem => "  - " + problem));

            throw new HarnessException(
                HarnessExit.InternalError,
                $"this build's own default workflow does not hold:{Environment.NewLine}{detail}");
        }

        return parsed;
    }

    /// <summary>
    /// Writes the default workflow into <paramref name="workflowsDirectory"/> where that directory
    /// is not there, and writes nothing where it is.
    /// </summary>
    /// <param name="workflowsDirectory">The repository's workflows directory, under its harness directory.</param>
    /// <param name="fileSystem">Writes the files.</param>
    /// <returns>The files written, relative to the repository's root with forward separators, in the order they were written.</returns>
    public static IReadOnlyList<string> EnsureDefault(string workflowsDirectory, IFileSystem fileSystem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowsDirectory);
        ArgumentNullException.ThrowIfNull(fileSystem);

        if (fileSystem.DirectoryExists(workflowsDirectory))
        {
            return [];
        }

        // Read whole before anything is written, so a build with a broken set writes nothing rather
        // than half a workflow.
        var contents = Files.Select(file => (File: file, Text: TextOf(file))).ToList();

        if (contents.Any(entry => entry.Text is null))
        {
            var missing = string.Join(", ", contents.Where(entry => entry.Text is null).Select(entry => $"'{entry.File}'"));

            throw new HarnessException(
                HarnessExit.InternalError,
                $"this build embeds no {missing}; the default workflow is incomplete in it");
        }

        // The workflow's own directory, under the workflows directory: everything the definition
        // names lives beside it.
        var directory = Path.Combine(workflowsDirectory, DefaultWorkflowName);
        fileSystem.CreateDirectory(directory);

        foreach (var (file, text) in contents)
        {
            var path = Path.Combine(directory, file.Replace('/', Path.DirectorySeparatorChar));
            fileSystem.CreateDirectory(Path.GetDirectoryName(path)!);
            fileSystem.WriteAllTextAtomic(path, text!);
        }

        return [.. Files.Select(file => $"{HarnessLayout.WorkflowsDirectoryRelative}/{DefaultWorkflowName}/{file.Replace('\\', '/')}")];
    }

    /// <summary>The text of one embedded file of the default workflow, or <see langword="null"/> where this build holds none.</summary>
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
