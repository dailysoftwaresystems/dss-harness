using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.ClaudeCode;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;

namespace DssHarness.Tests;

/// <summary>
/// What the engine answers a call Claude Code asks it about: each tool's call read as the action it is and decided by the
/// agent's policy - the very answer the policy gives, never one of the decider's own - a tool the launch was not given or
/// whose calls the engine cannot read refused by name, and a call that does not name what its action needs refused; and
/// the one table that says which tools a launch is given.
/// </summary>
public sealed class ClaudeToolDeciderTests : IDisposable
{
    private static readonly IReadOnlyList<string> All = ClaudeCodeTools.Of(AgentPolicyGround.EveryCapability);

    private readonly TempDirectory _temp = new();

    private readonly string _worktree;

    private readonly string _outside;

    private readonly string _slot;

    private readonly AgentPolicy _policy;

    public ClaudeToolDeciderTests()
    {
        _worktree = Directory.CreateDirectory(_temp.Combine("tree")).FullName;
        _outside = Directory.CreateDirectory(_temp.Combine("outside")).FullName;
        _slot = Directory.CreateDirectory(_temp.Combine("slot")).FullName;
        _policy = new AgentPolicy(
            new PassThroughFileSystem(new PhysicalFileSystem(FilePermissionsFactory.Create())),
            new HostPlatform(),
            PolicyGrounds.Of(_worktree, [_worktree], [_worktree], [_slot], [], []));
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// Each tool's call is the action its input names, and its answer is the policy's own for that action: a read's
    /// file, a search's path (the working directory where it names none), a write's and an edit's file, a notebook's,
    /// and a command whole.
    /// </summary>
    [Fact]
    public void EachToolsCall_IsTheActionItsInputNames_AndItsAnswerIsThePolicys()
    {
        var inside = Path.Combine(_worktree, "a.txt");
        var away = Path.Combine(_outside, "b.txt");

        foreach (var (tool, member, path, decide) in new (string, string, string, Func<string, AgentPolicyAnswer>)[]
        {
            ("Read", "file_path", inside, _policy.Read),
            ("Read", "file_path", away, _policy.Read),
            ("Read", "file_path", Path.Combine(_slot, "API_KEY"), _policy.Read),
            ("Grep", "path", _slot, _policy.Search),
            ("Glob", "path", inside, _policy.Search),
            ("Write", "file_path", away, _policy.Write),
            ("Edit", "file_path", inside, _policy.Write),
            ("NotebookEdit", "notebook_path", away, _policy.Write),
            ("Bash", "command", "sudo make install", _policy.Run),
            ("Bash", "command", "make test", _policy.Run),
        })
        {
            Assert.Equal(decide(path), Decide(tool, new JsonObject { [member] = path }));
        }

        Assert.IsType<AgentPolicyAnswer.Denied>(Decide("Write", new JsonObject { ["file_path"] = away, ["content"] = "x" }));
        Assert.Equal(AgentPolicyAnswer.Allow, Decide("Read", new JsonObject { ["file_path"] = inside }));
    }

    /// <summary>A search that names no path searches the working directory, and is decided as a search of it.</summary>
    [Fact]
    public void ASearchThatNamesNoPath_IsASearchOfTheWorkingDirectory()
    {
        Assert.Equal(AgentPolicyAnswer.Allow, Decide("Grep", new JsonObject { ["pattern"] = "x" }));
        Assert.Equal(_policy.Search(_slot), ClaudeToolDecider.Decide(_policy, All, "Glob", new JsonObject { ["pattern"] = "*" }, _slot));
        Assert.IsType<AgentPolicyAnswer.Denied>(ClaudeToolDecider.Decide(_policy, All, "Glob", new JsonObject { ["pattern"] = "*" }, _slot));
    }

    /// <summary>
    /// A search over a directory a credential slot lies beneath reads the slot, so it is refused, where a read of the
    /// directory itself names nothing in it.
    /// </summary>
    [Fact]
    public void ASearchOverADirectoryHoldingASlot_IsRefused_AReadOfItIsNot()
    {
        var denied = Assert.IsType<AgentPolicyAnswer.Denied>(Decide("Grep", new JsonObject { ["path"] = _temp.Path, ["pattern"] = "KEY" }));

        Assert.Equal("the credential slots", denied.Rule);
        Assert.IsType<AgentPolicyAnswer.Denied>(Decide("Glob", new JsonObject { ["path"] = _temp.Path, ["pattern"] = "**" }));
        Assert.Equal(AgentPolicyAnswer.Allow, _policy.Read(_temp.Path));
    }

    /// <summary>A tool the launch was not given is refused by name, naming what it was given, whatever its input.</summary>
    [Fact]
    public void AToolTheLaunchWasNotGiven_IsRefusedByName()
    {
        var denied = Assert.IsType<AgentPolicyAnswer.Denied>(
            ClaudeToolDecider.Decide(_policy, ["Read", "Grep"], "Write", new JsonObject { ["file_path"] = Path.Combine(_worktree, "a.txt") }, _worktree));

        Assert.Equal(ClaudeToolDecider.ToolsRule, denied.Rule);
        Assert.Equal(AgentPolicySource.Builtin, denied.Source);
        Assert.Equal("'Write' is no tool this agent was given; it was given: Read, Grep", denied.Reason);

        var none = Assert.IsType<AgentPolicyAnswer.Denied>(ClaudeToolDecider.Decide(_policy, [], "Read", null, _worktree));
        Assert.EndsWith("it was given: none", none.Reason, StringComparison.Ordinal);
    }

    /// <summary>A tool the launch was given but whose calls the engine cannot read is refused all the same.</summary>
    [Fact]
    public void AToolWhoseCallsTheEngineCannotRead_IsRefused()
    {
        var denied = Assert.IsType<AgentPolicyAnswer.Denied>(
            ClaudeToolDecider.Decide(_policy, ["WebFetch"], "WebFetch", new JsonObject { ["url"] = "https://example.com" }, _worktree));

        Assert.Equal(ClaudeToolDecider.ToolsRule, denied.Rule);
        Assert.Equal("'WebFetch' is a tool whose calls this tool cannot read, so none of them runs", denied.Reason);
    }

    /// <summary>
    /// A call that does not name what its action needs - no member, an empty one, one that is not text, or no input
    /// object at all - is refused naming the member, for nothing runs that the engine could not see whole.
    /// </summary>
    [Fact]
    public void ACallThatDoesNotNameWhatItsActionNeeds_IsRefused()
    {
        foreach (var input in new JsonNode?[]
        {
            null,
            new JsonObject(),
            new JsonObject { ["command"] = "" },
            new JsonObject { ["command"] = 3 },
            new JsonObject { ["command"] = new JsonArray("make") },
            new JsonArray("make"),
        })
        {
            var denied = Assert.IsType<AgentPolicyAnswer.Denied>(Decide("Bash", input));

            Assert.Equal("a call of 'Bash' that names no command cannot be decided, so it does not run", denied.Reason);
            Assert.Equal(AgentPolicySource.Builtin, denied.Source);
        }

        Assert.Equal(
            "a call of 'NotebookEdit' that names no notebook_path cannot be decided, so it does not run",
            Assert.IsType<AgentPolicyAnswer.Denied>(Decide("NotebookEdit", new JsonObject { ["file_path"] = "a" })).Reason);
    }

    /// <summary>
    /// What a call is of is one answer for the decision and for the denial that names it: the member its tool's row names,
    /// the working directory for a search that names no path, nothing for a call that names none or a tool the table does
    /// not name.
    /// </summary>
    [Fact]
    public void WhatACallIsOf_IsTheMemberItsToolsRowNames()
    {
        Assert.Equal("make test", ClaudeToolDecider.SubjectOf("Bash", new JsonObject { ["command"] = "make test", ["description"] = "x" }, _worktree));
        Assert.Equal("n.ipynb", ClaudeToolDecider.SubjectOf("NotebookEdit", new JsonObject { ["notebook_path"] = "n.ipynb" }, _worktree));
        Assert.Equal(_worktree, ClaudeToolDecider.SubjectOf("Grep", new JsonObject { ["pattern"] = "x" }, _worktree));
        Assert.Equal("src", ClaudeToolDecider.SubjectOf("Glob", new JsonObject { ["pattern"] = "*", ["path"] = "src" }, _worktree));
        Assert.Null(ClaudeToolDecider.SubjectOf("Read", new JsonObject { ["path"] = "a" }, _worktree));
        Assert.Null(ClaudeToolDecider.SubjectOf("WebFetch", new JsonObject { ["url"] = "https://example.com" }, _worktree));

        foreach (var (_, tools) in ClaudeCodeTools.Table)
        {
            Assert.All(tools, tool => Assert.Equal(tool, ClaudeCodeTools.Find(tool.Name)?.Tool));
        }
    }

    /// <summary>
    /// The one table: each capability names its own tools, each once; a launch is given its capabilities' tools in the
    /// table's order and the reporter the three that read; no tool that starts agents, loads skills, asks a person or
    /// plans is in it, nor the tool an answer arrives by; and the web names none until the engine's own tool server
    /// serves it.
    /// </summary>
    [Fact]
    public void TheTable_GivesEachCapabilityItsOwnTools_AndNeverAToolNoLaunchIsGiven()
    {
        var named = ClaudeCodeTools.Table.SelectMany(row => row.Tools).Select(tool => tool.Name).ToList();

        Assert.Equal(named.Count, named.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(Enum.GetValues<AgentCapability>(), ClaudeCodeTools.Table.Select(row => row.Capability));
        Assert.Empty(named.Intersect(ClaudeCodeTools.NeverOffered, StringComparer.Ordinal));
        Assert.DoesNotContain(ClaudeCodeTools.StructuredOutput, named);
        Assert.Empty(ClaudeCodeTools.Table.Single(row => row.Capability == AgentCapability.Web).Tools);

        Assert.Equal(["Read", "Grep", "Glob", "Write", "Edit", "NotebookEdit", "Bash"], All);
        Assert.Equal(["Read", "Grep", "Glob", "Bash"], ClaudeCodeTools.Of([AgentCapability.Run, AgentCapability.Read]));
        Assert.Empty(ClaudeCodeTools.Of([AgentCapability.Web]));
        Assert.Equal(["Read", "Grep", "Glob"], ClaudeCodeTools.Reporter);
        Assert.Equal(ClaudeCodeTools.Reporter, ClaudeCodeTools.Table.Single(row => row.Capability == AgentCapability.Read).Tools.Select(tool => tool.Name));
    }

    private AgentPolicyAnswer Decide(string tool, JsonNode? input)
        => ClaudeToolDecider.Decide(_policy, All, tool, input, _worktree);
}
