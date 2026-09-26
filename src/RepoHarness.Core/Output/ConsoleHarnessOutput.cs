using RepoHarness.Core.Platform;

namespace RepoHarness.Core.Output;

/// <inheritdoc cref="IHarnessOutput"/>
/// <param name="standardOutput">Where results, and progress while no document is being written, go.</param>
/// <param name="standardError">Where failures, warnings, and progress while a document is being written, go.</param>
/// <param name="verbose">Whether detail is shown.</param>
/// <param name="home">
/// How the machine's home directory is written in every line of the harness's own: as <c>~</c> on a host
/// answering another machine (see <see cref="HomeShorthand"/>), and as it is where this is left out.
/// </param>
public sealed class ConsoleHarnessOutput(TextWriter standardOutput, TextWriter standardError, bool verbose, HomeShorthand? home = null)
    : IHarnessOutput
{
    private readonly TextWriter _out = standardOutput;
    private readonly TextWriter _error = standardError;
    private readonly HomeShorthand _home = home ?? HomeShorthand.None;
    private readonly Lock _gate = new();
    private bool _dataOnly;

    /// <summary>Writes to the process console.</summary>
    /// <param name="verbose">Whether detail is shown.</param>
    /// <param name="home">How the machine's home directory is written in every line of the harness's own.</param>
    public ConsoleHarnessOutput(bool verbose, HomeShorthand? home = null)
        : this(Console.Out, Console.Error, verbose, home)
    {
    }

    public bool IsVerbose { get; } = verbose;

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

    public void Ok(string command, string message) => Write(Progress, $"{command}: OK - {Own(message)}");

    public void Fail(string command, string message) => Write(_error, FailureLine.For(command, Own(message)));

    public void Warn(string command, string message) => Write(_error, $"{command}: WARN - {Own(message)}");

    public void Info(string command, string message) => Write(Progress, $"{command}: {Own(message)}");

    public void Detail(string command, string message)
    {
        if (IsVerbose)
        {
            Write(Progress, $"{command}: {Own(message)}");
        }
    }

    public void Data(string text) => Write(_out, text);

    public void Raw(string line) => Write(Progress, line);

    public void RawError(string line) => Write(_error, line);

    public string Shown(string text) => _home.Shown(text);

    public IDisposable DataOnly()
    {
        lock (_gate)
        {
            var before = _dataOnly;
            _dataOnly = true;
            return new DataOnlyScope(this, before);
        }
    }

    /// <summary>
    /// Where anything that is not the command's answer goes. Standard error while a document is
    /// being written, so the document is the only thing on standard output.
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
    /// A line of the harness's own, as its reader is told it - except a row quoting what a program printed,
    /// which is written as the program printed it.
    /// </summary>
    private string Own(string message) => QuotedLine.Is(message) ? message : _home.Shown(message);

    private void Write(TextWriter writer, string line)
    {
        lock (_gate)
        {
            writer.WriteLine(line);
        }
    }

    /// <summary>
    /// Puts back what it found, so a scope opened inside another - a leg run inside a command that
    /// already answers with data - leaves the outer one standing when it closes.
    /// </summary>
    private sealed class DataOnlyScope(ConsoleHarnessOutput output, bool before) : IDisposable
    {
        private readonly ConsoleHarnessOutput _output = output;

        public void Dispose()
        {
            lock (_output._gate)
            {
                _output._dataOnly = before;
            }
        }
    }
}
