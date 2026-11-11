namespace DssHarness.Core.Codebase;

/// <summary>
/// The changes a conversation is told of the codebase ("Deltas, never a rewrite of what was sent"): a step that goes on
/// with a stored conversation is given, in its one new user message, the declarations its view changed since what that
/// conversation was told of them - its pack and every delta after it - moved or changed with the new range and hash,
/// removed; and the nodes of its own pack the conversation was not given, added; never a map or a pack again whole, for
/// a conversation's prefix is never changed. A declaration is known by its handle in its file, so one that moved is told
/// where it stands now and not named removed and added. Made by this one owner from the view, the step's own pack and
/// the codebase parts the conversation holds, each line read back by the one reader of a node's line
/// (<see cref="GraphLines.Placed"/>), so the same step on the same tree tells the same changes.
/// </summary>
public static class CodebaseConversationChanges
{
    /// <summary>What a delta writes before the line of a declaration that was taken out.</summary>
    public const string RemovedPrefix = "removed: ";

    /// <summary>One declaration the conversation holds: where it stands, and the line it was told by.</summary>
    /// <param name="Place">Where the line says it stands.</param>
    /// <param name="Line">The line as it was written.</param>
    private sealed record Stood(GraphPlace Place, string Line)
    {
        /// <summary>What names it as a set holds it: its handle in its file.</summary>
        public string Key => $"{Place.Path}:{Place.Handle}";
    }

    /// <summary>
    /// The changes of <paramref name="view"/> since what the conversation was told of the codebase - every part in
    /// <paramref name="told"/>, in order - as a step that goes on with it is told them: the declarations it holds that
    /// moved, changed or were removed - each with its new range and hash where it stands - and the nodes of
    /// <paramref name="pack"/> it was not told of, added, cut at whole lines under <paramref name="tokens"/>. Never null:
    /// where nothing changed and nothing is new, the first line alone says so.
    /// </summary>
    /// <param name="view">The step's view, as its start's sync left it.</param>
    /// <param name="pack">The step's own context pack, made at its start.</param>
    /// <param name="told">
    /// The codebase parts the conversation holds, in the order it was given them: its pack, then each delta after it;
    /// empty where it holds none.
    /// </param>
    /// <param name="tokens">The step's own cap, within which the delta is cut at whole lines.</param>
    public static string Make(CodebaseGraphQueries view, ContextPack pack, IReadOnlyList<string> told, int tokens)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(told);

        var known = Known(told);
        var own = pack.Lines.Where(line => line.Id is not null).Select(line => StoodOf(line.Text)).OfType<Stood>().ToList();

        var moved = new List<string>();
        var removed = new List<string>();
        var added = new List<string>();

        foreach (var file in known.Values.GroupBy(node => node.Place.Path, StringComparer.Ordinal))
        {
            // The file's declarations as the view holds them now; null where the view no longer holds the file.
            var now = view.OutlineFile(file.Key);

            foreach (var was in file)
            {
                if (now is { } cards && cards.FirstOrDefault(card => card.Handle == was.Place.Handle) is { } stands)
                {
                    // It stands where it stood, as it stood: told already, told again by nothing.
                    if (stands.Start == was.Place.Start && stands.End == was.Place.End && stands.Hash == was.Place.Hash)
                    {
                        continue;
                    }

                    // Moved or changed: its line as the view holds it now, the new range and hash in it.
                    moved.Add(GraphLines.Of(stands));
                }
                else
                {
                    removed.Add(was.Line);
                }
            }
        }

        foreach (var node in own)
        {
            if (!known.ContainsKey(node.Key))
            {
                added.Add(node.Line);
            }
        }

        var lines = new List<string>
        {
            $"{known.Count} declaration{(known.Count == 1 ? string.Empty : "s")} the conversation held: {moved.Count} moved or changed, {removed.Count} removed; "
                + $"{added.Count} node{(added.Count == 1 ? string.Empty : "s")} of this step's pack added",
        };
        lines.AddRange(moved);
        lines.AddRange(removed.Select(line => RemovedPrefix + line));
        lines.AddRange(added);

        var text = string.Join('\n', lines);
        var leftOut = 0;

        while (EstimatedTokens.Of(text) > tokens && text.Contains('\n', StringComparison.Ordinal))
        {
            text = text[..text.LastIndexOf('\n')];
            leftOut++;
        }

        return leftOut > 0 ? $"{text}\n{leftOut} lines left out" : text;
    }

    /// <summary>
    /// The declarations a conversation holds, folded from every codebase part it was told, in order: a node line puts the
    /// declaration it names where the line says - a pack's naming it first, a delta's moving it - and a delta's
    /// <see cref="RemovedPrefix"/> line takes it out. Every other line - a pack's first and last, a declaration a pack
    /// says was taken out, a delta's first, its count of lines left out - names none. In the order each was first told.
    /// </summary>
    /// <param name="told">The codebase parts, in the order the conversation was given them.</param>
    private static OrderedDictionary<string, Stood> Known(IReadOnlyList<string> told)
    {
        var known = new OrderedDictionary<string, Stood>(StringComparer.Ordinal);

        foreach (var line in told.SelectMany(part => part.Split('\n')))
        {
            if (line.StartsWith(RemovedPrefix, StringComparison.Ordinal))
            {
                if (StoodOf(line[RemovedPrefix.Length..]) is { } gone)
                {
                    known.Remove(gone.Key);
                }

                continue;
            }

            if (StoodOf(line) is { } stood)
            {
                known[stood.Key] = stood;
            }
        }

        return known;
    }

    /// <summary>One line read as a declaration's place, where it is a node's line; null where it is none.</summary>
    /// <param name="line">The line, whole.</param>
    private static Stood? StoodOf(string line) => GraphLines.Placed(line) is { } place ? new Stood(place, line) : null;
}
