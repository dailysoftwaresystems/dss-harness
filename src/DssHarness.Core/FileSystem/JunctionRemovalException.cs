namespace RepoHarness.Core.FileSystem;

/// <summary>A directory junction that could not be removed as a link, and the ones that were removed before it.</summary>
/// <param name="path">The junction that could not be removed.</param>
/// <param name="reason">Why, as Windows said it.</param>
/// <param name="removed">The junctions removed before it, as links; what each led to is untouched.</param>
public sealed class JunctionRemovalException(string path, string reason, IReadOnlyList<string> removed)
    : IOException($"'{path}' could not be removed as a link: {reason}")
{
    /// <summary>The junction that could not be removed.</summary>
    public string Path { get; } = path;

    /// <summary>Why, as Windows said it.</summary>
    public string Reason { get; } = reason;

    /// <summary>The junctions removed before it, as links; what each led to is untouched.</summary>
    public IReadOnlyList<string> Removed { get; } = removed;
}
