namespace RepoHarness.Core.Processes;

/// <summary>How much of one of a child's streams the process runner keeps for the result it returns.</summary>
public enum StreamKept
{
    /// <summary>
    /// All of it, as the child wrote it, and each of its lines handed on whole: what a caller that reads the stream's text,
    /// or a line of it as one answer, needs - git's answers, a probe's, an agent's.
    /// </summary>
    Whole = 0,

    /// <summary>
    /// Its last <see cref="ProcessRunner.TailLength"/> characters, for the messages that quote it, and its lines handed on
    /// in pieces of at most <see cref="ProcessRunner.LongestLine"/> characters: for a caller that takes the stream's lines as
    /// they come and keeps what it needs of them itself. Nothing then holds the stream whole, which nothing can do for a child
    /// that writes gigabytes: past a billion characters a string cannot hold it, whatever memory the machine has free.
    /// </summary>
    Tail,
}
