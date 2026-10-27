namespace DssHarness.Core.Processes;

/// <summary>
/// A batch file as Windows starts one: through cmd.exe, which reads the command line a second time, so an argument
/// holding what cmd reads as its own - a <c>&amp;</c>, a <c>|</c>, a <c>%</c> - runs or changes another command than the
/// one passed. The one rule of which program is one and which argument it would read again, held by the runner that
/// starts it and by the policy that decides what an agent runs.
/// </summary>
public static class BatchFiles
{
    /// <summary>
    /// What cmd.exe reads as its own in a line it is handed, quoted or not: its separators and redirections, its escape, a
    /// variable's <c>%</c> and a delayed one's <c>!</c>, a quote that turns its quoting, and a line's end.
    /// </summary>
    private static readonly char[] CmdOwn = ['&', '|', '<', '>', '^', '%', '!', '"', '\r', '\n'];

    /// <summary>The characters cmd.exe reads as its own, as help says them: each that prints, then a line's end.</summary>
    public static string Said { get; } = string.Join(' ', CmdOwn.Where(character => !char.IsControl(character))) + ", a line's end";

    /// <summary>
    /// Whether <paramref name="program"/> names a batch file: <c>.bat</c> or <c>.cmd</c> in any case, the dots and spaces
    /// Windows drops from a name's end dropped first.
    /// </summary>
    /// <param name="program">The program as it is named to start.</param>
    public static bool Is(string program)
    {
        ArgumentNullException.ThrowIfNull(program);

        var extension = Path.GetExtension(program.TrimEnd('.', ' '));

        return extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The first of <paramref name="arguments"/> cmd.exe would read again as its own; <see langword="null"/> where none would be.</summary>
    /// <param name="arguments">A batch file's arguments.</param>
    public static string? Reread(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        return arguments.FirstOrDefault(argument => argument.IndexOfAny(CmdOwn) >= 0);
    }
}
