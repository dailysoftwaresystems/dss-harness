namespace DssHarness.Core.Hosts;

/// <summary>
/// Reads the two streams of one request to a host's agent: which lines are the agent's, the exit code its
/// completion line carries, and each note the command it asked for writes while it runs, never relayed: handed on whole
/// where the machine that asked reads notes, and dropped where it reads none.
/// </summary>
/// <remarks>
/// Whatever reaches the streams ahead of the agent arrives first: the runtime's and the launcher's words on
/// the way to it, and a login shell's startup output - one consumer's printed the account's home layout on
/// every session, which a relayed line then published - that over ssh is dropped before this reads anything
/// (see <see cref="RemoteCommandLine.Marked"/>). So nothing a stream carried before the agent's started line
/// is the request's, except on standard error the
/// agent's own lines, because a request refused before it could be read carries no nonce to mark, and that
/// refusal is the whole of what the reader has to go on. The completion line is read before that gate and
/// never behind it: it is the one thing that must survive a host whose profile writes to that stream, because
/// a line that never arrives is reported as a request that may not have run at all.
/// </remarks>
/// <param name="nonce">The request's nonce.</param>
/// <param name="onNote">Told each whole note the command the request ran wrote, as it arrives; <see langword="null"/> where the machine that asked reads none, and each is dropped.</param>
public sealed class HostAgentLines(string nonce, Action<string, string>? onNote = null)
{
    private readonly string _nonce = nonce;
    private readonly Action<string, string>? _onNote = onNote;

    // Whether the agent has begun answering, on each stream.
    private bool _serving;
    private bool _reporting;

    // The note being read: its kind, and the pieces of its text, whole notes never interleaving on the host that writes them.
    private string? _note;
    private readonly System.Collections.Generic.List<byte> _pieces = [];

    /// <summary>
    /// The exit code the agent's completion line carried, or <see langword="null"/> while none has arrived: the
    /// request may not have run, or run only in part.
    /// </summary>
    public int? Finished { get; private set; }

    /// <summary>Whether <paramref name="line"/>, from standard output, is the agent's.</summary>
    /// <param name="line">A line the host wrote to standard output.</param>
    public bool Output(string line)
    {
        if (HostAgentProtocol.IsStartedLine(line, _nonce))
        {
            _reporting = true;
            return false;
        }

        return _reporting;
    }

    /// <summary>
    /// Whether <paramref name="line"/>, from standard error, is the agent's to relay; its started and completion
    /// lines are read here, and are never relayed, and nor is any line of a note the command the request ran wrote,
    /// which is handed on whole instead, or dropped where the machine that asked reads none.
    /// </summary>
    /// <param name="line">A line the host wrote to standard error.</param>
    public bool Error(string line)
    {
        if (HostAgentProtocol.TryReadCompletionLine(line, _nonce, out var code))
        {
            Finished = code;
            return false;
        }

        if (HostAgentProtocol.IsStartedLine(line, _nonce))
        {
            _serving = true;
            return false;
        }

        return !ReadNote(line) && (_serving || HostAgentProtocol.IsAgentsOwnLine(line));
    }

    /// <summary>
    /// Whether <paramref name="line"/> is a piece of a note of this request: the piece is gathered, and a whole note is
    /// handed on - or, where the machine that asked reads none, dropped - and never relayed. False where it is none, or
    /// another request's.
    /// </summary>
    /// <param name="line">A line the host wrote to standard error.</param>
    private bool ReadNote(string line)
    {
        if (!HostAgentProtocol.TryReadNoteLine(line, out var nonce, out var kind, out var last, out var base64)
            || !string.Equals(nonce, _nonce, StringComparison.Ordinal))
        {
            return false;
        }

        // A machine that reads no notes - a sweep typed by hand in a synced copy, which records nothing - still asked a
        // command that writes them: each is this request's, so it is dropped here, never relayed as a line of its own.
        if (_onNote is null)
        {
            return true;
        }

        // A note the reader was not reading, told whole by one line, replaces whatever half-read note went before it.
        if (_note is null)
        {
            _note = kind;
            _pieces.Clear();
        }

        try
        {
            _pieces.AddRange(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            // Not this request's note after all, or the transport damaged it: passed over, and the note it belonged to is
            // dropped - the record the notes feed is then missing an arm, which is pending, never a wrong verdict.
            _note = null;
            _pieces.Clear();
            return true;
        }

        if (last)
        {
            var text = System.Text.Encoding.UTF8.GetString([.. _pieces]);

            _note = null;
            _pieces.Clear();
            _onNote(kind, text);
        }

        return true;
    }
}
