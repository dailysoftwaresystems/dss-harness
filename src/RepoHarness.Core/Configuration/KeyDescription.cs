namespace RepoHarness.Core.Configuration;

/// <summary>One key a configuration file takes, with what it does, as help lists it.</summary>
/// <param name="Name">The key, spelled as the file writes it.</param>
/// <param name="Meaning">What it does, in one line.</param>
public sealed record KeyDescription(string Name, string Meaning)
{
    /// <summary>Whether the file must name it.</summary>
    public bool Required { get; init; }

    /// <summary>The keys its value takes in turn, when that value is a section of its own; empty otherwise.</summary>
    public IReadOnlyList<KeyDescription> Keys { get; init; } = [];

    /// <summary>The names in <paramref name="keys"/>, in order, as a refusal lists them.</summary>
    /// <param name="keys">The keys one part of a file takes.</param>
    public static IReadOnlyList<string> Names(IReadOnlyList<KeyDescription> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        return [.. keys.Select(key => key.Name)];
    }
}
