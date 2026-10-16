namespace DssHarness.Core.Processes;

/// <summary>
/// The directories a survey found the programs of a host in, which every process a leg there starts has
/// added to its PATH: the ones a tool's minimum version raised, ahead of the PATH the process inherits, and
/// every other one after it.
/// </summary>
/// <remarks>
/// <para>
/// Appended, as a rule: every name the PATH already answers for keeps that answer, and only a name it does
/// not know becomes findable. This is how a program found off the PATH of a command run without a login
/// shell - cmake in /opt/homebrew/bin - is started, and how the programs it starts by name in turn, ninja and
/// the compilers, are found where it was. Added after an environment that sets a PATH of its own, too: a
/// program a survey found is then still found by the run, which would otherwise look for it only where that
/// environment says.
/// </para>
/// <para>
/// Raised, as the one exception: a declared tool whose first copy on the PATH is older than its
/// <c>minVersion</c> allows is found where its first copy that is not lives, and that directory goes ahead of
/// the PATH, so that every process a leg starts - and every one those start by the tool's name - finds that
/// copy. Measured on a Mac: the PATH of a command run over ssh names macOS's own python3, 3.9.6, first, and a
/// leg whose configuration needs 3.12 ran its steps under it without a word. Every other program in a raised
/// directory comes ahead of the PATH with it, as it does in a shell Homebrew's own environment set up. A host
/// answers for all of its legs at once, so the directory is ahead of the PATH of every leg there, the ones the
/// tool is not declared for too; and ahead of a PATH a configuration or a developer environment sets as well, so
/// the copy chosen is the one started whichever PATH it goes ahead of.
/// </para>
/// </remarks>
/// <param name="Raised">The directories put ahead of the PATH, in order.</param>
/// <param name="Appended">The directories put after it, in order.</param>
public sealed record ProgramDirectories(IReadOnlyList<string> Raised, IReadOnlyList<string> Appended)
{
    /// <summary>Nothing added.</summary>
    public static ProgramDirectories None { get; } = new([], []);

    /// <summary>Whether nothing is added.</summary>
    public bool IsEmpty => Raised.Count == 0 && Appended.Count == 0;

    /// <summary>Only <paramref name="appended"/>, after the PATH.</summary>
    public static ProgramDirectories After(IReadOnlyList<string> appended) => new([], appended);

    /// <summary>
    /// Every directory, the raised ones first: where the programs a survey found are, for a process given only a
    /// list to look in after its PATH - a host's <c>keepAwake</c> command, which is no declared tool and is held to
    /// no minimum, and so is found wherever the survey found it.
    /// </summary>
    public IReadOnlyList<string> All => [.. Raised, .. Appended];

    /// <summary>
    /// <paramref name="path"/> with these directories around it, joined by <paramref name="separator"/>; or
    /// <paramref name="path"/> itself where none is added.
    /// </summary>
    /// <param name="path">The PATH, or <see langword="null"/> where there is none.</param>
    /// <param name="separator">What separates two directories in it.</param>
    public string? Around(string? path, char separator)
        => IsEmpty
            ? path
            : string.Join(separator, Raised.Append(path).Concat(Appended).Where(part => !string.IsNullOrEmpty(part)));

    /// <summary>
    /// <paramref name="path"/> with the raised directories ahead of it and nothing after: the PATH a program is
    /// looked up on by name, the appended directories being looked in one at a time after it.
    /// </summary>
    /// <param name="path">The PATH, or <see langword="null"/> where there is none.</param>
    /// <param name="separator">What separates two directories in it.</param>
    public string? Ahead(string? path, char separator) => (this with { Appended = [] }).Around(path, separator);
}
