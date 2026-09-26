namespace RepoHarness.Core.Output;

/// <summary>
/// Console output. Every line the harness itself emits identifies the command that
/// produced it, so output interleaved from nested or remote work stays attributable.
/// Passthrough output from child processes is deliberately unprefixed.
/// </summary>
public interface IHarnessOutput
{
    /// <summary>Whether verbose detail is being shown.</summary>
    bool IsVerbose { get; }

    /// <summary>
    /// Whether a document is being written to standard output right now, as <see cref="DataOnly"/>
    /// arranges it. Asked by anything that would otherwise interrupt one: a prompt written there
    /// becomes part of what the reader parses, and the same prompt written anywhere else is a wait
    /// with no visible reason for it.
    /// </summary>
    bool IsDataOnly { get; }

    /// <summary>Reports success: <c>&lt;command&gt;: OK - &lt;message&gt;</c>.</summary>
    void Ok(string command, string message);

    /// <summary>Reports failure to stderr: <c>&lt;command&gt;: FAIL - &lt;message&gt;</c>.</summary>
    void Fail(string command, string message);

    /// <summary>Reports a non fatal problem: <c>&lt;command&gt;: WARN - &lt;message&gt;</c>.</summary>
    void Warn(string command, string message);

    /// <summary>Reports progress that is worth seeing by default.</summary>
    void Info(string command, string message);

    /// <summary>Reports detail shown only when verbose.</summary>
    void Detail(string command, string message);

    /// <summary>
    /// Writes a command's result to stdout, unprefixed: a listing, a detail block, a JSON document.
    /// Unprefixed so that another program can read what a command answered without stripping anything.
    /// </summary>
    void Data(string text);

    /// <summary>Writes a line of passthrough output from a child process, unprefixed.</summary>
    void Raw(string line);

    /// <summary>Writes a line of passthrough stderr from a child process, unprefixed.</summary>
    void RawError(string line);

    /// <summary>
    /// <paramref name="text"/> as this output's reader is told it. On a host answering another machine,
    /// with the host's home directory written as <c>~</c> - as every line of the harness's own written
    /// here already is - and unchanged anywhere else.
    /// </summary>
    /// <param name="text">A path, or a line of the harness's own, bound for what a command answers with.</param>
    /// <remarks>
    /// For what reaches the reader through <see cref="Data"/>, whose documents this output cannot look into:
    /// the command that writes one knows which of its fields are its own words and which a program's.
    /// Passthrough output and documents are written as they are given.
    /// </remarks>
    string Shown(string text);

    /// <summary>
    /// Sends everything except <see cref="Data"/> to standard error until the returned scope is
    /// disposed, so a command answering with a document leaves standard output holding only that
    /// document.
    /// </summary>
    /// <remarks>
    /// Disposing the scope puts back what was in force when it opened, so a scope opened inside
    /// another leaves the outer one standing.
    /// <para>
    /// A command asked for JSON has one reader, and that reader parses standard output whole.
    /// Progress written there would be read as part of the document, and the run's own progress is
    /// the most likely thing to appear before it. Measured: a leg dispatched to a host returned its
    /// ledger behind two lines of progress, and the machine that asked reported the host as
    /// unreachable — a green leg turned into a connection failure by a line of prose.
    /// </para>
    /// </remarks>
    IDisposable DataOnly();
}
