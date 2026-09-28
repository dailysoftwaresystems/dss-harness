using System.ComponentModel;

namespace RepoHarness.Core.Configuration;

/// <summary>
/// A failure a runner is allowed to produce, and what to report when it does.
/// </summary>
/// <remarks>
/// An entry has two halves that must not be confused: what it <em>matches</em>, and the
/// <em>outcome</em> the match produces. It also has to show its work. A catalogue of excuses that
/// nobody can audit stops being a record of measured confounds and becomes a way to make a
/// regression invisible, so every entry carries when it was earned, by what measurement, and
/// which anchor holds the evidence.
/// </remarks>
public sealed class ExpectedException
{
    /// <summary>The exception's class name, matched exactly.</summary>
    [Description("the exception's class name, matched exactly")]
    public required string ExceptionType { get; init; }

    /// <summary>
    /// Messages this entry recognises, each plain text or a regular expression. Any one matching
    /// is a match, because one confound reaches a reader through several wordings.
    /// </summary>
    [Description("text or a regular expression its message or output shows")]
    public List<string> Messages { get; init; } = [];

    /// <summary>Whether a run producing this failure is reported as a success.</summary>
    [Description("report a run that fails this way as passed")]
    public bool Success { get; init; }

    /// <summary>Whether the outcome carries a warning, so an excused failure is still visible.</summary>
    [Description("keep a warning on that outcome; true unless set false")]
    public bool Warning { get; init; } = true;

    /// <summary>Result code to report. Never negative: a negative code is not one a process can return.</summary>
    [Description("the result code to report")]
    public int ResultCode { get; init; }

    /// <summary>What to report instead of the unexplained failure. Never blank.</summary>
    [Description("what to report in place of the failure")]
    public required string Message { get; init; }

    /// <summary>The day this entry was earned, as <c>yyyy-MM-dd</c>.</summary>
    [Description("the day it was earned, as yyyy-MM-dd")]
    public required string EarnedOn { get; init; }

    /// <summary>Where it was earned: the leg, host or run the measurement was taken on.</summary>
    [Description("the leg, host or run it was measured on")]
    public required string EarnedAt { get; init; }

    /// <summary>The mechanism measured, in one sentence. Not a restatement of the message.</summary>
    [Description("the mechanism measured, in one sentence")]
    public required string Mechanism { get; init; }

    /// <summary>The anchor id holding the evidence, so the claim can be read back.</summary>
    [Description("the anchor id holding the evidence")]
    public required string Anchor { get; init; }

    /// <summary>
    /// Checks that must confirm the confound before this entry excuses anything. Unconfirmed, the
    /// failure stays genuine: an unconditional excusal hides the regression it was written to explain.
    /// </summary>
    [Description("checks that must confirm it before it excuses anything")]
    public List<RunCheck> RunChecks { get; init; } = [];
}

/// <summary>
/// A confirmation an expected exception is gated on: another predefined runner is invoked, and its
/// outcome compared with what the check expects.
/// </summary>
/// <remarks>
/// A check is one level deep and cannot recurse: the runner it names is never the one carrying it,
/// and a runner reached this way may carry no checks of its own.
/// </remarks>
public sealed class RunCheck
{
    /// <summary>The predefined runner to invoke.</summary>
    [Description("the runner to run")]
    public required string PredefinedRunner { get; init; }

    /// <summary>What that runner's outcome must be for this check to pass.</summary>
    [Description("what that runner's outcome must be")]
    public RunCheckExpectation Expects { get; init; } = new();

    /// <summary>
    /// Fewest steps of at least <see cref="MinStepSeconds"/> that must be recorded inside the failing
    /// unit's own execution window for the failure to be excused.
    /// </summary>
    /// <remarks>
    /// The window is the failing unit's own — its output up to its verdict line — never a once-per-run
    /// sample. A sample taken before the run only says what the machine was doing then: a loaded run
    /// measured quiet charged genuine-looking failures to the tool under test, and a quiet run measured
    /// loaded excused them, on the same day. Zero means the check does not look at steps at all.
    /// </remarks>
    [Description("fewest minStepSeconds steps in the failing window; 0, none")]
    public int MinStepsInFailureWindow { get; init; }

    /// <summary>Shortest step that counts toward <see cref="MinStepsInFailureWindow"/>, in seconds.</summary>
    [Description("the shortest step counted, in seconds")]
    public double MinStepSeconds { get; init; }
}

/// <summary>
/// What a <see cref="RunCheck"/> requires of the runner it names. A field left out is not a
/// requirement, so a check states only what it actually measured.
/// </summary>
public sealed class RunCheckExpectation
{
    /// <summary>
    /// Whether the named runner must produce the same success, warning, result code and message as
    /// the entry this check gates.
    /// </summary>
    [Description("the same success, warning, result code and message")]
    public bool? SameException { get; init; }

    /// <summary>Whether the named runner must succeed.</summary>
    [Description("whether it must succeed")]
    public bool? Success { get; init; }

    /// <summary>Result code the named runner must report.</summary>
    [Description("the result code it must report")]
    public int? ResultCode { get; init; }

    /// <summary>Text the named runner's message must contain.</summary>
    [Description("text its message or its steps' output must contain")]
    public string? Message { get; init; }
}
