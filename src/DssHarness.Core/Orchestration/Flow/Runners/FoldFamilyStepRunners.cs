using System.Text.Json.Nodes;
using DssHarness.Core.Application;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// One call of a fold family verb a step makes: the run, the agent it works on, the consents given by name, the paths a
/// refresh leaves out for their merge conflicts, whether it applies, and what its apply is held to.
/// </summary>
/// <param name="Run">The step's run, with what the engine read where it started.</param>
/// <param name="Place">The run's place.</param>
/// <param name="Agent">The agent the step works on: its <c>of</c>'s, or its target among <c>others</c>.</param>
/// <param name="Closed">Whether the agent is closed already, its deletion begun: a deletion then folds nothing and takes no allowance.</param>
/// <param name="Grants">Each consent given by name, still naming what the dry run names.</param>
/// <param name="AlsoExcept">The paths a refresh leaves as the agent changed them, beside its <c>except</c>: those whose merges conflict.</param>
/// <param name="Apply">Whether it applies.</param>
/// <param name="Expected">What its dry run named, which a fold's apply is held to; null for a dry run, a refresh and a rebase.</param>
public sealed record FoldCall(
    FlowStepRun Run,
    FlowRunPlace Place,
    string Agent,
    bool Closed,
    IReadOnlyList<FlowFoldGrant> Grants,
    IReadOnlyList<string> AlsoExcept,
    bool Apply,
    AgentPlanned? Expected = null)
{
    /// <summary>The paths, rows or cells the consents of <paramref name="consent"/> were given for, each once, in the order given.</summary>
    /// <param name="consent">The consent, by the question kind it is asked as.</param>
    public IReadOnlyList<string> Granted(ConsentKind consent)
        => [.. Grants.Where(grant => grant.Kind == FlowQuestions.KindOf(consent)).SelectMany(grant => grant.Named).Distinct(StringComparer.Ordinal)];
}

/// <summary>
/// The paths a fold would write that a step's <c>within</c> leaves out: each path its plan writes or removes in the main
/// tree, those it would merge were it asked among them (<see cref="AgentPlanned.WouldWrite"/>), matched against the
/// patterns <c>within</c> names - never the registry files its rows change, nor a path handed to the agent.
/// </summary>
public static class FoldWithin
{
    /// <summary>The paths of <paramref name="paths"/> none of <paramref name="within"/> matches; none where the step names no <c>within</c>.</summary>
    /// <param name="paths">The paths it would write.</param>
    /// <param name="within">The patterns the step's <c>within</c> names, already checked; null where it names none.</param>
    /// <param name="comparison">How this machine compares paths.</param>
    public static IReadOnlyList<string> Outside(IReadOnlyList<string> paths, IReadOnlyList<string>? within, StringComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (within is null)
        {
            return [];
        }

        var patterns = within.Select(GlobPattern.Parse).ToList();

        return [.. paths.Where(path => !patterns.Any(pattern => pattern.Matches(path, comparison)))];
    }
}

/// <summary>What a fold family step does with its dry run, as <see cref="FoldStandings.Read"/> reads it.</summary>
public enum FoldStanding
{
    /// <summary>The step ends with the dry run's own code: it could not be weighed, or a run holds what the apply takes, or only a copy.</summary>
    Ends,

    /// <summary>The step asks: the question its dry run names, or - refused for anything but conflicts - the coordinator fails it 13.</summary>
    Asks,

    /// <summary>The step applies, with exactly what the dry run named.</summary>
    Applies,
}

/// <summary>
/// How a fold family step reads its dry run - its standing, typed, never its exit code but where the request is sound and
/// the one rule of a dry run (<see cref="DryRuns.Answer"/>) says what stands in the apply's way: in order, a dry run that
/// could not be weighed ends the step with its code; a refused one asks, or fails 13; paths its <c>within</c> leaves out
/// ask; a consent it lacks asks; a sound one a run holds, or holds a copy of, ends with the code that one rule gives it -
/// 13 with the cause of a held lock where that run is the one thing in the way, which the retry table waits out, and 21
/// where it holds only a copy a deletion removes - and a sound one nothing stands in the way of applies.
/// </summary>
public static class FoldStandings
{
    /// <summary>What the step does with <paramref name="dry"/>.</summary>
    /// <param name="dry">The dry run's answer, its facts weighed.</param>
    /// <param name="outside">The paths it would write that the step's <c>within</c> leaves out.</param>
    public static FoldStanding Read(UseCaseResult<AgentVerbFacts> dry, IReadOnlyList<string> outside)
    {
        ArgumentNullException.ThrowIfNull(dry);
        ArgumentNullException.ThrowIfNull(outside);

        return dry.Facts?.Standing switch
        {
            null => FoldStanding.Ends,
            RequestStanding.Refused or RequestStanding.NeedsConsent => FoldStanding.Asks,
            _ when outside.Count > 0 => FoldStanding.Asks,
            _ when dry.Outcome.ExitCode != HarnessExit.Success => FoldStanding.Ends,
            _ => FoldStanding.Applies,
        };
    }
}

/// <summary>
/// A step of the fold family - <c>fold</c>, <c>delete-agent</c>, <c>refresh</c>, <c>rebase</c> - over its verb's typed
/// facts: the dry run made first and read by its standing (<see cref="FoldStandings"/>); a consent it lacks, paths its
/// <c>within</c> leaves out or conflicting merges alone asked as the question they name, with what the dry run showed kept
/// in the step's directory (<c>fold.json</c>); answered, the dry run made again, and the consent given by name only where
/// that names exactly what the question showed - a consent that no longer does is never passed; then the apply, held to
/// what the dry run named, a fold's written ahead to the run's ledger of its folds and settled after. An apply its trees
/// moved under is weighed again, at most <see cref="MostPasses"/> times.
/// </summary>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="platform">This machine.</param>
/// <param name="store">Reads the agent's record, and keeps the step's.</param>
/// <param name="ledger">The run's ledger of its folds; null for a step that folds nothing into the main tree.</param>
public abstract class FoldFamilyStepRunner(IHarnessContextLoader contextLoader, IHostPlatform platform, OrchestrationStore store, FoldLedger? ledger) : IFlowStepTypeRunner
{
    /// <summary>The most times one attempt weighs its dry run and applies, its trees moving under it each time, before it fails.</summary>
    public const int MostPasses = 3;

    /// <inheritdoc/>
    public abstract string Type { get; }

    /// <summary>The verb's words for its keys' spelling, where one is wrong; null where the keys, with the consents given, spell a request.</summary>
    /// <param name="call">The call.</param>
    protected abstract string? ProblemOf(FoldCall call);

    /// <summary>Calls the verb, as <paramref name="call"/> says.</summary>
    /// <param name="call">The call.</param>
    /// <param name="cancellationToken">Cancels what has not begun writing.</param>
    protected abstract Task<UseCaseResult<AgentVerbFacts>> CallAsync(FoldCall call, CancellationToken cancellationToken);

    /// <summary>Whether <paramref name="call"/> folds into the main tree, through the run's ledger of its folds, and holds its paths to the step's <c>within</c>.</summary>
    /// <param name="call">The call.</param>
    protected virtual bool Folds(FoldCall call) => false;

    /// <summary>
    /// Whether a dry run refused for conflicting merges alone hands every other path first - a refresh's - and asks of those
    /// it left after; otherwise the refusal asks at once.
    /// </summary>
    protected virtual bool HandsAroundConflicts => false;

    /// <summary>
    /// Whether the step, read where it starts, weighs nothing at all - a refresh whose <c>paths</c> expression yielded none -
    /// and is done with no call made.
    /// </summary>
    /// <param name="run">The step's run.</param>
    protected virtual bool WeighsNothing(FlowStepRun run) => false;

    /// <summary>The step's output, from what its apply answered; null for a step that writes none.</summary>
    /// <param name="applied">The apply's facts.</param>
    protected virtual JsonObject? OutputOf(AgentVerbFacts applied) => null;

    /// <inheritdoc/>
    public async Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var (place, context) = await FlowStepRunners.PlaceOf(run, contextLoader, platform, cancellationToken).ConfigureAwait(false);

        if ((run.Target ?? run.WorksIn) is not { } agent)
        {
            return FlowStepResult.Ended(passed: false, HarnessExit.InternalError, $"'{run.Instance}' is a {Type} step, and the engine named no agent for it to work on");
        }

        if (WeighsNothing(run))
        {
            return FlowStepResult.Ended(passed: true, HarnessExit.Success);
        }

        var layout = OrchestratorLayout.Of(context.Layout, place.Run);
        var directory = run.Target is { } target
            ? OrchestratorLayout.TargetDirectory(layout.InstanceDirectory(run.Instance, null), target)
            : layout.InstanceDirectory(run.Instance, run.WorksIn);
        var closed = store.ReadAgent(layout, agent) is { State: AgentStates.Closed };
        var record = store.ReadFoldRecord(directory) ?? new FlowFoldRecord(run.Instance, run.Attempt, agent, Type, [], []);
        var call = new FoldCall(run, place, agent, closed, [], [], Apply: false);
        var folds = Folds(call) && ledger is not null;

        if (WithinOf(call, folds) is { Problem: { } typo })
        {
            return FlowStepResult.Ended(passed: false, HarnessExit.ConfigInvalid, $"'{run.Instance}' takes within: {typo}");
        }

        if (ledger is not null)
        {
            // An intent a crash left - the verb called, nothing recorded after - is settled from what the tree holds,
            // before this attempt does anything: a deletion whose crash came after its closing folds nothing now, and
            // what its fold wrote is the ledger's all the same.
            _ = await ledger.SettleInterruptedAsync(layout, place.Repository, run.Instance).ConfigureAwait(false);
        }

        // The answer the question this step asked was given, where it came back with it: it stands where the answers stood
        // when it asked. A retry made a new attempt that carries none, and the question is gone either way.
        var asked = record.Asked;
        var answer = asked is not null && run.Answers.Count > asked.AnswerAt ? run.Answers[asked.AnswerAt] : null;
        var grants = record.Granted.ToList();
        record = record with { Attempt = run.Attempt, Agent = agent, Asked = null };

        for (var pass = 1; ; pass++)
        {
            var bare = call with { Grants = [], AlsoExcept = [], Apply = false };

            if (ProblemOf(bare) is { } spelt)
            {
                return FlowStepResult.Ended(passed: false, HarnessExit.ConfigInvalid, $"'{run.Instance}' takes {spelt}");
            }

            // The dry run on the keys alone, which every consent is held to: what it names now is what the trees hold now.
            var first = await CallAsync(bare, cancellationToken).ConfigureAwait(false);

            if (first.Facts?.Standing is null)
            {
                // Not weighed - nothing it named can be held to the question - so an answer that came back waits on, where
                // it stands, for the attempt that can weigh it.
                store.WriteFoldRecord(directory, record with { Asked = asked, Shown = ShownOf(first, []), Applied = null });
                return Ended(first.Outcome);
            }

            var firstOutside = OutsideOf(first.Facts, call, folds);

            if (asked is not null && answer is not null)
            {
                if (answer.Answer is "allow" or "merge")
                {
                    grants.Add(new FlowFoldGrant(asked.Kind, asked.Named, answer.Question, answer.Answer));
                }

                answer = null;
            }

            // A consent is given by name only where the dry run names exactly what it was given for, the one just answered
            // among them: where it names anything else the trees moved since, the consent is never passed, and the
            // question is asked again with what the dry run names now.
            grants = [.. grants.Where(grant => Same(NamesFor(grant.Kind, first.Facts, firstOutside), grant.Named))];

            // A refresh refused for conflicting merges alone hands what does not conflict, and asks of the rest after.
            IReadOnlyList<string> leftConflicting = HandsAroundConflicts
                && first.Facts is { Standing: RequestStanding.Refused, Refusals: { Count: > 0 } refusals, Planned: { } firstPlan }
                && refusals.All(reason => reason == RefusalKind.Conflict)
                    ? firstPlan.Conflicting
                    : [];

            var given = call with { Grants = grants, AlsoExcept = leftConflicting };

            if (ProblemOf(given) is { } givenSpelt)
            {
                return FlowStepResult.Ended(passed: false, HarnessExit.ConfigInvalid, $"'{run.Instance}' takes {givenSpelt}");
            }

            var dry = grants.Count == 0 && leftConflicting.Count == 0 ? first : await CallAsync(given, cancellationToken).ConfigureAwait(false);
            var outside = OutsideOf(dry.Facts, call, folds);

            if (grants.Any(grant => grant.Kind == FlowQuestions.FoldOutside && Same(outside, grant.Named)))
            {
                outside = [];
            }

            record = record with { Granted = grants, LeftConflicting = leftConflicting, Shown = ShownOf(dry, outside), Applied = null };

            switch (FoldStandings.Read(dry, outside))
            {
                case FoldStanding.Ends:
                    store.WriteFoldRecord(directory, record);
                    return Ended(dry.Outcome);

                case FoldStanding.Asks:
                {
                    var dryRun = FlowDryRun.Of(dry.Facts!, outside, agent, run.Target);

                    if (FlowQuestions.FoldQuestionOf(dryRun) is { } question)
                    {
                        record = record with { Asked = new FlowFoldAsked(question.Kind, question.Named, run.Answers.Count) };
                    }

                    store.WriteFoldRecord(directory, record);
                    return FlowStepResult.Standing(dryRun);
                }
            }

            var expected = folds ? dry.Facts!.Planned : null;
            var applying = given with { Apply = true, Expected = expected };
            var intent = folds && expected is { } planned && Writes(planned)
                ? await ledger!.IntendAsync(layout, place.Repository, run.Instance, run.Attempt, Type, agent, planned, cancellationToken).ConfigureAwait(false)
                : null;
            UseCaseResult<AgentVerbFacts> applied;

            try
            {
                applied = await CallAsync(applying, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (intent is not null)
                {
                    // What it wrote before it stopped is read from the tree: nothing reaches the main tree unrecorded.
                    _ = await ledger!.SettleInterruptedAsync(layout, place.Repository, run.Instance).ConfigureAwait(false);
                }

                throw;
            }

            if (intent is not null)
            {
                await ledger!.SettleAsync(layout, place.Repository, intent, applied.Outcome.ExitCode).ConfigureAwait(false);
            }

            record = record with { Applied = AppliedOf(applied, intent) };
            store.WriteFoldRecord(directory, record);

            if (applied.Outcome.Succeeded)
            {
                // A refresh that handed what did not conflict asks of what it left: a person reconciles each.
                return leftConflicting.Count > 0
                    ? FlowStepResult.Standing(new FlowDryRun(RequestStanding.Refused, [], [], [RefusalKind.Conflict], applied.Outcome.Message)
                    {
                        Conflicting = leftConflicting,
                        Lines = applied.Facts?.Lines ?? [],
                        Agent = agent,
                        Target = run.Target,
                    })
                    : FlowStepResult.Ended(passed: true, HarnessExit.Success, output: applied.Facts is { } facts ? OutputOf(facts) : null);
            }

            // Refused under its hold for what it was not refused for a moment before - its trees moved between the two -
            // it is weighed again; a run holding its trees is waited out by the retry table, as a held lock is.
            if (applied.Outcome.ExitCode == HarnessExit.Refused && applied.Outcome.Cause != OutcomeCause.LockHeld && pass < MostPasses)
            {
                continue;
            }

            return Ended(applied.Outcome);
        }
    }

    /// <summary>The step's ending with <paramref name="outcome"/>'s code and words, and the cause of a held lock where it carries one.</summary>
    private static FlowStepResult Ended(CommandOutcome outcome)
        => FlowStepResult.Ended(passed: false, outcome.ExitCode, outcome.Message) with { Cause = outcome.Cause };

    /// <summary>Whether <paramref name="planned"/> writes anything into the main tree, or applies a row.</summary>
    private static bool Writes(AgentPlanned planned)
        => planned.Written.Count + planned.Deleted.Count + planned.Merged.Count + planned.Registries.Count + planned.Rows.Count > 0 || planned.ArmRegistry is not null;

    /// <summary>The patterns of the step's <c>within</c> where it folds - none where it does not, or names none - or why one is no pattern.</summary>
    private static (IReadOnlyList<string>? Patterns, string? Problem) WithinOf(FoldCall call, bool folds)
    {
        if (!folds || FlowStepKeys.NamesOf(call.Run.Step.Within, call.Run, "within") is not { } within)
        {
            return (null, null);
        }

        return within.Select(pattern => (pattern, GlobPattern.Problem(pattern))).FirstOrDefault(pair => pair.Item2 is not null) is { Item2: { } typo } wrong
            ? (null, $"the pattern '{wrong.pattern}' {typo}")
            : (within, null);
    }

    /// <summary>The paths a dry run would write that the step's <c>within</c> leaves out; none for a step that does not fold, and where nothing was planned.</summary>
    private IReadOnlyList<string> OutsideOf(AgentVerbFacts? facts, FoldCall call, bool folds)
        => facts?.Planned is { } planned ? FoldWithin.Outside(planned.WouldWrite, WithinOf(call, folds).Patterns, platform.PathComparison) : [];

    /// <summary>What a question of <paramref name="kind"/> is about in a dry run: its paths outside <c>within</c>, or what its plan names for the consent.</summary>
    private static IReadOnlyList<string> NamesFor(string kind, AgentVerbFacts? facts, IReadOnlyList<string> outside)
        => kind == FlowQuestions.FoldOutside
            ? outside
            : Enum.GetValues<ConsentKind>().Where(consent => FlowQuestions.KindOf(consent) == kind).Select(consent => facts?.Planned?.Named(consent) ?? []).FirstOrDefault() ?? [];

    /// <summary>Whether two lists name the same things, as sets.</summary>
    private static bool Same(IReadOnlyList<string> one, IReadOnlyList<string> other)
        => one.Count > 0 && one.ToHashSet(StringComparer.Ordinal).SetEquals(other);

    /// <summary>What a dry run named and stood, as the step's record keeps it.</summary>
    private static FlowFoldShown ShownOf(UseCaseResult<AgentVerbFacts> dry, IReadOnlyList<string> outside)
        => new(
            dry.Outcome.ExitCode,
            dry.Outcome.Message,
            [.. (dry.Facts?.Needs ?? []).Select(need => need.ToString())],
            [.. (dry.Facts?.Refusals ?? []).Select(reason => reason.ToString())],
            outside,
            dry.Facts?.Standing?.ToString(),
            dry.Facts?.Planned);

    /// <summary>How an apply ended, as the step's record keeps it.</summary>
    private static FlowFoldApplied AppliedOf(UseCaseResult<AgentVerbFacts> applied, string? intent)
        => new(applied.Outcome.ExitCode, applied.Outcome.Message, applied.Facts?.Standing?.ToString(), applied.Facts?.Folded, applied.Facts?.Planned, intent);

    /// <summary>The allowances a fold or a deletion's fold is given: its <c>allow</c>, each list as written or read where it started, and each consent given by name.</summary>
    /// <param name="call">The call.</param>
    protected static FoldAllowances AllowancesOf(FoldCall call)
    {
        ArgumentNullException.ThrowIfNull(call);

        var allow = call.Run.Step.Allow;

        return new FoldAllowances
        {
            Settled = allow?.Settled ?? [],
            New = Union(FlowStepKeys.NamesOf(allow?.New, call.Run, "allow.new"), call.Granted(ConsentKind.FoldNewRows)),
            AcceptLost = Union(allow?.AcceptLost, call.Granted(ConsentKind.FoldLostRows)),
            Merge = Union(FlowStepKeys.NamesOf(allow?.Merge, call.Run, "allow.merge"), call.Granted(ConsentKind.FoldTwoChanges)),
            AcceptOver = Union(allow?.AcceptOver, call.Granted(ConsentKind.FoldOverCells)),
        };
    }

    /// <summary><paramref name="first"/>'s names, then each of <paramref name="second"/>'s not among them.</summary>
    /// <param name="first">The names a key gives; null where it gives none.</param>
    /// <param name="second">The names given by consent.</param>
    protected static IReadOnlyList<string> Union(IReadOnlyList<string>? first, IReadOnlyList<string> second)
        => [.. (first ?? []).Concat(second).Distinct(StringComparer.Ordinal)];

    /// <summary>What a fold or a deletion's fold did, as the step's output: the paths written - those merged among them - removed, the registry files its rows changed and the rows it applied.</summary>
    /// <param name="folded">What it did; null where it did nothing.</param>
    protected static JsonObject FoldOutputOf(AgentFolded? folded)
        => new()
        {
            ["written"] = Texts([.. (folded?.Written ?? []).Concat(folded?.Merged ?? []).Concat(folded?.ArmRegistry is { } arms ? [arms] : []).Distinct(StringComparer.Ordinal)]),
            ["deleted"] = Texts(folded?.Deleted ?? []),
            ["registries"] = Texts(folded?.Registries ?? []),
            ["rows"] = Texts(folded?.Rows ?? []),
        };

    private static JsonArray Texts(IEnumerable<string> texts) => [.. texts.Select(text => (JsonNode?)text)];
}

/// <summary>
/// The <c>fold</c> step: <see cref="IAgentService.FoldAsync(FoldAgentRequest, CancellationToken)"/> held to its dry run,
/// through the run's ledger of its folds, its paths held to its <c>within</c>.
/// </summary>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="platform">This machine.</param>
/// <param name="store">Reads the agent's record, and keeps the step's.</param>
/// <param name="agents">Folds the agent.</param>
/// <param name="ledger">The run's ledger of its folds.</param>
public sealed class FoldStepRunner(IHarnessContextLoader contextLoader, IHostPlatform platform, OrchestrationStore store, IAgentService agents, FoldLedger ledger)
    : FoldFamilyStepRunner(contextLoader, platform, store, ledger)
{
    /// <inheritdoc/>
    public override string Type => "fold";

    /// <inheritdoc/>
    protected override bool Folds(FoldCall call) => true;

    /// <inheritdoc/>
    protected override string? ProblemOf(FoldCall call) => AllowancesOf(call).Problem(OptionSpelling.Key) is { } problem ? $"allow: {problem}" : null;

    /// <inheritdoc/>
    protected override Task<UseCaseResult<AgentVerbFacts>> CallAsync(FoldCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);

        return agents.FoldAsync(
            new FoldAgentRequest(call.Place.Repository, call.Place.Run, call.Agent, AllowancesOf(call), call.Apply) { Expected = call.Expected },
            cancellationToken);
    }

    /// <inheritdoc/>
    protected override JsonObject? OutputOf(AgentVerbFacts applied) => FoldOutputOf(applied?.Folded);
}

/// <summary>
/// The <c>delete-agent</c> step: <see cref="IAgentService.DeleteAsync(DeleteAgentRequest, CancellationToken)"/>, the fold
/// it makes held to its dry run through the run's ledger of its folds and to its <c>within</c> - or, discarding, or of an
/// agent whose deletion began, folding nothing and taking no allowance.
/// </summary>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="platform">This machine.</param>
/// <param name="store">Reads the agent's record, and keeps the step's.</param>
/// <param name="agents">Deletes the agent.</param>
/// <param name="ledger">The run's ledger of its folds.</param>
public sealed class DeleteAgentStepRunner(IHarnessContextLoader contextLoader, IHostPlatform platform, OrchestrationStore store, IAgentService agents, FoldLedger ledger)
    : FoldFamilyStepRunner(contextLoader, platform, store, ledger)
{
    /// <inheritdoc/>
    public override string Type => "delete-agent";

    /// <inheritdoc/>
    protected override bool Folds(FoldCall call) => !Discards(call) && !call.Closed;

    /// <inheritdoc/>
    protected override string? ProblemOf(FoldCall call)
        => Folds(call) && AllowancesOf(call).Problem(OptionSpelling.Key) is { } problem ? $"allow: {problem}" : null;

    /// <inheritdoc/>
    protected override Task<UseCaseResult<AgentVerbFacts>> CallAsync(FoldCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);

        return agents.DeleteAsync(
            new DeleteAgentRequest(call.Place.Repository, call.Place.Run, call.Agent, Folds(call) ? AllowancesOf(call) : new FoldAllowances(), call.Apply, Discards(call))
            {
                Expected = call.Expected,
            },
            cancellationToken);
    }

    /// <inheritdoc/>
    protected override JsonObject? OutputOf(AgentVerbFacts applied) => FoldOutputOf(applied?.Folded);

    private static bool Discards(FoldCall call) => call.Run.Step.DiscardUncommitted is true;
}

/// <summary>
/// The <c>refresh</c> step: <see cref="IAgentService.RefreshAsync(RefreshAgentRequest, CancellationToken)"/> - of its
/// <c>of</c>, or of each target of its <c>others</c> in turn - handing what does not conflict where only conflicting
/// merges refuse it, and asking of those after.
/// </summary>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="platform">This machine.</param>
/// <param name="store">Reads the agent's record, and keeps the step's.</param>
/// <param name="agents">Refreshes the agent.</param>
public sealed class RefreshStepRunner(IHarnessContextLoader contextLoader, IHostPlatform platform, OrchestrationStore store, IAgentService agents)
    : FoldFamilyStepRunner(contextLoader, platform, store, ledger: null)
{
    /// <inheritdoc/>
    public override string Type => "refresh";

    /// <inheritdoc/>
    protected override bool HandsAroundConflicts => true;

    /// <inheritdoc/>
    protected override bool WeighsNothing(FlowStepRun run)
        => run?.Step.Paths?.Expression is not null && FlowStepKeys.NamesOf(run.Step.Paths, run, "paths") is { Count: 0 };

    /// <inheritdoc/>
    protected override string? ProblemOf(FoldCall call) => RequestOf(call).Problem(OptionSpelling.Key) is { } problem ? $"refresh: {problem}" : null;

    /// <inheritdoc/>
    protected override Task<UseCaseResult<AgentVerbFacts>> CallAsync(FoldCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        return agents.RefreshAsync(new RefreshAgentRequest(call.Place.Repository, call.Place.Run, call.Agent, RequestOf(call), call.Apply), cancellationToken);
    }

    /// <summary>
    /// The refresh the step's keys ask for, with each consent given by name and the paths left for their merge conflicts -
    /// left, never merged, whatever its merge names: a merge that conflicts is no merge to take.
    /// </summary>
    private static RefreshRequest RequestOf(FoldCall call)
    {
        var (step, run) = (call.Run.Step, call.Run);
        var left = call.AlsoExcept.ToHashSet(StringComparer.Ordinal);

        return new RefreshRequest
        {
            Paths = FlowStepKeys.NamesOf(step.Paths, run, "paths") ?? [],
            All = step.All is true,
            Except = Union(FlowStepKeys.NamesOf(step.Except, run, "except"), call.AlsoExcept),
            Stale = FlowStepKeys.NamesOf(step.Stale, run, "stale") ?? [],
            Merge = [.. Union(FlowStepKeys.NamesOf(step.Merge, run, "merge"), call.Granted(ConsentKind.FoldTwoChanges)).Where(path => !left.Contains(path))],
        };
    }
}

/// <summary>
/// The <c>rebase</c> step: <see cref="IAgentService.RebaseAsync(RebaseAgentRequest, CancellationToken)"/> - of its
/// <c>of</c>, or of each target of its <c>others</c> in turn - which moves the base whole or not at all.
/// </summary>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="platform">This machine.</param>
/// <param name="store">Reads the agent's record, and keeps the step's.</param>
/// <param name="agents">Moves the agent's base.</param>
public sealed class RebaseStepRunner(IHarnessContextLoader contextLoader, IHostPlatform platform, OrchestrationStore store, IAgentService agents)
    : FoldFamilyStepRunner(contextLoader, platform, store, ledger: null)
{
    /// <inheritdoc/>
    public override string Type => "rebase";

    /// <inheritdoc/>
    protected override string? ProblemOf(FoldCall call) => RequestOf(call).Problem(OptionSpelling.Key) is { } problem ? $"a rebase: {problem}" : null;

    /// <inheritdoc/>
    protected override Task<UseCaseResult<AgentVerbFacts>> CallAsync(FoldCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        return agents.RebaseAsync(new RebaseAgentRequest(call.Place.Repository, call.Place.Run, call.Agent, RequestOf(call), call.Apply), cancellationToken);
    }

    /// <summary>The move the step's keys ask for, with each consent given by name.</summary>
    private static RebaseRequest RequestOf(FoldCall call)
        => new()
        {
            Merge = Union(FlowStepKeys.NamesOf(call.Run.Step.Merge, call.Run, "merge"), call.Granted(ConsentKind.FoldTwoChanges)),
            Settled = call.Run.Step.Settled,
        };
}
