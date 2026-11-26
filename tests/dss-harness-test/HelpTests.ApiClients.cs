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
    /// The loop the API clients share is said alike by help agent-clients and the README - each bound and number read from
    /// the type that holds it - and the architecture names its owners by the types that are them.
    /// </summary>
    [Fact]
    public void TheLoopTheApiClientsShare_IsSaidByTheHelpAndTheDocuments()
    {
        var prose = Prose.Words(HelpText.Of("agent-clients").StandardOutput);
        var readme = Prose.Document("README.md");
        var interrupted = DssHarness.Core.Agents.Runtime.AgentToolRuntime.Interrupted(DssHarness.Core.Agents.Loop.ToolLoop.Pausing);
        var cut = DssHarness.Core.Agents.Runtime.AgentToolRuntime.Interrupted(DssHarness.Core.Agents.AgentTurnInterrupt.Cut);
        var nudges = DssHarness.Core.Agents.Loop.ToolLoop.MostNudges;
        var tries = DssHarness.Core.Agents.Loop.ToolLoopTerms.DefaultMaxRetries;
        var (first, longest) = (DssHarness.Core.Agents.Loop.ToolLoop.FirstWait.TotalSeconds, DssHarness.Core.Agents.Loop.ToolLoop.LongestDoubledWait.TotalSeconds);
        var wait = DssHarness.Core.Agents.Loop.ToolLoopTerms.DefaultMaxRetryWait.TotalSeconds;
        var (refusals, every) = (DssHarness.Core.Agents.Http.ApiRecords.DirectoryName, DssHarness.Core.Waiting.Waits.SaidEvery.TotalMinutes);
        const string Paused = "A turn the provider paused is sent again with nothing added for as long as the provider pauses it, never counted - each pause told with how long the turn has waited - until the step's own bounds end it: its time, its budget, a handoff or an interrupt.";
        var refused = "a connection is given no time of its own to connect in - the operating system's own retries end one that never comes - and the body of a request the provider refused is read to its end, kept whole as it comes in the "
            + $"step's {{0}}{refusals}/{{0}} beside its records, a notice every {every} minutes saying how much has come while it still does, and said in the provider's own words where it is an error of the provider's, else whole where it was held or by its size and the file that keeps it; ";

        Assert.Contains(
            $"A turn cut off at its length that called no tool, its request holding it to no schema, is stored and nudged on, {nudges} times at most; one cut off while it called a tool - its calls never run - before it wrote anything, or while its request held it to a schema, which the answer to a nudge would be held to whole, is thrown away and asked again with twice the room, up to the most; past either the step is Truncated. "
                + $"{Paused} An answer gone on from, cut off or paused, is every piece of it. A failed request is asked again up to {tries} times after a wait that doubles from {first} s to {longest} s, "
                + "or the one the provider names; a service failing past them, or naming such a wait, fails the step, and one silent past the stall bound - nothing of it heard, not even the provider's keep-alive - stalls it; "
                + string.Format(CultureInfo.InvariantCulture, refused, string.Empty)
                + "a model the provider does not serve the key never starts the step before the provider begins an answer with the model asked and fails it once one began. The engine's fallback works inside the one conversation: a key "
                + $"the provider refuses - said, and passed over for the rest of the run - an account at its limit (a rate limit past those tries, or naming a wait past {wait} s: "
                + "the model's on the key it was sent with, or the key's whole where its balance is spent, each named) and a "
                + "request the model declined each move the run to its next entry of the same provider and client, the same "
                + "request sent again with the model or the key changed and nothing else, the declined turn thrown away with any "
                + "part of an answer it had written; at a limit the same model's next key is tried first and every entry the "
                + "limit covers is passed over until it lifts, a declined request passes over the model's other keys, and one the "
                + "provider marks about what the prompt asks (reasoning_extraction) moves no step. "
                + $"A handoff asked abandons the request under way and cuts the calls running, each answered '{interrupted}', then asks the agent in one more message of the conversation, held to the handoff's bound instead of the step's time, no tool run in that turn; "
                + $"a side-task's interrupt does the same, each call answered '{cut}', and ends the run Cancelled, the conversation stored to its last whole turn; "
                + "a request left so - by a handoff, an interrupt, the step's time or the handoff's bound - counts what the provider had said it used, and where it had said nothing yet the run's cost is unknown, never naught;",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("A turn is one answer of the agent's - a request the provider paused, or one thrown away and asked again, goes on as the same turn - counted against the step's maxTurns but a handoff turn;", prose, StringComparison.Ordinal);
        Assert.Contains("A turn is one answer of the agent's - a request the provider paused, or one thrown away and asked again, goes on as the same turn - counted against the step's `maxTurns` but a handoff turn;", readme, StringComparison.Ordinal);
        const string Priceless = "A step held to a budget in dollars - its own {0}, or the run's {1} it is charged to - is never started, on any client, on a model missing a price of what its client's requests are billed by: input, cache reads and output on every client, the cache write of its cache lifetime on {2} and both cache writes on {3}, in the model's large-prompt set too where it has one, nor on a provider that prices in another currency than the budget's dollars. "
            + "The step's first such entry ends it not started, naming the key to fill; a later one is passed over, said so, and never asked. The workflow's check - {4}, and every run before it starts - says the same first, for each model a step charged to a dollar budget may run on, of the prices the client it is reached through is billed by, given that client's {6} entry, naming the model, the client and the missing price (the {5}'s is never charged to the run's). "
            + "A turn whose cost cannot be counted is never charged as nothing: what was spent is said as at least what was counted, and why the rest is not known.";

        Assert.Contains("and no request but the handoff turn's is sent once the step's maxBudgetUsd is spent.", prose, StringComparison.Ordinal);
        Assert.Contains(string.Format(System.Globalization.CultureInfo.InvariantCulture, Priceless, "maxBudgetUsd", "budget.usd", "claude-api", "claude-code", "validate-workflow", "reporter", "agentClients"), prose, StringComparison.Ordinal);
        Assert.Contains("and no request but the handoff turn's is sent once the step's `maxBudgetUsd` is spent.", readme, StringComparison.Ordinal);
        Assert.Contains(string.Format(System.Globalization.CultureInfo.InvariantCulture, Priceless, "`maxBudgetUsd`", "`budget.usd`", "`claude-api`", "`claude-code`", "`validate-workflow`", "`reporter`", "`agentClients`"), readme, StringComparison.Ordinal);
        Assert.Contains("The loop the claude-api and glm clients share (ToolLoop) runs a step of either over the provider each gives it - the Claude API's and Z.AI's below.", prose, StringComparison.Ordinal);
        Assert.Contains("a step that goes on in it sends what it began with, every request the conversation whole. The first request asks for a quarter of the model's maxOutputTokens.", prose, StringComparison.Ordinal);
        Assert.Contains("a step that goes on in it sends what it began with, every request the conversation whole. The first request asks for a quarter of the model's `maxOutputTokens`.", readme, StringComparison.Ordinal);
        Assert.Contains($"under the agent's {DssHarness.Core.Orchestration.OrchestratorLayout.SessionsDirectoryName}/<id>/, each message as it was sent and received", prose, StringComparison.Ordinal);

        Assert.Contains(
            $"A turn cut off at its length that called no tool, its request holding it to no schema, is stored and nudged on, {nudges} times at most; one cut off while it called a tool - its calls never run - before it wrote anything, or while its request held it to a schema, which the answer to a nudge would be held to whole, is thrown away and asked again with twice the room, up to the most; past either the step is `Truncated`. "
                + $"{Paused} An answer gone on from, cut off or paused, is every piece of it. A failed request is asked again up to {tries} times after a wait that doubles from {first} s to {longest} s, "
                + $"or the one the provider names; a service failing past them, or naming such a wait, fails the step, and one silent past the stall bound - nothing of it heard, not even the provider's keep-alive - stalls it; "
                + string.Format(CultureInfo.InvariantCulture, refused, "`")
                + "a model the provider does not serve the key never starts the step before the provider begins an answer with the model asked and fails it once one began. The engine's fallback works inside the one conversation: a key "
                + $"the provider refuses - said, and passed over for the rest of the run - an account at its limit (a rate limit past those tries, or naming a wait past {wait} s: "
                + "the model's on the key it was sent with, or the key's whole where its balance is spent, each named) and a "
                + "request the model declined each move the run to its next entry of the same provider and client, the same "
                + "request sent again with the model or the key changed and nothing else, the declined turn thrown away with any "
                + "part of an answer it had written; at a limit the same model's next key is tried first and every entry the "
                + "limit covers is passed over until it lifts, a declined request passes over the model's other keys, and one the "
                + "provider marks about what the prompt asks (`reasoning_extraction`) moves no step. "
                + $"A handoff asked abandons the request under way and cuts the calls running, each answered `{interrupted}`, then asks the agent in one more message of the conversation, held to the handoff's bound instead of the step's time, no tool run in that turn; "
                + $"a side-task's interrupt does the same, each call answered `{cut}`, and ends the run `Cancelled`, the conversation stored to its last whole turn; "
                + "a request left so - by a handoff, an interrupt, the step's time or the handoff's bound - counts what the provider had said it used, and where it had said nothing yet the run's cost is unknown, never naught;",
            readme,
            StringComparison.Ordinal);
        Assert.Contains($"under the agent's `{DssHarness.Core.Orchestration.OrchestratorLayout.SessionsDirectoryName}/<id>/`, each message", readme, StringComparison.Ordinal);

        var architecture = Prose.Document("docs", "architecture.md");

        Assert.Contains($"`Agents\\Loop\\{nameof(DssHarness.Core.Agents.Loop.ToolLoop)}`", architecture, StringComparison.Ordinal);

        foreach (var owner in new[]
        {
            nameof(DssHarness.Core.Agents.Loop.IChatProvider),
            nameof(DssHarness.Core.Agents.Loop.ChatFailure),
            $"{nameof(DssHarness.Core.Agents.IAgentClient)}.{nameof(DssHarness.Core.Agents.IAgentClient.BilledBy)}",
            $"{nameof(DssHarness.Core.Agents.Loop.ChatPart)}.{nameof(DssHarness.Core.Agents.Loop.ChatPart.Heard)}",
            $"{nameof(DssHarness.Core.Agents.Loop.ChatPart)}.{nameof(DssHarness.Core.Agents.Loop.ChatPart.Used)}",
            nameof(DssHarness.Core.Agents.Loop.ToolLoopTerms),
            nameof(DssHarness.Core.Agents.Runtime.AgentTurnCut),
            $"{nameof(DssHarness.Core.Agents.AgentRun)}.{nameof(DssHarness.Core.Agents.AgentRun.Interrupt)}",
            $"{nameof(DssHarness.Core.Agents.AgentTurnInterrupt)}.{nameof(DssHarness.Core.Agents.AgentTurnInterrupt.Cut)}",
            $"{nameof(DssHarness.Core.Agents.AgentCost)}.{nameof(DssHarness.Core.Agents.AgentCost.MissingPrice)}",
            nameof(DssHarness.Core.Agents.AgentModelIds),
            nameof(DssHarness.Core.Agents.AgentEndings),
            nameof(DssHarness.Core.Agents.AgentUsageTally),
            $"{nameof(DssHarness.Core.Agents.Runtime.AgentToolHost)}.Unserved",
            nameof(DssHarness.Core.Agents.StoredConversation),
            $"{nameof(DssHarness.Core.Agents.Runtime.AgentToolRuntime)}.{nameof(DssHarness.Core.Agents.Runtime.AgentToolRuntime.RunTurnAsync)}",
            $"{nameof(DssHarness.Core.Agents.Runtime.AgentToolRuntime)}.{nameof(DssHarness.Core.Agents.Runtime.AgentToolRuntime.Interrupted)}",
            $"{nameof(DssHarness.Core.Agents.Runtime.AgentToolRuntime)}.{nameof(DssHarness.Core.Agents.Runtime.AgentToolRuntime.NotRun)}",
            $"{nameof(DssHarness.Core.Agents.AgentPrices)}.{nameof(DssHarness.Core.Agents.AgentPrices.BudgetRefusal)}",
            $"{nameof(DssHarness.Core.Agents.Loop.ToolLoop)}.{nameof(DssHarness.Core.Agents.Loop.ToolLoop.Backoff)}",
            $"{nameof(DssHarness.Core.Agents.Loop.ToolLoop)}.{nameof(DssHarness.Core.Agents.Loop.ToolLoop.FirstMaxTokens)}",
            $"{nameof(DssHarness.Core.Agents.AgentMessages)}.{nameof(DssHarness.Core.Agents.AgentMessages.TryTake)}",
            $"{nameof(DssHarness.Core.Agents.AgentRun)}.{nameof(DssHarness.Core.Agents.AgentRun.SessionsDirectory)}",
            $"{nameof(DssHarness.Core.Orchestration.OrchestratorLayout)}.{nameof(DssHarness.Core.Orchestration.OrchestratorLayout.SessionDirectoryIn)}",
            $"{nameof(DssHarness.Core.Agents.Loop.ChatRequest)}.{nameof(DssHarness.Core.Agents.Loop.ChatRequest.ContextTokens)}",
            $"{nameof(DssHarness.Core.Agents.Loop.ToolLoop)}.{nameof(DssHarness.Core.Agents.Loop.ToolLoop.PausedTurn)}",
        })
        {
            Assert.Contains($"`{owner}`", architecture, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The claude-api client is said by the help and the README alike - its version, its betas, its cache, its keys, what
    /// each failure is - each name and number read from the type that holds it, and the architecture names its owners.
    /// </summary>
    [Fact]
    public void TheClaudeApiClient_IsSaidByTheHelpAndTheDocuments()
    {
        var prose = Prose.Words(HelpText.Of("agent-clients").StandardOutput);
        var readme = Prose.Document("README.md");
        var architecture = Prose.Document("docs", "architecture.md");
        var (tries, wait) = (DssHarness.Core.Agents.Loop.ToolLoopTerms.DefaultMaxRetries, DssHarness.Core.Agents.Loop.ToolLoopTerms.DefaultMaxRetryWait.TotalSeconds);
        const string Version = DssHarness.Core.Agents.ClaudeApi.ClaudeApiRequest.Version;
        const string Context = DssHarness.Core.Agents.ClaudeApi.ClaudeApiRequest.ContextManagementBeta;
        const string Credit = DssHarness.Core.Agents.ClaudeApi.ClaudeApiRequest.FallbackCreditBeta;
        const string Clear = DssHarness.Core.Agents.ClaudeApi.ClaudeApiRequest.ClearToolUses;

        Assert.Contains(
            $"each request is posted to its provider's api with /{DssHarness.Core.Agents.ClaudeApi.ClaudeApiRequest.MessagesPath} after it, signed with a key of the provider's (x-api-key), under anthropic-version: {Version}, and its answer streamed.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains($"old tool uses cleared by the server as the context fills ({Clear}, under the {Context} beta), each clearing said in a notice;", prose, StringComparison.Ordinal);
        Assert.Contains("three cache marks of the client's lifetime for agentClients.claude-api.cacheTtl (5m or 1h, 5m where nothing is said) - on the system text, covering the tools and it, the same for every step of that agent with the same tools; on the run's repository map, where a step is pushed one (graph: map), the first block of the conversation's first user message, covering the tools, the system text and the map, the same for every such step of the run that reads alike; and the top-level mark, which moves with the conversation's last block - so a step's second request on reads the map from the cache, and so does the first request of the run's next step of the same agent - and a budget needs the model's price of that cache's writes too: priceCacheWrite, or priceCacheWrite1h for an hour.", prose, StringComparison.Ordinal);
        Assert.Contains($"A step with a fallback that can go on here sends the {Credit} beta, and a request the model declines moves the step to its next model of the same provider and client - the same request sent again with the model changed and the credit the API gave sent back as its fallback_credit_token, a failure naming it answered by the same request without it - Refused with its category only where none is left; no request carries fallbacks or a server-side-fallback-* beta,", prose, StringComparison.Ordinal);
        Assert.Contains("Two keys are claude-api's alone: betas, sent beside the tool's own, each one word and never the API's fallback's, and cacheTtl; three are both API clients':", prose, StringComparison.Ordinal);
        Assert.Contains($"refused where it sets what the tool sends itself or, here, fallbacks or a sampling parameter (temperature, top_p, top_k), which anthropic-version {Version} refuses on every model, and free to add a tool choice or thinking.budget_tokens where the step's model takes one; and maxRetries and maxRetryWaitSeconds, the loop's tries and longest wait ({tries} and {wait} where nothing is said).", prose, StringComparison.Ordinal);
        Assert.Contains("a 403, a 404, or a 400 answering a conversation's first request - a model the key's organization is not given, such as one that needs a data retention the organization does not keep - is a model the provider does not serve the key, naming it;", prose, StringComparison.Ordinal);
        Assert.Contains($"A spend limit reached - the organization's monthly cap, a 429 whose error names {DssHarness.Core.Agents.ClaudeApi.ClaudeApiFailures.SpendCapCode}, or one the organization or a workspace set, a 400 that says so - is the key's limit, every entry on that key passed over until the date the API names; a 401 is a key refused, said and passed over for the rest of the run; a 402 the key's balance spent, every entry on that key passed over; a 429 the model's limit on that key, waited out within those tries and the same model's next key tried past them;", prose, StringComparison.Ordinal);
        Assert.Contains("every request's usage - one that ended without its turn, failed, silent or left, by what it said it used so far - is counted at the model's prices,", prose, StringComparison.Ordinal);

        Assert.Contains(
            $"signed with a key of the provider's (`x-api-key`), under `anthropic-version: {Version}`, and its answer streamed.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains($"old tool uses cleared by the server as the context fills (`{Clear}`, under the `{Context}` beta), each clearing said in a notice;", readme, StringComparison.Ordinal);
        Assert.Contains("three cache marks of the client's lifetime for `agentClients.claude-api.cacheTtl` (`5m` or `1h`, `5m` where nothing is said) - on the system text, covering the tools and it, the same for every step of that agent with the same tools; on the run's repository map, where a step is pushed one (`graph: map`), the first block of the conversation's first user message, covering the tools, the system text and the map, the same for every such step of the run that reads alike; and the top-level mark, which moves with the conversation's last block - so a step's second request on reads the map from the cache, and so does the first request of the run's next step of the same agent - and a budget needs the model's price of that cache's writes too: `priceCacheWrite`, or `priceCacheWrite1h` for an hour.", readme, StringComparison.Ordinal);
        Assert.Contains($"A step with a fallback that can go on here sends the `{Credit}` beta, and a request the model declines moves the step to its next model of the same provider and client - the same", readme, StringComparison.Ordinal);
        Assert.Contains("Two keys are `claude-api`'s alone: `betas`, sent beside the tool's own, each one word and never the API's fallback's, and `cacheTtl`; three are both API clients':", readme, StringComparison.Ordinal);
        Assert.Contains($"refused where it sets what the tool sends itself or, here, `fallbacks` or a sampling parameter (`temperature`, `top_p`, `top_k`), which `anthropic-version {Version}` refuses on every model, and free to add a tool choice or `thinking.budget_tokens` where the step's model takes one; and `maxRetries` and `maxRetryWaitSeconds`, the loop's tries and longest wait ({tries} and {wait} where nothing is said).", readme, StringComparison.Ordinal);
        Assert.Contains("a 403, a 404, or a 400 answering a conversation's first request - a model the key's organization is not given, such as one that needs a data retention the organization does not keep - is a model the provider does not serve the key, naming it;", readme, StringComparison.Ordinal);
        Assert.Contains($"A spend limit reached - the organization's monthly cap, a 429 whose error names `{DssHarness.Core.Agents.ClaudeApi.ClaudeApiFailures.SpendCapCode}`, or one the organization or a workspace set, a 400 that says so - is the key's limit, every entry on that key passed over until the date the API names; a 401 is a key refused, said and passed over for the rest of the run; a 402 the key's balance spent, every entry on that key passed over; a 429 the model's limit on that key, waited out within those tries and the same model's next key tried past them;", readme, StringComparison.Ordinal);
        Assert.Contains("every request's usage - one that ended without its turn, failed, silent or left, by what it said it used so far - is counted at the model's prices,", readme, StringComparison.Ordinal);

        Assert.Contains($"`Agents\\ClaudeApi\\{nameof(DssHarness.Core.Agents.ClaudeApi.ClaudeApiClient)}`", architecture, StringComparison.Ordinal);
        Assert.Contains($"`Agents\\Http\\{nameof(DssHarness.Core.Agents.Http.ApiConnection)}`", architecture, StringComparison.Ordinal);

        foreach (var owner in new[]
        {
            nameof(DssHarness.Core.Agents.ClaudeApi.ClaudeApiProvider),
            nameof(DssHarness.Core.Agents.ClaudeApi.ClaudeApiRequest),
            nameof(DssHarness.Core.Agents.ClaudeApi.ClaudeApiFailures),
            nameof(DssHarness.Core.Agents.AgentEntries),
            nameof(DssHarness.Core.Agents.Http.ApiEvent),
            nameof(DssHarness.Core.Agents.Http.ApiRecords),
            nameof(DssHarness.Core.Agents.Http.ApiRefusedBody),
            $"{nameof(DssHarness.Core.Agents.Http.ApiEvent)}.{nameof(DssHarness.Core.Agents.Http.ApiEvent.Refusing)}",
            $"{nameof(DssHarness.Core.Agents.ClaudeApi.ClaudeApiRequest)}.{nameof(DssHarness.Core.Agents.ClaudeApi.ClaudeApiRequest.Owned)}",
            nameof(DssHarness.Core.Agents.ClaudeApi.ClaudeApiRequest.ExtrasProblems),
        })
        {
            Assert.Contains($"`{owner}`", architecture, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The envelope every client asks for where a step names an output is said alike by help agent-clients and the README -
    /// each member, file, bound and number from its owner - and the architecture names its owners by the types that are them.
    /// </summary>
    [Fact]
    public void TheEnvelope_IsSaidByTheHelpAndTheDocuments()
    {
        var prose = Prose.Words(HelpText.Of("agent-clients").StandardOutput);
        var readme = Prose.Document("README.md");
        var architecture = Prose.Document("docs", "architecture.md");
        var repairs = new DssHarness.Core.Agents.AgentOutput("o", "{}").MaxRepairs;
        static string Members(System.Text.Json.Nodes.JsonObject shape) => string.Join(", ", shape["properties"]!.AsObject().Select(member => member.Key));
        var (assumption, question) = (Members(DssHarness.Core.Agents.AgentEnvelope.Assumption()), Members(DssHarness.Core.Agents.AgentEnvelope.Question()));

        Assert.Contains("Where a step names an output, every client asks the agent for one envelope - notes, output, assumptions, and questions where the step may ask and has rounds left - and the harness's own validator holds the answer to it, whatever a client was sent.", prose, StringComparison.Ordinal);
        Assert.Contains("Where a step names an output, every client asks the agent for one envelope - `notes`, `output`, `assumptions`, and `questions` where the step may ask and has rounds left - and the harness's own validator holds the answer to it, whatever a client was sent.", readme, StringComparison.Ordinal);
        Assert.Contains($"The notes are not empty, and are kept, masked, as the step's {DssHarness.Core.Orchestration.OrchestratorLayout.StepNotesFileName} beside its result;", prose, StringComparison.Ordinal);
        Assert.Contains($"the output is read against the step's whole schema, each problem named by its path under $.output, a text an enum or a const holds in another case read as the schema spells it; each assumption is {{{assumption}}}; each question is {{{question}}}, with at least {DssHarness.Core.Agents.AgentEnvelope.FewestOptions} options, no two alike, recommending one of them word for word; no text of either is empty.", prose, StringComparison.Ordinal);
        Assert.Contains($"the output is read against the step's whole schema, each problem named by its path under `$.output`, a text an `enum` or a `const` holds in another case read as the schema spells it; each assumption is `{{{assumption}}}`; each question is `{{{question}}}`, with at least {DssHarness.Core.Agents.AgentEnvelope.FewestOptions} options, no two alike, recommending one of them word for word; no text of either is empty.", readme, StringComparison.Ordinal);
        Assert.Contains("The output is null only in a handoff, whose notes are the handoff, or where the answer asks, and the step then ends Asked with its questions; an answer with both is sent back, and so is one that asks where the step may not or has asked as often as it may, told to write what it would have asked under assumptions.", prose, StringComparison.Ordinal);
        Assert.Contains("The output is null only in a handoff, whose notes are the handoff, or where the answer asks, and the step then ends `Asked` with its questions; an answer with both is sent back, and so is one that asks where the step may not or has asked as often as it may, told to write what it would have asked under assumptions.", readme, StringComparison.Ordinal);
        Assert.Contains("The claude-code client holds the answer to the envelope by --json-schema;", prose, StringComparison.Ordinal);
        Assert.Contains("The `claude-code` client holds the answer to the envelope by `--json-schema`;", readme, StringComparison.Ordinal);
        Assert.Contains("an answer sent back (below) and a message the engine gives an agent at work are written to the same launch;", readme, StringComparison.Ordinal);
        Assert.Contains($"the claude-api client sends it as output_config.format on every request, the handoff's too, each array's minItems above {DssHarness.Core.Agents.ClaudeApi.ClaudeApiRequest.MostLeastItems} and every maxItems moved into the array's description in words, for structured outputs take no other - and where the API refuses it as more than it compiles, the run's later requests carry no format, and a message of the conversation says it in words, the system prompt untouched;", prose, StringComparison.Ordinal);
        Assert.Contains($"the `claude-api` client sends it as `output_config.format` on every request, the handoff's too, each array's `minItems` above {DssHarness.Core.Agents.ClaudeApi.ClaudeApiRequest.MostLeastItems} and every `maxItems` moved into the array's description in words, for structured outputs take no other - and where the API refuses it as more than it compiles, the run's later requests carry no format, and a message of the conversation says it in words, the system prompt untouched;", readme, StringComparison.Ordinal);
        Assert.Contains("and asks an answer that read as no JSON again in JSON alone - no tool offered, response_format a JSON object.", prose, StringComparison.Ordinal);
        Assert.Contains("and asks an answer that read as no JSON again in JSON alone - no tool offered, `response_format` a JSON object.", readme, StringComparison.Ordinal);
        Assert.Contains($"An answer that does not read is sent back in the same conversation, {repairs} times at most, and the step is then InvalidOutput.", prose, StringComparison.Ordinal);
        Assert.Contains($"An answer that does not read is sent back in the same conversation, {repairs} times at most, and the step is then `InvalidOutput`.", readme, StringComparison.Ordinal);

        foreach (var owner in new[]
        {
            nameof(DssHarness.Core.Agents.AgentEnvelope),
            nameof(DssHarness.Core.Agents.AgentAssumption),
            nameof(DssHarness.Core.Agents.AgentQuestion),
            nameof(DssHarness.Core.Agents.StructuredOutputGate),
            $"{nameof(DssHarness.Core.Agents.AgentOutput)}.{nameof(DssHarness.Core.Agents.AgentOutput.AskRounds)}",
            $"{nameof(DssHarness.Core.Schemas.JsonSchemaSubset)}.{nameof(DssHarness.Core.Schemas.JsonSchemaSubset.Respelled)}",
            $"{nameof(DssHarness.Core.Schemas.JsonSchemaSubset)}.{nameof(DssHarness.Core.Schemas.JsonSchemaSubset.SchemasIn)}",
            $"{nameof(DssHarness.Core.Orchestration.OrchestratorLayout)}.{nameof(DssHarness.Core.Orchestration.OrchestratorLayout.StepNotesFileName)}",
            $"{nameof(DssHarness.Core.Agents.Loop.ChatRequest)}.{nameof(DssHarness.Core.Agents.Loop.ChatRequest.Schema)}",
            $"{nameof(DssHarness.Core.Agents.Loop.ChatRequest)}.{nameof(DssHarness.Core.Agents.Loop.ChatRequest.JsonAlone)}",
            $"{nameof(DssHarness.Core.Agents.Loop.IChatProvider)}.{nameof(DssHarness.Core.Agents.Loop.IChatProvider.SendsSchema)}",
            $"{nameof(DssHarness.Core.Agents.ClaudeApi.ClaudeApiRequest)}.{nameof(DssHarness.Core.Agents.ClaudeApi.ClaudeApiRequest.Sent)}",
            $"{nameof(DssHarness.Core.Agents.AgentEnvelope)}.{nameof(DssHarness.Core.Agents.AgentEnvelope.InWords)}",
            $"{nameof(DssHarness.Core.Agents.Loop.ChatFailureKind)}.{nameof(DssHarness.Core.Agents.Loop.ChatFailureKind.SchemaRefused)}",
            $"{nameof(DssHarness.Core.Agents.AgentMessageKind)}.{nameof(DssHarness.Core.Agents.AgentMessageKind.EnvelopeInWords)}",
            $"{nameof(DssHarness.Core.Agents.ClaudeApi.ClaudeApiFailures)}.{nameof(DssHarness.Core.Agents.ClaudeApi.ClaudeApiFailures.RefusesTheFormat)}",
            $"{nameof(DssHarness.Core.Agents.AgentAssumption)}.{nameof(DssHarness.Core.Agents.AgentAssumption.Problems)}",
            $"{nameof(DssHarness.Core.Agents.AgentQuestion)}.{nameof(DssHarness.Core.Agents.AgentQuestion.Problems)}",
        })
        {
            Assert.Contains($"`{owner}`", architecture, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The glm client is said by the help and the README alike - its request, its efforts, the reasoning it keeps, where
    /// it sends a conversation shortened, its keys and each of Z.AI's codes from the one table of them - each name and
    /// number read from the type that holds it, and the architecture names its owners.
    /// </summary>
    [Fact]
    public void TheGlmClient_IsSaidByTheHelpAndTheDocuments()
    {
        var prose = Prose.Words(HelpText.Of("agent-clients").StandardOutput);
        var readme = Prose.Document("README.md");
        var architecture = Prose.Document("docs", "architecture.md");
        const string Path = DssHarness.Core.Agents.ZaiApi.ZaiApiRequest.CompletionsPath;
        const string Whole = "Z.AI clears nothing of a conversation itself, and every request sends it whole, each answer to a call as it was; once what the model last read and wrote - Z.AI's own count of it - reaches the model's {0}contextTokens{0}, the next request would pass the context and is not sent: the context is exhausted ({0}ContextExhausted{0}), as where Z.AI refuses a prompt too long, and the step's answer to a full context - its compaction among them - carries the work on.";

        Assert.Contains($"each request is posted to its provider's api with /{Path} after it, signed with a key of the provider's (Authorization: Bearer), and its answer streamed.", prose, StringComparison.Ordinal);
        Assert.Contains($"each request is posted to its provider's `api` with `/{Path}` after it, signed with a key of the provider's (`Authorization: Bearer`), and its answer streamed.", readme, StringComparison.Ordinal);
        Assert.Contains("the model's thinking on, the step's effort as reasoning_effort (low, medium, high, xhigh, max); and max_tokens - no sampling parameter or tool choice of the tool's own, which requestExtras may add, and no tool of Z.AI's, for the web is the harness's own.", prose, StringComparison.Ordinal);
        Assert.Contains("the model's thinking on, the step's effort as `reasoning_effort` (`low`, `medium`, `high`, `xhigh`, `max`); and `max_tokens` - no sampling parameter or tool choice of the tool's own, which `requestExtras` may add, and no tool of Z.AI's, for the web is the harness's own.", readme, StringComparison.Ordinal);
        Assert.Contains("unless agentClients.glm.preserveThinking is true (false where nothing is said), which keeps it (thinking.clear_thinking: false).", prose, StringComparison.Ordinal);
        Assert.Contains("unless `agentClients.glm.preserveThinking` is true (false where nothing is said), which keeps it (`thinking.clear_thinking: false`).", readme, StringComparison.Ordinal);
        Assert.Contains(string.Format(CultureInfo.InvariantCulture, Whole, string.Empty), prose, StringComparison.Ordinal);
        Assert.Contains(string.Format(CultureInfo.InvariantCulture, Whole, "`"), readme, StringComparison.Ordinal);
        Assert.Contains("and one is glm's alone: preserveThinking.", prose, StringComparison.Ordinal);
        Assert.Contains("and one is `glm`'s alone: `preserveThinking`.", readme, StringComparison.Ordinal);

        // Every row of the codes' table is said, in its order, with what the loop takes it for.
        foreach (var row in DssHarness.Core.Agents.ZaiApi.ZaiApiFailures.Codes)
        {
            Assert.Contains($"{row.What} ({string.Join(", ", row.Codes)})", prose, StringComparison.Ordinal);
            Assert.Contains($"{row.What} ({string.Join(", ", row.Codes)})", readme, StringComparison.Ordinal);
        }

        Assert.Contains("a usage limit that names when it resets (1308, 1310, 1316, 1317, 1318, 1319, 1320, 1321) is the key's limit, every", prose, StringComparison.Ordinal);
        Assert.Contains("entry on that key passed over until it resets;", readme, StringComparison.Ordinal);
        Assert.Contains("a model unknown, an API the key may not call or a plan without the model (1211, 1220, 1311) is a model the provider does not serve the key;", prose, StringComparison.Ordinal);
        Assert.Contains("a plan that does not cover the call or has ended (1309, 1313, 1314, 1315) fails the step. A code that list does not hold is read by the status:", prose, StringComparison.Ordinal);
        Assert.Contains("read by the status: a 401 is a key refused, said and passed over for the rest of the run; a 429 is the model's limit on that key, waited out within the tries and the same model's next key tried past them; a 413 is the context exhausted (ContextExhausted); a 403 or a 404 is a model the provider does not serve the key; a request timed out, a conflict or a server's error is asked again, and any other fails the step.", prose, StringComparison.Ordinal);
        Assert.Contains("read by the status: a 401 is a key refused, said and passed over for the rest of the run; a 429 is the model's limit on that key, waited out within the tries and the same model's next key tried past them; a 413 is the context exhausted (`ContextExhausted`); a 403 or a 404 is a model the provider does not serve the key; a request timed out, a conflict or a server's error is asked again, and any other fails the step.", readme, StringComparison.Ordinal);
        Assert.Contains("a failure Z.AI names is said in its own words.", prose, StringComparison.Ordinal);
        Assert.Contains("a failure Z.AI names is said in its own words.", readme, StringComparison.Ordinal);

        // Every row of the statuses' table is said, with what the loop takes it for.
        foreach (var row in DssHarness.Core.Agents.ZaiApi.ZaiApiFailures.Statuses)
        {
            Assert.Contains($"{string.Join(" or ", row.Statuses.Select(status => $"a {status}"))} is ", prose, StringComparison.Ordinal);
        }
        Assert.Contains("a plan that does not cover the call or has ended (1309, 1313, 1314, 1315) fails the step. A code that list does not hold is read by the status:", readme, StringComparison.Ordinal);
        Assert.Contains($"(1211, 1220, 1311) is a model the provider does not serve the key;", readme, StringComparison.Ordinal);

        Assert.Contains($"`Agents\\ZaiApi\\{nameof(DssHarness.Core.Agents.ZaiApi.ZaiApiClient)}`", architecture, StringComparison.Ordinal);

        foreach (var owner in new[]
        {
            nameof(DssHarness.Core.Agents.ZaiApi.ZaiApiProvider),
            nameof(DssHarness.Core.Agents.ZaiApi.ZaiApiRequest),
            nameof(DssHarness.Core.Agents.ZaiApi.ZaiApiFailures),
            nameof(DssHarness.Core.Agents.Http.ApiBody),
            nameof(DssHarness.Core.Agents.Http.ApiAnswers),
            nameof(DssHarness.Core.Agents.Http.OwnedMember),
            $"{nameof(DssHarness.Core.Agents.Loop.ToolLoopTerms)}.{nameof(DssHarness.Core.Agents.Loop.ToolLoopTerms.Of)}",
            $"{nameof(DssHarness.Core.Agents.Loop.ToolLoopTerms)}.{nameof(DssHarness.Core.Agents.Loop.ToolLoopTerms.Unstartable)}",
            $"{nameof(DssHarness.Core.Agents.ZaiApi.ZaiApiRequest)}.{nameof(DssHarness.Core.Agents.ZaiApi.ZaiApiRequest.Owned)}",
            $"{nameof(DssHarness.Core.Agents.ZaiApi.ZaiApiProvider)}.{nameof(DssHarness.Core.Agents.ZaiApi.ZaiApiProvider.ContextFull)}",
            $"{nameof(DssHarness.Core.Agents.ZaiApi.ZaiApiFailures)}.{nameof(DssHarness.Core.Agents.ZaiApi.ZaiApiFailures.Codes)}",
            $"{nameof(DssHarness.Core.Agents.ZaiApi.ZaiApiFailures)}.{nameof(DssHarness.Core.Agents.ZaiApi.ZaiApiFailures.Statuses)}",
        })
        {
            Assert.Contains($"`{owner}`", architecture, StringComparison.Ordinal);
        }
    }
}
