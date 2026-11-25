namespace DssHarness.Core.Processes;

/// <summary>
/// When a process of Windows was made, as the walk of a tree (<see cref="Containment.EndTree"/>) and the chain of this
/// process's own parents order a creator and a child Windows' list of every process names as made by it: by the sequence
/// number Windows gives each process as it creates it (ProcessSequenceNumber) - counted up once a boot, so a process made
/// later always holds a higher one, and never moved by the clock - where both of the two could be opened to read theirs,
/// and by their creation times where either could not.
/// </summary>
/// <remarks>
/// The list names each process's creator by its id alone, and goes on naming that id once the creator has gone and the
/// id has been given to another: what tells a later holder of the id from the creator is that it was made after the
/// child, which no creator is. A creation time is the system clock's, which steps - a step back sets a child's before its
/// creator's - so it decides only where a sequence number cannot be read: of a process that refuses this process every
/// right, one passed over as it was reached, or one the list names and no handle reaches.
/// </remarks>
/// <param name="Created">When the process was created, as a file time.</param>
/// <param name="Sequence">The process's sequence number, where it was read; <see langword="null"/> where it could not be.</param>
internal readonly record struct CreationOrder(long Created, ulong? Sequence)
{
    /// <summary>
    /// Whether this process was made before <paramref name="other"/>: by their sequence numbers where both were read, by
    /// their creation times where either was not.
    /// </summary>
    /// <param name="other">The other process.</param>
    public bool MadeBefore(CreationOrder other)
        => Sequence is { } mine && other.Sequence is { } theirs ? mine < theirs : Created < other.Created;
}
