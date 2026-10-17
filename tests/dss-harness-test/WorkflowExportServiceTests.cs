using System.Text.Json.Nodes;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Help;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;
using NamedFiles = DssHarness.Core.Orchestration.Flow.WorkflowFiles;

namespace DssHarness.Tests;

/// <summary>
/// export-workflow: a workflow's yml and every file it names, copied into a folder of its own - a declared one once it
/// holds, byte for byte, or the one this tool ships, as init writes it - refused where that folder is there, and taken
/// back where a file could not be written, so nothing is ever left part written.
/// </summary>
public sealed class WorkflowExportServiceTests
{
    private const string Shipped = WorkflowTemplates.ShippedWorkflowName;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The shipped workflow, exported with --shipped, is the files init writes, each byte for byte, the yml written last,
    /// in a folder of its own named as the workflow under the one asked for, made with every folder above it; no
    /// repository is read.
    /// </summary>
    [Fact]
    public async Task TheShippedWorkflow_IsExportedAsInitWritesIt_IntoAFolderOfItsOwn()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        WorkflowTemplates.EnsureShipped(temp.Combine("init"), harness.FileSystem);

        var export = await Service(harness).ExportAsync(
            temp.Combine("no-repository"),
            new WorkflowExportRequest(Shipped, temp.Combine("exports", "deeper"), Shipped: true),
            Token);

        var destination = temp.Combine("exports", "deeper", Shipped);

        Assert.Equal((Shipped, true, destination), (export.Workflow, export.Shipped, export.Destination));
        Assert.Equal($"{Shipped}.yml", export.Files[^1]);
        Assert.Equal(WorkflowTemplates.Files.Order(StringComparer.Ordinal), export.Files.Order(StringComparer.Ordinal));
        Assert.Equal(export.Files.Order(StringComparer.Ordinal), FilesUnder(destination));

        foreach (var file in export.Files)
        {
            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(temp.Combine("init", Shipped), file), Token), await File.ReadAllBytesAsync(Path.Combine(destination, file), Token));
        }
    }

    /// <summary>
    /// A declared workflow is exported as the repository has it: every file its definition names copied byte for byte -
    /// a prompt with a byte-order mark and CRLF endings included - and the definition last; a file of its folder the
    /// definition does not name is left behind.
    /// </summary>
    [Fact]
    public async Task ADeclaredWorkflow_IsExportedByteForByte_WithTheFilesItsDefinitionNames_AndNoOther()
    {
        using var repository = await RepositoryAsync();
        var directory = Path.Combine(repository.Path, ".harness-config", "workflows", Shipped);
        byte[] edited = [0xEF, 0xBB, 0xBF, (byte)'o', (byte)'w', (byte)'n', (byte)'\r', (byte)'\n'];
        await File.WriteAllBytesAsync(Path.Combine(directory, "prompts", "plan.md"), edited, Token);
        await File.WriteAllTextAsync(Path.Combine(directory, "notes.md"), "a person's own notes\n", Token);

        var export = await Service(repository.Harness).ExportAsync(
            repository.Path,
            new WorkflowExportRequest(Shipped, repository.Tree.Combine("out"), Shipped: false),
            Token);

        var destination = repository.Tree.Combine("out", Shipped);
        var named = NamedFiles.All(WorkflowFileParser.Parse("x.yml", await File.ReadAllTextAsync(Path.Combine(directory, $"{Shipped}.yml"), Token)));

        Assert.Equal((Shipped, false, destination), (export.Workflow, export.Shipped, export.Destination));
        Assert.Equal([.. named, $"{Shipped}.yml"], export.Files);
        Assert.Equal(export.Files.Order(StringComparer.Ordinal), FilesUnder(destination));
        Assert.Equal(edited, await File.ReadAllBytesAsync(Path.Combine(destination, "prompts", "plan.md"), Token));
        Assert.False(File.Exists(Path.Combine(destination, "notes.md")));

        foreach (var file in export.Files)
        {
            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(directory, file), Token), await File.ReadAllBytesAsync(Path.Combine(destination, file), Token));
        }
    }

    /// <summary>
    /// A declared workflow that does not hold is refused as configuration that does not (12), naming each problem as
    /// validate-workflow does, and nothing is written - not even the folder asked for.
    /// </summary>
    [Fact]
    public async Task ADeclaredWorkflowThatDoesNotHold_IsRefusedAsConfigurationThatDoesNot_AndNothingIsWritten()
    {
        using var repository = await RepositoryAsync();
        File.Delete(Path.Combine(repository.Path, ".harness-config", "workflows", Shipped, "prompts", "ask.md"));
        var to = repository.Tree.Combine("out");

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Service(repository.Harness).ExportAsync(
            repository.Path,
            new WorkflowExportRequest(Shipped, to, Shipped: false),
            Token));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Equal(
            $"the workflow '{Shipped}' does not hold, so it was not exported and nothing was written; 'dssharness validate-workflow {Shipped}' says the same:{Environment.NewLine}"
                + $"  - .harness-config/workflows/{Shipped}/{Shipped}.yml: 'prompts/ask.md', the reporter's ask prompt's prompt, is not at '.harness-config/workflows/{Shipped}/prompts/ask.md'; write it there, beside the definition that names it",
            refusal.Message);
        Assert.False(Directory.Exists(to));
    }

    /// <summary>
    /// The folder the workflow would be written into, there already - a folder with a person's file in it, or a file -
    /// is refused (13), and nothing is written into it, over it or beside it.
    /// </summary>
    [Fact]
    public async Task AFolderForTheWorkflowThatIsThere_IsRefused_AndNothingIsWritten()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var mine = temp.WriteFile(Path.Combine("folder", Shipped, "mine.md"), "mine\n");
        temp.WriteFile(Path.Combine("file", Shipped), "a file\n");

        foreach (var to in new[] { temp.Combine("folder"), temp.Combine("file") })
        {
            var refusal = await Assert.ThrowsAsync<HarnessException>(() => Service(harness).ExportAsync(
                temp.Path,
                new WorkflowExportRequest(Shipped, to, Shipped: true),
                Token));

            Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
            Assert.Equal(
                $"'{Path.Combine(to, Shipped)}' is there already; export-workflow writes a workflow into a folder of its own that it makes, and never into or over one that is there. Nothing was written.",
                refusal.Message);
        }

        Assert.Equal([$"{Shipped}/mine.md"], FilesUnder(temp.Combine("folder")));
        Assert.Equal("mine\n", await File.ReadAllTextAsync(mine, Token));
        Assert.Equal("a file\n", await File.ReadAllTextAsync(temp.Combine("file", Shipped), Token));
    }

    /// <summary>
    /// The shipped workflow asked for by a name it does not have, and a workflow config.json does not declare, are each a
    /// usage error (10) that says what is there to export; a --to that names no folder is too.
    /// </summary>
    [Fact]
    public async Task AWorkflowThatIsNotThereToExport_OrNoFolder_IsAUsageError()
    {
        using var repository = await RepositoryAsync();
        var service = Service(repository.Harness);
        var to = repository.Tree.Combine("out");

        var shipped = await Assert.ThrowsAsync<HarnessException>(() => service.ExportAsync(repository.Path, new WorkflowExportRequest("mine", to, Shipped: true), Token));
        var declared = await Assert.ThrowsAsync<HarnessException>(() => service.ExportAsync(repository.Path, new WorkflowExportRequest("mine", to, Shipped: false), Token));
        var nowhere = await Assert.ThrowsAsync<HarnessException>(() => service.ExportAsync(repository.Path, new WorkflowExportRequest(Shipped, " ", Shipped: true), Token));

        Assert.Equal(HarnessExit.UsageError, shipped.ExitCode);
        Assert.Equal($"this tool ships one workflow, '{Shipped}', not 'mine'; 'dssharness export-workflow {Shipped} --shipped --to <dir>' exports it.", shipped.Message);
        Assert.Equal(HarnessExit.UsageError, declared.ExitCode);
        Assert.StartsWith("'mine' is not ", declared.Message, StringComparison.Ordinal);
        Assert.Equal(HarnessExit.UsageError, nowhere.ExitCode);
        Assert.Equal("--to names no folder; the workflow is written into a folder of its own under the one it names.", nowhere.Message);
        Assert.False(Directory.Exists(to));
    }

    /// <summary>A --to that is relative is taken from the directory the command was typed in, never from the repository's.</summary>
    [Fact]
    public async Task ARelativeFolder_IsTakenFromWhereTheCommandWasTyped()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var typed = Directory.CreateDirectory(temp.Combine("typed-here")).FullName;

        var export = await Service(harness, process: new FixedProcessContext { Fixed = typed }).ExportAsync(
            temp.Combine("repository"),
            new WorkflowExportRequest(Shipped, "out", Shipped: true),
            Token);

        Assert.Equal(Path.Combine(typed, "out", Shipped), export.Destination);
        Assert.True(File.Exists(Path.Combine(typed, "out", Shipped, $"{Shipped}.yml")));
    }

    /// <summary>
    /// A file that cannot be written is refused (13) naming it and why, and what was written before it is taken back with
    /// every folder the export made, those above the workflow's own included; where that cannot be, the refusal says so,
    /// with why.
    /// </summary>
    [Fact]
    public async Task AFileThatCannotBeWritten_IsRefused_AndWhatWasWrittenIsTakenBack_OrSaidWhereItCannotBe()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var takenBack = new FailingWrites(harness.FileSystem, failOn: 3, keep: false);
        var kept = new FailingWrites(harness.FileSystem, failOn: 3, keep: true);

        var refused = await Assert.ThrowsAsync<HarnessException>(() => Service(harness, takenBack).ExportAsync(temp.Path, new WorkflowExportRequest(Shipped, temp.Combine("a", "deeper"), Shipped: true), Token));
        var left = await Assert.ThrowsAsync<HarnessException>(() => Service(harness, kept).ExportAsync(temp.Path, new WorkflowExportRequest(Shipped, temp.Combine("b"), Shipped: true), Token));

        var third = WorkflowTemplates.Files.Count > 2 ? ExportOrder()[2] : throw new InvalidOperationException("the shipped set is too small");
        var a = temp.Combine("a", "deeper", Shipped);
        var b = temp.Combine("b", Shipped);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal($"'{third}' could not be written to '{a}': the disk is full; what was written before it was taken back, and '{temp.Combine("a")}' is not there.", refused.Message);
        Assert.False(Directory.Exists(temp.Combine("a")));

        Assert.Equal(HarnessExit.Refused, left.ExitCode);
        Assert.Equal($"'{third}' could not be written to '{b}': the disk is full; what was written before it could not be taken back from '{temp.Combine("b")}': it is held.", left.Message);
        Assert.Equal(2, FilesUnder(b).Count);
    }

    /// <summary>
    /// A failed export takes back its own folder, and of the folders it made above it only those still empty: one another
    /// export has since written into is that export's, and stays with what it holds.
    /// </summary>
    [Fact]
    public async Task AFailedExport_KeepsAFolderAboveItThatAnotherHasSinceWrittenInto()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var other = temp.Combine("out", "other", "other.yml");
        var meanwhile = new FailingWrites(harness.FileSystem, failOn: 3, keep: false, beforeFailing: () => temp.WriteFile(Path.Combine("out", "other", "other.yml"), "theirs\n"));

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Service(harness, meanwhile)
            .ExportAsync(temp.Path, new WorkflowExportRequest(Shipped, temp.Combine("out"), Shipped: true), Token));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.False(Directory.Exists(temp.Combine("out", Shipped)));
        Assert.Equal("theirs\n", await File.ReadAllTextAsync(other, Token));
    }

    /// <summary>
    /// An export stopped while it writes takes back what it wrote and is the cancellation it was; where what it wrote
    /// cannot be taken back, it ends as cancelled (130), saying what is left and why.
    /// </summary>
    [Fact]
    public async Task AnExportStoppedWhileItWrites_TakesBackWhatItWrote_OrSaysWhatIsLeft()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(harness, new FailingWrites(harness.FileSystem, failOn: 0, keep: false, stop: (2, first)))
            .ExportAsync(temp.Path, new WorkflowExportRequest(Shipped, temp.Combine("a"), Shipped: true), first.Token));
        var left = await Assert.ThrowsAsync<HarnessException>(() => Service(harness, new FailingWrites(harness.FileSystem, failOn: 0, keep: true, stop: (2, second)))
            .ExportAsync(temp.Path, new WorkflowExportRequest(Shipped, temp.Combine("b"), Shipped: true), second.Token));

        var third = ExportOrder()[2];

        Assert.False(Directory.Exists(temp.Combine("a")));
        Assert.Equal(HarnessExit.Cancelled, left.ExitCode);
        Assert.Equal(
            $"'{third}' could not be written to '{temp.Combine("b", Shipped)}': the export was stopped; what was written before it could not be taken back from '{temp.Combine("b")}': it is held.",
            left.Message);
    }

    /// <summary>A folder to export into that cannot be looked at is refused (13), saying why, and nothing is written.</summary>
    [Fact]
    public async Task AFolderThatCannotBeLookedAt_IsRefused()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Service(harness, new UnlookedKind(harness.FileSystem))
            .ExportAsync(temp.Path, new WorkflowExportRequest(Shipped, temp.Combine("out"), Shipped: true), Token));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Equal($"'{temp.Combine("out", Shipped)}' could not be looked at: the disk would not say. Nothing was written.", refusal.Message);
        Assert.False(Directory.Exists(temp.Combine("out")));
    }

    /// <summary>
    /// What export-workflow prints: where the workflow went and how many files, each file after it; asked for --json, the
    /// document alone, the same facts - and its stopped document, nothing exported, saying whether the shipped one was asked.
    /// </summary>
    [Fact]
    public void WhatItPrints_SaysWhereTheWorkflowWent_AndEachFile_AsLinesOrAsItsDocument()
    {
        var export = new WorkflowExport(Shipped, true, "/x/out/implementation", ["agents/a.md", "implementation.yml"]);

        var lines = WorkflowExportReports.Render(export, json: false);
        var json = WorkflowExportReports.Render(export, json: true);
        var document = JsonNode.Parse(Assert.Single(json.Data))!.AsObject();
        var stopped = JsonNode.Parse(WorkflowExportReports.Stopped(HarnessExit.Refused, "no", shipped: true))!.AsObject();

        Assert.Equal(HarnessExit.Success, lines.ExitCode);
        Assert.Equal("'implementation', as this tool ships it, exported to '/x/out/implementation': 2 file(s)", lines.Message);
        Assert.Equal(["agents/a.md", "implementation.yml"], lines.Details);
        Assert.Same(export, lines.Facts);
        Assert.Empty(lines.Data);
        Assert.False(lines.Quiet);
        Assert.True(json.Quiet);
        Assert.Null(json.Details);
        Assert.Equal("'mine' exported to '/x': 0 file(s)", WorkflowExportReports.Render(new WorkflowExport("mine", false, "/x", []), json: false).Message);

        Assert.Equal(["workflow", "shipped", "destination", "files"], document.Select(member => member.Key));
        Assert.Equal(Shipped, document["workflow"]!.GetValue<string>());
        Assert.True(document["shipped"]!.GetValue<bool>());
        Assert.Equal("/x/out/implementation", document["destination"]!.GetValue<string>());
        Assert.Equal(["agents/a.md", "implementation.yml"], document["files"]!.AsArray().Select(file => file!.GetValue<string>()));

        Assert.Equal(["exitCode", "summary", "workflow", "shipped", "destination", "files"], stopped.Select(member => member.Key));
        Assert.Null(stopped["workflow"]);
        Assert.True(stopped["shipped"]!.GetValue<bool>());
        Assert.Null(stopped["destination"]);
        Assert.Empty(stopped["files"]!.AsArray());
    }

    /// <summary>
    /// The command, end to end: the shipped workflow exported with --json answers with its document, every file written,
    /// and the same command again is refused (13), the folder being there.
    /// </summary>
    [Fact]
    public async Task TheCommand_ExportsTheShippedWorkflow_AndRefusesTheSameFolderAgain()
    {
        using var temp = new TempDirectory();
        string[] arguments = ["export-workflow", Shipped, "--shipped", "--to", temp.Combine("out"), "--json", "--directory", temp.Path];

        var first = await CliRunner.RunAsync(arguments, Token);
        var again = await CliRunner.RunAsync(arguments, Token);

        Assert.Equal(HarnessExit.Success, first.ExitCode);
        var document = JsonNode.Parse(first.StandardOutput)!.AsObject();
        Assert.Equal(temp.Combine("out", Shipped), document["destination"]!.GetValue<string>());
        Assert.Equal(WorkflowTemplates.Files.Count, document["files"]!.AsArray().Count);
        Assert.Equal(WorkflowTemplates.Files.Order(StringComparer.Ordinal), FilesUnder(temp.Combine("out", Shipped)));

        Assert.Equal(HarnessExit.Refused, again.ExitCode);
        Assert.Equal(HarnessExit.Refused, JsonNode.Parse(again.StandardOutput)!["exitCode"]!.GetValue<int>());
    }

    /// <summary>
    /// The help, the README and the architecture say what export-workflow copies, where to, and what it refuses, as the
    /// service does.
    /// </summary>
    [Fact]
    public void TheHelpTheReadmeAndTheArchitecture_SayWhatAnExportCopies_WhereTo_AndWhatItRefuses()
    {
        var help = Prose.Words(HelpTopics.Render("workflows"));
        var readme = Prose.Document("README.md");
        var architecture = Prose.Document("docs", "architecture.md");

        Assert.Contains(
            "'dssharness export-workflow <name> --to <dir>' copies a workflow - its yml and every file it names, and no other - into <dir>/<name>/, a folder of its own that must not be "
                + $"there (13 where it is): a declared one once it holds (12 where it does not), byte for byte, or, with --shipped, the {Shipped} workflow as this tool ships it, to make a "
                + "version of your own. A file that cannot be written takes back what was written before it, or says why it could not.",
            help,
            StringComparison.Ordinal);
        Assert.Contains(
            "`export-workflow <workflow> --to <dir>` copies a workflow - its yml and every file it names, and no other - into `<dir>/<workflow>/`, a folder of its own that must not be there (13 where it is)",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "A file that cannot be written takes back what was written before it, with every folder the export made, so no folder is left with "
                + "part of a workflow in it - or the refusal says why it could not.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains("A declared workflow is exported once its check holds (12 where it does not), from the definition and the directory that check found", architecture, StringComparison.Ordinal);
        Assert.Contains("The yml is written last, and a file that cannot be written takes back what was written before it", architecture, StringComparison.Ordinal);
    }

    /// <summary>The order the shipped workflow's files are exported in: every file its definition names, then the definition.</summary>
    private static IReadOnlyList<string> ExportOrder()
        => [.. NamedFiles.All(WorkflowTemplates.ShippedDefinition()), $"{Shipped}.yml"];

    /// <summary>Every file under <paramref name="directory"/>, relative to it with '/', in ordinal order.</summary>
    private static IReadOnlyList<string> FilesUnder(string directory)
        =>
        [
            .. Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(directory, file).Replace('\\', '/'))
                .Order(StringComparer.Ordinal),
        ];

    private static WorkflowExportService Service(HarnessFactory harness, IFileSystem? fileSystem = null, FixedProcessContext? process = null)
    {
        var files = fileSystem ?? harness.FileSystem;

        return new WorkflowExportService(
            new WorkflowValidationService(harness.ContextLoader, files, harness.Platform),
            files,
            process ?? new FixedProcessContext());
    }

    /// <summary>A repository of one commit whose config declares the shipped workflow, with its files written.</summary>
    private static async Task<Repository> RepositoryAsync()
    {
        var temp = new TempDirectory();
        var harness = new HarnessFactory();

        temp.WriteFile("file.txt", "content\n");
        await harness.InitializeHarnessAsync(temp.Path, Token, new HarnessConfig { Workflows = [Shipped] });
        await harness.CommitAllAsync(temp.Path, "initial", Token);
        WorkflowTemplates.EnsureShipped(Path.Combine(temp.Path, ".harness-config", "workflows"), harness.FileSystem);

        return new Repository(harness, temp);
    }

    /// <summary>The repository an export ran in, disposable with its tree.</summary>
    private sealed record Repository(HarnessFactory Harness, TempDirectory Tree) : IDisposable
    {
        public string Path => Tree.Path;

        public void Dispose() => Tree.Dispose();
    }

    /// <summary>The real file system, but one that will not say what is at any path, as a disk that refuses says.</summary>
    private sealed class UnlookedKind(IFileSystem inner) : PassThroughFileSystem(inner)
    {
        public override PathKind KindOf(string path) => throw new UnauthorizedAccessException("the disk would not say.");
    }

    /// <summary>
    /// The real file system, but the write numbered <c>failOn</c> fails with a full disk; where <c>keep</c> is set, the
    /// folder an export would take back is held, and cannot be.
    /// </summary>
    private sealed class FailingWrites(IFileSystem inner, int failOn, bool keep, (int After, CancellationTokenSource Source)? stop = null, Action? beforeFailing = null) : PassThroughFileSystem(inner)
    {
        private int _writes;

        public override void WriteAllTextAtomic(string path, string contents)
        {
            if (++_writes == failOn)
            {
                beforeFailing?.Invoke();
                throw new IOException("the disk is full.");
            }

            base.WriteAllTextAtomic(path, contents);

            // Stopped once this many files are written, as a person's interrupt would stop it between two.
            if (stop is { } asked && _writes == asked.After)
            {
                asked.Source.Cancel();
            }
        }

        public override void DeleteDirectory(string path)
        {
            if (keep)
            {
                throw new IOException("it is held.");
            }

            base.DeleteDirectory(path);
        }
    }
}
