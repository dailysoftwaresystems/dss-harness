using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.ClaudeCode;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The run's reporter: one reason step that only reads, run by the reason step's
/// own runner as the advisor is, over the engine's facts and the run's records alone - its reads narrowed to the run's
/// directory and nowhere else, offered no command, no write and no web, its answer masked as the run's records are -
/// worked under the run's <c>asks/&lt;stamp&gt;/</c>, a directory the engine never reads. Nothing of the run is written
/// or locked, nothing of the machine is changed - a Claude Code it needs is only found, never installed or brought up to
/// date - and what it costs is the person's, never the run's. Whatever keeps it from wording the facts is why no model
/// worded them, with the code it means; the asking itself never fails over it.
/// </summary>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="platform">This machine.</param>
/// <param name="runner">Works the reporter, whatever the client is.</param>
/// <param name="fileSystem">The file system the workflow's files are read on.</param>
/// <param name="keys">The slot the entry's keys are read from.</param>
/// <param name="isolation">Decides the operating system's isolation the reporter runs under: none, for it runs no command.</param>
/// <param name="updates">Finds the Claude Code the reporter's order starts, where it holds the client that starts one; null where nothing does.</param>
public sealed class ReporterRunner(
    IHarnessContextLoader contextLoader,
    IHostPlatform platform,
    IAgentRunner runner,
    IFileSystem fileSystem,
    Secrets.AgentKeyStore keys,
    Agents.Isolation.IAgentIsolation isolation,
    ClaudeCodeUpdateService? updates = null) : IFlowReporterRunner
{
    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly IHostPlatform _platform = platform;
    private readonly IAgentRunner _runner = runner;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly ReasonLaunch _launch = new(fileSystem, platform, keys, updates, isolation);

    /// <inheritdoc/>
    public async Task<FlowReported> ReportAsync(FlowReporterCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);

        try
        {
            return await WordedAsync(call, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception failed)
        {
            // Whatever kept the reporter from wording the facts - its definition or prompt not reading, the slot or the
            // configuration refusing, its client failing - is why no model worded them, with the code it means by the
            // one mapping every command's failure is read by.
            var meaning = KnownCauses.Meaning(failed);

            return Wordless(meaning.Message, meaning.ExitCode);
        }
    }

    /// <summary>The reporter's run: its definition read, its model resolved, its launch built and its answer masked.</summary>
    /// <param name="call">The asking.</param>
    /// <param name="cancellationToken">Ends the asking.</param>
    private async Task<FlowReported> WordedAsync(FlowReporterCall call, CancellationToken cancellationToken)
    {
        var (place, context) = await FlowStepRunners.PlaceOf(
            new FlowStepRun(
                new WorkflowStep { Name = "reporter", Type = "reason" },
                call.Stamp,
                Attempt: 1,
                FlowValue.Missing,
                null,
                null,
                [],
                new NoEvents(),
                Handoff: null,
                Issue: null,
                Visit: 1,
                call.Place),
            _contextLoader,
            _platform,
            cancellationToken).ConfigureAwait(false);
        var layout = OrchestratorLayout.Of(context.Layout, place.Run);
        var files = new WorkflowNamedFiles(_fileSystem, _platform.PathComparison);

        // The reporter's own definition, read as the definition's check reads it - the file the orchestrator names,
        // which the check holds to be there.
        var (definition, problems) = AgentDefinitionParser.Parse(files.ReadText(context.Layout, call.Workflow, $"agents/{call.Reporter.Agent}.md", "an agent definition"), call.Reporter.Agent);

        if (problems.Count > 0 || definition is null)
        {
            return Wordless($"agents/{call.Reporter.Agent}.md does not read: {string.Join("; ", problems)}", HarnessExit.ConfigInvalid);
        }

        if (AgentProviders.Resolve(context.Config) is not { Providers: { } providers })
        {
            return Wordless("the configuration's agent providers do not resolve, so the reporter has no model to run on", HarnessExit.ConfigInvalid);
        }

        var resolution = AgentResolution.Of(providers, call.Reporter.Tier, call.Reporter.Model, call.Reporter.Effort, call.Reporter.EffortByModel, call.Reporter.Client, definition);

        if (resolution is null)
        {
            return Wordless("the reporter has no model to run on: name a tier or a model on it, or a tier under defaults.agentTier", HarnessExit.ConfigInvalid);
        }

        var order = _launch.Order(providers, resolution, context.Layout);

        // The reporter only reads, so a Claude Code its order starts is only found: one that is not there, or is too old,
        // is why no model worded the facts, and installing or updating it is a run's step's or a side-task's to do.
        var program = await _launch.ProgramAsync(context.Layout, providers, order, ClaudeCodeAsker.Run(" (the run's reporter)"), change: false, cancellationToken).ConfigureAwait(false);

        if (program is { Ready: false })
        {
            return Wordless(program.Message, program.ExitCode);
        }

        // The reporter works over the run's records alone: its work directory, its records and the conversations a client
        // stores under the call's own asks/ directory, which is inside the run's and which the engine never reads - never
        // under an agent's of the run - and its reads narrowed to the run's directory by the policy's one rule for it.
        var asks = Path.Combine(layout.AsksDirectory, call.Stamp);
        var workspace = _launch.Workspace(record: null, context.Layout, Path.Combine(asks, "work"), layout.Directory);
        var ground = AgentStepGround.Of(
            context.Config,
            definition,
            new WorkflowStep { Name = "reporter", Type = "reason" },
            workspace,
            webSearchGiven: false,
            _launch.ClaudeDirectories(context.Config, context.Layout),
            _launch.CredentialPlaces(context.Config),
            readsOnly: true) with
        {
            ReporterRoot = layout.Directory,
        };
        var mask = _launch.Mask(context.Layout, providers);

        // The reporter runs no command, so it needs no isolation of the operating system's; its records say so.
        var isolated = await _launch.IsolationAsync(context.Config, ground, context.Layout, asks, cancellationToken).ConfigureAwait(false);

        if (isolated.Refusal("the reporter") is { } unisolated)
        {
            return Wordless(unisolated, HarnessExit.ToolMissing);
        }

        var request = new AgentStepRequest(
            call.Run,
            call.Reporter.Agent,
            call.Stamp,
            Attempt: 1,
            call.Reporter.Agent,
            Provider: null,
            resolution.Serving.Client,
            call.Reporter.Tier,
            resolution.Serving.Model,
            call.Reporter.Effort,
            Fallback: null,
            definition.Prompt,
            PromptOf(files, context.Layout, call),
            ResumeTemplate: null,
            workspace,
            Output: null,
            Conversation: null,
            new AgentLimits(definition.MaxTurns, definition.MaxBudgetUsd, definition.MaxMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : null, definition.StallSeconds, place.Definition.HandoffSeconds),
            mask)
        {
            Ground = ground,
            Isolation = isolated,
            Credential = order.FirstOrDefault(entry => entry.Usable)?.Credential,
            Withheld = [.. providers.Appointed().Select(appointed => appointed.Name)],
            Order = order,
            Retries = call.Reporter.Retries,
            BackoffSeconds = call.Reporter.BackoffSeconds,
            RecordDirectory = asks,
            SessionsDirectory = Path.Combine(asks, OrchestratorLayout.SessionsDirectoryName),
            Program = program?.Program,
            ProgramVersion = program?.Version,
        };

        // What the reporter spends is counted as every agent run of the engine's is, from its turns as they come: every
        // token of every kind, at the configuration's prices.
        var spend = new AgentRunSpend(providers, resolution.Serving.Model);
        var result = await _runner.RunAsync(request, spend, cancellationToken).ConfigureAwait(false);

        // The answer is masked as the run's records are: whatever the reporter read or was told, no secret the slot holds
        // reaches the person's terminal or the call's record in its words. A run that completed without a word worded
        // nothing, and failed: the asking it was for is refused as a failed command, never answered as one that passed.
        return result.Problem() is { } wrong
            ? Wordless($"the reporter answered a result that does not hold together: {wrong}", HarnessExit.CommandFailed, result, spend)
            : (result.Status, result.FinalText) switch
            {
                (AgentRunStatus.Completed, { Length: > 0 } worded) => SpentBy(new FlowReported(mask?.Redact(worded) ?? worded, Model: result.Model), spend),
                (AgentRunStatus.Completed, _) => Wordless("the reporter's run completed without a word", HarnessExit.CommandFailed, result, spend),
                _ => Wordless(result.Error is { } error ? mask?.Redact(error) ?? error : $"the reporter's run ended {result.Status}", result.ToOutcome().ExitCode, result, spend),
            };
    }

    /// <summary>Why no model worded the facts, with the code it means and what the reporter's run cost where one ran.</summary>
    /// <param name="why">Why, said whole.</param>
    /// <param name="exitCode">The code it means.</param>
    /// <param name="result">The reporter's run, where one ran.</param>
    /// <param name="spend">What the reporter's run spent, where one ran.</param>
    private static FlowReported Wordless(string why, int exitCode, AgentRunResult? result = null, AgentRunSpend? spend = null)
        => SpentBy(new(Answer: null, WhyNot: why, ExitCode: exitCode, result?.Model), spend);

    /// <summary>
    /// <paramref name="reported"/> with what the reporter's run spent, as its count says it: the dollars counted, why the
    /// rest is not known where a turn's cost could not be counted, and the tokens - nothing where no turn was reported.
    /// </summary>
    /// <param name="reported">The answer.</param>
    /// <param name="spend">The run's count; null where no run was made.</param>
    private static FlowReported SpentBy(FlowReported reported, AgentRunSpend? spend)
        => spend?.Spent is { } spent
            ? reported with { Usd = spent.Dollars.Counted, Tokens = spent.Tokens, Uncounted = spent.Dollars.Uncounted }
            : reported;

    /// <summary>
    /// The reporter's prompt, filled with what it reads: the facts as text, the person's question where one was typed,
    /// and where the run's records are - the only three names its prompts may read.
    /// </summary>
    /// <param name="files">Reads the files the workflow names.</param>
    /// <param name="layout">The repository's layout.</param>
    /// <param name="call">The asking.</param>
    private static string PromptOf(WorkflowNamedFiles files, HarnessLayout layout, FlowReporterCall call)
    {
        var template = files.ReadText(layout, call.Workflow, call.Question is null ? call.Reporter.StatusPrompt : call.Reporter.AskPrompt, call.Question is null ? "a reporter status prompt" : "a reporter ask prompt");

        return FlowTemplate.Fill(
            template,
            path => path.Root switch
            {
                // The status as the three names its prompts read: the facts as text, the person's question where one
                // was typed, and where the run's records are.
                "status" => new(FlowValue.Of(new JsonObject
                {
                    ["facts"] = call.Facts,
                    ["question"] = call.Question ?? string.Empty,
                    ["records"] = call.Records,
                }), 0),
                _ => new(FlowValue.Missing, 0),
            },
            out var problem)
            ?? throw new HarnessException(HarnessExit.ConfigInvalid, problem!);
    }

    /// <summary>The events of a reporter's run, which tells the run nothing while it works - its answer is all it says.</summary>
    private sealed class NoEvents : IFlowRunEvents
    {
        public void LegPlaced(string leg, bool inACopy) { }

        public void EveryLegPlaced() { }

        public void LegCopyMade(string leg) { }

        public void LegEnded(string leg) { }

        public void Used(long tokens, double? usd, string? uncounted = null) { }

        public void HandoffTaken() { }
    }
}
