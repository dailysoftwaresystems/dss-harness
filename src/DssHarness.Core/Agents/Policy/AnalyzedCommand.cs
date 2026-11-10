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
    /// The words of each command that runs this one, outermost first, as each stood: a wrapper stripped before the
    /// program (<c>env</c>, <c>exec</c>, <c>nohup</c>), and a shell or a program that runs it as its script or its words
    /// (<c>bash -c</c>, <c>pwsh -c</c>, <c>find -exec</c>, <c>eval</c>, git's settings). Their own options say what this
    /// one inherits - an environment taken away (<c>env -i</c>, <c>env -u NAME</c>, <c>exec -c</c>), a shell's setting
    /// (<c>bash -O dotglob</c>), the shell that reads it. Empty where nothing runs it but the shell the string was given to.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> Wrappers { get; init; } = [];

    /// <summary>
    /// Whether what the command reads as its input is given by what stands around it rather than by its own redirects:
    /// it stands after a pipe, or in a group, loop, conditional or subshell that does or whose input is redirected, or
    /// a command that runs it is given its input so. False where only the agent's own command gives it its input.
    /// </summary>
    public bool InputGiven { get; init; }

    /// <summary>
    /// How many subshells and other processes stand between the command and the agent's own shell: a parenthesis, a
    /// substitution, a script another program runs, a program another starts elsewhere. A move at a level ends with it.
    /// </summary>
    public int Level { get; init; }

    /// <summary>
    /// How the command is joined to the command before it at its level: <c>&amp;&amp;</c>, run only where that one held;
    /// <c>||</c>, only where it did not; null after any other separator, or where it is the first.
    /// </summary>
    public string? Joined { get; init; }

    /// <summary>
    /// Whether the shell runs the command in the background - a list a lone <c>&amp;</c> ends, the command and every command
    /// of that list: its pipes, its <c>&amp;&amp;</c> and <c>||</c>, the groups and subshells in it - so the shell goes on, and
    /// may end, while the command still runs. A command a script runs is in the background where the command that runs the
    /// script is.
    /// </summary>
    public bool Background { get; init; }

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
    /// Whether the reading is one the analysis makes of a configuration set where the command runs - the <c>git config</c>
    /// or <c>gh config set</c> it is - rather than the command's own words: a write of that configuration whatever words
    /// it carries after its command, so none of them reads as a mode of the command that only reads
    /// (<see cref="CommandAnalysis.GitReadsOnly"/>).
    /// </summary>
    public bool Configures { get; init; }

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
