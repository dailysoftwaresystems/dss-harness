namespace RepoHarness.Core.Git;

/// <summary>
/// What a commit holds at a path: a file - or a symbolic link, which git holds as a file of its target's name - a
/// directory, a submodule's commit, or nothing. Each is told apart: a directory or a submodule read as nothing is how a
/// directory the commit held was taken for one an agent made.
/// </summary>
public sealed record GitHeld
{
    private GitHeld(string? blob, bool isDirectory, string? commit)
    {
        Blob = blob;
        IsDirectory = isDirectory;
        Commit = commit;
    }

    /// <summary>No entry at the path.</summary>
    public static GitHeld Nothing { get; } = new(null, isDirectory: false, null);

    /// <summary>A directory, whose entries are held at paths of their own.</summary>
    public static GitHeld Directory { get; } = new(null, isDirectory: true, null);

    /// <summary>The id of the blob a file holds; <see langword="null"/> where the entry is no file.</summary>
    public string? Blob { get; }

    /// <summary>Whether the entry is a directory.</summary>
    public bool IsDirectory { get; }

    /// <summary>The commit a submodule's entry names; <see langword="null"/> where the entry is no submodule.</summary>
    public string? Commit { get; }

    /// <summary>Whether there is no entry at the path.</summary>
    public bool IsNothing => Blob is null && !IsDirectory && Commit is null;

    /// <summary>A file, holding the blob <paramref name="blob"/>.</summary>
    public static GitHeld File(string blob)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blob);
        return new(blob, isDirectory: false, null);
    }

    /// <summary>A submodule's entry, naming the commit <paramref name="commit"/>.</summary>
    public static GitHeld Submodule(string commit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);
        return new(null, isDirectory: false, commit);
    }
}
