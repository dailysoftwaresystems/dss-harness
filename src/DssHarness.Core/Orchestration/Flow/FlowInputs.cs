using DssHarness.Core.FileSystem;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// What a run's command line gives as text: the inputs a run is started with, <c>name=value</c> or <c>name=@file</c>,
/// and the file an answer's text is read from. A file is read through the tool's own file system, relative to the
/// directory the command acts on - its <c>--directory</c>, or where it was typed - and one that is not there is a usage
/// error, said by its path. And what an input takes: the values a run starts with, resolved against its workflow.
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
    /// The values a run of <paramref name="definition"/> starts with: each input the workflow declares, given or its
    /// default - the one owner of what an input takes, which a start holds its inputs to before anything of the run is
    /// made, and the engine its run to. A required input nothing gave, a value outside an input's options and a required
    /// param with no default are each refused (13).
    /// </summary>
    /// <param name="definition">The workflow the run runs.</param>
    /// <param name="given">The inputs given, by name; null where none was.</param>
    /// <returns>The run's input values, by name.</returns>
    public static IReadOnlyDictionary<string, string> Resolve(WorkflowDefinition definition, IReadOnlyDictionary<string, string>? given)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var input in definition.Inputs)
        {
            var value = given is not null && given.TryGetValue(input.Name, out var supplied) ? supplied : input.Default;

            if (value is null)
            {
                if (input.Required)
                {
                    throw new HarnessException(HarnessExit.Refused, $"input '{input.Name}' is required, and nothing gave the run a value for it");
                }

                continue;
            }

            if (input.Options.Count > 0 && !input.Options.Contains(value, StringComparer.Ordinal))
            {
                throw new HarnessException(HarnessExit.Refused, $"input '{input.Name}' takes one of {string.Join(", ", input.Options)}, and the run was given '{value}'");
            }

            values[input.Name] = value;
        }

        foreach (var parameter in definition.Params)
        {
            if (parameter.Required && parameter.Default is null)
            {
                throw new HarnessException(HarnessExit.Refused, $"param '{parameter.Name}' is required, and the workflow declares no default for it");
            }
        }

        return values;
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
