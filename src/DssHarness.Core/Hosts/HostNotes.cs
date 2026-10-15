using System.Text;
using DssHarness.Core.Output;
using DssHarness.Core.Repository;

namespace DssHarness.Core.Hosts;

/// <summary>
/// How a command a host serves for another machine tells that machine something while it runs: a note, written on its
/// standard error as lines only that machine reads (<see cref="HostAgentProtocol.NoteLine"/>) and never relays. Whole
/// notes under one lock, so the pieces of one never interleave with another's.
/// </summary>
/// <remarks>
/// The first thing a host writes that its dispatcher reads mid-run: a transport that split or merged the lines would lose
/// a note, and the record of what that run judged is then missing an arm, which is pending - never a wrong verdict. Says
/// nothing where no machine asked, which is a command typed on the host by hand.
/// </remarks>
/// <param name="origin">Who the command runs for: what the machine that asked said of it beside the command line.</param>
/// <param name="output">Where the lines are written, raw.</param>
public sealed class HostNotes(CommandOrigin origin, IHarnessOutput output)
{
    private readonly CommandOrigin _origin = origin;
    private readonly IHarnessOutput _output = output;
    private readonly Lock _gate = new();

    /// <summary>
    /// Writes a note of <paramref name="kind"/> carrying <paramref name="text"/> to the machine that asked for this command,
    /// as it runs; nothing where no machine asked. The text crosses as the base64 of pieces of at most
    /// <see cref="HostAgentProtocol.NotePiece"/> bytes - each small enough that its whole line fits what the reader hands
    /// lines on in, so no piece is cut in two on the way - each piece one line.
    /// </summary>
    /// <param name="kind">The note's kind, which says what its text holds.</param>
    /// <param name="text">The note's text.</param>
    public void Write(string kind, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(text);

        var nonce = _origin.Dispatched?.Nonce;

        if (nonce is null)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        var piece = HostAgentProtocol.NotePiece(kind);

        lock (_gate)
        {
            for (var at = 0; at < bytes.Length; at += piece)
            {
                var carried = new ReadOnlyMemory<byte>(bytes, at, Math.Min(piece, bytes.Length - at));

                _output.RawError(HostAgentProtocol.NoteLine(nonce, kind, at + carried.Length >= bytes.Length, Convert.ToBase64String(carried.Span)));
            }

            // A note of nothing is one line, so the reader is never left waiting for a last piece that says nothing.
            if (bytes.Length == 0)
            {
                _output.RawError(HostAgentProtocol.NoteLine(nonce, kind, last: true, string.Empty));
            }
        }
    }
}
