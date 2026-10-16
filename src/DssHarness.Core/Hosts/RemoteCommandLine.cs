using System.Buffers;

namespace DssHarness.Core.Hosts;

/// <summary>
/// Which kind of shell an ssh server hands a command line to. It decides how the path to a
/// program has to be spelt, so it is measured once per connection rather than assumed.
/// </summary>
public enum RemoteShell
{
    /// <summary>
    /// A shell that leaves <c>%VAR%</c> alone and accepts forward slashes in a program's path: sh,
    /// bash, zsh, fish and tcsh on any system.
    /// </summary>
    Standard,

    /// <summary>cmd.exe, the default shell of OpenSSH on Windows, which needs backslashes.</summary>
    Cmd,

    /// <summary>
    /// PowerShell, which an OpenSSH server on Windows can be set to hand commands to. It reads a backslash
    /// in a word literally, as <see cref="Cmd"/> does, and writes to standard error in a way of its own.
    /// </summary>
    PowerShell,
}

/// <summary>
/// Builds the one line an ssh server passes to its shell. Nothing is quoted. A token is refused
/// unless every character in it is one that sh, bash, zsh, fish, cmd and PowerShell all read
/// literally, so the shell receives exactly what was built, whichever shell it is. Anything that
/// cannot meet that, a path with a space or a user's arguments, travels on standard input instead.
/// The one line ahead of the command is the harness's own, written in the syntax of the shell
/// measured: the mark it has the shell write first (see <see cref="Marked"/>).
/// </summary>
public static class RemoteCommandLine
{
    /// <summary>
    /// The command line that tells the shells apart: cmd expands <c>%COMSPEC%</c> to its own path, and
    /// every other shell prints it as written - a POSIX shell both words on one line, and PowerShell,
    /// whose echo writes each argument as an object of its own, each on a line of its own.
    /// </summary>
    public const string ShellProbe = "echo %COMSPEC% %COMSPEC%";

    /// <summary>Letters, digits, and punctuation no supported shell gives a meaning inside a word.</summary>
    private const string LiteralCharacters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._-/=:+";

    private static readonly SearchValues<char> Literal = SearchValues.Create(LiteralCharacters);

    /// <summary>The same, and the backslash a Windows path needs, which cmd and PowerShell read literally and a POSIX shell would consume.</summary>
    private static readonly SearchValues<char> CmdLiteral = SearchValues.Create(LiteralCharacters + "\\");

    /// <summary>Joins <paramref name="tokens"/> into a command line for <paramref name="shell"/>.</summary>
    /// <exception cref="ArgumentException">A token is empty, or holds a character a shell could reinterpret.</exception>
    public static string Join(IReadOnlyList<string> tokens, RemoteShell shell)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        foreach (var token in tokens)
        {
            if (!IsLiteral(token, shell))
            {
                throw new ArgumentException(
                    $"'{token}' cannot be passed to a remote shell without quoting; it has to travel on standard input.",
                    nameof(tokens));
            }
        }

        return string.Join(' ', tokens);
    }

    /// <summary>
    /// Whether <paramref name="shell"/> is one of Windows's own, cmd or PowerShell: one with neither <c>command -v</c>
    /// nor a POSIX <c>ls</c>, on a host whose programs are on its machine PATH.
    /// </summary>
    public static bool IsWindows(RemoteShell shell) => shell is RemoteShell.Cmd or RemoteShell.PowerShell;

    /// <summary>
    /// Whether <paramref name="token"/> can travel in a command line for <paramref name="shell"/>
    /// unchanged. Asked before a path is used rather than after <see cref="Join"/> has thrown, so that
    /// a program found at a path holding a space is passed over instead of stopping the host.
    /// </summary>
    public static bool IsLiteral(string token, RemoteShell shell)
        => !string.IsNullOrEmpty(token)
            && !token.AsSpan().ContainsAnyExcept(IsWindows(shell) ? CmdLiteral : Literal);

    /// <summary>Reads which kind of shell answered <see cref="ShellProbe"/>.</summary>
    public static RemoteShell ReadShellProbe(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        return output.Contains("%COMSPEC% %COMSPEC%", StringComparison.OrdinalIgnoreCase) ? RemoteShell.Standard
            : output.Contains("%COMSPEC%", StringComparison.OrdinalIgnoreCase) ? RemoteShell.PowerShell
            : RemoteShell.Cmd;
    }

    /// <summary>
    /// <paramref name="commandLine"/> as the line handed to <paramref name="shell"/>, which first writes
    /// <paramref name="mark"/> as a line of its own on standard output and then on standard error.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A shell an ssh server starts can run the account's startup files before the command - bash run by
    /// sshd reads <c>~/.bashrc</c> - and whatever they print arrives first, on the same streams the command
    /// writes. Measured on one consumer's Mac, every command's error carried the lines emsdk's environment
    /// script prints, and an install's failure quoted them in place of the shell's own words. Everything a
    /// stream carries up to the mark is the shell's, and is dropped (see <see cref="MarkedText"/>).
    /// </para>
    /// <para>
    /// The only line built here that is not a list of literal words: each shell's syntax for running one
    /// command after another and writing to standard error differs, and the mark is a word of the harness's
    /// own, written by the harness. The command itself is joined by <see cref="Join"/> as ever. PowerShell has
    /// no redirection of its output to standard error, so it writes there through .NET.
    /// </para>
    /// <para>
    /// The other shells write both marks through <c>sh</c>, whatever the account's own shell is. tcsh and csh have no
    /// redirection of standard output alone to standard error: measured with tcsh 6.24, <c>1&gt;&amp;2</c> wrote the mark
    /// into a file named <c>2</c> in the home directory and left standard error unmarked. And a shell tracing its
    /// commands - <c>set -x</c> in a startup file - writes each one to standard error before it runs it: the trace of an
    /// <c>echo</c> of the mark ends with the mark, as a mark line does, where the trace of <c>sh</c> ends with its quote.
    /// </para>
    /// </remarks>
    public static string Marked(string commandLine, string mark, RemoteShell shell)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);

        if (!IsLiteral(mark, RemoteShell.Standard))
        {
            throw new ArgumentException($"'{mark}' is no word every shell reads literally.", nameof(mark));
        }

        return shell switch
        {
            RemoteShell.Cmd => $"echo {mark}& echo {mark} 1>&2& {commandLine}",
            RemoteShell.PowerShell => $"echo {mark}; [Console]::Error.WriteLine('{mark}'); {commandLine}",
            _ => $"sh -c 'echo {mark}; echo {mark} 1>&2'; {commandLine}",
        };
    }
}
