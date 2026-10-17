using DssHarness.Core.Configuration;
using DssHarness.Core.Processes;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// What an exception that ends a command means, in the one place that owns the answer - so every front end, the command
/// line today and a later one, ends the same failure the same way.
/// </summary>
public sealed class KnownCausesTests
{
    /// <summary>
    /// A failure the service already decided on means its own exit code, and is not a defect in this tool: the line it
    /// ends on is the service's own message.
    /// </summary>
    [Fact]
    public void AFailureTheServiceDecidedOn_MeansItsOwnExitCode()
        => AssertMeaning(new HarnessException(HarnessExit.Refused, "nothing was changed"), HarnessExit.Refused, "nothing was changed", defect: false);

    /// <summary>A configuration that cannot be read means the configuration's code, whatever was wrong with it.</summary>
    [Fact]
    public void AConfigurationThatCannotBeRead_MeansTheConfigurationCode()
        => AssertMeaning(new ConfigException("an unknown key"), HarnessExit.ConfigInvalid, "an unknown key", defect: false);

    /// <summary>
    /// A program that would not start means the tool is missing, by the cause the leg executor knows too: the same
    /// missing program means the same thing whether it stopped a command or a leg.
    /// </summary>
    [Fact]
    public void AProgramThatWouldNotStart_MeansTheToolIsMissing()
        => AssertMeaning(new ProgramStartException("cmake", "not found"), HarnessExit.ToolMissing, "not found", defect: false);

    /// <summary>
    /// An interruption means no failure code: nothing ran to completion, and a caller reading a failure code would
    /// report a red verdict for an interrupted run.
    /// </summary>
    [Fact]
    public void AnInterruption_MeansNoFailureCode()
        => AssertMeaning(new OperationCanceledException(), HarnessExit.Cancelled, "Interrupted before completion.", defect: false);

    /// <summary>
    /// Anything else is a defect in this tool, not a failure of the thing asked about, said with the tool's own code so
    /// it never collides with a command's contract.
    /// </summary>
    [Fact]
    public void AnythingElse_IsADefectOfThisTool()
        => AssertMeaning(new InvalidOperationException("boom"), HarnessExit.InternalError, "Unexpected InvalidOperationException: boom", defect: true);

    private static void AssertMeaning(Exception exception, int exitCode, string message, bool defect)
    {
        var meaning = KnownCauses.Meaning(exception);

        Assert.Equal(exitCode, meaning.ExitCode);
        Assert.Equal(message, meaning.Message);
        Assert.Equal(defect, meaning.Defect);
    }
}
