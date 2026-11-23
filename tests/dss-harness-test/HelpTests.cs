using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DssHarness.Core.Anchors;
using DssHarness.Core.Ci;
using DssHarness.Core.Codebase;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Help;
using DssHarness.Core.Hosts;
using DssHarness.Core.Legs;
using DssHarness.Core.Mutations;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Runners;
using DssHarness.Core.Runs;
using DssHarness.Core.Sync;

namespace DssHarness.Tests;

/// <summary>
/// Help is treated as documentation, so it is tested like documentation: every
/// command must appear in it, and every documented value must come from the code
/// that produces it rather than from prose someone has to remember to update.
/// </summary>
public sealed partial class HelpTests
{
    /// <summary>A help topic's text, as the command prints it: the core's renderer asked directly, no tool spawned.</summary>
    private sealed record HelpText(string StandardOutput)
    {
        /// <summary>The text of one topic, or of the overview where none is asked.</summary>
        public static HelpText Of(string? topic) => new(HelpTopics.Render(topic));
    }

    [Fact]
    public async Task EveryListedCommand_HasItsOwnWorkingHelp()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var listed = await CliRunner.CommandsAsync(cancellationToken);

        Assert.NotEmpty(listed);

        foreach (var command in listed)
        {
            var result = await CliRunner.RunAsync([command, "--help"], cancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains(command, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task EveryListedCommand_HasADescription()
    {
        var result = await CliRunner.RunAsync(["--help"], TestContext.Current.CancellationToken);

        var lines = CliRunner.CommandLines(result.StandardOutput).ToList();
        Assert.NotEmpty(lines);

        foreach (var line in lines)
        {
            // "  name   description" - a command with no description is undocumented.
            var described = CommandLinePattern().Match(line);

            Assert.True(
                described.Success && described.Groups["description"].Value.Trim().Length > 10,
                $"command line has no usable description: '{line.Trim()}'");
        }
    }

    /// <summary>
    /// Every command the help tells a reader to type - in the overview's list, its examples and every topic - is spelt
    /// as the package installs it, lower case: a Linux filesystem is case-sensitive, and the product's name,
    /// capitalised, runs nothing there. The product's name in prose is no command, and stays as it is.
    /// </summary>
    /// <remarks>
    /// A line that stands in for a verb rather than naming one tells the reader to type the product's name just as
    /// surely: 'DssHarness &lt;command&gt;' is a command line, and was missed while only literal verbs were looked for.
    /// </remarks>
    [Fact]
    public async Task EveryCommandTheHelpSaysToType_IsSpeltAsTheToolInstallsIt()
    {
        var overview = HelpText.Of(null);
        var texts = new List<string> { overview.StandardOutput };

        foreach (Match topic in TopicPattern().Matches(overview.StandardOutput))
        {
            texts.Add(HelpText.Of(topic.Groups["topic"].Value).StandardOutput);
        }

        var commands = InstalledCommandPattern().Matches(overview.StandardOutput)
            .Select(match => match.Groups["verb"].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("legs", commands);
        Assert.Contains("help", commands);
        Assert.All(texts, text => Assert.DoesNotContain(
            ProductNamePattern().Matches(text).Select(match => match.Groups["verb"].Value),
            word => commands.Contains(word) || word.StartsWith('<')));
    }

    /// <summary>
    /// The usage line of a command's own help is spelt as the package installs it, as the help topics are.
    /// </summary>
    /// <remarks>
    /// Nothing here writes that line: the command-line library builds it from the executable's own name, which is
    /// why the assembly is named for the command and not for the product. One mechanism serves every command, so a
    /// few stand for all of them rather than starting a process per command.
    /// </remarks>
    [Theory]
    [InlineData()]
    [InlineData("init")]
    [InlineData("build")]
    [InlineData("help")]
    public async Task ACommandsOwnHelp_SpellsItsUsageAsTheToolInstallsIt(params string[] command)
    {
        var rendered = await CliRunner.RunAsync([.. command, "--help"], TestContext.Current.CancellationToken);

        var usage = CliRunner.UsageLine(rendered.StandardOutput);

        Assert.NotNull(usage);
        Assert.StartsWith(ToolPackage.Command + " ", usage, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every topic the overview advertises renders, and none is missing from it: the overview, the topic argument and the
    /// answer to an unknown topic all read one list of topics, which this reads back through the overview.
    /// </summary>
    [Fact]
    public async Task EveryTopicTheOverviewAdvertises_Renders()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var overview = HelpText.Of(null);


        var topics = TopicPattern().Matches(overview.StandardOutput).Select(match => match.Groups["topic"].Value).ToList();

        Assert.Contains("orchestrators", topics);
        Assert.Contains("ci", topics);

        foreach (var topic in topics)
        {
            var rendered = HelpText.Of(topic);

            Assert.True(rendered.StandardOutput.Length > 200, $"topic '{topic}' produced little output");
            Assert.DoesNotContain("Unknown topic", rendered.StandardOutput, StringComparison.Ordinal);
        }

        var unknown = await CliRunner.RunAsync(["help", "not-a-topic"], cancellationToken);
        Assert.Contains($"Try: {string.Join(", ", topics)}.", unknown.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownTopic_SaysSoAndListsTheRealOnes()
    {
        var result = await CliRunner.RunAsync(
            ["help", "not-a-topic"],
            TestContext.Current.CancellationToken);

        // A mistyped topic is a usage error. Reporting it on stdout with exit 0 would
        // break both "zero always means success" and the pipeable-output rule.
        Assert.Equal(HarnessExit.UsageError, result.ExitCode);
        Assert.Contains("Unknown topic", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("exit-codes", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown topic", result.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// A known topic is recognised and rendered: what the command writes to standard output with exit 0, where an unknown
    /// one is written to standard error as a usage error.
    /// </summary>
    [Fact]
    public async Task KnownTopic_IsRecognised_AndRendersItsText()
    {
        var recognised = HelpTopics.TryRender("layout", out var text);

        Assert.True(recognised);
        Assert.Contains(".harness-config", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The README's sentence of what the codebase graph reads names every implementation the registry holds: the table here
    /// holds the words that name each, its languages the registry's every one, so an implementation the sentence does not name
    /// - one written since, or one left out - is a red test.
    /// </summary>
    [Fact]
    public void TheReadmesGraphSentence_NamesEveryImplementationTheRegistryHolds()
    {
        var named = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["csharp"] = "C# through the compiler's own parser",
            ["msbuild"] = "MSBuild's projects and solutions",
            ["c"] = "C and C++ through a structural scanner",
            ["cpp"] = "C and C++ through a structural scanner",
            ["dart"] = "Dart through the same scanner",
            ["pubspec"] = "each Dart package's `pubspec.yaml`",
            ["javascript"] = "JavaScript and TypeScript through it too",
            ["typescript"] = "JavaScript and TypeScript through it too",
            ["npm"] = "`package.json`",
            ["tsconfig"] = "`tsconfig.json` and `jsconfig.json`",
            ["php"] = "PHP through it too",
            ["composer"] = "`composer.json`",
            ["python"] = "Python through it too",
            ["css"] = "style sheets through it too",
            ["html"] = "an HTML page's sections",
            ["vue"] = "a Vue component's template, script and styles",
            ["razor"] = "Razor's views and Blazor's components",
            ["json"] = "data files of JSON, YAML and XML",
            ["yaml"] = "data files of JSON, YAML and XML",
            ["xml"] = "data files of JSON, YAML and XML",
            ["markdown"] = "Markdown's headings",
            ["listed"] = "every other file by its name and size",
        };
        var readme = Prose.Document("README.md");
        var from = readme.IndexOf("each declaration a language's implementation reads", StringComparison.Ordinal);
        var sentence = readme[from..readme.IndexOf("and the edges between them", from, StringComparison.Ordinal)];

        Assert.Equal(CodeGraphExtractors.OfThisBuild.All.Select(extractor => extractor.Language).Order(StringComparer.Ordinal), named.Keys.Order(StringComparer.Ordinal));
        Assert.All(named, pair => Assert.Contains(pair.Value, sentence, StringComparison.Ordinal));
    }

    [GeneratedRegex(@"^\s{2}(?<name>\S+)(\s+\[?<\S+)*\s{2,}(?<description>.+)$")]
    private static partial Regex CommandLinePattern();

    [GeneratedRegex(ToolPackage.Command + @" help (?<topic>[a-z-]+)")]
    private static partial Regex TopicPattern();

    [GeneratedRegex(@"^\s{2}" + ToolPackage.Command + @" (?<verb>[a-z-]+)", RegexOptions.Multiline)]
    private static partial Regex InstalledCommandPattern();

    /// <summary>The product's name followed by a verb, or by something standing in for one.</summary>
    [GeneratedRegex(ToolPackage.Id + @" (?<verb><[a-z-]+>|[a-z-]+\b)")]
    private static partial Regex ProductNamePattern();
}
