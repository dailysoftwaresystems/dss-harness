using System.Globalization;
using System.Text;
using DssHarness.Core.Ci;
using DssHarness.Core.Anchors;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Codebase;
using DssHarness.Core.Build;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
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
using DssHarness.Core.Secrets;
using DssHarness.Core.SetUps;
using DssHarness.Core.Sync;
using DssHarness.Core.Tools;
using DssHarness.Core.Worktrees;

namespace DssHarness.Core.Help;

/// <summary>
/// The tool's reference material, one topic at a time: what does not fit in option descriptions. <c>--help</c> answers
/// "what can I type"; this answers "what do the answers mean", which is what a caller reaching for documentation actually
/// wants. The core owns the text, so a front end of its own answers the same questions the command line does.
/// </summary>
public static partial class HelpTopics
{
    /// <summary>Opening words of the unrecognised-topic message.</summary>
    public const string UnknownTopicPrefix = "Unknown topic";

    /// <summary>
    /// What a command asked for --json answers with where it stops before it has anything of its own to say, and the one
    /// exit where it answers with none: said in the overview for every command and wherever a listing's document is
    /// described, since most documents carry exitCode and summary only then.
    /// </summary>
    private const string StoppedDocument = "Asked for --json, a command answers with one document on every exit but two: --help, "
        + "-h or -? prints its usage instead, exit 0, whatever else its command line holds, even what the parser would otherwise "
        + "refuse; and --json given twice, a value each, asks for no document, so the parser's refusal is said alone, exit 10. Stopped before it has anything of its own to say - a command line the parser refuses, a --directory that is not "
        + "there, a repository not initialised, another usage error, or a refusal met before anything is weighed - it answers "
        + "with its own document holding nothing, exitCode and summary first, which the leg ledger, the agent document and the "
        + "check-mutations --list listing carry on every exit and every other document only then, or, where its document is an "
        + "array, with that array empty.";

    /// <summary>
    /// Every topic, in the order the overview lists them: the one list the overview, the topic argument, the answer to an
    /// unknown topic and the rendering all read, so no topic is ever advertised by one and missing from another.
    /// </summary>
    private static readonly IReadOnlyList<HelpTopic> Topics =
    [
        new("exit-codes", ["exit"], "What each exit code means", RenderExitCodes),
        new("config", ["configuration"], "What config.json declares", RenderConfig),
        new("legs", ["hosts", "emulators"], "Hosts, emulators, and how a leg finds where it runs", RenderLegs),
        new("space", ["disk", "clean"], "Freeing a full disk, and the room a build needs", RenderSpace),
        new("admission", ["heavy"], "How heavy legs share a machine: its slots, its memory and its room", RenderAdmission),
        new("worktrees", ["worktree"], "Naming rules, the path budget, and when deleting refuses", RenderWorktrees),
        new("orchestrators", ["orchestrator", "agents", "agent"], "Orchestrators, their agents, and folding an agent's work", RenderOrchestrators),
        new("workflows", ["workflow"], "Workflow definitions: what config.json declares and the yml file defines", RenderWorkflows),
        new("codebase-graph", ["graph", "graphify-codebase"], "A run's map of the repository: what it reads, and what it does not see", RenderCodebaseGraph),
        new("web-search", ["search", "web_search", "agentwebsearch"], "The harness's own web search: its sources, their kinds and keys", RenderWebSearch),
        new("agent-clients", ["clients", "models"], "Providers, models, tiers and keys in config.json", RenderAgentClients),
        new("side-tasks", ["side-task"], "An agent outside every workflow: run-side-task, its chat and its records", RenderSideTasks),
        new("agent-definitions", ["agent-definition", "definition", "definitions"], "The agent files of a workflow's directory: what each may name", RenderAgentDefinitions),
        new("skills", ["skill"], "A step's prompt names one: {skill.<name>}", RenderSkills),
        new("anchors", ["anchor"], "Anchor registries and the commands that change them", RenderAnchors),
        new("layout", [], "What init creates, and what git tracks", RenderLayout),
        new("secrets", ["hosts-secrets"], "Where each host's connection data lives", RenderSecrets),
        new("tools", [], "What install-missing-tools installs, and where", RenderTools),
        new("runners", ["runner", "actions"], "Predefined runners, action files and excused failures", RenderRunners),
        new("setups", ["setup", "set-up", "set-ups", "up", "down"], "Set-ups a run brings up around its work, and down after", RenderSetUps),
        new("verdicts", ["verdict"], "What each leg verdict means, and what to do about it", RenderVerdicts),
        new("mutations", ["mutation", "check-mutations", "arms", "self-test"], "Mutation testing: the registry, its workers, and the self-test", RenderMutations),
        new("ci", ["check-ci-legs"], "How check-ci-legs finds a workflow's legs and budgets", RenderCi),
    ];

    /// <summary>
    /// Every topic's name and one-line summary, in the order the overview lists them: what the command's own description
    /// says of them. The one list the overview, the rendering and the answer to an unknown topic read, so no topic is ever
    /// advertised by one and missing from another.
    /// </summary>
    public static IReadOnlyList<HelpTopicInfo> Described
        => [.. Topics.Select(topic => new HelpTopicInfo(topic.Name, topic.Summary))];

    /// <summary>Renders one topic, reporting whether it was recognised.</summary>
    public static bool TryRender(string? topic, out string text)
    {
        text = Render(topic);
        return !text.StartsWith(UnknownTopicPrefix, StringComparison.Ordinal);
    }

    /// <summary>Renders one topic, or the overview where none is asked. Separated from any command so it is directly callable.</summary>
    public static string Render(string? topic)
    {
        if (string.IsNullOrEmpty(topic))
        {
            return RenderOverview();
        }

        var asked = topic.ToLowerInvariant();

        return Topics.FirstOrDefault(known => known.Name == asked || known.Aliases.Contains(asked, StringComparer.Ordinal)) is { } found
            ? found.Render()
            : $"{UnknownTopicPrefix} '{topic}'. Try: {string.Join(", ", Topics.Select(known => known.Name))}.{Environment.NewLine}";
    }

    /// <summary>
    /// Lists <paramref name="keys"/> one to a line, each nested section's keys indented beneath the key
    /// that holds them, all meanings in one column.
    /// </summary>
    private static void AppendKeys(StringBuilder builder, IReadOnlyList<KeyDescription> keys)
        => AppendColumns(builder, [.. Indented(keys, "  ").Select(line => (line.Label, line.Key.Meaning + (line.Key.Required ? "; required" : string.Empty)))]);

    /// <summary>Each key written after <paramref name="indent"/>, followed by its own section's keys one step further in.</summary>
    private static IEnumerable<(string Label, KeyDescription Key)> Indented(IReadOnlyList<KeyDescription> keys, string indent)
        => keys.SelectMany(key => Indented(key.Keys, indent + "  ").Prepend((indent + key.Name, key)));

    /// <summary>Writes each label with its text beside it, every text starting in one column.</summary>
    private static void AppendColumns(StringBuilder builder, IReadOnlyList<(string Label, string Text)> lines)
    {
        var column = lines.Max(line => line.Label.Length) + 2;

        foreach (var (label, text) in lines)
        {
            builder.AppendLine(label.PadRight(column) + text);
        }
    }

    /// <summary>
    /// Writes <paramref name="text"/> as lines no wider than the rest of help, broken between words: the first after
    /// <paramref name="lead"/> - a key of a table - and the rest as far in as it is long.
    /// </summary>
    private static void AppendWrapped(StringBuilder builder, string text, string lead = "")
    {
        const int Width = 88;
        var line = new StringBuilder(lead);
        var empty = true;

        foreach (var word in text.Split(' '))
        {
            if (!empty && line.Length + 1 + word.Length > Width)
            {
                builder.AppendLine(line.ToString());
                line.Clear().Append(' ', lead.Length);
                empty = true;
            }

            line.Append(empty ? string.Empty : " ").Append(word);
            empty = false;
        }

        builder.AppendLine(line.ToString());
    }

    /// <summary>
    /// How the forms of git's commands that write its state (<see cref="Agents.Policy.AgentPolicy.GitStateForms"/>) are met
    /// among a command's words, said once for the worktree rules and the hold a run puts on a side-task.
    /// </summary>
    private const string GitStateFormsMet =
        "the option met wherever it stands among the command's words, by any cluster that holds its letter or any start "
        + "of its name git takes, and a word the shell expands held as one that may be it where its expansion may give "
        + "it - a word it begins, quoted or not, one after a start the option's own may have (-f$X), or one the shell "
        + "splits into words of their own where it stands unquoted (--depth=$n), the refusal saying that quoting it "
        + "lets it through - never a quoted one inside a word that begins otherwise (--depth=\"$n\", "
        + "\"refs/heads/$b:refs/remotes/o/$b\"), nor a pattern whose every match begins so (refs/heads/*)";

    /// <summary>One reference topic.</summary>
    /// <param name="Name">The name it is asked for by, and advertised under.</param>
    /// <param name="Aliases">The other names it answers to.</param>
    /// <param name="Summary">What the overview says of it.</param>
    /// <param name="Render">What renders it.</param>
    private sealed record HelpTopic(string Name, IReadOnlyList<string> Aliases, string Summary, Func<string> Render);
}

/// <summary>What a front end needs to know of one reference topic: its name and what the overview says of it.</summary>
/// <param name="Name">The name it is asked for by, and advertised under.</param>
/// <param name="Summary">What the overview says of it.</param>
public sealed record HelpTopicInfo(string Name, string Summary);
