using DssHarness.Core.Platform;

namespace DssHarness.Core.Output;

/// <summary>
/// The console's renderer of events: prints what an event's text forms say, and nothing else - keeping the stream
/// choice, the home shorthand, the quoted-line exemption, the document redirection and the write lock the lines have
/// always had.
/// </summary>
/// <param name="standardOutput">
/// Where results go, and progress and a program's own output while no document is being written.
/// </param>
/// <param name="standardError">
/// Where failures, warnings and a program's own error output go, and progress while a document is being written.
/// </param>
/// <param name="verbose">Whether detail is shown.</param>
/// <param name="home">
/// How the machine's home directory is written in every line of the harness's own: as <c>~</c> on a host
/// answering another machine (see <see cref="HomeShorthand"/>), and as it is where this is left out.
/// </param>
public sealed class TextRunEvents(TextWriter standardOutput, TextWriter standardError, bool verbose, HomeShorthand? home = null)
    : IRunEvents
{
    private readonly TextWriter _out = standardOutput;
    private readonly TextWriter _error = standardError;
    private readonly HomeShorthand _home = home ?? HomeShorthand.None;
    private readonly Lock _gate = new();
    private bool _dataOnly;

    /// <summary>Whether detail is shown.</summary>
    public bool Verbose { get; } = verbose;

    /// <summary>
    /// <paramref name="text"/> as this output's reader is told it: with this machine's home directory written as the
    /// lines of the harness's own write it, and unchanged where no home was given.
    /// </summary>
    /// <param name="text">A path, or a line of the harness's own.</param>
    public string Shown(string text) => _home.Shown(text);

    /// <summary>Whether a document is being written to standard output right now, as <see cref="DataOnly"/> arranges it.</summary>
    public bool IsDataOnly
    {
        get
        {
            lock (_gate)
            {
                return _dataOnly;
            }
        }
    }

    /// <summary>
    /// Sends everything except a command's answer to standard error until the returned scope is disposed, so a command
    /// answering with a document leaves standard output holding only that document.
    /// </summary>
    /// <remarks>
    /// Disposing the scope puts back what was in force when it opened, so a scope opened inside another leaves the
    /// outer one standing.
    /// </remarks>
    public IDisposable DataOnly()
    {
        lock (_gate)
        {
            var before = _dataOnly;
            _dataOnly = true;
            return new DataOnlyScope(this, before);
        }
    }

    /// <inheritdoc/>
    public void Emit(RunEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        foreach (var form in @event.TextForms())
        {
            if (form.VerboseOnly && !Verbose)
            {
                continue;
            }

            Write(form);
        }
    }

    /// <summary>
    /// Writes one text form as the member of <see cref="IHarnessOutput"/> it belongs to writes it: to the stream that
    /// member uses, in the shape it gives, under the one lock that keeps lines whole.
    /// </summary>
    /// <param name="form">The line to write.</param>
    private void Write(TextForm form)
    {
        // A line of the harness's own is prefixed and told as its reader is told it - except a row quoting what a
        // program printed, which is written as the program printed it. A child's line and a command's answer are
        // written as they came, with nothing added.
        switch (form.Kind)
        {
            case LineKind.Ok:
                WriteLine(Progress, form.Command is null ? null : $"{form.Command}: OK - {Own(form.Text)}");
                break;
            case LineKind.Fail:
                WriteLine(_error, form.Command is null ? null : FailureLine.For(form.Command, Own(form.Text)));
                break;
            case LineKind.Warn:
                WriteLine(_error, form.Command is null ? null : $"{form.Command}: WARN - {Own(form.Text)}");
                break;
            case LineKind.Info:
            case LineKind.Detail:
                WriteLine(Progress, form.Command is null ? form.Text : $"{form.Command}: {Own(form.Text)}");
                break;
            case LineKind.Data:
                WriteLine(_out, form.Text);
                break;
            case LineKind.Raw:
                WriteLine(Progress, form.Text);
                break;
            case LineKind.RawError:
                WriteLine(_error, form.Text);
                break;
        }
    }

    /// <summary>
    /// Where anything that is not the command's answer goes. Standard error while a document is being written, so the
    /// document is the only thing on standard output.
    /// </summary>
    private TextWriter Progress
    {
        get
        {
            lock (_gate)
            {
                return _dataOnly ? _error : _out;
            }
        }
    }

    /// <summary>
    /// A line of the harness's own, as its reader is told it - except a row quoting what a program printed, which is
    /// written as the program printed it.
    /// </summary>
    private string Own(string message) => QuotedLine.Is(message) ? message : _home.Shown(message);

    private void WriteLine(TextWriter writer, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_gate)
        {
            writer.WriteLine(line);
        }
    }

    /// <summary>
    /// Puts back what it found, so a scope opened inside another - a leg run inside a command that already answers
    /// with data - leaves the outer one standing when it closes.
    /// </summary>
    private sealed class DataOnlyScope(TextRunEvents sink, bool before) : IDisposable
    {
        private readonly TextRunEvents _sink = sink;

        public void Dispose()
        {
            lock (_sink._gate)
            {
                _sink._dataOnly = before;
            }
        }
    }
}
