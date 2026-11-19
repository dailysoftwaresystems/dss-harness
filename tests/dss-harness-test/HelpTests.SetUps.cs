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
    /// The set-ups topic, asked for by any of its names, says how a set-up is declared, what up and down run and exit
    /// with, and what a run asked to set up does with one; config names setUp, and exec no longer.
    /// </summary>
    [Theory]
    [InlineData("setups")]
    [InlineData("set-up")]
    [InlineData("up")]
    [InlineData("down")]
    public async Task SetUpsTopic_SaysHowOneIsDeclared_RunByHand_AndAroundARun(string topic)
    {
        var token = TestContext.Current.CancellationToken;
        var result = HelpText.Of(topic);
        var text = Prose.Words(result.StandardOutput);


        foreach (var said in new[]
        {
            "config.json names each set-up under setUp, with an up and a down command line",
            "a double-quoted word kept whole, and no shell between",
            "dssharness up <name> run its up line at the root of the tree, on this machine",
            "dssharness down <name> run its down line there",
            "test and run bring them up only when given --set-up; without it a run runs as it did.",
            "so a line must be safe to run twice.",
            "the run exits 21 where nothing failed.",
            "--input name=value gives an input its value, once per input, over its default;",
            "\"projects\": [ { \"name\": \"core\", \"test\": { \"all\": { \"setUp\": [\"e2e\"] } } } ]",
            "and 13 where a line a leg would run could not run on its system",
            "Where a run's step holds a leg's work inside the operating system's isolation under agentPolicy.isolation required "
                + "('help agent-clients'), the leg's set-ups' lines run outside it from an empty directory outside every tree, so a "
                + "relative argument is read there and finds nothing of the tree, and a line whose program or any word names a "
                + "place in a tree the agents wrote - a script of the repository's among them - skips the leg, nothing brought up.",
        })
        {
            Assert.Contains(said, text, StringComparison.Ordinal);
        }

        var config = Prose.Words((HelpText.Of("config")).StandardOutput);

        Assert.Contains("setUp what a run brings up around its work and down after, by name", config, StringComparison.Ordinal);
        Assert.DoesNotContain(" exec ", config, StringComparison.Ordinal);
    }
}
