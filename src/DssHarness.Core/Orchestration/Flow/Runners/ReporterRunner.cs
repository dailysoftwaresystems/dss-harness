using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The run's reporter (part 04, "Asking a run about itself"): one reason step that only reads, run by the reason step's
/// own runner as the advisor is, over the engine's facts and the run's records alone - its reads narrowed to the run's
/// directory and nowhere else, offered no command, no write and no web, its answer masked - worked under the run's
/// <c>asks/&lt;stamp&gt;/</c>, a directory the engine never reads. Nothing of the run is written or locked, and what it
/// costs is the person's, never the run's.
/// </summary>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="platform">This machine.</param>
/// <param name="runner">Works the reporter, whatever the client is.</param>
/// <param name="fileSystem">The file system the workflow's files are read on.</param>
/// <param name="keys">The slot the entry's keys are read from.</param>
public sealed class ReporterRunner(
    IHarnessContextLoader contextLoader,
    IHostPlatform platform,
    IAgentRunner runner,
    IFileSystem fileSystem,
    Secrets.AgentKeyStore keys) : IFlowReporterRunner
{
    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly IHostPlatform _platform = platform;
    private readonly IAgentRunner _runner = runner;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly Secrets.AgentKeyStore _keys = keys;

    /// <inheritdoc/>
    public async Task<FlowReported> ReportAsync(FlowReporterCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);

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
            throw new HarnessException(HarnessExit.ConfigInvalid, $"agents/{call.Reporter.Agent}.md does not read: {string.Join("; ", problems)}");
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

        // The reporter works over the run's records alone: its work directory under the call's own asks/ directory,
        // which is inside the run's, and its reads narrowed to the run's directory by the policy's one rule for it.
        var asks = Path.Combine(layout.AsksDirectory, call.Stamp);
        var work = Path.Combine(asks, "work");
        var workspace = new AgentWorkspace(
            Worktree: null,
            work,
            context.Layout.MainCheckoutRoot,
            [],
            work,
            layout.Directory);
        var order = AgentStepOrder.Of(providers, resolution, context.Layout, _keys, new ClaudeTranscripts(_fileSystem, _platform).ConfigDirectory, spent: null);
        var ground = AgentStepGround.Of(
            context.Config,
            definition,
            new WorkflowStep { Name = "reporter", Type = "reason" },
            workspace,
            webSearchGiven: false,
            [.. order.Select(entry => entry.Credential?.ConfigDirectory).OfType<string>()],
            context.Layout.WorkflowsEnvSlot) with
        {
            ReporterRoot = layout.Directory,
        };

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
            new Dictionary<string, string>(StringComparer.Ordinal),
            ResumeTemplate: null,
            workspace,
            Output: null,
            Conversation: null,
            new AgentLimits(definition.MaxTurns, definition.MaxBudgetUsd, definition.MaxMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : null, definition.StallSeconds, place.Definition.HandoffSeconds),
            Mask: null)
        {
            Ground = ground,
            Credential = order.FirstOrDefault(entry => entry.Usable)?.Credential,
            Withheld = [.. providers.Appointed().Select(appointed => appointed.Name)],
            Order = order,
            Retries = call.Reporter.Retries,
            BackoffSeconds = call.Reporter.BackoffSeconds,
            RecordDirectory = asks,
        };

        try
        {
            var result = await _runner.RunAsync(request, events: null, cancellationToken).ConfigureAwait(false);

            return result.Problem() is { } wrong
                ? Wordless($"the reporter answered a result that does not hold together: {wrong}", HarnessExit.CommandFailed, result)
                : result.Status == AgentRunStatus.Completed && result.FinalText is { Length: > 0 }
                    ? new FlowReported(result.FinalText, Model: result.Model, Usd: (double?)result.Usage?.Cost?.Usd, Tokens: result.Usage?.Total.OutputTokens)
                    : Wordless(result.Error ?? $"the reporter's run ended {result.Status}", result.ToOutcome().ExitCode, result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HarnessException refused)
        {
            return Wordless(refused.Message, refused.ExitCode);
        }
        catch (Exception failed)
        {
            return Wordless(failed.Message, HarnessExit.CommandFailed);
        }

        FlowReported Wordless(string why, int exitCode, AgentRunResult? result = null)
            => new(Answer: null, WhyNot: why, ExitCode: exitCode, result?.Model, (double?)result?.Usage?.Cost?.Usd, result?.Usage?.Total.OutputTokens);
    }

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

        public void Used(long tokens, double usd) { }

        public void HandoffTaken() { }
    }
}
