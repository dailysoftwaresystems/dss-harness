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

    /// <summary>The file a directory of a work tree declares git's attributes for the paths below it in.</summary>
    private const string AttributesFile = ".gitattributes";

    /// <summary>
    /// Paths named in one git invocation that takes them as arguments. Batched because a command line is bounded
    /// -- 32,767 characters on Windows -- and a repository has more files than that allows.
    /// </summary>
    internal const int CommandLineBatchSize = 200;

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
            var ids = new string?[contents.Count];
            var files = new List<(int At, string File)>();

            for (var at = 0; at < contents.Count; at++)
            {
                switch (contents[at])
                {
                    case GitContent.Blob blob:
                        ids[at] = blob.Id;
                        break;
                    case GitContent.File file:
                        files.Add((at, Path.GetFullPath(file.Path)));
                        break;
                    case GitContent.Bytes bytes:
                        var held = Path.Combine(scratch, $"content-{at}");
                        await File.WriteAllBytesAsync(held, bytes.Content, cancellationToken).ConfigureAwait(false);
                        files.Add((at, held));
                        break;
                }
            }

            if (files.Count > 0)
            {
                // Written, since it is the object that is read back - into the store of this question's own, never the
                // repository's.
                var hashed = await StoredIdsAsync(directory, [.. files.Select(file => file.File)], path, written: true, store, what, cancellationToken)
                    .ConfigureAwait(false);

                for (var at = 0; at < files.Count; at++)
                {
                    ids[files[at].At] = hashed[at];
                }
            }

            var read = await ReadBatchAsync(directory, [.. ids.Select(id => id!)], what, store, cancellationToken).ConfigureAwait(false);

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
