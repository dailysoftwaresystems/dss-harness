using RepoHarness.Core.FileSystem;

namespace RepoHarness.Core.Execution;

/// <summary>One phase's log among a leg's records.</summary>
/// <param name="Phase">The phase, as its log is named for it.</param>
/// <param name="File">The log.</param>
public sealed record PhaseLogFile(string Phase, string File);

/// <summary>
/// How a phase's log is named, said once for everything that writes one and for what lists the logs that are there.
/// </summary>
public static class PhaseLogs
{
    /// <summary>What every phase log's name ends with, after the phase.</summary>
    public const string Extension = ".log";

    /// <summary>The log of <paramref name="phase"/> in <paramref name="directory"/>.</summary>
    /// <param name="directory">The directory of records the phase's log is kept in: a leg's, an arm's, a control's.</param>
    /// <param name="phase">The phase, as its log is named for it.</param>
    public static string In(string directory, string phase) => Path.Combine(directory, phase + Extension);

    /// <summary>The phase <paramref name="file"/> is the log of, or <see langword="null"/> where it is no phase's log.</summary>
    /// <param name="file">A file's name or its path.</param>
    public static string? PhaseOf(string file)
    {
        var name = Path.GetFileName(file);

        return name.Length > Extension.Length && name.EndsWith(Extension, StringComparison.Ordinal) ? name[..^Extension.Length] : null;
    }
}

/// <summary>
/// Where a run keeps one leg's records: the one place that says it, for everything that writes there and for what
/// names what was written.
/// </summary>
/// <remarks>
/// Scoped to the leg inside the run, because one run's legs run at once under one run id: a file two legs shared
/// would be read as either's. The run directory is the one the work was given - the command's own, in the tree it
/// was typed in - and never one composed again from a layout, so a leg whose work happens in another tree still
/// keeps its records with its run's.
/// </remarks>
/// <param name="RunDirectory">The run's own directory.</param>
/// <param name="Leg">The leg, as the configuration names it.</param>
public sealed record LegRecords(string RunDirectory, string Leg)
{
    /// <summary>The leg's own directory in the run's.</summary>
    public string Directory => Path.Combine(RunDirectory, Leg);

    /// <summary>
    /// What the leg keeps <paramref name="below"/> its own directory, part by part: a sweep's <c>arms/&lt;arm&gt;</c>,
    /// its <c>controls/&lt;runner&gt;</c>.
    /// </summary>
    /// <param name="below">Each directory's name on the way down from the leg's own.</param>
    public string Under(params string[] below) => Path.Combine([Directory, .. below]);

    /// <summary>
    /// What lines call the records <paramref name="below"/> the leg's own, and what work kept there goes by:
    /// <c>&lt;leg&gt;/arms/&lt;arm&gt;</c>, the leg's name and each part after it, whatever this machine separates a
    /// path with.
    /// </summary>
    /// <param name="below">Each directory's name on the way down from the leg's own.</param>
    public string Named(params string[] below) => string.Join('/', [Leg, .. below]);

    /// <summary>The log of <paramref name="phase"/>, in the leg's directory.</summary>
    /// <param name="phase">The phase, as its log is named for it.</param>
    public string PhaseLog(string phase) => PhaseLogs.In(Directory, phase);

    /// <summary>
    /// The phase logs that are in the leg's directory now, each with its phase, in the order of their names: none of
    /// what is kept below it, and none where the leg kept nothing.
    /// </summary>
    /// <param name="fileSystem">Lists the directory.</param>
    public IReadOnlyList<PhaseLogFile> Logs(IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        if (!fileSystem.DirectoryExists(Directory))
        {
            return [];
        }

        // Each named again as its writer named it, from its phase: what is listed is what was written.
        return
        [
            .. fileSystem.EnumerateFiles(Directory, recursive: false)
                .Select(PhaseLogs.PhaseOf)
                .OfType<string>()
                .Order(StringComparer.Ordinal)
                .Select(phase => new PhaseLogFile(phase, PhaseLog(phase))),
        ];
    }

    /// <summary>
    /// How a line names the records a leg kept: <c>logs of &lt;leg&gt; on &lt;host&gt;: &lt;directory&gt; (build.log, test.log)</c> -
    /// the host only where another ran the leg, and the files by name, in the order of their names.
    /// </summary>
    /// <param name="leg">The leg, as the configuration names it.</param>
    /// <param name="host">The host that ran it, or <see langword="null"/> where this machine did.</param>
    /// <param name="directory">The leg's own directory of records, as its machine spells it.</param>
    /// <param name="files">Each log file in it, by name, in the order of their names.</param>
    public static string Said(string leg, string? host, string directory, IReadOnlyList<string> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leg);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(files);

        return $"logs of {leg}{(host is { Length: > 0 } named ? $" on {named}" : string.Empty)}: {directory}"
            + (files.Count > 0 ? $" ({string.Join(", ", files)})" : string.Empty);
    }
}
