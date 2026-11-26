using System.Text.Json.Nodes;
using DssHarness.Core.Hosts;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// <c>--output-logs &lt;file&gt;</c> as the command line takes it, on each of the three commands that drive a session of a
/// run: the zip written only where it is given, at every end - a refusal among them - and named in the text and in
/// <c>--json</c>; a path it cannot be written at refused before anything runs.
/// </summary>
public sealed class OutputLogsCliTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Given the option, each command writes the zip and names it - in its document's records and as the last line naming
    /// its records - the zip holding the command line as typed and the document as printed; at its next end too, refused
    /// or not: the start once its run's name is taken, the resume beside the open question, the answer once none is open.
    /// </summary>
    [Theory]
    [InlineData(FlowService.RunCommand, FlowExit.Paused, HarnessExit.Refused, false)]
    [InlineData(FlowService.ResumeCommand, HarnessExit.Refused, HarnessExit.Refused, true)]
    [InlineData(FlowService.AnswerCommand, HarnessExit.Success, HarnessExit.Refused, true)]
    public async Task EachCommand_GivenTheOption_WritesTheZip_NamedInItsTextAndItsJson_ARefusalAmongThem(string command, int first, int second, bool reachedAgain)
    {
        var (repository, _) = await FlowServiceTests.RepositoryAsync(FlowOutputLogsTests.Paused);
        using var disposed = repository;
        using var outside = new TempDirectory();
        await SceneAsync(command, repository);
        var json = outside.Combine("json.zip");

        var asked = await CliRunner.RunAsync([.. Line(command), "--json", FlowOutputLogs.Option, json, "-C", repository.Path], Token);

        CliRunner.ExitedWith(first, asked);

        var document = JsonNode.Parse(asked.StandardOutput)!;
        var manifest = FlowOutputLogsTests.Manifest(json);

        Assert.Equal(json, document["records"]![FlowOutputLogs.Record]!.GetValue<string>());
        Assert.Equal((command, first), (manifest["command"]!.GetValue<string>(), manifest["exitCode"]!.GetValue<int>()));
        Assert.Equal([ToolPackage.Command, .. Line(command), "--json", FlowOutputLogs.Option, json, "-C", repository.Path], Values(manifest["commandLine"]!));
        Assert.True(JsonNode.DeepEquals(document, manifest["output"]!["document"]), $"{document.ToJsonString()} was printed; the zip holds {manifest["output"]!["document"]?.ToJsonString()}");
        Assert.Equal("r1", manifest["run"]?["name"]?.GetValue<string>());

        var text = outside.Combine("text.zip");
        var said = await CliRunner.RunAsync([.. Line(command), FlowOutputLogs.Option, text, "-C", repository.Path], Token);

        CliRunner.ExitedWith(second, said);

        var again = FlowOutputLogsTests.Manifest(text);
        var lines = Lines(said.StandardOutput + said.StandardError);

        Assert.Contains($"{command}: {FlowOutputLogs.Record}: {text}", lines);
        Assert.Equal(lines.Order(StringComparer.Ordinal), Values(again["output"]!["lines"]!).Order(StringComparer.Ordinal));
        Assert.Equal(second, again["exitCode"]!.GetValue<int>());
        Assert.Equal(reachedAgain ? "r1" : null, again["run"]?["name"]?.GetValue<string>());
    }

    /// <summary>Without the option, no command writes a zip, and none names one.</summary>
    [Theory]
    [InlineData(FlowService.RunCommand)]
    [InlineData(FlowService.ResumeCommand)]
    [InlineData(FlowService.AnswerCommand)]
    public async Task WithoutTheOption_NoZipIsWritten_AndNoneIsNamed(string command)
    {
        var (repository, _) = await FlowServiceTests.RepositoryAsync(FlowOutputLogsTests.Paused);
        using var disposed = repository;
        using var outside = new TempDirectory();
        await SceneAsync(command, repository);

        var asked = await CliRunner.RunAsync([.. Line(command), "--json", "-C", repository.Path], Token, workingDirectory: outside.Path);

        Assert.DoesNotContain(FlowOutputLogs.Record, asked.StandardOutput + asked.StandardError, StringComparison.Ordinal);
        Assert.Null(JsonNode.Parse(asked.StandardOutput)!["records"]?[FlowOutputLogs.Record]);
        Assert.Empty(Directory.EnumerateFiles(outside.Path, "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(repository.Path, "*.zip", SearchOption.AllDirectories));
    }

    /// <summary>
    /// A path the zip cannot be written at - here a directory - is refused before anything runs, in the command's own
    /// shape: no run made, the run's records untouched, no zip named.
    /// </summary>
    [Theory]
    [InlineData(FlowService.RunCommand)]
    [InlineData(FlowService.ResumeCommand)]
    [InlineData(FlowService.AnswerCommand)]
    public async Task AnUnusablePath_IsRefusedBeforeAnythingRuns_InTheCommandsOwnShape(string command)
    {
        var (repository, _) = await FlowServiceTests.RepositoryAsync(FlowOutputLogsTests.Paused);
        using var disposed = repository;
        using var outside = new TempDirectory();
        await SceneAsync(command, repository);
        var orchestrators = Path.Combine(repository.Path, HarnessLayout.OrchestratorsDirectoryName);
        var before = Snapshot(orchestrators);

        var asked = await CliRunner.RunAsync([.. Line(command), "--json", FlowOutputLogs.Option, outside.Path, "-C", repository.Path], Token);

        CliRunner.ExitedWith(HarnessExit.Refused, asked);

        var document = JsonNode.Parse(asked.StandardOutput)!;
        var refusal = $"{FlowOutputLogs.Option} names '{outside.Path}', which is a directory; name the file the zip is written to. Nothing was started.";

        Assert.Equal(HarnessExit.Refused, document["exitCode"]!.GetValue<int>());
        Assert.Contains(refusal, asked.StandardOutput.Replace("\\\\", "\\", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains($"{command}: FAIL - {refusal}", Lines(asked.StandardError));
        Assert.Null(document["records"]?[FlowOutputLogs.Record]);
        Assert.Equal(before, Snapshot(orchestrators));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside.Path));
    }

    /// <summary>The command line of <paramref name="command"/> over run r1: a start of it, a resume, an answer to q1.</summary>
    private static string[] Line(string command)
        => command switch
        {
            FlowService.RunCommand => [command, FlowServiceTests.Workflow, "--run", "r1"],
            FlowService.ResumeCommand => [command, "r1"],
            _ => [command, "r1", "go"],
        };

    /// <summary>Where <paramref name="command"/> goes on with a run, the run r1 paused on q1, started as a person starts it.</summary>
    private static async Task SceneAsync(string command, FlowServiceTests.Repository repository)
    {
        if (command == FlowService.RunCommand)
        {
            return;
        }

        CliRunner.ExitedWith(FlowExit.Paused, await CliRunner.RunAsync([.. Line(FlowService.RunCommand), "-C", repository.Path], Token));
    }

    /// <summary>Every file under <paramref name="directory"/> with its bytes, as one text; empty where there is none.</summary>
    private static string Snapshot(string directory)
        => !Directory.Exists(directory)
            ? string.Empty
            : string.Join(
                "\n",
                Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                    .Order(StringComparer.Ordinal)
                    .Select(file => $"{Path.GetRelativePath(directory, file)} {Convert.ToBase64String(File.ReadAllBytes(file))}"));

    private static List<string> Values(JsonNode array) => [.. array.AsArray().Select(value => value!.GetValue<string>())];

    private static List<string> Lines(string text) => [.. text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0)];
}
