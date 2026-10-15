namespace DssHarness.Core.Git;

/// <summary>One entry of a commit's tree, as <c>git ls-tree</c> lists it.</summary>
/// <param name="Mode">The octal mode, such as 100644 for a file, 100755 for one that can be run, or 120000 for a link.</param>
/// <param name="Type">What the entry is: <c>blob</c>, <c>tree</c>, or <c>commit</c> for a submodule's.</param>
/// <param name="ObjectId">The object git holds the entry in.</param>
public sealed record GitEntry(string Mode, string Type, string ObjectId)
{
    /// <summary>The mode of a file that can be run.</summary>
    public const string ExecutableMode = "100755";

    /// <summary>The mode of a symbolic link, which git holds as a blob of its target's name.</summary>
    public const string LinkMode = "120000";

    /// <summary>
    /// Whether the entry is a blob - a file, or a symbolic link, its text the path it points at - rather than a directory,
    /// or a submodule's entry, which names a commit in another repository.
    /// </summary>
    public bool IsBlob => Type == "blob";

    /// <summary>Whether the entry is a file that can be run: its execute bit is set in the commit.</summary>
    public bool IsExecutable => Mode == ExecutableMode;

    /// <summary>Whether the entry is a symbolic link.</summary>
    public bool IsLink => Mode == LinkMode;
}

/// <summary>What <c>git check-attr</c> answers for an attribute that is not given a value of its own.</summary>
public static class GitAttributes
{
    /// <summary>The attribute is set: named with no value.</summary>
    public const string Set = "set";

    /// <summary>The attribute is unset: named with a leading dash.</summary>
    public const string Unset = "unset";

    /// <summary>No pattern says anything of the attribute for the path.</summary>
    public const string Unspecified = "unspecified";
}
