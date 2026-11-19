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
    /// <summary>The layout topic says each run is named however it ended, and one refused before it had a directory by its id alone.</summary>
    [Fact]
    public async Task LayoutTopic_SaysEachRunIsNamed_ARefusedOneByItsIdAlone()
    {
        var result = HelpText.Of("layout");

        Assert.Contains(
            "Each names its run too, in its first line, 'run <id>', and as runId in --json, however it ended: a run refused "
            + "before it had a directory keeps no records, and is cited by that id alone.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The layout topic names the agent clients' key slot, in the workflows directory beside the workflows' own: init
    /// creates its placeholder and ignores what it holds, the keys are read from it and from nowhere else, and no
    /// workflow may take its name. The agent-clients topic holds a sample file for it, one line per appointed name
    /// saying what its value is, none filled in.
    /// </summary>
    [Fact]
    public async Task LayoutTopic_NamesTheAgentClientsKeySlot()
    {
        var result = HelpText.Of("layout");
        var prose = Prose.Words(result.StandardOutput);

        Assert.Contains(
            ".harness-config/workflows/.env/ contents ignored, .gitkeep tracked; the agent clients' keys, NAME=value files, every file merged, "
                + "no key read from anywhere else; no workflow may be named .env",
            prose,
            StringComparison.Ordinal);

        var clients = Prose.Words(HelpText.Of("agent-clients").StandardOutput);

        Assert.Contains("A sample for the providers init declares, none filled in:", clients, StringComparison.Ordinal);
        Assert.Contains("# .harness-config/workflows/.env/keys.env", clients, StringComparison.Ordinal);
        Assert.Contains("ANTHROPIC_API_KEY= # the Anthropic API key", clients, StringComparison.Ordinal);
        Assert.Contains("ZAI_API_KEY= # the Z.AI API key", clients, StringComparison.Ordinal);
    }

    /// <summary>
    /// The layout topic and the README's Layout block are one list: the topic says every place of it with what it holds,
    /// and the block is that list a line to a place, in the same order and the same words - the side-tasks' records, the
    /// skills, the key-spend record and a run's asks among them - so neither names a place the other leaves out.
    /// </summary>
    [Fact]
    public void TheLayoutTopic_AndTheReadmesLayoutBlock_AreOneList()
    {
        var layout = Prose.Words(HelpText.Of("layout").StandardOutput);

        foreach (var (path, text) in HelpTopics.LayoutEntries)
        {
            Assert.Contains($"{path} {text}", layout, StringComparison.Ordinal);
        }

        const string Opening = "## Layout\n\n```\n";
        var readme = File.ReadAllText(Path.Combine(Prose.RepositoryRoot(), "README.md")).Replace("\r\n", "\n", StringComparison.Ordinal);
        var body = readme[(readme.IndexOf(Opening, StringComparison.Ordinal) + Opening.Length)..];
        var block = body[..body.IndexOf("\n```", StringComparison.Ordinal)].Split('\n');

        Assert.Equal(HelpTopics.LayoutEntries.Select(entry => $"{entry.Path,-44} {entry.Text}"), block);
        Assert.Contains(
            (HarnessLayout.SideTasksDirectoryName + "/", "contents ignored, .gitkeep tracked; the side-tasks' records, in the main checkout, and never sent to a host by sync ('dssharness help side-tasks')"),
            HelpTopics.LayoutEntries);
    }

    /// <summary>
    /// The layout topic says how a leg's records are named - the logs of line, and records and logs in --json - and how
    /// the run a host ran a leg under is said: once, by the dispatcher, and as hostRunId.
    /// </summary>
    [Fact]
    public async Task LayoutTopic_SaysHowALegsRecordsAndAHostsRunAreNamed()
    {
        var result = HelpText.Of("layout");

        Assert.Contains(
            "Each leg that kept records has a line of its own naming its directory and the logs in it - 'logs of <leg>[ on <host>]' - as its verdict is said and in the run's closing lines, and as records and the phase logs as logs in --json, a host's paths with its home written as ~",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
        Assert.Contains(
            "A host runs a leg under a run of its own: the dispatcher names it once, '<leg>: run <id> on <host>', never relaying the host's own line, and the leg's --json holds it as hostRunId.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The layout topic says the worktrees root, the orchestrators directory and the slots a tree keeps its copies in
    /// are kept in git by their placeholders with everything made in them ignored, what that costs, and that sync
    /// withholds each.
    /// </summary>
    [Fact]
    public async Task LayoutTopic_SaysTheWorktreesRootTheOrchestratorsDirectoryAndEachSlotAreKeptInGit()
    {
        var result = HelpText.Of("layout");
        var text = Prose.Words(result.StandardOutput);

        foreach (var said in new[]
        {
            ".worktrees/ contents ignored, .gitkeep tracked; the worktrees, at worktrees.root, which init writes as this",
            ".orchestrators/ contents ignored, .gitkeep tracked; what each orchestrator and its agents keep, in the main checkout, "
                + "and never sent to a host by sync",
            ".m/ contents ignored, .gitkeep tracked; the tree's mutation workers, in every tree, never sent to a host by "
                + "sync ('dssharness help mutations')",
            ".m/<key><mark>-<n>/ ignored; a worker, a repository of its own, building in build/<key>, and its claim beside it",
            ".mutations/ ignored whole, with no placeholder, an empty one left there removed by init; where an earlier version "
                + "kept the tree's mutation workers, never made again: never sent to a host by sync nor deleted by one, and what is "
                + "left there removed by clean --former-workers, or with the tree",
            ".mutations, where an earlier version kept the tree's mutation workers before they moved to .m, keeps its rule in the "
                + "block for ever - the directory ignored whole, with no placeholder, since nothing is made there again - so what "
                + "that version left there, each worker a repository of its own, never reads as work to commit; sync withholds it "
                + "by name too, and no agent writes there. The placeholder that version's init wrote there, still empty, init "
                + "removes, the directory with it where nothing else is left, for the repository to commit its removal; one "
                + "holding anything is left, and said. clean --former-workers removes what is left there, as does whatever "
                + "removes the tree.",
            ".legs/ contents ignored, .gitkeep tracked; the copies of the tree its legs work in, in every tree, never sent to a "
                + "host by sync",
            ".side-tasks/ contents ignored, .gitkeep tracked; the side-tasks' records, in the main checkout, and never sent to a host "
                + "by sync ('dssharness help side-tasks')",
            "The worktrees root, the orchestrators directory, .m, .legs and .side-tasks are kept in git by their placeholders, so each "
                + "reads as not ignored itself while everything made in it is, and none can be a link: init names one it finds, and "
                + "writes nothing through it. Sync withholds each by name. Each copy in them is a whole copy of the tree, so a "
                + "program of the repository's own that walks the tree from its root - a census, a guard over every file - must "
                + "leave out what the block ignores, as git does, or it reads every file once more for each copy kept there.",
        })
        {
            Assert.Contains(said, text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The layout topic names where a run keeps a step no agent owns, and where an agent keeps its steps - a step's
    /// handoff among them - and its conversations with an API client, each from the layout's own names.
    /// </summary>
    [Fact]
    public async Task LayoutTopic_NamesWhereARunsStepsAndAnAgentsStepsAndSessionsAre()
    {
        var result = HelpText.Of("layout");
        var agent = $"{HarnessLayout.OrchestratorsDirectoryName}/<o>/{OrchestratorLayout.AgentsDirectoryName}/<agent>";

        foreach (var said in new[]
        {
            $"{HarnessLayout.OrchestratorsDirectoryName}/<o>/{OrchestratorLayout.StepsDirectoryName}/<instance>/ ignored; one step of a "
                + "run that no agent owns, the create-agent that makes an agent included",
            $"{agent}/{OrchestratorLayout.StepsDirectoryName}/<instance>/ ignored; one step the agent runs, or that runs in or for "
                + $"its worktree: everything of the step, its {OrchestratorLayout.HandoffFileName} where one stopped it",
            $"{agent}/{OrchestratorLayout.SessionsDirectoryName}/<id>/ ignored; API clients only: one conversation with the "
                + "provider, in the provider's own form",
        })
        {
            Assert.Contains(said, Prose.Words(result.StandardOutput), StringComparison.Ordinal);
        }
    }
}
