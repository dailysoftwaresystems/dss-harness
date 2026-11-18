using System.Text.Json;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;

namespace DssHarness.Core.Configuration;

/// <summary>
/// What a machine declares of itself, for the user running this process, in its settings file
/// (<see cref="MachineSettingsFile"/>): what no tree's config.json can say for it, since every worktree holds the copy of
/// config.json its last commit made.
/// </summary>
public sealed class MachineSettings
{
    /// <summary>
    /// How heavy legs are admitted onto this machine, in the keys config.json's <c>admission</c> takes and by the same
    /// rules: each field it declares wins over what every tree's config.json declares for the machine, under its own host's
    /// section or under <c>defaults</c>, which stay the rule for each field it leaves out.
    /// </summary>
    public AdmissionSettings? Admission { get; init; }
}

/// <summary>What a machine's settings file says, as a survey of that machine answers it.</summary>
/// <param name="Path">
/// The file, as that machine shows it - its home written <c>~</c> where it answers another machine; <see langword="null"/>
/// where no directory of the user's own could be named there, and the machine declares nothing of itself.
/// </param>
/// <param name="Admission">Its <c>admission</c>, or <see langword="null"/> where it declares none or could not be read.</param>
/// <param name="Problem">Why it could not be read, naming it, where it could not; every leg it would admit is refused then.</param>
public sealed record MachineSettingsAnswer(string? Path, AdmissionSettings? Admission = null, string? Problem = null);

/// <summary>
/// This machine's settings file: <c>settings-&lt;machine id&gt;.json</c> in this user's own directory for this tool
/// (<see cref="UserState"/>), beside the records of the heavy legs admitted onto it - read by every command this user runs
/// here, whichever repository or worktree it runs in, and never by another machine's. Read as config.json is read: a key
/// it does not know, a key written twice or a value no machine could admit by is refused, naming the file, and never read
/// past. A file that is not there declares nothing.
/// </summary>
/// <remarks>
/// One per machine, named by what tells the machine from every other, as the admission records are: a home two machines
/// share holds a file for each, and what one machine can hold is never read as the other's.
/// </remarks>
/// <param name="fileSystem">Reads the file.</param>
/// <param name="path">Names the file, asked the first time it is read.</param>
public sealed class MachineSettingsFile(IFileSystem fileSystem, Func<string> path)
{
    /// <summary>What the file's name begins with.</summary>
    public const string FilePrefix = "settings";

    private readonly IFileSystem _fileSystem = fileSystem;

    // The file, or why no directory of this user's own could be named to hold it: asked once.
    private readonly Lazy<(string? Path, string? Nowhere)> _path = new(() =>
    {
        try
        {
            return (path(), null);
        }
        catch (DirectoryNotFoundException ex)
        {
            return (null, ex.Message.TrimEnd('.'));
        }
    });

    /// <summary>The settings file at <paramref name="path"/>.</summary>
    /// <param name="fileSystem">Reads the file.</param>
    /// <param name="path">The file.</param>
    public MachineSettingsFile(IFileSystem fileSystem, string path)
        : this(fileSystem, () => path)
    {
    }

    /// <summary>
    /// The settings file of <paramref name="platform"/>'s machine, for the user running this process. Where the machine can
    /// be told only by its name, that is said, with why: a command started after the name changes reads the file named by
    /// the new one.
    /// </summary>
    /// <param name="platform">Tells this machine from every other.</param>
    /// <param name="output">Says the file is named by the machine's name, where it is.</param>
    /// <exception cref="DirectoryNotFoundException">No directory of this user's own could be named.</exception>
    public static string PathFor(IHostPlatform platform, IHarnessOutput output)
        => UserState.MachineFile(platform, output, Execution.HeavyLegSlots.CommandName, FilePrefix, "settings", "reads the settings file named by the new name, not the old one");

    /// <summary>The file's name for the machine <paramref name="machineId"/> tells apart.</summary>
    /// <param name="machineId">What tells the machine from every other.</param>
    public static string FileNameFor(string machineId) => UserState.MachineFileName(FilePrefix, machineId);

    /// <summary>
    /// The file; <see langword="null"/> where no directory of this user's own could be named, where this machine declares
    /// nothing of itself.
    /// </summary>
    public string? Location => _path.Value.Path;

    /// <summary>What the file declares; nothing where it is not there, or no directory of this user's own could be named.</summary>
    /// <exception cref="ConfigException">
    /// The file is there and could not be read, is not what a settings file holds - a key it does not know, a key written
    /// twice, no object at all - or declares an admission no machine could admit by: refused, naming it, so a machine
    /// whose own count could not be read is never admitted by a tree's.
    /// </exception>
    public MachineSettings Read()
    {
        if (_path.Value.Path is not { } file)
        {
            return new MachineSettings();
        }

        string text;

        try
        {
            if (!_fileSystem.FileExists(file))
            {
                return new MachineSettings();
            }

            text = _fileSystem.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Refused(file, ex.Message.TrimEnd('.'), ex);
        }

        MachineSettings? settings;

        try
        {
            settings = JsonSerializer.Deserialize<MachineSettings>(text, JsonConfigOptions.Default);
        }
        catch (JsonException ex)
        {
            throw Refused(file, ex.Message.TrimEnd('.'), ex);
        }

        if (settings is null)
        {
            throw Refused(file, "it holds null where an object is expected", null);
        }

        var problems = new List<string>();

        HarnessConfigValidator.ValidateAdmission(settings.Admission, "admission", problems);

        return problems.Count == 0
            ? settings
            : throw Refused(file, string.Join("; ", problems), null);
    }

    /// <summary>
    /// What the file says, as a survey of this machine answers it: where it is, with <paramref name="shown"/>, and its
    /// admission, or why it could not be read. Never throws: a survey says what it found.
    /// </summary>
    /// <param name="shown">How a path is shown where it is answered: this machine's home as <c>~</c> for another machine.</param>
    public MachineSettingsAnswer Answer(Func<string, string> shown)
    {
        ArgumentNullException.ThrowIfNull(shown);

        try
        {
            var settings = Read();

            return new MachineSettingsAnswer(Location is { } file ? shown(file) : null, settings.Admission);
        }
        catch (ConfigException ex)
        {
            return new MachineSettingsAnswer(Location is { } file ? shown(file) : null, Problem: shown(ex.Message));
        }
    }

    /// <summary>The refusal of the file at <paramref name="file"/>, for <paramref name="why"/>.</summary>
    private static ConfigException Refused(string file, string why, Exception? inner)
        => new(
            $"This machine's settings file '{file}' could not be read: {why}. Until it can be, no heavy leg is admitted onto this "
            + "machine by it, nor by any tree's config.json in its place; put it right, or remove it to admit by config.json alone.",
            inner);
}
