using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Platform;
using DssHarness.Core.Results;
using DssHarness.Core.Waiting;

namespace DssHarness.Core.Execution;

/// <summary>
/// Serialises one file's read-decide-write across every process on the machine.
/// </summary>
/// <remarks>
/// The run lock, a log directory's owner, a machine's heavy-leg slots and the set-ups its legs hold are each decided by
/// reading a file, deciding, and writing it back. Two runs that each read "free" would each write
/// themselves in, and both would believe they held it. A named mutex is the machine-wide lock .NET offers on Windows, Linux and macOS alike,
/// and it is the same mechanism the anchor registries are changed under.
/// </remarks>
internal static class MachineWideFile
{
    /// <summary>
    /// How long one change of a file every run decides by - the run lock, a log directory's owner, the heavy legs admitted
    /// onto a machine - waits for another process's change of it: for as long as that process holds it, and no bound of
    /// the code's own. A process that ends holding it lets it go by the system's own hand, so a wait for a dead holder ends
    /// as it ends; one for a live holder busy for minutes - a disk that stalls, a machine that sleeps - waits for it, said
    /// every five minutes (<see cref="Waits.SaidEvery"/>) to whoever the operation tells its waits to.
    /// </summary>
    public static readonly TimeSpan Window = Timeout.InfiniteTimeSpan;

    /// <summary>
    /// What <paramref name="path"/> holds, read by every state file's rules (<see cref="JsonStateFile"/>), or
    /// <see langword="null"/> where nothing is there.
    /// </summary>
    /// <remarks>
    /// Asked what is there rather than whether a file is, which answers no for a file this process may not look at, or
    /// could not look at just then: a record read as absent from it would be written over. A file that cannot be looked
    /// at or read, holds a shape this build does not know - a member it needs left out included - or holds null, is
    /// refused as <paramref name="refusal"/> says it, never read as absent: that is exactly the reading that lets two
    /// processes proceed.
    /// </remarks>
    /// <typeparam name="T">What the file holds.</typeparam>
    /// <param name="fileSystem">Reads the file.</param>
    /// <param name="path">The file.</param>
    /// <param name="refusal">Says the refusal, from why the file could not be read.</param>
    /// <exception cref="HarnessException">The file could not be read, or not as this build's JSON.</exception>
    public static T? Read<T>(IFileSystem fileSystem, string path, Func<Exception, string> refusal)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(refusal);

        return TryRead<T>(fileSystem, path, out var value, out var unread)
            ? value
            : throw new HarnessException(HarnessExit.Refused, refusal(unread), unread);
    }

    /// <summary>
    /// Reads what <paramref name="path"/> holds as <see cref="Read{T}"/> does, for a caller that goes on where the
    /// file cannot be read, and says so, rather than stop.
    /// </summary>
    /// <remarks>
    /// What counts as a file that cannot be read is decided here alone, for both: <see cref="Read{T}"/> refuses exactly
    /// what this answers <see langword="false"/> for, from the same failure.
    /// </remarks>
    /// <typeparam name="T">What the file holds.</typeparam>
    /// <param name="fileSystem">Reads the file.</param>
    /// <param name="path">The file.</param>
    /// <param name="value">What the file holds, or <see langword="null"/> where nothing is there or it could not be read.</param>
    /// <param name="unread">Why the file could not be read, or <see langword="null"/> where it was read or nothing is there.</param>
    /// <returns>
    /// Whether the file was read, or nothing is there to read; <see langword="false"/> where a file is there and could
    /// not be looked at or read, or not as this build's JSON.
    /// </returns>
    public static bool TryRead<T>(IFileSystem fileSystem, string path, out T? value, [NotNullWhen(false)] out Exception? unread)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        try
        {
            value = fileSystem.KindOf(path) == PathKind.None
                ? null
                : JsonStateFile.Read<T>(fileSystem, path)
                    ?? throw new JsonException("it holds null");
            unread = null;

            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Removed between the look and the read: nothing is there to read, as where the look found none.
            value = null;
            unread = null;

            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            value = null;
            unread = ex;

            return false;
        }
    }

    /// <summary>
    /// Does <paramref name="write"/>, and refuses when it could not be done: a file every run
    /// decides by that nobody can write stops every run alike - a directory an earlier run under
    /// sudo left to root, a disk that is full - and is no defect in this tool.
    /// </summary>
    /// <param name="file">The file, as the refusal names it: <c>The run lock '...'</c>.</param>
    /// <param name="consequence">Why nothing may run until it can be written.</param>
    /// <param name="write">The write.</param>
    /// <exception cref="HarnessException">The write failed, refused with the reason the system gave.</exception>
    public static void Written(string file, string consequence, Action write)
    {
        ArgumentNullException.ThrowIfNull(write);

        try
        {
            write();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"{file} could not be written: {ex.Message.TrimEnd('.')}. {consequence}",
                ex);
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> with no other process inside the same file's update, waiting for another process's
    /// update of it as <see cref="Update{T}(string, TimeSpan, Action{WaitTold}, TimeProvider, Func{T}, CancellationToken)"/>
    /// does, told to the running operation's teller (<see cref="Waits.Told"/>) on the system's clock, and ended by nothing
    /// but the bound it is given.
    /// </summary>
    /// <param name="path">The file being updated, which names the mutex.</param>
    /// <param name="window">
    /// How long to wait for another process's update: <see cref="Window"/>, for as long as that process holds it, or a
    /// bound of the caller's own. A held lock is refused at once, and a taken heavy-leg slot is waited for between updates,
    /// never inside one: what waits here is another process's rewrite of the file.
    /// </param>
    /// <param name="work">The read, the decision and the write.</param>
    /// <exception cref="HarnessException">Another process held the file for longer than a bound the caller gave allows.</exception>
    public static T Update<T>(string path, TimeSpan window, Func<T> work)
        => Update(path, window, Waits.Told, TimeProvider.System, work, CancellationToken.None);

    /// <summary>
    /// Runs <paramref name="work"/> as <see cref="Update{T}(string, TimeSpan, Func{T})"/> does, and says what it gave to
    /// say afterwards once the file is let go - and only where the work was done, since a line about a change that was
    /// never written would be untrue.
    /// </summary>
    /// <remarks>
    /// Said while the file is held, a line would hold every other process's change of it for as long as the line took
    /// to write: a console whose window has text selected in it, or a pipe nobody reads, takes as long as it likes, and
    /// every process waiting to change the file would wait on it.
    /// </remarks>
    /// <param name="path">The file being updated, which names the mutex.</param>
    /// <param name="window">How long to wait for another process's update: <see cref="Window"/>, for as long as it holds it.</param>
    /// <param name="work">The read, the decision and the write, given where to leave what is to be said afterwards.</param>
    /// <exception cref="HarnessException">Another process held the file for longer than a bound the caller gave allows.</exception>
    public static T Update<T>(string path, TimeSpan window, Func<Action<Action>, T> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        var afterwards = new List<Action>();
        var answer = Update(path, window, () => work(afterwards.Add));

        foreach (var say in afterwards)
        {
            say();
        }

        return answer;
    }

    /// <summary>
    /// Runs <paramref name="work"/> with no other process inside the same file's update: the one wait every update of a
    /// file every run decides by waits through. Another process's update is waited for for as long as that process holds
    /// the file, or for <paramref name="window"/> where the caller gives a bound of its own: the system lets go a mutex
    /// whose holder ended holding it, so the wait ends where the holder lets go or dies, never at a number of the code's
    /// own. It is told as every wait is (<see cref="Waits.ForAsync{T}"/>), every <see cref="Waits.SaidEvery"/>, as waiting
    /// for another process's change of the file by its path, and ended by <paramref name="cancellationToken"/>.
    /// </summary>
    /// <remarks>
    /// The mutex is taken by a hold on a thread of its own (<see cref="MachineWideMutex.HoldAsync"/>), which keeps it until
    /// the hold is let go: the work, synchronous, runs on the calling thread meanwhile, and the hold lets the mutex go from
    /// whichever thread ends it. A work under a file's update that updates the same file again goes on inside the hold it
    /// runs under, as a mutex its own thread held was taken again at once.
    /// </remarks>
    /// <typeparam name="T">What the work answers.</typeparam>
    /// <param name="path">The file being updated, which names the mutex.</param>
    /// <param name="window">How long to wait for another process's update: <see cref="Window"/>, for as long as it holds it, or a bound of the caller's own.</param>
    /// <param name="tell">Told where the wait stands, in the one form a wait is told in (<see cref="WaitTold.Line"/>).</param>
    /// <param name="clock">The clock the wait is told on.</param>
    /// <param name="work">The read, the decision and the write.</param>
    /// <param name="cancellationToken">Ends the wait, before the work begins.</param>
    /// <exception cref="HarnessException">Another process held the file for longer than a bound the caller gave allows.</exception>
    /// <exception cref="OperationCanceledException">The token ended the wait; nothing was changed.</exception>
    public static T Update<T>(string path, TimeSpan window, Action<WaitTold> tell, TimeProvider clock, Func<T> work, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(tell);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(work);

        var name = NameFor(path);
        var held = HeldHere.Value!;

        // A work under this file's hold that updates the file again goes on inside the hold, as a mutex its thread holds
        // is taken again at once: the hold is another thread's, and a wait on it would wait on itself for ever.
        if (held.Contains(name))
        {
            return work();
        }

        // The caller's bound is the hold's own: the take ends there with nothing held, as it ends at the token.
        var end = Waits.ForAsync(
                token => MachineWideMutex.HoldAsync(name, $"'{path}'", window, token),
                new WaitWatch($"another process's change of '{path}'", tell, Bound: null, clock),
                cancellationToken)
            .GetAwaiter()
            .GetResult();

        if (end.Value is null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            throw new HarnessException(
                HarnessExit.Refused,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Another {ToolPackage.Id} process has been updating '{path}' for {window.TotalSeconds:0} seconds, so nothing was changed."));
        }

        using var hold = end.Value;
        held.Add(name);

        try
        {
            return work();
        }
        finally
        {
            held.Remove(name);
        }
    }

    /// <summary>
    /// The files whose work under an update (<see cref="Update{T}(string, TimeSpan, Action{WaitTold}, TimeProvider, Func{T}, CancellationToken)"/>)
    /// runs on this thread now, by their mutex's name: the work is synchronous, so the thread it runs on is the one that
    /// asks again.
    /// </summary>
    private static readonly ThreadLocal<HashSet<string>> HeldHere = new(() => new HashSet<string>(StringComparer.Ordinal));

    /// <summary>
    /// One mutex per file, however its path is spelled.
    /// </summary>
    /// <remarks>
    /// Case is always folded, whatever the filesystem says. On a case-sensitive one that can only
    /// make two genuinely different files share a lock, which costs a moment of waiting; the other
    /// way round, two spellings of one file would each believe they held it.
    /// </remarks>
    private static string NameFor(string path)
        => MachineWideMutex.NameFor("file", [path], ignoreCase: true);
}
