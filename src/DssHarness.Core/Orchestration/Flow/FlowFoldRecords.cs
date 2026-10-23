using System.Globalization;
using System.Text.Json.Serialization;
using DssHarness.Core.Execution;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The run's write-ahead ledger of its folds, <c>folds.json</c>: each fold a <c>fold</c> or a <c>delete-agent</c> step
/// made, every path it meant to write with the digest the main tree held there before, written before the verb is called,
/// and the digest it left there after - so nothing reaches the main tree unrecorded, and a crash between the two leaves an
/// intent the next attempt settles. Written whole each time, inside the run's own section.
/// </summary>
/// <param name="Schema">The file's schema, 1.</param>
/// <param name="Run">The run's name.</param>
/// <param name="Folds">Every fold the run made or meant to make, in the order intended.</param>
public sealed record FlowFoldLedgerFile(int Schema, string Run, IReadOnlyList<FlowFoldEntry> Folds)
{
    /// <summary>What is wrong with this file; null when nothing is.</summary>
    public string? Problem()
        => Schema != 1 ? $"its schema is {Schema.ToString(CultureInfo.InvariantCulture)}, and this build reads 1"
            : string.IsNullOrWhiteSpace(Run) ? "it names no run"
            : Folds.FirstOrDefault(fold => fold.Problem() is not null) is { } wrong ? $"its fold '{wrong.Id}': {wrong.Problem()}"
            : Folds.Select((fold, at) => (fold, at)).FirstOrDefault(pair => !string.Equals(pair.fold.Id, FlowFoldEntry.IdOf(pair.at + 1), StringComparison.Ordinal)) is { fold: { } misnumbered }
                ? $"its fold '{misnumbered.Id}' is out of order: ids are f<n>, in the order intended"
                : null;

    /// <summary>The entry named, or null where the ledger holds none.</summary>
    /// <param name="id">The entry's id.</param>
    public FlowFoldEntry? Of(string id) => Folds.FirstOrDefault(fold => string.Equals(fold.Id, id, StringComparison.Ordinal));
}

/// <summary>One fold of the run's ledger: who made it, what it meant to write, and what became of it.</summary>
/// <param name="Id">The entry's id: <c>f&lt;n&gt;</c>, in the order intended.</param>
/// <param name="Instance">The step instance that made it.</param>
/// <param name="Attempt">The attempt of that instance that made it.</param>
/// <param name="Type">The step's type: <c>fold</c> or <c>delete-agent</c>.</param>
/// <param name="Agent">The agent whose work it folds.</param>
/// <param name="State">
/// What became of it: <c>intended</c> (written before the verb is called, and nothing after yet), <c>applied</c> (the verb
/// went through), <c>stopped</c> (it wrote part of it and stopped, 21), <c>refused</c> (it wrote nothing: its trees moved,
/// or a holder stood in the way) or <c>interrupted</c> (a crash cut it off after its intent, and the next attempt read what
/// it left).
/// </param>
/// <param name="IntendedAt">When its intent was written.</param>
/// <param name="Paths">Every path it meant to write in the main tree, the registry files its rows change among them, in order.</param>
/// <param name="Rows">The ids of the rows it meant to apply, in order.</param>
/// <param name="SettledAt">When what became of it was written; null while it is intended.</param>
/// <param name="Graphed">
/// Whether the run's codebase graph took the paths it changed: true once the graph's base was synced by them; null until
/// then - a sync of them that failed, was stopped or was cut off owes them still - and in a run with no graph.
/// </param>
public sealed record FlowFoldEntry(
    string Id,
    string Instance,
    int Attempt,
    string Type,
    string Agent,
    string State,
    [property: JsonConverter(typeof(FlowTimeConverter))] DateTimeOffset IntendedAt,
    IReadOnlyList<FlowFoldPath> Paths,
    IReadOnlyList<string> Rows,
    [property: JsonConverter(typeof(FlowTimeConverter)), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? SettledAt = null,
    bool? Graphed = null)
{
    /// <summary>The state an entry is written in before its verb is called.</summary>
    public const string Intended = "intended";

    /// <summary>The state of an entry whose verb wrote nothing.</summary>
    public const string Refused = "refused";

    /// <summary>The state of an entry whose intent a crash left, settled by the next attempt.</summary>
    public const string Interrupted = "interrupted";

    /// <summary>The states an entry stands in.</summary>
    public static readonly IReadOnlyList<string> States = [Intended, "applied", "stopped", Refused, Interrupted];

    /// <summary>The step types that fold through the ledger.</summary>
    public static readonly IReadOnlyList<string> Types = ["fold", "delete-agent"];

    /// <summary>The id the <paramref name="n"/>-th fold of a run takes.</summary>
    /// <param name="n">Its place, from 1.</param>
    public static string IdOf(int n) => $"f{n.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Whether the entry holds what no commit has taken yet: an intent nobody settled - what it wrote cannot be told - or
    /// a path it changed that no commit names.
    /// </summary>
    [JsonIgnore]
    public bool Uncommitted => State == Intended || Paths.Any(path => path.Changed == true && path.Committed is null);

    /// <summary>What is wrong with this entry; null when nothing is.</summary>
    public string? Problem()
        => string.IsNullOrWhiteSpace(Id) || Id[0] != 'f' || !int.TryParse(Id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1 ? $"'{Id}' is not a fold's id"
            : !FlowInstanceId.IsWellFormed(Instance) ? $"'{Instance}' is not an instance id"
            : Attempt < 1 ? $"its attempt is {Attempt.ToString(CultureInfo.InvariantCulture)}, and the first is 1"
            : !Types.Contains(Type, StringComparer.Ordinal) ? $"'{Type}' is not a step that folds"
            : string.IsNullOrWhiteSpace(Agent) ? "it names no agent"
            : !States.Contains(State, StringComparer.Ordinal) ? $"'{State}' is not a state a fold stands in"
            : (State == Intended) != (SettledAt is null) ? "it is settled exactly when it says what became of it"
            : Paths.FirstOrDefault(path => path.Problem() is not null) is { } wrong ? wrong.Problem()
            : Paths.GroupBy(path => path.Path, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1) is { } twice ? $"'{twice.Key}' is named twice"
            : Paths.FirstOrDefault(path => (State is Intended or Refused) != (path.Changed is null)) is { } unread
                ? $"'{unread.Path}' says what the fold left exactly where it {(State is Intended or Refused ? "wrote nothing yet" : "was settled")}"
            : Rows.Any(string.IsNullOrWhiteSpace) ? "a row it names is empty"
            : Graphed is { } graphed && (!graphed || State is Intended or Refused) ? "it says the graph took what it changed where it changed nothing yet, or says it did not"
            : null;
}

/// <summary>One path a fold meant to write, with the digest the main tree held there before and the one it left.</summary>
/// <param name="Path">The path, relative to the tree and spelt with forward slashes.</param>
/// <param name="What">What the fold does to it: <c>written</c>, <c>deleted</c>, <c>merged</c>, or <c>registry</c> for a registry file its rows change.</param>
/// <param name="Before">The SHA-256 the main tree held there when the intent was written; null where it held no file.</param>
/// <param name="Left">The SHA-256 it held once the fold was settled; null where it holds no file, and while nothing was read.</param>
/// <param name="Changed">Whether what it left is not what it held before; null while nothing was read.</param>
/// <param name="Committed">The commit that took what it left; null until one did, and on a path it did not change.</param>
public sealed record FlowFoldPath(
    string Path,
    string What,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Before,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Left = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool? Changed = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Committed = null)
{
    /// <summary>What a fold does to a path, as the ledger names it.</summary>
    public static readonly IReadOnlyList<string> Whats = ["written", "deleted", "merged", "registry"];

    /// <summary>What is wrong with this path; null when nothing is.</summary>
    public string? Problem()
        => OrchestrationRules.RelativePathProblem(Path, "a folded path") is { } path ? path
            : !Whats.Contains(What, StringComparer.Ordinal) ? $"'{What}' is not what a fold does to '{Path}'"
            : (OrchestrationRules.DigestProblem(Before, Path, allowNone: true) ?? OrchestrationRules.DigestProblem(Left, Path, allowNone: true)) is { } digest ? digest
            : Changed is { } changed && changed == string.Equals(Before, Left, StringComparison.Ordinal) ? $"'{Path}' says it changed where what it left is {(changed ? "what it held" : "not what it held")}"
            : Changed is null && Left is not null ? $"'{Path}' names what the fold left and not whether it changed"
            : Committed is not null && Changed != true ? $"'{Path}' is committed and was not changed"
            : OrchestrationRules.CommitProblem(Committed, "commit") is { } commit ? commit
            : null;
}

/// <summary>
/// What a fold family step keeps in its directory, <c>fold.json</c>: what its dry run named and its standing, the question
/// it asked with what that showed, each consent given by name, and how its apply ended - so a step answered after a pause
/// makes its dry run again and applies only where that names what the question showed.
/// </summary>
/// <param name="Instance">The step instance.</param>
/// <param name="Attempt">The attempt that last wrote it.</param>
/// <param name="Agent">The agent the step works on: its <c>of</c>'s, or the target of a step of <c>others</c>.</param>
/// <param name="Type">The step's type: <c>fold</c>, <c>delete-agent</c>, <c>refresh</c> or <c>rebase</c>.</param>
/// <param name="Granted">Each consent given by name, in the order given; kept while what it named is what the dry run names.</param>
/// <param name="LeftConflicting">The paths a refresh left out of what it handed for their merge conflicts, where it handed the rest.</param>
/// <param name="Shown">What its last dry run named and stood; null before one was made.</param>
/// <param name="Asked">The question its dry run raised that no answer has come for; null where none waits.</param>
/// <param name="Applied">How its apply ended; null before one was made.</param>
public sealed record FlowFoldRecord(
    string Instance,
    int Attempt,
    string Agent,
    string Type,
    IReadOnlyList<FlowFoldGrant> Granted,
    IReadOnlyList<string> LeftConflicting,
    FlowFoldShown? Shown = null,
    FlowFoldAsked? Asked = null,
    FlowFoldApplied? Applied = null)
{
    /// <summary>The step types whose record this is.</summary>
    public static readonly IReadOnlyList<string> Types = ["fold", "delete-agent", "refresh", "rebase"];

    /// <summary>What is wrong with this record; null when nothing is.</summary>
    public string? Problem()
        => !FlowInstanceId.IsWellFormed(Instance) ? $"'{Instance}' is not an instance id"
            : Attempt < 1 ? $"its attempt is {Attempt.ToString(CultureInfo.InvariantCulture)}, and the first is 1"
            : string.IsNullOrWhiteSpace(Agent) ? "it names no agent"
            : !Types.Contains(Type, StringComparer.Ordinal) ? $"'{Type}' is not a step of the fold family"
            : Granted.FirstOrDefault(grant => grant.Problem() is not null) is { } grant ? $"a consent it was given: {grant.Problem()}"
            : Asked?.Problem() is { } asked ? $"its question: {asked}"
            : Shown?.Problem() is { } shown ? $"its dry run: {shown}"
            : Applied?.Problem() is { } applied ? $"its apply: {applied}"
            : LeftConflicting.Any(string.IsNullOrWhiteSpace) ? "a path it left for its conflict is empty"
            : null;
}

/// <summary>What a fold family step's dry run named and stood, as its record keeps it.</summary>
/// <param name="ExitCode">The code the dry run answered with.</param>
/// <param name="Summary">The line it ended on.</param>
/// <param name="Needs">Each consent it lacks, by kind, as <see cref="ConsentKind"/> names them.</param>
/// <param name="Refusals">Each reason it stands refused for, by kind, as <see cref="RefusalKind"/> names them.</param>
/// <param name="Outside">The paths it would write that the step's <c>within</c> leaves out; empty where it names none.</param>
/// <param name="Standing">What it wants of its caller, as <see cref="RequestStanding"/> names it; null where it could not be weighed.</param>
/// <param name="Planned">What its plan names; null where none was measured.</param>
public sealed record FlowFoldShown(
    int ExitCode,
    string Summary,
    IReadOnlyList<string> Needs,
    IReadOnlyList<string> Refusals,
    IReadOnlyList<string> Outside,
    string? Standing = null,
    AgentPlanned? Planned = null)
{
    /// <summary>What is wrong with this entry; null when nothing is.</summary>
    public string? Problem()
        => Standing is not null && !FlowRecordNames.Is<RequestStanding>(Standing) ? $"'{Standing}' is not a standing"
            : Needs.FirstOrDefault(need => !FlowRecordNames.Is<ConsentKind>(need)) is { } need ? $"'{need}' is not a consent"
            : Refusals.FirstOrDefault(reason => !FlowRecordNames.Is<RefusalKind>(reason)) is { } reason ? $"'{reason}' is not a reason a request is refused for"
            : null;
}

/// <summary>The question a fold family step's dry run raised, with what it showed and where its answer will stand.</summary>
/// <param name="Kind">The question's kind.</param>
/// <param name="Named">What the question showed: the rows, the cells or the paths it is about.</param>
/// <param name="AnswerAt">Where its answer will stand among the answers the step's visit is given: how many it held when it asked.</param>
public sealed record FlowFoldAsked(string Kind, IReadOnlyList<string> Named, int AnswerAt)
{
    /// <summary>What is wrong with this entry; null when nothing is.</summary>
    public string? Problem()
        => string.IsNullOrWhiteSpace(Kind) ? "its kind is empty"
            : AnswerAt < 0 ? $"its answer stands at {AnswerAt.ToString(CultureInfo.InvariantCulture)}"
            : null;
}

/// <summary>One consent a person or a configuration gave a fold family step by name.</summary>
/// <param name="Kind">The question's kind it answered: a consent's, or <c>foldOutside</c>.</param>
/// <param name="Named">What it was given for: exactly what the question showed.</param>
/// <param name="Question">The id of the question it answered.</param>
/// <param name="Answer">The answer: <c>allow</c> or <c>merge</c>.</param>
public sealed record FlowFoldGrant(string Kind, IReadOnlyList<string> Named, string Question, string Answer)
{
    /// <summary>What is wrong with this entry; null when nothing is.</summary>
    public string? Problem()
        => string.IsNullOrWhiteSpace(Kind) ? "its kind is empty"
            : !FlowQuestionIds.IsWellFormed(Question) ? $"'{Question}' is not a question's id"
            : Answer is not ("allow" or "merge") ? $"'{Answer}' gives no consent"
            : Named.Count == 0 ? "it names nothing it was given for"
            : null;
}

/// <summary>How a fold family step's apply ended, as its record keeps it.</summary>
/// <param name="ExitCode">The code the apply answered with.</param>
/// <param name="Summary">The line it ended on.</param>
/// <param name="Standing">What it stood, as <see cref="RequestStanding"/> names it; null where it failed or stopped part way.</param>
/// <param name="Folded">What a fold wrote, removed, merged, handed and applied; null where it did none of it.</param>
/// <param name="Planned">What its plan named, measured under its hold; null where none was measured.</param>
/// <param name="Ledger">The id of the ledger entry it was recorded under; null for a refresh and a rebase.</param>
public sealed record FlowFoldApplied(int ExitCode, string Summary, string? Standing = null, AgentFolded? Folded = null, AgentPlanned? Planned = null, string? Ledger = null)
{
    /// <summary>What is wrong with this entry; null when nothing is.</summary>
    public string? Problem()
        => Standing is not null && !FlowRecordNames.Is<RequestStanding>(Standing) ? $"'{Standing}' is not a standing"
            : null;
}

/// <summary>
/// One target of a refresh or a rebase of <c>others</c>, as the step's record keeps it: each agent it works on in turn,
/// admitted on its own, and how each ended.
/// </summary>
/// <param name="Agent">The agent.</param>
/// <param name="State">
/// Where it stands: <c>notStarted</c> (its turn has not ended), <c>done</c>, <c>failed</c>, or <c>passedOver</c> (it was
/// deleted, or lost its worktree, before its turn).
/// </param>
/// <param name="Attempts">How many attempts its turn has run.</param>
/// <param name="Exit">The code its turn ended with; null until it ended.</param>
/// <param name="Error">Why it ended as it did, where the code alone cannot say.</param>
/// <param name="Cause">
/// What its code alone cannot say of a failed turn, one of <see cref="OutcomeCause"/>'s names exactly: <c>LockHeld</c> for a
/// refusal whose only obstacle was a lock another run held; null for every other ending.
/// </param>
/// <param name="EndedByQuestion">The kind of the question whose <c>fail</c> answer ended its turn, where one did.</param>
public sealed record FlowTargetRecord(string Agent, string State, int Attempts, int? Exit = null, string? Error = null, string? Cause = null, string? EndedByQuestion = null)
{
    /// <summary>The state of a target whose turn has not ended.</summary>
    public const string NotStarted = "notStarted";

    /// <summary>The state of a target whose turn ended done.</summary>
    public const string Done = "done";

    /// <summary>The state of a target whose turn ended failed.</summary>
    public const string Failed = "failed";

    /// <summary>The state of a target that was gone before its turn.</summary>
    public const string PassedOver = "passedOver";

    /// <summary>The states a target stands in.</summary>
    public static readonly IReadOnlyList<string> States = [NotStarted, Done, Failed, PassedOver];

    /// <summary>Whether its turn has ended.</summary>
    [JsonIgnore]
    public bool Ended => State != NotStarted;

    /// <summary>What is wrong with this entry; null when nothing is.</summary>
    public string? Problem()
        => string.IsNullOrWhiteSpace(Agent) ? "it names no agent"
            : !States.Contains(State, StringComparer.Ordinal) ? $"'{State}' is not where a target stands"
            : Attempts < 0 ? $"its attempts are {Attempts.ToString(CultureInfo.InvariantCulture)}"
            : State == Failed && Exit is null ? "a target that failed names its code"
            : State is NotStarted or PassedOver && Exit is not null ? $"a target {State} names no code"
            : Cause is { } cause && (!FlowRecordNames.Is<OutcomeCause>(cause) || cause == nameof(OutcomeCause.None)) ? $"'{cause}' is not a cause an ending is recorded with"
            : (Cause ?? EndedByQuestion) is not null && State != Failed ? "only a target that failed names a cause or a question that ended it"
            : null;
}
