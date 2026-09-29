namespace RepoHarness.Core.Worktrees;

/// <summary>
/// Where a worktree sits under the worktrees root, and so what it is called: a plain worktree by its own name, and an
/// orchestrator's agent by both names, <c>orchestrator/agent</c>, as the directories it sits in are named. One
/// definition for every place a worktree is named or found - creating, deleting and listing it, the record of the
/// commit it was made from, and the name its copies on hosts are kept under - so no worktree is spelled two ways.
/// </summary>
public sealed record WorktreeAddress
{
    /// <summary>What joins an orchestrator's name to its agent's in an address, as their directories are joined.</summary>
    public const char Separator = '/';

    /// <summary>
    /// What joins them where a single path component is needed: the directory a host keeps a copy in, beside its main
    /// copy. A name never holds two hyphens together, and a copy's name is spelt so it never does either, so the two
    /// can never be read as one name.
    /// </summary>
    public const string CopySeparator = "--";

    private WorktreeAddress(IReadOnlyList<string> segments)
    {
        Segments = segments;
    }

    /// <summary>The names, outermost first: one for a plain worktree; an orchestrator's and its agent's for an agent's.</summary>
    public IReadOnlyList<string> Segments { get; }

    /// <summary>What the worktree is called wherever a person reads or types it: <c>name</c>, or <c>orchestrator/agent</c>.</summary>
    public string Name => string.Join(Separator, Segments);

    /// <summary>Whether this is an orchestrator's agent's worktree.</summary>
    public bool IsNested => Segments.Count == 2;

    /// <summary>The name the worktree's copies on hosts are kept under: <c>name</c>, or <c>orchestrator--agent</c>.</summary>
    public string CopyName => string.Join(CopySeparator, Segments);

    /// <summary>A plain worktree's address.</summary>
    /// <param name="name">A name that passed <see cref="WorktreeName.ValidateFormat"/>.</param>
    public static WorktreeAddress Plain(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new WorktreeAddress([name]);
    }

    /// <summary>An orchestrator's agent's address.</summary>
    /// <param name="orchestrator">The orchestrator's name, which passed <see cref="WorktreeName.ValidateFormat"/>.</param>
    /// <param name="agent">The agent's name, which passed it too.</param>
    public static WorktreeAddress Nested(string orchestrator, string agent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orchestrator);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);
        return new WorktreeAddress([orchestrator, agent]);
    }

    /// <summary>
    /// Reads an address as typed: one name, or an orchestrator's and its agent's joined by <see cref="Separator"/>,
    /// each shaped as <see cref="WorktreeName.ValidateFormat"/> requires. Only the shape: the length each may have is
    /// the configuration's.
    /// </summary>
    /// <param name="text">What was typed.</param>
    /// <param name="address">The address, when it reads as one.</param>
    /// <param name="error">Why it does not, when it does not.</param>
    public static bool TryParse(string? text, out WorktreeAddress? address, out string error)
    {
        address = null;

        if (string.IsNullOrWhiteSpace(text) || !text.Contains(Separator, StringComparison.Ordinal))
        {
            if (!WorktreeName.ValidateFormat(text).TryGetName(out var name, out var nameError))
            {
                error = nameError;
                return false;
            }

            address = Plain(name);
            error = string.Empty;
            return true;
        }

        var segments = text.Split(Separator);

        if (segments.Length != 2)
        {
            error = $"'{text}' names more than an orchestrator and one of its agents: a worktree is named 'name', or 'orchestrator/agent'.";
            return false;
        }

        foreach (var segment in segments)
        {
            if (!WorktreeName.ValidateFormat(segment).TryGetName(out _, out var segmentError))
            {
                error = $"'{text}' is not a valid address: {segmentError}";
                return false;
            }
        }

        address = Nested(segments[0], segments[1]);
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// The agent's name, where <paramref name="name"/> is the address of an agent of <paramref name="orchestrator"/>;
    /// null for a plain worktree's, another orchestrator's agent's, or text that is no address.
    /// </summary>
    /// <param name="orchestrator">The orchestrator.</param>
    /// <param name="name">An address, as a listing names a worktree.</param>
    public static string? AgentOf(string orchestrator, string name)
        => TryParse(name, out var address, out _) && address!.IsNested && string.Equals(address.Segments[0], orchestrator, StringComparison.Ordinal)
            ? address.Segments[1]
            : null;

    /// <summary>The worktree's directory under <paramref name="worktreesDirectory"/>.</summary>
    /// <param name="worktreesDirectory">The worktrees root, as a full path.</param>
    public string PathUnder(string worktreesDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreesDirectory);
        return Path.Combine([worktreesDirectory, .. Segments]);
    }

    /// <summary>
    /// The address of the tree at <paramref name="treeRoot"/>, where it is a worktree's directory under
    /// <paramref name="worktreesDirectory"/> - a name, or an orchestrator's and an agent's - and otherwise
    /// <see langword="null"/>. Both paths are compared as given: spelt both with links resolved, or neither.
    /// </summary>
    /// <param name="worktreesDirectory">The worktrees root.</param>
    /// <param name="treeRoot">The tree.</param>
    /// <param name="comparison">How this machine compares paths.</param>
    public static WorktreeAddress? OfTree(string worktreesDirectory, string treeRoot, StringComparison comparison)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreesDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(treeRoot);

        if (!FileSystem.PathContainment.IsStrictlyInside(worktreesDirectory, treeRoot, comparison))
        {
            return null;
        }

        var relative = Path.GetRelativePath(Path.GetFullPath(worktreesDirectory), Path.GetFullPath(treeRoot))
            .Replace(Path.DirectorySeparatorChar, Separator)
            .Replace(Path.AltDirectorySeparatorChar, Separator)
            .TrimEnd(Separator);

        return TryParse(relative, out var address, out _) ? address : null;
    }

    /// <summary>
    /// The address a copy's name stands for, as a person types it: <c>orchestrator/agent</c> for
    /// <c>orchestrator--agent</c>, and a plain worktree's name as it is.
    /// </summary>
    /// <param name="copyName">A name copies are kept under.</param>
    public static string OfCopyName(string copyName)
    {
        ArgumentNullException.ThrowIfNull(copyName);
        return copyName.Replace(CopySeparator, Separator.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Whether <paramref name="other"/> names the same worktree.</summary>
    /// <param name="other">Another address.</param>
    public bool Equals(WorktreeAddress? other) => other is not null && string.Equals(Name, other.Name, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Name);

    /// <inheritdoc/>
    public override string ToString() => Name;
}
