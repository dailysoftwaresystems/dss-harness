namespace DssHarness.Core.Agents.Policy;

/// <summary>
/// One command as <see cref="CommandAnalysis"/> reads it: the words its program is handed, its environment first where
/// it has one, and the redirects the shell makes for it, which no program is handed - kept apart as the shell reader
/// meets them, so no word is ever read as a redirect by how it is spelled.
/// </summary>
/// <param name="Words">The words: the environment's assignments, then the program and its arguments.</param>
/// <param name="Redirects">The redirects, in the order the command spells them.</param>
/// <param name="Quoted">
/// How each word's characters stand to the shell that read them, one character of <see cref="WordQuoting"/> for each of
/// the word's: which parts were quoted, so an expansion is read only where the shell would expand it. Short where a
/// word's spelling was not kept - a word a wrapper or a re-reading made - which reads as wholly open, so a word whose
/// spelling is unknown is refused as an expansion exactly as it was before any of this was read.
/// </param>
public sealed record AnalyzedCommand(IReadOnlyList<string> Words, IReadOnlyList<ShellRedirect> Redirects, IReadOnlyList<string>? Quoted = null)
{
    /// <summary>The command whole, as a message names it: its words, then each redirect as it is spelled.</summary>
    public IReadOnlyList<string> Whole => [.. Words, .. Redirects.Select(redirect => redirect.ToString())];

    /// <summary>
    /// How one word's characters stand to the shell, where the word's own spelling was kept: <see cref="WordQuoting"/>
    /// for each of its characters, in step.
    /// </summary>
    /// <param name="at">Which word.</param>
    public string QuotedWord(int at)
        => Quoted is not null && at < Quoted.Count && at < Words.Count && Quoted[at].Length == Words[at].Length ? Quoted[at] : string.Empty;
}

/// <summary>
/// How one character of a word stands to the shell that read it, as <see cref="AnalyzedCommand.Quoted"/> holds one for
/// each of a word's characters: what the shell would expand at that place, and what it never would.
/// </summary>
public static class WordQuoting
{
    /// <summary>
    /// A character the shell may expand everything at: unquoted, so a variable, a substitution, a pattern and a brace
    /// are all read as what they are.
    /// </summary>
    public const char Open = ' ';

    /// <summary>
    /// A character inside double quotes, where a dollar, a backtick, a cmd percent and a delayed name still expand and
    /// a glob, a bracket and a brace do not: <c>"$x"</c> is a variable, <c>"*"</c> is a star.
    /// </summary>
    public const char DoubleQuoted = 'd';

    /// <summary>A character the shell hands on as it stands - single-quoted, or a backslash's escape: nothing expands.</summary>
    public const char Literal = 'l';
}

/// <summary>
/// One reading of a command for its rules (<see cref="CommandAnalysis.ForRules(IReadOnlyList{string}, bool, IReadOnlyList{string}?)"/>):
/// the words the rules match against, and how each stands to the shell where the command's own spelling was kept, so a
/// word is read as an expansion only where the shell would expand it.
/// </summary>
/// <param name="Words">The words, as the reading spells them.</param>
/// <param name="Quoted">How each word's characters stand to the shell, kept in step with <paramref name="Words"/>.</param>
public sealed record RuleReading(IReadOnlyList<string> Words, IReadOnlyList<string> Quoted)
{
    /// <summary>
    /// How the word at <paramref name="at"/> stands to the shell, or null where its spelling was not kept - a word a
    /// reading made - which reads as wholly open.
    /// </summary>
    /// <param name="at">Which word.</param>
    public string? QuotedAt(int at)
        => at >= 0 && at < Quoted.Count && at < Words.Count && Quoted[at].Length == Words[at].Length ? Quoted[at] : null;
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
