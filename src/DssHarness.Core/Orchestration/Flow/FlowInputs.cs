using DssHarness.Core.FileSystem;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// What a run's command line gives as text: the inputs a run is started with, <c>name=value</c> or <c>name=@file</c>,
/// and the file an answer's text is read from. A file is read through the tool's own file system, relative to the
/// directory the command acts on - its <c>--directory</c>, or where it was typed - and one that is not there is a usage
/// error, said by its path.
/// </summary>
public static class FlowInputs
{
    /// <summary>
    /// The inputs <paramref name="given"/> names, by input name: each <c>name=value</c>, a value that begins with
    /// <c>@</c> the text of the file it names. An input given twice, one with no name or no value, and a file that is not
    /// there are each a usage error.
    /// </summary>
    /// <param name="fileSystem">Reads each file.</param>
    /// <param name="directory">The directory the command acts on, which a relative file is read from.</param>
    /// <param name="given">The inputs as the command line gave them, in order; null or empty where it gave none.</param>
    /// <returns>The inputs by name, null where none was given; or the problem, as a usage error says it.</returns>
    public static (IReadOnlyDictionary<string, string>? Inputs, string? Problem) Read(IFileSystem fileSystem, string directory, IReadOnlyList<string>? given)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (given is null || given.Count == 0)
        {
            return (null, null);
        }

        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var input in given)
        {
            var at = input.IndexOf('=', StringComparison.Ordinal);

            if (at <= 0 || at == input.Length - 1)
            {
                return (null, $"'{input}' is not an input as name=value; --input k=v gives one, and k=@<file> a text input a file's text");
            }

            var name = input[..at];
            var value = input[(at + 1)..];

            if (inputs.ContainsKey(name))
            {
                return (null, $"input '{name}' is given twice; --input gives each input once");
            }

            if (value.Length > 1 && value[0] == '@')
            {
                var (text, problem) = TextOf(fileSystem, directory, value[1..], $"--input {name}=@<file>");

                if (problem is not null)
                {
                    return (null, problem);
                }

                value = text!;
            }

            inputs[name] = value;
        }

        return (inputs, null);
    }

    /// <summary>
    /// The text of the file <paramref name="path"/> names, relative to <paramref name="directory"/>; a file that is not
    /// there is a usage error naming <paramref name="option"/>.
    /// </summary>
    /// <param name="fileSystem">Reads the file.</param>
    /// <param name="directory">The directory the command acts on.</param>
    /// <param name="path">The file, as the command line named it.</param>
    /// <param name="option">The option that named it, as the problem says it.</param>
    /// <returns>The file's text; or the problem, as a usage error says it.</returns>
    public static (string? Text, string? Problem) TextOf(IFileSystem fileSystem, string directory, string path, string option)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.GetFullPath(Path.Combine(directory, path));

        return fileSystem.KindOf(full) == PathKind.File
            ? (fileSystem.ReadAllText(full), null)
            : (null, $"'{full}' is not a file: {option} names the file whose text is given");
    }
}
