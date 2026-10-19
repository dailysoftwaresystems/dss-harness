using DssHarness.Core.FileSystem;

namespace DssHarness.Core.Secrets;

/// <summary>
/// The one rule for a directory of dotenv files, read as one set of names with any others read beside it: every file
/// in a directory is read and merged, a name defined twice is refused, and a line that defines nothing is reported
/// with its line number. Every problem is collected, in the order it was found, for the reader to report together.
/// </summary>
/// <remarks>
/// <para>
/// A directory rather than a single file, so one repository can hold several unrelated sets and a machine can be
/// given only the ones it needs: the runner's <c>.harness-config/runner/.env</c> and
/// <c>.harness-config/runner/.secrets</c> are each one, read here as one set of names, and the agent
/// clients' slot is one of the same kind.
/// </para>
/// <para>
/// A name defined twice is refused rather than letting the later file win - in two files, in one file twice, or in
/// two directories read as one set - and names are told apart without their case. Silent precedence over a set of
/// files whose order is a directory listing is a rule nobody can read off the files: the value that reached a reader
/// would depend on a file name, and renaming a file would change what ran without changing anything a reviewer looks
/// at. The files are read in the order of their names, so a refusal names the same two files whatever order the file
/// system listed them in.
/// </para>
/// <para>
/// Who may read a file is not asked here. Which files must be private turns on what their names hold, which only the
/// reader knows, so it is the reader's to ask, of the file each name came from (<see cref="Origins"/>); the runner's
/// reader asks it of neither of its directories.
/// </para>
/// </remarks>
public sealed class DotEnvDirectories(IFileSystem fileSystem)
{
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly Dictionary<string, string> _origins = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _problems = [];

    /// <summary>The file each name read so far was defined in, first, by name, its case ignored.</summary>
    public IReadOnlyDictionary<string, string> Origins => _origins;

    /// <summary>Every problem found so far, in the order it was found.</summary>
    public IReadOnlyList<string> Problems => _problems;

    /// <summary>
    /// The names and values <paramref name="directory"/>'s files define, held to every name read before it: a name
    /// defined again, here or in a directory read earlier, is a problem and keeps its first value.
    /// </summary>
    /// <param name="directory">The directory. Absent, it defines nothing.</param>
    /// <param name="cancellationToken">Stops the reading between two files.</param>
    /// <returns>What this directory defines, by name, its case ignored.</returns>
    public IReadOnlyDictionary<string, string> Read(string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!_fileSystem.DirectoryExists(directory))
        {
            // Each such directory is gitignored, so a fresh clone has none. A reader that needs a value it did not get
            // refuses where the value is used, with the name it wanted.
            return values;
        }

        var files = _fileSystem
            .EnumerateFiles(directory, recursive: false)
            .OrderBy(file => Path.GetFileName(file), StringComparer.Ordinal);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var (name, value) in ParseDotEnv(_fileSystem.ReadAllText(file), file, _problems))
            {
                if (_origins.TryGetValue(name, out var first))
                {
                    _problems.Add($"'{name}' is defined in '{first}' and again in '{file}'; "
                        + "nothing can say which value a run used.");
                    continue;
                }

                _origins[name] = file;
                values[name] = value;
            }
        }

        return values;
    }

    /// <summary>
    /// The definitions one dotenv file holds, every problem it has reported together with the line
    /// it was found at.
    /// </summary>
    /// <remarks>
    /// Read through the one <c>NAME=value</c> line scanner (<see cref="DotEnvFile.Scan"/>). A directory
    /// of these feeds a command's environment, so this is the stricter of the two readers of a line:
    /// a line that defines nothing is a problem named by its line, and a name with whitespace in it is
    /// not a name at all.
    /// </remarks>
    /// <param name="text">The file's contents.</param>
    /// <param name="path">Where it came from, quoted in any problem.</param>
    /// <param name="problems">Collects what was wrong with the file.</param>
    private static IReadOnlyList<KeyValuePair<string, string>> ParseDotEnv(
        string text,
        string path,
        List<string> problems)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(problems);

        var entries = new List<KeyValuePair<string, string>>();

        foreach (var line in DotEnvFile.Scan(text))
        {
            if (line.Name is not { } name)
            {
                problems.Add(line.Fault == DotEnvLineFault.NoEquals
                    ? $"'{path}' line {line.Number}: no '=', so nothing is defined here."
                    : $"'{path}' line {line.Number}: the name before '=' is empty.");
                continue;
            }

            if (name.Any(char.IsWhiteSpace))
            {
                problems.Add($"'{path}' line {line.Number}: '{name}' is not a name; "
                    + "an environment variable's name holds no spaces.");
                continue;
            }

            entries.Add(new KeyValuePair<string, string>(name, line.Value!));
        }

        return entries;
    }
}
