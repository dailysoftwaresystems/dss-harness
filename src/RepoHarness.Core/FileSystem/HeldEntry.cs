namespace RepoHarness.Core.FileSystem;

/// <summary>A file or directory another program holds, so that Windows would not delete it.</summary>
/// <param name="Path">The entry.</param>
/// <param name="Reason">What Windows said when it was opened as a deletion opens it.</param>
public sealed record HeldEntry(string Path, string Reason);
