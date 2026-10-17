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
/// Nothing that already exists is ever written over (user, 2026-10-10): each file of the workflow
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

    /// <summary>The agent files the implementation workflow names, in the order its steps name them.</summary>
    private static IReadOnlyList<string> Agents =>
    [
        "prioritizer", "triage", "planner", "plan-auditor", "implementer",
        "review-scope", "reviewer-correctness", "reviewer-errors", "reviewer-tests",
        "reviewer-types", "reviewer-comments", "reviewer-simplicity",
        "scribe", "auditor", "diagnostician", "advisor", "reporter",
    ];

    /// <summary>The prompt files the implementation workflow names.</summary>
    private static IReadOnlyList<string> Prompts =>
    [
        "priority", "triage", "plan", "replan", "plan-update", "plan-audit",
        "implement", "redo", "undo", "unblock", "mend", "reconcile", "anchors",
        "review-scope", "review", "rework",
        "deferrals", "handoff",
        "self-audit", "pr-body",
        "diagnose", "rediagnose", "redirect", "remedy",
        "recommend", "status", "ask",
    ];

    /// <summary>The schema files the implementation workflow names.</summary>
    private static IReadOnlyList<string> Schemas =>
    [
        "priority", "triage", "plan", "plan-audit", "implement",
        "review-scope", "review", "self-audit", "summary", "diagnosis",
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
        $"{ShippedWorkflowName}{HarnessLayout.WorkflowFileExtension}",
    ];

    /// <summary>
    /// The implementation workflow's definition, read back from this build's own embedded set, parsed and
    /// held to every rule a repository's workflow is held to.
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

        if (WorkflowValidator.Validate(parsed) is [_, ..] problems)
        {
            var detail = string.Join(Environment.NewLine, problems.Select(problem => "  - " + problem));

            throw new HarnessException(
                HarnessExit.InternalError,
                $"this build's own implementation workflow does not hold:{Environment.NewLine}{detail}");
        }

        return parsed;
    }

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
    /// A file could not be written (<see cref="HarnessExit.Refused"/>): the refusal names it and each file
    /// written before it, which stay, and init run again writes what is still missing. Or this build
    /// embeds a file of the set no more (<see cref="HarnessExit.InternalError"/>), and nothing is written.
    /// </exception>
    public static ShippedWorkflowFiles EnsureShipped(string workflowsDirectory, IFileSystem fileSystem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowsDirectory);
        ArgumentNullException.ThrowIfNull(fileSystem);

        // The workflow's own directory, under the workflows directory: everything the definition
        // names lives beside it.
        var directory = Path.Combine(workflowsDirectory, ShippedWorkflowName);
        var wanted = Wanted(directory, fileSystem);
        var missing = wanted.Where(file => !fileSystem.FileExists(PathOf(directory, file))).ToList();
        var kept = Files.Count(file => fileSystem.FileExists(PathOf(directory, file)));

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
    /// file a repository took out with its step is not written back (part 05) - and none where the definition does not
    /// read, since only its check can say what it names.
    /// </summary>
    private static IReadOnlyList<string> Wanted(string directory, IFileSystem fileSystem)
    {
        var definition = PathOf(directory, $"{ShippedWorkflowName}{HarnessLayout.WorkflowFileExtension}");

        if (!fileSystem.FileExists(definition))
        {
            return Files;
        }

        try
        {
            var named = WorkflowFiles.All(WorkflowFileParser.Parse(definition, fileSystem.ReadAllText(definition)));

            return [.. Files.Where(file => named.Contains(file, StringComparer.Ordinal))];
        }
        catch (Exception ex) when (ex is HarnessException or IOException or UnauthorizedAccessException)
        {
            return [];
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
