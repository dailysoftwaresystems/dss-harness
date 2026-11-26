using DssHarness.Core.FileSystem;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// What a run's command line gives as text: the inputs a run is started with, <c>name=value</c> or <c>name=@file</c>,
/// and the file an answer's text is read from. A file is read through the tool's own file system, relative to the
/// directory the command acts on - its <c>--directory</c>, or where it was typed - and one that is not there is a usage
/// error, said by its path. And what an input takes: the values a run starts with, resolved against its workflow, the
/// files its own inputs of the kind <c>file</c> name, opened and kept as it starts, and what its steps read for each.
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

    /// <summary>A run's inputs as one object, as the root <c>input</c> reads them wherever a run is read.</summary>
    /// <param name="inputs">The run's inputs, resolved.</param>
    public static System.Text.Json.Nodes.JsonObject AsObject(IReadOnlyDictionary<string, string> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var values = new System.Text.Json.Nodes.JsonObject();

        foreach (var (name, value) in inputs)
        {
            values[name] = value;
        }

        return values;
    }

    /// <summary>
    /// The values a run of <paramref name="definition"/> starts with: each input the workflow declares, given or its
    /// default - the one owner of what an input takes, which a start holds its inputs to before anything of the run is
    /// made, and the engine its run to. A required input nothing gave, a value outside an input's options and a required
    /// param with no default are each refused (13). An input with options may default empty - no choice made, and the
    /// run's expressions read it as it is - and a value given empty reads the same, for an empty value is the default's
    /// own and never a slip of the keyboard.
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

            if (input.Options.Count > 0 && value.Length > 0 && !input.Options.Contains(value, StringComparer.Ordinal))
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
    /// The values a run's steps read for its inputs - <c>{input.&lt;name&gt;}</c> in every prompt and every expression - from
    /// the values the run takes (<see cref="Resolve"/>): each as it is, but an input of the kind <c>file</c> that is not
    /// empty, which reads the full path of the copy the run made of the file as it started
    /// (<see cref="OrchestratorLayout.InputCopy"/>, <see cref="OpenFiles"/>). The one owner of what a step reads for an
    /// input: every step of every session of the run - a resume's included - reads the file the run was started with,
    /// whatever became of the person's file since, and every agent reads it, wherever its own tree was cut from. The run's
    /// records keep the values as the person gave them.
    /// </summary>
    /// <param name="definition">The workflow the run runs.</param>
    /// <param name="given">The inputs given, by name, as the run's records keep them; null where none was.</param>
    /// <param name="run">The run, whose directory keeps the copies.</param>
    /// <returns>What the run's steps read, by input name.</returns>
    public static IReadOnlyDictionary<string, string> AsRead(WorkflowDefinition definition, IReadOnlyDictionary<string, string>? given, OrchestratorLayout run)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(run);

        var values = Resolve(definition, given);
        var read = new Dictionary<string, string>(values, StringComparer.Ordinal);

        foreach (var input in Files(definition, values))
        {
            read[input.Name] = run.InputCopy(input.Name, values[input.Name]);
        }

        return read;
    }

    /// <summary>
    /// Opens the files a run of <paramref name="definition"/> keeps of its own inputs of the kind <c>file</c>, before
    /// anything of the run is made: each such input whose value is not empty names a file inside the repository - a path
    /// from the main checkout's root, never an absolute one and with no <c>..</c> in it, in none of the tool's credential
    /// slots, which no agent reads - read from the main checkout as it stands, committed or not. Each is opened here, so
    /// the bytes the run keeps (<see cref="FlowInputFiles.Keep"/>) are those of the very file checked. Anything else is
    /// refused (13), naming the input and the path and saying that no run was started, and what was opened before it is
    /// closed. A used workflow's file input is no part of this: it names a file of that workflow's own directory, which
    /// the definition's check reads.
    /// </summary>
    /// <param name="fileSystem">Reads each file.</param>
    /// <param name="definition">The workflow the run runs.</param>
    /// <param name="values">The values the run takes, by input name (<see cref="Resolve"/>).</param>
    /// <param name="mainCheckout">The main checkout's root, which each path is read from.</param>
    /// <returns>Each file opened, by input, in the order the workflow declares them; the caller keeps and closes them.</returns>
    /// <exception cref="HarnessException">An input names no file inside the repository (<see cref="HarnessExit.Refused"/>).</exception>
    public static FlowInputFiles OpenFiles(IFileSystem fileSystem, WorkflowDefinition definition, IReadOnlyDictionary<string, string> values, string mainCheckout)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentException.ThrowIfNullOrWhiteSpace(mainCheckout);

        var opened = new FlowInputFiles();

        try
        {
            foreach (var input in Files(definition, values))
            {
                var given = values[input.Name];
                var (content, why) = Opened(fileSystem, given, mainCheckout);

                if (why is not null)
                {
                    throw new HarnessException(
                        HarnessExit.Refused,
                        $"input '{input.Name}' is '{given}', {why}; a file input names a file inside the repository by its path from the repository's root ('{mainCheckout}'), and the run keeps a copy of it from its start. No run was started.");
                }

                opened.Add(input.Name, given, content!);
            }
        }
        catch
        {
            opened.Dispose();
            throw;
        }

        return opened;
    }

    /// <summary>The run's own inputs of the kind <c>file</c> whose value in <paramref name="values"/> is not empty, in the order the workflow declares them.</summary>
    private static IEnumerable<FlowInput> Files(WorkflowDefinition definition, IReadOnlyDictionary<string, string> values)
        => definition.Inputs.Where(input => input.Kind == FlowInput.FileKind && values.TryGetValue(input.Name, out var value) && value.Length > 0);

    /// <summary>
    /// The file <paramref name="given"/> names inside the repository at <paramref name="mainCheckout"/>, opened; or why it
    /// names none, as the refusal's clause after the path says it.
    /// </summary>
    private static (Stream? Content, string? Why) Opened(IFileSystem fileSystem, string given, string mainCheckout)
    {
        if (Path.IsPathRooted(given))
        {
            return (null, "an absolute path");
        }

        if (given.Split(['/', '\\']).Any(segment => segment == ".."))
        {
            return (null, "which climbs out by '..'");
        }

        var full = Path.GetFullPath(Path.Combine(mainCheckout, given));

        if (HarnessLayout.SecretSlotDirectoriesUnder(mainCheckout).FirstOrDefault(slot => PathContainment.IsInsideOrSame(slot, full, StringComparison.OrdinalIgnoreCase)) is { } slot)
        {
            return (null, $"which is in '{slot}', a credential slot of this tool, which no agent reads");
        }

        PathKind kind;

        try
        {
            kind = fileSystem.KindOf(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"and what is at '{full}' cannot be told: {ex.Message.TrimEnd('.')}");
        }

        if (kind == PathKind.None)
        {
            return (null, $"and '{full}' is not there");
        }

        if (kind == PathKind.Directory)
        {
            return (null, $"and '{full}' is a directory");
        }

        try
        {
            return (fileSystem.OpenRead(full), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"and '{full}' could not be read: {ex.Message.TrimEnd('.')}");
        }
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

/// <summary>
/// The files a run's own inputs of the kind <c>file</c> name, each opened as the run starts (<see cref="FlowInputs.OpenFiles"/>):
/// kept in the run's directory once it is made (<see cref="Keep"/>), and closed.
/// </summary>
public sealed class FlowInputFiles : IDisposable
{
    private readonly List<(string Name, string Given, Stream Content)> _opened = [];

    /// <summary>Holds the file <paramref name="content"/> opened for input <paramref name="name"/>, given as <paramref name="given"/>.</summary>
    internal void Add(string name, string given, Stream content) => _opened.Add((name, given, content));

    /// <summary>
    /// Copies each file, a little at a time and never held whole, to the copy the run keeps of it
    /// (<see cref="OrchestratorLayout.InputCopy"/>), written whole or not at all, its directory made where it is not there.
    /// </summary>
    /// <param name="fileSystem">Writes each copy.</param>
    /// <param name="run">The run, made: its directory keeps the copies.</param>
    public void Keep(IFileSystem fileSystem, OrchestratorLayout run)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(run);

        foreach (var (name, given, content) in _opened)
        {
            fileSystem.WriteAtomic(run.InputCopy(name, given), copy => content.CopyTo(copy));
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var (_, _, content) in _opened)
        {
            content.Dispose();
        }

        _opened.Clear();
    }
}
