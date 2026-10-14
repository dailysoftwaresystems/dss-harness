using System.Text;
using RepoHarness.Core.Output;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Git;

/// <content>
/// What a three-way merge of one path asks of git: each version as git would store it, the merge itself, the result as a
/// work tree would hold it, and which versions of a path a history held. None of it writes a repository - not its index,
/// and not its objects.
/// </content>
public sealed partial class GitClient
{
    /// <summary>The length of a conflict's markers where no line of the three versions could be read as one.</summary>
    private const int DefaultMarkerSize = 7;

    /// <summary>
    /// The characters a conflict's markers are made of. A line of a version that begins with a run of one of them as
    /// long as the markers would read as a marker, so the markers are made one longer than the longest such run.
    /// </summary>
    private const string MarkerCharacters = "<=>|";

    /// <summary>What the three versions of a merge are called in the directory git merges them in.</summary>
    private const string OursFile = "ours";

    private const string AncestorFile = "ancestor";

    private const string TheirsFile = "theirs";

    /// <summary>The file a directory of a work tree declares git's attributes for the paths below it in.</summary>
    private const string AttributesFile = ".gitattributes";

    /// <summary>
    /// Paths named in one git invocation that takes them as arguments. Batched because a command line is bounded
    /// -- 32,767 characters on Windows -- and a repository has more files than that allows.
    /// </summary>
    private const int CommandLineBatchSize = 200;

    /// <summary>
    /// Characters of path a single batch may carry, well inside the bound above, so a tree of long paths is batched by
    /// what it actually costs rather than by a count that assumes short names.
    /// </summary>
    private const int CommandLineBatchCharacters = 8000;

    public async Task<IReadOnlyDictionary<string, GitEntry>> ListEntriesAtAsync(
        string directory,
        string commit,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);
        ArgumentNullException.ThrowIfNull(paths);

        var entries = new Dictionary<string, GitEntry>(StringComparer.Ordinal);
        var asked = paths.ToHashSet(StringComparer.Ordinal);

        // Not recursive: a path names its own entry, a file's or a directory's, and nothing below it. And a depth at a
        // time: asked for a directory and for a path below it at once, git lists what is below and leaves the directory's
        // own entry out, and of paths equally deep none is below another.
        foreach (var batch in asked
            .GroupBy(path => path.Count(character => character == '/'))
            .SelectMany(depth => CommandLineBatches([.. depth.Order(StringComparer.Ordinal)])))
        {
            var result = await RunForBytesAsync(directory, ["--literal-pathspecs", "ls-tree", "-z", "--full-tree", commit, "--", .. batch], cancellationToken)
                .ConfigureAwait(false);

            Ensure(result, $"list what {commit} holds at {ReportText.Counted(batch.Count, "path")}");

            foreach (var entry in TreeEntries(result.StandardOutput).Where(entry => entry.Name.IsUtf8 && asked.Contains(entry.Name.Text)))
            {
                entries[entry.Name.Text] = new GitEntry(entry.Mode, entry.Type, entry.ObjectId);
            }
        }

        return entries;
    }

    public async Task<GitPathHistory> ListHeldInHistoryAsync(
        string directory,
        string tip,
        IReadOnlyList<string> paths,
        int most,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tip);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentOutOfRangeException.ThrowIfLessThan(most, 1);

        if (tip.StartsWith('-') || HoldsLineBreak(tip))
        {
            throw new ArgumentException($"'{tip}' names no commit a history can be read from.", nameof(tip));
        }

        var asked = paths.Distinct(StringComparer.Ordinal).ToList();
        var held = new Dictionary<string, IReadOnlyList<GitHeldAt>>(StringComparer.Ordinal);
        var commits = 0;
        var cut = false;

        // A path holding a line break can travel neither as a line of the walk's input nor as one of the batch's, so
        // such paths are asked about apart, each commit's listing read for them.
        foreach (var group in asked.GroupBy(HoldsLineBreak).Select(group => (Apart: group.Key, Paths: group.ToList())))
        {
            var walked = await WalkAsync(directory, tip, group.Paths, group.Apart, most, cancellationToken).ConfigureAwait(false);
            var read = walked.Take(most).ToList();
            var blobs = group.Apart
                ? await BlobsApartAsync(directory, read, group.Paths, cancellationToken).ConfigureAwait(false)
                : await BlobsAsync(directory, read, group.Paths, cancellationToken).ConfigureAwait(false);

            foreach (var path in group.Paths)
            {
                held[path] = Versions(read, commit => blobs.GetValueOrDefault((commit, path)));
            }

            commits = Math.Max(commits, read.Count);
            cut |= walked.Count > most;
        }

        return new GitPathHistory(held, commits, cut);
    }

    /// <summary>
    /// The commits of the history of <paramref name="tip"/> that changed any of <paramref name="paths"/>, newest first:
    /// one more than <paramref name="most"/> at most, whose being there is what says the history goes further back.
    /// </summary>
    private async Task<IReadOnlyList<string>> WalkAsync(string directory, string tip, IReadOnlyList<string> paths, bool asArguments, int most, CancellationToken cancellationToken)
    {
        // --full-history, because a version a merge dropped was still that history's: simplified, the walk follows only
        // the side a merge took, and what the other side held at the path is never listed.
        string[] walk = ["--literal-pathspecs", "rev-list", "--full-history", $"--max-count={most + 1}"];

        // The paths travel on standard input, after the commit and the line that ends the commits, so no list of them
        // outgrows a command line; a path that cannot be a line of it is named as an argument instead.
        var result = asArguments
            ? await RunCoreAsync(directory, [.. walk, tip, "--", .. paths], echoOutput: false, untranslated: false, cancellationToken).ConfigureAwait(false)
            : await RunCoreAsync(
                    directory,
                    [.. walk, "--stdin"],
                    echoOutput: false,
                    untranslated: false,
                    indexFile: null,
                    $"{tip}\n--\n{string.Join('\n', paths)}\n",
                    cancellationToken)
                .ConfigureAwait(false);

        Ensure(result, $"read which commits of {tip} changed {ReportText.Counted(paths.Count, "path")}");

        return result.OutputLines;
    }

    /// <summary>The blob each of <paramref name="commits"/> holds at each of <paramref name="paths"/>, asked of one git process; a pair is in no answer where the commit holds no file there.</summary>
    private async Task<Dictionary<(string Commit, string Path), string>> BlobsAsync(string directory, IReadOnlyList<string> commits, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var blobs = new Dictionary<(string Commit, string Path), string>();
        var pairs = commits.SelectMany(commit => paths.Select(path => (Commit: commit, Path: path))).ToList();

        if (pairs.Count == 0)
        {
            return blobs;
        }

        var held = $"what the history held at {ReportText.Counted(paths.Count, "path")}";
        var objects = await ObjectsAtAsync(directory, pairs, ReportText.Counted(commits.Count, "commit"), held, held, cancellationToken)
            .ConfigureAwait(false);

        for (var at = 0; at < pairs.Count; at++)
        {
            if (objects[at] is ("blob", var id))
            {
                blobs[pairs[at]] = id;
            }
        }

        return blobs;
    }

    /// <summary>What <see cref="BlobsAsync"/> answers, for paths that cannot be a line of git's batch input: each commit asked on its own.</summary>
    private async Task<Dictionary<(string Commit, string Path), string>> BlobsApartAsync(string directory, IReadOnlyList<string> commits, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var blobs = new Dictionary<(string Commit, string Path), string>();

        foreach (var commit in commits)
        {
            foreach (var (path, at) in await HeldAtAsync(directory, commit, paths, cancellationToken).ConfigureAwait(false))
            {
                if (at.Blob is { } blob)
                {
                    blobs[(commit, path)] = blob;
                }
            }
        }

        return blobs;
    }

    /// <summary>
    /// The versions of one path among <paramref name="commits"/>, newest first: each run of commits, one after another,
    /// that hold the same blob there is one version, named by the newest of them, as far as was read.
    /// </summary>
    private static List<GitHeldAt> Versions(IReadOnlyList<string> commits, Func<string, string?> blobAt)
    {
        var versions = new List<GitHeldAt>();
        string? last = null;

        foreach (var commit in commits)
        {
            var blob = blobAt(commit);

            if (blob is not null && blob != last)
            {
                versions.Add(new GitHeldAt(commit, blob));
            }

            last = blob;
        }

        return versions;
    }

    public async Task<IReadOnlyList<string>> ListStagedIdsAsync(string directory, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (paths.Count == 0)
        {
            return [];
        }

        const string What = "read what the tree holds as git would store it";
        var real = await GetIndexFileAsync(directory, cancellationToken).ConfigureAwait(false);

        // Staged into a copy of the work tree's own index, as git add stages into it: each file whose entry and stat still
        // agree is the blob the entry holds, and any other is read again, its entry's line endings deciding its own.
        return await InIndexOfItsOwnAsync(async index =>
        {
            CopyIndex(real, index, What);

            return await StagedIdsAsync(directory, workTree: null, index, paths, store: null, What, cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Copies the index at <paramref name="real"/> to <paramref name="copy"/> with the time it was last written, which git
    /// compares each entry's own with: an entry written as late as its index could hide an edit made the same moment, and git
    /// reads such a file again rather than trust it - a copy timed now would trust it. An index never written leaves no copy,
    /// which git takes for one holding nothing.
    /// </summary>
    /// <remarks>
    /// Read while git may replace it: opened so a replacement is not refused, and read whole from what was opened.
    /// </remarks>
    /// <exception cref="HarnessException">The index could not be read, or its copy written.</exception>
    private static void CopyIndex(string real, string copy, string what)
    {
        try
        {
            DateTime written;

            using (var source = new FileStream(real, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                written = File.GetLastWriteTimeUtc(source.SafeFileHandle);

                using var target = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                source.CopyTo(target);
            }

            File.SetLastWriteTimeUtc(copy, written);
        }
        catch (FileNotFoundException)
        {
            // Nothing was ever staged there: git starts the copy empty, as it reads the index that is not there.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(HarnessExit.CommandFailed, $"Could not {what}: the index '{real}' could not be copied - {ex.Message}");
        }
    }

    public Task<IReadOnlyList<byte[]>> ReadStagedAsync(string directory, string path, IReadOnlyList<GitContent> contents, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        var what = $"read '{path}' as git would store it";

        return InScratchAsync<IReadOnlyList<byte[]>>(what, async scratch =>
        {
            var store = await StoreAsync(directory, scratch, cancellationToken).ConfigureAwait(false);
            var ids = new string?[contents.Count];
            var placed = new List<(int At, Func<string, Task> Write)>();

            for (var at = 0; at < contents.Count; at++)
            {
                switch (contents[at])
                {
                    case GitContent.Blob blob:
                        ids[at] = blob.Id;
                        break;
                    case GitContent.File file:
                        placed.Add((at, destination =>
                        {
                            File.Copy(file.Path, destination);

                            return Task.CompletedTask;
                        }));
                        break;
                    case GitContent.Bytes bytes:
                        placed.Add((at, destination => File.WriteAllBytesAsync(destination, bytes.Content, cancellationToken)));
                        break;
                }
            }

            if (placed.Count > 0)
            {
                // Written, since it is the object that is read back - into the store of this question's own, never the
                // repository's.
                var staged = await StagedAtAsync(directory, path, [.. placed.Select(content => content.Write)], scratch, store, what, cancellationToken)
                    .ConfigureAwait(false);

                for (var at = 0; at < placed.Count; at++)
                {
                    ids[placed[at].At] = staged[at];
                }
            }

            var read = await ReadBatchAsync(directory, [.. ids.Select(id => id!)], what, store, cancellationToken).ConfigureAwait(false);

            return [.. read.Select((answer, at) => answer is { Type: "blob" } blob
                ? Encoding.Latin1.GetBytes(blob.Content)
                : throw new HarnessException(HarnessExit.CommandFailed, $"Could not {what}: git holds no blob '{ids[at]}'."))];
        });
    }

    /// <summary>
    /// The blob <c>git add</c> would record for each content <paramref name="contents"/> writes, were it the file at
    /// <paramref name="path"/> in the work tree at <paramref name="directory"/>, in the same order, each written into
    /// <paramref name="store"/>: each placed at that path in a tree of its own under <paramref name="scratch"/> and staged
    /// there (<see cref="StagedIdsAsync"/>), into an index holding what the work tree's own holds at the path and at each
    /// attribute file above it, beside a copy of each such attribute file the work tree holds.
    /// </summary>
    /// <remarks>
    /// git then reads what it reads in the work tree: the entry whose carriage returns decide whether the file's are kept,
    /// and each attribute file the work tree holds or, where it holds none, the index's, with the repository's own
    /// configuration and attributes. One that is the path itself is the content placed there, as it would be in the work
    /// tree. A link the work tree holds in an attribute file's place is not copied, as git does not read it. A clean filter
    /// runs in the tree of the question's own, where in the work tree it would run at its root.
    /// </remarks>
    /// <exception cref="HarnessException">git could not stage one - a filter of the path failed, for one.</exception>
    /// <exception cref="IOException">A file could not be read, or a tree written.</exception>
    private async Task<IReadOnlyList<string>> StagedAtAsync(
        string directory,
        string path,
        IReadOnlyList<Func<string, Task>> contents,
        string scratch,
        ObjectStore store,
        string what,
        CancellationToken cancellationToken)
    {
        // The attribute file of each directory above the path, from the root down.
        var directories = path.Split('/')[..^1];
        var attributes = Enumerable.Range(0, directories.Length + 1)
            .Select(depth => string.Join('/', [.. directories.Take(depth), AttributesFile]))
            .Where(file => file != path)
            .ToList();

        var entries = await ListIndexAsync(directory, indexFile: null, [path, .. attributes], cancellationToken).ConfigureAwait(false);
        var template = Path.Combine(scratch, "index");

        if (entries.Count > 0)
        {
            var written = await RunWithIndexAsync(
                    directory,
                    template,
                    ["update-index", "--no-split-index", "-z", "--index-info"],
                    string.Concat(entries.Select(entry => $"{entry.Mode} {entry.ObjectId} {entry.Stage}\t{entry.Path}\0")),
                    cancellationToken)
                .ConfigureAwait(false);

            Ensure(written, what);
        }

        var held = attributes.Where(file => new FileInfo(Path.Combine(directory, file)) is { Exists: true, LinkTarget: null }).ToList();
        var ids = new List<string>();

        for (var at = 0; at < contents.Count; at++)
        {
            var tree = Path.Combine(scratch, $"tree-{at}");
            var index = Path.Combine(scratch, $"index-{at}");

            foreach (var file in held)
            {
                File.Copy(Path.Combine(directory, file), Placed(tree, file));
            }

            await contents[at](Placed(tree, path)).ConfigureAwait(false);

            if (entries.Count > 0)
            {
                File.Copy(template, index);
            }

            ids.Add((await StagedIdsAsync(directory, tree, index, [path], store, what, cancellationToken).ConfigureAwait(false))[0]);
        }

        return ids;

        // Where a file goes at a path in a tree, its directory made.
        static string Placed(string tree, string path)
        {
            var file = Path.Combine(tree, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);

            return file;
        }
    }

    public Task<byte[]> ReadCheckedOutAsync(string directory, string path, byte[] staged, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(staged);

        var what = $"read '{path}' as git would check it out";

        return InScratchAsync(what, async scratch =>
        {
            var store = await StoreAsync(directory, scratch, cancellationToken).ConfigureAwait(false);
            var held = Path.Combine(scratch, "staged");
            await File.WriteAllBytesAsync(held, staged, cancellationToken).ConfigureAwait(false);

            // Stored as the bytes stand - they are the staged form already - and then read back as a checkout of the
            // path would write them.
            var id = (await HashAsync(directory, [held], store, what, cancellationToken).ConfigureAwait(false))[0];
            var result = await RunForBytesAsync(directory, ["cat-file", "--filters", $"--path={path}", id], cancellationToken, objects: store).ConfigureAwait(false);

            Ensure(result, what);

            return Encoding.Latin1.GetBytes(result.StandardOutput);
        });
    }

    public Task<GitTextMerge> MergeTextAsync(byte[] ancestor, byte[] ours, byte[] theirs, GitMergeNames names, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ancestor);
        ArgumentNullException.ThrowIfNull(ours);
        ArgumentNullException.ThrowIfNull(theirs);
        ArgumentNullException.ThrowIfNull(names);

        return InScratchAsync("merge three versions of a file", async scratch =>
        {
            await File.WriteAllBytesAsync(Path.Combine(scratch, AncestorFile), ancestor, cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(scratch, OursFile), ours, cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(scratch, TheirsFile), theirs, cancellationToken).ConfigureAwait(false);

            // Run where the three are, outside any repository, and told how a conflict is written: --diff3, so no
            // configuration changes what is shown, and markers no line of the three could be taken for.
            var size = MarkerSize(ancestor, ours, theirs);
            var merged = await RunForBytesAsync(
                    scratch,
                    ["merge-file", "-p", "--diff3", $"--marker-size={size}", "-L", names.Ours, "-L", names.Ancestor, "-L", names.Theirs, OursFile, AncestorFile, TheirsFile],
                    cancellationToken)
                .ConfigureAwait(false);

            // git's exit code is the answer, read as it is and never as success or failure: 0 is a clean merge, whose
            // output is the merged bytes; 1 to 127 is that many conflicts, 127 standing for as many or more; anything
            // else is no merge at all - a binary file, for one - said in git's own words.
            if (merged is { TimedOut: false, ExitCode: 0 })
            {
                return GitTextMerge.Clean(
                    Encoding.Latin1.GetBytes(merged.StandardOutput),
                    await HunksAsync(scratch, OursFile, cancellationToken).ConfigureAwait(false),
                    await HunksAsync(scratch, TheirsFile, cancellationToken).ConfigureAwait(false));
            }

            return merged is { TimedOut: false, ExitCode: >= 1 and <= GitTextMerge.MostConflictsCounted }
                ? GitTextMerge.Conflicting(merged.ExitCode, ConflictsShown(Encoding.Latin1.GetBytes(merged.StandardOutput), size))
                : GitTextMerge.Unmerged(merged.FailureMessage);
        });
    }

    public async Task<IReadOnlyList<byte[]?>> ReadBlobsAsync(string directory, IReadOnlyList<string> objects, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objects);

        var read = await ReadBatchAsync(directory, objects, $"read {ReportText.Counted(objects.Count, "object")}", store: null, cancellationToken).ConfigureAwait(false);

        return [.. read.Select(answer => answer is { Type: "blob" } blob ? Encoding.Latin1.GetBytes(blob.Content) : null)];
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> ReadAttributesAsync(
        string directory,
        IReadOnlyList<string> attributes,
        IReadOnlyList<string> paths,
        string? unread = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(paths);

        var declared = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        if (attributes.Count > 0)
        {
            foreach (var batch in CommandLineBatches(paths))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result = await RunForBytesAsync(directory, ["check-attr", "-z", .. attributes, "--", .. batch], cancellationToken)
                    .ConfigureAwait(false);

                if (!result.Succeeded)
                {
                    throw new HarnessException(
                        HarnessExit.CommandFailed,
                        $"git check-attr failed, so {unread ?? $"what git's attributes declare for {ReportText.Counted(batch.Count, "path")} could not be read"}: {result.FailureMessage}");
                }

                // NUL-separated triples of path, attribute and value. A path carrying a space or a quote
                // survives this where git's default quoting would have to be undone first. Read as bytes and
                // decoded as git's names are, so a path that begins as a byte order mark does keeps it, and
                // what is declared for it is filed under the path asked.
                var fields = result.StandardOutput.Split('\0');

                for (var index = 0; index + 2 < fields.Length; index += 3)
                {
                    var path = GitName.FromBytes(fields[index]).Text;

                    if (!declared.TryGetValue(path, out var ofPath))
                    {
                        declared[path] = ofPath = new Dictionary<string, string>(StringComparer.Ordinal);
                    }

                    ofPath[GitName.FromBytes(fields[index + 1]).Text] = GitName.FromBytes(fields[index + 2]).Text;
                }
            }
        }

        return declared.ToDictionary(pair => pair.Key, pair => (IReadOnlyDictionary<string, string>)pair.Value, StringComparer.Ordinal);
    }

    /// <summary>Groups paths into invocations neither too many nor too long for one command line.</summary>
    private static IEnumerable<List<string>> CommandLineBatches(IReadOnlyList<string> paths)
    {
        var batch = new List<string>();
        var characters = 0;

        foreach (var path in paths)
        {
            if (batch.Count > 0 && (batch.Count >= CommandLineBatchSize || characters + path.Length > CommandLineBatchCharacters))
            {
                yield return batch;
                batch = [];
                characters = 0;
            }

            batch.Add(path);
            characters += path.Length + 1;
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    /// <summary>
    /// How long a conflict's markers are made for these three versions: <see cref="DefaultMarkerSize"/>, or one more than
    /// the longest run of a marker's character that begins a line of any of them, so that a file holding such a line
    /// cannot have it taken for a conflict.
    /// </summary>
    private static int MarkerSize(params byte[][] versions)
    {
        var longest = 0;

        foreach (var version in versions)
        {
            for (var at = 0; at < version.Length; at++)
            {
                if ((at == 0 || version[at - 1] == (byte)'\n') && MarkerCharacters.Contains((char)version[at], StringComparison.Ordinal))
                {
                    var run = 1;

                    while (at + run < version.Length && version[at + run] == version[at])
                    {
                        run++;
                    }

                    longest = Math.Max(longest, run);
                    at += run - 1;
                }
            }
        }

        return Math.Max(DefaultMarkerSize, longest + 1);
    }

    /// <summary>
    /// The conflicts of a merge's output, each from its opening marker to its closing one, a line to an element: the
    /// first <see cref="GitTextMerge.MostConflictsShown"/>, <see cref="GitTextMerge.MostLinesShown"/> lines of each,
    /// with a line saying how much more there is. A marker is a line that begins with as many of its character as the
    /// merge was told to write, which no line of the three versions does.
    /// </summary>
    private static List<string> ConflictsShown(byte[] output, int markerSize)
    {
        var opening = new string('<', markerSize);
        var closing = new string('>', markerSize);
        var shown = new List<string>();
        var conflicts = 0;
        var lines = 0;
        var inside = false;

        foreach (var line in Encoding.UTF8.GetString(output).Split('\n').Select(line => line.TrimEnd('\r')))
        {
            if (!inside && line.StartsWith(opening, StringComparison.Ordinal))
            {
                inside = true;
                conflicts++;
                lines = 0;
            }

            if (!inside)
            {
                continue;
            }

            lines++;

            if (conflicts <= GitTextMerge.MostConflictsShown && lines <= GitTextMerge.MostLinesShown)
            {
                shown.Add(Shown(line));
            }

            if (line.StartsWith(closing, StringComparison.Ordinal))
            {
                if (conflicts <= GitTextMerge.MostConflictsShown && lines > GitTextMerge.MostLinesShown)
                {
                    shown.Add($"({ReportText.Counted(lines - GitTextMerge.MostLinesShown, "more line")} of this conflict, not shown)");
                }

                inside = false;
            }
        }

        if (conflicts > GitTextMerge.MostConflictsShown)
        {
            shown.Add($"({ReportText.Counted(conflicts - GitTextMerge.MostConflictsShown, "more conflict")}, not shown)");
        }

        return shown;
    }

    /// <summary>
    /// A line of a file as a report shows it: as it is, a tab included, which breaks no line - and quoted and escaped
    /// (<see cref="ReportText.Printable"/>) where it holds any other control character, which could.
    /// </summary>
    private static string Shown(string line)
        => line.Any(character => char.IsControl(character) && character != '\t') ? ReportText.Printable(line) : line;

    /// <summary>
    /// How many hunks <paramref name="side"/> changed from what both held, as <c>git diff</c> counts them between the two
    /// files in <paramref name="scratch"/>: with no context, so each run of changed lines is a hunk of its own.
    /// </summary>
    private async Task<int> HunksAsync(string scratch, string side, CancellationToken cancellationToken)
    {
        var result = await RunForBytesAsync(
                scratch,
                ["diff", "--no-index", "--no-color", "--no-ext-diff", "--no-textconv", "-U0", "--", AncestorFile, side],
                cancellationToken)
            .ConfigureAwait(false);

        // 1 is git's answer that the two differ, as 0 is that they do not: neither is a failure.
        if (result.TimedOut || result.ExitCode is not (0 or 1))
        {
            throw new HarnessException(HarnessExit.CommandFailed, $"Could not count the hunks of a merge: {result.FailureMessage}");
        }

        return result.StandardOutput.Split('\n').Count(line => line.StartsWith("@@", StringComparison.Ordinal));
    }

    /// <summary>
    /// An object directory of this question's own under <paramref name="scratch"/>, beside the objects of the repository
    /// at <paramref name="directory"/>: what git writes for the question goes there and is discarded with it, and what
    /// the repository holds is still read.
    /// </summary>
    private async Task<ObjectStore> StoreAsync(string directory, string scratch, CancellationToken cancellationToken)
    {
        var own = Path.Combine(scratch, "objects");
        Directory.CreateDirectory(own);

        return new ObjectStore(own, await GitPathAsync(directory, "objects", "find where the repository keeps its objects", cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Answers a question that hands git files of its own, in a directory of its own under the system's temporary
    /// directory, discarded once it is answered: one that cannot be is left for the system to clear, as an index of a
    /// question's own is, and nothing is raised for it.
    /// </summary>
    /// <param name="what">What was being done, for the failure where the directory cannot be written.</param>
    /// <param name="ask">Asks, given the directory.</param>
    /// <exception cref="HarnessException">The directory, or a file in it, could not be written.</exception>
    private static async Task<T> InScratchAsync<T>(string what, Func<string, Task<T>> ask)
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"dssharness-git-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(scratch);

            return await ask(scratch).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(HarnessExit.CommandFailed, $"Could not {what}: what git was to be handed could not be written under '{scratch}' - {ex.Message}");
        }
        finally
        {
            _ = DiscardDirectory(scratch);
        }
    }

    /// <summary>
    /// An object directory of one question's own, and the repository's beside it: git writes into the first, so nothing
    /// it stores for the question ever reaches the repository, and still reads every object the repository holds.
    /// </summary>
    /// <param name="Directory">Where git writes objects: a directory of the question's own.</param>
    /// <param name="Alternate">The repository's own object directory, read and never written.</param>
    private sealed record ObjectStore(string Directory, string Alternate)
    {
        /// <summary>
        /// <see cref="Alternate"/> as git's list of alternate object directories takes one entry: in double quotes, with
        /// a backslash and a quote escaped, so a path holding the character that parts the list's entries - a colon, a
        /// semicolon on Windows - is still one entry.
        /// </summary>
        public string AlternateList => $"\"{Alternate.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }
}
