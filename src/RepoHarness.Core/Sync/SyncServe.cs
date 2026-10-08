using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Sync;

/// <summary>
/// The operations one side of a sync asks the other to perform.
/// </summary>
/// <remarks>
/// A sync is the same conversation whichever host is on the far side, so the operations are named
/// once here and served by one command. The payload of a write travels inside the request the host
/// agent already carries on standard input, never on a command line: a command line is bounded, and
/// the words a remote shell reads literally are a small set that file content would leave at once.
/// </remarks>
public static class SyncServe
{
    /// <summary>The hidden command that serves these operations on a host.</summary>
    public const string CommandName = "sync-serve";

    /// <summary>Reports what the copy holds, as a manifest.</summary>
    public const string Manifest = "manifest";

    /// <summary>
    /// Reports whether the copy's root exists, whether the harness created it and whether its last sync finished, and
    /// what configuration it holds.
    /// </summary>
    public const string Inspect = "inspect";

    /// <summary>
    /// Creates the copy's root, with its parents, and marks it as the harness's: complete, a takeover begun, or a sync
    /// begun. Marking a copy already there keeps how it came to be.
    /// </summary>
    public const string Create = "create";

    /// <summary>Makes the copy a git repository, which the harness there needs to find anything.</summary>
    public const string InitRepository = "init-repository";

    /// <summary>What mark a <see cref="Create"/> request asks for, spelled as the enum's own name.</summary>
    /// <param name="arguments">The request's arguments, the copy's root first.</param>
    /// <exception cref="HarnessException">The request names a mark this build cannot serve.</exception>
    /// <remarks>
    /// A request naming no mark is a complete copy: that is what every request meant before a mark
    /// was carried, and reading it as anything else would turn a finished copy into one that still
    /// needs somebody's permission.
    /// <para>
    /// A mark that is spelled and is not one of those a copy can carry is refused rather than
    /// read as complete. <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> answers yes to
    /// any number, so <c>"7"</c> and <c>"0"</c> both parse - to no member, and to None - and writing
    /// either down would fail as a defect in this tool rather than name the cause. It means the two
    /// ends are different builds, which the version check should already have refused — the same
    /// reason an unknown operation is named rather than passed over.
    /// </para>
    /// </remarks>
    public static CopyMark MarkIn(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.Count <= 1 || arguments[1].Length == 0)
        {
            return CopyMark.Complete;
        }

        return Enum.TryParse<CopyMark>(arguments[1], ignoreCase: false, out var mark)
            && mark is CopyMark.Complete or CopyMark.AdoptionStopped or CopyMark.Unfinished
                ? mark
                : throw new HarnessException(
                    HarnessExit.UsageError,
                    $"'{arguments[1]}' is not a mark this build can record, so how '{arguments[0]}' "
                    + "came to be, or whether its last sync finished, would be written down wrong. The two ends are "
                    + "different builds.");
    }

    /// <summary>Writes one file into the copy.</summary>
    public const string Write = "write";

    /// <summary>Writes several files into the copy, in one request.</summary>
    /// <remarks>
    /// One request is one session on a host reached over ssh, and a session costs what opening one costs:
    /// a connection, an authentication, and whatever the far side's login profile does. Measured on a
    /// consumer's first sync of a worktree's copy, a file at a time opened 2,446 of them and ran 1,136
    /// seconds before the host slept mid-sync and the legs were left unavailable. A tree is thousands of
    /// files, and a copy per worktree makes a first full sync the ordinary case rather than a rare one.
    /// </remarks>
    public const string WriteMany = "write-many";

    /// <summary>
    /// The most content one batched write carries, in bytes, counted before encoding.
    /// </summary>
    /// <remarks>
    /// Both ends hold a batch whole - encoded here, decoded there - so this bounds the memory a sync
    /// spends at once, where <see cref="LargestFile"/> bounds only what one file may be. It is a budget
    /// somebody chose, not a limit of the format: large enough that a tree of ordinary source files
    /// crosses in tens of requests rather than thousands, and small enough to be unremarkable on the
    /// smallest machine a leg runs on. A file larger than this crosses in a batch of its own, since a
    /// batch always carries at least one file.
    /// </remarks>
    public const long LargestBatch = 8L * 1024 * 1024;

    /// <summary>
    /// The most files one batched write carries, however small they are, so that the overhead of a
    /// request is amortised without a batch of tiny files growing unbounded in entries.
    /// </summary>
    public const int MostFilesInABatch = 512;

    /// <summary>Makes the copy's git index hold exactly the files the sync placed there.</summary>
    public const string Index = "index";

    /// <summary>Deletes one file from the copy.</summary>
    public const string Delete = "delete";

    /// <summary>Removes directories of the copy that a deletion left empty.</summary>
    public const string Prune = "prune";

    /// <summary>Reads one file out of the copy.</summary>
    public const string Read = "read";

    /// <summary>Removes a whole copy the harness made, as deleting the worktree it holds asks.</summary>
    public const string RemoveCopy = "remove-copy";

    /// <summary>
    /// Lists the worktree copies kept beside the main copy its root names, each with what its marker says and how
    /// much its files hold, as listing a repository's worktrees with their hosts asks.
    /// </summary>
    public const string ListCopies = "list-copies";

    /// <summary>
    /// Removes the mutation workers kept beside the tree its root names, whether that tree is still there or not, as
    /// removing the tree's copy asks first, and a clean of a leg whose copy is gone: <c>remove-workers &lt;root&gt;</c>,
    /// and <see cref="MeasureOnly"/> after it to say what would go and remove nothing.
    /// </summary>
    public const string RemoveWorkers = "remove-workers";

    /// <summary>What follows the root where <see cref="RemoveWorkers"/> is only to measure.</summary>
    public const string MeasureOnly = "measure";

    /// <summary>Whether a <see cref="RemoveWorkers"/> request asks only to measure.</summary>
    /// <param name="arguments">The request's arguments, the tree's root first.</param>
    /// <exception cref="HarnessException">
    /// The request carries something else after its root: the two ends are different builds, and one that removed where
    /// the other asked for something it does not know would remove what nobody asked it to.
    /// </exception>
    public static bool MeasuresOnly(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.Count <= 1)
        {
            return false;
        }

        return string.Equals(arguments[1], MeasureOnly, StringComparison.Ordinal)
            ? true
            : throw new HarnessException(
                HarnessExit.UsageError,
                $"sync operation '{RemoveWorkers}' takes '{MeasureOnly}' after its root, or nothing; it was given '{arguments[1]}'.");
    }

    /// <summary>
    /// The largest file one request can carry, in bytes.
    /// </summary>
    /// <remarks>
    /// A file crosses whole, inside one request, encoded as base64 - which is a third longer again
    /// and is one string, so it cannot be longer than <see cref="int.MaxValue"/>. That is where
    /// this number comes from; it is not a policy anybody chose, and no configuration moves it.
    /// </remarks>
    public const long LargestFile = (int.MaxValue / 4) * 3L;

    /// <summary>
    /// Refuses a file too large to cross whole, by name and with its size, rather than leaving it
    /// to run out of memory.
    /// </summary>
    /// <param name="length">The file's size in bytes.</param>
    /// <param name="relativePath">The file, relative to the tree root.</param>
    /// <param name="host">The host it was going to or coming from.</param>
    /// <exception cref="HarnessException">It is larger than <see cref="LargestFile"/>.</exception>
    /// <remarks>
    /// Left to throw, this is an <see cref="OutOfMemoryException"/>, which every command reports as
    /// a defect in the tool. A build output of that size is ordinary, and "the harness has a bug"
    /// is the one reading of it that sends somebody nowhere useful.
    /// </remarks>
    public static void RefuseAFileTooLargeToCarry(long length, string relativePath, string host)
    {
        if (length <= LargestFile)
        {
            return;
        }

        throw new HarnessException(HarnessExit.CommandFailed, TooLargeToCarry(length, relativePath, host));
    }

    /// <summary>Why one file could not cross.</summary>
    /// <param name="length">The file's size in bytes, or -1 when it is not known.</param>
    /// <param name="relativePath">The file, relative to the tree root.</param>
    /// <param name="host">The host it was going to or coming from.</param>
    public static string TooLargeToCarry(long length, string relativePath, string host)
    {
        var size = length < 0
            ? "is too large"
            : $"is {length.ToString(CultureInfo.InvariantCulture)} bytes, past the "
                + $"{LargestFile.ToString(CultureInfo.InvariantCulture)} one request can hold";

        return $"'{relativePath}' {size}: a file crosses to and from {host} whole, inside one "
            + "request. Keep the smaller thing a later step actually reads - a packaged build "
            + "rather than a build tree - or put what has to cross somewhere both machines already "
            + "reach.";
    }

    /// <summary>How an answer is written, and read back, so both ends agree without guessing.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // An enum crosses as its name, the same spelling a request carries it in. As a number it
        // would be two spellings of one thing on one wire, and its meaning would depend on the
        // order the members happen to be declared in — so inserting one would silently change what
        // every older answer means. A name this build does not know fails the read, which is what
        // the two ends being different builds should do.
        Converters = { new JsonStringEnumConverter() },

        // A shape one end does not recognise is a hard failure rather than silent data loss, as it
        // is everywhere else this tool reads JSON.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>
    /// The line an answer is written on, marked so it is told apart from anything else the command
    /// prints. Without the mark a diagnostic written to the same stream would be parsed as the answer.
    /// </summary>
    public const string AnswerPrefix = "sync-serve-answer ";

    /// <summary>The files of a batched write, as the request carries them.</summary>
    /// <param name="files">The files, each with its path and its base64 content.</param>
    public static string Carry(IReadOnlyList<SyncFileWrite> files) => JsonSerializer.Serialize(files, JsonOptions);

    /// <summary>The files a batched write carries, read back.</summary>
    /// <param name="carried">What <see cref="Carry"/> wrote.</param>
    /// <exception cref="HarnessException">The request is in a shape this build cannot read.</exception>
    public static IReadOnlyList<SyncFileWrite> Carried(string carried)
    {
        ArgumentNullException.ThrowIfNull(carried);

        try
        {
            // A payload of 'null' reads as no batch at all, which is the very thing the refusal below
            // exists to prevent: written as nothing and answered as though every file had crossed.
            return JsonSerializer.Deserialize<IReadOnlyList<SyncFileWrite>>(carried, JsonOptions)
                ?? throw new JsonException("the files to write are null");
        }
        catch (JsonException ex)
        {
            // Refused rather than read as none: a batch read as empty would write nothing and answer
            // as though every file in it had crossed, and the sync would go on to call the copy current.
            throw new HarnessException(
                HarnessExit.UsageError,
                $"The files to write arrived in a shape this build cannot read: {ex.Message}. The two ends "
                + "are different builds.");
        }
    }

    /// <summary>The paths an index request carries.</summary>
    /// <param name="paths">Every file the copy holds from the sync, relative to its root.</param>
    public static string CarryPaths(IReadOnlyList<string> paths) => JsonSerializer.Serialize(paths, JsonOptions);

    /// <summary>The paths an index request carries, read back.</summary>
    /// <param name="carried">What <see cref="CarryPaths"/> wrote.</param>
    /// <exception cref="HarnessException">The request is in a shape this build cannot read.</exception>
    /// <remarks>
    /// Refused rather than read as none, as a batch of files is: an index made to hold nothing would
    /// unstage every file the copy has, and every build there would then fingerprint nothing again.
    /// </remarks>
    public static IReadOnlyList<string> CarriedPaths(string carried)
    {
        ArgumentNullException.ThrowIfNull(carried);

        try
        {
            var paths = JsonSerializer.Deserialize<IReadOnlyList<string>>(carried, JsonOptions)
                ?? throw new JsonException("the paths to index are null");

            return paths.Any(string.IsNullOrWhiteSpace)
                ? throw new JsonException("a path to index is blank")
                : paths;
        }
        catch (JsonException ex)
        {
            throw new HarnessException(
                HarnessExit.UsageError,
                $"The files to index arrived in a shape this build cannot read: {ex.Message}. The two ends "
                + "are different builds.");
        }
    }

    /// <summary>Writes an answer for the other end to read.</summary>
    /// <typeparam name="T">The answer's shape.</typeparam>
    /// <param name="answer">The answer.</param>
    public static string Answer<T>(T answer) => AnswerPrefix + JsonSerializer.Serialize(answer, JsonOptions);

    /// <summary>Reads an answer the other end wrote, or null when the line is not one.</summary>
    /// <typeparam name="T">The answer's shape.</typeparam>
    /// <param name="line">A line the far side printed.</param>
    /// <exception cref="HarnessException">The line is an answer this build cannot read.</exception>
    public static T? ReadAnswer<T>(string line)
        where T : class
    {
        if (line is null || !line.StartsWith(AnswerPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(line[AnswerPrefix.Length..], JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"The far side answered in a shape this build cannot read: {ex.Message}");
        }
    }
}

/// <summary>What the far side holds, as a manifest.</summary>
/// <param name="Entries">Every transferable file in the copy.</param>
public sealed record SyncManifestAnswer(IReadOnlyList<SyncEntry> Entries)
{
    /// <summary>
    /// Every link the far side's walk refused to follow, by relative path. Carried because it is
    /// the only way the asking machine can learn of them: every host a sync reaches is a far side,
    /// so a manifest that drops these leaves the list an adoption prints silent about exactly the
    /// paths no plan can speak for.
    /// </summary>
    /// <remarks>
    /// An answer from a build that did not send these leaves it empty rather than failing. Missing
    /// is legal here, unknown is not — <see cref="SyncServe.JsonOptions"/> refuses a member it does not know.
    /// </remarks>
    public IReadOnlyList<string> Links { get; init; } = [];
}

/// <summary>One directory a deletion emptied, and what became of it.</summary>
/// <remarks>
/// Built through <see cref="Removed"/> and <see cref="Kept"/> rather than by naming both fields, so
/// the two states that mean something are the only two that can be written. Removed-with-names and
/// kept-with-nothing are both readable as a sentence and neither is true of anything.
/// <para>
/// <see cref="Held"/> defaults to a list for the same reason <see cref="SyncManifestAnswer.Links"/>
/// does: a far side answering without the member at all deserialises it as null, and null here is
/// dereferenced while composing the warning that names what kept the directory.
/// </para>
/// </remarks>
public sealed record EmptiedDirectory
{
    /// <summary>Its path, relative to the copy's root.</summary>
    public required string Path { get; init; }

    /// <summary>Whether it was removed.</summary>
    public bool Removed { get; init; }

    /// <summary>
    /// What was still in it when it was not, by name, so a reader can see what kept it. Empty for
    /// one that went, and never null however the far side spelled its answer.
    /// </summary>
    public IReadOnlyList<string> Held { get; init; } = [];

    /// <summary>A directory the deletion emptied and this removed.</summary>
    /// <param name="path">Its path, relative to the copy's root.</param>
    public static EmptiedDirectory Gone(string path) => new() { Path = path, Removed = true };

    /// <summary>A directory that stayed, and what was still in it.</summary>
    /// <param name="path">Its path, relative to the copy's root.</param>
    /// <param name="held">What kept it, by name.</param>
    public static EmptiedDirectory Kept(string path, IReadOnlyList<string> held) =>
        new() { Path = path, Removed = false, Held = held };
}

/// <summary>What the far side did with the directories a deletion emptied.</summary>
/// <param name="Directories">One entry per directory considered.</param>
public sealed record SyncPruneAnswer(IReadOnlyList<EmptiedDirectory> Directories);

/// <summary>What removing a copy found there, and so did.</summary>
/// <param name="Removal">What was at the path.</param>
public sealed record SyncRemoveAnswer(CopyRemoval Removal);

/// <summary>What removing a copy found at its path, and so did.</summary>
public enum CopyRemoval
{
    /// <summary>
    /// A copy the harness made, or a directory holding nothing at all - what a removal whose last step failed
    /// leaves: removed.
    /// </summary>
    Removed,

    /// <summary>Nothing: there was nothing to remove.</summary>
    Absent,

    /// <summary>A directory the harness took over, somebody's before it was a copy: left where it is.</summary>
    Adopted,

    /// <summary>A directory holding no mark of the harness's: left where it is.</summary>
    NotACopy,
}

/// <summary>What removing the mutation workers kept beside a tree did.</summary>
/// <param name="Workers">Each worker removed, and each left.</param>
public sealed record SyncWorkersAnswer(WorkersRemoval Workers);

/// <summary>
/// What removing the mutation workers kept beside a tree did or, asked only to measure, would do: each copy of the
/// family a sync made is removed, with what an unfinished removal of one left aside, and every other directory of the
/// family is left, saying why.
/// </summary>
/// <param name="Removed">Each worker removed, or that would be, with what its files held, in the order of their paths.</param>
/// <param name="Left">Each directory of the family left where it is, and why.</param>
public sealed record WorkersRemoval(IReadOnlyList<WorkerRemoved> Removed, IReadOnlyList<WorkerLeft> Left)
{
    /// <summary>No worker beside the tree, so nothing removed and nothing left.</summary>
    public static WorkersRemoval None { get; } = new([], []);

    /// <summary>
    /// Whether the removal was stopped before every worker had been dealt with: what is here is what it had done by
    /// then, and a worker it names nowhere was not reached.
    /// </summary>
    public bool Interrupted { get; init; }

    /// <summary>What the workers removed held together.</summary>
    [JsonIgnore]
    public long Bytes => Removed.Sum(worker => worker.Bytes);

    /// <summary>
    /// What became of the workers, as the one verdict every command that removes them reads: refused-locked where a
    /// sweep still running holds one, which ends by waiting; failed where one could not be removed, or told; stopped
    /// where the removal was stopped before each had been dealt with; and passed where none of that is - the most
    /// fundamental of them where several are. A command's own code for the first two is the verdict's.
    /// </summary>
    [JsonIgnore]
    public LegVerdict Verdict => Verdicts.Worst([.. Left.Select(worker => Of(worker.As)), Interrupted ? LegVerdict.Stopped : LegVerdict.Passed]);

    /// <summary>
    /// The worker that keeps its tree from being removed with the others, or <see langword="null"/> where none does:
    /// the first of the kind <see cref="Verdict"/> ranks first, so what is said of it is what the verdict says.
    /// </summary>
    [JsonIgnore]
    public WorkerLeft? Kept => Left.Where(worker => Of(worker.As) != LegVerdict.Passed).MinBy(worker => Verdicts.Rank(Of(worker.As)));

    // What one worker left says of its tree: held, it is in use; not removed, its removal failed; somebody's, nothing.
    private static LegVerdict Of(WorkerLeftAs left)
        => left switch
        {
            WorkerLeftAs.Held => LegVerdict.RefusedLocked,
            WorkerLeftAs.NotRemoved => LegVerdict.Failed,
            _ => LegVerdict.Passed,
        };
}

/// <summary>One mutation worker removed, or that would be.</summary>
/// <param name="Path">Where it was, spelt from the tree it was asked about.</param>
/// <param name="Bytes">How many bytes its files held.</param>
public sealed record WorkerRemoved(string Path, long Bytes);

/// <summary>One directory of a tree's mutation workers left where it is.</summary>
/// <param name="Path">Where it is.</param>
/// <param name="Why">Why it was left, as a line says it.</param>
/// <param name="As">What it was left as, which says what would have it go.</param>
public sealed record WorkerLeft(string Path, string Why, WorkerLeftAs As)
{
    /// <summary>Why it was left, naming it: its path first, where what is said of it does not name it already.</summary>
    public string Told() => Why.Contains(Path, StringComparison.Ordinal) ? Why : $"'{Path}': {Why}";
}

/// <summary>What a directory of a tree's mutation workers was left as, which says whether asking again removes it.</summary>
public enum WorkerLeftAs
{
    /// <summary>Somebody's: nothing the harness may remove, and nothing that keeps its tree.</summary>
    Somebodys,

    /// <summary>Held: a sweep still running holds it, and asking again removes it once the sweep has ended.</summary>
    Held,

    /// <summary>
    /// Not removed: its removal failed or was stopped part way, or what it is could not be told - its marker cannot be
    /// read. Nothing holds it that waiting would end: asking again removes it, or says whose it is, once that is put right.
    /// </summary>
    NotRemoved,
}

/// <summary>The worktree copies a host keeps beside its main copy.</summary>
/// <param name="Copies">One for each, in the order of their names.</param>
public sealed record SyncCopiesAnswer(IReadOnlyList<HostCopyFound> Copies);

/// <summary>One worktree copy a host keeps beside its main copy, as the host found it.</summary>
/// <param name="Name">The name it is kept under: what follows <see cref="HostCopies.WorktreeSuffix"/>.</param>
/// <param name="Path">
/// Where it is, spelt from the repositoryPath the host was asked about, as a sync spells the copy it records, so
/// that the two compare.
/// </param>
/// <param name="Origin">What its marker says of how it came to be.</param>
/// <param name="Bytes">How many bytes its files hold.</param>
public sealed record HostCopyFound(string Name, string Path, CopyOrigin Origin, long Bytes)
{
    /// <summary>The machine its marker says made it, where it has a marker that could be read.</summary>
    public string? CreatedBy { get; init; }

    /// <summary>When its marker says it was made, where it has a marker that could be read.</summary>
    public string? CreatedUtc { get; init; }

    /// <summary>Why its marker could not be read, where it could not.</summary>
    public string? Problem { get; init; }
}

/// <summary>How a copy came to be, as its marker says: what decides whether removing it takes it.</summary>
public enum CopyOrigin
{
    /// <summary>The harness made it, so removing it takes it.</summary>
    Made,

    /// <summary>The harness took over a directory that was already there, so removing it leaves it.</summary>
    TakenOver,

    /// <summary>Nothing there says the harness made it, so removing it leaves it unless it holds nothing at all.</summary>
    Unmarked,

    /// <summary>Its marker is there and cannot be read, so removing it is refused.</summary>
    Unreadable,
}

/// <summary>What the far side's root looks like.</summary>
/// <param name="Exists">Whether the root directory is there.</param>
/// <param name="Mark">What the harness has recorded about it.</param>
/// <param name="Configuration">
/// What the configuration the copy holds is, by content, so a sync knows whether placing its own changes the copy;
/// <see langword="null"/> where it holds none, or one that cannot be read.
/// </param>
public sealed record SyncInspectAnswer(bool Exists, CopyMark Mark, string? Configuration = null);

/// <summary>What a copy's marker says about how it came to be, and whether the last sync of it finished.</summary>
public enum CopyMark
{
    /// <summary>There is no marker: whatever is there, this tool did not make it.</summary>
    None,

    /// <summary>A copy this tool made, or finished taking over, whose last sync finished.</summary>
    Complete,

    /// <summary>
    /// A copy this tool began taking over and did not finish. Neither the checkout somebody had nor
    /// a copy of the source: some of what was there is already gone, and what is left is not what a
    /// plan would now report, because a plan can only see what survived.
    /// </summary>
    AdoptionStopped,

    /// <summary>
    /// A copy this tool made or took over, which a sync began writing and has not finished - stopped,
    /// or still writing: part of the tree that sync was given and part of the one before, which no run
    /// began with. Still this tool's own, which the next sync puts right; until then a run on what is
    /// staged makes its legs inputs-moved, and a carry writes nothing into it.
    /// </summary>
    Unfinished,
}

/// <summary>One file's bytes, base64 encoded so they survive a line of text intact.</summary>
/// <param name="Content">The file's bytes.</param>
/// <param name="ContentHash">
/// The SHA-256 the far side computed of those bytes, before they were encoded and sent. Carried so
/// the machine that asked can check what arrived against what was read, rather than against itself:
/// a hash taken here of the bytes that arrived agrees with them whatever happened on the way.
/// </param>
public sealed record SyncFileAnswer(string Content, string ContentHash);

/// <summary>One file a batched write carries.</summary>
/// <param name="Path">Where it goes, relative to the copy's root.</param>
/// <param name="Content">Its bytes, base64 encoded so they survive a line of text intact.</param>
public sealed record SyncFileWrite(string Path, string Content)
{
    /// <summary>The bytes this carries, decoded.</summary>
    /// <exception cref="HarnessException">The content is not base64, so the two ends are different builds.</exception>
    /// <remarks>
    /// Decoded here rather than where the batch is served, so that a file which did not survive the journey
    /// is named and said as a transfer this build cannot read - the reasoning
    /// <see cref="SyncServe.TooLargeToCarry"/> already applies to a file too large. Left to the runtime it
    /// is a FormatException, which every command reports as a defect in this tool, naming neither the file
    /// nor the host, and one bad entry in a batch of hundreds would identify none of them.
    /// </remarks>
    public byte[] Bytes()
    {
        try
        {
            return Convert.FromBase64String(Content);
        }
        catch (FormatException ex)
        {
            throw new HarnessException(
                HarnessExit.UsageError,
                $"'{Path}' arrived in a shape this build cannot read: {ex.Message.TrimEnd('.')}. The two ends "
                + "are different builds.");
        }
    }
}
