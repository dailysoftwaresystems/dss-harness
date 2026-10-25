using System.Text.Json.Nodes;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Output;

namespace DssHarness.Core.Agents.Runtime;

/// <summary>
/// The tools that write: a file created, a string replaced, lines inserted, a directory made, a file or a directory
/// deleted, a path moved. Each is a write to every path it touches - each directory it makes, each entry it deletes or
/// moves, at both ends of a move - held to the writable roots and asked of the policy.
/// </summary>
public sealed partial class AgentToolRuntime
{
    /// <summary><c>create_file</c>: a new file holding the content as given, in UTF-8 with no mark, the directories on its way made.</summary>
    private async Task<Answer> CreateAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var path = Text(input, "path")!;
        var place = WritePlace(path);

        if (place.Denied is { } denied)
        {
            return Answer.Refused(path, denied);
        }

        var full = place.Full!;
        var files = _host.FileSystem;

        switch (files.KindOf(full))
        {
            case PathKind.Directory:
                return Answer.Failed($"'{Said(full)}' is a directory");
            case PathKind.File when !Flag(input, "overwrite"):
                return Answer.Failed($"'{Said(full)}' is there already: overwrite: true writes over it");
        }

        // The directories on its way are made by the write itself.
        var bytes = Utf8Text.Encode(Text(input, "content")!);
        await files.WriteAllBytesAtomicAsync(full, bytes, cancellationToken).ConfigureAwait(false);

        // The agent wrote what it holds, and knows it as well as one it viewed.
        _viewed[full] = Hash(bytes);
        Tally.Wrote(full);

        return Answer.Done($"wrote '{Said(full)}' ({DiskSpace.Size(bytes.Length)})");
    }

    /// <summary><c>replace_in_file</c>: a string replaced in a file viewed since it last changed, its encoding and its endings kept.</summary>
    private async Task<Answer> ReplaceAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var old = Text(input, "old_string")!;
        var replacement = Text(input, "new_string")!;

        if (old.Length == 0)
        {
            return Answer.Failed("old_string is empty: give the text to replace, as the file holds it");
        }

        if (old == replacement)
        {
            return Answer.Failed("old_string and new_string are the same: nothing would change");
        }

        return await EditAsync(input, edited =>
        {
            var count = edited.Count(old);

            if (count == 0)
            {
                return "old_string is not in the file: view it again, and give the text exactly as it stands";
            }

            if (count > 1 && !Flag(input, "replace_all"))
            {
                return $"old_string stands {Count(count)} times in the file: give more of the text around it so it stands once, or replace_all: true";
            }

            edited.Replace(old, replacement);
            return null;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>insert_in_file</c>: lines inserted before a line of a file viewed since it last changed, its encoding and its endings kept.</summary>
    private async Task<Answer> InsertAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var line = Number(input, "line")!.Value;
        var text = Text(input, "text")!;

        return await EditAsync(input, edited =>
        {
            if (line < 1 || line > edited.LineCount + 1)
            {
                return $"line is {line}, and the file has {ReportText.Counted(edited.LineCount, "line")}: give 1 to {Count(edited.LineCount + 1)}, one past the last to append";
            }

            edited.Insert((int)line, text);
            return null;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// An edit of the file <c>path</c> names: decided as a write, refused where the file was not viewed or changed since,
    /// read in its encoding, changed by <paramref name="edit"/> - which says why it would not, where it would not - and
    /// written back whole, in one write.
    /// </summary>
    private async Task<Answer> EditAsync(JsonObject input, Func<EditedText, string?> edit, CancellationToken cancellationToken)
    {
        var path = Text(input, "path")!;
        var place = WritePlace(path);

        if (place.Denied is { } denied)
        {
            return Answer.Refused(path, denied);
        }

        var full = place.Full!;
        var files = _host.FileSystem;

        if (!files.FileExists(full))
        {
            return Answer.Failed($"there is no file at '{Said(full)}'");
        }

        if (TooLarge(full) is { } large)
        {
            return Answer.Failed(large);
        }

        var bytes = await files.ReadAllBytesAsync(full, cancellationToken).ConfigureAwait(false);

        if (!_viewed.TryGetValue(full, out var seen))
        {
            return Answer.Failed(NeverViewed);
        }

        if (seen != Hash(bytes))
        {
            return Answer.Failed(ChangedSinceViewed);
        }

        var reading = EncodedText.Read(bytes);

        if (!reading.IsRead)
        {
            return Answer.Failed($"'{Said(full)}' is {(reading.Binary ? "binary" : "no text this tool reads")}: {reading.Why}");
        }

        var edited = EditedText.Of(reading.Text);

        if (edit(edited) is { } why)
        {
            return Answer.Failed(why);
        }

        var written = edited.Bytes();
        await files.WriteAllBytesAtomicAsync(full, written, cancellationToken).ConfigureAwait(false);

        _viewed[full] = Hash(written);
        Tally.Wrote(full);

        return Answer.Done($"edited '{Said(full)}': {ReportText.Counted(edited.LineCount, "line")} now");
    }

    /// <summary><c>create_directory</c>: a directory made, with every one on its way; one that is there is no error.</summary>
    private Answer CreateDirectory(JsonObject input)
    {
        var path = Text(input, "path")!;
        var place = WritePlace(path);

        if (place.Denied is { } denied)
        {
            return Answer.Refused(path, denied);
        }

        var full = place.Full!;

        switch (_host.FileSystem.KindOf(full))
        {
            case PathKind.Directory:
                return Answer.Done($"'{Said(full)}' is there already");
            case PathKind.File:
                return Answer.Failed($"'{Said(full)}' is a file");
        }

        _host.FileSystem.CreateDirectory(full);

        return Answer.Done($"made '{Said(full)}'");
    }

    /// <summary><c>delete_file</c>: one file deleted; one that is not there is an error.</summary>
    private Answer DeleteFile(JsonObject input)
    {
        var path = Text(input, "path")!;
        var place = WritePlace(path);

        if (place.Denied is { } denied)
        {
            return Answer.Refused(path, denied);
        }

        var full = place.Full!;

        switch (_host.FileSystem.KindOf(full))
        {
            case PathKind.None:
                return Answer.Failed($"there is no file at '{Said(full)}'");
            case PathKind.Directory:
                return Answer.Failed($"'{Said(full)}' is a directory: delete_directory deletes one");
        }

        _host.FileSystem.DeleteFile(full);
        _viewed.TryRemove(full, out _);
        Tally.Wrote(full);

        return Answer.Done($"deleted '{Said(full)}'");
    }

    /// <summary>
    /// <c>delete_directory</c>: an empty directory deleted, or with <c>recursive</c> everything in it - refused for a root,
    /// for one that holds a link, that holds a path the agent may not write, or that holds more entries than the bound.
    /// </summary>
    private Answer DeleteDirectory(JsonObject input)
    {
        var path = Text(input, "path")!;
        var place = WritePlace(path);

        if (place.Denied is { } denied)
        {
            return Answer.Refused(path, denied);
        }

        var full = place.Full!;

        switch (_host.FileSystem.KindOf(full))
        {
            case PathKind.None:
                return Answer.Failed($"there is no directory at '{Said(full)}'");
            case PathKind.File:
                return Answer.Failed($"'{Said(full)}' is a file: delete_file deletes one");
        }

        if (_paths.IsRoot(full))
        {
            return Answer.Refused(path, new AgentPolicyAnswer.Denied(
                ConfinedPaths.RootsRule,
                AgentPolicySource.Builtin,
                $"'{full}' is one of this agent's roots, which no tool deletes"));
        }

        var entries = Held(full);

        if (entries.Refusal is { } refusal)
        {
            return Answer.Refused(path, refusal);
        }

        if (entries.Entries.Count > 0 && !Flag(input, "recursive"))
        {
            return Answer.Failed($"'{Said(full)}' holds {ReportText.Counted(entries.Entries.Count, "entry")}: recursive: true deletes them with it");
        }

        try
        {
            _host.FileSystem.DeleteDirectory(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A delete stops at what it cannot remove, the rest gone before or after it: what is gone is a write all the same.
            var gone = entries.Files.Where(file => !_host.FileSystem.FileExists(file)).ToList();
            Deleted(full, gone);

            return Answer.Failed($"deleting '{Said(full)}' stopped part way, {gone.Count} of its {ReportText.Counted(entries.Files.Count, "file")} gone: {ex.Message.TrimEnd('.')}");
        }

        Deleted(full, entries.Files);

        return Answer.Done($"deleted '{Said(full)}' and the {ReportText.Counted(entries.Entries.Count, "entry")} it held");
    }

    /// <summary>The files a delete under <paramref name="directory"/> removed: written, and no longer known as they were viewed.</summary>
    private void Deleted(string directory, IReadOnlyCollection<string> files)
    {
        foreach (var file in files)
        {
            _viewed.TryRemove(file, out _);
        }

        if (files.Count > 0)
        {
            Tally.Wrote(directory, files.Count);
        }
    }

    /// <summary>
    /// <c>move_path</c>: a file or a directory moved inside the writable roots - every path it touches at both ends a
    /// write - the directories on the way made; a file at the end replaced only with <c>overwrite</c>, a directory never.
    /// </summary>
    private Answer MovePath(JsonObject input)
    {
        var fromPath = Text(input, "from")!;
        var toPath = Text(input, "to")!;
        var from = WritePlace(fromPath);

        if (from.Denied is { } fromDenied)
        {
            return Answer.Refused(fromPath, fromDenied);
        }

        var to = WritePlace(toPath);

        if (to.Denied is { } toDenied)
        {
            return Answer.Refused(toPath, toDenied);
        }

        var source = from.Full!;
        var destination = to.Full!;
        var files = _host.FileSystem;
        var kind = files.KindOf(source);

        // Names compare as the file system the source stands in compares them, which is not always its platform's way.
        var comparison = _paths.ComparisonIn(Path.GetDirectoryName(source) ?? source);

        if (kind == PathKind.None)
        {
            return Answer.Failed($"there is nothing at '{Said(source)}' to move");
        }

        // A name changed in its case alone, in a directory that finds a name in any case, is the one entry renamed: it is
        // what stands at its end. A directory above it is renamed by no move of what it holds.
        var renamed = !string.Equals(source, destination, StringComparison.Ordinal)
            && string.Equals(Path.GetDirectoryName(source), Path.GetDirectoryName(destination), StringComparison.Ordinal)
            && PathContainment.AreSame(source, destination, comparison);

        if (!renamed && PathContainment.IsInsideOrSame(source, destination, comparison))
        {
            return Answer.Failed($"'{Said(destination)}' is '{Said(source)}' or inside it: nothing moves into itself");
        }

        switch (renamed ? PathKind.None : files.KindOf(destination))
        {
            case PathKind.Directory:
                return Answer.Failed($"a directory is at '{Said(destination)}', and a move never replaces one");
            case PathKind.File when kind == PathKind.Directory:
                return Answer.Failed($"a file is at '{Said(destination)}', and a directory never replaces one");
            case PathKind.File when !Flag(input, "overwrite"):
                return Answer.Failed($"a file is at '{Said(destination)}': overwrite: true replaces it");
        }

        IReadOnlyList<string> moved = [source];

        if (kind == PathKind.Directory)
        {
            if (_paths.IsRoot(source))
            {
                return Answer.Refused(fromPath, new AgentPolicyAnswer.Denied(
                    ConfinedPaths.RootsRule,
                    AgentPolicySource.Builtin,
                    $"'{source}' is one of this agent's roots, which no tool moves"));
            }

            var held = Held(source);

            if (held.Refusal is { } refusal)
            {
                return Answer.Refused(fromPath, refusal);
            }

            // Every path beneath, at the end it lands at, is a write too.
            foreach (var entry in held.Entries)
            {
                var landed = Path.Combine(destination, Path.GetRelativePath(source, entry));

                if (_policy.Write(landed) is AgentPolicyAnswer.Denied landing)
                {
                    return Answer.Refused(toPath, landing with { Reason = $"'{Said(landed)}', where '{Said(entry)}' would land, is refused: {landing.Reason}" });
                }
            }

            moved = held.Files;
        }

        MakeTheWay(destination);

        if (kind == PathKind.Directory)
        {
            files.MoveDirectory(source, destination);
        }
        else
        {
            files.ReplaceFile(source, destination);
        }

        // A file moved is the file the agent viewed, at its new place: what it knew of it goes with it, and its old place,
        // and whatever stood where it landed, are known no more. Taken before anything is forgotten: a name renamed in its
        // case alone is its own destination where the views compare names without case.
        var carried = moved
            .Select(file => (Landed: kind == PathKind.Directory ? Path.Combine(destination, Path.GetRelativePath(source, file)) : destination, Seen: _viewed.TryRemove(file, out var seen) ? seen : null))
            .ToList();
        _viewed.TryRemove(destination, out _);

        foreach (var (landed, seen) in carried)
        {
            if (seen is not null)
            {
                _viewed[landed] = seen;
            }
        }

        Tally.Wrote(source, moved.Count);
        Tally.Wrote(destination, moved.Count);

        return Answer.Done($"moved '{Said(source)}' to '{Said(destination)}'");
    }

    /// <summary>
    /// Makes the directories missing on <paramref name="full"/>'s way, as part of the write to it that was decided: each
    /// lies inside the writable root the path does, through no link, and a rule that refuses a directory refuses what is
    /// written in it - while a step's <c>writes</c>, which names files, would refuse every directory its own files need.
    /// </summary>
    private void MakeTheWay(string full)
    {
        if (Path.GetDirectoryName(full) is { } directory && _host.FileSystem.KindOf(directory) == PathKind.None)
        {
            _host.FileSystem.CreateDirectory(directory);
        }
    }

    /// <summary>
    /// What <paramref name="directory"/> holds, walked whole without following a link: every entry and every file, or the
    /// refusal of the first that stops a tool acting on it - a link, a path the agent may not write, or more entries than
    /// <see cref="AgentTools.MostDeletedEntries"/>.
    /// </summary>
    private Contents Held(string directory)
    {
        var files = _host.FileSystem;
        var entries = new List<string>();
        var held = new List<string>();
        var waiting = new Stack<string>([directory]);

        while (waiting.TryPop(out var at))
        {
            foreach (var entry in files.EnumerateDirectories(at).Concat(files.EnumerateFiles(at, recursive: false)))
            {
                entries.Add(entry);

                // Past the bound nothing more is walked: a tree of a million entries is refused at its two thousand and first.
                if (entries.Count > AgentTools.MostDeletedEntries)
                {
                    return new Contents(entries, held, new AgentPolicyAnswer.Denied(
                        MostEntriesRule,
                        AgentPolicySource.Builtin,
                        $"'{directory}' holds more than {Count(AgentTools.MostDeletedEntries)} entries, the most one call of a tool acts on"));
                }

                switch (files.KindOf(entry))
                {
                    case PathKind.Link:
                        return new Contents(entries, held, new AgentPolicyAnswer.Denied(
                            ConfinedPaths.LinksRule,
                            AgentPolicySource.Builtin,
                            $"'{directory}' holds the link '{entry}': no tool acts on a directory that holds a link"));
                    case PathKind.Directory:
                        waiting.Push(entry);
                        break;
                    default:
                        held.Add(entry);
                        break;
                }

                if (_policy.Write(entry) is AgentPolicyAnswer.Denied denied)
                {
                    return new Contents(entries, held, denied with { Reason = $"'{directory}' holds '{entry}', which may not be written: {denied.Reason}" });
                }
            }
        }

        return new Contents(entries, held, null);
    }

    /// <summary>What a directory holds: every entry, every file, and the refusal of the first that stops a tool acting on it.</summary>
    private sealed record Contents(IReadOnlyList<string> Entries, IReadOnlyList<string> Files, AgentPolicyAnswer.Denied? Refusal);
}
