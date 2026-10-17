using System.Text.Json.Nodes;
using DssHarness.Core.Application;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>What export-workflow is asked.</summary>
/// <param name="Workflow">The workflow: one config.json declares, or, with <paramref name="Shipped"/>, the one this tool ships.</param>
/// <param name="To">
/// The folder the workflow's own folder is made in, as it was given: absolute, or relative to the directory the command was
/// typed in.
/// </param>
/// <param name="Shipped">Whether the workflow is the one this tool ships, read from the tool itself rather than from the repository.</param>
public sealed record WorkflowExportRequest(string Workflow, string To, bool Shipped);

/// <summary>What export-workflow wrote: the typed answer a front end reads, the same the lines say.</summary>
/// <param name="Workflow">The workflow exported.</param>
/// <param name="Shipped">Whether it was the one this tool ships.</param>
/// <param name="Destination">The folder made for it, absolute: the folder asked for, and the workflow's name under it.</param>
/// <param name="Files">
/// Each file written, relative to <paramref name="Destination"/> with '/', in the order it was written: every file the
/// definition names, then the definition.
/// </param>
public sealed record WorkflowExport(string Workflow, bool Shipped, string Destination, IReadOnlyList<string> Files);

/// <summary>
/// Copies a workflow - its yml and every file it names - into a folder of its own, so a person or a repository has it to
/// read and make their own version of: one a repository declares, once it holds, or the one this tool ships.
/// </summary>
/// <remarks>
/// The files are the ones the definition names (<see cref="WorkflowFiles.All"/>), the one list <c>validate-workflow</c>
/// and <c>init</c> read, so a file a repository took out with its step is not exported, and one the definition names is
/// never left behind. A declared workflow's files are copied byte for byte and each proved where it lands
/// (<see cref="VerifiedFileCopy"/>); the shipped one's are written as <c>init</c> writes them.
/// </remarks>
public interface IWorkflowExportService : IUseCase
{
    /// <summary>Exports the workflow <paramref name="request"/> names into a folder of its own under the folder it gives.</summary>
    /// <param name="startDirectory">A directory inside the repository, whose configuration declares the workflow; unread where it is the shipped one.</param>
    /// <param name="request">What is exported, and where to.</param>
    /// <param name="cancellationToken">Stops the export, taking back what it wrote.</param>
    /// <returns>Where the workflow was written, and each file.</returns>
    /// <exception cref="HarnessException">
    /// A workflow nobody declared, the shipped one asked for under a name it does not have, or no folder to write to
    /// (<see cref="HarnessExit.UsageError"/>); a declared workflow that does not hold (<see cref="HarnessExit.ConfigInvalid"/>,
    /// naming each problem); a folder for it that is there already or could not be looked at, or a file that could not be
    /// written (<see cref="HarnessExit.Refused"/>); stopped while it wrote (<see cref="HarnessExit.Cancelled"/>, where
    /// what it wrote could not be taken back; otherwise the cancellation itself). What was written before a failure is
    /// taken back, every folder the export made with it, or the refusal says why it could not be.
    /// </exception>
    Task<WorkflowExport> ExportAsync(string startDirectory, WorkflowExportRequest request, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IWorkflowExportService"/>
public sealed class WorkflowExportService(
    IWorkflowValidationService validation,
    IFileSystem fileSystem,
    IProcessContext process) : IWorkflowExportService
{
    /// <summary>The command this service reports under.</summary>
    public const string CommandName = "export-workflow";

    private readonly IWorkflowValidationService _validation = validation;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IProcessContext _process = process;

    /// <inheritdoc/>
    public async Task<WorkflowExport> ExportAsync(
        string startDirectory,
        WorkflowExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.To))
        {
            throw new HarnessException(HarnessExit.UsageError, "--to names no folder; the workflow is written into a folder of its own under the one it names.");
        }

        var files = request.Shipped
            ? Shipped(request.Workflow)
            : await DeclaredAsync(startDirectory, request.Workflow, cancellationToken).ConfigureAwait(false);

        var destination = Path.Combine(Path.GetFullPath(request.To, _process.CurrentDirectory), request.Workflow);
        string made;

        try
        {
            // A folder of its own, never one that is there: what is written is the workflow and nothing else, and nothing a
            // person put there is written over or mixed with it.
            if (_fileSystem.KindOf(destination) != PathKind.None)
            {
                throw new HarnessException(
                    HarnessExit.Refused,
                    $"'{destination}' is there already; {CommandName} writes a workflow into a folder of its own that it makes, and never into or over one that is there. Nothing was written.");
            }

            made = Outermost(destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(HarnessExit.Refused, $"'{destination}' could not be looked at: {ex.Message.TrimEnd('.')}. Nothing was written.", ex);
        }

        var written = new List<string>();

        foreach (var (file, write) in files)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await write(Path.Combine(destination, file.Replace('/', Path.DirectorySeparatorChar))).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                // Taken back whole, with every folder the export made above it: a folder with part of a workflow in it
                // would read as a workflow that names files it does not have.
                var left = TakeBack(made);

                if (ex is OperationCanceledException && left is null)
                {
                    throw;
                }

                var stopped = ex is OperationCanceledException;
                var why = stopped ? "the export was stopped" : ex.Message.TrimEnd('.');
                var after = left ?? $"what was written before it was taken back, and '{made}' is not there";

                throw new HarnessException(stopped ? HarnessExit.Cancelled : HarnessExit.Refused, $"'{file}' could not be written to '{destination}': {why}; {after}.", ex);
            }

            written.Add(file);
        }

        return new WorkflowExport(request.Workflow, request.Shipped, destination, written);
    }

    /// <summary>
    /// The outermost folder the export makes on the way to <paramref name="destination"/>: the destination itself where the
    /// folder it is made in is there, else the first of the folders above it that is not - what a failed export takes back,
    /// so no folder it made is left behind.
    /// </summary>
    private string Outermost(string destination)
    {
        var made = destination;

        while (Path.GetDirectoryName(made) is { } parent && _fileSystem.KindOf(parent) == PathKind.None)
        {
            made = parent;
        }

        return made;
    }

    /// <summary>
    /// The files of the declared workflow <paramref name="name"/>, once its check holds, each copied from the directory its
    /// check found it in: what is exported is what was checked.
    /// </summary>
    private async Task<IReadOnlyList<(string File, Func<string, Task> Write)>> DeclaredAsync(
        string startDirectory,
        string name,
        CancellationToken cancellationToken)
    {
        var report = await _validation.CheckAsync(startDirectory, name, cancellationToken).ConfigureAwait(false);
        var check = report.Workflows.Single();

        if (check is not { Holds: true, Definition: { } definition, Directory: { } directory })
        {
            var problems = string.Join(Environment.NewLine, check.Lines.Select(line => "  - " + line));

            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"the workflow '{name}' does not hold, so it was not exported and nothing was written; '{ToolPackage.Command} {WorkflowValidationService.CommandName} {name}' says the same:{Environment.NewLine}{problems}");
        }

        return
        [
            .. Named(definition, name).Select(file => (file, (Func<string, Task>)(path => VerifiedFileCopy.CopyAsync(
                _fileSystem,
                Path.Combine(directory, file.Replace('/', Path.DirectorySeparatorChar)),
                path,
                cancellationToken: cancellationToken)))),
        ];
    }

    /// <summary>
    /// The files of the workflow this tool ships, read from this build and held to <see cref="WorkflowValidator"/>'s
    /// rules (<see cref="WorkflowTemplates.ShippedDefinition"/>), each written as <c>init</c> writes it.
    /// </summary>
    private IReadOnlyList<(string File, Func<string, Task> Write)> Shipped(string name)
    {
        if (!string.Equals(name, WorkflowTemplates.ShippedWorkflowName, StringComparison.Ordinal))
        {
            throw new HarnessException(
                HarnessExit.UsageError,
                $"this tool ships one workflow, '{WorkflowTemplates.ShippedWorkflowName}', not '{name}'; "
                    + $"'{ToolPackage.Command} {CommandName} {WorkflowTemplates.ShippedWorkflowName} --shipped --to <dir>' exports it.");
        }

        var texts = Named(WorkflowTemplates.ShippedDefinition(), name)
            .Select(file => (File: file, Text: WorkflowTemplates.TextOf(file)))
            .ToList();

        // Read whole before anything is written, so a build with a broken set writes nothing rather than part of a workflow.
        if (texts.Where(entry => entry.Text is null).Select(entry => $"'{entry.File}'").ToList() is [_, ..] absent)
        {
            throw new HarnessException(
                HarnessExit.InternalError,
                $"this build embeds no {string.Join(", ", absent)}; the {name} workflow is incomplete in it");
        }

        return
        [
            .. texts.Select(entry => (entry.File, (Func<string, Task>)(path =>
            {
                _fileSystem.CreateDirectory(Path.GetDirectoryName(path)!);
                _fileSystem.WriteAllTextAtomic(path, entry.Text!);
                return Task.CompletedTask;
            }))),
        ];
    }

    /// <summary>Every file the definition names, each with '/', and the definition last: a folder that has its yml has every file it names.</summary>
    private static IReadOnlyList<string> Named(WorkflowDefinition definition, string name)
        =>
        [
            .. WorkflowFiles.All(definition).Select(file => file.Replace('\\', '/')).Distinct(StringComparer.Ordinal),
            $"{name}{HarnessLayout.WorkflowFileExtension}",
        ];

    /// <summary>Removes the outermost folder an export that failed made, and all it holds; says why where it cannot.</summary>
    private string? TakeBack(string destination)
    {
        try
        {
            _fileSystem.DeleteDirectory(destination);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"what was written before it could not be taken back from '{destination}': {ex.Message.TrimEnd('.')}";
        }
    }
}

/// <summary>Turns what export-workflow wrote into what it prints.</summary>
public static class WorkflowExportReports
{
    /// <summary>What export-workflow reports: where the workflow went, and each file, or the document alone.</summary>
    /// <param name="export">What was written.</param>
    /// <param name="json">Whether to write the result as JSON instead of as lines.</param>
    public static CommandOutcome Render(WorkflowExport export, bool json)
    {
        ArgumentNullException.ThrowIfNull(export);

        var shipped = export.Shipped ? ", as this tool ships it," : string.Empty;

        return CommandOutcome.Ok($"'{export.Workflow}'{shipped} exported to '{export.Destination}': {ReportText.Counted(export.Files.Count, "file")}") with
        {
            Data = json ? [Document(export, export.Shipped).ToJsonString(ReportJson.Options)] : [],
            Quiet = json,
            Details = json ? null : export.Files,
            Facts = export,
        };
    }

    /// <summary>
    /// The document export-workflow answers with where it stops before it wrote anything: its own, nothing exported, after
    /// the code it exits with and the line it ends on, and whether the shipped workflow was asked for.
    /// </summary>
    /// <param name="exitCode">The code the command exits with.</param>
    /// <param name="summary">The line it ends on.</param>
    /// <param name="shipped">Whether its command line asked for the shipped workflow.</param>
    public static string Stopped(int exitCode, string summary, bool shipped)
        => ReportJson.Stopped(exitCode, summary, Document(export: null, shipped));

    private static JsonObject Document(WorkflowExport? export, bool shipped)
        => new()
        {
            ["workflow"] = export?.Workflow,
            ["shipped"] = shipped,
            ["destination"] = export?.Destination,
            ["files"] = new JsonArray([.. (export?.Files ?? []).Select(file => (JsonNode)file!)]),
        };
}
