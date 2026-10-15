namespace DssHarness.Core.Configuration;

/// <summary>Finds the spelling a configuration declares a name with.</summary>
/// <remarks>
/// Configuration keys compare ignoring case, so a name may be typed in any case. Some programs a name is
/// handed to compare exactly: ssh applies a <c>Host</c> entry only to the name spelt as the entry spells it.
/// What reaches such a program is therefore the declared spelling, never the typed one.
/// </remarks>
public static class DeclaredName
{
    /// <summary>
    /// The spelling <paramref name="names"/> declares <paramref name="typed"/> with, or <see langword="null"/>
    /// when none of them is that name.
    /// </summary>
    public static string? In(IEnumerable<string> names, string typed)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(typed);

        return names.FirstOrDefault(name => string.Equals(name, typed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The refusal of <paramref name="typed"/> where <paramref name="section"/> declares no such name:
    /// <c>'x' is not declared under predefinedRunners. Declared: a, b.</c>
    /// </summary>
    /// <param name="typed">The name as the command line gave it.</param>
    /// <param name="section">The section of <c>config.json</c> its names are declared in.</param>
    /// <param name="names">The names declared there.</param>
    public static string NotDeclared(string typed, string section, IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(typed);
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(names);

        var declared = names.Order(StringComparer.Ordinal).ToList();

        return $"'{typed}' is not declared under {section}. "
            + (declared.Count == 0 ? "Nothing is declared there." : $"Declared: {string.Join(", ", declared)}.");
    }
}
