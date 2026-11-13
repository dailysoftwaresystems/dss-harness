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
/// <param name="Workflow">
/// The workflow: one config.json declares, or, with <paramref name="Shipped"/>, the one this tool ships; null where
/// <paramref name="From"/> names the workflow, and with it none is given.
/// </param>
/// <param name="To">
/// The folder the workflow's own folder is made in, as it was given: absolute, or relative to the directory the command was
/// typed in.
/// </param>
/// <param name="Shipped">Whether the workflow is the one this tool ships, read from the tool itself rather than from the repository.</param>
/// <param name="From">
/// A remote workflow to vendor into the repository, as a use names it - <c>owner/repo/path/workflow.yml@ref</c> - fetched at
/// its resolved commit with every file of its directory and a note of where it came from, so a team can review it and use it
/// as <c>./</c>; null where a workflow of the repository's own is exported.
/// </param>
public sealed record WorkflowExportRequest(string? Workflow, string To, bool Shipped, string? From = null);

/// <summary>What export-workflow wrote: the typed answer a front end reads, the same the lines say.</summary>
/// <param name="Workflow">The workflow exported: its name, or a vendored one's directory's.</param>
/// <param name="Shipped">Whether it was the one this tool ships.</param>
/// <param name="Destination">The folder made for it, absolute: the folder asked for, and the workflow's name under it.</param>
/// <param name="Files">
/// Each file written, relative to <paramref name="Destination"/> with '/', in the order it was written: every file the
/// definition names, then the definition; for a vendored one, every file of its directory, then the note of where it
/// came from.
/// </param>
/// <param name="Vendored">
/// Where a vendored remote workflow came from: its repository, its ref, and the commit it resolved to - said where that
/// commit is the cache's last resolution standing in, or the ref a branch, which moves.
/// </param>
public sealed record WorkflowExport(string Workflow, bool Shipped, string Destination, IReadOnlyList<string> Files, string? Vendored = null);

/// <summary>
/// Copies a workflow - its yml and every file it names - into a folder of its own, so a person or a repository has it to
/// read and make their own version of: one a repository declares, once it holds, or the one this tool ships.
/// </summary>
/// <remarks>
/// The files are the ones the definition names (<see cref="WorkflowFiles.AllUnder"/>), the one list <c>validate-workflow</c>
/// and <c>init</c> read, so a file a repository took out with its step is not exported, and one the definition names is
/// never left behind; a use of a workflow outside the workflow's folder, or of a remote one, is refused, for the folder
/// does not hold it. A declared workflow's files are copied byte for byte and each proved where it lands
/// (<see cref="VerifiedFileCopy"/>); the shipped one's are written as <c>init</c> writes them.
/// </remarks>
public interface IWorkflowExportService : DssHarness.Core.Application.IUseCase
{
    /// <summary>Exports the workflow <paramref name="request"/> names into a folder of its own under the folder it gives.</summary>
    /// <param name="startDirectory">A directory inside the repository, whose configuration declares the workflow; unread where it is the shipped one.</param>
    /// <param name="request">What is exported, and where to.</param>
    /// <param name="cancellationToken">Stops the export, taking back what it wrote.</param>
    /// <returns>Where the workflow was written, and each file.</returns>
    /// <exception cref="HarnessException">
    /// A workflow nobody declared, the shipped one asked for under a name it does not have, no workflow named, a workflow
    /// name or <c>--shipped</c> given beside <c>--from</c>, or no folder to write to (<see cref="HarnessExit.UsageError"/>);
    /// a declared workflow that does not hold (<see cref="HarnessExit.ConfigInvalid"/>, naming each problem); a workflow
    /// that uses one outside its folder or a remote one, a vendored directory holding a file named as the note, a folder
    /// for it that is there already or could not be looked at, or a file that could not be written
    /// (<see cref="HarnessExit.Refused"/>); stopped while it wrote (<see cref="HarnessExit.Cancelled"/>, where
    /// what it wrote could not be taken back; otherwise the cancellation itself). What was written before a failure is
    /// taken back, every folder the export made with it, or the refusal says why it could not be.
    /// </exception>
    Task<WorkflowExport> ExportAsync(string startDirectory, WorkflowExportRequest request, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IWorkflowExportService"/>
public sealed class WorkflowExportService(
    IWorkflowValidationService validation,
    IFileSystem fileSystem,
    IProcessContext process,
    DssHarness.Core.Repository.IHarnessContextLoader? contextLoader = null,
    WorkflowRemoteFetcher? remotes = null,
    WorkflowCache? cache = null) : IWorkflowExportService
{
    /// <summary>The command this service reports under.</summary>
    public const string CommandName = "export-workflow";

    /// <summary>The name of the note a vendored remote workflow carries, saying where it came from.</summary>
    public const string VendoredNote = "source.md";

    private readonly IWorkflowValidationService _validation = validation;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IProcessContext _process = process;
    private readonly DssHarness.Core.Repository.IHarnessContextLoader? _contextLoader = contextLoader;
    private readonly WorkflowRemoteFetcher? _remotes = remotes;
    private readonly WorkflowCache? _cache = cache;

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

        if (request.From is { } vendored)
        {
            // The remote workflow --from names is the one exported, into a folder named for its directory: a workflow
            // name or --shipped beside it would name another, and is never passed over in silence.
            if (request.Workflow is not null || request.Shipped)
            {
                throw new HarnessException(
                    HarnessExit.UsageError,
                    $"--from names the workflow it vendors, and it is given with {(request.Workflow is not null ? $"the workflow '{request.Workflow}'" : "--shipped")} as well; "
                        + $"'{ToolPackage.Command} {CommandName} --from <use> --to <dir>' vendors a remote one, and '{ToolPackage.Command} {CommandName} <workflow> --to <dir> [--shipped]' exports one of the repository's or this tool's. Nothing was written.");
            }

            var from = await VendoredAsync(startDirectory, vendored, cancellationToken).ConfigureAwait(false);

            return await WriteAsync(new WorkflowExportRequest(from.Folder, request.To, Shipped: false, From: vendored), from.Files, from.Said, cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(request.Workflow))
        {
            throw new HarnessException(
                HarnessExit.UsageError,
                $"no workflow is named; '{ToolPackage.Command} {CommandName} <workflow> --to <dir> [--shipped]' exports one of the repository's or this tool's, and '{ToolPackage.Command} {CommandName} --from <use> --to <dir>' vendors a remote one. Nothing was written.");
        }

        var files = request.Shipped
            ? Shipped(request.Workflow)
            : await DeclaredAsync(startDirectory, request.Workflow, cancellationToken).ConfigureAwait(false);

        return await WriteAsync(request, files, vendored: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What vendoring a remote workflow read: where it came from, said as one line; its folder's name; and its files.</summary>
    private sealed record Vendored(string Said, string Folder, IReadOnlyList<(string File, Func<string, Task> Write)> Files);

    /// <summary>Writes <paramref name="files"/> into the folder of the workflow's own the request asks for, taking back what a failure leaves.</summary>
    private async Task<WorkflowExport> WriteAsync(WorkflowExportRequest request, IReadOnlyList<(string File, Func<string, Task> Write)> files, string? vendored, CancellationToken cancellationToken)
    {
        var destination = Path.Combine(Path.GetFullPath(request.To, _process.CurrentDirectory), request.Workflow!);
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
                // Taken back whole, with every folder the export made above it that is still empty: a folder with part
                // of a workflow in it would read as a workflow that names files it does not have, and a folder above it
                // another export has since written into is that export's.
                var left = TakeBack(destination, made);

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

        return new WorkflowExport(request.Workflow!, request.Shipped, destination, written, vendored);
    }

    /// <summary>
    /// The remote workflow <paramref name="source"/> names, fetched at its resolved commit: every file of its directory,
    /// read from the workflow cache, and the note that says where it came from - a team can review what is written and
    /// use it as <c>./</c>.
    /// </summary>
    private async Task<Vendored> VendoredAsync(string startDirectory, string source, CancellationToken cancellationToken)
    {
        if (_contextLoader is null || _remotes is null || _cache is null)
        {
            throw new HarnessException(HarnessExit.Refused, $"'{source}' names a remote workflow, and this export reaches no fetcher for one");
        }

        if (WorkflowRemote.Source(source) is not { } parsed)
        {
            throw new HarnessException(HarnessExit.UsageError, $"'{source}' is no remote workflow source: an owner, a repository, a workflow file's path in it, and a ref after @");
        }

        var context = await _contextLoader.LoadAsync(startDirectory, cancellationToken).ConfigureAwait(false);
        var resolution = await _remotes.ResolveAsync(parsed, context.Config.WorkflowSources, context.Layout.MainCheckoutRoot, Environment.GetEnvironmentVariable, recorded: null, cancellationToken).ConfigureAwait(false);
        var entry = resolution.Entry;
        var folder = parsed.Directory[(parsed.Directory.LastIndexOf('/') + 1)..];
        var files = new List<(string, Func<string, Task>)>();

        foreach (var file in entry.Files)
        {
            // As the bytes the forge gave, hashed again as they are read: a vendored file is the commit's, byte for byte,
            // whatever it holds - a byte order mark, or bytes that are no text at all.
            var bytes = _cache.ReadBytes(entry, file.Path) ?? throw new HarnessException(
                HarnessExit.Refused,
                $"'{source}' resolved to the commit '{entry.Commit}', whose cache entry names no '{file.Path}'");
            var relative = file.Path[entry.Path.Length..].TrimStart('/');

            // The note of where the workflow came from is written beside its files, never over one of them.
            if (string.Equals(relative, VendoredNote, StringComparison.OrdinalIgnoreCase))
            {
                throw new HarnessException(
                    HarnessExit.Refused,
                    $"'{source}' holds a '{relative}' of its own in its directory, the name the note of where a vendored workflow came from is written as; {CommandName} writes that note beside a workflow's files and never over one of them. Nothing was written.");
            }

            files.Add((relative, path =>
            {
                _fileSystem.CreateDirectory(Path.GetDirectoryName(path)!);
                return _fileSystem.WriteAllBytesAtomicAsync(path, bytes, cancellationToken);
            }));
        }

        // How the commit came about, where it is worth a word: the cache's last resolution standing in for a ref that
        // could not be resolved, or a branch, which names another commit tomorrow.
        var how = resolution.FromLastResolved
            ? $", the workflow cache's last resolution of '{parsed.Ref}', standing in where the ref could not be resolved"
            : resolution.Moving ? $", where the branch '{parsed.Ref}' stood then - a branch moves" : string.Empty;
        var said = $"{parsed.Repository}/{parsed.File}@{parsed.Ref} at commit {entry.Commit}{how}";

        files.Add((VendoredNote, path =>
        {
            _fileSystem.CreateDirectory(Path.GetDirectoryName(path)!);
            _fileSystem.WriteAllTextAtomic(
                path,
                $"Vendored from {parsed.Repository}/{parsed.File}@{parsed.Ref}, which resolved to the commit {entry.Commit}{how}, and was fetched on {entry.FetchedAt.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}.\n"
                    + $"Use it as ./{folder}/{parsed.File[(parsed.File.LastIndexOf('/') + 1)..]} from a workflow of this repository, and take this note away when you have made it your own.\n");
            return Task.CompletedTask;
        }));

        return new Vendored(said, folder, [.. files]);
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

    /// <summary>
    /// Every file the definition names, each with '/', and the definition last: a folder that has its yml has every
    /// file it names - a used workflow's own yml among them, and each file under the used workflow's place in the set.
    /// </summary>
    /// <exception cref="HarnessException">
    /// A use names a workflow outside the workflow's folder, or a remote one (<see cref="HarnessExit.Refused"/>): the
    /// folder does not hold it, so a copy of the folder would name a workflow it has not got - said for each, a remote
    /// one with the <c>--from</c> that vendors it.
    /// </exception>
    private static IReadOnlyList<string> Named(WorkflowDefinition definition, string name)
    {
        var placed = WorkflowFiles.AllUnder(definition);

        if (placed.Beyond.Count > 0)
        {
            var said = placed.Beyond.Select(use => use.Remote
                ? $"step '{use.Path}' uses '{use.Source}', a remote workflow, which '{ToolPackage.Command} {CommandName} --from {use.Source} --to <dir>' vendors into the repository"
                : $"step '{use.Path}' uses '{use.Source}', a workflow outside its folder, which is exported on its own");

            throw new HarnessException(
                HarnessExit.Refused,
                $"the workflow '{name}' uses what its folder does not hold - {string.Join("; ", said)} - and {CommandName} copies the workflow's own folder, which would then name a workflow it has not got. Nothing was written.");
        }

        return [.. placed.Files, $"{name}{HarnessLayout.WorkflowFileExtension}"];
    }

    /// <summary>
    /// Removes the workflow's own folder, and all it holds, then each folder the export made above it, up to
    /// <paramref name="made"/>, while it is empty; says why where it cannot.
    /// </summary>
    private string? TakeBack(string destination, string made)
    {
        try
        {
            _fileSystem.DeleteDirectory(destination);

            // Each folder above it the export made, up to the outermost, while nothing else has come to be in it.
            for (var folder = destination; folder != made && Path.GetDirectoryName(folder) is { } parent; folder = parent)
            {
                if (_fileSystem.EnumerateFiles(parent, recursive: true).Any() || _fileSystem.EnumerateDirectories(parent).Any())
                {
                    break;
                }

                _fileSystem.DeleteDirectory(parent);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"what was written before it could not be taken back from '{made}': {ex.Message.TrimEnd('.')}";
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
        var vendored = export.Vendored is { } from ? $", vendored from {from}," : string.Empty;

        return CommandOutcome.Ok($"'{export.Workflow}'{shipped}{vendored} exported to '{export.Destination}': {ReportText.Counted(export.Files.Count, "file")}") with
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
            ["vendored"] = export?.Vendored,
        };
}
