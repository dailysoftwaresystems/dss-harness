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
    /// The agent-definitions topic says what an agent file is, the keys it takes, that a definition is the level below
    /// a step's, that a fallback is no key of one, and that the shipped files name no model, tier or effort.
    /// </summary>
    [Fact]
    public async Task TheAgentDefinitionsTopic_SaysWhatAnAgentFileIsAndTakes()
    {
        var topic = HelpText.Of("agent-definitions").StandardOutput;

        Assert.Equal(topic, HelpText.Of("definitions").StandardOutput);
        Assert.Contains(HelpTopics.Described, described => described.Name == "agent-definitions");

        var prose = Prose.Words(topic);

        Assert.Contains(
            "A definition is the level below a step's: where a step names a tier, a model, an effort or a client, its own "
                + "wins; where it names none, the definition's stands, each on its own.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "A fallback is no key of a definition or of a step: it is the order of the tier's models in tierConfig, each "
                + "model's keys before the next model.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "The sample agent files name no model, tier or effort, and no shipped one does: they are a step's "
                + "configuration, and the shipped workflow names them on its steps, where a person reads and changes them.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("a step's prompt {skill.<name>}", prose, StringComparison.Ordinal);
    }
}
