using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Output;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Execution;

/// <summary>How much of a tree a run takes, because two kinds of work share one.</summary>
public enum LockScope
{
    /// <summary>
    /// The whole tree, taken by syncing it: a sync rewrites files every variant reads, so nothing
    /// may build or test while it runs.
    /// </summary>
    TreeExclusive,

    /// <summary>
    /// The tree shared with other variants, and this run's own variant exclusively: variants build
    /// side by side, each in its own build directory, but never while their sources are replaced.
    /// </summary>
    TreeShared,
}

/// <summary>Who holds a lock, in enough detail to name them and to tell whether they still exist.</summary>
/// <param name="Machine">The machine the holder runs on.</param>
/// <param name="ProcessId">The holder's process id.</param>
/// <param name="RunId">The holder's run id, which is also what a log path is scoped to.</param>
/// <param name="TakenUtc">When the lock was taken, for display; nothing is ordered by it.</param>
/// <param name="Command">What the holder is doing, so the reader knows what they are waiting for.</param>
/// <param name="ProcessStamp">
/// What tells that process from another that inherits its id, so a recycled id is not mistaken for a
/// live holder. Holds no clock, so a clock that steps cannot turn a live holder into a dead one. Left
/// out by a build before stamps, and where the platform would not say.
/// </param>
public sealed record LockHolder(
    string Machine,
    int ProcessId,
    string RunId,
    DateTimeOffset TakenUtc,
    string Command,
    string? ProcessStamp = null)
{
    /// <summary>
    /// The wall-clock start time a build before this one recorded here, kept only so such a file is
    /// still readable. Nothing decides anything from it: it is exactly the value that moves when the
    /// clock steps, which is why it stopped being what identifies a process.
    /// </summary>
    /// <remarks>
    /// Declared rather than skipped so that every other unknown member can be refused. Not written
    /// by this build: an entry it records carries a stamp instead. An entry it only keeps — another
    /// machine's, which it has no business rewriting — is written back as it was read, this member
    /// among it, so that machine's own build still finds what it wrote.
    /// </remarks>
    [JsonPropertyName("processStartedUtc")]
    public DateTimeOffset? LegacyStartedUtc { get; init; }
}

/// <summary>One entry in the lock file: what is held, how much of it, and by whom.</summary>
/// <param name="Host">The host the work runs on, as the command line names it.</param>
/// <param name="Tree">The tree on that host, as the lock keys it (<see cref="RunLock.Key"/>).</param>
/// <param name="Scope">How much of the tree the entry takes.</param>
/// <param name="Holder">Who holds it.</param>
/// <param name="Variant">The build variant, when the entry takes one; absent for a whole tree.</param>
public sealed record LockEntry(string Host, string Tree, LockScope Scope, LockHolder Holder, string? Variant = null)
{
    /// <summary>The entry as a refusal names it, said to be another machine's where it is, never naming that machine.</summary>
    /// <param name="identity">This process, which tells this machine from another.</param>
    public string Describe(IProcessIdentity identity)
        => identity.Describe(Holder.Machine, Holder.ProcessId, Holder.RunId, Holder.TakenUtc)
            + $", running '{Holder.Command}'{ProcessHolders.OlderBuildNote(Holder.ProcessStamp)}";
}

/// <summary>What a run asks to take.</summary>
public sealed record LockRequest
{
    /// <summary>The host the work runs on.</summary>
    public required string Host { get; init; }

    /// <summary>The tree on that host.</summary>
    public required string Tree { get; init; }

    /// <summary>The build variant, required for <see cref="LockScope.TreeShared"/>.</summary>
    public string? Variant { get; init; }

    /// <summary>How much of the tree to take.</summary>
    public required LockScope Scope { get; init; }

    /// <summary>
    /// Whether the tree is to be removed, with everything kept inside it: then an entry on that host for anything inside the
    /// tree - a sweep's key in a host's copy, kept in its <c>.mutations</c> - is in its way too, as an entry for the tree
    /// is. Recorded as the tree taken as <see cref="Scope"/> says, so it is what a run asking for the tree afterwards
    /// meets.
    /// </summary>
    public bool Removal { get; init; }

    /// <summary>The run taking it.</summary>
    public required RunId RunId { get; init; }

    /// <summary>What this run is doing, recorded for whoever is refused.</summary>
    public required string Command { get; init; }

    /// <summary>
    /// Whether to take a lock held by another machine. Always a human decision: liveness cannot be
    /// checked from here, so the only alternative to asking is guessing.
    /// </summary>
    public bool Force { get; init; }
}

/// <summary>
/// The run lock, <c>.harness-config/lock.json</c>, always in the main checkout.
/// </summary>
/// <remarks>
/// In the main checkout because a worktree has its own <c>.harness-config</c>, so a per-tree lock
/// would make two runs of the same leg invisible to each other. A held lock refuses immediately and
/// names its holder: silently blocking for hours is worse than a refusal somebody can act on. A tree on
/// this machine is keyed one way however it is reached (<see cref="Key"/>), when an entry is written and
/// whenever the lock is asked, so a tree reached through a link and the same tree named as git names it
/// are one key, whoever spells it.
/// </remarks>
public sealed class RunLock(IFileSystem fileSystem, IHarnessOutput output, IProcessIdentity identity)
{
    private readonly IProcessIdentity _identity = identity;

    /// <summary>The command name this reports under.</summary>
    public const string CommandName = "lock";

    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHarnessOutput _output = output;
    private readonly ConcurrentDictionary<string, MachineWideList<LockEntry>> _files = new(StringComparer.Ordinal);

    /// <summary>
    /// The lock file of <paramref name="layout"/>, read and changed by the rules every such list keeps. A shape this build
    /// does not recognise is refused; the one field an older build wrote and this one no longer uses is declared on the
    /// holder, so upgrading reads its own lock file rather than refusing it. A lock already held is refused at once: only
    /// a change of the file waits, and only for another process's change of it. One for each lock file for as long as
    /// this process runs, so a lock it gave back but could not take out of the file is left out of every later reading.
    /// </summary>
    private MachineWideList<LockEntry> File(HarnessLayout layout)
        => _files.GetOrAdd(
            Path.GetFullPath(layout.LockFile),
            lockFile => new(_fileSystem, lockFile, "The run lock", "Until it can be, one run cannot be told from another.", "Remove it once no run is using it."));

    /// <summary>
    /// Takes what <paramref name="request"/> asks for, or says which run holds it.
    /// </summary>
    /// <param name="layout">The repository, whose main checkout holds the lock file.</param>
    /// <param name="request">What to take.</param>
    /// <param name="cancellationToken">Stops the attempt.</param>
    /// <remarks>
    /// A lock another run holds is an answer, about one tree at one moment, and is returned as one: a
    /// caller that turns it into a verdict for the legs on that tree must not also turn into one a
    /// lock file nobody can use, which stops every run on every tree alike.
    /// </remarks>
    /// <exception cref="HarnessException">
    /// The lock file could not be read or updated, the request names no variant for a shared hold,
    /// or where a tree on this machine leads cannot be told (<see cref="Key"/>). Never a wait: a run
    /// blocked for hours on a lock cannot be told from one that hung.
    /// </exception>
    public Task<LockAttempt> TryAcquireAsync(
        HarnessLayout layout,
        LockRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Scope == LockScope.TreeShared && string.IsNullOrWhiteSpace(request.Variant))
        {
            throw new HarnessException(
                HarnessExit.Refused,
                "A shared lock takes the tree and one variant, and no variant was named; a whole tree is taken exclusively.");
        }

        var entry = Entry(request);

        var heldBy = File(layout).Update<(string? Said, LockEntry? Holder)?>((entries, afterwards) =>
        {
            var kept = Live(entries, request.Force ? existing => InTheWay(existing, entry, request.Removal) : null, afterwards);

            // A request that was refused writes nothing: the file is left as it was found.
            return kept.FirstOrDefault(existing => InTheWay(existing, entry, request.Removal)) is { } holder
                ? (null, ($"{Describe(entry)} is held by {holder.Describe(_identity)}{_identity.ElsewhereNote(holder.Holder.Machine)}.", holder))
                : ([.. kept, entry], null);
        });

        return Task.FromResult(heldBy is null
            ? new LockAttempt(new RunLockHandle(this, layout, entry), null)
            : new LockAttempt(null, heldBy.Value.Said, heldBy.Value.Holder));
    }

    /// <summary>
    /// Every live entry in the way of any of <paramref name="requests"/>, without taking anything: all of them, each
    /// once, in the file's order, each with the requests it is in the way of - so whoever asks for several things at once
    /// tells what each entry holds by the lock's own rule, from the one reading. The lock file is only read, and nothing
    /// is reclaimed and nothing written: a holder whose process has ended is passed over and stays.
    /// </summary>
    /// <param name="layout">The repository, whose main checkout holds the lock file.</param>
    /// <param name="requests">What would be taken. Never forced: nothing is taken, so there is nothing to take over.</param>
    /// <exception cref="HarnessException">
    /// The lock file could not be read, another process kept it for too long, or where a tree on this machine leads
    /// cannot be told (<see cref="Key"/>).
    /// </exception>
    public IReadOnlyList<LockInTheWay> Holders(HarnessLayout layout, IReadOnlyList<LockRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(requests);

        var wanted = requests.Select(request => (Request: request, Entry: Entry(request))).ToList();

        if (wanted.Count == 0)
        {
            return [];
        }

        return File(layout).Update<IReadOnlyList<LockInTheWay>>((entries, _) =>
        {
            var inTheWay = Live(entries, forced: null, afterwards: null)
                .Distinct()
                .Select(existing => new LockInTheWay(
                    existing,
                    [.. wanted.Where(one => InTheWay(existing, one.Entry, one.Request.Removal)).Select(one => one.Request)]))
                .Where(holding => holding.Of.Count > 0)
                .ToList();

            return (null, (IReadOnlyList<LockInTheWay>)inTheWay);
        });
    }

    /// <summary>
    /// Which run holds what <paramref name="request"/> asks for, without taking it; and, where no run does,
    /// <paramref name="whileFree"/>, done before any run can take it. The lock file is only read.
    /// </summary>
    /// <param name="layout">The repository, whose main checkout holds the lock file.</param>
    /// <param name="request">What would be taken. Never forced: nothing is taken, so there is nothing to take over.</param>
    /// <param name="whileFree">
    /// What to do while nothing holds it - rename a build directory out of a build's way, say - under the
    /// same machine-wide mutex a run takes the lock under, so no run can take it in between. Synchronous,
    /// as everything under that mutex is.
    /// </param>
    /// <returns>Which run holds it, said as a refusal says it, or <see langword="null"/> where none does.</returns>
    /// <remarks>
    /// For work that must write nothing before it can free anything: removing a build directory from a disk
    /// that is full. A lock taken writes the lock file, so such work is kept from a run's way by holding the
    /// mutex a run needs to take the lock, for as long as a rename takes. An entry of a run that has ended is
    /// passed over and left: taking it back would write the file.
    /// </remarks>
    /// <exception cref="HarnessException">
    /// The lock file could not be read, another process kept it for too long, or where a tree on this machine leads
    /// cannot be told (<see cref="Key"/>).
    /// </exception>
    public string? HeldBy(HarnessLayout layout, LockRequest request, Action? whileFree = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Force)
        {
            throw new ArgumentException("Asking which run holds a lock takes nothing, so it cannot be forced.", nameof(request));
        }

        var wanted = Entry(request);

        return File(layout).Update<string?>((entries, _) =>
        {
            if (Live(entries, forced: null, afterwards: null).FirstOrDefault(existing => InTheWay(existing, wanted, request.Removal)) is { } holder)
            {
                return (null, $"{Describe(wanted)} is held by {holder.Describe(_identity)}{_identity.ElsewhereNote(holder.Holder.Machine)}.");
            }

            whileFree?.Invoke();
            return (null, null);
        });
    }

    /// <summary>
    /// The one spelling the lock keys <paramref name="tree"/> on <paramref name="host"/> by, which every entry is written
    /// with and every request is asked by: on this machine, the full path with every link and junction along it followed,
    /// as git spells a tree - so a worktree reached through a worktrees root that is a link to another disk, and the same
    /// worktree as git names it, are one key - and as spelled on any other host, whose links this machine cannot follow,
    /// and for a path that is not fully qualified.
    /// </summary>
    /// <param name="host">The host the tree is on, as the command line names it.</param>
    /// <param name="tree">The tree on that host.</param>
    /// <exception cref="HarnessException">
    /// Where a tree on this machine leads cannot be told - its links form a cycle, or one cannot be read: refused, never
    /// keyed as spelled, which would miss a run holding it by another spelling.
    /// </exception>
    public string Key(string host, string tree)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(tree);

        if (!HostId.TryParse(host, out var on) || on.Kind != HostKind.Local || !Path.IsPathFullyQualified(tree))
        {
            return tree;
        }

        try
        {
            return _fileSystem.ResolveLinks(tree);
        }
        catch (IOException ex)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"The run lock keys '{tree}' by where it leads, and that cannot be told: {ex.Message.TrimEnd('.')}. Until it can be, a run "
                + "holding it by another spelling cannot be told from none.");
        }
    }

    /// <summary>The entry <paramref name="request"/> would record, naming this process, its tree as the lock keys it.</summary>
    private LockEntry Entry(LockRequest request)
        => new(
            request.Host,
            Key(request.Host, request.Tree),
            request.Scope,
            new LockHolder(
                _identity.CurrentMachine,
                _identity.CurrentId,
                request.RunId.Value,
                DateTimeOffset.UtcNow,
                request.Command,
                _identity.Current),
            request.Variant);

    /// <summary>Every entry currently recorded, live or not.</summary>
    /// <param name="layout">The repository whose main checkout holds the lock file.</param>
    public IReadOnlyList<LockEntry> Read(HarnessLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        return File(layout).Read();
    }

    /// <summary>
    /// Gives up <paramref name="entry"/>, and only that entry. A lock is released by the run that
    /// took it: a run that removed another's would hand two runs the same tree.
    /// </summary>
    /// <param name="layout">The repository whose main checkout holds the lock file.</param>
    /// <param name="entry">The entry this run took.</param>
    /// <param name="cancellationToken">Stops the attempt.</param>
    /// <remarks>
    /// A lock that could not be given up is said, and never stands in for the verdict of the work it
    /// guarded, which is done. This run no longer counts it, so its own later legs are never refused
    /// by it; the entry names this process, and leaves the file with the next change this run makes
    /// to it, or is reclaimed as a dead holder's is once this run has ended.
    /// </remarks>
    internal Task ReleaseAsync(HarnessLayout layout, LockEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        File(layout).GiveBack(
            existing => Ours(existing, entry),
            ex => _output.Warn(
                CommandName,
                $"{Describe(entry)} could not be released: {ex.Message} This run no longer counts it; it leaves the lock file "
                + "with the next change this run makes to it, or once this run has ended."));

        return Task.CompletedTask;
    }

    /// <summary>
    /// Whether <paramref name="wanted"/> cannot be taken while <paramref name="existing"/> stands.
    /// A tree taken exclusively excludes everything on it; a tree taken shared excludes only the
    /// same variant, which is what lets variants build side by side; and a tree to be removed
    /// (<see cref="LockRequest.Removal"/>) excludes as well everything on its host kept inside it.
    /// </summary>
    private static bool InTheWay(LockEntry existing, LockEntry wanted, bool removal)
    {
        if (!Same(existing.Host, wanted.Host))
        {
            return false;
        }

        if (!Same(existing.Tree, wanted.Tree))
        {
            return removal && Inside(wanted.Tree, existing.Tree);
        }

        return existing.Scope == LockScope.TreeExclusive
            || wanted.Scope == LockScope.TreeExclusive
            || Same(existing.Variant, wanted.Variant);
    }

    /// <summary>
    /// Whether <paramref name="path"/> is kept inside <paramref name="tree"/>: below it, past a separator of either kind, as
    /// a host spells it, compared as the lock compares trees.
    /// </summary>
    private static bool Inside(string tree, string path)
    {
        var root = tree.TrimEnd('/', '\\');

        return path.Length > root.Length + 1
            && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && path[root.Length] is '/' or '\\';
    }

    private static bool Ours(LockEntry existing, LockEntry ours)
        => Same(existing.Host, ours.Host)
            && Same(existing.Tree, ours.Tree)
            && Same(existing.Variant, ours.Variant)
            && existing.Holder.ProcessId == ours.Holder.ProcessId
            && string.Equals(existing.Holder.RunId, ours.Holder.RunId, StringComparison.Ordinal)
            && string.Equals(existing.Holder.Machine, ours.Holder.Machine, StringComparison.OrdinalIgnoreCase);

    private static bool Same(string? first, string? second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    /// <summary>A tree as a refusal, or a leg waiting for a slot, names it: <c>'/src/repo' variant 'debug' on local</c>.</summary>
    /// <param name="host">The host it is on, as the command line names it.</param>
    /// <param name="tree">The tree on that host.</param>
    /// <param name="variant">The variant of it taken, where one is.</param>
    public static string TreeNamed(string host, string tree, string? variant)
        => variant is { Length: > 0 } ? $"'{tree}' variant '{variant}' on {host}" : $"'{tree}' on {host}";

    private static string Describe(LockEntry entry) => TreeNamed(entry.Host, entry.Tree, entry.Variant);

    /// <summary>
    /// The entries still standing: a dead holder on this machine is reclaimed and the reclaim is
    /// reported, and a holder on another machine stands until <c>--force-lock</c> says otherwise,
    /// because nothing here can ask that machine whether it is still running.
    /// </summary>
    /// <param name="entries">What the lock file records.</param>
    /// <param name="forced">
    /// Which entries are in the way of what is asked for, where they are to be dropped, as <c>--force-lock</c> says to;
    /// <see langword="null"/> where nothing is forced.
    /// </param>
    /// <param name="afterwards">
    /// Where what is left is written back: takes what is said of each entry taken out, a dead holder's reclaimed or one
    /// forced, to be said once the file is let go. <see langword="null"/> where nothing is written: a dead holder's entry
    /// is then passed over without a word, and stays until a run takes a lock.
    /// </param>
    private IReadOnlyList<LockEntry> Live(IReadOnlyList<LockEntry> entries, Func<LockEntry, bool>? forced, Action<Action>? afterwards)
    {
        var kept = new List<LockEntry>();

        foreach (var entry in entries)
        {
            // Only the entry actually in the way. --force-lock says "this lock is stale, take it";
            // taking every other lock as well would drop holds on trees and variants this run never
            // asked for, including one another run is mid-sync on.
            //
            // Asked before the holder's machine is, so that a lock this machine will not reclaim on
            // its own can still be given up. Otherwise an entry whose process id has come back around
            // to something live is held for ever, and only editing the file by hand recovers it.
            if (forced?.Invoke(entry) == true)
            {
                afterwards?.Invoke(() => _output.Warn(CommandName, ProcessHolders.TakenByForce(Describe(entry), entry.Describe(_identity))));
                continue;
            }

            if (_identity.Stands(entry.Holder.Machine, entry.Holder.ProcessId, entry.Holder.ProcessStamp))
            {
                kept.Add(entry);
                continue;
            }

            // Reported rather than done quietly: a lock that disappears without a word is
            // indistinguishable from one that was never taken.
            afterwards?.Invoke(() => _output.Info(CommandName, ProcessHolders.Reclaimed(Describe(entry), entry.Describe(_identity))));
        }

        return kept;
    }
}

/// <summary>A live entry of the lock in the way of what was asked, and which of the requests asked it is in the way of.</summary>
/// <param name="Entry">The entry, its tree as the lock keys it.</param>
/// <param name="Of">Each request it is in the way of, among those asked, as it was asked.</param>
public sealed record LockInTheWay(LockEntry Entry, IReadOnlyList<LockRequest> Of);

/// <summary>What asking for a lock came to: the lock, or the run that holds it. Exactly one is set.</summary>
/// <param name="Handle">The lock, when it was taken.</param>
/// <param name="HeldBy">Which run holds it, said as a refusal says it, when it was not.</param>
/// <param name="Holder">The entry of the run that holds it, with what it holds; <see langword="null"/> where the lock was taken.</param>
public sealed record LockAttempt(RunLockHandle? Handle, string? HeldBy, LockEntry? Holder = null)
{
    /// <summary>Whether the lock was taken.</summary>
    public bool Taken => Handle is not null;
}

/// <summary>
/// What a run holds, and the only thing that can give it up. Released by the run that took it, and
/// by nothing else: a release that matched on the tree alone would hand a second run a tree the
/// first was still building in.
/// </summary>
public sealed class RunLockHandle : IAsyncDisposable
{
    private readonly RunLock _lock;
    private readonly HarnessLayout _layout;
    private bool _released;

    internal RunLockHandle(RunLock runLock, HarnessLayout layout, LockEntry entry)
    {
        _lock = runLock;
        _layout = layout;
        Entry = entry;
    }

    /// <summary>The entry this run took.</summary>
    public LockEntry Entry { get; }

    /// <summary>Gives up the lock. Calling it twice is not an error; the second call does nothing.</summary>
    /// <param name="cancellationToken">Stops the attempt.</param>
    public async Task ReleaseAsync(CancellationToken cancellationToken = default)
    {
        if (_released)
        {
            return;
        }

        _released = true;
        await _lock.ReleaseAsync(_layout, Entry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gives up the lock when the run leaves the block that took it.</summary>
    public async ValueTask DisposeAsync() => await ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
}
