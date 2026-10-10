using RepoHarness.Core.Results;

namespace RepoHarness.Core.Git;

/// <summary>
/// The commit a tree is at, and how much of the history behind it a copy of the tree is asked to hold, so that git
/// answers about HEAD in the copy as it does in the tree.
/// </summary>
/// <param name="Commit">
/// The full id of the tree's HEAD commit, or <see langword="null"/> where its HEAD names none yet: nothing is then asked
/// of a copy but that a repository made for it name its objects as the tree's does.
/// </param>
/// <param name="Whole">
/// Whether every commit behind it is asked for, as far as the tree's own repository holds them; otherwise the commit
/// alone, with everything its tree names.
/// </param>
/// <param name="ObjectFormat">How the tree's repository names its objects: <c>sha1</c> or <c>sha256</c>.</param>
/// <param name="Boundary">
/// The commits the tree's own history stops at, where its repository is a shallow clone; none where it holds every
/// commit. A copy can hold no more behind one of them than the tree does.
/// </param>
public sealed record GitHistoryWanted(string? Commit, bool Whole, string ObjectFormat, IReadOnlyList<string> Boundary)
{
    /// <summary>How git names objects where nothing says otherwise, and the one way an old git knows.</summary>
    public const string DefaultObjectFormat = "sha1";

    /// <summary>How the commit alone is spelled, where a request or a configuration says how much is asked for.</summary>
    public const string HeadOnly = "head";

    /// <summary>How every commit behind it is spelled.</summary>
    public const string Full = "full";

    /// <summary>How much is asked for, as a request spells it.</summary>
    public string Depth => Whole ? Full : HeadOnly;
}

/// <summary>What a repository holds of the history another asks it to hold.</summary>
/// <param name="ObjectFormat">How this repository names its objects.</param>
public sealed record GitHistoryHeld(string ObjectFormat)
{
    /// <summary>What a repository just made holds: nothing, its HEAD naming no commit.</summary>
    /// <param name="objectFormat">How it names its objects.</param>
    public static GitHistoryHeld Nothing(string objectFormat) => new(objectFormat);

    /// <summary>The commit its HEAD names, or <see langword="null"/> where it names none yet.</summary>
    public string? Head { get; init; }

    /// <summary>The branch its HEAD is on, where it names a commit through one; <see langword="null"/> where it is detached.</summary>
    public string? Branch { get; init; }

    /// <summary>Whether it holds the commit asked for, and as much behind it as was asked.</summary>
    public bool Holds { get; init; }

    /// <summary>
    /// Whether what it was sent may leave out what its own HEAD holds: the files of that commit where the commit alone
    /// was asked for, and every commit behind it, as far as the asker's own history goes, where they all were.
    /// </summary>
    public bool HeadHeld { get; init; }
}

/// <summary>A pack of git objects written for a repository that lacks them.</summary>
/// <param name="File">Where it was written.</param>
/// <param name="Name">The name git gave it: the hash of what it holds.</param>
/// <param name="Bytes">How many bytes it is.</param>
public sealed record GitHistoryPack(string File, string Name, long Bytes);

/// <summary>What a repository is given to take: the history asked of it, and the objects it was found to lack.</summary>
/// <param name="Wanted">The commit its HEAD is to name, and how much behind it.</param>
/// <param name="Pack">
/// The name of the pack holding what it lacked, every piece of which it was sent first, or <see langword="null"/> where
/// it lacked nothing.
/// </param>
/// <param name="LeftOut">The commit of its own the pack leaves out as held already, or <see langword="null"/> where it leaves nothing out.</param>
public sealed record GitHistoryTaken(GitHistoryWanted Wanted, string? Pack, string? LeftOut);

/// <summary>Where a repository's HEAD stood before a history was taken, and whether taking it moved it.</summary>
/// <param name="From">The commit it named, or <see langword="null"/> where it named none.</param>
/// <param name="Branch">The branch it was on, where it named a commit through one.</param>
/// <param name="Moved">
/// Whether it was moved: not where it named the commit asked for already, and is then on whatever branch it was on.
/// </param>
public sealed record GitHeadMoved(string? From, string? Branch, bool Moved)
{
    /// <summary>
    /// Why the pack kept aside for the take could not be removed afterwards, naming where it is; <see langword="null"/>
    /// where it was, or none was kept. The next pack sent clears it, or says why it cannot.
    /// </summary>
    public string? LeftAside { get; init; }

    /// <summary>
    /// That the repository held every commit behind those it holds and now holds one without, as a line says it, naming
    /// the repository, the commit, and what undoes it: git reads it as a shallow repository from here on.
    /// <see langword="null"/> where it was one already, held no commit, or was given the commit's parents.
    /// </summary>
    public string? CutShort { get; init; }
}

/// <summary>The name git gives an object, as it crosses between two machines.</summary>
public static class GitObjectId
{
    /// <summary>Whether <paramref name="text"/> is a whole object name: 40 or 64 lowercase hexadecimal digits.</summary>
    /// <param name="text">What claims to be one.</param>
    public static bool IsWhole(string? text)
        => text is { Length: 40 or 64 } && text.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary><paramref name="text"/>, where it is a whole object name.</summary>
    /// <param name="text">What claims to be one.</param>
    /// <param name="what">What it names, for the refusal.</param>
    /// <exception cref="HarnessException">It is not one: the two ends are different builds.</exception>
    /// <remarks>
    /// Refused before it reaches git or a path: what arrives from another machine is put on a command line and into a
    /// file's name, and a name of any other shape is no object's.
    /// </remarks>
    public static string Require(string? text, string what)
        => IsWhole(text)
            ? text!
            : throw new HarnessException(
                HarnessExit.UsageError,
                $"'{text}' is not the whole name of a git object, so it cannot be {what}. The two ends are different builds.");
}
