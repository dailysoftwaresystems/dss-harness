using System.Text.Json;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Execution;

/// <summary>
/// A state file holding a list of entries that every process on the machine decides by - the run lock, the heavy
/// legs admitted onto a machine and the room they claim - read, decided on and written back as one step under
/// <see cref="MachineWideFile"/>.
/// </summary>
/// <remarks>
/// One implementation for every such list, so the rules that make one safe hold for all of them: two processes that
/// each read "free" would each write themselves in, so the read and the write are one step; the file is written
/// whole, through the atomic write, so no reader ever sees half of it and a crash never truncates it into a list that
/// appears to hold nothing; and a file that cannot be read, or holds a shape this build does not know, is refused
/// naming it - never read as empty, which is exactly the reading that lets two processes proceed. The work inside the
/// step is synchronous, as everything under a mutex is: a mutex is released by the thread that took it. What the work
/// has to say is said once the file is let go, never while it is held.
/// <para>
/// An entry this process gives back leaves its own reading of the list at once, whether or not the file could be
/// changed just then: every entry names the process that recorded it, and one this process gave back but could not take
/// out would otherwise stand, alive as long as this process is, in front of this process's own later entries.
/// </para>
/// </remarks>
/// <typeparam name="TEntry">One entry, as <see cref="JsonStateFile"/> writes it.</typeparam>
/// <param name="fileSystem">Reads and writes the file.</param>
/// <param name="path">The file.</param>
/// <param name="subject">What the file is, as a refusal begins: <c>The run lock</c>.</param>
/// <param name="consequence">What nothing can do until the file can be read and written, said after a refusal.</param>
/// <param name="unreadable">What to do about a file that is not this build's JSON, said after that refusal.</param>
internal sealed class MachineWideList<TEntry>(IFileSystem fileSystem, string path, string subject, string consequence, string unreadable)
{
    private readonly IFileSystem _fileSystem = fileSystem;

    /// <summary>
    /// The entries this process gave back that the file may still hold, each as it picks its entry out: left out of
    /// every list this process reads, until a change it writes has taken them out of the file too.
    /// </summary>
    private readonly List<Func<TEntry, bool>> _givenBack = [];

    /// <summary>The file, as every refusal names it.</summary>
    public string Path { get; } = System.IO.Path.GetFullPath(path);

    /// <summary>
    /// Reads the list, hands it to <paramref name="change"/> and writes back what that returns, with no other
    /// process in between. Where it returns <see langword="null"/> for the list nothing is written - not the file,
    /// nor a directory for it - so a question asked of a full disk costs no room.
    /// </summary>
    /// <param name="change">
    /// Decides, from the entries recorded - less those this process gave back - what to write and what to answer, and
    /// leaves what it has to say with the action it is given, to be said once the file is let go and only where the
    /// change was made.
    /// </param>
    /// <exception cref="HarnessException">The file could not be read or written, or another process kept it too long.</exception>
    public T Update<T>(Func<IReadOnlyList<TEntry>, Action<Action>, (IReadOnlyList<TEntry>? Written, T Answer)> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        return MachineWideFile.Update(Path, MachineWideFile.Window, afterwards =>
        {
            List<Func<TEntry, bool>> leaving;

            lock (_givenBack)
            {
                leaving = [.. _givenBack];
            }

            var recorded = Read();
            var standing = leaving.Count == 0 ? recorded : [.. recorded.Where(entry => !leaving.Any(ours => ours(entry)))];
            var (written, answer) = change(standing, afterwards);

            if (written is not null)
            {
                Written(() =>
                {
                    _fileSystem.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                    _fileSystem.WriteAllTextAtomic(Path, JsonSerializer.Serialize(written, JsonStateFile.Options) + "\n");
                });

                // Written from a list they were left out of, so the file no longer holds them either.
                lock (_givenBack)
                {
                    _givenBack.RemoveAll(leaving.Contains);
                }
            }

            return answer;
        });
    }

    /// <summary>
    /// Writes back what <paramref name="change"/> makes of the list, as <see cref="Update{T}"/> does, where nothing is
    /// answered but the change itself.
    /// </summary>
    /// <param name="change">Makes, from the entries recorded, the list to write, as <see cref="Update{T}"/>'s does.</param>
    /// <exception cref="HarnessException">The file could not be read or written, or another process kept it too long.</exception>
    public void Change(Func<IReadOnlyList<TEntry>, Action<Action>, IReadOnlyList<TEntry>> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        Update((entries, afterwards) => ((IReadOnlyList<TEntry>?)change(entries, afterwards), 0));
    }

    /// <summary>
    /// Takes the entries <paramref name="ours"/> picks out of the list: this process's own, which it has done with. They
    /// leave this process's reading of the list at once; where the file cannot be changed just then,
    /// <paramref name="couldNot"/> is told why, and they leave the file with the next change this process makes to it, or
    /// are reclaimed as a dead holder's are once it has ended.
    /// </summary>
    /// <param name="ours">Picks out the entries given back.</param>
    /// <param name="couldNot">Told why the file could not be changed, where it could not; nothing is thrown.</param>
    public void GiveBack(Func<TEntry, bool> ours, Action<HarnessException> couldNot)
    {
        ArgumentNullException.ThrowIfNull(ours);
        ArgumentNullException.ThrowIfNull(couldNot);

        lock (_givenBack)
        {
            _givenBack.Add(ours);
        }

        try
        {
            Change((standing, _) => standing);
        }
        catch (HarnessException ex)
        {
            couldNot(ex);
        }
    }

    /// <summary>Every entry recorded, read as the file stands.</summary>
    /// <exception cref="HarnessException">The file could not be read, or is not this build's JSON.</exception>
    public IReadOnlyList<TEntry> Read()
        => MachineWideFile.Read<List<TEntry>>(
            _fileSystem,
            Path,
            ex => ex is JsonException
                ? $"{subject} '{Path}' is not readable as JSON: {ex.Message.TrimEnd('.')}. {unreadable}"
                : $"{subject} '{Path}' could not be read: {ex.Message.TrimEnd('.')}. {consequence}")
            ?? [];

    /// <summary>Does <paramref name="write"/>, and refuses, naming the file, when it could not be done.</summary>
    private void Written(Action write) => MachineWideFile.Written($"{subject} '{Path}'", consequence, write);
}
