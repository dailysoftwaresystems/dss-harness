using System.Buffers;

namespace RepoHarness.Core.FileSystem;

/// <summary>Names made into file names: a leg's, a step's, a machine's.</summary>
public static class FileNames
{
    /// <summary>
    /// Characters a name may keep when it becomes a file name: the ones every platform this tool
    /// runs on accepts. Parentheses are among them, because the runner itself puts them in the name
    /// of a step with several lines.
    /// </summary>
    private static readonly SearchValues<char> SafeCharacters =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_. ()");

    /// <summary>
    /// <paramref name="name"/> with everything a file name cannot safely carry replaced, so a leg
    /// or a step named after a path or a flag still gets a file of its own.
    /// </summary>
    /// <param name="name">The name to make safe.</param>
    public static string SafeFor(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return string.Create(name.Length, name, static (span, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                span[index] = SafeCharacters.Contains(source[index]) ? source[index] : '-';
            }
        });
    }
}
