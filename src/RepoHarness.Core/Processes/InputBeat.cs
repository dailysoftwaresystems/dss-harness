namespace RepoHarness.Core.Processes;

/// <summary>
/// A line written to a child's standard input again and again for as long as that input is held open, so that a child
/// watching its input knows this process is still there.
/// </summary>
/// <remarks>
/// The end of a held input tells a child this process has gone, where whatever carries the input says so. An ssh server
/// that never closes its end, or a connection that dies without a word, says nothing: the input simply stays open, and
/// what the child started goes on with nobody to read it. A beat that stops is told apart from one that goes on by
/// anything that carries bytes at all.
/// </remarks>
/// <param name="Every">How long passes between one beat and the next.</param>
/// <param name="Line">What each beat writes, without its line ending.</param>
public sealed record InputBeat(TimeSpan Every, string Line);
