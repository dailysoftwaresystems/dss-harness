namespace DssHarness.Core.Codebase;

/// <summary>
/// What a tree holds otherwise than its base commit: every path that differs, and each declaration added, changed or
/// taken out - what change-first packs a step from (value 371).
/// </summary>
/// <param name="Base">The commit the tree was compared with.</param>
/// <param name="Files">Every path the tree holds otherwise than the base - changed, added, taken out, or untracked - that a graph may list, by path.</param>
/// <param name="Nodes">Each declaration changed, by path and line: added or changed as the tree holds it now, taken out as the base held it.</param>
public sealed record CodebaseGraphChanges(string Base, IReadOnlyList<string> Files, IReadOnlyList<CodeChangedNode> Nodes)
{
    /// <summary>
    /// The declarations of one file that differ between <paramref name="before"/> and <paramref name="after"/>, each of
    /// <see cref="CodeNodeKinds.Declarations"/>' kinds: one the base did not declare, added; one whose digest moved and whose
    /// own lines - its span, less the spans of what is declared inside it - a hunk meets, changed: its new lines among the
    /// node's own lines now, or the base's lines it took among the node's own lines at the base, so lines taken out at a
    /// node's first or last line are its; one the base declared and the file no longer does, taken out, each with what it
    /// was declared inside, so a reader tells what went with its container. Its lines are where it differs among its own
    /// lines - a place lines were only taken out where nothing new stands - and an added node's are its span.
    /// </summary>
    /// <param name="path">The file's path, relative to the tree with forward separators.</param>
    /// <param name="before">The file's extract at the base; null where the base held no file there.</param>
    /// <param name="beforeLines">Its lines at the base; none where it held no file there.</param>
    /// <param name="after">Its extract now; null where the tree holds no file there.</param>
    /// <param name="afterLines">Its lines now; none where the tree holds no file there.</param>
    public static IReadOnlyList<CodeChangedNode> Compared(string path, FileExtract? before, IReadOnlyList<string> beforeLines, FileExtract? after, IReadOnlyList<string> afterLines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(beforeLines);
        ArgumentNullException.ThrowIfNull(afterLines);

        var hunks = CodeLineDiff.Hunks(beforeLines, afterLines);
        var then = Declared(before);
        var now = Declared(after);
        var was = then.ToLookup(node => node.Id, StringComparer.Ordinal);
        var innerThen = Inner(then);
        var innerNow = Inner(now);
        var nowIds = now.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var changed = new List<CodeChangedNode>();

        foreach (var node in now)
        {
            if (was[node.Id].FirstOrDefault() is not { } old)
            {
                changed.Add(Changed(path, node, CodeChanges.Added, [new CodeLineHunk(node.Start, node.End - node.Start + 1, 0, 0)]));
                continue;
            }

            if (old.Digest is not null && string.Equals(old.Digest, node.Digest, StringComparison.Ordinal))
            {
                continue;
            }

            var own = Own(node, innerNow[node.Id], old, innerThen[node.Id], hunks);

            if (own.Count > 0)
            {
                changed.Add(Changed(path, node, CodeChanges.Changed, own));
            }
        }

        changed.AddRange(then.Where(node => !nowIds.Contains(node.Id)).Select(node => Changed(path, node, CodeChanges.Removed, [])));

        return [.. changed.OrderBy(node => node.Start).ThenBy(node => node.Id, StringComparer.Ordinal)];
    }

    private static List<ExtractNode> Declared(FileExtract? extract)
        => [.. (extract?.Nodes ?? []).Where(node => CodeNodeKinds.Declarations.Contains(node.Kind, StringComparer.Ordinal))];

    /// <summary>What is declared inside each node, by the node's id: built once a file.</summary>
    private static ILookup<string, ExtractNode> Inner(List<ExtractNode> nodes)
        => nodes.Where(node => node.Parent is not null).ToLookup(node => node.Parent!, StringComparer.Ordinal);

    private static CodeChangedNode Changed(string path, ExtractNode node, string change, IReadOnlyList<CodeLineHunk> lines)
        => new(node.Id, node.Kind, node.Qualified, path, node.Start, node.End, change, lines, node.Parent);

    /// <summary>
    /// The hunks that meet <paramref name="node"/>'s own lines: each run of its new lines among the node's own lines now,
    /// and, where none stands there, the place lines were taken out from among the node's own lines at the base.
    /// </summary>
    private static List<CodeLineHunk> Own(ExtractNode node, IEnumerable<ExtractNode> inner, ExtractNode old, IEnumerable<ExtractNode> oldInner, IReadOnlyList<CodeLineHunk> hunks)
    {
        var spans = inner.Select(one => (one.Start, one.End)).ToList();
        var oldSpans = oldInner.Select(one => (one.Start, one.End)).ToList();
        var own = new List<CodeLineHunk>();

        foreach (var hunk in hunks)
        {
            var runs = CodeOwnLines.Runs(Math.Max(hunk.Start, node.Start), Math.Min(hunk.End, node.End), spans);

            if (runs.Count > 0)
            {
                own.AddRange(runs.Select((run, at) => new CodeLineHunk(run.Start, run.End - run.Start + 1, at == 0 ? hunk.Taken : 0, hunk.From)));
            }
            else if (CodeOwnLines.Meet(hunk.From, hunk.TakenEnd, old.Start, old.End, oldSpans))
            {
                own.Add(new CodeLineHunk(hunk.Start, 0, hunk.Taken, hunk.From));
            }
        }

        return own;
    }
}

/// <summary>One declaration a change added, changed or took out.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Kind">Its kind (<see cref="CodeNodeKinds"/>).</param>
/// <param name="Qualified">Its qualified name.</param>
/// <param name="Path">The file it is declared in.</param>
/// <param name="Start">Its first line - now, or at the base where it was taken out.</param>
/// <param name="End">Its last line, inclusive, likewise.</param>
/// <param name="Change">What became of it: one of <see cref="CodeChanges"/>.</param>
/// <param name="Lines">Where it differs among its own lines, a place lines were only taken out with none new; its span where it was added; none where it was taken out.</param>
/// <param name="Parent">The declaration it is declared inside, by id - now, or at the base where it was taken out; null for one at a file's top.</param>
public sealed record CodeChangedNode(string Id, string Kind, string Qualified, string Path, int Start, int End, string Change, IReadOnlyList<CodeLineHunk> Lines, string? Parent);

/// <summary>What a change did to a declaration.</summary>
public static class CodeChanges
{
    /// <summary>The base did not declare it.</summary>
    public const string Added = "added";

    /// <summary>Its own lines differ from the base's.</summary>
    public const string Changed = "changed";

    /// <summary>The base declared it, and the tree no longer does.</summary>
    public const string Removed = "removed";
}
