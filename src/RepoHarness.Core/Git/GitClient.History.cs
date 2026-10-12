using System.Globalization;
using System.Text;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Git;

/// <content>Carrying a tree's HEAD commit, and the history behind it, into another repository.</content>
public sealed partial class GitClient
{
    /// <summary>What the packs written for a copy are called, before the name git gives each.</summary>
    private const string PackBase = "history";

    /// <summary>
    /// Where, in a repository's git directory, the pieces of a pack on its way are kept until it is taken whole. Nothing
    /// else writes there, so what a transfer that stopped left in it is this tool's own to clear.
    /// </summary>
    private const string IncomingDirectory = "dssharness-incoming";

    /// <summary>What a pack's file ends in, and its index's.</summary>
    private const string PackSuffix = ".pack";

    private const string IndexSuffix = ".idx";

    /// <summary>What the copy's record of its HEAD's moves says of one a sync made.</summary>
    private const string HeadMovedBy = "dssharness sync: moved to the commit of the tree synced";

    public async Task<GitHistoryWanted> DescribeHistoryAsync(string directory, bool whole, CancellationToken cancellationToken = default)
        => new(
            await ResolveCommitAsync(directory, "HEAD", cancellationToken).ConfigureAwait(false),
            whole,
            await ObjectFormatAsync(directory, cancellationToken).ConfigureAwait(false),
            await ListShallowAsync(directory, cancellationToken).ConfigureAwait(false));

    public async Task<GitHistoryPack> PackHistoryAsync(
        string directory,
        GitHistoryWanted wanted,
        string? leftOut,
        string into,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentException.ThrowIfNullOrWhiteSpace(into);

        var commit = GitObjectId.Require(wanted.Commit, "packed");
        var revisions = new StringBuilder();

        // The commit alone: named as a place history stops, it is walked as a commit with no parent, so the pack holds
        // it and what its tree names and nothing behind it - what git itself sends a clone of depth one.
        if (!wanted.Whole)
        {
            revisions.Append("--shallow ").Append(commit).Append('\n');
        }

        revisions.Append(commit).Append('\n');

        // What the copy holds already is left out: every commit behind its own, where they are all asked for; and where
        // the commit alone is, what its own commit's tree names - never that commit's history, which the copy may not hold.
        if (leftOut is not null)
        {
            revisions.Append('^').Append(GitObjectId.Require(leftOut, "left out of a pack")).Append(wanted.Whole ? "\n" : "^{tree}\n");
        }

        Directory.CreateDirectory(into);

        var packed = await RunCoreAsync(
                directory,
                // One pack, whatever size the repository's own configuration holds a pack to: split, it is several
                // names, and a piece of each is no pack the copy can take.
                ["-c", "pack.packSizeLimit=0", "pack-objects", "--revs", "--quiet", Path.Combine(into, PackBase)],
                echoOutput: false,
                untranslated: false,
                indexFile: null,
                revisions.ToString(),
                cancellationToken)
            .ConfigureAwait(false);

        Ensure(packed, $"pack {commit} for a copy of '{directory}'");

        var name = packed.StandardOutput.Trim();
        var file = Path.Combine(into, $"{PackBase}-{name}.pack");

        return GitObjectId.IsWhole(name) && File.Exists(file)
            ? new GitHistoryPack(file, name, new FileInfo(file).Length)
            : throw new HarnessException(
                HarnessExit.CommandFailed,
                $"git packed {commit} for a copy of '{directory}' and did not say where: it answered '{name}'.");
    }

    public async Task<GitHistoryHeld> ReadHistoryAsync(string directory, GitHistoryWanted? wanted, CancellationToken cancellationToken = default)
    {
        var format = await ObjectFormatAsync(directory, cancellationToken).ConfigureAwait(false);
        var head = await ResolveCommitAsync(directory, "HEAD", cancellationToken).ConfigureAwait(false);
        var here = new GitHistoryHeld(format)
        {
            Head = head,
            Branch = head is null ? null : await BranchAsync(directory, cancellationToken).ConfigureAwait(false),
        };

        // Nothing asked; or objects named another way, which are no objects of this repository's: it holds none of what is
        // asked, and can take none.
        if (wanted?.Commit is null || !string.Equals(format, wanted.ObjectFormat, StringComparison.Ordinal))
        {
            return here;
        }

        var commit = GitObjectId.Require(wanted.Commit, "looked for");
        var boundary = wanted.Boundary.Select(id => GitObjectId.Require(id, "a place history stops")).ToHashSet(StringComparer.Ordinal);

        // Where this repository's history stops and the asker's does not: behind each, the asker holds what this one lacks.
        var shallow = await ListShallowAsync(directory, cancellationToken).ConfigureAwait(false);
        var short_ = (await HeldAsync(directory, [.. shallow.Where(id => !boundary.Contains(id))], cancellationToken).ConfigureAwait(false))
            .Order(StringComparer.Ordinal)
            .ToList();

        var holds = await HoldsAsync(directory, commit, wanted.Whole, head, short_, cancellationToken).ConfigureAwait(false);

        return here with
        {
            Holds = holds,
            HeadHeld = !holds && head is not null && await HeadHeldAsync(directory, head, wanted.Whole, short_, cancellationToken).ConfigureAwait(false),
        };
    }

    public async Task ReceiveHistoryAsync(string directory, string pack, long offset, byte[] piece, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(piece);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        var file = await IncomingAsync(directory, pack, cancellationToken).ConfigureAwait(false);
        var kept = Path.GetDirectoryName(file)!;

        try
        {
            if (offset == 0)
            {
                // Said as what it is: left, the piece would be refused as one that does not follow what arrived.
                if (DiscardDirectory(kept) is { } uncleared)
                {
                    throw new HarnessException(
                        HarnessExit.CommandFailed,
                        $"'{kept}', where an earlier pack sent to '{directory}' was kept, could not be cleared for this one: {uncleared}.");
                }

                Directory.CreateDirectory(kept);
            }

            var before = File.Exists(file) ? new FileInfo(file).Length : 0;

            // Refused rather than written where it landed: a pack with a piece lost, or twice over, is no pack, and
            // git would say so only of the whole of it, naming none of this.
            if (before != offset)
            {
                throw new HarnessException(
                    HarnessExit.CommandFailed,
                    $"A piece of the history sent to '{directory}' did not follow what arrived before it: it starts at byte "
                    + $"{offset.ToString(CultureInfo.InvariantCulture)}, and {before.ToString(CultureInfo.InvariantCulture)} "
                    + "byte(s) had arrived. Nothing of it was taken.");
            }

            using var writing = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.None);

            writing.Write(piece);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"A piece of the history sent to '{directory}' could not be kept at '{file}': {ex.Message.TrimEnd('.')}.");
        }
    }

    public async Task<GitHeadMoved> TakeHistoryAsync(string directory, GitHistoryTaken taken, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taken);

        var kept = await GitPathAsync(directory, IncomingDirectory, "find where a pack on its way is kept", cancellationToken).ConfigureAwait(false);
        GitHeadMoved moved;

        try
        {
            moved = await TakeAsync(
                    directory,
                    taken,
                    taken.Pack is null ? null : Path.Combine(kept, GitObjectId.Require(taken.Pack, "the name of a pack") + PackSuffix),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // What could not be taken is sent again whole, and its first piece clears this, or says why it cannot: the
            // failure of the take is what is said here.
            DiscardDirectory(kept);
            throw;
        }

        // Taken, what was kept aside has served. Left where it could not be removed, it is said: it is the size of
        // what was sent.
        return DiscardDirectory(kept) is { } why
            ? moved with { LeftAside = $"'{kept}', where the pack sent was kept until it was taken, could not be removed: {why}. The next pack sent there clears it." }
            : moved;
    }

    /// <summary>Where the pieces of the pack named <paramref name="pack"/> are kept in the repository at <paramref name="directory"/>.</summary>
    private async Task<string> IncomingAsync(string directory, string pack, CancellationToken cancellationToken)
        => Path.Combine(
            await GitPathAsync(directory, IncomingDirectory, "find where a pack on its way is kept", cancellationToken).ConfigureAwait(false),
            GitObjectId.Require(pack, "the name of a pack") + PackSuffix);

    /// <summary>
    /// Removes <paramref name="path"/>, a directory of this tool's own, and all in it, where it is there.
    /// </summary>
    /// <returns>Why it could not be removed, or <see langword="null"/> where it was, or was not there.</returns>
    private static string? DiscardDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                // By the rule every directory is removed by: git marks the index it writes beside a pack read-only,
                // and one left here - by a pack the repository held already, or a take that then failed - is refused
                // by a plain removal on Windows, for every pack sent after it.
                FileSystem.PhysicalFileSystem.DeleteMarked(path);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message.TrimEnd('.');
        }
    }

    private async Task<GitHeadMoved> TakeAsync(string directory, GitHistoryTaken taken, string? pack, CancellationToken cancellationToken)
    {
        var wanted = taken.Wanted;
        var commit = GitObjectId.Require(wanted.Commit, "taken");
        var leftOut = taken.LeftOut is null ? null : GitObjectId.Require(taken.LeftOut, "left out of what is taken");
        var boundary = wanted.Boundary.Select(id => GitObjectId.Require(id, "a place history stops")).ToList();

        var head = await ResolveCommitAsync(directory, "HEAD", cancellationToken).ConfigureAwait(false);
        var branch = head is null ? null : await BranchAsync(directory, cancellationToken).ConfigureAwait(false);

        if (pack is not null)
        {
            await PlaceAsync(directory, pack, cancellationToken).ConfigureAwait(false);
        }

        if ((await HeldAsync(directory, [commit], cancellationToken).ConfigureAwait(false)).Count == 0)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"'{directory}' does not hold {commit}, the commit its HEAD was to name, "
                + $"{(pack is null ? "and was sent nothing" : "after taking what it was sent")}. Its HEAD was left where it was.");
        }

        // Where history stops is recorded where it does, and nowhere else: at each commit this repository holds a parent
        // of which it does not - of those it stopped at before, the commit taken, and those the asker's own stops at.
        // git reads a commit recorded so as one with no parent, and one not recorded with a parent missing as damage.
        var shallow = await GitPathAsync(directory, "shallow", "find where git records where history stops", cancellationToken).ConfigureAwait(false);
        var before = ReadShallow(shallow);
        var after = await BoundaryAsync(directory, [.. before, commit, .. boundary], cancellationToken).ConfigureAwait(false);
        var stops = after;

        if (!before.ToHashSet(StringComparer.Ordinal).SetEquals(after))
        {
            WriteShallow(shallow, after);
        }

        // The commit alone was asked for, and what is behind it is whatever the repository happens to hold - a commit
        // a take that stopped left without its parents among it, which nothing records and git reads as damage. Where
        // the history behind the commit cannot be walked, the commit is where history stops.
        if (!wanted.Whole
            && !after.Contains(commit, StringComparer.Ordinal)
            && !(await RunAsync(directory, ["rev-list", "--quiet", commit], cancellationToken: cancellationToken).ConfigureAwait(false)).Succeeded)
        {
            stops = [.. after, commit];
            WriteShallow(shallow, stops);
        }

        // Said whether or not the take then succeeds: a repository that held commits, each with all behind it, and now
        // holds one without, is a shallow one to git from here on, and a fetch there stops where its history does.
        var cut = head is not null && before.Count == 0 && stops.Count > 0
            ? $"'{directory}' held every commit behind those it holds, and was given {commit} without the commits behind it: git "
                + "reads it as a shallow repository from here on, and a fetch there brings nothing from behind that commit until "
                + "'git fetch --unshallow' is run there."
            : null;

        // Every object the commit names, walked as git walks them after a fetch, before HEAD names it: all behind it too
        // where all was asked for, less what was left out as held already - and, where nothing was sent, less what the
        // repository's own branches, tags and HEAD reach, which git keeps whole: walked again, a tree put back at an
        // earlier commit read every object of the history for each copy.
        IReadOnlyList<string> walked = !wanted.Whole ? ["--no-walk", commit]
            : pack is null ? [commit, "--not", "--all"]
            : leftOut is null ? [commit]
            : [commit, "^" + leftOut];

        var connected = await RunAsync(directory, ["rev-list", "--objects", "--quiet", .. walked], cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // Where history stops stays as recorded: it is true of what the repository now holds, whether or not that is
        // all the commit names.
        if (!connected.Succeeded)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"'{directory}' does not hold all that {commit} names after taking what it was sent, so its HEAD was left "
                + $"where it was: {connected.FailureMessage}{(cut is null ? string.Empty : " " + cut)}");
        }

        // Where it names the commit already it is left as it is, on whatever branch it is on.
        var moves = !string.Equals(head, commit, StringComparison.Ordinal);

        if (moves)
        {
            // HEAD itself, never the branch it is on: the branch, and every commit of it, stay as they were.
            var moved = await RunAsync(
                    directory,
                    ["update-ref", "--no-deref", "-m", HeadMovedBy, "HEAD", commit],
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            Ensure(moved, $"move the HEAD of '{directory}' to {commit}");
        }

        return new GitHeadMoved(head, branch, moves) { CutShort = cut };
    }

    /// <summary>
    /// Puts the pack kept at <paramref name="pack"/> among the repository's own: read whole and checked object by object
    /// where it was kept, and only then moved in, its index first - so one cut short on the way is no part of the
    /// repository.
    /// </summary>
    /// <remarks>
    /// Read where it was kept, never through git's own taking of a fetch, which writes the pack a second time into a
    /// temporary file among the repository's packs: a take stopped part way left that there, the size of the pack, for
    /// nothing to clear. What a take stopped here leaves is where the next pack sent clears it - and, among the
    /// repository's own, at most an index with no pack, which git passes over.
    /// </remarks>
    private async Task PlaceAsync(string directory, string pack, CancellationToken cancellationToken)
    {
        // Looked for before git is started: one that is not there is said as that, never as a pack git could not open.
        if (!File.Exists(pack))
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"The pack sent to '{directory}' is not where it was kept, at '{pack}'. Its HEAD was left where it was.");
        }

        var indexed = await RunAsync(directory, ["index-pack", pack], cancellationToken: cancellationToken).ConfigureAwait(false);

        Ensure(indexed, $"take the history sent to '{directory}'");

        // What git calls it: the hash of what it holds, whatever the sender called it.
        var name = indexed.StandardOutput.Trim();

        if (!GitObjectId.IsWhole(name))
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"git read the pack sent to '{directory}' and did not say what it holds: it answered '{name}'. Its HEAD was left where it was.");
        }

        var among = await GitPathAsync(directory, "objects/pack", "find where the repository keeps its packs", cancellationToken).ConfigureAwait(false);
        var kept = pack[..^PackSuffix.Length];
        var own = Path.Combine(among, "pack-" + name);

        try
        {
            // Held already - the same pack, taken before - it is the repository's own as it stands.
            if (File.Exists(own + IndexSuffix) && File.Exists(own + PackSuffix))
            {
                return;
            }

            Directory.CreateDirectory(among);

            // The index first and the pack last: git finds a pack by its index, and passes over an index whose pack is
            // not there.
            foreach (var part in new[] { IndexSuffix, ".rev", PackSuffix })
            {
                if (File.Exists(kept + part))
                {
                    Replace(kept + part, own + part);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"The pack sent to '{directory}' was read whole and could not be put among its own, in '{among}': "
                + $"{ex.Message.TrimEnd('.')}. Its HEAD was left where it was.");
        }
    }

    /// <summary>Moves <paramref name="from"/> to <paramref name="to"/>, in place of what an earlier take stopped part way left there.</summary>
    private static void Replace(string from, string to)
    {
        if (File.Exists(to))
        {
            // git marks what it keeps among its packs read-only, which no system's move replaces everywhere.
            File.SetAttributes(to, FileAttributes.Normal);
            File.Delete(to);
        }

        File.Move(from, to);
    }

    /// <summary>Where git keeps <paramref name="name"/> for the work tree at <paramref name="directory"/>, as an absolute path.</summary>
    private async Task<string> GitPathAsync(string directory, string name, string what, CancellationToken cancellationToken)
    {
        var result = await RunAsync(directory, ["rev-parse", "--git-path", name], cancellationToken: cancellationToken).ConfigureAwait(false);

        Ensure(result, what);

        // git answers relative to the directory it ran in, where it can.
        return Path.GetFullPath(Path.Combine(directory, result.StandardOutput.Trim()));
    }

    /// <summary>How the repository at <paramref name="directory"/> names its objects.</summary>
    private async Task<string> ObjectFormatAsync(string directory, CancellationToken cancellationToken)
    {
        var result = await RunAsync(directory, ["rev-parse", "--show-object-format"], cancellationToken: cancellationToken).ConfigureAwait(false);

        Ensure(result, $"read how the repository at '{directory}' names its objects");

        var said = result.StandardOutput.Trim();

        // A git older than 2.25 knows one way and not the question, which it answers with the option itself.
        return said.Length > 0 && said.All(char.IsAsciiLetterOrDigit) ? said : GitHistoryWanted.DefaultObjectFormat;
    }

    /// <summary>The branch the HEAD at <paramref name="directory"/> is on, or <see langword="null"/> where it is on none.</summary>
    private async Task<string?> BranchAsync(string directory, CancellationToken cancellationToken)
    {
        var result = await RunAsync(directory, ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken: cancellationToken).ConfigureAwait(false);

        // With --quiet, git answers "on no branch" with exit code 1 and nothing else.
        if (result.ExitCode == 1 && !result.TimedOut)
        {
            return null;
        }

        Ensure(result, $"read which branch the HEAD of '{directory}' is on");

        return result.StandardOutput.Trim();
    }

    /// <summary>The commits the history of the repository at <paramref name="directory"/> stops at; none where it holds every one.</summary>
    private async Task<IReadOnlyList<string>> ListShallowAsync(string directory, CancellationToken cancellationToken)
        => ReadShallow(await GitPathAsync(directory, "shallow", "find where git records where history stops", cancellationToken).ConfigureAwait(false));

    private static List<string> ReadShallow(string path)
    {
        try
        {
            return File.Exists(path)
                ? [.. File.ReadAllLines(path).Select(line => line.Trim()).Where(line => line.Length > 0)]
                : [];
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"Could not read '{path}', where git records where history stops: {ex.Message.TrimEnd('.')}.");
        }
    }

    /// <summary>
    /// Makes <paramref name="path"/> record <paramref name="commits"/> as where history stops, as git writes it: whole,
    /// under the lock git itself takes, and not at all where there are none.
    /// </summary>
    private static void WriteShallow(string path, IReadOnlyCollection<string> commits)
    {
        var held = path + ".lock";
        FileStream writing;

        try
        {
            writing = new FileStream(held, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (IOException) when (File.Exists(held))
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"Could not write '{path}', where git records where history stops: '{held}' is there, which a git at work in "
                + "that repository holds while it writes it, and one that was stopped leaves behind. Once no git runs there, "
                + "remove it and sync again.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"Could not write '{path}', where git records where history stops: {ex.Message.TrimEnd('.')}.");
        }

        try
        {
            using (writing)
            {
                writing.Write(Encoding.ASCII.GetBytes(string.Concat(commits.Order(StringComparer.Ordinal).Select(commit => commit + "\n"))));
            }

            if (commits.Count == 0)
            {
                File.Delete(path);
                File.Delete(held);
            }
            else
            {
                File.Move(held, path, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The lock was this write's own, and is not left to be taken for another git's - or is named, where it is.
            writing.Dispose();

            var left = DiscardFile(held) is { } why
                ? $" '{held}', which this write made, could not be removed either ({why}): remove it once no git runs there."
                : string.Empty;

            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"Could not write '{path}', where git records where history stops: {ex.Message.TrimEnd('.')}.{left}");
        }
    }

    /// <summary>Removes the file at <paramref name="path"/>, where it is there.</summary>
    /// <returns>Why it could not be removed, or <see langword="null"/> where it was, or was not there.</returns>
    private static string? DiscardFile(string path)
    {
        try
        {
            File.Delete(path);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message.TrimEnd('.');
        }
    }

    /// <summary>Whether the repository holds <paramref name="commit"/>, and as much behind it as is asked.</summary>
    private async Task<bool> HoldsAsync(
        string directory,
        string commit,
        bool whole,
        string? head,
        IReadOnlyList<string> stopsShortAt,
        CancellationToken cancellationToken)
    {
        if ((await HeldAsync(directory, [commit], cancellationToken).ConfigureAwait(false)).Count == 0)
        {
            return false;
        }

        // What HEAD names, the repository holds: git keeps it so. Anything else it holds may be part of something never
        // finished, and is walked.
        var atHead = string.Equals(head, commit, StringComparison.Ordinal);

        if (!whole)
        {
            return atHead || await ConnectedAsync(directory, ["--no-walk", commit], cancellationToken).ConfigureAwait(false);
        }

        return (atHead || await ConnectedAsync(directory, [commit, "--not", "--all"], cancellationToken).ConfigureAwait(false))
            && (await ReachedAsync(directory, commit, stopsShortAt, cancellationToken).ConfigureAwait(false)).Count == 0;
    }

    /// <summary>Whether a pack may leave out what the repository's own <paramref name="head"/> holds.</summary>
    private async Task<bool> HeadHeldAsync(
        string directory,
        string head,
        bool whole,
        IReadOnlyList<string> stopsShortAt,
        CancellationToken cancellationToken)
        => whole
            ? (await ReachedAsync(directory, head, stopsShortAt, cancellationToken).ConfigureAwait(false)).Count == 0
            : await ConnectedAsync(directory, ["--no-walk", head], cancellationToken).ConfigureAwait(false);

    /// <summary>Whether the repository holds every object <paramref name="revisions"/> select, as <c>git rev-list --objects</c> walks them.</summary>
    private async Task<bool> ConnectedAsync(string directory, IReadOnlyList<string> revisions, CancellationToken cancellationToken)
        => (await RunAsync(directory, ["rev-list", "--objects", "--quiet", .. revisions], cancellationToken: cancellationToken).ConfigureAwait(false)).Succeeded;

    /// <summary>Which of <paramref name="commits"/>, each one the repository's history stops at, the history of <paramref name="from"/> reaches.</summary>
    private async Task<IReadOnlyList<string>> ReachedAsync(string directory, string from, IReadOnlyList<string> commits, CancellationToken cancellationToken)
    {
        if (commits.Count == 0)
        {
            return [];
        }

        // Each is walked as a commit with no parent, so what git lists is those of them the history does not reach.
        var listed = await RunCoreAsync(
                directory,
                ["rev-list", "--stdin"],
                echoOutput: false,
                untranslated: false,
                indexFile: null,
                string.Concat(commits.Select(commit => commit + "\n")) + "^" + from + "\n",
                cancellationToken)
            .ConfigureAwait(false);

        Ensure(listed, $"walk the history behind {from}");

        var beyond = listed.OutputLines.ToHashSet(StringComparer.Ordinal);

        return [.. commits.Where(commit => !beyond.Contains(commit))];
    }

    /// <summary>Which of <paramref name="ids"/> the repository holds as commits.</summary>
    private async Task<IReadOnlySet<string>> HeldAsync(string directory, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        var held = new HashSet<string>(StringComparer.Ordinal);

        if (ids.Count == 0)
        {
            return held;
        }

        var checkedFor = await RunCoreAsync(
                directory,
                ["cat-file", "--batch-check"],
                echoOutput: false,
                untranslated: false,
                indexFile: null,
                string.Concat(ids.Select(id => id + "\n")),
                cancellationToken)
            .ConfigureAwait(false);

        Ensure(checkedFor, $"look for {ids.Count} commit(s) in '{directory}'");

        // '<object id> <type> <size>' for one it holds; '<name> missing' for one it does not.
        foreach (var line in checkedFor.OutputLines)
        {
            if (line.Split(' ') is [var id, "commit", _])
            {
                held.Add(id);
            }
        }

        return held;
    }

    /// <summary>
    /// Which of <paramref name="candidates"/> the repository's history stops at: each it holds, a parent of which it
    /// does not, in the order of their names.
    /// </summary>
    private async Task<IReadOnlyList<string>> BoundaryAsync(string directory, IReadOnlyCollection<string> candidates, CancellationToken cancellationToken)
    {
        var asked = candidates.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        if (asked.Count == 0)
        {
            return [];
        }

        // Each commit as its object names its parents: asked of git any other way, one recorded as where history stops
        // is answered as having none.
        var read = await RunForBytesAsync(
                directory,
                ["cat-file", "--batch"],
                cancellationToken,
                string.Concat(asked.Select(id => id + "^{commit}\n")))
            .ConfigureAwait(false);

        Ensure(read, $"read where the history of '{directory}' stops");

        var answers = new BatchAnswers(read.StandardOutput);
        var parents = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var id in asked)
        {
            if (answers.Next() is { Type: "commit" } commit)
            {
                parents[id] = ParentsNamedBy(commit.Content);
            }
        }

        var held = await HeldAsync(directory, [.. parents.Values.SelectMany(named => named).Distinct(StringComparer.Ordinal)], cancellationToken)
            .ConfigureAwait(false);

        return [.. asked.Where(id => parents.TryGetValue(id, out var named) && named.Any(parent => !held.Contains(parent)))];
    }

    /// <summary>The parents a commit's object names, in its header: each line of it before the first empty one that names one.</summary>
    private static List<string> ParentsNamedBy(string commit)
    {
        const string Parent = "parent ";

        var parents = new List<string>();

        foreach (var line in commit.Split('\n'))
        {
            if (line.Length == 0)
            {
                break;
            }

            if (line.StartsWith(Parent, StringComparison.Ordinal))
            {
                parents.Add(line[Parent.Length..].Trim());
            }
        }

        return parents;
    }
}
