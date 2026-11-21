namespace DssHarness.Tests;

/// <summary>
/// Drives a <see cref="TimerClock"/> under code that waits on it: the clock is moved a step each time the code has a timer
/// armed, until what the code runs ends. A test of a wait that lasts a day takes the steps the day has, and no real time.
/// </summary>
internal static class ClockDrive
{
    /// <summary>
    /// How long, in real time, a test waits for the code under test to arm its next timer or end: code that does neither
    /// within it is stuck, which is a fault of its own.
    /// </summary>
    public static readonly TimeSpan Within = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Moves <paramref name="clock"/> by <paramref name="step"/> whenever the code under test waits on it, until
    /// <paramref name="run"/> ends, and answers what it gave. Fails the test where the run has not ended after
    /// <paramref name="mostSteps"/> steps - a wait that should have ended and goes on fails there, rather than hanging the
    /// suite - or where it neither ends nor waits on the clock within <see cref="Within"/>.
    /// </summary>
    /// <typeparam name="T">What the run answers.</typeparam>
    /// <param name="clock">The clock the code under test waits on.</param>
    /// <param name="run">What the code runs.</param>
    /// <param name="step">How far the clock is moved each time.</param>
    /// <param name="mostSteps">The most steps the run may take before the test fails.</param>
    /// <param name="cancellationToken">The test's own token.</param>
    public static async Task<T> UntilEndedAsync<T>(TimerClock clock, Task<T> run, TimeSpan step, int mostSteps, CancellationToken cancellationToken)
    {
        for (var steps = 0; ; steps++)
        {
            // The run first: once it has ended, a timer it left armed is no reason to move the clock again.
            var next = await Task.WhenAny(run, clock.WaitingAsync()).WaitAsync(Within, cancellationToken);

            if (ReferenceEquals(next, run))
            {
                return await run;
            }

            if (steps == mostSteps)
            {
                Assert.Fail($"the run had not ended after {mostSteps} steps of {step}");
            }

            clock.Advance(step);
        }
    }
}
