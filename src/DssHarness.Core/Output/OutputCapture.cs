using System.Text;
using DssHarness.Core.Platform;

namespace DssHarness.Core.Output;

/// <summary>What an operation wrote, as <see cref="OutputCapture"/> kept it.</summary>
/// <param name="Json">Whether the operation answered with its <c>--json</c> document.</param>
/// <param name="Document">The document, as standard output held it, where it answered with one; null otherwise.</param>
/// <param name="Lines">
/// Every other line, in the order written, as the terminal was given it: with <c>--json</c>, what went to standard error
/// beside the document; without it, everything, on either stream.
/// </param>
public sealed record CapturedOutput(bool Json, string? Document, IReadOnlyList<string> Lines);

/// <summary>
/// The lines of one operation, kept as its console writes them: every event rendered by the console's own renderer
/// (<see cref="TextRunEvents"/>), with the operation's verbosity and home shorthand, into lines kept in the order they were
/// written - the command's answer apart from the rest, so a <c>--json</c> document is read back whole. Fed by
/// <see cref="CapturedHarnessOutput"/>, which writes nothing it was not given.
/// </summary>
/// <remarks>
/// Kept in memory, whole: what a command printed is what a terminal's scrollback holds of it, and every line of it is
/// kept, none dropped.
/// </remarks>
public sealed class OutputCapture : IRunEvents
{
    private readonly Lock _gate = new();
    private readonly List<(bool Answer, string Line)> _lines = [];

    /// <summary>Begins keeping an operation's lines.</summary>
    /// <param name="verbose">Whether detail is shown, as the operation's console shows it.</param>
    /// <param name="home">How the machine's home directory is written in every line of the harness's own, as the console writes it.</param>
    public OutputCapture(bool verbose, HomeShorthand? home = null)
    {
        Renderer = new TextRunEvents(new Kept(this, answer: true), new Kept(this, answer: false), verbose, home);

        // For as long as the capture lives: the command's answer is the one thing written to standard output then, so
        // everything else is kept apart from it, whatever the operation's own console does.
        _ = Renderer.DataOnly();
    }

    /// <summary>The renderer the lines are kept through: the console's own, writing into this capture.</summary>
    internal TextRunEvents Renderer { get; }

    /// <inheritdoc/>
    public void Emit(RunEvent @event) => Renderer.Emit(@event);

    /// <summary>What has been kept so far.</summary>
    /// <param name="json">Whether the operation answers with its <c>--json</c> document, which is then kept apart from its lines.</param>
    public CapturedOutput Captured(bool json)
    {
        lock (_gate)
        {
            return json
                ? new CapturedOutput(true, string.Join("\n", _lines.Where(line => line.Answer).Select(line => line.Line)), [.. _lines.Where(line => !line.Answer).Select(line => line.Line)])
                : new CapturedOutput(false, null, [.. _lines.Select(line => line.Line)]);
        }
    }

    private void Keep(bool answer, string line)
    {
        lock (_gate)
        {
            _lines.Add((answer, line));
        }
    }

    /// <summary>One of the two streams the renderer writes to, each line it is given kept in the order written.</summary>
    private sealed class Kept(OutputCapture capture, bool answer) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value) => capture.Keep(answer, value ?? string.Empty);
    }
}

/// <summary>
/// An operation's output, with every line it writes kept as well (<see cref="OutputCapture"/>): each member passed to the
/// output it wraps, then to an output over the capture's renderer - the one mapping of a member to the event the console
/// renders (<see cref="EventHarnessOutput"/>) - both under one lock, so the capture holds the lines in the order the
/// terminal was given them, and nothing the operation writes is changed by being kept.
/// </summary>
public sealed class CapturedHarnessOutput : IHarnessOutput
{
    private readonly Lock _gate = new();
    private readonly IHarnessOutput _inner;
    private readonly EventHarnessOutput _kept;

    /// <summary>Wraps <paramref name="inner"/>, keeping what it is given in <paramref name="capture"/>.</summary>
    /// <param name="inner">The output the operation writes through.</param>
    /// <param name="capture">Where its lines are kept.</param>
    public CapturedHarnessOutput(IHarnessOutput inner, OutputCapture capture)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(capture);

        _inner = inner;
        _kept = new EventHarnessOutput(capture.Renderer);
    }

    /// <inheritdoc/>
    public bool IsVerbose => _inner.IsVerbose;

    /// <inheritdoc/>
    public bool IsDataOnly => _inner.IsDataOnly;

    /// <inheritdoc/>
    public void Publish(RunEvent @event) => Both(output => output.Publish(@event));

    /// <inheritdoc/>
    public void Ok(string command, string message) => Both(output => output.Ok(command, message));

    /// <inheritdoc/>
    public void Fail(string command, string message) => Both(output => output.Fail(command, message));

    /// <inheritdoc/>
    public void Warn(string command, string message) => Both(output => output.Warn(command, message));

    /// <inheritdoc/>
    public void Info(string command, string message) => Both(output => output.Info(command, message));

    /// <inheritdoc/>
    public void Detail(string command, string message) => Both(output => output.Detail(command, message));

    /// <inheritdoc/>
    public void Data(string text) => Both(output => output.Data(text));

    /// <inheritdoc/>
    public void Raw(string line) => Both(output => output.Raw(line));

    /// <inheritdoc/>
    public void RawError(string line) => Both(output => output.RawError(line));

    /// <inheritdoc/>
    public string Shown(string text) => _inner.Shown(text);

    /// <inheritdoc/>
    public IDisposable DataOnly() => _inner.DataOnly();

    /// <summary>The member called on the output it wraps, then on the capture's, under the one lock.</summary>
    private void Both(Action<IHarnessOutput> write)
    {
        lock (_gate)
        {
            write(_inner);
            write(_kept);
        }
    }
}
