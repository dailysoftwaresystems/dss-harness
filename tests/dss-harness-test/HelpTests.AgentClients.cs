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
    /// The agent-clients topic says the gate: one lane per provider and key, the client's parallel bound with the
    /// built-in's own beside it, each client entering it at its own seam - an API client for each request, its input
    /// estimated as the codebase's tokens are, claude-code for each launch - the API clients' optional buckets, the 429
    /// cool-down every limit sets, a wait said and ended by the pause - and the README says the same.
    /// </summary>
    [Fact]
    public async Task AgentClientsTopic_SaysTheGateOfOneLanePerProviderAndKey()
    {
        var prose = Prose.Words(HelpText.Of("agent-clients").StandardOutput);

        Assert.Contains(
            "The requests of one provider and one key go through one lane of the gate: at most "
                + $"agentClients.<client>.maxParallel of them under way at once ({string.Join(", ", DssHarness.Core.Agents.ClientGateSettings.BuiltIn.Select(bound => $"{bound.Key} {bound.Value}"))} "
                + "where the entry says none; a client that signs in with a login is one lane by its client, and two keys of one "
                + "provider two lanes, keyed by a hash of the key). The API clients enter the lane for each request they send, its "
                + $"input counted as it enters (estimated, its bytes over {DssHarness.Core.Codebase.EstimatedTokens.BytesPerToken}); "
                + "claude-code, whose requests the engine cannot see one by one, is held in the lane for each launch.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "inputTokensPerMinute (a request more than the bucket holds goes in alone once the bucket is empty)",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "a 429's cool-down, set by every rate limit and account limit a request or a launch meets from when the provider "
                + "says it lifts, holding what its failure covers: a model's rate limit holds that model's requests on the lane, an "
                + "account's limit every request of it, a later or longer one holding past a sooner one. The claude-api client's own "
                + "web search enters the lane too. A wait at the gate is said as it is waited, and the run's pause ends it; a "
                + "cool-down past the client's maxRetryWaitSeconds is waited by no client - an API client meets it as the limit it "
                + "is, which moves the step as a 429 naming that wait does, and a claude-code launch it holds is not begun, the step "
                + "moved to an entry the cool-down does not hold.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "Left out, the provider's answers pace the client; the orchestrator's maxReasoningAgents bounds agents beside the gate, and both apply",
            prose,
            StringComparison.Ordinal);

        var readme = Prose.Document("README.md");

        Assert.Contains(
            "The requests of one provider and one key go through one lane of the gate: at most "
                + "`agentClients.<client>.maxParallel` of them under way at once (3 for claude-code, 4 for claude-api and 2 for glm where",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "The API clients enter the lane for each request they send, its input counted as it enters (estimated, its bytes over "
                + $"{DssHarness.Core.Codebase.EstimatedTokens.BytesPerToken}); `claude-code`, whose requests the engine cannot see one by one, is held in the lane for each launch.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "holding what its failure covers: a model's rate limit holds that model's requests on the lane, an account's limit "
                + "every request of it, a later or longer one holding past a sooner one. The `claude-api` client's own web search "
                + "enters the lane too. A wait at the gate is said as it is waited, and the run's pause ends it; a cool-down past "
                + "the client's `maxRetryWaitSeconds` is waited by no client - an API client meets it as the limit it is, which "
                + "moves the step as a 429 naming that wait does, and a `claude-code` launch it holds is not begun, the step moved "
                + "to an entry the cool-down does not hold.",
            readme,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The agent-clients topic says what this build does with keys and what the engine does with them: claude-code reads
    /// none, choosing one is the engine's where it runs a run's steps - the first the slot holds, or the least spent
    /// under balanceKeyUsage - what each key spent recorded by its name, and each request through the gate - and
    /// validate-workflow says the prices and the missing keys of a definition's steps; the workflows topic that a
    /// step's tier is one of tierConfig's.
    /// </summary>
    [Fact]
    public async Task AgentClientsTopic_SaysChoosingAKeyIsTheEngines_AndWhatValidateWorkflowSays_AndAStepsTierIsTierConfigs()
    {
        var prose = Prose.Words(HelpText.Of("agent-clients").StandardOutput);

        Assert.Contains(
            "claude-code signs in by its entry's auth (agentClients.claude-code.auth, one of login, apiKey, oauthToken, authToken): login, "
                + "the machine's login, where nothing is said, reading no key at all; apiKey, its provider's keys in their order; oauthToken or "
                + "authToken, the token the slot holds under the entry's token (CLAUDE_CODE_OAUTH_TOKEN or ANTHROPIC_AUTH_TOKEN where it names "
                + "none) - a name and never a token, appointed once beside every provider's keys. Its configDir and executable name the "
                + "variables of the slot that hold a specific login's Claude Code directory and a program to start in place of npm's "
                + "(CLAUDE_CONFIG_DIR and CLAUDE_EXECUTABLE where they name none), appointed once the same way and never a key; a launch is "
                + "to sign in from that directory where the slot holds it, else from the tool's own CLAUDE_CONFIG_DIR, else from Claude "
                + "Code's own. A launch is given that credential and no "
                + "other, whatever the tool's own environment holds; claude-api and glm sign each request with their provider's keys. "
                + "Choosing a key is the engine's, where it runs a run's steps: a step starts on the first key of its model's provider the "
                + "slot holds and no remembered limit covers, or - where balanceKeyUsage is on (off unless turned on) - on the one that has "
                + "spent least in the last seven days, counted back from the moment it starts, keys that spent alike in the list's order, "
                + "whatever tier, model, step, run or side-task spent it; the key is kept for the step, its conversation staying with one "
                + "account, and a limit or a rejected key moves it to the next in the same order. What a key spent is what the engine "
                + $"counts for each request served with it, at the configuration's prices - or, for a Claude Code launch, what the launch reports - recorded by the key's name (never its value) in {HarnessLayout.OrchestratorsDirectoryName}/key-spend.jsonl, "
                + "one line for each key and model of each entry of a step's order that ran, so a run that moved keys while it worked "
                + "books what each key spent to that key, read for a key's week and its lines older than seven days dropped "
                + "when it is next written; what the same key spends elsewhere - another repository, another machine, another tool - is not "
                + "seen. Each request an API client sends, and each Claude Code launch, goes through the gate of its provider and key "
                + "(below), beside the orchestrator's maxReasoningAgents.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "validate-workflow says, under the line that says a definition holds, every step's models and levels, each provider's "
                + "prices for the models its steps may run on and the date they were read, and each key a step's model would read that "
                + "the slot lacks, with the steps that would read it.",
            prose,
            StringComparison.Ordinal);

        // A step's tier is one of tierConfig's, the section the configuration holds tiers under.
        Assert.Contains(
            "reason.tier a name for a class of model: one of the configuration's tierConfig, its models in the order they are tried",
            Prose.Words(HelpText.Of("workflows").StandardOutput),
            StringComparison.Ordinal);

        // A step's order is entries of a model and one of its provider's keys, each model's keys before the next model,
        // and a model named outright has none after its own keys.
        Assert.Contains(
            "The tiers a step runs on are tierConfig's: each tier its models in the order they are tried, the first serving "
                + "a step on the tier and the rest its fallback - a step's order as entries of a model and one of its "
                + "provider's keys, each model's keys before the next model, and a model named outright, on the step or in "
                + "its agent definition, running on that model with none after it ('help agent-definitions')",
            prose,
            StringComparison.Ordinal);

        // A step that goes on with earlier work is given the continuation record, nothing of it cut, its pages by
        // read_result - its old keys retired - and the first user message's parts - the record only of a new
        // conversation - with the message kept as user.md.
        Assert.Contains(
            "A step that goes on with earlier work is given the engine's record of it: the continuation record, text put "
                + "together from the step directories of the agent - what it is, the last iterations of the newest attempt word "
                + "for word from its stored stream, the newest first and every one of them, every run of the attempt numbered as "
                + "one, each message the engine sent the agent in its place, then the handoff, the last red build or test, the "
                + "steps so far, the newest first, with each one's notes and output whole, and every file changed - nothing in it "
                + "written or shortened by a model, and nothing of it cut. It is kept whole among the results of the conversation "
                + "it begins and laid in pages as every long answer is: the step's first message holds its first page, an index "
                + $"of the pages each part of it is on, and how read_result with id '{DssHarness.Core.Agents.Runtime.KeptResults.ContinuationId}' gives every page of it; "
                + $"what it reads is copied to the step's own {DssHarness.Core.Orchestration.OrchestratorLayout.HandedDirectoryName}/{DssHarness.Core.Agents.AgentContinuation.CopiesFolder}/, "
                + "which the tool alone writes and the agent reads, never where its commands write. The keys of "
                + "agentContext.continuation that once bounded it are retired and refused, naming what replaces them",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "The record is sent only where the conversation is a new one - every launch on claude-code, and on the API "
                + "clients a conversation that cannot be gone on with - for a handed conversation goes on with one more "
                + $"user message alone; the message as sent is kept in the step's {OrchestratorLayout.StepUserFileName}, masked.",
            prose,
            StringComparison.Ordinal);

        // The compaction: made where a full context is answered compact - a side-task its own by itself - the
        // engine's own reduction of the attempt's every run, its bounds said, a new conversation on every client.
        Assert.Contains(
            "A step whose conversation has filled its model's context ends ContextExhausted, and where its limit's question "
                + "is answered compact the engine compacts the conversation - a side-task compacts its own by itself - "
                + "and no model does: a reduction worked out from the stored stream of the attempt, every run of it, the same "
                + "input always the same record - the run context and the task whole as they were first sent, the last "
                + $"{DssHarness.Core.Agents.AgentCompactionBounds.LastIterations} iterations word for word, every earlier message of the agent its text whole "
                + "with its thinking left out, every earlier call one line (the tool, its input cut at callInputChars, how it "
                + "ended, the path where its input names one), every earlier result left out, and every message the engine sent "
                + "since the first whole. The reduced part is at most agentContext.compaction.reducedChars "
                + $"({DssHarness.Core.Agents.AgentCompactionBounds.DefaultReducedChars}); a call's input is cut at callInputChars "
                + $"({DssHarness.Core.Agents.AgentCompactionBounds.DefaultCallInputChars}); where the reduced part is more, the "
                + "oldest messages of the agent are given by reference instead, oldest first, until it fits - each whole in the "
                + "stored stream, by the id read_result gives it by - and the record says how many were; nothing of one is cut.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "It begins a new conversation, on every client - the record is the first message of one and the old is kept "
                + "whole, never edited or sent again - said in the step's stream as Compacted and counted in its result once, "
                + "as the first launch given it begins the conversation - an API client's first answer begun, a claude-code "
                + "call run or turn ended, or the launch ending any way but NotStarted - so a run never started (its client "
                + "missing, its model unpriced, the client refusing it, a model the plan does not serve) or held at the gate "
                + "compacted nothing, and a retry or a move is given the same record and counts no other; a "
                + "side-task's compaction is said and counted the same way, its record carried before the person's next "
                + "message; an attempt compacted again is reduced whole again, its earlier records never copied into the next, and a later "
                + "launch's first message is read as its task alone, never the record it carried; a claude-code child is "
                + "started with DISABLE_COMPACT=1, so it never compacts by itself and a full context ends the step ContextExhausted.",
            prose,
            StringComparison.Ordinal);

        // The envelope said in words where the wire holds no schema: a message of its own after the first, so the first
        // message is the parts alone.
        Assert.Contains(
            $"the {DssHarness.Core.Agents.ZaiApi.ZaiApiClient.ClientName} client, whose wire holds no answer to a schema, says it "
                + "in words in a message of its own after the step's first, in every conversation the step begins, and asks an "
                + "answer that read as no JSON again in JSON alone",
            prose,
            StringComparison.Ordinal);

        // A side-task's own search on the Claude API, asked as the side-task stands when the agent asks it.
        Assert.Contains(
            "on the Claude API a request of the harness's own whose only tool is the provider's search, asked with the model "
                + "and key serving the side-task when the agent asks and through that key's gate as every request is, what it "
                + "costs counted then to that model and key, a failure of it - a gate held past the longest wait among them - "
                + "one failed part of the search saying why;",
            Prose.Words(HelpText.Of("side-tasks").StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The agent-clients topic says which Claude Code the tool uses and why - npm's, and never a native launcher -,
    /// that a run which needs it needs npm, what the tool runs to install it and bring it up to date, what it never
    /// runs - beside a session of that installation, each kind of process that counts as one said, a claude installed
    /// otherwise whose program is read not counting, and a table read by names alone refusing -, that a slot
    /// that cannot give a named program is refused rather than passed over, that what npm holds is said after every
    /// change, the two ways to turn it off, and that no key is read from the process environment.
    /// </summary>
    [Fact]
    public async Task AgentClientsTopic_SaysWhichClaudeCodeItUses()
    {
        var prose = Prose.Words(HelpText.Of("agent-clients").StandardOutput);

        Assert.Contains("The Claude Code this tool uses is the one npm installs, and no other", prose, StringComparison.Ordinal);
        Assert.Contains($"'npm install -g @anthropic-ai/claude-code@{DssHarness.Core.Agents.ClaudeCode.ClaudeCodeUpdateService.Needed}'", prose, StringComparison.Ordinal);
        Assert.Contains("never with sudo, never 'npm update -g', never past the version needed", prose, StringComparison.Ordinal);
        Assert.Contains(
            "never beside a session of that installation that is running (its program, node running its script, a claude node runs, or a "
                + "claude whose program the machine will not say, such as another user's on Linux; a claude installed any other way whose "
                + "program it names does not count, and a process table read by names alone refuses the change)",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("a slot that cannot give it is refused, never passed over for npm's", prose, StringComparison.Ordinal);
        Assert.Contains("After a change, whether it ran or failed, what npm holds since is said.", prose, StringComparison.Ordinal);
        Assert.Contains("and no key is ever read from the process environment", prose, StringComparison.Ordinal);
        Assert.Contains("A run that needs a Claude model through the claude-code client needs npm, and no other run", prose, StringComparison.Ordinal);
        Assert.Contains("CLAUDE_EXECUTABLE", prose, StringComparison.Ordinal);
        Assert.Contains("agentClients.<client>.update", prose, StringComparison.Ordinal);
        Assert.Contains("DISABLE_UPDATES", prose, StringComparison.Ordinal);
    }
}
