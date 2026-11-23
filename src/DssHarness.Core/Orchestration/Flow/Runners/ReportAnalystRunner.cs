using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.ClaudeCode;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Json;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Schemas;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The run's analyst: one reason step that only reads, as the reporter is, over the run's deterministic report and the
/// run's records alone - its reads narrowed to the run's directory and nowhere else, offered no command, no write and
/// no web, its answer masked as the run's records are - worked under the run's <c>analysis/&lt;stamp&gt;/</c>, a
/// directory the engine never reads. Nothing of the run is written or locked, nothing of the machine is changed, and
/// what it costs is the person's, never the run's. Its answer is held twice over: to the schema the tool ships
/// (<c>analysis.schema.json</c>, read as every step's schema is) and to the report it read - every number a pain
/// point cites must be the report's own, and an answer that invents one is asked again as a schema's repair asks,
/// twice at most, then refused with its problems. The model it runs on is the tier <c>reporting</c> names in
/// <c>tierConfig</c>; where the configuration names none, that is why no model analysed it.
/// </summary>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="platform">This machine.</param>
/// <param name="runner">Works the analyst, whatever the client is.</param>
/// <param name="fileSystem">The file system the run's records are read on.</param>
/// <param name="keys">The slot the entry's keys are read from.</param>
/// <param name="isolation">Decides the operating system's isolation the analyst runs under: none, for it runs no command.</param>
/// <param name="updates">Finds the Claude Code the analyst's order starts, where it holds the client that starts one; null where nothing does.</param>
public sealed class ReportAnalystRunner(
    IHarnessContextLoader contextLoader,
    IHostPlatform platform,
    IAgentRunner runner,
    IFileSystem fileSystem,
    Secrets.AgentKeyStore keys,
    Agents.Isolation.IAgentIsolation isolation,
    ClaudeCodeUpdateService? updates = null) : IFlowAnalystRunner
{
    private const int Repairs = 2;

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly IHostPlatform _platform = platform;
    private readonly IAgentRunner _runner = runner;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly ReasonLaunch _launch = new(fileSystem, platform, keys, updates, isolation);

    /// <inheritdoc/>
    public async Task<FlowAnalysed> AnalyzeAsync(FlowAnalystCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);

        try
        {
            return await AnalysedAsync(call, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception failed)
        {
            // Whatever kept the analyst from answering - the tool's own files not reading, the slot or the
            // configuration refusing, its client failing - is why no model analysed it, with the code it means by the
            // one mapping every command's failure is read by.
            var meaning = KnownCauses.Meaning(failed);

            return Wordless(meaning.Message, meaning.ExitCode);
        }
    }

    /// <summary>The analyst's run: the tool's own files read, its model resolved, its launch built and its answer held to the report.</summary>
    /// <param name="call">The analysis asked.</param>
    /// <param name="cancellationToken">Ends the analysis.</param>
    private async Task<FlowAnalysed> AnalysedAsync(FlowAnalystCall call, CancellationToken cancellationToken)
    {
        var (place, context) = await FlowStepRunners.PlaceOf(
            new FlowStepRun(
                new WorkflowStep { Name = "report-analyst", Type = "reason" },
                call.Stamp,
                Attempt: 1,
                FlowValue.Missing,
                null,
                null,
                [],
                FlowRunEvents.None,
                Handoff: null,
                Issue: null,
                Visit: 1,
                call.Place),
            _contextLoader,
            _platform,
            cancellationToken).ConfigureAwait(false);
        var layout = OrchestratorLayout.Of(context.Layout, place.Run);

        // The analyst is the tool's own, beside the reporter it takes after: its agent, its prompt and its schema are
        // the ones this build ships, read from the build itself - never a repository's, so an analysis of one
        // repository's run is the same analyst every repository's gets.
        if (WorkflowTemplates.TextOf($"agents/{WorkflowTemplates.AnalystAgent}.md") is not { } agentFile
            || WorkflowTemplates.TextOf($"prompts/{WorkflowTemplates.AnalystPrompt}.md") is not { } promptFile
            || WorkflowTemplates.TextOf($"schemas/{WorkflowTemplates.AnalystSchema}.schema.json") is not { } schemaFile)
        {
            return Wordless("this build ships no report analyst: its agent, prompt and schema are incomplete in it", HarnessExit.InternalError);
        }

        var (definition, problems) = AgentDefinitionParser.Parse(agentFile, WorkflowTemplates.AnalystAgent);

        if (problems.Count > 0 || definition is null)
        {
            return Wordless($"the shipped analyst does not read: {string.Join("; ", problems)}", HarnessExit.InternalError);
        }

        var schema = JsonSchemaSubset.Whole(schemaFile);

        if (schema.Problems is [_, ..] unreadable)
        {
            return Wordless($"the shipped analyst's schema does not read: {string.Join("; ", unreadable)}", HarnessExit.InternalError);
        }

        if (AgentProviders.Resolve(context.Config) is not { Providers: { } providers })
        {
            return Wordless("the configuration's agent providers do not resolve, so the analyst has no model to run on", HarnessExit.ConfigInvalid);
        }

        // The analyst runs on the tier 'reporting' names in tierConfig, and on nothing else: where the configuration
        // defines no such tier, the verb is refused naming the key to set - an analysis is a paid run, and which model
        // pays for it is a person's decision, not a default.
        var resolution = AgentResolution.Of(providers, "reporting", null, null, null, null, definition);

        if (resolution is null)
        {
            var defined = string.Join(", ", providers.Tiers);

            return Wordless(
                $"tierConfig names no 'reporting' tier for the analyst to run on (defined: {(defined.Length == 0 ? "none" : defined)}); add one - \"reporting\": [a model to word the analyses] - under tierConfig",
                HarnessExit.ConfigInvalid);
        }

        var order = _launch.Order(providers, resolution, context.Layout);

        // The analyst only reads, as the reporter does: a Claude Code its order starts is only found, and installing
        // or updating it is a run's step's or a side-task's to do.
        var program = await _launch.ProgramAsync(context.Layout, providers, order, ClaudeCodeAsker.Run(" (the run's analyst)"), change: false, cancellationToken).ConfigureAwait(false);

        if (program.Refusal is { } notFound)
        {
            return Wordless(notFound.Message, notFound.ExitCode);
        }

        order = program.Order;

        // The analyst works over the run's records alone, under the analysis's own directory inside the run's - which
        // the engine never reads - and its reads narrowed to the run's directory by the policy's one rule for it.
        var analysis = Path.Combine(layout.AnalysisDirectory, call.Stamp);
        var workspace = _launch.Workspace(record: null, context.Layout, Path.Combine(analysis, "work"), layout.Directory);
        var ground = AgentStepGround.Of(
            context.Config,
            definition,
            new WorkflowStep { Name = "report-analyst", Type = "reason" },
            workspace,
            webSearchGiven: false,
            _launch.ClaudeDirectories(context.Config, context.Layout),
            _launch.CredentialPlaces(context.Config),
            readsOnly: true) with
        {
            ReporterRoot = layout.Directory,
        };
        var mask = _launch.Mask(context.Layout, providers);
        var isolated = await _launch.IsolationAsync(context.Config, ground, context.Layout, analysis, cancellationToken).ConfigureAwait(false);

        if (isolated.Refusal("the analyst") is { } unisolated)
        {
            return Wordless(unisolated, HarnessExit.ToolMissing);
        }

        // What the analyst spends is counted as every agent run of the engine's is, from its turns as they come - over
        // every attempt of its answer, for the analysis is one paid run however many times it is asked again.
        var spend = new AgentRunSpend(providers, resolution.Serving.Model);
        var prompt = PromptOf(call);

        for (var asked = 0; ; asked++)
        {
            var request = new AgentStepRequest(
                call.Run,
                WorkflowTemplates.AnalystAgent,
                call.Stamp,
                Attempt: 1,
                WorkflowTemplates.AnalystAgent,
                Provider: null,
                resolution.Serving.Client,
                Tier: "reporting",
                resolution.Serving.Model,
                Effort: null,
                Fallback: null,
                definition.Prompt,
                prompt,
                ResumeTemplate: null,
                workspace,
                new AgentOutput("analysis", schemaFile, MaxRepairs: Repairs),
                Conversation: null,
                new AgentLimits(definition.MaxTurns, definition.MaxBudgetUsd, definition.MaxMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : null, definition.StallSeconds, place.Definition.HandoffSeconds),
                mask)
            {
                Ground = ground,
                Isolation = isolated,
                Credential = order.FirstOrDefault(entry => entry.Usable)?.Credential,
                Withheld = [.. providers.Appointed().Select(appointed => appointed.Name)],
                Order = order,
                RecordDirectory = analysis,
                SessionsDirectory = Path.Combine(analysis, OrchestratorLayout.SessionsDirectoryName),
                Program = program.Program,
                ProgramVersion = program.Version,
            };

            var result = await _runner.RunAsync(request, spend, cancellationToken).ConfigureAwait(false);

            if (result.Problem() is { } wrong)
            {
                return Wordless($"the analyst answered a result that does not hold together: {wrong}", HarnessExit.CommandFailed, result, spend);
            }

            if (result.Status != AgentRunStatus.Completed)
            {
                return Wordless(result.Error is { } error ? mask?.Redact(error) ?? error : $"the analyst's run ended {result.Status}", result.ToOutcome().ExitCode, result, spend);
            }

            if (result.StructuredOutput is not { Length: > 0 } structured)
            {
                return Wordless("the analyst's run completed without an answer", HarnessExit.CommandFailed, result, spend);
            }

            // The answer is held twice: to the schema the tool ships, and to the report it read. A citation that names
            // a number the report does not hold is a problem like a schema's, and the analyst is asked again as a
            // repair asks - twice at most, then refused with its problems. The analysis cannot invent a measurement.
            var answer = JsonDepth.ParseNode(structured);
            IReadOnlyList<string> held = answer is null
                ? ["the answer reads as no JSON"]
                :
                [
                    .. JsonSchemaSubset.Problems(schema.Schema!, answer),
                    .. FlowAnalysisEvidence.Problems(answer, call.Report),
                ];

            if (held.Count == 0)
            {
                var given = answer.Deserialize<FlowAnalysis>(JsonStateFile.Options)!;

                return given.Problem() is { } shape
                    ? Wordless($"the analyst's answer does not hold together: {shape}", HarnessExit.CommandFailed, result, spend)
                    : SpentBy(new FlowAnalysed(given, Model: result.Model), spend);
            }

            if (asked >= Repairs)
            {
                return Wordless($"the analyst's answer does not read against the report it was given, and still did not after {Repairs.ToString(System.Globalization.CultureInfo.InvariantCulture)} repairs: {string.Join("; ", held.Select(problem => mask?.Redact(problem) ?? problem))}", HarnessExit.CommandFailed, result, spend);
            }

            // The repair is the gate's own message, so an answer asked again hears the same words whichever check
            // asked - and the whole prompt stands under it, for a run asked again begins its conversation anew.
            prompt = $"{prompt}\n\n{StructuredOutputGate.Repair(held)}";
        }
    }

    /// <summary>Why no model analysed the report, with the code it means and what the analyst's run cost where one ran.</summary>
    /// <param name="why">Why, said whole.</param>
    /// <param name="exitCode">The code it means.</param>
    /// <param name="result">The analyst's run, where one ran.</param>
    /// <param name="spend">What the analyst's run spent, where one ran.</param>
    private static FlowAnalysed Wordless(string why, int exitCode, AgentRunResult? result = null, AgentRunSpend? spend = null)
        => SpentBy(new(Analysis: null, WhyNot: why, ExitCode: exitCode, result?.Model), spend);

    /// <summary>
    /// <paramref name="analysed"/> with what the analyst's run spent, as its count says it: the dollars counted, why
    /// the rest is not known where a turn's cost could not be counted, and the tokens - nothing where no turn was
    /// reported.
    /// </summary>
    /// <param name="analysed">The answer.</param>
    /// <param name="spend">The run's count; null where no run was made.</param>
    private static FlowAnalysed SpentBy(FlowAnalysed analysed, AgentRunSpend? spend)
        => spend?.Spent is { } spent
            ? analysed with { Usd = spent.Dollars.Counted, Tokens = spent.Tokens, Uncounted = spent.Dollars.Uncounted }
            : analysed;

    /// <summary>
    /// The analyst's prompt, filled with what it reads: the report whole, as JSON, and where the run's records are -
    /// the only two names its prompt reads.
    /// </summary>
    /// <param name="call">The analysis asked.</param>
    private static string PromptOf(FlowAnalystCall call)
        => FlowTemplate.Fill(
            WorkflowTemplates.TextOf($"prompts/{WorkflowTemplates.AnalystPrompt}.md")!,
            path => path.Root switch
            {
                "analysis" => new(FlowValue.Of(new JsonObject
                {
                    ["report"] = call.Report.ToJsonString(ReportJson.Options),
                    ["records"] = call.Records,
                }), 0),
                _ => new(FlowValue.Missing, 0),
            },
            out var problem)
            ?? throw new HarnessException(HarnessExit.ConfigInvalid, problem!);
}
