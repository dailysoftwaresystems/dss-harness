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
    /// The loop the API clients share: what a turn is and what it is counted against, where the
    /// conversation is kept, the two ways a turn is cut off, the provider's pause, the retries and their waits, and what a
    /// handoff and a message to an agent at work do to the turn under way - each number from its owner.
    /// </summary>
    private static string ToolLoopProse()
        => "The loop the claude-api and glm clients share (ToolLoop) runs a step of either over the provider each gives it - the "
            + "Claude API's and Z.AI's below. A turn is one answer of the agent's - a request the provider paused, or one "
            + "thrown away and asked again, goes on as the same turn - counted against the step's maxTurns but a handoff "
            + "turn; every request's usage - one that ended without its turn, failed, silent or left, by what it said it used "
            + "so far - is counted at the model's "
            + "prices, and no request but the handoff turn's is sent "
            + "once the step's maxBudgetUsd is spent. A step held to a budget in dollars - its own maxBudgetUsd, or the run's "
            + "budget.usd it is charged to - is never started, on any client, on a model missing a price of what its client's "
            + "requests are billed by: input, cache reads and output on every client, the cache write of its cache lifetime on "
            + "claude-api and both cache writes on claude-code, in the model's large-prompt set too where it has one, nor on a "
            + "provider that prices in another currency than the budget's dollars. The step's first such entry ends it not "
            + "started, naming the key to fill; a later one is passed over, said so, and never asked. The workflow's check - "
            + "validate-workflow, and every run before it starts - says the same first, for each model a step charged to a "
            + "dollar budget may run on, of the prices the client it is reached through is billed by, given that client's "
            + "agentClients entry, naming the model, the client and the missing price (the reporter's is never charged to "
            + "the run's). "
            + "A turn whose cost cannot be counted is never charged as nothing: what was spent is said as at least what was "
            + "counted, and why the rest is not known. Each turn's calls run as the tool runtime above decides them; a turn whose request spent "
            + "the budget runs none of them. The conversation is kept in the provider's own form under the agent's "
            + $"{OrchestratorLayout.SessionsDirectoryName}/<id>/, each message as it was sent and received and sent back so with "
            + "every later request, one line for each point where the history is whole - a turn that called tools stored with "
            + "their answers - and a step that goes on in it sends what it began with, every request the conversation whole. "
            + "The first request asks for a quarter of "
            + "the model's maxOutputTokens. A turn cut off at its length that called no tool, its request holding it to no "
            + $"schema, is stored and nudged on, {Agents.Loop.ToolLoop.MostNudges} times at most; one cut off while it called a "
            + "tool - its calls never run - before it wrote anything, or while its request held it to a schema, which the answer "
            + "to a nudge would be held to whole, is thrown away and asked again with twice the room, up to the most; "
            + $"past either the step is {nameof(Agents.AgentRunStatus.Truncated)}. A turn the provider paused is sent again with "
            + "nothing added for as long as the provider pauses it, never counted - each pause told with how long the turn has "
            + "waited - until the step's own bounds end it: its time, its budget, a handoff or an interrupt. An answer gone on "
            + "from, cut off or paused, is every piece of it. A failed request is asked again "
            + $"up to {Agents.Loop.ToolLoopTerms.DefaultMaxRetries} times after a wait that doubles from "
            + $"{Agents.Loop.ToolLoop.FirstWait.TotalSeconds:0} s to {Agents.Loop.ToolLoop.LongestDoubledWait.TotalSeconds:0} s, "
            + "or the one the provider names; a service failing past them, or naming such a wait, fails the "
            + "step, and one silent past the stall bound - nothing of it heard, not even the provider's keep-alive - stalls it; "
            + "a connection is given no time of its own to connect in - the operating system's own retries end one that never "
            + "comes - and the body of a request the provider refused is read to its end, kept whole as it comes in the "
            + $"step's {Agents.Http.ApiRecords.DirectoryName}/ beside its records, a notice every "
            + $"{Waiting.Waits.SaidEvery.TotalMinutes:0} minutes saying how much has come while it still does, and said in "
            + "the provider's own words where it is an error of the provider's, else whole where it was held or by its size "
            + "and the file that keeps it; "
            + "a model the provider does not serve the key never starts the "
            + "step before the provider begins an answer with the model asked and fails it once one began. The engine's fallback works inside the one conversation: a key "
            + $"the provider refuses - said, and passed over for the rest of the run - an account at its limit (a rate limit past those tries, or naming a wait past {Agents.Loop.ToolLoopTerms.DefaultMaxRetryWait.TotalSeconds:0} s: "
            + "the model's on the key it was sent with, or the key's whole where its balance is spent, each named) and a "
            + "request the model declined each move the run to its next entry of the same provider and client, the same "
            + "request sent again with the model or the key changed and nothing else, the declined turn thrown away with any "
            + "part of an answer it had written; at a limit the same model's next key is tried first and every entry the "
            + "limit covers is passed over until it lifts, a declined request passes over the model's other keys, and one the "
            + "provider marks about what the prompt asks (reasoning_extraction) moves no step. A handoff asked abandons the request under way and cuts the calls "
            + $"running, each answered '{Agents.Runtime.AgentToolRuntime.Interrupted(Agents.Loop.ToolLoop.Pausing)}', then asks "
            + "the agent in one more message of the conversation, held to the handoff's bound instead of the step's time, no tool run in that turn; "
            + "a side-task's interrupt does the same, each call answered "
            + $"'{Agents.Runtime.AgentToolRuntime.Interrupted(Agents.AgentTurnInterrupt.Cut)}', and ends the run Cancelled, the "
            + "conversation stored to its last whole turn; a request left so - by a handoff, an interrupt, the step's time or "
            + "the handoff's bound - "
            + "counts what the provider had said it used, and where it had said nothing yet the run's cost is unknown, "
            + "never naught; a message the engine gives an agent at work is given after "
            + "the results of the calls it is running, and an answer written while it was is not the step's.";

    /// <summary>
    /// The claude-api client: what each request is and carries - the version, the betas, the cache, what the tool sends
    /// itself and what the client's own keys add - and what each of the API's failures is to a step, each name and number
    /// from its owner.
    /// </summary>
    private static string ClaudeApiClientProse()
    {
        const string Client = Agents.ClaudeApi.ClaudeApiClient.ClientName;

        return $"The {Client} client works a step through the Claude API by the loop above, every call run by the tool "
            + $"runtime: each request is posted to its provider's api with /{Agents.ClaudeApi.ClaudeApiRequest.MessagesPath} "
            + "after it, signed with a key of the provider's (x-api-key), under anthropic-version: "
            + $"{Agents.ClaudeApi.ClaudeApiRequest.Version}, and its answer streamed. It is given the run's entries - its "
            + "provider, its model and, where the configuration declares one, its own entry under agentClients, every key "
            + "at its default where it declares none - and never starts without the first two, or on a provider "
            + $"of a type but '{Agents.ClaudeApi.ClaudeApiClient.Descriptor.ProviderType}'. A request sends the model, the "
            + "system prompt as one block, the tools - each streaming its input as it is written - and the messages as the "
            + "conversation stored them; adaptive thinking, its summary shown; the step's effort as output_config.effort; "
            + $"old tool uses cleared by the server as the context fills ({Agents.ClaudeApi.ClaudeApiRequest.ClearToolUses}, "
            + $"under the {Agents.ClaudeApi.ClaudeApiRequest.ContextManagementBeta} beta), each clearing said in a notice; and "
            + "three cache marks of the client's lifetime for agentClients."
            + Client + ".cacheTtl "
            + $"({string.Join(" or ", Agents.ClaudeApi.ClaudeApiRequest.CacheTtls)}, {Agents.ClaudeApi.ClaudeApiRequest.FiveMinutes} "
            + "where nothing is said) - on the system text, covering the tools and it, the same for every step of that "
            + "agent with the same tools; on the run's repository map, where a step is pushed one (graph: map), the first block of the conversation's first user "
            + "message, covering the tools, the system text and the map, the same for every such step of the run that reads alike; and the "
            + "top-level mark, which moves with the conversation's last block - so a step's second request on reads the "
            + "map from the cache, and so does the first request of the run's next step of the same agent - and a budget "
            + "needs the model's price of that cache's writes too: priceCacheWrite, or "
            + "priceCacheWrite1h for an hour. A step with a fallback that can go on here sends the "
            + $"{Agents.ClaudeApi.ClaudeApiRequest.FallbackCreditBeta} beta, and a request the model declines moves the step "
            + $"to its next model of the same provider and client - the same request sent again with the model changed and "
            + $"the credit the API gave sent back as its fallback_credit_token, a failure naming it answered by the same "
            + $"request without it - {nameof(Agents.AgentRunStatus.Refused)} with its category only where none is left; "
            + $"no request carries fallbacks or a {Agents.ClaudeApi.ClaudeApiRequest.ServerSideFallbackPrefix}* beta, for the "
            + $"engine's fallback is the only one a step has. One key is {Client}'s alone: betas, sent beside the tool's own, "
            + $"each one word and never the API's fallback's; cacheTtl is both Claude clients' ({Agents.ClaudeCode.ClaudeCodeClient.ClientName} "
            + "sets its launches' cache from it); three are both API clients': requestExtras, an "
            + "object merged into every request's body - a member it adds sent as given, an object's members merged into the "
            + "tool's own - refused where it sets what the tool sends itself or, here, fallbacks or a sampling parameter "
            + $"(temperature, top_p, top_k), which anthropic-version {Agents.ClaudeApi.ClaudeApiRequest.Version} refuses on every "
            + "model, and free to add a tool choice or thinking.budget_tokens where the step's model takes one; and maxRetries and "
            + $"maxRetryWaitSeconds, the loop's tries and longest wait ({Agents.Loop.ToolLoopTerms.DefaultMaxRetries} and "
            + $"{Agents.Loop.ToolLoopTerms.DefaultMaxRetryWait.TotalSeconds:0} where nothing is said). A spend limit reached "
            + $"- the organization's monthly cap, a 429 whose error names {Agents.ClaudeApi.ClaudeApiFailures.SpendCapCode}, or one "
            + "the organization or a workspace set, a 400 that says so - is the key's limit, every entry on that key passed "
            + "over until the date the API names; a 401 is a key "
            + "refused, said and passed over for the rest of the run; a 402 the key's balance spent, every entry on that "
            + "key passed over; a 429 the model's limit on that key, waited out within "
            + "those tries and the same model's next key tried past them; a 413, a prompt too long or an answer the model's context could hold no more "
            + $"of is the context exhausted ({nameof(Agents.AgentRunStatus.ContextExhausted)}); a 403, a 404, or a 400 "
            + "answering a conversation's first request - a model the key's organization is not given, such as one that "
            + "needs a data retention the organization does not keep - is a model the provider does not serve the key, "
            + "naming it; a 400 that refuses the format as more than structured outputs compile - its grammar too large, "
            + "more optional or union-typed properties than they take, or a schema too complex - is the format refused, a "
            + "first request's too, and the step goes on with its envelope in words, as below; an overload, a server's "
            + "error, a request timed out, a conflict, a network that failed and a stream cut or ended before its message are "
            + "asked again; and any other 400 fails the step, said in the "
            + "API's own words with the request it names.";
    }

    /// <summary>
    /// The glm client: what each request to Z.AI is and carries, the conversation sent whole until its model's context is
    /// full, its keys, and what each of Z.AI's failures is to a step - its codes from the one table of them, each name and
    /// number from its owner.
    /// </summary>
    private static string ZaiApiClientProse()
    {
        const string Client = Agents.ZaiApi.ZaiApiClient.ClientName;
        var efforts = Agents.AgentProviders.ClientKinds.First(kind => kind.Name == Client).Efforts;
        var codes = string.Join(
            "; ",
            Agents.ZaiApi.ZaiApiFailures.Codes.Select(row => $"{row.What} ({string.Join(", ", row.Codes)}) {FailureSaid(row.Kind, row.BalanceSpent)}"));
        var statuses = string.Join(
            "; ",
            Agents.ZaiApi.ZaiApiFailures.Statuses.Select(row => $"{string.Join(" or ", row.Statuses.Select(status => $"a {status.ToString(CultureInfo.InvariantCulture)}"))} {FailureSaid(row.Kind, false)}"));

        return $"The {Client} client works a step through Z.AI by the loop above, every call run by the tool runtime: each "
            + $"request is posted to its provider's api with /{Agents.ZaiApi.ZaiApiRequest.CompletionsPath} after it, signed "
            + "with a key of the provider's (Authorization: Bearer), and its answer streamed. It is given the run's entries as "
            + $"the {Agents.ClaudeApi.ClaudeApiClient.ClientName} client is, and never starts without its provider and model, or on a provider of a type but "
            + $"'{Agents.ZaiApi.ZaiApiClient.Descriptor.ProviderType}'. A request sends the model; the system prompt as the "
            + "first message and every message as the conversation stored it - the engine's as a user message of text, an "
            + "answer's calls each answered by a tool message, in order; the tools as functions, their calls' arguments "
            + "streamed (tool_stream); the model's thinking on, the step's effort as reasoning_effort "
            + $"({string.Join(", ", efforts)}); and max_tokens - no sampling parameter or tool choice of the tool's own, which "
            + "requestExtras may add, and no tool of Z.AI's, for the web is the harness's own. A first message that holds "
            + "the run's repository map holds it first, ahead of the step's context pack and its task - the same bytes for "
            + "every step of the run that reads alike, so the longest prefix the steps of an agent share begins with it. Z.AI caches what a "
            + "request repeats by itself and bills "
            + "no write, so a budget needs the model's input, cached-input and output prices. Each answer's reasoning is "
            + "stored with it and sent back; Z.AI leaves it out of what the model reads in later turns unless "
            + $"agentClients.{Client}.preserveThinking is true (false where nothing is said), which keeps it "
            + "(thinking.clear_thinking: false). Z.AI clears nothing of a conversation itself, and every request sends it "
            + "whole, each answer to a call as it was; once what the model last read and wrote - Z.AI's own count of it - "
            + "reaches the model's contextTokens, the next request would pass the context and is not sent: the context is "
            + $"exhausted ({nameof(Agents.AgentRunStatus.ContextExhausted)}), as where Z.AI refuses a prompt too long, and "
            + "the step's answer to a full context - its compaction among them - carries the work on. Three keys are both API clients' - requestExtras, "
            + $"refused where it sets what the tool sends itself, and maxRetries and maxRetryWaitSeconds, as the {Agents.ClaudeApi.ClaudeApiClient.ClientName} "
            + $"client's above - and one is {Client}'s alone: preserveThinking. A failure is read by Z.AI's code first: "
            + codes
            + ". A code that list does not hold is read by the status: "
            + statuses
            + "; a request timed out, a conflict or a server's error is asked again, and any other fails the step. An answer "
            + $"Z.AI ends as sensitive ends the step {nameof(Agents.AgentRunStatus.Refused)}, one it ends for its context the "
            + "context is exhausted and one it ends for a network error is asked again; a failure Z.AI names is said in its own "
            + "words.";
    }

    /// <summary>
    /// The envelope every client asks for where a step names an output, what the gate holds it to, and how each client
    /// asks for it - each member, file, bound and number from its owner.
    /// </summary>
    private static string StructuredOutputProse()
    {
        var output = new Agents.AgentOutput("o", "{}");
        static string Members(System.Text.Json.Nodes.JsonObject shape) => string.Join(", ", shape["properties"]!.AsObject().Select(member => member.Key));

        return $"Where a step names an output, every client asks the agent for one envelope - {Agents.AgentEnvelope.Notes}, "
            + $"{Agents.AgentEnvelope.Output}, {Agents.AgentEnvelope.Assumptions}, and {Agents.AgentEnvelope.Questions} where the step may ask and has rounds "
            + "left - and the harness's own validator holds the answer to it, whatever a client was sent. The notes are not "
            + $"empty, and are kept, masked, as the step's {OrchestratorLayout.StepNotesFileName} beside its result; the output is read "
            + "against the step's whole schema, each problem named by its path under $.output, a text an enum or a const holds "
            + $"in another case read as the schema spells it; each assumption is {{{Members(Agents.AgentEnvelope.Assumption())}}}; each question is "
            + $"{{{Members(Agents.AgentEnvelope.Question())}}}, with at least "
            + $"{Agents.AgentEnvelope.FewestOptions} options, no two alike, recommending one of them word for word; no text of "
            + "either is empty. The output is null only in a "
            + "handoff, whose notes are the handoff, or where the answer asks, and the step then ends "
            + $"{nameof(Agents.AgentRunStatus.Asked)} with its questions; an answer with both is sent back, and so is one that asks "
            + "where the step may not or has asked as often as it may, told to write what it would have asked under assumptions. "
            + $"The {Agents.ClaudeCode.ClaudeCodeClient.ClientName} client holds the answer to the envelope by --json-schema; the "
            + $"{Agents.ClaudeApi.ClaudeApiClient.ClientName} client sends it as output_config.format on every request, the "
            + $"handoff's too, each array's minItems above {Agents.ClaudeApi.ClaudeApiRequest.MostLeastItems} and every maxItems moved "
            + "into the array's description in words, for structured outputs take no other - and where the API refuses it as "
            + "more than it compiles - its grammar too large, more optional or union-typed properties than it takes - the run's "
            + "later requests carry no format, and a message of the conversation says it in words, the system prompt "
            + "untouched, and every later run of that envelope on that model, in the same session, says it in words from its "
            + "first request; the "
            + $"{Agents.ZaiApi.ZaiApiClient.ClientName} client, whose wire holds no answer to a schema, says it in words in a "
            + "message of its own after the step's first, in every conversation the step begins, and asks an answer that read "
            + "as no JSON again in JSON alone - no tool offered, "
            + "response_format a JSON object. An answer that does not read is sent back in the same conversation, "
            + $"{output.MaxRepairs} times at most, and the step is then {nameof(Agents.AgentRunStatus.InvalidOutput)}. "
            + "An answer that reads, but that a check the engine runs on it next finds wrong - a plan check over the items it "
            + "gives, a class closure over its record, each by that check's own rules - is sent back in the same conversation "
            + "with what the check found, no repair counted, for as long as each answer is new; one that gives again an output "
            + "it was sent back with is taken as given, for the check to record what is still wrong.";
    }

    /// <summary>What a failure of <paramref name="kind"/> is to a step, as the loop acts on it.</summary>
    private static string FailureSaid(Agents.Loop.ChatFailureKind kind, bool balanceSpent) => kind switch
    {
        Agents.Loop.ChatFailureKind.KeyRefused => "is a key refused, said and passed over for the rest of the run",
        Agents.Loop.ChatFailureKind.AccountLimit => $"is the key's limit{(balanceSpent ? ", its balance spent" : string.Empty)}, every entry on that key passed over",
        Agents.Loop.ChatFailureKind.RateLimited => "is the model's limit on that key, waited out within the tries and the same model's next key tried past them",
        Agents.Loop.ChatFailureKind.Transient => "is asked again",
        Agents.Loop.ChatFailureKind.ContextFull => $"is the context exhausted ({nameof(Agents.AgentRunStatus.ContextExhausted)})",
        Agents.Loop.ChatFailureKind.Declined => $"moves the step to its next model, {nameof(Agents.AgentRunStatus.Refused)} where none is left",
        Agents.Loop.ChatFailureKind.Unserved => "is a model the provider does not serve the key",
        _ => "fails the step",
    };
}
