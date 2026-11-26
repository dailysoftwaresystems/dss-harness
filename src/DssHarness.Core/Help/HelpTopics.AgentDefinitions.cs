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

public static partial class HelpTopics
{
    /// <summary>
    /// The agent-definitions topic: what a workflow's <c>agents/&lt;name&gt;.md</c> file is, the keys it takes, and how
    /// what it names stands below a step's own - the one reading of a definition, beside the clients topic that owns
    /// the configuration's half.
    /// </summary>
    private static string RenderAgentDefinitions()
    {
        var builder = new StringBuilder();

        builder.AppendLine("Agent definitions");
        builder.AppendLine();
        builder.AppendLine($"A workflow's agents are defined by markdown files in its own directory,");
        builder.AppendLine(".harness-config/workflows/<name>/agents/<name>.md, tracked: frontmatter that declares");
        builder.AppendLine("the agent, and a body that is the system prompt, verbatim. A run reads its agent files");
        builder.AppendLine("from the main checkout wherever the command was typed, never from an agent's worktree:");
        builder.AppendLine("what a later step is told must not be something an earlier agent wrote on its way.");
        builder.AppendLine();
        builder.AppendLine("A definition is the level below a step's: where a step names a tier, a model, an effort");
        builder.AppendLine("or a client, its own wins; where it names none, the definition's stands, each on its own.");
        builder.AppendLine("The keys a definition takes:");
        builder.AppendLine();
        builder.AppendLine("  name          the agent's name, which is the file's: lowercase letters, digits and");
        builder.AppendLine("                single hyphens, said again so a copied file cannot pass as the agent it was");
        builder.AppendLine("  description   what the agent is for, shown wherever agents are listed");
        builder.AppendLine("  tier          a tier tierConfig defines, for a step that names neither a tier nor a");
        builder.AppendLine("                model; never beside model");
        builder.AppendLine("  model         a model's code, as its provider lists it, for a step that names neither;");
        builder.AppendLine("                never beside tier. A model named outright runs on that model, and the rest");
        builder.AppendLine("                of the tier's models are no step's fallback after it");
        builder.AppendLine("  effort        one level, or a level for each model by its code with all for the rest;");
        builder.AppendLine("                read after the step's own, and a model neither names runs at its own default");
        builder.AppendLine("  client        for a step that names none: another client to reach the model through, one");
        builder.AppendLine("                that reaches its provider's type");
        builder.AppendLine("  maxTurns, maxBudgetUsd, maxMinutes, stallSeconds");
        builder.AppendLine("                limits, each a positive number");
        builder.AppendLine("  capabilities  of read, write, run and web; all four where the definition names none");
        builder.AppendLine("  writes        false makes its worktree read-only; true where nothing is said. The");
        builder.AppendLine("                tree of an agent that only reads is its own scratch, never folded, and");
        builder.AppendLine("                written whatever this says");
        builder.AppendLine("  restrictions  rules added to the configured ones; never removing one");
        builder.AppendLine();
        builder.AppendLine("A fallback is no key of a definition or of a step: it is the order of the tier's models");
        builder.AppendLine("in tierConfig, each model's keys before the next model. A key of another tool's agent");
        builder.AppendLine("files is refused naming what to use instead - tools for capabilities, disallowedTools for");
        builder.AppendLine("restrictions, skills for a step's prompt {skill.<name>} - and every problem the file has");
        builder.AppendLine("is said at once, each with the line it stands on, when the workflow is checked.");
        builder.AppendLine();
        builder.AppendLine("The sample agent files name no model, tier or effort, and no shipped one does: they are");
        builder.AppendLine("a step's configuration, and the shipped workflow names them on its steps, where a person");
        builder.AppendLine("reads and changes them.");

        return builder.ToString();
    }
}
