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
    /// The skills topic: the placeholder, the three places and their order beside Claude Code's, what is put into a
    /// prompt, the folder handed to the step, every key of the frontmatter and what is done with it, the body's reads
    /// and bounds, and what is not looked for.
    /// </summary>
    private static string RenderSkills()
    {
        var builder = new StringBuilder();

        builder.AppendLine("Skills in a prompt");
        builder.AppendLine();
        builder.AppendLine("A reason step's prompt says {skill.<name>} where a skill's text goes, and the engine puts");
        builder.AppendLine("the text there before the prompt is sent, on every client alike: the two API clients have no");
        builder.AppendLine("skill mechanism, and the claude child is started with --setting-sources user, under which it");
        builder.AppendLine("loads none of the worktree's. The file is Claude Code's own SKILL.md, unchanged - a skill");
        builder.AppendLine("written for Claude Code is used here as it is, and one written here is a skill there. This");
        builder.AppendLine("tool adds no key and no syntax of its own.");
        builder.AppendLine();
        builder.AppendLine("<name> is the folder's name alone - letters, digits, '_' and '-', a '.' between two words -");
        builder.AppendLine("so no name reaches past the place it is looked for in; any other is refused, by the check and");
        builder.AppendLine("by the step alike. The first place that has the folder decides:");
        builder.AppendLine();
        builder.AppendLine($"  1  {AgentSkills.HarnessPlace}/<name>/SKILL.md   in the main checkout - the tool's own place; tracked");
        builder.AppendLine($"  2  {AgentSkills.ClaudePlace}/<name>/SKILL.md           in the main checkout - the repository's Claude Code skills");
        builder.AppendLine($"  3  skills/<name>/SKILL.md                   in the user's Claude directory ({ClaudeTranscripts.ConfigDirectoryVariable} where");
        builder.AppendLine("                                             it is set, else ~/.claude)");
        builder.AppendLine();
        builder.AppendLine("The repository's come before the user's, so a run reads the same skill on every machine");
        builder.AppendLine("that has the repository; Claude Code's own order is the other way round. A SKILL.md that is");
        builder.AppendLine("there and cannot be used is refused: the next place is not tried in its stead, for a broken");
        builder.AppendLine("skill of the repository must not turn into the user's without a word. Not looked for, each");
        builder.AppendLine("named here: a plugin's skill (<plugin>:<skill>), a managed one, a .claude/skills below the");
        builder.AppendLine("main checkout's root, an added directory's, and a .claude/commands/<name>.md file - any of");
        builder.AppendLine($"them is used by copying its folder to {AgentSkills.HarnessPlace}/<name>/.");
        builder.AppendLine();
        builder.AppendLine("The places are read in the main checkout, never in an agent's worktree - what a later step");
        builder.AppendLine("is told must not be something an earlier agent wrote on its way - and each step reads them");
        builder.AppendLine("as it starts: a run does not freeze its skills. What is put into the prompt is the body, not");
        builder.AppendLine("the frontmatter, after its substitutions:");
        builder.AppendLine();
        builder.AppendLine("  <skill name=\"<name>\" from=\"<place>\" directory=\"<the folder handed to this step>\">");
        builder.AppendLine("  Files this skill refers to are in that directory. Read them from there.");
        builder.AppendLine();
        builder.AppendLine("  ...the body...");
        builder.AppendLine("  </skill>");
        builder.AppendLine();
        builder.AppendLine($"The folder is copied whole into what the step's agent is handed, the step's own {OrchestratorLayout.HandedDirectoryName}/skills/<name>/,");
        builder.AppendLine("in place of any earlier copy; that path is the directory the text states and what");
        builder.AppendLine("${CLAUDE_SKILL_DIR} becomes. It is a root the agent reads, on every client, which the tool alone");
        builder.AppendLine("writes, through no link - never the agent's work directory, where its commands write.");
        builder.AppendLine($"A folder that holds a link, more than {AgentSkills.FileLimit} files or more than 16 MB is");
        builder.AppendLine("refused. The step's record names every skill it was given, each once over its attempts - its");
        builder.AppendLine("step.json skills[], and its stream's sessionStarted event - with each one's place, its");
        builder.AppendLine("SKILL.md, the folder handed to the step, and the SHA-256 of SKILL.md and of the folder.");
        builder.AppendLine();
        builder.AppendLine("The frontmatter's keys, each honoured, recorded or refused:");
        builder.AppendLine();
        builder.AppendLine("  name            where given, it must be the folder's name: a folder 'a' holding a skill");
        builder.AppendLine("                  named 'b' is refused, for Claude Code would call it 'b' and the placeholder");
        builder.AppendLine("                  could not say which is meant");
        builder.AppendLine("  description, when_to_use, argument-hint, arguments, user-invocable,");
        builder.AppendLine("  disable-model-invocation, paths, metadata, license, compatibility");
        builder.AppendLine("                  read and recorded, with nothing to do: they say how and when Claude Code");
        builder.AppendLine("                  offers a skill, and a placeholder names it outright");
        builder.AppendLine("  allowed-tools   honoured as what the skill needs: each tool it names must be one the");
        builder.AppendLine("                  step's agent has, by the one table of Claude Code's tools for each");
        builder.AppendLine("                  capability (Read: read, Edit: write, Bash: run; an MCP tool, the sub-agent");
        builder.AppendLine("                  tool and the skill tool are named by none). It grants nothing: what an");
        builder.AppendLine("                  agent may do is its definition's and the policy's");
        builder.AppendLine("  shell           nothing to do: it chooses the shell of a command run when a skill is");
        builder.AppendLine("                  loaded, which is refused in itself");
        builder.AppendLine("  model, effort, context, agent, background, hooks, disallowed-tools");
        builder.AppendLine("                  refused: each changes what runs the skill in Claude Code, and text put");
        builder.AppendLine("                  into a prompt runs in the step's own agent, on its model, under its policy.");
        builder.AppendLine("                  Any other key is refused too, naming the keys this tool knows.");
        builder.AppendLine();
        builder.AppendLine("In the body, replaced: ${CLAUDE_SKILL_DIR} (the folder handed to the step),");
        builder.AppendLine("${CLAUDE_PROJECT_DIR} (the agent's worktree; for an agent with none, the main checkout),");
        builder.AppendLine("${CLAUDE_SESSION_ID} (the id the step's run begins its first session as, which the stream's");
        builder.AppendLine("first sessionStarted names; a later key or model an API client reaches inside its own walk");
        builder.AppendLine("keeps that session, and only a new conversation - a retry, a move the runner makes, a");
        builder.AppendLine("claude-code launch begun again on its next key - begins one of its own with the same prompt),");
        builder.AppendLine("${CLAUDE_EFFORT} (the effort the step resolved to), and \\$ (a dollar).");
        builder.AppendLine("Refused, each naming its line: a read of an argument ($ARGUMENTS,");
        builder.AppendLine("$ARGUMENTS[N], $1, a $name the arguments key declares) - a placeholder gives a skill no");
        builder.AppendLine("arguments; a command run when the skill is loaded (both exclamation-mark forms) - this");
        builder.AppendLine("tool runs nothing to build a prompt; and ${CLAUDE_PLUGIN_ROOT} and ${CLAUDE_PLUGIN_DATA}");
        builder.AppendLine("(only a plugin has them) and any other ${CLAUDE_...} this tool does not know. A body is at");
        builder.AppendLine($"most 64,000 characters after its substitutions; a longer one is refused. What a body asks");
        builder.AppendLine("for in words - \"use the sub-agent tool\", \"run /another-skill\" - the tool does not read:");
        builder.AppendLine("a skill that leans on Claude Code's own tools reads the same on every client and works as");
        builder.AppendLine("far as the agent's tools go. A skill's text is not scanned for placeholders again: {skill.b}");
        builder.AppendLine("inside skill a is plain text, and a prompt that needs both names both.");
        builder.AppendLine();
        builder.AppendLine("The placeholder stands in a reason step's prompt and resumePrompt alone, a file or");
        builder.AppendLine("inline: it is refused in an agent definition's body (the system prompt is sent as written),");
        builder.AppendLine("in any other template key - a commit's message, a pause's question, a pull request's title");
        builder.AppendLine("or body - and in an expression. Each skill once in a prompt; twice is refused. Nothing is");
        builder.AppendLine("carried from one step's prompt to another's: a step that goes on names the skills it needs");
        builder.AppendLine("itself.");
        builder.AppendLine();
        builder.AppendLine("A skill that is missing or unusable fails loud, twice: when the definition is checked -");
        builder.AppendLine("validate-workflow, and the start of run-orchestrator and resume-orchestrator (exit 12,");
        builder.AppendLine("nothing started, every problem of every prompt together) - and when the step starts, for a");
        builder.AppendLine("file can go or change between: the step is not started, it ends Failed with the same");
        builder.AppendLine("sentence and exit 12, and its onFailure decides what follows.");

        return builder.ToString();
    }
}
