using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DssHarness.Core.Hosts;
using DssHarness.Core.Json;
using DssHarness.Core.Results;
using DssHarness.Core.Testing;

namespace DssHarness.Core.Execution;

/// <summary>
/// The ledger as data, read back as <see cref="LedgerReport"/> writes it (<c>--json</c>): the one reader of that
/// document's shape, by the names it was written under rather than by position - another machine's answer for a leg it
/// ran, and the ledger a step of an orchestrator run keeps in its directory alike. Each leg's line reads to the
/// <see cref="LegEntry"/> it was written from (<see cref="LedgerDocumentLeg.Entry"/>), its digest through the digest's
/// own reader (<see cref="ResultDigest.Read"/>).
/// </summary>
/// <param name="Legs">Each leg's line.</param>
/// <param name="RunDirectory">Where the run keeps its records, when it got that far.</param>
/// <param name="RunId">The run the command named as it began, when it began one.</param>
/// <param name="Verdict">
/// The verdict of the run, which a run that reported on its legs names; <see langword="null"/> where something other
/// than its legs ended it, as <see cref="LedgerReport"/> writes such a run.
/// </param>
/// <param name="SetUpsLeftRunning">Each set-up a leg of the run left running, where one did.</param>
public sealed record LedgerDocument(
    [property: JsonPropertyName("legs")] IReadOnlyList<LedgerDocumentLeg>? Legs,
    [property: JsonPropertyName("runDirectory")] string? RunDirectory = null,
    [property: JsonPropertyName("runId")] string? RunId = null,
    [property: JsonPropertyName("verdict")] string? Verdict = null,
    [property: JsonPropertyName("setUpsLeftRunning")] IReadOnlyList<LedgerDocumentSetUpLeft>? SetUpsLeftRunning = null)
{
    /// <summary>How the document is read: by the camel-cased names it is written under, at any depth (<see cref="JsonDepth"/>).</summary>
    private static readonly JsonSerializerOptions LedgerOptions = JsonDepth.Serializer(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    });

    /// <summary>The ledger <paramref name="document"/> holds; <see langword="null"/> where it is JSON's null.</summary>
    /// <param name="document">The document, as <see cref="LedgerReport"/> writes it.</param>
    /// <exception cref="JsonException">The document is no ledger's shape.</exception>
    public static LedgerDocument? Read(string document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return JsonSerializer.Deserialize<LedgerDocument>(document, LedgerOptions);
    }
}

/// <summary>A set-up a leg of a run left running, as the ledger names it.</summary>
/// <param name="Leg">The leg.</param>
/// <param name="SetUp">The set-up.</param>
/// <param name="Up">How it came up for the leg.</param>
/// <param name="Inputs">The values it was brought up with.</param>
/// <param name="Detail">Why it did not come down.</param>
public sealed record LedgerDocumentSetUpLeft(
    [property: JsonPropertyName("leg")] string Leg,
    [property: JsonPropertyName("setUp")] string SetUp,
    [property: JsonPropertyName("up")] string? Up = null,
    [property: JsonPropertyName("inputs")] IReadOnlyDictionary<string, string>? Inputs = null,
    [property: JsonPropertyName("detail")] string? Detail = null);

/// <summary>One phase of a leg's line, and its time, as the ledger writes it.</summary>
/// <param name="Phase">The phase.</param>
/// <param name="DurationSeconds">Its time, to the millisecond.</param>
/// <param name="ClockStepped">Whether its time spanned a clock step or a host's sleep.</param>
public sealed record LedgerDocumentPhase(string Phase, double DurationSeconds, bool ClockStepped = false);

/// <summary>One leg's line of a ledger, as its document writes it.</summary>
public sealed record LedgerDocumentLeg(
    string? Verdict,
    string? Detail,
    double DurationSeconds,
    double CommandSeconds,
    int? TestCount,
    IReadOnlyList<string>? TimingNotes,
    IReadOnlyList<string>? SkippedSteps = null,
    IReadOnlyList<Build.CompilerFact>? Compilers = null,
    DeveloperEnvironmentFact? DeveloperEnvironment = null,
    string? Project = null,
    string? TestSet = null,
    IReadOnlyList<string>? LogTail = null,
    IReadOnlyList<string>? ManualSteps = null,
    IReadOnlyList<string>? UnselectedSteps = null,
    IReadOnlyList<string>? RanSteps = null,
    BuildSpace? Space = null,
    IReadOnlyList<string>? KeptOutputs = null,
    KeptOnFailure? KeptOnFailure = null,
    AdmissionFact? Admission = null,
    IReadOnlyList<LedgerDocumentArm>? Arms = null,
    string? Records = null,
    IReadOnlyList<PhaseLogFile>? Logs = null,
    PendingSweep? PendingSweep = null,
    IReadOnlyList<LedgerDocumentControl>? Controls = null,
    IReadOnlyList<SetUps.SetUpFact>? SetUps = null,
    string? TailPhase = null,
    Build.FailedUnits? FailedUnits = null,
    IReadOnlyList<string>? Named = null,
    JsonObject? Digest = null,
    string? Leg = null,
    IReadOnlyList<LedgerDocumentPhase>? Phases = null,
    string? RunDirectory = null,
    string? HostRunId = null,
    string? LogBroughtBack = null)
{
    /// <summary>
    /// The line as the entry it was written from: every member the ledger writes of a leg's work, read back - its phases
    /// with their times, its digest whole (<see cref="ResultDigest.Read"/>) - and each text a program or a host's harness
    /// wrote about the leg read through <paramref name="said"/>, as each line another machine's work printed is read on
    /// this one. A verdict this build does not know is read as <c>poisoned</c>: the line says nothing it can stand on. The
    /// tree the leg worked in is never read back, for it is the placing run's own to say (<see cref="LegEntry.Tree"/>).
    /// </summary>
    /// <param name="said">What each text read becomes on this machine.</param>
    /// <exception cref="JsonException">The line's digest is no digest's shape (<see cref="ResultDigest.Read"/>).</exception>
    public LegEntry Entry(Func<string, string> said)
    {
        ArgumentNullException.ThrowIfNull(said);

        return new LegEntry
        {
            Leg = Leg ?? string.Empty,
            Verdict = Verdicts.Parse(Verdict) ?? LegVerdict.Poisoned,
            Detail = said(Detail ?? string.Empty),
            Named = [.. Named ?? []],
            Duration = TimeSpan.FromSeconds(DurationSeconds),
            CommandTime = TimeSpan.FromSeconds(CommandSeconds),
            TestCount = TestCount,
            Project = Project,
            TestSet = TestSet,
            TimingNotes = [.. TimingNotes ?? []],
            Phases = [.. (Phases ?? []).Select(phase => new PhaseRecord(phase.Phase, TimeSpan.FromSeconds(phase.DurationSeconds), phase.ClockStepped))],
            RunDirectory = RunDirectory,
            HostRunId = HostRunId is { } id && Execution.RunId.TryParse(id, out var run) ? run : null,
            Records = Records,
            Logs = Logs is null ? null : [.. Logs.Select(log => new PhaseLogFile(log.Phase, log.File))],
            LogBroughtBack = LogBroughtBack,
            SkippedSteps = [.. SkippedSteps ?? []],
            RanSteps = [.. RanSteps ?? []],
            ManualSteps = [.. ManualSteps ?? []],
            UnselectedSteps = [.. UnselectedSteps ?? []],
            LogTail = [.. (LogTail ?? []).Select(said)],
            TailPhase = TailPhase,
            Compilers = [.. Compilers ?? []],
            DeveloperEnvironment = DeveloperEnvironment,
            SetUps = [.. SetUps ?? []],
            Admission = Admission,
            Space = Space,
            KeptOutputs = [.. KeptOutputs ?? []],
            KeptOnFailure = KeptOnFailure,
            FailedUnits = FailedUnits is { } failed
                ? failed with { Units = [.. failed.Units ?? []], Unread = failed.Unread is { } unread ? said(unread) : null }
                : null,
            Digest = ResultDigest.Read(Digest, said),
            Arms = [.. (Arms ?? []).Select(arm => new ArmEntry
            {
                Arm = arm.Arm ?? string.Empty,
                Verdict = Verdicts.Parse(arm.Verdict) ?? LegVerdict.Poisoned,
                Detail = said(arm.Detail ?? string.Empty),
                Duration = TimeSpan.FromSeconds(arm.DurationSeconds),
                Worker = arm.Worker,
                Cases = arm.Cases,
                DeclaredCases = arm.DeclaredCases,
                Reds = arm.Reds,
                DeclaredReds = [.. arm.DeclaredReds ?? []],
                Records = arm.Records,
                Key = arm.Key,
                Held = arm.Held,
                Pending = arm.Pending is { } pending ? pending with { Said = said(pending.Said) } : null,
            })],
            PendingSweep = PendingSweep,
            Controls = [.. (Controls ?? []).Select(control => new ControlEntry(
                control.Runner ?? string.Empty,
                control.Verdict ?? string.Empty,
                said(control.Detail ?? string.Empty),
                [.. control.Reds ?? []],
                control.Records ?? string.Empty,
                control.Ctest is { } ctest
                    ? new CtestControl(
                        ctest.Matched,
                        ctest.Unread is { } unread ? said(unread) : null,
                        [.. ctest.Tests ?? []])
                    : null))],
        };
    }
}

/// <summary>One control of a sweep that did not pass, beneath its leg's line.</summary>
public sealed record LedgerDocumentControl(
    string? Runner,
    string? Verdict,
    string? Detail,
    IReadOnlyList<string>? Reds = null,
    string? Records = null,
    LedgerDocumentCtest? Ctest = null);

/// <summary>What ctest registered for a control's red cases.</summary>
public sealed record LedgerDocumentCtest(
    string? Matched = null,
    string? Unread = null,
    IReadOnlyList<CtestTestLine>? Tests = null);

/// <summary>One arm's line of a sweep, beneath its leg's.</summary>
public sealed record LedgerDocumentArm(
    string? Arm,
    string? Verdict,
    string? Detail,
    double DurationSeconds,
    int? Worker,
    int? Cases,
    int DeclaredCases,
    IReadOnlyList<string>? Reds,
    IReadOnlyList<string>? DeclaredReds,
    string? Records,
    string? Key = null,
    ArmHeld? Held = null,
    ArmPending? Pending = null);
