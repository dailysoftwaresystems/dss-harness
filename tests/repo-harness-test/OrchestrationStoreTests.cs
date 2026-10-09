using RepoHarness.Core.Orchestration;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// The records orchestrators and agents keep, read strictly: one that does not read as exactly what it must be is refused,
/// named with why and where, and never guessed at.
/// </summary>
public sealed class OrchestrationStoreTests
{
    /// <summary>An agent's record that does not read as one - in any of the ways a hand or a crash leaves it - is refused, naming its file.</summary>
    [Theory]
    [InlineData("not JSON")]
    [InlineData("a member no record has")]
    [InlineData("a member missing")]
    [InlineData("null where a value is declared")]
    [InlineData("a member written twice")]
    [InlineData("another kind")]
    [InlineData("closed with no closing")]
    [InlineData("a session ending in a line break")]
    [InlineData("a closing whose evidence is null")]
    [InlineData("a move of its base to that very base")]
    [InlineData("a move of its base to no commit")]
    public async Task AnAgentRecordThatDoesNotRead_IsRefused_NamingItsFile(string how)
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("ag", session: "sess1");
        var file = kit.Layout.AgentRecordFile("ag");
        var text = File.ReadAllText(file);
        var based = kit.Record("ag").Base;

        File.WriteAllText(file, how switch
        {
            "not JSON" => "{",
            "a member no record has" => text.Replace("{", "{\n  \"extra\": 1,", StringComparison.Ordinal),
            "a member missing" => text.Replace("  \"model\": \"model-b\",\n", string.Empty, StringComparison.Ordinal),
            "null where a value is declared" => text.Replace("\"model\": \"model-b\"", "\"model\": null", StringComparison.Ordinal),
            "a member written twice" => text.Replace("{", "{\n  \"model\": \"model-c\",", StringComparison.Ordinal),
            "another kind" => text.Replace("\"kind\": \"agent\"", "\"kind\": \"orchestrator\"", StringComparison.Ordinal),
            "closed with no closing" => text.Replace("\"state\": \"live\"", "\"state\": \"closed\"", StringComparison.Ordinal),
            "a closing whose evidence is null" => text.Replace(
                "\"state\": \"live\"",
                "\"state\": \"closed\",\n  \"abandoned\": false,\n  \"closing\": { \"at\": \"2026-09-29T00:00:00+00:00\", \"evidence\": null, \"stamp\": \"1:1\", \"held\": {} }",
                StringComparison.Ordinal),
            "a move of its base to that very base" => text.Replace("\"state\": \"live\"", $"\"state\": \"live\",\n  \"moving\": \"{based}\"", StringComparison.Ordinal),
            "a move of its base to no commit" => text.Replace("\"state\": \"live\"", "\"state\": \"live\",\n  \"moving\": \"somewhere\"", StringComparison.Ordinal),
            _ => text.Replace("\"session\": \"sess1\"", "\"session\": \"sess1\\n\"", StringComparison.Ordinal),
        });

        Assert.NotEqual(text, File.ReadAllText(file));

        var refused = Assert.Throws<HarnessException>(() => kit.Harness.OrchestrationStore.ReadAgent(kit.Layout, "ag"));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(file, refused.Message);
    }

    /// <summary>
    /// A record copied from another agent's directory names that agent, and is refused where it is kept: every command
    /// on it acts on the name its record gives, and would act on the other agent.
    /// </summary>
    [Fact]
    public async Task ARecordCopiedFromAnotherAgent_IsRefused_AndNothingActsOnIt()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("a1");
        OrchestrationKit.Write(kit.Layout.AgentDirectory("a2"), "agent.json", File.ReadAllText(kit.Layout.AgentRecordFile("a1")));

        var refused = await Assert.ThrowsAsync<HarnessException>(() => kit.DeleteAsync("a2", apply: true, discard: true));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains("it names agent 'a1' of 'o1', and it is kept as 'a2' of 'o1'", refused.Message);
        Assert.Equal(AgentStates.Live, kit.Record("a1").State);
    }

    /// <summary>A seed naming a path with no digest, or an item that is null, is refused: a fold subtracting a seed read as best it can be subtracts the wrong set.</summary>
    [Theory]
    [InlineData("{\n  \"seededAt\": \"2026-09-29T00:00:00+00:00\",\n  \"empty\": false,\n  \"paths\": { \"a.txt\": null }\n}\n")]
    [InlineData("{\n  \"seededAt\": \"2026-09-29T00:00:00+00:00\",\n  \"empty\": false,\n  \"paths\": {},\n  \"absent\": [ null ]\n}\n")]
    [InlineData("{\n  \"seededAt\": \"2026-09-29T00:00:00+00:00\",\n  \"empty\": false,\n  \"paths\": {},\n  \"absent\": [ \"../outside\" ]\n}\n")]
    public async Task ASeedThatDoesNotRead_IsRefused(string seed)
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("ag");
        File.WriteAllText(kit.Layout.SeedFile("ag"), seed);

        var refused = Assert.Throws<HarnessException>(() => kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag"));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(kit.Layout.SeedFile("ag"), refused.Message);
    }

    /// <summary>
    /// A record of the rows an agent's folds applied that names a row twice, or declares one with a status or a priority
    /// that does not read, is refused naming its file: every row it records was one the registries took.
    /// </summary>
    [Theory]
    [InlineData("bogus", "P2", "D-AREA-TOPIC-TWO")]
    [InlineData("open", "P9", "D-AREA-TOPIC-TWO")]
    [InlineData("open", "P2", "D-AREA-TOPIC-ONE")]
    public async Task AnAppliedRowsRecordThatDoesNotRead_IsRefused_NamingItsFile(string status, string priority, string second)
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("ag");
        var file = kit.Layout.AppliedRowsFile("ag");

        string Record(string status, string priority, string second)
            => "{\n  \"rows\": [\n"
                + "    { \"id\": \"D-AREA-TOPIC-ONE\", \"status\": \"open\", \"trigger\": \"t\", \"closingWork\": \"c\", \"crossRefs\": \"r\", \"priority\": \"P2\" },\n"
                + $"    {{ \"id\": \"{second}\", \"status\": \"{status}\", \"trigger\": \"t\", \"closingWork\": \"c\", \"crossRefs\": \"r\", \"priority\": \"{priority}\" }}\n"
                + "  ]\n}\n";

        File.WriteAllText(file, Record("closed", "P3", "D-AREA-TOPIC-TWO"));
        Assert.Equal(2, kit.Harness.OrchestrationStore.ReadAppliedRows(kit.Layout, "ag")!.Rows.Count);

        File.WriteAllText(file, Record(status, priority, second));

        var refused = Assert.Throws<HarnessException>(() => kit.Harness.OrchestrationStore.ReadAppliedRows(kit.Layout, "ag"));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(file, refused.Message);
    }
}
