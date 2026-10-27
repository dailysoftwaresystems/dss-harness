namespace DssHarness.Core.Processes;

/// <summary>
/// What a command this tool starts for git or the forge is told, so that it cannot ask anyone anything: git's own
/// prompt for a password, a credential manager's window, and the forge's prompts. A command waiting for an answer
/// nobody will give hangs with no output - the one failure this tool cannot report - so every git command and every
/// <c>gh</c> call is started with these, from this one place.
/// </summary>
public static class UnattendedCommands
{
    /// <summary>Each variable, with the value that tells its reader nobody is there to answer.</summary>
    public static IReadOnlyDictionary<string, string> Variables { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GH_PROMPT_DISABLED"] = "1",
        ["GCM_INTERACTIVE"] = "never",
        ["GIT_TERMINAL_PROMPT"] = "0",
    };

    /// <summary>Sets each of <see cref="Variables"/> in <paramref name="environment"/>.</summary>
    /// <param name="environment">The variables a command is started with.</param>
    public static void Apply(IDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        foreach (var (name, value) in Variables)
        {
            environment[name] = value;
        }
    }
}
