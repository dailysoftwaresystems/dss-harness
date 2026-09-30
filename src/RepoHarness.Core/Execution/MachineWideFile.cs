using System.Globalization;
using System.Text.Json;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Execution;

/// <summary>
/// Serialises one file's read-decide-write across every process on the machine.
/// </summary>
/// <remarks>
/// The run lock, a log directory's owner and a machine's heavy-leg slots are each decided by
/// reading a file, deciding, and writing it back. Two runs that each read "free" would each write
/// themselves in, and both would believe they held it. A named mutex is the machine-wide lock .NET offers on Windows, Linux and macOS alike,
/// and it is the same mechanism the anchor registries are changed under.
/// </remarks>
internal static class MachineWideFile
{
    /// <summary>
    /// How long one change of a file every run decides by - the run lock, a log directory's owner, the heavy legs admitted
    /// onto a machine - waits for another process's change of it. This bounds a rewrite that takes microseconds, never
    /// what the file records.
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

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

        try
        {
            return fileSystem.KindOf(path) == PathKind.None
                ? null
                : JsonSerializer.Deserialize<T>(fileSystem.ReadAllText(path), JsonStateFile.Options)
                    ?? throw new JsonException("it holds null");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(HarnessExit.Refused, refusal(ex), ex);
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
    /// Runs <paramref name="work"/> with no other process inside the same file's update.
    /// </summary>
    /// <remarks>
    /// The work is synchronous, and must stay so: a mutex is released by the thread that took it,
    /// and an await inside the work could resume on a different one.
    /// </remarks>
    /// <param name="path">The file being updated, which names the mutex.</param>
    /// <param name="window">
    /// How long to wait for another process's update. It bounds a rewrite that takes microseconds,
    /// never what the file records: a held lock is refused at once, and a taken heavy-leg slot is
    /// waited for between updates, never inside one.
    /// </param>
    /// <param name="work">The read, the decision and the write.</param>
    /// <exception cref="HarnessException">Another process held the file for longer than the window allows.</exception>
    public static T Update<T>(string path, TimeSpan window, Func<T> work)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(work);

        var name = NameFor(path);
        using var mutex = MachineWideMutex.Open(name, $"'{path}'");
        var taken = Wait(mutex, path, window);

        try
        {
            return work();
        }
        finally
        {
            if (taken)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> as <see cref="Update{T}(string, TimeSpan, Func{T})"/> does, and says what it gave to
    /// say afterwards once the file is let go - and only where the work was done, since a line about a change that was
    /// never written would be untrue.
    /// </summary>
    /// <remarks>
    /// Said while the file is held, a line would hold every other process's change of it for as long as the line took
    /// to write: a console whose window has text selected in it, or a pipe nobody reads, takes as long as it likes, and
    /// every process waiting to change the file would be refused once the window had passed.
    /// </remarks>
    /// <param name="path">The file being updated, which names the mutex.</param>
    /// <param name="window">How long to wait for another process's update.</param>
    /// <param name="work">The read, the decision and the write, given where to leave what is to be said afterwards.</param>
    /// <exception cref="HarnessException">Another process held the file for longer than the window allows.</exception>
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
    /// One mutex per file, however its path is spelled.
    /// </summary>
    /// <remarks>
    /// Case is always folded, whatever the filesystem says. On a case-sensitive one that can only
    /// make two genuinely different files share a lock, which costs a moment of waiting; the other
    /// way round, two spellings of one file would each believe they held it.
    /// </remarks>
    private static string NameFor(string path)
        => MachineWideMutex.NameFor("file", [path], ignoreCase: true);

    private static bool Wait(Mutex mutex, string path, TimeSpan window)
        => MachineWideMutex.Wait(mutex, window)
            ? true
            : throw new HarnessException(
                HarnessExit.Refused,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Another {ToolPackage.Id} process has been updating '{path}' for {window.TotalSeconds:0} seconds, so nothing was changed."));
}
