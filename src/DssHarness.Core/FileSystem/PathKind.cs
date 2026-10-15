namespace RepoHarness.Core.FileSystem;

/// <summary>What is at a path itself, as <see cref="IFileSystem.KindOf"/> answers.</summary>
public enum PathKind
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary>A file.</summary>
    File,

    /// <summary>A directory.</summary>
    Directory,

    /// <summary>A symbolic link or a junction, as itself, whatever it leads to or whether it leads anywhere.</summary>
    Link,
}
