using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DssHarness.Core.Anchors;
using DssHarness.Core.Ci;
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

public sealed partial class HelpTests
{
    /// <summary>
    /// check-ci-legs assumes no workflow of its own, and its topic says what it reads a workflow by instead.
    /// </summary>
    [Fact]
    public async Task CiTopic_SaysCheckCiLegsReadsOnlyWhatTheSettingsDeclare()
    {
        var result = HelpText.Of("ci");

        Assert.Contains("It knows no workflow of its own", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("with {leg} standing for the leg's", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("A green leg past 80% of its", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "Every call of gh, whoever makes it, and every git command is started so that it cannot ask anything (GH_PROMPT_DISABLED, "
                + "GCM_INTERACTIVE, GIT_TERMINAL_PROMPT), and a call of gh is ended where it has not answered within the timeoutMinutes of "
                + $"the workflow step that made it, where the step says one, else within {ReportText.Limit(GhProcess.Limit)}: "
                + $"a call that did not answer in time is not done ({HarnessExit.Incomplete}), and asked again it may answer.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
        Assert.Equal(UnattendedCommands.Variables.Keys.Order(StringComparer.Ordinal), ["GCM_INTERACTIVE", "GH_PROMPT_DISABLED", "GIT_TERMINAL_PROMPT"]);

        // The example reads what it says it reads: a leg and its budget from a whole job name, and the leg from one
        // the forge cut short, which it would otherwise not read at all.
        var example = new Regex(
            JsonSerializer.Deserialize<string>(ExamplePattern().Match(result.StandardOutput).Groups["pattern"].Value)!,
            RegexOptions.None,
            TimeSpan.FromSeconds(1));
        var whole = example.Match("test (linux-gcc-debug, 45)");
        var cut = example.Match("test (linux-gcc-debug-with-a-name-long-enou");

        Assert.Equal(("linux-gcc-debug", "45"), (whole.Groups["leg"].Value, whole.Groups["budget"].Value));
        Assert.Equal(("linux-gcc-debug-with-a-name-long-enou", false), (cut.Groups["leg"].Value, cut.Groups["budget"].Success));
    }

    [GeneratedRegex(@"""legJobPattern"": (?<pattern>""(?:[^""\\]|\\.)*"")")]
    private static partial Regex ExamplePattern();
}
