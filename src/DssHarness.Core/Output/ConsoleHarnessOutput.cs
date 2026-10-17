using DssHarness.Core.Platform;

namespace DssHarness.Core.Output;

/// <inheritdoc cref="IHarnessOutput"/>
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
/// <param name="operation">The operation every event is stamped with, where it has an id.</param>
/// <param name="parent">The operation this one belongs to, which every event names, where there is one.</param>
/// <remarks>
/// The console as a front end: every line an event's text form, rendered by <see cref="TextRunEvents"/> under the
/// publisher's lock. What this class has always written it still writes, byte for byte - the tests that hold those
/// lines are the regression - and what is new, <see cref="IHarnessOutput.Publish"/>, reaches the same renderer.
/// </remarks>
public sealed class ConsoleHarnessOutput(
    TextWriter standardOutput,
    TextWriter standardError,
    bool verbose,
    HomeShorthand? home = null,
    OperationId? operation = null,
    OperationId? parent = null)
    : IHarnessOutput
{
    private readonly EventHarnessOutput _output = new(new TextRunEvents(standardOutput, standardError, verbose, home), operation, parent);

    /// <summary>Writes to the process console.</summary>
    /// <param name="verbose">Whether detail is shown.</param>
    /// <param name="home">How the machine's home directory is written in every line of the harness's own.</param>
    /// <param name="operation">The operation every event is stamped with, where it has an id.</param>
    /// <param name="parent">The operation this one belongs to, which every event names, where there is one.</param>
    public ConsoleHarnessOutput(bool verbose, HomeShorthand? home = null, OperationId? operation = null, OperationId? parent = null)
        : this(Console.Out, Console.Error, verbose, home, operation, parent)
    {
    }

    /// <inheritdoc cref="EventHarnessOutput.Operation"/>
    public OperationId? Operation => _output.Operation;

    /// <inheritdoc cref="EventHarnessOutput.Parent"/>
    public OperationId? Parent => _output.Parent;

    /// <inheritdoc/>
    public bool IsVerbose => _output.IsVerbose;

    /// <inheritdoc/>
    public bool IsDataOnly => _output.IsDataOnly;

    /// <inheritdoc/>
    public void Publish(RunEvent @event) => _output.Publish(@event);

    /// <inheritdoc/>
    public void Ok(string command, string message) => _output.Ok(command, message);

    /// <inheritdoc/>
    public void Fail(string command, string message) => _output.Fail(command, message);

    /// <inheritdoc/>
    public void Warn(string command, string message) => _output.Warn(command, message);

    /// <inheritdoc/>
    public void Info(string command, string message) => _output.Info(command, message);

    /// <inheritdoc/>
    public void Detail(string command, string message) => _output.Detail(command, message);

    /// <inheritdoc/>
    public void Data(string text) => _output.Data(text);

    /// <inheritdoc/>
    public void Raw(string line) => _output.Raw(line);

    /// <inheritdoc/>
    public void RawError(string line) => _output.RawError(line);

    /// <inheritdoc/>
    public string Shown(string text) => _output.Shown(text);

    /// <inheritdoc/>
    public IDisposable DataOnly() => _output.DataOnly();
}
