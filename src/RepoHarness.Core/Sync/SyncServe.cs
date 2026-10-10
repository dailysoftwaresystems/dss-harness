using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Git;
using RepoHarness.Core.Hosts;
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
/// Each value an operation takes is an argument of its own, after <see cref="OperandsFollow"/> - a
/// batch's files each as its path then its content, an index's paths one by one - never a list inside
/// one argument, which the request would then carry as text escaped inside text.
/// </remarks>
public static class SyncServe
{
    /// <summary>The hidden command that serves these operations on a host.</summary>
    public const string CommandName = "sync-serve";

    /// <summary>
    /// What stands between an operation and the copy's root and everything after it: each of those is then read as what it
    /// is - a path starting with '@' as that path, not as a file of arguments to read in its place, and one starting with
    /// '-' as that path, not as an option.
    /// </summary>
    /// <remarks>
    /// Without it, a tree holding a file named '@notes' had its deletion on a host read the file 'notes' beside the copy
    /// and delete whatever path that named, and a file named '-v' was taken for the verbose option.
    /// </remarks>
    public const string OperandsFollow = "--";

    /// <summary>Reports what the copy holds, as a manifest.</summary>
    public const string Manifest = "manifest";

    /// <summary>
    /// Reports whether the copy's root exists, whether the harness created it and whether its last sync finished, what
    /// configuration it holds, and - where it is a git repository of its own - where its HEAD stands and what it holds
    /// of the history asked of it: <c>inspect &lt;root&gt;</c>, then, where a tree is synced, what <see cref="Asking"/> spells.
    /// </summary>
    public const string Inspect = "inspect";

    /// <summary>
    /// Creates the copy's root, with its parents, and marks it as the harness's: complete, a takeover begun, or a sync
    /// begun. Marking a copy already there keeps how it came to be.
    /// </summary>
    public const string Create = "create";

    /// <summary>
    /// Makes the copy a git repository, which the harness there needs to find anything:
    /// <c>init-repository &lt;root&gt;</c>, then how one made there names its objects, where the tree synced says.
    /// </summary>
    public const string InitRepository = "init-repository";

    /// <summary>How a repository names its objects, as an <see cref="InitRepository"/> request carries it; none where it carries none.</summary>
    /// <param name="arguments">The request's arguments, the copy's root first.</param>
    /// <exception cref="HarnessException">It is no such word: the two ends are different builds.</exception>
    public static string? ObjectFormatIn(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.Count <= 1)
        {
            return null;
        }

        // A word of letters and digits, as git's own are: it becomes part of an option on git's command line.
        return arguments[1].Length > 0 && arguments[1].All(char.IsAsciiLetterOrDigit)
            ? arguments[1]
            : throw new HarnessException(
                HarnessExit.UsageError,
                $"'{arguments[1]}' is not how a git repository names its objects. The two ends are different builds.");
    }

    /// <summary>
    /// Keeps a piece of a pack of git objects aside in the copy's repository until the whole of it is taken:
    /// <c>history-piece &lt;root&gt; &lt;pack&gt; &lt;bytes before it&gt; &lt;content&gt;</c>.
    /// </summary>
    public const string HistoryPiece = "history-piece";

    /// <summary>
    /// Takes the pack sent, where one was, and moves the copy's HEAD to the commit of the tree synced:
    /// <c>take-history &lt;root&gt; &lt;pack&gt; &lt;commit left out&gt;</c>, each of those two <see cref="Nothing"/> where
    /// there is none, then what <see cref="Asking"/> spells.
    /// </summary>
    public const string TakeHistory = "take-history";

    /// <summary>What stands where an operation takes a value and there is none: no object's name, and never empty.</summary>
    public const string Nothing = "-";

    /// <summary>
    /// What a request says of the history asked of a copy: the commit, how much behind it, how the tree's repository
    /// names its objects, then each commit that repository's own history stops at; <see cref="Nothing"/> for the commit
    /// where the tree has none yet, and nothing at all where what is synced is no repository's tree.
    /// </summary>
    /// <param name="wanted">What is asked, or <see langword="null"/> where nothing is.</param>
    public static IReadOnlyList<string> Asking(GitHistoryWanted? wanted)
        => wanted is null ? [] : [wanted.Commit ?? Nothing, wanted.Depth, wanted.ObjectFormat, .. wanted.Boundary];

    /// <summary>The history a request asks of a copy, as <see cref="Asking"/> spelled it from <paramref name="at"/> on.</summary>
    /// <param name="arguments">The request's arguments, the copy's root first.</param>
    /// <param name="at">Where the commit stands among them.</param>
    /// <returns>What is asked, or <see langword="null"/> where the request asks for none.</returns>
    /// <exception cref="HarnessException">
    /// It arrived in part, or names how much is asked in a word this build does not know: the two ends are different builds.
    /// </exception>
    /// <remarks>
    /// Refused rather than read as less: a history taken short of what was asked would leave the copy's HEAD where
    /// the sync then says it is not.
    /// </remarks>
    public static GitHistoryWanted? WantedIn(IReadOnlyList<string> arguments, int at)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.Count <= at)
        {
            return null;
        }

        if (arguments.Count < at + 3 || arguments[at + 1] is not (GitHistoryWanted.HeadOnly or GitHistoryWanted.Full))
        {
            throw new HarnessException(
                HarnessExit.UsageError,
                "The history asked of the copy arrived in a shape this build cannot read: a commit, then "
                + $"'{GitHistoryWanted.HeadOnly}' or '{GitHistoryWanted.Full}', then how its objects are named. The two ends are different builds.");
        }

        return new GitHistoryWanted(
            Given(arguments, at, Inspect),
            arguments[at + 1] == GitHistoryWanted.Full,
            arguments[at + 2],
            [.. arguments.Skip(at + 3)]);
    }

    /// <summary>The value at <paramref name="index"/> of a request, or <see langword="null"/> where it is <see cref="Nothing"/>.</summary>
    /// <param name="arguments">The request's arguments, the copy's root first.</param>
    /// <param name="index">Where the value stands.</param>
    /// <param name="operation">The operation, for the refusal.</param>
    /// <exception cref="HarnessException">The request is too short to hold it: the two ends are different builds.</exception>
    public static string? Given(IReadOnlyList<string> arguments, int index, string operation)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        return index < arguments.Count
            ? string.Equals(arguments[index], Nothing, StringComparison.Ordinal) ? null : arguments[index]
            : throw new HarnessException(
                HarnessExit.UsageError,
                $"sync operation '{operation}' needs {(index + 1).ToString(CultureInfo.InvariantCulture)} argument(s); it was given "
                + $"{arguments.Count.ToString(CultureInfo.InvariantCulture)}.");
    }

    /// <summary>How many bytes of a pack a piece starts after, as a <see cref="HistoryPiece"/> request carries it.</summary>
    /// <param name="text">The number, in digits.</param>
    /// <exception cref="HarnessException">It is no such number: the two ends are different builds.</exception>
    public static long OffsetIn(string text)
        => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
            ? offset
            : throw new HarnessException(
                HarnessExit.UsageError,
                $"'{text}' is not how many bytes of a pack came before a piece of it. The two ends are different builds.");

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

    /// <summary>Lists the files below one directory of the copy, each with its size, for a pull that names the directory.</summary>
    public const string List = "list";

    /// <summary>The most files a pull brings back from one directory it names.</summary>
    /// <remarks>
    /// A file read back crosses in a request of its own, and a request is a session on a host reached over ssh: a
    /// directory named is a step's kept outputs, tens of files, never a build tree. A budget somebody chose, as
    /// <see cref="LargestBatch"/> is, checked on the side that holds the directory, before a file of it is read.
    /// </remarks>
    public const int MostFilesPulledFromADirectory = 256;

    /// <summary>The most bytes the files of one directory a pull names hold together, checked as their number is.</summary>
    public const long LargestDirectoryPulled = 1024L * 1024 * 1024;

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

    /// <summary>The most characters one string holds, however much memory is free: 1,073,741,791.</summary>
    public const int LongestString = 0x3FFFFFDF;

    /// <summary>
    /// The room a request's line keeps beside the largest file it carries, in characters: for the file's path, the copy's
    /// root and the rest of the request.
    /// </summary>
    private const int AroundAFile = 1024 * 1024;

    /// <summary>
    /// The largest file one request can carry, in bytes.
    /// </summary>
    /// <remarks>
    /// A file crosses to a host whole, inside one request, encoded as base64 - which is a third longer again - and the
    /// far side reads that request as one line of text: one string, which holds at most <see cref="LongestString"/>
    /// characters, the rest of the line among them. That is where this number comes from; it is not a policy anybody
    /// chose, and no configuration moves it. Reckoned from <see cref="int.MaxValue"/>, it was twice what a line holds,
    /// and a file between the two was turned away only by this machine running out of memory encoding it, in words naming
    /// a limit it was under. A file read back crosses a piece to a line (<see cref="ContentLines"/>), and is held to the
    /// same size: whichever way it goes, the same file crosses, or is refused by name.
    /// </remarks>
    public const long LargestFile = (LongestString - AroundAFile) / 4 * 3L;

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

    /// <summary>
    /// The lines a file read on the far side follows its answer with, each carrying the next piece of its content as base64:
    /// marked as the answer is, so nothing else the command prints is taken for a piece of the file.
    /// </summary>
    /// <remarks>
    /// A line at a time, so the machine that asked decodes the file as it arrives and never holds it as text: as one line
    /// inside its answer, one 64 MiB file left the machine that read it holding 2 GiB, in copies of that line.
    /// </remarks>
    public const string ContentPrefix = "sync-serve-content ";

    /// <summary>The files a batched write carries: after the copy's root, each file's path, then its content's base64.</summary>
    /// <param name="arguments">The request's arguments, the copy's root first.</param>
    /// <exception cref="HarnessException">
    /// The request carries no file, or a path without its content: the two ends are different builds.
    /// </exception>
    /// <remarks>
    /// Refused rather than read as fewer files: a batch read short would write less than it was sent and answer as though
    /// every file in it had crossed, and the sync would go on to call the copy current.
    /// </remarks>
    public static IReadOnlyList<SyncFileWrite> CarriedFiles(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.Count < 3 || arguments.Count % 2 == 0)
        {
            throw new HarnessException(
                HarnessExit.UsageError,
                $"The files to write arrived in a shape this build cannot read: {(arguments.Count - 1).ToString(CultureInfo.InvariantCulture)} "
                + "argument(s) after the copy's root, where each file is its path then its content. The two ends are different builds.");
        }

        return [.. arguments.Skip(1).Chunk(2).Select(file => new SyncFileWrite(file[0], file[1]))];
    }

    /// <summary>
    /// The names a request carries after the copy's root - the paths a manifest withholds, the directories a prune empties -
    /// each in turn. An empty one names nothing to either: a path pattern that is empty matches no path, and a prune
    /// passes over an empty name.
    /// </summary>
    /// <param name="arguments">The request's arguments, the copy's root first.</param>
    public static IReadOnlyList<string> Named(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        return [.. arguments.Skip(1)];
    }

    /// <summary>The paths an index request carries: everything after the copy's root.</summary>
    /// <param name="arguments">The request's arguments, the copy's root first.</param>
    /// <exception cref="HarnessException">A path to index is blank: the two ends are different builds.</exception>
    /// <remarks>
    /// Refused rather than read as fewer, as a batch of files is: an index made to hold less than the copy does
    /// would unstage what it left out, and every build there would then fingerprint it again.
    /// </remarks>
    public static IReadOnlyList<string> CarriedPaths(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var paths = arguments.Skip(1).ToList();

        return paths.Any(string.IsNullOrWhiteSpace)
            ? throw new HarnessException(
                HarnessExit.UsageError,
                "The files to index arrived in a shape this build cannot read: a path to index is blank. The two ends are different builds.")
            : paths;
    }

    /// <summary>Writes an answer for the other end to read.</summary>
    /// <typeparam name="T">The answer's shape.</typeparam>
    /// <param name="answer">The answer.</param>
    public static string Answer<T>(T answer) => AnswerPrefix + JsonSerializer.Serialize(answer, JsonOptions);

    /// <summary>
    /// The lines carrying <paramref name="contents"/> after its answer, <see cref="HostAgentProtocol.CarriedPiece"/> bytes to
    /// a line.
    /// </summary>
    /// <param name="contents">A file's bytes.</param>
    public static IEnumerable<string> ContentLines(byte[] contents)
    {
        ArgumentNullException.ThrowIfNull(contents);

        for (var at = 0; at < contents.Length; at += HostAgentProtocol.CarriedPiece)
        {
            yield return ContentPrefix + Convert.ToBase64String(contents.AsSpan(at, Math.Min(HostAgentProtocol.CarriedPiece, contents.Length - at)));
        }
    }

    /// <summary>
    /// Decodes the piece of a file <paramref name="line"/> carries into <paramref name="destination"/>, and says how many
    /// bytes it was; nothing where the line is not one of a file's content lines.
    /// </summary>
    /// <param name="line">A line the far side printed after its answer.</param>
    /// <param name="destination">Where the rest of the file goes.</param>
    /// <param name="relativePath">The file, as the request named it.</param>
    /// <exception cref="HarnessException">
    /// The line is not base64, or carries more than the answer said the file holds: the two ends are different builds, or
    /// the file did not survive the journey.
    /// </exception>
    public static int ReadContentLine(string line, Span<byte> destination, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (!line.StartsWith(ContentPrefix, StringComparison.Ordinal))
        {
            return 0;
        }

        return Convert.TryFromBase64Chars(line.AsSpan(ContentPrefix.Length), destination, out var written)
            ? written
            : throw new HarnessException(
                HarnessExit.CommandFailed,
                $"'{relativePath}' arrived in a shape this build cannot read: a piece of it is not base64, or carries more than "
                + "the file was said to hold.");
    }

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

        // Read from bytes made here rather than from the text: read from text, the serializer rents a buffer three times
        // its length from the shared pool to hold its bytes, and the pool keeps it for good.
        var json = line.AsSpan(AnswerPrefix.Length);
        var utf8 = new byte[Encoding.UTF8.GetByteCount(json)];

        Encoding.UTF8.GetBytes(json, utf8);

        try
        {
            return JsonSerializer.Deserialize<T>(utf8, JsonOptions);
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
public sealed record SyncInspectAnswer(bool Exists, CopyMark Mark, string? Configuration = null)
{
    /// <summary>
    /// Where the HEAD of the copy's git repository stands, how that repository names its objects and what it holds of
    /// the history asked of it; <see langword="null"/> where the root is not the top of a repository of its own - one
    /// is then made for it, which holds nothing.
    /// </summary>
    public GitHistoryHeld? Repository { get; init; }
}

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

/// <summary>
/// One file read on the far side, whose bytes follow its answer as <see cref="SyncServe.ContentLines"/>, base64 encoded so
/// they survive a line of text intact.
/// </summary>
/// <param name="Length">How many bytes the file holds.</param>
/// <param name="ContentHash">
/// The SHA-256 the far side computed of those bytes, before they were encoded and sent. Carried so
/// the machine that asked can check what arrived against what was read, rather than against itself:
/// a hash taken here of the bytes that arrived agrees with them whatever happened on the way.
/// </param>
public sealed record SyncFileAnswer(long Length, string ContentHash);

/// <summary>What one directory of a copy holds, as a pull that names it brings it back.</summary>
/// <param name="Files">Every file below it, by path relative to the copy's root, in the order of their paths.</param>
public sealed record SyncDirectoryListing(IReadOnlyList<SyncListedFile> Files)
{
    /// <summary>
    /// Every link below it, which the walk neither followed nor read - a directory's with a trailing separator - by path
    /// relative to the copy's root: named, since what a link leads to is nothing the copy holds, and never brought back.
    /// </summary>
    public IReadOnlyList<string> Links { get; init; } = [];
}

/// <summary>One file a directory of a copy holds.</summary>
/// <param name="Path">Where it is, relative to the copy's root, with forward separators.</param>
/// <param name="Length">How many bytes it holds.</param>
public sealed record SyncListedFile(string Path, long Length);

/// <summary>What a pull brought back, or would.</summary>
/// <param name="Files">Each file, relative to the copy's root: the files named, and every file below each directory named.</param>
/// <param name="Links">Each link below a directory named, passed over: never followed, and never brought back.</param>
public sealed record SyncPull(IReadOnlyList<string> Files, IReadOnlyList<string> Links)
{
    /// <summary>
    /// Why the pull stopped before every file had been brought, where it did; <see langword="null"/> where it brought
    /// them all. <see cref="Files"/> is then what crossed before it, each left where it was written.
    /// </summary>
    public SyncPullStop? Stopped { get; init; }

    /// <summary>Each file not brought back, where the pull stopped: the one it stopped at, then every one after it.</summary>
    public IReadOnlyList<string> Left { get; init; } = [];

    /// <summary>Whether <paramref name="path"/>, as a pull was given it, names a directory: it ends with a separator.</summary>
    /// <param name="path">A path a pull names.</param>
    public static bool NamesADirectory(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return path.EndsWith('/') || path.EndsWith('\\');
    }
}

/// <summary>What stopped a pull part way.</summary>
/// <param name="ExitCode">The code the pull fails with.</param>
/// <param name="Why">What failed, as the failure itself said it.</param>
public sealed record SyncPullStop(int ExitCode, string Why);

/// <summary>One file a write carries, as the far side reads it.</summary>
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
