using System.Globalization;
using System.Text;
using DssHarness.Core.Output;
using DssHarness.Core.Results;

namespace DssHarness.Core.Git;

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

    /// <summary>The copy of ours the merge is written over.</summary>
    private const string MergedFile = "merged";

    /// <summary>The file a directory of a work tree declares git's attributes for the paths below it in.</summary>
    private const string AttributesFile = ".gitattributes";

    /// <summary>
    /// Paths named in one git invocation that takes them as arguments. Batched because a command line is bounded
    /// -- 32,767 characters on Windows -- and a repository has more files than that allows.
    /// </summary>
    internal const int CommandLineBatchSize = 200;

    /// <summary>
    /// How many bytes of blobs one batch reads into memory together (<see cref="SizeBatches"/>): a working size, and never
    /// a bound on what is read - a blob larger is read alone.
    /// </summary>
    internal const long ReadTogether = 16 * 1024 * 1024;

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
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tip);
        ArgumentNullException.ThrowIfNull(paths);

        if (tip.StartsWith('-') || HoldsLineBreak(tip))
        {
            throw new ArgumentException($"'{tip}' names no commit a history can be read from.", nameof(tip));
        }

        var asked = paths.Distinct(StringComparer.Ordinal).ToList();
        var held = new Dictionary<string, IReadOnlyList<GitHeldAt>>(StringComparer.Ordinal);
        var commits = 0;

        // A path holding a line break can travel neither as a line of the walk's input nor as one of the batch's, so
        // such paths are asked about apart, each commit's listing read for them.
        foreach (var group in asked.GroupBy(HoldsLineBreak).Select(group => (Apart: group.Key, Paths: group.ToList())))
        {
            var read = await WalkAsync(directory, tip, group.Paths, group.Apart, cancellationToken).ConfigureAwait(false);
            var blobs = group.Apart
                ? await BlobsApartAsync(directory, read, group.Paths, cancellationToken).ConfigureAwait(false)
                : await BlobsAsync(directory, read, group.Paths, cancellationToken).ConfigureAwait(false);

            foreach (var path in group.Paths)
            {
                held[path] = Versions(read, commit => blobs.GetValueOrDefault((commit, path)));
            }

            commits = Math.Max(commits, read.Count);
        }

        return new GitPathHistory(held, commits);
    }

    public async Task<IReadOnlyList<GitRename>> ListRenamesAsync(
        string directory,
        string tip,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tip);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (tip.StartsWith('-') || HoldsLineBreak(tip))
        {
            throw new ArgumentException($"'{tip}' names no commit a history can be read from.", nameof(tip));
        }

        // --follow is what walks one file across its renames, and only renames are listed: each commit's id, then the one
        // entry of the file followed - its status, the name it had and the name it was given. Colour, signatures and a
        // configured relative diff are each turned off, for any would change what is read here.
        var result = await RunForBytesAsync(
                directory,
                [
                    "--literal-pathspecs", "log", "--follow", "--find-renames", "--diff-filter=R", "--name-status", "-z",
                    "--no-color", "--no-show-signature", "--no-relative", "--format=%H", tip, "--", path,
                ],
                cancellationToken)
            .ConfigureAwait(false);

        Ensure(result, $"read the renames of '{path}' in {tip}");

        var records = Records(result.StandardOutput);
        var renames = new List<GitRename>();

        if (records.Length % 4 != 0)
        {
            throw Unreadable();
        }

        for (var at = 0; at < records.Length; at += 4)
        {
            var from = GitName.FromBytes(records[at + 2]);
            var to = GitName.FromBytes(records[at + 3]);

            if (records[at].Trim().Length == 0 || !records[at + 1].TrimStart('\n').StartsWith('R'))
            {
                throw Unreadable();
            }

            // A name that is not UTF-8, either side of the rename, cannot be asked for by name: the history before it ends
            // the list.
            if (!from.IsUtf8 || !to.IsUtf8)
            {
                break;
            }

            renames.Add(new GitRename(from.Text));
        }

        return renames;

        HarnessException Unreadable()
            => new(HarnessExit.CommandFailed, $"git answered the renames of '{path}' in {tip} in a shape this build cannot read.");
    }

    /// <summary>
    /// Every commit of the history of <paramref name="tip"/> that changed any of <paramref name="paths"/>, newest first, as
    /// git's own walk lists them: each commit once, however many merges reach it.
    /// </summary>
    private async Task<IReadOnlyList<string>> WalkAsync(string directory, string tip, IReadOnlyList<string> paths, bool asArguments, CancellationToken cancellationToken)
    {
        // --full-history, because a version a merge dropped was still that history's: simplified, the walk follows only
        // the side a merge took, and what the other side held at the path is never listed.
        string[] walk = ["--literal-pathspecs", "rev-list", "--full-history"];

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

        return paths.Count == 0
            ? []
            : await StoredIdsAsync(directory, paths, asPath: null, written: false, store: null, "read what the tree holds as git would store it", cancellationToken)
                .ConfigureAwait(false);
    }

    public Task<IReadOnlyList<byte[]>> ReadStagedAsync(string directory, string path, IReadOnlyList<GitContent> contents, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        var what = $"read '{path}' as git would store it";

        return InScratchAsync<IReadOnlyList<byte[]>>(what, async scratch =>
        {
            var store = await StoreAsync(directory, scratch, cancellationToken).ConfigureAwait(false);
            var ids = await StoredVersionsAsync(directory, path, contents, scratch, store, what, cancellationToken).ConfigureAwait(false);
            var read = await ReadBatchAsync(directory, ids, what, store, cancellationToken).ConfigureAwait(false);

            return [.. read.Select((answer, at) => answer is { Type: "blob" } blob
                ? Encoding.Latin1.GetBytes(blob.Content)
                : throw new HarnessException(HarnessExit.CommandFailed, $"Could not {what}: git holds no blob '{ids[at]}'."))];
        });
    }

    /// <summary>
    /// The blob <c>git add</c> would record for each of <paramref name="files"/>, in the same order, were it the file at its
    /// path in the work tree at <paramref name="directory"/> - its own, or <paramref name="asPath"/> for them all: the one
    /// place this client asks what git stores for a file.
    /// </summary>
    /// <remarks>
    /// Read by <c>git hash-object</c> run at the work tree's root, as <c>git add</c> reads a file there: the path's clean
    /// filters run from the root, its attributes and the repository's settings read as git reads them, the path the work
    /// tree's own. hash-object reads no index, where <c>git add</c> reads it twice in reading a file: for the rule that
    /// leaves the line endings of a file whose entry holds carriage returns as they are, and for an attribute file the work
    /// tree lacks. Both are read here and told to it (<see cref="StoringAsync"/>). Nothing is staged, so no index is written
    /// and no mark on an entry - core.ignoreStat's, assume-unchanged, skip-worktree - stands for the file's bytes.
    /// </remarks>
    /// <param name="directory">The work tree's root, where git runs.</param>
    /// <param name="files">The files to read: each a path in the work tree, or each a file kept elsewhere where <paramref name="asPath"/> is given.</param>
    /// <param name="asPath">The path whose rules apply to every file; each file's own where null.</param>
    /// <param name="written">Whether each blob is written, into <paramref name="store"/>.</param>
    /// <param name="store">An object directory of the question's own; one is made where a tree of attributes needs a place and none is given.</param>
    /// <param name="what">What was being done, for the failure.</param>
    /// <param name="cancellationToken">Cancels the git processes.</param>
    /// <exception cref="HarnessException">git could not read a file, a filter of its path failed, or the path's attributes cannot be told to git.</exception>
    private async Task<IReadOnlyList<string>> StoredIdsAsync(
        string directory,
        IReadOnlyList<string> files,
        string? asPath,
        bool written,
        ObjectStore? store,
        string what,
        CancellationToken cancellationToken)
    {
        var storing = await StoringAsync(directory, asPath is null ? files : [asPath], cancellationToken).ConfigureAwait(false);
        var ids = new string[files.Count];

        Storing Of(int at) => storing[asPath ?? files[at]];

        // Each way of reading asked once, for every file it reads.
        foreach (var group in Enumerable.Range(0, files.Count).GroupBy(at => (Of(at).Filtered, Of(at).Told, Declared: Of(at).Declared is not null)))
        {
            var at = group.ToList();
            IReadOnlyList<string> read = group.Key.Declared
                ? await DeclaredIdsAsync(
                        directory,
                        [.. at.Select(index => files[index])],
                        asPath,
                        at.Select(index => asPath ?? files[index]).Distinct(StringComparer.Ordinal).ToDictionary(path => path, path => storing[path].Declared!, StringComparer.Ordinal),
                        written,
                        store,
                        what,
                        cancellationToken)
                    .ConfigureAwait(false)
                : await HashAsync(directory, [.. at.Select(index => files[index])], new Hashing(written, group.Key.Filtered, asPath, store, group.Key.Told), what, cancellationToken)
                    .ConfigureAwait(false);

            for (var index = 0; index < at.Count; index++)
            {
                ids[at[index]] = read[index];
            }
        }

        return ids;
    }

    /// <summary>What <c>git hash-object</c> is told so it reads a file as <c>git add</c> would at its path.</summary>
    /// <param name="Filtered">Whether the path's clean filters and line-ending rules are applied; not where none of them would change the file.</param>
    /// <param name="Told">A setting given to git first, where one is.</param>
    /// <param name="Declared">The path's conversion attributes, declared whole in place of the work tree's, where they are.</param>
    private sealed record Storing(bool Filtered, IReadOnlyList<string>? Told = null, IReadOnlyDictionary<string, string>? Declared = null)
    {
        /// <summary>As the path's attributes and the repository's settings say: git's rule for an entry's carriage returns does not apply.</summary>
        public static readonly Storing AsAttributed = new(Filtered: true);

        /// <summary>With <c>core.autocrlf</c> off: it alone asked for the conversion git's rule leaves out, and no other rule of the path changes.</summary>
        public static readonly Storing WithoutAutocrlf = new(Filtered: true, ["-c", "core.autocrlf=false"]);

        /// <summary>As its bytes stand: a <c>text=auto</c> attribute asked for the conversion git's rule leaves out, and nothing else would change the file.</summary>
        public static readonly Storing AsItStands = new(Filtered: false);
    }

    /// <summary>The attributes git reads in converting a file it stores, each read here to tell it again.</summary>
    private static readonly string[] ConversionAttributes = ["crlf", "ident", "filter", "eol", "text", "working-tree-encoding"];

    /// <summary>
    /// How <c>git hash-object</c> reads each of <paramref name="paths"/> as <c>git add</c> would in the work tree at
    /// <paramref name="directory"/>: what hash-object, which reads no index, is told of the two things git add reads in it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// git's rule (convert.c, <c>crlf_to_git</c>): where a path's line endings are converted only as git sees fit - its
    /// attributes say <c>text=auto</c>, or say nothing and <c>core.autocrlf</c> is true or input - and the path's entry in the
    /// index is text holding a carriage return before a line feed, the conversion is left out, and every other conversion of
    /// the path - a clean filter, <c>working-tree-encoding</c>, <c>ident</c> - still applies. Whether the entry holds one and
    /// what the attributes say are git's own words, from <c>git ls-files --eol</c>: <c>i/crlf</c> or <c>i/mixed</c> is the
    /// test the rule makes of the entry, and <c>attr/</c> the attributes as read before <c>core.autocrlf</c> is weighed. Where
    /// the attributes say nothing, <c>core.autocrlf</c> off leaves out exactly that conversion. Where they say
    /// <c>text=auto</c>, no setting does: a file no other conversion applies to is read as its bytes stand, and any other with
    /// its attributes declared whole, <c>text</c> unset.
    /// </para>
    /// <para>
    /// The second: git reads an attribute file the work tree does not hold as a file from the index, which hash-object never
    /// reads. A path below one is read with its attributes declared whole as git resolves them there (<c>git check-attr</c>,
    /// which reads the index as git add does).
    /// </para>
    /// </remarks>
    /// <exception cref="HarnessException">git could not list the index's line endings or a path's attributes.</exception>
    private async Task<IReadOnlyDictionary<string, Storing>> StoringAsync(string directory, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var asked = paths.Distinct(StringComparer.Ordinal).ToList();
        var above = asked.ToDictionary(path => path, AttributeFilesAbove, StringComparer.Ordinal);
        var listed = await LineEndingsAsync(directory, [.. asked.Concat(above.Values.SelectMany(files => files)).Distinct(StringComparer.Ordinal)], cancellationToken)
            .ConfigureAwait(false);

        // git reads an attribute file from the work tree where it opens there as a file, a link not followed, and from the
        // index otherwise.
        bool FromIndex(string path) => above[path].Any(file => listed.ContainsKey(file) && new FileInfo(Path.Combine(directory, file)) is not { Exists: true, LinkTarget: null });

        bool Kept(string path) => listed.TryGetValue(path, out var held)
            && held.Index is "crlf" or "mixed"
            && (held.Attributes.Length == 0 || held.Attributes.StartsWith("text=auto", StringComparison.Ordinal));

        var weighed = asked.Where(path => FromIndex(path) || (Kept(path) && listed[path].Attributes.Length > 0)).ToList();
        var resolved = weighed.Count == 0
            ? new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
            : await ReadAttributesAsync(directory, ConversionAttributes, weighed, told: [], store: null, unread: null, cancellationToken).ConfigureAwait(false);

        return asked.ToDictionary(
            path => path,
            path => (FromIndex(path), Kept(path)) switch
            {
                (true, var kept) => Declaring(resolved[path], kept),
                (false, false) => Storing.AsAttributed,
                (false, true) when listed[path].Attributes.Length == 0 => Storing.WithoutAutocrlf,
                (false, true) when ConvertsOtherwise(resolved[path]) => Declaring(resolved[path], kept: true),
                (false, true) => Storing.AsItStands,
            },
            StringComparer.Ordinal);

        // A clean filter, an encoding or an ident keyword the path has: git converts the file beside its line endings.
        static bool ConvertsOtherwise(IReadOnlyDictionary<string, string> attributes)
            => attributes["filter"] is not (GitAttributes.Set or GitAttributes.Unset or GitAttributes.Unspecified)
                || attributes["ident"] == GitAttributes.Set
                || attributes["working-tree-encoding"] is not (GitAttributes.Set or GitAttributes.Unset or GitAttributes.Unspecified);

        // The attributes as git resolves them, text unset where its rule leaves the line endings as they are.
        static Storing Declaring(IReadOnlyDictionary<string, string> attributes, bool kept)
            => new(Filtered: true, Declared: ConversionAttributes.ToDictionary(
                name => name,
                name => kept && name == "text" ? GitAttributes.Unset : attributes[name],
                StringComparer.Ordinal));
    }

    /// <summary>The attribute file of each directory above <paramref name="path"/>, from the root down, the path itself left out.</summary>
    private static List<string> AttributeFilesAbove(string path)
    {
        var directories = path.Split('/')[..^1];

        return [.. Enumerable.Range(0, directories.Length + 1)
            .Select(depth => string.Join('/', [.. directories.Take(depth), AttributesFile]))
            .Where(file => file != path)];
    }

    /// <summary>
    /// What the index holds at each of <paramref name="paths"/> it holds, as <c>git ls-files --eol</c> says: the line endings
    /// of its entry (<c>i/</c>) and its attributes as git weighs them for line endings (<c>attr/</c>); a path it does not hold
    /// has none. Read a command line of paths at a time, each literally.
    /// </summary>
    /// <exception cref="HarnessException">git could not list them, or listed one in a form this build cannot read.</exception>
    private async Task<Dictionary<string, (string Index, string Attributes)>> LineEndingsAsync(
        string directory,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var asked = paths.ToHashSet(StringComparer.Ordinal);
        var listed = new Dictionary<string, (string Index, string Attributes)>(StringComparer.Ordinal);

        foreach (var batch in CommandLineBatches(paths))
        {
            var result = await RunForBytesAsync(directory, ["--literal-pathspecs", "ls-files", "--eol", "-z", "--", .. batch], cancellationToken).ConfigureAwait(false);

            Ensure(result, $"read the line endings the index holds at {ReportText.Counted(batch.Count, "path")}");

            // 'i/<entry> w/<file> attr/<attributes>TAB<name>', each padded with spaces; the attributes may hold one.
            foreach (var record in Records(result.StandardOutput))
            {
                var tab = record.IndexOf('\t', StringComparison.Ordinal);
                var attributes = record.IndexOf(" attr/", StringComparison.Ordinal);

                if (tab < 0 || !record.StartsWith("i/", StringComparison.Ordinal) || attributes < 0 || attributes > tab)
                {
                    throw new HarnessException(HarnessExit.CommandFailed, $"git listed an entry's line endings in a form this build cannot read: '{GitName.FromBytes(record).Quoted}'");
                }

                var name = GitName.FromBytes(record[(tab + 1)..]);

                // An entry in conflict is listed once for each side, each with what git's rule reads for the path.
                if (name.IsUtf8 && asked.Contains(name.Text))
                {
                    listed.TryAdd(name.Text, (record[2..record.IndexOf(' ', StringComparison.Ordinal)], record[(attributes + 6)..tab].TrimEnd()));
                }
            }
        }

        return listed;
    }

    /// <summary>
    /// The pattern of a line of an attribute file at the root that matches <paramref name="path"/> and nothing else: from
    /// the root, each character a pattern reads otherwise escaped, quoted as git quotes a name so a space or a quote in it is
    /// still the pattern's.
    /// </summary>
    private static string AttributePattern(string path)
    {
        var quoted = new StringBuilder("\"/");

        foreach (var character in path)
        {
            if (character is '\\' or '*' or '?' or '[')
            {
                quoted.Append(@"\\");
            }

            quoted.Append(character switch
            {
                '\\' => @"\\",
                '"' => "\\\"",
                < ' ' or '\x7F' => $"\\{Convert.ToString((int)character, 8).PadLeft(3, '0')}",
                _ => character.ToString(),
            });
        }

        return quoted.Append('"').ToString();
    }

    /// <summary>
    /// The blob <c>git add</c> would record for each of <paramref name="files"/>, read with the attributes
    /// <paramref name="declared"/> gives their paths in place of the work tree's: each path's line in an attribute file at the
    /// root of a tree of the question's own, which git reads attributes from instead (<c>--attr-source</c>, git 2.41's), its
    /// other sources - the repository's info/attributes and the configured ones - read as ever.
    /// </summary>
    /// <remarks>
    /// An older git, which refuses the option as unknown and has no other way to be told, is refused before it is asked
    /// anything for the files, naming the version needed and the one installed. Each path's attributes are read back
    /// through that tree before a file is read: info/attributes outranks any tree, and one that says otherwise of a path
    /// leaves no way to tell git how it would read it there - said, never guessed.
    /// </remarks>
    /// <exception cref="HarnessException">
    /// The git installed is older than 2.41, git could not write the tree or read a file through it, or a path's attributes read back otherwise.
    /// </exception>
    private async Task<IReadOnlyList<string>> DeclaredIdsAsync(
        string directory,
        IReadOnlyList<string> files,
        string? asPath,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> declared,
        bool written,
        ObjectStore? store,
        string what,
        CancellationToken cancellationToken)
    {
        var (installed, release) = await InstalledVersionAsync(directory, cancellationToken).ConfigureAwait(false);

        if (release is not ({ Major: > 2 } or { Major: 2, Minor: >= 41 }))
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"Could not {what}: telling git how it would store '{declared.Keys.First()}' takes its --attr-source, which needs git 2.41 or later, and the git installed is {installed}.");
        }

        return await InScratchAsync(what, async scratch =>
        {
            var own = store ?? await StoreAsync(directory, scratch, cancellationToken).ConfigureAwait(false);
            var lines = Path.Combine(scratch, AttributesFile);
            await File.WriteAllTextAsync(lines, string.Concat(declared.Select(path => Declaration(path.Key, path.Value) + '\n')), cancellationToken).ConfigureAwait(false);

            var blob = (await HashAsync(directory, [lines], new Hashing(Written: true, Filtered: false, Store: own), what, cancellationToken).ConfigureAwait(false))[0];
            var tree = await RunCoreAsync(directory, ["mktree"], echoOutput: false, untranslated: false, indexFile: null, $"100644 blob {blob}\t{AttributesFile}\n", cancellationToken, objects: own)
                .ConfigureAwait(false);

            Ensure(tree, what);

            string[] told = [$"--attr-source={tree.StandardOutput.Trim()}"];
            var read = await ReadAttributesAsync(directory, ConversionAttributes, [.. declared.Keys], told, own, unread: null, cancellationToken).ConfigureAwait(false);

            if (declared.FirstOrDefault(path => ConversionAttributes.Any(name => read[path.Key][name] != path.Value[name])) is { Key: { } differing })
            {
                throw new HarnessException(
                    HarnessExit.CommandFailed,
                    $"Could not {what}: the repository's info/attributes decides how '{differing}' is stored, and outranks any attributes declared to git, so how git would read it cannot be told.");
            }

            return await HashAsync(directory, files, new Hashing(written, Filtered: true, asPath, own, told), what, cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>The line of an attribute file declaring <paramref name="attributes"/> for <paramref name="path"/> alone.</summary>
    private static string Declaration(string path, IReadOnlyDictionary<string, string> attributes)
        => string.Join(' ', [AttributePattern(path), .. ConversionAttributes.Select(name => attributes[name] switch
        {
            GitAttributes.Set => name,
            GitAttributes.Unset => $"-{name}",
            GitAttributes.Unspecified => $"!{name}",
            var value => $"{name}={value}",
        })]);

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
            var id = (await HashAsync(directory, [held], new Hashing(Written: true, Filtered: false, Store: store), what, cancellationToken).ConfigureAwait(false))[0];
            var result = await RunForBytesAsync(directory, ["cat-file", "--filters", $"--path={path}", id], cancellationToken, objects: store).ConfigureAwait(false);

            Ensure(result, what);

            return Encoding.Latin1.GetBytes(result.StandardOutput);
        });
    }

    public Task<GitTextMerge> MergeAsync(string directory, string path, IReadOnlyList<GitContent> versions, GitMergeNames names, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(names);

        if (versions.Count != 3)
        {
            throw new ArgumentException($"A merge is of three versions - what both held, ours and theirs - and {versions.Count} were given.", nameof(versions));
        }

        var what = $"merge three versions of '{path}'";

        return InScratchAsync(what, async scratch =>
        {
            // Each version as git stores it at the path - a blob as it is, a file or bytes through the path's clean filters -
            // into a store of the question's own, then unpacked as those bytes into a file of the scratch: what is merged.
            var store = await StoreAsync(directory, scratch, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<string> ids;

            try
            {
                ids = await StoredVersionsAsync(directory, path, versions, scratch, store, $"read '{path}' as git would store it", cancellationToken).ConfigureAwait(false);
            }
            catch (HarnessException ex) when (ex.ExitCode == HarnessExit.CommandFailed)
            {
                // A version git cannot bring to the form it stores - a filter that fails on it - is an answer: nothing to merge.
                return GitTextMerge.Unstored(ex.Message);
            }

            var gitDirectory = await AbsoluteGitDirectoryAsync(directory, what, cancellationToken).ConfigureAwait(false);
            string[] named = [AncestorFile, OursFile, TheirsFile];

            for (var at = 0; at < named.Length; at++)
            {
                await UnpackAsync(gitDirectory, scratch, store, ids[at], Path.Combine(scratch, named[at]), what, cancellationToken).ConfigureAwait(false);
            }

            // The merge is written over a copy of ours, a file on disk, and never through this process; ours itself is kept,
            // for the hunks it changed are counted against what both held.
            File.Copy(Path.Combine(scratch, OursFile), Path.Combine(scratch, MergedFile));

            // Run where the three are, outside any repository, and told how a conflict is written: --diff3, so no
            // configuration changes what is shown, and markers no line of the three could be taken for.
            var size = MarkerSize([.. named.Select(name => Path.Combine(scratch, name))]);
            var merged = await RunForBytesAsync(
                    scratch,
                    ["merge-file", "--diff3", $"--marker-size={size}", "-L", names.Ours, "-L", names.Ancestor, "-L", names.Theirs, MergedFile, AncestorFile, TheirsFile],
                    cancellationToken)
                .ConfigureAwait(false);

            // git's exit code is the answer, read as it is and never as success or failure: 0 is a clean merge; 1 to 127
            // says the merge conflicts, its count cut at 127, so the conflicts are counted by their markers; anything else
            // is no merge at all - a binary file, for one - said in git's own words.
            if (merged is { TimedOut: false, ExitCode: 0 })
            {
                var blob = (await HashAsync(directory, [Path.Combine(scratch, MergedFile)], new Hashing(Written: true, Filtered: false), what, cancellationToken).ConfigureAwait(false))[0];

                return GitTextMerge.Clean(
                    blob,
                    await HunksAsync(scratch, OursFile, cancellationToken).ConfigureAwait(false),
                    await HunksAsync(scratch, TheirsFile, cancellationToken).ConfigureAwait(false));
            }

            if (merged is { TimedOut: false, ExitCode: >= 1 and <= GitTextMerge.MostConflictsCounted })
            {
                var (conflicts, shown) = ConflictsOf(Path.Combine(scratch, MergedFile), size);

                return conflicts > 0
                    ? GitTextMerge.Conflicting(conflicts, shown)
                    : throw new HarnessException(HarnessExit.CommandFailed, $"Could not {what}: git said the merge conflicts, and its output holds no conflict's markers.");
            }

            return GitTextMerge.Unmerged(merged.FailureMessage);
        });
    }

    public Task<T> CheckedOutAsync<T>(string directory, string path, string blob, bool executable, Func<string, Task<T>> use, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(blob);
        ArgumentNullException.ThrowIfNull(use);

        var what = $"write '{path}' as git would check it out";

        return HandedAsync(what, scratch => CheckOutIntoAsync(directory, path, blob, executable, scratch, what, cancellationToken), use);
    }

    public Task<T> UnpackedAsync<T>(string directory, string blob, Func<string, Task<T>> use, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blob);
        ArgumentNullException.ThrowIfNull(use);

        var what = $"read blob {blob} as git stores it";

        return HandedAsync(
            what,
            async scratch =>
            {
                var unpacked = Path.Combine(scratch, "unpacked");
                await UnpackAsync(await AbsoluteGitDirectoryAsync(directory, what, cancellationToken).ConfigureAwait(false), scratch, store: null, blob, unpacked, what, cancellationToken)
                    .ConfigureAwait(false);
                return unpacked;
            },
            use);
    }

    /// <summary>
    /// Checks <paramref name="blob"/> out at <paramref name="path"/> as the work tree at <paramref name="directory"/> would,
    /// under a directory of <paramref name="scratch"/>, and names the file written: an index of the question's own holding
    /// the one entry, so git reads the work tree's attributes and settings for the path as a checkout does, and writes
    /// nothing of the tree's.
    /// </summary>
    private async Task<string> CheckOutIntoAsync(string directory, string path, string blob, bool executable, string scratch, string what, CancellationToken cancellationToken)
    {
        var index = Path.Combine(scratch, $"index-{Guid.NewGuid():N}");
        var written = Path.Combine(scratch, $"checked-out-{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
        var entry = await RunWithIndexAsync(
                directory,
                index,
                ["--literal-pathspecs", "update-index", "--add", "--cacheinfo", $"{(executable ? "100755" : "100644")},{blob},{path}"],
                standardInput: null,
                cancellationToken)
            .ConfigureAwait(false);

        Ensure(entry, what);

        var checkedOut = await RunWithIndexAsync(directory, index, ["--literal-pathspecs", "checkout-index", $"--prefix={written}", "--", path], standardInput: null, cancellationToken)
            .ConfigureAwait(false);

        Ensure(checkedOut, what);

        return Path.Combine(written, path.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// Hands <paramref name="use"/> the file <paramref name="make"/> writes in a scratch of the question's own, removed once
    /// <paramref name="use"/> has answered. What <paramref name="use"/> raises is its own, and reaches the caller as it was
    /// raised: never taken for a scratch that could not be written.
    /// </summary>
    private static async Task<T> HandedAsync<T>(string what, Func<string, Task<string>> make, Func<string, Task<T>> use)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failed = null;

        var answer = await InScratchAsync(what, async scratch =>
            {
                var file = await make(scratch).ConfigureAwait(false);

                try
                {
                    return await use(file).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
                    return default!;
                }
            })
            .ConfigureAwait(false);

        failed?.Throw();

        return answer;
    }

    /// <summary>
    /// The blob each of <paramref name="versions"/> is as git stores it at <paramref name="path"/>, in order: a blob's own
    /// id, and a file's or bytes' read through the path's rules and written into <paramref name="store"/>.
    /// </summary>
    private async Task<IReadOnlyList<string>> StoredVersionsAsync(
        string directory,
        string path,
        IReadOnlyList<GitContent> versions,
        string scratch,
        ObjectStore store,
        string what,
        CancellationToken cancellationToken)
    {
        var ids = new string?[versions.Count];
        var files = new List<(int At, string File)>();
        string? gitDirectory = null;

        for (var at = 0; at < versions.Count; at++)
        {
            var held = Path.Combine(scratch, $"content-{at}");

            switch (versions[at])
            {
                case GitContent.Blob blob:
                    ids[at] = blob.Id;
                    break;
                case GitContent.File file:
                    files.Add((at, Path.GetFullPath(file.Path)));
                    break;
                case GitContent.Bytes bytes:
                    await File.WriteAllBytesAsync(held, bytes.Content, cancellationToken).ConfigureAwait(false);
                    files.Add((at, held));
                    break;
                case GitContent.BlobAsFile { CheckedOutIn: null } raw:
                    gitDirectory ??= await AbsoluteGitDirectoryAsync(directory, what, cancellationToken).ConfigureAwait(false);
                    await UnpackAsync(gitDirectory, scratch, store, raw.Id, held, what, cancellationToken).ConfigureAwait(false);
                    files.Add((at, held));
                    break;
                case GitContent.BlobAsFile checkedOut:
                    files.Add((at, await CheckOutIntoAsync(checkedOut.CheckedOutIn!, path, checkedOut.Id, executable: false, scratch, what, cancellationToken).ConfigureAwait(false)));
                    break;
                default:
                    throw new ArgumentException($"Content is one of four kinds, and {versions[at].GetType().Name} is none of them.", nameof(versions));
            }
        }

        if (files.Count > 0)
        {
            // Written, since it is the object that is read back - into the store of this question's own, never the
            // repository's.
            var hashed = await StoredIdsAsync(directory, [.. files.Select(file => file.File)], path, written: true, store, what, cancellationToken).ConfigureAwait(false);

            for (var at = 0; at < files.Count; at++)
            {
                ids[files[at].At] = hashed[at];
            }
        }

        return [.. ids.Select(id => id!)];
    }

    /// <summary>The git directory of the work tree at <paramref name="directory"/>, as an absolute path.</summary>
    private async Task<string> AbsoluteGitDirectoryAsync(string directory, string what, CancellationToken cancellationToken)
    {
        var result = await RunQueryAsync(directory, ["rev-parse", "--absolute-git-dir"], cancellationToken).ConfigureAwait(false);

        Ensure(result, what);

        return result.StandardOutput.Trim();
    }

    /// <summary>
    /// Writes the bytes of <paramref name="blob"/>, as git stores them, to the file <paramref name="into"/>: unpacked by
    /// git itself (<c>git unpack-file</c>), which converts nothing, into the scratch, and named.
    /// </summary>
    private async Task UnpackAsync(string gitDirectory, string scratch, ObjectStore? store, string blob, string into, string what, CancellationToken cancellationToken)
    {
        // Run in the scratch, which git unpacks into, and told the repository whose objects it reads: the store of the
        // question's own, where there is one, the repository's beside it.
        var unpacked = await RunCoreAsync(
                scratch,
                [$"--git-dir={gitDirectory}", "unpack-file", blob],
                echoOutput: false,
                untranslated: false,
                indexFile: null,
                standardInput: null,
                cancellationToken,
                objects: store)
            .ConfigureAwait(false);

        Ensure(unpacked, what);

        var name = unpacked.StandardOutput.Trim();

        if (name.Length == 0 || name.IndexOfAny(['/', '\\']) >= 0)
        {
            throw new HarnessException(HarnessExit.CommandFailed, $"Could not {what}: git unpack-file named '{name}', which is no file of the directory it ran in.");
        }

        File.Move(Path.Combine(scratch, name), into);
    }

    public async Task<IReadOnlyList<byte[]?>> ReadBlobsAsync(string directory, IReadOnlyList<string> objects, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objects);

        var read = await ReadBatchAsync(directory, objects, $"read {ReportText.Counted(objects.Count, "object")}", store: null, cancellationToken).ConfigureAwait(false);

        return [.. read.Select(answer => answer is { Type: "blob" } blob ? Encoding.Latin1.GetBytes(blob.Content) : null)];
    }

    public async Task<IReadOnlyList<long?>> ReadBlobSizesAsync(string directory, IReadOnlyList<string> objects, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objects);

        if (objects.Count == 0)
        {
            return [];
        }

        if (objects.FirstOrDefault(name => name.Contains('\n', StringComparison.Ordinal) || name.Contains('\r', StringComparison.Ordinal)) is { } broken)
        {
            throw new ArgumentException($"'{broken}' holds a line break, which git reads as two names.", nameof(objects));
        }

        var result = await RunCoreAsync(
                directory,
                ["cat-file", "--batch-check=%(objecttype) %(objectsize)"],
                echoOutput: false,
                untranslated: false,
                indexFile: null,
                string.Concat(objects.Select(name => name + "\n")),
                cancellationToken)
            .ConfigureAwait(false);

        Ensure(result, $"size {ReportText.Counted(objects.Count, "object")}");

        var lines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToList();

        if (lines.Count != objects.Count)
        {
            throw new HarnessException(HarnessExit.CommandFailed, $"git answered {ReportText.Counted(lines.Count, "line")} for the {objects.Count} object(s) it was asked the size of.");
        }

        // A blob is its type and size; anything else - another type, or git's word that it holds no such object - is none.
        return [.. lines.Select(line => line.Split(' ') is ["blob", var size] && long.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) ? bytes : (long?)null)];
    }

    public Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> ReadAttributesAsync(
        string directory,
        IReadOnlyList<string> attributes,
        IReadOnlyList<string> paths,
        string? unread = null,
        CancellationToken cancellationToken = default)
        => ReadAttributesAsync(directory, attributes, paths, told: [], store: null, unread, cancellationToken);

    /// <summary>
    /// What <see cref="ReadAttributesAsync(string, IReadOnlyList{string}, IReadOnlyList{string}, string?, CancellationToken)"/>
    /// answers, git told <paramref name="told"/> first - where its attributes are read from - and reading
    /// <paramref name="store"/> beside the repository's objects where one is given.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> ReadAttributesAsync(
        string directory,
        IReadOnlyList<string> attributes,
        IReadOnlyList<string> paths,
        IReadOnlyList<string> told,
        ObjectStore? store,
        string? unread,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(paths);

        var declared = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        if (attributes.Count > 0)
        {
            foreach (var batch in CommandLineBatches(paths))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result = await RunForBytesAsync(directory, [.. told, "check-attr", "-z", .. attributes, "--", .. batch], cancellationToken, objects: store)
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

    /// <summary>
    /// Groups blobs, by the index of each in <paramref name="sizes"/>, into batches read together - each by one git
    /// process (<see cref="ReadBlobsAsync"/>) - of at most <see cref="ReadTogether"/> bytes in all: every blob is in one,
    /// in order, and one larger than that is a batch of its own, which a reader that need not hold it whole reads from a
    /// file instead (<see cref="UnpackedAsync"/>). A history of any size so costs no more memory than its largest batch.
    /// A blob git holds no size for counts as none.
    /// </summary>
    /// <param name="sizes">Each blob's size, as <see cref="ReadBlobSizesAsync"/> answers it.</param>
    internal static IEnumerable<IReadOnlyList<int>> SizeBatches(IReadOnlyList<long?> sizes)
    {
        ArgumentNullException.ThrowIfNull(sizes);

        var batch = new List<int>();
        long held = 0;

        for (var at = 0; at < sizes.Count; at++)
        {
            var size = sizes[at] ?? 0;

            if (batch.Count > 0 && held + size > ReadTogether)
            {
                yield return batch;
                (batch, held) = ([], 0);
            }

            batch.Add(at);
            held += size;
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    /// <summary>Groups paths into invocations neither too many nor too long for one command line.</summary>
    internal static IEnumerable<List<string>> CommandLineBatches(IReadOnlyList<string> paths)
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
    /// How long a conflict's markers are made for these three versions, each a file read a block at a time:
    /// <see cref="DefaultMarkerSize"/>, or one more than the longest run of a marker's character that begins a line of any
    /// of them, so that a file holding such a line cannot have it taken for a conflict.
    /// </summary>
    private static int MarkerSize(IReadOnlyList<string> versions)
    {
        var longest = 0;
        var buffer = new byte[64 * 1024];

        foreach (var version in versions)
        {
            using var read = File.OpenRead(version);

            // Where the scan stands: at a line's start, inside a run of one marker character from a line's start, or past it.
            var atStart = true;
            byte runOf = 0;
            var run = 0;

            for (var got = read.Read(buffer); got > 0; got = read.Read(buffer))
            {
                foreach (var next in buffer.AsSpan(0, got))
                {
                    if (run > 0 && next == runOf)
                    {
                        run++;
                        continue;
                    }

                    longest = Math.Max(longest, run);
                    run = 0;

                    if (atStart && MarkerCharacters.Contains((char)next, StringComparison.Ordinal))
                    {
                        (runOf, run) = (next, 1);
                    }

                    atStart = next == (byte)'\n';
                }
            }

            longest = Math.Max(longest, run);
        }

        return Math.Max(DefaultMarkerSize, longest + 1);
    }

    /// <summary>
    /// The conflicts of a merge's output, the file <paramref name="merged"/> read a line at a time - a line ends at a line
    /// feed, as git's do - each from its opening marker to its closing one, a line to an element: every conflict, counted,
    /// and every line of each. A marker is a line that begins with as many of its character as the merge was told to
    /// write, which no line of the three versions does. A line outside a conflict is read past and held nowhere, so the
    /// merged file's size costs nothing but its conflicts.
    /// </summary>
    private static (int Conflicts, List<string> Shown) ConflictsOf(string merged, int markerSize)
    {
        var opening = new string('<', markerSize);
        var closing = new string('>', markerSize);
        var shown = new List<string>();
        var conflicts = 0;
        var inside = false;
        var line = new List<byte>();
        var buffer = new byte[64 * 1024];

        // A line outside a conflict is held only as far as its marker would reach; one inside is held whole.
        void Ended()
        {
            var text = Encoding.UTF8.GetString([.. line]).TrimEnd('\r');

            if (!inside && text.StartsWith(opening, StringComparison.Ordinal))
            {
                inside = true;
                conflicts++;
            }

            if (inside)
            {
                shown.Add(Shown(text));

                if (text.StartsWith(closing, StringComparison.Ordinal))
                {
                    inside = false;
                }
            }

            line.Clear();
        }

        using (var read = File.OpenRead(merged))
        {
            var last = (byte)'\n';

            // How many of the line's first bytes open a conflict's marker; once the marker's length, the line opens one.
            var opened = 0;

            for (var got = read.Read(buffer); got > 0; got = read.Read(buffer))
            {
                foreach (var next in buffer.AsSpan(0, got))
                {
                    last = next;

                    if (next == (byte)'\n')
                    {
                        Ended();
                        opened = 0;
                        continue;
                    }

                    if (line.Count == opened && opened < markerSize && next == (byte)'<')
                    {
                        opened++;
                    }

                    if (inside || line.Count < markerSize || opened == markerSize)
                    {
                        line.Add(next);
                    }
                }
            }

            if (last != (byte)'\n')
            {
                Ended();
            }
        }

        return (conflicts, shown);
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
        // Written by git to a file, and read back a line at a time: a regenerated file's diff is as large as the file.
        var hunks = Path.Combine(scratch, $"{side}.diff");
        var result = await RunForBytesAsync(
                scratch,
                ["diff", "--no-index", "--no-color", "--no-ext-diff", "--no-textconv", "-U0", $"--output={hunks}", "--", AncestorFile, side],
                cancellationToken)
            .ConfigureAwait(false);

        // 1 is git's answer that the two differ, as 0 is that they do not: neither is a failure.
        if (result.TimedOut || result.ExitCode is not (0 or 1))
        {
            throw new HarnessException(HarnessExit.CommandFailed, $"Could not count the hunks of a merge: {result.FailureMessage}");
        }

        if (!File.Exists(hunks))
        {
            return 0;
        }

        // A hunk's header is a line that begins with "@@", a line ending at a line feed as git's do.
        var count = 0;
        var atStart = true;
        var matched = 0;
        var buffer = new byte[64 * 1024];

        using var read = File.OpenRead(hunks);

        for (var got = read.Read(buffer); got > 0; got = read.Read(buffer))
        {
            foreach (var next in buffer.AsSpan(0, got))
            {
                if (next == (byte)'\n')
                {
                    (atStart, matched) = (true, 0);
                    continue;
                }

                if (atStart && next == (byte)'@')
                {
                    matched++;
                    atStart = matched < 2;
                    count += matched == 2 ? 1 : 0;
                    continue;
                }

                atStart = false;
            }
        }

        return count;
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
