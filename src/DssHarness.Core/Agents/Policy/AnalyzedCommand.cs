namespace DssHarness.Core.Agents.Policy;

/// <summary>
/// One command as <see cref="CommandAnalysis"/> reads it: the words its program is handed, its environment first where
/// it has one, and the redirects the shell makes for it, which no program is handed - kept apart as the shell reader
/// meets them, so no word is ever read as a redirect by how it is spelled.
/// </summary>
/// <param name="Words">The words: the environment's assignments, then the program and its arguments.</param>
/// <param name="Redirects">The redirects, in the order the command spells them.</param>
public sealed record AnalyzedCommand(IReadOnlyList<string> Words, IReadOnlyList<ShellRedirect> Redirects)
{
    /// <summary>The command whole, as a message names it: its words, then each redirect as it is spelled.</summary>
    public IReadOnlyList<string> Whole => [.. Words, .. Redirects.Select(redirect => redirect.ToString())];
}

/// <summary>A redirect the shell makes for a command: the descriptor it sets, its operator, and the file it names or the text it gives.</summary>
/// <param name="Descriptor">The descriptor it sets: the number before it, or its operator's own - 0 for an input, 1 for an output.</param>
/// <param name="Operator">
/// <c>&lt;</c>, <c>&gt;</c>, <c>&gt;&gt;</c> or <c>&lt;&gt;</c> - each spelling of the shell's folded to one of these -
/// <c>&lt;&lt;&lt;</c> for the text a here-document or a here-string gives, or <c>&lt;&amp;</c> for another descriptor
/// duplicated onto the input, the one duplication kept.
/// </param>
/// <param name="Target">The file it names, the text it gives, or the descriptor it duplicates.</param>
public sealed record ShellRedirect(int Descriptor, string Operator, string Target)
{
    /// <summary>The redirect as one word: its operator, then its file or its text.</summary>
    public override string ToString() => Operator + Target;
}
