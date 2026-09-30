using System.Text.Json;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Execution;

/// <summary>
/// A state file holding a list of entries that every process on the machine decides by - the run lock, the heavy
/// legs admitted onto a machine - read, decided on and written back as one step under <see cref="MachineWideFile"/>.
/// </summary>
/// <remarks>
/// One implementation for every such list, so the rules that make one safe hold for all of them: two processes that
/// each read "free" would each write themselves in, so the read and the write are one step; the file is written
/// whole, through the atomic write, so no reader ever sees half of it and a crash never truncates it into a list that
/// appears to hold nothing; and a file that cannot be read, or holds a shape this build does not know, is refused
/// naming it - never read as empty, which is exactly the reading that lets two processes proceed. The work inside the
/// step is synchronous, as everything under a mutex is: a mutex is released by the thread that took it.
/// </remarks>
/// <typeparam name="TEntry">One entry, as <see cref="JsonStateFile"/> writes it.</typeparam>
/// <param name="fileSystem">Reads and writes the file.</param>
/// <param name="path">The file.</param>
/// <param name="subject">What the file is, as a refusal begins: <c>The run lock</c>.</param>
/// <param name="consequence">What nothing can do until the file can be read and written, said after a refusal.</param>
/// <param name="unreadable">What to do about a file that is not this build's JSON, said after that refusal.</param>
internal sealed class MachineWideList<TEntry>(IFileSystem fileSystem, string path, string subject, string consequence, string unreadable)
{
    /// <summary>
    /// How long one change of the file waits for another process's change of it. This bounds a rewrite that takes
    /// microseconds, never what the file records.
    /// </summary>
    private static readonly TimeSpan UpdateWindow = TimeSpan.FromSeconds(10);

    private readonly IFileSystem _fileSystem = fileSystem;

    /// <summary>The file, as every refusal names it.</summary>
    public string Path { get; } = System.IO.Path.GetFullPath(path);

    /// <summary>
    /// Reads the list, hands it to <paramref name="change"/> and writes back what that returns, with no other
    /// process in between. Where it returns <see langword="null"/> for the list nothing is written - not the file,
    /// nor a directory for it - so a question asked of a full disk costs no room.
    /// </summary>
    /// <param name="change">Decides, from the entries recorded, what to write and what to answer.</param>
    /// <exception cref="HarnessException">The file could not be read or written, or another process kept it too long.</exception>
    public T Update<T>(Func<IReadOnlyList<TEntry>, (IReadOnlyList<TEntry>? Written, T Answer)> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        return MachineWideFile.Update(Path, UpdateWindow, () =>
        {
            var (written, answer) = change(Read());

            if (written is not null)
            {
                Written(() =>
                {
                    _fileSystem.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                    _fileSystem.WriteAllTextAtomic(Path, JsonSerializer.Serialize(written, JsonStateFile.Options) + "\n");
                });
            }

            return answer;
        });
    }

    /// <summary>Every entry recorded, read as the file stands.</summary>
    /// <exception cref="HarnessException">The file could not be read, or is not this build's JSON.</exception>
    public IReadOnlyList<TEntry> Read()
    {
        if (!_fileSystem.FileExists(Path))
        {
            return [];
        }

        string text;

        try
        {
            text = _fileSystem.ReadAllText(Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"{subject} '{Path}' could not be read: {ex.Message.TrimEnd('.')}. {consequence}",
                ex);
        }

        try
        {
            return JsonSerializer.Deserialize<List<TEntry>>(text, JsonStateFile.Options) ?? [];
        }
        catch (JsonException ex)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"{subject} '{Path}' is not readable as JSON: {ex.Message.TrimEnd('.')}. {unreadable}",
                ex);
        }
    }

    /// <summary>Does <paramref name="write"/>, and refuses, naming the file, when it could not be done.</summary>
    private void Written(Action write) => MachineWideFile.Written($"{subject} '{Path}'", consequence, write);
}
