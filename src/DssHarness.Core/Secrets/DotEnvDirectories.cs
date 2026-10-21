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
/// <param name="fileSystem">The file system the directory is read through.</param>
/// <param name="twiceDeclared">
/// How the reader says a name defined twice, completing the sentence that names the name and the two files - the
/// agent clients' slot refuses one in its own words (<c>a name is declared once</c>). Where null, the runner's own
/// words are said.
/// </param>
/// <param name="skipFile">The name of a file in the directory that is no dotenv file and is not read at all - a slot's placeholder.</param>
/// <param name="name">
/// The shape a name is held to beyond holding no space, where the reader holds one - the agent clients' slot holds a
/// variable's name - with the shape as a problem says it. A line whose part before '=' is not of that shape is
/// reported by its file and line alone, never by its text, which may be part of a value pasted on its own: a key
/// cut at a '=' of its own would otherwise be printed as a name. Where null, a name is anything without a space.
/// </param>
public sealed class DotEnvDirectories(
    IFileSystem fileSystem,
    Func<string, string, string, string>? twiceDeclared = null,
    string? skipFile = null,
    (Func<string, bool> Holds, string Shape)? name = null)
{
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly Func<string, string, string, string>? _twiceDeclared = twiceDeclared;
    private readonly string? _skipFile = skipFile;
    private readonly (Func<string, bool> Holds, string Shape)? _name = name;
    private readonly Dictionary<string, string> _origins = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _files = [];
    private readonly List<string> _problems = [];

    /// <summary>The file each name read so far was defined in, first, by name, its case ignored.</summary>
    public IReadOnlyDictionary<string, string> Origins => _origins;

    /// <summary>
    /// Every file read so far, in the order it was read - by name within a directory, the directories in the order they
    /// were read - the skipped file not among them: the one list of what was read, which no reader works out again.
    /// </summary>
    public IReadOnlyList<string> Files => _files;

    /// <summary>Every problem found so far, in the order it was found.</summary>
    public IReadOnlyList<string> Problems => _problems;

    /// <summary>
    /// The names and values <paramref name="directory"/>'s files define, held to every name read before it: a name
    /// defined again, here or in a directory read earlier, is a problem and keeps its first value.
    /// </summary>
    /// <param name="directory">The directory. Absent, it defines nothing; a file in its place is a problem.</param>
    /// <param name="cancellationToken">Stops the reading between two files.</param>
    /// <returns>What this directory defines, by name, its case ignored.</returns>
    public IReadOnlyDictionary<string, string> Read(string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!_fileSystem.DirectoryExists(directory))
        {
            // A file where the directory should be is no directory that is not there: read as none, its values would be
            // lost without a word and the reader would run on nothing.
            if (_fileSystem.FileExists(directory))
            {
                _problems.Add($"'{directory}' is a file, where a directory of dotenv files is read: nothing of it was read. "
                    + "Make it a directory and move the file into it, where every file is read and merged.");
            }

            // Each such directory is gitignored, so a fresh clone has none. A reader that needs a value it did not get
            // refuses where the value is used, with the name it wanted.
            return values;
        }

        var files = _fileSystem
            .EnumerateFiles(directory, recursive: false)
            .Where(file => _skipFile is null || !string.Equals(Path.GetFileName(file), _skipFile, StringComparison.Ordinal))
            .OrderBy(file => Path.GetFileName(file), StringComparer.Ordinal);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _files.Add(file);

            foreach (var (name, value) in ParseDotEnv(_fileSystem.ReadAllText(file), file, _name, _problems))
            {
                if (_origins.TryGetValue(name, out var first))
                {
                    _problems.Add(_twiceDeclared is { } worded
                        ? worded(name, first, file)
                        : $"'{name}' is defined in '{first}' and again in '{file}'; "
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
    /// <param name="shape">The shape a name is held to, where the reader holds one: a line not of it is said by its number alone.</param>
    /// <param name="problems">Collects what was wrong with the file.</param>
    private static IReadOnlyList<KeyValuePair<string, string>> ParseDotEnv(
        string text,
        string path,
        (Func<string, bool> Holds, string Shape)? shape,
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

            if (shape is { } held && !held.Holds(name))
            {
                problems.Add($"'{path}' line {line.Number}: what is before '=' is no variable's name ({held.Shape}), "
                    + "and it is not repeated here, for it may be part of a key.");
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
