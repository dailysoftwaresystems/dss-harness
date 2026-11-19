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
    /// The skills topic says the placeholder, the three places and their order beside Claude Code's own, what is put
    /// into a prompt, the folder handed to the step, the keys of the frontmatter and the body's reads and bounds, and
    /// what is not looked for - and the README and the layout topic say the same of it.
    /// </summary>
    [Fact]
    public async Task TheSkillsTopic_SaysThePlaceholderThePlacesAndTheFolderHandedToTheStep()
    {
        var topic = HelpText.Of("skills").StandardOutput;

        Assert.Equal(topic, HelpText.Of("skill").StandardOutput);
        Assert.Contains(HelpTopics.Described, described => described.Name == "skills");

        var prose = Prose.Words(topic);

        Assert.Contains(
            "A reason step's prompt says {skill.<name>} where a skill's text goes, and the engine puts the text there "
                + "before the prompt is sent, on every client alike",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "The repository's come before the user's, so a run reads the same skill on every machine that has the "
                + "repository; Claude Code's own order is the other way round.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "A SKILL.md that is there and cannot be used is refused: the next place is not tried in its stead, for a "
                + "broken skill of the repository must not turn into the user's without a word.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "What is put into the prompt is the body, not the frontmatter, after its substitutions:",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            $"The folder is copied whole into what the step's agent is handed, the step's own {DssHarness.Core.Orchestration.OrchestratorLayout.HandedDirectoryName}/skills/<name>/, in place of any "
                + $"earlier copy; that path is the directory the text states and what ${{CLAUDE_SKILL_DIR}} becomes. It is a root the agent reads, on every client, which the tool alone "
                + "writes, through no link - never the agent's work directory, where its commands write. A folder that holds a link, more than "
                + $"{DssHarness.Core.Orchestration.Flow.AgentSkills.FileLimit} files or more than 16 MB is refused.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "It grants nothing: what an agent may do is its definition's and the policy's",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "refused: each changes what runs the skill in Claude Code, and text put into a prompt runs in the step's own "
                + "agent, on its model, under its policy. Any other key is refused too, naming the keys this tool knows.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "A skill that is missing or unusable fails loud, twice: when the definition is checked - validate-workflow, "
                + "and the start of run-orchestrator and resume-orchestrator (exit 12, nothing started, every problem of "
                + "every prompt together) - and when the step starts",
            prose,
            StringComparison.Ordinal);

        // The README says the same, and the layout topic names the tool's own place.
        var readme = Prose.Document("README.md");

        Assert.Contains(
            "A reason step's prompt says `{skill.<name>}` where a skill's text goes, and the engine puts the text there "
                + "before the prompt is sent, on every client alike (`help skills`)",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "The repository's come before the user's - Claude Code's own order is the other way round - and a `SKILL.md` "
                + "that is there and cannot be used is refused, the next place not tried in its stead.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "A skill that is missing or unusable fails loud, twice: when the definition is checked (`validate-workflow`, "
                + "and the start of `run-orchestrator` and `resume-orchestrator`; exit 12, nothing started, every problem "
                + "of every prompt together, each refusal naming every place looked for it), and when the step starts",
            readme,
            StringComparison.Ordinal);

        var layout = Prose.Words(HelpText.Of("layout").StandardOutput);

        Assert.Contains(
            "tracked; the tool's own place a step's prompt names a skill from; init creates nothing there",
            layout,
            StringComparison.Ordinal);
    }
}
