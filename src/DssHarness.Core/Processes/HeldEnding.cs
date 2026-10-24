namespace DssHarness.Core.Processes;

/// <summary>
/// What ending a session or a process group found (<see cref="LeftSessions.EndAsync"/>): whether anything was still in
/// it to end, and what of it could not be reached, and why.
/// </summary>
/// <param name="Found">
/// Whether it still held a process when it was looked for - or could not be told not to, its table unread - so that its
/// end is one to say; false where it had already ended, and nothing was signalled.
/// </param>
/// <param name="Unreached">What of it could not be reached, and why; <see langword="null"/> where everything in it was.</param>
public readonly record struct HeldEnding(bool Found, string? Unreached);
