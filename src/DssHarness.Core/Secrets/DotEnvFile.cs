namespace DssHarness.Core.Secrets;

/// <summary>
/// Reads a file of <c>NAME=value</c> lines. Names compare exactly, case included, as an environment
/// name does on every system but Windows: a reader that accepted <c>address</c> for <c>ADDRESS</c>
/// would also accept it for a second, different entry, and the file would then mean two things.
/// A refusal names the key it wanted, so an exact match costs nothing to satisfy.
/// </summary>
/// <remarks>
/// <para>
/// A blank line and a line whose first non-blank character is <c>#</c> are skipped, as is a line with
/// no <c>=</c>. A <c>#</c> anywhere else is an ordinary character and stays in the value: a generated
/// credential contains one often enough that stripping it would produce a value that is almost
/// right, which fails later and somewhere else. Surrounding whitespace is removed from the name and
/// from the value, and one matching pair of surrounding quotes is removed from the value, so a value
/// whose own spaces matter can be written with them: a credential ending in a space is otherwise
/// silently shortened, and the login it fails at says only that the password was wrong.
/// </para>
/// <para>
/// The one <c>NAME=value</c> line scanner this tool has: every reader of a file of this shape reads
/// through <see cref="Scan"/>, so two files written the same way cannot be read two ways. What a
/// reader does with a line that defines nothing is the reader's own - this one skips it, and the
/// runner's values directory reports it with its line number.
/// </para>
/// </remarks>
public static class DotEnvFile
{
    /// <summary>Reads <paramref name="text"/> into its names and values, skipping every line that defines nothing.</summary>
    public static IReadOnlyDictionary<string, string> Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in Scan(text))
        {
            if (line.Name is { } name)
            {
                values[name] = line.Value!;
            }
        }

        return values;
    }

    /// <summary>
    /// Reads <paramref name="text"/> one line at a time: each line a definition holds, or the fault
    /// of one that holds none.
    /// </summary>
    /// <param name="text">The file's contents.</param>
    public static IEnumerable<DotEnvLine> Scan(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return ScanLines(text);
    }

    /// <summary>The scan itself, an iterator begun only once its text is known not to be null.</summary>
    private static IEnumerable<DotEnvLine> ScanLines(string text)
    {
        var lines = text.Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            // The trailing carriage return of a file written on Windows is part of no value: a port
            // read as "22\r" is not a number, and an address read that way resolves nowhere.
            var line = lines[index].Trim();

            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);

            if (separator < 0)
            {
                yield return new DotEnvLine(index + 1, null, null, DotEnvLineFault.NoEquals);
                continue;
            }

            var name = line[..separator].Trim();

            if (name.Length == 0)
            {
                yield return new DotEnvLine(index + 1, null, null, DotEnvLineFault.EmptyName);
                continue;
            }

            yield return new DotEnvLine(index + 1, name, Unquote(line[(separator + 1)..].Trim()), null);
        }
    }

    /// <summary>
    /// Removes one matching pair of surrounding quotes, and nothing else.
    /// </summary>
    /// <param name="value">The text after the <c>=</c>, already trimmed.</param>
    /// <remarks>
    /// Shared with every other reader of this shape, so two files written the same way cannot be
    /// read two ways. One pair, not all of them: a value that is meant to carry a quote keeps it.
    /// </remarks>
    public static string Unquote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0]
            ? value[1..^1]
            : value;
    }
}

/// <summary>One line of a dotenv file, as the one scanner read it.</summary>
/// <param name="Number">The line's number, from one.</param>
/// <param name="Name">The name the line defines, where it defines one.</param>
/// <param name="Value">The value the line defines, where it defines one.</param>
/// <param name="Fault">Why the line defines nothing, where it does not.</param>
public readonly record struct DotEnvLine(int Number, string? Name, string? Value, DotEnvLineFault? Fault);

/// <summary>Why a line of a dotenv file defines nothing.</summary>
public enum DotEnvLineFault
{
    /// <summary>The line has no <c>=</c>, so nothing is defined on it.</summary>
    NoEquals,

    /// <summary>The line's name, before its <c>=</c>, is empty.</summary>
    EmptyName,
}
